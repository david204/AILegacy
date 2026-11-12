using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AILegacy.Config;
using AILegacy.Core;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AILegacy.Llm
{
    public class ChatMessage
    {
        public string Role;
        public string Content;
        public ChatMessage(string role, string content) { Role = role; Content = content; }
    }

    public class LlmResult
    {
        public bool Ok;
        public string Text;
        public string Provider;
        public string Error;
        public int PromptTokens;
        public int CompletionTokens;
        public double Seconds;
    }

    /// <summary>
    /// Cliente para cualquier endpoint compatible con OpenAI (/chat/completions).
    /// Recorre los proveedores en orden y salta al siguiente si uno falla.
    /// </summary>
    public static class LlmClient
    {
        private static readonly HttpClient Http;

        static LlmClient()
        {
            try
            {
                // .NET Framework no activa TLS 1.2/1.3 por defecto
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { /* Tls13 no disponible */ }
            }
            catch (Exception ex) { Log.Warn("No se pudo ajustar TLS: " + ex.Message); }

            Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("AILegacy/0.1");
        }

        /// <summary>Opciones leídas en el hilo principal (MCM) antes de saltar al hilo de fondo.</summary>
        private class CallOptions
        {
            public double Temperature = 0.8;
            public int MaxTokens = 900;
            public string Preferred = "";
        }

        private static volatile bool _debugBodies = true;

        private static CallOptions CaptureOptions(int? maxTokensOverride)
        {
            var o = new CallOptions();
            try
            {
                var s = Settings.S.Get;
                o.Temperature = s.TemperatureX100 / 100.0;
                o.MaxTokens = maxTokensOverride ?? s.MaxTokens;
                _debugBodies = s.DebugLogBodies;
            }
            catch { if (maxTokensOverride.HasValue) o.MaxTokens = maxTokensOverride.Value; }
            return o;
        }

        public static Task<LlmResult> CompleteAsync(List<ChatMessage> messages, bool wantJson, int? maxTokensOverride = null, string preferredProvider = null)
        {
            var opts = CaptureOptions(maxTokensOverride);
            opts.Preferred = (preferredProvider ?? "").Trim();
            return Task.Run(() => CompleteInternal(messages, wantJson, opts));
        }

        /// <summary>Botón de MCM: prueba cada proveedor por separado y lo cuenta en el registro del juego.</summary>
        public static void TestAllProviders()
        {
            var opts = CaptureOptions(60);
            var providers = LegacyConfig.Current.Providers.ToList();
            TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(
                L.F("AIL_c9d542c8", $"AI Legacy: probando {providers.Count(p => p.IsUsable)} proveedor(es)..."), TaleWorlds.Library.Colors.Gray));
            var msgs = new List<ChatMessage> { new ChatMessage("user", "Reply with the single word: OK") };
            foreach (var p in providers)
            {
                if (!p.IsUsable)
                {
                    Core.MainThread.Post(() => Show(L.F("AIL_0cd6f2e1", $"{p.Name}: desactivado o sin api_key"), TaleWorlds.Library.Colors.Gray));
                    continue;
                }
                var prov = p;
                Task.Run(() => TryProvider(prov, LegacyConfig.Current, msgs, false, opts)).ContinueWith(t =>
                {
                    var r = t.IsFaulted ? new LlmResult { Ok = false, Error = t.Exception?.GetBaseException().Message } : t.Result;
                    Core.MainThread.Post(() => Show(r.Ok ? L.F("AIL_263bf6a9", $"{prov.Name}/{prov.Model}: OK en {r.Seconds:0.0}s") : $"{prov.Name}/{prov.Model}: ERROR {r.Error}",
                        r.Ok ? TaleWorlds.Library.Colors.Green : TaleWorlds.Library.Colors.Red));
                });
            }
        }

        private static void Show(string msg, TaleWorlds.Library.Color c) =>
            TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage("AI Legacy — " + msg, c));

        private static async Task<LlmResult> CompleteInternal(List<ChatMessage> messages, bool wantJson, CallOptions opts)
        {
            var cfg = LegacyConfig.Current;
            var providers = cfg.Providers.Where(p => p.IsUsable).ToList();
            if (providers.Count == 0)
                return new LlmResult { Ok = false, Error = LegacyConfig.LastLoadError ?? L.T("AIL_b6af22fa", "No hay ningún proveedor con api_key en config.json") };
            // Proveedor preferido para esta tarea: primero; el resto de la cadena como respaldo
            if (!string.IsNullOrEmpty(opts.Preferred))
                providers = providers.OrderBy(p => string.Equals(p.Name, opts.Preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

            var errors = new List<string>();
            foreach (var p in providers)
            {
                var r = await TryProvider(p, cfg, messages, wantJson, opts).ConfigureAwait(false);
                if (r.Ok) return r;
                errors.Add($"{p.Name}: {r.Error}");
                Log.Warn($"Proveedor {p.Name} falló, paso al siguiente: {r.Error}");
            }
            return new LlmResult { Ok = false, Error = string.Join(" | ", errors) };
        }

        /// <summary>
        /// Prueba un proveedor. Si responde 400/422 por algún parámetro, reintenta
        /// quitando extras: completo -> sin json_mode ni extra -> mínimo.
        /// </summary>
        private static async Task<LlmResult> TryProvider(ProviderConfig p, LegacyConfig cfg, List<ChatMessage> messages, bool wantJson, CallOptions opts)
        {
            string lastError = null;
            // Nivel de parámetros que este proveedor aceptó la última vez: se empieza por ahí para no repetir la petición
            int start;
            lock (WorkingLevel) WorkingLevel.TryGetValue(p.Name + "|" + p.Model, out start);
            for (int level = start; level < 4; level++)
            {
                var body = BuildBody(p, opts, messages, wantJson, level);
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    var (status, text, err, secs) = await Send(p, body).ConfigureAwait(false);
                    if (status == 200)
                    {
                        var r = ParseResponse(text);
                        r.Provider = p.Name + "/" + p.Model;
                        r.Seconds = secs;
                        if (r.Ok)
                        {
                            Log.Info($"OK {r.Provider} en {secs:0.0}s (in={r.PromptTokens} out={r.CompletionTokens})" + (level > 0 ? $" [nivel {level}]" : ""));
                            if (level != start)
                            {
                                lock (WorkingLevel) WorkingLevel[p.Name + "|" + p.Model] = level;
                                Log.Warn($"{p.Name}: no acepta todos los parámetros; desde ahora se le envía el nivel {level} " +
                                         (level == 1 ? L.T("AIL_967c929e", "(sin los \"extra\" de config.json: revisa ese bloque)") : level == 2 ? L.T("AIL_d02be029", "(sin modo JSON)") : L.T("AIL_db9c0e6a", "(mínimo)")) + $". Motivo: {lastError}");
                            }
                            return r;
                        }
                        lastError = r.Error;
                        break; // respuesta vacía o rara: siguiente nivel
                    }

                    lastError = $"HTTP {status}: {Trim(err ?? text, 300)}";

                    if (status == 429 && attempt == 0)
                    {
                        // límite de peticiones: espera corta y un reintento; si no, siguiente proveedor
                        await Task.Delay(TimeSpan.FromSeconds(4)).ConfigureAwait(false);
                        continue;
                    }
                    if (status == 401 || status == 403 || status == 404 || status == 429 || status == 0 || status >= 500)
                        return new LlmResult { Ok = false, Error = lastError };
                    if ((status == 400 || status == 422) && (text ?? "").IndexOf("api key", StringComparison.OrdinalIgnoreCase) >= 0)
                        return new LlmResult { Ok = false, Error = lastError };
                    break; // 400/422: probar con menos parámetros
                }
            }
            return new LlmResult { Ok = false, Error = lastError ?? "error desconocido" };
        }

        private static string BuildBody(ProviderConfig p, CallOptions opts, List<ChatMessage> messages, bool wantJson, int level)
        {
            var body = new JObject
            {
                ["model"] = p.Model,
                ["messages"] = new JArray(messages.Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Content })),
            };
            // Escalera: 0 = todo · 1 = sin "extra" (pero CON modo JSON) · 2 = sin modo JSON · 3 = mínimo
            if (level < 3)
            {
                body[string.IsNullOrWhiteSpace(p.MaxTokensField) ? "max_tokens" : p.MaxTokensField] = opts.MaxTokens;
                if (p.SendTemperature) body["temperature"] = opts.Temperature;
            }
            if (level < 2 && wantJson && p.JsonMode) body["response_format"] = new JObject { ["type"] = "json_object" };
            if (level == 0 && p.Extra != null)
                foreach (var kv in p.Extra) body[kv.Key] = kv.Value?.DeepClone();
            return body.ToString(Formatting.None);
        }

        private static readonly Dictionary<string, int> WorkingLevel = new Dictionary<string, int>();

        private static async Task<(int status, string text, string error, double secs)> Send(ProviderConfig p, string body)
        {
            var url = p.BaseUrl.TrimEnd('/') + "/chat/completions";
            var started = DateTime.UtcNow;
            if (_debugBodies) Log.Dump("last_request.json", body);
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(Math.Max(5, p.TimeoutSeconds))))
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var secs = (DateTime.UtcNow - started).TotalSeconds;
                        if (_debugBodies) Log.Dump("last_response.json", text);
                        return ((int)resp.StatusCode, text, null, secs);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return (0, null, L.F("AIL_b74fa9dd", $"sin respuesta en {p.TimeoutSeconds}s"), (DateTime.UtcNow - started).TotalSeconds);
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                return (0, null, L.T("AIL_aa2c0935", "error de red: ") + inner.Message, (DateTime.UtcNow - started).TotalSeconds);
            }
        }

        private static LlmResult ParseResponse(string text)
        {
            try
            {
                var j = JObject.Parse(text);
                var content = j["choices"]?[0]?["message"]?["content"];
                string s = content?.Type == JTokenType.Array
                    ? string.Concat(content.Select(c => (string)c["text"] ?? ""))
                    : (string)content;
                s = StripThink(s ?? "").Trim();
                if (string.IsNullOrEmpty(s))
                    return new LlmResult { Ok = false, Error = L.T("AIL_f533587d", "respuesta vacía (finish_reason=") + (string)j["choices"]?[0]?["finish_reason"] + ")" };
                return new LlmResult
                {
                    Ok = true,
                    Text = s,
                    PromptTokens = (int?)j["usage"]?["prompt_tokens"] ?? 0,
                    CompletionTokens = (int?)j["usage"]?["completion_tokens"] ?? 0,
                };
            }
            catch (Exception ex)
            {
                return new LlmResult { Ok = false, Error = "respuesta ilegible: " + ex.Message };
            }
        }

        private static string StripThink(string s) =>
            Regex.Replace(s, @"<think>.*?</think>", "", RegexOptions.Singleline | RegexOptions.IgnoreCase);

        private static string Trim(string s, int n) => s == null ? "" : (s.Length <= n ? s : s.Substring(0, n) + "...");
    }
}
