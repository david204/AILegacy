using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.UI;
using AILegacy.World;
using Newtonsoft.Json.Linq;
using SandBox.Missions.MissionLogics;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Vida en las escenas: de vez en cuando, dos personajes que están cerca de ti hablan entre ellos de lo que pasa
    /// (noticias que conocen, la escasez, la enfermedad, sus señores), con el texto sobre la cabeza. A veces, alguien
    /// que te conoce te suelta una frase al pasar. Lo que han dicho lo recuerdan si luego hablas con ellos.
    /// </summary>
    internal static class Ambient
    {
        private class Line { public Agent Who; public Agent To; public string Text; public bool Quiet; public string Mood; }
        private static Agent _speaking;

        private static readonly Random Rng = new Random();
        private static readonly Queue<Line> Play = new Queue<Line>();
        private static Agent _looking;
        private static float _timer = 20f, _lineLeft;
        private static bool _busy;
        private static bool _warned;
        // Quien va andando hasta el otro para hablarle
        private static Agent _walker, _walkTo;
        private static float _walkLeft;
        private static bool _arrived = true;
        private static Mission _mission;

        private static readonly Tpl TAmbient = new Tpl("scene_conversations/ambient.txt",
@"You are writing a scene for a story set in Calradia, the medieval world of Mount & Blade II: Bannerlord. Two people in {place} fall into conversation; someone standing nearby overhears it. Write that conversation so that it sounds like two real people who share a life and a history, not like two speeches.

A: {a}
   On A's mind: {a_mind}
B: {b}
   On B's mind: {b_mind}
What they are to each other: {tie}
Who is within earshot: {onlooker}
Their world right now (background they both already know; they do NOT explain it to each other):
{context}
People they both know and may mention: {others}
Earlier talks between these two (do not repeat them; you may pick one up where it was left): {before}

What this conversation is about: {angle}

How to write it:
- ONE subject, followed through. Every line answers the line before it: a question gets an answer, a jab gets a retort, a worry gets comfort or dismissal. Nobody changes the subject to deliver news.
- People who know each other speak in shorthand. They never state what both know (their own clan, their town, who rules, that there is a war): they refer to it sideways (""your brother"", ""that business at the border"", ""since the rents came in"").
- Small and concrete beats grand: a name, a sum of coin, a sore knee, a letter that has not come, a horse that needs shoeing. No proclamations, no proverbs, no oaths to the gods to open a line, no flourishes about blood, honour or glory.
- Give them different voices, from their character and station above: one may be dry, the other anxious; one talks much, the other in three words. Vary the length of lines; some are very short (""Again?"", ""And you believed him."").
- Let something happen between them: one wants something from the other, or hides something, or teases, or gives way. End when it would really end (a decision, a shrug, a laugh, one of them cutting it short), not with a summary.
- They speak to each other only. If someone is within earshot they may lower their voice or glance at them, but they do not address them.
- {min_lines} to {max_lines} lines, alternating speakers as real talk does (the same person may speak twice in a row once), each under {max_chars} characters, in {language}, with the natural spoken phrasing of that language. Spoken words only; at most one brief *gesture* in the whole scene. No names of people who are not in the data. Nothing about a ""player"", a game or an AI.
Also say how the talk leaves them: ""after"": { ""a"": how A now feels about B compared with before, ""b"": how B now feels about A } — each a whole number from -2 (much worse: hurt, insulted, betrayed) through 0 (unchanged, the usual case) to 2 (much better: grateful, reconciled, impressed).
Each line also carries ""mood"": how the speaker looks while saying it, one of: neutral, pleased, delighted, amused, mocking, unsure, worried, annoyed, stern, angry, shocked, dismissive.
Answer ONLY with JSON: { ""lines"": [ { ""who"": ""A"", ""text"": ""..."", ""mood"": ""..."" }, { ""who"": ""B"", ""text"": ""..."", ""mood"": ""..."" } ], ""topic"": ""what they talked about and how it ended, ONE English sentence"" }");

        private static readonly Tpl TCallOut = new Tpl("scene_conversations/call_out_to_player.txt",
@"{a_name} is in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord, and sees {player} walk by a few steps away. They know each other.
Who {a_name} is: {a}
What {a_name} remembers of them: {memory}
Write the ONE short thing {a_name} calls out to them in passing (under {max_chars} characters, in {language}): a greeting, a jab, a reminder, a warning, a plea — whatever fits what lies between them. It is not the start of a long talk.
Answer ONLY with JSON: { ""lines"": [ { ""who"": ""A"", ""text"": ""..."", ""mood"": ""one of: neutral, pleased, delighted, amused, mocking, unsure, worried, annoyed, stern, angry, shocked, dismissive"" } ], ""topic"": ""what was called out, ONE English sentence"" }");

        private static readonly Tpl TErrandAngle = new Tpl("scene_conversations/errand_subject.txt",
@"{a} has just walked over to find {b}, because {player} told {a} this a moment ago: «{said}». {a} raises it at once, in their own words. {b} answers from what {b} really knows: if {b} never asked for {a} or knows nothing of it, {b} says so, and the two of them wonder what that person is playing at.");

        private static readonly Tpl TErrandReport = new Tpl("scene_conversations/errand_report.txt",
@"Afterwards {a} walks back to {player}. Add one more field to the JSON: ""report"": the ONE line {a} says to {player} on returning (under {max_chars} characters, in {language}) — what came of it, in {a}'s own voice and mood (annoyed if it was a lie or a waste of time).");

        public static void Touch() { var _ = TAmbient.Key + TCallOut.Key + TErrandAngle.Key + TErrandReport.Key; }

        /// <summary>Un recado dentro de la escena: alguien va a buscar a otro, hablan, y (si toca) vuelve a contártelo.</summary>
        private class Errand { public Agent Who, Target; public string Said, Report; public bool Back; public int Stage; public float Left; }
        private static Errand _errand;
        private static int _gen;       // cambia al cancelar: la respuesta de la IA que llegue tarde se descarta

        internal static bool ErrandBusy => _errand != null;
        /// <summary>¿Hay frases saliendo ahora mismo sobre las cabezas?</summary>
        internal static bool IsPlaying => Play.Count > 0 || _lineLeft > 0f;

        /// <summary>En cuanto terminen las frases en curso, ese personaje sale a buscar al otro.</summary>
        internal static void SendTo(Agent who, Agent target, string said, bool back)
        {
            if (who == null || target == null || _errand != null) return;
            _errand = new Errand { Who = who, Target = target, Said = said ?? "", Back = back, Stage = 0 };
        }

        private static void DropErrand(string why)
        {
            var e = _errand; _errand = null;
            if (e == null) return;
            SceneMove.Release(e.Who);
            if (why != null) Log.Info("[escena] recado cancelado: " + why);
        }

        private static void BeginErrand(Settlement st)
        {
            var e = _errand; var cfg = S.Get;
            if (e.Who == null || !e.Who.IsActive() || e.Target == null || !e.Target.IsActive()) { DropErrand(L.T("AIL_723bcca4", "ya no están")); return; }
            string da = Describe(e.Who, out var sa), db = Describe(e.Target, out var sb);
            StartWalk(e.Who, e.Target, 120f);
            if (_walker == null) { DropErrand(L.F("AIL_98b23f3f", $"{sa.Name} no puede moverse ahora")); return; }
            e.Stage = 1;
            string pairKey = string.CompareOrdinal(sa.Key, sb.Key) < 0 ? sa.Key + "|" + sb.Key : sb.Key + "|" + sa.Key;
            string player = ContextBuilder.PlayerNameFor(sa) ?? "a traveller they do not know";
            string angle = TErrandAngle.Format("a", sa.Name, "b", sb.Name, "player", player, "said", e.Said.Replace("«", "\"").Replace("»", "\""));
            var others = SharedPeople(st, sa, sb);
            string before = PairTopics.TryGetValue(pairKey, out var past) && past.Count > 0 ? string.Join(" | ", past.Skip(Math.Max(0, past.Count - 3))) : "(none yet today)";
            string prompt = TAmbient.Format("place", st.Name, "a", da, "b", db, "a_mind", Mind(sa, e.Who), "b_mind", Mind(sb, e.Target), "tie", Tie(sa, sb), "onlooker", "nobody in particular",
                                "context", Context(st, sa), "others", others.Count > 0 ? string.Join(", ", others) : "(nobody in particular)", "before", before, "angle", angle,
                                "min_lines", 3, "max_lines", 6, "max_chars", cfg.AmbientMaxChars, "language", cfg.Language)
                            + (e.Back ? "\n" + TErrandReport.Format("a", sa.Name, "player", player, "max_chars", cfg.AmbientMaxChars, "language", cfg.Language) : "");
            Log.Info($"[escena] recado: {sa.Name} va a buscar a {sb.Name} (a {e.Who.Position.Distance(e.Target.Position):0} m){(e.Back ? " y volverá a contarlo" : "")}");
            _lastPair = pairKey; _lastPairAt = DateTime.UtcNow;
            Ask(Mission.Current, prompt, e.Who, e.Target, sa, sb, 6, 900, pairKey, e);
        }

        private static void BeginReturn()
        {
            var e = _errand;
            if (!e.Back || e.Who == null || !e.Who.IsActive() || Agent.Main == null || !SceneMove.GoTo(e.Who, Agent.Main)) { DropErrand(null); return; }
            e.Stage = 2; e.Left = 120f;
        }

        private static void TickReturn(float dt)
        {
            var e = _errand;
            e.Left -= dt;
            if (e.Who == null || !e.Who.IsActive() || Agent.Main == null || e.Left <= 0f) { DropErrand("no ha podido volver"); return; }
            if (e.Who.Position.Distance(Agent.Main.Position) > 3.5f) return;
            SceneMove.Release(e.Who);
            _errand = null;
            if (string.IsNullOrWhiteSpace(e.Report)) return;
            try
            {
                float secs = Math.Max(3f, Math.Min(9f, 1.5f + e.Report.Length * 0.06f));
                Bubbles.Show(e.Who, e.Report, secs);
                InformationManager.DisplayMessage(new InformationMessage($"{e.Who.Name}: {e.Report}", Colors.Yellow));
                try { e.Who.SetLookAgent(Agent.Main); _looking = e.Who; _lineLeft = secs; } catch { }
                Describe(e.Who, out var sp);
                if (sp.IsHero) { sp.Memory.Turns.Add(new ChatTurn { Role = "npc", Text = "[On coming back to them] " + e.Report, Day = CampaignTime.Now.ToDays }); SaveFiles.Flush(); }
                GroupTalk.Note($"{sp.Name}: {e.Report}");
            }
            catch (Exception ex) { Log.Error("Volver a contarlo", ex); }
        }

        /// <summary>Soldados, guardias y matones: están de servicio, no se ponen a charlar por su cuenta.</summary>
        private static bool OnDuty(Agent a)
        {
            var c = a.Character as CharacterObject;
            if (c == null || c.IsHero) return false;
            switch (c.Occupation)
            {
                case Occupation.Soldier: case Occupation.Guard: case Occupation.PrisonGuard: case Occupation.Mercenary: case Occupation.CaravanGuard:
                case Occupation.BannerBearer: case Occupation.Bandit: case Occupation.Gangster: return true;
            }
            return c.IsSoldier;
        }

        private static bool Named(Agent a) => a.Character is CharacterObject c && c.IsHero && c.HeroObject != null;

        // De qué han hablado ya estos dos, y por dónde les ha dado (para no repetirse)
        private static readonly Dictionary<string, List<string>> PairTopics = new Dictionary<string, List<string>>();
        private static readonly Dictionary<string, List<string>> PairAngles = new Dictionary<string, List<string>>();
        private static string _lastPair = "";
        private static DateTime _lastPairAt = DateTime.MinValue;
        private static bool _ambientPlaying;
        private static readonly HashSet<string> CalledOut = new HashSet<string>();
        private static Mission _calledMission;

        private static string Feeling(int r)
        {
            if (r <= -50) return "they detest each other";
            if (r <= -20) return "there is bad blood between them";
            if (r < -5) return "they are cool with each other";
            if (r <= 5) return "neither warm nor cold";
            if (r < 20) return "they get on well enough";
            if (r < 50) return "they like and trust each other";
            return "they are very close";
        }

        /// <summary>Qué son el uno del otro: sin esto, hablan como dos desconocidos.</summary>
        private static string Tie(Speaker a, Speaker b)
        {
            try
            {
                if (a.IsHero && b.IsHero)
                {
                    Hero x = a.Hero, y = b.Hero;
                    var parts = new List<string>();
                    if (x.Spouse == y) parts.Add("husband and wife");
                    else if (y.Father == x || y.Mother == x) parts.Add($"{x.Name} is {y.Name}'s {(x.IsFemale ? "mother" : "father")}");
                    else if (x.Father == y || x.Mother == y) parts.Add($"{y.Name} is {x.Name}'s {(y.IsFemale ? "mother" : "father")}");
                    else if (x.Siblings.Contains(y)) parts.Add(x.IsFemale == y.IsFemale ? (x.IsFemale ? "sisters" : "brothers") : "brother and sister");
                    else if (x.Clan != null && x.Clan == y.Clan) parts.Add($"kin of the same house ({x.Clan.Name}), living under the same roof");
                    else if (x.MapFaction != null && x.MapFaction == y.MapFaction) parts.Add($"of the same realm but different houses ({x.Clan?.Name} and {y.Clan?.Name}): peers, sometimes rivals");
                    else if (x.MapFaction != null && y.MapFaction != null && x.MapFaction.IsAtWarWith(y.MapFaction)) parts.Add("their realms are at war with each other: guarded, formal");
                    else parts.Add("of different realms: courteous, not intimate");
                    if (x.Clan != null && x.Clan == y.Clan)
                    {
                        if (x.Clan.Leader == x) parts.Add($"{x.Name} heads the house");
                        else if (x.Clan.Leader == y) parts.Add($"{y.Name} heads the house");
                    }
                    if (Math.Abs(x.Age - y.Age) >= 15) parts.Add($"{(x.Age > y.Age ? x.Name : y.Name)} is much the elder");
                    parts.Add(Feeling(x.GetRelation(y)));
                    string own = Bonds.Between(a, b);
                    if (own.Length > 0) return string.Join("; ", parts) + "." + own;
                    return string.Join("; ", parts) + ".";
                }
                if (a.IsHero || b.IsHero)
                {
                    var hi = a.IsHero ? a : b; var lo = a.IsHero ? b : a;
                    return $"{hi.Name} is a person of standing here and the other ({(lo == a ? "A" : "B")}) is common folk who serves or depends on such people: respectful and careful, speaks when it is useful, while {hi.Name} is brief and at ease.";
                }
                return "Common folk of the same place who see each other most days: familiar, blunt, no ceremony.";
            }
            catch { return "They know each other."; }
        }

        /// <summary>Lo que cada uno tiene en la cabeza hoy, sacado de su situación real.</summary>
        private static string Mind(Speaker s, Agent ag)
        {
            var parts = new List<string>();
            try
            {
                if (s.IsHero)
                {
                    var h = s.Hero;
                    if (h.Clan != null)
                    {
                        if (h.Clan.Leader == h) parts.Add($"carries the house on their shoulders ({h.Clan.Fiefs.Count} holding(s), {(h.Clan.Gold < 15000 ? "coin is tight" : h.Clan.Gold > 200000 ? "the coffers are full" : "the accounts hold")})");
                        var away = h.Clan.Heroes.Where(o => o != h && o.IsAlive && !o.IsChild && o.CurrentSettlement != h.CurrentSettlement && (o.PartyBelongedTo != null || o.IsPrisoner)).Take(2)
                                    .Select(o => o.IsPrisoner ? $"{o.Name} is a prisoner" : $"{o.Name} is away in the field").ToList();
                        if (away.Count > 0) parts.Add("of the family, " + string.Join(" and ", away));
                    }
                    int kids = h.Children.Count(c => c.IsAlive && c.IsChild);
                    if (kids > 0) parts.Add($"{kids} young child(ren) to raise");
                    if (h.Spouse == null && h.Age >= 18 && h.Age < 40) parts.Add("unmarried, and the family notices");
                    if (h.IsWounded) parts.Add("still recovering from a wound");
                    if (h.Age >= 55) parts.Add("feels the years");
                    var heardP = InfoExchange.AboutPlayer(s.Memory).ToList();
                    foreach (var hd in heardP.Skip(Math.Max(0, heardP.Count - 2)))
                        if (!string.IsNullOrWhiteSpace(hd.Text)) parts.Add($"was told by {hd.FromName}: {hd.Text}");
                }
                else
                {
                    if (ag != null && ag.Health < ag.HealthLimit * 0.6f) parts.Add("hurt and sore");
                    parts.Add("their day's work, what it pays, and who is watching");
                }
            }
            catch { }
            return parts.Count == 0 ? "nothing pressing; an ordinary day" : string.Join("; ", parts) + ".";
        }

        /// <summary>Gente que los dos conocen y de la que pueden hablar.</summary>
        private static List<string> SharedPeople(Settlement st, Speaker a, Speaker b)
        {
            var list = new List<string>();
            try
            {
                void Add(Hero h, string what)
                {
                    if (h == null || !h.IsAlive || h == Hero.MainHero || (a.IsHero && a.Hero == h) || (b.IsHero && b.Hero == h)) return;
                    string n = h.Name.ToString();
                    if (list.Any(x => x.StartsWith(n + " ("))) return;
                    list.Add($"{n} ({what})");
                }
                foreach (var s in new[] { a, b })
                    if (s.IsHero && s.Hero.Clan != null)
                        foreach (var k in s.Hero.Clan.Heroes.Where(k => k.IsAlive && !k.IsChild).Take(4))
                            Add(k, k == s.Hero.Spouse ? $"{s.Name}'s spouse" : k == s.Hero.Father ? $"{s.Name}'s father" : k == s.Hero.Mother ? $"{s.Name}'s mother"
                                   : s.Hero.Children.Contains(k) ? $"{s.Name}'s {(k.IsFemale ? "daughter" : "son")}" : s.Hero.Siblings.Contains(k) ? $"{s.Name}'s {(k.IsFemale ? "sister" : "brother")}"
                                   : $"of house {s.Hero.Clan.Name}");
                Add(st.OwnerClan?.Leader, $"lord of {st.Name}");
                if (st.MapFaction is Kingdom kd) Add(kd.Leader, $"ruler of {kd.Name}");
                foreach (var n in st.Notables.Take(2)) Add(n, $"a notable of {st.Name}");
            }
            catch { }
            return list.Take(7).ToList();
        }

        /// <summary>Por dónde les da hoy: se elige aquí para que no acaben siempre en el tiempo y la guerra.</summary>
        private static string PickAngle(Settlement st, Speaker a, Speaker b, string pairKey, List<string> others, string playerKnown)
        {
            var opts = new List<string>
            {
                "a practical matter one of them wants settled today (coin owed, rents, a servant, supplies, a journey to arrange) and the other is slow to agree",
                "one of them is in a mood (tired, irritated, amused, uneasy) and the other notices and will not let it pass",
                "an old shared memory, brought up by something small, that they do not remember the same way",
                "a standing disagreement between them that flares up again, half in jest",
                "where each of them means to be in the coming weeks, and whether the other approves",
                "the small discomforts of the day (sleep, food, a draught, an ache, the noise) turning into something more personal",
                "one of them asks the other for advice or a favour and is embarrassed to ask",
            };
            try
            {
                if (others.Count > 0)
                {
                    string who = others[Rng.Next(others.Count)];
                    opts.Add($"gossip about {who}: something that person did or is said to have done, and what each of them thinks of it");
                    opts.Add($"a worry about {others[Rng.Next(others.Count)]}, which one of them takes more seriously than the other");
                }
                var news = NewsStore.KnownBy(a, 4);
                if (news.Count > 0) opts.Add("how this piece of news touches them personally (what it will cost them, whom they know in it): " + NewsStore.Describe(news[Rng.Next(news.Count)]));
                var wars = WorldSim.Kingdoms.Where(k => st.MapFaction != null && k != st.MapFaction && st.MapFaction.IsAtWarWith(k)).Select(k => k.Name.ToString()).ToList();
                if (wars.Count > 0) opts.Add($"what the war with {wars[Rng.Next(wars.Count)]} is doing to their own household (a kinsman in the field, the cost, a road no longer safe), not the war itself");
                if (playerKnown != null) opts.Add($"{playerKnown}, who is standing close by: in lowered voices, what each of them makes of that person");
                if (!a.IsHero && !b.IsHero) { opts.Add("prices, wages and who is cheating whom at the market"); opts.Add("something one of them saw last night and should perhaps not have seen"); }
            }
            catch { }
            if (!PairAngles.TryGetValue(pairKey, out var used)) PairAngles[pairKey] = used = new List<string>();
            var fresh = opts.Where(o => !used.Contains(o)).ToList();
            if (fresh.Count == 0) { used.Clear(); fresh = opts; }
            string pick = fresh[Rng.Next(fresh.Count)];
            used.Add(pick);
            return pick;
        }

        private static bool SceneOk(out Settlement st)
        {
            st = null;
            try
            {
                var m = Mission.Current;
                if (m == null || Campaign.Current == null || Agent.Main == null || !Agent.Main.IsActive()) return false;
                st = Settlement.CurrentSettlement;
                if (st == null) return false;                                   // solo en asentamientos: ni batallas ni emboscadas
                if (m.Mode == MissionMode.Battle || m.Mode == MissionMode.Duel || m.Mode == MissionMode.Stealth || m.Mode == MissionMode.Deployment) return false;
                var fh = m.GetMissionBehavior<MissionFightHandler>();
                return fh == null || !fh.IsThereActiveFight();
            }
            catch { return false; }
        }

        internal static string Describe(Agent a, out Speaker s)
        {
            var c = (CharacterObject)a.Character;
            if (c.IsHero && c.HeroObject != null)
            {
                s = Speaker.FromHero(c.HeroObject);
                var h = c.HeroObject;
                var p = s.Memory.Profile;
                return $"{h.Name}, {(h.IsFemale ? "woman" : "man")}, {(int)h.Age}, {h.Culture?.Name}; " +
                       (h.IsLord ? $"noble of clan {h.Clan?.Name}" : h.IsNotable ? "a notable of this place" : h.IsWanderer ? "a wanderer" : "a known face here") +
                       (p != null && !p.IsEmpty ? $". {p.Personality}" + (string.IsNullOrWhiteSpace(p.SpeechStyle) ? "" : $" Speaks: {p.SpeechStyle}") : "");
            }
            s = Speaker.FromResident(c, a);
            Lore.EnsureArchetype(s);
            int age = (int)c.Age;
            return $"{(c.IsFemale ? "a woman" : "a man")} of about {age / 5 * 5}, {c.Culture?.Name}, {c.Occupation.ToString().ToLowerInvariant()}" +
                   (string.IsNullOrEmpty(s.Memory.Archetype) ? "" : $" — {s.Memory.Archetype}: {s.Memory.ArchetypeText}");
        }

        internal static string Context(Settlement st, Speaker a)
        {
            var sb = new StringBuilder();
            var now = CampaignTime.Now;
            sb.AppendLine($"- Place: {st.Name}, {(st.IsTown ? "a town" : st.IsCastle ? "a castle" : "a village")} of {st.OwnerClan?.Leader?.Name} ({st.MapFaction?.Name}); {now.GetSeasonOfYear}, around {now.GetHourOfDay}:00.");
            if (st.Town != null)
                sb.AppendLine($"- Life here: {(st.Town.Prosperity < 2000 ? "poor" : st.Town.Prosperity > 6000 ? "thriving" : "ordinary")}; food {(st.Town.FoodStocks < 30 ? "is running short" : "is enough")}; " +
                              $"{(st.Town.Security < 35 ? "thieves are bold" : "the streets are safe enough")}; {(st.Town.Loyalty < 35 ? "people grumble against their lord" : "people accept their lord")}.");
            if (st.IsUnderSiege) sb.AppendLine("- The place is UNDER SIEGE.");
            try { var fx = WorldSim.DescribeEffectsAt(st); if (fx.Length > 0) sb.AppendLine("- " + fx); } catch { }
            try { var sick = Diseases.DescribeAt(st); if (sick.Length > 0) sb.AppendLine("- " + sick); } catch { }
            try
            {
                var wars = WorldSim.Kingdoms.Where(k => st.MapFaction != null && k != st.MapFaction && st.MapFaction.IsAtWarWith(k)).Select(k => k.Name.ToString()).ToList();
                if (wars.Count > 0) sb.AppendLine("- Their realm is at war with: " + string.Join(", ", wars) + ".");
                foreach (var n in NewsStore.KnownBy(a, 4)) sb.AppendLine("- They have heard: " + NewsStore.Describe(n));
            }
            catch { }
            return sb.ToString().TrimEnd();
        }

        private static void TryStart(Settlement st)
        {
            var cfg = S.Get;
            var m = Mission.Current;
            var me = Agent.Main.Position;
            var near = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject
                                           && a.Position.Distance(me) <= cfg.AmbientRadius && !GroupTalk.Has(a) && !SceneActions.Busy(a) && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main))).ToList();
            if (_calledMission != m) { _calledMission = m; CalledOut.Clear(); }
            if (near.Count == 0) { Log.Info($"[escena] nadie a menos de {cfg.AmbientRadius} m"); _timer = 20f; return; }

            // A veces, alguien que te conoce te dice algo al pasar
            if (cfg.AmbientCallOuts && Rng.Next(100) < 30)
            {
                var known = near.Where(a => ((CharacterObject)a.Character).IsHero && ((CharacterObject)a.Character).HeroObject != null
                                            && MemoryStore.Find(((CharacterObject)a.Character).HeroObject.StringId)?.ConversationCount > 0
                                            && !CalledOut.Contains(((CharacterObject)a.Character).HeroObject.StringId)
                                            && a.Position.Distance(me) <= 9f).OrderBy(_ => Rng.Next()).FirstOrDefault();
                if (known != null)
                {
                    // Cada uno te dice algo al pasar una sola vez por visita, y después hay un rato de silencio
                    CalledOut.Add(((CharacterObject)known.Character).HeroObject.StringId);
                    _timer = cfg.AmbientCooldownSeconds * 0.5f;
                    string desc = Describe(known, out var sp);
                    var mem = sp.Memory;
                    string memory = (string.IsNullOrWhiteSpace(mem.Summary) ? "" : mem.Summary + " ") + string.Join(" ", mem.Notes.Skip(Math.Max(0, mem.Notes.Count - 3)))
                                    + $" Opinion of them: {ContextBuilder.DescribeRelation((int)sp.Hero.GetRelationWithPlayer())}.";
                    Ask(m, TCallOut.Format("a_name", sp.Name, "a", desc, "place", st.Name, "player", ContextBuilder.PlayerNameFor(sp) ?? "them", "memory", memory,
                        "max_chars", cfg.AmbientMaxChars, "language", cfg.Language), known, null, sp, null);
                    return;
                }
            }

            // Quién charla por su cuenta: la gente con nombre (señores, damas, notables, errantes) siempre; la gente corriente
            // y la tropa, solo si está activado, y en corto.
            near = near.Where(a => Named(a) || (OnDuty(a) ? cfg.AmbientGuards : cfg.AmbientCommoners)).ToList();

            // La pareja: dos que estén lo bastante cerca el uno del otro. Se prefiere a los que tienes más a mano, y a los que tienen nombre.
            Agent first = null, second = null; float best = float.MaxValue;
            foreach (var x in near)
                foreach (var y in near)
                {
                    if (x == y || x.Index >= y.Index) continue;
                    float d = x.Position.Distance(y.Position);
                    if (d > cfg.AmbientPairDistance) continue;
                    float score = d + x.Position.Distance(me) * 0.5f + y.Position.Distance(me) * 0.5f + (float)Rng.NextDouble() * 6f
                                  + (Named(x) && Named(y) ? 0f : Named(x) || Named(y) ? 10f : 20f);
                    if (score < best) { best = score; first = x; second = y; }
                }
            if (first == null)
            {
                Log.Info($"[escena] sin pareja para charlar: {near.Count} personaje(s) a menos de {cfg.AmbientRadius} m, ninguno a menos de {cfg.AmbientPairDistance} m de otro");
                _timer = 20f;      // se reintenta pronto: la gente se mueve
                return;
            }
            if (Rng.Next(2) == 0) { var tmp = first; first = second; second = tmp; }
            string da = Describe(first, out var sa), db = Describe(second, out var sb2);
            string pairKey = string.CompareOrdinal(sa.Key, sb2.Key) < 0 ? sa.Key + "|" + sb2.Key : sb2.Key + "|" + sa.Key;
            // Los mismos dos no vuelven a la carga enseguida
            if (pairKey == _lastPair && (DateTime.UtcNow - _lastPairAt).TotalSeconds < cfg.AmbientCooldownSeconds * 2) { _timer = 30f; return; }
            float apart = first.Position.Distance(second.Position);
            if (cfg.AmbientWalk && apart > 3f) StartWalk(first, second);

            var others = SharedPeople(st, sa, sb2);
            string playerKnown = null, onlooker = "nobody in particular";
            if (Agent.Main.Position.Distance(first.Position) <= 8f || Agent.Main.Position.Distance(second.Position) <= 8f)
            {
                string na = ContextBuilder.PlayerNameFor(sa), nb = ContextBuilder.PlayerNameFor(sb2);
                playerKnown = na ?? nb;
                if (playerKnown == null) onlooker = "an armed traveller neither of them knows, standing a few steps away";
                else
                {
                    var bits = new List<string>();
                    foreach (var s in new[] { sa, sb2 })
                        if (s.IsHero && ContextBuilder.PlayerNameFor(s) != null)
                            bits.Add($"{s.Name}: {Feeling((int)s.Hero.GetRelationWithPlayer()).Replace("they ", "").Replace("each other", "them")}" + (string.IsNullOrWhiteSpace(s.Memory.Summary) ? "" : $" ({s.Memory.Summary})"));
                    onlooker = $"{playerKnown}, standing a few steps away" + (bits.Count > 0 ? ". What they make of that person — " + string.Join("; ", bits) : "");
                }
            }
            string before = PairTopics.TryGetValue(pairKey, out var past) && past.Count > 0 ? string.Join(" | ", past.Skip(Math.Max(0, past.Count - 3))) : "(none yet today)";
            string angle = PickAngle(st, sa, sb2, pairKey, others, playerKnown);
            // Solo la gente con nombre habla largo; con gente corriente o tropa por medio, dos o tres frases cortas
            bool full = sa.IsHero && sb2.IsHero;
            int maxLines = full ? Math.Max(2, cfg.AmbientMaxLines) : Math.Max(2, cfg.AmbientCommonerLines);
            int minLines = full ? Math.Min(maxLines, Math.Max(2, maxLines / 2)) : 2;
            int maxChars = full ? cfg.AmbientMaxChars : Math.Min(cfg.AmbientMaxChars, 90);
            Log.Info($"[escena] {sa.Name} y {sb2.Name} van a charlar (están a {apart:0.0} m{(_walker != null ? "; " + sa.Name + " va hacia allí" : "")}; tema: {angle})");
            _lastPair = pairKey; _lastPairAt = DateTime.UtcNow;
            // Por turnos: se pide de dos en dos frases y cada tanda sabe lo dicho antes. Más peticiones, y la
            // charla se corta sola si te metes por medio. Solo entre gente con nombre.
            if (cfg.AmbientTurnByTurn && full)
            {
                int step = Math.Max(1, cfg.AmbientTurnSize);
                string basePrompt = TAmbient.Format("place", st.Name, "a", da, "b", db, "a_mind", Mind(sa, first), "b_mind", Mind(sb2, second), "tie", Tie(sa, sb2), "onlooker", onlooker,
                    "context", Context(st, sa), "others", others.Count > 0 ? string.Join(", ", others) : "(nobody in particular)", "before", before, "angle", angle,
                    "min_lines", 1, "max_lines", step, "max_chars", maxChars, "language", cfg.Language);
                _chain = new Chain { Base = basePrompt, A = first, B = second, SA = sa, SB = sb2, Key = pairKey, Left = maxLines, Step = step };
                Ask(m, basePrompt + "\n" + TChainFirst.Format("n", step), first, second, sa, sb2, step, 200 + step * 110, pairKey, null, _chain);
                return;
            }
            Ask(m, TAmbient.Format("place", st.Name, "a", da, "b", db, "a_mind", Mind(sa, first), "b_mind", Mind(sb2, second), "tie", Tie(sa, sb2), "onlooker", onlooker,
                    "context", Context(st, sa), "others", others.Count > 0 ? string.Join(", ", others) : "(nobody in particular)", "before", before, "angle", angle,
                    "min_lines", minLines, "max_lines", maxLines, "max_chars", maxChars, "language", cfg.Language),
                first, second, sa, sb2, maxLines, 300 + maxLines * 90, pairKey);
        }

        // ---- charla por turnos
        private class Chain { public string Base, Key; public Agent A, B; public Speaker SA, SB; public int Left, Step; public readonly List<string> Said = new List<string>(); }
        private static Chain _chain;

        private static readonly Tpl TChainFirst = new Tpl("scene_conversations/turns_opening.txt",
@"This is only the OPENING of the conversation: write just its first {n} line(s). It will go on afterwards, so do not wrap it up. Add ""done"": false to the JSON.");

        private static readonly Tpl TChainNext = new Tpl("scene_conversations/turns_next.txt",
@"The conversation so far:
{said}
Write ONLY the next {n} line(s), continuing from the last one (whoever was just spoken to answers; one may cut the other off, ending a line with —). {closing}
Add ""done"": true to the JSON if the talk ends with these lines, otherwise ""done"": false.");

        private static void ContinueChain()
        {
            var c = _chain;
            if (c == null) return;
            if (Mission.Current != _mission || c.A == null || !c.A.IsActive() || c.B == null || !c.B.IsActive() || c.Left <= 0 || c.A.Position.Distance(c.B.Position) > 8f) { _chain = null; return; }
            int n = Math.Min(c.Step, c.Left);
            string closing = c.Left <= c.Step ? "These are the LAST lines: bring it to its natural end (a decision, a shrug, a laugh, one of them cutting it short)." : "Do not end it yet unless it would really end here.";
            Ask(_mission, c.Base + "\n" + TChainNext.Format("said", string.Join("\n", c.Said), "n", n, "closing", closing), c.A, c.B, c.SA, c.SB, n, 200 + n * 110, c.Key, null, c);
        }

        private static void Ask(Mission m, string prompt, Agent a, Agent b, Speaker sa, Speaker sb, int maxLines = 4, int tokens = 350, string pairKey = null, Errand errand = null, Chain chain = null)
        {
            _busy = true;
            int gen = _gen;
            string provider = (sa != null && sa.IsHero) || (sb != null && sb.IsHero) ? S.Get.ProviderHeroes : S.Get.ProviderResidents;
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "ambient", maxTokensOverride: tokens, preferredProvider: provider).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    _busy = false;
                    try
                    {
                        if (res == null || !res.Ok) { Log.Warn("[escena] la IA no ha dado la charla: " + res?.Error); if (chain != null && _chain == chain) _chain = null; return; }
                        if (Mission.Current != m || a == null || !a.IsActive() || gen != _gen) { if (chain != null && _chain == chain) _chain = null; return; }
                        int i0 = res.Text.IndexOf('{'), i1 = res.Text.LastIndexOf('}');
                        if (i0 < 0 || i1 <= i0) { if (chain != null && _chain == chain) _chain = null; return; }
                        var j = JObject.Parse(res.Text.Substring(i0, i1 - i0 + 1));
                        if (chain != null)
                        {
                            int got = 0;
                            if (j["lines"] is JArray cl)
                                foreach (var l in cl.OfType<JObject>().Take(maxLines))
                                {
                                    string tx = ((string)l["text"] ?? "").Trim();
                                    if (tx.Length < 2) continue;
                                    bool cb = ((string)l["who"] ?? "A").Trim().ToUpperInvariant() == "B";
                                    chain.Said.Add((cb ? "B" : "A") + " (" + (cb ? sb?.Name : sa?.Name) + "): " + tx);
                                    got++;
                                }
                            chain.Left -= Math.Max(1, got);
                            bool done = got == 0 || chain.Left <= 0 || (j["done"] != null && j["done"].Type == JTokenType.Boolean && (bool)j["done"]);
                            if (done && _chain == chain) _chain = null;
                        }
                        if (errand != null) errand.Report = ((string)j["report"] ?? "").Trim();
                        if (!(j["lines"] is JArray lines)) return;
                        int max = S.Get.AmbientMaxChars + 60;
                        foreach (var l in lines.OfType<JObject>().Take(maxLines))
                        {
                            string text = ((string)l["text"] ?? "").Trim();
                            if (text.Length < 2) continue;
                            if (text.Length > max) text = text.Substring(0, max);
                            bool isB = ((string)l["who"] ?? "A").Trim().ToUpperInvariant() == "B" && b != null;
                            Play.Enqueue(new Line { Who = isB ? b : a, To = isB ? a : (b ?? Agent.Main), Text = text, Mood = (string)l["mood"] });
                            // Lo hablado en un recado queda en lo dicho del grupo: quien fue, lo sabe al volver
                            if (errand != null && sa != null && sb != null) GroupTalk.Note($"{(isB ? sb.Name : sa.Name)} (to {(isB ? sa.Name : sb.Name)}, away from the group): {text}");
                        }
                        _mission = m;
                        if (b != null) _ambientPlaying = true;
                        string topic = ((string)j["topic"] ?? "").Trim();
                        if (topic.Length > 5 && pairKey != null && sa != null && sb != null)
                        {
                            // Lo recuerdan: no lo repiten, y quien sea alguien con nombre lo sabrá cuando hables con él
                            if (!PairTopics.TryGetValue(pairKey, out var past)) PairTopics[pairKey] = past = new List<string>();
                            past.Add(topic);
                            while (past.Count > 6) past.RemoveAt(0);
                            // Cómo quedan entre ellos tras la charla: lo recuerdan los dos, cada uno a su manera
                            try
                            {
                                int da = 0, db = 0;
                                if (j["after"] is JObject af)
                                {
                                    int.TryParse((af["a"] ?? "0").ToString().Trim(), out da); int.TryParse((af["b"] ?? "0").ToString().Trim(), out db);
                                    da = Math.Max(-2, Math.Min(2, da)); db = Math.Max(-2, Math.Min(2, db));
                                }
                                Bonds.TouchOther(sa, sb, da * 4, topic);
                                Bonds.TouchOther(sb, sa, db * 4, topic);
                            }
                            catch { }
                            // El tema ya queda en Others[...].Notes (TouchOther); no va a Heard, que es lo que saben del jugador
                        }
                        if (topic.Length > 5)
                        {
                            // Si luego hablas con ellos, saben de qué estaban hablando
                            if (sa != null) { sa.Memory.RecentTalk = b != null ? $"Moments ago you were talking with {(sb != null ? sb.Name : "someone")} nearby: {topic}" : $"Moments ago you called out to them as they passed: {topic}"; sa.Memory.RecentTalkAt = DateTime.UtcNow; }
                            if (sb != null) { sb.Memory.RecentTalk = $"Moments ago you were talking with {sa?.Name ?? "someone"} nearby: {topic}"; sb.Memory.RecentTalkAt = DateTime.UtcNow; }
                            Log.Info($"[escena] {(sa?.Name ?? "?")}{(sb != null ? " y " + sb.Name : " (al jugador)")}: {topic}");
                        }
                    }
                    catch (Exception ex) { Log.Error("Charla de ambiente", ex); }
                });
            });
        }

        /// <summary>Pone en cola frases para que salgan una a una sobre las cabezas (las usa la conversación de grupo).</summary>
        internal static void Enqueue(Mission m, List<(Agent who, Agent to, string text, string mood)> lines)
        {
            Play.Clear();
            _chain = null;      // si te metes tú, su charla privada se acaba
            _lineLeft = 0f;
            _mission = m;
            foreach (var l in lines) Play.Enqueue(new Line { Who = l.who, To = l.to, Text = l.text, Mood = l.mood, Quiet = true });   // el grupo ya lo escribe en el registro
        }

        private static readonly HashSet<string> OverheardBy = new HashSet<string>();

        /// <summary>
        /// Lo que le dices a alguien en una escena lo oyen los personajes con nombre que están a pocos pasos:
        /// les queda como algo que han oído (una vez por charla y oyente).
        /// </summary>
        public static void Overhear(Speaker speaker, string playerSaid)
        {
            if (speaker != null) Overhear(new[] { speaker }, playerSaid);
        }

        /// <summary>Lo mismo para una frase dicha a un corro: una sola entrada por oyente y nunca para los del corro.</summary>
        public static void Overhear(IList<Speaker> addressed, string playerSaid)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableOverhearing || Mission.Current == null || Agent.Main == null || string.IsNullOrWhiteSpace(playerSaid) || playerSaid.Length < 15) return;
                var to = (addressed ?? new Speaker[0]).Where(x => x != null).ToList();
                if (to.Count == 0) return;
                var inside = new HashSet<Hero>(to.Where(x => x.IsHero && x.Hero != null).Select(x => x.Hero));
                string key = string.Join(",", to.Select(x => x.Key));
                string names = to.Count == 1 ? to[0].Name : to.Count == 2 ? $"{to[0].Name} and {to[1].Name}" : $"the group ({string.Join(", ", to.Select(x => x.Name))})";
                string said = playerSaid.Length > 220 ? playerSaid.Substring(0, 220) + "…" : playerSaid;
                double now = CampaignTime.Now.ToDays;
                foreach (var a in Mission.Current.Agents)
                {
                    if (a == null || !a.IsHuman || !a.IsActive() || a == Agent.Main) continue;
                    var c = a.Character as CharacterObject;
                    var h = c != null && c.IsHero ? c.HeroObject : null;
                    if (h == null || h == Hero.MainHero || inside.Contains(h)) continue;
                    if (a.Position.Distance(Agent.Main.Position) > cfg.OverhearRadius) continue;
                    if (!OverheardBy.Add(key + "|" + h.StringId + "|" + (int)(now * 4))) continue;      // una vez por oyente y rato
                    var mem = MemoryStore.Get(h.StringId, h.Name.ToString());
                    InfoExchange.AddHeard(mem, new HeardItem
                    {
                        FromName = "your own ears", Secrecy = "private", Day = now,
                        Text = $"Standing nearby, you overheard them say to {names}: «{said}»",
                    });
                    Log.Info($"[escena] {h.Name} oye lo que le dices a {names}");
                }
                if (OverheardBy.Count > 500) OverheardBy.Clear();
            }
            catch (Exception ex) { Log.Error("Oír de pasada", ex); }
        }

        public static void Tick(float dt)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableAmbient || !SceneOk(out var st))
                {
                    if (Play.Count > 0) Play.Clear();
                    StopLooking();
                    ReleaseWalker();
                    if (_errand != null) { _gen++; DropErrand(null); }
                    if (Mission.Current == null) SceneMove.Forget();
                    return;
                }
                bool talking = Campaign.Current.ConversationManager?.IsConversationInProgress == true;

                // Si uno va andando hacia el otro, la charla no empieza hasta que llega (o se cansa de intentarlo)
                if (_walker != null && !_arrived)
                {
                    _walkLeft -= dt;
                    if (!_walker.IsActive() || _walkTo == null || !_walkTo.IsActive() || _walkLeft <= 0f || talking)
                    {
                        if (talking || _walkLeft <= 0f) { Play.Clear(); _gen++; ReleaseWalker(); if (_errand != null) DropErrand("no ha llegado a tiempo"); }
                        else _arrived = true;
                    }
                    else if (_walker.Position.Distance(_walkTo.Position) <= 3f) _arrived = true;
                    else return;
                }

                // Reproducir las frases una a una
                if (Play.Count > 0 || _lineLeft > 0f)
                {
                    if (Mission.Current != _mission) { Play.Clear(); _lineLeft = 0f; StopLooking(); return; }
                    _lineLeft -= dt;
                    if (_lineLeft <= 0f)
                    {
                        StopLooking();
                        if (Play.Count == 0 && _chain != null && !_busy && !talking)
                        {
                            // Por turnos: aún no ha terminado, se pide la tanda siguiente
                            ContinueChain();
                            if (_chain != null) return;
                        }
                        if (Play.Count == 0)
                        {
                            ReleaseWalker();      // se acabó la charla
                            if (_ambientPlaying)
                            {
                                // Tras una charla, un rato de silencio antes de la siguiente
                                _ambientPlaying = false;
                                _lastPairAt = DateTime.UtcNow;
                                _timer = Math.Max(_timer, cfg.AmbientCooldownSeconds * (0.8f + (float)Rng.NextDouble() * 0.4f));
                            }
                        }
                        if (Play.Count > 0 && !talking)
                        {
                            var l = Play.Dequeue();
                            if (l.Who != null && l.Who.IsActive())
                            {
                                float secs = Math.Max(2.5f, Math.Min(9f, 1.5f + l.Text.Length * 0.06f));
                                Bubbles.Show(l.Who, l.Text, secs);
                                Gestures.Play(l.Who, l.Mood); _speaking = l.Who;
                                if (!l.Quiet && cfg.AmbientToLog)
                                    InformationManager.DisplayMessage(new InformationMessage($"{l.Who.Name}: {l.Text}", new Color(0.78f, 0.78f, 0.7f)));
                                if (l.To != null && l.To.IsActive() && l.Who.IsAIControlled) { try { l.Who.SetLookAgent(l.To); _looking = l.Who; } catch { } }
                                _lineLeft = secs + 0.4f;
                            }
                        }
                        else if (talking) Play.Clear();
                    }
                    return;
                }

                // Recados dentro de la escena: salir a buscar a alguien, y volver a contarlo
                if (_errand != null)
                {
                    if (_errand.Stage == 0) { if (!talking && !_busy) BeginErrand(st); return; }
                    if (_errand.Stage == 1) { if (_busy) return; ReleaseWalker(); BeginReturn(); return; }
                    TickReturn(dt);
                    return;
                }
                if (_walker != null && !_busy) ReleaseWalker();     // la IA no dio charla: que no se quede plantado
                if (_busy || talking || ChatSession.IsBusy || Initiative.Busy) return;
                if (!Bubbles.Available)
                {
                    if (!_warned) { _warned = true; Log.Warn("[escena] el texto sobre la cabeza no está disponible (desactivado en MCM, falló antes, o la pantalla no es la de una escena): no hay charlas"); }
                    return;
                }
                _timer -= dt;
                if (_timer > 0f) return;
                _timer = cfg.AmbientIntervalSeconds * (0.7f + (float)Rng.NextDouble() * 0.6f);
                TryStart(st);
            }
            catch (Exception ex) { Log.Error("Vida en la escena", ex); Play.Clear(); _busy = false; }
        }

        /// <summary>El primero camina hasta quedar a un par de pasos del segundo.</summary>
        private static void StartWalk(Agent a, Agent b, float timeout = 40f)
        {
            try
            {
                if (!SceneMove.GoTo(a, b)) return;
                _walker = a; _walkTo = b; _walkLeft = timeout; _arrived = false;
            }
            catch (Exception ex) { Log.Error("Acercarse a hablar", ex); _walker = null; _walkTo = null; _arrived = true; }
        }

        /// <summary>Quien se acercó vuelve a lo suyo.</summary>
        private static void ReleaseWalker()
        {
            var w = _walker;
            _walker = null; _walkTo = null; _arrived = true;
            if (w == null) return;
            SceneMove.Release(w);
        }

        private static void StopLooking()
        {
            if (_speaking != null) { Gestures.End(_speaking); _speaking = null; }
            if (_looking == null) return;
            try { if (_looking.IsActive()) _looking.SetLookAgent(null); } catch { }
            _looking = null;
        }
    }
}
