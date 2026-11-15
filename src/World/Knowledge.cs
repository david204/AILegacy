using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using Newtonsoft.Json;

namespace AILegacy.World
{
    public class KnowledgeEntry
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("text")] public string Text;
        /// <summary>Chance (0-100) that a matching character knows it.</summary>
        [JsonProperty("chance")] public int Chance = 50;
        /// <summary>Who can know it: all, lords, faction_leaders, clan_leaders, companions, wanderers, merchants, gang_leaders,
        /// town_notables, village_notables, townsfolk, villagers, guards, culture:&lt;id&gt;, kingdom:&lt;id&gt;, or a hero's string_id.</summary>
        [JsonProperty("who")] public List<string> Who = new List<string>();
        /// <summary>Secrets only: low / medium / high, how much trust it takes to share it.</summary>
        [JsonProperty("access")] public string Access = "medium";
        /// <summary>Hero string_ids this entry is about. They know it for certain, whatever the chance says.</summary>
        [JsonProperty("about")] public List<string> About = new List<string>();
    }

    /// <summary>
    /// Editable world knowledge: Modules\AILegacy\world_data\info.json (common knowledge) and secrets.json,
    /// or the campaign's own copies when it has them (see WorldFiles).
    /// Each character rolls once per entry, and whatever they get they know for good.
    /// </summary>
    internal static class Knowledge
    {
        private static readonly FileCache Info = new FileCache("info.json");
        private static readonly FileCache Secrets = new FileCache("secrets.json");
        private static readonly Random Rng = new Random();

        /// <summary>
        /// Drops the cached facts and secrets. The editor calls it after it saves, resets or copies a world file:
        /// the file's date alone can miss a change (a copy keeps the date of the original).
        /// </summary>
        public static void Reload() { Info.Forget(); Secrets.Forget(); }

        public static void EnsureRolled(Speaker s)
        {
            var m = s.Memory;
            // Entries added to the files later also get rolled for characters that already exist
            var tags = s.Tags;
            Roll(Info.Entries, m.KnownInfoIds, tags, s.Name);
            if (Settings.S.Get.PresetSecrets) Roll(Secrets.Entries, m.KnownSecretIds, tags, s.Name);
            m.KnowledgeRolled = true;
        }

        private static void Roll(List<KnowledgeEntry> entries, List<string> known, HashSet<string> tags, string who)
        {
            foreach (var e in entries)
            {
                if (string.IsNullOrWhiteSpace(e.Id) || known.Contains(e.Id) || known.Contains("-" + e.Id)) continue;
                bool own = e.About != null && e.About.Any(tags.Contains);   // tags carry the hero's own id
                bool fits = own || e.Who == null || e.Who.Count == 0 || e.Who.Any(tags.Contains);
                if (!fits) continue;
                if (own || Rng.Next(100) < e.Chance) { known.Add(e.Id); Log.Info($"{who} knows [{e.Id}]"); }
                else known.Add("-" + e.Id); // failed roll, never rolled again
            }
        }

        public static List<string> InfoFor(Speaker s) =>
            s.Memory.KnownInfoIds.Select(id => Info.Entries.FirstOrDefault(e => e.Id == id)).Where(e => e != null)
             .Select(e => e.Text.Replace("{character}", s.Name)).ToList();

        public static List<string> SecretsFor(Speaker s)
        {
            // Secrets from this campaign's story first; preset ones (world_data\secrets.json) only if enabled
            var list = StorySecrets.For(s);
            if (Settings.S.Get.PresetSecrets)
                list.AddRange(s.Memory.KnownSecretIds.Select(id => Secrets.Entries.FirstOrDefault(e => e.Id == id)).Where(e => e != null)
                    .Select(e => $"{e.Text.Replace("{character}", s.Name)} (secret, {Dialogue.Bonds.ShareRule(s, e.Access)})"));
            return list;
        }

        private class FileCache
        {
            private readonly string _name;
            private List<KnowledgeEntry> _entries = new List<KnowledgeEntry>();
            private DateTime _stamp = DateTime.MinValue;
            private string _path;

            public FileCache(string name) { _name = name; }

            /// <summary>Forgets what was read, so the next use reads the file again whatever its date says.</summary>
            public void Forget() { _entries = new List<KnowledgeEntry>(); _stamp = DateTime.MinValue; _path = null; }

            public List<KnowledgeEntry> Entries
            {
                get
                {
                    var path = WorldFiles.Resolve(_name);
                    try
                    {
                        if (!File.Exists(path)) return _entries;
                        var st = File.GetLastWriteTimeUtc(path);
                        if (st != _stamp || path != _path)
                        {
                            _entries = JsonConvert.DeserializeObject<List<KnowledgeEntry>>(File.ReadAllText(path)) ?? new List<KnowledgeEntry>();
                            _stamp = st; _path = path;
                            Log.Info($"world_data/{_name}: {_entries.Count} entries");
                        }
                    }
                    catch (Exception ex) { Log.Error($"world_data/{_name} has an error", ex); }
                    return _entries;
                }
            }
        }
    }
}
