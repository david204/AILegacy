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
        public enum Id { GroupTalk, GroupEnd, BattleOrders, Yield, Truce, SummonTen, SummonAll }

        private static readonly string[] Defaults = { "Alt+T", "Alt+G", "Alt+B", "Alt+Y", "Alt+P", "Ctrl+X", "Ctrl+Alt+X" };
        private static readonly int Count = Defaults.Length;

        private class Combo
        {
            public string Raw;
            public InputKey Key = InputKey.Invalid;
            public bool Alt, Ctrl, Shift, Off;
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
            string raw = null;
            try { raw = Setting(id); } catch { }
            if (raw == null) raw = Defaults[(int)id];
            var have = Cache[(int)id];
            if (have != null && have.Raw == raw) return have;
            var c = Parse(raw);
            if (c == null)
            {
                Log.Warn($"Tecla no reconocida en MCM ({id}): «{raw}». Se usa {Defaults[(int)id]}.");
                c = Parse(Defaults[(int)id]);
                c.Raw = raw;
            }
            Cache[(int)id] = c;
            return c;
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
            if (c.Off || !Input.IsKeyPressed(c.Key) || !ModsDown(c)) return false;
            for (int i = 0; i < Count; i++)
            {
                if (i == (int)id) continue;
                var o = Get((Id)i);
                if (o.Off || o.Key != c.Key || o.Mods == c.Mods || (o.Mods & c.Mods) != c.Mods) continue;
                if (ModsDown(o)) return false;
            }
            return true;
        }

        /// <summary>How the shortcut reads now ("Alt+T"), or "-" when it is turned off.</summary>
        public static string Label(Id id) { var c = Get(id); return c.Off ? "-" : c.Label; }

        private static readonly Regex DefaultLabels = new Regex(@"Ctrl\+Alt\+X|Ctrl\+X|Alt\+[TGBYP]", RegexOptions.Compiled);

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
