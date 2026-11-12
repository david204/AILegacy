using System;
using System.IO;
using System.Text;

namespace AILegacy.Core
{
    internal static class Log
    {
        private static readonly object Gate = new object();
        private static bool _started;

        // Fuera del juego (banco de pruebas) se escribe en otro archivo, para no pisar el log de la partida en curso
        private static readonly bool InGame = System.Diagnostics.Process.GetCurrentProcess().ProcessName.IndexOf("TaleWorlds", StringComparison.OrdinalIgnoreCase) >= 0
                                              || System.Diagnostics.Process.GetCurrentProcess().ProcessName.IndexOf("Bannerlord", StringComparison.OrdinalIgnoreCase) >= 0;
        private static string LogFile => Path.Combine(Paths.LogDir, InGame ? "ailegacy.log" : "banco_de_pruebas.log");

        public static void Info(string msg) => Write("INFO", msg);
        public static void Warn(string msg) => Write("WARN", msg);
        // El mismo error, repetido, no llena el log: se apuntan las primeras veces y luego solo un recuento de vez en cuando.
        private static readonly System.Collections.Generic.Dictionary<string, int> Seen = new System.Collections.Generic.Dictionary<string, int>();
        private const int FullTimes = 3;

        public static void Error(string msg, Exception ex = null)
        {
            string key = msg + "|" + (ex == null ? "" : ex.GetType().Name + "|" + FirstFrame(ex));
            int n;
            lock (Gate) { Seen.TryGetValue(key, out n); Seen[key] = ++n; if (Seen.Count > 500) Seen.Clear(); }
            if (n <= FullTimes) { Write("ERROR", ex == null ? msg : msg + " :: " + ex); return; }
            if (n == FullTimes + 1) { Write("ERROR", $"{msg} :: se repite ({ex?.GetType().Name}); a partir de ahora solo se apunta el recuento"); return; }
            // 10, 100, 1000… veces
            if (n == 10 || n == 100 || n == 1000 || n % 10000 == 0) Write("ERROR", $"{msg} :: ya van {n} veces ({ex?.GetType().Name})");
        }

        private static string FirstFrame(Exception ex)
        {
            try { var st = ex.StackTrace ?? ""; int i = st.IndexOf('\n'); return i > 0 ? st.Substring(0, i).Trim() : st.Trim(); }
            catch { return ""; }
        }

        /// <summary>Guarda el último cuerpo de petición/respuesta para depurar.</summary>
        public static void Dump(string name, string content)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.LogDir);
                    File.WriteAllText(Path.Combine(Paths.LogDir, name), content ?? "", Encoding.UTF8);
                }
            }
            catch { /* el log nunca debe romper el juego */ }
        }

        private static void Write(string level, string msg)
        {
            try
            {
                lock (Gate)
                {
                    Directory.CreateDirectory(Paths.LogDir);
                    if (!_started)
                    {
                        _started = true;
                        // Un log por sesión; se guardan las 3 últimas (ailegacy.log, ailegacy.1.log, ailegacy.2.log),
                        // para que tras un cuelgue no se pierda el log de la sesión que falló
                        Rotate(LogFile, 2);
                        File.WriteAllText(LogFile, $"=== AI Legacy log {DateTime.Now:yyyy-MM-dd HH:mm:ss} ==={Environment.NewLine}", Encoding.UTF8);
                    }
                    File.AppendAllText(LogFile, $"[{DateTime.Now:HH:mm:ss}] {level} {msg}{Environment.NewLine}", Encoding.UTF8);
                }
            }
            catch { }
        }

        /// <summary>x.log → x.1.log → x.2.log… (se descarta el más viejo). Si algo falla, se sigue sin rotar.</summary>
        private static void Rotate(string file, int keepOld)
        {
            try
            {
                if (!File.Exists(file)) return;
                string dir = Path.GetDirectoryName(file), name = Path.GetFileNameWithoutExtension(file), ext = Path.GetExtension(file);
                string Old(int i) => Path.Combine(dir, $"{name}.{i}{ext}");
                if (File.Exists(Old(keepOld))) File.Delete(Old(keepOld));
                for (int i = keepOld - 1; i >= 1; i--)
                    if (File.Exists(Old(i))) File.Move(Old(i), Old(i + 1));
                File.Move(file, Old(1));
            }
            catch { }
        }
    }
}
