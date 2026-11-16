using System;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>
    /// What the Medicine perks do against disease, with AIInfluence's numbers (its DiseasePerkBonuses), fitted to our
    /// model. The party's perks are the effective surgeon's, the personal ones the hero's own, and a settlement's those
    /// of its governor. Each perk's description in the character screen says what it does here (ApplyPerkTexts).
    /// </summary>
    internal static partial class Diseases
    {
        private const float SelfMedication = 0.15f;      // recovery, personal
        private const float PreventiveMedicine = 0.10f;  // immunity, personal
        private const float TriageTent = 0.20f;          // recovery, party not moving
        private const float WalkItOffLow = 10f;          // below this progress it heals on its own (5 without the perk)
        private const float BaseLow = 5f;
        private const float Sledges = 0.30f;             // less of the march penalty
        private const float DoctorsOath = 0.50f;         // of the captives who would come in sick
        private const float BestMedicineMorale = 70f, BestMedicine = 0.15f;
        private const float SiegeMedic = 0.30f;          // slower worsening while besieging or besieged
        private const float Veterinarian = 0.25f;        // less spread inside the party
        private const float PristineStreets = 0.20f;     // less spread in the governed settlement
        private const float BushDoctor = 0.25f;          // cheaper herbs and remedies near the governed settlement
        private const float PerfectHealthPerFood = 0.03f;// immunity per kind of food carried
        private const float PhysicianOfPeople = 0.30f;   // daily share of sick tier 1-2 men who get over a severe illness
        private const float FortitudeTonic = 0.10f;      // immunity of the heroes of the party
        private const float HelpingHandsPerTen = 0.01f;  // recovery per ten men
        private const float BattleHardened = 0.05f;      // lasting immunity once the men got over a severe illness
        private const float MinisterImmunity = 0.005f, MinisterHealing = 0.003f;
        private const int MinisterFrom = 250;
        private const string HardenedKey = "hardened|";  // in SickImmune, which the campaign already saves
        private const double Forever = 1e7;

        private static Hero Surgeon(MobileParty p) { try { return p?.EffectiveSurgeon; } catch { return null; } }
        private static bool Has(Hero h, PerkObject perk) { try { return h != null && perk != null && h.GetPerkValue(perk); } catch { return false; } }
        private static int Med(Hero h) { try { return h?.GetSkillValue(DefaultSkills.Medicine) ?? 0; } catch { return 0; } }
        private static bool Moving(MobileParty p) { try { return p != null && p.IsMoving; } catch { return true; } }

        /// <summary>The share of the party's own surgeon in the speed of healing: Triage Tent, Best Medicine, Helping Hands, Minister of Health.</summary>
        private static float SurgeonRecovery(MobileParty p)
        {
            var s = Surgeon(p);
            if (s == null) return 0f;
            float add = 0f;
            if (!Moving(p) && Has(s, DefaultPerks.Medicine.TriageTent)) add += TriageTent;
            try { if (p.Morale > BestMedicineMorale && Has(s, DefaultPerks.Medicine.BestMedicine)) add += BestMedicine; } catch { }
            if (Has(s, DefaultPerks.Medicine.HelpingHands)) add += (p.MemberRoster?.TotalManCount ?? 0) / 10 * HelpingHandsPerTen;
            if (Has(s, DefaultPerks.Medicine.MinisterOfHealth) && Med(s) > MinisterFrom) add += (Med(s) - MinisterFrom) * MinisterHealing;
            return add;
        }

        /// <summary>How much faster a hero heals: their own Self Medication and the party's surgeon.</summary>
        private static float HeroRecovery(Hero h)
        {
            if (h == null) return 1f;
            float m = 1f;
            if (Has(h, DefaultPerks.Medicine.SelfMedication)) m += SelfMedication;
            m += SurgeonRecovery(h.PartyBelongedTo);
            return m;
        }

        private static float TroopRecovery(MobileParty p) => 1f + SurgeonRecovery(p);

        /// <summary>A siege slows the illness down with Siege Medic.</summary>
        private static float SiegeSlowing(MobileParty p)
        {
            try { return p?.SiegeEvent != null && Has(Surgeon(p), DefaultPerks.Medicine.SiegeMedic) ? SiegeMedic : 0f; }
            catch { return 0f; }
        }

        /// <summary>Below this progress an illness wears off by itself; Walk It Off raises it.</summary>
        private static float LowProgress(MobileParty p) => Has(Surgeon(p), DefaultPerks.Medicine.WalkItOff) ? WalkItOffLow : BaseLow;

        /// <summary>Chance that a hero shrugs off an illness they would catch: Preventive Medicine, Fortitude Tonic, Perfect Health, Minister of Health.</summary>
        private static float HeroImmunity(Hero h)
        {
            if (h == null) return 0f;
            float i = 0f;
            if (Has(h, DefaultPerks.Medicine.PreventiveMedicine)) i += PreventiveMedicine;
            var p = h.PartyBelongedTo;
            var s = Surgeon(p);
            if (s != null)
            {
                if (Has(s, DefaultPerks.Medicine.FortitudeTonic)) i += FortitudeTonic;
                if (Has(s, DefaultPerks.Medicine.PerfectHealth)) i += FoodKinds(p) * PerfectHealthPerFood;
            }
            if (Has(h, DefaultPerks.Medicine.MinisterOfHealth) && Med(h) > MinisterFrom) i += (Med(h) - MinisterFrom) * MinisterImmunity;
            return Math.Min(0.9f, i);
        }

        /// <summary>Share of the men a new illness spares: Battle Hardened, Perfect Health, Minister of Health.</summary>
        private static float TroopImmunity(MobileParty p)
        {
            if (p == null) return 0f;
            float i = 0f;
            if (Hardened(p)) i += BattleHardened;
            var s = Surgeon(p);
            if (s != null)
            {
                if (Has(s, DefaultPerks.Medicine.PerfectHealth)) i += FoodKinds(p) * PerfectHealthPerFood;
                if (Has(s, DefaultPerks.Medicine.MinisterOfHealth) && Med(s) > MinisterFrom) i += (Med(s) - MinisterFrom) * MinisterImmunity;
            }
            return Math.Min(0.9f, i);
        }

        private static int FoodKinds(MobileParty p) { try { return p?.ItemRoster?.FoodVariety ?? 0; } catch { return 0; } }

        private static bool Hardened(MobileParty p) => p != null && Immune.ContainsKey(HardenedKey + p.StringId);

        /// <summary>Men who got over a severe illness under a surgeon with Battle Hardened keep a little immunity for good.</summary>
        private static void Harden(MobileParty p, Sickness s)
        {
            try
            {
                if (p == null || Sev(s.Severity) < 3 || Hardened(p) || !Has(Surgeon(p), DefaultPerks.Medicine.BattleHardened)) return;
                Immune[HardenedKey + p.StringId] = CampaignTime.Now.ToDays + Forever;
                Log.Info($"[disease] Battle Hardened: the men of {p.Name} got over {s.NameEn} and keep +{BattleHardened:P0} immunity");
            }
            catch { }
        }

        /// <summary>Less spread inside a party with Veterinarian.</summary>
        private static float PartySpread(MobileParty p) => Has(Surgeon(p), DefaultPerks.Medicine.Veterinarian) ? 1f - Veterinarian : 1f;

        /// <summary>Less spread in a settlement whose governor has Pristine Streets.</summary>
        private static float PlaceSpread(Settlement st)
        {
            try { return Has(st?.Town?.Governor, DefaultPerks.Medicine.PristineStreets) ? 1f - PristineStreets : 1f; }
            catch { return 1f; }
        }

        /// <summary>Less of the march penalty with Sledges.</summary>
        private static float SledgesEase(MobileParty p) => Has(Surgeon(p), DefaultPerks.Medicine.Sledges) ? Sledges : 0f;

        /// <summary>Herbs and the hospital's remedies cost less where the governor (of the town, or of a village's town) has Bush Doctor.</summary>
        private static float HerbDiscount(Settlement st)
        {
            try
            {
                var gov = st?.Town?.Governor ?? st?.Village?.Bound?.Town?.Governor;
                return Has(gov, DefaultPerks.Medicine.BushDoctor) ? BushDoctor : 0f;
            }
            catch { return 0f; }
        }

        /// <summary>
        /// The treatment of a hero in a settlement counts one medical level more for each: their own Good Lodgings and
        /// the governor's Clean Infrastructure. Returned as a factor on the strength of the course.
        /// </summary>
        private static float LodgingBonus(Settlement st, Hero patient)
        {
            try
            {
                if (st == null || st.IsVillage || st.Town == null) return 1f;
                int bonus = (Has(patient, DefaultPerks.Medicine.GoodLogdings) ? 1 : 0) + (Has(st.Town.Governor, DefaultPerks.Medicine.CleanInfrastructure) ? 1 : 0);
                if (bonus == 0) return 1f;
                int tier = Math.Max(1, Math.Min(4, MedicalTier(st)));
                return TierCare[Math.Min(4, tier + bonus)] / TierCare[tier];
            }
            catch { return 1f; }
        }

        /// <summary>Tier 1 and 2 men of a surgeon with Health Advise do not die of anything short of a plague.</summary>
        private static int SparedTier(MobileParty p, Sickness s) =>
            Sev(s.Severity) < 5 && Has(Surgeon(p), DefaultPerks.Medicine.HealthAdvise) ? 2 : 0;

        /// <summary>With Physician of the People, part of the sick tier 1 and 2 men get over a severe (not deadly) illness each day.</summary>
        private static int PhysicianRecovers(MobileParty p, Sickness s, TroopRoster roster)
        {
            try
            {
                int sev = Sev(s.Severity);
                if (sev < 3 || sev >= 5 || s.Kind != Sickness.Troops || !Has(Surgeon(p), DefaultPerks.Medicine.PhysicianOfPeople)) return 0;
                int regulars = Math.Max(1, roster.TotalRegulars);
                int low = roster.GetTroopRoster().Where(e => e.Character != null && !e.Character.IsHero && e.Character.Tier <= 2).Sum(e => e.Number);
                int sickLow = (int)Math.Round(s.Count * Math.Min(1f, low / (float)regulars));
                return Math.Min(s.Count, Whole(sickLow * PhysicianOfPeople));
            }
            catch { return 0; }
        }

        /// <summary>Doctor's Oath: half of the captives who would come in sick are seen to by the surgeon.</summary>
        private static float OathShare(MobileParty winner) => Has(Surgeon(winner), DefaultPerks.Medicine.DoctorsOath) ? 1f - DoctorsOath : 1f;

        // ============================================================ the perks' descriptions

        private static FieldInfo _descField, _primaryField, _secondaryField;
        private static bool _fieldsLooked;

        private static string PerkText(PerkObject perk)
        {
            switch (perk?.StringId)
            {
                case "MedicineSelfMedication": return L.T("AIL_e7b1db20", "+15% de rapidez al curarse de una enfermedad (personal)");
                case "MedicinePreventiveMedicine": return L.T("AIL_e7b1db21", "+10% de inmunidad a las enfermedades (personal)");
                case "MedicineTriageTent": return L.T("AIL_e7b1db22", "+20% de rapidez al curar enfermedades con la partida parada");
                case "MedicineWalkItOff": return L.T("AIL_e7b1db23", "Una enfermedad por debajo del 10% de avance se pasa sola (5% sin la ventaja)");
                case "MedicineSledges": return L.T("AIL_e7b1db24", "-30% de la lentitud en el mapa que causan las enfermedades");
                case "MedicineDoctorsOath": return L.T("AIL_e7b1db25", "Los prisioneros capturados a partidas enfermas llegan la mitad de enfermos");
                case "MedicineBestMedicine": return L.T("AIL_e7b1db26", "+15% de rapidez al curar enfermedades con la moral por encima de 70");
                case "MedicineGoodLodging": return L.T("AIL_e7b1db27", "+1 nivel médico cuando te tratan en un asentamiento");
                case "MedicineSiegeMedic": return L.T("AIL_e7b1db28", "Las enfermedades avanzan un 30% más despacio durante un asedio");
                case "MedicineVeterinarian": return L.T("AIL_e7b1db29", "-25% de contagio dentro de la partida");
                case "MedicinePristineStreets": return L.T("AIL_e7b1db2a", "-20% de contagio en el asentamiento que gobiernas");
                case "MedicineBushDoctor": return L.T("AIL_e7b1db2b", "-25% en el precio de hierbas y remedios cerca del asentamiento que gobiernas");
                case "MedicinePerfectHealth": return L.T("AIL_e7b1db2c", "+3% de inmunidad de la partida por cada tipo de comida que lleva");
                case "MedicineHealthAdvise": return L.T("AIL_e7b1db2d", "Las tropas de nivel 1 y 2 no mueren de enfermedad (salvo de una plaga)");
                case "MedicinePhysicianOfPeople": return L.T("AIL_e7b1db2e", "Cada día, un 30% de las tropas de nivel 1 y 2 enfermas de algo grave se recuperan solas");
                case "MedicineCleanInfrastructure": return L.T("AIL_e7b1db2f", "+1 nivel médico en el asentamiento que gobiernas");
                case "MedicineCheatDeath": return L.T("AIL_e7b1db30", "Sobrevives una vez a una enfermedad que llega al 100% (vuelve al 50%)");
                case "MedicineFortitudeTonic": return L.T("AIL_e7b1db31", "+10% de inmunidad a las enfermedades para los héroes de tu partida");
                case "MedicineHelpingHands": return L.T("AIL_e7b1db32", "+1% de rapidez al curar enfermedades por cada 10 hombres");
                case "MedicineBattleHardened": return L.T("AIL_e7b1db33", "Las tropas que superan una enfermedad grave ganan un 5% de inmunidad para siempre");
                case "MedicineMinisterOfHealth": return L.T("AIL_e7b1db34", "+0,5% de inmunidad y +0,3% de curación por cada punto de Medicina por encima de 250");
                default: return null;
            }
        }

        /// <summary>
        /// Adds to the description of each Medicine perk what it does against disease, as AIInfluence does. Run when a
        /// campaign starts (perks are made anew for each); a perk that already carries the line is left alone.
        /// </summary>
        internal static void ApplyPerkTexts()
        {
            try
            {
                if (!Enabled || Campaign.Current == null) return;
                if (!_fieldsLooked)
                {
                    _fieldsLooked = true;
                    _descField = typeof(PropertyObject).GetField("_description", BindingFlags.Instance | BindingFlags.NonPublic);
                    _primaryField = typeof(PerkObject).GetField("<PrimaryDescription>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                    _secondaryField = typeof(PerkObject).GetField("<SecondaryDescription>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
                    if (_descField == null) Log.Warn("[disease] the perks' description field was not found; the Medicine perks keep their own text");
                }
                if (_descField == null) return;
                int n = 0;
                foreach (var perk in PerkObject.All.ToList())
                {
                    if (perk?.Skill != DefaultSkills.Medicine) continue;
                    string add = PerkText(perk);
                    if (string.IsNullOrEmpty(add)) continue;
                    var desc = perk.Description;
                    if (desc != null && desc.ToString().Contains(add)) continue;
                    if (desc != null) _descField.SetValue(perk, new TextObject("{=!}" + desc + "\n\n" + add));
                    bool done = false;
                    var second = perk.SecondaryDescription;
                    if (_secondaryField != null && second != null && !string.IsNullOrEmpty(second.ToString()))
                    {
                        _secondaryField.SetValue(perk, new TextObject("{=!}" + second + "\n" + add));
                        done = true;
                    }
                    var first = perk.PrimaryDescription;
                    if (!done && _primaryField != null && first != null) _primaryField.SetValue(perk, new TextObject("{=!}" + first + "\n" + add));
                    n++;
                }
                if (n > 0) Log.Info($"[disease] what they do against disease added to the description of {n} Medicine perks");
            }
            catch (Exception ex) { Log.Error("[disease] Medicine perk descriptions", ex); }
        }
    }
}
