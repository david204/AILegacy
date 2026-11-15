using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.World
{
    /// <summary>
    /// Clans with no fief (minor factions, dispossessed nobles, and the clans Banished.KeepAlive saves from being
    /// disbanded) have little to do on the map: the game's patrol behaviour only scores settlements the party's
    /// faction owns. Their idle parties get a patrol order of their own around a settlement of their kingdom or
    /// employer, or the nearest one that is not hostile. It is an ordinary "patrol" of OrderSystem flagged Auto, so
    /// nothing else moves them. A party under an order makes no decisions of its own, so patrols are short and a day
    /// of rest follows each one, and hungry or depleted parties are left to the game.
    /// </summary>
    internal static class LandlessPatrols
    {
        // Hero id -> campaign day until which they are left alone. Not saved: after loading everyone may start at once
        private static readonly Dictionary<string, double> RestUntil = new Dictionary<string, double>();
        private const double RestDays = 1.0;

        public static void OnDailyTick()
        {
            try
            {
                if (!S.Get.LandlessClansPatrol) { DropAll(); return; }
                double now = CampaignTime.Now.ToDays;
                Review(now);
                int given = 0;
                foreach (var clan in Clan.All.ToList())
                {
                    if (!IsLandless(clan)) continue;
                    foreach (var wp in clan.WarPartyComponents.ToList())
                    {
                        var p = wp?.MobileParty;
                        var h = p?.LeaderHero;
                        if (h == null || !Fit(p, h, now)) continue;
                        var st = PickSettlement(clan, p);
                        if (st == null) continue;
                        if (OrderSystem.Give(h, "patrol", st.StringId, st.Name.ToString(), Math.Max(1, S.Get.LandlessPatrolDays), auto: true))
                        {
                            RestUntil[h.StringId] = now + Math.Max(1, S.Get.LandlessPatrolDays) + RestDays;
                            given++;
                        }
                    }
                }
                if (given > 0) Log.Info($"[sin tierra] {given} partida(s) de clanes sin feudo salen a patrullar");
            }
            catch (Exception ex) { Log.Error("Patrullas de clanes sin tierra", ex); }
        }

        private static bool IsLandless(Clan c) =>
            c != null && !c.IsEliminated && c != Clan.PlayerClan && !c.IsBanditFaction && !c.IsRebelClan
            && c.Leader != null && c.Fiefs.Count == 0 && c.Settlements.Count == 0;

        /// <summary>Only parties doing nothing in particular, able to stay out for some days.</summary>
        private static bool Fit(MobileParty p, Hero h, double now)
        {
            if (p == null || !p.IsActive || p.IsMainParty || !p.IsLordParty || p.IsDisbanding) return false;
            if (p.Army != null || p.MapEvent != null || p.SiegeEvent != null || p.BesiegedSettlement != null || p.IsCurrentlyAtSea) return false;
            if (h.IsPrisoner || h.IsWounded || OrderSystem.FindAny(h) != null) return false;
            if (RestUntil.TryGetValue(h.StringId, out double until) && now < until) return false;
            if (!Healthy(p)) return false;
            // Inside a kingdom the game already gives them wars to fight: only take those standing still
            if (p.ActualClan?.Kingdom != null)
                return p.CurrentSettlement != null || p.DefaultBehavior == AiBehavior.Hold || p.DefaultBehavior == AiBehavior.None;
            return true;
        }

        private static bool Healthy(MobileParty p) =>
            p.GetNumDaysForFoodToLast() >= 4 && p.PartySizeRatio >= 0.4f && p.MemberRoster.TotalManCount >= 15;

        private static Settlement PickSettlement(Clan clan, MobileParty p)
        {
            var from = p.GetPosition2D;
            var own = (IFaction)clan.Kingdom ?? clan;
            // Never a place at war with the employer or with the party's own faction, whichever branch picks it
            IEnumerable<Settlement> all = Settlement.All.Where(s => s != null && (s.IsTown || s.IsCastle) && !s.IsUnderSiege && s.MapFaction != null && !QuarantineRules.Closed(s)
                && !Hostile(s, own, p));
            if (clan.Kingdom != null) all = all.Where(s => s.MapFaction == clan.Kingdom);
            else all = all.Where(s => s.IsTown);
            return all.OrderBy(s => s.GetPosition2D.Distance(from)).FirstOrDefault();
        }

        private static bool Hostile(Settlement s, IFaction employer, MobileParty p)
        {
            var f = s.MapFaction;
            if (f == null) return true;
            return (employer != null && f != employer && f.IsAtWarWith(employer)) || (p?.MapFaction != null && f != p.MapFaction && f.IsAtWarWith(p.MapFaction));
        }

        /// <summary>Patrols under way: called off when the party needs the game's own AI again or the place turned hostile.</summary>
        private static void Review(double now)
        {
            foreach (var o in OrderSystem.Orders.Where(x => x.Auto && x.Reason == null).ToList())
            {
                try
                {
                    var h = Hero.Find(o.HeroId);
                    var p = OrderSystem.PartyLedBy(h);
                    if (h == null || p == null) continue;                          // the order tick drops it
                    var st = Settlement.Find(o.TargetId);
                    // The employer is read again: the clan may have changed sides, or the place its owner, since the order
                    bool hostile = st == null || Hostile(st, (IFaction)h.Clan?.Kingdom ?? h.Clan, p) || st.IsUnderSiege || QuarantineRules.Closed(st);
                    if (hostile || !Healthy(p) || !IsLandless(h.Clan))
                    {
                        OrderSystem.Cancel(h, silent: true);
                        RestUntil[h.StringId] = now + RestDays;
                    }
                }
                catch (Exception ex) { Log.Error("Patrullas de clanes sin tierra: repaso", ex); }
            }
            if (RestUntil.Count > 400)
                foreach (var k in RestUntil.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) RestUntil.Remove(k);
        }

        /// <summary>Option turned off: whoever was patrolling goes back to the game's AI.</summary>
        private static void DropAll()
        {
            foreach (var o in OrderSystem.Orders.Where(x => x.Auto && x.Reason == null).ToList())
            {
                var h = Hero.Find(o.HeroId);
                if (h != null) OrderSystem.Cancel(h, silent: true); else OrderSystem.Orders.Remove(o);
            }
        }
    }
}
