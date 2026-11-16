using System;
using System.Collections.Generic;
using AILegacy.Core;
using AILegacy.Settings;
using AILegacy.World;
using HarmonyLib;
using SandBox.GameComponents;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Patches
{
    /// <summary>
    /// The sick show a green outline in battle, as in AIInfluence 7.0.5: a sick hero, and in a party with sick men a
    /// soldier picked at random with the share of the party that is ill, so about as many outlines as sick men. Each
    /// agent is decided once, the first time the game works out its stats with its body already built. Only in
    /// battles (field battles, sieges, hideouts), never in towns, villages or the arena.
    /// </summary>
    [HarmonyPatch(typeof(SandboxAgentStatCalculateModel), nameof(SandboxAgentStatCalculateModel.UpdateAgentStats))]
    internal static class DiseaseOutlinePatch
    {
        private const uint Green = 0xFF60C040u;
        private static readonly Random Rng = new Random();
        private static readonly HashSet<Agent> Decided = new HashSet<Agent>();
        private static Mission _mission;
        private static bool _logged;

        private static void Postfix(Agent agent)
        {
            try
            {
                if (agent == null || !agent.IsHuman || !Diseases.AnySick || !S.Get.DiseaseSickOutline || Campaign.Current == null) return;
                var m = Mission.Current;
                if (m == null || (m.Mode != MissionMode.Battle && m.Mode != MissionMode.Deployment) || m.CombatType != Mission.MissionCombatType.Combat) return;
                if (m != _mission) { _mission = m; Decided.Clear(); }
                if (Decided.Contains(agent)) return;
                var visuals = agent.AgentVisuals;
                if (visuals == null) return;          // not built yet: decided on a later update
                Decided.Add(agent);
                if (!Sick(agent)) return;
                visuals.SetContourColor(Green, true);
            }
            catch (Exception ex)
            {
                if (_logged) return;
                _logged = true;
                Log.Error("Green outline on the sick", ex);
            }
        }

        private static bool Sick(Agent a)
        {
            if (!(a.Character is CharacterObject ch)) return false;
            if (ch.IsHero) return ch.HeroObject != null && Diseases.OfHero(ch.HeroObject) != null;
            var party = (a.Origin?.BattleCombatant as PartyBase)?.MobileParty;
            var list = Diseases.OfTroops(party);
            if (list == null || party == null) return false;
            int total = party.MemberRoster.TotalRegulars, sick = 0;
            foreach (var s in list) if (s != null) sick += s.Count;
            if (total <= 0 || sick <= 0) return false;
            return Rng.NextDouble() < Math.Min(1.0, sick / (double)total);
        }
    }
}
