using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Engine;
using Path = System.IO.Path;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// The player's own music during a conversation. The mod ships no audio: whatever .ogg files are dropped into
    /// Modules\AILegacy\music\[moment]\ get played, one at a time, picked by how the talk is going (talk, tense,
    /// romance, sad, victory). The game's music is turned down while a track plays and back up afterwards.
    /// Files are played with the engine's own external-file sound event, the one the game uses for its voiced lines.
    /// </summary>
    internal static class TalkMusic
    {
        public static readonly string[] Moments = { "talk", "tense", "romance", "sad", "victory" };

        // FMOD event the game plays its .ogg voiced lines through (SandBox conversation and notification code)
        private const string ProgrammerEvent = "event:/Extra/voiceover";
        private const float FadeSeconds = 1.5f;
        private const float MinSecondsBeforeSwitch = 20f;

        public static string Root => Path.Combine(Paths.ModuleDir, "music");

        private static SoundEvent _sound;
        private static string _folder;
        private static int _shortRuns;
        private static string _lastFile;
        private static float _playedFor;
        private static float _poll;
        private static bool _ducked;
        private static float _savedVolume = 1f;
        private static float _volume = 1f;
        private static float _targetVolume = 1f;
        private static bool _foldersReady;
        private static bool _broken;
        private static readonly Random Rng = new Random();
        private static readonly Dictionary<string, string[]> Files = new Dictionary<string, string[]>();
        private static DateTime _filesAt;

        /// <summary>Every frame. With the option off and nothing playing it returns at once.</summary>
        public static void Tick(float dt)
        {
            if (_broken)
            {
                // Given up for this session, but never with the game's music left down
                if (_ducked || _sound != null) Silence();
                return;
            }
            bool on = S.Get.EnableTalkMusic;
            if (!on && _sound == null && !_ducked) return;
            try
            {
                Fade(dt);
                if (_sound != null) _playedFor += dt;
                _poll -= dt;
                if (_poll > 0f) return;
                _poll = 0.5f;

                // A talk that was closed but is still referenced counts as over
                var session = on && ChatSession.IsBusy ? ChatSession.Active : null;
                var who = session?.Who;
                if (who == null || Campaign.Current == null) { StopTrack(); return; }
                EnsureFolders();

                string wanted = MomentFor(who);
                // A moment with an empty folder falls back to the "talk" folder
                string folder = FilesFor(wanted).Length > 0 ? wanted : FilesFor("talk").Length > 0 ? "talk" : null;
                if (folder == null) { StopTrack(); return; }
                bool playing = _sound != null && _sound.IsValid && _sound.IsPlaying();
                if (playing && (folder == _folder || _playedFor < MinSecondsBeforeSwitch)) return;
                // A file the engine drops at once would otherwise be started again twice a second
                if (!playing && _sound != null && _playedFor < 3f && ++_shortRuns >= 3)
                {
                    Log.Warn("Música propia: las pistas se cortan nada más empezar; se deja de intentar en esta sesión");
                    _broken = true;
                    Silence();
                    return;
                }
                // Track over, or the talk has turned: next one
                Start(folder, wanted);
            }
            catch (Exception ex)
            {
                Log.Error("Música propia", ex);
                _broken = true;                 // never retry every frame
                Silence();
            }
        }

        /// <summary>Leaving the campaign: nothing of ours keeps sounding and the game's music is back at its level.</summary>
        public static void Shutdown() => Silence();

        /// <summary>A campaign was loaded, started or swapped in: the talk that was playing music is gone with the old one.</summary>
        public static void ResetSession()
        {
            Silence();
            _shortRuns = 0;
            _playedFor = 0f;
            _poll = 0f;
        }

        /// <summary>
        /// Track off and the game's music back at once, with no fade. Each half is tried on its own, so a failure
        /// releasing the track cannot leave the volume down.
        /// </summary>
        private static void Silence()
        {
            try { StopTrack(); } catch { _sound = null; _folder = null; }
            try { RestoreNow(); } catch { _ducked = false; }
        }

        // ------------------------------------------------------------ which moment

        private static readonly string[] SadWords = { "sad", "grief", "griev", "mourn", "sorrow", "melanchol", "weary", "despair", "lonely", "afraid", "fear", "hurt", "ashamed", "heartbroken", "resigned", "trist", "apenad", "afligid", "luto", "abatid", "desolad", "asustad", "dolid" };
        private static readonly string[] GladWords = { "triumph", "elated", "joy", "proud", "jubilant", "exultant", "delighted", "victorious", "euphoric", "happy", "cheerful", "triunf", "eufóric", "euforic", "orgullos", "alegr", "content", "feliz", "exultante" };
        private static readonly string[] AngryWords = { "angry", "furious", "hostile", "threat", "enraged", "outraged", "wrath", "seething", "irate", "furios", "enfurecid", "hostil", "amenaz", "airad", "colérico", "colerico", "rabios" };

        private static bool Has(string text, string[] words)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var w in words) if (text.IndexOf(w, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            return false;
        }

        /// <summary>Tension first, then romance, then the mood they showed and what weighs on them these days.</summary>
        internal static string MomentFor(Speaker s)
        {
            var cfg = S.Get;
            HeroMemory mem = s.Memory;
            if (mem == null) return "talk";
            string mood = (mem.Mood ?? "") + " " + (mem.MoodReason ?? "");
            if ((cfg.EnableTension && Bonds.Tension(mem) >= cfg.TalkMusicTenseFrom) || Has(mem.Mood, AngryWords)) return "tense";
            if (Bonds.RomancePossible(s) && mem.Romance >= cfg.TalkMusicRomanceFrom) return "romance";
            int score = 0;
            try
            {
                foreach (var c in World.Mood.Describe(s))
                    if (c.StartsWith("+")) score++; else if (c.StartsWith("−")) score--;
            }
            catch { }
            if (Has(mood, SadWords) || score <= -2) return "sad";
            if (Has(mood, GladWords) || score >= 2) return "victory";
            return "talk";
        }

        // ------------------------------------------------------------ files

        private static void EnsureFolders()
        {
            if (_foldersReady) return;
            _foldersReady = true;
            try
            {
                foreach (var m in Moments) Directory.CreateDirectory(Path.Combine(Root, m));
            }
            catch (Exception ex) { Log.Warn("Música propia: no se pudieron crear las carpetas: " + ex.Message); }
        }

        private static string[] FilesFor(string moment)
        {
            // The folders are read again every 30 seconds, so files can be added with the game open
            if ((DateTime.UtcNow - _filesAt).TotalSeconds > 30) { Files.Clear(); _filesAt = DateTime.UtcNow; }
            if (Files.TryGetValue(moment, out var list)) return list;
            try
            {
                var dir = Path.Combine(Root, moment);
                list = Directory.Exists(dir) ? Directory.GetFiles(dir, "*.ogg").OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToArray() : new string[0];
            }
            catch { list = new string[0]; }
            Files[moment] = list;
            return list;
        }

        // ------------------------------------------------------------ playing

        private static void Start(string folder, string moment)
        {
            var list = FilesFor(folder);
            if (list.Length == 0) return;
            string file = list.Length == 1 ? list[0] : list.Where(f => f != _lastFile).OrderBy(_ => Rng.Next()).First();
            ReleaseSound();
            var ev = SoundEvent.CreateEventFromExternalFile(ProgrammerEvent, file, null, false, false);
            if (ev == null || !ev.IsValid || !ev.Play())
            {
                Log.Warn("Música propia: el juego no ha podido reproducir " + Path.GetFileName(file));
                try { ev?.Release(); } catch { }
                _poll = 5f;
                return;
            }
            if (_playedFor >= 3f) _shortRuns = 0;
            _sound = ev;
            _folder = folder;
            _lastFile = file;
            _playedFor = 0f;
            Duck();
            Log.Info($"[música] {moment}{(folder != moment ? " (carpeta " + folder + ")" : "")}: {Path.GetFileName(file)}");
        }

        private static void ReleaseSound()
        {
            var ev = _sound;
            _sound = null;
            if (ev == null) return;
            try { if (ev.IsValid) ev.Release(); } catch (Exception ex) { Log.Warn("Música propia: al soltar la pista: " + ex.Message); }
        }

        private static void StopTrack()
        {
            if (_sound != null) ReleaseSound();
            _folder = null;
            if (_ducked) _targetVolume = _savedVolume;
        }

        // ------------------------------------------------------------ the game's music volume

        // The game's music goes through psai (TaleWorlds.PSAI.dll), which has its own master volume on top of the
        // player's music option. Reached by reflection so the mod keeps loading if that assembly ever changes.
        private static object _psai;
        private static MethodInfo _getVolume, _setVolume;
        private static bool _psaiLooked;

        private static bool Psai()
        {
            if (_psaiLooked) return _psai != null;
            _psaiLooked = true;
            try
            {
                var t = HarmonyLib.AccessTools.TypeByName("psai.net.PsaiCore");
                _psai = t?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                _getVolume = t?.GetMethod("GetVolume", Type.EmptyTypes);
                _setVolume = t?.GetMethod("SetVolume", new[] { typeof(float) });
                if (_psai == null || _getVolume == null || _setVolume == null) { _psai = null; Log.Warn("Música propia: no se encuentra el control de volumen de la música del juego; no se bajará"); }
            }
            catch (Exception ex) { _psai = null; Log.Warn("Música propia: " + ex.Message); }
            return _psai != null;
        }

        private static void Duck()
        {
            if (!Psai()) return;
            if (!_ducked)
            {
                _savedVolume = (float)_getVolume.Invoke(_psai, null);
                if (_savedVolume <= 0f) _savedVolume = 1f;
                _volume = _savedVolume;
                _ducked = true;
            }
            _targetVolume = _savedVolume * Math.Max(0, Math.Min(100, S.Get.TalkMusicGameVolumePercent)) / 100f;
        }

        private static void Fade(float dt)
        {
            if (!_ducked || _psai == null) return;
            if (Math.Abs(_volume - _targetVolume) > 0.001f)
            {
                float step = _savedVolume * dt / FadeSeconds;
                _volume = _volume < _targetVolume ? Math.Min(_targetVolume, _volume + step) : Math.Max(_targetVolume, _volume - step);
                _setVolume.Invoke(_psai, new object[] { _volume });
            }
            else if (_sound == null && Math.Abs(_targetVolume - _savedVolume) <= 0.001f)
                _ducked = false;            // back where it was
        }

        private static void RestoreNow()
        {
            if (!_ducked || _psai == null) { _ducked = false; return; }
            _ducked = false;
            _volume = _targetVolume = _savedVolume;
            _setVolume.Invoke(_psai, new object[] { _savedVolume });
        }
    }
}
