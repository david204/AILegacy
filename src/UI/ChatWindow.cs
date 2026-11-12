using System;
using AILegacy.Core;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.InputSystem;
using TaleWorlds.ScreenSystem;

namespace AILegacy.UI
{
    /// <summary>Capa Gauntlet con la ventana de chat (abrir, cerrar, Enter/Escape).</summary>
    internal class ChatWindow
    {
        public ChatVM VM { get; }
        private GauntletLayer _layer;
        private GauntletMovieIdentifier _movie;
        private ScreenBase _screen;

        public static ChatWindow Current { get; private set; }

        private ChatWindow(ChatVM vm) { VM = vm; }

        /// <summary>Abre la ventana. Devuelve null si no se pudo (y entonces se usa el modo antiguo de ventanas de texto).</summary>
        public static ChatWindow Open(ChatVM vm)
        {
            try
            {
                Current?.Close();
                var w = new ChatWindow(vm);
                w._screen = ScreenManager.TopScreen;
                if (w._screen == null) return null;
                w._layer = new GauntletLayer("AILegacyChatLayer", 450);
                w._movie = w._layer.LoadMovie("AILegacyChat", vm);
                w._layer.Input.RegisterHotKeyCategory(HotKeyManager.GetCategory("GenericPanelGameKeyCategory"));
                w._layer.IsFocusLayer = true;
                w._screen.AddLayer(w._layer);
                w._layer.InputRestrictions.SetInputRestrictions();
                ScreenManager.TrySetFocus(w._layer);
                Current = w;
                return w;
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo abrir la ventana de chat; uso ventanas de texto", ex);
                return null;
            }
        }

        public void Tick()
        {
            if (_layer == null) return;
            try
            {
                if (_layer.Input.IsHotKeyReleased("Confirm")) VM.ExecuteSend();
                else if (_layer.Input.IsHotKeyReleased("Exit")) VM.ExecuteClose();
            }
            catch (Exception ex) { Log.Error("Chat: teclas", ex); }
        }

        public void Close()
        {
            try
            {
                if (_layer != null)
                {
                    _layer.InputRestrictions.ResetInputRestrictions();
                    ScreenManager.TryLoseFocus(_layer);
                    if (_movie != null) _layer.ReleaseMovie(_movie);
                    _screen?.RemoveLayer(_layer);
                }
                VM?.OnFinalize();
            }
            catch (Exception ex) { Log.Error("Chat: cerrar ventana", ex); }
            finally
            {
                _layer = null; _movie = null; _screen = null;
                if (Current == this) Current = null;
            }
        }
    }
}
