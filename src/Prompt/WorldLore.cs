using System;
using System.IO;
using AILegacy.Core;

namespace AILegacy.Prompt
{
    /// <summary>Free-form world text: the campaign's own world.txt if it has one, else Modules\AILegacy\world.txt. Reloaded when the file changes.</summary>
    internal static class WorldLore
    {
        private static string _text = "";
        private static DateTime _stamp = DateTime.MinValue;
        private static string _path;

        private static string FilePath => World.WorldFiles.Resolve(World.WorldFiles.World);

        public static string Text
        {
            get
            {
                try
                {
                    var path = FilePath;
                    if (!File.Exists(path)) return "";
                    var s = File.GetLastWriteTimeUtc(path);
                    if (s != _stamp || path != _path)
                    {
                        _text = File.ReadAllText(path).Trim();
                        _stamp = s; _path = path;
                        Log.Info($"world.txt cargado ({_text.Length} caracteres)");
                    }
                }
                catch (Exception ex) { Log.Error("No se pudo leer world.txt", ex); }
                return _text;
            }
        }
    }
}
