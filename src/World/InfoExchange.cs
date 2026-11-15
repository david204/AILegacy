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

        /// <summary>The NPC sends word to another one. Returns the text to show the player, "" for nothing to show, or null on failure.</summary>
        public static string Send(Speaker from, string toName, string text, string secrecy, bool wantsAnswer = false)
        {
            if (!from.IsHero || string.IsNullOrWhiteSpace(text)) return null;
            string n = WorldSim.Norm(toName);
            if (n.Length < 2) return null;
            var pool = Hero.AllAliveHeroes.Where(h => h != from.Hero && h != P && !h.IsChild).ToList();
            var to = Hero.Find(toName) ?? pool.FirstOrDefault(h => WorldSim.Norm(h.Name.ToString()) == n)
                     ?? Confidants(from.Hero, 20).FirstOrDefault(h => WorldSim.Norm(h.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(h.Name.ToString())))
                     ?? pool.FirstOrDefault(h => (h.IsLord || h.IsNotable) && (WorldSim.Norm(h.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(h.Name.ToString()))));
            if (to == null || to == from.Hero || to == P) { Log.Warn($"Recado de {from.Name}: destinatario '{toName}' no encontrado"); return null; }
            secrecy = (secrecy ?? "").Trim().ToLowerInvariant();
            if (secrecy != "public" && secrecy != "secret") secrecy = "private";
            if (text.Length > 600) text = text.Substring(0, 600);

            double hours = Hours(from.Hero, to);
            Messages.Add(new NpcMessage
            {
                Id = WorldSim.NewId("m"), FromId = from.Hero.StringId, FromName = from.Name, ToId = to.StringId, Text = text, Secrecy = secrecy,
                Day = CampaignTime.Now.ToDays, ArriveDay = CampaignTime.Now.ToDays + hours / 24.0,
                WantsAnswer = wantsAnswer && S.Get.ErrandAnswers,
                Chain = new List<string> { from.Hero.StringId },
            });
            Log.Info($"Recado de {from.Name} para {to.Name} ({secrecy}, llega en {hours:0} h): {text}");
            // Private or secret messages aren't announced to the player. They'd have no way to know, and it gave away
            // who was being told. Only public ones get a notice.
            return secrecy == "public" ? L.F("AIL_c009f7dc", $"{from.Name} hará llegar noticia de esto a {to.Name}.") : "";
        }

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
                foreach (var m in Messages.Where(x => x.ArriveDay <= now).ToList())
                {
                    Messages.Remove(m);
                    var to = Hero.Find(m.ToId);
                    if (to == null || !to.IsAlive) continue;
                    var mem = MemoryStore.Get(to.StringId, to.Name.ToString());
                    AddHeard(mem, new HeardItem { FromName = m.FromName, Text = m.Text, Secrecy = m.Secrecy, Day = now, OriginName = m.OriginName, Hops = m.Hops });
                    Log.Info($"Recado entregado a {to.Name} (de {m.FromName}{(m.OriginName != null ? ", que lo supo por " + m.OriginName : "")}): {m.Text}");
                    if (!m.IsAnswer)
                    {
                        Forward(m, to, now);
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
            catch (Exception ex) { Log.Error("Recados entre NPC", ex); }
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
                if (m.Secrecy == "public") many = 2;
                else if (m.Secrecy == "private") many = Rng.Next(100) < 35 - honor * 10 ? 1 : 0;
                else { many = honor < 0 && Rng.Next(100) < 15 ? 1 : 0; secrecy = "private"; }        // a broken confidence
                if (many == 0) return;
                var chain = m.Chain ?? new List<string> { m.FromId };
                var next = Confidants(holder, 12).Where(h => !chain.Contains(h.StringId) && h.StringId != m.ToId && !Messages.Any(x => x.ToId == h.StringId && x.Text == m.Text)).Take(many).ToList();
                foreach (var to in next)
                {
                    double hours = Hours(holder, to);
                    var newChain = chain.ToList(); newChain.Add(holder.StringId);
                    Messages.Add(new NpcMessage
                    {
                        Id = WorldSim.NewId("m"), FromId = holder.StringId, FromName = holder.Name.ToString(), ToId = to.StringId, Text = m.Text, Secrecy = secrecy,
                        Day = now, ArriveDay = now + hours / 24.0, OriginName = m.OriginName ?? m.FromName, Chain = newChain, Hops = m.Hops + 1,
                    });
                    Log.Info($"Recado reenviado por {holder.Name} a {to.Name} ({secrecy}, mano {m.Hops + 1}, llega en {hours:0} h)");
                }
            }
            catch (Exception ex) { Log.Error("Reenviar recado", ex); }
        }

        private static readonly Tpl TAnswer = new Tpl("info_exchange/errand_answer.txt",
@"You are {to_name}: {to_desc}
{from_name} ({relation_to_sender}) has sent you word about {player}: «{text}»
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
                if (_answersToday >= cfg.ErrandAnswersPerDay) { Log.Info($"Recado: {to.Name} no contesta hoy (tope diario de respuestas)"); return; }
                _answersToday++;
                var s = Speaker.FromHero(to);
                var mem = s.Memory;
                string known = string.IsNullOrWhiteSpace(mem.Summary) ? (mem.ConversationCount > 0 ? "you have met them" : "you have never met them") : mem.Summary;
                if (known.Length > 500) known = known.Substring(0, 500);
                string desc = $"{(to.IsFemale ? "woman" : "man")}, {(int)to.Age}, {to.Culture?.Name}, {(to.IsLord ? "noble of clan " + to.Clan?.Name : to.IsNotable ? "notable" : "commoner")}"
                              + (mem.Profile != null && !mem.Profile.IsEmpty ? ". " + mem.Profile.Personality : "");
                string prompt = TAnswer.Format("to_name", to.Name.ToString(), "to_desc", desc, "from_name", m.FromName,
                    "relation_to_sender", ContextBuilder.DescribeRelation(to.GetRelation(from)), "player", P.Name.ToString(), "text", m.Text, "known", known);
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
                                Text = $"In answer to your word about {P.Name}: {ans}", Secrecy = m.Secrecy == "public" ? "private" : m.Secrecy,
                                Day = now, ArriveDay = now + hours / 24.0, IsAnswer = true, Chain = new List<string> { to.StringId },
                            });
                            Log.Info($"Respuesta de {to.Name} a {from.Name} (llega en {hours:0} h): {ans}");
                        }
                        catch (Exception ex) { Log.Error("Respuesta a un recado", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Contestar un recado", ex); }
        }

        /// <summary>Ambient chat topics that old saves stored. They aren't about the player.</summary>
        public static bool IsAmbientTopic(HeardItem x) => x == null || string.IsNullOrWhiteSpace(x.Text) || x.Text.StartsWith("(talking together)", StringComparison.Ordinal);

        /// <summary>Messages and overheard things that are actually about the player.</summary>
        public static IEnumerable<HeardItem> AboutPlayer(HeroMemory mem)
        {
            if (mem?.Heard == null) return Enumerable.Empty<HeardItem>();
            ForgetOverheard(mem);
            return mem.Heard.Where(x => !IsAmbientTopic(x)).ToList();
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
            var list = AboutPlayer(mem).ToList();
            if (list.Count == 0) return "";
            double now = CampaignTime.Now.ToDays;
            var sb = new StringBuilder();
            foreach (var x in list.Skip(Math.Max(0, list.Count - 6)))
                sb.AppendLine($"- {(int)(now - x.Day)} day(s) ago, from {x.FromName}" +
                              (string.IsNullOrEmpty(x.OriginName) ? "" : x.Hops >= 2 ? $", who had it from others (it began with {x.OriginName}; third-hand, details may have changed on the way)" : $", who had it from {x.OriginName}") +
                              (x.Secrecy == "secret" ? " (told to you in confidence: do not reveal who told you)" : x.Secrecy == "private" ? " (in private)" : "") + $": {x.Text}");
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
                        Log.Info($"Iniciativa en el mapa: {c.h.Name}, de tu partida, pide hablar ({c.why})");
                        Initiative.StartMap(c.h, TPartyAsk.Format("reasons", c.why));
                        return;
                    }
                    OrderSystem.Give(c.h, "approach", null, P.Name.ToString(), 0.5);
                    Log.Info($"Iniciativa en el mapa: {c.h.Name} va hacia el jugador ({c.why})");
                    return;
                }
            }
            catch (Exception ex) { Log.Error("Iniciativa en el mapa", ex); }
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
            Log.Info($"Iniciativa en el mapa: {h.Name} va a ver a su prisionero ({mood})");
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
