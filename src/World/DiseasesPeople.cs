using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AILegacy.World
{
    /// <summary>Effect numbers of one disease. Anything left at its default is worked out from the severity.</summary>
    public class DiseaseFx
    {
        [JsonProperty("spread")] public float Spread;          // 0 = by severity
        [JsonProperty("death")] public float Death = -1f;      // daily, -1 = by severity
        [JsonProperty("combat")] public float Combat;          // multiplier, 0 = by severity
        [JsonProperty("speed")] public float MapSpeed;         // multiplier, 0 = by severity
        [JsonProperty("morale")] public float Morale;          // negative, 0 = by severity
        [JsonProperty("skill")] public float Skill;            // negative, 0 = by severity
    }

    /// <summary>One hero, or the soldiers or prisoners of one party, down with a disease.</summary>
    public class Sickness
    {
        public const int Hero = 0, Troops = 1, Prisoners = 2;

        [JsonProperty("who")] public string TargetId;          // hero id, or party id for soldiers and prisoners
        [JsonProperty("kind")] public int Kind;
        [JsonProperty("name")] public string Name;             // player's language
        [JsonProperty("name_en")] public string NameEn;        // for prompts, and what tells one disease from another
        [JsonProperty("severity")] public int Severity = 1;
        [JsonProperty("seasonal")] public bool Seasonal;
        [JsonProperty("progress")] public float Progress = 15f;   // 0 = well again, 100 = at death's door
        [JsonProperty("start")] public double StartDay;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("count")] public int Count;              // sick soldiers or prisoners
        [JsonProperty("fx")] public DiseaseFx Fx;
        [JsonProperty("treated")] public bool Treated;
        [JsonProperty("treat_day")] public double TreatDay = -1;
        [JsonProperty("quality")] public float Quality = 1f;
        [JsonProperty("after_rate")] public float AfterRate;   // what a finished treatment keeps doing for a few days
        [JsonProperty("after_days")] public int AfterDays;
        [JsonProperty("warned")] public int Warned;            // last threshold the player was told about
    }

    /// <summary>
    /// The people side of the disease system: heroes and parties catch what goes around (the season, a stricken place,
    /// their own men, prisoners, an army, a battle), it worsens or heals day by day, treatment and a good surgeon help,
    /// and it weighs on combat, march speed and morale (see Patches\DiseasePatch.cs).
    /// </summary>
    internal static partial class Diseases
    {
        private static List<Sickness> Sick => WorldSim.State.Sick;
        private static Dictionary<string, double> Immune => WorldSim.State.SickImmune;
        private const int MaxSickHeroes = 300, MaxSickBands = 200;
        private const int SeasonalDays = 14, OutbreakImmunityDays = 90;

        private static readonly float[] DailyWorse = { 1f, 1f, 1.5f, 2.5f, 3.5f, 5f };
        private static readonly float[] DailyBetter = { 2f, 2f, 1.5f, 1f, 0.75f, 0.5f };
        private static readonly float[] MedicineReach = { 1f, 1f, 0.8f, 0.6f, 0.4f, 0.2f };
        private static readonly int[] TreatmentDays = { 7, 7, 6, 5, 4, 4 };
        private static readonly int[] HealerNeeded = { 50, 50, 100, 175, 250, int.MaxValue };
        private static readonly float[] HealerRate = { 0.5f, 0.5f, 0.3f, 0.2f, 0.1f, 0f };

        private static readonly Tpl TPlace = new Tpl("diseases/place.txt",
            @"A sickness is in {settlement}: {disease} ({severity}), for {days} day(s) now. People are afraid.");
        private static readonly Tpl TPlaceClosed = new Tpl("diseases/place_quarantine.txt",
            @"A sickness is in {settlement}: {disease} ({severity}), for {days} day(s) now. The place is under QUARANTINE by order of its rulers: nobody is supposed to come or go.");
        private static readonly Tpl TPlaceSick = new Tpl("diseases/place_sick_people.txt",
            @"Known to be ill here: {names}.");
        private static readonly Tpl TSelf = new Tpl("diseases/sick_self.txt",
            @"You are ill with {disease} ({severity}), {days} day(s) now. {stage} {care} Let it show in how you look, how you sound and what you have the strength for, without turning every line into a complaint.");
        private static readonly Tpl TStageMild = new Tpl("diseases/stage_mild.txt", @"It is still mild: a nuisance more than a danger.");
        private static readonly Tpl TStageWearing = new Tpl("diseases/stage_wearing.txt", @"It is wearing you down: you tire fast and your temper is short.");
        private static readonly Tpl TStageBad = new Tpl("diseases/stage_bad.txt", @"It is bad: you are weak and feverish, and should be in bed.");
        private static readonly Tpl TStageGrave = new Tpl("diseases/stage_grave.txt", @"It is grave: you may not live, and you know it.");
        private static readonly Tpl TCareYes = new Tpl("diseases/care_treated.txt", @"A physician is treating you.");
        private static readonly Tpl TCareNo = new Tpl("diseases/care_untreated.txt", @"Nobody is treating you.");
        private static readonly Tpl TBand = new Tpl("diseases/sick_party.txt",
            @"{count} of the {total} soldiers in your party are down with {disease}; it slows the march and sours the men.");
        private static readonly Tpl TPlayerSick = new Tpl("diseases/sick_player.txt",
            @"{player}, who is speaking with you, is visibly ill ({disease}). You may notice it, keep your distance or show concern, as fits you.");

        // ============================================================ lookup

        private sealed class SickIndex
        {
            public List<Sickness> Source;
            public int Count;
            public readonly Dictionary<string, List<Sickness>> Heroes = new Dictionary<string, List<Sickness>>();
            public readonly Dictionary<string, List<Sickness>> Troops = new Dictionary<string, List<Sickness>>();
            public readonly Dictionary<string, List<Sickness>> Prisoners = new Dictionary<string, List<Sickness>>();
        }

        private static volatile SickIndex _index;
        private static void Touch() => _index = null;

        // The game's models ask about sickness many times a second, so answers come from a table rebuilt only when the list changes.
        private static SickIndex Index()
        {
            var list = WorldSim.State?.Sick;
            var ix = _index;
            if (ix != null && ReferenceEquals(ix.Source, list) && ix.Count == (list?.Count ?? 0)) return ix;
            var n = new SickIndex { Source = list, Count = list?.Count ?? 0 };
            if (list != null)
                foreach (var s in list.ToArray())
                {
                    if (s?.TargetId == null) continue;
                    var map = s.Kind == Sickness.Hero ? n.Heroes : s.Kind == Sickness.Troops ? n.Troops : n.Prisoners;
                    if (!map.TryGetValue(s.TargetId, out var l)) map[s.TargetId] = l = new List<Sickness>();
                    l.Add(s);
                }
            _index = n;
            return n;
        }

        public static bool AnySick
        {
            get { try { return WorldSim.State != null && WorldSim.State.Sick.Count > 0 && Enabled; } catch { return false; } }
        }

        public static List<Sickness> OfHero(Hero h) => h == null || !AnySick ? null : Index().Heroes.TryGetValue(h.StringId, out var l) ? l : null;
        public static List<Sickness> OfTroops(MobileParty p) => p == null || !AnySick ? null : Index().Troops.TryGetValue(p.StringId, out var l) ? l : null;
        public static List<Sickness> OfPrisoners(MobileParty p) => p == null || !AnySick ? null : Index().Prisoners.TryGetValue(p.StringId, out var l) ? l : null;

        // Called from the game's models many times a second: a plain scan, no sorting and no allocation
        private static Sickness Worst(List<Sickness> l)
        {
            if (l == null) return null;
            Sickness best = null;
            for (int i = 0; i < l.Count; i++)
            {
                var x = l[i];
                if (x == null) continue;
                if (best == null || x.Severity > best.Severity || (x.Severity == best.Severity && x.Progress > best.Progress)) best = x;
            }
            return best;
        }
        private static Hero FindHero(string id) { try { return Campaign.Current.CampaignObjectManager.Find<Hero>(id); } catch { return null; } }
        private static MobileParty FindParty(string id) { try { return Campaign.Current.CampaignObjectManager.Find<MobileParty>(id); } catch { return null; } }
        private static void Remove(Sickness s) { Sick.Remove(s); Touch(); }
        private static int Sev(int s) => Math.Max(1, Math.Min(5, s));
        private static bool Roll(float chance) => chance > 0f && Rng.NextDouble() < chance;
        private static int Whole(float expected) => expected <= 0f ? 0 : (int)expected + (Rng.NextDouble() < expected - (int)expected ? 1 : 0);

        // ============================================================ what a disease does

        internal struct Fx { public float Spread, Death, Combat, MapSpeed, Morale, Skill; }

        internal static Fx FxOf(Sickness s) => FxOf(s.Severity, s.Seasonal, s.Fx);

        /// <summary>The numbers of a disease, from what the event gave or from its severity, kept inside the limits set in MCM.</summary>
        internal static Fx FxOf(int severity, bool seasonal, DiseaseFx raw)
        {
            var cfg = S.Get;
            int sev = Sev(severity);
            Fx f;
            f.Spread = Math.Max(0.05f, Math.Min(cfg.DiseaseMaxSpreadRate, raw != null && raw.Spread > 0f ? raw.Spread : seasonal ? 0.1f : 0.2f + 0.1f * sev));
            float death = raw != null && raw.Death >= 0f ? raw.Death : seasonal || sev <= 2 ? 0f : sev == 3 ? 0.05f : sev == 4 ? 0.1f : 0.2f;
            f.Death = Math.Max(0f, Math.Min(cfg.DiseaseMaxDeathChance, death));
            f.Combat = Math.Max(cfg.DiseaseMinCombatModifier, Math.Min(1f, raw != null && raw.Combat > 0f ? raw.Combat : 1f - 0.1f * sev));
            f.MapSpeed = Math.Max(cfg.DiseaseMinMapSpeedModifier, Math.Min(1f, raw != null && raw.MapSpeed > 0f ? raw.MapSpeed : 1f - 0.1f * sev));
            f.Morale = Math.Max(cfg.DiseaseMaxMoralePenalty, Math.Min(0f, raw != null && raw.Morale < 0f ? raw.Morale : -5f * sev));
            f.Skill = Math.Max(cfg.DiseaseMaxPhysicalSkillPenalty, Math.Min(0f, raw != null && raw.Skill < 0f ? raw.Skill : -6f * sev));
            return f;
        }

        /// <summary>How much of the full effect is felt: a third at the first symptoms, all of it when it is at its worst.</summary>
        private static float Weight(Sickness s) => 0.35f + 0.65f * Math.Max(0f, Math.Min(1f, s.Progress / 100f));

        /// <summary>Combat multiplier and fighting-skill penalty of a sick hero.</summary>
        public static bool HeroCombat(Hero h, out float mult, out float skill, out string name)
        {
            mult = 1f; skill = 0f; name = null;
            var l = OfHero(h);
            if (l == null) return false;
            foreach (var s in l)
            {
                var f = FxOf(s); float w = Weight(s);
                float m = 1f - (1f - f.Combat) * w;
                if (m < mult || name == null) { mult = Math.Min(mult, m); name = s.Name; }
                skill = Math.Min(skill, f.Skill * w);
            }
            return name != null;
        }

        /// <summary>Combat multiplier of an ordinary soldier of a party where part of the men are sick (the share that is sick, spread over all).</summary>
        public static bool TroopCombat(MobileParty p, out float mult, out string name)
        {
            mult = 1f; name = null;
            var l = OfTroops(p);
            if (l == null) return false;
            int total = Math.Max(1, p.MemberRoster.TotalRegulars);
            foreach (var s in l)
            {
                var f = FxOf(s);
                mult *= 1f - (1f - f.Combat) * Weight(s) * Math.Min(1f, s.Count / (float)total);
                if (name == null) name = s.Name;
            }
            mult = Math.Max(S.Get.DiseaseMinCombatModifier, mult);
            return name != null && mult < 0.999f;
        }

        /// <summary>March speed multiplier and morale change of a party with sick men or a sick leader.</summary>
        public static bool PartyMap(MobileParty p, out float speed, out float morale, out string name)
        {
            speed = 1f; morale = 0f; name = null;
            if (p == null || !AnySick) return false;
            var troops = OfTroops(p);
            if (troops != null)
            {
                int total = Math.Max(1, p.MemberRoster.TotalRegulars);
                foreach (var s in troops)
                {
                    var f = FxOf(s); float w = Weight(s) * Math.Min(1f, s.Count / (float)total);
                    speed *= 1f - (1f - f.MapSpeed) * w;
                    morale += f.Morale * w;
                    if (name == null) name = s.Name;
                }
            }
            var lead = Worst(OfHero(p.LeaderHero));
            if (lead != null)
            {
                // a commander who can barely ride holds the whole column back, though less than a sick rank and file
                var f = FxOf(lead); float w = Weight(lead) * 0.5f;
                speed *= 1f - (1f - f.MapSpeed) * w;
                morale += f.Morale * w;
                if (name == null) name = lead.Name;
            }
            if (name == null) return false;
            var cfg = S.Get;
            speed = Math.Max(cfg.DiseaseMinMapSpeedModifier, speed);
            morale = Math.Max(cfg.DiseaseMaxMoralePenalty, morale);
            return true;
        }

        public static string HeroLabel(string name) => L.F("AIL_e7b1c5f9", $"Enfermedad ({name})");

        /// <summary>Short note for the conversation panel when the character you talk to is ill.</summary>
        public static string MarkerFor(Hero h)
        {
            try { var s = Worst(OfHero(h)); return s == null ? "" : L.F("AIL_e7b1c5fb", $"Enfermo: {s.Name}"); }
            catch { return ""; }
        }

        // ============================================================ medicine

        private static int Medicine(Hero h)
        {
            if (h == null) return 0;
            var surgeon = h.PartyBelongedTo?.EffectiveSurgeon;
            return Math.Max(surgeon?.GetSkillValue(DefaultSkills.Medicine) ?? 0, h.GetSkillValue(DefaultSkills.Medicine));
        }

        private static int TroopMedicine(MobileParty p, bool prisoners)
        {
            float m = (p?.EffectiveSurgeon?.GetSkillValue(DefaultSkills.Medicine) ?? 0) * S.Get.DiseaseMedicineSkillForTroopsMultiplier;
            return (int)(prisoners ? m * 0.5f : m);      // prisoners get what is left of the surgeon's time
        }

        /// <summary>How much of the disease a healer of that skill holds back (0..0.5), less the worse the disease.</summary>
        private static float MedEff(int medicine, int severity) => Math.Min(0.5f, medicine / 300f * 0.5f) * MedicineReach[Sev(severity)];

        private static float Difficulty(float progress)
        {
            float t = Math.Max(0f, Math.Min(1f, progress / 100f));
            return t <= 0.5f ? 2f - 2f * t : 1f - 0.75f * ((t - 0.5f) / 0.5f);      // early it heals fast, late it barely moves
        }

        private static void StartTreatment(Sickness s, float quality)
        {
            s.Treated = true; s.TreatDay = CampaignTime.Now.ToDays; s.Quality = Math.Max(0.5f, quality);
            s.AfterRate = 0f; s.AfterDays = 0;
        }

        private static bool NeedsCare(Sickness s) => !s.Treated && s.AfterDays <= 0 && s.Progress > 0f;

        /// <summary>One day of the illness: under treatment it usually recedes, a good healer holds it or pushes it back, otherwise it grows.</summary>
        private static void Advance(Sickness s, int medicine, double now, bool quarantinedCare)
        {
            var cfg = S.Get;
            int sev = Sev(s.Severity);
            float worse = DailyWorse[sev] * cfg.DiseaseProgressionMultiplier * (1f - MedEff(medicine, sev));
            float better = DailyBetter[sev] * cfg.DiseaseRecoveryMultiplier;
            float diff = Difficulty(s.Progress);
            if (s.Treated)
            {
                int days = TreatmentDays[sev];
                float strength = Math.Max(0f, 1f - (float)(now - s.TreatDay) / days);
                if (strength <= 0f)
                {
                    // the course is over: its good keeps working, at half pace, for as many days again
                    s.Treated = false; s.AfterRate = better * s.Quality * 0.5f; s.AfterDays = days;
                }
                else
                {
                    float chance = Math.Max(5f, Math.Min(100f, 50f + medicine * 0.2f + s.Quality * 25f - sev * 15f));
                    if (Rng.NextDouble() * 100.0 <= chance) s.Progress -= better * strength * s.Quality * diff * (quarantinedCare ? 1.3f : 1f);
                    else s.Progress += worse;
                    Cap(s);
                    return;
                }
            }
            if (s.AfterDays > 0) { s.Progress -= s.AfterRate * diff; s.AfterDays--; }
            else if (medicine >= HealerNeeded[sev]) s.Progress -= HealerRate[sev] * (0.5f + medicine / 300f) * diff;
            else s.Progress += worse;
            Cap(s);
        }

        private static void Cap(Sickness s)
        {
            float top = s.Severity <= 2 ? 90f : 100f;      // mild diseases never reach the end
            s.Progress = Math.Max(0f, Math.Min(top, s.Progress));
        }

        // ============================================================ catching it

        private static string ImmuneKey(Hero h, bool seasonal, string nameEn) => h.StringId + "|" + (seasonal ? "*" : nameEn);

        public static Sickness InfectHero(Hero h, string name, string nameEn, int severity, bool seasonal, DiseaseFx fx, double untilDay)
        {
            try
            {
                if (!Enabled || h == null || !h.IsAlive || h.IsChild || string.IsNullOrWhiteSpace(nameEn)) return null;
                double now = CampaignTime.Now.ToDays;
                if (Immune.TryGetValue(ImmuneKey(h, seasonal, nameEn), out var until) && until > now) return null;
                var mine = OfHero(h);
                if (mine != null && (mine.Count >= 2 || mine.Any(x => x.NameEn == nameEn) || (seasonal && mine.Any(x => x.Seasonal)))) return null;
                if (h != Hero.MainHero && Sick.Count(x => x.Kind == Sickness.Hero) >= MaxSickHeroes) return null;
                int sev = Sev(severity);
                var s = new Sickness
                {
                    TargetId = h.StringId, Kind = Sickness.Hero, Name = string.IsNullOrWhiteSpace(name) ? nameEn : name, NameEn = nameEn, Severity = sev, Seasonal = seasonal, Fx = fx,
                    StartDay = now, UntilDay = Math.Max(now + 5, untilDay), Progress = Math.Max(1f, 15f - 15f * MedEff(Medicine(h), sev) * 2f),
                };
                Sick.Add(s); Touch();
                Log.Info($"[disease] {h.Name} falls ill with {nameEn} (severity {sev})");
                if (h == Hero.MainHero) WorldSim.Say(L.F("AIL_e7b1c5ee", $"Has caído enfermo: {s.Name}."), Colors.Red);
                else if (Close(h)) WorldSim.Say(L.F("AIL_e7b1c5ef", $"{h.Name} ha caído enfermo: {s.Name}."), Colors.Red);
                return s;
            }
            catch (Exception ex) { Log.Error("[disease] character infection", ex); return null; }
        }

        private static bool Close(Hero h) => h != null && (h.PartyBelongedTo == MobileParty.MainParty || (h.Clan != null && h.Clan == Clan.PlayerClan));

        /// <summary>Soldiers (or prisoners) of a party fall sick. Returns how many new ones.</summary>
        public static int InfectBand(MobileParty p, int kind, string name, string nameEn, int severity, bool seasonal, DiseaseFx fx, double untilDay, int count, bool wound = true, bool quiet = false)
        {
            try
            {
                if (!Enabled || p == null || !p.IsActive || count <= 0 || string.IsNullOrWhiteSpace(nameEn)) return 0;
                if (p.IsMainParty && kind == Sickness.Troops && !S.Get.DiseaseAffectsPlayerTroops) return 0;
                var roster = kind == Sickness.Troops ? p.MemberRoster : p.PrisonRoster;
                int total = roster?.TotalRegulars ?? 0;
                if (total <= 0) return 0;
                double now = CampaignTime.Now.ToDays;
                var list = kind == Sickness.Troops ? OfTroops(p) : OfPrisoners(p);
                var s = list?.FirstOrDefault(x => x.NameEn == nameEn);
                bool fresh = s == null;
                int added;
                if (s != null)
                {
                    added = Math.Min(count, total - s.Count);
                    if (added <= 0) return 0;
                    s.Count += added;
                }
                else
                {
                    if (list != null && list.Count >= 2) return 0;
                    if (!p.IsMainParty && Sick.Count(x => x.Kind != Sickness.Hero) >= MaxSickBands) return 0;
                    int sev = Sev(severity);
                    added = Math.Min(count, total);
                    s = new Sickness
                    {
                        TargetId = p.StringId, Kind = kind, Name = string.IsNullOrWhiteSpace(name) ? nameEn : name, NameEn = nameEn, Severity = sev, Seasonal = seasonal, Fx = fx,
                        StartDay = now, UntilDay = Math.Max(now + 5, untilDay), Count = added,
                        Progress = Math.Max(1f, 15f - 15f * MedEff(TroopMedicine(p, kind == Sickness.Prisoners), sev) * 2f),
                    };
                    Sick.Add(s); Touch();
                }
                // a chill creeping through the ranks is told once, not man by man
                if (p.IsMainParty && !quiet && (fresh || !seasonal))
                {
                    // a real outbreak lays the player's men low like before; a seasonal chill only weakens them
                    if (wound && !seasonal && kind == Sickness.Troops) WoundRegulars(roster, added);
                    WorldSim.Say(kind == Sickness.Troops ? L.F("AIL_e7b1c5f0", $"{added} de tus soldados enferman de {s.Name}.") : L.F("AIL_e7b1c5f1", $"{added} de tus prisioneros enferman de {s.Name}."), Colors.Red);
                }
                return added;
            }
            catch (Exception ex) { Log.Error("[disease] troop infection", ex); return 0; }
        }

        /// <summary>Soldiers of the player just laid low by an outbreak (already wounded and announced by the caller).</summary>
        private static void RegisterSickTroops(Outbreak o, int count) =>
            InfectBand(MobileParty.MainParty, Sickness.Troops, o.Name, o.NameEn, o.Severity, false, o.Fx, o.UntilDay + 3, count, false, true);

        private static int WoundRegulars(TroopRoster roster, int toll)
        {
            int done = 0;
            foreach (var e in roster.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero && x.Number - x.WoundedNumber > 0).OrderBy(_ => Rng.Next()).ToList())
            {
                if (done >= toll) break;
                int n = Math.Min(e.Number - e.WoundedNumber, toll - done);
                roster.WoundTroop(e.Character, n);
                done += n;
            }
            return done;
        }

        private static int KillRegulars(TroopRoster roster, int toll)
        {
            int done = 0;
            foreach (var e in roster.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero && x.Number > 0).OrderBy(x => x.Character.Tier).ToList())
            {
                if (done >= toll) break;
                int n = Math.Min(e.Number, toll - done);
                roster.AddToCounts(e.Character, -n, false, -Math.Min(n, e.WoundedNumber));      // the sick ones are among the wounded
                done += n;
            }
            return done;
        }

        // ---- the season

        private struct Seasonal { public string Id, Es, En; public int Severity, Season; public char Likes; }

        // Season: -1 any, otherwise the game's season number. Likes: what makes it twice as likely (s sea, c cold, h heat, r rain).
        private static readonly Seasonal[] SeasonalKinds =
        {
            new Seasonal { Id = "AIL_e7b1c607", Es = "resfriado", En = "a head cold", Severity = 1, Season = -1, Likes = 'r' },
            new Seasonal { Id = "AIL_e7b1c608", Es = "tos de campaña", En = "camp cough", Severity = 1, Season = -1, Likes = 'r' },
            new Seasonal { Id = "AIL_e7b1c609", Es = "fiebre del camino", En = "road fever", Severity = 1, Season = -1, Likes = ' ' },
            new Seasonal { Id = "AIL_e7b1c60a", Es = "mal de vientre", En = "camp gripes", Severity = 1, Season = -1, Likes = 's' },
            new Seasonal { Id = "AIL_e7b1c60b", Es = "calenturas", En = "the ague", Severity = 2, Season = -1, Likes = 'r' },
            new Seasonal { Id = "AIL_e7b1c60e", Es = "fiebre de a bordo", En = "ship fever", Severity = 1, Season = -1, Likes = 's' },
            new Seasonal { Id = "AIL_b86021cd", Es = "fiebre de pulmón", En = "lung fever", Severity = 2, Season = 3, Likes = 'c' },
            new Seasonal { Id = "AIL_e7b1c60c", Es = "fiebre de la escarcha", En = "frost fever", Severity = 2, Season = 3, Likes = 'c' },
            new Seasonal { Id = "AIL_e7b1c60f", Es = "flujo de sangre", En = "the bloody flux", Severity = 2, Season = 1, Likes = 'h' },
            new Seasonal { Id = "AIL_e7b1c60d", Es = "insolación", En = "sun sickness", Severity = 1, Season = 1, Likes = 'h' },
            new Seasonal { Id = "AIL_d8412138", Es = "tercianas de los pantanos", En = "marsh ague", Severity = 1, Season = 2, Likes = 'r' },
            new Seasonal { Id = "AIL_fa46e0da", Es = "fiebre de las manchas", En = "spotted fever", Severity = 1, Season = 0, Likes = ' ' },
        };

        private static Seasonal PickSeasonal(int season, bool sea, bool snow, bool rain)
        {
            var pool = new List<(Seasonal k, float w)>();
            foreach (var k in SeasonalKinds)
            {
                if (k.Season >= 0 && k.Season != season) continue;
                if (k.En == "ship fever" && !sea) continue;
                float w = 1f;
                if (k.Likes == 's' && sea) w *= 2f;
                if (k.Likes == 'c' && snow) w *= 2f;
                if (k.Likes == 'h' && season == 1) w *= 2f;
                if (k.Likes == 'r' && rain) w *= 1.5f;
                pool.Add((k, w));
            }
            float roll = (float)Rng.NextDouble() * pool.Sum(x => x.w);
            foreach (var (k, w) in pool) { roll -= w; if (roll <= 0f) return k; }
            return pool[0].k;
        }

        /// <summary>Every six hours, those on the road may catch something mild: more in winter, in rain or snow, and at sea.</summary>
        private static void SeasonalRound()
        {
            var cfg = S.Get;
            if (!cfg.EnableSeasonalDiseases) return;
            double now = CampaignTime.Now.ToDays;
            int season = (int)CampaignTime.Now.GetSeasonOfYear;
            float seasonAdd = season == 3 ? cfg.SeasonalWinterBonus : season == 2 ? cfg.SeasonalAutumnBonus : season == 0 ? cfg.SeasonalSpringBonus : cfg.SeasonalSummerBonus;
            var weather = Campaign.Current.Models?.MapWeatherModel;
            // the cap keeps the whole map from going sick at once; the player's own party is never held back by it
            int cap = cfg.SeasonalMaxLiveCases;
            int live = cap > 0 ? Sick.Count(x => x.Seasonal) : 0;
            foreach (var p in MobileParty.All.ToList())
            {
                if (p == null || !p.IsActive || p.CurrentSettlement != null || p.MapEvent != null) continue;
                if (!p.IsMainParty && !p.IsLordParty && !p.IsCaravan) continue;
                if (cap > 0 && live >= cap && !p.IsMainParty) continue;
                float chance = cfg.SeasonalDiseaseBaseChance + seasonAdd;
                bool sea = false, snow = false, rain = false;
                try
                {
                    sea = p.IsCurrentlyAtSea;
                    if (sea) chance += cfg.SeasonalShipAtSeaBonus;
                    if (weather != null)
                    {
                        var pos = p.GetPosition2D;
                        switch (weather.GetWeatherEventInPosition(pos))
                        {
                            case MapWeatherModel.WeatherEvent.LightRain: chance += cfg.SeasonalWeatherLightRainBonus; rain = true; break;
                            case MapWeatherModel.WeatherEvent.HeavyRain:
                            case MapWeatherModel.WeatherEvent.Storm: chance += cfg.SeasonalWeatherHeavyRainBonus; rain = true; break;
                            case MapWeatherModel.WeatherEvent.Snowy: chance += cfg.SeasonalWeatherSnowyBonus; snow = true; break;
                            case MapWeatherModel.WeatherEvent.Blizzard: chance += cfg.SeasonalWeatherBlizzardBonus; snow = true; rain = true; break;
                        }
                        if (weather.GetWeatherEffectOnTerrainForPosition(pos) == MapWeatherModel.WeatherEventEffectOnTerrain.Wet) chance += cfg.SeasonalWeatherWetBonus;
                    }
                }
                catch { }
                float guard = p.IsMainParty ? PlayerGuard : 1f;
                var heroes = p.IsMainParty ? p.MemberRoster.GetTroopRoster().Where(e => e.Character.IsHero).Select(e => e.Character.HeroObject).ToList()
                                           : p.LeaderHero != null ? new List<Hero> { p.LeaderHero } : new List<Hero>();
                foreach (var h in heroes)
                {
                    if (h == null || !h.IsAlive) continue;
                    float c = Math.Min(cfg.SeasonalDiseaseMaxChance, chance * Math.Max(0.5f, 1f - h.GetSkillValue(DefaultSkills.Medicine) / 600f)) * (1f - Hardiness(h)) * guard;
                    if (!Roll(c)) continue;
                    var k = PickSeasonal(season, sea, snow, rain);
                    if (InfectHero(h, L.T(k.Id, k.Es), k.En, k.Severity, true, null, now + SeasonalDays) != null) live++;
                }
                if (cap > 0 && live >= cap && !p.IsMainParty) continue;
                int regulars = p.MemberRoster.TotalRegulars;
                if (regulars <= 0 || (OfTroops(p)?.Any(x => x.Seasonal) ?? false)) continue;
                float ct = Math.Min(cfg.SeasonalDiseaseMaxChance, chance * Math.Max(0.5f, 1f - (p.LeaderHero?.GetSkillValue(DefaultSkills.Medicine) ?? 0) / 600f)) * guard;
                if (!Roll(ct)) continue;
                var kind = PickSeasonal(season, sea, snow, rain);
                if (InfectBand(p, Sickness.Troops, L.T(kind.Id, kind.Es), kind.En, kind.Severity, true, null, now + SeasonalDays, Math.Max(1, (int)(regulars * 0.02f))) > 0) live++;
            }
        }

        /// <summary>
        /// The daily chance that you catch something mild on the road where you stand now, the same sum as the six-hourly
        /// roll (season, weather, sea, your Medicine and constitution, prevention and herbs). 0 when seasonal illness is off.
        /// </summary>
        internal static float PlayerRoadRiskPerDay()
        {
            try
            {
                var cfg = S.Get;
                var p = MobileParty.MainParty;
                if (!cfg.EnableSeasonalDiseases || p == null || Hero.MainHero == null) return 0f;
                int season = (int)CampaignTime.Now.GetSeasonOfYear;
                float chance = cfg.SeasonalDiseaseBaseChance + (season == 3 ? cfg.SeasonalWinterBonus : season == 2 ? cfg.SeasonalAutumnBonus : season == 0 ? cfg.SeasonalSpringBonus : cfg.SeasonalSummerBonus);
                try
                {
                    if (p.IsCurrentlyAtSea) chance += cfg.SeasonalShipAtSeaBonus;
                    var weather = Campaign.Current.Models?.MapWeatherModel;
                    if (weather != null)
                    {
                        var pos = p.GetPosition2D;
                        switch (weather.GetWeatherEventInPosition(pos))
                        {
                            case MapWeatherModel.WeatherEvent.LightRain: chance += cfg.SeasonalWeatherLightRainBonus; break;
                            case MapWeatherModel.WeatherEvent.HeavyRain:
                            case MapWeatherModel.WeatherEvent.Storm: chance += cfg.SeasonalWeatherHeavyRainBonus; break;
                            case MapWeatherModel.WeatherEvent.Snowy: chance += cfg.SeasonalWeatherSnowyBonus; break;
                            case MapWeatherModel.WeatherEvent.Blizzard: chance += cfg.SeasonalWeatherBlizzardBonus; break;
                        }
                        if (weather.GetWeatherEffectOnTerrainForPosition(pos) == MapWeatherModel.WeatherEventEffectOnTerrain.Wet) chance += cfg.SeasonalWeatherWetBonus;
                    }
                }
                catch { }
                var h = Hero.MainHero;
                float c = Math.Min(cfg.SeasonalDiseaseMaxChance, chance * Math.Max(0.5f, 1f - h.GetSkillValue(DefaultSkills.Medicine) / 600f)) * (1f - Hardiness(h)) * PlayerGuard;
                return 1f - (float)Math.Pow(1f - Math.Max(0f, Math.Min(1f, c)), 4);
            }
            catch (Exception ex) { Log.Error("[disease] road risk of the player's party", ex); return 0f; }
        }

        /// <summary>A strong constitution shrugs off part of what goes around (0..0.4).</summary>
        private static float Hardiness(Hero h)
        {
            try { return Math.Min(0.4f, h.GetAttributeValue(DefaultCharacterAttributes.Endurance) * 0.04f); } catch { return 0f; }
        }

        // ---- a stricken place

        private static float PlaceFactor(Outbreak o, Settlement st)
        {
            float prosperity = st.Town != null ? st.Town.Prosperity : 1000f;
            float crowding = 1f - Math.Min(0.5f, prosperity / 10000f);      // richer places keep cleaner streets and wells
            return crowding * FxOf(o.Severity, false, o.Fx).Spread * (o.Quarantined ? S.Get.QuarantineInfectionReduction : 1f);
        }

        private static float HeroResist(Hero h) => 1f - Math.Min(0.5f, Medicine(h) / 600f);

        private static void CatchAt(Hero h, Outbreak o, Settlement st, float baseChance, float perSeverity)
        {
            if (h == null || !h.IsAlive) return;
            float guard = h == Hero.MainHero || h.PartyBelongedTo == MobileParty.MainParty ? PlayerGuard : 1f;
            if (Roll(baseChance * (1f + o.Severity * perSeverity) * PlaceFactor(o, st) * HeroResist(h) * guard))
                InfectHero(h, o.Name, o.NameEn, o.Severity, false, o.Fx, o.UntilDay + 3);
        }

        /// <summary>Every six hours, whoever is inside a place with an outbreak may catch it: residents, visiting lords and their men, and the player's company.</summary>
        private static void PlaceRound()
        {
            var cfg = S.Get;
            foreach (var o in All.ToList())
            {
                var st = Settlement.Find(o.SettlementId);
                if (st == null) continue;
                foreach (var h in st.HeroesWithoutParty.Concat(st.Notables).Distinct().ToList())
                    CatchAt(h, o, st, cfg.SettlementInfectionBaseChance, 0.1f);
                foreach (var p in st.Parties.ToList())
                {
                    if (p == null || !p.IsActive) continue;
                    if (p.IsMainParty)
                    {
                        foreach (var e in p.MemberRoster.GetTroopRoster().Where(x => x.Character.IsHero).ToList())
                            CatchAt(e.Character.HeroObject, o, st, cfg.SettlementInfectionBaseChance, 0.1f);
                        continue;      // the player's soldiers have their own daily roll (OnDailyTick)
                    }
                    if (!p.IsLordParty && !p.IsCaravan) continue;
                    CatchAt(p.LeaderHero, o, st, cfg.SettlementInfectionBaseChance, 0.1f);
                    int regulars = p.MemberRoster.TotalRegulars;
                    if (regulars > 0 && Roll(0.075f * (1f + o.Severity * 0.1f) * PlaceFactor(o, st)))
                        InfectBand(p, Sickness.Troops, o.Name, o.NameEn, o.Severity, false, o.Fx, o.UntilDay + 3, Math.Max(1, (int)(regulars * 0.03f)));
                }
            }
        }

        public static void OnHourlyTick()
        {
            try
            {
                if (Campaign.Current == null || !Enabled) return;
                bool round = (long)CampaignTime.Now.ToHours % 6 == 0;
                try { CareRound(CampaignTime.Now.ToDays, round); } catch (Exception ex) { Log.Error("[disease] parties going for treatment", ex); }
                if (!round) return;
                SeasonalRound();
                PlaceRound();
                try { ForceRound(); } catch (Exception ex) { Log.Error("[disease] garrison infection", ex); }
            }
            catch (Exception ex) { Log.Error("[disease] infection round", ex); }
        }

        // ---- walking around inside (missions)

        private static float _walkClock;
        private static string _hallSeen;

        /// <summary>While the player walks a stricken town, a roll every minute; and one on entering a hall where sick lords sit.</summary>
        public static void MissionTick(float dt)
        {
            try
            {
                if (Campaign.Current != null && Mission.Current == null) RefundCarriedHerbs();
                if (Campaign.Current == null || Mission.Current == null || Agent.Main == null) { _walkClock = 0f; _hallSeen = null; return; }
                var st = Settlement.CurrentSettlement;
                if (st == null || !Enabled) return;
                var cfg = S.Get;
                string where = null;
                try { where = CampaignMission.Current?.Location?.StringId; } catch { }
                if (where == "lordshall" && _hallSeen != st.StringId)
                {
                    _hallSeen = st.StringId;
                    var sickLords = st.HeroesWithoutParty.Concat(st.Parties.Select(p => p.LeaderHero)).Where(h => h != null && h.IsLord && h != Hero.MainHero && OfHero(h) != null).Distinct().ToList();
                    if (sickLords.Count > 0)
                    {
                        var s = Worst(OfHero(sickLords[Rng.Next(sickLords.Count)]));
                        float closed = QuarantineRules.Closed(st) ? cfg.QuarantineInfectionReduction : 1f;
                        float chance = Math.Min(0.35f, cfg.LordHallInfectionChancePerLord * sickLords.Count * (1f + s.Severity * 0.1f) * HeroResist(Hero.MainHero) * closed);
                        if (Roll(chance * PlayerGuard)) InfectHero(Hero.MainHero, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay);
                    }
                }
                var o = At(st);
                if (o == null) return;
                _walkClock += dt;
                if (_walkClock < 60f) return;
                _walkClock = 0f;
                CatchAt(Hero.MainHero, o, st, cfg.MissionInfectionBaseChance, 0.2f);
            }
            catch (Exception ex) { Log.Error("[disease] infection in the scene", ex); }
        }

        // ---- a sick party arrives somewhere

        private static void OnAnyPartyEntered(MobileParty party, Settlement st)
        {
            if (party == null || st == null || !AnySick) return;
            var lead = OfHero(party.LeaderHero);
            var troops = OfTroops(party);
            if (lead == null && troops == null) return;
            bool hostile = party.MapFaction != null && st.MapFaction != null && party.MapFaction.IsAtWarWith(st.MapFaction);
            if (!party.IsMainParty && !hostile && (st.IsTown || st.IsCastle || st.IsVillage)) SeekCare(party, st, lead, troops);

            // carried in: a party with a real disease may start it where there was none
            if (st.IsHideout || !(st.IsTown || st.IsCastle || st.IsVillage) || At(st) != null) return;
            int regulars = Math.Max(1, party.MemberRoster.TotalRegulars);
            var carried = Worst((troops ?? new List<Sickness>()).Concat(lead ?? new List<Sickness>()).Where(x => !x.Seasonal && x.Severity >= 2).ToList());
            if (carried == null) return;
            float share = carried.Kind == Sickness.Hero ? 0.5f : Math.Max(0.2f, Math.Min(1f, carried.Count / (float)regulars));
            if (!Roll(0.05f * FxOf(carried).Spread * share)) return;
            double now = CampaignTime.Now.ToDays;
            int days = (int)Math.Max(10, (carried.UntilDay - now) * S.Get.DiseaseSpreadInheritFactor);
            var n = Start(st, carried.Name, carried.NameEn, Math.Max(1, carried.Severity - (Rng.Next(2) == 0 ? 1 : 0)), days, true, carried.Fx);
            if (n != null) Log.Info($"[disease] {party.Name} carries {carried.NameEn} to {st.Name}");
        }

        /// <summary>Strength of a course of treatment from the physicians of a place: by its medical level, lowest with a village healer.</summary>
        private static float CareQuality(Settlement st)
        {
            if (st == null || st.IsVillage || st.Town == null) return 1f;
            return TierCare[Math.Max(1, Math.Min(4, MedicalTier(st)))];
        }

        private static int HeroCareCost(Sickness s, Settlement st) => (int)Math.Ceiling(S.Get.DiseaseTreatmentBaseCost * s.Severity * HospitalPrice(st));
        private static int BandCareCost(Sickness s, Settlement st) =>
            (int)Math.Ceiling(S.Get.DiseaseTreatmentBaseCost * s.Severity * HospitalPrice(st) * S.Get.DiseaseTreatmentTroopMultiplier * Math.Sqrt(Math.Max(1, s.Count)));

        /// <summary>A lord, or a caravan master, who rides into a friendly place sick pays its physicians if the purse allows.</summary>
        private static void SeekCare(MobileParty party, Settlement st, List<Sickness> lead, List<Sickness> troops)
        {
            var payer = party.LeaderHero;
            float q = CareQuality(st);
            foreach (var s in (lead ?? new List<Sickness>()).Concat(troops ?? new List<Sickness>()).Where(NeedsCare).ToList())
            {
                int cost = s.Kind == Sickness.Hero ? HeroCareCost(s, st) : BandCareCost(s, st);
                if (payer != null && !PayCare(payer, cost, st)) continue;
                StartTreatment(s, q);
            }
        }

        // ---- battle

        public static void OnMapEventEnded(MapEvent e)
        {
            try
            {
                if (e == null || !AnySick) return;
                double now = CampaignTime.Now.ToDays;
                var sides = new[] { e.AttackerSide, e.DefenderSide };
                var parties = sides.Select(side => side?.Parties.Select(x => x.Party?.MobileParty).Where(x => x != null && x.IsActive).ToList() ?? new List<MobileParty>()).ToArray();
                var all = parties[0].Concat(parties[1]).ToList();
                foreach (var carrier in all)
                {
                    var s = Worst(OfTroops(carrier));
                    if (s == null) continue;
                    var f = FxOf(s);
                    float share = Math.Min(1f, s.Count / (float)Math.Max(1, carrier.MemberRoster.TotalRegulars));
                    foreach (var other in all)
                    {
                        if (other == carrier) continue;
                        int regulars = other.MemberRoster.TotalRegulars;
                        if (regulars > 0 && Roll(0.075f * f.Spread * (1f + s.Severity * 0.1f) * (0.3f + share)))
                            InfectBand(other, Sickness.Troops, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay, Math.Max(1, (int)(regulars * 0.02f)));
                    }
                }
                // the winners' new prisoners bring it along
                if (!e.HasWinner || S.Get.PrisonerSpreadModifier <= 0f) return;
                int win = e.WinningSide == BattleSideEnum.Attacker ? 0 : 1;
                Sickness worst = null; float worstShare = 0f;
                foreach (var loser in parties[1 - win])
                {
                    var s = Worst(OfTroops(loser));
                    if (s == null) continue;
                    float share = Math.Min(1f, s.Count / (float)Math.Max(1, loser.MemberRoster.TotalRegulars + s.Count));
                    if (worst == null || share > worstShare) { worst = s; worstShare = share; }
                }
                if (worst == null) return;
                foreach (var winner in parties[win])
                {
                    int held = winner.PrisonRoster?.TotalRegulars ?? 0;
                    if (held > 0 && Roll(S.Get.PrisonerSpreadModifier))
                        InfectBand(winner, Sickness.Prisoners, worst.Name, worst.NameEn, worst.Severity, worst.Seasonal, worst.Fx, worst.UntilDay, Math.Max(1, (int)(held * Math.Max(0.05f, worstShare))));
                }
            }
            catch (Exception ex) { Log.Error("[disease] infection in battle", ex); }
        }

        // ============================================================ day by day

        private static void TickSick(double now)
        {
            foreach (var s in Sick.ToList())
            {
                try
                {
                    if (s == null || s.TargetId == null) { Sick.Remove(s); Touch(); continue; }
                    if (s.Kind == Sickness.Hero) TickHero(s, now); else TickBand(s, now);
                }
                catch (Exception ex) { Log.Error("[disease] sick person's day", ex); }
            }
            try { SpreadInParties(now); } catch (Exception ex) { Log.Error("[disease] infection in parties", ex); }
            foreach (var k in Immune.Where(x => x.Value <= now).Select(x => x.Key).ToList()) Immune.Remove(k);
        }

        private static void TickHero(Sickness s, double now)
        {
            var h = FindHero(s.TargetId);
            if (h == null || !h.IsAlive) { Remove(s); return; }
            if (now >= s.UntilDay) { Recover(s, h, now); return; }
            // at home or lodged in a settlement, someone looks after them
            if (h != Hero.MainHero && h.PartyBelongedTo == null && h.PartyBelongedToAsPrisoner == null && h.CurrentSettlement != null && NeedsCare(s))
                StartTreatment(s, CareQuality(h.CurrentSettlement));
            Advance(s, Medicine(h), now, h.CurrentSettlement != null && QuarantineRules.Closed(h.CurrentSettlement));
            if (s.Progress <= 0f) { Recover(s, h, now); return; }
            float death = FxOf(s).Death;
            if (s.Severity > 2 && death > 0f)
            {
                // at the very end it kills; from the halfway point there is a growing daily risk
                float t = s.Progress / 100f;
                if (s.Progress >= 100f || (s.Progress >= 50f && Roll(death * t * t))) { Kill(h, s); return; }
            }
            if (h == Hero.MainHero || Close(h))
            {
                int step = s.Progress >= 80f ? 80 : s.Progress >= 50f ? 50 : 0;
                if (step > s.Warned && h == Hero.MainHero)
                    WorldSim.Say(L.F("AIL_e7b1c5f5", $"Tu {s.Name} empeora ({(int)s.Progress}%). Necesitas un médico."), Colors.Red);
                s.Warned = step;
            }
        }

        private static void Kill(Hero h, Sickness s)
        {
            // never in the middle of a battle or siege, never someone a running quest hangs on
            if (h.PartyBelongedTo?.MapEvent != null || h.PartyBelongedTo?.SiegeEvent != null || (h.IsNotable && h.Issue != null)
                || !h.CanDie(KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge)) { s.Progress = Math.Min(s.Progress, 95f); return; }
            // Not while a scene is open or someone is talking: the dead hero may be standing in it. It waits a day.
            try
            {
                if (TaleWorlds.MountAndBlade.Mission.Current != null || Campaign.Current.ConversationManager?.IsConversationInProgress == true)
                { s.Progress = Math.Min(s.Progress, 95f); return; }
            }
            catch { }
            // An illness never ends the campaign: the player only dies of it when someone is left to carry on
            if (h == Hero.MainHero)
            {
                bool heir = false;
                try { heir = Clan.PlayerClan != null && Clan.PlayerClan.Heroes.Any(x => x != null && x != h && x.IsAlive && !x.IsChild && !x.IsPrisoner); } catch { }
                if (!heir) { s.Progress = Math.Min(s.Progress, 95f); return; }
            }
            string who = h.Name.ToString();
            bool close = Close(h), isPlayer = h == Hero.MainHero;
            Settlement where = h.CurrentSettlement ?? h.HomeSettlement;
            var realm = h.MapFaction;
            bool known = h.IsLord;
            bool ownTexts = NoteDeath(h, s);
            KillCharacterAction.ApplyByOldAge(h, true);
            if (isPlayer) { Remove(s); return; }      // the game takes over from here (heir, or the end)
            if (h.IsAlive) { ForgetDeath(h); s.Progress = 95f; return; }
            ConfirmDeath(h);
            Remove(s);
            Log.Info($"[disease] {who} dies of {s.NameEn}");
            if (close && !ownTexts) WorldSim.Say(L.F("AIL_e7b1c5f8", $"{who} ha muerto de {s.Name}."), Colors.Red);
            if (known)
                NewsStore.Add(new NewsItem
                {
                    Type = "event_disease", Importance = 2, Text = $"{who} has died of {s.NameEn}.",
                    TitleEs = L.F("AIL_e7b1c606", $"Muere {who}"), TextEs = L.F("AIL_e7b1c5f8", $"{who} ha muerto de {s.Name}."),
                    SettlementId = where?.StringId, Kingdoms = realm != null ? new List<string> { realm.StringId } : new List<string>(),
                });
        }

        private static void Recover(Sickness s, Hero h, double now)
        {
            Remove(s);
            int days = s.Seasonal ? S.Get.SeasonalPostRecoveryImmunityDays : OutbreakImmunityDays;
            if (days > 0) Immune[ImmuneKey(h, s.Seasonal, s.NameEn)] = now + days;
            if (h == Hero.MainHero) WorldSim.Say(L.F("AIL_e7b1c5f2", $"Te has recuperado de {s.Name}."), Colors.Green);
            else if (Close(h)) WorldSim.Say(L.F("AIL_e7b1c5f3", $"{h.Name} se ha recuperado de {s.Name}."), Colors.Green);
        }

        private static void TickBand(Sickness s, double now)
        {
            var p = FindParty(s.TargetId);
            if (p == null || !p.IsActive) { Remove(s); return; }
            bool prisoners = s.Kind == Sickness.Prisoners;
            var roster = prisoners ? p.PrisonRoster : p.MemberRoster;
            s.Count = Math.Min(s.Count, roster?.TotalRegulars ?? 0);
            if (s.Count <= 0) { Remove(s); return; }
            if (now < s.UntilDay) Advance(s, TroopMedicine(p, prisoners), now, false);
            if (now >= s.UntilDay || s.Progress <= 0f)
            {
                Remove(s);
                if (p.IsMainParty && !prisoners) WorldSim.Say(L.F("AIL_e7b1c5f4", $"Tus soldados ya están libres de {s.Name}."), Colors.Green);
                return;
            }
            if (S.Get.DiseaseMaxDeathChance <= 0f || p.MapEvent != null) return;
            float rate = s.Progress >= 100f ? 0.05f + s.Severity * 0.01f : s.Progress >= 80f ? (s.Progress - 80f) / 400f : 0f;
            if (rate <= 0f) return;
            int dead = KillRegulars(roster, Math.Max(1, (int)(s.Count * rate)));
            s.Count -= dead;
            if (dead > 0 && p.IsMainParty)
                WorldSim.Say(prisoners ? L.F("AIL_e7b1c5f7", $"{dead} de tus prisioneros mueren de {s.Name}.") : L.F("AIL_e7b1c5f6", $"{dead} de tus soldados mueren de {s.Name}."), Colors.Red);
            if (s.Count <= 0) Remove(s);
        }

        /// <summary>Inside a party it passes between the leader, the companions, the rank and file and the prisoners; in an army, between parties.</summary>
        private static void SpreadInParties(double now)
        {
            var cfg = S.Get;
            var parties = new HashSet<MobileParty>();
            foreach (var s in Sick.ToList())
            {
                if (s.Kind != Sickness.Hero) { var p = FindParty(s.TargetId); if (p != null) parties.Add(p); continue; }
                var h = FindHero(s.TargetId);
                var hp = h?.PartyBelongedTo ?? (h?.PartyBelongedToAsPrisoner != null && h.PartyBelongedToAsPrisoner.IsMobile ? h.PartyBelongedToAsPrisoner.MobileParty : null);
                if (hp != null) parties.Add(hp);
            }
            foreach (var p in parties)
            {
                if (p == null || !p.IsActive || p.MapEvent != null) continue;
                int regulars = p.MemberRoster.TotalRegulars;
                float damp = 150f / (150f + regulars);      // big hosts camp spread out
                bool main = p.IsMainParty;
                float guard = main ? PlayerGuard : 1f;
                var riders = (main ? p.MemberRoster.GetTroopRoster().Where(e => e.Character.IsHero).Select(e => e.Character.HeroObject) : new[] { p.LeaderHero }).Where(h => h != null && h.IsAlive).ToList();
                var troops = (OfTroops(p) ?? new List<Sickness>()).ToList();
                var held = (OfPrisoners(p) ?? new List<Sickness>()).ToList();

                // sick heroes (riding with the party, or kept in its chains) to the rank and file
                var sources = riders.Select(h => (s: Worst(OfHero(h)), k: 1f)).ToList();
                if (cfg.PrisonerSpreadModifier > 0f)
                    sources.AddRange(p.PrisonRoster.GetTroopRoster().Where(e => e.Character.IsHero).Select(e => (s: Worst(OfHero(e.Character.HeroObject)), k: cfg.PrisonerSpreadModifier)));
                foreach (var (s, k) in sources)
                {
                    if (s == null || regulars <= 0) continue;
                    var f = FxOf(s);
                    int healthy = regulars - (troops.FirstOrDefault(x => x.NameEn == s.NameEn)?.Count ?? 0);
                    int n = Whole(healthy * 0.075f * (1f - MedEff(TroopMedicine(p, false), s.Severity)) * f.Spread * (1f + s.Severity * 0.1f) * damp * guard * k);
                    if (n > 0) InfectBand(p, Sickness.Troops, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay, n);
                }

                foreach (var s in troops)
                {
                    var f = FxOf(s);
                    float sevMult = 1f + s.Severity * 0.1f;
                    float share = Math.Min(1f, s.Count / (float)Math.Max(1, regulars));
                    // among the men
                    int n = Whole((regulars - s.Count) * 0.025f * Math.Min(1f, 0.1f + s.Count / 100f) * (1f - MedEff(TroopMedicine(p, false), s.Severity)) * f.Spread * sevMult * damp * guard);
                    if (n > 0) InfectBand(p, Sickness.Troops, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay, n);
                    // to whoever leads and rides with them
                    foreach (var h in riders)
                        if (Roll(cfg.PlayerFromTroopsBaseChance * share * f.Spread * sevMult * (1f - Math.Min(0.9f, h.GetSkillValue(DefaultSkills.Medicine) / 600f)) * guard))
                            InfectHero(h, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay);
                    // to the prisoners
                    int captives = p.PrisonRoster.TotalRegulars;
                    if (captives > 0 && cfg.PrisonerSpreadModifier > 0f)
                    {
                        int c = Whole(captives * 0.025f * share * f.Spread * sevMult * cfg.PrisonerSpreadModifier);
                        if (c > 0) InfectBand(p, Sickness.Prisoners, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay, c);
                    }
                    // to the parties marching in the same army
                    if (p.Army != null)
                        foreach (var q in p.Army.Parties.ToList())
                        {
                            if (q == null || q == p || !q.IsActive) continue;
                            int qr = q.MemberRoster.TotalRegulars;
                            if (qr > 0 && Roll(0.075f * f.Spread * sevMult * (0.3f + share) * (q.IsMainParty ? PlayerGuard : 1f)))
                                InfectBand(q, Sickness.Troops, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay, Math.Max(1, (int)(qr * 0.02f)));
                        }
                }

                if (cfg.PrisonerSpreadModifier <= 0f) continue;
                foreach (var s in held)
                {
                    var f = FxOf(s);
                    float sevMult = 1f + s.Severity * 0.1f;
                    float share = Math.Min(1f, s.Count / (float)Math.Max(1, p.PrisonRoster.TotalRegulars));
                    int n = Whole(regulars * 0.025f * share * f.Spread * sevMult * damp * guard * cfg.PrisonerSpreadModifier);
                    if (n > 0) InfectBand(p, Sickness.Troops, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay, n);
                    foreach (var h in riders)
                        if (Roll(cfg.PlayerFromTroopsBaseChance * share * f.Spread * sevMult * cfg.PrisonerSpreadModifier * 0.5f * guard))
                            InfectHero(h, s.Name, s.NameEn, s.Severity, s.Seasonal, s.Fx, s.UntilDay);
                }
            }
        }

        // ============================================================ what they know

        private static string PlaceText(Outbreak o, Settlement st)
        {
            string text = (o.Quarantined ? TPlaceClosed : TPlace).Format("settlement", st.Name, "disease", o.NameEn, "severity", SeverityWord(o.Severity), "days", (int)(CampaignTime.Now.ToDays - o.StartDay));
            try
            {
                if (AnySick)
                {
                    var names = st.HeroesWithoutParty.Concat(st.Notables).Distinct().Where(h => h != null && OfHero(h) != null).Take(6).Select(h => h.Name.ToString()).ToList();
                    if (names.Count > 0) text += " " + TPlaceSick.Format("names", string.Join(", ", names));
                }
            }
            catch { }
            return text;
        }

        /// <summary>For the prompt of a character: their own illness, their sick soldiers, and the player's if it shows.</summary>
        public static string DescribeSick(Hero h)
        {
            try
            {
                if (h == null || !AnySick) return "";
                var lines = new List<string>();
                double now = CampaignTime.Now.ToDays;
                foreach (var s in OfHero(h) ?? new List<Sickness>())
                {
                    var stage = s.Progress >= 85f ? TStageGrave : s.Progress >= 60f ? TStageBad : s.Progress >= 30f ? TStageWearing : TStageMild;
                    lines.Add(TSelf.Format("disease", s.NameEn, "severity", SeverityWord(s.Severity), "days", (int)Math.Max(0, now - s.StartDay),
                        "stage", stage.Text, "care", (s.Treated || s.AfterDays > 0 ? TCareYes : TCareNo).Text));
                }
                var party = h.PartyBelongedTo;
                if (party != null && party.LeaderHero == h)
                    foreach (var s in OfTroops(party) ?? new List<Sickness>())
                        lines.Add(TBand.Format("count", s.Count, "total", party.MemberRoster.TotalRegulars, "disease", s.NameEn));
                var mine = h == Hero.MainHero ? null : Worst(OfHero(Hero.MainHero));
                if (mine != null && mine.Progress >= 20f)
                    lines.Add(TPlayerSick.Format("player", Hero.MainHero.Name, "disease", mine.NameEn));
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }

        // ============================================================ hospital

        private static List<Hero> MyHeroes() =>
            MobileParty.MainParty.MemberRoster.GetTroopRoster().Where(e => e.Character.IsHero).Select(e => e.Character.HeroObject).ToList();
    }
}
