using System;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.ScreenSystem;

namespace AILegacy.UI
{
    /// <summary>Opens and closes a full-screen Gauntlet window for a ViewModel.</summary>
    internal class GauntletWindow
    {
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private ScreenBase _screen;
        private ViewModel _vm;
        private Action _onEscape;
        public static GauntletWindow Current { get; private set; }

        public static GauntletWindow Open(string movie, ViewModel vm, Action onEscape)
        {
            try
            {
                Current?.Close();
                var w = new GauntletWindow { _vm = vm, _onEscape = onEscape, _screen = ScreenManager.TopScreen };
                if (w._screen == null) return null;
                w._layer = new GauntletLayer("AILegacy_" + movie, 440);
                w._movie = w._layer.LoadMovie(movie, vm);
                w._layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                w._layer.IsFocusLayer = true;
                w._screen.AddLayer(w._layer);
                w._layer.InputRestrictions.SetInputRestrictions();
                ScreenManager.TrySetFocus(w._layer);
                Current = w;
                return w;
            }
            catch (Exception ex) { Log.Error("Could not open window " + movie, ex); return null; }
        }

        public void Tick()
        {
            try { if (_layer != null && _layer.Input.IsHotKeyReleased("Exit")) _onEscape?.Invoke(); }
            catch { }
        }

        public void Close()
        {
            try
            {
                if (_layer != null)
                {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    ScreenManager.TryLoseFocus(_layer);
                    if (_movie != null) _layer.ReleaseMovie(_movie);
                    _screen?.RemoveLayer(_layer);
                }
                _vm?.OnFinalize();
            }
            catch (Exception ex) { Log.Error("Closing window", ex); }
            finally { _layer = null; if (Current == this) Current = null; }
        }
    }

    /// <summary>News and Memories windows.</summary>
    internal static class Books
    {
        /// <summary>Full transcript with someone: recent turns, archived old ones, and their memory book pages.</summary>
        public static void OpenTranscript(Dialogue.Speaker s)
        {
            try
            {
                if (s == null) return;
                var mem = s.Memory;
                string me = Hero.MainHero.Name.ToString();
                string Lines(System.Collections.Generic.IEnumerable<Memory.ChatTurn> turns)
                {
                    var b = new StringBuilder();
                    foreach (var t in turns)
                    {
                        string text = (t.Text ?? "").Trim();
                        if (text.Length == 0 || t.IsNote) continue;      // event notes are for the AI, not part of the conversation
                        if (t.Role != "player" && text.StartsWith("{")) { try { text = ((string)Newtonsoft.Json.Linq.JObject.Parse(text)["reply"] ?? text).Trim(); } catch { } }
                        bool note = text.StartsWith("[");
                        b.Append(note ? text : (t.Role == "player" ? me : s.Name) + ":\n" + text).Append("\n\n");
                    }
                    return b.Length == 0 ? L.T("AIL_d88f9ba0", "Nada todavía.") : b.ToString().Replace("{", "(").Replace("}", ")").TrimEnd();
                }
                var vm = new BookVM { Title = L.F("AIL_fd36d8df", $"Lo hablado con {s.Name}") };
                void Add(string title, string sub, string text) => vm.Entries.Add(new BookEntryVM(title, sub, text, e => vm.Select(e, e.Title, (string)e.Tag)));
                Add(L.T("AIL_e147d85d", "Esta conversación y las últimas"), L.F("AIL_e778fb02", $"{mem.Turns.Count} frase(s)"), Lines(mem.Turns));
                if (mem.Archive.Count > 0) Add(L.T("AIL_1834d79b", "Conversaciones antiguas"), L.F("AIL_ceb6c4a4", $"{mem.Archive.Count} frase(s) archivadas"), Lines(mem.Archive));
                if (!string.IsNullOrWhiteSpace(mem.Summary)) Add(L.T("AIL_7584960c", "Lo que recuerda de ti, en resumen"), "", mem.Summary);
                foreach (var p in mem.Pages) Add(p.Title ?? "Recuerdo", L.T("AIL_031d614f", "Página de su libro"), p.Text ?? "");
                vm.Select(vm.Entries[0], vm.Entries[0].Title, (string)vm.Entries[0].Tag);
                Open("AILegacyBook", vm);
            }
            catch (Exception ex) { Core.Log.Error("Reviewing past conversation", ex); }
        }

        /// <summary>What the news window is showing: everything or only events, which kind of record, which category.</summary>
        private class NewsView
        {
            public bool All;
            public string Record = "all";       // all | events | statements
            public string Category;             // null = every category
        }

        /// <summary>World news in a single column, read like a town crier's notice: title, who it concerns, what happened, effects, type and date.</summary>
        public static void OpenNews()
        {
            try
            {
                var vm = new NewsFeedVM { Title = L.T("AIL_52b8aa60", "Sucesos del mundo") };
                var view = new NewsView();
                FillNews(vm, view);
                vm.OnToggle = () => { view.All = !view.All; FillNews(vm, view); };
                vm.OnSearch = () => FillNews(vm, view);
                // Rulers can make declarations from here
                if (Diplomacy.PlayerCanDeclare) { vm.ActionText = L.T("AIL_7611f3a8", "Hacer una declaración"); vm.HasAction = true; }
                GauntletWindow w = null;
                vm.OnClose = () => w?.Close();
                vm.OnAction = () => { w?.Close(); Core.MainThread.Post(Diplomacy.PlayerStatement); };
                w = GauntletWindow.Open("AILegacyNews", vm, () => w?.Close());
                if (w == null) OpenNewsOld();
            }
            catch (Exception ex) { Core.Log.Error("News window", ex); OpenNewsOld(); }
        }

        /// <summary>True for news worth telling (world events, royal statements, big happenings). The rest is the steady trickle of captures and skirmishes.</summary>
        private static bool IsEvent(NewsItem n)
        {
            string t = n.Type ?? "";
            if (t.StartsWith("event_") || t == "statement" || t == "decree" || t == "custom") return true;
            if (t == "rumor") return !string.IsNullOrWhiteSpace(n.TextEs);
            return n.Importance >= 3 && (t == "war" || t == "peace" || t == "ruler" || t == "kingdom" || t == "death");
        }

        /// <summary>One-word tag for the bottom left corner and its category key (which also picks its color).</summary>
        private static string TagOf(NewsItem n, out string key)
        {
            string t = n.Type ?? "";
            if (t == "statement") { key = "statement"; return L.T("AIL_e14ea467", "Declaración"); }
            if (t == "decree") { key = "verdict"; return L.T("AIL_e7b1cb12", "Sentencia"); }
            if (t == "event_political" || t == "war" || t == "peace" || t == "ruler" || t == "kingdom") { key = "political"; return L.T("AIL_41ac9547", "Política"); }
            if (n.Importance <= 1) { key = "local"; return L.T("AIL_28b223a8", "Local"); }
            switch (t)
            {
                case "event_military": case "battle": case "siege": case "raid": case "capture": key = "military"; return L.T("AIL_78abe94b", "Militar");
                case "event_economic": key = "economic"; return L.T("AIL_0d2bb14b", "Economía");
                case "event_disease": key = "disease"; return L.T("AIL_ee39700e", "Enfermedad");
                case "event_mysterious": key = "mysterious"; return L.T("AIL_c3a1387b", "Misterio");
                case "rumor": key = "rumor"; return L.T("AIL_be7cce2e", "Rumor");
                case "death": key = "death"; return L.T("AIL_7e5382b7", "Muerte");
                case "prisoner": key = "prisoner"; return L.T("AIL_11783c51", "Prisionero");
                case "event_news": case "custom": key = "news"; return L.T("AIL_5418db17", "Suceso");
                default: key = "social"; return L.T("AIL_44de911b", "Sociedad");
            }
        }

        /// <summary>Color of each category's tag and filter button.</summary>
        private static string CategoryColor(string key)
        {
            switch (key)
            {
                case "statement": return "#6B5A1EFF";
                case "verdict": return "#5A2E2EFF";
                case "political": return "#2B3760FF";
                case "military": return "#7A2626FF";
                case "economic": return "#7A6420FF";
                case "disease": return "#5E6B1FFF";
                case "mysterious": return "#4B2E6BFF";
                case "rumor": return "#5A4A3AFF";
                case "death": return "#3A3A3AFF";
                case "prisoner": return "#4A4A6BFF";
                case "local": return "#4A3524FF";
                case "news": return "#3B4A5AFF";
                case "law": return "#2B4F6BFF";
                default: return "#1F4A44FF";
            }
        }

        /// <summary>Tag text and color of a decision of a statement.</summary>
        private static (string label, string color) DecisionTag(string kind)
        {
            switch (kind)
            {
                case "war": return (L.T("AIL_13afe103", "Guerra"), "#8B2E2EFF");
                case "peace": return (L.T("AIL_c05b0270", "Paz"), "#2E6B3AFF");
                case "alliance": return (L.T("AIL_e7b1c762", "Alianza"), "#2B4F80FF");
                case "reject": return (L.T("AIL_e7b1cb13", "Rechazo"), "#5A5A5AFF");
                case "trade": return (L.T("AIL_e7b1cb14", "Comercio"), "#8A6D1EFF");
                case "territory": return (L.T("AIL_e7b1cb15", "Tierras"), "#6B4A2BFF");
                case "tribute": return (L.T("AIL_e7b1cb16", "Pagos"), "#7A5C10FF");
                case "tax": return (L.T("AIL_e7b1cb17", "Impuesto"), "#5C3D6BFF");
                case "quarantine": return (L.T("AIL_e7b1cb18", "Cuarentena"), "#2E6B6BFF");
                case "clan": return (L.T("AIL_e7b1cb19", "Clan"), "#4A3524FF");
                case "law": return (L.T("AIL_e7b1c5ec", "Ley"), "#2B3760FF");
                default: return (L.T("AIL_e7b1cb1a", "Decisión"), "#3B3A4AFF");
            }
        }

        private static string Clean(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");

        /// <summary>"Place: Pravend (Vlandia)", or the first three places and how many more.</summary>
        private static string PlaceLine(NewsItem n)
        {
            try
            {
                var ids = new System.Collections.Generic.List<string>();
                string evId = EventIdOf(n);
                var ev = WorldSim.State.Events.FirstOrDefault(e => e.Id == evId);
                if (ev != null) ids.AddRange(ev.Settlements);
                if (!string.IsNullOrEmpty(n.SettlementId) && !ids.Contains(n.SettlementId)) ids.Insert(0, n.SettlementId);
                var names = ids.Select(id => TaleWorlds.CampaignSystem.Settlements.Settlement.Find(id)).Where(st => st != null)
                               .Select(st => st.MapFaction != null && st.MapFaction.Name != null ? $"{st.Name} ({st.MapFaction.Name})" : st.Name.ToString()).Distinct().ToList();
                if (names.Count == 0) return "";
                if (names.Count == 1) return L.F("AIL_e7b1cb1b", $"Lugar: {names[0]}");
                if (names.Count <= 3) return L.F("AIL_e7b1cb1c", $"Lugares: {string.Join(", ", names)}");
                return L.F("AIL_e7b1cb1d", $"Lugares: {string.Join(", ", names.Take(3))} y {names.Count - 3} más");
            }
            catch { return ""; }
        }

        /// <summary>The outbreak an event started: what, where, how bad, and whether it is still going.</summary>
        private static string DiseaseLine(NewsDisease d)
        {
            if (d == null) return "";
            try
            {
                string where = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(d.SettlementId)?.Name?.ToString() ?? "?";
                int s = d.Severity;
                string how = s <= 1 ? L.T("AIL_e7b1c94e", "leve") : s == 2 ? L.T("AIL_e7b1c94f", "preocupante") : s == 3 ? L.T("AIL_e7b1c943", "grave") : s == 4 ? L.T("AIL_44011913", "mortífera") : L.T("AIL_e45c3132", "una peste");
                string line = L.F("AIL_e7b1cb1e", $"{Clean(d.Name)} en {where}: {how}");
                var o = WorldSim.State.Outbreaks.FirstOrDefault(x => x.Id == d.Id);
                if (o == null) return line + L.T("AIL_e7b1cb1f", " (ya ha pasado)");
                int left = Math.Max(1, (int)Math.Ceiling(o.UntilDay - CampaignTime.Now.ToDays));
                return line + (o.Quarantined ? L.F("AIL_e7b1cb20", $" (en cuarentena, quedan {left} días)") : L.F("AIL_e7b1cb21", $" (quedan {left} días)"));
            }
            catch { return ""; }
        }

        /// <summary>A card for one news item, with its category key.</summary>
        private static NewsCardVM CardOf(NewsItem n, double now, out string category)
        {
            int d = (int)(now - n.Day);
            string when = d <= 0 ? L.T("AIL_564e2e23", "Hoy") : d == 1 ? L.T("AIL_0529fe40", "Hace 1 día") : L.F("AIL_b2b050b8", $"Hace {d} días");
            var who = n.Kingdoms.Select(id => Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == id)?.Name?.ToString()).Where(x => x != null).Distinct().ToList();
            string place = PlaceLine(n);
            // with no kingdom the place is who it concerns, and the place line would only say it again
            if (who.Count == 0 && !string.IsNullOrEmpty(n.SettlementId)) { var st = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(n.SettlementId); if (st != null) { who.Add(st.Name.ToString()); if (!place.Contains(",")) place = ""; } }
            string tag = TagOf(n, out category);
            string title = n.Type == "statement" && string.IsNullOrWhiteSpace(n.TitleEs) ? L.T("AIL_99bcbc8e", "Declaración de un soberano") : n.DisplayTitle;
            var card = new NewsCardVM(Clean(title), who.Count > 0 ? (n.Type == "statement" ? "" : L.T("AIL_08cd64bc", "Participantes: ")) + Clean(string.Join(", ", who)) : "",
                Clean(n.DisplayText), "", tag, when) { TagColorValue = CategoryColor(category), Place = n.Type == "statement" ? "" : Clean(place) };
            if (n.Chapter > 0 && (n.Chapter > 1 || !string.IsNullOrEmpty(n.StoryId))) card.Chapter = L.F("AIL_e7b1cb22", $"Capítulo {n.Chapter}");
            if (n.Decisions != null)
                foreach (var dec in n.Decisions.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Text)))
                {
                    var (label, color) = DecisionTag(dec.Kind);
                    card.Decisions.Add(new NewsLineVM(Clean(dec.Text), 3, label, color));
                }
            // lasting effects belong to the story's newest chapter; an older chapter keeps only the blows it dealt
            bool older = n.Id != null && n.Id.Contains("#");
            foreach (var (text, tone) in WorldSim.EffectLines(older ? null : EventIdOf(n), n.Hits))
                card.EffectLines.Add(new NewsLineVM(Clean(text), tone));
            card.Disease = Clean(DiseaseLine(n.Disease));
            card.SearchText = string.Join("\n", card.Title, card.Participants, card.Text, card.Place, card.Tag, card.Disease,
                string.Join("\n", card.Decisions.Select(x => x.Label + " " + x.Text)), string.Join("\n", card.EffectLines.Select(x => x.Text))).ToLowerInvariant();
            return card;
        }

        private static void FillNews(NewsFeedVM vm, NewsView view)
        {
            var cfg = S.Get;
            double now = CampaignTime.Now.ToDays;
            string search = (vm.SearchText ?? "").Trim().ToLowerInvariant();

            // everything the "see everything" switch lets through, newest first; law cards first of all
            var all = new System.Collections.Generic.List<(NewsCardVM card, string category, bool statement, int day)>();
            foreach (var c in LawCards(view.All))
            {
                c.SearchText = (c.Title + "\n" + c.Text).ToLowerInvariant();
                all.Add((c, "law", true, int.MaxValue));
            }
            foreach (var n in NewsStore.All.Where(n => now - n.Day <= cfg.NewsLifetimeDays && (view.All || IsEvent(n))).OrderByDescending(n => n.Day).Take(120))
            {
                try { var card = CardOf(n, now, out string cat); all.Add((card, cat, n.Type == "statement", (int)Math.Floor(n.Day))); }
                catch (Exception ex) { Core.Log.Error("A news card", ex); }
            }

            // record buttons, with their counts
            int nStatements = all.Count(x => x.statement), nEvents = all.Count - nStatements;
            if (vm.Records.Count == 0)
                foreach (var key in new[] { "all", "events", "statements" })
                    vm.Records.Add(new NewsFilterVM(key, "", key == "all" ? "#3B3A4AFF" : key == "events" ? "#1F4A44FF" : "#6B5A1EFF",
                        f => { view.Record = f.Key; view.Category = null; FillNews(vm, view); }));
            foreach (var f in vm.Records)
            {
                f.Text = f.Key == "all" ? L.F("AIL_e7b1cb23", $"Todo ({all.Count})") : f.Key == "events" ? L.F("AIL_e7b1cb24", $"Sucesos ({nEvents})") : L.F("AIL_e7b1cb25", $"Declaraciones ({nStatements})");
                f.IsSelected = f.Key == view.Record;
            }
            var inRecord = all.Where(x => view.Record == "all" || (view.Record == "statements") == x.statement).ToList();

            // category buttons, for the categories in that record kind
            var cats = inRecord.GroupBy(x => x.category).Select(g => (key: g.Key, label: g.First().card.Tag, count: g.Count())).OrderByDescending(x => x.count).ToList();
            if (view.Category != null && !cats.Any(c => c.key == view.Category)) view.Category = null;
            bool sameKeys = vm.Categories.Count == cats.Count && vm.Categories.Select(c => c.Key).SequenceEqual(cats.Select(c => c.key));
            if (!sameKeys)
            {
                vm.Categories.Clear();
                foreach (var c in cats)
                    vm.Categories.Add(new NewsFilterVM(c.key, "", CategoryColor(c.key), f => { view.Category = view.Category == f.Key ? null : f.Key; FillNews(vm, view); }));
            }
            foreach (var f in vm.Categories)
            {
                var c = cats.First(x => x.key == f.Key);
                f.Text = $"{c.label} ({c.count})";
                f.IsSelected = f.Key == view.Category;
            }

            var shown = inRecord.Where(x => (view.Category == null || x.category == view.Category) && (search.Length == 0 || x.card.SearchText.Contains(search))).ToList();

            // the cards, a header opening each day
            vm.Cards.Clear();
            int lastDay = int.MinValue;
            int today = (int)Math.Floor(now);
            foreach (var x in shown)
            {
                if (x.day != int.MaxValue && x.day != lastDay)
                {
                    lastDay = x.day;
                    vm.Cards.Add(NewsCardVM.Header(DayHeader(x.day, today)));
                }
                vm.Cards.Add(x.card);
            }
            if (vm.Cards.Count == 0)
            {
                string why = all.Count > 0 ? L.T("AIL_e7b1cb26", "Ningún registro coincide con el filtro o la búsqueda.")
                           : view.All ? L.T("AIL_6549b43d", "Todavía no ha pasado nada digno de contarse.")
                           : L.T("AIL_cf032cac", "Todavía no hay ningún suceso de los que dan que hablar. Con «Ver todo» salen también las capturas, batallas y saqueos del día a día.");
                vm.Cards.Add(new NewsCardVM(L.T("AIL_dd4380be", "Sin sucesos"), "", why, "", "", "", 2));
            }
            vm.ToggleText = view.All ? L.T("AIL_f96ef1d1", "Solo sucesos") : L.T("AIL_ebad7b06", "Ver todo");
        }

        /// <summary>"Today · 12 Summer 1084", "Yesterday · …", "5 days ago · …".</summary>
        private static string DayHeader(int day, int today)
        {
            int ago = today - day;
            string when = ago <= 0 ? L.T("AIL_564e2e23", "Hoy") : ago == 1 ? L.T("AIL_58a5a0a4", "Ayer") : L.F("AIL_b2b050b8", $"Hace {ago} días");
            try
            {
                string date = CampaignTime.Days(day).ToString();
                return string.IsNullOrWhiteSpace(date) ? when : when + " · " + date;
            }
            catch { return when; }
        }

        /// <summary>Laws created by rulers, one card per kingdom: the player's own always, the rest with "see everything".</summary>
        private static System.Collections.Generic.List<NewsCardVM> LawCards(bool all)
        {
            var cards = new System.Collections.Generic.List<NewsCardVM>();
            try
            {
                if (!Laws.Enabled) return cards;
                var mine = Hero.MainHero?.MapFaction as Kingdom;
                foreach (var k in WorldSim.Kingdoms.OrderByDescending(x => x == mine))
                {
                    if (k != mine && !all) continue;
                    var laws = Laws.Of(k);
                    if (laws.Count == 0) continue;
                    var sb = new StringBuilder();
                    foreach (var law in laws)
                    {
                        var fx = Laws.DescribeAll(law);
                        sb.Append("«").Append((law.Name ?? "?").Replace("{", "(").Replace("}", ")")).Append("»");
                        if (!string.IsNullOrWhiteSpace(law.Text)) sb.Append(": ").Append(law.Text.Replace("{", "(").Replace("}", ")"));
                        sb.Append("\n");
                        if (fx.Count > 0) sb.Append(string.Join("; ", fx)).Append("\n");
                        sb.Append("\n");
                    }
                    cards.Add(new NewsCardVM(string.Format(L.T("AIL_e7b1c5eb", "Leyes en vigor en {0}"), k.Name), "", sb.ToString().TrimEnd(), "", L.T("AIL_e7b1c5ec", "Ley"), "", 1) { TagColorValue = CategoryColor("law") });
                }
            }
            catch (Exception ex) { Core.Log.Error("Laws in the news window", ex); }
            return cards;
        }

        /// <summary>
        /// Event id for a news item. New items carry it; an earlier chapter of a story carries it before its "#n";
        /// older saves are matched by title.
        /// </summary>
        private static string EventIdOf(NewsItem n)
        {
            if (n == null) return null;
            if (!string.IsNullOrEmpty(n.StoryId)) return n.StoryId;
            string id = n.Id ?? "";
            int hash = id.IndexOf('#');
            if (hash > 0) return id.Substring(0, hash);
            if (n.Type == null || !n.Type.StartsWith("event_")) return n.Id;
            if (WorldSim.State.Events.Any(e => e.Id == n.Id)) return n.Id;
            return WorldSim.State.Events.LastOrDefault(e => !string.IsNullOrEmpty(n.TitleEs) && e.Title == n.TitleEs)?.Id ?? n.Id;
        }

        /// <summary>Old list and detail window, kept as a fallback in case the new one fails to open.</summary>
        private static void OpenNewsOld()
        {
            var cfg = S.Get;
            double now = CampaignTime.Now.ToDays;
            var vm = new BookVM { Title = L.T("AIL_85c6e155", "Noticias de Calradia") };
            var list = NewsStore.All.Where(n => now - n.Day <= cfg.NewsLifetimeDays).OrderByDescending(n => n.Day).Take(80).ToList();
            foreach (var n in list)
            {
                int d = (int)(now - n.Day);
                string when = d <= 0 ? L.T("AIL_564e2e23", "Hoy") : d == 1 ? L.T("AIL_58a5a0a4", "Ayer") : L.F("AIL_b2b050b8", $"Hace {d} días");
                string kind = TypeName(n.Type);
                var title = n.DisplayTitle;
                vm.Entries.Add(new BookEntryVM(title, L.F("AIL_beccbd63", $"{kind} · {when} · importancia {n.Importance}"), n, e => ShowNews(vm, e)));
            }
            // Rulers can make declarations from here
            if (Diplomacy.PlayerCanDeclare)
            {
                vm.ActionText = L.T("AIL_7611f3a8", "Hacer una declaración");
                vm.HasAction = true;
                vm.OnAction = e => { GauntletWindow.Current?.Close(); Core.MainThread.Post(Diplomacy.PlayerStatement); };
            }
            if (vm.Entries.Count == 0) vm.Select(null, L.T("AIL_22932a37", "Sin noticias"), L.T("AIL_53647ac2", "Todavía no ha pasado nada digno de contarse. Las guerras, paces, asedios, batallas y rumores aparecerán aquí."));
            else ShowNews(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowNews(BookVM vm, BookEntryVM e)
        {
            var n = (NewsItem)e.Tag;
            int d = (int)(CampaignTime.Now.ToDays - n.Day);
            var b = new StringBuilder();
            b.Append(n.DisplayText).Append("\n\n");
            b.Append(L.F("AIL_f8647bfd", $"Tipo: {TypeName(n.Type)}   ·   Hace {d} día(s)   ·   Importancia {n.Importance} ({ImportanceName(n.Importance)})"));
            var kingdoms = n.Kingdoms.Select(id => Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == id)?.Name?.ToString()).Where(x => x != null).ToList();
            if (kingdoms.Count > 0) b.Append(L.T("AIL_35c15083", "\nReinos: ")).Append(string.Join(", ", kingdoms));
            var heroes = n.Heroes.Select(id => Hero.Find(id)?.Name?.ToString()).Where(x => x != null).ToList();
            if (heroes.Count > 0) b.Append(L.T("AIL_5de8b992", "\nImplicados: ")).Append(string.Join(", ", heroes));
            if (n.PlayerInvolved) b.Append(L.T("AIL_40a03089", "\n(Estás implicado)"));
            vm.Select(e, n.DisplayTitle, b.ToString().Replace("{", "(").Replace("}", ")"));
        }

        /// <summary>AI quests (open and recent) and the documents you carry.</summary>
        public static void OpenQuests()
        {
            double now = CampaignTime.Now.ToDays;
            var vm = new BookVM { Title = L.T("AIL_32bdb403", "Encargos") };
            foreach (var q in QuestSystem.Quests.OrderBy(q => q.Active ? 0 : 1).ThenBy(q => q.Active ? q.DueDay : -q.EndDay).Take(80))
            {
                string state = q.Active ? L.F("AIL_e1d05f2f", $"Quedan {Math.Max(0, (int)Math.Ceiling(q.DueDay - now))} días")
                             : q.State == "completed" ? "Cumplido" : q.State == "expired" ? L.T("AIL_5a9ce79b", "Plazo vencido") : "Fracasado";
                vm.Entries.Add(new BookEntryVM(q.Name, $"{q.GiverName} · {state}", q, e => ShowQuest(vm, e)));
            }
            foreach (var it in Belongings.Items)
                vm.Entries.Add(new BookEntryVM(it.Name, L.F("AIL_095b55a1", $"Documento · de {it.FromName}"), it, e => ShowQuest(vm, e)));
            if (vm.Entries.Count == 0) vm.Select(null, L.T("AIL_f92abc25", "Sin encargos"), L.T("AIL_a54a9d45", "Nadie te ha encargado nada todavía. Cuando alguien te pida un favor y lo aceptes, aparecerá aquí con su plazo y su recompensa. También verás aquí los documentos que te entreguen."));
            else ShowQuest(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowQuest(BookVM vm, BookEntryVM e)
        {
            var b = new StringBuilder();
            if (e.Tag is RpItem it)
            {
                b.Append(it.Description).Append(L.F("AIL_61468823", $"\n\nTe lo entregó {it.FromName} hace {(int)(CampaignTime.Now.ToDays - it.Day)} día(s). Puedes enseñarlo o mencionarlo al hablar con cualquiera."));
                vm.Select(e, it.Name, b.ToString().Replace("{", "(").Replace("}", ")"));
                return;
            }
            var q = (AiQuest)e.Tag;
            double now = CampaignTime.Now.ToDays;
            b.Append(q.Description).Append("\n\n");
            b.Append(L.F("AIL_5983def9", $"Te lo encargó: {q.GiverName}   ·   hace {(int)(now - q.CreatedDay)} día(s)"));
            if (q.Active) b.Append(L.F("AIL_eacf656a", $"\nPlazo: quedan {Math.Max(0, (int)Math.Ceiling(q.DueDay - now))} día(s)"));
            else b.Append(L.F("AIL_3a52fd31", $"\nEstado: {(q.State == "completed" ? L.T("AIL_a9d11dfa", "cumplido") : q.State == "expired" ? L.T("AIL_9b9b3ffb", "plazo vencido") : L.T("AIL_f45ab3e9", "fracasado"))}"));
            if (q.RewardGold > 0) b.Append(L.F("AIL_fd8ad9a8", $"\nRecompensa prometida: {q.RewardGold} denarios"));
            if (q.ProgressTarget > 0) b.Append(L.F("AIL_a91252d2", $"\nProgreso: {q.Progress}/{q.ProgressTarget} {q.ProgressLabel}"));
            if (q.Active) b.Append(L.F("AIL_adfeb4cc", $"\n\nPara cerrarlo, habla con {q.GiverName} y cuéntale lo que has hecho: es quien decide si está cumplido."));
            vm.Select(e, q.Name, b.ToString().Replace("{", "(").Replace("}", ")"));
        }

        /// <summary>The book opens on its chapters when there are any, and on the people otherwise.</summary>
        public static void OpenMemories()
        {
            bool anyPage = false;
            try { anyPage = MemoryStore.AllHeroes.Any(m => m.Pages != null && m.Pages.Count > 0); } catch { }
            if (anyPage) OpenChapters(); else OpenPeople();
        }

        /// <summary>
        /// Chapters: every page of every character's memories, newest first. Left, the list with whose page it is and
        /// how long ago; right, its illustration (if it has one), the title and the text.
        /// </summary>
        public static void OpenChapters()
        {
            var vm = new BookVM { Title = L.T("AIL_18fe8281", "Libro de memorias") };
            double now = CampaignTime.Now.ToDays;
            var all = new System.Collections.Generic.List<(HeroMemory mem, MemoryPage page, string who)>();
            foreach (var m in MemoryStore.AllHeroes)
            {
                if (m.Pages == null || m.Pages.Count == 0) continue;
                string who = Hero.Find(m.HeroId)?.Name?.ToString() ?? m.HeroName ?? "";
                foreach (var pg in m.Pages) all.Add((m, pg, who));
            }
            foreach (var x in all.OrderByDescending(x => x.page.Day).Take(300))
            {
                int ago = Math.Max(0, (int)(now - x.page.Day));
                string sub = ago == 0 ? L.F("AIL_e7b1c4e6", $"{x.who} · hoy") : L.F("AIL_e7b1c4e7", $"{x.who} · hace {ago} día(s)");
                vm.Entries.Add(new BookEntryVM(x.page.Title ?? L.T("AIL_463eb3a3", "Sin título"), sub, x, e => ShowChapter(vm, e)));
            }
            vm.Action2Text = L.T("AIL_e7b1c4e8", "Personas"); vm.HasAction2 = true;
            vm.OnAction2 = () => { GauntletWindow.Current?.Close(); Core.MainThread.Post(OpenPeople); };
            vm.OnAction = e =>
            {
                if (!(e.Tag is ValueTuple<HeroMemory, MemoryPage, string> t) || !World.SceneImages.Enabled) return;
                if (World.SceneImages.IsBusy) return;
                var page = t.Item2;
                vm.ActionText = L.T("AIL_e7b1c4e9", "Dibujando…");
                World.SceneImages.Request(t.Item1, page, () =>
                {
                    // only repaint if the book is still open on this same chapter
                    try { if (GauntletWindow.Current != null && vm.Selected == e) ShowChapter(vm, e); } catch { }
                    if (World.SceneImages.PathOf(page) == null)
                        InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1c4ea", "No se pudo generar la imagen. El motivo está en el registro del mod."), Colors.Red));
                });
            };
            if (vm.Entries.Count == 0) vm.Select(null, L.T("AIL_0872713a", "Vacío"), L.T("AIL_e7b1c4eb", "Todavía no hay capítulos. Se escriben solos cuando una charla larga pasa a la memoria de un personaje."));
            else ShowChapter(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowChapter(BookVM vm, BookEntryVM e)
        {
            var t = (ValueTuple<HeroMemory, MemoryPage, string>)e.Tag;
            var page = t.Item2;
            string path = World.SceneImages.PathOf(page);
            vm.HasAction = World.SceneImages.Enabled;
            vm.ActionText = World.SceneImages.IsBusy ? L.T("AIL_e7b1c4e9", "Dibujando…") : path == null ? L.T("AIL_e7b1c4ec", "Generar imagen") : L.T("AIL_e7b1c4ed", "Otra imagen");
            vm.Select(e, page.Title ?? "", ((page.Text ?? "") + "\n\n" + e.Subtitle).Replace("{", "(").Replace("}", ")"), path);
        }

        public static void OpenPeople()
        {
            var vm = new BookVM { Title = L.T("AIL_18fe8281", "Libro de memorias"), ActionText = L.T("AIL_7623bdb7", "Hablar"), HasAction = false };
            vm.Action2Text = L.T("AIL_e7b1c4ee", "Capítulos"); vm.HasAction2 = true;
            vm.OnAction2 = () => { GauntletWindow.Current?.Close(); Core.MainThread.Post(OpenChapters); };
            var list = MemoryStore.AllHeroes
                .Where(m => m.ConversationCount > 0 || m.Turns.Count > 0 || !string.IsNullOrEmpty(m.Summary))
                .Select(m => (m, h: Hero.Find(m.HeroId))).Where(x => x.h != null)
                .OrderByDescending(x => x.m.LastConversationDay).Take(200).ToList();
            foreach (var x in list)
            {
                int rel = (int)x.h.GetRelationWithPlayer();
                string sub = L.F("AIL_734fab9c", $"{(x.h.IsDead ? L.T("AIL_986efa13", "Fallecido · ") : "")}Relación {rel:+0;-0;0} · {x.m.ConversationCount} charla(s)");
                vm.Entries.Add(new BookEntryVM(x.h.Name.ToString(), sub, x.h, e => ShowHero(vm, e)));
            }
            // Role-play documents and items you carry
            foreach (var it in Belongings.Items)
                vm.Entries.Add(new BookEntryVM(it.Name, L.F("AIL_095b55a1", $"Documento · de {it.FromName}"), it, e => ShowQuest(vm, e)));
            vm.OnAction = e =>
            {
                var h = e.Tag as Hero;
                if (h == null || !h.IsAlive) return;
                GauntletWindow.Current?.Close();
                /* letters are sent from the town backstreet menu */
            };
            if (vm.Entries.Count == 0) vm.Select(null, L.T("AIL_0872713a", "Vacío"), L.T("AIL_b93602c9", "Aún no has hablado con nadie. Cada persona con la que hables aparecerá aquí con lo que recuerda de ti."));
            else ShowHero(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowHero(BookVM vm, BookEntryVM e)
        {
            var h = (Hero)e.Tag;
            var m = MemoryStore.Find(h.StringId);
            var b = new StringBuilder();
            if (h.Clan != null) b.Append($"Clan {h.Clan.Name}").Append(h.MapFaction is Kingdom k ? $" · {k.Name}" : "").Append("\n");
            b.Append(L.F("AIL_f3138943", $"Relación: {(int)h.GetRelationWithPlayer():+0;-0;0}"));
            if (m != null)
            {
                if (!string.IsNullOrWhiteSpace(m.Mood)) b.Append(L.F("AIL_03bc7447", $"   ·   Ánimo: {m.Mood}"));
                b.Append(L.F("AIL_0ddba7d9", $"\nTe conoce como: {m.KnownPlayerName ?? (Prompt.ContextBuilder.WhyKnowsPlayer(Speaker.FromHero(h)) != null ? Hero.MainHero.Name.ToString() : L.T("AIL_99316d74", "(desconocido)"))}"));
                if (m.Profile != null && !m.Profile.IsEmpty)
                {
                    b.Append(L.T("AIL_d9727a35", "\n\nPERSONALIDAD\n")).Append(m.Profile.Personality);
                    if (!string.IsNullOrWhiteSpace(m.Profile.Backstory)) b.Append(L.T("AIL_e5df30fb", "\n\nPASADO\n")).Append(m.Profile.Backstory);
                    if (!string.IsNullOrWhiteSpace(m.Profile.Goals)) b.Append(L.T("AIL_73afba9b", "\n\nLO QUE BUSCA\n")).Append(m.Profile.Goals);
                }
                if (!string.IsNullOrWhiteSpace(m.Summary)) b.Append(L.T("AIL_9bf3e70c", "\n\nLO QUE RECUERDA DE VOSOTROS\n")).Append(m.Summary);
                if (m.Pages != null && m.Pages.Count > 0)
                {
                    b.Append(L.T("AIL_aa4dc800", "\n\nPÁGINAS DE SU MEMORIA"));
                    foreach (var pg in m.Pages.Skip(Math.Max(0, m.Pages.Count - 8)))
                        b.Append(L.F("AIL_0e382db2", $"\n\n— {pg.Title} (hace {(int)(CampaignTime.Now.ToDays - pg.Day)} días) —\n")).Append(pg.Text);
                }
                if (!string.IsNullOrEmpty(m.Archetype)) b.Append(L.T("AIL_949e7526", "\n\nMOLDE\n")).Append(m.Archetype);
                if (m.Notes.Count > 0) b.Append(L.T("AIL_570feb77", "\n\nRECUERDOS\n")).Append(string.Join("\n", m.Notes.Select(n => "· " + n)));
                var said = m.Turns.Where(t => !t.IsNote).ToList();
                var lastTurns = said.Skip(Math.Max(0, said.Count - 6)).ToList();
                if (lastTurns.Count > 0)
                {
                    b.Append(L.T("AIL_5cc4dc1c", "\n\nÚLTIMAS PALABRAS\n"));
                    foreach (var t in lastTurns) b.Append(t.Role == "player" ? L.T("AIL_63ea9af4", "Tú: ") : h.Name + ": ").Append(t.Text).Append("\n");
                }
            }
            vm.HasAction = false; // letters are sent from the town backstreet menu
            vm.ActionText = L.T("AIL_bd58c13d", "Escribir carta");
            vm.Select(e, h.Name.ToString(), b.ToString().Replace("{", "(").Replace("}", ")"));
        }

        /// <summary>A picture of a moment of a conversation, in the book window, with what was said beside it.</summary>
        public static void OpenPicture(string path, string title, string text)
        {
            try
            {
                var vm = new BookVM { Title = title };
                var e = new BookEntryVM(title, "", null, x => { });
                vm.Entries.Add(e);
                vm.Select(e, title, (text ?? "").Replace("{", "(").Replace("}", ")"), path);
                Open("AILegacyBook", vm);
            }
            catch (Exception ex) { Log.Error("Scene image", ex); }
        }

        private static void Open(string movie, BookVM vm)
        {
            GauntletWindow w = null;
            vm.OnClose = () => w?.Close();
            w = GauntletWindow.Open(movie, vm, () => w?.Close());
            if (w == null)
                InformationManager.ShowInquiry(new InquiryData(vm.Title, vm.DetailText, true, false, L.T("AIL_e8438e2a", "Cerrar"), "", null, null), true);
        }

        private static string TypeName(string t)
        {
            switch (t)
            {
                case "war": return L.T("AIL_13afe103", "Guerra");
                case "peace": return L.T("AIL_c05b0270", "Paz");
                case "alliance": return L.T("AIL_e7b1c762", "Alianza");
                case "capture": return L.T("AIL_cf4c4fba", "Conquista");
                case "fief": return L.T("AIL_c52373ae", "Feudo");
                case "battle": return L.T("AIL_eb66dabe", "Batalla");
                case "raid": return L.T("AIL_ec31e35b", "Saqueo");
                case "siege": return L.T("AIL_a0bc9158", "Asedio");
                case "death": return L.T("AIL_7e5382b7", "Muerte");
                case "ruler": return L.T("AIL_901ece79", "Nuevo soberano");
                case "kingdom": return L.T("AIL_fc0a69e3", "Caída de un reino");
                case "clan": return L.T("AIL_14ed1b61", "Clanes");
                case "prisoner": return L.T("AIL_11783c51", "Prisionero");
                case "rumor": return L.T("AIL_be7cce2e", "Rumor");
                case "statement": return L.T("AIL_99bcbc8e", "Declaración de un soberano");
                case "event_political": return L.T("AIL_41ac9547", "Política");
                case "event_military": return L.T("AIL_13afe103", "Guerra");
                case "event_economic": return L.T("AIL_0d2bb14b", "Economía");
                case "event_social": return L.T("AIL_44de911b", "Sociedad");
                case "event_mysterious": return L.T("AIL_c3a1387b", "Misterio");
                case "event_news": return L.T("AIL_5418db17", "Suceso");
                case "event_disease": return L.T("AIL_ee39700e", "Enfermedad");
                case "custom": return L.T("AIL_043426f0", "Evento");
                default: return string.IsNullOrEmpty(t) ? "Noticia" : char.ToUpper(t[0]) + t.Substring(1);
            }
        }

        private static string ImportanceName(int i) => i >= 4 ? L.T("AIL_fe2134ff", "todo el mundo") : i == 3 ? L.T("AIL_5179ad99", "todo el reino") : i == 2 ? L.T("AIL_1d84efed", "la corte") : L.T("AIL_4c08be8b", "el lugar");
    }

    /// <summary>Fixed buttons on the campaign map. The layer is transparent, only the buttons take clicks.</summary>
    internal static class MapHud
    {
        private static GauntletLayer _layer;
        private static GauntletMovieIdentifier _movie;
        private static ScreenBase _screen;
        private static MapButtonsVM _vm;

        public static void Tick()
        {
            try
            {
                GauntletWindow.Current?.Tick();
                bool want = Campaign.Current != null && S.Get.ShowMapButtons;
                var top = ScreenManager.TopScreen;
                bool onMap = want && top != null && top.GetType().Name.Contains("MapScreen") && GameStateManager.Current?.ActiveState is MapState;

                if (!want) { Detach(); return; }
                if (onMap && top != _screen) Attach(top);
            }
            catch (Exception ex) { Log.Error("Map buttons", ex); Detach(); }
        }

        private static void Attach(ScreenBase screen)
        {
            Detach();
            _vm = new MapButtonsVM { OnNews = Books.OpenNews, OnMemories = Books.OpenMemories, OnQuests = Books.OpenQuests };
            Log.Info("Creating map buttons...");
            _layer = new GauntletLayer("AILegacyMapHud", 300);
            _movie = _layer.LoadMovie("AILegacyMapButtons", _vm);
            // isMouseVisible false, same as the map's own layers. With true the cursor gets stuck and the camera can't rotate.
            _layer.InputRestrictions.SetInputRestrictions(false, InputUsageMask.MouseButtons);
            screen.AddLayer(_layer);
            _screen = screen;
            Log.Info("Map buttons added to " + screen.GetType().Name);
        }

        public static void Detach()
        {
            try
            {
                if (_layer != null)
                {
                    if (_movie != null) _layer.ReleaseMovie(_movie);
                    _screen?.RemoveLayer(_layer);
                }
            }
            catch { }
            _layer = null; _movie = null; _screen = null; _vm = null;
        }
    }
}
