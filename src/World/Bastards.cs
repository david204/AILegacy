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
    }

    /// <summary>
    /// Children out of wedlock. The pregnancy itself goes in the game's own queue with the right father (the game only ever
    /// writes the mother's husband), so the clock, stillbirths and labour are the game's. This keeps the record of who the
    /// father is, and at birth: who raises the child, a surname for the baseborn, a secret kept or the scandal, and later
    /// the ways a child is made legitimate.
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
                if (_queueField == null) { Log.Warn("Embarazo ilegítimo: el juego no tiene _heroPregnancies"); return false; }
                var list = _queueField.GetValue(behavior) as IList;
                if (list == null) return false;
                if (_pregnancyCtor == null)
                {
                    var type = behavior.GetType().GetNestedType("Pregnancy", BindingFlags.NonPublic | BindingFlags.Public);
                    _pregnancyCtor = type?.GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, new[] { typeof(Hero), typeof(Hero), typeof(CampaignTime) }, null);
                }
                if (_pregnancyCtor == null) { Log.Warn("Embarazo ilegítimo: no encuentro el constructor del embarazo del juego"); return false; }
                foreach (var e in list)
                {
                    var m = e?.GetType().GetField("Mother")?.GetValue(e) as Hero;
                    if (m == mother) return false;
                }
                var due = CampaignTime.DaysFromNow(Campaign.Current.Models.PregnancyModel.PregnancyDurationInDays);
                list.Add(_pregnancyCtor.Invoke(new object[] { mother, father, due }));
                mother.IsPregnant = true;
                return true;
            }
            catch (Exception ex) { Log.Error("Embarazo ilegítimo: cola del juego", ex); return false; }
        }

        /// <summary>A child conceived out of wedlock. True if the pregnancy took.</summary>
        public static bool Conceive(Hero mother, Hero father)
        {
            if (!Enabled || mother == null || father == null || !mother.IsFemale || father.IsFemale || mother.IsPregnant || !mother.IsAlive || !father.IsAlive) return false;
            if (mother.Spouse == father) return false;
            if (!Inject(mother, father)) return false;
            var b = new Bastard
            {
                Id = WorldSim.NewId("bst"), MotherId = mother.StringId, FatherId = father.StringId, ConceivedDay = CampaignTime.Now.ToDays,
                HusbandId = mother.Spouse != null && mother.Spouse != father && mother.Spouse.IsAlive ? mother.Spouse.StringId : null,
            };
            WorldSim.State.Bastards.Add(b);
            // without MakePregnantAction there is no conception event: tell the mood system by hand
            try { Mood.OnChildConceived(mother); } catch { }
            Log.Info($"[bastardos] {mother.Name} espera un hijo de {father.Name}{(b.HusbandId != null ? " (no es su marido)" : "")}");
            SaveFiles.Dirty();
            return true;
        }

        /// <summary>Intimacy with the player, not married to each other: the same roll as between spouses.</summary>
        public static void TryWithPlayer(Hero other)
        {
            try
            {
                if (!Enabled || other == null || other.IsFemale == P.IsFemale || other.Spouse == P) return;
                var mother = other.IsFemale ? other : P; var father = other.IsFemale ? P : other;
                if (mother.IsPregnant || mother.Age < 18 || mother.Age >= 46) return;
                float chance = S.Get.BastardChancePercent * 10f * Mood.FertilityFactor(mother);
                if (Rng.Next(1000) < Math.Min(1000f, chance)) Conceive(mother, father);
            }
            catch (Exception ex) { Log.Error("[bastardos] intimidad con el jugador", ex); }
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
            && !(S.Get.BastardProtectPlayerClan && w.Clan == Clan.PlayerClan) && !w.Children.Any(c => c.IsAlive && c.Age < 1);

        private static bool MayBeLover(Hero w, Hero m) =>
            m != null && !m.IsFemale && m != P && m.IsAlive && m.Age >= 18 && !m.IsNotable && m.Clan != null && m != w.Spouse && m.Clan != w.Clan
            && !Kin(w, m) && w.GetRelation(m) >= 0;

        private static bool Kin(Hero a, Hero b)
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
                foreach (var (h, wt) in lovers) { if ((pick -= wt) > 0) continue; Conceive(w, h); return; }
            }
            catch (Exception ex) { Log.Error("[bastardos] tirada diaria", ex); }
        }

        // ============================================================ birth

        public static void OnGivenBirth(Hero mother, List<Hero> children, int stillborn)
        {
            try
            {
                if (!Enabled || mother == null) return;
                var b = All.FirstOrDefault(x => !x.Born && !x.Lost && x.MotherId == mother.StringId);
                if (b == null) return;
                b.Born = true;
                var father = Hero.Find(b.FatherId);
                if (children == null || children.Count == 0 || father == null) { Log.Info($"[bastardos] el hijo de {mother.Name} y {b.FatherId} nace muerto"); return; }
                foreach (var c in children) if (c != null) { b.Children.Add(c.StringId); b.BirthNames[c.StringId] = c.FirstName?.ToString() ?? c.Name.ToString(); }
                Log.Info($"[bastardos] {mother.Name} da a luz {children.Count} hijo(s) de {father.Name}");
                // married to each other by now: nothing to hide or name
                if (mother.Spouse == father) { foreach (var c in children) Legitimize(b, c, quiet: true); return; }
                bool playerParent = mother == P || father == P;
                if (playerParent)
                {
                    var other = mother == P ? father : mother;
                    if (other.Clan == Clan.PlayerClan) { foreach (var c in children) Name(b, c); return; }
                    AskGuardian(b, children, other);
                    return;
                }
                foreach (var c in children) RaiseWith(b, c, mother, father);
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[bastardos] parto", ex); }
        }

        private static void AskGuardian(Bastard b, List<Hero> children, Hero other)
        {
            string names = string.Join(", ", children.Select(c => c.FirstName?.ToString() ?? c.Name.ToString()));
            string question = other.IsFemale
                ? L.F("AIL_e7b1c817", $"{other.Name} ha dado a luz: {names}, hijo de los dos. ¿Lo crías como tuyo, en tu casa? Si no, se queda con {other.Name}, y quien se entere no lo olvidará.")
                : L.F("AIL_e7b1c835", $"Has dado a luz: {names}, hijo tuyo y de {other.Name}. ¿Lo crías en tu casa? Si no, se queda con {other.Name}, y quien se entere no lo olvidará.");
            InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1c816", "Un hijo fuera del matrimonio"), question,
                true, true, L.T("AIL_e7b1c818", "Criarlo en tu casa"), L.T("AIL_e7b1c819", "Que se quede con su madre o su padre"),
                () =>
                {
                    try { foreach (var c in children) { if (c.Clan != Clan.PlayerClan) c.Clan = Clan.PlayerClan; Name(b, c); } Log.Info("[bastardos] el jugador cría al niño en su casa"); }
                    catch (Exception ex) { Log.Error("[bastardos] tutela: en casa", ex); }
                },
                () =>
                {
                    try { foreach (var c in children) RaiseWith(b, c, other, P); }
                    catch (Exception ex) { Log.Error("[bastardos] tutela: con el otro", ex); }
                }), true);
        }

        /// <summary>The child stays with <paramref name="guardian"/>. A married mother may pass it off as her husband's.</summary>
        private static void RaiseWith(Bastard b, Hero child, Hero guardian, Hero culprit)
        {
            var cfg = S.Get;
            if (guardian.Clan != null && child.Clan != guardian.Clan) child.Clan = guardian.Clan;
            var husband = guardian.IsFemale ? guardian.Spouse : null;
            var mother = Hero.Find(b.MotherId); var father = Hero.Find(b.FatherId);
            if (husband != null && husband.IsAlive && husband != father && Rng.Next(100) < cfg.BastardSecretKeptPercent)
            {
                // Kept secret: the world takes the child for the husband's
                try
                {
                    var children = HarmonyLib.AccessTools.Field(typeof(Hero), "_children")?.GetValue(father) as IList;
                    children?.Remove(child);
                    child.Father = husband;
                    b.Secret = true;
                    var sec = StorySecrets.AddEvent($"{child.Name} is not the child of {husband.Name}: the true father is {father?.Name}.",
                        L.F("AIL_e7b1c81a", $"{child.Name} no es hijo de {husband.Name}: su verdadero padre es {father?.Name}."), new[] { mother, father, child }, new[] { mother, father }, "high");
                    b.SecretId = sec?.Id;
                    Log.Info($"[bastardos] {child.Name} pasa por hijo de {husband.Name}");
                    return;
                }
                catch (Exception ex) { Log.Error("[bastardos] secreto guardado", ex); }
            }
            Name(b, child);
            if (!cfg.BastardConsequences || guardian.Clan == Clan.PlayerClan) return;
            // The scandal: whoever is to blame pays for it with the guardian's spouse and head of house
            try
            {
                var spouse = guardian.Spouse;
                if (culprit != null && spouse != null && spouse.IsAlive && spouse != culprit)
                {
                    if (culprit == P) ChangeRelationAction.ApplyPlayerRelation(spouse, -cfg.BastardSpouseRelationLoss, false, true);
                    else ChangeRelationAction.ApplyRelationChangeBetweenHeroes(culprit, spouse, -cfg.BastardSpouseRelationLoss, false);
                }
                var head = guardian.Clan?.Leader;
                if (culprit != null && head != null && head != guardian && head != culprit && head.IsAlive)
                {
                    if (culprit == P) ChangeRelationAction.ApplyPlayerRelation(head, -cfg.BastardClanLeaderRelationLoss, false, true);
                    else ChangeRelationAction.ApplyRelationChangeBetweenHeroes(culprit, head, -cfg.BastardClanLeaderRelationLoss, false);
                }
                if (mother != null && father != null)
                    NewsStore.Add(new NewsItem
                    {
                        Type = "rumor", Importance = 2, Text = $"{mother.Name} has borne a child out of wedlock, by {father.Name}.",
                        TitleEs = L.F("AIL_e7b1c81b", $"Un hijo ilegítimo de {father.Name}"), TextEs = L.F("AIL_e7b1c81c", $"{mother.Name} ha dado a luz a {child.Name}, y todos saben que el padre es {father.Name}."),
                        Kingdoms = new[] { mother.MapFaction, father.MapFaction }.Where(f => f != null).Select(f => f.StringId).Distinct().ToList(),
                        Heroes = new List<string> { mother.StringId, father.StringId, child.StringId },
                    });
            }
            catch (Exception ex) { Log.Error("[bastardos] escándalo", ex); }
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
                string full = first + " " + names[Rng.Next(names.Count)];
                child.SetName(new TextObject("{=!}" + full), new TextObject("{=!}" + first));
            }
            catch (Exception ex) { Log.Error("[bastardos] apellido", ex); }
        }

        // ============================================================ legitimation

        public static Bastard RecordOf(Hero child) => child == null ? null : All.FirstOrDefault(b => b.Children.Contains(child.StringId));
        public static bool IsBaseborn(Hero child)
        {
            var b = RecordOf(child);
            return b != null && !b.Secret && !b.Legitimized.Contains(child.StringId);
        }

        public static void Legitimize(Bastard b, Hero child, bool quiet = false)
        {
            try
            {
                if (b == null || child == null || b.Legitimized.Contains(child.StringId)) return;
                b.Legitimized.Add(child.StringId);
                if (b.BirthNames.TryGetValue(child.StringId, out string first) && !string.IsNullOrWhiteSpace(first))
                    child.SetName(new TextObject("{=!}" + first), new TextObject("{=!}" + first));
                // a child still in no house of either parent goes to the father's (or the player's)
                var father = Hero.Find(b.FatherId); var mother = Hero.Find(b.MotherId);
                var home = father == P || mother == P ? Clan.PlayerClan : father?.Clan ?? mother?.Clan;
                if (home != null && !home.IsBanditFaction && child.Clan != home && child.Spouse == null && child.Clan?.Leader != child) child.Clan = home;
                if (b.SecretId != null) StorySecrets.Reveal(b.SecretId, null);
                Log.Info($"[bastardos] {child.Name} queda legitimado");
                if (!quiet) WorldSim.Say(L.F("AIL_e7b1c81d", $"{child.Name} es ahora hijo legítimo."), Colors.Yellow);
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[bastardos] legitimar", ex); }
        }

        /// <summary>Marriages: the parents marrying make their children legitimate; so does a baseborn child marrying, if set.</summary>
        public static void OnMarried(Hero a, Hero b, bool show)
        {
            try
            {
                if (!Enabled || a == null || b == null) return;
                foreach (var r in All.Where(x => x.Born && ((x.MotherId == a.StringId && x.FatherId == b.StringId) || (x.MotherId == b.StringId && x.FatherId == a.StringId))).ToList())
                    foreach (var id in r.Children.ToList()) { var c = Hero.Find(id); if (c != null && c.IsAlive) Legitimize(r, c); }
                if (S.Get.BastardLegitimizeOnMarriage)
                    foreach (var h in new[] { a, b }) if (IsBaseborn(h)) Legitimize(RecordOf(h), h);
            }
            catch (Exception ex) { Log.Error("[bastardos] boda", ex); }
        }

        /// <summary>Heading a house makes one legitimate.</summary>
        public static void OnClanLeaderChanged(Hero oldLeader, Hero newLeader)
        {
            try { if (Enabled && newLeader != null && IsBaseborn(newLeader)) Legitimize(RecordOf(newLeader), newLeader); }
            catch (Exception ex) { Log.Error("[bastardos] jefe de clan", ex); }
        }

        /// <summary>The player, as parent, acknowledges an adult baseborn child.</summary>
        public static bool CanAcknowledge(Hero child)
        {
            var b = RecordOf(child);
            return Enabled && b != null && IsBaseborn(child) && child.Age >= 18 && (b.FatherId == P?.StringId || b.MotherId == P?.StringId);
        }

        public static void Acknowledge(Hero child)
        {
            var b = RecordOf(child);
            if (b == null) return;
            Legitimize(b, child);
            try
            {
                if (child.Clan != Clan.PlayerClan && child.Clan?.Leader != child) { child.Clan = Clan.PlayerClan; if (child.Occupation != Occupation.Lord) child.SetNewOccupation(Occupation.Lord); }
            }
            catch (Exception ex) { Log.Error("[bastardos] reconocer", ex); }
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
                    Log.Info($"[bastardos] {b.MotherId} pierde el embarazo");
                    if (mother != null && mother.IsAlive)
                    {
                        try { AILegacy.Api.AILegacyApi.AddMoodCause(mother, "You lost the child you were carrying.", -1, 3, 14); } catch { }
                        if (b.FatherId == P?.StringId) WorldSim.Say(L.F("AIL_e7b1c81e", $"{mother.Name} ha perdido el hijo que esperaba de ti."), Colors.Gray);
                    }
                }
                WorldSim.State.Bastards.RemoveAll(x => x.Lost && now - x.ConceivedDay > 400);
            }
            catch (Exception ex) { Log.Error("[bastardos] día", ex); }
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
                    string state = !b.Born ? "You are carrying a child" : "You have " + (b.Children.Count == 1 ? "a child, " : "children, ") + Kids(b) + ",";
                    string secret = b.Secret ? ": the world believes " + (b.Children.Count == 1 ? "it is" : "they are") + " your husband's" : b.Born ? "; it is known" : "";
                    if (b.MotherId == id) lines.Add(TMother.Format("state", state, "father", N(b.FatherId), "secret", secret));
                    else if (b.FatherId == id) lines.Add(TFather.Format("state", (!b.Born ? "She is carrying your child" : "You have " + (b.Children.Count == 1 ? "a child, " : "children, ") + Kids(b) + ","),
                        "mother", N(b.MotherId), "secret", b.Secret ? ": the world believes the child is her husband's" : ""));
                    else if (b.HusbandId == id && b.Born && !b.Secret) lines.Add(THusband.Format("mother", N(b.MotherId), "father", N(b.FatherId), "children", Kids(b)));
                    else if (b.Children.Contains(id))
                    {
                        if (b.Legitimized.Contains(id)) lines.Add(TLegitChild.Format("father", N(b.FatherId), "mother", N(b.MotherId)));
                        else if (!b.Secret) lines.Add(TChild.Format("father", N(b.FatherId), "known", "", "heir", h.Clan?.Leader == h ? "" : " and cannot inherit unless someone makes you so"));
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
                lines.Add($"{b.Id}: {Hero.Find(b.MotherId)?.Name}+{Hero.Find(b.FatherId)?.Name} día {(int)b.ConceivedDay} {(b.Lost ? "perdido" : b.Born ? "nacido: " + string.Join(", ", b.Children.Select(id => Hero.Find(id)?.Name?.ToString() ?? id)) : "en camino")}{(b.Secret ? " (secreto)" : "")}");
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
            Hero Find(string n) { n = n.Replace('_', ' ').ToLowerInvariant(); return Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant() == n) ?? Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant().Contains(n)); }
            var m = Find(args[0]); var f = Find(args[1]);
            if (m == null || f == null) return "No encuentro a uno de los dos.";
            return Bastards.Conceive(m, f) ? $"{m.Name} espera un hijo de {f.Name}." : "No ha podido ser (¿ya embarazada, mismo sexo, cónyuges o el ajuste apagado?).";
        }
    }
}
