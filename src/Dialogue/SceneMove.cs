using System;
using System.Collections.Generic;
using AILegacy.Core;
using SandBox;
using SandBox.Missions.AgentBehaviors;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Walks a character to another one in the scene, across floors too. Uses the same behavior the game uses to make
    /// companions follow you, which already handles pathing, stairs and doors.
    /// </summary>
    internal static class SceneMove
    {
        // Agents we set walking, so we never release a companion who was already following you on their own
        private static readonly HashSet<Agent> Ours = new HashSet<Agent>();

        private static DailyBehaviorGroup Group(Agent a)
        {
            try { return a.GetComponent<CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<DailyBehaviorGroup>(); }
            catch { return null; }
        }

        /// <summary>Returns false if the agent can't be moved.</summary>
        public static bool GoTo(Agent a, Agent target)
        {
            try
            {
                if (a == null || target == null || a == target || !a.IsActive() || !target.IsActive() || !a.IsAIControlled || a.MountAgent != null) return false;
                SceneActions.StandFromGround(a);
                var g = Group(a);
                if (g != null)
                {
                    if (g.ScriptedBehavior is FollowAgentBehavior && !Ours.Contains(a)) return false;      // already following someone (with you)
                    try { if (a.IsUsingGameObject) a.StopUsingGameObject(); } catch { }
                    var f = g.GetBehavior<FollowAgentBehavior>() ?? g.AddBehavior<FollowAgentBehavior>();
                    g.SetScriptedBehavior<FollowAgentBehavior>();
                    f.SetTargetAgent(target);
                    Ours.Add(a);
                    return true;
                }
                // No campaign navigator (rare), just send them to the spot
                try { if (a.IsUsingGameObject) a.StopUsingGameObject(); } catch { }
                var wp = target.GetWorldPosition();
                a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun);
                Ours.Add(a);
                return true;
            }
            catch (Exception ex) { Log.Error("Move a character in the scene", ex); return false; }
        }

        public static void Release(Agent a)
        {
            try
            {
                if (a == null || !Ours.Remove(a) || !a.IsActive()) return;
                var g = Group(a);
                if (g != null && g.ScriptedBehavior is FollowAgentBehavior) g.DisableScriptedBehavior();
                a.DisableScriptedMovement();
            }
            catch (Exception ex) { Log.Error("Release a character", ex); }
        }

        /// <summary>Takes charge of someone the game itself set following (brought into the scene behind the player), so they can be released.</summary>
        public static void Adopt(Agent a) { if (a != null) Ours.Add(a); }

        public static void Forget() => Ours.Clear();
    }
}
