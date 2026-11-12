using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Settings;
using SandBox;
using SandBox.Conversation.MissionLogics;
using SandBox.View;
using SandBox.View.Missions;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.AgentOrigins;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using SandBox.Missions.MissionLogics;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.Source.Missions;
using TaleWorlds.MountAndBlade.View;
using TaleWorlds.MountAndBlade.View.MissionViews;
using TaleWorlds.MountAndBlade.View.MissionViews.Singleplayer;

namespace AILegacy.Scenes
{
    /// <summary>
    /// «Hablar en la escena»: un encuentro del mapa con un señor se convierte en una escena 3D del terreno donde estáis
    /// cara a cara, a pie, cada uno con su gente detrás. Allí funciona todo lo de las escenas (hablar con F, grupo con Alt+T…).
    /// </summary>
    internal static class MapTalk
    {
        public const string MissionName = "AILegacyMapTalk";

        private static Hero _pending;
        private static float _wait;
        private static bool _open;
        private static string _fightKind;      // si la escena se abre para pelear (duelo acordado en el mapa)
        internal static string TakeFight() { var k = _fightKind; _fightKind = null; return k; }
        internal static void ResetSession() { _pending = null; _fightKind = null; _open = false; _current = null; }

        public static bool IsOpen => _open;

        /// <summary>¿Se le puede proponer hablar a pie a este personaje ahora?</summary>
        public static bool CanOffer(Hero h)
        {
            try
            {
                if (!S.Get.EnableMapTalk || h == null || _pending != null || _open) return false;
                if (Mission.Current != null || Settlement.CurrentSettlement != null) return false;
                var party = h.PartyBelongedTo;
                if (party == null || party == MobileParty.MainParty || party.MapEvent != null || MobileParty.MainParty.MapEvent != null) return false;
                if (party.IsCurrentlyAtSea || MobileParty.MainParty.IsCurrentlyAtSea) return false;
                if (AtWar(h) && !S.Get.MapTalkWithEnemies) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool AtWar(Hero h) => h?.MapFaction != null && Hero.MainHero.MapFaction != null && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction);
        private static Hero _current;

        /// <summary>Al cerrar la charla del mapa, se abre la escena.</summary>
        public static void Queue(Hero h, string fightKind = null) { _pending = h; _wait = 0.3f; _fightKind = fightKind; }

        public static void Tick(float dt)
        {
            if (_pending == null) return;
            try
            {
                if (Campaign.Current == null) { _pending = null; return; }
                if (Campaign.Current.ConversationManager?.IsConversationInProgress == true || Mission.Current != null) return;
                if ((_wait -= dt) > 0f) return;
                var h = _pending; _pending = null;
                Open(h);
            }
            catch (Exception ex) { Log.Error("Hablar en la escena", ex); _pending = null; }
        }

        private static void Open(Hero h)
        {
            var party = h.PartyBelongedTo;
            if (party == null || !h.IsAlive) return;
            string scene = Campaign.Current.Models.SceneModel.GetConversationSceneForMapPosition(party.Position);
            if (string.IsNullOrEmpty(scene)) { Log.Warn("[escena del mapa] no hay escena para este terreno"); return; }
            var rec = SandBoxMissions.CreateSandBoxMissionInitializerRecord(scene, "", false, DecalAtlasGroup.Town);
            Log.Info($"[escena del mapa] se abre «{scene}» para hablar con {h.Name}{(AtWar(h) ? " (enemigo: parlamento)" : "")}");
            _open = true; _current = h;
            MissionState.OpenNew(MissionName, rec, mission => new MissionBehavior[]
            {
                new CampaignMissionComponent(),
                new MissionConversationLogic(),
                new MissionOptionsComponent(),
                new MissionFightHandler(),
                new MapTalkLogic(h, party),
                new BasicLeaveMissionLogic(),
                new EquipmentControllerLeaveLogic(),
            });
        }

        /// <summary>La escena ha terminado: de vuelta al mapa, cada uno sigue su camino.</summary>
        internal static void Closed()
        {
            _open = false;
            var who = _current; _current = null;
            MainThread.Post(() =>
            {
                try
                {
                    // Con un enemigo, el parlamento no cierra el encuentro: al volver al mapa sigue ahí (atacar, rendirse, que te deje ir…),
                    // salvo que en la charla haya quedado resuelto.
                    if (who != null && AtWar(who) && who.IsAlive && !who.IsPrisoner) { Log.Info("[escena del mapa] fin del parlamento con un enemigo: el encuentro sigue"); return; }
                    if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement && !Combat.KeepEncounter) PlayerEncounter.Finish(true);
                }
                catch (Exception ex) { Log.Error("Cerrar el encuentro tras la escena", ex); }
            });
        }
    }

    /// <summary>Coloca a los dos jefes frente a frente y a su gente detrás.</summary>
    internal class MapTalkLogic : MissionLogic
    {
        private readonly Hero _lord;
        private readonly MobileParty _party;

        public MapTalkLogic(Hero lord, MobileParty party) { _lord = lord; _party = party; }

        public override void AfterStart()
        {
            base.AfterStart();
            try
            {
                var player = Hero.MainHero;
                Mission.PlayerTeam = Mission.Teams.Add(BattleSideEnum.Attacker, player.MapFaction.Color, player.MapFaction.Color2);
                // Un segundo bando vacío: ahí pasa quien pelee contigo (duelo, pelea) mientras dure
                try { Mission.Teams.Add(BattleSideEnum.Defender, _party.MapFaction?.Color ?? uint.MaxValue, _party.MapFaction?.Color2 ?? uint.MaxValue); } catch (Exception ex) { Log.Warn("Segundo bando de la escena: " + ex.Message); }

                // Los puntos de encuentro que traen las escenas de conversación del juego
                MatrixFrame mine = MatrixFrame.Identity, theirs = MatrixFrame.Identity;
                bool found = false;
                var set = Mission.Scene.FindEntitiesWithTag("binary_conversation_point").FirstOrDefault();
                if (set != null)
                {
                    foreach (var child in set.GetChildren())
                    {
                        if (child.HasTag("player_infantry_spawn")) { mine = child.GetGlobalFrame(); found = true; }
                        else if (child.HasTag("opponent_infantry_spawn")) theirs = child.GetGlobalFrame();
                    }
                }
                if (!found)
                {
                    var sp = Mission.Scene.FindEntityWithTag("spawnpoint_player") ?? Mission.Scene.FindEntitiesWithTag("sp_player_conversation").FirstOrDefault();
                    if (sp != null) mine = sp.GetGlobalFrame();
                    theirs = mine;
                    theirs.origin = mine.origin + mine.rotation.f.NormalizedCopy() * 5f;
                }
                Vec2 toThem = (theirs.origin - mine.origin).AsVec2;
                if (toThem.Length < 0.5f) toThem = new Vec2(0f, 1f);
                toThem = toThem.Normalized();
                // Un poco más separados que en una charla de cámara fija, para poder andar entre los dos grupos
                Vec3 pPos = Ground(mine.origin - new Vec3(toThem.x, toThem.y) * 1.5f);
                Vec3 lPos = Ground(theirs.origin + new Vec3(toThem.x, toThem.y) * 1.5f);

                Spawn(CharacterObject.PlayerCharacter, PartyBase.MainParty, pPos, toThem, true);
                var lordAgent = Spawn(_lord.CharacterObject, _party.Party, lPos, -toThem, false);
                try { lordAgent?.SetLookAgent(Agent.Main); } catch { }

                int escort = S.Get.MapTalkEscort;
                Line(Escort(PartyBase.MainParty, player, escort), PartyBase.MainParty, pPos, -toThem, toThem);
                Line(Escort(_party.Party, _lord, escort), _party.Party, lPos, toThem, -toThem);

                string fight = MapTalk.TakeFight();
                if (fight != null && lordAgent != null) Combat.QueueSceneFight(Speaker.FromHero(_lord), lordAgent, fight, 4f);
                else InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_7c146680", $"Estás frente a {_lord.Name}. F para hablarle, Alt+T para hablar a los que tengas cerca, Tab para marcharte."), Colors.Yellow));
            }
            catch (Exception ex) { Log.Error("Montar la escena del mapa", ex); }
        }

        private Vec3 Ground(Vec3 p)
        {
            try { p.z = Mission.Scene.GetGroundHeightAtPosition(p); } catch { }
            return p;
        }

        /// <summary>Quién acompaña al jefe: primero sus héroes, luego sus mejores tropas.</summary>
        private static List<CharacterObject> Escort(PartyBase party, Hero leader, int max)
        {
            var list = new List<CharacterObject>();
            try
            {
                var roster = party.MemberRoster.GetTroopRoster();
                foreach (var e in roster.Where(e => e.Character.IsHero && e.Character.HeroObject != leader && e.Character.HeroObject != Hero.MainHero && !e.Character.HeroObject.IsWounded))
                    if (list.Count < max) list.Add(e.Character);
                foreach (var e in roster.Where(e => !e.Character.IsHero && e.Number - e.WoundedNumber > 0).OrderByDescending(e => e.Character.Tier))
                    for (int i = 0; i < e.Number - e.WoundedNumber && list.Count < max; i++) list.Add(e.Character);
            }
            catch (Exception ex) { Log.Error("Escolta de la escena", ex); }
            return list;
        }

        /// <summary>Los coloca en filas detrás de su jefe.</summary>
        private void Line(List<CharacterObject> who, PartyBase party, Vec3 leaderPos, Vec2 back, Vec2 facing)
        {
            var side = new Vec2(-back.y, back.x);
            const int cols = 5;
            for (int i = 0; i < who.Count; i++)
            {
                int row = i / cols, col = i % cols;
                int inRow = Math.Min(cols, who.Count - row * cols);
                var off = back * (3f + row * 1.6f) + side * ((col - (inRow - 1) / 2f) * 1.5f);
                Spawn(who[i], party, Ground(leaderPos + new Vec3(off.x, off.y)), facing, false);
            }
        }

        private Agent Spawn(CharacterObject c, PartyBase party, Vec3 pos, Vec2 dir, bool isPlayer)
        {
            try
            {
                var data = new AgentBuildData(c).TroopOrigin(new SimpleAgentOrigin(c)).Team(Mission.PlayerTeam)
                    .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                var banner = party?.LeaderHero?.ClanBanner ?? party?.MapFaction?.Banner;
                if (banner != null) data.Banner(banner);
                if (party?.MapFaction != null) data.ClothingColor1(party.MapFaction.Color).ClothingColor2(party.MapFaction.Color2);
                if (isPlayer) data.Controller(AgentControllerType.Player);
                var agent = Mission.SpawnAgent(data);
                if (!isPlayer && agent != null) { try { agent.SetLookAgent(null); } catch { } }
                return agent;
            }
            catch (Exception ex) { Log.Error($"Poner a {c?.Name} en la escena", ex); return null; }
        }

        protected override void OnEndMission()
        {
            base.OnEndMission();
            MapTalk.Closed();
        }
    }

    /// <summary>La interfaz de esa escena: la de un paseo por una aldea (hablar, salir, estado), sin lo que depende de un asentamiento.</summary>
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
