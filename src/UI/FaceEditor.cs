using System;
using AILegacy.Core;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade.GauntletUI.BodyGenerator;
using TaleWorlds.ScreenSystem;

namespace AILegacy.UI
{
    /// <summary>
    /// The game's face and body editor for any of your people (a relative or companion made in the editor), as AIInfluence
    /// opens it: what you shape is kept on that character; their age stays theirs.
    /// </summary>
    internal sealed class FaceEditor : ScreenBase
    {
        private readonly Hero _hero;
        private readonly BodyGeneratorView _view;

        /// <summary>Opens it for that hero, on the map only. The reason in the player's language when it cannot.</summary>
        public static string Open(Hero h)
        {
            try
            {
                if (h == null || !h.IsAlive) return L.T("AIL_10f114fb", "Ese personaje ya no existe en la partida.");
                if (TaleWorlds.MountAndBlade.Mission.Current != null || !(Game.Current?.GameStateManager?.ActiveState is TaleWorlds.CampaignSystem.GameState.MapState))
                    return L.T("AIL_e7b1dea1", "Abre el editor de cara desde el mapa, fuera de escenas y menús.");
                ScreenManager.PushScreen(new FaceEditor(h));
                Log.Info($"[face] appearance editor opened for {h.Name}");
                return null;
            }
            catch (Exception ex) { Log.Error("Opening the face editor", ex); return L.T("AIL_e7b1dea2", "No se pudo abrir el editor de cara; mira ailegacy.log."); }
        }

        private FaceEditor(Hero hero)
        {
            _hero = hero;
            _view = new BodyGeneratorView(OnDone, GameTexts.FindText("str_done"), OnCancel, GameTexts.FindText("str_cancel"), hero.CharacterObject, false, CharacterHelper.GetFaceGeneratorFilter());
        }

        protected override void OnInitialize()
        {
            base.OnInitialize();
            Game.Current.GameStateManager.RegisterActiveStateDisableRequest(this);
            AddLayer(_view.GauntletLayer);
            InformationManager.HideAllMessages();
        }

        protected override void OnActivate()
        {
            base.OnActivate();
            AddLayer(_view.SceneLayer);
        }

        protected override void OnDeactivate()
        {
            base.OnDeactivate();
            _view.OnFinalize();
            LoadingWindow.EnableGlobalLoadingWindow();
            MBInformationManager.HideInformations();
        }

        protected override void OnFinalize()
        {
            base.OnFinalize();
            if (LoadingWindow.IsLoadingWindowActive) LoadingWindow.DisableGlobalLoadingWindow();
            Game.Current.GameStateManager.UnregisterActiveStateDisableRequest(this);
        }

        protected override void OnFrameTick(float dt)
        {
            base.OnFrameTick(dt);
            _view.OnTick(dt);
        }

        private void OnDone()
        {
            try
            {
                var bp = _view.BodyGen.CurrentBodyProperties;
                _hero.StaticBodyProperties = bp.StaticProperties;
                _hero.Weight = bp.Weight;
                _hero.Build = bp.Build;
                Log.Info($"[face] new appearance kept for {_hero.Name}");
            }
            catch (Exception ex) { Log.Error("Keeping the new appearance", ex); }
            ScreenManager.PopScreen();
        }

        private void OnCancel() => ScreenManager.PopScreen();
    }
}
