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
    /// Daily effects of world events and laws on a town or castle go into the game's own ExplainedNumber, each with its
    /// reason, so they show up in the tooltip breakdown and get added once with everything else.
    /// If another mod replaces these models and the calculation no longer passes through here, WorldSim applies them by hand.
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
            catch (Exception ex) { Log.Error("Effects on the game accounts", ex); }
        }

        /// <summary>A village's hearths: same idea, called from the hearth postfix in KingdomLawPatch.cs.</summary>
        public static void AddVillage(Village village, ref ExplainedNumber result)
        {
            try
            {
                WorldSim.ModelSeen(WorldSim.StatHearth);
                if (village == null) return;
                foreach (var (value, label) in WorldSim.VillageModifiers(village))
                    if (Math.Abs(value) >= 0.005f) result.Add(value, new TextObject("{=!}" + label));
            }
            catch (Exception ex) { Log.Error("Effects on a village's hearths", ex); }
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
