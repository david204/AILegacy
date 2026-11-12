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
    /// Iniciativa: un NPC se dirige a ti sin que le hables. Primero se le pide a la IA su primera frase;
    /// cuando llega, se abre la conversación del juego con él y esa frase es lo primero que dice.
    ///  - En una escena (Start): la guardia tras una pelea, o cualquier NPC que deba acercarse.
    ///  - En el mapa (StartMap): un noble que te conoce cabalga hasta ti y pide hablar.
    /// </summary>
    internal static class Initiative
    {
        private class Approach
        {
            public Speaker Who;
            public Agent Agent;           // escena
            public Hero Hero;             // mapa
            public string Reply;          // null = la IA aún está pensando
            public bool Failed;
            public bool Asked;            // mapa: ya se ha preguntado al jugador si le recibe
            public bool Hostile;          // mapa: un enemigo te ha dado alcance; no pide permiso, te corta el paso
            public bool InParty;          // mapa: va en tu propia partida
            public float Age;
            public Action OnUnanswered;   // el jugador se fue o no se pudo abrir la charla
        }

        private static Approach _p;

        public static bool Busy => _p != null;

        /// <summary>En una escena: el NPC (ya con su Situation puesta) prepara su primera frase y se acerca a hablar.</summary>
        public static bool Start(Agent agent, Speaker s, string openingInstruction, Action onUnanswered = null)
        {
            if (_p != null || agent == null || s == null) return false;
            return Begin(new Approach { Who = s, Agent = agent, OnUnanswered = onUnanswered }, openingInstruction);
        }

        /// <summary>En el mapa: el noble ha llegado junto a tu grupo y quiere hablar.</summary>
        public static bool StartMap(Hero hero, string openingInstruction, bool hostile = false)
        {
            if (_p != null || hero == null) return false;
            var s = Speaker.FromHero(hero);
            s.SituationKind = "initiative";
            s.Situation = openingInstruction;
            bool inParty = hero.PartyBelongedTo == MobileParty.MainParty;
            return Begin(new Approach { Who = s, Hero = hero, Hostile = hostile, InParty = inParty },
                hostile ? "You have ridden them down and barred their way. Speak first."
                : inParty ? "You ride in their party and have asked for a word. Speak first and say what is on your mind."
                : "You rode over to them. Speak first and say why you came.");
        }

        private static bool Begin(Approach p, string openingInstruction)
        {
            var s = p.Who;
            List<ChatMessage> messages;
            try
            {
                messages = PromptBuilder.BuildDialogue(s, S.Get.MaxRelationChangePerConversation, openingInstruction);
            }
            catch (Exception ex) { Log.Error("Iniciativa: prompt", ex); return false; }

            _p = p;
            int session = World.WorldSim.Session;
            Log.Info($"Iniciativa: {s.Name} va a dirigirse al jugador");
            bool first = s.Memory.Profile == null || s.Memory.Profile.IsEmpty;
            LlmClient.CompleteAsync(messages, wantJson: true, task: "initiative", maxTokensOverride: first ? Math.Max(S.Get.MaxTokens, 1800) : (int?)null,
                preferredProvider: s.IsHero ? S.Get.ProviderHeroes : S.Get.ProviderResidents).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    if (_p != p || session != World.WorldSim.Session) return;
                    if (res == null || !res.Ok) { p.Failed = true; Log.Warn("Iniciativa: sin respuesta de la IA: " + res?.Error); return; }
                    var reply = NpcReply.Parse(res.Text);
                    if (string.IsNullOrWhiteSpace(reply.Reply)) { p.Failed = true; return; }
                    if (reply.Profile != null && (s.Memory.Profile == null || s.Memory.Profile.IsEmpty)) s.Memory.Profile = reply.Profile;
                    if (!string.IsNullOrWhiteSpace(reply.Mood) && reply.Mood.Length <= 30) { s.Memory.Mood = reply.Mood; s.Memory.MoodReason = reply.MoodReason; s.Memory.MoodDay = CampaignTime.Now.ToDays; }
                    if (!string.IsNullOrWhiteSpace(reply.Thoughts)) Log.Info($"[piensa] {s.Name}: {reply.Thoughts}");
                    p.Reply = reply.Reply;
                });
            });
            return true;
        }

        public static void Cancel() { _p = null; }

        /// <summary>Partida nueva, cargada o importada: nadie de la anterior sigue de camino a hablarte.</summary>
        public static void ResetSession() { _p = null; }

        /// <summary>¿La conversación que se acaba de abrir es la de este acercamiento? (condición de la línea de diálogo)</summary>
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

        /// <summary>Entrega la primera frase a la sesión de diálogo.</summary>
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

        /// <summary>La línea ya se ha enseñado: el acercamiento ha cumplido.</summary>
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
            catch (Exception ex) { Log.Error("Iniciativa: tick", ex); _p = null; }
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
            // si la línea de inicio no llega a casar, el acercamiento caduca solo por edad
        }

        private static void TickMap(Approach p)
        {
            var h = p.Hero;
            var party = h?.PartyBelongedTo;
            if (h == null || !h.IsAlive || h.IsPrisoner || party == null || !party.IsActive || party.MapEvent != null || p.Age > 240f) { _p = null; return; }
            if (p.Reply == null || p.Asked) return;
            if (!WorldSim.OnMap() || MobileParty.MainParty.MapEvent != null) return;
            // ya no está al lado: se ha ido el momento
            if (party.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) > 8f) { _p = null; return; }

            p.Asked = true;
            p.Age = 0f;
            // Un enemigo que te ha dado alcance no pide permiso: te corta el paso, como cualquier encuentro hostil del juego.
            // Su primera frase ya está lista y sale en cuanto se abre el parlamento; después, batalla o lo que acordéis.
            if (p.Hostile)
            {
                try
                {
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_f4c8e2a1", $"{h.Name} te corta el paso."), Colors.Red));
                    EncounterManager.StartPartyEncounter(party.Party, PartyBase.MainParty);
                }
                catch (Exception ex) { Log.Error("Iniciativa hostil: abrir el encuentro", ex); _p = null; }
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
                    catch (Exception ex) { Log.Error("Iniciativa: abrir conversación en el mapa", ex); _p = null; }
                }),
                () => { _p = null; ChatSession.AddNote(p.Who.Memory, "I rode over to speak with them and they would not receive me.", S.Get.MaxNotesPerHero, p.Who.Name); }), true);
        }
    }
}
