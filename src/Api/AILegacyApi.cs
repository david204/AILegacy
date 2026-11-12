using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.World;
using TaleWorlds.CampaignSystem;

namespace AILegacy.Api
{
    /// <summary>What an NPC keeps about the player. A copy: changing it changes nothing.</summary>
    public sealed class NpcInfo
    {
        public string HeroId { get; internal set; }
        public string Name { get; internal set; }
        public int Conversations { get; internal set; }
        /// <summary>Campaign day of the last conversation, or -1.</summary>
        public double LastConversationDay { get; internal set; }
        /// <summary>-100..100.</summary>
        public int Trust { get; internal set; }
        /// <summary>0..100.</summary>
        public int Romance { get; internal set; }
        public int Tension { get; internal set; }
        public int LiesSuspected { get; internal set; }
        public string Mood { get; internal set; }
        public string MoodReason { get; internal set; }
        /// <summary>The running summary of what has happened between them and the player.</summary>
        public string Summary { get; internal set; }
        public IReadOnlyList<string> Notes { get; internal set; }
        public IReadOnlyList<string> Instructions { get; internal set; }
    }

    /// <summary>A world event as AI Legacy keeps it. A copy.</summary>
    public sealed class WorldEventInfo
    {
        public string Id { get; internal set; }
        public double Day { get; internal set; }
        /// <summary>political | military | economic | social | mysterious | news</summary>
        public string Type { get; internal set; }
        public string Title { get; internal set; }
        public string Text { get; internal set; }
        /// <summary>English summary, the one that goes into prompts.</summary>
        public string Summary { get; internal set; }
        public int Importance { get; internal set; }
        public IReadOnlyList<string> KingdomIds { get; internal set; }
        public IReadOnlyList<string> SettlementIds { get; internal set; }
        /// <summary>world | statement | player</summary>
        public string Source { get; internal set; }
    }

    /// <summary>
    /// Public API of AI Legacy for other mods. Reference AILegacy.dll, load after AILegacy, and call from the game's main
    /// thread (campaign events, ticks, your own behaviors). Every call is safe: it never throws, and it returns false, null
    /// or an empty list when no campaign is loaded or the input is not valid.
    ///
    /// Text you hand over (notes, instructions, world rules, action descriptions) goes into prompts: write it in English.
    /// </summary>
    public static class AILegacyApi
    {
        /// <summary>Goes up when something is added. Nothing is removed within the same major version of the mod.</summary>
        public const int ApiVersion = 1;

        /// <summary>A campaign is loaded and AI Legacy has its data ready.</summary>
        public static bool IsReady
        {
            get { try { return Campaign.Current != null && Hero.MainHero != null && WorldSim.State != null; } catch { return false; } }
        }

        // ============================================================ events

        /// <summary>The player has said something to a hero in an AI Legacy conversation. (hero, text)</summary>
        public static event Action<Hero, string> PlayerSaid;
        /// <summary>A hero has answered the player. (hero, text)</summary>
        public static event Action<Hero, string> NpcReplied;
        /// <summary>An action decided by a hero has really taken effect. (hero, action name, argument, message shown to the player or "")</summary>
        public static event Action<Hero, string, string, string> ActionExecuted;

        internal static void RaisePlayerSaid(Hero h, string text) => Raise(PlayerSaid, d => ((Action<Hero, string>)d)(h, text), h);
        internal static void RaiseNpcReplied(Hero h, string text) => Raise(NpcReplied, d => ((Action<Hero, string>)d)(h, text), h);
        internal static void RaiseActionExecuted(Hero h, string action, string arg, string message) =>
            Raise(ActionExecuted, d => ((Action<Hero, string, string, string>)d)(h, action, arg ?? "", message ?? ""), h);

        // Cada suscriptor por separado: el fallo de un mod no deja sin aviso a los demás ni rompe la conversación
        private static void Raise(Delegate ev, Action<Delegate> call, Hero h)
        {
            if (ev == null || h == null) return;
            foreach (var d in ev.GetInvocationList())
            {
                try { call(d); }
                catch (Exception ex) { Log.Error("API: fallo en un mod suscrito (" + (d.Method?.DeclaringType?.FullName ?? "?") + ")", ex); }
            }
        }

        // ============================================================ read

        /// <summary>What that hero keeps about the player, or null if they have never dealt with each other.</summary>
        public static NpcInfo GetNpc(Hero hero)
        {
            try
            {
                if (!IsReady || hero == null) return null;
                var m = MemoryStore.Find(hero.StringId);
                if (m == null) return null;
                return new NpcInfo
                {
                    HeroId = m.HeroId, Name = hero.Name?.ToString() ?? m.HeroName, Conversations = m.ConversationCount, LastConversationDay = m.LastConversationDay,
                    Trust = m.Trust, Romance = m.Romance, Tension = m.Tension, LiesSuspected = m.LiesSuspected, Mood = m.Mood ?? "", MoodReason = m.MoodReason ?? "",
                    Summary = m.Summary ?? "", Notes = m.Notes.ToList(), Instructions = m.CustomInstructions.ToList(),
                };
            }
            catch (Exception ex) { Log.Error("API: GetNpc", ex); return null; }
        }

        /// <summary>Heroes the player has talked to at least once.</summary>
        public static IReadOnlyList<Hero> GetKnownHeroes()
        {
            var list = new List<Hero>();
            try
            {
                if (!IsReady) return list;
                var ids = new HashSet<string>(MemoryStore.AllHeroes.Where(m => m.ConversationCount > 0).Select(m => m.HeroId));
                list.AddRange(Hero.AllAliveHeroes.Where(h => ids.Contains(h.StringId)));
            }
            catch (Exception ex) { Log.Error("API: GetKnownHeroes", ex); }
            return list;
        }

        /// <summary>The world events AI Legacy has in play, oldest first.</summary>
        public static IReadOnlyList<WorldEventInfo> GetWorldEvents()
        {
            var list = new List<WorldEventInfo>();
            try
            {
                if (!IsReady) return list;
                foreach (var e in WorldSim.State.Events)
                    list.Add(new WorldEventInfo
                    {
                        Id = e.Id, Day = e.Day, Type = e.Type, Title = e.Title, Text = e.Text, Summary = e.SummaryEn, Importance = e.Importance,
                        KingdomIds = e.Kingdoms.ToList(), SettlementIds = e.Settlements.ToList(), Source = e.Source,
                    });
            }
            catch (Exception ex) { Log.Error("API: GetWorldEvents", ex); }
            return list;
        }

        // ============================================================ write: one hero

        private static HeroMemory Mem(Hero hero) => !IsReady || hero == null || hero == Hero.MainHero ? null : MemoryStore.Get(hero.StringId, hero.Name?.ToString() ?? hero.StringId);

        private static string Clean(string t, int max)
        {
            t = (t ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return t.Length <= max ? t : t.Substring(0, max);
        }

        /// <summary>A fact the hero will remember about the player ("They saved your brother at Epicrotea."). At most 300 characters.</summary>
        public static bool AddNote(Hero hero, string note)
        {
            try
            {
                var m = Mem(hero); note = Clean(note, 300);
                if (m == null || note.Length == 0) return false;
                if (!m.Notes.Contains(note)) { m.Notes.Add(note); if (m.Notes.Count > 60) m.Notes.RemoveAt(0); }
                return true;
            }
            catch (Exception ex) { Log.Error("API: AddNote", ex); return false; }
        }

        /// <summary>Removes a note by its exact text.</summary>
        public static bool RemoveNote(Hero hero, string note)
        {
            try
            {
                var m = Mem(hero); note = Clean(note, 300);
                if (m == null || !m.Notes.Remove(note)) return false;
               
                return true;
            }
            catch (Exception ex) { Log.Error("API: RemoveNote", ex); return false; }
        }

        /// <summary>A standing instruction for how that hero behaves with the player ("You owe them a debt and it shames you."). At most 400 characters.</summary>
        public static bool AddInstruction(Hero hero, string instruction)
        {
            try
            {
                var m = Mem(hero); instruction = Clean(instruction, 400);
                if (m == null || instruction.Length == 0) return false;
                if (!m.CustomInstructions.Contains(instruction)) { m.CustomInstructions.Add(instruction); if (m.CustomInstructions.Count > 20) m.CustomInstructions.RemoveAt(0); }
                return true;
            }
            catch (Exception ex) { Log.Error("API: AddInstruction", ex); return false; }
        }

        public static bool RemoveInstruction(Hero hero, string instruction)
        {
            try
            {
                var m = Mem(hero); instruction = Clean(instruction, 400);
                if (m == null || !m.CustomInstructions.Remove(instruction)) return false;
               
                return true;
            }
            catch (Exception ex) { Log.Error("API: RemoveInstruction", ex); return false; }
        }

        /// <summary>Adds to (or takes from) the trust the hero has in the player. Trust stays within -100..100. Returns the new value, or null.</summary>
        public static int? ChangeTrust(Hero hero, int delta)
        {
            try
            {
                var m = Mem(hero);
                if (m == null) return null;
                m.Trust = Math.Max(-100, Math.Min(100, m.Trust + delta));
                m.TrustSet = true;
               
                return m.Trust;
            }
            catch (Exception ex) { Log.Error("API: ChangeTrust", ex); return null; }
        }

        /// <summary>Adds to (or takes from) the romance between the hero and the player. Stays within 0..100. Returns the new value, or null.</summary>
        public static int? ChangeRomance(Hero hero, int delta)
        {
            try
            {
                var m = Mem(hero);
                if (m == null) return null;
                m.Romance = Math.Max(0, Math.Min(100, m.Romance + delta));
                if (delta > 0) m.LastRomanceDay = CampaignTime.Now.ToDays;
               
                return m.Romance;
            }
            catch (Exception ex) { Log.Error("API: ChangeRomance", ex); return null; }
        }

        /// <summary>
        /// Something has happened to the hero that will weigh on their mood for some days. <paramref name="whatHappened"/> is
        /// written in English and in the second person ("Your caravan was robbed on the road to Sargot.").
        /// sign: -1 weighs on them, +1 lifts them, 0 unsettles them. strength: 1..3.
        /// </summary>
        public static bool AddMoodCause(Hero hero, string whatHappened, int sign, int strength, int days)
        {
            try
            {
                whatHappened = Clean(whatHappened, 300);
                if (!IsReady || hero == null || hero == Hero.MainHero || whatHappened.Length == 0) return false;
                return Mood.AddFromMod(hero.StringId, whatHappened, Math.Sign(sign), Math.Max(1, Math.Min(3, strength)), Math.Max(1, Math.Min(60, days)));
            }
            catch (Exception ex) { Log.Error("API: AddMoodCause", ex); return false; }
        }

        // ============================================================ write: every prompt

        /// <summary>
        /// A rule of the world that every character knows ("Magic is real here and feared."). One per id: setting it again
        /// replaces it; null or empty removes it. It goes into the part of the prompt shared by all characters, so change it
        /// rarely (every change makes the providers' prompt cache start over). At most 600 characters. Not saved: set it again
        /// on every load.
        /// </summary>
        public static bool SetWorldRule(string id, string rule)
        {
            try
            {
                id = Clean(id, 60); rule = Clean(rule, 600);
                if (id.Length == 0) return false;
                if (rule.Length == 0) return ModHooks.WorldRules.Remove(id);
                ModHooks.WorldRules[id] = rule;
                return true;
            }
            catch (Exception ex) { Log.Error("API: SetWorldRule", ex); return false; }
        }

        /// <summary>
        /// Lines about the present moment for the hero who is talking ("Your village is under the protection of the Order.").
        /// The function is called on every turn of a conversation: keep it fast, return null or "" when there is nothing to
        /// say. One per id; null removes it.
        /// </summary>
        public static bool SetContextProvider(string id, Func<Hero, string> provider)
        {
            try
            {
                id = Clean(id, 60);
                if (id.Length == 0) return false;
                if (provider == null) return ModHooks.Context.Remove(id);
                ModHooks.Context[id] = provider;
                return true;
            }
            catch (Exception ex) { Log.Error("API: SetContextProvider", ex); return false; }
        }

        // ============================================================ actions

        /// <summary>
        /// Gives the characters a new action they can decide to take while talking to the player. It is offered to the AI as
        /// "mod_&lt;modId&gt;_&lt;name&gt;" (or with ":&lt;argument&gt;" when <paramref name="argumentHint"/> is given, e.g. "amount of gold").
        /// </summary>
        /// <param name="modId">Short id of your mod: letters, digits and underscores.</param>
        /// <param name="name">Short name of the action: letters, digits and underscores.</param>
        /// <param name="description">What it does and when a character should use it. English. This is what the AI reads.</param>
        /// <param name="argumentHint">What goes after the colon, or null if the action takes no argument.</param>
        /// <param name="isAvailable">Is the action open to this hero right now? Called often: keep it fast.</param>
        /// <param name="execute">Does it. Gets the hero and the argument. Returns the message to show the player ("" for none), or null if it could not be done (the character is then told it did not happen).</param>
        /// <returns>The full action name, or null if it could not be registered.</returns>
        public static string RegisterAction(string modId, string name, string description, string argumentHint, Func<Hero, bool> isAvailable, Func<Hero, string, string> execute)
        {
            try
            {
                string full = ModHooks.ActionName(modId, name);
                description = Clean(description, 700);
                if (full == null || description.Length == 0 || execute == null) return null;
                return NpcActions.RegisterModAction(full, description, Clean(argumentHint, 60), isAvailable, execute) ? full : null;
            }
            catch (Exception ex) { Log.Error("API: RegisterAction", ex); return null; }
        }

        /// <summary>Removes every action registered under that mod id. Returns how many.</summary>
        public static int UnregisterActions(string modId)
        {
            try
            {
                string prefix = ModHooks.ActionName(modId, "x");
                return prefix == null ? 0 : NpcActions.UnregisterModActions(prefix.Substring(0, prefix.Length - 1));
            }
            catch (Exception ex) { Log.Error("API: UnregisterActions", ex); return 0; }
        }
    }

    /// <summary>Lo que otros mods han colgado del prompt.</summary>
    internal static class ModHooks
    {
        public static readonly Dictionary<string, string> WorldRules = new Dictionary<string, string>();
        public static readonly Dictionary<string, Func<Hero, string>> Context = new Dictionary<string, Func<Hero, string>>();

        private static string Slug(string t)
        {
            var chars = (t ?? "").Trim().ToLowerInvariant().Select(c => (c >= 'a' && c <= 'z') || (c >= '0' && c <= '9') ? c : '_').ToArray();
            string s = new string(chars).Trim('_');
            while (s.Contains("__")) s = s.Replace("__", "_");
            return s.Length > 30 ? s.Substring(0, 30) : s;
        }

        public static string ActionName(string modId, string name)
        {
            string a = Slug(modId), b = Slug(name);
            return a.Length == 0 || b.Length == 0 ? null : "mod_" + a + "_" + b;
        }

        /// <summary>Reglas de mundo de otros mods, para la parte del prompt común a todos (orden estable).</summary>
        public static string DescribeWorldRules()
        {
            try
            {
                if (WorldRules.Count == 0) return "";
                return string.Join("\n", WorldRules.OrderBy(kv => kv.Key, StringComparer.Ordinal).Select(kv => "- " + kv.Value));
            }
            catch { return ""; }
        }

        /// <summary>Lo que otros mods quieren decir del momento a quien habla.</summary>
        public static string DescribeContext(Hero h)
        {
            if (h == null || Context.Count == 0) return "";
            var lines = new List<string>();
            foreach (var kv in Context.OrderBy(kv => kv.Key, StringComparer.Ordinal).ToList())
            {
                try
                {
                    string t = kv.Value(h);
                    if (string.IsNullOrWhiteSpace(t)) continue;
                    t = t.Replace("\r", " ").Trim();
                    lines.Add(t.Length > 500 ? t.Substring(0, 500) : t);
                }
                catch (Exception ex) { Log.Error("API: fallo en el contexto del mod «" + kv.Key + "»", ex); }
            }
            return string.Join("\n", lines);
        }
    }
}
