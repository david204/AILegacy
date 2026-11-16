using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using HarmonyLib;

namespace AILegacy.Patches
{
    /// <summary>
    /// The voice of a reply in a dialogue, played by the game itself. As the dialogue shows a line it asks the conversation (on
    /// the map, its tableau; in a scene, CampaignMissionComponent) to play its sound file; our lines have none. When the line is the
    /// character's reply and its voice is ready, that file goes in its place, so the game plays it at the speaker and moves their
    /// mouth from the mouth cues beside it (same name, .xml), as it does with its own voiced lines.
    /// </summary>
    [HarmonyPatch]
    internal static class VoiceLinePatch
    {
        private static int _errors;

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var t in new[] { typeof(SandBox.Missions.MissionLogics.CampaignMissionComponent), typeof(SandBox.View.Map.MapConversationView.MapConversationMission) })
            {
                var m = t.GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                         .FirstOrDefault(x => x.Name.EndsWith("OnConversationPlay", StringComparison.Ordinal) && x.GetParameters().Length == 5);
                if (m != null) yield return m;
            }
        }

        private static void Prefix(ref string soundPath)
        {
            try
            {
                bool reply = AILegacy.Voice.Speech.ChatLineShowing;
                AILegacy.Voice.Speech.ChatLineShowing = false;
                if (!reply || !string.IsNullOrEmpty(soundPath)) return;
                if (AILegacy.Voice.Speech.TakeForLine(out var file)) soundPath = file;
            }
            catch (Exception ex) { if (_errors++ < 5) Log.Error("Voice of a dialogue line", ex); }
        }
    }
}
