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
    /// AI Legacy data on disk, per campaign: Modules\AILegacy\save_data\[campaign id]\
    ///   Name (hero_id).json          one file per character (memory, personality, trust, romance...)
    ///   ailegacy_news.json           news and rumors
    ///   ailegacy_letters.json        letters on the way
    ///   ailegacy_orders.json         orders on the map
    ///   ailegacy_quests.json         quests
    ///   ailegacy_items.json          documents and roleplay items
    ///   ailegacy_campaign.json       loose campaign data
    ///   _escritos.json               hashes of what the mod wrote (to spot files you edited by hand)
    ///   _ramas\YYYYMMDD_HHMMSS\      files from a branch dropped when loading an older save (last 5 kept)
    ///   _papelera\                   leftover files (forgotten characters, unknown files) moved here instead of deleted
    ///   _copias_editor\              full memory backup before an import or delete from the editor
    /// Written right away (after each reply, letter, order...) and when the game saves.
    /// On load, the copy inside the save wins, except for files you edited by hand.
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

        // Hash of the last content written to each file of the current campaign (key = file name)
        private static Dictionary<string, string> _written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static string _writtenDir;

        // Stamp of the files' state. It changes every time the mod writes something new, and it also goes into the save.
        // If it doesn't match the save's stamp on load, the files come from another (later) branch and get archived first.
        private static string _stamp;

        // Campaign subfolders
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

        /// <summary>Current files stamp for this campaign (stored inside the save).</summary>
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

        /// <summary>FNV-1a hash that stays the same across sessions (unlike GetHashCode). Ignores \r and the BOM.</summary>
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

        /// <summary>Writes the file only if its content changed. If it can't be written, it's skipped with a log warning
        /// and retried on the next flush.</summary>
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
        /// Writes to .tmp and swaps it in one go (File.Replace is atomic on NTFS), so a crash halfway never leaves the
        /// good file deleted. A few retries for antivirus or the file being open in an editor. If it still fails, logs a
        /// warning and returns false without throwing, so it can't break the game's save.
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
                            // Drives without Replace (FAT, network): move the original aside and delete it only once the new one is in place
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
        /// Cleans up after a crash. A .tmp with no main file becomes the main file (if it's valid JSON).
        /// A .tmp next to its main file is dropped, since File.Replace keeps the main one whole.
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
                        JToken.Parse(File.ReadAllText(tmp, Encoding.UTF8));   // throws if it's cut short
                        File.Move(tmp, main);
                        Log.Warn("Recuperado de un corte: " + Path.GetFileName(main));
                    }
                    catch (Exception ex) { Log.Warn($"Resto de escritura ilegible, se ignora: {Path.GetFileName(tmp)} ({ex.Message})"); }
                }
            }
            catch { }
        }

        /// <summary>Moves a file to _papelera (date prefixed) instead of deleting it. Keeps the last MaxTrash.</summary>
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

        /// <summary>Keeps only the <paramref name="keep"/> newest entries (names start with the date).</summary>
        private static void Prune(IEnumerable<string> entries, int keep, Action<string> delete)
        {
            foreach (var old in entries.OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal).Skip(keep))
                try { delete(old); } catch { }
        }

        // ============================================================ save

        private static double _lastPumpHour = -1;

        /// <summary>
        /// Writes whatever changed, now. Called after each NPC reply, when a chat closes, and with every letter and order.
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

        /// <summary>Call often. Every 6 game hours it flushes news, letters and orders; once a day, the characters too.</summary>
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

        /// <param name="keepStamp">true only for the flush right after loading: the files become that save's files and
        /// keep its stamp, so reloading it again isn't taken as another branch.</param>
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
                // Only serialize characters that changed since the last flush (with hundreds of them, serializing all after
                // every reply caused stutters). Every 12 flushes check them all, in case the signature misses something.
                if (_sigDir != dir) { _sigDir = dir; _sig.Clear(); _flushes = 0; }
                bool all = _flushes++ % 12 == 0;
                foreach (var m in heroes ? MemoryStore.AllHeroes.ToList() : new List<HeroMemory>())
                {
                    if (string.IsNullOrEmpty(m.HeroId)) continue;
                    var name = HeroFileName(m);
                    keep.Add(name);
                    long sig = 0;
                    try { sig = Signature(m); } catch { all = true; }
                    if (!all && _sig.TryGetValue(m.HeroId, out long old) && old == sig && _written.ContainsKey(name)) continue;
                    try { if (WriteIfChanged(dir, name, JsonConvert.SerializeObject(m, Formatting.Indented))) written++; _sig[m.HeroId] = sig; }
                    catch (Exception ex) { Log.Warn($"Se salta el archivo de {m.HeroName} ({m.HeroId}): {ex.Message}"); }
                }
                if (WriteIfChanged(dir, NewsFile, JsonConvert.SerializeObject(NewsStore.All.ToList(), Formatting.Indented))) written++;
                if (WriteIfChanged(dir, LettersFile, JsonConvert.SerializeObject(MemoryStore.Letters, Formatting.Indented))) written++;
                if (WriteIfChanged(dir, OrdersFile, JsonConvert.SerializeObject(OrderSystem.Orders, Formatting.Indented))) written++;
                // Something changed, so new stamp (except on the flush after loading, which takes the save's stamp)
                if (string.IsNullOrEmpty(_stamp) || (written > 0 && !keepStamp)) _stamp = NewStamp();
                if (WriteIfChanged(dir, CampaignFile, JsonConvert.SerializeObject(
                        new CampaignInfo { LastLetterDay = MemoryStore.LastLetterDay, SavedDay = campaignDay, Stamp = _stamp }, Formatting.Indented))) written++;

                // Forgotten (/olvidar) or renamed characters and unknown files go to _papelera, never deleted
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

        private static readonly Dictionary<string, long> _sig = new Dictionary<string, long>();
        private static string _sigDir;
        private static int _flushes;

        /// <summary>Something changed outside the usual paths (the editor, an import), so the next flush checks every character.</summary>
        public static void Dirty() { _sig.Clear(); _flushes = 0; }

        /// <summary>Cheap signature of what changes in a character's memory, so we skip serializing it if nothing did.</summary>
        private static long Signature(HeroMemory m)
        {
            unchecked
            {
                long h = 1469598103934665603L;
                void A(long v) { h = (h ^ v) * 1099511628211L; }
                void T(string t) { A(t == null ? -1 : t.Length); A(t == null ? 0 : t.GetHashCode()); }
                A(m.Turns.Count); if (m.Turns.Count > 0) { var l = m.Turns[m.Turns.Count - 1]; T(l.Text); A((long)(l.Day * 100)); }
                T(m.Summary); A(m.Summaries?.Count ?? 0); A(m.Notes.Count); if (m.Notes.Count > 0) T(m.Notes[m.Notes.Count - 1]);
                A(m.CustomInstructions.Count); if (m.CustomInstructions.Count > 0) T(m.CustomInstructions[m.CustomInstructions.Count - 1]);
                A(m.Trust); A(m.TrustSet ? 1 : 0); A(m.Romance); A(m.Tension); A(m.LiesSuspected); A(m.ConversationCount); A(m.RelationChangedTotal);
                A((long)(m.LastConversationDay * 100)); A((long)(m.MoodDay * 100)); T(m.Mood); T(m.MoodReason); T(m.KnownPlayerName); T(m.KnownPlayerClaims); T(m.HeroName);
                A(m.Pages.Count); if (m.Pages.Count > 0) T(m.Pages[m.Pages.Count - 1].Text);
                A(m.Archive.Count); A(m.ArchivedTotal); A(m.Heard.Count);
                // Capped lists can change without changing size (oldest out, new one in), so hash the last item too
                if (m.Heard.Count > 0) { var hd = m.Heard[m.Heard.Count - 1]; T(hd.Text); T(hd.FromName); A((long)(hd.Day * 100)); } A(m.KnownSecretIds.Count); A(m.KnownInfoIds.Count); A(m.KnowledgeRolled ? 1 : 0);
                A(m.Others.Count); foreach (var o in m.Others.Values) { A(o.Trust); A(o.Notes.Count); A((long)(o.Day * 100)); if (o.Notes.Count > 0) T(o.Notes[o.Notes.Count - 1]); }
                A(m.ProfileRevision); A(m.Profile == null ? 0 : 1);
                if (m.Profile != null) { T(m.Profile.Personality); T(m.Profile.Backstory); T(m.Profile.SpeechStyle); T(m.Profile.Goals); T(m.Profile.Secret); T(m.Profile.GivenName); T(m.Profile.Reputation); T(m.Profile.Religion); }
                A(m.LettersSent); A(m.OwesReplyToPlayer ? 1 : 0); A((long)(m.LastLetterDay * 100)); A((long)(m.LastInitiativeDay * 100)); A((long)(m.LastRomanceDay * 100)); A((long)(m.LastIntimacyDay * 100));
                A((long)(m.TensionDay * 100)); A((long)(m.LastDevelopmentDay * 100)); T(m.Archetype); T(m.Morality); T(m.Profanity); T(m.Piety); T(m.InitiativeReason); T(m.ImportedFrom); A(m.Seeded ? 1 : 0);
                T(m.LastAiResponse);
                return h;
            }
        }

        // ============================================================ load

        /// <summary>
        /// Characters whose file you edited by hand (its content no longer matches what the mod wrote).
        /// Call it BEFORE writing anything again.
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
                if (manifest.Count == 0) return list;   // no hashes, no way to tell, so assume nothing
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("ailegacy_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                    try
                    {
                        var text = File.ReadAllText(f, Encoding.UTF8);
                        if (!manifest.TryGetValue(name, out var h) || h == Hash(text)) continue;   // only files the mod wrote that changed afterwards
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
        /// Runs when loading a save whose memory is about to overwrite the files. If the files belong to another branch
        /// (you kept playing after that save, or came from a different save), copy them first to _ramas\YYYYMMDD_HHMMSS\
        /// (last MaxBranches kept). Compares the save's stamp with the files' stamp. For saves older than the stamp,
        /// compares the day stored in the files with the save's day.
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

        /// <summary>After loading a save, the files take its stamp (the next flush must use keepStamp).</summary>
        public static void AdoptStamp(string campaignId, string stamp)
        {
            if (string.IsNullOrEmpty(campaignId) || string.IsNullOrEmpty(stamp)) return;
            UseDir(DirFor(campaignId));
            _stamp = stamp;
        }

        /// <summary>
        /// Backs up the whole memory (same as the editor export) to _copias_editor\ before an import or delete.
        /// Keeps the last MaxEditorBackups. Returns the path, or null on failure.
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

        /// <summary>Moves the given characters' files to a subfolder, so an import can be undone without losing anything.</summary>
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
            catch (Exception ex)
            {
                // Keep an untouched copy so the data can be recovered by hand
                try
                {
                    string keep = Path.Combine(dir, "_ilegibles");
                    Directory.CreateDirectory(keep);
                    string to = Path.Combine(keep, Path.GetFileNameWithoutExtension(file) + "." + DateTime.Now.ToString("yyyyMMdd_HHmmss") + ".json");
                    File.Copy(path, to, true);
                    Log.Error($"{file} es ilegible ({ex.Message}). Copia guardada en _ilegibles\\{Path.GetFileName(to)}; se sigue sin esos datos.");
                }
                catch (Exception ex2) { Log.Error($"{file} ilegible y no se pudo apartar: {ex.Message} / {ex2.Message}"); }
                return null;
            }
        }
    }
}
