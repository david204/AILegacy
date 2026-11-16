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
    /// Background conversations as real circles. Two people start one; up to AmbientMaxMembers stand in it, and
    /// bystanders near it may walk over and join. It is asked for a few lines at a time, each round written while the
    /// lines before it are still being said, until the talk runs dry or its line budget is spent. Someone in it can
    /// turn to you if you linger close by: the circle then becomes your group talk (GroupTalk.TakeOver), with what was
    /// already said and who is in it. Up to AmbientMaxCircles run at once besides yours. A dialogue window pauses them,
    /// a fight nearby stops them until it is over, and every line goes to its people's memory and to whoever overhears it.
    /// </summary>
    internal static partial class Ambient
    {
        private class CMember
        {
            public Agent Agent; public Speaker Who; public string Desc;
            /// <summary>On the way over to <see cref="WalkTo"/>; their lines wait until they are there.</summary>
            public bool Walking; public Agent WalkTo; public float WalkLeft;
            /// <summary>The single note in their history for this circle, rewritten as the talk grows.</summary>
            public ChatTurn Note;
            public int Spoke;
        }

        private class CLine { public CMember Who, To; public bool ToPlayer; public string Text, Mood; }

        /// <summary>A reply that came back while earlier lines were still being said.</summary>
        private class CRound { public int Gen; public JObject J; public List<CMember> Members; public List<Agent> Bystanders; public bool PlayerOpen; public int Max; }

        private class Circle
        {
            public int Id; public Mission M; public string Place, Angle, Key;
            public readonly List<CMember> Members = new List<CMember>();
            /// <summary>"Name (to X): line", in order: for the AI, and handed to your group talk if they turn to you.</summary>
            public readonly List<string> Said = new List<string>();
            /// <summary>"Name: «line»": what its people keep in memory.</summary>
            public readonly List<string> Heard = new List<string>();
            public readonly Queue<CLine> Queue = new Queue<CLine>();
            public CLine Cur; public float Left; public Agent Speaking;
            public bool VoiceWait, AfterVoice; public float VoiceWaitLeft, VoiceCap;
            public bool Asking; public CRound Ready; public int Gen;
            public int LinesLeft, Step; public bool Full, Done, Started;
            public bool Paused, Watching, Noticed;
            public float Linger, Spread;
            public string Topic, LastKey, LastName;
            public readonly List<(int from, int to, int change, List<CMember> members)> After = new List<(int, int, int, List<CMember>)>();
            public Agent ThinkingOn;
        }

        private static readonly List<Circle> Circles = new List<Circle>();
        private static int _circleIds;
        // Only one voice at a time: the circle whose line is being voiced now
        private static Circle _voiceOwner;
        private static bool _fightWas, _quietWas, _circlesWaited;

        // Within this distance, lingering by a circle for LingerSeconds lets its people turn to you
        private const float LingerDistance = 6f, LingerSeconds = 8f;
        // A newcomer is there once this close to the one they walk to
        private const float ArriveDistance = 3f;

        private static readonly Tpl TCircle = new Tpl("scene_conversations/circle_round.txt",
@"You are writing a scene for a story set in Calradia, the medieval world of Mount & Blade II: Bannerlord. A few people are standing together in {place}, talking among themselves. Write the next lines of their conversation so that it sounds like real people who share a life and a history, not like speeches.

The people in the conversation (each speaks only for themselves, in their own voice and station):
{members}
What they are to each other:
{ties}
Their world right now (background they all already know; they do NOT explain it to each other):
{context}
People they know and may mention: {others}
Earlier talks among them (do not repeat them; one may be picked up where it was left): {before}
What this conversation is about: {angle}

The talk so far (it HAS HAPPENED and they all remember it):
{transcript}

Standing nearby, NOT part of the conversation: {bystanders}
A bystander steps in only rarely, when what is said truly concerns them (their lord, their trade, their kin, their post) or is outrageous: then use their code (for example ""B1"") as ""who""; they walk over and join.
{onlooker}

Write {min_lines} to {max_lines} line(s). {closing}
- ONE subject, followed through. Every line answers the line just before it: a question gets an answer, a jab a retort, a worry comfort or dismissal. Take up a new subject only if the old one has truly run out.
- Never the same person twice in a row. The last words above were {last_speaker}'s, so someone else speaks first. Whoever has said least may come in; nobody has to.
- People who know each other speak in shorthand: they never state what all of them know, they refer to it sideways. Small and concrete beats grand: a name, a sum of coin, a sore knee, a letter that has not come. No proclamations, no proverbs.
- Different voices from their character and station; vary the length of lines. Soldiers, guards and common folk speak in ONE short sentence (under 80 characters).
- Each line under {max_chars} characters, in {language}, with the natural spoken phrasing of that language. Spoken words only (at most one brief *gesture*). No names of people who are not in the data. Nothing about a game or an AI.
Each line also carries ""mood"": how the speaker looks while saying it, one of: {moods}.
Add ""done"": true if the talk really ends with these lines (a decision, a shrug, a laugh, someone cutting it short, nothing left to say), otherwise false. Add ""topic"": what they have talked about so far and where it stands, ONE English sentence. When ""done"" is true, add ""after"": [ { ""from"": <number>, ""to"": <number>, ""change"": -2 to 2 } ] for anyone whose feeling towards another person present truly changed in this talk (usually empty).
Answer ONLY with JSON: { ""lines"": [ { ""who"": <number of the speaker, or a bystander code>, ""to"": <number of the person spoken to{to_newcomer}>, ""text"": ""..."", ""mood"": ""..."" } ], ""done"": false, ""topic"": ""..."", ""after"": [] }");

        private static readonly Tpl TCircleLinger = new Tpl("scene_conversations/circle_newcomer_lingers.txt",
@"Someone else has come close and has been lingering by the conversation for a while without speaking or joining: {who}. The people talking react as fits what they know of that person and their own business: a glance, a greeting, a question, caution, lowering their voice, or simply carrying on with each other are all possible. If one of them speaks TO that person, use 0 as ""to"" for that line and end the lines there: that person then becomes part of the conversation, so do it only when it would really happen.");

        private static readonly Tpl TCircleEarshot = new Tpl("scene_conversations/circle_within_earshot.txt",
@"Within earshot: {who}. They may lower their voices or glance at that person, but they do not speak to them.");

        private static void TouchCircles() { var _ = TCircle.Key + TCircleLinger.Key + TCircleEarshot.Key; }

        /// <summary>The agent stands in, or is walking over to, one of the background circles.</summary>
        internal static bool InCircle(Agent a) => a != null && Circles.Any(c => c.Members.Any(m => m.Agent == a));

        /// <summary>A circle line you can hear is being said right now.</summary>
        private static bool CircleSpeaking => Circles.Any(c => c.Cur != null && c.Cur.Who?.Agent != null && Bubbles.CanHear(c.Cur.Who.Agent));

        /// <summary>The two can walk to each other: same spot, or a path on the navmesh that is not a huge detour.</summary>
        private static bool Reachable(Agent a, Agent b)
        {
            try
            {
                float straight = a.Position.Distance(b.Position);
                if (straight <= 2.5f && Math.Abs(a.Position.z - b.Position.z) < 1.5f) return true;
                float path = PathDistance(a, b);
                return path > 0f && path <= straight * 3f + 10f;
            }
            catch { return false; }
        }

        private static string KeyOf(Circle c)
        {
            var keys = c.Members.Select(m => m.Who.Key).OrderBy(k => k, StringComparer.Ordinal).ToList();
            return string.Join("|", keys);
        }

        private static Vec3 Center(Circle c, CMember except = null)
        {
            var at = c.Members.Where(m => m != except && !m.Walking && m.Agent != null && m.Agent.IsActive()).ToList();
            if (at.Count == 0) at = c.Members.Where(m => m != except && m.Agent != null && m.Agent.IsActive()).ToList();
            if (at.Count == 0) return Agent.Main != null ? Agent.Main.Position : Vec3.Zero;
            var sum = Vec3.Zero;
            foreach (var m in at) sum += m.Agent.Position;
            return sum * (1f / at.Count);
        }

        private static CMember AddMember(Circle c, Agent a)
        {
            string desc = Describe(a, out var sp);
            var m = new CMember { Agent = a, Who = sp, Desc = desc };
            c.Members.Add(m);
            return m;
        }

        /// <summary>Kept on the spot, turned to someone. If we can't hold them, they at least look.</summary>
        private static void HoldMember(CMember m, Agent face)
        {
            if (m?.Agent == null || !m.Agent.IsActive()) return;
            if (!SceneMove.Hold(m.Agent, face)) { try { if (face != null && m.Agent.IsAIControlled) m.Agent.SetLookAgent(face); } catch { } }
        }

        private static void LookAt(Agent a, Agent face)
        {
            if (a == null || face == null || a == face || !a.IsActive()) return;
            if (SceneMove.IsHeld(a)) SceneMove.Face(a, face);
            else { try { if (a.IsAIControlled) a.SetLookAgent(face); } catch { } }
        }

        private static void LetGo(CMember m)
        {
            try
            {
                if (m?.Agent == null) return;
                Bubbles.Thinking(m.Agent, false);
                // someone sent off since (to follow you, to fetch someone) keeps that errand
                if (SceneActions.Busy(m.Agent)) return;
                if (m.Walking || SceneMove.IsHeld(m.Agent)) SceneMove.Release(m.Agent);
                else if (m.Agent.IsActive() && m.Agent.IsAIControlled) m.Agent.SetLookAgent(null);
            }
            catch { }
        }

        /// <summary>A new circle: the first walks over to the second if they stand apart, and its first lines are asked for at once.</summary>
        private static void StartCircle(Settlement st, Agent first, Agent second)
        {
            var cfg = S.Get;
            var c = new Circle { Id = ++_circleIds, M = Mission.Current, Place = st.Name.ToString() };
            var ma = AddMember(c, first);
            var mb = AddMember(c, second);
            float apart = first.Position.Distance(second.Position);
            c.Spread = apart;
            if (cfg.AmbientWalk && apart > ArriveDistance && SceneMove.GoTo(first, second)) { ma.Walking = true; ma.WalkTo = second; ma.WalkLeft = 40f; c.Spread = 0f; }
            HoldMember(mb, first);
            if (!ma.Walking) HoldMember(ma, second);
            c.Full = ma.Who.IsHero && mb.Who.IsHero;
            // Named people talk at length. With a commoner or a soldier involved, two or three short lines in one go
            c.LinesLeft = c.Full ? Math.Max(2, cfg.AmbientMaxLines) : Math.Max(2, cfg.AmbientCommonerLines);
            c.Step = c.Full ? (cfg.AmbientTurnByTurn ? Math.Max(1, cfg.AmbientTurnSize) : 4) : c.LinesLeft;
            c.Key = KeyOf(c);
            string playerKnown = null;
            if (Agent.Main != null && (Agent.Main.Position.Distance(first.Position) <= 8f || Agent.Main.Position.Distance(second.Position) <= 8f))
                playerKnown = ContextBuilder.PlayerNameFor(ma.Who) ?? ContextBuilder.PlayerNameFor(mb.Who);
            c.Angle = PickAngle(st, ma.Who, mb.Who, c.Key, SharedPeople(st, ma.Who, mb.Who), playerKnown);
            Circles.Add(c);
            Log.Info($"[scene] encounter #{c.Id} ({c.Place}) started by {ma.Who.Name} with {mb.Who.Name} ({apart:0.0} m apart{(ma.Walking ? ", " + ma.Who.Name + " walks over" : "")}; topic: {c.Angle})");
            AskRound(c);
        }

        /// <summary>Someone near the circle walks over and takes a place in it.</summary>
        private static CMember JoinCircle(Circle c, Agent a, string why)
        {
            var m = AddMember(c, a);
            var near = c.Members.Where(x => x != m && !x.Walking && x.Agent.IsActive()).OrderBy(x => x.Agent.Position.Distance(a.Position)).FirstOrDefault();
            var to = near?.Agent;
            if (to != null && a.Position.Distance(to.Position) > ArriveDistance && SceneMove.GoTo(a, to)) { m.Walking = true; m.WalkTo = to; m.WalkLeft = 30f; }
            else HoldMember(m, to);
            c.Said.Add($"({m.Who.Name} {(m.Walking ? "is coming over to join them" : "has joined them")}.)");
            Log.Info($"[scene] encounter #{c.Id}: {m.Who.Name} joining ({why})");
            return m;
        }

        /// <summary>People standing near the circle who could step in: not busy, not in another talk, not enemies.</summary>
        private static List<Agent> CircleBystanders(Circle c, AILegacySettings cfg, int take)
        {
            var m = Mission.Current;
            if (m == null || Agent.Main == null || c.Members.Count >= Math.Max(2, cfg.AmbientMaxMembers)) return new List<Agent>();
            var center = Center(c);
            var me = Agent.Main.Position;
            return m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.IsAIControlled && a.Character is CharacterObject
                                       && a.Position.Distance(center) <= 10f && a.Position.Distance(me) <= cfg.AmbientRadius
                                       && !InCircle(a) && !GroupTalk.Has(a) && !SceneActions.Busy(a) && !Combat.FoughtHere(a) && a.MountAgent == null
                                       && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main))
                                       && (Named(a) || (OnDuty(a) ? cfg.AmbientGuards : cfg.AmbientCommoners)))
                           .OrderBy(a => a.Position.Distance(center)).Take(take).ToList();
        }

        /// <summary>Now and then someone with a tie to the circle wanders over to listen, before a round is asked for.</summary>
        private static void MaybeWanderOver(Circle c, AILegacySettings cfg)
        {
            if (c.Members.Count >= Math.Max(2, cfg.AmbientMaxMembers) || Rng.Next(100) >= 20) return;
            var cand = CircleBystanders(c, cfg, 4).Where(a => Named(a)).ToList();
            Agent pick = null;
            foreach (var a in cand)
            {
                var h = ((CharacterObject)a.Character).HeroObject;
                if (h == null) continue;
                // kin or the same house first, then anyone named who knows one of them
                if (c.Members.Any(x => x.Who.IsHero && x.Who.Hero.Clan != null && x.Who.Hero.Clan == h.Clan)) { pick = a; break; }
                if (pick == null && c.Members.Any(x => x.Who.IsHero && x.Who.Hero.GetRelation(h) >= 10)) pick = a;
            }
            if (pick != null && Reachable(pick, c.Members[0].Agent)) JoinCircle(c, pick, "walked over");
        }

        private static string Onlooker(Circle c, bool lingering)
        {
            if (Agent.Main == null || Hero.MainHero == null) return "";
            string known = null;
            var bits = new List<string>();
            foreach (var m in c.Members)
            {
                string n = ContextBuilder.PlayerNameFor(m.Who);
                if (n == null) continue;
                known = known ?? n;
                if (m.Who.IsHero)
                    bits.Add($"{m.Who.Name}: {Feeling((int)m.Who.Hero.GetRelationWithPlayer()).Replace("they ", "").Replace("each other", "them")}" + (string.IsNullOrWhiteSpace(m.Who.Memory.Summary) ? "" : $" ({Clip(m.Who.Memory.Summary, 200)})"));
            }
            string who = known == null ? "an armed traveller none of them knows" : known + (bits.Count > 0 ? ". What they make of that person — " + string.Join("; ", bits) : "");
            return lingering ? TCircleLinger.Format("who", who) : TCircleEarshot.Format("who", who);
        }

        private static string Ties(Circle c)
        {
            var lines = new List<string>();
            for (int i = 0; i < c.Members.Count && lines.Count < 6; i++)
                for (int j = i + 1; j < c.Members.Count && lines.Count < 6; j++)
                {
                    Speaker a = c.Members[i].Who, b = c.Members[j].Who;
                    string t = Tie(a, b).Replace("the other (A)", a.Name).Replace("the other (B)", b.Name);
                    lines.Add($"- {a.Name} and {b.Name}: {t}");
                }
            return lines.Count == 0 ? "(nothing in particular)" : string.Join("\n", lines);
        }

        /// <summary>Asks for the next few lines. The reply plays when the lines before it are done.</summary>
        private static void AskRound(Circle c, string extra = null)
        {
            var cfg = S.Get;
            var st = Settlement.CurrentSettlement;
            if (c.Asking || c.Done || c.LinesLeft <= 0 || st == null || Hero.MainHero == null) return;
            if (c.Started) MaybeWanderOver(c, cfg);
            int n = Math.Max(1, Math.Min(c.Step, c.LinesLeft));
            bool last = c.LinesLeft <= c.Step;
            string closing = !c.Started
                ? (last ? "These are all the lines this talk will have: it opens and comes to its natural end." : "This is only the OPENING of the conversation: do not wrap it up.")
                : last ? "These are the LAST lines: bring it to its natural end (a decision, a shrug, a laugh, one of them cutting it short)."
                       : "Do not end it yet unless it would really end here.";
            if (extra != null) closing = extra + " " + closing;
            var members = c.Members.ToList();
            var sb = new StringBuilder();
            for (int i = 0; i < members.Count; i++)
            {
                var m = members[i];
                sb.AppendLine($"{i + 1}. {m.Who.Name} — {m.Desc}. On their mind: {Mind(m.Who, m.Agent)}{(m.Walking ? " (is walking over to join them)" : "")}");
            }
            var bys = CircleBystanders(c, cfg, 3);
            string byText = bys.Count == 0 ? "(nobody)" : string.Join("; ", bys.Select((a, i) => $"B{i + 1}. {Describe(a, out var _sp)}"));
            float pd = Agent.Main != null ? Agent.Main.Position.Distance(Center(c)) : 999f;
            bool open = cfg.EnableGroupTalk && pd <= LingerDistance && c.Linger >= LingerSeconds;
            string onlooker = open ? Onlooker(c, true) : pd <= 8f ? Onlooker(c, false) : "";
            var others = SharedPeople(st, members[0].Who, members.Count > 1 ? members[1].Who : members[0].Who);
            string before = PairTopics.TryGetValue(c.Key, out var past) && past.Count > 0 ? string.Join(" | ", past.Skip(Math.Max(0, past.Count - 3))) : "(none yet today)";
            int maxChars = c.Full ? cfg.AmbientMaxChars : Math.Min(cfg.AmbientMaxChars, 90);
            string prompt = TCircle.Format("place", c.Place, "members", sb.ToString().TrimEnd(), "ties", Ties(c), "context", Context(st, members[0].Who),
                "others", others.Count > 0 ? string.Join(", ", others) : "(nobody in particular)", "before", before, "angle", c.Angle,
                "transcript", c.Said.Count == 0 ? "(nothing yet: this is how it begins)" : string.Join("\n", c.Said.Skip(Math.Max(0, c.Said.Count - 16))),
                "bystanders", byText, "onlooker", onlooker, "min_lines", c.Started ? 1 : Math.Min(n, 2), "max_lines", n, "closing", closing,
                "last_speaker", c.LastName ?? "nobody yet", "max_chars", maxChars, "language", cfg.Language, "moods", Gestures.Moods,
                "to_newcomer", open ? ", or 0 for the person lingering nearby" : "");
            c.Asking = true;
            int gen = c.Gen;
            int session = WorldSim.Session;
            string provider = members.Any(m => m.Who.IsHero) ? cfg.ProviderHeroes : cfg.ProviderResidents;
            Log.Info($"[scene] encounter #{c.Id}: asking for {n} line(s) ({members.Count} in the circle{(open ? ", the player lingers by it" : "")})");
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "ambient", maxTokensOverride: 260 + n * 110, preferredProvider: provider).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    try
                    {
                        if (!Circles.Contains(c) || gen != c.Gen) return;       // ended, or stopped for a fight, since it was asked
                        c.Asking = false;
                        ThinkingOff(c);
                        if (session != WorldSim.Session) { EndCircle(c, "reply from another game"); return; }
                        JObject j = null;
                        if (res != null && res.Ok)
                        {
                            int i0 = res.Text.IndexOf('{'), i1 = res.Text.LastIndexOf('}');
                            if (i0 >= 0 && i1 > i0) { try { j = JObject.Parse(res.Text.Substring(i0, i1 - i0 + 1)); } catch { j = null; } }
                        }
                        if (j == null)
                        {
                            Log.Warn($"[scene] encounter #{c.Id}: the AI gave no lines: {(res == null ? "no answer" : res.Ok ? "unreadable" : res.Error)}");
                            c.Done = true;        // what is queued still plays, then the circle ends
                            return;
                        }
                        var r = new CRound { Gen = gen, J = j, Members = members, Bystanders = bys, PlayerOpen = open, Max = n };
                        if (c.Cur == null && c.Queue.Count == 0) ApplyRound(c, r);
                        else c.Ready = r;
                    }
                    catch (Exception ex) { Log.Error("Scene circle reply", ex); EndCircle(c, "reply failed"); }
                });
            });
        }

        /// <summary>Checks and queues the lines of a round, lets bystanders step in, and asks for the next round right away.</summary>
        private static void ApplyRound(Circle c, CRound r)
        {
            var cfg = S.Get;
            if (r.Gen != c.Gen || !Circles.Contains(c)) return;
            var j = r.J;
            string playerName = Hero.MainHero?.Name?.ToString() ?? "the newcomer";
            int kept = 0, max = cfg.AmbientMaxChars + 60;
            bool toPlayer = false;
            if (j["lines"] is JArray arr)
                foreach (var l in arr.OfType<JObject>().Take(r.Max + 1))
                {
                    string text = ((string)l["text"] ?? "").Trim();
                    if (text.Length < 2) continue;
                    if (text.Length > max) text = text.Substring(0, max);
                    string whoRaw = (l["who"]?.ToString() ?? "").Trim();
                    int.TryParse(new string(whoRaw.Where(char.IsDigit).ToArray()), out int idx);
                    if (idx < 1) continue;
                    CMember who = null;
                    if (whoRaw.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                    {
                        // A bystander steps in: walks over, takes a place, and says this once there
                        if (idx > r.Bystanders.Count) continue;
                        var ba = r.Bystanders[idx - 1];
                        if (ba == null || !ba.IsActive() || InCircle(ba) || GroupTalk.Has(ba) || SceneActions.Busy(ba) || c.Members.Count >= Math.Max(2, cfg.AmbientMaxMembers)) continue;
                        var near = c.Members.FirstOrDefault(x => !x.Walking && x.Agent.IsActive());
                        if (near == null || !Reachable(ba, near.Agent)) { Log.Info($"[scene] encounter #{c.Id}: {ba.Name} meant to step in, but has no way over"); continue; }
                        who = JoinCircle(c, ba, "stepped in");
                    }
                    else
                    {
                        if (idx > r.Members.Count) continue;
                        who = r.Members[idx - 1];
                        if (!c.Members.Contains(who)) continue;      // has left since the round was asked for
                    }
                    if (who.Agent == null || !who.Agent.IsActive()) continue;
                    if (who.Who.Key == c.LastKey) { Log.Info($"[scene] encounter #{c.Id}: dropped a second line in a row from {who.Who.Name}"); continue; }
                    // Who it is said to: another one present, the one lingering nearby (0), or by default whoever spoke last
                    string toRaw = (l["to"]?.ToString() ?? "").Trim();
                    CMember to = null;
                    bool tp = false;
                    if (int.TryParse(new string(toRaw.Where(char.IsDigit).ToArray()), out int ti) && !toRaw.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                    {
                        if (ti == 0) tp = r.PlayerOpen;
                        else if (ti <= r.Members.Count && r.Members[ti - 1] != who && c.Members.Contains(r.Members[ti - 1])) to = r.Members[ti - 1];
                    }
                    if (to == null && !tp) to = c.Members.FirstOrDefault(x => x.Who.Key == c.LastKey && x != who) ?? c.Members.FirstOrDefault(x => x != who);
                    c.Queue.Enqueue(new CLine { Who = who, To = to, ToPlayer = tp, Text = text, Mood = (string)l["mood"] });
                    c.Said.Add($"{who.Who.Name} (to {(tp ? playerName : to?.Who.Name ?? "the others")}): {text}");
                    c.LastKey = who.Who.Key; c.LastName = who.Who.Name;
                    kept++;
                    // the talk turns to you after this line: whatever was written after it is left unsaid
                    if (tp) { toPlayer = true; break; }
                }
            while (c.Said.Count > 40) c.Said.RemoveAt(0);
            c.LinesLeft -= Math.Max(1, kept);
            c.Started = true;
            string topic = ((string)j["topic"] ?? "").Trim();
            if (topic.Length > 5) c.Topic = topic;
            bool done = j["done"] != null && j["done"].Type == JTokenType.Boolean && (bool)j["done"];
            if (done && j["after"] is JArray af)
                foreach (var e in af.OfType<JObject>().Take(4))
                {
                    int.TryParse(new string((e["from"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int fi);
                    int.TryParse(new string((e["to"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int ti2);
                    int.TryParse((e["change"] ?? "0").ToString().Trim(), out int ch);
                    if (fi >= 1 && ti2 >= 1 && fi <= r.Members.Count && ti2 <= r.Members.Count && fi != ti2 && ch != 0) c.After.Add((fi, ti2, Math.Max(-2, Math.Min(2, ch)), r.Members));
                }
            if (kept == 0 || done || toPlayer || c.LinesLeft <= 0) c.Done = true;
            Log.Info($"[scene] encounter #{c.Id}: {kept} line(s){(toPlayer ? ", ends turning to the player" : c.Done ? ", the talk is ending" : "")}");
            if (!c.Done) AskRound(c);
        }

        /// <summary>The background circles, one frame: fights, dialogue windows, who comes and goes, and the line being said in each.</summary>
        private static void TickCircles(float dt, AILegacySettings cfg)
        {
            try
            {
                if (!cfg.EnableAmbient || !CirclesOk(out bool fight))
                {
                    if (Circles.Count > 0) foreach (var c in Circles.ToList()) EndCircle(c, Mission.Current == null ? "mission ended" : "left the scene");
                    _fightWas = false;
                    return;
                }
                // A fight breaks out: everyone stops to watch it. When it is over they take the talk up again
                if (fight && !_fightWas)
                {
                    if (Circles.Count > 0) Log.Info($"[scene] a fight broke out nearby: {Circles.Count} circle(s) stop to watch it");
                    foreach (var c in Circles.ToList()) StartWatching(c);
                }
                else if (!fight && _fightWas) foreach (var c in Circles.ToList()) StopWatching(c);
                _fightWas = fight;
                bool quiet = fight || Combat.QuietAfterFight;
                if (_quietWas && !quiet) Log.Info("[scene] the fight nearby is over: new circles may start again");
                _quietWas = quiet;
                if (Circles.Count == 0) return;

                bool window = Campaign.Current.ConversationManager?.IsConversationInProgress == true;
                bool talking = window || ChatSession.IsBusy;
                if (talking != _circlesWaited) { _circlesWaited = talking; Log.Info(talking ? "[scene] dialog window opened: circles wait for it to close" : "[scene] dialog window closed: circles carry on"); }
                foreach (var c in Circles.ToList())
                {
                    try { TickCircle(c, dt, cfg, talking); }
                    catch (Exception ex) { Log.Error("Scene circle", ex); EndCircle(c, "tick failed"); }
                }
            }
            catch (Exception ex) { Log.Error("Scene circles", ex); }
        }

        /// <summary>Circles live in settlement scenes. A fight there (the scene turns to battle mode) only stops them.</summary>
        private static bool CirclesOk(out bool fight)
        {
            fight = false;
            try
            {
                var m = Mission.Current;
                if (m == null || Campaign.Current == null || Agent.Main == null || !Agent.Main.IsActive() || Settlement.CurrentSettlement == null) return false;
                if (m.Mode == MissionMode.Duel || m.Mode == MissionMode.Stealth || m.Mode == MissionMode.Deployment) return false;
                var fh = m.GetMissionBehavior<MissionFightHandler>();
                fight = m.Mode == MissionMode.Battle || (fh != null && fh.IsThereActiveFight()) || Combat.Fighting;
                return true;
            }
            catch { return false; }
        }

        private static void TickCircle(Circle c, float dt, AILegacySettings cfg, bool talking)
        {
            if (c.M != Mission.Current) { EndCircle(c, "mission ended"); return; }
            if (c.Watching) return;
            if (talking) { if (!c.Paused) PauseCircle(c); return; }
            if (c.Paused) { ResumeCircle(c); if (!Circles.Contains(c)) return; }

            if (!Prune(c, dt)) return;
            // Too far for you to hear anything of it: it ends rather than spend requests on nobody
            float pd = Agent.Main.Position.Distance(Center(c));
            if (pd > cfg.AmbientRadius + 10f) { EndCircle(c, "the player walked away"); return; }
            if (pd <= LingerDistance) c.Linger += dt; else if (pd > LingerDistance + 3f) c.Linger = 0f;
            if (!c.Noticed && c.Linger >= LingerSeconds && cfg.EnableGroupTalk && !c.Done)
            {
                // You have been standing by them a while: what was written before you came close is written again with you in it
                c.Noticed = true;
                if (c.Ready != null && !c.Ready.PlayerOpen) c.Ready = null;
                else if (c.Asking) { c.Gen++; c.Asking = false; ThinkingOff(c); }
                Log.Info($"[scene] encounter #{c.Id}: noticed the nearby player; its next lines may turn to them");
            }

            // A voiced line waits for its audio to start, so the words and the text come out together (10 s at most)
            if (c.VoiceWait)
            {
                c.VoiceWaitLeft -= dt;
                double left = AILegacy.Voice.Speech.SecondsLeft;
                if (left <= 0 && AILegacy.Voice.Speech.IsSpeaking && c.VoiceWaitLeft > 0f) return;
                c.VoiceWait = false;
                if (c.Cur != null && c.Members.Contains(c.Cur.Who) && c.Cur.Who.Agent.IsActive()) Present(c, c.Cur, cfg, left > 0 ? (float)left : 0f, true);
                else { c.Cur = null; if (_voiceOwner == c) _voiceOwner = null; }
                return;
            }
            c.Left -= dt;
            if (c.Left > 0f) return;
            if (c.AfterVoice)
            {
                // the next speaker waits until the voice is done, then a breath
                if (AILegacy.Voice.Speech.IsSpeaking && (c.VoiceCap -= dt) > 0f) return;
                c.AfterVoice = false;
                if (_voiceOwner == c) _voiceOwner = null;
                c.Left = VoiceGap;
                return;
            }
            EndLine(c);
            if (c.Queue.Count == 0 && c.Ready != null) { var r = c.Ready; c.Ready = null; ApplyRound(c, r); if (!Circles.Contains(c)) return; }
            if (c.Queue.Count == 0)
            {
                if (c.Asking) { ThinkingOn(c); return; }
                if (c.Done || c.LinesLeft <= 0) { EndCircle(c, c.LinesLeft <= 0 ? "said everything" : "conversation ran dry"); return; }
                AskRound(c);
                return;
            }
            var next = c.Queue.Peek();
            if (!c.Members.Contains(next.Who) || next.Who.Agent == null || !next.Who.Agent.IsActive()) { c.Queue.Dequeue(); return; }
            // The speaker, or the one spoken to, is still walking over: the line waits for them (their walk has a time limit)
            if (next.Who.Walking || (next.To != null && next.To.Walking && c.Members.Contains(next.To))) return;
            // Voiced only where you could hear it, and only when no other voice is speaking
            bool voiced = cfg.VoiceInGroups && AILegacy.Voice.Speech.Enabled && Bubbles.CanHear(next.Who.Agent) && _voiceOwner == null
                          && !AILegacy.Voice.Speech.IsSpeaking && _voiceWait == null && !_afterVoice;
            c.Queue.Dequeue();
            c.Cur = next;
            if (voiced)
            {
                AILegacy.Voice.Speech.Say((next.Who.Agent.Character?.StringId ?? "") + "|" + next.Who.Agent.Name, next.Who.Agent.IsFemale, next.Text);
                if (AILegacy.Voice.Speech.IsSpeaking) { _voiceOwner = c; c.VoiceWait = true; c.VoiceWaitLeft = 10f; return; }
            }
            Present(c, next, cfg, 0f, false);
        }

        /// <summary>Drops whoever is gone, taken elsewhere or wandered off, and sees newcomers arrive. False if the circle ended.</summary>
        private static bool Prune(Circle c, float dt)
        {
            foreach (var m in c.Members.ToList())
            {
                var a = m.Agent;
                string why = null;
                if (a == null || !a.IsActive()) why = "agent gone";
                else if (GroupTalk.Has(a)) why = "taken into the player's talk";
                else if (SceneActions.Busy(a)) why = "busy elsewhere";
                else if (Combat.FoughtHere(a)) why = "fought here";
                else if (m.Walking)
                {
                    m.WalkLeft -= dt;
                    if (m.WalkTo == null || !m.WalkTo.IsActive() || !c.Members.Any(x => x.Agent == m.WalkTo && x != m))
                    {
                        // the one they were walking to left: head for someone else still there
                        var other = c.Members.FirstOrDefault(x => x != m && !x.Walking && x.Agent.IsActive());
                        if (other == null || !SceneMove.GoTo(a, other.Agent)) why = "could not reach";
                        else m.WalkTo = other.Agent;
                    }
                    else if (m.WalkLeft <= 0f) why = "could not reach";
                    else if (a.Position.Distance(m.WalkTo.Position) <= ArriveDistance)
                    {
                        m.Walking = false;
                        HoldMember(m, m.WalkTo);
                        Log.Info($"[scene] encounter #{c.Id}: {m.Who.Name} reached the circle");
                    }
                }
                else if (c.Members.Count(x => x != m && !x.Walking) > 0 && a.Position.Distance(Center(c, m)) > Math.Max(8f, c.Spread + 4f)) why = "walked away";
                if (why != null) RemoveMember(c, m, why);
            }
            if (c.Members.Count < 2) { EndCircle(c, "nobody left to talk to"); return false; }
            return true;
        }

        private static void RemoveMember(Circle c, CMember m, string why)
        {
            c.Members.Remove(m);
            if (why != "busy elsewhere" && why != "taken into the player's talk") LetGo(m);
            else Bubbles.Thinking(m.Agent, false);
            if (why != "agent gone") c.Said.Add($"({m.Who.Name} has left the conversation.)");
            Log.Info($"[scene] encounter #{c.Id}: {m.Who.Name} left ({why})");
        }

        /// <summary>The line appears over the speaker, who gestures and looks at the one spoken to; the others turn to the speaker.</summary>
        private static void Present(Circle c, CLine l, AILegacySettings cfg, float audio, bool voiced)
        {
            var who = l.Who.Agent;
            float show = ShowSeconds(l.Text);
            Bubbles.Show(who, l.Text, audio > 0f ? Math.Max(show, audio + 1f) : show);
            Gestures.Play(who, l.Mood);
            c.Speaking = who;
            if (cfg.AmbientToLog && Agent.Main != null && who.Position.Distance(Agent.Main.Position) <= cfg.AmbientRadius)
                InformationManager.DisplayMessage(new InformationMessage($"{who.Name}: {l.Text}", new Color(0.78f, 0.78f, 0.7f)));
            LookAt(who, l.ToPlayer ? Agent.Main : l.To?.Agent);
            foreach (var m in c.Members) if (m != l.Who && !m.Walking) LookAt(m.Agent, who);
            if (voiced && (audio > 0f || AILegacy.Voice.Speech.IsSpeaking))
            {
                c.AfterVoice = true;
                c.VoiceCap = show + 20f;
                c.Left = Math.Min(1f, show);
            }
            else
            {
                c.AfterVoice = false;
                if (_voiceOwner == c) _voiceOwner = null;
                c.Left = Math.Min(show, 2f + 0.05f * l.Text.Length) + SilentPause;
            }
            l.Who.Spoke++;
            RememberCircle(c, l);
            if (l.ToPlayer) TurnToPlayer(c, l);
        }

        private static void EndLine(Circle c)
        {
            if (c.Speaking != null) { try { Gestures.End(c.Speaking); } catch { } c.Speaking = null; }
            c.Cur = null;
        }

        private static void ThinkingOn(Circle c)
        {
            if (c.ThinkingOn != null) return;
            var m = c.Members.Where(x => !x.Walking && x.Who.Key != c.LastKey && x.Agent.IsActive()).OrderBy(x => x.Spoke).FirstOrDefault();
            if (m == null || !Bubbles.CanHear(m.Agent)) return;
            c.ThinkingOn = m.Agent;
            Bubbles.Thinking(m.Agent, true);
        }

        private static void ThinkingOff(Circle c)
        {
            if (c.ThinkingOn == null) return;
            Bubbles.Thinking(c.ThinkingOn, false);
            c.ThinkingOn = null;
        }

        /// <summary>
        /// Each line goes into the history of everyone in the circle (one note per circle that grows line by line, so they
        /// remember it when you talk to them) and reaches whoever stands within earshot.
        /// </summary>
        private static void RememberCircle(Circle c, CLine l)
        {
            try
            {
                double day = CampaignTime.Now.ToDays;
                c.Heard.Add($"{l.Who.Who.Name}: «{Clip(l.Text, 160)}»");
                int total = c.Heard.Sum(x => x.Length);
                while (c.Heard.Count > 1 && total > 900) { total -= c.Heard[0].Length; c.Heard.RemoveAt(0); }
                string body = string.Join(" / ", c.Heard);
                string at = string.IsNullOrEmpty(c.Place) ? "" : " in " + c.Place;
                string near = Agent.Main != null && Bubbles.CanHear(l.Who.Agent) ? ", within earshot of the one you are speaking with now" : "";
                foreach (var m in c.Members)
                {
                    var names = c.Members.Where(x => x != m).Select(x => x.Who.Name).ToList();
                    if (names.Count == 0) continue;
                    string with = names.Count == 1 ? names[0] : string.Join(", ", names.Take(names.Count - 1)) + " and " + names[names.Count - 1];
                    m.Note = Note(m.Who, m.Note, $"[In a group: you were talking with {with}{at}{near} — {body}]", day);
                }
                _memDirty = true;
            }
            catch (Exception ex) { Log.Error("Remember a circle line", ex); }
            // Bystanders within earshot catch it too. The people of the circle heard it directly
            try { Overhear(c.Members.Where(m => m != l.Who).Select(m => m.Who).ToList(), l.Text, l.Who.Who, 1f, l.Who.Agent); } catch { }
        }

        /// <summary>
        /// Someone in the circle spoke to you: you are part of it now. It goes on as your group talk, with what was said
        /// and who is in it, and they wait for your answer. If you were already talking with a group, they join it.
        /// </summary>
        private static void TurnToPlayer(Circle c, CLine l)
        {
            var cfg = S.Get;
            var speaker = l.Who.Agent;
            if (!cfg.EnableGroupTalk || Agent.Main == null || !Agent.Main.IsActive() || speaker.Position.Distance(Agent.Main.Position) > LingerDistance + 3f)
            {
                Log.Info($"[scene] encounter #{c.Id}: {l.Who.Who.Name} turned to the player, who is no longer close: the talk ends");
                c.Done = true;
                return;
            }
            var agents = new List<Agent> { speaker };
            agents.AddRange(c.Members.Where(m => m != l.Who && !m.Walking && m.Agent.IsActive()).Select(m => m.Agent));
            var said = c.Said.ToList();
            bool own = GroupTalk.Active;
            EndCircle(c, "turned to the player", false);
            if (!GroupTalk.TakeOver(agents, said, speaker, true)) { Log.Info($"[scene] {l.Who.Who.Name} could not join the player's conversation"); return; }
            Log.Info(own ? $"[scene] encounter #{c.Id} turned to the player, who talks in their own circle: it joins that talk after this line"
                         : $"[scene] encounter #{c.Id}: {l.Who.Who.Name} spoke to the player, who is now part of this conversation");
            InformationManager.DisplayMessage(new InformationMessage(Hotkeys.Fix(L.F("AIL_e7b1ce00", $"{l.Who.Who.Name} se dirige a ti: ya formas parte de su conversación (Alt+T para contestar).")), Colors.Gray));
        }

        /// <summary>
        /// You open your group talk standing in a circle, or looking at someone in one: it becomes yours, with what was
        /// said and who is in it. Called by GroupTalk before it gathers the people around you.
        /// </summary>
        internal static void HandOverNear(float radius, Agent lookedAt = null)
        {
            try
            {
                if (Agent.Main == null || Circles.Count == 0) return;
                var me = Agent.Main.Position;
                var c = (lookedAt != null ? Circles.FirstOrDefault(x => !x.Watching && x.Members.Any(m => m.Agent == lookedAt)) : null)
                        ?? Circles.Where(x => !x.Watching && x.Members.Any(m => !m.Walking && m.Agent.IsActive() && m.Agent.Position.Distance(me) <= radius))
                                  .OrderBy(x => Center(x).Distance(me)).FirstOrDefault();
                if (c == null) return;
                var agents = c.Members.Where(m => !m.Walking && m.Agent.IsActive()).OrderBy(m => m.Agent.Position.Distance(me)).Select(m => m.Agent).ToList();
                var last = c.Members.FirstOrDefault(m => m.Who.Key == c.LastKey)?.Agent;
                var said = c.Said.ToList();
                EndCircle(c, "the player stepped into it");
                if (GroupTalk.TakeOver(agents, said, last, false)) Log.Info($"[scene] encounter #{c.Id}: the player stepped in, it goes on as the player's conversation");
            }
            catch (Exception ex) { Log.Error("Step into a circle", ex); }
        }

        /// <summary>A dialogue window opened: the circle stops where it is and waits. Whoever you talk to belongs to the dialogue now.</summary>
        private static void PauseCircle(Circle c)
        {
            c.Paused = true;
            StopVoice(c, true);
            EndLineKeep(c);
            ThinkingOff(c);
            try
            {
                var inTalk = Campaign.Current?.ConversationManager?.ConversationAgents;
                if (inTalk != null)
                    foreach (var m in c.Members)
                        if (inTalk.Any(x => ReferenceEquals(x, m.Agent)) && SceneMove.IsHeld(m.Agent)) SceneMove.Release(m.Agent);
            }
            catch { }
        }

        /// <summary>The window closed: they take the talk up again if they are still together, or it ends.</summary>
        private static void ResumeCircle(Circle c)
        {
            c.Paused = false;
            foreach (var m in c.Members.ToList())
            {
                if (m.Agent == null || !m.Agent.IsActive()) { RemoveMember(c, m, "agent gone"); continue; }
                if (!m.Walking && c.Members.Count(x => x != m && !x.Walking) > 0 && m.Agent.Position.Distance(Center(c, m)) > Math.Max(8f, c.Spread + 4f)) RemoveMember(c, m, "walked away");
            }
            if (c.Members.Count < 2) { EndCircle(c, "after the dialogue window they are no longer together"); return; }
            Rehold(c);
            c.Left = Math.Max(c.Left, 2f);
        }

        /// <summary>A fight nearby: the circle falls silent and lets go of its people, who react to it as the game has them.</summary>
        private static void StartWatching(Circle c)
        {
            c.Watching = true;
            StopVoice(c, true);
            EndLineKeep(c);
            ThinkingOff(c);
            foreach (var m in c.Members) LetGo(m);
            Log.Info($"[scene] encounter #{c.Id}: watches the fight nearby; its talk waits");
        }

        /// <summary>The fight is over: those still together take the talk up again, starting with what they just saw.</summary>
        private static void StopWatching(Circle c)
        {
            c.Watching = false;
            foreach (var m in c.Members.ToList())
            {
                string why = m.Agent == null || !m.Agent.IsActive() ? "agent gone" : Combat.FoughtHere(m.Agent) ? "fought here" : SceneActions.Busy(m.Agent) ? "busy elsewhere" : null;
                if (why != null) RemoveMember(c, m, why);
            }
            foreach (var m in c.Members.ToList())
                if (c.Members.Count(x => x != m) > 0 && m.Agent.Position.Distance(Center(c, m)) > Math.Max(10f, c.Spread + 6f)) RemoveMember(c, m, "walked away during the fight");
            if (c.Members.Count < 2) { EndCircle(c, "the fight broke it up"); return; }
            // What they were about to say was written before the fight: it is dropped, and they react to what they saw
            int dropped = c.Queue.Count + (c.Ready != null ? 1 : 0);
            c.Queue.Clear(); c.Ready = null; c.Cur = null;
            c.Gen++; c.Asking = false;
            c.Said.Add("(A fight broke out a few steps away and is over now. They all watched it; none of them fought in it.)");
            c.Done = false;
            c.LinesLeft = Math.Max(c.LinesLeft, 2);
            Rehold(c);
            Log.Info($"[scene] encounter #{c.Id}: the fight it watched is over{(dropped > 0 ? $"; {dropped} prepared round(s) dropped" : "")}");
            AskRound(c, "They have just watched that fight: the first line reacts to it.");
        }

        /// <summary>Everyone still in the circle is held on the spot again, turned to the middle of the group.</summary>
        private static void Rehold(Circle c)
        {
            foreach (var m in c.Members)
            {
                if (m.Walking) { if (m.WalkTo != null && m.WalkTo.IsActive()) SceneMove.GoTo(m.Agent, m.WalkTo); continue; }
                var face = c.Members.Where(x => x != m && x.Agent.IsActive()).OrderBy(x => x.Agent.Position.Distance(m.Agent.Position)).FirstOrDefault();
                HoldMember(m, face?.Agent);
            }
        }

        /// <summary>The voice of this circle stops. requeue: a line still waiting for its audio is said again afterwards.</summary>
        private static void StopVoice(Circle c, bool requeue)
        {
            if (_voiceOwner != c) { c.VoiceWait = false; c.AfterVoice = false; return; }
            if (c.VoiceWait && requeue && c.Cur != null)
            {
                var rest = c.Queue.ToList();
                c.Queue.Clear();
                c.Queue.Enqueue(c.Cur);
                foreach (var x in rest) c.Queue.Enqueue(x);
                c.Cur = null;
            }
            if (c.VoiceWait || c.AfterVoice) { try { AILegacy.Voice.Speech.Stop(); } catch { } }
            c.VoiceWait = false; c.AfterVoice = false;
            _voiceOwner = null;
        }

        /// <summary>The gesture of the line on now ends; a line put back in the queue stays there.</summary>
        private static void EndLineKeep(Circle c)
        {
            if (c.Speaking != null) { try { Gestures.End(c.Speaking); } catch { } c.Speaking = null; }
            c.Cur = null;
            c.Left = 0f;
        }

        /// <summary>
        /// The circle is over: its people go back to their routine, keep what was talked about (and how they now feel
        /// about each other), and a quiet stretch follows before a new one starts.
        /// </summary>
        private static void EndCircle(Circle c, string why, bool stopVoice = true)
        {
            if (!Circles.Remove(c)) return;
            c.Gen++;
            int dropped = c.Queue.Count + (c.Ready != null ? 1 : 0);
            if (_voiceOwner == c)
            {
                // a line already showing with its voice is left to finish; one still waiting for its audio is cut
                if (stopVoice && c.VoiceWait) { try { AILegacy.Voice.Speech.Stop(); } catch { } }
                _voiceOwner = null;
            }
            ThinkingOff(c);
            if (c.Speaking != null) { try { Gestures.End(c.Speaking); } catch { } c.Speaking = null; }
            foreach (var m in c.Members) LetGo(m);
            try
            {
                if (!string.IsNullOrWhiteSpace(c.Topic) && c.Heard.Count > 0)
                {
                    // Remembered so it isn't repeated, and a named person will know it when you talk to them
                    if (!PairTopics.TryGetValue(c.Key, out var past)) PairTopics[c.Key] = past = new List<string>();
                    past.Add(c.Topic);
                    while (past.Count > 6) past.RemoveAt(0);
                    foreach (var (from, to, change, members) in c.After)
                        Bonds.TouchOther(members[from - 1].Who, members[to - 1].Who, change * 4, c.Topic);
                    foreach (var m in c.Members.Where(x => x.Note != null || x.Spoke > 0))
                    {
                        var names = c.Members.Where(x => x != m).Select(x => x.Who.Name).ToList();
                        m.Who.Memory.RecentTalk = $"Moments ago you were talking with {(names.Count == 0 ? "someone" : string.Join(", ", names))} nearby: {c.Topic}";
                        m.Who.Memory.RecentTalkAt = DateTime.UtcNow;
                    }
                    Log.Info($"[scene] encounter #{c.Id} ({string.Join(", ", c.Members.Select(x => x.Who.Name))}): {c.Topic}");
                }
            }
            catch (Exception ex) { Log.Error("End of a scene circle", ex); }
            // A circle that ends on its own may leave something undone that one of them acts on now
            if (c.Heard.Count > 0 && (why == "said everything" || why == "conversation ran dry" || why == "nobody left to talk to" || why == "the player walked away"))
                SceneMatters.After($"encounter #{c.Id}", c.Place, c.Members.Where(x => x.Agent != null && x.Who != null).Select(x => (x.Agent, x.Who, x.Desc)).ToList(), c.Said.ToList(), false);
            if (c.Heard.Count > 0)
            {
                // Quiet stretch before the next one
                _lastPairAt = DateTime.UtcNow;
                _timer = Math.Max(_timer, S.Get.AmbientCooldownSeconds * (0.8f + (float)Rng.NextDouble() * 0.4f));
            }
            if (_memDirty) { _memDirty = false; try { SaveFiles.Flush(); } catch { } }
            Log.Info($"[scene] encounter #{c.Id} ({c.Place}) dissolved: {why}{(dropped > 0 ? $"; {dropped} prepared line(s) or round(s) dropped" : "")}");
        }
    }
}
