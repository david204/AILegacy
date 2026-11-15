using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>A map order an NPC agreed to carry out with their own party.</summary>
    public class PartyOrder
    {
        [JsonProperty("hero")] public string HeroId;
        /// <summary>follow | goto | meet | wait | patrol | attack | besiege | raid</summary>
        [JsonProperty("type")] public string Type;
        /// <summary>StringId of the target settlement or party.</summary>
        [JsonProperty("target")] public string TargetId;
        [JsonProperty("target_name")] public string TargetName;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("arrived")] public bool Arrived;
        /// <summary>Days the agreement lasts once they arrive (following you, waiting for you).</summary>
        [JsonProperty("stay")] public double StayDays;
        /// <summary>What to do when done: "follow" = go back to the player.</summary>
        [JsonProperty("then")] public string Then;
        /// <summary>Days to stay on arrival before the order counts as done ("go to X and wait two days").</summary>
        [JsonProperty("wait")] public double WaitDays;
        /// <summary>Not agreed with the player: a landless clan patrolling on its own (World/LandlessPatrols.cs). Silent, and anything the player asks replaces it.</summary>
        [JsonProperty("auto", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Auto;
        /// <summary>Why an Auto order was given, when it is not a landless patrol: "care" = a sick party riding to be treated (World/DiseasesExtra.cs).</summary>
        [JsonProperty("why", DefaultValueHandling = DefaultValueHandling.Ignore)] public string Reason;
    }

    /// <summary>
    /// Map orders: the NPC moves their party as agreed (follow you, go somewhere, wait, patrol, attack,
    /// besiege, raid) until the order expires or is done. While it lasts, the game AI doesn't make decisions for them.
    /// </summary>
    internal static class OrderSystem
    {
        public static List<PartyOrder> Orders = new List<PartyOrder>();

        private static readonly Prompt.Tpl TOwnPatrol = new Prompt.Tpl("orders/landless_patrol.txt",
            "your clan holds no land, so these days you are patrolling the country around {place} with your men, of your own accord");
        private static readonly Prompt.Tpl TOwnCare = new Prompt.Tpl("orders/seeking_care.txt",
            "there is sickness in your party, so you are taking it to {place} to have the physicians there treat you and your men, of your own accord");

        // ============================================================ queries

        /// <summary>The party this hero leads. Not the player's party, and not one they just ride along in.</summary>
        public static MobileParty PartyLedBy(Hero h)
        {
            var p = h?.PartyBelongedTo;
            if (p == null || p.IsMainParty || p.LeaderHero != h || !p.IsActive) return null;
            return p;
        }

        /// <summary>Leads a party and isn't tied to someone else's army.</summary>
        public static bool CanTakeOrders(Hero h, bool auto = false)
        {
            if ((!auto && !S.Get.EnableMapOrders) || h == null || h.IsPrisoner || !h.IsAlive) return false;
            var p = PartyLedBy(h);
            if (p == null) return false;
            return p.Army == null || p.Army.LeaderParty == p || p.Army.LeaderParty == MobileParty.MainParty;
        }

        /// <summary>The order this hero has agreed with the player. A patrol of their own does not count.</summary>
        public static PartyOrder Find(Hero h) => h == null ? null : Orders.FirstOrDefault(o => o.HeroId == h.StringId && !o.Auto);

        /// <summary>Any order moving this hero's party, their own patrols included.</summary>
        public static PartyOrder FindAny(Hero h) => h == null ? null : Orders.FirstOrDefault(o => o.HeroId == h.StringId);

        /// <summary>For the prompt: what they promised to do with their party.</summary>
        public static string Describe(Hero h)
        {
            var o = FindAny(h);
            if (o == null) return "";
            if (o.Auto) return (o.Reason == "care" ? TOwnCare : TOwnPatrol).Format("place", o.TargetName ?? "");
            int days = Math.Max(0, (int)Math.Ceiling(o.UntilDay - CampaignTime.Now.ToDays));
            string who = Hero.MainHero.Name.ToString();
            string left = days <= 0 ? "ending today" : $"for about {days} more day(s)";
            switch (o.Type)
            {
                case "follow": return o.Arrived ? $"you agreed to ride with {who}'s party and follow them ({left})" : $"you are on your way to join {who}'s party, as agreed";
                case "goto": return $"you agreed with {who} to travel to {o.TargetName}" + (o.WaitDays > 0 ? $", stay there {o.WaitDays:0.#} day(s)" : "") + (o.Then == "follow" ? " and then come back to them" : "");
                case "meet": return o.Arrived ? $"you are waiting at {o.TargetName} to meet {who}, as agreed ({left})" : $"you are travelling to {o.TargetName} to meet {who} there, as agreed";
                case "wait": return $"you agreed with {who} to hold your position and wait ({left})";
                case "approach": return $"you are riding over to {who}'s party to speak with them";
                case "patrol": return $"you agreed with {who} to patrol around {o.TargetName} ({left})";
                case "attack": return $"you agreed with {who} to hunt down {o.TargetName}";
                case "besiege": return $"you agreed with {who} to besiege {o.TargetName}";
                case "raid": return $"you agreed with {who} to raid {o.TargetName}";
                default: return "";
            }
        }

        // ============================================================ target lookup

        private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        public static Settlement FindSettlement(string name, Func<Settlement, bool> filter = null)
        {
            var n = Norm(name);
            if (n.Length < 2) return null;
            var all = Settlement.All.Where(s => s != null && !s.IsHideout && (filter == null || filter(s))).ToList();
            return all.FirstOrDefault(s => s.StringId == name)
                   ?? all.FirstOrDefault(s => Norm(s.Name.ToString()) == n)
                   ?? all.FirstOrDefault(s => Norm(s.Name.ToString()).StartsWith(n))
                   ?? all.FirstOrDefault(s => n.Contains(Norm(s.Name.ToString())) || Norm(s.Name.ToString()).Contains(n));
        }

        /// <summary>Hostile parties near the NPC, nearest first (attack targets).</summary>
        public static List<MobileParty> HostilesNear(Hero h, int max = 6)
        {
            var result = new List<MobileParty>();
            var own = PartyLedBy(h);
            if (own == null || own.MapFaction == null) return result;
            try
            {
                float radius = S.Get.OrderAttackRadius;
                var here = own.GetPosition2D;
                result = MobileParty.All
                    .Where(p => p != null && p != own && p.IsActive && !p.IsMainParty && !p.IsGarrison && !p.IsMilitia && p.CurrentSettlement == null
                                && p.MapFaction != null && p.MapFaction != own.MapFaction && p.MapFaction.IsAtWarWith(own.MapFaction)
                                && p.MemberRoster.TotalManCount > 0 && p.GetPosition2D.Distance(here) <= radius)
                    .OrderBy(p => p.GetPosition2D.Distance(here)).Take(max).ToList();
            }
            catch (Exception ex) { Log.Error("Orders: looking for enemies", ex); }
            return result;
        }

        public static MobileParty FindHostile(Hero h, string name)
        {
            var n = Norm(name);
            var list = HostilesNear(h, 30);
            if (list.Count == 0) return null;
            if (n.Length < 2) return list[0];
            return list.FirstOrDefault(p => Norm(p.Name.ToString()) == n)
                   ?? list.FirstOrDefault(p => Norm(p.Name.ToString()).Contains(n) || n.Contains(Norm(p.Name.ToString())))
                   ?? list.FirstOrDefault(p => p.LeaderHero != null && (n.Contains(Norm(p.LeaderHero.Name.ToString())) || Norm(p.LeaderHero.Name.ToString()).Contains(n)));
        }

        // ============================================================ giving and cancelling orders

        /// <summary>Nearest friendly party matching its name or its leader's name (escort target).</summary>
        public static MobileParty FindFriendlyParty(Hero h, string name)
        {
            try
            {
                string n = WorldSim.Norm(name);
                if (n.Length < 2) return null;
                var mine = PartyLedBy(h);
                if (mine == null) return null;
                return MobileParty.All.Where(p => p != null && p.IsActive && p != mine && p != MobileParty.MainParty && p.MapFaction != null && h.MapFaction != null && !p.MapFaction.IsAtWarWith(h.MapFaction)
                                                  && (WorldSim.Norm(p.Name.ToString()).Contains(n) || (p.LeaderHero != null && WorldSim.Norm(p.LeaderHero.Name.ToString()).Contains(n))))
                                      .OrderBy(p => p.GetPosition2D.Distance(mine.GetPosition2D)).FirstOrDefault();
            }
            catch { return null; }
        }

        public static bool Give(Hero h, string type, string targetId, string targetName, double days, string then = null, double waitDays = 0, bool auto = false, string reason = null)
        {
            if (!CanTakeOrders(h, auto)) return false;
            var cfg = S.Get;
            if (days <= 0) days = cfg.OrderDefaultDays;
            days = Math.Max(0.5, Math.Min(cfg.OrderMaxDays, days));
            Cancel(h, silent: true);
            var o = new PartyOrder { HeroId = h.StringId, Type = type, TargetId = targetId, TargetName = targetName, UntilDay = CampaignTime.Now.ToDays + days, StayDays = days, Then = then, WaitDays = Math.Max(0, Math.Min(S.Get.OrderMaxDays, waitDays)), Auto = auto, Reason = auto ? reason : null };
            // To join you or keep a meeting they have to get there first. The real deadline starts on arrival
            if (type == "follow") o.UntilDay = CampaignTime.Now.ToDays + Math.Max(days, cfg.OrderMaxDays);
            if (type == "meet") o.StayDays = cfg.OrderDefaultDays;
            Orders.Add(o);
            Log.Info(auto && reason == "care" ? $"[disease] {h.Name} ({h.Clan?.Name}) takes their party to {targetName} for treatment"
                   : auto ? $"[landless] {h.Name} ({h.Clan?.Name}) patrols on their own around {targetName} ({days:0.#} days)" : $"Order for {h.Name}: {type} {targetName} ({days:0.#} days)");
            // Don't move the party while inside a scene (pulling it out of the settlement while you talk breaks the scene).
            // The order starts on the first tick after you leave.
            if (TaleWorlds.MountAndBlade.Mission.Current == null)
            {
                try { Apply(o, h, PartyLedBy(h)); } catch (Exception ex) { Log.Error("Orders: apply", ex); }
            }
            return true;
        }

        public static bool Cancel(Hero h, bool silent = false)
        {
            var o = FindAny(h);
            if (o == null) return false;
            Release(o, h, silent ? null : L.F("AIL_7cd9f263", $"{h.Name} vuelve a sus asuntos."));
            return true;
        }

        private static void Release(PartyOrder o, Hero h, string message)
        {
            Orders.Remove(o);
            try
            {
                var p = h?.PartyBelongedTo;
                if (p != null && !p.IsMainParty && p.LeaderHero == h) p.Ai.SetDoNotMakeNewDecisions(false);
            }
            catch (Exception ex) { Log.Error("Orders: release", ex); }
            if (message != null && !o.Auto) Say(message);
            Log.Info($"Order finished ({o.Type} {o.TargetName}) of {h?.Name}");
        }

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));

        // ============================================================ tick

        private static double _lastHour = -1;
        private static readonly Dictionary<string, string> LastState = new Dictionary<string, string>();

        /// <summary>Call often. Runs the order tick once per game hour.</summary>
        public static void Pump()
        {
            if (Campaign.Current == null || Orders.Count == 0) return;
            double hour = CampaignTime.Now.ToHours;
            if (_lastHour >= 0 && hour >= _lastHour && hour - _lastHour < 1.0) return;
            _lastHour = hour;
            OnHourlyTick();
        }

        /// <summary>Logs what the party is doing, only when it changes. Useful for debugging stuck orders.</summary>
        private static void Trace(PartyOrder o, Hero h, MobileParty p)
        {
            try
            {
                string state = L.F("AIL_d7c17160", $"{o.Type} {o.TargetName} | en={p.CurrentSettlement?.Name?.ToString() ?? "-"} def={p.DefaultBehavior} corto={p.ShortTermBehavior} obj={p.TargetSettlement?.Name?.ToString() ?? p.TargetParty?.Name?.ToString() ?? "-"} llegó={o.Arrived} batalla={p.MapEvent != null}");
                if (LastState.TryGetValue(o.HeroId, out var old) && old == state) return;
                LastState[o.HeroId] = state;
                Log.Info($"[order] {h.Name}: {state}");
            }
            catch { }
        }

        public static void OnHourlyTick()
        {
            if (Orders.Count == 0) return;
            double now = CampaignTime.Now.ToDays;
            foreach (var o in Orders.ToList())
            {
                try
                {
                    var h = Hero.Find(o.HeroId);
                    if (h == null || h.IsDead) { Orders.Remove(o); continue; }
                    var p = PartyLedBy(h);
                    // Defeated, captured or without a party: the deal is left undone, and both sides know it
                    if (p == null || h.IsPrisoner)
                    {
                        if (o.Type != "approach" && !o.Auto) Remember(h, h.IsPrisoner ? $"you were taken prisoner and could not do what you had agreed with them ({o.Type} {o.TargetName})"
                                                 : $"you lost your party and could not do what you had agreed with them ({o.Type} {o.TargetName})");
                        Release(o, h, o.Type == "approach" ? null : L.F("AIL_5e0b93a7", $"{h.Name} ya no puede cumplir lo que acordasteis: {(h.IsPrisoner ? L.T("AIL_91c2d4f6", "ha caído prisionero") : L.T("AIL_2f7a8b13", "se ha quedado sin partida"))}."));
                        continue;
                    }
                    // The game pulled them into someone else's army: say so instead of dropping the order silently
                    if (p.Army != null && p.Army.LeaderParty != p && p.Army.LeaderParty != MobileParty.MainParty)
                    {
                        string boss = p.Army.LeaderParty?.LeaderHero?.Name?.ToString() ?? p.Army.Name?.ToString() ?? "?";
                        if (o.Type != "approach" && !o.Auto) Remember(h, $"you were called into the army of {boss} and had to leave undone what you had agreed with them ({o.Type} {o.TargetName}). You owe them an explanation");
                        Release(o, h, o.Type == "approach" ? null : L.F("AIL_c4d81e09", $"{h.Name} ha sido llamado al ejército de {boss} y deja lo que acordasteis."));
                        continue;
                    }
                    if (h.MapFaction != null && Hero.MainHero.MapFaction != null && h.MapFaction != Hero.MainHero.MapFaction
                        && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction) && (o.Type == "follow" || o.Type == "meet"))
                    { Release(o, h, L.F("AIL_35617af2", $"{h.Name} ya no acude a tu llamada: ahora sois enemigos.")); continue; }
                    if (now >= o.UntilDay) { Done(o, h, EndText(o, h)); continue; }
                    if (p.MapEvent != null) continue;                                                     // in a battle
                    Apply(o, h, p);
                    if (Orders.Contains(o)) Trace(o, h, p);
                }
                catch (Exception ex) { Log.Error("Orders: tick", ex); }
            }
        }

        /// <summary>Notes in the character's memory what happened with an order, so they know it next time you talk.</summary>
        private static void Remember(Hero h, string what)
        {
            try
            {
                var mem = Memory.MemoryStore.Find(h.StringId);
                if (mem == null) return;
                mem.Turns.Add(new Memory.ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays, Text = "[Since you last spoke: " + what + ".]" });
            }
            catch { }
        }

        /// <summary>Normal end of an order (time's up, target reached or lost). If they agreed to come back afterwards, they do.</summary>
        private static void Done(PartyOrder o, Hero h, string message)
        {
            bool back = o.Then == "follow" && o.Type != "follow" && !o.Auto;
            Release(o, h, message);
            try
            {
                if (!back || h == null || h.IsPrisoner || !h.IsAlive) return;
                var mine = Hero.MainHero.MapFaction;
                if (h.MapFaction != null && mine != null && h.MapFaction != mine && h.MapFaction.IsAtWarWith(mine)) return;
                if (Give(h, "follow", null, Hero.MainHero.Name.ToString(), 0)) Say(L.F("AIL_c7a9d5e1", $"{h.Name} vuelve contigo, como acordasteis."));
            }
            catch (Exception ex) { Log.Error("Orders: return", ex); }
        }

        private static string EndText(PartyOrder o, Hero h)
        {
            switch (o.Type)
            {
                case "follow": return o.Arrived ? L.F("AIL_0a0689c9", $"{h.Name} deja de seguirte y vuelve a sus asuntos.") : L.F("AIL_23bfd455", $"{h.Name} no ha conseguido alcanzarte y vuelve a sus asuntos.");
                case "meet": return o.Arrived ? L.F("AIL_b0a43892", $"{h.Name} se cansa de esperarte en {o.TargetName} y se marcha.") : L.F("AIL_5c3efc53", $"{h.Name} no ha podido llegar a {o.TargetName} y lo deja.");
                case "wait": return L.F("AIL_645733f1", $"{h.Name} deja de esperar y vuelve a sus asuntos.");
                case "patrol": return L.F("AIL_55479028", $"{h.Name} termina su patrulla en {o.TargetName}.");
                case "approach": return null;      // never caught up with you, just drop it
                default: return L.F("AIL_7cd9f263", $"{h.Name} vuelve a sus asuntos.");
            }
        }

        /// <summary>Sets (or keeps) the party doing what was agreed. Ends the order if it no longer makes sense.</summary>
        private static void Apply(PartyOrder o, Hero h, MobileParty p)
        {
            var nav = p.IsCurrentlyAtSea ? p.NavigationCapability : MobileParty.NavigationType.Default;
            var st = o.Type == "follow" || o.Type == "wait" || o.Type == "attack" || o.Type == "approach" || string.IsNullOrEmpty(o.TargetId) ? null : Settlement.Find(o.TargetId);
            p.Ai.SetDoNotMakeNewDecisions(true);

            switch (o.Type)
            {
                case "follow":
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != MobileParty.MainParty)
                        p.SetMoveEscortParty(MobileParty.MainParty, nav, false);
                    if (!o.Arrived && p.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) <= 4f)
                    {
                        o.Arrived = true;
                        o.UntilDay = CampaignTime.Now.ToDays + Math.Max(0.5, o.StayDays);
                        Say(L.F("AIL_85e6e5d4", $"{h.Name} se une a tu marcha con {p.MemberRoster.TotalManCount} hombres."));
                    }
                    break;

                case "wait":
                    if (p.DefaultBehavior != AiBehavior.Hold) p.SetMoveModeHold();
                    break;

                case "escort":
                {
                    // Escort ANOTHER party (a caravan, villagers, a relative) by riding alongside it
                    var ward = MobileParty.All.FirstOrDefault(x => x != null && x.StringId == o.TargetId);
                    if (ward == null || !ward.IsActive || ward.MapEvent != null && ward.MapEvent.HasWinner) { Release(o, h, L.F("AIL_49b6ffff", $"{h.Name} ya no tiene a quién escoltar y vuelve a sus asuntos.")); return; }
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != ward) p.SetMoveEscortParty(ward, nav, false);
                    break;
                }

                case "goto":
                case "meet":
                    if (st == null) { Release(o, h, null); return; }
                    // Arrived if inside or standing at the gate (in some places the party never actually "enters")
                    bool there = p.CurrentSettlement == st || p.GetPosition2D.Distance(st.GatePosition.ToVec2()) <= 2f;
                    if (there)
                    {
                        if (!o.Arrived)
                        {
                            o.Arrived = true;
                            if (o.Type == "meet")
                            {
                                // the wait starts counting on arrival
                                o.UntilDay = CampaignTime.Now.ToDays + Math.Max(0.5, o.StayDays);
                                Say(L.F("AIL_7d054e89", $"{h.Name} ha llegado a {st.Name} y te espera allí."));
                            }
                            else if (o.WaitDays > 0)
                            {
                                // "Go and wait N days": stays there, and the time counts from arrival
                                o.UntilDay = CampaignTime.Now.ToDays + o.WaitDays;
                                if (!o.Auto) Say(L.F("AIL_c7a9d5e2", $"{h.Name} ha llegado a {st.Name} y se queda allí {o.WaitDays:0.#} día(s)."));
                            }
                            else
                            {
                                bool back = o.Then == "follow";
                                Release(o, h, back ? L.F("AIL_d65def98", $"{h.Name} ha llegado a {st.Name} y emprende el regreso hacia ti.") : L.F("AIL_518def5c", $"{h.Name} ha llegado a {st.Name}."));
                                if (back) Give(h, "follow", null, Hero.MainHero.Name.ToString(), 0);
                                return;
                            }
                        }
                        // hold in place: inside stays inside, and at the gate it doesn't keep going in and out
                        if (p.DefaultBehavior != AiBehavior.Hold) p.SetMoveModeHold();
                    }
                    else if (p.DefaultBehavior != AiBehavior.GoToSettlement || p.TargetSettlement != st)
                        p.SetMoveGoToSettlement(st, nav, false);
                    break;

                case "patrol":
                    if (st == null) { Release(o, h, null); return; }
                    if (p.DefaultBehavior != AiBehavior.PatrolAroundPoint || p.TargetSettlement != st)
                        p.SetMovePatrolAroundSettlement(st, nav, false);
                    break;

                case "attack":
                {
                    var target = MobileParty.All.FirstOrDefault(x => x != null && x.StringId == o.TargetId);
                    if (target == null || !target.IsActive || target.MemberRoster.TotalManCount == 0)
                    { Done(o, h, L.F("AIL_9480373b", $"{h.Name} ya no tiene a quién perseguir ({o.TargetName}).")); return; }
                    if (target.MapFaction == null || !target.MapFaction.IsAtWarWith(p.MapFaction) || target.CurrentSettlement != null)
                    { Done(o, h, L.F("AIL_6f87960d", $"{h.Name} abandona la persecución de {o.TargetName}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.EngageParty || p.TargetParty != target)
                        p.SetMoveEngageParty(target, nav);
                    break;
                }

                case "besiege":
                    if (st == null || !st.IsFortification) { Release(o, h, null); return; }
                    if (st.MapFaction == null || !st.MapFaction.IsAtWarWith(p.MapFaction))
                    { Done(o, h, st.MapFaction == p.MapFaction ? L.F("AIL_dd1a051b", $"{st.Name} ha caído. {h.Name} da el asedio por terminado.") : L.F("AIL_4eae4e56", $"{h.Name} levanta el asedio de {st.Name}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.BesiegeSettlement || p.TargetSettlement != st)
                        p.SetMoveBesiegeSettlement(st, nav);
                    break;

                case "raid":
                    if (st == null || !st.IsVillage) { Release(o, h, null); return; }
                    if (st.Village.VillageState == Village.VillageStates.Looted || st.IsRaided)
                    { Done(o, h, L.F("AIL_399e0d5f", $"{st.Name} ha sido saqueada. {h.Name} vuelve a sus asuntos.")); return; }
                    if (st.MapFaction == null || !st.MapFaction.IsAtWarWith(p.MapFaction))
                    { Done(o, h, L.F("AIL_c351bc8a", $"{h.Name} renuncia a saquear {st.Name}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.RaidSettlement || p.TargetSettlement != st)
                        p.SetMoveRaidSettlement(st, nav, false);
                    break;

                case "approach":
                    // Coming over on their own to talk. Once next to your party, they ask to speak
                    if (p.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) <= 3.5f)
                    {
                        Release(o, h, null);
                        MapInitiative.Arrived(h);
                        return;
                    }
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != MobileParty.MainParty)
                        p.SetMoveEscortParty(MobileParty.MainParty, nav, false);
                    break;

                default:
                    Release(o, h, null);
                    break;
            }
        }

        // ============================================================ armies

        /// <summary>The NPC's party joins the player's army. Creates the army if the player has none and is allowed to.</summary>
        public static string JoinPlayerArmy(Hero h)
        {
            var p = PartyLedBy(h);
            var main = MobileParty.MainParty;
            if (p == null || main == null) return null;
            if (main.Army == null)
            {
                if (!(Hero.MainHero.MapFaction is Kingdom k)) return null;
                var gather = Settlement.All.Where(s => s.IsFortification || s.IsVillage).OrderBy(s => s.GetPosition2D.Distance(main.GetPosition2D)).FirstOrDefault();
                k.CreateArmy(Hero.MainHero, gather, Army.ArmyTypes.Patrolling);
            }
            if (main.Army == null || main.Army.LeaderParty != main) return null;
            Cancel(h, silent: true);
            if (p.Army != null && p.Army != main.Army)
            {
                if (p.Army.LeaderParty == p) DisbandArmyAction.ApplyByUnknownReason(p.Army); else p.Army = null;
            }
            p.Army = main.Army;
            p.SetMoveEscortParty(main, p.IsCurrentlyAtSea ? p.NavigationCapability : MobileParty.NavigationType.Default, false);
            return L.F("AIL_675de4ca", $"{h.Name} se une a tu ejército con {p.MemberRoster.TotalManCount} hombres.");
        }

        /// <summary>The NPC gathers their own army. The game AI runs it afterwards.</summary>
        public static string FormOwnArmy(Hero h, Settlement target)
        {
            var p = PartyLedBy(h);
            if (p == null || p.Army != null || !(h.MapFaction is Kingdom k)) return null;
            Cancel(h, silent: true);
            bool hostile = target != null && target.MapFaction != null && target.MapFaction.IsAtWarWith(k);
            var type = hostile ? (target.IsVillage ? Army.ArmyTypes.Raider : Army.ArmyTypes.Besieger)
                               : (target != null ? Army.ArmyTypes.Defender : Army.ArmyTypes.Patrolling);
            // The settlement passed to CreateArmy is the GATHERING POINT (Army.Gather), not the target. Never gather at
            // the enemy's gates: with a hostile target (or none) use the nearest friendly fortification.
            Settlement gather = hostile || target == null ? null : target;
            if (gather == null)
            {
                var from = hostile ? target.GetPosition2D : p.GetPosition2D;
                gather = Settlement.All.Where(s => s.IsFortification && s.MapFaction == k && !s.IsUnderSiege).OrderBy(s => s.GetPosition2D.Distance(from)).FirstOrDefault()
                         ?? Settlement.All.Where(s => (s.IsFortification || s.IsVillage) && s.MapFaction == k).OrderBy(s => s.GetPosition2D.Distance(p.GetPosition2D)).FirstOrDefault();
            }
            if (gather == null) return null;
            k.CreateArmy(h, gather, type);
            if (p.Army == null) return null;
            return L.F("AIL_55cee6ef", $"{h.Name} reúne un ejército") + (target != null ? L.F("AIL_927a75ae", $" con la vista puesta en {target.Name}.") : ".");
        }

        public static string LeaveArmy(Hero h)
        {
            var p = PartyLedBy(h);
            if (p?.Army == null) return null;
            if (p.Army.LeaderParty == p) { DisbandArmyAction.ApplyByUnknownReason(p.Army); return L.F("AIL_ae49ce20", $"{h.Name} disuelve su ejército."); }
            p.Army = null;
            p.Ai.SetDoNotMakeNewDecisions(false);
            return L.F("AIL_a7d1636b", $"{h.Name} abandona el ejército con sus hombres.");
        }
    }
}
