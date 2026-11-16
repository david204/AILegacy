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
    /// When a character's history gets too long, the old turns are summarized in the background and only the latest stay
    /// word for word. The summary is a list of dated parts: each request writes only the part leaving the history and
    /// appends it. Old parts are never rewritten, because rewriting one big summary every time kept losing old facts.
    /// When there are too many parts, the oldest get merged into one in the same request. That request also writes a
    /// memory book page for the new part.
    /// </summary>
    internal static class MemorySummarizer
    {
        /// <summary>At this many parts, the next request merges the oldest ones.</summary>
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

        /// <summary>Full summary as plain text, for the book, the editor and other systems.</summary>
        public static string Compose(HeroMemory mem) =>
            string.Join("\n", mem.Summaries.Where(p => !string.IsNullOrWhiteSpace(p.Text)).Select(p => Label(p) + p.Text.Trim()));

        /// <summary>
        /// Keeps the parts in line with the Summary text. If the text was changed from outside (editor, import, file
        /// edited by hand), the text wins and becomes a single undated part.
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

        /// <param name="keepAtLeast">Minimum turns to keep word for word (mid-conversation, the ones that fit in the prompt).</param>
        public static void MaybeSummarize(Speaker s, int keepAtLeast = 0)
        {
            var mem = s.Memory;
            var cfg = S.Get;
            if (mem.SummarizeInFlight || mem.Turns.Count <= cfg.SummarizeAfterTurns) return;

            int keep = Math.Max(Math.Max(2, cfg.KeepTurnsAfterSummary), keepAtLeast);
            int cut = mem.Turns.Count - keep;
            if (cut < 6) return;                                  // not worth a request
            var old = mem.Turns.Take(cut).ToList();

            Sync(mem);
            var merge = mem.Summaries.Count >= MaxParts ? mem.Summaries.Take(MergeCount).ToList() : new List<SummaryPart>();
            var messages = PromptBuilder.BuildSummary(s, old, merge);
            mem.SummarizeInFlight = true;
            int session = World.WorldSim.Session;
            Log.Info($"Summarizing {old.Count} old turns of {s.Name}" + (merge.Count > 0 ? $" and merging {merge.Count} old segments" : ""));

            LlmClient.CompleteAsync(messages, wantJson: true, task: "summary", maxTokensOverride: merge.Count > 0 ? 1200 : 900, preferredProvider: cfg.ProviderSummaries).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    mem.SummarizeInFlight = false;
                    if (session != World.WorldSim.Session) return;       // another save was loaded meanwhile
                    if (res == null || !res.Ok || string.IsNullOrWhiteSpace(res.Text))
                    {
                        Log.Warn($"Could not summarize the memory of {s.Name}: {res?.Error}");
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
                    catch { /* plain text reply, use it as the summary */ }
                    if (summary.Length < 10 || summary.StartsWith("{")) { Log.Warn($"Summary of {s.Name} unreadable: keeping the history"); return; }

                    // Only drop the turns we sent, and only while they're still at the front of the history
                    int n = 0;
                    while (n < old.Count && n < mem.Turns.Count && ReferenceEquals(mem.Turns[n], old[n])) n++;
                    if (n == 0) { Log.Warn($"History of {s.Name} changed while summarizing: leaving it as is"); return; }
                    var folded = mem.Turns.Take(n).ToList();
                    Recall.Archive(mem, folded);
                    mem.Turns.RemoveRange(0, n);
                    RagIndex.Schedule(s);          // re-index so the archived turns show up in semantic recall

                    Sync(mem);
                    // Merge the old parts, but only if they're still the ones we sent
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
                    // Fallback in case the model never returns a merge: glue the two oldest parts together as they are
                    while (mem.Summaries.Count > MaxParts + 3)
                    {
                        var a = mem.Summaries[0]; var b = mem.Summaries[1];
                        mem.Summaries.RemoveRange(0, 2);
                        mem.Summaries.Insert(0, new SummaryPart { FromDay = a.ToDay < 0 ? -1 : a.FromDay, ToDay = b.ToDay, Text = Clip(a.Text.Trim() + " " + b.Text.Trim(), 1400) });
                    }
                    mem.Summary = Compose(mem);
                    Log.Info($"Memory of {s.Name}: new segment ({n} turns, {mem.Summaries.Count} segments in total): {summary}");

                    if (cfg.EnableMemoryPages && s.IsHero && page.Length > 20 && PageInScope(s, folded))
                    {
                        mem.Pages.Add(new MemoryPage
                        {
                            Title = title.Length > 0 ? (title.Length > 80 ? title.Substring(0, 80) : title) : L.T("AIL_463eb3a3", "Sin título"),
                            Text = page.Length > 2500 ? page.Substring(0, 2500) : page,
                            Day = CampaignTime.Now.ToDays,
                        });
                        while (mem.Pages.Count > cfg.MaxMemoryPages) mem.Pages.RemoveAt(0);
                        var added = mem.Pages[mem.Pages.Count - 1];
                        if (cfg.ImageAuto && World.SceneImages.Enabled) World.SceneImages.Request(mem, added);
                        Log.Info($"New page in the memory book of {s.Name}: {title}");
                        UI.PageNotices.Notify(s.Hero, added);
                    }
                    else if (cfg.EnableMemoryPages && s.IsHero && page.Length > 20)
                        Log.Info($"Memory book: no page for {s.Name}, that stretch is outside \"whose memories\" ({S.Get.MemoryBookScope?.SelectedValue})");
                    SaveFiles.Flush();
                });
            });
        }

        private static string Clip(string t, int max) => t.Length <= max ? t : t.Substring(0, max) + "…";

        // ============================================================ whose memories (MCM 4. Memory)

        /// <summary>
        /// Whether a stretch of history becomes a book page. Player only: the player took part in it. Player and
        /// family: that, or the character is family of the player. Everyone: always.
        /// </summary>
        private static bool PageInScope(Speaker s, List<ChatTurn> stretch)
        {
            int mode = S.Get.MemoryBookScopeMode();
            if (mode >= 2) return true;
            if (PlayerTookPart(stretch)) return true;
            return mode == 1 && s.IsHero && IsPlayerFamily(s.Hero);
        }

        /// <summary>
        /// Anything said to or by the player, a letter, a group talk with them, a deed or an order of theirs counts. Only
        /// the notes of scene talks between characters (Ambient) are lived without the player.
        /// </summary>
        public static bool PlayerTookPart(IEnumerable<ChatTurn> stretch)
        {
            foreach (var t in stretch ?? Enumerable.Empty<ChatTurn>())
            {
                string x = t?.Text ?? "";
                if (x.Length == 0) continue;
                if (x.StartsWith("[In a group: you were talking with ", StringComparison.Ordinal) || x.StartsWith("[In a group: as they walked past", StringComparison.Ordinal)) continue;
                return true;
            }
            return false;
        }

        /// <summary>The player's family, wide: spouse, parents, grandparents, children, grandchildren, siblings, nephews and nieces, uncles and aunts, cousins and in-laws.</summary>
        public static bool IsPlayerFamily(Hero h)
        {
            var p = Hero.MainHero;
            if (h == null || p == null || h == p) return false;
            try { return FamilyOf(p).Contains(h); }
            catch { return false; }
        }

        private static HashSet<Hero> FamilyOf(Hero p)
        {
            var set = new HashSet<Hero>();
            void A(Hero x) { if (x != null && x != p) set.Add(x); }
            IEnumerable<Hero> Kids(Hero x) => x?.Children ?? Enumerable.Empty<Hero>();
            IEnumerable<Hero> Sibs(Hero x) => x?.Siblings ?? Enumerable.Empty<Hero>();
            IEnumerable<Hero> Parents(Hero x) => x == null ? Enumerable.Empty<Hero>() : new[] { x.Father, x.Mother }.Where(y => y != null);

            A(p.Spouse);
            foreach (var x in p.ExSpouses ?? Enumerable.Empty<Hero>()) A(x);
            foreach (var par in Parents(p)) { A(par); foreach (var g in Parents(par)) A(g); foreach (var u in Sibs(par)) { A(u); foreach (var c in Kids(u)) A(c); } }
            foreach (var c in Kids(p)) { A(c); A(c.Spouse); foreach (var g in Kids(c)) A(g); }
            foreach (var b in Sibs(p)) { A(b); A(b.Spouse); foreach (var n in Kids(b)) A(n); }
            if (p.Spouse != null) { foreach (var x in Parents(p.Spouse)) A(x); foreach (var x in Sibs(p.Spouse)) A(x); }
            return set;
        }

        /// <summary>
        /// Once a day, with "whose memories" on family or everyone: characters whose history grew long without the
        /// player (scene talks with others) get it summarized too, which writes their page. A few per day, so the AI
        /// is not flooded; the rest wait for the next days.
        /// </summary>
        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                int mode = cfg.MemoryBookScopeMode();
                if (mode == 0 || !cfg.EnableMemoryPages || Campaign.Current == null) return;
                Hero talking = null;
                try { talking = ChatSession.Active?.Who?.Hero; } catch { }
                var family = mode == 1 && Hero.MainHero != null ? FamilyOf(Hero.MainHero) : null;
                int started = 0;
                foreach (var m in MemoryStore.AllHeroes.Where(x => x != null && !x.SummarizeInFlight && x.Turns.Count > cfg.SummarizeAfterTurns + 6).ToList())
                {
                    if (started >= 2) break;
                    var h = Hero.Find(m.HeroId);
                    if (h == null || !h.IsAlive || h == Hero.MainHero || h == talking) continue;
                    if (family != null && !family.Contains(h)) continue;
                    var sp = Speaker.FromHero(h);
                    if (sp == null || !ReferenceEquals(sp.Memory, m)) continue;
                    MaybeSummarize(sp);
                    if (m.SummarizeInFlight) { started++; Log.Info($"Memory book ({cfg.MemoryBookScope?.SelectedValue}): summarizing the history of {h.Name} lived without you"); }
                }
            }
            catch (Exception ex) { Log.Error("Memory book: daily pass", ex); }
        }
    }
}
