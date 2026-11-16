using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;

namespace AILegacy.World
{
    /// <summary>
    /// AI lords train their men too. A lord resting in a town whose party is mostly green troops pays the arena out of
    /// his own purse and his healthy soldiers gain experience, with the same price, experience and injury odds as the
    /// player's arena training (ArenaTraining in Extras.cs). Each party waits the same hours before training again.
    /// </summary>
    internal static class AiArenaTraining
    {
        private static readonly Random Rng = new Random();
        private const string KeyPrefix = "party:";      // shares WorldState.ArenaLastAt with the player's arenas (settlement ids)
        private const double MaxCost = 1000000;         // per session, whatever the settings say
        private const double MaxXpEach = 10000;         // per soldier
        private const long MaxXpStack = 2000000;        // per troop type in one session

        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableArenaTraining || !cfg.AiLordsTrainTroops || WorldSim.State == null) return;
                var last = WorldSim.State.ArenaLastAt;
                double now = CampaignTime.Now.ToHours;
                int parties = 0, men = 0;
                foreach (var p in MobileParty.AllLordParties.ToList())
                {
                    var h = p?.LeaderHero;
                    var st = p?.CurrentSettlement;
                    if (h == null || st == null || !st.IsTown || p.IsMainParty || h.Clan == Clan.PlayerClan || !p.IsActive) continue;
                    if (st.Town.HasTournament || st.IsUnderSiege || p.MapEvent != null || QuarantineRules.Closed(st)) continue;
                    string key = KeyPrefix + p.StringId;
                    if (last.TryGetValue(key, out double at) && now - at < Math.Max(24, cfg.ArenaTrainingCooldownHours)) continue;

                    var roster = p.MemberRoster;
                    var troops = roster.GetTroopRoster().Where(e => e.Character != null && !e.Character.IsHero && e.Number - e.WoundedNumber > 0).ToList();
                    int fit = troops.Sum(e => e.Number - e.WoundedNumber);
                    int green = troops.Where(e => e.Character.Tier <= 3).Sum(e => e.Number - e.WoundedNumber);
                    if (fit < 10 || green * 2 <= fit) continue;
                    float own = st.OwnerClan == h.Clan ? 0.6f : 1f;
                    // The percentage and the experience are settings and troop tiers come from other mods: worked out
                    // wide and clamped, so a strange value can neither hand out gold nor overflow
                    double price = troops.Sum(e => (double)(e.Number - e.WoundedNumber) * (8 + Math.Max(0, e.Character.Tier) * 4)) * own * cfg.ArenaTrainingCostPercent / 100.0;
                    if (double.IsNaN(price)) continue;
                    int cost = (int)Math.Ceiling(Math.Max(0.0, Math.Min(MaxCost, price)));
                    // He keeps enough to pay his men afterwards. One who does not head his clan may put the rest on the
                    // clan's account, as in AIInfluence: it is charged with the clan's next daily balance
                    int ownShare = Math.Max(0, Math.Min(cost, h.Gold - 2000));
                    int owed = cost - ownShare;
                    var head = h.Clan?.Leader;
                    bool clanPays = owed > 0 && head != null && head != h && head.IsAlive && head.Gold >= owed + 2000;
                    if (owed > 0 && !clanPays) continue;

                    if (ownShare > 0) GiveGoldAction.ApplyBetweenCharacters(h, null, ownShare, true);
                    if (owed > 0) Patches.ArenaExpensePatch.AddDebt(h.Clan, owed);
                    foreach (var e in troops)
                    {
                        int n = e.Number - e.WoundedNumber;
                        double each = cfg.ArenaTrainingXp * (1.5 - Math.Min(1.0, Math.Max(0, e.Character.Tier) / 6.0));
                        int xpEach = double.IsNaN(each) ? 0 : (int)Math.Max(0.0, Math.Min(MaxXpEach, each));
                        int xp = (int)Math.Min(MaxXpStack, (long)xpEach * n);
                        if (xp > 0) roster.AddXpToTroop(e.Character, xp);
                        int injured = 0;
                        for (int i = 0; i < n; i++) if (Rng.Next(100) < cfg.ArenaTrainingInjuryPercent) injured++;
                        if (injured > 0) roster.WoundTroop(e.Character, injured);
                        men += n;
                    }
                    last[key] = now;
                    parties++;
                    // "Trains troops" on the town's nameplate on the map
                    Patches.ArenaNotice.Show(st, h);
                }
                if (parties > 0) Log.Info($"[arena] {parties} lord(s) training troops in the arena ({men} soldiers)");
                // Parties that no longer exist
                if (last.Count > 300)
                    foreach (var k in last.Where(kv => kv.Key.StartsWith(KeyPrefix) && now - kv.Value > 24 * 30).Select(kv => kv.Key).ToList()) last.Remove(k);
            }
            catch (Exception ex) { Log.Error("Lords' troop training in the arena", ex); }
        }
    }
}
