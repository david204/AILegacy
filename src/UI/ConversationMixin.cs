using System;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Settings;
using Bannerlord.UIExtenderEx.Attributes;
using Bannerlord.UIExtenderEx.Prefabs2;
using Bannerlord.UIExtenderEx.ViewModels;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Conversation;
using TaleWorlds.Library;

namespace AILegacy.UI
{
    public class AilHistoryLineVM : ViewModel
    {
        private string _speaker = "", _text = "";
        private bool _isPlayer;
        [DataSourceProperty] public string Speaker { get => _speaker; set { if (value != _speaker) { _speaker = value; OnPropertyChangedWithValue(value, nameof(Speaker)); } } }
        [DataSourceProperty] public string Text { get => _text; set { if (value != _text) { _text = value; OnPropertyChangedWithValue(value, nameof(Text)); } } }
        [DataSourceProperty] public bool IsPlayer { get => _isPlayer; set { if (value != _isPlayer) { _isPlayer = value; OnPropertyChangedWithValue(value, nameof(IsPlayer)); OnPropertyChanged(nameof(IsNpc)); } } }
        [DataSourceProperty] public bool IsNpc => !_isPlayer;
    }

    /// <summary>
    /// Adds to the game's conversation screen: relation, trust and romance bars next to the name (plus a warning when they
    /// suspect lies or there's tension), and a button that opens a scrollable history of past talks above the dialogue box.
    /// </summary>
    [ViewModelMixin("Refresh")]
    internal sealed class ConversationMixin : BaseViewModelMixin<MissionConversationVM>
    {
        private static WeakReference<ConversationMixin> _current;
        private static float _since;

        private bool _cardVisible;
        private bool _statsVisible, _trustVisible, _romanceVisible, _tensionVisible, _warnVisible, _historyAvailable, _historyVisible;
        private string _relationText = "", _trustText = "", _romanceText = "", _tensionText = "", _warnText = "", _historyLabel = "";
        private int _relationFill, _trustFill, _romanceFill, _tensionFill, _scrollVersion;
        private bool _moodVisible, _romanceInfoVisible, _familyVisible;
        private string _moodText = "", _knownText = "", _romanceInfoText = "", _familyText = "";
        private MBBindingList<AilHistoryLineVM> _history = new MBBindingList<AilHistoryLineVM>();
        private string _historyOf;
        private int _historyCount = -1;

        public ConversationMixin(MissionConversationVM vm) : base(vm)
        {
            _current = new WeakReference<ConversationMixin>(this);
            try { Recompute(); } catch (Exception ex) { Log.Error("Conversation: bars", ex); }
        }

        /// <summary>True while the bars are on screen, so the stats line under the reply can be skipped.</summary>
        public static bool ShowingStats
        {
            get { try { return _current != null && _current.TryGetTarget(out var m) && m._statsVisible; } catch { return false; } }
        }

        /// <summary>
        /// Replaces the text in the game's dialogue box without moving the conversation. Used while a reply is still being
        /// written. The game puts its own text back the next time the conversation advances. False if there is no box.
        /// </summary>
        public static bool SetDialogText(string text)
        {
            try
            {
                if (_current == null || !_current.TryGetTarget(out var m)) return false;
                var vm = m.ViewModel;
                if (vm == null) return false;
                vm.DialogText = text ?? "";
                return true;
            }
            catch { return false; }
        }

        /// <summary>Runs twice a second. The numbers change after each reply and the game doesn't refresh its screen on its own.</summary>
        public static void Tick(float dt)
        {
            _since += dt;
            if (_since < 0.5f) return;
            _since = 0f;
            try { if (_current != null && _current.TryGetTarget(out var m)) m.Recompute(); }
            catch (Exception ex) { Log.Error("Conversation: bars", ex); }
        }

        public override void OnRefresh()
        {
            try { Recompute(); } catch (Exception ex) { Log.Error("Conversation: bars", ex); }
        }

        public override void OnFinalize()
        {
            try { if (_current != null && _current.TryGetTarget(out var m) && m == this) _current = null; } catch { }
            base.OnFinalize();
        }

        private static Hero Conversed()
        {
            try
            {
                if (Campaign.Current == null) return null;
                var cm = Campaign.Current.ConversationManager;
                if (cm == null || !cm.IsConversationInProgress) return null;
                var h = Hero.OneToOneConversationHero;
                if (h == null && ChatSession.Active?.Who != null && ChatSession.Active.Who.IsHero) h = ChatSession.Active.Who.Hero;
                return h == Hero.MainHero ? null : h;
            }
            catch { return null; }
        }

        private void Recompute()
        {
            var cfg = S.Get;
            var h = Conversed();
            var mem = h == null ? null : MemoryStore.Find(h.StringId);

            // ---- bars
            bool stats = h != null && cfg.ShowNameBars;
            AilStatsVisible = stats;
            if (stats)
            {
                int rel = (int)h.GetRelationWithPlayer();
                AilRelationText = L.F("AIL_f3138943", $"Relación: {rel:+0;-0;0}");
                AilRelationFill = Math.Max(0, Math.Min(100, (rel + 100) / 2));
                // All bars show from the first word, at their starting value, so the player sees where things stand
                int trustNow = mem?.Trust ?? 0, romanceNow = mem?.Romance ?? 0, tensionNow = mem == null ? 0 : Bonds.Tension(mem);
                AilTrustVisible = cfg.EnableTrust;
                AilTrustText = L.F("AIL_2cb6f858", $"Confianza: {trustNow}"); AilTrustFill = Math.Max(0, Math.Min(100, (trustNow + 100) / 2));
                AilRomanceVisible = true;
                AilRomanceText = L.F("AIL_d5a0e001", $"Romance: {romanceNow}"); AilRomanceFill = Math.Max(0, Math.Min(100, romanceNow));
                AilTensionVisible = cfg.EnableTension;
                AilTensionText = L.F("AIL_55654690", $"Tensión: {tensionNow}"); AilTensionFill = Math.Max(0, Math.Min(100, tensionNow));
                // Mood, in the player's language when the character has given it that way
                string moodLine = mem == null ? "" : !string.IsNullOrWhiteSpace(mem.MoodShown) ? mem.MoodShown : (mem.Mood ?? "");
                AilMoodText = moodLine; AilMoodVisible = moodLine.Trim().Length > 0;
                // Whether they know who the player is
                string knownAs = null;
                try { knownAs = Prompt.ContextBuilder.PlayerNameFor(Speaker.FromHero(h)); } catch { }
                AilKnownText = string.IsNullOrWhiteSpace(knownAs) ? L.T("AIL_e7b1c4e2", "No sabe quién eres") : L.F("AIL_e7b1c4e1", $"Te conoce como {knownAs}");
                // Romance only where it can exist, with the two values that matter
                bool canLove = false;
                try { canLove = Bonds.RomancePossible(Speaker.FromHero(h)); } catch { }
                AilRomanceVisible = canLove || romanceNow > 0;
                AilRomanceInfoVisible = canLove && h.Spouse != Hero.MainHero;
                AilRomanceInfoText = L.F("AIL_e7b1c4e3", $"Intimidad {cfg.RomanceForIntimacy} · Boda {cfg.RomanceForMarriage}");
                // Family ties with the player
                string family = "";
                try
                {
                    var pc = Hero.MainHero;
                    if (h.Spouse == pc) family = L.T("AIL_e7b1c4e4", "Tu cónyuge");
                    else if (AILegacy.World.Concubines.Is(h)) family = h.IsFemale ? L.T("AIL_e7b1c83e", "Tu concubina") : L.T("AIL_e7b1c83f", "Tu concubino");
                    int kids = h.Children.Count(c => c != null && (c.Father == pc || c.Mother == pc));
                    if (kids > 0) family += (family.Length > 0 ? " · " : "") + L.F("AIL_e7b1c4e5", $"{kids} hijo(s) en común");
                    // expecting your child, or children of yours out of wedlock that pass for someone else's
                    string outside = AILegacy.World.Bastards.PanelLine(h);
                    if (outside.Length > 0) family += (family.Length > 0 ? " · " : "") + outside;
                }
                catch { }
                AilFamilyText = family; AilFamilyVisible = family.Length > 0;
                string warn = mem != null && mem.LiesSuspected > 0 ? L.T("AIL_d5a0e002", "Sospecha que mientes") : "";
                string ill = AILegacy.World.Diseases.MarkerFor(h);
                if (ill.Length > 0) warn = warn.Length > 0 ? warn + " · " + ill : ill;
                AilWarnText = warn;
                AilWarnVisible = warn.Length > 0;
            }
            else { AilTrustVisible = false; AilRomanceVisible = false; AilTensionVisible = false; AilWarnVisible = false; AilMoodVisible = false; AilRomanceInfoVisible = false; AilFamilyVisible = false; }

            // ---- history
            bool can = h != null && cfg.ShowDialogHistory && mem != null && mem.Turns.Any(t => !t.IsNote);
            AilHistoryAvailable = can;
            AilCardVisible = stats || can;
            if (!can) { AilHistoryVisible = false; return; }
            AilHistoryLabel = AilHistoryVisible ? L.T("AIL_d5a0e004", "Cerrar historial") : L.T("AIL_d5a0e003", "Historial");
            if (!AilHistoryVisible) return;
            if (_historyOf == h.StringId && _historyCount == mem.Turns.Count) return;
            _historyOf = h.StringId; _historyCount = mem.Turns.Count;
            _history.Clear();
            string me = Hero.MainHero?.Name?.ToString() ?? "", them = h.Name?.ToString() ?? "";
            foreach (var t in mem.Turns.Where(t => !t.IsNote && !string.IsNullOrWhiteSpace(t.Text)).Reverse().Take(Math.Max(5, cfg.DialogHistoryLines)).Reverse())
            {
                bool player = t.Role == "player";
                string text = t.Text.Replace("\r", "").Trim();
                if (text.Length > 700) text = text.Substring(0, 700) + "…";
                _history.Add(new AilHistoryLineVM { Speaker = player ? me : them, Text = text, IsPlayer = player });
            }
            AilScrollVersion = _scrollVersion + 1;       // scroll to the bottom
        }

        [DataSourceMethod]
        public void ExecuteAilToggleHistory()
        {
            try
            {
                AilHistoryVisible = !AilHistoryVisible;
                _historyCount = -1;
                Recompute();
            }
            catch (Exception ex) { Log.Error("Conversation: history", ex); }
        }

        [DataSourceProperty] public bool AilCardVisible { get => _cardVisible; set { if (value != _cardVisible) { _cardVisible = value; OnPropertyChangedWithValue(value, nameof(AilCardVisible)); } } }
        [DataSourceProperty] public bool AilStatsVisible { get => _statsVisible; set { if (value != _statsVisible) { _statsVisible = value; OnPropertyChangedWithValue(value, nameof(AilStatsVisible)); } } }
        [DataSourceProperty] public bool AilTrustVisible { get => _trustVisible; set { if (value != _trustVisible) { _trustVisible = value; OnPropertyChangedWithValue(value, nameof(AilTrustVisible)); } } }
        [DataSourceProperty] public bool AilRomanceVisible { get => _romanceVisible; set { if (value != _romanceVisible) { _romanceVisible = value; OnPropertyChangedWithValue(value, nameof(AilRomanceVisible)); } } }
        [DataSourceProperty] public bool AilTensionVisible { get => _tensionVisible; set { if (value != _tensionVisible) { _tensionVisible = value; OnPropertyChangedWithValue(value, nameof(AilTensionVisible)); } } }
        [DataSourceProperty] public string AilTensionText { get => _tensionText; set { if (value != _tensionText) { _tensionText = value; OnPropertyChangedWithValue(value, nameof(AilTensionText)); } } }
        [DataSourceProperty] public int AilTensionFill { get => _tensionFill; set { if (value != _tensionFill) { _tensionFill = value; OnPropertyChangedWithValue(value, nameof(AilTensionFill)); } } }
        [DataSourceProperty] public bool AilMoodVisible { get => _moodVisible; set { if (value != _moodVisible) { _moodVisible = value; OnPropertyChangedWithValue(value, nameof(AilMoodVisible)); } } }
        [DataSourceProperty] public bool AilRomanceInfoVisible { get => _romanceInfoVisible; set { if (value != _romanceInfoVisible) { _romanceInfoVisible = value; OnPropertyChangedWithValue(value, nameof(AilRomanceInfoVisible)); } } }
        [DataSourceProperty] public bool AilFamilyVisible { get => _familyVisible; set { if (value != _familyVisible) { _familyVisible = value; OnPropertyChangedWithValue(value, nameof(AilFamilyVisible)); } } }
        [DataSourceProperty] public string AilMoodText { get => _moodText; set { if (value != _moodText) { _moodText = value; OnPropertyChangedWithValue(value, nameof(AilMoodText)); } } }
        [DataSourceProperty] public string AilKnownText { get => _knownText; set { if (value != _knownText) { _knownText = value; OnPropertyChangedWithValue(value, nameof(AilKnownText)); } } }
        [DataSourceProperty] public string AilRomanceInfoText { get => _romanceInfoText; set { if (value != _romanceInfoText) { _romanceInfoText = value; OnPropertyChangedWithValue(value, nameof(AilRomanceInfoText)); } } }
        [DataSourceProperty] public string AilFamilyText { get => _familyText; set { if (value != _familyText) { _familyText = value; OnPropertyChangedWithValue(value, nameof(AilFamilyText)); } } }
        [DataSourceProperty] public bool AilWarnVisible { get => _warnVisible; set { if (value != _warnVisible) { _warnVisible = value; OnPropertyChangedWithValue(value, nameof(AilWarnVisible)); } } }
        [DataSourceProperty] public bool AilHistoryAvailable { get => _historyAvailable; set { if (value != _historyAvailable) { _historyAvailable = value; OnPropertyChangedWithValue(value, nameof(AilHistoryAvailable)); } } }
        [DataSourceProperty] public bool AilHistoryVisible { get => _historyVisible; set { if (value != _historyVisible) { _historyVisible = value; OnPropertyChangedWithValue(value, nameof(AilHistoryVisible)); } } }
        [DataSourceProperty] public string AilRelationText { get => _relationText; set { if (value != _relationText) { _relationText = value; OnPropertyChangedWithValue(value, nameof(AilRelationText)); } } }
        [DataSourceProperty] public string AilTrustText { get => _trustText; set { if (value != _trustText) { _trustText = value; OnPropertyChangedWithValue(value, nameof(AilTrustText)); } } }
        [DataSourceProperty] public string AilRomanceText { get => _romanceText; set { if (value != _romanceText) { _romanceText = value; OnPropertyChangedWithValue(value, nameof(AilRomanceText)); } } }
        [DataSourceProperty] public string AilWarnText { get => _warnText; set { if (value != _warnText) { _warnText = value; OnPropertyChangedWithValue(value, nameof(AilWarnText)); } } }
        [DataSourceProperty] public string AilHistoryLabel { get => _historyLabel; set { if (value != _historyLabel) { _historyLabel = value; OnPropertyChangedWithValue(value, nameof(AilHistoryLabel)); } } }
        [DataSourceProperty] public int AilRelationFill { get => _relationFill; set { if (value != _relationFill) { _relationFill = value; OnPropertyChangedWithValue(value, nameof(AilRelationFill)); } } }
        [DataSourceProperty] public int AilTrustFill { get => _trustFill; set { if (value != _trustFill) { _trustFill = value; OnPropertyChangedWithValue(value, nameof(AilTrustFill)); } } }
        [DataSourceProperty] public int AilRomanceFill { get => _romanceFill; set { if (value != _romanceFill) { _romanceFill = value; OnPropertyChangedWithValue(value, nameof(AilRomanceFill)); } } }
        [DataSourceProperty] public int AilScrollVersion { get => _scrollVersion; set { if (value != _scrollVersion) { _scrollVersion = value; OnPropertyChangedWithValue(value, nameof(AilScrollVersion)); } } }
        [DataSourceProperty] public MBBindingList<AilHistoryLineVM> AilHistory { get => _history; set { if (value != _history) { _history = value; OnPropertyChangedWithValue(value, nameof(AilHistory)); } } }
    }

    /// <summary>
    /// Our own panel to the right of the scene, just above the dialogue options: mood, whether they know who you are,
    /// the bars stacked one under another, family ties, the lie warning and the history button. It copies the dialogue
    /// box height, like the game's name strip, so it moves with the box.
    /// </summary>
    [PrefabExtension("SPConversation", "descendant::ListPanel[@Id='VerticalContainer']")]
    internal sealed class ConversationStatsPrefab : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;

        // Widths are all Fixed. A StretchToParent child is measured with the whole available size, so a CoverChildren
        // box that holds one grows to that size (that made the first card and its button 680 px wide).
        // The panel's height follows its content. To keep its BOTTOM edge just above the dialogue options it sits at
        // the bottom of a 460 px slot, and the slot hangs from the top of the synced box: PaddingAmount 468 = 8 + 460.
        // MarginLeft 1480 on a centred 320 px box puts it 740 px right of the screen centre.
        [PrefabExtensionText]
        public string Xml =>
            "<DimensionSyncWidget Id=\"AilStatsSync\" DoNotAcceptEvents=\"true\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"320\" VerticalAlignment=\"Bottom\" HorizontalAlignment=\"Center\" MarginLeft=\"1480\" WidgetToCopyHeightFrom=\"..\\VerticalContainer\\BottomPanelsContainer\\DialogueContainer\" DimensionToSync=\"Vertical\" PaddingAmount=\"468\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"320\" SuggestedHeight=\"460\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" DoNotAcceptEvents=\"true\">" +
            "<Children>" +
            "<Widget Id=\"AilStatsCard\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"320\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Bottom\" IsVisible=\"@AilCardVisible\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"316\" HorizontalAlignment=\"Center\" MarginTop=\"2\" MarginBottom=\"2\" Sprite=\"BlankWhiteSquare_9\" Color=\"#0D0A07FF\">" +
            "<Children>" +
            "<ListPanel WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" HorizontalAlignment=\"Center\" MarginTop=\"12\" MarginBottom=\"12\" StackLayout.LayoutMethod=\"VerticalTopToBottom\">" +
            "<Children>" +
            "<ListPanel WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" StackLayout.LayoutMethod=\"VerticalTopToBottom\" IsVisible=\"@AilStatsVisible\" DoNotAcceptEvents=\"true\">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"17\" Brush.FontColor=\"#C9A6E8FF\" Brush.TextHorizontalAlignment=\"Center\" Text=\"@AilMoodText\" IsVisible=\"@AilMoodVisible\" />" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" MarginTop=\"2\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"16\" Brush.FontColor=\"#9FB4D9FF\" Brush.TextHorizontalAlignment=\"Center\" Text=\"@AilKnownText\" />" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"1\" MarginTop=\"6\" MarginBottom=\"6\" Sprite=\"BlankWhiteSquare_9\" Color=\"#5A4A30FF\" />" +
            "<ListPanel WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"44\" StackLayout.LayoutMethod=\"VerticalTopToBottom\" DoNotAcceptEvents=\"true\">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"24\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@AilRelationText\" />" +
            "<FillBarWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" MarginTop=\"3\" Sprite=\"BlankWhiteSquare_9\" Color=\"#2A241AFF\" FillWidget=\"FillWidget\" InitialAmount=\"@AilRelationFill\" MaxAmount=\"100\" IsEnabled=\"false\">" +
            "<Children>" +
            "<Widget Id=\"FillWidget\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\" ClipContents=\"true\">" +
            "<Children><Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"#D9BF86FF\" /></Children>" +
            "</Widget>" +
            "</Children>" +
            "</FillBarWidget>" +
            "</Children>" +
            "</ListPanel>" +
            "<ListPanel WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"44\" StackLayout.LayoutMethod=\"VerticalTopToBottom\" DoNotAcceptEvents=\"true\" IsVisible=\"@AilTrustVisible\">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"24\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@AilTrustText\" />" +
            "<FillBarWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" MarginTop=\"3\" Sprite=\"BlankWhiteSquare_9\" Color=\"#2A241AFF\" FillWidget=\"FillWidget\" InitialAmount=\"@AilTrustFill\" MaxAmount=\"100\" IsEnabled=\"false\">" +
            "<Children>" +
            "<Widget Id=\"FillWidget\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\" ClipContents=\"true\">" +
            "<Children><Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"#7FB0D9FF\" /></Children>" +
            "</Widget>" +
            "</Children>" +
            "</FillBarWidget>" +
            "</Children>" +
            "</ListPanel>" +
            "<ListPanel WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"44\" StackLayout.LayoutMethod=\"VerticalTopToBottom\" DoNotAcceptEvents=\"true\" IsVisible=\"@AilRomanceVisible\">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"24\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@AilRomanceText\" />" +
            "<FillBarWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" MarginTop=\"3\" Sprite=\"BlankWhiteSquare_9\" Color=\"#2A241AFF\" FillWidget=\"FillWidget\" InitialAmount=\"@AilRomanceFill\" MaxAmount=\"100\" IsEnabled=\"false\">" +
            "<Children>" +
            "<Widget Id=\"FillWidget\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\" ClipContents=\"true\">" +
            "<Children><Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"#D97F9FFF\" /></Children>" +
            "</Widget>" +
            "</Children>" +
            "</FillBarWidget>" +
            "</Children>" +
            "</ListPanel>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"14\" Brush.FontColor=\"#A8909AFF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@AilRomanceInfoText\" IsVisible=\"@AilRomanceInfoVisible\" />" +
            "<ListPanel WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"44\" StackLayout.LayoutMethod=\"VerticalTopToBottom\" DoNotAcceptEvents=\"true\" IsVisible=\"@AilTensionVisible\">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"24\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@AilTensionText\" />" +
            "<FillBarWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" MarginTop=\"3\" Sprite=\"BlankWhiteSquare_9\" Color=\"#2A241AFF\" FillWidget=\"FillWidget\" InitialAmount=\"@AilTensionFill\" MaxAmount=\"100\" IsEnabled=\"false\">" +
            "<Children>" +
            "<Widget Id=\"FillWidget\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\" ClipContents=\"true\">" +
            "<Children><Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"#D9703FFF\" /></Children>" +
            "</Widget>" +
            "</Children>" +
            "</FillBarWidget>" +
            "</Children>" +
            "</ListPanel>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" MarginTop=\"4\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"17\" Brush.FontColor=\"#E8D9A6FF\" Brush.TextHorizontalAlignment=\"Center\" Text=\"@AilFamilyText\" IsVisible=\"@AilFamilyVisible\" />" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"CoverChildren\" SuggestedWidth=\"280\" MarginTop=\"2\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"17\" Brush.FontColor=\"#F0A040FF\" Brush.TextHorizontalAlignment=\"Center\" Text=\"@AilWarnText\" IsVisible=\"@AilWarnVisible\" />" +
            "</Children>" +
            "</ListPanel>" +
            "<ButtonWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"280\" SuggestedHeight=\"36\" MarginTop=\"8\" Command.Click=\"ExecuteAilToggleHistory\" IsVisible=\"@AilHistoryAvailable\" UpdateChildrenStates=\"true\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" Sprite=\"BlankWhiteSquare_9\" Color=\"#4A3920FF\" DoNotAcceptEvents=\"true\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Text=\"@AilHistoryLabel\" DoNotAcceptEvents=\"true\" />" +
            "</Children>" +
            "</ButtonWidget>" +
            "</Children>" +
            "</ListPanel>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</DimensionSyncWidget>";
    }

    /// <summary>Scrollable chat history panel above the name strip. Moves with the dialogue box.</summary>
    [PrefabExtension("SPConversation", "descendant::ListPanel[@Id='VerticalContainer']")]
    internal sealed class ConversationHistoryPrefab : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;

        // PaddingAmount 493 = 55 (the game's name strip margin) + 8 gap + 430 panel height
        [PrefabExtensionText]
        public string Xml =>
            "<DimensionSyncWidget Id=\"AilHistorySync\" DoNotAcceptEvents=\"true\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"680\" VerticalAlignment=\"Bottom\" HorizontalAlignment=\"Center\" MarginRight=\"700\" WidgetToCopyHeightFrom=\"..\\VerticalContainer\\BottomPanelsContainer\\DialogueContainer\" DimensionToSync=\"Vertical\" PaddingAmount=\"493\">" +
            "<Children>" +
            "<Widget Id=\"AilHistoryPanel\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"640\" SuggestedHeight=\"430\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" MarginLeft=\"40\" IsVisible=\"@AilHistoryVisible\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" Sprite=\"BlankWhiteSquare_9\" Color=\"#0D0A07FF\" AlphaFactor=\"0.92\" />" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"2\" VerticalAlignment=\"Top\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"2\" VerticalAlignment=\"Bottom\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"2\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"2\" HorizontalAlignment=\"Right\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<ScrollablePanel Id=\"AilHistoryScroll\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" MarginLeft=\"4\" MarginRight=\"24\" MarginTop=\"8\" MarginBottom=\"8\" AutoHideScrollBars=\"true\" ClipRect=\"AilHistoryClip\" InnerPanel=\"AilHistoryClip\\AilHistoryLines\" VerticalScrollbar=\"..\\AilHistoryScrollbarHolder\\AilHistoryScrollbar\">" +
            "<Children>" +
            "<Widget Id=\"AilHistoryClip\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" ClipContents=\"true\">" +
            "<Children>" +
            "<ListPanel Id=\"AilHistoryLines\" DataSource=\"{AilHistory}\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" StackLayout.LayoutMethod=\"VerticalTopToBottom\">" +
            "<ItemTemplate>" +
            "<ListPanel WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" MarginLeft=\"16\" MarginRight=\"12\" MarginTop=\"8\" StackLayout.LayoutMethod=\"VerticalTopToBottom\">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#D9BF86FF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@Speaker\" IsVisible=\"@IsNpc\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#8FB8D9FF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@Speaker\" IsVisible=\"@IsPlayer\" />" +
            "<TextWidget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"CoverChildren\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"20\" Brush.FontColor=\"#F0E8D8FF\" Brush.TextHorizontalAlignment=\"Left\" Brush.TextVerticalAlignment=\"Top\" Text=\"@Text\" />" +
            "</Children>" +
            "</ListPanel>" +
            "</ItemTemplate>" +
            "</ListPanel>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</ScrollablePanel>" +
            "<Widget Id=\"AilHistoryScrollbarHolder\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"20\" HorizontalAlignment=\"Right\" MarginRight=\"3\">" +
            "<Children>" +
            "<AILegacyScrollbarWidget Id=\"AilHistoryScrollbar\" WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" AlignmentAxis=\"Vertical\" Handle=\"AilHistoryHandle\" MaxValue=\"100\" MinValue=\"0\" MarginTop=\"8\" MarginBottom=\"8\" ScrollVersion=\"@AilScrollVersion\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"3\" HorizontalAlignment=\"Center\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" AlphaFactor=\"0.6\" />" +
            "<Widget Id=\"AilHistoryHandle\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"10\" SuggestedHeight=\"40\" MinHeight=\"20\" HorizontalAlignment=\"Center\" Sprite=\"BlankWhiteSquare_9\" Color=\"#C9B48AFF\" />" +
            "</Children>" +
            "</AILegacyScrollbarWidget>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</DimensionSyncWidget>";
    }
}
