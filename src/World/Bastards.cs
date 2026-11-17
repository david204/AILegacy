using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>A child conceived outside marriage: who the parents are, and what became of it.</summary>
    public class Bastard
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("mother")] public string MotherId;
        [JsonProperty("father")] public string FatherId;
        [JsonProperty("conceived")] public double ConceivedDay;
        /// <summary>The mother's husband when she conceived, if she had one who is not the father.</summary>
        [JsonProperty("husband", NullValueHandling = NullValueHandling.Ignore)] public string HusbandId;
        [JsonProperty("born")] public bool Born;
        [JsonProperty("lost")] public bool Lost;
        [JsonProperty("children")] public List<string> Children = new List<string>();
        /// <summary>Children that pass for the husband's: the world does not know.</summary>
        [JsonProperty("secret")] public bool Secret;
        [JsonProperty("secret_id", NullValueHandling = NullValueHandling.Ignore)] public string SecretId;
        /// <summary>Children made legitimate since (by id).</summary>
        [JsonProperty("legit")] public List<string> Legitimized = new List<string>();
        /// <summary>Each child's name as it was born, to give it back when legitimized.</summary>
        [JsonProperty("names")] public Dictionary<string, string> BirthNames = new Dictionary<string, string>();
        /// <summary>Record format. 0 = written before the mother's house and the father's knowledge were kept (those births stay as they were).</summary>
        [JsonProperty("v", DefaultValueHandling = DefaultValueHandling.Ignore)] public int Version;
        /// <summary>Whether the mother had met the father when she conceived. Null on older records: worked out when needed.</summary>
        [JsonProperty("met", NullValueHandling = NullValueHandling.Ignore)] public bool? Met;
        /// <summary>The player, as parent, has owned the child before everyone.</summary>
        [JsonProperty("acknowledged", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Acknowledged;
        /// <summary>The scandal (relation lost with the mother's husband and head of house) has already been paid.</summary>
        [JsonProperty("scandal", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool ScandalPaid;
    }

    /// <summary>
    /// Children out of wedlock. The pregnancy itself goes in the game's own queue with the right father (the game only ever
    /// writes the mother's husband), so the clock, stillbirths and labour are the game's. This keeps the record of who the
    /// father is, and at birth: the child grows up in its mother's house with a baseborn surname; a married mother may pass
    /// it off as her husband's, an unmarried one may keep the father's name to herself, or the scandal comes out. Later the
    /// player, as parent, may acknowledge the child (through the mother, or face to face once the child is of age), and
    /// there are the other ways a child is made legitimate. The father is only ever named if the mother knows who he is.
    /// </summary>
    internal static class Bastards
    {
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;
        private static List<Bastard> All => WorldSim.State?.Bastards ?? new List<Bastard>();
        public static bool Enabled => S.Get.EnableBastards && Campaign.Current != null;

        private static readonly Tpl TSurnames = new Tpl("bastards/surnames.txt",
@"empire: Nothus, Spurius, Viator, Ex Agro, Sine Domo, Advena, Fortunatus, Crescens
vlandia: Fitzmarsh, of the Hedge, Ashford, Lackland, Thornby, Underhill, Wayborn, Stillwater
sturgia: Lausung, Frostkin, Birchborn, Hearthless, Pinewood, Coldbrook, Wolfden, Mossback
nord: Lausung, Saltborn, Shorekin, Driftwood, Gullcry, Kelpfoot, Tidewalker, Stonebeach
battania: mac Coille, of the Glen, Fernborn, Briarson, Mistvale, Oakshade, Burnside, Heathkin
khuzait: of the Tent, Grassborn, Windrider, Steppekin, Dustmane, Far-Camp, Kumis-born, Lonefire
aserai: ibn al-Rih, of the Dunes, Sandborn, Oasiskin, Wadi-born, Starfall, of the Well, Mirage
other: the Baseborn, of No House, Roadborn, Nameless, Wayfarer, Foundling");

        private static readonly Tpl TMother = new Tpl("bastards/ctx_mother.txt",
@"{state} by {father}, who is not your husband{secret}.");
        private static readonly Tpl TFather = new Tpl("bastards/ctx_father.txt",
@"{state} with {mother}, who is not your wife{secret}.");
        private static readonly Tpl THusband = new Tpl("bastards/ctx_husband.txt",
            "You know that your wife {mother} bore a child by {father}: {children}. It weighs on you.");
        private static readonly Tpl TChild = new Tpl("bastards/ctx_child.txt",
            "You were born out of wedlock: your father is {father}{known}. You are not legitimate{heir}.");
        private static readonly Tpl TLegitChild = new Tpl("bastards/ctx_child_legit.txt",
            "You were born out of wedlock, the child of {father} and {mother}, and have since been made legitimate.");

        public static void Touch() { var _ = TSurnames.Key + TMother.Key + TFather.Key + THusband.Key + TChild.Key + TLegitChild.Key; }

        // ============================================================ conception

        private static FieldInfo _queueField;
        private static ConstructorInfo _pregnancyCtor;

        /// <summary>Every record kept, for the console commands.</summary>
        internal static List<Bastard> Records => All;

        /// <summary>The game's own pregnancy queue (its entries have Mother, Father and DueDate), or null.</summary>
        private static IList GameQueue()
        {
            try
            {
                var behavior = Campaign.Current?.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.PregnancyCampaignBehavior>();
                if (behavior == null) return null;
                if (_queueField == null) _queueField = HarmonyLib.AccessTools.Field(behavior.GetType(), "_heroPregnancies");
                return _queueField?.GetValue(behavior) as IList;
            }
            catch { return null; }
        }

        private static Hero MotherOf(object entry) => entry?.GetType().GetField("Mother")?.GetValue(entry) as Hero;
        private static Hero FatherOf(object entry) => entry?.GetType().GetField("Father")?.GetValue(entry) as Hero;

        /// <summary>Console: the child is due now (the game delivers it on its next daily look at the mother).</summary>
        internal static bool DueNow(Bastard b)
        {
            var q = GameQueue();
            var mother = Hero.Find(b.MotherId);
            if (q == null || mother == null) return false;
            foreach (var e in q)
            {
                if (MotherOf(e) != mother) continue;
                // the entry's date cannot be changed: it is replaced by the same pregnancy, due now
                var father = FatherOf(e);
                if (_pregnancyCtor == null)
                    _pregnancyCtor = e.GetType().GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Hero), typeof(Hero), typeof(CampaignTime) }, null);
                if (_pregnancyCtor == null || father == null) return false;
                int i = q.IndexOf(e);
                q[i] = _pregnancyCtor.Invoke(new object[] { mother, father, CampaignTime.Now });
                Log.Info($"[bastards] console: {mother.Name}'s child is due now");
                return true;
            }
            return false;
        }

        /// <summary>Console: the pregnancy ends without a birth, out of the game's queue too.</summary>
        internal static void EndPregnancy(Bastard b)
        {
            var mother = Hero.Find(b.MotherId);
            var q = GameQueue();
            if (q != null && mother != null) for (int i = q.Count - 1; i >= 0; i--) if (MotherOf(q[i]) == mother) q.RemoveAt(i);
            if (mother != null) mother.IsPregnant = false;
            b.Lost = true;
            Log.Info($"[bastards] console: {mother?.Name?.ToString() ?? b.MotherId}'s pregnancy is ended");
            SaveFiles.Dirty();
        }

        /// <summary>The pregnancies kept, as text: each with when it is due, and those ended without a birth (closed as lost when <paramref name="apply"/>).</summary>
        internal static string CheckText(bool apply)
        {
            var open = All.Where(x => !x.Born && !x.Lost).ToList();
            if (open.Count == 0) return "No tracked pregnancies.";
            var due = new Dictionary<Hero, CampaignTime>();
            var q = GameQueue();
            if (q != null) foreach (var e in q) { var m = MotherOf(e); if (m != null && e.GetType().GetField("DueDate")?.GetValue(e) is CampaignTime t) due[m] = t; }
            var sb = new System.Text.StringBuilder();
            var seen = new HashSet<string>();
            foreach (var b in open)
            {
                var mother = Hero.Find(b.MotherId); var father = Hero.Find(b.FatherId);
                string why = mother == null || !mother.IsAlive ? "the mother is dead" : !mother.IsPregnant ? "the mother is no longer pregnant" : !seen.Add(b.MotherId) ? "a repeated record" : null;
                if (why == null)
                {
                    string when = due.TryGetValue(mother, out var t) ? $"due in {Math.Max(0, (int)Math.Ceiling(t.ToDays - CampaignTime.Now.ToDays))} day(s)" : "not in the game's queue";
                    sb.AppendLine($"  OK    {mother.Name} by {father?.Name}{(Concubines.Is(mother) || Concubines.Is(father) ? " (concubine)" : "")}, {when}");
                }
                else
                {
                    sb.AppendLine($"  {(apply ? "LOST " : "WOULD")} {mother?.Name?.ToString() ?? b.MotherId} by {father?.Name?.ToString() ?? b.FatherId}: {why}");
                    if (apply) b.Lost = true;
                }
            }
            if (apply) SaveFiles.Dirty();
            return sb.ToString().TrimEnd();
        }

        /// <summary>Console: what a woman's daily chance of an affair is right now, and with whom.</summary>
        internal static string AffairOdds(Hero w)
        {
            var cfg = S.Get;
            var sb = new System.Text.StringBuilder();
            sb.AppendLine($"{w.Name}: yearly chance setting {cfg.BastardNpcYearlyPercent}%");
            if (!MayBeMother(w)) { sb.Append("  cannot be a mother now (age, pregnant, notable, no house, a baby under a year, a concubine or keeping one, or your clan protected)."); return sb.ToString(); }
            float dw = Disposition(w) * Fertility(w);
            sb.AppendLine($"  disposition {Disposition(w):0.00}, fertility {Fertility(w):0.00}");
            double year = CampaignTime.DaysInYear;
            double Daily(float yearly) => 1.0 - Math.Pow(1.0 - Math.Max(0f, Math.Min(0.95f, yearly)), 1.0 / year);
            var lovers = NearbyMen(w).Where(m => MayBeLover(w, m)).Select(m => (h: m, wt: Disposition(m) * (1f + w.GetRelation(m) / 100f))).Where(x => x.wt > 0f).ToList();
            if (lovers.Count == 0) { sb.Append("  nobody suitable near her now."); return sb.ToString(); }
            float best = lovers.Max(x => x.wt);
            double total = lovers.Sum(x => x.wt);
            sb.AppendLine($"  today: {Daily(cfg.BastardNpcYearlyPercent / 100f * Math.Min(3f, dw * best)) * 100:0.000}% (yearly {cfg.BastardNpcYearlyPercent * Math.Min(3f, dw * best):0.0}%)");
            foreach (var (h, wt) in lovers.OrderByDescending(x => x.wt)) sb.AppendLine($"    {h.Name}: weight {wt:0.00} ({wt / total * 100:0}% of the pick)");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Console (Illegitimate Children's «fix vanilla pregnancies»): entries in the game's queue with no mother or father,
        /// a dead mother, or the same mother twice, which would break a birth. Removed when <paramref name="apply"/>.
        /// </summary>
        internal static string CleanGameQueue(bool apply)
        {
            var q = GameQueue();
            if (q == null) return "The game's pregnancy list was not found.";
            var sb = new System.Text.StringBuilder();
            var seen = new HashSet<Hero>();
            var bad = new List<object>();
            foreach (var e in q)
            {
                var m = MotherOf(e); var f = FatherOf(e);
                string why = m?.CharacterObject == null || f?.CharacterObject == null ? "no mother or no father" : !m.IsAlive ? "the mother is dead" : !seen.Add(m) ? "the same mother twice" : null;
                if (why == null) { sb.AppendLine($"  OK    {m.Name} by {f.Name}"); continue; }
                bad.Add(e);
                sb.AppendLine($"  {(apply ? "FIXED" : "BAD  ")} {m?.Name?.ToString() ?? "?"} by {f?.Name?.ToString() ?? "?"}: {why}");
            }
            if (apply) foreach (var e in bad) q.Remove(e);
            if (bad.Count == 0) sb.AppendLine("Nothing to fix.");
            else if (!apply) sb.AppendLine("Run ailegacy.fix_vanilla_pregnancies without arguments to remove them.");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Puts a pregnancy with this father in the game's own queue. Every step is checked: if the game changed and any of it
        /// is missing, nothing happens rather than half a pregnancy.
        /// </summary>
        private static bool Inject(Hero mother, Hero father)
        {
            try
            {
                var behavior = Campaign.Current.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.PregnancyCampaignBehavior>();
                if (behavior == null) return false;
                if (_queueField == null) _queueField = HarmonyLib.AccessTools.Field(behavior.GetType(), "_heroPregnancies");
                if (_queueField == null) { Log.Warn("Illegitimate pregnancy: game has no _heroPregnancies"); return false; }
                var list = _queueField.GetValue(behavior) as IList;
                if (list == null) return false;
                if (_pregnancyCtor == null)
                {
                    var type = behavior.GetType().GetNestedType("Pregnancy", BindingFlags.NonPublic | BindingFlags.Public);
                    _pregnancyCtor = type?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Hero), typeof(Hero), typeof(CampaignTime) }, null);
                }
                if (_pregnancyCtor == null) { Log.Warn("Illegitimate pregnancy: game pregnancy constructor not found"); return false; }
                foreach (var e in list)
                {
                    var m = e?.GetType().GetField("Mother")?.GetValue(e) as Hero;
                    if (m == mother) return false;
                }
                var due = CampaignTime.DaysFromNow(Campaign.Current.Models.PregnancyModel.PregnancyDurationInDays);
                if (S.Get.BastardCustomPregnancy)
                {
                    float lo = Math.Max(0.05f, Math.Min(S.Get.BastardBirthMinYears, S.Get.BastardBirthMaxYears));
                    float hi = Math.Max(lo, Math.Max(S.Get.BastardBirthMinYears, S.Get.BastardBirthMaxYears));
                    due = CampaignTime.YearsFromNow(lo + (hi - lo) * (float)Rng.NextDouble());
                }
                list.Add(_pregnancyCtor.Invoke(new object[] { mother, father, due }));
                mother.IsPregnant = true;
                return true;
            }
            catch (Exception ex) { Log.Error("Illegitimate pregnancy: game queue", ex); return false; }
        }

        /// <summary>
        /// A child conceived out of wedlock. True if the pregnancy took. <paramref name="met"/>: whether the two were together
        /// when it happened (an affair, intimacy); null works it out from what the mother knows of him.
        /// </summary>
        public static bool Conceive(Hero mother, Hero father, bool? met = null)
        {
            if (!Enabled || mother == null || father == null || !mother.IsFemale || father.IsFemale || mother.IsPregnant || !mother.IsAlive || !father.IsAlive) return false;
            if (mother.Spouse == father) return false;
            if (!Inject(mother, father)) return false;
            var b = new Bastard
            {
                Id = WorldSim.NewId("bst"), MotherId = mother.StringId, FatherId = father.StringId, ConceivedDay = CampaignTime.Now.ToDays,
                HusbandId = mother.Spouse != null && mother.Spouse != father && mother.Spouse.IsAlive ? mother.Spouse.StringId : null,
                Version = 2, Met = met ?? HaveMet(mother, father),
            };
            WorldSim.State.Bastards.Add(b);
            // without MakePregnantAction there is no conception event: tell the mood system by hand
            try { Mood.OnChildConceived(mother); } catch { }
            if (S.Get.BastardNotifications && (mother == P || father == P))
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1de27", $"¡{mother.Name} se ha quedado embarazada!"), Color.FromUint(0xFFFF99CC)));
            Log.Info($"[bastards] {mother.Name} is expecting a child by {father.Name}{(b.HusbandId != null ? " (not her husband)" : "")}");
            SaveFiles.Dirty();
            return true;
        }

        /// <summary>Intimacy with the player, not married to each other: the same roll as between spouses.</summary>
        public static void TryWithPlayer(Hero other)
        {
            try
            {
                if (!Enabled || other == null || P == null || other.IsFemale == P.IsFemale || other.Spouse == P || Kin(other, P)) return;
                var mother = other.IsFemale ? other : P; var father = other.IsFemale ? P : other;
                if (mother.IsPregnant || mother.Age < 18 || mother.Age >= 46) return;
                float chance = S.Get.BastardChancePercent * 10f * Mood.FertilityFactor(mother);
                if (Rng.Next(1000) < Math.Min(1000f, chance) && Conceive(mother, father, met: true))
                {
                    var mem = MemoryStore.Find(other.StringId);
                    if (mem != null && mem.Romance < S.Get.RomanceForMarriage) { mem.Romance = S.Get.RomanceForMarriage; Log.Info($"[bastards] {other.Name}'s romance rises to {mem.Romance} with the child on the way"); }
                }
            }
            catch (Exception ex) { Log.Error("[bastards] intimacy with the player", ex); }
        }

        // ---- between two characters, when they are together (off by default)

        private static float Disposition(Hero h)
        {
            int honor = 0; try { honor = h.GetTraitLevel(DefaultTraits.Honor); } catch { }
            if (h.Spouse != null && h.Spouse.IsAlive && honor > 0) return 0f;
            float d = honor <= -2 ? 2f : honor == -1 ? 1.5f : honor == 0 ? 1f : honor == 1 ? 0.5f : 0.25f;
            if (h.Spouse != null && h.Spouse.IsAlive) d *= Math.Max(0f, Math.Min(1f, 0.5f - h.GetRelation(h.Spouse) / 200f));
            return d;
        }

        private static float Fertility(Hero w) => w.Age < 18 || w.Age > 45 ? 0f : w.Age <= 30 ? 1f : 1f - (w.Age - 30f) / 15f * 0.8f;

        private static IEnumerable<Hero> NearbyMen(Hero w)
        {
            var seen = new HashSet<Hero>();
            var st = w.CurrentSettlement ?? w.PartyBelongedTo?.CurrentSettlement;
            if (st != null)
            {
                foreach (var p in st.Parties) if (p?.LeaderHero != null && seen.Add(p.LeaderHero)) yield return p.LeaderHero;
                foreach (var h in st.HeroesWithoutParty) if (seen.Add(h)) yield return h;
            }
            var party = w.PartyBelongedTo;
            if (party?.Army != null) foreach (var p in party.Army.Parties) if (p?.LeaderHero != null && seen.Add(p.LeaderHero)) yield return p.LeaderHero;
            if (party != null)
                foreach (var e in party.MemberRoster.GetTroopRoster())
                    if (e.Character?.HeroObject != null && seen.Add(e.Character.HeroObject)) yield return e.Character.HeroObject;
        }

        private static bool MayBeMother(Hero w) =>
            w != null && w.IsFemale && w != P && w.IsAlive && !w.IsChild && !w.IsNotable && w.Clan != null && !w.IsPregnant && Fertility(w) > 0
            && !Concubines.Is(w) && !Concubines.Keeps(w)
            && !(S.Get.BastardProtectPlayerClan && w.Clan == Clan.PlayerClan) && !w.Children.Any(c => c.IsAlive && c.Age < 1);

        private static bool MayBeLover(Hero w, Hero m) =>
            m != null && !m.IsFemale && m != P && m.IsAlive && m.Age >= 18 && !m.IsNotable && m.Clan != null && m != w.Spouse && m.Clan != w.Clan
            && !Kin(w, m) && w.GetRelation(m) >= 0 && !Concubines.Is(m) && !Concubines.Keeps(m);

        internal static bool Kin(Hero a, Hero b)
        {
            if (a == null || b == null) return false;
            if (a.Father == b || a.Mother == b || b.Father == a || b.Mother == a) return true;
            if ((a.Father != null && a.Father == b.Father) || (a.Mother != null && a.Mother == b.Mother)) return true;
            return a.Children.Contains(b) || b.Children.Contains(a);
        }

        /// <summary>One roll per woman a day, checked first against the best case so the costly search is rare.</summary>
        public static void OnDailyTickHero(Hero w)
        {
            try
            {
                var cfg = S.Get;
                if (!Enabled || cfg.BastardNpcYearlyPercent <= 0 || !MayBeMother(w)) return;
                float dw = Disposition(w) * Fertility(w);
                if (dw <= 0f) return;
                double year = CampaignTime.DaysInYear;
                double Daily(float yearly) => 1.0 - Math.Pow(1.0 - Math.Max(0f, Math.Min(0.95f, yearly)), 1.0 / year);
                double roll = Rng.NextDouble();
                // best possible: a lover with the top weight (disposition 2, attraction 2)
                if (roll >= Daily(cfg.BastardNpcYearlyPercent / 100f * Math.Min(3f, dw * 4f))) return;
                var lovers = new List<(Hero h, float w)>();
                foreach (var m in NearbyMen(w))
                {
                    if (!MayBeLover(w, m)) continue;
                    float weight = Disposition(m) * (1f + w.GetRelation(m) / 100f);
                    if (weight > 0f) lovers.Add((m, weight));
                }
                if (lovers.Count == 0) return;
                float best = lovers.Max(x => x.w);
                if (roll >= Daily(cfg.BastardNpcYearlyPercent / 100f * Math.Min(3f, dw * best))) return;
                double total = lovers.Sum(x => x.w), pick = Rng.NextDouble() * total;
                foreach (var (h, wt) in lovers) { if ((pick -= wt) > 0) continue; Conceive(w, h, met: true); return; }
            }
            catch (Exception ex) { Log.Error("[bastards] daily roll", ex); }
        }

        // ============================================================ birth

        public static void OnGivenBirth(Hero mother, List<Hero> children, int stillborn)
        {
            try
            {
                if (!Enabled || mother == null) return;
                // a record marked lost the same day still gets its child: the newest one for this mother wins
                var b = All.Where(x => !x.Born && x.MotherId == mother.StringId).OrderByDescending(x => x.ConceivedDay).FirstOrDefault();
                if (b == null) b = Unrecorded(mother, children);
                if (b == null) return;
                b.Born = true; b.Lost = false;
                var father = Hero.Find(b.FatherId);
                if (children == null || children.Count == 0 || father == null) { Log.Info($"[bastards] the child of {mother.Name} and {b.FatherId} is stillborn"); return; }
                // a pregnancy from before this was kept: worked out now and settled by the rules below
                if (b.Met == null) b.Met = HaveMet(mother, father);
                b.Version = 2;
                var kids = children.Where(c => c != null).ToList();
                if (S.Get.BastardCustomPregnancy && Rng.Next(100) < S.Get.BastardStillbirthPercent && mother.Spouse != father)
                {
                    Log.Info($"[bastards] {mother.Name}'s child by {father.Name} is stillborn (own pregnancy rules)");
                    WorldSim.Say(L.F("AIL_e7b1de28", $"{mother.Name} ha dado a luz un niño muerto."), Colors.Red);
                    foreach (var c in kids) Erase(b, c);
                    SaveFiles.Dirty();
                    return;
                }
                foreach (var c in kids) { b.Children.Add(c.StringId); b.BirthNames[c.StringId] = c.FirstName?.ToString() ?? c.Name.ToString(); }
                Log.Info($"[bastards] {mother.Name} gives birth to {kids.Count} child(ren) by {father.Name}");
                if (mother == P || father == P) foreach (var c in kids) PlayerChildCulture(c, mother);
                if (S.Get.BastardCustomPregnancy && mother != P && mother.Spouse != father && Rng.Next(100) < S.Get.BastardLaborDeathPercent)
                {
                    int session = WorldSim.Session;
                    MainThread.Post(() => { try { if (session == WorldSim.Session && mother.IsAlive) { Log.Info($"[bastards] {mother.Name} dies in labour"); KillCharacterAction.ApplyInLabor(mother, true); } } catch (Exception ex) { Log.Error("[bastards] death in labour", ex); } });
                }
                // married to each other by now: nothing to hide or name
                if (mother.Spouse == father) { foreach (var c in kids) Legitimize(b, c, quiet: true); SaveFiles.Dirty(); return; }
                if (mother == P || father == P)
                {
                    var other = mother == P ? father : mother;
                    // the other parent lives in the player's clan: the child stays there, nothing to ask
                    if (other.Clan == Clan.PlayerClan)
                    {
                        foreach (var c in kids) ToPlayerClan(b, c);
                        if (S.Get.BastardNotifications)
                        {
                            string names = string.Join(", ", kids.Select(c => c.FirstName?.ToString() ?? c.Name.ToString()));
                            InformationManager.DisplayMessage(new InformationMessage(mother == P
                                ? L.F("AIL_e7b1de2a", $"Has dado a luz a {names}, hijo de {father.Name}, que entra en tu casa.")
                                : L.F("AIL_e7b1de29", $"{mother.Name} ha dado a luz a {names}, que entra en tu casa."), Color.FromUint(0xFFFFC896)));
                        }
                        SaveFiles.Dirty(); return;
                    }
                    AskGuardian(b, kids, mother, father, other);
                    return;
                }
                RaiseWith(b, kids, mother, mother, father, father);
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[bastards] birth", ex); }
        }

        /// <summary>
        /// A child the game delivered whose father is not the mother's husband and that has no record (the pregnancy came from
        /// the game or another mod, or the marriage ended while she carried it): it is one born out of wedlock all the same.
        /// A widow's child by her late husband is not.
        /// </summary>
        private static Bastard Unrecorded(Hero mother, List<Hero> children)
        {
            try
            {
                var child = children?.FirstOrDefault(c => c != null);
                var father = child?.Father;
                if (child == null || father == null || father == mother || father.IsFemale || mother.Spouse == father || !father.IsAlive) return null;
                if (All.Any(x => x.Children.Contains(child.StringId))) return null;
                var b = new Bastard
                {
                    Id = WorldSim.NewId("bst"), MotherId = mother.StringId, FatherId = father.StringId, ConceivedDay = CampaignTime.Now.ToDays,
                    HusbandId = mother.Spouse != null && mother.Spouse.IsAlive ? mother.Spouse.StringId : null, Version = 2, Met = HaveMet(mother, father),
                };
                WorldSim.State.Bastards.Add(b);
                Log.Info($"[bastards] {child.Name} was born to {mother.Name} by {father.Name}, who is not her husband, without a record: handled as born out of wedlock");
                return b;
            }
            catch (Exception ex) { Log.Error("[bastards] unrecorded birth", ex); return null; }
        }

        /// <summary>A child of the player's out of wedlock takes the culture the setting says: the player's house, the mother's, or either.</summary>
        private static void PlayerChildCulture(Hero child, Hero mother)
        {
            try
            {
                var mine = Clan.PlayerClan?.Culture ?? P.Culture;
                var hers = mother?.Culture;
                int pick = 0;
                try { pick = S.Get.PlayerBastardCulture.SelectedIndex; } catch { }
                var c = pick == 1 ? hers : pick == 2 ? (Rng.Next(2) == 0 ? mine : hers) : mine;
                if (c != null && child.Culture != c) child.Culture = c;
            }
            catch (Exception ex) { Log.Warn("[bastards] culture of the player's child: " + ex.Message); }
        }

        /// <summary>Raised by the player, in the player's clan, as a lord. Still baseborn until acknowledged.</summary>
        private static void ToPlayerClan(Bastard b, Hero c)
        {
            if (c.Clan != Clan.PlayerClan) c.Clan = Clan.PlayerClan;
            if (c.Occupation != Occupation.Lord) c.SetNewOccupation(Occupation.Lord);
            Name(b, c);
        }

        /// <summary>The player is a parent: raise the child as your own in your clan, or leave it with the other parent.</summary>
        private static void AskGuardian(Bastard b, List<Hero> kids, Hero mother, Hero father, Hero other)
        {
            string names = string.Join(", ", kids.Select(c => c.FirstName?.ToString() ?? c.Name.ToString()));
            int session = WorldSim.Session;
            string question = mother == P
                ? L.F("AIL_e7b1d512", $"Has dado a luz a {names}. ¿Lo criarás como tuyo y lo acogerás en tu clan?")
                : L.F("AIL_e7b1d511", $"{mother.Name} ha dado a luz a {names}. ¿Lo criarás como tuyo y lo acogerás en tu clan?");
            InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1d510", "Ha nacido un hijo bastardo"), question,
                true, true, L.T("AIL_e7b1d513", "Sí"), L.T("AIL_e7b1d514", "No"),
                () =>
                {
                    if (session != WorldSim.Session || Campaign.Current == null) return;
                    try { foreach (var c in kids) ToPlayerClan(b, c); Log.Info("[bastards] the player raises the child in their clan"); SaveFiles.Dirty(); }
                    catch (Exception ex) { Log.Error("[bastards] guardianship: player's clan", ex); }
                },
                () =>
                {
                    if (session != WorldSim.Session || Campaign.Current == null) return;
                    try { RaiseWith(b, kids, other, mother, father, P); Log.Info("[bastards] the child stays with the other parent"); SaveFiles.Dirty(); }
                    catch (Exception ex) { Log.Error("[bastards] guardianship: other parent", ex); }
                }), true);
        }

        /// <summary>
        /// The child grows up with <paramref name="guardian"/>, in their clan (no clan: a wanderer). A married mother may pass
        /// it off as her husband's. Otherwise, if the mother knows who the father is, it comes out: a rumour naming him, and he
        /// pays for it (or <paramref name="culprit"/>: the player who would not raise it) with the guardian's spouse and head of house. A father the mother cannot name is never named or blamed:
        /// there is only talk in her town.
        /// </summary>
        private static void RaiseWith(Bastard b, List<Hero> kids, Hero guardian, Hero mother, Hero father, Hero culprit)
        {
            var cfg = S.Get;
            if (guardian.Clan == null)
            {
                foreach (var c in kids) { Name(b, c); c.Clan = null; if (c.Occupation != Occupation.Wanderer) c.SetNewOccupation(Occupation.Wanderer); }
                if (!cfg.BastardNoHouseWanderer) { foreach (var c in kids) Erase(b, c); return; }
                Log.Info($"[bastards] {guardian.Name} has no house: the child grows up a wanderer");
                return;
            }
            foreach (var c in kids)
            {
                if (c.Clan != guardian.Clan) c.Clan = guardian.Clan;
                if (c.Occupation != Occupation.Lord) c.SetNewOccupation(Occupation.Lord);
            }
            bool knows = FatherKnown(b);
            var husband = guardian.IsFemale ? guardian.Spouse : null;
            bool married = husband != null && husband.IsAlive && husband != father;
            // one roll for the whole birth
            if (married && Rng.Next(100) < cfg.BastardSecretKeptPercent)
            {
                foreach (var c in kids) if (!PassOff(b, c, mother, father, husband, knows)) Name(b, c);
                if (b.Secret) return;
            }
            else foreach (var c in kids) Name(b, c);
            bool player = mother == P || father == P || culprit == P;
            if (!knows)
            {
                if (player) LocalTalk(b, kids, mother, mother.Spouse != null && mother.Spouse.IsAlive && mother.Spouse != father);
                else TellFamily(guardian, mother, null, kids);
                Log.Info($"[bastards] {mother.Name} does not know who the father is"); return;
            }
            Scandal(b, guardian, culprit);
            if (player) PublicTalk(b, kids, mother, father);
            else TellFamily(guardian, mother, father, kids);
        }

        /// <summary>
        /// A child left with a parent who has no house, with "grows up a wanderer" off: taken out of the world quietly, as the
        /// game removes a notable (death by "Lost", no notice), after unlinking it from both parents so no family list keeps it.
        /// On the next frame, so the other listeners of this birth still get a live child.
        /// </summary>
        private static void Erase(Bastard b, Hero child)
        {
            int session = WorldSim.Session;
            MainThread.Post(() =>
            {
                if (session != WorldSim.Session || Campaign.Current == null || child == null || !child.IsAlive) return;
                try
                {
                    var kids = HarmonyLib.AccessTools.Field(typeof(Hero), "_children");
                    foreach (var parent in new[] { child.Father, child.Mother })
                        if (parent != null && kids?.GetValue(parent) is List<Hero> list) list.Remove(child);
                    HarmonyLib.AccessTools.Field(typeof(Hero), "_father")?.SetValue(child, null);
                    HarmonyLib.AccessTools.Field(typeof(Hero), "_mother")?.SetValue(child, null);
                    b.Children.Remove(child.StringId);
                    NewsTracker.ToldElsewhere = child.StringId;
                    KillCharacterAction.ApplyByRemove(child, false, true);
                    Log.Info($"[bastards] {child.Name} has no house to grow up in and is taken out of the world");
                    SaveFiles.Dirty();
                }
                catch (Exception ex) { Log.Error("[bastards] taking a child with no house out of the world", ex); }
            });
        }

        /// <summary>Kept secret: the world takes the child for the husband's. False if the game would not have it.</summary>
        private static bool PassOff(Bastard b, Hero child, Hero mother, Hero father, Hero husband, bool knows)
        {
            try
            {
                var field = HarmonyLib.AccessTools.Field(typeof(Hero), "_children");
                // the setter adds the child to the new father's list but never takes it off the old one
                while ((field?.GetValue(father) as IList)?.Contains(child) == true) ((IList)field.GetValue(father)).Remove(child);
                child.Father = husband;
                var theirs = field?.GetValue(husband) as IList;
                if (theirs != null) { int seen = 0; for (int i = theirs.Count - 1; i >= 0; i--) if (theirs[i] == child && ++seen > 1) theirs.RemoveAt(i); }
                b.Secret = true;
                // a mother who cannot name the father knows only that the child is not her husband's
                string fname = knows ? FatherNameFor(b, mother, father) : father.Name.ToString();
                var sec = StorySecrets.AddEvent($"{child.Name} is not the child of {husband.Name}: the true father is {fname}.",
                    L.F("AIL_e7b1c81a", $"{child.Name} no es hijo de {husband.Name}: su verdadero padre es {fname}."), new[] { mother, father, child },
                    knows ? new[] { mother, father } : new[] { father }, "high");
                b.SecretId = sec?.Id;
                Log.Info($"[bastards] {child.Name} is passed off as the child of {husband.Name}");
                return true;
            }
            catch (Exception ex) { Log.Error("[bastards] saved secret", ex); return false; }
        }

        /// <summary>
        /// The father is known: whoever is to blame (the father, or the player who would not raise the child) loses relation with the guardian's spouse and head of house.
        /// Paid once, and not when the guardian is of the player's own clan.
        /// </summary>
        private static void Scandal(Bastard b, Hero guardian, Hero culprit)
        {
            var cfg = S.Get;
            if (b.ScandalPaid || b.Version < 2 || guardian == null || culprit == null) return;
            b.ScandalPaid = true;
            if (!cfg.BastardConsequences || guardian.Clan == Clan.PlayerClan) return;
            try
            {
                var spouse = guardian.Spouse;
                if (spouse != null && spouse.IsAlive && spouse != culprit)
                {
                    if (culprit == P) ChangeRelationAction.ApplyPlayerRelation(spouse, -cfg.BastardSpouseRelationLoss, false, true);
                    else ChangeRelationAction.ApplyRelationChangeBetweenHeroes(culprit, spouse, -cfg.BastardSpouseRelationLoss, false);
                }
                var head = guardian.Clan?.Leader;
                if (head != null && head != guardian && head != culprit && head.IsAlive)
                {
                    if (culprit == P) ChangeRelationAction.ApplyPlayerRelation(head, -cfg.BastardClanLeaderRelationLoss, false, true);
                    else ChangeRelationAction.ApplyRelationChangeBetweenHeroes(culprit, head, -cfg.BastardClanLeaderRelationLoss, false);
                }
                Log.Info($"[bastards] scandal: {culprit.Name} pays for the child raised by {guardian.Name}");
            }
            catch (Exception ex) { Log.Error("[bastards] scandal", ex); }
        }

        /// <summary>Between other characters: no rumour, only the guardian's spouse and head of house learn of it (Illegitimate Children).</summary>
        private static void TellFamily(Hero guardian, Hero mother, Hero father, List<Hero> kids)
        {
            try
            {
                string names = string.Join(", ", kids.Select(c => c.Name.ToString()));
                string by = father != null ? $"by {father.Name}" : "by a man she cannot name";
                string text = $"[You learned: {mother.Name} bore {names}, a child out of wedlock {by}. It is raised in {guardian.Clan?.Name}.]";
                foreach (var h in new[] { guardian.Spouse, guardian.Clan?.Leader }.Where(x => x != null && x.IsAlive && x != guardian && x != P).Distinct())
                    MemoryStore.Get(h.StringId, h.Name.ToString()).Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays });
                Log.Info($"[bastards] the birth stays within the family of {guardian.Name}");
            }
            catch (Exception ex) { Log.Error("[bastards] telling the family", ex); }
        }

        /// <summary>Talk that names the father: a rumour, never "everyone knows".</summary>
        private static void PublicTalk(Bastard b, List<Hero> kids, Hero mother, Hero father)
        {
            try
            {
                string fname = FatherNameFor(b, mother, father), names = string.Join(", ", kids.Select(c => c.Name.ToString()));
                // the name she knows the player by may not be his own: then the talk does not point at him
                bool real = fname == father.Name.ToString();
                var heroes = new List<string> { mother.StringId }; heroes.AddRange(kids.Select(c => c.StringId));
                if (real) heroes.Add(father.StringId);
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 2,
                    Text = $"It is said that {mother.Name} has borne a child out of wedlock, {names}, and that the father is {fname}.",
                    TitleEs = L.F("AIL_e7b1d503", $"Se habla de un hijo ilegítimo de {fname}"),
                    TextEs = L.F("AIL_e7b1d504", $"Se dice que {mother.Name} ha dado a luz a {names} fuera del matrimonio, y que el padre es {fname}."),
                    Kingdoms = new[] { mother.MapFaction, real ? father.MapFaction : null }.Where(f => f != null).Select(f => f.StringId).Distinct().ToList(),
                    Heroes = heroes,
                });
            }
            catch (Exception ex) { Log.Error("[bastards] rumour", ex); }
        }

        /// <summary>A birth out of wedlock with no father named: talk in the mother's own town, at most.</summary>
        private static void LocalTalk(Bastard b, List<Hero> kids, Hero mother, bool married)
        {
            try
            {
                var place = mother.CurrentSettlement ?? mother.PartyBelongedTo?.CurrentSettlement ?? mother.HomeSettlement;
                if (place == null) return;
                string names = string.Join(", ", kids.Select(c => c.Name.ToString()));
                string text, local;
                if (married)
                {
                    text = $"It is whispered that {names}, the child {mother.Name} has borne, is not her husband's, and no one knows whose it is.";
                    local = L.F("AIL_e7b1d507", $"Se murmura que {names}, el hijo que ha tenido {mother.Name}, no es de su marido, y nadie sabe de quién es.");
                }
                else
                {
                    text = $"{mother.Name} has given birth to {names} out of wedlock. No one knows who the father is.";
                    local = L.F("AIL_e7b1d505", $"{mother.Name} ha dado a luz a {names} fuera del matrimonio. Nadie sabe quién es el padre.");
                }
                var heroes = new List<string> { mother.StringId }; heroes.AddRange(kids.Select(c => c.StringId));
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 1, SettlementId = place.StringId, NoReaction = true,
                    Text = text, TitleEs = L.T("AIL_e7b1c816", "Un hijo fuera del matrimonio"), TextEs = local,
                    Kingdoms = mother.MapFaction != null ? new List<string> { mother.MapFaction.StringId } : new List<string>(),
                    Heroes = heroes,
                });
            }
            catch (Exception ex) { Log.Error("[bastards] local talk", ex); }
        }

        // ============================================================ who knows who the father is

        /// <summary>
        /// Whether the mother had met the father: for the player, she remembers him (they have talked) or knows him from
        /// family, clan or dealings; for anyone else, they share a house, a relation or a memory of each other.
        /// </summary>
        private static bool HaveMet(Hero mother, Hero father)
        {
            try
            {
                if (mother == null || father == null) return false;
                if (mother == P) return true;
                if (father == P)
                {
                    var sp = Speaker.FromHero(mother);
                    if (sp.Memory != null && sp.Memory.ConversationCount > 0) return true;
                    string why = ContextBuilder.WhyKnowsPlayer(sp);
                    return why == "family" || why == "clan" || why == "past_dealings";
                }
                if (mother.Clan != null && mother.Clan == father.Clan) return true;
                if (mother.GetRelation(father) != 0) return true;
                return Speaker.FromHero(mother).Memory?.Others?.ContainsKey(father.StringId) == true;
            }
            catch { return false; }
        }

        private static bool MetOf(Bastard b)
        {
            if (b.Met.HasValue) return b.Met.Value;
            return HaveMet(Hero.Find(b.MotherId), Hero.Find(b.FatherId));
        }

        /// <summary>The mother knows who fathered the child: she met him and, if it is the player, knows his name.</summary>
        public static bool FatherKnown(Bastard b)
        {
            try
            {
                if (b == null) return false;
                if (b.Acknowledged) return true;
                var mother = Hero.Find(b.MotherId); var father = Hero.Find(b.FatherId);
                if (mother == null || father == null) return b.Met != false;
                if (mother == P) return true;
                if (!MetOf(b)) return false;
                return father != P || ContextBuilder.PlayerNameFor(Speaker.FromHero(mother)) != null;
            }
            catch { return false; }
        }

        /// <summary>Talk names the father: the mother knows him and has not kept quiet (or he owned the child himself).</summary>
        private static bool FatherPublic(Bastard b) => b.Acknowledged || (!b.Secret && FatherKnown(b));

        /// <summary>The name the mother knows the father by (the player may have given her another), or a description.</summary>
        private static string FatherNameFor(Bastard b, Hero mother, Hero father)
        {
            if (father == null) return "someone";
            if (father != P || b.Acknowledged || mother == P) return father.Name.ToString();
            try { return ContextBuilder.PlayerNameFor(Speaker.FromHero(mother)) ?? "a man whose name she never learned"; }
            catch { return "a man whose name she never learned"; }
        }

        /// <summary>The baseborn surname, after the mother's culture. The birth name is kept to give back later.</summary>
        private static void Name(Bastard b, Hero child)
        {
            try
            {
                var mother = Hero.Find(b.MotherId);
                string culture = (mother?.Culture?.StringId ?? "other").ToLowerInvariant();
                string line = Diplomacy.Line(TSurnames, culture);
                if (line.Length == 0) line = Diplomacy.Line(TSurnames, "other");
                var names = line.Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
                if (names.Count == 0) return;
                string first = child.FirstName?.ToString() ?? child.Name.ToString();
                if (!b.BirthNames.ContainsKey(child.StringId)) b.BirthNames[child.StringId] = first;
                if (!S.Get.BastardSurnames) return;
                string full = first + " " + names[Rng.Next(names.Count)];
                child.SetName(new TextObject("{=!}" + full), new TextObject("{=!}" + first));
            }
            catch (Exception ex) { Log.Error("[bastards] surname", ex); }
        }

        // ============================================================ legitimation

        public static Bastard RecordOf(Hero child) => child == null ? null : All.FirstOrDefault(b => b.Children.Contains(child.StringId));
        public static bool IsBaseborn(Hero child)
        {
            var b = RecordOf(child);
            return b != null && !b.Secret && !b.Legitimized.Contains(child.StringId);
        }

        /// <summary>
        /// Takes the baseborn surname off. <paramref name="move"/> (the parents marrying each other): the child also goes to
        /// their house, the player's if the player is one of them; otherwise it stays where it grew up.
        /// </summary>
        public static void Legitimize(Bastard b, Hero child, bool quiet = false, bool move = false)
        {
            try
            {
                if (b == null || child == null || b.Legitimized.Contains(child.StringId)) return;
                if (b.BirthNames.TryGetValue(child.StringId, out string first) && !string.IsNullOrWhiteSpace(first))
                    child.SetName(new TextObject("{=!}" + first), new TextObject("{=!}" + first));
                var father = Hero.Find(b.FatherId); var mother = Hero.Find(b.MotherId);
                var home = father == P || mother == P ? Clan.PlayerClan : father?.Clan ?? mother?.Clan;
                if (move && home != null && !home.IsBanditFaction && child.Clan != home && child.Spouse == null && child.Clan?.Leader != child) child.Clan = home;
                if (b.SecretId != null) StorySecrets.Reveal(b.SecretId, null);
                // marked only once it is all done, so a failure can be tried again
                b.Legitimized.Add(child.StringId);
                Log.Info($"[bastards] {child.Name} is legitimized");
                if (!quiet) WorldSim.Say(L.F("AIL_e7b1c81d", $"{child.Name} es ahora hijo legítimo."), Colors.Yellow);
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[bastards] legitimize", ex); }
        }

        /// <summary>Marriages: the parents marrying make their children legitimate; so does a baseborn child marrying, if set.</summary>
        public static void OnMarried(Hero a, Hero b, bool show)
        {
            try
            {
                if (!Enabled || a == null || b == null) return;
                var woman = a.IsFemale && !b.IsFemale ? a : b.IsFemale && !a.IsFemale ? b : null;
                if (woman?.Clan != null)
                {
                    Clan after = null;
                    try { after = Campaign.Current.Models.MarriageModel.GetClanAfterMarriage(a, b); } catch { }
                    if (after != null && after != woman.Clan)
                        foreach (var c in woman.Children.ToList())
                            if (c != null && c.IsAlive && RecordOf(c) != null && c.Clan == woman.Clan && c.Clan?.Leader != c && c.Spouse == null)
                            { c.Clan = after; Log.Info($"[bastards] {c.Name} follows their mother {woman.Name} to {after.Name}"); }
                }
                if (S.Get.BastardLegitimizeParentsMarry)
                    foreach (var r in All.Where(x => x.Born && ((x.MotherId == a.StringId && x.FatherId == b.StringId) || (x.MotherId == b.StringId && x.FatherId == a.StringId))).ToList())
                        foreach (var id in r.Children.ToList()) { var c = Hero.Find(id); if (c != null && c.IsAlive) Legitimize(r, c, move: true); }
                if (S.Get.BastardLegitimizeOnMarriage)
                    foreach (var h in new[] { a, b }) if (IsBaseborn(h)) Legitimize(RecordOf(h), h);
            }
            catch (Exception ex) { Log.Error("[bastards] wedding", ex); }
        }

        /// <summary>Heading a house makes one legitimate.</summary>
        public static void OnClanLeaderChanged(Hero oldLeader, Hero newLeader)
        {
            try { if (Enabled && S.Get.BastardLegitimizeHeirs && newLeader != null && IsBaseborn(newLeader)) Legitimize(RecordOf(newLeader), newLeader); }
            catch (Exception ex) { Log.Error("[bastards] clan leader", ex); }
        }

        /// <summary>The player, as parent, acknowledges an adult baseborn child.</summary>
        public static bool CanAcknowledge(Hero child)
        {
            var b = RecordOf(child);
            return child != null && Enabled && b != null && IsBaseborn(child) && child.Age >= 18 && (b.FatherId == P?.StringId || b.MotherId == P?.StringId);
        }

        /// <summary>
        /// Influence that acknowledging a child costs the player, as in the Bastard Children mod: the setting's amount,
        /// twice that for someone who does not rule a kingdom. 0 = free.
        /// </summary>
        public static int LegitimizeCost()
        {
            int cost = Math.Max(0, S.Get.BastardLegitimizeInfluenceCost);
            bool rules = P?.Clan?.Kingdom != null && P.Clan.Kingdom.Leader == P;
            return rules ? cost : cost * 2;
        }

        /// <summary>For the dialogue line: greyed out, with the reason, while the player's clan lacks the influence.</summary>
        public static bool CanPayLegitimize(out TaleWorlds.Localization.TextObject why)
        {
            int cost = LegitimizeCost();
            why = null;
            if (cost <= 0 || (Clan.PlayerClan != null && Clan.PlayerClan.Influence >= cost)) return true;
            why = new TaleWorlds.Localization.TextObject("{=!}" + L.F("AIL_e7b1db01", $"Necesitas {cost} de influencia."));
            return false;
        }

        /// <summary>The player owns the adult child as theirs: legitimate, into the player's clan as a lord, for the influence it costs.</summary>
        public static void Acknowledge(Hero child)
        {
            var b = RecordOf(child);
            if (b == null || child == null) return;
            try
            {
                int cost = LegitimizeCost();
                if (cost > 0 && Clan.PlayerClan != null)
                {
                    if (Clan.PlayerClan.Influence < cost) { Log.Warn($"[bastards] not enough influence to acknowledge {child.Name} ({Clan.PlayerClan.Influence:0} of {cost})"); return; }
                    ChangeClanInfluenceAction.Apply(Clan.PlayerClan, -cost);
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1db02", $"Has gastado {cost} de influencia."), Colors.White));
                }
                b.Acknowledged = true;
                Legitimize(b, child, quiet: true);
                if (child.Clan != Clan.PlayerClan && child.Clan?.Leader != child) child.Clan = Clan.PlayerClan;
                if (child.Occupation != Occupation.Lord) child.SetNewOccupation(Occupation.Lord);
                WorldSim.Say(L.F("AIL_e7b1d50e", $"{child.Name} es ahora hijo legítimo tuyo y entra en tu clan."), Colors.Yellow);
                Log.Info($"[bastards] the player acknowledges {child.Name} ({cost} influence)");
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[bastards] acknowledge", ex); }
        }

        // ============================================================ daily: lost pregnancies

        public static void OnDailyTick()
        {
            try
            {
                if (!Enabled) return;
                double now = CampaignTime.Now.ToDays;
                foreach (var b in All.Where(x => !x.Born && !x.Lost).ToList())
                {
                    var mother = Hero.Find(b.MotherId);
                    if (mother != null && mother.IsAlive && mother.IsPregnant) continue;
                    if (now - b.ConceivedDay < 1) continue;
                    b.Lost = true;
                    Log.Info($"[bastards] {b.MotherId} loses the pregnancy");
                    if (mother != null && mother.IsAlive)
                    {
                        try { AILegacy.Api.AILegacyApi.AddMoodCause(mother, "You lost the child you were carrying.", -1, 3, 14); } catch { }
                        if (b.FatherId == P?.StringId) WorldSim.Say(L.F("AIL_e7b1c81e", $"{mother.Name} ha perdido el hijo que esperaba de ti."), Colors.Gray);
                    }
                }
                WorldSim.State.Bastards.RemoveAll(x => x.Lost && now - x.ConceivedDay > 400);
            }
            catch (Exception ex) { Log.Error("[bastards] day", ex); }
        }

        /// <summary>
        /// The MCM button (Illegitimate Children's «Check pregnancies»): each pregnancy out of wedlock being kept, with when it
        /// is due, and those that ended without a birth closed as lost (the mother died or is no longer carrying, or a repeat).
        /// </summary>
        public static void CheckPregnancies()
        {
            try
            {
                if (Campaign.Current == null || WorldSim.State?.Bastards == null) return;
                var open = All.Where(x => !x.Born && !x.Lost).ToList();
                if (open.Count == 0) { InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1de2b", "No hay embarazos registrados."), Colors.Gray)); return; }
                var due = new Dictionary<Hero, CampaignTime>();
                var q = GameQueue();
                if (q != null) foreach (var e in q) { var m = MotherOf(e); if (m != null && e.GetType().GetField("DueDate")?.GetValue(e) is CampaignTime t) due[m] = t; }
                var seen = new HashSet<string>();
                int ok = 0, lost = 0;
                foreach (var b in open)
                {
                    var mother = Hero.Find(b.MotherId); var father = Hero.Find(b.FatherId);
                    string mn = mother?.Name?.ToString() ?? b.MotherId, fn = father?.Name?.ToString() ?? b.FatherId;
                    string why = mother == null || !mother.IsAlive ? L.T("AIL_e7b1de2f", "la madre ha muerto")
                               : !mother.IsPregnant ? L.T("AIL_e7b1de30", "la madre ya no está embarazada")
                               : !seen.Add(b.MotherId) ? L.T("AIL_e7b1de31", "registro repetido") : null;
                    if (why == null)
                    {
                        ok++;
                        int days = due.TryGetValue(mother, out var t) ? Math.Max(0, (int)Math.Ceiling(t.ToDays - CampaignTime.Now.ToDays)) : -1;
                        InformationManager.DisplayMessage(new InformationMessage(days >= 0 ? L.F("AIL_e7b1de2d", $"{mn}, de {fn}: nace en {days} días.") : L.F("AIL_e7b1de2e", $"{mn}, de {fn}: en curso."), Colors.White));
                    }
                    else
                    {
                        lost++;
                        b.Lost = true;
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1de32", $"{mn}, de {fn}: se da por perdido ({why})."), Colors.Gray));
                    }
                }
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1de2c", $"Embarazos: {ok} en curso, {lost} cerrados por perdidos."), Colors.Yellow));
                Log.Info($"[bastards] check: {ok} pregnancies going on, {lost} closed as lost");
                if (lost > 0) SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[bastards] check pregnancies", ex); }
        }

        public static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool show)
        {
            try
            {
                if (!Enabled || victim == null || !victim.IsFemale) return;
                foreach (var b in All.Where(x => !x.Born && !x.Lost && x.MotherId == victim.StringId)) b.Lost = true;
            }
            catch { }
        }

        // ============================================================ what characters know

        public static string DescribeFor(Speaker s)
        {
            try
            {
                if (!Enabled || s == null || !s.IsHero) return "";
                var h = s.Hero; string id = h.StringId;
                var lines = new List<string>();
                string player = P?.Name?.ToString() ?? "";
                string N(string hid) => hid == P?.StringId ? player : Hero.Find(hid)?.Name?.ToString() ?? "someone";
                string Kids(Bastard b) => string.Join(", ", b.Children.Select(Hero.Find).Where(c => c != null).Select(c => c.Name.ToString()));
                foreach (var b in All.Where(x => !x.Lost))
                {
                    bool asMother = b.MotherId == id, asFather = b.FatherId == id, asHusband = b.HusbandId == id && b.Born && !b.Secret, asChild = b.Children.Contains(id);
                    if (!asMother && !asFather && !asHusband && !asChild) continue;
                    var mother = Hero.Find(b.MotherId); var father = Hero.Find(b.FatherId);
                    // what the mother knows is all anyone close to her knows: an unknown father is never named
                    bool knows = FatherKnown(b), isPublic = FatherPublic(b);
                    string fatherForMother = knows ? FatherNameFor(b, mother, father).Replace("she never learned", "you never learned")
                        : b.FatherId == P?.StringId && MetOf(b) ? "a stranger whose name you never learned (you would know his face)"
                        : "a man you cannot name: you do not know who the father is";
                    if (asMother)
                    {
                        string state = !b.Born ? "You are carrying a child" : "You have " + (b.Children.Count == 1 ? "a child, " : "children, ") + Kids(b) + ",";
                        string secret = b.Secret ? ": the world believes " + (b.Children.Count == 1 ? "it is" : "they are") + " your husband's"
                            : b.Acknowledged ? "; the father has acknowledged the child as his before everyone"
                            : !b.Born || !knows ? ""
                            : b.Version < 2 ? "; it is known" : "; people talk, and it is rumoured who the father is";
                        lines.Add(TMother.Format("state", state, "father", fatherForMother, "secret", secret));
                    }
                    else if (asFather)
                    {
                        string theMother = b.MotherId == P?.StringId ? ContextBuilder.PlayerNameFor(s) ?? "a woman whose name you never learned" : N(b.MotherId);
                        string secret = b.Secret ? ": the world believes the child is her husband's"
                            : b.Acknowledged ? "; the child has been acknowledged before everyone"
                            : !knows ? "; she does not know the child is yours"
                            : !b.Born ? ""
                            : b.Version < 2 ? "" : "; it is rumoured that you are the father";
                        lines.Add(TFather.Format("state", (!b.Born ? "She is carrying your child" : "You have " + (b.Children.Count == 1 ? "a child, " : "children, ") + Kids(b) + ","),
                            "mother", theMother, "secret", secret));
                    }
                    else if (asHusband)
                        lines.Add(THusband.Format("mother", N(b.MotherId), "father", isPublic ? FatherNameFor(b, mother, father) : "another man (you do not know who)", "children", Kids(b)));
                    else if (asChild)
                    {
                        string fatherForChild = knows ? FatherNameFor(b, mother, father).Replace("she never learned", "your mother never learned") : "unknown: your mother never knew who he was";
                        if (b.Legitimized.Contains(id)) lines.Add(TLegitChild.Format("father", knows ? fatherForChild : "an unknown father", "mother", N(b.MotherId)));
                        else if (!b.Secret) lines.Add(TChild.Format("father", fatherForChild, "known", "",
                            "heir", h.Clan?.Leader == h ? "" : " and cannot inherit unless someone makes you so"));
                    }
                }
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }

        /// <summary>For the conversation panel: "expecting your child" or how many children out of wedlock you have together.</summary>
        public static string PanelLine(Hero h)
        {
            try
            {
                if (!Enabled || h == null || P == null) return "";
                var mine = All.Where(b => !b.Lost && ((b.MotherId == h.StringId && b.FatherId == P.StringId) || (b.FatherId == h.StringId && b.MotherId == P.StringId))).ToList();
                if (mine.Count == 0) return "";
                if (mine.Any(b => !b.Born)) return L.T("AIL_e7b1c81f", "Espera un hijo tuyo");
                int n = mine.Sum(b => b.Children.Count(id => Hero.Find(id)?.IsAlive == true));
                return n > 0 ? L.F("AIL_e7b1c820", $"{n} hijo(s) en común fuera del matrimonio") : "";
            }
            catch { return ""; }
        }

        // ============================================================ console

        public static string Report()
        {
            var lines = new List<string>();
            foreach (var b in All)
                lines.Add($"{b.Id}: {Hero.Find(b.MotherId)?.Name}+{Hero.Find(b.FatherId)?.Name} día {(int)b.ConceivedDay} {(b.Lost ? "perdido" : b.Born ? "nacido: " + string.Join(", ", b.Children.Select(id => Hero.Find(id)?.Name?.ToString() ?? id)) : "en camino")}{(b.Secret ? " (secreto)" : "")}{(!FatherKnown(b) ? " (padre desconocido para la madre)" : "")}{(b.Acknowledged ? " (reconocido)" : "")}");
            return lines.Count == 0 ? "Sin registros." : string.Join("\n", lines);
        }
    }

    /// <summary>Console commands to test the bastards without waiting weeks of game time.</summary>
    internal static class BastardCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("pregnancies", "ailegacy")]
        public static string Pregnancies(List<string> args) => Campaign.Current == null ? "Sin campaña." : Bastards.Report();

        [CommandLineFunctionality.CommandLineArgumentFunction("conceive", "ailegacy")]
        public static string ConceiveCmd(List<string> args)
        {
            if (Campaign.Current == null || args == null || args.Count < 2) return "Uso: ailegacy.conceive <madre> <padre> (nombres sin espacios o con _)";
            if (string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1])) return "Uso: ailegacy.conceive <madre> <padre>";
            Hero Find(string n) { n = n.Replace('_', ' ').ToLowerInvariant(); return Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant() == n) ?? Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant().Contains(n)); }
            var m = Find(args[0]); var f = Find(args[1]);
            if (m == null || f == null) return "No encuentro a uno de los dos.";
            return Bastards.Conceive(m, f) ? $"{m.Name} espera un hijo de {f.Name}." : "No ha podido ser (¿ya embarazada, mismo sexo, cónyuges o el ajuste apagado?).";
        }
    }
}
