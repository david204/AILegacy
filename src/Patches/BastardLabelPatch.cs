using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace AILegacy.Patches
{
    /// <summary>In the encyclopedia and the family lists, a child born out of wedlock reads as such until made legitimate.</summary>
    [HarmonyPatch(typeof(TaleWorlds.CampaignSystem.Conversation.ConversationHelper), nameof(TaleWorlds.CampaignSystem.Conversation.ConversationHelper.GetHeroRelationToHeroTextShort))]
    internal static class BastardLabelPatch
    {
        private static void Postfix(Hero queriedHero, Hero baseHero, bool uppercaseFirst, ref string __result)
        {
            try
            {
                if (queriedHero == null || baseHero == null) return;
                // the player's concubine, seen from the player (and the other way round)
                if (Concubines.Enabled && ((baseHero == Hero.MainHero && Concubines.Is(queriedHero)) || (queriedHero == Hero.MainHero && Concubines.Is(baseHero))))
                {
                    string c = queriedHero == Hero.MainHero ? L.T("AIL_e7b1c840", "amante") : queriedHero.IsFemale ? L.T("AIL_e7b1c841", "concubina") : L.T("AIL_e7b1c842", "concubino");
                    __result = uppercaseFirst && c.Length > 0 ? char.ToUpper(c[0]) + c.Substring(1) : c;
                    return;
                }
                if (!Bastards.Enabled) return;
                if (queriedHero.Father != baseHero && queriedHero.Mother != baseHero) return;
                if (!Bastards.IsBaseborn(queriedHero)) return;
                string label = queriedHero.IsFemale ? L.T("AIL_e7b1c821", "hija bastarda") : L.T("AIL_e7b1c822", "hijo bastardo");
                __result = uppercaseFirst && label.Length > 0 ? char.ToUpper(label[0]) + label.Substring(1) : label;
            }
            catch { }
        }
    }
}
