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

        [JsonIgnore] public bool Quarantined => QuarantineUntilDay > CampaignTime.Now.ToDays;
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
    /// Seasonal or event-driven outbreaks. They hurt the settlement, spread to neighbors and wound your troops if you go in.
    /// Quarantines slow them down (kings decide, or you in your own fiefs).
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

        public static Outbreak Start(Settlement st, string name, string nameEn, int severity, int days, bool announce = true, DiseaseFx fx = null)
        {
            var cfg = S.Get;
            if (!Enabled || st == null || st.IsHideout || At(st) != null) return null;
            if (All.Count >= cfg.DiseaseMaxSimultaneous) return null;
            severity = Math.Max(1, Math.Min(cfg.DiseaseMaxSeverity, severity));
            days = Math.Max(7, Math.Min(120, days <= 0 ? 20 + severity * 12 : days));
            var o = new Outbreak
            {
                Id = WorldSim.NewId("d"), SettlementId = st.StringId, Severity = severity, StartDay = CampaignTime.Now.ToDays, UntilDay = CampaignTime.Now.ToDays + days,
                Name = string.IsNullOrWhiteSpace(name) ? L.T("AIL_e7b1c94c", "unas fiebres") : WorldSim.Clean(name, 50), NameEn = string.IsNullOrWhiteSpace(nameEn) ? "a fever" : WorldSim.Clean(nameEn, 50), Fx = fx,
            };
            All.Add(o);
            WorldSim.State.LastOutbreakDay = o.StartDay;
            Log.Info($"[disease] outbreak in {st.Name}: {o.NameEn}, severity {severity}, {days} days");
            if (announce)
                NewsStore.Add(new NewsItem
                {
                    Type = "event_disease", Importance = severity >= 4 ? 3 : 2, Text = $"A sickness has broken out in {st.Name}: {o.NameEn} ({SeverityWord(severity)}).",
                    TitleEs = L.F("AIL_4b3f2067", $"Enfermedad en {st.Name}"), TextEs = L.F("AIL_053cbde7", $"Se ha declarado {o.Name} en {st.Name}. Dicen que es {SeverityEs(severity)}."),
                    SettlementId = st.StringId, Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(),
                });
            return o;
        }

        /// <summary>Outbreak from the event generator: { settlement, name, name_en, severity, duration_days }.</summary>
        public static void FromEvent(JObject d)
        {
            if (d == null || !Enabled || !S.Get.DiseaseFromEvents) return;
            var st = OrderSystem.FindSettlement((string)d["settlement"]);
            if (st == null) return;
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
            Start(st, (string)d["name"], (string)d["name_en"], (int?)d["severity"] ?? 2, (int?)d["duration_days"] ?? 0, announce: false, fx: fx);
        }

        private static (string es, string en) SeasonalName()
        {
            switch ((int)CampaignTime.Now.GetSeasonOfYear)
            {
                case 0: return (L.T("AIL_fa46e0da", "fiebre de las manchas"), "spotted fever");        // spring
                case 1: return (L.T("AIL_e7b1c60f", "flujo de sangre"), "the bloody flux");            // summer
                case 2: return (L.T("AIL_d8412138", "tercianas de los pantanos"), "marsh ague");       // autumn
                default: return (L.T("AIL_b86021cd", "fiebre de pulmón"), "lung fever");               // winter
            }
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
                        st.Town.Prosperity = Math.Max(0f, st.Town.Prosperity - s * cfg.DiseaseProsperityLossPerSeverity - (o.Quarantined ? 3f : 0f));
                        st.Town.FoodStocks = Math.Max(0f, st.Town.FoodStocks - s * 0.8f);
                        st.Town.Loyalty = Math.Max(0f, st.Town.Loyalty - s * 0.15f - (o.Quarantined ? 0.2f : 0f));
                        st.Town.Security = Math.Max(0f, st.Town.Security - s * 0.1f + (o.Quarantined ? 0.2f : 0f));
                    }
                    else if (st.Village != null)
                        st.Village.Hearth = Math.Max(10f, st.Village.Hearth - s * cfg.DiseaseProsperityLossPerSeverity * 0.4f);

                    if (!o.Quarantined && All.Count < cfg.DiseaseMaxSimultaneous && Rng.Next(1000) < cfg.DiseaseSpreadChancePerMille * s * (FxOf(o.Severity, false, o.Fx).Spread / 0.4f))
                    {
                        var here = st.GetPosition2D;
                        var next = Settlement.All.Where(x => x != st && !x.IsHideout && (x.IsTown || x.IsCastle || x.IsVillage) && At(x) == null)
                            .OrderBy(x => x.GetPosition2D.Distance(here)).Take(4).OrderBy(_ => Rng.Next()).FirstOrDefault();
                        if (next != null)
                        {
                            var n = Start(next, o.Name, o.NameEn, Math.Max(1, o.Severity - (Rng.Next(2) == 0 ? 1 : 0)), (int)Math.Max(10, (o.UntilDay - now) * cfg.DiseaseSpreadInheritFactor), true, o.Fx);
                            if (n != null) Log.Info($"[disease] {o.NameEn} spreads from {st.Name} to {next.Name}");
                        }
                    }
                }

                // Player's troops, if inside an infected settlement
                var mine = MobileParty.MainParty?.CurrentSettlement;
                var ob = At(mine);
                if (ob != null) SickenPlayerTroops(ob, mine, 0.5f * (ob.Quarantined ? cfg.QuarantineInfectionReduction * 2f : 1f));

                TickSick(now);
                try { ForceDaily(now); } catch (Exception ex) { Log.Error("[disease] garrisons and militias", ex); }

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
            catch (Exception ex) { Log.Error("[disease] daily tick", ex); }
        }

        public static void OnSettlementEntered(MobileParty party, Settlement st, Hero hero)
        {
            try
            {
                if (!Enabled || party == null) return;
                if (!party.IsMainParty || !QuarantineRules.StopsPlayer(st)) OnAnyPartyEntered(party, st);
                if (!party.IsMainParty) return;
                var o = At(st);
                if (o == null) return;
                if (QuarantineRules.StopsPlayer(st)) return;      // stopped at the gate, not inside yet
                InformationManager.DisplayMessage(new InformationMessage(
                    L.F("AIL_e13e09e9", $"En {st.Name} hay {o.Name}") + (o.Quarantined ? L.T("AIL_5eccf2c1", ". Está en cuarentena: entras por tu cuenta y riesgo.") : "."), Colors.Red));
                SickenPlayerTroops(o, st, o.Quarantined ? 1.3f : 1f);
            }
            catch (Exception ex) { Log.Error("[disease] entry", ex); }
        }

        /// <summary>The player bribed the guards to get into a quarantined settlement.</summary>
        public static void SickenAtGate(Settlement st)
        {
            try { var o = At(st); if (Enabled && o != null) SickenPlayerTroops(o, st, 1.3f); }
            catch (Exception ex) { Log.Error("[disease] entry with bribe", ex); }
        }

        /// <summary>Some of the player's soldiers fall sick. They count as wounded and recover over time.</summary>
        private static void SickenPlayerTroops(Outbreak o, Settlement st, float factor)
        {
            var cfg = S.Get;
            factor *= PlayerGuard;      // prevention paid for at a hospital, herbs taken on the road
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
            {
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_7074bda0", $"{done} de tus soldados caen enfermos de {o.Name} en {st.Name}."), Colors.Red));
                // from here on they are a sick part of the party: it can pass to the rest, weigh on the march and be treated
                RegisterSickTroops(o, done);
            }
        }

        // ============================================================ quarantine

        public static string Quarantine(Settlement st, int days, string byWhom)
        {
            var o = At(st);
            if (o == null) return null;
            days = Math.Max(5, Math.Min(60, days <= 0 ? 15 : days));
            o.QuarantineUntilDay = CampaignTime.Now.ToDays + days;
            try { if (Settlement.CurrentSettlement == st) QuarantineRules.NotePlayerInside(); } catch { }      // if the player is already inside, they don't get thrown out
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
        private static int CostPrevention(Settlement st) => (int)Math.Ceiling((50 + MobileParty.MainParty.MemberRoster.TotalManCount * 6) * HospitalPrice(st) * S.Get.DiseasePreventionHerbCostMultiplier);
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

        /// <summary>In the player's own fiefs, the player decides from the settlement menu.</summary>
        public static void AddMenus(CampaignGameStarter starter)
        {
            try { AddHospital(starter); } catch (Exception ex) { Log.Error("Hospital menu", ex); }
            try { QuarantineRules.AddMenus(starter); } catch (Exception ex) { Log.Error("Quarantine gate menu", ex); }
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
