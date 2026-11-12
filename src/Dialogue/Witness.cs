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
    /// Testigos. Lo que haces a escondidas no lo sabe nadie: para que una agresión tenga consecuencias con tu nombre hace
    /// falta que alguien vivo la haya visto, y para que te señalen por tu nombre hace falta que ese alguien sepa quién eres
    /// (o que seas ya famoso). Si solo te han visto la cara, se busca a «un forastero armado», no a ti.
    /// </summary>
    internal static class Witness
    {
        public class Result
        {
            /// <summary>0 = nadie lo vio · 1 = te vieron, pero no saben quién eres · 2 = te han reconocido por tu nombre.</summary>
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

        /// <summary>Quién ha visto lo que acabas de hacerles a estos (los que sigan vivos también cuentan: te han visto la cara).</summary>
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
                // Famoso: a un noble de renombre lo reconoce cualquiera
                bool famous = Clan.PlayerClan != null && Clan.PlayerClan.Tier >= cfg.WitnessFameTier;
                var knower = famous ? seen[0] : seen.FirstOrDefault(KnowsName);
                r.Level = knower != null ? 2 : 1;
                r.ByWhom = (knower ?? seen[0]).Name ?? "";
                Log.Info($"[testigos] {r.Count} testigo(s); {(r.Level == 2 ? "te reconoce " + r.ByWhom : "nadie sabe tu nombre")}");
            }
            catch (Exception ex) { Log.Error("Testigos", ex); r.Level = 2; r.Count = 1; }      // ante la duda, como antes
            return r;
        }
    }
}
