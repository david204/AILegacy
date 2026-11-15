using System;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;

namespace AILegacy.Patches
{
    /// <summary>
    /// Nobles staying in a town spend some hours of the day at the arena. The game asks this model where each hero of
    /// the settlement stands every time the player enters it or comes out of a scene, so answering "arena" for a few
    /// of those it would send to the keep is enough: the game moves them and spawns them there itself.
    /// They are visitors of the arena scene. The practice fights are not touched: those spawn their fighters from a
    /// troop list with wooden weapons, and putting a hero in that list could get him killed or spawned twice.
    /// </summary>
    [HarmonyPatch(typeof(DefaultHeroAgentLocationModel), nameof(DefaultHeroAgentLocationModel.GetLocationForHero))]
    internal static class ArenaLordsPatch
    {
        private static readonly Tpl TAtArena = new Tpl("scene/arena_training.txt",
            "at the town's arena, where you came for a few hours to practise with your weapons and watch the fighters");

        private static void Postfix(Hero hero, Settlement settlement, ref Location __result, HeroAgentLocationModel.HeroLocationDetail heroLocationDetail)
        {
            try
            {
                if (__result == null || hero == null || settlement == null || !settlement.IsTown || __result.StringId != "lordshall") return;
                if (heroLocationDetail != HeroAgentLocationModel.HeroLocationDetail.NobleBelongingToNoParty
                    && heroLocationDetail != HeroAgentLocationModel.HeroLocationDetail.PartyLeader) return;
                if (!TrainsNow(hero, settlement)) return;
                var arena = settlement.LocationComplex?.GetLocationWithId("arena");
                if (arena != null) __result = arena;
            }
            catch (Exception ex) { Log.Error("Señores en la arena", ex); }
        }

        /// <summary>The same answer all day for the same lord: some go in the morning, some in the afternoon, most not at all.</summary>
        internal static bool TrainsNow(Hero hero, Settlement settlement)
        {
            var cfg = S.Get;
            if (!cfg.LordsTrainInArena || cfg.LordsInArenaChancePercent <= 0) return false;
            if (!hero.IsLord || hero.IsChild || hero.IsWounded || hero.IsPrisoner || hero.Age > 65f || hero.Clan == Clan.PlayerClan) return false;
            if (hero.GovernorOf != null || settlement.Town == null || settlement.Town.HasTournament || settlement.IsUnderSiege) return false;
            int day = (int)CampaignTime.Now.ToDays;
            uint h = 2166136261u;
            foreach (char ch in hero.StringId + ":" + day) h = (h ^ ch) * 16777619u;
            if (h % 100u >= (uint)cfg.LordsInArenaChancePercent) return false;
            int hour = CampaignTime.Now.GetHourOfDay;
            bool morning = ((h >> 8) & 1u) == 0u;
            return morning ? hour >= 7 && hour < 12 : hour >= 14 && hour < 19;
        }

        /// <summary>For the prompt: the lord the player is facing is at the arena right now.</summary>
        internal static string Describe(Hero h)
        {
            try
            {
                if (h == null || !S.Get.LordsTrainInArena || CampaignMission.Current?.Location?.StringId != "arena") return "";
                var where = LocationComplex.Current?.GetLocationOfCharacter(h);
                return where != null && where.StringId == "arena" && h.IsLord ? TAtArena.Text : "";
            }
            catch { return ""; }
        }
    }
}
