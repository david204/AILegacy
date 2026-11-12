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
    /// <summary>Orden en el mapa que un NPC ha aceptado cumplir con su propio grupo.</summary>
    public class PartyOrder
    {
        [JsonProperty("hero")] public string HeroId;
        /// <summary>follow | goto | meet | wait | patrol | attack | besiege | raid</summary>
        [JsonProperty("type")] public string Type;
        /// <summary>StringId del asentamiento o del grupo objetivo.</summary>
        [JsonProperty("target")] public string TargetId;
        [JsonProperty("target_name")] public string TargetName;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("arrived")] public bool Arrived;
        /// <summary>Días que dura lo acordado una vez ha llegado (seguirte, esperarte).</summary>
        [JsonProperty("stay")] public double StayDays;
        /// <summary>Qué hacer al cumplirla: "follow" = volver junto al jugador.</summary>
        [JsonProperty("then")] public string Then;
    }

    /// <summary>
    /// Órdenes en el mapa: el NPC mueve su grupo como acordó (seguirte, ir a un sitio, esperar, patrullar,
    /// atacar, asediar, saquear) hasta que la orden caduca o se cumple. Mientras dura, la IA del juego no decide por él.
    /// </summary>
    internal static class OrderSystem
    {
        public static List<PartyOrder> Orders = new List<PartyOrder>();

        // ============================================================ consultas

        /// <summary>El grupo que este héroe manda (no vale el del jugador ni ir de pasajero).</summary>
        public static MobileParty PartyLedBy(Hero h)
        {
            var p = h?.PartyBelongedTo;
            if (p == null || p.IsMainParty || p.LeaderHero != h || !p.IsActive) return null;
            return p;
        }

        /// <summary>Puede recibir órdenes: manda un grupo y no está atado a un ejército ajeno.</summary>
        public static bool CanTakeOrders(Hero h)
        {
            if (!S.Get.EnableMapOrders || h == null || h.IsPrisoner || !h.IsAlive) return false;
            var p = PartyLedBy(h);
            if (p == null) return false;
            return p.Army == null || p.Army.LeaderParty == p || p.Army.LeaderParty == MobileParty.MainParty;
        }

        public static PartyOrder Find(Hero h) => h == null ? null : Orders.FirstOrDefault(o => o.HeroId == h.StringId);

        /// <summary>Para el prompt: qué ha prometido hacer con su grupo.</summary>
        public static string Describe(Hero h)
        {
            var o = Find(h);
            if (o == null) return "";
            int days = Math.Max(0, (int)Math.Ceiling(o.UntilDay - CampaignTime.Now.ToDays));
            string who = Hero.MainHero.Name.ToString();
            string left = days <= 0 ? "ending today" : $"for about {days} more day(s)";
            switch (o.Type)
            {
                case "follow": return o.Arrived ? $"you agreed to ride with {who}'s party and follow them ({left})" : $"you are on your way to join {who}'s party, as agreed";
                case "goto": return $"you agreed with {who} to travel to {o.TargetName}" + (o.Then == "follow" ? " and then come back to them" : "");
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

        // ============================================================ búsqueda de objetivos

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

        /// <summary>Grupos enemigos del NPC que están cerca de él (para atacar).</summary>
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
            catch (Exception ex) { Log.Error("Órdenes: buscando enemigos", ex); }
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

        // ============================================================ dar y quitar órdenes

        /// <summary>Da (o sustituye) la orden del héroe y la aplica ya. Devuelve false si no se puede.</summary>
        /// <summary>Una partida amiga cercana por su nombre o el de su jefe (para escoltarla).</summary>
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

        public static bool Give(Hero h, string type, string targetId, string targetName, double days, string then = null)
        {
            if (!CanTakeOrders(h)) return false;
            var cfg = S.Get;
            if (days <= 0) days = cfg.OrderDefaultDays;
            days = Math.Max(0.5, Math.Min(cfg.OrderMaxDays, days));
            Cancel(h, silent: true);
            var o = new PartyOrder { HeroId = h.StringId, Type = type, TargetId = targetId, TargetName = targetName, UntilDay = CampaignTime.Now.ToDays + days, StayDays = days, Then = then };
            // Para acudir a tu lado o a una cita primero hay que llegar: el plazo de verdad empieza al llegar
            if (type == "follow") o.UntilDay = CampaignTime.Now.ToDays + Math.Max(days, cfg.OrderMaxDays);
            if (type == "meet") o.StayDays = cfg.OrderDefaultDays;
            Orders.Add(o);
            Log.Info($"Orden para {h.Name}: {type} {targetName} ({days:0.#} días)");
            // Dentro de una escena no se mueve a su grupo (sacarlo del asentamiento mientras hablas con él rompe la escena):
            // la orden empieza en el primer tick, cuando ya estás fuera.
            if (TaleWorlds.MountAndBlade.Mission.Current == null)
            {
                try { Apply(o, h, PartyLedBy(h)); } catch (Exception ex) { Log.Error("Órdenes: aplicar", ex); }
            }
            return true;
        }

        public static bool Cancel(Hero h, bool silent = false)
        {
            var o = Find(h);
            if (o == null) return false;
            Release(o, h, silent ? null : L.F($"{h.Name} vuelve a sus asuntos."));
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
            catch (Exception ex) { Log.Error("Órdenes: liberar", ex); }
            if (message != null) Say(message);
            Log.Info($"Orden terminada ({o.Type} {o.TargetName}) de {h?.Name}");
        }

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));

        // ============================================================ tick

        private static double _lastHour = -1;
        private static readonly Dictionary<string, string> LastState = new Dictionary<string, string>();

        /// <summary>Llamar a menudo: pasa el tick de órdenes una vez por hora de juego.</summary>
        public static void Pump()
        {
            if (Campaign.Current == null || Orders.Count == 0) return;
            double hour = CampaignTime.Now.ToHours;
            if (_lastHour >= 0 && hour >= _lastHour && hour - _lastHour < 1.0) return;
            _lastHour = hour;
            OnHourlyTick();
        }

        /// <summary>Deja en el log qué está haciendo el grupo, solo cuando cambia (para diagnosticar órdenes atascadas).</summary>
        private static void Trace(PartyOrder o, Hero h, MobileParty p)
        {
            try
            {
                string state = L.F($"{o.Type} {o.TargetName} | en={p.CurrentSettlement?.Name?.ToString() ?? "-"} def={p.DefaultBehavior} corto={p.ShortTermBehavior} obj={p.TargetSettlement?.Name?.ToString() ?? p.TargetParty?.Name?.ToString() ?? "-"} llegó={o.Arrived} batalla={p.MapEvent != null}");
                if (LastState.TryGetValue(o.HeroId, out var old) && old == state) return;
                LastState[o.HeroId] = state;
                Log.Info($"[orden] {h.Name}: {state}");
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
                    if (p == null || h.IsPrisoner) { Release(o, h, null); continue; }                    // derrotado, preso o sin grupo
                    if (p.Army != null && p.Army.LeaderParty != p && p.Army.LeaderParty != MobileParty.MainParty) { Release(o, h, null); continue; }
                    if (h.MapFaction != null && Hero.MainHero.MapFaction != null && h.MapFaction != Hero.MainHero.MapFaction
                        && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction) && (o.Type == "follow" || o.Type == "meet"))
                    { Release(o, h, L.F($"{h.Name} ya no acude a tu llamada: ahora sois enemigos.")); continue; }
                    if (now >= o.UntilDay) { Release(o, h, EndText(o, h)); continue; }
                    if (p.MapEvent != null) continue;                                                     // en plena batalla
                    Apply(o, h, p);
                    if (Orders.Contains(o)) Trace(o, h, p);
                }
                catch (Exception ex) { Log.Error("Órdenes: tick", ex); }
            }
        }

        private static string EndText(PartyOrder o, Hero h)
        {
            switch (o.Type)
            {
                case "follow": return o.Arrived ? L.F($"{h.Name} deja de seguirte y vuelve a sus asuntos.") : L.F($"{h.Name} no ha conseguido alcanzarte y vuelve a sus asuntos.");
                case "meet": return o.Arrived ? L.F($"{h.Name} se cansa de esperarte en {o.TargetName} y se marcha.") : L.F($"{h.Name} no ha podido llegar a {o.TargetName} y lo deja.");
                case "wait": return L.F($"{h.Name} deja de esperar y vuelve a sus asuntos.");
                case "patrol": return L.F($"{h.Name} termina su patrulla en {o.TargetName}.");
                case "approach": return null;      // no te alcanzó: lo deja sin más
                default: return L.F($"{h.Name} vuelve a sus asuntos.");
            }
        }

        /// <summary>Pone (o mantiene) al grupo haciendo lo acordado. Si la orden ya no tiene sentido, la termina.</summary>
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
                        Say(L.F($"{h.Name} se une a tu marcha con {p.MemberRoster.TotalManCount} hombres."));
                    }
                    break;

                case "wait":
                    if (p.DefaultBehavior != AiBehavior.Hold) p.SetMoveModeHold();
                    break;

                case "escort":
                {
                    // Escoltar a OTRA partida (una caravana, unos aldeanos, un pariente): cabalga a su lado
                    var ward = MobileParty.All.FirstOrDefault(x => x != null && x.StringId == o.TargetId);
                    if (ward == null || !ward.IsActive || ward.MapEvent != null && ward.MapEvent.HasWinner) { Release(o, h, L.F($"{h.Name} ya no tiene a quién escoltar y vuelve a sus asuntos.")); return; }
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != ward) p.SetMoveEscortParty(ward, nav, false);
                    break;
                }

                case "goto":
                case "meet":
                    if (st == null) { Release(o, h, null); return; }
                    // Ha llegado si está dentro o plantado en la puerta (hay sitios donde el grupo no llega a "entrar")
                    bool there = p.CurrentSettlement == st || p.GetPosition2D.Distance(st.GatePosition.ToVec2()) <= 2f;
                    if (there)
                    {
                        if (!o.Arrived)
                        {
                            o.Arrived = true;
                            if (o.Type == "meet")
                            {
                                // empieza a contar la espera desde que llega
                                o.UntilDay = CampaignTime.Now.ToDays + Math.Max(0.5, o.StayDays);
                                Say(L.F($"{h.Name} ha llegado a {st.Name} y te espera allí."));
                            }
                            else
                            {
                                bool back = o.Then == "follow";
                                Release(o, h, back ? L.F($"{h.Name} ha llegado a {st.Name} y emprende el regreso hacia ti.") : L.F($"{h.Name} ha llegado a {st.Name}."));
                                if (back) Give(h, "follow", null, Hero.MainHero.Name.ToString(), 0);
                                return;
                            }
                        }
                        // quieto donde está: dentro se queda dentro, y en la puerta no entra y sale sin parar
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
                    { Release(o, h, L.F($"{h.Name} ya no tiene a quién perseguir ({o.TargetName}).")); return; }
                    if (target.MapFaction == null || !target.MapFaction.IsAtWarWith(p.MapFaction) || target.CurrentSettlement != null)
                    { Release(o, h, L.F($"{h.Name} abandona la persecución de {o.TargetName}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.EngageParty || p.TargetParty != target)
                        p.SetMoveEngageParty(target, nav);
                    break;
                }

                case "besiege":
                    if (st == null || !st.IsFortification) { Release(o, h, null); return; }
                    if (st.MapFaction == null || !st.MapFaction.IsAtWarWith(p.MapFaction))
                    { Release(o, h, st.MapFaction == p.MapFaction ? L.F($"{st.Name} ha caído. {h.Name} da el asedio por terminado.") : L.F($"{h.Name} levanta el asedio de {st.Name}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.BesiegeSettlement || p.TargetSettlement != st)
                        p.SetMoveBesiegeSettlement(st, nav);
                    break;

                case "raid":
                    if (st == null || !st.IsVillage) { Release(o, h, null); return; }
                    if (st.Village.VillageState == Village.VillageStates.Looted || st.IsRaided)
                    { Release(o, h, L.F($"{st.Name} ha sido saqueada. {h.Name} vuelve a sus asuntos.")); return; }
                    if (st.MapFaction == null || !st.MapFaction.IsAtWarWith(p.MapFaction))
                    { Release(o, h, L.F($"{h.Name} renuncia a saquear {st.Name}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.RaidSettlement || p.TargetSettlement != st)
                        p.SetMoveRaidSettlement(st, nav, false);
                    break;

                case "approach":
                    // Viene por su cuenta a hablar contigo: al llegar junto a tu grupo, pide la palabra
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

        // ============================================================ ejércitos

        /// <summary>El grupo del NPC entra en el ejército del jugador (lo crea si el jugador aún no tiene y puede).</summary>
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
            return L.F($"{h.Name} se une a tu ejército con {p.MemberRoster.TotalManCount} hombres.");
        }

        /// <summary>El NPC reúne su propio ejército (la IA del juego lo dirige después).</summary>
        public static string FormOwnArmy(Hero h, Settlement target)
        {
            var p = PartyLedBy(h);
            if (p == null || p.Army != null || !(h.MapFaction is Kingdom k)) return null;
            Cancel(h, silent: true);
            var type = target != null && target.MapFaction != null && target.MapFaction.IsAtWarWith(k)
                ? (target.IsVillage ? Army.ArmyTypes.Raider : Army.ArmyTypes.Besieger)
                : (target != null ? Army.ArmyTypes.Defender : Army.ArmyTypes.Patrolling);
            k.CreateArmy(h, target, type);
            if (p.Army == null) return null;
            return L.F($"{h.Name} reúne un ejército") + (target != null ? L.F($" con la vista puesta en {target.Name}.") : ".");
        }

        public static string LeaveArmy(Hero h)
        {
            var p = PartyLedBy(h);
            if (p?.Army == null) return null;
            if (p.Army.LeaderParty == p) { DisbandArmyAction.ApplyByUnknownReason(p.Army); return L.F($"{h.Name} disuelve su ejército."); }
            p.Army = null;
            p.Ai.SetDoNotMakeNewDecisions(false);
            return L.F($"{h.Name} abandona el ejército con sus hombres.");
        }
    }
}
