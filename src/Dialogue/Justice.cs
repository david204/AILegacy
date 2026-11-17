using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Town guard. After a fight in a settlement (or if someone shouts for the guard), the nearest guard walks up
    /// and questions you. Depending on what you say and who you are, they let you go, fine you or lock you up.
    /// </summary>
    internal static class Justice
    {
        private static Speaker _guard;        // guard handling the current case
        private static bool _resolved;
        private static bool _talked;
        private static Settlement _jailIn;    // jail the player once they leave the scene
        private static string _case, _jailStanding, _jailCrime;      // what happened, for the lord's trial
        private static Speaker _jailGuard;

        /// <summary>Beaten down alone in a fight with the guard: to the dungeon once the scene closes, then the lord's trial.</summary>
        internal static void JailAfterFight(Settlement st, string whatHappenedEn)
        {
            if (st == null) return;
            _case = whatHappenedEn; _jailGuard = null;
            try { _jailStanding = Standing(st); _jailCrime = Crime(st); } catch { }
            _jailIn = st;
        }
        private static Agent _guardAgent;
        private static Settlement _arrestIn;  // told they're under arrest, waiting on the player's choice
        private static bool _asking;          // choice popup is open
        private static bool _resisting;       // fighting the guards to avoid the cell
        private static float _resistAge;      // seconds since resisting started
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        private static readonly Tpl TSituation = new Tpl("justice/guard_after_trouble.txt",
@"You are a guard on duty in {settlement}, which belongs to {owner} of {faction}. {what_happened}
You walked up to them. Your duty is to find out what happened and settle it on the spot. You alone decide, with one of these actions when you have heard enough (not in your very first words):
- let_go: you accept their account or it is not worth the trouble; they walk free, perhaps with a warning.
- fine:<denars>: they pay on the spot for the disturbance (usually {fine_low} to {fine_high} denars, more for blood or for the insolent).
- arrest: you take them to the cells. For serious blood, for those who lie to your face, threaten you, cannot or will not pay, or are already wanted here.
What weighs on you: how bad it was (fists are common, steel is serious, a death is grave); who they are to this place ({standing}); their standing with the law here ({crime}); and how they answer you — respect and a straight story go far, lies, mockery and threats do not. You are a common soldier: wary of the powerful, hard on troublemakers, not easily bought but not a saint either. Do not let them stall for ever: after a few exchanges, decide.");

        public static void Touch() { var _ = TSituation.Key; }

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        /// <summary>The player is fighting the guard to avoid the cell: that fight is part of the guard's story, not a news item of its own.</summary>
        public static bool Resisting => _resisting;

        public static bool Active(Speaker s) => s != null && _guard != null && !_resolved && s.SituationKind == "justice" && s.Key == _guard.Key;

        // ============================================================ find a guard and send them over

        private static Agent NearestGuard(Agent except, float maxDistance)
        {
            try
            {
                var m = Mission.Current;
                if (m == null || Agent.Main == null) return null;
                Agent best = null; float bestD = float.MaxValue;
                foreach (var a in m.Agents)
                {
                    if (a == null || !a.IsHuman || !a.IsActive() || a == Agent.Main || a == except || a.IsHero) continue;
                    var c = a.Character as CharacterObject;
                    if (c == null || (c.Occupation != Occupation.Guard && c.Occupation != Occupation.Soldier && c.Occupation != Occupation.PrisonGuard)) continue;
                    if (a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main)) continue;
                    float d = a.Position.Distance(Agent.Main.Position);
                    if (d < bestD && d <= maxDistance) { best = a; bestD = d; }
                }
                return best;
            }
            catch (Exception ex) { Log.Error("Finding guard", ex); return null; }
        }

        private static string Standing(Settlement st)
        {
            if (st.OwnerClan != null && st.OwnerClan == Clan.PlayerClan) return "they are the lord of this very place";
            if (st.MapFaction != null && st.MapFaction == P.MapFaction)
                return P.MapFaction is Kingdom k && k.Leader == P ? "they are your sovereign" : "they serve the same realm as your lord" + (Clan.PlayerClan.Tier >= 3 ? ", and are a noble of some name" : "");
            if (st.MapFaction != null && P.MapFaction != null && st.MapFaction.IsAtWarWith(P.MapFaction)) return "they belong to a realm AT WAR with yours: an enemy inside your walls";
            return Clan.PlayerClan != null && Clan.PlayerClan.Tier >= 4 ? "an outsider, but a famous one" : "an outsider with no standing here";
        }

        private static string Crime(Settlement st)
        {
            try
            {
                float c = st.MapFaction?.MainHeroCrimeRating ?? 0f;
                return c < 5 ? "clean" : c < 30 ? "known for small trouble" : c < 60 ? "a known offender" : "a wanted criminal";
            }
            catch { return "unknown"; }
        }

        /// <summary>Trouble happened. If a guard is close enough (or was shouted for), they come for you.</summary>
        public static bool Summon(string whatHappenedEn, Agent except, bool called)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.AllowJustice || _guard != null || Initiative.Busy || Mission.Current == null) return false;
                var st = Settlement.CurrentSettlement;
                if (st == null || st.OwnerClan == Clan.PlayerClan) return false;       // nobody questions you in your own fief
                var agent = NearestGuard(except, called ? cfg.GuardCallDistance : cfg.GuardHearingDistance);
                if (agent == null) return false;

                var s = Speaker.FromResident((CharacterObject)agent.Character, agent);
                s.SituationKind = "justice";
                s.Situation = TSituation.Format("settlement", st.Name, "owner", st.OwnerClan?.Leader?.Name?.ToString() ?? "its lord", "faction", st.MapFaction?.Name?.ToString() ?? "no realm",
                    "what_happened", whatHappenedEn, "fine_low", cfg.FineMin, "fine_high", cfg.FineMax, "standing", Standing(st), "crime", Crime(st));
                _guard = s; _resolved = false; _talked = false;
                _guardAgent = agent;
                _case = whatHappenedEn;
                if (!Initiative.Start(agent, s, "Walk up to them and speak first: demand to know what just happened here.", Unanswered))
                { _guard = null; return false; }
                return true;
            }
            catch (Exception ex) { Log.Error("Calling the guard", ex); _guard = null; return false; }
        }

        public static void OnFightEnded(Speaker opponent, Agent opponentAgent, string kind, bool playerWon, bool unprovoked = false)
        {
            if (_resisting) { _resisting = false; ResistEnded(playerWon); return; }
            string who = opponent.IsHero ? opponent.Name : (opponent.IsFemale ? "a local woman" : "a local man");
            string what = kind == "brawl" ? $"Moments ago the armed person in front of you had a fist fight with {who} in public"
                        : kind == "death" ? $"Moments ago the armed person in front of you fought {who} with drawn steel, to the death, in public"
                        : $"Moments ago the armed person in front of you fought a duel with drawn steel against {who} in public";
            what += playerWon ? $", and left them on the ground. You saw the end of it." : $", and got the worst of it. You saw the end of it.";
            if (unprovoked) what += " Witnesses say the armed person struck first, without a word and without provocation.";
            Summon(what, opponentAgent, false);
        }

        // ============================================================ the guard's verdict

        /// <summary>The guard settled it. The news is told with the rest of the visit, once the player leaves.</summary>
        private static void Close(string step, int fine = 0)
        {
            _resolved = true;
            VisitNews.Guard(step, fine);
        }

        public static string LetGo(Speaker s)
        {
            Close("let_go");
            return L.T("AIL_3f4f693a", "El guardia te deja marchar.");
        }

        public static string Fine(Speaker s, int amount)
        {
            var cfg = S.Get;
            var st = Settlement.CurrentSettlement;
            amount = Math.Max(1, Math.Min(cfg.FineMax * 5, amount));
            if (P.Gold < amount) return L.F("AIL_6d808299", $"No llevas {amount} denarios para pagar la multa.");
            if (st != null) GiveGoldAction.ApplyForCharacterToSettlement(P, st, amount);
            else P.ChangeHeroGold(-amount);
            Close("fine", amount);
            return L.F("AIL_dc5f5110", $"Pagas una multa de {amount} denarios.");
        }

        public static string Arrest(Speaker s)
        {
            var st = Settlement.CurrentSettlement;
            if (st == null) return null;
            // Not straight to the cell. When the talk ends the player picks: surrender, resist or run
            _arrestIn = st;
            _jailGuard = s; _jailStanding = Standing(st); _jailCrime = Crime(st);
            try { if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 10f, true); } catch { }
            Close("arrest");
            return L.F("AIL_f2440a00", $"El guardia te detiene: vas a los calabozos de {st.Name}.");
        }

        // ============================================================ arrested: the player's choice

        private static void AskArrest(Settlement st)
        {
            _asking = true;
            try
            {
                var items = new List<InquiryElement>
                {
                    new InquiryElement("yield", L.T("AIL_e1f3b7a1", "Entregarte"), null, true, L.T("AIL_e1f3b7a2", "Vas a los calabozos y el señor del lugar decide tu pena.")),
                    new InquiryElement("resist", L.T("AIL_e1f3b7a3", "Resistirte"), null, Combat.CanFightHere() && _guardAgent != null && _guardAgent.IsActive(),
                        L.T("AIL_e1f3b7a4", "Peleas con la guardia. Si ganas, escapas y quedas buscado; si pierdes, a la celda y con peor fama.")),
                    new InquiryElement("run", L.T("AIL_e1f3b7a5", "Echar a correr"), null, true, L.T("AIL_e1f3b7a6", "Intentas huir de la ciudad. Puede salir mal.")),
                };
                MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                    L.F("AIL_e1f3b7a7", $"La guardia de {st.Name} te detiene"), L.T("AIL_e1f3b7a8", "¿Qué haces?"),
                    items, false, 1, 1, L.T("AIL_e1f3b7a9", "Decidir"), "",
                    sel => MainThread.Post(() => OnArrestChoice(st, sel != null && sel.Count > 0 ? sel[0].Identifier as string : "yield")), null, "", false), true);
            }
            catch (Exception ex) { Log.Error("Arrest: window", ex); _asking = false; _jailIn = st; }
        }

        private static void OnArrestChoice(Settlement st, string choice)
        {
            _asking = false;
            try
            {
                if (Settlement.CurrentSettlement != st) return;         // player already left
                switch (choice)
                {
                    case "resist":
                    {
                        var other = NearestGuard(_guardAgent, 25f);
                        try { if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 10f, true); } catch { }
                        if (_jailGuard != null && Combat.QueueFightWith(_jailGuard, _guardAgent, "duel", other != null ? new[] { other } : null))
                        {
                            _resisting = true; _resistAge = 0f;
                            Say(L.T("AIL_e1f3b7aa", "Te resistes a la guardia."), Colors.Red);
                            return;
                        }
                        _jailIn = st;      // nobody to fight, so they take you
                        return;
                    }
                    case "run":
                    {
                        int athletics = 0;
                        try { athletics = P.GetSkillValue(DefaultSkills.Athletics); } catch { }
                        int chance = Math.Max(20, Math.Min(85, 35 + athletics / 4));
                        if (Rng.Next(100) < chance)
                        {
                            try { if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 15f, true); } catch { }
                            Say(L.F("AIL_e1f3b7ab", $"Escapas por los pelos de {st.Name}: ahora te buscan."), Colors.Yellow);
                            VisitNews.Guard("escaped");
                            _escapeFrom = st;
                        }
                        else
                        {
                            try { if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 8f, true); } catch { }
                            Say(L.T("AIL_e1f3b7ac", "No llegas lejos: te alcanzan."), Colors.Red);
                            VisitNews.Guard("caught");
                            _jailIn = st;
                        }
                        return;
                    }
                    default:
                        _jailIn = st;
                        return;
                }
            }
            catch (Exception ex) { Log.Error("Arrest: decision", ex); _jailIn = st; }
        }

        private static Settlement _escapeFrom;

        /// <summary>The fight against the guards (to avoid the cell) is over.</summary>
        private static void ResistEnded(bool playerWon)
        {
            var st = Settlement.CurrentSettlement;
            if (st == null) return;
            try
            {
                if (playerWon)
                {
                    if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 20f, true);
                    Say(L.F("AIL_e1f3b7ad", $"Te abres paso y escapas de {st.Name}: ahora te buscan."), Colors.Yellow);
                    _escapeFrom = st;
                }
                else
                {
                    if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, 10f, true);
                    Say(L.T("AIL_e1f3b7ae", "La guardia te reduce."), Colors.Red);
                    _jailIn = st;
                }
                VisitNews.Guard(playerWon ? "resist_won" : "resist_lost");
            }
            catch (Exception ex) { Log.Error("Arrest: after the fight", ex); }
        }

        /// <summary>The guard never got to talk (player left the scene, the AI didn't answer...).</summary>
        private static void Unanswered() { _guard = null; }

        // ============================================================ tick

        public static void Tick(float dt)
        {
            try
            {
                if (Campaign.Current == null) { _guard = null; _jailIn = null; _arrestIn = null; _escapeFrom = null; _asking = false; _resisting = false; return; }
                bool talking = Campaign.Current.ConversationManager?.IsConversationInProgress == true;
                if (!string.IsNullOrEmpty(WorldSim.State?.JailMenu)) Verdict.Pump();      // a dungeon menu waiting to be shown

                // Was resisting but the fight never started (scene changed). Don't leave it hanging, take them to jail
                if (_resisting)
                {
                    _resistAge += dt;
                    if (_resistAge > 6f && !Combat.Fighting)
                    {
                        _resisting = false;
                        var here = Settlement.CurrentSettlement;
                        if (here != null) _jailIn = here;
                    }
                }

                // Arrested: the player decides as soon as the talk closes
                if (_arrestIn != null && !talking && !_asking && !ChatSession.IsBusy)
                {
                    var st = _arrestIn; _arrestIn = null;
                    if (Settlement.CurrentSettlement == st && Mission.Current != null) AskArrest(st);
                    else _jailIn = st;
                }

                // Escaped: out of the scene and out of the settlement
                if (_escapeFrom != null && !talking)
                {
                    if (Mission.Current != null)
                    {
                        if (!Mission.Current.MissionEnded) Mission.Current.EndMission();
                    }
                    else
                    {
                        var st = _escapeFrom; _escapeFrom = null;
                        if (Settlement.CurrentSettlement == st) { PlayerEncounter.LeaveSettlement(); PlayerEncounter.Finish(); }
                    }
                }

                if (_guard != null)
                {
                    if (talking && ChatSession.Active != null && ChatSession.Active.Who?.Key == _guard.Key) _talked = true;
                    else if (!talking && !ChatSession.IsBusy && !Initiative.Busy)
                    {
                        if (_talked && !_resolved)
                        {
                            var st = Settlement.CurrentSettlement;
                            try { if (st?.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, S.Get.CrimeForWalkingAway, true); } catch { }
                            Say(L.T("AIL_fe057bc7", "Dejas al guardia con la palabra en la boca: tu mala fama aquí crece."), Colors.Red);
                        }
                        _guard = null;
                    }
                }

                // To the dungeon, but the scene has to close first
                if (_jailIn != null && !talking)
                {
                    if (Mission.Current != null)
                    {
                        if (!Mission.Current.MissionEnded) Mission.Current.EndMission();
                    }
                    else
                    {
                        var st = _jailIn; _jailIn = null;
                        if (Settlement.CurrentSettlement == st)
                        {
                            // same as the game does when a prison break fails (the visit's news is told as the player leaves)
                            VisitNews.Guard("jailed");
                            PlayerEncounter.LeaveSettlement();
                            PlayerEncounter.Finish();
                            TakePrisonerAction.Apply(st.Party, P);
                            Log.Info($"Player is jailed in {st.Name}");
                            Verdict.Judge(st, _case, _jailGuard, _jailStanding, _jailCrime);
                            _jailGuard = null;
                        }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Justice: tick", ex); _guard = null; _jailIn = null; _arrestIn = null; _escapeFrom = null; }
        }
    }
}
