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
    /// Lo que un personaje puede HACER en una escena además de hablar (lo decide la IA en la conversación de grupo):
    /// acompañarte, quedarse, guiarte hasta alguien, llamar a alguien para que venga, o marcharse.
    /// </summary>
    internal static class SceneActions
    {
        private class Guide { public Agent Who, To; public float Left; public bool Waiting; }
        private class Call { public Agent Who; public float Left; public bool Quiet; }

        private static readonly HashSet<Agent> Followers = new HashSet<Agent>();
        private static readonly List<Guide> Guides = new List<Guide>();
        private static readonly List<Call> Calls = new List<Call>();
        private static Mission _mission;
        // Arrestos de un personaje por otro: quién va a la mazmorra al acabar la escena, y quién desaparece de ella ahora
        private static readonly List<(TaleWorlds.CampaignSystem.Hero who, TaleWorlds.CampaignSystem.Settlements.Settlement where)> Jail = new List<(TaleWorlds.CampaignSystem.Hero, TaleWorlds.CampaignSystem.Settlements.Settlement)>();
        private static readonly List<(Agent who, float left)> Leaving = new List<(Agent, float)>();

        /// <summary>Partida nueva o cargada: nada pendiente de la anterior (sobre todo, nadie por encerrar).</summary>
        public static void ResetSession()
        {
            Jail.Clear(); Leaving.Clear(); Followers.Clear(); Guides.Clear(); Calls.Clear(); Ring.Clear();
            _mission = null;
        }

        /// <summary>Lista para la IA de lo que se puede hacer.</summary>
        public const string Help =
@"""action"" is what the speaker DOES besides speaking, or """" for nothing. Use one only when the talk really leads to it:
 - ""follow"": walks along with {player} from now on (they were asked to come along and agree).
 - ""stay"": stops following and remains here.
 - ""guide:<exact name>"": leads {player} on foot to that person (someone from the 'Elsewhere' list), waiting if {player} falls behind.
 - ""call:<exact name>"": calls that person from the 'Elsewhere' list to come over and join this conversation (only someone they could rightly summon: kin, a servant, a subordinate, a friend).
 - ""leave"": ends their part in the conversation and goes back to their own business.
 - ""draw_weapon"": bares steel as a threat, without attacking (yet). ""sheathe_weapon"": puts it away again when calmed or ordered to.
 - ""sit_down"": goes to the nearest free seat and sits (they are invited to sit, or settle in for a long talk).
 - ""stand_up"": rises from where they sit or leaves what they were busy with, to face {player}.
 - ""call_guards"": orders the guard to remove {player} from their presence. ONLY for someone with authority in this place (its lord, the lord's family, a noble of this realm, the ruler), and only when {player} has truly earned it: insults, threats, refusing to leave when told, or insolence a proud noble would not swallow from a nobody. They say it aloud in the same line.
 - ""arrest:<exact name>"": takes ANOTHER person who is present (not {player}) away to the cells. ONLY a guard or soldier on duty, or the lord of this place, in a town or castle; and only with grave cause that they themselves find convincing: that person is accused before witnesses of blood or treason AND something visible supports it (bloodied hands, a body, a weapon, a confession, their own guilty answers), or they were caught in the act. An accusation alone, from a stranger, against a person of rank is not enough: then they question, they do not arrest. Never against the lord of the place or the ruler.";

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Gray));

        /// <summary>¿Lo tenemos ocupado (siguiéndote, guiando, viniendo)? Entonces no se pone a charlar por su cuenta.</summary>
        public static bool Busy(Agent a) => Followers.Contains(a) || Guides.Any(g => g.Who == a) || Calls.Any(c => c.Who == a);

        private static void Sync()
        {
            if (_mission == Mission.Current) return;
            _mission = Mission.Current;
            Followers.Clear(); Guides.Clear(); Calls.Clear(); Ring.Clear();
            SceneMove.Forget(); Gestures.Forget();
        }

        /// <summary>Aplica la acción que acompaña a una frase. Devuelve true si el personaje sale de la conversación.</summary>
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
                        if (Followers.Contains(who) || !SceneMove.GoTo(who, Agent.Main)) return false;
                        Guides.RemoveAll(g => g.Who == who);
                        Followers.Add(who);
                        Say(L.F("AIL_dd2f52c0", $"{who.Name} te acompaña."));
                        Log.Info($"[escena] {who.Name} te acompaña");
                        return false;
                    case "stay":
                        if (!Followers.Remove(who)) return false;
                        SceneMove.Release(who);
                        Say(L.F("AIL_78680a96", $"{who.Name} se queda aquí."));
                        return false;
                    case "guide":
                    {
                        var to = Find();
                        if (to == null || to == who || !SceneMove.GoTo(who, to)) return false;
                        Followers.Remove(who);
                        Guides.RemoveAll(g => g.Who == who);
                        Guides.Add(new Guide { Who = who, To = to, Left = 240f });
                        Say(L.F("AIL_5f595733", $"{who.Name} te lleva hasta {to.Name}. Síguele."));
                        Log.Info($"[escena] {who.Name} guía al jugador hasta {to.Name}");
                        return true;
                    }
                    case "call":
                    {
                        var target = Find();
                        if (target == null || target == who || Busy(target) || !SceneMove.GoTo(target, Agent.Main)) return false;
                        Calls.Add(new Call { Who = target, Left = 120f });
                        Say(L.F("AIL_7f71ac3a", $"{who.Name} llama a {target.Name}."));
                        Log.Info($"[escena] {who.Name} llama a {target.Name}");
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
                            if (who.IsUsingGameObject) return false;
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
                            if (best == null) return false;
                            Followers.Remove(who); SceneMove.Release(who);
                            who.AIMoveToGameObjectEnable(best, bestChair, Agent.AIScriptedFrameFlags.NoAttack);
                            Say(L.F("AIL_67c8cba8", $"{who.Name} va a sentarse."));
                        }
                        catch (Exception ex) { Log.Error("Sentarse", ex); }
                        return false;
                    case "stand_up":
                        try { if (who.IsUsingGameObject) who.StopUsingGameObject(); who.SetLookAgent(Agent.Main); } catch { }
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
                        if (!Justice.Summon(what, who, true)) { Log.Info($"[escena] {who.Name} llama a la guardia, pero no hay ningún guardia cerca (o la justicia está apagada)"); return false; }
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_95594522", $"{who.Name} llama a la guardia: entrégate, convéncele o resístete."), Colors.Red));
                        Log.Info($"[escena] {who.Name} llama a la guardia contra el jugador");
                        return false;
                    }
                    case "arrest":
                    {
                        // Un guardia de servicio, o quien manda en el lugar, se lleva preso a OTRO personaje presente
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
                        // A quien manda aquí no se le arresta en su propia casa
                        if (th == st.OwnerClan?.Leader || (st.MapFaction is TaleWorlds.CampaignSystem.Kingdom k3 && k3.Leader == th)) return false;
                        if (Jail.Any(j => j.who == th)) return false;
                        var led = th.PartyBelongedTo;
                        if (led != null && !led.IsMainParty && led.LeaderHero == th && led.MemberRoster.TotalManCount > 1) return false;
                        Jail.Add((th, st));
                        Leaving.Add((target, 7f));
                        SceneMove.GoTo(who, target);
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_ff0ad962", $"{who.Name} detiene a {th.Name}: se la llevan a los calabozos de {st.Name}."), Colors.Red));
                        Log.Info($"[escena] {who.Name} detiene a {th.Name} en {st.Name}. Motivo dicho: {line}");
                        try
                        {
                            // El detenido no olvida quién le señaló
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
                        catch (Exception ex) { Log.Error("Arresto: memoria", ex); }
                        try { if (wh != null) Bonds.TouchOther(Speaker.FromHero(th), Speaker.FromHero(wh), -40, $"{wh.Name} had me arrested and thrown in the cells of {st.Name}"); } catch { }
                        World.WorldSim.OnIncident($"{th.Name}{(th.IsLord ? $", a noble of clan {th.Clan?.Name}," : "")} was arrested by the guard and taken to the cells after {TaleWorlds.CampaignSystem.Hero.MainHero.Name} accused them in front of witnesses. What was said as they were taken: \"{(line ?? "").Replace("\"", "'")}\"", st, false);
                        return false;
                    }
                    case "leave":
                        if (Followers.Remove(who)) SceneMove.Release(who);
                        Say(L.F("AIL_56f0d4a1", $"{who.Name} se aparta de la conversación."));
                        return true;
                }
            }
            catch (Exception ex) { Log.Error("Acción en la escena", ex); }
            return false;
        }

        // Corro: cada uno camina hasta su sitio alrededor de ti y se queda mirándote
        private static readonly List<(Agent who, Vec2 slot, float left)> Ring = new List<(Agent, Vec2, float)>();

        /// <summary>Los del grupo se colocan en semicírculo delante de ti, a un par de pasos, y te miran (quien está sentado u ocupado se queda donde está).</summary>
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
                // De izquierda a derecha según donde ya están, para que no se crucen al colocarse
                var left = new Vec2(-front.y, front.x);
                free = free.OrderByDescending(a => Vec2.DotProduct(a.Position.AsVec2 - me.Position.AsVec2, left)).ToList();
                float spread = free.Count == 1 ? 0f : Math.Min(2.4f, 0.75f * (free.Count - 1));      // abanico total, en radianes
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
            catch (Exception ex) { Log.Error("Colocar el corro", ex); }
        }

        /// <summary>Ese personaje se acerca a ti (sin avisos): para quien se suma a la conversación.</summary>
        public static void Summon(Agent a)
        {
            try
            {
                Sync();
                if (a == null || Busy(a) || Agent.Main == null || a.Position.Distance(Agent.Main.Position) <= 3.5f) return;
                if (SceneMove.GoTo(a, Agent.Main)) Calls.Add(new Call { Who = a, Left = 60f, Quiet = true });
            }
            catch (Exception ex) { Log.Error("Acercarse al jugador", ex); }
        }

        public static void Tick(float dt)
        {
            try
            {
                // Detenidos: salen de la escena a los pocos segundos y, ya fuera de ella, quedan presos de verdad
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
                                Log.Info($"{j.who.Name} queda presa en {j.where.Name}");
                            }
                        }
                        catch (Exception ex) { Log.Error("Encerrar al detenido", ex); }
                    }
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
                        // Ha llegado; espera a que llegues tú también
                        if (!g.Waiting) { g.Waiting = true; SceneMove.Release(g.Who); try { g.Who.SetLookAgent(Agent.Main); } catch { } }
                        if (toPlayer <= 5f)
                        {
                            try { g.Who.SetLookAgent(null); } catch { }
                            Say(L.F("AIL_3cc9d40f", $"{g.Who.Name} te ha traído hasta {g.To.Name}."));
                            Guides.RemoveAt(i);
                        }
                        continue;
                    }
                    // Si te quedas atrás, se para y te espera; cuando te acercas, sigue
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
                    if (!c.Quiet) { GroupTalk.Join(c.Who); Say(L.F("AIL_4eaee384", $"{c.Who.Name} se acerca.")); }
                }
            }
            catch (Exception ex) { Log.Error("Acciones en la escena", ex); Guides.Clear(); Calls.Clear(); }
        }
    }
}
