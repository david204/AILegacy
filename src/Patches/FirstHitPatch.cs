using System;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Dialogue;
using HarmonyLib;
using SandBox.Missions.MissionLogics;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Patches
{
    /// <summary>
    /// The game cancels every blow the player lands on someone who is not an enemy: no damage, and the player is stunned
    /// as for friendly fire, so hitting a townsperson felt like hitting a wall. In a settlement scene, with street
    /// violence allowed and nothing else going on, the first blow at a grown-up bystander lands for real and starts the
    /// fight (Combat.LetPlayerHit decides). Everything else is left to the game.
    /// </summary>
    [HarmonyPatch]
    internal static class FirstHitPatch
    {
        // private bool Mission.CancelsDamageAndBlocksAttackBecauseOfNonEnemyCase(Agent attacker, Agent victim)
        private static MethodBase TargetMethod() =>
            AccessTools.Method(typeof(Mission), "CancelsDamageAndBlocksAttackBecauseOfNonEnemyCase", new[] { typeof(Agent), typeof(Agent) });

        private static bool Prepare()
        {
            bool found = TargetMethod() != null;
            if (!found) Log.Warn("First blow: Mission.CancelsDamageAndBlocksAttackBecauseOfNonEnemyCase not found, blows at bystanders stay cancelled");
            return found;
        }

        private static bool Prefix(Mission __instance, Agent attacker, Agent victim, ref bool __result)
        {
            try
            {
                // Cheapest check first: this runs for every melee collision in every mission
                if (attacker == null || victim == null || attacker != Agent.Main) return true;
                if (!Combat.LetPlayerHit(__instance, attacker, victim)) return true;
                __result = false;      // not cancelled: the blow does its damage
                return false;
            }
            catch { return true; }
        }
    }

    /// <summary>
    /// While a fight is on, the game makes every unarmed bystander who is near the player (15 m) or sees him treat him as
    /// a danger and run, frame after frame. During our fist fight in a tavern the patrons are left out of it: they stay
    /// and watch. Street fights keep the game's behaviour.
    /// </summary>
    [HarmonyPatch(typeof(MissionFightHandler), nameof(MissionFightHandler.GetDangerSources))]
    internal static class TavernCalmPatch
    {
        private static void Postfix(Agent ownerAgent, ref IEnumerable<Agent> __result)
        {
            try { if (ownerAgent != null && Combat.KeepCalm(ownerAgent)) __result = new List<Agent>(); }
            catch { }
        }
    }

    /// <summary>
    /// Added by Combat to each settlement scene. Sees the real melee collisions, including the ones the game cancels,
    /// so a fight starts on the first blow and not on a guess from the swing animation. Also tells the engine that
    /// anyone in a friendly fight is knocked out, never killed, when their health runs out.
    /// </summary>
    internal class FightWatch : MissionLogic, IAgentStateDecider
    {
        public override void OnMeleeHit(Agent attacker, Agent victim, bool isCanceled, AttackCollisionData collisionData)
        {
            try
            {
                if (attacker == null || victim == null || attacker != Agent.Main) return;
                Combat.OnPlayerHit(victim, collisionData);
            }
            catch (Exception ex) { Log.Error("First blow: hit", ex); }
        }

        public override void OnAgentRemoved(Agent affectedAgent, Agent affectorAgent, AgentState agentState, KillingBlow blow)
        {
            try { if (affectedAgent != null && affectedAgent == Agent.Main) Combat.OnMainDown(); }
            catch (Exception ex) { Log.Error("Player down", ex); }
        }

        protected override void OnEndMission()
        {
            try { Combat.OnSceneEnded(); } catch (Exception ex) { Log.Error("Fight scene end", ex); }
            base.OnEndMission();
        }

        public AgentState GetAgentState(Agent affectedAgent, float deathProbability, out bool usedSurgery)
        {
            usedSurgery = false;
            try { return Combat.DecideState(affectedAgent); }      // AgentState.None: the game decides as usual
            catch { return AgentState.None; }
        }
    }
}
