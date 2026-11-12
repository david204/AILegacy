using System;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;

namespace AILegacy.Patches
{
    /// <summary>
    /// Relación personal con cada NPC. El juego, para los nobles, usa la relación con el JEFE de su clan:
    /// caerle bien (o mal) a un lord se lo apuntaba a su jefe de clan. Con esto, cuando uno de los dos es el jugador,
    /// la relación es la de esas dos personas concretas, tanto al leerla como al cambiarla.
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
