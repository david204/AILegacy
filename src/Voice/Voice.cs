using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AILegacy.Config;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;

namespace AILegacy.Voice
{
    /// <summary>
    /// Characters speak their lines aloud. Only what the character says, never the player's line or a name in front.
    /// Engines, none of them a paid middleman: the voices installed in Windows (free, local), OpenAI with the player's own
    /// key, any server that speaks the OpenAI /audio/speech API (a local Kokoro or similar), or Piper run locally.
    /// One line at a time; a new conversation cuts the old one off.
    /// </summary>
    internal static class Speech
    {
        public const int Off = 0, Windows = 1, OpenAi = 2, Endpoint = 3, Piper = 4, Fish = 5;
        public static readonly string[] EngineNames = { "Off", "Windows voices (free, local)", "OpenAI (your key)", "OpenAI-compatible server (local or other)", "Piper (local)", "Fish Audio (your key, free model)" };

        public static int Engine { get { try { return Math.Max(0, Math.Min(5, S.Get.VoiceEngine.SelectedIndex)); } catch { return Off; } } }

        // What went wrong with the voice service, told on screen once per session each (the log has every one)
        private static readonly HashSet<string> Told = new HashSet<string>();

        /// <summary>A new campaign or a load: the voice problems may be told again.</summary>
        public static void ResetSession() { lock (Told) Told.Clear(); }

        private static void Tell(string kind, string msg)
        {
            lock (Told) { if (!Told.Add(kind)) return; }
            Log.Warn("Voice: " + msg);
            Core.MainThread.Post(() =>
            {
                try { TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage("AI Legacy — " + msg, TaleWorlds.Library.Colors.Yellow)); }
                catch { }
            });
        }
        public static bool Enabled => Engine != Off;

        /// <summary>A line made ahead of time by <see cref="Prepare"/>: its audio, and on disk with its mouth cues when the game is to play it.</summary>
        public class Prepared
        {
            internal byte[] Wav; internal string File, Xml; internal double Secs; internal int Gen; internal string Key;
        }

        private class Job
        {
            public int Gen; public string Key; public bool Female; public string Text; public string Tone; public string Voice;
            /// <summary>Who says it in a scene: the voice comes from them (3D) and moves their mouth.</summary>
            public TaleWorlds.MountAndBlade.Agent Agent;
            /// <summary>The person in the open dialogue: the game's conversation plays it when their line shows.</summary>
            public bool Conversation;
            public Prepared Ready;
            public Action<Prepared> Done;
        }

        private static readonly BlockingCollection<Job> Lines = new BlockingCollection<Job>(new ConcurrentQueue<Job>(), 8);
        // Lines made ahead of time (Prepare), on a thread of their own so they never wait behind a line still playing
        private static readonly BlockingCollection<Job> Preps = new BlockingCollection<Job>(new ConcurrentQueue<Job>(), 8);
        private static Thread _prepWorker;
        private static int _gen;
        private static Thread _worker;
        private static System.Media.SoundPlayer _player;
        private static readonly object PlayerGate = new object();
        // Lines queued, being synthesized or playing right now
        private static int _pending;
        // Stopwatch timestamp at which the line playing now ends (0 = nothing is playing)
        private static long _endsAt;
        // The voice and tone of the line being made on this thread (read by the engines)
        [ThreadStatic] private static string _assigned;
        [ThreadStatic] private static string _tone;

        /// <summary>A line is queued, being synthesized or playing. Scene talk waits on this before the next speaker.</summary>
        public static bool IsSpeaking => Volatile.Read(ref _pending) > 0;

        /// <summary>Seconds until the line playing now ends; 0 when nothing is playing (a line still being synthesized counts in IsSpeaking only).</summary>
        public static double SecondsLeft
        {
            get
            {
                long end = Interlocked.Read(ref _endsAt);
                if (end == 0) return 0;
                double s = (end - Stopwatch.GetTimestamp()) / (double)Stopwatch.Frequency;
                return s > 0 ? s : 0;
            }
        }

        /// <summary>Gestures between asterisks, stage notes in brackets and the like are not read out.</summary>
        private static string Clean(string text)
        {
            string t = text ?? "";
            t = Regex.Replace(t, @"\*[^*]{0,200}\*", " ");
            t = Regex.Replace(t, @"\[[^\]]{0,200}\]", " ");
            t = Regex.Replace(t, @"\{[^}]{0,80}\}", " ");
            t = Regex.Replace(t, @"\s+", " ").Trim();
            return t.Length > 600 ? t.Substring(0, 600) : t;
        }

        // ---- the tone of each line ("tts_instructions"), given by the AI with the line itself

        private static readonly ConcurrentDictionary<string, string> Tones = new ConcurrentDictionary<string, string>();

        /// <summary>Notes how a line is to be said, so its voice finds it when the line is spoken (group and scene talk).</summary>
        public static void NoteTone(string text, string tone)
        {
            try
            {
                if (!Enabled || string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(tone)) return;
                if (Tones.Count > 200) Tones.Clear();
                Tones[Clean(text)] = tone.Trim();
            }
            catch { }
        }

        private static string ToneOf(string cleaned) => cleaned != null && Tones.TryRemove(cleaned, out var t) ? t : null;

        /// <summary>For prompts that write several lines: the extra field that says how each is said aloud. Empty when there is no voice.</summary>
        public static string ToneAsk(string language)
        {
            if (!Enabled || !S.Get.VoiceInGroups) return "";
            return $"\nEach line also takes \"tts_instructions\": how it is said aloud, for the voice (mood, emotion, pace and delivery), 20 to 120 characters in {language}.";
        }

        /// <summary>What the voice service is told about the delivery: the tone and the accent of the game's language.</summary>
        private static string Delivery(string tone, AILegacySettings cfg)
        {
            string t = (tone ?? "").Trim();
            string lang = string.IsNullOrWhiteSpace(cfg.Language) ? "English" : cfg.Language.Trim();
            string accent = lang + " accent.";
            if (t.Length == 0) return accent;
            if (t.Length > 200) t = t.Substring(0, 200);
            if (!".!?…。".Contains(t[t.Length - 1])) t += ".";
            return t + " " + accent;
        }

        // ---- each character keeps the voice they were given (saved with the campaign)

        private static readonly List<string> RecentMale = new List<string>(), RecentFemale = new List<string>();

        private static List<string> Split(string list) =>
            (list ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        /// <summary>The voices a character of that sex may get from this engine: the lists in H. Voice, else the engine's own.</summary>
        private static List<string> PoolFor(int engine, bool female, AILegacySettings cfg)
        {
            var list = Split(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices);
            if (list.Count > 0 || engine == Windows || engine == Piper) return list;
            if (engine == OpenAi) return female ? new List<string> { "nova", "shimmer", "coral", "sage", "alloy" } : new List<string> { "onyx", "echo", "ash", "ballad", "verse", "fable" };
            if (engine == Endpoint && (cfg.VoiceModel.Length == 0 || cfg.VoiceModel.IndexOf("kokoro", StringComparison.OrdinalIgnoreCase) >= 0))
                return female ? new List<string> { "af_heart", "af_bella", "af_nicole", "bf_emma" } : new List<string> { "am_adam", "am_michael", "bm_george", "bm_lewis" };
            return list;
        }

        /// <summary>
        /// The voice this character speaks with. Given once, at random among the voices of their sex (not one of the last ten given),
        /// and kept; given again if that voice is no longer in the list or the character is now of the other sex. Null: the engine chooses
        /// (Windows and Piper with no list). Main thread.
        /// </summary>
        private static string Assign(string key, bool female)
        {
            try
            {
                var st = World.WorldSim.State;
                if (st == null || string.IsNullOrEmpty(key)) return null;
                var pool = PoolFor(Engine, female, S.Get);
                if (pool.Count == 0) return null;
                string tag = (female ? "F:" : "M:");
                if (st.Voices.TryGetValue(key, out var had) && had != null)
                {
                    string v = had.Length > 2 ? had.Substring(2) : "";
                    if (had.StartsWith(tag) && pool.Contains(v, StringComparer.OrdinalIgnoreCase)) return v;
                    Log.Info(had.StartsWith(tag) ? $"Voice: voice {v} of {key} no longer exists, replacing it" : $"Voice: {key} is now {(female ? "female" : "male")}; voice {v} will be replaced");
                }
                var recent = female ? RecentFemale : RecentMale;
                var fresh = pool.Where(v => !recent.Contains(v, StringComparer.OrdinalIgnoreCase)).ToList();
                if (fresh.Count == 0) { recent.Clear(); fresh = pool; }
                string pick = fresh[Rng.Next(fresh.Count)];
                recent.Add(pick);
                while (recent.Count > 10) recent.RemoveAt(0);
                if (st.Voices.Count > 3000) st.Voices.Clear();
                st.Voices[key] = tag + pick;
                Log.Info($"Voice: {key} gets the voice {pick}");
                return pick;
            }
            catch (Exception ex) { Log.Error("Voice: assigning a voice", ex); return null; }
        }

        private static readonly Random Rng = new Random();

        /// <summary>
        /// Queue a line. <paramref name="tone"/>: how to say it (the AI's tts_instructions). <paramref name="agent"/>: who says it in a
        /// scene, so it comes from them and moves their mouth. <paramref name="conversation"/>: the person in the open dialogue.
        /// </summary>
        public static void Say(string key, bool female, string text, string tone = null, TaleWorlds.MountAndBlade.Agent agent = null, bool conversation = false)
        {
            try
            {
                if (!Enabled) return;
                string t = Clean(text);
                if (t.Length < 2) return;
                var job = new Job { Gen = _gen, Key = key ?? "", Female = female, Text = t, Tone = tone ?? ToneOf(t), Voice = Assign(key, female), Agent = agent, Conversation = conversation };
                EnsureWorker();
                Interlocked.Increment(ref _pending);
                if (!Lines.TryAdd(job)) Interlocked.Decrement(ref _pending);
            }
            catch (Exception ex) { Log.Error("Voice: enqueue", ex); }
        }

        /// <summary>
        /// Makes the audio of a line without playing it, so the reply and its voice can start together. <paramref name="done"/>
        /// always runs once, on the voice thread: with the line, or null if there is no voice, it failed or the talk changed.
        /// False when nothing was asked (voice off, nothing to say): then <paramref name="done"/> never runs.
        /// </summary>
        public static bool Prepare(string key, bool female, string text, string tone, Action<Prepared> done)
        {
            try
            {
                if (!Enabled || done == null) return false;
                string t = Clean(text);
                if (t.Length < 2) return false;
                var job = new Job { Gen = _gen, Key = key ?? "", Female = female, Text = t, Tone = tone ?? ToneOf(t), Voice = Assign(key, female), Conversation = true, Done = done };
                EnsurePrepWorker();
                Interlocked.Increment(ref _pending);
                if (Preps.TryAdd(job)) return true;
                Interlocked.Decrement(ref _pending);
            }
            catch (Exception ex) { Log.Error("Voice: prepare", ex); }
            return false;
        }

        /// <summary>
        /// Plays a line made by <see cref="Prepare"/>. In an open dialogue it waits for the character's line to show and the game
        /// plays it there, with their mouth moving; elsewhere it plays in turn with the other lines.
        /// </summary>
        public static void PlayPrepared(Prepared p, bool waitForLine = true)
        {
            try
            {
                if (!Enabled || p == null || p.Wav == null || p.Wav.Length < 100 || p.Gen != _gen) return;
                if (waitForLine && p.File != null && ConversationOpen()) { ArmConversation(p); return; }
                EnsureWorker();
                Interlocked.Increment(ref _pending);
                if (!Lines.TryAdd(new Job { Gen = p.Gen, Key = p.Key, Ready = p })) Interlocked.Decrement(ref _pending);
            }
            catch (Exception ex) { Log.Error("Voice: play prepared", ex); }
        }

        private static void EnsurePrepWorker()
        {
            if (_prepWorker != null && _prepWorker.IsAlive) return;
            _prepWorker = new Thread(RunPreps) { IsBackground = true, Name = "AILegacy voice prepare" };
            _prepWorker.SetApartmentState(ApartmentState.STA);
            _prepWorker.Start();
        }

        private static void RunPreps()
        {
            foreach (var job in Preps.GetConsumingEnumerable())
            {
                Prepared ready = null;
                try
                {
                    if (job.Gen == _gen)
                    {
                        var watch = Stopwatch.StartNew();
                        var cfg = S.Get;
                        _assigned = job.Voice; _tone = job.Tone;
                        byte[] wav = Synthesize(Engine, job.Key, job.Female, job.Text, cfg);
                        if (wav != null && wav.Length >= 100)
                        {
                            wav = Scale(wav, Math.Max(0, Math.Min(100, cfg.VoiceVolume)) / 100f);
                            ready = new Prepared { Wav = wav, Secs = Duration(wav), Gen = job.Gen, Key = job.Key };
                            // the dialogue's own voice line needs it on disk, with its mouth cues beside it
                            ready.File = WriteTemp(wav);
                            if (ready.File != null && cfg.VoiceLipSync) ready.Xml = Lips(ready.File);
                            if (ready.File != null && ready.Xml == null) RestCues(ready.File, ready.Secs);
                        }
                        Log.Info($"Voice: line prepared in {watch.Elapsed.TotalSeconds:0.0}s{(ready == null ? " (no audio)" : ready.Xml != null ? " with lip sync" : "")}");
                    }
                    if (job.Gen != _gen) ready = null;
                }
                catch (Exception ex) { ready = null; Log.Warn("Voice: could not prepare the sentence: " + ex.GetBaseException().Message); }
                finally { _assigned = null; _tone = null; Interlocked.Decrement(ref _pending); }
                try { job.Done(ready); } catch (Exception ex) { Log.Error("Voice: after preparing", ex); }
            }
        }

        /// <summary>Whatever is playing or waiting is dropped (a new conversation, the game closing it).</summary>
        public static void Stop()
        {
            Interlocked.Increment(ref _gen);
            while (Lines.TryTake(out _)) Interlocked.Decrement(ref _pending);
            // lines being made ahead are told there is no audio, so whoever waits on them goes on
            while (Preps.TryTake(out var prep)) { Interlocked.Decrement(ref _pending); try { prep.Done?.Invoke(null); } catch { } }
            lock (PlayerGate) { try { _player?.Stop(); } catch { } }
            Interlocked.Exchange(ref _endsAt, 0);
            _armed = null;
            Core.MainThread.Post(StopInGame);
        }

        private static void EnsureWorker()
        {
            if (_worker != null && _worker.IsAlive) return;
            _worker = new Thread(Run) { IsBackground = true, Name = "AILegacy voice" };
            // SAPI wants a single-threaded apartment
            _worker.SetApartmentState(ApartmentState.STA);
            _worker.Start();
        }

        private static void Run()
        {
            foreach (var job in Lines.GetConsumingEnumerable())
            {
                try
                {
                    if (job.Gen != _gen) continue;
                    var cfg = S.Get;
                    byte[] wav; string file = null, xml = null;
                    if (job.Ready != null) { wav = job.Ready.Wav; file = job.Ready.File; xml = job.Ready.Xml; }
                    else
                    {
                        _assigned = job.Voice; _tone = job.Tone;
                        try { wav = Synthesize(Engine, job.Key, job.Female, job.Text, cfg); }
                        finally { _assigned = null; _tone = null; }
                        if (wav != null && wav.Length >= 100) wav = Scale(wav, Math.Max(0, Math.Min(100, cfg.VoiceVolume)) / 100f);
                    }
                    if (wav == null || wav.Length < 100 || job.Gen != _gen) continue;
                    double secs = Duration(wav);

                    // The game plays it when someone in a scene or in the open dialogue says it: from them, with their mouth moving
                    if (job.Agent != null || job.Conversation)
                    {
                        if (file == null)
                        {
                            file = WriteTemp(wav);
                            if (file != null && cfg.VoiceLipSync) xml = Lips(file);
                            if (file != null && xml == null) RestCues(file, secs);
                        }
                        if (file != null)
                        {
                            if (job.Conversation && job.Agent == null)
                            {
                                // the dialogue plays it when the character's line shows (see VoiceLinePatch)
                                var p = new Prepared { Wav = wav, File = file, Xml = xml, Secs = secs, Gen = job.Gen, Key = job.Key };
                                bool armed = false;
                                using (var gate = new ManualResetEventSlim())
                                {
                                    Core.MainThread.Post(() => { try { if (ConversationOpen()) { ArmConversation(p); armed = true; } } finally { gate.Set(); } });
                                    gate.Wait(3000);
                                }
                                if (armed) continue;
                            }
                            else
                            {
                                int started = 0;     // 0 = could not, 1 = playing, 2 = out of earshot (silent, but its time still passes)
                                using (var gate = new ManualResetEventSlim())
                                {
                                    Core.MainThread.Post(() => { try { started = PlayInScene(job, file, xml); } catch (Exception ex) { Log.Warn("Voice: in-scene playback: " + ex.GetBaseException().Message); } finally { gate.Set(); } });
                                    gate.Wait(3000);
                                }
                                if (started > 0)
                                {
                                    Interlocked.Exchange(ref _endsAt, Stopwatch.GetTimestamp() + (long)((secs + 0.15) * Stopwatch.Frequency));
                                    var w = Stopwatch.StartNew();
                                    while (w.Elapsed.TotalSeconds < secs + 0.15 && job.Gen == _gen) Thread.Sleep(50);
                                    Core.MainThread.Post(() => EndInScene(job.Agent));
                                    continue;
                                }
                            }
                        }
                    }

                    using (var ms = new MemoryStream(wav))
                    using (var player = new System.Media.SoundPlayer(ms))
                    {
                        player.Load();
                        // started under the same lock Stop takes, so a Stop can never slip in between the check and the start
                        lock (PlayerGate)
                        {
                            if (job.Gen != _gen) continue;
                            _player = player;
                            player.Play();
                            Interlocked.Exchange(ref _endsAt, Stopwatch.GetTimestamp() + (long)((secs + 0.15) * Stopwatch.Frequency));
                        }
                        var watch = Stopwatch.StartNew();
                        while (watch.Elapsed.TotalSeconds < secs + 0.15 && job.Gen == _gen) Thread.Sleep(50);
                        lock (PlayerGate) { if (job.Gen != _gen) { try { player.Stop(); } catch { } } _player = null; }
                    }
                }
                catch (Exception ex) { Log.Warn("Voice: could not speak the sentence: " + ex.GetBaseException().Message); }
                finally
                {
                    Interlocked.Exchange(ref _endsAt, 0);
                    Interlocked.Decrement(ref _pending);
                }
            }
        }

        // ---- playing through the game itself

        private static readonly string VoiceDir = Path.Combine(Path.GetTempPath(), "AILegacy_voice");
        private static bool _cleaned;

        /// <summary>The line as a WAV file of its own (the game's sound and the lip-sync tool both read files). Old ones are cleared once per run.</summary>
        private static string WriteTemp(byte[] wav)
        {
            try
            {
                Directory.CreateDirectory(VoiceDir);
                if (!_cleaned)
                {
                    _cleaned = true;
                    foreach (var f in Directory.GetFiles(VoiceDir, "tts_*.*")) { try { if (File.GetLastWriteTimeUtc(f) < DateTime.UtcNow.AddMinutes(-10)) File.Delete(f); } catch { } }
                }
                string path = Path.Combine(VoiceDir, "tts_" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".wav");
                File.WriteAllBytes(path, wav);
                return path;
            }
            catch (Exception ex) { Log.Warn("Voice: could not write the line to disk: " + ex.GetBaseException().Message); return null; }
        }

        /// <summary>
        /// Mouth cues with the mouth at rest for the whole line. The game always reads the cues beside a dialogue's sound file,
        /// so a line without lip sync still has them.
        /// </summary>
        private static void RestCues(string wavPath, double secs)
        {
            try
            {
                string d = Math.Max(0.1, secs).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
                File.WriteAllText(Path.ChangeExtension(wavPath, ".xml"),
                    "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<rhubarbResult>\n  <metadata>\n    <soundFile>" + Path.GetFileName(wavPath) + "</soundFile>\n    <duration>" + d +
                    "</duration>\n  </metadata>\n  <mouthCues>\n    <mouthCue start=\"0.00\" end=\"" + d + "\">X</mouthCue>\n  </mouthCues>\n</rhubarbResult>\n");
            }
            catch { }
        }

        /// <summary>rhubarb.exe next to AILegacy.dll (Rhubarb Lip Sync, MIT licence). Null when it is not there.</summary>
        private static string RhubarbExe
        {
            get
            {
                try
                {
                    string dir = Path.GetDirectoryName(typeof(Speech).Assembly.Location);
                    string exe = dir == null ? null : Path.Combine(dir, "rhubarb.exe");
                    return exe != null && File.Exists(exe) ? exe : null;
                }
                catch { return null; }
            }
        }

        private static bool _noRhubarbTold;

        /// <summary>
        /// The mouth cues of a line, in the game's own format (Rhubarb XML beside the sound, same name, .xml), or null.
        /// Phonetic recognition, so it works in any language; at most 30 s.
        /// </summary>
        private static string Lips(string wavPath)
        {
            string exe = RhubarbExe;
            if (exe == null)
            {
                if (!_noRhubarbTold) { _noRhubarbTold = true; Log.Warn("Voice: rhubarb.exe not found next to AILegacy.dll; lines play without mouth movement"); }
                return null;
            }
            string xml = Path.ChangeExtension(wavPath, ".xml");
            try
            {
                var psi = new ProcessStartInfo(exe, $"-o \"{ShortPath(xml)}\" -f xml -r phonetic --machineReadable \"{ShortPath(wavPath)}\"")
                {
                    UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                    WorkingDirectory = Path.GetDirectoryName(exe),
                };
                using (var proc = Process.Start(psi))
                {
                    // read both streams so a full pipe never blocks it
                    var err = proc.StandardError.ReadToEndAsync();
                    var outp = proc.StandardOutput.ReadToEndAsync();
                    if (!proc.WaitForExit(30000)) { try { proc.Kill(); } catch { } Log.Warn("Voice: lip sync took over 30 s, the line plays without it"); return null; }
                    if (proc.ExitCode != 0 || !File.Exists(xml)) { Log.Warn($"Voice: lip sync failed ({proc.ExitCode}): {Short(err.Result)}"); return null; }
                }
                return xml;
            }
            catch (Exception ex) { Log.Warn("Voice: lip sync could not run: " + ex.GetBaseException().Message); return null; }
        }

        [System.Runtime.InteropServices.DllImport("kernel32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern uint GetShortPathName(string longPath, StringBuilder shortPath, uint size);

        /// <summary>The 8.3 form of a path, so a folder name with accents or other letters cannot trip up the tool. The file must exist (or its folder).</summary>
        private static string ShortPath(string path)
        {
            try
            {
                string target = File.Exists(path) ? path : Path.GetDirectoryName(path);
                var sb = new StringBuilder(1024);
                if (GetShortPathName(target, sb, (uint)sb.Capacity) == 0) return path;
                return File.Exists(path) ? sb.ToString() : Path.Combine(sb.ToString(), Path.GetFileName(path));
            }
            catch { return path; }
        }

        // The dialogue's next line of the character, waiting for it to show (main thread)
        private static Prepared _armed;
        private static DateTime _armedAt;

        /// <summary>A map or scene dialogue with someone is open.</summary>
        private static bool ConversationOpen()
        {
            try { var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager; return cm != null && cm.IsConversationInProgress && TaleWorlds.CampaignSystem.CampaignMission.Current != null; }
            catch { return false; }
        }

        private static void ArmConversation(Prepared p)
        {
            _armed = p; _armedAt = DateTime.UtcNow;
        }

        /// <summary>
        /// Called as the dialogue shows a line (VoiceLinePatch): a line of the character's that has no voice of its own takes the one
        /// waiting, so the game plays it at them with their mouth moving. True with the file to play.
        /// </summary>
        internal static bool TakeForLine(out string file)
        {
            file = null;
            try
            {
                var p = _armed;
                if (p == null) return false;
                if (p.Gen != _gen || (DateTime.UtcNow - _armedAt).TotalSeconds > 90 || !File.Exists(p.File)) { _armed = null; return false; }
                var cm = TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager;
                var speaker = cm?.SpeakerAgent;
                if (speaker == null || speaker.Character == null || speaker.Character.IsPlayerCharacter) return false;
                _armed = null;
                file = p.File;
                Hold(p.Secs, p.Gen);
                Log.Info($"Voice: playing the prepared line of {p.Key} in the dialogue{(p.Xml != null ? " with lip sync" : "")}");
                return true;
            }
            catch (Exception ex) { Log.Error("Voice: dialogue line", ex); return false; }
        }

        /// <summary>Set by the dialogue just before it shows the character's reply, so only that line takes the voice.</summary>
        internal static bool ChatLineShowing;

        /// <summary>A line the game plays counts as speaking for its length, like the ones played here.</summary>
        private static void Hold(double secs, int gen)
        {
            Interlocked.Increment(ref _pending);
            Interlocked.Exchange(ref _endsAt, Stopwatch.GetTimestamp() + (long)((secs + 0.15) * Stopwatch.Frequency));
            Task.Delay(TimeSpan.FromSeconds(secs + 0.15)).ContinueWith(_ =>
            {
                Interlocked.Decrement(ref _pending);
                if (gen == _gen) Interlocked.Exchange(ref _endsAt, 0);
            });
        }

        private class LiveVoice { public TaleWorlds.Engine.SoundEvent Ev; public TaleWorlds.MountAndBlade.Agent Agent; public bool Lips; }
        private static readonly List<LiveVoice> Live = new List<LiveVoice>();

        /// <summary>
        /// Plays a scene line from the speaker (main thread): 3D at their head and following them if "3D voice" is on, their mouth
        /// moving if lip sync is on. Out of earshot it is not played. 0 = could not, 1 = playing, 2 = out of earshot.
        /// </summary>
        private static int PlayInScene(Job job, string file, string xml)
        {
            if (job.Gen != _gen) return 0;
            var m = TaleWorlds.MountAndBlade.Mission.Current;
            var a = job.Agent;
            if (m == null || m.Scene == null || a == null || !a.IsActive()) return 0;
            var cfg = S.Get;
            bool spatial = cfg.VoiceSpatial;
            var me = TaleWorlds.MountAndBlade.Agent.Main;
            if (spatial && me != null && me != a && a.Position.Distance(me.Position) > Math.Max(1, cfg.OverhearRadius)) return 2;
            var ev = TaleWorlds.Engine.SoundEvent.CreateEventFromExternalFile("event:/Extra/voiceover", file, m.Scene, spatial, false);
            if (ev == null || ev.IsNullSoundEvent()) return 0;
            ev.SetPosition(a.GetEyeGlobalPosition());
            if (!ev.Play()) return 0;
            bool lips = false;
            if (xml != null) { try { a.AgentVisuals?.StartRhubarbRecord(xml, ev.GetSoundId()); lips = true; } catch (Exception ex) { Log.Warn("Voice: lip sync on the agent: " + ex.GetBaseException().Message); } }
            EndInScene(a);
            Live.Add(new LiveVoice { Ev = ev, Agent = a, Lips = lips });
            return 1;
        }

        /// <summary>The line of that speaker ends: their sound stops and their mouth rests.</summary>
        private static void EndInScene(TaleWorlds.MountAndBlade.Agent a)
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var v = Live[i];
                if (a != null && v.Agent != a) continue;
                try { v.Ev.Stop(); v.Ev.Release(); } catch { }
                if (v.Lips) { try { if (v.Agent != null && v.Agent.IsActive()) v.Agent.AgentVisuals?.StartRhubarbRecord("", -1); } catch { } }
                Live.RemoveAt(i);
            }
        }

        private static void StopInGame() { try { EndInScene(null); } catch { } }

        /// <summary>Every frame of a scene: the voices follow their speakers, and stop if you walk out of earshot.</summary>
        public static void Tick()
        {
            if (Live.Count == 0) return;
            try
            {
                var cfg = S.Get;
                var me = TaleWorlds.MountAndBlade.Agent.Main;
                for (int i = Live.Count - 1; i >= 0; i--)
                {
                    var v = Live[i];
                    if (v.Agent == null || !v.Agent.IsActive() || TaleWorlds.MountAndBlade.Mission.Current == null) { EndInScene(v.Agent); continue; }
                    if (!v.Ev.IsPlaying()) { EndInScene(v.Agent); continue; }
                    v.Ev.SetPosition(v.Agent.GetEyeGlobalPosition());
                    if (cfg.VoiceSpatial && me != null && me != v.Agent && v.Agent.Position.Distance(me.Position) > Math.Max(1, cfg.OverhearRadius) + 2f)
                    {
                        Log.Info($"Voice: voice of {v.Agent.Name} stopped: the player is out of earshot");
                        EndInScene(v.Agent);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Voice: scene tick", ex); Live.Clear(); }
        }

        // stable across sessions, unlike string.GetHashCode
        private static int Hash(string key) { unchecked { int h = 17; foreach (char c in key ?? "") h = h * 31 + c; return h == int.MinValue ? 0 : h; } }

        private static string Pick(string list, string key, string fallback)
        {
            // the voice this character was given (Assign), when the engine has a list to give from
            if (!string.IsNullOrEmpty(_assigned)) return _assigned;
            var voices = (list ?? "").Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries).Select(v => v.Trim()).Where(v => v.Length > 0).ToList();
            if (voices.Count == 0) return fallback;
            return voices[Math.Abs(Hash(key)) % voices.Count];
        }

        private static string LanguageCode(string english)
        {
            string l = (english ?? "").ToLowerInvariant();
            if (l.StartsWith("span")) return "es";
            if (l.StartsWith("fren")) return "fr";
            if (l.StartsWith("germ")) return "de";
            if (l.StartsWith("ital")) return "it";
            if (l.StartsWith("portu") || l.StartsWith("brazil")) return "pt";
            if (l.StartsWith("russ")) return "ru";
            if (l.StartsWith("chin")) return "zh";
            if (l.StartsWith("turk")) return "tr";
            if (l.StartsWith("pol")) return "pl";
            return "en";
        }

        private static byte[] Synthesize(int engine, string key, bool female, string text, AILegacySettings cfg)
        {
            switch (engine)
            {
                case Windows: return FromWindows(key, female, text, cfg);
                case OpenAi:
                {
                    ProviderConfig p = null;
                    try { p = LegacyConfig.Current.Providers.FirstOrDefault(x => x.IsUsable && (x.BaseUrl ?? "").IndexOf("api.openai.com", StringComparison.OrdinalIgnoreCase) >= 0); } catch { }
                    if (p == null) { Log.Warn("Voice: no OpenAI provider with a key in 0. AI Keys"); return null; }
                    string model = cfg.VoiceModel.Length > 0 ? cfg.VoiceModel : "gpt-4o-mini-tts";
                    // the older tts-1 models take no instructions
                    string how = model.StartsWith("tts-1", StringComparison.OrdinalIgnoreCase) ? null : Delivery(_tone, cfg);
                    return FromSpeechApi(p.BaseUrl, p.ApiKey, model, Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, female ? "nova" : "onyx"), text, cfg, how);
                }
                case Endpoint:
                    return FromSpeechApi(cfg.VoiceEndpointUrl, cfg.VoiceEndpointKey, cfg.VoiceModel.Length > 0 ? cfg.VoiceModel : "kokoro",
                        Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, female ? "af_heart" : "am_adam"), text, cfg);
                case Piper: return FromPiper(key, female, text, cfg);
                case Fish: return FromFish(key, female, text, cfg);
            }
            return null;
        }

        // ---- Windows' newer voices (Pablo, Laura, Helena... the ones in Settings > Time & language > Speech).
        // System.Speech on .NET Framework only lists the old "Desktop" voices, which in many languages are all women.
        // SAPI itself can use the newer ones if asked for their registry category, through plain COM.
        private const string OneCoreCategory = @"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Speech_OneCore\Voices";
        private class OneCoreVoice { public string Name; public bool Female; public string Culture; public int Index; }
        private static List<OneCoreVoice> _oneCore;

        private static object ComNew(string progId) => Activator.CreateInstance(Type.GetTypeFromProgID(progId, true));
        private static object ComCall(object o, string name, params object[] args) => o.GetType().InvokeMember(name, System.Reflection.BindingFlags.InvokeMethod, null, o, args);
        private static object ComGet(object o, string name) => o.GetType().InvokeMember(name, System.Reflection.BindingFlags.GetProperty, null, o, null);
        private static void ComSet(object o, string name, object value, bool byRef = false) =>
            o.GetType().InvokeMember(name, byRef ? System.Reflection.BindingFlags.PutRefDispProperty : System.Reflection.BindingFlags.SetProperty, null, o, new[] { value });

        private static object OneCoreTokens()
        {
            var cat = ComNew("SAPI.SpObjectTokenCategory");
            ComCall(cat, "SetId", OneCoreCategory, false);
            return ComCall(cat, "EnumerateTokens", "", "");
        }

        /// <summary>The newer voices, read once. Empty on a Windows without them or if SAPI refuses.</summary>
        private static List<OneCoreVoice> OneCoreVoices()
        {
            if (_oneCore != null) return _oneCore;
            var list = new List<OneCoreVoice>();
            try
            {
                var tokens = OneCoreTokens();
                int n = (int)ComGet(tokens, "Count");
                for (int i = 0; i < n; i++)
                {
                    var t = ComCall(tokens, "Item", i);
                    string culture = "";
                    try
                    {
                        string lcid = ((string)ComCall(t, "GetAttribute", "Language") ?? "").Split(';')[0];
                        culture = new System.Globalization.CultureInfo(Convert.ToInt32(lcid, 16)).Name;
                    }
                    catch { }
                    list.Add(new OneCoreVoice
                    {
                        Name = (string)ComCall(t, "GetDescription", 0),
                        Female = string.Equals((string)ComCall(t, "GetAttribute", "Gender"), "Female", StringComparison.OrdinalIgnoreCase),
                        Culture = culture, Index = i,
                    });
                }
                Log.Info("Voice: Windows voices beyond the Desktop ones: " + (list.Count == 0 ? "none" : string.Join(", ", list.Select(v => $"{v.Name} ({(v.Female ? "F" : "M")})"))));
            }
            catch (Exception ex) { Log.Warn("Voice: the newer Windows voices could not be read: " + ex.GetBaseException().Message); }
            _oneCore = list;
            return list;
        }

        private static byte[] FromOneCore(OneCoreVoice v, string text, AILegacySettings cfg)
        {
            var tokens = OneCoreTokens();
            var voice = ComNew("SAPI.SpVoice");
            ComSet(voice, "Voice", ComCall(tokens, "Item", v.Index), true);
            ComSet(voice, "Rate", Math.Max(-10, Math.Min(10, cfg.VoiceRate)));
            var stream = ComNew("SAPI.SpMemoryStream");
            ComSet(ComGet(stream, "Format"), "Type", 22);          // 22 kHz, 16 bit, mono
            ComSet(voice, "AudioOutputStream", stream, true);
            ComCall(voice, "Speak", text, 0);
            var pcm = ComCall(stream, "GetData") as byte[];
            if (pcm == null || pcm.Length == 0) return null;
            // raw samples: put the WAV header in front so the player can read it
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                const int rate = 22050, bits = 16, channels = 1;
                w.Write(Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length); w.Write(Encoding.ASCII.GetBytes("WAVE"));
                w.Write(Encoding.ASCII.GetBytes("fmt ")); w.Write(16); w.Write((short)1); w.Write((short)channels); w.Write(rate);
                w.Write(rate * channels * bits / 8); w.Write((short)(channels * bits / 8)); w.Write((short)bits);
                w.Write(Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
                w.Flush();
                return ms.ToArray();
            }
        }

        // Windows' own voices: no network, no key
        private static byte[] FromWindows(string key, bool female, string text, AILegacySettings cfg)
        {
            // A newer voice of the right sex and language comes first: the old list often has no man at all
            try
            {
                string lang = LanguageCode(cfg.Language);
                var modern = OneCoreVoices();
                var pool = modern.Where(v => v.Culture.StartsWith(lang, StringComparison.OrdinalIgnoreCase)).ToList();
                if (pool.Count == 0) pool = modern;
                string wanted = Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, null);
                var chosen = (wanted != null ? modern.FirstOrDefault(v => v.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) : null);
                if (chosen == null && wanted == null)
                {
                    var same = pool.Where(v => v.Female == female).ToList();
                    if (same.Count > 0) chosen = same[Math.Abs(Hash(key)) % same.Count];
                }
                if (chosen != null) { var wav = FromOneCore(chosen, text, cfg); if (wav != null) return wav; }
            }
            catch (Exception ex) { Log.Warn("Voice: newer Windows voice failed, using the Desktop ones: " + ex.GetBaseException().Message); }
            using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
            {
                var installed = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
                if (installed.Count == 0) { Log.Warn("Voice: Windows has no voice installed"); return null; }
                var gender = female ? System.Speech.Synthesis.VoiceGender.Female : System.Speech.Synthesis.VoiceGender.Male;
                // the language the characters speak in, so a Spanish line is not read by an English voice
                string lang = LanguageCode(cfg.Language);
                var sameLang = installed.Where(v => v.Culture != null && v.Culture.Name.StartsWith(lang, StringComparison.OrdinalIgnoreCase)).ToList();
                var pool = sameLang.Count > 0 ? sameLang : installed;
                string wanted = Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, null);
                var matching = pool.Where(v => v.Gender == gender).ToList();
                var voice = (wanted != null ? installed.FirstOrDefault(v => v.Name.IndexOf(wanted, StringComparison.OrdinalIgnoreCase) >= 0) : null)
                            ?? (matching.Count > 0 ? matching[Math.Abs(Hash(key)) % matching.Count] : null)
                            ?? pool[Math.Abs(Hash(key)) % pool.Count];
                synth.SelectVoice(voice.Name);
                synth.Rate = Math.Max(-10, Math.Min(10, cfg.VoiceRate));
                using (var ms = new MemoryStream())
                {
                    synth.SetOutputToWaveStream(ms);
                    // a man read by a woman's voice (the only one installed) at least sounds lower
                    if (!female && voice.Gender != gender)
                        synth.SpeakSsml($"<speak version='1.0' xmlns='http://www.w3.org/2001/10/synthesis' xml:lang='{voice.Culture?.Name ?? "en-US"}'><prosody pitch='x-low'>{System.Security.SecurityElement.Escape(text)}</prosody></speak>");
                    else synth.Speak(text);
                    synth.SetOutputToNull();
                    return ms.ToArray();
                }
            }
        }

        // OpenAI's /audio/speech, or any server that copies it
        private static byte[] FromSpeechApi(string baseUrl, string apiKey, string model, string voice, string text, AILegacySettings cfg, string instructions = null)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) { Log.Warn("Voice: voice server address is missing"); return null; }
            var body = new JObject { ["model"] = model, ["voice"] = voice, ["input"] = text, ["response_format"] = "wav" };
            if (cfg.VoiceRate != 0) body["speed"] = Math.Max(0.5, Math.Min(2.0, 1.0 + cfg.VoiceRate / 10.0));
            if (!string.IsNullOrWhiteSpace(instructions)) body["instructions"] = instructions;
            using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/audio/speech"))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                var resp = LlmClient.HttpShared.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode)
                {
                    int code = (int)resp.StatusCode;
                    string said = Encoding.UTF8.GetString(bytes, 0, Math.Min(200, bytes.Length));
                    Log.Warn($"Voice: HTTP {code} {said}");
                    Tell("http" + code, L.F("AIL_e7b1dd40", $"La voz no ha podido hablar: el servicio respondió {code} ({Short(said)}). Revisa la clave, el modelo y las voces en H. Voz."));
                    return null;
                }
                return bytes;
            }
        }

        private static string Short(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length > 120 ? s.Substring(0, 120) + "…" : s;
        }

        // ---- Fish Audio: POST https://api.fish.audio/v1/tts, the model in a "model" header, the voice as reference_id.
        // s2.1-pro-free costs nothing (fair use); s2.1-pro and the others are paid per character.
        private const string FishUrl = "https://api.fish.audio/v1/tts", FishFreeModel = "s2.1-pro-free";

        private static byte[] FromFish(string key, bool female, string text, AILegacySettings cfg)
        {
            string apiKey = (cfg.VoiceFishKey ?? "").Trim();
            if (apiKey.Length == 0) { Tell("fish_key", L.T("AIL_e7b1dd41", "Fish Audio: falta tu clave en H. Voz (se crea en fish.audio, en API Keys).")); return null; }
            string model = (cfg.VoiceModel ?? "").Trim();
            if (model.Length == 0 || model.Equals("gpt-4o-mini-tts", StringComparison.OrdinalIgnoreCase) || model.Equals("kokoro", StringComparison.OrdinalIgnoreCase)) model = FishFreeModel;
            string voice = Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, null);
            var body = new JObject { ["text"] = text, ["format"] = "wav", ["sample_rate"] = 24000, ["latency"] = "normal", ["normalize"] = true };
            if (voice != null) body["reference_id"] = voice;
            if (cfg.VoiceRate != 0) body["prosody"] = new JObject { ["speed"] = Math.Max(0.5, Math.Min(2.0, 1.0 + cfg.VoiceRate / 10.0)) };
            using (var req = new HttpRequestMessage(HttpMethod.Post, FishUrl))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey);
                req.Headers.TryAddWithoutValidation("model", model);
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                var resp = LlmClient.HttpShared.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (resp.IsSuccessStatusCode) return bytes;
                int code = (int)resp.StatusCode;
                string said = Encoding.UTF8.GetString(bytes, 0, Math.Min(300, bytes.Length));
                Log.Warn($"Voice: Fish Audio HTTP {code} (model {model}, voice {voice ?? "default"}): {said}");
                switch (code)
                {
                    case 401: Tell("fish401", L.T("AIL_e7b1dd42", "Fish Audio no acepta tu clave. Cópiala otra vez desde fish.audio (API Keys) en H. Voz.")); break;
                    case 402: Tell("fish402", L.F("AIL_e7b1dd43", $"Fish Audio: no tienes crédito para el modelo {model}. Deja el modelo vacío para usar el gratuito ({FishFreeModel}).")); break;
                    case 400: case 404: Tell("fish404", L.F("AIL_e7b1dd44", $"Fish Audio no encuentra la voz {voice ?? "?"} o el modelo {model}. En las listas de voces van los id de los modelos de voz de fish.audio, separados por comas.")); break;
                    case 429: Tell("fish429", L.T("AIL_e7b1dd45", "Fish Audio: demasiadas peticiones a la vez. Algunas frases se quedarán sin voz.")); break;
                    default: Tell("fish" + code, L.F("AIL_e7b1dd40", $"La voz no ha podido hablar: el servicio respondió {code} ({Short(said)}). Revisa la clave, el modelo y las voces en H. Voz.")); break;
                }
                return null;
            }
        }

        // Piper: an executable and a voice model on disk, text in, WAV out
        private static byte[] FromPiper(string key, bool female, string text, AILegacySettings cfg)
        {
            string exe = (cfg.VoicePiperPath ?? "").Trim().Trim('"');
            string model = Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, null);
            if (exe.Length == 0 || !File.Exists(exe) || model == null || !File.Exists(model)) { Log.Warn("Voice: Piper needs its executable and an .onnx model in the voice lists"); return null; }
            string outFile = Path.Combine(Path.GetTempPath(), "ailegacy_voice_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var psi = new ProcessStartInfo(exe, $"--model \"{model}\" --output_file \"{outFile}\"")
                {
                    // only stdin is redirected: output nobody reads would fill its pipe and stall Piper
                    UseShellExecute = false, RedirectStandardInput = true, CreateNoWindow = true,
                };
                using (var p = Process.Start(psi))
                {
                    var w = new StreamWriter(p.StandardInput.BaseStream, new UTF8Encoding(false));
                    w.Write(text); w.Flush(); w.Close();
                    if (!p.WaitForExit(60000)) { try { p.Kill(); } catch { } return null; }
                }
                return File.Exists(outFile) ? File.ReadAllBytes(outFile) : null;
            }
            finally { try { if (File.Exists(outFile)) File.Delete(outFile); } catch { } }
        }

        /// <summary>Seconds of sound in a WAV, from its byte rate and data size. A long guess if it cannot be read.</summary>
        private static double Duration(byte[] wav)
        {
            try
            {
                if (wav.Length < 44 || Encoding.ASCII.GetString(wav, 0, 4) != "RIFF") return 30;
                int pos = 12, byteRate = 0;
                while (pos + 8 <= wav.Length)
                {
                    string id = Encoding.ASCII.GetString(wav, pos, 4);
                    int size = BitConverter.ToInt32(wav, pos + 4);
                    if (id == "fmt ") byteRate = BitConverter.ToInt32(wav, pos + 16);
                    else if (id == "data")
                    {
                        long bytes = size < 0 ? wav.Length - pos - 8 : Math.Min(size, wav.Length - pos - 8);
                        return byteRate > 0 ? bytes / (double)byteRate : 30;
                    }
                    if (size < 0) break;
                    pos += 8 + size + (size % 2);
                }
            }
            catch { }
            return 30;
        }

        /// <summary>16-bit PCM WAV at a lower volume. Anything else is played as it came.</summary>
        private static byte[] Scale(byte[] wav, float volume)
        {
            try
            {
                if (volume >= 0.999f || wav.Length < 44) return wav;
                if (Encoding.ASCII.GetString(wav, 0, 4) != "RIFF" || Encoding.ASCII.GetString(wav, 8, 4) != "WAVE") return wav;
                int pos = 12, bits = 0, format = 0;
                while (pos + 8 <= wav.Length)
                {
                    string id = Encoding.ASCII.GetString(wav, pos, 4);
                    int size = BitConverter.ToInt32(wav, pos + 4);
                    if (id == "fmt ") { format = BitConverter.ToInt16(wav, pos + 8); bits = BitConverter.ToInt16(wav, pos + 22); }
                    else if (id == "data")
                    {
                        if (format != 1 || bits != 16) return wav;
                        var copy = (byte[])wav.Clone();
                        // a streamed WAV may carry 0xFFFFFFFF (read as -1) for the data size: then it runs to the end of the file
                        long declared = size < 0 ? long.MaxValue : size;
                        int end = (int)Math.Min(copy.Length, pos + 8 + declared);
                        for (int i = pos + 8; i + 1 < end; i += 2)
                        {
                            short s = BitConverter.ToInt16(copy, i);
                            short v = (short)Math.Max(short.MinValue, Math.Min(short.MaxValue, s * volume));
                            copy[i] = (byte)(v & 0xFF); copy[i + 1] = (byte)((v >> 8) & 0xFF);
                        }
                        return copy;
                    }
                    if (size < 0) break;
                    pos += 8 + size + (size % 2);
                }
            }
            catch { }
            return wav;
        }
    }
}
