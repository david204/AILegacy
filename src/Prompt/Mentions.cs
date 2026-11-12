using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.Prompt
{
    /// <summary>
    /// Contexto por menciones: si en los últimos mensajes se nombra un lugar, una casa, un reino, una persona o una
    /// mercancía, sus datos públicos entran en el prompt de ese turno. Así el personaje sabe de qué le hablan («¿quién
    /// tiene ahora Sargot?», «¿a cuánto está el grano?») sin cargar el mundo entero en cada petición.
    /// Solo datos que cualquiera podría saber; lo que sabe o calla el personaje lo decide él.
    /// </summary>
    internal static class Mentions
    {
        private const int LookBackTurns = 5;
        private const int MaxSettlements = 5, MaxClans = 4, MaxHeroes = 4, MaxItems = 6, MaxParties = 6;
        private const float NearbyRadius = 10f;

        private class Entry { public string Key; public object What; }

        // Índice de nombres del mundo (se rehace una vez por día de campaña: los héroes nacen, mueren y cambian)
        private static List<Entry> _index;
        private static int _indexDay = -1;
        private static int _indexSession = -1;

        public static string Fold(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var n = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(n.Length + 2);
            sb.Append(' ');
            bool space = true;
            foreach (char c in n)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
                else if (!space) { sb.Append(' '); space = true; }
            }
            if (!space) sb.Append(' ');
            return sb.ToString();
        }

        private static List<Entry> Index()
        {
            int day = (int)CampaignTime.Now.ToDays;
            if (_index != null && _indexDay == day && _indexSession == World.WorldSim.Session) return _index;
            var list = new List<Entry>();
            void Add(string name, object what)
            {
                string k = Fold(name);
                if (k.Trim().Length >= 4) list.Add(new Entry { Key = k, What = what });
            }
            try { foreach (var st in Settlement.All) if (st != null && (st.IsTown || st.IsCastle || st.IsVillage)) Add(st.Name?.ToString(), st); } catch { }
            try { foreach (var k in Kingdom.All) if (k != null && !k.IsEliminated) Add(k.Name?.ToString(), k); } catch { }
            try { foreach (var c in Clan.All) if (c != null && !c.IsEliminated && !c.IsBanditFaction && c.Leader != null) Add(c.Name?.ToString(), c); } catch { }
            try
            {
                foreach (var h in Hero.AllAliveHeroes)
                    if (h != null && h != Hero.MainHero && !h.IsChild && (h.IsLord || h.IsNotable || h.IsWanderer || h.IsPlayerCompanion)) Add(h.FirstName?.ToString() ?? h.Name?.ToString(), h);
            }
            catch { }
            try { foreach (var it in TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<ItemObject>()) if (it != null && it.IsTradeGood) Add(it.Name?.ToString(), it); } catch { }
            // Los nombres largos primero: «Clan dey Meroc» antes que «Meroc»
            _index = list.OrderByDescending(e => e.Key.Length).ToList();
            _indexDay = day; _indexSession = World.WorldSim.Session;
            return _index;
        }

        /// <summary>Lo nombrado en los últimos mensajes, con sus datos. Cadena vacía si no hay nada.</summary>
        public static string Describe(Speaker s)
        {
            try
            {
                if (!S.Get.EnableMentions || Campaign.Current == null) return "";
                var mem = s.Memory;
                var recent = mem.Turns.Where(t => !t.IsNote).Skip(Math.Max(0, mem.Turns.Count(t => !t.IsNote) - LookBackTurns)).Select(t => t.Text ?? "");
                string text = Fold(string.Join(" . ", recent));
                if (text.Length < 6) return "";

                var settlements = new List<Settlement>(); var clans = new List<Clan>(); var kingdoms = new List<Kingdom>();
                var heroes = new List<Hero>(); var items = new List<ItemObject>();
                foreach (var e in Index())
                {
                    if (text.IndexOf(e.Key, StringComparison.Ordinal) < 0) continue;
                    switch (e.What)
                    {
                        case Settlement st: if (settlements.Count < MaxSettlements && st != s.Settlement && !settlements.Contains(st)) settlements.Add(st); break;
                        case Kingdom k: if (kingdoms.Count < 3 && !kingdoms.Contains(k)) kingdoms.Add(k); break;
                        case Clan c: if (clans.Count < MaxClans && !clans.Contains(c) && !(s.IsHero && s.Hero.Clan == c)) clans.Add(c); break;
                        case Hero h: if (heroes.Count < MaxHeroes && !heroes.Contains(h) && !(s.IsHero && s.Hero == h)) heroes.Add(h); break;
                        case ItemObject it: if (items.Count < MaxItems && !items.Contains(it)) items.Add(it); break;
                    }
                }
                if (settlements.Count + clans.Count + kingdoms.Count + heroes.Count + items.Count == 0) return "";

                var from = Where(s);
                var sb = new StringBuilder();
                foreach (var st in settlements) sb.AppendLine("- " + DescribeSettlement(st, from));
                foreach (var k in kingdoms) sb.AppendLine("- " + DescribeKingdom(k));
                foreach (var c in clans) sb.AppendLine("- " + DescribeClan(c));
                foreach (var h in heroes) sb.AppendLine("- " + DescribeHero(h));
                foreach (var it in items) sb.AppendLine("- " + DescribeItem(it, s));
                return sb.ToString().TrimEnd();
            }
            catch (Exception ex) { Log.Error("Menciones", ex); return ""; }
        }

        private static Vec2 Where(Speaker s)
        {
            try
            {
                var st = s.Settlement;
                if (st != null) return st.GetPosition2D;
                if (s.IsHero && s.Hero.PartyBelongedTo != null) return s.Hero.PartyBelongedTo.GetPosition2D;
                if (s.Band != null) return s.Band.GetPosition2D;
            }
            catch { }
            return MobileParty.MainParty != null ? MobileParty.MainParty.GetPosition2D : Vec2.Zero;
        }

        private static string Far(Vec2 from, Vec2 to)
        {
            float d = from.Distance(to);
            if (d < 3f) return "right here";
            var v = to - from;
            string dir = Math.Abs(v.x) > Math.Abs(v.y) * 2 ? (v.x > 0 ? "east" : "west")
                       : Math.Abs(v.y) > Math.Abs(v.x) * 2 ? (v.y > 0 ? "north" : "south")
                       : (v.y > 0 ? "north" : "south") + "-" + (v.x > 0 ? "east" : "west");
            string how = d < 25f ? "close by, under a day's ride" : d < 70f ? "a couple of days' ride" : d < 150f ? "several days' ride" : "very far, across the continent";
            return $"{how} to the {dir}";
        }

        private static string DescribeSettlement(Settlement st, Vec2 from)
        {
            string kind = st.IsTown ? "town" : st.IsCastle ? "castle" : "village";
            var sb = new StringBuilder($"{st.Name}: {kind}");
            try
            {
                if (st.IsVillage && st.Village?.Bound != null) sb.Append($" bound to {st.Village.Bound.Name}");
                if (st.MapFaction != null) sb.Append($", of {st.MapFaction.Name}");
                if (st.OwnerClan != null) sb.Append($", held by clan {st.OwnerClan.Name}" + (st.OwnerClan.Leader != null ? $" ({st.OwnerClan.Leader.Name})" : ""));
                if (st.Town != null)
                {
                    sb.Append(st.Town.Prosperity < 2000 ? "; poor" : st.Town.Prosperity > 6000 ? "; rich" : "; of middling wealth");
                    if (st.Town.Governor != null) sb.Append($"; governed by {st.Town.Governor.Name}");
                }
                if (st.IsVillage && st.Village?.VillageType?.PrimaryProduction != null) sb.Append($"; lives off {st.Village.VillageType.PrimaryProduction.Name}");
                if (st.IsUnderSiege) sb.Append("; UNDER SIEGE now");
                if (st.IsVillage && st.Village != null && st.Village.VillageState != Village.VillageStates.Normal) sb.Append($"; {st.Village.VillageState.ToString().ToLowerInvariant()}");
                sb.Append($". It lies {Far(from, st.GetPosition2D)}.");
            }
            catch { }
            return sb.ToString();
        }

        private static string DescribeKingdom(Kingdom k)
        {
            var sb = new StringBuilder($"{k.Name}: a realm");
            try
            {
                if (k.Leader != null) sb.Append($" ruled by {k.Leader.Name}");
                var wars = Kingdom.All.Where(o => o != k && !o.IsEliminated && k.IsAtWarWith(o)).Select(o => o.Name.ToString()).ToList();
                sb.Append(wars.Count > 0 ? "; at war with " + string.Join(", ", wars) : "; at peace with the other realms");
                sb.Append($"; {k.Clans.Count(c => !c.IsEliminated)} houses, {k.Settlements.Count(x => x.IsTown)} towns and {k.Settlements.Count(x => x.IsCastle)} castles.");
            }
            catch { }
            return sb.ToString();
        }

        private static string DescribeClan(Clan c)
        {
            var sb = new StringBuilder($"Clan {c.Name}");
            try
            {
                sb.Append(c.Kingdom != null ? $": a house of {c.Kingdom.Name}" + (c.IsUnderMercenaryService ? " (mercenaries)" : "") : c.IsMinorFaction ? ": a minor faction" : ": a house with no realm");
                if (c.Leader != null) sb.Append($", led by {c.Leader.Name}");
                sb.Append(c.Tier >= 5 ? "; one of the great houses" : c.Tier >= 3 ? "; a house of standing" : "; a lesser house");
                var fiefs = c.Settlements.Where(x => x.IsTown || x.IsCastle).Select(x => x.Name.ToString()).Take(5).ToList();
                sb.Append(fiefs.Count > 0 ? "; holds " + string.Join(", ", fiefs) : "; holds no land");
                sb.Append('.');
            }
            catch { }
            return sb.ToString();
        }

        private static string DescribeHero(Hero h)
        {
            var sb = new StringBuilder($"{h.Name}");
            try
            {
                sb.Append($": {(h.IsFemale ? "woman" : "man")} of about {(int)h.Age}");
                if (h.IsLord && h.Clan != null) sb.Append($", of clan {h.Clan.Name}" + (h.Clan.Leader == h ? " (its head)" : ""));
                else if (h.IsNotable) sb.Append($", a notable of {h.HomeSettlement?.Name}");
                else if (h.IsPlayerCompanion) sb.Append(", one of their companions");
                else if (h.IsWanderer) sb.Append(", a wanderer");
                if (h.MapFaction is Kingdom k) sb.Append(k.Leader == h ? $", ruler of {k.Name}" : $", of {k.Name}");
                if (h.Spouse != null) sb.Append($"; married to {h.Spouse.Name}");
                if (h.IsPrisoner) sb.Append("; said to be a prisoner" + (h.PartyBelongedToAsPrisoner?.Name != null ? $" of {h.PartyBelongedToAsPrisoner.Name}" : ""));
                else
                {
                    var seen = h.CurrentSettlement ?? h.LastKnownClosestSettlement;
                    if (seen != null) sb.Append($"; last heard of {(h.CurrentSettlement != null ? "in" : "near")} {seen.Name}");
                }
                sb.Append('.');
            }
            catch { }
            return sb.ToString();
        }

        private static string DescribeItem(ItemObject it, Speaker s)
        {
            var sb = new StringBuilder($"{it.Name}: a trade good");
            try
            {
                var st = s.Settlement ?? Settlement.CurrentSettlement;
                int price = st?.Town != null ? st.Town.GetItemPrice(new EquipmentElement(it)) : st?.Village != null ? st.Village.GetItemPrice(it) : it.Value;
                sb.Append($", worth about {price} denars {(st != null ? "here" : "in most markets")}");
                int theirs = MobileParty.MainParty?.ItemRoster?.GetItemNumber(it) ?? 0;
                if (theirs > 0) sb.Append($"; they carry {theirs}");
                ItemRoster own = s.IsHero ? World.OrderSystem.PartyLedBy(s.Hero)?.ItemRoster : s.Band?.ItemRoster;
                int mine = own?.GetItemNumber(it) ?? 0;
                if (mine > 0) sb.Append($"; you carry {mine}");
                sb.Append('.');
            }
            catch { }
            return sb.ToString();
        }

        /// <summary>Partidas que hay cerca ahora mismo (solo si el personaje está en el mapa, a la vista del jugador).</summary>
        public static string NearbyParties(Speaker s)
        {
            try
            {
                var main = MobileParty.MainParty;
                if (!S.Get.EnableMentions || main == null || TaleWorlds.MountAndBlade.Mission.Current != null && Settlement.CurrentSettlement != null) return "";
                if (Settlement.CurrentSettlement != null) return "";
                MobileParty own = s.IsHero ? s.Hero.PartyBelongedTo : s.Band;
                if (own == null) return "";
                var center = main.GetPosition2D;
                var list = new List<(MobileParty p, float d)>();
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsActive || p == main || p == own || p.CurrentSettlement != null || p.MemberRoster.TotalManCount <= 0) continue;
                    float d = p.GetPosition2D.Distance(center);
                    if (d <= NearbyRadius) list.Add((p, d));
                }
                if (list.Count == 0) return "";
                var sb = new StringBuilder();
                var myFaction = s.Faction;
                foreach (var (p, d) in list.OrderBy(x => x.d).Take(MaxParties))
                {
                    string side = myFaction != null && p.MapFaction != null && p.MapFaction != myFaction && p.MapFaction.IsAtWarWith(myFaction) ? "your enemy"
                                : p.MapFaction != null && p.MapFaction == myFaction ? "of your side" : "neutral to you";
                    sb.AppendLine($"- {p.Name} ({p.MapFaction?.Name}), about {Round(p.MemberRoster.TotalManCount)} strong, {side}, {(d < 3f ? "right beside you" : "within sight")}");
                }
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        private static int Round(int n) => n < 20 ? n : n < 100 ? n / 5 * 5 : n / 10 * 10;
    }
}
