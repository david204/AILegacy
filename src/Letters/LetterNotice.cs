using System;
using AILegacy.Core;
using SandBox.View.Map;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ViewModelCollection.Map.MapNotificationTypes;
using TaleWorlds.Core;
using TaleWorlds.Localization;

namespace AILegacy.Letters
{
    /// <summary>
    /// A received letter as one of the round notices at the right of the map. It is handed straight to the map's
    /// notification list (MBInformationManager.AddNotice) and never to the campaign's own list, which is the one
    /// that gets saved: a type of ours in a save would need a save definer and would tie the save to the mod.
    /// So these last for the session only. The letter itself is already in the character's memory.
    /// </summary>
    public sealed class LetterNoticeData : InformationData
    {
        public readonly Hero From;
        public readonly string Text;
        private readonly TextObject _title;

        public LetterNoticeData(Hero from, string text, string title, string description) : base(Plain(description))
        {
            From = from; Text = text;
            _title = Plain(title);
        }

        // Braces would be read as text variables
        private static TextObject Plain(string s) => new TextObject("{=!}" + (s ?? "").Replace('{', '(').Replace('}', ')'));

        public override TextObject TitleText => _title;
        public override string SoundEventPath => "event:/ui/notification/alert";
    }

    /// <summary>The notice itself. Clicking it opens the letter and removes the notice.</summary>
    public sealed class LetterNoticeVM : MapNotificationItemBaseVM
    {
        public LetterNoticeVM(LetterNoticeData data) : base(data)
        {
            // The icon comes from the identifier. A new one would need its own sprite and brush, so borrow the quest one
            NotificationIdentifier = "quest";
            _onInspect = () =>
            {
                try { LetterSystem.Open(data.From, data.Text); }
                catch (Exception ex) { Log.Error("Cartas: abrir desde el aviso", ex); }
                ExecuteRemove();
            };
        }
    }

    internal static class LetterNotice
    {
        /// <summary>Puts the notice on the map. False if the map's notification list is not there, so the caller can fall back to the window.</summary>
        public static bool Add(Hero from, string text)
        {
            try
            {
                var view = MapScreen.Instance?.GetMapView<MapNotificationView>();
                if (view == null) return false;
                // Setting it again is harmless, and a new map screen starts without it
                view.RegisterMapNotificationType(typeof(LetterNoticeData), typeof(LetterNoticeVM));
                string first = (text ?? "").Replace('\n', ' ').Trim();
                if (first.Length > 140) first = first.Substring(0, 140).TrimEnd() + "…";
                MBInformationManager.AddNotice(new LetterNoticeData(from, text, L.F("AIL_da85936f", $"Carta de {from.Name}"), first));
                return true;
            }
            catch (Exception ex) { Log.Error("Cartas: aviso en el mapa", ex); return false; }
        }
    }
}
