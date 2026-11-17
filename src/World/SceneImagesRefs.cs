using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
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
    /// Pictures that look like the characters: the game draws each person (face sheet and whole figure, see
    /// CharacterCapture) and those drawings go to an image model that takes reference images (Google Gemini or OpenAI,
    /// with the player's own key), with a list saying which image is whom and the order to copy faces and clothes. The
    /// last picture made with the same character goes last, for continuity. Each kind of picture (conversation, memory
    /// book, world event) has its own service in the settings.
    /// </summary>
    internal static partial class SceneImages
    {
        public const int Gemini = 4;
        public enum Kind { Book, Talk, Event }

        public static int SourceOf(Kind k)
        {
            try
            {
                var cfg = S.Get;
                var d = k == Kind.Talk ? cfg.TalkImageSource : k == Kind.Event ? cfg.EventImageSource : cfg.ImageSource;
                return Math.Max(0, Math.Min(Gemini, d.SelectedIndex));
            }
            catch { return Off; }
        }

        public static bool EnabledFor(Kind k) => SourceOf(k) != Off;
        public static bool TalkEnabled => EnabledFor(Kind.Talk);

        /// <summary>Services that take reference pictures of the characters.</summary>
        internal static bool TakesReferences(int source) => source == Gemini || source == OpenAi;

        internal static ProviderConfig ProviderFor(string host)
        {
            try { return LegacyConfig.Current.Providers.FirstOrDefault(p => p.IsUsable && (p.BaseUrl ?? "").IndexOf(host, StringComparison.OrdinalIgnoreCase) >= 0); }
            catch { return null; }
        }

        internal static ProviderConfig GeminiProvider => ProviderFor("generativelanguage.googleapis.com");
        internal static ProviderConfig OpenAiProvider => ProviderFor("api.openai.com");

        // ============================================================ the people in it, for the model

        /// <summary>A reference picture and what it is.</summary>
        internal sealed class Reference { public byte[] Jpeg; public string Line; }

        /// <summary>Face sheet and figure of each person, in order, each labelled with whom it shows.</summary>
        internal static async Task<List<Reference>> ReferencesOf(List<CharacterCapture.Look> looks)
        {
            var refs = new List<Reference>();
            var tasks = looks.Where(l => l != null).Select(l => (look: l, task: CharacterCapture.Get(l, 20000))).ToList();
            foreach (var t in tasks)
            {
                CharacterCapture.Pictures p;
                try { p = await t.task.ConfigureAwait(false); } catch { continue; }
                int faceNo = 0;
                if (p.Face != null)
                {
                    refs.Add(new Reference { Jpeg = CharacterCapture.Jpeg(p.Face), Line = $"{t.look.Name} [id: {t.look.Id}] — FACE: the same head seen from the front and from both three-quarter sides, helmet off. It is the truth about their face, age, hair, beard, skin tone and scars; it does not show their whole outfit." });
                    faceNo = refs.Count;
                }
                if (p.Figure != null)
                    refs.Add(new Reference { Jpeg = CharacterCapture.Jpeg(p.Figure), Line = $"{t.look.Name} [id: {t.look.Id}] — WHOLE BODY: the truth about their build, clothing, armour and gear" + (faceNo > 0 ? $"; the same person as image {faceNo}." : ".") });
            }
            return refs;
        }

        /// <summary>The list of what is attached and the rule that makes the model keep everyone as they are.</summary>
        internal static string Manifest(List<Reference> refs, string previousLine = null)
        {
            var sb = new StringBuilder();
            if (refs.Count == 0 && previousLine == null)
                return "No reference images are attached: draw the people from the description alone.";
            sb.AppendLine("ATTACHED REFERENCE IMAGES (these are all the images attached, in this order):");
            int n = 0;
            foreach (var r in refs) sb.AppendLine($"Image {++n} = {r.Line}");
            if (previousLine != null) sb.AppendLine($"Image {++n} = {previousLine}");
            sb.Append("Every person shown who has reference images must be recognisably that person: their own face, apparent age, hairstyle, facial hair, skin tone and the outfit they wear in their whole-body image. Copy what you see; do not invent other faces, clothes or gear for them, and do not swap features between people. Match each image only to its id and name: two people may share a name and still be different. Never write ids, names or labels in the picture.");
            return sb.ToString();
        }

        internal static string AgeOf(Hero h)
        {
            if (h == null) return "";
            int a = (int)h.Age;
            string band = a < 18 ? "a youth" : a < 25 ? "young" : a < 35 ? "a young adult" : a < 50 ? "middle-aged" : a < 65 ? "older" : "elderly";
            return $"{a} years old ({band})";
        }

        // ============================================================ the picture of a conversation

        private static readonly Tpl TTalk = new Tpl("images/talk_scene.txt",
@"A photorealistic still of a scene in Calradia, the medieval world of Mount & Blade II: Bannerlord, in the 11th to 13th century: real history, no fantasy and no magic.

{references}

CONTINUITY
{continuity}

WHERE AND WHEN
{setting}. Season: {season}. Time of day: {time}.

WHO IS IN IT
- {npc_name}, {npc_age}.
- {player_name}, {player_age}{player_alias}.
Both are in the picture, near each other, as people in the middle of a conversation.

WHAT IS HAPPENING (this decides the moment: who does what, their gestures, posture, faces and how close they stand; never a neutral pose)
{lines}

HOW IT LOOKS
Cinematic and photorealistic, a frame of a historical film: real skin, worn cloth, leather and metal, light that fits the hour and the weather. Wide 16:9 landscape framing, both people clearly seen from at least the waist up, the place around them readable. One frozen instant. Everything physically possible: nobody floats, nothing passes through anything, each person has two arms and two legs, weapons and objects are held the right way. Clothes, armour and gear exactly as in the reference images. No text, letters, captions, labels or interface anywhere in the picture.{child_rule}");

        public static void TouchRefs() { var _ = TTalk.Key; }

        // the last picture made with each character, for continuity: file and the campaign hour it was made
        private static readonly Dictionary<string, (string file, double hour)> LastWith = new Dictionary<string, (string, double)>();
        private static bool _talkBusy;
        private static int _talkGen;

        public static void ResetRefs() { LastWith.Clear(); _talkBusy = false; _talkGen++; }

        /// <summary>
        /// The picture of the moment just spoken in a conversation, with both people drawn by the game as references (when
        /// the chosen service takes them) and the last picture with this character for continuity. <paramref name="lines"/>
        /// are the last lines of the talk, the reply last. False if no picture was started.
        /// </summary>
        public static bool RequestTalk(Hero npc, string npcName, string lines, Action<string> shown, Action<string> failed = null, CharacterCapture.Look npcLook = null)
        {
            int source = SourceOf(Kind.Talk);
            if (source == Off || Dir == null || string.IsNullOrWhiteSpace(lines)) return false;
            if (_talkBusy) { Log.Info("Conversation image: another is already being made, this reply gets none"); return false; }
            _talkBusy = true;
            int gen = _talkGen, session = WorldSim.Session;
            var player = Hero.MainHero;
            var looks = TakesReferences(source) ? new List<CharacterCapture.Look> { npcLook ?? CharacterCapture.LookOf(npc), CharacterCapture.LookOf(player) } : new List<CharacterCapture.Look>();
            string key = npc?.StringId ?? npcName ?? "";
            string prevFile = null; double prevHours = 0;
            if (LastWith.TryGetValue(key, out var last) && File.Exists(last.file)) { prevFile = last.file; prevHours = CampaignTime.Now.ToHours - last.hour; }
            string setting = Setting(), season = SeasonName(), time = TimeOfDay();
            string playerName = player?.Name?.ToString() ?? "the traveller";
            bool child = (npc != null && npc.Age < 18) || (player != null && player.Age < 18);
            string dir = Dir;

            Task.Run(async () =>
            {
                string file = null, error = null;
                try
                {
                    var refs = TakesReferences(source) ? await ReferencesOf(looks).ConfigureAwait(false) : new List<Reference>();
                    string prevLine = null;
                    if (prevFile != null && TakesReferences(source))
                    {
                        try
                        {
                            refs.Add(new Reference { Jpeg = CharacterCapture.Jpeg(File.ReadAllBytes(prevFile), 90) });
                            prevLine = $"PREVIOUS SCENE with {npcName}, made {Ago(prevHours)} of game time: only for continuity of place, light and framing; the character images above always win on faces, age and clothes.";
                        }
                        catch { }
                    }
                    var charRefs = prevLine != null ? refs.Take(refs.Count - 1).ToList() : refs;
                    string continuity = prevLine == null ? "This is the first picture of these two together."
                        : prevHours < 3 ? "Little time has passed since the previous scene: keep the same place, light and framing unless the words say they moved."
                        : "Time has passed since the previous scene: keep the people consistent; place and light follow the present.";
                    string prompt = TTalk.Format("references", Manifest(charRefs, prevLine), "continuity", continuity, "setting", setting, "season", season, "time", time,
                        "npc_name", npcName ?? "the other person", "npc_age", AgeOf(npc), "player_name", playerName, "player_age", AgeOf(player),
                        "player_alias", "", "lines", lines.Trim(),
                        "child_rule", child ? " Someone in the scene is a child: the picture stays wholly innocent, nothing violent, cruel or sexual near them." : "");
                    byte[] raw = await Draw(source, prompt, refs.Select(r => r.Jpeg).ToList(), false, NoSnowFor(setting)).ConfigureAwait(false);
                    if (raw == null || raw.Length < 2000) error = "the service returned no picture";
                    else
                    {
                        Directory.CreateDirectory(dir);
                        file = Path.Combine(dir, "t_" + Guid.NewGuid().ToString("N").Substring(0, 12) + ".png");
                        SavePng(raw, file, 0f);
                    }
                }
                catch (Exception ex) { error = ex.GetBaseException().Message; }
                try
                {
                MainThread.Post(() =>
                {
                    if (gen != _talkGen) return;
                    _talkBusy = false;
                    if (session != WorldSim.Session) return;
                    if (file == null) { Log.Warn("Conversation image: failed (" + error + ")"); failed?.Invoke(ShortError(error)); return; }
                    LastWith[key] = (file, CampaignTime.Now.ToHours);
                    Log.Info($"Conversation image with {npcName}: {Path.GetFileName(file)}");
                    try { shown?.Invoke(file); } catch (Exception ex) { Log.Error("Conversation image: shown", ex); }
                });
                }
                catch { if (gen == _talkGen) _talkBusy = false; }
            });
            return true;
        }

        private static string Ago(double hours)
        {
            if (hours < 0.5) return "just now";
            if (hours < 1) return $"{(int)(hours * 60)} minutes ago";
            if (hours < 24) return $"{hours:0.#} hours ago";
            double days = hours / 24;
            return days < 7 ? $"{days:0.#} days ago" : $"{days:0} days ago ({(int)(days / 7)} weeks)";
        }

        private static string SeasonName() { try { return Season(); } catch { return "summer"; } }
        public static string SeasonNow => SeasonName();

        private static string TimeOfDay()
        {
            int h = CampaignTime.Now.GetHourOfDay;
            return h < 5 ? "night" : h < 11 ? "morning" : h < 17 ? "daytime" : h < 21 ? "evening" : "night";
        }

        private static bool NoSnowFor(string setting) => !Snowy(setting);

        // ============================================================ models that exist in the player's account

        private static readonly Dictionary<string, string> FoundModel = new Dictionary<string, string>();

        /// <summary>The service said the model does not exist (or is not offered for this kind of request).</summary>
        internal static bool ModelMissing(Exception ex)
        {
            string m = ex?.GetBaseException().Message ?? "";
            return m.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
                   || m.IndexOf("HTTP 404", StringComparison.OrdinalIgnoreCase) >= 0 || m.IndexOf("model_not_found", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        /// <summary>
        /// The models this Google key can use for pictures (generateContent with "image" in the name) or for video (Veo,
        /// predictLongRunning), the cheaper "flash"/"fast" ones first, the newest next. Null if none.
        /// </summary>
        internal static async Task<string> GeminiModelFor(string key, bool video)
        {
            string tag = video ? "gemini-video" : "gemini-image";
            if (FoundModel.TryGetValue(tag, out var have)) return have;
            var req = new HttpRequestMessage(HttpMethod.Get, "https://generativelanguage.googleapis.com/v1beta/models?pageSize=1000");
            req.Headers.TryAddWithoutValidation("x-goog-api-key", key.Trim());
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 30).ConfigureAwait(false)));
            var names = new List<string>();
            foreach (var m in (j["models"] as JArray) ?? new JArray())
            {
                if (!(m is JObject)) continue;
                string name = ((string)m["name"] ?? "").Replace("models/", "");
                var methods = ((m["supportedGenerationMethods"] as JArray) ?? new JArray()).Select(x => (string)x).ToList();
                if (video ? name.Contains("veo") && methods.Contains("predictLongRunning") : name.Contains("image") && !name.StartsWith("imagen") && methods.Contains("generateContent"))
                    names.Add(name);
            }
            string pick = names.OrderByDescending(n => n.Contains(video ? "fast" : "flash")).ThenByDescending(n => n, StringComparer.Ordinal).FirstOrDefault();
            if (pick != null) FoundModel[tag] = pick;
            Log.Info($"Google models for {(video ? "video" : "pictures")} on this key: {(names.Count == 0 ? "none" : string.Join(", ", names))}; using {pick ?? "none"}");
            return pick;
        }

        /// <summary>The image models this OpenAI key can use (gpt-image…), the newest first, the full one before "mini". Null if none.</summary>
        internal static async Task<string> OpenAiImageModel(ProviderConfig p)
        {
            if (FoundModel.TryGetValue("openai-image", out var have)) return have;
            var req = new HttpRequestMessage(HttpMethod.Get, p.BaseUrl.TrimEnd('/') + "/models");
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 30).ConfigureAwait(false)));
            var ids = ((j["data"] as JArray) ?? new JArray()).Select(x => x is JObject o ? (string)o["id"] ?? "" : "").Where(x => x.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)).ToList();
            string pick = ids.OrderBy(x => x.Contains("mini")).ThenByDescending(x => x, StringComparer.Ordinal).FirstOrDefault();
            if (pick != null) FoundModel["openai-image"] = pick;
            Log.Info($"OpenAI image models on this key: {(ids.Count == 0 ? "none" : string.Join(", ", ids))}; using {pick ?? "none"}");
            return pick;
        }

        /// <summary>A service's error in one short line for the screen (the whole answer stays in the log).</summary>
        internal static string ShortError(string raw)
        {
            raw = raw ?? "";
            if (raw.Contains("429") && (raw.IndexOf("quota", StringComparison.OrdinalIgnoreCase) >= 0 || raw.Contains("RESOURCE_EXHAUSTED")))
                return L.T("AIL_e7b1df48", "sin cupo en tu cuenta: este modelo no es gratuito o has llegado al límite");
            if (raw.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0 || raw.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0)
                return L.T("AIL_e7b1df49", "el modelo no existe en tu cuenta");
            if (raw.Contains("HTTP 401") || raw.Contains("HTTP 403")) return L.T("AIL_e7b1df4a", "la clave no es válida o no tiene permiso para esto");
            if (raw.Contains("HTTP 503") || raw.IndexOf("high demand", StringComparison.OrdinalIgnoreCase) >= 0) return L.T("AIL_e7b1df4b", "el servicio está saturado; prueba otra vez en un rato");
            var m = System.Text.RegularExpressions.Regex.Match(raw, "\"message\"\\s*:\\s*\"([^\"]+)");
            string t = m.Success ? m.Groups[1].Value : raw;
            t = System.Text.RegularExpressions.Regex.Replace(t, "\\s+", " ").Trim();
            return t.Length > 120 ? t.Substring(0, 120) + "…" : t;
        }

        // ============================================================ the services

        /// <summary>Draws with the chosen service; if Gemini or OpenAI fails and the other is set up, tries it (setting).</summary>
        internal static async Task<byte[]> Draw(int source, string prompt, List<byte[]> refs, bool wide, bool noSnow)
        {
            try { return await DrawWith(source, prompt, refs, wide, noSnow).ConfigureAwait(false); }
            catch (Exception ex)
            {
                int other = source == Gemini ? OpenAi : source == OpenAi ? Gemini : -1;
                bool can = other == Gemini ? GeminiProvider != null : other == OpenAi && OpenAiProvider != null;
                if (other < 0 || !can || !S.Get.ImageTryOtherService) throw;
                Log.Warn($"Image: {SourceNames[source]} failed ({ex.GetBaseException().Message}); trying {SourceNames[other]}");
                return await DrawWith(other, prompt, refs, wide, noSnow).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> DrawWith(int source, string prompt, List<byte[]> refs, bool wide, bool noSnow)
        {
            refs = refs ?? new List<byte[]>();
            switch (source)
            {
                case Gemini: return await FromGemini(prompt, refs, wide).ConfigureAwait(false);
                case OpenAi:
                    return refs.Count > 0 ? await FromOpenAiEdit(prompt, refs).ConfigureAwait(false)
                                          : await FromOpenAi(prompt, OpenAiProvider, (S.Get.ImageOpenAiModel ?? "").Trim()).ConfigureAwait(false);
                case Free: return await FromPollinations(prompt, 1 + Math.Abs(prompt.GetHashCode() % 99991), wide).ConfigureAwait(false);
                case Local: return await FromLocal(prompt, (S.Get.ImageLocalUrl ?? "").Trim().TrimEnd('/'), wide, !noSnow).ConfigureAwait(false);
                default: throw new Exception("no picture service");
            }
        }

        /// <summary>Google Gemini image model: the prompt, then each reference after its number, an image back.</summary>
        private static async Task<byte[]> FromGemini(string prompt, List<byte[]> refs, bool wide)
        {
            var p = GeminiProvider;
            if (p == null) throw new Exception("there is no Google Gemini provider with a key in 0. AI Keys");
            string model = FoundModel.TryGetValue("gemini-image", out var found) ? found : (S.Get.ImageGeminiModel ?? "").Trim();
            if (model.Length == 0) model = "gemini-2.5-flash-image";
            try { return await GeminiDraw(p, model, prompt, refs, wide).ConfigureAwait(false); }
            catch (Exception ex) when (ModelMissing(ex))
            {
                string other = await GeminiModelFor(p.ApiKey, false).ConfigureAwait(false);
                if (other == null || other == model) throw;
                Log.Warn($"Gemini: the model {model} is not in this account; using {other}");
                return await GeminiDraw(p, other, prompt, refs, wide).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> GeminiDraw(ProviderConfig p, string model, string prompt, List<byte[]> refs, bool wide)
        {
            var parts = new JArray { new JObject { ["text"] = prompt } };
            for (int i = 0; i < refs.Count; i++)
            {
                parts.Add(new JObject { ["text"] = $"Image {i + 1}:" });
                parts.Add(new JObject { ["inline_data"] = new JObject { ["mime_type"] = "image/jpeg", ["data"] = Convert.ToBase64String(refs[i]) } });
            }
            var body = new JObject
            {
                ["contents"] = new JArray { new JObject { ["role"] = "user", ["parts"] = parts } },
                ["generationConfig"] = new JObject
                {
                    ["responseModalities"] = new JArray { "IMAGE", "TEXT" },
                    ["imageConfig"] = new JObject { ["aspectRatio"] = wide ? "21:9" : "16:9" },
                },
            };
            var req = new HttpRequestMessage(HttpMethod.Post, $"https://generativelanguage.googleapis.com/v1beta/models/{Uri.EscapeDataString(model)}:generateContent");
            req.Headers.TryAddWithoutValidation("x-goog-api-key", p.ApiKey.Trim());
            req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 240).ConfigureAwait(false)));
            foreach (var cand in (j["candidates"] as JArray) ?? new JArray())
                foreach (var part in (cand["content"]?["parts"] as JArray) ?? new JArray())
                {
                    string data = (string)(part["inlineData"]?["data"] ?? part["inline_data"]?["data"]);
                    if (!string.IsNullOrEmpty(data)) return Convert.FromBase64String(data);
                }
            string why = (string)j["promptFeedback"]?["blockReason"] ?? (string)(j["candidates"]?[0]?["finishReason"]) ?? "no image in the answer";
            throw new Exception("Gemini: " + why);
        }

        /// <summary>OpenAI image edits: the references as images, the prompt, faces kept with high fidelity.</summary>
        private static async Task<byte[]> FromOpenAiEdit(string prompt, List<byte[]> refs)
        {
            var p = OpenAiProvider;
            if (p == null) throw new Exception("there is no OpenAI provider with a key in 0. AI Keys");
            string model = FoundModel.TryGetValue("openai-image", out var found) ? found : (S.Get.ImageOpenAiModel ?? "").Trim();
            if (model.Length == 0) model = "gpt-image-1";
            try { return await OpenAiEditWith(p, model, prompt, refs).ConfigureAwait(false); }
            catch (Exception ex) when (ModelMissing(ex) || (ex.GetBaseException().Message ?? "").Contains("invalid_value"))
            {
                string other = await OpenAiImageModel(p).ConfigureAwait(false);
                if (other == null || other == model) throw;
                Log.Warn($"OpenAI: the model {model} is not in this account; using {other}");
                return await OpenAiEditWith(p, other, prompt, refs).ConfigureAwait(false);
            }
        }

        private static async Task<byte[]> OpenAiEditWith(ProviderConfig p, string model, string prompt, List<byte[]> refs)
        {
            var form = new MultipartFormDataContent();
            form.Add(new StringContent(model), "model");
            form.Add(new StringContent(prompt.Length > 30000 ? prompt.Substring(0, 30000) : prompt), "prompt");
            form.Add(new StringContent("1536x1024"), "size");
            form.Add(new StringContent("1"), "n");
            if (model.StartsWith("gpt-image", StringComparison.OrdinalIgnoreCase)) form.Add(new StringContent("high"), "input_fidelity");
            for (int i = 0; i < refs.Count && i < 16; i++)
            {
                var c = new ByteArrayContent(refs[i]);
                c.Headers.ContentType = new MediaTypeHeaderValue("image/jpeg");
                form.Add(c, "image[]", $"ref{i + 1}.jpg");
            }
            var req = new HttpRequestMessage(HttpMethod.Post, p.BaseUrl.TrimEnd('/') + "/images/edits") { Content = form };
            req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + p.ApiKey.Trim());
            var j = JObject.Parse(Encoding.UTF8.GetString(await Get(req, 300).ConfigureAwait(false)));
            var first = (j["data"] as JArray)?.FirstOrDefault();
            string b64 = (string)first?["b64_json"];
            if (!string.IsNullOrEmpty(b64)) return Convert.FromBase64String(b64);
            string link = (string)first?["url"];
            if (string.IsNullOrEmpty(link)) throw new Exception("OpenAI: no image in the answer");
            return await Get(new HttpRequestMessage(HttpMethod.Get, link), 120).ConfigureAwait(false);
        }
    }
}
