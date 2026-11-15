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
            public bool Failed;
            public bool Asked;            // map: player was already asked whether to receive them
            public bool Hostile;          // map: an enemy caught up and blocks your way, no asking
            public bool InParty;          // map: in the player's own party
            public bool Captor;           // map: holds the player prisoner and comes to see them
            public float Age;
            public Action OnUnanswered;   // player left or the conversation couldn't open
        }

        private static Approach _p;

        public static bool Busy => _p != null;

        /// <summary>In a scene: the NPC (Situation already set) prepares an opening line and walks up to talk.</summary>
        public static bool Start(Agent agent, Speaker s, string openingInstruction, Action onUnanswered = null)
        {
            if (_p != null || agent == null || s == null) return false;
            return Begin(new Approach { Who = s, Agent = agent, OnUnanswered = onUnanswered }, openingInstruction);
        }

        /// <summary>On the map: the noble reached the player's party and wants to talk.</summary>
        public static bool StartMap(Hero hero, string openingInstruction, bool hostile = false)
        {
            if (_p != null || hero == null || Hero.MainHero == null || Hero.MainHero.IsPrisoner) return false;
            var s = Speaker.FromHero(hero);
            s.SituationKind = "initiative";
            s.Situation = openingInstruction;
            bool inParty = hero.PartyBelongedTo == MobileParty.MainParty;
            return Begin(new Approach { Who = s, Hero = hero, Hostile = hostile, InParty = inParty },
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
                    p.Reply = reply.Reply;
                });
            });
            return true;
        }

        public static void Cancel() { _p = null; }

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

        /// <summary>Hands the opening line to the dialog session.</summary>
        public static ChatSession Take()
        {
            var p = _p;
            if (p == null || p.Reply == null) return null;
            var session = ChatSession.StartDialog(p.Who);
            if (session == null) return null;
            if (!session.Seeded)
            {
                p.Who.Memory.Turns.Add(new ChatTurn { Role = "npc", Text = p.Reply, Day = CampaignTime.Now.ToDays });
                session.Seed(p.Reply);
            }
            return session;
        }

        /// <summary>The line was shown, so the approach is done.</summary>
        public static void Done() { _p = null; }

        public static void Tick(float dt)
        {
            var p = _p;
            if (p == null) return;
            try
            {
                if (Campaign.Current == null || p.Failed) { _p = null; if (!p.Failed) p.OnUnanswered?.Invoke(); return; }
                p.Age += dt;
                if (p.Hero != null) TickMap(p); else TickScene(p);
            }
            catch (Exception ex) { Log.Error("Initiative: tick", ex); _p = null; }
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
            if (p.Captor) { TickCaptor(p); return; }
            if (h == null || !h.IsAlive || h.IsPrisoner || party == null || !party.IsActive || party.MapEvent != null || p.Age > 240f) { _p = null; return; }
            if (p.Reply == null || p.Asked) return;
            if (!WorldSim.OnMap() || MobileParty.MainParty.MapEvent != null) return;
            // no longer close by, the moment has passed
            if (party.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) > 8f) { _p = null; return; }

            p.Asked = true;
            p.Age = 0f;
            // An enemy who caught up doesn't ask, they block your way like any hostile encounter in the game.
            // Their opening line is ready and shows as soon as the parley opens. After that, battle or whatever you agree on.
            if (p.Hostile)
            {
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_f4c8e2a1", $"{h.Name} te corta el paso."), Colors.Red));
                    EncounterManager.StartPartyEncounter(party.Party, PartyBase.MainParty);
                }
                catch (Exception ex) { Log.Error("Hostile initiative: open encounter", ex); _p = null; }
                return;
            }
            InformationManager.ShowInquiry(new InquiryData(L.F("AIL_c65afb5d", $"{h.Name} quiere hablar contigo"),
                p.InParty ? L.F("AIL_f4c8e2a2", $"{h.Name}, que cabalga contigo, te pide un momento.")
                          : L.F("AIL_b6d441ce", $"{h.Name}{(h.Clan != null ? L.T("AIL_4514967a", ", del clan ") + h.Clan.Name : "")}, ha cabalgado hasta tu grupo y pide hablar contigo."),
                true, true, L.T("AIL_f4c8e2a3", "Recibirle"), L.T("AIL_f4c8e2a4", "Ahora no"),
                () => MainThread.Post(() =>
                {
                    try
                    {
                        if (_p != p || !WorldSim.OnMap()) { _p = null; return; }
                        CampaignMapConversation.OpenConversation(
                            new ConversationCharacterData(CharacterObject.PlayerCharacter, PartyBase.MainParty),
                            new ConversationCharacterData(h.CharacterObject, party.Party));
                    }
                    catch (Exception ex) { Log.Error("Initiative: open conversation on the map", ex); _p = null; }
                }),
                () => { _p = null; ChatSession.AddNote(p.Who.Memory, "I rode over to speak with them and they would not receive me.", S.Get.MaxNotesPerHero, p.Who.Name); }), true);
        }
    }
}
