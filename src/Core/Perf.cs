using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Text;
using AILegacy.Settings;
using HarmonyLib;
using TaleWorlds.Library;

namespace AILegacy.Core
{
    /// <summary>
    /// Performance meter. While the MCM option is on it times the mod's per-frame entry points and every Harmony patch
    /// method of the mod, and writes a summary sorted by total time to the log every 60 seconds and when the console
    /// command "ailegacy.perf" is run. Off, a tick entry costs one bool check and the patches are not touched at all.
    /// </summary>
    internal static class Perf
    {
        private const string HarmonyId = "com.ailegacy.perf";
        private const double ReportEverySeconds = 60;

        /// <summary>Read on every tick entry. Only changed from the game thread.</summary>
        public static volatile bool On;

        private class Stat
        {
            public long Calls, Ticks, Max;
        }

        // Patches can run on the game's worker threads (model calculations), so the table is locked
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Stat> Stats = new Dictionary<string, Stat>();
        private static readonly Dictionary<MethodBase, string> Names = new Dictionary<MethodBase, string>();
        private static Harmony _harmony;
        private static bool _instrumented;
        private static long _windowStart;
        private static int _patchCount;

        /// <summary>Start of a timed section: 0 when the meter is off.</summary>
        public static long Begin() => On ? Stopwatch.GetTimestamp() : 0L;

        public static void End(string name, long start)
        {
            if (start == 0L) return;
            Add(name, Stopwatch.GetTimestamp() - start);
        }

        private static void Add(string name, long ticks)
        {
            lock (Gate)
            {
                if (!Stats.TryGetValue(name, out var s)) Stats[name] = s = new Stat();
                s.Calls++;
                s.Ticks += ticks;
                if (ticks > s.Max) s.Max = ticks;
            }
        }

        /// <summary>Once a second from the game thread: follows the MCM option and writes the periodic summary.</summary>
        public static void Pump()
        {
            try
            {
                bool want = S.Get.PerformanceMeter;
                if (want != On)
                {
                    if (want) { Reset(); Instrument(); On = true; Log.Info($"[rendimiento] medidor encendido: {_patchCount} métodos de parche medidos"); }
                    else { On = false; Uninstrument(); Log.Info("[rendimiento] medidor apagado"); }
                }
                if (!On) return;
                double seconds = (Stopwatch.GetTimestamp() - _windowStart) / (double)Stopwatch.Frequency;
                if (seconds < ReportEverySeconds) return;
                Log.Info(Summary(seconds));
                Reset();
            }
            catch (Exception ex) { Log.Error("Medidor de rendimiento", ex); }
        }

        private static void Reset()
        {
            lock (Gate) Stats.Clear();
            _windowStart = Stopwatch.GetTimestamp();
        }

        /// <summary>Table of what was measured since the last periodic summary, slowest first.</summary>
        public static string Summary(double seconds = -1)
        {
            if (seconds < 0) seconds = (Stopwatch.GetTimestamp() - _windowStart) / (double)Stopwatch.Frequency;
            List<KeyValuePair<string, Stat>> rows;
            lock (Gate) rows = Stats.Select(kv => new KeyValuePair<string, Stat>(kv.Key, new Stat { Calls = kv.Value.Calls, Ticks = kv.Value.Ticks, Max = kv.Value.Max })).ToList();
            double ms = 1000.0 / Stopwatch.Frequency;
            var sb = new StringBuilder();
            sb.AppendLine($"[rendimiento] últimos {seconds:0} s, {rows.Count} puntos medidos, ordenados por tiempo total");
            sb.AppendLine("    total ms |   llamadas |  media ms |    máx ms | punto");
            foreach (var r in rows.OrderByDescending(x => x.Value.Ticks).Take(40))
                sb.AppendLine($"  {r.Value.Ticks * ms,10:0.00} | {r.Value.Calls,10} | {r.Value.Ticks * ms / Math.Max(1, r.Value.Calls),9:0.0000} | {r.Value.Max * ms,9:0.000} | {r.Key}");
            double total = rows.Where(r => r.Key.StartsWith("tick:")).Sum(r => r.Value.Ticks) * ms;
            sb.Append($"  trabajo por fotograma del mod: {total:0.0} ms en total ({(seconds > 0 ? total / (seconds * 10) : 0):0.00} % del tiempo)");
            return sb.ToString();
        }

        // ------------------------------------------------------------ patches

        /// <summary>
        /// Wraps the mod's plain prefixes, postfixes and finalizers with a timing pair. Done through Harmony on the
        /// patch methods themselves, so a patch added later is measured without anyone remembering to time it.
        /// Transpilers, patch methods shared by several targets and classes that use TargetMethod(s) are left alone.
        /// </summary>
        private static void Instrument()
        {
            if (_instrumented) return;
            _instrumented = true;
            _patchCount = 0;
            try
            {
                _harmony = _harmony ?? new Harmony(HarmonyId);
                var own = typeof(Perf).Assembly;
                var pre = new HarmonyMethod(typeof(Perf).GetMethod(nameof(PatchBegin), BindingFlags.Static | BindingFlags.NonPublic));
                var post = new HarmonyMethod(typeof(Perf).GetMethod(nameof(PatchEnd), BindingFlags.Static | BindingFlags.NonPublic));
                // First every use of each patch method is gathered: only a method that is a plain prefix, postfix or
                // finalizer of exactly one target gets wrapped
                var targets = new Dictionary<MethodInfo, HashSet<MethodBase>>();
                var skip = new HashSet<MethodInfo>();
                foreach (var target in Harmony.GetAllPatchedMethods().ToList())
                {
                    var info = Harmony.GetPatchInfo(target);
                    if (info == null) continue;
                    foreach (var p in info.Transpilers) if (p?.PatchMethod != null) skip.Add(p.PatchMethod);
                    foreach (var p in info.Prefixes.Concat(info.Postfixes).Concat(info.Finalizers))
                    {
                        var m = p?.PatchMethod;
                        if (m == null || m.DeclaringType == null || m.DeclaringType.Assembly != own || m.DeclaringType == typeof(Perf)) continue;
                        if (!targets.TryGetValue(m, out var set)) targets[m] = set = new HashSet<MethodBase>();
                        set.Add(target);
                    }
                }
                foreach (var kv in targets)
                {
                    var m = kv.Key;
                    if (skip.Contains(m) || kv.Value.Count != 1 || !Plain(m)) continue;
                    var target = kv.Value.First();
                    try
                    {
                        _harmony.Patch(m, prefix: pre, finalizer: post);
                        lock (Gate) Names[m] = "parche:" + m.DeclaringType.Name + "." + m.Name + " -> " + (target.DeclaringType?.Name ?? "?") + "." + target.Name;
                        _patchCount++;
                    }
                    catch (Exception ex)
                    {
                        Log.Warn($"[rendimiento] no se puede medir {m.DeclaringType.Name}.{m.Name}: {ex.Message}");
                        // Nothing of the meter may stay on a patch it could not wrap whole
                        try { _harmony.Unpatch(m, HarmonyPatchType.All, HarmonyId); } catch { }
                    }
                }
            }
            catch (Exception ex)
            {
                Log.Error("Medidor de rendimiento: instrumentar parches", ex);
                try { _harmony?.UnpatchAll(HarmonyId); } catch { }
                _patchCount = 0;
            }
        }

        /// <summary>
        /// A patch method the meter may wrap: static, not generic, not a transpiler in shape, and from a class that
        /// names its target with attributes instead of working it out in TargetMethod(s).
        /// </summary>
        private static bool Plain(MethodInfo m)
        {
            try
            {
                if (!m.IsStatic || m.IsGenericMethod || m.ContainsGenericParameters || m.DeclaringType.ContainsGenericParameters) return false;
                if (typeof(System.Collections.IEnumerable).IsAssignableFrom(m.ReturnType) && m.ReturnType != typeof(string)) return false;
                const BindingFlags all = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;
                foreach (var other in m.DeclaringType.GetMethods(all))
                {
                    if (other.Name == "TargetMethods" || other.Name == "TargetMethod") return false;
                    if (other.GetCustomAttributes(typeof(HarmonyTargetMethods), false).Length > 0 || other.GetCustomAttributes(typeof(HarmonyTargetMethod), false).Length > 0) return false;
                }
                return true;
            }
            catch { return false; }
        }

        private static void Uninstrument()
        {
            if (!_instrumented) return;
            _instrumented = false;
            try { _harmony?.UnpatchAll(HarmonyId); }
            catch (Exception ex) { Log.Error("Medidor de rendimiento: retirar la medición", ex); }
        }

        private static void PatchBegin(out long __state) => __state = Stopwatch.GetTimestamp();

        // A finalizer runs even when the patch body throws, and returning nothing leaves the exception as it was
        private static void PatchEnd(MethodBase __originalMethod, long __state)
        {
            // An exception leaving a finalizer would replace the patch's own result
            try
            {
                if (__state == 0L || __originalMethod == null) return;
                long ticks = Stopwatch.GetTimestamp() - __state;
                string name;
                lock (Gate) { if (!Names.TryGetValue(__originalMethod, out name)) name = "parche:" + __originalMethod.Name; }
                Add(name, ticks);
            }
            catch { }
        }

        // ------------------------------------------------------------ console

        /// <summary>Console: ailegacy.perf prints the summary so far and copies it to the log.</summary>
        [CommandLineFunctionality.CommandLineArgumentFunction("perf", "ailegacy")]
        public static string Command(List<string> args)
        {
            try
            {
                if (!On) return "AI Legacy: el medidor de rendimiento está apagado. Enciéndelo en Mod Options > AI Legacy > 7. AI > Performance meter y espera unos segundos.";
                string text = Summary();
                Log.Info(text);
                return text;
            }
            catch (Exception ex) { return "AI Legacy: " + ex.Message; }
        }
    }
}
