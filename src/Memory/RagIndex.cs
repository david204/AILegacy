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
    /// <summary>Un trozo de vuestra historia: unas cuantas frases seguidas, con su día y su vector de significado.</summary>
    public class RagChunk
    {
        /// <summary>Posición de su última frase en la cuenta total de frases archivadas de ese personaje.</summary>
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
        /// <summary>Cuántas frases archivadas están ya indexadas.</summary>
        [JsonProperty("indexed")] public int Indexed;
        /// <summary>Huella de la última frase indexada: si el archivo del personaje ya no la tiene ahí, es otra rama de la partida.</summary>
        [JsonProperty("tail")] public string Tail;
        [JsonProperty("chunk_size")] public int ChunkSize;
        [JsonProperty("chunks")] public List<RagChunk> Chunks = new List<RagChunk>();
    }

    /// <summary>
    /// Recuerdo por significado. Cada personaje tiene un índice en save_data\[campaña]\rag\[id].json con trozos de las
    /// frases que ya salieron de su historial; cada trozo lleva su texto, así que el recuerdo llega más atrás que el
    /// archivo de frases (que tiene tope). Se indexa en segundo plano cuando hay frases nuevas y se puede borrar: se rehace.
    /// Al hablar, el vector de lo que dices se compara con todos los trozos (son unos cientos: milisegundos).
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

        /// <summary>Lo dicho en un turno archivado, con quién lo dijo.</summary>
        private static string Line(ChatTurn t, string npc, string player) =>
            t.IsNote ? t.Text.Trim() : (t.Role == "player" ? player : npc) + ": " + (t.Text ?? "").Trim();

        /// <summary>
        /// Si hay frases archivadas sin indexar, las trocea y pide sus vectores en segundo plano. No hace nada si no hay
        /// de dónde sacar vectores. Hay que llamarlo en el hilo principal.
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

                // ¿Sigue siendo el mismo hilo de la historia? Si se cargó otra rama (o cambió el tamaño de trozo), se rehace
                int total = mem.ArchivedTotal, have = mem.Archive.Count;
                bool rebuild = f.Indexed > total || f.ChunkSize != size;
                if (!rebuild && f.Indexed > 0)
                {
                    int at = have - (total - f.Indexed) - 1;         // dónde debería estar la última frase indexada
                    if (at >= 0 && at < have && f.Tail != null && f.Tail != Print(mem.Archive[at])) rebuild = true;
                }
                if (rebuild)
                {
                    f.Chunks.Clear(); f.Indexed = Math.Max(0, total - have); f.Tail = null; f.ChunkSize = size;
                    Log.Info($"Índice de recuerdos de {s.Name}: se rehace");
                }
                // Si se archivó más de lo que el archivo conserva, lo que ya no está no se puede indexar: se salta
                if (total - f.Indexed > have) f.Indexed = total - have;

                int pending = total - f.Indexed;
                if (pending < size) return;                          // aún no hay un trozo entero
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
                Embeddings.EmbedAsync(chunks.Select(c => c.Text).ToList(), "embed").ContinueWith(t =>
                {
                    var vecs = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        lock (Busy) Busy.Remove(key);
                        if (session != World.WorldSim.Session || vecs == null || vecs.Length != chunks.Count) return;
                        for (int i = 0; i < chunks.Count; i++) { chunks[i].V = vecs[i]; chunks[i].Vec = Embeddings.ToText(vecs[i]); }
                        f.Chunks.AddRange(chunks);
                        f.Indexed = chunks[chunks.Count - 1].End;
                        f.Tail = tail; f.ChunkSize = size; f.Model = Embeddings.Working;
                        int max = Math.Max(50, S.Get.RagMaxChunks);
                        if (f.Chunks.Count > max) f.Chunks.RemoveRange(0, f.Chunks.Count - max);
                        Write(key, f);
                        Log.Info($"Índice de recuerdos de {s.Name}: {chunks.Count} trozo(s) nuevos, {f.Chunks.Count} en total");
                    });
                });
            }
            catch (Exception ex) { Log.Error("Índice de recuerdos", ex); }
        }

        /// <summary>¿Merece la pena pedir el vector de lo que acaba de decir el jugador? (hay índice, o acciones que elegir)</summary>
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
        /// Pide el vector de la frase del jugador y lo deja en la memoria del personaje para el prompt de este turno.
        /// No espera más de dos segundos y medio: si no llega, el turno sigue con la búsqueda por palabras.
        /// </summary>
        public static Task PrepareQuery(Speaker s, string text)
        {
            var mem = s.Memory;
            mem.QueryVec = null; mem.QueryText = null;
            if (string.IsNullOrWhiteSpace(text) || !WantsQuery(s)) return Task.CompletedTask;
            // Con frases muy cortas («sí», «¿y eso?») ayuda sumar lo último que dijo el personaje
            var lastOwn = mem.Turns.LastOrDefault(t => t.Role != "player");
            string query = text.Trim().Length < 40 && lastOwn != null ? (lastOwn.Text ?? "") + "\n" + text : text;
            int session = World.WorldSim.Session;
            var embed = Embeddings.EmbedAsync(new[] { query }, "embed").ContinueWith(t =>
            {
                var v = t.IsFaulted ? null : t.Result;
                if (v != null && v.Length == 1 && session == World.WorldSim.Session) { mem.QueryVec = v[0]; mem.QueryText = text; }
            });
            return Task.WhenAny(embed, Task.Delay(2500));
        }

        /// <summary>Los trozos de vuestra historia que más se parecen, por significado, a lo que se dice ahora (en orden de tiempo).</summary>
        public static List<string> Find(Speaker s, double today, int max)
        {
            var result = new List<string>();
            try
            {
                var mem = s.Memory;
                if (!s.IsHero || mem.QueryVec == null || max <= 0 || !S.Get.EnableRag) return result;
                var f = Get(s.Key, false);
                if (f == null || f.Chunks.Count == 0) return result;
                var scored = f.Chunks.Where(c => c.V != null).Select(c => (c, score: Embeddings.Cosine(mem.QueryVec, c.V))).OrderByDescending(x => x.score).ToList();
                if (scored.Count == 0) return result;
                double best = scored[0].score;
                if (best < 0.30) return result;                      // nada que se parezca de verdad
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
