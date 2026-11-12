using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AILegacy.Config;
using AILegacy.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AILegacy.Llm
{
    /// <summary>
    /// Vectores de significado («embeddings») para buscar recuerdos y acciones por lo que quieren decir y no solo por las
    /// palabras. Se piden al punto /embeddings (formato OpenAI) de un proveedor que ya esté configurado: primero los
    /// gratuitos. Si ninguno los da, todo sigue funcionando con la búsqueda por palabras.
    /// Los vectores se recortan a 256 dimensiones y se guardan en un byte por dimensión: caben miles en poco sitio.
    /// </summary>
    public static class Embeddings
    {
        public const int Dim = 256;
        private const int BatchSize = 48;

        private static readonly SemaphoreSlim Gate = new SemaphoreSlim(1, 1);
        // Proveedores que han fallado dando vectores en esta sesión (nombre → fallos)
        private static readonly Dictionary<string, int> Fails = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        private static readonly Dictionary<string, bool> NoDimensions = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private static DateTime _retryAt = DateTime.MinValue;
        private static volatile string _working;       // "proveedor/modelo" que funcionó la última vez

        /// <summary>Modelo de vectores de un proveedor: el de config.json, el de MCM, o el habitual de ese servicio.</summary>
        private static string ModelFor(ProviderConfig p, string fromSettings)
        {
            if (!string.IsNullOrWhiteSpace(p.EmbeddingModel)) return p.EmbeddingModel.Trim() == "-" ? null : p.EmbeddingModel.Trim();
            string u = (p.BaseUrl ?? "").ToLowerInvariant();
            if (u.Contains("generativelanguage.googleapis.com")) return "gemini-embedding-001";
            if (u.Contains("api.openai.com")) return "text-embedding-3-small";
            if (u.Contains("localhost:11434") || u.Contains("127.0.0.1:11434")) return "nomic-embed-text";
            return string.IsNullOrWhiteSpace(fromSettings) ? null : fromSettings.Trim();
        }

        private class Source { public ProviderConfig P; public string Model; }

        /// <summary>Qué proveedores pueden dar vectores, en orden (hay que llamarlo en el hilo principal: lee MCM).</summary>
        private static List<Source> Capture()
        {
            var list = new List<Source>();
            try
            {
                string custom = Settings.S.Get.EmbeddingModel;
                foreach (var p in LegacyConfig.Current.Providers.Where(x => x.IsUsable).OrderBy(x => x.IsPaid ? 1 : 0))
                {
                    string m = ModelFor(p, custom);
                    if (m != null) list.Add(new Source { P = p, Model = m });
                }
            }
            catch { }
            return list;
        }

        /// <summary>¿Hay de dónde sacar vectores ahora mismo?</summary>
        public static bool Available
        {
            get
            {
                try
                {
                    if (!Settings.S.Get.EnableRag || DateTime.UtcNow < _retryAt) return false;
                    return Capture().Count > 0;
                }
                catch { return false; }
            }
        }

        public static string Working => _working;

        /// <summary>Vectores de esos textos (uno por texto, ya recortados y en bytes), o null si no se han podido sacar.</summary>
        public static Task<sbyte[][]> EmbedAsync(IList<string> texts, string task = "embed")
        {
            if (texts == null || texts.Count == 0) return Task.FromResult<sbyte[][]>(null);
            var sources = Capture();
            if (sources.Count == 0 || DateTime.UtcNow < _retryAt) return Task.FromResult<sbyte[][]>(null);
            var copy = texts.Select(t => Clip(t ?? "", 2000)).ToList();
            return Task.Run(() => Run(copy, sources, task));
        }

        private static async Task<sbyte[][]> Run(List<string> texts, List<Source> sources, string task)
        {
            await Gate.WaitAsync().ConfigureAwait(false);
            try
            {
                foreach (var src in sources)
                {
                    int fails; lock (Fails) Fails.TryGetValue(src.P.Name, out fails);
                    if (fails >= 3) continue;
                    var all = new List<sbyte[]>();
                    bool ok = true;
                    for (int i = 0; i < texts.Count && ok; i += BatchSize)
                    {
                        var part = await Call(src, texts.Skip(i).Take(BatchSize).ToList(), task).ConfigureAwait(false);
                        if (part == null) ok = false; else all.AddRange(part);
                    }
                    if (ok && all.Count == texts.Count)
                    {
                        lock (Fails) Fails[src.P.Name] = 0;
                        string id = src.P.Name + "/" + src.Model;
                        if (_working != id) { _working = id; Log.Info($"Vectores de significado: se usa {id}"); }
                        return all.ToArray();
                    }
                    lock (Fails) Fails[src.P.Name] = fails + 1;
                }
                // Nadie ha podido: se deja descansar diez minutos antes de volver a intentarlo
                _retryAt = DateTime.UtcNow.AddMinutes(10);
                lock (Fails) Fails.Clear();
                Log.Warn("Vectores de significado: ningún proveedor los ha dado; se sigue con la búsqueda por palabras (se reintentará en 10 minutos)");
                return null;
            }
            catch (Exception ex) { Log.Error("Vectores de significado", ex); return null; }
            finally { Gate.Release(); }
        }

        private static async Task<List<sbyte[]>> Call(Source src, List<string> batch, string task)
        {
            var p = src.P;
            bool noDims; lock (NoDimensions) NoDimensions.TryGetValue(p.Name, out noDims);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var body = new JObject { ["model"] = src.Model, ["input"] = new JArray(batch) };
                if (!noDims) body["dimensions"] = Dim;
                var started = DateTime.UtcNow;
                try
                {
                    using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20)))
                    using (var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/embeddings"))
                    {
                        req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
                        req.Content = new StringContent(body.ToString(Formatting.None), Encoding.UTF8, "application/json");
                        using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
                        {
                            string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            double secs = (DateTime.UtcNow - started).TotalSeconds;
                            int status = (int)resp.StatusCode;
                            if (status == 200)
                            {
                                var j = JObject.Parse(text);
                                var data = j["data"] as JArray;
                                if (data == null || data.Count != batch.Count) { Log.Warn($"Vectores ({p.Name}): respuesta con {data?.Count ?? 0} vectores para {batch.Count} textos"); return null; }
                                var list = new List<sbyte[]>();
                                foreach (var d in data.OrderBy(x => (int?)x["index"] ?? 0))
                                {
                                    var arr = d["embedding"] as JArray;
                                    if (arr == null || arr.Count < 16) return null;
                                    list.Add(Pack(arr.Select(x => (float)x).ToArray()));
                                }
                                int tin = (int?)j["usage"]?["prompt_tokens"] ?? (int?)j["usage"]?["total_tokens"] ?? 0;
                                UsageMeter.Record(p.Name, task, true, tin, 0, 0, secs, p.PriceIn, 0);
                                return list;
                            }
                            UsageMeter.Record(p.Name, task, false, 0, 0, 0, secs, 0, 0);
                            // Parámetro no aceptado: otra vez sin pedir dimensiones (se recortan aquí)
                            if ((status == 400 || status == 422) && !noDims) { noDims = true; lock (NoDimensions) NoDimensions[p.Name] = true; continue; }
                            Log.Warn($"Vectores ({p.Name}/{src.Model}): HTTP {status} {Clip(text, 200)}");
                            return null;
                        }
                    }
                }
                catch (Exception ex) { Log.Warn($"Vectores ({p.Name}): {ex.GetBaseException().Message}"); return null; }
            }
            return null;
        }

        /// <summary>Recorta a 256 dimensiones, normaliza y guarda cada una en un byte con signo.</summary>
        public static sbyte[] Pack(float[] v)
        {
            int n = Math.Min(Dim, v.Length);
            double norm = 0;
            for (int i = 0; i < n; i++) norm += v[i] * (double)v[i];
            norm = Math.Sqrt(norm);
            var r = new sbyte[n];
            if (norm < 1e-9) return r;
            // Con vectores normalizados, casi todo cae en ±0,25: se escala para aprovechar el byte
            for (int i = 0; i < n; i++)
            {
                double x = v[i] / norm * 4.0 * 127.0;
                r[i] = (sbyte)Math.Max(-127, Math.Min(127, Math.Round(x)));
            }
            return r;
        }

        /// <summary>Parecido entre dos vectores, de -1 a 1.</summary>
        public static double Cosine(sbyte[] a, sbyte[] b)
        {
            if (a == null || b == null) return 0;
            int n = Math.Min(a.Length, b.Length);
            if (n == 0) return 0;
            long dot = 0, na = 0, nb = 0;
            for (int i = 0; i < n; i++) { dot += a[i] * b[i]; na += a[i] * a[i]; nb += b[i] * b[i]; }
            return na == 0 || nb == 0 ? 0 : dot / (Math.Sqrt(na) * Math.Sqrt(nb));
        }

        public static string ToText(sbyte[] v) => v == null ? null : Convert.ToBase64String((byte[])(Array)v);
        public static sbyte[] FromText(string s)
        {
            try { return string.IsNullOrEmpty(s) ? null : (sbyte[])(Array)Convert.FromBase64String(s); }
            catch { return null; }
        }

        private static string Clip(string t, int max) => t.Length <= max ? t : t.Substring(0, max);
    }
}
