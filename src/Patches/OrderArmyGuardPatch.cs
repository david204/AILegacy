using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace AILegacy.Patches
{
    /// <summary>
    /// A lord carrying out something agreed with you (follow, wait, go somewhere, patrol...) ignores calls to join
    /// someone else's army while the order lasts. Otherwise the game pulls him away and the order never gets done.
    /// He can still join your army or one he leads himself.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.Army), MethodType.Setter)]
    internal static class OrderArmyGuardPatch
    {
        private static bool Prefix(MobileParty __instance, Army value)
        {
            try
            {
                if (value == null || __instance == null || __instance.IsMainParty || OrderSystem.Orders.Count == 0) return true;
                var lead = value.LeaderParty;
                if (lead == null || lead == __instance || lead == MobileParty.MainParty) return true;
                var h = __instance.LeaderHero;
                if (h == null) return true;
                var o = OrderSystem.Find(h);
                if (o == null || o.Type == "approach") return true;
                Log.Info($"[order] {h.Name} ignores the army call from {lead.LeaderHero?.Name}: busy carrying out '{o.Type} {o.TargetName}'");
                return false;
            }
            catch (Exception ex) { Log.Error("Order vs army call", ex); return true; }
        }
    }
}
