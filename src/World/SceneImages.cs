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
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

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
@"Turn this moment of a conversation into ONE prompt in English for a still from the scene. 50 to 90 words.
Describe only what would be seen: the two people face to face, their posture, gestures and expressions as the words suggest, and the place around them.
Use the facts below EXACTLY: each person's sex, age, build, the look of their people and what they are wearing, and the place, weather and light. Do not change or leave out any of them, and add no weather, season or landscape they do not state (no snow unless it says snow). No names.
The world is Calradia, a medieval land with 12th-century technology: no magic, no modern objects. No writing or letters in the picture.
The people in it:
{people}
Where and when: {title}
What was just said:
{text}
Answer with the prompt only, nothing else.");

        private static readonly Tpl TEvent = new Tpl("images/world_event.txt",
@"Turn this world event into ONE prompt in English for a wide picture of it, 2:1 landscape, like a photograph. 60 to 100 words.
Show one scene that tells the event: its subject, what happens, the place and the scale, as the description says. Describe only what would be seen.
The world is Calradia, a land of the 11th to 13th century: no magic, no firearms, no modern objects. Its peoples look like this: Vlandia, Norman knights in green farmland with stone castles; Sturgia, Rus and Norse folk of snowy northern forests in furs; Aserai, Arab desert and oasis towns; Battania, Celtic clans of forested hills and wooden hillforts; Khuzait, Mongol steppe riders and felt yurts; the Empire, Byzantine Roman legions and marble towns. Medieval violence may be shown when the event calls for it.
Use the banner colours given for each realm on the surcoats, cloaks, shields and banners of its people. A realm that does not fit the scene can be left out.
The people in it, only if the scene needs them (no names in the picture):
{people}
Headline: {title}
{text}
No writing or letters in the picture. Answer with the prompt only, nothing else.");

        // For world events: wide, like a photograph
        private const string EventStyle = "Photorealistic wide shot, like a photograph, natural light, worn metal, leather, wool, wood and stone, historically grounded medieval setting. No text, no watermark, no modern objects.";

        private static readonly Queue<(HeroMemory mem, MemoryPage page, Action done, Tpl tpl, string people, bool wide)> Pending = new Queue<(HeroMemory, MemoryPage, Action, Tpl, string, bool)>();

        /// <summary>
        /// A wide picture of a new world event, for its news card. Queued behind any other picture. <paramref name="done"/>
        /// gets the file name on the main thread, or null if it could not be made.
        /// </summary>
        public static bool RequestEvent(string title, string text, string people, Action<string> done)
        {
            if (!Enabled || Dir == null || string.IsNullOrWhiteSpace(text)) return false;
            var page = new MemoryPage { Title = title ?? "", Text = text, Day = CampaignTime.Now.ToDays };
            Pending.Enqueue((null, page, () => done?.Invoke(page.Image), TEvent, people, true));
            Next();
            return true;
        }

        /// <summary>
        /// Who an event picture may show: the rulers and heads of house of its realms that the text names, and the player
        /// if named, up to four, each as an image model needs them.
        /// </summary>
        internal static string EventPeople(string text, List<Kingdom> realms)
        {
            var found = new List<Hero>();
            try
            {
                string t = text ?? "";
                var pool = realms.Select(k => k.Leader).Concat(realms.SelectMany(k => k.Clans.Where(c => !c.IsEliminated).Select(c => c.Leader)))
                                 .Where(h => h != null && h.IsAlive).Distinct().ToList();
                if (Hero.MainHero != null) pool.Add(Hero.MainHero);
                foreach (var h in pool)
                {
                    if (found.Count >= 4) break;
                    string name = h.FirstName?.ToString() ?? h.Name?.ToString() ?? "";
                    if (name.Length >= 3 && !found.Contains(h) && System.Text.RegularExpressions.Regex.IsMatch(t, @"\b" + System.Text.RegularExpressions.Regex.Escape(name) + @"\b", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                        found.Add(h);
                }
            }
            catch { }
            return found.Count == 0 ? "(nobody in particular: figures of this land, as the scene needs)" : string.Join("\n", found.Select(h => "- " + Look(h, false)));
        }

        /// <summary>
        /// A picture of the moment just spoken in a conversation. Skipped while another picture is being made, so a quick
        /// talk does not pile up requests. <paramref name="shown"/> gets the file path on the main thread when it is ready.
        /// </summary>
        public static bool RequestMoment(HeroMemory mem, string where, string exchange, Action<string> shown)
        {
            if (!Enabled || mem == null || Dir == null || string.IsNullOrWhiteSpace(exchange)) return false;
            if (IsBusy) { Log.Info("Conversation image: another is already in progress, this reply gets no image"); return false; }
            var page = new MemoryPage { Title = where ?? "", Text = exchange, Day = CampaignTime.Now.ToDays };
            Pending.Enqueue((mem, page, () => { string path = PathOf(page); if (path != null) shown?.Invoke(path); }, TMoment, null, false));
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
            Pending.Enqueue((mem, page, done, null, null, false));
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
                        Log.Info($"Image {(job.wide ? "of the event" : job.tpl == null ? "of the page" : "of the conversation in")} «{job.page.Title}»: {file}");
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
                string people = job.people ?? People(job.mem);
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
                    string full;
                    // A conversation picture: the facts of the place go again after the description, so the model
                    // cannot drop them, and it is drawn as a film still whatever the style for memory pages is
                    if (job.wide) full = prompt.TrimEnd('.', ' ') + ". " + EventStyle;
                    else if (job.tpl != null) full = prompt.TrimEnd('.', ' ') + ". Setting: " + (job.page.Title ?? "") + ". " + MomentStyle;
                    else full = style.Length > 0 ? prompt.TrimEnd('.', ' ') + ". " + style : prompt;
                    byte[] raw = source == Free ? await FromPollinations(full, SeedFor(job.mem), job.wide).ConfigureAwait(false)
                               : source == OpenAi ? await FromOpenAi(full, openAi, openAiModel).ConfigureAwait(false)
                               : await FromLocal(full, localUrl, job.wide).ConfigureAwait(false);
                    if (raw == null || raw.Length < 2000) { Finish(false, null, prompt, "el servicio no devolvió una imagen"); return; }
                    Directory.CreateDirectory(dir);
                    string file = (job.wide ? "e_" : job.tpl == null ? "p_" : "t_") + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png";
                    SavePng(raw, Path.Combine(dir, file), job.wide ? 2f : 0f);
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
                bool civilian = Settlement.CurrentSettlement != null;
                var h = Hero.Find(mem.HeroId);
                if (h != null) sb.AppendLine("- " + Look(h, civilian));
                var p = Hero.MainHero;
                if (p != null) sb.AppendLine("- " + Look(p, civilian) + " (the other person)");
            }
            catch { }
            return sb.Length == 0 ? "(two people of this land)" : sb.ToString().TrimEnd();
        }

        /// <summary>
        /// The cultures of Calradia by the real peoples they are drawn from: an image model knows what a Bedouin or a
        /// Byzantine looks like, not what "Aserai" means. Keyed by the culture's id, never its translated name.
        /// </summary>
        private static string CultureLook(string id)
        {
            switch ((id ?? "").ToLowerInvariant())
            {
                case "aserai": return "medieval Arab Bedouin look, sun-darkened olive-brown skin, dark hair, headwrap or turban, layered desert robes";
                case "empire": return "Byzantine Roman look, Mediterranean features, olive skin, Eastern Roman tunics and cloaks";
                case "vlandia": return "Norman and Frankish look, fair to ruddy European skin, Western European medieval dress";
                case "sturgia": return "Kievan Rus and Norse look, pale skin, light or brown hair, fur-trimmed Northern European dress";
                case "battania": return "Gaelic Celtic look, pale freckled skin, red or brown hair, plaid wool and leather";
                case "khuzait": return "Mongol and Cuman steppe nomad look, East Asian features, steppe deels and fur hats";
                case "nord": case "nords": return "Viking Norse look, pale skin, fair hair, wool tunics and furs";
                default: return "medieval European look";
            }
        }

        /// <summary>One person as an image model needs them: sex, age band, build, the real look of their people and what they wear right now.</summary>
        private static string Look(Hero h, bool civilian)
        {
            string age = h.Age < 18 ? "a youth" : h.Age < 30 ? "young" : h.Age < 45 ? "in their prime" : h.Age < 60 ? "middle-aged" : "old";
            string build = "";
            try { build = h.Weight > 0.66f ? ", heavy-set" : h.Weight < 0.33f ? ", lean" : ""; if (h.Build > 0.66f) build += ", strongly built"; } catch { }
            string station = h.IsLord ? (h.Clan?.Leader == h ? "a noble head of a house" : "a noble") : h.IsNotable ? "a local notable" : h.IsWanderer ? "a wandering adventurer" : "a commoner";
            if (h == Hero.MainHero) station = "a travelling warrior";
            return $"{(h.IsFemale ? "woman" : "man")}, {age} (about {(int)h.Age}){build}, {station}; {CultureLook(h.Culture?.StringId)}; wearing {Clothes(h, civilian)}";
        }

        /// <summary>What they actually have on, from their equipment: item ids read like English ("desert_robe", "lamellar_armor").</summary>
        private static string Clothes(Hero h, bool civilian)
        {
            try
            {
                var eq = civilian ? h.CivilianEquipment : h.BattleEquipment;
                if (eq == null) return "clothes of their people";
                var parts = new List<string>();
                foreach (var slot in new[] { EquipmentIndex.Head, EquipmentIndex.Body, EquipmentIndex.Cape, EquipmentIndex.Gloves, EquipmentIndex.Leg })
                {
                    var item = eq[slot].Item;
                    if (item == null) continue;
                    string words = System.Text.RegularExpressions.Regex.Replace(item.StringId ?? "", @"[_\d]+", " ").Trim();
                    if (words.Length > 2) parts.Add(words);
                }
                return parts.Count == 0 ? "plain clothes of their people" : string.Join(", ", parts);
            }
            catch { return "clothes of their people"; }
        }

        /// <summary>
        /// Where and when, from the game itself: the settlement or the real terrain under the party, the map weather and
        /// the hour. Snow is only named when the ground or the sky really has it, so a southern summer steppe stays green.
        /// </summary>
        public static string Setting()
        {
            var sb = new StringBuilder();
            try
            {
                var st = Settlement.CurrentSettlement;
                var party = MobileParty.MainParty;
                if (st != null)
                {
                    string kind = st.IsTown ? "town" : st.IsCastle ? "castle" : st.IsVillage ? "village" : "settlement";
                    sb.Append($"in the {kind} of {st.Name}, built in the style of the {CultureLook(st.Culture?.StringId).Split(',')[0].Replace(" look", "")} world");
                }
                else
                {
                    string terrain = "open country";
                    try { if (party != null) terrain = Campaign.Current.MapSceneWrapper.GetFaceTerrainType(party.CurrentNavigationFace).ToString().ToLowerInvariant(); } catch { }
                    if (party != null && party.IsCurrentlyAtSea) terrain = "the deck of a ship at sea";
                    sb.Append($"outdoors on {terrain} ground");
                }
                bool snow = false;
                string sky = "clear or lightly clouded sky";
                try
                {
                    if (party != null)
                        switch (Campaign.Current.Models.MapWeatherModel.GetWeatherEventInPosition(party.GetPosition2D))
                        {
                            case MapWeatherModel.WeatherEvent.LightRain: sky = "light rain"; break;
                            case MapWeatherModel.WeatherEvent.HeavyRain: sky = "heavy rain"; break;
                            case MapWeatherModel.WeatherEvent.Storm: sky = "a storm"; break;
                            case MapWeatherModel.WeatherEvent.Snowy: sky = "falling snow"; snow = true; break;
                            case MapWeatherModel.WeatherEvent.Blizzard: sky = "a blizzard"; snow = true; break;
                        }
                    if (sb.ToString().Contains("snow")) snow = true;
                }
                catch { }
                int hour = CampaignTime.Now.GetHourOfDay;
                string light = hour < 5 ? "night, moonlight or torchlight" : hour < 8 ? "dawn" : hour < 11 ? "morning light" : hour < 15 ? "midday sun" : hour < 18 ? "afternoon light" : hour < 21 ? "dusk, evening light" : "night, torchlight";
                sb.Append($", {sky}, {light}");
                if (!snow) sb.Append(", no snow anywhere");
            }
            catch { }
            return sb.Length == 0 ? "a place in Calradia" : sb.ToString();
        }

        // For conversation pictures: a still from a film, not a painting
        private const string MomentStyle = "Cinematic photorealistic film still, natural light, 35mm lens, detailed faces, historically grounded medieval setting. No text, no watermark, no modern objects.";

        /// <summary>The same seed for the same person, so their pictures stay alike from one to the next.</summary>
        private static int SeedFor(HeroMemory mem) => Math.Abs((mem?.HeroId ?? "x").GetHashCode() % 999983) + 1;

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
        private static Task<byte[]> FromPollinations(string prompt, int seed, bool wide = false)
        {
            if (prompt.Length > 1400) prompt = prompt.Substring(0, 1400);
            string url = "https://image.pollinations.ai/prompt/" + Uri.EscapeDataString(prompt)
                         + (wide ? "?width=1280&height=640" : "?width=1280&height=720") + "&model=flux&enhance=false&nologo=true&private=true&seed=" + seed;
            var req = new HttpRequestMessage(HttpMethod.Get, url);
            // With the player's own free Pollinations token the watermark goes and the queue is shorter
            string token = (S.Get.ImagePollinationsToken ?? "").Trim();
            if (token.Length > 0) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + token);
            return Get(req, 150);
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
        private static async Task<byte[]> FromLocal(string prompt, string baseUrl, bool wide = false)
        {
            if (baseUrl.Length == 0) baseUrl = "http://127.0.0.1:7860";
            var body = new JObject
            {
                ["prompt"] = prompt, ["negative_prompt"] = "text, letters, watermark, signature, modern objects, blurry, deformed hands",
                ["width"] = 1024, ["height"] = wide ? 512 : 576, ["steps"] = 24,
            };
            var req = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/sdapi/v1/txt2img") { Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json") };
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 300).ConfigureAwait(false)));
            string b64 = (string)(j["images"] as JArray)?.FirstOrDefault();
            if (string.IsNullOrEmpty(b64)) throw new Exception("respuesta sin imagen");
            int comma = b64.IndexOf(',');
            return Convert.FromBase64String(comma > 0 && comma < 64 ? b64.Substring(comma + 1) : b64);
        }

        /// <summary>
        /// Whatever came back (JPEG, WebP is not supported, PNG) is stored as PNG, which the game can load. With an
        /// <paramref name="aspect"/> (width / height) it is cut from the middle to that shape first, so a 3:2 picture fills a 2:1 frame.
        /// </summary>
        private static void SavePng(byte[] raw, string path, float aspect = 0f)
        {
            using (var ms = new MemoryStream(raw))
            using (var img = System.Drawing.Image.FromStream(ms))
            {
                int w = img.Width, h = img.Height;
                var cut = new System.Drawing.Rectangle(0, 0, w, h);
                if (aspect > 0f && w > 0 && h > 0)
                {
                    if (w / (float)h > aspect) { int cw = (int)Math.Round(h * aspect); cut = new System.Drawing.Rectangle((w - cw) / 2, 0, cw, h); }
                    else { int ch = (int)Math.Round(w / aspect); cut = new System.Drawing.Rectangle(0, (h - ch) / 2, w, ch); }
                }
                using (var bmp = new System.Drawing.Bitmap(cut.Width, cut.Height))
                {
                    using (var g = System.Drawing.Graphics.FromImage(bmp))
                        g.DrawImage(img, new System.Drawing.Rectangle(0, 0, cut.Width, cut.Height), cut, System.Drawing.GraphicsUnit.Pixel);
                    bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
                }
            }
        }
    }
}
