using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using SandBox.Missions.MissionLogics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Violencia que nace de una charla: pelea a puñetazos y duelos dentro de la escena (con el gestor de peleas del juego),
    /// ataque y rendición en el mapa, y llamar a la guardia. Todo empieza cuando el diálogo ya se ha cerrado.
    /// </summary>
    internal static class Combat
    {
        private class PendingFight
        {
            public Speaker Who;
            public Agent Opponent;
            public string Kind;      // brawl | duel | death
            public float Wait;
            public bool Unprovoked;                              // le has pegado tú sin mediar palabra
            public List<Agent> Helpers = new List<Agent>();      // los que salen en su defensa
        }

        private static PendingFight _fight;
        private static Speaker _surrender;
        private static readonly List<Hero> ToKill = new List<Hero>();

        /// <summary>El NPC ha decidido atacar: al cerrar la charla NO se sale del encuentro (aparece el menú de batalla).</summary>
        public static bool KeepEncounter;

        private static Hero P => Hero.MainHero;

        // ============================================================ pelea y duelo en la escena

        private static Agent TalkingAgent()
        {
            try { return Campaign.Current?.ConversationManager?.OneToOneConversationAgent as Agent; }
            catch { return null; }
        }

        /// <summary>¿Se puede pelear aquí y ahora? (hay escena, el NPC está delante y el juego tiene gestor de peleas).</summary>
        public static bool CanFightHere()
        {
            try
            {
                if (!S.Get.AllowCombat || _fight != null) return false;
                var m = Mission.Current;
                if (m == null || Agent.Main == null || !Agent.Main.IsActive()) return false;
                var a = TalkingAgent();
                if (a == null || !a.IsActive() || !a.IsHuman) return false;
                var fh = m.GetMissionBehavior<MissionFightHandler>();
                return fh != null && !fh.IsThereActiveFight();
            }
            catch { return false; }
        }

        public static string QueueFight(Speaker s, string kind)
        {
            var a = TalkingAgent();
            if (a == null) return null;
            _fight = new PendingFight { Who = s, Opponent = a, Kind = kind, Wait = 0.6f };
            Log.Info($"Pelea pendiente con {s.Name}: {kind}");
            switch (kind)
            {
                case "brawl": return L.F("AIL_8e8232dd", $"{s.Name} se remanga: habrá pelea a puñetazos en cuanto termine la charla.");
                case "death": return L.F("AIL_2d487a5a", $"{s.Name} acepta un duelo a muerte. Empieza en cuanto termine la charla.");
                default: return L.F("AIL_8a331c41", $"{s.Name} acepta el duelo. Empieza en cuanto termine la charla.");
            }
        }

        private static void StartFight()
        {
            var f = _fight;
            _fight = null;
            try
            {
                var m = Mission.Current;
                var fh = m?.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || fh.IsThereActiveFight() || Agent.Main == null || !Agent.Main.IsActive() || f.Opponent == null || !f.Opponent.IsActive())
                {
                    Log.Warn("La pelea no ha podido empezar (la escena ha cambiado)");
                    return;
                }
                // No se usa StartFistFight del juego: al acabar devuelve las armas sin mirar si el agente sigue activo
                // y el motor revienta (crash al terminar una pelea en la calle). Aquí se quitan y devuelven a mano.
                List<Stripped> stripped = null;
                var foes = new List<Agent> { f.Opponent };
                foes.AddRange(f.Helpers.Where(h => h != null && h.IsActive() && h != f.Opponent));
                if (f.Kind == "brawl")
                {
                    stripped = new List<Stripped> { Strip(Agent.Main) };
                    foreach (var foe in foes) stripped.Add(Strip(foe));
                }
                fh.StartCustomFight(new List<Agent> { Agent.Main }, foes, false, false, won =>
                {
                    try { Unguard(); } catch (Exception ex) { Log.Error("Curar tras la pelea amistosa", ex); }
                    try { if (stripped != null) foreach (var st in stripped) Restore(st); }
                    catch (Exception ex) { Log.Error("Devolver armas tras la pelea", ex); }
                    MainThread.Post(() => OnFightEnded(f, won));
                });
                // Puños y duelo amistoso: nadie muere; a media vida se rinde y la pelea acaba
                if (IsFriendly(f)) Guard(f, new List<Agent> { Agent.Main }.Concat(foes));
                _active = f; _wave = 6f; _waves = 0; _alarm = false;
                ActiveFoes.Clear(); ActiveFoes.AddRange(foes); _unleash = 0f;
                Unleash();
                Say(f.Unprovoked ? L.F("AIL_1ee35da7", $"¡Atacas a {f.Who.Name}!") : f.Kind == "brawl" ? L.F("AIL_96bdc93c", $"¡Pelea a puñetazos con {f.Who.Name}!") : f.Kind == "death" ? L.F("AIL_70eda317", $"¡Duelo a muerte con {f.Who.Name}!") : L.F("AIL_e97439d8", $"¡Duelo con {f.Who.Name}!"), Colors.Red);
                Log.Info($"Pelea empezada con {f.Who.Name}: {f.Kind}");
                FightBanter.Begin(f.Who, f.Opponent, f.Kind);
            }
            catch (Exception ex) { Log.Error("Empezar la pelea", ex); }
        }

        // ============================================================ peleas amistosas: sin muertes

        // Puños (siempre) y duelo que no es a muerte ni una agresión armada: quien baja a la mitad de la vida con la que empezó
        // se rinde y la pelea termina; nadie muere. En el duelo amistoso el daño se reduce a la mitad. Al acabar, todos vuelven
        // a la vida que tenían antes de pelear. Solo el duelo a muerte (y la agresión con acero) puede matar.
        private class Guarded
        {
            public Agent Agent;
            public float Start;
            public Agent.MortalityState Prev;
            public bool Yielded;
        }

        private static readonly List<Guarded> GuardedAgents = new List<Guarded>();
        private static PendingFight _friendly;
        private static int _yield;              // 0 = nadie se ha rendido; 1 = se rinde el rival (ganas); 2 = te rindes tú
        private static bool _inHealthFix;
        private const float FriendlyDuelDamage = 0.5f;
        private const float YieldAt = 0.5f;

        private static bool IsFriendly(PendingFight f) => f != null && (f.Kind == "brawl" || (f.Kind == "duel" && !f.Unprovoked));

        private static void Guard(PendingFight f, IEnumerable<Agent> agents)
        {
            Unguard();
            _friendly = f; _yield = 0;
            foreach (var a in agents.Distinct())
            {
                try
                {
                    if (a == null || !a.IsActive() || !a.IsHuman) continue;
                    GuardedAgents.Add(new Guarded { Agent = a, Start = a.Health, Prev = a.CurrentMortalityState });
                    a.OnAgentHealthChanged += OnGuardedHealth;
                }
                catch (Exception ex) { Log.Error("Proteger la pelea amistosa", ex); }
            }
        }

        /// <summary>Se llama justo después de cada golpe y antes de que el juego mire si muere: aquí se corrige la vida.</summary>
        private static void OnGuardedHealth(Agent a, float oldHealth, float newHealth)
        {
            if (_inHealthFix || newHealth >= oldHealth || _friendly == null) return;
            try
            {
                var g = GuardedAgents.FirstOrDefault(x => x.Agent == a);
                if (g == null) return;
                float h = newHealth;
                if (_friendly.Kind == "duel") h = oldHealth - (oldHealth - newHealth) * FriendlyDuelDamage;
                float floor = Math.Max(1f, (float)Math.Ceiling(g.Start * YieldAt));
                bool yields = h <= floor;
                if (yields) h = Math.Max(floor, 1f);
                if (Math.Abs(h - newHealth) > 0.01f)
                {
                    _inHealthFix = true;
                    try { a.Health = h; } finally { _inHealthFix = false; }
                }
                if (!yields || g.Yielded) return;
                // Se rinde: ya no recibe más daño hasta que acabe
                g.Yielded = true;
                a.SetMortalityState(Agent.MortalityState.Immortal);
                if (_yield == 0)
                {
                    if (a == Agent.Main) _yield = 2;
                    else if (a == _friendly.Opponent) _yield = 1;
                }
            }
            catch (Exception ex) { Log.Error("Vida en la pelea amistosa", ex); }
        }

        /// <summary>Fin de la pelea amistosa: cada uno vuelve a la vida (y a la mortalidad) que tenía antes.</summary>
        private static void Unguard()
        {
            foreach (var g in GuardedAgents)
            {
                try
                {
                    var a = g.Agent;
                    if (a == null) continue;
                    a.OnAgentHealthChanged -= OnGuardedHealth;
                    if (a.Mission == null || !a.IsActive()) continue;
                    a.SetMortalityState(g.Prev);
                    if (a.Health < g.Start) a.Health = g.Start;
                }
                catch { }
            }
            GuardedAgents.Clear();
            _friendly = null; _yield = 0;
        }

        /// <summary>Alguien se ha rendido en una pelea amistosa: se cierra con el resultado (fuera del golpe, en el tick).</summary>
        private static void EndFriendlyOnYield()
        {
            int y = _yield;
            _yield = 0;
            try
            {
                var fh = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || !fh.IsThereActiveFight()) { Unguard(); return; }
                Log.Info($"Pelea amistosa: {(y == 1 ? "el rival se rinde" : "el jugador se rinde")} a media vida");
                fh.EndFight(y == 1);
            }
            catch (Exception ex) { Log.Error("Terminar la pelea amistosa", ex); }
        }

        // ============================================================ pegar a alguien sin mediar palabra

        /// <summary>Partida nueva o cargada: nada pendiente de la anterior.</summary>
        public static void ResetSession()
        {
            _fight = null; _active = null; _surrender = null; _surrenderParty = null; KeepEncounter = false; _autoAttack = 0f;
            try { Unguard(); } catch { GuardedAgents.Clear(); _friendly = null; _yield = 0; }
            ToKill.Clear(); RoleDeaths.Clear(); ToFade.Clear(); ActiveFoes.Clear(); Summoned.Clear();
        }

        /// <summary>Pelea que empieza sola al abrirse una escena (duelo acordado en el mapa).</summary>
        public static void QueueSceneFight(Speaker s, Agent opponent, string kind, float wait)
        {
            if (s == null || opponent == null) return;
            _fight = new PendingFight { Who = s, Opponent = opponent, Kind = kind, Wait = wait };
            Log.Info($"Pelea en la escena del mapa con {s.Name}: {kind}");
        }

        // Muerte por rol: se cumple al cerrar la charla y salir de la escena
        private static readonly List<(Hero who, bool executed, string how)> RoleDeaths = new List<(Hero, bool, string)>();

        public static void QueueDeath(Hero h, bool executedByPlayer, string how)
        {
            if (h == null || RoleDeaths.Any(d => d.who == h)) return;
            RoleDeaths.Add((h, executedByPlayer, how ?? ""));
            Log.Info($"Muerte por rol pendiente: {h.Name} ({(executedByPlayer ? "ejecución" : "por su mano o sus heridas")}: {how})");
        }

        // Quien se va contigo desaparece de la escena al acabar de hablar
        private static readonly List<Agent> ToFade = new List<Agent>();
        public static void FadeAfterTalk(Agent a) { if (a != null && !ToFade.Contains(a)) ToFade.Add(a); }

        private static float _streetCool;

        // ============================================================ la pelea crece: guardia por oleadas, alarma, y tus hombres

        private static PendingFight _active;
        private static float _wave;
        private static int _waves;
        private static bool _alarm;
        private static readonly List<Agent> Summoned = new List<Agent>();

        private static bool IsGuard(Agent a)
        {
            var c = a.Character as CharacterObject;
            return c != null && !c.IsHero && (c.Occupation == Occupation.Guard || c.Occupation == Occupation.Soldier || c.Occupation == Occupation.PrisonGuard);
        }

        /// <summary>Mete a alguien en la pelea en curso, de un bando u otro, y le pone a pelear de verdad.</summary>
        private static bool JoinFight(MissionFightHandler fh, Agent a, bool playerSide)
        {
            try
            {
                if (a == null || !a.IsActive()) return false;
                fh.AddAgentToSide(a, playerSide);
                var nav = a.GetComponent<SandBox.CampaignAgentComponent>()?.AgentNavigator;
                if (nav != null)
                {
                    var g = nav.GetBehaviorGroup<SandBox.Missions.AgentBehaviors.AlarmedBehaviorGroup>();
                    if (g != null) { g.DisableCalmDown = true; g.AddBehavior<SandBox.Missions.AgentBehaviors.FightBehavior>(); g.SetScriptedBehavior<SandBox.Missions.AgentBehaviors.FightBehavior>(); }
                }
                if (!playerSide) { ActiveFoes.Add(a); if (_active != null && !_active.Helpers.Contains(a)) _active.Helpers.Add(a); }
                return true;
            }
            catch (Exception ex) { Log.Error("Sumar a la pelea", ex); return false; }
        }

        /// <summary>Mientras dura una agresión armada tuya: la guardia va llegando por oleadas; con muchos caídos, suena la alarma.</summary>
        private static void GrowFight(float dt)
        {
            var f = _active;
            var m = Mission.Current;
            if (f == null || m == null || Agent.Main == null) return;
            var fh = m.GetMissionBehavior<MissionFightHandler>();
            if (fh == null || !fh.IsThereActiveFight()) return;
            var cfg = S.Get;

            // Ctrl+X: llamas a los tuyos (de diez en diez); Ctrl+Alt+X: a todos los que caben
            bool ctrl = TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.LeftControl) || TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.RightControl);
            // En una pelea amistosa no entran tus soldados
            if (ctrl && TaleWorlds.InputSystem.Input.IsKeyPressed(TaleWorlds.InputSystem.InputKey.X) && cfg.StreetSummonTroops && !IsFriendly(f))
            {
                bool alt = TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.LeftAlt) || TaleWorlds.InputSystem.Input.IsKeyDown(TaleWorlds.InputSystem.InputKey.RightAlt);
                SummonTroops(fh, alt ? cfg.StreetMaxOwnTroops : 10);
            }

            if (!f.Unprovoked || f.Kind == "brawl" || !cfg.StreetGuardWaves) return;
            if ((_wave -= dt) > 0f) return;
            _wave = cfg.StreetGuardWaveSeconds;
            int inFight = f.Helpers.Count(h => h != null && h.IsActive());
            int down = f.Helpers.Count(h => h != null && !h.IsActive());
            if (f.Helpers.Count >= cfg.StreetMaxGuards) return;
            var here = Agent.Main.Position;
            var come = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a.IsAIControlled && a != f.Opponent && !f.Helpers.Contains(a) && !Summoned.Contains(a) && IsGuard(a)
                                           && a.Position.Distance(here) <= (_alarm ? 150f : 45f))
                               .OrderBy(a => a.Position.Distance(here)).Take(_alarm ? cfg.StreetGuardWaveSize * 2 : cfg.StreetGuardWaveSize).ToList();
            if (!_alarm && (down >= 3 || (down >= 1 && _waves >= 2)))
            {
                _alarm = true;
                Say(L.F("AIL_2f7a736d", $"¡Suena la campana de alarma en {Settlement.CurrentSettlement?.Name}: acude toda la guardia!"), Colors.Red);
            }
            if (come.Count == 0) return;
            int joined = come.Count(a => JoinFight(fh, a, false));
            if (joined > 0)
            {
                _waves++;
                Say(_waves == 1 ? L.T("AIL_9f068a43", "La guardia ha oído la pelea y viene a por ti.") : L.F("AIL_5336141e", $"Llegan {joined} guardias más."), Colors.Red);
                Unleash();
            }
        }

        /// <summary>Tus soldados entran en la pelea, apareciendo a tu espalda. Solo en la calle o en la aldea, nunca en interiores.</summary>
        private static void SummonTroops(MissionFightHandler fh, int want)
        {
            try
            {
                var cfg = S.Get;
                var m = Mission.Current;
                string loc = "";
                try { loc = CampaignMission.Current?.Location?.StringId ?? ""; } catch { }
                if (loc != "center" && loc != "village_center") { Say(L.T("AIL_5affe75c", "Aquí dentro no pueden entrar tus hombres: solo en la calle."), Colors.Gray); return; }
                int room = cfg.StreetMaxOwnTroops - Summoned.Count(a => a != null && a.IsActive());
                want = Math.Min(want, room);
                if (want <= 0) { Say(L.T("AIL_a9628091", "Ya tienes dentro a todos los hombres que caben."), Colors.Gray); return; }
                var me = Agent.Main;
                var back = -me.LookDirection.AsVec2;
                if (back.Length < 0.01f) back = new Vec2(0f, -1f);
                back = back.Normalized();
                var side = new Vec2(-back.y, back.x);
                int done = 0;
                foreach (var e in MobileParty.MainParty.MemberRoster.GetTroopRoster().Where(e => !e.Character.IsHero && e.Number - e.WoundedNumber > 0).OrderByDescending(e => e.Character.Tier).ToList())
                {
                    int already = Summoned.Count(a => a != null && a.Character == e.Character);
                    for (int i = already; i < e.Number - e.WoundedNumber && done < want; i++)
                    {
                        int k = Summoned.Count;
                        var off = back * (4f + (k / 5) * 1.5f) + side * ((k % 5 - 2) * 1.4f);
                        var pos = me.Position + new Vec3(off.x, off.y);
                        try { pos.z = m.Scene.GetGroundHeightAtPosition(pos); } catch { }
                        var dir = -back;
                        var data = new AgentBuildData(e.Character).TroopOrigin(new TaleWorlds.CampaignSystem.AgentOrigins.PartyAgentOrigin(PartyBase.MainParty, e.Character)).Team(m.PlayerTeam)
                            .InitialPosition(in pos).InitialDirection(in dir).NoHorses(true);
                        var a = m.SpawnAgent(data);
                        if (a == null) continue;
                        a.WieldInitialWeapons();
                        Summoned.Add(a);
                        fh.AddAgentToSide(a, true);
                        done++;
                    }
                    if (done >= want) break;
                }
                Say(done > 0 ? L.F("AIL_5a62c49f", $"{done} de tus hombres entran en la pelea.") : L.T("AIL_f71c6f6e", "No te quedan hombres sanos a los que llamar."), done > 0 ? Colors.Green : Colors.Gray);
                if (done > 0) Log.Info($"[escena] el jugador mete {done} soldados en la pelea");
            }
            catch (Exception ex) { Log.Error("Llamar a tus tropas", ex); }
        }

        /// <summary>Acabada la pelea, tus hombres salen de la escena (los caídos quedan heridos en tu partida).</summary>
        private static void DismissSummoned()
        {
            foreach (var a in Summoned.ToList())
            {
                try
                {
                    if (a == null) continue;
                    if (a.IsActive()) a.FadeOut(false, true);
                    else if (a.Character is CharacterObject c && MobileParty.MainParty.MemberRoster.GetTroopCount(c) > 0)
                    {
                        if (a.State == AgentState.Killed) MobileParty.MainParty.MemberRoster.AddToCounts(c, -1);
                        else MobileParty.MainParty.MemberRoster.AddToCounts(c, 0, false, 1);      // queda herido
                    }
                }
                catch { }
            }
            Summoned.Clear();
        }

        // Los que pelean contra ti: se les quita el paso de paseo y lo que estuvieran haciendo, para que corran a por ti
        private static readonly List<Agent> ActiveFoes = new List<Agent>();
        private static float _unleash;

        private static void Unleash()
        {
            foreach (var a in ActiveFoes)
            {
                try
                {
                    if (a == null || !a.IsActive()) continue;
                    if (a.IsUsingGameObject) a.StopUsingGameObject();
                    a.SetMaximumSpeedLimit(-1f, false);
                    a.SetLookAgent(null);
                }
                catch { }
            }
        }

        /// <summary>
        /// En una escena de asentamiento, si sueltas un golpe (puño o arma) teniendo a alguien justo delante, empieza la pelea
        /// con él: a puñetazos si vas con las manos vacías, en serio si llevas el arma fuera. Salen en su defensa sus parientes
        /// y, si hay acero, los guardias que estén cerca.
        /// </summary>
        private static void StreetCheck(float dt)
        {
            try
            {
                if ((_streetCool -= dt) > 0f) return;
                var cfg = S.Get;
                if (!cfg.AllowCombat || !cfg.AllowStreetViolence) return;
                var m = Mission.Current; var me = Agent.Main;
                if (m == null || me == null || !me.IsActive() || me.MountAgent != null || Settlement.CurrentSettlement == null) return;
                if (m.Mode != MissionMode.StartUp) return;
                if (me.GetCurrentActionStage(1) != Agent.ActionStage.AttackRelease) return;
                var fh = m.GetMissionBehavior<MissionFightHandler>();
                if (fh == null || fh.IsThereActiveFight()) return;

                var look = me.LookDirection.AsVec2;
                if (look.Length < 0.01f) return;
                look = look.Normalized();
                // Tiene que estar a tu alcance de verdad y justo delante: un golpe al aire cerca de alguien no cuenta
                bool armedNow = !me.WieldedWeapon.IsEmpty;
                float reach = armedNow ? 1.9f : 1.25f, cone = armedNow ? 0.86f : 0.9f;
                Agent victim = null; float best = reach;
                foreach (var a in m.Agents)
                {
                    if (a == null || a == me || !a.IsHuman || !a.IsActive() || !a.IsAIControlled || !(a.Character is CharacterObject c) || c.Age < 18f) continue;
                    if (a.Team != null && me.Team != null && a.IsEnemyOf(me)) continue;
                    var d = a.Position.AsVec2 - me.Position.AsVec2;
                    float dist = d.Length;
                    if (dist > best || dist < 0.05f || Math.Abs(a.Position.z - me.Position.z) > 1.5f) continue;
                    if (Vec2.DotProduct(look, d * (1f / dist)) < cone) continue;
                    best = dist; victim = a;
                }
                // Golpe al aire: no se vuelve a mirar en un momento (si no, se recorrería a toda la gente en cada fotograma del golpe)
                if (victim == null) { _streetCool = 0.4f; return; }
                _streetCool = 3f;

                var vc = (CharacterObject)victim.Character;
                var sp = vc.IsHero && vc.HeroObject != null ? Speaker.FromHero(vc.HeroObject) : Speaker.FromResident(vc, victim);
                bool armed = !me.WieldedWeapon.IsEmpty;
                var f = new PendingFight { Who = sp, Opponent = victim, Kind = armed ? "duel" : "brawl", Wait = 0f, Unprovoked = true };
                // Quién sale en su defensa: los de su casa que estén al lado; y si hay acero, la guardia cercana
                foreach (var a in m.Agents)
                {
                    if (f.Helpers.Count >= cfg.StreetFightHelpers) break;
                    if (a == null || a == me || a == victim || !a.IsHuman || !a.IsActive() || !a.IsAIControlled || !(a.Character is CharacterObject c) || c.Age < 18f) continue;
                    float dist = a.Position.Distance(victim.Position);
                    bool kin = vc.IsHero && c.IsHero && c.HeroObject?.Clan != null && c.HeroObject.Clan == vc.HeroObject?.Clan && c.HeroObject.Clan != Clan.PlayerClan && dist <= 12f;
                    bool guard = armed && dist <= 20f && (c.Occupation == Occupation.Guard || c.Occupation == Occupation.Soldier || c.Occupation == Occupation.PrisonGuard);
                    if (!kin && !guard) continue;
                    f.Helpers.Add(a);
                    Say(L.F("AIL_9ccbdf55", $"{a.Name} sale en defensa de {sp.Name}."), Colors.Red);
                }
                Log.Info($"[escena] el jugador golpea a {sp.Name} sin mediar palabra ({f.Kind}; {f.Helpers.Count} en su defensa)");
                if (armed && cfg.StreetSummonTroops) Say(L.T("AIL_c4f81902", "Puedes llamar a tus hombres: Ctrl+X (diez) o Ctrl+Alt+X (todos los que quepan). Solo en la calle."), Colors.Gray);
                _fight = f;
                StartFight();
            }
            catch (Exception ex) { Log.Error("Pelea callejera", ex); _streetCool = 5f; }
        }

        private class Stripped
        {
            public Agent Agent;
            public List<(EquipmentIndex slot, MissionWeapon weapon)> Weapons = new List<(EquipmentIndex, MissionWeapon)>();
        }

        /// <summary>Guarda y quita las armas del agente para pelear a puñetazos.</summary>
        private static Stripped Strip(Agent a)
        {
            var s = new Stripped { Agent = a };
            try
            {
                a.TryToSheathWeaponInHand(Agent.HandIndex.OffHand, Agent.WeaponWieldActionType.Instant);
                a.TryToSheathWeaponInHand(Agent.HandIndex.MainHand, Agent.WeaponWieldActionType.Instant);
                for (var i = EquipmentIndex.WeaponItemBeginSlot; i < EquipmentIndex.NumAllWeaponSlots; i++)
                {
                    var w = a.Equipment[i];
                    if (w.IsEmpty) continue;
                    s.Weapons.Add((i, w));
                    a.RemoveEquippedWeapon(i);
                }
            }
            catch (Exception ex) { Log.Error("Quitar armas para la pelea", ex); }
            return s;
        }

        /// <summary>Devuelve las armas solo si el agente sigue en pie en la escena.</summary>
        private static void Restore(Stripped s)
        {
            var a = s?.Agent;
            if (a == null || a.Mission == null || !a.IsActive()) return;
            foreach (var (slot, weapon) in s.Weapons)
            {
                if (!a.Equipment[slot].IsEmpty) continue;
                var w = weapon;
                a.EquipWeaponWithNewEntity(slot, ref w);
            }
        }

        private static void OnFightEnded(PendingFight f, bool playerWon)
        {
            try
            {
                FightBanter.End(playerWon);
                _active = null;
                DismissSummoned();
                var s = f.Who;
                var mem = s.Memory;
                string you = Prompt.ContextBuilder.PlayerNameFor(s) ?? "the stranger";
                string what = f.Kind == "brawl" ? "a fist fight" : f.Kind == "death" ? "a duel to the death" : "a duel";
                Log.Info($"Pelea con {s.Name} ({f.Kind}): {(playerWon ? "gana el jugador" : "pierde el jugador")}");

                // Lo recuerda, y la próxima charla lo sabe
                mem.Turns.Add(new ChatTurn
                {
                    Role = "player", Day = CampaignTime.Now.ToDays,
                    Text = $"[What happened: {(f.Unprovoked ? $"{you} attacked you without a word of warning, and you fought {what}" : $"you and {you} fought {what}")}. " + (playerWon ? "They beat you." : "You beat them.") + "]"
                });
                // Testigos: una agresión solo te pasa factura si alguien vivo la ha visto, y con tu nombre solo si te conocen
                Witness.Result seen = null;
                if (f.Unprovoked)
                {
                    var victims = new List<Agent> { f.Opponent };
                    victims.AddRange(f.Helpers);
                    seen = S.Get.EnableWitnesses ? Witness.Assess(victims) : new Witness.Result { Level = 2, Count = 1 };
                    ChatSession.AddNote(mem, $"{you} attacked me without provocation.", S.Get.MaxNotesPerHero, s.Name);
                    mem.Trust = Math.Max(-100, mem.Trust - 25);
                    float crime = (f.Kind == "brawl" ? 5f : 15f) * (seen.Level == 2 ? 1f : seen.Level == 1 ? 0.5f : 0f);
                    try { var stf = Settlement.CurrentSettlement?.MapFaction; if (crime > 0f && stf != null && stf != P.MapFaction) ChangeCrimeRatingAction.Apply(stf, crime, true); } catch { }
                    Say(seen.Level == 0 ? L.T("AIL_ef1030cb", "Nadie lo ha visto: nadie sabrá que has sido tú.")
                      : seen.Level == 1 ? L.F("AIL_a58c5f5d", $"Te han visto ({seen.Count} testigo(s)), pero nadie sabe quién eres: buscan a un forastero armado.")
                      : L.F("AIL_992e899d", $"{seen.ByWhom} te ha reconocido: se sabrá que has sido tú."), seen.Level == 0 ? Colors.Green : seen.Level == 1 ? Colors.Yellow : Colors.Red);
                }
                ChatSession.AddNote(mem, playerWon ? $"{you} beat me in {what}." : $"I beat {you} in {what}.", S.Get.MaxNotesPerHero, s.Name);
                mem.Tension = Math.Max(0, mem.Tension - 40);

                if (s.IsHero)
                {
                    // Quien pelea bien se gana respeto (más entre valientes); perder un duelo serio escuece
                    int rel = f.Unprovoked ? (f.Kind == "brawl" ? -4 : -10) : f.Kind == "brawl" ? 1 : playerWon ? 2 : 0;
                    if (seen != null && !(f.Opponent != null && (f.Opponent.IsActive() || f.Opponent.State == AgentState.Unconscious))) rel = 0;      // un muerto no guarda rencor
                    if (rel != 0) ChangeRelationAction.ApplyPlayerRelation(s.Hero, rel, affectRelatives: false, showQuickNotification: true);
                }

                if (f.Kind == "death")
                {
                    if (playerWon)
                    {
                        if (s.IsHero && S.Get.AllowDuelDeath) { ToKill.Add(s.Hero); Say(L.F("AIL_e10a40d5", $"{s.Name} cae en el duelo."), Colors.Red); }
                        else Say(L.F("AIL_a92c1422", $"{s.Name} queda tendido y malherido: has ganado el duelo."), Colors.Yellow);
                    }
                    else
                    {
                        // El jugador no muere (se acabaría la partida): queda malherido
                        try { P.HitPoints = 1; } catch { }
                        Say(L.F("AIL_080a0e95", $"{s.Name} te deja por muerto. Sobrevives de milagro."), Colors.Red);
                    }
                }
                else Say(playerWon ? L.F("AIL_d0109f54", $"Has vencido a {s.Name}.") : L.F("AIL_805db527", $"{s.Name} te ha vencido."), Colors.Yellow);

                // El mundo lo cuenta a su manera: lo que se sabe depende de quién lo vio
                if (f.Unprovoked && seen != null && (f.Kind != "brawl" || s.IsHero))
                {
                    bool victimDead = !(f.Opponent != null && (f.Opponent.IsActive() || f.Opponent.State == AgentState.Unconscious));
                    string victimEn = s.IsHero ? $"{s.Name}{(s.Hero.IsLord ? $", a noble of clan {s.Hero.Clan?.Name}" : "")}" : (s.IsFemale ? "a local woman" : "a local man") + $" ({s.Character?.Name})";
                    string doer = seen.Level == 2 ? $"{P.Name} of clan {Clan.PlayerClan?.Name}" : seen.Level == 1 ? "an armed stranger whom witnesses saw but nobody could name" : "someone nobody saw";
                    int downed = f.Helpers.Count(h => h != null && !h.IsActive());
                    WorldSim.OnIncident($"{doer} attacked {victimEn} {(f.Kind == "brawl" ? "with bare fists" : "with drawn steel")}, without provocation. " +
                                        (victimDead ? "The victim was left dead. " : playerWon ? "The victim was left on the ground, alive. " : "The attacker got the worst of it. ") +
                                        (downed > 0 ? $"{downed} who came to the victim's defence (guards or kin) were also cut down. " : "") +
                                        (seen.Level == 0 ? "There were no living witnesses: the body was simply found." : $"{seen.Count} witness(es) saw it."),
                                        Settlement.CurrentSettlement, f.Kind != "brawl");
                }

                // Si hay un guardia cerca, viene a pedir cuentas (si nadie lo ha visto, no viene nadie)
                if (seen == null || seen.Level > 0) Justice.OnFightEnded(s, f.Opponent, f.Kind, playerWon, f.Unprovoked);

                if (seen != null && seen.Level < 2)
                {
                    // Sin tu nombre: se cuenta lo ocurrido, no quién lo hizo
                    if (s.IsHero || f.Kind != "brawl")
                    {
                        string victimName = s.IsHero ? s.Name : (s.IsFemale ? "a local woman" : "a local man"), victimEs = s.IsHero ? s.Name : (s.IsFemale ? L.T("AIL_53ab6227", "una vecina") : L.T("AIL_1c6f47d4", "un vecino"));
                        bool anyone = seen.Level == 1;
                        NewsStore.Add(new NewsItem
                        {
                            Type = "rumor", Importance = s.IsHero ? 2 : 1,
                            Text = anyone ? $"An armed stranger attacked {victimName}{(s.Settlement != null ? " at " + s.Settlement.Name : "")}. Witnesses saw the attacker but nobody knows who it was."
                                          : $"{victimName} was found {(f.Kind == "brawl" ? "beaten" : "cut down")}{(s.Settlement != null ? " at " + s.Settlement.Name : "")}. Nobody saw who did it.",
                            TitleEs = anyone ? L.T("AIL_f7d8e495", "Agresión de un forastero") : L.T("AIL_0aaea1bc", "Agresión sin testigos"),
                            TextEs = anyone ? L.F("AIL_122eb7e1", $"Un forastero armado atacó a {victimEs}{(s.Settlement != null ? L.T("AIL_4b08b3e0", " en ") + s.Settlement.Name : "")}. Hay quien vio al agresor, pero nadie sabe quién era.")
                                            : L.F("AIL_ce1a0814", $"Han encontrado a {victimEs} {(f.Kind == "brawl" ? L.T("AIL_717f4a1a", "apaleado") : L.T("AIL_07cfa3c4", "acuchillado"))}{(s.Settlement != null ? L.T("AIL_4b08b3e0", " en ") + s.Settlement.Name : "")}. Nadie vio quién lo hizo."),
                            SettlementId = s.Settlement?.StringId,
                            Kingdoms = s.Faction != null ? new List<string> { s.Faction.StringId } : new List<string>(),
                            Heroes = s.IsHero ? new List<string> { s.Hero.StringId } : new List<string>(),
                        });
                    }
                }
                else if (s.IsHero || f.Kind != "brawl")
                {
                    string winner = playerWon ? P.Name.ToString() : s.Name, loser = playerWon ? s.Name : P.Name.ToString();
                    string whatEs = f.Kind == "brawl" ? L.T("AIL_03bb5d20", "una pelea a puñetazos") : f.Kind == "death" ? L.T("AIL_58f9d028", "un duelo a muerte") : L.T("AIL_a65a644d", "un duelo");
                    NewsStore.Add(new NewsItem
                    {
                        Type = "rumor", Importance = f.Kind == "death" && s.IsHero ? 3 : 2,
                        Text = $"{winner} defeated {loser} in {what}" + (s.Settlement != null ? $" at {s.Settlement.Name}." : "."),
                        TitleEs = f.Kind == "brawl" ? L.T("AIL_a0bdbcf2", "Pelea a puñetazos") : L.T("AIL_0db9b33c", "Duelo"),
                        TextEs = L.F("AIL_c140427d", $"{winner} venció a {loser} en {whatEs}") + (s.Settlement != null ? L.F("AIL_d63eaabc", $" en {s.Settlement.Name}.") : "."),
                        SettlementId = s.Settlement?.StringId,
                        Kingdoms = s.Faction != null ? new List<string> { s.Faction.StringId } : new List<string>(),
                        Heroes = s.IsHero ? new List<string> { s.Hero.StringId, P.StringId } : new List<string> { P.StringId },
                    });
                }
            }
            catch (Exception ex) { Log.Error("Fin de la pelea", ex); }
        }

        // ============================================================ mapa: ataque y rendición

        /// <summary>
        /// La partida con la que hablas en el mapa: la que manda el héroe, o, si quien habla no es un héroe (bandidos,
        /// partidas sin señor), la partida de la charla en curso.
        /// </summary>
        public static MobileParty MapFoeParty(Speaker s)
        {
            try
            {
                if (s == null) return null;
                if (s.IsHero) return OrderSystem.PartyLedBy(s.Hero);
                if (Mission.Current != null) return null;
                // El jefe de una partida sin señor con la que se abrió la charla (bandidos, caravana, aldeanos)
                if (s.Band != null) return s.Band.IsActive && !s.Band.IsMainParty ? s.Band : null;
                var p = MobileParty.ConversationParty ?? PlayerEncounter.EncounteredMobileParty;
                return p != null && p.IsActive && !p.IsMainParty && p.LeaderHero == null ? p : null;
            }
            catch { return null; }
        }

        public static bool EnemyOnMap(Speaker s)
        {
            try
            {
                if (!S.Get.AllowCombat || Mission.Current != null || PlayerEncounter.Current == null || PlayerEncounter.InsideSettlement || P.MapFaction == null) return false;
                var p = MapFoeParty(s);
                if (p == null) return false;
                var fac = s.IsHero ? s.Faction : p.MapFaction;
                return fac != null && fac != P.MapFaction && (fac.IsBanditFaction || fac.IsAtWarWith(P.MapFaction));
            }
            catch { return false; }
        }

        public static int Men(MobileParty p) => p?.MemberRoster.TotalHealthyCount ?? 0;

        // ============================================================ mapa: la batalla empieza sola

        // El jefe de la partida ha decidido atacar en la charla: al cerrarla, en vez de dejar al jugador en el menú del
        // encuentro (donde tendría que pulsar «Atacar»), la batalla empieza sola. Igual con señores que con bandidos.
        private static float _autoAttack;        // segundos (reales, fuera de la charla) que se espera al menú del encuentro
        private static string _autoAttackWho;

        public static void QueueMapAttack(Speaker s)
        {
            KeepEncounter = true;
            _autoAttack = 15f;
            _autoAttackWho = s?.Name;
            Log.Info($"Ataque en el mapa de {s?.Name}: la batalla empezará al cerrar la charla");
        }

        private static void TickMapAttack(float dt)
        {
            try
            {
                if ((_autoAttack -= dt) <= 0f) { _autoAttack = 0f; Log.Warn($"La batalla con {_autoAttackWho} no ha empezado sola: queda el menú del encuentro"); return; }
                if (PlayerEncounter.Current == null) { _autoAttack = 0f; return; }       // el encuentro ya se ha resuelto
                var mc = Campaign.Current?.CurrentMenuContext;
                if (mc?.GameMenu == null || mc.GameMenu.StringId != "encounter") return;   // aún no ha salido el menú
                if (PlayerEncounter.Battle == null || TaleWorlds.CampaignSystem.MapEvents.MapEvent.PlayerMapEvent == null) return;
                _autoAttack = 0f;
                var args = new TaleWorlds.CampaignSystem.GameMenus.MenuCallbackArgs(mc, null);
                if (!global::Helpers.MenuHelper.EncounterAttackCondition(args) || !args.IsEnabled)
                {
                    // Herido, en una balsa…: el juego no deja atacar; el jugador decide en el menú
                    Log.Info($"La batalla con {_autoAttackWho} no puede empezar sola (el juego no deja atacar ahora)");
                    return;
                }
                Log.Info($"Empieza la batalla con {_autoAttackWho}");
                global::Helpers.MenuHelper.EncounterAttackConsequence(args);
            }
            catch (Exception ex) { _autoAttack = 0f; Log.Error("Empezar la batalla del mapa", ex); }
        }

        /// <summary>El NPC se rinde con todo su grupo: se resuelve al cerrar la charla.</summary>
        public static string QueueSurrender(Speaker s)
        {
            _surrender = s;
            _surrenderParty = MapFoeParty(s);      // al cerrar la charla ya no hay partida de la charla
            return L.F("AIL_807992a5", $"{s.Name} rinde las armas.");
        }

        private static MobileParty _surrenderParty;

        private static void DoSurrender()
        {
            var s = _surrender;
            var party = _surrenderParty ?? MapFoeParty(s);
            _surrender = null; _surrenderParty = null;
            try
            {
                if (party == null || !party.IsActive) return;
                if (PlayerEncounter.Current != null) PlayerEncounter.Finish(true);

                var main = MobileParty.MainParty;
                int taken = 0;
                foreach (var e in party.MemberRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null || e.Character.IsHero || e.Number <= 0) continue;
                    main.PrisonRoster.AddToCounts(e.Character, e.Number);
                    taken += e.Number;
                }
                // Sus prisioneros pasan a ti; los de tu bando quedan libres
                foreach (var e in party.PrisonRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null) continue;
                    if (e.Character.IsHero)
                    {
                        var ph = e.Character.HeroObject;
                        if (ph != null && ph.MapFaction != null && !ph.MapFaction.IsAtWarWith(P.MapFaction)) EndCaptivityAction.ApplyByReleasedAfterBattle(ph);
                        else if (ph != null) TransferPrisonerAction.Apply(e.Character, party.Party, main.Party);
                    }
                    else main.PrisonRoster.AddToCounts(e.Character, e.Number);
                }
                var heroes = party.MemberRoster.GetTroopRoster().Where(e => e.Character != null && e.Character.IsHero && e.Character.HeroObject != null)
                    .Select(e => e.Character.HeroObject).ToList();
                foreach (var x in heroes) TakePrisonerAction.Apply(main.Party, x);
                if (party.IsActive) DestroyPartyAction.Apply(main.Party, party);
                Say(L.F("AIL_454cbf99", $"{s.Name} se rinde: {taken} hombres pasan a ser tus prisioneros."), Colors.Green);
                Log.Info($"Rendición de {s.Name}: {taken} prisioneros");
            }
            catch (Exception ex) { Log.Error("Rendición", ex); }
        }

        // ============================================================ tick

        private static float _playerSurrender;

        /// <summary>El jugador se rinde a quien tiene delante en el mapa: se hace efectivo al cerrarse la charla.</summary>
        public static void QueuePlayerSurrender() { KeepEncounter = true; _playerSurrender = 12f; }

        private static void TickPlayerSurrender(float dt)
        {
            if (_playerSurrender <= 0f) return;
            try
            {
                if ((_playerSurrender -= dt) <= 0f) { _playerSurrender = 0f; KeepEncounter = false; return; }
                if (Campaign.Current?.ConversationManager?.IsConversationInProgress == true || Mission.Current != null) return;
                if (PlayerEncounter.Current == null) { _playerSurrender = 0f; KeepEncounter = false; return; }
                _playerSurrender = 0f; KeepEncounter = false;
                var foe = PlayerEncounter.EncounteredMobileParty;
                if (PlayerEncounter.InsideSettlement || foe == null || foe.MapFaction == null || Hero.MainHero.MapFaction == null
                    || !(foe.MapFaction.IsBanditFaction || foe.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction)))
                { Log.Warn("Rendición del jugador: el encuentro ya no es con un enemigo en el mapa; no se hace nada"); return; }
                // Lo mismo que hace la opción «Rendirse» del menú del encuentro
                PlayerEncounter.PlayerSurrender = true;
                PlayerEncounter.Update();
                Log.Info("El jugador se rinde: queda prisionero");
            }
            catch (Exception ex) { _playerSurrender = 0f; KeepEncounter = false; Log.Error("Rendición del jugador", ex); }
        }

        public static void Tick(float dt)
        {
            TickPlayerSurrender(dt);
            try
            {
                if (Campaign.Current == null) { _fight = null; _surrender = null; _surrenderParty = null; ToKill.Clear(); KeepEncounter = false; _autoAttack = 0f; return; }
                bool talking = Campaign.Current.ConversationManager?.IsConversationInProgress == true;
                FightBanter.Tick(dt);
                if (_yield != 0) EndFriendlyOnYield();
                if (GuardedAgents.Count > 0 && Mission.Current == null) Unguard();
                if (_active != null) GrowFight(dt);
                if (ActiveFoes.Count > 0)
                {
                    var fhNow = Mission.Current?.GetMissionBehavior<MissionFightHandler>();
                    if (fhNow == null || !fhNow.IsThereActiveFight()) ActiveFoes.Clear();
                    else if ((_unleash -= dt) <= 0f) { _unleash = 1f; Unleash(); }
                }
                if (!talking && _fight == null) StreetCheck(dt);

                if (KeepEncounter && !talking && !ChatSession.IsBusy) KeepEncounter = false;

                if (_fight != null && !talking)
                {
                    if (Mission.Current == null) { _fight = null; }
                    else if ((_fight.Wait -= dt) <= 0f) StartFight();
                }

                if (_surrender != null && !talking && Mission.Current == null) DoSurrender();
                if (_autoAttack > 0f && Mission.Current != null) _autoAttack = 0f;      // la batalla (u otra escena) ya ha empezado
                if (_autoAttack > 0f && !talking && !ChatSession.IsBusy) TickMapAttack(dt);

                if (ToFade.Count > 0 && !talking)
                {
                    foreach (var a in ToFade.ToList()) { try { if (Mission.Current != null && a.IsActive()) a.FadeOut(false, true); } catch { } }
                    ToFade.Clear();
                }
                if (RoleDeaths.Count > 0 && Mission.Current == null && !talking && !ChatSession.IsBusy)
                {
                    foreach (var d in RoleDeaths.ToList())
                    {
                        RoleDeaths.Remove(d);
                        try
                        {
                            if (d.who == null || !d.who.IsAlive) continue;
                            if (d.executed) KillCharacterAction.ApplyByExecution(d.who, P, true, true);
                            else KillCharacterAction.ApplyByMurder(d.who, null, true);
                            Log.Info($"Muerte por rol cumplida: {d.who.Name}");
                        }
                        catch (Exception ex) { Log.Error("Muerte por rol", ex); }
                    }
                }
                if (ToKill.Count > 0 && Mission.Current == null && !talking)
                {
                    foreach (var h in ToKill.ToList())
                    {
                        ToKill.Remove(h);
                        if (h != null && h.IsAlive) KillCharacterAction.ApplyByBattle(h, P, true);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Combate: tick", ex); _fight = null; _surrender = null; }
        }

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));
    }
}
