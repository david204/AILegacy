using System;
using AILegacy.Behaviors;
using AILegacy.Config;
using AILegacy.Core;
using AILegacy.Letters;
using AILegacy.Memory;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy
{
    public class SubModule : MBSubModuleBase
    {
        private float _letterCheck;

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("AI Legacy cargado desde " + Paths.ModuleDir);
            var cfg = LegacyConfig.Current; // primera lectura (queda en el log)
            try { new HarmonyLib.Harmony("com.ailegacy").PatchAll(typeof(SubModule).Assembly); Log.Info("Parches de Harmony aplicados"); }
            catch (Exception ex) { Log.Error("Harmony", ex); }
            // Lo que se añade a pantallas del propio juego (barras junto al nombre e historial en la conversación)
            try
            {
                var ui = Bannerlord.UIExtenderEx.UIExtender.Create("AILegacy");
                ui.Register(typeof(SubModule).Assembly);
                ui.Enable();
                Log.Info("Extensiones de interfaz registradas");
            }
            catch (Exception ex) { Log.Error("UIExtenderEx", ex); }
        }

        protected override void OnGameStart(Game game, IGameStarter gameStarterObject)
        {
            base.OnGameStart(game, gameStarterObject);
            if (game.GameType is Campaign && gameStarterObject is CampaignGameStarter starter)
            {
                MemoryStore.Clear(); // partida nueva o cargada: SyncData rellenará si hay datos
                starter.AddBehavior(new AILegacyBehavior());
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            MainThread.Pump();
            if (Campaign.Current != null)
            {
                Dialogue.ChatSession.TickUI();
                UI.MapHud.Tick();
                UI.ConversationMixin.Tick(dt);
            }

            // Cartas pendientes: comprobar cada segundo si el jugador está en el mapa
            _letterCheck += dt;
            if (_letterCheck >= 1f)
            {
                Core.L.SyncGameTexts();          // los textos «{=AIL_…}», en el idioma elegido en el mod
                _letterCheck = 0f;
                try { if (Campaign.Current != null) LetterSystem.TryShowPending(); } catch (Exception ex) { Log.Error("Cartas", ex); }
                try { World.OrderSystem.Pump(); } catch (Exception ex) { Log.Error("Órdenes", ex); }
                try { SaveFiles.Pump(); } catch (Exception ex) { Log.Error("Archivos", ex); }
                try { World.WorldSim.Pump(); } catch (Exception ex) { Log.Error("Mundo", ex); }
            }
            Dialogue.Combat.Tick(dt);
            Dialogue.Initiative.Tick(dt);
            Dialogue.Justice.Tick(dt);
            Dialogue.GroupTalk.Tick(dt);
            Dialogue.SceneActions.Tick(dt);
            Battle.BattleAI.Tick(dt);
            Scenes.MapTalk.Tick(dt);
            Dialogue.Ambient.Tick(dt);
            Dialogue.PassBy.Tick(dt);
            UI.Bubbles.Tick(dt);
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            try { UI.MapHud.Detach(); UI.Bubbles.Detach(); Web.EditorServer.Stop(); } catch { }
        }

        public override void OnGameLoaded(Game game, object initializerObject)
        {
            base.OnGameLoaded(game, initializerObject);
            try
            {
                if (LegacyConfig.LastLoadError != null)
                    InformationManager.DisplayMessage(new InformationMessage("AI Legacy: " + LegacyConfig.LastLoadError, Colors.Red));
            }
            catch (Exception ex) { Log.Error("OnGameLoaded", ex); }
        }
    }
}
