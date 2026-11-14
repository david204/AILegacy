using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;

namespace AILegacy.UI
{
    /// <summary>
    /// Scrollbar that jumps to the bottom whenever ScrollVersion changes (new chat message).
    /// Used from GUI/Prefabs/AILegacyChat.xml as &lt;AILegacyScrollbarWidget ScrollVersion="@ScrollVersion" .../&gt;
    /// </summary>
    public class AILegacyScrollbarWidget : ScrollbarWidget
    {
        private int _version;
        private int _pendingFrames;

        public AILegacyScrollbarWidget(UIContext context) : base(context) { }

        public int ScrollVersion
        {
            get => _version;
            set
            {
                if (_version == value) return;
                _version = value;
                _pendingFrames = 4; // the panel keeps resizing for a few frames
            }
        }

        protected override void OnLateUpdate(float dt)
        {
            base.OnLateUpdate(dt);
            if (_pendingFrames > 0)
            {
                _pendingFrames--;
                ValueFloat = MaxValue;
            }
        }
    }
}
