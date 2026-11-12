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
    // ================================================================ cuarentena

    /// <summary>Las caravanas no eligen como destino una ciudad en cuarentena.</summary>
    [HarmonyPatch(typeof(CaravansCampaignBehavior), "GetTradeScoreForTown")]
    internal static class QuarantineCaravanPatch
    {
        private static void Postfix(MobileParty caravanParty, Town town, ref float __result)
        {
            try { if (__result > 0f && town != null && QuarantineRules.Barred(caravanParty, town.Settlement)) __result = 0f; }
            catch (Exception ex) { Log.Error("[cuarentena] caravanas", ex); }
        }
    }

    /// <summary>Los aldeanos no salen a vender si su aldea o su ciudad de mercado están en cuarentena.</summary>
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

    /// <summary>En una ciudad en cuarentena no se convoca torneo (el que ya esté en marcha sigue su curso).</summary>
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

    // ================================================================ destierro

    /// <summary>Un clan desterrado no vuelve al reino que lo echó hasta ser perdonado.</summary>
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

    /// <summary>La expulsión votada en el propio juego también es un destierro.</summary>
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
            catch (Exception ex) { Log.Error("Destierro por votación", ex); }
        }
    }

    // ================================================================ guardas del juego

    /// <summary>
    /// El juego disuelve (y mata a sus miembros) a un clan sin reino ni tierras al cabo de un tiempo. No lo hace con un clan
    /// desterrado, que puede ser perdonado, ni con uno donde hay gente con la que has tratado.
    /// </summary>
    [HarmonyPatch(typeof(FactionDiscontinuationCampaignBehavior), "DiscontinueClan")]
    internal static class KeepClanPatch
    {
        private static bool Prefix(Clan clan)
        {
            try { return !Banished.KeepAlive(clan); } catch { return true; }
        }
    }

    /// <summary>Una pareja en la que uno de los dos no pertenece a ningún clan no concibe por la vía del juego (el hijo nacería sin casa).</summary>
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
