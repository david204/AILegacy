using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Prompt;
using AILegacy.Settings;
using SandBox;
using SandBox.Conversation.MissionLogics;
using SandBox.View;
using SandBox.View.Missions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.CampaignSystem.Map;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using SandBox.Missions.MissionLogics;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.MissionViews.Order;
using TaleWorlds.MountAndBlade.View.MissionViews.Singleplayer;

namespace AILegacy.Scenes
{
    /// <summary>Who you talk to on foot: a lord with his party, one of your own people at your halted camp, or a prisoner you hold.</summary>
    internal enum MapTalkMode { Lord, Companion, Prisoner }

    /// <summary>
    /// "Talk in the scene": a talk from the map becomes a 3D scene of the ground you stand on (the battle terrain of that spot of the
    /// map, never a town or castle scene), with its weather and hour, everyone on foot. A lord comes with his party and you with yours,
    /// in ranks behind each of you; a companion or a prisoner meets you at your halted camp. The talk opens by itself a moment after
    /// you arrive. F12 lets you give your troops orders. With an enemy everything is settled there: a battle (the game's own, on this
    /// same ground), a surrender, or being let go; leaving without any of them brings back the map encounter.
    /// </summary>
    internal static class MapTalk
    {
        public const string MissionName = "AILegacyMapTalk";
        private static Hero _pending;
        private static MapTalkMode _pendingMode;
        private static float _wait;
        private static DateTime _queuedAt;
        private static bool _open;
        private static string _fightKind;      // set when the scene opens for a fight (duel agreed on the map)
        internal static string TakeFight() { var k = _fightKind; _fightKind = null; return k; }
        internal static void ResetSession() { _pending = null; _fightKind = null; _open = false; _current = null; _after = null; _settled = false; _battling = false; }

        // The talk settled things with an enemy (they let you go): the encounter ends with the scene
        private static bool _settled;
        // Followers are being asked whether they fight: the battle waits for them
        private static bool _battling;

        public static bool IsOpen => _open;
        public static bool IsPending => _pending != null;
        internal static MapTalkMode CurrentMode { get; private set; }
        private static Hero _current;
        internal static Hero Current => _open ? _current : null;

        // What the scene left for the map to do once it is free (a battle, a surrender…), and until when to wait for it
        private static Action _after;
        private static DateTime _afterUntil;

        /// <summary>How far (map units) a lord's party may be for the scene: the two parties are side by side.</summary>
        private const float LordReach = 5f;

        /// <summary>The kind of scene this hero can be met in right now, or null.</summary>
        public static MapTalkMode? ModeFor(Hero h)
        {
            try
            {
                if (!S.Get.EnableMapTalk || h == null || h == Hero.MainHero || !h.IsAlive || _pending != null || _open) return null;
                if (Mission.Current != null || Settlement.CurrentSettlement != null || Hero.MainHero.IsPrisoner) return null;
                var main = MobileParty.MainParty;
                if (main == null || main.MapEvent != null || main.IsCurrentlyAtSea || main.CurrentSettlement != null) return null;
                if (h.IsPrisoner) return h.PartyBelongedToAsPrisoner == PartyBase.MainParty ? MapTalkMode.Prisoner : (MapTalkMode?)null;
                if (h.PartyBelongedTo == main) return MapTalkMode.Companion;
                var party = h.PartyBelongedTo;
                if (party == null || party.LeaderHero != h || party.MapEvent != null || party.CurrentSettlement != null || party.IsCurrentlyAtSea) return null;
                if (party.Position.Distance(main.Position) > LordReach) return null;
                if (AtWar(h) && !S.Get.MapTalkWithEnemies) return null;
                return MapTalkMode.Lord;
            }
            catch { return null; }
        }

        /// <summary>Whether the "talk on foot" option can be offered for this hero right now.</summary>
        public static bool CanOffer(Hero h) => ModeFor(h) != null;

        /// <summary>A meeting with this lord opens the scene by itself (setting "open the scene automatically").</summary>
        public static bool AutoFor(Hero h) => S.Get.MapTalkAutomatic && ModeFor(h) == MapTalkMode.Lord;

        internal static bool AtWar(Hero h) => h?.MapFaction != null && Hero.MainHero.MapFaction != null && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction);

        /// <summary>The scene opens once the map is free (the conversation or the party screen closed).</summary>
        public static void Queue(Hero h, string fightKind = null)
        {
            var mode = ModeFor(h);
            if (mode == null) return;
            _pending = h; _pendingMode = mode.Value; _wait = 0.3f; _fightKind = fightKind; _queuedAt = DateTime.UtcNow;
            Log.Info($"[map scene] queued: {mode} {h.Name}");
        }

        /// <summary>The map has nothing in front of it: no scene, no conversation, no screen on top, the player free.</summary>
        private static bool MapFree()
        {
            try
            {
                return Campaign.Current != null && Mission.Current == null && Campaign.Current.ConversationManager?.IsConversationInProgress != true
                       && Game.Current?.GameStateManager?.ActiveState is MapState && Hero.MainHero != null && !Hero.MainHero.IsPrisoner;
            }
            catch { return false; }
        }

        /// <summary>After the scene: what it left for the map runs as soon as the map is free (a battle, a surrender).</summary>
        internal static void AfterScene(Action a) { _after = a; _afterUntil = DateTime.UtcNow.AddMinutes(3); }

        public static void Tick(float dt)
        {
            try
            {
                if (_after != null && !_open)
                {
                    if (DateTime.UtcNow > _afterUntil) { _after = null; Log.Warn("[map scene] the map did not become free after the scene"); }
                    else if (Campaign.Current != null && Mission.Current == null && Campaign.Current.ConversationManager?.IsConversationInProgress != true)
                    {
                        var a = _after; _after = null;
                        try { a(); } catch (Exception ex) { Log.Error("After the map scene", ex); }
                    }
                }
                if (_pending == null) return;
                if (Campaign.Current == null) { _pending = null; return; }
                if ((DateTime.UtcNow - _queuedAt).TotalSeconds > 30) { Log.Warn($"[map scene] scene request timed out ({_pending.Name})"); _pending = null; return; }
                if (!MapFree()) return;
                if ((_wait -= dt) > 0f) return;
                var h = _pending; var mode = _pendingMode; _pending = null;
                // the situation may have changed while the map got free
                if (ModeFor(h) != mode) { Log.Info($"[map scene] scene request dropped; the situation changed ({h.Name})"); _fightKind = null; return; }
                Open(h, mode);
            }
            catch (Exception ex) { Log.Error("Talking in the scene", ex); _pending = null; }
        }

        // Scenes the game may give for a spot of the map that are a settlement's, never wanted here
        private static readonly string[] SettlementStarts = { "town_", "castle_", "village_" };
        private static readonly string[] SettlementParts = { "interior", "_arena", "_tavern", "_prison", "_dungeon", "_keep", "_throne", "_lordshall" };

        /// <summary>The battle terrain of the spot of the map you stand on; a settlement scene is never used, the terrain type's then.</summary>
        private static string SceneHere(out MapPatchData patch, out bool hasPatch)
        {
            patch = default(MapPatchData); hasPatch = false;
            string scene = null;
            try
            {
                patch = Campaign.Current.MapSceneWrapper.GetMapPatchAtPosition(MobileParty.MainParty.Position);
                scene = Campaign.Current.Models.SceneModel.GetBattleSceneForMapPatch(patch, false);
                hasPatch = !string.IsNullOrEmpty(scene);
            }
            catch (Exception ex) { Log.Warn("[map scene] map patch: " + ex.Message); }
            string low = (scene ?? "").ToLowerInvariant();
            if (low.Length > 0 && (SettlementStarts.Any(low.StartsWith) || SettlementParts.Any(low.Contains)))
            {
                Log.Info($"[map scene] the map patch gives a settlement scene ({scene}); the terrain scene is used instead");
                scene = null; hasPatch = false;
            }
            if (string.IsNullOrEmpty(scene))
            {
                TerrainType t = TerrainType.Plain;
                try { t = Campaign.Current.MapSceneWrapper.GetFaceTerrainType(MobileParty.MainParty.CurrentNavigationFace); } catch { }
                scene = t == TerrainType.Forest ? "battle_terrain_b" : t == TerrainType.Desert ? "battle_terrain_d" : t == TerrainType.Snow ? "battle_terrain_a"
                      : t == TerrainType.Steppe ? "battle_terrain_c" : "battle_terrain_f";
            }
            return scene;
        }

        private static void Open(Hero h, MapTalkMode mode)
        {
            var party = mode == MapTalkMode.Lord ? h.PartyBelongedTo : MobileParty.MainParty;
            if (party == null || !h.IsAlive) return;
            string scene = SceneHere(out var patch, out bool hasPatch);
            var rec = new MissionInitializerRecord(scene)
            {
                PlayingInCampaignMode = true,
                NeedsRandomTerrain = false,
                RandomTerrainSeed = MBRandom.RandomInt(10000),
                DecalAtlasGroup = 2,
                DamageToFriendsMultiplier = 0f,
                DamageFromPlayerToFriendsMultiplier = 0f,
                AtmosphereOnCampaign = Campaign.Current.Models.MapWeatherModel.GetAtmosphereModel(MobileParty.MainParty.Position),
                SceneHasMapPatch = hasPatch,
            };
            try { rec.TerrainType = (int)Campaign.Current.MapSceneWrapper.GetFaceTerrainType(MobileParty.MainParty.CurrentNavigationFace); } catch { }
            if (hasPatch)
            {
                rec.PatchCoordinates = patch.normalizedCoordinates;
                var dir = mode == MapTalkMode.Lord ? (party.Position.ToVec2() - MobileParty.MainParty.Position.ToVec2()) : new Vec2(0f, 1f);
                rec.PatchEncounterDir = dir.Length > 0.001f ? dir.Normalized() : new Vec2(0f, 1f);
            }
            Log.Info($"[map scene] opening '{scene}' ({mode}) to talk with {h.Name}{(mode == MapTalkMode.Lord && AtWar(h) ? " (enemy: parley)" : "")}");
            _open = true; _current = h; CurrentMode = mode;
            MissionState.OpenNew(MissionName, rec, mission => new MissionBehavior[]
            {
                new CampaignMissionComponent(),
                new MissionConversationLogic(),
                new MissionOptionsComponent(),
                new MissionFightHandler(),
                new MapTalkLogic(h, party, mode),
                new BasicLeaveMissionLogic(),
                new EquipmentControllerLeaveLogic(),
            });
        }

        /// <summary>Scene is over: back on the map, each party goes its own way, unless the scene left something for the map.</summary>
        internal static void Closed()
        {
            _open = false;
            var who = _current; _current = null;
            MainThread.Post(() =>
            {
                try
                {
                    if (_after != null) return;      // a battle or a surrender takes over the encounter
                    if (_settled)
                    {
                        _settled = false;
                        Log.Info("[map scene] enemy lord let the player go");
                        if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement) PlayerEncounter.Finish(true);
                        return;
                    }
                    // With an enemy, the parley doesn't end the encounter: leaving without a battle, a surrender or being let go
                    // brings back the game's own encounter (attack, surrender, ask to be let go...)
                    if (who != null && CurrentMode == MapTalkMode.Lord && AtWar(who) && who.IsAlive && !who.IsPrisoner) { Log.Info("[map scene] outcome left to the map: the encounter continues"); return; }
                    if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement && !Combat.KeepEncounter) PlayerEncounter.Finish(true);
                    if (CurrentMode == MapTalkMode.Lord && who != null) Log.Info($"[map scene] {who.Name}'s party goes on its way");
                }
                catch (Exception ex) { Log.Error("Closing the encounter after the scene", ex); }
            });
        }

        /// <summary>With an enemy in the scene, they let you go: the scene closes and so does the encounter.</summary>
        internal static void Settle() { if (_open) { _settled = true; LeaveSoon(); } }

        /// <summary>
        /// In the scene with an enemy lord: the talk ended in a battle. Each lord riding with you first decides, in his own way,
        /// whether he and his men fight at your side or leave the field; then the scene closes and the game's own battle opens on
        /// this ground, with the real parties, their losses and your orders. No answer in time: the game's usual rule decides.
        /// </summary>
        internal static void EndForBattle(Speaker s)
        {
            if (!_open || Mission.Current == null || _battling) return;
            _battling = true;
            Log.Info($"[map scene] {s?.Name} attacks: map-talk battle on this ground");
            var followers = Followers();
            bool done = false;
            void Go()
            {
                if (done) return;
                done = true; _battling = false;
                AfterScene(() => Combat.QueueMapAttack(s));
                LeaveSoon();
            }
            if (followers.Count == 0) { Go(); return; }
            int left = followers.Count;
            int session = World.WorldSim.Session;
            foreach (var f in followers) AskFollower(f, s, () => { if (session == World.WorldSim.Session && --left <= 0) MainThread.Post(Go); });
            System.Threading.Tasks.Task.Delay(15000).ContinueWith(_ => MainThread.Post(() => { if (session == World.WorldSim.Session) Go(); }));
        }

        /// <summary>Lords who ride with you with their parties: your army, or following you.</summary>
        private static List<Hero> Followers()
        {
            var list = new List<Hero>();
            try
            {
                var main = MobileParty.MainParty;
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsActive || p == main || p.LeaderHero == null || p.MapEvent != null) continue;
                    bool withMe = (main.Army != null && p.Army == main.Army) || World.OrderSystem.FindAny(p.LeaderHero)?.Type == "follow";
                    if (withMe && p.Position.Distance(main.Position) <= 6f) list.Add(p.LeaderHero);
                }
            }
            catch { }
            return list.Take(4).ToList();
        }

        private static readonly Tpl TFollower = new Tpl("map_scene/follower_battle.txt",
@"You are {name}, {who}. You ride with {player}, your {tie}, and {player} is about to fight {enemy} here, now: {theirs} fighting men against {ours} on your side (yours {mine} of them).
Decide, as yourself, whether you and your men fight at {player}'s side or lead them off the field. Weigh your character and honour, your oath and your ties to {player}, whether your realm is at war with them, and the odds.
Answer ONLY with JSON: {""decision"": ""fight"" or ""leave"", ""line"": ""what you say aloud, one short sentence in {language}"", ""reason"": ""a few words in English""}");

        public static void Touch() { var _ = TFollower.Key; }

        private static void AskFollower(Hero h, Speaker enemy, Action done)
        {
            try
            {
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1dda3", $"{h.Name} sopesa si se une a la batalla…"), Colors.Gray));
                var main = MobileParty.MainParty;
                var foe = Combat.MapFoeParty(enemy);
                int ours = Combat.Men(main) + Followers().Sum(x => Combat.Men(x.PartyBelongedTo));
                string tie = h.Clan == Clan.PlayerClan ? "clan kin" : main.Army != null && h.PartyBelongedTo?.Army == main.Army ? "army commander" : "ally";
                string ask = TFollower.Format("name", h.Name, "who", h.IsLord ? $"a noble of clan {h.Clan?.Name}" : "a captain", "player", Hero.MainHero.Name,
                    "tie", tie, "enemy", enemy?.Name ?? "the enemy", "theirs", Combat.Men(foe), "ours", ours, "mine", Combat.Men(h.PartyBelongedTo), "language", S.Get.Language);
                Llm.LlmClient.CompleteAsync(new List<Llm.ChatMessage> { new Llm.ChatMessage("user", ask) }, wantJson: true, maxTokensOverride: 200, task: "maptalk_follower_battle")
                    .ContinueWith(t =>
                    {
                        var r = t.IsFaulted ? null : t.Result;
                        MainThread.Post(() =>
                        {
                            try
                            {
                                if (r == null || !r.Ok) { Log.Info($"[map scene] {h.Name} gave no answer: the usual rule decides"); return; }
                                int a = r.Text.IndexOf('{'), b = r.Text.LastIndexOf('}');
                                if (a < 0 || b <= a) return;
                                var j = Newtonsoft.Json.Linq.JObject.Parse(r.Text.Substring(a, b - a + 1));
                                string line = ((string)j["line"] ?? "").Trim();
                                bool leave = ((string)j["decision"] ?? "").Trim().StartsWith("leave", StringComparison.OrdinalIgnoreCase);
                                if (line.Length > 0) InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1dda4", $"{h.Name}: «{line}»"), Colors.White));
                                if (leave)
                                {
                                    var p = h.PartyBelongedTo;
                                    if (p != null && p.Army != null && p.Army == MobileParty.MainParty.Army && p != p.Army.LeaderParty) p.Army = null;
                                    World.OrderSystem.Cancel(h, true);
                                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1dda5", $"{h.Name} se lleva a los suyos del campo."), Colors.Gray));
                                }
                                else InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1dda6", $"{h.Name} se pone de tu lado."), Colors.Green));
                                Log.Info($"[map scene] follower {h.Name}: {(leave ? "leaves" : "fights")} ({(string)j["reason"]})");
                            }
                            catch (Exception ex) { Log.Error("Map scene: follower's answer", ex); }
                            finally { done(); }
                        });
                    });
            }
            catch (Exception ex) { Log.Error("Map scene: asking a follower", ex); done(); }
        }

        /// <summary>The scene closes on its own a moment after the talk ends (surrender, being let go, a battle).</summary>
        internal static void LeaveSoon() { MapTalkLogic.LeaveIn = 1.5f; }
    }

    /// <summary>Spawns both sides face to face, opens the talk, and gives the player the orders of their troops on F12.</summary>
    internal class MapTalkLogic : MissionLogic
    {
        private readonly Hero _target;
        private readonly MobileParty _party;
        private readonly MapTalkMode _mode;
        private Agent _targetAgent;
        private float _talkIn = -1f;
        private bool _orders;
        internal static float LeaveIn = -1f;
        private readonly Dictionary<Agent, Vec3> _posts = new Dictionary<Agent, Vec3>();

        // At most this many people in the scene, heroes on each side, and captives shown on each side
        private const int MaxAgents = 260, MaxHeroesPerSide = 25, MaxCaptivesPerSide = 15, RankWidth = 10;
        private const float RankDepth = 11.5f;

        public MapTalkLogic(Hero target, MobileParty party, MapTalkMode mode) { _target = target; _party = party; _mode = mode; LeaveIn = -1f; }

        public override void AfterStart()
        {
            base.AfterStart();
            try
            {
                var player = Hero.MainHero;
                Mission.PlayerTeam = Mission.Teams.Add(BattleSideEnum.Defender, player.MapFaction?.Color ?? uint.MaxValue, player.MapFaction?.Color2 ?? uint.MaxValue, player.ClanBanner);
                // The other side, friendly until a fight. Whoever fights you (duel, brawl) is moved there for the fight
                try
                {
                    var other = _mode == MapTalkMode.Lord ? _party.MapFaction : player.MapFaction;
                    Mission.Teams.Add(BattleSideEnum.Attacker, other?.Color ?? uint.MaxValue, other?.Color2 ?? uint.MaxValue, _mode == MapTalkMode.Lord ? _target.ClanBanner : player.ClanBanner);
                }
                catch (Exception ex) { Log.Warn("Second side of the scene: " + ex.Message); }

                FramesFor(out var mine, out var theirs);
                Vec2 toThem = (theirs.origin - mine.origin).AsVec2;
                if (toThem.Length < 0.5f) toThem = new Vec2(0f, 1f);
                toThem = toThem.Normalized();
                Vec3 pPos = Ground(mine.origin - new Vec3(toThem.x, toThem.y) * 1.5f);
                Vec3 tPos = Ground(theirs.origin + new Vec3(toThem.x, toThem.y) * 1.5f);

                Spawn(CharacterObject.PlayerCharacter, PartyBase.MainParty, pPos, toThem, true, Mission.PlayerTeam);
                int left = MaxAgents - 2;

                if (_mode == MapTalkMode.Lord)
                {
                    _targetAgent = Spawn(_target.CharacterObject, _party.Party, tPos, -toThem, false, Mission.PlayerTeam);
                    var mySide = new List<MobileParty> { MobileParty.MainParty };
                    var theirSide = new List<MobileParty> { _party };
                    JoiningParties(mySide, theirSide);
                    Log.Info($"[map scene] nearby parties join the meeting: allies {mySide.Count - 1}, enemies {theirSide.Count - 1}");
                    var myCaptives = Captives(PartyBase.MainParty, null);
                    var theirCaptives = Captives(_party.Party, null);
                    left -= myCaptives.Count + theirCaptives.Count;
                    int perSide = Math.Max(0, left / 2);
                    if (S.Get.MapTalkEscort > 0) perSide = Math.Min(perSide, S.Get.MapTalkEscort);
                    var mine2 = Roster(mySide, Hero.MainHero, perSide);
                    var theirs2 = Roster(theirSide, _target, perSide);
                    Log.Info($"[map scene] rosters: player side {mySide.Count} parties, {mine2.Count} people; other side {theirSide.Count} parties, {theirs2.Count} people; captives {myCaptives.Count}/{theirCaptives.Count}");
                    Ranks(mine2, pPos, -toThem, toThem);
                    Ranks(theirs2, tPos, toThem, -toThem);
                    Aside(myCaptives, pPos, -toThem, toThem, PartyBase.MainParty);
                    Aside(theirCaptives, tPos, toThem, -toThem, _party.Party);
                }
                else
                {
                    // Your halted camp: the one you came to see in front of you, your people around, your captives set apart
                    var captives = Captives(PartyBase.MainParty, _mode == MapTalkMode.Prisoner ? _target : null);
                    if (_mode == MapTalkMode.Prisoner) _targetAgent = SpawnCaptive(_target.CharacterObject, PartyBase.MainParty, tPos, -toThem);
                    else _targetAgent = Spawn(_target.CharacterObject, PartyBase.MainParty, tPos, -toThem, false, Mission.PlayerTeam);
                    left -= captives.Count;
                    int cap = S.Get.MapTalkEscort > 0 ? Math.Min(left, S.Get.MapTalkEscort) : left;
                    var people = Roster(new List<MobileParty> { MobileParty.MainParty }, Hero.MainHero, cap).Where(c => c.HeroObject != _target).ToList();
                    Camp(people, pPos, tPos);
                    Aside(captives, tPos, toThem, -toThem, PartyBase.MainParty);
                    Log.Info($"[map scene] camp: {people.Count} of your people, {captives.Count} captives");
                }
                try { _targetAgent?.SetLookAgent(Agent.Main); } catch { }

                string fight = MapTalk.TakeFight();
                if (fight != null && _targetAgent != null) Combat.QueueSceneFight(Speaker.FromHero(_target), _targetAgent, fight, 4f);
                else
                {
                    _talkIn = 1f;      // the talk opens by itself a moment after you arrive
                    InformationManager.DisplayMessage(new InformationMessage(Hotkeys.Fix(L.F("AIL_e7b1dda0", $"Estás frente a {_target.Name}. F para hablar, Alt+T para hablar a los que tengas cerca, F12 para dar órdenes a tu gente, Tab para marcharte.")), Colors.Yellow));
                }
            }
            catch (Exception ex) { Log.Error("Building the map scene", ex); }
        }

        public override void OnMissionTick(float dt)
        {
            base.OnMissionTick(dt);
            try
            {
                if (_talkIn > 0f && (_talkIn -= dt) <= 0f)
                {
                    _talkIn = -1f;
                    var logic = Mission.GetMissionBehavior<MissionConversationLogic>();
                    if (logic != null && _targetAgent != null && _targetAgent.IsActive() && Campaign.Current.ConversationManager?.IsConversationInProgress != true)
                        logic.StartConversation(_targetAgent, false, false);
                }
                if (LeaveIn > 0f && Campaign.Current.ConversationManager?.IsConversationInProgress != true && (LeaveIn -= dt) <= 0f)
                {
                    LeaveIn = -1f;
                    Mission.EndMission();
                    return;
                }
                if (Input.IsKeyPressed(InputKey.F12) && Agent.Main != null && Agent.Main.IsActive() && Campaign.Current.ConversationManager?.IsConversationInProgress != true)
                    ToggleOrders();
            }
            catch (Exception ex) { Log.Error("Map scene tick", ex); }
        }

        /// <summary>F12: your people take orders (F1-F7) like in a battle, weapons still sheathed; F12 again and they go back to their places.</summary>
        private void ToggleOrders()
        {
            var team = Mission.PlayerTeam;
            if (team == null) return;
            _orders = !_orders;
            if (_orders)
            {
                foreach (var a in team.ActiveAgents.Where(x => x != Agent.Main && x.IsHuman))
                {
                    if (a.Formation == null) a.Formation = team.GetFormation(a.IsRangedCached ? FormationClass.Ranged : FormationClass.Infantry);
                }
                team.PlayerOrderController.Owner = Agent.Main;
                team.SetPlayerRole(true, true);
                Mission.SetMissionMode(MissionMode.Battle, false);
                InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1dda1", "Órdenes: activadas (F1-F7). F12 para que descansen."), Colors.Yellow));
            }
            else
            {
                Mission.SetMissionMode(MissionMode.StartUp, false);
                foreach (var kv in _posts)
                {
                    var a = kv.Key;
                    if (a == null || !a.IsActive()) continue;
                    try
                    {
                        a.Formation = null;
                        var wp = new WorldPosition(Mission.Scene, kv.Value);
                        a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.None);
                    }
                    catch { }
                }
                InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1dda2", "Descansen. F12 para dar órdenes."), Colors.Gray));
            }
        }

        // ---------------------------------------------------------------- who comes

        /// <summary>Parties near enough to be part of the meeting: friends of yours on your side, friends of theirs on theirs.</summary>
        private void JoiningParties(List<MobileParty> mine, List<MobileParty> theirs)
        {
            try
            {
                float reach = 3f;
                try { reach = Math.Max(1.5f, Campaign.Current.Models.EncounterModel.GetEncounterJoiningRadius); } catch { }
                var main = MobileParty.MainParty;
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsActive || p == main || p == _party || p.MapEvent != null || p.CurrentSettlement != null || p.IsCurrentlyAtSea) continue;
                    if (!p.IsLordParty || p.LeaderHero == null || p.Position.Distance(main.Position) > reach) continue;
                    // lords riding with you (army or following you) stand with you
                    bool withMe = (main.Army != null && p.Army == main.Army) || World.OrderSystem.FindAny(p.LeaderHero)?.Type == "follow";
                    if (withMe) { mine.Add(p); Log.Info($"[map scene] a hero following the player stands with them: {p.LeaderHero.Name}, with his party"); continue; }
                    if (p.MapFaction == _party.MapFaction && _party.MapFaction != main.MapFaction) theirs.Add(p);
                    else if (p.MapFaction == main.MapFaction && _party.MapFaction != main.MapFaction) mine.Add(p);
                }
            }
            catch (Exception ex) { Log.Warn("[map scene] nearby parties: " + ex.Message); }
        }

        /// <summary>Everyone fit to stand from those parties: heroes first (at most 25), then the best troops, up to <paramref name="max"/>.</summary>
        private static List<CharacterObject> Roster(List<MobileParty> parties, Hero leader, int max)
        {
            var heroes = new List<CharacterObject>();
            var troops = new List<CharacterObject>();
            try
            {
                foreach (var p in parties)
                {
                    foreach (var e in p.MemberRoster.GetTroopRoster())
                    {
                        if (e.Character == null) continue;
                        if (e.Character.IsHero)
                        {
                            var h = e.Character.HeroObject;
                            if (h == null || h == leader || h == Hero.MainHero || h.IsWounded || heroes.Count >= MaxHeroesPerSide) continue;
                            heroes.Add(e.Character);
                        }
                        else for (int i = 0; i < e.Number - e.WoundedNumber; i++) troops.Add(e.Character);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Scene roster", ex); }
            var all = heroes.Concat(troops.OrderByDescending(t => t.Tier)).Take(Math.Max(0, max)).ToList();
            return all;
        }

        /// <summary>Captives of that party shown in the scene (at most 15), heroes first; <paramref name="skip"/> is spawned apart.</summary>
        private static List<CharacterObject> Captives(PartyBase party, Hero skip)
        {
            var list = new List<CharacterObject>();
            try
            {
                foreach (var e in party.PrisonRoster.GetTroopRoster().OrderByDescending(e => e.Character.IsHero))
                {
                    if (e.Character == null || (skip != null && e.Character.HeroObject == skip)) continue;
                    int n = e.Character.IsHero ? 1 : e.Number;
                    for (int i = 0; i < n && list.Count < MaxCaptivesPerSide; i++) list.Add(e.Character);
                }
            }
            catch { }
            return list;
        }

        // ---------------------------------------------------------------- where they stand

        /// <summary>The two leaders' places: the scene's own conversation points, or the player's spawn and a spot in front of it.</summary>
        private void FramesFor(out MatrixFrame mine, out MatrixFrame theirs)
        {
            mine = MatrixFrame.Identity; theirs = MatrixFrame.Identity;
            bool found = false;
            var set = Mission.Scene.FindEntitiesWithTag("binary_conversation_point").FirstOrDefault();
            if (set != null)
                foreach (var child in set.GetChildren())
                {
                    if (child.HasTag("player_infantry_spawn")) { mine = child.GetGlobalFrame(); found = true; }
                    else if (child.HasTag("opponent_infantry_spawn")) theirs = child.GetGlobalFrame();
                }
            if (!found)
            {
                var sp = Mission.Scene.FindEntityWithTag("spawnpoint_player") ?? Mission.Scene.FindEntitiesWithTag("sp_player_conversation").FirstOrDefault()
                         ?? Mission.Scene.FindEntitiesWithTag("sp_attacker_infantry").FirstOrDefault() ?? Mission.Scene.FindEntitiesWithTag("attacker_infantry").FirstOrDefault();
                if (sp != null) mine = sp.GetGlobalFrame();
                theirs = mine;
                theirs.origin = mine.origin + mine.rotation.f.NormalizedCopy() * 6f;
            }
        }

        private Vec3 Ground(Vec3 p)
        {
            try { p.z = Mission.Scene.GetGroundHeightAtPosition(p); } catch { }
            return p;
        }

        /// <summary>Ranks of 10 behind their leader, the first rank 11.5 m back.</summary>
        private void Ranks(List<CharacterObject> who, Vec3 leaderPos, Vec2 back, Vec2 facing)
        {
            var side = new Vec2(-back.y, back.x);
            for (int i = 0; i < who.Count; i++)
            {
                int row = i / RankWidth, col = i % RankWidth;
                int inRow = Math.Min(RankWidth, who.Count - row * RankWidth);
                float depth = (who[i].IsHero && row == 0) ? 3f : RankDepth + row * 1.6f;
                var off = back * depth + side * ((col - (inRow - 1) / 2f) * 1.5f);
                var party = who[i].HeroObject?.PartyBelongedTo?.Party ?? (facing.DotProduct(back) < 0 ? null : null);
                Spawn(who[i], party, Ground(leaderPos + new Vec3(off.x, off.y)), facing, false, Mission.PlayerTeam);
            }
        }

        /// <summary>Your camp: your people in a loose ring around you, looking in.</summary>
        private void Camp(List<CharacterObject> who, Vec3 me, Vec3 them)
        {
            var center = (me + them) * 0.5f;
            for (int i = 0; i < who.Count; i++)
            {
                int ring = i / 16, k = i % 16;
                float r = 7f + ring * 2.5f;
                float ang = (k / 16f) * (float)Math.PI * 2f + ring * 0.2f;
                var pos = Ground(center + new Vec3((float)Math.Cos(ang) * r, (float)Math.Sin(ang) * r));
                var look = (center - pos).AsVec2;
                Spawn(who[i], PartyBase.MainParty, pos, look.Length > 0.01f ? look.Normalized() : new Vec2(0f, 1f), false, Mission.PlayerTeam);
            }
        }

        /// <summary>Captives set apart, to one side of their captors.</summary>
        private void Aside(List<CharacterObject> who, Vec3 leaderPos, Vec2 back, Vec2 facing, PartyBase captor)
        {
            var side = new Vec2(-back.y, back.x);
            for (int i = 0; i < who.Count; i++)
            {
                var off = side * (8f + (i % 5) * 1.3f) + back * (2f + (i / 5) * 1.5f);
                SpawnCaptive(who[i], captor, Ground(leaderPos + new Vec3(off.x, off.y)), facing);
            }
        }

        // ---------------------------------------------------------------- spawning

        private Agent Spawn(CharacterObject c, PartyBase party, Vec3 pos, Vec2 dir, bool isPlayer, Team team)
        {
            try
            {
                party = party ?? c.HeroObject?.PartyBelongedTo?.Party ?? PartyBase.MainParty;
                var data = new AgentBuildData(c).TroopOrigin(new SimpleAgentOrigin(c)).Team(team)
                    .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                var banner = party?.LeaderHero?.ClanBanner ?? party?.MapFaction?.Banner;
                if (banner != null) data.Banner(banner);
                if (party?.MapFaction != null) data.ClothingColor1(party.MapFaction.Color).ClothingColor2(party.MapFaction.Color2);
                if (isPlayer) data.Controller(AgentControllerType.Player);
                var agent = Mission.SpawnAgent(data);
                if (!isPlayer && agent != null)
                {
                    try { agent.SetLookAgent(null); } catch { }
                    _posts[agent] = pos;
                }
                return agent;
            }
            catch (Exception ex) { Log.Error($"Placing {c?.Name} in the scene", ex); return null; }
        }

        private static Equipment _rags;

        /// <summary>A captive: in rags, empty-handed, standing where they are put.</summary>
        private Agent SpawnCaptive(CharacterObject c, PartyBase captor, Vec3 pos, Vec2 dir)
        {
            try
            {
                if (_rags == null)
                {
                    _rags = new Equipment(Equipment.EquipmentType.Civilian);
                    foreach (var (slot, id) in new[] { (EquipmentIndex.Body, "ragged_robes"), (EquipmentIndex.Gloves, "ragged_armwraps"), (EquipmentIndex.Leg, "ragged_boots") })
                    {
                        var item = TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObject<ItemObject>(id);
                        if (item != null) _rags[slot] = new EquipmentElement(item);
                    }
                }
                var data = new AgentBuildData(c).TroopOrigin(new SimpleAgentOrigin(c)).Team(Mission.PlayerTeam)
                    .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true).Equipment(_rags.Clone(false)).CivilianEquipment(true);
                var agent = Mission.SpawnAgent(data);
                if (agent != null) { try { agent.SetLookAgent(null); } catch { } }
                return agent;
            }
            catch (Exception ex) { Log.Error($"Placing the captive {c?.Name} in the scene", ex); return null; }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            MapTalk.Closed();
        }
    }

    /// <summary>UI for this scene: the village walk UI (talk, leave, status) and the battle order bar for F12.</summary>
    [ViewCreatorModule]
    public class MapTalkViews
    {
        [ViewMethod(MapTalk.MissionName)]
        public static MissionView[] OpenMapTalk(Mission mission)
        {
            return new List<MissionView>
            {
                new MissionCampaignView(),
                new MissionConversationCameraView(),
                SandBoxViewCreator.CreateMissionConversationView(mission),
                ViewCreator.CreateMissionSingleplayerEscapeMenu(CampaignOptions.IsIronmanMode),
                ViewCreator.CreateOptionsUIHandler(),
                ViewCreator.CreateMissionMainAgentEquipDropView(mission),
                ViewCreator.CreateMissionOrderUIHandler(null),
                new OrderTroopPlacer(null),
                ViewCreator.CreateMissionFormationMarkerUIHandler(mission),
                new MissionFormationTargetSelectionHandler(),
                new MissionSingleplayerViewHandler(),
                ViewCreator.CreateMissionAgentStatusUIHandler(mission),
                ViewCreator.CreateMissionMainAgentEquipmentController(mission),
                ViewCreator.CreateMissionAgentLockVisualizerView(mission),
                SandBoxViewCreator.CreateMissionBarterView(),
                ViewCreator.CreateMissionLeaveView(),
                new MissionItemContourControllerView(),
                new MissionAgentContourControllerView(),
                ViewCreator.CreatePhotoModeView(),
            }.ToArray();
        }
    }
}
