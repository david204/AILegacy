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
        /// <summary>Probabilidad 0-100 de que un personaje que encaja lo sepa.</summary>
        [JsonProperty("chance")] public int Chance = 50;
        /// <summary>Quién puede saberlo: all, lords, faction_leaders, clan_leaders, companions, wanderers, merchants, gang_leaders,
        /// town_notables, village_notables, townsfolk, villagers, guards, culture:&lt;id&gt;, kingdom:&lt;id&gt;, o el string_id de un héroe.</summary>
        [JsonProperty("who")] public List<string> Who = new List<string>();
        /// <summary>Solo secretos: low / medium / high (cuánta confianza hace falta para contarlo).</summary>
        [JsonProperty("access")] public string Access = "medium";
    }

    /// <summary>
    /// Conocimiento editable del mundo: Modules\AILegacy\world_data\info.json (lo que se sabe) y secrets.json (secretos).
    /// A cada personaje se le tira el dado una vez; lo que le toque lo sabe para siempre.
    /// </summary>
    internal static class Knowledge
    {
        private static readonly FileCache Info = new FileCache("info.json");
        private static readonly FileCache Secrets = new FileCache("secrets.json");
        private static readonly Random Rng = new Random();

        public static void EnsureRolled(Speaker s)
        {
            var m = s.Memory;
            // Las entradas nuevas añadidas a los archivos también se tiran para quien ya existía
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
                bool fits = e.Who == null || e.Who.Count == 0 || e.Who.Any(tags.Contains);
                if (!fits) continue;
                if (Rng.Next(100) < e.Chance) { known.Add(e.Id); Log.Info($"{who} sabe [{e.Id}]"); }
                else known.Add("-" + e.Id); // tirada fallida: no se vuelve a tirar
            }
        }

        public static List<string> InfoFor(Speaker s) =>
            s.Memory.KnownInfoIds.Select(id => Info.Entries.FirstOrDefault(e => e.Id == id)).Where(e => e != null)
             .Select(e => e.Text.Replace("{character}", s.Name)).ToList();

        public static List<string> SecretsFor(Speaker s)
        {
            // Primero los nacidos de la historia de esta partida; los de fábrica (world_data\secrets.json), solo si están activados
            var list = StorySecrets.For(s);
            if (Settings.S.Get.PresetSecrets)
                list.AddRange(s.Memory.KnownSecretIds.Select(id => Secrets.Entries.FirstOrDefault(e => e.Id == id)).Where(e => e != null)
                    .Select(e => $"{e.Text.Replace("{character}", s.Name)} (secret, trust needed to share it: {e.Access})"));
            return list;
        }

        private class FileCache
        {
            private readonly string _name;
            private List<KnowledgeEntry> _entries = new List<KnowledgeEntry>();
            private DateTime _stamp = DateTime.MinValue;

            public FileCache(string name) { _name = name; }

            public List<KnowledgeEntry> Entries
            {
                get
                {
                    var path = Path.Combine(Paths.ModuleDir, "world_data", _name);
                    try
                    {
                        if (!File.Exists(path)) return _entries;
                        var st = File.GetLastWriteTimeUtc(path);
                        if (st != _stamp)
                        {
                            _entries = JsonConvert.DeserializeObject<List<KnowledgeEntry>>(File.ReadAllText(path)) ?? new List<KnowledgeEntry>();
                            _stamp = st;
                            Log.Info($"world_data/{_name}: {_entries.Count} entradas");
                        }
                    }
                    catch (Exception ex) { Log.Error($"world_data/{_name} tiene un error", ex); }
                    return _entries;
                }
            }
        }
    }
}
