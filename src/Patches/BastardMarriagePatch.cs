using System;
using AILegacy.Settings;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;

namespace AILegacy.Patches
{
    /// <summary>
    /// In a marriage negotiation a baseborn child is worth less, as Illegitimate Children does it: when the one asked for is
    /// baseborn the deal's value drops by that share of itself, and when the one asking is baseborn it rises by it.
    /// </summary>
    [HarmonyPatch(typeof(MarriageBarterable), nameof(MarriageBarterable.GetUnitValueForFaction))]
    internal static class BastardMarriagePatch
    {
        private static void Postfix(MarriageBarterable __instance, IFaction faction, ref int __result)
        {
            try
            {
                int share = S.Get.BastardMarriageValuePercent;
                if (share <= 0 || !Bastards.Enabled || __instance == null) return;
                Hero asking = __instance.ProposingHero, asked = __instance.HeroBeingProposedTo;
                if (asking == null || asked == null) return;
                int cut = (int)Math.Round(Math.Abs((double)__result) * share / 100.0);
                if (Bastards.IsBaseborn(asked)) __result -= cut;
                if (Bastards.IsBaseborn(asking)) __result += cut;
            }
            catch { }
        }
    }
}
