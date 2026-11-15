using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Letters;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>Something one NPC tells another about the player. It travels, so it doesn't arrive instantly.</summary>
    public class NpcMessage
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("from")] public string FromId;
        [JsonProperty("from_name")] public string FromName;
        [JsonProperty("to")] public string ToId;
        [JsonProperty("text")] public string Text;          // English, another NPC's prompt reads it
        /// <summary>public = will spread it, private = just for them, secret = sworn to silence</summary>
        [JsonProperty("secrecy")] public string Secrecy = "private";
        [JsonProperty("day")] public double Day;
        [JsonProperty("arrive")] public double ArriveDay;
        /// <summary>Who first told it, when it has passed through other hands since. Null if the sender is the source.</summary>
        [JsonProperty("origin", NullValueHandling = NullValueHandling.Ignore)] public string OriginName;
        /// <summary>Ids of everyone it has already passed through, so it never goes back to them.</summary>
        [JsonProperty("chain", NullValueHandling = NullValueHandling.Ignore)] public List<string> Chain;
        /// <summary>How many times it has been passed on (0 = straight from the source).</summary>
        [JsonProperty("hops")] public int Hops;
        /// <summary>The sender expects an answer back.</summary>
        [JsonProperty("wants_answer")] public bool WantsAnswer;
        /// <summary>This is the answer to an earlier message.</summary>
        [JsonProperty("is_answer")] public bool IsAnswer;
        /// <summary>Who it is about, by name. Null = the player.</summary>
        [JsonProperty("about", NullValueHandling = NullValueHandling.Ignore)] public string About;
        /// <summary>The sender asked the recipient to pass it on to this person.</summary>
        [JsonProperty("relay_to", NullValueHandling = NullValueHandling.Ignore)] public string RelayToId;
        /// <summary>travelling, delivered or lost.</summary>
        [JsonProperty("status")] public string Status = "travelling";
        [JsonProperty("fail", NullValueHandling = NullValueHandling.Ignore)] public string FailureReason;
        /// <summary>When it was first due to arrive, before any delay.</summary>
        [JsonProperty("due")] public double DeliveryDueDay;
        /// <summary>How far it can be trusted (0-100): straight from the source is high, each hand takes some away.</summary>
        [JsonProperty("confidence")] public int Confidence = 90;
        /// <summary>1 small talk, 2 worth telling, 3 urgent.</summary>
        [JsonProperty("importance")] public int Importance = 2;
        /// <summary>Somebody along the way changed it in the telling.</summary>
        [JsonProperty("distorted")] public bool IsDistorted;
        /// <summary>Handed to the player to carry by hand: it arrives when the player speaks to the recipient.</summary>
        [JsonProperty("courier")] public bool ByPlayer;
        [JsonProperty("delayed")] public bool Delayed;
    }

    /// <summary>Something an NPC heard from another one.</summary>
    public class HeardItem
    {
        [JsonProperty("from")] public string FromName;
        [JsonProperty("text")] public string Text;
        [JsonProperty("secrecy")] public string Secrecy = "private";
        [JsonProperty("day")] public double Day;
        /// <summary>Who it came from originally, when the teller was only passing it on.</summary>
        [JsonProperty("origin", NullValueHandling = NullValueHandling.Ignore)] public string OriginName;
        [JsonProperty("hops")] public int Hops;
        /// <summary>Who it is about, by name. Null = the player.</summary>
        [JsonProperty("about", NullValueHandling = NullValueHandling.Ignore)] public string About;
        [JsonProperty("confidence")] public int Confidence = 90;
        [JsonProperty("importance")] public int Importance = 2;
        [JsonProperty("distorted")] public bool IsDistorted;
        /// <summary>The player brought it by hand.</summary>
        [JsonProperty("by_player")] public bool ByPlayer;
    }

    /// <summary>
    /// What you tell one NPC (or do in front of them) can reach another, delayed by distance and with a secrecy level.
    /// What arrives is a claim, not a fact.
    /// </summary>
    internal static class InfoExchange
    {
        public static List<NpcMessage> Messages = new List<NpcMessage>();
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        /// <summary>People this NPC would plausibly tell things to: family, liege, clan leader, friends.</summary>
        public static List<Hero> Confidants(Hero h, int max = 8)
        {
            var list = new List<Hero>();
            void Add(Hero x) { if (x != null && x != h && x != P && x.IsAlive && !x.IsChild && !list.Contains(x)) list.Add(x); }
            try
            {
                Add(h.Spouse); Add(h.Father); Add(h.Mother);
                foreach (var s in h.Siblings) Add(s);
                if (h.Clan?.Leader != null) Add(h.Clan.Leader);
                if (h.MapFaction is Kingdom k) Add(k.Leader);
                if (h.IsNotable && h.CurrentSettlement?.OwnerClan?.Leader != null) Add(h.CurrentSettlement.OwnerClan.Leader);
                foreach (var x in Hero.AllAliveHeroes.Where(o => o != h && o != P && (o.IsLord || o.IsNotable) && h.GetRelation(o) >= 30).OrderByDescending(o => h.GetRelation(o)).Take(3)) Add(x);
            }
            catch { }
            return list.Take(max).ToList();
        }

        public static string DescribeConfidants(Hero h)
        {
            var c = Confidants(h);
            return c.Count == 0 ? "" : "People you could send word to: " + string.Join(", ", c.Select(x => x.Name.ToString())) + ".";
        }

        /// <summary>A real person by the name a character wrote, the sender's own people first. Never the sender or the player.</summary>
        private static Hero FindPerson(Hero from, string name)
        {
            string n = WorldSim.Norm(name);
            if (n.Length < 2) return null;
            var pool = Hero.AllAliveHeroes.Where(h => h != from && h != P && !h.IsChild).ToList();
            var to = Hero.Find(name) ?? pool.FirstOrDefault(h => WorldSim.Norm(h.Name.ToString()) == n)
                     ?? Confidants(from, 20).FirstOrDefault(h => WorldSim.Norm(h.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(h.Name.ToString())))
                     ?? pool.FirstOrDefault(h => (h.IsLord || h.IsNotable) && (WorldSim.Norm(h.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(h.Name.ToString()))));
            return to == null || to == from || to == P ? null : to;
        }

        /// <summary>
        /// The NPC sends word to another one. Returns the text to show the player, "" for nothing to show, or null on failure.
        /// <paramref name="about"/> is who it is about (null or the player's name = the player); <paramref name="passTo"/> asks
        /// the recipient to hand it on to that person.
        /// </summary>
        public static string Send(Speaker from, string toName, string text, string secrecy, bool wantsAnswer = false, string about = null, string passTo = null, int importance = 2)
        {
            if (!from.IsHero || string.IsNullOrWhiteSpace(text)) return null;
            var to = FindPerson(from.Hero, toName);
            if (to == null) { Log.Warn($"Errand from {from.Name}: recipient '{toName}' not found"); return null; }
            secrecy = (secrecy ?? "").Trim().ToLowerInvariant();
            if (secrecy != "public" && secrecy != "secret") secrecy = "private";
            if (text.Length > 600) text = text.Substring(0, 600);
            Hero relay = string.IsNullOrWhiteSpace(passTo) ? null : FindPerson(to, passTo);
            if (relay == from.Hero) relay = null;

            double hours = Hours(from.Hero, to), now = CampaignTime.Now.ToDays;
            Messages.Add(new NpcMessage
            {
                Id = WorldSim.NewId("m"), FromId = from.Hero.StringId, FromName = from.Name, ToId = to.StringId, Text = text, Secrecy = secrecy,
                Day = now, ArriveDay = now + hours / 24.0, DeliveryDueDay = now + hours / 24.0,
                WantsAnswer = wantsAnswer && S.Get.ErrandAnswers,
                Chain = new List<string> { from.Hero.StringId },
                About = AboutName(about), RelayToId = relay?.StringId, Importance = Math.Max(1, Math.Min(3, importance)),
            });
            Log.Info($"Errand from {from.Name} to {to.Name} ({secrecy}, arrives in {hours:0} h{(relay != null ? ", to be relayed to " + relay.Name : "")}): {text}");
            // Private or secret messages aren't announced to the player. They'd have no way to know, and it gave away
            // who was being told. Only public ones get a notice.
            return secrecy == "public" ? L.F("AIL_c009f7dc", $"{from.Name} hará llegar noticia de esto a {to.Name}.") : "";
        }

        /// <summary>Who a message is about, as stored: null when it is the player.</summary>
        private static string AboutName(string about)
        {
            string a = (about ?? "").Trim();
            if (a.Length < 2) return null;
            string n = WorldSim.Norm(a), pn = WorldSim.Norm(P?.Name?.ToString() ?? "");
            if (n == "player" || n == "you" || (pn.Length > 0 && (n == pn || pn.Contains(n) || n.Contains(pn)))) return null;
            return a.Length > 60 ? a.Substring(0, 60) : a;
        }

        /// <summary>The NPC hands the player a message to carry by hand. It reaches the recipient when the player next speaks to them.</summary>
        public static string GiveToPlayer(Speaker from, string toName, string text)
        {
            if (!from.IsHero || string.IsNullOrWhiteSpace(text)) return null;
            var to = FindPerson(from.Hero, toName);
            if (to == null) { Log.Warn($"Hand-delivered errand from {from.Name}: recipient '{toName}' not found"); return null; }
            if (text.Length > 600) text = text.Substring(0, 600);
            if (Messages.Count(x => x.ByPlayer) >= 12) return null;
            double now = CampaignTime.Now.ToDays;
            Messages.Add(new NpcMessage
            {
                Id = WorldSim.NewId("m"), FromId = from.Hero.StringId, FromName = from.Name, ToId = to.StringId, Text = text, Secrecy = "private",
                Day = now, ArriveDay = double.MaxValue, DeliveryDueDay = now + CourierDays, ByPlayer = true, Confidence = 95,
                Chain = new List<string> { from.Hero.StringId },
            });
            Log.Info($"Hand-delivered errand: {from.Name} gives the player an errand for {to.Name}: {text}");
            return L.F("AIL_e7b1c7ae", $"{from.Name} te pide que lleves en mano un recado a {to.Name}.");
        }

        private const int CourierDays = 60;

        /// <summary>The player starts talking to someone: any word they carry for that person is handed over. Text to show, or "".</summary>
        public static string DeliverCarried(Speaker s)
        {
            try
            {
                if (s == null || !s.IsHero || Messages.Count == 0) return "";
                var mine = Messages.Where(x => x.ByPlayer && x.ToId == s.Hero.StringId).ToList();
                if (mine.Count == 0) return "";
                double now = CampaignTime.Now.ToDays;
                var shown = new List<string>();
                foreach (var m in mine)
                {
                    Messages.Remove(m);
                    m.Status = "delivered";
                    AddHeard(s.Memory, new HeardItem { FromName = m.FromName, Text = m.Text, Secrecy = m.Secrecy, Day = now, About = m.About, Confidence = m.Confidence, Importance = m.Importance, ByPlayer = true });
                    NoteSender(m, TByHand.Format("player", P.Name.ToString(), "to", s.Name));
                    shown.Add(L.F("AIL_e7b1c7af", $"Entregas a {s.Name} el recado de {m.FromName}."));
                    Log.Info($"Hand-delivered errand delivered to {s.Name} (from {m.FromName}): {m.Text}");
                }
                return string.Join(" ", shown);
            }
            catch (Exception ex) { Log.Error("Deliver a hand-delivered errand", ex); return ""; }
        }

        /// <summary>The sender learns what became of their word, as a note in their history.</summary>
        private static void NoteSender(NpcMessage m, string note)
        {
            try
            {
                var from = Hero.Find(m.FromId);
                if (from == null || !from.IsAlive || string.IsNullOrWhiteSpace(note)) return;
                var mem = MemoryStore.Get(from.StringId, from.Name.ToString());
                mem.Turns.Add(new ChatTurn { Role = "player", Text = "[" + note.Trim().TrimStart('[').TrimEnd(']') + "]", Day = CampaignTime.Now.ToDays });
            }
            catch { }
        }

        private static readonly Tpl TByHand = new Tpl("info_exchange/word_by_hand.txt",
            "{player} delivered by hand the word you gave them for {to}.");
        private static readonly Tpl TNotCarried = new Tpl("info_exchange/word_not_carried.txt",
            "{player} never delivered the word you gave them for {to}: it has been weeks.");
        private static readonly Tpl TLostDead = new Tpl("info_exchange/word_lost_dead.txt",
            "Your word to {to} never reached them: {to} is dead.");
        private static readonly Tpl TRelayRefused = new Tpl("info_exchange/word_not_passed.txt",
            "{to} received your word but did not pass it on to {relay} as you asked.");
        // One line per tag, picked by the word before the colon
        private static readonly Tpl THeardTags = new Tpl("info_exchange/heard_tags.txt",
@"by_hand: brought to you by hand by {player}
low: you cannot be sure of it, it has come a long way
medium: fairly reliable
twisted: it may have been twisted in the telling along the way
urgent: they meant it as urgent");

        public static void Touch() { var _ = TByHand.Key; }

        private static double Hours(Hero a, Hero b)
        {
            try
            {
                Vec2? Pos(Hero h) => h.PartyBelongedTo != null ? h.PartyBelongedTo.GetPosition2D : (h.CurrentSettlement ?? h.HomeSettlement)?.GetPosition2D;
                var pa = Pos(a); var pb = Pos(b);
                if (pa.HasValue && pb.HasValue)
                {
                    float d = pa.Value.Distance(pb.Value);
                    if (d < 3f) return 1;                                   // together, tells them in person
                    return Math.Max(2, d * S.Get.MessengerHoursPerDistanceX10 / 10.0);
                }
            }
            catch { }
            return 48;
        }

        public static void OnHourlyTick()
        {
            try
            {
                if (Messages.Count == 0) return;
                double now = CampaignTime.Now.ToDays;
                // Word the player was given to carry and never handed over
                foreach (var m in Messages.Where(x => x.ByPlayer && now > x.DeliveryDueDay).ToList())
                {
                    Messages.Remove(m);
                    m.Status = "lost"; m.FailureReason = "not carried";
                    string toName = Hero.Find(m.ToId)?.Name?.ToString() ?? "?";
                    NoteSender(m, TNotCarried.Format("player", P.Name.ToString(), "to", toName));
                    WorldSim.Say(L.F("AIL_e7b1c7b0", $"El recado de {m.FromName} para {toName} ya no vale: no lo entregaste."), Colors.Gray);
                }
                foreach (var m in Messages.Where(x => !x.ByPlayer && x.ArriveDay <= now).ToList())
                {
                    var to = Hero.Find(m.ToId);
                    if (to == null || !to.IsAlive)
                    {
                        Messages.Remove(m);
                        m.Status = "lost"; m.FailureReason = "dead";
                        if (!m.IsAnswer) NoteSender(m, TLostDead.Format("to", to?.Name?.ToString() ?? "?"));
                        Log.Info($"Errand from {m.FromName} lost: recipient is dead");
                        continue;
                    }
                    // A rider needs time to get word into a cell
                    if (to.IsPrisoner && !m.Delayed) { m.Delayed = true; m.ArriveDay = now + 2; continue; }
                    Messages.Remove(m);
                    m.Status = "delivered";
                    var mem = MemoryStore.Get(to.StringId, to.Name.ToString());
                    AddHeard(mem, new HeardItem { FromName = m.FromName, Text = m.Text, Secrecy = m.Secrecy, Day = now, OriginName = m.OriginName, Hops = m.Hops,
                                                  About = m.About, Confidence = m.Confidence, Importance = m.Importance, IsDistorted = m.IsDistorted });
                    Log.Info($"Errand delivered to {to.Name} (from {m.FromName}{(m.OriginName != null ? ", who learned it from " + m.OriginName : "")}): {m.Text}");
                    if (!m.IsAnswer)
                    {
                        if (m.RelayToId != null) Relay(m, to, now);
                        else Forward(m, to, now);
                        if (m.WantsAnswer && m.Hops == 0) Answer(m, to);
                    }

                    // Public news spreads by word of mouth
                    if (m.Secrecy == "public")
                        NewsStore.Add(new NewsItem
                        {
                            Type = "rumor", Importance = 2, Text = $"{m.FromName} has been telling people: {m.Text}",
                            TitleEs = L.F("AIL_5ff5abe9", $"Lo que cuenta {m.FromName}"), TextEs = "",
                            Heroes = new List<string> { m.FromId, m.ToId, P.StringId },
                            Kingdoms = to.MapFaction != null ? new List<string> { to.MapFaction.StringId } : new List<string>(),
                            SettlementId = (to.CurrentSettlement ?? to.HomeSettlement)?.StringId,
                        });
                }
            }
            catch (Exception ex) { Log.Error("NPC errands", ex); }
        }

        /// <summary>
        /// Word travels on from whoever receives it. A public message goes to a couple of their own people, a private
        /// one sometimes to the one they are closest to, a secret only if they are the kind to break a confidence.
        /// It never goes back to anyone it has already passed through, and stops after a few hands.
        /// </summary>
        private static void Forward(NpcMessage m, Hero holder, double now)
        {
            try
            {
                var cfg = S.Get;
                if (m.Hops >= cfg.ErrandMaxHops) return;
                int honor = 0;
                try { honor = holder.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor); } catch { }
                int many; string secrecy = m.Secrecy;
                int urgency = (m.Importance - 2) * 10;          // urgent news travels more, small talk less
                if (m.Secrecy == "public") many = m.Importance >= 3 ? 3 : m.Importance <= 1 ? 1 : 2;
                else if (m.Secrecy == "private") many = Rng.Next(100) < 35 - honor * 10 + urgency ? 1 : 0;
                else { many = honor < 0 && Rng.Next(100) < 15 + urgency ? 1 : 0; secrecy = "private"; }        // a broken confidence
                if (many == 0) return;
                var chain = m.Chain ?? new List<string> { m.FromId };
                var next = Confidants(holder, 12).Where(h => !chain.Contains(h.StringId) && h.StringId != m.ToId && !Messages.Any(x => x.ToId == h.StringId && x.Text == m.Text)).Take(many).ToList();
                foreach (var to in next) Pass(m, holder, to, secrecy, now, honor);
            }
            catch (Exception ex) { Log.Error("Forward errand", ex); }
        }

        /// <summary>The recipient was asked to hand it on to one named person: they do, unless they bear the sender ill will.</summary>
        private static void Relay(NpcMessage m, Hero holder, double now)
        {
            try
            {
                var target = Hero.Find(m.RelayToId);
                var from = Hero.Find(m.FromId);
                if (target == null || !target.IsAlive || target == holder) return;
                if (m.Hops >= S.Get.ErrandMaxHops || (m.Chain != null && m.Chain.Contains(target.StringId))) return;
                if (from != null && holder.GetRelation(from) < -10 && Rng.Next(100) < 60)
                {
                    NoteSender(m, TRelayRefused.Format("to", holder.Name.ToString(), "relay", target.Name.ToString()));
                    Log.Info($"Errand: {holder.Name} does not pass it to {target.Name} (bad relation with {m.FromName})");
                    return;
                }
                int honor = 0;
                try { honor = holder.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor); } catch { }
                Pass(m, holder, target, m.Secrecy, now, honor);
            }
            catch (Exception ex) { Log.Error("Pass an errand", ex); }
        }

        /// <summary>One more hand: it loses some trust, and a dishonest or distant teller may bend it.</summary>
        private static void Pass(NpcMessage m, Hero holder, Hero to, string secrecy, double now, int honor)
        {
            try
            {
                double hours = Hours(holder, to);
                var chain = m.Chain ?? new List<string> { m.FromId };
                var newChain = chain.ToList(); newChain.Add(holder.StringId);
                bool twisted = m.IsDistorted || Rng.Next(100) < 10 + 10 * m.Hops + (honor < 0 ? 15 : 0);
                int conf = Math.Max(10, m.Confidence - 15 - (twisted && !m.IsDistorted ? 10 : 0));
                Messages.Add(new NpcMessage
                {
                    Id = WorldSim.NewId("m"), FromId = holder.StringId, FromName = holder.Name.ToString(), ToId = to.StringId, Text = m.Text, Secrecy = secrecy,
                    Day = now, ArriveDay = now + hours / 24.0, DeliveryDueDay = now + hours / 24.0, OriginName = m.OriginName ?? m.FromName, Chain = newChain, Hops = m.Hops + 1,
                    About = m.About, Importance = m.Importance, Confidence = conf, IsDistorted = twisted,
                });
                Log.Info($"Errand forwarded by {holder.Name} to {to.Name} ({secrecy}, hop {m.Hops + 1}, trust {conf}{(twisted ? ", distorted" : "")}, arrives in {hours:0} h)");
            }
            catch (Exception ex) { Log.Error("Forward errand", ex); }
        }

        private static readonly Tpl TAnswer = new Tpl("info_exchange/errand_answer.txt",
@"You are {to_name}: {to_desc}
{from_name} ({relation_to_sender}) has sent you word about {about}: «{text}»
What you already know or have heard about {player}: {known}
They expect your answer. Give it in one or two sentences, in your own voice, as the word you send back to {from_name}: what you make of it, what you will do, or what you ask in return. It is a claim that reached you, not a fact you saw.
Answer ONLY with JSON: { ""answer"": ""your word back, in English"" }");

        private static double _answerDay = -1;
        private static int _answersToday;

        /// <summary>Whoever receives a message that asks for an answer sends one back (one short AI request, a few a day at most).</summary>
        private static void Answer(NpcMessage m, Hero to)
        {
            try
            {
                var cfg = S.Get;
                var from = Hero.Find(m.FromId);
                if (!cfg.ErrandAnswers || from == null || !from.IsAlive) return;
                double today = Math.Floor(CampaignTime.Now.ToDays);
                if (today != _answerDay) { _answerDay = today; _answersToday = 0; }
                if (_answersToday >= cfg.ErrandAnswersPerDay) { Log.Info($"Errand: {to.Name} does not reply today (daily reply cap)"); return; }
                _answersToday++;
                var s = Speaker.FromHero(to);
                var mem = s.Memory;
                string known = string.IsNullOrWhiteSpace(mem.Summary) ? (mem.ConversationCount > 0 ? "you have met them" : "you have never met them") : mem.Summary;
                if (known.Length > 500) known = known.Substring(0, 500);
                string desc = $"{(to.IsFemale ? "woman" : "man")}, {(int)to.Age}, {to.Culture?.Name}, {(to.IsLord ? "noble of clan " + to.Clan?.Name : to.IsNotable ? "notable" : "commoner")}"
                              + (mem.Profile != null && !mem.Profile.IsEmpty ? ". " + mem.Profile.Personality : "");
                string prompt = TAnswer.Format("to_name", to.Name.ToString(), "to_desc", desc, "from_name", m.FromName,
                    "relation_to_sender", ContextBuilder.DescribeRelation(to.GetRelation(from)), "player", P.Name.ToString(), "text", m.Text, "known", known,
                    "about", m.About ?? P.Name.ToString());
                int session = WorldSim.Session;
                Llm.LlmClient.CompleteAsync(new List<Llm.ChatMessage> { new Llm.ChatMessage("user", prompt) }, wantJson: true, task: "errand", maxTokensOverride: 200,
                    preferredProvider: cfg.ProviderWorld).ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        try
                        {
                            if (session != WorldSim.Session || res == null || !res.Ok) return;
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            string ans = ((string)Newtonsoft.Json.Linq.JObject.Parse(res.Text.Substring(a, b - a + 1))["answer"] ?? "").Trim();
                            if (ans.Length < 5 || !to.IsAlive || !from.IsAlive) return;
                            if (ans.Length > 400) ans = ans.Substring(0, 400);
                            double now = CampaignTime.Now.ToDays, hours = Hours(to, from);
                            Messages.Add(new NpcMessage
                            {
                                Id = WorldSim.NewId("m"), FromId = to.StringId, FromName = to.Name.ToString(), ToId = from.StringId,
                                Text = $"In answer to your word about {m.About ?? P.Name.ToString()}: {ans}", Secrecy = m.Secrecy == "public" ? "private" : m.Secrecy,
                                Day = now, ArriveDay = now + hours / 24.0, DeliveryDueDay = now + hours / 24.0, IsAnswer = true, Chain = new List<string> { to.StringId }, About = m.About,
                            });
                            Log.Info($"Reply from {to.Name} to {from.Name} (arrives in {hours:0} h): {ans}");
                        }
                        catch (Exception ex) { Log.Error("Errand reply", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Answer an errand", ex); }
        }

        /// <summary>Ambient chat topics that old saves stored. They aren't about the player.</summary>
        public static bool IsAmbientTopic(HeardItem x) => x == null || string.IsNullOrWhiteSpace(x.Text) || x.Text.StartsWith("(talking together)", StringComparison.Ordinal);

        /// <summary>Messages and overheard things that are actually about the player.</summary>
        public static IEnumerable<HeardItem> AboutPlayer(HeroMemory mem)
        {
            if (mem?.Heard == null) return Enumerable.Empty<HeardItem>();
            ForgetOverheard(mem);
            return mem.Heard.Where(x => !IsAmbientTopic(x) && x.About == null).ToList();
        }

        /// <summary>Word they were sent about other people.</summary>
        public static IEnumerable<HeardItem> AboutOthers(HeroMemory mem)
        {
            if (mem?.Heard == null) return Enumerable.Empty<HeardItem>();
            ForgetOverheard(mem);
            return mem.Heard.Where(x => !IsAmbientTopic(x) && x.About != null).ToList();
        }

        /// <summary>FromName of something caught in passing, as opposed to something they were told.</summary>
        public const string OwnEars = "your own ears";

        /// <summary>What was only overheard fades after a while. Checked right before it would go into a prompt.</summary>
        public static void ForgetOverheard(HeroMemory mem)
        {
            try
            {
                int days = S.Get.OverheardLifespanDays;
                if (mem?.Heard == null || mem.Heard.Count == 0) return;
                if (days <= 0) days = int.MaxValue;
                double now = CampaignTime.Now.ToDays;
                mem.Heard.RemoveAll(x => x != null && x.FromName == OwnEars && now - x.Day > days);
                int told = S.Get.HeardLifespanDays;
                if (told > 0) mem.Heard.RemoveAll(x => x != null && x.FromName != OwnEars && now - x.Day > told);
            }
            catch { }
        }

        /// <summary>Clears old ambient topics first so they don't push out real messages.</summary>
        public static void AddHeard(HeroMemory mem, HeardItem item)
        {
            if (mem == null || item == null) return;
            if (mem.Heard == null) mem.Heard = new List<HeardItem>();
            mem.Heard.RemoveAll(IsAmbientTopic);
            mem.Heard.Add(item);
            while (mem.Heard.Count > S.Get.MaxHeardPerHero) mem.Heard.RemoveAt(0);
        }

        /// <summary>For the prompt: what this NPC heard about the player from others.</summary>
        public static string DescribeHeard(HeroMemory mem)
        {
            return Describe(AboutPlayer(mem).ToList(), false);
        }

        /// <summary>For the prompt: what this NPC was sent about other people.</summary>
        public static string DescribeHeardOthers(HeroMemory mem) => Describe(AboutOthers(mem).ToList(), true);

        private static string Describe(List<HeardItem> list, bool sayWho)
        {
            if (list.Count == 0) return "";
            double now = CampaignTime.Now.ToDays;
            string player = P?.Name?.ToString() ?? "";
            string Tag(string key) => Diplomacy.Line(THeardTags, key).Replace("{player}", player);
            var sb = new StringBuilder();
            foreach (var x in list.Skip(Math.Max(0, list.Count - 6)))
            {
                var tags = new List<string>();
                if (x.ByPlayer) tags.Add(Tag("by_hand"));
                if (x.Importance >= 3) tags.Add(Tag("urgent"));
                if (x.IsDistorted) tags.Add(Tag("twisted"));
                else if (x.Confidence < 50) tags.Add(Tag("low"));
                else if (x.Confidence < 75) tags.Add(Tag("medium"));
                tags.RemoveAll(t => t.Length == 0);
                sb.AppendLine($"- {(int)(now - x.Day)} day(s) ago, from {x.FromName}" + (sayWho ? $", about {x.About}" : "") +
                              (string.IsNullOrEmpty(x.OriginName) ? "" : x.Hops >= 2 ? $", who had it from others (it began with {x.OriginName}; third-hand, details may have changed on the way)" : $", who had it from {x.OriginName}") +
                              (x.Secrecy == "secret" ? " (told to you in confidence: do not reveal who told you)" : x.Secrecy == "private" ? " (in private)" : "") +
                              (tags.Count > 0 ? " (" + string.Join("; ", tags) + ")" : "") + $": {x.Text}");
            }
            return sb.ToString().TrimEnd();
        }

        public static bool HasFreshNews(HeroMemory mem) => AboutPlayer(mem).Any(x => CampaignTime.Now.ToDays - x.Day <= 10);
    }

    /// <summary>
    /// A noble who knows you and passes nearby can ride up to talk if they have a reason
    /// (an open task, something they heard about you, an unanswered letter, affection or a grudge).
    /// </summary>
    internal static class MapInitiative
    {
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        public static void OnHourlyTick()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableMapInitiative || Campaign.Current == null || Initiative.Busy || !WorldSim.OnMap()) return;
                var main = MobileParty.MainParty;
                // A captive player is already with their captor: nobody rides up to "intercept" them.
                // The captor may come to see them instead.
                if (P == null) return;
                if (P.IsPrisoner || PlayerCaptivity.IsCaptive) { CaptorVisit(); return; }
                if (main == null || main.MapEvent != null || main.CurrentSettlement != null || main.Army != null && main.Army.LeaderParty != main) return;
                double now = CampaignTime.Now.ToDays;
                if (MemoryStore.LastInitiativeDay >= 0 && now - MemoryStore.LastInitiativeDay < cfg.MapInitiativeMinDaysBetween) return;
                if (OrderSystem.Orders.Any(o => o.Type == "approach")) return;
                if (Rng.Next(100) >= cfg.MapInitiativeChancePercent) return;

                float range = Math.Max(4f, main.SeeingRange * 0.9f);
                var here = main.GetPosition2D;
                var candidates = new List<(Hero h, double w, string why)>();
                // People riding with you don't need to approach, they just ask for a moment
                try
                {
                    foreach (var e in main.MemberRoster.GetTroopRoster())
                    {
                        var h = e.Character?.HeroObject;
                        if (h == null || h == P || !h.IsAlive || h.IsWounded) continue;
                        var mem = MemoryStore.Find(h.StringId);
                        if (mem == null || mem.ConversationCount == 0) continue;
                        if (mem.LastInitiativeDay >= 0 && now - mem.LastInitiativeDay < cfg.MapInitiativeDaysPerNpc) continue;
                        if (mem.LastConversationDay >= 0 && now - mem.LastConversationDay < 1) continue;
                        var (w, why) = Motive(h, mem);
                        if (mem.LastConversationDay >= 0 && now - mem.LastConversationDay >= 20) { w += 1.5; why = (why.Length > 0 ? why + "; " : "") + "you ride together every day and have not really talked in weeks"; }
                        if (w > 0) candidates.Add((h, w, why));
                    }
                }
                catch { }
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsActive || p.IsMainParty || !p.IsLordParty || p.LeaderHero == null || p.MapEvent != null || p.CurrentSettlement != null) continue;
                    if (p.Army != null && p.Army.LeaderParty != p) continue;
                    if (p.GetPosition2D.Distance(here) > range) continue;
                    var h = p.LeaderHero;
                    if (h.MapFaction != null && P.MapFaction != null && h.MapFaction != P.MapFaction && h.MapFaction.IsAtWarWith(P.MapFaction))
                    {
                        // An enemy who knows you, holds a grudge and knows they're stronger rides you down to say it to your face
                        // first (at most every three days per character)
                        var hm = MemoryStore.Find(h.StringId);
                        if (hm == null || hm.ConversationCount == 0 || !OrderSystem.CanTakeOrders(h) || OrderSystem.Find(h) != null) continue;
                        if (hm.LastInitiativeDay >= 0 && now - hm.LastInitiativeDay < 3) continue;
                        int hrel = (int)h.GetRelationWithPlayer();
                        bool grudge = hrel <= -20 || Bonds.Tension(hm) >= 40 || hm.LiesSuspected > 0;
                        if (!grudge || p.Party.CalculateCurrentStrength() < main.Party.CalculateCurrentStrength() * 1.2f) continue;
                        candidates.Add((h, 2.0, "HOSTILE|" + (hrel <= -20 ? "you bear them a grudge" : hm.LiesSuspected > 0 ? "they lied to you and you have not forgotten" : "things were left ugly between you")));
                        continue;
                    }
                    if (!OrderSystem.CanTakeOrders(h) || OrderSystem.Find(h) != null) continue;
                    var mem = MemoryStore.Find(h.StringId);
                    if (mem == null || mem.ConversationCount == 0) continue;                // only people who already know you
                    if (mem.LastInitiativeDay >= 0 && now - mem.LastInitiativeDay < cfg.MapInitiativeDaysPerNpc) continue;
                    if (mem.LastConversationDay >= 0 && now - mem.LastConversationDay < 1) continue;

                    var (w, why) = Motive(h, mem);
                    if (w > 0) candidates.Add((h, w, why));
                }
                if (candidates.Count == 0) return;
                double total = candidates.Sum(c => c.w), roll = Rng.NextDouble() * total;
                foreach (var c in candidates)
                {
                    roll -= c.w;
                    if (roll > 0) continue;
                    var mem = MemoryStore.Find(c.h.StringId);
                    mem.LastInitiativeDay = now;
                    mem.InitiativeReason = c.why;
                    MemoryStore.LastInitiativeDay = now;
                    if (c.h.PartyBelongedTo == main)
                    {
                        Log.Info($"Map initiative: {c.h.Name}, from your party, asks to talk ({c.why})");
                        Initiative.StartMap(c.h, TPartyAsk.Format("reasons", c.why));
                        return;
                    }
                    OrderSystem.Give(c.h, "approach", null, P.Name.ToString(), 0.5);
                    Log.Info($"Map initiative: {c.h.Name} heads for the player ({c.why})");
                    return;
                }
            }
            catch (Exception ex) { Log.Error("Map initiative", ex); }
        }

        /// <summary>
        /// While the player is a prisoner, the lord holding them may come to speak: at most once every two days,
        /// and not in the first hours after the capture.
        /// </summary>
        private static void CaptorVisit()
        {
            var captor = PlayerCaptivity.CaptorParty;
            if (captor == null) return;
            Hero h = captor.IsMobile ? captor.LeaderHero : null;
            string place = "your camp, under guard";
            if (captor.IsSettlement && captor.Settlement != null)
            {
                // In a dungeon: the lord of the place, only if they are inside the walls right now
                var st = captor.Settlement;
                h = st.OwnerClan?.Heroes.FirstOrDefault(x => x.IsAlive && !x.IsChild && !x.IsPrisoner && x.CurrentSettlement == st && (x == st.OwnerClan.Leader || x == st.Town?.Governor));
                place = "the dungeon of " + st.Name + ", which you hold";
            }
            if (h == null || h == P || !h.IsAlive || h.IsPrisoner || h.IsChild) return;
            double now = CampaignTime.Now.ToDays;
            double held = 0;
            try { held = PlayerCaptivity.CaptivityStartTime.ElapsedDaysUntilNow; } catch { }
            if (held < 0.15) return;
            var mem = MemoryStore.Get(h.StringId, h.Name.ToString());
            if (mem == null) return;
            if (mem.LastInitiativeDay >= 0 && now - mem.LastInitiativeDay < 2) return;
            if (mem.LastConversationDay >= 0 && now - mem.LastConversationDay < 0.25) return;
            if (Rng.Next(100) >= 12) return;

            int rel = (int)h.GetRelationWithPlayer();
            string mood = rel <= -20 || Bonds.Tension(mem) >= 40 || mem.LiesSuspected > 0 ? "you bear them a grudge and it shows"
                : rel >= 20 ? "you think well of them and holding them sits badly with you"
                : mem.ConversationCount == 0 ? "you have never spoken before and want to see who you have caught"
                : "you have no strong feelings about them: they are a prisoner with a price";
            mem.LastInitiativeDay = now;
            mem.InitiativeReason = "CAPTOR|" + mood;
            MemoryStore.LastInitiativeDay = now;
            string days = held < 1 ? "since earlier today" : "for " + (int)held + " day(s)";
            Log.Info($"Map initiative: {h.Name} goes to see their prisoner ({mood})");
            Initiative.StartCaptor(h, TCaptorVisit.Format("place", place, "days", days, "mood", mood));
        }

        private static readonly Tpl TCaptorVisit = new Tpl("npc_initiative/captor_visit.txt",
@"They are YOUR PRISONER, held in {place} {days}. They are disarmed and bound, and they go nowhere unless you say so. You have come to see them, on your own initiative: {mood}.
You did not just meet or catch them on the road: the capture is done. Open with what you came for (questions, terms, a ransom, a warning, scorn or an apology, whatever fits you and your history with them). No small talk first.");

        /// <summary>Does the NPC have something to say? Weight and reason (English, goes into their prompt).</summary>
        private static (double w, string why) Motive(Hero h, HeroMemory mem)
        {
            var reasons = new List<string>(); double w = 0;
            if (QuestSystem.ActiveFrom(h.StringId) != null) { w += 3; reasons.Add("you gave them a task that is still open and want to know how it goes"); }
            if (InfoExchange.HasFreshNews(mem)) { w += 3; reasons.Add("you have recently heard things about them from others and want to hear it from them"); }
            if (mem.Romance >= 20) { w += 2; reasons.Add("you have feelings for them and seized the chance to see them"); }
            else
            {
                // An unattached character who likes the player may be the one to make the first move
                try
                {
                    if (S.Get.EnableNpcRomance && h.Spouse == null && P.Spouse == null && NpcRomance.Of(h) == null && h.GetRelationWithPlayer() >= 30
                        && Bonds.RomancePossible(Speaker.FromHero(h)) && Rng.Next(100) < 25)
                    {
                        w += 1.5;
                        reasons.Add("you have taken a liking to them that is more than friendship and want them to know it, in your own way and at your own pace");
                    }
                }
                catch { }
            }
            if (mem.OwesReplyToPlayer) { w += 1; reasons.Add("they wrote to you and you would rather answer in person"); }
            int rel = (int)h.GetRelationWithPlayer();
            if (rel >= 25) { w += 1.5; reasons.Add("you count them a friend and were glad to see their banner"); }
            else if (rel <= -25) { w += 1; reasons.Add("you bear them a grudge and want to have it out, in words"); }
            if (Bonds.Tension(mem) >= 40) { w += 1; reasons.Add("things were left tense between you last time"); }
            if (h.MapFaction != null && h.MapFaction == P.MapFaction && h.MapFaction.IsKingdomFaction && WorldSim.Kingdoms.Any(k => k != h.MapFaction && h.MapFaction.IsAtWarWith(k)))
            { w += 0.7; reasons.Add("you serve the same realm, it is at war, and there is news and plans to share"); }
            return (w, string.Join("; ", reasons));
        }

        private static readonly Tpl TMapApproach = new Tpl("npc_initiative/map_approach.txt",
@"You saw their party on the road and rode over with your own men to speak with them, on your own initiative. Why you came: {reasons}.
Open with that reason, in your own voice; do not make small talk and do not ask ""what brings you here"" (it is you who came). If what you want is to ask about a task, about something you heard, or to settle a grievance, say so plainly.");

        private static readonly Tpl TPartyAsk = new Tpl("npc_initiative/party_member.txt",
@"You travel in their party and have asked them for a moment, on your own initiative. Why: {reasons}.
Open with that, in your own voice, as someone who shares the road and the camp with them. No small talk first.");

        private static readonly Tpl THostile = new Tpl("npc_initiative/hostile_intercept.txt",
@"You are at war with their side and your party is the stronger. You have ridden them down and barred their way, and you want to say something to their face before anything else. Why: {reasons}.
Open with it, in your own voice: a demand, a taunt, an offer of terms, or a last word before blood. You decide how this ends with one of the actions open to you (attack them, let them pass, or take their surrender if they yield); do not decide in your very first words.");

        public static void Touch() { var _ = TMapApproach.Key; }

        /// <summary>The NPC's party reached the player's.</summary>
        public static void Arrived(Hero h)
        {
            var mem = MemoryStore.Find(h.StringId);
            string why = string.IsNullOrWhiteSpace(mem?.InitiativeReason) ? "you wanted a word with them" : mem.InitiativeReason;
            if (why.StartsWith("HOSTILE|"))
            {
                // Only if still at war and still able, otherwise drop it
                bool still = h.MapFaction != null && P.MapFaction != null && h.MapFaction.IsAtWarWith(P.MapFaction);
                if (still) Initiative.StartMap(h, THostile.Format("reasons", why.Substring(8)), hostile: true);
                return;
            }
            Initiative.StartMap(h, TMapApproach.Format("reasons", why));
        }
    }
}
