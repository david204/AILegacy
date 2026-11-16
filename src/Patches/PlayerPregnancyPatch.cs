using System;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CampaignBehaviors;

namespace AILegacy.Patches
{
    /// <summary>
    /// With "Disable the game's daily pregnancy for you and your spouse" on, the game's daily roll for a married couple
    /// (PregnancyCampaignBehavior.RefreshSpouseVisit) is skipped when the player is one of the two, whichever the
    /// player's sex. Children then only come from intimacy in conversation. Pregnancies already under way and every
    /// other couple are left alone. As AIInfluence's DisableVanillaPlayerPregnancy.
    /// </summary>
    [HarmonyPatch(typeof(PregnancyCampaignBehavior), "RefreshSpouseVisit")]
    internal static class PlayerPregnancyPatch
    {
        private static bool Prefix(Hero hero)
        {
            try
            {
                var me = Hero.MainHero;
                if (me == null || hero?.Spouse == null || !S.Get.DisableVanillaPlayerPregnancy) return true;
                return hero != me && hero.Spouse != me;
            }
            catch (Exception ex) { Log.Error("Player's pregnancy off", ex); return true; }
        }
    }
}
