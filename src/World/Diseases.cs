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
    /// <summary>Un brote de enfermedad en un asentamiento.</summary>
    public class Outbreak
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;        // idioma del jugador
        [JsonProperty("name_en")] public string NameEn;   // para los prompts
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("severity")] public int Severity = 2;   // 1..5
        [JsonProperty("start")] public double StartDay;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("quarantine_until")] public double QuarantineUntilDay = -1;

        [JsonIgnore] public bool Quarantined => QuarantineUntilDay > CampaignTime.Now.ToDays;
    }

    /// <summary>Ley proclamada por un rey: un efecto pequeño y diario sobre todas las ciudades y castillos de su reino.</summary>
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
    }

    /// <summary>
    /// Enfermedades: brotes estacionales o provocados por un evento, que dañan al asentamiento, se contagian a los vecinos,
    /// hieren a tus tropas si entras, y se frenan con cuarentenas (que deciden los reyes, o tú en tus feudos).
    /// </summary>
    internal static class Diseases
    {
        private static readonly Random Rng = new Random();
        private static List<Outbreak> All => WorldSim.State.Outbreaks;
        public static bool Enabled => S.Get.EnableDiseases;

        public static Outbreak At(Settlement st) => st == null ? null : All.FirstOrDefault(o => o.SettlementId == st.StringId);
        private static string SeverityWord(int s) => s <= 1 ? "mild" : s == 2 ? "troubling" : s == 3 ? "serious" : s == 4 ? "deadly" : "a plague";
        private static string SeverityEs(int s) => s <= 1 ? "leve" : s == 2 ? "preocupante" : s == 3 ? "grave" : s == 4 ? L.T("AIL_44011913", "mortífera") : L.T("AIL_e45c3132", "una peste");

        /// <summary>Para el prompt de quien vive o está en ese sitio.</summary>
        public static string DescribeAt(Settlement st)
        {
            var o = At(st);
            if (o == null) return "";
            return $"A sickness is in {st.Name}: {o.NameEn} ({SeverityWord(o.Severity)}), for {(int)(CampaignTime.Now.ToDays - o.StartDay)} day(s) now." +
                   (o.Quarantined ? " The place is under QUARANTINE by order of its rulers: nobody is supposed to come or go." : " People are afraid.");
        }

        /// <summary>Para los datos técnicos de un reino.</summary>
        public static string DescribeFor(Kingdom k)
        {
            var list = All.Select(o => (o, st: Settlement.Find(o.SettlementId))).Where(x => x.st != null && x.st.MapFaction == k).ToList();
            return list.Count == 0 ? "" : "sickness in: " + string.Join(", ", list.Select(x => $"{x.st.Name} ({x.o.NameEn}, {SeverityWord(x.o.Severity)}{(x.o.Quarantined ? ", quarantined" : "")})"));
        }

        // ============================================================ brotes

        public static Outbreak Start(Settlement st, string name, string nameEn, int severity, int days, bool announce = true)
        {
            var cfg = S.Get;
            if (!Enabled || st == null || st.IsHideout || At(st) != null) return null;
            if (All.Count >= cfg.DiseaseMaxSimultaneous) return null;
            severity = Math.Max(1, Math.Min(cfg.DiseaseMaxSeverity, severity));
            days = Math.Max(7, Math.Min(120, days <= 0 ? 20 + severity * 12 : days));
            var o = new Outbreak
            {
                Id = WorldSim.NewId("d"), SettlementId = st.StringId, Severity = severity, StartDay = CampaignTime.Now.ToDays, UntilDay = CampaignTime.Now.ToDays + days,
                Name = string.IsNullOrWhiteSpace(name) ? "unas fiebres" : WorldSim.Clean(name, 50), NameEn = string.IsNullOrWhiteSpace(nameEn) ? "a fever" : WorldSim.Clean(nameEn, 50),
            };
            All.Add(o);
            WorldSim.State.LastOutbreakDay = o.StartDay;
            Log.Info($"[enfermedad] brote en {st.Name}: {o.NameEn}, gravedad {severity}, {days} días");
            if (announce)
                NewsStore.Add(new NewsItem
                {
                    Type = "event_disease", Importance = severity >= 4 ? 3 : 2, Text = $"A sickness has broken out in {st.Name}: {o.NameEn} ({SeverityWord(severity)}).",
                    TitleEs = L.F("AIL_4b3f2067", $"Enfermedad en {st.Name}"), TextEs = L.F("AIL_053cbde7", $"Se ha declarado {o.Name} en {st.Name}. Dicen que es {SeverityEs(severity)}."),
                    SettlementId = st.StringId, Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(),
                });
            return o;
        }

        /// <summary>El generador de eventos puede traer un brote: { settlement, name, name_en, severity, duration_days }.</summary>
        public static void FromEvent(JObject d)
        {
            if (d == null || !Enabled || !S.Get.DiseaseFromEvents) return;
            var st = OrderSystem.FindSettlement((string)d["settlement"]);
            if (st == null) return;
            Start(st, (string)d["name"], (string)d["name_en"], (int?)d["severity"] ?? 2, (int?)d["duration_days"] ?? 0, announce: false);
        }

        private static (string es, string en) SeasonalName()
        {
            switch ((int)CampaignTime.Now.GetSeasonOfYear)
            {
                case 0: return (L.T("AIL_fa46e0da", "fiebre de las manchas"), "spotted fever");        // primavera
                case 1: return ("flujo de sangre", "the bloody flux");            // verano
                case 2: return (L.T("AIL_d8412138", "tercianas de los pantanos"), "marsh ague");       // otoño
                default: return (L.T("AIL_b86021cd", "fiebre de pulmón"), "lung fever");               // invierno
            }
        }

        public static void OnDailyTick()
        {
            if (Campaign.Current == null) return;
            try
            {
                var cfg = S.Get;
                double now = CampaignTime.Now.ToDays;
                if (!Enabled) { if (All.Count > 0) All.Clear(); return; }

                foreach (var o in All.ToList())
                {
                    var st = Settlement.Find(o.SettlementId);
                    if (st == null) { All.Remove(o); continue; }
                    if (now >= o.UntilDay)
                    {
                        All.Remove(o);
                        Log.Info($"[enfermedad] fin del brote en {st.Name}");
                        NewsStore.Add(new NewsItem
                        {
                            Type = "event_disease", Importance = 1, Text = $"The sickness in {st.Name} has passed.", TitleEs = L.F("AIL_387fb206", $"{st.Name} se libra de la enfermedad"),
                            TextEs = L.F("AIL_f4f38e0c", $"Ya no hay enfermos de {o.Name} en {st.Name}."), SettlementId = st.StringId,
                            Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(),
                        });
                        continue;
                    }

                    // Daño al asentamiento
                    float s = o.Severity;
                    if (st.Town != null)
                    {
                        st.Town.Prosperity = Math.Max(0f, st.Town.Prosperity - s * cfg.DiseaseProsperityLossPerSeverity - (o.Quarantined ? 3f : 0f));
                        st.Town.FoodStocks = Math.Max(0f, st.Town.FoodStocks - s * 0.8f);
                        st.Town.Loyalty = Math.Max(0f, st.Town.Loyalty - s * 0.15f - (o.Quarantined ? 0.2f : 0f));
                        st.Town.Security = Math.Max(0f, st.Town.Security - s * 0.1f + (o.Quarantined ? 0.2f : 0f));
                    }
                    else if (st.Village != null)
                        st.Village.Hearth = Math.Max(10f, st.Village.Hearth - s * cfg.DiseaseProsperityLossPerSeverity * 0.4f);

                    // Contagio a un vecino (la cuarentena lo corta)
                    if (!o.Quarantined && All.Count < cfg.DiseaseMaxSimultaneous && Rng.Next(1000) < cfg.DiseaseSpreadChancePerMille * s)
                    {
                        var here = st.GetPosition2D;
                        var next = Settlement.All.Where(x => x != st && !x.IsHideout && (x.IsTown || x.IsCastle || x.IsVillage) && At(x) == null)
                            .OrderBy(x => x.GetPosition2D.Distance(here)).Take(4).OrderBy(_ => Rng.Next()).FirstOrDefault();
                        if (next != null)
                        {
                            var n = Start(next, o.Name, o.NameEn, Math.Max(1, o.Severity - (Rng.Next(2) == 0 ? 1 : 0)), (int)Math.Max(10, (o.UntilDay - o.StartDay) * 0.8));
                            if (n != null) Log.Info($"[enfermedad] {o.NameEn} salta de {st.Name} a {next.Name}");
                        }
                    }
                }

                // Tus tropas, si estás dentro de un sitio con enfermos
                var mine = MobileParty.MainParty?.CurrentSettlement;
                var ob = At(mine);
                if (ob != null) SickenPlayerTroops(ob, mine, 0.5f);

                // Brotes de temporada
                if (cfg.DiseaseSeasonalChancePerMille > 0 && All.Count < cfg.DiseaseMaxSimultaneous
                    && (WorldSim.State.LastOutbreakDay < 0 || now - WorldSim.State.LastOutbreakDay >= cfg.DiseaseMinDaysBetweenOutbreaks)
                    && Rng.Next(1000) < cfg.DiseaseSeasonalChancePerMille)
                {
                    var towns = Settlement.All.Where(x => x.IsTown && At(x) == null).ToList();
                    if (towns.Count > 0)
                    {
                        var (es, en) = SeasonalName();
                        Start(towns[Rng.Next(towns.Count)], es, en, 1 + Rng.Next(Math.Min(3, cfg.DiseaseMaxSeverity)), 0);
                    }
                }
            }
            catch (Exception ex) { Log.Error("[enfermedad] tick diario", ex); }
        }

        public static void OnSettlementEntered(MobileParty party, Settlement st, Hero hero)
        {
            try
            {
                if (!Enabled || party == null || !party.IsMainParty) return;
                var o = At(st);
                if (o == null) return;
                if (QuarantineRules.StopsPlayer(st)) return;      // te paran en la puerta: aún no has entrado
                InformationManager.DisplayMessage(new InformationMessage(
                    L.F("AIL_e13e09e9", $"En {st.Name} hay {o.Name}") + (o.Quarantined ? L.T("AIL_5eccf2c1", ". Está en cuarentena: entras por tu cuenta y riesgo.") : "."), Colors.Red));
                SickenPlayerTroops(o, st, o.Quarantined ? 1.3f : 1f);
            }
            catch (Exception ex) { Log.Error("[enfermedad] entrada", ex); }
        }

        /// <summary>Has entrado sobornando a la guardia en un sitio en cuarentena.</summary>
        public static void SickenAtGate(Settlement st)
        {
            try { var o = At(st); if (Enabled && o != null) SickenPlayerTroops(o, st, 1.3f); }
            catch (Exception ex) { Log.Error("[enfermedad] entrada con soborno", ex); }
        }

        /// <summary>Parte de tus soldados cae enferma (quedan como heridos y se recuperan con el tiempo).</summary>
        private static void SickenPlayerTroops(Outbreak o, Settlement st, float factor)
        {
            var cfg = S.Get;
            if (WorldSim.State.PreventionUntilDay > CampaignTime.Now.ToDays) factor *= 0.25f;      // prevención pagada en un hospital
            if (!cfg.DiseaseAffectsPlayerTroops || Rng.Next(100) >= o.Severity * cfg.DiseaseTroopChancePerSeverity * factor) return;
            var roster = MobileParty.MainParty.MemberRoster;
            int healthy = roster.TotalHealthyCount;
            int toll = Math.Max(1, (int)(healthy * 0.02f * o.Severity * factor));
            int done = 0;
            foreach (var e in roster.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero && x.Number - x.WoundedNumber > 0).OrderBy(_ => Rng.Next()).ToList())
            {
                if (done >= toll) break;
                int n = Math.Min(e.Number - e.WoundedNumber, toll - done);
                roster.WoundTroop(e.Character, n);
                done += n;
            }
            if (done > 0)
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_7074bda0", $"{done} de tus soldados caen enfermos de {o.Name} en {st.Name}."), Colors.Red));
        }

        // ============================================================ cuarentena

        public static string Quarantine(Settlement st, int days, string byWhom)
        {
            var o = At(st);
            if (o == null) return null;
            days = Math.Max(5, Math.Min(60, days <= 0 ? 15 : days));
            o.QuarantineUntilDay = CampaignTime.Now.ToDays + days;
            try { if (Settlement.CurrentSettlement == st) QuarantineRules.NotePlayerInside(); } catch { }      // si estás dentro, no te echan
            return L.F("AIL_34a22788", $"{byWhom} decreta {days} días de cuarentena en {st.Name}.");
        }

        public static string Lift(Settlement st, string byWhom)
        {
            var o = At(st);
            if (o == null || !o.Quarantined) return null;
            o.QuarantineUntilDay = -1;
            return L.F("AIL_fa2d5313", $"{byWhom} levanta la cuarentena de {st.Name}.");
        }

        // ============================================================ hospital

        private static float HospitalPrice(Settlement st) => st?.Town == null ? 1.3f : st.Town.Prosperity > 5500f ? 0.8f : st.Town.Prosperity >= 2500f ? 1f : 1.3f;
        private static string HospitalLevel(Settlement st) => st?.Town == null ? L.T("AIL_0b2f92c7", "Básico") : st.Town.Prosperity > 5500f ? "Avanzado" : st.Town.Prosperity >= 2500f ? "Bueno" : L.T("AIL_0b2f92c7", "Básico");

        private static int CostSelf(Settlement st) => (int)Math.Ceiling(Math.Max(0, Hero.MainHero.MaxHitPoints - Hero.MainHero.HitPoints) * 4 * HospitalPrice(st));
        private static List<Hero> WoundedCompanions() =>
            MobileParty.MainParty.MemberRoster.GetTroopRoster().Where(e => e.Character.IsHero && e.Character.HeroObject != Hero.MainHero && e.Character.HeroObject.HitPoints < e.Character.HeroObject.MaxHitPoints * 0.95f)
                .Select(e => e.Character.HeroObject).ToList();
        private static int CostCompanions(Settlement st) => (int)Math.Ceiling(WoundedCompanions().Sum(h => (h.MaxHitPoints - h.HitPoints) * 4) * HospitalPrice(st));
        private static int WoundedCount(TroopRoster r) => r.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => e.WoundedNumber);
        private static int CostRoster(TroopRoster r, Settlement st) =>
            (int)Math.Ceiling(r.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => e.WoundedNumber * (10 + e.Character.Tier * 8)) * HospitalPrice(st));
        private static int CostPrevention(Settlement st) => (int)Math.Ceiling((50 + MobileParty.MainParty.MemberRoster.TotalManCount * 6) * HospitalPrice(st));
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

        private static string HospitalText()
        {
            var st = Settlement.CurrentSettlement;
            var sb = new System.Text.StringBuilder();
            sb.Append(L.F("AIL_3f44d794", $"Hospital de {st?.Name}\n\nNivel de sus médicos: {HospitalLevel(st)}."));
            var season = CampaignTime.Now.GetSeasonOfYear;
            sb.Append(L.F("AIL_f054b3ad", $"\nÉpoca del año: {(season == CampaignTime.Seasons.Winter ? L.T("AIL_e37fa365", "invierno (riesgo alto de enfermedad)") : season == CampaignTime.Seasons.Autumn ? L.T("AIL_8fdf1238", "otoño (riesgo medio)") : season == CampaignTime.Seasons.Spring ? L.T("AIL_62c99c8c", "primavera (riesgo medio)") : L.T("AIL_7319097c", "verano (riesgo bajo)"))}."));
            var o = At(st);
            sb.Append(o != null ? L.F("AIL_c327ea4f", $"\n\nEn la ciudad hay {o.Name} ({SeverityEs(o.Severity)}){(o.Quarantined ? L.T("AIL_413e791f", ", y está en cuarentena") : "")}.") : L.T("AIL_2691821b", "\n\nNo hay ninguna enfermedad en la ciudad."));
            var me = Hero.MainHero;
            sb.Append(me.HitPoints >= me.MaxHitPoints * 0.95f ? L.T("AIL_226c9a7f", "\n\nEstás sano.") : L.F("AIL_8f3749ae", $"\n\nEstás herido ({me.HitPoints} de {me.MaxHitPoints})."));
            int w = WoundedCount(MobileParty.MainParty.MemberRoster);
            if (w > 0) sb.Append(L.F("AIL_6d44c3f5", $"\nTienes {w} soldado(s) heridos o enfermos."));
            if (WorldSim.State.PreventionUntilDay > CampaignTime.Now.ToDays) sb.Append(L.F("AIL_e9459925", $"\nTus hombres están prevenidos {(int)Math.Ceiling(WorldSim.State.PreventionUntilDay - CampaignTime.Now.ToDays)} día(s) más."));
            return sb.ToString();
        }

        private static void Refresh() { MBTextManager.SetTextVariable("AIL_HOSPITAL", HospitalText()); GameMenu.SwitchToMenu("ailegacy_hospital"); }

        private static void AddHospital(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town", "ailegacy_hospital_enter", L.T("AIL_5d5538b0", "{=!}Visitar el hospital"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Submenu; return S.Get.EnableHospital && Settlement.CurrentSettlement != null && Settlement.CurrentSettlement.IsTown; },
                args => { MBTextManager.SetTextVariable("AIL_HOSPITAL", HospitalText()); GameMenu.SwitchToMenu("ailegacy_hospital"); }, false, 1);

            starter.AddGameMenu("ailegacy_hospital", "{=!}{AIL_HOSPITAL}", args => MBTextManager.SetTextVariable("AIL_HOSPITAL", HospitalText()));

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_info", L.T("AIL_6ffaa6ff", "{=!}Preguntar por las enfermedades que corren"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Conversation; return true; },
                args =>
                {
                    var here = Settlement.CurrentSettlement;
                    var list = All.Select(o => (o, st: Settlement.Find(o.SettlementId))).Where(x => x.st != null)
                                  .OrderBy(x => here != null ? x.st.GetPosition2D.Distance(here.GetPosition2D) : 0f).Take(12).ToList();
                    string text = list.Count == 0 ? L.T("AIL_0b9bdfcf", "Los médicos no tienen noticia de ninguna enfermedad en Calradia ahora mismo.")
                        : string.Join("\n", list.Select(x => L.F("AIL_a63c51e2", $"• {x.st.Name}: {x.o.Name}, {SeverityEs(x.o.Severity)}; le quedan unos {(int)Math.Max(1, x.o.UntilDay - CampaignTime.Now.ToDays)} días") + (x.o.Quarantined ? L.T("AIL_c1183e43", " (en cuarentena)") : "")))
                          + L.T("AIL_63b5c8dc", "\n\nEntrar en un sitio con enfermos puede hacer caer a parte de tus hombres. La cuarentena corta el contagio a los vecinos, pero hunde el comercio.");
                    InformationManager.ShowInquiry(new InquiryData(L.T("AIL_965719f1", "Lo que saben los médicos"), text, true, false, L.T("AIL_265409ff", "Entendido"), "", null, null), true);
                }, false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_self", L.T("AIL_85ee2964", "{=!}Curarte ({AIL_H_SELF}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                    int c = CostSelf(Settlement.CurrentSettlement);
                    MBTextManager.SetTextVariable("AIL_H_SELF", c);
                    if (c <= 0) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_0bc1afa9", "{=!}Estás sano.")); }
                    return true;
                },
                args => { if (Pay(CostSelf(Settlement.CurrentSettlement))) { Hero.MainHero.HitPoints = Hero.MainHero.MaxHitPoints; WorldSim.Say(L.T("AIL_24d3d112", "Los médicos te dejan como nuevo."), Colors.Green); } Refresh(); }, false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_comp", L.T("AIL_0a44192c", "{=!}Curar a tus compañeros ({AIL_H_COMP}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                    int c = CostCompanions(Settlement.CurrentSettlement);
                    MBTextManager.SetTextVariable("AIL_H_COMP", c);
                    if (c <= 0) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_95b516ae", "{=!}No llevas compañeros heridos.")); }
                    return true;
                },
                args => { if (Pay(CostCompanions(Settlement.CurrentSettlement))) { foreach (var h in WoundedCompanions()) h.HitPoints = h.MaxHitPoints; WorldSim.Say(L.T("AIL_52fe29a9", "Tus compañeros quedan curados."), Colors.Green); } Refresh(); }, false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_troops", L.T("AIL_e94ae63e", "{=!}Curar a tus tropas ({AIL_H_TROOPS}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                    int c = CostRoster(MobileParty.MainParty.MemberRoster, Settlement.CurrentSettlement);
                    MBTextManager.SetTextVariable("AIL_H_TROOPS", c);
                    if (c <= 0) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_586fb7ef", "{=!}No tienes soldados heridos ni enfermos.")); }
                    return true;
                },
                args => { if (Pay(CostRoster(MobileParty.MainParty.MemberRoster, Settlement.CurrentSettlement))) { HealRoster(MobileParty.MainParty.MemberRoster); WorldSim.Say(L.T("AIL_be8758dd", "Tus soldados heridos y enfermos vuelven a filas."), Colors.Green); } Refresh(); }, false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_prisoners", L.T("AIL_9ef83fd5", "{=!}Curar a tus prisioneros ({AIL_H_PRIS}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                    int c = CostRoster(MobileParty.MainParty.PrisonRoster, Settlement.CurrentSettlement);
                    MBTextManager.SetTextVariable("AIL_H_PRIS", c);
                    if (c <= 0) { args.IsEnabled = false; args.Tooltip = new TextObject("{=!}No llevas prisioneros heridos."); }
                    return true;
                },
                args => { if (Pay(CostRoster(MobileParty.MainParty.PrisonRoster, Settlement.CurrentSettlement))) { HealRoster(MobileParty.MainParty.PrisonRoster); WorldSim.Say(L.T("AIL_e3e82b77", "Tus prisioneros quedan curados."), Colors.Green); } Refresh(); }, false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_prevent", L.T("AIL_3c407add", "{=!}Prevención para tu partida, ") + PreventionDays + L.T("AIL_48d043af", " días ({AIL_H_PREV}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                    MBTextManager.SetTextVariable("AIL_H_PREV", CostPrevention(Settlement.CurrentSettlement));
                    args.Tooltip = new TextObject(L.T("AIL_35404728", "{=!}Remedios, agua hervida y ropa limpia: durante esos días tus hombres enferman cuatro veces menos al entrar en sitios con enfermedad."));
                    if (WorldSim.State.PreventionUntilDay > CampaignTime.Now.ToDays + PreventionDays - 1) args.IsEnabled = false;
                    return Enabled;
                },
                args =>
                {
                    if (Pay(CostPrevention(Settlement.CurrentSettlement)))
                    {
                        WorldSim.State.PreventionUntilDay = CampaignTime.Now.ToDays + PreventionDays;
                        WorldSim.Say(L.F("AIL_e7c1ccde", $"Tu partida queda prevenida durante {PreventionDays} días."), Colors.Green);
                    }
                    Refresh();
                }, false, -1);

            starter.AddGameMenuOption("ailegacy_hospital", "ailegacy_hospital_back", L.T("AIL_0042ce81", "{=!}Volver"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args => GameMenu.SwitchToMenu("town"), true, -1);
        }

        /// <summary>En tus propios feudos decides tú, desde el menú del asentamiento.</summary>
        public static void AddMenus(CampaignGameStarter starter)
        {
            try { AddHospital(starter); } catch (Exception ex) { Log.Error("Menú del hospital", ex); }
            try { QuarantineRules.AddMenus(starter); } catch (Exception ex) { Log.Error("Menú de la puerta en cuarentena", ex); }
            foreach (var menu in new[] { "town", "castle", "village" })
            {
                starter.AddGameMenuOption(menu, "ailegacy_quarantine_" + menu, L.T("AIL_b23dc670", "{=!}Decretar cuarentena (15 días)"),
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                        var st = Settlement.CurrentSettlement; var o = At(st);
                        return Enabled && o != null && !o.Quarantined && st.OwnerClan == Clan.PlayerClan;
                    },
                    args => { var r = Quarantine(Settlement.CurrentSettlement, 15, Hero.MainHero.Name.ToString()); if (r != null) WorldSim.Say(r, Colors.Yellow); GameMenu.SwitchToMenu(menu); }, false, 2);
                starter.AddGameMenuOption(menu, "ailegacy_lift_quarantine_" + menu, L.T("AIL_4114935f", "{=!}Levantar la cuarentena"),
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Manage;
                        var st = Settlement.CurrentSettlement; var o = At(st);
                        return Enabled && o != null && o.Quarantined && st.OwnerClan == Clan.PlayerClan;
                    },
                    args => { var r = Lift(Settlement.CurrentSettlement, Hero.MainHero.Name.ToString()); if (r != null) WorldSim.Say(r, Colors.Yellow); GameMenu.SwitchToMenu(menu); }, false, 2);
            }
        }

        // ============================================================ leyes (viven aquí por ser efectos diarios del mismo tipo)

        public static void ApplyLaws()
        {
            foreach (var law in WorldSim.State.Laws.ToList())
            {
                var k = WorldSim.FindKingdom(law.KingdomId);
                if (k == null) { WorldSim.State.Laws.Remove(law); continue; }
                // Los efectos de las leyes van dentro de la cuenta diaria del juego (WorldSim.TownModifiers, con su motivo en el
                // desglose). Aquí solo se suman a mano si esa cuenta ha dejado de pasar por nuestro enganche.
                if (WorldSim.LawsViaModel()) continue;
                foreach (var town in k.Fiefs)
                {
                    town.Loyalty = Math.Max(0f, Math.Min(100f, town.Loyalty + law.LoyaltyPerDay));
                    town.Prosperity = Math.Max(0f, town.Prosperity + Math.Max(-1.5f, Math.Min(1.5f, law.ProsperityPerDay)));   // tope también para leyes de partidas antiguas
                    town.Security = Math.Max(0f, Math.Min(100f, town.Security + law.SecurityPerDay));
                    town.FoodStocks = Math.Max(0f, town.FoodStocks + law.FoodPerDay);
                }
            }
        }

        public static string DescribeLaws(Kingdom k)
        {
            var laws = WorldSim.State.Laws.Where(l => l.KingdomId == k.StringId).ToList();
            return laws.Count == 0 ? "" : "laws in force: " + string.Join("; ", laws.Select(l => l.Name));
        }
    }
}
