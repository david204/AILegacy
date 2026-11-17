using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using SandBox.Conversation.MissionLogics;
using SandBox.Missions.MissionLogics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Conversation;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// An NPC starts talking to you on their own. The AI is asked for their opening line first.
    /// Once it arrives, the game conversation opens and that line is the first thing they say.
    ///  - In a scene (Start): the guard after a fight, or any NPC that should walk up.
    ///  - On the map (StartMap): a noble who knows you rides up and asks to talk.
    /// </summary>
    internal static class Initiative
    {
        private class Approach
        {
            public Speaker Who;
            public Agent Agent;           // scene
            public Hero Hero;             // map
            public string Reply;          // null = AI still thinking
            public string Emotion, Gesture;   // how they look and move while saying it
            public bool Failed;
            public bool Asked;            // map: player was already asked whether to receive them
            public bool Hostile;          // map: an enemy caught up and blocks your way, no asking
            public bool InParty;          // map: in the player's own party
            public bool InScene;          // map: the talk opens in the scene on foot (it waits for the scene)
            public float SceneAge;
            public bool Captor;           // map: holds the player prisoner and comes to see them
            public float Age;
            public Action OnUnanswered;   // player left or the conversation couldn't open
            public bool Escorted;         // map: rode over under an "approach" order, which keeps their party at the player's side
            public double ReadyHour = -1; // map: campaign hour the opening line was ready (give-up timer)
            public bool Received;         // map: the player said yes while they were still riding over; the talk opens when they arrive
            public double ReceivedHour = -1;
        }

        private static Approach _p;

        // Map units. The game's own encounter distance between parties is 0.5 and its "joining" radius 3. They only ask
        // to talk when their party is really next to the player's.
        private const float AskDistance = 1.5f;
        // Campaign hours they keep trying to get next to the player once their opening line is ready. Then they give up, with no popup.
        private const double CatchUpHours = 6.0;

        public static bool Busy => _p != null;

        /// <summary>A noble riding over on the map to talk is being handled (their opening line or the "wants to talk" window is pending).</summary>
        public static bool IsApproaching(Hero h)
        {
            var p = _p;
            return p != null && h != null && p.Hero == h && !p.Captor && !p.InParty;
        }

        /// <summary>
        /// The player told them not to come and ask for a word. Lasts until the player starts a talk with them (a conversation
        /// after the day it was set). Kept per campaign in the world file. Enemies who bar the way are never held back by it.
        /// </summary>
        public static bool IsMuted(Hero h)
        {
            try
            {
                var map = World.WorldSim.State?.ApproachMuted;
                if (h == null || map == null || !map.TryGetValue(h.StringId, out double day)) return false;
                var mem = Memory.MemoryStore.Find(h.StringId);
                if (mem != null && mem.LastConversationDay > day)
                {
                    map.Remove(h.StringId);
                    Log.Info($"Initiative: {h.Name} may come to talk again, the player has talked to them");
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void Mute(Hero h)
        {
            try
            {
                var st = World.WorldSim.State;
                if (h == null || st == null) return;
                if (st.ApproachMuted == null) st.ApproachMuted = new Dictionary<string, double>();
                st.ApproachMuted[h.StringId] = CampaignTime.Now.ToDays;
                Log.Info($"Initiative: {h.Name} will not come to talk until the player talks to them");
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1dd92", $"{h.Name} no volverá a venir a hablarte hasta que le hables tú."), Colors.Yellow));
                AILegacy.Memory.SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Initiative: no more approaches", ex); }
        }

        // Times each character was sent away lately (this session): the second time in a few days they may be told to stop
        private static readonly Dictionary<string, List<double>> Declined = new Dictionary<string, List<double>>();

        private static void AfterDecline(Hero h)
        {
            if (h == null) return;
            double now = CampaignTime.Now.ToDays;
            if (!Declined.TryGetValue(h.StringId, out var days)) Declined[h.StringId] = days = new List<double>();
            days.RemoveAll(d => now - d > 7);
            days.Add(now);
            if (days.Count < 2) return;
            days.Clear();
            int session = World.WorldSim.Session;
            InformationManager.ShowInquiry(new InquiryData(L.F("AIL_e7b1dd93", $"{h.Name} quiere hablar contigo"),
                L.F("AIL_e7b1dd94", $"{h.Name} ya ha venido varias veces. ¿Le dices que no vuelva a pedirte audiencia? Podrá volver a hacerlo en cuanto hables tú con él o con ella."),
                true, true, L.T("AIL_e7b1dd95", "Que no vuelva"), L.T("AIL_e7b1dd96", "Que siga viniendo"),
                () => { if (session == World.WorldSim.Session && Campaign.Current != null) Mute(h); }, null), true);
        }

        /// <summary>In a scene: the NPC (Situation already set) prepares an opening line and walks up to talk.</summary>
        public static bool Start(Agent agent, Speaker s, string openingInstruction, Action onUnanswered = null)
        {
            if (_p != null || agent == null || s == null) return false;
            if (s.IsHero && IsMuted(s.Hero)) { Log.Info($"Initiative: {s.Name} would come to talk, but was told not to"); return false; }
            return Begin(new Approach { Who = s, Agent = agent, OnUnanswered = onUnanswered }, openingInstruction);
        }

        /// <summary>On the map: the noble reached the player's party and wants to talk.</summary>
        public static bool StartMap(Hero hero, string openingInstruction, bool hostile = false)
        {
            if (_p != null || hero == null || Hero.MainHero == null || Hero.MainHero.IsPrisoner) return false;
            if (!hostile && IsMuted(hero)) { Log.Info($"Initiative: {hero.Name} would ride over to talk, but was told not to"); return false; }
            var s = Speaker.FromHero(hero);
            s.SituationKind = "initiative";
            s.Situation = openingInstruction;
            bool inParty = hero.PartyBelongedTo == MobileParty.MainParty;
            bool escorted = !inParty && World.OrderSystem.FindAny(hero)?.Type == "approach";
            return Begin(new Approach { Who = s, Hero = hero, Hostile = hostile, InParty = inParty, Escorted = escorted },
                hostile ? "You have ridden them down and barred their way. Speak first."
                : inParty ? "You ride in their party and have asked for a word. Speak first and say what is on your mind."
                : "You rode over to them. Speak first and say why you came.");
        }

        /// <summary>On the map: the lord who holds the player prisoner comes to speak to them.</summary>
        public static bool StartCaptor(Hero hero, string openingInstruction)
        {
            if (_p != null || hero == null || Hero.MainHero == null || !Hero.MainHero.IsPrisoner) return false;
            var s = Speaker.FromHero(hero);
            s.SituationKind = "initiative";
            s.Situation = openingInstruction;
            return Begin(new Approach { Who = s, Hero = hero, Captor = true },
                "You have come to see your prisoner. Speak first and say what you came for.");
        }

        private static bool Begin(Approach p, string openingInstruction)
        {
            var s = p.Who;
            List<ChatMessage> messages;
            try
            {
                messages = PromptBuilder.BuildDialogue(s, S.Get.MaxRelationChangePerConversation, openingInstruction);
            }
            catch (Exception ex) { Log.Error("Initiative: prompt", ex); return false; }

            _p = p;
            int session = World.WorldSim.Session;
            Log.Info($"Initiative: {s.Name} is going to approach the player");
            bool first = s.Memory.Profile == null || s.Memory.Profile.IsEmpty;
            LlmClient.CompleteAsync(messages, wantJson: true, task: "initiative", maxTokensOverride: first ? Math.Max(S.Get.MaxTokens, 1800) : (int?)null,
                preferredProvider: s.IsHero ? S.Get.ProviderHeroes : S.Get.ProviderResidents).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    if (_p != p || session != World.WorldSim.Session) return;
                    if (res == null || !res.Ok) { p.Failed = true; Log.Warn("Initiative: no AI reply: " + res?.Error); return; }
                    var reply = NpcReply.Parse(res.Text);
                    if (string.IsNullOrWhiteSpace(reply.Reply)) { p.Failed = true; return; }
                    if (reply.Profile != null && (s.Memory.Profile == null || s.Memory.Profile.IsEmpty)) s.Memory.Profile = reply.Profile;
                    if (!string.IsNullOrWhiteSpace(reply.Mood) && reply.Mood.Length <= 30) { s.Memory.Mood = reply.Mood; s.Memory.MoodReason = reply.MoodReason; s.Memory.MoodDay = CampaignTime.Now.ToDays; }
                    if (!string.IsNullOrWhiteSpace(reply.Thoughts)) Log.Info($"[thinks] {s.Name}: {reply.Thoughts}");
                    p.Emotion = reply.Emotion; p.Gesture = reply.Gesture;
                    p.Reply = reply.Reply;
                });
            });
            return true;
        }

        public static void Cancel() { var p = _p; if (p != null) End(p); }

        /// <summary>Ends this approach and, on the map, lets their party go back to its own business.</summary>
        private static void End(Approach p, string why = null)
        {
            if (_p == p) _p = null;
            if (why != null) Log.Info($"Initiative: {p.Who?.Name} {why}");
            ReleaseParty(p);
        }

        /// <summary>Ends the "approach" order that kept their party at the player's side, if it is still there.</summary>
        private static void ReleaseParty(Approach p)
        {
            if (p?.Hero == null || p.InParty || p.Captor) return;
            try
            {
                var o = World.OrderSystem.FindAny(p.Hero);
                if (o != null && o.Type == "approach") World.OrderSystem.Cancel(p.Hero, silent: true);
            }
            catch (Exception ex) { Log.Error("Initiative: release the party", ex); }
        }

        /// <summary>New, loaded or imported game: nobody from the previous one is still on their way to talk.</summary>
        public static void ResetSession() { _p = null; }

        /// <summary>Dialog line condition: is the conversation that just opened this approach?</summary>
        public static bool Matches()
        {
            try
            {
                if (_p == null || _p.Reply == null) return false;
                if (_p.Hero != null) return Hero.OneToOneConversationHero == _p.Hero;
                return Campaign.Current?.ConversationManager?.OneToOneConversationAgent == _p.Agent;
            }
            catch { return false; }
        }

        /// <summary>
        /// The approach ready to open is an enemy who rode the player down: "Back" from the talk then returns to the
        /// game's own hostile encounter (its surrender demand), not to the friendly options.
        /// </summary>
        public static bool HostilePending { get { var p = _p; return p != null && p.Reply != null && p.Hostile; } }

        /// <summary>Hands the opening line to the dialog session.</summary>
        public static ChatSession Take()
        {
            var p = _p;
            if (p == null || p.Reply == null) return null;
            // an enemy who caught the player (by our encounter or by the game's own chase): the shared wait starts
            if (p.Hostile) World.MapInitiative.HostileCaught();
            var session = ChatSession.StartDialog(p.Who);
            if (session == null) return null;
            if (!session.Seeded)
            {
                p.Who.Memory.Turns.Add(new ChatTurn { Role = "npc", Text = p.Reply, Day = CampaignTime.Now.ToDays });
                session.Seed(p.Reply, p.Emotion, p.Gesture);
            }
            return session;
        }

        /// <summary>The line was shown, so the approach is done.</summary>
        public static void Done() { var p = _p; _p = null; ReleaseParty(p); }

        public static void Tick(float dt)
        {
            var p = _p;
            if (p == null) return;
            try
            {
                if (Campaign.Current == null) { _p = null; p.OnUnanswered?.Invoke(); return; }
                if (p.Failed) { End(p, "has no opening line, the approach is dropped"); return; }
                // Waiting for the scene on foot: the talk there takes the opening line; if it never comes, it is dropped
                if (p.InScene)
                {
                    if (Scenes.MapTalk.IsOpen || Scenes.MapTalk.IsPending) { p.SceneAge = 0f; return; }
                    if ((p.SceneAge += dt) > 10f) End(p, "the scene closed without the talk");
                    return;
                }
                // The player reading the "wants to talk" window does not count against the time limit
                if (!(p.Asked && InquiryUp())) p.Age += dt;
                if (p.Hero != null) TickMap(p); else TickScene(p);
            }
            catch (Exception ex) { Log.Error("Initiative: tick", ex); End(p); }
        }

        private static bool InquiryUp()
        {
            try { return InformationManager.IsAnyInquiryActive(); } catch { return false; }
        }

        private static void TickScene(Approach p)
        {
            if (Mission.Current == null || p.Agent == null || !p.Agent.IsActive() || Agent.Main == null || !Agent.Main.IsActive() || p.Age > 90f)
            {
                _p = null; p.OnUnanswered?.Invoke(); return;
            }
            if (p.Reply == null) return;
            if (Campaign.Current.ConversationManager.IsConversationInProgress || ChatSession.IsBusy) return;
            var fh = Mission.Current.GetMissionBehavior<MissionFightHandler>();
            if (fh != null && fh.IsThereActiveFight()) return;
            if (Mission.Current.Mode == MissionMode.Battle || Mission.Current.Mode == MissionMode.Duel) return;

            var logic = Mission.Current.GetMissionBehavior<MissionConversationLogic>();
            if (logic == null) { _p = null; p.OnUnanswered?.Invoke(); return; }
            InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_ff6e4415", $"{p.Who.Name} se te acerca."), Colors.Yellow));
            logic.StartConversation(p.Agent, false, false);
            // if the opening line never matches, the approach expires on its own by age
        }

        /// <summary>A prisoner isn't asked whether to receive anyone: the captor's line is ready, so the talk just opens.</summary>
        private static void TickCaptor(Approach p)
        {
            var h = p.Hero;
            var captor = PlayerCaptivity.CaptorParty;
            bool stillTheirs = h != null && captor != null && Hero.MainHero.IsPrisoner
                && (captor.IsMobile ? captor.LeaderHero == h : captor.IsSettlement && h.CurrentSettlement == captor.Settlement);
            if (h == null || !h.IsAlive || h.IsPrisoner || !stillTheirs || p.Age > 240f) { _p = null; return; }
            if (captor.IsMobile && captor.MobileParty?.MapEvent != null) return;      // busy fighting: later, or it expires
            if (p.Reply == null || p.Asked) return;
            if (!WorldSim.OnMap()) return;
            p.Asked = true;
            p.Age = 0f;
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1c4d8", $"{h.Name} viene a verte."), Colors.Yellow));
                CampaignMapConversation.OpenConversation(
                    new ConversationCharacterData(CharacterObject.PlayerCharacter, PartyBase.MainParty),
                    new ConversationCharacterData(h.CharacterObject, captor));
            }
            catch (Exception ex) { Log.Error("Captor initiative: open conversation", ex); _p = null; }
        }

        private static void TickMap(Approach p)
        {
            var h = p.Hero;
            var party = h?.PartyBelongedTo;
            var main = MobileParty.MainParty;
            if (p.Captor) { TickCaptor(p); return; }
            if (h == null || !h.IsAlive || h.IsPrisoner || party == null || !party.IsActive || main == null) { End(p, "is no longer able to come over, the approach is dropped"); return; }
            if (party.MapEvent != null) { End(p, "got caught up in a battle, the approach is dropped"); return; }
            if (p.Asked)
            {
                if (p.Received)
                {
                    // Received while still on the way: they ride the rest (at the player's side, under their order)
                    // and the talk opens as soon as they are next to the player's party
                    if (p.Escorted)
                    {
                        var o = World.OrderSystem.FindAny(h);
                        if (o == null || o.Type != "approach") { End(p, "stopped riding over after being received"); return; }
                        KeepClose(party);
                    }
                    if (CampaignTime.Now.ToHours - p.ReceivedHour > CatchUpHours) { End(p, "never reached the player's party after being received, gave up"); return; }
                    if (!WorldSim.OnMap() || !Letters.LetterSystem.PlayerFreeOnMap()) return;
                    if (party.GetPosition2D.Distance(main.GetPosition2D) > AskDistance) return;
                    Open(p, h, party);
                    return;
                }
                // Waiting for the window's answer or for the conversation to open
                if (p.Age > 240f) End(p);
                return;
            }
            if (p.Reply == null)
            {
                if (p.Age > 240f) { End(p, "took too long to find their words, the approach is dropped"); return; }
            }

            float dist = p.InParty ? 0f : party.GetPosition2D.Distance(main.GetPosition2D);
            if (!p.InParty && p.Escorted)
            {
                // The order that keeps them riding at the player's side ended (expired, army call, new order): no popup
                var o = World.OrderSystem.FindAny(h);
                if (o == null || o.Type != "approach") { End(p, $"stopped riding over ({dist:0.0} map units away), no popup"); return; }
                KeepClose(party);
            }
            if (p.Reply == null) return;

            if (p.ReadyHour < 0) p.ReadyHour = CampaignTime.Now.ToHours;
            if (!p.InParty && CampaignTime.Now.ToHours - p.ReadyHour > CatchUpHours)
            {
                End(p, $"never got next to the player's party ({dist:0.0} map units away after {CatchUpHours:0} hours), gave up with no popup");
                return;
            }
            // Only with the player free on the map (no menu, battle, conversation or other window): an inquiry shown
            // behind another one waits in the game's queue while time runs and they ride off
            if (!WorldSim.OnMap() || !Letters.LetterSystem.PlayerFreeOnMap()) return;
            // An enemy has to catch the player first. A friendly lord asks as he approaches (AIInfluence): the
            // "wants to talk" window comes up as soon as his words are ready, wherever he is on his way
            if (p.Hostile && dist > AskDistance) return;

            Ask(p, h, party, dist);
        }

        /// <summary>Keeps their party riding at the player's side and out of the game AI's hands while the talk is pending.</summary>
        private static void KeepClose(MobileParty party)
        {
            try
            {
                if (Mission.Current != null) return;      // never move parties while a scene is open
                var main = MobileParty.MainParty;
                if (!party.Ai.DoNotMakeNewDecisions) party.Ai.SetDoNotMakeNewDecisions(true);
                if (party.DefaultBehavior != AiBehavior.EscortParty || party.TargetParty != main)
                    party.SetMoveEscortParty(main, party.IsCurrentlyAtSea ? party.NavigationCapability : MobileParty.NavigationType.Default, false);
            }
            catch (Exception ex) { Log.Error("Initiative: keep the party close", ex); }
        }

        /// <summary>Opens the talk with a lord who came over (or rides with the player) and was received.</summary>
        private static void Open(Approach p, Hero h, MobileParty party)
        {
            try
            {
                var talkParty = h.PartyBelongedTo ?? party;
                // Their party goes back to its own business after the talk, unless they agree to something in it
                ReleaseParty(p);
                // With the scene opening by itself, they come and speak to you on foot (their opening line is ready)
                var mode = Scenes.MapTalk.ModeFor(h);
                if (S.Get.MapTalkAutomatic && !p.Hostile && (mode == Scenes.MapTalkMode.Lord || mode == Scenes.MapTalkMode.Companion))
                {
                    p.InScene = true; p.SceneAge = 0f;
                    Scenes.MapTalk.Queue(h);
                    Log.Info($"[map scene] NPC initiative opens the scene: {mode} {h.Name}");
                    return;
                }
                CampaignMapConversation.OpenConversation(
                    new ConversationCharacterData(CharacterObject.PlayerCharacter, PartyBase.MainParty),
                    new ConversationCharacterData(h.CharacterObject, talkParty.Party));
            }
            catch (Exception ex) { Log.Error("Initiative: open conversation on the map", ex); End(p); }
        }

        private static void Ask(Approach p, Hero h, MobileParty party, float dist)
        {
            p.Asked = true;
            p.Age = 0f;
            Log.Info(p.InParty ? $"Initiative: {h.Name}, from the player's party, asks for a word"
                               : $"Initiative: {h.Name} is next to the player's party ({dist:0.00} map units), {(p.Hostile ? "bars their way" : "asks to talk")}");
            // An enemy who caught up doesn't ask, they block your way like any hostile encounter in the game.
            // Their opening line is ready and shows as soon as the parley opens. After that, battle or whatever you agree on.
            if (p.Hostile)
            {
                try
                {
                    ReleaseParty(p);
                    World.MapInitiative.HostileCaught();
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_f4c8e2a1", $"{h.Name} te corta el paso."), Colors.Red));
                    EncounterManager.StartPartyEncounter(party.Party, PartyBase.MainParty);
                }
                catch (Exception ex) { Log.Error("Hostile initiative: open encounter", ex); End(p); }
                return;
            }
            // The window pauses the game, so they stay where they are until the player answers
            InformationManager.ShowInquiry(new InquiryData(L.F("AIL_c65afb5d", $"{h.Name} quiere hablar contigo"),
                p.InParty ? L.F("AIL_f4c8e2a2", $"{h.Name}, que cabalga contigo, te pide un momento.")
                          : dist > AskDistance ? L.F("AIL_e7b1d827", $"{h.Name}{(h.Clan != null ? L.T("AIL_4514967a", ", del clan ") + h.Clan.Name : "")}, cabalga hacia tu grupo y pide hablar contigo.")
                          : L.F("AIL_b6d441ce", $"{h.Name}{(h.Clan != null ? L.T("AIL_4514967a", ", del clan ") + h.Clan.Name : "")}, ha cabalgado hasta tu grupo y pide hablar contigo."),
                true, true, L.T("AIL_f4c8e2a3", "Recibirle"), L.T("AIL_f4c8e2a4", "Ahora no"),
                () => MainThread.Post(() =>
                {
                    try
                    {
                        if (_p != p || !WorldSim.OnMap()) { End(p, "could not be received (the moment passed)"); return; }
                        var talkParty = h.PartyBelongedTo ?? party;
                        // Still on the way: they ride up to the player's side and wait there; the talk opens when they arrive
                        if (!p.InParty && talkParty.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) > AskDistance)
                        {
                            p.Received = true;
                            p.ReceivedHour = CampaignTime.Now.ToHours;
                            InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1d826", $"{h.Name} se acerca para hablar contigo."), Colors.Yellow));
                            return;
                        }
                        Open(p, h, talkParty);
                    }
                    catch (Exception ex) { Log.Error("Initiative: open conversation on the map", ex); End(p); }
                }),
                () =>
                {
                    End(p, "was not received");
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1ca00", $"{h.Name} no oculta su decepción, pero asiente."), Colors.Gray));
                    ChatSession.AddNote(p.Who.Memory, "I rode over to speak with them and they would not receive me.", S.Get.MaxNotesPerHero, p.Who.Name);
                    MainThread.Post(() => AfterDecline(h));
                }), true);
        }
    }
}
