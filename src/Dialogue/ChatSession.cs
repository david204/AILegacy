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
            // a new conversation: whoever was still speaking stops
            try { AILegacy.Voice.Speech.Stop(); } catch { }
            try
            {
                string shown = World.InfoExchange.DeliverCarried(s);
                if (shown.Length > 0) Notify(shown, Colors.Green);
            }
            catch (Exception ex) { Log.Error("Errands in hand", ex); }
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
            Log.Info($"Conversation started with {s.Name} ({s.Key})");
            HandOverCarried(s);

            var vm = new ChatVM(s.Name, session.OnPlayerSaid, session.OnCloseClicked);
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
            catch (Exception ex) { Log.Error("Reply text while it arrives", ex); }
            try { Active?.ContinueIfSilent(); }
            catch (Exception ex) { Log.Error("Continue once the voice ends", ex); }
            try { TickScene(); }
            catch (Exception ex) { Log.Error("Gesture or walk away after the talk", ex); }
        }

        // ====================================================== body and face in the scene

        // The gesture's own clip, a moment after the game has played the line's tags (so it is not overwritten)
        private static TaleWorlds.MountAndBlade.Agent _clipAgent;
        private static string _clipGesture;
        private static DateTime _clipAt;
        // Someone who ended the talk walks off once the game has closed the conversation
        private static TaleWorlds.MountAndBlade.Agent _walkOffAgent;
        private static DateTime _walkOffAt;

        private static void TickScene()
        {
            if (_clipAgent != null && DateTime.UtcNow >= _clipAt)
            {
                var a = _clipAgent; string g = _clipGesture;
                _clipAgent = null; _clipGesture = null;
                if (TaleWorlds.MountAndBlade.Mission.Current != null) Gestures.PlayClip(a, g);
            }
            if (_walkOffAgent != null && DateTime.UtcNow >= _walkOffAt)
            {
                var cm = Campaign.Current?.ConversationManager;
                if (cm != null && cm.IsConversationInProgress) { _walkOffAt = DateTime.UtcNow.AddMilliseconds(300); return; }
                var a = _walkOffAgent;
                _walkOffAgent = null;
                if (TaleWorlds.MountAndBlade.Mission.Current != null && a.IsActive()) SceneActions.WalkAway(a);
            }
            if (_strikeAgent != null && DateTime.UtcNow >= _strikeAt)
            {
                var cm = Campaign.Current?.ConversationManager;
                if (cm != null && cm.IsConversationInProgress) { _strikeAt = DateTime.UtcNow.AddMilliseconds(300); return; }
                var a = _strikeAgent;
                _strikeAgent = null;
                if (TaleWorlds.MountAndBlade.Mission.Current != null && a.IsActive()) SceneActions.DeathBlow(a, _strikeByPlayer ? TaleWorlds.MountAndBlade.Agent.Main : null);
            }
        }

        /// <summary>The character in front of the player in a scene (not on the map, where the game draws a portrait).</summary>
        private static TaleWorlds.MountAndBlade.Agent SceneAgent()
        {
            try
            {
                if (TaleWorlds.MountAndBlade.Mission.Current == null) return null;
                return Campaign.Current?.ConversationManager?.OneToOneConversationAgent as TaleWorlds.MountAndBlade.Agent;
            }
            catch { return null; }
        }

        private void QueueClip(string gesture)
        {
            if (string.IsNullOrWhiteSpace(gesture) || gesture.Trim().Equals("none", StringComparison.OrdinalIgnoreCase)) return;
            var a = SceneAgent();
            if (a == null) return;
            _clipAgent = a; _clipGesture = gesture; _clipAt = DateTime.UtcNow.AddMilliseconds(150);
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
        public void Seed(string npcText, string emotion = null, string gesture = null)
        {
            _lastPlayer = "";
            _lastNpc = npcText ?? "";
            _npcInTalk = false;
            _emotion = emotion ?? ""; _gesture = gesture ?? ""; _lookShown = false;
            HasUnreadReply = true;
            Seeded = true;
            try { AILegacy.Voice.Speech.Say(_s.Key, _s.IsFemale, _lastNpc); } catch { }
            _lines++;
            _answered++;
        }
        public bool IsWaiting => _waiting;
        public bool Ended => _npcEnded;
        public Speaker Who => _s;

        // ====================================================== the talk for the scrollable panel in the dialogue box
        // Every line of this talk that has reached the screen, oldest first. The panel (UI.ConversationTalkPanel) shows
        // them all in place of the game's text, which still gets the last exchange as before (voice mods read it).

        public enum TalkKind { Player, Npc, Note }

        /// <summary>One line of the talk as the panel shows it.</summary>
        public sealed class TalkLine
        {
            public TalkKind Kind;
            public string Speaker = "";
            public string Text = "";
        }

        private const int MaxTalkLines = 200;
        private readonly List<TalkLine> _talk = new List<TalkLine>();
        private int _talkRevision;
        /// <summary>The current reply (or failure) is already in the talk, so showing the box again doesn't repeat it.</summary>
        private bool _npcInTalk;
        /// <summary>"Finishing speaking" is on screen while Continue waits for the voice.</summary>
        private bool _voiceWaitShown;
        private string _talkStats = "";

        public bool IsDialogMode => _dialogMode;
        public IReadOnlyList<TalkLine> TalkLines => _talk;
        /// <summary>Changes whenever a line is added to the talk.</summary>
        public int TalkRevision => _talkRevision;
        /// <summary>The status line under the last reply (empty when the bars show it or it is turned off).</summary>
        public string TalkStats => _talkStats;
        /// <summary>There is something to show in the panel: a line already said, or a reply being written.</summary>
        public bool HasTalkToShow => _dialogMode && !_closed && (_talk.Count > 0 || (_waiting && _liveShown > 0));

        private void AddTalk(TalkKind kind, string speaker, string text)
        {
            if (!_dialogMode || string.IsNullOrWhiteSpace(text)) return;
            _talk.Add(new TalkLine { Kind = kind, Speaker = speaker ?? "", Text = text.Replace("\r", "").Trim() });
            while (_talk.Count > MaxTalkLines) _talk.RemoveAt(0);
            _talkRevision++;
        }

        private void AddTalkReply(string npcText)
        {
            if (_npcInTalk || string.IsNullOrEmpty(npcText)) return;
            _npcInTalk = true;
            // a failure in brackets is a note, not something the character said
            if (npcText.StartsWith("(")) AddTalk(TalkKind.Note, "", npcText);
            else AddTalk(TalkKind.Npc, _s.Name, npcText);
        }

        /// <summary>
        /// What the box shows past the lines already in the talk: the reply as it is being written, "thinking" while it
        /// hasn't started, a finished reply already painted in the box but not yet read, or "finishing speaking". Null if nothing.
        /// </summary>
        public TalkLine TalkPending()
        {
            if (!_dialogMode || _closed) return null;
            if (_waiting)
            {
                string part = _live?.Text ?? "";
                if (_liveShown > 0 && _liveShown <= part.Length) return new TalkLine { Kind = TalkKind.Npc, Speaker = _s.Name, Text = part.Substring(0, _liveShown).Replace("\r", "").Trim() };
                return new TalkLine { Kind = TalkKind.Note, Text = L.F("AIL_30214b10", $"*{_s.Name} todavía está pensando…*") };
            }
            if (!_npcInTalk && _livePainted && _lastNpc.Length > 0)
                return _lastNpc.StartsWith("(") ? new TalkLine { Kind = TalkKind.Note, Text = _lastNpc } : new TalkLine { Kind = TalkKind.Npc, Speaker = _s.Name, Text = _lastNpc.Replace("\r", "").Trim() };
            if (_voiceWaitShown && Holding) return new TalkLine { Kind = TalkKind.Note, Text = L.F("AIL_e7b1ca05", $"*{_s.Name} termina de hablar…*") };
            return null;
        }

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
            Log.Info($"Conversation (dialogue) started with {s.Name} ({s.Key})");
            HandOverCarried(s);
            try { Campaign.Current.ConversationManager.ConversationEndOneShot += () => session.Close(false); }
            catch (Exception ex) { Log.Error("Could not hook into the end of the conversation", ex); }
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
            // What was written for a reply that never came is offered again
            string retry = _retryText ?? "";
            _retryText = null;
            // Nothing written, or cancelled: the player keeps silent, and the character answers to that silence
            InformationManager.ShowTextInquiry(new TextInquiryData(
                L.F("AIL_4f47d62d", $"Tu mensaje para {_s.Name}"), body + L.T("AIL_904fdecc", "\n\n(/ayuda para ver los comandos)"),
                true, true, L.T("AIL_e5c9b3a3", "Confirmar"), L.T("AIL_e7b1ca01", "Callar"),
                t => OnPlayerSaid(t), () => MainThread.Post(() => OnPlayerSaid("")), false,
                t => new Tuple<bool, string>(true, ""), "", retry), true);
        }

        /// <summary>What the character is told when the player says nothing at all.</summary>
        private const string SilenceTurn = "*says nothing and stays silent*";

        /// <summary>Text for the NPC's dialogue box right now. Marks the reply as read.</summary>
        public string ConsumeDialogText()
        {
            if (_waiting)
            {
                // Clicked "See their reply" while it is still being written: keep showing what there is
                var live = _live;
                string part = live?.Text ?? "";
                if (_liveShown > 0 && _liveShown <= part.Length) return Compose(part.Substring(0, _liveShown)) + Gestures.ThinkingTags;
                return Sanitize(L.F("AIL_30214b10", $"*{_s.Name} todavía está pensando…*")) + Gestures.ThinkingTags;
            }
            if (!HasUnreadReply && _lastNpc.Length == 0 && _dialogNotes.Count == 0) return L.T("AIL_07e9f3c5", "(Envía tu mensaje y espera su respuesta)");
            // After Continue: only what came of the held actions, the reply itself was already read
            if (_notesOnly)
            {
                _notesOnly = false;
                HasUnreadReply = false;
                _voiceWaitShown = false;
                var nb = new StringBuilder();
                foreach (var n in _dialogNotes) { if (nb.Length > 0) nb.Append("{newline}"); nb.Append("» ").Append(n); AddTalk(TalkKind.Note, "", "» " + n); }
                _dialogNotes.Clear();
                return nb.Length > 0 ? Sanitize(nb.ToString()) : "...";
            }
            // The face and the gesture of a reply play once, when it is first shown
            string tags = "";
            if (HasUnreadReply && _lastNpc.Length > 0 && !_lastNpc.StartsWith("(") && !_lookShown)
            {
                _lookShown = true;
                tags = Gestures.Tags(_emotion, _gesture);
                if (tags.Length > 0) QueueClip(_gesture);
            }
            HasUnreadReply = false;
            var sb = new StringBuilder();
            // With "only their words" the box holds nothing but what the character says, so a voice mod that reads
            // the box doesn't also read the player's line and the name in front
            bool bare = S.Get.DialogOnlyTheirWords;
            if (!bare && _lastPlayer.Length > 0) sb.Append(L.T("AIL_63ea9af4", "Tú: ")).Append(_lastPlayer).Append("{newline}{newline}");
            if (_lastNpc.Length > 0) sb.Append(bare || _lastNpc.StartsWith("(") ? "" : _s.Name + ": ").Append(_lastNpc);
            AddTalkReply(_lastNpc);
            foreach (var n in _dialogNotes) { sb.Append("{newline}» ").Append(n); AddTalk(TalkKind.Note, "", "» " + n); }
            _dialogNotes.Clear();
            string stats = _lastNpc.Length > 0 ? StatsLine() : "";
            if (stats.Length > 0) sb.Append("{newline}").Append(stats);
            if (stats != _talkStats) { _talkStats = stats; _talkRevision++; }
            // the game's animation tags go after the text has been cleaned of brackets
            return Sanitize(sb.ToString()) + tags;
        }

        // How the character looks and moves with their last line, and whether that was already played
        private string _emotion = "", _gesture = "";
        private bool _lookShown;
        // A message that got no reply, offered again in the next text box
        private string _retryText;

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
            catch (Exception ex) { Log.Error("Show the reply on its own", ex); }
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

        public string WaitText => _waiting || _preparing || _lastNpc.Length == 0 ? L.T("AIL_07e9f3c5", "(Envía tu mensaje y espera su respuesta)") : Sanitize(_lastNpc);

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
            if (_dialogMode)
            {
                _lastPlayer = text; _lastNpc = ""; _emotion = ""; _gesture = "";
                _npcInTalk = false; _voiceWaitShown = false;
                if (_talkStats.Length > 0) { _talkStats = ""; _talkRevision++; }
                string me = "";
                try { me = Hero.MainHero?.Name?.ToString() ?? ""; } catch { }
                AddTalk(TalkKind.Player, me.Length > 0 ? me : L.T("AIL_4c6b494e", "Tú"), text);
                return;
            }
            if (UseWindow) _win.VM.AddLine(L.T("AIL_4c6b494e", "Tú"), text, ChatVM.PlayerColor);
            else _transcript.Add(L.F("AIL_016ea49e", $"Tú: {text}"));
        }

        // Bumped when a reply is shown: a voice made ready after that (or after the wait ran out) is not for it
        private int _prepGen;
        // The reply is in; its voice is being made before it is shown
        private bool _preparing;

        private void UiNpc(string text)
        {
            // In the dialogue box the voice is made first and the reply is told ready when both can start together.
            // A voice that fails, or is not ready in 30 s, leaves the reply silent.
            if (_dialogMode)
            {
                int gen = ++_prepGen;
                bool asked = false;
                try
                {
                    asked = AILegacy.Voice.Speech.Prepare(_s.Key, _s.IsFemale, text, wav => MainThread.Post(() =>
                    {
                        if (gen != _prepGen) return;
                        _prepGen++; _preparing = false;
                        if (_closed || Active != this) return;
                        try { AILegacy.Voice.Speech.PlayPrepared(wav); } catch { }
                        ShowNpc(text, voiced: true);
                    }));
                }
                catch { }
                if (asked)
                {
                    _preparing = true;
                    System.Threading.Tasks.Task.Delay(30000).ContinueWith(_ => MainThread.Post(() =>
                    {
                        if (gen != _prepGen) return;
                        _prepGen++; _preparing = false;
                        Log.Warn($"Voice: the line of {_s.Name} was not ready in 30 s; the reply shows without it");
                        if (!_closed && Active == this) ShowNpc(text, voiced: true);
                    }));
                    return;
                }
            }
            ShowNpc(text, voiced: false);
        }

        private void ShowNpc(string text, bool voiced)
        {
            // Said aloud, only their words (never the player's line or a name in front)
            if (!voiced) { try { AILegacy.Voice.Speech.Say(_s.Key, _s.IsFemale, text); } catch { } }
            if (_dialogMode)
            {
                _lastNpc = text;
                _npcInTalk = false;
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
            // Our window covers no dialogue line of the game's, so the face and gesture are played on the agent directly
            try { var a = SceneAgent(); if (a != null) { _lookShown = true; Gestures.Play(a, _emotion, _gesture); } } catch { }
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
                if (error) { _lastNpc = "(" + text + ")"; _npcInTalk = false; HasUnreadReply = true; Notify(text.Split('\n')[0], Colors.Red); if (_livePainted) PaintBox(Compose(_lastNpc)); }
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

            string retry = _retryText ?? "";
            _retryText = null;
            // "Say" with nothing written: the player keeps silent
            InformationManager.ShowTextInquiry(new TextInquiryData(
                _s.Name, body.ToString().TrimEnd(), true, true, L.T("AIL_aa23f794", "Decir"), L.T("AIL_e5c9b3a5", "Terminar"),
                OnPlayerSaid, OnCloseClicked, false,
                t => new Tuple<bool, string>(true, ""), "", retry), true);
        }

        // ============================================================ logic

        private void OnPlayerSaid(string text)
        {
            if (_closed || _waiting) return;
            text = (text ?? "").Trim();
            // Nothing said: in our window the send button needs text, elsewhere it is keeping silent
            if (text.Length == 0 && UseWindow) return;
            if (_npcEnded || Holding) return;
            if (text.StartsWith("/")) { HandleCommand(text); return; }

            bool silent = text.Length == 0;
            if (silent) text = SilenceTurn;
            Mem.Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays });
            if (_s.IsHero && !silent) AILegacy.Api.AILegacyApi.RaisePlayerSaid(_s.Hero, text);
            UiPlayer(silent ? "*" + L.T("AIL_e7b1ca02", "Te quedas en silencio.") + "*" : text);
            if (!silent) Ambient.Overhear(_s, text);
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
                Log.Error("Error building the prompt", ex);
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
        // The instruction of an opening line still waiting for its answer, so a retry asks for the same thing
        private string _opening;

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

        // ---- a video of the scene, on request

        private string _video, _pairPlayer, _pairNpc;
        public bool CanMakeVideo => World.SceneVideos.Enabled && !World.SceneVideos.Busy && !string.IsNullOrEmpty(_pairNpc) && !_waiting && !_closed;
        public bool HasVideo => _video != null && System.IO.File.Exists(_video);

        public void MakeVideo()
        {
            if (!CanMakeVideo) return;
            string player = Hero.MainHero?.Name?.ToString() ?? "";
            var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
            string where = (st != null ? st.Name.ToString() : "the open country") + ", " + (CampaignTime.Now.IsNightTime ? "at night" : "by day");
            string exchange = $"{player}: {_pairPlayer}\n{_s.Name}: {_pairNpc}";
            var self = this;
            World.SceneVideos.Offer(World.SceneImages.People(Mem), where, exchange, path =>
            {
                self._video = path;
                if (self.UseWindow && !self._closed) self.ShowVideo();
                else Notify(L.T("AIL_e7b1c85e", "El vídeo de la escena está listo: elige «Ver el vídeo»."), Colors.Gray);
            });
        }

        public void ShowVideo()
        {
            if (HasVideo) World.SceneVideos.Play(_video, L.F("AIL_e7b1c7ce", $"La escena con {_s.Name}"));
        }

        private void AskPicture(string playerText, string npcText)
        {
            _pairPlayer = playerText; _pairNpc = npcText;
            try
            {
                if (!WithPictures || !World.SceneImages.Enabled) return;
                string player = Hero.MainHero?.Name?.ToString() ?? "";
                string where = World.SceneImages.Setting();
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
            catch (Exception ex) { Log.Error("Talk image", ex); }
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
            _opening = instruction;
            try
            {
                var messages = PromptBuilder.BuildDialogue(_s, S.Get.MaxRelationChangePerConversation - _relationMovedThisTalk, instruction);
                Ask(messages, "");
            }
            catch (Exception ex) { Log.Error("Character first line", ex); }
        }

        private void Ask(List<ChatMessage> messages, string text)
        {
            _pendingAsked = messages;
            _waiting = true;
            UiWaiting(true);
            // they think it over while the reply is on its way
            try { Gestures.Think(SceneAgent()); } catch { }

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
                    if (session != World.WorldSim.Session) { _waiting = false; _closed = true; Log.Info("Reply to a talk from another game, discarded"); return; }
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
                // an opening line has no player turn of its own to take back; what was written comes back in the next box
                if (!string.IsNullOrEmpty(playerText))
                {
                    RemoveLastPlayerTurn(playerText);
                    if (playerText != SilenceTurn) _retryText = playerText;
                }
                // the provider's error goes to the log; the player sees the character lost for words
                Log.Error("No reply: " + res.Error);
                NoReply();
                LlmClient.ShowFailure(res);
                return;
            }

            var reply = NpcReply.Parse(res.Text);
            Mem.LastAiResponse = res.Text != null && res.Text.Length > 4000 ? res.Text.Substring(0, 4000) : res.Text;
            if (_s.IsHero) Mem.Snapshot = ContextBuilder.Snapshot(_s.Hero);
            if (string.IsNullOrWhiteSpace(reply.Reply)) reply.Reply = L.T("AIL_da6122fe", "*te mira en silencio*");

            if (reply.Profile != null && Mem.Profile != null && !Mem.Profile.IsEmpty && Mem.Profile.IsIncomplete)
            {
                int filled = Mem.Profile.FillMissingFrom(reply.Profile);
                if (filled > 0) { Mem.ProfileRevision++; Log.Info($"Profile of {_s.Name} completed: {filled} missing part(s)"); }
            }
            if (reply.Profile != null && (Mem.Profile == null || Mem.Profile.IsEmpty))
            {
                Mem.Profile = reply.Profile;
                Log.Info($"Personality created for {_s.Name}: {reply.Profile.Personality} | Secret: {reply.Profile.Secret}");
            }

            // Nearly the same line as a moment ago: don't show it, ask for another one (once per turn)
            if (WarnIfRepeated(reply.Reply) && !_retriedRepeat)
            {
                _retriedRepeat = true;
                try
                {
                    var again = PromptBuilder.BuildDialogue(_s, S.Get.MaxRelationChangePerConversation - _relationMovedThisTalk, string.IsNullOrEmpty(playerText) ? _opening : null);
                    again[again.Count - 1].Content += "\n\n[Your last attempt at a reply was almost word for word something you already said. Say something new: react to what they just said, move the talk forward, or keep it very short.]";
                    Ask(again, playerText);
                    return;
                }
                catch (Exception ex) { Log.Error("Retry due to repetition", ex); }
            }
            Mem.Turns.Add(new ChatTurn { Role = "npc", Text = reply.Reply, Day = CampaignTime.Now.ToDays });
            _lastAsked = _pendingAsked; _lastRaw = res.Text;
            AskPicture(playerText, reply.Reply);
            if (_s.IsHero) AILegacy.Api.AILegacyApi.RaiseNpcReplied(_s.Hero, reply.Reply);
            _emotion = reply.Emotion ?? ""; _gesture = reply.Gesture ?? ""; _lookShown = false;
            UiNpc(reply.Reply);
            _lines++;
            _answered++;

            List<string> effects = new List<string>();
            try { effects = ApplyConsequences(reply); }
            catch (Exception ex) { Log.Error("Error applying consequences", ex); }
            foreach (var e in effects) { if (UseWindow) _win.VM.AddSystem("» " + e); else if (_dialogMode) _dialogNotes.Add(e); }

            // Very long talk: summarize what no longer fits in the prompt now, without waiting for the close
            try
            {
                int window = S.Get.HistoryTurnsInPrompt;
                if (Mem.Turns.Count >= Math.Max(S.Get.SummarizeAfterTurns + 1, window + 12)) MemorySummarizer.MaybeSummarize(_s, window);
            }
            catch (Exception ex) { Log.Error("Mid-talk summary", ex); }

            SaveFiles.Flush();   // write it now, whether or not the game gets saved

            // A reply that ends the talk or turns it into something else (a fight, a departure, a new companion) waits
            // for the player: they read it, hear it out, press Continue, and only then does it happen
            if (Holding)
            {
                Log.Info($"{_s.Name}: {(_held.Count > 0 ? string.Join(", ", _held) : "end of the talk")} held until the player continues");
                string how = HeldNotice();
                string leaving = _heldLeave ? L.F("AIL_e7b1ca04", $"*{_s.Name} se da la vuelta para marcharse…*") : "";
                if (UseWindow)
                {
                    if (leaving.Length > 0) _win.VM.AddSystem(leaving);
                    _win.VM.AddSystem(how);
                    _win.VM.CanType = false;
                    _win.VM.CloseText = L.T("AIL_b9c116de", "Continuar");
                    RefreshSubtitle();
                }
                else
                {
                    if (leaving.Length > 0) _dialogNotes.Add(leaving);
                    _dialogNotes.Add(how);
                }
                return;
            }

            if (reply.EndConversation)
            {
                _npcEnded = true;
                Log.Info($"{_s.Name} ends the conversation");
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
                Log.Info($"Reply from {_s.Name} arrived after the talk closed: saved without applying actions");
                SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Late reply", ex); }
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
                    if (sim >= 0.8) { Log.Warn($"{_s.Name} repeats itself: reply is {(int)(sim * 100)} % similar to a recent one"); return true; }
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

            if (!string.IsNullOrWhiteSpace(reply.Thoughts)) Log.Info($"[thinks] {_s.Name}: {reply.Thoughts}");
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
                Log.Info($"{_s.Name} now knows you as \"{reply.PlayerIntroducedAs}\"");
            }
            // What you say you are, kept apart from what you really are (only while they can't know better)
            if (reply.PlayerClaims.Length > 8 && reply.PlayerClaims.Length <= 220 && ContextBuilder.WhyKnowsPlayer(_s) == null)
            {
                string had = Mem.KnownPlayerClaims ?? "";
                if (had.IndexOf(reply.PlayerClaims, StringComparison.OrdinalIgnoreCase) < 0)
                {
                    string all = had.Length == 0 ? reply.PlayerClaims : had + " " + reply.PlayerClaims;
                    Mem.KnownPlayerClaims = all.Length > 400 ? all.Substring(all.Length - 400) : all;
                    Log.Info($"{_s.Name} notes what you claim to be: {reply.PlayerClaims}");
                }
            }
            // Named people standing close hear the answer too
            try { if (TaleWorlds.MountAndBlade.Mission.Current != null) Ambient.Overhear(new Speaker[0], reply.Reply, _s); } catch { }

            var asked = (reply.Actions ?? new List<string>()).Where(a => !string.IsNullOrWhiteSpace(a)).ToList();
            // A child can make nothing happen in the world: at most they run off
            if (_s.IsMinor) asked = asked.Where(a => HeldName(a) == "leave").ToList();
            // What ends or turns the talk waits until the player has read and heard the reply (dialogue box and our window)
            var later = _dialogMode || UseWindow ? asked.Where(a => HeldNames.Contains(HeldName(a))).ToList() : new List<string>();
            var now = asked.Where(a => !later.Contains(a)).ToList();

            var actx = new NpcActions.ActionContext();
            foreach (var msg in NpcActions.Execute(_s, now, actx))
            {
                if (!_dialogMode) Notify(msg, Colors.Yellow);
                shown.Add(msg);
            }
            if (actx.EndConversation) reply.EndConversation = true;
            bool hold = (_dialogMode || UseWindow) && (later.Count > 0 || reply.EndConversation);
            if (hold)
            {
                _held = later;
                _heldEnds = reply.EndConversation;
                _heldLeave = later.Count == 0 || later.Any(a => HeldName(a) == "leave");
                // asked for and on its way: not a deed spoken without its action
                foreach (var a in later) actx.Tried.Add(HeldName(a));
            }
            // Words that state a deed as done, with no action behind them, are marked as words
            _deedNudge = NpcActions.CheckSpokenDeeds(_s, reply.Reply, actx, _deedsNudged, reply.Quest != null) ?? _deedNudge;

            // A bandit leader who ambushed you ends the talk without letting you pass: you don't just walk away, it's a fight
            // (when the end is held, this is decided once the player continues)
            if (!hold) BandEnd(reply.EndConversation, actx.LetPass, shown);

            if (_s.IsHero && reply.SignificantEvent.Length > 12) Lore.Develop(_s, reply.SignificantEvent);

            if (reply.Quest != null)
            {
                try { shown.AddRange(QuestSystem.Apply(_s, reply.Quest)); }
                catch (Exception ex) { Log.Error("Error applying the task", ex); }
            }

            if (cfg.EnableTrust)
            {
                int td = Math.Max(-cfg.MaxTrustChangePerReply, Math.Min(cfg.MaxTrustChangePerReply, reply.TrustChange)) + actx.TrustDelta;
                td = td != 0 ? Bonds.ChangeTrust(_s, td) : 0;
                if (td < 0) _trustLostThisTalk -= td;
                if (td != 0)
                {
                    Log.Info($"Trust of {_s.Name}: {(td > 0 ? "+" : "")}{td} (now {Mem.Trust})");
                    shown.Add(L.F("AIL_c78d48c5", $"Confianza {(td > 0 ? "+" : "")}{td}"));
                }
            }
            if (cfg.EnableTension)
            {
                int xd = Math.Max(-cfg.MaxTensionChangePerReply, Math.Min(cfg.MaxTensionChangePerReply, reply.TensionChange)) + actx.TensionDelta;
                xd = xd != 0 ? Bonds.ChangeTension(_s, xd) : 0;
                if (xd != 0)
                {
                    Log.Info($"Tension with {_s.Name}: {(xd > 0 ? "+" : "")}{xd} (now {Mem.Tension})");
                    shown.Add(L.F("AIL_23b67cad", $"Tensión {(xd > 0 ? "+" : "")}{xd}"));
                }
            }
            if (Bonds.RomancePossible(_s))
            {
                int rd = Math.Max(-cfg.MaxRomanceChangePerReply, Math.Min(cfg.MaxRomanceChangePerReply, reply.RomanceChange));
                rd = rd != 0 ? Bonds.ChangeRomance(_s, rd) : 0;
                if (rd != 0)
                {
                    Log.Info($"Romance with {_s.Name}: {(rd > 0 ? "+" : "")}{rd} (now {Mem.Romance})");
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
                    Log.Info($"Relationship with {_s.Name}: {(delta > 0 ? "+" : "")}{delta}");
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
                    Log.Info($"Rumor from the talk with {_s.Name}, updated: {text}");
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
                    WorldSim.OnConversation($"A conversation between {Hero.MainHero.Name} and {_s.Name}. What could get about from it: {reply.RumorText}", _s.Settlement);
            }
            return shown;
        }

        /// <summary>A bandit leader who ambushed the player ends the parley without letting them pass: it is a fight.</summary>
        private void BandEnd(bool ends, bool letPass, List<string> shown)
        {
            try
            {
                if (ends && _s.Band != null && !letPass && !Combat.KeepEncounter && Combat.EnemyOnMap(_s)
                    && PlayerEncounter.Current != null && PlayerEncounter.PlayerIsDefender)
                {
                    Combat.QueueMapAttack(_s);
                    shown.Add(L.F("AIL_53ea433d", $"{_s.Name} da la charla por terminada y se lanza al ataque."));
                }
            }
            catch (Exception ex) { Log.Error("End of the parley with a band", ex); }
        }

        // ====================================================== actions that wait for the player
        // Leaving, fighting, surrendering, going somewhere together or joining: the reply is read (and heard) first,
        // the player presses Continue, and only then does it happen. Someone who leaves really walks off.

        private static readonly HashSet<string> HeldNames = new HashSet<string>
        {
            "leave", "brawl", "duel", "duel_to_death", "attack", "surrender", "accept_surrender", "let_pass", "call_guards", "arrest", "die",
            "join_party", "join_with_band", "join_their_company", "come_along", "go_into",
            // the wedding is held once the talk is over (AIInfluence: "the wedding will take place after the conversation")
            "accept_marriage",
            // scene actions that take them out of the talk or bring trouble on: after the last word, like leaving
            "guide", "go_talk_to", "go_with", "call_help", "detain",
        };

        // the names models also use for those
        private static readonly Dictionary<string, string> HeldAliases = new Dictionary<string, string>
        {
            { "end_conversation", "leave" }, { "goodbye", "leave" }, { "fight", "brawl" }, { "fist_fight", "brawl" },
            { "let_them_pass", "let_pass" }, { "let_them_go", "let_pass" }, { "recruit", "join_party" }, { "join", "join_party" },
            { "lead_to", "guide" }, { "take_to", "guide" }, { "escort_to", "guide" }, { "go_talk", "go_talk_to" }, { "talk_to", "go_talk_to" },
            { "shout_for_help", "call_help" }, { "help", "call_help" }, { "arrest_other", "detain" },
            { "marry", "accept_marriage" }, { "friendly_duel", "duel" }, { "hostile_duel", "duel_to_death" },
        };

        /// <summary>
        /// What the player is told while the reply waits for Continue, as AIInfluence words each case (the wedding,
        /// a surrender either way, a death). Anything else gets the plain line.
        /// </summary>
        private string HeldNotice()
        {
            var names = (_held ?? new List<string>()).Select(HeldName).ToList();
            if (names.Contains("die")) return L.F("AIL_e7b1d60d", $"{_s.Name} vive sus últimos instantes… (termina de leer o de escuchar y pulsa «Continuar»)");
            if (names.Contains("accept_marriage")) return L.T("AIL_e7b1d60e", "La boda será al terminar la conversación… (termina de leer o de escuchar y pulsa «Continuar»)");
            if (names.Contains("accept_surrender")) return L.F("AIL_e7b1d60f", $"Te has rendido a {_s.Name}… (termina de leer o de escuchar y pulsa «Continuar»)");
            if (names.Contains("surrender")) return L.F("AIL_e7b1d610", $"{_s.Name} se dispone a rendirse… (termina de leer o de escuchar y pulsa «Continuar»)");
            return L.T("AIL_e7b1ca03", "(Termina de leer o de escuchar y pulsa «Continuar»)");
        }

        // Someone who died in the talk falls in front of the player once the game has closed the conversation
        private static TaleWorlds.MountAndBlade.Agent _strikeAgent;
        private static bool _strikeByPlayer;
        private static DateTime _strikeAt;

        /// <summary>
        /// A death in a scene is seen: once the conversation is closed, the character's body takes a killing blow, from
        /// the player when they did it (AIInfluence kills the agent the same way).
        /// </summary>
        public static void StrikeAfterTalk(TaleWorlds.MountAndBlade.Agent victim, bool byPlayer)
        {
            if (victim == null) return;
            _strikeAgent = victim; _strikeByPlayer = byPlayer; _strikeAt = DateTime.UtcNow.AddMilliseconds(300);
        }

        private static string HeldName(string command)
        {
            string n = NpcReply.ActionName(command);
            return HeldAliases.TryGetValue(n, out var real) ? real : n;
        }

        private List<string> _held;           // null = nothing waiting
        private bool _heldEnds, _heldLeave;
        private bool _continueWhenSilent;
        private bool _notesOnly;
        private TaleWorlds.MountAndBlade.Agent _leaveAgent;

        /// <summary>The last reply waits for the player's Continue before its actions happen.</summary>
        public bool Holding => _held != null && !_closed;

        /// <summary>The voice is still reading the line out.</summary>
        private static bool Speaking { get { try { return AILegacy.Voice.Speech.IsSpeaking; } catch { return false; } } }

        /// <summary>
        /// "Continue" in the dialogue box: carries out what was held, unless the voice is still speaking, in which case
        /// it is done by itself as soon as the voice ends (ContinueIfSilent).
        /// </summary>
        public void Continue()
        {
            if (!Holding) return;
            if (Speaking) { _continueWhenSilent = true; return; }
            Release();
        }

        /// <summary>Text while Continue waits for the voice to end.</summary>
        public string WaitVoiceText()
        {
            _voiceWaitShown = true;
            return Sanitize(L.F("AIL_e7b1ca05", $"*{_s.Name} termina de hablar…*"));
        }

        private void ContinueIfSilent()
        {
            if (!_continueWhenSilent || _closed || Speaking) return;
            if (UseWindow) { OnCloseClicked(); return; }
            if (!_dialogMode) return;
            var cm = Campaign.Current?.ConversationManager;
            if (cm == null || !cm.IsConversationInProgress || cm.CurOptions == null) return;
            // only while the box sits on its Continue: elsewhere it waits for the player
            if (cm.CurOptions.Any(o => o.Id == "ailegacy_d_cont")) { _continueWhenSilent = false; cm.DoOption("ailegacy_d_cont"); }
        }

        /// <summary>Carries out the held actions now and says what came of them.</summary>
        private void Release()
        {
            var list = _held;
            if (list == null) return;
            _held = null;
            _continueWhenSilent = false;
            _voiceWaitShown = false;
            var shown = new List<string>();
            var ctx = new NpcActions.ActionContext();
            try
            {
                foreach (var msg in NpcActions.Execute(_s, list, ctx))
                {
                    if (!_dialogMode) Notify(msg, Colors.Yellow);
                    shown.Add(msg);
                }
                ApplyDeltas(ctx, shown);
            }
            catch (Exception ex) { Log.Error("Actions held until the player continued", ex); }
            bool ends = _heldEnds || ctx.EndConversation;
            BandEnd(ends, ctx.LetPass, shown);
            if (ends)
            {
                _npcEnded = true;
                Log.Info($"{_s.Name} ends the conversation");
                // only someone simply leaving walks off: a fight, a surrender or an arrest is handled by those
                if (_heldLeave && !ctx.Executed.Any(n => n != "leave")) _leaveAgent = SceneAgent();
            }
            if (UseWindow)
            {
                foreach (var e in shown) _win.VM.AddSystem("» " + e);
                if (!ends) { _win.VM.CanType = true; _win.VM.CloseText = L.T("AIL_e5c9b3a5", "Terminar"); }
                RefreshSubtitle();
            }
            else if (_dialogMode)
            {
                _dialogNotes.AddRange(shown);
                if (ends) _dialogNotes.Add(L.F("AIL_42b37383", $"({_s.Name} da la conversación por terminada)"));
                HasUnreadReply = true;
                _notesOnly = true;
                _lookShown = true;      // the reply's face was already played; these are only the notes
            }
            SaveFiles.Flush();
        }

        /// <summary>Trust, tension and relation moved by held actions (calling the guards, say).</summary>
        private void ApplyDeltas(NpcActions.ActionContext ctx, List<string> shown)
        {
            var cfg = S.Get;
            if (cfg.EnableTrust && ctx.TrustDelta != 0)
            {
                int td = Bonds.ChangeTrust(_s, ctx.TrustDelta);
                if (td < 0) _trustLostThisTalk -= td;
                if (td != 0) shown.Add(L.F("AIL_c78d48c5", $"Confianza {(td > 0 ? "+" : "")}{td}"));
            }
            if (cfg.EnableTension && ctx.TensionDelta != 0)
            {
                int xd = Bonds.ChangeTension(_s, ctx.TensionDelta);
                if (xd != 0) shown.Add(L.F("AIL_23b67cad", $"Tensión {(xd > 0 ? "+" : "")}{xd}"));
            }
            if (_s.IsHero && ctx.RelationDelta != 0)
            {
                int delta = Math.Max(-cfg.MaxRelationChangePerReply, Math.Min(cfg.MaxRelationChangePerReply, ctx.RelationDelta));
                int budget = cfg.MaxRelationChangePerConversation - _relationMovedThisTalk;
                if (Math.Abs(delta) > budget) delta = Math.Sign(delta) * Math.Max(0, budget);
                if (delta != 0)
                {
                    ChangeRelationAction.ApplyPlayerRelation(_s.Hero, delta, affectRelatives: false, showQuickNotification: true);
                    _relationChangedThisTalk += delta;
                    _relationMovedThisTalk += Math.Abs(delta);
                    Mem.RelationChangedTotal += delta;
                    shown.Add(L.F("AIL_a190aec6", $"Relación {(delta > 0 ? "+" : "")}{delta}"));
                }
            }
        }

        /// <summary>Our window's close button (Continue while something is held), and the fallback popup's End.</summary>
        private void OnCloseClicked()
        {
            if (_closed) return;
            if (Holding)
            {
                if (Speaking)
                {
                    if (!_continueWhenSilent && UseWindow) _win.VM.AddSystem(L.F("AIL_e7b1ca05", $"*{_s.Name} termina de hablar…*"));
                    _continueWhenSilent = true;
                    return;
                }
                Release();
                if (!_npcEnded) return;      // something happened but the talk goes on
            }
            Close(_npcEnded);
        }

        /// <summary>No reply came: the character is lost for words, and the player can try again.</summary>
        private void NoReply()
        {
            string msg = L.F("AIL_e7b1ca06", $"{_s.Name} se queda en silencio, con la mente en otra parte. Inténtalo luego.");
            if (_dialogMode)
            {
                _lastNpc = "(" + msg + ")";
                _npcInTalk = false;
                _emotion = "thoughtful"; _gesture = ""; _lookShown = false;
                HasUnreadReply = true;
                if (_livePainted) PaintBox(Compose(_lastNpc));
                if (S.Get.AutoShowReply) MainThread.Post(AutoShow);
                return;
            }
            if (UseWindow) { _win.VM.AddSystem(msg); return; }
            InformationManager.ShowInquiry(new InquiryData(_s.Name, msg, true, false, L.T("AIL_1df7d173", "Volver"), "", () => MainThread.Post(ShowLegacyInput), null), true);
        }

        /// <summary>
        /// Options button: gets the player out of a talk that hangs (our window or session, the game's conversation,
        /// a map encounter left open, a character still on their way to talk).
        /// </summary>
        public static void ForceClose()
        {
            try { AILegacy.Voice.Speech.Stop(); } catch { }
            try { var a = Active; if (a != null) { a._held = null; a._continueWhenSilent = false; a.Close(false); } } catch (Exception ex) { Log.Error("Force close: session", ex); }
            try { ChatWindow.Current?.Close(); } catch (Exception ex) { Log.Error("Force close: window", ex); }
            try { Initiative.Cancel(); } catch { }
            _clipAgent = null; _walkOffAgent = null; _strikeAgent = null;
            try
            {
                var cm = Campaign.Current?.ConversationManager;
                if (cm != null && cm.IsConversationInProgress) cm.EndConversation();
            }
            catch (Exception ex) { Log.Error("Force close: game conversation", ex); }
            try
            {
                // An encounter on the open map with no battle going on is what keeps the attack/leave menu stuck
                if (TaleWorlds.MountAndBlade.Mission.Current == null && PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement
                    && PlayerEncounter.Battle == null && TaleWorlds.CampaignSystem.Party.MobileParty.MainParty?.MapEvent == null)
                    PlayerEncounter.Finish(true);
            }
            catch (Exception ex) { Log.Error("Force close: encounter", ex); }
            Log.Info("Dialogue force-closed from the options");
            Notify(L.T("AIL_e7b1ca07", "AI Legacy: diálogo cerrado a la fuerza."), Colors.Gray);
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
                    Log.Info($"Instruction for {_s.Name}: {arg}");
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
                    // the player aborted the talk: whatever the last reply held back is dropped, not carried out
                    Close(false, discardHeld: true);
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
            Log.Info($"{who} remembers: {note}");
        }

        // ============================================================ closing

        /// <summary>Ends the current talk as if the character had ended it (after accepting a duel, a surrender...).</summary>
        public static void EndFromOutside()
        {
            try { var a = Active; if (a != null && !a._closed) { a._npcEnded = true; a.Close(true); } }
            catch (Exception ex) { Log.Error("Close the talk", ex); }
        }

        /// <param name="discardHeld">The player aborted the talk (/forget): held actions are dropped instead of carried out.</param>
        private void Close(bool npcEnded, bool discardHeld = false)
        {
            if (_closed) return;
            if (_held != null && discardHeld)
            {
                Log.Info($"{_s.Name}: held actions discarded because the talk was aborted ({(_held.Count > 0 ? string.Join(", ", _held) : "end of the talk")})");
                _held = null;
                _continueWhenSilent = false;
                _leaveAgent = null;
            }
            // the talk closed some other way while something was still held: it is not lost
            if (_held != null)
            {
                try { Release(); } catch (Exception ex) { Log.Error("Held actions on close", ex); }
                npcEnded = npcEnded || _npcEnded;
            }
            _closed = true;
            _prepGen++; _preparing = false;     // a voice still being made for this talk is no longer for anyone
            if (_leaveAgent != null) { _walkOffAgent = _leaveAgent; _walkOffAt = DateTime.UtcNow.AddMilliseconds(400); _leaveAgent = null; }
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
            Log.Info($"Conversation with {_s.Name} closed ({_lines} lines, relationship {(_relationChangedThisTalk >= 0 ? "+" : "")}{_relationChangedThisTalk})");

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
                catch (Exception ex) { Log.Error("Could not close the game conversation", ex); }
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
