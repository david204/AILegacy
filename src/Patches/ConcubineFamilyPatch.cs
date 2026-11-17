using System;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Items;
using TaleWorlds.CampaignSystem.ViewModelCollection.Encyclopedia.Pages;
using TaleWorlds.Library;

namespace AILegacy.Patches
{
    /// <summary>
    /// In a character's encyclopedia page, a concubine and the one they live with appear in each other's family list, as
    /// Illegitimate Children shows them (with the «concubine» and «paramour» tags of <see cref="BastardLabelPatch"/>).
    /// </summary>
    [HarmonyPatch(typeof(EncyclopediaHeroPageVM), nameof(EncyclopediaHeroPageVM.Refresh))]
    internal static class ConcubineFamilyPatch
    {
        private static readonly AccessTools.FieldRef<EncyclopediaHeroPageVM, Hero> HeroOf = AccessTools.FieldRefAccess<EncyclopediaHeroPageVM, Hero>("_hero");
        private static int _errors;

        private static void Postfix(EncyclopediaHeroPageVM __instance)
        {
            try
            {
                if (!Concubines.Enabled || !S.Get.BastardEncyclopediaTags || __instance?.Family == null) return;
                var hero = HeroOf(__instance);
                if (hero == null) return;
                var family = __instance.Family;
                void Add(Hero h)
                {
                    if (h == null || h == hero || family.Any(x => x.Hero == h)) return;
                    family.Add(new EncyclopediaFamilyMemberVM(h, hero));
                }
                Add(Concubines.PartnerOf(hero));
                foreach (var c in Concubines.KeptBy(hero)) Add(c);
            }
            catch (Exception ex) { if (_errors++ < 3) Log.Error("Encyclopedia: concubines in the family list", ex); }
        }
    }
}
