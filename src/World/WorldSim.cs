using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.World
{
    // ===================================================================== data

    public class EconomicEffect
    {
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("prosperity")] public float ProsperityPerDay;
        [JsonProperty("food")] public float FoodPerDay;
        [JsonProperty("security")] public float SecurityPerDay;
        [JsonProperty("loyalty")] public float LoyaltyPerDay;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("reason")] public string Reason = "";
        [JsonProperty("event")] public string EventId;
        // Effect on a whole clan (its name stained or rising): its income goes up or down by this percent
        [JsonProperty("clan")] public string ClanId;
        [JsonProperty("income")] public float IncomePercent;
        // Market prices at that place go up or down by this percent while it lasts (shortage, fear, plenty)
        [JsonProperty("prices")] public float PricePercent;
        /// <summary>Which goods the price change touches: food, goods, weapons, armor, horses. Empty = everything.</summary>
        [JsonProperty("price_of", NullValueHandling = NullValueHandling.Ignore)] public string PriceCategory;
    }

    public class WorldEvent
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("day")] public double Day;
        [JsonProperty("type")] public string Type = "news";            // political | military | economic | social | mysterious | news
        [JsonProperty("title")] public string Title = "";             // player's language
        [JsonProperty("text")] public string Text = "";               // player's language
        [JsonProperty("summary")] public string SummaryEn = "";       // English, for prompts
        [JsonProperty("imp")] public int Importance = 2;
        [JsonProperty("kingdoms")] public List<string> Kingdoms = new List<string>();
        [JsonProperty("settlements")] public List<string> Settlements = new List<string>();
        /// <summary>Daily chance (0-100) that each kingdom's ruler speaks out about this.</summary>
        [JsonProperty("engagement")] public Dictionary<string, int> Engagement = new Dictionary<string, int>();
        [JsonProperty("source")] public string Source = "world";      // world | statement | player
        /// <summary>Earlier stages of the same story, oldest first: "day|what was told then" in English.</summary>
        [JsonProperty("history", NullValueHandling = NullValueHandling.Ignore)] public List<string> History;
    }

    /// <summary>A ruler whose turn it is to speak, about an event or because a proposal is waiting for them.</summary>
    public class QueuedStatement
    {
        [JsonProperty("kingdom")] public string KingdomId;
        [JsonProperty("event")] public string EventId;       // null: answers proposals
        [JsonProperty("due")] public double DueHour;          // campaign hour from which they speak
        /// <summary>"wars": nothing called them, their realm is worn by its wars and they decide about them.</summary>
        [JsonProperty("why", NullValueHandling = NullValueHandling.Ignore)] public string Why;
    }

    public class RulerStatement
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("day")] public double Day;
        [JsonProperty("kingdom")] public string KingdomId;
        [JsonProperty("ruler")] public string RulerName;
        [JsonProperty("text")] public string Text = "";
        [JsonProperty("summary")] public string SummaryEn = "";
        [JsonProperty("results")] public List<string> Results = new List<string>();
        [JsonProperty("imp")] public int Importance = 2;
    }

    /// <summary>A proposal or demand from one kingdom to another, waiting for an answer.</summary>
    public class Proposal
    {
        [JsonProperty("id")] public string Id;
        /// <summary>peace | alliance | trade | tribute | reparations | territory</summary>
        [JsonProperty("type")] public string Type;
        [JsonProperty("from")] public string From;
        [JsonProperty("to")] public string To;
        [JsonProperty("day")] public double Day;
        [JsonProperty("expires")] public double ExpiresDay;
        [JsonProperty("daily")] public int DailyTribute;       // paid by "to" to "from"
        [JsonProperty("days")] public int TributeDays;
        [JsonProperty("amount")] public int Reparations;       // paid by "to" to "from"
        [JsonProperty("settlement")] public string SettlementId;   // given up by "to" to "from"
        [JsonProperty("years")] public int TradeYears;
        // Peace only: what "from" offers to give "to". The fields above stay what "from" demands of "to".
        [JsonProperty("offer_daily", DefaultValueHandling = DefaultValueHandling.Ignore)] public int OfferDailyTribute;      // paid by "from" to "to"
        [JsonProperty("offer_days", DefaultValueHandling = DefaultValueHandling.Ignore)] public int OfferTributeDays;
        [JsonProperty("offer_amount", DefaultValueHandling = DefaultValueHandling.Ignore)] public int OfferReparations;      // paid by "from" to "to"
        [JsonProperty("offer_settlement", DefaultValueHandling = DefaultValueHandling.Ignore)] public string OfferSettlementId;   // given up by "from" to "to"
        /// <summary>
        /// The settlement demanded (or offered) was its giver's last town or castle when the peace was proposed, and
        /// the terms said so. Without this a realm's last one is never handed over.
        /// </summary>
        [JsonProperty("last", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool SettlementLast;
        [JsonProperty("offer_last", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool OfferSettlementLast;
        /// <summary>A peace that took the place of the one its recipient had proposed: they are told theirs is off the table.</summary>
        [JsonProperty("counter", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Counter;
        /// <summary>Already shown to the player (when it's aimed at their kingdom).</summary>
        [JsonProperty("notified")] public bool Notified;
        /// <summary>Its recipient has had a turn to answer it. One that runs out without that was never refused.</summary>
        [JsonProperty("seen", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Seen;
        /// <summary>A peace with terms whose recipient asked for a plain one: they are told the terms stand.</summary>
        [JsonProperty("wanted_plain", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool WantedPlain;
    }

    public class Tribute
    {
        [JsonProperty("payer")] public string Payer;
        [JsonProperty("receiver")] public string Receiver;
        [JsonProperty("daily")] public int Daily;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("total")] public int TotalPaid;
    }

    /// <summary>All world simulation state. Stored in ailegacy_world.json and in the in-save copy.</summary>
    public class WorldState
    {
        [JsonProperty("events")] public List<WorldEvent> Events = new List<WorldEvent>();
        [JsonProperty("statements")] public List<RulerStatement> Statements = new List<RulerStatement>();
        [JsonProperty("proposals")] public List<Proposal> Proposals = new List<Proposal>();
        [JsonProperty("tributes")] public List<Tribute> Tributes = new List<Tribute>();
        [JsonProperty("effects")] public List<EconomicEffect> Effects = new List<EconomicEffect>();
        /// <summary>Day until which paid hospital prevention lasts (your men get sick much less).</summary>
        [JsonProperty("prevention_until")] public double PreventionUntilDay = -1;
        /// <summary>Secrets born from this campaign's story, and the kingdoms whose origin secrets are already written.</summary>
        [JsonProperty("secrets")] public List<StorySecret> Secrets = new List<StorySecret>();
        [JsonProperty("secret_kingdoms")] public List<string> SecretKingdoms = new List<string>();
        /// <summary>Written in the editor: who the player's character is taken for, and their family. Goes to those who know who the player is.</summary>
        [JsonProperty("player_description")] public string PlayerDescription = "";
        [JsonProperty("player_family")] public string PlayerFamily = "";
        /// <summary>Characters starred in the editor's NPC list.</summary>
        [JsonProperty("editor_favorites")] public List<string> EditorFavorites = new List<string>();
        /// <summary>Arenas you already trained at (settlement id -> campaign hour).</summary>
        [JsonProperty("arena_last")] public Dictionary<string, double> ArenaLastAt = new Dictionary<string, double>();
        /// <summary>Current dungeon sentence: where, and until which day.</summary>
        [JsonProperty("jail_in")] public string JailSettlementId;
        [JsonProperty("jail_until")] public double JailReleaseDay = -1;
        [JsonProperty("uniques")] public List<string> UniqueIds = new List<string>();
        /// <summary>Townsfolk the player has talked to: their memory and the face they keep (World/ResidentFaces.cs).</summary>
        [JsonProperty("residents")] public List<Memory.HeroMemory> Residents = new List<Memory.HeroMemory>();
        /// <summary>Events that weigh on someone's (or a kingdom's) mood for a few days, and who killed whom.</summary>
        [JsonProperty("mood_events")] public List<MoodEvent> MoodEvents = new List<MoodEvent>();
        [JsonProperty("recent_kills")] public List<KillRecord> RecentKills = new List<KillRecord>();
        /// <summary>Everyone the player has executed or killed with their own hand, and whether the game's own log was already read for the older ones.</summary>
        [JsonProperty("executions")] public List<ExecutionRecord> Executions = new List<ExecutionRecord>();
        [JsonProperty("executions_seeded")] public bool ExecutionsSeeded;
        /// <summary>Day each current pregnancy was conceived (the mother takes a few days to find out).</summary>
        [JsonProperty("conceived")] public Dictionary<string, double> Conceived = new Dictionary<string, double>();
        /// <summary>Last day a chat changed war or peace between two kingdoms ("a|b").</summary>
        [JsonProperty("war_peace_day")] public Dictionary<string, double> WarPeaceDay = new Dictionary<string, double>();
        /// <summary>Recent rulers of each kingdom and recent owners of each town or castle.</summary>
        [JsonProperty("rulers")] public Dictionary<string, List<TenureRecord>> Rulers = new Dictionary<string, List<TenureRecord>>();
        [JsonProperty("owners")] public Dictionary<string, List<TenureRecord>> Owners = new Dictionary<string, List<TenureRecord>>();
        [JsonProperty("next_unique_day")] public double NextUniqueDay = -1;
        [JsonProperty("fatigue")] public Dictionary<string, float> WarFatigue = new Dictionary<string, float>();
        [JsonProperty("taxes")] public Dictionary<string, int> KingdomTaxPercent = new Dictionary<string, int>();
        /// <summary>A ruler's stance toward another kingdom: "a|b" -> -100..100.</summary>
        [JsonProperty("stance")] public Dictionary<string, int> Stance = new Dictionary<string, int>();
        [JsonProperty("outbreaks")] public List<Outbreak> Outbreaks = new List<Outbreak>();
        /// <summary>Heroes and parties that are ill right now, and who is immune to what until which day ("hero|disease").</summary>
        [JsonProperty("sick")] public List<Sickness> Sick = new List<Sickness>();
        [JsonProperty("sick_immune")] public Dictionary<string, double> SickImmune = new Dictionary<string, double>();
        /// <summary>Garrisons and militias down with the outbreak of their settlement.</summary>
        [JsonProperty("sick_forces")] public List<ForceSickness> SickForces = new List<ForceSickness>();
        /// <summary>Treatment a clan still owes the physicians (clan id -> gold). Charged with the next daily finances.</summary>
        [JsonProperty("treatment_debt")] public Dictionary<string, int> TreatmentDebt = new Dictionary<string, int>();
        /// <summary>Heroes killed by a disease (hero id -> which and when), for the texts of their death.</summary>
        [JsonProperty("disease_deaths")] public Dictionary<string, DiseaseDeath> DiseaseDeaths = new Dictionary<string, DiseaseDeath>();
        /// <summary>Herbs the player's party took against contagion: until which day, and how much they cut it (0..1).</summary>
        [JsonProperty("herb_guard_until")] public double HerbGuardUntilDay = -1;
        [JsonProperty("herb_guard")] public float HerbGuard;
        [JsonProperty("banished")] public List<Banishment> Banished = new List<Banishment>();
        [JsonProperty("courtships")] public List<Courtship> Courtships = new List<Courtship>();
        [JsonProperty("statement_queue")] public List<QueuedStatement> StatementQueue = new List<QueuedStatement>();
        [JsonProperty("last_outbreak_day")] public double LastOutbreakDay = -1;
        [JsonProperty("laws")] public List<KingdomLaw> Laws = new List<KingdomLaw>();
        [JsonProperty("last_event_day")] public double LastEventGenerationDay = -1;
        [JsonProperty("next_event_day")] public double NextEventGenerationDay = -1;
        [JsonProperty("last_reactive_hour")] public double LastReactiveHour = -1;
        [JsonProperty("last_statement_day")] public Dictionary<string, double> LastStatementDay = new Dictionary<string, double>();
        /// <summary>Last day any AI ruler spoke, for the world-wide gap between statements.</summary>
        [JsonProperty("last_statement_any")] public double LastStatementAnyDay = -1;
        /// <summary>Day each kingdom was first seen. -1 = it was already there when the mod first looked: an old realm, not a new one.</summary>
        [JsonProperty("kingdom_seen")] public Dictionary<string, double> KingdomSeenDay = new Dictionary<string, double>();
        /// <summary>Day each alliance in force began ("a|b"). The game only keeps when it runs out.</summary>
        [JsonProperty("alliance_since")] public Dictionary<string, double> AllianceSince = new Dictionary<string, double>();
        /// <summary>Last day a town or castle changed hands between two kingdoms ("a|b"), to tell a stalemate.</summary>
        [JsonProperty("last_capture")] public Dictionary<string, double> WarLastCapture = new Dictionary<string, double>();
        /// <summary>Day a proposal between two kingdoms was last refused or left unanswered ("type|a|b").</summary>
        [JsonProperty("refused")] public Dictionary<string, double> Refused = new Dictionary<string, double>();
        /// <summary>Day we first saw a war whose start date the game does not have ("a|b").</summary>
        [JsonProperty("war_seen")] public Dictionary<string, double> WarSeenDay = new Dictionary<string, double>();
        /// <summary>Day until which neither may declare war on the other after a peace forced by exhaustion ("a|b").</summary>
        [JsonProperty("forced_truce")] public Dictionary<string, double> ForcedTruceUntil = new Dictionary<string, double>();
        /// <summary>Per kingdom id: what its AI ruler is told on his next turn about a peace that could not be signed (English).</summary>
        [JsonProperty("peace_notes")] public Dictionary<string, List<string>> PeaceNotes = new Dictionary<string, List<string>>();
    }

    // ===================================================================== engine

    /// <summary>
    /// The living world: AI-generated events with real effects on settlements, war fatigue, tributes and taxes.
    /// Ruler diplomacy lives in Diplomacy.cs.
    /// </summary>
    internal static class WorldSim
    {
        public static WorldState State = new WorldState();

        private static readonly Random Rng = new Random();
        private static readonly ConcurrentQueue<Action> Pending = new ConcurrentQueue<Action>();
        private static volatile bool _busy;
        private static DateTime _lastCall = DateTime.MinValue;

        /// <summary>A world request waiting its turn (in-memory queue, not saved).</summary>
        private class WorldRequest
        {
            public string What, Key, Camp;
            public List<ChatMessage> Messages;
            public int MaxTokens, Session, Attempts;
            public Action<JObject> Apply;
            public Action Failed;
            public bool Important;
        }
        private static readonly List<WorldRequest> Queue = new List<WorldRequest>();
        /// <summary>Keys queued, in flight or waiting to be applied, so the same thing isn't asked for twice.</summary>
        private static readonly HashSet<string> Reserved = new HashSet<string>();
        private const int MaxQueued = 8;
        private static int _reactRetries;

        public static bool Enabled => S.Get.EnableWorldEvents;
        private static string Lang => S.Get.Language;

        public static IEnumerable<Kingdom> Kingdoms => Campaign.Current.Kingdoms.Where(k => k != null && !k.IsEliminated && k.Leader != null);
        public static Kingdom FindKingdom(string idOrName)
        {
            if (string.IsNullOrWhiteSpace(idOrName)) return null;
            string n = Norm(idOrName);
            return Kingdoms.FirstOrDefault(k => k.StringId == idOrName) ?? Kingdoms.FirstOrDefault(k => Norm(k.StringId) == n || Norm(k.Name.ToString()) == n)
                   ?? Kingdoms.FirstOrDefault(k => Norm(k.Name.ToString()).Contains(n) || n.Contains(Norm(k.Name.ToString())));
        }
        public static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        public static string NewId(string prefix) => prefix + Rng.Next(100000, 999999);
        public static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        // ============================================================ background AI calls

        /// <summary>Safe to change the world now (on the map, no mission or conversation).</summary>
        public static bool OnMap()
        {
            try
            {
                return Campaign.Current != null && Mission.Current == null && !ChatSession.IsBusy
                       && Campaign.Current.ConversationManager?.IsConversationInProgress != true
                       && GameStateManager.Current?.ActiveState is MapState;
            }
            catch { return false; }
        }

        private static int _session;
        /// <summary>Changes every time a campaign is loaded, created or imported. Anyone calling the AI stores it and checks it when the reply comes back.</summary>
        public static int Session => _session;

        /// <summary>
        /// New or loaded campaign: nothing left half done in the previous one (events to react to, queued replies,
        /// fights, arrests, pending deaths) can leak into this one.
        /// </summary>
        public static void ResetSession()
        {
            _session++;
            for (int i = 0; i < ModelDay.Length; i++) ModelDay[i] = -1;
            lock (Triggers) Triggers.Clear();
            _urgent = false;
            _reactRetries = 0;
            while (Pending.TryDequeue(out _)) { }
            lock (Queue) { Queue.Clear(); Reserved.Clear(); }
            try { Dialogue.Combat.ResetSession(); } catch { }
            try { Dialogue.SceneActions.ResetSession(); } catch { }
            try { Dialogue.NpcActions.ResetSession(); } catch { }
            try { Scenes.MapTalk.ResetSession(); } catch { }
            try { Letters.LetterSystem.ResetSession(); } catch { }
            try { Dialogue.Verdict.ResetSession(); } catch { }
            try { Dialogue.Initiative.ResetSession(); } catch { }
            try { SceneImages.ResetSession(); } catch { }
            try { Dialogue.TalkMusic.ResetSession(); } catch { }
            try { NewsStore.ResetSession(); } catch { }
            try { Laws.Invalidate(); } catch { }
            try { Diplomacy.ResetSession(); } catch { }
            Log.Info("[mundo] estado de sesión limpiado (partida nueva o cargada)");
        }

        /// <summary>Nothing in flight and the minimum gap between calls has passed.</summary>
        private static bool Ready => !_busy && (DateTime.UtcNow - _lastCall).TotalSeconds >= S.Get.WorldAiMinSecondsBetweenCalls;

        /// <summary>For optional calls (event batches, secrets, characters): only when nothing is in flight or queued.</summary>
        public static bool CanCall { get { lock (Queue) return Ready && Queue.Count == 0; } }

        public static bool IsReserved(string key) { if (key == null) return false; lock (Queue) return Reserved.Contains(key); }

        /// <summary>
        /// Sends a world request. The result is applied when the player is on the map. If another one is running it waits in
        /// the queue (capped: when full, the oldest non-important one is dropped). With a key, the same thing is never asked twice.
        /// The caller must consume its schedule inside <paramref name="apply"/>, not before. Returns false if already requested.
        /// </summary>
        public static bool Ask(string what, List<ChatMessage> messages, int maxTokens, Action<JObject> apply, string key = null, Action failed = null, bool important = false)
        {
            var r = new WorldRequest
            {
                What = what, Key = key, Messages = messages, MaxTokens = maxTokens, Apply = apply, Failed = failed, Important = important,
                Session = _session, Camp = Campaign.Current?.UniqueGameId,      // the reply only counts for the campaign that asked
            };
            WorldRequest dropped = null;
            lock (Queue)
            {
                if (key != null && !Reserved.Add(key)) { Log.Info($"[mundo] {what}: ya estaba pedida"); return false; }
                if (Ready && Queue.Count == 0) { Launch(r); return true; }
                Queue.Add(r);
                Log.Info($"[mundo] {what}: en cola ({Queue.Count})");
                if (Queue.Count > MaxQueued)
                {
                    dropped = Queue.FirstOrDefault(x => !x.Important) ?? Queue[0];
                    Queue.Remove(dropped);
                    if (dropped.Key != null) Reserved.Remove(dropped.Key);
                }
            }
            if (dropped != null)
            {
                Log.Warn($"[mundo] cola llena: se descarta «{dropped.What}»");
                try { dropped.Failed?.Invoke(); } catch (Exception ex) { Log.Error("[mundo] descartando petición", ex); }
            }
            return true;
        }

        private static void Launch(WorldRequest r)
        {
            _busy = true;
            _lastCall = DateTime.UtcNow;
            r.Attempts++;
            Log.Info($"[mundo] petición: {r.What}");
            LlmClient.CompleteAsync(r.Messages, wantJson: true, task: "world", maxTokensOverride: r.MaxTokens, preferredProvider: S.Get.ProviderWorld).ContinueWith(t =>
            {
                _busy = false;
                var res = t.IsFaulted ? null : t.Result;
                JObject j = null;
                if (res == null || !res.Ok) Log.Warn($"[mundo] {r.What}: sin respuesta ({res?.Error})");
                else
                {
                    try
                    {
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a >= 0 && b > a) j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                    }
                    catch (Exception ex) { Log.Warn($"[mundo] {r.What}: JSON ilegible ({ex.Message})"); }
                }
                Pending.Enqueue(() =>
                {
                    // From another campaign: ResetSession already released its keys and cleared the queue
                    if (r.Session != _session || Campaign.Current == null || Campaign.Current.UniqueGameId != r.Camp) { if (r.Session == _session) Release(r.Key); Log.Info($"[mundo] {r.What}: respuesta de otra partida, descartada"); return; }
                    if (j == null)
                    {
                        // Important requests (the player's statement) get one retry. The rest hand back their schedule
                        if (r.Important && r.Attempts < 2) { lock (Queue) Queue.Insert(0, r); Log.Info($"[mundo] {r.What}: se reintenta"); return; }
                        Release(r.Key);
                        try { r.Failed?.Invoke(); } catch (Exception ex) { Log.Error($"[mundo] fallo de {r.What}", ex); }
                        return;
                    }
                    Release(r.Key);
                    try { r.Apply(j); }
                    catch (Exception ex) { Log.Error($"[mundo] aplicando {r.What}", ex); }
                });
            });
        }

        private static void Release(string key) { if (key != null) lock (Queue) Reserved.Remove(key); }

        private static bool LaunchQueued()
        {
            lock (Queue)
            {
                if (Queue.Count == 0 || !Ready) return false;
                var r = Queue[0];
                Queue.RemoveAt(0);
                if (r.Session != _session) { if (r.Key != null) Reserved.Remove(r.Key); return true; }
                Launch(r);
                return true;
            }
        }

        /// <summary>Call often, from the game thread.</summary>
        public static void Pump()
        {
            if (Campaign.Current == null) { while (Pending.TryDequeue(out _)) { } return; }
            if (!OnMap()) return;
            if (Pending.TryDequeue(out var a)) { a(); Memory.SaveFiles.Flush(heroes: false); return; }
            if (LaunchQueued()) return;
            if (CanCall) TryReact();
        }

        // ============================================================ world state for prompts

        public static float Fatigue(Kingdom k) => k != null && State.WarFatigue.TryGetValue(k.StringId, out var f) ? f : 0f;

        public static string FatigueWord(Kingdom k)
        {
            float f = Fatigue(k);
            return f < 15 ? "fresh" : f < 35 ? "strained" : f < 60 ? "weary" : f < 85 ? "exhausted" : "at breaking point";
        }

        public static int GetStance(string from, string to) => State.Stance.TryGetValue(from + "|" + to, out var v) ? v : 0;
        public static void ChangeStance(string from, string to, int delta)
        {
            var key = from + "|" + to;
            State.Stance[key] = Math.Max(-100, Math.Min(100, GetStance(from, to) + delta));
        }

        public static string StanceWord(int v) => v <= -60 ? "hatred" : v <= -25 ? "hostile" : v < -5 ? "cold" : v <= 5 ? "neutral" : v < 25 ? "cordial" : v < 60 ? "friendly" : "close";

        /// <summary>Hard facts about the kingdoms: the true state of the world right now.</summary>
        public static string DescribeKingdoms(Kingdom focus = null)
        {
            var sb = new StringBuilder();
            foreach (var k in Kingdoms.OrderByDescending(x => x.CurrentTotalStrength))
            {
                var towns = k.Fiefs.Where(f => f.IsTown).Select(f => f.Name.ToString()).ToList();
                int castles = k.Fiefs.Count(f => f.IsCastle);
                // each war with how long it has lasted, and the peaces of the last year: without them a new war reads
                // like an old one and a kingdom founded last season reads like one that has lost everything
                var wars = Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).Select(o => o.StringId + Diplomacy.WarSpan(k, o)).ToList();
                var allies = Kingdoms.Where(o => o != k && Diplomacy.Allied(k, o)).Select(o => o.StringId).ToList();
                var trade = Kingdoms.Where(o => o != k && Diplomacy.HasTrade(k, o)).Select(o => o.StringId).ToList();
                var peaces = Diplomacy.RecentPeaces(k, Math.Max(1, CampaignTime.DaysInYear));
                sb.Append($"- {k.Name} [id: {k.StringId}]{(k == Hero.MainHero.MapFaction ? " (the realm of " + Hero.MainHero.Name + ")" : "")}: ruler {k.Leader.Name}; {Diplomacy.Age(k)}; ")
                  .Append($"strength {(int)k.CurrentTotalStrength}; towns ({towns.Count}): {(towns.Count > 0 ? string.Join(", ", towns.Take(8)) : "none")}; castles {castles}; ")
                  .Append($"AT WAR with: {(wars.Count > 0 ? string.Join(", ", wars) : "nobody")}; ")
                  .Append(peaces.Count > 0 ? $"peace made lately with: {string.Join(", ", peaces)}; " : "")
                  .Append(allies.Count > 0 ? $"allied with: {string.Join(", ", allies)}; " : "")
                  .Append(trade.Count > 0 ? $"trade agreement with: {string.Join(", ", trade)}; " : "")
                  .Append($"war weariness: {FatigueWord(k)}");
                if (State.KingdomTaxPercent.TryGetValue(k.StringId, out var tax) && tax != 0) sb.Append($"; extra crown tax {tax:+0;-0}%");
                var laws = Diseases.DescribeLaws(k); if (laws.Length > 0) sb.Append("; " + laws);
                var sick = Diseases.DescribeFor(k); if (sick.Length > 0) sb.Append("; " + sick);
                if (focus != null && focus != k) sb.Append($"; your stance towards them: {StanceWord(GetStance(focus.StringId, k.StringId))}");
                sb.AppendLine(".");
            }
            return sb.ToString().TrimEnd();
        }

        public static string DescribeRecentNews(int max)
        {
            double now = CampaignTime.Now.ToDays;
            var items = NewsStore.All.Where(n => n.Importance >= 2 && now - n.Day <= 20 && n.Type != "rumor").OrderByDescending(n => n.Day).Take(max).ToList();
            return items.Count == 0 ? "(nothing of note)" : string.Join("\n", items.Select(n => $"- {(int)(now - n.Day)} day(s) ago: {n.Text}"));
        }

        public static string DescribeActiveEvents(int max)
        {
            double now = CampaignTime.Now.ToDays;
            var ev = State.Events.Where(e => now - e.Day <= S.Get.WorldEventLifespanDays).OrderByDescending(e => e.Day).Take(max).ToList();
            return ev.Count == 0 ? "(none)" : string.Join("\n", ev.Select(e => $"- [{e.Type}, {(int)(now - e.Day)} day(s) ago] {(string.IsNullOrWhiteSpace(e.SummaryEn) ? e.Title : e.SummaryEn)}"));
        }

        // ============================================================ event generator

        private static readonly Tpl TEventGen = new Tpl("dynamic_events_generator/generator.txt",
@"You are the chronicler of Calradia, the medieval world of Mount & Blade II: Bannerlord. From the real state of the world below, write {count} new development(s) that could plausibly be happening right now: political, military, economic, social or mysterious. They become news that people talk about and that rulers may react to.

Rules:
- Ground every event in the data below: a real kingdom, real towns, a real war, a real recent event. Structure: cause (from the data) → what is happening → what it threatens or promises. Continue existing stories when it makes sense.
- NEVER state as done anything only the world itself can do: a war declared, a peace signed, a town changing hands, a ruler or lord dead, a clan expelled, a law or tax enacted. You may write pressure, rumours, accusations, demands, unrest, shortages, omens, plots: the real outcome, if any, will come later from the rulers.
- Do not involve the person named as ""the realm of ..."" personally unless the data does.
- Mind the dates in the data. A kingdom marked NEWLY FOUNDED is young and growing: holding little land is its beginning, so never write it as collapsing, dying or fallen. Each war says how long it has lasted and each recent peace when it was made: a war of a few days is new, one of years is old and wearing, and where a peace was made lately the war before it is over.
- Use readable names in title and text, never ids. Never put a double-quote character inside a text value; use «» if you must quote.
- importance: 1 = one town or village talks about it, 2 = nobles of the realm, 3 = the whole realm, 4 = the whole world (rare).
- kingdom_engagement: for events that call for a response from a crown, the daily chance (0-100) that the ruler of that kingdom speaks out about it. Only kingdoms listed in kingdoms_involved. Leave empty for events nobody needs to answer.
- continues (optional): the stories below are still open. When the state of the world has plainly moved one of them forward (it worsened, it was settled, someone answered it), write its next chapter and set continues to its id. Otherwise leave it empty and write something new. At most one continuation per answer.
Open stories:
{open}
- economic_effects (optional, only when the event really touches daily life in a real town, castle or village): small daily changes for a number of days. Limits per day: prosperity ±{max_prosperity}, food ±{max_food}, security ±{max_security}, loyalty ±{max_loyalty}; duration up to {max_days} days. For a town or village you may also set price_percent (±{max_price}): how much dearer or cheaper its market is meanwhile (a shortage, a panic, a glut), and price_of to say which goods: food, goods, weapons, armor, horses, or empty for everything. A sudden blow that lands at once (a sack, a fire, a flood, a plague arriving, a great fair) goes in the _now fields, each at most {max_now} times the daily limit; most events have none. To touch every town of a realm use kingdom (its id) and leave settlement empty: only for things that truly reach the whole realm, and with small numbers.
- clan_effects (optional): when the event is about the name of ONE specific clan being stained or raised (a scandal, an outrage committed by one of its members, a feat that brings it honour), how its income changes while people remember: income_percent from -{max_clan}% to +{max_clan}%, for up to {max_days} days, with the reason in a few words. Merchants and allies avoid a disgraced house; they court an honoured one.
- secret (optional, for about one event in three): the hidden truth behind this event that only a few people know and that the public version leaves out (who really paid for it, who is lying, what was covered up). ""known_by"": 1 to 3 REAL named nobles from the data who would know. ""access"": low | medium | high (how hard it is to get it out of them).
- clan_rebellion (rare, optional): the name of ONE vassal clan that rises against its own crown right now. Only when the data shows a real break between that clan and its ruler; never the ruling clan and never a clan of ""the realm of ..."". The world will carry it out.
- disease (rare, optional): a sickness breaking out in ONE real town or village, when the season, a siege, hunger or crowding make it plausible. severity 1 (mild) to {max_severity}. You may also give how it behaves, or leave those fields out and they follow from the severity: spread_rate 0.1 to {max_spread} (how catching it is), death_chance 0 to {max_death} (daily risk for the gravely ill; 0 for anything nobody dies of), combat_modifier {min_combat} to 1 and map_speed_modifier {min_speed} to 1 (how much strength it leaves a fighter and a marching party), morale_penalty {max_morale} to 0, skill_penalty {max_skill} to 0.
- claims: every allegiance, war and alliance your text relies on, so the world can check it before it is published: { ""who"": a lord or a clan, ""kingdom"": the id of the kingdom they are SUBJECTS of, or ""none"", ""mercenary"": true when they only serve that kingdom for pay }, { ""war"": [id, id] }, { ""allies"": [id, id] }. An event with a claim that contradicts the data below is thrown away. Take every allegiance from the list of who serves whom: a person's culture says where they were born, never whom they serve; a mercenary clan is neither kin nor subject of the crown that pays it and matters to it only while the contract lasts; minor clans answer to no crown.
- title: 3-8 words in {language}. text: {min_len}-{max_len} characters in {language}, written like a chronicle. summary: the same in ONE English sentence.

Answer ONLY with JSON:
{ ""events"": [ { ""type"": ""political|military|economic|social|mysterious"", ""title"": ""..."", ""text"": ""..."", ""summary"": ""..."", ""importance"": 1-4, ""continues"": """",
  ""kingdoms_involved"": [""kingdom ids""], ""settlements_involved"": [""settlement names""], ""kingdom_engagement"": { ""kingdom_id"": 0-100 },
  ""claims"": [ { ""who"": ""name"", ""kingdom"": ""id or none"", ""mercenary"": false }, { ""war"": [""id"", ""id""] }, { ""allies"": [""id"", ""id""] } ],
  ""economic_effects"": [ { ""settlement"": ""name"", ""prosperity_per_day"": 0, ""food_per_day"": 0, ""security_per_day"": 0, ""loyalty_per_day"": 0, ""price_percent"": 0, ""price_of"": """", ""prosperity_now"": 0, ""food_now"": 0, ""security_now"": 0, ""loyalty_now"": 0, ""kingdom"": """", ""duration_days"": 0, ""reason"": ""..."" } ],
  ""clan_effects"": [ { ""clan"": ""clan name"", ""income_percent"": 0, ""duration_days"": 0, ""reason"": ""..."" } ],
  ""secret"": { ""text"": ""one or two English sentences"", ""text_local"": ""the same in {language}"", ""about"": [""names""], ""known_by"": [""names""], ""access"": ""high"" },
  ""clan_rebellion"": ""clan name or empty"", ""disease"": { ""settlement"": ""name"", ""name"": ""name of the sickness in {language}"", ""name_en"": ""the same in English"", ""severity"": 1, ""duration_days"": 30, ""spread_rate"": 0.3, ""death_chance"": 0, ""combat_modifier"": 0.9, ""map_speed_modifier"": 0.9, ""morale_penalty"": -5, ""skill_penalty"": -6 } } ] }
Leave out economic_effects, clan_effects, secret, clan_rebellion and disease when they do not apply.");

        public static void Touch() { var _ = TEventGen.Key + TReact.Key + TFactsBlock.Key; Diplomacy.Touch(); Laws.Touch(); Facts.Touch(); }

        // REAL game events that just happened and deserve a world reaction (not saved, they only matter right now).
        // Each one carries its fact sheet: the news line alone does not say whose man anyone is.
        private static readonly List<Trigger> Triggers = new List<Trigger>();

        private static readonly Tpl TReact = new Tpl("dynamic_events_generator/react_to_real_event.txt",
@"## THIS HAS JUST REALLY HAPPENED
{triggers}

## HARD FACTS ABOUT IT
Built by the world itself. They are the only truth about whom these people and places belong to and who is fighting whom:
{facts}

How to read them:
- Culture is where someone was born. It is never an allegiance: a Nord by birth sworn to the Aserai is an Aserai vassal, and a Nord in no kingdom is nobody's man.
- A mercenary clan is hired. It is neither kin nor subject of the kingdom that pays it, and it matters to that kingdom only while it is in its pay. A clan with no kingdom matters to no crown.
- Do not state any allegiance, war, alliance or kinship that is not in these facts or in the kingdom data above. If a fact is missing, leave it out; do not guess it from a name or a culture.
- If the facts do not make this matter to anyone powerful, write it as local talk: importance 1, kingdoms_involved and kingdom_engagement empty.

Write ONE development that follows directly from it: how the people, the nobles, the merchants or the other crowns take it, what it stirs up, what it threatens next. It is a consequence or a reaction, not a retelling of the fact itself (everyone already knows the fact).
kingdoms_involved and kingdom_engagement may only name kingdoms from this list, the ones the facts tie to what happened: {allowed}. Any other kingdom is discarded. Give kingdom_engagement only to the crowns that would truly want to answer.");

        // For a reaction template edited before the facts existed: the facts still go in
        private static readonly Tpl TFactsBlock = new Tpl("dynamic_events_generator/react_facts_fallback.txt",
@"## HARD FACTS ABOUT IT (the only truth about whom these people and places belong to; culture is not allegiance, and a mercenary clan is neither kin nor subject of the kingdom that pays it)
{facts}
kingdoms_involved and kingdom_engagement may only name: {allowed}.");

        /// <summary>Every real game news item comes through here. Big ones trigger a world reaction as soon as possible.</summary>
        public static void OnRealNews(NewsItem n)
        {
            var cfg = S.Get;
            if (!Enabled || !cfg.ReactiveEvents || n == null || Campaign.Current == null) return;
            string t = n.Type ?? "";
            if (t == "statement" || t == "custom" || t.StartsWith("event_")) return;     // don't feed the AI's own writing back into itself
            if (n.NoReaction || n.Importance < cfg.ReactiveMinImportance) return;
            var trigger = Facts.ForNews(n);
            lock (Triggers)
            {
                if (Triggers.Count >= 5) Triggers.RemoveAt(0);
                Triggers.Add(trigger);
            }
        }

        private static bool _urgent;      // a player incident is waiting, so skip the gap between reactions

        /// <summary>
        /// Something the player did or said that gets people talking (a street fight, a death, an arrest, a big rumor they
        /// started). The world turns it into its own event, with local effects if it deserves them.
        /// </summary>
        public static void OnIncident(string whatEn, Settlement where, bool blood)
        {
            try
            {
                var cfg = S.Get;
                if (!Enabled || !cfg.ReactiveEvents || !cfg.IncidentEvents || string.IsNullOrWhiteSpace(whatEn) || Campaign.Current == null) return;
                string t = whatEn.Trim() + (where != null ? $" [This happened at {where.Name} ({where.StringId}), of {where.MapFaction?.Name}." : " [")
                         + " It is NOT yet common knowledge: write the event as the news of it spreading — what people there are saying and fearing — not as a reaction to something everyone knows. "
                         + "Keep it LOCAL (importance 1, or 2 if a noble was hurt, killed or arrested) unless it truly concerns a whole realm."
                         + (blood && where != null && (where.IsTown || where.IsCastle || where.IsVillage) ? $" Blood was shed: give {where.Name} a modest, short-lived effect (security and/or prosperity) with the reason in a few words." : " No economic effect unless it clearly follows.")
                         + " If the one who did it is known by name and it is an outrage (blood, sacrilege, a noble struck down), their clan's name may suffer: a clan_effects entry for that clan."
                         + " Say only what the witnesses could know: if nobody knows who did it, the event must not name them.]";
                var trigger = Facts.ForIncident(t, where);
                lock (Triggers)
                {
                    if (Triggers.Count >= 5) Triggers.RemoveAt(0);
                    Triggers.Add(trigger);
                }
                _urgent = true;
                Log.Info("[mundo] suceso tuyo en cola: " + (whatEn.Length > 120 ? whatEn.Substring(0, 120) + "…" : whatEn));
            }
            catch (Exception ex) { Log.Error("Suceso del jugador", ex); }
        }

        private static void TryReact()
        {
            var cfg = S.Get;
            if (!Enabled || !cfg.ReactiveEvents) { lock (Triggers) Triggers.Clear(); return; }
            double hour = CampaignTime.Now.ToHours;
            if (State.LastReactiveHour > hour) State.LastReactiveHour = -1;     // loaded an older save
            if (!_urgent && State.LastReactiveHour >= 0 && hour - State.LastReactiveHour < cfg.ReactiveMinHoursBetween) return;
            _urgent = false;
            List<Trigger> take;
            lock (Triggers)
            {
                if (Triggers.Count == 0) return;
                take = Triggers.ToList();
                Triggers.Clear();
            }
            State.LastReactiveHour = hour;
            GenerateEvents(false, take);
        }

        /// <summary>The reaction failed, so its events go back in the queue (at most twice in a row).</summary>
        private static void RequeueTriggers(List<Trigger> take)
        {
            if (take == null || take.Count == 0 || ++_reactRetries > 2) return;
            lock (Triggers)
            {
                foreach (var t in take.AsEnumerable().Reverse()) if (!Triggers.Contains(t)) Triggers.Insert(0, t);
                while (Triggers.Count > 5) Triggers.RemoveAt(Triggers.Count - 1);
            }
        }

        public static void GenerateEvents(bool forced = false, List<Trigger> reactTo = null)
        {
            var cfg = S.Get;
            if (!Enabled || Campaign.Current == null) return;
            if (!forced && !CanCall) return;
            bool reactive = reactTo != null && reactTo.Count > 0;
            if (IsReserved(reactive ? "react" : "events")) return;     // already requested, still waiting for it to arrive or apply
            int count = reactive ? 1 : cfg.WorldEventsPerGeneration;
            var sys = new StringBuilder();
            sys.AppendLine(TEventGen.Format("count", count, "language", Lang, "min_len", cfg.WorldEventMinLength, "max_len", cfg.WorldEventMaxLength,
                "open", OpenStories(), "max_now", InstantFactor,
                "max_prosperity", cfg.EffectMaxProsperityPerDay, "max_food", cfg.EffectMaxFoodPerDay, "max_security", cfg.EffectMaxSecurityPerDay,
                "max_loyalty", cfg.EffectMaxLoyaltyPerDay, "max_days", cfg.EffectMaxDays, "max_severity", cfg.DiseaseMaxSeverity, "max_clan", cfg.ClanEffectMaxPercent, "max_price", cfg.EffectMaxPricePercent,
                "max_spread", cfg.DiseaseMaxSpreadRate.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), "max_death", cfg.DiseaseMaxDeathChance.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                "min_combat", cfg.DiseaseMinCombatModifier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), "min_speed", cfg.DiseaseMinMapSpeedModifier.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture),
                "max_morale", cfg.DiseaseMaxMoralePenalty, "max_skill", cfg.DiseaseMaxPhysicalSkillPenalty));
            var lore = WorldLore.Text;
            if (!string.IsNullOrWhiteSpace(lore)) { sys.AppendLine(); sys.AppendLine("## The world"); sys.AppendLine(lore); }
            sys.AppendLine();
            sys.AppendLine(Facts.Today());
            sys.AppendLine("## Kingdoms (technical truth, right now)");
            sys.AppendLine(DescribeKingdoms());
            sys.AppendLine("## Who serves whom (exact, right now; nobody else belongs to these kingdoms)");
            // what happened may be hours old by now: who is at war with whom is read again, not taken from then
            if (reactive) foreach (var tr in reactTo) Facts.Refresh(tr);
            // the kingdoms of what happened are never the ones left out when the lists run over their budget
            var tied = reactive ? new HashSet<string>(reactTo.SelectMany(tr => tr.Kingdoms)) : null;
            sys.AppendLine(Facts.Roster(tied));
            { string dead = Facts.RecentDead(); if (dead.Length > 0) sys.AppendLine(dead); }
            sys.AppendLine(Facts.CalendarAndMap(tied));
            sys.AppendLine("## What really happened lately");
            sys.AppendLine(DescribeRecentNews(14));
            sys.AppendLine("## Developments already in the air (do not repeat them; you may continue one)");
            sys.AppendLine(DescribeActiveEvents(8));
            var pend = Diplomacy.DescribeAllProposals();
            if (pend.Length > 0) { sys.AppendLine("## Pending proposals between crowns (do not narrate their outcome)"); sys.AppendLine(pend); }

            // Kingdoms a reaction may involve: those of the people and places in what happened, and whoever is at war or allied with them
            HashSet<string> allowed = null;
            if (reactive)
            {
                var facts = new List<string>();
                foreach (var tr in reactTo) foreach (var f in tr.Facts) if (!facts.Contains(f)) facts.Add(f);
                try { allowed = FactCheck.Connected(reactTo.SelectMany(tr => tr.Kingdoms), Facts.BuildModel()); }
                catch (Exception ex) { Log.Error("[mundo] reinos ligados a un suceso", ex); allowed = new HashSet<string>(reactTo.SelectMany(tr => tr.Kingdoms)); }
                string factText = facts.Count > 0 ? string.Join("\n", facts) : "(nobody of note and no kingdom is tied to it)";
                string allowedText = allowed.Count > 0 ? string.Join(", ", allowed) : "(none: this is local talk)";
                sys.AppendLine();
                bool hasFacts = TReact.Text.Contains("{facts}");
                sys.AppendLine(TReact.Format("triggers", string.Join("\n", reactTo.Select(x => "- " + x.Text)), "facts", factText, "allowed", allowedText));
                if (!hasFacts) sys.AppendLine(TFactsBlock.Format("facts", factText, "allowed", allowedText));
            }
            Ask(reactive ? L.T("AIL_ce8115b0", "reacción a un suceso real") : "generar eventos",
                new List<ChatMessage> { new ChatMessage("system", sys.ToString()), new ChatMessage("user", reactive ? "Write the development now." : "Write the events now.") },
                count * 800 + 400, j =>
                {
                    if (reactive) _reactRetries = 0;
                    else
                    {
                        // The schedule is consumed when the batch arrives. Not like clockwork: the next gap is 60% to 140% of the setting
                        double d = CampaignTime.Now.ToDays;
                        State.LastEventGenerationDay = d;
                        State.NextEventGenerationDay = d + S.Get.WorldEventsIntervalDays * (0.6 + Rng.NextDouble() * 0.8);
                    }
                    ApplyEvents(j, S.Get.FactCheckEvents ? allowed : null);
                },
                key: reactive ? "react" : "events", failed: reactive ? (Action)(() => RequeueTriggers(reactTo)) : null);
        }

        private static List<FactClaim> ParseClaims(JToken t)
        {
            var list = new List<FactClaim>();
            if (!(t is JArray arr)) return list;
            foreach (var c in arr.OfType<JObject>().Take(20))
            {
                try
                {
                    if (c["war"] is JArray w && w.Count >= 2) list.Add(new FactClaim { Kind = "war", A = w[0]?.ToString(), B = w[1]?.ToString() });
                    else if (c["allies"] is JArray al && al.Count >= 2) list.Add(new FactClaim { Kind = "allies", A = al[0]?.ToString(), B = al[1]?.ToString() });
                    else if (c["who"] != null && c["who"].Type == JTokenType.String)
                        list.Add(new FactClaim
                        {
                            Who = (string)c["who"], Kingdom = c["kingdom"] == null || c["kingdom"].Type == JTokenType.Null ? "" : c["kingdom"].ToString(),
                            Mercenary = string.Equals(c["mercenary"]?.ToString(), "true", StringComparison.OrdinalIgnoreCase),
                        });
                }
                catch { }
            }
            return list;
        }

        /// <summary>
        /// What the AI wrote, checked against the world before it is published. Returns what is wrong with it, or null.
        /// <paramref name="model"/> is built on first use and reused for the rest of the batch.
        /// </summary>
        public static string Contradiction(ref FactModel model, JToken claims, params string[] texts)
        {
            if (!S.Get.FactCheckEvents) return null;
            try
            {
                model = model ?? Facts.BuildModel();
                string wrong = FactCheck.CheckClaims(ParseClaims(claims), model);
                foreach (var text in texts) wrong = wrong ?? FactCheck.CheckSummary(text, model);
                return wrong;
            }
            catch (Exception ex) { Log.Error("[mundo] comprobando hechos", ex); return null; }
        }

        /// <param name="allowed">For a reaction to a real event: the only kingdoms it may involve. null for the scheduled batch.</param>
        private static void ApplyEvents(JObject j, HashSet<string> allowed = null)
        {
            if (!(j["events"] is JArray arr)) return;
            bool continued = false;
            FactModel model = null;
            foreach (var x in arr.OfType<JObject>().Take(S.Get.WorldEventsPerGeneration))
            {
                var e = new WorldEvent
                {
                    Id = NewId("ev"), Day = CampaignTime.Now.ToDays,
                    Type = Clean((string)x["type"], 20).ToLowerInvariant(), Title = Clean((string)x["title"], 90), Text = Clean((string)x["text"], 1400),
                    SummaryEn = Clean((string)x["summary"], 350), Importance = Math.Max(1, Math.Min(4, (int?)x["importance"] ?? 2)),
                };
                if (e.Title.Length < 3 || e.Text.Length < 20) continue;
                // A stated allegiance, war or alliance that the game denies: the event is not published
                string wrong = Contradiction(ref model, x["claims"], e.SummaryEn, e.Text);
                // Someone dead written as acting, ruling or being plotted against
                if (wrong == null && S.Get.FactCheckEvents)
                {
                    try { wrong = FactCheck.CheckDead(ParseClaims(x["claims"]), e.SummaryEn, model ?? (model = Facts.BuildModel())); }
                    catch (Exception ex) { Log.Error("[mundo] comprobando muertos", ex); }
                }
                if (wrong != null) { Log.Warn($"[mundo] evento descartado por contradecir el mundo: «{e.Title}» {wrong}"); continue; }
                // A next chapter of an open story keeps its id (so its effects and its news stay tied to it) and its past
                string cont = ((string)(x["continues"]?.Type == JTokenType.String ? x["continues"] : null) ?? "").Trim();
                if (cont.Length > 0 && !continued)
                {
                    var old = State.Events.FirstOrDefault(o => o.Id == cont && o.Source == "world" && e.Day - o.Day <= OpenStoryDays);
                    if (old != null)
                    {
                        continued = true;
                        e.Id = old.Id;
                        e.History = old.History ?? new List<string>();
                        e.History.Add((int)old.Day + "|" + (string.IsNullOrWhiteSpace(old.SummaryEn) ? old.Title : old.SummaryEn));
                        while (e.History.Count > 5) e.History.RemoveAt(0);
                        foreach (var k in old.Kingdoms) if (!e.Kingdoms.Contains(k)) e.Kingdoms.Add(k);
                        foreach (var st0 in old.Settlements) if (!e.Settlements.Contains(st0)) e.Settlements.Add(st0);
                        // rulers already due to speak about it still have their say on the new chapter
                        foreach (var kv in old.Engagement) if (!e.Engagement.ContainsKey(kv.Key)) e.Engagement[kv.Key] = kv.Value;
                        State.Events.Remove(old);
                        NewsStore.Delete(old.Id);
                        Log.Info($"[mundo] sigue la historia «{old.Title}» ({e.History.Count} capítulo(s) anteriores)");
                    }
                }
                var strangers = new List<string>();
                if (x["kingdoms_involved"] is JArray ks)
                {
                    var named = ks.Select(k => FindKingdom(k?.ToString())?.StringId).Where(id => id != null).ToList();
                    foreach (var id in allowed == null ? named : FactCheck.FilterKingdoms(named, allowed, strangers))
                        if (!e.Kingdoms.Contains(id)) e.Kingdoms.Add(id);
                }
                if (x["settlements_involved"] is JArray ss)
                    foreach (var s in ss) { var st = OrderSystem.FindSettlement(s?.ToString()); if (st != null && !e.Settlements.Contains(st.StringId)) e.Settlements.Add(st.StringId); }
                if (x["kingdom_engagement"] is JObject eng)
                    foreach (var p in eng.Properties())
                    {
                        var kg = FindKingdom(p.Name);
                        if (kg == null || kg.Leader == Hero.MainHero) continue;      // the player speaks when they want to
                        // a crown the real event has nothing to do with gets no turn to speak about it
                        if (allowed != null && FactCheck.FilterKingdoms(new[] { kg.StringId }, allowed, strangers).Count == 0) continue;
                        if (!e.Kingdoms.Contains(kg.StringId)) e.Kingdoms.Add(kg.StringId);
                        int chance = 0;
                        try { chance = (int?)p.Value ?? 0; } catch { }
                        e.Engagement[kg.StringId] = Math.Max(0, Math.Min(100, chance));
                    }
                if (strangers.Count > 0) Log.Info($"[mundo] «{e.Title}»: reinos ajenos al suceso real, descartados: {string.Join(", ", strangers)}");
                // A reaction left with no kingdom is local talk. With importance 3 or 4 and no kingdom the news would
                // reach every court in the world, so it is held at 2: only the place it names hears of it.
                if (allowed != null && e.Kingdoms.Count == 0 && e.Importance > 2)
                {
                    Log.Info($"[mundo] «{e.Title}»: sin reinos ligados al suceso, importancia {e.Importance} → 2");
                    e.Importance = 2;
                }
                Publish(e);
                if (x["economic_effects"] is JArray fx) foreach (var f in fx.OfType<JObject>().Take(4)) AddEffect(e, f, allowed);
                if (x["clan_effects"] is JArray cfx) foreach (var f in cfx.OfType<JObject>().Take(2)) AddClanEffect(e, f);
                // What's behind the event, known only to a few
                if (S.Get.StorySecrets && x["secret"] is JObject sec)
                    StorySecrets.Add(sec, e.Kingdoms.Select(FindKingdom).FirstOrDefault(k => k != null), "event");
                if (x["disease"] is JObject dis) Diseases.FromEvent(dis);
                var rebel = (string)(x["clan_rebellion"]?.Type == JTokenType.String ? x["clan_rebellion"] : null);
                if (!string.IsNullOrWhiteSpace(rebel)) Diplomacy.Rebellion(rebel);
            }
        }

        private const int OpenStoryDays = 45, InstantFactor = 6;

        /// <summary>Recent world events the chronicler may carry on, with what was told before.</summary>
        private static string OpenStories()
        {
            try
            {
                double now = CampaignTime.Now.ToDays;
                var open = State.Events.Where(e => e.Source == "world" && now - e.Day <= OpenStoryDays).OrderByDescending(e => e.Day).Take(6).ToList();
                if (open.Count == 0) return "(none)";
                var sb = new StringBuilder();
                foreach (var e in open)
                {
                    sb.Append($"- id {e.Id}, {(int)(now - e.Day)} day(s) ago: {(string.IsNullOrWhiteSpace(e.SummaryEn) ? e.Title : e.SummaryEn)}");
                    if (e.History != null && e.History.Count > 0)
                        sb.Append(" Before that: " + string.Join(" Then: ", e.History.Select(h => h.Substring(h.IndexOf('|') + 1))));
                    sb.AppendLine();
                }
                return sb.ToString().TrimEnd();
            }
            catch { return "(none)"; }
        }

        public static string Clean(string s, int max)
        {
            s = (s ?? "").Replace("\"", "»").Replace("{", "(").Replace("}", ")").Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>Stores the event and turns it into news, so NPCs learn about it depending on their reach.</summary>
        public static void Publish(WorldEvent e)
        {
            State.Events.Add(e);
            while (State.Events.Count > 150) State.Events.RemoveAt(0);
            NewsStore.Add(new NewsItem
            {
                Id = e.Id,      // effects store EventId = e.Id, and Books looks them up by the news id
                Type = "event_" + e.Type, Importance = e.Importance, Text = string.IsNullOrWhiteSpace(e.SummaryEn) ? e.Title : e.SummaryEn,
                TitleEs = e.Title, TextEs = e.Text, Kingdoms = e.Kingdoms.ToList(), SettlementId = e.Settlements.FirstOrDefault(),
            });
            Log.Info($"[mundo] evento [{e.Type}, imp {e.Importance}]: {e.Title}");
            if (e.Importance >= S.Get.WorldEventNotifyImportance) Say(L.F("AIL_ee278059", $"Se cuenta por Calradia: {e.Title}"), Colors.Cyan);
        }

        private static void AddClanEffect(WorldEvent e, JObject f)
        {
            var cfg = S.Get;
            if (!cfg.EnableEconomicEffects || !cfg.EnableClanEffects) return;
            string name = Norm((string)f["clan"]);
            if (name.Length < 2) return;
            var clan = Clan.All.FirstOrDefault(c => c != null && !c.IsEliminated && (Norm(c.Name.ToString()) == name || Norm(c.StringId) == name));
            if (clan == null) return;
            int max = cfg.ClanEffectMaxPercent;
            float pct = Math.Max(-max, Math.Min(max, (float?)f["income_percent"] ?? 0f));
            if (Math.Abs(pct) < 1f) return;
            State.Effects.RemoveAll(x => x.ClanId == clan.StringId && x.EventId == e.Id);
            State.Effects.Add(new EconomicEffect
            {
                ClanId = clan.StringId, IncomePercent = pct, EventId = e.Id, Reason = Clean((string)f["reason"], 120),
                UntilDay = CampaignTime.Now.ToDays + Math.Max(1, Math.Min(cfg.EffectMaxDays, (int?)f["duration_days"] ?? 14)),
            });
            Log.Info($"[mundo] efecto en el clan {clan.Name}: ingresos {pct:+0;-0}% ");
            if (clan == Clan.PlayerClan) Say(L.F("AIL_c81ffc1c", $"Tu clan lo nota: ingresos {pct:+0;-0} % mientras dure ({Clean((string)f["reason"], 120)})."), pct < 0 ? Colors.Red : Colors.Green);
        }

        /// <summary>How much current events push that place's market prices up or down (0.25 = +25%).</summary>
        /// <summary>Which of our price groups an item falls in.</summary>
        private static string PriceGroup(ItemObject item)
        {
            if (item == null) return "";
            if (item.IsFood) return "food";
            if (item.HasHorseComponent) return "horses";
            if (item.HasWeaponComponent) return "weapons";
            if (item.HasArmorComponent) return "armor";
            return item.IsTradeGood ? "goods" : "";
        }

        public static float PriceFactor(Settlement st, ItemObject item = null)
        {
            if (st == null || State.Effects.Count == 0) return 0f;
            double now = CampaignTime.Now.ToDays;
            float sum = 0f;
            string group = null;
            foreach (var fx in State.Effects)
            {
                if (fx.PricePercent == 0f || fx.SettlementId != st.StringId || fx.UntilDay <= now) continue;
                if (!string.IsNullOrEmpty(fx.PriceCategory))
                {
                    if (group == null) group = PriceGroup(item);
                    if (group != fx.PriceCategory) continue;
                }
                sum += fx.PricePercent;
            }
            int max = S.Get.EffectMaxPricePercent;
            return Math.Max(-max, Math.Min(max, sum)) / 100f;
        }

        /// <summary>How much current events push that clan's income up or down (0.2 = +20%), and why.</summary>
        public static float ClanIncomeFactor(Clan clan, out string reason)
        {
            reason = "";
            if (clan == null || State.Effects.Count == 0) return 0f;
            double now = CampaignTime.Now.ToDays;
            float sum = 0f;
            foreach (var fx in State.Effects)
            {
                if (fx.ClanId != clan.StringId || fx.UntilDay <= now) continue;
                sum += fx.IncomePercent;
                if (reason.Length == 0) reason = fx.Reason ?? "";
            }
            int max = S.Get.ClanEffectMaxPercent;
            return Math.Max(-max, Math.Min(max, sum)) / 100f;
        }

        private static readonly HashSet<string> PriceCategories = new HashSet<string> { "food", "goods", "weapons", "armor", "horses" };

        /// <param name="allowed">For a reaction to a real event: the only kingdoms a realm-wide effect may fall on. null for the scheduled batch.</param>
        private static void AddEffect(WorldEvent e, JObject f, HashSet<string> allowed = null)
        {
            var cfg = S.Get;
            if (!cfg.EnableEconomicEffects) return;
            // A whole realm: the same effect in each of its towns, with no sudden blow
            string kname = ((string)(f["kingdom"]?.Type == JTokenType.String ? f["kingdom"] : null) ?? "").Trim();
            if (kname.Length > 0 && string.IsNullOrWhiteSpace((string)f["settlement"]))
            {
                var kg = FindKingdom(kname);
                if (kg == null) return;
                if (allowed != null && !allowed.Contains(kg.StringId)) { Log.Info($"[mundo] «{e.Title}»: efecto sobre {kg.Name} descartado, reino ajeno al suceso real"); return; }
                foreach (var town in kg.Settlements.Where(s0 => s0.IsTown).Take(14))
                {
                    var one = (JObject)f.DeepClone();
                    one["settlement"] = town.StringId; one["kingdom"] = "";
                    foreach (var k in new[] { "prosperity_now", "food_now", "security_now", "loyalty_now" }) one.Remove(k);
                    AddEffectAt(e, one, town);
                }
                return;
            }
            var st = OrderSystem.FindSettlement((string)f["settlement"]);
            if (st == null) return;
            AddEffectAt(e, f, st);
        }

        private static void AddEffectAt(WorldEvent e, JObject f, Settlement st)
        {
            var cfg = S.Get;
            float C(string k, int max) { float v = (float?)f[k] ?? 0f; return Math.Max(-max, Math.Min(max, v)); }
            var fx = new EconomicEffect
            {
                SettlementId = st.StringId, EventId = e.Id, Reason = Clean((string)f["reason"], 120),
                ProsperityPerDay = C("prosperity_per_day", cfg.EffectMaxProsperityPerDay), FoodPerDay = C("food_per_day", cfg.EffectMaxFoodPerDay),
                SecurityPerDay = C("security_per_day", cfg.EffectMaxSecurityPerDay), LoyaltyPerDay = C("loyalty_per_day", cfg.EffectMaxLoyaltyPerDay),
                UntilDay = CampaignTime.Now.ToDays + Math.Max(1, Math.Min(cfg.EffectMaxDays, (int?)f["duration_days"] ?? 7)),
                PricePercent = st.IsTown || st.IsVillage ? C("price_percent", cfg.EffectMaxPricePercent) : 0f,
            };
            string cat = ((string)(f["price_of"]?.Type == JTokenType.String ? f["price_of"] : null) ?? "").Trim().ToLowerInvariant();
            if (fx.PricePercent != 0 && PriceCategories.Contains(cat)) fx.PriceCategory = cat;
            // The sudden blow lands now, once, straight on the place
            try
            {
                float pNow = C("prosperity_now", cfg.EffectMaxProsperityPerDay * InstantFactor), fNow = C("food_now", cfg.EffectMaxFoodPerDay * InstantFactor);
                float sNow = C("security_now", cfg.EffectMaxSecurityPerDay * InstantFactor), lNow = C("loyalty_now", cfg.EffectMaxLoyaltyPerDay * InstantFactor);
                // a village has only its hearths: the other three have nowhere to land
                if (st.Town == null) { fNow = 0; sNow = 0; lNow = 0; }
                if (pNow != 0 || fNow != 0 || sNow != 0 || lNow != 0)
                {
                    if (st.Town != null)
                    {
                        if (pNow != 0) st.Town.Prosperity = Math.Max(0f, st.Town.Prosperity + pNow);
                        if (fNow != 0) st.Town.FoodStocks = Math.Max(0f, st.Town.FoodStocks + fNow);
                        if (sNow != 0) st.Town.Security = Math.Max(0f, Math.Min(100f, st.Town.Security + sNow));
                        if (lNow != 0) st.Town.Loyalty = Math.Max(0f, Math.Min(100f, st.Town.Loyalty + lNow));
                    }
                    else if (st.Village != null && pNow != 0) st.Village.Hearth = Math.Max(10f, st.Village.Hearth + pNow);
                    Log.Info($"[mundo] golpe en {st.Name}: prosperidad {pNow:+0.#;-0.#;0}, comida {fNow:+0.#;-0.#;0}, seguridad {sNow:+0.#;-0.#;0}, lealtad {lNow:+0.#;-0.#;0} ({fx.Reason})");
                    if (st.OwnerClan == Clan.PlayerClan)
                        Say(L.F("AIL_e7b1c514", $"{st.Name} lo sufre de golpe: {fx.Reason}"), pNow + fNow + sNow + lNow < 0 ? Colors.Red : Colors.Green);
                }
            }
            catch (Exception ex) { Log.Error("Efecto instantáneo", ex); }
            if (fx.ProsperityPerDay == 0 && fx.FoodPerDay == 0 && fx.SecurityPerDay == 0 && fx.LoyaltyPerDay == 0 && fx.PricePercent == 0) return;
            State.Effects.Add(fx);
            Log.Info($"[mundo] efecto en {st.Name}: prosperidad {fx.ProsperityPerDay:+0.#;-0.#;0}/día, comida {fx.FoodPerDay:+0.#;-0.#;0}, seguridad {fx.SecurityPerDay:+0.#;-0.#;0}, lealtad {fx.LoyaltyPerDay:+0.#;-0.#;0} ({fx.Reason})");
        }

        // ============================================================ ticks

        public static void OnDailyTick()
        {
            try
            {
                if (Campaign.Current == null) return;
                var cfg = S.Get;
                double now = CampaignTime.Now.ToDays;

                ApplyEffects(now);
                PayTributes(now);
                ApplyTaxPressure();
                Diseases.ApplyLaws();
                Diseases.OnDailyTick();
                Banished.OnDailyTick();
                NpcRomance.OnDailyTick();
                // each on its own: one that fails must not take the others with it
                try { TickFatigue(); } catch (Exception ex) { Log.Error("[mundo] cansancio de guerra", ex); }
                Diplomacy.TrackKingdoms();
                try { Diplomacy.OnDailyTick(now); } catch (Exception ex) { Log.Error("[diplomacia] tick diario", ex); }

                // Background batch: things brewing even when nothing big happened. Reactions to real events are separate (TryReact).
                if (Enabled && cfg.BackgroundEvents)
                {
                    if (State.NextEventGenerationDay < 0 || State.NextEventGenerationDay - now > cfg.WorldEventsIntervalDays * 2)
                        State.NextEventGenerationDay = now + 2;      // first one after two days
                    else if (now >= State.NextEventGenerationDay) GenerateEvents();
                }
            }
            catch (Exception ex) { Log.Error("[mundo] tick diario", ex); }
        }

        // ---- daily effects inside the game's own calculation (see Patches\TownEffectsPatch.cs)
        public const int StatProsperity = 0, StatFood = 1, StatSecurity = 2, StatLoyalty = 3;
        // Last day the game ran each calculation through our hook. If it stops (another mod replaced the model),
        // the effects are added by hand again so they aren't lost.
        private static readonly double[] ModelDay = { -1, -1, -1, -1 };

        public static void ModelSeen(int stat)
        {
            try { if (stat >= 0 && stat < 4 && Campaign.Current != null) ModelDay[stat] = CampaignTime.Now.ToDays; } catch { }
        }

        /// <summary>Whether the game's calculation for that stat goes through our hook (so it already includes our effects).</summary>
        private static bool ViaModel(int stat, double now)
        {
            if (ModelDay[stat] < 0) { ModelDay[stat] = now; return true; }      // first day of the session: assume yes
            return now - ModelDay[stat] < 2.0;
        }

        /// <summary>All four calculations go through the game, so laws don't need adding by hand.</summary>
        public static bool LawsViaModel()
        {
            try
            {
                double now = CampaignTime.Now.ToDays;
                return ViaModel(StatProsperity, now) && ViaModel(StatFood, now) && ViaModel(StatSecurity, now) && ViaModel(StatLoyalty, now);
            }
            catch { return false; }
        }

        /// <summary>What events and laws add to or take from that stat of the town or castle today, with the reason.</summary>
        public static List<(float value, string label)> TownModifiers(Town town, int stat)
        {
            var list = new List<(float, string)>();
            try
            {
                var st = town?.Settlement;
                if (st == null || State == null) return list;
                double now = CampaignTime.Now.ToDays;
                foreach (var fx in State.Effects)
                {
                    if (fx.SettlementId != st.StringId || !string.IsNullOrEmpty(fx.ClanId) || now >= fx.UntilDay) continue;
                    float v = stat == StatProsperity ? fx.ProsperityPerDay : stat == StatFood ? fx.FoodPerDay : stat == StatSecurity ? fx.SecurityPerDay : fx.LoyaltyPerDay;
                    if (Math.Abs(v) < 0.005f) continue;
                    string why = string.IsNullOrWhiteSpace(fx.Reason) ? L.T("AIL_a9c3e7f1", "Sucesos del mundo") : (fx.Reason.Length > 60 ? fx.Reason.Substring(0, 60) + "…" : fx.Reason);
                    list.Add((v, why));
                }
                Laws.SettlementLines(st, stat == StatProsperity ? Laws.Prosperity : stat == StatFood ? Laws.Food : stat == StatSecurity ? Laws.Security : Laws.Loyalty, list);
            }
            catch { }
            return list;
        }

        private static void ApplyEffects(double now)
        {
            foreach (var fx in State.Effects.ToList())
            {
                if (now >= fx.UntilDay) { State.Effects.Remove(fx); continue; }
                if (!string.IsNullOrEmpty(fx.ClanId)) continue;      // clan effects aren't daily, they act on income
                var st = Settlement.Find(fx.SettlementId);
                if (st == null) { State.Effects.Remove(fx); continue; }
                try
                {
                    if (st.Town != null)
                    {
                        // Normally these effects are already part of the game's daily calculation (and show in its breakdown).
                        // Only add by hand the ones that stopped going through it.
                        if (!ViaModel(StatProsperity, now)) st.Town.Prosperity = Math.Max(0f, st.Town.Prosperity + fx.ProsperityPerDay);
                        if (!ViaModel(StatFood, now)) st.Town.FoodStocks = Math.Max(0f, st.Town.FoodStocks + fx.FoodPerDay);
                        if (!ViaModel(StatSecurity, now)) st.Town.Security = Math.Max(0f, Math.Min(100f, st.Town.Security + fx.SecurityPerDay));
                        if (!ViaModel(StatLoyalty, now)) st.Town.Loyalty = Math.Max(0f, Math.Min(100f, st.Town.Loyalty + fx.LoyaltyPerDay));
                    }
                    else if (st.Village != null)
                        st.Village.Hearth = Math.Max(10f, st.Village.Hearth + fx.ProsperityPerDay);   // for villages, prosperity means hearths
                }
                catch (Exception ex) { Log.Error("[mundo] efecto económico", ex); State.Effects.Remove(fx); }
            }
        }

        /// <summary>For the news window: that event's current effects in plain words ("Prosperity -2 per day, 14 days left").</summary>
        public static string EffectsLineEs(string newsId, string settlementId, string type)
        {
            try
            {
                double now = CampaignTime.Now.ToDays;
                var list = State.Effects.Where(f => f.UntilDay > now && !string.IsNullOrEmpty(f.EventId) && f.EventId == newsId).ToList();
                if (list.Count == 0) return "";
                var parts = new List<string>();
                foreach (var f in list.Take(4))
                {
                    if (!string.IsNullOrEmpty(f.ClanId))
                    {
                        var cl = Clan.All.FirstOrDefault(c => c.StringId == f.ClanId);
                        parts.Add(L.F("AIL_3be1eaff", $"Clan {cl?.Name?.ToString() ?? "?"}: ingresos {(f.IncomePercent > 0 ? "+" : "−")}{Math.Abs(f.IncomePercent):0} % (quedan {Math.Max(1, (int)Math.Ceiling(f.UntilDay - now))} días). {f.Reason}").TrimEnd());
                        continue;
                    }
                    var st = Settlement.Find(f.SettlementId);
                    var bits = new List<string>();
                    void Add(string name, float v) { if (Math.Abs(v) >= 0.01f) bits.Add(L.F("AIL_4d99daae", $"{name} {(v > 0 ? "+" : "−")}{Math.Abs(v):0.##} al día")); }
                    Add(L.T("AIL_e4a9f3a0", "prosperidad"), f.ProsperityPerDay); Add(L.T("AIL_d93015a6", "comida"), f.FoodPerDay); Add(L.T("AIL_5fd22751", "seguridad"), f.SecurityPerDay); Add(L.T("AIL_e3f5b714", "lealtad"), f.LoyaltyPerDay);
                    if (Math.Abs(f.PricePercent) >= 1f) bits.Add(L.F("AIL_9d381c5c", $"precios del mercado {(f.PricePercent > 0 ? "+" : "−")}{Math.Abs(f.PricePercent):0} %"));
                    if (bits.Count == 0) continue;
                    parts.Add(L.F("AIL_01e11e81", $"{st?.Name?.ToString() ?? L.T("AIL_c8ad772b", "El lugar")}: {string.Join(", ", bits)} (quedan {Math.Max(1, (int)Math.Ceiling(f.UntilDay - now))} días)"));
                }
                return string.Join("\n", parts);
            }
            catch { return ""; }
        }

        /// <summary>For an NPC's prompt: what's happening in their settlement because of an event.</summary>
        public static string DescribeEffectsAt(Settlement st)
        {
            if (st == null) return "";
            var list = State.Effects.Where(f => f.SettlementId == st.StringId && !string.IsNullOrWhiteSpace(f.Reason)).Select(f => f.Reason).Distinct().Take(3).ToList();
            return list.Count == 0 ? "" : "Life here is being affected by: " + string.Join("; ", list) + ".";
        }

        private static void PayTributes(double now)
        {
            foreach (var t in State.Tributes.ToList())
            {
                var payer = FindKingdom(t.Payer); var receiver = FindKingdom(t.Receiver);
                if (now >= t.UntilDay || payer == null || receiver == null || payer.IsAtWarWith(receiver)) { State.Tributes.Remove(t); continue; }
                int amount = Math.Min(t.Daily, Math.Max(0, payer.Leader.Gold));
                if (amount <= 0) continue;
                TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyBetweenCharacters(payer.Leader, receiver.Leader, amount, true);
                t.TotalPaid += amount;
            }
        }

        /// <summary>An extra crown tax gives more gold (Harmony patch) and wears down loyalty and prosperity in return.</summary>
        private static void ApplyTaxPressure()
        {
            foreach (var kv in State.KingdomTaxPercent.ToList())
            {
                var k = FindKingdom(kv.Key);
                if (k == null || kv.Value == 0) continue;
                float p = kv.Value / 100f;
                foreach (var town in k.Fiefs)
                {
                    town.Loyalty = Math.Max(0f, Math.Min(100f, town.Loyalty - p * 2f));
                    town.Prosperity = Math.Max(0f, town.Prosperity - p * 6f);
                }
            }
        }

        public static float TaxFactor(Town town)
        {
            try
            {
                var k = town?.Settlement?.OwnerClan?.Kingdom;
                if (k == null || !State.KingdomTaxPercent.TryGetValue(k.StringId, out var pct)) return 1f;
                return Math.Max(0.2f, 1f + pct / 100f);
            }
            catch { return 1f; }
        }

        // ============================================================ war fatigue

        public static void AddFatigue(Kingdom k, float amount)
        {
            if (k == null || amount == 0) return;
            State.WarFatigue.TryGetValue(k.StringId, out var f);
            State.WarFatigue[k.StringId] = Math.Max(0f, Math.Min(100f, f + amount));
        }

        /// <summary>
        /// What a day of war costs a kingdom. Each war weighs more the longer it has lasted (half as much again per
        /// year, up to three times), and every war beyond the first makes all of them a quarter heavier, so a realm
        /// fighting many at once wears out far faster than the sum of its wars. Recovery only comes at full peace.
        /// </summary>
        public static float DailyFatigue(Kingdom k)
        {
            var cfg = S.Get;
            var foes = Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).ToList();
            if (foes.Count == 0) return -cfg.FatigueRecoveryPerDayX10 / 10f;
            float year = Math.Max(1, CampaignTime.DaysInYear), sum = 0f;
            foreach (var o in foes) sum += 1f + Math.Min(2f, Math.Max(0, Diplomacy.WarDays(k, o)) / year * 0.5f);
            return sum * (1f + 0.25f * (foes.Count - 1)) * cfg.FatiguePerWarDayX10 / 10f;
        }

        private static void TickFatigue()
        {
            if (State.WarFatigue == null) State.WarFatigue = new Dictionary<string, float>();
            foreach (var k in Kingdoms.ToList())
            {
                try { AddFatigue(k, DailyFatigue(k)); }
                catch (Exception ex) { Log.Error($"[mundo] cansancio de guerra de {k?.StringId}", ex); }
            }
        }

        public static void OnMapEventEnded(MapEvent e)
        {
            try
            {
                if (e == null || !e.HasWinner) return;
                var loser = e.WinningSide == BattleSideEnum.Attacker ? e.DefenderSide : e.AttackerSide;
                var winner = e.WinningSide == BattleSideEnum.Attacker ? e.AttackerSide : e.DefenderSide;
                AddFatigue(loser?.LeaderParty?.MapFaction as Kingdom, S.Get.FatiguePerBattleLostX10 / 10f);
                AddFatigue(winner?.LeaderParty?.MapFaction as Kingdom, S.Get.FatiguePerBattleLostX10 / 40f);
            }
            catch { }
            Mood.OnMapEventEnded(e);
        }

        public static void OnHeroKilled(Hero victim, Hero killer, TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail detail, bool show)
        {
            // only its own lords weary a kingdom: a hired sword lost is not its blood (MapFaction would give the paymaster)
            try { if (victim != null && victim.IsLord) AddFatigue(Facts.Subject(victim.Clan), S.Get.FatiguePerLordKilled); } catch { }
            Chronicle.OnHeroKilled(victim, killer, detail);
            Mood.OnHeroKilled(victim, killer);
        }

        public static void OnPrisonerTaken(PartyBase captor, Hero prisoner)
        {
            try { if (prisoner != null && prisoner.IsLord) AddFatigue(Facts.Subject(prisoner.Clan), S.Get.FatiguePerLordKilled / 3f); } catch { }
        }

        public static void OnSettlementOwnerChanged(Settlement s, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturer,
            TaleWorlds.CampaignSystem.Actions.ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                var oldK = oldOwner?.MapFaction as Kingdom; var newK = newOwner?.MapFaction as Kingdom;
                if (oldK != null && oldK != newK && s != null && (s.IsTown || s.IsCastle)) AddFatigue(oldK, S.Get.FatiguePerSettlementLost);
                // a town or castle passing from one crown to another: their war, if they have one, is not a stalemate
                if (oldK != null && newK != null && oldK != newK && s != null && (s.IsTown || s.IsCastle))
                    State.WarLastCapture[FactCheck.Pair(oldK.StringId, newK.StringId)] = CampaignTime.Now.ToDays;
                Facts.Invalidate();
            }
            catch { }
        }
    }
}
