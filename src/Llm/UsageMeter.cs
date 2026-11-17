using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using AILegacy.Core;
using Newtonsoft.Json;

namespace AILegacy.Llm
{
    /// <summary>
    /// Tracks requests, tokens (input, output, cached) and estimated cost per provider and task. Session totals stay in
    /// memory; spend per real-world day is saved to logs\spending.json for the daily cap. Cost is only known if the provider
    /// has a price set (in MCM or config.json).
    /// </summary>
    public static class UsageMeter
    {
        public class Row
        {
            [JsonProperty("provider")] public string Provider;
            [JsonProperty("task")] public string Task;
            [JsonProperty("calls")] public int Calls;
            [JsonProperty("failed")] public int Failed;
            [JsonProperty("in")] public long In;
            [JsonProperty("cached")] public long Cached;
            [JsonProperty("out")] public long Out;
            [JsonProperty("usd")] public double Usd;
            [JsonProperty("seconds")] public double Seconds;
        }

        private static readonly object Gate = new object();
        private static readonly Dictionary<string, Row> Rows = new Dictionary<string, Row>();
        private static Dictionary<string, double> _days;        // "2026-11-12" -> USD
        private static int _sinceWrite;

        private static string DayKey => DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        private static string FilePath
        {
            get
            {
                string now = Path.Combine(Paths.LogDir, "spending.json"), old = Path.Combine(Paths.LogDir, "gasto.json");
                try { if (File.Exists(old) && !File.Exists(now)) File.Move(old, now); } catch { }
                return now;
            }
        }

        private static Dictionary<string, double> Days
        {
            get
            {
                if (_days != null) return _days;
                try { if (File.Exists(FilePath)) _days = JsonConvert.DeserializeObject<Dictionary<string, double>>(File.ReadAllText(FilePath)); }
                catch { }
                return _days ?? (_days = new Dictionary<string, double>());
            }
        }

        public static void Record(string provider, string task, bool ok, int tokensIn, int tokensCached, int tokensOut, double seconds, double priceIn, double priceOut)
        {
            try
            {
                double usd = (tokensIn * priceIn + tokensOut * priceOut) / 1000000.0;
                lock (Gate)
                {
                    string key = provider + "|" + task;
                    if (!Rows.TryGetValue(key, out var r)) Rows[key] = r = new Row { Provider = provider, Task = task };
                    r.Calls++;
                    if (!ok) r.Failed++;
                    r.In += tokensIn; r.Cached += tokensCached; r.Out += tokensOut; r.Usd += usd; r.Seconds += seconds;
                    if (usd > 0)
                    {
                        Days.TryGetValue(DayKey, out double d);
                        Days[DayKey] = d + usd;
                    }
                    if (++_sinceWrite >= 10 && usd > 0) WriteDays();
                }
            }
            catch { }
        }

        private static void WriteDays()
        {
            _sinceWrite = 0;
            try
            {
                Directory.CreateDirectory(Paths.LogDir);
                foreach (var k in Days.Keys.OrderByDescending(k => k).Skip(60).ToList()) Days.Remove(k);
                File.WriteAllText(FilePath, JsonConvert.SerializeObject(Days, Formatting.Indented));
            }
            catch { }
        }

        /// <summary>USD spent today (real-world date) at the configured prices.</summary>
        public static double SpentToday { get { lock (Gate) { Days.TryGetValue(DayKey, out double d); return d; } } }

        public static void Flush() { lock (Gate) WriteDays(); }

        /// <summary>This session only.</summary>
        public static List<Row> Snapshot()
        {
            lock (Gate) return Rows.Values.Select(r => new Row { Provider = r.Provider, Task = r.Task, Calls = r.Calls, Failed = r.Failed, In = r.In, Cached = r.Cached, Out = r.Out, Usd = r.Usd, Seconds = r.Seconds })
                                   .OrderByDescending(r => r.In + r.Out).ToList();
        }

        public static string Summary()
        {
            var rows = Snapshot();
            if (rows.Count == 0) return "sin peticiones";
            long tin = rows.Sum(r => r.In), tc = rows.Sum(r => r.Cached), tout = rows.Sum(r => r.Out);
            return $"{rows.Sum(r => r.Calls)} peticiones, {tin} tokens de entrada ({(tin > 0 ? tc * 100 / tin : 0)} % de caché), {tout} de salida, {rows.Sum(r => r.Usd).ToString("0.0000", CultureInfo.InvariantCulture)} $ estimados; hoy {SpentToday.ToString("0.0000", CultureInfo.InvariantCulture)} $";
        }
    }
}
