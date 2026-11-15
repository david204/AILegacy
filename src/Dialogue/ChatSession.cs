using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Config;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.UI;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.Library;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// A free-form conversation. Uses our own chat window (ChatWindow), and falls back to the game's
    /// text inquiry popups if it can't be opened.
    /// </summary>
    internal class ChatSession
    {
        public static ChatSession Active { get; private set; }

        private readonly Speaker _s;
        private static NewsItem _lastRumor;
        private static string _lastRumorWho;
        private HeroMemory Mem => _s.Memory;
        private int _relationChangedThisTalk;
        /// <summary>Total relation movement this talk, ups and downs added together. The cap applies to this, not to the net change.</summary>
        private int _relationMovedThisTalk;
        private double _gainCarry;
        /// <summary>Trust lost this talk. If any, the end-of-talk familiarity bonus is skipped.</summary>
        private int _trustLostThisTalk;
        private bool _waiting;
        private bool _closed;
        private bool _npcEnded;
        private int _lines;
        /// <summary>Replies received. A talk with none doesn't count as a conversation.</summary>
        private int _answered;
        /// <summary>This turn's reply was already re-requested once for repeating itself.</summary>
        private bool _retriedRepeat;
        // A deed spoken as done with no action for it: the nudge for the next turn, and the kinds already nudged in this talk
        private string _deedNudge;
        private readonly HashSet<string> _deedsNudged = new HashSet<string>();
        private ChatWindow _win;
        private readonly List<string> _transcript = new List<string>(); // fallback popup mode only

        private ChatSession(Speaker s) { _s = s; }

        public static bool IsBusy => Active != null && !Active._closed;

        /// <summary>Word the player carries by hand for this person is given over as the talk begins.</summary>
        private static void HandOverCarried(Speaker s)
        {
            try
            {
                string shown = World.InfoExchange.DeliverCarried(s);
                if (shown.Length > 0) Notify(shown, Colors.Green);
            }
            catch (Exception ex) { Log.Error("Recados en mano", ex); }
        }

        public static void Start(Speaker s)
        {
            if (s == null) return;
            if (IsBusy) return;

            if (!LegacyConfig.Current.Providers.Any(p => p.IsUsable))
            {
                Notify("AI Legacy: " + (LegacyConfig.LastLoadError ?? L.T("AIL_bb646884", "No hay ninguna clave: ponla en Opciones, Mod Options, AI Legacy, 0. Claves de IA.")), Colors.Red);
                return;
            }

            var session = new ChatSession(s);
            Active = session;
            Log.Info($"Conversación iniciada con {s.Name} ({s.Key})");
            HandOverCarried(s);

            var vm = new ChatVM(s.Name, session.OnPlayerSaid, () => session.Close(session._npcEnded));
            session._win = ChatWindow.Open(vm);
            if (session._win != null)
            {
                session.RefreshSubtitle();
                vm.WaitingText = L.F("AIL_e6c89e51", $"{s.Name} está pensando...");
                vm.AddSystem(session.Mem.ConversationCount > 0
                    ? L.F("AIL_aad18359", $"{s.Name} te reconoce. Ya habéis hablado {session.Mem.ConversationCount} {(session.Mem.ConversationCount == 1 ? L.T("AIL_8a30751a", "vez") : L.T("AIL_592eb989", "veces"))}.  (Escribe /ayuda para ver los comandos)")
                    : L.F("AIL_4b77b3ae", $"{s.Name} espera a que hables.  (Escribe /ayuda para ver los comandos)"));
            }
            else
            {
                session.ShowLegacyInput();
            }
        }

        public static void TickUI()
        {
            ChatWindow.Current?.Tick();
            try { Active?.PaintLive(); }
            catch (Exception ex) { Log.Error("Texto de la respuesta mientras llega", ex); }
        }

        // ====================================================== reply shown while it is being written
        // The HTTP thread only leaves the text in LiveText. Everything on screen is done here, on the game tick.

        private class LiveText { public volatile string Text = ""; }

        private LiveText _live;
        /// <summary>Characters of the live text already on screen. It trails what has arrived so the line grows at a readable pace.</summary>
        private int _liveShown;
        private DateTime _livePaintAt;
        /// <summary>Some of this turn's reply reached the screen before it was complete.</summary>
        private bool _livePainted;
        /// <summary>The "reply ready" sound already played for this turn, when the text started.</summary>
        private bool _liveSounded;
        private ChatLineVM _liveLine;
        private TaleWorlds.MountAndBlade.Agent _liveAgent;

        private const int LivePaintMs = 100;

        private void PaintLive()
        {
            var live = _live;
            if (live == null || !_waiting || _closed) return;
            var now = DateTime.UtcNow;
            if ((now - _livePaintAt).TotalMilliseconds < LivePaintMs) return;
            _livePaintAt = now;

            string all = live.Text ?? "";
            if (all.Length < _liveShown) _liveShown = 0;        // that attempt was thrown away and another one started
            if (all.Length == 0)
            {
                if (_livePainted) ShowLive("");
                return;
            }
            int behind = all.Length - _liveShown;
            if (behind > 0)
            {
                _liveShown += Math.Min(behind, Math.Max(6, behind / 3));
                if (_liveShown < all.Length && char.IsHighSurrogate(all[_liveShown - 1])) _liveShown++;
            }
            ShowLive(all.Substring(0, _liveShown));
        }

        /// <summary>Puts the unfinished line where the player is looking: the dialogue box, the bubble over the head, or our window.</summary>
        private void ShowLive(string npcText)
        {
            bool painted = false;
            if (_dialogMode)
            {
                var cm = Campaign.Current?.ConversationManager;
                bool inBox = cm != null && cm.IsConversationInProgress && cm.CurOptions != null && cm.CurOptions.Any(o => o.Id == "ailegacy_d_view");
                if (inBox)
                {
                    string box = npcText.Length > 0 ? Compose(npcText) : L.F("AIL_30214b10", $"*{_s.Name} todavía está pensando…*");
                    painted = ConversationMixin.SetDialogText(box.Replace("{newline}", "\n"));
                }
                try
                {
                    if (npcText.Length > 0 && cm != null && TaleWorlds.MountAndBlade.Mission.Current != null && Bubbles.Available
                        && cm.OneToOneConversationAgent is TaleWorlds.MountAndBlade.Agent agent && agent.IsActive())
                    {
                        Bubbles.ShowLive(agent, npcText, 6f);
                        _liveAgent = agent;
                        painted = true;
                    }
                }
                catch { }
            }
            else if (UseWindow)
            {
                if (_liveLine == null) _liveLine = _win.VM.AddLiveLine(_s.Name, ChatVM.NpcColor);
                string line = npcText.Length > 0 ? npcText : "…";
                if (_liveLine.Text != line) { _liveLine.Text = line; _win.VM.ScrollVersion++; }
                painted = true;
            }
            if (!painted || npcText.Length == 0) return;
            _livePainted = true;
            if (_liveSounded) return;
            _liveSounded = true;
            try { if (S.Get.ReplyReadySound) TaleWorlds.Engine.SoundEvent.PlaySound2D("event:/ui/notification/quest_update"); } catch { }
        }

        /// <summary>The reply stopped being a live text: it is complete, it failed, or the turn is being asked again.</summary>
        private void EndLive(bool keepLine)
        {
            _live = null;
            _liveShown = 0;
            if (!keepLine && _liveLine != null)
            {
                try { _win?.VM.RemoveLine(_liveLine); } catch { }
                _liveLine = null;
            }
        }

        // ====================================================== game dialogue mode
        // The talk lives in the native dialogue box: type, "(wait)", then "See their reply".

        private bool _dialogMode;
        private string _lastPlayer = "", _lastNpc = "";
        private readonly List<string> _dialogNotes = new List<string>();
        public bool HasUnreadReply { get; private set; }
        public bool Seeded { get; private set; }

        /// <summary>The NPC speaks first (initiative): their line is already written and is the first thing the box shows.</summary>
        public void Seed(string npcText)
        {
            _lastPlayer = "";
            _lastNpc = npcText ?? "";
            HasUnreadReply = true;
            Seeded = true;
            _lines++;
            _answered++;
        }
        public bool IsWaiting => _waiting;
        public bool Ended => _npcEnded;
        public Speaker Who => _s;

        /// <summary>Session tied to the current game conversation. Closes by itself when the conversation ends.</summary>
        public static ChatSession StartDialog(Speaker s)
        {
            if (s == null) return null;
            if (IsBusy && Active._dialogMode && Active._s.Key == s.Key) return Active;
            if (IsBusy) Active.Close(false);

            if (!LegacyConfig.Current.Providers.Any(p => p.IsUsable))
            {
                Notify("AI Legacy: " + (LegacyConfig.LastLoadError ?? L.T("AIL_bb646884", "No hay ninguna clave: ponla en Opciones, Mod Options, AI Legacy, 0. Claves de IA.")), Colors.Red);
                return null;
            }
            var session = new ChatSession(s) { _dialogMode = true };
            Active = session;
            Log.Info($"Conversación (diálogo) iniciada con {s.Name} ({s.Key})");
            HandOverCarried(s);
            try { Campaign.Current.ConversationManager.ConversationEndOneShot += () => session.Close(false); }
            catch (Exception ex) { Log.Error("No pude engancharme al final de la conversación", ex); }
            return session;
        }

        /// <summary>Opens the text box for the player's message (dialogue mode).</summary>
        public void AskInput()
        {
            if (_closed || _waiting || _npcEnded) return;
            string last = Mem.LastConversationDay < 0 ? L.T("AIL_e5c9b3a6", "Nunca")
                : (int)(CampaignTime.Now.ToDays - Mem.LastConversationDay) <= 0 ? L.T("AIL_564e2e23", "Hoy")
                : L.F("AIL_b2b050b8", $"Hace {(int)(CampaignTime.Now.ToDays - Mem.LastConversationDay)} días");
            var lastNpc = Mem.Turns.LastOrDefault(t => t.Role == "npc")?.Text;
            string body = string.IsNullOrWhiteSpace(lastNpc)
                ? L.F("AIL_1e733cef", $"Última vez que hablasteis: {last}.")
                : L.F("AIL_e102e90e", $"Último mensaje de {_s.Name}:\n{(lastNpc.Length > 700 ? lastNpc.Substring(0, 700) + "…" : lastNpc)}");
            InformationManager.ShowTextInquiry(new TextInquiryData(
                L.F("AIL_4f47d62d", $"Tu mensaje para {_s.Name}"), body + L.T("AIL_904fdecc", "\n\n(/ayuda para ver los comandos)"),
                true, true, L.T("AIL_e5c9b3a3", "Confirmar"), L.T("AIL_e5c9b3a4", "Cancelar"),
                t => OnPlayerSaid(t), null, false,
                t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_e07caced", "Escribe algo"))), true);
        }

        /// <summary>Text for the NPC's dialogue box right now. Marks the reply as read.</summary>
        public string ConsumeDialogText()
        {
            if (_waiting)
            {
                // Clicked "See their reply" while it is still being written: keep showing what there is
                var live = _live;
                string part = live?.Text ?? "";
                if (_liveShown > 0 && _liveShown <= part.Length) return Compose(part.Substring(0, _liveShown));
                return L.F("AIL_30214b10", $"*{_s.Name} todavía está pensando…*");
            }
            if (!HasUnreadReply && _lastNpc.Length == 0 && _dialogNotes.Count == 0) return L.T("AIL_07e9f3c5", "(Envía tu mensaje y espera su respuesta)");
            HasUnreadReply = false;
            var sb = new StringBuilder();
            // With "only their words" the box holds nothing but what the character says, so a voice mod that reads
            // the box doesn't also read the player's line and the name in front
            bool bare = S.Get.DialogOnlyTheirWords;
            if (!bare && _lastPlayer.Length > 0) sb.Append(L.T("AIL_63ea9af4", "Tú: ")).Append(_lastPlayer).Append("{newline}{newline}");
            if (_lastNpc.Length > 0) sb.Append(bare || _lastNpc.StartsWith("(") ? "" : _s.Name + ": ").Append(_lastNpc);
            foreach (var n in _dialogNotes) sb.Append("{newline}» ").Append(n);
            _dialogNotes.Clear();
            string stats = _lastNpc.Length > 0 ? StatsLine() : "";
            if (stats.Length > 0) sb.Append("{newline}").Append(stats);
            return Sanitize(sb.ToString());
        }

        /// <summary>The last exchange as the dialogue box shows it: the player's line and the character's, with the game's line breaks.</summary>
        private string Compose(string npcText)
        {
            var sb = new StringBuilder();
            bool bare = S.Get.DialogOnlyTheirWords;
            if (!bare && _lastPlayer.Length > 0) sb.Append(L.T("AIL_63ea9af4", "Tú: ")).Append(_lastPlayer).Append("{newline}{newline}");
            if (npcText.Length > 0) sb.Append(bare || npcText.StartsWith("(") ? "" : _s.Name + ": ").Append(npcText);
            return Sanitize(sb.ToString());
        }

        /// <summary>
        /// The reply has arrived: if the dialogue box is sitting on "See their reply", click it for the player.
        /// If the player is somewhere else (another dialogue branch, an open window), leave it alone.
        /// </summary>
        private void AutoShow()
        {
            try
            {
                if (_closed || !_dialogMode || !HasUnreadReply || Active != this) return;
                var cm = Campaign.Current?.ConversationManager;
                if (cm == null || !cm.IsConversationInProgress || cm.CurOptions == null) return;
                if (!cm.CurOptions.Any(o => o.Id == "ailegacy_d_view")) return;
                cm.DoOption("ailegacy_d_view");
            }
            catch (Exception ex) { Log.Error("Enseñar la respuesta sola", ex); }
        }

        /// <summary>One-line status: relation, trust, tension and romance.</summary>
        private string StatsLine()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.ShowDialogStats || !_s.IsHero) return "";
                if (UI.ConversationMixin.ShowingStats) return "";      // already shown in the bars next to the name
                var parts = new List<string> { L.F("AIL_f3138943", $"Relación: {(int)_s.Hero.GetRelationWithPlayer():+0;-0;0}") };
                if (cfg.EnableTrust) { Bonds.EnsureTrust(_s); parts.Add(L.F("AIL_2cb6f858", $"Confianza: {Mem.Trust}")); }
                if (cfg.EnableTension && Bonds.Tension(Mem) > 0) parts.Add(L.F("AIL_55654690", $"Tensión: {Bonds.Tension(Mem)}"));
                if (Bonds.RomancePossible(_s) && Mem.Romance > 0) parts.Add(L.F("AIL_bbb8296b", $"   ·   Romance: {Mem.Romance}").Trim(' ', '·'));
                if (Mem.LiesSuspected > 0) parts.Add("⚠ " + Mem.LiesSuspected);
                return "[ " + string.Join("  ·  ", parts) + " ]";
            }
            catch { return ""; }
        }

        public string WaitText => _waiting || _lastNpc.Length == 0 ? L.T("AIL_07e9f3c5", "(Envía tu mensaje y espera su respuesta)") : Sanitize(_lastNpc);

        // Braces are text variables to the game. Square brackets are animation tags: the dialogue box drops everything
        // from a "[" to the next "]", and to the end of the line when it is never closed, so both become parentheses.
        private static string Sanitize(string s) =>
            (s ?? "").Replace("{newline}", "\u0001").Replace("{", "(").Replace("}", ")").Replace("[", "(").Replace("]", ")")
                     .Replace("\u0001", "{newline}").Replace("\r", "").Replace("\n", "{newline}");

        // =============================================================== UI

        private bool UseWindow => _win != null;

        private void RefreshSubtitle()
        {
            if (!UseWindow) return;
            var parts = new List<string>();
            if (_s.IsHero)
            {
                int rel = (int)_s.Hero.GetRelationWithPlayer();
                parts.Add(L.F("AIL_f3138943", $"Relación: {rel:+0;-0;0}"));
                if (_s.Hero.Clan != null) parts.Add(_s.Hero.Clan.Name.ToString());
            }
            else if (_s.Settlement != null) parts.Add(_s.Settlement.Name.ToString());
            if (!string.IsNullOrWhiteSpace(Mem.Mood)) parts.Add(L.T("AIL_5e479046", "Ánimo: ") + Mem.Mood);
            _win.VM.Subtitle = string.Join("   ·   ", parts);
            _win.VM.Title = _s.Name;
        }

        private void UiPlayer(string text)
        {
            if (_dialogMode) { _lastPlayer = text; _lastNpc = ""; return; }
            if (UseWindow) _win.VM.AddLine(L.T("AIL_4c6b494e", "Tú"), text, ChatVM.PlayerColor);
            else _transcript.Add(L.F("AIL_016ea49e", $"Tú: {text}"));
        }

        private void UiNpc(string text)
        {
            if (_dialogMode)
            {
                _lastNpc = text;
                HasUnreadReply = true;
                bool auto = S.Get.AutoShowReply;
                // If the text was already growing on screen, the sound played when it started
                bool sound = S.Get.ReplyReadySound && !_liveSounded;
                try
                {
                    if (auto)
                    {
                        // The reply will show in the box by itself, so just play the sound (the floating message overlapped the text)
                        if (sound) TaleWorlds.Engine.SoundEvent.PlaySound2D("event:/ui/notification/quest_update");
                    }
                    else
                    {
                        var note = new InformationMessage(L.F("AIL_c1353aff", $"{_s.Name} te contesta."), Colors.Yellow);
                        if (sound) note.SoundEventPath = "event:/ui/notification/quest_update";
                        InformationManager.DisplayMessage(note);
                        // The box was showing the line as it grew: leave it on the finished one until the player clicks
                        if (_livePainted) PaintBox(Compose(text));
                    }
                    if (_liveAgent != null && _liveAgent.IsActive()) Bubbles.Show(_liveAgent, text, Math.Max(3f, Math.Min(8f, 2f + text.Length * 0.04f)));
                }
                catch { }
                _liveAgent = null;
                if (auto) MainThread.Post(AutoShow);
                return;
            }
            if (UseWindow)
            {
                if (_liveLine != null) { _liveLine.Text = text; _liveLine = null; _win.VM.ScrollVersion++; }
                else _win.VM.AddLine(_s.Name, text, ChatVM.NpcColor);
            }
            else _transcript.Add($"{_s.Name}: {text}");
        }

        /// <summary>Writes straight into the game's dialogue box, if it is sitting on our waiting line.</summary>
        private void PaintBox(string composed)
        {
            try
            {
                var cm = Campaign.Current?.ConversationManager;
                if (cm == null || !cm.IsConversationInProgress || cm.CurOptions == null || !cm.CurOptions.Any(o => o.Id == "ailegacy_d_view")) return;
                ConversationMixin.SetDialogText(composed.Replace("{newline}", "\n"));
            }
            catch { }
        }

        private void UiSystem(string text, bool error = false)
        {
            if (_dialogMode)
            {
                if (error) { _lastNpc = "(" + text + ")"; HasUnreadReply = true; Notify(text.Split('\n')[0], Colors.Red); if (_livePainted) PaintBox(Compose(_lastNpc)); }
                else { _dialogNotes.Add(text); HasUnreadReply = true; Notify(L.T("AIL_78f99860", "AI Legacy: comando aplicado. Pulsa «Ver su respuesta»."), Colors.Gray); }
                return;
            }
            if (UseWindow) { if (error) _win.VM.AddError(text); else _win.VM.AddSystem(text); }
            else InformationManager.ShowInquiry(new InquiryData(error ? "AI Legacy" : _s.Name, text, true, false, L.T("AIL_1df7d173", "Volver"), "", () => MainThread.Post(ShowLegacyInput), null), true);
        }

        private void UiWaiting(bool waiting)
        {
            if (_dialogMode) return;
            if (UseWindow) _win.VM.IsWaiting = waiting;
            else if (waiting) Notify(L.F("AIL_e6c89e51", $"{_s.Name} está pensando..."), Colors.Gray);
        }

        private void UiReady()
        {
            if (_dialogMode) return;
            if (UseWindow) RefreshSubtitle();
            else ShowLegacyInput();
        }

        // -------- fallback mode (game text popups)

        private void ShowLegacyInput()
        {
            if (_closed) return;
            var body = new StringBuilder();
            if (_transcript.Count == 0)
                body.Append(Mem.ConversationCount > 0 ? L.F("AIL_96bb78de", $"{_s.Name} te reconoce.") : L.F("AIL_2d66ff9e", $"{_s.Name} espera a que hables."));
            else
                foreach (var line in _transcript.Skip(Math.Max(0, _transcript.Count - 2)))
                    body.AppendLine(line.Length > 600 ? line.Substring(0, 600) + "…" : line).AppendLine();

            InformationManager.ShowTextInquiry(new TextInquiryData(
                _s.Name, body.ToString().TrimEnd(), true, true, L.T("AIL_aa23f794", "Decir"), L.T("AIL_e5c9b3a5", "Terminar"),
                OnPlayerSaid, () => Close(_npcEnded), false,
                t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_e07caced", "Escribe algo"))), true);
        }

        // ============================================================ logic

        private void OnPlayerSaid(string text)
        {
            if (_closed || _waiting) return;
            text = (text ?? "").Trim();
            if (text.Length == 0) { if (!UseWindow && !_dialogMode) MainThread.Post(ShowLegacyInput); return; }
            if (_npcEnded) return;
            if (text.StartsWith("/")) { HandleCommand(text); return; }

            Mem.Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays });
            if (_s.IsHero) AILegacy.Api.AILegacyApi.RaisePlayerSaid(_s.Hero, text);
            UiPlayer(text);
            Ambient.Overhear(_s, text);
            _lines++;

            _retriedRepeat = false;
            _livePainted = false;
            _liveSounded = false;
            _liveAgent = null;
            bool deep = false;
            try { deep = RagIndex.WantsQuery(_s); } catch { }
            if (!deep) { BuildAndAsk(text); return; }

            // Memory recall and action matching by meaning need the embedding of what was just said first
            _waiting = true;
            UiWaiting(true);
            int session = World.WorldSim.Session;
            try { NpcActions.EnsureActionVectors(); } catch { }
            RagIndex.PrepareQuery(_s, text).ContinueWith(_ => MainThread.Post(() =>
            {
                if (session != World.WorldSim.Session) { _waiting = false; _closed = true; return; }
                BuildAndAsk(text);
            }));
        }

        private void BuildAndAsk(string text)
        {
            List<ChatMessage> messages;
            try
            {
                messages = PromptBuilder.BuildDialogue(_s, S.Get.MaxRelationChangePerConversation - _relationMovedThisTalk);
                // once, on the turn after the words: do it with the action or say plainly why not
                if (_deedNudge != null && messages.Count > 0)
                {
                    messages[messages.Count - 1].Content += "\n\n" + _deedNudge;
                    _deedNudge = null;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error construyendo el prompt", ex);
                _waiting = false;
                UiWaiting(false);
                RemoveLastPlayerTurn(text);
                UiSystem(L.T("AIL_ccda8e3e", "Error interno construyendo el contexto: ") + ex.Message, true);
                return;
            }
            Ask(messages, text);
        }

        // The exact prompt and raw answer of the last reply shown, for "flag a mistake"
        private List<ChatMessage> _pendingAsked, _lastAsked;
        private string _lastRaw;

        public bool CanReport => S.Get.EnablePlayerLessons && _lastAsked != null && !_waiting && !_closed;

        /// <summary>"Talk (with a picture of the scene)": every reply also gets a picture of that moment.</summary>
        public bool WithPictures;
        private string _picture, _pictureText;
        public bool HasPicture => _picture != null && System.IO.File.Exists(_picture);

        public void ShowPicture()
        {
            if (!HasPicture) return;
            UI.Books.OpenPicture(_picture, L.F("AIL_e7b1c7ce", $"La escena con {_s.Name}"), _pictureText);
        }

        private void AskPicture(string playerText, string npcText)
        {
            try
            {
                if (!WithPictures || !World.SceneImages.Enabled) return;
                string player = Hero.MainHero?.Name?.ToString() ?? "";
                var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                string where = (st != null ? st.Name.ToString() : "the open country") + ", " + (CampaignTime.Now.IsNightTime ? "at night" : "by day")
                               + ", " + CampaignTime.Now.GetSeasonOfYear.ToString().ToLowerInvariant();
                string exchange = $"{player}: {playerText}\n{_s.Name}: {npcText}";
                string shown = $"{L.T("AIL_63ea9af4", "Tú: ")}{playerText}\n\n{_s.Name}: {npcText}";
                var self = this;
                World.SceneImages.RequestMoment(Mem, where, exchange, path =>
                {
                    self._picture = path; self._pictureText = shown;
                    if (self.UseWindow && !self._closed) self.ShowPicture();
                    else Notify(L.T("AIL_e7b1c7cf", "La imagen de la escena está lista: elige «Ver la escena»."), Colors.Gray);
                });
            }
            catch (Exception ex) { Log.Error("Imagen de la charla", ex); }
        }

        /// <summary>The player says what the last reply got wrong; the review runs and may propose a lesson.</summary>
        public void ReportMistake() => ReportMistake(null);

        private void ReportMistake(string complaint)
        {
            if (!S.Get.EnablePlayerLessons) return;
            if (_lastAsked == null) { UiSystem(L.T("AIL_e7b1c7c2", "Todavía no hay ninguna respuesta que revisar en esta charla.")); return; }
            var asked = _lastAsked; string raw = _lastRaw, name = _s.Name;
            // The answer comes later, maybe after the dialogue moved on: a window of its own, not the dialogue box
            void Show(string msg, bool error)
            {
                if (UseWindow && !_closed) { if (error) _win.VM.AddError(msg); else _win.VM.AddSystem(msg); return; }
                InformationManager.ShowInquiry(new InquiryData("AI Legacy", msg, true, false, L.T("AIL_1df7d173", "Volver"), "", null, null), true);
            }
            void Run(string c)
            {
                if (string.IsNullOrWhiteSpace(c)) return;
                Notify(L.T("AIL_e7b1c7b9", "Revisando la última respuesta…"), Colors.Gray);
                Lessons.Review(asked, raw, c.Trim(), name, Show);
            }
            if (!string.IsNullOrWhiteSpace(complaint)) { Run(complaint); return; }
            InformationManager.ShowTextInquiry(new TextInquiryData(
                L.T("AIL_e7b1c7b7", "Señalar un error de la IA"),
                L.T("AIL_e7b1c7b8", "Di qué hizo mal en su última respuesta. Se revisa esa respuesta y, si procede, se propone una lección para que no vuelva a pasar. Nada se guarda sin que lo aceptes."),
                true, true, L.T("AIL_e5c9b3a3", "Confirmar"), L.T("AIL_e5c9b3a4", "Cancelar"), t => Run(t), null, false,
                t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_e07caced", "Escribe algo"))), true);
        }

        /// <summary>The character speaks first in a conversation the game has already opened (bandits blocking the road).</summary>
        public void AskOpening(string instruction)
        {
            if (_closed || _waiting) return;
            try
            {
                var messages = PromptBuilder.BuildDialogue(_s, S.Get.MaxRelationChangePerConversation - _relationMovedThisTalk, instruction);
                Ask(messages, "");
            }
            catch (Exception ex) { Log.Error("Primera frase del personaje", ex); }
        }

        private void Ask(List<ChatMessage> messages, string text)
        {
            _pendingAsked = messages;
            _waiting = true;
            UiWaiting(true);

            // The first reply also carries the character profile (12 fields) and needs more room to avoid being cut off
            bool firstTime = Mem.Profile == null || Mem.Profile.IsEmpty;
            int? tokens = firstTime ? Math.Max(S.Get.MaxTokens, 1800) : (int?)null;
            int session = World.WorldSim.Session;
            // The line shows while it is written. The callback runs on the HTTP thread and only stores the text;
            // if another save has been loaded since, it answers false and the request is dropped.
            Func<string, bool> onPartial = null;
            EndLive(keepLine: true);
            if (S.Get.StreamReplies)
            {
                var live = new LiveText();
                _live = live;
                onPartial = t => { if (session != World.WorldSim.Session) return false; live.Text = t ?? ""; return true; };
                if (_livePainted) ShowLive("");      // asked again (a repeated line): back to "thinking" until the new text starts
            }
            LlmClient.CompleteStreamedAsync(messages, wantJson: true, onPartial: onPartial, task: "dialogue", maxTokensOverride: tokens, preferredProvider: _s.IsHero ? S.Get.ProviderHeroes : S.Get.ProviderResidents).ContinueWith(task =>
            {
                var res = task.IsFaulted ? new LlmResult { Ok = false, Error = task.Exception?.GetBaseException().Message } : task.Result;
                MainThread.Post(() =>
                {
                    // Another save was loaded in the meantime: this reply belongs to it, so don't apply or store it
                    if (session != World.WorldSim.Session) { _waiting = false; _closed = true; Log.Info("Respuesta de una charla de otra partida, descartada"); return; }
                    OnNpcAnswered(res, text);
                });
            });
        }

        private void RemoveLastPlayerTurn(string text)
        {
            if (Mem.Turns.Count > 0 && Mem.Turns[Mem.Turns.Count - 1].Role == "player")
                Mem.Turns.RemoveAt(Mem.Turns.Count - 1);
            if (!UseWindow && _transcript.Count > 0) _transcript.RemoveAt(_transcript.Count - 1);
            if (UseWindow && string.IsNullOrEmpty(_win.VM.InputText)) _win.VM.InputText = text; // so Enter retries
        }

        private void OnNpcAnswered(LlmResult res, string playerText)
        {
            _waiting = false;
            UiWaiting(false);
            EndLive(keepLine: res.Ok && !_closed);
            if (_closed) { OnLateAnswer(res); return; }

            if (!res.Ok)
            {
                // an opening line has no player turn of its own to take back
                if (!string.IsNullOrEmpty(playerText)) RemoveLastPlayerTurn(playerText);
                Log.Error("Sin respuesta: " + res.Error);
                UiSystem(L.T("AIL_4073a6cc", "No ha llegado respuesta de ninguna IA. Pulsa Decir para reintentar.\n") + res.Error, true);
                return;
            }

            var reply = NpcReply.Parse(res.Text);
            Mem.LastAiResponse = res.Text != null && res.Text.Length > 4000 ? res.Text.Substring(0, 4000) : res.Text;
            if (_s.IsHero) Mem.Snapshot = ContextBuilder.Snapshot(_s.Hero);
            if (string.IsNullOrWhiteSpace(reply.Reply)) reply.Reply = L.T("AIL_da6122fe", "*te mira en silencio*");

            if (reply.Profile != null && Mem.Profile != null && !Mem.Profile.IsEmpty && Mem.Profile.IsIncomplete)
            {
                int filled = Mem.Profile.FillMissingFrom(reply.Profile);
                if (filled > 0) { Mem.ProfileRevision++; Log.Info($"Ficha de {_s.Name} completada: {filled} parte(s) que faltaban"); }
            }
            if (reply.Profile != null && (Mem.Profile == null || Mem.Profile.IsEmpty))
            {
                Mem.Profile = reply.Profile;
                Log.Info($"Personalidad creada para {_s.Name}: {reply.Profile.Personality} | Secreto: {reply.Profile.Secret}");
            }

            // Nearly the same line as a moment ago: don't show it, ask for another one (once per turn)
            if (WarnIfRepeated(reply.Reply) && !_retriedRepeat)
            {
                _retriedRepeat = true;
                try
                {
                    var again = PromptBuilder.BuildDialogue(_s, S.Get.MaxRelationChangePerConversation - _relationMovedThisTalk);
                    again[again.Count - 1].Content += "\n\n[Your last attempt at a reply was almost word for word something you already said. Say something new: react to what they just said, move the talk forward, or keep it very short.]";
                    Ask(again, playerText);
                    return;
                }
                catch (Exception ex) { Log.Error("Reintento por repetición", ex); }
            }
            Mem.Turns.Add(new ChatTurn { Role = "npc", Text = reply.Reply, Day = CampaignTime.Now.ToDays });
            _lastAsked = _pendingAsked; _lastRaw = res.Text;
            AskPicture(playerText, reply.Reply);
            if (_s.IsHero) AILegacy.Api.AILegacyApi.RaiseNpcReplied(_s.Hero, reply.Reply);
            UiNpc(reply.Reply);
            _lines++;
            _answered++;

            List<string> effects = new List<string>();
            try { effects = ApplyConsequences(reply); }
            catch (Exception ex) { Log.Error("Error aplicando consecuencias", ex); }
            foreach (var e in effects) { if (UseWindow) _win.VM.AddSystem("» " + e); else if (_dialogMode) _dialogNotes.Add(e); }

            // Very long talk: summarize what no longer fits in the prompt now, without waiting for the close
            try
            {
                int window = S.Get.HistoryTurnsInPrompt;
                if (Mem.Turns.Count >= Math.Max(S.Get.SummarizeAfterTurns + 1, window + 12)) MemorySummarizer.MaybeSummarize(_s, window);
            }
            catch (Exception ex) { Log.Error("Resumen a mitad de charla", ex); }

            SaveFiles.Flush();   // write it now, whether or not the game gets saved

            if (reply.EndConversation)
            {
                _npcEnded = true;
                Log.Info($"{_s.Name} termina la conversación");
                if (UseWindow)
                {
                    _win.VM.AddSystem(L.F("AIL_42b37383", $"({_s.Name} da la conversación por terminada)"));
                    _win.VM.CanType = false;
                    _win.VM.CloseText = L.T("AIL_b9c116de", "Continuar");
                    RefreshSubtitle();
                }
                else if (_dialogMode)
                {
                    _dialogNotes.Add(L.F("AIL_42b37383", $"({_s.Name} da la conversación por terminada)"));
                }
                else
                {
                    InformationManager.ShowInquiry(new InquiryData(_s.Name, reply.Reply + "\n\n(" + _s.Name + L.T("AIL_552be781", " da la conversación por terminada)"),
                        true, false, L.T("AIL_b9c116de", "Continuar"), "", () => Close(true), null), true);
                }
                return;
            }
            UiReady();
        }

        /// <summary>
        /// The reply arrived after the talk was closed (the player left before hearing it). The line is still stored so the
        /// player's message isn't left unanswered in the history. Its actions are not applied, and the NPC is told so.
        /// </summary>
        private void OnLateAnswer(LlmResult res)
        {
            try
            {
                if (res == null || !res.Ok) return;
                if (Mem.Turns.Count == 0 || Mem.Turns[Mem.Turns.Count - 1].Role != "player" || Mem.Turns[Mem.Turns.Count - 1].IsNote) return;
                var reply = NpcReply.Parse(res.Text);
                if (string.IsNullOrWhiteSpace(reply.Reply)) return;
                Mem.Turns.Add(new ChatTurn { Role = "npc", Text = reply.Reply, Day = CampaignTime.Now.ToDays });
                Mem.Turns.Add(new ChatTurn
                {
                    Role = "player", Day = CampaignTime.Now.ToDays,
                    Text = "[Did not happen: they had already walked away and did not hear that last answer" +
                           (reply.Actions != null && reply.Actions.Any(a => !string.IsNullOrWhiteSpace(a)) ? "; nothing you meant to do with it was done (" + string.Join(", ", reply.Actions.Take(3)) + ")" : "") + ".]",
                });
                Log.Info($"Respuesta de {_s.Name} llegada con la charla cerrada: guardada sin aplicar acciones");
                SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Respuesta tardía", ex); }
        }

        /// <summary>Logs and returns true if the reply is nearly the same as a recent one from this character.</summary>
        private bool WarnIfRepeated(string reply)
        {
            try
            {
                var now = Tokens(reply);
                if (now.Count < 6) return false;
                foreach (var t in Mem.Turns.Where(x => x.Role == "npc").Reverse().Take(5))
                {
                    var old = Tokens(t.Text);
                    if (old.Count < 6) continue;
                    double sim = now.Intersect(old).Count() / (double)now.Union(old).Count();
                    if (sim >= 0.8) { Log.Warn($"{_s.Name} se repite: su respuesta se parece un {(int)(sim * 100)} % a una reciente"); return true; }
                }
            }
            catch { }
            return false;
        }

        private static HashSet<string> Tokens(string text) =>
            new HashSet<string>((text ?? "").ToLowerInvariant().Split(new[] { ' ', '\n', ',', '.', ';', ':', '!', '?', '¿', '¡', '*', '"', '«', '»' }, StringSplitOptions.RemoveEmptyEntries).Where(w => w.Length > 2));

        private List<string> ApplyConsequences(NpcReply reply)
        {
            var cfg = S.Get;
            var shown = new List<string>();

            if (!string.IsNullOrWhiteSpace(reply.Thoughts)) Log.Info($"[piensa] {_s.Name}: {reply.Thoughts}");
            if (!string.IsNullOrWhiteSpace(reply.Mood) && reply.Mood.Length <= 30)
            {
                Mem.Mood = reply.Mood;
                Mem.MoodReason = reply.MoodReason?.Length > 120 ? reply.MoodReason.Substring(0, 120) : reply.MoodReason;
                Mem.MoodDay = CampaignTime.Now.ToDays;
                Mem.MoodShown = reply.MoodShown.Length > 140 ? reply.MoodShown.Substring(0, 140) : reply.MoodShown;
            }

            if (!string.IsNullOrWhiteSpace(reply.PlayerIntroducedAs) && reply.PlayerIntroducedAs.Length <= 60
                && !string.Equals(Mem.KnownPlayerName, reply.PlayerIntroducedAs, StringComparison.OrdinalIgnoreCase))
            {
                Mem.KnownPlayerName = reply.PlayerIntroducedAs;
                Log.Info($"{_s.Name} ahora te conoce como \"{reply.PlayerIntroducedAs}\"");
            }
            // What you say you are, kept apart from what you really are (only while they can't know better)
            if (reply.PlayerClaims.Length > 8 && reply.PlayerClaims.Length <= 220 && ContextBuilder.WhyKnowsPlayer(_s) == null)
            {
                string had = Mem.KnownPlayerClaims ?? "";
                if (had.IndexOf(reply.PlayerClaims, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string all = had.Length == 0 ? reply.PlayerClaims : had + " " + reply.PlayerClaims;
                    Mem.KnownPlayerClaims = all.Length > 400 ? all.Substring(all.Length - 400) : all;
                    Log.Info($"{_s.Name} anota lo que dices ser: {reply.PlayerClaims}");
                }
            }
            // Named people standing close hear the answer too
            try { if (TaleWorlds.MountAndBlade.Mission.Current != null) Ambient.Overhear(new Speaker[0], reply.Reply, _s); } catch { }

            var actx = new NpcActions.ActionContext();
            foreach (var msg in NpcActions.Execute(_s, reply.Actions, actx))
            {
                if (!_dialogMode) Notify(msg, Colors.Yellow);
                shown.Add(msg);
            }
            if (actx.EndConversation) reply.EndConversation = true;
            // Words that state a deed as done, with no action behind them, are marked as words
            _deedNudge = NpcActions.CheckSpokenDeeds(_s, reply.Reply, actx, _deedsNudged, reply.Quest != null) ?? _deedNudge;

            // A bandit leader who ambushed you ends the talk without letting you pass: you don't just walk away, it's a fight
            try
            {
                if (reply.EndConversation && _s.Band != null && !actx.LetPass && !Combat.KeepEncounter && Combat.EnemyOnMap(_s)
                    && PlayerEncounter.Current != null && PlayerEncounter.PlayerIsDefender)
                {
                    Combat.QueueMapAttack(_s);
                    shown.Add(L.F("AIL_53ea433d", $"{_s.Name} da la charla por terminada y se lanza al ataque."));
                }
            }
            catch (Exception ex) { Log.Error("Fin del parlamento con una banda", ex); }

            if (_s.IsHero && reply.SignificantEvent.Length > 12) Lore.Develop(_s, reply.SignificantEvent);

            if (reply.Quest != null)
            {
                try { shown.AddRange(QuestSystem.Apply(_s, reply.Quest)); }
                catch (Exception ex) { Log.Error("Error aplicando el encargo", ex); }
            }

            if (cfg.EnableTrust)
            {
                int td = Math.Max(-cfg.MaxTrustChangePerReply, Math.Min(cfg.MaxTrustChangePerReply, reply.TrustChange)) + actx.TrustDelta;
                td = td != 0 ? Bonds.ChangeTrust(_s, td) : 0;
                if (td < 0) _trustLostThisTalk -= td;
                if (td != 0)
                {
                    Log.Info($"Confianza de {_s.Name}: {(td > 0 ? "+" : "")}{td} (ahora {Mem.Trust})");
                    shown.Add(L.F("AIL_c78d48c5", $"Confianza {(td > 0 ? "+" : "")}{td}"));
                }
            }
            if (cfg.EnableTension)
            {
                int xd = Math.Max(-cfg.MaxTensionChangePerReply, Math.Min(cfg.MaxTensionChangePerReply, reply.TensionChange)) + actx.TensionDelta;
                xd = xd != 0 ? Bonds.ChangeTension(_s, xd) : 0;
                if (xd != 0)
                {
                    Log.Info($"Tensión con {_s.Name}: {(xd > 0 ? "+" : "")}{xd} (ahora {Mem.Tension})");
                    shown.Add(L.F("AIL_23b67cad", $"Tensión {(xd > 0 ? "+" : "")}{xd}"));
                }
            }
            if (Bonds.RomancePossible(_s))
            {
                int rd = Math.Max(-cfg.MaxRomanceChangePerReply, Math.Min(cfg.MaxRomanceChangePerReply, reply.RomanceChange));
                rd = rd != 0 ? Bonds.ChangeRomance(_s, rd) : 0;
                if (rd != 0)
                {
                    Log.Info($"Romance con {_s.Name}: {(rd > 0 ? "+" : "")}{rd} (ahora {Mem.Romance})");
                    shown.Add(L.F("AIL_d09068cf", $"Romance {(rd > 0 ? "+" : "")}{rd}"));
                }
            }

            if (_s.IsHero)
            {
                int delta = Math.Max(-cfg.MaxRelationChangePerReply, Math.Min(cfg.MaxRelationChangePerReply, reply.RelationChange + actx.RelationDelta));
                // Liking is harder to win than to lose: gains are scaled down, losses count in full
                if (delta > 0 && cfg.RelationGainPercent < 100)
                {
                    _gainCarry += delta * Math.Max(10, cfg.RelationGainPercent) / 100.0;
                    delta = (int)Math.Floor(_gainCarry + 1e-9);
                    _gainCarry -= delta;
                }
                int budget = cfg.MaxRelationChangePerConversation - _relationMovedThisTalk;
                if (Math.Abs(delta) > budget) delta = Math.Sign(delta) * Math.Max(0, budget);
                if (delta != 0)
                {
                    ChangeRelationAction.ApplyPlayerRelation(_s.Hero, delta, affectRelatives: false, showQuickNotification: true);
                    _relationChangedThisTalk += delta;
                    _relationMovedThisTalk += Math.Abs(delta);
                    Mem.RelationChangedTotal += delta;
                    Log.Info($"Relación con {_s.Name}: {(delta > 0 ? "+" : "")}{delta}");
                    shown.Add(L.F("AIL_a190aec6", $"Relación {(delta > 0 ? "+" : "")}{delta}"));
                }
            }

            AddNote(Mem, reply.Remember, cfg.MaxNotesPerHero, _s.Name);

            if (cfg.EnableRumors && !string.IsNullOrWhiteSpace(reply.RumorText) && reply.RumorText.Length > 10)
            {
                int imp = Math.Max(1, Math.Min(3, reply.RumorImportance));
                string text = reply.RumorText.Length > 300 ? reply.RumorText.Substring(0, 300) : reply.RumorText;
                // Models put a rumour in almost every reply of the same scene. One talk gives one piece of news:
                // later rumours from the same character within a day rewrite it, they don't add another.
                double today = CampaignTime.Now.ToDays;
                bool grew = true;
                if (_lastRumor != null && _lastRumorWho == _s.Key && today - _lastRumor.Day < 1.0 && NewsStore.Contains(_lastRumor))
                {
                    grew = imp > _lastRumor.Importance;
                    _lastRumor.Text = text;
                    _lastRumor.TitleEs = reply.RumorTitleLocal;
                    _lastRumor.TextEs = reply.RumorTextLocal;
                    _lastRumor.Importance = Math.Max(_lastRumor.Importance, imp);
                    Log.Info($"Rumor de la charla con {_s.Name}, actualizado: {text}");
                }
                else
                {
                    _lastRumor = new NewsItem
                    {
                        Type = "rumor",
                        Importance = imp,
                        Text = text,
                        TitleEs = reply.RumorTitleLocal,
                        TextEs = reply.RumorTextLocal,
                        SettlementId = _s.Settlement?.StringId,
                        Kingdoms = _s.Faction != null ? new List<string> { _s.Faction.StringId } : new List<string>(),
                        Heroes = _s.IsHero ? new List<string> { _s.Hero.StringId, Hero.MainHero.StringId } : new List<string> { Hero.MainHero.StringId },
                    };
                    _lastRumorWho = _s.Key;
                    NewsStore.Add(_lastRumor);
                }
                // An important rumour starts its own world incident (once, unless it becomes more important)
                if (grew && reply.RumorImportance >= 2 && _s.IsHero)
                    WorldSim.OnIncident($"A rumour has started to spread, first told by {_s.Name}: {reply.RumorText}", _s.Settlement, false);
            }
            return shown;
        }

        // ========================================================== commands

        private void HandleCommand(string text)
        {
            int sp = text.IndexOf(' ');
            var cmd = (sp > 0 ? text.Substring(0, sp) : text).ToLowerInvariant();
            var arg = sp > 0 ? text.Substring(sp + 1).Trim() : "";

            switch (cmd)
            {
                case "/nota": case "/note":
                case "/instruccion": case "/instruction":
                    if (arg.Length == 0) { UiSystem(L.T("AIL_3ff4d651", "Uso: /nota <instrucción>   Ej: /nota Es mi esposa y está embarazada; nunca me sermonea sobre el peligro.")); return; }
                    Mem.CustomInstructions.Add(arg.Length > 600 ? arg.Substring(0, 600) : arg);
                    Log.Info($"Instrucción para {_s.Name}: {arg}");
                    SaveFiles.Flush();
                    UiSystem(L.F("AIL_84e9a594", $"Instrucción guardada para {_s.Name}."));
                    return;

                case "/error": case "/mistake":
                    if (arg.Length == 0) { UiSystem(L.T("AIL_e7b1c7c6", "Uso: /error <qué hizo mal en su última respuesta>   ·   /lecciones   ·   /borrarleccion <número>")); return; }
                    ReportMistake(arg);
                    return;

                case "/lecciones": case "/lessons":
                    UiSystem(Lessons.Listing());
                    return;

                case "/borrarleccion": case "/deletelesson":
                {
                    int.TryParse(new string(arg.Where(char.IsDigit).ToArray()), out int n);
                    if (n > 0 && Lessons.Remove(n)) UiSystem(L.F("AIL_e7b1c7c5", $"Lección {n} borrada."));
                    else UiSystem(Lessons.Listing());
                    return;
                }

                case "/borrarnotas": case "/clearnotes":
                    Mem.CustomInstructions.Clear();
                    SaveFiles.Flush();
                    UiSystem(L.F("AIL_96ea6365", $"Instrucciones de {_s.Name} borradas."));
                    return;

                case "/memoria": case "/memory":
                {
                    var b = new StringBuilder();
                    b.AppendLine(L.F("AIL_a5eee76f", $"Te conoce como: {ContextBuilder.PlayerNameFor(_s) ?? L.T("AIL_99316d74", "(desconocido)")}   ·   Conversaciones: {Mem.ConversationCount}   ·   Mentiras pilladas: {Mem.LiesSuspected}"));
                    if (!string.IsNullOrWhiteSpace(Mem.Mood)) b.AppendLine(L.F("AIL_2259555a", $"Ánimo: {Mem.Mood} ({Mem.MoodReason})"));
                    foreach (var c in World.Mood.Describe(_s)) b.AppendLine("   " + c);
                    if (S.Get.EnableTension) b.AppendLine(L.F("AIL_55654690", $"Tensión: {Bonds.Tension(Mem)}"));
                    if (S.Get.EnableTrust) { Bonds.EnsureTrust(_s); b.AppendLine(L.F("AIL_2cb6f858", $"Confianza: {Mem.Trust}") + (Bonds.RomancePossible(_s) ? L.F("AIL_bbb8296b", $"   ·   Romance: {Mem.Romance}") : "")); }
                    if (_s.IsHero && OrderSystem.Find(_s.Hero) is PartyOrder po) b.AppendLine(L.F("AIL_7ff96949", $"Orden en curso: {po.Type} {po.TargetName} (hasta el día {po.UntilDay:0})"));
                    if (Mem.Profile != null && !Mem.Profile.IsEmpty) b.AppendLine("Personalidad: " + Mem.Profile.Personality).AppendLine("Secreto: " + Mem.Profile.Secret);
                    if (!string.IsNullOrWhiteSpace(Mem.Summary)) b.AppendLine("Resumen: " + Mem.Summary);
                    foreach (var n in Mem.Notes) b.AppendLine(L.T("AIL_0ede5f95", "· Recuerda: ") + n);
                    foreach (var n in Mem.CustomInstructions) b.AppendLine(L.T("AIL_70ab64bf", "· Tu instrucción: ") + n);
                    if (Mem.ImportedFrom != null) b.AppendLine(L.T("AIL_4e260c5a", "Importado de AIInfluence: ") + Mem.ImportedFrom);
                    UiSystem(b.ToString().TrimEnd());
                    return;
                }

                case "/olvidar": case "/forget":
                    if (!string.Equals(arg, "confirmar", StringComparison.OrdinalIgnoreCase))
                    {
                        UiSystem(L.F("AIL_07d92c66", $"Esto borra TODA la memoria de {_s.Name} sobre ti. Para hacerlo escribe: /olvidar confirmar"));
                        return;
                    }
                    MemoryStore.Remove(_s.Key);
                    Notify(L.F("AIL_1a8ef2ad", $"{_s.Name} lo ha olvidado todo."), Colors.Green);
                    Close(false);
                    return;

                default:
                    UiSystem(L.T("AIL_dd76aabc", "Comandos:  /nota <texto> (instrucción permanente)  ·  /borrarnotas  ·  /memoria (qué recuerda)  ·  /olvidar  ·  /ayuda"));
                    return;
            }
        }

        public static void AddNote(HeroMemory mem, string note, int max, string who)
        {
            if (string.IsNullOrWhiteSpace(note) || note.Length <= 3) return;
            if (note.Length > 200) note = note.Substring(0, 200);
            if (mem.Notes.Any(n => string.Equals(n, note, StringComparison.OrdinalIgnoreCase))) return;
            mem.Notes.Add(note);
            while (mem.Notes.Count > max) mem.Notes.RemoveAt(0);
            Log.Info($"{who} recuerda: {note}");
        }

        // ============================================================ closing

        /// <summary>Ends the current talk as if the character had ended it (after accepting a duel, a surrender...).</summary>
        public static void EndFromOutside()
        {
            try { var a = Active; if (a != null && !a._closed) { a._npcEnded = true; a.Close(true); } }
            catch (Exception ex) { Log.Error("Cerrar la charla", ex); }
        }

        private void Close(bool npcEnded)
        {
            if (_closed) return;
            _closed = true;
            if (Active == this) Active = null;
            _win?.Close();
            _win = null;

            if (_answered > 0)
            {
                Mem.ConversationCount++;
                Mem.LastConversationDay = CampaignTime.Now.ToDays;
                if (_s.IsHero) QuestObjectives.OnTalked(_s.Hero);
                Bonds.Touch(Mem);
                // Familiarity: a real talk (not two lines) with no trust lost adds one point, up to a modest cap.
                // High trust has to be earned with deeds, not hours of chatting.
                try
                {
                    if (S.Get.EnableTrust && _s.IsHero && _answered >= 3 && _trustLostThisTalk == 0 && Mem.Trust < 30) Bonds.ChangeTrust(_s, 1);
                }
                catch { }
            }
            Log.Info($"Conversación con {_s.Name} cerrada ({_lines} líneas, relación {(_relationChangedThisTalk >= 0 ? "+" : "")}{_relationChangedThisTalk})");

            MemorySummarizer.MaybeSummarize(_s);
            try { RagIndex.Schedule(_s); } catch { }
            SaveFiles.Flush();

            if (npcEnded)
            {
                try
                {
                    // On the map, talking to a party is an "encounter". We have to flag that we're leaving, like the
                    // game's own farewell does, or the attack/leave menu pops up.
                    if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement && !Combat.KeepEncounter)
                        PlayerEncounter.LeaveEncounter = true;
                    if (Campaign.Current?.ConversationManager != null && Campaign.Current.ConversationManager.IsConversationInProgress)
                        Campaign.Current.ConversationManager.EndConversation();
                }
                catch (Exception ex) { Log.Error("No se pudo cerrar la conversación del juego", ex); }
            }
        }

        private static string _lastNotice;
        private static DateTime _lastNoticeAt;

        public static void Notify(string msg, Color color)
        {
            // Show the same notice only once in a row (the game evaluates a dialogue line more than once)
            if (msg == _lastNotice && (DateTime.UtcNow - _lastNoticeAt).TotalSeconds < 2) return;
            _lastNotice = msg; _lastNoticeAt = DateTime.UtcNow;
            InformationManager.DisplayMessage(new InformationMessage(msg, color));
        }
    }
}
