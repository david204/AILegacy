using System;
using System.Linq;
using AILegacy.Core;
using HarmonyLib;
using SandBox.View.Map.Navigation;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace AILegacy.Patches
{
    /// <summary>"Memories" button in the map bar, between Quests and Clan.</summary>
    public class MemoriesNavigationElement : MapNavigationElementBase
    {
        public const string Id = "ailegacy_memories"; // also the icon layer name in GUI/Brushes/AILegacyMapBar.xml

        public MemoriesNavigationElement(MapNavigationHandler handler) : base(handler) { }

        public override string StringId => Id;
        public override bool IsActive => false;
        public override bool IsLockingNavigation => false;
        public override bool HasAlert => false;

        protected override NavigationPermissionItem GetPermission() =>
            MapNavigationHelper.IsNavigationBarEnabled(_handler) ? new NavigationPermissionItem(true, null) : new NavigationPermissionItem(false, null);

        protected override TextObject GetTooltip() => new TextObject(L.T("AIL_3dad18db", "{=!}Libro de memorias (AI Legacy)"));
        protected override TextObject GetAlertTooltip() => TextObject.GetEmpty();

        public override void OpenView() => MainThread.Post(UI.Books.OpenMemories);
        public override void OpenView(params object[] parameters) => OpenView();
        public override void GoToLink() { }
    }

    [HarmonyPatch(typeof(MapNavigationHandler), "OnCreateElements")]
    internal static class MapBarPatch
    {
        private static void Postfix(MapNavigationHandler __instance, ref INavigationElement[] __result)
        {
            try
            {
                if (__result == null || __result.Any(e => e.StringId == MemoriesNavigationElement.Id)) return;
                var list = __result.ToList();
                int quest = list.FindIndex(e => e.StringId == "quest");
                list.Insert(quest >= 0 ? quest + 1 : list.Count, new MemoriesNavigationElement(__instance));
                __result = list.ToArray();
            }
            catch (Exception ex) { Log.Error("Map bar", ex); }
        }
    }
}
