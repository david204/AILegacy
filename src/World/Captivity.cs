using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Prompt;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.LogEntries;
using TaleWorlds.CampaignSystem.Party;

namespace AILegacy.World
{
    /// <summary>Someone the player put to death: by execution, or with their own hand in a scene.</summary>
    public class ExecutionRecord
    {
        [JsonProperty("v")] public string Victim;
        [JsonProperty("n")] public string Name;
        /// <summary>Who they were when they died, in English ("ruler of Vlandia", "head of clan dey Meroc").</summary>
        [JsonProperty("t")] public string Title = "";
        [JsonProperty("d")] public double Day;
        [JsonProperty("k")] public string Kind = "executed";
    }

    /// <summary>
    /// The player's prisoners: who counts as one, the record of those the player has put to death, and what a
    /// captive is told about his situation.
    /// </summary>
    internal static class Captivity
    {
        private static readonly Tpl TCaptive = new Tpl("context/captive_of_player.txt",
@"## You are their prisoner
You are held in chains by {player_name}. Nobody is coming for you today, and whether you live, rot or walk free is theirs to decide. {record}
A threat of death from them is credible: take it as real, and each time it is repeated take it as closer. You feel real fear, whatever face you put on it. You bargain for your life with what you have (a ransom, your oath, land, what you know), and you give way rather than die; the more they threaten, the more you yield. How little you like or trust them is no reason to refuse, and it outweighs what your trust in them would otherwise let you confide: a frightened captive talks and signs. Do not lecture them, preach or dare them to do it.
{temper}");

        private static readonly Tpl TRecordSome = new Tpl("context/captive_record_some.txt",
            "It is known that they have put {count} captive(s) to death before you: {names}. They do it.");
        private static readonly Tpl TRecordNone = new Tpl("context/captive_record_none.txt",
            "You have not heard of them killing a prisoner, but nothing and nobody here would stop them.");

        private static readonly Tpl TTemperBrave = new Tpl("context/captive_temper_brave.txt",
            "You are exceptionally brave: you may keep your dignity and refuse what would shame you, but you know the blade is real and you do not waste your life on a speech.");
        private static readonly Tpl TTemperHonour = new Tpl("context/captive_temper_honour.txt",
            "You hold your sworn word above most things: breaking faith costs you dearly and you say so, but a dead man keeps no oaths, and you weigh that.");
        private static readonly Tpl TTemperCoward = new Tpl("context/captive_temper_coward.txt",
            "You are not a brave person: the first serious threat breaks you, and you offer more than you were asked for.");
        private static readonly Tpl TTemperOrdinary = new Tpl("context/captive_temper_ordinary.txt",
            "You are no martyr. Only if your inner character above plainly makes you a fanatic who welcomes death would you choose it over yielding.");

        public static void Touch() { var _ = TCaptive.Key + TRecordSome.Key + TRecordNone.Key + TTemperBrave.Key + TTemperHonour.Key + TTemperCoward.Key + TTemperOrdinary.Key; }

        /// <summary>Held in the player's party, or in the dungeon of a town or castle of the player's clan.</summary>
        public static bool HeldByPlayer(Hero h)
        {
            try
            {
                if (h == null || !h.IsPrisoner || h == Hero.MainHero) return false;
                var at = h.PartyBelongedToAsPrisoner;
                if (at == null) return false;
                if (at == PartyBase.MainParty) return true;
                return at.IsSettlement && at.Settlement?.OwnerClan != null && at.Settlement.OwnerClan == Clan.PlayerClan;
            }
            catch { return false; }
        }

        // ============================================================ the record

        private static string TitleOf(Hero h)
        {
            try
            {
                var c = h.Clan;
                if (c == null) return h.IsNotable ? "a notable" : "";
                var k = Facts.Subject(c);
                if (k != null && k.Leader == h) return $"ruler of {k.Name}";
                if (c.Leader == h) return $"head of clan {c.Name}" + (k != null ? $" of {k.Name}" : "");
                return $"of clan {c.Name}" + (k != null ? $" of {k.Name}" : "");
            }
            catch { return ""; }
        }

        /// <summary>The record of this save. A malformed save can bring the list, or an entry of it, as null.</summary>
        private static List<ExecutionRecord> Records(WorldState st)
        {
            if (st == null) return null;
            if (st.Executions == null) st.Executions = new List<ExecutionRecord>();
            st.Executions.RemoveAll(e => e == null || string.IsNullOrEmpty(e.Victim));
            foreach (var e in st.Executions) { e.Name = e.Name ?? ""; e.Title = e.Title ?? ""; e.Kind = e.Kind ?? "executed"; }
            return st.Executions;
        }

        private static void Add(Hero victim, string kind, double day)
        {
            var st = WorldSim.State;
            if (Records(st) == null || victim == null || st.Executions.Any(e => e.Victim == victim.StringId)) return;
            st.Executions.Add(new ExecutionRecord { Victim = victim.StringId, Name = victim.Name?.ToString() ?? "", Title = TitleOf(victim), Day = day, Kind = kind });
            while (st.Executions.Count > 60) st.Executions.RemoveAt(0);
        }

        /// <summary>
        /// Runs right before the game kills someone. The title is read here because by the time the death is
        /// announced the game has already handed the clan and the crown to someone else.
        /// </summary>
        public static void OnBeforeHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (victim == null || killer == null || killer != Hero.MainHero || victim == killer) return;
                string d = detail.ToString();
                if (d != "Executed" && d != "Murdered" && d != "ExecutionAfterMapEvent") return;
                Seed();
                Add(victim, d == "Murdered" ? "killed" : "executed", CampaignTime.Now.ToDays);
            }
            catch (Exception ex) { Log.Error("Execution log", ex); }
        }

        /// <summary>Once per save: the executions the game's own log remembers from before this record existed.</summary>
        private static void Seed()
        {
            var st = WorldSim.State;
            if (st == null || st.ExecutionsSeeded) return;
            st.ExecutionsSeeded = true;
            try
            {
                var logs = Campaign.Current?.LogEntryHistory?.GameActionLogs;
                if (logs == null) return;
                foreach (var e in logs.OfType<CharacterKilledLogEntry>())
                {
                    var v = e.Victim;
                    if (v == null || e.Killer != Hero.MainHero || v.IsAlive) continue;
                    string mark = v.DeathMark.ToString();
                    if (mark != "Executed" && mark != "ExecutionAfterMapEvent") continue;
                    double day = CampaignTime.Now.ToDays;
                    try { day = v.DeathDay.ToDays; } catch { }
                    Add(v, "executed", day);
                }
                st.Executions = Records(st).OrderBy(x => x.Day).ToList();
            }
            catch (Exception ex) { Log.Error("Past executions, read from the game log", ex); }
        }

        public static int Count { get { try { Seed(); return Records(WorldSim.State)?.Count ?? 0; } catch { return 0; } } }

        /// <summary>The player's record with captives, for a captive's briefing.</summary>
        private static string Record()
        {
            try
            {
                Seed();
                var list = Records(WorldSim.State);
                if (list == null || list.Count == 0) return TRecordNone.Text;
                // rulers first, then the latest
                var shown = list.OrderByDescending(e => e.Title.StartsWith("ruler") ? 1 : 0).ThenByDescending(e => e.Day).Take(5)
                    .Select(e => e.Name + (e.Title.Length > 0 ? $" ({e.Title})" : "")).ToList();
                if (list.Count > shown.Count) shown.Add($"and {list.Count - shown.Count} more");
                return TRecordSome.Format("count", list.Count, "names", string.Join(", ", shown));
            }
            catch { return TRecordNone.Text; }
        }

        private static string Temper(Hero h)
        {
            int valor = 0, honor = 0;
            try { valor = h.GetTraitLevel(DefaultTraits.Valor); honor = h.GetTraitLevel(DefaultTraits.Honor); } catch { }
            if (valor >= 2) return TTemperBrave.Text;
            if (valor <= -1) return TTemperCoward.Text;
            if (honor >= 2) return TTemperHonour.Text;
            return TTemperOrdinary.Text;
        }

        /// <summary>What a prisoner of the player is told about his situation. Nothing for anyone else.</summary>
        public static void AppendTo(StringBuilder sb, Speaker s)
        {
            try
            {
                if (s == null || !s.IsHero || !HeldByPlayer(s.Hero)) return;
                sb.AppendLine();
                sb.AppendLine(TCaptive.Format("player_name", ContextBuilder.PlayerNameFor(s) ?? "your captor", "record", Record(), "temper", Temper(s.Hero)));
            }
            catch (Exception ex) { Log.Error("Prisoner status", ex); }
        }
    }
}
