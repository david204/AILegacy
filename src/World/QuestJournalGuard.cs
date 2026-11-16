using System;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using HarmonyLib;
using TaleWorlds.CampaignSystem;

namespace AILegacy.World
{
    /// <summary>
    /// The two guards below are applied once a campaign is running, not when the mod loads. Patching a method of
    /// CampaignUIHelper can make the runtime set up that class there and then, and in the main menu its setup fails
    /// (it reads game texts that are not loaded yet). The failure sticks for the whole session, and the first screen
    /// that needs the class (the story screen of character creation) closes the game.
    /// </summary>
    internal static class QuestJournalGuards
    {
        private static bool _done;

        public static void Apply()
        {
            if (_done) return;
            _done = true;
            var harmony = new Harmony("com.ailegacy.questguard");
            foreach (var type in new[] { typeof(QuestsRelatedToSettlementGuard), typeof(IsQuestRelatedToSettlementGuard) })
            {
                try
                {
                    var target = (MethodBase)AccessTools.Method(type, "TargetMethod").Invoke(null, null);
                    if (target == null) { Log.Warn("Quest journal guard: target not found for " + type.Name); continue; }
                    harmony.Patch(target, finalizer: new HarmonyMethod(AccessTools.Method(type, "Finalizer")));
                }
                catch (Exception ex) { Log.Error("Quest journal guard: " + type.Name, ex); }
            }
        }
    }

    /// <summary>
    /// CampaignUIHelper.GetQuestsRelatedToSettlement (used by the settlement nameplates on the map) can throw a
    /// NullReferenceException when a broken quest still tracks something, like a dead giver or a destroyed party.
    /// If it throws, return an empty list instead of breaking the UI.
    /// </summary>
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
            if (!_warned) { _warned = true; Log.Error("Quest journal: GetQuestsRelatedToSettlement failed (ignored)", __exception); }
            __result = new List<(bool, QuestBase)>();
            return null;
        }
    }

    /// <summary>Same for CampaignUIHelper.IsQuestRelatedToSettlement. If it throws, return false.</summary>
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
            if (!_warned) { _warned = true; Log.Error("Quest journal: IsQuestRelatedToSettlement failed (ignored)", __exception); }
            __result = false;
            return null;
        }
    }
}
