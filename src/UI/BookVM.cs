using System;
using AILegacy.Core;
using System.Collections.Generic;
using TaleWorlds.Library;

namespace AILegacy.UI
{
    public class BookEntryVM : ViewModel
    {
        private readonly Action<BookEntryVM> _onSelect;
        private string _title, _subtitle;
        private bool _isSelected;
        public object Tag { get; }

        public BookEntryVM(string title, string subtitle, object tag, Action<BookEntryVM> onSelect)
        {
            _title = title; _subtitle = subtitle; Tag = tag; _onSelect = onSelect;
        }

        public void ExecuteSelect() => _onSelect?.Invoke(this);

        [DataSourceProperty] public string Title { get => _title; set { if (value != _title) { _title = value; OnPropertyChangedWithValue(value, nameof(Title)); } } }
        [DataSourceProperty] public string Subtitle { get => _subtitle; set { if (value != _subtitle) { _subtitle = value; OnPropertyChangedWithValue(value, nameof(Subtitle)); } } }
        /// <summary>The entry shown on the right, highlighted in the list.</summary>
        [DataSourceProperty] public bool IsSelected { get => _isSelected; set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } } }
    }

    /// <summary>"Book" window: list on the left, details on the right.</summary>
    public class BookVM : ViewModel
    {
        private string _title = "", _detailTitle = "", _detailText = "", _actionText = "", _closeText = L.T("AIL_e8438e2a", "Cerrar");
        private bool _hasAction, _hasAction2, _hasImage;
        private string _action2Text = "", _detailImage = "";
        public Action OnAction2;
        public void ExecuteAction2() => OnAction2?.Invoke();
        private MBBindingList<BookEntryVM> _entries = new MBBindingList<BookEntryVM>();
        public Action OnClose;
        public Action<BookEntryVM> OnAction;
        public BookEntryVM Selected { get; private set; }

        public void Select(BookEntryVM e, string detailTitle, string detailText, string imagePath = null)
        {
            if (Selected != null && Selected != e) Selected.IsSelected = false;
            if (e != null) e.IsSelected = true;
            Selected = e;
            RefreshCounter();
            DetailTitle = detailTitle;
            DetailText = detailText;
            DetailImage = imagePath ?? "";
            HasImage = !string.IsNullOrEmpty(imagePath);
        }

        public void ExecuteClose() => OnClose?.Invoke();
        public void ExecuteAction() { if (Selected != null) OnAction?.Invoke(Selected); }

        // ------------------------------------------------------------ turning pages
        private bool _hasPaging;
        private string _counterText = "";

        /// <summary>Previous and next buttons, with "N / M" between them. They wrap around at both ends.</summary>
        [DataSourceProperty] public bool HasPaging { get => _hasPaging; set { if (value != _hasPaging) { _hasPaging = value; OnPropertyChangedWithValue(value, nameof(HasPaging)); RefreshCounter(); } } }
        [DataSourceProperty] public string CounterText { get => _counterText; set { if (value != _counterText) { _counterText = value; OnPropertyChangedWithValue(value, nameof(CounterText)); } } }
        [DataSourceProperty] public string PreviousText => L.T("AIL_e7b1cf06", "Anterior");
        [DataSourceProperty] public string NextText => L.T("AIL_e7b1cf07", "Siguiente");

        public void ExecutePrevious() => Turn(-1);
        public void ExecuteNext() => Turn(1);

        private void Turn(int step)
        {
            int n = _entries.Count;
            if (n == 0) return;
            int i = Selected == null ? -1 : _entries.IndexOf(Selected);
            i = i < 0 ? 0 : ((i + step) % n + n) % n;
            _entries[i].ExecuteSelect();
        }

        private void RefreshCounter()
        {
            int n = _entries.Count;
            int i = Selected == null ? -1 : _entries.IndexOf(Selected);
            CounterText = n == 0 ? "0 / 0" : $"{(i < 0 ? 0 : i + 1)} / {n}";
        }

        [DataSourceProperty] public MBBindingList<BookEntryVM> Entries { get => _entries; set { if (value != _entries) { _entries = value; OnPropertyChangedWithValue(value, nameof(Entries)); } } }
        [DataSourceProperty] public string Title { get => _title; set { if (value != _title) { _title = value; OnPropertyChangedWithValue(value, nameof(Title)); } } }
        [DataSourceProperty] public string DetailTitle { get => _detailTitle; set { if (value != _detailTitle) { _detailTitle = value; OnPropertyChangedWithValue(value, nameof(DetailTitle)); } } }
        [DataSourceProperty] public string DetailText { get => _detailText; set { if (value != _detailText) { _detailText = value; OnPropertyChangedWithValue(value, nameof(DetailText)); } } }
        [DataSourceProperty] public string ActionText { get => _actionText; set { if (value != _actionText) { _actionText = value; OnPropertyChangedWithValue(value, nameof(ActionText)); } } }
        [DataSourceProperty] public string CloseText { get => _closeText; set { if (value != _closeText) { _closeText = value; OnPropertyChangedWithValue(value, nameof(CloseText)); } } }
        [DataSourceProperty] public string DetailImage { get => _detailImage; set { if (value != _detailImage) { _detailImage = value; OnPropertyChangedWithValue(value, nameof(DetailImage)); } } }
        [DataSourceProperty] public bool HasImage { get => _hasImage; set { if (value != _hasImage) { _hasImage = value; OnPropertyChangedWithValue(value, nameof(HasImage)); } } }
        [DataSourceProperty] public string Action2Text { get => _action2Text; set { if (value != _action2Text) { _action2Text = value; OnPropertyChangedWithValue(value, nameof(Action2Text)); } } }
        [DataSourceProperty] public bool HasAction2 { get => _hasAction2; set { if (value != _hasAction2) { _hasAction2 = value; OnPropertyChangedWithValue(value, nameof(HasAction2)); } } }
        [DataSourceProperty] public bool HasAction { get => _hasAction; set { if (value != _hasAction) { _hasAction = value; OnPropertyChangedWithValue(value, nameof(HasAction)); } } }
    }

    /// <summary>One row of a news card's lists: a decision with its colored tag, or a line of the consequences block.</summary>
    public class NewsLineVM : ViewModel
    {
        public NewsLineVM(string text, int tone, string label = "", string color = "#3B3A4AFF")
        { Text = text ?? ""; Tone = tone; Label = label ?? ""; Color = color; }
        /// <summary>0 = where it falls (gold), 1 = good (green), 2 = bad (red), 3 = plain (white).</summary>
        public int Tone { get; }
        [DataSourceProperty] public string Text { get; }
        [DataSourceProperty] public string Label { get; }
        [DataSourceProperty] public string Color { get; }
        [DataSourceProperty] public bool IsPlace => Tone == 0;
        [DataSourceProperty] public bool IsGood => Tone == 1;
        [DataSourceProperty] public bool IsBad => Tone == 2;
        [DataSourceProperty] public bool IsPlain => Tone == 3;
    }

    /// <summary>A card of the news window, or (IsHeader) the line that opens a day.</summary>
    public class NewsCardVM : ViewModel
    {
        public NewsCardVM(string title, string participants, string text, string effects, string tag, string when, int tagKind = 2)
        { Title = title; Participants = participants ?? ""; Text = text; Tag = tag; When = when; TagKind = tagKind; if (!string.IsNullOrEmpty(effects)) EffectLines.Add(new NewsLineVM(effects, 3)); }

        /// <summary>The header that opens the news of one day.</summary>
        public static NewsCardVM Header(string text) => new NewsCardVM("", "", "", "", "", "") { IsHeader = true, HeaderText = text };

        /// <summary>Tag color: 0 = local (brown), 1 = political (blue), 2 = anything else (green). TagColor wins when set.</summary>
        public int TagKind { get; }
        public string TagColorValue;
        [DataSourceProperty] public string TagColor => TagColorValue ?? (TagKind == 0 ? "#4A3524FF" : TagKind == 1 ? "#2B3760FF" : "#1F4A44FF");
        [DataSourceProperty] public bool IsLocal => TagKind == 0;
        [DataSourceProperty] public bool IsPolitical => TagKind == 1;
        [DataSourceProperty] public bool IsOther => TagKind == 2;
        [DataSourceProperty] public bool IsHeader { get; private set; }
        [DataSourceProperty] public bool IsCard => !IsHeader;
        [DataSourceProperty] public string HeaderText { get; private set; } = "";
        [DataSourceProperty] public string Title { get; }
        [DataSourceProperty] public string Participants { get; }
        [DataSourceProperty] public string Text { get; }
        [DataSourceProperty] public string Tag { get; }
        [DataSourceProperty] public string When { get; }
        /// <summary>Where it happened ("Place: Pravend (Vlandia)").</summary>
        [DataSourceProperty] public string Place { get; set; } = "";
        /// <summary>"Chapter 2" on a development of a story already told.</summary>
        [DataSourceProperty] public string Chapter { get; set; } = "";
        [DataSourceProperty] public string DecisionsTitle => L.T("AIL_e7b1cb0f", "Decisiones");
        [DataSourceProperty] public string EffectsTitle => L.T("AIL_e7b1cb10", "Consecuencias");
        [DataSourceProperty] public string DiseaseTitle => L.T("AIL_ee39700e", "Enfermedad");
        [DataSourceProperty] public string Disease { get; set; } = "";
        [DataSourceProperty] public MBBindingList<NewsLineVM> Decisions { get; } = new MBBindingList<NewsLineVM>();
        [DataSourceProperty] public MBBindingList<NewsLineVM> EffectLines { get; } = new MBBindingList<NewsLineVM>();
        [DataSourceProperty] public bool HasParticipants => !string.IsNullOrEmpty(Participants);
        [DataSourceProperty] public bool HasPlace => !string.IsNullOrEmpty(Place);
        [DataSourceProperty] public bool HasChapter => !string.IsNullOrEmpty(Chapter);
        [DataSourceProperty] public bool HasDecisions => Decisions.Count > 0;
        [DataSourceProperty] public bool HasDisease => !string.IsNullOrEmpty(Disease);
        /// <summary>The tag row (type and date) only shows on real news, not on the "nothing here" card.</summary>
        [DataSourceProperty] public bool HasTags => !string.IsNullOrEmpty(Tag) || !string.IsNullOrEmpty(When);
        [DataSourceProperty] public bool HasEffects => EffectLines.Count > 0;
        /// <summary>Everything a search can find in this card, lower case.</summary>
        public string SearchText = "";
    }

    /// <summary>A filter button of the news window: a kind of record or a category, with how many there are.</summary>
    public class NewsFilterVM : ViewModel
    {
        private readonly Action<NewsFilterVM> _onSelect;
        private bool _isSelected;
        private string _text;
        public NewsFilterVM(string key, string text, string color, Action<NewsFilterVM> onSelect) { Key = key; _text = text ?? ""; Color = color; _onSelect = onSelect; }
        public string Key { get; }
        public void ExecuteSelect() => _onSelect?.Invoke(this);
        [DataSourceProperty] public string Text { get => _text; set { if (value != _text) { _text = value; OnPropertyChangedWithValue(value, nameof(Text)); } } }
        [DataSourceProperty] public string Color { get; }
        [DataSourceProperty] public bool IsSelected { get => _isSelected; set { if (value != _isSelected) { _isSelected = value; OnPropertyChangedWithValue(value, nameof(IsSelected)); } } }
    }

    public class NewsFeedVM : ViewModel
    {
        public Action OnClose, OnAction, OnToggle, OnSearch;
        public void ExecuteClose() => OnClose?.Invoke();
        public void ExecuteAction() => OnAction?.Invoke();
        public void ExecuteToggle() => OnToggle?.Invoke();
        private string _toggleText = "", _searchText = "";
        [DataSourceProperty] public string ToggleText { get => _toggleText; set { if (value != _toggleText) { _toggleText = value; OnPropertyChangedWithValue(value, nameof(ToggleText)); } } }
        /// <summary>What the player typed in the search box. Every change filters the cards again.</summary>
        [DataSourceProperty] public string SearchText { get => _searchText; set { if (value != _searchText) { _searchText = value ?? ""; OnPropertyChangedWithValue(value, nameof(SearchText)); OnSearch?.Invoke(); } } }
        [DataSourceProperty] public string SearchPlaceholder => L.T("AIL_e7b1cb11", "Buscar en la crónica…");
        [DataSourceProperty] public string Title { get; set; } = "";
        [DataSourceProperty] public string CloseText => L.T("AIL_e8438e2a", "Cerrar");
        [DataSourceProperty] public string ActionText { get; set; } = "";
        [DataSourceProperty] public bool HasAction { get; set; }
        [DataSourceProperty] public MBBindingList<NewsCardVM> Cards { get; } = new MBBindingList<NewsCardVM>();
        /// <summary>All / Events / Statements, each with its count.</summary>
        [DataSourceProperty] public MBBindingList<NewsFilterVM> Records { get; } = new MBBindingList<NewsFilterVM>();
        /// <summary>The categories the news shown has, each with its count. None picked = all of them.</summary>
        [DataSourceProperty] public MBBindingList<NewsFilterVM> Categories { get; } = new MBBindingList<NewsFilterVM>();
    }

    /// <summary>
    /// The map notice for a new memory book page. Only shown (MBInformationManager.AddNotice), never added to the
    /// campaign's saved notices: an unknown type in the save would make it unloadable without the mod.
    /// </summary>
    public sealed class MemoryPageNotice : TaleWorlds.Core.InformationData
    {
        public readonly Memory.MemoryPage Page;
        public MemoryPageNotice(TaleWorlds.Localization.TextObject description, Memory.MemoryPage page) : base(description) { Page = page; }
        public override TaleWorlds.Localization.TextObject TitleText => new TaleWorlds.Localization.TextObject("{=!}" + L.T("AIL_18fe8281", "Libro de memorias"));
        public override string SoundEventPath => PageNotices.Sound;
    }

    /// <summary>Clicking the notice opens the book on that page and takes the notice away.</summary>
    public sealed class MemoryPageNoticeItemVM : TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapNotificationTypes.MapNotificationItemBaseVM
    {
        public MemoryPageNoticeItemVM(MemoryPageNotice data) : base(data)
        {
            NotificationIdentifier = "education";
            var page = data?.Page;
            _onInspect = () =>
            {
                try
                {
                    if (!PageNotices.CanOpenBook()) return;
                    Books.OpenChapters(page);
                    ExecuteRemove();
                }
                catch (Exception ex) { Log.Error("Memory page notice", ex); }
            };
        }
    }

    public class MapButtonsVM : ViewModel
    {
        public Action OnNews, OnMemories, OnQuests;
        public void ExecuteQuests() => OnQuests?.Invoke();
        [DataSourceProperty] public string QuestsText => L.T("AIL_32bdb403", "Encargos");
        private bool _isVisible = true;
        public void ExecuteNews() => OnNews?.Invoke();
        public void ExecuteMemories() => OnMemories?.Invoke();
        [DataSourceProperty] public string NewsText => L.T("AIL_e5c9b3a1", "Noticias");
        [DataSourceProperty] public string MemoriesText => L.T("AIL_e5c9b3a2", "Memorias");
        [DataSourceProperty] public bool IsVisible { get => _isVisible; set { if (value != _isVisible) { _isVisible = value; OnPropertyChangedWithValue(value, nameof(IsVisible)); } } }
    }
}
