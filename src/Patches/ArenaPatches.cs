using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using AILegacy.Core;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Localization;

namespace AILegacy.Patches
{
    /// <summary>
    /// Arena training paid by a clan, as AIInfluence's ArenaTrainingExpensePatch: when a lord of the AI who is not the
    /// head of his clan cannot pay the arena from his own purse, the rest is a debt of his clan, charged with the clan's
    /// next daily balance and shown in its breakdown as "Arena troop training". Kept for the session only, as theirs.
    /// </summary>
    [HarmonyPatch(typeof(DefaultClanFinanceModel), nameof(DefaultClanFinanceModel.CalculateClanGoldChange))]
    internal static class ArenaExpensePatch
    {
        private static readonly Dictionary<string, int> Debts = new Dictionary<string, int>();
        private static Campaign _campaign;

        private static void Fresh()
        {
            if (Campaign.Current == _campaign) return;
            _campaign = Campaign.Current;
            Debts.Clear();
        }

        public static void AddDebt(Clan clan, int amount)
        {
            if (clan == null || amount <= 0) return;
            Fresh();
            Debts.TryGetValue(clan.StringId, out int was);
            Debts[clan.StringId] = (int)Math.Min(int.MaxValue, (long)was + amount);
        }

        private static void Postfix(Clan clan, bool includeDescriptions, bool applyWithdrawals, ref ExplainedNumber __result)
        {
            try
            {
                if (clan == null || Campaign.Current == null || Debts.Count == 0) return;
                Fresh();
                if (!Debts.TryGetValue(clan.StringId, out int owed) || owed <= 0) return;
                if (includeDescriptions) __result.Add(-owed, new TextObject("{=!}" + L.T("AIL_e7b1db1d", "Entrenamiento de tropas en la arena")));
                else __result.Add(-owed);
                // the daily balance that is really paid settles it
                if (applyWithdrawals) Debts.Remove(clan.StringId);
            }
            catch (Exception ex) { Log.Error("Arena training in the clan's balance", ex); }
        }
    }

    /// <summary>
    /// "Trains troops" on the town's nameplate on the map, next to the face of whoever is training there (the player, a
    /// companion or a lord of the AI), like the game's own "Solved an issue" notes. AIInfluence shows the same. The
    /// game's nameplate classes are reached by name: one of its own notes is made and given our text.
    /// </summary>
    internal static class ArenaNotice
    {
        private const string VmName = "SandBox.ViewModelCollection.Nameplate.NameplateNotifications.SettlementNotificationTypes.SettlementNameplateNotificationsVM";
        private const string ItemName = "SandBox.ViewModelCollection.Nameplate.NameplateNotifications.SettlementNotificationTypes.IssueSolvedByLordNotificationItemVM";
        private const string BaseName = "SandBox.ViewModelCollection.Nameplate.NameplateNotifications.SettlementNotificationItemBaseVM";

        internal static readonly Dictionary<Settlement, object> Live = new Dictionary<Settlement, object>();
        private static bool _ready, _broken;
        private static Type _vm, _item, _base;
        private static FieldInfo _settlement, _tick;
        private static MethodInfo _remove;
        private static PropertyInfo _list, _text;

        internal static Type FindType(string full)
        {
            var t = AccessTools.TypeByName(full);
            if (t != null) return t;
            int dot = full.LastIndexOf('.');
            return dot > 0 ? AccessTools.TypeByName(full.Substring(dot + 1)) : null;
        }

        internal static Type VmType => _vm ?? (_vm = FindType(VmName));

        private static bool Ready()
        {
            if (_ready || _broken) return _ready;
            try
            {
                _item = FindType(ItemName); _base = FindType(BaseName);
                if (VmType != null)
                {
                    _settlement = AccessTools.Field(_vm, "_settlement");
                    _tick = AccessTools.Field(_vm, "_tickSinceEnabled");
                    _remove = AccessTools.Method(_vm, "RemoveItem");
                    _list = AccessTools.Property(_vm, "Notifications");
                }
                _text = _base != null ? AccessTools.Property(_base, "Text") : null;
                _ready = _item != null && _base != null && _settlement != null && _remove != null && _list != null && _text != null;
                if (!_ready) { _broken = true; Log.Warn("Arena: the game's settlement nameplate notes were not found; no \"trains troops\" note on the map"); }
            }
            catch (Exception ex) { _broken = true; Log.Error("Arena: settlement nameplate notes", ex); }
            return _ready;
        }

        internal static Settlement SettlementOf(object vm)
        {
            try { return Ready() ? _settlement.GetValue(vm) as Settlement : null; } catch { return null; }
        }

        /// <summary>The note on the nameplate of that town, if the map has it on screen. Never throws.</summary>
        public static void Show(Settlement st, Hero hero)
        {
            try
            {
                if (st == null || hero == null || !Ready() || !Live.TryGetValue(st, out var vm) || vm == null) return;
                int tick = _tick != null && _tick.GetValue(vm) is int t ? t : 0;
                var onRemove = Delegate.CreateDelegate(typeof(Action<>).MakeGenericType(_base), vm, _remove);
                var item = Activator.CreateInstance(_item, onRemove, hero, tick);
                _text.SetValue(item, L.T("AIL_e7b1db1c", "Entrena a sus tropas"));
                (_list.GetValue(vm) as IList)?.Add(item);
            }
            catch (Exception ex)
            {
                if (_broken) return;
                _broken = true; _ready = false;
                Log.Error("Arena: note on the town's nameplate", ex);
            }
        }
    }

    /// <summary>Keeps track of the nameplate note lists the map has on screen, by settlement.</summary>
    [HarmonyPatch]
    internal static class ArenaNoticeRegisterPatch
    {
        private static bool Prepare() { try { return TargetMethod() != null; } catch { return false; } }
        private static MethodBase TargetMethod() => ArenaNotice.VmType == null ? null : AccessTools.Method(ArenaNotice.VmType, "RegisterEvents");

        private static void Postfix(object __instance)
        {
            try { var st = ArenaNotice.SettlementOf(__instance); if (st != null) ArenaNotice.Live[st] = __instance; }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class ArenaNoticeUnloadPatch
    {
        private static bool Prepare() { try { return TargetMethod() != null; } catch { return false; } }
        private static MethodBase TargetMethod() => ArenaNotice.VmType == null ? null : AccessTools.Method(ArenaNotice.VmType, "UnloadEvents");

        private static void Postfix(object __instance)
        {
            try
            {
                var st = ArenaNotice.SettlementOf(__instance);
                if (st != null && ArenaNotice.Live.TryGetValue(st, out var vm) && ReferenceEquals(vm, __instance)) ArenaNotice.Live.Remove(st);
            }
            catch { }
        }
    }
}
