using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Workshops;

namespace AILegacy.Patches
{
    /// <summary>
    /// Effects of the laws created by rulers, added to the game's own calculations so each one shows in the tooltip
    /// with the law's name. Loyalty, prosperity, security and food go in through TownEffectsPatch.cs with the world events.
    /// Every postfix leaves the vanilla result alone when no law is in force or something fails.
    /// </summary>
    internal static class LawPatchLog
    {
        private static int _shown;
        // These run many times a second: a broken one must not flood the log
        public static void Error(string what, Exception ex) { if (_shown++ < 5) Log.Error("[laws] " + what, ex); }
    }

    [HarmonyPatch(typeof(DefaultSettlementMilitiaModel), nameof(DefaultSettlementMilitiaModel.CalculateMilitiaChange))]
    internal static class LawMilitiaPatch
    {
        private static void Postfix(Settlement settlement, ref ExplainedNumber __result)
        {
            try { if (settlement != null && !Laws.Idle) Laws.AddForSettlement(settlement, Laws.Militia, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("militia", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultSettlementMilitiaModel), nameof(DefaultSettlementMilitiaModel.CalculateVeteranMilitiaSpawnChance))]
    internal static class LawVeteransPatch
    {
        private static void Postfix(Settlement settlement, ref ExplainedNumber __result)
        {
            try { if (settlement != null && !Laws.Idle) Laws.AddForSettlement(settlement, Laws.Veterans, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("veteran militia", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultSettlementProsperityModel), nameof(DefaultSettlementProsperityModel.CalculateHearthChange))]
    internal static class LawHearthPatch
    {
        private static void Postfix(Village village, ref ExplainedNumber __result)
        {
            try { if (village != null && !Laws.Idle) Laws.AddForSettlement(village.Settlement, Laws.Hearth, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("households", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultWorkshopModel), nameof(DefaultWorkshopModel.GetEffectiveConversionSpeedOfProduction))]
    internal static class LawWorkshopPatch
    {
        private static void Postfix(Workshop workshop, ref ExplainedNumber __result)
        {
            try { if (workshop != null && !Laws.Idle) Laws.AddForSettlement(workshop.Settlement, Laws.Workshops, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("workshops", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultBuildingConstructionModel), nameof(DefaultBuildingConstructionModel.CalculateDailyConstructionPower))]
    internal static class LawConstructionPatch
    {
        private static void Postfix(Town town, ref ExplainedNumber __result)
        {
            try { if (town != null && !Laws.Idle) Laws.AddForSettlement(town.Settlement, Laws.Construction, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("construction", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultSettlementTaxModel), nameof(DefaultSettlementTaxModel.CalculateTownTax))]
    internal static class LawTaxPatch
    {
        private static void Postfix(Town town, ref ExplainedNumber __result)
        {
            try { if (town != null && !Laws.Idle) Laws.AddForSettlement(town.Settlement, Laws.Tax, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("taxes", ex); }
        }
    }

    /// <summary>This one returns a bare chance, so there is no tooltip line to add: the chance is scaled.</summary>
    [HarmonyPatch(typeof(DefaultVolunteerModel), nameof(DefaultVolunteerModel.GetDailyVolunteerProductionProbability))]
    internal static class LawRecruitmentPatch
    {
        private static void Postfix(Settlement settlement, ref float __result)
        {
            try
            {
                if (settlement == null || Laws.Idle) return;
                float f = Laws.SumForSettlement(settlement, Laws.Recruitment);
                if (f != 0f) __result = Math.Max(0f, Math.Min(1f, __result * (1f + f)));
            }
            catch (Exception ex) { LawPatchLog.Error("recruits", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultClanPoliticsModel), nameof(DefaultClanPoliticsModel.CalculateInfluenceChange))]
    internal static class LawInfluencePatch
    {
        private static void Postfix(Clan clan, ref ExplainedNumber __result)
        {
            try { if (clan != null && !Laws.Idle) Laws.AddForClan(clan, Laws.Influence, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("influence", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanIncome))]
    internal static class LawClanIncomePatch
    {
        private static void Postfix(Clan clan, ref ExplainedNumber __result)
        {
            try { if (clan != null && !Laws.Idle) Laws.AddForClan(clan, Laws.ClanIncome, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("clan income", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultPartySizeLimitModel), nameof(DefaultPartySizeLimitModel.GetPartyMemberSizeLimit))]
    internal static class LawPartySizePatch
    {
        private static void Postfix(PartyBase party, ref ExplainedNumber __result)
        {
            try { if (party?.MobileParty != null && !Laws.Idle) Laws.AddForParty(party.MobileParty, Laws.PartySize, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("party size", ex); }
        }
    }

    /// <summary>Wages of a lord's party, or of a garrison (that one counts as an effect on its settlement).</summary>
    [HarmonyPatch(typeof(DefaultPartyWageModel), nameof(DefaultPartyWageModel.GetTotalWage))]
    internal static class LawWagesPatch
    {
        private static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            try
            {
                if (mobileParty == null || Laws.Idle) return;
                if (mobileParty.IsGarrison)
                {
                    var st = mobileParty.CurrentSettlement ?? mobileParty.HomeSettlement;
                    if (st != null) Laws.AddForSettlement(st, Laws.GarrisonWages, ref __result);
                }
                else Laws.AddForParty(mobileParty, Laws.Wages, ref __result);
            }
            catch (Exception ex) { LawPatchLog.Error("wages", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultPartyMoraleModel), nameof(DefaultPartyMoraleModel.GetEffectivePartyMorale))]
    internal static class LawMoralePatch
    {
        private static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            try { if (mobileParty != null && !Laws.Idle) Laws.AddForParty(mobileParty, Laws.Morale, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("morale", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultPartySpeedCalculatingModel), nameof(DefaultPartySpeedCalculatingModel.CalculateFinalSpeed))]
    internal static class LawSpeedPatch
    {
        private static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            try { if (mobileParty != null && !Laws.Idle) Laws.AddForParty(mobileParty, Laws.Speed, ref __result); }
            catch (Exception ex) { LawPatchLog.Error("speed", ex); }
        }
    }

    /// <summary>Influence a lord pays to call a party into his army. A plain number, so it is scaled.</summary>
    [HarmonyPatch(typeof(DefaultArmyManagementCalculationModel), nameof(DefaultArmyManagementCalculationModel.CalculatePartyInfluenceCost))]
    internal static class LawArmyCostPatch
    {
        private static void Postfix(MobileParty armyLeaderParty, ref int __result)
        {
            try
            {
                if (armyLeaderParty == null || __result <= 0 || Laws.Idle) return;
                float f = Laws.SumForParty(armyLeaderParty, Laws.ArmyCost);
                if (f != 0f) __result = Math.Max(1, (int)Math.Round(__result * (1f + f)));
            }
            catch (Exception ex) { LawPatchLog.Error("army cost", ex); }
        }
    }
}
