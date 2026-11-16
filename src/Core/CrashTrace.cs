using System;
using System.Diagnostics;
using System.Runtime.ExceptionServices;
using System.Threading;

namespace AILegacy.Core
{
    /// <summary>
    /// Writes to the log every exception thrown while the player is making their character, with the full call
    /// stack, and any exception nobody catches. A report of the game closing on that screen without a word in any
    /// log is why this exists: the engine wraps the error and the crash report loses where it came from.
    /// </summary>
    internal static class CrashTrace
    {
        private const int MaxEntries = 40;
        private static int _written;
        [ThreadStatic] private static bool _inside;

        public static void Install()
        {
            try
            {
                AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;
                AppDomain.CurrentDomain.UnhandledException += OnUnhandled;
            }
            catch (Exception ex) { Log.Error("Crash trace", ex); }
            PatchStages();
        }

        private static void OnFirstChance(object sender, FirstChanceExceptionEventArgs e)
        {
            if (_inside || _written >= MaxEntries) return;
            _inside = true;
            try
            {
                if (!SubModule.InCharacterCreation) return;
                int n = Interlocked.Increment(ref _written);
                if (n > MaxEntries) return;
                var ex = e.Exception;
                // At this point the exception only knows the frame that threw it: the stack of the thread tells the rest
                string stack = new StackTrace(1, false).ToString();
                Log.Warn($"[character creation] exception {n}/{MaxEntries}: {ex.GetType().FullName}: {ex.Message}"
                         + Environment.NewLine + "  thrown at: " + (ex.StackTrace ?? "?").Trim()
                         + Environment.NewLine + "  call stack:" + Environment.NewLine + stack);
            }
            catch { }
            finally { _inside = false; }
        }

        /// <summary>
        /// One line per character creation stage, so the log says on which screen something went wrong. Patched on its
        /// own, not with the rest: if the game renamed the method, only this line is lost.
        /// </summary>
        private static void PatchStages()
        {
            try
            {
                var target = HarmonyLib.AccessTools.Method(typeof(TaleWorlds.CampaignSystem.CharacterCreationContent.CharacterCreationManager), "ActivateStage");
                if (target == null) { Log.Warn("Crash trace: the character creation stages are not followed (method not found)"); return; }
                new HarmonyLib.Harmony("com.ailegacy.crashtrace").Patch(target,
                    prefix: new HarmonyLib.HarmonyMethod(typeof(CrashTrace), nameof(StagePrefix)));
            }
            catch (Exception ex) { Log.Error("Crash trace: stages", ex); }
        }

        private static void StagePrefix(object __0)
        {
            try { Log.Info("[character creation] stage: " + (__0?.GetType().Name ?? "none")); }
            catch { }
        }

        private static void OnUnhandled(object sender, UnhandledExceptionEventArgs e)
        {
            if (_inside) return;
            _inside = true;
            try { Log.Error("Unhandled exception, the game is closing: " + e.ExceptionObject); }
            catch { }
            finally { _inside = false; }
        }
    }
}
