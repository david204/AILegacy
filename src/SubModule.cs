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
            var cfg = LegacyConfig.Current; // first read, so it shows up in the log
            try { new HarmonyLib.Harmony("com.ailegacy").PatchAll(typeof(SubModule).Assembly); Log.Info("Parches de Harmony aplicados"); }
            catch (Exception ex) { Log.Error("Harmony", ex); }
            // Additions to vanilla screens (bars next to the name, history in conversations)
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
                MemoryStore.Clear(); // new or loaded game; SyncData fills it if there's saved data
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

            _letterCheck += dt;
            if (_letterCheck >= 1f)
            {
                Core.L.SyncGameTexts();          // "{=AIL_...}" texts, in the language picked in the mod
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
