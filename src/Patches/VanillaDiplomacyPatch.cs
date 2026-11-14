using System;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.Patches
{
    /// <summary>
    /// With AI Legacy diplomacy on, vanilla clans stop proposing wars, peace, trade agreements and alliances on their own.
    /// Rulers decide those through their declarations now. Vanilla policies and annexations still work as usual.
    /// </summary>
    [HarmonyPatch]
    internal static class VanillaDiplomacyPatch
    {
        private static readonly string[] Methods = { "GetRandomWarDecision", "GetRandomPeaceDecision", "GetRandomTradeAgreementDecision", "GetRandomStartingAllianceDecision" };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.KingdomDecisionProposalBehavior");
            if (t == null) yield break;
            foreach (var name in Methods)
            {
                var m = AccessTools.Method(t, name);
                if (m != null) yield return m;
                else Log.Warn("Diplomacia: no encuentro " + name + " en esta versión del juego");
            }
        }

        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.KingdomDecisionProposalBehavior");
            return t != null && AccessTools.Method(t, Methods[0]) != null;
        }

        private static bool Prefix(ref TaleWorlds.CampaignSystem.Election.KingdomDecision __result)
        {
            try
            {
                if (!Diplomacy.Enabled) return true;
                __result = null;      // no proposal
                return false;
            }
            catch (Exception ex) { Log.Error("Bloqueo de la diplomacia del juego", ex); return true; }
        }
    }

    /// <summary>The game won't marry off someone who is in a romance with the player.</summary>
    [HarmonyPatch(typeof(DefaultMarriageModel), nameof(DefaultMarriageModel.IsCoupleSuitableForMarriage))]
    internal static class CourtshipGuardPatch
    {
        private static void Postfix(Hero firstHero, Hero secondHero, ref bool __result)
        {
            try
            {
                if (!__result || firstHero == null || secondHero == null || firstHero == Hero.MainHero || secondHero == Hero.MainHero) return;
                int min = AILegacy.Settings.S.Get.RomanceProtectsFromMarriage;
                if (min <= 0) return;
                var a = AILegacy.Memory.MemoryStore.Find(firstHero.StringId); var b = AILegacy.Memory.MemoryStore.Find(secondHero.StringId);
                if ((a != null && a.Romance >= min) || (b != null && b.Romance >= min)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>World events raise or lower a settlement's market prices while they last.</summary>
    [HarmonyPatch(typeof(DefaultTradeItemPriceFactorModel), nameof(DefaultTradeItemPriceFactorModel.GetPrice))]
    internal static class MarketPricePatch
    {
        private static void Postfix(TaleWorlds.CampaignSystem.Party.PartyBase merchant, ref int __result)
        {
            try
            {
                if (WorldSim.State.Effects.Count == 0 || merchant?.Settlement == null) return;
                float f = WorldSim.PriceFactor(merchant.Settlement);
                if (Math.Abs(f) >= 0.005f) __result = Math.Max(1, (int)Math.Round(__result * (1f + f)));
            }
            catch (Exception ex) { Log.Error("Precios por sucesos", ex); }
        }
    }

    /// <summary>A clan's reputation from world events affects its income.</summary>
    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanIncome))]
    internal static class ClanIncomePatch
    {
        private static void Postfix(Clan clan, ref ExplainedNumber __result)
        {
            try
            {
                if (clan == null || WorldSim.State.Effects.Count == 0) return;
                float f = WorldSim.ClanIncomeFactor(clan, out string reason);
                if (Math.Abs(f) < 0.005f) return;
                __result.AddFactor(f, new TaleWorlds.Localization.TextObject("{=!}" + (string.IsNullOrWhiteSpace(reason) ? L.T("AIL_94517b9a", "El nombre del clan (sucesos del mundo)") : reason)));
            }
            catch (Exception ex) { Log.Error("Ingresos del clan por sucesos", ex); }
        }
    }

    /// <summary>A ruler's set_tax action raises or lowers tax income in their towns.</summary>
    [HarmonyPatch(typeof(DefaultSettlementTaxModel), nameof(DefaultSettlementTaxModel.GetTownTaxRatio))]
    internal static class KingdomTaxPatch
    {
        private static void Postfix(Town town, ref float __result)
        {
            try { if (WorldSim.State.KingdomTaxPercent.Count > 0) __result *= WorldSim.TaxFactor(town); }
            catch (Exception ex) { Log.Error("Impuesto de la corona", ex); }
        }
    }
}
