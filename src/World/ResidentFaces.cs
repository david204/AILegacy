using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;
using FaceParams = TaleWorlds.MountAndBlade.FaceGenerationParams;
using MBFace = TaleWorlds.MountAndBlade.MBBodyProperties;

namespace AILegacy.World
{
    /// <summary>
    /// Townsfolk the player has talked to keep their face, build, age, clothes and name on later visits.
    /// The game draws a new random face for every townsman each time a place is entered (the seed comes from a random
    /// rank in SimpleAgentOrigin, read once in the LocationCharacter constructor). Here the face of whoever the player
    /// talked to is stored with their memory, and when a place is about to spawn its people it is put back on one
    /// newly made character of the same kind, before the agent exists. Their memory key is built from the face, so
    /// with the face comes the name and everything they remember.
    /// It also decides how everyone else looks (Patches/ResidentLooksPatch.cs): a face of their own for every
    /// townsman (and, if asked, every soldier, guard, mercenary and bandit), and everyday clothes drawn from all the
    /// common folk of their culture. Both come from the seed the game already gives each person, so whoever is
    /// remembered above gets back the same face and the same clothes.
    /// </summary>
    internal static class ResidentFaces
    {
        // Location characters already looked at. They live as long as the visit, so no clearing is needed
        private static readonly ConditionalWeakTable<LocationCharacter, object> Seen = new ConditionalWeakTable<LocationCharacter, object>();
        private static readonly object Mark = new object();

        /// <summary>Same hash Speaker uses for the resident key.</summary>
        public static string Hash(StaticBodyProperties f)
        {
            ulong h = 1469598103934665603UL;
            foreach (ulong k in new[] { f.KeyPart1, f.KeyPart2, f.KeyPart3, f.KeyPart4, f.KeyPart5, f.KeyPart6, f.KeyPart7, f.KeyPart8 })
                h = (h ^ k) * 1099511628211UL;
            return h.ToString("x");
        }

        private static string Pack(BodyProperties b)
        {
            var c = CultureInfo.InvariantCulture;
            return string.Join(";", b.Age.ToString("0.##", c), b.Weight.ToString("0.####", c), b.Build.ToString("0.####", c),
                b.KeyPart1.ToString("x"), b.KeyPart2.ToString("x"), b.KeyPart3.ToString("x"), b.KeyPart4.ToString("x"),
                b.KeyPart5.ToString("x"), b.KeyPart6.ToString("x"), b.KeyPart7.ToString("x"), b.KeyPart8.ToString("x"));
        }

        private static bool Unpack(string s, out BodyProperties b)
        {
            b = default(BodyProperties);
            var p = (s ?? "").Split(';');
            if (p.Length != 11) return false;
            var c = CultureInfo.InvariantCulture;
            if (!float.TryParse(p[0], NumberStyles.Float, c, out float age) || !float.TryParse(p[1], NumberStyles.Float, c, out float weight) || !float.TryParse(p[2], NumberStyles.Float, c, out float build)) return false;
            var k = new ulong[8];
            for (int i = 0; i < 8; i++)
                if (!ulong.TryParse(p[3 + i], NumberStyles.HexNumber, c, out k[i])) return false;
            b = new BodyProperties(new DynamicBodyProperties(age, weight, build), new StaticBodyProperties(k[0], k[1], k[2], k[3], k[4], k[5], k[6], k[7]));
            return true;
        }

        // Everyone met this session. Only those the player really talks to move on to the saved list
        private static readonly Dictionary<string, HeroMemory> Met = new Dictionary<string, HeroMemory>();
        private static WorldState _metFor;

        private static List<HeroMemory> Saved => WorldSim.State?.Residents;

        /// <summary>
        /// Another campaign or save became the live one: nobody met before belongs to it, and its saved list is
        /// cleaned (empty, repeated or unreadable entries out) and cut to the cap, which may have been lowered.
        /// </summary>
        private static void Sync()
        {
            var state = WorldSim.State;
            if (_metFor == state) return;
            _metFor = state;
            Met.Clear();
            var saved = state?.Residents;
            if (saved == null) return;
            var ids = new HashSet<string>();
            int had = saved.Count;
            // The newest copy of a repeated id is the one kept
            var keep = saved.Where(m => m != null && !string.IsNullOrEmpty(m.HeroId) && (string.IsNullOrEmpty(m.Face) || Unpack(m.Face, out _)))
                .OrderByDescending(m => m.LastConversationDay).Where(m => ids.Add(m.HeroId)).Take(Math.Max(1, S.Get.ResidentsRemembered)).ToList();
            if (keep.Count == had) return;
            saved.Clear();
            saved.AddRange(keep);
            Log.Info($"[neighbours] remembered neighbours list sanitized on load: {had} -> {keep.Count}");
        }

        // Child, teenager and adult bodies are not interchangeable. An adult's exact age comes back with the face.
        private static int AgeBand(float age)
        {
            float child = 6f, teen = 14f, adult = 18f;
            try
            {
                var model = Campaign.Current?.Models?.AgeModel;
                if (model != null) { child = model.BecomeChildAge; teen = model.BecomeTeenagerAge; adult = model.HeroComesOfAge; }
            }
            catch { }
            return age < child ? 0 : age < teen ? 1 : age < adult ? 2 : 3;
        }

        private static float SlotAge(LocationCharacter lc) => lc.AgentData.AgentAge > 0 ? lc.AgentData.AgentAge : lc.Character.Age;

        /// <summary>
        /// Called when the mod meets a resident. Brings back what a remembered one knew (resident memory is otherwise
        /// only kept for the session) and notes how a new one looks.
        /// </summary>
        public static void Attach(HeroMemory mem, IAgent agent, Settlement settlement, CharacterObject c)
        {
            try
            {
                if (mem == null || string.IsNullOrEmpty(mem.HeroId) || settlement == null || c == null || c.IsHero || !S.Get.UniqueResidentFaces) return;
                var saved = Saved;
                if (saved == null) return;
                Sync();
                int at = saved.FindIndex(m => m != null && m.HeroId == mem.HeroId);
                if (at >= 0 && !ReferenceEquals(saved[at], mem))
                {
                    // First meeting since loading: the saved copy becomes the live one
                    if (mem.Turns.Count == 0 && mem.ConversationCount == 0)
                        Newtonsoft.Json.JsonConvert.PopulateObject(Newtonsoft.Json.JsonConvert.SerializeObject(saved[at]), mem);
                    mem.IsResident = true;
                    saved[at] = mem;
                }
                if (string.IsNullOrEmpty(mem.Face) && agent is TaleWorlds.MountAndBlade.Agent a && a.IsHuman)
                {
                    mem.Face = Pack(a.BodyPropertiesValue);
                    mem.FaceFemale = a.IsFemale;
                    mem.FaceSettlement = settlement.StringId;
                    mem.FaceCharacter = c.StringId;
                    try
                    {
                        var lc = LocationComplex.Current?.FindCharacter(agent);
                        if (lc != null) mem.FaceSeed = lc.AgentData.AgentEquipmentSeed;
                        mem.FaceLocation = CampaignMission.Current?.Location?.StringId;
                    }
                    catch { }
                }
                Met[mem.HeroId] = mem;
            }
            catch (Exception ex) { Log.Error("Neighbour faces: save", ex); }
        }

        /// <summary>Once a second: residents the player has now talked to join the saved list, the oldest making room.</summary>
        public static void Sweep()
        {
            try
            {
                Sync();
                if (Met.Count == 0) return;
                var saved = Saved;
                if (saved == null) { Met.Clear(); return; }
                bool added = false;
                foreach (var mem in Met.Values)
                {
                    if (mem.ConversationCount <= 0 || string.IsNullOrEmpty(mem.Face) || saved.Contains(mem)) continue;
                    saved.RemoveAll(m => m == null || m.HeroId == mem.HeroId);
                    saved.Add(mem);
                    added = true;
                    Log.Info($"[neighbours] remembering {(string.IsNullOrWhiteSpace(mem.Profile?.GivenName) ? mem.HeroName : mem.Profile.GivenName)} ({mem.HeroId})");
                }
                int cap = Math.Max(1, S.Get.ResidentsRemembered);
                if (added && saved.Count > cap)
                {
                    var keep = saved.OrderByDescending(m => m.LastConversationDay).Take(cap).ToList();
                    saved.Clear();
                    saved.AddRange(keep);
                }
            }
            catch (Exception ex) { Log.Error("Neighbour faces: remember", ex); }
        }

        /// <summary>A resident who became a named hero no longer exists as a resident.</summary>
        public static void Forget(string key)
        {
            try { Met.Remove(key ?? ""); Saved?.RemoveAll(m => m == null || m.HeroId == key); } catch { }
        }

        private static List<HeroMemory> RememberedAt(string settlementId) =>
            (Saved ?? new List<HeroMemory>()).Where(m => m != null && m.FaceSettlement == settlementId && !string.IsNullOrEmpty(m.Face) && !string.IsNullOrEmpty(m.FaceCharacter)).ToList();

        /// <summary>
        /// The game raises this right before it spawns the people of the place the player is entering, after the game's
        /// own behaviours have added them. Whoever is new in that place may get the face of a remembered resident.
        /// </summary>
        public static void OnLocationCharactersReady(Dictionary<string, int> unusedUsablePointCount)
        {
            try
            {
                if (!S.Get.UniqueResidentFaces) return;
                var settlement = Settlement.CurrentSettlement;
                var here = CampaignMission.Current?.Location;
                var complex = LocationComplex.Current;
                if (settlement == null || here == null || complex == null) return;
                Sync();

                var fresh = new List<LocationCharacter>();
                foreach (var lc in here.GetCharacterList().ToList())
                {
                    if (lc?.AgentData == null || lc.Character == null) continue;
                    if (Seen.TryGetValue(lc, out _)) continue;
                    Seen.Add(lc, Mark);
                    if (!lc.Character.IsHero) fresh.Add(lc);
                }
                if (fresh.Count == 0) return;

                var known = RememberedAt(settlement.StringId);
                if (known.Count == 0) return;

                // Faces already walking around this settlement (someone met earlier in the same visit, or a fixed character)
                var present = new HashSet<string>();
                foreach (var lc in complex.GetListOfCharacters())
                    if (lc?.AgentData != null && lc.AgentData.BodyPropertiesOverriden && lc.Character != null && !lc.Character.IsHero)
                        present.Add(Hash(lc.AgentData.AgentBodyProperties.StaticProperties));

                int put = 0;
                // Those last seen in this very place go first, so the tavern regular is found in the tavern
                foreach (var mem in known.OrderByDescending(m => m.FaceLocation == here.StringId).ThenByDescending(m => m.LastConversationDay))
                {
                    if (!Unpack(mem.Face, out var body)) continue;
                    string hash = Hash(body.StaticProperties);
                    if (present.Contains(hash)) continue;
                    // Same kind of character is not enough: the game can give one character either sex or a
                    // child's age, and a face on the wrong body is a different person
                    int band = AgeBand(body.Age);
                    var slot = fresh.FirstOrDefault(lc => lc.Character.StringId == mem.FaceCharacter
                        && (mem.FaceFemale == null || lc.AgentData.AgentIsFemale == mem.FaceFemale.Value)
                        && AgeBand(SlotAge(lc)) == band);
                    if (slot == null) continue;
                    fresh.Remove(slot);
                    if (mem.FaceFemale == null) mem.FaceFemale = slot.AgentData.AgentIsFemale;
                    slot.AgentData.BodyProperties(body).Age((int)Math.Round(body.Age));
                    if (mem.FaceSeed != 0) slot.AgentData.EquipmentSeed(mem.FaceSeed);
                    present.Add(hash);
                    put++;
                }
                if (put > 0) Log.Info($"[neighbours] {settlement.Name} ({here.StringId}): {put} acquaintance(s) restored with their face");
            }
            catch (Exception ex) { Log.Error("Neighbour faces: apply", ex); }
        }

        // ===================================================================== how everyone else looks
        // As in AIInfluence 7.0: the game gives every townsman one of a handful of template faces and outfits. Here each
        // person gets a face of their own, worked out from the template and the seed the game picked for them (a face
        // type, a few marked features, height and build, skin, hair and eye colour within their people's range, a
        // hairstyle and beard their culture wears, now and then a scar), and townsfolk dress from the everyday clothes
        // of all the common folk of their culture. Lords, companions and notables keep their own.

        private const int MaxLookFailures = 5;
        private static int _faceFailures, _clothesFailures;
        private static bool _faceLogged, _clothesLogged;
        private static readonly object LooksGate = new object();
        private static Campaign _looksFor;

        private static readonly HashSet<Occupation> FaceResidents = new HashSet<Occupation>
        {
            Occupation.Villager, Occupation.Townsfolk, Occupation.TavernWench, Occupation.Musician, Occupation.ShopWorker,
            Occupation.Tavernkeeper, Occupation.GoodsTrader, Occupation.Weaponsmith, Occupation.Armorer, Occupation.HorseTrader,
            Occupation.Blacksmith, Occupation.TavernGameHost, Occupation.RansomBroker, Occupation.ArenaMaster, Occupation.ShipWright,
            Occupation.Artisan,
        };
        private static readonly HashSet<Occupation> FaceFighters = new HashSet<Occupation>
        {
            Occupation.Soldier, Occupation.Guard, Occupation.PrisonGuard, Occupation.Mercenary, Occupation.CaravanGuard,
            Occupation.Bandit, Occupation.Gangster, Occupation.BannerBearer,
        };
        // Who gets mixed clothes, and whose everyday clothes go into the mix (only the lowest soldiers: their off-duty wear)
        private static readonly HashSet<Occupation> ClothesWearers = new HashSet<Occupation> { Occupation.Townsfolk, Occupation.Villager };
        private static readonly HashSet<Occupation> ClothesSources = new HashSet<Occupation>
        {
            Occupation.Townsfolk, Occupation.Villager, Occupation.TavernWench, Occupation.ShopWorker, Occupation.Musician, Occupation.Soldier,
        };
        // Special characters whose look belongs to a scene or a story
        private static readonly string[] ClothesSkip = { "dancer", "test", "crazy", "facgen", "notary", "cutscene", "disguise", "player_char", "bodyguard" };
        // Hats stay as they are
        private static readonly EquipmentIndex[] ClothesSlots = { EquipmentIndex.Body, EquipmentIndex.Leg, EquipmentIndex.Cape, EquipmentIndex.Gloves };

        private sealed class KeyMap
        {
            public readonly List<int> All = new List<int>();
            public readonly HashSet<int> Subtle = new HashSet<int>();      // asymmetry and ears: only a touch
            public readonly Dictionary<string, int> ByName = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        }
        private struct ColourRange { public float SkinLo, SkinHi, HairLo, HairHi, EyeLo, EyeHi; }
        private sealed class HairPool { public readonly List<string> Hair = new List<string>(), Beard = new List<string>(); }

        private static readonly Dictionary<(string, int, int, int, int), BodyProperties> FaceCache = new Dictionary<(string, int, int, int, int), BodyProperties>();
        private static readonly Dictionary<(int, int, int), KeyMap> KeyMaps = new Dictionary<(int, int, int), KeyMap>();
        private static readonly Dictionary<string, ColourRange> Colours = new Dictionary<string, ColourRange>();
        private static readonly Dictionary<(string, bool, int, bool), HairPool> HairPools = new Dictionary<(string, bool, int, bool), HairPool>();
        private static readonly Dictionary<(int, int, int, string, int), int[]> TagIndices = new Dictionary<(int, int, int, string, int), int[]>();
        private static readonly Dictionary<(string, bool, bool), Dictionary<EquipmentIndex, List<ItemObject>>> ClothesPools = new Dictionary<(string, bool, bool), Dictionary<EquipmentIndex, List<ItemObject>>>();
        private static readonly Dictionary<string, Dictionary<EquipmentIndex, int>> ClothesCaps = new Dictionary<string, Dictionary<EquipmentIndex, int>>();

        /// <summary>Face types: a few features pulled together towards one look. Opposite looks touch the same features.</summary>
        private static readonly (string Id, float Target)[][] FaceTypes =
        {
            new[] { ("face_width", 0.85f), ("cheekbone_width", 0.75f), ("temple_width", 0.7f), ("face_ratio", 0.3f), ("cheeks", 0.65f) },          // broad
            new[] { ("face_width", 0.15f), ("cheekbone_width", 0.25f), ("temple_width", 0.3f), ("face_ratio", 0.7f), ("cheeks", 0.25f) },          // narrow
            new[] { ("face_ratio", 0.85f), ("chin_length", 0.75f), ("nose_length", 0.7f), ("center_height", 0.65f) },                            // long
            new[] { ("face_ratio", 0.15f), ("chin_length", 0.25f), ("nose_length", 0.3f), ("center_height", 0.35f) },                            // short
            new[] { ("jaw_line", 0.8f), ("jaw_height", 0.75f), ("chin_shape", 0.75f), ("chin_forward", 0.7f), ("neck_slope", 0.7f) },            // strong jaw
            new[] { ("chin_forward", 0.15f), ("chin_length", 0.3f), ("jaw_line", 0.25f), ("jaw_height", 0.3f), ("chin_shape", 0.25f) },         // soft jaw
            new[] { ("cheekbone_height", 0.8f), ("cheekbone_depth", 0.8f), ("cheeks", 0.15f), ("face_sharpness", 0.8f), ("eye_socket_size", 0.75f) }, // gaunt
            new[] { ("cheeks", 0.85f), ("face_sharpness", 0.2f), ("cheekbone_depth", 0.3f), ("face_width", 0.65f), ("jaw_line", 0.35f) },       // round
            new[] { ("nose_bump", 0.8f), ("nose_size", 0.75f), ("nose_angle", 0.7f), ("nose_definition", 0.75f), ("eye_depth", 0.7f) },         // hooked nose
            new[] { ("nose_bridge", 0.2f), ("nose_size", 0.35f), ("nose_width", 0.8f), ("nostril_scale", 0.7f), ("face_depth", 0.3f) },        // flat nose
            new[] { ("eye_size", 0.8f), ("eyelid_height", 0.7f), ("eye_to_eye_distance", 0.65f), ("lip_thickness", 0.25f) },                   // wide-eyed
            new[] { ("eye_size", 0.2f), ("monolid_eyes", 0.65f), ("eyelid_height", 0.3f), ("lip_thickness", 0.8f), ("mouth_width", 0.65f) },    // heavy-lidded
        };

        /// <summary>Another campaign: what was worked out for the last one may not hold (other mods, other characters).</summary>
        private static void LooksSync()
        {
            var c = Campaign.Current;
            if (_looksFor == c) return;
            _looksFor = c;
            FaceCache.Clear(); KeyMaps.Clear(); Colours.Clear(); HairPools.Clear(); TagIndices.Clear(); ClothesPools.Clear(); ClothesCaps.Clear();
        }

        // stable across sessions, unlike string.GetHashCode
        private static int Stable(string s) { unchecked { int h = 17; foreach (char ch in s ?? "") h = h * 31 + ch; return h; } }
        private static int Mix(int a, int b) { unchecked { uint h = (uint)a * 2654435761u ^ (uint)b * 40503u; h ^= h >> 15; h *= 2246822519u; h ^= h >> 13; return (int)(h & 0x7FFFFFFF); } }
        private static float Clamp(float x, float lo, float hi) => x < lo ? lo : x > hi ? hi : x;
        private static float Clamp01(float x) => Clamp(x, 0f, 1f);
        private static float Gauss(Random r) { double u = 1.0 - r.NextDouble(), v = r.NextDouble(); return (float)(Math.Sqrt(-2.0 * Math.Log(u)) * Math.Cos(2.0 * Math.PI * v)); }

        /// <summary>This person gets a face of their own. <paramref name="fighter"/>: a soldier, guard, mercenary or bandit.</summary>
        public static bool WantsFace(CharacterObject c, out bool fighter)
        {
            fighter = false;
            if (c == null || c.IsHero || _faceFailures >= MaxLookFailures || Campaign.Current == null) return false;
            var cfg = S.Get;
            if (cfg == null || !cfg.ResidentFaceGeneration) return false;
            var occ = c.Occupation;
            if (FaceResidents.Contains(occ)) return true;
            if (!FaceFighters.Contains(occ)) return false;
            fighter = true;
            return cfg.UniqueFacesForFighters;
        }

        /// <summary>
        /// The face the game drew for this template and seed, made into someone's own. Same seed, same face: the
        /// result is kept, and it only changes with the variety setting.
        /// </summary>
        public static BodyProperties UniqueFace(CharacterObject c, Equipment eq, int seed, BodyProperties plain, bool fighter)
        {
            try
            {
                float v = Clamp01(S.Get.ResidentFaceVariety);
                if (v <= 0.001f) return plain;
                if (seed == -1) seed = Stable(c.StringId);
                int hairCover = (int)(eq?.HairCoverType ?? ArmorComponent.HairCoverTypes.None);
                int beardCover = (int)(eq?.BeardCoverType ?? ArmorComponent.BeardCoverTypes.None);
                var key = (c.StringId, seed, hairCover, beardCover, (int)Math.Round(v * 100f));
                lock (LooksGate)
                {
                    LooksSync();
                    if (FaceCache.TryGetValue(key, out var known)) return known;
                }
                var made = MakeFace(c, eq, seed, plain, v, fighter);
                lock (LooksGate)
                {
                    if (FaceCache.Count >= 4096) FaceCache.Clear();
                    FaceCache[key] = made;
                }
                if (!_faceLogged) { _faceLogged = true; Log.Info($"[looks] first face of their own made for {c.StringId} (seed {seed}, variety {v:0.00})"); }
                return made;
            }
            catch (Exception ex)
            {
                _faceFailures++;
                Log.Error($"[looks] own face for {c?.StringId} failed ({_faceFailures}/{MaxLookFailures})", ex);
                return plain;
            }
        }

        private static FaceParams ParamsOf(BodyProperties b, int race, int gender, float age, bool earsHidden, bool mouthHidden)
        {
            var p = FaceParams.Create();
            p.CurrentRace = race; p.CurrentGender = gender; p.CurrentAge = age;
            MBFace.GetParamsFromKey(ref p, b, earsHidden, mouthHidden);
            p.SetRaceGenderAndAdjustParams(race, gender, (int)age);
            return p;
        }

        private static BodyProperties MakeFace(CharacterObject c, Equipment eq, int seed, BodyProperties plain, float v, bool fighter)
        {
            float age = plain.Age;
            var maturity = FaceGen.GetMaturityTypeWithAge(age);
            bool adult = maturity >= BodyMeshMaturityType.Adult;
            if (!adult) v *= 0.6f;      // children always get a milder version
            int race = c.Race, gender = c.IsFemale ? 1 : 0;
            bool earsHidden = eq != null && eq.EarsAreHidden, mouthHidden = eq != null && eq.MouthIsHidden;
            var rng = new Random(Mix(seed, Stable(c.StringId)));
            var p = ParamsOf(plain, race, gender, age, earsHidden, mouthHidden);

            Shape(ref p, KeysFor(race, gender, age, maturity), rng, v);
            Colour(ref p, ColoursFor(c, race, gender, age), rng, v);
            p.HeightMultiplier = Clamp(p.HeightMultiplier + Gauss(rng) * 0.15f * v, 0.1f, 0.9f);

            int hairNum = 0, beardNum = 0, faceTexNum = 0, mouthTexNum = 0, tattooNum = 0, soundNum = 0, eyebrowNum = 0;
            float scale = 0f;
            MBFace.GetParamsMax(race, gender, (int)age, ref hairNum, ref beardNum, ref faceTexNum, ref mouthTexNum, ref tattooNum, ref soundNum, ref eyebrowNum, ref scale);
            var pool = HairPoolFor(c, maturity, fighter);
            // a hairstyle of their people, only where nothing covers the head
            if (pool != null && (eq == null || eq.HairCoverType == ArmorComponent.HairCoverTypes.None) && rng.NextDouble() < v)
            {
                int h = PickByTag(pool.Hair, rng, race, gender, age, maturity, 0);
                if (h >= 0 && h < hairNum) p.CurrentHair = h;
            }
            p.IsHairFlipped = rng.Next(2) == 0;
            if (pool != null && adult && gender == 0 && (eq == null || eq.BeardCoverType == ArmorComponent.BeardCoverTypes.None) && rng.NextDouble() < v)
            {
                int b = PickByTag(pool.Beard, rng, race, gender, age, maturity, 1);
                if (b >= 0 && b < beardNum) p.CurrentBeard = b;
            }
            if (eyebrowNum > 1 && rng.NextDouble() < v) p.CurrentEyebrow = rng.Next(eyebrowNum);
            if (faceTexNum > 1 && rng.NextDouble() < v * 0.5) p.CurrentFaceTexture = rng.Next(faceTexNum);
            if (mouthTexNum > 1 && rng.NextDouble() < v * 0.5) p.CurrentMouthTexture = rng.Next(mouthTexNum);
            // now and then a scar: often on those who fight for a living, seldom on others, and on women less
            if (adult && p.CurrentFaceTattoo <= 0 && tattooNum > 1)
            {
                double chance = v * (fighter ? 0.35 : 0.1) * (gender == 1 ? 0.4 : 1.0);
                if (rng.NextDouble() < chance)
                {
                    var scars = Indices(race, gender, age, maturity, "scar", 2);
                    if (scars.Length > 0) { int s = scars[rng.Next(scars.Length)]; if (s > 0 && s < tattooNum) p.CurrentFaceTattoo = s; }
                }
            }
            MBFace.EnforceConstraints(ref p);
            var body = plain;
            MBFace.ProduceNumericKeyWithParams(p, earsHidden, mouthHidden, ref body);
            float weight = Clamp(plain.Weight + Gauss(rng) * 0.22f * v, 0.05f, 0.95f);
            float build = Clamp(plain.Build + Gauss(rng) * 0.2f * v, 0.05f, 0.95f);
            return new BodyProperties(new DynamicBodyProperties(plain.Age, weight, build), body.StaticProperties);
        }

        /// <summary>The face's shape keys (not height, build, weight or age, which have their own fields).</summary>
        private static KeyMap KeysFor(int race, int gender, float age, BodyMeshMaturityType maturity)
        {
            var key = (race, gender, (int)maturity);
            lock (LooksGate) { if (KeyMaps.TryGetValue(key, out var known)) return known; }
            var map = new KeyMap();
            int n = MBFace.GetNumEditableDeformKeys(race, gender == 1, (int)age);
            for (int i = 0; i < n; i++)
            {
                var d = MBFace.GetDeformKeyData(i, race, gender, (int)age);
                string id = d.Id ?? "";
                if (d.GroupId < 0 || id == "height" || id == "build" || id == "weight" || id == "age") continue;
                map.All.Add(i);
                if (id.IndexOf("asym", StringComparison.OrdinalIgnoreCase) >= 0 || id.StartsWith("ear_", StringComparison.OrdinalIgnoreCase)) map.Subtle.Add(i);
                else if (id.Length > 0) map.ByName[id] = i;
            }
            lock (LooksGate) KeyMaps[key] = map;
            return map;
        }

        private static void Shape(ref FaceParams p, KeyMap map, Random rng, float v)
        {
            var w = p.KeyWeights;
            if (w == null || map == null || map.All.Count == 0) return;
            // every feature a little off the template
            foreach (int i in map.All)
                if (i < w.Length) w[i] = Clamp01(w[i] + Gauss(rng) * (map.Subtle.Contains(i) ? 0.03f : 0.09f) * v);
            // a face type, and sometimes a second one that touches other features
            int a = rng.Next(FaceTypes.Length);
            PullTowards(w, map, FaceTypes[a], rng, v);
            if (rng.NextDouble() < 0.4)
            {
                int b = rng.Next(FaceTypes.Length);
                if (b != a && !FaceTypes[a].Any(x => FaceTypes[b].Any(y => y.Id == x.Id))) PullTowards(w, map, FaceTypes[b], rng, v);
            }
            // one to three features that stand out
            var named = map.ByName.Values.Where(i => i < w.Length).ToList();
            int marks = 1 + (int)Math.Round(2.0 * v * rng.NextDouble());
            for (int k = 0; k < marks && named.Count > 0; k++)
            {
                int i = named[rng.Next(named.Count)];
                float target = w[i] >= 0.5f ? 0.82f + 0.12f * (float)rng.NextDouble() : 0.18f - 0.12f * (float)rng.NextDouble();
                if (rng.NextDouble() < 0.3) target = 1f - target;
                w[i] = Clamp01(w[i] + (target - w[i]) * (0.25f + 0.35f * v));
            }
        }

        private static void PullTowards(float[] w, KeyMap map, (string Id, float Target)[] type, Random rng, float v)
        {
            float strength = 0.35f + 0.45f * v;
            foreach (var (id, target) in type)
            {
                if (!map.ByName.TryGetValue(id, out int i) || i >= w.Length) continue;
                float t = Clamp01(target + (float)(rng.NextDouble() - 0.5) * 0.14f);
                w[i] = Clamp01(w[i] + (t - w[i]) * strength);
            }
        }

        /// <summary>Skin, hair and eye colour of this template's people: the span between its lightest and darkest.</summary>
        private static ColourRange ColoursFor(CharacterObject c, int race, int gender, float age)
        {
            lock (LooksGate) { if (Colours.TryGetValue(c.StringId, out var known)) return known; }
            var lo = ParamsOf(c.GetBodyPropertiesMin(), race, gender, age, false, false);
            var hi = ParamsOf(c.GetBodyPropertiesMax(), race, gender, age, false, false);
            var r = new ColourRange
            {
                SkinLo = Math.Min(lo.CurrentSkinColorOffset, hi.CurrentSkinColorOffset), SkinHi = Math.Max(lo.CurrentSkinColorOffset, hi.CurrentSkinColorOffset),
                HairLo = Math.Min(lo.CurrentHairColorOffset, hi.CurrentHairColorOffset), HairHi = Math.Max(lo.CurrentHairColorOffset, hi.CurrentHairColorOffset),
                EyeLo = Math.Min(lo.CurrentEyeColorOffset, hi.CurrentEyeColorOffset), EyeHi = Math.Max(lo.CurrentEyeColorOffset, hi.CurrentEyeColorOffset),
            };
            lock (LooksGate) Colours[c.StringId] = r;
            return r;
        }

        private static void Colour(ref FaceParams p, ColourRange r, Random rng, float v)
        {
            p.CurrentSkinColorOffset = InRange(p.CurrentSkinColorOffset + Gauss(rng) * 0.1f * v, r.SkinLo, r.SkinHi, v);
            p.CurrentHairColorOffset = InRange(p.CurrentHairColorOffset + Gauss(rng) * 0.25f * v, r.HairLo, r.HairHi, v);
            p.CurrentEyeColorOffset = InRange(p.CurrentEyeColorOffset + Gauss(rng) * 0.3f * v, r.EyeLo, r.EyeHi, v);
        }

        // A colour may go a little past its people's range, more with more variety
        private static float InRange(float x, float lo, float hi, float v)
        {
            float slack = Math.Max(0.03f, (hi - lo) * 0.35f) * v;
            return Clamp(x, Math.Max(0f, lo - slack), Math.Min(1f, hi + slack));
        }

        /// <summary>Hairstyle and beard tags worn by the common folk (or the fighters) of this culture, sex and age.</summary>
        private static HairPool HairPoolFor(CharacterObject c, BodyMeshMaturityType maturity, bool fighter)
        {
            string culture = c.Culture?.StringId;
            if (string.IsNullOrEmpty(culture)) return null;
            var key = (culture, c.IsFemale, (int)maturity, fighter);
            lock (LooksGate) { if (HairPools.TryGetValue(key, out var known)) return known; }
            var pool = new HairPool();
            var kinds = fighter ? FaceFighters : FaceResidents;
            foreach (var other in CharacterObject.All)
            {
                if (other == null || other.IsHero || other.IsFemale != c.IsFemale || other.Culture?.StringId != culture || !kinds.Contains(other.Occupation)) continue;
                if (FaceGen.GetMaturityTypeWithAge(other.Age) != maturity) continue;
                var range = other.BodyPropertyRange;
                if (range == null) continue;
                AddTags(pool.Hair, range.HairTags);
                AddTags(pool.Beard, range.BeardTags);
            }
            lock (LooksGate) HairPools[key] = pool;
            return pool;
        }

        private static void AddTags(List<string> into, string tags)
        {
            if (string.IsNullOrWhiteSpace(tags)) return;
            foreach (var t in tags.Split(','))
            {
                var s = t.Trim();
                if (s.Length > 0) into.Add(s);      // repeated tags weigh more, as they are worn more
            }
        }

        /// <summary>What = 0 hair, 1 beard, 2 tattoo (scars).</summary>
        private static int[] Indices(int race, int gender, float age, BodyMeshMaturityType maturity, string tag, int what)
        {
            var key = (race, gender, (int)maturity, tag, what);
            lock (LooksGate) { if (TagIndices.TryGetValue(key, out var known)) return known; }
            int[] found = (what == 0 ? FaceGen.GetHairIndicesByTag(race, gender, age, tag)
                         : what == 1 ? FaceGen.GetFacialIndicesByTag(race, gender, age, tag)
                         : FaceGen.GetTattooIndicesByTag(race, gender, age, tag)) ?? new int[0];
            lock (LooksGate) TagIndices[key] = found;
            return found;
        }

        private static int PickByTag(List<string> tags, Random rng, int race, int gender, float age, BodyMeshMaturityType maturity, int what)
        {
            if (tags == null || tags.Count == 0) return -1;
            for (int tries = 0; tries < 3; tries++)
            {
                var found = Indices(race, gender, age, maturity, tags[rng.Next(tags.Count)], what);
                if (found.Length > 0) return found[rng.Next(found.Length)];
            }
            return -1;
        }

        // ---- everyday clothes

        /// <summary>
        /// A townsman's or villager's civilian outfit, each piece (tunic, trousers, cloak, gloves) most of the time swapped
        /// for one worn by any common person of their culture, sex and age. Nothing much richer than what their own
        /// template wears. With the game's seed the same person always gets the same clothes.
        /// </summary>
        public static void MixClothes(BasicCharacterObject character, Equipment.EquipmentType type, int seed, Equipment result)
        {
            if (type != Equipment.EquipmentType.Civilian || result == null) return;
            var c = character as CharacterObject;
            try
            {
                if (c == null || c.IsHero || Campaign.Current == null || _clothesFailures >= MaxLookFailures) return;
                var cfg = S.Get;
                if (cfg == null || !cfg.UniqueResidentClothes) return;
                string id = c.StringId ?? "";
                if (!ClothesWearers.Contains(c.Occupation) || c.Age < 5f || Skipped(id) || id.IndexOf("beggar", StringComparison.OrdinalIgnoreCase) >= 0) return;
                var pool = ClothesPoolFor(c);
                var caps = ClothesCapsFor(c);
                if (pool == null || caps == null) return;
                var rng = new Random(seed == -1 ? MBRandom.RandomInt(int.MaxValue) : Mix(seed, Stable(id)));
                int changed = 0;
                foreach (var slot in ClothesSlots)
                {
                    if (result[slot].IsEmpty || !caps.TryGetValue(slot, out int cap) || !pool.TryGetValue(slot, out var items) || items.Count == 0) continue;
                    if (rng.NextDouble() >= 0.75) continue;
                    for (int t = 0; t < 4; t++)
                    {
                        var item = items[rng.Next(items.Count)];
                        if (item.Value > cap) continue;
                        result[slot] = new EquipmentElement(item);
                        changed++;
                        break;
                    }
                }
                if (changed > 0 && !_clothesLogged)
                {
                    _clothesLogged = true;
                    Log.Info($"[looks] first mixed outfit for {id}: {changed} piece(s) from a pool of {pool.Values.Sum(l => l.Count)}");
                }
            }
            catch (Exception ex)
            {
                _clothesFailures++;
                Log.Error($"[looks] mixed clothes for {c?.StringId} failed ({_clothesFailures}/{MaxLookFailures})", ex);
            }
        }

        private static bool Skipped(string id) => ClothesSkip.Any(s => id.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0);

        private static bool Grown(BasicCharacterObject c) => FaceGen.GetMaturityTypeWithAge(c.Age) >= BodyMeshMaturityType.Adult;

        /// <summary>Every civilian piece worn by the common folk of this culture, sex and age, slot by slot.</summary>
        private static Dictionary<EquipmentIndex, List<ItemObject>> ClothesPoolFor(CharacterObject c)
        {
            string culture = c.Culture?.StringId;
            if (string.IsNullOrEmpty(culture)) return null;
            bool grown = Grown(c);
            var key = (culture, c.IsFemale, grown);
            lock (LooksGate)
            {
                LooksSync();
                if (ClothesPools.TryGetValue(key, out var known)) return known;
            }
            var pool = new Dictionary<EquipmentIndex, List<ItemObject>>();
            foreach (var other in CharacterObject.All)
            {
                if (other == null || other.IsHero || other.IsFemale != c.IsFemale || other.Culture?.StringId != culture) continue;
                if (!ClothesSources.Contains(other.Occupation) || other.Age < 5f || Grown(other) != grown || Skipped(other.StringId ?? "")) continue;
                if (other.Occupation == Occupation.Soldier && other.Tier > 3) continue;
                foreach (var eq in other.CivilianEquipments)
                {
                    if (eq == null) continue;
                    foreach (var slot in ClothesSlots)
                    {
                        var item = eq[slot].Item;
                        if (item == null || item.ArmorComponent == null || !item.IsCivilian) continue;
                        if (!pool.TryGetValue(slot, out var list)) pool[slot] = list = new List<ItemObject>();
                        list.Add(item);      // a piece many wear comes up more often
                    }
                }
            }
            lock (LooksGate) ClothesPools[key] = pool;
            return pool;
        }

        /// <summary>The dearest piece this template wears in each slot, half as much again: the most a swapped piece may cost.</summary>
        private static Dictionary<EquipmentIndex, int> ClothesCapsFor(CharacterObject c)
        {
            lock (LooksGate)
            {
                LooksSync();
                if (ClothesCaps.TryGetValue(c.StringId, out var known)) return known;
            }
            var caps = new Dictionary<EquipmentIndex, int>();
            foreach (var eq in c.CivilianEquipments)
            {
                if (eq == null) continue;
                foreach (var slot in ClothesSlots)
                {
                    var item = eq[slot].Item;
                    if (item == null) continue;
                    caps.TryGetValue(slot, out int top);
                    caps[slot] = Math.Max(top, item.Value);
                }
            }
            foreach (var slot in caps.Keys.ToList()) caps[slot] = (int)Math.Ceiling(caps[slot] * 1.5f);
            lock (LooksGate) ClothesCaps[c.StringId] = caps;
            return caps;
        }
    }
}
