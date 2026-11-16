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
        /// <summary>The reply was cut off by the token limit.</summary>
        public bool Truncated;
        /// <summary>The provider answered, but with no text.</summary>
        public bool Empty;
    }

    /// <summary>
    /// Client for any OpenAI-compatible endpoint (/chat/completions).
    ///
    /// Priority queue: what the player is waiting on (dialogue) goes before background work (ambient, world,
    /// summaries), and background work never takes every slot.
    /// Provider chain: the task's preferred provider first, the rest as fallback.
    /// Circuit breaker: a provider that returns 429, runs out of quota or stops answering is paused for a while
    /// instead of being tried on every request.
    /// Parameter ladder: only a 400/422 (rejected parameter) steps down a level. An empty or cut-off reply is
    /// retried with more room, then moves to the next provider without touching the level.
    /// Usage is tracked per provider and task (UsageMeter), with a daily spend cap. Background tasks can be
    /// limited to free providers.
    /// </summary>
    public static class LlmClient
    {
        private static readonly HttpClient Http;
        /// <summary>Same HTTP client, reused for embedding requests.</summary>
        internal static HttpClient HttpShared => Http;

        static LlmClient()
        {
            try
            {
                // .NET Framework doesn't enable TLS 1.2/1.3 by default
                ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                try { ServicePointManager.SecurityProtocol |= (SecurityProtocolType)12288; } catch { /* no TLS 1.3 on this runtime */ }
                // .NET Framework opens only 2 connections per host by default. With several tasks running, dialogue
                // requests sat waiting for a connection and timed out before they were even sent.
                if (ServicePointManager.DefaultConnectionLimit < 16) ServicePointManager.DefaultConnectionLimit = 16;
            }
            catch (Exception ex) { Log.Warn("Could not tune the network: " + ex.Message); }

            Http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan };
            Http.DefaultRequestHeaders.UserAgent.ParseAdd("AILegacy/0.1");
        }

        // ============================================================ options and tasks

        /// <summary>Read on the main thread (MCM) before the call moves to a background thread.</summary>
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
            /// <summary>Streamed request: gets the reply text written so far, on a background thread. Returning false drops the request.</summary>
            public Func<string, bool> OnPartial;
        /// <summary>OnPartial gets the whole text so far instead of the "reply" field (a reply with several lines).</summary>
        public bool RawPartial;
            public bool Stream;
            /// <summary>Some text of an attempt was already handed over, so a new attempt has to clear it first.</summary>
            public bool PartialSent;
        }

        private static volatile bool _debugBodies;

        /// <summary>0 = player is waiting, 1 = matters but can wait, 2 = background.</summary>
        private static int PriorityOf(string task)
        {
            switch (task)
            {
                case "dialogue": case "initiative": case "group": case "verdict": case "order": case "editor": case "test": case "lesson":
                    return 0;
                case "letter": case "banter": case "passby":
                    return 1;
                default:
                    return 2;       // world, summary, lore, ambient, battle, embed, other
            }
        }

        private static CallOptions CaptureOptions(int? maxTokensOverride, string task, string preferred, Func<string, bool> onPartial = null)
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
                // Only what is shown in the dialogue or over a speaker's head as it is written is streamed.
                // World, summaries, letters and battles never are.
                o.Stream = onPartial != null && s.StreamReplies && (o.Task == "dialogue" || o.Task == "group");
                if (o.Stream) o.OnPartial = onPartial;
            }
            catch { if (maxTokensOverride.HasValue) o.MaxTokens = maxTokensOverride.Value; }
            // Read the provider list here, not on the background thread (it touches MCM and the disk)
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

        /// <param name="task">What the call is for: dialogue, initiative, group, verdict, order, letter, banter, passby, ambient, world, summary, lore, battle, editor. Sets the priority and where usage is logged.</param>
        public static Task<LlmResult> CompleteAsync(List<ChatMessage> messages, bool wantJson, int? maxTokensOverride = null, string preferredProvider = null, string task = null)
        {
            var opts = CaptureOptions(maxTokensOverride, task, preferredProvider);
            var job = new Job { Messages = messages, WantJson = wantJson, Opts = opts, Seq = Interlocked.Increment(ref _seq) };
            lock (QueueGate) Queue.Add(job);
            Pump();
            return job.Done.Task;
        }

        /// <summary>
        /// Same as CompleteAsync, but the reply is read as it is written. <paramref name="onPartial"/> gets the text of the
        /// "reply" field so far (the whole text when no JSON is asked for), on a background thread, and an empty string
        /// when an attempt is thrown away and another starts. Return false from it to drop the request.
        /// The final result is the same as without streaming. Only the "dialogue" task streams, and only if the setting is on.
        /// </summary>
        public static Task<LlmResult> CompleteStreamedAsync(List<ChatMessage> messages, bool wantJson, Func<string, bool> onPartial, int? maxTokensOverride = null, string preferredProvider = null, string task = null, bool raw = false)
        {
            var opts = CaptureOptions(maxTokensOverride, task, preferredProvider, onPartial);
            opts.RawPartial = raw;
            var job = new Job { Messages = messages, WantJson = wantJson, Opts = opts, Seq = Interlocked.Increment(ref _seq) };
            lock (QueueGate) Queue.Add(job);
            Pump();
            return job.Done.Task;
        }

        // ============================================================ priority queue

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
                // The deadline starts when the job leaves the queue, not while it waits its turn
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

        // ============================================================ provider state (circuit breaker and per-minute quota)

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

        /// <summary>null if usable right now, otherwise the reason it isn't.</summary>
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
            Log.Warn($"Provider {p.Name} paused for {(int)seconds} s: {reason}");
        }

        /// <summary>Counts a failure and pauses the provider when needed.</summary>
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

        /// <summary>Provider status, shown in the editor.</summary>
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

        // ============================================================ connection test

        /// <summary>
        /// MCM button: tests each provider on its own and reports the result in the game log.
        /// <paramref name="onlyActive"/>: leave out the ones that are off or have no key (the welcome message on load).
        /// </summary>
        public static void TestAllProviders(bool onlyActive = false)
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
                    if (onlyActive) continue;
                    Core.MainThread.Post(() => Show(L.F("AIL_0cd6f2e1", $"{p.Name}: desactivado o sin api_key"), TaleWorlds.Library.Colors.Gray));
                    continue;
                }
                var prov = p;
                // The test ignores the pause, since the point is to see if the provider works again
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

        // ============================================================ provider chain

        private static async Task<LlmResult> CompleteInternal(List<ChatMessage> messages, bool wantJson, CallOptions opts)
        {
            var providers = opts.Providers;
            if (providers.Count == 0) return new LlmResult { Ok = false, Error = opts.NoProvidersError ?? "sin proveedores" };

            // Background tasks stick to free providers when there are any, paid ones are kept for what the player waits on
            if (opts.Priority >= 2 && opts.BackgroundFreeOnly && providers.Any(p => !p.IsPaid) && providers.Any(p => p.IsPaid))
                providers = providers.Where(p => !p.IsPaid).ToList();
            // Daily spend cap reached: paid providers are left out
            if (opts.DailyCapUsd > 0 && UsageMeter.SpentToday >= opts.DailyCapUsd)
            {
                var free = providers.Where(p => !p.IsPaid).ToList();
                if (free.Count == 0) return new LlmResult { Ok = false, Error = L.T("AIL_c3a7f1e1", "Se ha alcanzado el tope de gasto diario (MCM › 1. IA) y no hay ningún proveedor gratis.") };
                providers = free;
            }
            if (!string.IsNullOrEmpty(opts.Preferred))
                providers = providers.OrderBy(p => string.Equals(p.Name, opts.Preferred, StringComparison.OrdinalIgnoreCase) ? 0 : 1).ToList();

            var errors = new List<string>();
            LlmResult prose = null;           // non-JSON reply to a JSON request, kept as a last resort
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
                        // Asked for JSON and got prose: try the next one, and fall back to this if nobody returns JSON
                        if (wantJson && r.Text.IndexOf('{') < 0) { if (prose == null) prose = r; errors.Add($"{p.Name}: response without JSON"); Log.Warn($"Provider {p.Name}: JSON was requested but it answered in prose, trying the next one"); continue; }
                        return r;
                    }
                    errors.Add($"{p.Name}/{p.Model}: {r.Error}");
                    Log.Warn($"Provider {p.Name}/{p.Model} failed, moving to the next: {r.Error}");
                }
                if (anyTried || prose != null) break;
                // All paused or over quota. If the player is waiting, hold on a bit for the first one to come back
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
        /// Tries one provider. Only a 400/422 (rejected parameter) steps down the parameter ladder:
        /// full, then no "extra", then no JSON mode, then minimal. An empty or cut-off reply does not.
        /// </summary>
        private static async Task<LlmResult> TryProvider(ProviderConfig p, List<ChatMessage> messages, bool wantJson, CallOptions opts)
        {
            string lastError = null;
            // Start at the level this provider accepted last time, so a rejected request isn't sent again
            int start;
            lock (WorkingLevel) WorkingLevel.TryGetValue(p.Name + "|" + p.Model, out start);
            int maxTokens = opts.MaxTokens;
            bool grown = false;
            string key = p.Name + "|" + p.Model;
            bool stream = opts.Stream && !IsFlagged(NoStream, key);
            bool usageOpt = stream && !IsFlagged(NoStreamUsage, key);
            bool droppedUsage = false, suspectStream = false, streamNamed = false;
            // On Off, models that think unless told not to get their "don't" (dropped like any effort if refused)
            bool reasoning = (ReasoningLevel(p) != null || ThinksByDefault(p)) && !IsFlagged(NoReasoning, key);
            for (int level = start; level < 4; level++)
            {
                ClearPartial(opts);
                bool withReasoning = reasoning && level < 3;
                var body = BuildBody(p, opts, messages, wantJson, level, maxTokens, stream, usageOpt, reasoning);
                NoteSent(p);
                int status; string text, err; double secs; double? retryAfter;
                LlmResult streamed = null;
                if (stream)
                {
                    var so = await SendStream(p, body, opts, wantJson).ConfigureAwait(false);
                    if (so.Dropped) return new LlmResult { Ok = false, Error = "petición descartada" };
                    status = so.Status; text = so.Text; err = so.Error; secs = so.Secs; retryAfter = so.RetryAfter;
                    if (status == 200 && so.Result == null)
                    {
                        // The stream broke halfway: ask again the normal way, and stop streaming with this provider
                        UsageMeter.Record(p.Name, opts.Task, false, 0, 0, 0, secs, 0, 0);
                        Flag(NoStream, key);
                        Log.Warn($"{p.Name}: streaming response was cut off ({err}); retrying without streaming and won't ask for it again this session");
                        lastError = "streaming cortado: " + err;
                        stream = false; usageOpt = false;
                        level--;
                        continue;
                    }
                    if (withReasoning && RejectsReasoning(status, err ?? text))
                    {
                        // The reasoning field is the first suspect when the error names it: same level again, without it
                        UsageMeter.Record(p.Name, opts.Task, false, 0, 0, 0, secs, 0, 0);
                        lastError = $"HTTP {status}: {Trim(err ?? text, 300)}";
                        DropReasoning(p, key, status, err ?? text);
                        reasoning = false;
                        level--;
                        continue;
                    }
                    if ((status == 400 || status == 422) && IsBadParam(text))
                    {
                        UsageMeter.Record(p.Name, opts.Task, false, 0, 0, 0, secs, 0, 0);
                        lastError = $"HTTP {status}: {Trim(err ?? text, 300)}";
                        // First suspect is the usage option, then streaming itself. Only after that, the usual parameter ladder
                        if (usageOpt) { usageOpt = false; droppedUsage = true; }
                        else { stream = false; suspectStream = true; streamNamed = ((err ?? "") + (text ?? "")).IndexOf("stream", StringComparison.OrdinalIgnoreCase) >= 0; }
                        level--;
                        continue;
                    }
                    if (status == 200)
                    {
                        streamed = so.Result;
                        if (droppedUsage && !IsFlagged(NoStreamUsage, key))
                        {
                            Flag(NoStreamUsage, key);
                            Log.Warn($"{p.Name}: does not accept stream_options; usage of its streaming responses will be estimated");
                        }
                    }
                }
                else
                {
                    (status, text, err, secs, retryAfter) = await Send(p, body, opts).ConfigureAwait(false);
                    if (withReasoning && RejectsReasoning(status, err ?? text))
                    {
                        UsageMeter.Record(p.Name, opts.Task, false, 0, 0, 0, secs, 0, 0);
                        lastError = $"HTTP {status}: {Trim(err ?? text, 300)}";
                        DropReasoning(p, key, status, err ?? text);
                        reasoning = false;
                        level--;
                        continue;
                    }
                    if (suspectStream)
                    {
                        suspectStream = false;
                        // Only when the provider's own error named streaming. A 400 about something else that happened
                        // to pass on the retry must not switch streaming off for the whole session.
                        if (status == 200 && streamNamed)
                        {
                            Flag(NoStream, key);
                            Log.Warn($"{p.Name}: rejects streaming requests; won't ask for it again this session");
                        }
                    }
                }
                if (status == 200)
                {
                    var r = streamed ?? ParseResponse(text);
                    r.Provider = p.Name + "/" + p.Model;
                    r.Seconds = secs;
                    UsageMeter.Record(p.Name, opts.Task, r.Ok, r.PromptTokens, r.CachedTokens, r.CompletionTokens, secs, p.PriceIn, p.PriceOut);
                    if (r.Ok)
                    {
                        NoteOk(p);
                        Log.Info($"OK {r.Provider} [{opts.Task}] in {secs:0.0}s (in={r.PromptTokens}" + (r.CachedTokens > 0 ? $", cache={r.CachedTokens}" : "") + $" out={r.CompletionTokens})" + (level > 0 ? $" [level {level}]" : ""));
                        if (level != start)
                        {
                            lock (WorkingLevel) WorkingLevel[p.Name + "|" + p.Model] = level;
                            Log.Warn($"{p.Name}: does not accept all parameters; from now on it gets level {level} " +
                                     (level == 1 ? L.T("AIL_967c929e", "(sin los \"extra\" de config.json: revisa ese bloque)") : level == 2 ? L.T("AIL_d02be029", "(sin modo JSON)") : L.T("AIL_db9c0e6a", "(mínimo)")) + $". Reason: {lastError}");
                        }
                        return r;
                    }
                    // Empty or unreadable. If it hit the token limit (reasoning ate the budget), try once with twice the room
                    lastError = r.Error;
                    if (r.Truncated && !grown && level < 3 && DateTime.UtcNow < opts.Deadline)
                    {
                        grown = true;
                        maxTokens = Math.Min(4000, Math.Max(maxTokens * 2, 1200));
                        Log.Warn($"{p.Name}: response cut by the token limit; retrying with {maxTokens}");
                        level--;            // the loop bumps it back up, so this same level runs again (once, see grown)
                        continue;
                    }
                    if (r.Empty) NoteEmpty(p, key, r.Truncated);
                    return new LlmResult { Ok = false, Error = lastError, Empty = r.Empty, Truncated = r.Truncated };      // next provider, level unchanged
                }

                lastError = $"HTTP {status}: {Trim(err ?? text, 300)}";
                UsageMeter.Record(p.Name, opts.Task, false, 0, 0, 0, secs, 0, 0);

                bool badParam = (status == 400 || status == 422) && IsBadParam(text);
                if (!badParam)
                {
                    NoteFailure(p, status, text ?? err, retryAfter);
                    return new LlmResult { Ok = false, Error = lastError };
                }
                // 400/422: retry with fewer parameters
            }
            return new LlmResult { Ok = false, Error = lastError ?? "error desconocido" };
        }

        /// <summary>A 400/422 that is about the request's parameters and not about the key.</summary>
        private static bool IsBadParam(string text) =>
            (text ?? "").IndexOf("api key", StringComparison.OrdinalIgnoreCase) < 0 && (text ?? "").IndexOf("API_KEY", StringComparison.Ordinal) < 0;

        // Providers that turned out not to stream, or not to take stream_options, in this session (key = name|model)
        private static readonly HashSet<string> NoStream = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> NoStreamUsage = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private static bool IsFlagged(HashSet<string> set, string key) { lock (set) return set.Contains(key); }
        private static void Flag(HashSet<string> set, string key) { lock (set) set.Add(key); }

        /// <summary>Hands the text written so far to whoever is showing it. False = they no longer want it.</summary>
        private static bool SendPartial(CallOptions opts, string text)
        {
            if (opts.OnPartial == null) return true;
            opts.PartialSent = text.Length > 0;
            try { return opts.OnPartial(text); }
            catch (Exception ex) { Log.Error("Partial response text", ex); return true; }
        }

        /// <summary>Before a new attempt: what the previous one showed is no longer valid.</summary>
        private static void ClearPartial(CallOptions opts)
        {
            if (opts.OnPartial != null && opts.PartialSent) SendPartial(opts, "");
        }

        private static string BuildBody(ProviderConfig p, CallOptions opts, List<ChatMessage> messages, bool wantJson, int level, int maxTokens, bool stream = false, bool usageOpt = false, bool reasoning = false)
        {
            var body = new JObject
            {
                ["model"] = p.Model,
                ["messages"] = new JArray(messages.Select(m => new JObject { ["role"] = m.Role, ["content"] = m.Content })),
            };
            // Reasoning effort (off unless chosen). The minimal level of the ladder never carries it
            string effort = reasoning && level < 3 ? ReasoningLevel(p) : null;
            var style = effort == null ? ReasoningStyle.None : StyleOf(p);
            // Thinking tokens come out of the same budget as the reply, so they get room of their own on top
            int room = style == ReasoningStyle.Anthropic ? ThinkingBudget(effort) : style != ReasoningStyle.None ? ReasoningRoom(effort) : 0;
            // Ladder: 0 = everything, 1 = no "extra" (JSON mode kept), 2 = no JSON mode, 3 = minimal
            if (level < 3)
            {
                body[string.IsNullOrWhiteSpace(p.MaxTokensField) ? "max_tokens" : p.MaxTokensField] = Math.Min(maxTokens + room, 64000);
                // Anthropic takes no temperature while thinking
                if (p.SendTemperature && style != ReasoningStyle.Anthropic) body["temperature"] = opts.Temperature;
            }
            if (level < 2 && wantJson && p.JsonMode) body["response_format"] = new JObject { ["type"] = "json_object" };
            if (level == 0 && p.Extra != null)
                foreach (var kv in p.Extra) body[kv.Key] = kv.Value?.DeepClone();
            switch (style)
            {
                case ReasoningStyle.OpenAI:
                    // OpenAI, Gemini (OpenAI-compatible), Groq, Ollama, DeepSeek...: low / medium / high; there is nothing above high
                    body["reasoning_effort"] = effort == "max" ? "high" : effort;
                    break;
                case ReasoningStyle.OpenRouter:
                    body.Remove("reasoning_effort");
                    body["reasoning"] = new JObject { ["effort"] = effort == "max" ? "high" : effort };
                    break;
                case ReasoningStyle.Anthropic:
                    body.Remove("reasoning_effort");
                    body["thinking"] = new JObject { ["type"] = "enabled", ["budget_tokens"] = room };
                    break;
            }
            // Off: GLM thinks by default and spends the reply's tokens on it, so it comes back empty. Tell it not to
            if (reasoning && level < 3 && effort == null && ThinksByDefault(p))
                body["thinking"] = new JObject { ["type"] = "disabled" };
            if (stream)
            {
                body["stream"] = true;
                if (usageOpt) body["stream_options"] = new JObject { ["include_usage"] = true };
            }
            return body.ToString(Formatting.None);
        }

        private static readonly Dictionary<string, int> WorkingLevel = new Dictionary<string, int>();

        // ============================================================ reasoning effort

        private enum ReasoningStyle { None, OpenAI, OpenRouter, Anthropic }

        /// <summary>low, medium, high or max; null when the provider has it off.</summary>
        private static string ReasoningLevel(ProviderConfig p)
        {
            string e = (p.ReasoningEffort ?? "").Trim().ToLowerInvariant();
            switch (e)
            {
                case "low": case "medium": case "high": case "max": return e;
                case "minimal": return "low";
                case "xhigh": case "maximum": return "max";
                default: return null;       // "", off, none
            }
        }

        /// <summary>
        /// Models that think unless told not to and take a field that turns it off: GLM ({"thinking":{"type":"disabled"}}),
        /// on Zhipu's own endpoints or anywhere else under a glm name. Not through OpenRouter or Anthropic, which have their own
        /// form, nor when config.json already sets "thinking" in "extra".
        /// </summary>
        private static bool ThinksByDefault(ProviderConfig p)
        {
            if (p.Extra != null && p.Extra.ContainsKey("thinking")) return false;
            string u = (p.BaseUrl ?? "").ToLowerInvariant(), m = (p.Model ?? "").Trim().ToLowerInvariant();
            if (u.Contains("openrouter.ai") || u.Contains("anthropic.com")) return false;
            return u.Contains("bigmodel.cn") || u.Contains("z.ai") || m.StartsWith("glm");
        }

        // Providers already warned this session about an empty reply (key = name|model)
        private static readonly HashSet<string> EmptyNoted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Once per session and provider: it answered with no text, most often a model that spent its room thinking.</summary>
        private static void NoteEmpty(ProviderConfig p, string key, bool cut)
        {
            if (IsFlagged(EmptyNoted, key)) return;
            Flag(EmptyNoted, key);
            string who = p.Name + "/" + p.Model;
            string effort = string.IsNullOrEmpty(p.ReasoningEffort) ? "off" : p.ReasoningEffort;
            Log.Warn($"{who}: returned an empty reply{(cut ? " cut by the token limit" : "")} with reasoning effort \"{effort}\". If the model thinks before answering, try another effort (Low) or another model.");
            Core.MainThread.Post(() => Show(cut
                ? L.F("AIL_e7b1dd01", $"{who} devolvió una respuesta vacía: se quedó sin espacio, seguramente pensando. Prueba otro esfuerzo de razonamiento para él en 0. Claves de IA (por ejemplo Low) u otro modelo.")
                : L.F("AIL_e7b1dd02", $"{who} devolvió una respuesta vacía. Si el modelo piensa antes de contestar, prueba otro esfuerzo de razonamiento para él en 0. Claves de IA (por ejemplo Low) u otro modelo."),
                TaleWorlds.Library.Colors.Yellow));
        }

        /// <summary>A campaign starts or loads: the empty-reply notice may show again.</summary>
        public static void ResetSession() { lock (EmptyNoted) EmptyNoted.Clear(); }

        /// <summary>For the player waiting on a reply that never came: why, in one short line on screen (the full text is in the log).</summary>
        public static void ShowFailure(LlmResult r)
        {
            string why = Trim((r?.Error ?? "").Replace("\r", " ").Replace("\n", " "), 220);
            if (why.Length == 0) return;
            Core.MainThread.Post(() => Show(L.F("AIL_e7b1dd03", $"Sin respuesta de la IA: {why}"), TaleWorlds.Library.Colors.Red));
        }

        private static ReasoningStyle StyleOf(ProviderConfig p)
        {
            string u = (p.BaseUrl ?? "").ToLowerInvariant();
            if (u.Contains("openrouter.ai")) return ReasoningStyle.OpenRouter;
            if (u.Contains("anthropic.com")) return ReasoningStyle.Anthropic;
            return ReasoningStyle.OpenAI;
        }

        /// <summary>Extra output tokens left for the model's thinking, so it doesn't eat the reply.</summary>
        private static int ReasoningRoom(string effort) => effort == "low" ? 1024 : effort == "medium" ? 2048 : effort == "high" ? 4096 : 8192;

        /// <summary>Anthropic's thinking budget (at least 1024 tokens).</summary>
        private static int ThinkingBudget(string effort) => effort == "low" ? 1024 : effort == "medium" ? 4096 : effort == "high" ? 8192 : 16000;

        // Providers that rejected the reasoning field in this session (key = name|model): asked without it from then on
        private static readonly HashSet<string> NoReasoning = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>A 400/422 whose text is about the reasoning field we sent.</summary>
        private static bool RejectsReasoning(int status, string text)
        {
            if (status != 400 && status != 422) return false;
            string t = text ?? "";
            return t.IndexOf("reasoning", StringComparison.OrdinalIgnoreCase) >= 0 || t.IndexOf("thinking", StringComparison.OrdinalIgnoreCase) >= 0
                || t.IndexOf("effort", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>Notes once per session that this provider takes no reasoning effort, in the log and on screen.</summary>
        private static void DropReasoning(ProviderConfig p, string key, int status, string text)
        {
            if (IsFlagged(NoReasoning, key)) return;
            Flag(NoReasoning, key);
            Log.Warn($"{p.Name}/{p.Model}: rejected the reasoning effort \"{(string.IsNullOrEmpty(p.ReasoningEffort) ? "off (thinking disabled)" : p.ReasoningEffort)}\" (HTTP {status}: {Trim(text, 300)}). Retrying without it; it won't be sent to this provider again this session.");
            string who = p.Name + "/" + p.Model;
            Core.MainThread.Post(() => Show(L.F("AIL_e7b1d107", $"{who} no acepta el esfuerzo de razonamiento; sigue sin él (detalles en el log)."), TaleWorlds.Library.Colors.Yellow));
        }

        // ============================================================ sending

        private static int _dumpSeq;

        /// <summary>Dumps the request or reply: the usual last_*.json plus a ring of the last 20, named by task and provider.</summary>
        private static void Dump(string kind, string task, string provider, int seq, string content)
        {
            try
            {
                Log.Dump(kind == "req" ? "last_request.json" : "last_response.json", content);
                string dir = Path.Combine(Paths.LogDir, "peticiones");
                Directory.CreateDirectory(dir);
                string safe = new string((task + "_" + provider).Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray());
                File.WriteAllText(Path.Combine(dir, $"{seq % 20:00}_{kind}_{safe}.json"), content ?? "");
                // Drop whatever held this slot under another name (another task)
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
            // Per-provider timeout, capped by the task's total deadline
            double left = (opts.Deadline - started).TotalSeconds;
            if (left < 3) return (0, null, "tiempo agotado", 0, null);
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

        private class StreamOutcome
        {
            public int Status;
            public string Text, Error;
            public double Secs;
            public double? RetryAfter;
            /// <summary>Set when the stream ended properly (or the provider answered with a plain reply). Null with status 200 = it broke.</summary>
            public LlmResult Result;
            /// <summary>Whoever asked no longer wants the reply.</summary>
            public bool Dropped;
        }

        /// <summary>
        /// Same request as Send, read as Server-Sent Events. The text is handed over through opts.OnPartial while it arrives.
        /// Same time budget as the normal request, counted over the whole reply.
        /// </summary>
        private static async Task<StreamOutcome> SendStream(ProviderConfig p, string body, CallOptions opts, bool wantJson)
        {
            var o = new StreamOutcome();
            var url = p.BaseUrl.TrimEnd('/') + "/chat/completions";
            var started = DateTime.UtcNow;
            int seq = Interlocked.Increment(ref _dumpSeq);
            if (_debugBodies) Dump("req", opts.Task, p.Name, seq, body);
            double left = (opts.Deadline - started).TotalSeconds;
            if (left < 3) { o.Error = "tiempo agotado"; return o; }
            double budget = Math.Min(Math.Max(5, p.TimeoutSeconds), left);

            var sse = new SseReader();
            var asm = new StreamAssembler();
            var scanner = wantJson && !opts.RawPartial ? new ReplyFieldScanner() : null;
            var raw = new StringBuilder();
            bool reading = false;
            var timedOut = new bool[1];
            var cts = new CancellationTokenSource(TimeSpan.FromSeconds(budget));
            try
            {
                using (var req = new HttpRequestMessage(HttpMethod.Post, url))
                {
                    req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
                    req.Headers.TryAddWithoutValidation("Accept", "text/event-stream");
                    req.Content = new StringContent(body, Encoding.UTF8, "application/json");
                    using (var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                    // A read already waiting on the network does not see the token on .NET Framework: closing the response wakes it up
                    using (cts.Token.Register(() => { timedOut[0] = true; try { resp.Dispose(); } catch { } }))
                    {
                        o.Status = (int)resp.StatusCode;
                        try
                        {
                            var ra = resp.Headers.RetryAfter;
                            if (ra != null) o.RetryAfter = ra.Delta?.TotalSeconds ?? (ra.Date.HasValue ? (ra.Date.Value - DateTimeOffset.UtcNow).TotalSeconds : (double?)null);
                        }
                        catch { }
                        string ctype = resp.Content.Headers.ContentType?.MediaType ?? "";
                        bool plainJson = ctype.IndexOf("json", StringComparison.OrdinalIgnoreCase) >= 0;
                        if (o.Status != 200 || plainJson)
                        {
                            // An error, or a provider that ignored "stream" and sent the whole reply at once
                            o.Text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                            o.Secs = (DateTime.UtcNow - started).TotalSeconds;
                            if (_debugBodies) Dump("res", opts.Task, p.Name, seq, o.Text);
                            if (o.Status == 200) o.Result = ParseResponse(o.Text);
                            return o;
                        }

                        reading = true;
                        var events = new List<string>();
                        using (var net = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                        {
                            var buffer = new byte[8192];
                            while (!asm.Done)
                            {
                                int n = await net.ReadAsync(buffer, 0, buffer.Length, cts.Token).ConfigureAwait(false);
                                if (n <= 0) break;
                                events.Clear();
                                sse.Feed(buffer, n, events);
                                if (!TakeEvents(events, asm, scanner, raw, opts)) { o.Dropped = true; return o; }
                            }
                            if (!asm.Done)
                            {
                                events.Clear();
                                sse.Finish(events);
                                if (!TakeEvents(events, asm, scanner, raw, opts)) { o.Dropped = true; return o; }
                            }
                        }
                    }
                }
                o.Secs = (DateTime.UtcNow - started).TotalSeconds;
                if (_debugBodies) Dump("res", opts.Task, p.Name, seq, raw.Length > 0 ? raw.ToString() : sse.Other.ToString());

                if (asm.Error != null) o.Error = "el proveedor cortó con un error: " + Trim(asm.Error, 200);
                else if (asm.Events == 0 && sse.Other.ToString().TrimStart().StartsWith("{"))
                {
                    o.Text = sse.Other.ToString();
                    o.Result = ParseResponse(o.Text);
                }
                else if (!asm.Complete) o.Error = asm.Events == 0 ? "no llegó ningún dato" : "terminó sin aviso de fin";
                else o.Result = StreamResult(asm, body.Length);
                return o;
            }
            catch (Exception ex)
            {
                o.Secs = (DateTime.UtcNow - started).TotalSeconds;
                if (ex is OperationCanceledException || timedOut[0])
                {
                    o.Status = 0; o.Result = null;
                    o.Error = L.F("AIL_b74fa9dd", $"sin respuesta en {(int)budget}s");
                    return o;
                }
                var inner = ex;
                while (inner.InnerException != null) inner = inner.InnerException;
                if (reading) { o.Status = 200; o.Result = null; o.Error = inner.Message; }       // headers were fine, the body broke
                else { o.Status = 0; o.Error = L.T("AIL_aa2c0935", "error de red: ") + inner.Message; }
                return o;
            }
            finally { try { cts.Dispose(); } catch { } }
        }

        /// <summary>Feeds the events just read to the assembler and hands the new text over. False = the reply is no longer wanted.</summary>
        private static bool TakeEvents(List<string> events, StreamAssembler asm, ReplyFieldScanner scanner, StringBuilder raw, CallOptions opts)
        {
            bool grew = false;
            foreach (var data in events)
            {
                if (raw.Length < 400000) raw.Append("data: ").Append(data).Append("\n\n");
                string added = asm.OnEvent(data);
                if (added.Length == 0) continue;
                grew = true;
                scanner?.Feed(added);
            }
            if (!grew) return true;
            if (scanner != null) return !scanner.Started || SendPartial(opts, scanner.Text);
            return SendPartial(opts, asm.Content.ToString());
        }

        /// <summary>The finished stream as a result, the same way ParseResponse builds it from a whole reply.</summary>
        private static LlmResult StreamResult(StreamAssembler asm, int requestChars)
        {
            string content = asm.Content.ToString();
            string s = StripThink(content).Trim();
            string finish = asm.FinishReason;
            // No usage chunk: count about four characters per token so the spend cap still sees the call
            int tin = asm.HasUsage ? asm.PromptTokens : requestChars / 4;
            int tout = asm.HasUsage ? asm.CompletionTokens : (content.Length + 3) / 4;
            int cached = asm.HasUsage ? asm.CachedTokens : 0;
            bool cut = string.Equals(finish, "length", StringComparison.OrdinalIgnoreCase) || string.Equals(finish, "MAX_TOKENS", StringComparison.OrdinalIgnoreCase);
            if (string.IsNullOrEmpty(s))
                return new LlmResult { Ok = false, Empty = true, Truncated = cut, PromptTokens = tin, CachedTokens = cached, CompletionTokens = tout, Error = L.T("AIL_f533587d", "respuesta vacía (finish_reason=") + finish + ")" };
            return new LlmResult { Ok = true, Text = s, Truncated = cut, PromptTokens = tin, CachedTokens = cached, CompletionTokens = tout };
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
                    return new LlmResult { Ok = false, Empty = true, Truncated = cut, PromptTokens = tin, CachedTokens = cached, CompletionTokens = tout, Error = L.T("AIL_f533587d", "respuesta vacía (finish_reason=") + finish + ")" };
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
