using System;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.Patches
{
    /// <summary>
    /// With AI Legacy diplomacy on, vanilla clans stop proposing wars, peace, trade agreements and alliances on their own.
    /// Rulers decide those through their declarations now. Vanilla policies and annexations still work as usual.
    /// </summary>
    [HarmonyPatch]
    internal static class VanillaDiplomacyPatch
    {
        private static readonly string[] Methods = { "GetRandomWarDecision", "GetRandomPeaceDecision", "GetRandomTradeAgreementDecision", "GetRandomStartingAllianceDecision" };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            var t = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.KingdomDecisionProposalBehavior");
            if (t == null) yield break;
            foreach (var name in Methods)
            {
                var m = AccessTools.Method(t, name);
                if (m != null) yield return m;
                else Log.Warn("Diplomacy: cannot find " + name + " in this game version");
            }
        }

        private static bool Prepare()
        {
            var t = AccessTools.TypeByName("TaleWorlds.CampaignSystem.CampaignBehaviors.KingdomDecisionProposalBehavior");
            return t != null && AccessTools.Method(t, Methods[0]) != null;
        }

        private static bool Prefix(ref TaleWorlds.CampaignSystem.Election.KingdomDecision __result)
        {
            try
            {
                if (!Diplomacy.Enabled) return true;
                __result = null;      // no proposal
                return false;
            }
            catch (Exception ex) { Log.Error("Blocking vanilla diplomacy", ex); return true; }
        }
    }

    /// <summary>
    /// With AI Legacy diplomacy on, wars and peace between two AI kingdoms are decided by their rulers' declarations.
    /// This stops the game's own decisions, call-to-war agreements and other mods from making or ending such wars on
    /// their own. Anything that involves the player's kingdom, and wars that start from what happens in the world
    /// (an attack, a rebellion, a new kingdom, a claim to a throne, a quest), go through untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class ForeignWarPeacePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            var w = AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Actions.DeclareWarAction), "ApplyInternal");
            var p = AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Actions.MakePeaceAction), "ApplyInternal");
            if (w != null) yield return w; else Log.Warn("Diplomacy: cannot find DeclareWarAction.ApplyInternal in this game version");
            if (p != null) yield return p; else Log.Warn("Diplomacy: cannot find MakePeaceAction.ApplyInternal in this game version");
        }

        private static bool Prepare() =>
            AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Actions.DeclareWarAction), "ApplyInternal") != null
            && AccessTools.Method(typeof(TaleWorlds.CampaignSystem.Actions.MakePeaceAction), "ApplyInternal") != null;

        private static bool Prefix(IFaction faction1, IFaction faction2, object[] __args, MethodBase __originalMethod)
        {
            try
            {
                if (!Diplomacy.Enabled || Diplomacy.IsOurs || !AILegacy.Settings.S.Get.BlockForeignWarPeace) return true;
                if (!(faction1 is Kingdom a) || !(faction2 is Kingdom b)) return true;
                var mine = Hero.MainHero?.MapFaction;
                if (a == mine || b == mine) return true;
                if (__originalMethod.DeclaringType == typeof(TaleWorlds.CampaignSystem.Actions.DeclareWarAction))
                {
                    var detail = __args.Length > 2 && __args[2] is TaleWorlds.CampaignSystem.Actions.DeclareWarAction.DeclareWarDetail d ? d : TaleWorlds.CampaignSystem.Actions.DeclareWarAction.DeclareWarDetail.Default;
                    bool political = detail == TaleWorlds.CampaignSystem.Actions.DeclareWarAction.DeclareWarDetail.Default || detail == TaleWorlds.CampaignSystem.Actions.DeclareWarAction.DeclareWarDetail.CausedByKingdomDecision
                                     || detail == TaleWorlds.CampaignSystem.Actions.DeclareWarAction.DeclareWarDetail.CausedByCallToWarAgreement;
                    if (!political) return true;
                    Log.Info($"[diplomacy] war of {a.Name} against {b.Name} ({detail}) stopped: their rulers decide");
                }
                else Log.Info($"[diplomacy] peace between {a.Name} and {b.Name} stopped: their rulers decide");
                return false;
            }
            catch (Exception ex) { Log.Error("Blocking other factions' war and peace", ex); return true; }
        }
    }

    /// <summary>
    /// Same rule as wars and peace for pacts between two AI kingdoms: the game renews an expired alliance by putting it
    /// to the vote (AddAllianceDecision), and the vote, a call to war or another mod can seal an alliance or a trade
    /// agreement. With AI Legacy diplomacy on, only the rulers' declarations do. An expired alliance simply ends, and
    /// the rulers may propose it again. Anything that involves the player's kingdom goes through untouched.
    /// </summary>
    [HarmonyPatch]
    internal static class ForeignPactPatch
    {
        private static readonly string[][] Targets =
        {
            new[] { "TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior", "AddAllianceDecision" },
            new[] { "TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior", "StartAlliance" },
            new[] { "TaleWorlds.CampaignSystem.CampaignBehaviors.TradeAgreementsCampaignBehavior", "MakeTradeAgreement" },
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var target in Targets)
            {
                var t = AccessTools.TypeByName(target[0]);
                var m = t != null ? AccessTools.Method(t, target[1]) : null;
                if (m != null) yield return m;
                else Log.Warn("Diplomacy: cannot find " + target[1] + " in this game version");
            }
        }

        private static bool Prepare()
        {
            foreach (var target in Targets)
            {
                var t = AccessTools.TypeByName(target[0]);
                if (t != null && AccessTools.Method(t, target[1]) != null) return true;
            }
            return false;
        }

        private static bool Prefix(object[] __args, MethodBase __originalMethod)
        {
            try
            {
                if (!Diplomacy.Enabled || Diplomacy.IsOurs || !AILegacy.Settings.S.Get.BlockForeignWarPeace) return true;
                if (__args == null || __args.Length < 2 || !(__args[0] is Kingdom a) || !(__args[1] is Kingdom b)) return true;
                var mine = Hero.MainHero?.MapFaction;
                if (mine != null && (a == mine || b == mine)) return true;
                Log.Info($"[diplomacy] {__originalMethod.Name} between {a.Name} and {b.Name} stopped: their rulers decide");
                return false;
            }
            catch (Exception ex) { Log.Error("Blocking other factions' pacts", ex); return true; }
        }
    }

    /// <summary>
    /// The game's alliance and trade code reads the ruler and the ruling clan of both kingdoms without checking
    /// them. With a kingdom that is destroyed or has no ruler at that moment it throws inside the campaign tick:
    /// when an alliance runs out and its renewal is put to the vote, when one is sealed, and when the penalty for
    /// breaking a pact by war is applied. Those calls are skipped; everything else runs as the game wrote it.
    /// </summary>
    [HarmonyPatch]
    internal static class PactGuardPatch
    {
        private const string Alliances = "TaleWorlds.CampaignSystem.CampaignBehaviors.AllianceCampaignBehavior";
        private const string Trade = "TaleWorlds.CampaignSystem.CampaignBehaviors.TradeAgreementsCampaignBehavior";
        private static readonly string[][] Targets =
        {
            new[] { Alliances, "AddAllianceDecision" }, new[] { Alliances, "StartAlliance" },
            new[] { Alliances, "ApplyBrokenAlliancePenalty" }, new[] { Trade, "ApplyBrokenTradeAgreementPenalty" },
        };

        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var target in Targets)
            {
                var t = AccessTools.TypeByName(target[0]);
                var m = t != null ? AccessTools.Method(t, target[1]) : null;
                if (m != null) yield return m;
                else Log.Warn("Diplomacy: cannot find " + target[1] + " in this game version");
            }
        }

        private static bool Prepare()
        {
            foreach (var target in Targets)
            {
                var t = AccessTools.TypeByName(target[0]);
                if (t != null && AccessTools.Method(t, target[1]) != null) return true;
            }
            return false;
        }

        private static bool Prefix(object[] __args, MethodBase __originalMethod)
        {
            try
            {
                if (__args == null) return true;
                foreach (var arg in __args)
                {
                    if (!(arg is Kingdom k)) continue;
                    if (!k.IsEliminated && k.Leader != null && k.RulingClan != null) continue;
                    Log.Info($"[diplomacy] {__originalMethod.Name} with {k.Name}, destroyed or without a ruler: not executed");
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Log.Error("Alliance guard", ex); return true; }
        }
    }

    /// <summary>The game won't marry off someone who is in a romance with the player.</summary>
    [HarmonyPatch(typeof(DefaultMarriageModel), nameof(DefaultMarriageModel.IsCoupleSuitableForMarriage))]
    internal static class CourtshipGuardPatch
    {
        private static void Postfix(Hero firstHero, Hero secondHero, ref bool __result)
        {
            try
            {
                if (!__result || firstHero == null || secondHero == null) return;
                // a concubine of the player is married to nobody else meanwhile (to the player, yes)
                if ((AILegacy.World.Concubines.Is(firstHero) && secondHero != AILegacy.World.Concubines.PartnerOf(firstHero)) || (AILegacy.World.Concubines.Is(secondHero) && firstHero != AILegacy.World.Concubines.PartnerOf(secondHero))) { __result = false; return; }
                if (firstHero == Hero.MainHero || secondHero == Hero.MainHero) return;
                int min = AILegacy.Settings.S.Get.RomanceProtectsFromMarriage;
                if (min <= 0) return;
                var a = AILegacy.Memory.MemoryStore.Find(firstHero.StringId); var b = AILegacy.Memory.MemoryStore.Find(secondHero.StringId);
                if ((a != null && a.Romance >= min) || (b != null && b.Romance >= min)) __result = false;
            }
            catch { }
        }
    }

    /// <summary>World events raise or lower a settlement's market prices while they last.</summary>
    [HarmonyPatch(typeof(DefaultTradeItemPriceFactorModel), nameof(DefaultTradeItemPriceFactorModel.GetPrice))]
    internal static class MarketPricePatch
    {
        private static void Postfix(TaleWorlds.Core.EquipmentElement itemRosterElement, TaleWorlds.CampaignSystem.Party.PartyBase merchant, ref int __result)
        {
            try
            {
                if (WorldSim.State.Effects.Count == 0 || merchant?.Settlement == null) return;
                float f = WorldSim.PriceFactor(merchant.Settlement, itemRosterElement.Item);
                if (Math.Abs(f) >= 0.005f) __result = Math.Max(1, (int)Math.Round(__result * (1f + f)));
            }
            catch (Exception ex) { Log.Error("Event prices", ex); }
        }
    }

    /// <summary>A clan's reputation from world events affects its income.</summary>
    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanIncome))]
    internal static class ClanIncomePatch
    {
        private static void Postfix(Clan clan, ref ExplainedNumber __result)
        {
            try
            {
                if (clan == null || WorldSim.State.Effects.Count == 0) return;
                float f = WorldSim.ClanIncomeFactor(clan, out string reason);
                if (Math.Abs(f) < 0.005f) return;
                __result.AddFactor(f, new TaleWorlds.Localization.TextObject("{=!}" + (string.IsNullOrWhiteSpace(reason) ? L.T("AIL_94517b9a", "El nombre del clan (sucesos del mundo)") : reason)));
            }
            catch (Exception ex) { Log.Error("Clan income from events", ex); }
        }
    }

    /// <summary>
    /// A ruler's set_tax action raises or lowers tax income in their towns, and world events can hurt or help a town's
    /// trade. Each one is a line of the game's own tax breakdown, so the town's tooltip says why.
    /// </summary>
    [HarmonyPatch(typeof(DefaultSettlementTaxModel), nameof(DefaultSettlementTaxModel.CalculateTownTax))]
    internal static class KingdomTaxPatch
    {
        private static int _errors;

        private static void Postfix(Town town, ref ExplainedNumber __result)
        {
            try
            {
                if (town == null || (WorldSim.State.KingdomTaxPercent.Count == 0 && WorldSim.State.Effects.Count == 0)) return;
                foreach (var (factor, label) in WorldSim.TaxModifiers(town))
                    if (Math.Abs(factor) >= 0.005f) __result.AddFactor(factor, new TaleWorlds.Localization.TextObject("{=!}" + label));
            }
            catch (Exception ex) { if (_errors++ < 5) Log.Error("Crown tax", ex); }
        }
    }
}
