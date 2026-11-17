using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.ComponentInterfaces;
using TaleWorlds.CampaignSystem.GameComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Closed doors, as AIInfluence has them: the people of a town know which of its doors are closed to the player and why,
    /// and the right ones can open them for this visit. A guard opens the door they keep, on the word of the lord's household
    /// or for the usual fee; someone of the house that holds the place has the say over every door and can tell the guards,
    /// or take the player in themselves. The pass lasts until the player leaves the settlement.
    /// </summary>
    internal static class Doors
    {
        // The doors a visitor may find closed: the lord's hall and the dungeon (and the tavern or arena when the game shuts them)
        private static readonly string[] Ids = { "lordshall", "prison", "tavern", "arena" };
        private static readonly HashSet<string> Passes = new HashSet<string>();
        private static Settlement _where;

        public static void ResetSession() { Passes.Clear(); _where = null; }

        private static void Sync()
        {
            var cur = Settlement.CurrentSettlement;
            if (_where != null && cur != _where)
            {
                if (Passes.Count > 0) Log.Info($"[doors] door passes for the player are over ({_where.Name}, {string.Join(", ", Passes)})");
                Passes.Clear(); _where = null;
            }
        }

        public static bool HasPass(Settlement s, string locationId)
        {
            Sync();
            return s != null && s == _where && locationId != null && Passes.Contains(locationId);
        }

        private static void Grant(Settlement s, string locationId, string by)
        {
            Sync();
            _where = s;
            Passes.Add(locationId);
            Log.Info($"[doors] the door into {locationId} of {s.Name} is open to the player now: {by}");
        }

        /// <summary>The doors of this settlement closed to the player right now, with the reason in English.</summary>
        public static List<(string id, string name, string why, int fee)> Closed()
        {
            var list = new List<(string, string, string, int)>();
            try
            {
                Sync();
                var st = Settlement.CurrentSettlement;
                var complex = LocationComplex.Current;
                var model = Campaign.Current?.Models?.SettlementAccessModel;
                if (st == null || complex == null || model == null || !st.IsFortification) return list;
                foreach (var id in Ids)
                {
                    var loc = complex.GetLocationWithId(id);
                    if (loc == null || HasPass(st, id)) continue;
                    if (model.CanMainHeroAccessLocation(st, id, out _, out _)) continue;
                    list.Add((id, loc.Name?.ToString() ?? id, Reason(st, id, model), Fee(st, id)));
                }
            }
            catch (Exception ex) { Log.Warn("[doors] closed doors: " + ex.Message); }
            return list;
        }

        private static string Reason(Settlement st, string id, SettlementAccessModel model)
        {
            try
            {
                if (id != "lordshall" && id != "prison") return "they may not go in there now";
                SettlementAccessModel.AccessDetails d;
                if (id == "lordshall") model.CanMainHeroEnterLordsHall(st, out d); else model.CanMainHeroEnterDungeon(st, out d);
                switch (d.AccessLimitationReason)
                {
                    case SettlementAccessModel.AccessLimitationReason.LocationEmpty: return "nobody of the household is inside to receive them";
                    case SettlementAccessModel.AccessLimitationReason.CrimeRating: return "they are wanted for crimes here";
                    case SettlementAccessModel.AccessLimitationReason.Disguised: return "they belong to an enemy";
                    case SettlementAccessModel.AccessLimitationReason.ClanTier: return "the guards do not know them: their clan is too little known";
                }
            }
            catch { }
            return "the guards do not let strangers in";
        }

        private static int Fee(Settlement st, string id)
        {
            try
            {
                var bribe = Campaign.Current.Models.BribeCalculationModel;
                return id == "lordshall" ? bribe.GetBribeToEnterLordsHall(st) : id == "prison" ? bribe.GetBribeToEnterDungeon(st) : 0;
            }
            catch { return 0; }
        }

        /// <summary>Someone of the house that holds this place: their word opens any door.</summary>
        public static bool Household(Speaker s)
        {
            var st = Settlement.CurrentSettlement;
            var h = s?.Hero;
            return h != null && st?.OwnerClan != null && h.Clan == st.OwnerClan && h != Hero.MainHero && h.IsAlive && !h.IsPrisoner && h.Age >= 18;
        }

        /// <summary>A guard of that door: the prison guard keeps the dungeon, the hall guard the lord's hall, any other guard either.</summary>
        public static bool Guards(Speaker s, string id)
        {
            if (s == null || s.IsHero) return false;
            string role = (s.Role ?? "").ToLowerInvariant();
            if (!role.Contains("guard")) return false;
            if (role.Contains("prison")) return id == "prison";
            if (role.Contains("lord's hall") || role.Contains("hall")) return id == "lordshall";
            return true;
        }

        public static bool CanOpenAny(Speaker s)
        {
            try { return (Household(s) || !s.IsHero) && Closed().Any(c => Household(s) || Guards(s, c.id)); }
            catch { return false; }
        }

        /// <summary>The prompt lines: each door this person can open, why it is closed, and how.</summary>
        public static string PromptFor(Speaker s)
        {
            var lines = new List<string>();
            bool house = Household(s);
            foreach (var c in Closed())
            {
                if (house)
                    lines.Add($"- `let_in:{c.id}` — you have authority here: tell the guards to let the other person into {c.name} for this visit (closed to them now: {c.why}). Taking them in yourself (`go_into:{c.id}`) lets them in as well.");
                else if (Guards(s, c.id))
                    lines.Add($"- `let_in:{c.id}` — you guard the door into {c.name}: open it to the other person for this visit. It is closed to them: {c.why}. " +
                              "Your orders let a stranger in on the word of the lord's household" +
                              (c.fee > 0 ? $", or for the usual fee of {c.fee} denars: add `,gold:<denars>` with the sum they agreed to pay, it is taken from them (`let_in:{c.id},gold:{c.fee}`)" : "") +
                              ". The rest is your own judgement.");
            }
            if (lines.Count == 0) return "";
            return "Doors closed to them here:\n" + string.Join("\n", lines) + "\nWords alone open no door: until `let_in` is used, the door stays closed to them.";
        }

        /// <summary>`let_in:<place>[,gold:<denars>]`. The line the player sees, or null with the reason in <paramref name="why"/>.</summary>
        public static string LetIn(Speaker s, string arg, out string why)
        {
            why = null;
            try
            {
                var st = Settlement.CurrentSettlement;
                string place = (arg ?? "").Split(',')[0].Trim().Trim('<', '>', '"', '\'').Trim().ToLowerInvariant();
                int gold = 0;
                foreach (var part in (arg ?? "").Split(',').Skip(1))
                {
                    var kv = part.Split(':');
                    if (kv.Length == 2 && kv[0].Trim().Equals("gold", StringComparison.OrdinalIgnoreCase)) int.TryParse(kv[1].Trim(), out gold);
                }
                bool house = Household(s);
                if (st == null || (!house && s.IsHero)) { why = "you have no say over the doors here: only the guards and the lord's household do"; return null; }
                var closed = Closed();
                if (closed.Count == 0) { why = "there is no door here that is closed to them"; return null; }
                var c = closed.FirstOrDefault(x => x.id == place || x.name.ToLowerInvariant() == place || (place.Length > 2 && x.name.ToLowerInvariant().Contains(place)));
                if (c.id == null) { why = $"there is no door to «{place}» here that is closed to them; you can open: {string.Join(", ", closed.Select(x => x.id))}"; return null; }
                if (!house && !Guards(s, c.id)) { why = "you have no say over the doors here: only the guards and the lord's household do"; return null; }
                if (!house && gold > 0)
                {
                    if (Hero.MainHero.Gold < gold) { why = $"they do not have {gold} denars to pay you; the door stays closed"; return null; }
                    Hero.MainHero.ChangeHeroGold(-gold);
                }
                Grant(st, c.id, house ? $"{s.Name} told the guards to let them in" : gold > 0 ? $"{s.Name} took {gold} denars" : $"{s.Name} let them in");
                var t = new TextObject("{=!}" + L.T("AIL_e7b1de60", "La puerta está abierta para ti: {PLACE} ({NPC_NAME})."));
                t.SetTextVariable("PLACE", c.name); t.SetTextVariable("NPC_NAME", s.Name);
                return t.ToString();
            }
            catch (Exception ex) { Log.Error("Opening a door", ex); why = "the door could not be opened"; return null; }
        }

        /// <summary>Someone of the house takes the player into a closed place themselves: that opens the door as well.</summary>
        public static void OpenForLead(Speaker s, string arg)
        {
            try
            {
                if (!Household(s)) return;
                string place = (arg ?? "").Split(',')[0].Trim().Trim('<', '>', '"', '\'').Trim().ToLowerInvariant();
                var c = Closed().FirstOrDefault(x => x.id == place || x.name.ToLowerInvariant() == place || (place.Length > 2 && x.name.ToLowerInvariant().Contains(place)));
                if (c.id != null) Grant(Settlement.CurrentSettlement, c.id, $"{s.Name} takes them in");
            }
            catch (Exception ex) { Log.Warn("[doors] lead in: " + ex.Message); }
        }

        /// <summary>For the go_into line: closed places someone of the house could still take them to.</summary>
        public static string ClosedForHousehold(Speaker s)
        {
            if (!Household(s)) return "";
            var closed = Closed();
            return closed.Count == 0 ? "" : " Closed to them, but yours to take them into: " + string.Join(", ", closed.Select(c => $"{c.id} ({c.name})")) + ".";
        }
    }

    /// <summary>A door opened for this visit lets the player through, whatever the game's own rule says.</summary>
    [HarmonyPatch(typeof(DefaultSettlementAccessModel), nameof(DefaultSettlementAccessModel.CanMainHeroAccessLocation))]
    internal static class DoorPassPatch
    {
        private static void Postfix(Settlement settlement, string locationId, ref bool disableOption, ref TextObject disabledText, ref bool __result)
        {
            try
            {
                if (__result || !Doors.HasPass(settlement, locationId)) return;
                __result = true; disableOption = false; disabledText = null;
            }
            catch { }
        }
    }
}
