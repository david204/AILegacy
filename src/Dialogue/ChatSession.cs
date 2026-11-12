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
    /// Una conversación libre. Usa la ventana de chat propia (ChatWindow); si no se pudiera abrir,
    /// cae al modo antiguo de ventanas de texto del juego.
    /// </summary>
    internal class ChatSession
    {
        public static ChatSession Active { get; private set; }

        private readonly Speaker _s;
        private HeroMemory Mem => _s.Memory;
        private int _relationChangedThisTalk;
        private bool _waiting;
        private bool _closed;
        private bool _npcEnded;
        private int _lines;
        private ChatWindow _win;
        private readonly List<string> _transcript = new List<string>(); // solo modo antiguo

        private ChatSession(Speaker s) { _s = s; }

        public static bool IsBusy => Active != null && !Active._closed;

        public static void Start(Speaker s)
        {
            if (s == null) return;
            if (IsBusy) return;

            if (!LegacyConfig.Current.Providers.Any(p => p.IsUsable))
            {
                Notify("AI Legacy: " + (LegacyConfig.LastLoadError ?? L.T("no hay ninguna api_key configurada en Modules\\AILegacy\\config.json")), Colors.Red);
                return;
            }

            var session = new ChatSession(s);
            Active = session;
            Log.Info($"Conversación iniciada con {s.Name} ({s.Key})");

            var vm = new ChatVM(s.Name, session.OnPlayerSaid, () => session.Close(session._npcEnded));
            session._win = ChatWindow.Open(vm);
            if (session._win != null)
            {
                session.RefreshSubtitle();
                vm.WaitingText = L.F($"{s.Name} está pensando...");
                vm.AddSystem(session.Mem.ConversationCount > 0
                    ? L.F($"{s.Name} te reconoce. Ya habéis hablado {session.Mem.ConversationCount} {(session.Mem.ConversationCount == 1 ? L.T("vez") : L.T("veces"))}.  (Escribe /ayuda para ver los comandos)")
                    : L.F($"{s.Name} espera a que hables.  (Escribe /ayuda para ver los comandos)"));
            }
            else
            {
                session.ShowLegacyInput();
            }
        }

        public static void TickUI() => ChatWindow.Current?.Tick();

        // ====================================================== modo diálogo del juego
        // La charla vive en el cuadro de diálogo nativo: escribir -> "(espera)" -> "Ver su respuesta".

        private bool _dialogMode;
        private string _lastPlayer = "", _lastNpc = "";
        private readonly List<string> _dialogNotes = new List<string>();
        public bool HasUnreadReply { get; private set; }
        public bool Seeded { get; private set; }

        /// <summary>El NPC habla primero (iniciativa): su frase ya está escrita y es lo primero que enseña el cuadro.</summary>
        public void Seed(string npcText)
        {
            _lastPlayer = "";
            _lastNpc = npcText ?? "";
            HasUnreadReply = true;
            Seeded = true;
            _lines++;
        }
        public bool IsWaiting => _waiting;
        public bool Ended => _npcEnded;
        public Speaker Who => _s;

        /// <summary>Sesión ligada a la conversación del juego en curso (se cierra sola al acabar la conversación).</summary>
        public static ChatSession StartDialog(Speaker s)
        {
            if (s == null) return null;
            if (IsBusy && Active._dialogMode && Active._s.Key == s.Key) return Active;
            if (IsBusy) Active.Close(false);

            if (!LegacyConfig.Current.Providers.Any(p => p.IsUsable))
            {
                Notify("AI Legacy: " + (LegacyConfig.LastLoadError ?? L.T("no hay ninguna api_key configurada en Modules\\AILegacy\\config.json")), Colors.Red);
                return null;
            }
            var session = new ChatSession(s) { _dialogMode = true };
            Active = session;
            Log.Info($"Conversación (diálogo) iniciada con {s.Name} ({s.Key})");
            try { Campaign.Current.ConversationManager.ConversationEndOneShot += () => session.Close(false); }
            catch (Exception ex) { Log.Error("No pude engancharme al final de la conversación", ex); }
            return session;
        }

        /// <summary>Abre la caja para escribir el mensaje (modo diálogo).</summary>
        public void AskInput()
        {
            if (_closed || _waiting || _npcEnded) return;
            string last = Mem.LastConversationDay < 0 ? "Nunca"
                : (int)(CampaignTime.Now.ToDays - Mem.LastConversationDay) <= 0 ? L.T("Hoy")
                : L.F($"Hace {(int)(CampaignTime.Now.ToDays - Mem.LastConversationDay)} días");
            var lastNpc = Mem.Turns.LastOrDefault(t => t.Role == "npc")?.Text;
            string body = string.IsNullOrWhiteSpace(lastNpc)
                ? L.F($"Última vez que hablasteis: {last}.")
                : L.F($"Último mensaje de {_s.Name}:\n{(lastNpc.Length > 700 ? lastNpc.Substring(0, 700) + "…" : lastNpc)}");
            InformationManager.ShowTextInquiry(new TextInquiryData(
                L.F($"Tu mensaje para {_s.Name}"), body + L.T("\n\n(/ayuda para ver los comandos)"),
                true, true, "Confirmar", "Cancelar",
                t => OnPlayerSaid(t), null, false,
                t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("Escribe algo"))), true);
        }

        /// <summary>Texto que enseña el cuadro de diálogo del NPC ahora mismo (marca la respuesta como leída).</summary>
        public string ConsumeDialogText()
        {
            if (_waiting) return L.F($"*{_s.Name} todavía está pensando…*");
            if (!HasUnreadReply && _lastNpc.Length == 0 && _dialogNotes.Count == 0) return L.T("(Envía tu mensaje y espera su respuesta)");
            HasUnreadReply = false;
            var sb = new StringBuilder();
            if (_lastPlayer.Length > 0) sb.Append(L.T("Tú: ")).Append(_lastPlayer).Append("{newline}{newline}");
            if (_lastNpc.Length > 0) sb.Append(_lastNpc.StartsWith("(") ? "" : _s.Name + ": ").Append(_lastNpc);
            foreach (var n in _dialogNotes) sb.Append("{newline}» ").Append(n);
            _dialogNotes.Clear();
            return Sanitize(sb.ToString());
        }

        public string WaitText => _waiting || _lastNpc.Length == 0 ? L.T("(Envía tu mensaje y espera su respuesta)") : Sanitize(_lastNpc);

        private static string Sanitize(string s) =>
            (s ?? "").Replace("{newline}", "\u0001").Replace("{", "(").Replace("}", ")").Replace("\u0001", "{newline}").Replace("\r", "").Replace("\n", "{newline}");

        // =============================================================== UI

        private bool UseWindow => _win != null;

        private void RefreshSubtitle()
        {
            if (!UseWindow) return;
            var parts = new List<string>();
            if (_s.IsHero)
            {
                int rel = (int)_s.Hero.GetRelationWithPlayer();
                parts.Add(L.F($"Relación: {rel:+0;-0;0}"));
                if (_s.Hero.Clan != null) parts.Add(_s.Hero.Clan.Name.ToString());
            }
            else if (_s.Settlement != null) parts.Add(_s.Settlement.Name.ToString());
            if (!string.IsNullOrWhiteSpace(Mem.Mood)) parts.Add(L.T("Ánimo: ") + Mem.Mood);
            _win.VM.Subtitle = string.Join("   ·   ", parts);
            _win.VM.Title = _s.Name;
        }

        private void UiPlayer(string text)
        {
            if (_dialogMode) { _lastPlayer = text; _lastNpc = ""; return; }
            if (UseWindow) _win.VM.AddLine(L.T("Tú"), text, ChatVM.PlayerColor);
            else _transcript.Add(L.F($"Tú: {text}"));
        }

        private void UiNpc(string text)
        {
            if (_dialogMode)
            {
                _lastNpc = text;
                HasUnreadReply = true;
                Notify(L.F($"{_s.Name} te contesta."), Colors.Yellow);
                return;
            }
            if (UseWindow) _win.VM.AddLine(_s.Name, text, ChatVM.NpcColor);
            else _transcript.Add($"{_s.Name}: {text}");
        }

        private void UiSystem(string text, bool error = false)
        {
            if (_dialogMode)
            {
                if (error) { _lastNpc = "(" + text + ")"; HasUnreadReply = true; Notify(text.Split('\n')[0], Colors.Red); }
                else { _dialogNotes.Add(text); HasUnreadReply = true; Notify(L.T("AI Legacy: comando aplicado. Pulsa «Ver su respuesta»."), Colors.Gray); }
                return;
            }
            if (UseWindow) { if (error) _win.VM.AddError(text); else _win.VM.AddSystem(text); }
            else InformationManager.ShowInquiry(new InquiryData(error ? "AI Legacy" : _s.Name, text, true, false, L.T("Volver"), "", () => MainThread.Post(ShowLegacyInput), null), true);
        }

        private void UiWaiting(bool waiting)
        {
            if (_dialogMode) return;
            if (UseWindow) _win.VM.IsWaiting = waiting;
            else if (waiting) Notify(L.F($"{_s.Name} está pensando..."), Colors.Gray);
        }

        private void UiReady()
        {
            if (_dialogMode) return;
            if (UseWindow) RefreshSubtitle();
            else ShowLegacyInput();
        }

        // -------- modo antiguo (ventanas de texto del juego)

        private void ShowLegacyInput()
        {
            if (_closed) return;
            var body = new StringBuilder();
            if (_transcript.Count == 0)
                body.Append(Mem.ConversationCount > 0 ? L.F($"{_s.Name} te reconoce.") : L.F($"{_s.Name} espera a que hables."));
            else
                foreach (var line in _transcript.Skip(Math.Max(0, _transcript.Count - 2)))
                    body.AppendLine(line.Length > 600 ? line.Substring(0, 600) + "…" : line).AppendLine();

            InformationManager.ShowTextInquiry(new TextInquiryData(
                _s.Name, body.ToString().TrimEnd(), true, true, L.T("Decir"), "Terminar",
                OnPlayerSaid, () => Close(_npcEnded), false,
                t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("Escribe algo"))), true);
        }

        // ============================================================ lógica

        private void OnPlayerSaid(string text)
        {
            if (_closed || _waiting) return;
            text = (text ?? "").Trim();
            if (text.Length == 0) { if (!UseWindow && !_dialogMode) MainThread.Post(ShowLegacyInput); return; }
            if (_npcEnded) return;
            if (text.StartsWith("/")) { HandleCommand(text); return; }

            Mem.Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays });
            UiPlayer(text);
            Ambient.Overhear(_s, text);
            _lines++;

            List<ChatMessage> messages;
            try
            {
                messages = PromptBuilder.BuildDialogue(_s, S.Get.MaxRelationChangePerConversation - Math.Abs(_relationChangedThisTalk));
            }
            catch (Exception ex)
            {
                Log.Error("Error construyendo el prompt", ex);
                RemoveLastPlayerTurn(text);
                UiSystem(L.T("Error interno construyendo el contexto: ") + ex.Message, true);
                return;
            }

            _waiting = true;
            UiWaiting(true);

            // La primera respuesta trae además la ficha de carácter (12 campos): necesita más sitio para no quedar cortada
            bool firstTime = Mem.Profile == null || Mem.Profile.IsEmpty;
            int? tokens = firstTime ? Math.Max(S.Get.MaxTokens, 1800) : (int?)null;
            int session = World.WorldSim.Session;
            LlmClient.CompleteAsync(messages, wantJson: true, maxTokensOverride: tokens, preferredProvider: _s.IsHero ? S.Get.ProviderHeroes : S.Get.ProviderResidents).ContinueWith(task =>
            {
                var res = task.IsFaulted ? new LlmResult { Ok = false, Error = task.Exception?.GetBaseException().Message } : task.Result;
                MainThread.Post(() =>
                {
                    // Si mientras tanto has cargado otra partida, esa respuesta ya no es de aquí: ni se aplica ni se guarda
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
            if (UseWindow && string.IsNullOrEmpty(_win.VM.InputText)) _win.VM.InputText = text; // para reintentar con Enter
        }

        private void OnNpcAnswered(LlmResult res, string playerText)
        {
            _waiting = false;
            UiWaiting(false);
            if (_closed) return;

            if (!res.Ok)
            {
                RemoveLastPlayerTurn(playerText);
                Log.Error("Sin respuesta: " + res.Error);
                UiSystem(L.T("No ha llegado respuesta de ninguna IA. Pulsa Decir para reintentar.\n") + res.Error, true);
                return;
            }

            var reply = NpcReply.Parse(res.Text);
            Mem.LastAiResponse = res.Text != null && res.Text.Length > 4000 ? res.Text.Substring(0, 4000) : res.Text;
            if (_s.IsHero) Mem.Snapshot = ContextBuilder.Snapshot(_s.Hero);
            if (string.IsNullOrWhiteSpace(reply.Reply)) reply.Reply = L.T("*te mira en silencio*");

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

            Mem.Turns.Add(new ChatTurn { Role = "npc", Text = reply.Reply, Day = CampaignTime.Now.ToDays });
            UiNpc(reply.Reply);
            _lines++;

            List<string> effects = new List<string>();
            try { effects = ApplyConsequences(reply); }
            catch (Exception ex) { Log.Error("Error aplicando consecuencias", ex); }
            foreach (var e in effects) { if (UseWindow) _win.VM.AddSystem("» " + e); else if (_dialogMode) _dialogNotes.Add(e); }

            SaveFiles.Flush();   // lo dicho queda escrito ya, se guarde o no la partida

            if (reply.EndConversation)
            {
                _npcEnded = true;
                Log.Info($"{_s.Name} termina la conversación");
                if (UseWindow)
                {
                    _win.VM.AddSystem(L.F($"({_s.Name} da la conversación por terminada)"));
                    _win.VM.CanType = false;
                    _win.VM.CloseText = L.T("Continuar");
                    RefreshSubtitle();
                }
                else if (_dialogMode)
                {
                    _dialogNotes.Add(L.F($"({_s.Name} da la conversación por terminada)"));
                }
                else
                {
                    InformationManager.ShowInquiry(new InquiryData(_s.Name, reply.Reply + "\n\n(" + _s.Name + L.T(" da la conversación por terminada)"),
                        true, false, L.T("Continuar"), "", () => Close(true), null), true);
                }
                return;
            }
            UiReady();
        }

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
            }

            if (!string.IsNullOrWhiteSpace(reply.PlayerIntroducedAs) && reply.PlayerIntroducedAs.Length <= 60
                && !string.Equals(Mem.KnownPlayerName, reply.PlayerIntroducedAs, StringComparison.OrdinalIgnoreCase))
            {
                Mem.KnownPlayerName = reply.PlayerIntroducedAs;
                Log.Info($"{_s.Name} ahora te conoce como \"{reply.PlayerIntroducedAs}\"");
            }

            var actx = new NpcActions.ActionContext();
            foreach (var msg in NpcActions.Execute(_s, reply.Actions, actx))
            {
                Notify(msg, Colors.Yellow);
                shown.Add(msg);
            }
            if (actx.EndConversation) reply.EndConversation = true;

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
                if (td != 0)
                {
                    Log.Info($"Confianza de {_s.Name}: {(td > 0 ? "+" : "")}{td} (ahora {Mem.Trust})");
                    shown.Add(L.F($"Confianza {(td > 0 ? "+" : "")}{td}"));
                }
            }
            if (cfg.EnableTension)
            {
                int xd = Math.Max(-cfg.MaxTensionChangePerReply, Math.Min(cfg.MaxTensionChangePerReply, reply.TensionChange)) + actx.TensionDelta;
                xd = xd != 0 ? Bonds.ChangeTension(_s, xd) : 0;
                if (xd != 0)
                {
                    Log.Info($"Tensión con {_s.Name}: {(xd > 0 ? "+" : "")}{xd} (ahora {Mem.Tension})");
                    shown.Add(L.F($"Tensión {(xd > 0 ? "+" : "")}{xd}"));
                }
            }
            if (Bonds.RomancePossible(_s))
            {
                int rd = Math.Max(-cfg.MaxRomanceChangePerReply, Math.Min(cfg.MaxRomanceChangePerReply, reply.RomanceChange));
                rd = rd != 0 ? Bonds.ChangeRomance(_s, rd) : 0;
                if (rd != 0)
                {
                    Log.Info($"Romance con {_s.Name}: {(rd > 0 ? "+" : "")}{rd} (ahora {Mem.Romance})");
                    shown.Add(L.F($"Romance {(rd > 0 ? "+" : "")}{rd}"));
                }
            }

            if (_s.IsHero)
            {
                int delta = Math.Max(-cfg.MaxRelationChangePerReply, Math.Min(cfg.MaxRelationChangePerReply, reply.RelationChange + actx.RelationDelta));
                int budget = cfg.MaxRelationChangePerConversation - Math.Abs(_relationChangedThisTalk);
                if (Math.Abs(delta) > budget) delta = Math.Sign(delta) * Math.Max(0, budget);
                if (delta != 0)
                {
                    ChangeRelationAction.ApplyPlayerRelation(_s.Hero, delta, affectRelatives: false, showQuickNotification: true);
                    _relationChangedThisTalk += delta;
                    Mem.RelationChangedTotal += delta;
                    Log.Info($"Relación con {_s.Name}: {(delta > 0 ? "+" : "")}{delta}");
                    shown.Add(L.F($"Relación {(delta > 0 ? "+" : "")}{delta}"));
                }
            }

            AddNote(Mem, reply.Remember, cfg.MaxNotesPerHero, _s.Name);

            if (cfg.EnableRumors && !string.IsNullOrWhiteSpace(reply.RumorText) && reply.RumorText.Length > 10)
            {
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor",
                    Importance = Math.Max(1, Math.Min(3, reply.RumorImportance)),
                    Text = reply.RumorText.Length > 300 ? reply.RumorText.Substring(0, 300) : reply.RumorText,
                    TitleEs = reply.RumorTitleLocal,
                    TextEs = reply.RumorTextLocal,
                    SettlementId = _s.Settlement?.StringId,
                    Kingdoms = _s.Faction != null ? new List<string> { _s.Faction.StringId } : new List<string>(),
                    Heroes = _s.IsHero ? new List<string> { _s.Hero.StringId, Hero.MainHero.StringId } : new List<string> { Hero.MainHero.StringId },
                });
                // Un rumor de peso echa a rodar su propio suceso en el mundo
                if (reply.RumorImportance >= 2 && _s.IsHero)
                    WorldSim.OnIncident($"A rumour has started to spread, first told by {_s.Name}: {reply.RumorText}", _s.Settlement, false);
            }
            return shown;
        }

        // ========================================================== comandos

        private void HandleCommand(string text)
        {
            int sp = text.IndexOf(' ');
            var cmd = (sp > 0 ? text.Substring(0, sp) : text).ToLowerInvariant();
            var arg = sp > 0 ? text.Substring(sp + 1).Trim() : "";

            switch (cmd)
            {
                case "/nota": case "/note":
                case "/instruccion": case "/instruction":
                    if (arg.Length == 0) { UiSystem(L.T("Uso: /nota <instrucción>   Ej: /nota Es mi esposa y está embarazada; nunca me sermonea sobre el peligro.")); return; }
                    Mem.CustomInstructions.Add(arg.Length > 600 ? arg.Substring(0, 600) : arg);
                    Log.Info($"Instrucción para {_s.Name}: {arg}");
                    SaveFiles.Flush();
                    UiSystem(L.F($"Instrucción guardada para {_s.Name}."));
                    return;

                case "/borrarnotas": case "/clearnotes":
                    Mem.CustomInstructions.Clear();
                    SaveFiles.Flush();
                    UiSystem(L.F($"Instrucciones de {_s.Name} borradas."));
                    return;

                case "/memoria": case "/memory":
                {
                    var b = new StringBuilder();
                    b.AppendLine(L.F($"Te conoce como: {ContextBuilder.PlayerNameFor(_s) ?? L.T("(desconocido)")}   ·   Conversaciones: {Mem.ConversationCount}   ·   Mentiras pilladas: {Mem.LiesSuspected}"));
                    if (!string.IsNullOrWhiteSpace(Mem.Mood)) b.AppendLine(L.F($"Ánimo: {Mem.Mood} ({Mem.MoodReason})"));
                    if (S.Get.EnableTension) b.AppendLine(L.F($"Tensión: {Bonds.Tension(Mem)}"));
                    if (S.Get.EnableTrust) { Bonds.EnsureTrust(_s); b.AppendLine(L.F($"Confianza: {Mem.Trust}") + (Bonds.RomancePossible(_s) ? L.F($"   ·   Romance: {Mem.Romance}") : "")); }
                    if (_s.IsHero && OrderSystem.Find(_s.Hero) is PartyOrder po) b.AppendLine(L.F($"Orden en curso: {po.Type} {po.TargetName} (hasta el día {po.UntilDay:0})"));
                    if (Mem.Profile != null && !Mem.Profile.IsEmpty) b.AppendLine("Personalidad: " + Mem.Profile.Personality).AppendLine("Secreto: " + Mem.Profile.Secret);
                    if (!string.IsNullOrWhiteSpace(Mem.Summary)) b.AppendLine("Resumen: " + Mem.Summary);
                    foreach (var n in Mem.Notes) b.AppendLine(L.T("· Recuerda: ") + n);
                    foreach (var n in Mem.CustomInstructions) b.AppendLine(L.T("· Tu instrucción: ") + n);
                    if (Mem.ImportedFrom != null) b.AppendLine(L.T("Importado de AIInfluence: ") + Mem.ImportedFrom);
                    UiSystem(b.ToString().TrimEnd());
                    return;
                }

                case "/olvidar": case "/forget":
                    if (!string.Equals(arg, "confirmar", StringComparison.OrdinalIgnoreCase))
                    {
                        UiSystem(L.F($"Esto borra TODA la memoria de {_s.Name} sobre ti. Para hacerlo escribe: /olvidar confirmar"));
                        return;
                    }
                    MemoryStore.Remove(_s.Key);
                    Notify(L.F($"{_s.Name} lo ha olvidado todo."), Colors.Green);
                    Close(false);
                    return;

                default:
                    UiSystem(L.T("Comandos:  /nota <texto> (instrucción permanente)  ·  /borrarnotas  ·  /memoria (qué recuerda)  ·  /olvidar  ·  /ayuda"));
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

        // ============================================================ cierre

        private void Close(bool npcEnded)
        {
            if (_closed) return;
            _closed = true;
            if (Active == this) Active = null;
            _win?.Close();
            _win = null;

            if (_lines > 0)
            {
                Mem.ConversationCount++;
                Mem.LastConversationDay = CampaignTime.Now.ToDays;
                if (_s.IsHero) QuestObjectives.OnTalked(_s.Hero);
                Bonds.Touch(Mem);
            }
            Log.Info($"Conversación con {_s.Name} cerrada ({_lines} líneas, relación {(_relationChangedThisTalk >= 0 ? "+" : "")}{_relationChangedThisTalk})");

            MemorySummarizer.MaybeSummarize(_s);
            SaveFiles.Flush();

            if (npcEnded)
            {
                try
                {
                    // En el mapa, hablar con un grupo es un "encuentro": hay que marcar que nos vamos,
                    // como hace el juego en su despedida; si no, sale el menú de atacar / irse.
                    if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement && !Combat.KeepEncounter)
                        PlayerEncounter.LeaveEncounter = true;
                    if (Campaign.Current?.ConversationManager != null && Campaign.Current.ConversationManager.IsConversationInProgress)
                        Campaign.Current.ConversationManager.EndConversation();
                }
                catch (Exception ex) { Log.Error("No se pudo cerrar la conversación del juego", ex); }
            }
        }

        public static void Notify(string msg, Color color) => InformationManager.DisplayMessage(new InformationMessage(msg, color));
    }
}
