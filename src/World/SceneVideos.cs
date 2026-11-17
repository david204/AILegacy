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
        public const int Off = 0, OpenAi = 1, Endpoint = 2, Gemini = 3;
        public static readonly string[] SourceNames = { "Off", "OpenAI (your key, paid)", "OpenAI-compatible server", "Google Gemini · Veo (your key)" };
        /// <summary>What the video setting offers: a service and its model in one choice.</summary>
        internal static readonly (string label, int source, string model)[] VideoOptions =
        {
            ("Off", Off, ""),
            ("OpenAI · sora-2 (your key)", OpenAi, "sora-2"),
            ("OpenAI · sora-2-pro (your key)", OpenAi, "sora-2-pro"),
            ("Google · Veo 3.1 fast · veo-3.1-fast-generate-preview (your key)", Gemini, "veo-3.1-fast-generate-preview"),
            ("Google · Veo 3.1 · veo-3.1-generate-preview (your key)", Gemini, "veo-3.1-generate-preview"),
            ("OpenAI-compatible server · address and key in Advanced", Endpoint, ""),
        };

        public static string[] VideoLabels => VideoOptions.Select(o => o.label).ToArray();

        private static int Choice { get { try { return Math.Max(0, Math.Min(VideoOptions.Length - 1, S.Get.VideoService.SelectedIndex)); } catch { return 0; } } }
        public static int Source => VideoOptions[Choice].source;

        /// <summary>The model: the one written in Advanced, or the one of the list.</summary>
        private static string ChosenModel
        {
            get
            {
                string m = (S.Get.VideoCustomModel ?? "").Trim();
                return m.Length > 0 ? m : VideoOptions[Choice].model;
            }
        }

        /// <summary>The clip made from the scene picture: who says what, so they speak it in the clip.</summary>
        public sealed class Scene
        {
            public string Image, NpcName, PlayerName, NpcLine, PlayerLine, NpcLook, PlayerLook, Where, Season, Time;
        }

        private static readonly Tpl TSpeak = new Tpl("videos/from_picture.txt",
@"The attached image is the first frame of this clip. Keep the same two people all through it: the same faces, hair, age, build, clothes and gear, in the same place and light. Do not restyle anything and do not add anyone.

WHERE: {where}. Season: {season}. Time of day: {time}.

WHO IS WHO
- {player_name}: {player_look}.
- {npc_name}: {npc_look}.

WHAT HAPPENS ({seconds} seconds)
1. {player_name} says: ""{player_line}""
2. {npc_name} answers: ""{npc_line}""
{acting}

VOICES: {voices}
ACTING: small, natural gestures that fit the words; they keep facing each other as people in a conversation.
CAMERA: steady, at most a slow push in; no cuts.
REALISM: photorealistic, like a historical film; bodies, cloth and objects move in a physically possible way.
NOT ALLOWED: subtitles, captions or any writing, a change of style, new people, modern objects.");
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

        public static void Touch() { var _ = TPrompt.Key + TSpeak.Key; }

        /// <summary>A line as it is said: what is acted (*between asterisks*) set apart, no double quotes.</summary>
        private static (string said, string acted) Split(string line)
        {
            line = (line ?? "").Trim();
            var acted = new List<string>();
            string said = System.Text.RegularExpressions.Regex.Replace(line, @"\*([^*]+)\*", m => { acted.Add(m.Groups[1].Value.Trim()); return " "; });
            said = System.Text.RegularExpressions.Regex.Replace(said.Replace("\"", "'").Replace("«", "'").Replace("»", "'"), @"\s{2,}", " ").Trim();
            return (said.Length == 0 ? "(says nothing)" : said, string.Join("; ", acted));
        }

        private static string SpeakPrompt(Scene s, int seconds)
        {
            var p = Split(s.PlayerLine); var n = Split(s.NpcLine);
            var acting = new List<string>();
            if (p.acted.Length > 0) acting.Add($"({s.PlayerName}, acted, not spoken: {p.acted})");
            if (n.acted.Length > 0) acting.Add($"({s.NpcName}, acted, not spoken: {n.acted})");
            string voices = S.Get.VideoVoices
                ? "each speaks with a natural voice of their own that fits their age and sex; only the one speaking moves their lips; no narrator and no music over the words."
                : "nobody's voice is heard: only the sounds of the place; their lips may move as people talking.";
            return TSpeak.Format("where", s.Where ?? "", "season", s.Season ?? "", "time", s.Time ?? "", "player_name", s.PlayerName ?? "the traveller", "player_look", s.PlayerLook ?? "",
                "npc_name", s.NpcName ?? "the other", "npc_look", s.NpcLook ?? "", "seconds", seconds, "player_line", p.said, "npc_line", n.said,
                "acting", string.Join("\n", acting), "voices", voices);
        }

        /// <summary>Whether a clip carries the characters' voices (then the mod's own voice is not played for that reply).</summary>
        public static bool CarriesSpeech => Enabled && S.Get.VideoVoices;

        /// <summary>The first frame at the size of the clip, cropped to fill it, as JPEG.</summary>
        private static byte[] Frame(string path, int w, int h)
        {
            using (var src = System.Drawing.Image.FromFile(path))
            using (var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
            {
                float k = Math.Max(w / (float)src.Width, h / (float)src.Height);
                int sw = (int)(w / k), sh = (int)(h / k);
                var cut = new System.Drawing.Rectangle((src.Width - sw) / 2, (src.Height - sh) / 2, sw, sh);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                    g.DrawImage(src, new System.Drawing.Rectangle(0, 0, w, h), cut, System.Drawing.GraphicsUnit.Pixel);
                }
                using (var ms = new MemoryStream()) { bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Jpeg); return ms.ToArray(); }
            }
        }

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
        public static void Offer(string people, string where, string exchange, Action<string> done, bool ask = true, Action accepted = null, Scene scene = null)
        {
            var cfg = S.Get;
            if (!Enabled || Busy || string.IsNullOrWhiteSpace(exchange)) return;
            if (!UnderCap()) { WorldSim.Say(L.F("AIL_e7b1c855", $"Ya has hecho los {cfg.VideoPerDay} vídeos de hoy (tope de los ajustes)."), Colors.Yellow); return; }
            // a talk with video asks once, with the cost of each clip; after that every reply gets its clip (or never, by setting)
            if (!ask || !cfg.VideoAskCost) { accepted?.Invoke(); Make(people, where, exchange, done, scene); return; }
            int secs = Math.Max(4, Math.Min(12, cfg.VideoSeconds));
            double cost = secs * Math.Max(0, cfg.VideoCentsPerSecond) / 100.0;
            string costText = Source == OpenAi ? L.F("AIL_e7b1c856", $"Unos {cost:0.00} $ de tu cuenta de OpenAI ({secs} s).")
                            : Source == Gemini ? L.F("AIL_e7b1df61", $"Unos {cost:0.00} $ de tu cuenta de Google ({secs} s).")
                            : L.F("AIL_e7b1c857", $"Lo hace tu servidor de vídeo ({secs} s).");
            InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1c858", "Vídeo de la escena"),
                L.F("AIL_e7b1c859", $"{costText} Tarda unos minutos; puedes seguir jugando y te aviso cuando esté.\n\n¿Lo hago?"),
                true, true, L.T("AIL_e7b1c85a", "Hacerlo"), L.T("AIL_e5c9b3a4", "Cancelar"),
                () => { accepted?.Invoke(); Make(people, where, exchange, done, scene); }, null), true);
        }

        private static void Make(string people, string where, string exchange, Action<string> done, Scene scene = null)
        {
            var cfg = S.Get;
            if (Busy) return;
            Busy = true;
            int gen = _gen, session = WorldSim.Session;
            string dir = SceneImages.Dir;
            int source = Source;
            string model = ChosenModel, size = (cfg.VideoSize ?? "").Trim();
            int secs = Math.Max(4, Math.Min(12, cfg.VideoSeconds));
            string baseUrl = null, key = null;
            if (source == Gemini)
            {
                var g = Named(cfg.VideoProvider) ?? SceneImages.GeminiProvider;
                if (g == null) { Busy = false; WorldSim.Say(L.T("AIL_e7b1df60", "No hay ningún proveedor de Google Gemini con clave en 0. Claves de IA."), Colors.Red); return; }
                key = g.ApiKey;
            }
            else if (source == OpenAi)
            {
                ProviderConfig p = Named(cfg.VideoProvider);
                try { if (p == null) p = LegacyConfig.Current.Providers.FirstOrDefault(x => x.IsUsable && (x.BaseUrl ?? "").IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
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
                    if (file == null) { Log.Warn("Scene video: " + error); WorldSim.Say(L.F("AIL_e7b1c85d", $"El vídeo no se ha podido hacer: {SceneImages.ShortError(error)}"), Colors.Red); return; }
                    _today++;      // only the clips that were made count toward the daily cap
                    Log.Info("Scene video: " + file);
                    try { done?.Invoke(file); } catch (Exception ex) { Log.Error("Video: end callback", ex); }
                });
            }

            // From the scene picture: it is the first frame, and they say their lines in it
            if (scene != null && !string.IsNullOrEmpty(scene.Image) && File.Exists(scene.Image))
            {
                if (source == Gemini) secs = secs <= 4 ? 4 : secs <= 6 ? 6 : 8;
                string speak = SpeakPrompt(scene, secs);
                string vsize = size.Length > 0 ? size : "1280x720";
                Task.Run(async () =>
                {
                    try
                    {
                        int fw = 1280, fh = 720;
                        var dims = vsize.Split('x');
                        if (dims.Length == 2 && int.TryParse(dims[0], out var a) && int.TryParse(dims[1], out var b2) && a > 0 && b2 > 0) { fw = a; fh = b2; }
                        byte[] first = Frame(scene.Image, fw, fh);
                        byte[] mp4 = source == Gemini ? await VeoAny(key, model.Length > 0 ? model : "veo-3.1-fast-generate-preview", speak, first, secs).ConfigureAwait(false)
                                                      : await Generate(baseUrl, key, model.Length > 0 ? model : "sora-2", vsize, secs, speak, first).ConfigureAwait(false);
                        if (mp4 == null || mp4.Length < 10000) { Finish(null, "el servicio no devolvió un vídeo"); return; }
                        if (dir == null) { Finish(null, "sin campaña"); return; }
                        Directory.CreateDirectory(dir);
                        string file = Path.Combine(dir, "v_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".mp4");
                        File.WriteAllBytes(file, mp4);
                        Finish(file, null);
                    }
                    catch (Exception ex) { Finish(null, ex.GetBaseException().Message); }
                });
                return;
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
                        byte[] mp4 = source == Gemini ? await VeoAny(key, model.Length > 0 ? model : "veo-3.1-fast-generate-preview", prompt, null, secs <= 4 ? 4 : secs <= 6 ? 6 : 8).ConfigureAwait(false)
                                                      : await Generate(baseUrl, key, model.Length > 0 ? model : "sora-2", size.Length > 0 ? size : "1280x720", secs, prompt).ConfigureAwait(false);
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

        /// <summary>Only scheme, host and path of an address, for messages: no user, password or query (a key may hide there).</summary>
        private static string SafeUrl(string url)
        {
            try { var u = new Uri(url); return u.Scheme + "://" + u.Host + (u.IsDefaultPort ? "" : ":" + u.Port) + u.AbsolutePath; }
            catch { return "(address)"; }
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
                    if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode} ({method} {SafeUrl(url)}) {Encoding.UTF8.GetString(bytes, 0, Math.Min(200, bytes.Length))}");
                    return bytes;
                }
            }
        }

        /// <summary>The video API: create the job, wait until it is done (ten minutes at most), then fetch the clip.</summary>
        private static async Task<byte[]> Generate(string baseUrl, string key, string model, string size, int seconds, string prompt, byte[] firstFrame = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) throw new Exception("falta la dirección del servicio de vídeo");
            string root = baseUrl.TrimEnd('/');
            JObject job;
            if (firstFrame != null)
            {
                // the scene picture as the first frame (input_reference, the same size as the clip)
                var form = new MultipartFormDataContent();
                form.Add(new StringContent(model), "model");
                form.Add(new StringContent(prompt), "prompt");
                form.Add(new StringContent(seconds.ToString()), "seconds");
                form.Add(new StringContent(size), "size");
                var img = new ByteArrayContent(firstFrame);
                img.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg");
                form.Add(img, "input_reference", "first_frame.jpg");
                using (var req = new HttpRequestMessage(HttpMethod.Post, root + "/videos") { Content = form })
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
                {
                    if (!string.IsNullOrWhiteSpace(key)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + key.Trim());
                    using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) throw new Exception($"HTTP {(int)resp.StatusCode} (POST {SafeUrl(root + "/videos")}, model {model}, size {size}, {seconds} s) {(text.Length > 200 ? text.Substring(0, 200) : text)}");
                        job = JObject.Parse(text);
                    }
                }
            }
            else
            {
                var body = new JObject { ["model"] = model, ["prompt"] = prompt, ["seconds"] = seconds.ToString(), ["size"] = size };
                job = JObject.Parse(Encoding.UTF8.GetString(await Send(HttpMethod.Post, root + "/videos", key, body.ToString(), 60).ConfigureAwait(false)));
            }
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

        /// <summary>
        /// Google Veo through the Gemini API: a long-running job from the prompt (and the first frame, if any), polled until
        /// it is done, then the clip is fetched with the same key.
        /// </summary>
        private static string _veoFound;

        /// <summary>The provider of 0. AI Keys named in the video setting, or null.</summary>
        private static ProviderConfig Named(string name)
        {
            name = (name ?? "").Trim();
            if (name.Length == 0) return null;
            try { return LegacyConfig.Current.Providers.FirstOrDefault(p => p.IsUsable && string.Equals((p.Name ?? "").Trim(), name, StringComparison.OrdinalIgnoreCase)); }
            catch { return null; }
        }

        /// <summary>Veo with the chosen model; if this key does not have it, with one it has.</summary>
        private static async Task<byte[]> VeoAny(string key, string model, string prompt, byte[] firstFrame, int seconds)
        {
            if (_veoFound != null) model = _veoFound;
            try { return await FromVeo(key, model, prompt, firstFrame, seconds).ConfigureAwait(false); }
            catch (Exception ex) when (SceneImages.ModelMissing(ex))
            {
                string other = await SceneImages.GeminiModelFor(key, true).ConfigureAwait(false);
                if (other == null) throw new Exception("NO_VEO");
                if (other == model) throw;
                Log.Warn($"Veo: the model {model} is not in this account; using {other}");
                _veoFound = other;
                return await FromVeo(key, other, prompt, firstFrame, seconds).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> FromVeo(string key, string model, string prompt, byte[] firstFrame, int seconds)
        {
            const string root = "https://generativelanguage.googleapis.com/v1beta/";
            var instance = new JObject { ["prompt"] = prompt };
            if (firstFrame != null) instance["image"] = new JObject { ["bytesBase64Encoded"] = Convert.ToBase64String(firstFrame), ["mimeType"] = "image/jpeg" };
            var body = new JObject
            {
                ["instances"] = new JArray { instance },
                ["parameters"] = new JObject { ["aspectRatio"] = "16:9", ["durationSeconds"] = seconds, ["personGeneration"] = firstFrame != null ? "allow_adult" : "allow_all" },
            };
            string op;
            using (var req = new HttpRequestMessage(HttpMethod.Post, root + "models/" + Uri.EscapeDataString(model) + ":predictLongRunning"))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(90)))
            {
                req.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
                {
                    string text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    if (!resp.IsSuccessStatusCode) throw new Exception($"Veo: HTTP {(int)resp.StatusCode} {(text.Length > 200 ? text.Substring(0, 200) : text)}");
                    op = (string)JObject.Parse(text)["name"];
                }
            }
            if (string.IsNullOrEmpty(op)) throw new Exception("Veo: no job name");
            var until = DateTime.UtcNow.AddMinutes(12);
            while (DateTime.UtcNow < until)
            {
                await Task.Delay(8000).ConfigureAwait(false);
                JObject st;
                using (var req = new HttpRequestMessage(HttpMethod.Get, root + op))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30)))
                {
                    req.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
                    using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
                        st = JObject.Parse(await resp.Content.ReadAsStringAsync().ConfigureAwait(false));
                }
                if (st["error"] != null) throw new Exception("Veo: " + ((string)st["error"]["message"] ?? "error"));
                if (st["done"]?.Value<bool>() != true) continue;
                var resp2 = st["response"];
                string uri = (string)(resp2?["generateVideoResponse"]?["generatedSamples"]?[0]?["video"]?["uri"] ?? resp2?["generatedVideos"]?[0]?["video"]?["uri"]);
                if (string.IsNullOrEmpty(uri))
                {
                    string why = (string)resp2?["generateVideoResponse"]?["raiMediaFilteredReasons"]?[0] ?? "no clip in the answer";
                    throw new Exception("Veo: " + why);
                }
                using (var req = new HttpRequestMessage(HttpMethod.Get, uri))
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(180)))
                {
                    req.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
                    using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
                    {
                        var bytes = await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
                        if (!resp.IsSuccessStatusCode) throw new Exception($"Veo download: HTTP {(int)resp.StatusCode}");
                        return bytes;
                    }
                }
            }
            throw new Exception("el vídeo tardaba demasiado (más de doce minutos)");
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
