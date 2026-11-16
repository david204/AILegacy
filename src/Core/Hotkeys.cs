using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using AILegacy.Settings;
using TaleWorlds.InputSystem;

namespace AILegacy.Core
{
    /// <summary>
    /// The mod's keyboard shortcuts, read from MCM (1. General, Keys). Each one is written as text: "Alt+T",
    /// "Ctrl+Shift+F5", "F9". Empty turns it off, and text that can't be read falls back to the default.
    /// </summary>
    internal static class Hotkeys
    {
        public enum Id { GroupTalk, GroupEnd, BattleOrders, Yield, Truce, SummonTen, SummonAll, Herbs }

        private static readonly string[] Defaults = { "Alt+T", "Alt+G", "Alt+B", "Alt+Y", "Alt+P", "Ctrl+X", "Ctrl+Alt+X", "Alt+H" };
        private static readonly int Count = Defaults.Length;

        private class Combo
        {
            public string Raw;
            public InputKey Key = InputKey.Invalid;
            public bool Alt, Ctrl, Shift, Off;
            // Same key and modifiers as an earlier shortcut: this one is turned off. DupOf is the one that keeps the key
            public bool Dup;
            public int DupOf = -1;
            public string Label = "";
            public int Mods => (Alt ? 1 : 0) | (Ctrl ? 2 : 0) | (Shift ? 4 : 0);
        }

        private static readonly Combo[] Cache = new Combo[Defaults.Length];

        public static string Default(Id id) => Defaults[(int)id];

        private static string Setting(Id id)
        {
            var c = S.Get;
            switch (id)
            {
                case Id.GroupTalk: return c.KeyGroupTalk;
                case Id.GroupEnd: return c.KeyGroupEnd;
                case Id.BattleOrders: return c.KeyBattleOrders;
                case Id.Yield: return c.KeyYield;
                case Id.Truce: return c.KeyTruce;
                case Id.SummonTen: return c.KeySummonTen;
                case Id.Herbs: return "";      // retired: herbs are used in the hospital now, not carried (kept so the ids keep their order)
                default: return c.KeySummonAll;
            }
        }

        private static Combo Parse(string raw)
        {
            var c = new Combo { Raw = raw };
            var text = (raw ?? "").Trim();
            if (text.Length == 0) { c.Off = true; return c; }
            var parts = text.Split('+');
            for (int i = 0; i < parts.Length; i++)
            {
                var p = parts[i].Trim();
                bool last = i == parts.Length - 1;
                if (!last)
                {
                    var m = p.ToLowerInvariant();
                    if (m == "alt") c.Alt = true;
                    else if (m == "ctrl" || m == "control") c.Ctrl = true;
                    else if (m == "shift" || m == "mayus" || m == "mayús") c.Shift = true;
                    else return null;
                    continue;
                }
                // A bare digit means the number row. Enum.TryParse would take it as the enum's numeric value
                if (p.Length == 1 && char.IsDigit(p[0])) p = "D" + p;
                if (p.Length == 0 || char.IsDigit(p[0]) || p[0] == '-') return null;
                if (!Enum.TryParse(p, true, out InputKey key) || !Enum.IsDefined(typeof(InputKey), key) || key == InputKey.Invalid) return null;
                c.Key = key;
            }
            c.Label = (c.Ctrl ? "Ctrl+" : "") + (c.Alt ? "Alt+" : "") + (c.Shift ? "Shift+" : "") + KeyName(c.Key);
            return c;
        }

        private static string KeyName(InputKey k)
        {
            var n = k.ToString();
            return n.Length == 2 && n[0] == 'D' && char.IsDigit(n[1]) ? n.Substring(1) : n;
        }

        private static Combo Get(Id id)
        {
            var c = Fresh((int)id, out bool changed);
            if (changed) MarkDuplicates();
            return c;
        }

        /// <summary>The cached shortcut, parsed again if its MCM text changed.</summary>
        private static Combo Fresh(int i, out bool changed)
        {
            changed = false;
            string raw = null;
            try { raw = Setting((Id)i); } catch { }
            if (raw == null) raw = Defaults[i];
            var have = Cache[i];
            if (have != null && have.Raw == raw) return have;
            var c = Parse(raw);
            if (c == null)
            {
                Log.Warn($"Unrecognised key in MCM ({(Id)i}): '{raw}'. Using {Defaults[i]}.");
                c = Parse(Defaults[i]);
                c.Raw = raw;
            }
            // Keeps what was decided about it, so the same clash is not logged again
            if (have != null && have.Dup && have.Key == c.Key && have.Mods == c.Mods && have.Off == c.Off) { c.Dup = true; c.DupOf = have.DupOf; }
            Cache[i] = c;
            changed = true;
            return c;
        }

        /// <summary>
        /// Two shortcuts on the same key and modifiers would both fire. Runs whenever a shortcut changes: in the fixed
        /// order of Id the first one keeps the key and any later one is turned off, with one log line per clash.
        /// </summary>
        private static void MarkDuplicates()
        {
            var all = new Combo[Count];
            for (int i = 0; i < Count; i++) all[i] = Fresh(i, out _);
            for (int i = 0; i < Count; i++)
            {
                var c = all[i];
                int first = -1;
                if (!c.Off)
                    for (int j = 0; j < i && first < 0; j++)
                        if (!all[j].Off && !all[j].Dup && all[j].Key == c.Key && all[j].Mods == c.Mods) first = j;
                bool told = c.Dup && c.DupOf == first;
                c.Dup = first >= 0;
                c.DupOf = first;
                if (c.Dup && !told)
                    Log.Warn($"Duplicate key in MCM: {(Id)i} and {(Id)first} both use {c.Label}. {(Id)first} keeps it; {(Id)i} has no key until you change one of them.");
            }
        }

        private static bool ModsDown(Combo c) =>
            (!c.Alt || Input.IsKeyDown(InputKey.LeftAlt) || Input.IsKeyDown(InputKey.RightAlt)) &&
            (!c.Ctrl || Input.IsKeyDown(InputKey.LeftControl) || Input.IsKeyDown(InputKey.RightControl)) &&
            (!c.Shift || Input.IsKeyDown(InputKey.LeftShift) || Input.IsKeyDown(InputKey.RightShift));

        /// <summary>
        /// True on the frame the shortcut is pressed. Extra modifiers held down don't matter, unless they make up
        /// another of our shortcuts on the same key (Ctrl+Alt+X must not also count as Ctrl+X).
        /// </summary>
        public static bool Pressed(Id id)
        {
            var c = Get(id);
            if (c.Off || c.Dup || !Input.IsKeyPressed(c.Key) || !ModsDown(c)) return false;
            for (int i = 0; i < Count; i++)
            {
                if (i == (int)id) continue;
                var o = Get((Id)i);
                if (o.Off || o.Dup || o.Key != c.Key || o.Mods == c.Mods || (o.Mods & c.Mods) != c.Mods) continue;
                if (ModsDown(o)) return false;
            }
            return true;
        }

        /// <summary>How the shortcut reads now ("Alt+T"), or "-" when it is turned off or lost its key to an earlier one.</summary>
        public static string Label(Id id) { var c = Get(id); return c.Off || c.Dup ? "-" : c.Label; }

        private static readonly Regex DefaultLabels = new Regex(@"Ctrl\+Alt\+X|Ctrl\+X|Alt\+[TGBYPH]", RegexOptions.Compiled);

        /// <summary>Texts name the default keys. This swaps them for the ones set in MCM.</summary>
        public static string Fix(string text)
        {
            if (string.IsNullOrEmpty(text)) return text;
            try
            {
                return DefaultLabels.Replace(text, m =>
                {
                    int i = Array.IndexOf(Defaults, m.Value);
                    return i < 0 ? m.Value : Label((Id)i);
                });
            }
            catch { return text; }
        }
    }
}
