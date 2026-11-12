using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.UI;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.Core;
using TaleWorlds.Engine;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Battle
{
    /// <summary>
    /// IA de táctica en batallas campales. El jefe de cada bando que no mandas tú recibe cada cierto tiempo una foto de la
    /// batalla (formaciones, bajas, distancias) y da órdenes reales a sus formaciones. Mientras decide, o si la IA falla,
    /// siguen las tácticas del juego. Con Alt+B das tú órdenes a tus tropas escribiéndolas con tus palabras.
    /// </summary>
    internal static class BattleAI
    {
        private static readonly Tpl TCommander = new Tpl("battle/commander_orders.txt",
@"You are {commander}, commanding one side of a field battle in Calradia, the medieval world of Mount & Blade II: Bannerlord. You fight against {enemy}.
Your temper as a commander: {temper}
Time since the lines were drawn: {time}. Your side: {own_total} fighting men left of {own_start}. The enemy: {enemy_total} left of {enemy_start}.

YOUR FORMATIONS (name — what it is — men — where it stands — what it is doing):
{own}

ENEMY FORMATIONS (name — what it is — men — where it stands):
{foe}

Your recent orders (oldest first): {history}

Give orders for this moment, as this commander would, and think like a soldier: infantry holds the line and fixes the enemy; archers need a clear shot and someone between them and enemy horse; cavalry is for flanks, for enemy archers and for breaking men who are already engaged, not for charging fresh spearmen head-on; do not send formations in one by one; if you are much the stronger, close and finish it; if much the weaker, hold good ground and make them come to you. Do not change a sound order every moment: repeat it or leave that formation out. You may leave a formation to its captain's judgment by not listing it.
For each formation you order:
- ""move"": charge | advance | hold | fall_back | flank_left | flank_right | attack:<enemy formation name> | retreat   (flank = swing wide around that side of the enemy's main body; retreat = leave the field, only when all is lost: outnumbered about three to one)
- ""shape"" (optional): line | shield_wall | loose | square | circle | scatter
- ""fire"" (optional): at_will | hold
""shout"": what you bellow to your men right now (under 90 characters, in {language}), in your own voice. ""plan"": your intent in ONE short English sentence.
Answer ONLY with JSON: { ""orders"": [ { ""formation"": ""<your formation name>"", ""move"": ""..."", ""shape"": """", ""fire"": """" } ], ""shout"": ""..."", ""plan"": ""..."" }");

        private static readonly Tpl TPlayerOrder = new Tpl("battle/player_orders.txt",
@"A commander in a field battle in Calradia (Mount & Blade II: Bannerlord) has just shouted an order to their troops in their own words. Turn it into orders for their formations. Do exactly what was asked, no more: formations not mentioned and not covered by words like ""everyone"" or ""all"" get no order.

THEIR FORMATIONS (name — what it is — men — where it stands — what it is doing):
{own}

ENEMY FORMATIONS (name — what it is — men — where it stands):
{foe}

The order as shouted: «{said}»

For each formation concerned:
- ""move"": charge | advance | hold | fall_back | flank_left | flank_right | attack:<enemy formation name> | retreat
- ""shape"" (optional): line | shield_wall | loose | square | circle | scatter
- ""fire"" (optional): at_will | hold
Answer ONLY with JSON: { ""orders"": [ { ""formation"": ""<formation name>"", ""move"": ""..."", ""shape"": """", ""fire"": """" } ], ""understood"": ""what you understood, one short sentence in {language}"" }");

        public static void Touch() { var _ = TCommander.Key + TPlayerOrder.Key; }

        private class Side
        {
            public Team Team;
            public float NextAt, LastOkAt = -1f;
            public int Gen, StartMen, MenAtLastDecision;
            public bool Waiting;
            public readonly List<string> History = new List<string>();
            public readonly HashSet<Formation> Held = new HashSet<Formation>();      // formaciones a las que les hemos quitado el mando al juego
            public readonly Dictionary<Formation, string> Doing = new Dictionary<Formation, string>();
        }

        private static Mission _mission;
        private static readonly List<Side> Sides = new List<Side>();
        private static int _requests;
        private static bool _playerWaiting;
        /// <summary>Una orden que llega más tarde que esto (segundos de batalla) ya no vale: la situación ha cambiado.</summary>
        private const float MaxLateSeconds = 20f;
        /// <summary>Un jefe de la IA solo abandona el campo si le superan de verdad (sus hombres &lt; 35 % de los del enemigo).</summary>
        private const float RetreatRatio = 0.35f;

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        private static bool BattleOn(Mission m)
        {
            try
            {
                return m != null && Campaign.Current != null && MapEvent.PlayerMapEvent != null && m.IsFieldBattle && m.Mode == MissionMode.Battle
                       && Agent.Main != null && m.PlayerTeam != null;
            }
            catch { return false; }
        }

        private static int Men(Team t) => t.FormationsIncludingEmpty.Sum(f => f.CountOfUnits);

        private static IEnumerable<Team> EnemiesOf(Team t) =>
            _mission.Teams.Where(o => o != t && o.Side != BattleSideEnum.None && o.Side != t.Side && o.ActiveAgents.Count > 0);

        private static string Kind(Formation f)
        {
            try
            {
                var q = f.QuerySystem;
                if (q.IsRangedCavalryFormation) return "horse archers";
                if (q.IsCavalryFormation) return "cavalry";
                if (q.IsRangedFormation) return "archers/crossbowmen on foot";
                return "infantry";
            }
            catch { return "troops"; }
        }

        private static string Name(Formation f) => f.FormationIndex.ToString();

        /// <summary>Dónde está algo respecto a un punto y una dirección de frente: «120 m ahead, to the left».</summary>
        private static string Where(Vec2 from, Vec2 front, Vec2 what)
        {
            var d = what - from;
            float dist = d.Length;
            if (dist < 12f) return "right here";
            var left = new Vec2(-front.y, front.x);
            float ahead = Vec2.DotProduct(d, front), side = Vec2.DotProduct(d, left);
            string a = Math.Abs(ahead) < dist * 0.35f ? "" : ahead > 0 ? "ahead" : "behind";
            string s = Math.Abs(side) < dist * 0.35f ? "" : side > 0 ? "to the left" : "to the right";
            return $"{(int)(dist / 10f) * 10} m {a}{(a != "" && s != "" ? ", " : "")}{s}".TrimEnd();
        }

        private static void Snapshot(Team team, Side side, out string own, out string foe, out Vec2 centre, out Vec2 front, out Formation mainEnemy)
        {
            var mine = team.FormationsIncludingEmpty.Where(f => f.CountOfUnits > 0).ToList();
            var theirs = EnemiesOf(team).SelectMany(t => t.FormationsIncludingEmpty).Where(f => f.CountOfUnits > 0).ToList();
            centre = Vec2.Zero; int n = 0;
            foreach (var f in mine) { centre += f.CurrentPosition * f.CountOfUnits; n += f.CountOfUnits; }
            if (n > 0) centre = centre * (1f / n);
            mainEnemy = theirs.OrderByDescending(f => f.CountOfUnits).FirstOrDefault();
            front = mainEnemy != null ? (mainEnemy.CurrentPosition - centre) : new Vec2(0f, 1f);
            if (front.Length < 0.1f) front = new Vec2(0f, 1f);
            front = front.Normalized();

            var sb = new StringBuilder();
            foreach (var f in mine)
            {
                var pos = f.CurrentPosition;
                float nearest = theirs.Count == 0 ? 999f : theirs.Min(e => e.CurrentPosition.Distance(pos));
                string doing = side != null && side.Held.Contains(f) && side.Doing.TryGetValue(f, out var d) ? "your last order: " + d : "acting on its captain's own judgment";
                if (f.PlayerOwner != null && f.PlayerOwner == Agent.Main && side != null) doing = "under another commander's direct orders (you cannot order it)";
                sb.AppendLine($"- {Name(f)} — {Kind(f)} — {f.CountOfUnits} men — {Where(centre, front, pos)} of your centre; nearest enemy {(int)nearest} m{(nearest < 18f ? " (IN MELEE)" : "")} — {doing}");
            }
            own = sb.Length == 0 ? "(none left)" : sb.ToString().TrimEnd();
            sb.Clear();
            foreach (var f in theirs)
                sb.AppendLine($"- {Name(f)}{(theirs.Count(x => Name(x) == Name(f)) > 1 ? "/" + f.Team.TeamIndex : "")} — {Kind(f)} — {f.CountOfUnits} men — {Where(centre, front, f.CurrentPosition)} of your centre");
            foe = sb.Length == 0 ? "(none in sight)" : sb.ToString().TrimEnd();
        }

        private static string Temper(Agent general)
        {
            var h = (general?.Character as CharacterObject)?.HeroObject;
            if (h == null) return "a seasoned captain, careful with his men.";
            int valor = h.GetTraitLevel(DefaultTraits.Valor), calc = h.GetTraitLevel(DefaultTraits.Calculating), mercy = h.GetTraitLevel(DefaultTraits.Mercy);
            int tactics = h.GetSkillValue(DefaultSkills.Tactics);
            return $"{(valor > 0 ? "bold, likes to strike first" : valor < 0 ? "cautious, hates to gamble" : "neither rash nor timid")}; "
                 + $"{(calc > 0 ? "cold and methodical" : calc < 0 ? "impulsive" : "practical")}; {(mercy < 0 ? "spends men freely" : "cares what his men pay")}; "
                 + $"{(tactics >= 150 ? "a master tactician" : tactics >= 80 ? "a competent tactician" : "no great tactician: plain, direct orders")}.";
        }

        // ============================================================ aplicar órdenes

        private static int Apply(Team team, Side side, JArray orders, Vec2 centre, Vec2 front, Formation mainEnemy, bool playerOrder, StringBuilder shown)
        {
            int done = 0;
            if (orders == null) return 0;
            var mine = team.FormationsIncludingEmpty.Where(f => f.CountOfUnits > 0).ToList();
            var theirs = EnemiesOf(team).SelectMany(t => t.FormationsIncludingEmpty).Where(f => f.CountOfUnits > 0).ToList();
            // Retirarse del campo solo si todo está perdido; si no, se queda en replegarse (tus órdenes, Alt+B, no se tocan)
            bool mayRetreat = playerOrder || Men(team) < RetreatRatio * EnemiesOf(team).Sum(Men);
            foreach (var o in orders.OfType<JObject>())
            {
                string fname = ((string)o["formation"] ?? "").Trim();
                var f = mine.FirstOrDefault(x => string.Equals(Name(x), fname, StringComparison.OrdinalIgnoreCase));
                if (f == null) continue;
                bool players = f.PlayerOwner != null && f.PlayerOwner == Agent.Main;
                if (players != playerOrder && !(playerOrder && team.IsPlayerGeneral)) continue;      // cada cual manda en lo suyo
                string move = ((string)o["move"] ?? "").Trim().ToLowerInvariant(), shape = ((string)o["shape"] ?? "").Trim().ToLowerInvariant(), fire = ((string)o["fire"] ?? "").Trim().ToLowerInvariant();
                if (move == "retreat" && !mayRetreat) { Log.Info($"[batalla] retirada de {Name(f)} rechazada: no están en clara inferioridad"); move = "fall_back"; }
                MovementOrder? mo = null;
                string label = move;
                if (move == "charge") mo = MovementOrder.MovementOrderCharge;
                else if (move == "advance") mo = MovementOrder.MovementOrderAdvance;
                else if (move == "fall_back") mo = MovementOrder.MovementOrderFallBack;
                else if (move == "retreat") mo = MovementOrder.MovementOrderRetreat;
                else if (move == "hold") { var wp = f.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache.None); mo = MovementOrder.MovementOrderMove(wp); }
                else if ((move == "flank_left" || move == "flank_right") && mainEnemy != null)
                {
                    var left = new Vec2(-front.y, front.x);
                    var target = mainEnemy.CurrentPosition + left * (move == "flank_left" ? 55f : -55f);
                    var wp = mainEnemy.CreateNewOrderWorldPosition(WorldPosition.WorldPositionEnforcedCache.None);
                    wp.SetVec2(target);
                    mo = MovementOrder.MovementOrderMove(wp);
                }
                else if (move.StartsWith("attack:"))
                {
                    string en = move.Substring(7).Trim();
                    int slash = en.IndexOf('/');
                    if (slash > 0) en = en.Substring(0, slash);
                    var pos = f.CurrentPosition;
                    var target = theirs.Where(x => string.Equals(Name(x), en, StringComparison.OrdinalIgnoreCase)).OrderBy(x => x.CurrentPosition.Distance(pos)).FirstOrDefault();
                    if (target != null) mo = MovementOrder.MovementOrderChargeToTarget(target);      // un objetivo que no existe se rechaza
                }
                if (mo == null && shape.Length == 0 && fire.Length == 0) continue;

                if (!playerOrder && f.IsAIControlled) { f.SetControlledByAI(false); side.Held.Add(f); }
                if (mo != null) f.SetMovementOrder(mo.Value);
                switch (shape)
                {
                    case "line": f.SetArrangementOrder(ArrangementOrder.ArrangementOrderLine); break;
                    case "shield_wall": f.SetArrangementOrder(ArrangementOrder.ArrangementOrderShieldWall); break;
                    case "loose": f.SetArrangementOrder(ArrangementOrder.ArrangementOrderLoose); break;
                    case "square": f.SetArrangementOrder(ArrangementOrder.ArrangementOrderSquare); break;
                    case "circle": f.SetArrangementOrder(ArrangementOrder.ArrangementOrderCircle); break;
                    case "scatter": f.SetArrangementOrder(ArrangementOrder.ArrangementOrderScatter); break;
                    default: shape = ""; break;
                }
                if (fire == "at_will") f.SetFiringOrder(FiringOrder.FiringOrderFireAtWill);
                else if (fire == "hold") f.SetFiringOrder(FiringOrder.FiringOrderHoldYourFire);
                else fire = "";
                label = (mo != null ? label : "") + (shape != "" ? " " + shape : "") + (fire != "" ? " fire:" + fire : "");
                if (side != null) side.Doing[f] = label.Trim();
                shown?.Append($"{Name(f)}: {label.Trim()}; ");
                done++;
            }
            return done;
        }

        private static void Release(Side s)
        {
            foreach (var f in s.Held) { try { if (f.CountOfUnits > 0 && !f.IsAIControlled && f.PlayerOwner == null) f.SetControlledByAI(true); } catch { } }
            s.Held.Clear(); s.Doing.Clear();
        }

        // ============================================================ decisión de un jefe

        private static void Decide(Side s, float now)
        {
            var cfg = S.Get;
            var team = s.Team;
            Snapshot(team, s, out string own, out string foe, out var centre, out var front, out var mainEnemy);
            if (mainEnemy == null) return;
            var general = team.GeneralAgent != null && team.GeneralAgent.IsActive() ? team.GeneralAgent : null;
            string commander = general?.Name ?? (team.IsPlayerAlly ? "the allied commander" : "the enemy commander");
            var enemies = EnemiesOf(team).ToList();
            string enemyName = enemies.Any(e => e.IsPlayerTeam || e.IsPlayerAlly) ? $"the host of {Hero.MainHero.Name}" + (Hero.MainHero.MapFaction != null ? $" ({Hero.MainHero.MapFaction.Name})" : "")
                             : enemies.Select(e => e.GeneralAgent?.Name).FirstOrDefault(x => x != null) ?? "the enemy host";
            int ownMen = Men(team), foeMen = enemies.Sum(Men);
            int foeStart = Sides.Where(x => enemies.Contains(x.Team)).Sum(x => x.StartMen);
            if (foeStart < foeMen) foeStart = foeMen;
            string prompt = TCommander.Format("commander", commander, "enemy", enemyName, "temper", Temper(general), "time", $"{(int)(now / 60f)} min {(int)(now % 60f)} s",
                "own_total", ownMen, "own_start", Math.Max(s.StartMen, ownMen), "enemy_total", foeMen, "enemy_start", foeStart, "own", own, "foe", foe,
                "history", s.History.Count == 0 ? "(none yet: this is your first order)" : string.Join(" → ", s.History), "language", cfg.Language);

            s.Waiting = true; _requests++;
            int gen = ++s.Gen;
            var m = _mission;
            s.MenAtLastDecision = ownMen;
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "battle", maxTokensOverride: 500, preferredProvider: cfg.ProviderBattle).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    _requests = Math.Max(0, _requests - 1);
                    s.Waiting = false;
                    try
                    {
                        if (Mission.Current != m || _mission != m || gen != s.Gen || !BattleOn(m)) return;
                        if (res == null || !res.Ok) { Log.Warn("[batalla] sin órdenes de la IA: " + res?.Error); return; }
                        if (m.CurrentTime - now > MaxLateSeconds) { Log.Info($"[batalla] respuesta tardía ({(int)(m.CurrentTime - now)} s), descartada"); return; }
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a < 0 || b <= a) return;
                        var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                        // La foto es de hace unos segundos: se vuelve a tomar la posición para los flancos
                        Snapshot(team, s, out _, out _, out var c2, out var f2, out var me2);
                        var shown = new StringBuilder();
                        int done = Apply(team, s, j["orders"] as JArray, c2, f2, me2, false, shown);
                        s.LastOkAt = m.CurrentTime;
                        string plan = ((string)j["plan"] ?? "").Trim(), shout = ((string)j["shout"] ?? "").Trim();
                        if (plan.Length > 3) { s.History.Add(plan); while (s.History.Count > 4) s.History.RemoveAt(0); }
                        var color = team.IsPlayerAlly || team.IsPlayerTeam ? new Color(0.55f, 0.9f, 0.55f) : new Color(1f, 0.55f, 0.45f);
                        if (shout.Length > 1 && cfg.BattleAIShouts)
                        {
                            Say($"{commander}: {shout}", color);
                            try { if (general != null && general.IsActive()) Bubbles.Show(general, shout, 5f); } catch { }
                        }
                        if (cfg.BattleAIShowOrders && done > 0) Say($"[{commander}] {shown.ToString().TrimEnd(' ', ';')}", new Color(0.7f, 0.7f, 0.7f));
                        Log.Info($"[batalla] {commander} ({ownMen} contra {foeMen}): {shown}— {plan}");
                    }
                    catch (Exception ex) { Log.Error("Órdenes de batalla", ex); }
                });
            });
        }

        // ============================================================ órdenes del jugador (Alt+B)

        private static void PlayerOrder()
        {
            var cfg = S.Get;
            if (!Config.LegacyConfig.Current.Providers.Any(p => p.IsUsable)) { Say("AI Legacy: no hay ninguna api_key configurada.", Colors.Red); return; }
            InformationManager.ShowTextInquiry(new TextInquiryData(L.T("AIL_56a85a72", "Órdenes a tus tropas"), L.T("AIL_33dbf353", "Escríbelas con tus palabras: «infantería en muro de escudos y aguantad», «la caballería por la izquierda contra sus arqueros», «todos a la carga»…"),
                true, true, "Ordenar", "Cancelar", text =>
                {
                    text = (text ?? "").Trim();
                    var m = Mission.Current;
                    if (text.Length == 0 || !BattleOn(m) || _playerWaiting) return;
                    var team = m.PlayerTeam;
                    Snapshot(team, null, out string own, out string foe, out _, out _, out _);
                    string prompt = TPlayerOrder.Format("own", own, "foe", foe, "said", text.Replace("«", "\"").Replace("»", "\""), "language", cfg.Language);
                    _playerWaiting = true;
                    Say(L.F("AIL_2bd4d43b", $"Ordenas: «{text}»"), new Color(0.55f, 0.9f, 0.55f));
                    try { Bubbles.Show(Agent.Main, text, 4f); } catch { }
                    LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "order", maxTokensOverride: 400, preferredProvider: cfg.ProviderBattle).ContinueWith(t =>
                    {
                        var res = t.IsFaulted ? null : t.Result;
                        MainThread.Post(() =>
                        {
                            _playerWaiting = false;
                            try
                            {
                                if (Mission.Current != m || !BattleOn(m)) return;
                                if (res == null || !res.Ok) { Say(L.T("AIL_ab14812d", "Tus hombres no han entendido la orden (la IA no ha respondido)."), Colors.Red); return; }
                                int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                                if (a < 0 || b <= a) return;
                                var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                                Snapshot(team, null, out _, out _, out var c2, out var f2, out var me2);
                                var shown = new StringBuilder();
                                int done = Apply(team, null, j["orders"] as JArray, c2, f2, me2, true, shown);
                                string understood = ((string)j["understood"] ?? "").Trim();
                                Say(done > 0 ? L.F("AIL_6c53dc0f", $"Tus capitanes: {(understood.Length > 2 ? understood : shown.ToString())}") : L.T("AIL_7237f719", "Tus capitanes no saben qué hacer con esa orden."), done > 0 ? new Color(0.55f, 0.9f, 0.55f) : Colors.Gray);
                                Log.Info($"[batalla] orden del jugador «{text}» → {shown}");
                            }
                            catch (Exception ex) { Log.Error("Orden del jugador en batalla", ex); }
                        });
                    });
                }, null, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_ca0dcdf4", "Escribe una orden"))), true);
        }

        // ============================================================ tick

        public static void Tick(float dt)
        {
            try
            {
                var cfg = S.Get;
                var m = Mission.Current;
                if (!(cfg.EnableBattleAI || cfg.BattleAITextOrders) || !BattleOn(m))
                {
                    if (_mission != null) { _mission = null; Sides.Clear(); _requests = 0; _playerWaiting = false; }
                    return;
                }
                if (_mission != m)
                {
                    _mission = m; Sides.Clear(); _requests = 0; _playerWaiting = false;
                    Log.Info(cfg.EnableBattleAI ? "[batalla] empieza una batalla campal: IA de táctica activa" : "[batalla] empieza una batalla campal: solo órdenes con tus palabras");
                }
                float now = m.CurrentTime;

                // Alt+B: tus órdenes con tus palabras
                if (cfg.BattleAITextOrders && (Input.IsKeyDown(InputKey.LeftAlt) || Input.IsKeyDown(InputKey.RightAlt)) && Input.IsKeyPressed(InputKey.B) && Agent.Main.IsActive() && !_playerWaiting)
                    PlayerOrder();

                if (!cfg.EnableBattleAI) return;        // la IA de los jefes, aparte
                foreach (var team in m.Teams)
                {
                    if (team == null || team.Side == BattleSideEnum.None || team.ActiveAgents.Count == 0) continue;
                    bool friendly = team.IsPlayerTeam || team.IsPlayerAlly;
                    if (team.IsPlayerTeam && team.IsPlayerGeneral) continue;                 // ahí mandas tú
                    if (friendly ? !cfg.BattleAIAllies : !cfg.BattleAIEnemy) continue;
                    var s = Sides.FirstOrDefault(x => x.Team == team);
                    if (s == null)
                    {
                        s = new Side { Team = team, StartMen = Men(team), NextAt = now + 6f + Sides.Count * 4f };
                        s.MenAtLastDecision = s.StartMen;
                        Sides.Add(s);
                    }
                    int men = Men(team);
                    if (men > s.StartMen) s.StartMen = men;      // refuerzos
                    if (men == 0) continue;

                    // Si la IA lleva mucho sin dar órdenes válidas, las formaciones vuelven a las tácticas del juego (con intervalos largos, no más de 45 s de margen)
                    if (s.Held.Count > 0 && s.LastOkAt >= 0f && now - s.LastOkAt > Math.Min(cfg.BattleAIInterval * 2.5f, cfg.BattleAIInterval + 45f)) { Release(s); Log.Info("[batalla] sin decisiones recientes: mando devuelto a las tácticas del juego"); }

                    if (s.Waiting || _requests >= 2) continue;
                    bool heavyLosses = s.MenAtLastDecision > 0 && men < s.MenAtLastDecision * 0.82f && now >= s.NextAt - cfg.BattleAIInterval + 12f;
                    if (now < s.NextAt && !heavyLosses) continue;
                    s.NextAt = now + cfg.BattleAIInterval;
                    Decide(s, now);
                }
            }
            catch (Exception ex) { Log.Error("IA de batalla", ex); _mission = null; Sides.Clear(); }
        }
    }
}
