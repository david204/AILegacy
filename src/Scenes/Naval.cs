using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using AILegacy.Core;
using TaleWorlds.CampaignSystem.Naval;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;
using TaleWorlds.MountAndBlade.View.MissionViews;

namespace AILegacy.Scenes
{
    /// <summary>
    /// The War Sails expansion reached by reflection, so the mod loads and runs the same without it: its mission state, the ship
    /// logics, a ship's deck frames, anchoring, the sail wind profile and drowning. Nothing here is touched unless it is installed.
    /// </summary>
    internal static class Naval
    {
        private const BindingFlags Any = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
        private static Assembly _asm, _view;

        public const string StateType = "NavalDLC.NavalMissionState";
        public const string ShipsLogicType = "NavalDLC.Missions.MissionLogics.NavalShipsLogic";
        public const string FloatsamLogicType = "NavalDLC.Missions.MissionLogics.NavalFloatsamLogic";
        public const string AgentsLogicType = "NavalDLC.Missions.MissionLogics.NavalAgentsLogic";
        public const string WavesLogicType = "NavalDLC.Missions.MissionLogics.WaveParametersComputerLogic";
        public const string WindType = "NavalDLC.Missions.ShipActuators.SailWindProfile";
        public const string ControllerType = "NavalDLC.Missions.ShipControl.ShipControllerType";
        public const string AgentComponentType = "NavalDLC.Missions.AgentNavalComponent";
        public const string PrepareViewType = "NavalDLC.View.MissionViews.NavalMissionPrepareView";

        /// <summary>The expansion's assemblies are loaded (its module is on in the launcher).</summary>
        public static bool Installed
        {
            get
            {
                if (_asm == null || _view == null)
                    foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        string n;
                        try { n = a.GetName().Name; } catch { continue; }
                        if (n == "NavalDLC") _asm = a;
                        else if (n == "NavalDLC.View") _view = a;
                    }
                return _asm != null && Find(StateType) != null;
            }
        }

        private static Type Find(string name) { try { return _asm?.GetType(name, false) ?? _view?.GetType(name, false); } catch { return null; } }

        private static MethodInfo Method(Type t, string name, int args) =>
            t?.GetMethods(Any).FirstOrDefault(m => m.Name == name && m.GetParameters().Length == args);

        private static object Call(object o, string name, params object[] args)
        {
            var m = Method(o?.GetType(), name, args.Length) ?? throw new MissingMethodException(o?.GetType().FullName, name);
            return m.Invoke(o, args);
        }

        /// <summary>Opens a mission in the expansion's mission state (its screen, its water), like its own sea battles.</summary>
        public static Mission OpenMission(string name, MissionInitializerRecord rec, InitializeMissionBehaviorsDelegate handler)
        {
            var m = Find(StateType)?.GetMethod("OpenNew", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(MissionInitializerRecord), typeof(InitializeMissionBehaviorsDelegate), typeof(bool), typeof(bool) }, null)
                ?? throw new MissingMethodException(StateType, "OpenNew");
            return (Mission)m.Invoke(null, new object[] { name, rec, handler, true, true });
        }

        /// <summary>One of the expansion's mission behaviours, built with its empty constructor.</summary>
        public static MissionBehavior New(string type) => (MissionBehavior)Activator.CreateInstance(Find(type) ?? throw new TypeLoadException(type));

        public static MissionView PrepareView() { try { var t = Find(PrepareViewType); return t == null ? null : (MissionView)Activator.CreateInstance(t); } catch { return null; } }

        public static MissionBehavior ShipsLogic(Mission m) => m?.MissionBehaviors.FirstOrDefault(b => b.GetType().FullName == ShipsLogicType);

        /// <summary>Puts one of the party's ships on the water at that frame (the nearest free spot), anchored, in that formation.</summary>
        public static object SpawnShip(MissionBehavior shipsLogic, Ship ship, MatrixFrame frame, Team team, Formation formation)
        {
            var m = shipsLogic.GetType().GetMethods(Any).FirstOrDefault(x => x.Name == "SpawnShip" && x.GetParameters().Length == 7)
                    ?? throw new MissingMethodException(ShipsLogicType, "SpawnShip");
            return m.Invoke(shipsLogic, new object[] { ship, frame, team, formation, true, FormationClass.NumberOfRegularFormations, true });
        }

        /// <summary>The ship stays where it is: anchored in place, nobody steering, no orders to its crew.</summary>
        public static void Hold(object missionShip)
        {
            if (missionShip == null) return;
            try { Call(missionShip, "SetShipOrderActive", false); } catch (Exception ex) { Log.Warn("[deck] ship orders: " + ex.Message); }
            try { Call(missionShip, "SetController", Enum.ToObject(Find(ControllerType), 0), false); } catch (Exception ex) { Log.Warn("[deck] ship controller: " + ex.Message); }
            try { Call(missionShip, "SetAnchor", true, true, 1f); } catch (Exception ex) { Log.Warn("[deck] anchor: " + ex.Message); }
        }

        public static MatrixFrame ShipFrame(object missionShip)
        {
            try { return (MatrixFrame)missionShip.GetType().GetProperty("GlobalFrame", Any).GetValue(missionShip); } catch { return MatrixFrame.Identity; }
        }

        public static MatrixFrame CaptainFrame(object missionShip) => (MatrixFrame)Call(missionShip, "GetCaptainSpawnGlobalFrame");

        /// <summary>The ship's deck places (outer and inner), each once: the game hands them round in a ring.</summary>
        public static List<MatrixFrame> DeckFrames(object missionShip, int max = 60)
        {
            var list = new List<MatrixFrame>();
            try
            {
                for (int i = 0; i < max; i++)
                {
                    var f = (MatrixFrame)Call(missionShip, "GetNextOuterInnerSpawnGlobalFrame");
                    if (list.Count > 0 && list.Any(x => x.origin.DistanceSquared(f.origin) < 0.01f)) break;
                    list.Add(f);
                }
            }
            catch (Exception ex) { Log.Warn("[deck] deck places: " + ex.Message); }
            return list;
        }

        public static bool WindReady
        {
            get { try { return (bool)Find(WindType).GetProperty("IsSailWindProfileInitialized", Any).GetValue(null); } catch { return true; } }
        }

        public static void StartWind() { try { if (!WindReady) Method(Find(WindType), "InitializeProfile", 0).Invoke(null, null); } catch (Exception ex) { Log.Warn("[deck] wind profile: " + ex.Message); } }

        public static void EndWind() { try { if (WindReady) Method(Find(WindType), "FinalizeProfile", 0).Invoke(null, null); } catch (Exception ex) { Log.Warn("[deck] wind profile end: " + ex.Message); } }

        /// <summary>Whoever falls in the water swims: nobody drowns while on deck.</summary>
        public static void NoDrowning(Agent a)
        {
            try
            {
                var c = a?.Components.FirstOrDefault(x => x.GetType().FullName == AgentComponentType);
                if (c != null) Call(c, "SetCanDrown", false);
            }
            catch { }
        }

        /// <summary>What of the expansion this mod needs and cannot find (empty when all is there); for the offline checks.</summary>
        public static List<string> Missing()
        {
            var miss = new List<string>();
            if (!Installed) { miss.Add("NavalDLC assembly"); return miss; }
            void Need(bool ok, string what) { if (!ok) miss.Add(what); }
            var st = Find(StateType);
            Need(st?.GetMethod("OpenNew", BindingFlags.Public | BindingFlags.Static, null,
                new[] { typeof(string), typeof(MissionInitializerRecord), typeof(InitializeMissionBehaviorsDelegate), typeof(bool), typeof(bool) }, null) != null, "NavalMissionState.OpenNew");
            foreach (var t in new[] { ShipsLogicType, FloatsamLogicType, AgentsLogicType, WavesLogicType })
                Need(Find(t)?.GetConstructor(Type.EmptyTypes) != null && typeof(MissionBehavior).IsAssignableFrom(Find(t)), t);
            Need(Find(ShipsLogicType)?.GetMethods(Any).Any(x => x.Name == "SpawnShip" && x.GetParameters().Length == 7 && x.GetParameters()[0].ParameterType.IsAssignableFrom(typeof(Ship))) == true, "SpawnShip(IShipOrigin…)");
            var ship = Find("NavalDLC.Missions.Objects.MissionShip");
            foreach (var (n, c) in new[] { ("SetShipOrderActive", 1), ("SetController", 2), ("SetAnchor", 3), ("GetCaptainSpawnGlobalFrame", 0), ("GetNextOuterInnerSpawnGlobalFrame", 0) })
                Need(Method(ship, n, c) != null, "MissionShip." + n);
            Need(ship?.GetProperty("GlobalFrame", Any) != null, "MissionShip.GlobalFrame");
            Need(Find(ControllerType) != null && Enum.GetName(Find(ControllerType), 0) == "None", "ShipControllerType.None");
            Need(Find(WindType)?.GetProperty("IsSailWindProfileInitialized", Any) != null && Method(Find(WindType), "InitializeProfile", 0) != null && Method(Find(WindType), "FinalizeProfile", 0) != null, "SailWindProfile");
            Need(Method(Find(AgentComponentType), "SetCanDrown", 1) != null, "AgentNavalComponent.SetCanDrown");
            Need(Find(PrepareViewType) != null && typeof(MissionView).IsAssignableFrom(Find(PrepareViewType)), "NavalMissionPrepareView");
            return miss;
        }
    }
}
