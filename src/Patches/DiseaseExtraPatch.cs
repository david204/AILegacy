using System;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using AILegacy.World;
using HarmonyLib;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.LogEntries;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.SceneInformationPopupTypes;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.ViewModelCollection;
using TaleWorlds.Core.ViewModelCollection.Information;
using TaleWorlds.Localization;

namespace AILegacy.Patches
{
    /// <summary>
    /// Disease lines for the map tooltips of parties and settlements. The game ends those tooltips with its key hints,
    /// so the lines are worked out before it builds the tooltip and written right before the hints; if the game never
    /// gets to the hints they go at the end.
    /// </summary>
    internal static class DiseaseTips
    {
        [ThreadStatic] private static List<TipLine> _pending;

        public static void Hold(List<TipLine> lines) => _pending = lines;

        public static void Flush(PropertyBasedTooltipVM vm)
        {
            var lines = _pending;
            _pending = null;
            if (lines == null || vm == null) return;
            foreach (var l in lines)
            {
                if (l.Kind == TipLine.Space) vm.AddProperty(string.Empty, string.Empty, -1);
                else if (l.Kind == TipLine.Rule) vm.AddProperty("", "", 0, TooltipProperty.TooltipPropertyFlags.RundownSeperator);
                else vm.AddProperty(l.Label ?? "", l.Value ?? "");
            }
        }
    }

    [HarmonyPatch(typeof(TooltipRefresherCollection), nameof(TooltipRefresherCollection.RefreshMobilePartyTooltip))]
    internal static class DiseasePartyTooltipPatch
    {
        private static void Prefix(PropertyBasedTooltipVM propertyBasedTooltipVM, object[] args)
        {
            try
            {
                DiseaseTips.Hold(null);
                if (!Diseases.AnySick || propertyBasedTooltipVM == null || args == null || args.Length == 0) return;
                if (args[0] is MobileParty party) DiseaseTips.Hold(Diseases.PartyTip(party, propertyBasedTooltipVM.IsExtended));
            }
            catch (Exception ex) { DiseaseEffects.Report("tooltip de partida", ex); }
        }

        private static void Finalizer(PropertyBasedTooltipVM propertyBasedTooltipVM)
        {
            try { DiseaseTips.Flush(propertyBasedTooltipVM); }
            catch (Exception ex) { DiseaseEffects.Report("tooltip de partida", ex); }
        }
    }

    [HarmonyPatch(typeof(TooltipRefresherCollection), nameof(TooltipRefresherCollection.RefreshSettlementTooltip))]
    internal static class DiseaseSettlementTooltipPatch
    {
        private static void Prefix(PropertyBasedTooltipVM propertyBasedTooltipVM, object[] args)
        {
            try
            {
                DiseaseTips.Hold(null);
                if ((!Diseases.AnyOutbreak && !Diseases.AnyForces) || propertyBasedTooltipVM == null || args == null || args.Length == 0) return;
                if (args[0] is Settlement st) DiseaseTips.Hold(Diseases.SettlementTip(st));
            }
            catch (Exception ex) { DiseaseEffects.Report("tooltip de asentamiento", ex); }
        }

        private static void Finalizer(PropertyBasedTooltipVM propertyBasedTooltipVM)
        {
            try { DiseaseTips.Flush(propertyBasedTooltipVM); }
            catch (Exception ex) { DiseaseEffects.Report("tooltip de asentamiento", ex); }
        }
    }

    [HarmonyPatch(typeof(TooltipRefresherCollection), nameof(TooltipRefresherCollection.AddTooltipHintProperties))]
    internal static class DiseaseTooltipHintPatch
    {
        private static void Prefix(PropertyBasedTooltipVM propertyBasedTooltipVM)
        {
            try { DiseaseTips.Flush(propertyBasedTooltipVM); }
            catch (Exception ex) { DiseaseEffects.Report("tooltip", ex); }
        }
    }

    /// <summary>Militia dying of the outbreak goes into the game's own daily militia change, with the disease as the reason.</summary>
    [HarmonyPatch(typeof(DefaultSettlementMilitiaModel), nameof(DefaultSettlementMilitiaModel.CalculateMilitiaChange))]
    internal static class DiseaseMilitiaPatch
    {
        private static void Postfix(Settlement settlement, ref ExplainedNumber __result)
        {
            try
            {
                Diseases.MilitiaModelSeen();
                if (!Diseases.AnyForces) return;
                float loss = Diseases.MilitiaLoss(settlement, out string name);
                if (loss > 0.005f) __result.Add(-loss, DiseaseEffects.Reason(name));
            }
            catch (Exception ex) { DiseaseEffects.Report("milicia", ex); }
        }
    }

    /// <summary>Treatment the clan owes: a line of the daily gold change, cleared when the game charges the day.</summary>
    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanGoldChange))]
    internal static class DiseaseDebtPatch
    {
        private static void Postfix(Clan clan, bool applyWithdrawals, ref ExplainedNumber __result)
        {
            try
            {
                int debt = Diseases.DebtOf(clan);
                if (debt <= 0) return;
                __result.Add(-debt, new TextObject("{=!}" + Diseases.DebtLabel));
                if (applyWithdrawals) Diseases.ClearDebt(clan);
            }
            catch (Exception ex) { DiseaseEffects.Report("deuda de tratamiento", ex); }
        }
    }

    /// <summary>The clan screen adds up the expenses with this other calculation: the same line, never cleared here.</summary>
    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanExpenses))]
    internal static class DiseaseDebtExpensesPatch
    {
        private static void Postfix(Clan clan, ref ExplainedNumber __result)
        {
            try
            {
                int debt = Diseases.DebtOf(clan);
                if (debt > 0) __result.Add(-debt, new TextObject("{=!}" + Diseases.DebtLabel));
            }
            catch (Exception ex) { DiseaseEffects.Report("deuda de tratamiento", ex); }
        }
    }

    // A hero killed by a disease dies through the game's old-age death. These four put the disease in its texts,
    // and only for that kind of death: someone the disease was about to kill who falls in battle keeps the game's.

    internal static class DiseaseDeathDetail
    {
        private static readonly FieldInfo Detail = AccessTools.Field(typeof(CharacterKilledLogEntry), "_actionDetail");

        /// <summary>How the game recorded this death, or null if the field is not there in this version (then only the record decides).</summary>
        public static KillCharacterAction.KillCharacterActionDetail? Of(CharacterKilledLogEntry entry)
        {
            try { return Detail != null && entry != null ? (KillCharacterAction.KillCharacterActionDetail?)(KillCharacterAction.KillCharacterActionDetail)Detail.GetValue(entry) : null; }
            catch { return null; }
        }
    }

    [HarmonyPatch(typeof(KillCharacterAction), "CreateObituary")]
    internal static class DiseaseObituaryPatch
    {
        private static void Postfix(Hero hero, KillCharacterAction.KillCharacterActionDetail detail, ref TextObject __result)
        {
            try
            {
                string disease = Diseases.DiedOf(hero, detail);
                if (disease == null || __result == null) return;
                var details = new TextObject("{=!}" + L.T("AIL_e7b1c726", "Murió de {DISEASE} en el año {YEAR}, a los {CHARACTER.AGE} años. Tenía fama de {REPUTATION}"));
                StringHelpers.SetCharacterProperties("CHARACTER", hero.CharacterObject, details, true);
                details.SetTextVariable("DISEASE", disease);
                details.SetTextVariable("REPUTATION", CharacterHelper.GetReputationDescription(hero.CharacterObject));
                details.SetTextVariable("YEAR", CampaignTime.Now.GetYear.ToString());
                __result.SetTextVariable("FURTHER_DETAILS", details);
            }
            catch (Exception ex) { DiseaseEffects.Report("obituario", ex); }
        }
    }

    [HarmonyPatch(typeof(CharacterKilledLogEntry), nameof(CharacterKilledLogEntry.GetNotificationText))]
    internal static class DiseaseDeathNoticePatch
    {
        private static void Postfix(CharacterKilledLogEntry __instance, ref TextObject __result)
        {
            try
            {
                var victim = __instance?.Victim;
                string disease = Diseases.DiedOf(victim, DiseaseDeathDetail.Of(__instance));
                if (disease == null) return;
                var text = new TextObject("{=!}" + L.T("AIL_e7b1c724", "{VICTIM.NAME} ha muerto de {DISEASE}. Su familia y sus amigos siempre {?VICTIM.GENDER}la{?}lo{\\?} recordarán."));
                StringHelpers.SetCharacterProperties("VICTIM", victim.CharacterObject, text);
                text.SetTextVariable("DISEASE", disease);
                __result = text;
            }
            catch (Exception ex) { DiseaseEffects.Report("aviso de muerte", ex); }
        }
    }

    [HarmonyPatch(typeof(CharacterKilledLogEntry), nameof(CharacterKilledLogEntry.GetEncyclopediaText))]
    internal static class DiseaseDeathEncyclopediaPatch
    {
        private static void Postfix(CharacterKilledLogEntry __instance, ref TextObject __result)
        {
            try
            {
                var victim = __instance?.Victim;
                string disease = Diseases.DiedOf(victim, DiseaseDeathDetail.Of(__instance));
                if (disease == null) return;
                var text = new TextObject("{=!}" + L.T("AIL_e7b1c725", "{VICTIM.LINK} murió de {DISEASE}."));
                StringHelpers.SetCharacterProperties("VICTIM", victim.CharacterObject, text);
                text.SetTextVariable("DISEASE", disease);
                __result = text;
            }
            catch (Exception ex) { DiseaseEffects.Report("enciclopedia", ex); }
        }
    }

    /// <summary>The scene shown when someone of the player's own dies of age, the player included (the heir comes after it).</summary>
    [HarmonyPatch(typeof(DeathOldAgeSceneNotificationItem), nameof(DeathOldAgeSceneNotificationItem.TitleText), MethodType.Getter)]
    internal static class DiseaseDeathScenePatch
    {
        private static readonly FieldInfo Created = AccessTools.Field(typeof(DeathOldAgeSceneNotificationItem), "_creationCampaignTime");

        private static void Postfix(DeathOldAgeSceneNotificationItem __instance, ref TextObject __result)
        {
            try
            {
                var dead = __instance?.DeadHero;
                // this scene only exists for a death of old age
                string disease = Diseases.DiedOf(dead, KillCharacterAction.KillCharacterActionDetail.DiedOfOldAge);
                if (disease == null) return;
                var when = CampaignTime.Now;
                try { if (Created != null) when = (CampaignTime)Created.GetValue(__instance); } catch { }
                var text = new TextObject("{=!}" + L.T("AIL_e7b1c727", "En el {DAY_OF_YEAR} día de {YEAR}, {VICTIM.NAME} murió de {DISEASE}."));
                StringHelpers.SetCharacterProperties("VICTIM", dead.CharacterObject, text);
                text.SetTextVariable("DAY_OF_YEAR", CampaignSceneNotificationHelper.GetFormalDayAndSeasonText(when));
                text.SetTextVariable("YEAR", when.GetYear);
                text.SetTextVariable("DISEASE", disease);
                __result = text;
            }
            catch (Exception ex) { DiseaseEffects.Report("escena de muerte", ex); }
        }
    }
}
