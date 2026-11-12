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
    /// <summary>Una línea del historial que se despliega sobre el cuadro de diálogo.</summary>
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
    /// Lo que AI Legacy añade a la pantalla de conversación del juego: junto al nombre, las barras de relación, confianza y
    /// romance (y un aviso si sospecha que le mientes o hay tensión); y un botón que despliega, sobre el cuadro de diálogo,
    /// lo hablado con ese personaje, con scroll.
    /// </summary>
    [ViewModelMixin("Refresh")]
    internal sealed class ConversationMixin : BaseViewModelMixin<MissionConversationVM>
    {
        private static WeakReference<ConversationMixin> _current;
        private static float _since;

        private bool _cardVisible;
        private bool _statsVisible, _trustVisible, _romanceVisible, _warnVisible, _historyAvailable, _historyVisible;
        private string _relationText = "", _trustText = "", _romanceText = "", _warnText = "", _historyLabel = "";
        private int _relationFill, _trustFill, _romanceFill, _scrollVersion;
        private MBBindingList<AilHistoryLineVM> _history = new MBBindingList<AilHistoryLineVM>();
        private string _historyOf;
        private int _historyCount = -1;

        public ConversationMixin(MissionConversationVM vm) : base(vm)
        {
            _current = new WeakReference<ConversationMixin>(this);
            try { Recompute(); } catch (Exception ex) { Log.Error("Conversación: barras", ex); }
        }

        /// <summary>¿Están las barras en pantalla ahora mismo? (entonces sobra la línea de cifras bajo la respuesta)</summary>
        public static bool ShowingStats
        {
            get { try { return _current != null && _current.TryGetTarget(out var m) && m._statsVisible; } catch { return false; } }
        }

        /// <summary>Dos veces por segundo: las cifras cambian tras cada respuesta sin que el juego refresque su pantalla.</summary>
        public static void Tick(float dt)
        {
            _since += dt;
            if (_since < 0.5f) return;
            _since = 0f;
            try { if (_current != null && _current.TryGetTarget(out var m)) m.Recompute(); }
            catch (Exception ex) { Log.Error("Conversación: barras", ex); }
        }

        public override void OnRefresh()
        {
            try { Recompute(); } catch (Exception ex) { Log.Error("Conversación: barras", ex); }
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

            // ---- barras
            bool stats = h != null && cfg.ShowNameBars;
            AilStatsVisible = stats;
            if (stats)
            {
                int rel = (int)h.GetRelationWithPlayer();
                AilRelationText = L.F("AIL_f3138943", $"Relación: {rel:+0;-0;0}");
                AilRelationFill = Math.Max(0, Math.Min(100, (rel + 100) / 2));
                bool trust = cfg.EnableTrust && mem != null && (mem.TrustSet || mem.ConversationCount > 0);
                AilTrustVisible = trust;
                if (trust) { AilTrustText = L.F("AIL_2cb6f858", $"Confianza: {mem.Trust}"); AilTrustFill = Math.Max(0, Math.Min(100, (mem.Trust + 100) / 2)); }
                bool romance = mem != null && mem.Romance > 0;
                AilRomanceVisible = romance;
                if (romance) { AilRomanceText = L.F("AIL_d5a0e001", $"Romance: {mem.Romance}"); AilRomanceFill = Math.Max(0, Math.Min(100, mem.Romance)); }
                string warn = "";
                if (mem != null && mem.LiesSuspected > 0) warn = L.T("AIL_d5a0e002", "Sospecha que mientes");
                if (mem != null && cfg.EnableTension && Bonds.Tension(mem) > 0)
                    warn += (warn.Length > 0 ? "  ·  " : "") + L.F("AIL_55654690", $"Tensión: {Bonds.Tension(mem)}");
                AilWarnText = warn;
                AilWarnVisible = warn.Length > 0;
            }
            else { AilTrustVisible = false; AilRomanceVisible = false; AilWarnVisible = false; }

            // ---- historial
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
            AilScrollVersion = _scrollVersion + 1;       // baja al final
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
            catch (Exception ex) { Log.Error("Conversación: historial", ex); }
        }

        [DataSourceProperty] public bool AilCardVisible { get => _cardVisible; set { if (value != _cardVisible) { _cardVisible = value; OnPropertyChangedWithValue(value, nameof(AilCardVisible)); } } }
        [DataSourceProperty] public bool AilStatsVisible { get => _statsVisible; set { if (value != _statsVisible) { _statsVisible = value; OnPropertyChangedWithValue(value, nameof(AilStatsVisible)); } } }
        [DataSourceProperty] public bool AilTrustVisible { get => _trustVisible; set { if (value != _trustVisible) { _trustVisible = value; OnPropertyChangedWithValue(value, nameof(AilTrustVisible)); } } }
        [DataSourceProperty] public bool AilRomanceVisible { get => _romanceVisible; set { if (value != _romanceVisible) { _romanceVisible = value; OnPropertyChangedWithValue(value, nameof(AilRomanceVisible)); } } }
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
    /// Una tarjeta propia justo encima de la franja del nombre: fondo oscuro con filete dorado, las barras, el aviso y el
    /// botón del historial. Se coloca igual que la franja del nombre del juego (copia la altura del cuadro de diálogo), así
    /// sube y baja con él y no pisa el nombre ni el estandarte.
    /// </summary>
    [PrefabExtension("SPConversation", "descendant::ListPanel[@Id='VerticalContainer']")]
    internal sealed class ConversationStatsPrefab : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;

        private static string Gauge(string text, string fill, string visible, string color) =>
            "<ListPanel WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"CoverChildren\" VerticalAlignment=\"Center\" MarginRight=\"18\" StackLayout.LayoutMethod=\"VerticalTopToBottom\" DoNotAcceptEvents=\"true\"" +
            (visible == null ? "" : " IsVisible=\"@" + visible + "\"") + ">" +
            "<Children>" +
            "<TextWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"140\" SuggestedHeight=\"24\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Brush.TextHorizontalAlignment=\"Left\" Text=\"@" + text + "\" />" +
            "<FillBarWidget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"140\" SuggestedHeight=\"8\" MarginTop=\"3\" Sprite=\"BlankWhiteSquare_9\" Color=\"#2A241AFF\" FillWidget=\"FillWidget\" InitialAmount=\"@" + fill + "\" MaxAmount=\"100\" IsEnabled=\"false\">" +
            "<Children>" +
            "<Widget Id=\"FillWidget\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"140\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Center\" ClipContents=\"true\">" +
            "<Children><Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"140\" SuggestedHeight=\"8\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"" + color + "\" /></Children>" +
            "</Widget>" +
            "</Children>" +
            "</FillBarWidget>" +
            "</Children>" +
            "</ListPanel>";

        // Altura de la tarjeta 64; la franja del nombre del juego usa 55 de margen: 55 + 8 de hueco + 64
        [PrefabExtensionText]
        public string Xml =>
            "<DimensionSyncWidget Id=\"AilStatsSync\" DoNotAcceptEvents=\"true\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"680\" VerticalAlignment=\"Bottom\" HorizontalAlignment=\"Center\" MarginRight=\"700\" WidgetToCopyHeightFrom=\"..\\VerticalContainer\\BottomPanelsContainer\\DialogueContainer\" DimensionToSync=\"Vertical\" PaddingAmount=\"127\">" +
            "<Children>" +
            "<Widget Id=\"AilStatsCard\" WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"64\" HorizontalAlignment=\"Left\" VerticalAlignment=\"Top\" MarginLeft=\"40\" IsVisible=\"@AilCardVisible\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" Sprite=\"BlankWhiteSquare_9\" Color=\"#0D0A07FF\" AlphaFactor=\"0.86\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"2\" VerticalAlignment=\"Top\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"2\" VerticalAlignment=\"Bottom\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"2\" HorizontalAlignment=\"Left\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"StretchToParent\" SuggestedWidth=\"2\" HorizontalAlignment=\"Right\" Sprite=\"BlankWhiteSquare_9\" Color=\"#A37434FF\" DoNotAcceptEvents=\"true\" />" +
            "<ListPanel WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"CoverChildren\" VerticalAlignment=\"Center\" MarginLeft=\"18\" MarginRight=\"14\">" +
            "<Children>" +
            "<ListPanel WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"CoverChildren\" VerticalAlignment=\"Center\" IsVisible=\"@AilStatsVisible\" DoNotAcceptEvents=\"true\">" +
            "<Children>" +
            Gauge("AilRelationText", "AilRelationFill", null, "#D9BF86FF") +
            Gauge("AilTrustText", "AilTrustFill", "AilTrustVisible", "#7FB0D9FF") +
            Gauge("AilRomanceText", "AilRomanceFill", "AilRomanceVisible", "#D97F9FFF") +
            "</Children>" +
            "</ListPanel>" +
            "<TextWidget WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"30\" VerticalAlignment=\"Center\" MarginRight=\"18\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"17\" Brush.FontColor=\"#F0A040FF\" Text=\"@AilWarnText\" IsVisible=\"@AilWarnVisible\" />" +
            "<ButtonWidget WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"36\" VerticalAlignment=\"Center\" Command.Click=\"ExecuteAilToggleHistory\" IsVisible=\"@AilHistoryAvailable\" UpdateChildrenStates=\"true\">" +
            "<Children>" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"StretchToParent\" Sprite=\"BlankWhiteSquare_9\" Color=\"#3A2C16FF\" DoNotAcceptEvents=\"true\" />" +
            "<Widget WidthSizePolicy=\"StretchToParent\" HeightSizePolicy=\"Fixed\" SuggestedHeight=\"1\" VerticalAlignment=\"Bottom\" Sprite=\"BlankWhiteSquare_9\" Color=\"#C9B48AFF\" DoNotAcceptEvents=\"true\" />" +
            "<TextWidget WidthSizePolicy=\"CoverChildren\" HeightSizePolicy=\"StretchToParent\" MarginLeft=\"14\" MarginRight=\"14\" Brush=\"Popup.Title.Text\" Brush.FontSize=\"18\" Brush.FontColor=\"#F0E6CCFF\" Text=\"@AilHistoryLabel\" DoNotAcceptEvents=\"true\" />" +
            "</Children>" +
            "</ButtonWidget>" +
            "</Children>" +
            "</ListPanel>" +
            "</Children>" +
            "</Widget>" +
            "</Children>" +
            "</DimensionSyncWidget>";
    }

    /// <summary>Encima de la tarjeta: lo hablado con ese personaje, con scroll. Sube y baja con el cuadro de diálogo.</summary>
    [PrefabExtension("SPConversation", "descendant::ListPanel[@Id='VerticalContainer']")]
    internal sealed class ConversationHistoryPrefab : PrefabExtensionInsertPatch
    {
        public override InsertType Type => InsertType.Append;

        // 127 de la tarjeta + 8 de hueco + 430 del panel
        [PrefabExtensionText]
        public string Xml =>
            "<DimensionSyncWidget Id=\"AilHistorySync\" DoNotAcceptEvents=\"true\" WidthSizePolicy=\"Fixed\" HeightSizePolicy=\"Fixed\" SuggestedWidth=\"680\" VerticalAlignment=\"Bottom\" HorizontalAlignment=\"Center\" MarginRight=\"700\" WidgetToCopyHeightFrom=\"..\\VerticalContainer\\BottomPanelsContainer\\DialogueContainer\" DimensionToSync=\"Vertical\" PaddingAmount=\"565\">" +
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
