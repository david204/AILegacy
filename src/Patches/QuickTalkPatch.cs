using System;
using System.Collections;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;

namespace AILegacy.Patches
{
    /// <summary>
    /// Removes the quick "Talk" option (menu conversation without a scene) from a settlement's character list and leaves
    /// "Visit", so you have to walk up to the person and talk to them there.
    /// </summary>
    [HarmonyPatch]
    internal static class QuickTalkPatch
    {
        private static MethodBase TargetMethod()
        {
            // Assembly-qualified name, so it gets loaded if it isn't yet when patches are applied
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
                    // Identifier is a public field (not a property) on StringItemWithEnabledAndHintVM
                    var id = item == null ? null : AccessTools.Field(item.GetType(), "Identifier")?.GetValue(item);
                    if (id != null && id.ToString() == "QuickConversation") list.RemoveAt(i);
                }
            }
            catch (Exception ex) { Log.Error("Quitar «Hablar» del menú de personajes", ex); }
        }
    }
}
