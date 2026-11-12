using System;
using System.Collections.Generic;
using AILegacy.Core;
using HarmonyLib;
using SandBox.View.Map;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade.ViewModelCollection.EscapeMenu;

namespace AILegacy.Patches
{
    /// <summary>Añade "AI Legacy" al menú de Escape del mapa (abre el editor dentro del juego).</summary>
    [HarmonyPatch(typeof(MapScreen), "GetEscapeMenuItems")]
    internal static class EscapeMenuPatch
    {
        private static void Postfix(List<EscapeMenuItemVM> __result)
        {
            try
            {
                if (__result == null) return;
                var item = new EscapeMenuItemVM(new TextObject("{=!}AI Legacy"),
                    o => Web.EditorServer.OpenInBrowser(),
                    "ailegacy_editor",
                    () => new Tuple<bool, TextObject>(false, TextObject.GetEmpty()));
                __result.Insert(Math.Min(2, __result.Count), item);
            }
            catch (Exception ex) { Log.Error("Menú de Escape", ex); }
        }
    }
}
