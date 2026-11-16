using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.UI;
using Newtonsoft.Json.Linq;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// What a character can do in a scene besides talking (follow you, guide you to someone, call someone over, leave...).
    /// The AI picks the action in group conversations, and in a one-to-one talk through the matching NpcActions.
    /// </summary>
    internal static class SceneActions
    {
        private class Guide { public Agent Who, To; public float Left; public bool Waiting, Back, Asking; public float LagFor, NextReact; public int Reacts; }
        private class Call { public Agent Who, Caller; public float Left; public bool Quiet, Arrived; }

        private static readonly HashSet<Agent> Followers = new HashSet<Agent>();
        private static readonly List<Guide> Guides = new List<Guide>();
        private static readonly List<Call> Calls = new List<Call>();
        // go_with: who walks at whose side (not the player's), and for how much longer
        private static readonly Dictionary<Agent, (Agent leader, float left)> With = new Dictionary<Agent, (Agent, float)>();
        // stay: who keeps their post after the talk, and for how much longer
        private static readonly Dictionary<Agent, float> Posts = new Dictionary<Agent, float>();
        // sit_down: on the way to a seat, with the seconds left to reach it
        private static readonly List<(Agent who, StandingPoint seat, float left)> Seats = new List<(Agent, StandingPoint, float)>();
        // weapons drawn by our own action (so a guard's usual spear is not taken for a threat, but a drawn one is)
        private static readonly HashSet<Agent> Drawn = new HashSet<Agent>();
        private static Mission _mission;
        // NPC-on-NPC arrests: who goes to the dungeon when the scene ends, and who fades out of the scene now
        private static readonly List<(TaleWorlds.CampaignSystem.Hero who, TaleWorlds.CampaignSystem.Settlements.Settlement where)> Jail = new List<(TaleWorlds.CampaignSystem.Hero, TaleWorlds.CampaignSystem.Settlements.Settlement)>();
        private static readonly List<(Agent who, float left)> Leaving = new List<(Agent, float)>();

        // How long a post (stay) and a walk at someone's side (go_with) last, in mission seconds
        private const float PostSeconds = 900f, WithSeconds = 600f, SeatSeconds = 25f;

        /// <summary>New or loaded save: drop anything pending from the previous one, above all queued arrests.</summary>
        public static void ResetSession()
        {
            Jail.Clear(); Leaving.Clear(); Followers.Clear(); Guides.Clear(); Calls.Clear(); Ring.Clear(); Away.Clear(); Grounded.Clear(); DownUntil.Clear(); Lying.Clear();
            With.Clear(); Posts.Clear(); Seats.Clear(); Drawn.Clear(); Reacting.Clear(); ReactSaid.Clear(); _reactGen++;
            _mission = null;
            SceneTravel.Reset();
        }

        public const string Help =
@"""action"" is what the speaker DOES besides speaking, or """" for nothing. Use one only when the talk really leads to it:
 - ""follow"": walks along with {player} from now on (they were asked to come along and agree).
 - ""go_with:<exact name>"": goes along with that person (someone present or from the 'Elsewhere' list): walks at their side and stops where they stop. ""go_with:STOP"" stops it.
 - ""stay"": stays where they are and waits there, even after this conversation ends (and stops following, if they were).
 - ""guide:<exact name>"": leads {player} on foot to that person (someone from the 'Elsewhere' list), waiting if {player} falls behind.
 - ""call:<exact name>"": calls that person from the 'Elsewhere' list to come over and join this conversation (only someone they could rightly summon: kin, a servant, a subordinate, a friend). The one called decides for themselves whether to come.
 - ""leave"": ends their part in the conversation and goes back to their own business.
 - ""draw_weapon"": bares steel as a threat, without attacking (yet). ""sheathe_weapon"": puts it away again when calmed or ordered to.
 - ""sit_down"": goes to the nearest free seat and sits, or sits on the ground where they stand if there is none (they are invited to sit, or settle in for a long talk).
 - ""stand_up"": rises from where they sit or leaves what they were busy with, to face {player}.
 - ""call_guards"": orders the guard to remove {player} from their presence. ONLY for someone with authority in this place (its lord, the lord's family, a noble of this realm, the ruler), and only when {player} has truly earned it: insults, threats, refusing to leave when told, or insolence a proud noble would not swallow from a nobody. They say it aloud in the same line.
 - ""arrest:<exact name>"": takes ANOTHER person who is present (not {player}) away to the cells. ONLY a guard or soldier on duty, or the lord of this place, in a town or castle; and only with grave cause that they themselves find convincing: that person is accused before witnesses of blood or treason AND something visible supports it (bloodied hands, a body, a weapon, a confession, their own guilty answers), or they were caught in the act. An accusation alone, from a stranger, against a person of rank is not enough: then they question, they do not arrest. Never against the lord of the place or the ruler.";

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Gray));

        /// <summary>Following, guiding, walking over, going with someone or keeping a post. Busy agents don't start chatting on their own.</summary>
        public static bool Busy(Agent a) => Moving(a) || Posts.ContainsKey(a);

        /// <summary>On their way somewhere because of us (following, guiding, called over, at someone's side, leading to a door).</summary>
        private static bool Moving(Agent a) => Followers.Contains(a) || Guides.Any(g => g.Who == a) || Calls.Any(c => c.Who == a && !c.Arrived) || With.ContainsKey(a) || SceneTravel.Leading(a);

        /// <summary>Walking along with the player.</summary>
        internal static bool Following(Agent a) => a != null && Followers.Contains(a);

        /// <summary>Keeping their post (stay) after the talk.</summary>
        internal static bool Posted(Agent a) => a != null && Posts.ContainsKey(a);

        /// <summary>The person they are walking beside (go_with), or null.</summary>
        internal static Agent GoingWith(Agent a) => a != null && With.TryGetValue(a, out var w) ? w.leader : null;

        /// <summary>Why the last action asked for did not happen, in English for the character, or null.</summary>
        internal static string LastWhy { get; private set; }

        /// <summary>The list of actions for the AI, with the places someone could lead the player to from this scene, and the weapons drawn in sight.</summary>
        public static string HelpFor(string player)
        {
            string drawn = "";
            try { drawn = DrawnBlock(Agent.Main, player, player); } catch { }
            return Help.Replace("{player}", player) + SceneTravel.HelpLine(player) + (drawn.Length > 0 ? "\n\n" + drawn : "");
        }

        /// <summary>Named people in this scene other than <paramref name="who"/> and the player, nearest first: the ones a character can name in an action.</summary>
        internal static List<Agent> Around(Agent who)
        {
            try
            {
                var m = Mission.Current;
                if (m == null || who == null) return new List<Agent>();
                return m.Agents.Where(a => a != null && a != who && a != Agent.Main && a.IsHuman && a.IsActive()
                                           && a.Character is TaleWorlds.CampaignSystem.CharacterObject co && co.IsHero && co.HeroObject != null)
                               .OrderBy(a => a.Position.Distance(who.Position)).Take(10).ToList();
            }
            catch { return new List<Agent>(); }
        }

        /// <summary>The people of <see cref="Around"/>, for a prompt: "Name (on this floor, about N paces away); ...". Empty when there is nobody.</summary>
        internal static string DescribeAround(Agent who)
        {
            var list = Around(who);
            if (list.Count == 0) return "";
            return "People elsewhere in this place you can name (use these exact names): " + string.Join("; ", list.Select(a =>
            {
                float dz = a.Position.z - who.Position.z;
                return $"{a.Name} ({(dz > 2f ? "on the floor above" : dz < -2f ? "on the floor below" : "on this floor")}, about {(int)a.Position.Distance(who.Position)} paces away)";
            })) + ".";
        }

        /// <summary>Named people standing close to <paramref name="who"/> (within 25 m), for "detain". Empty when there is nobody.</summary>
        internal static string DescribePresent(Agent who)
        {
            var list = Around(who).Where(a => a.Position.Distance(who.Position) <= 25f).ToList();
            return list.Count == 0 ? "" : "Named people present (use these exact names): " + string.Join(", ", list.Select(a => a.Name)) + ".";
        }

        /// <summary>Someone the game brought into this scene behind the player: ours to release later, and following again if asked.</summary>
        internal static void Adopt(Agent a, bool follow)
        {
            try
            {
                if (a == null || !a.IsActive()) return;
                Sync();
                SceneMove.Adopt(a);
                if (!follow) return;
                SceneMove.GoTo(a, Agent.Main);
                Followers.Add(a);
            }
            catch (Exception ex) { Log.Error("Follow in the new scene", ex); }
        }

        /// <summary>Stops walking with the player, with no message and without touching who crosses doors.</summary>
        internal static void Unfollow(Agent a)
        {
            try { Followers.Remove(a); SceneMove.Release(a); }
            catch (Exception ex) { Log.Error("Stop following", ex); }
        }

        private static void Sync()
        {
            if (_mission == Mission.Current) return;
            _mission = Mission.Current;
            Followers.Clear(); Guides.Clear(); Calls.Clear(); Ring.Clear(); Grounded.Clear(); DownUntil.Clear(); Lying.Clear();
            With.Clear(); Posts.Clear(); Seats.Clear(); Drawn.Clear(); ReactSaid.Clear(); _reactGen++;
            SceneMove.Forget(); Gestures.Forget();
        }

        /// <summary>Applies the action attached to a line. Returns true if the character leaves the conversation.</summary>
        public static bool Apply(Agent who, string action, List<Agent> elsewhere, string line = null) => Run(who, action, elsewhere, line, out _);

        /// <summary>
        /// The same, for a one-to-one talk: true if it really happened (see <see cref="LastWhy"/> when not), and in
        /// <paramref name="leaves"/> whether the character walks out of the talk because of it.
        /// </summary>
        internal static bool Try(Agent who, string action, List<Agent> elsewhere, string line, out bool leaves)
        {
            leaves = Run(who, action, elsewhere, line, out bool happened);
            return happened;
        }

        /// <summary>Stops keeping a post (stay) and lets their routine back, if they were.</summary>
        private static void Unpost(Agent a)
        {
            if (a != null && Posts.Remove(a)) SceneMove.Release(a);
        }

        /// <summary>Stops walking at someone's side (go_with), if they were.</summary>
        private static void Unwith(Agent a)
        {
            if (a != null && With.Remove(a)) SceneMove.Release(a);
        }

        private static bool Run(Agent who, string action, List<Agent> elsewhere, string line, out bool ok)
        {
            ok = false;
            LastWhy = null;
            bool Fail(string why) { LastWhy = why; return false; }
            try
            {
                if (who == null || string.IsNullOrWhiteSpace(action) || !S.Get.SceneErrands || Agent.Main == null || !who.IsAIControlled) return Fail("nothing can be done in this place right now");
                Sync();
                elsewhere = elsewhere ?? new List<Agent>();
                action = action.Trim();
                string verb = action, arg = "";
                int colon = action.IndexOf(':');
                if (colon > 0) { verb = action.Substring(0, colon).Trim(); arg = action.Substring(colon + 1).Trim().Trim('<', '>', '"', '\'').Trim(); }
                Agent FindIn(List<Agent> list) => arg.Length < 2 ? null
                    : list.FirstOrDefault(x => x.IsActive() && string.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase))
                      ?? list.FirstOrDefault(x => x.IsActive() && (x.Name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 || arg.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0));
                // first among those the AI was shown, then anyone named in the scene
                Agent Find() => FindIn(elsewhere) ?? FindIn(Around(who));
                bool AtPlayer() => arg.Length == 0 || arg.Equals("main_hero", StringComparison.OrdinalIgnoreCase) || arg.Equals("player", StringComparison.OrdinalIgnoreCase)
                                   || arg.Equals("me", StringComparison.OrdinalIgnoreCase) || (TaleWorlds.CampaignSystem.Hero.MainHero != null && string.Equals(arg, TaleWorlds.CampaignSystem.Hero.MainHero.Name.ToString(), StringComparison.OrdinalIgnoreCase));

                switch (verb.ToLowerInvariant())
                {
                    case "go_with":
                        if (arg.Equals("STOP", StringComparison.OrdinalIgnoreCase) || arg.Equals("none", StringComparison.OrdinalIgnoreCase))
                        {
                            var was = GoingWith(who);
                            if (was == null && !Followers.Contains(who)) return Fail("you were not going along with anyone");
                            Unwith(who);
                            if (Followers.Remove(who)) { SceneTravel.Drop(who); SceneMove.Release(who); }
                            ok = true;
                            Say(L.F("AIL_e7b1d001", $"{who.Name} deja de ir con {(was != null ? was.Name : TaleWorlds.CampaignSystem.Hero.MainHero?.Name?.ToString())}."));
                            return false;
                        }
                        if (AtPlayer()) goto case "follow";
                        {
                            var leader = Find();
                            if (leader == null || leader == who) return Fail($"there is nobody called «{arg}» in this place");
                            if (GoingWith(who) == leader) return Fail($"you are already going along with {leader.Name}");
                            Unpost(who); Unwith(who);
                            if (Followers.Remove(who)) { SceneTravel.Drop(who); SceneMove.Release(who); }
                            Guides.RemoveAll(g => g.Who == who);
                            if (!SceneMove.GoTo(who, leader)) return Fail("you cannot walk anywhere right now");
                            With[who] = (leader, WithSeconds);
                            ok = true;
                            Say(L.F("AIL_e7b1d000", $"{who.Name} se va con {leader.Name}."));
                            Log.Info($"[scene] {who.Name} goes along with {leader.Name}");
                            return true;
                        }
                    case "follow":
                        if (Followers.Contains(who)) return Fail("you are already walking along with them");
                        Unpost(who); Unwith(who);
                        if (!SceneMove.GoTo(who, Agent.Main)) return Fail("you cannot walk anywhere right now");
                        Guides.RemoveAll(g => g.Who == who);
                        Followers.Add(who);
                        SceneTravel.Follow(who);
                        ok = true;
                        Say(L.F("AIL_dd2f52c0", $"{who.Name} te acompaña."));
                        Log.Info($"[scene] {who.Name} accompanies you");
                        return false;
                    case "stay":
                    {
                        // Stops whatever walk they were on and keeps the spot, also once the talk is over
                        if (Posts.ContainsKey(who)) return Fail("you are already staying here and waiting");
                        SceneTravel.Drop(who);
                        Followers.Remove(who); Unwith(who);
                        Guides.RemoveAll(g => g.Who == who);
                        SceneMove.Release(who);
                        if (SceneMove.Hold(who, null)) Posts[who] = PostSeconds;
                        ok = true;
                        Say(L.F("AIL_78680a96", $"{who.Name} se queda aquí."));
                        Log.Info($"[scene] {who.Name} stays at their post");
                        return false;
                    }
                    case "go_into":
                    {
                        Unpost(who); Unwith(who);
                        string place = SceneTravel.GoInto(who, arg);
                        if (place == null) return Fail("there is no way from here into that place that you can take them through");
                        Guides.RemoveAll(g => g.Who == who);
                        ok = true;
                        Say(L.F("AIL_e7b1c641", $"{who.Name} te lleva a {place}: síguele hasta la puerta y crúzala."));
                        return true;
                    }
                    case "guide":
                    {
                        var to = Find();
                        if (to == null || to == who) return Fail($"there is nobody called «{arg}» in this place");
                        Unpost(who); Unwith(who);
                        if (!SceneMove.GoTo(who, to)) return Fail("you cannot walk anywhere right now");
                        Followers.Remove(who); SceneTravel.Drop(who);
                        Guides.RemoveAll(g => g.Who == who);
                        Guides.Add(new Guide { Who = who, To = to, Left = 240f });
                        ok = true;
                        Say(L.F("AIL_5f595733", $"{who.Name} te lleva hasta {to.Name}. Síguele."));
                        Log.Info($"[scene] {who.Name} guides the player to {to.Name}");
                        return true;
                    }
                    case "call":
                    {
                        var target = Find();
                        if (target == null || target == who) return Fail($"there is nobody called «{arg}» in this place");
                        if (Moving(target) || Calls.Any(c => c.Who == target)) return Fail($"{target.Name} is busy going somewhere else");
                        ok = true;
                        Say(L.F("AIL_7f71ac3a", $"{who.Name} llama a {target.Name}."));
                        Log.Info($"[scene] {who.Name} calls {target.Name}");
                        AskCalled(who, target, line);
                        return false;
                    }
                    case "draw_weapon":
                        if (Wielding(who)) return Fail("your weapon is already drawn");
                        if (!HasWeapon(who)) return Fail("you carry no weapon to draw");
                        try { who.WieldInitialWeapons(); } catch { }
                        Drawn.Add(who);
                        ok = true;
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_a6a88727", $"{who.Name} desenvaina."), Colors.Red));
                        return false;
                    case "sheathe_weapon":
                        if (!Wielding(who)) return Fail("you hold no drawn weapon");
                        try
                        {
                            who.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.WithAnimation);
                            who.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.WithAnimation);
                        }
                        catch { }
                        Drawn.Remove(who);
                        ok = true;
                        Say(L.F("AIL_1ee0d3b1", $"{who.Name} envaina."));
                        return false;
                    case "sit_down":
                        try
                        {
                            if (who.IsUsingGameObject || who.IsSitting()) return Fail("you are already sitting");
                            StandingPoint best = null; UsableMachine bestChair = null; float bd = 14f;
                            foreach (var mo in Mission.Current.ActiveMissionObjects)
                            {
                                if (!(mo is SandBox.Objects.Usables.Chair chair) || chair.IsDisabled) continue;
                                foreach (var sp in chair.StandingPoints)
                                {
                                    if (sp == null || sp.HasUser || sp.HasAIMovingTo || sp.IsDeactivated) continue;
                                    float d = sp.GameEntity.GlobalPosition.Distance(who.Position);
                                    if (d < bd) { bd = d; best = sp; bestChair = chair; }
                                }
                            }
                            Unpost(who); Unwith(who);
                            if (best == null) { ok = SitOnGround(who); return ok ? false : Fail("there is no seat near and you cannot sit down here"); }
                            Followers.Remove(who); Posts.Remove(who); With.Remove(who); SceneTravel.Drop(who); SceneMove.Release(who);
                            Guides.RemoveAll(g => g.Who == who);
                            who.AIMoveToGameObjectEnable(best, bestChair, Agent.AIScriptedFrameFlags.NoAttack);
                            // they have a while to get there; if they can't, they give it up (TickSeats)
                            Seats.RemoveAll(x => x.who == who);
                            Seats.Add((who, best, SeatSeconds));
                            ok = true;
                            Say(L.F("AIL_67c8cba8", $"{who.Name} va a sentarse."));
                        }
                        catch (Exception ex) { Log.Error("Sit down", ex); }
                        return false;
                    case "stand_up":
                        if (!who.IsUsingGameObject && !who.IsSitting() && !Grounded.Any(g => g.who == who) && !Seats.Any(x => x.who == who)) return Fail("you are already on your feet");
                        try
                        {
                            Seats.RemoveAll(x => x.who == who);
                            if (who.IsUsingGameObject) who.StopUsingGameObject();
                            StandFromGround(who); who.SetLookAgent(Agent.Main);
                        }
                        catch { }
                        ok = true;
                        return false;
                    case "call_guards":
                    {
                        var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                        var h = (who.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                        if (st == null || h == null || !HasAuthorityHere(who)) return Fail("you have no authority to order the guard about here");
                        bool lordHere = st.OwnerClan != null && st.OwnerClan == h.Clan;
                        string what = $"Moments ago {h.Name}, {(st.OwnerClan?.Leader == h ? "the lord of this place" : lordHere ? "of the house that rules this place" : "a noble with authority here")}, " +
                                      $"ordered you in front of everyone to remove the armed person now in front of you from their presence, with these words: \"{(line ?? "").Replace("\"", "'")}\". " +
                                      "No blood was shed: the offence is insolence in a noble's hall. You carry out the order. They either come away quietly with you (let_go, once they agree to leave), " +
                                      "pay for the insult (fine), or go to the cells (arrest) if they argue with the order, mock or resist.";
                        if (!Justice.Summon(what, who, true)) { Log.Info($"[scene] {who.Name} calls the guard, but there is no guard nearby (or justice is off)"); return Fail("no guard is near enough to hear you"); }
                        ok = true;
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_95594522", $"{who.Name} llama a la guardia: entrégate, convéncele o resístete."), Colors.Red));
                        Log.Info($"[scene] {who.Name} calls the guard on the player");
                        return false;
                    }
                    case "arrest":
                    case "detain":
                    {
                        // A guard on duty or the local lord takes some other present character (never the player) to the cells
                        var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                        var wc = who.Character as TaleWorlds.CampaignSystem.CharacterObject;
                        if (st == null || wc == null || !S.Get.AllowNpcArrests || !CanDetain(who)) return Fail("you have no right to arrest anyone here");
                        var wh = wc.HeroObject;
                        var target = Mission.Current.Agents.Where(x => x != null && x != who && x != Agent.Main && x.IsHuman && x.IsActive() && x.Position.Distance(who.Position) <= 25f
                                                                       && x.Character is TaleWorlds.CampaignSystem.CharacterObject tc0 && tc0.IsHero && tc0.HeroObject != null)
                                                           .FirstOrDefault(x => string.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase) || (arg.Length > 2 && x.Name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0));
                        var th = (target?.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                        if (th == null) return Fail($"there is nobody called «{arg}» close enough to seize");
                        if (th.IsChild || th == TaleWorlds.CampaignSystem.Hero.MainHero || th.Clan == TaleWorlds.CampaignSystem.Clan.PlayerClan) return Fail($"{th.Name} is not someone you can take to the cells");
                        // Nobody arrests the lord in his own seat
                        if (th == st.OwnerClan?.Leader || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k3 && k3.Leader == th)) return Fail($"nobody arrests {th.Name} here");
                        if (Jail.Any(j => j.who == th)) return Fail($"{th.Name} is already being taken away");
                        var led = th.PartyBelongedTo;
                        if (led != null && !led.IsMainParty && led.LeaderHero == th && led.MemberRoster.TotalManCount > 1) return Fail($"{th.Name}'s own men are at hand: you cannot just take them");
                        Jail.Add((th, st));
                        Leaving.Add((target, 7f));
                        Unpost(who); Unwith(who);
                        SceneMove.GoTo(who, target);
                        ok = true;
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_ff0ad962", $"{who.Name} detiene a {th.Name}: se la llevan a los calabozos de {st.Name}."), Colors.Red));
                        Log.Info($"[scene] {who.Name} arrests {th.Name} in {st.Name}. Stated reason: {line}");
                        try
                        {
                            // The prisoner remembers who pointed the finger
                            var tm = Memory.MemoryStore.Get(th.StringId, th.Name.ToString());
                            string you = tm.KnownPlayerName ?? "a stranger";
                            tm.Turns.Add(new Memory.ChatTurn { Role = "player", Day = TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays, Text = $"[What happened: in front of everyone, {you} accused you, and {who.Name} arrested you and threw you into the cells of {st.Name}.]" });
                            ChatSession.AddNote(tm, $"{you} got me arrested at {st.Name} with their accusations.", S.Get.MaxNotesPerHero, th.Name.ToString());
                            tm.Trust = Math.Max(-100, tm.Trust - 60);
                            TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyPlayerRelation(th, -20, affectRelatives: true, showQuickNotification: true);
                            World.NewsStore.Add(new World.NewsItem
                            {
                                Type = "rumor", Importance = th.IsLord ? 3 : 2, PlayerInvolved = true,
                                Text = $"{th.Name} was arrested at {st.Name} and taken to the cells, accused before witnesses by {TaleWorlds.CampaignSystem.Hero.MainHero.Name}.",
                                TitleEs = L.F("AIL_c5c07b9e", $"Arresto de {th.Name}"), TextEs = L.F("AIL_e911ba7b", $"{th.Name} ha sido detenida en {st.Name} y llevada a los calabozos, acusada ante testigos por {TaleWorlds.CampaignSystem.Hero.MainHero.Name}."),
                                SettlementId = st.StringId, Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(),
                                Heroes = new List<string> { th.StringId, TaleWorlds.CampaignSystem.Hero.MainHero.StringId },
                            });
                        }
                        catch (Exception ex) { Log.Error("Arrest: memory", ex); }
                        try { if (wh != null) Bonds.TouchOther(Speaker.FromHero(th), Speaker.FromHero(wh), -40, $"{wh.Name} had me arrested and thrown in the cells of {st.Name}"); } catch { }
                        World.WorldSim.OnIncident($"{th.Name}{(th.IsLord ? $", a noble of clan {th.Clan?.Name}," : "")} was arrested by the guard and taken to the cells after {TaleWorlds.CampaignSystem.Hero.MainHero.Name} accused them in front of witnesses. What was said as they were taken: \"{(line ?? "").Replace("\"", "'")}\"", st, false);
                        return false;
                    }
                    case "leave":
                        SceneTravel.Drop(who);
                        if (Followers.Remove(who)) SceneMove.Release(who);
                        WalkAway(who);
                        ok = true;
                        Say(L.F("AIL_56f0d4a1", $"{who.Name} sale de la conversación."));
                        return true;
                }
                return Fail("there is no such thing to do in this place");
            }
            catch (Exception ex) { Log.Error("Scene action", ex); }
            return Fail("something went wrong and it could not be done");
        }

        /// <summary>The lord of this place, his house, the ruler, or a noble of this realm: someone who may order the guard about here.</summary>
        internal static bool HasAuthorityHere(Agent who)
        {
            try
            {
                var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                var h = (who?.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                if (st == null || h == null) return false;
                bool lordHere = st.OwnerClan != null && st.OwnerClan == h.Clan;
                return lordHere || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k && k.Leader == h) || (h.IsLord && h.MapFaction != null && h.MapFaction == st.MapFaction);
            }
            catch { return false; }
        }

        /// <summary>A guard or soldier on duty, or the lord of this town or castle: someone who may take another person to the cells.</summary>
        internal static bool CanDetain(Agent who)
        {
            try
            {
                var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                var wc = who?.Character as TaleWorlds.CampaignSystem.CharacterObject;
                if (st == null || wc == null || (!st.IsTown && !st.IsCastle)) return false;
                var wh = wc.HeroObject;
                bool guard = !wc.IsHero && (wc.Occupation == TaleWorlds.CampaignSystem.Occupation.Guard || wc.Occupation == TaleWorlds.CampaignSystem.Occupation.Soldier || wc.Occupation == TaleWorlds.CampaignSystem.Occupation.PrisonGuard);
                bool lord = wh != null && ((st.OwnerClan != null && st.OwnerClan == wh.Clan) || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k2 && k2.Leader == wh));
                return guard || lord;
            }
            catch { return false; }
        }

        /// <summary>
        /// Someone shouts for help: their own kin and close friends within earshot walk over. Returns how many come.
        /// The guard is called apart (Justice.Summon), by whoever handles the shout.
        /// </summary>
        internal static int CallKin(Agent who)
        {
            int n = 0;
            try
            {
                var h = (who?.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                if (h == null || Agent.Main == null) return 0;
                foreach (var a in Around(who))
                {
                    if (n >= 3 || a.Position.Distance(who.Position) > 30f || !a.IsAIControlled || Moving(a)) continue;
                    var k = (a.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                    if (k == null || k == TaleWorlds.CampaignSystem.Hero.MainHero || k.IsChild || k.Clan == TaleWorlds.CampaignSystem.Clan.PlayerClan) continue;
                    bool close = (h.Clan != null && k.Clan == h.Clan) || h.Spouse == k || h.GetRelation(k) >= 30;
                    if (!close) continue;
                    Unpost(a);
                    if (!SceneMove.GoTo(a, who)) continue;
                    Calls.Add(new Call { Who = a, Caller = who, Left = 60f, Quiet = true });
                    Say(L.F("AIL_e7b1d00b", $"{a.Name} se acerca a ayudar a {who.Name}."));
                    n++;
                }
            }
            catch (Exception ex) { Log.Error("Call for help", ex); }
            return n;
        }

        // ============================================================ weapons

        /// <summary>The weapon held in the main hand (not a shield or a banner), or null.</summary>
        private static TaleWorlds.Core.ItemObject InHand(Agent a)
        {
            try
            {
                var idx = a.GetPrimaryWieldedItemIndex();
                if (idx == TaleWorlds.Core.EquipmentIndex.None) return null;
                var w = a.Equipment[idx];
                if (w.IsEmpty || w.Item == null || w.CurrentUsageItem == null || w.CurrentUsageItem.IsShield) return null;
                return w.CurrentUsageItem.WeaponClass == TaleWorlds.Core.WeaponClass.Banner ? null : w.Item;
            }
            catch { return null; }
        }

        /// <summary>They hold a weapon drawn in their hand right now.</summary>
        internal static bool Wielding(Agent a) => a != null && a.IsActive() && InHand(a) != null;

        /// <summary>They carry some weapon they could draw.</summary>
        internal static bool HasWeapon(Agent a)
        {
            try
            {
                if (a == null || !a.IsActive()) return false;
                for (var i = TaleWorlds.Core.EquipmentIndex.WeaponItemBeginSlot; i < TaleWorlds.Core.EquipmentIndex.NumAllWeaponSlots; i++)
                {
                    var w = a.Equipment[i];
                    if (!w.IsEmpty && w.CurrentUsageItem != null && !w.CurrentUsageItem.IsShield && w.CurrentUsageItem.WeaponClass != TaleWorlds.Core.WeaponClass.Banner) return true;
                }
            }
            catch { }
            return false;
        }

        /// <summary>What kind of weapon, in plain English: sword, axe, spear...</summary>
        internal static string WeaponKind(TaleWorlds.Core.ItemObject item)
        {
            try
            {
                switch (item?.PrimaryWeapon?.WeaponClass)
                {
                    case TaleWorlds.Core.WeaponClass.Dagger: case TaleWorlds.Core.WeaponClass.ThrowingKnife: return "dagger";
                    case TaleWorlds.Core.WeaponClass.OneHandedSword: case TaleWorlds.Core.WeaponClass.TwoHandedSword: return "sword";
                    case TaleWorlds.Core.WeaponClass.OneHandedAxe: case TaleWorlds.Core.WeaponClass.TwoHandedAxe: case TaleWorlds.Core.WeaponClass.ThrowingAxe: return "axe";
                    case TaleWorlds.Core.WeaponClass.Mace: case TaleWorlds.Core.WeaponClass.TwoHandedMace: return "mace";
                    case TaleWorlds.Core.WeaponClass.Pick: return "war pick";
                    case TaleWorlds.Core.WeaponClass.OneHandedPolearm: case TaleWorlds.Core.WeaponClass.TwoHandedPolearm: case TaleWorlds.Core.WeaponClass.LowGripPolearm: case TaleWorlds.Core.WeaponClass.Javelin: return "spear";
                    case TaleWorlds.Core.WeaponClass.Bow: return "bow";
                    case TaleWorlds.Core.WeaponClass.Crossbow: return "crossbow";
                }
            }
            catch { }
            return "weapon";
        }

        /// <summary>The weapon they would draw (or hold), in plain English.</summary>
        internal static string WeaponOf(Agent a)
        {
            var held = InHand(a);
            if (held != null) return WeaponKind(held);
            try
            {
                for (var i = TaleWorlds.Core.EquipmentIndex.WeaponItemBeginSlot; i < TaleWorlds.Core.EquipmentIndex.NumAllWeaponSlots; i++)
                {
                    var w = a.Equipment[i];
                    if (!w.IsEmpty && w.CurrentUsageItem != null && !w.CurrentUsageItem.IsShield) return WeaponKind(w.Item);
                }
            }
            catch { }
            return "weapon";
        }

        private static bool OnDuty(Agent a)
        {
            var c = a.Character as TaleWorlds.CampaignSystem.CharacterObject;
            if (c == null || c.IsHero) return false;
            switch (c.Occupation)
            {
                case TaleWorlds.CampaignSystem.Occupation.Soldier: case TaleWorlds.CampaignSystem.Occupation.Guard: case TaleWorlds.CampaignSystem.Occupation.PrisonGuard:
                case TaleWorlds.CampaignSystem.Occupation.CaravanGuard: case TaleWorlds.CampaignSystem.Occupation.Mercenary: return true;
            }
            return false;
        }

        private static readonly Tpl TDrawn = new Tpl("scene/drawn_weapons.txt",
@"### Drawn weapons in sight ###
(Weapons people hold in hand right now, not sheathed. Nobody has struck yet.)
{list}");

        /// <summary>
        /// The "Drawn weapons in sight" block: who holds a weapon bared within sight of <paramref name="from"/>, and how far.
        /// A guard's spear or bow carried as usual does not count, unless they drew it on purpose. Empty when there are none.
        /// </summary>
        internal static string DrawnBlock(Agent from, string fromLabel, string player)
        {
            try
            {
                var m = Mission.Current;
                if (m == null || from == null || !from.IsActive()) return "";
                var lines = new List<string>();
                foreach (var a in m.Agents)
                {
                    if (a == null || !a.IsHuman || !a.IsActive() || a == from || a.Position.Distance(from.Position) > 30f) continue;
                    var item = InHand(a);
                    if (item == null) continue;
                    string kind = WeaponKind(item);
                    if (OnDuty(a) && !Drawn.Contains(a) && (kind == "spear" || kind == "bow" || kind == "crossbow")) continue;
                    if (!Bubbles.LineOfSight(from.GetEyeGlobalPosition(), a.GetEyeGlobalPosition())) continue;
                    string name = a == Agent.Main ? player : a.Name;
                    lines.Add($"- {name} holds a drawn {kind}, about {(int)Math.Round(a.Position.Distance(from.Position))} m from {fromLabel}");
                    if (lines.Count >= 6) break;
                }
                return lines.Count == 0 ? "" : TDrawn.Format("list", string.Join("\n", lines));
            }
            catch { return ""; }
        }

        private static readonly Tpl TDoing = new Tpl("scene/what_you_are_doing.txt",
@"### What you are doing in this place ###
{list}");

        /// <summary>
        /// The scene part of a one-to-one prompt: what the character is already doing here (so they don't ask for it
        /// again) and the weapons drawn in sight. Empty outside a scene.
        /// </summary>
        internal static string PromptBlock(Agent me, string player)
        {
            try
            {
                if (me == null || Mission.Current == null || !me.IsActive()) return "";
                Sync();
                var doing = new List<string>();
                if (Followers.Contains(me)) doing.Add($"- Right now you are walking along with {player}: this is already in effect, do not repeat come_along. stay_here stops it.");
                var w = GoingWith(me);
                if (w != null) doing.Add($"- Right now you are going along with {w.Name}: this is already in effect, do not repeat go_with to them. go_with:STOP stops it.");
                if (Posts.ContainsKey(me)) doing.Add("- Right now you are staying here and waiting: stay_here is already in effect, do not repeat it.");
                var g = Guides.FirstOrDefault(x => x.Who == me);
                if (g != null && g.To != null) doing.Add($"- Right now you are leading {player} to {g.To.Name}: this is already in effect, do not repeat guide.");
                if (SceneTravel.Leading(me)) doing.Add($"- Right now you are taking {player} to a door, to go through it together: this is already in effect, do not repeat go_into there. stay_here ends it.");
                if (Wielding(me)) doing.Add($"- You hold your {WeaponOf(me)} drawn in your hand, and everyone around sees it.");
                string a = doing.Count > 0 ? TDoing.Format("list", string.Join("\n", doing)) : "";
                string b = DrawnBlock(me, "you", player);
                return a.Length > 0 && b.Length > 0 ? a + "\n\n" + b : a + b;
            }
            catch { return ""; }
        }

        // ============================================================ sitting where there is no seat

        // Agents we sat on the ground: their daily routine is paused until they stand up
        private static readonly List<(Agent who, float since, bool settled)> Grounded = new List<(Agent, float, bool)>();
        private const string SitDownOnFloor = "act_sit_down_on_floor_1", SitIdleOnFloor = "act_sit_idle_on_floor_1", StandUpFromFloor = "act_stand_up_floor_1";

        private static SandBox.Missions.AgentBehaviors.DailyBehaviorGroup Daily(Agent a)
        {
            try { return a.GetComponent<SandBox.CampaignAgentComponent>()?.AgentNavigator?.GetBehaviorGroup<SandBox.Missions.AgentBehaviors.DailyBehaviorGroup>(); }
            catch { return null; }
        }

        // Knocked down in a fight: when they may get up again (mission time)
        private static readonly Dictionary<Agent, float> DownUntil = new Dictionary<Agent, float>();
        private static readonly Random Rng = new Random();
        private static readonly string[] Falls = { "act_fall_face_hold", "act_fall_body_back", "act_fall_abdomen_hold_front" };
        // Lying flat on the floor. Only some action sets have it (the dungeon prisoner's), so it is checked per agent
        private const string LieIdleOnFloor = "act_dungeon_prisoner_sleep_cycle", GetUpFromLying = "act_dungeon_prisoner_sleep_wakeup";
        // Those we laid flat instead of sitting
        private static readonly HashSet<Agent> Lying = new HashSet<Agent>();

        /// <summary>The agent's own action set really has an animation for this action (an id alone is not enough).</summary>
        private static bool HasAnim(Agent a, string action)
        {
            try
            {
                var c = ActionIndexCache.Create(action);
                return c.Index >= 0 && MBActionSet.CheckActionAnimationClipExists(a.ActionSet, in c);
            }
            catch { return false; }
        }

        /// <summary>
        /// The loser of a fist fight who is still conscious (they yielded) goes down: a fall, then lying on the floor (or,
        /// when their animations have no lying pose, sitting there nursing it) for a while before getting up. Someone
        /// knocked out cold is not handled here: the engine already laid them out and took them out of the scene.
        /// Nobody else is touched; the player's own character is never moved.
        /// </summary>
        internal static void Knockdown(Agent who, float seconds)
        {
            try
            {
                var m = Mission.Current;
                if (who == null || m == null || who == Agent.Main || !who.IsActive() || !who.IsHuman || who.MountAgent != null || Grounded.Any(g => g.who == who)) return;
                bool lie = HasAnim(who, LieIdleOnFloor);
                var sitIdle = ActionIndexCache.Create(SitIdleOnFloor);
                if (!lie && sitIdle.Index < 0) return;
                var fall = ActionIndexCache.act_none;
                // A fall their action set can play, or failing that any fall the game knows (what this did before)
                foreach (var name in Falls.OrderBy(_ => Rng.Next()).OrderByDescending(n => HasAnim(who, n)))
                {
                    var c = ActionIndexCache.Create(name);
                    if (c.Index >= 0) { fall = c; break; }
                }
                if (fall.Index < 0) fall = ActionIndexCache.Create(SitDownOnFloor);
                if (fall.Index < 0) return;
                Followers.Remove(who); Posts.Remove(who); With.Remove(who); SceneTravel.Drop(who); SceneMove.Release(who);
                try { who.DisableScriptedMovement(); } catch { }
                var g = Daily(who);
                if (g != null) { g.AddBehavior<SandBox.Missions.AgentBehaviors.IdleAgentBehavior>(); g.SetScriptedBehavior<SandBox.Missions.AgentBehaviors.IdleAgentBehavior>(); }
                else who.SetIsAIPaused(true);
                if (!who.SetActionChannel(0, in fall, ignorePriority: true)) { Unpause(who); return; }
                Grounded.Add((who, 0f, false));
                if (lie) Lying.Add(who);
                DownUntil[who] = m.CurrentTime + Math.Max(5f, seconds);
                Log.Info($"[fight] {who.Name} is on the ground ({(lie ? "lying" : "sitting")})");
            }
            catch (Exception ex) { Log.Error("Fall after the fight", ex); }
        }

        /// <summary>No free seat nearby: they sit down where they stand. False if this scene or agent can't do it.</summary>
        private static bool SitOnGround(Agent who)
        {
            try
            {
                var m = Mission.Current;
                if (who == null || m == null || !who.IsActive() || who.MountAgent != null || who.IsSitting() || m.Mode == TaleWorlds.Core.MissionMode.Battle || Combat.Fighting) return false;
                if (Grounded.Any(g => g.who == who)) return false;
                var down = ActionIndexCache.Create(SitDownOnFloor);
                if (down.Index < 0 || ActionIndexCache.Create(SitIdleOnFloor).Index < 0) { Log.Warn("Sit on the floor: the game does not have that animation"); return false; }
                Followers.Remove(who); Posts.Remove(who); With.Remove(who); SceneTravel.Drop(who); SceneMove.Release(who);
                try { who.DisableScriptedMovement(); } catch { }
                // Hold them in place with the game's own idle behavior, or their routine walks them off mid-animation
                var g = Daily(who);
                if (g != null) { g.AddBehavior<SandBox.Missions.AgentBehaviors.IdleAgentBehavior>(); g.SetScriptedBehavior<SandBox.Missions.AgentBehaviors.IdleAgentBehavior>(); }
                else who.SetIsAIPaused(true);
                if (!who.SetActionChannel(0, in down, ignorePriority: true))
                {
                    Unpause(who);
                    return false;
                }
                Grounded.Add((who, 0f, false));
                Say(L.F("AIL_e7b1c644", $"{who.Name} se sienta en el suelo."));
                return true;
            }
            catch (Exception ex) { Log.Error("Sit on the floor", ex); return false; }
        }

        private static void Unpause(Agent a)
        {
            try
            {
                var g = Daily(a);
                if (g != null && g.ScriptedBehavior is SandBox.Missions.AgentBehaviors.IdleAgentBehavior) g.DisableScriptedBehavior();
                a.SetIsAIPaused(false);
            }
            catch (Exception ex) { Log.Error("Resume routine", ex); }
        }

        /// <summary>Gets up someone we sat on the ground and gives them back their routine. Does nothing for anyone else.</summary>
        internal static void StandFromGround(Agent a)
        {
            try
            {
                int i = Grounded.FindIndex(g => g.who == a);
                if (i < 0) return;
                Grounded.RemoveAt(i);
                bool lay = Lying.Remove(a);
                if (a == null || !a.IsActive()) return;
                if (lay || a.IsSitting())
                {
                    var up = ActionIndexCache.Create(lay && HasAnim(a, GetUpFromLying) ? GetUpFromLying : StandUpFromFloor);
                    if (up.Index < 0) up = ActionIndexCache.act_none;
                    a.SetActionChannel(0, in up, ignorePriority: true);
                }
                Unpause(a);
            }
            catch (Exception ex) { Log.Error("Get up from the floor", ex); }
        }

        private static void TickGrounded(float dt)
        {
            for (int i = Grounded.Count - 1; i >= 0; i--)
            {
                var g = Grounded[i];
                try
                {
                    var a = g.who;
                    // Gone, or knocked out cold (the engine lays them out and removes them): nothing to hold
                    if (a == null || Mission.Current == null || a.Mission != Mission.Current || !a.IsActive()) { Grounded.RemoveAt(i); DownUntil.Remove(a); Lying.Remove(a); continue; }
                    bool knocked = DownUntil.TryGetValue(a, out float until);
                    // Knocked down: they stay down for their time, whatever the alarm around them; then up they get
                    if (knocked && Mission.Current.CurrentTime >= until) { DownUntil.Remove(a); StandFromGround(a); continue; }
                    // Trouble around them, or a fight: nobody stays sitting
                    if (Combat.Fighting || (!knocked && !a.IsAlarmStateNormal())) { DownUntil.Remove(a); StandFromGround(a); continue; }
                    float since = g.since + dt;
                    if (since < (knocked ? 2.5f : 1f) || (!knocked && a.IsSitting())) { Grounded[i] = (a, since, g.settled); continue; }
                    if (knocked && g.settled) { Grounded[i] = (a, since, true); continue; }
                    // The sitting-down (or falling) animation is over: hold the pose, lying flat when we laid them down.
                    // If that was already done, something else moved them
                    if (g.settled) { Grounded.RemoveAt(i); Lying.Remove(a); Unpause(a); continue; }
                    var idle = ActionIndexCache.Create(Lying.Contains(a) ? LieIdleOnFloor : SitIdleOnFloor);
                    a.SetActionChannel(0, in idle, ignorePriority: true);
                    Grounded[i] = (a, 0f, true);
                }
                catch (Exception ex) { Log.Error("Seated on the floor", ex); Grounded.RemoveAt(i); }
            }
        }

        // Group ring: each member walks to a spot around the player and faces them
        private static readonly List<(Agent who, Vec2 slot, float left)> Ring = new List<(Agent, Vec2, float)>();

        /// <summary>Puts the group in a half circle a couple of steps in front of the player, facing them. Anyone seated or busy stays put.</summary>
        public static void Arrange(List<Agent> members)
        {
            try
            {
                Sync();
                var me = Agent.Main;
                if (me == null || members == null) return;
                var free = members.Where(a => a != null && a.IsActive() && a.IsAIControlled && a.MountAgent == null && !a.IsSitting() && !a.IsUsingGameObject && !Busy(a)).ToList();
                foreach (var a in members) { try { if (a != null && a.IsActive() && a.IsAIControlled) a.SetLookAgent(me); } catch { } }
                if (free.Count == 0 || !S.Get.GroupCircle) return;
                var front = me.LookDirection.AsVec2;
                if (front.Length < 0.01f) front = new Vec2(0f, 1f);
                front = front.Normalized();
                float baseAngle = (float)Math.Atan2(front.y, front.x);
                // Sort left to right by current position so they don't cross paths
                var left = new Vec2(-front.y, front.x);
                free = free.OrderByDescending(a => Vec2.DotProduct(a.Position.AsVec2 - me.Position.AsVec2, left)).ToList();
                float spread = free.Count == 1 ? 0f : Math.Min(2.4f, 0.75f * (free.Count - 1));      // total fan width, radians
                float radius = free.Count <= 2 ? 1.9f : 2.3f;
                for (int i = 0; i < free.Count; i++)
                {
                    float ang = baseAngle + (free.Count == 1 ? 0f : spread / 2f - spread * i / (free.Count - 1));
                    var slot = me.Position.AsVec2 + new Vec2((float)Math.Cos(ang), (float)Math.Sin(ang)) * radius;
                    var a = free[i];
                    if (a.Position.AsVec2.Distance(slot) < 0.7f) continue;
                    var wp = me.GetWorldPosition();
                    wp.SetVec2(slot);
                    a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun);
                    Ring.RemoveAll(r => r.who == a);
                    Ring.Add((a, slot, 8f));
                }
            }
            catch (Exception ex) { Log.Error("Place the circle", ex); }
        }

        private static readonly List<(Agent who, float left)> Away = new List<(Agent, float)>();

        /// <summary>
        /// Someone leaving the talk turns away and walks a few steps off before going back to their routine.
        /// Anyone seated, mounted or busy with something just stops looking at the player.
        /// </summary>
        public static void WalkAway(Agent a)
        {
            try
            {
                var me = Agent.Main;
                if (a == null || !a.IsActive() || me == null) return;
                // leaving goes back to their own business: no post or walk of ours holds them any more
                Unpost(a); Unwith(a);
                try { a.SetLookAgent(null); } catch { }
                Ring.RemoveAll(r => r.who == a);
                if (!a.IsAIControlled || a.MountAgent != null || a.IsSitting() || a.IsUsingGameObject || Busy(a)) return;
                var dir = a.Position.AsVec2 - me.Position.AsVec2;
                if (dir.Length < 0.05f) dir = me.LookDirection.AsVec2;
                if (dir.Length < 0.01f) dir = new Vec2(0f, 1f);
                var wp = a.GetWorldPosition();
                wp.SetVec2(a.Position.AsVec2 + dir.Normalized() * 7f);
                a.SetScriptedPosition(ref wp, false, Agent.AIScriptedFrameFlags.DoNotRun);
                Away.RemoveAll(x => x.who == a);
                Away.Add((a, 7f));
            }
            catch (Exception ex) { Log.Error("Leave the conversation", ex); }
        }

        /// <summary>Walks the agent over to the player with no message. Used when someone joins the conversation.</summary>
        public static void Summon(Agent a)
        {
            try
            {
                Sync();
                if (a == null || Busy(a) || Agent.Main == null || a.Position.Distance(Agent.Main.Position) <= 3.5f) return;
                if (SceneMove.GoTo(a, Agent.Main)) Calls.Add(new Call { Who = a, Left = 60f, Quiet = true });
            }
            catch (Exception ex) { Log.Error("Approach the player", ex); }
        }

        public static void Tick(float dt)
        {
            try
            {
                SceneTravel.Tick(dt);
                if (Grounded.Count > 0) TickGrounded(dt);
                // Arrested heroes fade out after a few seconds and are jailed for real once the mission is over
                for (int i = Leaving.Count - 1; i >= 0; i--)
                {
                    var l = Leaving[i];
                    float left = l.left - dt;
                    if (left > 0f && Mission.Current != null) { Leaving[i] = (l.who, left); continue; }
                    try { if (Mission.Current != null && l.who != null && l.who.IsActive()) l.who.FadeOut(false, true); } catch { }
                    Leaving.RemoveAt(i);
                }
                if (Jail.Count > 0 && Mission.Current == null && TaleWorlds.CampaignSystem.Campaign.Current != null
                    && TaleWorlds.CampaignSystem.Campaign.Current.ConversationManager?.IsConversationInProgress != true)
                {
                    foreach (var j in Jail.ToList())
                    {
                        Jail.Remove(j);
                        try
                        {
                            if (j.who != null && j.who.IsAlive && !j.who.IsPrisoner && j.where?.Party != null)
                            {
                                TaleWorlds.CampaignSystem.Actions.TakePrisonerAction.Apply(j.where.Party, j.who);
                                Log.Info($"{j.who.Name} is jailed in {j.where.Name}");
                            }
                        }
                        catch (Exception ex) { Log.Error("Lock up the detainee", ex); }
                    }
                }
                for (int i = Away.Count - 1; i >= 0; i--)
                {
                    var w = Away[i];
                    float left = w.left - dt;
                    if (left > 0f && Mission.Current != null && w.who != null && w.who.IsActive()) { Away[i] = (w.who, left); continue; }
                    try { if (w.who != null && w.who.IsActive()) w.who.DisableScriptedMovement(); } catch { }
                    Away.RemoveAt(i);
                }
                for (int i = Ring.Count - 1; i >= 0; i--)
                {
                    var r = Ring[i];
                    float left = r.left - dt;
                    bool there = r.who == null || !r.who.IsActive() || Mission.Current == null || r.who.Position.AsVec2.Distance(r.slot) < 0.6f;
                    if (!there && left > 0f) { Ring[i] = (r.who, r.slot, left); continue; }
                    try { if (r.who != null && r.who.IsActive()) { r.who.DisableScriptedMovement(); if (Agent.Main != null) r.who.SetLookAgent(Agent.Main); } } catch { }
                    Ring.RemoveAt(i);
                }
                TickPosts(dt);
                if (Followers.Count == 0 && Guides.Count == 0 && Calls.Count == 0 && With.Count == 0 && Seats.Count == 0 && Posts.Count == 0) return;
                Sync();
                if (Mission.Current == null || Agent.Main == null || !Agent.Main.IsActive()) { Followers.Clear(); Guides.Clear(); Calls.Clear(); With.Clear(); Seats.Clear(); return; }
                var me = Agent.Main.Position;
                float now = Mission.Current.CurrentTime;

                Followers.RemoveWhere(a => a == null || !a.IsActive());
                TickWith(dt);
                TickSeats(dt);

                for (int i = Guides.Count - 1; i >= 0; i--)
                {
                    var g = Guides[i];
                    g.Left -= dt;
                    if (g.Who == null || !g.Who.IsActive() || g.To == null || !g.To.IsActive() || g.Left <= 0f) { SceneMove.Release(g.Who); Guides.RemoveAt(i); continue; }
                    float toPlayer = g.Who.Position.Distance(me);
                    // Went back for the player: once beside them, they wait to be followed again
                    if (g.Back)
                    {
                        if (toPlayer > 3.5f) continue;
                        g.Back = false; g.Waiting = true; g.LagFor = 0f;
                        SceneMove.Release(g.Who);
                        try { g.Who.SetLookAgent(Agent.Main); } catch { }
                        continue;
                    }
                    bool there = g.Who.Position.Distance(g.To.Position) <= 3.5f;
                    if (there)
                    {
                        // Arrived, now wait for the player
                        if (!g.Waiting) { g.Waiting = true; g.LagFor = 0f; SceneMove.Release(g.Who); try { g.Who.SetLookAgent(Agent.Main); } catch { } }
                        if (toPlayer <= 5f)
                        {
                            try { g.Who.SetLookAgent(null); } catch { }
                            Say(L.F("AIL_3cc9d40f", $"{g.Who.Name} te ha traído hasta {g.To.Name}."));
                            Guides.RemoveAt(i);
                            continue;
                        }
                    }
                    // Stop and wait if the player falls behind, carry on once they catch up
                    else if (!g.Waiting && toPlayer > 10f) { g.Waiting = true; g.LagFor = 0f; SceneMove.Release(g.Who); try { g.Who.SetLookAgent(Agent.Main); } catch { } Log.Info($"[scene] {g.Who.Name} stops: the player fell behind ({toPlayer:0.#} m)"); }
                    else if (g.Waiting && toPlayer <= 5f) { g.Waiting = false; g.LagFor = 0f; try { g.Who.SetLookAgent(null); } catch { } SceneMove.GoTo(g.Who, g.To); continue; }
                    if (g.Waiting && toPlayer > 10f) GuideLags(g, toPlayer, dt, now);
                }

                for (int i = Calls.Count - 1; i >= 0; i--)
                {
                    var c = Calls[i];
                    c.Left -= dt;
                    if (c.Who == null || !c.Who.IsActive() || c.Left <= 0f) { SceneMove.Release(c.Who); Calls.RemoveAt(i); continue; }
                    if (c.Arrived)
                    {
                        // Came while the player was still talking one to one: they stand by, and join once that talk is over
                        if (ChatSession.IsBusy || TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager?.IsConversationInProgress == true) continue;
                        SceneMove.Release(c.Who);
                        Calls.RemoveAt(i);
                        JoinAfterCall(c);
                        continue;
                    }
                    var to = c.Quiet && c.Caller != null && c.Caller.IsActive() ? c.Caller.Position : me;
                    if (c.Who.Position.Distance(to) > 3.5f) continue;
                    SceneMove.Release(c.Who);
                    if (c.Quiet) { Calls.RemoveAt(i); try { c.Who.SetLookAgent(c.Caller != null && c.Caller.IsActive() ? c.Caller : Agent.Main); } catch { } continue; }
                    if (GroupTalk.Active) { Calls.RemoveAt(i); GroupTalk.Join(c.Who); Say(L.F("AIL_2215a755", $"{c.Who.Name} se une a la conversación.")); continue; }
                    if (ChatSession.IsBusy || TaleWorlds.CampaignSystem.Campaign.Current?.ConversationManager?.IsConversationInProgress == true)
                    {
                        c.Arrived = true; c.Left = 300f;
                        SceneMove.Hold(c.Who, Agent.Main);
                        Say(L.F("AIL_e7b1d004", $"{c.Who.Name} llega y se queda a tu lado."));
                        NoteTo(c.Caller, $"[{c.Who.Name} has come over at your call and now stands here with you and the person in front of you.]");
                        continue;
                    }
                    Calls.RemoveAt(i);
                    JoinAfterCall(c);
                }
            }
            catch (Exception ex) { Log.Error("Scene actions", ex); Guides.Clear(); Calls.Clear(); }
        }

        /// <summary>Someone called over is here and no talk is going on: they and whoever called them become a talk with the player.</summary>
        private static void JoinAfterCall(Call c)
        {
            try
            {
                var agents = new List<Agent>();
                if (c.Caller != null && c.Caller.IsActive() && Agent.Main != null && c.Caller.Position.Distance(Agent.Main.Position) <= 8f) agents.Add(c.Caller);
                agents.Add(c.Who);
                if (GroupTalk.Active) GroupTalk.Join(c.Who);
                else if (!GroupTalk.TakeOver(agents, new List<string> { $"({c.Who.Name} has come over, called by {(c.Caller != null ? c.Caller.Name : "someone")}.)" }, c.Who, false)) { try { c.Who.SetLookAgent(Agent.Main); } catch { } return; }
                Say(L.F("AIL_2215a755", $"{c.Who.Name} se une a la conversación."));
            }
            catch (Exception ex) { Log.Error("Join after a call", ex); }
        }

        // ============================================================ posts, walks with someone, seats

        private static void TickPosts(float dt)
        {
            if (Posts.Count == 0) return;
            foreach (var a in Posts.Keys.ToList())
            {
                try
                {
                    string why = null;
                    if (a == null || Mission.Current == null || a.Mission != Mission.Current || !a.IsActive()) why = "gone";
                    else if (Combat.Fighting || !a.IsAlarmStateNormal()) why = "trouble around them";
                    else if ((Posts[a] -= dt) <= 0f) why = "waited long enough";
                    if (why == null) continue;
                    Posts.Remove(a);
                    if (why != "gone") SceneMove.Release(a);
                    Log.Info($"[scene] {a?.Name} stops waiting ({why})");
                }
                catch { Posts.Remove(a); }
            }
        }

        private static void TickWith(float dt)
        {
            if (With.Count == 0) return;
            foreach (var a in With.Keys.ToList())
            {
                var w = With[a];
                string why = null;
                if (a == null || !a.IsActive()) why = "gone";
                else if (w.leader == null || !w.leader.IsActive()) why = "the other is gone";
                else if (Combat.Fighting) why = "a fight";
                else if (w.left - dt <= 0f) why = "walked along long enough";
                if (why == null) { With[a] = (w.leader, w.left - dt); continue; }
                With.Remove(a);
                if (why != "gone") SceneMove.Release(a);
                if (why == "the other is gone" && a != null && a.IsActive()) Say(L.F("AIL_e7b1d001", $"{a.Name} deja de ir con {w.leader?.Name}."));
                Log.Info($"[scene] {a?.Name} no longer goes along with {w.leader?.Name} ({why})");
            }
        }

        /// <summary>Someone on their way to a seat who cannot get there in time (or finds it taken) gives it up and stays on their feet.</summary>
        private static void TickSeats(float dt)
        {
            for (int i = Seats.Count - 1; i >= 0; i--)
            {
                var s = Seats[i];
                try
                {
                    if (s.who == null || !s.who.IsActive()) { Seats.RemoveAt(i); continue; }
                    if (s.who.IsSitting() || (s.who.IsUsingGameObject && s.seat != null && s.seat.UserAgent == s.who)) { Seats.RemoveAt(i); continue; }
                    float left = s.left - dt;
                    bool taken = s.seat == null || s.seat.IsDeactivated || (s.seat.HasUser && s.seat.UserAgent != s.who);
                    if (!taken && left > 0f) { Seats[i] = (s.who, s.seat, left); continue; }
                    Seats.RemoveAt(i);
                    try { s.who.AIMoveToGameObjectDisable(); } catch { }
                    try { if (s.who.IsUsingGameObject) s.who.StopUsingGameObject(); s.who.DisableScriptedMovement(); } catch { }
                    Say(L.F("AIL_e7b1d005", $"{s.who.Name} no llega al asiento y se queda de pie."));
                    Log.Info($"[scene] {s.who.Name} lost the seat on the way ({(taken ? "someone else took it" : "could not reach it in time")})");
                }
                catch (Exception ex) { Log.Error("On the way to a seat", ex); Seats.RemoveAt(i); }
            }
        }

        // ============================================================ reacting in character

        private static readonly Tpl TReact = new Tpl("scene/reacts_to_player.txt",
@"You are {name}, in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord.
Who you are: {who}
{situation}
Say ONE short line out loud to {player}, the way you really would in this moment (under {max_chars} characters, in {language}): spoken words only, at most a brief *gesture*. Do not say again what you already called out: {before}
Then decide what you do now: {choices}.
Never mention games or AI.
Answer ONLY with JSON: { ""line"": ""..."", ""mood"": ""one of: {moods}"", ""do"": ""{choice_words}"" }");

        private static readonly Tpl TGuideBehind = new Tpl("scene/guide_player_behind.txt",
            "You are leading {player} on foot to {target}, as you agreed, and they have stopped following you: they stand about {dist} m behind. React in character: call them, ask what is wrong, wait, or give up.");
        private static readonly Tpl TGuideAway = new Tpl("scene/guide_player_walked_off.txt",
            "You were leading {player} on foot to {target}, as you agreed, but they walked away from you instead of following: they are about {dist} m off now. React in character: call after them, ask what is wrong, insist, tease, or give up.");
        internal static readonly Tpl TLeadBehind = new Tpl("scene/lead_player_behind.txt",
            "You are taking {player} into {target}, as you agreed, and they have stopped following you: they stand about {dist} m behind. React in character: call them, ask what is wrong, wait, or give up.");
        internal static readonly Tpl TLeadAway = new Tpl("scene/lead_player_walked_off.txt",
            "You were taking {player} into {target}, but they walked away from you instead of following: they are about {dist} m off now. React in character: call after them, ask what is wrong, insist, tease, or give up.");
        internal static readonly Tpl TLeadDoor = new Tpl("scene/lead_player_not_entering.txt",
            "You have brought {player} to the door into {target}, but they are not going in. Say something in character: invite them in, ask what they are waiting for, or let it go.");
        private static readonly Tpl TChoices = new Tpl("scene/react_choices.txt",
            "wait = stay where you are and wait for them a little longer; go_back = walk back to them; give_up = stop leading them and go about your own business");
        internal static readonly Tpl TDoorChoices = new Tpl("scene/react_choices_door.txt",
            "wait = wait at the door a little longer; give_up = let it go and stop leading them");

        public static void Touch() { var _ = TReact.Key + TGuideBehind.Key + TGuideAway.Key + TLeadBehind.Key + TLeadAway.Key + TLeadDoor.Key + TChoices.Key + TDoorChoices.Key + TCalled.Key + TDrawn.Key + TDoing.Key; }

        internal static string GuideChoices => TChoices.Text;

        private static readonly HashSet<Agent> Reacting = new HashSet<Agent>();
        private static readonly Dictionary<Agent, List<string>> ReactSaid = new Dictionary<Agent, List<string>>();
        private static int _reactGen;

        /// <summary>
        /// The character says one line in character about what is going on (the player lags behind, walks off, will not
        /// go through the door) and picks what to do next. The line plays over their head; <paramref name="then"/> gets
        /// the choice (one word of <paramref name="choices"/>, or null when the AI gave none). False if it cannot be asked now.
        /// </summary>
        internal static bool React(Agent who, Tpl situation, string choices, Action<string> then, params object[] args)
        {
            try
            {
                var cfg = S.Get;
                var m = Mission.Current;
                if (who == null || !who.IsActive() || m == null || Reacting.Contains(who) || !(who.Character is TaleWorlds.CampaignSystem.CharacterObject)) return false;
                string desc = Ambient.Describe(who, out var sp);
                string player = ContextBuilder.PlayerNameFor(sp) ?? "the stranger";
                var pairs = new List<object>(args ?? new object[0]) { "player", player };
                string sit = situation.Format(pairs.ToArray());
                var words = choices.Split(';').Select(x => x.Split('=')[0].Trim()).Where(x => x.Length > 0).ToList();
                ReactSaid.TryGetValue(who, out var said);
                string prompt = TReact.Format("name", sp.Name, "place", TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement?.Name?.ToString() ?? "this place", "who", desc,
                    "situation", sit, "player", player, "max_chars", cfg.AmbientMaxChars, "language", cfg.Language,
                    "before", said != null && said.Count > 0 ? string.Join(" | ", said.Select(x => "«" + x + "»")) : "(nothing yet)",
                    "choices", choices, "moods", Gestures.Moods, "choice_words", string.Join("|", words));
                Reacting.Add(who);
                try { Bubbles.Thinking(who, true); } catch { }
                int gen = _reactGen, session = World.WorldSim.Session;
                Log.Info($"[scene] {sp.Name} reacts: {sit}");
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, maxTokensOverride: 260,
                    preferredProvider: sp.IsHero ? cfg.ProviderHeroes : cfg.ProviderResidents, task: "ambient").ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        Reacting.Remove(who);
                        try { Bubbles.Thinking(who, false); } catch { }
                        if (gen != _reactGen || session != World.WorldSim.Session || Mission.Current != m || !who.IsActive()) return;
                        string choice = null;
                        try
                        {
                            if (res != null && res.Ok && res.Text != null)
                            {
                                int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                                if (a >= 0 && b > a)
                                {
                                    var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                                    string line = ((string)j["line"] ?? "").Trim();
                                    string d = ((string)j["do"] ?? "").Trim().ToLowerInvariant();
                                    choice = words.FirstOrDefault(w => d.StartsWith(w, StringComparison.OrdinalIgnoreCase));
                                    if (line.Length > 1) SpeakLine(who, sp, line, (string)j["mood"]);
                                }
                            }
                            else Log.Warn("[scene] no reaction line: " + res?.Error);
                        }
                        catch (Exception ex) { Log.Error("Scene reaction", ex); }
                        try { then?.Invoke(choice); } catch (Exception ex) { Log.Error("Scene reaction: what they do", ex); }
                    });
                });
                return true;
            }
            catch (Exception ex) { Log.Error("Scene reaction", ex); Reacting.Remove(who); return false; }
        }

        /// <summary>A line said aloud in the scene: over their head (and voiced), heard by those around, kept in their memory.</summary>
        private static void SpeakLine(Agent who, Speaker sp, string line, string mood)
        {
            int max = S.Get.AmbientMaxChars + 60;
            if (line.Length > max) line = line.Substring(0, max);
            Ambient.Enqueue(Mission.Current, new List<(Agent who, Agent to, string text, string mood, string pre, bool deferred)> { (who, Agent.Main, line, mood, null, false) }, append: true);
            if (!ReactSaid.TryGetValue(who, out var said)) ReactSaid[who] = said = new List<string>();
            said.Add(line);
            while (said.Count > 3) said.RemoveAt(0);
            try
            {
                if (sp.IsHero) sp.Memory.Turns.Add(new ChatTurn { Role = "npc", Text = "[Called out in the scene] " + line, Day = TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays });
                Ambient.Overhear(new Speaker[0], line, sp, 1f, who);
                GroupTalk.Note($"{sp.Name}: {line}");
            }
            catch { }
        }

        /// <summary>A note in that character's history (English, for the AI), so their next line starts from it.</summary>
        private static void NoteTo(Agent a, string text)
        {
            try
            {
                if (a == null || !(a.Character is TaleWorlds.CampaignSystem.CharacterObject)) return;
                Ambient.Describe(a, out var sp);
                sp.Memory.Turns.Add(new ChatTurn { Role = "player", Text = text, Day = TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays });
            }
            catch { }
        }

        /// <summary>The guide has been waiting for a player who does not come: now and then they react; after three times they give up.</summary>
        private static void GuideLags(Guide g, float toPlayer, float dt, float now)
        {
            g.LagFor += dt;
            if (g.Asking || g.LagFor < 5f || now < g.NextReact) return;
            if (g.Reacts >= 3)
            {
                Guides.Remove(g);
                SceneMove.Release(g.Who);
                try { g.Who.SetLookAgent(null); } catch { }
                Say(L.F("AIL_e7b1d007", $"{g.Who.Name} se cansa de esperarte."));
                Log.Info($"[scene] {g.Who.Name} gives up guiding the player (the player would not follow)");
                return;
            }
            g.Reacts++;
            g.Asking = true;
            var gg = g;
            bool asked = React(g.Who, toPlayer >= 25f ? TGuideAway : TGuideBehind, TChoices.Text, choice =>
            {
                gg.Asking = false;
                if (!Guides.Contains(gg) || gg.Who == null || !gg.Who.IsActive()) return;
                float t = Mission.Current?.CurrentTime ?? 0f;
                if (choice == "give_up")
                {
                    Guides.Remove(gg);
                    SceneMove.Release(gg.Who);
                    try { gg.Who.SetLookAgent(null); } catch { }
                    Say(L.F("AIL_e7b1d006", $"{gg.Who.Name} deja de guiarte."));
                    Log.Info($"[scene] {gg.Who.Name} gives up guiding the player");
                    return;
                }
                if (choice == "go_back" && Agent.Main != null && gg.Who.Position.Distance(Agent.Main.Position) > 5f && SceneMove.GoTo(gg.Who, Agent.Main))
                {
                    gg.Back = true;
                    Say(L.F("AIL_e7b1d008", $"{gg.Who.Name} vuelve a por ti."));
                    Log.Info($"[scene] {gg.Who.Name} goes back to the player who stopped following");
                }
                gg.NextReact = t + 20f;
                gg.Left = Math.Max(gg.Left, 60f);
            }, "target", g.To.Name, "dist", (int)toPlayer);
            if (!asked) { g.Asking = false; g.NextReact = now + 20f; }
        }

        // ============================================================ calling someone over (they may refuse)

        private static readonly Tpl TCalled = new Tpl("scene/called_over.txt",
@"You are {name}, in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord.
Who you are: {who}
{caller}, {tie}, has just called out to you from about {dist} m away: «{line}»
Decide, as yourself, whether you go over to them now. Kin, friends, your lord or master and anyone with the right to summon you are usually answered; a stranger, a rival or someone below you may be ignored, put off or refused, and nobody drops urgent business on a whim.
Answer ONLY with JSON: { ""comes"": true or false, ""line"": ""what you call back, ONE short line under {max_chars} characters in {language}, or empty to just come or ignore it"", ""mood"": ""one of: {moods}"" }");

        private static TaleWorlds.CampaignSystem.Hero HeroOf(Agent a) => (a?.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;

        /// <summary>What the caller is to the one called, in a few English words.</summary>
        private static string TieOf(Agent called, Agent caller)
        {
            try
            {
                var x = HeroOf(called); var y = HeroOf(caller);
                if (x != null && y != null)
                {
                    if (x.Spouse == y) return "your spouse";
                    if (x.Father == y || x.Mother == y) return "your " + (y.IsFemale ? "mother" : "father");
                    if (y.Father == x || y.Mother == x) return "your " + (y.IsFemale ? "daughter" : "son");
                    if (x.Siblings.Contains(y)) return "your " + (y.IsFemale ? "sister" : "brother");
                    if (x.Clan != null && x.Clan == y.Clan) return y.Clan.Leader == y ? "the head of your own house" : "kin of your own house";
                    if (x.Clan?.Kingdom != null && x.Clan.Kingdom.Leader == y) return "your sovereign";
                    int r = x.GetRelation(y);
                    return r >= 30 ? "a friend of yours" : r <= -30 ? "someone you dislike" : x.MapFaction != null && x.MapFaction == y.MapFaction ? "a noble of your realm" : "someone you know";
                }
                if (y != null) return y.IsLord ? "a noble, while you are common folk" : "a person of some standing here";
                if (x != null) return "common folk, well below you";
                return "someone of this place";
            }
            catch { return "someone of this place"; }
        }

        /// <summary>
        /// The one called hears it and decides, as themselves, whether to come: if so they walk over, otherwise they
        /// call back (or ignore it) and the caller is told. Without an answer from the AI they come, as before.
        /// </summary>
        private static void AskCalled(Agent caller, Agent target, string line)
        {
            void Come()
            {
                if (target == null || !target.IsActive() || Agent.Main == null) return;
                Unpost(target);
                if (!SceneMove.GoTo(target, Agent.Main)) { Say(L.F("AIL_e7b1d003", $"{target.Name} no acude a la llamada de {caller.Name}.")); NoteTo(caller, $"[{target.Name} could not come when you called.]"); return; }
                Calls.RemoveAll(c => c.Who == target);
                Calls.Add(new Call { Who = target, Caller = caller, Left = 120f });
                Log.Info($"[scene] {target.Name} answers the call of {caller.Name}");
            }
            try
            {
                var cfg = S.Get;
                var m = Mission.Current;
                if (m == null || !(target.Character is TaleWorlds.CampaignSystem.CharacterObject)) { Come(); return; }
                string desc = Ambient.Describe(target, out var sp);
                string prompt = TCalled.Format("name", sp.Name, "place", TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement?.Name?.ToString() ?? "this place", "who", desc,
                    "caller", caller.Name, "tie", TieOf(target, caller), "dist", (int)target.Position.Distance(caller.Position),
                    "line", (line ?? "").Replace("«", "\"").Replace("»", "\""), "max_chars", cfg.AmbientMaxChars, "language", cfg.Language, "moods", Gestures.Moods);
                int gen = _reactGen, session = World.WorldSim.Session;
                Reacting.Add(target);
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, maxTokensOverride: 200,
                    preferredProvider: sp.IsHero ? cfg.ProviderHeroes : cfg.ProviderResidents, task: "ambient").ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        Reacting.Remove(target);
                        if (gen != _reactGen || session != World.WorldSim.Session || Mission.Current != m || !target.IsActive()) return;
                        bool comes = true; string back = "", mood = null;
                        try
                        {
                            if (res != null && res.Ok && res.Text != null)
                            {
                                int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                                if (a >= 0 && b > a)
                                {
                                    var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                                    var c = j["comes"];
                                    if (c != null) comes = c.Type == JTokenType.Boolean ? (bool)c : !string.Equals(c.ToString().Trim(), "false", StringComparison.OrdinalIgnoreCase);
                                    back = ((string)j["line"] ?? "").Trim();
                                    mood = (string)j["mood"];
                                }
                            }
                            else Log.Warn("[scene] no answer to the call, they come: " + res?.Error);
                        }
                        catch (Exception ex) { Log.Error("Answer to a call", ex); }
                        if (back.Length > 1) SpeakLine(target, sp, back, mood);
                        if (comes) { Come(); return; }
                        Say(L.F("AIL_e7b1d003", $"{target.Name} no acude a la llamada de {caller.Name}."));
                        NoteTo(caller, $"[{target.Name} did not come when you called them over{(back.Length > 1 ? $"; they called back: «{back}»" : "")}.]");
                        Log.Info($"[scene] {target.Name} ignores the call of {caller.Name}");
                    });
                });
            }
            catch (Exception ex) { Log.Error("Call someone over", ex); Reacting.Remove(target); Come(); }
        }
    }
}
