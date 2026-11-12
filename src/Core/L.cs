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
    /// Idiomas de la interfaz. Los textos van con un identificador y un texto por defecto (en español):
    ///   L.T("AIL_1a2b3c4d", "Texto fijo")
    ///   L.F("AIL_1a2b3c4d", $"Texto con {huecos}")   — la traducción lleva {0}, {1}… en el orden de los huecos
    /// Las traducciones están en el formato normal de Bannerlord: ModuleData\Languages\&lt;CARPETA&gt;\sta_strings.xml
    /// (&lt;string id="…" text="…"/&gt;) con su language_data.xml. El juego las carga solo; además aquí se leen para poder
    /// elegir el idioma en MCM sin cambiar el del juego. Lo que falte en un idioma sale en inglés y, si tampoco, el texto por defecto.
    /// </summary>
    public static class L
    {
        public static readonly string[] Codes = { "auto", "es", "en", "de", "fr", "it", "pt", "ru", "zh" };
        public static readonly string[] Names = { "Auto (game language)", "Español", "English", "Deutsch", "Français", "Italiano", "Português (Brasil)", "Русский", "简体中文" };

        /// <summary>Carpeta de ModuleData\Languages de cada idioma (las mismas siglas que usa el juego).</summary>
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

        /// <summary>Código del idioma en uso ahora: es, en, de, fr, it, pt, ru, zh.</summary>
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

        /// <summary>Nombre en inglés del idioma en uso, para decirle a la IA en qué idioma debe escribir.</summary>
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

        /// <summary>Tabla id → texto de un idioma, leída de su sta_strings.xml (una vez).</summary>
        private static Dictionary<string, string> Table(string code)
        {
            lock (Gate)
            {
                if (Tables.TryGetValue(code, out var t)) return t;
                t = new Dictionary<string, string>();
                // Carpeta propia, NO ModuleData\Languages: si el juego cargara estos archivos, traduciría los ajustes de MCM
                // al idioma del juego aunque en el mod se haya elegido otro. Los textos los pone el mod (SyncGameTexts).
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
                catch (Exception ex) { Log.Warn($"No se pudo leer {path}: {ex.Message}"); }
                Tables[code] = t;
                return t;
            }
        }

        private static string _synced;
        private static System.Reflection.FieldInfo _gameTexts;

        /// <summary>
        /// Pone nuestros textos, en el idioma elegido en AI Legacy, dentro de la tabla de textos del juego. MCM (y cualquier
        /// «{=AIL_…}») los lee de ahí. Enganchar LocalizedTextManager.GetTranslatedText no sirve: es tan corta que el juego la
        /// incrusta al compilar y el enganche no se ejecuta. Se repite si cambia el idioma o si el juego recarga su tabla.
        /// Barato: llamar cada segundo o dos.
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
                // ¿Sigue puesto? (basta mirar uno: si el juego recargó su tabla, ya no coincide)
                string probe = "AIL_dda0e8ca";
                mine.TryGetValue(probe, out string want);
                if (want == null && en != null) en.TryGetValue(probe, out want);
                dict.TryGetValue(probe, out string have);
                if (code == _synced && (want == null || want == have)) return;
                if (en != null) foreach (var kv in en) dict[kv.Key] = kv.Value;
                foreach (var kv in mine) dict[kv.Key] = kv.Value;
                _synced = code;
            }
            catch (Exception ex) { try { Log.Warn("Textos en la tabla del juego: " + ex.Message); } catch { } }
        }

        /// <summary>Traducción del identificador en el idioma en uso (o en inglés), o null.</summary>
        private static string Find(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            string code = Code;
            if (Table(code).TryGetValue(id, out var v)) return v;
            if (code != "en" && code != "es" && Table("en").TryGetValue(id, out v)) return v;
            return null;
        }

        /// <summary>Texto fijo.</summary>
        public static string T(string id, string fallback) => Find(id) ?? fallback;

        /// <summary>Texto con huecos: se traduce la plantilla («Pagas {0} denarios a {1}.») y después se rellenan.</summary>
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

        /// <summary>Para los textos «{=AIL_xxxx}…» del menú de MCM: traducción en el idioma elegido, o null para dejar el texto de origen.</summary>
        internal static string ById_(string id) => Find(id);

        /// <summary>Texto en español → texto en el idioma en uso (para el editor web, que busca por el texto que enseña).</summary>
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
    /// Los textos del menú de ajustes llevan un identificador «{=AIL_xxxx}». El juego pregunta por su traducción aquí:
    /// se contesta con la del idioma elegido en AI Legacy, sea cual sea el idioma del juego.
    /// </summary>
    [HarmonyPatch(typeof(LocalizedTextManager), nameof(LocalizedTextManager.GetTranslatedText))]
    internal static class LocalizedTextPatch
    {
        private static void Postfix(string id, ref string __result)
        {
            try
            {
                if (id == null || id.Length < 5 || id[0] != 'A' || !id.StartsWith("AIL_", StringComparison.Ordinal)) return;
                // Solo se toca si hay traducción: sin ella el juego usa el texto de origen que va tras el identificador
                string tr = L.ById_(id);
                if (tr != null) __result = tr;
            }
            catch { }
        }
    }
}
