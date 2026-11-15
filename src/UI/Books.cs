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
            catch (Exception ex) { Log.Error("No se pudo abrir la ventana " + movie, ex); return null; }
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
            catch (Exception ex) { Log.Error("Cerrar ventana", ex); }
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
            catch (Exception ex) { Core.Log.Error("Repasar lo hablado", ex); }
        }

        /// <summary>World news in a single column, read like a town crier's notice: title, who it concerns, what happened, effects, type and date.</summary>
        public static void OpenNews()
        {
            try
            {
                var vm = new NewsFeedVM { Title = L.T("AIL_52b8aa60", "Sucesos del mundo") };
                bool all = false;
                FillNews(vm, all);
                vm.OnToggle = () => { all = !all; FillNews(vm, all); };
                // Rulers can make declarations from here
                if (Diplomacy.PlayerCanDeclare) { vm.ActionText = L.T("AIL_7611f3a8", "Hacer una declaración"); vm.HasAction = true; }
                GauntletWindow w = null;
                vm.OnClose = () => w?.Close();
                vm.OnAction = () => { w?.Close(); Core.MainThread.Post(Diplomacy.PlayerStatement); };
                w = GauntletWindow.Open("AILegacyNews", vm, () => w?.Close());
                if (w == null) OpenNewsOld();
            }
            catch (Exception ex) { Core.Log.Error("Ventana de noticias", ex); OpenNewsOld(); }
        }

        /// <summary>True for news worth telling (world events, royal statements, big happenings). The rest is the steady trickle of captures and skirmishes.</summary>
        private static bool IsEvent(NewsItem n)
        {
            string t = n.Type ?? "";
            if (t.StartsWith("event_") || t == "statement" || t == "decree" || t == "custom") return true;
            if (t == "rumor") return !string.IsNullOrWhiteSpace(n.TextEs);
            return n.Importance >= 3 && (t == "war" || t == "peace" || t == "ruler" || t == "kingdom" || t == "death");
        }

        /// <summary>One-word tag for the bottom left corner. kind: 0 = local, 1 = politics, 2 = everything else.</summary>
        private static string TagOf(NewsItem n, out int kind)
        {
            string t = n.Type ?? "";
            if (t == "statement") { kind = 1; return L.T("AIL_e14ea467", "Declaración"); }
            if (t == "decree") { kind = 1; return "Sentencia"; }
            if (t == "event_political" || t == "war" || t == "peace" || t == "ruler" || t == "kingdom") { kind = 1; return L.T("AIL_41ac9547", "Política"); }
            if (n.Importance <= 1) { kind = 0; return L.T("AIL_28b223a8", "Local"); }
            kind = 2;
            switch (t)
            {
                case "event_military": case "battle": case "siege": case "raid": case "capture": return L.T("AIL_78abe94b", "Militar");
                case "event_economic": return L.T("AIL_0d2bb14b", "Economía");
                case "event_disease": return L.T("AIL_ee39700e", "Enfermedad");
                case "event_mysterious": return L.T("AIL_c3a1387b", "Misterio");
                case "rumor": return L.T("AIL_be7cce2e", "Rumor");
                case "death": return L.T("AIL_7e5382b7", "Muerte");
                case "prisoner": return L.T("AIL_11783c51", "Prisionero");
                default: return "Social";
            }
        }

        private static void FillNews(NewsFeedVM vm, bool all)
        {
            var cfg = S.Get;
            double now = CampaignTime.Now.ToDays;
            string clean(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");
            vm.Cards.Clear();
            AddLawCards(vm, all);
            foreach (var n in NewsStore.All.Where(n => now - n.Day <= cfg.NewsLifetimeDays && (all || IsEvent(n))).OrderByDescending(n => n.Day).Take(80))
            {
                int d = (int)(now - n.Day);
                string when = d <= 0 ? L.T("AIL_564e2e23", "Hoy") : d == 1 ? L.T("AIL_0529fe40", "Hace 1 día") : L.F("AIL_b2b050b8", $"Hace {d} días");
                var who = n.Kingdoms.Select(id => Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == id)?.Name?.ToString()).Where(x => x != null).Distinct().ToList();
                if (who.Count == 0 && !string.IsNullOrEmpty(n.SettlementId)) { var st = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(n.SettlementId); if (st != null) who.Add(st.Name.ToString()); }
                string tag = TagOf(n, out int kind);
                string title = n.Type == "statement" && string.IsNullOrWhiteSpace(n.TitleEs) ? L.T("AIL_99bcbc8e", "Declaración de un soberano") : n.DisplayTitle;
                vm.Cards.Add(new NewsCardVM(clean(title), who.Count > 0 ? (n.Type == "statement" ? "" : L.T("AIL_08cd64bc", "Participantes: ")) + clean(string.Join(", ", who)) : "",
                    clean(n.DisplayText), clean(WorldSim.EffectsLineEs(EventIdOf(n), n.SettlementId, n.Type)), tag, when, kind));
            }
            if (vm.Cards.Count == 0)
                vm.Cards.Add(new NewsCardVM(L.T("AIL_dd4380be", "Sin sucesos"), "", all ? L.T("AIL_6549b43d", "Todavía no ha pasado nada digno de contarse.") : L.T("AIL_cf032cac", "Todavía no hay ningún suceso de los que dan que hablar. Con «Ver todo» salen también las capturas, batallas y saqueos del día a día."), "", "", "", 2));
            vm.ToggleText = all ? L.T("AIL_f96ef1d1", "Solo sucesos") : L.T("AIL_ebad7b06", "Ver todo");
        }

        /// <summary>Laws created by rulers, one card per kingdom: the player's own always, the rest with "see everything".</summary>
        private static void AddLawCards(NewsFeedVM vm, bool all)
        {
            try
            {
                if (!Laws.Enabled) return;
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
                    vm.Cards.Add(new NewsCardVM(string.Format(L.T("AIL_e7b1c5eb", "Leyes en vigor en {0}"), k.Name), "", sb.ToString().TrimEnd(), "", L.T("AIL_e7b1c5ec", "Ley"), "", 1));
                }
            }
            catch (Exception ex) { Core.Log.Error("Leyes en la ventana de noticias", ex); }
        }

        /// <summary>Event id for a news item. New items carry it, older saves are matched by title.</summary>
        private static string EventIdOf(NewsItem n)
        {
            if (n == null || n.Type == null || !n.Type.StartsWith("event_")) return n?.Id;
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
            catch (Exception ex) { Log.Error("Botones del mapa", ex); Detach(); }
        }

        private static void Attach(ScreenBase screen)
        {
            Detach();
            _vm = new MapButtonsVM { OnNews = Books.OpenNews, OnMemories = Books.OpenMemories, OnQuests = Books.OpenQuests };
            Log.Info("Creando botones del mapa…");
            _layer = new GauntletLayer("AILegacyMapHud", 300);
            _movie = _layer.LoadMovie("AILegacyMapButtons", _vm);
            // isMouseVisible false, same as the map's own layers. With true the cursor gets stuck and the camera can't rotate.
            _layer.InputRestrictions.SetInputRestrictions(false, InputUsageMask.MouseButtons);
            screen.AddLayer(_layer);
            _screen = screen;
            Log.Info("Botones del mapa añadidos a " + screen.GetType().Name);
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
