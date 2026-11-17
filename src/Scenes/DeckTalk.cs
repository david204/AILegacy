using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using SandBox;
using SandBox.Conversation.MissionLogics;
using SandBox.Missions.MissionLogics;
using SandBox.View;
using SandBox.View.Missions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.MissionViews.Singleplayer;

namespace AILegacy.Scenes
{
    /// <summary>
    /// Walking on deck (War Sails): at sea, a key on the map, or «Talk» with a companion or a prisoner on the party screen, takes you
    /// on foot onto your own ships, anchored on the sea of the party's position with its weather and hour. Your companions, the lords
    /// you hold captive, some of the crew and a few prisoners are there to talk to; the one you came to see speaks first.
    /// Without the expansion nothing of it runs: the mod reaches it by reflection.
    /// </summary>
    internal static class DeckTalk
    {
        public const string MissionName = "AILegacyDeck";
        public static readonly string[] KeyNames = { "F10", "F9", "F11", "None" };
        private static readonly InputKey[] Keys = { InputKey.F10, InputKey.F9, InputKey.F11, InputKey.Invalid };

        // At most this many ships, companions, captive lords, crew on each ship and common prisoners on deck
        internal const int MaxShips = 8, MaxCompanions = 8, MaxCaptiveLords = 6, CrewPerShip = 8, MaxPrisoners = 3;

        private static bool _pending, _open;
        private static Hero _pendingHero, _current;
        private static float _wait;
        private static DateTime _queuedAt;
        private static readonly Dictionary<Ship, (float hull, float sail)> _health = new Dictionary<Ship, (float, float)>();

        public static bool IsOpen => _open;
        internal static Hero Current => _open ? _current : null;

        internal static void ResetSession() { _pending = false; _pendingHero = null; _open = false; _current = null; _health.Clear(); }

        private static InputKey Key { get { try { return Keys[Math.Max(0, Math.Min(Keys.Length - 1, S.Get.MapTalkDeckKey.SelectedIndex))]; } catch { return InputKey.F10; } } }

        /// <summary>The party's ships taken on deck: the flagship first, then the strongest, at most 8.</summary>
        internal static List<Ship> Ships()
        {
            try
            {
                var all = PartyBase.MainParty?.Ships;
                if (all == null || all.Count == 0) return new List<Ship>();
                Ship flag = null;
                try { flag = PartyBase.MainParty.FlagShip; } catch { }
                return all.Where(s => s != null).OrderByDescending(s => s == flag).ThenByDescending(s => s.MaxHitPoints).Take(MaxShips).ToList();
            }
            catch { return new List<Ship>(); }
        }

        /// <summary>Why the deck can't be opened now (a message for the player), "" when it is silently not the moment, null when it can.</summary>
        internal static string WhyNot(Hero h)
        {
            try
            {
                if (!S.Get.EnableMapTalk || _open || _pending || MapTalk.IsOpen || MapTalk.IsPending) return "";
                if (!Naval.Installed) return L.T("AIL_e7b1dde2", "Caminar por cubierta necesita la expansión War Sails.");
                var main = MobileParty.MainParty;
                if (main == null || Hero.MainHero == null || Hero.MainHero.IsPrisoner || main.MapEvent != null) return "";
                if (!main.IsCurrentlyAtSea || main.CurrentSettlement != null || Ships().Count == 0)
                    return L.T("AIL_e7b1dde3", "Solo puedes caminar por cubierta en el mar, en tus propios barcos.");
                if (h != null && h != Hero.MainHero)
                {
                    bool mine = h.PartyBelongedTo == main || (h.IsPrisoner && h.PartyBelongedToAsPrisoner == PartyBase.MainParty);
                    if (!mine || !h.IsAlive) return "";
                }
                return null;
            }
            catch { return ""; }
        }

        public static bool CanOpen(Hero h) => WhyNot(h) == null;

        /// <summary>The deck opens once the map is free (the party screen or the conversation closed).</summary>
        public static void Queue(Hero h)
        {
            if (!CanOpen(h)) return;
            _pending = true; _pendingHero = h; _wait = 0.3f; _queuedAt = DateTime.UtcNow;
            Log.Info($"[deck] queued{(h != null ? " to talk with " + h.Name : "")}");
        }

        private static bool MapFree()
        {
            try
            {
                return Campaign.Current != null && Mission.Current == null && Campaign.Current.ConversationManager?.IsConversationInProgress != true
                       && Game.Current?.GameStateManager?.ActiveState is MapState && Hero.MainHero != null && !Hero.MainHero.IsPrisoner;
            }
            catch { return false; }
        }

        public static void Tick(float dt)
        {
            try
            {
                if (Campaign.Current == null) { _pending = false; return; }
                if (!_pending)
                {
                    var key = Key;
                    if (key == InputKey.Invalid || !MapFree() || !Input.IsKeyPressed(key)) return;
                    string why = WhyNot(null);
                    if (why == null) Queue(null);
                    else if (why.Length > 0) InformationManager.DisplayMessage(new InformationMessage(why, Colors.Yellow));
                    return;
                }
                if ((DateTime.UtcNow - _queuedAt).TotalSeconds > 30) { Log.Warn("[deck] request timed out"); _pending = false; return; }
                if (!MapFree() || (_wait -= dt) > 0f) return;
                var hero = _pendingHero; _pending = false; _pendingHero = null;
                string no = WhyNot(hero);
                if (no != null) { if (no.Length > 0) InformationManager.DisplayMessage(new InformationMessage(no, Colors.Yellow)); Log.Info("[deck] request dropped; the situation changed"); return; }
                Open(hero);
            }
            catch (Exception ex) { Log.Error("Walking on deck", ex); _pending = false; }
        }

        private static string SeaScene()
        {
            try
            {
                var patch = Campaign.Current.MapSceneWrapper.GetMapPatchAtPosition(MobileParty.MainParty.Position);
                string s = Campaign.Current.Models.SceneModel.GetBattleSceneForMapPatch(patch, true);
                if (!string.IsNullOrEmpty(s)) return s;
            }
            catch (Exception ex) { Log.Warn("[deck] sea scene: " + ex.Message); }
            return null;
        }

        private static void Open(Hero h)
        {
            string scene = SeaScene();
            if (string.IsNullOrEmpty(scene))
            {
                InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1dde4", "No hay escena de mar en la posición del grupo."), Colors.Yellow));
                Log.Warn("[deck] no sea scene at the party's position");
                return;
            }
            var ships = Ships();
            var rec = new MissionInitializerRecord(scene)
            {
                PlayingInCampaignMode = true,
                NeedsRandomTerrain = false,
                RandomTerrainSeed = MBRandom.RandomInt(10000),
                DecalAtlasGroup = 2,
                DamageToFriendsMultiplier = 0f,
                DamageFromPlayerToFriendsMultiplier = 0f,
                AtmosphereOnCampaign = Campaign.Current.Models.MapWeatherModel.GetAtmosphereModel(MobileParty.MainParty.Position),
                SceneHasMapPatch = false,
            };
            try { rec.TerrainType = (int)Campaign.Current.MapSceneWrapper.GetFaceTerrainType(MobileParty.MainParty.CurrentNavigationFace); } catch { }
            rec.AtmosphereOnCampaign.NauticalInfo.UsesNavalSimulatedWater = 1;

            // The ships come back from the deck as they went: nothing done on board wears them
            _health.Clear();
            foreach (var s in ships) { try { _health[s] = (s.HitPoints, s.SailHitPoints); } catch { } }

            Log.Info($"[deck] opening '{scene}' with {ships.Count} ships{(h != null ? ", to talk with " + h.Name : "")}");
            _open = true; _current = h;
            try
            {
                Naval.OpenMission(MissionName, rec, mission => new MissionBehavior[]
                {
                    Naval.New(Naval.ShipsLogicType),
                    Naval.New(Naval.FloatsamLogicType),
                    Naval.New(Naval.AgentsLogicType),
                    Naval.New(Naval.WavesLogicType),
                    new CampaignMissionComponent(),
                    new MissionConversationLogic(),
                    new MissionOptionsComponent(),
                    new AgentHumanAILogic(),
                    new MissionBoundaryPlacer(),
                    new MissionHardBorderPlacer(),
                    new DeckLogic(h, ships),
                    new BasicLeaveMissionLogic(),
                    new EquipmentControllerLeaveLogic(),
                });
            }
            catch (Exception ex)
            {
                _open = false; _current = null;
                Log.Error("Opening the deck", ex);
                InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1dde7", "No se pudo abrir la cubierta; mira ailegacy.log."), Colors.Red));
            }
        }

        /// <summary>Back on the map: the ships as they were, the party sails on.</summary>
        internal static void Closed()
        {
            _open = false; _current = null;
            foreach (var kv in _health)
            {
                try { kv.Key.HitPoints = kv.Value.hull; kv.Key.SailHitPoints = kv.Value.sail; } catch { }
            }
            _health.Clear();
            Log.Info("[deck] back on the map");
        }
    }

    /// <summary>Puts the ships on the water anchored in a row, then the people on their decks, and opens the talk.</summary>
    internal class DeckLogic : MissionLogic
    {
        private readonly Hero _target;
        private readonly List<Ship> _ships;
        private readonly List<object> _spawned = new List<object>();
        private int _stage;
        private float _talkIn = -1f;
        private Agent _targetAgent;

        public DeckLogic(Hero target, List<Ship> ships) { _target = target; _ships = ships; }

        public override void OnBehaviorInitialize()
        {
            base.OnBehaviorInitialize();
            try
            {
                // Teams before the naval logics start: they keep their data per team. An empty second side, as the game always has one
                var player = Hero.MainHero;
                var f = player.MapFaction;
                Mission.Teams.Add(BattleSideEnum.Defender, f?.Color ?? uint.MaxValue, f?.Color2 ?? uint.MaxValue, player.ClanBanner);
                Mission.PlayerTeam = Mission.DefenderTeam;
                Mission.Teams.Add(BattleSideEnum.Attacker, f?.Color ?? uint.MaxValue, f?.Color2 ?? uint.MaxValue, player.ClanBanner);
            }
            catch (Exception ex) { Log.Error("Deck teams", ex); }
        }

        public override void EarlyStart()
        {
            base.EarlyStart();
            Mission.MissionTeamAIType = Mission.MissionTeamAITypeEnum.NavalBattle;
            Naval.StartWind();
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            try
            {
                if (_stage == 0)
                {
                    _stage = 1;
                    Naval.StartWind();
                    SpawnShips();
                    try { Mission.OnInitialSpawnCompleted(); } catch (Exception ex) { Log.Warn("[deck] initial spawn: " + ex.Message); }
                    foreach (var s in _spawned) Naval.Hold(s);      // the game weighs anchor when the deployment ends: drop it again
                }
                else if (_stage == 1)
                {
                    _stage = 2;
                    foreach (var s in _spawned) Naval.Hold(s);
                    SpawnPeople();
                    _talkIn = _targetAgent != null ? 1.2f : -1f;
                    InformationManager.DisplayMessage(new InformationMessage(Hotkeys.Fix(L.T("AIL_e7b1dde5", "En cubierta. F para hablar, Alt+T para hablar a los que tengas cerca, Tab para volver al mapa.")), Colors.Yellow));
                }
                if (_talkIn > 0f && (_talkIn -= dt) <= 0f)
                {
                    _talkIn = -1f;
                    var logic = Mission.GetMissionBehavior<MissionConversationLogic>();
                    if (logic != null && _targetAgent != null && _targetAgent.IsActive() && Campaign.Current.ConversationManager?.IsConversationInProgress != true)
                        logic.StartConversation(_targetAgent, false, false);
                }
            }
            catch (Exception ex) { Log.Error("Deck tick", ex); _stage = 2; }
        }

        // ---------------------------------------------------------------- ships

        private void SpawnShips()
        {
            var logic = Naval.ShipsLogic(Mission);
            if (logic == null) { Log.Error("Deck: the ship logic is missing", null); return; }
            Vec2 pos, dir;
            WhereShips(out pos, out dir);
            var side = new Vec2(-dir.y, dir.x);
            var team = Mission.PlayerTeam;
            int n = _ships.Count;
            for (int i = 0; i < n; i++)
            {
                try
                {
                    var at = pos + side * ((i - (n - 1) / 2f) * 45f);
                    var frame = MatrixFrame.Identity;
                    frame.origin = new Vec3(at.x, at.y, 0f);
                    frame.rotation.f = new Vec3(dir.x, dir.y, 0f);
                    frame.rotation.OrthonormalizeAccordingToForwardAndKeepUpAsZAxis();
                    var formation = team.GetFormation((FormationClass)i);
                    var ms = Naval.SpawnShip(logic, _ships[i], frame, team, formation);
                    if (ms != null) { _spawned.Add(ms); Naval.Hold(ms); }
                }
                catch (Exception ex) { Log.Error($"Deck: putting {_ships[i]?.Name} on the water", ex); }
            }
            Log.Info($"[deck] ships on the water: {_spawned.Count} of {n}");
        }

        /// <summary>Where the row of ships lies: the scene's own starting path for your side, or the middle of the scene.</summary>
        private void WhereShips(out Vec2 pos, out Vec2 dir)
        {
            pos = Vec2.Zero; dir = new Vec2(0f, 1f);
            try
            {
                if (Mission.HasSpawnPath)
                {
                    Mission.GetInitialSpawnPathData(BattleSideEnum.Defender).GetSpawnPathFrameFacingTarget(0f, 1f, false, out pos, out dir);
                    if (dir.Length > 0.01f) { dir = dir.Normalized(); return; }
                }
            }
            catch (Exception ex) { Log.Warn("[deck] spawn path: " + ex.Message); }
            try
            {
                var sp = Mission.Scene.FindEntityWithTag("spawnpoint_player") ?? Mission.Scene.FindEntitiesWithTag("player_spawn_frame").FirstOrDefault();
                if (sp != null)
                {
                    var g = sp.GetGlobalFrame();
                    pos = g.origin.AsVec2; dir = g.rotation.f.AsVec2.Length > 0.01f ? g.rotation.f.AsVec2.Normalized() : new Vec2(0f, 1f);
                    return;
                }
                Mission.Scene.GetBoundingBox(out var min, out var max);
                pos = ((min + max) * 0.5f).AsVec2;
            }
            catch (Exception ex) { Log.Warn("[deck] scene middle: " + ex.Message); }
        }

        // ---------------------------------------------------------------- people

        private void SpawnPeople()
        {
            if (_spawned.Count == 0) { Log.Warn("[deck] no ship on the water: nobody to put on deck"); return; }
            var main = PartyBase.MainParty;
            var flag = _spawned[0];
            var places = Naval.DeckFrames(flag);
            MatrixFrame captain;
            try { captain = Naval.CaptainFrame(flag); } catch { captain = places.Count > 0 ? places[0] : Naval.ShipFrame(flag); }
            var pDir = captain.rotation.f.AsVec2.Length > 0.01f ? captain.rotation.f.AsVec2.Normalized() : new Vec2(0f, 1f);
            Spawn(CharacterObject.PlayerCharacter, captain.origin, pDir, true, false);

            // The flagship's places, nearest to you first: the one you came to see stands closest
            var free = places.Where(p => p.origin.DistanceSquared(captain.origin) > 0.8f).OrderBy(p => p.origin.DistanceSquared(captain.origin)).ToList();
            Vec2 Facing(Vec3 at) { var d = (captain.origin - at).AsVec2; return d.Length > 0.01f ? d.Normalized() : -pDir; }

            var companions = new List<CharacterObject>();
            var lords = new List<CharacterObject>();
            var prisoners = new List<CharacterObject>();
            var crew = new List<CharacterObject>();
            try
            {
                foreach (var e in main.MemberRoster.GetTroopRoster())
                {
                    var c = e.Character;
                    if (c == null || c == CharacterObject.PlayerCharacter) continue;
                    if (c.IsHero) { if (c.HeroObject != _target) companions.Add(c); }
                    else for (int i = 0; i < e.Number - e.WoundedNumber; i++) crew.Add(c);
                }
                foreach (var e in main.PrisonRoster.GetTroopRoster())
                {
                    var c = e.Character;
                    if (c == null) continue;
                    if (c.IsHero) { if (c.HeroObject != _target) lords.Add(c); }
                    else for (int i = 0; i < e.Number; i++) prisoners.Add(c);
                }
            }
            catch (Exception ex) { Log.Error("Deck: who is on board", ex); }
            crew = crew.OrderByDescending(c => c.Tier).ToList();

            int k = 0;
            Vec3 Next() { var p = free.Count == 0 ? captain.origin : free[k % free.Count].origin; k++; return p; }
            bool Room() => k < free.Count;

            if (_target != null && Room())
            {
                var at = Next();
                _targetAgent = Spawn(_target.CharacterObject, at, Facing(at), false, _target.IsPrisoner);
                try { _targetAgent?.SetLookAgent(Agent.Main); } catch { }
            }
            int nComp = 0, nLords = 0, nPris = 0, nCrew = 0;
            foreach (var c in companions.Take(DeckTalk.MaxCompanions)) { if (!Room()) break; var at = Next(); Spawn(c, at, Facing(at), false, false); nComp++; }
            foreach (var c in lords.Take(DeckTalk.MaxCaptiveLords)) { if (!Room()) break; var at = Next(); Spawn(c, at, Facing(at), false, true); nLords++; }
            foreach (var c in prisoners.Take(DeckTalk.MaxPrisoners)) { if (!Room()) break; var at = Next(); Spawn(c, at, Facing(at), false, true); nPris++; }
            int ci = 0;
            for (int i = 0; i < DeckTalk.CrewPerShip && ci < crew.Count && Room(); i++) { var at = Next(); Spawn(crew[ci++], at, Facing(at), false, false); nCrew++; }

            // The other ships: some of their crew on deck
            for (int s = 1; s < _spawned.Count; s++)
            {
                var deck = Naval.DeckFrames(_spawned[s]);
                for (int i = 0; i < Math.Min(DeckTalk.CrewPerShip, deck.Count) && ci < crew.Count; i++)
                {
                    var d = deck[i].rotation.f.AsVec2;
                    Spawn(crew[ci++], deck[i].origin, d.Length > 0.01f ? d.Normalized() : pDir, false, false);
                    nCrew++;
                }
            }
            Log.Info($"[deck] deck ready: {_spawned.Count} ships, {nComp} companions, {nLords} captive lords, {nCrew} crew, {nPris} prisoners");
        }

        private Agent Spawn(CharacterObject c, Vec3 pos, Vec2 dir, bool isPlayer, bool captive)
        {
            try
            {
                var data = new AgentBuildData(c).TroopOrigin(new SimpleAgentOrigin(c)).Team(Mission.PlayerTeam)
                    .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                if (captive) data.Equipment(MapTalkLogic.Rags()).CivilianEquipment(true);
                else if (c.IsHero) data.CivilianEquipment(true);
                var f = PartyBase.MainParty.MapFaction;
                if (f != null) data.ClothingColor1(f.Color).ClothingColor2(f.Color2);
                if (isPlayer) data.Controller(AgentControllerType.Player);
                var agent = Mission.SpawnAgent(data);
                if (agent == null) return null;
                Naval.NoDrowning(agent);
                if (!isPlayer)
                {
                    try { agent.SetLookAgent(null); } catch { }
                    try { agent.SetTargetPosition(agent.Position.AsVec2); } catch { }
                }
                return agent;
            }
            catch (Exception ex) { Log.Error($"Placing {c?.Name} on deck", ex); return null; }
        }

        public override void OnMissionStateFinalized()
        {
            base.OnMissionStateFinalized();
            Naval.EndWind();
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            DeckTalk.Closed();
        }
    }

    /// <summary>UI on deck: walking and talking, the ships dressed in your colours.</summary>
    [ViewCreatorModule]
    public class DeckViews
    {
        [ViewMethod(DeckTalk.MissionName)]
        public static MissionView[] OpenDeck(Mission mission)
        {
            var list = new List<MissionView>
            {
                new MissionCampaignView(),
                new MissionConversationCameraView(),
                SandBoxViewCreator.CreateMissionConversationView(mission),
                ViewCreator.CreateMissionSingleplayerEscapeMenu(CampaignOptions.IsIronmanMode),
                ViewCreator.CreateOptionsUIHandler(),
                ViewCreator.CreateMissionMainAgentEquipDropView(mission),
                new MissionSingleplayerViewHandler(),
                ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                ViewCreator.CreateMissionMainAgentEquipmentController(mission),
                SandBoxViewCreator.CreateMissionBarterView(),
                ViewCreator.CreateMissionLeaveView(),
                new MissionItemContourControllerView(),
                new MissionAgentContourControllerView(),
                ViewCreator.CreatePhotoModeView(),
            };
            var prepare = Naval.PrepareView();
            if (prepare != null) list.Add(prepare);
            return list.ToArray();
        }
    }
}
