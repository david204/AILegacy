using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;

namespace AILegacy.World
{
    /// <summary>Men lost on one day by each side of a war.</summary>
    public class TollDay
    {
        [JsonProperty("d")] public int Day;
        [JsonProperty("a")] public int A;
        [JsonProperty("b")] public int B;
    }

    /// <summary>What an open war has cost each side in men. The sides are the two ids of its key ("a|b"), in that order.</summary>
    public class WarToll
    {
        /// <summary>Day the count began: the start of the war, or the day we first saw it.</summary>
        [JsonProperty("since")] public double SinceDay;
        [JsonProperty("lost")] public int[] Lost = new int[2];
        [JsonProperty("days")] public List<TollDay> Days = new List<TollDay>();
    }

    public class DayValue
    {
        [JsonProperty("d")] public int Day;
        [JsonProperty("v")] public float Value;
    }

    /// <summary>A lord held by a kingdom at war with his own, and the weariness that cost his realm.</summary>
    public class CaptiveToll
    {
        [JsonProperty("kingdom")] public string KingdomId;
        [JsonProperty("amount")] public float Amount;
    }

    /// <summary>
    /// What the wars between kingdoms cost: men lost in each war, lords held by the enemy, and how the strength of
    /// each realm moves. It only counts. Which wars exist is always read from the game, never from here.
    /// </summary>
    internal static class WarTolls
    {
        private static WorldState St => WorldSim.State;

        public const int TrendDays = 10;
        private const int KeepDays = TrendDays + 1, MaxCaptives = 300;
        // A defeat counts in full from this many men lost, a smaller one in proportion
        private const int FullDefeatLosses = 250;
        private const float CaravanLostFatigue = 0.5f;

        // The last battles between kingdoms at war, with the lords who were in them. Not saved. A lord who dies of
        // one of them with no killer named is still that enemy's doing, but only if he fought in it.
        private class WarBattle
        {
            public double Hour;
            public string A, B;                                              // the kingdoms of the two sides
            public readonly HashSet<string> OfA = new HashSet<string>();     // heroes on each side
            public readonly HashSet<string> OfB = new HashSet<string>();
        }
        private static readonly List<WarBattle> RecentBattles = new List<WarBattle>();
        private const int MaxRecentBattles = 24, MaxHeroesPerSide = 60;
        private const double BattleDeathHours = 2;

        public static void ResetSession() { RecentBattles.Clear(); }

        private static void AddHeroes(MapEventSide side, HashSet<string> into)
        {
            try
            {
                foreach (var p in side.Parties)
                {
                    var party = p?.Party;
                    if (party == null) continue;
                    if (party.LeaderHero != null && into.Count < MaxHeroesPerSide) into.Add(party.LeaderHero.StringId);
                    var roster = party.MemberRoster;
                    if (roster == null) continue;
                    for (int i = 0; i < roster.Count && into.Count < MaxHeroesPerSide; i++)
                    {
                        var c = roster.GetCharacterAtIndex(i);
                        if (c != null && c.IsHero && c.HeroObject != null) into.Add(c.HeroObject.StringId);
                    }
                }
            }
            catch { }
        }

        /// <summary>The battle the victim is in right now, read from the game: the kingdom of the side he is not on.</summary>
        private static Kingdom EnemyInCurrentBattle(Hero victim)
        {
            try
            {
                var mine = victim.PartyBelongedTo?.Party?.MapEventSide;
                // defending the walls from inside, in no party of his own
                if (mine == null && victim.PartyBelongedTo == null) mine = victim.CurrentSettlement?.Party?.MapEventSide;
                return mine == null ? null : SideKingdom(mine.OtherSide, out _);
            }
            catch { return null; }
        }

        private static readonly Tpl TWarLosses = new Tpl("kingdom_statement/war_losses.txt",
            " Men lost in battle in this war: you {mine}, they {theirs}; in the last {days} days: you {mine_recent}, they {theirs_recent}.{partial}");
        private static readonly Tpl TWarLossesPartial = new Tpl("kingdom_statement/war_losses_partial.txt",
            " (The count only covers the last {counted} day(s) of this war.)");
        private static readonly Tpl TWarStrength = new Tpl("kingdom_statement/war_strength_trend.txt",
            " Strength {days} day(s) ago: yours {mine_then}, theirs {theirs_then}; change since then: yours {mine_change}%, theirs {theirs_change}%.");

        public static void Touch() { var _ = TWarLosses.Key + TWarLossesPartial.Key + TWarStrength.Key; }

        private static bool Usable(Kingdom k) => k != null && !k.IsEliminated;

        /// <summary>
        /// The kingdom a side of a battle fights for, from its leading party. null for bandits, villagers and anyone
        /// under no crown. A caravan is told apart: its guards are not the realm's army.
        /// </summary>
        private static Kingdom SideKingdom(MapEventSide side, out bool caravan)
        {
            caravan = false;
            var party = side?.LeaderParty;
            if (party == null) return null;
            var mobile = party.MobileParty;
            if (mobile != null)
            {
                if (mobile.IsVillager) return null;
                caravan = mobile.IsCaravan;
            }
            var k = party.MapFaction as Kingdom;
            return Usable(k) ? k : null;
        }

        // ============================================================ battles

        /// <summary>
        /// A battle is over. It wears a kingdom down only when the leaders of the two sides fight for two kingdoms
        /// at war with each other: each pays for the men it lost, and the loser a little more for the defeat.
        /// </summary>
        public static void OnBattleEnded(MapEvent e)
        {
            if (e == null || St == null || Campaign.Current == null) return;
            var att = e.AttackerSide; var def = e.DefenderSide;
            Kingdom ka = SideKingdom(att, out bool caravanA), kd = SideKingdom(def, out bool caravanD);
            if (ka == null || kd == null || ka == kd || !ka.IsAtWarWith(kd)) return;
            bool attLost = e.HasWinner && e.WinningSide == BattleSideEnum.Defender, defLost = e.HasWinner && e.WinningSide == BattleSideEnum.Attacker;
            if (caravanA || caravanD)
            {
                // a caravan taken by the enemy is a loss to its realm; nothing else about such a fight counts
                if (caravanA && attLost) WorldSim.AddFatigue(ka, CaravanLostFatigue);
                if (caravanD && defLost) WorldSim.AddFatigue(kd, CaravanLostFatigue);
                return;
            }
            int lostA = Math.Max(0, att.TroopCasualties), lostD = Math.Max(0, def.TroopCasualties);
            var cfg = S.Get;
            float perMan = cfg.FatiguePerHundredCasualtiesX10 / 1000f, defeat = cfg.FatiguePerBattleLostX10 / 10f;
            WorldSim.AddFatigue(ka, lostA * perMan + (attLost ? defeat * Math.Min(1f, lostA / (float)FullDefeatLosses) : 0f));
            WorldSim.AddFatigue(kd, lostD * perMan + (defLost ? defeat * Math.Min(1f, lostD / (float)FullDefeatLosses) : 0f));
            Record(ka, kd, lostA, lostD);
            double hour = CampaignTime.Now.ToHours;
            var fought = new WarBattle { Hour = hour, A = ka.StringId, B = kd.StringId };
            AddHeroes(att, fought.OfA);
            AddHeroes(def, fought.OfB);
            RecentBattles.RemoveAll(x => x.Hour > hour || hour - x.Hour > BattleDeathHours);      // x.Hour > hour: an older save was loaded
            if (fought.OfA.Count + fought.OfB.Count > 0) RecentBattles.Add(fought);
            while (RecentBattles.Count > MaxRecentBattles) RecentBattles.RemoveAt(0);
        }

        private static int Side(Kingdom k, Kingdom other) => string.CompareOrdinal(k.StringId, other.StringId) < 0 ? 0 : 1;

        /// <summary>The count of the war between these two, or null. A count left from an earlier war between them is started again.</summary>
        private static WarToll Toll(Kingdom a, Kingdom b, bool create)
        {
            if (St.WarTolls == null) St.WarTolls = new Dictionary<string, WarToll>();
            string key = FactCheck.Pair(a.StringId, b.StringId);
            double now = CampaignTime.Now.ToDays;
            St.WarTolls.TryGetValue(key, out var t);
            if (t != null)
            {
                int war = Diplomacy.WarDays(a, b);
                if (t.SinceDay > now || (war >= 0 && now - t.SinceDay > war + 1.5)) t = null;      // SinceDay > now: an older save was loaded
            }
            if (t == null)
            {
                if (!create) { St.WarTolls.Remove(key); return null; }
                St.WarTolls[key] = t = new WarToll { SinceDay = now };
            }
            if (t.Lost == null || t.Lost.Length != 2) t.Lost = new int[2];
            if (t.Days == null) t.Days = new List<TollDay>();
            return t;
        }

        private static void Record(Kingdom a, Kingdom b, int lostA, int lostB)
        {
            if (lostA <= 0 && lostB <= 0) return;
            var t = Toll(a, b, true);
            int ia = Side(a, b), day = (int)CampaignTime.Now.ToDays;
            t.Days.RemoveAll(d => d == null || d.Day > day || d.Day < day - KeepDays);
            var today = t.Days.Count > 0 ? t.Days[t.Days.Count - 1] : null;
            if (today == null || today.Day != day) t.Days.Add(today = new TollDay { Day = day });
            int first = ia == 0 ? lostA : lostB, second = ia == 0 ? lostB : lostA;
            today.A += first; today.B += second;
            t.Lost[0] += first; t.Lost[1] += second;
        }

        private static int Recent(WarToll t, int side, int day) =>
            t.Days.Where(d => d != null && d.Day <= day && d.Day > day - TrendDays).Sum(d => side == 0 ? d.A : d.B);

        // ============================================================ lords killed and held

        /// <summary>
        /// The kingdom at war with the victim's own that this death can be laid on, or null: killed in battle
        /// against it, or executed or cut down by one of its people. Old age, childbirth and the like are nobody's.
        /// </summary>
        public static Kingdom KilledByEnemy(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, Kingdom own)
        {
            if (!Usable(own) || victim == null) return null;
            bool battle = detail == KillCharacterAction.KillCharacterActionDetail.DiedInBattle || detail == KillCharacterAction.KillCharacterActionDetail.WoundedInBattle;
            bool violent = battle || detail == KillCharacterAction.KillCharacterActionDetail.Executed
                           || detail == KillCharacterAction.KillCharacterActionDetail.ExecutionAfterMapEvent || detail == KillCharacterAction.KillCharacterActionDetail.Murdered;
            if (!violent) return null;
            var enemy = killer?.MapFaction as Kingdom;
            // The game names no killer when he falls in a fought battle, or when the other side had no lord at its
            // head (a garrison). Then it is the battle he is in that says who did it; or, when he dies of it just
            // after it ended, a recent battle he took part in. A battle his kingdom fought without him says nothing.
            if (enemy == null && battle)
            {
                enemy = EnemyInCurrentBattle(victim);
                if (enemy == null)
                {
                    double now = CampaignTime.Now.ToHours;
                    string id = victim.StringId;
                    for (int i = RecentBattles.Count - 1; i >= 0 && enemy == null; i--)
                    {
                        var x = RecentBattles[i];
                        if (now - x.Hour < 0 || now - x.Hour > BattleDeathHours) continue;
                        string other = x.OfA.Contains(id) ? x.B : x.OfB.Contains(id) ? x.A : null;
                        if (other != null) enemy = Campaign.Current.Kingdoms.FirstOrDefault(k => k != null && k.StringId == other);
                    }
                }
            }
            return Usable(enemy) && enemy != own && own.IsAtWarWith(enemy) ? enemy : null;
        }

        /// <summary>A lord taken by a kingdom at war with his own wears his realm down until he goes free.</summary>
        public static void OnPrisonerTaken(PartyBase captor, Hero prisoner)
        {
            if (prisoner == null || !prisoner.IsLord || St == null) return;
            if (St.CaptiveTolls == null) St.CaptiveTolls = new Dictionary<string, CaptiveToll>();
            // moved from one enemy party or dungeon to another: already counted
            if (St.CaptiveTolls.ContainsKey(prisoner.StringId)) return;
            var own = Facts.Subject(prisoner.Clan);
            var holder = captor?.MapFaction as Kingdom;
            if (!Usable(own) || !Usable(holder) || own == holder || !own.IsAtWarWith(holder)) return;
            float amount = S.Get.FatiguePerLordKilled / 3f;
            if (amount <= 0f || St.CaptiveTolls.Count >= MaxCaptives) return;
            WorldSim.AddFatigue(own, amount);
            St.CaptiveTolls[prisoner.StringId] = new CaptiveToll { KingdomId = own.StringId, Amount = amount };
        }

        /// <summary>Free again, however it came about: what his captivity cost his realm is given back.</summary>
        public static void OnPrisonerReleased(Hero prisoner)
        {
            if (prisoner == null || St?.CaptiveTolls == null) return;
            if (!St.CaptiveTolls.TryGetValue(prisoner.StringId, out var c)) return;
            St.CaptiveTolls.Remove(prisoner.StringId);
            // one who died in his cell is not given back: his death is counted on its own
            if (c == null || !prisoner.IsAlive) return;
            var own = Campaign.Current.Kingdoms.FirstOrDefault(k => k != null && k.StringId == c.KingdomId);
            if (Usable(own)) WorldSim.AddFatigue(own, -Math.Abs(c.Amount));
        }

        // ============================================================ daily upkeep

        /// <summary>A kingdom is destroyed: its wars, its strength log and the captives counted against it go, whatever the kingdom list says right now.</summary>
        public static void OnKingdomGone(string id)
        {
            if (St == null || string.IsNullOrEmpty(id)) return;
            if (St.WarTolls != null)
                foreach (var key in St.WarTolls.Keys.Where(x => { var ids = (x ?? "").Split('|'); return ids.Length != 2 || ids[0] == id || ids[1] == id; }).ToList()) St.WarTolls.Remove(key);
            St.StrengthLog?.Remove(id);
            if (St.CaptiveTolls != null)
                foreach (var h in St.CaptiveTolls.Where(x => x.Value == null || x.Value.KingdomId == id).Select(x => x.Key).ToList()) St.CaptiveTolls.Remove(h);
            RecentBattles.RemoveAll(x => x.A == id || x.B == id);
        }

        public static void OnDailyTick(double now)
        {
            if (St == null || Campaign.Current == null) return;
            if (St.WarTolls == null) St.WarTolls = new Dictionary<string, WarToll>();
            if (St.StrengthLog == null) St.StrengthLog = new Dictionary<string, List<DayValue>>();
            if (St.CaptiveTolls == null) St.CaptiveTolls = new Dictionary<string, CaptiveToll>();
            int day = (int)now;
            // Entries that are broken in themselves go before anything else, so they are cleaned even on a day the
            // kingdom list comes back empty. What depends on which kingdoms exist needs the list, and waits for it.
            foreach (var key in St.WarTolls.Where(x => x.Value == null || (x.Key ?? "").Split('|').Length != 2).Select(x => x.Key).ToList()) St.WarTolls.Remove(key);
            foreach (var id in St.StrengthLog.Where(x => x.Value == null).Select(x => x.Key).ToList()) St.StrengthLog.Remove(id);
            foreach (var id in St.CaptiveTolls.Where(x => x.Value == null || x.Value.KingdomId == null).Select(x => x.Key).ToList()) St.CaptiveTolls.Remove(id);
            var all = Campaign.Current.Kingdoms.Where(Usable).ToList();
            if (all.Count == 0) return;
            var byId = new Dictionary<string, Kingdom>();
            foreach (var k in all) if (k.StringId != null) byId[k.StringId] = k;

            // every open war has its count from the day it is first seen, so a ruler is told how much of it is covered
            var ruled = WorldSim.Kingdoms.ToList();
            for (int i = 0; i < ruled.Count; i++)
                for (int j = i + 1; j < ruled.Count; j++)
                    if (ruled[i].IsAtWarWith(ruled[j])) Toll(ruled[i], ruled[j], true);
            // wars that are over, and kingdoms that are gone
            foreach (var key in St.WarTolls.Keys.ToList())
            {
                var ids = (key ?? "").Split('|');
                if (ids.Length != 2 || !byId.TryGetValue(ids[0], out var a) || !byId.TryGetValue(ids[1], out var b) || !a.IsAtWarWith(b)) { St.WarTolls.Remove(key); continue; }
                var t = St.WarTolls[key];
                if (t == null) { St.WarTolls.Remove(key); continue; }
                t.Days?.RemoveAll(d => d == null || d.Day > day || d.Day < day - KeepDays);
            }

            foreach (var k in ruled)
            {
                if (!St.StrengthLog.TryGetValue(k.StringId, out var log) || log == null) St.StrengthLog[k.StringId] = log = new List<DayValue>();
                log.RemoveAll(v => v == null || v.Day >= day || v.Day < day - KeepDays);
                log.Add(new DayValue { Day = day, Value = k.CurrentTotalStrength });
            }
            foreach (var id in St.StrengthLog.Keys.Where(x => !byId.ContainsKey(x)).ToList()) St.StrengthLog.Remove(id);

            // captives whose release we never heard of (an event missed, a save from another branch)
            foreach (var id in St.CaptiveTolls.Keys.ToList())
            {
                var c = St.CaptiveTolls[id];
                Hero h = null;
                try { h = Hero.Find(id); } catch { }
                if (c == null || h == null || !h.IsAlive || c.KingdomId == null || !byId.ContainsKey(c.KingdomId)) { St.CaptiveTolls.Remove(id); continue; }
                if (!h.IsPrisoner) OnPrisonerReleased(h);
            }
        }

        // ============================================================ for the ruler's prompt

        /// <summary>How the war between these two is going for <paramref name="k"/>: men lost by each side and how the strength of both has moved.</summary>
        public static string Trend(Kingdom k, Kingdom o)
        {
            var sb = new StringBuilder();
            try
            {
                if (St == null || k == null || o == null) return "";
                double now = CampaignTime.Now.ToDays;
                int day = (int)now;
                var t = Toll(k, o, false);
                if (t != null)
                {
                    int mine = Side(k, o), theirs = 1 - mine;
                    int counted = Math.Max(0, (int)(now - t.SinceDay)), war = Diplomacy.WarDays(k, o);
                    sb.Append(TWarLosses.Format("mine", t.Lost[mine], "theirs", t.Lost[theirs], "days", TrendDays,
                        "mine_recent", Recent(t, mine, day), "theirs_recent", Recent(t, theirs, day),
                        "partial", war < 0 || war - counted > 2 ? TWarLossesPartial.Format("counted", counted) : ""));
                }
                if (St.StrengthLog != null && St.StrengthLog.TryGetValue(k.StringId, out var a) && St.StrengthLog.TryGetValue(o.StringId, out var b) && a != null && b != null)
                {
                    // the oldest day within the window that both were measured on
                    DayValue va = null, vb = null;
                    foreach (var x in a.Where(v => v != null && v.Day < day && v.Day >= day - TrendDays).OrderBy(v => v.Day))
                    {
                        vb = b.FirstOrDefault(v => v != null && v.Day == x.Day);
                        if (vb != null) { va = x; break; }
                    }
                    if (va != null && va.Value > 0f && vb.Value > 0f)
                        sb.Append(TWarStrength.Format("days", day - va.Day, "mine_then", (int)va.Value, "theirs_then", (int)vb.Value,
                            "mine_change", Change(va.Value, k.CurrentTotalStrength), "theirs_change", Change(vb.Value, o.CurrentTotalStrength)));
                }
            }
            catch (Exception ex) { Log.Error("[diplomacia] tendencia de una guerra", ex); }
            return sb.ToString();
        }

        private static string Change(float then, float now) =>
            ((int)Math.Round((now - then) / then * 100f)).ToString("+0;-0;0", System.Globalization.CultureInfo.InvariantCulture);
    }
}
