using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.Localization;

namespace AILegacy.Patches
{
    /// <summary>
    /// Tributes between kingdoms in the ruler's clan gold breakdown ("Tribute payment to X", "Tribute received from X").
    /// The gold moves in WorldSim.PayTributes, so these lines only go into the calculations made to be read
    /// (with descriptions, nothing withdrawn): the daily one that really changes the gold never sees them.
    /// </summary>
    internal static class TributeFinance
    {
        private static int _errors;

        /// <param name="sign">1 = only tributes received, -1 = only those paid, 0 = both.</param>
        public static void Add(Clan clan, bool includeDescriptions, bool applyWithdrawals, int sign, ref ExplainedNumber result)
        {
            try
            {
                if (!includeDescriptions || applyWithdrawals || clan == null || WorldSim.State?.Tributes == null || WorldSim.State.Tributes.Count == 0) return;
                foreach (var (amount, label) in WorldSim.TributeLines(clan))
                    if (sign == 0 || Math.Sign(amount) == sign) result.Add(amount, new TextObject("{=!}" + label));
            }
            catch (Exception ex) { if (_errors++ < 5) Log.Error("Tribute in the clan finances", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanGoldChange))]
    internal static class TributeGoldChangePatch
    {
        private static void Postfix(Clan clan, bool includeDescriptions, bool applyWithdrawals, ref ExplainedNumber __result)
            => TributeFinance.Add(clan, includeDescriptions, applyWithdrawals, 0, ref __result);
    }

    /// <summary>The clan screen lists income and expenses with these two: received goes in one, paid in the other.</summary>
    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanIncome))]
    internal static class TributeIncomePatch
    {
        private static void Postfix(Clan clan, bool includeDescriptions, bool applyWithdrawals, ref ExplainedNumber __result)
            => TributeFinance.Add(clan, includeDescriptions, applyWithdrawals, 1, ref __result);
    }

    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanExpenses))]
    internal static class TributeExpensesPatch
    {
        private static void Postfix(Clan clan, bool includeDescriptions, bool applyWithdrawals, ref ExplainedNumber __result)
            => TributeFinance.Add(clan, includeDescriptions, applyWithdrawals, -1, ref __result);
    }
}
