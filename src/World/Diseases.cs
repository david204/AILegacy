using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.Core;
using TaleWorlds.Localization;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace AILegacy.World
{
    public class Outbreak
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;        // player's language
        [JsonProperty("name_en")] public string NameEn;   // for prompts
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("severity")] public int Severity = 2;   // 1..5
        [JsonProperty("start")] public double StartDay;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("quarantine_until")] public double QuarantineUntilDay = -1;
        /// <summary>What it does to those who catch it. Null = worked out from the severity.</summary>
        [JsonProperty("fx")] public DiseaseFx Fx;
        /// <summary>Brought in by someone down with a seasonal illness: it keeps that illness's mild nature and does not count for the outbreak limits.</summary>
        [JsonProperty("seasonal", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Seasonal;

        /// <summary>Under quarantine: the decree of a ruler, or one the owner set until lifted (Diseases.Quarantines).</summary>
        [JsonIgnore] public bool Quarantined => QuarantineUntilDay > CampaignTime.Now.ToDays || Diseases.ManualQuarantine(SettlementId);
    }

    /// <summary>Law decreed by a ruler: a list of effects on the settlements, clans or parties of the kingdom (see Laws.cs).</summary>
    public class KingdomLaw
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("kingdom")] public string KingdomId;
        [JsonProperty("name")] public string Name;
        [JsonProperty("text")] public string Text;
        [JsonProperty("loyalty")] public float LoyaltyPerDay;
        [JsonProperty("prosperity")] public float ProsperityPerDay;
        [JsonProperty("security")] public float SecurityPerDay;
        [JsonProperty("food")] public float FoodPerDay;
        [JsonProperty("day")] public double EnactedDay;
        [JsonProperty("author")] public string AuthorName;
        // The four numbers above are how saves before the effect list stored a law. Laws.Upgrade moves them in here.
        [JsonProperty("effects")] public List<LawEffect> Effects = new List<LawEffect>();
    }

    /// <summary>
    /// Outbreaks in settlements. One starts only from a world event, or as an epidemic when four heroes down with the
    /// same disease gather in one town or castle; from there sick lords, caravan masters and villagers carry it to other
    /// places (DiseasesPeople.cs). An outbreak hurts the settlement and whoever stays in it, and a quarantine closes the
    /// place and slows it down (kings decide, or the player in their own fiefs or, as ruler, in their vassals').
    /// </summary>
    internal static partial class Diseases
    {
        private static readonly Random Rng = new Random();
        private static List<Outbreak> All => WorldSim.State.Outbreaks;
        public static bool Enabled => S.Get.EnableDiseases;

        public static Outbreak At(Settlement st) => st == null ? null : All.FirstOrDefault(o => o.SettlementId == st.StringId);
        private static string SeverityWord(int s) => s <= 1 ? "mild" : s == 2 ? "troubling" : s == 3 ? "serious" : s == 4 ? "deadly" : "a plague";
        private static string SeverityEs(int s) => s <= 1 ? L.T("AIL_e7b1c94e", "leve") : s == 2 ? L.T("AIL_e7b1c94f", "preocupante") : s == 3 ? L.T("AIL_e7b1c943", "grave") : s == 4 ? L.T("AIL_44011913", "mortífera") : L.T("AIL_e45c3132", "una peste");

        /// <summary>For the prompt of anyone who lives in or is at that settlement.</summary>
        public static string DescribeAt(Settlement st)
        {
            var o = At(st);
            return o == null ? "" : PlaceText(o, st);
        }

        public static string DescribeFor(Kingdom k)
        {
            var list = All.Select(o => (o, st: Settlement.Find(o.SettlementId))).Where(x => x.st != null && x.st.MapFaction == k).ToList();
            return list.Count == 0 ? "" : "sickness in: " + string.Join(", ", list.Select(x => $"{x.st.Name} ({x.o.NameEn}, {SeverityWord(x.o.Severity)}{(x.o.Quarantined ? ", quarantined" : "")})"));
        }

        // ============================================================ outbreaks

        /// <summary>
        /// A new outbreak in a settlement. Only those from world events are held back by the limits set in MCM (how many
        /// at once and how many days apart); one carried in or born of an epidemic is not, as long as the place is free.
        /// </summary>
        public static Outbreak Start(Settlement st, string name, string nameEn, int severity, int days, bool announce = true, DiseaseFx fx = null, bool fromEvent = false, bool seasonal = false)
        {
            var cfg = S.Get;
            if (!Enabled || st == null || st.IsHideout || At(st) != null) return null;
            if (fromEvent && EventOutbreakBlocked()) return null;
            severity = Math.Max(1, Math.Min(cfg.DiseaseMaxSeverity, severity));
            days = Math.Max(7, Math.Min(120, days <= 0 ? 15 + severity * 15 : days));
            var o = new Outbreak
            {
                Id = WorldSim.NewId("d"), SettlementId = st.StringId, Severity = severity, StartDay = CampaignTime.Now.ToDays, UntilDay = CampaignTime.Now.ToDays + days,
                Name = string.IsNullOrWhiteSpace(name) ? L.T("AIL_e7b1c94c", "unas fiebres") : WorldSim.Clean(name, 50), NameEn = string.IsNullOrWhiteSpace(nameEn) ? "a fever" : WorldSim.Clean(nameEn, 50), Fx = fx,
                Seasonal = seasonal,
            };
            // a running quarantine of the place (decreed, or set by its owner) covers the new outbreak too
            All.Add(o);
            if (!seasonal) WorldSim.State.LastOutbreakDay = o.StartDay;
            Log.Info($"[disease] outbreak in {st.Name}: {o.NameEn}, severity {severity}, {days} days{(seasonal ? " (seasonal)" : "")}");
            if (announce)
                NewsStore.Add(new NewsItem
                {
                    Type = "event_disease", Importance = severity >= 4 ? 3 : 2, Text = $"A sickness has broken out in {st.Name}: {o.NameEn} ({SeverityWord(severity)}).",
                    TitleEs = L.F("AIL_4b3f2067", $"Enfermedad en {st.Name}"), TextEs = L.F("AIL_053cbde7", $"Se ha declarado {o.Name} en {st.Name}. Dicen que es {SeverityEs(severity)}."),
                    SettlementId = st.StringId, Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(),
                });
            return o;
        }

        /// <summary>A world event may not start another outbreak: too many going already (seasonal ones aside), or the last one too recent.</summary>
        public static bool EventOutbreakBlocked()
        {
            try
            {
                var cfg = S.Get;
                if (All.Count(o => !o.Seasonal) >= cfg.DiseaseMaxSimultaneous) return true;
                double last = WorldSim.State.LastOutbreakDay;
                return last >= 0 && CampaignTime.Now.ToDays - last < cfg.DiseaseMinDaysBetweenOutbreaks;
            }
            catch { return true; }
        }

        /// <summary>Outbreak from the event generator: { settlement, name, name_en, severity, duration_days }.</summary>
        public static void FromEvent(JObject d)
        {
            if (d == null || !Enabled || !S.Get.DiseaseFromEvents) return;
            var st = OrderSystem.FindSettlement((string)d["settlement"]);
            if (st == null) return;
            if (EventOutbreakBlocked()) { Log.Info($"[disease] the event's outbreak in {st.Name} is skipped: outbreak limit or too soon after the last one"); return; }
            // optional numbers from the event; FxOf keeps them inside the MCM limits when they are used
            DiseaseFx fx = null;
            try
            {
                float spread = (float?)d["spread_rate"] ?? 0f, death = (float?)d["death_chance"] ?? -1f, combat = (float?)d["combat_modifier"] ?? 0f,
                      speed = (float?)d["map_speed_modifier"] ?? 0f, morale = (float?)d["morale_penalty"] ?? 0f, skill = (float?)d["skill_penalty"] ?? 0f;
                if (spread > 0f || death >= 0f || combat > 0f || speed > 0f || morale != 0f || skill != 0f)
                    fx = new DiseaseFx { Spread = spread, Death = death, Combat = combat, MapSpeed = speed, Morale = -Math.Abs(morale), Skill = -Math.Abs(skill) };
            }
            catch { }
            Start(st, (string)d["name"], (string)d["name_en"], (int?)d["severity"] ?? 2, (int?)d["duration_days"] ?? 0, announce: false, fx: fx, fromEvent: true);
        }

        public static void OnDailyTick()
        {
            if (Campaign.Current == null) return;
            try
            {
                var cfg = S.Get;
                double now = CampaignTime.Now.ToDays;
                PruneDeaths(now);
                if (!Enabled)
                {
                    if (All.Count > 0) All.Clear();
                    if (Sick.Count > 0) { Sick.Clear(); Touch(); }
                    if (Forces.Count > 0) Forces.Clear();
                    return;
                }
                PruneQuarantines(now);

                foreach (var o in All.ToList())
                {
                    var st = Settlement.Find(o.SettlementId);
                    if (st == null) { All.Remove(o); continue; }
                    if (now >= o.UntilDay)
                    {
                        All.Remove(o);
                        Log.Info($"[disease] outbreak ended in {st.Name}");
                        NewsStore.Add(new NewsItem
                        {
                            Type = "event_disease", Importance = 1, Text = $"The sickness in {st.Name} has passed.", TitleEs = L.F("AIL_387fb206", $"{st.Name} se libra de la enfermedad"),
                            TextEs = L.F("AIL_f4f38e0c", $"Ya no hay enfermos de {o.Name} en {st.Name}."), SettlementId = st.StringId,
                            Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(),
                        });
                        continue;
                    }

                    float s = o.Severity;
                    if (st.Town != null)
                    {
                        st.Town.Prosperity = Math.Max(0f, st.Town.Prosperity - s * cfg.DiseaseProsperityLossPerSeverity);
                        st.Town.FoodStocks = Math.Max(0f, st.Town.FoodStocks - s * 0.8f);
                        st.Town.Loyalty = Math.Max(0f, st.Town.Loyalty - s * 0.15f);
                        st.Town.Security = Math.Max(0f, st.Town.Security - s * 0.1f);
                    }
                    else if (st.Village != null)
                        st.Village.Hearth = Math.Max(10f, st.Village.Hearth - s * cfg.DiseaseProsperityLossPerSeverity * 0.4f);
                }

                // what a quarantine costs normally goes through the game's own daily calculation (DiseaseExtraPatch.cs);
                // only if that calculation stopped going through our hooks is it taken here by hand
                if (!WorldSim.LawsViaModel()) QuarantineByHand();

                TickSick(now);
                try { ForceDaily(now); } catch (Exception ex) { Log.Error("[disease] garrisons and militias", ex); }
            }
            catch (Exception ex) { Log.Error("[disease] daily tick", ex); }
        }

        public static void OnSettlementEntered(MobileParty party, Settlement st, Hero hero)
        {
            try
            {
                if (!Enabled || party == null || party.IsMainParty) return;
                OnAnyPartyEntered(party, st);
            }
            catch (Exception ex) { Log.Error("[disease] entry", ex); }
        }

        // ============================================================ quarantine

        private static Dictionary<string, double> Quarantines => WorldSim.State.Quarantines ?? (WorldSim.State.Quarantines = new Dictionary<string, double>());

        /// <summary>A quarantine of the place on its own, with or without a disease in it: a ruler's decree for some days, or the owner's until lifted.</summary>
        internal static bool ManualQuarantine(string settlementId)
        {
            try
            {
                var q = WorldSim.State?.Quarantines;
                if (settlementId == null || q == null || q.Count == 0 || !q.TryGetValue(settlementId, out double until)) return false;
                return until < 0 || until > CampaignTime.Now.ToDays;
            }
            catch { return false; }
        }

        public static bool IsQuarantined(Settlement st)
        {
            if (st == null) return false;
            if (ManualQuarantine(st.StringId)) return true;
            var o = At(st);
            return o != null && o.QuarantineUntilDay > CampaignTime.Now.ToDays;
        }

        /// <summary>Some place on the map might be under quarantine: a cheap first check for code that runs very often.</summary>
        public static bool AnyQuarantine
        {
            get
            {
                try
                {
                    var s = WorldSim.State;
                    if (s == null) return false;
                    if (s.Quarantines != null && s.Quarantines.Count > 0) return true;
                    double now = CampaignTime.Now.ToDays;
                    for (int i = 0; i < s.Outbreaks.Count; i++) if (s.Outbreaks[i] != null && s.Outbreaks[i].QuarantineUntilDay > now) return true;
                    return false;
                }
                catch { return false; }
            }
        }

        /// <summary>Day the quarantine of the place ends; -1 while it lasts until someone lifts it; null when there is none.</summary>
        public static double? QuarantineEnd(Settlement st)
        {
            if (st == null) return null;
            double now = CampaignTime.Now.ToDays;
            var q = WorldSim.State?.Quarantines;
            if (q != null && q.TryGetValue(st.StringId, out double until) && (until < 0 || until > now)) return until;
            var o = At(st);
            return o != null && o.QuarantineUntilDay > now ? o.QuarantineUntilDay : (double?)null;
        }

        /// <summary>A ruler's decree: some days of quarantine, added to a timed one already running.</summary>
        public static string Quarantine(Settlement st, int days, string byWhom)
        {
            if (st == null) return null;
            days = Math.Max(5, Math.Min(60, days <= 0 ? 15 : days));
            double now = CampaignTime.Now.ToDays, from = now;
            if (Quarantines.TryGetValue(st.StringId, out double had) && had > now) from = had;
            double until = from + days;
            Quarantines[st.StringId] = until;
            var o = At(st);
            if (o != null) o.QuarantineUntilDay = until;
            try { if (Settlement.CurrentSettlement == st) QuarantineRules.NotePlayerInside(); } catch { }
            Log.Info($"[quarantine] {st.Name} closed for {days} days by {byWhom}");
            return L.F("AIL_34a22788", $"{byWhom} decreta {days} días de cuarentena en {st.Name}.");
        }

        public static string Lift(Settlement st, string byWhom)
        {
            if (st == null || !IsQuarantined(st)) return null;
            LiftQuarantine(st);
            Log.Info($"[quarantine] {st.Name} opened by {byWhom}");
            return L.F("AIL_fa2d5313", $"{byWhom} levanta la cuarentena de {st.Name}.");
        }

        private static void LiftQuarantine(Settlement st)
        {
            Quarantines.Remove(st.StringId);
            var o = At(st);
            if (o != null) o.QuarantineUntilDay = -1;
        }

        /// <summary>The player sets a quarantine with no end (it lasts until they lift it), or lifts whatever quarantine there is.</summary>
        public static void SetPlayerQuarantine(Settlement st, bool on)
        {
            if (st == null) return;
            if (on)
            {
                Quarantines[st.StringId] = -1;
                try { if (Settlement.CurrentSettlement == st) QuarantineRules.NotePlayerInside(); } catch { }
                Log.Info($"[quarantine] the player closes {st.Name} until further notice");
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1d723", $"Cuarentena decretada en {st.Name}. Levántala cuando quieras.")));
            }
            else
            {
                LiftQuarantine(st);
                Log.Info($"[quarantine] the player opens {st.Name}");
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1d722", $"Cuarentena levantada en {st.Name}.")));
            }
        }

        /// <summary>Quarantines whose days are up end; so do those of places that are gone.</summary>
        private static void PruneQuarantines(double now)
        {
            var q = WorldSim.State?.Quarantines;
            if (q == null || q.Count == 0) return;
            foreach (var kv in q.ToList())
            {
                if (Settlement.Find(kv.Key) == null || (kv.Value >= 0 && kv.Value <= now))
                {
                    q.Remove(kv.Key);
                    Log.Info($"[quarantine] the quarantine of {Settlement.Find(kv.Key)?.Name.ToString() ?? kv.Key} is over");
                }
            }
        }

        /// <summary>The player decides on the quarantine of their own fief, or as ruler on a vassal's. Not villages.</summary>
        public static bool CanManageQuarantine(Settlement st)
        {
            if (!Enabled || st == null || !(st.IsTown || st.IsCastle) || Hero.MainHero == null) return false;
            if (st.OwnerClan == Clan.PlayerClan) return true;
            var k = Hero.MainHero.Clan?.Kingdom;
            return Hero.MainHero.IsKingdomLeader && k != null && st.OwnerClan?.Kingdom == k;
        }

        /// <summary>Daily change a quarantine makes to a town or castle (WorldSim stat). It weighs twice as much with no disease to hold back.</summary>
        internal static float QuarantineStat(Settlement st, int stat)
        {
            if (!Enabled || st?.Town == null || !IsQuarantined(st)) return 0f;
            bool sick = At(st) != null;
            if (stat == WorldSim.StatProsperity || stat == WorldSim.StatFood) return sick ? -10f : -20f;
            if (stat == WorldSim.StatLoyalty || stat == WorldSim.StatSecurity) return sick ? 1f : -1f;
            return 0f;
        }

        /// <summary>Share of its tax a quarantined town still brings in.</summary>
        internal static float QuarantineTax(Settlement st)
        {
            if (!Enabled || st?.Town == null || !IsQuarantined(st)) return 1f;
            return At(st) != null ? 0.75f : 0.5f;
        }

        internal static string QuarantineLabel => L.T("AIL_e7b1cb18", "Cuarentena");

        private static void QuarantineByHand()
        {
            var q = WorldSim.State?.Quarantines;
            var places = new HashSet<string>(q?.Keys ?? Enumerable.Empty<string>());
            foreach (var o in All) if (o.QuarantineUntilDay > CampaignTime.Now.ToDays) places.Add(o.SettlementId);
            foreach (var id in places)
            {
                var st = Settlement.Find(id);
                var town = st?.Town;
                if (town == null) continue;
                town.Prosperity = Math.Max(0f, town.Prosperity + QuarantineStat(st, WorldSim.StatProsperity));
                town.FoodStocks = Math.Max(0f, town.FoodStocks + QuarantineStat(st, WorldSim.StatFood));
                town.Loyalty = Math.Max(0f, Math.Min(100f, town.Loyalty + QuarantineStat(st, WorldSim.StatLoyalty)));
                town.Security = Math.Max(0f, Math.Min(100f, town.Security + QuarantineStat(st, WorldSim.StatSecurity)));
            }
        }

        /// <summary>What the quarantine window shows: the place, its diseases and the days left, and what a quarantine does.</summary>
        internal static string QuarantineStatus(Settlement st)
        {
            if (st == null) return "";
            var lines = new List<string> { L.F("AIL_e7b1d711", $"Asentamiento: {st.Name}"), "" };
            var o = Enabled ? At(st) : null;
            bool closed = IsQuarantined(st);
            if (o != null)
            {
                lines.Add(L.T("AIL_e7b1d712", "Enfermedades activas:"));
                lines.Add("• " + L.F("AIL_e7b1d714", $"{o.Name} (gravedad {o.Severity})") + (closed ? L.T("AIL_e7b1d713", " (EN CUARENTENA)") : ""));
                var end = QuarantineEnd(st);
                if (closed && end.HasValue && end.Value >= 0)
                {
                    int left = (int)(end.Value - CampaignTime.Now.ToDays);
                    if (left > 0) lines.Add("   " + L.F("AIL_e7b1d715", $"Días restantes: {left}"));
                }
            }
            else lines.Add(L.T("AIL_e7b1d716", "No hay enfermedades activas."));
            if (closed && o == null)
            {
                lines.Add("");
                lines.Add(L.T("AIL_e7b1d717", "AVISO: no hay ninguna enfermedad activa. ¡Las penalizaciones de la cuarentena se duplican!"));
            }
            lines.Add("");
            lines.Add(L.T("AIL_e7b1d718", "Efectos de la cuarentena:"));
            lines.Add("• " + L.T("AIL_e7b1d719", "Frena el contagio"));
            lines.Add("• " + L.T("AIL_e7b1d71a", "Impide las epidemias"));
            lines.Add("• " + L.T("AIL_e7b1d71b", "Restringe el comercio y las visitas"));
            return string.Join("\n", lines);
        }

        private static void OpenQuarantine()
        {
            bool open = false;
            try { open = UI.QuarantineWindow.Open(); } catch (Exception ex) { Log.Error("[quarantine] window", ex); }
            if (!open) UI.QuarantineWindow.Ask();
        }

        // ============================================================ hospital

        // Price multiplier of the physicians by medical level (index 1..4; 0 unused): the poorest charge the most for the least.
        private static readonly float[] TierPrice = { 1.3f, 1.3f, 1f, 0.9f, 0.8f };
        private static float HospitalPrice(Settlement st) => st == null ? 1.3f : st.IsVillage ? 0.9f : TierPrice[Math.Max(1, Math.Min(4, MedicalTier(st)))];

        private static int CostSelf(Settlement st) => (int)Math.Ceiling(Math.Max(0, Hero.MainHero.MaxHitPoints - Hero.MainHero.HitPoints) * 4 * HospitalPrice(st));
        private static List<Hero> WoundedCompanions() =>
            MobileParty.MainParty.MemberRoster.GetTroopRoster().Where(e => e.Character.IsHero && e.Character.HeroObject != Hero.MainHero && e.Character.HeroObject.HitPoints < e.Character.HeroObject.MaxHitPoints * 0.95f)
                .Select(e => e.Character.HeroObject).ToList();
        private static int CostCompanions(Settlement st) => (int)Math.Ceiling(WoundedCompanions().Sum(h => (h.MaxHitPoints - h.HitPoints) * 4) * HospitalPrice(st));
        private static int WoundedCount(TroopRoster r) => r.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => e.WoundedNumber);
        private static int CostRoster(TroopRoster r, Settlement st) =>
            (int)Math.Ceiling(r.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => e.WoundedNumber * (10 + e.Character.Tier * 8)) * HospitalPrice(st));
        private static int CostPrevention(Settlement st) => (int)Math.Ceiling((50 + MobileParty.MainParty.MemberRoster.TotalManCount * 6) * HospitalPrice(st) * S.Get.DiseasePreventionHerbCostMultiplier * (1f - HerbDiscount(st)));
        private const int PreventionDays = 12;

        private static void HealRoster(TroopRoster r)
        {
            foreach (var e in r.GetTroopRoster().Where(e => !e.Character.IsHero && e.WoundedNumber > 0).ToList())
                r.AddToCounts(e.Character, 0, false, -e.WoundedNumber);
        }

        private static bool Pay(int cost)
        {
            if (cost <= 0) return true;
            if (Hero.MainHero.Gold < cost) { WorldSim.Say(L.F("AIL_af35e867", $"No tienes los {cost} denarios que pide el hospital."), Colors.Red); return false; }
            var st = Settlement.CurrentSettlement;
            if (st != null) TaleWorlds.CampaignSystem.Actions.GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, st, cost);
            else Hero.MainHero.ChangeHeroGold(-cost);
            return true;
        }

        /// <summary>Text of the fallback hospital menu, shown only if the hospital window cannot open.</summary>
        private static string HospitalText()
        {
            var st = Settlement.CurrentSettlement;
            var lines = new List<string> { HospitalTitle(st), "" };
            try { lines.AddRange(HospitalFactsLeft(st)); } catch (Exception ex) { Log.Error("[disease] hospital text (place)", ex); }
            try { lines.AddRange(HospitalFactsRight(st)); } catch (Exception ex) { Log.Error("[disease] hospital text (party)", ex); }
            return string.Join("\n", lines).Replace("{", "(").Replace("}", ")");
        }

        private static string ParentMenu(Settlement st) => st == null || st.IsTown ? "town" : st.IsCastle ? "castle" : st.IsVillage ? "village" : "town";

        /// <summary>The hospital window; if it cannot open, the plain menu with the same facts.</summary>
        private static void OpenHospital()
        {
            bool open = false;
            try { open = UI.HospitalWindow.Open(); } catch (Exception ex) { Log.Error("[disease] hospital window", ex); }
            if (open) return;
            MBTextManager.SetTextVariable("AIL_HOSPITAL", HospitalText());
            GameMenu.SwitchToMenu("ailegacy_hospital");
        }

        private static void AddHospital(CampaignGameStarter starter)
        {
            // One way in per kind of place: the town hospital, the castle's physician, the village healer
            var doors = new[]
            {
                ("town", "ailegacy_hospital_enter", L.T("AIL_5d5538b0", "{=!}Visitar el hospital")),
                ("castle", "ailegacy_hospital_enter_castle", L.T("AIL_e7b1c946", "{=!}Visitar al médico del castillo")),
                ("village", "ailegacy_hospital_enter_village", L.T("AIL_e7b1c947", "{=!}Visitar al curandero")),
            };
            foreach (var (menu, id, text) in doors)
            {
                string kind = menu;
                starter.AddGameMenuOption(menu, id, text,
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Submenu;
                        var st = Settlement.CurrentSettlement;
                        if (!HospitalHere(st)) return false;
                        return kind == "town" ? st.IsTown : kind == "castle" ? st.IsCastle : st.IsVillage;
                    },
                    args => MainThread.Post(OpenHospital), false, 1);
            }

            // The old hospital menu stays: it is the fallback when the window cannot open, and a save made inside it still finds it
            starter.AddGameMenu("ailegacy_hospital", "{=!}{AIL_HOSPITAL}", args => MBTextManager.SetTextVariable("AIL_HOSPITAL", HospitalText()));

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_window", L.T("AIL_e7b1c948", "{=!}Abrir el hospital"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Manage; return HospitalHere(Settlement.CurrentSettlement); },
                args => MainThread.Post(() =>
                {
                    bool open = false;
                    try { open = UI.HospitalWindow.Open(); } catch (Exception ex) { Log.Error("[disease] hospital window", ex); }
                    if (!open) WorldSim.Say(L.T("AIL_e7b1c949", "No se pudo abrir la ventana del hospital. El motivo está en el registro del mod."), Colors.Red);
                }), false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_back", L.T("AIL_0042ce81", "{=!}Volver"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args => GameMenu.SwitchToMenu(ParentMenu(Settlement.CurrentSettlement)), true, -1);
        }

        /// <summary>The hospital, the quarantine gate, and the quarantine the player manages from the menu of a town or castle.</summary>
        public static void AddMenus(CampaignGameStarter starter)
        {
            try { AddHospital(starter); } catch (Exception ex) { Log.Error("Hospital menu", ex); }
            try { QuarantineRules.AddMenus(starter); } catch (Exception ex) { Log.Error("Quarantine gate menu", ex); }
            // once per campaign start: what each Medicine perk does against disease, in its description (DiseasesPerks.cs)
            ApplyPerkTexts();
            foreach (var menu in new[] { "town", "castle" })
            {
                starter.AddGameMenuOption(menu, "ailegacy_manage_quarantine_" + menu, L.T("AIL_e7b1d70f", "{=!}Gestionar la cuarentena"),
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                        return CanManageQuarantine(Settlement.CurrentSettlement);
                    },
                    args => MainThread.Post(OpenQuarantine), false, 6);
            }
        }

        // ============================================================ laws (they live here because they're the same kind of daily effect)

        public static void ApplyLaws()
        {
            WorldSim.State.Laws.RemoveAll(law => law == null || WorldSim.FindKingdom(law.KingdomId) == null);
            Laws.Invalidate();
            // Law effects normally go through the game's daily calculation (WorldSim.TownModifiers, with a line in the
            // breakdown). We only apply the four town stats by hand here if that calculation stopped going through our hook.
            if (!Laws.Enabled || Laws.Idle || WorldSim.LawsViaModel()) return;
            foreach (var k in WorldSim.Kingdoms.ToList())
                foreach (var town in k.Fiefs)
                {
                    var st = town.Settlement;
                    town.Loyalty = Math.Max(0f, Math.Min(100f, town.Loyalty + Laws.SumForSettlement(st, Laws.Loyalty)));
                    town.Prosperity = Math.Max(0f, town.Prosperity + Laws.SumForSettlement(st, Laws.Prosperity));
                    town.Security = Math.Max(0f, Math.Min(100f, town.Security + Laws.SumForSettlement(st, Laws.Security)));
                    town.FoodStocks = Math.Max(0f, town.FoodStocks + Laws.SumForSettlement(st, Laws.Food));
                }
        }

        public static string DescribeLaws(Kingdom k) => Laws.DescribeLaws(k);
    }
}
