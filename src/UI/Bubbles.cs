using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.Engine;
using TaleWorlds.Engine.GauntletUI;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.Screens;
using TaleWorlds.ScreenSystem;

namespace AILegacy.UI
{
    public class BubbleVM : ViewModel
    {
        private string _text = "";
        private float _x, _y;
        private bool _isVisible;
        [DataSourceProperty] public string Text { get => _text; set { if (value != _text) { _text = value; OnPropertyChangedWithValue(value, nameof(Text)); } } }
        [DataSourceProperty] public float X { get => _x; set { if (value != _x) { _x = value; OnPropertyChangedWithValue(value, nameof(X)); } } }
        [DataSourceProperty] public float Y { get => _y; set { if (value != _y) { _y = value; OnPropertyChangedWithValue(value, nameof(Y)); } } }
        [DataSourceProperty] public bool IsVisible { get => _isVisible; set { if (value != _isVisible) { _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); } } }
    }

    public class BubblesVM : ViewModel
    {
        private MBBindingList<BubbleVM> _bubbles = new MBBindingList<BubbleVM>();
        [DataSourceProperty] public MBBindingList<BubbleVM> Bubbles { get => _bubbles; set { if (value != _bubbles) { _bubbles = value; OnPropertyChangedWithValue(value, nameof(Bubbles)); } } }
    }

    /// <summary>
    /// Texto sobre la cabeza de los personajes en una escena (lo que dicen entre ellos, lo que te gritan al pasar).
    /// Es una capa propia sobre la pantalla de la misión: cada frase sigue a su personaje y se borra sola.
    /// </summary>
    internal static class Bubbles
    {
        private class Item { public Agent Agent; public BubbleVM VM; public float Left; public bool Logged; }

        private static readonly List<Item> Items = new List<Item>();
        private static GauntletLayer _layer;
        private static ScreenBase _screen;
        private static BubblesVM _vm;
        private static bool _broken;      // si la interfaz falla una vez, no se insiste en esta sesión

        public static bool Available => !_broken && S.Get.EnableBubbles && ScreenManager.TopScreen is MissionScreen;

        /// <summary>Enseña una frase sobre la cabeza de ese personaje durante unos segundos.</summary>
        public static void Show(Agent agent, string text, float seconds)
        {
            try
            {
                if (_broken || agent == null || string.IsNullOrWhiteSpace(text) || !S.Get.EnableBubbles) return;
                if (!Attach()) return;
                text = text.Replace("{", "(").Replace("}", ")").Trim();
                if (text.Length > 220) text = text.Substring(0, 220) + "…";
                var old = Items.FirstOrDefault(i => i.Agent == agent);
                if (old != null) { old.VM.Text = text; old.Left = seconds; return; }
                var vm = new BubbleVM { Text = text, IsVisible = false };
                _vm.Bubbles.Add(vm);
                Items.Add(new Item { Agent = agent, VM = vm, Left = seconds });
            }
            catch (Exception ex) { Fail(L.T("AIL_2ef46d51", "enseñando una frase"), ex); }
        }

        private static bool Attach()
        {
            var top = ScreenManager.TopScreen as MissionScreen;
            if (top == null) return false;
            if (_layer != null && _screen == top) return true;
            Detach();
            _vm = new BubblesVM();
            _layer = new GauntletLayer("AILegacyBubbles", 20);
            _layer.LoadMovie("AILegacyBubbles", _vm);
            top.AddLayer(_layer);
            _screen = top;
            return true;
        }

        public static void Detach()
        {
            try { if (_layer != null && _screen != null) _screen.RemoveLayer(_layer); } catch { }
            _layer = null; _screen = null; _vm = null;
            Items.Clear();
        }

        private static void Fail(string where, Exception ex)
        {
            Log.Error("Texto sobre la cabeza: " + where, ex);
            _broken = true;
            Detach();
        }

        public static void Tick(float dt)
        {
            if (_layer == null) return;
            try
            {
                var screen = ScreenManager.TopScreen as MissionScreen;
                if (screen == null || screen != _screen || Mission.Current == null) { Detach(); return; }
                var cam = screen.CombatCamera;
                float scale = Screen.RealScreenResolutionHeight / 1080f;
                for (int i = Items.Count - 1; i >= 0; i--)
                {
                    var it = Items[i];
                    it.Left -= dt;
                    if (it.Left <= 0f || it.Agent == null || !it.Agent.IsActive())
                    {
                        _vm.Bubbles.Remove(it.VM);
                        Items.RemoveAt(i);
                        continue;
                    }
                    if (cam == null) { it.VM.IsVisible = false; continue; }
                    // Punto real de los ojos en el mundo, y un poco más arriba (GetEyeGlobalHeight no servía: el texto caía fuera de la pantalla)
                    var pos = it.Agent.GetEyeGlobalPosition();
                    pos.z += 0.4f;
                    float x = -5000f, y = -5000f, w = -1f;
                    MBWindowManager.WorldToScreenInsideUsableArea(cam, pos, ref x, ref y, ref w);
                    if (!it.Logged)
                    {
                        it.Logged = true;
                        Log.Info($"[texto] «{(it.VM.Text.Length > 40 ? it.VM.Text.Substring(0, 40) + "…" : it.VM.Text)}» → pantalla x={x:0} y={y:0} w={w:0.00} (resolución {Screen.RealScreenResolutionWidth:0}x{Screen.RealScreenResolutionHeight:0}, escala {scale:0.00})");
                    }
                    bool visible = w > 0f && x > -200f && y > -200f && x < Screen.RealScreenResolutionWidth + 200f && y < Screen.RealScreenResolutionHeight + 200f
                                   && (Agent.Main == null || it.Agent.Position.Distance(Agent.Main.Position) <= S.Get.BubbleMaxDistance);
                    it.VM.IsVisible = visible;
                    if (!visible) continue;
                    it.VM.X = x - 170f * scale;       // centrado sobre el personaje (el cuadro mide 340)
                    it.VM.Y = y - 70f * scale;
                }
            }
            catch (Exception ex) { Fail("actualizando posiciones", ex); }
        }
    }
}
