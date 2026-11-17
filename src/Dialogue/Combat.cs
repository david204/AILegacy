using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using SandBox.Missions.MissionLogics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Violence that comes out of a chat: fist fights and duels in the scene (through the game's fight handler),
    /// attack and surrender on the map, and calling the guards. It all starts after the dialogue has closed.
    /// </summary>
    internal static partial class Combat
    {
        private class PendingFight
        {
            public Speaker Who;
            public Agent Opponent;
            public string Kind;      // brawl | duel | death
            public float Wait;
            public bool Unprovoked;                              // you hit them out of nowhere
            public List<Agent> Helpers = new List<Agent>();      // people who step in to defend them
            /// <summary>How it ended when it wasn't simply won or lost: truce, fled, yielded, player_yielded, struck_down. Null otherwise.</summary>
            public string Ending;
            /// <summary>A duel agreed in a talk: the 3-second "ready your weapon" warning was already given.</summary>
            public bool Announced;
            /// <summary>The player broke the terms of an agreed duel by calling troops: it became a battle.</summary>
            public bool Betrayed;
            /// <summary>A fist fight inside a tavern: part of the evening, the patrons stay and watch and nobody calls the guard.</summary>
            public bool Tavern;
        }

        private static PendingFight _fight;
        private static Speaker _surrender;
        private static readonly List<Hero> ToKill = new List<Hero>();

        /// <summary>The NPC decided to attack, so closing the chat does NOT leave the encounter (the battle menu shows up).</summary>
        public static bool KeepEncounter;

        private static Hero P => Hero.MainHero;

        // ============================================================ fights and duels in the scene

        private static Agent TalkingAgent()
        {
            try { return Campaign.Current?.ConversationManager?.OneToOneConversationAgent as Agent; }
            catch { return null; }
        }

        /// <summary>True when there's a scene, the NPC is in it, and the mission has a fight handler.</summary>
        public static bool CanFightHere()
        {
            try
            {
                if (!S.Get.AllowCombat || _fight != null) return false;
                var m = Mission.Current;
                if (m == null || Agent.Main == null || !Agent.Main.IsActive()) return false;
                var a = TalkingAgent();
                if (a == null || !a.IsActive() || !a.IsHuman) return false;
                var fh = m.GetMissionBehavior<MissionFightHandler>();
                return fh != null && !fh.IsThereActiveFight();
            }
            catch { return false; }
        }

        public static string QueueFight(Speaker s, string kind)
        {
            var a = TalkingAgent();
            if (a == null) return null;
            // An agreed duel starts 3 seconds after the talk closes, with AIInfluence's warning (Tick)
            _fight = new PendingFight { Who = s, Opponent = a, Kind = kind, Wait = kind == "brawl" ? 0.6f : 3f };
            Log.Info($"Pending fight with {s.Name}: {kind}");
            switch (kind)
            {
                case "brawl": return L.F("AIL_8e8232dd", $"{s.Name} se remanga: habrá pelea a puñetazos en cuanto termine la charla.");
                case "death": return L.F("AIL_2d487a5a", $"{s.Name} acepta un duelo a muerte. Empieza en cuanto termine la charla.");
                default: return L.F("AIL_8a331c41", $"{s.Name} acepta el duelo. Empieza en cuanto termine la charla.");
            }
        }

        public static bool Fighting => _fight != null || _active != null;

        public static bool QueueFightWith(Speaker s, Agent opponent, string kind, IEnumerable<Agent> helpers = null)
        {
            if (opponent == null || !opponent.IsActive() || _fight != null || !CanFightHere()) return false;
            _fight = new PendingFight { Who = s, Opponent = opponent, Kind = kind, Wait = 0.4f };
            if (helpers != null) _fight.Helpers.AddRange(helpers.Where(h => h != null && h.IsActive() && h != opponent));
            Log.Info($"Pending fight with {s?.Name}: {kind} (outside a talk)");
            return true;
        }

        private static void StartFight()
        {
            var f = _fight;
            _fight = null;
            try
            {
                var m = Mission.Current;
                var fh = m?.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || fh.IsThereActiveFight() || Agent.Main == null || !Agent.Main.IsActive() || f.Opponent == null || !f.Opponent.IsActive())
                {
                    Log.Warn("The fight could not start (scene changed)");
                    return;
                }
                // Not using the game's StartFistFight: when it ends it gives weapons back without checking the agent is
                // still active, and the engine crashes (after a street fight). We strip and restore weapons by hand.
                List<Stripped> stripped = null;
                var foes = new List<Agent> { f.Opponent };
                foes.AddRange(f.Helpers.Where(h => h != null && h.IsActive() && h != f.Opponent));
                if (f.Kind == "brawl")
                {
                    stripped = new List<Stripped> { Strip(Agent.Main) };
                    foreach (var foe in foes) stripped.Add(Strip(foe));
                }
                Withdrawn.Clear(); TeamBefore.Clear();
                foreach (var foe in foes) TeamBefore[foe] = foe.Team;
                f.Tavern = f.Kind == "brawl" && InTavern();
                if (_foughtIn != m) { Fought.Clear(); _foughtIn = m; }
                foreach (var foe in foes) Fought.Add(foe);
                fh.StartCustomFight(new List<Agent> { Agent.Main }, foes, false, false, won =>
                {
                    try { ReleasePatrons(); } catch (Exception ex) { Log.Error("Release the tavern patrons", ex); }
                    try { Unguard(); } catch (Exception ex) { Log.Error("Heal after friendly fight", ex); }
                    try { if (stripped != null) foreach (var st in stripped) Restore(st); }
                    catch (Exception ex) { Log.Error("Return weapons after the fight", ex); }
                    MainThread.Post(() => OnFightEnded(f, won));
                });
                // Fist fight or friendly duel: nobody dies. The player (and both sides of a duel) yield at half health;
                // the other side of a fist fight is knocked out cold
                if (IsFriendly(f)) Guard(f, new List<Agent> { Agent.Main }.Concat(foes));
                _active = f; _activeIn = m; _waves = 0; _alarm = false; _wave = FirstGuardDelay();
                ActiveFoes.Clear(); ActiveFoes.AddRange(foes); _unleash = 0f;
                Unleash();
                if (f.Tavern) CalmPatrons(f);
                Say(f.Unprovoked ? L.F("AIL_1ee35da7", $"¡Atacas a {f.Who.Name}!") : f.Kind == "brawl" ? L.F("AIL_96bdc93c", $"¡Pelea a puñetazos con {f.Who.Name}!") : f.Kind == "death" ? L.F("AIL_70eda317", $"¡Duelo a muerte con {f.Who.Name}!") : L.F("AIL_e97439d8", $"¡Duelo con {f.Who.Name}!"), Colors.Red);
                if (f.Announced) Banner(L.T("AIL_e7b1d82f", "¡Empieza el duelo!"));
                Log.Info($"Fight started with {f.Who.Name}: {f.Kind}");
                FightBanter.Begin(f.Who, f.Opponent, f.Kind, IsFriendly(f) ? null : foes.Where(x => x != f.Opponent).ToList());
                Say(Hotkeys.Fix(L.T("AIL_e7b1c508", "(Alt+Y: rendirte · Alt+P: pedir tregua)")), Colors.Gray);
            }
            catch (Exception ex) { Log.Error("Start the fight", ex); }
        }

        // ============================================================ friendly fights: no deaths

        // Fist fights (always) and duels that aren't to the death or an armed assault: nobody dies. In a friendly duel
        // whoever drops to 20 health yields and the fight ends (AIInfluence). In a fist
        // fight only the player yields at half: the people they fight with go down for real, knocked out cold on the
        // floor by the engine (DecideState makes sure it is never a death). Afterwards those still standing get back
        // the health they had before. Only a duel to the death (or an assault with steel) can kill.
        private class Guarded
        {
            public Agent Agent;
            public float Start;
            public Agent.MortalityState Prev;
            public bool Yielded;
        }

        private static readonly List<Guarded> GuardedAgents = new List<Guarded>();
        private static PendingFight _friendly;
        private static int _yield;              // 0 = nobody yielded, 1 = opponent yields (you win), 2 = you yield
        private static bool _inHealthFix;
        private const float YieldAt = 0.5f;
        private const float DuelYieldHealth = 20f;

        private static bool IsFriendly(PendingFight f) => f != null && (f.Kind == "brawl" || (f.Kind == "duel" && !f.Unprovoked));

        private static void Guard(PendingFight f, IEnumerable<Agent> agents)
        {
            Unguard();
            _friendly = f; _yield = 0;
            foreach (var a in agents.Distinct())
            {
                try
                {
                    if (a == null || !a.IsActive() || !a.IsHuman) continue;
                    GuardedAgents.Add(new Guarded { Agent = a, Start = a.Health, Prev = a.CurrentMortalityState });
                    a.OnAgentHealthChanged += OnGuardedHealth;
                }
                catch (Exception ex) { Log.Error("Protect friendly fight", ex); }
            }
        }

        /// <summary>Runs right after each hit and before the game checks for death, so health can be fixed here.</summary>
        private static void OnGuardedHealth(Agent a, float oldHealth, float newHealth)
        {
            if (_inHealthFix || newHealth >= oldHealth || _friendly == null) return;
            try
            {
                var g = GuardedAgents.FirstOrDefault(x => x.Agent == a);
                if (g == null) return;
                // Fists: the NPC side is not held up at half health, they take the beating until they drop.
                // Someone the game keeps alive on purpose (not mortal) still yields the old way, or they would never fall
                if (_friendly.Kind == "brawl" && a != Agent.Main && g.Prev == Agent.MortalityState.Mortal) return;
                float h = newHealth;
                // A friendly duel ends when a duelist drops to 20 health (AIInfluence), with blows at full strength;
                // in a fist fight the player yields at half
                float floor = _friendly.Kind == "duel" ? Math.Max(1f, Math.Min(DuelYieldHealth, g.Start - 1f)) : Math.Max(1f, (float)Math.Ceiling(g.Start * YieldAt));
                bool yields = h <= floor;
                if (yields) h = Math.Max(floor, 1f);
                if (Math.Abs(h - newHealth) > 0.01f)
                {
                    _inHealthFix = true;
                    try { a.Health = h; } finally { _inHealthFix = false; }
                }
                if (!yields || g.Yielded) return;
                // Yielded: takes no more damage until the end
                g.Yielded = true;
                a.SetMortalityState(Agent.MortalityState.Immortal);
                if (_yield == 0)
                {
                    if (a == Agent.Main) _yield = 2;
                    else if (a == _friendly.Opponent) _yield = 1;
                }
            }
            catch (Exception ex) { Log.Error("Health in friendly fight", ex); }
        }

        /// <summary>End of a friendly fight: everyone gets back the health (and mortality state) they had before.</summary>
        private static void Unguard()
        {
            foreach (var g in GuardedAgents)
            {
                try
                {
                    var a = g.Agent;
                    if (a == null) continue;
                    a.OnAgentHealthChanged -= OnGuardedHealth;
                    if (a.Mission == null || !a.IsActive()) continue;
                    a.SetMortalityState(g.Prev);
                    if (a.Health < g.Start) a.Health = g.Start;
                }
                catch { }
            }
            GuardedAgents.Clear();
            _friendly = null; _yield = 0;
        }

        /// <summary>Someone yielded in a friendly fight. Closes it with the result, from the tick rather than inside the hit callback.</summary>
        private static void EndFriendlyOnYield()
        {
            int y = _yield;
            _yield = 0;
            try
            {
                var fh = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || !fh.IsThereActiveFight()) { Unguard(); return; }
                Log.Info($"Friendly fight: {(y == 1 ? "rival yields" : "player yields")} at half health");
                fh.EndFight(y == 1);
            }
            catch (Exception ex) { Log.Error("End friendly fight", ex); }
        }

        /// <summary>
        /// Asked by the engine (through FightWatch) when someone's health runs out. Anyone in a friendly fight is knocked
        /// out, never killed. AgentState.None leaves the decision to the game.
        /// </summary>
        internal static AgentState DecideState(Agent a)
        {
            try
            {
                if (a != null && SparedHeroes.Contains(a)) return AgentState.Unconscious;      // a lord who rode in is beaten, never killed
                if (a == null || _friendly == null || GuardedAgents.Count == 0) return AgentState.None;
                return GuardedAgents.Any(g => g.Agent == a) ? AgentState.Unconscious : AgentState.None;
            }
            catch { return AgentState.None; }
        }

        // ============================================================ what fighters decide mid-fight

        internal static bool ActiveIsFriendly => IsFriendly(_active);
        private static bool _asking;

        /// <summary>Ends the fight in progress with that outcome. False if there is none.</summary>
        private static bool EndActive(string ending, bool playerWon)
        {
            try
            {
                var fh = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                if (_active == null || fh == null || !fh.IsThereActiveFight()) return false;
                _active.Ending = ending;
                Log.Info($"[fight] ends: {ending}");
                fh.EndFight(playerWon);
                return true;
            }
            catch (Exception ex) { Log.Error("End the fight", ex); return false; }
        }

        private static void AskPlayer(string title, string text, string yes, string no, Action onYes)
        {
            if (_asking) return;
            _asking = true;
            InformationManager.ShowInquiry(new InquiryData(title, text, true, true, yes, no,
                () => { _asking = false; try { onYes(); } catch (Exception ex) { Log.Error("Fight: player decision", ex); } },
                () => { _asking = false; }), true);
        }

        /// <summary>The opponent gives up. In a friendly fight that ends it. With steel drawn in earnest, the player decides.</summary>
        internal static bool OpponentYields()
        {
            var f = _active;
            if (f == null) return false;
            if (IsFriendly(f)) return EndActive("yielded", true);
            AskPlayer(L.F("AIL_e7b1c509", $"Rendición de {f.Who.Name}"), L.F("AIL_e7b1c4fd", $"{f.Who.Name} se rinde. ¿Aceptas su rendición?"),
                L.T("AIL_e7b1c4fe", "Aceptar"), L.T("AIL_e7b1c4ff", "Seguir luchando"), () => EndActive("yielded", true));
            return true;
        }

        internal static bool OpponentFlees()
        {
            var f = _active;
            if (f == null) return false;
            Say(L.F("AIL_e7b1c501", $"{f.Who.Name} huye de la pelea."), Colors.Yellow);
            return EndActive("fled", true);
        }

        internal static bool OpponentOffersTruce()
        {
            var f = _active;
            if (f == null) return false;
            AskPlayer(L.T("AIL_e7b1c50a", "Tregua"), L.F("AIL_e7b1c500", $"{f.Who.Name} pide una tregua. ¿La aceptas?"),
                L.T("AIL_e7b1c4fe", "Aceptar"), L.T("AIL_e7b1c4ff", "Seguir luchando"), () => EndActive("truce", true));
            return true;
        }

        /// <summary>The nearest people who would stand with them come in on their side. Not in a friendly fight.</summary>
        internal static void OpponentCallsHelp()
        {
            try
            {
                var f = _active; var m = Mission.Current;
                var fh = m?.GetMissionBehavior<MissionFightHandler>();
                if (f == null || fh == null || !fh.IsThereActiveFight() || IsFriendly(f) || f.Opponent == null) return;
                var near = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a.IsAIControlled && a != f.Opponent && a != Agent.Main && !ActiveFoes.Contains(a) && !Withdrawn.Contains(a)
                                               && a.Position.Distance(f.Opponent.Position) <= 18f && IsGuard(a))
                                   .OrderBy(a => a.Position.Distance(f.Opponent.Position)).Take(2).ToList();
                foreach (var a in near)
                    if (JoinFight(fh, a, false)) Say(L.F("AIL_e7b1c507", $"{a.Name} se mete en la pelea de parte de {f.Who.Name}."), Colors.Red);
            }
            catch (Exception ex) { Log.Error("Ask for help in the fight", ex); }
        }

        /// <summary>Someone watching takes a side. Never in a friendly fight, which is between the two of them.</summary>
        internal static void OnlookerStepsIn(bool playerSide, string line)
        {
            try
            {
                var f = _active; var m = Mission.Current;
                var fh = m?.GetMissionBehavior<MissionFightHandler>();
                if (f == null || fh == null || !fh.IsThereActiveFight() || IsFriendly(f) || Agent.Main == null) return;
                var who = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a.IsAIControlled && a != f.Opponent && a != Agent.Main && !ActiveFoes.Contains(a) && !Withdrawn.Contains(a)
                                              && a.MountAgent == null && a.Position.Distance(Agent.Main.Position) <= 15f)
                                  .OrderBy(a => a.Position.Distance(Agent.Main.Position)).FirstOrDefault();
                if (who == null || !JoinFight(fh, who, playerSide)) return;
                if (!string.IsNullOrWhiteSpace(line)) { try { UI.Bubbles.Show(who, line, 4f); } catch { } Say($"{who.Name}: {line}", Colors.Yellow); }
                Say(L.F("AIL_e7b1c507", $"{who.Name} se mete en la pelea de parte de {(playerSide ? P.Name.ToString() : f.Who.Name)}."), playerSide ? Colors.Green : Colors.Red);
            }
            catch (Exception ex) { Log.Error("Onlooker joins the fight", ex); }
        }

        // The fight handler keeps each side in a private list, and the team each fighter had before in a private table
        private static readonly System.Reflection.FieldInfo FOpponentSide = HarmonyLib.AccessTools.Field(typeof(MissionFightHandler), "_opponentSideAgents");
        private static readonly System.Reflection.FieldInfo FOpponentOld = HarmonyLib.AccessTools.Field(typeof(MissionFightHandler), "_opponentSideAgentsOldTeamData");
        private static readonly System.Reflection.FieldInfo FPlayerOld = HarmonyLib.AccessTools.Field(typeof(MissionFightHandler), "_playerSideAgentsOldTeamData");

        /// <summary>
        /// For someone added mid-fight the handler notes down the fight team as the one to give back, so they would
        /// stay on that team when it ends. Writes the team they really had.
        /// </summary>
        private static void RememberTeam(MissionFightHandler fh, Agent a, bool playerSide, Team before)
        {
            try
            {
                if (before == null) return;
                var table = (playerSide ? FPlayerOld : FOpponentOld)?.GetValue(fh) as Dictionary<Agent, Team>;
                if (table != null && table.ContainsKey(a)) table[a] = before;
            }
            catch (Exception ex) { Log.Error("Save previous equipment", ex); }
        }

        /// <summary>
        /// One of those fighting beside the opponent lowers their weapon and steps back, or turns on the opponent.
        /// who: their name, or "anyone" for the nearest. Never in a friendly fight, which is one against one.
        /// </summary>
        internal static void HelperWavers(string who, bool turns, string line)
        {
            try
            {
                var f = _active; var m = Mission.Current;
                var fh = m?.GetMissionBehavior<MissionFightHandler>();
                if (f == null || fh == null || !fh.IsThereActiveFight() || IsFriendly(f) || !S.Get.FightHelpersWaver || Agent.Main == null || !Agent.Main.IsActive()) return;
                if (f.Opponent == null || !f.Opponent.IsActive()) return;
                // The opponent stays in: the handler only ends a fight when the last one of a side falls, never when a side is emptied by hand
                var side = FOpponentSide?.GetValue(fh) as List<Agent>;
                if (side == null || !side.Contains(f.Opponent)) return;
                var pool = ActiveFoes.Where(a => a != null && a != f.Opponent && a.IsHuman && a.IsActive() && a.IsAIControlled && side.Contains(a)).ToList();
                who = (who ?? "").Trim();
                Agent h = who.Length < 2 || who.Equals("anyone", StringComparison.OrdinalIgnoreCase)
                    ? pool.OrderBy(a => a.Position.Distance(Agent.Main.Position)).FirstOrDefault()
                    : pool.FirstOrDefault(a => string.Equals(a.Name, who, StringComparison.OrdinalIgnoreCase))
                      ?? pool.FirstOrDefault(a => a.Name.IndexOf(who, StringComparison.OrdinalIgnoreCase) >= 0 || who.IndexOf(a.Name, StringComparison.OrdinalIgnoreCase) >= 0);
                if (h == null) return;
                TeamBefore.TryGetValue(h, out var before);
                if (before == null && !turns) return;      // nowhere to put them back

                side.Remove(h);
                ActiveFoes.Remove(h); Withdrawn.Add(h);
                try { h.ResetEnemyCaches(); h.InvalidateTargetAgent(); h.InvalidateAIWeaponSelections(); } catch { }
                if (!string.IsNullOrWhiteSpace(line)) { try { UI.Bubbles.Show(h, line, 4f); } catch { } Say($"{h.Name}: {line}", Colors.Yellow); }

                if (turns)
                {
                    f.Helpers.Remove(h);
                    fh.AddAgentToSide(h, true);
                    RememberTeam(fh, h, true, before);
                    Say(L.F("AIL_e7b1c646", $"{h.Name} cambia de bando y lucha a tu lado."), Colors.Green);
                    Log.Info($"[fight] {h.Name} switches sides");
                    return;
                }
                // Out of the fight: what the handler does for everyone when it ends, done now for this one
                try
                {
                    var g = h.GetComponent<SandBox.CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<SandBox.Missions.AgentBehaviors.AlarmedBehaviorGroup>();
                    if (g != null) { g.DisableScriptedBehavior(); g.DisableCalmDown = false; }
                }
                catch (Exception ex) { Log.Error("Calm down the one leaving the fight", ex); }
                h.SetWatchState(Agent.WatchState.Patrolling);
                h.SetTeam(new Team(before.MBTeam, BattleSideEnum.None, m), true);
                try
                {
                    h.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.WithAnimation);
                    h.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.WithAnimation);
                }
                catch { }
                SceneActions.WalkAway(h);
                Say(L.F("AIL_e7b1c645", $"{h.Name} deja la pelea."), Colors.Yellow);
                Log.Info($"[fight] {h.Name} leaves the fight");
            }
            catch (Exception ex) { Log.Error("Helper leaving the fight or switching sides", ex); }
        }

        /// <summary>Alt+Y: the player yields. Alt+P: the player asks to stop as equals. The opponent answers as they decided.</summary>
        private static void PlayerFightKeys()
        {
            try
            {
                if (_active == null || _asking) return;
                bool yield = Hotkeys.Pressed(Hotkeys.Id.Yield), parley = !yield && Hotkeys.Pressed(Hotkeys.Id.Truce);
                if (!yield && !parley) return;
                var fh = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || !fh.IsThereActiveFight()) return;
                string name = _active.Who.Name;
                if (yield)
                {
                    Say(L.T("AIL_e7b1c502", "Te rindes."), Colors.Yellow);
                    if (FightBanter.AcceptsYield) { FightBanter.Shout(FightBanter.YieldAnswer); EndActive("player_yielded", false); }
                    else { FightBanter.Shout(FightBanter.YieldAnswer); Say(L.F("AIL_e7b1c503", $"{name} no acepta tu rendición."), Colors.Red); }
                }
                else
                {
                    Say(L.T("AIL_e7b1c504", "Pides una tregua."), Colors.Yellow);
                    if (FightBanter.AcceptsTruce) { FightBanter.Shout(FightBanter.TruceAnswer); Say(L.F("AIL_e7b1c505", $"{name} acepta la tregua."), Colors.Green); EndActive("truce", true); }
                    else { FightBanter.Shout(FightBanter.TruceAnswer); Say(L.F("AIL_e7b1c506", $"{name} no quiere tregua."), Colors.Red); }
                }
            }
            catch (Exception ex) { Log.Error("Fight keys", ex); }
        }

        // ============================================================ hitting someone out of nowhere

        /// <summary>New or loaded campaign: drop anything pending from the previous one.</summary>
        public static void ResetSession()
        {
            _fight = null; _active = null; _surrender = null; _surrenderParty = null; KeepEncounter = false; _autoAttack = 0f; _pulled.Clear();
            _struck = null; _watched = null; _foughtIn = null; _quietUntil = 0f; Fought.Clear();
            try { ReleasePatrons(); } catch { Calmed.Clear(); Watching.Clear(); }
            try { StopBells(); } catch { _bells = null; }
            try { Unguard(); } catch { GuardedAgents.Clear(); _friendly = null; _yield = 0; }
            _bandJoin = null;
            ToKill.Clear(); RoleDeaths.Clear(); ToFade.Clear(); ActiveFoes.Clear(); Summoned.Clear(); SummonedFrom.Clear(); Withdrawn.Clear(); TeamBefore.Clear();
            Coming.Clear(); SparedHeroes.Clear(); _reserve = 0; _ordersOn = false; _ordersIn = null; _summonHinted = false; _mapLordsAsked = false; _carried = false; _captured = false;
        }

        /// <summary>Fight that starts by itself when a scene opens (a duel agreed on the map).</summary>
        public static void QueueSceneFight(Speaker s, Agent opponent, string kind, float wait)
        {
            if (s == null || opponent == null) return;
            _fight = new PendingFight { Who = s, Opponent = opponent, Kind = kind, Wait = wait };
            Log.Info($"Fight in the map scene with {s.Name}: {kind}");
        }

        // A band that agreed to join: once the talk and the encounter are over, its men move to the player's party
        private static MobileParty _bandJoin;
        public static bool BandJoinPending => _bandJoin != null;
        public static void QueueBandJoin(MobileParty band) { if (band != null) _bandJoin = band; }

        private static void TickBandJoin()
        {
            var b = _bandJoin;
            try
            {
                if (b == null || !b.IsActive) { _bandJoin = null; return; }
                if (b.MapEvent != null) return;      // caught in a battle: they still join once it is over
                // The meeting has to be closed first, or the game still treats them as the party in front of you
                if (PlayerEncounter.Current != null)
                {
                    try { PlayerEncounter.LeaveEncounter = true; PlayerEncounter.Finish(true); } catch (Exception ex) { Log.Warn("Close the encounter with the band: " + ex.Message); }
                    if (PlayerEncounter.Current != null) return;      // next frame
                }
                _bandJoin = null;
                var main = MobileParty.MainParty;
                int joined = 0;
                foreach (var e in b.MemberRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null || e.Character.IsHero || e.Number <= 0) continue;
                    main.MemberRoster.AddToCounts(e.Character, e.Number, false, e.WoundedNumber);
                    joined += e.Number;
                }
                // Whoever they were holding: ordinary captives change hands, named ones walk free
                foreach (var e in b.PrisonRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null) continue;
                    if (e.Character.IsHero) { try { if (e.Character.HeroObject.IsPrisoner) EndCaptivityAction.ApplyByEscape(e.Character.HeroObject); } catch { } }
                    else if (e.Number > 0) main.PrisonRoster.AddToCounts(e.Character, e.Number);
                }
                int purse = Math.Max(0, b.PartyTradeGold);
                if (purse > 0) P.ChangeHeroGold(purse);
                string name = b.Name?.ToString() ?? "";
                DestroyPartyAction.Apply(null, b);
                Log.Info($"Band {name} joins the party: {joined} men, {purse} denars");
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1c4fc", $"{joined} hombres de {name} marchan ahora contigo."), Colors.Green));
            }
            catch (Exception ex) { Log.Error("Join the band to the party", ex); _bandJoin = null; }
        }

        // Roleplay deaths: applied once the chat closes and the scene ends
        private static readonly List<(Hero who, string kind, string how, string found)> RoleDeaths = new List<(Hero, string, string, string)>();

        /// <summary>kind: executed, killed (by the player's hand), suicide, poison, wounds, illness, accident.</summary>
        public static void QueueDeath(Hero h, string kind, string how, string found)
        {
            if (h == null || RoleDeaths.Any(d => d.who == h)) return;
            RoleDeaths.Add((h, kind ?? "suicide", how ?? "", found ?? ""));
            Log.Info($"Role-play death pending: {h.Name} ({kind}: {how}){(string.IsNullOrWhiteSpace(found) ? "" : " — found: " + found)}");
        }

        private static string DeathInEnglish(string kind, string victim, string killer)
        {
            switch (kind)
            {
                case "executed": return $"{victim} was executed by {killer}";
                case "killed": return $"{victim} was killed by {killer}";
                case "suicide": return $"{victim} took their own life";
                case "poison": return $"{victim} died of poison";
                case "wounds": return $"{victim} died of their wounds";
                case "illness": return $"{victim} died of illness";
                default: return $"{victim} died in an accident";
            }
        }

        /// <summary>A sentence the character wrote, tidied to stand on its own: no stray quotes, a capital, a full stop.</summary>
        private static string AsSentence(string t, bool capital)
        {
            t = (t ?? "").Replace("\r", " ").Replace("\n", " ").Trim().Trim('"', '«', '»', '“', '”', ' ').TrimEnd('.', ',', ';', ':').Trim();
            if (t.Length == 0) return "";
            if (capital) t = char.ToUpper(t[0]) + t.Substring(1);
            return t;
        }

        /// <summary>The death as the player reads it, in their language: the fact, how it went, and what was found.</summary>
        private static string DeathForPlayer(string kind, string victim, string how, string found)
        {
            string fact;
            switch (kind)
            {
                case "executed": fact = L.F("AIL_e7b1c788", $"Se ha cumplido la ejecución de {victim}, por orden tuya."); break;
                case "killed": fact = L.F("AIL_e7b1c789", $"{victim} ha muerto por tu mano."); break;
                case "suicide": fact = L.F("AIL_e7b1c78a", $"{victim} se ha quitado la vida."); break;
                case "poison": fact = L.F("AIL_e7b1c78b", $"{victim} ha muerto por el veneno."); break;
                case "wounds": fact = L.F("AIL_e7b1c78c", $"{victim} ha muerto de sus heridas."); break;
                case "illness": fact = L.F("AIL_e7b1c78d", $"{victim} ha muerto de enfermedad."); break;
                default: fact = L.F("AIL_e7b1c78e", $"{victim} ha muerto en un accidente."); break;
            }
            var sb = new System.Text.StringBuilder(fact);
            string h = AsSentence(how, true), f = AsSentence(found, false);
            if (h.Length > 2) sb.Append("\n\n").Append(h).Append('.');
            if (f.Length > 2) sb.Append("\n\n").Append(L.F("AIL_e7b1c78f", $"Junto al cuerpo se encuentra: {f}. Es parte del relato: no se añade nada a tu inventario."));
            return sb.ToString();
        }

        // Whoever leaves with you fades out of the scene when the talk ends
        private static readonly List<Agent> ToFade = new List<Agent>();
        public static void FadeAfterTalk(Agent a) { if (a != null && !ToFade.Contains(a)) ToFade.Add(a); }

        // ============================================================ who fought here, and the quiet after it

        private static readonly HashSet<Agent> Fought = new HashSet<Agent>();
        private static Mission _foughtIn;
        private static float _quietUntil;

        /// <summary>Took part in a fight with the player in this scene: idle remarks and small talk should leave them alone.</summary>
        public static bool FoughtHere(Agent a)
        {
            try { return a != null && _foughtIn != null && _foughtIn == Mission.Current && Fought.Contains(a); }
            catch { return false; }
        }

        /// <summary>A fight with the player ended moments ago in this scene: nobody around is in the mood for idle remarks.</summary>
        public static bool QuietAfterFight
        {
            get
            {
                try { var m = Mission.Current; return m != null && m == _foughtIn && (_active != null || m.CurrentTime < _quietUntil); }
                catch { return false; }
            }
        }

        // ============================================================ tavern patrons watch a fist fight

        // Patrons we took the alarm away from (with the flags they had), and those we turned to watch
        private static readonly Dictionary<Agent, AgentFlag> Calmed = new Dictionary<Agent, AgentFlag>();
        private static readonly HashSet<Agent> Watching = new HashSet<Agent>();

        private static bool IsFighter(Agent a)
        {
            var f = _active;
            return f != null && (a == f.Opponent || ActiveFoes.Contains(a) || f.Helpers.Contains(a) || Summoned.Contains(a));
        }

        /// <summary>
        /// Asked by the game's alarm logic (TavernCalmPatch): during our fist fight in a tavern, nobody but the fighters
        /// treats the player as a danger, so the patrons stay at their tables instead of running.
        /// </summary>
        internal static bool KeepCalm(Agent a)
        {
            try
            {
                var f = _active;
                if (f == null || !f.Tavern || a == null || a == Agent.Main) return false;
                return !IsFighter(a);
            }
            catch { return false; }
        }

        /// <summary>The fist fight in the tavern starts: the patrons lose the urge to flee, and those close by turn to watch.</summary>
        private static void CalmPatrons(PendingFight f)
        {
            try
            {
                ReleasePatrons();
                var m = Mission.Current; var me = Agent.Main;
                if (m == null || me == null) return;
                foreach (var a in m.Agents)
                {
                    try
                    {
                        if (a == null || a == me || !a.IsHuman || !a.IsActive() || !a.IsAIControlled || IsFighter(a)) continue;
                        float d = a.Position.Distance(me.Position);
                        if (d > 40f) continue;
                        var flags = a.GetAgentFlags();
                        Calmed[a] = flags;
                        if ((flags & AgentFlag.CanGetAlarmed) != 0) a.SetAgentFlags(flags & ~AgentFlag.CanGetAlarmed);
                        if (!a.IsAlarmStateNormal())
                        {
                            a.SetWatchState(Agent.WatchState.Patrolling);
                            a.GetComponent<SandBox.CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<SandBox.Missions.AgentBehaviors.AlarmedBehaviorGroup>()?.DisableScriptedBehavior();
                        }
                        if (d <= 12f) { a.SetLookAgent(me); Watching.Add(a); }
                    }
                    catch { }
                }
                Log.Info($"[fight] tavern brawl: {Calmed.Count} patrons stay and watch");
            }
            catch (Exception ex) { Log.Error("Calm the tavern patrons", ex); }
        }

        /// <summary>The fight is over (or the scene changed): the patrons get back their alarm and stop staring.</summary>
        private static void ReleasePatrons()
        {
            var m = Mission.Current;
            foreach (var kv in Calmed)
            {
                try
                {
                    var a = kv.Key;
                    if (a == null || m == null || a.Mission != m || !a.IsActive()) continue;
                    if ((kv.Value & AgentFlag.CanGetAlarmed) != 0) a.SetAgentFlags(a.GetAgentFlags() | AgentFlag.CanGetAlarmed);
                }
                catch { }
            }
            foreach (var a in Watching)
            {
                try { if (a != null && m != null && a.Mission == m && a.IsActive()) a.SetLookAgent(null); }
                catch { }
            }
            Calmed.Clear(); Watching.Clear();
        }

        // ============================================================ the first blow starts it

        // The player's first blow at a bystander, seen by FightWatch during the engine's hit callback. The fight is set up
        // on the next tick, outside the engine callback.
        private static Agent _struck;
        private static bool _struckArmed;
        private static Mission _watched;
        // Bystanders the player hits while a fight is already going: they are dragged into it on the next tick
        private static readonly List<Agent> _pulled = new List<Agent>();

        /// <summary>During a fight of ours, a blow at someone else in the scene lands too and brings them into it.</summary>
        private static bool PullInAllowed(Mission m, Agent victim)
        {
            if (m == null || Campaign.Current == null || _active == null) return false;
            var cfg = S.Get;
            if (!cfg.AllowCombat || !cfg.AllowStreetViolence || Settlement.CurrentSettlement == null) return false;
            if (Campaign.Current.ConversationManager?.IsConversationInProgress == true) return false;
            var fh = m.GetMissionBehavior<MissionFightHandler>();
            return fh != null && fh.IsThereActiveFight() && StreetTarget(victim);
        }

        /// <summary>Settlement scene, nothing else going on, and the settings allow it: the player may pick a fight by hitting someone.</summary>
        private static bool StreetFightAllowed(Mission m)
        {
            if (m == null || Campaign.Current == null) return false;
            var cfg = S.Get;
            if (!cfg.AllowCombat || !cfg.AllowStreetViolence) return false;
            var me = Agent.Main;
            if (me == null || !me.IsActive() || me.MountAgent != null || m.Mode != MissionMode.StartUp) return false;
            if (Settlement.CurrentSettlement == null) return false;
            string loc = CampaignMission.Current?.Location?.StringId;
            if (string.IsNullOrEmpty(loc) || loc == "arena") return false;
            if (Campaign.Current.ConversationManager?.IsConversationInProgress == true) return false;
            var fh = m.GetMissionBehavior<MissionFightHandler>();
            return fh != null && !fh.IsThereActiveFight();
        }

        /// <summary>A grown-up townsperson on foot who is neither the player's enemy already nor one of their own.</summary>
        private static bool StreetTarget(Agent a)
        {
            var me = Agent.Main;
            if (a == null || me == null || a == me || !a.IsHuman || a.IsMount || !a.IsActive() || !a.IsAIControlled) return false;
            if (!(a.Character is CharacterObject c) || c.Age < 18f || a.Age < 18f) return false;
            // Enemies already take real blows from the game; the player's own men (same team) are not picked on
            if (a.IsEnemyOf(me) || (a.Team != null && me.Team != null && a.Team == me.Team)) return false;
            var h = c.HeroObject;
            if (h != null && (h == P || h.Clan == Clan.PlayerClan || h.IsPlayerCompanion)) return false;
            return true;
        }

        /// <summary>
        /// Asked by FirstHitPatch for every blow the player lands on someone the game would treat as a friend (it cancels
        /// those: no damage, and the player is stunned). True lets the first blow at a bystander land for real.
        /// </summary>
        internal static bool LetPlayerHit(Mission m, Agent attacker, Agent victim)
        {
            try
            {
                if (attacker == null || attacker != Agent.Main || !attacker.IsPlayerControlled || victim == null) return false;
                if (_active != null) return PullInAllowed(m, victim);
                // Between the first blow and the next tick the fight is not set up yet: further blows at them land too
                if (_struck != null) return victim == _struck;
                if (_fight != null) return false;
                return StreetFightAllowed(m) && StreetTarget(victim);
            }
            catch { return false; }
        }

        /// <summary>The engine reports a melee blow from the player. The first one at a bystander picks the fight.</summary>
        internal static void OnPlayerHit(Agent victim, AttackCollisionData cd)
        {
            try
            {
                if (cd.IsMissile) return;
                if (_active != null)
                {
                    if (PullInAllowed(Mission.Current, victim) && !_pulled.Contains(victim)) { _pulled.Add(victim); Log.Info($"[scene] {victim.Name} struck during the fight: pulled in"); }
                    return;
                }
                if (_struck != null || _fight != null) return;
                var m = Mission.Current;
                if (!StreetFightAllowed(m) || !StreetTarget(victim)) return;
                _struck = victim;
                // Steel in hand counts only if the blow came from it; a kick or a shield bash is still a scuffle
                _struckArmed = cd.AffectorWeaponSlotOrMissileIndex >= 0 && !cd.IsAlternativeAttack;
                Log.Info($"[scene] first blow at {victim.Name} ({(_struckArmed ? "weapon" : "bare hands")})");
            }
            catch (Exception ex) { Log.Error("First blow", ex); }
        }

        /// <summary>Puts our hit watcher into each settlement scene once it is running.</summary>
        private static void EnsureWatch()
        {
            try
            {
                var m = Mission.Current;
                if (m == null || m == _watched || m.CurrentState != Mission.State.Continuing || Agent.Main == null) return;
                _watched = m;
                if (m.GetMissionBehavior<MissionFightHandler>() == null || m.GetMissionBehavior<Patches.FightWatch>() != null) return;
                m.AddMissionBehavior(new Patches.FightWatch());
            }
            catch (Exception ex) { Log.Error("Watch blows in the scene", ex); }
        }

        /// <summary>
        /// The blow landed on the last frame: a fist fight if it was bare hands, a real one if it came from a weapon.
        /// Their kin nearby step in, and if steel is drawn, so do nearby guards.
        /// </summary>
        /// <summary>Those the player hit in the middle of a fight join it against the player.</summary>
        private static void PullIn()
        {
            var list = _pulled.ToList(); _pulled.Clear();
            try
            {
                var fh = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || !fh.IsThereActiveFight() || _active == null) return;
                foreach (var a in list)
                    if (a != null && a.IsActive() && !ActiveFoes.Contains(a) && JoinFight(fh, a, false))
                        Say(L.F("AIL_e7b1d300", $"{a.Name} se ve metido en la pelea."), Colors.Red);
            }
            catch (Exception ex) { Log.Error("Pull into the fight", ex); }
        }

        private static void TakeFirstBlow()
        {
            var victim = _struck; bool armed = _struckArmed;
            _struck = null;
            try
            {
                var m = Mission.Current; var me = Agent.Main;
                if (m == null || me == null || !me.IsActive() || victim == null || (victim.Mission != null && victim.Mission != m) || _fight != null || _active != null) return;
                if (!(victim.Character is CharacterObject vc)) return;
                var cfg = S.Get;
                var sp = vc.IsHero && vc.HeroObject != null ? Speaker.FromHero(vc.HeroObject) : Speaker.FromResident(vc, victim);
                var f = new PendingFight { Who = sp, Opponent = victim, Kind = armed ? "duel" : "brawl", Wait = 0f, Unprovoked = true };
                if (!victim.IsActive())
                {
                    // Cut down with the first blow: nobody left to fight, only what it costs
                    f.Ending = "struck_down";
                    Log.Info($"[scene] the player cuts down {sp.Name} with the first blow");
                    OnFightEnded(f, true);
                    return;
                }
                // Defenders: their household nearby, plus nearby guards if steel is drawn
                foreach (var a in m.Agents)
                {
                    if (f.Helpers.Count >= cfg.StreetFightHelpers) break;
                    if (a == null || a == me || a == victim || !a.IsHuman || !a.IsActive() || !a.IsAIControlled || !(a.Character is CharacterObject c) || c.Age < 18f) continue;
                    float dist = a.Position.Distance(victim.Position);
                    bool kin = vc.IsHero && c.IsHero && c.HeroObject?.Clan != null && c.HeroObject.Clan == vc.HeroObject?.Clan && c.HeroObject.Clan != Clan.PlayerClan && dist <= 12f;
                    bool guard = armed && dist <= 20f && (c.Occupation == Occupation.Guard || c.Occupation == Occupation.Soldier || c.Occupation == Occupation.PrisonGuard);
                    if (!kin && !guard) continue;
                    f.Helpers.Add(a);
                    if (guard) Yell(a);
                    Say(L.F("AIL_9ccbdf55", $"{a.Name} sale en defensa de {sp.Name}."), Colors.Red);
                }
                if (armed) { try { victim.MakeVoice(SkinVoiceManager.VoiceType.Fear, SkinVoiceManager.CombatVoiceNetworkPredictionType.NoPrediction); } catch { } }
                Log.Info($"[scene] the player strikes {sp.Name} without a word ({f.Kind}; {f.Helpers.Count} defending them)");
                if (armed && cfg.StreetSummonTroops) Say(Hotkeys.Fix(L.T("AIL_c4f81902", "Puedes llamar a tus hombres: Ctrl+X (diez) o Ctrl+Alt+X (todos los que quepan). Solo en la calle.")), Colors.Gray);
                _fight = f;
                StartFight();
            }
            catch (Exception ex) { Log.Error("Street fight", ex); }
        }

        // ============================================================ the alarm, heard and not only read

        private const string BellsEvent = "event:/mission/ambient/detail/warning_bells";      // looping: stopped by hand
        private const string HornEvent = "event:/alerts/horns/reinforcements";                // one-shot, 2D
        private static TaleWorlds.Engine.SoundEvent _bells;
        private static Mission _bellsIn;
        private static float _bellsLeft;

        private static void Yell(Agent a)
        {
            try { if (a != null && a.IsActive()) a.MakeVoice(SkinVoiceManager.VoiceType.Yell, SkinVoiceManager.CombatVoiceNetworkPredictionType.NoPrediction); }
            catch { }
        }

        /// <summary>The settlement raises the alarm: the big banner, the bells over the rooftops, the street hears it.</summary>
        private static void RingAlarm(string text)
        {
            var m = Mission.Current; var me = Agent.Main;
            Say(text, Colors.Red);
            try { MBInformationManager.AddQuickInformation(new TaleWorlds.Localization.TextObject(text), 0, null, null, ""); }
            catch (Exception ex) { Log.Error("Alarm banner", ex); }
            if (m == null || me == null) return;
            try
            {
                StopBells();
                var ev = TaleWorlds.Engine.SoundEvent.CreateEventFromString(BellsEvent, m.Scene);
                if (ev != null && ev.IsValid)
                {
                    // Up above the street, as from a bell tower: heard all over, wherever the player runs
                    ev.SetPosition(me.Position + new Vec3(0f, 0f, 18f));
                    ev.Play();
                    _bells = ev; _bellsIn = m; _bellsLeft = 15f;
                }
                TaleWorlds.Engine.SoundEvent.PlaySound2D(HornEvent);
            }
            catch (Exception ex) { Log.Error("Alarm sound", ex); }
            try
            {
                // Everyone within earshot of the bells takes fright or comes running
                var pos = me.Position;
                m.AddSoundAlarmFactorToAgents(me, in pos, 40f);
            }
            catch (Exception ex) { Log.Error("Alarm in the street", ex); }
        }

        private static void StopBells()
        {
            var ev = _bells;
            _bells = null;
            if (ev == null) return;
            // Only touch the sound while its scene is alive; a closed scene takes its sounds with it
            if (_bellsIn != null && _bellsIn == Mission.Current) { try { ev.Stop(); ev.Release(); } catch { } }
            _bellsIn = null;
        }

        private static void TickBells(float dt)
        {
            if (_bells == null) return;
            if (Mission.Current != _bellsIn) { _bells = null; _bellsIn = null; return; }
            if ((_bellsLeft -= dt) <= 0f) StopBells();
        }

        // ============================================================ the fight grows: guard waves, alarm, your men

        private static PendingFight _active;
        private static Mission _activeIn;
        private static float _wave;
        private static int _waves;
        private static bool _alarm;
        private static readonly List<Agent> Summoned = new List<Agent>();
        private static float _crowd;

        private static bool InTavern()
        {
            try { return TaleWorlds.CampaignSystem.CampaignMission.Current?.Location?.StringId == "tavern"; } catch { return false; }
        }

        /// <summary>
        /// The people around a fight. In a tavern a fist fight is part of the evening: nobody runs, and anyone the game alarms
        /// is calmed. In the street, or with steel drawn, ordinary folk near it take fright and get away. Guards, heroes and
        /// the fighters are left alone.
        /// </summary>
        private static void Crowd()
        {
            try
            {
                var m = Mission.Current;
                if (m == null || Agent.Main == null || _active == null) return;
                var fh = m.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || !fh.IsThereActiveFight()) return;
                // fists in a tavern are a show (TavernCalmPatch keeps the game from alarming the patrons; this catches
                // anyone alarmed some other way); steel drawn anywhere sends ordinary people running
                bool tavern = _active.Tavern;
                var here = Agent.Main.Position;
                foreach (var a in m.Agents)
                {
                    if (a == null || !a.IsActive() || !a.IsHuman || a == Agent.Main || !a.IsAIControlled) continue;
                    if (a == _active.Opponent || ActiveFoes.Contains(a) || _active.Helpers.Contains(a) || IsGuard(a)) continue;
                    if (a.Team != null && Agent.Main.Team != null && a.Team == Agent.Main.Team) continue;
                    if (a.Character is CharacterObject c && c.IsHero) continue;
                    if (a.Position.Distance(here) > (tavern ? 40f : 18f)) continue;
                    if (tavern)
                    {
                        if (a.IsAlarmStateNormal()) continue;
                        a.SetAlarmState(Agent.AIStateFlag.None);
                        try
                        {
                            var g = a.GetComponent<SandBox.CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<SandBox.Missions.AgentBehaviors.AlarmedBehaviorGroup>();
                            g?.DisableScriptedBehavior();
                        }
                        catch { }
                    }
                    else if (a.IsAlarmStateNormal()) a.SetAlarmState(Agent.AIStateFlag.Alarmed);
                }
            }
            catch (Exception ex) { Log.Error("People around the fight", ex); }
        }

        private static bool IsGuard(Agent a)
        {
            var c = a.Character as CharacterObject;
            return c != null && !c.IsHero && (c.Occupation == Occupation.Guard || c.Occupation == Occupation.Soldier || c.Occupation == Occupation.PrisonGuard);
        }

        /// <summary>Adds someone to the current fight on either side and makes them actually fight.</summary>
        private static bool JoinFight(MissionFightHandler fh, Agent a, bool playerSide)
        {
            try
            {
                if (a == null || !a.IsActive()) return false;
                var before = a.Team;
                fh.AddAgentToSide(a, playerSide);
                RememberTeam(fh, a, playerSide, before);
                if (!TeamBefore.ContainsKey(a)) TeamBefore[a] = before;
                var nav = a.GetComponent<SandBox.CampaignAgentComponent>()?.AgentNavigator;
                if (nav != null)
                {
                    var g = nav.GetBehaviorGroup<SandBox.Missions.AgentBehaviors.AlarmedBehaviorGroup>();
                    if (g != null) { g.DisableCalmDown = true; g.AddBehavior<SandBox.Missions.AgentBehaviors.FightBehavior>(); g.SetScriptedBehavior<SandBox.Missions.AgentBehaviors.FightBehavior>(); }
                }
                if (!playerSide) { ActiveFoes.Add(a); if (_active != null && !_active.Helpers.Contains(a)) _active.Helpers.Add(a); }
                if (_foughtIn == Mission.Current) Fought.Add(a);
                return true;
            }
            catch (Exception ex) { Log.Error("Add to the fight", ex); return false; }
        }

        /// <summary>During an armed assault by the player, guards arrive in waves. With many down, the alarm goes off.</summary>
        private static void GrowFight(float dt)
        {
            var f = _active;
            var m = Mission.Current;
            if (f == null || m == null || Agent.Main == null) return;
            var fh = m.GetMissionBehavior<MissionFightHandler>();
            if (fh == null || !fh.IsThereActiveFight()) return;
            var cfg = S.Get;

            // Ctrl+X calls your troops ten at a time, Ctrl+Alt+X calls as many as fit (keys set in MCM)
            // No troops in a fist fight. In a duel agreed in a talk, calling them breaks its terms: it becomes a battle
            // (nobody is spared any more) and the betrayal is remembered, as in AIInfluence
            if (cfg.StreetSummonTroops && f.Kind != "brawl")
            {
                HintSummon();
                bool all = Hotkeys.Pressed(Hotkeys.Id.SummonAll);
                if (all || Hotkeys.Pressed(Hotkeys.Id.SummonTen))
                {
                    int before = Summoned.Count;
                    SummonTroops(fh, 10, all);
                    if (Summoned.Count > before && !f.Unprovoked && !f.Betrayed) BetrayDuel(f);
                }
                TickReserve(fh);
            }
            TickComing(fh, f);

            if (!f.Unprovoked || f.Kind == "brawl" || !cfg.StreetGuardWaves) return;
            if ((_wave -= dt) > 0f) return;
            _wave = cfg.StreetGuardWaveSeconds;
            int inFight = f.Helpers.Count(h => h != null && h.IsActive());
            int down = f.Helpers.Count(h => h != null && !h.IsActive());
            if (f.Helpers.Count >= cfg.StreetMaxGuards) return;
            var here = Agent.Main.Position;
            var come = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a.IsAIControlled && a != f.Opponent && !f.Helpers.Contains(a) && !Summoned.Contains(a) && !Withdrawn.Contains(a) && IsGuard(a)
                                           && a.Position.Distance(here) <= (_alarm ? 150f : 45f))
                               .OrderBy(a => a.Position.Distance(here)).Take(WaveSize()).ToList();
            if (!_alarm && (down >= 3 || (down >= 1 && _waves >= 2)))
            {
                _alarm = true;
                RingAlarm(L.F("AIL_2f7a736d", $"¡Suena la campana de alarma en {Settlement.CurrentSettlement?.Name}: acude toda la guardia!"));
                VisitNews.AlarmRung();
                CallMapLords();
            }
            // No guard left in the street: the alarm brings men from the real garrison and from the lords inside the walls
            if (come.Count == 0) { if (_alarm && cfg.StreetGarrisonReinforcements) SpawnDefenders(fh, f, Math.Min(WaveSize(), cfg.StreetMaxGuards - f.Helpers.Count)); return; }
            int joined = 0;
            foreach (var a in come)
            {
                if (!JoinFight(fh, a, false)) continue;
                joined++;
                Yell(a);      // they come shouting, not in silence
            }
            if (joined > 0)
            {
                _waves++;
                VisitNews.GuardsCame(joined);
                Say(_waves == 1 ? L.T("AIL_9f068a43", "La guardia ha oído la pelea y viene a por ti.") : L.F("AIL_5336141e", $"Llegan {joined} guardias más."), Colors.Red);
                Unleash();
            }
        }

        /// <summary>Guards per wave: the first as set (8), the next half as many again (12), within the cap.</summary>
        private static int WaveSize() { int n = S.Get.StreetGuardWaveSize; return _waves == 0 ? n : (int)Math.Ceiling(n * 1.5); }

        // Soldiers brought in from a garrison or a lord's party, with the roster they came from, to settle the count afterwards
        private static readonly List<(Agent agent, MobileParty from)> Defenders = new List<(Agent, MobileParty)>();

        /// <summary>
        /// Men of the settlement's real garrison, then of the lords of its realm who are inside, run up from further down the
        /// street. Only outdoors. What happens to them counts: the dead leave their roster, the fallen are wounded there.
        /// </summary>
        private static void SpawnDefenders(MissionFightHandler fh, PendingFight f, int want)
        {
            try
            {
                if (want <= 0) return;
                var m = Mission.Current; var st = Settlement.CurrentSettlement; var me = Agent.Main;
                if (m == null || st == null || me == null) return;
                string loc = "";
                try { loc = CampaignMission.Current?.Location?.StringId ?? ""; } catch { }
                if (loc != "center" && loc != "village_center") return;
                var sources = new List<MobileParty>();
                if (st.Town?.GarrisonParty != null) sources.Add(st.Town.GarrisonParty);
                if (st.MapFaction != null)
                    sources.AddRange(st.Parties.Where(p => p != null && p.IsActive && p.IsLordParty && p != MobileParty.MainParty && p.MapFaction == st.MapFaction && p.MapEvent == null
                                                           && !SummonedFrom.Values.Contains(p)));
                var team = f.Opponent?.Team ?? m.PlayerEnemyTeam;
                if (team == null) return;
                var ahead = me.LookDirection.AsVec2;
                if (ahead.Length < 0.01f) ahead = new Vec2(0f, 1f);
                ahead = ahead.Normalized();
                var side = new Vec2(-ahead.y, ahead.x);
                int done = 0;
                foreach (var src in sources)
                {
                    foreach (var e in src.MemberRoster.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero && x.Number - x.WoundedNumber > 0).ToList())
                    {
                        int used = Defenders.Count(d => d.from == src && d.agent?.Character == e.Character);
                        for (int i = used; i < e.Number - e.WoundedNumber && done < want; i++)
                        {
                            int k = Defenders.Count;
                            var off = ahead * (22f + (k / 6) * 1.5f) + side * ((k % 6 - 2.5f) * 1.4f);
                            var pos = me.Position + new Vec3(off.x, off.y);
                            try { pos.z = m.Scene.GetGroundHeightAtPosition(pos); } catch { }
                            var dir = -ahead;
                            var data = new AgentBuildData(e.Character).TroopOrigin(new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(src.Party, e.Character)).Team(team)
                                .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                            var a = m.SpawnAgent(data);
                            if (a == null) continue;
                            a.WieldInitialWeapons();
                            Defenders.Add((a, src));
                            JoinFight(fh, a, false);
                            if (done < 3) Yell(a);
                            done++;
                        }
                        if (done >= want) break;
                    }
                    if (done >= want) break;
                }
                if (done > 0)
                {
                    _waves++;
                    VisitNews.GuardsCame(done);
                    Say(L.F("AIL_e7b1c86d", $"Llegan {done} soldados de la guarnición y de los señores que hay en {st.Name}."), Colors.Red);
                    Log.Info($"[fight] {done} defenders from the garrison and the lords of {st.Name}");
                    Unleash();
                }
            }
            catch (Exception ex) { Log.Error("Garrison defenders", ex); }
        }

        /// <summary>After the fight the defenders go back: the dead leave their roster, the fallen stay there wounded.</summary>
        private static void DismissDefenders()
        {
            foreach (var (a, from) in Defenders.ToList())
            {
                try
                {
                    if (a == null || from == null) continue;
                    if (a.Character is CharacterObject c && from.MemberRoster.GetTroopCount(c) > 0)
                    {
                        // counts are read again for each one, so nothing goes below what the roster holds
                        if (a.State == AgentState.Killed) from.MemberRoster.AddToCounts(c, -1);
                        else if (a.State == AgentState.Unconscious && from.MemberRoster.GetElementWoundedNumber(from.MemberRoster.FindIndexOfTroop(c)) < from.MemberRoster.GetTroopCount(c)) from.MemberRoster.AddToCounts(c, 0, false, 1);
                    }
                    if (a.IsActive()) a.FadeOut(false, true);
                }
                catch { }
            }
            Defenders.Clear();
        }

        /// <summary>After the fight your men leave the scene (those knocked down stay wounded in your party).</summary>
        private static void DismissSummoned()
        {
            foreach (var a in Summoned.ToList())
            {
                try
                {
                    if (a == null) continue;
                    if (a.IsActive()) a.FadeOut(false, true);
                    else if (a.Character is CharacterObject c)
                    {
                        // back to the party they came from: yours, or a lord's who rides with you
                        var from = SummonedFrom.TryGetValue(a, out var p) && p != null ? p : MobileParty.MainParty;
                        if (from.MemberRoster.GetTroopCount(c) > 0)
                        {
                            if (a.State == AgentState.Killed) from.MemberRoster.AddToCounts(c, -1);
                            else from.MemberRoster.AddToCounts(c, 0, false, 1);      // wounded
                        }
                    }
                }
                catch { }
            }
            Summoned.Clear();
            SummonedFrom.Clear();
        }

        // Agents fighting you: clear their walk mode and whatever they were doing so they run at you
        private static readonly List<Agent> ActiveFoes = new List<Agent>();
        // Who stopped fighting or changed sides in this fight, and the team each foe had before it
        private static readonly HashSet<Agent> Withdrawn = new HashSet<Agent>();
        private static readonly Dictionary<Agent, Team> TeamBefore = new Dictionary<Agent, Team>();
        private static float _unleash;

        private static void Unleash()
        {
            foreach (var a in ActiveFoes)
            {
                try
                {
                    if (a == null || !a.IsActive()) continue;
                    if (a.IsUsingGameObject) a.StopUsingGameObject();
                    a.SetMaximumSpeedLimit(-1f, false);
                    a.SetLookAgent(null);
                }
                catch { }
            }
        }

        private class Stripped
        {
            public Agent Agent;
            public List<(EquipmentIndex slot, MissionWeapon weapon)> Weapons = new List<(EquipmentIndex, MissionWeapon)>();
        }

        private static Stripped Strip(Agent a)
        {
            var s = new Stripped { Agent = a };
            try
            {
                a.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.Instant);
                a.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.Instant);
                for (var i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                {
                    var w = a.Equipment[i];
                    if (w.IsEmpty) continue;
                    s.Weapons.Add((i, w));
                    a.RemoveEquippedWeapon(i);
                }
            }
            catch (Exception ex) { Log.Error("Remove weapons for the fight", ex); }
            return s;
        }

        /// <summary>Gives weapons back only if the agent is still standing in the scene.</summary>
        private static void Restore(Stripped s)
        {
            var a = s?.Agent;
            if (a == null || a.Mission == null || !a.IsActive()) return;
            foreach (var (slot, weapon) in s.Weapons)
            {
                if (!a.Equipment[slot].IsEmpty) continue;
                var w = weapon;
                a.EquipWeaponWithNewEntity(slot, ref w);
            }
        }

        /// <summary>The outcome in the NPC's own terms ("you" is the NPC), for their memory.</summary>
        private static string HowItEnded(PendingFight f, bool playerWon, bool firstPerson)
        {
            switch (f.Ending)
            {
                case "truce": return firstPerson ? "we stopped it between us, a truce with no winner." : "You stopped it between you: a truce, with no winner.";
                case "fled": return firstPerson ? "I broke off and ran." : "You broke off and ran from them.";
                case "yielded": return firstPerson ? "I yielded and they accepted it." : "You yielded and they accepted it.";
                case "player_yielded": return firstPerson ? "they yielded to me and I accepted it." : "They yielded to you and you accepted it.";
                case "struck_down": return firstPerson ? "they struck me down with the first blow." : "They struck you down with the first blow.";
                default: return firstPerson ? (playerWon ? "they beat me." : "I beat them.") : (playerWon ? "They beat you." : "You beat them.");
            }
        }

        private static void OnFightEnded(PendingFight f, bool playerWon)
        {
            try
            {
                FightBanter.End(playerWon, f.Ending);
                _active = null;
                StopBells();
                DismissSummoned();
                DismissDefenders();
                // The fighters are left alone by idle remarks, and the whole place goes quiet for a minute
                var mq = Mission.Current;
                if (mq != null)
                {
                    if (_foughtIn != mq) { Fought.Clear(); _foughtIn = mq; }
                    if (f.Opponent != null) Fought.Add(f.Opponent);
                    _quietUntil = mq.CurrentTime + 60f;
                }
                var s = f.Who;
                var mem = s.Memory;
                string you = Prompt.ContextBuilder.PlayerNameFor(s) ?? "the stranger";
                string what = f.Kind == "brawl" ? "a fist fight" : f.Kind == "death" ? "a duel to the death" : "a duel";
                bool knockedOut = f.Opponent != null && f.Opponent.State == AgentState.Unconscious;
                // A fist fight in a tavern is rough fun, not a crime: no witnesses hunt, no guard, no manhunt
                bool barBrawl = f.Kind == "brawl" && f.Tavern;
                Log.Info($"Fight with {s.Name} ({f.Kind}{(barBrawl ? ", tavern" : "")}): {(playerWon ? "player wins" : "player loses")}{(knockedOut ? ", knocked out" : "")}");

                // They remember it, so the next chat knows
                mem.Turns.Add(new ChatTurn
                {
                    Role = "player", Day = CampaignTime.Now.ToDays,
                    Text = $"[What happened: {(f.Unprovoked ? $"{you} attacked you without a word of warning, and you fought {what}" : $"you and {you} fought {what}")}{(barBrawl ? " in the tavern" : "")}. " + HowItEnded(f, playerWon, false)
                           + (knockedOut && playerWon ? " You were knocked out cold and came to on the floor, bruised." : "") + "]"
                });
                // Witnesses: an assault only costs you if someone alive saw it, and only by name if they know you
                Witness.Result seen = null;
                if (f.Unprovoked && barBrawl)
                {
                    ChatSession.AddNote(mem, $"{you} picked a fist fight with me in the tavern for no reason.", S.Get.MaxNotesPerHero, s.Name);
                    mem.Trust = Math.Max(-100, mem.Trust - 15);
                    Say(L.T("AIL_e7b1c9a1", "Una pelea de taberna más: nadie llama a la guardia."), Colors.Gray);
                }
                else if (f.Unprovoked)
                {
                    var victims = new List<Agent> { f.Opponent };
                    victims.AddRange(f.Helpers);
                    seen = S.Get.EnableWitnesses ? Witness.Assess(victims) : new Witness.Result { Level = 2, Count = 1 };
                    ChatSession.AddNote(mem, $"{you} attacked me without provocation.", S.Get.MaxNotesPerHero, s.Name);
                    mem.Trust = Math.Max(-100, mem.Trust - 25);
                    float crime = (f.Kind == "brawl" ? 5f : 15f) * (seen.Level == 2 ? 1f : seen.Level == 1 ? 0.5f : 0f);
                    try { var stf = Settlement.CurrentSettlement?.MapFaction; if (crime > 0f && stf != null && stf != P.MapFaction) ChangeCrimeRatingAction.Apply(stf, crime, true); } catch { }
                    Say(seen.Level == 0 ? L.T("AIL_ef1030cb", "Nadie lo ha visto: nadie sabrá que has sido tú.")
                      : seen.Level == 1 ? (f.Kind == "brawl" ? L.F("AIL_e7b1c9a0", $"Te han visto ({seen.Count} testigo(s)), pero nadie sabe quién eres: buscan a un forastero que se lio a puñetazos.")
                                                             : L.F("AIL_a58c5f5d", $"Te han visto ({seen.Count} testigo(s)), pero nadie sabe quién eres: buscan a un forastero armado."))
                      : L.F("AIL_992e899d", $"{seen.ByWhom} te ha reconocido: se sabrá que has sido tú."), seen.Level == 0 ? Colors.Green : seen.Level == 1 ? Colors.Yellow : Colors.Red);
                }
                ChatSession.AddNote(mem, f.Ending == null ? (playerWon ? $"{you} beat me in {what}." : $"I beat {you} in {what}.") : $"{what} with {you}: {HowItEnded(f, playerWon, true)}", S.Get.MaxNotesPerHero, s.Name);
                if (f.Ending == "fled") { try { SceneActions.WalkAway(f.Opponent); } catch { } }
                // Beaten in a fist fight or a friendly duel: down on the floor for a while, nursing it
                // (someone knocked out cold already lies there: the engine took them out of the scene, Knockdown skips them)
                else if (playerWon && IsFriendly(f) && (f.Ending == null || f.Ending == "yielded")) { try { SceneActions.Knockdown(f.Opponent, 40f); } catch { } }
                mem.Tension = Math.Max(0, mem.Tension - 40);
                // Knocked out cold: they come to bruised and hurt, wounded like after a lost battle
                if (knockedOut && s.IsHero && s.Hero != null && s.Hero.IsAlive && s.Hero != P)
                {
                    try { int cap = Math.Max(1, s.Hero.MaxHitPoints / 5); if (s.Hero.HitPoints > cap) s.Hero.HitPoints = cap; }
                    catch (Exception ex) { Log.Error("Wound the one knocked out", ex); }
                }

                if (s.IsHero)
                {
                    // Fighting well earns respect, losing a serious duel stings
                    int rel = f.Unprovoked ? (f.Kind == "brawl" ? -4 : -10) : f.Kind == "brawl" ? 1 : playerWon ? 2 : 0;
                    if (seen != null && !(f.Opponent != null && (f.Opponent.IsActive() || f.Opponent.State == AgentState.Unconscious))) rel = 0;      // the dead hold no grudge
                    if (rel != 0) ChangeRelationAction.ApplyPlayerRelation(s.Hero, rel, affectRelatives: false, showQuickNotification: true);
                }

                if (f.Kind == "death")
                {
                    if (playerWon)
                    {
                        if (s.IsHero && S.Get.AllowDuelDeath) { ToKill.Add(s.Hero); Say(L.F("AIL_e10a40d5", $"{s.Name} cae en el duelo."), Colors.Red); }
                        else Say(L.F("AIL_a92c1422", $"{s.Name} queda tendido y malherido: has ganado el duelo."), Colors.Yellow);
                    }
                    else
                    {
                        // The player can't die (it would end the campaign), so leave them badly hurt
                        try { P.HitPoints = 1; } catch { }
                        Say(L.F("AIL_080a0e95", $"{s.Name} te deja por muerto. Sobrevives de milagro."), Colors.Red);
                    }
                }
                else if (f.Ending == "struck_down") Say(L.F("AIL_e7b1c9a2", $"{s.Name} cae al primer golpe."), Colors.Red);
                else if (playerWon && knockedOut) Say(L.F("AIL_e7b1c9a3", $"{s.Name} cae redondo y se queda en el suelo, sin sentido. Has ganado la pelea."), Colors.Yellow);
                else Say(playerWon ? L.F("AIL_d0109f54", $"Has vencido a {s.Name}.") : L.F("AIL_805db527", $"{s.Name} te ha vencido."), Colors.Yellow);

                // The news waits for the end of the visit: everything the player does here is told once, together,
                // when they leave (the fight against the guard to avoid arrest is told as part of the guard's story)
                if (!Justice.Resisting)
                {
                    var op = f.Opponent;
                    bool broken = f.Ending == "truce" || f.Ending == "fled" || f.Ending == "player_yielded";
                    string fate = op == null ? "killed" : op.State == AgentState.Unconscious ? "senseless"
                                : op.IsActive() ? (playerWon && !broken ? "down" : "standing") : "killed";
                    VisitNews.AddFight(new VisitNews.Fight
                    {
                        VictimName = s.Name ?? "", HeroId = s.IsHero ? s.Hero?.StringId : null, FactionId = s.Faction?.StringId,
                        IsHero = s.IsHero, IsLord = s.IsHero && s.Hero != null && s.Hero.IsLord, Female = s.IsFemale,
                        Kind = f.Kind, Unprovoked = f.Unprovoked, Tavern = f.Tavern, PlayerWon = playerWon, Ending = f.Ending, Fate = fate,
                        SeenLevel = seen?.Level ?? -1, Witnesses = seen?.Count ?? 0, ByWhom = seen?.ByWhom ?? "",
                        Defenders = f.Helpers.Count(h => h != null),
                        DefendersDown = f.Helpers.Count(h => h != null && !h.IsActive()),
                        DefendersKilled = f.Helpers.Count(h => h != null && h.State == AgentState.Killed),
                    });
                }

                // A nearby guard comes to ask questions (nobody comes if nobody saw it, nor over a tavern brawl)
                if (!barBrawl && (seen == null || seen.Level > 0)) Justice.OnFightEnded(s, f.Opponent, f.Kind, playerWon, f.Unprovoked);
                else BackToTalk(f, playerWon);
            }
            catch (Exception ex) { Log.Error("End of the fight", ex); }
        }

        /// <summary>A banner at the top of the screen (the duel's warnings, as AIInfluence shows them).</summary>
        private static void Banner(string text)
        {
            try { MBInformationManager.AddQuickInformation(new TaleWorlds.Localization.TextObject("{=!}" + text.Replace("{", "(").Replace("}", ")")), 0, null, null, ""); }
            catch { Say(text, Colors.Yellow); }
        }

        /// <summary>
        /// After a duel agreed in a talk, the opponent (still standing) comes back to the conversation, knowing how it
        /// ended. Not after a betrayal, a flight or a fist fight.
        /// </summary>
        private static void BackToTalk(PendingFight f, bool playerWon)
        {
            try
            {
                if (f == null || f.Unprovoked || f.Betrayed || f.Kind == "brawl" || f.Ending == "fled") return;
                var a = f.Opponent;
                if (a == null || !a.IsActive() || Mission.Current == null || Agent.Main == null || !Agent.Main.IsActive()) return;
                var s = f.Who;
                s.SituationKind = "initiative";
                s.Situation = playerWon ? "The duel is over: the player has won and you have lost." : "The duel is over: you have won and the player has lost.";
                if (Initiative.Start(a, s, s.Situation + " Speak first.")) Log.Info($"Returned to conversation after duel: {s.Name}");
            }
            catch (Exception ex) { Log.Error("Back to the talk after a duel", ex); }
        }

        /// <summary>The player called troops into a duel agreed in a talk: it is a battle now, and the betrayal is remembered.</summary>
        private static void BetrayDuel(PendingFight f)
        {
            try
            {
                f.Betrayed = true;
                // nobody is spared any more: what was a friendly duel is fought to the end
                if (IsFriendly(f)) { Unguard(); f.Kind = "death"; }
                var s = f.Who;
                string you = Prompt.ContextBuilder.PlayerNameFor(s) ?? "the stranger";
                string place = Settlement.CurrentSettlement?.Name?.ToString();
                s.Memory.Turns.Add(new ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays,
                    Text = $"[What happened: {you} broke the terms of the duel you had agreed and called troops into the fight. The duel turned into a battle.]" });
                ChatSession.AddNote(s.Memory, $"{you} broke the terms of our duel{(place != null ? " at " + place : "")} and set their troops on me.", S.Get.MaxNotesPerHero, s.Name);
                if (s.IsHero && s.Hero != null) TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyPlayerRelation(s.Hero, -10, affectRelatives: true, showQuickNotification: true);
                Say(L.F("AIL_e7b1d830", $"Rompes las condiciones del duelo con {s.Name}: el duelo se convierte en una batalla."), Colors.Red);
                Log.Info($"[fight] the player broke the agreed duel with {s.Name} and called troops: it is a battle now");
            }
            catch (Exception ex) { Log.Error("Duel betrayal", ex); }
        }

        // ============================================================ map: attack and surrender

        /// <summary>
        /// The party you're talking to on the map: the one the hero leads, or for a non-hero speaker (bandits,
        /// lordless parties), the party of the current conversation.
        /// </summary>
        public static MobileParty MapFoeParty(Speaker s)
        {
            try
            {
                if (s == null) return null;
                if (s.IsHero) return OrderSystem.PartyLedBy(s.Hero);
                if (Mission.Current != null) return null;
                // Leader of the lordless party the chat was opened with (bandits, caravan, villagers)
                if (s.Band != null) return s.Band.IsActive && !s.Band.IsMainParty ? s.Band : null;
                var p = MobileParty.ConversationParty ?? PlayerEncounter.EncounteredMobileParty;
                return p != null && p.IsActive && !p.IsMainParty && p.LeaderHero == null ? p : null;
            }
            catch { return null; }
        }

        public static bool EnemyOnMap(Speaker s)
        {
            try
            {
                // On the map, or in the scene on foot with that very lord (everything with an enemy is settled there)
                bool inScene = Mission.Current != null && Scenes.MapTalk.IsOpen && s != null && s.IsHero && Scenes.MapTalk.Current == s.Hero && Scenes.MapTalk.CurrentMode == Scenes.MapTalkMode.Lord;
                if (!S.Get.AllowCombat || (Mission.Current != null && !inScene) || PlayerEncounter.Current == null || PlayerEncounter.InsideSettlement || P.MapFaction == null) return false;
                var p = MapFoeParty(s);
                if (p == null) return false;
                var fac = s.IsHero ? s.Faction : p.MapFaction;
                return fac != null && fac != P.MapFaction && (fac.IsBanditFaction || fac.IsAtWarWith(P.MapFaction));
            }
            catch { return false; }
        }

        public static int Men(MobileParty p) => p?.MemberRoster.TotalHealthyCount ?? 0;

        // ============================================================ map: the battle starts by itself

        // The party leader decided in the chat to attack. When the chat closes, instead of leaving the player on the
        // encounter menu (where they'd have to click "Attack"), the battle starts on its own. Same for lords and bandits.
        private static float _autoAttack;        // real seconds (outside the chat) to wait for the encounter menu
        private static string _autoAttackWho;

        public static void QueueMapAttack(Speaker s)
        {
            KeepEncounter = true;
            _autoAttack = 15f;
            _autoAttackWho = s?.Name;
            Log.Info($"Map attack by {s?.Name}: the battle will start when the talk closes");
        }

        private static void TickMapAttack(float dt)
        {
            try
            {
                if ((_autoAttack -= dt) <= 0f) { _autoAttack = 0f; Log.Warn($"The battle with {_autoAttackWho} did not start on its own: the encounter menu remains"); return; }
                if (PlayerEncounter.Current == null) { _autoAttack = 0f; return; }       // encounter already resolved
                var mc = Campaign.Current?.CurrentMenuContext;
                if (mc?.GameMenu == null || mc.GameMenu.StringId != "encounter") return;   // menu not up yet
                if (PlayerEncounter.Battle == null || TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent == null) return;
                _autoAttack = 0f;
                var args = new TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs(mc, null);
                if (!global::Helpers.MenuHelper.EncounterAttackCondition(args) || !args.IsEnabled)
                {
                    // Wounded, on a raft, etc.: the game won't allow an attack, so the player decides in the menu
                    Log.Info($"The battle with {_autoAttackWho} cannot start on its own (the game does not allow attacking now)");
                    return;
                }
                Log.Info($"Battle starts with {_autoAttackWho}");
                global::Helpers.MenuHelper.EncounterAttackConsequence(args);
            }
            catch (Exception ex) { _autoAttack = 0f; Log.Error("Start the map battle", ex); }
        }

        /// <summary>The NPC surrenders with their whole party. Resolved when the chat closes.</summary>
        public static string QueueSurrender(Speaker s)
        {
            _surrender = s;
            _surrenderParty = MapFoeParty(s);      // once the chat closes the conversation party is gone
            return L.F("AIL_807992a5", $"{s.Name} rinde las armas.");
        }

        private static MobileParty _surrenderParty;

        private static void DoSurrender()
        {
            var s = _surrender;
            var party = _surrenderParty ?? MapFoeParty(s);
            _surrender = null; _surrenderParty = null;
            try
            {
                if (party == null || !party.IsActive) return;
                if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);

                var main = MobileParty.MainParty;
                int taken = 0;
                foreach (var e in party.MemberRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null || e.Character.IsHero || e.Number <= 0) continue;
                    main.PrisonRoster.AddToCounts(e.Character, e.Number);
                    taken += e.Number;
                }
                // Their prisoners become yours, and those from your side go free
                foreach (var e in party.PrisonRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null) continue;
                    if (e.Character.IsHero)
                    {
                        var ph = e.Character.HeroObject;
                        if (ph != null && ph.MapFaction != null && !ph.MapFaction.IsAtWarWith(P.MapFaction)) EndCaptivityAction.ApplyByReleasedAfterBattle(ph);
                        else if (ph != null) TransferPrisonerAction.Apply(e.Character, party.Party, main.Party);
                    }
                    else main.PrisonRoster.AddToCounts(e.Character, e.Number);
                }
                var heroes = party.MemberRoster.GetTroopRoster().Where(e => e.Character != null && e.Character.IsHero && e.Character.HeroObject != null)
                    .Select(e => e.Character.HeroObject).ToList();
                foreach (var x in heroes) TakePrisonerAction.Apply(main.Party, x);
                if (party.IsActive) DestroyPartyAction.Apply(main.Party, party);
                Say(L.F("AIL_454cbf99", $"{s.Name} se rinde: {taken} hombres pasan a ser tus prisioneros."), Colors.Green);
                Log.Info($"Surrender of {s.Name}: {taken} prisoners");
            }
            catch (Exception ex) { Log.Error("Surrender", ex); }
        }

        // ============================================================ tick

        private static float _playerSurrender;

        /// <summary>The player surrenders to whoever they face on the map. Takes effect when the chat closes.</summary>
        public static void QueuePlayerSurrender() { KeepEncounter = true; _playerSurrender = 12f; }

        private static void TickPlayerSurrender(float dt)
        {
            if (_playerSurrender <= 0f) return;
            try
            {
                if ((_playerSurrender -= dt) <= 0f) { _playerSurrender = 0f; KeepEncounter = false; return; }
                if (Campaign.Current?.ConversationManager?.IsConversationInProgress == true || Mission.Current != null) return;
                if (PlayerEncounter.Current == null) { _playerSurrender = 0f; KeepEncounter = false; return; }
                _playerSurrender = 0f; KeepEncounter = false;
                var foe = PlayerEncounter.EncounteredMobileParty;
                if (PlayerEncounter.InsideSettlement || foe == null || foe.MapFaction == null || Hero.MainHero.MapFaction == null
                    || !(foe.MapFaction.IsBanditFaction || foe.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction)))
                { Log.Warn("Player surrender: the encounter is no longer with an enemy on the map; doing nothing"); return; }
                // Same as the "Surrender" option in the encounter menu
                PlayerEncounter.PlayerSurrender = true;
                PlayerEncounter.Update();
                Log.Info("The player surrenders: becomes a prisoner");
            }
            catch (Exception ex) { _playerSurrender = 0f; KeepEncounter = false; Log.Error("Player surrender", ex); }
        }

        public static void Tick(float dt)
        {
            TickPlayerSurrender(dt);
            try
            {
                if (Campaign.Current == null) { _fight = null; _surrender = null; _surrenderParty = null; ToKill.Clear(); KeepEncounter = false; _autoAttack = 0f; return; }
                bool talking = Campaign.Current.ConversationManager?.IsConversationInProgress == true;
                FightBanter.Tick(dt);
                VisitNews.Tick();
                if (_yield != 0) EndFriendlyOnYield();
                if (GuardedAgents.Count > 0 && Mission.Current == null) Unguard();
                if (Defenders.Count > 0 && Mission.Current == null) DismissDefenders();      // the scene closed without the fight's own end: settle the count anyway
                if (_active != null) { GrowFight(dt); PlayerFightKeys(); if ((_crowd -= dt) <= 0f) { _crowd = 1f; Crowd(); } }
                if (ActiveFoes.Count > 0)
                {
                    var fhNow = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                    if (fhNow == null || !fhNow.IsThereActiveFight()) ActiveFoes.Clear();
                    else if ((_unleash -= dt) <= 0f) { _unleash = 1f; Unleash(); }
                }
                // The scene closed mid-fight: the game never reports the end, so forget it here
                if (_active != null && Mission.Current != _activeIn) { Log.Info("[fight] the scene closed before the fight ended"); _active = null; }
                EnsureWatch();
                TickBells(dt);
                if (Calmed.Count > 0 && _active == null) ReleasePatrons();      // the fight ended some other way
                if (_struck != null) { if (talking || Mission.Current == null) _struck = null; else TakeFirstBlow(); }
                if (_pulled.Count > 0) PullIn();

                if (KeepEncounter && !talking && !ChatSession.IsBusy) KeepEncounter = false;

                if (_fight != null && !talking)
                {
                    if (Mission.Current == null) { _fight = null; }
                    else
                    {
                        // an agreed duel: 3 seconds to ready your weapon
                        if (!_fight.Announced && _fight.Kind != "brawl" && !_fight.Unprovoked && _fight.Wait > 1f)
                        {
                            _fight.Announced = true;
                            Banner(L.T("AIL_e7b1d82e", "El duelo empieza en 3 segundos. Prepara tu arma."));
                        }
                        if ((_fight.Wait -= dt) <= 0f) StartFight();
                    }
                }

                if (_surrender != null && !talking && Mission.Current == null) DoSurrender();
                if (_autoAttack > 0f && Mission.Current != null) _autoAttack = 0f;      // the battle (or another scene) already started
                if (_autoAttack > 0f && !talking && !ChatSession.IsBusy) TickMapAttack(dt);

                if (ToFade.Count > 0 && !talking)
                {
                    foreach (var a in ToFade.ToList()) { try { if (Mission.Current != null && a.IsActive()) a.FadeOut(false, true); } catch { } }
                    ToFade.Clear();
                }
                if (_bandJoin != null && Mission.Current == null && !talking && !ChatSession.IsBusy) TickBandJoin();
                // A death in a conversation goes through as soon as the talk is closed: in the scene too, once the
                // visible blow has dropped the body (SceneActions.DeathBlow), as AIInfluence does
                if (RoleDeaths.Count > 0 && !talking && !ChatSession.IsBusy)
                {
                    foreach (var d in RoleDeaths.ToList())
                    {
                        if (Mission.Current != null && StillStanding(d.who)) continue;
                        RoleDeaths.Remove(d);
                        try
                        {
                            if (d.who == null || !d.who.IsAlive) continue;
                            string victim = d.who.Name.ToString(), clanId = d.who.MapFaction?.StringId, heroId = d.who.StringId;
                            var where = d.who.CurrentSettlement ?? Settlement.CurrentSettlement;
                            bool byPlayer = d.kind == "executed" || d.kind == "killed";
                            // read before the death hands the crown or the clan to someone else
                            int imp = d.who.IsKingdomLeader ? 4 : d.who.IsLord ? 3 : 2;
                            // One death, one news item: this record (how it went, what was found) replaces the game's own
                            NewsTracker.ToldElsewhere = heroId;
                            // Always the game's murder (with the player as culprit when it was his doing), never its
                            // execution: AIInfluence avoids the execution's own honour and relation penalties
                            var home = d.who.CurrentSettlement;
                            KillCharacterAction.ApplyByMurder(d.who, byPlayer ? P : null, true);
                            NewsTracker.ToldElsewhere = null;
                            DropNotable(home, d.who);
                            Log.Info($"Role-play death carried out: {victim} ({d.kind})");
                            // How it happened, who did it and what was found go to the news, where characters learn of it
                            string text = DeathInEnglish(d.kind, victim, P.Name.ToString()) + (d.how.Length > 2 ? $" ({d.how})" : "") + "."
                                          + (d.found.Length > 2 ? $" Found afterwards: {d.found}" : "");
                            var item = new NewsItem
                            {
                                Type = "death", Importance = imp, Text = text, SettlementId = where?.StringId,
                                Kingdoms = clanId != null ? new List<string> { clanId } : new List<string>(),
                                Heroes = byPlayer ? new List<string> { heroId, P.StringId } : new List<string> { heroId },
                            };
                            // What the player reads: how they died as a sentence, then how it went and what was on them
                            // when the character said so. Nothing found is ever added to the inventory, and it says so.
                            // No window: the news item (and, for someone of note, the event the chronicler writes
                            // from it) is the one place it is told.
                            item.TitleEs = L.F("AIL_e7b1c4e0", $"Muerte de {victim}");
                            item.TextEs = DeathForPlayer(d.kind, victim, d.how, d.found);
                            NewsStore.Add(item);
                        }
                        catch (Exception ex) { Log.Error("Role-play death", ex); }
                    }
                }
                if (ToKill.Count > 0 && Mission.Current == null && !talking)
                {
                    foreach (var h in ToKill.ToList())
                    {
                        ToKill.Remove(h);
                        if (h != null && h.IsAlive) KillCharacterAction.ApplyByBattle(h, P, true);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Combat: tick", ex); _fight = null; _surrender = null; }
        }

        /// <summary>The hero still has a living agent in the scene (the blow has not dropped them yet).</summary>
        private static bool StillStanding(Hero h)
        {
            try
            {
                var m = Mission.Current;
                if (m == null || h == null) return false;
                return m.Agents.Any(a => a != null && a.IsActive() && a.IsHuman && (a.Character as CharacterObject)?.HeroObject == h);
            }
            catch { return false; }
        }

        private static readonly System.Reflection.FieldInfo FNotables = HarmonyLib.AccessTools.Field(typeof(Settlement), "_notablesCache");

        /// <summary>A notable killed in a talk is taken off the place's list of notables right away (AIInfluence does it to avoid a crash).</summary>
        private static void DropNotable(Settlement st, Hero h)
        {
            try
            {
                if (st == null || h == null || FNotables == null) return;
                if (FNotables.GetValue(st) is System.Collections.IList list && list.Contains(h)) list.Remove(h);
            }
            catch (Exception ex) { Log.Warn("Removing a dead notable from the list: " + ex.Message); }
        }

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));
    }
}
