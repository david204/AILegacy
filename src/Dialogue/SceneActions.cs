using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// What a character can do in a scene besides talking (follow you, guide you to someone, call someone over, leave...).
    /// The AI picks the action in group conversations.
    /// </summary>
    internal static class SceneActions
    {
        private class Guide { public Agent Who, To; public float Left; public bool Waiting; }
        private class Call { public Agent Who; public float Left; public bool Quiet; }

        private static readonly HashSet<Agent> Followers = new HashSet<Agent>();
        private static readonly List<Guide> Guides = new List<Guide>();
        private static readonly List<Call> Calls = new List<Call>();
        private static Mission _mission;
        // NPC-on-NPC arrests: who goes to the dungeon when the scene ends, and who fades out of the scene now
        private static readonly List<(TaleWorlds.CampaignSystem.Hero who, TaleWorlds.CampaignSystem.Settlements.Settlement where)> Jail = new List<(TaleWorlds.CampaignSystem.Hero, TaleWorlds.CampaignSystem.Settlements.Settlement)>();
        private static readonly List<(Agent who, float left)> Leaving = new List<(Agent, float)>();

        /// <summary>New or loaded save: drop anything pending from the previous one, above all queued arrests.</summary>
        public static void ResetSession()
        {
            Jail.Clear(); Leaving.Clear(); Followers.Clear(); Guides.Clear(); Calls.Clear(); Ring.Clear(); Away.Clear(); Grounded.Clear(); DownUntil.Clear(); Lying.Clear();
            _mission = null;
            SceneTravel.Reset();
        }

        public const string Help =
@"""action"" is what the speaker DOES besides speaking, or """" for nothing. Use one only when the talk really leads to it:
 - ""follow"": walks along with {player} from now on (they were asked to come along and agree).
 - ""stay"": stops following and remains here.
 - ""guide:<exact name>"": leads {player} on foot to that person (someone from the 'Elsewhere' list), waiting if {player} falls behind.
 - ""call:<exact name>"": calls that person from the 'Elsewhere' list to come over and join this conversation (only someone they could rightly summon: kin, a servant, a subordinate, a friend).
 - ""leave"": ends their part in the conversation and goes back to their own business.
 - ""draw_weapon"": bares steel as a threat, without attacking (yet). ""sheathe_weapon"": puts it away again when calmed or ordered to.
 - ""sit_down"": goes to the nearest free seat and sits, or sits on the ground where they stand if there is none (they are invited to sit, or settle in for a long talk).
 - ""stand_up"": rises from where they sit or leaves what they were busy with, to face {player}.
 - ""call_guards"": orders the guard to remove {player} from their presence. ONLY for someone with authority in this place (its lord, the lord's family, a noble of this realm, the ruler), and only when {player} has truly earned it: insults, threats, refusing to leave when told, or insolence a proud noble would not swallow from a nobody. They say it aloud in the same line.
 - ""arrest:<exact name>"": takes ANOTHER person who is present (not {player}) away to the cells. ONLY a guard or soldier on duty, or the lord of this place, in a town or castle; and only with grave cause that they themselves find convincing: that person is accused before witnesses of blood or treason AND something visible supports it (bloodied hands, a body, a weapon, a confession, their own guilty answers), or they were caught in the act. An accusation alone, from a stranger, against a person of rank is not enough: then they question, they do not arrest. Never against the lord of the place or the ruler.";

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Gray));

        /// <summary>Following, guiding or walking over. Busy agents don't start chatting on their own.</summary>
        public static bool Busy(Agent a) => Followers.Contains(a) || Guides.Any(g => g.Who == a) || Calls.Any(c => c.Who == a) || SceneTravel.Leading(a);

        /// <summary>The list of actions for the AI, with the places someone could lead the player to from this scene.</summary>
        public static string HelpFor(string player) => Help.Replace("{player}", player) + SceneTravel.HelpLine(player);

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
            SceneMove.Forget(); Gestures.Forget();
        }

        /// <summary>Applies the action attached to a line. Returns true if the character leaves the conversation.</summary>
        public static bool Apply(Agent who, string action, List<Agent> elsewhere, string line = null)
        {
            try
            {
                if (who == null || string.IsNullOrWhiteSpace(action) || !S.Get.SceneErrands || Agent.Main == null || !who.IsAIControlled) return false;
                Sync();
                action = action.Trim();
                string verb = action, arg = "";
                int colon = action.IndexOf(':');
                if (colon > 0) { verb = action.Substring(0, colon).Trim(); arg = action.Substring(colon + 1).Trim(); }
                Agent Find() => arg.Length < 2 ? null
                    : elsewhere.FirstOrDefault(x => x.IsActive() && string.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase))
                      ?? elsewhere.FirstOrDefault(x => x.IsActive() && (x.Name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0 || arg.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0));

                switch (verb.ToLowerInvariant())
                {
                    case "follow":
                    case "go_with":      // the name some models bring from other mods
                        if (Followers.Contains(who) || !SceneMove.GoTo(who, Agent.Main)) return false;
                        Guides.RemoveAll(g => g.Who == who);
                        Followers.Add(who);
                        SceneTravel.Follow(who);
                        Say(L.F("AIL_dd2f52c0", $"{who.Name} te acompaña."));
                        Log.Info($"[scene] {who.Name} accompanies you");
                        return false;
                    case "stay":
                    {
                        bool led = SceneTravel.Leading(who);
                        SceneTravel.Drop(who);
                        if (!Followers.Remove(who) && !led) return false;
                        SceneMove.Release(who);
                        Say(L.F("AIL_78680a96", $"{who.Name} se queda aquí."));
                        return false;
                    }
                    case "go_into":
                    {
                        string place = SceneTravel.GoInto(who, arg);
                        if (place == null) return false;
                        Guides.RemoveAll(g => g.Who == who);
                        Say(L.F("AIL_e7b1c641", $"{who.Name} te lleva a {place}: síguele hasta la puerta y crúzala."));
                        return true;
                    }
                    case "guide":
                    {
                        var to = Find();
                        if (to == null || to == who || !SceneMove.GoTo(who, to)) return false;
                        Followers.Remove(who); SceneTravel.Drop(who);
                        Guides.RemoveAll(g => g.Who == who);
                        Guides.Add(new Guide { Who = who, To = to, Left = 240f });
                        Say(L.F("AIL_5f595733", $"{who.Name} te lleva hasta {to.Name}. Síguele."));
                        Log.Info($"[scene] {who.Name} guides the player to {to.Name}");
                        return true;
                    }
                    case "call":
                    {
                        var target = Find();
                        if (target == null || target == who || Busy(target) || !SceneMove.GoTo(target, Agent.Main)) return false;
                        Calls.Add(new Call { Who = target, Left = 120f });
                        Say(L.F("AIL_7f71ac3a", $"{who.Name} llama a {target.Name}."));
                        Log.Info($"[scene] {who.Name} calls {target.Name}");
                        return false;
                    }
                    case "draw_weapon":
                        try { who.WieldInitialWeapons(); } catch { }
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_a6a88727", $"{who.Name} desenvaina."), Colors.Red));
                        return false;
                    case "sheathe_weapon":
                        try
                        {
                            who.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.WithAnimation);
                            who.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.WithAnimation);
                        }
                        catch { }
                        Say(L.F("AIL_1ee0d3b1", $"{who.Name} envaina."));
                        return false;
                    case "sit_down":
                        try
                        {
                            if (who.IsUsingGameObject || who.IsSitting()) return false;
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
                            if (best == null) { SitOnGround(who); return false; }
                            Followers.Remove(who); SceneTravel.Drop(who); SceneMove.Release(who);
                            who.AIMoveToGameObjectEnable(best, bestChair, Agent.AIScriptedFrameFlags.NoAttack);
                            Say(L.F("AIL_67c8cba8", $"{who.Name} va a sentarse."));
                        }
                        catch (Exception ex) { Log.Error("Sit down", ex); }
                        return false;
                    case "stand_up":
                        try { if (who.IsUsingGameObject) who.StopUsingGameObject(); StandFromGround(who); who.SetLookAgent(Agent.Main); } catch { }
                        return false;
                    case "call_guards":
                    {
                        var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                        var h = (who.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                        if (st == null || h == null) return false;
                        bool lordHere = st.OwnerClan != null && st.OwnerClan == h.Clan;
                        bool authority = lordHere || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k && k.Leader == h) || (h.IsLord && h.MapFaction != null && h.MapFaction == st.MapFaction);
                        if (!authority) return false;
                        string what = $"Moments ago {h.Name}, {(st.OwnerClan?.Leader == h ? "the lord of this place" : lordHere ? "of the house that rules this place" : "a noble with authority here")}, " +
                                      $"ordered you in front of everyone to remove the armed person now in front of you from their presence, with these words: \"{(line ?? "").Replace("\"", "'")}\". " +
                                      "No blood was shed: the offence is insolence in a noble's hall. You carry out the order. They either come away quietly with you (let_go, once they agree to leave), " +
                                      "pay for the insult (fine), or go to the cells (arrest) if they argue with the order, mock or resist.";
                        if (!Justice.Summon(what, who, true)) { Log.Info($"[scene] {who.Name} calls the guard, but there is no guard nearby (or justice is off)"); return false; }
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_95594522", $"{who.Name} llama a la guardia: entrégate, convéncele o resístete."), Colors.Red));
                        Log.Info($"[scene] {who.Name} calls the guard on the player");
                        return false;
                    }
                    case "arrest":
                    {
                        // A guard on duty or the local lord takes some other present character (never the player) to the cells
                        var st = TaleWorlds.CampaignSystem.Settlements.Settlement.CurrentSettlement;
                        var wc = who.Character as TaleWorlds.CampaignSystem.CharacterObject;
                        if (st == null || wc == null || !S.Get.AllowNpcArrests || (!st.IsTown && !st.IsCastle)) return false;
                        var wh = wc.HeroObject;
                        bool guard = !wc.IsHero && (wc.Occupation == TaleWorlds.CampaignSystem.Occupation.Guard || wc.Occupation == TaleWorlds.CampaignSystem.Occupation.Soldier || wc.Occupation == TaleWorlds.CampaignSystem.Occupation.PrisonGuard);
                        bool lord = wh != null && ((st.OwnerClan != null && st.OwnerClan == wh.Clan) || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k2 && k2.Leader == wh));
                        if (!guard && !lord) return false;
                        var target = Mission.Current.Agents.Where(x => x != null && x != who && x != Agent.Main && x.IsHuman && x.IsActive() && x.Position.Distance(who.Position) <= 25f
                                                                       && x.Character is TaleWorlds.CampaignSystem.CharacterObject tc0 && tc0.IsHero && tc0.HeroObject != null)
                                                           .FirstOrDefault(x => string.Equals(x.Name, arg, StringComparison.OrdinalIgnoreCase) || (arg.Length > 2 && x.Name.IndexOf(arg, StringComparison.OrdinalIgnoreCase) >= 0));
                        var th = (target?.Character as TaleWorlds.CampaignSystem.CharacterObject)?.HeroObject;
                        if (th == null || th.IsChild || th == TaleWorlds.CampaignSystem.Hero.MainHero || th.Clan == TaleWorlds.CampaignSystem.Clan.PlayerClan) return false;
                        // Nobody arrests the lord in his own seat
                        if (th == st.OwnerClan?.Leader || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k3 && k3.Leader == th)) return false;
                        if (Jail.Any(j => j.who == th)) return false;
                        var led = th.PartyBelongedTo;
                        if (led != null && !led.IsMainParty && led.LeaderHero == th && led.MemberRoster.TotalManCount > 1) return false;
                        Jail.Add((th, st));
                        Leaving.Add((target, 7f));
                        SceneMove.GoTo(who, target);
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
                        Say(L.F("AIL_56f0d4a1", $"{who.Name} sale de la conversación."));
                        return true;
                }
            }
            catch (Exception ex) { Log.Error("Scene action", ex); }
            return false;
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
                Followers.Remove(who); SceneTravel.Drop(who); SceneMove.Release(who);
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
                Followers.Remove(who); SceneTravel.Drop(who); SceneMove.Release(who);
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
                if (Followers.Count == 0 && Guides.Count == 0 && Calls.Count == 0) return;
                Sync();
                if (Mission.Current == null || Agent.Main == null || !Agent.Main.IsActive()) { Followers.Clear(); Guides.Clear(); Calls.Clear(); return; }
                var me = Agent.Main.Position;

                Followers.RemoveWhere(a => a == null || !a.IsActive());

                for (int i = Guides.Count - 1; i >= 0; i--)
                {
                    var g = Guides[i];
                    g.Left -= dt;
                    if (g.Who == null || !g.Who.IsActive() || g.To == null || !g.To.IsActive() || g.Left <= 0f) { SceneMove.Release(g.Who); Guides.RemoveAt(i); continue; }
                    float toPlayer = g.Who.Position.Distance(me);
                    if (g.Who.Position.Distance(g.To.Position) <= 3.5f)
                    {
                        // Arrived, now wait for the player
                        if (!g.Waiting) { g.Waiting = true; SceneMove.Release(g.Who); try { g.Who.SetLookAgent(Agent.Main); } catch { } }
                        if (toPlayer <= 5f)
                        {
                            try { g.Who.SetLookAgent(null); } catch { }
                            Say(L.F("AIL_3cc9d40f", $"{g.Who.Name} te ha traído hasta {g.To.Name}."));
                            Guides.RemoveAt(i);
                        }
                        continue;
                    }
                    // Stop and wait if the player falls behind, carry on once they catch up
                    if (!g.Waiting && toPlayer > 10f) { g.Waiting = true; SceneMove.Release(g.Who); try { g.Who.SetLookAgent(Agent.Main); } catch { } }
                    else if (g.Waiting && toPlayer <= 5f) { g.Waiting = false; try { g.Who.SetLookAgent(null); } catch { } SceneMove.GoTo(g.Who, g.To); }
                }

                for (int i = Calls.Count - 1; i >= 0; i--)
                {
                    var c = Calls[i];
                    c.Left -= dt;
                    if (c.Who == null || !c.Who.IsActive() || c.Left <= 0f) { SceneMove.Release(c.Who); Calls.RemoveAt(i); continue; }
                    if (c.Who.Position.Distance(me) > 3.5f) continue;
                    SceneMove.Release(c.Who);
                    Calls.RemoveAt(i);
                    if (!c.Quiet) { GroupTalk.Join(c.Who); Say(L.F("AIL_2215a755", $"{c.Who.Name} se une a la conversación.")); }
                }
            }
            catch (Exception ex) { Log.Error("Scene actions", ex); Guides.Clear(); Calls.Clear(); }
        }
    }
}
