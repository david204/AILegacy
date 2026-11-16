using System;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Election;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.TournamentGames;

namespace AILegacy.Patches
{
    /// <summary>Caravans don't pick a quarantined town as their destination.</summary>
    [HarmonyPatch(typeof(CaravansCampaignBehavior), "GetTradeScoreForTown")]
    internal static class QuarantineCaravanPatch
    {
        private static void Postfix(MobileParty caravanParty, Town town, ref float __result)
        {
            try { if (__result > 0f && town != null && QuarantineRules.Barred(caravanParty, town.Settlement)) __result = 0f; }
            catch (Exception ex) { Log.Error("[quarantine] caravans", ex); }
        }
    }

    /// <summary>Villagers don't go out to sell if their village or its market town is quarantined.</summary>
    [HarmonyPatch(typeof(VillagerCampaignBehavior), "ThinkAboutSendingItemToTown")]
    internal static class QuarantineVillagerSendPatch
    {
        private static bool Prefix(Village village)
        {
            try { return village == null || (!QuarantineRules.Closed(village.Settlement) && !QuarantineRules.Closed(village.TradeBound)); }
            catch { return true; }
        }
    }

    [HarmonyPatch(typeof(VillagerCampaignBehavior), "SendVillagerPartyToTradeBoundTown")]
    internal static class QuarantineVillagerGoPatch
    {
        private static bool Prefix(MobileParty villagerParty)
        {
            try
            {
                var home = villagerParty?.HomeSettlement;
                return home?.Village == null || (!QuarantineRules.Closed(home) && !QuarantineRules.Closed(home.Village.TradeBound));
            }
            catch { return true; }
        }
    }

    /// <summary>No new tournaments in a quarantined town. One already running carries on.</summary>
    [HarmonyPatch(typeof(TournamentCampaignBehavior), "ConsiderStartOrEndTournament")]
    internal static class QuarantineTournamentPatch
    {
        private static bool Prefix(Town town)
        {
            try
            {
                if (town == null || !QuarantineRules.Closed(town.Settlement)) return true;
                return Campaign.Current.TournamentManager.GetTournamentGame(town) != null;
            }
            catch { return true; }
        }
    }

    /// <summary>A banished clan can't rejoin the kingdom that expelled it until it's pardoned.</summary>
    [HarmonyPatch(typeof(ChangeKingdomAction))]
    internal static class BanishedReturnPatch
    {
        [HarmonyPrefix, HarmonyPatch(nameof(ChangeKingdomAction.ApplyByJoinToKingdom))]
        private static bool Join(Clan clan, Kingdom newKingdom) => Banished.AllowJoin(clan, newKingdom);

        [HarmonyPrefix, HarmonyPatch(nameof(ChangeKingdomAction.ApplyByJoinToKingdomByDefection))]
        private static bool Defect(Clan clan, Kingdom newKingdom) => Banished.AllowJoin(clan, newKingdom);

        [HarmonyPrefix, HarmonyPatch(nameof(ChangeKingdomAction.ApplyByJoinFactionAsMercenary))]
        private static bool Mercenary(Clan clan, Kingdom newKingdom) => Banished.AllowJoin(clan, newKingdom);
    }

    /// <summary>An expulsion voted through the game's own kingdom decision also counts as a banishment.</summary>
    [HarmonyPatch(typeof(ExpelClanFromKingdomDecision), nameof(ExpelClanFromKingdomDecision.ApplyChosenOutcome))]
    internal static class BanishedByVotePatch
    {
        private static void Prefix(ExpelClanFromKingdomDecision __instance, out Kingdom __state)
        {
            __state = null;
            try { __state = __instance?.ClanToExpel?.Kingdom; } catch { }
        }

        private static void Postfix(ExpelClanFromKingdomDecision __instance, Kingdom __state)
        {
            try
            {
                var clan = __instance?.ClanToExpel;
                if (__state != null && clan != null && clan.Kingdom != __state) Banished.Ban(clan, __state);
            }
            catch (Exception ex) { Log.Error("Banishment by vote", ex); }
        }
    }

    /// <summary>
    /// A law of the realm that forbids expelling clans (World/Laws.cs) also holds against the game's own vote: lords
    /// other than the player may not put an expulsion to the council while it stands.
    /// </summary>
    [HarmonyPatch(typeof(ExpelClanFromKingdomDecision), nameof(ExpelClanFromKingdomDecision.IsAllowed))]
    internal static class ExpelLawAllowedPatch
    {
        private static void Postfix(ExpelClanFromKingdomDecision __instance, ref bool __result)
        {
            try
            {
                if (!__result || __instance == null || __instance.ProposerClan == Clan.PlayerClan) return;
                if (Laws.Forbidding(__instance.OldKingdom ?? __instance.ClanToExpel?.Kingdom, Laws.ExpelClan) != null) __result = false;
            }
            catch (Exception ex) { Log.Error("Expulsion law: proposal", ex); }
        }
    }

    /// <summary>
    /// ...and an expulsion vote an AI lord had already started, or that a new law came to forbid while it ran, is not
    /// carried out. The player's own proposals are left to the vote.
    /// </summary>
    [HarmonyPatch(typeof(ExpelClanFromKingdomDecision), nameof(ExpelClanFromKingdomDecision.ApplyChosenOutcome))]
    internal static class ExpelLawOutcomePatch
    {
        private static bool Prefix(ExpelClanFromKingdomDecision __instance, DecisionOutcome chosenOutcome)
        {
            try
            {
                if (__instance == null || __instance.ProposerClan == Clan.PlayerClan) return true;
                if (!(chosenOutcome is ExpelClanFromKingdomDecision.ExpelClanDecisionOutcome o) || !o.ShouldBeExpelled) return true;
                var law = Laws.Forbidding(__instance.OldKingdom ?? __instance.ClanToExpel?.Kingdom, Laws.ExpelClan);
                if (law == null) return true;
                Log.Info($"[laws] the vote to expel {__instance.ClanToExpel?.Name} from {__instance.OldKingdom?.Name} is not carried out: the law «{law.Name}» forbids expelling clans");
                return false;
            }
            catch (Exception ex) { Log.Error("Expulsion law: outcome", ex); return true; }
        }
    }

    /// <summary>
    /// After a while the game disbands a clan with no kingdom and no fiefs, and kills its members. Skip that for a banished
    /// clan (it can still be pardoned) and for clans with someone the player has talked to.
    /// </summary>
    [HarmonyPatch(typeof(FactionDiscontinuationCampaignBehavior), "DiscontinueClan")]
    internal static class KeepClanPatch
    {
        private static bool Prefix(Clan clan)
        {
            try { return !Banished.KeepAlive(clan); } catch { return true; }
        }
    }

    /// <summary>A couple where one spouse has no clan can't conceive through the vanilla path (the child would be born clanless).</summary>
    [HarmonyPatch(typeof(PregnancyCampaignBehavior), "RefreshSpouseVisit")]
    internal static class ClanlessCouplePatch
    {
        private static bool Prefix(Hero hero)
        {
            try
            {
                var sp = hero?.Spouse;
                if (sp == null || hero == Hero.MainHero || sp == Hero.MainHero) return true;
                return hero.Clan != null && sp.Clan != null;
            }
            catch { return true; }
        }
    }
}
