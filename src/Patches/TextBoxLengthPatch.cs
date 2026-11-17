using HarmonyLib;
using TaleWorlds.GauntletUI.BaseTypes;

namespace AILegacy.Patches
{
    /// <summary>
    /// The game's text boxes stop at 512 characters, which cuts a long message, letter or declaration short. Boxes left at
    /// that default take 2048 (as AIInfluence does); a box that sets its own limit keeps it.
    /// </summary>
    [HarmonyPatch(typeof(EditableTextWidget), nameof(EditableTextWidget.MaxLength), MethodType.Getter)]
    internal static class TextBoxLengthPatch
    {
        private static void Postfix(ref int __result)
        {
            if (__result == 512) __result = 2048;
        }
    }
}
