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
    /// <summary>Abre/cierra una ventana Gauntlet a pantalla completa con un ViewModel.</summary>
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

    /// <summary>Ventanas de Noticias y Memorias.</summary>
    internal static class Books
    {
        /// <summary>Todo lo hablado con alguien, para repasarlo con calma: lo reciente, lo antiguo (archivado) y las páginas de su libro.</summary>
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
                        if (text.Length == 0) continue;
                        if (t.Role != "player" && text.StartsWith("{")) { try { text = ((string)Newtonsoft.Json.Linq.JObject.Parse(text)["reply"] ?? text).Trim(); } catch { } }
                        bool note = text.StartsWith("[");
                        b.Append(note ? text : (t.Role == "player" ? me : s.Name) + ":\n" + text).Append("\n\n");
                    }
                    return b.Length == 0 ? L.T("Nada todavía.") : b.ToString().Replace("{", "(").Replace("}", ")").TrimEnd();
                }
                var vm = new BookVM { Title = L.F($"Lo hablado con {s.Name}") };
                void Add(string title, string sub, string text) => vm.Entries.Add(new BookEntryVM(title, sub, text, e => vm.Select(e, e.Title, (string)e.Tag)));
                Add(L.T("Esta conversación y las últimas"), L.F($"{mem.Turns.Count} frase(s)"), Lines(mem.Turns));
                if (mem.Archive.Count > 0) Add(L.T("Conversaciones antiguas"), L.F($"{mem.Archive.Count} frase(s) archivadas"), Lines(mem.Archive));
                if (!string.IsNullOrWhiteSpace(mem.Summary)) Add(L.T("Lo que recuerda de ti, en resumen"), "", mem.Summary);
                foreach (var p in mem.Pages) Add(p.Title ?? "Recuerdo", L.T("Página de su libro"), p.Text ?? "");
                vm.Select(vm.Entries[0], vm.Entries[0].Title, (string)vm.Entries[0].Tag);
                Open("AILegacyBook", vm);
            }
            catch (Exception ex) { Core.Log.Error("Repasar lo hablado", ex); }
        }

        /// <summary>Noticias del mundo en una sola columna, como se lee un pregón: título, a quién toca, lo ocurrido, sus efectos, tipo y fecha.</summary>
        public static void OpenNews()
        {
            try
            {
                var vm = new NewsFeedVM { Title = L.T("Sucesos del mundo") };
                bool all = false;
                FillNews(vm, all);
                vm.OnToggle = () => { all = !all; FillNews(vm, all); };
                // Si reinas, desde aquí proclamas
                if (Diplomacy.PlayerCanDeclare) { vm.ActionText = L.T("Hacer una declaración"); vm.HasAction = true; }
                GauntletWindow w = null;
                vm.OnClose = () => w?.Close();
                vm.OnAction = () => { w?.Close(); Core.MainThread.Post(Diplomacy.PlayerStatement); };
                w = GauntletWindow.Open("AILegacyNews", vm, () => w?.Close());
                if (w == null) OpenNewsOld();
            }
            catch (Exception ex) { Core.Log.Error("Ventana de noticias", ex); OpenNewsOld(); }
        }

        /// <summary>¿Es un suceso de los que se cuentan (lo que escribe el mundo, lo que proclaman los reyes, lo sonado)? El resto es el goteo de capturas y escaramuzas.</summary>
        private static bool IsEvent(NewsItem n)
        {
            string t = n.Type ?? "";
            if (t.StartsWith("event_") || t == "statement" || t == "decree" || t == "custom") return true;
            if (t == "rumor") return !string.IsNullOrWhiteSpace(n.TextEs);
            return n.Importance >= 3 && (t == "war" || t == "peace" || t == "ruler" || t == "kingdom" || t == "death");
        }

        /// <summary>La etiqueta de abajo a la izquierda: una palabra. 0 = local, 1 = política, 2 = lo demás.</summary>
        private static string TagOf(NewsItem n, out int kind)
        {
            string t = n.Type ?? "";
            if (t == "statement") { kind = 1; return L.T("Declaración"); }
            if (t == "decree") { kind = 1; return "Sentencia"; }
            if (t == "event_political" || t == "war" || t == "peace" || t == "ruler" || t == "kingdom") { kind = 1; return L.T("Política"); }
            if (n.Importance <= 1) { kind = 0; return L.T("Local"); }
            kind = 2;
            switch (t)
            {
                case "event_military": case "battle": case "siege": case "raid": case "capture": return L.T("Militar");
                case "event_economic": return L.T("Economía");
                case "event_disease": return L.T("Enfermedad");
                case "event_mysterious": return L.T("Misterio");
                case "rumor": return L.T("Rumor");
                case "death": return L.T("Muerte");
                case "prisoner": return L.T("Prisionero");
                default: return "Social";
            }
        }

        private static void FillNews(NewsFeedVM vm, bool all)
        {
            var cfg = S.Get;
            double now = CampaignTime.Now.ToDays;
            string clean(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");
            vm.Cards.Clear();
            foreach (var n in NewsStore.All.Where(n => now - n.Day <= cfg.NewsLifetimeDays && (all || IsEvent(n))).OrderByDescending(n => n.Day).Take(80))
            {
                int d = (int)(now - n.Day);
                string when = d <= 0 ? L.T("Hoy") : d == 1 ? L.T("Hace 1 día") : L.F($"Hace {d} días");
                var who = n.Kingdoms.Select(id => Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == id)?.Name?.ToString()).Where(x => x != null).Distinct().ToList();
                if (who.Count == 0 && !string.IsNullOrEmpty(n.SettlementId)) { var st = TaleWorlds.CampaignSystem.Settlements.Settlement.Find(n.SettlementId); if (st != null) who.Add(st.Name.ToString()); }
                string tag = TagOf(n, out int kind);
                string title = n.Type == "statement" && string.IsNullOrWhiteSpace(n.TitleEs) ? L.T("Declaración de un soberano") : n.DisplayTitle;
                vm.Cards.Add(new NewsCardVM(clean(title), who.Count > 0 ? (n.Type == "statement" ? "" : L.T("Participantes: ")) + clean(string.Join(", ", who)) : "",
                    clean(n.DisplayText), clean(WorldSim.EffectsLineEs(n.Id, n.SettlementId, n.Type)), tag, when, kind));
            }
            if (vm.Cards.Count == 0)
                vm.Cards.Add(new NewsCardVM(L.T("Sin sucesos"), "", all ? L.T("Todavía no ha pasado nada digno de contarse.") : L.T("Todavía no hay ningún suceso de los que dan que hablar. Con «Ver todo» salen también las capturas, batallas y saqueos del día a día."), "", L.T("Local"), L.T("Hoy"), 0));
            vm.ToggleText = all ? L.T("Solo sucesos") : L.T("Ver todo");
        }

        /// <summary>La ventana anterior (lista y detalle): queda de reserva si la nueva no se puede abrir.</summary>
        private static void OpenNewsOld()
        {
            var cfg = S.Get;
            double now = CampaignTime.Now.ToDays;
            var vm = new BookVM { Title = L.T("Noticias de Calradia") };
            var list = NewsStore.All.Where(n => now - n.Day <= cfg.NewsLifetimeDays).OrderByDescending(n => n.Day).Take(80).ToList();
            foreach (var n in list)
            {
                int d = (int)(now - n.Day);
                string when = d <= 0 ? L.T("Hoy") : d == 1 ? L.T("Ayer") : L.F($"Hace {d} días");
                string kind = TypeName(n.Type);
                var title = n.DisplayTitle;
                vm.Entries.Add(new BookEntryVM(title, L.F($"{kind} · {when} · importancia {n.Importance}"), n, e => ShowNews(vm, e)));
            }
            // Si reinas, desde aquí proclamas
            if (Diplomacy.PlayerCanDeclare)
            {
                vm.ActionText = L.T("Hacer una declaración");
                vm.HasAction = true;
                vm.OnAction = e => { GauntletWindow.Current?.Close(); Core.MainThread.Post(Diplomacy.PlayerStatement); };
            }
            if (vm.Entries.Count == 0) vm.Select(null, L.T("Sin noticias"), L.T("Todavía no ha pasado nada digno de contarse. Las guerras, paces, asedios, batallas y rumores aparecerán aquí."));
            else ShowNews(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowNews(BookVM vm, BookEntryVM e)
        {
            var n = (NewsItem)e.Tag;
            int d = (int)(CampaignTime.Now.ToDays - n.Day);
            var b = new StringBuilder();
            b.Append(n.DisplayText).Append("\n\n");
            b.Append(L.F($"Tipo: {TypeName(n.Type)}   ·   Hace {d} día(s)   ·   Importancia {n.Importance} ({ImportanceName(n.Importance)})"));
            var kingdoms = n.Kingdoms.Select(id => Campaign.Current.Kingdoms.FirstOrDefault(k => k.StringId == id)?.Name?.ToString()).Where(x => x != null).ToList();
            if (kingdoms.Count > 0) b.Append(L.T("\nReinos: ")).Append(string.Join(", ", kingdoms));
            var heroes = n.Heroes.Select(id => Hero.Find(id)?.Name?.ToString()).Where(x => x != null).ToList();
            if (heroes.Count > 0) b.Append(L.T("\nImplicados: ")).Append(string.Join(", ", heroes));
            if (n.PlayerInvolved) b.Append(L.T("\n(Estás implicado)"));
            vm.Select(e, n.DisplayTitle, b.ToString().Replace("{", "(").Replace("}", ")"));
        }

        /// <summary>Encargos de la IA (abiertos y recientes) y documentos que llevas.</summary>
        public static void OpenQuests()
        {
            double now = CampaignTime.Now.ToDays;
            var vm = new BookVM { Title = L.T("Encargos") };
            foreach (var q in QuestSystem.Quests.OrderBy(q => q.Active ? 0 : 1).ThenBy(q => q.Active ? q.DueDay : -q.EndDay).Take(80))
            {
                string state = q.Active ? L.F($"Quedan {Math.Max(0, (int)Math.Ceiling(q.DueDay - now))} días")
                             : q.State == "completed" ? "Cumplido" : q.State == "expired" ? L.T("Plazo vencido") : "Fracasado";
                vm.Entries.Add(new BookEntryVM(q.Name, $"{q.GiverName} · {state}", q, e => ShowQuest(vm, e)));
            }
            foreach (var it in Belongings.Items)
                vm.Entries.Add(new BookEntryVM(it.Name, L.F($"Documento · de {it.FromName}"), it, e => ShowQuest(vm, e)));
            if (vm.Entries.Count == 0) vm.Select(null, L.T("Sin encargos"), L.T("Nadie te ha encargado nada todavía. Cuando alguien te pida un favor y lo aceptes, aparecerá aquí con su plazo y su recompensa. También verás aquí los documentos que te entreguen."));
            else ShowQuest(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowQuest(BookVM vm, BookEntryVM e)
        {
            var b = new StringBuilder();
            if (e.Tag is RpItem it)
            {
                b.Append(it.Description).Append(L.F($"\n\nTe lo entregó {it.FromName} hace {(int)(CampaignTime.Now.ToDays - it.Day)} día(s). Puedes enseñarlo o mencionarlo al hablar con cualquiera."));
                vm.Select(e, it.Name, b.ToString().Replace("{", "(").Replace("}", ")"));
                return;
            }
            var q = (AiQuest)e.Tag;
            double now = CampaignTime.Now.ToDays;
            b.Append(q.Description).Append("\n\n");
            b.Append(L.F($"Te lo encargó: {q.GiverName}   ·   hace {(int)(now - q.CreatedDay)} día(s)"));
            if (q.Active) b.Append(L.F($"\nPlazo: quedan {Math.Max(0, (int)Math.Ceiling(q.DueDay - now))} día(s)"));
            else b.Append(L.F($"\nEstado: {(q.State == "completed" ? L.T("cumplido") : q.State == "expired" ? L.T("plazo vencido") : L.T("fracasado"))}"));
            if (q.RewardGold > 0) b.Append(L.F($"\nRecompensa prometida: {q.RewardGold} denarios"));
            if (q.ProgressTarget > 0) b.Append(L.F($"\nProgreso: {q.Progress}/{q.ProgressTarget} {q.ProgressLabel}"));
            if (q.Active) b.Append(L.F($"\n\nPara cerrarlo, habla con {q.GiverName} y cuéntale lo que has hecho: es quien decide si está cumplido."));
            vm.Select(e, q.Name, b.ToString().Replace("{", "(").Replace("}", ")"));
        }

        public static void OpenMemories()
        {
            var vm = new BookVM { Title = L.T("Libro de memorias"), ActionText = L.T("Hablar"), HasAction = false };
            var list = MemoryStore.AllHeroes
                .Where(m => m.ConversationCount > 0 || m.Turns.Count > 0 || !string.IsNullOrEmpty(m.Summary))
                .Select(m => (m, h: Hero.Find(m.HeroId))).Where(x => x.h != null)
                .OrderByDescending(x => x.m.LastConversationDay).Take(200).ToList();
            foreach (var x in list)
            {
                int rel = (int)x.h.GetRelationWithPlayer();
                string sub = L.F($"{(x.h.IsDead ? L.T("Fallecido · ") : "")}Relación {rel:+0;-0;0} · {x.m.ConversationCount} charla(s)");
                vm.Entries.Add(new BookEntryVM(x.h.Name.ToString(), sub, x.h, e => ShowHero(vm, e)));
            }
            // Documentos y objetos de rol que llevas encima
            foreach (var it in Belongings.Items)
                vm.Entries.Add(new BookEntryVM(it.Name, L.F($"Documento · de {it.FromName}"), it, e => ShowQuest(vm, e)));
            vm.OnAction = e =>
            {
                var h = e.Tag as Hero;
                if (h == null || !h.IsAlive) return;
                GauntletWindow.Current?.Close();
                /* las cartas se envían desde el distrito de tabernas */
            };
            if (vm.Entries.Count == 0) vm.Select(null, L.T("Vacío"), L.T("Aún no has hablado con nadie. Cada persona con la que hables aparecerá aquí con lo que recuerda de ti."));
            else ShowHero(vm, vm.Entries[0]);
            Open("AILegacyBook", vm);
        }

        private static void ShowHero(BookVM vm, BookEntryVM e)
        {
            var h = (Hero)e.Tag;
            var m = MemoryStore.Find(h.StringId);
            var b = new StringBuilder();
            if (h.Clan != null) b.Append($"Clan {h.Clan.Name}").Append(h.MapFaction is Kingdom k ? $" · {k.Name}" : "").Append("\n");
            b.Append(L.F($"Relación: {(int)h.GetRelationWithPlayer():+0;-0;0}"));
            if (m != null)
            {
                if (!string.IsNullOrWhiteSpace(m.Mood)) b.Append(L.F($"   ·   Ánimo: {m.Mood}"));
                b.Append(L.F($"\nTe conoce como: {m.KnownPlayerName ?? (Prompt.ContextBuilder.WhyKnowsPlayer(Speaker.FromHero(h)) != null ? Hero.MainHero.Name.ToString() : L.T("(desconocido)"))}"));
                if (m.Profile != null && !m.Profile.IsEmpty)
                {
                    b.Append(L.T("\n\nPERSONALIDAD\n")).Append(m.Profile.Personality);
                    if (!string.IsNullOrWhiteSpace(m.Profile.Backstory)) b.Append(L.T("\n\nPASADO\n")).Append(m.Profile.Backstory);
                    if (!string.IsNullOrWhiteSpace(m.Profile.Goals)) b.Append(L.T("\n\nLO QUE BUSCA\n")).Append(m.Profile.Goals);
                }
                if (!string.IsNullOrWhiteSpace(m.Summary)) b.Append(L.T("\n\nLO QUE RECUERDA DE VOSOTROS\n")).Append(m.Summary);
                if (m.Pages != null && m.Pages.Count > 0)
                {
                    b.Append(L.T("\n\nPÁGINAS DE SU MEMORIA"));
                    foreach (var pg in m.Pages.Skip(Math.Max(0, m.Pages.Count - 8)))
                        b.Append(L.F($"\n\n— {pg.Title} (hace {(int)(CampaignTime.Now.ToDays - pg.Day)} días) —\n")).Append(pg.Text);
                }
                if (!string.IsNullOrEmpty(m.Archetype)) b.Append(L.T("\n\nMOLDE\n")).Append(m.Archetype);
                if (m.Notes.Count > 0) b.Append(L.T("\n\nRECUERDOS\n")).Append(string.Join("\n", m.Notes.Select(n => "· " + n)));
                var lastTurns = m.Turns.Skip(Math.Max(0, m.Turns.Count - 6)).ToList();
                if (lastTurns.Count > 0)
                {
                    b.Append(L.T("\n\nÚLTIMAS PALABRAS\n"));
                    foreach (var t in lastTurns) b.Append(t.Role == "player" ? L.T("Tú: ") : h.Name + ": ").Append(t.Text).Append("\n");
                }
            }
            vm.HasAction = false; // las cartas se envían desde el distrito de tabernas
            vm.ActionText = L.T("Escribir carta");
            vm.Select(e, h.Name.ToString(), b.ToString().Replace("{", "(").Replace("}", ")"));
        }

        private static void Open(string movie, BookVM vm)
        {
            GauntletWindow w = null;
            vm.OnClose = () => w?.Close();
            w = GauntletWindow.Open(movie, vm, () => w?.Close());
            if (w == null)
                InformationManager.ShowInquiry(new InquiryData(vm.Title, vm.DetailText, true, false, L.T("Cerrar"), "", null, null), true);
        }

        private static string TypeName(string t)
        {
            switch (t)
            {
                case "war": return L.T("Guerra");
                case "peace": return L.T("Paz");
                case "capture": return L.T("Conquista");
                case "fief": return L.T("Feudo");
                case "battle": return L.T("Batalla");
                case "raid": return L.T("Saqueo");
                case "siege": return L.T("Asedio");
                case "death": return L.T("Muerte");
                case "ruler": return L.T("Nuevo soberano");
                case "kingdom": return L.T("Caída de un reino");
                case "clan": return L.T("Clanes");
                case "prisoner": return L.T("Prisionero");
                case "rumor": return L.T("Rumor");
                case "statement": return L.T("Declaración de un soberano");
                case "event_political": return L.T("Política");
                case "event_military": return L.T("Guerra");
                case "event_economic": return L.T("Economía");
                case "event_social": return L.T("Sociedad");
                case "event_mysterious": return L.T("Misterio");
                case "event_news": return L.T("Suceso");
                case "event_disease": return L.T("Enfermedad");
                case "custom": return L.T("Evento");
                default: return string.IsNullOrEmpty(t) ? "Noticia" : char.ToUpper(t[0]) + t.Substring(1);
            }
        }

        private static string ImportanceName(int i) => i >= 4 ? L.T("todo el mundo") : i == 3 ? L.T("todo el reino") : i == 2 ? L.T("la corte") : L.T("el lugar");
    }

    /// <summary>Botones fijos en el mapa (capa transparente: solo los botones reciben clics).</summary>
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
            // isMouseVisible: false, como las capas del propio mapa: con true el cursor queda fijo y no se puede girar la cámara
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
