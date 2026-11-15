using System;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;

namespace AILegacy.Patches
{
    /// <summary>
    /// Scales the game's daily pregnancy chance for a married woman by her cycle day: near zero during her period, more
    /// than double on fertile days. With the cycle off in settings (or no cycle for her) the factor is 1 and vanilla is unchanged.
    /// </summary>
    [HarmonyPatch(typeof(DefaultPregnancyModel), nameof(DefaultPregnancyModel.GetDailyChanceOfPregnancyForHero))]
    internal static class CyclePregnancyPatch
    {
        private static void Postfix(Hero hero, ref float __result)
        {
            try
            {
                if (hero == null || __result <= 0f || !S.Get.EnableFemaleCycle) return;
                // The game can ask for either spouse; her cycle is the one that counts
                var woman = hero.IsFemale ? hero : hero.Spouse;
                if (woman == null || !woman.IsFemale) return;
                __result = Math.Min(1f, __result * World.Mood.FertilityFactor(woman));
            }
            catch (Exception ex) { Log.Error("Cycle and pregnancy", ex); }
        }
    }
}
