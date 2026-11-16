using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.Core;
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
        private bool _isVisible, _isEnemy, _isThinking;
        private int _fontSize = 20;
        [DataSourceProperty] public string Text { get => _text; set { if (value != _text) { _text = value; OnPropertyChangedWithValue(value, nameof(Text)); } } }
        [DataSourceProperty] public float X { get => _x; set { if (value != _x) { _x = value; OnPropertyChangedWithValue(value, nameof(X)); } } }
        [DataSourceProperty] public float Y { get => _y; set { if (value != _y) { _y = value; OnPropertyChangedWithValue(value, nameof(Y)); } } }
        [DataSourceProperty] public bool IsVisible { get => _isVisible; set { if (value != _isVisible) { _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); } } }
        [DataSourceProperty] public int FontSize { get => _fontSize; set { if (value != _fontSize) { _fontSize = value; OnPropertyChangedWithValue(value, nameof(FontSize)); } } }

        /// <summary>Red text: the speaker is hostile to you.</summary>
        [DataSourceProperty] public bool IsEnemy { get => _isEnemy; set { if (value != _isEnemy) { _isEnemy = value; OnPropertyChangedWithValue(value, nameof(IsEnemy)); Flags(); } } }
        /// <summary>The "..." plate over someone whose reply is being written.</summary>
        [DataSourceProperty] public bool IsThinking { get => _isThinking; set { if (value != _isThinking) { _isThinking = value; OnPropertyChangedWithValue(value, nameof(IsThinking)); Flags(); } } }
        [DataSourceProperty] public bool ShowGold => !_isThinking && !_isEnemy;
        [DataSourceProperty] public bool ShowRed => !_isThinking && _isEnemy;

        private void Flags() { OnPropertyChanged(nameof(ShowGold)); OnPropertyChanged(nameof(ShowRed)); }
    }

    public class BubblesVM : ViewModel
    {
        private MBBindingList<BubbleVM> _bubbles = new MBBindingList<BubbleVM>();
        [DataSourceProperty] public MBBindingList<BubbleVM> Bubbles { get => _bubbles; set { if (value != _bubbles) { _bubbles = value; OnPropertyChangedWithValue(value, nameof(Bubbles)); } } }
    }

    /// <summary>
    /// Text over characters' heads in a scene. Our own layer on top of the mission screen; each line follows its
    /// speaker and disappears on its own. In towns and keeps a line is only shown where you could hear it: within
    /// earshot and not through a wall or a floor. It shrinks with distance and the plate is as wide as the text.
    /// </summary>
    internal static class Bubbles
    {
        private class Item
        {
            public Agent Agent; public BubbleVM VM; public float Left; public bool Logged;
            public bool Thinking; public float Anim;
            // last line-of-sight answer, refreshed a few times a second
            public bool Clear = true; public float SightAge = 99f;
        }

        private static readonly List<Item> Items = new List<Item>();
        private static GauntletLayer _layer;
        private static ScreenBase _screen;
        private static BubblesVM _vm;
        private static bool _broken;      // if the UI fails once, don't retry this session

        // Layout of AILegacyBubbles.xml: the item box is this big and the plate sits bottom-centre inside it
        private const float BoxWidth = 760f, BoxHeight = 360f;
        private const int BaseFont = 22;
        // Longest a "..." plate stays if nobody takes it down
        private const float ThinkingMax = 120f;

        public static bool Available => !_broken && S.Get.EnableBubbles && ScreenManager.TopScreen is MissionScreen;

        public static void Show(Agent agent, string text, float seconds) => Show(agent, text, seconds, false);

        /// <summary>A line still being written. When it outgrows the bubble, the end stays in view instead of the start.</summary>
        public static void ShowLive(Agent agent, string text, float seconds) => Show(agent, text, seconds, true);

        /// <summary>
        /// Puts up (true) or takes down (false) an animated "..." over someone whose reply is being written. A real line
        /// shown for them replaces it; taking it down never removes a real line.
        /// </summary>
        public static void Thinking(Agent agent, bool on)
        {
            try
            {
                if (_broken || agent == null || !S.Get.EnableBubbles) return;
                var old = Items.FirstOrDefault(i => i.Agent == agent);
                if (!on)
                {
                    if (old != null && old.Thinking) { _vm?.Bubbles.Remove(old.VM); Items.Remove(old); }
                    return;
                }
                if (old != null) return;            // already saying something, or already thinking
                if (!agent.IsActive() || !Attach()) return;
                var vm = new BubbleVM { Text = ".", IsThinking = true, IsVisible = false, IsEnemy = Hostile(agent) };
                _vm.Bubbles.Add(vm);
                Items.Add(new Item { Agent = agent, VM = vm, Left = ThinkingMax, Thinking = true });
            }
            catch (Exception ex) { Fail("thinking", ex); }
        }

        /// <summary>The player could hear this agent now: within earshot and not behind a wall or on another floor.</summary>
        public static bool CanHear(Agent agent)
        {
            try
            {
                if (agent == null || !agent.IsActive()) return false;
                var me = Agent.Main;
                if (me == null || agent == me) return true;
                if (agent.Position.Distance(me.Position) > Range()) return false;
                return Unblocked(agent, me);
            }
            catch { return true; }
        }

        private static void Show(Agent agent, string text, float seconds, bool keepEnd)
        {
            try
            {
                if (_broken || agent == null || string.IsNullOrWhiteSpace(text) || !S.Get.EnableBubbles) return;
                if (!Attach()) return;
                text = text.Replace("{", "(").Replace("}", ")").Trim();
                if (text.Length > 220)
                {
                    if (keepEnd)
                    {
                        int from = text.Length - 220;
                        if (char.IsLowSurrogate(text[from])) from++;
                        text = "…" + text.Substring(from);
                    }
                    else text = text.Substring(0, 220) + "…";
                }
                var old = Items.FirstOrDefault(i => i.Agent == agent);
                if (old != null)
                {
                    old.VM.Text = text; old.Left = seconds;
                    if (old.Thinking) { old.Thinking = false; old.VM.IsThinking = false; }
                    return;
                }
                var vm = new BubbleVM { Text = text, IsVisible = false, IsEnemy = Hostile(agent) };
                _vm.Bubbles.Add(vm);
                Items.Add(new Item { Agent = agent, VM = vm, Left = seconds });
            }
            catch (Exception ex) { Fail(L.T("AIL_2ef46d51", "enseñando una frase"), ex); }
        }

        private static bool Hostile(Agent a)
        {
            try { return Agent.Main != null && a != Agent.Main && a.IsActive() && Agent.Main.IsActive() && a.IsEnemyOf(Agent.Main); }
            catch { return false; }
        }

        /// <summary>In a battle shouts carry over the field; elsewhere only what is within earshot.</summary>
        private static bool Battle()
        {
            try { var m = Mission.Current; return m != null && (m.Mode == MissionMode.Battle || m.Mode == MissionMode.Deployment || m.Mode == MissionMode.Duel); }
            catch { return false; }
        }

        private static float Range()
        {
            var cfg = S.Get;
            float max = Math.Max(5f, cfg.BubbleMaxDistance);
            if (Battle()) return max;
            // About the distance at which people hear each other (10 m by default)
            return Math.Min(max, Math.Max(6f, cfg.OverhearRadius));
        }

        /// <summary>No wall or floor between the player's eyes and the speaker's.</summary>
        private static bool Unblocked(Agent a, Agent me)
        {
            if (Battle()) return true;
            return LineOfSight(me.GetEyeGlobalPosition(), a.GetEyeGlobalPosition());
        }

        /// <summary>
        /// Nothing solid (wall, floor, closed door) between two points, such as two people's eyes. Without a scene to ask,
        /// only checks they are not a floor apart.
        /// </summary>
        internal static bool LineOfSight(Vec3 from, Vec3 to)
        {
            float len = from.Distance(to);
            if (len < 1.2f) return true;
            try
            {
                var scene = Mission.Current?.Scene;
                if (scene != null)
                {
                    if (scene.RayCastForClosestEntityOrTerrain(from, to, out float hit, 0.01f, BodyFlags.CommonFocusRayCastExcludeFlags)
                        && !float.IsNaN(hit) && hit < len - 0.5f)
                        return false;
                    return true;
                }
            }
            catch { }
            return Math.Abs(from.z - to.z) < 2.2f;
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
            Log.Error("Overhead text: " + where, ex);
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
                float range = Range();
                bool battle = Battle();
                var me = Agent.Main;
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
                    if (it.Thinking)
                    {
                        // three dots that fill up and start again
                        it.Anim += dt;
                        int n = 1 + (int)(it.Anim / 0.4f) % 3;
                        it.VM.Text = n == 1 ? "." : n == 2 ? ". ." : ". . .";
                    }
                    if (cam == null) { it.VM.IsVisible = false; continue; }
                    // World position of the eyes, a bit higher. GetEyeGlobalHeight put the text off screen.
                    var pos = it.Agent.GetEyeGlobalPosition();
                    pos.z += 0.4f;
                    float x = -5000f, y = -5000f, w = -1f;
                    MBWindowManager.WorldToScreenInsideUsableArea(cam, pos, ref x, ref y, ref w);
                    float dist = me != null && me != it.Agent ? it.Agent.Position.Distance(me.Position) : 0f;
                    bool onScreen = w > 0f && x > -200f && y > -200f && x < Screen.RealScreenResolutionWidth + 200f && y < Screen.RealScreenResolutionHeight + 200f;
                    bool heard = dist <= range;
                    if (heard && me != null && me != it.Agent)
                    {
                        it.SightAge += dt;
                        if (it.SightAge >= 0.3f) { it.SightAge = 0f; it.Clear = Unblocked(it.Agent, me); }
                        heard = it.Clear;
                    }
                    if (!it.Logged)
                    {
                        it.Logged = true;
                        Log.Info($"[text] '{(it.VM.Text.Length > 40 ? it.VM.Text.Substring(0, 40) + "…" : it.VM.Text)}' -> screen x={x:0} y={y:0} w={w:0.00} at {dist:0.0} m{(heard ? "" : " (out of earshot or behind a wall: hidden)")} (resolution {Screen.RealScreenResolutionWidth:0}x{Screen.RealScreenResolutionHeight:0}, scale {scale:0.00})");
                    }
                    bool visible = onScreen && heard;
                    it.VM.IsVisible = visible;
                    if (!visible) continue;
                    // Smaller the further away: full size up close, about 60 % at the edge of hearing
                    float near = battle ? 5f : 3f;
                    float t = range <= near ? 0f : Math.Max(0f, Math.Min(1f, (dist - near) / (range - near)));
                    it.VM.FontSize = Math.Max(12, (int)Math.Round(BaseFont * (1f - (battle ? 0.5f : 0.4f) * t)));
                    it.VM.X = x - BoxWidth * 0.5f * scale;       // the box is centred over the character, the plate grows up from its bottom
                    it.VM.Y = y - BoxHeight * scale;
                }
            }
            catch (Exception ex) { Fail("actualizando posiciones", ex); }
        }
    }
}
