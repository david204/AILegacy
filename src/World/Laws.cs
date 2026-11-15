using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>One effect of a law: what it changes, by how much and on whom.</summary>
    public class LawEffect
    {
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("value")] public float Value;
        /// <summary>Settlement effects: all, towns, castles or villages.</summary>
        [JsonProperty("scope")] public string Scope = "all";
        /// <summary>all, ruler (party effects only), ruler_clan or other_clans.</summary>
        [JsonProperty("target")] public string Target = "all";
        [JsonProperty("tier")] public int MinTier;
    }

    internal enum LawOn { Settlement, Clan, Party }

    internal sealed class LawKind
    {
        public string Id;
        public LawOn On;
        public bool Percent, Towns, Castles, Villages;
        public string NameId, NameEs;
        public Func<AILegacySettings, float> Min, Max;

        public string Name => L.T(NameId, NameEs);

        public void Bounds(AILegacySettings cfg, out float lo, out float hi)
        {
            float a = Min(cfg), b = Max(cfg);
            lo = Math.Min(0f, Math.Min(a, b)); hi = Math.Max(0f, Math.Max(a, b));
        }

        public float Clamp(float v, AILegacySettings cfg)
        {
            if (float.IsNaN(v) || float.IsInfinity(v)) return 0f;
            Bounds(cfg, out float lo, out float hi);
            return (float)Math.Round(Math.Max(lo, Math.Min(hi, v)), 2);
        }

        public bool FitsScope(string scope) => scope == "towns" ? Towns : scope == "castles" ? Castles : scope == "villages" ? Villages : true;
    }

    /// <summary>
    /// Laws created by rulers and the game's own kingdom policies. A created law is a list of effects that the patches in
    /// Patches\KingdomLawPatch.cs add to the game's calculations, with the law's name as the reason.
    /// </summary>
    internal static class Laws
    {
        public const string Loyalty = "loyalty", Prosperity = "prosperity", Security = "security", Food = "food", Militia = "militia", Hearth = "hearth",
            Workshops = "workshop_production", Veterans = "militia_veterans", Construction = "construction", Recruitment = "recruitment", Tax = "tax_income",
            GarrisonWages = "garrison_wages", Influence = "influence", ClanIncome = "clan_income", PartySize = "party_size", Wages = "troop_wages",
            Morale = "party_morale", Speed = "party_speed", ArmyCost = "army_cost";

        public const int MaxEffectsPerLaw = 6;
        public const string DisplayPrefix = "ailegacy_law_";

        public static readonly List<LawKind> Kinds = new List<LawKind>
        {
            new LawKind { Id = Loyalty, On = LawOn.Settlement, Towns = true, Castles = true, NameId = "AIL_e7b1c5a0", NameEs = "Lealtad al día", Min = s => s.LawLoyaltyMin, Max = s => s.LawLoyaltyMax },
            new LawKind { Id = Prosperity, On = LawOn.Settlement, Towns = true, Castles = true, NameId = "AIL_e7b1c5a1", NameEs = "Prosperidad al día", Min = s => s.LawProsperityMin, Max = s => s.LawProsperityMax },
            new LawKind { Id = Security, On = LawOn.Settlement, Towns = true, Castles = true, NameId = "AIL_e7b1c5a2", NameEs = "Seguridad al día", Min = s => s.LawSecurityMin, Max = s => s.LawSecurityMax },
            new LawKind { Id = Food, On = LawOn.Settlement, Towns = true, Castles = true, NameId = "AIL_e7b1c5a3", NameEs = "Comida al día", Min = s => s.LawFoodMin, Max = s => s.LawFoodMax },
            new LawKind { Id = Militia, On = LawOn.Settlement, Towns = true, Castles = true, Villages = true, NameId = "AIL_e7b1c5a4", NameEs = "Milicia al día", Min = s => s.LawMilitiaMin, Max = s => s.LawMilitiaMax },
            new LawKind { Id = Hearth, On = LawOn.Settlement, Villages = true, NameId = "AIL_e7b1c5a5", NameEs = "Hogares de aldea al día", Min = s => s.LawHearthMin, Max = s => s.LawHearthMax },
            new LawKind { Id = Workshops, On = LawOn.Settlement, Percent = true, Towns = true, NameId = "AIL_e7b1c5a6", NameEs = "Producción de talleres", Min = s => s.LawWorkshopMin, Max = s => s.LawWorkshopMax },
            new LawKind { Id = Veterans, On = LawOn.Settlement, Percent = true, Towns = true, Castles = true, Villages = true, NameId = "AIL_e7b1c5a7", NameEs = "Milicia veterana", Min = s => s.LawVeteransMin, Max = s => s.LawVeteransMax },
            new LawKind { Id = Construction, On = LawOn.Settlement, Percent = true, Towns = true, Castles = true, NameId = "AIL_e7b1c5a8", NameEs = "Velocidad de construcción", Min = s => s.LawConstructionMin, Max = s => s.LawConstructionMax },
            new LawKind { Id = Recruitment, On = LawOn.Settlement, Percent = true, Towns = true, Villages = true, NameId = "AIL_e7b1c5a9", NameEs = "Reposición de reclutas", Min = s => s.LawRecruitmentMin, Max = s => s.LawRecruitmentMax },
            new LawKind { Id = Tax, On = LawOn.Settlement, Percent = true, Towns = true, Castles = true, NameId = "AIL_e7b1c5aa", NameEs = "Recaudación de impuestos", Min = s => s.LawTaxMin, Max = s => s.LawTaxMax },
            new LawKind { Id = GarrisonWages, On = LawOn.Settlement, Percent = true, Towns = true, Castles = true, NameId = "AIL_e7b1c5ab", NameEs = "Salarios de guarnición", Min = s => s.LawGarrisonWagesMin, Max = s => s.LawGarrisonWagesMax },
            new LawKind { Id = Influence, On = LawOn.Clan, NameId = "AIL_e7b1c5ac", NameEs = "Influencia de clan al día", Min = s => s.LawInfluenceMin, Max = s => s.LawInfluenceMax },
            new LawKind { Id = ClanIncome, On = LawOn.Clan, Percent = true, NameId = "AIL_e7b1c5ad", NameEs = "Ingresos de clan", Min = s => s.LawClanIncomeMin, Max = s => s.LawClanIncomeMax },
            new LawKind { Id = PartySize, On = LawOn.Party, NameId = "AIL_e7b1c5ae", NameEs = "Tamaño de partida", Min = s => s.LawPartySizeMin, Max = s => s.LawPartySizeMax },
            new LawKind { Id = Wages, On = LawOn.Party, Percent = true, NameId = "AIL_e7b1c5af", NameEs = "Salarios de tropa", Min = s => s.LawWagesMin, Max = s => s.LawWagesMax },
            new LawKind { Id = Morale, On = LawOn.Party, NameId = "AIL_e7b1c5b0", NameEs = "Moral de partida", Min = s => s.LawMoraleMin, Max = s => s.LawMoraleMax },
            new LawKind { Id = Speed, On = LawOn.Party, Percent = true, NameId = "AIL_e7b1c5b1", NameEs = "Velocidad de partida", Min = s => s.LawSpeedMin, Max = s => s.LawSpeedMax },
            new LawKind { Id = ArmyCost, On = LawOn.Party, Percent = true, NameId = "AIL_e7b1c5b2", NameEs = "Coste de influencia de ejércitos", Min = s => s.LawArmyCostMin, Max = s => s.LawArmyCostMax },
        };

        private static readonly Dictionary<string, LawKind> ById = Kinds.ToDictionary(k => k.Id, StringComparer.OrdinalIgnoreCase);
        // Names a model may use for a kind without it being an error
        private static readonly Dictionary<string, string> Alias = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["settlement_loyalty"] = Loyalty, ["settlement_prosperity"] = Prosperity, ["settlement_security"] = Security, ["settlement_food"] = Food,
            ["settlement_militia"] = Militia, ["village_hearth"] = Hearth, ["hearths"] = Hearth, ["workshops"] = Workshops, ["veterans"] = Veterans,
            ["construction_speed"] = Construction, ["recruits"] = Recruitment, ["tax"] = Tax, ["taxes"] = Tax, ["clan_influence"] = Influence,
            ["income"] = ClanIncome, ["wages"] = Wages, ["morale"] = Morale, ["speed"] = Speed, ["army_influence_cost"] = ArmyCost,
        };

        public static LawKind Kind(string id)
        {
            if (string.IsNullOrWhiteSpace(id)) return null;
            id = id.Trim().Replace("-", "_").Replace(" ", "_");
            if (id.EndsWith("_percent", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 8);
            if (id.EndsWith("_per_day", StringComparison.OrdinalIgnoreCase)) id = id.Substring(0, id.Length - 8);
            if (Alias.TryGetValue(id, out var real)) id = real;
            return ById.TryGetValue(id, out var k) ? k : null;
        }

        public static bool Enabled => S.Get.EnableKingdomLaws;

        // ============================================================ index of effects in force

        internal sealed class Entry
        {
            public LawEffect Fx;
            public LawKind Kind;
            public float Value;          // already inside the limits
            public string Label;
            public TextObject Reason;
        }

        private sealed class Snapshot
        {
            public List<KingdomLaw> Source;
            public int Count;
            public Dictionary<string, Dictionary<string, Entry[]>> ByKingdom = new Dictionary<string, Dictionary<string, Entry[]>>();
        }

        private static readonly object Gate = new object();
        private static readonly Entry[] None = new Entry[0];
        private static volatile Snapshot _snap;
        private static volatile bool _dirty = true;

        /// <summary>Call after the list of laws or the limits change. The daily tick calls it too, so MCM changes land within a day.</summary>
        public static void Invalidate() => _dirty = true;

        // The game models read this many times a second, some from worker threads: they only ever see a finished snapshot.
        private static Snapshot Current()
        {
            var s = _snap;
            var src = WorldSim.State?.Laws;
            if (s != null && !_dirty && ReferenceEquals(s.Source, src) && s.Count == (src?.Count ?? 0)) return s;
            lock (Gate)
            {
                _dirty = false;
                var snap = new Snapshot { Source = src, Count = src?.Count ?? 0 };
                try
                {
                    var cfg = S.Get;
                    if (src != null && cfg.EnableKingdomLaws)
                    {
                        var tmp = new Dictionary<string, Dictionary<string, List<Entry>>>();
                        foreach (var law in src.ToArray())
                        {
                            if (law == null || string.IsNullOrEmpty(law.KingdomId)) continue;
                            Upgrade(law);
                            string label = L.T("AIL_a9c3e7f2", "Ley: ") + SafeName(law);
                            var reason = new TextObject("{=!}" + label);
                            foreach (var fx in law.Effects)
                            {
                                var kind = Kind(fx?.Kind);
                                if (kind == null) continue;
                                float v = kind.Clamp(fx.Value, cfg);
                                if (Math.Abs(v) < 0.005f) continue;
                                if (!tmp.TryGetValue(law.KingdomId, out var byKind)) tmp[law.KingdomId] = byKind = new Dictionary<string, List<Entry>>();
                                if (!byKind.TryGetValue(kind.Id, out var list)) byKind[kind.Id] = list = new List<Entry>();
                                list.Add(new Entry { Fx = fx, Kind = kind, Value = v, Label = label, Reason = reason });
                            }
                        }
                        foreach (var kv in tmp) snap.ByKingdom[kv.Key] = kv.Value.ToDictionary(x => x.Key, x => x.Value.ToArray());
                    }
                }
                catch (Exception ex) { Log.Error("[leyes] índice de efectos", ex); }
                _snap = snap;
                return snap;
            }
        }

        private static string SafeName(KingdomLaw law) => string.IsNullOrWhiteSpace(law?.Name) ? "?" : law.Name.Replace("{", "(").Replace("}", ")");

        /// <summary>Laws from saves before the effect list kept four fixed numbers. They become effects once.</summary>
        private static void Upgrade(KingdomLaw law)
        {
            if (law.Effects == null) law.Effects = new List<LawEffect>();
            void Move(string kind, ref float v)
            {
                if (Math.Abs(v) >= 0.005f && !law.Effects.Any(e => e != null && e.Kind == kind)) law.Effects.Add(new LawEffect { Kind = kind, Value = v });
                v = 0f;
            }
            Move(Loyalty, ref law.LoyaltyPerDay); Move(Prosperity, ref law.ProsperityPerDay); Move(Security, ref law.SecurityPerDay); Move(Food, ref law.FoodPerDay);
        }

        private static Entry[] For(Kingdom k, string kind)
        {
            if (k == null) return None;
            var snap = Current();
            if (snap.ByKingdom.Count == 0) return None;
            return snap.ByKingdom.TryGetValue(k.StringId, out var byKind) && byKind.TryGetValue(kind, out var list) ? list : None;
        }

        /// <summary>True when no kingdom has an effect in force: lets the patches leave at once.</summary>
        public static bool Idle => Current().ByKingdom.Count == 0;

        private static bool FitsClan(LawEffect e, Clan clan, Kingdom k)
        {
            if (clan == null || clan.IsUnderMercenaryService) return false;
            if (e.MinTier > 0 && clan.Tier < e.MinTier) return false;
            switch (e.Target)
            {
                case "ruler": case "ruler_clan": return clan == k.RulingClan;
                case "other_clans": return clan != k.RulingClan;
                default: return true;
            }
        }

        private static bool FitsHero(LawEffect e, Hero hero, Kingdom k) =>
            hero != null && (e.Target == "ruler" ? hero == k.Leader : FitsClan(e, hero.Clan, k));

        private static bool FitsSettlement(LawEffect e, Settlement st, Kingdom k)
        {
            switch (e.Scope)
            {
                case "towns": if (!st.IsTown) return false; break;
                case "castles": if (!st.IsCastle) return false; break;
                case "villages": if (!st.IsVillage) return false; break;
            }
            switch (e.Target)
            {
                case "ruler": case "ruler_clan": return st.OwnerClan == k.RulingClan;
                case "other_clans": return st.OwnerClan != k.RulingClan;
                default: return true;
            }
        }

        private static void Put(Entry e, ref ExplainedNumber n)
        {
            if (e.Kind.Percent) n.AddFactor(e.Value / 100f, e.Reason); else n.Add(e.Value, e.Reason);
        }

        /// <summary>Adds what the kingdom's laws do to that settlement, one line per law.</summary>
        public static void AddForSettlement(Settlement st, string kind, ref ExplainedNumber n)
        {
            var k = st?.OwnerClan?.Kingdom;
            foreach (var e in For(k, kind)) if (FitsSettlement(e.Fx, st, k)) Put(e, ref n);
        }

        /// <summary>The same as a list, for the calculations that have their own way in (TownEffects).</summary>
        public static void SettlementLines(Settlement st, string kind, List<(float, string)> into)
        {
            var k = st?.OwnerClan?.Kingdom;
            foreach (var e in For(k, kind)) if (FitsSettlement(e.Fx, st, k)) into.Add((e.Value, e.Label));
        }

        /// <summary>Sum for that settlement. Percent kinds come back as a fraction (0.1 = +10%).</summary>
        public static float SumForSettlement(Settlement st, string kind)
        {
            var k = st?.OwnerClan?.Kingdom;
            float sum = 0f;
            foreach (var e in For(k, kind)) if (FitsSettlement(e.Fx, st, k)) sum += e.Kind.Percent ? e.Value / 100f : e.Value;
            return sum;
        }

        public static void AddForClan(Clan clan, string kind, ref ExplainedNumber n)
        {
            var k = clan?.Kingdom;
            foreach (var e in For(k, kind)) if (FitsClan(e.Fx, clan, k)) Put(e, ref n);
        }

        private static Kingdom KingdomOfLord(MobileParty party, out Hero leader)
        {
            leader = null;
            if (party == null || !party.IsLordParty) return null;
            leader = party.LeaderHero;
            return leader?.Clan?.Kingdom;
        }

        /// <summary>Effects on parties led by a lord of the kingdom (the player's own party included).</summary>
        public static void AddForParty(MobileParty party, string kind, ref ExplainedNumber n)
        {
            var k = KingdomOfLord(party, out var leader);
            foreach (var e in For(k, kind)) if (FitsHero(e.Fx, leader, k)) Put(e, ref n);
        }

        public static float SumForParty(MobileParty party, string kind)
        {
            var k = KingdomOfLord(party, out var leader);
            float sum = 0f;
            foreach (var e in For(k, kind)) if (FitsHero(e.Fx, leader, k)) sum += e.Kind.Percent ? e.Value / 100f : e.Value;
            return sum;
        }

        // ============================================================ reading a law from the AI

        private static string NormScope(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_"))
            {
                case "towns": case "cities": case "all_towns": case "town": return "towns";
                case "castles": case "all_castles": case "castle": return "castles";
                case "villages": case "all_villages": case "village": return "villages";
                default: return "all";
            }
        }

        private static string NormTarget(string raw)
        {
            switch ((raw ?? "").Trim().ToLowerInvariant().Replace("-", "_").Replace(" ", "_"))
            {
                case "ruler": case "king": case "leader": case "sovereign": return "ruler";
                case "ruler_clan": case "ruling_clan": case "ruling_house": return "ruler_clan";
                case "other_clans": case "vassals": case "non_ruler_clans": case "vassal_clans": return "other_clans";
                default: return "all";
            }
        }

        private static float Num(JToken t)
        {
            try
            {
                if (t == null || t.Type == JTokenType.Null) return 0f;
                if (t.Type == JTokenType.Float || t.Type == JTokenType.Integer) return (float)t;
                string s = t.ToString().Replace("%", "").Replace("+", "").Replace(',', '.').Trim();
                return float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out float v) ? v : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// Effects of a new law, cut to the limits. Takes a list of { effect, value, scope, target, min_clan_tier } or the
        /// short form { "loyalty": 1, "food": -2 } that older templates ask for.
        /// </summary>
        public static List<LawEffect> ParseEffects(JToken raw, List<string> problems)
        {
            var res = new List<LawEffect>();
            var cfg = S.Get;
            void Take(string id, JToken value, string scope, string target, int tier)
            {
                var kind = Kind(id);
                if (kind == null) { if (!string.IsNullOrWhiteSpace(id)) problems?.Add("efecto desconocido «" + id + "»"); return; }
                float v = kind.Clamp(Num(value), cfg);
                if (Math.Abs(v) < 0.005f) return;
                var fx = new LawEffect { Kind = kind.Id, Value = v, Target = NormTarget(target), MinTier = Math.Max(0, Math.Min(6, tier)) };
                if (kind.On == LawOn.Settlement)
                {
                    fx.Scope = NormScope(scope);
                    if (!kind.FitsScope(fx.Scope)) fx.Scope = "all";
                    if (fx.Target == "ruler") fx.Target = "ruler_clan";
                    fx.MinTier = 0;
                }
                else if (kind.On == LawOn.Clan && fx.Target == "ruler") fx.Target = "ruler_clan";
                // The same effect twice on the same people would only be a way round the limit
                if (res.Any(e => e.Kind == fx.Kind && e.Scope == fx.Scope && e.Target == fx.Target && e.MinTier == fx.MinTier)) return;
                if (res.Count < MaxEffectsPerLaw) res.Add(fx);
            }
            try
            {
                if (raw is JArray arr)
                {
                    foreach (var item in arr.OfType<JObject>())
                    {
                        int tier = 0; try { tier = (int)Num(item["min_clan_tier"] ?? item["tier"]); } catch { }
                        Take((string)(item["effect"] ?? item["kind"] ?? item["type"]), item["value"], (string)item["scope"], (string)item["target"], tier);
                    }
                }
                else if (raw is JObject obj)
                {
                    foreach (var p in obj.Properties())
                        if (p.Value.Type != JTokenType.Object && p.Value.Type != JTokenType.Array) Take(p.Name, p.Value, null, null, 0);
                }
            }
            catch (Exception ex) { problems?.Add(ex.Message); }
            return res;
        }

        // ============================================================ words

        private static string Signed(LawKind kind, float v) => v.ToString("+0.##;-0.##", CultureInfo.InvariantCulture) + (kind.Percent ? "%" : "");

        private static IEnumerable<LawEffect> Shown(KingdomLaw law, AILegacySettings cfg) =>
            (law.Effects ?? new List<LawEffect>()).Where(e => Kind(e?.Kind) is LawKind k && Math.Abs(k.Clamp(e.Value, cfg)) >= 0.005f);

        /// <summary>One effect in the player's language: "Lealtad al día: +1 (ciudades)".</summary>
        public static string Describe(LawEffect e)
        {
            var kind = Kind(e?.Kind);
            if (kind == null) return "";
            string text = kind.Name + ": " + Signed(kind, kind.Clamp(e.Value, S.Get));
            var q = new List<string>();
            bool land = kind.On == LawOn.Settlement;
            if (land && e.Scope == "towns") q.Add(L.T("AIL_e7b1c5df", "ciudades"));
            else if (land && e.Scope == "castles") q.Add(L.T("AIL_e7b1c5e0", "castillos"));
            else if (land && e.Scope == "villages") q.Add(L.T("AIL_e7b1c5e1", "aldeas"));
            if (e.Target == "ruler") q.Add(L.T("AIL_e7b1c5e2", "solo el soberano"));
            else if (e.Target == "ruler_clan") q.Add(land ? L.T("AIL_e7b1c5e4", "feudos del clan reinante") : L.T("AIL_e7b1c5e3", "clan reinante"));
            else if (e.Target == "other_clans") q.Add(land ? L.T("AIL_e7b1c5e6", "feudos de los demás clanes") : L.T("AIL_e7b1c5e5", "los demás clanes"));
            if (!land && e.MinTier > 0) q.Add(string.Format(L.T("AIL_e7b1c5e7", "clanes de nivel {0} o más"), e.MinTier));
            return q.Count == 0 ? text : text + " (" + string.Join("; ", q) + ")";
        }

        public static List<string> DescribeAll(KingdomLaw law)
        {
            Upgrade(law);
            return Shown(law, S.Get).Select(Describe).Where(x => x.Length > 0).ToList();
        }

        /// <summary>The same for a prompt, in English.</summary>
        private static string DescribeEn(LawEffect e)
        {
            var kind = Kind(e?.Kind);
            if (kind == null) return "";
            string text = kind.Id.Replace('_', ' ') + " " + Signed(kind, kind.Clamp(e.Value, S.Get)) + (kind.Percent || kind.On == LawOn.Party ? "" : " a day");
            var q = new List<string>();
            bool land = kind.On == LawOn.Settlement;
            if (land && e.Scope != "all" && !string.IsNullOrEmpty(e.Scope)) q.Add(e.Scope);
            if (e.Target == "ruler") q.Add("the ruler only");
            else if (e.Target == "ruler_clan") q.Add(land ? "fiefs of the ruling clan" : "the ruling clan");
            else if (e.Target == "other_clans") q.Add(land ? "fiefs of the other clans" : "the other clans");
            if (!land && e.MinTier > 0) q.Add("clans of tier " + e.MinTier + " or higher");
            return q.Count == 0 ? text : text + " (" + string.Join(", ", q) + ")";
        }

        public static List<KingdomLaw> Of(Kingdom k) =>
            k == null || WorldSim.State?.Laws == null ? new List<KingdomLaw>() : WorldSim.State.Laws.Where(l => l != null && l.KingdomId == k.StringId).ToList();

        private static string LinesEn(Kingdom k, bool withText)
        {
            var cfg = S.Get;
            var sb = new StringBuilder();
            double now = CampaignTime.Now.ToDays;
            foreach (var law in Of(k))
            {
                Upgrade(law);
                var fx = Shown(law, cfg).Select(DescribeEn).Where(x => x.Length > 0).ToList();
                sb.Append("- ").Append(SafeName(law)).Append($" (decreed by {law.AuthorName}, {(int)Math.Max(0, now - law.EnactedDay)} day(s) ago)");
                if (fx.Count > 0) sb.Append(": ").Append(string.Join("; ", fx));
                if (withText && !string.IsNullOrWhiteSpace(law.Text)) sb.Append(". Text: ").Append(law.Text.Length > 220 ? law.Text.Substring(0, 220) + "…" : law.Text);
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Short mention for the list of kingdoms every ruler reads.</summary>
        public static string DescribeLaws(Kingdom k)
        {
            if (!Enabled) return "";
            var laws = Of(k);
            return laws.Count == 0 ? "" : "laws in force: " + string.Join("; ", laws.Select(SafeName));
        }

        // ============================================================ the game's own policies

        public static bool PoliciesEnabled => Enabled && S.Get.EnableNativePolicies;

        private static string PolicyName(PolicyObject p) => (p?.Name?.ToString() ?? p?.StringId ?? "?").Replace("{", "(").Replace("}", ")");

        private static string PolicyEffects(PolicyObject p, int max)
        {
            string s = (p?.SecondaryEffects?.ToString() ?? "").Replace("\r", "").Replace("\n", "; ").Replace("{", "(").Replace("}", ")").Trim();
            return s.Length > max ? s.Substring(0, max) + "…" : s;
        }

        public static PolicyObject FindPolicy(string idOrName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(idOrName)) return null;
                string raw = idOrName.Trim(), n = WorldSim.Norm(idOrName);
                if (n.Length < 3) return null;
                var all = PolicyObject.All.Where(p => p != null && p.StringId != null && !IsDisplayPolicy(p)).ToList();
                return all.FirstOrDefault(p => string.Equals(p.StringId, raw, StringComparison.OrdinalIgnoreCase))
                    ?? all.FirstOrDefault(p => WorldSim.Norm(p.StringId) == n || WorldSim.Norm(p.StringId) == "policy" + n || WorldSim.Norm(PolicyName(p)) == n);
            }
            catch { return null; }
        }

        private static string PoliciesInForceEn(Kingdom k)
        {
            var list = (k?.ActivePolicies ?? new List<PolicyObject>()).Where(p => p != null).ToList();
            return list.Count == 0 ? "(none)" : string.Join("\n", list.Select(p => $"- {PolicyName(p)} [id: {p.StringId}]: {PolicyEffects(p, 160)}"));
        }

        private static string PoliciesAvailableEn(Kingdom k)
        {
            if (!PoliciesEnabled) return "(none: this crown keeps to its own decrees)";
            var list = PolicyObject.All.Where(p => p != null && !IsDisplayPolicy(p) && !k.ActivePolicies.Contains(p)).ToList();
            return list.Count == 0 ? "(none)" : string.Join("\n", list.Select(p => $"- {PolicyName(p)} [id: {p.StringId}]: {PolicyEffects(p, 160)}"));
        }

        // ============================================================ what is sent to the AI

        private static readonly Tpl TRuler = new Tpl("kingdom_statement/laws.txt",
@"## Laws of {kingdom}
Laws proclaimed by the crown and in force now ({law_count} of at most {max_laws}); repeal_law takes the name:
{laws}
Established policies in force (repeal_policy takes the id):
{policies_in_force}
Established policies the realm does not have yet (enact_policy takes the id):
{policies_available}

Catalogue of effects for enact_law. law_effects is a list of { ""effect"": id, ""value"": number, ""scope"": ""..."", ""target"": ""..."", ""min_clan_tier"": 0 }. A value outside the range is cut down to it. Use one to four effects, and let a law cost something as well as give something: a law with only gains is not believable.
Effects on settlements. scope = all, towns, castles or villages (only the kinds named in brackets). target = all, ruler_clan (only the fiefs of the ruling house) or other_clans (only the fiefs of the vassals).
- loyalty [towns, castles]: loyalty gained or lost each day. Range {range_loyalty}.
- prosperity [towns, castles]: prosperity gained or lost each day. Range {range_prosperity}.
- security [towns, castles]: security gained or lost each day. Range {range_security}.
- food [towns, castles]: food stocks gained or lost each day. Range {range_food}.
- militia [towns, castles, villages]: militia gained or lost each day. Range {range_militia}.
- hearth [villages]: village hearths (people) gained or lost each day. Range {range_hearth}.
- workshop_production [towns]: how fast workshops produce, percent. Range {range_workshop_production}.
- militia_veterans [towns, castles, villages]: chance that new militia are veterans, percent. Range {range_militia_veterans}.
- construction [towns, castles]: daily building progress, percent. Range {range_construction}.
- recruitment [towns, villages]: how fast notables get new recruits, percent. Range {range_recruitment}.
- tax_income [towns, castles]: tax gathered by the owner, percent. Range {range_tax_income}.
- garrison_wages [towns, castles]: what garrisons cost their owner, percent; positive is dearer. Range {range_garrison_wages}.
Effects on the clans of the realm (mercenaries are never touched). target = all, ruler_clan or other_clans; min_clan_tier limits it to clans of that tier or higher.
- influence: influence gained or lost each day by each clan. Range {range_influence}.
- clan_income: income of each clan, percent. Range {range_clan_income}.
Effects on the parties led by the lords of the realm. target = all, ruler (only the sovereign's own party), ruler_clan or other_clans; min_clan_tier as above.
- party_size: how many troops a party may hold. Range {range_party_size}.
- troop_wages: wages of the party, percent; positive is dearer. Range {range_troop_wages}.
- party_morale: morale of the party. Range {range_party_morale}.
- party_speed: speed on the map, percent. Range {range_party_speed}.
- army_cost: influence it costs to call parties into an army, percent; negative is cheaper. Range {range_army_cost}.");

        private static readonly Tpl TSubject = new Tpl("context/kingdom_laws.txt",
@"The law of {kingdom}, the realm you live under. You know it as anyone here does, and you feel it in your purse, your fields or your banner. Speak of it only when it comes up or touches what is being talked about; do not recite it.
{laws}");

        public static void Touch() { var _ = TRuler.Key + TSubject.Key; }

        /// <summary>Section for a ruler about to speak: laws, policies and what a new law may do.</summary>
        public static string ForRuler(Kingdom k)
        {
            try
            {
                if (k == null || !Enabled) return "";
                var cfg = S.Get;
                var pairs = new List<object>
                {
                    "kingdom", k.Name, "law_count", Of(k).Count, "max_laws", cfg.MaxLawsPerKingdom,
                    "laws", Of(k).Count == 0 ? "(none)" : LinesEn(k, true),
                    "policies_in_force", PoliciesInForceEn(k), "policies_available", PoliciesAvailableEn(k),
                };
                foreach (var kind in Kinds)
                {
                    kind.Bounds(cfg, out float lo, out float hi);
                    string unit = kind.Percent ? "%" : "";
                    pairs.Add("range_" + kind.Id);
                    pairs.Add(lo.ToString("0.##", CultureInfo.InvariantCulture) + unit + " to " + hi.ToString("0.##", CultureInfo.InvariantCulture) + unit);
                }
                return TRuler.Format(pairs.ToArray());
            }
            catch (Exception ex) { Log.Error("[leyes] texto para el soberano", ex); return ""; }
        }

        /// <summary>What someone living in that kingdom knows of its laws. Empty when there is nothing to know.</summary>
        public static string ForSubject(Kingdom k)
        {
            try
            {
                if (k == null || !Enabled) return "";
                var sb = new StringBuilder();
                string laws = LinesEn(k, true);
                if (laws.Length > 0) sb.AppendLine(laws);
                var old = (k.ActivePolicies ?? new List<PolicyObject>()).Where(p => p != null).Select(PolicyName).ToList();
                if (laws.Length == 0 && old.Count == 0) return "";
                if (old.Count > 0) sb.AppendLine("- Old policies of the realm, voted by its lords: " + string.Join(", ", old.Take(12)) + ".");
                return TSubject.Format("kingdom", k.Name, "laws", sb.ToString().TrimEnd());
            }
            catch { return ""; }
        }

        // ============================================================ Kingdom > Policies screen

        private static readonly Dictionary<string, PolicyObject> Display = new Dictionary<string, PolicyObject>();
        private static readonly Dictionary<string, string> DisplayStamp = new Dictionary<string, string>();

        public static bool IsDisplayPolicy(PolicyObject p) => p?.StringId != null && p.StringId.StartsWith(DisplayPrefix, StringComparison.Ordinal);

        /// <summary>
        /// Created laws as policy objects for the kingdom screen only. They are never registered with the game nor added to
        /// the kingdom, so nothing of them reaches the save.
        /// </summary>
        public static List<PolicyObject> DisplayPolicies(Kingdom k)
        {
            var res = new List<PolicyObject>();
            if (k == null || !Enabled) return res;
            foreach (var law in Of(k))
            {
                if (string.IsNullOrEmpty(law.Id)) continue;
                var lines = DescribeAll(law);
                string name = SafeName(law);
                string text = string.IsNullOrWhiteSpace(law.Text) ? string.Format(L.T("AIL_e7b1c5ed", "Ley proclamada por {0}."), law.AuthorName) : law.Text.Replace("{", "(").Replace("}", ")");
                string stamp = name + "|" + text + "|" + string.Join("|", lines);
                if (!Display.TryGetValue(law.Id, out var p) || !DisplayStamp.TryGetValue(law.Id, out var had) || had != stamp)
                {
                    p = new PolicyObject(DisplayPrefix + law.Id);
                    p.Initialize(new TextObject("{=!}" + name), new TextObject("{=!}" + text), new TextObject("{=!}" + name),
                        new TextObject("{=!}" + (lines.Count > 0 ? string.Join("\n", lines) : "—")), 0f, 0f, 0f);
                    Display[law.Id] = p; DisplayStamp[law.Id] = stamp;
                }
                res.Add(p);
            }
            return res;
        }
    }
}
