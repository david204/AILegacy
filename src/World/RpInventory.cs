using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Memory;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.ViewModelCollection.Inventory;
using TaleWorlds.Core;
using TaleWorlds.Core.ViewModelCollection;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace AILegacy.World
{
    /// <summary>
    /// Roleplay items as real items in the inventory, as AIInfluence has them: the letter, contract or seal a character
    /// hands over is there among your goods, with its name, and its contents in the tooltip. Instead of making a game item
    /// at runtime (which a save cannot always find again), each one takes one of the mod's 40 document items, defined in
    /// ailegacy_items.xml, and gives it its name. The list and the inventory are kept matched: a new document comes into the
    /// inventory, one that leaves the inventory (sold, dropped, given away) leaves the list, and one a character takes from
    /// the list leaves the inventory.
    /// </summary>
    internal static class RpInventory
    {
        public const int Slots = 40;
        private const string Prefix = "ailegacy_doc_";
        private static float _since;
        private static readonly Dictionary<int, string> Named = new Dictionary<int, string>();

        // the slots the list held on the last pass: one gone from the list since then was taken from the player
        private static HashSet<int> _last;

        public static void ResetSession() { Named.Clear(); _since = 0f; _last = null; Seen.Clear(); _seenAge = 0f; }

        /// <summary>
        /// Where else the item is: in the player's keeping (a stash, a party of the player's clan) or someone else's (a market,
        /// another party). Only looked at when the main party lost it or a slot is to be reused.
        /// </summary>
        private static readonly Dictionary<ItemObject, (bool mine, bool others)> Seen = new Dictionary<ItemObject, (bool, bool)>();
        private static float _seenAge;

        private static void Elsewhere(ItemObject item, out bool mine, out bool others)
        {
            mine = false; others = false;
            if (item != null && Seen.TryGetValue(item, out var known)) { mine = known.mine; others = known.others; return; }
            try
            {
                foreach (var party in MobileParty.All)
                {
                    if (party == null || party == MobileParty.MainParty || party.ItemRoster == null || party.ItemRoster.GetItemNumber(item) == 0) continue;
                    if (party.ActualClan == Clan.PlayerClan) mine = true; else others = true;
                }
                foreach (var st in Settlement.All)
                {
                    if (st == null) continue;
                    if (st.Stash != null && st.Stash.GetItemNumber(item) > 0) mine = true;
                    if (st.ItemRoster != null && st.ItemRoster.GetItemNumber(item) > 0) others = true;
                }
            }
            catch (Exception ex) { Log.Warn("[documents] looking for a document elsewhere: " + ex.Message); }
            if (item != null) Seen[item] = (mine, others);
        }

        private static ItemObject SlotItem(int slot)
        {
            try { return MBObjectManager.Instance.GetObject<ItemObject>(Prefix + slot.ToString("00")); } catch { return null; }
        }

        public static RpItem Of(ItemObject item)
        {
            if (item?.StringId == null || !item.StringId.StartsWith(Prefix)) return null;
            if (!int.TryParse(item.StringId.Substring(Prefix.Length), out int slot)) return null;
            return Belongings.Items.FirstOrDefault(i => i.Slot == slot);
        }

        private static readonly AccessTools.FieldRef<ItemObject, TextObject> NameField = Field();
        private static AccessTools.FieldRef<ItemObject, TextObject> Field()
        {
            try { return AccessTools.FieldRefAccess<ItemObject, TextObject>("<Name>k__BackingField"); } catch { return null; }
        }

        /// <summary>The document item shows the name of what it stands for.</summary>
        private static void Name(int slot, string name)
        {
            if (Named.TryGetValue(slot, out var n) && n == name) return;
            var item = SlotItem(slot);
            if (item == null) return;
            try
            {
                var t = new TextObject("{=!}" + name);
                if (NameField != null) NameField(item) = t;
                else AccessTools.Property(typeof(ItemObject), "Name")?.SetValue(item, t);
                Named[slot] = name;
            }
            catch (Exception ex) { Log.Warn("[documents] naming the item: " + ex.Message); }
        }

        public static void Tick(float dt)
        {
            if ((_seenAge += dt) > 15f) { _seenAge = 0f; Seen.Clear(); }
            if ((_since += dt) < 1f) return;
            _since = 0f;
            try { Sync(); } catch (Exception ex) { Log.Error("Documents in the inventory", ex); }
        }

        /// <summary>Matches the list of documents with the inventory of the player's party.</summary>
        public static void Sync()
        {
            if (Campaign.Current == null) return;
            var roster = MobileParty.MainParty?.ItemRoster;
            if (roster == null || Hero.MainHero == null || Hero.MainHero.IsPrisoner) return;
            // the inventory or trade screen works on its own copy until it closes: wait for it
            if (TaleWorlds.Core.Game.Current?.GameStateManager?.ActiveState is TaleWorlds.CampaignSystem.GameState.InventoryState) return;
            var used = new HashSet<int>(Belongings.Items.Where(i => i.Slot > 0).Select(i => i.Slot));
            bool changed = false;
            foreach (var it in Belongings.Items.ToList())
            {
                if (it.Slot <= 0)
                {
                    // a slot whose item is still somewhere (a market, a stash, another party) keeps its old name: not reused
                    int free = Enumerable.Range(1, Slots).FirstOrDefault(n =>
                    {
                        if (used.Contains(n) || SlotItem(n) == null || roster.GetItemNumber(SlotItem(n)) > 0) return false;
                        Elsewhere(SlotItem(n), out bool m, out bool o);
                        return !m && !o;
                    });
                    if (free == 0) continue;      // all 40 in use: it stays in the list only
                    it.Slot = free; used.Add(free);
                    changed = true;
                }
                var item = SlotItem(it.Slot);
                if (item == null) continue;
                Name(it.Slot, it.Name);
                int have = roster.GetItemNumber(item);
                if (!it.Delivered)
                {
                    if (have == 0) roster.AddToCounts(item, 1);
                    it.Delivered = true; changed = true;
                    Log.Info($"[documents] \"{it.Name}\" is in the inventory (slot {it.Slot})");
                }
                else if (have == 0)
                {
                    // put away in a stash or with a party of the player's clan: still the player's
                    Elsewhere(item, out bool mine, out _);
                    if (mine) continue;
                    // sold, dropped or given away: it is no longer the player's
                    Belongings.Items.Remove(it);
                    changed = true;
                    Log.Info($"[documents] \"{it.Name}\" left the inventory: no longer carried");
                }
                else if (have > 1) roster.AddToCounts(item, 1 - have);
            }
            // a document taken from the list since the last pass (handed to a character, removed in the editor) leaves the
            // inventory; one bought back from a market, never on the list, stays where it is
            var now = new HashSet<int>(Belongings.Items.Where(i => i.Slot > 0).Select(i => i.Slot));
            if (_last != null)
                foreach (int n in _last)
                {
                    if (now.Contains(n)) continue;
                    var item = SlotItem(n);
                    int have = item == null ? 0 : roster.GetItemNumber(item);
                    if (have > 0) { roster.AddToCounts(item, -have); Log.Info($"[documents] slot {n} taken out of the inventory"); }
                }
            _last = now;
            if (changed) SaveFiles.Dirty();
        }
    }

    /// <summary>A document's contents and who gave it, in its tooltip in the inventory.</summary>
    [HarmonyPatch(typeof(ItemMenuVM), "RefreshItemTooltips")]
    internal static class RpItemTooltipPatch
    {
        private static readonly System.Reflection.MethodInfo Create = AccessTools.Method(typeof(ItemMenuVM), "CreateProperty");
        private static int _errors;

        private static void Postfix(ItemMenuVM __instance, ItemVM item)
        {
            try
            {
                var it = RpInventory.Of(item?.ItemRosterElement.EquipmentElement.Item);
                if (it == null || Create == null) return;
                var list = __instance.TargetItemProperties;
                if (!string.IsNullOrWhiteSpace(it.Description)) Create.Invoke(__instance, new object[] { list, "", it.Description, 0, null });
                if (!string.IsNullOrWhiteSpace(it.FromName)) Create.Invoke(__instance, new object[] { list, "", L.F("AIL_e7b1de71", $"De {it.FromName}"), 0, null });
            }
            catch (Exception ex) { if (_errors++ < 3) Log.Error("Document tooltip", ex); }
        }
    }
}
