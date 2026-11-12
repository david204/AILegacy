using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;

namespace AILegacy.UI
{
    /// <summary>
    /// Barra de scroll que baja sola al final cada vez que cambia ScrollVersion (nuevo mensaje en el chat).
    /// Se usa desde GUI/Prefabs/AILegacyChat.xml como &lt;AILegacyScrollbarWidget ScrollVersion="@ScrollVersion" .../&gt;
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
                _pendingFrames = 4; // el panel recalcula el tamaño durante unos frames
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
