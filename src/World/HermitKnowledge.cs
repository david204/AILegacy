using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace AILegacy.World
{
    /// <summary>
    /// What the hermit sees, as in AIInfluence: everything about the player (their person, house, lands, forces,
    /// purse, skills and temper), the people close to them, what they have done lately, and the whole world below
    /// (realms, events, rulers' words, the lore and the hidden truths). In English, for his prompt.
    /// </summary>
    internal static class HermitKnowledge
    {
        private const int MaxPlayerEvents = 30;

        public static string Build()
        {
            var sb = new StringBuilder();
            Part(sb, "player", Player);
            Part(sb, "circle", Circle);
            Part(sb, "player events", PlayerEvents);
            Part(sb, "world", World);
            return sb.ToString().TrimEnd();
        }

        private static void Part(StringBuilder sb, string what, Func<string> f)
        {
            try { var s = f(); if (!string.IsNullOrWhiteSpace(s)) { sb.AppendLine(s.TrimEnd()); sb.AppendLine(); } }
            catch (Exception ex) { Log.Error("Hermit knowledge: " + what, ex); }
        }

        private static string Days(double day)
        {
            double d = CampaignTime.Now.ToDays - day;
            return d < 1 ? "today" : $"{(int)d} day(s) ago";
        }

        private static string Where(Hero h)
        {
            try
            {
                if (h == null) return "unknown";
                if (h.IsPrisoner) return "a prisoner" + (h.CurrentSettlement != null ? " in " + h.CurrentSettlement.Name : h.PartyBelongedToAsPrisoner != null ? " of " + h.PartyBelongedToAsPrisoner.Name : "");
                var main = MobileParty.MainParty;
                if (h.PartyBelongedTo != null && h.PartyBelongedTo == main) return "travelling in the wanderer's party";
                if (h.PartyBelongedTo != null && main?.Army != null && h.PartyBelongedTo.Army == main.Army) return "in the wanderer's army";
                if (h.CurrentSettlement != null) return "in " + h.CurrentSettlement.Name;
                if (h.PartyBelongedTo != null) return h.PartyBelongedTo.LeaderHero == h ? "on the roads, leading their own party" : "on the roads, in the party of " + h.PartyBelongedTo.LeaderHero?.Name;
                return "somewhere in the world";
            }
            catch { return "unknown"; }
        }

        // ---- the wanderer

        private static string Player()
        {
            var p = Hero.MainHero;
            if (p == null) return "";
            var clan = Clan.PlayerClan;
            var party = MobileParty.MainParty;
            var sb = new StringBuilder();
            sb.AppendLine("=== THIS WANDERER (you see everything about them) ===");
            sb.AppendLine($"- Name: {p.Name} (id {p.StringId}), age {(int)p.Age}, {(p.IsFemale ? "woman" : "man")}, culture {p.Culture?.Name}, {(p.IsLord ? "a noble" : "a commoner")}.");
            var ws = WorldSim.State;
            if (!string.IsNullOrWhiteSpace(ws?.PlayerDescription)) sb.AppendLine("- Who they are taken to be: " + ws.PlayerDescription.Trim());
            if (!string.IsNullOrWhiteSpace(ws?.PlayerFamily)) sb.AppendLine("- Their family, as it is told: " + ws.PlayerFamily.Trim());
            sb.AppendLine("- They came up to you at the Retreat on Mount Erithrys to speak.");
            if (clan != null)
            {
                sb.AppendLine($"- Clan: {clan.Name} (id {clan.StringId}), tier {clan.Tier}, renown {(int)clan.Renown}, influence {(int)clan.Influence}{(clan.Leader == p ? ", and they lead it" : clan.Leader != null ? ", led by " + clan.Leader.Name : "")}.");
                var k = clan.Kingdom;
                if (k == null) sb.AppendLine("- Kingdom: none; they answer to no crown.");
                else if (k.Leader == p) sb.AppendLine($"- Kingdom: they rule {k.Name} (id {k.StringId}).");
                else if (clan.IsUnderMercenaryService) sb.AppendLine($"- Kingdom: a mercenary in the pay of {k.Name} (id {k.StringId}), not sworn to it.");
                else sb.AppendLine($"- Kingdom: vassal of {k.Name} (id {k.StringId}), ruled by {k.Leader?.Name}.");
                var fiefs = clan.Settlements.Where(s => s.IsTown || s.IsCastle).Select(s => $"{s.Name} ({(s.IsTown ? "town" : "castle")}, id {s.StringId})").ToList();
                var villages = clan.Settlements.Where(s => s.IsVillage).Select(s => s.Name.ToString()).ToList();
                sb.AppendLine("- Holdings: " + (fiefs.Count + villages.Count == 0 ? "none" : string.Join(", ", fiefs.Concat(villages.Count > 0 ? new[] { "villages: " + string.Join(", ", villages) } : new string[0]))) + ".");
                var members = clan.Heroes.Where(h => h != p && h.IsAlive).Select(h => $"{h.Name} ({(int)h.Age}{(h.IsChild ? ", a child" : "")}, {Where(h)})").ToList();
                if (members.Count > 0) sb.AppendLine("- Members of their clan: " + string.Join("; ", members.Take(20)) + ".");
            }
            sb.AppendLine($"- Gold: {p.Gold} denars.");
            try
            {
                var shops = p.OwnedWorkshops.Where(w => w != null).Select(w => $"{w.WorkshopType?.Name} in {w.Settlement?.Name}").ToList();
                sb.AppendLine("- Workshops: " + (shops.Count > 0 ? string.Join(", ", shops) : "none") + ".");
            }
            catch { }
            sb.AppendLine("- Whereabouts: " + (p.CurrentSettlement != null ? "at " + p.CurrentSettlement.Name : "on the roads") + (party?.Army != null ? $", in the army of {party.Army.LeaderParty?.LeaderHero?.Name}" : "") + ".");
            if (party != null)
            {
                try { sb.AppendLine("- Forces: " + ContextBuilder.DescribeTroops(party, true)); } catch { sb.AppendLine($"- Forces: {party.MemberRoster.TotalManCount} men."); }
                var companions = party.MemberRoster.GetTroopRoster().Where(e => e.Character != null && e.Character.IsHero && e.Character.HeroObject != p).Select(e => e.Character.Name.ToString()).ToList();
                if (companions.Count > 0) sb.AppendLine("- Companions travelling with them: " + string.Join(", ", companions) + ".");
                if (party.PrisonRoster.TotalManCount > 0)
                {
                    var held = party.PrisonRoster.GetTroopRoster().Where(e => e.Character != null).OrderByDescending(e => e.Character.IsHero).ThenByDescending(e => e.Number)
                        .Take(10).Select(e => e.Character.IsHero ? $"{e.Character.Name} (a lord)" : $"{e.Character.Name} x{e.Number}").ToList();
                    sb.AppendLine($"- Prisoners in their party ({party.PrisonRoster.TotalManCount}): {string.Join(", ", held)}.");
                }
                try
                {
                    var goods = party.ItemRoster.Where(e => e.EquipmentElement.Item != null && e.Amount > 0)
                        .OrderByDescending(e => (long)e.EquipmentElement.Item.Value * e.Amount).Take(20)
                        .Select(e => $"{e.EquipmentElement.Item.Name} x{e.Amount}").ToList();
                    if (goods.Count > 0) sb.AppendLine("- What they carry (the most valuable): " + string.Join(", ", goods) + ".");
                }
                catch { }
            }
            try
            {
                var traits = DefaultTraits.Personality.Select(t => (t, lvl: p.GetTraitLevel(t))).Where(x => x.lvl != 0).Select(x => $"{x.t.Name} {x.lvl:+0;-0}").ToList();
                sb.AppendLine("- Temper: " + (traits.Count > 0 ? string.Join(", ", traits) : "nothing marked yet") + ".");
            }
            catch { }
            try
            {
                var skills = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<SkillObject>().Select(s => (s, v: p.GetSkillValue(s))).Where(x => x.v > 0).OrderByDescending(x => x.v).ToList();
                sb.AppendLine("- Skills: " + (skills.Count > 0 ? string.Join(", ", skills.Select(x => $"{x.s.Name} {x.v}")) : "no training has settled in yet") + ".");
            }
            catch { }
            return sb.ToString();
        }

        // ---- the people close to them

        private static string Bond(int r) => r >= 50 ? "a close friend" : r >= 20 ? "a friend" : r <= -50 ? "a bitter enemy" : r <= -20 ? "an enemy" : "neither friend nor foe";

        private static string Circle()
        {
            var p = Hero.MainHero;
            if (p == null) return "";
            var sb = new StringBuilder();
            sb.AppendLine("=== THE PEOPLE CLOSE TO THIS WANDERER ===");
            var family = new List<string>();
            void Kin(Hero h, string what) { if (h != null) family.Add($"{what} {h.Name}{(h.IsAlive ? $" ({(int)h.Age}, {Where(h)})" : " (dead)")}"); }
            Kin(p.Spouse, "spouse");
            foreach (var x in p.ExSpouses.Where(x => x != null && x != p.Spouse)) Kin(x, "former spouse");
            Kin(p.Father, "father"); Kin(p.Mother, "mother");
            foreach (var x in p.Children) Kin(x, "child");
            foreach (var x in p.Siblings) Kin(x, "sibling");
            if (family.Count > 0) sb.AppendLine("- Family: " + string.Join("; ", family) + ".");
            var seen = new HashSet<Hero> { p };
            var lines = new List<string>();
            void Person(Hero h, string role)
            {
                if (h == null || !seen.Add(h)) return;
                int rel = 0;
                try { rel = (int)h.GetRelationWithPlayer(); } catch { }
                var line = new StringBuilder($"• {h.Name} (id {h.StringId}, {(int)h.Age}, {role}): {Bond(rel)} (relation {rel:+0;-0;0}); {Where(h)}");
                var mem = MemoryStore.Find(h.StringId);
                if (mem != null)
                {
                    if (mem.Romance > 0) line.Append($"; romantic interest in the wanderer {mem.Romance}/100");
                    string sum = (mem.Summary ?? "").Trim();
                    if (sum.Length > 0) line.Append("; what passed between them: " + (sum.Length > 260 ? sum.Substring(0, 260) + "…" : sum));
                }
                lines.Add(line.ToString());
            }
            foreach (var h in Clan.PlayerClan?.Companions ?? Enumerable.Empty<Hero>())
                if (h.IsAlive) Person(h, h.PartyBelongedTo == MobileParty.MainParty ? "companion of their clan, in their party now" : "companion of their clan");
            var others = Hero.AllAliveHeroes.Where(h => h != null && h != p && !h.IsChild && (h.IsLord || h.IsNotable || h.IsWanderer))
                .Select(h => { int r = 0; try { r = (int)h.GetRelationWithPlayer(); } catch { } return (h, r); }).ToList();
            foreach (var (h, r) in others.Where(x => x.r >= 20).OrderByDescending(x => x.r).Take(12)) Person(h, "friend");
            foreach (var (h, r) in others.Where(x => x.r <= -20).OrderBy(x => x.r).Take(12)) Person(h, "enemy");
            if (lines.Count > 0) sb.AppendLine(string.Join("\n", lines));
            return family.Count + lines.Count > 0 ? sb.ToString() : "";
        }

        // ---- what they did

        private static string PlayerEvents()
        {
            var items = NewsStore.All.Where(n => n != null && n.PlayerInvolved).OrderByDescending(n => n.Day).Take(MaxPlayerEvents).ToList();
            if (items.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine($"=== THE LAST {items.Count} THINGS THIS WANDERER HAD A HAND IN ===");
            foreach (var n in items) sb.AppendLine($"- {Days(n.Day)}: {n.Text}");
            return sb.ToString();
        }

        // ---- the world below

        private static string World()
        {
            var sb = new StringBuilder();
            try { sb.AppendLine(Facts.Today()); } catch { }
            sb.AppendLine("=== THE REALMS (the truth of this very day) ===");
            sb.AppendLine(WorldSim.DescribeKingdoms());
            sb.AppendLine();
            sb.AppendLine("=== WHAT REALLY HAPPENED LATELY ===");
            sb.AppendLine(WorldSim.DescribeRecentNews(14));
            var st = WorldSim.State;
            double now = CampaignTime.Now.ToDays;
            var events = st.Events.Where(e => now - e.Day <= S.Get.WorldEventLifespanDays).OrderByDescending(e => e.Day).Take(15).ToList();
            if (events.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== WHAT IS STIRRING IN THE WORLD (events, and what the rulers said of them) ===");
                foreach (var e in events)
                    sb.AppendLine($"- [{e.Type}, importance {e.Importance}, {Days(e.Day)}] {(string.IsNullOrWhiteSpace(e.SummaryEn) ? e.Title : e.SummaryEn)}{(e.Kingdoms.Count > 0 ? " (kingdoms: " + string.Join(", ", e.Kingdoms) + ")" : "")}");
            }
            var words = st.Statements.OrderByDescending(s => s.Day).Take(10).ToList();
            if (words.Count > 0)
            {
                sb.AppendLine("Rulers' statements:");
                foreach (var s in words)
                    sb.AppendLine($"  >>> {s.RulerName} of {s.KingdomId} ({Days(s.Day)}): {(string.IsNullOrWhiteSpace(s.SummaryEn) ? s.Text : s.SummaryEn)}{(s.Results != null && s.Results.Count > 0 ? " [came of it: " + string.Join("; ", s.Results) + "]" : "")}");
            }
            var lore = WorldLore.Text;
            if (!string.IsNullOrWhiteSpace(lore))
            {
                sb.AppendLine();
                sb.AppendLine("=== THE SHAPE OF THIS WORLD (as it was written; mortals live inside it, you watch it from the mountain) ===");
                sb.AppendLine(lore.Length > 4000 ? lore.Substring(0, 4000) + "…" : lore.Trim());
            }
            var secrets = st.Secrets.Where(s => s != null && !string.IsNullOrWhiteSpace(s.Text)).OrderByDescending(s => s.Day).Take(15).ToList();
            if (secrets.Count > 0)
            {
                sb.AppendLine();
                sb.AppendLine("=== HIDDEN TRUTHS (mortals may one day learn them; you have always known them) ===");
                foreach (var s in secrets) sb.AppendLine("- " + s.Text.Trim());
            }
            return sb.ToString();
        }
    }
}
