using System;
using AILegacy.Core;
using AILegacy.Settings;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.ViewModelCollection.KingdomManagement.Policies;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace AILegacy.Patches
{
    /// <summary>
    /// Kingdom > Policies also lists the laws created by the ruler of the player's kingdom, among the active policies.
    /// They are display-only entries: selecting one shows its text and effects, and the vote button stays off because a
    /// decree is not put to a vote.
    /// </summary>
    internal static class PoliciesScreen
    {
        public static readonly AccessTools.FieldRef<KingdomPoliciesVM, PolicyObject> Selected =
            AccessTools.FieldRefAccess<KingdomPoliciesVM, PolicyObject>("_currentSelectedPolicyObject");
        public static readonly AccessTools.FieldRef<KingdomPoliciesVM, KingdomDecision> Unresolved =
            AccessTools.FieldRefAccess<KingdomPoliciesVM, KingdomDecision>("_currentItemsUnresolvedDecision");

        public static bool On => Laws.Enabled && S.Get.LawsInPoliciesScreen;
    }

    [HarmonyPatch(typeof(KingdomPoliciesVM), nameof(KingdomPoliciesVM.RefreshPolicyList))]
    internal static class PoliciesListPatch
    {
        private static void Postfix(KingdomPoliciesVM __instance)
        {
            try
            {
                if (!PoliciesScreen.On) return;
                var laws = Laws.DisplayPolicies(Clan.PlayerClan?.Kingdom);
                if (laws == null || laws.Count == 0 || __instance.ActivePolicies == null) return;
                var select = AccessTools.MethodDelegate<Action<KingdomPolicyItemVM>>(AccessTools.Method(typeof(KingdomPoliciesVM), "OnPolicySelect"), __instance);
                // a refresh that keeps the list must not add the same law twice
                foreach (var p in laws)
                {
                    bool there = false;
                    foreach (var it in __instance.ActivePolicies) if (it != null && it.Policy == p) { there = true; break; }
                    if (!there) __instance.ActivePolicies.Add(new KingdomPolicyItemVM(p, select, x => true));
                }
                GameTexts.SetVariable("STR", __instance.ActivePolicies.Count);
                __instance.NumOfActivePoliciesText = GameTexts.FindText("str_STR_in_parentheses").ToString();
                // A kingdom with no voted policy would open the active list with nothing selected
                if (__instance.CurrentSelectedPolicy == null && !__instance.IsInProposeMode) __instance.SelectPolicy(laws[0]);
            }
            catch (Exception ex) { Log.Error("Laws on the policies screen", ex); }
        }
    }

    [HarmonyPatch(typeof(KingdomPoliciesVM), "OnPolicySelect")]
    internal static class PoliciesSelectPatch
    {
        private static bool Prefix(KingdomPoliciesVM __instance, KingdomPolicyItemVM policy)
        {
            try
            {
                if (policy == null || !Laws.IsDisplayPolicy(policy.Policy)) return true;
                if (__instance.CurrentSelectedPolicy == policy) return false;
                if (__instance.CurrentSelectedPolicy != null) __instance.CurrentSelectedPolicy.IsSelected = false;
                __instance.CurrentSelectedPolicy = policy;
                policy.IsSelected = true;
                PoliciesScreen.Selected(__instance) = policy.Policy;
                PoliciesScreen.Unresolved(__instance) = null;
                var hint = new TextObject("{=!}" + L.T("AIL_e7b1c5ea", "Ley creada por decreto del soberano. No se vota: solo quien reina puede derogarla, con una declaración."));
                __instance.CanProposeOrDisavowPolicy = false;
                __instance.DoneHint.HintText = hint;
                __instance.ProposeActionExplanationText = hint.ToString();
                __instance.PolicyLikelihood = 0;
                __instance.PolicyLikelihoodText = "—";
                __instance.IsAcceptableItemSelected = true;
                return false;
            }
            catch (Exception ex) { Log.Error("Choosing a law on the policies screen", ex); return false; }
        }
    }

    [HarmonyPatch(typeof(KingdomPoliciesVM), "ExecuteProposeOrDisavow")]
    internal static class PoliciesVotePatch
    {
        // The game would open a vote on a policy the kingdom does not really have
        private static bool Prefix(KingdomPoliciesVM __instance)
        {
            try { return !Laws.IsDisplayPolicy(PoliciesScreen.Selected(__instance)); }
            catch { return true; }
        }
    }
}
