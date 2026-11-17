using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.Core;

namespace AILegacy.World
{
    /// <summary>A faith of Calradia (world_data\faiths.json).</summary>
    public class Faith
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("description")] public string Description;
        /// <summary>How its people greet, swear, curse and bless: background, not words to repeat.</summary>
        [JsonProperty("speech")] public string Speech;
        [JsonProperty("cultures")] public List<string> Cultures = new List<string>();
        [JsonProperty("weight")] public float Weight = 1f;
    }

    /// <summary>
    /// The core of each character, decided by the game before any AI request and given to the AI as fixed facts: one
    /// archetype (and at times a weaker second one) drawn by weight from archetypes.json, their nature (principled,
    /// pragmatic, ruthless), how much they swear, how devout they are, and their faith (mostly their family's). The draw
    /// favours archetypes their game traits agree with, and avoids the ones their neighbours and the rest of the world
    /// already have, so no village is full of copies.
    /// </summary>
    internal static partial class Lore
    {
        private static readonly Random CoreRng = new Random();
        private static readonly Cache<List<Faith>> FaithsFile = new Cache<List<Faith>>("faiths.json", DefaultFaiths);

        private static T Embedded<T>(string name) where T : class
        {
            try
            {
                using (var st = typeof(Lore).Assembly.GetManifestResourceStream("AILegacy.Data." + name))
                {
                    if (st == null) return null;
                    using (var r = new StreamReader(st, Encoding.UTF8)) return JsonConvert.DeserializeObject<T>(r.ReadToEnd());
                }
            }
            catch (Exception ex) { Log.Warn($"Built-in {name}: {ex.Message}"); return null; }
        }

        private static List<Faith> DefaultFaiths() => Embedded<List<Faith>>("faiths.json") ?? new List<Faith>();

        public static List<Faith> Faiths() => FaithsFile.Value ?? new List<Faith>();
        public static Faith FaithById(string id) => string.IsNullOrEmpty(id) ? null : Faiths().FirstOrDefault(f => f.Id == id);

        private static bool _migrated;

        /// <summary>
        /// The module's archetypes.json from older versions (the 24 short archetypes) is replaced once by the new library;
        /// the old file is kept beside it as archetypes.old.json. A file the player edited is left alone.
        /// </summary>
        private static void MigrateArchetypes()
        {
            if (_migrated) return;
            _migrated = true;
            try
            {
                string path = Path.Combine(Dir, "archetypes.json");
                if (!File.Exists(path)) return;
                var have = JsonConvert.DeserializeObject<List<Archetype>>(File.ReadAllText(path, Encoding.UTF8)) ?? new List<Archetype>();
                if (have.Any(a => !string.IsNullOrWhiteSpace(a.Hook))) return;
                var oldIds = new HashSet<string>(OldDefaultArchetypes().Select(a => a.Id));
                if (have.Count == 0 || !have.All(a => oldIds.Contains(a.Id ?? ""))) return;
                File.Copy(path, Path.Combine(Dir, "archetypes.old.json"), true);
                File.WriteAllText(path, JsonConvert.SerializeObject(DefaultArchetypes(), Formatting.Indented), new UTF8Encoding(false));
                ArchetypesFile.Forget();
                Log.Info("world_data\\archetypes.json: the old 24 archetypes were replaced by the new library (old file kept as archetypes.old.json)");
            }
            catch (Exception ex) { Log.Warn("Archetypes update: " + ex.Message); }
        }

        public static List<Archetype> AllArchetypes() { MigrateArchetypes(); return ArchetypesFile.Value ?? new List<Archetype>(); }
        public static Archetype ArchetypeById(string id) => string.IsNullOrEmpty(id) ? null : AllArchetypes().FirstOrDefault(a => a.Id == id);

        // ============================================================ who they are, for the draw

        /// <summary>The roles of a person, in the words archetypes.json uses (the old names too, for files written before).</summary>
        internal static HashSet<string> RolesFor(Speaker s)
        {
            var r = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            bool child = s.Age < 18f || s.KnownChild;
            if (s.IsHero)
            {
                var h = s.Hero;
                if (child) { r.Add(h.IsLord ? "child_noble" : "child"); return r; }
                if (h.IsKingdomLeader) { r.Add("ruler"); r.Add("noble"); }
                if (h.IsLord) { r.Add("noble"); r.Add("lord"); }
                if (h.IsLord && h.Clan?.Leader == h) r.Add("clan_leader");
                if (h.Clan != null && h.Clan.IsClanTypeMercenary) r.Add("mercenary");
                if (h.Clan != null && h.Clan.IsBanditFaction) r.Add("bandit");
                if (h.IsWanderer) { r.Add("wanderer"); r.Add("companion"); }
                if (h.IsPlayerCompanion) r.Add("companion");
                if (h.IsMerchant) { r.Add("merchant"); r.Add("trader"); r.Add("notable"); r.Add("town_notable"); }
                if (h.IsArtisan) { r.Add("artisan"); r.Add("craftsman"); r.Add("notable"); r.Add("town_notable"); }
                if (h.IsGangLeader) { r.Add("gang_leader"); r.Add("gang"); }
                if (h.IsHeadman) { r.Add("headman"); r.Add("rural_notable"); r.Add("villager"); r.Add("village_notable"); }
                if (h.IsRuralNotable) { r.Add("rural_notable"); r.Add("villager"); r.Add("village_notable"); }
                if (h.IsPreacher) { r.Add("notable"); r.Add("town_notable"); }
                if (r.Count == 0) r.Add("commoner");
            }
            else
            {
                if (child) { r.Add("child"); return r; }
                string occ = s.Character?.Occupation.ToString() ?? "";
                string id = (s.Character?.StringId ?? "").ToLowerInvariant();
                if (occ == "Soldier") { r.Add("soldier"); r.Add("guard"); }
                else if (occ == "Guard" || occ == "PrisonGuard" || id.Contains("guard")) r.Add("guard");
                else if (occ == "Mercenary" || id.Contains("caravan_guard")) { r.Add("mercenary"); r.Add("soldier"); }
                else if (occ == "Bandit" || s.Band?.MapFaction?.IsBanditFaction == true) r.Add("bandit");
                else if (occ == "Tavernkeeper" || id.Contains("tavern") || id.Contains("musician") || id.Contains("barmaid")) r.Add("tavern");
                else if (occ.Contains("Trader") || occ == "Merchant" || occ == "ShopWorker" || id.Contains("trader") || id.Contains("shop")) { r.Add("trader"); r.Add("merchant"); }
                else if (occ == "Blacksmith" || occ == "Armorer" || occ == "Weaponsmith" || id.Contains("smith")) { r.Add("craftsman"); r.Add("artisan"); }
                else if (occ == "RansomBroker" || id.Contains("ransom")) r.Add("trader");
                else if (occ == "GangLeader" || id.Contains("gang")) r.Add("gang_leader");
                var st = s.ResidentSettlement;
                if (s.Band != null && s.Band.IsVillager) r.Add("villager");
                else if (st != null && st.IsVillage) r.Add("villager");
                else r.Add("townsfolk");
                r.Add("commoner");
            }
            return r;
        }

        private static int TraitOf(Speaker s, string trait)
        {
            if (!s.IsHero) return 0;
            try
            {
                var t = trait == "Honor" ? DefaultTraits.Honor : trait == "Mercy" ? DefaultTraits.Mercy : trait == "Valor" ? DefaultTraits.Valor
                      : trait == "Generosity" ? DefaultTraits.Generosity : trait == "Calculating" ? DefaultTraits.Calculating : null;
                return t == null ? 0 : s.Hero.GetTraitLevel(t);
            }
            catch { return 0; }
        }

        private static bool Fits(Archetype a, Speaker s, HashSet<string> roles, bool child)
        {
            var aroles = a.Roles ?? new List<string>();
            bool forChildren = aroles.Any(x => x == "child" || x == "child_noble");
            if (aroles.Count > 0 && forChildren != child) return false;
            if (aroles.Count > 0 && !aroles.Any(roles.Contains)) return false;
            if (a.Cultures != null && a.Cultures.Count > 0 && !a.Cultures.Contains(s.Culture?.StringId ?? "")) return false;
            float age = s.Age;
            if (age > 0 && a.MinAge.HasValue && age < a.MinAge.Value) return false;
            if (age > 0 && a.MaxAge.HasValue && age > a.MaxAge.Value) return false;
            if (!string.IsNullOrEmpty(a.Gender) && (a.Gender == "female") != s.IsFemale) return false;
            return true;
        }

        /// <summary>Archetype ids taken around them (clan, place) and how many characters in the world have each.</summary>
        private static void TakenAround(Speaker s, out HashSet<string> near, out Dictionary<string, int> world)
        {
            near = new HashSet<string>();
            world = new Dictionary<string, int>();
            try
            {
                foreach (var m in MemoryStore.AllHeroes)
                {
                    foreach (var id in new[] { m.ArchetypeId, m.Archetype2Id })
                        if (!string.IsNullOrEmpty(id)) world[id] = (world.TryGetValue(id, out var n) ? n : 0) + 1;
                }
                var people = new List<Hero>();
                if (s.IsHero && s.Hero.Clan != null) people.AddRange(s.Hero.Clan.Heroes);
                var st = s.Settlement ?? s.HomeSettlement;
                if (st != null) { people.AddRange(st.Notables); people.AddRange(st.HeroesWithoutParty); foreach (var p in st.Parties) if (p?.LeaderHero != null) people.Add(p.LeaderHero); }
                foreach (var h in people)
                {
                    if (h == null || (s.IsHero && h == s.Hero)) continue;
                    var m = MemoryStore.Find(h.StringId);
                    if (!string.IsNullOrEmpty(m?.ArchetypeId)) near.Add(m.ArchetypeId);
                    if (!string.IsNullOrEmpty(m?.Archetype2Id)) near.Add(m.Archetype2Id);
                }
            }
            catch { }
        }

        private static double WeightOf(Archetype a, Speaker s, bool child, HashSet<string> near, Dictionary<string, int> world)
        {
            double w = Math.Max(0.01, a.Weight);
            if (a.Roles != null && a.Roles.Count > 0 && !child) w *= 2.5;
            if (a.Cultures != null && a.Cultures.Count > 0) w *= 1.2;
            if (a.Traits != null)
                foreach (var kv in a.Traits)
                {
                    int mine = TraitOf(s, kv.Key);
                    if (mine == 0 || kv.Value == 0) continue;
                    bool same = Math.Sign(mine) == Math.Sign(kv.Value);
                    int strength = Math.Min(2, Math.Abs(kv.Value));
                    w *= same ? (strength >= 2 ? 3.5 : 2.25) : (strength >= 2 ? 0.1 : 0.3);
                }
            if (near.Contains(a.Id)) w *= 0.08;
            if (world.TryGetValue(a.Id ?? "", out var n)) w /= 1.0 + 0.25 * n;
            return w;
        }

        private static T Roulette<T>(List<(T item, double w)> pool)
        {
            double total = pool.Sum(x => Math.Max(0, x.w));
            if (total <= 0) return default(T);
            double roll = CoreRng.NextDouble() * total;
            foreach (var x in pool) { roll -= Math.Max(0, x.w); if (roll <= 0) return x.item; }
            return pool[pool.Count - 1].item;
        }

        private static string RollOf(params (string v, double w)[] opts) => Roulette(opts.Select(o => (o.v, o.w)).ToList());

        /// <summary>
        /// Gives a character their core once: archetype (and maybe a second), nature, swearing, piety and faith. What is
        /// already set (by hand in the editor, or from an older version) is kept; only what is missing is drawn.
        /// </summary>
        public static void EnsureCore(Speaker s)
        {
            var mem = s?.Memory;
            if (mem == null) return;
            try
            {
                var cfg = S.Get;
                bool child = s.Age < 18f || s.KnownChild;
                var roles = RolesFor(s);
                if (cfg.EnableArchetypes && string.IsNullOrEmpty(mem.ArchetypeId) && string.IsNullOrEmpty(mem.Archetype))
                {
                    TakenAround(s, out var near, out var world);
                    var pool = AllArchetypes().Where(a => !string.IsNullOrEmpty(a.Id) && Fits(a, s, roles, child)).Select(a => (a, WeightOf(a, s, child, near, world))).ToList();
                    var first = pool.Count > 0 ? Roulette(pool) : null;
                    if (first != null)
                    {
                        mem.ArchetypeId = first.Id; mem.Archetype = first.Name; mem.ArchetypeText = first.Core;
                        if (CoreRng.NextDouble() < 0.45)
                        {
                            var second = Roulette(pool.Where(x => x.a != first && x.a.Secondary
                                                                 && !(first.Conflicts ?? new List<string>()).Contains(x.a.Id) && !(x.a.Conflicts ?? new List<string>()).Contains(first.Id)
                                                                 && MoralsAgree(first.Morality, x.a.Morality)).ToList());
                            if (second != null) mem.Archetype2Id = second.Id;
                        }
                        if (HasOwn(first.Morality)) mem.Morality = first.Morality;
                        if (HasOwn(first.Profanity)) mem.Profanity = first.Profanity;
                        if (HasOwn(first.Piety)) mem.Piety = first.Piety;
                    }
                }
                int virtue = TraitOf(s, "Mercy") + TraitOf(s, "Honor");
                if (string.IsNullOrEmpty(mem.Morality))
                    mem.Morality = child ? RollOf(("principled", 0.35 * Math.Pow(1.6, virtue)), ("pragmatic", 0.55), ("ruthless", 0.10 * Math.Pow(1.6, -virtue)))
                                         : RollOf(("principled", 0.28 * Math.Pow(1.6, virtue)), ("pragmatic", 0.42), ("ruthless", 0.30 * Math.Pow(1.6, -virtue)));
                if (string.IsNullOrEmpty(mem.Profanity))
                {
                    double none, mild, heavy;
                    if (child) { none = 0.65; mild = 0.32; heavy = 0.03; }
                    else if (roles.Contains("noble")) { none = 0.35; mild = 0.5; heavy = 0.15; }
                    else if (mem.Morality == "ruthless") { none = 0.15; mild = 0.5; heavy = 0.35; }
                    else { none = 0.25; mild = 0.55; heavy = 0.2; }
                    if (new[] { "soldier", "guard", "bandit", "mercenary", "tavern", "gang_leader", "villager", "craftsman" }.Any(roles.Contains)) heavy *= 1.4;
                    if (mem.Morality == "principled") { none *= 1.4; heavy *= 0.7; }
                    mem.Profanity = RollOf(("none", none), ("mild", mild), ("heavy", heavy));
                }
                if (string.IsNullOrEmpty(mem.Piety))
                {
                    double dev = child ? 0.15 : 0.25, obs = child ? 0.75 : 0.55, ind = child ? 0.07 : 0.12, sk = child ? 0.03 : 0.08;
                    if (mem.Morality == "principled") dev *= 1.3;
                    if (mem.Morality == "ruthless") { ind *= 1.5; sk *= 1.5; }
                    mem.Piety = RollOf(("devout", dev), ("observant", obs), ("indifferent", ind), ("skeptic", sk));
                }
                if (cfg.EnableReligion && string.IsNullOrEmpty(mem.Faith)) mem.Faith = PickFaith(s);
                if (mem.Profile != null && !string.IsNullOrEmpty(mem.Faith) && string.IsNullOrWhiteSpace(mem.Profile.Religion))
                    mem.Profile.Religion = FaithById(mem.Faith)?.Name ?? "";
                if (!string.IsNullOrEmpty(mem.ArchetypeId))
                    Log.Info($"[ARCHETYPES] {s.Name}: {mem.ArchetypeId}{(string.IsNullOrEmpty(mem.Archetype2Id) ? "" : " + " + mem.Archetype2Id)} (roles: {string.Join(",", roles)}), nature {mem.Morality}, swearing {mem.Profanity}, piety {mem.Piety}, faith {mem.Faith ?? "-"}");
            }
            catch (Exception ex) { Log.Error("Character core", ex); }
        }

        private static bool HasOwn(string v) => !string.IsNullOrWhiteSpace(v);
        private static bool MoralsAgree(string a, string b) => string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b) || a == b || (a != "principled" || b != "ruthless") && (a != "ruthless" || b != "principled");

        /// <summary>The faith of their family most of the time (85%); otherwise one of their people's, the main one likeliest.</summary>
        private static string PickFaith(Speaker s)
        {
            var all = Faiths();
            if (all.Count == 0) return null;
            if (s.IsHero && CoreRng.NextDouble() < 0.85)
            {
                var kin = new List<Hero> { s.Hero.Father, s.Hero.Mother, s.Hero.Spouse };
                kin.AddRange(s.Hero.Siblings ?? Enumerable.Empty<Hero>());
                kin.AddRange(s.Hero.Children ?? Enumerable.Empty<Hero>());
                var their = kin.Where(h => h != null).Select(h => MemoryStore.Find(h.StringId)?.Faith).Where(f => f != null && all.Any(x => x.Id == f))
                               .GroupBy(f => f).OrderByDescending(g => g.Count()).FirstOrDefault()?.Key;
                if (their != null) return their;
            }
            string culture = s.Culture?.StringId ?? "";
            var pick = Roulette(all.Select(f => (f, (double)Math.Max(0.001f, f.Weight) * ((f.Cultures ?? new List<string>()).Contains(culture) ? 1.0 : 0.02))).ToList());
            return pick?.Id;
        }

        // ============================================================ what the AI is told

        private static readonly Tpl TCoreCreate = new Tpl("character_creation/core.txt",
@"CHARACTER CORE (fixed facts about this person, decided before you; build every field from them)
{archetype}
NATURE — {nature}
SWEARING (the obscene words of {language}) — {swearing}
{habit}
{faith}
Push the core to its extreme: it should show in almost every conversation. Make it this person's own through their culture, rank, age and past; the archetype's habits, secrets and lines only point the way. Where their game traits pull elsewhere, keep the core and let the traits colour it (an honourable snob, a merciful bully).");

        private static readonly Tpl TCoreTalk = new Tpl("archetypes/core_talk.txt",
            "Your core: {core}{second}. Nature: {nature}. Swearing: {swearing}.{habit}{faith} Let it show whenever the moment invites it; never soften it into a reasonable, ordinary person.");

        private static readonly Tpl TCoreOfficial = new Tpl("archetypes/core_official.txt",
            "A ruler's core: {core}. It shows in what you choose, demand, fear, reward and punish, not as a catchphrase. Nature: {nature}. In an official text there are no obscenities: {swearing}{reputation}{faith}");

        private static readonly Tpl TNoArchetype = new Tpl("character_creation/no_archetype.txt",
            "This person has no archetype: their core is their own nature as the data in this prompt shows it.");

        public static void TouchCore() { var _ = TCoreCreate.Key + TCoreTalk.Key + TCoreOfficial.Key + TNoArchetype.Key + TFieldRules.Key + TFieldRulesResident.Key; }

        private static string NatureText(string m, bool child, bool brief)
        {
            if (child)
                return m == "principled" ? (brief ? "a good-hearted child" : "a good-hearted child: shares, owns up to mischief, is upset by anything unfair; still a child, with a child's tempers.")
                     : m == "ruthless" ? (brief ? "a mean-spirited child" : "a mean-spirited child: picks on the weaker, enjoys others getting into trouble, lies easily to stay out of it; a child, not a monster.")
                     : (brief ? "an ordinary child" : "an ordinary child: sometimes kind, sometimes selfish, lies to get out of trouble and forgets it by supper.");
            return m == "principled" ? (brief ? "decent at heart, with lines they will not cross" : "a decent person at heart: there are things they will not do even when it costs them, and cruelty or betrayal sits badly with them. It shows in what they refuse, not in sermons.")
                 : m == "ruthless" ? (brief ? "genuinely bad: hurts, cheats or betrays whenever it serves them" : "genuinely bad: they hurt, cheat or betray without losing sleep whenever it serves them, and despise the soft. Never soften or redeem this; manners may hide it, nothing changes it.")
                 : (brief ? "an ordinary mix of decent and shabby" : "an ordinary mix: kind when it is cheap, hard when it pays, capable of a generous and a shabby act in the same week; they bend rules for their own and justify it afterwards.");
        }

        private static string SwearingText(string p, bool child, bool brief)
        {
            if (child) return p == "heavy" ? "uses the rude words children pick up from grown-ups, never real obscenities" : "no bad words at all";
            return p == "heavy" ? (brief ? "often and with relish, when feeling runs high or among equals" : "swears often and with relish, mostly when feeling runs high or among equals, less before their betters; never in every sentence.")
                 : p == "none" ? (brief ? "never; coarse words offend them" : "never swears: coarse words embarrass or offend them, and even in anger they reach for milder ones.")
                 : (brief ? "now and then, in anger, pain or surprise" : "swears now and then, in anger, pain or surprise, rarely before people above them.");
        }

        private static string PietyRule(string p) =>
            p == "devout" ? "devout: faith is in their mouth; roughly one line in three or four touches it (an oath, a blessing, a judgement by the holy ones), and at most two of their sample lines do."
          : p == "skeptic" ? "a skeptic: privately doubts or mocks the holy ones; never invokes them except in mockery or for show."
          : p == "indifferent" ? "indifferent: thinks of the holy ones only in trouble or out of habit, and almost never brings faith up."
          : "customary: keeps the forms, a usual greeting or oath now and then, about one line in ten; faith seldom shapes what they argue.";

        private static string ArchetypeBlock(Archetype a, bool secondary)
        {
            var sb = new StringBuilder();
            if (!secondary)
            {
                sb.AppendLine($"Core archetype — {a.Name}: {a.Core}");
                if (!string.IsNullOrWhiteSpace(a.SeenByOthers)) sb.AppendLine("How people see such a person: " + a.SeenByOthers);
                if (!string.IsNullOrWhiteSpace(a.Habits)) sb.AppendLine("Typical habits: " + a.Habits);
                if (a.Secrets != null && a.Secrets.Count > 0) sb.AppendLine("Possible secrets (take one, or invent your own of the same weight): " + string.Join(" | ", a.Secrets));
                if (a.Lines != null && a.Lines.Count > 0) sb.AppendLine("Sample lines (only the direction of the voice; never copy them): " + string.Join(" | ", a.Lines));
            }
            else
            {
                sb.AppendLine($"Secondary colour (weaker: it tints the core, never replaces it) — {a.Name}: {a.Core}");
                if (!string.IsNullOrWhiteSpace(a.SeenByOthers)) sb.AppendLine("How people see it: " + a.SeenByOthers);
                if (a.Lines != null && a.Lines.Count > 0) sb.AppendLine("Its lines (direction only): " + string.Join(" | ", a.Lines.Take(3)));
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>The core as the AI writing their character gets it (archetypes, nature, swearing, voice habit, faith).</summary>
        public static string CoreForCreation(Speaker s)
        {
            EnsureCore(s);
            var mem = s.Memory;
            bool child = s.Age < 18f || s.KnownChild;
            var a1 = ArchetypeById(mem.ArchetypeId);
            var a2 = ArchetypeById(mem.Archetype2Id);
            string arch = a1 != null ? ArchetypeBlock(a1, false) + (a2 != null ? "\n" + ArchetypeBlock(a2, true) : "")
                        : !string.IsNullOrEmpty(mem.Archetype) ? $"Core archetype — {mem.Archetype}: {mem.ArchetypeText}"
                        : TNoArchetype.Text;
            var faith = S.Get.EnableReligion ? FaithById(mem.Faith) : null;
            string faithText = faith == null ? "" : $"FAITH — {faith.Name}: {faith.Description} How its people talk: {faith.Speech} (background knowledge, not a list of words to use).\nPIETY — {PietyRule(mem.Piety)}";
            return TCoreCreate.Format("archetype", arch, "nature", NatureText(mem.Morality, child, false), "language", S.Get.Language,
                "swearing", SwearingText(mem.Profanity, child, false), "habit", HabitForCreation(s), "faith", faithText).Replace("\n\n\n", "\n");
        }

        /// <summary>The one line every conversation carries: core, nature, swearing, voice habit and faith.</summary>
        public static string CoreForTalk(Speaker s)
        {
            EnsureCore(s);
            var mem = s.Memory;
            bool child = s.Age < 18f || s.KnownChild;
            var a1 = ArchetypeById(mem.ArchetypeId);
            var a2 = ArchetypeById(mem.Archetype2Id);
            string core = a1 != null ? $"{a1.Name} — {a1.Core}" : !string.IsNullOrEmpty(mem.Archetype) ? $"{mem.Archetype} — {mem.ArchetypeText}" : "your own nature";
            string habit = HabitForTalk(s);
            var faith = S.Get.EnableReligion ? FaithById(mem.Faith) : null;
            return TCoreTalk.Format("core", core, "second", a2 != null ? $"; also, more weakly: {a2.Core}" : "",
                "nature", NatureText(mem.Morality, child, true), "swearing", SwearingText(mem.Profanity, child, true),
                "habit", habit.Length > 0 ? " " + habit : "", "faith", faith != null ? $" Faith: {faith.Name}, {PietyRule(mem.Piety)}" : "");
        }

        /// <summary>For a ruler's statements and decrees: the core as policy, no obscenities, their reputation and the official faith.</summary>
        public static string CoreForOfficial(Speaker s)
        {
            try
            {
                EnsureCore(s);
                var mem = s.Memory;
                var a1 = ArchetypeById(mem.ArchetypeId);
                string core = a1 != null ? a1.Core : !string.IsNullOrEmpty(mem.ArchetypeText) ? mem.ArchetypeText : "your own nature";
                string swearing = mem.Profanity == "heavy" ? "coarseness shows as blunt, rude, contemptuous wording." : mem.Profanity == "none" ? "your words stay measured and clean." : "an edge of roughness at most.";
                var faith = S.Get.EnableReligion ? FaithById(mem.Faith) : null;
                string rep = string.IsNullOrWhiteSpace(mem.Profile?.Reputation) ? "" : " What is said of you: " + mem.Profile.Reputation;
                string f = faith == null ? "" : mem.Piety == "devout" ? $" Official faith: {faith.Name}; you invoke it and justify your decisions by it." : $" Official faith: {faith.Name}.";
                return TCoreOfficial.Format("core", core, "nature", NatureText(mem.Morality, false, true), "swearing", swearing, "reputation", rep, "faith", f);
            }
            catch { return ""; }
        }

        // ============================================================ the fields the AI writes

        /// <summary>The profile fields, their length (characters) and who writes them (everyone, or heroes only).</summary>
        internal static readonly (string key, int min, int max, bool all)[] Fields =
        {
            ("personality", 150, 300, true), ("backstory", 500, 900, true), ("speech_style", 50, 160, true), ("cognitive_style", 100, 250, true),
            ("reputation", 80, 220, true), ("values", 100, 220, false), ("drives", 100, 250, false), ("goals", 80, 220, false),
            ("social_behavior", 120, 280, false), ("secret", 60, 200, false),
        };

        internal static string FieldOf(CharacterProfile p, string key)
        {
            if (p == null) return "";
            switch (key)
            {
                case "personality": return p.Personality; case "backstory": return p.Backstory; case "speech_style": return p.SpeechStyle;
                case "cognitive_style": return p.CognitiveStyle; case "reputation": return p.Reputation; case "values": return p.Values;
                case "drives": return p.Drives; case "goals": return p.Goals; case "social_behavior": return p.SocialBehavior; case "secret": return p.Secret;
                default: return "";
            }
        }

        /// <summary>The fields this person still lacks (sample lines and naivety included). Empty when the profile is complete.</summary>
        public static List<string> MissingFields(Speaker s)
        {
            var p = s.Memory.Profile;
            bool hero = s.IsHero;
            var miss = Fields.Where(f => (f.all || hero) && string.IsNullOrWhiteSpace(FieldOf(p, f.key))).Select(f => f.key).ToList();
            if (p == null || p.SampleLines == null || p.SampleLines.Count < 3) miss.Add("sample_lines");
            if (hero && (p == null || p.Naivety < 0)) miss.Add("naivety");
            return miss;
        }

        /// <summary>Each field cut to its length; sample lines kept to eight, one line each, no double quotes.</summary>
        public static void Tidy(CharacterProfile p)
        {
            if (p == null) return;
            string Cut(string v, int max) { v = (v ?? "").Trim().Trim('"'); return v.Length > max ? v.Substring(0, max).TrimEnd() : v; }
            foreach (var f in Fields)
            {
                int max = f.key == "backstory" ? 1400 : (int)(f.max * 1.4);
                switch (f.key)
                {
                    case "personality": p.Personality = Cut(p.Personality, max); break; case "backstory": p.Backstory = Cut(p.Backstory, max); break;
                    case "speech_style": p.SpeechStyle = Cut(p.SpeechStyle, max); break; case "cognitive_style": p.CognitiveStyle = Cut(p.CognitiveStyle, max); break;
                    case "reputation": p.Reputation = Cut(p.Reputation, max); break; case "values": p.Values = Cut(p.Values, max); break;
                    case "drives": p.Drives = Cut(p.Drives, max); break; case "goals": p.Goals = Cut(p.Goals, max); break;
                    case "social_behavior": p.SocialBehavior = Cut(p.SocialBehavior, max); break; case "secret": p.Secret = Cut(p.Secret, max); break;
                }
            }
            if (p.SampleLines != null)
                p.SampleLines = p.SampleLines.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => Cut(x.Replace("\n", " ").Replace("\"", "'"), 220)).Take(8).ToList();
        }

        private static readonly Tpl TFieldRules = new Tpl("character_creation/field_rules.txt",
@"Character creation (REQUIRED this time): write the missing fields of ""profile"" listed below, in {language}. Each field has its own job: never repeat the same facts in several. What is already written about this person, and the CHARACTER CORE, are fixed facts: build on them.
- personality ({personality} characters): who they are. The core pushed to its extreme first, then their temperament; a contradiction only if it sharpens them, never if it softens them.
- backstory ({backstory}): only their past and what shaped them (show events, do not list traits); no current wars and nothing of today. Their family is exactly the family listed in this prompt: never add a spouse, children, a widowhood or a pregnancy.
- values ({values}): concrete lines they keep or cross (""keeps an oath even to an enemy""), not words like honour.
- drives ({drives}): what they want and fear, as things they can act on; the main basis of their own decisions.
- goals ({goals}): what they mean to achieve this year, and in their life.
- cognitive_style ({cognitive_style}): only how they think: how they judge claims, spot or miss a lie, decide, admit a mistake.
- social_behavior ({social_behavior}): how they treat strangers, friends, enemies and those above them; how they meet kindness, disrespect, threats, flirting and manipulation; their humour, and how readily they lie.
- reputation ({reputation}): what others say about them, as gossip, with a nickname if they have one.
- secret ({secret}): ONE small, personal secret, not a world conspiracy, with no other real people invented.
- speech_style ({speech_style}): only the way they talk: delivery, the kind of words they reach for, how their tone shifts, how faith and swearing show as set above. No words or phrases in quotes (they turn into a tic in every line). Not ""speaks shorter or quieter when angry"": everyone writes that. Not a list of adjectives.
- naivety: a number from 0.0 (shrewd, very hard to fool) to 1.0 (trusting, easily taken in), from their cognitive style, age and life; a child or a sheltered villager high, a merchant or a schemer low; no extremes without a reason.
- sample_lines: 6 to 8 lines, each in a different situation (greeting a stranger, their work or duty, war or the powerful, being insulted, being flattered, a jab at someone, muttering to themselves), all in the voice of speech_style; one or two plain everyday ones so the core never turns into a catchphrase; different words and images in each, none taken from the archetype; swearing and faith only where the core puts them; no double quotes inside.
Whatever they know about themselves (in ""Things you know"") is a fact of their life: every field respects it.
Your reply must already sound like this person.");

        private static readonly Tpl TFieldRulesResident = new Tpl("character_creation/field_rules_resident.txt",
@"Character creation (REQUIRED this time): write the missing fields of ""profile"" listed below, in {language}, each with its own job, built on the CHARACTER CORE and on what you are.
- given_name: a first name of your people.
- personality ({personality} characters): who you are, the core pushed to its extreme, then your temper.
- backstory ({backstory}): only your past and what shaped you; no family you were not given.
- cognitive_style ({cognitive_style}): how you think, judge claims and spot lies.
- reputation ({reputation}): what the neighbours say about you, as gossip.
- speech_style ({speech_style}): only the way you talk, no quoted words, not ""quieter when angry"".
- sample_lines: 6 to 8 lines in different situations, in that voice, no double quotes inside.
Your reply must already sound like this person.");

        private static string Range(string key) { var f = Fields.First(x => x.key == key); return $"{f.min}-{f.max}"; }

        /// <summary>The rules for the fields to write, with their lengths.</summary>
        public static string FieldRules(Speaker s) =>
            (s.IsHero ? TFieldRules : TFieldRulesResident).Format("language", S.Get.Language, "personality", Range("personality"), "backstory", Range("backstory"),
                "values", Range("values"), "drives", Range("drives"), "goals", Range("goals"), "cognitive_style", Range("cognitive_style"),
                "social_behavior", Range("social_behavior"), "reputation", Range("reputation"), "secret", Range("secret"), "speech_style", Range("speech_style"));

        /// <summary>The JSON skeleton of the profile, with only the missing fields.</summary>
        public static string ProfileSkeleton(Speaker s, List<string> missing)
        {
            var parts = new List<string>();
            if (!s.IsHero && string.IsNullOrWhiteSpace(s.Memory.Profile?.GivenName)) parts.Add("\"given_name\": \"...\"");
            foreach (var k in missing)
                parts.Add(k == "naivety" ? "\"naivety\": <number from 0.0 to 1.0>" : k == "sample_lines" ? "\"sample_lines\": [\"...\", \"...\", \"...\", \"...\", \"...\", \"...\"]" : $"\"{k}\": \"...\"");
            return "\"profile\": { " + string.Join(", ", parts) + " }";
        }

        // ============================================================ facts for the creation

        /// <summary>What the game knows of a hero that shapes who they are: skills, what they wear, their realm, their whole family.</summary>
        public static string CreationFacts(Speaker s)
        {
            if (!s.IsHero) return "";
            var h = s.Hero;
            var sb = new StringBuilder();
            try
            {
                var skills = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<SkillObject>()
                    .Select(k => (k, v: h.GetSkillValue(k))).Where(x => x.v > 0).OrderByDescending(x => x.v).Take(6).ToList();
                string Level(int v) => v >= 250 ? "masterly" : v >= 175 ? "expert" : v >= 125 ? "seasoned" : v >= 75 ? "practised" : v >= 30 ? "some skill" : "a beginner";
                if (skills.Count > 0)
                    sb.AppendLine("Their best skills: " + string.Join(", ", skills.Take(3).Select(x => $"{x.k.Name} ({Level(x.v)}, {x.v})"))
                                  + (skills.Count > 3 ? "; also " + string.Join(", ", skills.Skip(3).Select(x => $"{x.k.Name} ({x.v})")) : "") + ".");
            }
            catch { }
            try
            {
                var eq = h.BattleEquipment;
                var worn = new List<string>();
                if (eq != null)
                    for (int i = 0; i < (int)EquipmentIndex.NumEquipmentSetSlots; i++)
                    {
                        var it = eq[(EquipmentIndex)i].Item;
                        if (it != null) worn.Add($"{it.Name} ({it.Value} denars)");
                    }
                if (worn.Count > 0) sb.AppendLine("What they wear and carry: " + string.Join(", ", worn) + ".");
            }
            catch { }
            try
            {
                if (h.MapFaction is Kingdom k && k.EncyclopediaText != null)
                {
                    string t = k.EncyclopediaText.ToString();
                    if (t.Length > 600) t = t.Substring(0, 600) + "…";
                    if (t.Length > 0) sb.AppendLine($"Their realm, {k.Name}: {t}");
                }
            }
            catch { }
            try
            {
                var fam = new List<string>();
                void Add(Hero x, string tie) { if (x != null) fam.Add($"{tie}: {x.Name}{(x.IsDead ? " (dead)" : "")}"); }
                Add(h.Father, "father"); Add(h.Mother, "mother"); Add(h.Spouse, "spouse");
                foreach (var x in h.ExSpouses ?? Enumerable.Empty<Hero>()) Add(x, "former spouse");
                foreach (var x in h.Siblings ?? Enumerable.Empty<Hero>()) Add(x, x.IsFemale ? "sister" : "brother");
                foreach (var x in h.Children ?? Enumerable.Empty<Hero>()) Add(x, x.IsFemale ? "daughter" : "son");
                sb.AppendLine("Their whole family (no other relatives exist; never add any): " + (fam.Count > 0 ? string.Join("; ", fam) : "none living or known") + ".");
            }
            catch { }
            return sb.ToString().TrimEnd();
        }
    }
}
