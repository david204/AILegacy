using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace AILegacy.World
{
    /// <summary>The garrison or the militia of a settlement, down with the outbreak of the place.</summary>
    public class ForceSickness
    {
        public const int Militia = 0, Garrison = 1;

        [JsonProperty("st")] public string SettlementId;
        [JsonProperty("kind")] public int Kind;
        [JsonProperty("name")] public string Name;
        [JsonProperty("name_en")] public string NameEn;
        [JsonProperty("severity")] public int Severity = 1;
        [JsonProperty("fx")] public DiseaseFx Fx;
        [JsonProperty("progress")] public float Progress = 15f;
        [JsonProperty("count")] public float Count;            // sick men (the game counts militia in fractions)
        [JsonProperty("start")] public double StartDay;
        [JsonProperty("care_day")] public double CareDay;      // last round of the physicians
        [JsonProperty("loss")] public float Loss;              // militia dying today, taken through the game's militia model
    }

    public class DiseaseDeath
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("day")] public double Day;
        /// <summary>The hero was seen dead right after the disease killed them. Not set in records from before this was noted, nor while the player's own death waits for the heir.</summary>
        [JsonProperty("done", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Done;
    }

    /// <summary>One line of a map tooltip.</summary>
    internal struct TipLine
    {
        public const int Text = 0, Space = 1, Rule = 2;
        public string Label, Value;
        public int Kind;
    }

    /// <summary>
    /// The rest of the disease system: healing herbs sold by the hospitals, sick garrisons and militias, lords who ride off to be
    /// treated, treatment owed by the clan, what a hero died of, and the lines for the map tooltips.
    /// </summary>
    internal static partial class Diseases
    {
        // ============================================================ how good the physicians of a place are

        private static readonly float[] TierRecovery = { 1f, 1f, 1.3f, 1.7f, 2f };
        private static readonly float[] TierImmunity = { 0.05f, 0.05f, 0.1f, 0.2f, 0.3f };
        private static readonly int[] TierBestHerb = { 2, 2, 3, 4, 5 };

        /// <summary>
        /// 1 to 4: a village or poor castle, up to a rich town. The seat of a ruler counts one more. The hospital window
        /// shows this same number, and the strength and prices of the local care follow it (CareQuality, HospitalPrice).
        /// </summary>
        private static int MedicalTier(Settlement st)
        {
            if (st == null || st.IsVillage || st.Town == null) return 1;
            float p = st.Town.Prosperity;
            if (st.IsCastle) return p >= 1500f ? 2 : 1;
            int tier = p >= 7000f ? 4 : p >= 4000f ? 3 : p >= 2000f ? 2 : 1;
            return IsCapital(st) ? Math.Min(4, tier + 1) : tier;
        }

        private static bool IsCapital(Settlement st)
        {
            try
            {
                if (!(st.MapFaction is Kingdom k)) return false;
                var seat = k.RulingClan?.Settlements.Where(s => s.IsTown).OrderByDescending(s => s.Town.Prosperity).FirstOrDefault()
                           ?? k.Settlements.Where(s => s.IsTown).OrderByDescending(s => s.Town.Prosperity).FirstOrDefault();
                return seat == st;
            }
            catch { return false; }
        }

        /// <summary>What the player's party is spared: the hospital's prevention and the herbs drunk there, one on top of the other.</summary>
        private static float PlayerGuard
        {
            get
            {
                var st = WorldSim.State;
                double now = CampaignTime.Now.ToDays;
                float g = st.PreventionUntilDay > now ? 0.25f : 1f;
                if (st.HerbGuardUntilDay > now && st.HerbGuard > 0f) g *= 1f - Math.Min(0.9f, st.HerbGuard);
                return g;
            }
        }

        // ============================================================ healing herbs

        private sealed class Herb
        {
            public string ItemId, NameId, NameEs;
            public int Strength, Cost, Days;
            public float Guard, Quality;
        }

        // Herbs are not goods any more: the hospital sells them and uses them on the spot. As a way of treatment,
        // Quality multiplies the strength of the local care; drunk against contagion, the whole party catches Guard
        // less for Days. The item ids stay because Module\ModuleData\ailegacy_items.xml still defines those items,
        // so saves that carried them load; RefundCarriedHerbs takes them out of the party and pays them back.
        private static readonly Herb[] Herbs =
        {
            new Herb { ItemId = "ailegacy_herbs_common", NameId = "AIL_e7b1c6f6", NameEs = "Hierbas comunes", Strength = 1, Cost = 100, Days = 7, Guard = 0.1f, Quality = 1.15f },
            new Herb { ItemId = "ailegacy_herbs_rare", NameId = "AIL_e7b1c6f7", NameEs = "Hierbas raras", Strength = 2, Cost = 300, Days = 10, Guard = 0.15f, Quality = 1.3f },
            new Herb { ItemId = "ailegacy_herbs_valuable", NameId = "AIL_e7b1c6f8", NameEs = "Hierbas valiosas", Strength = 3, Cost = 700, Days = 12, Guard = 0.2f, Quality = 1.5f },
            new Herb { ItemId = "ailegacy_herbs_exceptional", NameId = "AIL_e7b1c6f9", NameEs = "Hierbas excepcionales", Strength = 4, Cost = 1500, Days = 14, Guard = 0.3f, Quality = 1.75f },
            new Herb { ItemId = "ailegacy_herbs_legendary", NameId = "AIL_e7b1c6fa", NameEs = "Hierbas legendarias", Strength = 5, Cost = 3000, Days = 14, Guard = 0.4f, Quality = 2f },
        };

        private static bool HerbsOn => Enabled && S.Get.EnableHealingHerbs;

        private static ItemObject HerbItem(Herb h)
        {
            try { return MBObjectManager.Instance?.GetObject<ItemObject>(h.ItemId); } catch { return null; }
        }

        private static string HerbName(Herb h) => L.T(h.NameId, h.NameEs);
        private static int HerbPrice(Herb h) => Math.Max(1, (int)(h.Cost * S.Get.DiseasePreventionHerbCostMultiplier * (1f - HerbDiscount(Settlement.CurrentSettlement))));

        /// <summary>Doses one case takes: more for a worse disease, and for a band by the root of its size like the hospital's bill.</summary>
        private static int HerbUnits(Sickness s)
        {
            float u = 1f + (Sev(s.Severity) - 1) * 0.5f;
            if (s.Kind != Sickness.Hero) u *= (float)Math.Sqrt(Math.Max(1, s.Count)) * S.Get.DiseaseTreatmentTroopMultiplier;
            return Math.Max(1, (int)Math.Ceiling(u));
        }

        /// <summary>Doses the whole party drinks against contagion.</summary>
        private static int GuardUnits() => Math.Max(1, (int)Math.Ceiling(MobileParty.MainParty.MemberRoster.TotalManCount / 25f));

        /// <summary>The party drinks a herb against contagion, paid for in the hospital. It replaces whatever herb was drunk before.</summary>
        private static void DrinkHerb(Herb h)
        {
            var st = WorldSim.State;
            st.HerbGuard = h.Guard;
            st.HerbGuardUntilDay = CampaignTime.Now.ToDays + h.Days;
            WorldSim.Say(L.F("AIL_e7b1c70c", $"Tu partida toma {HerbName(h)}: enfermará un {(int)Math.Round(h.Guard * 100f)}% menos durante {h.Days} días."), Colors.Green);
        }

        private static int _refundSession = -1;

        /// <summary>
        /// Once per loaded campaign, on the map: herbs carried from older versions leave the party's inventory and their
        /// price is paid back. Herbs are bought and used in the hospital now.
        /// </summary>
        private static void RefundCarriedHerbs()
        {
            if (_refundSession == WorldSim.Session || !WorldSim.OnMap()) return;
            _refundSession = WorldSim.Session;
            try
            {
                var party = MobileParty.MainParty;
                if (party == null || Hero.MainHero == null) return;
                var roster = party.ItemRoster;
                int doses = 0, gold = 0;
                foreach (var h in Herbs)
                {
                    var item = HerbItem(h);
                    if (item == null) continue;
                    int n = roster.GetItemNumber(item);
                    if (n <= 0) continue;
                    roster.AddToCounts(item, -n);
                    doses += n;
                    gold += n * HerbPrice(h);
                }
                if (doses <= 0) return;
                Hero.MainHero.ChangeHeroGold(gold);
                Log.Info($"[disease] {doses} carried herb dose(s) removed from the party and {gold} denars paid back");
                WorldSim.Say(L.F("AIL_e7b1c945", $"Las hierbas curativas ya no se llevan encima: se compran y se usan en el hospital. Te devuelven {gold} denarios por las {doses} dosis que llevabas."), Colors.Yellow);
            }
            catch (Exception ex) { Log.Error("[disease] refund of carried herbs", ex); }
        }

        // ============================================================ garrisons and militias

        private static List<ForceSickness> Forces => WorldSim.State.SickForces;
        private const int MaxForces = 150;

        private sealed class ForceIndex
        {
            public List<ForceSickness> Source;
            public int Count;
            public readonly Dictionary<string, ForceSickness[]> By = new Dictionary<string, ForceSickness[]>();
        }

        private static volatile ForceIndex _forceIndex;
        private static double _militiaModelDay = -1;

        // Asked by the militia model and the tooltips, so it is a table rebuilt only when the list changes.
        private static ForceIndex ForceIx()
        {
            var list = WorldSim.State?.SickForces;
            var ix = _forceIndex;
            if (ix != null && ReferenceEquals(ix.Source, list) && ix.Count == (list?.Count ?? 0)) return ix;
            var n = new ForceIndex { Source = list, Count = list?.Count ?? 0 };
            if (list != null)
                foreach (var f in list.ToArray())
                {
                    if (f?.SettlementId == null || f.Kind < 0 || f.Kind > 1) continue;
                    if (!n.By.TryGetValue(f.SettlementId, out var pair)) n.By[f.SettlementId] = pair = new ForceSickness[2];
                    pair[f.Kind] = f;
                }
            _forceIndex = n;
            return n;
        }

        public static bool AnyForces
        {
            get { try { return WorldSim.State != null && WorldSim.State.SickForces.Count > 0 && Enabled; } catch { return false; } }
        }

        public static bool AnyOutbreak
        {
            get { try { return WorldSim.State != null && WorldSim.State.Outbreaks.Count > 0 && Enabled; } catch { return false; } }
        }

        public static ForceSickness ForceOf(Settlement st, int kind) =>
            st == null || !AnyForces ? null : ForceIx().By.TryGetValue(st.StringId, out var pair) ? pair[kind] : null;

        private static void RemoveForce(ForceSickness f) { Forces.Remove(f); _forceIndex = null; }

        private static int GarrisonMen(Settlement st) => st?.Town?.GarrisonParty?.MemberRoster?.TotalRegulars ?? 0;

        /// <summary>The game's militia model went through our hook: it takes the militia's dead itself.</summary>
        public static void MilitiaModelSeen()
        {
            try { if (Campaign.Current != null) _militiaModelDay = CampaignTime.Now.ToDays; } catch { }
        }

        private static bool MilitiaViaModel(double now)
        {
            if (_militiaModelDay < 0) { _militiaModelDay = now; return true; }      // first day of the session: assume yes
            return now - _militiaModelDay < 2.0;
        }

        /// <summary>Militia a settlement loses to disease today, for the game's own count of its militia.</summary>
        public static float MilitiaLoss(Settlement st, out string name)
        {
            name = null;
            var f = ForceOf(st, ForceSickness.Militia);
            if (f == null || f.Loss <= 0f) return 0f;
            name = f.Name;
            return f.Loss;
        }

        /// <summary>Every six hours, the garrison and the militia of a stricken place may catch what goes around it.</summary>
        private static void ForceRound()
        {
            if (!S.Get.DiseaseGarrisonsFallIll) return;
            double now = CampaignTime.Now.ToDays;
            foreach (var o in All.ToList())
            {
                if (Forces.Count >= MaxForces) break;
                var st = Settlement.Find(o.SettlementId);
                if (st == null || st.IsHideout) continue;
                if (Roll(Math.Min(0.3f, TierImmunity[MedicalTier(st)]))) continue;      // a well-kept place holds it off this time
                float k = (1f + o.Severity * 0.1f) * PlaceFactor(o, st);
                float militia = st.Militia;
                if (militia >= 1f && ForceOf(st, ForceSickness.Militia) == null && Roll(0.125f * k)) AddForce(o, st, ForceSickness.Militia, militia, now);
                int garrison = GarrisonMen(st);
                if (garrison > 0 && ForceOf(st, ForceSickness.Garrison) == null && Roll(0.1f * k)) AddForce(o, st, ForceSickness.Garrison, garrison, now);
            }
        }

        private static void AddForce(Outbreak o, Settlement st, int kind, float total, double now)
        {
            Forces.Add(new ForceSickness
            {
                SettlementId = st.StringId, Kind = kind, Name = o.Name, NameEn = o.NameEn, Severity = Sev(o.Severity), Fx = o.Fx,
                Count = Math.Max(1f, total * 0.05f), StartDay = now, CareDay = now,
            });
            _forceIndex = null;
            // nothing on screen: the settlement's tooltip shows its sick garrison and militia
            Log.Info($"[disease] {(kind == ForceSickness.Militia ? "the militia" : "the garrison")} of {st.Name} falls ill with {o.NameEn}");
        }

        private static void ForceDaily(double now)
        {
            var cfg = S.Get;
            if (!cfg.DiseaseGarrisonsFallIll)
            {
                if (Forces.Count > 0) { Forces.Clear(); _forceIndex = null; }
                return;
            }
            if (Forces.Count == 0) return;
            bool viaModel = MilitiaViaModel(now);
            foreach (var f in Forces.ToList())
            {
                try { TickForce(f, now, cfg, viaModel); }
                catch (Exception ex) { Log.Error("[disease] garrison day", ex); }
            }
        }

        /// <summary>
        /// One day for a sick garrison or militia. While the outbreak lasts it spreads among them and worsens; the
        /// physicians of the place make a round every few days; once the outbreak is over they see to them daily.
        /// A serious disease that gets far kills some each day.
        /// </summary>
        private static void TickForce(ForceSickness f, double now, AILegacySettings cfg, bool viaModel)
        {
            var st = f?.SettlementId == null ? null : Settlement.Find(f.SettlementId);
            if (st == null) { RemoveForce(f); return; }
            bool militia = f.Kind == ForceSickness.Militia;
            var garrison = militia ? null : st.Town?.GarrisonParty;
            float total = militia ? st.Militia : garrison?.MemberRoster?.TotalRegulars ?? 0;
            f.Loss = 0f;
            if (total < 1f) { RemoveForce(f); return; }
            f.Count = Math.Min(f.Count, total);
            var o = At(st);
            if (o != null && o.NameEn != f.NameEn) o = null;
            int sev = Sev(f.Severity);
            bool closed = o != null && o.Quarantined;
            if (o != null)
            {
                float spread = FxOf(sev, false, f.Fx).Spread;
                f.Count = Math.Min(total, f.Count + (total - f.Count) * 0.025f * spread * (1f + sev * 0.1f) * (closed ? cfg.QuarantineInfectionReduction : 1f));
                f.Progress += DailyWorse[sev] * cfg.DiseaseProgressionMultiplier;
            }
            int every = Math.Max(1, cfg.DiseaseForceCareEveryDays);
            if (o == null || now - f.CareDay >= every)
            {
                f.Progress -= DailyBetter[sev] * cfg.DiseaseRecoveryMultiplier * TierRecovery[MedicalTier(st)] * Difficulty(f.Progress) * (closed ? 1.3f : 1f) * (o == null ? 1f : every);
                f.CareDay = now;
            }
            f.Progress = Math.Max(0f, Math.Min(sev <= 2 ? 90f : 100f, f.Progress));
            if (f.Progress <= 0f || f.Count < 0.5f) { RemoveForce(f); return; }
            float rate = DailyDeathRate(f.Progress, sev, FxOf(sev, false, f.Fx).Death);
            if (rate <= 0f) return;
            float toll = Math.Min(total, militia ? f.Count * rate : Whole(f.Count * rate));
            if (toll <= 0f) return;
            if (militia)
            {
                f.Loss = toll;
                f.Count = Math.Max(0f, f.Count - toll);
                // another mod replaced the militia model: nobody would take them, so they are taken here
                if (!viaModel) { st.Militia = Math.Max(0f, st.Militia - toll); f.Loss = 0f; }
                return;
            }
            if (garrison == null || garrison.MapEvent != null || st.IsUnderSiege) return;      // not while they fight
            int dead = KillRegulars(garrison.MemberRoster, (int)toll);
            f.Count = Math.Max(0f, f.Count - dead);
            if (dead > 0) Log.Info($"[disease] {dead} soldier(s) of the garrison of {st.Name} die of {f.NameEn}");
        }

        // ============================================================ map tooltips

        private static void TipHead(ref List<TipLine> lines)
        {
            if (lines != null) return;
            lines = new List<TipLine>
            {
                new TipLine { Kind = TipLine.Space },
                new TipLine { Label = L.T("AIL_e7b1c715", "Enfermedad"), Value = " " },
                new TipLine { Kind = TipLine.Rule },
            };
        }

        private static string Treated(Sickness s) => s.Treated || s.AfterDays > 0 ? L.T("AIL_e7b1c5fd", " (en tratamiento)") : "";

        private static void TipHero(ref List<TipLine> lines, Hero h, bool extended)
        {
            var l = OfHero(h);
            if (l == null) return;
            for (int i = 0; i < l.Count; i++)
            {
                var s = l[i];
                if (s == null) continue;
                TipHead(ref lines);
                lines.Add(new TipLine { Label = h.Name.ToString(), Value = s.Name + (extended ? " (" + (int)s.Progress + "%)" : "") + Treated(s) });
            }
        }

        private static void TipHeroes(ref List<TipLine> lines, TroopRoster roster, Hero skip, bool extended)
        {
            if (roster == null) return;
            for (int i = 0; i < roster.Count; i++)
            {
                var c = roster.GetCharacterAtIndex(i);
                if (c == null || !c.IsHero || c.HeroObject == skip) continue;
                TipHero(ref lines, c.HeroObject, extended);
            }
        }

        /// <summary>Share of the men who are sick, as the tooltip writes it: whole percents, two decimals below 1%.</summary>
        private static string Rate(int sick, int total)
        {
            if (sick <= 0 || total <= 0) return "0";
            float pct = Math.Min(100f, sick * 100f / total);
            return pct < 1f ? Math.Max(0.01f, pct).ToString("0.##", System.Globalization.CultureInfo.CurrentCulture) : pct.ToString("F0", System.Globalization.CultureInfo.CurrentCulture);
        }

        private static void TipBand(ref List<TipLine> lines, List<Sickness> l, int total, string label, bool extended)
        {
            if (l == null || total <= 0) return;
            int sick = 0;
            for (int i = 0; i < l.Count; i++) if (l[i] != null) sick += l[i].Count;
            if (sick <= 0) return;
            sick = Math.Min(sick, total);
            TipHead(ref lines);
            lines.Add(new TipLine { Label = label, Value = L.F("AIL_e7b1d708", $"{Rate(sick, total)}% infectados ({sick}/{total})") });
            if (!extended) return;
            for (int i = 0; i < l.Count; i++)
            {
                var s = l[i];
                if (s == null || s.Count <= 0) continue;
                lines.Add(new TipLine { Label = "  " + s.Name, Value = L.F("AIL_e7b1c71d", $"{s.Count} enfermos ({(int)s.Progress}%)") + Treated(s) });
            }
        }

        /// <summary>Who is ill in a party, for its map tooltip. Null when nobody is, or when the party's details are hidden.</summary>
        public static List<TipLine> PartyTip(MobileParty p, bool extended)
        {
            if (p == null || !AnySick || !S.Get.DiseaseMapTooltips || p.IsInfoHidden) return null;
            // like the game with a party's speed and what it is doing: only when it is close enough to make out
            if (!p.IsMainParty && !p.IsInspected) return null;
            List<TipLine> lines = null;
            var lead = p.LeaderHero;
            if (lead != null) TipHero(ref lines, lead, extended);
            // only the player rides with companions and keeps noble prisoners worth listing one by one
            if (p.IsMainParty) TipHeroes(ref lines, p.MemberRoster, lead, extended);
            TipBand(ref lines, OfTroops(p), p.MemberRoster.TotalRegulars, L.T("AIL_e7b1c71a", "Tropas"), extended);
            if (p.IsMainParty) TipHeroes(ref lines, p.PrisonRoster, null, extended);
            TipBand(ref lines, OfPrisoners(p), p.PrisonRoster.TotalRegulars, L.T("AIL_e7b1c71b", "Prisioneros"), extended);
            return lines;
        }

        /// <summary>The outbreak of a settlement and its sick garrison and militia, for its map tooltip.</summary>
        public static List<TipLine> SettlementTip(Settlement st)
        {
            if (st == null || !S.Get.DiseaseMapTooltips) return null;
            var o = AnyOutbreak ? At(st) : null;
            var g = ForceOf(st, ForceSickness.Garrison);
            var m = ForceOf(st, ForceSickness.Militia);
            if (o == null && g == null && m == null) return null;
            List<TipLine> lines = null;
            TipHead(ref lines);
            if (o != null) lines.Add(new TipLine { Label = o.Name, Value = SeverityEs(o.Severity) + (o.Quarantined ? L.T("AIL_c1183e43", " (en cuarentena)") : "") });
            if (g != null) lines.Add(new TipLine { Label = L.T("AIL_e7b1c716", "Guarnición"), Value = L.F("AIL_e7b1c71d", $"{(int)Math.Ceiling(g.Count)} enfermos ({(int)g.Progress}%)") });
            if (m != null) lines.Add(new TipLine { Label = L.T("AIL_e7b1c717", "Milicia"), Value = L.F("AIL_e7b1c71d", $"{(int)Math.Ceiling(m.Count)} enfermos ({(int)m.Progress}%)") });
            return lines;
        }

        // ============================================================ what a hero died of

        private static bool NoteDeath(Hero h, Sickness s)
        {
            try
            {
                if (h == null || s == null || !S.Get.DiseaseDeathTexts) return false;
                var d = WorldSim.State.DiseaseDeaths;
                if (d.Count > 400)
                    foreach (var k in d.OrderBy(x => x.Value?.Day ?? 0).Take(100).Select(x => x.Key).ToList()) d.Remove(k);
                d[h.StringId] = new DiseaseDeath { Name = s.Name, Day = CampaignTime.Now.ToDays };
                return true;
            }
            catch { return false; }
        }

        private static void ForgetDeath(Hero h)
        {
            try { WorldSim.State.DiseaseDeaths.Remove(h.StringId); } catch { }
        }

        /// <summary>The disease's own kill went through: from now on the record answers for this death and no other.</summary>
        private static void ConfirmDeath(Hero h)
        {
            try { if (WorldSim.State.DiseaseDeaths.TryGetValue(h.StringId, out var e) && e != null) e.Done = true; } catch { }
        }

        /// <summary>
        /// Daily. A record is only good for the death it was written for: one whose hero is still alive after three
        /// days (the kill did not happen), is gone, or died on some other day is dropped.
        /// </summary>
        private static void PruneDeaths(double now)
        {
            try
            {
                var d = WorldSim.State?.DiseaseDeaths;
                if (d == null || d.Count == 0) return;
                foreach (var kv in d.ToList())
                {
                    var e = kv.Value;
                    if (e == null || string.IsNullOrWhiteSpace(e.Name)) { d.Remove(kv.Key); continue; }
                    if (e.Done || now - e.Day <= DeathWindowDays) continue;
                    // The dead are looked for by hand as well, in case the game's lookup only answers for the living
                    var h = FindHero(kv.Key) ?? Hero.DeadOrDisabledHeroes.FirstOrDefault(x => x != null && x.StringId == kv.Key);
                    if (h != null && h.IsDead && Math.Abs(h.DeathDay.ToDays - e.Day) <= DeathWindowDays) e.Done = true;      // from a save made before Done existed
                    else d.Remove(kv.Key);
                }
            }
            catch (Exception ex) { Log.Error("[disease] review of recorded deaths", ex); }
        }

        private const double DeathWindowDays = 3.0;
        private const KillCharacterAction.KillCharacterActionDetail DiseaseKill = KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge;

        /// <summary>
        /// The disease that killed this hero, or null. The disease kills through the game's old-age death, so any
        /// other kind of death keeps the game's own text even when a record is still around (a kill that did not go
        /// through, the player's death waiting for the heir). Detail is how the game says they died, when the caller
        /// knows it. Besides that the record has to be of this very death: within its days.
        /// </summary>
        public static string DiedOf(Hero h, KillCharacterAction.KillCharacterActionDetail? detail = null)
        {
            try
            {
                var d = WorldSim.State?.DiseaseDeaths;
                if (h == null || d == null || d.Count == 0 || !S.Get.DiseaseDeathTexts) return null;
                if (detail.HasValue && detail.Value != DiseaseKill) return null;
                if (!d.TryGetValue(h.StringId, out var e) || e == null || string.IsNullOrWhiteSpace(e.Name)) return null;
                // A confirmed record belongs to a dead hero. One not confirmed yet only counts while the kill is under way
                if (e.Done && !h.IsDead) return null;
                double day = h.IsDead ? h.DeathDay.ToDays : CampaignTime.Now.ToDays;
                return Math.Abs(day - e.Day) <= DeathWindowDays ? e.Name : null;
            }
            catch { return null; }
        }

        // ============================================================ treatment on the clan's account

        public static int DebtOf(Clan c)
        {
            try
            {
                var d = WorldSim.State?.TreatmentDebt;
                return c == null || d == null || d.Count == 0 ? 0 : d.TryGetValue(c.StringId, out int v) ? v : 0;
            }
            catch { return 0; }
        }

        public static void ClearDebt(Clan c)
        {
            try { if (c != null) WorldSim.State?.TreatmentDebt.Remove(c.StringId); } catch { }
        }

        private static void AddDebt(Clan c, int amount)
        {
            if (c == null || amount <= 0) return;
            var d = WorldSim.State.TreatmentDebt;
            d.TryGetValue(c.StringId, out int had);
            // Added wide and capped: the whole debt goes into one day's expenses
            int total = (int)Math.Min(MaxDebt, Math.Max(0L, had) + amount);
            d[c.StringId] = total;
            Log.Info($"[disease] {c.Name} owes {amount} denars for treatment (total {total})");
        }

        private const long MaxDebt = 1000000;

        public static string DebtLabel => L.T("AIL_e7b1c720", "Tratamiento de enfermedades");

        /// <summary>
        /// A sick lord pays the physicians. Short of gold, the head of the clan covers the rest; in the player's clan
        /// the rest is left owed and goes into the clan's next daily expenses.
        /// </summary>
        private static bool PayCare(Hero payer, int cost, Settlement st)
        {
            if (cost <= 0) return true;
            int has = Math.Max(0, payer.Gold);
            if (has >= cost) { payer.ChangeHeroGold(-cost); return true; }
            int lack = cost - has;
            if (payer.Clan != null && payer.Clan == Clan.PlayerClan)
            {
                if (!S.Get.DiseaseTreatmentDebt) return false;
                if (has > 0) payer.ChangeHeroGold(-has);
                AddDebt(Clan.PlayerClan, lack);
                WorldSim.Say(L.F("AIL_e7b1c721", $"{payer.Name} no puede pagar a los médicos de {st.Name}: quedan {lack} denarios a cuenta de tu clan."), Colors.Yellow);
                return true;
            }
            var head = payer.Clan?.Leader;
            if (head == null || head == payer || !head.IsAlive || head.Gold < lack) return false;
            head.ChangeHeroGold(-lack);
            if (has > 0) payer.ChangeHeroGold(-has);
            return true;
        }

        /// <summary>In the hospital: with the purse short, the physicians put the rest on the clan's account.</summary>
        private static bool PayOrOwe(int cost)
        {
            if (cost <= 0) return true;
            int has = Math.Max(0, Hero.MainHero.Gold);
            if (has >= cost || !S.Get.DiseaseTreatmentDebt) return Pay(cost);
            if (has > 0 && !Pay(has)) return false;
            AddDebt(Clan.PlayerClan, cost - has);
            WorldSim.Say(L.F("AIL_e7b1c722", $"Los médicos apuntan {cost - has} denarios a la cuenta de tu clan: se cobrarán con los gastos del día."), Colors.Yellow);
            return true;
        }

        // ============================================================ lords who ride off to be treated

        private const string CareReason = "care";
        // Hero id -> campaign day until which they are not sent again. Not saved.
        private static readonly Dictionary<string, double> CareRest = new Dictionary<string, double>();
        private static int _careSession = -1;

        /// <summary>How pressing it is, 3 to 10: grows with how far the worst untreated illness has gone and how severe it is. 0 = nothing to treat.</summary>
        private static float CareUrgency(MobileParty p, Hero lead, List<Sickness> mine, List<Sickness> troops, AILegacySettings cfg)
        {
            float progress = 0f;
            int severity = 1;
            if (mine != null)
                foreach (var s in mine)
                    if (NeedsCare(s)) { progress = Math.Max(progress, s.Progress); severity = Math.Max(severity, s.Severity); }
            if (troops != null)
            {
                int total = Math.Max(1, p.MemberRoster.TotalRegulars);
                foreach (var s in troops)
                    if (NeedsCare(s) && s.Count * 100f / total >= cfg.DiseaseAiTreatmentMinTroopShare) { progress = Math.Max(progress, s.Progress); severity = Math.Max(severity, s.Severity); }
            }
            if (progress <= 0f) return 0f;
            return 3f + 7f * Math.Min(1f, progress / 50f) * (0.5f + Sev(severity) / 5f * 0.5f);
        }

        private static bool CareNeeded(List<Sickness> mine, List<Sickness> troops) =>
            (mine != null && mine.Any(NeedsCare)) || (troops != null && troops.Any(NeedsCare));

        private static bool FitForCare(MobileParty p, Settlement st) =>
            st != null && (st.IsTown || st.IsCastle) && !st.IsUnderSiege && st.MapFaction != null && p.MapFaction != null
            && (st.MapFaction == p.MapFaction || (st.IsTown && !st.MapFaction.IsAtWarWith(p.MapFaction)))
            && At(st) == null && !QuarantineRules.Barred(p, st);

        /// <summary>
        /// Hourly: parties already on their way are treated on arrival, or called off when it no longer makes sense.
        /// Every six hours (search): sick lords with nothing better to do are sent to the nearest physicians.
        /// </summary>
        private static void CareRound(double now, bool search)
        {
            var cfg = S.Get;
            if (_careSession != WorldSim.Session)
            {
                // another campaign, or an earlier save of this one: its days are not these
                _careSession = WorldSim.Session;
                CareRest.Clear();
                _militiaModelDay = -1;
            }
            foreach (var o in OrderSystem.Orders.Where(x => x.Auto && x.Reason == CareReason).ToList())
            {
                try
                {
                    var h = Hero.Find(o.HeroId);
                    var p = OrderSystem.PartyLedBy(h);
                    if (h == null || p == null) continue;      // the order tick drops it
                    var st = Settlement.Find(o.TargetId);
                    var mine = OfHero(h);
                    var troops = OfTroops(p);
                    bool need = CareNeeded(mine, troops);
                    if (!cfg.DiseaseAiSeekTreatment || p.Army != null || st == null || st.IsUnderSiege || QuarantineRules.Barred(p, st)
                        || (st.MapFaction != null && p.MapFaction != null && st.MapFaction.IsAtWarWith(p.MapFaction)))
                    {
                        OrderSystem.Cancel(h, silent: true);
                        CareRest[h.StringId] = now + 2;
                        continue;
                    }
                    if (!o.Arrived)
                    {
                        if (!need && p.CurrentSettlement != st) OrderSystem.Cancel(h, silent: true);      // got well on the way
                        continue;
                    }
                    if (!need || p.MapEvent != null) continue;      // treated: resting until the order runs out
                    SeekCare(p, st, mine, troops);
                    if (CareNeeded(OfHero(h), OfTroops(p)))
                    {
                        // could not pay: no point in waiting there
                        OrderSystem.Cancel(h, silent: true);
                        CareRest[h.StringId] = now + 5;
                    }
                }
                catch (Exception ex) { Log.Error("[disease] party in treatment", ex); }
            }
            if (!search || !cfg.DiseaseAiSeekTreatment || !AnySick) return;

            if (CareRest.Count > 400)
                foreach (var k in CareRest.Where(kv => kv.Value < now).Select(kv => kv.Key).ToList()) CareRest.Remove(k);
            var seen = new HashSet<MobileParty>();
            List<Settlement> places = null;
            int sent = 0;
            foreach (var s in Sick.ToList())
            {
                if (s == null || s.Kind == Sickness.Prisoners || !NeedsCare(s)) continue;
                var p = s.Kind == Sickness.Hero ? OrderSystem.PartyLedBy(FindHero(s.TargetId)) : FindParty(s.TargetId);
                if (p == null || !seen.Add(p)) continue;
                try
                {
                    if (places == null) places = Settlement.All.Where(x => x != null && (x.IsTown || x.IsCastle)).ToList();
                    if (SendToCare(p, now, cfg, places)) sent++;
                }
                catch (Exception ex) { Log.Error("[disease] send a party for treatment", ex); }
            }
            if (sent > 0) Log.Info($"[disease] {sent} sick part(ies) going for treatment");
        }

        // Urgency runs from 3 to 10. Below this, a party whose faction is at war stays with its war
        private const float WarUrgency = 6.5f;

        /// <summary>
        /// What the party is doing can be dropped: holding, patrolling or simply travelling, both as its plan and as
        /// what it does this moment. Raiding, besieging, assaulting, engaging, escorting, joining someone, riding to
        /// defend a place or running from an enemy are all left to finish.
        /// </summary>
        private static bool SafeToInterrupt(MobileParty p)
        {
            if (!Idle(p.DefaultBehavior) || !Idle(p.ShortTermBehavior)) return false;
            // travelling to a place under attack is riding to its defence, whatever the behaviour is called
            if (p.DefaultBehavior == AiBehavior.GoToSettlement || p.ShortTermBehavior == AiBehavior.GoToSettlement)
            {
                var to = p.TargetSettlement;
                if (to != null && (to.IsUnderSiege || to.IsUnderRaid || to.Party?.MapEvent != null)) return false;
            }
            return true;
        }

        private static bool Idle(AiBehavior b) =>
            b == AiBehavior.Hold || b == AiBehavior.None || b == AiBehavior.GoToSettlement || b == AiBehavior.GoToPoint || b == AiBehavior.PatrolAroundPoint;

        private static bool AtWar(IFaction f)
        {
            try { return f != null && Kingdom.All.Any(k => k != null && !k.IsEliminated && k != f && k.IsAtWarWith(f)); }
            catch { return false; }
        }

        private static bool SendToCare(MobileParty p, double now, AILegacySettings cfg, List<Settlement> places)
        {
            var h = p.LeaderHero;
            if (h == null || p.IsMainParty || !p.IsActive || !p.IsLordParty || !h.IsLord || p.IsDisbanding) return false;
            if (p.Army != null || p.MapEvent != null || p.SiegeEvent != null || p.BesiegedSettlement != null || p.IsCurrentlyAtSea || p.CurrentSettlement != null) return false;
            if (h.IsPrisoner || OrderSystem.FindAny(h) != null) return false;      // an order of the player's, or a patrol of their own, comes first
            var faction = p.MapFaction;
            if (faction == null || !(faction.IsKingdomFaction || faction.IsMinorFaction)) return false;
            if (CareRest.TryGetValue(h.StringId, out double until) && now < until) return false;
            var mine = OfHero(h);
            var troops = OfTroops(p);
            float urgency = CareUrgency(p, h, mine, troops, cfg);
            if (urgency < cfg.DiseaseAiTreatmentMinUrgency) return false;
            // An order takes the party away from the game's AI: only one doing nothing that matters is taken
            if (!SafeToInterrupt(p)) return false;
            // With a war on, a mild illness waits: the party is asked again on a later round
            if (urgency < WarUrgency && AtWar(faction)) return false;

            float speed = 5f;
            try { speed = Math.Max(1f, Campaign.Current.EstimatedAverageLordPartySpeed); } catch { }
            float reach = cfg.DiseaseAiTreatmentMaxDays * speed * 24f;
            var from = p.GetPosition2D;
            Settlement town = null, castle = null;
            float townAt = float.MaxValue, castleAt = float.MaxValue;
            foreach (var st in places)
            {
                float d = st.GetPosition2D.Distance(from);
                if (d > reach || d >= (st.IsTown ? townAt : castleAt) || !FitForCare(p, st)) continue;
                if (st.IsTown) { town = st; townAt = d; } else { castle = st; castleAt = d; }
            }
            var target = town ?? castle;      // a town's physicians are better than a castle's
            if (target == null) { CareRest[h.StringId] = now + 1; return false; }

            // no use riding there with an empty purse
            int cheapest = (mine ?? new List<Sickness>()).Where(NeedsCare).Select(s => HeroCareCost(s, target))
                .Concat((troops ?? new List<Sickness>()).Where(NeedsCare).Select(s => BandCareCost(s, target))).DefaultIfEmpty(0).Min();
            var head = h.Clan?.Leader;
            bool canPay = h.Gold >= cheapest || (h.Clan == Clan.PlayerClan && cfg.DiseaseTreatmentDebt) || (head != null && head != h && head.IsAlive && head.Gold + Math.Max(0, h.Gold) >= cheapest);
            if (!canPay) { CareRest[h.StringId] = now + 3; return false; }

            double stay = Math.Max(0.5, cfg.DiseaseAiTreatmentStayDays);
            if (!OrderSystem.Give(h, "goto", target.StringId, target.Name.ToString(), cfg.DiseaseAiTreatmentMaxDays + 1.0, waitDays: stay, auto: true, reason: CareReason)) return false;
            CareRest[h.StringId] = now + cfg.DiseaseAiTreatmentMaxDays + stay + 3.0;
            return true;
        }
    }
}
