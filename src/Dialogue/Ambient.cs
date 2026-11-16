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
    /// Scene life: now and then characters near you fall into conversation about what's going on (news they know,
    /// shortages, disease, their lords), with the text over their heads. Those talks are circles (see AmbientCircles.cs)
    /// that others may walk over to join and that can turn to you. Sometimes someone who knows you calls out as you
    /// pass. They remember what was said if you talk to them later.
    /// </summary>
    internal static partial class Ambient
    {
        private class Line
        {
            public Agent Who; public Agent To; public string Text; public bool Quiet; public string Mood;
            /// <summary>Group talk: the speaker is still walking over. The line waits for them and is logged when spoken.</summary>
            public bool Deferred;
            /// <summary>Notice shown just before the line ("X joins the conversation").</summary>
            public string Pre;
            /// <summary>Group talk line: it goes to the log when it is spoken (or dropped), not when it was written.</summary>
            public bool Group;
            /// <summary>Scene talk lines (not group talk): who says it and to whom, and the talk it belongs to, so both remember it and bystanders overhear it.</summary>
            public Speaker WhoS, ToS;
            public Talk Talk;
        }

        /// <summary>One talk between two people in the scene. Each keeps a single note in their history that grows line by line.</summary>
        private class Talk
        {
            public Speaker A, B;
            public string Place;
            public ChatTurn NoteA, NoteB;
            public readonly List<string> Said = new List<string>();
        }

        // Display time of a line over a head: 5 s plus 0.045 s a character, 30 s at most
        internal static float ShowSeconds(string text) => Math.Min(30f, 5f + 0.045f * (text?.Length ?? 0));
        // Without voice: after a line has had time to be read, this pause before the next speaker
        private const float SilentPause = 3f;
        // With voice: this gap after the audio ends before the next speaker
        private const float VoiceGap = 0.7f;

        // The two people of the current scene talk, kept on the spot and turned to each other
        private static readonly List<Agent> Held = new List<Agent>();
        private static bool _paused;
        private static bool _memDirty;
        // A voiced line waits here until its audio starts, so text and voice come out together
        private static Line _voiceWait;
        private static float _voiceWaitLeft, _voiceHold;
        // The line on now is voiced: the next one waits until the audio is over (at most _voiceCap more seconds)
        private static bool _afterVoice;
        private static float _voiceCap;
        private static float _hold;
        private static Agent _speaking;
        // The one spoken to in a group line turns to the speaker while it lasts
        private static Agent _listening;
        private static bool _curGroup;

        private static readonly Random Rng = new Random();
        private static readonly Queue<Line> Play = new Queue<Line>();
        private static Agent _looking;
        private static float _timer = 20f, _lineLeft;
        private static bool _busy;
        private static bool _warned;
        // Agent walking over to talk to the other one
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

        public static void Touch() { var _ = TAmbient.Key + TCallOut.Key + TErrandAngle.Key + TErrandReport.Key; TouchCircles(); }

        /// <summary>An errand inside the scene: someone goes to find another person, they talk, and if asked the first one comes back to tell you.</summary>
        private class Errand { public Agent Who, Target; public string Said, Report; public bool Back; public int Stage; public float Left; }
        private static Errand _errand;
        private static int _gen;       // bumped on cancel so a late AI reply gets thrown away

        internal static bool ErrandBusy => _errand != null;
        internal static bool IsPlaying => Play.Count > 0 || _lineLeft > 0f || _voiceWait != null || _afterVoice || CircleSpeaking;

        /// <summary>Group talk lines are still queued or one of them is being spoken.</summary>
        internal static bool GroupPlaying => ((_lineLeft > 0f || _afterVoice) && _curGroup) || (_voiceWait != null && _voiceWait.Group) || Play.Any(l => l.Group);

        /// <summary>Once the current lines finish, this character goes off to find the other one.</summary>
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
            if (why != null) Log.Info("[scene] errand cancelled: " + why);
        }

        private static void BeginErrand(Settlement st)
        {
            var e = _errand; var cfg = S.Get;
            if (e.Who == null || !e.Who.IsActive() || e.Target == null || !e.Target.IsActive()) { DropErrand(L.T("AIL_723bcca4", "ya no están")); return; }
            string da = Describe(e.Who, out var sa), db = Describe(e.Target, out var sb);
            StartWalk(e.Who, e.Target, 120f);
            if (_walker == null) { DropErrand(L.F("AIL_98b23f3f", $"{sa.Name} no puede moverse ahora")); return; }
            HoldPair(e.Who, e.Target);
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
            Log.Info($"[scene] errand: {sa.Name} goes to find {sb.Name} (at {e.Who.Position.Distance(e.Target.Position):0} m){(e.Back ? " and will come back to tell it" : "")}");
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
                float secs = ShowSeconds(e.Report);
                Bubbles.Show(e.Who, e.Report, secs);
                InformationManager.DisplayMessage(new InformationMessage($"{e.Who.Name}: {e.Report}", Colors.Yellow));
                try { e.Who.SetLookAgent(Agent.Main); _looking = e.Who; _lineLeft = secs; } catch { }
                Describe(e.Who, out var sp);
                if (sp.IsHero) { sp.Memory.Turns.Add(new ChatTurn { Role = "npc", Text = "[On coming back to them] " + e.Report, Day = CampaignTime.Now.ToDays }); SaveFiles.Flush(); }
                Overhear(new Speaker[0], e.Report, sp, 1f, e.Who);
                GroupTalk.Note($"{sp.Name}: {e.Report}");
            }
            catch (Exception ex) { Log.Error("Come back to tell it", ex); }
        }

        /// <summary>Soldiers, guards and thugs are on duty and don't start chatting on their own.</summary>
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

        // What each pair already talked about and which angle they took, so they don't repeat themselves
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

        /// <summary>What they are to each other. Without this they talk like strangers.</summary>
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

        /// <summary>What's on their mind today, taken from their actual situation.</summary>
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

        /// <summary>Today's angle. Picked here so they don't always end up talking about the weather and the war.</summary>
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
                if (st == null) return false;                                   // settlements only, no battles or ambushes
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
                                           && a.Position.Distance(me) <= cfg.AmbientRadius && !GroupTalk.Has(a) && !SceneActions.Busy(a) && !InCircle(a) && !Combat.FoughtHere(a)
                                           && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main))).ToList();
            if (_calledMission != m) { _calledMission = m; CalledOut.Clear(); }
            if (near.Count == 0) { Log.Info($"[scene] nobody within {cfg.AmbientRadius} m"); _timer = 20f; return; }

            // Sometimes someone who knows you says something as you pass
            if (cfg.AmbientCallOuts && Rng.Next(100) < 30)
            {
                var known = near.Where(a => ((CharacterObject)a.Character).IsHero && ((CharacterObject)a.Character).HeroObject != null
                                            && MemoryStore.Find(((CharacterObject)a.Character).HeroObject.StringId)?.ConversationCount > 0
                                            && !CalledOut.Contains(((CharacterObject)a.Character).HeroObject.StringId)
                                            && a.Position.Distance(me) <= 9f).OrderBy(_ => Rng.Next()).FirstOrDefault();
                if (known != null)
                {
                    // Each one calls out only once per visit, then a quiet stretch follows
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

            // Named people (lords, ladies, notables, wanderers) always chat on their own. Commoners and troops only
            // when enabled, and briefly.
            near = near.Where(a => Named(a) || (OnDuty(a) ? cfg.AmbientGuards : cfg.AmbientCommoners)).ToList();

            // At most so many background circles at once besides yours, and none while people are still shaken by a fight
            if (Circles.Count >= Math.Max(1, cfg.AmbientMaxCircles)) { Log.Info($"[scene] no new circles nearby: {Circles.Count} already going"); _timer = 20f; return; }
            if (Combat.QuietAfterFight) { Log.Info("[scene] no new circles nearby: a fight has just ended here"); _timer = 20f; return; }

            // Pick two who stand close enough to each other and can walk to each other, preferring the ones nearest you and named ones.
            var pairs = new List<(Agent x, Agent y, float score)>();
            foreach (var x in near)
                foreach (var y in near)
                {
                    if (x == y || x.Index >= y.Index) continue;
                    float d = x.Position.Distance(y.Position);
                    if (d > cfg.AmbientPairDistance) continue;
                    float score = d + x.Position.Distance(me) * 0.5f + y.Position.Distance(me) * 0.5f + (float)Rng.NextDouble() * 6f
                                  + (Named(x) && Named(y) ? 0f : Named(x) || Named(y) ? 10f : 20f);
                    pairs.Add((x, y, score));
                }
            Agent first = null, second = null;
            foreach (var p in pairs.OrderBy(p => p.score).Take(6))
            {
                if (Reachable(p.x, p.y)) { first = p.x; second = p.y; break; }
                Log.Info($"[scene] pair {p.x.Name} / {p.y.Name} looks close but has no way between them");
            }
            if (first == null)
            {
                if (pairs.Count == 0) Log.Info($"[scene] no pair to chat: {near.Count} character(s) within {cfg.AmbientRadius} m, none within {cfg.AmbientPairDistance} m of another");
                _timer = 20f;      // retry soon, people move around
                return;
            }
            if (Rng.Next(2) == 0) { var tmp = first; first = second; second = tmp; }
            Describe(first, out var sa); Describe(second, out var sb2);
            string pairKey = string.CompareOrdinal(sa.Key, sb2.Key) < 0 ? sa.Key + "|" + sb2.Key : sb2.Key + "|" + sa.Key;
            // Don't let the same two start again right away
            if (pairKey == _lastPair && (DateTime.UtcNow - _lastPairAt).TotalSeconds < cfg.AmbientCooldownSeconds * 2) { _timer = 30f; return; }
            _lastPair = pairKey; _lastPairAt = DateTime.UtcNow;
            StartCircle(st, first, second);
        }

        private static void Ask(Mission m, string prompt, Agent a, Agent b, Speaker sa, Speaker sb, int maxLines = 4, int tokens = 350, string pairKey = null, Errand errand = null)
        {
            _busy = true;
            int gen = _gen;
            int session = WorldSim.Session;
            string provider = (sa != null && sa.IsHero) || (sb != null && sb.IsHero) ? S.Get.ProviderHeroes : S.Get.ProviderResidents;
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "ambient", maxTokensOverride: tokens, preferredProvider: provider).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    _busy = false;
                    try
                    {
                        // asked in another campaign: nothing of it is said, heard or remembered in this one
                        if (session != WorldSim.Session) { return; }
                        if (res == null || !res.Ok) { Log.Warn("[scene] the AI gave no chat: " + res?.Error); return; }
                        if (Mission.Current != m || a == null || !a.IsActive() || gen != _gen) { return; }
                        int i0 = res.Text.IndexOf('{'), i1 = res.Text.LastIndexOf('}');
                        if (i0 < 0 || i1 <= i0) { return; }
                        var j = JObject.Parse(res.Text.Substring(i0, i1 - i0 + 1));
                        if (errand != null) errand.Report = ((string)j["report"] ?? "").Trim();
                        if (!(j["lines"] is JArray lines)) return;
                        int max = S.Get.AmbientMaxChars + 60;
                        // the talk these lines belong to
                        Talk talk = sa != null ? new Talk { A = sa, B = b != null ? sb : null, Place = Settlement.CurrentSettlement?.Name?.ToString() } : null;
                        foreach (var l in lines.OfType<JObject>().Take(maxLines))
                        {
                            string text = ((string)l["text"] ?? "").Trim();
                            if (text.Length < 2) continue;
                            if (text.Length > max) text = text.Substring(0, max);
                            bool isB = ((string)l["who"] ?? "A").Trim().ToUpperInvariant() == "B" && b != null;
                            Play.Enqueue(new Line { Who = isB ? b : a, To = isB ? a : (b ?? Agent.Main), Text = text, Mood = (string)l["mood"],
                                                    WhoS = isB ? sb : sa, ToS = isB ? sa : (b != null ? sb : null), Talk = talk });
                            // Errand lines go into the group log, so whoever went knows them on the way back
                            if (errand != null && sa != null && sb != null) GroupTalk.Note($"{(isB ? sb.Name : sa.Name)} (to {(isB ? sa.Name : sb.Name)}, away from the group): {text}");
                        }
                        _mission = m;
                        if (b != null) _ambientPlaying = true;
                        string topic = ((string)j["topic"] ?? "").Trim();
                        if (topic.Length > 5 && pairKey != null && sa != null && sb != null)
                        {
                            // Remembered so it isn't repeated, and a named person will know it when you talk to them
                            if (!PairTopics.TryGetValue(pairKey, out var past)) PairTopics[pairKey] = past = new List<string>();
                            past.Add(topic);
                            while (past.Count > 6) past.RemoveAt(0);
                            // How they stand after the talk. Each one remembers it their own way
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
                            // TouchOther already puts the topic in Others[...].Notes. It doesn't go to Heard, which is only for what they know about the player
                        }
                        if (topic.Length > 5)
                        {
                            // If you talk to them afterwards, they know what they were just talking about
                            if (sa != null) { sa.Memory.RecentTalk = b != null ? $"Moments ago you were talking with {(sb != null ? sb.Name : "someone")} nearby: {topic}" : $"Moments ago you called out to them as they passed: {topic}"; sa.Memory.RecentTalkAt = DateTime.UtcNow; }
                            if (sb != null) { sb.Memory.RecentTalk = $"Moments ago you were talking with {sa?.Name ?? "someone"} nearby: {topic}"; sb.Memory.RecentTalkAt = DateTime.UtcNow; }
                            Log.Info($"[scene] {(sa?.Name ?? "?")}{(sb != null ? " and " + sb.Name : " (to the player)")}: {topic}");
                        }
                    }
                    catch (Exception ex) { Log.Error("Ambient chat", ex); }
                });
            });
        }

        /// <summary>
        /// Queues lines to show one at a time over heads. Used by group talk. Each line reaches the log when it is spoken.
        /// append = false cuts whatever is playing (you spoke); true adds them after it (the group goes on by itself).
        /// </summary>
        internal static void Enqueue(Mission m, List<(Agent who, Agent to, string text, string mood, string pre, bool deferred)> lines, bool append = false)
        {
            if (!append)
            {
                FlushDeferred();
                Play.Clear();
                _hold = 0f;
                _lineLeft = 0f;
                _voiceWait = null; _afterVoice = false; _voiceHold = 0f;
                _paused = false;
            }
            _mission = m;
            foreach (var l in lines) Play.Enqueue(new Line { Who = l.who, To = l.to, Text = l.text, Mood = l.mood, Quiet = true, Pre = l.pre, Deferred = l.deferred, Group = true });
        }

        private static readonly Color GroupLineColor = Colors.Yellow, NoticeColor = Colors.Gray;

        private static void Announce(Line l)
        {
            try
            {
                if (l.Pre != null) InformationManager.DisplayMessage(new InformationMessage(l.Pre, NoticeColor));
                if ((l.Deferred || l.Group) && l.Who != null) InformationManager.DisplayMessage(new InformationMessage($"{l.Who.Name}: {l.Text}", GroupLineColor));
            }
            catch { }
        }

        /// <summary>Lines dropped before being spoken still reach the log, so nothing said in the group is lost.</summary>
        private static void FlushDeferred()
        {
            foreach (var l in Play) if (l.Deferred || l.Group || l.Pre != null) Announce(l);
        }

        private static readonly HashSet<string> OverheardBy = new HashSet<string>();

        /// <summary>
        /// Named characters a few steps away hear what you say to someone in a scene, and keep it as
        /// something they overheard (once per talk and listener).
        /// </summary>
        public static void Overhear(Speaker speaker, string playerSaid)
        {
            if (speaker != null) Overhear(new[] { speaker }, playerSaid);
        }

        /// <summary>Within this distance every word is heard. Further away, words and letters are lost.</summary>
        private const float ClearHearing = 4.5f;
        private static readonly Dictionary<string, int> HeardCount = new Dictionary<string, int>();

        /// <summary>What reaches a listener at that distance: clean up close, with gaps further off.</summary>
        internal static string Degrade(string said, float distance, float radius)
        {
            if (distance <= ClearHearing || radius <= ClearHearing) return said;
            double loss = Math.Min(1.0, (distance - ClearHearing) / (radius - ClearHearing));
            var rng = new Random(said.GetHashCode() ^ (int)(distance * 10));
            var words = said.Split(' ');
            var sb = new StringBuilder();
            bool gap = false;
            foreach (var w in words)
            {
                if (w.Length == 0) continue;
                if (rng.NextDouble() < loss * 0.6)
                {
                    if (!gap) { sb.Append("… "); gap = true; }
                    continue;
                }
                gap = false;
                foreach (char c in w) sb.Append(char.IsLetter(c) && rng.NextDouble() < loss * 0.3 ? '·' : c);
                sb.Append(' ');
            }
            return sb.ToString().Trim();
        }

        /// <summary>
        /// A line spoken in a scene reaches the named characters standing around. speaker null = the player said it to
        /// 'addressed'; otherwise that character said it (to the player, or within the group in 'addressed').
        /// The people in 'addressed' and the speaker heard it directly, so they are skipped.
        /// reach scales the hearing distance: 0.25 for a whisper, 2 for a shout. from = the speaking agent when it is
        /// known (needed for people who are not heroes; otherwise the speaker's hero is looked up in the scene).
        /// Distance is measured along the walkable path; a wall in between muffles the words.
        /// </summary>
        public static void Overhear(IList<Speaker> addressed, string said, Speaker speaker = null, float reach = 1f, Agent from = null)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableOverhearing || Mission.Current == null || Agent.Main == null || string.IsNullOrWhiteSpace(said) || said.Length < 15) return;
                var to = (addressed ?? new Speaker[0]).Where(x => x != null).ToList();
                if (to.Count == 0 && speaker == null) return;
                var inside = new HashSet<Hero>(to.Where(x => x.IsHero && x.Hero != null).Select(x => x.Hero));
                if (speaker != null && speaker.IsHero && speaker.Hero != null) inside.Add(speaker.Hero);
                string key = (speaker?.Key ?? "player") + ">" + string.Join(",", to.Select(x => x.Key));
                string names = to.Count == 0 ? "the one they were talking to" : to.Count == 1 ? to[0].Name : to.Count == 2 ? $"{to[0].Name} and {to[1].Name}" : $"the group ({string.Join(", ", to.Select(x => x.Name))})";
                if (said.Length > 220) said = said.Substring(0, 220) + "…";
                double now = CampaignTime.Now.ToDays;
                float radius = Math.Max(1f, cfg.OverhearRadius * reach);
                // where the words come from
                Agent src = Agent.Main;
                if (speaker != null)
                {
                    src = from != null && from.IsActive() ? from
                        : Mission.Current.Agents.FirstOrDefault(x => x != null && x.IsActive() && x.Character is CharacterObject sc && speaker.IsHero && sc.IsHero && sc.HeroObject == speaker.Hero);
                    if (src == null) src = Agent.Main;      // not found in the scene: they were talking to you, so about where you stand
                }
                var origin = src.Position;
                string how = reach <= 0.5f ? ", in a whisper" : reach >= 1.5f ? ", shouted" : "";
                foreach (var a in Mission.Current.Agents)
                {
                    if (a == null || !a.IsHuman || !a.IsActive() || a == Agent.Main || a == src) continue;
                    var c = a.Character as CharacterObject;
                    var h = c != null && c.IsHero ? c.HeroObject : null;
                    if (h == null || h == Hero.MainHero || inside.Contains(h)) continue;
                    float straight = a.Position.Distance(origin);
                    if (straight > radius) continue;
                    // A wall or a floor between them: the sound goes round (path) or comes through muffled
                    bool muffled = !Bubbles.LineOfSight(src.GetEyeGlobalPosition(), a.GetEyeGlobalPosition());
                    float dist = straight;
                    if (muffled)
                    {
                        float path = PathDistance(src, a);
                        dist = Math.Max(straight * 1.8f, path > 0f ? Math.Min(path, straight * 3f) : 0f);
                        if (dist > radius) continue;
                    }
                    string slot = key + "|" + h.StringId + "|" + (int)(now * 4);
                    if (speaker == null)
                    {
                        if (!OverheardBy.Add(slot)) continue;                  // the player: once per listener per quarter day
                    }
                    else
                    {
                        // other speakers: a few lines per listener per quarter day, never the same line twice
                        if (!OverheardBy.Add(slot + "|" + said.GetHashCode())) continue;
                        HeardCount.TryGetValue(slot, out int n);
                        if (n >= 3) continue;
                        HeardCount[slot] = n + 1;
                    }
                    string caught = Degrade(said, dist, radius);
                    if (caught.Replace("…", "").Replace("·", "").Trim().Length < 8) continue;      // too far to make anything out
                    bool partial = caught != said;
                    var mem = MemoryStore.Get(h.StringId, h.Name.ToString());
                    string where = $"[Overheard nearby, day {(int)now}, about {Math.Max(1, (int)Math.Round(straight))} m away{(muffled ? ", muffled by a wall" : "")}{how}] ";
                    InfoExchange.AddHeard(mem, new HeardItem
                    {
                        FromName = InfoExchange.OwnEars, Secrecy = "private", Day = now,
                        Text = where + (partial ? (muffled ? "Through the wall, you caught only part of what " : "From a distance, you caught only part of what ") : (muffled ? "Through the wall, you overheard " : "Standing nearby, you overheard "))
                               + (speaker == null ? (partial ? "they said to " : "them say to ") : (partial ? speaker.Name + " said to " : speaker.Name + " say to "))
                               + names + $": «{caught}»" + (partial ? " (the gaps are what you could not hear; do not fill them in as if you knew)" : ""),
                    });
                    Log.Info($"[scene] {h.Name} overhears {(partial ? "half of " : "")}what {(speaker == null ? "you say" : speaker.Name + " says")} to {names} ({straight:0.0} m, heard as {dist:0.0} m{(muffled ? ", through a wall" : "")})");
                }
                if (OverheardBy.Count > 800) { OverheardBy.Clear(); HeardCount.Clear(); }
            }
            catch (Exception ex) { Log.Error("Overhear in passing", ex); }
        }

        /// <summary>Walking distance between two agents in the scene, or -1 if there is no path or it can't be measured.</summary>
        private static float PathDistance(Agent a, Agent b)
        {
            try
            {
                var scene = Mission.Current?.Scene;
                if (scene == null) return -1f;
                var p0 = a.GetWorldPosition();
                var p1 = b.GetWorldPosition();
                return scene.GetPathDistanceBetweenPositions(ref p0, ref p1, 0.3f, out float d) && d > 0f ? d : -1f;
            }
            catch { return -1f; }
        }

        public static void Tick(float dt)
        {
            try
            {
                var cfg = S.Get;
                // The background circles run on their own: they keep going while other lines play and wait out fights
                TickCircles(dt, cfg);
                if (!cfg.EnableAmbient || !SceneOk(out var st))
                {
                    // Group talk also works where scene chats don't (on the road, or with them turned off): its lines still play
                    if (GroupOnlyQueue() && GroupSceneOk())
                    {
                        bool win = Campaign.Current.ConversationManager?.IsConversationInProgress == true;
                        PlayStep(dt, cfg, win, win);
                        return;
                    }
                    if (Play.Count > 0) { FlushDeferred(); Play.Clear(); }
                    _lineLeft = 0f;
                    _voiceWait = null; _afterVoice = false; _paused = false;
                    StopLooking();
                    ReleaseWalker();
                    if (_errand != null) { _gen++; DropErrand(null); }
                    if (Mission.Current == null) SceneMove.Forget();
                    return;
                }
                // A dialogue window is open: the talk around waits for it to close
                bool window = Campaign.Current.ConversationManager?.IsConversationInProgress == true;
                bool talking = window || ChatSession.IsBusy;

                // If one is walking over to the other, the talk waits until they arrive (or give up)
                if (_walker != null && !_arrived)
                {
                    if (talking) return;            // paused while the window is open; the walk goes on and the clock stops
                    _walkLeft -= dt;
                    if (!_walker.IsActive() || _walkTo == null || !_walkTo.IsActive() || _walkLeft <= 0f)
                    {
                        if (_walkLeft <= 0f) { Play.Clear(); _gen++; ReleaseWalker(); if (_errand != null) DropErrand("no ha llegado a tiempo"); }
                        else _arrived = true;
                    }
                    else if (_walker.Position.Distance(_walkTo.Position) <= 3f)
                    {
                        _arrived = true;
                        // there now: stays put, turned to the other one
                        if (SceneMove.Hold(_walker, _walkTo) && !Held.Contains(_walker)) Held.Add(_walker);
                    }
                    else return;
                }

                if (Play.Count > 0 || _lineLeft > 0f || _voiceWait != null || _afterVoice || _paused)
                {
                    PlayStep(dt, cfg, talking, window);
                    return;
                }

                // Errands inside the scene: go find someone, then come back to report
                if (_errand != null)
                {
                    if (_errand.Stage == 0) { if (!talking && !_busy) BeginErrand(st); return; }
                    if (_errand.Stage == 1) { if (_busy) return; ReleaseWalker(); BeginReturn(); return; }
                    TickReturn(dt);
                    return;
                }
                // the AI gave no talk (or it is over): don't leave them standing there
                if ((_walker != null || Held.Count > 0) && !_busy) ReleaseWalker();
                if (_busy || talking || Initiative.Busy) return;
                if (!Bubbles.Available)
                {
                    if (!_warned) { _warned = true; Log.Warn("[scene] overhead text is not available (disabled in MCM, failed earlier, or the screen is not a scene): no chats"); }
                    return;
                }
                _timer -= dt;
                if (_timer > 0f) return;
                _timer = cfg.AmbientIntervalSeconds * (0.7f + (float)Rng.NextDouble() * 0.6f);
                TryStart(st);
            }
            catch (Exception ex) { Log.Error("Scene life", ex); Play.Clear(); _busy = false; _voiceWait = null; _afterVoice = false; _paused = false; }
        }

        /// <summary>Nothing but group talk lines are queued or playing.</summary>
        private static bool GroupOnlyQueue() => (Play.Count > 0 || _lineLeft > 0f || _voiceWait != null || _afterVoice) && Play.All(l => l.Group)
                                                && (_voiceWait == null || _voiceWait.Group) && ((_lineLeft <= 0f && !_afterVoice) || _curGroup);

        private static bool GroupSceneOk()
        {
            try
            {
                var m = Mission.Current;
                if (m == null || Campaign.Current == null || Agent.Main == null || !Agent.Main.IsActive() || !S.Get.EnableGroupTalk) return false;
                return m.Mode != MissionMode.Battle && m.Mode != MissionMode.Duel && m.Mode != MissionMode.Deployment;
            }
            catch { return false; }
        }

        /// <summary>
        /// One frame of the queued lines: the current one runs out, the next one is spoken. A line is shown for
        /// ShowSeconds; the next speaker waits for the voice to end, or without voice for a short pause after reading.
        /// </summary>
        private static void PlayStep(float dt, AILegacySettings cfg, bool talking, bool window)
        {
            if (Mission.Current != _mission) { Play.Clear(); _lineLeft = 0f; _voiceWait = null; _afterVoice = false; _paused = false; StopLooking(); return; }

            // In a dialogue window group talk lines go to the log as before
            if (window && (Play.Any(l => l.Group) || (_voiceWait != null && _voiceWait.Group))) DropGroupLines();
            if (talking)
            {
                // the scene talk waits until the window closes
                bool scene = Play.Any(l => !l.Group) || (_voiceWait != null && !_voiceWait.Group) || (!_curGroup && (_lineLeft > 0f || _afterVoice));
                if (scene) { Pause(); return; }
                if (window || Play.Count == 0) { if (_lineLeft > 0f) _lineLeft -= dt; return; }
            }
            if (_paused) { Resume(); if (_paused) return; }

            // A voiced line waits for its audio to start, so the words and the text come out together (10 s at most)
            if (_voiceWait != null)
            {
                _voiceWaitLeft -= dt;
                double left = AILegacy.Voice.Speech.SecondsLeft;
                if (left <= 0 && AILegacy.Voice.Speech.IsSpeaking && _voiceWaitLeft > 0f) return;
                var vl = _voiceWait; _voiceWait = null;
                if (vl.Who != null && vl.Who.IsActive()) Present(vl, cfg, left > 0 ? (float)left : 0f, true);
                return;
            }

            _lineLeft -= dt;
            if (_lineLeft > 0f) return;
            if (_afterVoice)
            {
                // the next speaker waits until the voice is done, then a breath
                if (AILegacy.Voice.Speech.IsSpeaking && (_voiceCap -= dt) > 0f) return;
                _afterVoice = false;
                _lineLeft = VoiceGap;
                return;
            }
            StopLooking();
            _curGroup = false;
            if (Play.Count == 0)
            {
                if (_busy) return;
                ReleaseWalker();
                if (_ambientPlaying)
                {
                    // Quiet stretch before the next talk
                    _ambientPlaying = false;
                    _lastPairAt = DateTime.UtcNow;
                    _timer = Math.Max(_timer, cfg.AmbientCooldownSeconds * (0.8f + (float)Rng.NextDouble() * 0.4f));
                }
                return;
            }

            // Someone joining speaks once they have walked up, not from across the room (12 s at most)
            var next = Play.Peek();
            if (next.Deferred && next.Who != null && next.Who.IsActive() && Agent.Main != null
                && next.Who.Position.Distance(Agent.Main.Position) > 4.5f && (_hold += dt) < 12f) return;
            // Voiced only where you could hear it. Whatever is still being said finishes first, so the next audio is this line's
            bool voiced = cfg.VoiceInGroups && AILegacy.Voice.Speech.Enabled && next.Who != null && next.Who.IsActive() && Bubbles.CanHear(next.Who);
            if (voiced && AILegacy.Voice.Speech.IsSpeaking && (_voiceHold += dt) < 15f) return;
            _hold = 0f; _voiceHold = 0f;
            var l = Play.Dequeue();
            Announce(l);
            if (l.Who == null || !l.Who.IsActive()) return;
            _curGroup = l.Group;
            if (voiced)
            {
                AILegacy.Voice.Speech.Say((l.Who.Character?.StringId ?? "") + "|" + l.Who.Name, l.Who.IsFemale, l.Text);
                if (AILegacy.Voice.Speech.IsSpeaking) { _voiceWait = l; _voiceWaitLeft = 10f; return; }
            }
            Present(l, cfg, 0f, false);
        }

        /// <summary>The line appears over the speaker, who gestures and looks at the one spoken to; that one turns to them.</summary>
        private static void Present(Line l, AILegacySettings cfg, float audio, bool voiced)
        {
            float show = ShowSeconds(l.Text);
            Bubbles.Show(l.Who, l.Text, audio > 0f ? Math.Max(show, audio + 1f) : show);
            Gestures.Play(l.Who, l.Mood); _speaking = l.Who;
            if (!l.Quiet && cfg.AmbientToLog)
                InformationManager.DisplayMessage(new InformationMessage($"{l.Who.Name}: {l.Text}", new Color(0.78f, 0.78f, 0.7f)));
            if (l.To != null && l.To.IsActive() && l.Who.IsAIControlled) { try { l.Who.SetLookAgent(l.To); _looking = l.Who; } catch { } }
            if (!l.Group && l.To != null && l.To != Agent.Main && l.To != l.Who) { SceneMove.Face(l.To, l.Who); SceneMove.Face(l.Who, l.To); }
            // In a group, whoever is spoken to (if not you) turns to the speaker
            if (l.Group && l.To != null && l.To != Agent.Main && l.To != l.Who && l.To.IsActive() && l.To.IsAIControlled)
            { try { l.To.SetLookAgent(l.Who); _listening = l.To; } catch { } }
            _curGroup = l.Group;
            if (voiced && (audio > 0f || AILegacy.Voice.Speech.IsSpeaking))
            {
                _afterVoice = true;
                _voiceCap = show + 20f;
                _lineLeft = Math.Min(1f, show);
            }
            else
            {
                _afterVoice = false;
                _lineLeft = Math.Min(show, 2f + 0.05f * l.Text.Length) + SilentPause;
            }
            Remember(l);
        }

        private static string Clip(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "…";

        /// <summary>
        /// A scene talk line goes into both speakers' histories (one note per talk that grows line by line, so they
        /// remember it when you talk to them) and reaches whoever stands within earshot.
        /// </summary>
        private static void Remember(Line l)
        {
            var t = l.Talk;
            if (t == null || l.WhoS == null) return;
            try
            {
                double day = CampaignTime.Now.ToDays;
                t.Said.Add($"{l.WhoS.Name}: «{Clip(l.Text, 160)}»");
                int total = t.Said.Sum(x => x.Length);
                while (t.Said.Count > 1 && total > 900) { total -= t.Said[0].Length; t.Said.RemoveAt(0); }
                string body = string.Join(" / ", t.Said);
                string at = string.IsNullOrEmpty(t.Place) ? "" : " in " + t.Place;
                bool playerNear = Agent.Main != null && Bubbles.CanHear(l.Who);
                if (t.B == null)
                    t.NoteA = Note(t.A, t.NoteA, $"[In a group: as they walked past{at}, you called out to them — {body}]", day);
                else
                {
                    string near = playerNear ? ", within earshot of the one you are speaking with now" : "";
                    t.NoteA = Note(t.A, t.NoteA, $"[In a group: you were talking with {t.B.Name}{at}{near} — {body}]", day);
                    t.NoteB = Note(t.B, t.NoteB, $"[In a group: you were talking with {t.A.Name}{at}{near} — {body}]", day);
                }
                _memDirty = true;
            }
            catch (Exception ex) { Log.Error("Remember a scene line", ex); }
            // Bystanders within earshot catch it too
            try { Overhear(l.ToS != null ? new[] { l.ToS } : new Speaker[0], l.Text, l.WhoS, 1f, l.Who); } catch { }
        }

        private static ChatTurn Note(Speaker s, ChatTurn turn, string text, double day)
        {
            if (s?.Memory == null) return turn;
            if (turn == null || !s.Memory.Turns.Contains(turn))
            {
                turn = new ChatTurn { Role = "player", Text = text, Day = day };
                s.Memory.Turns.Add(turn);
            }
            else { turn.Text = text; turn.Day = day; }
            return turn;
        }

        /// <summary>Group talk lines still waiting go to the log at once (as when a dialogue window opens); scene talk lines stay.</summary>
        private static void DropGroupLines()
        {
            if (_voiceWait != null && _voiceWait.Group) _voiceWait = null;        // already in the log
            var keep = new List<Line>();
            foreach (var l in Play) { if (l.Group) Announce(l); else keep.Add(l); }
            Play.Clear();
            foreach (var l in keep) Play.Enqueue(l);
            if (_curGroup) { _afterVoice = false; }
        }

        /// <summary>A dialogue window opened: the scene talk stops where it is and waits.</summary>
        private static void Pause()
        {
            if (_paused) return;
            _paused = true;
            StopLooking();
            // the line waiting for its voice goes back to the front, to be said again afterwards
            if (_voiceWait != null)
            {
                var rest = Play.ToList();
                Play.Clear();
                Play.Enqueue(_voiceWait);
                foreach (var x in rest) Play.Enqueue(x);
                _voiceWait = null;
                AILegacy.Voice.Speech.Stop();
            }
            else if (_afterVoice && !_curGroup) { _afterVoice = false; AILegacy.Voice.Speech.Stop(); }
            // whoever you are talking to belongs to the dialogue now; the other one keeps their place
            try
            {
                var cm = Campaign.Current?.ConversationManager;
                var inTalk = cm?.ConversationAgents;
                if (inTalk != null)
                    foreach (var a in Held.ToList())
                        if (inTalk.Any(x => ReferenceEquals(x, a))) { SceneMove.Release(a); Held.Remove(a); }
            }
            catch { }
            Log.Info("[scene] a dialogue window opened: the talk nearby waits for it to close");
        }

        /// <summary>The window closed: the two take the talk up again if they are still together, or it ends.</summary>
        private static void Resume()
        {
            _paused = false;
            var next = Play.FirstOrDefault(x => !x.Group);
            Agent a = next?.Who, b = next != null && next.To != Agent.Main ? next.To : null;
            if (a == null || !a.IsActive() || (b != null && (!b.IsActive() || a.Position.Distance(b.Position) > 8f)))
            {
                if (next != null) Log.Info("[scene] after the dialogue window the two are no longer together: their talk ends");
                var keep = Play.Where(x => x.Group).ToList();
                Play.Clear();
                foreach (var x in keep) Play.Enqueue(x);
                _lineLeft = 0f; _afterVoice = false;
                return;
            }
            if (b != null) HoldPair(a, b);
            _lineLeft = Math.Max(_lineLeft, 2f);
            Log.Info("[scene] the dialogue window closed: the talk nearby goes on");
        }

        /// <summary>Both keep their place for the talk, turned to each other. The one walking over is held once there.</summary>
        private static void HoldPair(Agent a, Agent b)
        {
            if (a == null || b == null) return;
            foreach (var (x, y) in new[] { (b, a), (a, b) })
            {
                if (x == _walker && !_arrived) continue;
                if (SceneMove.Hold(x, y) && !Held.Contains(x)) Held.Add(x);
            }
        }

        /// <summary>The first agent walks up to within a couple of steps of the second.</summary>
        private static void StartWalk(Agent a, Agent b, float timeout = 40f)
        {
            try
            {
                if (!SceneMove.GoTo(a, b)) return;
                _walker = a; _walkTo = b; _walkLeft = timeout; _arrived = false;
            }
            catch (Exception ex) { Log.Error("Approach to talk", ex); _walker = null; _walkTo = null; _arrived = true; }
        }

        /// <summary>The walker and the ones held for the talk go back to what they were doing.</summary>
        private static void ReleaseWalker()
        {
            var w = _walker;
            _walker = null; _walkTo = null; _arrived = true;
            if (w != null) SceneMove.Release(w);
            // only those still held by us: someone may have been sent off since (to follow you, on an errand)
            foreach (var a in Held) if (a != w && SceneMove.IsHeld(a)) SceneMove.Release(a);
            Held.Clear();
            if (_memDirty) { _memDirty = false; try { SaveFiles.Flush(); } catch { } }
        }

        private static void StopLooking()
        {
            if (_speaking != null) { Gestures.End(_speaking); _speaking = null; }
            // the ones held for a talk keep looking at each other
            if (_listening != null) { try { if (_listening.IsActive() && !SceneMove.IsHeld(_listening)) _listening.SetLookAgent(null); } catch { } _listening = null; }
            if (_looking == null) return;
            try { if (_looking.IsActive() && !SceneMove.IsHeld(_looking)) _looking.SetLookAgent(null); } catch { }
            _looking = null;
        }
    }
}
