using System;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using SandBox.GameComponents;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Patches
{
    /// <summary>
    /// What being sick costs, added to the game's own numbers so it shows in their breakdown with the disease as the
    /// reason: march speed and morale of a party, the simulated blows of a battle fought without the player, and the
    /// speed, weapon handling, damage and fighting skills of an agent in a scene.
    /// </summary>
    internal static class DiseaseEffects
    {
        private static bool _logged;

        public static void Report(string where, Exception ex)
        {
            // these run many times a second: one line in the log is enough
            if (_logged) return;
            _logged = true;
            Log.Error("Efectos de la enfermedad (" + where + ")", ex);
        }

        public static TextObject Reason(string name) => new TextObject("{=!}" + Diseases.HeroLabel(name));

        /// <summary>Combat multiplier of whoever this agent is: a sick hero, or a soldier of a party with sick men.</summary>
        public static float Of(Agent agent, out float skill)
        {
            skill = 0f;
            if (!(agent?.Character is CharacterObject ch)) return 1f;
            if (ch.IsHero) return Diseases.HeroCombat(ch.HeroObject, out float m, out skill, out _) ? m : 1f;
            var party = (agent.Origin?.BattleCombatant as PartyBase)?.MobileParty;
            return party != null && Diseases.TroopCombat(party, out float t, out _) ? t : 1f;
        }

        public static float Of(CharacterObject troop, PartyBase party, out string name)
        {
            name = null;
            if (troop == null) return 1f;
            if (troop.IsHero) return Diseases.HeroCombat(troop.HeroObject, out float m, out _, out name) ? m : 1f;
            return party?.MobileParty != null && Diseases.TroopCombat(party.MobileParty, out float t, out name) ? t : 1f;
        }
    }

    [HarmonyPatch(typeof(DefaultPartySpeedCalculatingModel), nameof(DefaultPartySpeedCalculatingModel.CalculateFinalSpeed))]
    internal static class DiseaseMapSpeedPatch
    {
        private static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            try
            {
                if (!Diseases.AnySick || !Diseases.PartyMap(mobileParty, out float speed, out _, out string name) || speed > 0.999f) return;
                __result.AddFactor(speed - 1f, DiseaseEffects.Reason(name));
            }
            catch (Exception ex) { DiseaseEffects.Report("velocidad", ex); }
        }
    }

    [HarmonyPatch(typeof(DefaultPartyMoraleModel), nameof(DefaultPartyMoraleModel.GetEffectivePartyMorale))]
    internal static class DiseaseMoralePatch
    {
        private static void Postfix(MobileParty mobileParty, ref ExplainedNumber __result)
        {
            try
            {
                if (!Diseases.AnySick || !Diseases.PartyMap(mobileParty, out _, out float morale, out string name) || morale > -0.05f) return;
                __result.Add(morale, DiseaseEffects.Reason(name));
            }
            catch (Exception ex) { DiseaseEffects.Report("moral", ex); }
        }
    }

    /// <summary>Battles resolved on the map: a sick striker hits softer, a sick hero who is struck takes it harder.</summary>
    [HarmonyPatch]
    internal static class DiseaseSimulatedHitPatch
    {
        // The model has two SimulateHit; this is the one between two troops (the other is ship against ship).
        private static MethodBase TargetMethod() =>
            AccessTools.GetDeclaredMethods(typeof(DefaultCombatSimulationModel))
                .FirstOrDefault(m => m.Name == "SimulateHit" && m.GetParameters().Length > 1 && m.GetParameters()[0].ParameterType == typeof(CharacterObject));

        private static void Postfix(CharacterObject strikerTroop, CharacterObject struckTroop, PartyBase strikerParty, PartyBase struckParty, ref ExplainedNumber __result)
        {
            try
            {
                if (!Diseases.AnySick) return;
                float hit = DiseaseEffects.Of(strikerTroop, strikerParty, out string a);
                if (hit < 0.999f) __result.AddFactor(hit - 1f, DiseaseEffects.Reason(a));
                if (struckTroop != null && struckTroop.IsHero)
                {
                    float guard = DiseaseEffects.Of(struckTroop, struckParty, out string b);
                    if (guard < 0.999f) __result.AddFactor((1f - guard) * 0.5f, DiseaseEffects.Reason(b));
                }
            }
            catch (Exception ex) { DiseaseEffects.Report("combate simulado", ex); }
        }
    }

    [HarmonyPatch(typeof(SandboxAgentStatCalculateModel), nameof(SandboxAgentStatCalculateModel.UpdateAgentStats))]
    internal static class DiseaseAgentStatsPatch
    {
        private static void Postfix(Agent agent, AgentDrivenProperties agentDrivenProperties)
        {
            try
            {
                if (!Diseases.AnySick || agent == null || !agent.IsHuman || agentDrivenProperties == null) return;
                float m = DiseaseEffects.Of(agent, out _);
                if (m > 0.999f) return;
                agentDrivenProperties.MaxSpeedMultiplier *= 1f - (1f - m) * 0.5f;      // legs go before the sword arm does
                agentDrivenProperties.SwingSpeedMultiplier *= m;
                agentDrivenProperties.ThrustOrRangedReadySpeedMultiplier *= m;
            }
            catch (Exception ex) { DiseaseEffects.Report("agente", ex); }
        }
    }

    [HarmonyPatch(typeof(SandboxAgentStatCalculateModel), nameof(SandboxAgentStatCalculateModel.GetWeaponDamageMultiplier))]
    internal static class DiseaseAgentDamagePatch
    {
        private static void Postfix(Agent agent, ref float __result)
        {
            try
            {
                if (!Diseases.AnySick || agent == null || !agent.IsHuman) return;
                float m = DiseaseEffects.Of(agent, out _);
                if (m < 0.999f) __result *= m;
            }
            catch (Exception ex) { DiseaseEffects.Report("daño", ex); }
        }
    }

    [HarmonyPatch(typeof(SandboxAgentStatCalculateModel), nameof(SandboxAgentStatCalculateModel.GetEffectiveSkill))]
    internal static class DiseaseAgentSkillPatch
    {
        private static bool Fighting(SkillObject s) =>
            s == DefaultSkills.OneHanded || s == DefaultSkills.TwoHanded || s == DefaultSkills.Polearm || s == DefaultSkills.Bow
            || s == DefaultSkills.Crossbow || s == DefaultSkills.Throwing || s == DefaultSkills.Riding || s == DefaultSkills.Athletics;

        private static void Postfix(Agent agent, SkillObject skill, ref int __result)
        {
            try
            {
                if (!Diseases.AnySick || agent == null || !agent.IsHuman || __result <= 0 || !Fighting(skill)) return;
                DiseaseEffects.Of(agent, out float penalty);
                if (penalty < -0.5f) __result = Math.Max(0, __result + (int)penalty);
            }
            catch (Exception ex) { DiseaseEffects.Report("habilidad", ex); }
        }
    }
}
