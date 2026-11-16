using System;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages;
using TaleWorlds.Core;
using TaleWorlds.Core.ViewModelCollection.Generic;

namespace AILegacy.Patches
{
    /// <summary>
    /// The hero's page in the encyclopedia shows their faith next to age and occupation, as AIInfluence does: only for
    /// someone whose character the AI has already written (the faith comes from that profile), and hidden like the rest
    /// of the page while the player knows nothing about them. Needs "Religion and customs of each culture" on.
    /// </summary>
    [HarmonyPatch(typeof(EncyclopediaHeroPageVM), nameof(EncyclopediaHeroPageVM.Refresh))]
    internal static class EncyclopediaReligionPatch
    {
        private const int MaxLength = 90;

        private static void Postfix(EncyclopediaHeroPageVM __instance)
        {
            try
            {
                if (!(__instance?.Obj is Hero hero) || hero == Hero.MainHero || __instance.Stats == null || !S.Get.EnableReligion) return;
                string faith = Short(MemoryStore.Find(hero.StringId)?.Profile?.Religion);
                if (string.IsNullOrEmpty(faith)) return;
                string value = __instance.IsInformationHidden ? GameTexts.FindText("str_missing_info_indicator").ToString() : faith;
                __instance.Stats.Add(new StringPairItemVM(L.T("AIL_e7b1db00", "Religión:"), value));
            }
            catch (Exception ex) { Log.Error("Religion in the encyclopedia", ex); }
        }

        /// <summary>The profile's faith is a sentence or two: its first sentence, cut short if it is still long.</summary>
        private static string Short(string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            int dot = s.IndexOfAny(new[] { '.', ';' });
            if (dot > 10) s = s.Substring(0, dot);
            if (s.Length > MaxLength) s = s.Substring(0, MaxLength - 1).TrimEnd(' ', ',') + "…";
            return s;
        }
    }
}
