using System;
using AILegacy.Core;
using AILegacy.Scenes;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.CampaignSystem.ViewModelCollection.Party;

namespace AILegacy.Patches
{
    /// <summary>
    /// "Talk" on the party screen, out on the map: instead of the game's fixed conversation, the party screen closes and you meet
    /// that companion at your halted camp, or that prisoner, in the scene on foot. In a settlement the game's own way stays.
    /// </summary>
    [HarmonyPatch(typeof(PartyVM), "ExecuteOpenConversation")]
    internal static class PartyTalkScenePatch
    {
        private static int _errors;

        private static bool Prefix(PartyVM __instance)
        {
            try
            {
                var c = __instance?.CurrentCharacter;
                if (c?.Character == null || !c.Character.IsHero || c.Character == CharacterObject.PlayerCharacter) return true;
                if (c.Side != PartyScreenLogic.PartyRosterSide.Right || LocationComplex.Current != null) return true;
                var h = c.Character.HeroObject;
                // at sea: on deck, with that companion or prisoner
                if (MobileParty.MainParty != null && MobileParty.MainParty.IsCurrentlyAtSea && DeckTalk.CanOpen(h))
                {
                    DeckTalk.Queue(h);
                    Log.Info($"[deck] party screen talk with {h.Name} opens the deck");
                    PartyScreenHelper.CloseScreen(true);
                    return false;
                }
                var mode = MapTalk.ModeFor(h);
                if (mode != MapTalkMode.Companion && mode != MapTalkMode.Prisoner) return true;
                MapTalk.Queue(h);
                Log.Info($"[map scene] party screen talk with {h.Name} opens the scene ({mode})");
                PartyScreenHelper.CloseScreen(true);
                return false;
            }
            catch (Exception ex) { if (_errors++ < 5) Log.Error("Party screen talk in the scene", ex); return true; }
        }
    }
}
