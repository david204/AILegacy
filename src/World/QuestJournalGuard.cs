using System;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace AILegacy.World
{
    /// <summary>
    /// Red de seguridad del diario de misiones: CampaignUIHelper.GetQuestsRelatedToSettlement (la usan los rótulos de los
    /// asentamientos en el mapa) puede lanzar un NullReferenceException si queda marcado algo de una misión rota
    /// (un dador muerto, una partida destruida…). Si falla, en vez de romper la interfaz devuelve una lista vacía.
    /// </summary>
    [HarmonyPatch]
    internal static class QuestsRelatedToSettlementGuard
    {
        private static bool _warned;

        private static MethodBase TargetMethod()
        {
            const string name = "TaleWorlds.CampaignSystem.ViewModelCollection.CampaignUIHelper";
            var t = Type.GetType(name + ", TaleWorlds.CampaignSystem.ViewModelCollection") ?? AccessTools.TypeByName(name);
            return t == null ? null : AccessTools.Method(t, "GetQuestsRelatedToSettlement");
        }

        private static bool Prepare() => TargetMethod() != null;

        private static Exception Finalizer(Exception __exception, ref List<(bool isHeroQuestGiver, QuestBase quest)> __result)
        {
            if (__exception == null) return null;
            if (!_warned) { _warned = true; Log.Error("Diario de misiones: GetQuestsRelatedToSettlement ha fallado (se ignora)", __exception); }
            __result = new List<(bool, QuestBase)>();
            return null;
        }
    }

    /// <summary>Lo mismo para CampaignUIHelper.IsQuestRelatedToSettlement: si falla, «no está relacionada».</summary>
    [HarmonyPatch]
    internal static class IsQuestRelatedToSettlementGuard
    {
        private static bool _warned;

        private static MethodBase TargetMethod()
        {
            const string name = "TaleWorlds.CampaignSystem.ViewModelCollection.CampaignUIHelper";
            var t = Type.GetType(name + ", TaleWorlds.CampaignSystem.ViewModelCollection") ?? AccessTools.TypeByName(name);
            return t == null ? null : AccessTools.Method(t, "IsQuestRelatedToSettlement");
        }

        private static bool Prepare() => TargetMethod() != null;

        private static Exception Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception == null) return null;
            if (!_warned) { _warned = true; Log.Error("Diario de misiones: IsQuestRelatedToSettlement ha fallado (se ignora)", __exception); }
            __result = false;
            return null;
        }
    }
}
