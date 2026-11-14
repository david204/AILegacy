using System;
using System.IO;
using AILegacy.Core;

namespace AILegacy.Prompt
{
    /// <summary>Free-form world text from Modules\AILegacy\world.txt. Reloaded when the file changes.</summary>
    internal static class WorldLore
    {
        private static string _text = "";
        private static DateTime _stamp = DateTime.MinValue;

        private static string FilePath => Path.Combine(Paths.ModuleDir, "world.txt");

        public static string Text
        {
            get
            {
                try
                {
                    if (!File.Exists(FilePath)) return "";
                    var s = File.GetLastWriteTimeUtc(FilePath);
                    if (s != _stamp)
                    {
                        _text = File.ReadAllText(FilePath).Trim();
                        _stamp = s;
                        Log.Info($"world.txt cargado ({_text.Length} caracteres)");
                    }
                }
                catch (Exception ex) { Log.Error("No se pudo leer world.txt", ex); }
                return _text;
            }
        }
    }
}
