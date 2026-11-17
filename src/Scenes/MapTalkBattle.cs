using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using AILegacy.Dialogue;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.TroopSuppliers;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Scenes
{
    /// <summary>
    /// The talk on foot turned into the battle, there and then, without leaving the scene. The map encounter's battle is
    /// started (or the one already there is used), the parties standing in the scene join their sides, and every soldier in
    /// the scene takes his own place in that battle, so each death and wound counts as in any battle of the game. The rest of
    /// both parties come in as reinforcements from behind their ranks. When one side has nobody left, the battle is won or
    /// lost; leaving earlier, or falling, leaves it to the map encounter, where it goes on as usual (results, loot, prisoners).
    /// </summary>
    internal partial class MapTalkLogic
    {
        internal static MapTalkLogic Instance { get; private set; }

        private bool _battle, _resultGiven, _matching, _committed;
        private Speaker _battlePending;
        private PartyGroupTroopSupplier _mineSupplier, _theirSupplier;
        private bool _mineDry, _theirDry;
        private Team _enemyTeam;
        private float _check, _mainDown;
        private const int MaxBattleAgents = 300, Wave = 10, LowWater = 40;
        private readonly Dictionary<(PartyBase, CharacterObject), int> _needMine = new Dictionary<(PartyBase, CharacterObject), int>();
        private readonly Dictionary<(PartyBase, CharacterObject), int> _needTheirs = new Dictionary<(PartyBase, CharacterObject), int>();

        internal bool InBattle => _battle;

        /// <summary>Asked when the talk ends in a fight: the battle starts once the conversation has closed.</summary>
        internal bool QueueBattle(Speaker s)
        {
            if (_mode != MapTalkMode.Lord || _battle || _battlePending != null) return false;
            _battlePending = s ?? Speaker.FromHero(_target);
            return true;
        }

        private void PendingBattleTick()
        {
            if (_battlePending == null || Campaign.Current.ConversationManager?.IsConversationInProgress == true) return;
            var s = _battlePending; _battlePending = null;
            if (!StartBattle(s))
            {
                if (_committed)
                {
                    // the map battle was already under way when it failed: the scene closes and the encounter goes on with it
                    Log.Warn("[map scene] the battle in the scene failed half-way; the map encounter takes it over");
                    MapTalk.LeaveSoon();
                    return;
                }
                // Could not be fought here: as before, the scene closes and the game's battle opens on this ground
                Log.Warn("[map scene] the battle could not start in the scene; it opens on the map instead");
                MapTalk.AfterScene(() => Combat.QueueMapAttack(s));
                MapTalk.LeaveSoon();
            }
        }

        private bool StartBattle(Speaker s)
        {
            try
            {
                var foe = _party;
                if (foe == null || !foe.IsActive || Agent.Main == null || !Agent.Main.IsActive()) return false;
                // the map battle: the encounter's own, or a new one with the other side as the attacker
                var battle = PlayerEncounter.Battle;
                if (battle == null)
                {
                    if (PlayerEncounter.Current == null) PlayerEncounter.RestartPlayerEncounter(PartyBase.MainParty, foe.Party, false);
                    battle = PlayerEncounter.StartBattle();
                }
                if (battle == null || MobileParty.MainParty.MapEvent != battle) { Log.Warn("[map scene] no map battle for the scene"); return false; }
                var mySide = PartyBase.MainParty.Side;
                var theirSide = mySide == BattleSideEnum.Attacker ? BattleSideEnum.Defender : BattleSideEnum.Attacker;
                if (foe.MapEvent != battle) { Log.Warn("[map scene] the other party is not in the map battle"); return false; }
                _committed = true;      // from here on the map battle exists: a failure leaves it to the encounter, never a second attack
                var leader = battle.GetLeaderParty(theirSide);
                if (leader != null && (!leader.IsMobile || (!leader.MobileParty.IsVillager && !leader.MobileParty.IsCaravan)))
                    BeHostileAction.ApplyEncounterHostileAction(PartyBase.MainParty, leader);

                // the parties standing here join their side, if they are in no other battle
                foreach (var p in _partyOf.Values.Where(x => x != null).Distinct().ToList())
                {
                    try
                    {
                        if (p.MapEvent != null || !p.IsMobile || MapTalk.Leavers.Contains(p) || !p.MobileParty.IsActive) continue;
                        bool theirs = _theirParties.Contains(p);
                        p.MapEventSide = battle.GetMapEventSide(theirs ? theirSide : mySide);
                        Log.Info($"[map scene] {p.Name} joins the battle on the {(theirs ? "other" : "player's")} side");
                    }
                    catch (Exception ex) { Log.Warn($"[map scene] {p?.Name} could not join the battle: {ex.Message}"); }
                }
                // lords who rode with you and chose to leave take their men out of the battle too
                foreach (var p in MapTalk.Leavers.ToList())
                {
                    try
                    {
                        if (p == null || p == PartyBase.MainParty || p.MapEvent != battle || battle.GetLeaderParty(mySide) == p) continue;
                        p.MapEventSide = null;
                        Log.Info($"[map scene] {p.Name} leaves the battle");
                    }
                    catch (Exception ex) { Log.Warn($"[map scene] {p?.Name} could not leave the battle: {ex.Message}"); }
                }
                PlayerEncounter.StartAttackMission();

                // who is fighting here: everyone standing, but captives and the men of lords who rode off
                var leavers = new List<Agent>();
                var mine = new List<Agent>(); var theirs2 = new List<Agent>();
                foreach (var a in Mission.Agents.ToList())
                {
                    if (a == null || !a.IsActive() || !a.IsHuman) continue;
                    if (_captiveAgents.Contains(a)) { leavers.Add(a); continue; }
                    _partyOf.TryGetValue(a, out var p);
                    if (a == Agent.Main) p = PartyBase.MainParty;
                    if (p == null || MapTalk.Leavers.Contains(p)) { leavers.Add(a); continue; }
                    (_theirAgents.Contains(a) ? theirs2 : mine).Add(a);
                }

                // each soldier takes his own place in the map battle
                _needMine.Clear(); _needTheirs.Clear();
                foreach (var a in mine) Need(_needMine, Key(a));
                foreach (var a in theirs2) Need(_needTheirs, Key(a));
                _matching = true;
                _mineSupplier = new PartyGroupTroopSupplier(battle, mySide, null, (d, mep) => Allocate(_needMine, d, mep));
                _theirSupplier = new PartyGroupTroopSupplier(battle, theirSide, null, (d, mep) => Allocate(_needTheirs, d, mep));
                var mineOrigins = _mineSupplier.SupplyTroops(mine.Count).ToList();
                var theirOrigins = _theirSupplier.SupplyTroops(theirs2.Count).ToList();
                _matching = false;
                int placed = Place(mine, mineOrigins) + Place(theirs2, theirOrigins);
                // whoever got no place in the map battle does not fight: his wounds would count nowhere
                foreach (var a in mine.Concat(theirs2).Where(x => !(x.Origin is PartyGroupAgentOrigin)).ToList())
                {
                    if (a == Agent.Main) { Log.Warn("[map scene] the player has no place in the map battle; their wounds will not count"); continue; }
                    mine.Remove(a); theirs2.Remove(a); leavers.Add(a);
                }
                Log.Info($"[map scene] battle in the scene: {mine.Count} on your side, {theirs2.Count} on theirs; {placed} placed in the map battle; {leavers.Count} leave the field");

                foreach (var a in leavers) { try { a.FadeOut(true, false); } catch { } }

                // two sides at war
                _enemyTeam = Mission.Teams.FirstOrDefault(t => t != Mission.PlayerTeam && t.Side != BattleSideEnum.None)
                             ?? Mission.Teams.Add(BattleSideEnum.Attacker, foe.MapFaction?.Color ?? uint.MaxValue, foe.MapFaction?.Color2 ?? uint.MaxValue, _target.ClanBanner);
                _enemyTeam.SetIsEnemyOf(Mission.PlayerTeam, true);
                foreach (var a in theirs2) Arm(a, _enemyTeam);
                foreach (var a in mine) Arm(a, Mission.PlayerTeam);
                _battle = true;
                Mission.SetMissionMode(MissionMode.Battle, true);
                var team = Mission.PlayerTeam;
                team.PlayerOrderController.Owner = Agent.Main;
                team.SetPlayerRole(true, false);
                try { _enemyTeam.AddTeamAI(new TeamAIGeneral(Mission, _enemyTeam, 10f, 1f), false); _enemyTeam.AddTacticOption(new TacticCharge(_enemyTeam)); }
                catch (Exception ex) { Log.Warn("[map scene] the other side's battle AI: " + ex.Message); }
                Orders();
                InformationManager.DisplayMessage(new InformationMessage(Hotkeys.Fix(L.F("AIL_e7b1df30", $"¡A las armas! La batalla contra {_target.Name} es aquí mismo. F1-F7 para dar órdenes; Tab para retirarte.")), Colors.Red));
                Log.Info($"[map scene] {s?.Name} attacks: the battle is fought in the scene");
                return true;
            }
            catch (Exception ex) { Log.Error("Map scene: battle in the scene", ex); return false; }
        }

        private (PartyBase, CharacterObject) Key(Agent a) => (a == Agent.Main ? PartyBase.MainParty : _partyOf.TryGetValue(a, out var p) ? p : null, a.Character as CharacterObject);

        private static void Need(Dictionary<(PartyBase, CharacterObject), int> need, (PartyBase, CharacterObject) k)
        {
            if (k.Item1 == null || k.Item2 == null) return;
            need[k] = (need.TryGetValue(k, out var n) ? n : 0) + 1;
        }

        /// <summary>While matching, only the soldiers standing in the scene; after that, anyone (the reinforcements).</summary>
        private bool Allocate(Dictionary<(PartyBase, CharacterObject), int> need, UniqueTroopDescriptor d, MapEventParty mep)
        {
            if (!_matching) return true;
            try
            {
                var k = (mep.Party, mep.GetTroop(d));
                if (!need.TryGetValue(k, out var n) || n <= 0) return false;
                need[k] = n - 1;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Gives each agent the place of a soldier of his party and kind. Returns how many got one.</summary>
        private int Place(List<Agent> agents, List<IAgentOriginBase> origins)
        {
            var free = agents.ToList();
            int done = 0;
            foreach (var o in origins.OfType<PartyGroupAgentOrigin>())
            {
                var a = free.FirstOrDefault(x => Key(x).Item1 == o.Party && Key(x).Item2 == o.Troop);
                if (a == null) continue;
                free.Remove(a);
                a.Origin = o;
                done++;
            }
            return done;
        }

        private void Arm(Agent a, Team team)
        {
            try
            {
                if (a.Team != team) a.SetTeam(team, true);
                a.SetMortalityState(Agent.MortalityState.Mortal);
                a.SetWatchState(Agent.WatchState.Alarmed);
                try { a.DisableScriptedMovement(); } catch { }
                try { a.SetLookAgent(null); } catch { }
                if (a != Agent.Main) a.Formation = team.GetFormation(a.IsRangedCached ? FormationClass.Ranged : FormationClass.Infantry);
            }
            catch (Exception ex) { Log.Warn("[map scene] arming " + a?.Name + ": " + ex.Message); }
        }

        /// <summary>The other side charges and shoots at will; yours follow you until you order otherwise.</summary>
        private void Orders()
        {
            foreach (var f in _enemyTeam.FormationsIncludingEmpty.Where(x => x.CountOfUnits > 0))
            {
                try { f.SetMovementOrder(MovementOrder.MovementOrderCharge); f.SetFiringOrder(FiringOrder.FiringOrderFireAtWill); } catch { }
            }
            foreach (var f in Mission.PlayerTeam.FormationsIncludingEmpty.Where(x => x.CountOfUnits > 0))
            {
                try { if (Agent.Main != null) f.SetMovementOrder(MovementOrder.MovementOrderFollow(Agent.Main)); f.SetFiringOrder(FiringOrder.FiringOrderFireAtWill); } catch { }
            }
        }

        // ---------------------------------------------------------------- while it is fought

        private void BattleTick(float dt)
        {
            if (!_battle || _resultGiven) return;
            // you fell: what is left of it is settled on the map
            if (Agent.Main == null || !Agent.Main.IsActive())
            {
                if ((_mainDown += dt) > 5f)
                {
                    _resultGiven = true;
                    InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1df31", "Has caído. Lo que quede de la batalla se decide en el mapa."), Colors.Red));
                    LeaveIn = 1f;
                }
                return;
            }
            if ((_check -= dt) > 0f) return;
            _check = 1.5f;
            Reinforce(_mineSupplier, Mission.PlayerTeam, false, ref _mineDry);
            Reinforce(_theirSupplier, _enemyTeam, true, ref _theirDry);
        }

        private static int Standing(Team t) => t == null ? 0 : t.ActiveAgents.Count(a => a.IsHuman);

        private void Reinforce(PartyGroupTroopSupplier supplier, Team team, bool theirs, ref bool dry)
        {
            try
            {
                if (supplier == null || team == null || dry || Standing(team) >= LowWater) return;
                int room = MaxBattleAgents - Mission.Agents.Count(a => a.IsActive() && a.IsHuman);
                // a side with nobody left always gets a wave, even with the scene full
                if (room <= 0 && Standing(team) > 0) return;
                room = Math.Max(room, Standing(team) == 0 ? Wave : 0);
                if (room <= 0) return;
                if (supplier.NumTroopsNotSupplied <= 0) { dry = true; return; }
                var come = supplier.SupplyTroops(Math.Min(Wave, room)).ToList();
                if (come.Count == 0) { dry = true; return; }
                Vec3 front = theirs ? _theirFront : _myFront;
                Vec2 back = theirs ? _toThem : -_toThem;
                var side = new Vec2(-back.y, back.x);
                for (int i = 0; i < come.Count; i++)
                {
                    var o = come[i];
                    var c = o.Troop as CharacterObject;
                    if (c == null) continue;
                    var off = back * (22f + (i / 5) * 1.6f) + side * (((i % 5) - 2) * 1.5f);
                    var pos = Ground(front + new Vec3(off.x, off.y));
                    var dir = -back;
                    var data = new AgentBuildData(c).TroopOrigin(o).Team(team).InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                    var party = (o as PartyGroupAgentOrigin)?.Party;
                    var banner = party?.LeaderHero?.ClanBanner ?? party?.MapFaction?.Banner;
                    if (banner != null) data.Banner(banner);
                    if (party?.MapFaction != null) data.ClothingColor1(party.MapFaction.Color).ClothingColor2(party.MapFaction.Color2);
                    var a = Mission.SpawnAgent(data);
                    if (a == null) continue;
                    if (party != null) _partyOf[a] = party;
                    if (theirs) _theirAgents.Add(a);
                    Arm(a, team);
                }
                Orders();
                Log.Info($"[map scene] reinforcements: {come.Count} for {(theirs ? "the other side" : "your side")}");
            }
            catch (Exception ex) { dry = true; Log.Error("Map scene: reinforcements", ex); }
        }

        private bool SideBeaten(Team team, PartyGroupTroopSupplier supplier, bool dry) => Standing(team) == 0 && (dry || supplier == null || supplier.NumTroopsNotSupplied <= 0);

        public override bool MissionEnded(ref MissionResult missionResult)
        {
            if (!_battle || _resultGiven || Agent.Main == null || !Agent.Main.IsActive()) return false;
            bool theyLost = SideBeaten(_enemyTeam, _theirSupplier, _theirDry);
            bool weLost = SideBeaten(Mission.PlayerTeam, _mineSupplier, _mineDry);
            if (!theyLost && !weLost) return false;
            var mySide = PartyBase.MainParty.Side;
            bool won = theyLost;
            var state = won == (mySide == BattleSideEnum.Attacker) ? BattleState.AttackerVictory : BattleState.DefenderVictory;
            var r = Result(state, won, !won);
            if (r == null) return false;
            missionResult = r;
            _resultGiven = true;
            InformationManager.DisplayMessage(new InformationMessage(won ? L.F("AIL_e7b1df32", $"¡Victoria! Las tropas de {_target.Name} han caído.") : L.T("AIL_e7b1df33", "Tu gente ha caído. La batalla está perdida."), won ? Colors.Green : Colors.Red));
            Log.Info($"[map scene] the battle in the scene is {(won ? "won" : "lost")}");
            LeaveIn = 4f;
            return true;
        }

        private static ConstructorInfo _resultCtor;

        private static MissionResult Result(BattleState state, bool victory, bool defeat)
        {
            try
            {
                _resultCtor = _resultCtor ?? typeof(MissionResult).GetConstructor(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
                    new[] { typeof(BattleState), typeof(bool), typeof(bool), typeof(bool) }, null);
                return (MissionResult)_resultCtor.Invoke(new object[] { state, victory, defeat, false });
            }
            catch (Exception ex) { Log.Error("Map scene: battle result", ex); return null; }
        }

        public override void OnAgentRemoved(Agent affected, Agent affector, AgentState state, KillingBlow blow)
        {
            base.OnAgentRemoved(affected, affector, state, blow);
            try
            {
                if (!_battle || !(affected?.Origin is PartyGroupAgentOrigin o)) return;
                if (state == AgentState.Unconscious) o.SetWounded();
                else if (state == AgentState.Killed) o.SetKilled();
                else if (state == AgentState.Routed) o.SetRouted(false);
            }
            catch (Exception ex) { Log.Error("Map scene: a fallen soldier", ex); }
        }
    }
}
