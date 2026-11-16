using System;
using AILegacy.Core;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;

namespace AILegacy.Patches
{
    /// <summary>
    /// A fix for the game's speed tooltip. MobileParty.SpeedExplained (the breakdown shown when hovering a party) stores
    /// the base speed it builds, with its list of descriptions, in the party's cache, and then adds the final speed's
    /// lines to that same list: the cache grows on every hover and the party's later speed checks carry it around. Here
    /// the breakdown is built on its own, the cache is left as it was, and only the last speed is updated, as the game
    /// does. AIInfluence carries the same fix. On any error the game's own code runs.
    /// </summary>
    [HarmonyPatch(typeof(MobileParty), nameof(MobileParty.SpeedExplained), MethodType.Getter)]
    internal static class PartySpeedTooltipPatch
    {
        private static readonly AccessTools.FieldRef<MobileParty, float> LastSpeed = Field();
        private static bool _logged;

        private static AccessTools.FieldRef<MobileParty, float> Field()
        {
            try { return AccessTools.FieldRefAccess<MobileParty, float>("_lastCalculatedSpeed"); }
            catch { return null; }
        }

        private static bool Prefix(MobileParty __instance, ref ExplainedNumber __result)
        {
            try
            {
                var model = Campaign.Current?.Models?.PartySpeedCalculatingModel;
                if (model == null || __instance == null || LastSpeed == null) return true;
                var speed = model.CalculateBaseSpeed(__instance, true);
                speed = model.CalculateFinalSpeed(__instance, speed);
                LastSpeed(__instance) = speed.ResultNumber;
                __result = speed;
                return false;
            }
            catch (Exception ex)
            {
                if (!_logged) { _logged = true; Log.Error("Party speed tooltip", ex); }
                return true;
            }
        }
    }
}
