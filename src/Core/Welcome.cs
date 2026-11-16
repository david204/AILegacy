using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using AILegacy.Config;
using AILegacy.Llm;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameState;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.Core
{
    /// <summary>
    /// A short welcome in the message log, once each time a campaign is started or loaded (MCM 1. AI › Welcome message).
    /// With no usable AI provider: where to set one up. With providers: the campaign rules changed from their defaults,
    /// then the same connection test as the MCM Test button, for every active provider.
    /// It waits for the campaign map to be on screen, so it isn't lost behind the loading screen or character creation,
    /// and nothing of it blocks the main thread: the wait runs on timers and the test requests in the background.
    /// </summary>
    internal static class Welcome
    {
        /// <summary>Group id of the campaign rules in MCM: every setting in that group is a rule.</summary>
        private const string RulesGroupId = "AIL_e7b1c74f";

        private static int _generation;

        /// <summary>Called on every session launch (new campaign or loaded save).</summary>
        public static void Arm()
        {
            int gen = Interlocked.Increment(ref _generation);
            try { if (!S.Get.WelcomeMessage) return; } catch { }
            Later(gen, 0, 0);
        }

        /// <summary>Checks again in a second, on the main thread.</summary>
        private static void Later(int gen, int onMap, int tries)
        {
            Task.Delay(1000).ContinueWith(_ => MainThread.Post(() => Check(gen, onMap, tries + 1)));
        }

        private static void Check(int gen, int onMap, int tries)
        {
            // Another campaign was loaded meanwhile, back at the main menu, or waited too long (half an hour)
            if (gen != _generation || Campaign.Current == null || tries > 1800) return;
            try
            {
                bool map = GameStateManager.Current?.ActiveState is MapState;
                // A couple of seconds on the map first, so it comes after the game's own load messages
                if (!map) { Later(gen, 0, tries); return; }
                if (onMap < 2) { Later(gen, onMap + 1, tries); return; }
                Show();
            }
            catch (Exception ex) { Log.Error("Welcome message", ex); }
        }

        private static void Show()
        {
            bool any;
            try { any = LegacyConfig.Current.Providers.Any(p => p.IsUsable); } catch { any = false; }
            if (!any)
            {
                Say(L.T("AIL_e7b1d108", "¡Bienvenido a AI Legacy! Para que los personajes hablen, pon un proveedor de IA en Mod Options › AI Legacy › 0. Claves de IA. Las reglas de la partida están en 2. Reglas de la partida: como ya son ajustes, tu texto del mundo puede ser solo ambientación."),
                    Colors.Yellow);
                Log.Info("Welcome: no usable AI provider set up");
                return;
            }
            var changed = ChangedRules();
            if (changed.Count == 0) Say(L.T("AIL_e7b1d109", "¡Bienvenido a AI Legacy!"), Colors.Cyan);
            else Say(L.F("AIL_e7b1d10a", $"¡Bienvenido a AI Legacy! Reglas de la partida que has cambiado: {string.Join("; ", changed)}."), Colors.Cyan);
            Log.Info("Welcome: campaign rules changed from their defaults: " + (changed.Count == 0 ? "none" : string.Join("; ", changed)));
            LlmClient.TestAllProviders(onlyActive: true);
        }

        private static void Say(string text, Color c) => InformationManager.DisplayMessage(new InformationMessage(text, c));

        /// <summary>"Name: value" for every campaign rule whose value differs from its default.</summary>
        private static List<string> ChangedRules()
        {
            var list = new List<string>();
            try
            {
                var now = S.Get;
                var defaults = new AILegacySettings();
                foreach (var prop in typeof(AILegacySettings).GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    string group = null, name = null;
                    foreach (var a in prop.GetCustomAttributes(true))
                    {
                        var t = a.GetType();
                        if (group == null) group = t.GetProperty("GroupName")?.GetValue(a) as string;
                        if (name == null) name = t.GetProperty("DisplayName")?.GetValue(a) as string;
                    }
                    if (group == null || name == null || group.IndexOf(RulesGroupId, StringComparison.Ordinal) < 0) continue;
                    var type = prop.PropertyType;
                    if (type != typeof(bool) && type != typeof(int) && type != typeof(float) && type != typeof(string)) continue;
                    object v = prop.GetValue(now), d = prop.GetValue(defaults);
                    if (Equals(v, d)) continue;
                    string value = v is bool b ? (b ? L.T("AIL_e7b1d10b", "sí") : L.T("AIL_e7b1d10c", "no")) : Convert.ToString(v, System.Globalization.CultureInfo.CurrentCulture);
                    list.Add(ShortName(name) + ": " + value);
                }
            }
            catch (Exception ex) { Log.Error("Welcome: reading the campaign rules", ex); }
            return list;
        }

        /// <summary>The setting's name in the menu language, without a "House rule:" style prefix or a trailing "(0 = ...)".</summary>
        private static string ShortName(string raw)
        {
            string text = raw;
            var m = Regex.Match(raw ?? "", @"^\{=(AIL_[0-9a-f]+)\}(.*)$", RegexOptions.Singleline);
            if (m.Success) text = L.ById_(m.Groups[1].Value) ?? m.Groups[2].Value;
            text = Regex.Replace(text ?? "", @"\s*[(（][^()（）]*[)）]\s*$", "").Trim();
            int colon = text.IndexOfAny(new[] { ':', '：' });
            if (colon > 0 && colon < 40 && text.Length - colon > 12) text = text.Substring(colon + 1).Trim();
            if (text.Length > 0) text = char.ToUpper(text[0]) + text.Substring(1);
            return text;
        }
    }
}
