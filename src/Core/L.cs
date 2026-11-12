using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using HarmonyLib;
using Newtonsoft.Json;
using TaleWorlds.Localization;

namespace AILegacy.Core
{
    /// <summary>
    /// Idiomas de la interfaz de AI Legacy. El texto de origen del código está en español; cada idioma es un archivo
    /// lang\&lt;código&gt;.json con «texto en español» → «traducción». Lo que no esté traducido cae al inglés y, si tampoco,
    /// se enseña en español. El idioma se elige en MCM (o «Auto»: el del juego).
    ///   L.T("texto fijo")            — texto sin huecos
    ///   L.F($"texto con {huecos}")   — texto con huecos: se traduce la plantilla y luego se rellenan
    /// </summary>
    public static class L
    {
        public static readonly string[] Codes = { "auto", "es", "en", "de", "fr", "it", "pt", "ru", "zh" };
        public static readonly string[] Names = { "Auto (game language)", "Español", "English", "Deutsch", "Français", "Italiano", "Português (Brasil)", "Русский", "简体中文" };

        private static readonly object Gate = new object();
        private static readonly object CacheGate = new object();
        private static readonly Dictionary<string, Dictionary<string, string>> Tables = new Dictionary<string, Dictionary<string, string>>();
        private static readonly Dictionary<string, Dictionary<string, string>> ById = new Dictionary<string, Dictionary<string, string>>();
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

        /// <summary>El mismo resumen que usa el generador de identificadores: FNV-1a de 32 bits sobre UTF-8.</summary>
        public static string Id(string spanish)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (byte b in Encoding.UTF8.GetBytes(spanish ?? "")) { h ^= b; h *= 16777619; }
                return "AIL_" + h.ToString("x8");
            }
        }

        private static Dictionary<string, string> Table(string code)
        {
            lock (Gate)
            {
                if (Tables.TryGetValue(code, out var t)) return t;
                t = new Dictionary<string, string>();
                try
                {
                    var path = Path.Combine(Paths.ModuleDir, "lang", code + ".json");
                    if (File.Exists(path))
                        t = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(path, Encoding.UTF8)) ?? t;
                }
                catch (Exception ex) { Log.Warn($"lang\\{code}.json no se pudo leer: {ex.Message}"); }
                Tables[code] = t;
                var ids = new Dictionary<string, string>();
                foreach (var kv in t) if (!string.IsNullOrEmpty(kv.Value)) ids[Id(kv.Key)] = kv.Value;
                ById[code] = ids;
                return t;
            }
        }

        private static string Find(string spanish)
        {
            string code = Code;
            if (code == "es" || string.IsNullOrEmpty(spanish)) return null;
            if (Table(code).TryGetValue(spanish, out var v) && !string.IsNullOrEmpty(v)) return v;
            if (code != "en" && Table("en").TryGetValue(spanish, out v) && !string.IsNullOrEmpty(v)) return v;
            return null;
        }

        /// <summary>Texto fijo.</summary>
        public static string T(string spanish) => Find(spanish) ?? spanish;

        /// <summary>Texto con huecos: se traduce la plantilla («Pagas {0} denarios a {1}.») y después se rellenan.</summary>
        public static string F(FormattableString text)
        {
            if (text == null) return "";
            try
            {
                string tr = Find(text.Format);
                if (tr == null) return text.ToString();
                return string.Format(CultureInfo.CurrentCulture, tr, text.GetArguments());
            }
            catch { return text.ToString(); }
        }

        /// <summary>Para los textos «{=AIL_xxxx}…» (menú de MCM): traducción por identificador, o null para dejar el texto de origen.</summary>
        internal static string ById_(string id)
        {
            string code = Code;
            if (code == "es") return null;
            Table(code);
            lock (Gate)
            {
                if (ById.TryGetValue(code, out var d) && d.TryGetValue(id, out var v)) return v;
            }
            if (code != "en")
            {
                Table("en");
                lock (Gate) { if (ById.TryGetValue("en", out var d) && d.TryGetValue(id, out var v)) return v; }
            }
            return null;
        }

        /// <summary>Todo el diccionario del idioma en uso (para el editor web).</summary>
        public static Dictionary<string, string> Current()
        {
            string code = Code;
            var res = new Dictionary<string, string>();
            if (code == "es") return res;
            if (code != "en") foreach (var kv in Table("en")) res[kv.Key] = kv.Value;
            foreach (var kv in Table(code)) if (!string.IsNullOrEmpty(kv.Value)) res[kv.Key] = kv.Value;
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
