using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// What nobody sees, nobody knows. An assault only has consequences under your name if someone alive saw it and
    /// knows who you are (or you're already famous). If they only saw your face, they look for "an armed stranger".
    /// </summary>
    internal static class Witness
    {
        public class Result
        {
            /// <summary>0 = nobody saw it, 1 = seen but not identified, 2 = recognized by name.</summary>
            public int Level;
            public int Count;
            public string ByWhom = "";
        }

        private static bool Alive(Agent a) => a != null && (a.IsActive() || a.State == AgentState.Unconscious);

        private static bool Mine(Agent a)
        {
            var h = (a.Character as CharacterObject)?.HeroObject;
            return h != null && (h == Hero.MainHero || h.Clan == Clan.PlayerClan || h.IsPlayerCompanion);
        }

        private static bool KnowsName(Agent a)
        {
            try
            {
                var c = a.Character as CharacterObject;
                if (c == null) return false;
                var sp = c.IsHero && c.HeroObject != null ? Speaker.FromHero(c.HeroObject) : Speaker.FromResident(c, a);
                return ContextBuilder.PlayerNameFor(sp) != null;
            }
            catch { return false; }
        }

        /// <summary>Who saw what the player just did to these victims. Victims still alive count too, they saw your face.</summary>
        public static Result Assess(IEnumerable<Agent> victims)
        {
            var r = new Result();
            try
            {
                var m = Mission.Current; var me = Agent.Main;
                if (m == null || me == null) return r;
                var cfg = S.Get;
                var vs = (victims ?? Enumerable.Empty<Agent>()).Where(v => v != null).ToList();
                var seen = new List<Agent>(vs.Where(Alive));
                foreach (var a in m.Agents)
                {
                    if (a == null || a == me || !a.IsHuman || !a.IsActive() || vs.Contains(a) || Mine(a)) continue;
                    if (!(a.Character is CharacterObject c) || c.Age < 7f) continue;
                    if (a.Position.Distance(me.Position) > cfg.WitnessRadius || Math.Abs(a.Position.z - me.Position.z) > 3.5f) continue;
                    seen.Add(a);
                }
                r.Count = seen.Count;
                if (r.Count == 0) return r;
                // Everyone recognizes a famous noble
                bool famous = Clan.PlayerClan != null && Clan.PlayerClan.Tier >= cfg.WitnessFameTier;
                var knower = famous ? seen[0] : seen.FirstOrDefault(KnowsName);
                r.Level = knower != null ? 2 : 1;
                r.ByWhom = (knower ?? seen[0]).Name ?? "";
                Log.Info($"[witnesses] {r.Count} witness(es); {(r.Level == 2 ? "recognizes you " + r.ByWhom : "nobody knows your name")}");
            }
            catch (Exception ex) { Log.Error("Witnesses", ex); r.Level = 2; r.Count = 1; }      // on error, assume they were recognized
            return r;
        }

        // Those who see a handover: within this many metres of the player, the nearest first, as AIInfluence does
        private const float HandoverRadius = 12f;
        private const int HandoverMax = 12;

        /// <summary>
        /// Something passed from hand to hand in a talk in a scene (gold, goods, soldiers, prisoners): everyone standing
        /// close enough to see it, heroes and townsfolk alike, keeps it in their history. The two who made the deal
        /// already have it. <paramref name="what"/> is in English, in the third person, for the AI.
        /// </summary>
        public static void SawHandover(Speaker other, string what)
        {
            try
            {
                var m = Mission.Current; var me = Agent.Main;
                if (m == null || me == null || string.IsNullOrWhiteSpace(what)) return;
                var near = new List<(Agent a, float d)>();
                foreach (var a in m.Agents)
                {
                    if (a == null || a == me || !a.IsHuman || !a.IsActive() || !(a.Character is CharacterObject c) || c.Age < 7f) continue;
                    float d = a.Position.Distance(me.Position);
                    if (d > HandoverRadius || Math.Abs(a.Position.z - me.Position.z) > 3.5f) continue;
                    near.Add((a, d));
                }
                if (near.Count == 0) return;
                double day = CampaignTime.Now.ToDays;
                var names = new List<string>();
                var seen = new HashSet<string>();
                foreach (var (a, d) in near.OrderBy(x => x.d))
                {
                    if (names.Count >= HandoverMax) break;
                    Ambient.Describe(a, out var sp);
                    if (sp?.Memory == null || (other != null && sp.Key == other.Key) || !seen.Add(sp.Key)) continue;
                    sp.Memory.Turns.Add(new Memory.ChatTurn { Role = "player", Day = day, Text = $"[You saw it, standing a few steps away: {what}]" });
                    names.Add($"{sp.Name} {d:0.0}m");
                }
                if (names.Count > 0) Log.Info($"[witnesses] handover seen by {string.Join(", ", names)} — {what}");
            }
            catch (Exception ex) { Log.Error("Witnesses of a handover", ex); }
        }
    }
}
