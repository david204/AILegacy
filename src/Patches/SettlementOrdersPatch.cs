using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Dialogue;
using HarmonyLib;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.MissionViews.Order;

namespace AILegacy.Patches
{
    /// <summary>
    /// The game's order bar in the street, the village and the lord's hall, for the fights where you call your troops in
    /// (Backspace, 1-8, F1-F8 as in a battle). Usable only while such a fight is on and you lead your side.
    /// </summary>
    [HarmonyPatch]
    internal static class SettlementOrdersViewPatch
    {
        internal static readonly HashSet<Mission> WithOrders = new HashSet<Mission>();

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = typeof(SandBox.View.Missions.SandBoxMissionViews);
            foreach (var name in new[] { "OpenTownCenterMission", "OpenVillageMission", "OpenTavernMission" })
            {
                var m = AccessTools.Method(t, name);
                if (m != null) yield return m;
            }
        }

        private static void Postfix(Mission mission, ref MissionView[] __result)
        {
            try
            {
                if (mission == null || __result == null) return;
                var list = __result.ToList();
                list.Add(ViewCreator.CreateMissionOrderUIHandler(null));
                list.Add(new OrderTroopPlacer(null));
                list.Add(ViewCreator.CreateMissionFormationMarkerUIHandler(mission));
                list.Add(new MissionFormationTargetSelectionHandler());
                __result = list.ToArray();
                if (WithOrders.Count > 16) WithOrders.Clear();
                WithOrders.Add(mission);
            }
            catch (Exception ex) { Log.Error("Order bar in a settlement", ex); }
        }
    }

    /// <summary>In those scenes the order keys only work during a fight with your soldiers in it; otherwise they do nothing.</summary>
    [HarmonyPatch]
    internal static class SettlementOrdersInputPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(TaleWorlds.MountAndBlade.GauntletUI.GauntletOrderUIHandler), "TickInput");

        private static bool Prefix()
        {
            try
            {
                var m = Mission.Current;
                if (m == null || !SettlementOrdersViewPatch.WithOrders.Contains(m)) return true;
                return Combat.OrdersAllowed;
            }
            catch { return true; }
        }
    }
}
