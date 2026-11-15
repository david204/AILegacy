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

namespace AILegacy.World
{
    /// <summary>
    /// Townsfolk the player has talked to keep their face, build, age, clothes and name on later visits.
    /// The game draws a new random face for every townsman each time a place is entered (the seed comes from a random
    /// rank in SimpleAgentOrigin, read once in the LocationCharacter constructor). Here the face of whoever the player
    /// talked to is stored with their memory, and when a place is about to spawn its people it is put back on one
    /// newly made character of the same kind, before the agent exists. Their memory key is built from the face, so
    /// with the face comes the name and everything they remember.
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
    }
}
