using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using SandBox.Missions.MissionLogics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using Helpers;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Battles inside settlements, as AIInfluence has them: the game's own order bar in the street, the village and the lord's
    /// hall; your troops called in ten at a time or all of them from the outskirts (the rest wait in reserve), from your party,
    /// the lords who follow you and your army; the guard's first wave late or early depending on the place; lords of the realm
    /// out on the map riding in when the alarm rings; and how it ends for you: carried off by your men, taken to the dungeon with
    /// the men who fell, or away through the gate. A village whose defenders all lie beaten can then be looted or burned.
    /// </summary>
    internal static partial class Combat
    {
        // ---------------------------------------------------------------- where

        private static string LocId() { try { return CampaignMission.Current?.Location?.StringId ?? ""; } catch { return ""; } }
        private static bool Outdoors(string loc) => loc == "center" || loc == "village_center";
        /// <summary>No soldiers of yours get in here: the tavern, the arena, the prison and the dungeon.</summary>
        private static bool TroopsBarred(string loc) => loc == "tavern" || loc == "arena" || loc == "prison" || loc.Contains("dungeon");
        private static int TroopCap(string loc) => Outdoors(loc) ? S.Get.StreetMaxOwnTroops : S.Get.StreetIndoorTroops;

        /// <summary>
        /// Seconds before the guard first comes: 20 in a street, 30 in a tavern, 5 in a lord's hall, 10 in a castle courtyard, 15 in
        /// a village; times the setting, and sooner where the place is well kept (high security).
        /// </summary>
        internal static float FirstGuardDelay()
        {
            string loc = LocId();
            var st = Settlement.CurrentSettlement;
            float baseSec = loc == "tavern" ? 30f : loc == "lordshall" ? 5f : loc == "village_center" ? 15f : st != null && st.IsCastle ? 10f : 20f;
            float safe = 1f;
            try { if (st?.Town != null) safe = 1.2f - Math.Max(0f, Math.Min(100f, st.Town.Security)) / 250f; } catch { }
            return Math.Max(2f, baseSec * Math.Max(0.25f, Math.Min(3f, S.Get.StreetGuardDelayScale)) * safe);
        }

        // ---------------------------------------------------------------- the order bar

        private static bool _ordersOn;
        private static Mission _ordersIn;

        /// <summary>The order bar may be used: your fight is on, you stand, you lead your side and some of your men are in a formation.</summary>
        internal static bool OrdersAllowed
        {
            get
            {
                try
                {
                    var m = Mission.Current;
                    if (!_ordersOn || m == null || _ordersIn != m || _active == null || Agent.Main == null || !Agent.Main.IsActive()) return false;
                    if (PlayerEncounter.EncounteredBattle != null) return false;
                    var team = m.PlayerTeam;
                    return team != null && team.IsPlayerGeneral && team.FormationsIncludingEmpty.Any(f => f.CountOfUnits > 0);
                }
                catch { return false; }
            }
        }

        /// <summary>From the first soldier called in: you command your side, and the men take formations as in a battle.</summary>
        private static void EnableOrders(Mission m)
        {
            try
            {
                if (_ordersIn == m && _ordersOn) return;
                if (m.GetMissionBehavior<AgentHumanAILogic>() == null) m.AddMissionBehavior(new AgentHumanAILogic());
                var team = m.PlayerTeam;
                if (team == null) return;
                team.SetPlayerRole(true, true);
                team.PlayerOrderController.Owner = Agent.Main;
                _ordersOn = true; _ordersIn = m;
                Say(L.T("AIL_e7b1ddb0", "Pista: Retroceso abre el menú de órdenes como en una batalla; 1-8 eligen formaciones (0, todas) y F1-F8 dan las órdenes."), Colors.Gray);
            }
            catch (Exception ex) { Log.Warn("[fight] orders in the scene: " + ex.Message); }
        }

        private static void IntoFormation(Agent a, Team team)
        {
            try
            {
                if (a == null || team == null) return;
                var cls = a.HasMount ? FormationClass.Cavalry : a.IsRangedCached ? FormationClass.Ranged : FormationClass.Infantry;
                a.Formation = team.GetFormation(cls);
            }
            catch { }
        }

        // ---------------------------------------------------------------- your troops

        // Where each of your soldiers came from (your party, a lord who follows you), to settle the counts afterwards
        private static readonly Dictionary<Agent, MobileParty> SummonedFrom = new Dictionary<Agent, MobileParty>();
        private static int _reserve;
        private static bool _summonHinted;

        /// <summary>The first time an armed fight starts: how to call your men.</summary>
        private static void HintSummon()
        {
            if (_summonHinted) return;
            _summonHinted = true;
            if (TroopsBarred(LocId())) return;
            Say(L.T("AIL_e7b1ddb1", "Puedes llamar a tus tropas:"), Colors.Yellow);
            Say(L.T("AIL_e7b1ddb2", "CTRL + X: 10 soldados"), Colors.Yellow);
            Say(L.T("AIL_e7b1ddb3", "CTRL + ALT + X: todas las tropas (llegan desde las afueras)"), Colors.Yellow);
        }

        /// <summary>The healthy soldiers who could come: your party first, then the lords riding with you and your army.</summary>
        private static List<(CharacterObject c, MobileParty from)> Available()
        {
            var list = new List<(CharacterObject, MobileParty)>();
            try
            {
                var main = MobileParty.MainParty;
                var sources = new List<MobileParty> { main };
                var here = Settlement.CurrentSettlement;
                foreach (var p in MobileParty.All)
                {
                    if (p == null || !p.IsActive || p == main || p.LeaderHero == null || p.MapEvent != null) continue;
                    // the place's own realm inside its walls defends it (SpawnDefenders): never on your side as well
                    if (here != null && p.CurrentSettlement == here && here.MapFaction != null && p.MapFaction == here.MapFaction) continue;
                    bool withMe = (main.Army != null && p.Army == main.Army) || OrderSystem.FindAny(p.LeaderHero)?.Type == "follow";
                    if (withMe && (p.CurrentSettlement == main.CurrentSettlement || p.Position.Distance(main.Position) <= 3f)) sources.Add(p);
                }
                foreach (var src in sources)
                    foreach (var e in src.MemberRoster.GetTroopRoster().Where(e => e.Character != null && !e.Character.IsHero && e.Number - e.WoundedNumber > 0).OrderByDescending(e => e.Character.Tier))
                    {
                        int used = SummonedFrom.Count(kv => kv.Value == src && kv.Key?.Character == e.Character);
                        for (int i = used; i < e.Number - e.WoundedNumber; i++) list.Add((e.Character, src));
                    }
            }
            catch (Exception ex) { Log.Error("Troops that could come", ex); }
            return list;
        }

        /// <summary>Spawn points at the edge of the place (the town gate), for troops coming from outside.</summary>
        private static List<Vec3> Outskirts(Mission m)
        {
            var pts = new List<Vec3>();
            try
            {
                foreach (var tag in new[] { "sp_outside_near_town_main_gate", "spawnpoint_player_outside", "sp_player_outside", "sp_guard_castle", "sp_guard" })
                {
                    foreach (var e in m.Scene.FindEntitiesWithTag(tag)) pts.Add(e.GetGlobalFrame().origin);
                    if (pts.Count > 0) break;
                }
            }
            catch { }
            return pts;
        }

        /// <summary>
        /// Your soldiers into the fight. Ten (Ctrl+X) come up behind you; all of them (Ctrl+Alt+X) come from the outskirts, as many
        /// as there is room for, and the rest wait in reserve and come in as places free. At most 75 outdoors and 5 indoors per
        /// side (settings); never in a tavern, the arena, the prison or the dungeon.
        /// </summary>
        private static void SummonTroops(MissionFightHandler fh, int want, bool all = false)
        {
            try
            {
                var m = Mission.Current;
                string loc = LocId();
                if (TroopsBarred(loc)) { Say(L.T("AIL_5affe75c", "Aquí dentro no pueden entrar tus hombres: solo en la calle."), Colors.Gray); return; }
                int room = TroopCap(loc) - Summoned.Count(a => a != null && a.IsActive());
                var pool = Available();
                if (pool.Count == 0) { Say(L.T("AIL_f71c6f6e", "No te quedan hombres sanos a los que llamar."), Colors.Gray); return; }
                if (room <= 0)
                {
                    if (all) _reserve = pool.Count;
                    Say(L.F("AIL_e7b1ddb4", $"Aún no caben más refuerzos. Quedan {pool.Count} esperando en reserva."), Colors.Gray);
                    return;
                }
                int count = Math.Min(Math.Min(all ? pool.Count : want, room), pool.Count);
                var me = Agent.Main;
                var back = -me.LookDirection.AsVec2;
                if (back.Length < 0.01f) back = new Vec2(0f, -1f);
                back = back.Normalized();
                var side = new Vec2(-back.y, back.x);
                var gates = all && Outdoors(loc) ? Outskirts(m) : new List<Vec3>();
                var rng = new Random();
                EnableOrders(m);
                int done = 0;
                foreach (var (c, from) in pool.Take(count))
                {
                    int k = Summoned.Count;
                    Vec3 pos;
                    if (gates.Count > 0)
                    {
                        var g = gates[k % gates.Count];
                        pos = g + new Vec3((float)(rng.NextDouble() * 10 - 5), (float)(rng.NextDouble() * 10 - 5));
                    }
                    else
                    {
                        var off = back * (4f + (k / 5) * 1.5f) + side * ((k % 5 - 2) * 1.4f);
                        pos = me.Position + new Vec3(off.x, off.y);
                    }
                    try { pos.z = m.Scene.GetGroundHeightAtPosition(pos); } catch { }
                    var dir = (me.Position - pos).AsVec2;
                    dir = dir.Length > 0.01f ? dir.Normalized() : -back;
                    var data = new AgentBuildData(c).TroopOrigin(new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(from.Party, c)).Team(m.PlayerTeam)
                        .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                    var a = m.SpawnAgent(data);
                    if (a == null) continue;
                    a.WieldInitialWeapons();
                    Summoned.Add(a);
                    SummonedFrom[a] = from;
                    IntoFormation(a, m.PlayerTeam);
                    fh.AddAgentToSide(a, true);
                    done++;
                }
                _reserve = all ? Math.Max(0, pool.Count - done) : _reserve;
                if (done > 0)
                {
                    string line = all ? (_reserve > 0 ? L.F("AIL_e7b1ddb5", $"«¡Todas las fuerzas, conmigo!» ({done} se unen, {_reserve} en reserva)") : L.T("AIL_e7b1ddb6", "«¡Guerreros, a las armas! ¡Victoria o muerte!»"))
                                      : L.T("AIL_e7b1ddb7", "«¡A la batalla! ¡Conmigo!»");
                    Say(line, Colors.Green);
                    int formations = m.PlayerTeam.FormationsIncludingEmpty.Count(f => f.CountOfUnits > 0);
                    Say(L.F("AIL_e7b1ddb8", $"Hecho: {formations} formaciones listas para recibir órdenes."), Colors.Gray);
                    Log.Info($"[scene] the player puts {done} soldiers into the fight{(_reserve > 0 ? $", {_reserve} in reserve" : "")}");
                }
            }
            catch (Exception ex) { Log.Error("Call your troops", ex); }
        }

        /// <summary>Places free up as your men fall: those waiting in reserve come in.</summary>
        private static void TickReserve(MissionFightHandler fh)
        {
            if (_reserve <= 0) return;
            int room = TroopCap(LocId()) - Summoned.Count(a => a != null && a.IsActive());
            if (room < 3) return;
            int before = Summoned.Count;
            int keep = _reserve;
            SummonTroops(fh, Math.Min(room, keep), all: true);
            if (Summoned.Count == before) _reserve = 0;
        }

        // ---------------------------------------------------------------- lords riding in from the map

        private static readonly List<(Hero lord, MobileParty party, float due)> Coming = new List<(Hero, MobileParty, float)>();
        private static readonly HashSet<Agent> SparedHeroes = new HashSet<Agent>();
        private static bool _mapLordsAsked;

        private static readonly Tpl TMapLord = new Tpl("fights/map_lord_comes.txt",
@"You are {name}, a noble of {kingdom}, out on the map with {men} men, about {dist} leagues from {settlement} ({owner}'s). The alarm bell is ringing there: {player} has drawn steel inside and is fighting its guard{realm}.
Decide, as yourself, whether you ride in now to put it down or stay away. Weigh your duty to {kingdom}, your honour, what you think of {player}, and what you are doing.
Answer ONLY with JSON: {""decision"": ""come"" or ""stay"", ""line"": ""what you say as you decide, one short sentence in {language}"", ""reason"": ""a few words in English""}");

        public static void TouchStreet() { var _ = TMapLord.Key; }

        /// <summary>When the alarm rings: the 4 lords of the realm nearest the place on the map (within the setting's reach) decide whether to come.</summary>
        private static void CallMapLords()
        {
            if (_mapLordsAsked || !S.Get.StreetLordsFromMap) return;
            _mapLordsAsked = true;
            var st = Settlement.CurrentSettlement;
            if (st?.MapFaction == null) return;
            var m = Mission.Current;
            int session = WorldSim.Session;
            float radius = Math.Max(2, S.Get.StreetMapLordRadius);
            var lords = MobileParty.All.Where(p => p != null && p.IsActive && p.IsLordParty && p.LeaderHero != null && p != MobileParty.MainParty && p.MapEvent == null
                                                  && p.MapFaction == st.MapFaction && p.CurrentSettlement != st && p.BesiegedSettlement == null && !p.IsCurrentlyAtSea
                                                  && p.Position.Distance(st.Position) <= radius)
                                       .OrderBy(p => p.Position.Distance(st.Position)).Take(4).ToList();
            foreach (var p in lords)
            {
                var h = p.LeaderHero;
                float dist = p.Position.Distance(st.Position);
                string ask = TMapLord.Format("name", h.Name, "kingdom", st.MapFaction.Name, "men", Men(p), "dist", Math.Round(dist, 1), "settlement", st.Name,
                    "owner", st.OwnerClan?.Leader?.Name?.ToString() ?? "its lord", "player", Hero.MainHero.Name,
                    "realm", Hero.MainHero.MapFaction != null && Hero.MainHero.MapFaction.IsAtWarWith(st.MapFaction) ? $", and {Hero.MainHero.MapFaction.Name} is at war with you" : "", "language", S.Get.Language);
                Llm.LlmClient.CompleteAsync(new List<Llm.ChatMessage> { new Llm.ChatMessage("user", ask) }, wantJson: true, maxTokensOverride: 180, task: "fight_map_lord")
                    .ContinueWith(t =>
                    {
                        var r = t.IsFaulted ? null : t.Result;
                        MainThread.Post(() =>
                        {
                            try
                            {
                                if (session != WorldSim.Session || Mission.Current != m || _active == null || r == null || !r.Ok) return;
                                int a = r.Text.IndexOf('{'), b = r.Text.LastIndexOf('}');
                                if (a < 0 || b <= a) return;
                                var j = Newtonsoft.Json.Linq.JObject.Parse(r.Text.Substring(a, b - a + 1));
                                bool come = ((string)j["decision"] ?? "").Trim().StartsWith("come", StringComparison.OrdinalIgnoreCase);
                                Log.Info($"[fight] {h.Name} on the map: {(come ? "comes" : "stays away")} ({(string)j["reason"]})");
                                if (!come) return;
                                float due = m.CurrentTime + 60f + 20f * dist;
                                Coming.Add((h, p, due));
                                Say(L.F("AIL_e7b1ddb9", $"{h.Name} ha oído la alarma y cabalga hacia {st.Name}."), Colors.Red);
                            }
                            catch (Exception ex) { Log.Error("A lord on the map deciding", ex); }
                        });
                    });
            }
        }

        /// <summary>The lords who chose to come arrive at the gate with their men and join the fight.</summary>
        private static void TickComing(MissionFightHandler fh, PendingFight f)
        {
            var m = Mission.Current;
            if (Coming.Count == 0 || m == null) return;
            foreach (var c in Coming.Where(x => m.CurrentTime >= x.due).ToList())
            {
                Coming.Remove(c);
                try
                {
                    if (c.lord == null || !c.lord.IsAlive || c.lord.IsPrisoner || c.party == null || !c.party.IsActive) continue;
                    var team = f.Opponent?.Team ?? m.PlayerEnemyTeam;
                    if (team == null) continue;
                    var gates = Outskirts(m);
                    var at = gates.Count > 0 ? gates[0] : Agent.Main.Position + Agent.Main.LookDirection * 25f;
                    int room = Math.Max(0, S.Get.StreetMaxGuards - f.Helpers.Count);
                    // the lord himself, who is never killed in a street fight, and as many of his men as there is room for
                    var lordData = new AgentBuildData(c.lord.CharacterObject).TroopOrigin(new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(c.party.Party, c.lord.CharacterObject)).Team(team)
                        .InitialPosition(in at).InitialDirection(new Vec2(0f, 1f)).NoHorses(true);
                    var la = m.SpawnAgent(lordData);
                    if (la != null) { la.WieldInitialWeapons(); SparedHeroes.Add(la); JoinFight(fh, la, false); }
                    int done = 0;
                    var rng = new Random();
                    foreach (var e in c.party.MemberRoster.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero && x.Number - x.WoundedNumber > 0).OrderByDescending(x => x.Character.Tier).ToList())
                    {
                        for (int i = 0; i < e.Number - e.WoundedNumber && done < room; i++)
                        {
                            var pos = at + new Vec3((float)(rng.NextDouble() * 10 - 5), (float)(rng.NextDouble() * 10 - 5));
                            try { pos.z = m.Scene.GetGroundHeightAtPosition(pos); } catch { }
                            var data = new AgentBuildData(e.Character).TroopOrigin(new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(c.party.Party, e.Character)).Team(team)
                                .InitialPosition(in pos).InitialDirection(new Vec2(0f, 1f)).NoHorses(true);
                            var a = m.SpawnAgent(data);
                            if (a == null) continue;
                            a.WieldInitialWeapons();
                            Defenders.Add((a, c.party));
                            JoinFight(fh, a, false);
                            done++;
                        }
                        if (done >= room) break;
                    }
                    Say(L.F("AIL_e7b1ddba", $"¡{c.lord.Name} y sus hombres se unen a la pelea!"), Colors.Red);
                    Log.Info($"[fight] {c.lord.Name} arrives from the map with {done} men");
                    Unleash();
                }
                catch (Exception ex) { Log.Error("A lord arriving from the map", ex); }
            }
        }

        // ---------------------------------------------------------------- how it ends for you

        private static bool _carried, _captured;
        private static Settlement _fightPlace;

        /// <summary>From FightWatch: the player went down. With your men still standing, they carry you off; alone, the guard takes you.</summary>
        internal static void OnMainDown()
        {
            try
            {
                var f = _active;
                if (f == null || IsFriendly(f) || !f.Unprovoked || _carried || _captured) return;
                var m0 = Mission.Current;
                var fh0 = m0?.GetMissionBehavior<MissionFightHandler>();
                if (m0 == null || _activeIn != m0 || fh0 == null || !fh0.IsThereActiveFight()) return;
                var st = Settlement.CurrentSettlement;
                if (st == null) return;
                _fightPlace = st;
                int mine = Summoned.Count(a => a != null && a.IsActive());
                if (mine > 0)
                {
                    _carried = true;
                    Say(L.F("AIL_e7b1ddbb", $"Tus hombres te sacan a hombros de {st.Name}."), Colors.Yellow);
                    Log.Info($"[fight] the player is down: carried out of {st.Name} by {mine} of their men");
                }
                else
                {
                    _captured = true;
                    Say(L.F("AIL_e7b1ddbc", $"Caes ante la guardia de {st.Name}: te llevan a la mazmorra."), Colors.Red);
                    Log.Info($"[fight] the player is down alone: taken to the dungeon of {st.Name}");
                    Justice.JailAfterFight(st, "attacked people and the guard with steel in the street");
                }
                // not from inside the engine's own report of the fall: a moment later
                var mission = Mission.Current;
                MainThread.Post(() => { try { if (mission != null && Mission.Current == mission && !mission.MissionEnded) mission.EndMission(); } catch { } });
            }
            catch (Exception ex) { Log.Error("Player down in a fight", ex); }
        }

        /// <summary>From FightWatch: the scene ended. An armed fight still going with you on your feet: you got away.</summary>
        internal static void OnSceneEnded()
        {
            try
            {
                var f = _active;
                var st = Settlement.CurrentSettlement;
                bool village = LocId() == "village_center";
                bool beaten = f != null && f.Unprovoked && !IsFriendly(f) && f.Helpers.Count > 0 && f.Helpers.All(h => h == null || !h.IsActive())
                              && Defenders.All(d => d.agent == null || !d.agent.IsActive()) && Agent.Main != null && Agent.Main.IsActive();
                // the men who fell beside you go to the dungeon with you
                if (_captured && st != null)
                {
                    foreach (var a in Summoned.Where(a => a != null && !a.IsActive() && a.State == AgentState.Unconscious).ToList())
                    {
                        if (!(a.Character is CharacterObject c) || !SummonedFrom.TryGetValue(a, out var from) || from.MemberRoster.GetTroopCount(c) <= 0) continue;
                        from.MemberRoster.AddToCounts(c, -1);
                        st.Party.PrisonRoster.AddToCounts(c, 1);
                        Summoned.Remove(a);
                        SummonedFrom.Remove(a);
                    }
                }
                bool escaped = f != null && f.Unprovoked && !IsFriendly(f) && !_captured && !_carried && _alarm && Agent.Main != null && Agent.Main.IsActive() && !beaten;
                var place = st ?? _fightPlace;
                bool carried = _carried;
                if (escaped || carried)
                {
                    if (escaped) Say(L.F("AIL_e7b1ddbd", $"Escapas de {place?.Name} con la guardia pisándote los talones."), Colors.Yellow);
                    try { if (place?.MapFaction != null) ChangeCrimeRatingAction.Apply(place.MapFaction, 20f, true); } catch { }
                    MainThread.Post(() =>
                    {
                        try { if (Settlement.CurrentSettlement != null && Mission.Current == null) { PlayerEncounter.LeaveSettlement(); PlayerEncounter.Finish(true); } }
                        catch (Exception ex) { Log.Error("Leaving after the fight", ex); }
                    });
                }
                if (village && beaten && place?.Village != null) { var v = place; MainThread.Post(() => AskLootOrBurn(v)); }
            }
            catch (Exception ex) { Log.Error("End of a fight in a settlement", ex); }
            finally
            {
                _ordersOn = false; _ordersIn = null; _reserve = 0; _summonHinted = false; _mapLordsAsked = false;
                _carried = false; _captured = false; Coming.Clear(); SparedHeroes.Clear();
            }
        }

        // ---------------------------------------------------------------- a beaten village

        /// <summary>Every defender of the village lies beaten: loot it, burn it, both, or leave it be.</summary>
        private static void AskLootOrBurn(Settlement st)
        {
            try
            {
                if (st?.Village == null || Mission.Current != null) return;
                int session = WorldSim.Session;
                var options = new List<InquiryElement>
                {
                    new InquiryElement("loot", L.T("AIL_e7b1ddbe", "Saquearla"), null, true, L.T("AIL_e7b1df01", "Os lleváis su oro y lo que produce; la aldea pierde hogares, pero sigue en pie.")),
                    new InquiryElement("burn", L.T("AIL_e7b1df02", "Solo quemarla"), null, true, L.T("AIL_e7b1df03", "Fuego a las casas sin llevaros nada: queda arrasada y sin milicia.")),
                    new InquiryElement("both", L.T("AIL_e7b1df04", "Saquearla y quemarla"), null, true, L.T("AIL_e7b1df05", "Primero el botín, luego el fuego.")),
                    new InquiryElement("spare", L.T("AIL_e7b1ddc2", "Dejarla en paz"), null, true, ""),
                };
                MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(L.F("AIL_e7b1ddc3", $"{st.Name} está a tu merced"),
                    L.T("AIL_e7b1ddc4", "Sus defensores están en el suelo. ¿Qué haces con la aldea?"), options, false, 1, 1,
                    L.T("AIL_e5c9b3a3", "Confirmar"), null, chosen =>
                    {
                        if (session != WorldSim.Session || chosen == null || chosen.Count == 0) return;
                        string what = chosen[0].Identifier as string;
                        try { ApplyVillageFate(st, what); } catch (Exception ex) { Log.Error("Village fate", ex); }
                    }, null), true);
            }
            catch (Exception ex) { Log.Error("Loot or burn", ex); }
        }

        /// <summary>
        /// The village's fate. Loot: a third of its hearths (20 at least) are lost, the game's raid gold for each, and its
        /// produce in proportion (a sixtieth of each day's making per hearth lost), handed over in the loot screen; it
        /// stays standing. Burn: only a village still whole; it is left looted and its militia gone. Both: loot, then fire.
        /// </summary>
        private static void ApplyVillageFate(Settlement st, string what)
        {
            if (what == "spare") { Log.Info($"[fight] the player leaves {st.Name} untouched"); return; }
            bool loot = what == "loot" || what == "both", burn = what == "burn" || what == "both";
            if (!loot && !burn) return;
            var v = st.Village;
            int gold = 0; int lost = 0; ItemRoster goods = null;
            if (loot)
            {
                try { (gold, lost, goods) = LootVillage(v); } catch (Exception ex) { Log.Error("village spoils (loot)", ex); }
            }
            bool burned = false;
            if (burn)
            {
                try { burned = BurnVillage(st); } catch (Exception ex) { Log.Error("village spoils (burn)", ex); }
            }
            if (!(loot && lost > 0) && !burned) return;
            var lord = st.OwnerClan?.Leader;
            // what the game makes of preying on peasants: their realm's law and their lord and headmen remember it
            try { if (st.MapFaction != null) ChangeCrimeRatingAction.Apply(st.MapFaction, burned ? 40f : 20f, true); } catch { }
            try { if (lord != null && lord != Hero.MainHero) ChangeRelationAction.ApplyPlayerRelation(lord, burned ? -15 : -5, true, true); } catch { }
            foreach (var n in st.Notables.ToList()) { try { ChangeRelationAction.ApplyPlayerRelation(n, burned ? -20 : -10, false, true); } catch { } }
            string es = burned && loot ? L.F("AIL_e7b1df06", $"Saqueáis {st.Name} y le prendéis fuego: {gold} denarios y {lost} hogares menos.")
                      : burned ? L.F("AIL_e7b1df07", $"{st.Name} arde. No os lleváis nada.")
                      : L.F("AIL_e7b1df08", $"Saqueáis {st.Name}: {gold} denarios y {lost} hogares menos.");
            Say(es, Colors.Yellow);
            Log.Info($"[fight] the player {(loot && burned ? "loots and burns" : burned ? "burns" : "loots")} {st.Name} after beating its defenders ({gold} denars, {lost} hearths)");
            try
            {
                string en = burned && loot ? $"{Hero.MainHero.Name} looted {st.Name} and burned it after beating its defenders."
                          : burned ? $"{Hero.MainHero.Name} burned {st.Name} after beating its defenders."
                          : $"{Hero.MainHero.Name} looted {st.Name} after beating its defenders.";
                NewsStore.Add(new NewsItem
                {
                    Id = WorldSim.NewId("vf"), Type = "raid", Importance = 2, SettlementId = st.StringId, Text = en,
                    TitleEs = burned ? L.F("AIL_e7b1ddc7", $"{st.Name}, quemada") : L.F("AIL_e7b1ddc8", $"{st.Name}, saqueada"), TextEs = es,
                    Kingdoms = st.MapFaction is Kingdom k ? new List<string> { k.StringId } : new List<string>(),
                });
            }
            catch { }
            if (goods != null && goods.Count > 0)
                MainThread.Post(() => { try { if (Mission.Current == null) InventoryScreenHelper.OpenScreenAsLoot(new Dictionary<PartyBase, ItemRoster> { [PartyBase.MainParty] = goods }); } catch (Exception ex) { Log.Error("village loot screen", ex); } });
        }

        internal static (int gold, int lost, ItemRoster goods) LootVillage(Village v)
        {
            if (v?.VillageType == null || MobileParty.MainParty == null) return (0, 0, null);
            int lost = (int)Math.Max(20f, v.Hearth * 0.35f);
            int perHearth = 4;
            try { perHearth = Campaign.Current.Models.RaidModel.GoldRewardForEachLostHearth; } catch { }
            int gold = lost * perHearth;
            if (gold > 0) GiveGoldAction.ApplyBetweenCharacters(null, Hero.MainHero, gold, false);
            var goods = new ItemRoster();
            foreach (var (item, rate) in v.VillageType.Productions)
            {
                int n = (int)(rate / 60f * lost);
                if (item != null && n > 0) goods.AddToCounts(item, n);
            }
            v.Hearth = Math.Max(1f, v.Hearth - lost);
            return (gold, lost, goods);
        }

        internal static bool BurnVillage(Settlement st)
        {
            if (st?.Village == null || MobileParty.MainParty == null || st.Village.VillageState != Village.VillageStates.Normal) return false;
            ChangeVillageStateAction.ApplyBySettingToLooted(st, MobileParty.MainParty);
            if (st.Militia > 0f) st.Militia = 0f;
            return true;
        }
    }
}
