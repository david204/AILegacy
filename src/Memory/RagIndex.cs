using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;

namespace AILegacy.Memory
{
    /// <summary>A few consecutive lines of shared history, with their day and embedding vector.</summary>
    public class RagChunk
    {
        /// <summary>Index of its last line in the character's total count of archived lines.</summary>
        [JsonProperty("end")] public int End;
        [JsonProperty("day")] public double Day;
        [JsonProperty("text")] public string Text;
        [JsonProperty("vec")] public string Vec;
        [JsonIgnore] public sbyte[] V;
    }

    public class RagFile
    {
        [JsonProperty("v")] public int Version = 1;
        [JsonProperty("npc")] public string Npc;
        [JsonProperty("model")] public string Model;
        /// <summary>How many archived lines are already indexed.</summary>
        [JsonProperty("indexed")] public int Indexed;
        /// <summary>Fingerprint of the last indexed line. If the character's archive no longer has it there, it's another branch of the save.</summary>
        [JsonProperty("tail")] public string Tail;
        [JsonProperty("chunk_size")] public int ChunkSize;
        [JsonProperty("chunks")] public List<RagChunk> Chunks = new List<RagChunk>();
    }

    /// <summary>
    /// Recall by meaning. Each character has an index in save_data\[campaign]\rag\[id].json with chunks of the lines
    /// that already left their history. Each chunk keeps its text, so recall reaches further back than the
    /// line archive (which is capped). Indexing runs in the background when there are new lines. The file can be deleted, it gets rebuilt.
    /// When you talk, the vector of what you say is compared against every chunk (a few hundred, so milliseconds).
    /// </summary>
    internal static class RagIndex
    {
        private static readonly Dictionary<string, RagFile> Loaded = new Dictionary<string, RagFile>();
        private static readonly HashSet<string> Busy = new HashSet<string>();
        private static string _dir;

        private static string Dir
        {
            get
            {
                var c = Campaign.Current;
                if (c == null || string.IsNullOrEmpty(c.UniqueGameId)) return null;
                string d = Path.Combine(SaveFiles.DirFor(c.UniqueGameId), "rag");
                if (d != _dir) { _dir = d; lock (Loaded) Loaded.Clear(); }
                return d;
            }
        }

        public static void ResetSession() { lock (Loaded) Loaded.Clear(); lock (Busy) Busy.Clear(); }

        private static string FileFor(string dir, string key) =>
            Path.Combine(dir, new string((key ?? "x").Select(c => char.IsLetterOrDigit(c) || c == '_' || c == '-' ? c : '_').ToArray()) + ".json");

        private static RagFile Get(string key, bool create)
        {
            string dir = Dir;
            if (dir == null || key == null) return null;
            lock (Loaded)
            {
                if (Loaded.TryGetValue(key, out var f)) return f;
                try
                {
                    string path = FileFor(dir, key);
                    if (File.Exists(path))
                    {
                        f = JsonConvert.DeserializeObject<RagFile>(File.ReadAllText(path, Encoding.UTF8));
                        if (f != null) foreach (var c in f.Chunks) c.V = Embeddings.FromText(c.Vec);
                    }
                }
                catch (Exception ex) { Log.Warn($"Índice de recuerdos de {key} ilegible, se rehace: {ex.Message}"); f = null; }
                if (f == null && !create) { Loaded[key] = null; return null; }
                if (f == null) f = new RagFile { Npc = key };
                Loaded[key] = f;
                return f;
            }
        }

        private static void Write(string key, RagFile f)
        {
            try
            {
                string dir = Dir;
                if (dir == null) return;
                Directory.CreateDirectory(dir);
                string path = FileFor(dir, key), tmp = path + ".tmp";
                File.WriteAllText(tmp, JsonConvert.SerializeObject(f, Formatting.None), Encoding.UTF8);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
            catch (Exception ex) { Log.Warn($"No se pudo escribir el índice de recuerdos de {key}: {ex.Message}"); }
        }

        private static string Print(ChatTurn t) => t == null ? "" : ((t.Text ?? "").Length + ":" + (t.Text ?? "").GetHashCode().ToString("x") + ":" + (int)(t.Day * 10));

        /// <summary>An archived turn's text, with who said it.</summary>
        private static string Line(ChatTurn t, string npc, string player) =>
            t.IsNote ? t.Text.Trim() : (t.Role == "player" ? player : npc) + ": " + (t.Text ?? "").Trim();

        /// <summary>
        /// Chunks any unindexed archived lines and fetches their vectors in the background. Does nothing if no
        /// provider gives vectors. Main thread only.
        /// </summary>
        public static void Schedule(Speaker s)
        {
            try
            {
                if (s == null || !s.IsHero || !S.Get.EnableRag || !Embeddings.Available) return;
                var mem = s.Memory;
                string key = s.Key;
                int size = Math.Max(2, Math.Min(20, S.Get.RagChunkSize));
                var f = Get(key, true);
                if (f == null) return;
                lock (Busy) { if (Busy.Contains(key)) return; }

                // Vectors from different models can't be compared: re-embed the stored chunk texts with the current one
                string working = Embeddings.Working;
                if (f.Chunks.Count > 0 && working != null && !Embeddings.SameModel(f.Model, working)) { Reembed(s.Name, key, f); return; }

                // Same story thread? If another branch was loaded (or the chunk size changed), rebuild
                int total = mem.ArchivedTotal, have = mem.Archive.Count;
                bool rebuild = f.Indexed > total || f.ChunkSize != size;
                if (!rebuild && f.Indexed > 0)
                {
                    int at = have - (total - f.Indexed) - 1;         // where the last indexed line should be
                    if (at >= 0 && at < have && f.Tail != null && f.Tail != Print(mem.Archive[at])) rebuild = true;
                }
                if (rebuild)
                {
                    f.Chunks.Clear(); f.Indexed = Math.Max(0, total - have); f.Tail = null; f.ChunkSize = size;
                    Log.Info($"Índice de recuerdos de {s.Name}: se rehace");
                }
                // If more was archived than the archive keeps, the missing lines can't be indexed, skip them
                if (total - f.Indexed > have) f.Indexed = total - have;

                int pending = total - f.Indexed;
                if (pending < size) return;                          // not a full chunk yet
                int start = have - pending;
                string npc = s.Name, player = Prompt.ContextBuilder.PlayerNameFor(s) ?? "the stranger";
                var chunks = new List<RagChunk>();
                int pos = f.Indexed;
                for (int i = start; i + size <= have && chunks.Count < 40; i += size)
                {
                    var part = mem.Archive.Skip(i).Take(size).ToList();
                    string text = string.Join("\n", part.Select(t => Line(t, npc, player)));
                    if (text.Length > 1500) text = text.Substring(0, 1500) + "…";
                    pos += size;
                    chunks.Add(new RagChunk { End = pos, Day = part.Where(t => t.Day > 0).Select(t => t.Day).DefaultIfEmpty(-1).Max(), Text = text });
                }
                if (chunks.Count == 0) return;
                string tail = Print(mem.Archive[start + chunks.Count * size - 1]);
                int session = World.WorldSim.Session;
                lock (Busy) Busy.Add(key);
                Task<Embeddings.Result> job;
                try { job = Embeddings.EmbedAsync(chunks.Select(c => c.Text).ToList(), "embed"); }
                catch { lock (Busy) Busy.Remove(key); throw; }
                job.ContinueWith(t =>
                {
                    var r = t.IsFaulted ? null : t.Result;
                    var vecs = r?.Vecs;
                    MainThread.Post(() =>
                    {
                        lock (Busy) Busy.Remove(key);
                        if (session != World.WorldSim.Session || vecs == null || vecs.Length != chunks.Count) return;
                        // A different model answered this time. Don't mix it in, the next pass re-embeds the whole index
                        if (f.Chunks.Count > 0 && !Embeddings.SameModel(f.Model, r.Model)) return;
                        for (int i = 0; i < chunks.Count; i++) { chunks[i].V = vecs[i]; chunks[i].Vec = Embeddings.ToText(vecs[i]); }
                        f.Chunks.AddRange(chunks);
                        f.Indexed = chunks[chunks.Count - 1].End;
                        f.Tail = tail; f.ChunkSize = size; f.Model = r.Model;
                        int max = Math.Max(50, S.Get.RagMaxChunks);
                        if (f.Chunks.Count > max) f.Chunks.RemoveRange(0, f.Chunks.Count - max);
                        Write(key, f);
                        Log.Info($"Índice de recuerdos de {s.Name}: {chunks.Count} trozo(s) nuevos, {f.Chunks.Count} en total");
                    });
                });
            }
            catch (Exception ex) { Log.Error("Índice de recuerdos", ex); }
        }

        /// <summary>Re-embeds every stored chunk with the model now in use. The texts are kept, so no memory is lost.</summary>
        private static void Reembed(string name, string key, RagFile f)
        {
            var chunks = f.Chunks.ToList();
            int session = World.WorldSim.Session;
            lock (Busy) Busy.Add(key);
            Log.Info($"Memory index of {name}: re-embedding {chunks.Count} chunk(s), the vector model changed ({f.Model ?? "?"} -> {Embeddings.Working})");
            Task<Embeddings.Result> job;
            try { job = Embeddings.EmbedAsync(chunks.Select(c => c.Text ?? "").ToList(), "embed"); }
            catch { lock (Busy) Busy.Remove(key); throw; }
            job.ContinueWith(t =>
            {
                var r = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    lock (Busy) Busy.Remove(key);
                    if (session != World.WorldSim.Session || r?.Vecs == null || r.Vecs.Length != chunks.Count) return;
                    for (int i = 0; i < chunks.Count; i++) { chunks[i].V = r.Vecs[i]; chunks[i].Vec = Embeddings.ToText(r.Vecs[i]); }
                    f.Model = r.Model;
                    Write(key, f);
                });
            });
        }

        /// <summary>Is it worth embedding the player's line? Yes if there's an index, or actions to pick from.</summary>
        public static bool WantsQuery(Speaker s)
        {
            try
            {
                if (!S.Get.EnableRag || !Embeddings.Available) return false;
                if (S.Get.EnableActionRag) return true;
                var f = s.IsHero ? Get(s.Key, false) : null;
                return f != null && f.Chunks.Count > 0;
            }
            catch { return false; }
        }

        /// <summary>
        /// Embeds the player's line and stores the vector in the character's memory for this turn's prompt.
        /// Waits 2.5 s at most. If it doesn't arrive, the turn falls back to keyword search.
        /// </summary>
        public static Task PrepareQuery(Speaker s, string text)
        {
            var mem = s.Memory;
            mem.QueryVec = null; mem.QueryText = null; mem.QueryModel = null;
            if (string.IsNullOrWhiteSpace(text) || !WantsQuery(s)) return Task.CompletedTask;
            // Very short lines ("yes", "how so?") work better with the character's last line added
            var lastOwn = mem.Turns.LastOrDefault(t => t.Role != "player");
            string query = text.Trim().Length < 40 && lastOwn != null ? (lastOwn.Text ?? "") + "\n" + text : text;
            int session = World.WorldSim.Session;
            var embed = Embeddings.EmbedAsync(new[] { query }, "embed").ContinueWith(t =>
            {
                var r = t.IsFaulted ? null : t.Result;
                if (r?.Vecs == null || r.Vecs.Length != 1) return;
                // The memory is written on the main thread, where the session can be trusted. It is queued before this
                // task ends, so it lands ahead of whoever waits on the task and then posts its own work.
                MainThread.Post(() =>
                {
                    if (session != World.WorldSim.Session) return;
                    mem.QueryModel = r.Model; mem.QueryVec = r.Vecs[0]; mem.QueryText = text;
                });
            });
            return Task.WhenAny(embed, Task.Delay(2500));
        }

        /// <summary>History chunks closest in meaning to what's being said now, in time order.</summary>
        public static List<string> Find(Speaker s, double today, int max)
        {
            var result = new List<string>();
            try
            {
                var mem = s.Memory;
                if (!s.IsHero || mem.QueryVec == null || max <= 0 || !S.Get.EnableRag) return result;
                var f = Get(s.Key, false);
                if (f == null || f.Chunks.Count == 0 || !Embeddings.SameModel(f.Model, mem.QueryModel)) return result;
                var scored = f.Chunks.Where(c => c.V != null).Select(c => (c, score: Embeddings.Cosine(mem.QueryVec, c.V))).OrderByDescending(x => x.score).ToList();
                if (scored.Count == 0) return result;
                double best = scored[0].score;
                if (best < 0.30) return result;                      // nothing really similar
                foreach (var x in scored.Where(x => x.score >= Math.Max(0.30, best * 0.75)).Take(max).OrderBy(x => x.c.End))
                {
                    string when = x.c.Day <= 0 ? "" : (today - x.c.Day) < 1 ? "[earlier today]\n" : $"[{(int)(today - x.c.Day)} days ago]\n";
                    result.Add(when + x.c.Text);
                }
            }
            catch (Exception ex) { Log.Error("Recuerdo por significado", ex); }
            return result;
        }
    }
}
