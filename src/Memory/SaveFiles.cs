using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.World;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

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
    ///   _ramas\AAAAMMDD_HHMMSS\      → archivos de una rama descartada al cargar un guardado anterior (las 5 últimas)
    ///   _papelera\                   → archivos que sobraban (personajes olvidados, archivos desconocidos), en vez de borrarlos
    ///   _copias_editor\              → copia de toda la memoria antes de importar o borrar desde el editor
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

        // Sello del estado de los archivos: cambia cada vez que el mod escribe algo nuevo y va también dentro de la partida.
        // Si al cargar no coincide con el de la partida, los archivos son de otra rama (posterior) y se archivan antes de pisarlos.
        private static string _stamp;

        // Subcarpetas de la campaña
        private const string BranchesDir = "_ramas";
        private const string TrashDir = "_papelera";
        private const string EditorBackupsDir = "_copias_editor";
        private const int MaxBranches = 5;
        private const int MaxTrash = 100;
        private const int MaxEditorBackups = 10;

        private class CampaignInfo
        {
            [JsonProperty("v")] public int Version = 3;
            [JsonProperty("last_letter_day")] public double LastLetterDay = -1;
            [JsonProperty("saved_day")] public double SavedDay;
            [JsonProperty("stamp")] public string Stamp;
        }

        /// <summary>Sello actual de los archivos de la campaña en curso (se guarda dentro de la partida).</summary>
        public static string Stamp => _stamp;

        private static string NewStamp() => Guid.NewGuid().ToString("N").Substring(0, 16);

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
            _stamp = ReadInfo(dir)?.Stamp;
        }

        private static CampaignInfo ReadInfo(string dir)
        {
            try
            {
                var p = Path.Combine(dir, CampaignFile);
                return File.Exists(p) ? JsonConvert.DeserializeObject<CampaignInfo>(File.ReadAllText(p, Encoding.UTF8)) : null;
            }
            catch { return null; }
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

        /// <summary>Escribe el archivo solo si su contenido ha cambiado (primero a .tmp para no dejarlo a medias).
        /// Si no se puede escribir, se salta con aviso en el log (se reintentará en el siguiente volcado).</summary>
        private static bool WriteIfChanged(string dir, string name, string content)
        {
            var path = Path.Combine(dir, name);
            string hash = Hash(content);
            if (_written.TryGetValue(name, out var old) && old == hash && File.Exists(path)) return false;
            if (!WriteAtomic(path, content)) return false;
            _written[name] = hash;
            return true;
        }

        /// <summary>
        /// Escribe en .tmp y lo cambia por el archivo de una vez (File.Replace, atómico en NTFS): un corte a medias
        /// nunca deja el archivo bueno borrado. Unos pocos reintentos (antivirus, el archivo abierto en un editor…);
        /// si aun así falla, aviso en el log y false, sin lanzar nada (no debe romper el guardado del juego).
        /// </summary>
        private static bool WriteAtomic(string path, string content)
        {
            var tmp = path + ".tmp";
            Exception last = null;
            for (int attempt = 0; attempt < 3; attempt++)
            {
                try
                {
                    if (attempt > 0) System.Threading.Thread.Sleep(40 * attempt);
                    File.WriteAllText(tmp, content, new UTF8Encoding(false));
                    if (File.Exists(path))
                    {
                        try { File.Replace(tmp, path, null, true); }
                        catch (PlatformNotSupportedException)
                        {
                            // Unidades sin Replace (FAT, red): el original se aparta y solo se borra cuando el nuevo ya está en su sitio
                            var old = path + ".old";
                            if (File.Exists(old)) File.Delete(old);
                            File.Move(path, old);
                            try { File.Move(tmp, path); }
                            catch { if (!File.Exists(path)) File.Move(old, path); throw; }
                            try { File.Delete(old); } catch { }
                        }
                    }
                    else File.Move(tmp, path);
                    return true;
                }
                catch (Exception ex) { last = ex; }
            }
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { }
            Log.Warn($"No se pudo escribir {Path.GetFileName(path)} tras 3 intentos; se salta por ahora ({last?.GetType().Name}: {last?.Message})");
            return false;
        }

        /// <summary>
        /// Restos de un corte: un .tmp sin su archivo principal pasa a ser el principal (si es JSON válido);
        /// un .tmp junto a su principal sobra (el principal está entero gracias a File.Replace).
        /// </summary>
        private static void RecoverTemp(string dir)
        {
            try
            {
                foreach (var tmp in Directory.GetFiles(dir, "*.json.tmp"))
                {
                    var main = tmp.Substring(0, tmp.Length - 4);
                    try
                    {
                        if (File.Exists(main)) { File.Delete(tmp); continue; }
                        JToken.Parse(File.ReadAllText(tmp, Encoding.UTF8));   // solo si está entero
                        File.Move(tmp, main);
                        Log.Warn("Recuperado de un corte: " + Path.GetFileName(main));
                    }
                    catch (Exception ex) { Log.Warn($"Resto de escritura ilegible, se ignora: {Path.GetFileName(tmp)} ({ex.Message})"); }
                }
            }
            catch { }
        }

        /// <summary>Mueve un archivo a _papelera (con fecha delante) en vez de borrarlo; se conservan los últimos MaxTrash.</summary>
        private static bool MoveToTrash(string dir, string file)
        {
            try
            {
                var trash = Path.Combine(dir, TrashDir);
                Directory.CreateDirectory(trash);
                var baseName = $"{DateTime.Now:yyyyMMdd_HHmmss}_{Path.GetFileName(file)}";
                var dest = Path.Combine(trash, baseName);
                for (int i = 2; File.Exists(dest); i++) dest = Path.Combine(trash, $"{Path.GetFileNameWithoutExtension(baseName)}_{i}{Path.GetExtension(baseName)}");
                File.Move(file, dest);
                Prune(Directory.GetFiles(trash), MaxTrash, File.Delete);
                return true;
            }
            catch (Exception ex) { Log.Warn($"No se pudo mover a la papelera {Path.GetFileName(file)}: {ex.Message}"); return false; }
        }

        /// <summary>Deja solo las <paramref name="keep"/> entradas más nuevas (los nombres empiezan por la fecha).</summary>
        private static void Prune(IEnumerable<string> entries, int keep, Action<string> delete)
        {
            foreach (var old in entries.OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).Skip(keep))
                try { delete(old); } catch { }
        }

        // ============================================================ guardar

        private static double _lastPumpHour = -1;

        /// <summary>
        /// Escribe ya lo que haya cambiado. Se llama tras cada respuesta de un NPC, al cerrar una charla, con cada carta
        /// y cada orden.
        /// </summary>
        public static void Flush(bool heroes = true, bool keepStamp = false)
        {
            try
            {
                var c = TaleWorlds.CampaignSystem.Campaign.Current;
                if (c == null || string.IsNullOrEmpty(c.UniqueGameId)) return;
                Save(c.UniqueGameId, TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays, heroes, keepStamp);
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

        /// <param name="keepStamp">true solo en el volcado justo tras cargar: los archivos pasan a ser los de esa partida
        /// y conservan su sello (así, recargarla otra vez no se toma por otra rama).</param>
        public static void Save(string campaignId, double campaignDay, bool heroes = true, bool keepStamp = false)
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
                    // Un personaje que no se pueda escribir no detiene a los demás
                    try { if (WriteIfChanged(dir, name, JsonConvert.SerializeObject(m, Formatting.Indented))) written++; }
                    catch (Exception ex) { Log.Warn($"Se salta el archivo de {m.HeroName} ({m.HeroId}): {ex.Message}"); }
                }
                if (WriteIfChanged(dir, NewsFile, JsonConvert.SerializeObject(NewsStore.All.ToList(), Formatting.Indented))) written++;
                if (WriteIfChanged(dir, LettersFile, JsonConvert.SerializeObject(MemoryStore.Letters, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, OrdersFile, JsonConvert.SerializeObject(OrderSystem.Orders, Formatting.Indented))) written++;
                // Algo ha cambiado: nuevo sello (salvo en el volcado de después de cargar, que adopta el de la partida)
                if (string.IsNullOrEmpty(_stamp) || (written > 0 && !keepStamp)) _stamp = NewStamp();
                if (WriteIfChanged(dir, CampaignFile, JsonConvert.SerializeObject(
                        new CampaignInfo { LastLetterDay = MemoryStore.LastLetterDay, SavedDay = campaignDay, Stamp = _stamp }, Formatting.Indented))) written++;

                // Personajes olvidados (/olvidar) o renombrados, o archivos que no conocemos: no se borran, van a _papelera
                int removed = 0;
                foreach (var f in heroes ? Directory.GetFiles(dir, "*.json") : new string[0])
                {
                    var n = Path.GetFileName(f);
                    if (keep.Contains(n)) continue;
                    if (MoveToTrash(dir, f)) { _written.Remove(n); removed++; }
                }
                if (written + removed > 0)
                {
                    WriteAtomic(Path.Combine(dir, ManifestFile), JsonConvert.SerializeObject(_written, Formatting.Indented));
                    Log.Info($"Archivos de la campaña {campaignId}: {written} escritos, {removed} a la papelera");
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
                RecoverTemp(dir);
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
                RecoverTemp(dir);
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

        /// <summary>
        /// Al cargar una partida cuya memoria va a pisar los archivos: si estos son de otra rama (has seguido jugando
        /// después de ese guardado, o vienes de otro guardado distinto), se copian antes a _ramas\AAAAMMDD_HHMMSS\
        /// (se conservan las MaxBranches últimas). Se compara el sello de la partida con el de los archivos; con partidas
        /// de antes del sello, el día guardado en los archivos con el de la partida.
        /// </summary>
        public static string ArchiveIfDiverged(string campaignId, string savedStamp, double campaignDay)
        {
            if (!Exists(campaignId)) return null;
            try
            {
                var dir = DirFor(campaignId);
                RecoverTemp(dir);
                var info = ReadInfo(dir);
                bool diverged;
                string why;
                if (!string.IsNullOrEmpty(savedStamp) && !string.IsNullOrEmpty(info?.Stamp))
                {
                    diverged = savedStamp != info.Stamp;
                    why = $"sello {info.Stamp} ≠ {savedStamp}";
                }
                else
                {
                    diverged = info == null || info.SavedDay > campaignDay + 0.01;
                    why = $"día de los archivos {info?.SavedDay:0.##} > día de la partida {campaignDay:0.##}";
                }
                if (!diverged) return null;
                var files = Directory.GetFiles(dir, "*.json");
                if (files.Length == 0) return null;

                var root = Path.Combine(dir, BranchesDir);
                var dest = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
                for (int i = 2; Directory.Exists(dest); i++) dest = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss}_{i}");
                Directory.CreateDirectory(dest);
                int copied = 0;
                foreach (var f in files)
                {
                    try { File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true); copied++; }
                    catch (Exception ex) { Log.Warn($"Rama archivada sin {Path.GetFileName(f)}: {ex.Message}"); }
                }
                Prune(Directory.GetDirectories(root), MaxBranches, d => Directory.Delete(d, true));
                Log.Info($"Los archivos eran de otra rama ({why}): {copied} archivos copiados a {dest} antes de cargar la memoria de la partida");
                return dest;
            }
            catch (Exception ex) { Log.Error("Archivando la rama descartada", ex); return null; }
        }

        /// <summary>Tras cargar una partida, los archivos pasan a llevar su sello (el siguiente volcado debe usar keepStamp).</summary>
        public static void AdoptStamp(string campaignId, string stamp)
        {
            if (string.IsNullOrEmpty(campaignId) || string.IsNullOrEmpty(stamp)) return;
            UseDir(DirFor(campaignId));
            _stamp = stamp;
        }

        /// <summary>
        /// Copia de toda la memoria (lo mismo que exporta el editor) en _copias_editor\, antes de importar o borrar.
        /// Se conservan las MaxEditorBackups últimas. Devuelve la ruta, o null si no se pudo.
        /// </summary>
        public static string BackupMemory(string campaignId, string label)
        {
            try
            {
                var root = Path.Combine(DirFor(campaignId), EditorBackupsDir);
                Directory.CreateDirectory(root);
                var path = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Safe(label)}.json");
                for (int i = 2; File.Exists(path); i++) path = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss}_{Safe(label)}_{i}.json");
                if (!WriteAtomic(path, MemoryStore.Serialize())) return null;
                Prune(Directory.GetFiles(root, "*.json"), MaxEditorBackups, File.Delete);
                Log.Info("Copia de la memoria antes de " + label + ": " + path);
                return path;
            }
            catch (Exception ex) { Log.Error("Copia de la memoria", ex); return null; }
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
