using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using AILegacy.Config;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>
    /// A short video of a moment of a conversation, on request and only after the player accepts what it will cost. The
    /// moment is first turned into a description for a camera (one AI request), then a video service makes the clip:
    /// OpenAI's video API with the player's own key, or any server that copies it. The clip is saved with the campaign
    /// and played in the in-game panel.
    /// </summary>
    internal static class SceneVideos
    {
        public const int Off = 0, OpenAi = 1, Endpoint = 2;
        public static readonly string[] SourceNames = { "Off", "OpenAI (your key, paid)", "OpenAI-compatible server" };
        public static int Source { get { try { return Math.Max(0, Math.Min(2, S.Get.VideoSource.SelectedIndex)); } catch { return Off; } } }
        public static bool Enabled => Source != Off;
        public static bool Busy { get; private set; }

        private static DateTime _day = DateTime.MinValue;
        private static int _today;
        private static int _gen;

        private static readonly Tpl TPrompt = new Tpl("videos/talk_moment.txt",
@"Turn this moment of a conversation into ONE prompt in English for a short video clip of it, a few seconds long. 50 to 90 words.
Describe what the camera sees and how it moves: the two people (man or woman, rough age, build, hair, clothing that fits their culture and station; no names), their gestures and expressions as the words suggest, the place, the time of day and the light, and one simple camera movement.
The world is Calradia, a medieval land with 12th-century technology: no magic, no modern objects. No writing or letters in the picture.
The people in it:
{people}
Where and when: {where}
What was just said:
{text}
Answer with the prompt only, nothing else.");

        public static void Touch() { var _ = TPrompt.Key; }

        public static void ResetSession() { _gen++; Busy = false; }

        private static bool UnderCap()
        {
            if (_day != DateTime.Today) { _day = DateTime.Today; _today = 0; }
            int cap = S.Get.VideoPerDay;
            return cap <= 0 || _today < cap;
        }

        /// <summary>
        /// Ask the player first (with the cost), then make the clip. <paramref name="done"/> gets the file on the main thread.
        /// </summary>
        public static void Offer(string people, string where, string exchange, Action<string> done, bool ask = true, Action accepted = null)
        {
            var cfg = S.Get;
            if (!Enabled || Busy || string.IsNullOrWhiteSpace(exchange)) return;
            if (!UnderCap()) { WorldSim.Say(L.F("AIL_e7b1c855", $"Ya has hecho los {cfg.VideoPerDay} vídeos de hoy (tope de los ajustes)."), Colors.Yellow); return; }
            // a talk with video asks once, with the cost of each clip; after that every reply gets its clip
            if (!ask) { Make(people, where, exchange, done); return; }
            int secs = Math.Max(4, Math.Min(12, cfg.VideoSeconds));
            double cost = secs * Math.Max(0, cfg.VideoCentsPerSecond) / 100.0;
            string costText = Source == OpenAi ? L.F("AIL_e7b1c856", $"Unos {cost:0.00} $ de tu cuenta de OpenAI ({secs} s).") : L.F("AIL_e7b1c857", $"Lo hace tu servidor de vídeo ({secs} s).");
            InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1c858", "Vídeo de la escena"),
                L.F("AIL_e7b1c859", $"{costText} Tarda unos minutos; puedes seguir jugando y te aviso cuando esté.\n\n¿Lo hago?"),
                true, true, L.T("AIL_e7b1c85a", "Hacerlo"), L.T("AIL_e5c9b3a4", "Cancelar"),
                () => { accepted?.Invoke(); Make(people, where, exchange, done); }, null), true);
        }

        private static void Make(string people, string where, string exchange, Action<string> done)
        {
            var cfg = S.Get;
            if (Busy) return;
            Busy = true;
            _today++;
            int gen = _gen, session = WorldSim.Session;
            string dir = SceneImages.Dir;
            int source = Source;
            string model = (cfg.VideoModel ?? "").Trim(), size = (cfg.VideoSize ?? "").Trim();
            int secs = Math.Max(4, Math.Min(12, cfg.VideoSeconds));
            string baseUrl = null, key = null;
            if (source == OpenAi)
            {
                ProviderConfig p = null;
                try { p = LegacyConfig.Current.Providers.FirstOrDefault(x => x.IsUsable && (x.BaseUrl ?? "").IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                if (p == null) { Busy = false; WorldSim.Say(L.T("AIL_e7b1c85b", "No hay ningún proveedor de OpenAI con clave en 0. Claves de IA."), Colors.Red); return; }
                baseUrl = p.BaseUrl; key = p.ApiKey;
            }
            else { baseUrl = cfg.VideoEndpointUrl; key = cfg.VideoEndpointKey; }
            WorldSim.Say(L.T("AIL_e7b1c85c", "Preparando el vídeo de la escena…"), Colors.Gray);

            void Finish(string file, string error)
            {
                MainThread.Post(() =>
                {
                    if (gen != _gen) return;
                    Busy = false;
                    if (session != WorldSim.Session) return;
                    if (file == null) { Log.Warn("Scene video: " + error); WorldSim.Say(L.F("AIL_e7b1c85d", $"El vídeo no se ha podido hacer: {error}"), Colors.Red); return; }
                    Log.Info("Scene video: " + file);
                    try { done?.Invoke(file); } catch (Exception ex) { Log.Error("Video: end callback", ex); }
                });
            }

            string ask;
            try { ask = TPrompt.Format("people", people ?? "", "where", where ?? "", "text", exchange.Length > 1500 ? exchange.Substring(0, 1500) : exchange); }
            catch (Exception ex) { Busy = false; Log.Error("Video: description", ex); return; }
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", ask) }, wantJson: false, task: "image_prompt", maxTokensOverride: 300)
                .ContinueWith(async t =>
                {
                    string prompt = null;
                    try { var r = t.IsFaulted ? null : t.Result; if (r != null && r.Ok) prompt = r.Text?.Trim().Trim('"', '`').Replace("\r", " ").Replace("\n", " "); } catch { }
                    if (string.IsNullOrWhiteSpace(prompt)) { Finish(null, "sin descripción de la escena"); return; }
                    if (prompt.Length > 900) prompt = prompt.Substring(0, 900);
                    try
                    {
                        byte[] mp4 = await Generate(baseUrl, key, model.Length > 0 ? model : "sora-2", size.Length > 0 ? size : "1280x720", secs, prompt).ConfigureAwait(false);
                        if (mp4 == null || mp4.Length < 10000) { Finish(null, "el servicio no devolvió un vídeo"); return; }
                        if (dir == null) { Finish(null, "sin campaña"); return; }
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, "v_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".mp4");
                        File.WriteAllBytes(file, mp4);
                        Finish(file, null);
                    }
                    catch (Exception ex) { Finish(null, ex.GetBaseException().Message); }
                });
        }

        private static async Task<byte[]> Send(HttpMethod method, string url, string key, string json, int seconds)
        {
            using (var req = new HttpRequestMessage(method, url))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            {
                if (!string.IsNullOrWhiteSpace(key)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
                if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
                using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode} {Encoding.UTF8.GetString(bytes, 0, Math.Min(200, bytes.Length))}");
                    return bytes;
                }
            }
        }

        /// <summary>The video API: create the job, wait until it is done (ten minutes at most), then fetch the clip.</summary>
        private static async Task<byte[]> Generate(string baseUrl, string key, string model, string size, int seconds, string prompt)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new Exception("falta la dirección del servicio de vídeo");
            string root = baseUrl.TrimEnd('/');
            var body = new JObject { ["model"] = model, ["prompt"] = prompt, ["seconds"] = seconds.ToString(), ["size"] = size };
            var job = JObject.Parse(Encoding.UTF8.GetString(await Send(HttpMethod.Post, root + "/videos", key, body.ToString(), 60).ConfigureAwait(false)));
            string id = (string)job["id"];
            if (string.IsNullOrEmpty(id)) throw new Exception("el servicio no dio un identificador de vídeo");
            var until = DateTime.UtcNow.AddMinutes(10);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(8000).ConfigureAwait(false);
                var st = JObject.Parse(Encoding.UTF8.GetString(await Send(HttpMethod.Get, root + "/videos/" + id, key, null, 30).ConfigureAwait(false)));
                string status = ((string)st["status"] ?? "").ToLowerInvariant();
                if (status == "completed" || status == "succeeded") return await Send(HttpMethod.Get, root + "/videos/" + id + "/content", key, null, 120).ConfigureAwait(false);
                if (status == "failed" || status == "cancelled" || status == "canceled")
                    throw new Exception((string)st["error"]?["message"] ?? status);
            }
            throw new Exception("el vídeo tardaba demasiado (más de diez minutos)");
        }

        /// <summary>Plays a clip in the in-game panel: a page next to it with the video and nothing else.</summary>
        public static void Play(string file, string title)
        {
            try
            {
                if (string.IsNullOrEmpty(file) || !File.Exists(file)) return;
                string page = Path.ChangeExtension(file, ".html");
                string name = Path.GetFileName(file);
                string html = "<!doctype html><html><head><meta charset='utf-8'><title>" + System.Security.SecurityElement.Escape(title ?? "") + "</title>"
                    + "<style>html,body{margin:0;height:100%;background:#16120d;color:#e8dcc4;font-family:Georgia,serif}"
                    + "div{display:flex;flex-direction:column;align-items:center;justify-content:center;height:100%}video{max-width:96%;max-height:86%;border:2px solid #c4a060}"
                    + "p{margin:10px}button{background:#2a2218;color:#e8dcc4;border:1px solid #c4a060;padding:6px 18px;cursor:pointer}</style></head><body><div>"
                    + "<video src='" + name + "' autoplay controls></video><p>" + System.Security.SecurityElement.Escape(title ?? "") + "</p>"
                    + "<button onclick=\"window.chrome.webview.postMessage('close')\">" + System.Security.SecurityElement.Escape(L.T("AIL_e8438e2a", "Cerrar")) + "</button></div>"
                    + "<script>document.addEventListener('keydown',e=>{if(e.key==='Escape')window.chrome.webview.postMessage('close')});</script></body></html>";
                File.WriteAllText(page, html, new UTF8Encoding(false));
                Web.EditorWindow.Show(new Uri(page).AbsoluteUri);
            }
            catch (Exception ex) { Log.Error("Watch the video", ex); }
        }
    }
}
