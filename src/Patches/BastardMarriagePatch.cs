using AILegacy.Settings;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.BarterSystem.Barterables;

namespace AILegacy.Patches
{
    /// <summary>
    /// In a marriage negotiation a baseborn child is worth less: the house asking for their hand offers less, a house
    /// giving a legitimate child to a baseborn one asks for more, and the baseborn's own house lets them go cheaper.
    /// </summary>
    [HarmonyPatch(typeof(MarriageBarterable), nameof(MarriageBarterable.GetUnitValueForFaction))]
    internal static class BastardMarriagePatch
    {
        private static void Postfix(MarriageBarterable __instance, IFaction faction, ref int __result)
        {
            try
            {
                int penalty = S.Get.BastardMarriagePenalty;
                if (penalty <= 0 || !Bastards.Enabled || __instance == null || faction == null) return;
                Hero asking = __instance.ProposingHero, asked = __instance.HeroBeingProposedTo;
                if (asking == null || asked == null) return;
                bool askedBaseborn = Bastards.IsBaseborn(asked), askingBaseborn = Bastards.IsBaseborn(asking);
                if (askedBaseborn == askingBaseborn) return;   // both or neither: an even match
                if (faction == asking.Clan)
                {
                    if (askedBaseborn) __result -= penalty;
                }
                else if (faction == asked.Clan)
                {
                    if (askingBaseborn) __result -= penalty;
                    else __result += penalty / 2;
                }
            }
            catch { }
        }
    }
}
