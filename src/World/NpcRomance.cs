using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Library;

namespace AILegacy.World
{
    public class Courtship
    {
        [JsonProperty("a")] public string A;              // the one who began it
        [JsonProperty("b")] public string B;
        [JsonProperty("level")] public int Level;         // 0..100
        [JsonProperty("since")] public double Since;
        [JsonProperty("last")] public double LastTogether;
        [JsonProperty("known")] public bool Known;        // people have started to talk about it
    }

    /// <summary>
    /// Courtships between two characters who are not the player. They begin when two unmarried nobles spend time in the
    /// same hall or the same army and suit each other, grow while they stay near, cool when they are apart, and end in a
    /// wedding if they last. No AI requests: the two of them and the people around them learn of it through their own
    /// context and the news, and talk about it from there.
    /// </summary>
    internal static class NpcRomance
    {
        private static readonly Random Rng = new Random();
        private static List<Courtship> All => WorldSim.State.Courtships;
        private static Hero P => Hero.MainHero;

        public static Courtship Of(Hero h)
        {
            try { return h == null || WorldSim.State == null ? null : All.FirstOrDefault(c => c.A == h.StringId || c.B == h.StringId); }
            catch { return null; }
        }

        private static Hero Other(Courtship c, Hero h) => Hero.Find(c.A == h.StringId ? c.B : c.A);

        private static bool Free(Hero h) =>
            h != null && h != P && h.IsAlive && h.IsLord && !h.IsChild && h.Age >= 18 && h.Age <= 55 && h.Spouse == null && !h.IsPrisoner && h.Clan != null && h.Clan != Clan.PlayerClan;

        private static bool Kin(Hero a, Hero b) =>
            a.Father == b || a.Mother == b || b.Father == a || b.Mother == a || a.Siblings.Contains(b)
            || (a.Father != null && (a.Father == b.Father)) || (a.Mother != null && (a.Mother == b.Mother))
            || a.Clan == b.Clan;

        private static bool Suits(Hero a, Hero b)
        {
            if (a == b || !Free(a) || !Free(b) || Kin(a, b)) return false;
            if (a.IsFemale == b.IsFemale && !S.Get.RomanceSameSex) return false;
            if (Math.Abs(a.Age - b.Age) > 18) return false;
            // two heads of different houses can't marry without one house losing its head
            if (a.Clan.Leader == a && b.Clan.Leader == b) return false;
            if (a.MapFaction != null && b.MapFaction != null && a.MapFaction != b.MapFaction && a.MapFaction.IsAtWarWith(b.MapFaction)) return false;
            // someone the player is courting is left alone: that story is the player's
            var ma = MemoryStore.Find(a.StringId); var mb = MemoryStore.Find(b.StringId);
            if ((ma != null && ma.Romance >= 20) || (mb != null && mb.Romance >= 20)) return false;
            return true;
        }

        /// <summary>Same hall or same army: the only places two nobles get to know each other.</summary>
        private static bool Together(Hero a, Hero b)
        {
            if (a.CurrentSettlement != null && a.CurrentSettlement == b.CurrentSettlement) return true;
            var pa = a.PartyBelongedTo; var pb = b.PartyBelongedTo;
            return pa != null && pb != null && (pa == pb || (pa.Army != null && pa.Army == pb.Army));
        }

        private static int Temper(Hero h)
        {
            // warm and open characters fall faster, calculating ones slower
            int t = 0;
            try { t += h.GetTraitLevel(DefaultTraits.Mercy) + h.GetTraitLevel(DefaultTraits.Generosity) - h.GetTraitLevel(DefaultTraits.Calculating); } catch { }
            return t;
        }

        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableNpcRomance || WorldSim.State == null || Campaign.Current == null) return;
                double now = CampaignTime.Now.ToDays;

                // ---- the ones under way
                foreach (var c in All.ToList())
                {
                    var a = Hero.Find(c.A); var b = Hero.Find(c.B);
                    if (a == null || b == null || !a.IsAlive || !b.IsAlive || a.Spouse != null || b.Spouse != null
                        || (a.MapFaction != null && b.MapFaction != null && a.MapFaction != b.MapFaction && a.MapFaction.IsAtWarWith(b.MapFaction)))
                    {
                        if (a != null && b != null && a.IsAlive && b.IsAlive && a.Spouse != b)
                        {
                            Remember(a, $"What there was between you and {b.Name} is over.");
                            Remember(b, $"What there was between you and {a.Name} is over.");
                        }
                        All.Remove(c);
                        continue;
                    }
                    if (Together(a, b))
                    {
                        c.LastTogether = now;
                        c.Level = Math.Min(100, c.Level + Math.Max(1, 3 + Rng.Next(0, 4) + (Temper(a) + Temper(b)) / 2));
                    }
                    else if (now - c.LastTogether > 10) c.Level = Math.Max(0, c.Level - 2);

                    if (c.Level <= 0 && now - c.Since > 5)
                    {
                        Remember(a, $"Your courtship of {b.Name} came to nothing: you drifted apart.");
                        Remember(b, $"The courtship between you and {a.Name} came to nothing: you drifted apart.");
                        All.Remove(c);
                        continue;
                    }
                    // People start to talk once it is plain to see
                    if (!c.Known && c.Level >= 40)
                    {
                        c.Known = true;
                        NewsStore.Add(new NewsItem
                        {
                            Type = "rumor", Importance = 2, Text = $"It is said that {a.Name} of {a.Clan?.Name} is courting {b.Name} of {b.Clan?.Name}.",
                            TitleEs = L.F("AIL_e7b1c50b", $"{a.Name} corteja a {b.Name}"),
                            TextEs = L.F("AIL_e7b1c50c", $"Se dice que {a.Name}, del clan {a.Clan?.Name}, corteja a {b.Name}, del clan {b.Clan?.Name}."),
                            Lang = L.Code,
                            Kingdoms = new[] { a.MapFaction?.StringId, b.MapFaction?.StringId }.Where(x => x != null).Distinct().ToList(),
                            Heroes = new List<string> { a.StringId, b.StringId }, SettlementId = a.CurrentSettlement?.StringId,
                        });
                    }
                    if (c.Level >= cfg.NpcRomanceForMarriage && now - c.Since >= cfg.NpcRomanceMinDays && Together(a, b) && CanWedNow(a) && CanWedNow(b)
                        && !(a.Clan.Leader == a && b.Clan.Leader == b)) Wed(c, a, b);
                }

                // ---- a new one, now and then
                if (All.Count >= cfg.NpcRomanceMax || Rng.Next(100) >= cfg.NpcRomanceChancePercent) return;
                var free = Hero.AllAliveHeroes.Where(h => Free(h) && Of(h) == null).ToList();
                if (free.Count < 2) return;
                foreach (var a in free.OrderBy(_ => Rng.Next()).Take(40))
                {
                    var b = free.Where(x => x != a && Together(a, x) && Suits(a, x))
                                .OrderByDescending(x => a.GetRelation(x) + Temper(x) * 5 - Math.Abs(a.Age - x.Age)).FirstOrDefault();
                    if (b == null || a.GetRelation(b) < -10) continue;
                    All.Add(new Courtship { A = a.StringId, B = b.StringId, Level = 10, Since = now, LastTogether = now });
                    Remember(a, $"You have begun to court {b.Name} of {b.Clan?.Name}: you seek their company and they do not turn you away.");
                    Remember(b, $"{a.Name} of {a.Clan?.Name} has begun to court you, and you have not turned them away.");
                    Log.Info($"[cortejo] {a.Name} empieza a cortejar a {b.Name}");
                    return;
                }
            }
            catch (Exception ex) { Log.Error("Cortejos entre personajes", ex); }
        }

        /// <summary>Not in the middle of a battle or a siege, and not in chains.</summary>
        private static bool CanWedNow(Hero h) => !h.IsPrisoner && (h.PartyBelongedTo == null || (h.PartyBelongedTo.MapEvent == null && h.PartyBelongedTo.SiegeEvent == null));

        private static void Wed(Courtship c, Hero a, Hero b)
        {
            try
            {
                All.Remove(c);
                // The one who is not head of a clan moves; with two heads, the game decides and may refuse
                MarriageAction.Apply(a, b, true);
                if (a.Spouse != b) { Log.Warn($"[cortejo] el juego no ha casado a {a.Name} y {b.Name}"); return; }
                Remember(a, $"You married {b.Name}, after courting them.");
                Remember(b, $"You married {a.Name}, who courted you.");
                Log.Info($"[cortejo] boda de {a.Name} y {b.Name}");
            }
            catch (Exception ex) { Log.Error("Boda entre personajes", ex); }
        }

        private static void Remember(Hero h, string note)
        {
            try
            {
                var mem = MemoryStore.Find(h.StringId);
                if (mem != null) ChatSession.AddNote(mem, note, S.Get.MaxNotesPerHero, h.Name.ToString());
            }
            catch { }
        }

        /// <summary>Line for the character's own prompt, or empty.</summary>
        public static string DescribeFor(Hero h)
        {
            try
            {
                if (!S.Get.EnableNpcRomance) return "";
                var c = Of(h);
                if (c == null) return "";
                var o = Other(c, h);
                if (o == null) return "";
                string how = c.Level >= 70 ? "you are in love and mean to marry" : c.Level >= 40 ? "it has grown serious and people have noticed" : "it is still new and unsure";
                return c.A == h.StringId ? $"You are courting {o.Name} of {o.Clan?.Name}: {how}." : $"{o.Name} of {o.Clan?.Name} is courting you and you welcome it: {how}.";
            }
            catch { return ""; }
        }
    }
}
