using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using HarmonyLib;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Buildings;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>A town's hospital or a castle's infirmary, kept by the mod: its level and building progress, its place in the lists, its herbs.</summary>
    public class HospitalRecord
    {
        [JsonProperty("level")] public int Level = 1;
        [JsonProperty("progress")] public float Progress;
        [JsonProperty("list_index")] public int ListIndex = -1;
        [JsonProperty("queue_index")] public int QueueIndex = -1;
        /// <summary>Bundles of each herb, weakest first (5).</summary>
        [JsonProperty("stock")] public float[] Stock = new float[5];
        [JsonProperty("short_day")] public double ShortDay = -1;
        /// <summary>A castle's infirmary rather than a town's hospital.</summary>
        [JsonProperty("castle")] public bool? Castle;
    }

    /// <summary>
    /// Hospital buildings and herb stocks, as AIInfluence has them. Every town has a «Hospital» and every castle an
    /// «Infirmary» in the town management screen, at level 1, built up like any other building (the AI more eagerly in an
    /// epidemic). Its level is the place's medical level (a capital one more). It makes herbs every day, more and stronger at a
    /// higher level, less in winter or with villages looted, none under siege, up to 30 days' worth; herbs lose 5% a day and the
    /// townsfolk use them, more in winter and three times more in an outbreak. Prices follow the stock (×1.75 scarce, ×0.8 in
    /// plenty), courses and the hospital's own care take from it, villages share a fifth of their town's. The buildings are
    /// taken out of the towns before every save and put back after it, so a save never holds them.
    /// </summary>
    internal static partial class Diseases
    {
        public const string TownHospitalId = "building_ailegacy_hospital";
        public const string CastleInfirmaryId = "building_ailegacy_infirmary";
        private static readonly int[] TownCosts = { 0, 2500, 5000 };
        private static readonly int[] CastleCosts = { 0, 1500, 3000 };
        private static readonly float[,] TownDaily = { { 6f, 2f, 0f, 0f, 0f }, { 8f, 4f, 1.5f, 0f, 0f }, { 10f, 5f, 3f, 1f, 0f } };
        private static readonly float[] TownUseShare = { 0.35f, 0.25f, 0.2f, 0.2f, 0.1f };
        private static BuildingType _townType, _castleType;

        private static Dictionary<string, HospitalRecord> Records => WorldSim.State?.Hospitals ?? (WorldSim.State != null ? WorldSim.State.Hospitals = new Dictionary<string, HospitalRecord>() : new Dictionary<string, HospitalRecord>());

        private static bool BuildingsOn => Enabled && S.Get.EnableHospital;

        internal static bool IsHospitalType(BuildingType t) => t != null && (t.StringId == TownHospitalId || t.StringId == CastleInfirmaryId);

        private static Building BuildingOf(Town town)
        {
            if (town?.Buildings == null) return null;
            foreach (var b in town.Buildings) if (IsHospitalType(b?.BuildingType)) return b;
            return null;
        }

        /// <summary>The two building types, registered with this game's objects once per session.</summary>
        public static void RegisterHospitals()
        {
            try
            {
                var om = Game.Current?.ObjectManager;
                if (om == null) return;
                // once per game: the types already in this game's objects are left as they are
                if (om.GetObject<BuildingType>(TownHospitalId) is BuildingType t && om.GetObject<BuildingType>(CastleInfirmaryId) is BuildingType c) { _townType = t; _castleType = c; return; }
                _townType = om.GetObject<BuildingType>(TownHospitalId) ?? om.RegisterPresumedObject(new BuildingType(TownHospitalId));
                _castleType = om.GetObject<BuildingType>(CastleInfirmaryId) ?? om.RegisterPresumedObject(new BuildingType(CastleInfirmaryId));
                var none = new Tuple<BuildingEffectEnum, BuildingEffectIncrementType, float, float, float>[0];
                _townType.Initialize(new TextObject("{=!}" + L.T("AIL_e7b1dec2", "Hospital")),
                    new TextObject("{=!}" + L.T("AIL_e7b1dec3", "Médicos, camas y un huerto de hierbas. Marca el nivel médico de la ciudad y las hierbas que hace cada día.")), TownCosts, none, false, 0f, 1);
                _castleType.Initialize(new TextObject("{=!}" + L.T("AIL_e7b1dec4", "Enfermería")),
                    new TextObject("{=!}" + L.T("AIL_e7b1dec5", "Un cirujano barbero y unos catres. Marca el nivel médico del castillo y las hierbas que hace cada día.")), CastleCosts, none, false, 0f, 1);
            }
            catch (Exception ex) { Log.Error("Registering the hospital buildings", ex); }
        }

        private static HospitalRecord RecordOf(Settlement st)
        {
            if (st == null) return null;
            if (Records.TryGetValue(st.StringId, out var r) && r != null)
            {
                if (r.Stock == null || r.Stock.Length != 5) { var s = new float[5]; if (r.Stock != null) Array.Copy(r.Stock, s, Math.Min(5, r.Stock.Length)); r.Stock = s; }
                return r;
            }
            r = new HospitalRecord();
            for (int i = 1; i <= 5; i++) r.Stock[i - 1] = BaseDaily(1, i, st.IsCastle, IsCapital(st)) * 10f;
            Records[st.StringId] = r;
            return r;
        }

        /// <summary>Puts the hospitals into every town and castle (or takes them out while the system is off).</summary>
        public static void EnsureHospitals()
        {
            try
            {
                if (_townType == null) RegisterHospitals();
                if (_townType == null || Campaign.Current == null) return;
                int put = 0, taken = 0;
                foreach (var st in Settlement.All)
                {
                    if (st == null || !st.IsFortification || st.Town == null) continue;
                    var town = st.Town;
                    var b = BuildingOf(town);
                    var r = RecordOf(st);
                    // the place changed kind (a town became a castle or the other way round): its hospital starts again
                    if (r.Castle.HasValue && r.Castle.Value != st.IsCastle) { r.Level = 1; r.Progress = 0f; r.QueueIndex = -1; }
                    r.Castle = st.IsCastle;
                    if (!BuildingsOn) { if (b != null) { Capture(st, b, r); Take(town, b); taken++; } continue; }
                    if (b != null) { Capture(st, b, r); continue; }
                    b = new Building(st.IsCastle ? _castleType : _townType, town, r.Progress, Math.Max(1, Math.Min(3, r.Level)));
                    if (r.ListIndex >= 0 && r.ListIndex <= town.Buildings.Count) town.Buildings.Insert(r.ListIndex, b); else town.Buildings.Add(b);
                    if (r.QueueIndex >= 0)
                    {
                        var q = town.BuildingsInProgress.ToList();
                        q.Insert(Math.Min(r.QueueIndex, q.Count), b);
                        town.BuildingsInProgress.Clear();
                        foreach (var x in q) town.BuildingsInProgress.Enqueue(x);
                    }
                    put++;
                }
                if (put > 0) Log.Info($"[hospital] hospitals put into {put} towns and castles");
                if (taken > 0) Log.Info($"[hospital] hospitals taken out of {taken} towns and castles (levels and herbs kept)");
            }
            catch (Exception ex) { Log.Error("Putting the hospitals in", ex); }
        }

        private static void Capture(Settlement st, Building b, HospitalRecord r)
        {
            r.Level = Math.Max(1, Math.Min(3, b.CurrentLevel));
            r.Progress = b.BuildingProgress;
            r.ListIndex = st.Town.Buildings.IndexOf(b);
            r.QueueIndex = st.Town.BuildingsInProgress.ToList().IndexOf(b);
        }

        private static void Take(Town town, Building b)
        {
            town.Buildings.Remove(b);
            if (!town.BuildingsInProgress.Contains(b)) return;
            var rest = town.BuildingsInProgress.Where(x => x != b).ToList();
            town.BuildingsInProgress.Clear();
            foreach (var x in rest) town.BuildingsInProgress.Enqueue(x);
        }

        /// <summary>Before a save: every hospital out of the towns, its state kept by the mod (a save never holds them).</summary>
        public static void OnSaveStarted()
        {
            try
            {
                if (Campaign.Current == null) return;
                // each place on its own: one that fails never leaves the others' hospitals in the save
                foreach (var st in Settlement.All)
                {
                    try
                    {
                        if (st == null || !st.IsFortification || st.Town == null) continue;
                        var b = BuildingOf(st.Town);
                        if (b == null) continue;
                        try { Capture(st, b, RecordOf(st)); } catch (Exception ex) { Log.Warn("[hospital] keeping a hospital's state: " + ex.Message); }
                        Take(st.Town, b);
                    }
                    catch (Exception ex) { Log.Error($"Taking the hospital of {st?.Name} out for the save", ex); }
                }
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("Taking the hospitals out for the save", ex); }
        }

        public static void OnSaveOver(bool ok, string name) => EnsureHospitals();

        /// <summary>The hospital's level, 1 to 3 (from its building, or as kept while it is out).</summary>
        internal static int HospitalLevel(Settlement st)
        {
            if (st == null || !st.IsFortification || st.Town == null) return 1;
            var b = BuildingOf(st.Town);
            return b != null ? Math.Max(1, Math.Min(3, b.CurrentLevel)) : Math.Max(1, Math.Min(3, RecordOf(st).Level));
        }

        // ============================================================ herbs: making, keeping, using

        private static float BaseDaily(int level, int strength, bool castle, bool capital)
        {
            if (strength < 1 || strength > 5) return 0f;
            int l = Math.Max(1, Math.Min(3, level));
            float v = strength == 5 ? (l >= 3 && capital ? 0.2f : 0f) : TownDaily[l - 1, strength - 1];
            return castle ? v * 0.5f : v;
        }

        private static float SeasonMaking(CampaignTime.Seasons s) => s == CampaignTime.Seasons.Spring ? 1.1f : s == CampaignTime.Seasons.Summer ? 1.2f : s == CampaignTime.Seasons.Winter ? 0.6f : 1f;
        private static float SeasonSickness(CampaignTime.Seasons s) => s == CampaignTime.Seasons.Summer ? 0.6f : s == CampaignTime.Seasons.Autumn ? 1.1f : s == CampaignTime.Seasons.Winter ? 1.6f : 1f;

        private static Settlement StockHolder(Settlement st) => st == null ? null : st.IsVillage ? st.Village?.Bound : st.IsFortification ? st : null;

        /// <summary>Bundles of that herb (1..5) to be had here; a village shares a fifth of its town's, of the three weaker herbs.</summary>
        internal static float StockOf(Settlement st, int strength)
        {
            if (!BuildingsOn) return 999f;
            var holder = StockHolder(st);
            if (holder == null || strength < 1 || strength > 5) return 0f;
            float s = RecordOf(holder).Stock[strength - 1];
            if (st.IsVillage) return strength > 3 ? 0f : s * 0.2f;
            return s;
        }

        /// <summary>×0.8 in plenty to ×1.75 when scarce, against ten days of what the place makes.</summary>
        private static float StockPrice(Settlement st, int strength)
        {
            if (!BuildingsOn) return 1f;
            var holder = StockHolder(st);
            if (holder == null) return 1f;
            float norm = BaseDaily(HospitalLevel(holder), strength, holder.IsCastle, IsCapital(holder)) * 10f;
            if (st.IsVillage) norm *= 0.2f;
            if (norm <= 0f) return 1.75f;
            return Math.Max(0.8f, Math.Min(1.75f, 1f + 0.75f * (1f - StockOf(st, strength) / norm)));
        }

        /// <summary>Bundles a course takes: one for a hero, a fifth of one per sick soldier.</summary>
        private static float Bundles(Sickness s) => s == null ? 0f : s.Kind == Sickness.Hero ? 1f : Math.Max(1, s.Count) * 0.2f;

        /// <summary>Takes bundles from the place's stock. False (and nothing taken) if there are not enough.</summary>
        private static bool UseStock(Settlement st, int strength, float bundles)
        {
            if (!BuildingsOn) return true;
            if (strength < 1 || strength > 5 || bundles <= 0f) return true;
            var holder = StockHolder(st);
            if (holder == null) return true;
            var r = RecordOf(holder);
            float have = StockOf(st, strength);
            if (have + 0.001f < bundles) { r.ShortDay = CampaignTime.Now.ToDays; return false; }
            r.Stock[strength - 1] = Math.Max(0f, r.Stock[strength - 1] - bundles);
            if (r.Stock[strength - 1] < 1f) r.ShortDay = CampaignTime.Now.ToDays;
            SaveFiles.Dirty();
            return true;
        }

        /// <summary>Days until the next bundle of that herb is made here, or -1 if it is not made.</summary>
        internal static int DaysToNext(Settlement st, int strength, float need)
        {
            var holder = StockHolder(st);
            if (holder == null) return -1;
            float have = StockOf(st, strength);
            if (have >= need) return 0;
            float daily = MakingToday(holder, strength) * (st.IsVillage ? 0.2f : 1f);
            return daily <= 0f ? -1 : (int)Math.Ceiling((need - have) / daily);
        }

        /// <summary>Half the making with every village looted, all of it with none.</summary>
        private static float VillageFactor(Settlement st)
        {
            var villages = st.BoundVillages;
            if (villages == null || villages.Count == 0) return 1f;
            int looted = villages.Count(x => x.VillageState == Village.VillageStates.Looted || x.VillageState == Village.VillageStates.BeingRaided);
            return 0.5f + 0.5f * (villages.Count - looted) / villages.Count;
        }

        private static float MakingToday(Settlement st, int strength, float villageFactor = -1f)
        {
            if (st.IsUnderSiege) return 0f;
            float v = BaseDaily(HospitalLevel(st), strength, st.IsCastle, IsCapital(st)) * SeasonMaking(CampaignTime.Now.GetSeasonOfYear);
            return v * (villageFactor >= 0f ? villageFactor : VillageFactor(st));
        }

        private static bool OutbreakIn(Settlement st)
        {
            try { return WorldSim.State.Outbreaks.Any(o => o.SettlementId == st.StringId && o.UntilDay > CampaignTime.Now.ToDays); } catch { return false; }
        }

        /// <summary>Each day: what each hospital makes, what goes bad, what the townsfolk use.</summary>
        public static void HospitalDay()
        {
            try
            {
                if (!BuildingsOn || Campaign.Current == null) return;
                var season = CampaignTime.Now.GetSeasonOfYear;
                foreach (var st in Settlement.All)
                {
                    if (st == null || !st.IsFortification || st.Town == null) continue;
                    var r = RecordOf(st);
                    int level = HospitalLevel(st);
                    bool castle = st.IsCastle, capital = IsCapital(st), outbreak = OutbreakIn(st);
                    float villageFactor = VillageFactor(st);
                    for (int i = 1; i <= 5; i++)
                    {
                        float baseDaily = BaseDaily(level, i, castle, capital);
                        float cap = baseDaily * 30f;
                        float s = Math.Max(0f, r.Stock[i - 1]);
                        float make = MakingToday(st, i, villageFactor);
                        if (make > 0f && s < cap) s = Math.Min(cap, s + make);
                        s *= 0.95f;
                        s = Math.Max(0f, s - baseDaily * TownUseShare[i - 1] * SeasonSickness(season) * (outbreak ? 3f : 1f));
                        r.Stock[i - 1] = (float)Math.Round(s, 2);
                    }
                }
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("[hospital] day", ex); }
        }

        internal static bool HadShortage(Settlement st, int days) { var r = st == null ? null : RecordOf(st); return r != null && r.ShortDay >= 0 && CampaignTime.Now.ToDays - r.ShortDay <= days; }

        /// <summary>The building's explanation at each level: what it makes.</summary>
        internal static TextObject HospitalExplanation(BuildingType t, int level)
        {
            bool castle = t.StringId == CastleInfirmaryId;
            var made = new List<string>();
            for (int i = 1; i <= 5; i++)
            {
                float d = BaseDaily(level, i, castle, i == 5);
                if (d > 0f) made.Add($"{HerbName(Herbs[i - 1])} {Num(d)}{(i == 5 ? " " + L.T("AIL_e7b1dec6", "(solo en una capital)") : "")}");
            }
            return new TextObject("{=!}" + L.F("AIL_e7b1dec7", $"Nivel médico {level} (una capital, uno más). Hierbas al día: {string.Join(", ", made)}."));
        }
    }

    // ============================================================ the buildings in the game's own screens

    [HarmonyPatch(typeof(DefaultBuildingModel), nameof(DefaultBuildingModel.CanAddBuildingTypeToTown))]
    internal static class HospitalCanAddPatch
    {
        private static void Postfix(BuildingType buildingType, Town town, ref bool __result)
        {
            try
            {
                if (town == null || !Diseases.IsHospitalType(buildingType)) return;
                __result = Diseases.Enabled && S.Get.EnableHospital && (buildingType.StringId == Diseases.TownHospitalId ? town.IsTown : town.IsCastle);
            }
            catch { }
        }
    }

    [HarmonyPatch(typeof(BuildingType), nameof(BuildingType.GetExplanationAtLevel))]
    internal static class HospitalExplanationPatch
    {
        private static void Postfix(BuildingType __instance, int level, ref TextObject __result)
        {
            try { if (Diseases.IsHospitalType(__instance)) __result = Diseases.HospitalExplanation(__instance, level); } catch { }
        }
    }

    /// <summary>The town management screen draws a building from a sprite named after it: the hospital borrows the waterworks'.</summary>
    [HarmonyPatch]
    internal static class HospitalSpritePatch
    {
        private static System.Reflection.MethodBase TargetMethod() =>
            AccessTools.Method(typeof(TaleWorlds.MountAndBlade.GauntletUI.Widgets.Menu.TownManagement.DevelopmentItemVisualButtonWidget), "DetermineSpriteImageFromSpriteCode");
        private static void Postfix(string spriteCode, bool useSmallVariant, ref string __result)
        {
            if (spriteCode != Diseases.TownHospitalId && spriteCode != Diseases.CastleInfirmaryId) return;
            __result = useSmallVariant ? "building_waterworks_t" : "building_waterworks";
        }
    }

    /// <summary>The AI builds its hospital up when sickness is near: an outbreak in the place or its realm, or herbs that ran out lately.</summary>
    [HarmonyPatch(typeof(DefaultBuildingScoreCalculationModel), nameof(DefaultBuildingScoreCalculationModel.GetNextBuilding))]
    internal static class HospitalAiPatch
    {
        private static readonly Random Rng = new Random();
        private static void Postfix(Town town, ref Building __result)
        {
            try
            {
                var st = town?.Settlement;
                if (st == null || !Diseases.Enabled || !S.Get.EnableHospital) return;
                Building h = null;
                foreach (var b in town.Buildings) if (Diseases.IsHospitalType(b?.BuildingType)) { h = b; break; }
                if (h == null || h == __result || h.CurrentLevel >= 3 || town.BuildingsInProgress.Contains(h)) return;
                bool sick = WorldSim.State?.Outbreaks?.Any(o => o.SettlementId == st.StringId || (st.MapFaction != null && Settlement.Find(o.SettlementId)?.MapFaction == st.MapFaction)) == true;
                if ((sick || Diseases.HadShortage(st, 30)) && Rng.NextDouble() < 0.4) __result = h;
            }
            catch { }
        }
    }
}
