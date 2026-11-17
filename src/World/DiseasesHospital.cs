using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>A way of treatment offered by the physicians of a place: their own care, or their care with a herb quality.</summary>
    internal sealed class HospitalMethod
    {
        public int Herb = -1;       // index into the herb qualities; -1 = the local care alone
        public float Quality;       // strength of the treatment course it starts
        public string Name, Detail;
    }

    /// <summary>One block of the hospital window: you, your companions, your troops or your prisoners.</summary>
    internal sealed class HospitalSection
    {
        public const int You = 0, Companions = 1, Troops = 2, Prisoners = 3;
        public int Kind;
        public string Title, Info;
        public int WoundCost;       // price of healing the wounds of this block; 0 = nobody wounded
        public readonly List<KeyValuePair<Sickness, string>> Cases = new List<KeyValuePair<Sickness, string>>();
    }

    /// <summary>Something to take against contagion, bought and taken on the spot.</summary>
    internal sealed class HospitalPrevention
    {
        public int Herb = -1;       // -1 = the hospital's own remedies
        public string Text, Button;
        public int Price;
        public bool Allowed;
    }

    /// <summary>
    /// What the hospital window (UI\HospitalWindow.cs) shows and does: the facts of the place, who is sick in the
    /// player's party and how it goes for each, the ways of treatment with their price and daily chance, wound care
    /// and prevention. Nothing here is carried: everything is paid for and used in the hospital.
    /// </summary>
    internal static partial class Diseases
    {
        // Strength of the local care by medical level (index 1..4; 0 unused)
        private static readonly float[] TierCare = { 1f, 1.1f, 1.4f, 1.7f, 2f };

        private static string Num(float v) => v.ToString("0.#", CultureInfo.CurrentCulture);

        /// <summary>The places that have physicians the player can visit.</summary>
        internal static bool HospitalHere(Settlement st) => st != null && S.Get.EnableHospital && (st.IsTown || st.IsCastle || st.IsVillage);

        internal static string HospitalTitle(Settlement st) =>
            st == null ? "" : st.IsVillage ? L.F("AIL_e7b1c900", $"Curandero de {st.Name}") : L.F("AIL_e7b1c901", $"Hospital de {st.Name}");

        private static string LevelName(Settlement st)
        {
            if (st != null && st.IsVillage) return L.T("AIL_e7b1c902", "curandero de aldea");
            switch (MedicalTier(st))
            {
                case 4: return L.T("AIL_e7b1c903", "excelente");
                case 3: return L.T("AIL_e7b1c904", "avanzado");
                case 2: return L.T("AIL_e7b1c905", "bueno");
                default: return L.T("AIL_e7b1c906", "básico");
            }
        }

        private static string SeasonText()
        {
            var season = CampaignTime.Now.GetSeasonOfYear;
            return season == CampaignTime.Seasons.Winter ? L.T("AIL_e37fa365", "invierno (riesgo alto de enfermedad)")
                 : season == CampaignTime.Seasons.Autumn ? L.T("AIL_8fdf1238", "otoño (riesgo medio)")
                 : season == CampaignTime.Seasons.Spring ? L.T("AIL_62c99c8c", "primavera (riesgo medio)")
                 : L.T("AIL_7319097c", "verano (riesgo bajo)");
        }

        /// <summary>Each line is built on its own: one that fails is logged and left out, the rest still show.</summary>
        private static void AddLine(List<string> lines, string what, Func<string> make)
        {
            try
            {
                var s = make();
                if (!string.IsNullOrEmpty(s)) lines.Add(s);
            }
            catch (Exception ex) { Log.Error("[disease] hospital line: " + what, ex); }
        }

        private static string RoadRiskLine()
        {
            if (!Enabled || !S.Get.EnableSeasonalDiseases)
                return L.T("AIL_e7b1c90d", "Riesgo de coger un mal de temporada por el camino: no aplica (los males de temporada están desactivados).");
            string pct = Num(PlayerRoadRiskPerDay() * 100f);
            return L.F("AIL_e7b1c90e", $"Riesgo de coger un mal de temporada por el camino, aquí y ahora: {pct} % al día (ya cuenta tu prevención).");
        }

        private static string SurgeonLine()
        {
            var party = MobileParty.MainParty;
            var surgeon = party?.EffectiveSurgeon;
            if (surgeon == null) return L.T("AIL_e7b1c910", "No tienes cirujano: nadie de tu partida cuida de los enfermos por el camino.");
            int med = surgeon.GetSkillValue(DefaultSkills.Medicine);
            string advice = med >= 150 ? L.T("AIL_e7b1c7d4", "Tus enfermos se curan mucho antes y pocos empeoran.")
                          : med >= 60 ? L.T("AIL_e7b1c7d5", "Ayuda a que tus enfermos se curen antes.")
                          : L.T("AIL_e7b1c7d6", "Sabe poco: tus enfermos dependen sobre todo de un buen hospital.");
            // Without a surgeon named, the game hands the job to the party's leader
            if (surgeon == Hero.MainHero && party.GetRoleHolder(PartyRole.Surgeon) != Hero.MainHero)
                return L.F("AIL_e7b1c912", $"No has nombrado cirujano: cuidas tú de los enfermos (Medicina {med}). {advice}");
            return L.F("AIL_e7b1c911", $"Tu cirujano: {surgeon.Name} (Medicina {med}). {advice}");
        }

        private static string PreventionLine()
        {
            var state = WorldSim.State;
            double now = CampaignTime.Now.ToDays;
            var parts = new List<string>();
            if (state.PreventionUntilDay > now)
            {
                int d = (int)Math.Ceiling(state.PreventionUntilDay - now);
                parts.Add(L.F("AIL_e7b1c915", $"remedios del hospital, {d} día(s) más"));
            }
            if (state.HerbGuardUntilDay > now && state.HerbGuard > 0f)
            {
                int pct = (int)Math.Round(state.HerbGuard * 100f), d = (int)Math.Ceiling(state.HerbGuardUntilDay - now);
                parts.Add(L.F("AIL_e7b1c916", $"hierbas, un {pct}% menos de contagio, {d} día(s) más"));
            }
            string what = parts.Count == 0 ? L.T("AIL_e7b1c914", "ninguna") : string.Join("; ", parts);
            return L.F("AIL_e7b1c913", $"Prevención: {what}.");
        }

        /// <summary>Left half of the window's header: the place.</summary>
        internal static List<string> HospitalFactsLeft(Settlement st)
        {
            var lines = new List<string>();
            AddLine(lines, "level", () =>
            {
                int tier = MedicalTier(st);
                string level = LevelName(st), care = Num(CareQuality(st));
                return L.F("AIL_e7b1c907", $"Nivel médico: {tier} de 4 ({level}). Cuidados del lugar: fuerza ×{care}.");
            });
            AddLine(lines, "season", () => { string season = SeasonText(); return L.F("AIL_e7b1c908", $"Estación: {season}."); });
            AddLine(lines, "road risk", RoadRiskLine);
            AddLine(lines, "outbreak", () =>
            {
                var o = Enabled ? At(st) : null;
                if (o == null) return L.T("AIL_e7b1c90a", "Aquí no corre ninguna enfermedad. Cuarentena: no.");
                string sev = SeverityEs(o.Severity), closed = o.Quarantined ? L.T("AIL_e7b1c90b", "sí") : L.T("AIL_e7b1c90c", "no");
                return L.F("AIL_e7b1c909", $"Aquí corre {o.Name} ({sev}). Cuarentena: {closed}.");
            });
            return lines;
        }

        /// <summary>Right half of the window's header: the player.</summary>
        internal static List<string> HospitalFactsRight(Settlement st)
        {
            var lines = new List<string>();
            AddLine(lines, "purse", () => { int gold = Hero.MainHero.Gold; return L.F("AIL_e7b1c90f", $"Tu bolsa: {gold} denarios."); });
            AddLine(lines, "surgeon", SurgeonLine);
            if (Enabled) AddLine(lines, "prevention", PreventionLine);
            AddLine(lines, "debt", () =>
            {
                int debt = DebtOf(Clan.PlayerClan);
                return debt > 0 ? L.F("AIL_e7b1c917", $"Tu clan debe {debt} denarios a los médicos; se cobran con los gastos del día.") : null;
            });
            return lines;
        }

        // ============================================================ the sick

        internal static List<HospitalSection> HospitalSections(Settlement st)
        {
            var list = new List<HospitalSection>();
            var party = MobileParty.MainParty;
            var me = Hero.MainHero;
            if (party == null || me == null) return list;

            var you = new HospitalSection { Kind = HospitalSection.You, Title = L.T("AIL_e7b1c918", "Tú"), WoundCost = CostSelf(st) };
            you.Info = me.HitPoints >= me.MaxHitPoints * 0.95f ? L.T("AIL_e7b1c919", "Sin heridas.") : L.F("AIL_e7b1c91a", $"Herido: {me.HitPoints} de {me.MaxHitPoints} de vida.");
            foreach (var s in (OfHero(me) ?? new List<Sickness>()).ToList()) you.Cases.Add(new KeyValuePair<Sickness, string>(s, me.Name.ToString()));
            list.Add(you);

            var mates = MyHeroes().Where(h => h != null && h != me).ToList();
            if (mates.Count > 0)
            {
                int sick = mates.Count(h => OfHero(h) != null), total = mates.Count, hurt = WoundedCompanions().Count;
                var sec = new HospitalSection { Kind = HospitalSection.Companions, Title = L.F("AIL_e7b1c91b", $"Compañeros ({sick} de {total} enfermos)"), WoundCost = CostCompanions(st) };
                sec.Info = hurt > 0 ? L.F("AIL_e7b1c91c", $"{hurt} compañero(s) herido(s).") : L.T("AIL_e7b1c919", "Sin heridas.");
                foreach (var h in mates)
                    foreach (var s in (OfHero(h) ?? new List<Sickness>()).ToList()) sec.Cases.Add(new KeyValuePair<Sickness, string>(s, h.Name.ToString()));
                list.Add(sec);
            }

            int men = party.MemberRoster.TotalRegulars;
            var troops = (OfTroops(party) ?? new List<Sickness>()).ToList();
            if (men > 0 || troops.Count > 0)
            {
                int sick = Math.Min(men, troops.Sum(x => x.Count)), hurt = WoundedCount(party.MemberRoster);
                var sec = new HospitalSection { Kind = HospitalSection.Troops, Title = L.F("AIL_e7b1c91d", $"Tropas ({sick} de {men} enfermos)"), WoundCost = CostRoster(party.MemberRoster, st) };
                sec.Info = hurt > 0 ? L.F("AIL_e7b1c91e", $"{hurt} soldado(s) herido(s) o enfermo(s).") : L.T("AIL_e7b1c919", "Sin heridas.");
                foreach (var s in troops) { int n = s.Count; sec.Cases.Add(new KeyValuePair<Sickness, string>(s, L.F("AIL_e7b1c921", $"{n} soldado(s)"))); }
                list.Add(sec);
            }

            int held = party.PrisonRoster.TotalRegulars;
            var captives = (OfPrisoners(party) ?? new List<Sickness>()).ToList();
            if (held > 0 || captives.Count > 0)
            {
                int sick = Math.Min(held, captives.Sum(x => x.Count)), hurt = WoundedCount(party.PrisonRoster);
                var sec = new HospitalSection { Kind = HospitalSection.Prisoners, Title = L.F("AIL_e7b1c91f", $"Prisioneros ({sick} de {held} enfermos)"), WoundCost = CostRoster(party.PrisonRoster, st) };
                sec.Info = hurt > 0 ? L.F("AIL_e7b1c920", $"{hurt} prisionero(s) herido(s).") : L.T("AIL_e7b1c919", "Sin heridas.");
                foreach (var s in captives) { int n = s.Count; sec.Cases.Add(new KeyValuePair<Sickness, string>(s, L.F("AIL_e7b1c922", $"{n} prisionero(s)"))); }
                list.Add(sec);
            }
            return list;
        }

        /// <summary>A case can start a treatment: still sick, not under one, and still on the books.</summary>
        internal static bool HospitalCanTreat(Sickness s) => s != null && Enabled && NeedsCare(s) && Sick.Contains(s);

        /// <summary>Severe enough for "only the severe": a dangerous disease, or one already far along.</summary>
        internal static bool HospitalSevere(Sickness s) => s != null && (Sev(s.Severity) >= 3 || s.Progress >= 50f);

        private static int CaseMedicine(Sickness s)
        {
            if (s.Kind == Sickness.Hero) return Medicine(FindHero(s.TargetId));
            return TroopMedicine(MobileParty.MainParty, s.Kind == Sickness.Prisoners);
        }

        /// <summary>The chance, each day of the course, that the treatment wins ground (the same sum as in Advance).</summary>
        internal static int HospitalChance(Sickness s, HospitalMethod m)
        {
            int sev = Sev(s.Severity);
            return (int)Math.Max(5f, Math.Min(100f, 50f + CaseMedicine(s) * 0.2f + m.Quality * 25f - sev * 15f));
        }

        internal static int HospitalCost(Sickness s, HospitalMethod m, Settlement st)
        {
            int cost = s.Kind == Sickness.Hero ? HeroCareCost(s, st) : BandCareCost(s, st);
            if (m != null && m.Herb >= 0 && m.Herb < Herbs.Length) cost += HerbUnits(s) * HerbPrice(Herbs[m.Herb]);
            return cost;
        }

        internal static bool HospitalCanPay(int cost) => cost <= 0 || Hero.MainHero.Gold >= cost || S.Get.DiseaseTreatmentDebt;

        internal static string HospitalCaseTitle(Sickness s, string who)
        {
            string sev = SeverityEs(s.Severity);
            int progress = (int)s.Progress;
            return L.F("AIL_e7b1c923", $"{who}: {s.Name} ({sev}), avance {progress}%");
        }

        private static string StateText(Sickness s, int med, double now)
        {
            int sev = Sev(s.Severity);
            if (s.Treated)
            {
                int left = Math.Max(1, (int)Math.Ceiling(TreatmentDays[sev] - (now - s.TreatDay)));
                return L.F("AIL_e7b1c924", $"En tratamiento, {left} día(s) más");
            }
            int after = s.AfterDays;
            if (after > 0) return L.F("AIL_e7b1c925", $"El tratamiento sigue haciendo efecto {after} día(s)");
            int runs = Math.Max(1, (int)Math.Ceiling(s.UntilDay - now));
            if (med >= HealerNeeded[sev]) return L.F("AIL_e7b1c926", $"Tu cirujano lo hace remitir; se pasa en {runs} día(s)");
            if (sev <= 2 || FxOf(s).Death <= 0f) return L.F("AIL_e7b1c927", $"Avanza, pero se pasará solo en {runs} día(s)");
            return L.F("AIL_e7b1c928", $"Avanza y puede matar; le quedan {runs} día(s) de curso");
        }

        /// <summary>What a course of this strength is likely to do: the expected daily change early in the course.</summary>
        private static string Verdict(Sickness s, HospitalMethod m, int med, int chance)
        {
            var cfg = S.Get;
            int sev = Sev(s.Severity);
            float p = chance / 100f;
            float worse = DailyWorse[sev] * cfg.DiseaseProgressionMultiplier * (1f - MedEff(med, sev));
            float better = DailyBetter[sev] * cfg.DiseaseRecoveryMultiplier * m.Quality * Difficulty(s.Progress) * 0.6f;
            float net = p * better - (1f - p) * worse;
            return net > 0.25f ? L.T("AIL_e7b1c929", "lo cura")
                 : net > -0.25f ? L.T("AIL_e7b1c92a", "solo lo contiene")
                 : L.T("AIL_e7b1c92b", "no basta: seguirá avanzando");
        }

        /// <summary>Second line of a patient: how it goes now and, when it can be treated, what the chosen treatment would do and cost.</summary>
        internal static string HospitalCaseState(Sickness s, HospitalMethod m, Settlement st)
        {
            int med = CaseMedicine(s);
            string state = StateText(s, med, CampaignTime.Now.ToDays);
            if (m == null || !HospitalCanTreat(s)) return state;
            int chance = HospitalChance(s, m), cost = HospitalCost(s, m, st);
            string verdict = Verdict(s, m, med, chance);
            return L.F("AIL_e7b1c92c", $"{state}. Con este tratamiento: {chance}% de mejora al día, {verdict}; {cost} denarios.");
        }

        // ============================================================ ways of treatment

        private static int BestHerb(Settlement st)
        {
            if (st == null) return 0;
            if (!BuildingsOn) return st.IsVillage ? 1 : TierBestHerb[MedicalTier(st)];
            for (int i = 5; i >= 1; i--) if (StockOf(st, i) >= 0.2f) return i;
            return 0;
        }

        /// <summary>The stock line of a herb in the hospital window: how much is left, or when the next bundle comes.</summary>
        private static string StockLine(Settlement st, int strength)
        {
            if (!BuildingsOn) return "";
            float have = StockOf(st, strength);
            if (have >= 1f) return " " + L.F("AIL_e7b1dec8", $"Quedan {Num(have)}.");
            int d = DaysToNext(st, strength, 1f);
            return " " + (d < 0 ? L.T("AIL_e7b1dec9", "Agotadas, y aquí no se hacen.") : L.F("AIL_e7b1deca", $"Agotadas: el próximo lote llega en {d} días."));
        }

        internal static List<HospitalMethod> HospitalMethods(Settlement st, out bool betterElsewhere)
        {
            betterElsewhere = false;
            var list = new List<HospitalMethod>();
            float local = CareQuality(st);
            string strength = Num(local);
            list.Add(new HospitalMethod
            {
                Herb = -1, Quality = local,
                Name = st != null && st.IsVillage ? L.T("AIL_e7b1c92d", "Cuidados del curandero") : L.T("AIL_e7b1c92e", "Cuidados del hospital"),
                Detail = L.F("AIL_e7b1c92f", $"Fuerza ×{strength}. El precio depende de la gravedad y, en las tropas, de cuántos son."),
            });
            if (!HerbsOn) return list;
            int best = BestHerb(st);
            for (int i = 0; i < Herbs.Length; i++)
            {
                var h = Herbs[i];
                if (h.Strength > best) { betterElsewhere = true; break; }
                float q = Math.Min(4f, local * h.Quality);
                string name = HerbName(h), power = Num(q);
                int price = HerbPrice(h);
                list.Add(new HospitalMethod
                {
                    Herb = i, Quality = q,
                    Name = L.F("AIL_e7b1c930", $"{name} y cuidados del lugar"),
                    Detail = L.F("AIL_e7b1c931", $"Fuerza ×{power}. {price} denarios por dosis, además de los cuidados.") + StockLine(st, h.Strength),
                });
            }
            return list;
        }

        /// <summary>Starts a course for the cases given, all with the same method, paid at once (or partly owed by the clan).</summary>
        internal static bool HospitalTreat(List<Sickness> cases, HospitalMethod m, Settlement st)
        {
            if (m == null || cases == null) return false;
            var open = cases.Where(HospitalCanTreat).Distinct().ToList();
            if (open.Count == 0) return false;
            int cost = open.Sum(s => HospitalCost(s, m, st));
            float bundles = open.Sum(Bundles);
            int strength = m.Herb >= 0 && m.Herb < Herbs.Length ? Herbs[m.Herb].Strength : 1;
            if (m.Herb >= 0 && StockOf(st, strength) + 0.001f < bundles)
            {
                int d = DaysToNext(st, strength, bundles);
                WorldSim.Say(d < 0 ? L.F("AIL_e7b1decb", $"No quedan bastantes {HerbName(Herbs[m.Herb])} y aquí no se hacen.") : L.F("AIL_e7b1decc", $"No quedan bastantes {HerbName(Herbs[m.Herb])}: el próximo lote llega en {d} días."), Colors.Yellow);
                return false;
            }
            if (!PayOrOwe(cost)) return false;
            float quality = m.Quality;
            if (m.Herb >= 0) UseStock(st, strength, bundles);
            else if (!UseStock(st, 1, bundles)) quality = OwnCareWithoutHerbs(st, quality);
            // a hero's own Good Lodgings and the governor's Clean Infrastructure count a level more (DiseasesPerks.cs)
            foreach (var s in open) StartTreatment(s, quality * LodgingBonus(st, s.Kind == Sickness.Hero ? FindHero(s.TargetId) : null));
            int n = open.Count;
            WorldSim.Say(L.F("AIL_e7b1c944", $"Los médicos empiezan el tratamiento de {n} enfermo(s) por {cost} denarios."), Colors.Green);
            Log.Info($"[disease] hospital of {st?.Name}: {n} case(s) put under treatment for {cost} denars (strength {m.Quality:0.##})");
            return true;
        }

        // ============================================================ wounds

        internal static bool HospitalHealWounds(int kind, Settlement st)
        {
            var party = MobileParty.MainParty;
            switch (kind)
            {
                case HospitalSection.You:
                    if (!Pay(CostSelf(st))) return false;
                    Hero.MainHero.HitPoints = Hero.MainHero.MaxHitPoints;
                    WorldSim.Say(L.T("AIL_24d3d112", "Los médicos te dejan como nuevo."), Colors.Green);
                    return true;
                case HospitalSection.Companions:
                    var hurt = WoundedCompanions();
                    if (hurt.Count == 0 || !Pay(CostCompanions(st))) return false;
                    foreach (var h in hurt) h.HitPoints = h.MaxHitPoints;
                    WorldSim.Say(L.T("AIL_52fe29a9", "Tus compañeros quedan curados."), Colors.Green);
                    return true;
                case HospitalSection.Troops:
                    if (!Pay(CostRoster(party.MemberRoster, st))) return false;
                    HealRoster(party.MemberRoster);
                    WorldSim.Say(L.T("AIL_be8758dd", "Tus soldados heridos y enfermos vuelven a filas."), Colors.Green);
                    return true;
                case HospitalSection.Prisoners:
                    if (!Pay(CostRoster(party.PrisonRoster, st))) return false;
                    HealRoster(party.PrisonRoster);
                    WorldSim.Say(L.T("AIL_e3e82b77", "Tus prisioneros quedan curados."), Colors.Green);
                    return true;
            }
            return false;
        }

        // ============================================================ prevention

        internal static List<HospitalPrevention> HospitalPreventions(Settlement st)
        {
            var list = new List<HospitalPrevention>();
            if (!Enabled || MobileParty.MainParty == null) return list;
            double now = CampaignTime.Now.ToDays;
            int days = PreventionDays, price = CostPrevention(st);
            var remedies = new HospitalPrevention
            {
                Herb = -1, Price = price, Allowed = WorldSim.State.PreventionUntilDay <= now + PreventionDays - 1,
                Text = L.F("AIL_e7b1c932", $"Remedios del hospital: agua hervida, ropa limpia y vahos. Tu partida enferma un 75% menos durante {days} días."),
                Button = L.F("AIL_e7b1c934", $"Comprar ({price})"),
            };
            if (WorldSim.State.PreventionUntilDay > now)
            {
                int left = (int)Math.Ceiling(WorldSim.State.PreventionUntilDay - now);
                remedies.Text += " " + L.F("AIL_e7b1c94d", $"Ya la tienes {left} día(s) más.");
            }
            list.Add(remedies);
            if (!HerbsOn) return list;
            int best = BestHerb(st), doses = GuardUnits();
            for (int i = 0; i < Herbs.Length; i++)
            {
                var h = Herbs[i];
                if (h.Strength > best) break;
                string name = HerbName(h);
                int pct = (int)Math.Round(h.Guard * 100f), d = h.Days, cost = HerbPrice(h) * doses;
                list.Add(new HospitalPrevention
                {
                    Herb = i, Price = cost, Allowed = true,
                    Text = L.F("AIL_e7b1c933", $"{name}: toda tu partida las toma ahora ({doses} dosis) y enferma un {pct}% menos durante {d} días. Sustituyen a las hierbas que tomaste antes."),
                    Button = L.F("AIL_e7b1c935", $"Comprar y tomar ({cost})"),
                });
            }
            return list;
        }

        internal static bool HospitalBuyPrevention(HospitalPrevention p, Settlement st)
        {
            if (p == null || !Enabled) return false;
            double now = CampaignTime.Now.ToDays;
            if (p.Herb < 0)
            {
                if (WorldSim.State.PreventionUntilDay > now + PreventionDays - 1) return false;
                if (!Pay(CostPrevention(st))) return false;
                WorldSim.State.PreventionUntilDay = now + PreventionDays;
                WorldSim.Say(L.F("AIL_e7c1ccde", $"Tu partida queda prevenida durante {PreventionDays} días."), Colors.Green);
                return true;
            }
            if (!HerbsOn || p.Herb >= Herbs.Length || Herbs[p.Herb].Strength > BestHerb(st)) return false;
            var h = Herbs[p.Herb];
            if (StockOf(st, h.Strength) + 0.001f < GuardUnits())
            {
                int d = DaysToNext(st, h.Strength, GuardUnits());
                WorldSim.Say(d < 0 ? L.F("AIL_e7b1decb", $"No quedan bastantes {HerbName(h)} y aquí no se hacen.") : L.F("AIL_e7b1decc", $"No quedan bastantes {HerbName(h)}: el próximo lote llega en {d} días."), Colors.Yellow);
                return false;
            }
            if (!Pay(HerbPrice(h) * GuardUnits())) return false;
            UseStock(st, h.Strength, GuardUnits());
            DrinkHerb(h);
            return true;
        }

        // ============================================================ what the physicians know

        internal static string HospitalOutbreaks(Settlement here)
        {
            if (!Enabled) return L.T("AIL_e7b1c94b", "Las enfermedades no están activadas: aquí solo se curan heridas.");
            var list = All.Select(o => (o, st: Settlement.Find(o.SettlementId))).Where(x => x.st != null)
                          .OrderBy(x => here != null ? x.st.GetPosition2D.Distance(here.GetPosition2D) : 0f).Take(10).ToList();
            if (list.Count == 0) return L.T("AIL_0b9bdfcf", "Los médicos no tienen noticia de ninguna enfermedad en Calradia ahora mismo.");
            return string.Join("\n", list.Select(x => L.F("AIL_a63c51e2", $"• {x.st.Name}: {x.o.Name}, {SeverityEs(x.o.Severity)}; le quedan unos {(int)Math.Max(1, x.o.UntilDay - CampaignTime.Now.ToDays)} días") + (x.o.Quarantined ? L.T("AIL_c1183e43", " (en cuarentena)") : "")))
                   + L.T("AIL_63b5c8dc", "\n\nEntrar en un sitio con enfermos puede hacer caer a parte de tus hombres. La cuarentena corta el contagio a los vecinos, pero hunde el comercio.");
        }

        internal static string HospitalOutbreaksTitle => L.T("AIL_965719f1", "Lo que saben los médicos");
    }
}
