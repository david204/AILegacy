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
        public const int Off = 0, Windows = 1, OpenAi = 2, Endpoint = 3, Piper = 4;
        public static readonly string[] EngineNames = { "Off", "Windows voices (free, local)", "OpenAI (your key)", "OpenAI-compatible server (local or other)", "Piper (local)" };

        public static int Engine { get { try { return Math.Max(0, Math.Min(4, S.Get.VoiceEngine.SelectedIndex)); } catch { return Off; } } }
        public static bool Enabled => Engine != Off;

        private static readonly BlockingCollection<(int gen, string key, bool female, string text)> Lines = new BlockingCollection<(int, string, bool, string)>(new ConcurrentQueue<(int, string, bool, string)>(), 8);
        private static int _gen;
        private static Thread _worker;
        private static System.Media.SoundPlayer _player;
        private static readonly object PlayerGate = new object();

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

        /// <summary>Queue a line. <paramref name="key"/> picks the same voice for the same character every time.</summary>
        public static void Say(string key, bool female, string text)
        {
            try
            {
                if (!Enabled) return;
                string t = Clean(text);
                if (t.Length < 2) return;
                EnsureWorker();
                Lines.TryAdd((_gen, key ?? "", female, t));
            }
            catch (Exception ex) { Log.Error("Voz: encolar", ex); }
        }

        /// <summary>Whatever is playing or waiting is dropped (a new conversation, the game closing it).</summary>
        public static void Stop()
        {
            Interlocked.Increment(ref _gen);
            while (Lines.TryTake(out _)) { }
            lock (PlayerGate) { try { _player?.Stop(); } catch { } }
        }

        private static void EnsureWorker()
        {
            if (_worker != null && _worker.IsAlive) return;
            _worker = new Thread(Run) { IsBackground = true, Name = "AILegacy voice" };
            _worker.Start();
        }

        private static void Run()
        {
            foreach (var job in Lines.GetConsumingEnumerable())
            {
                try
                {
                    if (job.gen != _gen) continue;
                    var cfg = S.Get;
                    byte[] wav = Synthesize(Engine, job.key, job.female, job.text, cfg);
                    if (wav == null || wav.Length < 100 || job.gen != _gen) continue;
                    wav = Scale(wav, Math.Max(0, Math.Min(100, cfg.VoiceVolume)) / 100f);
                    using (var ms = new MemoryStream(wav))
                    {
                        var player = new System.Media.SoundPlayer(ms);
                        lock (PlayerGate) _player = player;
                        if (job.gen == _gen) player.PlaySync();
                        lock (PlayerGate) _player = null;
                    }
                }
                catch (Exception ex) { Log.Warn("Voz: no se pudo decir la frase: " + ex.GetBaseException().Message); }
            }
        }

        // stable across sessions, unlike string.GetHashCode
        private static int Hash(string key) { unchecked { int h = 17; foreach (char c in key ?? "") h = h * 31 + c; return h == int.MinValue ? 0 : h; } }

        private static string Pick(string list, string key, string fallback)
        {
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
                    if (p == null) { Log.Warn("Voz: no hay ningún proveedor de OpenAI con clave en 0. Claves de IA"); return null; }
                    return FromSpeechApi(p.BaseUrl, p.ApiKey, cfg.VoiceModel.Length > 0 ? cfg.VoiceModel : "gpt-4o-mini-tts",
                        Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, female ? "nova" : "onyx"), text, cfg);
                }
                case Endpoint:
                    return FromSpeechApi(cfg.VoiceEndpointUrl, cfg.VoiceEndpointKey, cfg.VoiceModel.Length > 0 ? cfg.VoiceModel : "kokoro",
                        Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, female ? "af_heart" : "am_adam"), text, cfg);
                case Piper: return FromPiper(key, female, text, cfg);
            }
            return null;
        }

        // Windows' own voices: no network, no key
        private static byte[] FromWindows(string key, bool female, string text, AILegacySettings cfg)
        {
            using (var synth = new System.Speech.Synthesis.SpeechSynthesizer())
            {
                var installed = synth.GetInstalledVoices().Where(v => v.Enabled).Select(v => v.VoiceInfo).ToList();
                if (installed.Count == 0) { Log.Warn("Voz: Windows no tiene ninguna voz instalada"); return null; }
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
        private static byte[] FromSpeechApi(string baseUrl, string apiKey, string model, string voice, string text, AILegacySettings cfg)
        {
            if (string.IsNullOrWhiteSpace(baseUrl)) { Log.Warn("Voz: falta la dirección del servidor de voz"); return null; }
            var body = new JObject { ["model"] = model, ["voice"] = voice, ["input"] = text, ["response_format"] = "wav" };
            if (cfg.VoiceRate != 0) body["speed"] = Math.Max(0.5, Math.Min(2.0, 1.0 + cfg.VoiceRate / 10.0));
            using (var req = new HttpRequestMessage(HttpMethod.Post, baseUrl.TrimEnd('/') + "/audio/speech"))
            using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
            {
                if (!string.IsNullOrWhiteSpace(apiKey)) req.Headers.TryAddWithoutValidation("Authorization", "Bearer " + apiKey.Trim());
                req.Content = new StringContent(body.ToString(), Encoding.UTF8, "application/json");
                var resp = LlmClient.HttpShared.SendAsync(req, cts.Token).GetAwaiter().GetResult();
                var bytes = resp.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                if (!resp.IsSuccessStatusCode) { Log.Warn($"Voz: HTTP {(int)resp.StatusCode} {Encoding.UTF8.GetString(bytes, 0, Math.Min(200, bytes.Length))}"); return null; }
                return bytes;
            }
        }

        // Piper: an executable and a voice model on disk, text in, WAV out
        private static byte[] FromPiper(string key, bool female, string text, AILegacySettings cfg)
        {
            string exe = (cfg.VoicePiperPath ?? "").Trim().Trim('"');
            string model = Pick(female ? cfg.VoiceFemaleVoices : cfg.VoiceMaleVoices, key, null);
            if (exe.Length == 0 || !File.Exists(exe) || model == null || !File.Exists(model)) { Log.Warn("Voz: Piper necesita su ejecutable y un modelo .onnx en las listas de voces"); return null; }
            string outFile = Path.Combine(Path.GetTempPath(), "ailegacy_voice_" + Guid.NewGuid().ToString("N") + ".wav");
            try
            {
                var psi = new ProcessStartInfo(exe, $"--model \"{model}\" --output_file \"{outFile}\"")
                {
                    UseShellExecute = false, RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true,
                    StandardOutputEncoding = Encoding.UTF8,
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
                        int end = Math.Min(copy.Length - 1, pos + 8 + (size < 0 || size == int.MaxValue ? copy.Length : size));
                        for (int i = pos + 8; i + 1 <= end; i += 2)
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
