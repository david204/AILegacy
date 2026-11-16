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

        public const string EarlyPatches = "ailegacy.early";
        private static readonly HarmonyLib.Harmony Harmony = new HarmonyLib.Harmony("com.ailegacy");
        private static bool _campaignPatched;

        /// <summary>The rest of the patches, once a campaign is running and the game texts are loaded. Once per run.</summary>
        public static void PatchForCampaign()
        {
            if (_campaignPatched) return;
            _campaignPatched = true;
            try { Harmony.PatchAllUncategorized(typeof(SubModule).Assembly); Log.Info("Harmony patches applied"); }
            catch (Exception ex) { Log.Error("Harmony", ex); }
        }

        protected override void OnSubModuleLoad()
        {
            base.OnSubModuleLoad();
            Log.Info("AI Legacy loaded from " + Paths.ModuleDir);
            var cfg = LegacyConfig.Current; // first read, so it shows up in the log
            CrashTrace.Install();
            // Only what the main menu needs is patched now. Patching makes the runtime compile the game's method, and
            // that can set up the game classes it uses; in the menu some of those (CampaignUIHelper) fail because the
            // game texts are not loaded yet, and stay broken for the whole session. The rest waits for the campaign.
            try { Harmony.PatchCategory(typeof(SubModule).Assembly, EarlyPatches); Log.Info("Harmony: menu patches applied"); }
            catch (Exception ex) { Log.Error("Harmony", ex); }
            // Additions to vanilla screens (bars next to the name, history in conversations)
            try
            {
                var ui = Bannerlord.UIExtenderEx.UIExtender.Create("AILegacy");
                ui.Register(typeof(SubModule).Assembly);
                ui.Enable();
                Log.Info("UI extensions registered");
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

        /// <summary>The player is still making their character: the campaign exists but is not running yet.</summary>
        public static bool InCharacterCreation
        {
            get
            {
                try { return TaleWorlds.Core.GameStateManager.Current?.ActiveState is TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationState; }
                catch { return false; }
            }
        }

        protected override void OnApplicationTick(float dt)
        {
            base.OnApplicationTick(dt);
            // Nothing of the mod runs while the character is being made: it has nothing to do there, and a report of the
            // game closing at that screen pointed here
            if (InCharacterCreation) return;
            // Each entry is timed when the performance meter is on (Core/Perf.cs). Off, Begin is one bool check
            long t = Perf.Begin();
            MainThread.Pump();
            Perf.End("tick:MainThread.Pump", t);
            if (Campaign.Current != null)
            {
                t = Perf.Begin(); Dialogue.ChatSession.TickUI(); Perf.End("tick:ChatSession.TickUI", t);
                t = Perf.Begin(); UI.MapHud.Tick(); Perf.End("tick:MapHud", t);
                t = Perf.Begin(); UI.ConversationMixin.Tick(dt); Perf.End("tick:ConversationMixin", t);
            }

            _letterCheck += dt;
            if (_letterCheck >= 1f)
            {
                t = Perf.Begin();
                Core.L.SyncGameTexts();          // "{=AIL_...}" texts, in the language picked in the mod
                _letterCheck = 0f;
                try { if (Campaign.Current != null) LetterSystem.TryShowPending(); } catch (Exception ex) { Log.Error("Letters", ex); }
                try { World.OrderSystem.Pump(); } catch (Exception ex) { Log.Error("Orders", ex); }
                try { SaveFiles.Pump(); } catch (Exception ex) { Log.Error("Files", ex); }
                try { World.WorldSim.Pump(); } catch (Exception ex) { Log.Error("World", ex); }
                try { if (Campaign.Current != null) World.ResidentFaces.Sweep(); } catch (Exception ex) { Log.Error("Neighbours", ex); }
                Perf.End("tick:cada segundo (textos, cartas, órdenes, archivos, mundo)", t);
                Perf.Pump();
            }
            t = Perf.Begin(); Dialogue.Combat.Tick(dt); Perf.End("tick:Combat", t);
            t = Perf.Begin(); Dialogue.Initiative.Tick(dt); Perf.End("tick:Initiative", t);
            t = Perf.Begin(); Dialogue.Justice.Tick(dt); Perf.End("tick:Justice", t);
            t = Perf.Begin(); Dialogue.GroupTalk.Tick(dt); Perf.End("tick:GroupTalk", t);
            t = Perf.Begin(); Dialogue.SceneActions.Tick(dt); Perf.End("tick:SceneActions", t);
            t = Perf.Begin(); AILegacy.Voice.Speech.Tick(); AILegacy.Voice.Listen.Tick(); Perf.End("tick:Voice", t);
            t = Perf.Begin(); Battle.BattleAI.Tick(dt); Perf.End("tick:BattleAI", t);
            t = Perf.Begin(); Scenes.MapTalk.Tick(dt); Perf.End("tick:MapTalk", t);
            t = Perf.Begin(); Dialogue.Ambient.Tick(dt); Perf.End("tick:Ambient", t);
            t = Perf.Begin(); Dialogue.PassBy.Tick(dt); Perf.End("tick:PassBy", t);
            t = Perf.Begin(); World.Diseases.MissionTick(dt); Perf.End("tick:Diseases.MissionTick", t);
            t = Perf.Begin(); UI.Bubbles.Tick(dt); Perf.End("tick:Bubbles", t);
            t = Perf.Begin(); Dialogue.TalkMusic.Tick(dt); Perf.End("tick:TalkMusic", t);
        }

        public override void OnGameEnd(Game game)
        {
            base.OnGameEnd(game);
            try { UI.MapHud.Detach(); UI.Bubbles.Detach(); Web.EditorServer.Stop(); } catch { }
            Dialogue.TalkMusic.Shutdown();
            // Leaving the campaign: whatever is still queued for the campaign files goes to disk first
            try { Memory.SaveFiles.Drain(); } catch { }
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
