using System;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;

namespace AILegacy.Patches
{
    /// <summary>
    /// Townsfolk dress from the everyday clothes of all the common folk of their culture (World/ResidentFaces.cs).
    /// The game builds every civilian outfit here, from the person's template and seed.
    /// </summary>
    [HarmonyPatch(typeof(Equipment), nameof(Equipment.GetRandomEquipmentElements))]
    internal static class ResidentClothesPatch
    {
        private static void Postfix(BasicCharacterObject character, Equipment.EquipmentType equipmentType, int seed, Equipment __result)
        {
            try { ResidentFaces.MixClothes(character, equipmentType, seed, __result); }
            catch { }
        }
    }

    /// <summary>
    /// Every townsman (and, if asked, every soldier, guard, mercenary and bandit) gets a face of their own
    /// (World/ResidentFaces.cs). Seed -2 is the game asking for the template's own face, which stays as it is.
    /// </summary>
    [HarmonyPatch(typeof(CharacterObject), nameof(CharacterObject.GetBodyProperties))]
    internal static class ResidentFacePatch
    {
        private static void Postfix(CharacterObject __instance, Equipment equipment, int seed, ref BodyProperties __result)
        {
            try
            {
                if (seed != -2 && ResidentFaces.WantsFace(__instance, out bool fighter))
                    __result = ResidentFaces.UniqueFace(__instance, equipment, seed, __result, fighter);
            }
            catch { }
        }
    }
}
