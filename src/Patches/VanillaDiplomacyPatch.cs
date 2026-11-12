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
    /// Con la diplomacia de AI Legacy activa, los clanes del juego dejan de proponer por su cuenta
    /// guerras, paces, acuerdos comerciales y alianzas. Eso lo deciden ahora los reyes con sus declaraciones.
    /// (Las políticas y anexiones del juego siguen como siempre.)
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
                __result = null;      // «ninguna propuesta»
                return false;
            }
            catch (Exception ex) { Log.Error("Bloqueo de la diplomacia del juego", ex); return true; }
        }
    }

    /// <summary>A quien te corteja (o cortejas) el juego no lo casa con otro por su cuenta.</summary>
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

    /// <summary>Los sucesos del mundo encarecen o abaratan el mercado de un lugar mientras duran.</summary>
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

    /// <summary>El buen o mal nombre de un clan, por los sucesos del mundo, se nota en lo que ingresa.</summary>
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
                __result.AddFactor(f, new TaleWorlds.Localization.TextObject("{=!}" + (string.IsNullOrWhiteSpace(reason) ? L.T("El nombre del clan (sucesos del mundo)") : reason)));
            }
            catch (Exception ex) { Log.Error("Ingresos del clan por sucesos", ex); }
        }
    }

    /// <summary>Impuesto extraordinario de la corona (acción set_tax de un rey): más o menos recaudación en sus ciudades.</summary>
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
