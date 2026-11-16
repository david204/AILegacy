using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Xml;
using HarmonyLib;
using TaleWorlds.Localization;

namespace AILegacy.Core
{
    /// <summary>
    /// UI languages. Each text has an id and a default text (in Spanish):
    ///   L.T("AIL_1a2b3c4d", "Fixed text")
    ///   L.F("AIL_1a2b3c4d", $"Text with {holes}")   (the translation uses {0}, {1}... in the order of the holes)
    /// Translations use the normal Bannerlord format in ModuleData\AILegacyTexts\&lt;FOLDER&gt;\sta_strings.xml
    /// (&lt;string id="..." text="..."/&gt;). They're read here so the language can be
    /// picked in MCM without changing the game's. Anything missing in a language falls back to English, then to the default text.
    /// </summary>
    public static class L
    {
        public static readonly string[] Codes = { "auto", "es", "en", "de", "fr", "it", "pt", "ru", "zh" };
        public static readonly string[] Names = { "Auto (game language)", "Español", "English", "Deutsch", "Français", "Italiano", "Português (Brasil)", "Русский", "简体中文" };

        /// <summary>Language folder name, same codes the game uses.</summary>
        private static string Folder(string code)
        {
            switch (code)
            {
                case "es": return "SP";
                case "de": return "DE";
                case "fr": return "FR";
                case "it": return "IT";
                case "pt": return "BR";
                case "ru": return "RU";
                case "zh": return "CNs";
                default: return "EN";
            }
        }

        private static readonly object Gate = new object();
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        private static string _cached;
        private static DateTime _cachedAt = DateTime.MinValue;

        /// <summary>Current language code: es, en, de, fr, it, pt, ru, zh.</summary>
        public static string Code
        {
            get
            {
                var now = DateTime.UtcNow;
                lock (CacheGate) { if (_cached != null && (now - _cachedAt).TotalSeconds < 3) return _cached; }
                string c = "auto";
                try { c = Settings.S.Get.UiLanguageCode(); } catch { }
                if (c == "auto") c = FromGame();
                lock (CacheGate) { _cached = c; _cachedAt = now; }
                return c;
            }
        }

        /// <summary>English name of the current language, to tell the AI what language to write in.</summary>
        public static string EnglishName
        {
            get
            {
                switch (Code)
                {
                    case "es": return "Spanish (Spain)";
                    case "de": return "German";
                    case "fr": return "French";
                    case "it": return "Italian";
                    case "pt": return "Brazilian Portuguese";
                    case "ru": return "Russian";
                    case "zh": return "Simplified Chinese";
                    default: return "English";
                }
            }
        }

        private static string FromGame()
        {
            try
            {
                string g = (TaleWorlds.MountAndBlade.BannerlordConfig.Language ?? "").ToLowerInvariant();
                if (g.Contains("espa") || g.Contains("spanish")) return "es";
                if (g.Contains("deutsch") || g.Contains("german")) return "de";
                if (g.Contains("fran") || g.Contains("french")) return "fr";
                if (g.Contains("ital")) return "it";
                if (g.Contains("portugu")) return "pt";
                if (g.Contains("русск") || g.Contains("russian")) return "ru";
                if (g.Contains("中文") || g.Contains("简体") || g.Contains("繁體") || g.Contains("chinese")) return "zh";
            }
            catch { }
            return "en";
        }

        /// <summary>id to text table for one language, read once from its sta_strings.xml.</summary>
        private static Dictionary<string, string> Table(string code)
        {
            lock (Gate)
            {
                if (Tables.TryGetValue(code, out var t)) return t;
                t = new Dictionary<string, string>();
                // Our own folder, not ModuleData\Languages. If the game loaded these files it would translate the MCM settings
                // into the game's language even when another one is picked in the mod. The mod injects the texts itself (SyncGameTexts).
                string path = Path.Combine(Paths.ModuleDir, "ModuleData", "AILegacyTexts", Folder(code), "sta_strings.xml");
                try
                {
                    if (File.Exists(path))
                    {
                        var doc = new XmlDocument();
                        doc.Load(path);
                        foreach (XmlNode n in doc.GetElementsByTagName("string"))
                        {
                            string id = n.Attributes?["id"]?.Value, text = n.Attributes?["text"]?.Value;
                            if (!string.IsNullOrEmpty(id) && !string.IsNullOrEmpty(text)) t[id] = text;
                        }
                    }
                }
                catch (Exception ex) { Log.Warn($"Could not read {path}: {ex.Message}"); }
                Tables[code] = t;
                return t;
            }
        }

        private static string _synced;
        private static System.Reflection.FieldInfo _gameTexts;

        /// <summary>
        /// Puts our texts, in the language picked in AI Legacy, into the game's text table. MCM (and any
        /// "{=AIL_...}") reads them from there. Patching LocalizedTextManager.GetTranslatedText isn't enough: it's so short
        /// it gets inlined and the patch never runs. Redone when the language changes or the game reloads its table.
        /// Cheap, fine to call every second or two.
        /// </summary>
        public static void SyncGameTexts()
        {
            try
            {
                string code = Code;
                if (_gameTexts == null)
                    _gameTexts = typeof(LocalizedTextManager).GetField("_gameTextDictionary", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                var dict = _gameTexts?.GetValue(null) as Dictionary<string, string>;
                if (dict == null) return;
                var mine = Table(code);
                var en = code == "en" || code == "es" ? null : Table("en");
                // Still there? Checking one id is enough, it won't match if the game reloaded its table
                string probe = "AIL_dda0e8ca";
                mine.TryGetValue(probe, out string want);
                if (want == null && en != null) en.TryGetValue(probe, out want);
                dict.TryGetValue(probe, out string have);
                if (code == _synced && (want == null || want == have)) return;
                if (en != null) foreach (var kv in en) dict[kv.Key] = kv.Value;
                foreach (var kv in mine) dict[kv.Key] = kv.Value;
                _synced = code;
            }
            catch (Exception ex) { try { Log.Warn("Texts in the game table: " + ex.Message); } catch { } }
        }

        /// <summary>Translation in the current language (or English), or null.</summary>
        private static string Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string code = Code;
            if (Table(code).TryGetValue(id, out var v)) return v;
            if (code != "en" && code != "es" && Table("en").TryGetValue(id, out v)) return v;
            return null;
        }

        public static string T(string id, string fallback) => Find(id) ?? fallback;

        /// <summary>Translates the format template ("Pagas {0} denarios a {1}.") and then fills in the holes.</summary>
        public static string F(string id, FormattableString text)
        {
            if (text == null) return "";
            try
            {
                string tr = Find(id);
                if (tr == null) return text.ToString();
                return string.Format(CultureInfo.CurrentCulture, tr, text.GetArguments());
            }
            catch { return text.ToString(); }
        }

        /// <summary>For "{=AIL_xxxx}..." texts in the MCM menu: translation in the picked language, or null to keep the source text.</summary>
        internal static string ById_(string id) => Find(id);

        /// <summary>Spanish text to current-language text, for the web editor (it looks texts up by what it displays).</summary>
        public static Dictionary<string, string> Current()
        {
            var res = new Dictionary<string, string>();
            if (Code == "es") return res;
            foreach (var kv in Table("es"))
            {
                string tr = Find(kv.Key);
                if (tr != null) res[kv.Value] = tr;
            }
            return res;
        }
    }

    /// <summary>
    /// Settings menu texts carry an "{=AIL_xxxx}" id. When the game asks for the translation, answer with the
    /// language picked in AI Legacy, whatever the game's language is.
    /// </summary>
    [HarmonyPatch(typeof(LocalizedTextManager), nameof(LocalizedTextManager.GetTranslatedText))]
    [HarmonyPatchCategory(SubModule.EarlyPatches)]
    internal static class LocalizedTextPatch
    {
        private static void Postfix(string id, ref string __result)
        {
            try
            {
                if (id == null || id.Length < 5 || id[0] != 'A' || !id.StartsWith("AIL_", StringComparison.Ordinal)) return;
                // Only override when there's a translation. Without one the game uses the source text after the id
                string tr = L.ById_(id);
                if (tr != null) __result = tr;
            }
            catch { }
        }
    }
}
