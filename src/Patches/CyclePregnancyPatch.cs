using System;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;

namespace AILegacy.Patches
{
    /// <summary>
    /// Ciclo femenino y embarazo del juego. La probabilidad diaria que calcula el juego para una mujer casada se multiplica
    /// según el día del ciclo en que está: casi nula con la regla, más del doble en los días fértiles. Con el ciclo apagado
    /// en los ajustes (o si ella no tiene ciclo) el factor es 1 y el juego se queda como estaba.
    /// </summary>
    [HarmonyPatch(typeof(DefaultPregnancyModel), nameof(DefaultPregnancyModel.GetDailyChanceOfPregnancyForHero))]
    internal static class CyclePregnancyPatch
    {
        private static void Postfix(Hero hero, ref float __result)
        {
            try
            {
                if (hero == null || __result <= 0f || !hero.IsFemale || !S.Get.EnableFemaleCycle) return;
                __result *= World.Mood.FertilityFactor(hero);
            }
            catch (Exception ex) { Log.Error("Ciclo y embarazo", ex); }
        }
    }
}
