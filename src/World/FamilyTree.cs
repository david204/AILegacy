using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>
    /// The player's family built at once from the editor, as AIInfluence does it: several new people linked to each other,
    /// to heroes already in the world and to the player (father, mother, spouse), so grandparents, uncles, cousins, in-laws
    /// or a stepfamily come out of the links. Each one can bring a description (the base for the personality the AI writes),
    /// traits, skills, what they know of the player and a shared history. Ages and sexes are checked against the roles.
    /// Also the names of the wider kinship (grandfather, cousin, half-sister, stepson…) for the encyclopedia and the talk.
    /// </summary>
    internal static class FamilyTree
    {
        private static Hero P => Hero.MainHero;
        private const int MinParentGap = 14;

        public class Member
        {
            public string Id, Name, Culture, Father, Mother, Spouse, Direct, Description, Known, History;
            public bool Female, InClan = true, InParty = true;
            public int Age;
            public Dictionary<string, int> Traits = new Dictionary<string, int>();
            public Dictionary<string, int> Skills = new Dictionary<string, int>();
        }

        private static string S(JToken t) => ((string)t ?? "").Trim();

        public static List<Member> Parse(JArray arr)
        {
            var list = new List<Member>();
            foreach (var x in arr ?? new JArray())
            {
                var m = new Member
                {
                    Id = S(x["id"]), Name = S(x["name"]), Culture = S(x["culture"]), Female = (bool?)x["female"] == true, Age = (int?)x["age"] ?? 20,
                    Father = S(x["father"]), Mother = S(x["mother"]), Spouse = S(x["spouse"]), Direct = S(x["direct"]),
                    Description = S(x["description"]), Known = S(x["known"]), History = S(x["history"]),
                    InClan = (bool?)x["in_clan"] ?? true, InParty = (bool?)x["in_party"] ?? true,
                };
                if (x["traits"] is JObject tr) foreach (var p in tr.Properties()) if (int.TryParse((string)p.Value, out int v)) m.Traits[p.Name] = Math.Max(-2, Math.Min(2, v));
                if (x["skills"] is JObject sk) foreach (var p in sk.Properties()) if (int.TryParse((string)p.Value, out int v)) m.Skills[p.Name] = Math.Max(0, Math.Min(300, v));
                list.Add(m);
            }
            return list;
        }

        /// <summary>Who a reference names: "player", "@hero_id" (someone already in the world) or a member's id. Null if none.</summary>
        private static bool Ref(string r, List<Member> all, out Member member, out Hero hero)
        {
            member = null; hero = null;
            if (string.IsNullOrEmpty(r)) return false;
            if (r == "player") { hero = P; return true; }
            if (r.StartsWith("@")) { hero = Hero.Find(r.Substring(1)); return hero != null && hero.IsAlive; }
            member = all.FirstOrDefault(x => x.Id == r);
            return member != null;
        }

        private static bool FemaleOf(Member m, Hero h) => m != null ? m.Female : h != null && h.IsFemale;
        private static float AgeOf(Member m, Hero h) => m != null ? m.Age : h?.Age ?? 0f;

        /// <summary>Why the family cannot be made as given (in the player's language), or null if it can.</summary>
        public static string Validate(List<Member> all)
        {
            if (Campaign.Current == null || P == null) return L.T("AIL_e7b1de80", "No hay ninguna partida en marcha.");
            if (all.Count == 0) return L.T("AIL_e7b1de81", "Añade al menos un familiar.");
            var ids = new HashSet<string>();
            foreach (var m in all)
            {
                if (m.Id.Length == 0) return L.T("AIL_e7b1de82", "Cada familiar necesita un identificador.");
                if (m.Id.StartsWith("@") || m.Id == "player") return L.T("AIL_e7b1de83", "Los identificadores que empiezan por @ son para personajes que ya existen.");
                if (!ids.Add(m.Id)) return L.F("AIL_e7b1de84", $"Identificador repetido: {m.Id}.");
                if (m.Name.Length < 2) return L.T("AIL_e7b1de85", "Cada familiar necesita un nombre.");
                if (m.Age < 0 || m.Age > 100) return L.F("AIL_e7b1de86", $"Edad no válida para {m.Name}.");
                if (m.Culture.Length > 0 && TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<CultureObject>(m.Culture) == null) return L.F("AIL_e7b1de87", $"Cultura no válida para {m.Name}.");
            }
            foreach (var m in all)
            {
                foreach (var (r, role) in new[] { (m.Father, "father"), (m.Mother, "mother"), (m.Spouse, "spouse") })
                {
                    if (string.IsNullOrEmpty(r)) continue;
                    if (r == m.Id) return L.F("AIL_e7b1de88", $"{m.Name} no puede ser su propio pariente.");
                    if (!Ref(r, all, out var om, out var oh)) return L.F("AIL_e7b1de89", $"Un parentesco de {m.Name} apunta a alguien que no existe.");
                    if (role == "father" && FemaleOf(om, oh)) return L.F("AIL_e7b1de8a", $"El padre de {m.Name} tiene que ser un hombre.");
                    if (role == "mother" && !FemaleOf(om, oh)) return L.F("AIL_e7b1de8b", $"La madre de {m.Name} tiene que ser una mujer.");
                    if (role != "spouse" && AgeOf(om, oh) < m.Age + MinParentGap) return L.F("AIL_e7b1de8c", $"Un padre o una madre de {m.Name} tiene que ser al menos {MinParentGap} años mayor.");
                    if (role == "spouse" && (m.Age < 18 || AgeOf(om, oh) < 18)) return L.F("AIL_e7b1de8d", $"{m.Name} y su cónyuge tienen que ser adultos.");
                }
                switch (m.Direct)
                {
                    case "": break;
                    case "father": if (m.Female) return L.F("AIL_e7b1de8a", $"El padre de {m.Name} tiene que ser un hombre."); if (P.Father != null) return L.T("AIL_42716849", "Ya tienes padre."); if (m.Age < P.Age + MinParentGap) return L.F("AIL_e7b1de8e", $"{m.Name} es demasiado joven para ser tu padre o tu madre."); break;
                    case "mother": if (!m.Female) return L.F("AIL_e7b1de8b", $"La madre de {m.Name} tiene que ser una mujer."); if (P.Mother != null) return L.T("AIL_de5d5b50", "Ya tienes madre."); if (m.Age < P.Age + MinParentGap) return L.F("AIL_e7b1de8e", $"{m.Name} es demasiado joven para ser tu padre o tu madre."); break;
                    case "child": if (m.Age > P.Age - MinParentGap) return L.F("AIL_e7b1de8f", $"{m.Name} es demasiado mayor para ser hijo tuyo."); break;
                    case "sibling": if (P.Father == null && P.Mother == null && !all.Any(x => x.Direct == "father" || x.Direct == "mother")) return L.T("AIL_fb600910", "Para tener hermanos primero hace falta un padre o una madre: crea antes un progenitor."); break;
                    case "spouse": if (P.Spouse != null || all.Count(x => x.Direct == "spouse") > 1) return L.T("AIL_2603bf41", "Ya estás casado."); if (m.Age < 18) return L.F("AIL_e7b1de8d", $"{m.Name} y su cónyuge tienen que ser adultos."); break;
                    default: return L.T("AIL_96a911e0", "Parentesco no válido.");
                }
            }
            if (all.Count(x => x.Direct == "father") > 1 || all.Count(x => x.Direct == "mother") > 1) return L.T("AIL_96a911e0", "Parentesco no válido.");
            // marriages: everyone free, and the pairs consistent (A with B means B with nobody else)
            var partner = new Dictionary<string, string>();
            foreach (var m in all)
            {
                string other = m.Direct == "spouse" ? "player" : m.Spouse;
                if (string.IsNullOrEmpty(other)) continue;
                Ref(other, all, out var om, out var oh);
                if (oh != null && oh.Spouse != null && oh.Spouse.IsAlive) return L.F("AIL_e7b1dec0", $"{oh.Name} ya está casado: no puede casarse con {m.Name}.");
                foreach (var (a, c) in new[] { (m.Id, other), (other, m.Id) })
                {
                    if (partner.TryGetValue(a, out var had) && had != c) return L.F("AIL_e7b1dec1", $"{m.Name} no puede tener dos cónyuges.");
                    partner[a] = c;
                }
            }
            return null;
        }

        /// <summary>Makes them all, then ties them. The heroes made, or null with the reason.</summary>
        public static List<Hero> Create(List<Member> all, out string error)
        {
            error = Validate(all);
            if (error != null) return null;
            var made = new Dictionary<string, Hero>();
            try
            {
                foreach (var m in all)
                {
                    var h = Spawn(m, out error);
                    if (h == null) return null;
                    made[m.Id] = h;
                }
                Hero Who(string r) => string.IsNullOrEmpty(r) ? null : r == "player" ? P : r.StartsWith("@") ? Hero.Find(r.Substring(1)) : made.TryGetValue(r, out var h) ? h : null;
                // the player's own parents first, so a sibling can take them
                foreach (var m in all.Where(x => x.Direct == "father")) P.Father = made[m.Id];
                foreach (var m in all.Where(x => x.Direct == "mother")) P.Mother = made[m.Id];
                foreach (var m in all)
                {
                    var h = made[m.Id];
                    var f = Who(m.Father); var mo = Who(m.Mother);
                    if (f != null) h.Father = f;
                    if (mo != null) h.Mother = mo;
                    switch (m.Direct)
                    {
                        case "child":
                            if (P.IsFemale) { h.Mother = P; if (f == null && P.Spouse != null) h.Father = P.Spouse; }
                            else { h.Father = P; if (mo == null && P.Spouse != null) h.Mother = P.Spouse; }
                            break;
                        case "sibling": if (h.Father == null) h.Father = P.Father; if (h.Mother == null) h.Mother = P.Mother; break;
                    }
                }
                foreach (var m in all)
                {
                    var h = made[m.Id];
                    var sp = m.Direct == "spouse" ? P : Who(m.Spouse);
                    if (sp != null && h.Spouse != sp && sp.Spouse == null && h.Spouse == null) Marry(h, sp);
                }
                foreach (var m in all) Finish(m, made[m.Id]);
                SaveFiles.Flush();
                Log.Info($"[family] family made from the editor: {string.Join(", ", made.Values.Select(h => h.Name))}");
                return made.Values.ToList();
            }
            catch (Exception ex) { Log.Error("Making the family", ex); error = L.T("AIL_e7b1de90", "No se pudo crear la familia; mira ailegacy.log."); return null; }
        }

        private static Hero Spawn(Member m, out string error)
        {
            error = null;
            var culture = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<CultureObject>(m.Culture) ?? P.Culture;
            var templates = CharacterObject.All.Where(c => c.IsTemplate && c.Occupation == Occupation.Wanderer && c.IsFemale == m.Female).ToList();
            var template = templates.Where(c => c.Culture == culture).OrderBy(_ => Guid.NewGuid()).FirstOrDefault() ?? templates.OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
            if (template == null) { error = L.T("AIL_89a46e62", "El juego no tiene un molde para ese sexo."); return null; }
            var st = Settlement.CurrentSettlement ?? P.HomeSettlement ?? Settlement.All.Where(x => x.IsTown).OrderBy(x => x.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D)).First();
            var hero = HeroCreator.CreateSpecialHero(template, st, m.InClan ? Clan.PlayerClan : null, null, Math.Max(1, m.Age));
            var full = new TextObject("{=!}" + m.Name);
            hero.SetName(full, full);
            if (m.InClan)
            {
                hero.SetNewOccupation(Occupation.Lord); People.SyncOccupation(hero);
                if (hero.Clan != Clan.PlayerClan) hero.Clan = Clan.PlayerClan;
            }
            else { hero.Clan = null; if (hero.Occupation != Occupation.Wanderer) hero.SetNewOccupation(Occupation.Wanderer); }
            if (culture != null && hero.Culture != culture) hero.Culture = culture;
            return hero;
        }

        private static void Marry(Hero a, Hero b)
        {
            try { MarriageAction.Apply(a, b, false); } catch (Exception ex) { Log.Warn("[family] wedding: " + ex.Message); }
            if (a.Spouse == b) return;
            a.Spouse = b; if (b.Spouse != a) b.Spouse = a;
            Log.Info($"[family] {a.Name} and {b.Name} married by hand");
        }

        private static readonly Dictionary<string, TraitObject> TraitIds = new Dictionary<string, TraitObject>
        {
            { "honor", DefaultTraits.Honor }, { "mercy", DefaultTraits.Mercy }, { "valor", DefaultTraits.Valor }, { "generosity", DefaultTraits.Generosity }, { "calculating", DefaultTraits.Calculating },
        };

        /// <summary>Traits, skills, what they know and remember, their place, and the personality the AI writes from the description.</summary>
        private static void Finish(Member m, Hero h)
        {
            Sheet(h, m.Traits, m.Skills, m.Description, m.Known, m.History);
            if (h.IsChild) { h.ChangeState(Hero.CharacterStates.NotSpawned); try { h.ChangeState(Hero.CharacterStates.Active); } catch { } return; }
            h.ChangeState(Hero.CharacterStates.Active);
            if (m.InClan && m.InParty) AddHeroToPartyAction.Apply(h, MobileParty.MainParty, true);
            else
            {
                var home = (m.InClan ? Clan.PlayerClan.HomeSettlement : null) ?? h.BornSettlement ?? Settlement.All.FirstOrDefault(s => s.IsTown);
                if (home != null) try { TeleportHeroAction.ApplyImmediateTeleportToSettlement(h, home); } catch (Exception ex) { Log.Warn("[family] placing them: " + ex.Message); }
            }
        }

        /// <summary>The sheet AIInfluence gives a new relative or companion: traits, skills, a description, what they know, a shared history.</summary>
        public static void Sheet(Hero h, Dictionary<string, int> traits, Dictionary<string, int> skills, string description, string known, string history)
        {
            try
            {
                foreach (var kv in traits ?? new Dictionary<string, int>())
                    if (TraitIds.TryGetValue(kv.Key.ToLowerInvariant(), out var t)) h.SetTraitLevel(t, kv.Value);
                foreach (var kv in skills ?? new Dictionary<string, int>())
                {
                    var sk = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<SkillObject>().FirstOrDefault(x => string.Equals(x.StringId, kv.Key.Replace("_", "").Replace(" ", ""), StringComparison.OrdinalIgnoreCase));
                    if (sk != null) h.SetSkillValue(sk, kv.Value);
                }
                var mem = MemoryStore.Get(h.StringId, h.Name.ToString());
                if (!string.IsNullOrWhiteSpace(description))
                {
                    if (mem.Profile == null) mem.Profile = new CharacterProfile();
                    if (string.IsNullOrWhiteSpace(mem.Profile.Backstory)) mem.Profile.Backstory = description;
                }
                if (!string.IsNullOrWhiteSpace(known)) ChatSession.AddNote(mem, $"What you know about {P.Name}: {known}", Settings.S.Get.MaxNotesPerHero, h.Name.ToString());
                if (!string.IsNullOrWhiteSpace(history)) ChatSession.AddNote(mem, $"Your history with {P.Name}: {history}", Settings.S.Get.MaxNotesPerHero, h.Name.ToString());
                if (!string.IsNullOrWhiteSpace(description)) Web.EditorServer.StartGenerate(h, description);
            }
            catch (Exception ex) { Log.Error("Character sheet", ex); }
        }

        /// <summary>Living relatives of the player, for the editor's links ("@id").</summary>
        public static IEnumerable<Hero> Relatives()
        {
            var seen = new HashSet<Hero>();
            void Add(Hero h) { if (h != null && h != P && h.IsAlive) seen.Add(h); }
            Add(P.Father); Add(P.Mother); Add(P.Spouse);
            foreach (var h in P.Children) Add(h);
            foreach (var h in P.Siblings) Add(h);
            foreach (var x in new[] { P.Father, P.Mother, P.Spouse })
                if (x != null) { Add(x.Father); Add(x.Mother); foreach (var s in x.Siblings) Add(s); foreach (var c in x.Children) Add(c); }
            foreach (var h in Clan.PlayerClan.Heroes) Add(h);
            return seen;
        }

        // ============================================================ names of the wider kinship

        private static bool ParentOf(Hero parent, Hero child) => parent != null && child != null && (child.Father == parent || child.Mother == parent);
        private static IEnumerable<Hero> Parents(Hero h) => new[] { h?.Father, h?.Mother }.Where(x => x != null);
        private static bool Siblings(Hero a, Hero b) => a != null && b != null && a != b && Parents(a).Any(p => ParentOf(p, b));
        private static bool FullSiblings(Hero a, Hero b) => a?.Father != null && a.Mother != null && a.Father == b?.Father && a.Mother == b.Mother && a != b;

        /// <summary>What <paramref name="q"/> is to <paramref name="b"/>, in English for prompts ("paternal grandmother"), or null.</summary>
        public static string Resolve(Hero q, Hero b)
        {
            if (q == null || b == null || q == b) return null;
            bool f = q.IsFemale;
            string side(Hero parent) => parent == b.Father ? "paternal " : parent == b.Mother ? "maternal " : "";
            if (b.Spouse == q) return f ? "wife" : "husband";
            if (b.ExSpouses.Contains(q)) return q.IsAlive ? (f ? "former wife" : "former husband") : (f ? "late wife" : "late husband");
            if (b.Father == q) return "father";
            if (b.Mother == q) return "mother";
            if (ParentOf(b, q)) return f ? "daughter" : "son";
            if (FullSiblings(q, b)) return f ? "sister" : "brother";
            if (Siblings(q, b)) return (f ? "half-sister" : "half-brother") + (q.Father != null && q.Father == b.Father ? " (same father)" : " (same mother)");
            foreach (var p in Parents(b))
            {
                if (ParentOf(q, p)) return side(p) + (f ? "grandmother" : "grandfather");
                foreach (var gp in Parents(p)) if (ParentOf(q, gp)) return side(p) + (f ? "great-grandmother" : "great-grandfather");
                if (Siblings(q, p)) return side(p) + (f ? "aunt" : "uncle");
                if (p.Siblings.Any(s => s.Spouse == q)) return side(p) + (f ? "aunt" : "uncle") + " by marriage";
                if (Parents(q).Any(qp => Siblings(qp, p))) return side(p) + (f ? "female first cousin" : "male first cousin");
                if (p.Spouse == q && !ParentOf(q, b)) return f ? "stepmother" : "stepfather";
            }
            foreach (var c in b.Children)
            {
                if (ParentOf(c, q)) return f ? "granddaughter" : "grandson";
                foreach (var gc in c.Children) if (ParentOf(gc, q)) return f ? "great-granddaughter" : "great-grandson";
                if (c.Spouse == q) return f ? "daughter-in-law" : "son-in-law";
            }
            if (Parents(q).Any(qp => Siblings(qp, b))) return f ? "niece" : "nephew";
            if (b.Spouse != null)
            {
                if (ParentOf(q, b.Spouse)) return f ? "mother-in-law" : "father-in-law";
                if (Siblings(q, b.Spouse)) return f ? "sister-in-law" : "brother-in-law";
                if (ParentOf(b.Spouse, q)) return f ? "stepdaughter" : "stepson";
            }
            if (b.Siblings.Any(s => s.Spouse == q)) return f ? "sister-in-law" : "brother-in-law";
            foreach (var p in Parents(b))
                if (p.Spouse != null && !ParentOf(p.Spouse, b) && ParentOf(p.Spouse, q) && !Siblings(q, b)) return f ? "stepsister" : "stepbrother";
            return null;
        }

        /// <summary>The encyclopedia's word for the kinship the game has none for (or a half-sibling), or null.</summary>
        public static string Label(Hero q, Hero b)
        {
            string r = Resolve(q, b);
            if (r == null) return null;
            r = r.Replace("paternal ", "").Replace("maternal ", "").Replace(" by marriage", "");
            int paren = r.IndexOf(" (");
            if (paren > 0) r = r.Substring(0, paren);
            switch (r)
            {
                case "grandfather": return L.T("AIL_e7b1de91", "Abuelo");
                case "grandmother": return L.T("AIL_e7b1de92", "Abuela");
                case "great-grandfather": return L.T("AIL_e7b1de93", "Bisabuelo");
                case "great-grandmother": return L.T("AIL_e7b1de94", "Bisabuela");
                case "great-grandson": return L.T("AIL_e7b1de95", "Bisnieto");
                case "great-granddaughter": return L.T("AIL_e7b1de96", "Bisnieta");
                case "male first cousin": return L.T("AIL_e7b1de97", "Primo");
                case "female first cousin": return L.T("AIL_e7b1de98", "Prima");
                case "half-brother": return L.T("AIL_e7b1de99", "Medio hermano");
                case "half-sister": return L.T("AIL_e7b1de9a", "Media hermana");
                case "stepfather": return L.T("AIL_e7b1de9b", "Padrastro");
                case "stepmother": return L.T("AIL_e7b1de9c", "Madrastra");
                case "stepson": return L.T("AIL_e7b1de9d", "Hijastro");
                case "stepdaughter": return L.T("AIL_e7b1de9e", "Hijastra");
                case "stepbrother": return L.T("AIL_e7b1de9f", "Hermanastro");
                case "stepsister": return L.T("AIL_e7b1dea0", "Hermanastra");
                default: return null;
            }
        }
    }
}
