using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;

namespace AILegacy.Patches
{
    /// <summary>
    /// A party lent to a notable or a wanderer so they can follow the player (World\OrderSystem.cs) is not disbanded by
    /// the game while the order lasts: the game would scatter it and leave its leader stranded halfway. When the order
    /// ends, the order system breaks it up itself. As AIInfluence's NonCombatantPartyProtector.
    /// </summary>
    [HarmonyPatch(typeof(DisbandPartyAction), nameof(DisbandPartyAction.StartDisband))]
    internal static class TempPartyPatch
    {
        private static bool Prefix(MobileParty disbandParty)
        {
            try
            {
                if (disbandParty == null || !disbandParty.IsActive || !OrderSystem.IsTempParty(disbandParty)) return true;
                Log.Info($"Orders: the game tried to disband {disbandParty.Name}, lent to {disbandParty.LeaderHero?.Name} to follow the player; kept");
                return false;
            }
            catch (Exception ex) { Log.Error("Orders: keep the lent party", ex); return true; }
        }
    }
}
