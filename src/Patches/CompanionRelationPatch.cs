using System;
using System.Reflection;
using AILegacy.Core;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;

namespace AILegacy.Patches
{
    /// <summary>
    /// The game removes a companion from the world for good (KillCharacterAction.ApplyByRemove) the moment their relation
    /// with the player drops below -10 (CompanionRolesCampaignBehavior.OnHeroRelationChanged). With conversations that
    /// can sour a relation quickly, that would make companions vanish without a word, so it is skipped: a companion who
    /// has fallen out with the player stays, and leaving is up to them or to the player. AIInfluence does the same.
    /// </summary>
    [HarmonyPatch]
    internal static class CompanionRelationPatch
    {
        private static MethodBase Target()
        {
            var type = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.CompanionRolesCampaignBehavior");
            return type == null ? null : AccessTools.Method(type, "OnHeroRelationChanged");
        }

        private static bool Prepare()
        {
            try
            {
                if (Target() != null) return true;
                Log.Warn("Companions: the game's handler for relation changes was not found; the patch that keeps companions is off");
                return false;
            }
            catch { return false; }
        }

        private static MethodBase TargetMethod() => Target();

        private static bool Prefix(Hero effectiveHero, Hero effectiveHeroGainedRelationWith, int relationChange)
        {
            try
            {
                if (effectiveHero == null || effectiveHeroGainedRelationWith == null || relationChange >= 0) return true;
                var me = Hero.MainHero;
                Hero companion = effectiveHero == me && effectiveHeroGainedRelationWith.IsPlayerCompanion ? effectiveHeroGainedRelationWith
                               : effectiveHero.IsPlayerCompanion && effectiveHeroGainedRelationWith == me ? effectiveHero : null;
                if (companion == null || effectiveHero.GetRelation(effectiveHeroGainedRelationWith) >= -10) return true;
                Log.Info($"[companions] {companion.Name}'s relation with the player is {effectiveHero.GetRelation(effectiveHeroGainedRelationWith)}: the game would remove them from the world; they stay");
                return false;
            }
            catch (Exception ex) { Log.Error("Companions on bad relation", ex); return true; }
        }
    }
}
