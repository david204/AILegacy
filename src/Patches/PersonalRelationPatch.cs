using System;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;

namespace AILegacy.Patches
{
    /// <summary>
    /// Personal relation with each NPC. For nobles the game uses the relation with their clan LEADER, so winning over a
    /// lord actually changed the relation with his clan leader. When one side is the player, this makes reads and
    /// changes use the two actual people.
    /// </summary>
    [HarmonyPatch(typeof(DefaultDiplomacyModel), nameof(DefaultDiplomacyModel.GetHeroesForEffectiveRelation))]
    internal static class PersonalRelationPatch
    {
        private static void Postfix(Hero hero1, Hero hero2, ref Hero effectiveHero1, ref Hero effectiveHero2)
        {
            try
            {
                if (hero1 == null || hero2 == null || !S.Get.PersonalRelation) return;
                if (hero1 != Hero.MainHero && hero2 != Hero.MainHero) return;
                effectiveHero1 = hero1;
                effectiveHero2 = hero2;
            }
            catch (Exception ex) { Log.Error("Relación personal", ex); }
        }
    }
}
