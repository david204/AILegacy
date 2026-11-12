using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Memory;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;

namespace AILegacy.Prompt
{
    /// <summary>
    /// Una plantilla de instrucciones editable. El texto por defecto vive en el código; al cargar una campaña
    /// se copia a save_data\[campaña]\prompts\[carpeta]\[archivo].txt y desde entonces manda el archivo.
    /// Los huecos van entre llaves: {npc_name}, {language}…
    /// </summary>
    internal class Tpl
    {
        public readonly string Key;       // "rules/dialogue_rules.txt"
        public readonly string Default;
        private string _cached;
        private string _cachedPath;
        private DateTime _cachedStamp;
        private DateTime _nextCheck;

        public Tpl(string key, string defaultText)
        {
            Key = key.Replace('\\', '/');
            Default = (defaultText ?? "").Replace("\r\n", "\n").Trim('\n');
            Templates.Register(this);
        }

        /// <summary>El texto vigente (el archivo de la campaña si existe; si no, el de fábrica).</summary>
        public string Text
        {
            get
            {
                try
                {
                    var path = Templates.PathFor(Key);
                    if (path == null) return Default;
                    var now = DateTime.UtcNow;
                    if (_cached != null && _cachedPath == path && now < _nextCheck) return _cached;
                    _nextCheck = now.AddSeconds(2);
                    if (!File.Exists(path)) { _cached = Default; _cachedPath = path; return Default; }
                    var stamp = File.GetLastWriteTimeUtc(path);
                    if (_cached == null || _cachedPath != path || stamp != _cachedStamp)
                    {
                        var text = File.ReadAllText(path, Encoding.UTF8).Replace("\r\n", "\n").Trim('\n', ' ', '﻿');
                        _cached = text.Length == 0 ? Default : text;   // archivo vacío = volver al de fábrica
                        _cachedPath = path; _cachedStamp = stamp;
                    }
                    return _cached;
                }
                catch { return Default; }
            }
        }

        /// <summary>Rellena los huecos: Format("npc_name", "Rhagaea", "language", "Spanish").</summary>
        public string Format(params object[] pairs)
        {
            var sb = new StringBuilder(Text);
            for (int i = 0; i + 1 < pairs.Length; i += 2)
                sb.Replace("{" + pairs[i] + "}", pairs[i + 1]?.ToString() ?? "");
            return sb.ToString();
        }

        public override string ToString() => Text;
    }

    internal static class Templates
    {
        private static readonly Dictionary<string, Tpl> All = new Dictionary<string, Tpl>(StringComparer.OrdinalIgnoreCase);
        private static string _campaignDir;   // ...\save_data\<id>\prompts
        private const string ManifestName = "_defaults.json";

        public static void Register(Tpl t) { lock (All) All[t.Key] = t; }

        public static Tpl Get(string key) { lock (All) return All.TryGetValue(key.Replace('\\', '/'), out var t) ? t : null; }

        /// <summary>Todas las plantillas registradas (para el editor).</summary>
        public static List<Tpl> AllTemplates() { lock (All) return All.Values.OrderBy(t => t.Key, StringComparer.OrdinalIgnoreCase).ToList(); }

        /// <summary>Plantilla creada sobre la marcha (acciones): la devuelve si existe o la registra.</summary>
        public static Tpl GetOrAdd(string key, string defaultText) => Get(key) ?? new Tpl(key, defaultText);

        public static string PathFor(string key) => _campaignDir == null ? null : Path.Combine(_campaignDir, key.Replace('/', Path.DirectorySeparatorChar));

        private static string Hash(string s)
        {
            unchecked
            {
                uint h = 2166136261;
                foreach (char c in s.Replace("\r\n", "\n").Trim('\n', ' ', '﻿')) { h ^= c; h *= 16777619; }
                return h.ToString("x8");
            }
        }

        /// <summary>
        /// Prepara la carpeta prompts de la campaña: crea los archivos que falten y actualiza los que el usuario
        /// no ha tocado cuando el texto de fábrica ha cambiado con una versión nueva. Lo editado a mano no se pisa.
        /// </summary>
        public static void UseCampaign(string campaignId)
        {
            try
            {
                if (string.IsNullOrEmpty(campaignId)) { _campaignDir = null; return; }
                var dir = Path.Combine(SaveFiles.DirFor(campaignId), "prompts");
                Directory.CreateDirectory(dir);
                var manifestPath = Path.Combine(dir, ManifestName);
                Dictionary<string, string> manifest = null;
                try { if (File.Exists(manifestPath)) manifest = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(manifestPath)); } catch { }
                manifest = manifest ?? new Dictionary<string, string>();

                List<Tpl> all;
                lock (All) all = All.Values.ToList();
                int created = 0, updated = 0, custom = 0;
                foreach (var t in all)
                {
                    var path = Path.Combine(dir, t.Key.Replace('/', Path.DirectorySeparatorChar));
                    string defHash = Hash(t.Default);
                    if (!File.Exists(path))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(path));
                        File.WriteAllText(path, t.Default.Replace("\n", "\r\n"), new UTF8Encoding(false));
                        manifest[t.Key] = defHash; created++;
                        continue;
                    }
                    string fileHash = Hash(File.ReadAllText(path, Encoding.UTF8));
                    manifest.TryGetValue(t.Key, out var writtenHash);
                    if (fileHash == defHash) { manifest[t.Key] = defHash; continue; }
                    if (writtenHash != null && fileHash == writtenHash)
                    {
                        // intacto desde que lo escribimos, pero el de fábrica ha mejorado: se actualiza
                        File.WriteAllText(path, t.Default.Replace("\n", "\r\n"), new UTF8Encoding(false));
                        manifest[t.Key] = defHash; updated++;
                    }
                    else custom++;
                }
                File.WriteAllText(manifestPath, JsonConvert.SerializeObject(manifest, Formatting.Indented));

                var readme = Path.Combine(dir, "LEEME.txt");
                if (!File.Exists(readme))
                    File.WriteAllText(readme,
                        L.T("AIL_089c5db1", "Plantillas de instrucciones de AI Legacy para ESTA campaña.\r\n\r\n") +
                        L.T("AIL_33265d9a", "Cada archivo es un trozo de lo que se le manda a la IA. Puedes editarlos con el juego abierto: se releen solos.\r\n") +
                        L.T("AIL_13527f3f", "Lo que va entre llaves, como {npc_name} o {language}, lo rellena el mod: no lo traduzcas ni lo borres.\r\n") +
                        L.T("AIL_3f0d143a", "Escríbelos en inglés (los modelos lo siguen mejor); los NPC contestan en el idioma de los ajustes.\r\n\r\n") +
                        L.T("AIL_b1624bd9", "Para volver al texto de fábrica de un archivo: bórralo (o déjalo vacío) y carga la partida.\r\n") +
                        L.T("AIL_2bf0193d", "Los archivos que no hayas tocado se actualizan solos cuando una versión nueva del mod mejora el texto.\r\n") +
                        L.T("AIL_94c21a0a", "Los que hayas editado no se tocan nunca.\r\n"), new UTF8Encoding(false));

                _campaignDir = dir;
                Log.Info($"Plantillas: {all.Count} en {dir} ({created} creadas, {updated} actualizadas, {custom} editadas por ti)");
            }
            catch (Exception ex) { Log.Error("No se pudo preparar la carpeta de plantillas", ex); _campaignDir = null; }
        }
    }
}
