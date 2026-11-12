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
    // ===================================================================== datos

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
        // Efecto sobre un clan entero (su nombre manchado o en alza): sus ingresos suben o bajan ese tanto por ciento
        [JsonProperty("clan")] public string ClanId;
        [JsonProperty("income")] public float IncomePercent;
        // Precios del mercado de ese lugar: suben o bajan ese tanto por ciento mientras dure (escasez, miedo, abundancia)
        [JsonProperty("prices")] public float PricePercent;
    }

    public class WorldEvent
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("day")] public double Day;
        [JsonProperty("type")] public string Type = "news";            // political | military | economic | social | mysterious | news
        [JsonProperty("title")] public string Title = "";             // idioma del jugador
        [JsonProperty("text")] public string Text = "";               // idioma del jugador
        [JsonProperty("summary")] public string SummaryEn = "";       // inglés, para los prompts
        [JsonProperty("imp")] public int Importance = 2;
        [JsonProperty("kingdoms")] public List<string> Kingdoms = new List<string>();
        [JsonProperty("settlements")] public List<string> Settlements = new List<string>();
        /// <summary>Probabilidad diaria (0–100) de que el rey de cada reino se pronuncie sobre esto.</summary>
        [JsonProperty("engagement")] public Dictionary<string, int> Engagement = new Dictionary<string, int>();
        [JsonProperty("source")] public string Source = "world";      // world | statement | player
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

    /// <summary>Propuesta o exigencia de un reino a otro, a la espera de respuesta.</summary>
    public class Proposal
    {
        [JsonProperty("id")] public string Id;
        /// <summary>peace | alliance | trade | tribute | reparations | territory</summary>
        [JsonProperty("type")] public string Type;
        [JsonProperty("from")] public string From;
        [JsonProperty("to")] public string To;
        [JsonProperty("day")] public double Day;
        [JsonProperty("expires")] public double ExpiresDay;
        [JsonProperty("daily")] public int DailyTribute;       // lo paga «to» a «from»
        [JsonProperty("days")] public int TributeDays;
        [JsonProperty("amount")] public int Reparations;       // lo paga «to» a «from»
        [JsonProperty("settlement")] public string SettlementId;   // lo cede «to» a «from»
        [JsonProperty("years")] public int TradeYears;
        /// <summary>Ya se le ha enseñado al jugador (cuando va dirigida a su reino).</summary>
        [JsonProperty("notified")] public bool Notified;
    }

    public class Tribute
    {
        [JsonProperty("payer")] public string Payer;
        [JsonProperty("receiver")] public string Receiver;
        [JsonProperty("daily")] public int Daily;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("total")] public int TotalPaid;
    }

    /// <summary>Todo el estado del «mundo que se mueve solo». Va en ailegacy_world.json y en la copia de la partida.</summary>
    public class WorldState
    {
        [JsonProperty("events")] public List<WorldEvent> Events = new List<WorldEvent>();
        [JsonProperty("statements")] public List<RulerStatement> Statements = new List<RulerStatement>();
        [JsonProperty("proposals")] public List<Proposal> Proposals = new List<Proposal>();
        [JsonProperty("tributes")] public List<Tribute> Tributes = new List<Tribute>();
        [JsonProperty("effects")] public List<EconomicEffect> Effects = new List<EconomicEffect>();
        /// <summary>Hasta qué día dura la prevención pagada en un hospital (tus hombres enferman mucho menos).</summary>
        [JsonProperty("prevention_until")] public double PreventionUntilDay = -1;
        /// <summary>Secretos nacidos de la historia de esta partida, y de qué reinos ya se han escrito los de origen.</summary>
        [JsonProperty("secrets")] public List<StorySecret> Secrets = new List<StorySecret>();
        [JsonProperty("secret_kingdoms")] public List<string> SecretKingdoms = new List<string>();
        /// <summary>Personajes únicos creados por la IA en esta partida, y cuándo toca el siguiente.</summary>
        /// <summary>Arenas donde ya entrenaste (id del asentamiento → hora de campaña): se guarda con la partida.</summary>
        [JsonProperty("arena_last")] public Dictionary<string, double> ArenaLastAt = new Dictionary<string, double>();
        /// <summary>Condena de mazmorra en curso: dónde y hasta qué día.</summary>
        [JsonProperty("jail_in")] public string JailSettlementId;
        [JsonProperty("jail_until")] public double JailReleaseDay = -1;
        [JsonProperty("uniques")] public List<string> UniqueIds = new List<string>();
        [JsonProperty("next_unique_day")] public double NextUniqueDay = -1;
        [JsonProperty("fatigue")] public Dictionary<string, float> WarFatigue = new Dictionary<string, float>();
        [JsonProperty("taxes")] public Dictionary<string, int> KingdomTaxPercent = new Dictionary<string, int>();
        /// <summary>Postura de un rey hacia otro reino: "a|b" → -100..100.</summary>
        [JsonProperty("stance")] public Dictionary<string, int> Stance = new Dictionary<string, int>();
        [JsonProperty("outbreaks")] public List<Outbreak> Outbreaks = new List<Outbreak>();
        [JsonProperty("last_outbreak_day")] public double LastOutbreakDay = -1;
        [JsonProperty("laws")] public List<KingdomLaw> Laws = new List<KingdomLaw>();
        [JsonProperty("last_event_day")] public double LastEventGenerationDay = -1;
        [JsonProperty("next_event_day")] public double NextEventGenerationDay = -1;
        [JsonProperty("last_reactive_hour")] public double LastReactiveHour = -1;
        [JsonProperty("last_statement_day")] public Dictionary<string, double> LastStatementDay = new Dictionary<string, double>();
    }

    // ===================================================================== motor

    /// <summary>
    /// El mundo que se mueve solo: eventos generados por la IA con efectos reales en los asentamientos,
    /// cansancio de guerra, tributos e impuestos. La diplomacia de reyes está en Diplomacy.cs.
    /// </summary>
    internal static class WorldSim
    {
        public static WorldState State = new WorldState();

        private static readonly Random Rng = new Random();
        private static readonly ConcurrentQueue<Action> Pending = new ConcurrentQueue<Action>();
        private static volatile bool _busy;
        private static DateTime _lastCall = DateTime.MinValue;

        /// <summary>Petición del mundo en espera de turno (cola en memoria; no se guarda con la partida).</summary>
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
        /// <summary>Claves en cola, en vuelo o esperando a aplicarse: evitan pedir lo mismo dos veces.</summary>
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

        // ============================================================ llamadas a la IA en segundo plano

        /// <summary>¿Se puede tocar el mundo ahora? (en el mapa, sin misión ni conversación)</summary>
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
        /// <summary>Número de sesión: cambia cada vez que se carga, se crea o se importa una partida. Quien pide algo a la IA lo guarda y lo comprueba al volver.</summary>
        public static int Session => _session;

        /// <summary>
        /// Partida nueva o cargada: nada de lo que quedó a medias en la anterior (sucesos por reaccionar, respuestas en cola,
        /// peleas, detenidos, muertes pendientes) puede colarse en esta.
        /// </summary>
        public static void ResetSession()
        {
            _session++;
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
            Log.Info("[mundo] estado de sesión limpiado (partida nueva o cargada)");
        }

        /// <summary>Libre para lanzar ya: nada en vuelo y respetado el hueco mínimo entre llamadas.</summary>
        private static bool Ready => !_busy && (DateTime.UtcNow - _lastCall).TotalSeconds >= S.Get.WorldAiMinSecondsBetweenCalls;

        /// <summary>Para lo opcional (tandas, secretos, personajes): solo si no hay nada en vuelo ni en cola.</summary>
        public static bool CanCall { get { lock (Queue) return Ready && Queue.Count == 0; } }

        /// <summary>¿Hay ya una petición con esta clave en cola, en vuelo o por aplicar?</summary>
        public static bool IsReserved(string key) { if (key == null) return false; lock (Queue) return Reserved.Contains(key); }

        /// <summary>
        /// Lanza una petición del mundo; el resultado se aplica cuando el jugador está en el mapa. Si hay otra en curso, espera en cola
        /// (con límite: si se llena, sale la más vieja que no sea importante). Con clave, no se pide dos veces lo mismo.
        /// Quien pide debe consumir su agenda dentro de <paramref name="apply"/>, no antes. Devuelve false si ya estaba pedida.
        /// </summary>
        public static bool Ask(string what, List<ChatMessage> messages, int maxTokens, Action<JObject> apply, string key = null, Action failed = null, bool important = false)
        {
            var r = new WorldRequest
            {
                What = what, Key = key, Messages = messages, MaxTokens = maxTokens, Apply = apply, Failed = failed, Important = important,
                Session = _session, Camp = Campaign.Current?.UniqueGameId,      // la respuesta solo vale para la partida que la pidió
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
            LlmClient.CompleteAsync(r.Messages, wantJson: true, maxTokensOverride: r.MaxTokens, preferredProvider: S.Get.ProviderWorld).ContinueWith(t =>
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
                    // De otra partida: ResetSession ya soltó sus claves y vació la cola
                    if (r.Session != _session || Campaign.Current == null || Campaign.Current.UniqueGameId != r.Camp) { if (r.Session == _session) Release(r.Key); Log.Info($"[mundo] {r.What}: respuesta de otra partida, descartada"); return; }
                    if (j == null)
                    {
                        // Lo importante (la declaración del jugador) se reintenta una vez; lo demás devuelve su agenda
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

        /// <summary>Saca la siguiente de la cola si toca.</summary>
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

        /// <summary>Llamar a menudo desde el hilo del juego.</summary>
        public static void Pump()
        {
            if (Campaign.Current == null) { while (Pending.TryDequeue(out _)) { } return; }
            if (!OnMap()) return;
            if (Pending.TryDequeue(out var a)) { a(); Memory.SaveFiles.Flush(heroes: false); return; }
            if (LaunchQueued()) return;
            if (CanCall) TryReact();
        }

        // ============================================================ estado del mundo para los prompts

        public static string FatigueWord(Kingdom k)
        {
            State.WarFatigue.TryGetValue(k.StringId, out var f);
            return f < 15 ? "fresh" : f < 35 ? "strained" : f < 60 ? "weary" : f < 85 ? "exhausted" : "at breaking point";
        }

        public static int GetStance(string from, string to) => State.Stance.TryGetValue(from + "|" + to, out var v) ? v : 0;
        public static void ChangeStance(string from, string to, int delta)
        {
            var key = from + "|" + to;
            State.Stance[key] = Math.Max(-100, Math.Min(100, GetStance(from, to) + delta));
        }

        public static string StanceWord(int v) => v <= -60 ? "hatred" : v <= -25 ? "hostile" : v < -5 ? "cold" : v <= 5 ? "neutral" : v < 25 ? "cordial" : v < 60 ? "friendly" : "close";

        /// <summary>Datos técnicos de los reinos: la verdad del mundo ahora mismo.</summary>
        public static string DescribeKingdoms(Kingdom focus = null)
        {
            var sb = new StringBuilder();
            foreach (var k in Kingdoms.OrderByDescending(x => x.CurrentTotalStrength))
            {
                var towns = k.Fiefs.Where(f => f.IsTown).Select(f => f.Name.ToString()).ToList();
                int castles = k.Fiefs.Count(f => f.IsCastle);
                var wars = Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).Select(o => o.StringId).ToList();
                var allies = Kingdoms.Where(o => o != k && Diplomacy.Allied(k, o)).Select(o => o.StringId).ToList();
                var trade = Kingdoms.Where(o => o != k && Diplomacy.HasTrade(k, o)).Select(o => o.StringId).ToList();
                sb.Append($"- {k.Name} [id: {k.StringId}]{(k == Hero.MainHero.MapFaction ? " (the realm of " + Hero.MainHero.Name + ")" : "")}: ruler {k.Leader.Name}; ")
                  .Append($"strength {(int)k.CurrentTotalStrength}; towns ({towns.Count}): {(towns.Count > 0 ? string.Join(", ", towns.Take(8)) : "none")}; castles {castles}; ")
                  .Append($"AT WAR with: {(wars.Count > 0 ? string.Join(", ", wars) : "nobody")}; ")
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

        // ============================================================ generador de eventos

        private static readonly Tpl TEventGen = new Tpl("dynamic_events_generator/generator.txt",
@"You are the chronicler of Calradia, the medieval world of Mount & Blade II: Bannerlord. From the real state of the world below, write {count} new development(s) that could plausibly be happening right now: political, military, economic, social or mysterious. They become news that people talk about and that rulers may react to.

Rules:
- Ground every event in the data below: a real kingdom, real towns, a real war, a real recent event. Structure: cause (from the data) → what is happening → what it threatens or promises. Continue existing stories when it makes sense.
- NEVER state as done anything only the world itself can do: a war declared, a peace signed, a town changing hands, a ruler or lord dead, a clan expelled, a law or tax enacted. You may write pressure, rumours, accusations, demands, unrest, shortages, omens, plots: the real outcome, if any, will come later from the rulers.
- Do not involve the person named as ""the realm of ..."" personally unless the data does.
- Use readable names in title and text, never ids. Never put a double-quote character inside a text value; use «» if you must quote.
- importance: 1 = one town or village talks about it, 2 = nobles of the realm, 3 = the whole realm, 4 = the whole world (rare).
- kingdom_engagement: for events that call for a response from a crown, the daily chance (0-100) that the ruler of that kingdom speaks out about it. Only kingdoms listed in kingdoms_involved. Leave empty for events nobody needs to answer.
- economic_effects (optional, only when the event really touches daily life in a real town, castle or village): small daily changes for a number of days. Limits per day: prosperity ±{max_prosperity}, food ±{max_food}, security ±{max_security}, loyalty ±{max_loyalty}; duration up to {max_days} days. For a town or village you may also set price_percent (±{max_price}): how much dearer or cheaper everything in its market is meanwhile (a shortage, a panic, a glut).
- clan_effects (optional): when the event is about the name of ONE specific clan being stained or raised (a scandal, an outrage committed by one of its members, a feat that brings it honour), how its income changes while people remember: income_percent from -{max_clan}% to +{max_clan}%, for up to {max_days} days, with the reason in a few words. Merchants and allies avoid a disgraced house; they court an honoured one.
- secret (optional, for about one event in three): the hidden truth behind this event that only a few people know and that the public version leaves out (who really paid for it, who is lying, what was covered up). ""known_by"": 1 to 3 REAL named nobles from the data who would know. ""access"": low | medium | high (how hard it is to get it out of them).
- clan_rebellion (rare, optional): the name of ONE vassal clan that rises against its own crown right now. Only when the data shows a real break between that clan and its ruler; never the ruling clan and never a clan of ""the realm of ..."". The world will carry it out.
- disease (rare, optional): a sickness breaking out in ONE real town or village, when the season, a siege, hunger or crowding make it plausible. severity 1 (mild) to {max_severity}.
- title: 3-8 words in {language}. text: {min_len}-{max_len} characters in {language}, written like a chronicle. summary: the same in ONE English sentence.

Answer ONLY with JSON:
{ ""events"": [ { ""type"": ""political|military|economic|social|mysterious"", ""title"": ""..."", ""text"": ""..."", ""summary"": ""..."", ""importance"": 1-4,
  ""kingdoms_involved"": [""kingdom ids""], ""settlements_involved"": [""settlement names""], ""kingdom_engagement"": { ""kingdom_id"": 0-100 },
  ""economic_effects"": [ { ""settlement"": ""name"", ""prosperity_per_day"": 0, ""food_per_day"": 0, ""security_per_day"": 0, ""loyalty_per_day"": 0, ""price_percent"": 0, ""duration_days"": 0, ""reason"": ""..."" } ],
  ""clan_effects"": [ { ""clan"": ""clan name"", ""income_percent"": 0, ""duration_days"": 0, ""reason"": ""..."" } ],
  ""secret"": { ""text"": ""one or two English sentences"", ""text_local"": ""the same in {language}"", ""about"": [""names""], ""known_by"": [""names""], ""access"": ""high"" },
  ""clan_rebellion"": ""clan name or empty"", ""disease"": { ""settlement"": ""name"", ""name"": ""name of the sickness in {language}"", ""name_en"": ""the same in English"", ""severity"": 1, ""duration_days"": 30 } } ] }
Leave out economic_effects, clan_effects, secret, clan_rebellion and disease when they do not apply.");

        public static void Touch() { var _ = TEventGen.Key; Diplomacy.Touch(); }

        // Sucesos REALES del juego que acaban de pasar y merecen una reacción del mundo (no se guardan: son del momento)
        private static readonly List<string> Triggers = new List<string>();

        private static readonly Tpl TReact = new Tpl("dynamic_events_generator/react_to_real_event.txt",
@"## THIS HAS JUST REALLY HAPPENED
{triggers}
Write ONE development that follows directly from it: how the people, the nobles, the merchants or the other crowns take it, what it stirs up, what it threatens next. It is a consequence or a reaction, not a retelling of the fact itself (everyone already knows the fact). Give kingdom_engagement to the crowns that would want to answer.");

        /// <summary>Cada noticia real del juego pasa por aquí: las gordas provocan una reacción del mundo en cuanto se puede.</summary>
        public static void OnRealNews(NewsItem n)
        {
            var cfg = S.Get;
            if (!Enabled || !cfg.ReactiveEvents || n == null || Campaign.Current == null) return;
            string t = n.Type ?? "";
            if (t == "statement" || t == "custom" || t.StartsWith("event_")) return;     // lo que escribe la propia IA no se realimenta
            if (n.Importance < cfg.ReactiveMinImportance) return;
            lock (Triggers)
            {
                if (Triggers.Count >= 5) Triggers.RemoveAt(0);
                Triggers.Add(n.Text);
            }
        }

        private static bool _urgent;      // hay un suceso tuyo esperando: no aguarda al plazo entre reacciones

        /// <summary>
        /// Algo que has hecho o dicho tú y que da que hablar (una pelea en la calle, un muerto, un arresto, un rumor gordo
        /// que has echado a rodar): el mundo lo convierte en un suceso propio, con sus efectos en el lugar si los merece.
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
                lock (Triggers)
                {
                    if (Triggers.Count >= 5) Triggers.RemoveAt(0);
                    Triggers.Add(t);
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
            if (State.LastReactiveHour > hour) State.LastReactiveHour = -1;     // partida cargada de antes
            if (!_urgent && State.LastReactiveHour >= 0 && hour - State.LastReactiveHour < cfg.ReactiveMinHoursBetween) return;
            _urgent = false;
            List<string> take;
            lock (Triggers)
            {
                if (Triggers.Count == 0) return;
                take = Triggers.ToList();
                Triggers.Clear();
            }
            State.LastReactiveHour = hour;
            GenerateEvents(false, take);
        }

        /// <summary>La reacción no salió: sus sucesos vuelven a la cola (como mucho dos veces seguidas).</summary>
        private static void RequeueTriggers(List<string> take)
        {
            if (take == null || take.Count == 0 || ++_reactRetries > 2) return;
            lock (Triggers)
            {
                foreach (var t in take.AsEnumerable().Reverse()) if (!Triggers.Contains(t)) Triggers.Insert(0, t);
                while (Triggers.Count > 5) Triggers.RemoveAt(Triggers.Count - 1);
            }
        }

        public static void GenerateEvents(bool forced = false, List<string> reactTo = null)
        {
            var cfg = S.Get;
            if (!Enabled || Campaign.Current == null) return;
            if (!forced && !CanCall) return;
            bool reactive = reactTo != null && reactTo.Count > 0;
            if (IsReserved(reactive ? "react" : "events")) return;     // ya pedida: falta que llegue o se aplique
            int count = reactive ? 1 : cfg.WorldEventsPerGeneration;
            var sys = new StringBuilder();
            sys.AppendLine(TEventGen.Format("count", count, "language", Lang, "min_len", cfg.WorldEventMinLength, "max_len", cfg.WorldEventMaxLength,
                "max_prosperity", cfg.EffectMaxProsperityPerDay, "max_food", cfg.EffectMaxFoodPerDay, "max_security", cfg.EffectMaxSecurityPerDay,
                "max_loyalty", cfg.EffectMaxLoyaltyPerDay, "max_days", cfg.EffectMaxDays, "max_severity", cfg.DiseaseMaxSeverity, "max_clan", cfg.ClanEffectMaxPercent, "max_price", cfg.EffectMaxPricePercent));
            var lore = WorldLore.Text;
            if (!string.IsNullOrWhiteSpace(lore)) { sys.AppendLine(); sys.AppendLine("## The world"); sys.AppendLine(lore); }
            var now = CampaignTime.Now;
            sys.AppendLine();
            sys.AppendLine($"## Date: day {now.GetDayOfSeason + 1} of {now.GetSeasonOfYear}, year {now.GetYear}");
            sys.AppendLine("## Kingdoms (technical truth, right now)");
            sys.AppendLine(DescribeKingdoms());
            sys.AppendLine("## What really happened lately");
            sys.AppendLine(DescribeRecentNews(14));
            sys.AppendLine("## Developments already in the air (do not repeat them; you may continue one)");
            sys.AppendLine(DescribeActiveEvents(8));
            var pend = Diplomacy.DescribeAllProposals();
            if (pend.Length > 0) { sys.AppendLine("## Pending proposals between crowns (do not narrate their outcome)"); sys.AppendLine(pend); }

            if (reactive)
            {
                sys.AppendLine();
                sys.AppendLine(TReact.Format("triggers", string.Join("\n", reactTo.Select(x => "- " + x))));
            }
            Ask(reactive ? L.T("AIL_ce8115b0", "reacción a un suceso real") : "generar eventos",
                new List<ChatMessage> { new ChatMessage("system", sys.ToString()), new ChatMessage("user", reactive ? "Write the development now." : "Write the events now.") },
                count * 700 + 400, j =>
                {
                    if (reactive) _reactRetries = 0;
                    else
                    {
                        // La agenda se consume al llegar la tanda. No cae como un reloj: el siguiente plazo varía entre el 60 % y el 140 % del ajuste
                        double d = CampaignTime.Now.ToDays;
                        State.LastEventGenerationDay = d;
                        State.NextEventGenerationDay = d + S.Get.WorldEventsIntervalDays * (0.6 + Rng.NextDouble() * 0.8);
                    }
                    ApplyEvents(j);
                },
                key: reactive ? "react" : "events", failed: reactive ? (Action)(() => RequeueTriggers(reactTo)) : null);
        }

        private static void ApplyEvents(JObject j)
        {
            if (!(j["events"] is JArray arr)) return;
            foreach (var x in arr.OfType<JObject>().Take(S.Get.WorldEventsPerGeneration))
            {
                var e = new WorldEvent
                {
                    Id = NewId("ev"), Day = CampaignTime.Now.ToDays,
                    Type = Clean((string)x["type"], 20).ToLowerInvariant(), Title = Clean((string)x["title"], 90), Text = Clean((string)x["text"], 1400),
                    SummaryEn = Clean((string)x["summary"], 350), Importance = Math.Max(1, Math.Min(4, (int?)x["importance"] ?? 2)),
                };
                if (e.Title.Length < 3 || e.Text.Length < 20) continue;
                if (x["kingdoms_involved"] is JArray ks)
                    foreach (var k in ks) { var kg = FindKingdom(k?.ToString()); if (kg != null && !e.Kingdoms.Contains(kg.StringId)) e.Kingdoms.Add(kg.StringId); }
                if (x["settlements_involved"] is JArray ss)
                    foreach (var s in ss) { var st = OrderSystem.FindSettlement(s?.ToString()); if (st != null && !e.Settlements.Contains(st.StringId)) e.Settlements.Add(st.StringId); }
                if (x["kingdom_engagement"] is JObject eng)
                    foreach (var p in eng.Properties())
                    {
                        var kg = FindKingdom(p.Name);
                        if (kg == null || kg.Leader == Hero.MainHero) continue;      // el jugador habla cuando él quiere
                        if (!e.Kingdoms.Contains(kg.StringId)) e.Kingdoms.Add(kg.StringId);
                        e.Engagement[kg.StringId] = Math.Max(0, Math.Min(100, (int?)p.Value ?? 0));
                    }
                Publish(e);
                if (x["economic_effects"] is JArray fx) foreach (var f in fx.OfType<JObject>().Take(4)) AddEffect(e, f);
                if (x["clan_effects"] is JArray cfx) foreach (var f in cfx.OfType<JObject>().Take(2)) AddClanEffect(e, f);
                // Lo que hay detrás del suceso y solo saben unos pocos
                if (S.Get.StorySecrets && x["secret"] is JObject sec)
                    StorySecrets.Add(sec, e.Kingdoms.Select(FindKingdom).FirstOrDefault(k => k != null), "event");
                if (x["disease"] is JObject dis) Diseases.FromEvent(dis);
                var rebel = (string)(x["clan_rebellion"]?.Type == JTokenType.String ? x["clan_rebellion"] : null);
                if (!string.IsNullOrWhiteSpace(rebel)) Diplomacy.Rebellion(rebel);
            }
        }

        public static string Clean(string s, int max)
        {
            s = (s ?? "").Replace("\"", "»").Replace("{", "(").Replace("}", ")").Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        /// <summary>Guarda el evento y lo convierte en noticia (así lo conocen los NPC según su alcance).</summary>
        public static void Publish(WorldEvent e)
        {
            State.Events.Add(e);
            while (State.Events.Count > 150) State.Events.RemoveAt(0);
            NewsStore.Add(new NewsItem
            {
                Id = e.Id,      // los efectos guardan EventId = e.Id; Books los busca por el id de la noticia
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

        /// <summary>Cuánto suben o bajan ahora los precios del mercado de ese lugar por los sucesos en curso (0,25 = +25 %).</summary>
        public static float PriceFactor(Settlement st)
        {
            if (st == null || State.Effects.Count == 0) return 0f;
            double now = CampaignTime.Now.ToDays;
            float sum = 0f;
            foreach (var fx in State.Effects)
                if (fx.PricePercent != 0f && fx.SettlementId == st.StringId && fx.UntilDay > now) sum += fx.PricePercent;
            int max = S.Get.EffectMaxPricePercent;
            return Math.Max(-max, Math.Min(max, sum)) / 100f;
        }

        /// <summary>Cuánto suben o bajan ahora los ingresos de ese clan por los sucesos en curso (0,2 = +20 %), y por qué.</summary>
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

        private static void AddEffect(WorldEvent e, JObject f)
        {
            var cfg = S.Get;
            if (!cfg.EnableEconomicEffects) return;
            var st = OrderSystem.FindSettlement((string)f["settlement"]);
            if (st == null) return;
            float C(string k, int max) { float v = (float?)f[k] ?? 0f; return Math.Max(-max, Math.Min(max, v)); }
            var fx = new EconomicEffect
            {
                SettlementId = st.StringId, EventId = e.Id, Reason = Clean((string)f["reason"], 120),
                ProsperityPerDay = C("prosperity_per_day", cfg.EffectMaxProsperityPerDay), FoodPerDay = C("food_per_day", cfg.EffectMaxFoodPerDay),
                SecurityPerDay = C("security_per_day", cfg.EffectMaxSecurityPerDay), LoyaltyPerDay = C("loyalty_per_day", cfg.EffectMaxLoyaltyPerDay),
                UntilDay = CampaignTime.Now.ToDays + Math.Max(1, Math.Min(cfg.EffectMaxDays, (int?)f["duration_days"] ?? 7)),
                PricePercent = st.IsTown || st.IsVillage ? C("price_percent", cfg.EffectMaxPricePercent) : 0f,
            };
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
                TickFatigue();
                Diplomacy.OnDailyTick(now);

                // Tanda «de fondo»: lo que se cuece aunque no haya pasado nada sonado. Las reacciones a sucesos reales van aparte (TryReact).
                if (Enabled && cfg.BackgroundEvents)
                {
                    if (State.NextEventGenerationDay < 0 || State.NextEventGenerationDay - now > cfg.WorldEventsIntervalDays * 2)
                        State.NextEventGenerationDay = now + 2;      // la primera, a los dos días
                    else if (now >= State.NextEventGenerationDay) GenerateEvents();
                }
            }
            catch (Exception ex) { Log.Error("[mundo] tick diario", ex); }
        }

        private static void ApplyEffects(double now)
        {
            foreach (var fx in State.Effects.ToList())
            {
                if (now >= fx.UntilDay) { State.Effects.Remove(fx); continue; }
                if (!string.IsNullOrEmpty(fx.ClanId)) continue;      // los de clan no se aplican día a día: actúan sobre sus ingresos
                var st = Settlement.Find(fx.SettlementId);
                if (st == null) { State.Effects.Remove(fx); continue; }
                try
                {
                    if (st.Town != null)
                    {
                        st.Town.Prosperity = Math.Max(0f, st.Town.Prosperity + fx.ProsperityPerDay);
                        st.Town.FoodStocks = Math.Max(0f, st.Town.FoodStocks + fx.FoodPerDay);
                        st.Town.Security = Math.Max(0f, Math.Min(100f, st.Town.Security + fx.SecurityPerDay));
                        st.Town.Loyalty = Math.Max(0f, Math.Min(100f, st.Town.Loyalty + fx.LoyaltyPerDay));
                    }
                    else if (st.Village != null)
                        st.Village.Hearth = Math.Max(10f, st.Village.Hearth + fx.ProsperityPerDay);   // en aldeas, la prosperidad son los hogares
                }
                catch (Exception ex) { Log.Error("[mundo] efecto económico", ex); State.Effects.Remove(fx); }
            }
        }

        /// <summary>Para la ventana de noticias: los efectos en curso de ese evento, dichos en claro («Prosperidad −2 al día, quedan 14 días»).</summary>
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

        /// <summary>Para el prompt de un NPC: qué pasa en su asentamiento por culpa de un evento.</summary>
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

        /// <summary>Un impuesto extra de la corona da más oro (parche de Harmony) y, a cambio, desgasta la lealtad y la prosperidad.</summary>
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

        // ============================================================ cansancio de guerra

        public static void AddFatigue(Kingdom k, float amount)
        {
            if (k == null || amount == 0) return;
            State.WarFatigue.TryGetValue(k.StringId, out var f);
            State.WarFatigue[k.StringId] = Math.Max(0f, Math.Min(100f, f + amount));
        }

        private static void TickFatigue()
        {
            var cfg = S.Get;
            foreach (var k in Kingdoms)
            {
                int wars = Kingdoms.Count(o => o != k && k.IsAtWarWith(o));
                if (wars == 0) AddFatigue(k, -cfg.FatigueRecoveryPerDayX10 / 10f);
                else AddFatigue(k, wars * cfg.FatiguePerWarDayX10 / 10f);
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
        }

        public static void OnHeroKilled(Hero victim, Hero killer, TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail detail, bool show)
        {
            try { if (victim != null && victim.IsLord) AddFatigue(victim.MapFaction as Kingdom, S.Get.FatiguePerLordKilled); } catch { }
            Chronicle.OnHeroKilled(victim, killer, detail);
        }

        public static void OnPrisonerTaken(PartyBase captor, Hero prisoner)
        {
            try { if (prisoner != null && prisoner.IsLord) AddFatigue(prisoner.MapFaction as Kingdom, S.Get.FatiguePerLordKilled / 3f); } catch { }
        }

        public static void OnSettlementOwnerChanged(Settlement s, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturer,
            TaleWorlds.CampaignSystem.Actions.ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                var oldK = oldOwner?.MapFaction as Kingdom; var newK = newOwner?.MapFaction as Kingdom;
                if (oldK != null && oldK != newK && s != null && (s.IsTown || s.IsCastle)) AddFatigue(oldK, S.Get.FatiguePerSettlementLost);
            }
            catch { }
        }
    }
}
