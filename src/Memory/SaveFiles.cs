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
    /// What changed is decided and serialized on the game thread. The disk writes go to a writer thread, the file of
    /// the character being talked to first. Saving the game, loading and anything that reads the folder wait for it.
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
            Drain();        // nothing of the previous campaign may still be on its way to disk
            _writtenDir = dir;
            _written = ReadManifest(dir);
            lock (IoGate)
            {
                _ioDir = dir;
                _onDisk = new Dictionary<string, string>(_written, StringComparer.OrdinalIgnoreCase);
                _manifestDirty = false;
                _failed.Clear();
            }
            _stamp = ReadInfo(dir)?.Stamp;
        }

        // ============================================================ writer thread

        private class PendingWrite
        {
            public string Dir, Name, Content, Hash;
            public bool Urgent;
        }

        // Guards everything below. The game thread fills the queue, the writer empties it.
        private static readonly object IoGate = new object();
        private static readonly List<PendingWrite> IoQueue = new List<PendingWrite>();
        private static bool _writerRunning;
        private static string _ioDir;
        // What really is on disk (name = hash). The manifest is written from this, never from what is only queued,
        // so a write that failed does not look like a file edited by hand on the next load.
        private static Dictionary<string, string> _onDisk = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        private static bool _manifestDirty;
        // The manifest could not be written: it stays dirty, and is tried again with the next batch, not in a tight loop
        private static bool _manifestHold;
        private static bool _exitHooked;
        private static readonly HashSet<string> _failed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        /// <summary>Queues the file if its content changed since the last time it was queued or written.</summary>
        private static bool QueueIfChanged(string dir, string name, string content, bool urgent = false)
        {
            string hash = Hash(content);
            if (_written.TryGetValue(name, out var old) && old == hash && (IsQueued(dir, name) || File.Exists(Path.Combine(dir, name)))) return false;
            _written[name] = hash;
            lock (IoGate)
            {
                // A newer version of the same file replaces the one still waiting
                IoQueue.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Dir, dir, StringComparison.OrdinalIgnoreCase));
                IoQueue.Add(new PendingWrite { Dir = dir, Name = name, Content = content, Hash = hash, Urgent = urgent });
                StartWriter();
            }
            return true;
        }

        private static bool IsQueued(string dir, string name)
        {
            lock (IoGate)
                return IoQueue.Any(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Dir, dir, StringComparison.OrdinalIgnoreCase));
        }

        /// <summary>Call with IoGate held.</summary>
        private static void StartWriter()
        {
            if (_writerRunning) return;
            _writerRunning = true;
            _manifestHold = false;
            // A background thread, so a hung disk can never keep the game's process alive. On a normal exit the queue
            // is given up to 30 seconds to finish (Drain) before the process goes.
            if (!_exitHooked)
            {
                _exitHooked = true;
                try { AppDomain.CurrentDomain.ProcessExit += (s, e) => Drain(); } catch { }
            }
            new System.Threading.Thread(WriterLoop) { IsBackground = true, Name = "AILegacyFiles" }.Start();
        }

        private static void WriterLoop()
        {
            while (true)
            {
                PendingWrite job = null;
                string manifest = null, manifestDir = null;
                lock (IoGate)
                {
                    if (IoQueue.Count > 0)
                    {
                        int at = IoQueue.FindIndex(w => w.Urgent);
                        if (at < 0) at = 0;
                        job = IoQueue[at];
                        IoQueue.RemoveAt(at);
                    }
                    else if (_manifestDirty && !_manifestHold && _ioDir != null)
                    {
                        _manifestDirty = false;
                        manifestDir = _ioDir;
                        try { manifest = JsonConvert.SerializeObject(_onDisk, Formatting.Indented); } catch { }
                    }
                    else
                    {
                        _writerRunning = false;
                        System.Threading.Monitor.PulseAll(IoGate);
                        return;
                    }
                }
                try
                {
                    if (job != null)
                    {
                        bool ok = WriteAtomic(Path.Combine(job.Dir, job.Name), job.Content);
                        lock (IoGate)
                        {
                            if (!string.Equals(job.Dir, _ioDir, StringComparison.OrdinalIgnoreCase)) continue;
                            if (ok) { _onDisk[job.Name] = job.Hash; _manifestDirty = true; }
                            else _failed.Add(job.Name);
                        }
                    }
                    else if (manifest != null && !WriteAtomic(Path.Combine(manifestDir, ManifestFile), manifest))
                    {
                        // An old manifest next to newer files would make them look hand-edited on the next load
                        lock (IoGate) { _manifestDirty = true; _manifestHold = true; }
                        Log.Warn("No se pudo escribir el índice de archivos de la campaña; se reintenta con el próximo guardado");
                    }
                }
                catch (Exception ex) { Log.Error("Escritura de archivos en segundo plano", ex); }
            }
        }

        /// <summary>
        /// Waits until everything queued is on disk. Returns at once when nothing is. False when the writer was still
        /// at it after 30 seconds: whoever is about to replace the files must not go on then.
        /// </summary>
        public static bool Drain()
        {
            try
            {
                lock (IoGate)
                {
                    var until = DateTime.UtcNow.AddSeconds(30);
                    while (_writerRunning && DateTime.UtcNow < until) System.Threading.Monitor.Wait(IoGate, 500);
                    if (_writerRunning) Log.Warn("Los archivos de la campaña siguen escribiéndose tras 30 s; se continúa sin esperar más");
                    return !_writerRunning;
                }
            }
            catch (Exception ex) { Log.Error("Esperando la escritura de archivos", ex); return false; }
        }

        /// <summary>
        /// After a flush that waited: true when every file reached the disk. False when the writer is still running,
        /// something is still queued or a write failed, so the disk may hold an older version than the memory.
        /// </summary>
        private static bool AllWritten(out int failed)
        {
            lock (IoGate)
            {
                failed = _failed.Count;
                return !_writerRunning && IoQueue.Count == 0 && failed == 0;
            }
        }

        /// <summary>A file was moved or deleted by hand from the game thread: forget its hash.</summary>
        private static void ForgetFile(string name)
        {
            _written.Remove(name);
            lock (IoGate)
            {
                IoQueue.RemoveAll(w => string.Equals(w.Name, name, StringComparison.OrdinalIgnoreCase) && string.Equals(w.Dir, _ioDir, StringComparison.OrdinalIgnoreCase));
                if (_onDisk.Remove(name)) { _manifestDirty = true; StartWriter(); }
            }
        }

        private static CampaignInfo ReadInfo(string dir)
        {
            try
            {
                var p = Path.Combine(dir, CampaignFile);
                return File.Exists(p) ? ReadJson<CampaignInfo>(p) : null;
            }
            catch { return null; }
        }

        private static Dictionary<string, string> ReadManifest(string dir)
        {
            try
            {
                var p = Path.Combine(dir, ManifestFile);
                if (File.Exists(p))
                    return new Dictionary<string, string>(ReadJson<Dictionary<string, string>>(p) ?? new Dictionary<string, string>(),
                        StringComparer.OrdinalIgnoreCase);
            }
            catch { }
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
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
                        CheckJson(ReadBounded(tmp));   // throws if it's cut short, too large or too deep
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
        /// The files reach the disk a moment later, from the writer thread. The flush after loading (keepStamp) waits for them.
        /// </summary>
        /// <param name="recheck">Also compare in full a share of the characters whose signature did not change (the daily flush).</param>
        public static void Flush(bool heroes = true, bool keepStamp = false, bool recheck = false, bool wait = false)
        {
            try
            {
                var c = TaleWorlds.CampaignSystem.Campaign.Current;
                if (c == null || string.IsNullOrEmpty(c.UniqueGameId)) return;
                Save(c.UniqueGameId, TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays, heroes, keepStamp, wait: keepStamp || wait, recheck: recheck);
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
            Flush(heroes: newDay, recheck: true);
        }

        /// <param name="keepStamp">true only for the flush right after loading: the files become that save's files and
        /// keep its stamp, so reloading it again isn't taken as another branch.</param>
        /// <param name="wait">true (the game's own save) returns only when every file is on disk, and every character is
        /// compared in full instead of trusting its signature.</param>
        /// <param name="recheck">Compare in full a twelfth of the characters whose signature did not change, in turn.</param>
        public static void Save(string campaignId, double campaignDay, bool heroes = true, bool keepStamp = false, bool wait = true, bool recheck = false)
        {
            if (string.IsNullOrEmpty(campaignId)) return;
            try
            {
                var dir = DirFor(campaignId);
                Directory.CreateDirectory(dir);
                UseDir(dir);
                int written = 0, serialized = 0, checkedHeroes = 0;
                // Files the writer could not write last time are compared again from scratch
                lock (IoGate)
                {
                    foreach (var n in _failed) _written.Remove(n);
                    _failed.Clear();
                }
                string talking = null;
                try { talking = Dialogue.ChatSession.Active?.Who?.Key; } catch { }

                var keep = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { NewsFile, LettersFile, OrdersFile, CampaignFile, QuestsFile, ItemsFile, ManifestFile, WorldFile, MessagesFile };
                if (QueueIfChanged(dir, MessagesFile, JsonConvert.SerializeObject(InfoExchange.Messages, Formatting.Indented))) written++;
                if (QueueIfChanged(dir, WorldFile, JsonConvert.SerializeObject(WorldSim.State, Formatting.Indented))) written++;
                if (QueueIfChanged(dir, QuestsFile, JsonConvert.SerializeObject(QuestSystem.Quests, Formatting.Indented))) written++;
                if (QueueIfChanged(dir, ItemsFile, JsonConvert.SerializeObject(Belongings.Items, Formatting.Indented))) written++;
                // Only serialize characters whose signature changed since the last flush (with hundreds of them, serializing
                // all after every reply caused stutters). In case the signature misses a change, everyone is compared in
                // full on the first flush and whenever the game saves, and the daily flush goes through a twelfth of them
                // in turn. That used to be everyone at once every 12 flushes, whichever flush that happened to be.
                if (_sigDir != dir) { _sigDir = dir; _sig.Clear(); _flushes = 0; _recheckAt = 0; }
                var list = heroes ? MemoryStore.AllHeroes.ToList() : new List<HeroMemory>();
                bool all = heroes && (_flushes++ == 0 || wait);
                int slice = all || !recheck || list.Count == 0 ? 0 : (list.Count + 11) / 12;
                if (list.Count > 0) _recheckAt %= list.Count;
                for (int i = 0; i < list.Count; i++)
                {
                    var m = list[i];
                    if (string.IsNullOrEmpty(m.HeroId)) continue;
                    var name = HeroFileName(m);
                    keep.Add(name);
                    long sig = 0;
                    try { sig = Signature(m); } catch { all = true; }
                    bool again = slice > 0 && (i - _recheckAt + list.Count) % list.Count < slice;
                    if (!all && !again && _sig.TryGetValue(m.HeroId, out long old) && old == sig && _written.ContainsKey(name)) continue;
                    try
                    {
                        serialized++;
                        if (again) checkedHeroes++;
                        if (QueueIfChanged(dir, name, JsonConvert.SerializeObject(m, Formatting.Indented), urgent: talking != null && m.HeroId == talking)) written++;
                        _sig[m.HeroId] = sig;
                    }
                    catch (Exception ex) { Log.Warn($"Se salta el archivo de {m.HeroName} ({m.HeroId}): {ex.Message}"); }
                }
                if (list.Count > 0) _recheckAt = (_recheckAt + slice) % list.Count;
                if (QueueIfChanged(dir, NewsFile, JsonConvert.SerializeObject(NewsStore.All.ToList(), Formatting.Indented))) written++;
                if (QueueIfChanged(dir, LettersFile, JsonConvert.SerializeObject(MemoryStore.Letters, Formatting.Indented))) written++;
                if (QueueIfChanged(dir, OrdersFile, JsonConvert.SerializeObject(OrderSystem.Orders, Formatting.Indented))) written++;
                // Something changed, so new stamp (except on the flush after loading, which takes the save's stamp)
                if (string.IsNullOrEmpty(_stamp) || (written > 0 && !keepStamp)) _stamp = NewStamp();
                if (QueueIfChanged(dir, CampaignFile, JsonConvert.SerializeObject(
                        new CampaignInfo { LastLetterDay = MemoryStore.LastLetterDay, SavedDay = campaignDay, Stamp = _stamp }, Formatting.Indented))) written++;

                // Forgotten (/olvidar) or renamed characters and unknown files go to _papelera, never deleted
                int removed = 0;
                foreach (var f in heroes ? Directory.GetFiles(dir, "*.json") : new string[0])
                {
                    var n = Path.GetFileName(f);
                    if (keep.Contains(n)) continue;
                    if (MoveToTrash(dir, f)) { ForgetFile(n); removed++; }
                }
                // The writer leaves the manifest (_escritos.json) for last, once the files it names are in place
                if (written + removed > 0)
                    Log.Info($"Archivos de la campaña {campaignId}: {written} escritos, {removed} a la papelera" +
                             (heroes ? $"; personajes serializados {serialized} de {list.Count}" + (checkedHeroes > 0 ? $" ({checkedHeroes} de repaso)" : "") : ""));
                if (wait) Drain();
            }
            catch (Exception ex) { Log.Error("No se pudieron escribir los archivos de la campaña", ex); }
        }

        private static readonly Dictionary<string, long> _sig = new Dictionary<string, long>();
        private static string _sigDir;
        private static int _flushes;
        /// <summary>Where the next flush's re-check of unchanged characters starts.</summary>
        private static int _recheckAt;

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
                if (m.Profile != null) { T(m.Profile.Drives); T(m.Profile.Values); T(m.Profile.SocialBehavior); T(m.Profile.CognitiveStyle); A((long)(m.Profile.Naivety * 1000)); A(m.Profile.SampleLines?.Count ?? 0); }
                T(m.MoodShown); T(m.ArchetypeText);
                if (m.Pages.Count > 0) { var pg = m.Pages[m.Pages.Count - 1]; T(pg.Title); T(pg.Image); }
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
                Drain();
                RecoverTemp(dir);
                var manifest = ReadManifest(dir);
                if (manifest.Count == 0) return list;   // no hashes, no way to tell, so assume nothing
                foreach (var f in Directory.GetFiles(dir, "*.json"))
                {
                    var name = Path.GetFileName(f);
                    if (name.StartsWith("ailegacy_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                    try
                    {
                        var text = ReadBounded(f);
                        if (!manifest.TryGetValue(name, out var h) || h == Hash(text)) continue;   // only files the mod wrote that changed afterwards
                        var m = JsonConvert.DeserializeObject<HeroMemory>(text, BoundedJson);
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
                Drain();
                RecoverTemp(dir);
                UseDir(dir);
                return ReadDir(dir, campaignId);
            }
            catch (Exception ex) { Log.Error("No se pudieron leer los archivos de la campaña", ex); return false; }
        }

        /// <summary>Puts a folder of campaign files into memory, as tolerant as loading always was. Loading a campaign ends here.</summary>
        private static bool ReadDir(string dir, string campaignId)
        {
            try
            {
                var s = ParseDir(dir, false, out string what);
                if (s == null || !MemoryStore.Replace(s)) return false;
                Log.Info($"Campaña {campaignId} cargada de archivos: {what}");
                return true;
            }
            catch (Exception ex) { Log.Error("No se pudieron leer los archivos de la campaña", ex); return false; }
        }

        /// <summary>
        /// Reads a folder of campaign files into a snapshot, without touching what is live. Not strict (loading a
        /// campaign): an unreadable file is skipped or takes its default, as always. Strict (restoring an archived
        /// branch): any file that is unreadable, empty, damaged or a link throws, so nothing partial gets restored.
        /// </summary>
        private static MemoryStore.Snapshot ParseDir(string dir, bool strict, out string what)
        {
            var heroes = new List<HeroMemory>();
            int bad = 0;
            if (strict) CheckBranch(dir);
            foreach (var f in Directory.GetFiles(dir, "*.json"))
            {
                var name = Path.GetFileName(f);
                if (strict && IsLink(f)) throw new InvalidDataException(name + ": es un enlace");
                if (name.StartsWith("ailegacy_", StringComparison.OrdinalIgnoreCase) || name.StartsWith("_")) continue;
                try
                {
                    var m = ReadJson<HeroMemory>(f);
                    if (m == null || string.IsNullOrEmpty(m.HeroId))
                    {
                        if (strict) throw new InvalidDataException("no es la memoria de un personaje");
                        bad++; continue;
                    }
                    heroes.Add(m);
                }
                catch (Exception ex)
                {
                    if (strict) throw new InvalidDataException(name + ": " + ex.Message, ex);
                    bad++; Log.Warn($"Archivo de personaje ilegible, se ignora: {name} ({ex.Message})");
                }
            }

            var news = Read<List<NewsItem>>(dir, NewsFile, strict) ?? new List<NewsItem>();
            var letters = Read<List<PendingLetter>>(dir, LettersFile, strict) ?? new List<PendingLetter>();
            var orders = Read<List<PartyOrder>>(dir, OrdersFile, strict) ?? new List<PartyOrder>();
            var info = Read<CampaignInfo>(dir, CampaignFile, strict) ?? new CampaignInfo();
            var quests = Read<List<AiQuest>>(dir, QuestsFile, strict) ?? new List<AiQuest>();
            var items = Read<List<RpItem>>(dir, ItemsFile, strict) ?? new List<RpItem>();
            var world = Read<WorldState>(dir, WorldFile, strict) ?? new WorldState();
            var messages = Read<List<NpcMessage>>(dir, MessagesFile, strict) ?? new List<NpcMessage>();

            var s = MemoryStore.Compose(heroes, news, info.LastLetterDay, letters, orders, quests, items, world, messages, strict, out string why);
            if (s == null) throw new InvalidDataException(why ?? "datos incompletos");
            what = $"{s.HeroCount} personajes, {news.Count} noticias, {letters.Count} cartas, {orders.Count} órdenes, {quests.Count} encargos" +
                   (bad > 0 ? $", {bad} archivos ilegibles" : "");
            return s;
        }

        /// <summary>
        /// An archived branch is a flat folder of .json files the mod copied there. Before any of it is read, every
        /// entry has to be exactly that: directly inside the folder, a plain name, not a folder, not a link, and a
        /// .json. Anything else throws and nothing is restored.
        /// </summary>
        private static void CheckBranch(string dir)
        {
            if (IsLink(dir)) throw new InvalidDataException("la carpeta de la rama es un enlace");
            string root = Path.GetFullPath(dir).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            foreach (var entry in Directory.GetFileSystemEntries(dir))
            {
                string name = Path.GetFileName(entry);
                string full = Path.GetFullPath(entry);
                if (!PlainName(name) || !string.Equals(Path.GetDirectoryName(full), root, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException(name + ": fuera de la carpeta de la rama");
                var attr = File.GetAttributes(full);
                if ((attr & FileAttributes.ReparsePoint) != 0) throw new InvalidDataException(name + ": es un enlace");
                if ((attr & FileAttributes.Directory) != 0) throw new InvalidDataException(name + ": es una carpeta");
                if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException(name + ": tipo de archivo no admitido");
            }
        }

        // ============================================================ bounded reading

        // One file of the campaign folder (a character, the news, the world state). Same limits as what the editor
        // page may upload. A legitimate file is far smaller and a handful of levels deep.
        public const long MaxFileBytes = 16L * 1024 * 1024;
        public const int MaxJsonDepth = 64;
        internal static readonly JsonSerializerSettings BoundedJson = new JsonSerializerSettings { MaxDepth = MaxJsonDepth };

        /// <summary>
        /// The text of a file, never more than maxBytes of it: a larger file throws InvalidDataException before its
        /// content is in memory. The bytes that really arrive are counted, the size on disk can change meanwhile.
        /// Encoding as File.ReadAllText: UTF-8 unless a byte order mark says otherwise.
        /// </summary>
        internal static string ReadBounded(string path, long maxBytes = MaxFileBytes)
        {
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                if (fs.Length > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)}: demasiado grande ({fs.Length} bytes, máximo {maxBytes})");
                using (var ms = new MemoryStream((int)Math.Min(fs.Length, int.MaxValue / 2)))
                {
                    var buf = new byte[81920];
                    int n;
                    while ((n = fs.Read(buf, 0, buf.Length)) > 0)
                    {
                        if (ms.Length + n > maxBytes) throw new InvalidDataException($"{Path.GetFileName(path)}: demasiado grande (máximo {maxBytes} bytes)");
                        ms.Write(buf, 0, n);
                    }
                    ms.Position = 0;
                    using (var reader = new StreamReader(ms, Encoding.UTF8, true)) return reader.ReadToEnd();
                }
            }
        }

        /// <summary>A file of the campaign folder as an object, bounded in size and nesting. Throws when it is too large, too deep or not valid.</summary>
        internal static T ReadJson<T>(string path, long maxBytes = MaxFileBytes) => JsonConvert.DeserializeObject<T>(ReadBounded(path, maxBytes), BoundedJson);

        /// <summary>Throws unless the text is one complete JSON value no deeper than the limit.</summary>
        private static void CheckJson(string text)
        {
            using (var reader = new JsonTextReader(new StringReader(text)) { MaxDepth = MaxJsonDepth, DateParseHandling = DateParseHandling.None })
                JToken.Load(reader);
        }

        private static bool IsLink(string path) => (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;

        /// <summary>
        /// True when the path is below the root and nothing from the root down to it is a link (symlink,
        /// junction). The root itself is not checked: mod managers may deploy the module folder as a link.
        /// </summary>
        internal static bool PlainUnder(string root, string path)
        {
            try
            {
                if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path)) return false;
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                string p = Path.GetFullPath(path);
                if (!p.StartsWith(r + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return false;
                for (string at = p; at != null && at.Length > r.Length; at = Path.GetDirectoryName(at))
                {
                    // Asked of the entry itself, so a link whose target is gone is still seen. Not there yet is fine
                    try { if (IsLink(at)) return false; }
                    catch (FileNotFoundException) { }
                    catch (DirectoryNotFoundException) { }
                }
                return true;
            }
            catch { return false; }
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
                Drain();
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
                var dest = ArchiveFiles(dir, out int copied);
                if (dest != null) Log.Info($"Los archivos eran de otra rama ({why}): {copied} archivos copiados a {dest} antes de cargar la memoria de la partida");
                return dest;
            }
            catch (Exception ex) { Log.Error("Archivando la rama descartada", ex); return null; }
        }

        /// <summary>Copies the campaign's .json files to a new folder of _ramas (last MaxBranches kept). Null when there are none.</summary>
        private static string ArchiveFiles(string dir, out int copied)
        {
            copied = 0;
            var files = Directory.GetFiles(dir, "*.json");
            if (files.Length == 0) return null;
            var root = Path.Combine(dir, BranchesDir);
            var dest = Path.Combine(root, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
            for (int i = 2; Directory.Exists(dest); i++) dest = Path.Combine(root, $"{DateTime.Now:yyyyMMdd_HHmmss}_{i}");
            Directory.CreateDirectory(dest);
            foreach (var f in files)
            {
                try { File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), true); copied++; }
                catch (Exception ex) { Log.Warn($"Rama archivada sin {Path.GetFileName(f)}: {ex.Message}"); }
            }
            Prune(Directory.GetDirectories(root), MaxBranches, d => Directory.Delete(d, true));
            return dest;
        }

        /// <summary>
        /// The memory inside the save could not be read, or the campaign files did not load. Nothing is emptied: the
        /// campaign files are tried first, then the editor's copies from the newest back. If none of them reads,
        /// whatever files are on disk are copied whole to _ramas before the game goes on with an empty memory and
        /// writes over them. True when a memory was recovered.
        /// </summary>
        public static bool Recover(string campaignId)
        {
            if (string.IsNullOrEmpty(campaignId)) return false;
            try
            {
                if (Load(campaignId))
                {
                    Log.Warn($"Memoria de la campaña {campaignId} recuperada de sus archivos (la copia de la partida guardada no se pudo usar)");
                    return true;
                }
                var dir = DirFor(campaignId);
                if (!Directory.Exists(dir)) { Log.Error($"La campaña {campaignId} no tiene archivos ni copias de los que recuperar la memoria: se empieza sin memoria"); return false; }
                // The files did not load as a whole, but they are what there is: set aside before anything writes over them
                Drain();
                var kept = ArchiveFiles(dir, out int copied);
                if (kept != null) Log.Warn($"Los {copied} archivos de la campaña que no se pudieron cargar quedan guardados en {kept}");
                var root = Path.Combine(dir, EditorBackupsDir);
                if (Directory.Exists(root) && !IsLink(root))
                {
                    foreach (var f in Directory.GetFiles(root, "*.json").OrderByDescending(x => Path.GetFileName(x), StringComparer.Ordinal))
                    {
                        try
                        {
                            if (IsLink(f)) continue;
                            if (!MemoryStore.TryParse(ReadBounded(f, MaxBackupBytes), out var snap, out string why, strict: false)) { Log.Warn($"Copia {Path.GetFileName(f)} ilegible, se prueba la anterior: {why}"); continue; }
                            if (!MemoryStore.Replace(snap)) continue;
                            UseDir(dir);
                            Log.Warn($"Memoria de la campaña {campaignId} recuperada de la copia {Path.GetFileName(f)} ({snap.HeroCount} personajes): ni la partida guardada ni los archivos se pudieron leer");
                            return true;
                        }
                        catch (Exception ex) { Log.Warn($"Copia {Path.GetFileName(f)} ilegible, se prueba la anterior: {ex.Message}"); }
                    }
                }
                Log.Error($"No se pudo recuperar la memoria de la campaña {campaignId} de la partida, de sus archivos ni de las copias: se empieza sin memoria."
                          + (kept != null ? $" Los {copied} archivos que había quedan guardados en {kept}" : " No había archivos que conservar"));
            }
            catch (Exception ex) { Log.Error("Recuperando la memoria de la campaña", ex); }
            return false;
        }

        /// <summary>After loading a save, the files take its stamp (the next flush must use keepStamp).</summary>
        public static void AdoptStamp(string campaignId, string stamp)
        {
            if (string.IsNullOrEmpty(campaignId) || string.IsNullOrEmpty(stamp)) return;
            UseDir(DirFor(campaignId));
            _stamp = stamp;
        }

        /// <summary>
        /// Backs up the whole memory (same as the editor export) to _copias_editor\ before an import, delete or restore.
        /// The copy is read back and checked before it counts: a copy that can't be restored is no copy.
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
                int want = MemoryStore.AllHeroes.Count(h => !string.IsNullOrEmpty(h?.HeroId));
                if (!WriteAtomic(path, MemoryStore.Serialize())) return null;
                // A whole memory is larger than any one file, so it gets the backups' own limit
                if (!MemoryStore.TryParse(ReadBounded(path, MaxBackupBytes), out var back, out string why, strict: false) || back.HeroCount != want)
                {
                    Log.Error($"La copia de la memoria antes de {label} no se lee bien ({why ?? $"{back?.HeroCount} personajes de {want}"}); se descarta");
                    try { File.Delete(path); } catch { }
                    return null;
                }
                Prune(Directory.GetFiles(root, "*.json"), MaxEditorBackups, File.Delete);
                Log.Info("Copia de la memoria antes de " + label + ": " + path);
                return path;
            }
            catch (Exception ex) { Log.Error("Copia de la memoria", ex); return null; }
        }

        // ============================================================ copies the editor lists and restores

        public class BackupInfo
        {
            /// <summary>editor (whole memory, one file), branch (a folder of campaign files) or trash (one discarded file).</summary>
            [JsonProperty("kind")] public string Kind;
            [JsonProperty("name")] public string Name;
            /// <summary>What it was made for (editor copies) or the file's own name (trash).</summary>
            [JsonProperty("label")] public string Label;
            [JsonProperty("when")] public string When;
            [JsonProperty("size")] public long Size;
            [JsonProperty("files")] public int Files;
            /// <summary>False for trash files that are not a character's memory.</summary>
            [JsonProperty("can_restore")] public bool CanRestore = true;
        }

        private static string BackupRoot(string campaignId, string kind)
        {
            string sub = kind == "editor" ? EditorBackupsDir : kind == "branch" ? BranchesDir : kind == "trash" ? TrashDir : null;
            return sub == null ? null : Path.Combine(DirFor(campaignId), sub);
        }

        // The largest copy the editor will bring back. The mod's own copies of a long campaign are a few MB.
        public const long MaxBackupBytes = 64L * 1024 * 1024;

        private static readonly string[] DeviceNames = { "CON", "PRN", "AUX", "NUL", "CONIN$", "CONOUT$", "CLOCK$" };

        /// <summary>
        /// True for a plain file or folder name: no separators, no "..", nothing Windows rewrites on the way to the
        /// disk (it strips trailing dots and spaces, so "a." opens "a") and no device name (CON, NUL, COM1...).
        /// </summary>
        internal static bool PlainName(string name)
        {
            if (string.IsNullOrWhiteSpace(name) || name.Length > 255) return false;
            if (name.IndexOfAny(new[] { '/', '\\', ':' }) >= 0 || name.Contains("..") || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0) return false;
            char last = name[name.Length - 1];
            if (last == '.' || last == ' ') return false;
            int dot = name.IndexOf('.');
            string stem = (dot < 0 ? name : name.Substring(0, dot)).TrimEnd(' ').ToUpperInvariant();
            if (Array.IndexOf(DeviceNames, stem) >= 0) return false;
            if (stem.Length == 4 && (stem.StartsWith("COM") || stem.StartsWith("LPT")) && (char.IsDigit(stem[3]) || stem[3] == '¹' || stem[3] == '²' || stem[3] == '³')) return false;
            return true;
        }

        /// <summary>
        /// Full path of a direct child of root, or null. The name is never turned into a path: it has to be one of
        /// the names an actual listing of root gives back, so whatever is returned is something that is really there
        /// under that exact name. Links (symlinks, junctions) are refused for the root and for the child unless
        /// allowLinks, because what they point at can be anywhere.
        /// </summary>
        internal static string ChildOf(string root, string name, bool allowLinks = false)
        {
            if (string.IsNullOrWhiteSpace(root) || !PlainName(name)) return null;
            try
            {
                var dir = new DirectoryInfo(root);
                if (!dir.Exists || (!allowLinks && (dir.Attributes & FileAttributes.ReparsePoint) != 0)) return null;
                foreach (var entry in Directory.GetFileSystemEntries(dir.FullName))
                {
                    if (!string.Equals(Path.GetFileName(entry), name, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!allowLinks && IsLink(entry)) return null;
                    return entry;
                }
            }
            catch { }
            return null;
        }

        /// <summary>"20261115_101500_importar.json" gives the date and "importar".</summary>
        private static void SplitStamp(string name, DateTime fallback, out string when, out string rest)
        {
            rest = name;
            var at = fallback;
            if (name.Length >= 15 && DateTime.TryParseExact(name.Substring(0, 15), "yyyyMMdd_HHmmss", System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var d))
            {
                at = d;
                rest = name.Substring(15).TrimStart('_');
            }
            when = at.ToString("yyyy-MM-dd HH:mm:ss");
        }

        /// <summary>The copies the mod keeps for this campaign, newest first: editor copies, archived branches and the trash.</summary>
        public static List<BackupInfo> ListBackups(string campaignId)
        {
            var list = new List<BackupInfo>();
            if (string.IsNullOrEmpty(campaignId)) return list;
            foreach (var kind in new[] { "editor", "branch", "trash" })
            {
                try
                {
                    var root = BackupRoot(campaignId, kind);
                    if (!Directory.Exists(root) || IsLink(root)) continue;
                    if (kind == "branch")
                    {
                        foreach (var d in Directory.GetDirectories(root))
                        {
                            if (IsLink(d)) continue;
                            var files = Directory.GetFiles(d, "*.json").Where(f => !IsLink(f)).ToArray();
                            SplitStamp(Path.GetFileName(d), Directory.GetLastWriteTime(d), out var when, out _);
                            list.Add(new BackupInfo { Kind = kind, Name = Path.GetFileName(d), Label = "", When = when, Files = files.Length, Size = files.Sum(f => new FileInfo(f).Length) });
                        }
                        continue;
                    }
                    foreach (var f in Directory.GetFiles(root, "*.json"))
                    {
                        var fi = new FileInfo(f);
                        if ((fi.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        SplitStamp(fi.Name, fi.LastWriteTime, out var when, out var rest);
                        rest = Path.GetFileNameWithoutExtension(rest);
                        // A character's file ends in "(hero id)"; the rest of the trash is not memory that can go back
                        bool ok = kind == "editor" || rest.EndsWith(")");
                        list.Add(new BackupInfo { Kind = kind, Name = fi.Name, Label = rest, When = when, Files = 1, Size = fi.Length, CanRestore = ok });
                    }
                }
                catch (Exception ex) { Log.Warn("Lista de copias (" + kind + "): " + ex.Message); }
            }
            return list.OrderByDescending(b => b.When, StringComparer.Ordinal).ToList();
        }

        /// <summary>
        /// Brings a copy back, all of it or nothing. First the copy is read and checked in full into temporary
        /// objects (an editor copy as a whole memory, a branch as a folder of campaign files, a trash file as one
        /// character). Then the current memory is copied to _copias_editor and that copy is read back. Only then is
        /// the live memory swapped, and everything written so disk and memory match. If any step fails the memory
        /// stays exactly as it was. Returns the safety copy's file name, or null with the reason in error.
        /// </summary>
        public static string RestoreBackup(string campaignId, string kind, string name, out string error)
        {
            error = null;
            string gone = L.T("AIL_e7b1c65b", "Esa copia ya no existe o está incompleta.");
            string damaged = L.T("AIL_e7b1c6df", "Los datos están dañados o incompletos; no se ha cambiado nada.");
            string tooBig = L.T("AIL_e7b1c6de", "Esa copia es demasiado grande para restaurarla desde aquí.");
            string busy = L.T("AIL_e7b1c749", "Los archivos de la campaña aún se están escribiendo; inténtalo de nuevo en unos segundos.");
            try
            {
                var path = ChildOf(BackupRoot(campaignId, kind), name);
                if (path == null) { error = L.T("AIL_61be9174", "Archivo no válido."); return null; }
                // Nothing may still be on its way to disk: a write queued before the swap would land after it and
                // leave the old version of that file in place
                if (!Drain()) { error = busy; return null; }
                MemoryStore.Snapshot whole = null;
                HeroMemory one = null;
                if (kind == "branch")
                {
                    if (!Directory.Exists(path) || !File.Exists(Path.Combine(path, CampaignFile))) { error = gone; return null; }
                    if (Directory.GetFiles(path, "*.json").Sum(f => new FileInfo(f).Length) > 2 * MaxBackupBytes) { error = tooBig; return null; }
                    try { whole = ParseDir(path, true, out _); }
                    catch (Exception ex) { Log.Warn($"Rama {name} no restaurada: {ex.Message}"); error = damaged; return null; }
                }
                else
                {
                    if (!File.Exists(path)) { error = gone; return null; }
                    if (new FileInfo(path).Length > MaxBackupBytes) { error = tooBig; return null; }
                    string text = ReadBounded(path, MaxBackupBytes);
                    if (kind == "editor")
                    {
                        if (!MemoryStore.TryParse(text, out whole, out string why)) { Log.Warn($"Copia {name} no restaurada: {why}"); error = damaged; return null; }
                    }
                    else
                    {
                        try { one = JsonConvert.DeserializeObject<HeroMemory>(text, BoundedJson); }
                        catch (Exception ex) { Log.Warn($"Archivo {name} no restaurado: {ex.Message}"); error = damaged; return null; }
                        if (one == null || string.IsNullOrEmpty(one.HeroId)) { error = L.T("AIL_e7b1c65c", "Ese archivo no es la memoria de un personaje; no se puede restaurar desde aquí."); return null; }
                        if (MemoryStore.Normalize(one) > 0) { Log.Warn($"Archivo {name} no restaurado: tiene entradas vacías"); error = damaged; return null; }
                    }
                }

                // Written, read back and counted inside BackupMemory. Without it nothing is touched
                var safety = BackupMemory(campaignId, "restaurar");
                if (safety == null) { error = L.T("AIL_0490687a", "No se pudo hacer la copia previa; no se ha cambiado nada."); return null; }

                // The safety copy is written directly, not through the queue, and everything here runs on the game
                // thread, the only one that queues: the queue drained above is still empty
                if (whole != null)
                {
                    if (!MemoryStore.Replace(whole, out bool rollbackFailed))
                    {
                        error = rollbackFailed ? RollbackFailedText(safety) : damaged;
                        return null;
                    }
                }
                else MemoryStore.Put(one);

                // Waits for the writer, so disk and memory match before the page is told it is done
                Dirty();
                Flush(wait: true);
                if (!AllWritten(out int failed))
                {
                    Log.Error($"Copia restaurada ({kind}): {name}, pero no todos los archivos llegaron al disco ({failed} con fallo): en disco puede quedar la versión anterior hasta el próximo guardado. Lo que había queda en {Path.GetFileName(safety)}");
                    string file = Path.GetFileName(safety);
                    error = L.F("AIL_e7b1c74b", $"La copia se ha restaurado, pero {failed} archivos no se pudieron escribir en disco. Guarda la partida para volver a intentarlo. Lo que había queda en {file}.");
                    return null;
                }
                Log.Info($"Copia restaurada ({kind}): {name}; lo que había queda en {Path.GetFileName(safety)}");
                return Path.GetFileName(safety);
            }
            // The exception's text can carry full paths: it goes to the log, not to the page
            catch (Exception ex) { Log.Error("Restaurar copia", ex); error = damaged; return null; }
        }

        /// <summary>
        /// What the page is told when swapping the memory failed and the previous one could not be put back. The
        /// details are in the log (MemoryStore.Replace).
        /// </summary>
        internal static string RollbackFailedText(string safetyPath)
        {
            string file = Path.GetFileName(safetyPath ?? "");
            return L.F("AIL_e7b1c74a", $"El cambio ha fallado y la memoria no ha podido volver a como estaba. Carga la partida de nuevo sin guardar. Lo que había queda en {file}.");
        }

        /// <summary>Moves the given characters' files to a subfolder, so an import can be undone without losing anything.</summary>
        public static string BackupHeroFiles(string campaignId, IEnumerable<HeroMemory> heroes, string label)
        {
            try
            {
                var dir = DirFor(campaignId);
                Drain();
                UseDir(dir);
                var dest = Path.Combine(dir, $"_{label}_{DateTime.Now:yyyyMMdd_HHmmss}");
                Directory.CreateDirectory(dest);
                foreach (var m in heroes)
                {
                    var name = HeroFileName(m);
                    File.WriteAllText(Path.Combine(dest, name), JsonConvert.SerializeObject(m, Formatting.Indented), new UTF8Encoding(false));
                    var src = Path.Combine(dir, name);
                    if (File.Exists(src)) File.Delete(src);
                    ForgetFile(name);
                }
                return dest;
            }
            catch (Exception ex) { Log.Error("Copia de personajes", ex); return null; }
        }

        /// <summary>
        /// One of the campaign's own files, or null when it isn't there. Not strict: an unreadable file is set aside and
        /// null comes back. Strict: a file that is there but unreadable or empty throws, and nothing on disk is touched.
        /// </summary>
        private static T Read<T>(string dir, string file, bool strict = false) where T : class
        {
            var path = Path.Combine(dir, file);
            if (!File.Exists(path)) return null;
            if (strict)
            {
                T got;
                try { got = ReadJson<T>(path); }
                catch (Exception ex) { throw new InvalidDataException(file + ": " + ex.Message, ex); }
                return got ?? throw new InvalidDataException(file + ": vacío");
            }
            try { return ReadJson<T>(path); }
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
