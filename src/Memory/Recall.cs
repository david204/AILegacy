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
    /// Keyword recall. When the history is summarized the old lines are archived, not thrown away. On each turn we search
    /// that archive (and the memory book pages) for exchanges related to what's being said now and hand them back to the
    /// character word for word. Runs locally with no requests, and rare words (names, places) weigh more.
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
                set.Add(w.Length > 7 ? w.Substring(0, 7) : w);      // rough stemming, so "harvest" and "harvests" match
            }
            return set;
        }

        private class Unit { public string Text; public double Day; public HashSet<string> W; public int Order; }

        public static void Archive(HeroMemory mem, IEnumerable<ChatTurn> removed)
        {
            if (mem == null || removed == null) return;
            if (mem.ArchivedTotal < mem.Archive.Count) mem.ArchivedTotal = mem.Archive.Count;       // memories saved before this counter existed
            foreach (var t in removed)
            {
                if (t == null || string.IsNullOrWhiteSpace(t.Text)) continue;
                string text = t.Text.Trim();
                // Replies stored as raw JSON: archive only the spoken line
                if (t.Role != "player" && text.StartsWith("{"))
                {
                    try { text = ((string)Newtonsoft.Json.Linq.JObject.Parse(text)["reply"] ?? text).Trim(); } catch { }
                }
                if (text.Length > 600) text = text.Substring(0, 600) + "…";
                mem.Archive.Add(new ChatTurn { Role = t.Role, Text = text, Day = t.Day });
                mem.ArchivedTotal++;
            }
            int max = Math.Max(20, S.Get.ArchiveTurns);
            if (mem.Archive.Count > max) mem.Archive.RemoveRange(0, mem.Archive.Count - max);
        }

        private static string Said(ChatTurn t)
        {
            string text = (t.Text ?? "").Trim();
            if (t.Role != "player" && text.StartsWith("{"))
            {
                try { text = ((string)Newtonsoft.Json.Linq.JObject.Parse(text)["reply"] ?? text).Trim(); } catch { }
            }
            return text.Length > 600 ? text.Substring(0, 600) + "…" : text;
        }

        /// <summary>
        /// Old exchanges most related to the current line, in time order.
        /// <c>older</c> are turns still in the history that no longer fit in the prompt (a very long chat). Without them the
        /// start of today's conversation wouldn't be in the prompt, the summary or the archive.
        /// </summary>
        public static List<string> Find(HeroMemory mem, string query, string npcName, string playerName, double today, int max, IList<ChatTurn> older = null)
        {
            var result = new List<string>();
            try
            {
                int olderCount = older?.Count ?? 0;
                if (mem == null || max <= 0 || (mem.Archive.Count == 0 && mem.Pages.Count == 0 && olderCount == 0)) return result;
                var q = Words(query);
                if (q.Count == 0) return result;

                // One unit per player line plus the reply that followed it
                var past = new List<ChatTurn>(mem.Archive.Count + olderCount);
                past.AddRange(mem.Archive);
                if (older != null) past.AddRange(older.Where(t => t != null && !string.IsNullOrWhiteSpace(t.Text)));
                var units = new List<Unit>();
                for (int i = 0; i < past.Count; i++)
                {
                    var t = past[i];
                    string line = t.IsNote ? t.Text.Trim() : (t.Role == "player" ? playerName : npcName) + ": " + Said(t);
                    if (t.Role == "player" && !t.IsNote && i + 1 < past.Count && past[i + 1].Role != "player")
                    {
                        line += "\n   " + npcName + ": " + Said(past[i + 1]);
                        i++;
                    }
                    units.Add(new Unit { Text = line, Day = t.Day, W = Words(line), Order = units.Count });
                }
                int turnUnits = units.Count;
                // Book pages are embellished prose, so they're only a fallback when no literal turn matches
                foreach (var p in mem.Pages)
                {
                    string line = $"(from your memories — {p.Title}) {p.Text}";
                    if (line.Length > 500) line = line.Substring(0, 500) + "…";
                    units.Add(new Unit { Text = line, Day = -1, W = Words(p.Title + " " + p.Text), Order = units.Count });
                }
                if (units.Count == 0) return result;

                var df = new Dictionary<string, int>();
                foreach (var u in units) foreach (var w in u.W) { df.TryGetValue(w, out int c); df[w] = c + 1; }
                double Idf(string w) => df.TryGetValue(w, out int c) ? Math.Log(1.0 + units.Count / (double)c) : 0.0;

                var scored = new List<(Unit u, double score, int hits)>();
                foreach (var u in units)
                {
                    double sc = 0; int hits = 0;
                    foreach (var w in q) if (u.W.Contains(w)) { sc += Idf(w); hits++; }
                    if (hits == 0) continue;
                    sc /= Math.Sqrt(4.0 + u.W.Count);      // so long paragraphs don't always win
                    scored.Add((u, sc, hits));
                }
                // Sharing one ordinary word isn't enough
                double floor = Math.Log(1.0 + units.Count / Math.Max(1.0, units.Count * 0.5)) / Math.Sqrt(12.0);
                var good = scored.Where(x => x.hits >= 2 || x.score > floor * 1.6).ToList();
                if (good.Any(x => x.u.Order < turnUnits)) good = good.Where(x => x.u.Order < turnUnits).ToList();
                foreach (var x in good.OrderByDescending(x => x.score).Take(max).OrderBy(x => x.u.Order))
                {
                    string when = x.u.Day <= 0 ? "" : (today - x.u.Day) < 1 ? "[earlier today] " : $"[{(int)(today - x.u.Day)} days ago] ";
                    result.Add(when + x.u.Text);
                }
            }
            catch (Exception ex) { Core.Log.Error("Relevant recall", ex); }
            return result;
        }
    }
}
