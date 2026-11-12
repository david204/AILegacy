using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;

namespace AILegacy.Memory
{
    /// <summary>
    /// Cuando el historial de un personaje crece demasiado, lo antiguo se resume en segundo plano y quedan solo los últimos
    /// turnos literales. El resumen va por TRAMOS FECHADOS: cada vez se escribe solo el tramo que sale del historial y se
    /// añade a la lista; lo ya escrito no se vuelve a redactar (reescribir un único resumen cada vez iba borrando los datos
    /// viejos, como el teléfono escacharrado). Solo cuando hay demasiados tramos se funden los más antiguos en uno, en la
    /// misma petición. De paso se escribe una página del libro de memorias: ese tramo contado como un capítulo.
    /// (Lo resumido se vuelca a archivo en el siguiente Flush.)
    /// </summary>
    internal static class MemorySummarizer
    {
        /// <summary>Con tantos tramos, la siguiente petición funde los más viejos en uno.</summary>
        private const int MaxParts = 6;
        private const int MergeCount = 3;
        public const int PartWords = 90;
        public const int MergedWords = 140;

        private static string Norm(string t) => (t ?? "").Replace("\r", "").Trim();

        private static string Label(SummaryPart p)
        {
            if (p.ToDay < 0) return "";
            int a = (int)Math.Floor(p.FromDay), b = (int)Math.Floor(p.ToDay);
            return a < 0 || a == b ? $"[day {b}] " : $"[day {a}–{b}] ";
        }

        /// <summary>El resumen entero como texto (lo que se enseña en el libro, en el editor y a otros sistemas).</summary>
        public static string Compose(HeroMemory mem) =>
            string.Join("\n", mem.Summaries.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => Label(p) + p.Text.Trim()));

        /// <summary>
        /// Pone de acuerdo los tramos con el texto del resumen. Si el texto se ha cambiado por fuera (el editor, una
        /// importación, el archivo tocado a mano), manda el texto: pasa a ser un único tramo sin fecha.
        /// </summary>
        public static void Sync(HeroMemory mem)
        {
            if (mem == null) return;
            if (mem.Summaries == null) mem.Summaries = new List<SummaryPart>();
            string text = Norm(mem.Summary);
            if (mem.Summaries.Count == 0)
            {
                if (text.Length > 0) mem.Summaries.Add(new SummaryPart { Text = text });
                return;
            }
            if (Norm(Compose(mem)) == text) return;
            mem.Summaries.Clear();
            if (text.Length > 0) mem.Summaries.Add(new SummaryPart { Text = text });
        }

        /// <param name="keepAtLeast">Turnos literales que deben quedar como mínimo (a mitad de charla, los que caben en el prompt).</param>
        public static void MaybeSummarize(Speaker s, int keepAtLeast = 0)
        {
            var mem = s.Memory;
            var cfg = S.Get;
            if (mem.SummarizeInFlight || mem.Turns.Count <= cfg.SummarizeAfterTurns) return;

            int keep = Math.Max(Math.Max(2, cfg.KeepTurnsAfterSummary), keepAtLeast);
            int cut = mem.Turns.Count - keep;
            if (cut < 6) return;                                  // no merece una petición
            var old = mem.Turns.Take(cut).ToList();

            Sync(mem);
            var merge = mem.Summaries.Count >= MaxParts ? mem.Summaries.Take(MergeCount).ToList() : new List<SummaryPart>();
            var messages = PromptBuilder.BuildSummary(s, old, merge);
            mem.SummarizeInFlight = true;
            int session = World.WorldSim.Session;
            Log.Info($"Resumiendo {old.Count} turnos antiguos de {s.Name}" + (merge.Count > 0 ? $" y fundiendo {merge.Count} tramos viejos" : ""));

            LlmClient.CompleteAsync(messages, wantJson: true, maxTokensOverride: merge.Count > 0 ? 1200 : 900, preferredProvider: cfg.ProviderSummaries).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    mem.SummarizeInFlight = false;
                    if (session != World.WorldSim.Session) return;       // se ha cargado otra partida mientras tanto
                    if (res == null || !res.Ok || string.IsNullOrWhiteSpace(res.Text))
                    {
                        Log.Warn($"No se pudo resumir la memoria de {s.Name}: {res?.Error}");
                        return;
                    }
                    string summary = res.Text.Trim(), title = "", page = "", merged = "";
                    try
                    {
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a >= 0 && b > a)
                        {
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            summary = ((string)j["summary"] ?? "").Trim();
                            title = ((string)j["page_title"] ?? "").Trim();
                            page = ((string)j["page_text"] ?? "").Trim();
                            merged = ((string)j["merged"] ?? "").Trim();
                        }
                    }
                    catch { /* texto suelto: vale como resumen */ }
                    if (summary.Length < 10 || summary.StartsWith("{")) { Log.Warn($"Resumen de {s.Name} ilegible: se conserva el historial"); return; }

                    // Solo salen del historial los turnos que se mandaron a resumir y siguen ahí, en su sitio
                    int n = 0;
                    while (n < old.Count && n < mem.Turns.Count && ReferenceEquals(mem.Turns[n], old[n])) n++;
                    if (n == 0) { Log.Warn($"El historial de {s.Name} ha cambiado mientras se resumía: se deja como está"); return; }
                    var folded = mem.Turns.Take(n).ToList();
                    Recall.Archive(mem, folded);
                    mem.Turns.RemoveRange(0, n);

                    Sync(mem);
                    // Fundir los tramos viejos, si siguen siendo los mismos que se mandaron
                    if (merged.Length > 20 && merge.Count > 0 && mem.Summaries.Count >= merge.Count && merge.Select((p, i) => ReferenceEquals(p, mem.Summaries[i])).All(x => x))
                    {
                        var one = new SummaryPart { FromDay = merge[0].ToDay < 0 ? -1 : merge[0].FromDay, ToDay = merge[merge.Count - 1].ToDay, Text = Clip(merged, 1400) };
                        if (merge[0].ToDay < 0) one.FromDay = -1;
                        mem.Summaries.RemoveRange(0, merge.Count);
                        mem.Summaries.Insert(0, one);
                    }
                    double from = folded.Where(x => x.Day > 0).Select(x => x.Day).DefaultIfEmpty(-1).Min();
                    double to = folded.Where(x => x.Day > 0).Select(x => x.Day).DefaultIfEmpty(-1).Max();
                    mem.Summaries.Add(new SummaryPart { FromDay = from, ToDay = to, Text = Clip(summary, 1000) });
                    // Red de seguridad: si el modelo nunca devuelve la fusión, los dos más viejos se pegan tal cual
                    while (mem.Summaries.Count > MaxParts + 3)
                    {
                        var a = mem.Summaries[0]; var b = mem.Summaries[1];
                        mem.Summaries.RemoveRange(0, 2);
                        mem.Summaries.Insert(0, new SummaryPart { FromDay = a.ToDay < 0 ? -1 : a.FromDay, ToDay = b.ToDay, Text = Clip(a.Text.Trim() + " " + b.Text.Trim(), 1400) });
                    }
                    mem.Summary = Compose(mem);
                    Log.Info($"Memoria de {s.Name}: nuevo tramo ({n} turnos, {mem.Summaries.Count} tramos en total): {summary}");

                    if (cfg.EnableMemoryPages && s.IsHero && page.Length > 20)
                    {
                        mem.Pages.Add(new MemoryPage
                        {
                            Title = title.Length > 0 ? (title.Length > 80 ? title.Substring(0, 80) : title) : L.T("AIL_463eb3a3", "Sin título"),
                            Text = page.Length > 2500 ? page.Substring(0, 2500) : page,
                            Day = CampaignTime.Now.ToDays,
                        });
                        while (mem.Pages.Count > cfg.MaxMemoryPages) mem.Pages.RemoveAt(0);
                        Log.Info($"Nueva página en el libro de memorias de {s.Name}: {title}");
                    }
                    SaveFiles.Flush();
                });
            });
        }

        private static string Clip(string t, int max) => t.Length <= max ? t : t.Substring(0, max) + "…";
    }
}
