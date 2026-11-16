using TaleWorlds.CampaignSystem;

namespace AILegacy.Letters
{
    /// <summary>
    /// Received letters used to show as a round notice on the map, which had no sprite of its own (a magenta circle)
    /// and only opened the letter when clicked. Letters now open as a popup as soon as the player is free on the map
    /// (LetterSystem.Receive). This stays as the entry point other systems already call.
    /// </summary>
    internal static class LetterNotice
    {
        /// <summary>Queues the letter to pop up as soon as possible. Always true: the letter is never lost.</summary>
        public static bool Add(Hero from, string text)
        {
            LetterSystem.Receive(from, text);
            return true;
        }
    }
}
