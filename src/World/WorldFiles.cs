using System;
using System.IO;
using AILegacy.Core;
using AILegacy.Memory;
using TaleWorlds.CampaignSystem;

namespace AILegacy.World
{
    /// <summary>
    /// The editable world files: the world description, facts, secrets, archetypes, religions and traditions.
    /// The module holds the defaults (world.txt and world_data\). A campaign can carry its own copy of any of them in
    /// save_data\[campaign]\world_data\, and that copy wins while it exists. Only the names listed here are reachable.
    /// </summary>
    internal static class WorldFiles
    {
        public const string World = "world.txt";
        public static readonly string[] Names = { World, "info.json", "secrets.json", "archetypes.json", "religion.json", "cultural_traditions.json", "voices.json" };

        public static bool IsKnown(string name) => Array.IndexOf(Names, name) >= 0;

        public static string DefaultPath(string name) =>
            name == World ? Path.Combine(Paths.ModuleDir, name) : Path.Combine(Paths.ModuleDir, "world_data", name);

        /// <summary>This campaign's folder for its own copies, or null with no campaign loaded.</summary>
        public static string CampaignDir
        {
            get
            {
                try
                {
                    var id = Campaign.Current?.UniqueGameId;
                    return string.IsNullOrEmpty(id) ? null : Path.Combine(SaveFiles.DirFor(id), "world_data");
                }
                catch { return null; }
            }
        }

        public static string CampaignPath(string name)
        {
            var dir = CampaignDir;
            return dir == null || !IsKnown(name) ? null : Path.Combine(dir, name);
        }

        public static bool HasOwn(string name)
        {
            try { var p = CampaignPath(name); return p != null && File.Exists(p); } catch { return false; }
        }

        /// <summary>The file the game reads right now: the campaign's copy if there is one, the module's otherwise.</summary>
        public static string Resolve(string name) => HasOwn(name) ? CampaignPath(name) : DefaultPath(name);
    }
}
