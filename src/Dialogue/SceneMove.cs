using System;
using System.Collections.Generic;
using AILegacy.Core;
using SandBox;
using SandBox.Missions.AgentBehaviors;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Mover a un personaje por la escena hasta otro (también entre plantas): se usa el mismo comportamiento con el que
    /// el juego hace que tus compañeros te sigan, que ya sabe buscar camino, subir escaleras y cruzar puertas.
    /// </summary>
    internal static class SceneMove
    {
        // A quién hemos puesto a andar nosotros (para no soltar a un compañero que ya te seguía por su cuenta)
        private static readonly HashSet<Agent> Ours = new HashSet<Agent>();

        private static DailyBehaviorGroup Group(Agent a)
        {
            try { return a.GetComponent<CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<DailyBehaviorGroup>(); }
            catch { return null; }
        }

        /// <summary>Ese personaje deja lo que hace y camina hasta el otro. Devuelve false si no se le puede mover.</summary>
        public static bool GoTo(Agent a, Agent target)
        {
            try
            {
                if (a == null || target == null || a == target || !a.IsActive() || !target.IsActive() || !a.IsAIControlled || a.MountAgent != null) return false;
                var g = Group(a);
                if (g != null)
                {
                    if (g.ScriptedBehavior is FollowAgentBehavior && !Ours.Contains(a)) return false;      // ya sigue a alguien (va contigo)
                    try { if (a.IsUsingGameObject) a.StopUsingGameObject(); } catch { }
                    var f = g.GetBehavior<FollowAgentBehavior>() ?? g.AddBehavior<FollowAgentBehavior>();
                    g.SetScriptedBehavior<FollowAgentBehavior>();
                    f.SetTargetAgent(target);
                    Ours.Add(a);
                    return true;
                }
                // Sin el navegador de la campaña (raro): se le manda al punto, sin más
                try { if (a.IsUsingGameObject) a.StopUsingGameObject(); } catch { }
                var wp = target.GetWorldPosition();
                a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun);
                Ours.Add(a);
                return true;
            }
            catch (Exception ex) { Log.Error("Mover a un personaje por la escena", ex); return false; }
        }

        /// <summary>Vuelve a su vida normal.</summary>
        public static void Release(Agent a)
        {
            try
            {
                if (a == null || !Ours.Remove(a) || !a.IsActive()) return;
                var g = Group(a);
                if (g != null && g.ScriptedBehavior is FollowAgentBehavior) g.DisableScriptedBehavior();
                a.DisableScriptedMovement();
            }
            catch (Exception ex) { Log.Error("Soltar a un personaje", ex); }
        }

        public static void Forget() => Ours.Clear();
    }
}
