using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace AILegacy.Patches
{
    /// <summary>
    /// Los efectos diarios de los sucesos del mundo y de las leyes sobre una ciudad o castillo (prosperidad, comida, seguridad,
    /// lealtad) entran en la cuenta que hace el propio juego, cada uno con su motivo. Así salen en el desglose al pasar el
    /// ratón por la cifra, y el juego los suma una sola vez con el resto. Antes se sumaban a mano por detrás y no se veían.
    /// Si otro mod sustituye estos modelos y la cuenta deja de pasar por aquí, WorldSim vuelve a aplicarlos a mano.
    /// </summary>
    internal static class TownEffects
    {
        public static void Add(Town town, int stat, ref ExplainedNumber result)
        {
            try
            {
                WorldSim.ModelSeen(stat);
                if (town == null) return;
                foreach (var (value, label) in WorldSim.TownModifiers(town, stat))
                    if (Math.Abs(value) >= 0.005f) result.Add(value, new TextObject("{=!}" + label));
            }
            catch (Exception ex) { Log.Error("Efectos en la cuenta del juego", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultSettlementProsperityModel), nameof(DefaultSettlementProsperityModel.CalculateProsperityChange))]
    internal static class TownProsperityPatch
    {
        private static void Postfix(Town fortification, ref ExplainedNumber __result) => TownEffects.Add(fortification, WorldSim.StatProsperity, ref __result);
    }

    [HarmonyPatch(typeof(DefaultSettlementFoodModel), nameof(DefaultSettlementFoodModel.CalculateTownFoodStocksChange))]
    internal static class TownFoodPatch
    {
        private static void Postfix(Town town, ref ExplainedNumber __result) => TownEffects.Add(town, WorldSim.StatFood, ref __result);
    }

    [HarmonyPatch(typeof(DefaultSettlementSecurityModel), nameof(DefaultSettlementSecurityModel.CalculateSecurityChange))]
    internal static class TownSecurityPatch
    {
        private static void Postfix(Town town, ref ExplainedNumber __result) => TownEffects.Add(town, WorldSim.StatSecurity, ref __result);
    }

    [HarmonyPatch(typeof(DefaultSettlementLoyaltyModel), nameof(DefaultSettlementLoyaltyModel.CalculateLoyaltyChange))]
    internal static class TownLoyaltyPatch
    {
        private static void Postfix(Town town, ref ExplainedNumber __result) => TownEffects.Add(town, WorldSim.StatLoyalty, ref __result);
    }
}
