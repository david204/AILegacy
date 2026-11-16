using System;
using System.Collections.Generic;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;
using TaleWorlds.GauntletUI;
using TaleWorlds.GauntletUI.BaseTypes;
using TaleWorlds.Library;

namespace AILegacy.UI
{
    /// <summary>One line of the current talk in the dialogue box panel: who speaks and what, or a grey note.</summary>
    public class AilTalkLineVM : ViewModel
    {
        private string _speaker = "", _text = "";
        private bool _isPlayer, _isNpc, _isNote, _hasSeparator;

        [DataSourceProperty] public string Speaker { get => _speaker; set { if (value != _speaker) { _speaker = value; OnPropertyChangedWithValue(value, nameof(Speaker)); } } }
        [DataSourceProperty] public string Text { get => _text; set { if (value != _text) { _text = value; OnPropertyChangedWithValue(value, nameof(Text)); } } }
        [DataSourceProperty] public bool IsPlayer { get => _isPlayer; set { if (value != _isPlayer) { _isPlayer = value; OnPropertyChangedWithValue(value, nameof(IsPlayer)); } } }
        [DataSourceProperty] public bool IsNpc { get => _isNpc; set { if (value != _isNpc) { _isNpc = value; OnPropertyChangedWithValue(value, nameof(IsNpc)); } } }
        [DataSourceProperty] public bool IsNote { get => _isNote; set { if (value != _isNote) { _isNote = value; OnPropertyChangedWithValue(value, nameof(IsNote)); OnPropertyChanged(nameof(IsSpoken)); } } }
        [DataSourceProperty] public bool IsSpoken => !_isNote;
        /// <summary>A thin line above each spoken line except the first, so the turns read apart.</summary>
        [DataSourceProperty] public bool HasSeparator { get => _hasSeparator; set { if (value != _hasSeparator) { _hasSeparator = value; OnPropertyChangedWithValue(value, nameof(HasSeparator)); } } }
    }

    /// <summary>
    /// The scrollable panel that holds the whole current talk inside the game's dialogue box. Its height follows the
    /// answer list (at least the box's own minimum, at most MaxViewHeight), so the box never grows past the options.
    /// Each time ScrollVersion changes it scrolls to the newest spoken line: to the very bottom when that line and
    /// what follows it fit, or to the top of that line when it is taller than the view, so it reads from its start.
    /// </summary>
    public class AilTalkScrollPanel : ScrollablePanel
    {
        private int _version, _pendingFrames;

        public AilTalkScrollPanel(UIContext context) : base(context) { }

        /// <summary>Unscaled height of the text area when the options are short (the native box minimum, 192, less margins).</summary>
        public float MinViewHeight { get; set; } = 142f;
        public float MaxViewHeight { get; set; } = 430f;
        /// <summary>The game's answer list; the panel grows to its height.</summary>
        public Widget AnswersContainer { get; set; }
        /// <summary>Index in InnerPanel of the line to keep in view; -1 scrolls to the bottom.</summary>
        public int AnchorIndex { get; set; } = -1;

        public int ScrollVersion
        {
            get => _version;
            set
            {
                if (_version == value) return;
                _version = value;
                _pendingFrames = 6;      // the list keeps laying out for a few frames after it changes
            }
        }

        protected override void OnLateUpdate(float dt)
        {
            base.OnLateUpdate(dt);
            try
            {
                FitHeight();
                if (_pendingFrames > 0 && IsVisible)
                {
                    _pendingFrames--;
                    ScrollToNewest();
                }
            }
            catch { }
        }

        private void FitHeight()
        {
            float inv = _inverseScaleToUse;
            if (inv <= 0f) return;
            float want = MinViewHeight;
            var answers = AnswersContainer;
            if (answers != null && answers.IsVisible)
            {
                // the box is as tall as the answers; take what is left inside its margins
                float room = answers.Size.Y * inv - (ScaledMarginTop + ScaledMarginBottom) * inv;
                if (room > want) want = room;
            }
            if (want > MaxViewHeight) want = MaxViewHeight;
            if (Math.Abs(SuggestedHeight - want) > 0.5f) { SuggestedHeight = want; _pendingFrames = Math.Max(_pendingFrames, 2); }
        }

        private void ScrollToNewest()
        {
            var inner = InnerPanel;
            var clip = ClipRect;
            var bar = VerticalScrollbar;
            if (inner == null || clip == null || bar == null) return;
            float range = inner.Size.Y - clip.Size.Y;
            if (range <= 0.5f) { bar.ValueFloat = bar.MinValue; return; }
            float target = range;
            int i = AnchorIndex;
            if (i >= 0 && i < inner.ChildCount)
            {
                var line = inner.GetChild(i);
                float top = line.GlobalPosition.Y - inner.GlobalPosition.Y;
                // the newest line and the notes under it don't fit: start at its first words
                if (inner.Size.Y - top > clip.Size.Y) target = Math.Max(0f, Math.Min(range, top));
            }
            bar.ValueFloat = bar.MinValue + (bar.MaxValue - bar.MinValue) * (target / range);
        }
    }

    /// <summary>
    /// Inside the game's dialogue box, next to its own text: our scrollable panel with the whole current talk. It shows
    /// only during an AI Legacy talk (@AilTalkActive); the rest of the time the box is exactly the game's.
    /// Same margins as the game's text, so it sits where that text was.
    /// </summary>
    [PrefabExtension("SPConversation", "descendant::RichTextWidget[@Text='@DialogText']")]
    internal sealed class ConversationTalkPrefab : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;

        [PrefabExtensionText]
        public string Xml =>
            "<AilTalkScrollPanel Id=\"AilTalkPanel\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"142\" VerticalAlignment=\"Center\" MarginLeft=\"80\" MarginRight=\"25\" MarginTop=\"25\" MarginBottom=\"25\" IsVisible=\"@AilTalkActive\" AutoHideScrollBars=\"true\" OnlyAcceptScrollEventIfCanScroll=\"true\" ClipRect=\"AilTalkClip\" InnerPanel=\"AilTalkClip\\AilTalkLines\" VerticalScrollbar=\"AilTalkScrollbar\" AnswersContainer=\"..\\..\\..\\AnswerListContainer\" ScrollVersion=\"@AilTalkScrollVersion\" AnchorIndex=\"@AilTalkAnchor\">" +
            "<Children>" +
            "<Widget Id=\"AilTalkClip\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" ClipContents=\"true\">" +
            "<Children>" +
            "<ListPanel Id=\"AilTalkLines\" DataSource=\"{AilTalk}\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" VerticalAlignment=\"Top\" StackLayout.LayoutMethod=\"VerticalTopToBottom\">" +
            "<ItemTemplate>" +
            "<ListPanel WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" StackLayout.LayoutMethod=\"VerticalTopToBottom\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"1\" MarginTop=\"10\" MarginBottom=\"10\" Sprite=\"BlankWhiteSquare_9\" Color=\"#E2AF5440\" DoNotAcceptEvents=\"true\" IsVisible=\"@HasSeparator\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" Brush=\"Conversation.ConversedPartyLine.Text\" Brush.FontSize=\"22\" Brush.FontColor=\"#E2AF54FF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@Speaker\" IsVisible=\"@IsNpc\" DoNotAcceptEvents=\"true\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" Brush=\"Conversation.ConversedPartyLine.Text\" Brush.FontSize=\"22\" Brush.FontColor=\"#8FB8D9FF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@Speaker\" IsVisible=\"@IsPlayer\" DoNotAcceptEvents=\"true\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" Brush=\"Conversation.ConversedPartyLine.Text\" Brush.FontSize=\"24\" Brush.TextHorizontalAlignment=\"Left\" Brush.TextVerticalAlignment=\"Top\" Text=\"@Text\" IsVisible=\"@IsSpoken\" DoNotAcceptEvents=\"true\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" MarginTop=\"4\" Brush=\"Conversation.ConversedPartyLine.Text\" Brush.FontSize=\"20\" Brush.FontColor=\"#A8A8A8FF\" Brush.TextHorizontalAlignment=\"Left\" Brush.TextVerticalAlignment=\"Top\" Text=\"@Text\" IsVisible=\"@IsNote\" DoNotAcceptEvents=\"true\" />" +
            "</Children>" +
            "</ListPanel>" +
            "</ItemTemplate>" +
            "</ListPanel>" +
            "</Children>" +
            "</Widget>" +
            "<ScrollbarWidget Id=\"AilTalkScrollbar\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"12\" HorizontalAlignment=\"Right\" PositionXOffset=\"18\" AlignmentAxis=\"Vertical\" Handle=\"AilTalkHandle\" MinValue=\"0\" MaxValue=\"100\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"2\" HorizontalAlignment=\"Center\" Sprite=\"BlankWhiteSquare_9\" Color=\"#E2AF5438\" DoNotAcceptEvents=\"true\" />" +
            "<Widget Id=\"AilTalkHandle\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"6\" SuggestedHeight=\"36\" MinHeight=\"20\" HorizontalAlignment=\"Center\" Sprite=\"BlankWhiteSquare_9\" Color=\"#E2AF54DD\" />" +
            "</Children>" +
            "</ScrollbarWidget>" +
            "</Children>" +
            "</AilTalkScrollPanel>";
    }

    /// <summary>The game's own dialogue text hides while our panel is up (@AilNativeTextVisible is true otherwise).</summary>
    [PrefabExtension("SPConversation", "descendant::RichTextWidget[@Text='@DialogText']")]
    internal sealed class ConversationNativeTextPatch : PrefabExtensionSetAttributePatch
    {
        public override List<Attribute> Attributes => new List<Attribute> { new Attribute("IsVisible", "@AilNativeTextVisible") };
    }
}
