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
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;

namespace AILegacy.World
{
    /// <summary>
    /// One illustration per memory book page. The page text is first turned into a short visual description (one AI
    /// request), then an image service draws it. Files live in save_data\[campaign]\images as PNG.
    /// Sources: a free public service that needs no key, OpenAI with the player's own key, or a local Stable Diffusion.
    /// </summary>
    internal static class SceneImages
    {
        public const int Off = 0, Free = 1, OpenAi = 2, Local = 3;
        public static readonly string[] SourceNames = { "Off", "Free (Pollinations, no key)", "OpenAI (your key)", "Local Stable Diffusion" };

        private static readonly Tpl TPrompt = new Tpl("images/scene_prompt.txt",
@"Turn this page from a character's memories into ONE prompt in English for an illustration of its key moment. 40 to 70 words.
Describe only what would be seen: the people (man or woman, rough age, build, hair, clothing that fits their culture and station; no names), what they are doing, the place, the time of day and the light.
The world is Calradia, a medieval land with 12th-century technology: no magic, no modern objects. No writing or letters in the picture.
The people in it:
{people}
Page title: {title}
Page:
{text}
Answer with the prompt only, nothing else.");

        private static readonly Tpl TMoment = new Tpl("images/talk_moment.txt",
@"Turn this moment of a conversation into ONE prompt in English for an illustration of it, as if it were a still from the scene. 40 to 70 words.
Describe only what would be seen: the two people face to face (man or woman, rough age, build, hair, clothing that fits their culture and station; no names), their posture and expressions as the words suggest, the place, the time of day and the light.
The world is Calradia, a medieval land with 12th-century technology: no magic, no modern objects. No writing or letters in the picture.
The people in it:
{people}
Where and when: {title}
What was just said:
{text}
Answer with the prompt only, nothing else.");

        private static readonly Queue<(HeroMemory mem, MemoryPage page, Action done, Tpl tpl)> Pending = new Queue<(HeroMemory, MemoryPage, Action, Tpl)>();

        /// <summary>
        /// A picture of the moment just spoken in a conversation. Skipped while another picture is being made, so a quick
        /// talk does not pile up requests. <paramref name="shown"/> gets the file path on the main thread when it is ready.
        /// </summary>
        public static bool RequestMoment(HeroMemory mem, string where, string exchange, Action<string> shown)
        {
            if (!Enabled || mem == null || Dir == null || string.IsNullOrWhiteSpace(exchange)) return false;
            if (IsBusy) { Log.Info("Conversation image: another is already in progress, this reply gets no image"); return false; }
            var page = new MemoryPage { Title = where ?? "", Text = exchange, Day = CampaignTime.Now.ToDays };
            Pending.Enqueue((mem, page, () => { string path = PathOf(page); if (path != null) shown?.Invoke(path); }, TMoment));
            Next();
            return true;
        }
        private static bool _busy;
        private static int _failures;
        // Bumped on every session reset. A request that finishes after a reset belongs to nobody and must not touch
        // the busy flag of the new session.
        private static int _gen;
        private static MemoryPage _active;

        public static bool Enabled => Source != Off;
        public static int Source { get { try { return Math.Max(0, Math.Min(3, S.Get.ImageSource.SelectedIndex)); } catch { return Off; } } }
        public static bool IsBusy => _busy || Pending.Count > 0;

        public static void ResetSession() { _gen++; Pending.Clear(); _busy = false; _active = null; _failures = 0; }

        internal static string Dir
        {
            get
            {
                var c = Campaign.Current;
                return c == null || string.IsNullOrEmpty(c.UniqueGameId) ? null : Path.Combine(SaveFiles.DirFor(c.UniqueGameId), "images");
            }
        }

        /// <summary>Full path of a page's picture, or null if it has none on disk.</summary>
        public static string PathOf(MemoryPage page)
        {
            try
            {
                string dir = Dir;
                if (dir == null || page == null || string.IsNullOrEmpty(page.Image)) return null;
                string path = Path.Combine(dir, page.Image);
                return File.Exists(path) ? path : null;
            }
            catch { return null; }
        }

        /// <summary>Queues a picture for that page. Main thread. 'done' runs on the main thread whether it worked or not.</summary>
        public static void Request(HeroMemory mem, MemoryPage page, Action done = null)
        {
            if (!Enabled || mem == null || page == null || Dir == null) { done?.Invoke(); return; }
            if (_active == page || Pending.Any(x => x.page == page)) return;
            Pending.Enqueue((mem, page, done, null));
            Next();
        }

        private static void Next()
        {
            if (_busy || Pending.Count == 0) return;
            // after several failures in a row the service is down or misconfigured: stop asking this session
            if (_failures >= 4) { while (Pending.Count > 0) Pending.Dequeue().done?.Invoke(); return; }
            var job = Pending.Dequeue();
            _busy = true;
            _active = job.page;
            int gen = _gen;
            int session = WorldSim.Session;
            string dir = Dir;
            int source = Source;
            string style = (S.Get.ImageStyle ?? "").Trim();
            string localUrl = (S.Get.ImageLocalUrl ?? "").Trim().TrimEnd('/');
            string openAiModel = (S.Get.ImageOpenAiModel ?? "").Trim();
            ProviderConfig openAi = null;
            try { openAi = LegacyConfig.Current.Providers.FirstOrDefault(p => p.IsUsable && (p.BaseUrl ?? "").IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }

            void Finish(bool ok, string file, string prompt, string error)
            {
                MainThread.Post(() =>
                {
                    if (gen != _gen) return;            // a reset happened meanwhile: this job is no longer ours
                    _busy = false; _active = null;
                    if (session != WorldSim.Session) return;
                    if (ok)
                    {
                        _failures = 0;
                        job.page.Image = file; job.page.ImagePrompt = prompt;
                        if (job.tpl == null) SaveFiles.Dirty();
                        Log.Info($"Image {(job.tpl == null ? "of the page" : "of the conversation in")} «{job.page.Title}»: {file}");
                    }
                    else
                    {
                        _failures++;
                        if (!string.IsNullOrEmpty(prompt)) job.page.ImagePrompt = prompt;     // the description is kept for the next try
                        Log.Warn($"Image of the page «{job.page.Title}»: failed ({error})");
                    }
                    try { job.done?.Invoke(); } catch (Exception ex) { Log.Error("Image: end callback", ex); }
                    Next();
                });
            }

            // Step 1 (main thread): the visual description, unless the page already has one
            string known = job.page.ImagePrompt;
            Task<string> describe;
            if (!string.IsNullOrWhiteSpace(known)) describe = Task.FromResult(known);
            else
            {
                string people = People(job.mem);
                string text = job.page.Text ?? "";
                if (text.Length > 1800) text = text.Substring(0, 1800);
                string ask = (job.tpl ?? TPrompt).Format("people", people, "title", job.page.Title ?? "", "text", text);
                describe = LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", ask) }, wantJson: false, task: "image_prompt", maxTokensOverride: 300)
                    .ContinueWith(t =>
                    {
                        var r = t.IsFaulted ? null : t.Result;
                        if (r == null || !r.Ok || string.IsNullOrWhiteSpace(r.Text)) return null;
                        string p = r.Text.Trim().Trim('"', '`', '\'').Replace("\r", " ").Replace("\n", " ");
                        return p.Length > 600 ? p.Substring(0, 600) : p;
                    });
            }

            // Step 2 (background): the picture itself
            describe.ContinueWith(async t =>
            {
                string prompt = null;
                try { prompt = t.Status == TaskStatus.RanToCompletion ? t.Result : null; } catch { }
                if (string.IsNullOrWhiteSpace(prompt)) { Finish(false, null, null, "sin descripción de la escena"); return; }
                try
                {
                    string full = style.Length > 0 ? prompt.TrimEnd('.', ' ') + ". " + style : prompt;
                    byte[] raw = source == Free ? await FromPollinations(full).ConfigureAwait(false)
                               : source == OpenAi ? await FromOpenAi(full, openAi, openAiModel).ConfigureAwait(false)
                               : await FromLocal(full, localUrl).ConfigureAwait(false);
                    if (raw == null || raw.Length < 2000) { Finish(false, null, prompt, "el servicio no devolvió una imagen"); return; }
                    Directory.CreateDirectory(dir);
                    string file = (job.tpl == null ? "p_" : "t_") + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png";
                    SavePng(raw, Path.Combine(dir, file));
                    Finish(true, file, prompt, null);
                }
                catch (Exception ex) { Finish(false, null, prompt, ex.GetBaseException().Message); }
            });
        }

        /// <summary>Who appears, in plain visual terms, so the same people look alike from page to page.</summary>
        internal static string People(HeroMemory mem)
        {
            var sb = new StringBuilder();
            try
            {
                var h = Hero.Find(mem.HeroId);
                if (h != null)
                    sb.AppendLine($"- {h.Name}: {(h.IsFemale ? "woman" : "man")}, about {(int)h.Age}, {h.Culture?.Name} culture, {(h.IsLord ? "noble" : h.IsNotable ? "local notable" : h.IsWanderer ? "wanderer" : "commoner")}");
                var p = Hero.MainHero;
                if (p != null)
                    sb.AppendLine($"- {p.Name} (the other person in these memories): {(p.IsFemale ? "woman" : "man")}, about {(int)p.Age}, {p.Culture?.Name} culture, a travelling warrior");
            }
            catch { }
            return sb.Length == 0 ? "(two people of this land)" : sb.ToString().TrimEnd();
        }

        private static async Task<byte[]> Get(HttpRequestMessage req, int seconds)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds)))
            using (req)
            using (var resp = await LlmClient.HttpShared.SendAsync(req, cts.Token).ConfigureAwait(false))
            {
                if (!resp.IsSuccessStatusCode)
                {
                    string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                    throw new Exception($"HTTP {(int)resp.StatusCode} {(body.Length > 160 ? body.Substring(0, 160) : body)}");
                }
                return await resp.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            }
        }

        // Free and keyless. Only the scene description is sent, never names of the player or anything from the PC.
        private static Task<byte[]> FromPollinations(string prompt)
        {
            if (prompt.Length > 900) prompt = prompt.Substring(0, 900);
            string url = "https://image.pollinations.ai/prompt/" + Uri.EscapeDataString(prompt) + "?width=1024&height=576&nologo=true&seed=" + new Random().Next(1, 999999);
            return Get(new HttpRequestMessage(HttpMethod.Get, url), 120);
        }

        private static async Task<byte[]> FromOpenAi(string prompt, ProviderConfig p, string model)
        {
            if (p == null) throw new Exception("no hay ningún proveedor de OpenAI con clave en 0. AI Keys");
            var body = new JObject { ["model"] = model.Length > 0 ? model : "gpt-image-1", ["prompt"] = prompt, ["size"] = "1536x1024", ["n"] = 1 };
            var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/images/generations");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
            req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 180).ConfigureAwait(false)));
            var first = (j["data"] as JArray)?.FirstOrDefault();
            string b64 = (string)first?["b64_json"];
            if (!string.IsNullOrEmpty(b64)) return Convert.FromBase64String(b64);
            string link = (string)first?["url"];
            if (string.IsNullOrEmpty(link)) throw new Exception("respuesta sin imagen");
            return await Get(new HttpRequestMessage(HttpMethod.Get, link), 120).ConfigureAwait(false);
        }

        // AUTOMATIC1111 and compatible servers started with their API on
        private static async Task<byte[]> FromLocal(string prompt, string baseUrl)
        {
            if (baseUrl.Length == 0) baseUrl = "http://127.0.0.1:7860";
            var body = new JObject
            {
                ["prompt"] = prompt, ["negative_prompt"] = "text, letters, watermark, signature, modern objects, blurry, deformed hands",
                ["width"] = 1024, ["height"] = 576, ["steps"] = 24,
            };
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/sdapi/v1/txt2img") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 300).ConfigureAwait(false)));
            string b64 = (string)(j["images"] as JArray)?.FirstOrDefault();
            if (string.IsNullOrEmpty(b64)) throw new Exception("respuesta sin imagen");
            int comma = b64.IndexOf(',');
            return Convert.FromBase64String(comma > 0 && comma < 64 ? b64.Substring(comma + 1) : b64);
        }

        /// <summary>Whatever came back (JPEG, WebP is not supported, PNG) is stored as PNG, which the game can load.</summary>
        private static void SavePng(byte[] raw, string path)
        {
            using (var ms = new MemoryStream(raw))
            using (var img = System.Drawing.Image.FromStream(ms))
            using (var bmp = new System.Drawing.Bitmap(img))
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
}
