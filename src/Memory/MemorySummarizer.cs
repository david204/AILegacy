using System;
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
    /// Cuando el historial de un personaje crece demasiado, resume lo antiguo en segundo plano y se queda solo con los
    /// últimos turnos literales. De paso escribe una página del libro de memorias: ese tramo contado como un capítulo.
    /// (Lo resumido se vuelca a archivo en el siguiente Flush.)
    /// </summary>
    internal static class MemorySummarizer
    {
        public static void MaybeSummarize(Speaker s)
        {
            var mem = s.Memory;
            var cfg = S.Get;
            if (mem.SummarizeInFlight || mem.Turns.Count <= cfg.SummarizeAfterTurns) return;

            int keep = Math.Max(2, cfg.KeepTurnsAfterSummary);
            int cut = mem.Turns.Count - keep;
            var old = mem.Turns.Take(cut).ToList();

            var messages = PromptBuilder.BuildSummary(s, old);
            mem.SummarizeInFlight = true;
            Log.Info($"Resumiendo {old.Count} turnos antiguos de {s.Name}");

            LlmClient.CompleteAsync(messages, wantJson: true, maxTokensOverride: 900, preferredProvider: cfg.ProviderSummaries).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    mem.SummarizeInFlight = false;
                    if (res == null || !res.Ok || string.IsNullOrWhiteSpace(res.Text))
                    {
                        Log.Warn($"No se pudo resumir la memoria de {s.Name}: {res?.Error}");
                        return;
                    }
                    string summary = res.Text.Trim(), title = "", page = "";
                    try
                    {
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a >= 0 && b > a)
                        {
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            summary = ((string)j["summary"] ?? "").Trim();
                            title = ((string)j["page_title"] ?? "").Trim();
                            page = ((string)j["page_text"] ?? "").Trim();
                        }
                    }
                    catch { /* texto suelto: vale como resumen */ }
                    if (summary.Length < 10 || summary.StartsWith("{")) { Log.Warn($"Resumen de {s.Name} ilegible: se conserva el historial"); return; }

                    int n = Math.Min(old.Count, mem.Turns.Count);
                    Recall.Archive(mem, mem.Turns.Take(n).ToList());
                    mem.Turns.RemoveRange(0, n);
                    mem.Summary = summary;
                    Log.Info($"Memoria de {s.Name} resumida: {mem.Summary}");

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
                });
            });
        }
    }
}
