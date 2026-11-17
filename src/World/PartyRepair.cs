using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using HarmonyLib;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;

namespace AILegacy.World
{
    /// <summary>
    /// A hero counted twice in your party, or still in it while leading a party of their own (left behind by moves between
    /// parties), is put right: one copy only, and the one who leads goes back to their own party. Looked at a few seconds
    /// after loading a campaign, and on demand from the console (Illegitimate Children's «repair party»).
    /// </summary>
    internal static class PartyRepair
    {
        private static float _countdown = -1f;
        internal static bool Quiet;

        public static void Request(float seconds = 3f) { if (_countdown < 0f || seconds < _countdown) _countdown = seconds; }
        public static void ResetSession() { _countdown = -1f; Quiet = false; }

        public static void Tick(float dt)
        {
            if (_countdown < 0f || Campaign.Current == null || MobileParty.MainParty == null) return;
            if ((_countdown -= dt) > 0f) return;
            _countdown = -1f;
            try { string r = Run(false); if (!r.StartsWith("Party membership is clean")) Log.Info("[party repair] after loading: " + r.Replace(Environment.NewLine, " / ")); }
            catch (Exception ex) { Log.Error("Party repair", ex); }
        }

        public static string Run(bool checkOnly)
        {
            var main = MobileParty.MainParty;
            if (main == null) return "No party: load a campaign first.";
            var leads = new Dictionary<Hero, MobileParty>();
            foreach (var p in MobileParty.All)
                if (p != null && p != main && p.IsActive && p.LeaderHero != null && !leads.ContainsKey(p.LeaderHero)) leads[p.LeaderHero] = p;
            var found = new List<string>();
            var roster = main.MemberRoster;
            foreach (var e in roster.GetTroopRoster().ToList())
            {
                var c = e.Character;
                var h = c?.HeroObject;
                if (h == null || h == Hero.MainHero) continue;
                bool leading = leads.TryGetValue(h, out var own) && own.MemberRoster.FindIndexOfTroop(c) >= 0;
                int extra = leading ? e.Number : e.Number - 1;
                if (extra <= 0) continue;
                found.Add(leading ? $"{h.Name}: in your party while leading {own.Name} (the stale copy goes)" : $"{h.Name}: counted {e.Number} times (one is kept)");
                if (checkOnly) continue;
                Quiet = true;
                try { roster.AddToCounts(c, -extra); }
                finally { Quiet = false; }
                if (leading)
                    try { typeof(Hero).GetMethod("OnAddedToParty", BindingFlags.Instance | BindingFlags.NonPublic)?.Invoke(h, new object[] { own }); } catch (Exception ex) { Log.Warn("Party repair: " + ex.Message); }
            }
            if (found.Count == 0) return "Party membership is clean: nothing to repair.";
            string head = checkOnly ? $"Found {found.Count} hero(es) with broken party membership (nothing changed):" : $"Repaired {found.Count} hero(es) with broken party membership:";
            if (!checkOnly) Log.Info("[party repair] " + string.Join("; ", found));
            return head + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", found);
        }
    }

    /// <summary>While a stale copy is taken off your party, the game must not think the hero left the party they really lead.</summary>
    [HarmonyPatch]
    internal static class PartyRepairGuardPatch
    {
        private static MethodBase TargetMethod() => AccessTools.Method(typeof(PartyBase), "OnHeroRemoved");
        private static bool Prefix() => !PartyRepair.Quiet;
    }
}
