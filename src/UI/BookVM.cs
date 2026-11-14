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
        public object Tag { get; }

        public BookEntryVM(string title, string subtitle, object tag, Action<BookEntryVM> onSelect)
        {
            _title = title; _subtitle = subtitle; Tag = tag; _onSelect = onSelect;
        }

        public void ExecuteSelect() => _onSelect?.Invoke(this);

        [DataSourceProperty] public string Title { get => _title; set { if (value != _title) { _title = value; OnPropertyChangedWithValue(value, nameof(Title)); } } }
        [DataSourceProperty] public string Subtitle { get => _subtitle; set { if (value != _subtitle) { _subtitle = value; OnPropertyChangedWithValue(value, nameof(Subtitle)); } } }
    }

    /// <summary>"Book" window: list on the left, details on the right.</summary>
    public class BookVM : ViewModel
    {
        private string _title = "", _detailTitle = "", _detailText = "", _actionText = "", _closeText = L.T("AIL_e8438e2a", "Cerrar");
        private bool _hasAction;
        private MBBindingList<BookEntryVM> _entries = new MBBindingList<BookEntryVM>();
        public Action OnClose;
        public Action<BookEntryVM> OnAction;
        public BookEntryVM Selected { get; private set; }

        public void Select(BookEntryVM e, string detailTitle, string detailText)
        {
            Selected = e;
            DetailTitle = detailTitle;
            DetailText = detailText;
        }

        public void ExecuteClose() => OnClose?.Invoke();
        public void ExecuteAction() { if (Selected != null) OnAction?.Invoke(Selected); }

        [DataSourceProperty] public MBBindingList<BookEntryVM> Entries { get => _entries; set { if (value != _entries) { _entries = value; OnPropertyChangedWithValue(value, nameof(Entries)); } } }
        [DataSourceProperty] public string Title { get => _title; set { if (value != _title) { _title = value; OnPropertyChangedWithValue(value, nameof(Title)); } } }
        [DataSourceProperty] public string DetailTitle { get => _detailTitle; set { if (value != _detailTitle) { _detailTitle = value; OnPropertyChangedWithValue(value, nameof(DetailTitle)); } } }
        [DataSourceProperty] public string DetailText { get => _detailText; set { if (value != _detailText) { _detailText = value; OnPropertyChangedWithValue(value, nameof(DetailText)); } } }
        [DataSourceProperty] public string ActionText { get => _actionText; set { if (value != _actionText) { _actionText = value; OnPropertyChangedWithValue(value, nameof(ActionText)); } } }
        [DataSourceProperty] public string CloseText { get => _closeText; set { if (value != _closeText) { _closeText = value; OnPropertyChangedWithValue(value, nameof(CloseText)); } } }
        [DataSourceProperty] public bool HasAction { get => _hasAction; set { if (value != _hasAction) { _hasAction = value; OnPropertyChangedWithValue(value, nameof(HasAction)); } } }
    }

    public class NewsCardVM : ViewModel
    {
        public NewsCardVM(string title, string participants, string text, string effects, string tag, string when, int tagKind = 2)
        { Title = title; Participants = participants ?? ""; Text = text; Effects = effects ?? ""; Tag = tag; When = when; TagKind = tagKind; }
        /// <summary>Tag color: 0 = local (brown), 1 = political (blue), 2 = anything else (green).</summary>
        public int TagKind { get; }
        [DataSourceProperty] public bool IsLocal => TagKind == 0;
        [DataSourceProperty] public bool IsPolitical => TagKind == 1;
        [DataSourceProperty] public bool IsOther => TagKind == 2;
        [DataSourceProperty] public string Title { get; }
        [DataSourceProperty] public string Participants { get; }
        [DataSourceProperty] public string Text { get; }
        [DataSourceProperty] public string Effects { get; }
        [DataSourceProperty] public string Tag { get; }
        [DataSourceProperty] public string When { get; }
        [DataSourceProperty] public bool HasParticipants => Participants.Length > 0;
        /// <summary>The tag row (type and date) only shows on real news, not on the "nothing here" card.</summary>
        [DataSourceProperty] public bool HasTags => !string.IsNullOrEmpty(Tag) || !string.IsNullOrEmpty(When);
        [DataSourceProperty] public bool HasEffects => Effects.Length > 0;
    }

    public class NewsFeedVM : ViewModel
    {
        public Action OnClose, OnAction, OnToggle;
        public void ExecuteClose() => OnClose?.Invoke();
        public void ExecuteAction() => OnAction?.Invoke();
        public void ExecuteToggle() => OnToggle?.Invoke();
        private string _toggleText = "";
        [DataSourceProperty] public string ToggleText { get => _toggleText; set { if (value != _toggleText) { _toggleText = value; OnPropertyChangedWithValue(value, nameof(ToggleText)); } } }
        [DataSourceProperty] public string Title { get; set; } = "";
        [DataSourceProperty] public string CloseText => L.T("AIL_e8438e2a", "Cerrar");
        [DataSourceProperty] public string ActionText { get; set; } = "";
        [DataSourceProperty] public bool HasAction { get; set; }
        [DataSourceProperty] public MBBindingList<NewsCardVM> Cards { get; } = new MBBindingList<NewsCardVM>();
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
