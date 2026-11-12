using System;
using AILegacy.Core;
using TaleWorlds.Library;

namespace AILegacy.UI
{
    public class ChatLineVM : ViewModel
    {
        private string _speaker, _text, _nameColor, _textColor;

        public ChatLineVM(string speaker, string text, string nameColor, string textColor)
        {
            _speaker = speaker; _text = text; _nameColor = nameColor; _textColor = textColor;
        }

        [DataSourceProperty] public string Speaker { get => _speaker; set { if (value != _speaker) { _speaker = value; OnPropertyChangedWithValue(value, nameof(Speaker)); } } }
        [DataSourceProperty] public string Text { get => _text; set { if (value != _text) { _text = value; OnPropertyChangedWithValue(value, nameof(Text)); } } }
        [DataSourceProperty] public string NameColor { get => _nameColor; set { if (value != _nameColor) { _nameColor = value; OnPropertyChangedWithValue(value, nameof(NameColor)); } } }
        [DataSourceProperty] public string TextColor { get => _textColor; set { if (value != _textColor) { _textColor = value; OnPropertyChangedWithValue(value, nameof(TextColor)); } } }
        [DataSourceProperty] public bool HasSpeaker => !string.IsNullOrEmpty(_speaker);
    }

    /// <summary>Datos de la ventana de chat (GUI/Prefabs/AILegacyChat.xml).</summary>
    public class ChatVM : ViewModel
    {
        public const string NpcColor = "#E8C67AFF";
        public const string PlayerColor = "#9CC3E6FF";
        public const string SystemColor = "#B0B0B0FF";
        public const string ErrorColor = "#E07070FF";
        public const string BodyColor = "#F2E8D5FF";

        private readonly Action<string> _onSend;
        private readonly Action _onClose;

        private string _title = "", _subtitle = "", _inputText = "", _waitingText = "", _sendText = L.T("AIL_aa23f794", "Decir"), _closeText = L.T("AIL_e5c9b3a5", "Terminar");
        private bool _isWaiting, _canType = true;
        private int _scrollVersion;
        private MBBindingList<ChatLineVM> _lines = new MBBindingList<ChatLineVM>();

        public ChatVM(string title, Action<string> onSend, Action onClose)
        {
            _title = title; _onSend = onSend; _onClose = onClose;
        }

        // ------------------------------------------------------------ API
        public void AddLine(string speaker, string text, string nameColor, string textColor = BodyColor)
        {
            Lines.Add(new ChatLineVM(speaker, text, nameColor, textColor));
            ScrollVersion++;
        }

        public void AddSystem(string text) => AddLine("", text, SystemColor, SystemColor);
        public void AddError(string text) => AddLine("", text, ErrorColor, ErrorColor);

        // ------------------------------------------------------------ comandos (desde el XML)
        public void ExecuteSend()
        {
            if (IsWaiting || !CanType) return;
            var t = (InputText ?? "").Trim();
            if (t.Length == 0) return;
            InputText = "";
            _onSend?.Invoke(t);
        }

        public void ExecuteClose() => _onClose?.Invoke();

        // ------------------------------------------------------------ propiedades enlazadas
        [DataSourceProperty] public MBBindingList<ChatLineVM> Lines { get => _lines; set { if (value != _lines) { _lines = value; OnPropertyChangedWithValue(value, nameof(Lines)); } } }
        [DataSourceProperty] public string Title { get => _title; set { if (value != _title) { _title = value; OnPropertyChangedWithValue(value, nameof(Title)); } } }
        [DataSourceProperty] public string Subtitle { get => _subtitle; set { if (value != _subtitle) { _subtitle = value; OnPropertyChangedWithValue(value, nameof(Subtitle)); } } }
        [DataSourceProperty] public string InputText { get => _inputText; set { if (value != _inputText) { _inputText = value; OnPropertyChangedWithValue(value, nameof(InputText)); } } }
        [DataSourceProperty] public string WaitingText { get => _waitingText; set { if (value != _waitingText) { _waitingText = value; OnPropertyChangedWithValue(value, nameof(WaitingText)); } } }
        [DataSourceProperty] public string SendText { get => _sendText; set { if (value != _sendText) { _sendText = value; OnPropertyChangedWithValue(value, nameof(SendText)); } } }
        [DataSourceProperty] public string CloseText { get => _closeText; set { if (value != _closeText) { _closeText = value; OnPropertyChangedWithValue(value, nameof(CloseText)); } } }
        [DataSourceProperty] public int ScrollVersion { get => _scrollVersion; set { if (value != _scrollVersion) { _scrollVersion = value; OnPropertyChangedWithValue(value, nameof(ScrollVersion)); } } }

        [DataSourceProperty]
        public bool IsWaiting
        {
            get => _isWaiting;
            set { if (value != _isWaiting) { _isWaiting = value; OnPropertyChangedWithValue(value, nameof(IsWaiting)); OnPropertyChangedWithValue(CanSend, nameof(CanSend)); } }
        }

        [DataSourceProperty]
        public bool CanType
        {
            get => _canType;
            set { if (value != _canType) { _canType = value; OnPropertyChangedWithValue(value, nameof(CanType)); OnPropertyChangedWithValue(CanSend, nameof(CanSend)); } }
        }

        [DataSourceProperty] public bool CanSend => !_isWaiting && _canType;
    }
}
