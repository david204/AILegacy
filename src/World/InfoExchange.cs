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
    /// <summary>Algo que un NPC le cuenta a otro sobre el jugador. Viaja: no llega al instante.</summary>
    public class NpcMessage
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("from")] public string FromId;
        [JsonProperty("from_name")] public string FromName;
        [JsonProperty("to")] public string ToId;
        [JsonProperty("text")] public string Text;          // en inglés: lo leerá otro NPC
        /// <summary>public = lo irá contando · private = solo para él · secret = bajo palabra de callar</summary>
        [JsonProperty("secrecy")] public string Secrecy = "private";
        [JsonProperty("day")] public double Day;
        [JsonProperty("arrive")] public double ArriveDay;
    }

    /// <summary>Lo que un NPC ha sabido por boca de otro.</summary>
    public class HeardItem
    {
        [JsonProperty("from")] public string FromName;
        [JsonProperty("text")] public string Text;
        [JsonProperty("secrecy")] public string Secrecy = "private";
        [JsonProperty("day")] public double Day;
    }

    /// <summary>
    /// Intercambio de información entre NPC: lo que le cuentas a uno (o lo que haces delante de él) puede llegar a otro,
    /// con retraso según la distancia y con un grado de secreto. Lo que llega es una afirmación, no un hecho.
    /// </summary>
    internal static class InfoExchange
    {
        public static List<NpcMessage> Messages = new List<NpcMessage>();
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        /// <summary>A quién tiene sentido que este NPC le cuente algo: los suyos (familia, señor, jefe de clan, amigos).</summary>
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

        /// <summary>El NPC decide mandar recado a otro. Devuelve el texto para enseñar al jugador, o null.</summary>
        public static string Send(Speaker from, string toName, string text, string secrecy)
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
            });
            Log.Info($"Recado de {from.Name} para {to.Name} ({secrecy}, llega en {hours:0} h): {text}");
            // Lo que le cuenta a otro en privado o en secreto no se le anuncia al jugador: no tiene cómo saberlo, y además
            // delataba a quién se lo contaba. Solo se avisa de lo que piensa ir contando abiertamente.
            // (Cadena vacía = hecho, sin nada que enseñar; null = no se ha podido.)
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
                    if (d < 3f) return 1;                                   // están juntos: se lo dice en persona
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
                    AddHeard(mem, new HeardItem { FromName = m.FromName, Text = m.Text, Secrecy = m.Secrecy, Day = now });
                    Log.Info($"Recado entregado a {to.Name} (de {m.FromName}): {m.Text}");

                    // Lo público corre de boca en boca
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

        /// <summary>Temas de charlas ambientales que guardaban partidas antiguas: no tratan del jugador.</summary>
        public static bool IsAmbientTopic(HeardItem x) => x == null || string.IsNullOrWhiteSpace(x.Text) || x.Text.StartsWith("(talking together)", StringComparison.Ordinal);

        /// <summary>Lo que sí trata del jugador (recados y lo oído de pasada).</summary>
        public static IEnumerable<HeardItem> AboutPlayer(HeroMemory mem) => mem?.Heard == null ? Enumerable.Empty<HeardItem>() : mem.Heard.Where(x => !IsAmbientTopic(x));

        /// <summary>Añade algo oído: antes limpia los temas ambientales viejos para que no echen a los recados reales.</summary>
        public static void AddHeard(HeroMemory mem, HeardItem item)
        {
            if (mem == null || item == null) return;
            if (mem.Heard == null) mem.Heard = new List<HeardItem>();
            mem.Heard.RemoveAll(IsAmbientTopic);
            mem.Heard.Add(item);
            while (mem.Heard.Count > S.Get.MaxHeardPerHero) mem.Heard.RemoveAt(0);
        }

        /// <summary>Para el prompt: lo que este NPC ha sabido del jugador por otros.</summary>
        public static string DescribeHeard(HeroMemory mem)
        {
            var list = AboutPlayer(mem).ToList();
            if (list.Count == 0) return "";
            double now = CampaignTime.Now.ToDays;
            var sb = new StringBuilder();
            foreach (var x in list.Skip(Math.Max(0, list.Count - 6)))
                sb.AppendLine($"- {(int)(now - x.Day)} day(s) ago, from {x.FromName}" +
                              (x.Secrecy == "secret" ? " (told to you in confidence: do not reveal who told you)" : x.Secrecy == "private" ? " (in private)" : "") + $": {x.Text}");
            return sb.ToString().TrimEnd();
        }

        public static bool HasFreshNews(HeroMemory mem) => AboutPlayer(mem).Any(x => CampaignTime.Now.ToDays - x.Day <= 10);
    }

    /// <summary>
    /// Iniciativa en el mapa: un noble que te conoce y pasa cerca puede cabalgar hasta ti para hablar,
    /// si tiene un motivo (un encargo abierto, algo que ha oído de ti, una deuda de carta, afecto o rencor).
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
                if (main == null || main.MapEvent != null || main.CurrentSettlement != null || main.Army != null && main.Army.LeaderParty != main) return;
                double now = CampaignTime.Now.ToDays;
                if (MemoryStore.LastInitiativeDay >= 0 && now - MemoryStore.LastInitiativeDay < cfg.MapInitiativeMinDaysBetween) return;
                if (OrderSystem.Orders.Any(o => o.Type == "approach")) return;
                if (Rng.Next(100) >= cfg.MapInitiativeChancePercent) return;

                float range = Math.Max(4f, main.SeeingRange * 0.9f);
                var here = main.GetPosition2D;
                var candidates = new List<(Hero h, double w, string why)>();
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsActive || p.IsMainParty || !p.IsLordParty || p.LeaderHero == null || p.MapEvent != null || p.CurrentSettlement != null) continue;
                    if (p.Army != null && p.Army.LeaderParty != p) continue;
                    if (p.GetPosition2D.Distance(here) > range) continue;
                    var h = p.LeaderHero;
                    if (h.MapFaction != null && P.MapFaction != null && h.MapFaction != P.MapFaction && h.MapFaction.IsAtWarWith(P.MapFaction)) continue;   // a los enemigos ya los mueve el juego
                    if (!OrderSystem.CanTakeOrders(h) || OrderSystem.Find(h) != null) continue;
                    var mem = MemoryStore.Find(h.StringId);
                    if (mem == null || mem.ConversationCount == 0) continue;                // solo quien ya te conoce
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
                    OrderSystem.Give(c.h, "approach", null, P.Name.ToString(), 0.5);
                    Log.Info($"Iniciativa en el mapa: {c.h.Name} va hacia el jugador ({c.why})");
                    return;
                }
            }
            catch (Exception ex) { Log.Error("Iniciativa en el mapa", ex); }
        }

        /// <summary>¿Tiene algo que decirte? Peso y motivo (en inglés, va a su prompt).</summary>
        private static (double w, string why) Motive(Hero h, HeroMemory mem)
        {
            var reasons = new List<string>(); double w = 0;
            if (QuestSystem.ActiveFrom(h.StringId) != null) { w += 3; reasons.Add("you gave them a task that is still open and want to know how it goes"); }
            if (InfoExchange.HasFreshNews(mem)) { w += 3; reasons.Add("you have recently heard things about them from others and want to hear it from them"); }
            if (mem.Romance >= 20) { w += 2; reasons.Add("you have feelings for them and seized the chance to see them"); }
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

        public static void Touch() { var _ = TMapApproach.Key; }

        /// <summary>El grupo del NPC ha llegado junto al del jugador.</summary>
        public static void Arrived(Hero h)
        {
            var mem = MemoryStore.Find(h.StringId);
            string why = string.IsNullOrWhiteSpace(mem?.InitiativeReason) ? "you wanted a word with them" : mem.InitiativeReason;
            Initiative.StartMap(h, TMapApproach.Format("reasons", why));
        }
    }
}
