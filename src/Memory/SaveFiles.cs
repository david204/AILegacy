using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.World;
using Newtonsoft.Json;

namespace AILegacy.Memory
{
    /// <summary>
    /// Datos de AI Legacy en archivos, por campaña: Modules\AILegacy\save_data\[Id de campaña]\
    ///   Nombre (id_del_héroe).json   → un archivo por personaje (memoria, personalidad, confianza, romance…)
    ///   ailegacy_news.json           → noticias y rumores
    ///   ailegacy_letters.json        → cartas en camino
    ///   ailegacy_orders.json         → órdenes en el mapa
    ///   ailegacy_quests.json         → encargos
    ///   ailegacy_items.json          → documentos y objetos de rol
    ///   ailegacy_campaign.json       → datos sueltos de la campaña
    ///   _escritos.json               → huella de lo que escribió el mod (para saber qué has editado tú a mano)
    /// Se escriben al momento (tras cada respuesta, carta, orden…) y al guardar la partida.
    /// Al cargar una partida manda la copia que va dentro de ella,
    /// salvo los archivos que hayas editado a mano, que se respetan.
    /// </summary>
    internal static class SaveFiles
    {
        private const string NewsFile = "ailegacy_news.json";
        private const string LettersFile = "ailegacy_letters.json";
        private const string OrdersFile = "ailegacy_orders.json";
        private const string CampaignFile = "ailegacy_campaign.json";
        private const string QuestsFile = "ailegacy_quests.json";
        private const string ItemsFile = "ailegacy_items.json";
        private const string ManifestFile = "_escritos.json";
        private const string WorldFile = "ailegacy_world.json";
        private const string MessagesFile = "ailegacy_messages.json";

        // Huella de lo último escrito en cada archivo (clave = nombre de archivo) de la campaña en curso
        private static Dictionary<string, string> _written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _writtenDir;

        private class CampaignInfo
        {
            [JsonProperty("v")] public int Version = 3;
            [JsonProperty("last_letter_day")] public double LastLetterDay = -1;
            [JsonProperty("saved_day")] public double SavedDay;
        }

        public static string Root => Path.Combine(Paths.ModuleDir, "save_data");
        public static string DirFor(string campaignId) => Path.Combine(Root, Safe(campaignId));
        public static bool Exists(string campaignId) => !string.IsNullOrEmpty(campaignId) && File.Exists(Path.Combine(DirFor(campaignId), CampaignFile));

        private static string Safe(string s)
        {
            var bad = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder();
            foreach (var c in s ?? "") sb.Append(Array.IndexOf(bad, c) >= 0 || c == '(' || c == ')' ? '_' : c);
            var r = sb.ToString().Trim().Trim('.');
            return r.Length == 0 ? "_" : r.Length > 60 ? r.Substring(0, 60) : r;
        }

        private static string HeroFileName(HeroMemory m) => $"{Safe(m.HeroName ?? "npc").Replace(' ', '_')} ({Safe(m.HeroId)}).json";

        /// <summary>Huella estable (no cambia entre sesiones) del texto, sin contar saltos de línea ni BOM.</summary>
        private static string Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s ?? "")
                {
                    if (c == '\r' || c == '﻿') continue;
                    h ^= c; h *= 16777619;
                }
                return h.ToString("x8");
            }
        }

        private static void UseDir(string dir)
        {
            if (string.Equals(_writtenDir, dir, StringComparison.OrdinalIgnoreCase)) return;
            _writtenDir = dir;
            _written = ReadManifest(dir);
        }

        private static Dictionary<string, string> ReadManifest(string dir)
        {
            try
            {
                var p = Path.Combine(dir, ManifestFile);
                if (File.Exists(p))
                    return new Dictionary<string, string>(JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(p)) ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Escribe el archivo solo si su contenido ha cambiado (primero a .tmp para no dejarlo a medias).</summary>
        private static bool WriteIfChanged(string dir, string name, string content)
        {
            var path = Path.Combine(dir, name);
            string hash = Hash(content);
            if (_written.TryGetValue(name, out var old) && old == hash && File.Exists(path)) return false;
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, content, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
            _written[name] = hash;
            return true;
        }

        // ============================================================ guardar

        private static double _lastPumpHour = -1;

        /// <summary>
        /// Escribe ya lo que haya cambiado. Se llama tras cada respuesta de un NPC, al cerrar una charla, con cada carta
        /// y cada orden.
        /// </summary>
        public static void Flush(bool heroes = true)
        {
            try
            {
                var c = TaleWorlds.CampaignSystem.Campaign.Current;
                if (c == null || string.IsNullOrEmpty(c.UniqueGameId)) return;
                Save(c.UniqueGameId, TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays, heroes);
            }
            catch (Exception ex) { Log.Error("Volcado de archivos de la campaña", ex); }
        }

        /// <summary>Llamar a menudo: cada 6 horas de juego vuelca noticias, cartas y órdenes; una vez al día, también los personajes.</summary>
        public static void Pump()
        {
            var c = TaleWorlds.CampaignSystem.Campaign.Current;
            if (c == null) { _lastPumpHour = -1; return; }
            double hour = TaleWorlds.CampaignSystem.CampaignTime.Now.ToHours;
            if (_lastPumpHour < 0 || hour < _lastPumpHour) { _lastPumpHour = hour; return; }
            if (hour - _lastPumpHour < 6) return;
            bool newDay = (int)(hour / 24) != (int)(_lastPumpHour / 24);
            _lastPumpHour = hour;
            Flush(heroes: newDay);
        }

        public static void Save(string campaignId, double campaignDay, bool heroes = true)
        {
            if (string.IsNullOrEmpty(campaignId)) return;
            try
            {
                var dir = DirFor(campaignId);
                Directory.CreateDirectory(dir);
                UseDir(dir);
                int written = 0;

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NewsFile, LettersFile, OrdersFile, CampaignFile, QuestsFile, ItemsFile, ManifestFile, WorldFile, MessagesFile };
                if (WriteIfChanged(dir, MessagesFile, JsonConvert.SerializeObject(InfoExchange.Messages, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, WorldFile, JsonConvert.SerializeObject(WorldSim.State, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, QuestsFile, JsonConvert.SerializeObject(QuestSystem.Quests, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, ItemsFile, JsonConvert.SerializeObject(Belongings.Items, Formatting.Indented))) written++;
                foreach (var m in heroes ? MemoryStore.AllHeroes.ToList() : new List<HeroMemory>())
                {
                    if (string.IsNullOrEmpty(m.HeroId)) continue;
                    var name = HeroFileName(m);
                    keep.Add(name);
                    if (WriteIfChanged(dir, name, JsonConvert.SerializeObject(m, Formatting.Indented))) written++;
                }
                if (WriteIfChanged(dir, NewsFile, JsonConvert.SerializeObject(NewsStore.All.ToList(), Formatting.Indented))) written++;
                if (WriteIfChanged(dir, LettersFile, JsonConvert.SerializeObject(MemoryStore.Letters, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, OrdersFile, JsonConvert.SerializeObject(OrderSystem.Orders, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, CampaignFile, JsonConvert.SerializeObject(
                        new CampaignInfo { LastLetterDay = MemoryStore.LastLetterDay, SavedDay = campaignDay }, Formatting.Indented))) written++;

                // Personajes olvidados (/olvidar) o renombrados: su archivo viejo sobra
                int removed = 0;
                foreach (var f in heroes ? Directory.GetFiles(dir, "*.json") : new string[0])
                {
                    var n = Path.GetFileName(f);
                    if (keep.Contains(n)) continue;
                    try { File.Delete(f); _written.Remove(n); removed++; } catch { }
                }
                if (written + removed > 0)
                {
                    File.WriteAllText(Path.Combine(dir, ManifestFile), JsonConvert.SerializeObject(_written, Formatting.Indented));
                    Log.Info($"Archivos de la campaña {campaignId}: {written} escritos, {removed} borrados");
                }
            }
            catch (Exception ex) { Log.Error("No se pudieron escribir los archivos de la campaña", ex); }
        }

        // ============================================================ cargar

        /// <summary>
        /// Personajes cuyo archivo has editado tú a mano (su contenido ya no coincide con lo que escribió el mod).
        /// Hay que llamarlo ANTES de volver a escribir nada.
        /// </summary>
        public static List<HeroMemory> ManualEdits(string campaignId)
        {
            var list = new List<HeroMemory>();
            if (!Exists(campaignId)) return list;
            try
            {
                var dir = DirFor(campaignId);
                var manifest = ReadManifest(dir);
                if (manifest.Count == 0) return list;   // sin huellas no se puede saber: no se asume nada
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("ailegacy_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                    try
                    {
                        var text = File.ReadAllText(f, Encoding.UTF8);
                        if (!manifest.TryGetValue(name, out var h) || h == Hash(text)) continue;   // solo los que escribió el mod y luego cambiaron
                        var m = JsonConvert.DeserializeObject<HeroMemory>(text);
                        if (m != null && !string.IsNullOrEmpty(m.HeroId)) list.Add(m);
                    }
                    catch (Exception ex) { Log.Warn($"Archivo editado ilegible, se ignora: {name} ({ex.Message})"); }
                }
            }
            catch (Exception ex) { Log.Error("Buscando archivos editados a mano", ex); }
            return list;
        }

        public static bool Load(string campaignId)
        {
            if (!Exists(campaignId)) return false;
            try
            {
                var dir = DirFor(campaignId);
                UseDir(dir);
                var heroes = new List<HeroMemory>();
                int bad = 0;
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("ailegacy_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                    try
                    {
                        var m = JsonConvert.DeserializeObject<HeroMemory>(File.ReadAllText(f, Encoding.UTF8));
                        if (m == null || string.IsNullOrEmpty(m.HeroId)) { bad++; continue; }
                        heroes.Add(m);
                    }
                    catch (Exception ex) { bad++; Log.Warn($"Archivo de personaje ilegible, se ignora: {name} ({ex.Message})"); }
                }

                var news = Read<List<NewsItem>>(dir, NewsFile) ?? new List<NewsItem>();
                var letters = Read<List<PendingLetter>>(dir, LettersFile) ?? new List<PendingLetter>();
                var orders = Read<List<PartyOrder>>(dir, OrdersFile) ?? new List<PartyOrder>();
                var info = Read<CampaignInfo>(dir, CampaignFile) ?? new CampaignInfo();
                var quests = Read<List<AiQuest>>(dir, QuestsFile) ?? new List<AiQuest>();
                var items = Read<List<RpItem>>(dir, ItemsFile) ?? new List<RpItem>();

                var world = Read<WorldState>(dir, WorldFile) ?? new WorldState();
                var messages = Read<List<NpcMessage>>(dir, MessagesFile) ?? new List<NpcMessage>();
                MemoryStore.LoadFrom(heroes, news, info.LastLetterDay, letters, orders, quests, items, world, messages);
                Log.Info($"Campaña {campaignId} cargada de archivos: {heroes.Count} personajes, {news.Count} noticias, {letters.Count} cartas, {orders.Count} órdenes, {quests.Count} encargos" +
                         (bad > 0 ? $", {bad} archivos ilegibles" : ""));
                return true;
            }
            catch (Exception ex) { Log.Error("No se pudieron leer los archivos de la campaña", ex); return false; }
        }

        /// <summary>Aparta a una subcarpeta los archivos de los personajes indicados (para deshacer una importación sin perder nada).</summary>
        public static string BackupHeroFiles(string campaignId, IEnumerable<HeroMemory> heroes, string label)
        {
            try
            {
                var dir = DirFor(campaignId);
                UseDir(dir);
                var dest = Path.Combine(dir, $"_{label}_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(dest);
                foreach (var m in heroes)
                {
                    var name = HeroFileName(m);
                    File.WriteAllText(Path.Combine(dest, name), JsonConvert.SerializeObject(m, Formatting.Indented), new UTF8Encoding(false));
                    var src = Path.Combine(dir, name);
                    if (File.Exists(src)) File.Delete(src);
                    _written.Remove(name);
                }
                return dest;
            }
            catch (Exception ex) { Log.Error("Copia de personajes", ex); return null; }
        }

        private static T Read<T>(string dir, string file) where T : class
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            try { return JsonConvert.DeserializeObject<T>(File.ReadAllText(path, Encoding.UTF8)); }
            catch (Exception ex) { Log.Warn($"{file} ilegible, se ignora: {ex.Message}"); return null; }
        }
    }
}
