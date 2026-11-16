using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using SandBox;
using SandBox.Missions.MissionLogics;
using SandBox.Objects.Usables;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Settlements.Locations;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Keeps whoever walks with the player across the doors of a settlement (tavern, hall, streets...), and lets a
    /// character lead the player into another location. Uses the game's own list of characters accompanying the
    /// player, the one that brings companions into each scene. Only named heroes can cross: an agent with no hero
    /// behind it is a different person in every scene.
    /// </summary>
    internal static class SceneTravel
    {
        private class Entry
        {
            public LocationCharacter Lc;      // null: a nameless agent, who stays behind at the door
            public Agent Agent;               // their body in the current scene, if any
            public string Name;
            public bool Follows;              // keeps walking with the player after arriving
            public string Dest;               // location id they are leading the player to, or null
            public float Left;                // seconds left of the lead
            public Vec3 Door;
            public bool HasDoor, AtDoor;
            // the player lags behind or will not go through: stopped to wait, walking back for them, asking the AI how to react
            public bool Paused, Back, Asking;
            public float LagFor, DoorWait, NextReact;
            public int Reacts;
        }

        private static readonly Tpl THelp = new Tpl("scene/go_into.txt",
@" - ""go_into:<place>"": leads {player} on foot to another part of this settlement and goes in with them (they offered to show the way, or were asked to take {player} there). Only someone with a name of their own can do it, not a nameless passer-by. <place> must be one of: {places}.");

        public static void Touch() { var _ = THelp.Key; SceneActions.Touch(); }

        private static readonly List<Entry> Entries = new List<Entry>();
        private static readonly Dictionary<Agent, Hero> Promoted = new Dictionary<Agent, Hero>();
        private static Settlement _where;
        private static Mission _mission;

        public static void Reset() { Entries.Clear(); Promoted.Clear(); _where = null; _mission = null; }

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Gray));

        /// <summary>Walking to a door to take the player through it. Busy agents don't start chatting on their own.</summary>
        public static bool Leading(Agent a) => a != null && Entries.Any(e => e.Agent == a && e.Dest != null && e.HasDoor);

        /// <summary>A resident who became a named hero in this scene: from the next door on, the hero is who walks with you.</summary>
        public static void NotePromoted(Agent a, Hero h)
        {
            try
            {
                if (a == null || h == null) return;
                if (Mission.Current != _mission && Entries.Count == 0) Promoted.Clear();
                Promoted[a] = h;
                var e = Entries.FirstOrDefault(x => x.Agent == a && x.Lc == null);
                if (e == null) return;
                Entries.Remove(e);
                Follow(a);
            }
            catch (Exception ex) { Log.Error("Scene travel: new character", ex); }
        }

        private static Hero HeroOf(Agent a)
        {
            var h = (a?.Character as CharacterObject)?.HeroObject;
            if (h != null) return h;
            return a != null && Promoted.TryGetValue(a, out var p) && p != null && p.IsAlive ? p : null;
        }

        /// <summary>The game's record of that hero in this settlement, made if the game has none yet.</summary>
        private static LocationCharacter RecordOf(Agent a, Hero h)
        {
            var complex = LocationComplex.Current;
            if (complex == null || h == null) return null;
            LocationCharacter lc = null;
            if ((a.Character as CharacterObject)?.HeroObject == h) lc = complex.FindCharacter(a);
            lc = lc ?? complex.GetLocationCharacterOfHero(h);
            if (lc != null) return lc;
            var monster = TaleWorlds.Core.FaceGen.GetMonsterWithSuffix(h.CharacterObject.Race, "_settlement");
            var data = new AgentData(new TaleWorlds.CampaignSystem.AgentOrigins.SimpleAgentOrigin(h.CharacterObject)).Monster(monster).NoHorses(true);
            return new LocationCharacter(data, SandBoxManager.Instance.AgentBehaviorManager.AddWandererBehaviors, "npc_common", false,
                LocationCharacter.CharacterRelations.Neutral, null, true);
        }

        private static Entry Register(Agent a, bool follows)
        {
            var le = PlayerEncounter.LocationEncounter;
            if (!S.Get.FollowAcrossDoors || a == null || le == null || le.Settlement == null || CampaignMission.Current?.Location == null) return null;
            var h = HeroOf(a);
            if (h == null || h == Hero.MainHero || h.IsWounded) return null;
            var lc = RecordOf(a, h);
            if (lc == null) return null;
            // Already brought along by the game (a companion of yours): leave them to it
            if (le.GetAccompanyingCharacter(lc) != null || le.GetAccompanyingCharacter(lc.Character) != null) return null;
            if (_where != le.Settlement) { Entries.Clear(); _where = le.Settlement; }
            _mission = Mission.Current;
            le.AddAccompanyingCharacter(lc, true);
            var acc = le.GetAccompanyingCharacter(lc);
            if (acc == null) return null;
            // The cells are no place for a visitor: only the house that holds them, or your own people, go down with you
            bool cells = h.Clan != null && (h.Clan == le.Settlement.OwnerClan || h.Clan == Clan.PlayerClan);
            if (!cells) acc.DisallowEntranceToLocations(l => l != null && l.StringId == "prison");
            var e = new Entry { Lc = lc, Agent = a, Name = a.Name, Follows = follows };
            Entries.Add(e);
            return e;
        }

        /// <summary>Someone started walking with the player. Heroes will cross doors with them, the rest stop there.</summary>
        public static void Follow(Agent a)
        {
            try
            {
                if (a == null || !S.Get.FollowAcrossDoors) return;
                var e = Entries.FirstOrDefault(x => x.Agent == a);
                if (e != null) { e.Follows = true; return; }
                if (Register(a, true) != null) { Log.Info($"[scene] {a.Name} will also follow you through doors"); return; }
                var le = PlayerEncounter.LocationEncounter;
                if (le?.Settlement == null || HeroOf(a) != null) return;
                if (_where != le.Settlement) { Entries.Clear(); _where = le.Settlement; }
                _mission = Mission.Current;
                Entries.Add(new Entry { Agent = a, Name = a.Name, Follows = true });
            }
            catch (Exception ex) { Log.Error("Scene travel: follow", ex); }
        }

        /// <summary>They stop walking with the player (told to stay, sat down, left, went to guide).</summary>
        public static void Drop(Agent a)
        {
            try
            {
                foreach (var e in Entries.Where(x => x.Agent == a).ToList()) Remove(e);
            }
            catch (Exception ex) { Log.Error("Scene travel: release", ex); }
        }

        private static void Remove(Entry e)
        {
            Entries.Remove(e);
            try
            {
                if (e.HasDoor && e.Agent != null && e.Agent.IsActive()) { e.Agent.DisableScriptedMovement(); e.Agent.SetLookAgent(null); }
            }
            catch { }
            try { if (e.Lc != null) PlayerEncounter.LocationEncounter?.RemoveAccompanyingCharacter(e.Lc); } catch { }
        }

        // ============================================================ places and the way to them

        private static bool Open(Location l)
        {
            try { return l != null && l.GetSceneCount() > 0 && l.CanPlayerSee() && l.CanPlayerEnter(); }
            catch { return false; }
        }

        /// <summary>First location to step into on the way from one to the other, through doors the player may use. Null if there is no way.</summary>
        private static Location NextHop(Location from, Location to)
        {
            if (from == null || to == null || from == to) return null;
            var first = new Dictionary<Location, Location>();
            var queue = new Queue<Location>();
            foreach (var n in from.LocationsOfPassages)
            {
                if (n == null || n == from || first.ContainsKey(n) || !Open(n)) continue;
                first[n] = n; queue.Enqueue(n);
            }
            while (queue.Count > 0)
            {
                var cur = queue.Dequeue();
                if (cur == to) return first[cur];
                foreach (var n in cur.LocationsOfPassages)
                {
                    if (n == null || n == from || first.ContainsKey(n) || !Open(n)) continue;
                    first[n] = first[cur]; queue.Enqueue(n);
                }
            }
            return null;
        }

        private static List<Location> Reachable()
        {
            var list = new List<Location>();
            try
            {
                var here = CampaignMission.Current?.Location;
                var complex = LocationComplex.Current;
                if (here == null || complex == null || !S.Get.FollowAcrossDoors || Mission.Current?.GetMissionBehavior<MissionLocationLogic>() == null) return list;
                foreach (var l in complex.GetListOfLocations())
                    if (l != here && Open(l) && NextHop(here, l) != null) list.Add(l);
            }
            catch (Exception ex) { Log.Error("Scene travel: places", ex); }
            return list;
        }

        /// <summary>Places a character can lead the player to from this scene, as "id (name)". Empty when there are none.</summary>
        public static string Places()
        {
            return string.Join(", ", Reachable().Select(l => $"{l.StringId} ({l.Name})"));
        }

        /// <summary>The extra line for the list of scene actions, or "" when nobody can lead anywhere from here.</summary>
        public static string HelpLine(string player)
        {
            string places = Places();
            return places.Length == 0 ? "" : "\n" + THelp.Format("player", player, "places", places);
        }

        /// <summary>True if that agent could take the player through a door right now.</summary>
        public static bool CanLead(Agent a)
        {
            try { return S.Get.FollowAcrossDoors && a != null && HeroOf(a) != null && !HeroOf(a).IsWounded && Reachable().Count > 0; }
            catch { return false; }
        }

        private static Location FindPlace(string arg)
        {
            arg = (arg ?? "").Trim().Trim('<', '>', '"', '\'').Trim();
            int paren = arg.IndexOf('(');
            if (paren > 0) arg = arg.Substring(0, paren).Trim();
            if (arg.Length < 2) return null;
            var all = Reachable();
            return all.FirstOrDefault(l => string.Equals(l.StringId, arg, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(l => string.Equals(l.Name?.ToString(), arg, StringComparison.OrdinalIgnoreCase))
                   ?? all.FirstOrDefault(l => (l.Name?.ToString() ?? "").IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 || arg.IndexOf(l.StringId, StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>The character sets off to take the player into that place. Returns its name, or null if they can't.</summary>
        public static string GoInto(Agent who, string place)
        {
            try
            {
                var here = CampaignMission.Current?.Location;
                var dest = FindPlace(place);
                if (who == null || here == null || dest == null || NextHop(here, dest) == null) return null;
                var e = Entries.FirstOrDefault(x => x.Agent == who && x.Lc != null) ?? Register(who, false);
                if (e == null) return null;
                e.Dest = dest.StringId; e.Left = 300f;
                Lead(e);
                Log.Info($"[scene] {who.Name} takes the player to {dest.StringId}");
                return dest.Name?.ToString() ?? dest.StringId;
            }
            catch (Exception ex) { Log.Error("Scene travel: take the player", ex); return null; }
        }

        /// <summary>Sends them to the door of this scene that leads towards their destination. Without such a door they just walk with the player.</summary>
        private static void Lead(Entry e)
        {
            e.HasDoor = false; e.AtDoor = false; e.Paused = false; e.Back = false; e.LagFor = 0f; e.DoorWait = 0f;
            var a = e.Agent;
            var here = CampaignMission.Current?.Location;
            var dest = LocationComplex.Current?.GetLocationWithId(e.Dest);
            var hop = NextHop(here, dest);
            if (a == null || !a.IsActive() || hop == null) return;
            var handler = Mission.Current?.GetMissionBehavior<MissionAgentHandler>();
            var door = handler?.TownPassageProps?.OfType<Passage>().FirstOrDefault(p => p.ToLocation == hop && !p.IsDeactivated && p.PilotStandingPoint != null);
            if (door == null || a.MountAgent != null)
            {
                SceneActions.Adopt(a, true);
                return;
            }
            // Stand a step short of the door, on the player's side, so they don't block it
            var at = door.PilotStandingPoint.GameEntity.GlobalPosition;
            var back = Agent.Main != null ? (Agent.Main.Position - at).AsVec2 : Vec2.Zero;
            if (back.Length > 0.1f) { back = back.Normalized() * 1.4f; at += new Vec3(back.x, back.y); }
            SceneActions.Unfollow(a);
            try { if (a.IsUsingGameObject) a.StopUsingGameObject(); } catch { }
            var wp = new WorldPosition(Mission.Current.Scene, UIntPtr.Zero, at, false);
            a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun);
            e.Door = at; e.HasDoor = true;
        }

        private static void EndLead(Entry e, bool gaveUp)
        {
            e.Dest = null;
            var a = e.Agent;
            try { if (e.HasDoor && a != null && a.IsActive()) { a.DisableScriptedMovement(); a.SetLookAgent(null); } } catch { }
            e.HasDoor = false; e.AtDoor = false;
            if (gaveUp) Say(L.F("AIL_e7b1c643", $"{e.Name} se cansa de esperarte en la puerta."));
            if (!e.Follows) { Remove(e); if (a != null && a.IsActive()) SceneActions.Unfollow(a); }
            else if (a != null && a.IsActive()) SceneActions.Adopt(a, true);
        }

        // ============================================================ tick

        public static void Tick(float dt)
        {
            if (Entries.Count == 0) return;
            try
            {
                var le = Campaign.Current != null ? PlayerEncounter.LocationEncounter : null;
                if (le == null || le.Settlement != _where) { Entries.Clear(); Promoted.Clear(); _where = null; _mission = null; return; }      // left the settlement
                var m = Mission.Current;
                if (m == null || Agent.Main == null || !Agent.Main.IsActive()) return;
                if (m != _mission) { _mission = m; Promoted.Clear(); NewScene(m); return; }

                for (int i = Entries.Count - 1; i >= 0; i--)
                {
                    var e = Entries[i];
                    var a = e.Agent;
                    if (a == null) continue;      // waiting outside: they come back in at the next door
                    if (!a.IsActive())
                    {
                        if (a.State == AgentState.Killed || a.State == AgentState.Unconscious) Remove(e);
                        continue;
                    }
                    if (e.Dest == null) continue;
                    if ((e.Left -= dt) <= 0f) { EndLead(e, true); continue; }
                    if (!e.HasDoor && !e.Back) continue;
                    WaitForPlayer(e, a, dt, m.CurrentTime);
                    if (e.Dest == null || !e.HasDoor) continue;
                    if (!e.AtDoor && !e.Paused && a.Position.AsVec2.Distance(e.Door.AsVec2) <= 1.6f)
                    {
                        e.AtDoor = true;
                        try { a.SetLookAgent(Agent.Main); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Scene travel", ex); Entries.Clear(); }
        }

        /// <summary>
        /// The guide to a door keeps an eye on the player: they stop when the player falls behind and walk on when they
        /// catch up; when the player lags, walks off or stands at the door without going in, the guide reacts in
        /// character now and then (calls, insists, goes back for them) and after three times gives up.
        /// </summary>
        private static void WaitForPlayer(Entry e, Agent a, float dt, float now)
        {
            var me = Agent.Main;
            float d = a.Position.Distance(me.Position);
            if (e.Back)
            {
                if (d > 3.5f) return;
                // beside the player again: back to the door, to be followed there
                Lead(e);
                return;
            }
            if (!e.AtDoor)
            {
                if (!e.Paused && d > 10f)
                {
                    e.Paused = true; e.LagFor = 0f;
                    try { var wp = a.GetWorldPosition(); a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun); a.SetLookAgent(me); } catch { }
                    Log.Info($"[scene] {e.Name} stops: the player fell behind ({d:0.#} m)");
                }
                else if (e.Paused && d <= 6f)
                {
                    e.Paused = false; e.LagFor = 0f;
                    try { var wp = new WorldPosition(Mission.Current.Scene, UIntPtr.Zero, e.Door, false); a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun); a.SetLookAgent(null); } catch { }
                    Log.Info($"[scene] {e.Name} walks on to the door into {e.Dest}");
                }
                if (e.Paused) Lag(e, a, d, dt, now, false);
                return;
            }
            if (d > 10f) { e.DoorWait = 0f; Lag(e, a, d, dt, now, false); return; }
            e.LagFor = 0f;
            // at the door with the player close by, but they do not go in
            e.DoorWait += dt;
            if (e.DoorWait >= 15f) Lag(e, a, d, 0f, now, true);
        }

        private static void Lag(Entry e, Agent a, float d, float dt, float now, bool atDoor)
        {
            e.LagFor += dt;
            if (e.Asking || (!atDoor && e.LagFor < 5f) || now < e.NextReact) return;
            if (e.Reacts >= 3)
            {
                Log.Info($"[scene] {e.Name} gives up: the player would not follow");
                if (e.AtDoor) EndLead(e, true);
                else { Say(L.F("AIL_e7b1d007", $"{e.Name} se cansa de esperarte.")); EndLead(e, false); }
                return;
            }
            e.Reacts++;
            e.Asking = true;
            var place = LocationComplex.Current?.GetLocationWithId(e.Dest);
            string target = place?.Name?.ToString() ?? e.Dest;
            var tpl = atDoor ? SceneActions.TLeadDoor : d >= 25f ? SceneActions.TLeadAway : SceneActions.TLeadBehind;
            var ee = e;
            bool asked = SceneActions.React(a, tpl, atDoor ? SceneActions.TDoorChoices.Text : SceneActions.GuideChoices, choice =>
            {
                ee.Asking = false;
                if (!Entries.Contains(ee) || ee.Dest == null || ee.Agent == null || !ee.Agent.IsActive()) return;
                float t = Mission.Current?.CurrentTime ?? 0f;
                ee.NextReact = t + (atDoor ? 25f : 20f);
                ee.DoorWait = 0f;
                ee.Left = Math.Max(ee.Left, 60f);
                if (choice == "give_up")
                {
                    Say(L.F("AIL_e7b1d006", $"{ee.Name} deja de guiarte."));
                    Log.Info($"[scene] {ee.Name} gives up taking the player to {ee.Dest}");
                    EndLead(ee, false);
                    return;
                }
                if (choice == "go_back" && !ee.AtDoor && Agent.Main != null && ee.Agent.Position.Distance(Agent.Main.Position) > 5f)
                {
                    try { ee.Agent.DisableScriptedMovement(); } catch { }
                    if (SceneMove.GoTo(ee.Agent, Agent.Main))
                    {
                        ee.Back = true; ee.HasDoor = false; ee.Paused = false;
                        Say(L.F("AIL_e7b1d008", $"{ee.Name} vuelve a por ti."));
                        Log.Info($"[scene] {ee.Name} goes back to the player who stopped following");
                    }
                    else Lead(ee);
                }
            }, "target", target, "dist", (int)d);
            if (!asked) { e.Asking = false; e.NextReact = now + 20f; }
        }

        /// <summary>The player has just appeared in another scene of the same settlement: the game has already brought in whoever could enter.</summary>
        private static void NewScene(Mission m)
        {
            bool location = m.GetMissionBehavior<MissionLocationLogic>() != null;
            var here = CampaignMission.Current?.Location;
            foreach (var e in Entries.ToList())
            {
                try
                {
                    e.HasDoor = false; e.AtDoor = false;
                    if (e.Lc == null)
                    {
                        Entries.Remove(e);
                        Say(L.F("AIL_e7b1c63f", $"{e.Name} no te sigue más allá de la puerta."));
                        continue;
                    }
                    e.Agent = m.Agents.FirstOrDefault(x => x != null && x.IsActive() && x.Origin == e.Lc.AgentOrigin);
                    if (e.Agent == null)
                    {
                        if (location) Say(L.F("AIL_e7b1c640", $"{e.Name} te espera fuera."));
                        continue;
                    }
                    Log.Info($"[scene] {e.Name} enters with you at {here?.StringId}");
                    if (e.Dest != null && here != null && here.StringId == e.Dest)
                    {
                        Say(L.F("AIL_e7b1c642", $"{e.Name} te ha traído hasta {here.Name}."));
                        SceneActions.Adopt(e.Agent, false);
                        EndLead(e, false);
                        continue;
                    }
                    Say(L.F("AIL_e7b1c63e", $"{e.Name} entra contigo."));
                    SceneActions.Adopt(e.Agent, true);
                    if (e.Dest != null) Lead(e);
                }
                catch (Exception ex) { Log.Error("Scene travel: new scene", ex); Entries.Remove(e); }
            }
        }
    }
}
