using System;
using System.Collections;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;

namespace AILegacy.Patches
{
    /// <summary>
    /// En la lista de personajes de un asentamiento (ciudad, castillo, taberna…) se quita la opción
    /// «Hablar» (charla rápida desde el menú, sin escena) y queda «Visitar»: hay que ir hasta la persona y hablarle allí.
    /// </summary>
    [HarmonyPatch]
    internal static class QuickTalkPatch
    {
        private static MethodBase TargetMethod()
        {
            // Con el nombre del ensamblado: lo carga si aún no lo está cuando se aplican los parches
            const string name = "TaleWorlds.CampaignSystem.ViewModelCollection.GameMenu.Overlay.SettlementMenuOverlayVM";
            var t = Type.GetType(name + ", TaleWorlds.CampaignSystem.ViewModelCollection") ?? AccessTools.TypeByName(name);
            return t == null ? null : AccessTools.Method(t, "ExecuteOnSetAsActiveContextMenuItem");
        }

        private static bool Prepare() => TargetMethod() != null;

        private static void Postfix(object __instance)
        {
            try
            {
                if (!S.Get.BlockQuickTalk || __instance == null) return;
                var list = AccessTools.Property(__instance.GetType(), "ContextList")?.GetValue(__instance) as IList;
                if (list == null) return;
                for (int i = list.Count - 1; i >= 0; i--)
                {
                    var item = list[i];
                    // Identifier es un campo público (no una propiedad) de StringItemWithEnabledAndHintVM
                    var id = item == null ? null : AccessTools.Field(item.GetType(), "Identifier")?.GetValue(item);
                    if (id != null && id.ToString() == "QuickConversation") list.RemoveAt(i);
                }
            }
            catch (Exception ex) { Log.Error("Quitar «Hablar» del menú de personajes", ex); }
        }
    }
}
