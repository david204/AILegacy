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
                else Log.Warn("Diplomacia: no encuentro " + name + " en esta versión del juego");
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
            catch (Exception ex) { Log.Error("Bloqueo de la diplomacia del juego", ex); return true; }
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
            if (w != null) yield return w; else Log.Warn("Diplomacia: no encuentro DeclareWarAction.ApplyInternal en esta versión del juego");
            if (p != null) yield return p; else Log.Warn("Diplomacia: no encuentro MakePeaceAction.ApplyInternal en esta versión del juego");
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
                    Log.Info($"[diplomacia] guerra de {a.Name} contra {b.Name} ({detail}) parada: la deciden sus soberanos");
                }
                else Log.Info($"[diplomacia] paz entre {a.Name} y {b.Name} parada: la deciden sus soberanos");
                return false;
            }
            catch (Exception ex) { Log.Error("Bloqueo de guerra y paz ajenas", ex); return true; }
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
                else Log.Warn("Diplomacia: no encuentro " + target[1] + " en esta versión del juego");
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
                    Log.Info($"[diplomacia] {__originalMethod.Name} con {k.Name}, destruido o sin soberano: no se ejecuta");
                    return false;
                }
                return true;
            }
            catch (Exception ex) { Log.Error("Guarda de alianzas", ex); return true; }
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
                if (!__result || firstHero == null || secondHero == null || firstHero == Hero.MainHero || secondHero == Hero.MainHero) return;
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
            catch (Exception ex) { Log.Error("Precios por sucesos", ex); }
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
            catch (Exception ex) { Log.Error("Ingresos del clan por sucesos", ex); }
        }
    }

    /// <summary>A ruler's set_tax action raises or lowers tax income in their towns.</summary>
    [HarmonyPatch(typeof(DefaultSettlementTaxModel), nameof(DefaultSettlementTaxModel.GetTownTaxRatio))]
    internal static class KingdomTaxPatch
    {
        private static void Postfix(Town town, ref float __result)
        {
            try { if (WorldSim.State.KingdomTaxPercent.Count > 0) __result *= WorldSim.TaxFactor(town); }
            catch (Exception ex) { Log.Error("Impuesto de la corona", ex); }
        }
    }
}
