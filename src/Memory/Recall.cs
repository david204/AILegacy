using System;
using AILegacy.Core;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AILegacy.Settings;

namespace AILegacy.Memory
{
    /// <summary>
    /// Recuerdo relevante: cuando el historial se resume, las frases viejas no se tiran, se archivan. Al hablar, se buscan
    /// en ese archivo (y en las páginas del libro de memorias) los intercambios que tienen que ver con lo que se está
    /// diciendo ahora, y se le recuerdan al personaje palabra por palabra. No gasta peticiones: es una búsqueda por
    /// palabras hecha aquí mismo, dando más peso a las palabras raras (nombres, lugares, cosas concretas).
    /// </summary>
    internal static class Recall
    {
        private static readonly HashSet<string> Stop = new HashSet<string>(
            (L.T("AIL_f4d11fd9", "para pero porque como cuando donde quien quién cual esto esta este esos esas estos estas aquel aquella tiene tengo tienes tenemos hacer hace hago haces puede puedo puedes ") +
             L.T("AIL_88476cb6", "quiero quieres quiere muy mas más menos todo toda todos todas nada algo alguien nadie bien mal aqui aquí alli allí ahora luego antes despues después siempre nunca tambien también ") +
             L.T("AIL_48f76bf1", "sobre entre desde hasta hacia contra segun según mismo misma otro otra otros otras mucho mucha poco poca usted vuestro vuestra nuestro nuestra señor señora ") +
             "that this with from have what when where which there their they them then than your yours will would could should about been were into some more most very just also " +
             "said told know think want need like well here because while after before again never always something nothing someone").Split(' '));

        private static string Fold(string s)
        {
            var n = s.ToLowerInvariant().Normalize(NormalizationForm.FormD);
            var sb = new StringBuilder(n.Length);
            foreach (char c in n)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark) continue;
                sb.Append(char.IsLetterOrDigit(c) ? c : ' ');
            }
            return sb.ToString();
        }

        private static HashSet<string> Words(string text)
        {
            var set = new HashSet<string>();
            if (string.IsNullOrWhiteSpace(text)) return set;
            foreach (var w in Fold(text).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (w.Length < 4 || Stop.Contains(w)) continue;
                set.Add(w.Length > 7 ? w.Substring(0, 7) : w);      // raíz aproximada: «cosecha» y «cosechas» cuentan igual
            }
            return set;
        }

        private class Unit { public string Text; public double Day; public HashSet<string> W; public int Order; }

        /// <summary>Al resumir: las frases que salen del historial pasan al archivo.</summary>
        public static void Archive(HeroMemory mem, IEnumerable<ChatTurn> removed)
        {
            if (mem == null || removed == null) return;
            foreach (var t in removed)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Text)) continue;
                string text = t.Text.Trim();
                // Las respuestas guardadas como JSON se archivan solo con lo dicho
                if (t.Role != "player" && text.StartsWith("{"))
                {
                    try { text = ((string)Newtonsoft.Json.Linq.JObject.Parse(text)["reply"] ?? text).Trim(); } catch { }
                }
                if (text.Length > 600) text = text.Substring(0, 600) + "…";
                mem.Archive.Add(new ChatTurn { Role = t.Role, Text = text, Day = t.Day });
            }
            int max = Math.Max(20, S.Get.ArchiveTurns);
            if (mem.Archive.Count > max) mem.Archive.RemoveRange(0, mem.Archive.Count - max);
        }

        /// <summary>Los intercambios antiguos que más tienen que ver con lo que se dice ahora (en orden de tiempo).</summary>
        public static List<string> Find(HeroMemory mem, string query, string npcName, string playerName, double today, int max)
        {
            var result = new List<string>();
            try
            {
                if (mem == null || max <= 0 || (mem.Archive.Count == 0 && mem.Pages.Count == 0)) return result;
                var q = Words(query);
                if (q.Count == 0) return result;

                // Unidades: cada frase del jugador con la respuesta que le siguió; y cada página del libro
                var units = new List<Unit>();
                for (int i = 0; i < mem.Archive.Count; i++)
                {
                    var t = mem.Archive[i];
                    string line = (t.Role == "player" ? playerName : npcName) + ": " + t.Text;
                    if (t.Role == "player" && i + 1 < mem.Archive.Count && mem.Archive[i + 1].Role != "player")
                    {
                        line += "\n   " + npcName + ": " + mem.Archive[i + 1].Text;
                        i++;
                    }
                    units.Add(new Unit { Text = line, Day = t.Day, W = Words(line), Order = units.Count });
                }
                foreach (var p in mem.Pages)
                {
                    string line = $"(from your memories — {p.Title}) {p.Text}";
                    if (line.Length > 500) line = line.Substring(0, 500) + "…";
                    units.Add(new Unit { Text = line, Day = -1, W = Words(p.Title + " " + p.Text), Order = units.Count });
                }
                if (units.Count == 0) return result;

                // Peso de cada palabra: cuanto más rara en el archivo, más vale
                var df = new Dictionary<string, int>();
                foreach (var u in units) foreach (var w in u.W) { df.TryGetValue(w, out int c); df[w] = c + 1; }
                double Idf(string w) => df.TryGetValue(w, out int c) ? Math.Log(1.0 + units.Count / (double)c) : 0.0;

                var scored = new List<(Unit u, double score, int hits)>();
                foreach (var u in units)
                {
                    double sc = 0; int hits = 0;
                    foreach (var w in q) if (u.W.Contains(w)) { sc += Idf(w); hits++; }
                    if (hits == 0) continue;
                    sc /= Math.Sqrt(4.0 + u.W.Count);      // que no ganen siempre los párrafos largos
                    scored.Add((u, sc, hits));
                }
                // Hace falta algo más que una palabra corriente en común
                double floor = Math.Log(1.0 + units.Count / Math.Max(1.0, units.Count * 0.5)) / Math.Sqrt(12.0);
                foreach (var x in scored.Where(x => x.hits >= 2 || x.score > floor * 1.6).OrderByDescending(x => x.score).Take(max).OrderBy(x => x.u.Order))
                {
                    string when = x.u.Day < 0 ? "" : (today - x.u.Day) < 1 ? "[earlier today] " : $"[{(int)(today - x.u.Day)} days ago] ";
                    result.Add(when + x.u.Text);
                }
            }
            catch (Exception ex) { Core.Log.Error("Recuerdo relevante", ex); }
            return result;
        }
    }
}
