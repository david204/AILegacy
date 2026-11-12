using System;
using System.Collections.Generic;
using System.IO;
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
        public int CachedTokens;
        public int CompletionTokens;
        public double Seconds;
        /// <summary>La respuesta se cortó por el tope de tokens.</summary>
        public bool Truncated;
    }

    /// <summary>
    /// Cliente para cualquier endpoint compatible con OpenAI (/chat/completions).
    ///
    /// · Cola con prioridad: lo que el jugador está esperando (diálogo) sale antes que lo de fondo (ambiente, mundo,
    ///   resúmenes), y lo de fondo nunca ocupa todos los huecos.
    /// · Cadena de proveedores: el preferido de la tarea primero y el resto de respaldo.
    /// · Disyuntor: un proveedor que da 429, agota su cuota o no contesta se deja en pausa un rato en vez de probarlo
    ///   en cada petición.
    /// · Escalera de parámetros: solo se baja de nivel ante un 400/422 (parámetro no aceptado). Una respuesta vacía o
    ///   cortada se reintenta con más sitio y, si no, pasa al siguiente proveedor sin tocar el nivel.
    /// · Contabilidad por proveedor y tarea (UsageMeter), tope de gasto diario y tareas de fondo solo con proveedores gratis.
    /// </summary>
    public static class LlmClient
    {
        private static readonly HttpClient Http;
        /// <summary>El mismo cliente HTTP, para las peticiones de vectores.</summary>
        internal static HttpClient HttpShared => Http;

        static LlmClient()
        {
            try
            {
                // .NET Framework no activa TLS 1.2/1.3 por defecto
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { /* Tls13 no disponible */ }
                // Por defecto .NET Framework solo abre 2 conexiones por servidor: con varias tareas a la vez, el diálogo
                // se quedaba esperando conexión y agotaba su tiempo sin haber salido
                if (ServicePointManager.DefaultConnectionLimit < 16) ServicePointManager.DefaultConnectionLimit = 16;
            }
            catch (Exception ex) { Log.Warn("No se pudo ajustar la red: " + ex.Message); }

            Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("AILegacy/0.1");
        }

        // ============================================================ opciones y tareas

        /// <summary>Opciones leídas en el hilo principal (MCM) antes de saltar al hilo de fondo.</summary>
        private class CallOptions
        {
            public double Temperature = 0.8;
            public int MaxTokens = 900;
            public string Preferred = "";
            public string Task = "other";
            public int Priority = 1;
            public bool BackgroundFreeOnly = true;
            public double DailyCapUsd;
            public List<ProviderConfig> Providers = new List<ProviderConfig>();
            public string NoProvidersError;
            public DateTime Deadline;
        }

        private static volatile bool _debugBodies;

        /// <summary>0 = el jugador está esperando · 1 = importa pero puede esperar · 2 = de fondo.</summary>
        private static int PriorityOf(string task)
        {
            switch (task)
            {
                case "dialogue": case "initiative": case "group": case "verdict": case "order": case "editor": case "test":
                    return 0;
                case "letter": case "banter": case "passby":
                    return 1;
                default:
                    return 2;       // world, summary, lore, ambient, battle, embed, other
            }
        }

        private static CallOptions CaptureOptions(int? maxTokensOverride, string task, string preferred)
        {
            var o = new CallOptions { Task = string.IsNullOrWhiteSpace(task) ? "other" : task.Trim().ToLowerInvariant(), Preferred = (preferred ?? "").Trim() };
            o.Priority = PriorityOf(o.Task);
            try
            {
                var s = Settings.S.Get;
                o.Temperature = s.TemperatureX100 / 100.0;
                o.MaxTokens = maxTokensOverride ?? s.MaxTokens;
                o.BackgroundFreeOnly = s.BackgroundOnFreeProviders;
                o.DailyCapUsd = Math.Max(0, s.DailySpendCapCents) / 100.0;
                _debugBodies = s.DebugLogBodies;
            }
            catch { if (maxTokensOverride.HasValue) o.MaxTokens = maxTokensOverride.Value; }
            // La lista de proveedores se lee aquí (toca MCM y el disco), no en el hilo de fondo
            try
            {
                var cfg = LegacyConfig.Current;
                o.Providers = cfg.Providers.Where(p => p.IsUsable).ToList();
                if (o.Providers.Count == 0) o.NoProvidersError = LegacyConfig.LastLoadError ?? L.T("AIL_bb646884", "No hay ninguna clave: ponla en Opciones, Mod Options, AI Legacy, 0. Claves de IA.");
            }
            catch (Exception ex) { o.NoProvidersError = ex.Message; }
            o.Deadline = DateTime.UtcNow.AddSeconds(o.Priority == 0 ? 75 : 180);
            return o;
        }

        /// <param name="task">Para qué es: dialogue, initiative, group, verdict, order, letter, banter, passby, ambient, world, summary, lore, battle, editor. Decide la prioridad y dónde se apunta el gasto.</param>
        public static Task<LlmResult> CompleteAsync(List<ChatMessage> messages, bool wantJson, int? maxTokensOverride = null, string preferredProvider = null, string task = null)
        {
            var opts = CaptureOptions(maxTokensOverride, task, preferredProvider);
            var job = new Job { Messages = messages, WantJson = wantJson, Opts = opts, Seq = Interlocked.Increment(ref _seq) };
            lock (QueueGate) Queue.Add(job);
            Pump();
            return job.Done.Task;
        }

        // ============================================================ cola con prioridad

        private class Job
        {
            public List<ChatMessage> Messages;
            public bool WantJson;
            public CallOptions Opts;
            public long Seq;
            public readonly TaskCompletionSource<LlmResult> Done = new TaskCompletionSource<LlmResult>();
        }

        private const int MaxInFlight = 6;
        private const int MaxBackgroundInFlight = 2;
        private static readonly object QueueGate = new object();
        private static readonly List<Job> Queue = new List<Job>();
        private static int _inFlight, _backgroundInFlight;
        private static long _seq;

        private static void Pump()
        {
            while (true)
            {
                Job next = null;
                lock (QueueGate)
                {
                    if (Queue.Count == 0 || _inFlight >= MaxInFlight) return;
                    foreach (var j in Queue.OrderBy(j => j.Opts.Priority).ThenBy(j => j.Seq))
                    {
                        if (j.Opts.Priority >= 2 && _backgroundInFlight >= MaxBackgroundInFlight) continue;
                        next = j; break;
                    }
                    if (next == null) return;
                    Queue.Remove(next);
                    _inFlight++;
                    if (next.Opts.Priority >= 2) _backgroundInFlight++;
                }
                var job = next;
                // El tiempo de cada proveedor empieza a contar ahora, al salir de la cola (no mientras esperaba turno)
                job.Opts.Deadline = DateTime.UtcNow.AddSeconds(job.Opts.Priority == 0 ? 75 : 180);
                Task.Run(async () =>
                {
                    LlmResult r;
                    try { r = await CompleteInternal(job.Messages, job.WantJson, job.Opts).ConfigureAwait(false); }
                    catch (Exception ex) { r = new LlmResult { Ok = false, Error = ex.GetBaseException().Message }; }
                    lock (QueueGate) { _inFlight--; if (job.Opts.Priority >= 2) _backgroundInFlight--; }
                    job.Done.TrySetResult(r);
                    Pump();
                });
            }
        }

        // ============================================================ estado de cada proveedor (disyuntor y cupo por minuto)

        private class ProviderState
        {
            public DateTime BlockedUntil = DateTime.MinValue;
            public string BlockReason;
            public int Fails429, Timeouts, ServerErrors;
            public readonly Queue<DateTime> Recent = new Queue<DateTime>();
        }

        private static readonly Dictionary<string, ProviderState> States = new Dictionary<string, ProviderState>(StringComparer.OrdinalIgnoreCase);

        private static ProviderState StateOf(ProviderConfig p)
        {
            string key = p.Name + "|" + p.Model;
            lock (States)
            {
                if (!States.TryGetValue(key, out var s)) States[key] = s = new ProviderState();
                return s;
            }
        }

        /// <summary>null si se puede usar ahora; si no, por qué no.</summary>
        private static string Unavailable(ProviderConfig p)
        {
            var st = StateOf(p);
            var now = DateTime.UtcNow;
            lock (st)
            {
                if (st.BlockedUntil > now) return $"en pausa {(int)(st.BlockedUntil - now).TotalSeconds} s ({st.BlockReason})";
                if (p.Rpm > 0)
                {
                    while (st.Recent.Count > 0 && (now - st.Recent.Peek()).TotalSeconds > 60) st.Recent.Dequeue();
                    if (st.Recent.Count >= p.Rpm) return $"cupo de {p.Rpm} peticiones por minuto agotado";
                }
                return null;
            }
        }

        private static void NoteSent(ProviderConfig p) { var st = StateOf(p); lock (st) st.Recent.Enqueue(DateTime.UtcNow); }

        private static void NoteOk(ProviderConfig p) { var st = StateOf(p); lock (st) { st.Fails429 = 0; st.Timeouts = 0; st.ServerErrors = 0; } }

        private static void Block(ProviderConfig p, double seconds, string reason)
        {
            var st = StateOf(p);
            lock (st) { st.BlockedUntil = DateTime.UtcNow.AddSeconds(seconds); st.BlockReason = reason; }
            Log.Warn($"Proveedor {p.Name} en pausa {(int)seconds} s: {reason}");
        }

        /// <summary>Apunta un fallo y decide si el proveedor se deja en pausa.</summary>
        private static void NoteFailure(ProviderConfig p, int status, string body, double? retryAfter)
        {
            var st = StateOf(p);
            if (status == 429)
            {
                int n; lock (st) n = ++st.Fails429;
                string b = body ?? "";
                bool daily = b.IndexOf("RESOURCE_EXHAUSTED", StringComparison.OrdinalIgnoreCase) >= 0 && (b.IndexOf("per day", StringComparison.OrdinalIgnoreCase) >= 0 || b.IndexOf("PerDay", StringComparison.Ordinal) >= 0 || b.IndexOf("daily", StringComparison.OrdinalIgnoreCase) >= 0)
                             || b.IndexOf("insufficient_quota", StringComparison.OrdinalIgnoreCase) >= 0;
                if (daily) { Block(p, 3600, "cuota agotada"); return; }
                double wait = retryAfter.HasValue && retryAfter.Value > 0 ? Math.Min(600, retryAfter.Value + 1) : Math.Min(600, 30 * Math.Pow(2, Math.Min(5, n - 1)));
                Block(p, wait, "límite de peticiones (429)");
            }
            else if (status == 0)
            {
                int n; lock (st) n = ++st.Timeouts;
                if (n >= 3) { Block(p, 120, "no contesta"); lock (st) st.Timeouts = 0; }
            }
            else if (status >= 500)
            {
                int n; lock (st) n = ++st.ServerErrors;
                if (n >= 2) { Block(p, 60, $"error del servidor ({status})"); lock (st) st.ServerErrors = 0; }
            }
            else if (status == 401 || status == 403)
                Block(p, 600, $"clave rechazada ({status})");
        }

        /// <summary>Estado de los proveedores, para el editor.</summary>
        public static List<object> ProviderStatus()
        {
            var list = new List<object>();
            try
            {
                foreach (var p in LegacyConfig.Current.Providers.Where(x => x.IsUsable))
                    list.Add(new { name = p.Name, model = p.Model, paid = p.IsPaid, price_in = p.PriceIn, price_out = p.PriceOut, rpm = p.Rpm, state = Unavailable(p) ?? "ok" });
            }
            catch { }
            return list;
        }

        public static int QueueLength { get { lock (QueueGate) return Queue.Count + _inFlight; } }

        // ============================================================ prueba de conexión

        /// <summary>Botón de MCM: prueba cada proveedor por separado y lo cuenta en el registro del juego.</summary>
        public static void TestAllProviders()
        {
            var opts = CaptureOptions(60, "test", null);
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
                // La prueba se salta la pausa: es justo para saber si ya vuelve a funcionar
                { var st = StateOf(prov); lock (st) st.BlockedUntil = DateTime.MinValue; }
                Task.Run(() => TryProvider(prov, msgs, false, opts)).ContinueWith(t =>
                {
                    var r = t.IsFaulted ? new LlmResult { Ok = false, Error = t.Exception?.GetBaseException().Message } : t.Result;
                    Core.MainThread.Post(() => Show(r.Ok ? L.F("AIL_263bf6a9", $"{prov.Name}/{prov.Model}: OK en {r.Seconds:0.0}s") : $"{prov.Name}/{prov.Model}: ERROR {r.Error}",
                        r.Ok ? TaleWorlds.Library.Colors.Green : TaleWorlds.Library.Colors.Red));
                });
            }
        }

        private static void Show(string msg, TaleWorlds.Library.Color c) =>
            TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage("AI Legacy — " + msg, c));

        // ============================================================ cadena de proveedores

        private static async Task<LlmResult> CompleteInternal(List<ChatMessage> messages, bool wantJson, CallOptions opts)
        {
            var providers = opts.Providers;
            if (providers.Count == 0) return new LlmResult { Ok = false, Error = opts.NoProvidersError ?? "sin proveedores" };

            // Tareas de fondo: solo con proveedores gratis, si hay alguno (el de pago queda para lo que el jugador espera)
            if (opts.Priority >= 2 && opts.BackgroundFreeOnly && providers.Any(p => !p.IsPaid) && providers.Any(p => p.IsPaid))
                providers = providers.Where(p => !p.IsPaid).ToList();
            // Tope de gasto diario: alcanzado, los de pago se quedan fuera
            if (opts.DailyCapUsd > 0 && UsageMeter.SpentToday >= opts.DailyCapUsd)
            {
                var free = providers.Where(p => !p.IsPaid).ToList();
                if (free.Count == 0) return new LlmResult { Ok = false, Error = L.T("AIL_c3a7f1e1", "Se ha alcanzado el tope de gasto diario (MCM › 7. IA) y no hay ningún proveedor gratis.") };
                providers = free;
            }
            // Proveedor preferido para esta tarea: primero; el resto de la cadena como respaldo
            if (!string.IsNullOrEmpty(opts.Preferred))
                providers = providers.OrderBy(p => string.Equals(p.Name, opts.Preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

            var errors = new List<string>();
            LlmResult prose = null;           // respuesta que no es JSON cuando se pidió JSON: último recurso
            for (int round = 0; round < 2; round++)
            {
                bool anyTried = false;
                foreach (var p in providers)
                {
                    if (DateTime.UtcNow >= opts.Deadline) { errors.Add("tiempo agotado"); goto done; }
                    string why = Unavailable(p);
                    if (why != null) { if (round == 0) errors.Add($"{p.Name}: {why}"); continue; }
                    anyTried = true;
                    var r = await TryProvider(p, messages, wantJson, opts).ConfigureAwait(false);
                    if (r.Ok)
                    {
                        // Se pidió JSON y ha llegado prosa: se prueba el siguiente; si nadie da JSON, vale esta
                        if (wantJson && r.Text.IndexOf('{') < 0) { if (prose == null) prose = r; errors.Add($"{p.Name}: respuesta sin JSON"); Log.Warn($"Proveedor {p.Name}: se pidió JSON y ha contestado en prosa, pruebo el siguiente"); continue; }
                        return r;
                    }
                    errors.Add($"{p.Name}: {r.Error}");
                    Log.Warn($"Proveedor {p.Name} falló, paso al siguiente: {r.Error}");
                }
                if (anyTried || prose != null) break;
                // Todos en pausa o sin cupo. Si el jugador está esperando, se aguanta un poco al que antes vuelva
                if (opts.Priority != 0) break;
                double wait = providers.Select(p => { var st = StateOf(p); lock (st) return (st.BlockedUntil - DateTime.UtcNow).TotalSeconds; }).Where(s => s > 0).DefaultIfEmpty(5).Min();
                if (wait > 20 || DateTime.UtcNow.AddSeconds(wait) >= opts.Deadline) break;
                await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, wait + 0.5))).ConfigureAwait(false);
            }
        done:
            if (prose != null) return prose;
            return new LlmResult { Ok = false, Error = string.Join(" | ", errors.Distinct()) };
        }

        /// <summary>
        /// Prueba un proveedor. Solo un 400/422 (parámetro no aceptado) baja la escalera de parámetros:
        /// completo → sin "extra" → sin modo JSON → mínimo. Una respuesta vacía o cortada NO la baja.
        /// </summary>
        private static async Task<LlmResult> TryProvider(ProviderConfig p, List<ChatMessage> messages, bool wantJson, CallOptions opts)
        {
            string lastError = null;
            // Nivel de parámetros que este proveedor aceptó la última vez: se empieza por ahí para no repetir la petición
            int start;
            lock (WorkingLevel) WorkingLevel.TryGetValue(p.Name + "|" + p.Model, out start);
            int maxTokens = opts.MaxTokens;
            bool grown = false;
            for (int level = start; level < 4; level++)
            {
                var body = BuildBody(p, opts, messages, wantJson, level, maxTokens);
                NoteSent(p);
                var (status, text, err, secs, retryAfter) = await Send(p, body, opts).ConfigureAwait(false);
                if (status == 200)
                {
                    var r = ParseResponse(text);
                    r.Provider = p.Name + "/" + p.Model;
                    r.Seconds = secs;
                    UsageMeter.Record(p.Name, opts.Task, r.Ok, r.PromptTokens, r.CachedTokens, r.CompletionTokens, secs, p.PriceIn, p.PriceOut);
                    if (r.Ok)
                    {
                        NoteOk(p);
                        Log.Info($"OK {r.Provider} [{opts.Task}] en {secs:0.0}s (in={r.PromptTokens}" + (r.CachedTokens > 0 ? $", caché={r.CachedTokens}" : "") + $" out={r.CompletionTokens})" + (level > 0 ? $" [nivel {level}]" : ""));
                        if (level != start)
                        {
                            lock (WorkingLevel) WorkingLevel[p.Name + "|" + p.Model] = level;
                            Log.Warn($"{p.Name}: no acepta todos los parámetros; desde ahora se le envía el nivel {level} " +
                                     (level == 1 ? L.T("AIL_967c929e", "(sin los \"extra\" de config.json: revisa ese bloque)") : level == 2 ? L.T("AIL_d02be029", "(sin modo JSON)") : L.T("AIL_db9c0e6a", "(mínimo)")) + $". Motivo: {lastError}");
                        }
                        return r;
                    }
                    // Vacía o ilegible. Si se cortó por tokens (el razonamiento se los comió), un intento con el doble de sitio
                    lastError = r.Error;
                    if (r.Truncated && !grown && level < 3 && DateTime.UtcNow < opts.Deadline)
                    {
                        grown = true;
                        maxTokens = Math.Min(4000, Math.Max(maxTokens * 2, 1200));
                        Log.Warn($"{p.Name}: respuesta cortada por el tope de tokens; reintento con {maxTokens}");
                        level--;            // el for lo vuelve a subir: se repite ESTE mismo nivel, una sola vez (grown)
                        continue;
                    }
                    return new LlmResult { Ok = false, Error = lastError };      // al siguiente proveedor, sin tocar el nivel
                }

                lastError = $"HTTP {status}: {Trim(err ?? text, 300)}";
                UsageMeter.Record(p.Name, opts.Task, false, 0, 0, 0, secs, 0, 0);

                bool badParam = (status == 400 || status == 422) && (text ?? "").IndexOf("api key", StringComparison.OrdinalIgnoreCase) < 0 && (text ?? "").IndexOf("API_KEY", StringComparison.Ordinal) < 0;
                if (!badParam)
                {
                    NoteFailure(p, status, text ?? err, retryAfter);
                    return new LlmResult { Ok = false, Error = lastError };
                }
                // 400/422: probar con menos parámetros
            }
            return new LlmResult { Ok = false, Error = lastError ?? "error desconocido" };
        }

        private static string BuildBody(ProviderConfig p, CallOptions opts, List<ChatMessage> messages, bool wantJson, int level, int maxTokens)
        {
            var body = new JObject
            {
                ["model"] = p.Model,
                ["messages"] = new JArray(messages.Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Content })),
            };
            // Escalera: 0 = todo · 1 = sin "extra" (pero CON modo JSON) · 2 = sin modo JSON · 3 = mínimo
            if (level < 3)
            {
                body[string.IsNullOrWhiteSpace(p.MaxTokensField) ? "max_tokens" : p.MaxTokensField] = maxTokens;
                if (p.SendTemperature) body["temperature"] = opts.Temperature;
            }
            if (level < 2 && wantJson && p.JsonMode) body["response_format"] = new JObject { ["type"] = "json_object" };
            if (level == 0 && p.Extra != null)
                foreach (var kv in p.Extra) body[kv.Key] = kv.Value?.DeepClone();
            return body.ToString(Formatting.None);
        }

        private static readonly Dictionary<string, int> WorkingLevel = new Dictionary<string, int>();

        // ============================================================ envío

        private static int _dumpSeq;

        /// <summary>Vuelca la petición o la respuesta: el «último» de siempre y un anillo de las 20 últimas con su tarea y proveedor.</summary>
        private static void Dump(string kind, string task, string provider, int seq, string content)
        {
            try
            {
                Log.Dump(kind == "req" ? "last_request.json" : "last_response.json", content);
                string dir = Path.Combine(Paths.LogDir, "peticiones");
                Directory.CreateDirectory(dir);
                string safe = new string((task + "_" + provider).Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
                File.WriteAllText(Path.Combine(dir, $"{seq % 20:00}_{kind}_{safe}.json"), content ?? "");
                // Lo que ocupaba ese hueco con otro nombre (otra tarea) se quita
                foreach (var f in Directory.GetFiles(dir, $"{seq % 20:00}_{kind}_*.json"))
                    if (!f.EndsWith($"{seq % 20:00}_{kind}_{safe}.json", StringComparison.OrdinalIgnoreCase)) File.Delete(f);
            }
            catch { }
        }

        private static async Task<(int status, string text, string error, double secs, double? retryAfter)> Send(ProviderConfig p, string body, CallOptions opts)
        {
            var url = p.BaseUrl.TrimEnd('/') + "/chat/completions";
            var started = DateTime.UtcNow;
            int seq = Interlocked.Increment(ref _dumpSeq);
            if (_debugBodies) Dump("req", opts.Task, p.Name, seq, body);
            // El tiempo de este proveedor, sin pasar del plazo total de la tarea
            double left = (opts.Deadline - started).TotalSeconds;
            if (left < 3) return (0, null, "tiempo agotado", 0, null);          // el plazo total de la tarea manda
            double budget = Math.Min(Math.Max(5, p.TimeoutSeconds), left);
            try
            {
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budget)))
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        var text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        var secs = (DateTime.UtcNow - started).TotalSeconds;
                        if (_debugBodies) Dump("res", opts.Task, p.Name, seq, text);
                        double? retry = null;
                        try
                        {
                            var ra = resp.Headers.RetryAfter;
                            if (ra != null) retry = ra.Delta?.TotalSeconds ?? (ra.Date.HasValue ? (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
                        }
                        catch { }
                        return ((int)resp.StatusCode, text, null, secs, retry);
                    }
                }
            }
            catch (TaskCanceledException)
            {
                return (0, null, L.F("AIL_b74fa9dd", $"sin respuesta en {(int)budget}s"), (DateTime.UtcNow - started).TotalSeconds, null);
            }
            catch (Exception ex)
            {
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                return (0, null, L.T("AIL_aa2c0935", "error de red: ") + inner.Message, (DateTime.UtcNow - started).TotalSeconds, null);
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
                string finish = (string)j["choices"]?[0]?["finish_reason"] ?? "";
                var usage = j["usage"];
                int tin = (int?)usage?["prompt_tokens"] ?? 0, tout = (int?)usage?["completion_tokens"] ?? 0;
                int cached = (int?)usage?["prompt_tokens_details"]?["cached_tokens"] ?? (int?)usage?["cached_tokens"] ?? (int?)usage?["prompt_cache_hit_tokens"] ?? 0;
                bool cut = string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) || string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase);
                if (string.IsNullOrEmpty(s))
                    return new LlmResult { Ok = false, Truncated = cut, PromptTokens = tin, CachedTokens = cached, CompletionTokens = tout, Error = L.T("AIL_f533587d", "respuesta vacía (finish_reason=") + finish + ")" };
                return new LlmResult { Ok = true, Text = s, Truncated = cut, PromptTokens = tin, CachedTokens = cached, CompletionTokens = tout };
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
