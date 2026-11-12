using System.IO;
using System.Reflection;

namespace AILegacy.Core
{
    internal static class Paths
    {
        // ...\Modules\AILegacy\bin\Win64_Shipping_Client\AILegacy.dll -> ...\Modules\AILegacy
        public static readonly string ModuleDir =
            Path.GetFullPath(Path.Combine(Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location), "..", ".."));

        public static string ConfigFile => Path.Combine(ModuleDir, "config.json");
        public static string LogDir => Path.Combine(ModuleDir, "logs");
    }
}
