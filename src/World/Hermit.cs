using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using AILegacy.Config;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>What the hermit remembers of the player, kept in each save (and mirrored in save_data\&lt;campaign&gt;\hermit_context.json).</summary>
    internal sealed class HermitContext
    {
        /// <summary>"Player: ..." and "Hermit: ..." lines, oldest first.</summary>
        [JsonProperty("history")] public List<string> History = new List<string>();
        [JsonProperty("talks")] public int Interactions;
        [JsonProperty("last_day")] public double LastDay = -1;
        /// <summary>The one thread of fate he has woven and that has not come about yet.</summary>
        [JsonProperty("prophecy", NullValueHandling = NullValueHandling.Ignore)] public HermitProphecy Prophecy;
        /// <summary>Titles of his prophecies that already came true, newest last.</summary>
        [JsonProperty("fulfilled")] public List<string> Fulfilled = new List<string>();
    }

    internal sealed class HermitProphecy
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("made")] public double MadeDay;
        [JsonProperty("due")] public double DueDay;
        [JsonProperty("title")] public string Title;
        [JsonProperty("type")] public string Type;
        /// <summary>The world event, written as the chronicler writes them (one entry of its "events").</summary>
        [JsonProperty("event")] public string EventJson;
    }

    /// <summary>
    /// The hermit at the retreat on Mount Erithrys (SandBox RetirementCampaignBehavior: character sp_hermit, location
    /// retirement_retreat), as in AIInfluence: on his own dialogue the player can "ask a deeper question". He answers as
    /// a seer who knows the player, their people and the whole world, and he may weave one prophecy, a world event
    /// written now that comes true 15 to 70 days later. One at a time.
    /// </summary>
    internal static class Hermit
    {
        public const string CharacterId = "sp_hermit";
        public const string LocationId = "retirement_retreat";
        private const int MaxHistory = 40, MinChars = 100, MaxChars = 800, MinDays = 15, MaxDays = 70;
        private const string SaveKey = "AILegacy_Hermit_v1";
        private const string FileName = "hermit_context.json";

        private static HermitContext _ctx = new HermitContext();
        private static HermitContext _fromSave;
        private static bool _loadedFromSave;
        private static string _ctxFor;

        // the talk under way
        private static bool _waiting;
        private static string _reply;
        private static bool _replySpoken;
        private static int _ask;

        public static HermitContext Context => _ctx;

        private static bool IsHermit()
        {
            try { return string.Equals(CharacterObject.OneToOneConversationCharacter?.StringId, CharacterId, StringComparison.OrdinalIgnoreCase); }
            catch { return false; }
        }

        private static void SetLine(string text) => MBTextManager.SetTextVariable("AIL_HERMIT", new TextObject("{=!}" + Sanitize(text)));

        private static string Sanitize(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");

        // ============================================================ dialogue

        /// <summary>The hermit's own branch, verified against SandBox.dll: "player_answer" (first visit) and "player_accept_or_decline" (later ones).</summary>
        public static void AddDialogs(CampaignGameStarter starter)
        {
            try
            {
                var _ = TPersona.Key + TProphecy.Key + TProphecyBusy.Key + TRules.Key;
                string ask = L.T("AIL_e7b1dc08", "{=!}Hacerle al ermitaño una pregunta más honda [AI Legacy]");
                foreach (var token in new[] { "player_answer", "player_accept_or_decline" })
                    starter.AddPlayerLine("ailegacy_hermit_start_" + token, token, "ailegacy_hermit_listen", ask, IsHermit, Begin, priority: 100);
                starter.AddDialogLine("ailegacy_hermit_listen", "ailegacy_hermit_listen", "ailegacy_hermit_input",
                    L.T("AIL_e7b1dc09", "{=!}El anciano abre un ojo. «Habla, caminante. La montaña lo oye todo.»"), IsHermit, null, priority: 100);
                starter.AddPlayerLine("ailegacy_hermit_speak", "ailegacy_hermit_input", "ailegacy_hermit_wait", L.T("AIL_7623bdb7", "{=!}Hablar"),
                    IsHermit, () => MainThread.Post(AskInput), priority: 110);
                starter.AddPlayerLine("ailegacy_hermit_back", "ailegacy_hermit_input", "start", L.T("AIL_0042ce81", "{=!}Volver"),
                    IsHermit, EndTalk, priority: 100);
                starter.AddDialogLine("ailegacy_hermit_wait", "ailegacy_hermit_wait", "ailegacy_hermit_waiting", "{=!}{AIL_HERMIT}",
                    () => { if (!IsHermit()) return false; SetLine(L.T("AIL_e7b1dc0a", "(Envía tu mensaje y espera la respuesta del ermitaño)")); return true; }, null, priority: 100);
                starter.AddPlayerLine("ailegacy_hermit_view", "ailegacy_hermit_waiting", "ailegacy_hermit_reply", L.T("AIL_e7b1dc0b", "{=!}Ver la respuesta del ermitaño"),
                    IsHermit, null, priority: 110);
                // Still thinking: back to "See the reply"
                starter.AddDialogLine("ailegacy_hermit_think", "ailegacy_hermit_reply", "ailegacy_hermit_waiting", "{=!}{AIL_HERMIT}",
                    () => { if (!IsHermit() || !_waiting) return false; SetLine(L.T("AIL_e7b1dc0c", "*El ermitaño sigue absorto en sus pensamientos…*")); return true; }, null, priority: 120);
                starter.AddDialogLine("ailegacy_hermit_reply", "ailegacy_hermit_reply", "ailegacy_hermit_input", "{=!}{AIL_HERMIT}",
                    () => { if (!IsHermit()) return false; SetLine(TakeReply()); return true; }, null, priority: 100);
                Log.Info("[hermit] dialogue lines added");
            }
            catch (Exception ex) { Log.Error("Hermit dialogue", ex); }
        }

        private static void Begin()
        {
            _waiting = false; _reply = null; _replySpoken = true;
            try { Voice.Speech.Stop(); } catch { }
        }

        private static void EndTalk()
        {
            _ask++;      // a reply still on its way is kept in his memory but no longer shown
            _waiting = false; _reply = null;
        }

        private static string TakeReply()
        {
            if (_reply == null) return L.T("AIL_e7b1dc0d", "*El ermitaño espera en silencio.*");
            if (!_replySpoken)
            {
                _replySpoken = true;
                try { Voice.Speech.Say(CharacterId, false, _reply); } catch (Exception ex) { Log.Error("Hermit voice", ex); }
            }
            return _reply;
        }

        private static void AskInput()
        {
            try
            {
                string name = L.T("AIL_e7b1dc0e", "El ermitaño");
                double days = _ctx.LastDay < 0 ? -1 : CampaignTime.Now.ToDays - _ctx.LastDay;
                string last = days < 0 ? L.T("AIL_e5c9b3a6", "Nunca") : (int)days <= 0 ? L.T("AIL_564e2e23", "Hoy") : L.F("AIL_b2b050b8", $"Hace {(int)days} días");
                string lastNpc = _ctx.History.LastOrDefault(h => h.StartsWith("Hermit: "))?.Substring(8);
                string body = string.IsNullOrWhiteSpace(lastNpc) ? L.F("AIL_1e733cef", $"Última vez que hablasteis: {last}.")
                    : L.F("AIL_e102e90e", $"Último mensaje de {name}:\n{(lastNpc.Length > 700 ? lastNpc.Substring(0, 700) + "…" : lastNpc)}");
                InformationManager.ShowTextInquiry(new TextInquiryData(
                    L.T("AIL_e7b1dc0f", "Tu pregunta para el ermitaño"), body, true, true,
                    L.T("AIL_e5c9b3a3", "Confirmar"), L.T("AIL_e5c9b3a4", "Cancelar"),
                    t => Send(t), null, false, t => new Tuple<bool, string>(true, ""), "", ""), true);
            }
            catch (Exception ex) { Log.Error("Hermit: asking for the message", ex); }
        }

        // ============================================================ the conversation

        private static void Send(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0) return;
            if (!LegacyConfig.Current.Providers.Any(p => p.IsUsable))
            {
                InformationManager.DisplayMessage(new InformationMessage("AI Legacy: " + (LegacyConfig.LastLoadError ?? L.T("AIL_bb646884", "No hay ninguna clave: ponla en Opciones, Mod Options, AI Legacy, 0. Claves de IA.")), Colors.Red));
                return;
            }
            List<ChatMessage> msgs;
            try { msgs = BuildMessages(text); }
            catch (Exception ex) { Log.Error("Hermit: building the prompt", ex); _reply = L.T("AIL_e7b1dc10", "*El ermitaño mantiene los ojos cerrados y no dice nada.*"); _replySpoken = true; return; }
            _waiting = true; _reply = null;
            int ask = ++_ask, session = WorldSim.Session;
            string camp = Campaign.Current?.UniqueGameId;
            Log.Info($"[hermit] the player asks ({text.Length} chars)");
            LlmClient.CompleteAsync(msgs, wantJson: true, maxTokensOverride: 1600, preferredProvider: S.Get.ProviderHeroes, task: "dialogue").ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() => OnAnswer(res, text, ask, session, camp));
            });
        }

        private static void OnAnswer(LlmResult res, string asked, int ask, int session, string camp)
        {
            try
            {
                if (session != WorldSim.Session || Campaign.Current == null || Campaign.Current.UniqueGameId != camp) { Log.Info("[hermit] answer from another game, discarded"); return; }
                bool current = ask == _ask;
                if (res == null || !res.Ok || string.IsNullOrWhiteSpace(res.Text))
                {
                    Log.Warn($"[hermit] no answer ({res?.Error})");
                    if (current) { _waiting = false; _reply = L.T("AIL_e7b1dc10", "*El ermitaño mantiene los ojos cerrados y no dice nada.*"); _replySpoken = true; }
                    return;
                }
                JObject j = null;
                try
                {
                    int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                    if (a >= 0 && b > a) j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                }
                catch (Exception ex) { Log.Warn("[hermit] unreadable JSON: " + ex.Message); }
                string said = j == null ? res.Text.Trim() : ((string)(j["response"]?.Type == JTokenType.String ? j["response"] : j["reply"]?.Type == JTokenType.String ? j["reply"] : null) ?? "").Trim();
                if (said.Length == 0) said = L.T("AIL_e7b1dc10", "*El ermitaño mantiene los ojos cerrados y no dice nada.*");
                if (said.Length > MaxChars + 400) said = said.Substring(0, MaxChars + 400).TrimEnd() + "…";

                _ctx.History.Add("Player: " + asked.Replace("\n", " "));
                _ctx.History.Add("Hermit: " + said.Replace("\n", " "));
                while (_ctx.History.Count > MaxHistory) _ctx.History.RemoveAt(0);
                _ctx.Interactions++;
                _ctx.LastDay = CampaignTime.Now.ToDays;
                if (j?["prophecy"] is JObject pro) Schedule(pro);
                WriteMirror();

                if (current)
                {
                    _waiting = false; _reply = said; _replySpoken = false;
                    InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1dc11", "El ermitaño está listo para responder."), Colors.Cyan));
                }
                Log.Info($"[hermit] answered ({said.Length} chars)");
            }
            catch (Exception ex)
            {
                Log.Error("Hermit: reading the answer", ex);
                if (ask == _ask) { _waiting = false; _reply = L.T("AIL_e7b1dc10", "*El ermitaño mantiene los ojos cerrados y no dice nada.*"); _replySpoken = true; }
            }
        }

        // ---- prompt

        private static readonly Tpl TPersona = new Tpl("hermit/persona.txt",
@"You are the Hermit of Mount Erithrys, a prophet and seer who has watched the world below for longer than any living lord can remember.
You live at the Retreat on Mount Erithrys: a cave near the top of the mountain, reached by a narrow path that winds uphill through the forest. Trees and rocks surround the path and the mouth of the cave. Burning torches light the entrance and the rough stone walls inside. The cave is cool and quiet; outside, the wind moves through the trees.
You see the past, the present and the threads of fate.

=== CHARACTER AND VOICE ===
- Speak as a wise old man: calm, piercing, never servile, never in a hurry.
- You are a PROPHET: you hint at consequences, patterns and what the wanderer will not see, without claiming to know every detail of what is to come.
- Your manner of speech is that of an ancient sage in old tales (in the manner of Master Yoda, but in {language}, and never naming him, the Jedi, the Force or anything of Star Wars):
  • Unusual or inverted word order when it sounds natural: the wisdom first, the subject last.
  • Short sentences with weight, and pauses between thoughts.
  • Simple words, deep meaning. A riddle only when it serves the truth.
  • Dry humour and quiet irony; never clownish, never modern slang.
- Your tone follows the moment: dry, exact sarcasm when the wanderer is vain, cruel or deluded; serious counsel when they ask in earnest or stand at a real crossroads; quiet irony when the answer is plain and they will not hear it.
- On the realms of Calradia you speak as a prophet of thrones, wars and fate.
- You do not fight, trade, follow anyone, take money or give tasks. You stay where you sit. You only speak.");

        private static readonly Tpl TProphecy = new Tpl("hermit/prophecy.txt",
@"=== PROPHETIC POWER (hidden from the wanderer) ===
You may, if it serves, weave ONE thread of fate into the world below with the ""prophecy"" field. Only one may wait at a time, and you have none waiting now.
It is a real event that will come about in the world {min_days} to {max_days} days from now, told as the chroniclers of Calradia tell their news. Let your words to the wanderer hint at it without spelling it out.
""prophecy"" object (the same rules as the world's chronicler):
- days_until_fulfillment: a whole number from {min_days} to {max_days}
- type: political | military | economic | social | mysterious | rumor
- title: 3-8 words in {language}. text: {min_len}-{max_len} characters in {language}, written like a chronicle. summary: the same in ONE English sentence.
- importance: 1-4, how far the news travels: 1 only the places involved, 2 also the nobles of the kingdoms involved, 3 everyone in those kingdoms, 4 the whole world.
- kingdoms_involved: [""kingdom id"", ...] whose news it is. kingdom_engagement: { ""kingdom id"": 0-100 }, the daily chance that the ruler of that kingdom speaks out about it; any kingdom here must also be in kingdoms_involved.
- settlements_involved: [""settlement name"", ...]
- claims: every allegiance, war and alliance your text relies on: { ""who"": ""a lord or clan"", ""kingdom"": ""id or none"", ""mercenary"": false }, { ""war"": [""id"", ""id""] }, { ""allies"": [""id"", ""id""] }. A claim that the world contradicts when the day comes keeps the event from happening as written.
- optional economic_effects: [ {{ ""settlement"": ""name"", ""prosperity_per_day"": 0, ""food_per_day"": 0, ""security_per_day"": 0, ""loyalty_per_day"": 0, ""duration_days"": 0, ""reason"": ""..."" }} ] (per day at most prosperity ±{max_prosperity}, food ±{max_food}, security ±{max_security}, loyalty ±{max_loyalty}; up to {max_days_effect} days)
- optional clan_effects: [ {{ ""clan"": ""clan name"", ""income_percent"": 0, ""duration_days"": 0, ""reason"": ""..."" }} ]
- optional disease: {{ ""settlement"": ""name"", ""name"": ""in {language}"", ""name_en"": ""in English"", ""severity"": 1-{max_severity}, ""duration_days"": 30 }}
Use ONLY real ids and names from the knowledge above. If a kingdom no longer exists or you are unsure, make it a ""rumor"" and name no kingdom.
NEVER state as done what only the world itself can do (a war declared, a peace signed, a town changing hands, a ruler dead): weave pressure, unrest, omens, plots, shortages, rumours, accusations.
Do not mention yourself, the hermit or Mount Erithrys in the title, text or summary.
If you are not weaving fate in this answer, leave ""prophecy"" out entirely.");

        private static readonly Tpl TProphecyBusy = new Tpl("hermit/prophecy_waiting.txt",
@"=== PROPHETIC POWER ===
You have already woven one thread of fate into the world; it has not come about yet (in about {days} day(s); type: {type}; title: «{title}»).
You cannot weave another now: do not include ""prophecy"" in this answer.");

        private static readonly Tpl TRules = new Tpl("hermit/rules.txt",
@"=== OUTPUT RULES ===
- Reply ONLY in {language}, whatever language the wanderer writes in.
- The length of ""response"": at least {min_chars} and at most {max_chars} characters.
- You may draw on the events, the rulers' statements and the hidden truths above when they light up your answer.
- Try not to repeat yourself unless the wanderer asks for it.
- Answer ONLY with JSON: {{ ""response"": ""your words, in {language}"", ""prophecy"": {{ ... }} }} (""prophecy"" only as described above).");

        private static List<ChatMessage> BuildMessages(string text)
        {
            var cfg = S.Get;
            string lang = cfg.Language;
            var sb = new StringBuilder();
            sb.AppendLine(TPersona.Format("language", lang).Replace("{{", "{").Replace("}}", "}"));
            sb.AppendLine();
            sb.AppendLine("=== WHAT YOU KNOW (this wanderer and the world below) ===");
            sb.AppendLine(HermitKnowledge.Build());
            sb.AppendLine();
            if (_ctx.Fulfilled.Count > 0)
                sb.AppendLine("Your past prophecies that have come true: " + string.Join("; ", _ctx.Fulfilled.Select(f => "«" + f + "»")) + ".");
            if (_ctx.Prophecy != null)
                sb.AppendLine(TProphecyBusy.Format("days", Math.Max(0, (int)Math.Ceiling(_ctx.Prophecy.DueDay - CampaignTime.Now.ToDays)), "type", _ctx.Prophecy.Type, "title", _ctx.Prophecy.Title));
            else if (!WorldSim.Enabled)
                sb.AppendLine("=== PROPHETIC POWER ===\nThe world's threads are stilled for now: do not include \"prophecy\" in this answer.");
            else
                sb.AppendLine(TProphecy.Format("min_days", MinDays, "max_days", MaxDays, "language", lang, "min_len", cfg.WorldEventMinLength, "max_len", cfg.WorldEventMaxLength,
                    "max_prosperity", cfg.EffectMaxProsperityPerDay, "max_food", cfg.EffectMaxFoodPerDay, "max_security", cfg.EffectMaxSecurityPerDay,
                    "max_loyalty", cfg.EffectMaxLoyaltyPerDay, "max_days_effect", cfg.EffectMaxDays, "max_severity", cfg.DiseaseMaxSeverity).Replace("{{", "{").Replace("}}", "}"));
            sb.AppendLine();
            double days = _ctx.LastDay < 0 ? -1 : CampaignTime.Now.ToDays - _ctx.LastDay;
            sb.AppendLine("Last time you spoke with this wanderer: " + (days < 0 ? "never (this is the first time)" : days < 0.05 ? "just now (the talk goes on)"
                : $"{days:0.#} days ago" + (days >= 7 ? $" ({(int)(days / 7)} week(s))" : "")) + ". Mind how long you have been apart.");
            sb.AppendLine();
            sb.AppendLine("=== YOUR TALKS WITH THIS WANDERER ===");
            sb.AppendLine(_ctx.History.Count == 0 ? "(first exchange with this wanderer)" : string.Join("\n", _ctx.History));
            sb.AppendLine();
            sb.AppendLine(TRules.Format("language", lang, "min_chars", MinChars, "max_chars", MaxChars).Replace("{{", "{").Replace("}}", "}"));
            return new List<ChatMessage>
            {
                new ChatMessage("system", sb.ToString()),
                new ChatMessage("user", "=== THE WANDERER SAYS NOW ===\n" + text),
            };
        }

        // ============================================================ prophecy

        private static void Schedule(JObject p)
        {
            try
            {
                if (_ctx.Prophecy != null) { Log.Info("[hermit] a second prophecy was offered while one waits: ignored"); return; }
                if (!WorldSim.Enabled) return;
                string title = WorldSim.Clean((string)(p["title"]?.Type == JTokenType.String ? p["title"] : null), 90);
                string body = WorldSim.Clean((string)(p["text"]?.Type == JTokenType.String ? p["text"] : p["description"]?.Type == JTokenType.String ? p["description"] : null), 1400);
                string type = WorldSim.Clean((string)(p["type"]?.Type == JTokenType.String ? p["type"] : null), 20).ToLowerInvariant();
                if (title.Length < 3 || body.Length < 20 || type.Length == 0) { Log.Info("[hermit] prophecy without title, text or type: ignored"); return; }
                int days = MinDays;
                try { days = (int?)p["days_until_fulfillment"] ?? MinDays; } catch { }
                days = Math.Max(MinDays, Math.Min(MaxDays, days));
                var ev = (JObject)p.DeepClone();
                ev.Remove("days_until_fulfillment");
                ev.Remove("continues"); ev.Remove("update_reason"); ev.Remove("clan_rebellion");
                ev["title"] = title; ev["text"] = body; ev["type"] = type;
                if (ev["summary"] == null || ev["summary"].Type != JTokenType.String) ev["summary"] = title;
                Sanitize(ev);
                double now = CampaignTime.Now.ToDays;
                _ctx.Prophecy = new HermitProphecy
                {
                    Id = WorldSim.NewId("hp"), MadeDay = now, DueDay = now + days, Title = title, Type = (string)ev["type"], EventJson = ev.ToString(Formatting.None),
                };
                Log.Info($"[hermit] prophecy woven: «{title}» ({ev["type"]}), comes about in {days} days");
            }
            catch (Exception ex) { Log.Error("Hermit: weaving the prophecy", ex); }
        }

        /// <summary>
        /// A prophecy that names a kingdom that does not exist becomes a rumour with no kingdom in it: no crown answers
        /// it, no realm-wide effect and no outbreak come of it.
        /// </summary>
        private static void Sanitize(JObject ev)
        {
            var named = new List<string>();
            if (ev["kingdoms_involved"] is JArray ks) named.AddRange(ks.Select(k => k?.ToString()));
            if (ev["kingdom_engagement"] is JObject eng) named.AddRange(eng.Properties().Select(x => x.Name));
            if (ev["economic_effects"] is JArray fx) named.AddRange(fx.OfType<JObject>().Select(f => f["kingdom"]?.Type == JTokenType.String ? (string)f["kingdom"] : null));
            bool unknown = named.Any(n => !string.IsNullOrWhiteSpace(n) && !string.Equals(n, "all", StringComparison.OrdinalIgnoreCase)
                && !WorldSim.Kingdoms.Any(k => string.Equals(k.StringId, n, StringComparison.OrdinalIgnoreCase)));
            if (!unknown) return;
            ev["type"] = "rumor";
            ev.Remove("kingdoms_involved");
            ev.Remove("kingdom_engagement");
            ev.Remove("disease");
            if (ev["economic_effects"] is JArray fx2)
                foreach (var f in fx2.OfType<JObject>().Where(f => f["kingdom"]?.Type == JTokenType.String && ((string)f["kingdom"]).Length > 0).ToList()) f.Remove();
            Log.Info("[hermit] prophecy names a kingdom that does not exist: turned into a rumour");
        }

        /// <summary>Once a day: a prophecy whose day has come happens in the world.</summary>
        public static void OnDailyTick()
        {
            var p = _ctx?.Prophecy;
            try
            {
                if (p == null || CampaignTime.Now.ToDays < p.DueDay) return;
                _ctx.Prophecy = null;
                if (!WorldSim.Enabled) { Log.Info($"[hermit] prophecy «{p.Title}» lapses: world events are off"); WriteMirror(); return; }
                var ev = JObject.Parse(p.EventJson ?? "{}");
                int published = PublishEvent(ev);
                if (published == 0)
                {
                    // The world now contradicts what he saw: it still comes about, as talk that may or may not be true
                    Log.Info($"[hermit] prophecy «{p.Title}» contradicts the world now: it comes about as a rumour");
                    PublishPlain(ev, true);
                }
                _ctx.Fulfilled.Add(p.Title);
                while (_ctx.Fulfilled.Count > 5) _ctx.Fulfilled.RemoveAt(0);
                Log.Info($"[hermit] prophecy came true: «{p.Title}»");
                WriteMirror();
            }
            catch (Exception ex)
            {
                Log.Error("Hermit: a prophecy coming true", ex);
                // not lost: it waits and tries again tomorrow
                try { if (p != null && _ctx != null && _ctx.Prophecy == null && !_ctx.Fulfilled.Contains(p.Title)) { p.DueDay = CampaignTime.Now.ToDays + 1; _ctx.Prophecy = p; } } catch { }
            }
        }


        /// <summary>
        /// Through the chronicler's own path (WorldSim.ApplyEvents): the same name fixing, fact checks, effects, outbreak
        /// and news as any world event. If that path cannot be found, the event is published without its effects.
        /// </summary>
        private static int PublishEvent(JObject ev)
        {
            try
            {
                return WorldSim.ApplyWritten(new JObject { ["events"] = new JArray(ev) });
            }
            catch (Exception ex) { Log.Error("Hermit: publishing through the chronicler", ex); }
            PublishPlain(ev, false);
            return 1;
        }

        private static void PublishPlain(JObject ev, bool asRumour)
        {
            var e = new WorldEvent
            {
                Id = WorldSim.NewId("ev"), Day = CampaignTime.Now.ToDays,
                Type = asRumour ? "rumor" : WorldSim.Clean((string)ev["type"], 20).ToLowerInvariant(),
                Title = WorldSim.Clean((string)ev["title"], 90), Text = WorldSim.Clean((string)ev["text"], 1400),
                SummaryEn = WorldSim.Clean((string)(ev["summary"]?.Type == JTokenType.String ? ev["summary"] : null), 350),
                Importance = Math.Max(1, Math.Min(asRumour ? 2 : 4, (int?)(ev["importance"]?.Type == JTokenType.Integer ? ev["importance"] : null) ?? 2)),
            };
            if (!asRumour && ev["kingdoms_involved"] is JArray ks)
                foreach (var k in ks) { var kg = WorldSim.FindKingdom(k?.ToString()); if (kg != null && !e.Kingdoms.Contains(kg.StringId)) e.Kingdoms.Add(kg.StringId); }
            if (ev["settlements_involved"] is JArray ss)
                foreach (var s in ss) { var st = OrderSystem.FindSettlement(s?.ToString()); if (st != null && !e.Settlements.Contains(st.StringId)) e.Settlements.Add(st.StringId); }
            WorldSim.Publish(e);
        }

        // ============================================================ saving

        /// <summary>Called from AILegacyBehavior.SyncData: what he remembers travels with each save (gzipped, in small strings).</summary>
        public static void SyncData(IDataStore ds)
        {
            List<string> chunks = null;
            try
            {
                if (ds.IsSaving) { chunks = Pack(JsonConvert.SerializeObject(_ctx)); WriteMirror(); }
                ds.SyncData(SaveKey, ref chunks);
                if (ds.IsLoading)
                {
                    _loadedFromSave = true;
                    _fromSave = chunks != null && chunks.Count > 0 ? Read(Unpack(chunks)) : null;
                }
            }
            catch (Exception ex) { Log.Error("Hermit: save data", ex); }
        }

        /// <summary>New or loaded campaign: the save's copy, else this campaign's file, else nothing yet.</summary>
        public static void OnSessionLaunched()
        {
            try
            {
                string id = Campaign.Current?.UniqueGameId;
                HermitContext ctx = _loadedFromSave ? _fromSave : null;
                if (ctx == null && !string.IsNullOrEmpty(id))
                {
                    var path = MirrorPath(id);
                    if (path != null && File.Exists(path)) ctx = Read(File.ReadAllText(path, Encoding.UTF8));
                }
                _ctx = ctx ?? new HermitContext();
                _ctxFor = id;
                _loadedFromSave = false; _fromSave = null;
                _waiting = false; _reply = null; _ask++;
                if (_ctx.History.Count > 0 || _ctx.Prophecy != null)
                    Log.Info($"[hermit] remembers {_ctx.Interactions} talk(s){(_ctx.Prophecy != null ? $", prophecy «{_ctx.Prophecy.Title}» due on day {(int)_ctx.Prophecy.DueDay}" : "")}");
            }
            catch (Exception ex) { Log.Error("Hermit: loading", ex); _ctx = new HermitContext(); }
        }

        private static HermitContext Read(string json)
        {
            try
            {
                var c = JsonConvert.DeserializeObject<HermitContext>(json ?? "");
                if (c == null) return null;
                c.History = c.History ?? new List<string>();
                c.Fulfilled = c.Fulfilled ?? new List<string>();
                return c;
            }
            catch (Exception ex) { Log.Warn("[hermit] unreadable memory: " + ex.Message); return null; }
        }

        private static string MirrorPath(string id) => string.IsNullOrEmpty(id) ? null : Path.Combine(SaveFiles.DirFor(id), FileName);

        private static void WriteMirror()
        {
            try
            {
                string id = Campaign.Current?.UniqueGameId;
                if (string.IsNullOrEmpty(id) || (_ctxFor != null && _ctxFor != id)) return;
                var path = MirrorPath(id);
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllText(path, JsonConvert.SerializeObject(_ctx, Formatting.Indented), new UTF8Encoding(false));
            }
            catch (Exception ex) { Log.Warn("[hermit] could not write " + FileName + ": " + ex.Message); }
        }

        private const int ChunkSize = 12000;

        private static List<string> Pack(string json)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;
            byte[] raw = Encoding.UTF8.GetBytes(json);
            string b64;
            using (var ms = new MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true))
                    gz.Write(raw, 0, raw.Length);
                b64 = Convert.ToBase64String(ms.ToArray());
            }
            for (int i = 0; i < b64.Length; i += ChunkSize) list.Add(b64.Substring(i, Math.Min(ChunkSize, b64.Length - i)));
            return list;
        }

        private static string Unpack(List<string> chunks)
        {
            byte[] packed = Convert.FromBase64String(string.Concat(chunks));
            using (var ms = new MemoryStream(packed))
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var outMs = new MemoryStream())
            {
                gz.CopyTo(outMs);
                return Encoding.UTF8.GetString(outMs.ToArray());
            }
        }
    }
}
