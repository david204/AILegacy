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
    /// <summary>Something that happened to someone (or to a whole realm) and affects their mood for a few days.</summary>
    public class MoodEvent
    {
        /// <summary>Hero id, or "f:" + faction id when it affects the whole faction (a war just declared).</summary>
        [JsonProperty("who")] public string Who;
        [JsonProperty("kind")] public string Kind;
        /// <summary>What happened, in English and second person, for the prompt.</summary>
        [JsonProperty("text")] public string Text;
        [JsonProperty("sign")] public int Sign;            // -1 weighs down, +1 lifts, 0 unsettles
        [JsonProperty("strength")] public int Strength;    // 1..3
        [JsonProperty("day")] public double Day;
        /// <summary>Campaign day until the effect lasts.</summary>
        [JsonProperty("until")] public double Until;
        /// <summary>No other event of the same kind counts until this day, so ten battles in a row aren't ten separate joys.</summary>
        [JsonProperty("cool")] public double CoolUntil;
        /// <summary>Who it's about (the dead, the winner...), used to avoid duplicates.</summary>
        [JsonProperty("about")] public string About;
    }

    /// <summary>
    /// Mood built from several causes at once. Some come from world events (a battle won or lost, a tournament, release
    /// from captivity, a new war, a birth) and last a few days. Others are read live while they hold (prisoner, wounded,
    /// party wrecked, mourning a dead relative or friend, a relative held captive, pregnancy, the cycle). Each cause has a
    /// sign, a strength and an age, and at most five reach the prompt. What the AI said it felt at the end of the last
    /// talk is just one more cause, it doesn't set the mood on its own.
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

        // ============================================================ events that leave a mark

        private static void Add(string who, string kind, int sign, int strength, double days, double coolDays, string text, string about = null)
        {
            try
            {
                if (!S.Get.EnableMoodCauses || string.IsNullOrEmpty(who) || St == null) return;
                double now = Now;
                var list = St.MoodEvents;
                // Cooling down: one of the same kind happened recently
                if (list.Any(e => e.Who == who && e.Kind == kind && (e.CoolUntil > now || (about != null && e.About == about && e.Until > now)))) return;
                list.Add(new MoodEvent { Who = who, Kind = kind, Sign = sign, Strength = strength, Day = now, Until = now + days * Scale, CoolUntil = now + coolDays * Scale, Text = text, About = about });
                if (list.Count > 600) list.RemoveRange(0, list.Count - 600);
            }
            catch (Exception ex) { Log.Error("Mood: record cause", ex); }
        }

        /// <summary>A mood cause added by another mod through the API.</summary>
        internal static bool AddFromMod(string heroId, string text, int sign, int strength, int days)
        {
            if (!S.Get.EnableMoodCauses || St == null) return false;
            Add(heroId, "mod", sign, strength, days, 0, text, text);
            return true;
        }

        public static void OnDailyTick()
        {
            try
            {
                if (St == null) return;
                double now = Now;
                St.MoodEvents.RemoveAll(e => e.Until < now && e.CoolUntil < now);
                St.RecentKills.RemoveAll(k => now - k.Day > 40);
                foreach (var id in St.Conceived.Where(kv => now - kv.Value > 60).Select(kv => kv.Key).ToList()) St.Conceived.Remove(id);
            }
            catch { }
        }

        /// <summary>Battle, for each party leader: a win lasts 3 days, a loss 4, with a 14-day cooldown.</summary>
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
            catch (Exception ex) { Log.Error("Mood: battle", ex); }
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

        /// <summary>Tournament: the winner enjoys it for 5 days (30-day cooldown).</summary>
        public static void OnTournamentFinished(CharacterObject winner, TaleWorlds.Library.MBReadOnlyList<CharacterObject> participants, Town town, ItemObject prize)
        {
            try
            {
                var h = winner?.HeroObject;
                if (h == null || h == Hero.MainHero) return;
                Add(h.StringId, "tournament_won", +1, 2, 5, 30, $"You won the tournament of {town?.Name}: the cheers are still in your ears.");
            }
            catch (Exception ex) { Log.Error("Mood: tournament", ex); }
        }

        /// <summary>Capturing a noble: satisfied for 3 days (14-day cooldown).</summary>
        public static void OnPrisonerTaken(PartyBase captor, Hero prisoner)
        {
            try
            {
                var h = captor?.LeaderHero;
                if (h == null || h == Hero.MainHero || prisoner == null || !prisoner.IsLord) return;
                Add(h.StringId, "took_prisoner", +1, 1, 3, 14, $"You took {prisoner.Name} prisoner days ago: a prize, and you know it.");
            }
            catch (Exception ex) { Log.Error("Mood: prisoner", ex); }
        }

        /// <summary>Released from captivity: relieved for 4 days.</summary>
        public static void OnPrisonerReleased(Hero h, PartyBase captor, IFaction faction, EndCaptivityDetail detail, bool show)
        {
            try
            {
                if (h == null || h == Hero.MainHero || !h.IsAlive) return;
                Add(h.StringId, "released", +1, 2, 4, 10, "You were a prisoner until a few days ago and you are free again: relief, and a grudge against whoever held you.");
            }
            catch (Exception ex) { Log.Error("Mood: release", ex); }
        }

        /// <summary>A new war puts both sides on edge for 5 days.</summary>
        public static void OnWarDeclared(IFaction a, IFaction b, DeclareWarAction.DeclareWarDetail detail)
        {
            try
            {
                if (a == null || b == null || !a.IsKingdomFaction || !b.IsKingdomFaction) return;
                Add("f:" + a.StringId, "war_declared", 0, 1, 5, 0, $"War has just been declared between your realm and {b.Name}: everyone is on edge.", b.StringId);
                Add("f:" + b.StringId, "war_declared", 0, 1, 5, 0, $"War has just been declared between your realm and {a.Name}: everyone is on edge.", a.StringId);
            }
            catch (Exception ex) { Log.Error("Mood: war", ex); }
        }

        /// <summary>A birth: joy (or grief if stillborn) for 10 days.</summary>
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
            catch (Exception ex) { Log.Error("Mood: birth", ex); }
        }

        /// <summary>Records the conception day. She only finds out 7 to 11 days later (fixed per woman).</summary>
        public static void OnChildConceived(Hero mother)
        {
            try { if (mother != null && St != null) St.Conceived[mother.StringId] = Now; } catch { }
        }

        /// <summary>Pregnancies with no recorded conception day (older saves) count as known.</summary>
        public static bool KnowsSheIsPregnant(Hero h)
        {
            try
            {
                if (h == null || !h.IsPregnant) return false;
                if (St == null || !St.Conceived.TryGetValue(h.StringId, out double day)) return true;
                return Now - day >= 7 + Stable(h.StringId + "|p") % 5u;
            }
            catch { return true; }
        }

        /// <summary>Records who killed whom, so the killer doesn't mourn the victim.</summary>
        public static void OnHeroKilled(Hero victim, Hero killer)
        {
            try
            {
                if (victim == null || killer == null || St == null) return;
                St.RecentKills.Add(new KillRecord { Victim = victim.StringId, Killer = killer.StringId, Day = Now });
            }
            catch { }
        }

        // ============================================================ current mood

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

                // --- live states, they last as long as the condition holds
                try
                {
                    if (h.IsPrisoner)
                    {
                        string by = h.PartyBelongedToAsPrisoner?.LeaderHero?.Name?.ToString() ?? h.PartyBelongedToAsPrisoner?.Name?.ToString();
                        Live("You are a prisoner" + (by != null ? $" of {by}" : "") + ": humiliated, watchful, thinking of how to get out.", -1, 3);
                    }
                    if (h.IsWounded) Live("You are wounded and in pain: short-tempered and tired.", -1, 2);
                    if (h.IsPregnant && KnowsSheIsPregnant(h)) Live("You are with child: tired, careful of yourself, and thinking of what is coming.", 0, 2);
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

                // --- mourning: only for family and friends. A dead enemy is a quiet satisfaction, and the killer never mourns
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
                        else if (h.Clan != null && d.Clan == h.Clan && d.IsLord && ago <= 7 * Scale)
                            list.Add(new Cause { Text = $"{d.Name}, of your own house, died {Ago(ago)}: the house is in mourning.", Sign = -1, Strength = 1, Age = ago });
                        else if (ago <= 10 * Scale)
                        {
                            int rel = CharacterRelationManager.GetHeroRelation(h, d);
                            if (rel >= 40) list.Add(new Cause { Text = $"Your friend {d.Name} died {Ago(ago)}: you grieve for them.", Sign = -1, Strength = 2, Age = ago });
                            else if (rel <= -40 && ago <= 5 * Scale) list.Add(new Cause { Text = $"Your enemy {d.Name} died {Ago(ago)}: a grim satisfaction you keep mostly to yourself.", Sign = +1, Strength = 1, Age = ago });
                        }
                    }
                }
                catch { }

                // --- recorded events (their own and their realm's)
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

            // --- the cycle is just another cause
            try { var c = CycleCause(s); if (c != null) list.Add(c); } catch { }

            // --- how the last talk left them (what the AI said they felt). It counts, but doesn't dominate
            var mem = s.Memory;
            if (!string.IsNullOrWhiteSpace(mem.Mood) && mem.MoodDay >= 0 && now - mem.MoodDay <= 3)
                list.Add(new Cause
                {
                    Text = $"After your last talk with them you were left feeling {mem.Mood}" + (string.IsNullOrWhiteSpace(mem.MoodReason) ? "." : $" ({mem.MoodReason})."),
                    Sign = 0, Strength = 1, Age = now - mem.MoodDay,
                });
            return list;
        }

        /// <summary>Writes the mood into the prompt. Writes nothing if there are no causes.</summary>
        public static void AppendTo(StringBuilder sys, Speaker s)
        {
            try
            {
                var list = CausesFor(s).OrderByDescending(c => c.Strength).ThenBy(c => c.Age).Take(MaxCauses).ToList();
                if (list.Count == 0) return;
                // Causes system off and only one cause: the plain one-line mood is enough
                if (!S.Get.EnableMoodCauses && list.Count == 1) { sys.AppendLine("Your mood lately: " + list[0].Text); return; }
                sys.AppendLine();
                sys.AppendLine(THeader.Text);
                foreach (var c in list)
                    sys.AppendLine($"- ({(c.Sign > 0 ? "lifts you" : c.Sign < 0 ? "weighs on you" : "unsettles you")}, {(c.Strength >= 3 ? "strongly" : c.Strength == 2 ? "clearly" : "a little")}) {c.Text}");
            }
            catch (Exception ex) { Log.Error("Mood: text", ex); }
        }

        /// <summary>Short list for the UI and the editor. In English, since these are the same causes the AI sees.</summary>
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
                foreach (var x in h.ExSpouses) if (x != null && x.IsDead) A(x);      // a dead spouse moves to this list
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

        // ============================================================ cycle
        // Five phases. Each woman's cycle length, period length and how much it affects her are derived from her id,
        // so nothing has to be saved.

        public enum CyclePhase { Menstruation, Follicular, Fertile, Luteal, Premenstrual }

        public class CycleState
        {
            public CyclePhase Phase;
            public int Length;            // days, the settings average +/-2
            public int Day;               // 1..Length
            public int PeriodLength;      // days
            public int DaysUntilPeriod;
            public int Severity;          // 0 barely notices, 1 notices, 2 has a hard time
            public float Fertility;       // pregnancy chance multiplier
        }

        /// <summary>A woman's cycle on a given day. Depends only on her id and the day, nothing is saved.</summary>
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

        /// <summary>Today's cycle, or null if she has none: wrong age, pregnant, just gave birth, or turned off in settings.</summary>
        public static CycleState Cycle(string key, bool female, float age, Hero hero)
        {
            try
            {
                if (!S.Get.EnableFemaleCycle || key == null || !female || age < 13f || age >= 47f) return null;
                if (hero != null)
                {
                    if (hero.IsPregnant || !hero.IsAlive) return null;
                    // No cycle for a while after giving birth
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

        /// <summary>Pregnancy chance multiplier for the current cycle day (1 if there's no cycle or it's turned off).</summary>
        public static float FertilityFactor(Hero mother)
        {
            try { return mother == null ? 1f : Cycle(mother.StringId, mother.IsFemale, mother.Age, mother)?.Fertility ?? 1f; }
            catch { return 1f; }
        }

        /// <summary>FNV-1a hash, stable across sessions (string.GetHashCode isn't).</summary>
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
