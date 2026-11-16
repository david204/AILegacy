using System;
using System.Collections.Generic;
using AILegacy.Core;
using SandBox;
using SandBox.Missions.AgentBehaviors;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Walks a character to another one in the scene, across floors too. Uses the same behavior the game uses to make
    /// companions follow you, which already handles pathing, stairs and doors. Also keeps someone standing where they are
    /// while they talk, turned to whoever is speaking.
    /// </summary>
    internal static class SceneMove
    {
        // Agents we set walking or holding, so we never release a companion who was already following you on their own
        private static readonly HashSet<Agent> Ours = new HashSet<Agent>();
        // Seated people we only turn the head of: they keep their seat and their own routine
        private static readonly Dictionary<Agent, Agent> Seated = new Dictionary<Agent, Agent>();

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
                // A seated one we were only turning the head of: let go of that before they walk
                if (Seated.Remove(a)) { try { a.SetLookAgent(null); } catch { } }
                var g = Group(a);
                if (g != null)
                {
                    if (g.ScriptedBehavior is FollowAgentBehavior && !Ours.Contains(a)) return false;      // already following someone (with you)
                    // Held on the spot by us: end that hold first, so the walk takes over from their normal state
                    if (g.ScriptedBehavior is HoldBehavior && Ours.Contains(a)) { try { g.DisableScriptedBehavior(); a.DisableScriptedMovement(); } catch { } }
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

        /// <summary>
        /// Keeps the agent where it stands, body turned to <paramref name="face"/> (and turning when that changes, see Face).
        /// Someone seated keeps the seat and only turns the head. False if the agent is not ours to hold (a companion
        /// following you, a mounted rider, the player).
        /// </summary>
        public static bool Hold(Agent a, Agent face)
        {
            try
            {
                if (a == null || !a.IsActive() || !a.IsAIControlled || a.MountAgent != null) return false;
                if (a.IsUsingGameObject || a.IsSitting())
                {
                    Seated[a] = face;
                    try { if (face != null && face.IsActive()) a.SetLookAgent(face); } catch { }
                    return true;
                }
                var g = Group(a);
                if (g == null) return false;
                var current = g.ScriptedBehavior;
                if (current != null && !(current is HoldBehavior) && !Ours.Contains(a)) return false;     // someone else's script (following you, a guide...)
                var h = g.GetBehavior<HoldBehavior>() ?? g.AddBehavior<HoldBehavior>();
                if (h == null) return false;
                h.Face = face;
                if (!(current is HoldBehavior)) g.SetScriptedBehavior<HoldBehavior>();
                Ours.Add(a);
                return true;
            }
            catch (Exception ex) { Log.Error("Hold a character in place", ex); return false; }
        }

        /// <summary>A held agent turns to someone else (the new speaker).</summary>
        public static void Face(Agent a, Agent face)
        {
            try
            {
                if (a == null || !a.IsActive() || face == null || face == a) return;
                if (Seated.ContainsKey(a)) { Seated[a] = face; try { a.SetLookAgent(face); } catch { } return; }
                if (!Ours.Contains(a)) return;
                var g = Group(a);
                if (g?.ScriptedBehavior is HoldBehavior h) h.Face = face;
            }
            catch { }
        }

        /// <summary>The agent is being held in place by us (standing or seated).</summary>
        public static bool IsHeld(Agent a)
        {
            if (a == null) return false;
            if (Seated.ContainsKey(a)) return true;
            if (!Ours.Contains(a)) return false;
            return Group(a)?.ScriptedBehavior is HoldBehavior;
        }

        public static void Release(Agent a)
        {
            try
            {
                if (a == null) return;
                if (Seated.Remove(a)) { try { if (a.IsActive()) a.SetLookAgent(null); } catch { } }
                if (!Ours.Remove(a) || !a.IsActive()) return;
                var g = Group(a);
                if (g != null && (g.ScriptedBehavior is FollowAgentBehavior || g.ScriptedBehavior is HoldBehavior)) g.DisableScriptedBehavior();
                a.DisableScriptedMovement();
            }
            catch (Exception ex) { Log.Error("Release a character", ex); }
        }

        /// <summary>Takes charge of someone the game itself set following (brought into the scene behind the player), so they can be released.</summary>
        public static void Adopt(Agent a) { if (a != null) Ours.Add(a); }

        /// <summary>
        /// Lets go of everyone we were walking or holding (their own routine comes back), then forgets them. Agents of a
        /// mission that is already over are skipped by Release.
        /// </summary>
        public static void Forget()
        {
            foreach (var a in new List<Agent>(Seated.Keys)) Release(a);
            foreach (var a in new List<Agent>(Ours)) Release(a);
            Ours.Clear(); Seated.Clear();
        }
    }

    /// <summary>
    /// Scripted behavior that keeps its owner on the spot where it started, turned towards someone. Put in the daily
    /// behavior group as the scripted one, so the routine (wandering, going to a chair) waits until we release it.
    /// </summary>
    public class HoldBehavior : AgentBehavior
    {
        public Agent Face;
        private WorldPosition _spot;
        private bool _hasSpot;
        private float _since = 1f, _lastAngle = float.NaN;
        private Agent _lookingAt;

        public HoldBehavior(AgentBehaviorGroup behaviorGroup) : base(behaviorGroup) { }

        protected override void OnActivate()
        {
            try
            {
                _spot = OwnerAgent.GetWorldPosition();
                _hasSpot = true;
                _lastAngle = float.NaN; _since = 1f; _lookingAt = null;
                Navigator?.ClearTarget();
            }
            catch { _hasSpot = false; }
        }

        protected override void OnDeactivate()
        {
            try
            {
                _hasSpot = false; _lookingAt = null; Face = null;
                OwnerAgent.DisableScriptedMovement();
                OwnerAgent.SetLookAgent(null);
            }
            catch { }
        }

        public override void Tick(float dt, bool isSimulation)
        {
            try
            {
                var me = OwnerAgent;
                if (me == null || !me.IsActive() || !_hasSpot) return;
                var face = Face != null && Face.IsActive() && Face != me ? Face : null;
                if (face != _lookingAt) { _lookingAt = face; try { me.SetLookAgent(face); } catch { } }
                _since += dt;
                if (_since < 0.4f) return;
                _since = 0f;
                float angle = me.LookDirectionAsAngle;
                if (face != null)
                {
                    var d = (face.Position - me.Position).AsVec2;
                    if (d.LengthSquared > 0.04f) angle = d.RotationInRadians;
                }
                // Only re-issue when the facing really changes, so they don't shuffle on the spot
                if (!float.IsNaN(_lastAngle) && Math.Abs(MBMath.WrapAngle(angle - _lastAngle)) < 0.26f) return;
                _lastAngle = angle;
                var spot = _spot;
                me.SetScriptedPositionAndDirection(ref spot, angle, false, Agent.AIScriptedFrameFlags.DoNotRun);
            }
            catch { }
        }

        public override float GetAvailability(bool isSimulation) => 0f;      // never picked by the routine on its own

        public override string GetDebugInfo() => "AI Legacy: hold " + (Face != null ? "facing " + Face.Name : "in place");
    }
}
