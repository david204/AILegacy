using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace AILegacy.World
{
    /// <summary>Algo que le ha pasado a alguien (o a un reino entero) y le pesa en el ánimo unos días.</summary>
    public class MoodEvent
    {
        /// <summary>Id del héroe, o «f:» + id de la facción cuando afecta a todos los suyos (una guerra recién declarada).</summary>
        [JsonProperty("who")] public string Who;
        [JsonProperty("kind")] public string Kind;
        /// <summary>Qué ha pasado, en inglés y en segunda persona, para el prompt.</summary>
        [JsonProperty("text")] public string Text;
        [JsonProperty("sign")] public int Sign;            // -1 pesa, +1 anima, 0 inquieta
        [JsonProperty("strength")] public int Strength;    // 1..3
        [JsonProperty("day")] public double Day;
        /// <summary>Hasta cuándo se nota.</summary>
        [JsonProperty("until")] public double Until;
        /// <summary>Hasta cuándo no puede volver a contar otra del mismo tipo (para que diez batallas seguidas no sean diez alegrías).</summary>
        [JsonProperty("cool")] public double CoolUntil;
        /// <summary>Con quién tiene que ver (el muerto, el vencedor…), para no repetir.</summary>
        [JsonProperty("about")] public string About;
    }

    /// <summary>
    /// Ánimo hecho de varias causas a la vez. Unas llegan con los sucesos del mundo (una batalla ganada o perdida, un torneo,
    /// la liberación, una guerra recién declarada, un parto) y duran unos días; otras se leen en vivo mientras duren (estar
    /// preso, herido, con la partida deshecha, un duelo por un familiar o un amigo muerto, un familiar cautivo, el embarazo
    /// y el ciclo). Cada una lleva signo, fuerza y antigüedad, y al prompt llegan como mucho cinco. Lo que la IA dijo sentir
    /// al acabar la última charla cuenta como una causa más: no fija el ánimo ella sola.
    /// </summary>
    internal static class Mood
    {
        private const int MaxCauses = 5;

        private static readonly Tpl THeader = new Tpl("mood/header.txt",
            "## How you feel these days\nSeveral things weigh on you at once. Let them colour your tone, your patience and what you are willing to do, the strongest most of all — " +
            "but do not list them or announce them: a person shows a mood, they do not report it. They are yours; what you think of the person in front of you is a separate matter.");

        private static readonly Tpl TCycle = new Tpl("mood/cycle.txt",
            "Your monthly cycle: day {day} of {length}. {phase} How much it shows in you: {strength}. It is private: you would speak of it only to someone very close, and never as an excuse.");

        public static void Touch() { var _ = THeader.Key; }

        private static WorldState St => WorldSim.State;
        private static double Now => CampaignTime.Now.ToDays;
        private static double Scale => Math.Max(10, S.Get.MoodDurationPercent) / 100.0;

        // ============================================================ sucesos que dejan huella

        private static void Add(string who, string kind, int sign, int strength, double days, double coolDays, string text, string about = null)
        {
            try
            {
                if (!S.Get.EnableMoodCauses || string.IsNullOrEmpty(who) || St == null) return;
                double now = Now;
                var list = St.MoodEvents;
                // En pausa: ya hubo una del mismo tipo hace poco
                if (list.Any(e => e.Who == who && e.Kind == kind && (e.CoolUntil > now || (about != null && e.About == about && e.Until > now)))) return;
                list.Add(new MoodEvent { Who = who, Kind = kind, Sign = sign, Strength = strength, Day = now, Until = now + days * Scale, CoolUntil = now + coolDays * Scale, Text = text, About = about });
                if (list.Count > 600) list.RemoveRange(0, list.Count - 600);
            }
            catch (Exception ex) { Log.Error("Ánimo: apuntar causa", ex); }
        }

        public static void OnDailyTick()
        {
            try
            {
                if (St == null) return;
                double now = Now;
                St.MoodEvents.RemoveAll(e => e.Until < now && e.CoolUntil < now);
                St.RecentKills.RemoveAll(k => now - k.Day > 40);
            }
            catch { }
        }

        /// <summary>Batalla: quien manda una partida la gana o la pierde (3 días la victoria, 4 la derrota; pausa de 14).</summary>
        public static void OnMapEventEnded(MapEvent e)
        {
            try
            {
                if (e == null || !e.HasWinner || !S.Get.EnableMoodCauses) return;
                var win = e.WinningSide == BattleSideEnum.Attacker ? e.AttackerSide : e.DefenderSide;
                var lose = e.WinningSide == BattleSideEnum.Attacker ? e.DefenderSide : e.AttackerSide;
                string where = "";
                try { var st = e.MapEventSettlement; if (st != null) where = $" at {st.Name}"; } catch { }
                string winner = win?.LeaderParty?.LeaderHero?.Name?.ToString() ?? win?.LeaderParty?.Name?.ToString();
                string loser = lose?.LeaderParty?.LeaderHero?.Name?.ToString() ?? lose?.LeaderParty?.Name?.ToString();
                foreach (var h in Leaders(win))
                    Add(h.StringId, "battle_won", +1, 2, 3, 14, $"You won a battle{where}" + (loser != null ? $" against {loser}" : "") + ": you are proud of it, and a little reckless.");
                foreach (var h in Leaders(lose))
                    Add(h.StringId, "battle_lost", -1, 2, 4, 14, $"You lost a battle{where}" + (winner != null ? $" to {winner}" : "") + ": the defeat stings, and you are short of patience.");
            }
            catch (Exception ex) { Log.Error("Ánimo: batalla", ex); }
        }

        private static IEnumerable<Hero> Leaders(MapEventSide side)
        {
            var list = new List<Hero>();
            try
            {
                if (side == null) return list;
                foreach (var p in side.Parties)
                {
                    var h = p?.Party?.LeaderHero;
                    if (h != null && h != Hero.MainHero && h.IsAlive && !list.Contains(h)) list.Add(h);
                }
            }
            catch { }
            return list;
        }

        /// <summary>Torneo: el ganador lo saborea 5 días (pausa de 30).</summary>
        public static void OnTournamentFinished(CharacterObject winner, TaleWorlds.Library.MBReadOnlyList<CharacterObject> participants, Town town, ItemObject prize)
        {
            try
            {
                var h = winner?.HeroObject;
                if (h == null || h == Hero.MainHero) return;
                Add(h.StringId, "tournament_won", +1, 2, 5, 30, $"You won the tournament of {town?.Name}: the cheers are still in your ears.");
            }
            catch (Exception ex) { Log.Error("Ánimo: torneo", ex); }
        }

        /// <summary>Salir del cautiverio: alivio durante 4 días.</summary>
        public static void OnPrisonerReleased(Hero h, PartyBase captor, IFaction faction, EndCaptivityDetail detail, bool show)
        {
            try
            {
                if (h == null || h == Hero.MainHero || !h.IsAlive) return;
                Add(h.StringId, "released", +1, 2, 4, 10, "You were a prisoner until a few days ago and you are free again: relief, and a grudge against whoever held you.");
            }
            catch (Exception ex) { Log.Error("Ánimo: liberación", ex); }
        }

        /// <summary>Una guerra recién declarada tensa a los dos bandos durante 5 días.</summary>
        public static void OnWarDeclared(IFaction a, IFaction b, DeclareWarAction.DeclareWarDetail detail)
        {
            try
            {
                if (a == null || b == null || !a.IsKingdomFaction || !b.IsKingdomFaction) return;
                Add("f:" + a.StringId, "war_declared", 0, 1, 5, 0, $"War has just been declared between your realm and {b.Name}: everyone is on edge.", b.StringId);
                Add("f:" + b.StringId, "war_declared", 0, 1, 5, 0, $"War has just been declared between your realm and {a.Name}: everyone is on edge.", a.StringId);
            }
            catch (Exception ex) { Log.Error("Ánimo: guerra", ex); }
        }

        /// <summary>Un parto: alegría (o pena, si nació muerto) durante 10 días. También marca que no hay ciclo tras el parto.</summary>
        public static void OnGivenBirth(Hero mother, List<Hero> children, int stillborn)
        {
            try
            {
                if (mother == null) return;
                bool alive = children != null && children.Count > 0;
                Add(mother.StringId, "gave_birth", alive ? +1 : -1, 3, 10, 0,
                    alive ? "You gave birth days ago: you are worn out and fiercely happy." : "Your child was born dead days ago: you are grieving and exhausted.");
                var father = mother.Spouse;
                if (father != null && father != Hero.MainHero)
                    Add(father.StringId, "became_parent", alive ? +1 : -1, 2, 8, 0, alive ? $"{mother.Name} has just borne you a child: you are proud and glad." : $"The child {mother.Name} bore you was born dead: you are grieving.");
            }
            catch (Exception ex) { Log.Error("Ánimo: parto", ex); }
        }

        /// <summary>Quién mató a quién, para que el asesino no llore a su víctima.</summary>
        public static void OnHeroKilled(Hero victim, Hero killer)
        {
            try
            {
                if (victim == null || killer == null || St == null) return;
                St.RecentKills.Add(new KillRecord { Victim = victim.StringId, Killer = killer.StringId, Day = Now });
            }
            catch { }
        }

        // ============================================================ lo que siente ahora

        private class Cause { public string Text; public int Sign; public int Strength; public double Age; }

        private static List<Cause> CausesFor(Speaker s)
        {
            var list = new List<Cause>();
            var cfg = S.Get;
            double now = Now;
            if (s.IsHero && cfg.EnableMoodCauses)
            {
                var h = s.Hero;
                void Live(string text, int sign, int strength) => list.Add(new Cause { Text = text, Sign = sign, Strength = strength, Age = 0 });

                // --- estados que duran mientras duren
                try
                {
                    if (h.IsPrisoner)
                    {
                        string by = h.PartyBelongedToAsPrisoner?.LeaderHero?.Name?.ToString() ?? h.PartyBelongedToAsPrisoner?.Name?.ToString();
                        Live("You are a prisoner" + (by != null ? $" of {by}" : "") + ": humiliated, watchful, thinking of how to get out.", -1, 3);
                    }
                    if (h.IsWounded) Live("You are wounded and in pain: short-tempered and tired.", -1, 2);
                    if (h.IsPregnant) Live("You are with child: tired, careful of yourself, and thinking of what is coming.", 0, 2);
                    var p = h.PartyBelongedTo;
                    if (p != null && p.LeaderHero == h && !h.IsPrisoner)
                    {
                        int limit = p.Party.PartySizeLimit;
                        if (limit > 20 && p.MemberRoster.TotalManCount * 100 < limit * 35) Live("Your party is a shadow of what it should be: too few men, and you feel exposed.", -1, 1);
                        if (p.MemberRoster.TotalManCount > 10 && p.MemberRoster.TotalWounded * 100 > p.MemberRoster.TotalManCount * 30) Live("Many of your men are wounded: you are worried for them.", -1, 1);
                    }
                    var home = h.CurrentSettlement;
                    if (home != null && home.IsUnderSiege) Live($"{home.Name} is under siege with you inside: fear and anger, and little sleep.", -1, 3);
                    foreach (var kin in Family(h).Where(k => k.IsAlive && k.IsPrisoner).Take(2))
                        Live($"Your {Kin(h, kin)} {kin.Name} is held prisoner: you worry and want them back.", -1, 2);
                }
                catch { }

                // --- duelo: solo por los suyos y por sus amigos; el enemigo muerto se celebra en silencio; quien mató no llora
                try
                {
                    var kinOf = Family(h).ToList();
                    foreach (var d in Hero.DeadOrDisabledHeroes)
                    {
                        if (d == null || !d.IsDead || d == h) continue;
                        double ago = now - d.DeathDay.ToDays;
                        if (ago < 0 || ago > 14 * Scale) continue;
                        bool killedByMe = St != null && St.RecentKills.Any(k => k.Victim == d.StringId && k.Killer == h.StringId);
                        if (killedByMe) continue;
                        bool family = kinOf.Contains(d);
                        if (family)
                            list.Add(new Cause { Text = $"Your {Kin(h, d)} {d.Name} died {Ago(ago)}: you are in mourning.", Sign = -1, Strength = 3, Age = ago });
                        else if (ago <= 10 * Scale)
                        {
                            int rel = CharacterRelationManager.GetHeroRelation(h, d);
                            if (rel >= 40) list.Add(new Cause { Text = $"Your friend {d.Name} died {Ago(ago)}: you grieve for them.", Sign = -1, Strength = 2, Age = ago });
                            else if (rel <= -40 && ago <= 5 * Scale) list.Add(new Cause { Text = $"Your enemy {d.Name} died {Ago(ago)}: a grim satisfaction you keep mostly to yourself.", Sign = +1, Strength = 1, Age = ago });
                        }
                    }
                }
                catch { }

                // --- sucesos apuntados (los suyos y los de su reino)
                try
                {
                    if (St != null)
                    {
                        string fac = h.MapFaction != null ? "f:" + h.MapFaction.StringId : null;
                        foreach (var e in St.MoodEvents)
                            if (e.Until > now && (e.Who == h.StringId || (fac != null && e.Who == fac)))
                                list.Add(new Cause { Text = e.Text, Sign = e.Sign, Strength = e.Strength, Age = now - e.Day });
                    }
                }
                catch { }
            }

            // --- el ciclo, una causa más
            try { var c = CycleCause(s); if (c != null) list.Add(c); } catch { }

            // --- cómo le dejó la última charla (lo que dijo sentir): cuenta, pero no manda
            var mem = s.Memory;
            if (!string.IsNullOrWhiteSpace(mem.Mood) && mem.MoodDay >= 0 && now - mem.MoodDay <= 3)
                list.Add(new Cause
                {
                    Text = $"After your last talk with them you were left feeling {mem.Mood}" + (string.IsNullOrWhiteSpace(mem.MoodReason) ? "." : $" ({mem.MoodReason})."),
                    Sign = 0, Strength = 1, Age = now - mem.MoodDay,
                });
            return list;
        }

        /// <summary>Su ánimo, para el prompt. Si no hay nada que le pese, no se escribe nada.</summary>
        public static void AppendTo(StringBuilder sys, Speaker s)
        {
            try
            {
                var list = CausesFor(s).OrderByDescending(c => c.Strength).ThenBy(c => c.Age).Take(MaxCauses).ToList();
                if (list.Count == 0) return;
                // Con una sola causa y sin el sistema de causas, basta la línea de siempre
                if (!S.Get.EnableMoodCauses && list.Count == 1) { sys.AppendLine("Your mood lately: " + list[0].Text); return; }
                sys.AppendLine();
                sys.AppendLine(THeader.Text);
                foreach (var c in list)
                    sys.AppendLine($"- ({(c.Sign > 0 ? "lifts you" : c.Sign < 0 ? "weighs on you" : "unsettles you")}, {(c.Strength >= 3 ? "strongly" : c.Strength == 2 ? "clearly" : "a little")}) {c.Text}");
            }
            catch (Exception ex) { Log.Error("Ánimo: texto", ex); }
        }

        /// <summary>Resumen corto para la interfaz y el editor (en inglés: son las mismas causas que ve la IA).</summary>
        public static List<string> Describe(Speaker s)
        {
            try { return CausesFor(s).OrderByDescending(c => c.Strength).ThenBy(c => c.Age).Take(MaxCauses).Select(c => (c.Sign > 0 ? "+ " : c.Sign < 0 ? "− " : "· ") + c.Text).ToList(); }
            catch { return new List<string>(); }
        }

        private static IEnumerable<Hero> Family(Hero h)
        {
            var set = new List<Hero>();
            void A(Hero x) { if (x != null && x != h && !set.Contains(x)) set.Add(x); }
            try
            {
                A(h.Spouse); A(h.Father); A(h.Mother);
                foreach (var c in h.Children) A(c);
                foreach (var b in h.Siblings) A(b);
                foreach (var x in h.ExSpouses) if (x != null && x.IsDead) A(x);      // el cónyuge muerto pasa a esta lista
            }
            catch { }
            return set;
        }

        private static string Kin(Hero h, Hero k)
        {
            try
            {
                if (h.Spouse == k || h.ExSpouses.Contains(k)) return k.IsFemale ? "wife" : "husband";
                if (h.Father == k) return "father";
                if (h.Mother == k) return "mother";
                if (h.Children.Contains(k)) return k.IsFemale ? "daughter" : "son";
                return k.IsFemale ? "sister" : "brother";
            }
            catch { return "kin"; }
        }

        private static string Ago(double days) => days < 1 ? "today" : days < 2 ? "yesterday" : $"{(int)days} days ago";

        // ============================================================ ciclo
        // Mismo modelo que AIInfluence 7.0.1 (cinco fases; cada mujer tiene su duración, sus días de regla y cuánto le
        // afecta, y salen de ella misma, sin guardar nada), escrito aquí de cero.

        public enum CyclePhase { Menstruation, Follicular, Fertile, Luteal, Premenstrual }

        public class CycleState
        {
            public CyclePhase Phase;
            public int Length;            // días de su ciclo (la media de los ajustes, ±2)
            public int Day;               // 1..Length
            public int PeriodLength;      // días de regla
            public int DaysUntilPeriod;
            public int Severity;          // 0 casi no lo nota · 1 lo nota · 2 lo lleva mal
            public float Fertility;       // por cuánto se multiplica la probabilidad de embarazo
        }

        /// <summary>El ciclo de una mujer en un día dado. Depende solo de quién es y del día: no se guarda nada.</summary>
        public static CycleState Compute(string id, double nowDays, int baseLength)
        {
            uint h = Stable(id ?? "");
            int length = Math.Max(8, baseLength + (int)(h % 5u) - 2);
            int offset = (int)(h / 5u % (uint)length);
            int day = (int)((((long)Math.Floor(nowDays) + offset) % length + length) % length);       // 0..length-1
            int period = Math.Max(2, (int)Math.Round(length * 0.15)) + (int)(h / 100000u % 2u);
            int pms = Math.Max(1, (int)Math.Round(length * 0.14));
            int ovulation = length / 2;
            int window = Math.Max(1, (int)Math.Round(length * 0.11));
            uint roll = h / 7919u % 100u;
            var st = new CycleState
            {
                Length = length, Day = day + 1, PeriodLength = period, DaysUntilPeriod = length - day,
                Severity = roll < 20u ? 0 : roll < 60u ? 1 : 2,
            };
            if (day < period) { st.Phase = CyclePhase.Menstruation; st.Fertility = 0.05f; }
            else if (day < ovulation - window) { st.Phase = CyclePhase.Follicular; st.Fertility = 0.5f; }
            else if (day <= ovulation + 1) { st.Phase = CyclePhase.Fertile; st.Fertility = day == ovulation || day == ovulation - 1 ? 4.5f : 3f; }
            else if (day < length - pms) { st.Phase = CyclePhase.Luteal; st.Fertility = 0.3f; }
            else { st.Phase = CyclePhase.Premenstrual; st.Fertility = 0.1f; }
            return st;
        }

        /// <summary>Su ciclo hoy, o null si no tiene: fuera de edad, embarazada, recién parida o apagado en los ajustes.</summary>
        public static CycleState Cycle(string key, bool female, float age, Hero hero)
        {
            try
            {
                if (!S.Get.EnableFemaleCycle || key == null || !female || age < 13f || age >= 47f) return null;
                if (hero != null)
                {
                    if (hero.IsPregnant || !hero.IsAlive) return null;
                    // Tras el parto no hay ciclo durante un tiempo
                    foreach (var c in hero.Children)
                        if (c != null && c.Mother == hero && c.BirthDay.ElapsedDaysUntilNow < 12f) return null;
                }
                return Compute(key, Now, Math.Max(10, Math.Min(40, S.Get.FemaleCycleDays)));
            }
            catch { return null; }
        }

        public static CycleState Cycle(Speaker s) => s == null ? null : Cycle(s.Key, s.IsFemale, s.Age, s.Hero);

        private static Cause CycleCause(Speaker s)
        {
            var c = Cycle(s);
            if (c == null) return null;
            string phase;
            int sign, weight;
            switch (c.Phase)
            {
                case CyclePhase.Menstruation:
                    phase = "You are bleeding these days: cramps, a heavy body, less patience than usual."; sign = -1; weight = c.Severity; break;
                case CyclePhase.Premenstrual:
                    phase = $"Your bleeding is {(c.DaysUntilPeriod <= 1 ? "a day" : c.DaysUntilPeriod + " days")} away: you feel swollen and tired, and small things irritate or sadden you more easily."; sign = -1; weight = c.Severity; break;
                case CyclePhase.Fertile:
                    phase = "These are your fertile days: you feel well, lively and more easily drawn to people."; sign = +1; weight = c.Severity > 0 ? 1 : 0; break;
                case CyclePhase.Follicular:
                    phase = "Your bleeding is behind you: you feel light and like yourself, with energy to spare."; sign = +1; weight = 0; break;
                default:
                    phase = "You feel steady, a little slower than a week ago."; sign = 0; weight = 0; break;
            }
            string how = c.Severity == 0 ? "hardly at all" : c.Severity == 1 ? "clearly" : "a great deal";
            return new Cause { Text = TCycle.Format("day", c.Day, "length", c.Length, "phase", phase, "strength", how), Sign = sign, Strength = weight, Age = 0 };
        }

        /// <summary>Por cuánto se multiplica la probabilidad de embarazo según el día del ciclo (1 si no tiene ciclo o está apagado).</summary>
        public static float FertilityFactor(Hero mother)
        {
            try { return mother == null ? 1f : Cycle(mother.StringId, mother.IsFemale, mother.Age, mother)?.Fertility ?? 1f; }
            catch { return 1f; }
        }

        /// <summary>Hash estable entre sesiones (string.GetHashCode no lo es).</summary>
        private static uint Stable(string text)
        {
            uint h = 2166136261;
            foreach (char c in text ?? "") { h ^= c; h *= 16777619; }
            return h;
        }
    }

    public class KillRecord
    {
        [JsonProperty("v")] public string Victim;
        [JsonProperty("k")] public string Killer;
        [JsonProperty("d")] public double Day;
    }
}
