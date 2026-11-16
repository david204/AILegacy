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
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.InputSystem;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Group talk in a scene: you speak to everyone nearby and the AI decides who answers and in
    /// what order. Replies show over their heads and in the game log.
    /// After answering you they go on among themselves for a few lines, wind down and wait for you.
    /// Each round is asked for as soon as the one before it starts playing, so it is ready when the last bubble ends.
    /// What the talk changed (summary, bonds, opinions, rumours) is worked out by a separate cheap request every few rounds.
    /// </summary>
    internal static class GroupTalk
    {
        private class Member
        {
            public Agent Agent; public Speaker Who; public string Desc;
            /// <summary>Turns in their memory when they joined. What comes after belongs to this circle and is already in the transcript.</summary>
            public int TurnsAtJoin;
            public DateTime JoinedAt;
            /// <summary>Their last lines in this circle, to catch them saying the same thing again.</summary>
            public readonly List<string> Recent = new List<string>();
        }

        /// <summary>What a round is for: answering you, going on among themselves, winding down, nudging you after a question, or the last word of someone left alone with you.</summary>
        private enum Kind { Player, CarryOn, WindDown, Nudge, Alone }

        /// <summary>A reply that came back, kept until the lines before it have been spoken.</summary>
        private class Round
        {
            public int Gen; public Kind Kind; public JObject J; public List<Member> Circle; public Mission M; public int MaxLines;
            public List<Agent> Bystanders = new List<Agent>(); public List<Agent> Away = new List<Agent>(); public string PlayerText;
        }

        private static readonly List<Member> Circle = new List<Member>();
        private static readonly List<string> Transcript = new List<string>();     // "Name (to X): line", in order
        private static Mission _mission;
        // A request for the current round is out, and what kind it is
        private static bool _waiting;
        private static Kind _waitingKind;
        // Bumped when you speak or the circle ends: replies asked for before that are thrown away
        private static volatile int _gen;
        private static Round _ready;
        // The reply as it is being written (set on the HTTP thread) and who could be speaking, for the live bubble
        private static volatile string _liveRaw;
        private static string _livePainted;
        private static List<Member> _liveCircle;
        private static float _idle;
        private static Agent _addressed;      // who you were looking at when the window opened
        // Pace of the talk without you
        private static float _silence;
        private static int _carryLeft;        // lines they may still say among themselves before winding down
        private static int _windLeft;         // lines left to wind down
        private static int _nudges;           // nudges since you last spoke
        private static bool _awaitPlayer;     // the last line put a question to you
        private static string _asker;
        private static string _lastKey, _lastName;
        // What has been said since the last bookkeeping request
        private static readonly List<string> BookLines = new List<string>();
        private static readonly List<Member> BookMembers = new List<Member>();
        private static int _roundsSinceBook;
        private static bool _bookPlayerSpoke;
        private const int WindDownLines = 2, BookEveryRounds = 3;
        private static DateTime _lastRumor = DateTime.MinValue;
        private const int SlowId = 751204;
        private static Mission _slowed;

        private static void Unslow()
        {
            var m = _slowed; _slowed = null;
            try { if (m != null && Mission.Current == m) m.RemoveTimeSpeedRequest(SlowId); } catch { }
        }

        /// <summary>The character closest to the center of your view, within maxDist.</summary>
        private static Agent LookedAt(float maxDist)
        {
            try
            {
                var main = Agent.Main;
                var eye = main.GetEyeGlobalPosition();
                var dir = main.LookDirection;
                Agent best = null; float bestDot = 0.93f;
                foreach (var a in Mission.Current.Agents)
                {
                    if (a == null || !a.IsHuman || !a.IsActive() || a == main || !(a.Character is CharacterObject)) continue;
                    var d = a.GetChestGlobalPosition() - eye;
                    float dist = d.Normalize();
                    if (dist > maxDist || dist < 0.2f) continue;
                    float dot = Vec3.DotProduct(dir, d);
                    if (dot > bestDot) { bestDot = dot; best = a; }
                }
                return best;
            }
            catch { return null; }
        }

        private static readonly Tpl TGroup = new Tpl("group_conversation/group_reply.txt",
@"Several people are standing together in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord, and {player} has just spoken to the group.
The people present (each speaks only for themselves, in their own voice and station):
{members}
What is going on around them:
{context}
The talk so far:
{transcript}
Elsewhere in this place, out of earshot (someone here could walk over to them): {elsewhere}
Bystanders within earshot who are NOT part of the conversation:
{bystanders}
{player} now says: «{said}»
How it was said: {manner}
A bystander steps in only rarely, when what they overheard truly concerns them (their lord, their trade, their kin, their post) or is outrageous: then use their code (for example ""B1"") as ""who"" and they walk over and join. Otherwise bystanders stay out of it.

Everything under 'The talk so far' and each person's 'JUST NOW' HAS HAPPENED and they remember it: nobody asks again what they were already told, nobody is surprised twice by the same thing, and someone who has just come back from speaking to another person answers from what they learned there.
Write how the group takes it, as it would really happen: usually the one addressed by name or the one it concerns most answers first; then someone else may cut in, agree, contradict, mock, or turn to another person present about it. Every line answers the line just before it. {player} is one of the people in this talk, not the only one: a line may be spoken to another person present. People with nothing to say stay silent. Nobles speak before commoners dare to.
Never the same person twice in a row, and nobody repeats what was already said in 'The talk so far', not even in other words: each line moves the talk on.
Length by station: lords, ladies and other people listed by name may speak fully, up to the limit. Soldiers, guards and common folk answer with ONE short sentence (under 80 characters).
Going to someone: if what was said gives a person here a real reason to go and speak to someone listed under 'Elsewhere' (they are told that person is looking for them, they are sent with a message, they must warn or ask them), and they believe it enough to bother, they say so in their line and you set ""goes_to"" to that person's exact name: they will walk there and talk to them. Set ""returns"" to true if they would come back afterwards to tell {player} what came of it. Someone with no reason to trust {player}, or of too high a station to be sent on errands, may refuse or send {player} instead: then leave ""goes_to"" empty. Never use it without a reason given in what was said.
Other things a speaker can do:
{actions}
A line may carry ""goes_to"" or ""action"", never both.
Each line also carries ""mood"": how the speaker looks while saying it, one of: {moods}.
Rules: 1 to {max_lines} lines in total, each under {max_chars} characters, in {language}. Each line is spoken words only (a brief *gesture* is allowed). Nobody speaks for {player}. Nobody uses a name they have not been told or would not know. Stay in character; never mention games or AI.
Answer ONLY with JSON: { ""lines"": [ { ""who"": <number of the speaker from the list>, ""to"": <number of the person spoken to, or 0 for {player}>, ""text"": ""..."", ""mood"": ""..."", ""goes_to"": """", ""returns"": false, ""action"": """" } ] }");

        // The group going on by itself. The situation line says why they speak now.
        private static readonly Tpl TGroupOn = new Tpl("group_conversation/group_carry_on.txt",
@"Several people are standing together in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord, talking. {player} is standing with them.
The people present (each speaks only for themselves, in their own voice and station):
{members}
What is going on around them:
{context}
The talk so far (it HAS HAPPENED and they all remember it):
{transcript}

Now: {situation}

Write the next {min_lines} to {max_lines} lines of this talk, as people who know each other really go on talking:
- Every line answers the line just before it: a question gets an answer, a jab a retort, a claim agreement or doubt. Follow the subject on the table; take up a new one only if the old one has truly run out.
- Never the same person twice in a row. The last words above were {last_speaker}'s, so someone else speaks first. Prefer whoever has spoken least.
- They speak to each other. {player} is one participant among them, not the one everyone turns to.
- Nobody repeats what they or anyone else already said above, not even in other words: each line adds something (a detail, an objection, a decision, a joke, a question to someone else).
- Small and concrete beats grand. Different voices by character and station; vary the length of lines. Soldiers, guards and common folk speak in ONE short sentence (under 80 characters).
- Each line under {max_chars} characters, in {language}; spoken words only (a brief *gesture* is allowed). Nobody speaks for {player}. Nobody uses a name they have not been told or would not know. Stay in character; never mention games or AI.
Each line also carries ""mood"": how the speaker looks while saying it, one of: {moods}.
Answer ONLY with JSON: { ""lines"": [ { ""who"": <number of the speaker from the list>, ""to"": <number of the person spoken to, or 0 for {player}>, ""text"": ""..."", ""mood"": ""..."" } ] }");

        private static readonly Tpl TCarryOn = new Tpl("group_conversation/situation_carry_on.txt",
@"{player} has said nothing since the last lines and is listening. The others do not wait for {player} and do not ask why they are quiet: they carry on BETWEEN THEMSELVES with what was just said. Each line is spoken to another person present.");

        private static readonly Tpl TWindDown = new Tpl("group_conversation/situation_winding_down.txt",
@"The talk has gone on for a while without {player}. These are the LAST lines for now: let it wind down as it really would (a decision, a shrug, a laugh, someone cutting it short), or one of them turns back to {player} with a short remark. Do not start a new subject.");

        private static readonly Tpl TNudge = new Tpl("group_conversation/situation_nudge.txt",
@"A moment ago {asker} put a question to {player}, and {player} has not answered. Do NOT repeat the question and do not insist: at most someone remarks on the silence in passing, and the talk carries on with the others.");

        private static readonly Tpl TAlone = new Tpl("group_conversation/situation_alone.txt",
@"{asker} is the only one left with {player}, and {player} has said nothing for a while. {asker} says ONE short last thing to {player}, without repeating anything said before, or simply takes their leave. Exactly 1 line, from person 1.");

        // What the talk changed, asked for apart and only now and then, so the lines themselves come quickly
        private static readonly Tpl TBook = new Tpl("group_conversation/group_bookkeeping.txt",
@"A conversation took place in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord. {player_part}
The people in it, by number:
{members}
What was said, in order:
{lines}
Read it and answer ONLY with JSON: { ""summary"": ""what was said and how it went, ONE or TWO English sentences, naming who said what matters"", ""between"": [ { ""from"": <number>, ""to"": <number>, ""change"": -2 to 2, ""why"": ""few English words"" } ] (only if one person present was hurt, insulted, defended, shamed or won over by ANOTHER person present, not by {player}; usually empty), ""toward_player"": [ { ""who"": <number>, ""relation"": -2 to 2, ""trust"": -3 to 3 } ] (only for someone whose opinion of {player}, or trust in their word, truly moved because of what {player} said or did here; usually empty), ""rumor_title"": ""a 3-6 word headline of that rumour in {language}, or empty"", ""rumor_local"": ""the rumour itself in {language}, or empty"", ""rumor"": ""almost always empty. Only if {player} told these people news of public weight that they will repeat to others whether they believe it or not (a death, a crime, a betrayal, a scandal touching named nobles or a crown), or if something happened here that the whole place will talk about: ONE neutral English sentence, worded as hearsay when it is only something they were told"" }");

        // What the others in a group keep of a deed one of them really carried out. It is stored after "[Seen: ",
        // which is what marks it as a fact for the summary.
        private static readonly Tpl TOpenTask = new Tpl("group_conversation/open_task.txt",
            "They gave {player} a task that is still open: {brief} ({days} day(s) left). They may ask how it goes, in front of the others or aside, as fits them.");

        private static readonly Tpl TSeen = new Tpl("group_conversation/seen_deed.txt",
            "{name} really did this in front of you, in the group with {player}. It took effect in the world and it was {name}'s deed, not yours. From {name}'s own record, where \"you\" is {name} and \"they\" is {player}: {deed}");

        public static void Touch() { var _ = TGroup.Key + TSeen.Key; }

        private static Member NewMember(Agent a)
        {
            string desc = Ambient.Describe(a, out var sp);
            return new Member { Agent = a, Who = sp, Desc = desc, TurnsAtJoin = sp.Memory?.Turns?.Count ?? 0, JoinedAt = DateTime.UtcNow };
        }

        /// <summary>
        /// The notes of what <paramref name="doer"/> really carried out since <paramref name="turnsBefore"/> go, in the
        /// third person, into the history of everyone else in the group: they saw it, and their own record of the
        /// talk must not say that nothing was done.
        /// </summary>
        private static void ShareDeeds(Member doer, List<Member> circle, int turnsBefore, string playerName)
        {
            try
            {
                var turns = doer.Who.Memory.Turns;
                var deeds = turns.Skip(Math.Max(0, turnsBefore)).Where(t => t.IsNote && t.Text.StartsWith("[Done", StringComparison.Ordinal)).Select(t => t.Text).ToList();
                foreach (var note in deeds)
                {
                    int colon = note.IndexOf(':');
                    string deed = (colon > 0 ? note.Substring(colon + 1) : note).Trim().TrimEnd(']').Trim();
                    if (deed.Length < 3) continue;
                    string seen = "[Seen: " + TSeen.Format("name", doer.Who.Name, "player", playerName, "deed", deed) + "]";
                    foreach (var x in circle)
                    {
                        if (x == doer || x.Who == null || x.Who.Key == doer.Who.Key) continue;
                        try { x.Who.Memory.Turns.Add(new ChatTurn { Role = "player", Text = seen, Day = CampaignTime.Now.ToDays }); } catch { }
                    }
                }
            }
            catch (Exception ex) { Log.Error("Circle: what the others saw done", ex); }
        }

        /// <summary>Whether the agent is in your group talk. While it is, it won't start ambient chats of its own.</summary>
        internal static bool Has(Agent a) => Circle.Any(x => x.Agent == a);

        /// <summary>You are talking with a group right now.</summary>
        internal static bool Active => Circle.Count > 0;

        /// <summary>
        /// A conversation going on nearby becomes yours: someone in it spoke to you, or you stepped into it. Its people join
        /// your group (a new one if you had none) with what they already said; if they turned to you (askedPlayer) they now
        /// wait for your answer, and nudge you if you stay silent. False if group talk can't run here.
        /// </summary>
        internal static bool TakeOver(List<Agent> agents, List<string> said, Agent last, bool askedPlayer)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableGroupTalk || !SceneOk() || agents == null) return false;
                var m = Mission.Current;
                bool fresh = Circle.Count == 0;
                // A new circle: nothing asked for by the last one may play in it
                if (fresh) { _gen++; _ready = null; _waiting = false; _liveRaw = null; _liveCircle = null; _livePainted = null; ResetFlow(); _lastKey = null; _lastName = null; }
                if (_mission != m) { Transcript.Clear(); _mission = m; }
                var added = new List<Member>();
                foreach (var a in agents)
                {
                    if (a == null || !a.IsActive() || a == Agent.Main || Circle.Any(x => x.Agent == a)) continue;
                    if (Circle.Count >= Math.Max(1, cfg.GroupMaxParticipants)) break;
                    var mb = NewMember(a);
                    Circle.Add(mb);
                    added.Add(mb);
                }
                if (Circle.Count == 0) return false;
                if (!fresh && added.Count > 0) Transcript.Add($"({string.Join(", ", added.Select(x => x.Who.Name))} were talking nearby and have joined:)");
                foreach (var l in said ?? new List<string>()) if (!string.IsNullOrWhiteSpace(l)) Transcript.Add(l);
                while (Transcript.Count > 40) Transcript.RemoveAt(0);
                var lm = last != null ? Circle.FirstOrDefault(x => x.Agent == last) : null;
                if (lm != null) { _lastKey = lm.Who.Key; _lastName = lm.Who.Name; }
                _idle = 0f; _silence = 0f; _nudges = 0;
                if (askedPlayer && lm != null) { _awaitPlayer = true; _asker = lm.Who.Name; }
                Log.Info($"[group] took over a conversation nearby: {string.Join(", ", added.Select(x => x.Who.Name))}{(askedPlayer && lm != null ? $" ({lm.Who.Name} spoke to you)" : "")}");
                return true;
            }
            catch (Exception ex) { Log.Error("Take over a conversation nearby", ex); return false; }
        }

        /// <summary>Someone who was called over arrives and joins the talk.</summary>
        internal static void Join(Agent a)
        {
            try
            {
                if (a == null || !a.IsActive() || Circle.Any(x => x.Agent == a) || Circle.Count > S.Get.GroupMaxParticipants) return;
                if (Mission.Current != _mission) { Transcript.Clear(); _mission = Mission.Current; }
                var mb = NewMember(a);
                Circle.Add(mb);
                Transcript.Add($"({mb.Who.Name} has come over and joined.)");
                _idle = 0f;
            }
            catch (Exception ex) { Log.Error("Join the group", ex); }
        }

        internal static void Note(string line) { if (_mission != null && Mission.Current == _mission && !string.IsNullOrWhiteSpace(line)) Transcript.Add(line); }

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        /// <summary>The first line of the reply being written, over its speaker's head. Bystanders who would butt in wait for the whole reply.</summary>
        private static void PaintLive()
        {
            string raw = _liveRaw;
            var circle = _liveCircle;
            if (raw == null || circle == null || !_waiting) return;
            // A round written ahead waits for the lines before it; only your answer shows while they still speak
            if (_waitingKind != Kind.Player && Ambient.GroupPlaying) return;
            if (!FirstLineSoFar(raw, out int idx, out string text) || idx < 1 || idx > circle.Count || text.Length < 2) return;
            if (text == _livePainted) return;
            var who = circle[idx - 1].Agent;
            if (who == null || !who.IsActive()) return;
            _livePainted = text;
            Bubbles.ShowLive(who, text, 8f);
        }

        /// <summary>
        /// Reads the first entry of "lines" from a reply that is still arriving: the speaker's number and the text so far.
        /// False while the speaker is not known yet, or when it is a bystander ("B1").
        /// </summary>
        internal static bool FirstLineSoFar(string raw, out int idx, out string text)
        {
            idx = 0; text = "";
            if (string.IsNullOrEmpty(raw)) return false;
            int lines = raw.IndexOf("\"lines\"", StringComparison.Ordinal);
            if (lines < 0) return false;
            int open = raw.IndexOf('{', lines);
            if (open < 0) return false;
            int close = ObjectEnd(raw, open);
            string obj = close > 0 ? raw.Substring(open, close - open + 1) : raw.Substring(open);
            string whoRaw = StringOrBare(obj, "who", out _);
            if (whoRaw == null || whoRaw.StartsWith("B", StringComparison.OrdinalIgnoreCase)) return false;
            if (!int.TryParse(new string(whoRaw.Where(char.IsDigit).ToArray()), out idx)) return false;
            text = (StringOrBare(obj, "text", out _) ?? "").Trim();
            return true;
        }

        // Index of the brace that closes the object opened at 'open', or -1 if it has not arrived yet
        private static int ObjectEnd(string s, int open)
        {
            int depth = 0; bool inStr = false;
            for (int i = open; i < s.Length; i++)
            {
                char c = s[i];
                if (inStr) { if (c == '\\') i++; else if (c == '"') inStr = false; continue; }
                if (c == '"') inStr = true;
                else if (c == '{') depth++;
                else if (c == '}' && --depth == 0) return i;
            }
            return -1;
        }

        // The value of a key inside a (maybe unfinished) object: a string decoded so far, or a bare token. Null if absent.
        private static string StringOrBare(string obj, string key, out bool finished)
        {
            finished = false;
            int k = obj.IndexOf("\"" + key + "\"", StringComparison.Ordinal);
            if (k < 0) return null;
            int i = k + key.Length + 2;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t' || obj[i] == '\r' || obj[i] == '\n')) i++;
            if (i >= obj.Length || obj[i] != ':') return null;
            i++;
            while (i < obj.Length && (obj[i] == ' ' || obj[i] == '\t' || obj[i] == '\r' || obj[i] == '\n')) i++;
            if (i >= obj.Length) return null;
            var sb = new StringBuilder();
            if (obj[i] != '"')
            {
                while (i < obj.Length && obj[i] != ',' && obj[i] != '}' && !char.IsWhiteSpace(obj[i])) sb.Append(obj[i++]);
                finished = i < obj.Length;
                return sb.Length > 0 ? sb.ToString() : null;      // a bare number that is still growing is good enough for a speaker index
            }
            for (i++; i < obj.Length; i++)
            {
                char c = obj[i];
                if (c == '"') { finished = true; break; }
                if (c != '\\') { sb.Append(c); continue; }
                if (i + 1 >= obj.Length) break;              // escape cut in half: wait for the rest
                char e = obj[++i];
                if (e == 'n') sb.Append(' ');
                else if (e == 't') sb.Append(' ');
                else if (e == 'u')
                {
                    if (i + 4 >= obj.Length) break;
                    if (int.TryParse(obj.Substring(i + 1, 4), System.Globalization.NumberStyles.HexNumber, null, out int code)) sb.Append((char)code);
                    i += 4;
                }
                else if (e != 'r') sb.Append(e);
            }
            return sb.ToString();
        }

        private static bool SceneOk()
        {
            try
            {
                var m = Mission.Current;
                if (m == null || Campaign.Current == null || Agent.Main == null || !Agent.Main.IsActive()) return false;
                if (m.Mode == MissionMode.Battle || m.Mode == MissionMode.Duel || m.Mode == MissionMode.Deployment) return false;
                return true;
            }
            catch { return false; }
        }

        private static int ActiveMembers() => Circle.Count(x => x.Agent != null && x.Agent.IsActive());

        /// <summary>Back to a fresh start: you have just spoken, or the circle formed or broke up.</summary>
        private static void ResetFlow()
        {
            _carryLeft = Math.Max(0, S.Get.GroupSilenceFollowUps);
            _windLeft = WindDownLines;
            _nudges = 0;
            _silence = 0f;
            _awaitPlayer = false;
            _asker = null;
        }

        public static void Tick(float dt)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableGroupTalk || !SceneOk()) { if (Circle.Count > 0) End(false); return; }
                PaintLive();

                // The group breaks up if you walk away, change scene or stay silent too long
                if (Circle.Count > 0)
                {
                    _idle += dt;
                    Circle.RemoveAll(x => x.Agent == null || !x.Agent.IsActive() || x.Agent.Position.Distance(Agent.Main.Position) > cfg.GroupGraceRadius);
                    if (Mission.Current != _mission || Circle.Count == 0 || _idle > cfg.GroupIdleSeconds) End(true);
                }

                if (Circle.Count > 0) Pace(dt, cfg);
                // Everyone left during a round (sent off, walked away): what was said still goes into their memories
                else if (BookLines.Count > 0 && !_waiting && !Ambient.GroupPlaying) BookIfPending();

                // In case the popup closed without going through its buttons, don't leave the world in slow motion
                if (_slowed != null) { bool open = true; try { open = InformationManager.IsAnyInquiryActive(); } catch { } if (!open) Unslow(); }

                // Alt+G ends the talk, Alt+T opens it (keys set in MCM)
                if (Circle.Count > 0 && Hotkeys.Pressed(Hotkeys.Id.GroupEnd)) { End(true); return; }
                if (!Hotkeys.Pressed(Hotkeys.Id.GroupTalk)) return;
                // Only your own words still being answered keep the window shut; the group's own rounds give way to you
                if ((_waiting && _waitingKind == Kind.Player) || ChatSession.IsBusy || Initiative.Busy || Campaign.Current.ConversationManager.IsConversationInProgress) return;
                Open();
            }
            catch (Exception ex) { Log.Error("Group conversation", ex); End(false); }
        }

        /// <summary>
        /// The talk without you: a round written ahead starts when the lines before it are done; after a question
        /// put to you, a nudge at the first pause and another later; someone left alone with you has a last word and the circle ends.
        /// </summary>
        private static void Pace(float dt, AILegacySettings cfg)
        {
            bool blocked = ChatSession.IsBusy || Initiative.Busy || Campaign.Current.ConversationManager.IsConversationInProgress;
            try { blocked = blocked || InformationManager.IsAnyInquiryActive(); } catch { }
            bool playing = Ambient.GroupPlaying;
            if (_ready != null)
            {
                // Held while you type: what you say replaces it
                if (!playing && !blocked) { var r = _ready; _ready = null; Apply(r); }
                return;
            }
            if (blocked || Ambient.ErrandBusy) { _silence = 0f; return; }
            if (playing || _waiting) return;

            _silence += dt;
            float s = cfg.GroupSilenceSeconds;
            if (s <= 0f) return;
            if (ActiveMembers() < 2)
            {
                // Nobody else to talk to: at most one nudge, then the circle ends quietly
                if (_awaitPlayer && _nudges == 0 && _silence >= s) { _nudges = 1; _awaitPlayer = false; _silence = 0f; Ask(Kind.Alone); return; }
                if (_silence >= (_nudges > 0 ? s : s * 2.5f)) { Log.Info("[group] only one person left and you say nothing: the circle ends"); End(false); }
                return;
            }
            if (_awaitPlayer && _nudges < 2 && _silence >= (_nudges == 0 ? s : s * 1.5f))
            {
                _nudges++; _awaitPlayer = false; _silence = 0f;
                Ask(Kind.Nudge);
            }
        }

        private static void Open()
        {
            var cfg = S.Get;
            if (!Config.LegacyConfig.Current.Providers.Any(p => p.IsUsable)) { Say("AI Legacy: no hay ninguna api_key configurada.", Colors.Red); return; }
            var m = Mission.Current;
            var me = Agent.Main.Position;
            // Standing in a conversation going on nearby: it becomes yours, with what was said in it
            Ambient.HandOverNear(cfg.GroupRadius, LookedAt(cfg.GroupRadius * 2f));
            bool fresh = Circle.Count == 0;
            // A new circle: nothing asked for by the last one may play in it
            if (fresh) { _gen++; _ready = null; _waiting = false; _liveRaw = null; _liveCircle = null; }

            // Whoever you look at is the one addressed, even a bit further away (they walk over)
            _addressed = LookedAt(cfg.GroupRadius * 2f);
            if (_addressed != null && !Circle.Any(x => x.Agent == _addressed) && !(_addressed.Team != null && Agent.Main.Team != null && _addressed.IsEnemyOf(Agent.Main)))
            {
                Circle.Insert(0, NewMember(_addressed));
                if (_addressed.Position.Distance(me) > 3.5f) SceneActions.Summon(_addressed);
            }

            // Add whoever is near now (up to the max). Existing members stay
            foreach (var a in m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject
                                                  && a.Position.Distance(me) <= cfg.GroupRadius && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main)))
                                      .OrderBy(a => a.Position.Distance(me)))
            {
                if (Circle.Count >= cfg.GroupMaxParticipants) break;
                if (Circle.Any(x => x.Agent == a)) continue;
                Circle.Add(NewMember(a));
            }
            if (Circle.Count == 0) { Say(L.F("AIL_71b565dc", $"No hay nadie a menos de {cfg.GroupRadius} metros a quien hablar."), Colors.Gray); return; }
            // They gather around and face you. The ones a bit apart walk over (anyone seated or busy stays put)
            SceneActions.Arrange(Circle.Select(x => x.Agent).ToList());
            if (_mission != m) { Transcript.Clear(); _mission = m; }
            _idle = 0f;
            if (fresh) { ResetFlow(); _lastKey = null; _lastName = null; }
            _silence = 0f; _nudges = 0;

            string names = string.Join(", ", Circle.Select(x => x.Who.Name));
            string last = Transcript.Count == 0 ? "" : L.T("AIL_cbe9f6a8", "\n\nLo último:\n") + string.Join("\n", Transcript.Skip(Math.Max(0, Transcript.Count - 4)).Select(x => ForPlayer(x)));
            string title = L.T("AIL_6d3c2db8", "Hablas al grupo");
            string how = Hotkeys.Fix(L.T("AIL_dd02342f", "\n\n(Empieza con * para susurrárselo solo a quien miras, o con ! para gritarlo. Alt+G termina la conversación.)"));
            // The world doesn't pause while you type, it goes into slow motion
            bool slow = cfg.GroupSlowMotion;
            if (slow) { try { m.AddTimeSpeedRequest(new Mission.TimeSpeedRequest(0.2f, SlowId)); _slowed = m; } catch { slow = false; } }
            InformationManager.ShowTextInquiry(new TextInquiryData(title, L.F("AIL_26906366", $"Te oyen: {names}.{last}{how}"), true, true, L.T("AIL_aa23f794", "Decir"), L.T("AIL_b8f9f7be", "Callar"),
                t => { Unslow(); OnSaid(t); }, Unslow, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_e07caced", "Escribe algo"))), !slow);
        }

        private static void OnSaid(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || Circle.Count == 0) return;
            if (_waiting && _waitingKind == Kind.Player) return;      // your last words are still being answered
            var cfg = S.Get;
            var m = Mission.Current;
            if (m == null) return;

            // Whisper (*) or shout (!)
            bool whisper = text.StartsWith("*"), shout = !whisper && text.StartsWith("!");
            if (whisper || shout) text = text.Substring(1).Trim();
            if (text.Length == 0) return;

            // You speak: whatever the group was writing ahead is dropped, and the pace starts over
            _gen++;
            _ready = null; _waiting = false; _liveRaw = null; _liveCircle = null;
            _idle = 0f;
            ResetFlow();

            var addressed = _addressed != null ? Circle.FirstOrDefault(x => x.Agent == _addressed && x.Agent.IsActive()) : null;
            if (whisper && addressed == null) addressed = Circle.OrderBy(x => x.Agent.Position.Distance(Agent.Main.Position)).First();
            if (shout)
            {
                // A shout carries twice as far, and whoever hears it joins the talk
                foreach (var a in m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject
                                                      && a.Position.Distance(Agent.Main.Position) <= cfg.GroupRadius * 2f && !Circle.Any(x => x.Agent == a)
                                                      && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main))).OrderBy(a => a.Position.Distance(Agent.Main.Position)))
                {
                    if (Circle.Count >= cfg.GroupMaxParticipants) break;
                    var mb = NewMember(a);
                    Circle.Add(mb);
                    SceneActions.Summon(a);
                    Say(L.F("AIL_2215a755", $"{mb.Who.Name} se une a la conversación."), Colors.Gray);
                }
            }
            string manner = whisper ? $"{{player}} WHISPERS this to {addressed.Who.Name} alone. The others see the two of them whispering but do NOT hear the words: only {addressed.Who.Name} can answer what was said (in a low voice); another person may at most remark on the whispering itself."
                          : shout ? "{player} SHOUTS this so that everyone around hears it; people react to being shouted at as their station and temper dictate."
                          : addressed != null ? $"{{player}} happens to be looking at {addressed.Who.Name} while speaking, but everyone in the group hears it. If the words are plainly meant for {addressed.Who.Name} (their name, a matter that is theirs alone), {addressed.Who.Name} answers and the others join in only if they have something to add. If the words are meant for everyone (a greeting to all, a plural address such as 'my lords' or 'friends', a question put to the group), {addressed.Who.Name} answers first and then each other person listed by name also answers in their own line, briefly; nobody named is left out of a greeting made to all."
                          : "{player} speaks to the group at large.";
            string playerName = Hero.MainHero.Name.ToString();
            manner = manner.Replace("{player}", playerName);

            // The prompt is built before your line goes into the transcript: it is given apart, as what you now say
            Ask(Kind.Player, text, manner, whisper, shout, addressed);

            _lastKey = "player"; _lastName = playerName;
            Transcript.Add($"{playerName}: {text}");
            BookLines.Add($"{playerName}{(whisper && addressed != null ? $" (whispering to {addressed.Who.Name})" : "")}: {text}");
            _bookPlayerSpoke = true;
            foreach (var x in Circle) AddBookMember(x);
            Bubbles.Show(Agent.Main, text, Math.Max(2.5f, Math.Min(7f, 1.5f + text.Length * 0.05f)));
            // A whisper carries a quarter as far and a shout twice as far. Group members don't count as overhearing
            if (whisper) Ambient.Overhear(new List<Speaker> { addressed.Who }, text, null, 0.25f);
            else Ambient.Overhear(Circle.Select(x => x.Who).ToList(), text, null, shout ? 2f : 1f);
        }

        /// <summary>A transcript line as shown to you: "Name: words", without the English "(to X)" kept for the AI.</summary>
        private static string ForPlayer(string line)
        {
            if (string.IsNullOrEmpty(line)) return line ?? "";
            int colon = line.IndexOf(": ", StringComparison.Ordinal);
            if (colon <= 0) return line;
            int to = line.LastIndexOf(" (to ", colon, StringComparison.Ordinal);
            return to > 0 ? line.Substring(0, to) + line.Substring(colon) : line;
        }

        private static void AddBookMember(Member x) { if (x != null && x.Who != null && !BookMembers.Any(b => b.Who.Key == x.Who.Key)) BookMembers.Add(x); }

        private static string Clip(string s, int max) => string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s.Substring(0, max) + "…";

        /// <summary>
        /// The people present, for the prompt. When answering you (full) it adds their past with you, their last private
        /// words with you, what they heard lately and open tasks. Lines said in this circle are left out: they are in the
        /// transcript, and feeding a speaker their own lines back makes them repeat themselves.
        /// </summary>
        private static string MembersBlock(List<Member> circle, bool full, string playerName, AILegacySettings cfg)
        {
            var members = new StringBuilder();
            for (int i = 0; i < circle.Count; i++)
            {
                var x = circle[i];
                var xm = x.Who.Memory;
                string knows = ContextBuilder.PlayerNameFor(x.Who) != null ? $"knows who {playerName} is" : $"does not know who {playerName} is";
                string rel = x.Who.IsHero ? $" Opinion of {playerName}: {ContextBuilder.DescribeRelation((int)x.Who.Hero.GetRelationWithPlayer())}." : "";
                string recent = "";
                // What they were doing just before joining. A summary of this very circle is left out (it is in the transcript)
                if (!string.IsNullOrWhiteSpace(xm.RecentTalk) && xm.RecentTalkAt < x.JoinedAt && (DateTime.UtcNow - xm.RecentTalkAt).TotalMinutes < 30) recent += $" JUST NOW: {xm.RecentTalk}.";
                string mem = "", task = "";
                if (full)
                {
                    if (x.Who.IsHero && !string.IsNullOrWhiteSpace(xm.Summary)) mem = $" Past dealings with {playerName}: {Clip(xm.Summary, 300)}";
                    // Recent context from before this circle. Without it, someone just back from talking to another person answers as if nothing happened
                    var before = xm.Turns.Take(Math.Min(x.TurnsAtJoin, xm.Turns.Count))
                                   .Where(t => t.Text != null && !t.IsNote && !t.Text.StartsWith("[Said to the group", StringComparison.Ordinal)).ToList();
                    var lastTurns = before.Skip(Math.Max(0, before.Count - 3)).Select(t => (t.Role == "npc" ? x.Who.Name : playerName) + ": " + Clip(t.Text, 120)).ToList();
                    if (lastTurns.Count > 0) recent += $" Their last private words with {playerName} — " + string.Join(" / ", lastTurns) + ".";
                    var heardP = InfoExchange.AboutPlayer(xm).ToList();
                    var heard = heardP.Skip(Math.Max(0, heardP.Count - 2)).Where(h => !string.IsNullOrWhiteSpace(h.Text)).Select(h => $"{h.FromName}: {Clip(h.Text, 160)}").ToList();
                    if (heard.Count > 0) recent += " Lately heard from others — " + string.Join(" / ", heard) + ".";
                    // A task they gave the player that is still open: they may well bring it up
                    try
                    {
                        var q = cfg.EnableQuests && x.Who.IsHero ? World.QuestSystem.ActiveFrom(x.Who.Hero.StringId) : null;
                        if (q != null)
                            task = " " + TOpenTask.Format("player", playerName, "brief", string.IsNullOrWhiteSpace(q.Brief) ? q.Name : q.Brief,
                                "days", Math.Max(0, (int)Math.Ceiling(q.DueDay - CampaignTime.Now.ToDays))).Trim();
                    }
                    catch { }
                }
                // How they stand with the others present
                string among = "";
                if (cfg.EnableNpcBonds && x.Who.IsHero)
                    foreach (var y in circle)
                        if (y != x && y.Who.IsHero && xm.Others.TryGetValue(y.Who.Hero.StringId, out var ob))
                            among += $" Towards {y.Who.Name}: {Bonds.OtherWord(ob.Trust)}" + (ob.Notes.Count > 0 ? $" (last: {Clip(ob.Notes[ob.Notes.Count - 1], 120)})" : "") + ".";
                members.AppendLine($"{i + 1}. {x.Who.Name} — {x.Desc}. {knows}.{rel}{mem}{recent}{among}{task}");
            }
            return members.ToString().TrimEnd();
        }

        /// <summary>Asks for the next round. Your answer (Player) is played as soon as it comes; the others wait in Pace for the lines before them.</summary>
        private static void Ask(Kind kind, string said = null, string manner = null, bool whisper = false, bool shout = false, Member addressed = null)
        {
            var cfg = S.Get;
            var m = Mission.Current;
            if (m == null || Circle.Count == 0 || Hero.MainHero == null) return;
            var st = Settlement.CurrentSettlement;
            bool player = kind == Kind.Player;
            if (!player && kind != Kind.Alone && ActiveMembers() < 2) return;
            string playerName = Hero.MainHero.Name.ToString();
            var circle = Circle.ToList();
            string members = MembersBlock(circle, player, playerName, cfg);
            string transcript = Transcript.Count == 0 ? "(nothing yet)" : string.Join("\n", Transcript.Skip(Math.Max(0, Transcript.Count - 14)));
            string context = st != null ? Ambient.Context(st, circle[0].Who) : "(on the road)";
            string place = st?.Name?.ToString() ?? "the camp";
            var bystanders = new List<Agent>();
            var away = new List<Agent>();
            string prompt;
            int maxLines;
            if (player)
            {
                // People nearby but outside the group heard it, and can butt in if it concerns them
                string byText = "(nobody)";
                if (cfg.GroupBystanders && !whisper)
                {
                    float hear = cfg.GroupHearRadius * (shout ? 2f : 1f);
                    bystanders = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject && a.IsAIControlled
                                                     && a.Position.Distance(Agent.Main.Position) <= hear && !Circle.Any(x => x.Agent == a) && !SceneActions.Busy(a)
                                                     && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main)))
                                          .OrderBy(a => a.Position.Distance(Agent.Main.Position)).Take(4).ToList();
                    if (bystanders.Count > 0)
                        byText = string.Join("\n", bystanders.Select((a, i) => $"B{i + 1}. {Ambient.Describe(a, out var _sp)}"));
                }
                // Named people in the scene but outside the group, who someone could go and fetch
                string elsewhere = "(nobody)";
                if (cfg.SceneErrands && st != null)
                {
                    away = m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject co && co.IsHero && co.HeroObject != null
                                               && !Circle.Any(x => x.Agent == a)).Take(8).ToList();
                    if (away.Count > 0)
                        elsewhere = string.Join("; ", away.Select(a =>
                        {
                            float dz = a.Position.z - Agent.Main.Position.z;
                            return $"{a.Name} ({(dz > 2f ? "on the floor above" : dz < -2f ? "on the floor below" : "on this floor")}, about {(int)a.Position.Distance(Agent.Main.Position)} paces away)";
                        }));
                }
                // The action catalog, short, only for the named people most likely to act: the one you look at, then those who know you
                string deeds = "";
                if (cfg.GroupFullActions && cfg.EnableActions)
                {
                    var sbDeeds = new StringBuilder();
                    var actors = circle.Select((x, i) => new { x, i }).Where(p => p.x.Who.IsHero)
                                       .OrderByDescending(p => p.x == addressed).ThenByDescending(p => ContextBuilder.PlayerNameFor(p.x.Who) != null).Take(whisper ? 1 : 2).ToList();
                    foreach (var p in actors)
                    {
                        if (whisper && p.x != addressed) continue;
                        string brief = "";
                        try { brief = NpcActions.DescribeBrief(p.x.Who, 12); } catch { }
                        if (brief.Length == 0) continue;
                        sbDeeds.AppendLine($"- Person {p.i + 1} ({p.x.Who.Name}): {brief}");
                    }
                    if (sbDeeds.Length > 0)
                        deeds = "\nDeeds with real consequences. Besides the above, these people can DO the following, exactly as they could in a private talk with " + playerName +
                                ", and only when the talk truly calls for it and they would really decide it in front of the others. To do one, add \"do\": [\"action as written\"] to that person's line " +
                                "(it takes effect in the world at once; never announce a deed in words without it):\n" + sbDeeds.ToString().TrimEnd();
                }
                maxLines = Math.Max(1, cfg.GroupMaxLines);
                prompt = TGroup.Format("manner", manner ?? "", "bystanders", byText, "actions", (cfg.SceneErrands ? SceneActions.HelpFor(playerName) : "(nothing else)") + deeds, "moods", Gestures.Moods,
                    "place", place, "player", playerName, "members", members, "context", context, "elsewhere", elsewhere,
                    "transcript", transcript, "said", (said ?? "").Replace("«", "\"").Replace("»", "\""), "max_lines", maxLines, "max_chars", cfg.GroupMaxChars, "language", cfg.Language);
                try { string lessons = Lessons.Block(); if (lessons.Length > 0) prompt += "\n\n" + lessons; } catch { }
            }
            else
            {
                int minLines;
                string situation;
                switch (kind)
                {
                    case Kind.CarryOn:
                        maxLines = Math.Max(2, Math.Min(4, _carryLeft)); minLines = 2;
                        situation = TCarryOn.Format("player", playerName);
                        break;
                    case Kind.WindDown:
                        maxLines = Math.Max(1, Math.Min(WindDownLines, _windLeft)); minLines = 1;
                        situation = TWindDown.Format("player", playerName);
                        break;
                    case Kind.Nudge:
                        maxLines = 3; minLines = 1;
                        situation = TNudge.Format("player", playerName, "asker", _asker ?? "someone");
                        break;
                    default:
                        maxLines = 1; minLines = 1;
                        circle = circle.Where(x => x.Agent != null && x.Agent.IsActive()).Take(1).ToList();
                        if (circle.Count == 0) return;
                        members = MembersBlock(circle, false, playerName, cfg);
                        situation = TAlone.Format("player", playerName, "asker", circle[0].Who.Name);
                        break;
                }
                prompt = TGroupOn.Format("place", place, "player", playerName, "members", members, "context", context, "transcript", transcript, "situation", situation,
                    "last_speaker", _lastName ?? playerName, "min_lines", minLines, "max_lines", maxLines, "max_chars", cfg.GroupMaxChars, "language", cfg.Language, "moods", Gestures.Moods);
            }

            int gen = _gen;
            _waiting = true; _waitingKind = kind;
            int session = WorldSim.Session;
            // The first line shows over its speaker's head while it is written; the round itself starts when the reply is whole
            _liveRaw = null; _livePainted = null; _liveCircle = circle;
            Func<string, bool> onPartial = raw => { if (session != WorldSim.Session || gen != _gen) return false; _liveRaw = raw; return true; };
            int tokens = Math.Max(300, 120 + maxLines * 110);
            Log.Info($"[group] asking for a round ({kind}, {maxLines} line(s) at most, {circle.Count} present)");
            LlmClient.CompleteStreamedAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, onPartial: onPartial, task: "group", maxTokensOverride: tokens, preferredProvider: cfg.ProviderHeroes, raw: true).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    // You spoke or the circle ended since it was asked for: nobody waits for it any more
                    if (gen != _gen) { Log.Info($"[group] a {kind} round nobody waits for any more, dropped"); return; }
                    _waiting = false;
                    _liveRaw = null; _liveCircle = null;
                    try
                    {
                        // asked in another campaign: its lines, deeds and memories do not belong in this one
                        if (session != WorldSim.Session) { Log.Info("[group] reply from another game, discarded"); return; }
                        if (res == null || !res.Ok)
                        {
                            if (player) Say(L.T("AIL_38907a94", "Nadie del grupo contesta (la IA no ha respondido)."), Colors.Red);
                            Log.Warn($"Group: no reply ({kind}): " + res?.Error);
                            BookIfPending();
                            return;
                        }
                        if (Mission.Current != m) return;
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a < 0 || b <= a) { Log.Warn($"Group: unreadable reply ({kind})"); BookIfPending(); return; }
                        var round = new Round { Gen = gen, Kind = kind, J = JObject.Parse(res.Text.Substring(a, b - a + 1)), Circle = circle, M = m, MaxLines = maxLines,
                                                Bystanders = bystanders, Away = away, PlayerText = said };
                        // Your answer plays at once (it cuts what they were saying); the group's own rounds wait for the lines before them
                        if (player) Apply(round);
                        else _ready = round;
                    }
                    catch (Exception ex) { Log.Error("Group reply", ex); }
                });
            });
        }

        // Words of a line, for telling whether a speaker is saying the same thing again. Short words weigh nothing
        // unless the line has few others.
        private static HashSet<string> Words(string s)
        {
            var all = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in (s ?? "") + " ")
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(char.ToLowerInvariant(c)); continue; }
                if (sb.Length > 0) { all.Add(sb.ToString()); sb.Clear(); }
            }
            var big = all.Where(w => w.Length >= 4).ToList();
            return new HashSet<string>(big.Count >= 3 ? big : all);
        }

        /// <summary>True if the line shares 70% or more of its words with one of the speaker's last 3 lines in this circle.</summary>
        private static bool Repeats(Member m, string said)
        {
            var w = Words(said);
            if (w.Count == 0) return false;
            foreach (var old in m.Recent)
            {
                var o = Words(old);
                if (o.Count == 0) continue;
                int shared = w.Count(x => o.Contains(x));
                if (shared >= 0.7 * w.Count) return true;
            }
            return false;
        }

        /// <summary>Plays a round that came back: checks each line, queues the bubbles, carries out what was decided and asks for the next round.</summary>
        private static void Apply(Round r)
        {
            try
            {
                var cfg = S.Get;
                var m = r.M;
                if (Mission.Current != m || r.Gen != _gen || Hero.MainHero == null) return;
                bool player = r.Kind == Kind.Player;
                string playerName = Hero.MainHero.Name.ToString();
                var circle = r.Circle;
                var j = r.J;
                string text = r.PlayerText ?? "";
                var lines = new List<(Agent who, Agent to, string text, string mood, string pre, bool deferred)>();
                var walkingOver = new HashSet<Agent>();
                var heard = new HashSet<string>();
                bool askedPlayer = false;
                string asker = null;
                int kept = 0;
                if (j["lines"] is JArray arr)
                    foreach (var l in arr.OfType<JObject>().Take(Math.Max(1, r.MaxLines)))
                    {
                        string said = ((string)l["text"] ?? "").Trim();
                        int idx = 0;
                        string whoRaw = (l["who"]?.ToString() ?? "").Trim();
                        int.TryParse(new string(whoRaw.Where(char.IsDigit).ToArray()), out idx);
                        if (said.Length < 2 || idx < 1) continue;
                        Member member;
                        string pre = null;
                        if (whoRaw.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                        {
                            // A bystander butts in: walks over and joins the group (only when you have just spoken)
                            if (!player || idx > r.Bystanders.Count || !r.Bystanders[idx - 1].IsActive()) continue;
                            var ba = r.Bystanders[idx - 1];
                            Join(ba);
                            member = Circle.FirstOrDefault(x => x.Agent == ba);
                            if (member == null) continue;
                            // Far away: they walk over first and their line waits for them. Close by: they just join
                            string joined = L.F("AIL_2215a755", $"{member.Who.Name} se une a la conversación.");
                            if (Agent.Main != null && ba.Position.Distance(Agent.Main.Position) > 4.5f && ba.IsAIControlled)
                            {
                                SceneActions.Summon(ba);
                                Say(L.F("AIL_4eaee384", $"{member.Who.Name} se acerca."), Colors.Gray);
                                walkingOver.Add(ba);
                                pre = joined;
                            }
                            else Say(joined, Colors.Gray);
                        }
                        else
                        {
                            if (idx > circle.Count) continue;
                            member = circle[idx - 1];
                            if (!Circle.Contains(member)) continue;      // has left the circle since the round was asked for
                        }
                        if (member.Agent == null || !member.Agent.IsActive()) continue;
                        if (said.Length > cfg.GroupMaxChars + 80) said = said.Substring(0, cfg.GroupMaxChars + 80);
                        // Nobody speaks twice in a row, and nobody says again what they already said here
                        if (member.Who.Key == _lastKey) { Log.Info($"[group] dropped a second line in a row from {member.Who.Name}"); continue; }
                        if (Repeats(member, said)) { Log.Info($"[group] dropped a line {member.Who.Name} had nearly said already: {said}"); continue; }

                        // Who it is said to: another person present, or you
                        Member toMember = null;
                        string toRaw = (l["to"]?.ToString() ?? "").Trim();
                        if (!toRaw.StartsWith("B", StringComparison.OrdinalIgnoreCase) && int.TryParse(new string(toRaw.Where(char.IsDigit).ToArray()), out int ti) && ti >= 1 && ti <= circle.Count)
                        {
                            var cand = circle[ti - 1];
                            if (cand != member && Circle.Contains(cand) && cand.Agent != null && cand.Agent.IsActive()) toMember = cand;
                        }
                        string toName = toMember != null ? toMember.Who.Name : playerName;
                        bool later = walkingOver.Contains(member.Agent);
                        lines.Add((member.Agent, toMember?.Agent ?? Agent.Main, said, (string)l["mood"], pre, later));
                        Transcript.Add($"{member.Who.Name} (to {toName}): {said}");
                        BookLines.Add($"{member.Who.Name} (to {toName}): {said}");
                        AddBookMember(member);
                        if (toMember != null) AddBookMember(toMember);
                        member.Recent.Add(said);
                        while (member.Recent.Count > 3) member.Recent.RemoveAt(0);
                        _lastKey = member.Who.Key; _lastName = member.Who.Name;
                        kept++;
                        askedPlayer = toMember == null && (said.IndexOf('?') >= 0 || said.IndexOf('？') >= 0);
                        if (askedPlayer) asker = member.Who.Name;
                        try { Ambient.Overhear(circle.Select(x => x.Who).ToList(), said, member.Who); } catch { }
                        if (member.Who.IsHero) { member.Who.Memory.ConversationCount = Math.Max(1, member.Who.Memory.ConversationCount); member.Who.Memory.LastConversationDay = CampaignTime.Now.ToDays; }
                        if (!player) continue;

                        // Going off to fetch someone?
                        string goes = ((string)l["goes_to"] ?? "").Trim();
                        if (goes.Length > 1 && !Ambient.ErrandBusy && member.Agent.IsAIControlled)
                        {
                            var target = r.Away.FirstOrDefault(x => x.IsActive() && string.Equals(x.Name, goes, StringComparison.OrdinalIgnoreCase))
                                         ?? r.Away.FirstOrDefault(x => x.IsActive() && (x.Name.IndexOf(goes, StringComparison.OrdinalIgnoreCase) >= 0 || goes.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0));
                            if (target != null && target != member.Agent)
                            {
                                bool back = l["returns"] != null && l["returns"].Type == JTokenType.Boolean && (bool)l["returns"];
                                Ambient.SendTo(member.Agent, target, text, back);
                                Circle.RemoveAll(x => x.Agent == member.Agent);
                                Say(L.F("AIL_e9023c1f", $"{member.Who.Name} va a buscar a {target.Name}."), Colors.Gray);
                            }
                        }
                        else if (SceneActions.Apply(member.Agent, (l["action"] ?? "").ToString(), r.Away, said))
                            Circle.RemoveAll(x => x.Agent == member.Agent);
                        // Deeds from the full catalog (give gold, swear an oath, join...), decided in front of everyone
                        try
                        {
                            if (cfg.GroupFullActions && member.Who.IsHero && l["do"] != null)
                            {
                                var cmds = l["do"] is JArray da ? da.Select(x => (x ?? "").ToString()).ToList() : new List<string> { l["do"].ToString() };
                                cmds = cmds.Where(c => !string.IsNullOrWhiteSpace(c)).Take(2).ToList();
                                if (cmds.Count > 0)
                                {
                                    int turnsBefore = member.Who.Memory.Turns.Count;
                                    foreach (var shownMsg in NpcActions.Execute(member.Who, cmds, new NpcActions.ActionContext()))
                                        Say(shownMsg, Colors.Yellow);
                                    ShareDeeds(member, Circle.Union(circle).ToList(), turnsBefore, playerName);
                                }
                            }
                        }
                        catch (Exception ex) { Log.Error("Circle: catalog action", ex); }
                        // Your words go into the speaker's history once per round. Their own lines in the circle don't:
                        // the circle is kept as a short account (see Book), so they don't parrot their own words later
                        if (heard.Add(member.Who.Key)) member.Who.Memory.Turns.Add(new ChatTurn { Role = "player", Text = "[Said to the group you were in] " + text, Day = CampaignTime.Now.ToDays });
                    }

                _silence = 0f;
                if (r.Kind == Kind.CarryOn) _carryLeft -= Math.Max(1, kept);
                else if (r.Kind == Kind.WindDown) _windLeft = 0;
                if (kept == 0)
                {
                    if (player) Say(L.T("AIL_156ada7f", "El grupo se queda callado."), Colors.Gray);
                    Log.Info($"[group] {r.Kind} round with nothing to say");
                    // an empty round of their own talk still spent its budget: go on to the next step (or wind down)
                    if (r.Kind == Kind.CarryOn || r.Kind == Kind.WindDown) { ScheduleNext(false, r.Kind); return; }
                    BookIfPending();
                    return;
                }
                Ambient.Enqueue(m, lines, append: !player);
                _asker = asker;
                while (Transcript.Count > 40) Transcript.RemoveAt(0);
                Log.Info($"[group] {r.Kind}: {kept} line(s){(askedPlayer ? ", ends asking you" : "")}");
                if (++_roundsSinceBook >= BookEveryRounds) BookIfPending();
                ScheduleNext(askedPlayer, r.Kind);
                SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Group reply", ex); }
        }

        /// <summary>
        /// Right after a round starts playing: asks for the next one so it is ready when the last bubble ends, or stops
        /// and waits for you (a question was put to you, the budget is spent, or nobody is left to talk to).
        /// </summary>
        private static void ScheduleNext(bool askedPlayer, Kind kind)
        {
            var cfg = S.Get;
            _awaitPlayer = false;
            if (kind == Kind.Alone) { BookIfPending(); return; }
            if (askedPlayer) { _awaitPlayer = true; BookIfPending(); return; }
            if (cfg.GroupSilenceSeconds <= 0 || cfg.GroupSilenceFollowUps <= 0 || ActiveMembers() < 2) { BookIfPending(); return; }
            if (_carryLeft > 0) Ask(Kind.CarryOn);
            else if (_windLeft > 0) Ask(Kind.WindDown);
            else BookIfPending();
        }

        /// <summary>
        /// What the talk since the last time changed: one short account for everyone's memory, bonds between them,
        /// their opinion of you and maybe a rumour. A cheap background request, apart from the lines themselves.
        /// </summary>
        private static void BookIfPending()
        {
            try
            {
                var members = BookMembers.ToList();
                var said = BookLines.ToList();
                bool playerSpoke = _bookPlayerSpoke;
                BookLines.Clear(); BookMembers.Clear(); _roundsSinceBook = 0; _bookPlayerSpoke = false;
                if (said.Count == 0 || members.Count == 0 || Hero.MainHero == null || Campaign.Current == null) return;
                if (!said.Any(x => members.Any(mb => x.StartsWith(mb.Who.Name + " (to ", StringComparison.Ordinal)))) return;      // nobody answered anything
                var cfg = S.Get;
                string playerName = Hero.MainHero.Name.ToString();
                var st = Settlement.CurrentSettlement;
                string list = string.Join("\n", members.Select((x, i) => $"{i + 1}. {x.Who.Name}{(x.Who.IsHero ? "" : " (common folk)")}"));
                string prompt = TBook.Format("place", st?.Name?.ToString() ?? "the camp", "player", playerName, "members", list, "lines", string.Join("\n", said), "language", cfg.Language,
                    "player_part", playerSpoke ? $"{playerName} took part in it." : $"{playerName} stood with them but only listened.");
                int session = WorldSim.Session;
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, maxTokensOverride: 400, preferredProvider: cfg.ProviderSummaries, task: "summary").ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        try
                        {
                            if (session != WorldSim.Session) return;
                            if (res == null || !res.Ok) { Log.Warn("Group: no account of the talk: " + res?.Error); return; }
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            ApplyBook(JObject.Parse(res.Text.Substring(a, b - a + 1)), members, playerSpoke, playerName);
                        }
                        catch (Exception ex) { Log.Error("Group: account of the talk", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Group: ask for the account of the talk", ex); }
        }

        private static void ApplyBook(JObject j, List<Member> members, bool playerSpoke, string playerName)
        {
            var cfg = S.Get;
            string summary = ((string)j["summary"] ?? "").Trim();
            // What happened BETWEEN them updates their bonds with each other
            try
            {
                if (j["between"] is JArray bt)
                    foreach (var e in bt.OfType<JObject>().Take(4))
                    {
                        int.TryParse(new string((e["from"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int fi);
                        int.TryParse(new string((e["to"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int ti);
                        int.TryParse((e["change"] ?? "0").ToString().Trim(), out int ch);
                        if (fi < 1 || ti < 1 || fi > members.Count || ti > members.Count || fi == ti) continue;
                        ch = Math.Max(-2, Math.Min(2, ch));
                        string why = ((string)e["why"] ?? "").Trim();
                        Bonds.TouchOther(members[fi - 1].Who, members[ti - 1].Who, ch * 4, why.Length > 2 ? $"in front of {playerName}: {why}" : null);
                    }
            }
            catch { }
            // How each one now sees YOU after what you said or did in front of everyone
            try
            {
                if (playerSpoke && j["toward_player"] is JArray tp)
                    foreach (var e in tp.OfType<JObject>().Take(4))
                    {
                        int.TryParse(new string((e["who"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int wi);
                        if (wi < 1 || wi > members.Count) continue;
                        var who = members[wi - 1].Who;
                        if (!who.IsHero) continue;
                        int.TryParse((e["relation"] ?? "0").ToString().Trim(), out int rel);
                        int.TryParse((e["trust"] ?? "0").ToString().Trim(), out int tr);
                        rel = Math.Max(-2, Math.Min(2, rel)); tr = Math.Max(-3, Math.Min(3, tr));
                        if (rel != 0) TaleWorlds.CampaignSystem.Actions.ChangeRelationAction.ApplyPlayerRelation(who.Hero, rel, false, true);
                        if (tr != 0 && cfg.EnableTrust) tr = Bonds.ChangeTrust(who, tr); else tr = 0;
                        if (rel != 0 || tr != 0)
                        {
                            var bits = new List<string>();
                            if (rel != 0) bits.Add(L.F("AIL_a190aec6", $"Relación {(rel > 0 ? "+" : "")}{rel}"));
                            if (tr != 0) bits.Add(L.F("AIL_c78d48c5", $"Confianza {(tr > 0 ? "+" : "")}{tr}"));
                            Say($"{who.Name}: " + string.Join(", ", bits), Colors.Gray);
                        }
                    }
            }
            catch (Exception ex) { Log.Error("Group: opinion of the player", ex); }
            string rumor = ((string)j["rumor"] ?? "").Trim();
            if (cfg.EnableRumors && rumor.Length > 20 && (DateTime.UtcNow - _lastRumor).TotalSeconds > 120)
            {
                _lastRumor = DateTime.UtcNow;
                var stn = Settlement.CurrentSettlement;
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 2, Text = rumor.Length > 300 ? rumor.Substring(0, 300) : rumor, SettlementId = stn?.StringId,
                    TitleEs = ((string)j["rumor_title"] ?? "").Trim(), TextEs = ((string)j["rumor_local"] ?? "").Trim(),
                    Kingdoms = stn?.MapFaction != null ? new List<string> { stn.MapFaction.StringId } : new List<string>(),
                    Heroes = members.Where(x => x.Who.IsHero).Select(x => x.Who.Hero.StringId).Take(4).ToList(),
                });
                WorldSim.OnIncident($"A rumour has started to spread among those who were present ({string.Join(", ", members.Select(x => x.Who.Name).Take(4))}): {rumor}", stn, false);
            }
            foreach (var x in members)
            {
                if (summary.Length > 5)
                {
                    x.Who.Memory.RecentTalk = $"Moments ago, in a group with them: {summary}"; x.Who.Memory.RecentTalkAt = DateTime.UtcNow;
                    // The circle is remembered as this short account, not line by line
                    x.Who.Memory.Turns.Add(new ChatTurn { Role = "player", Text = $"[In a group: {summary}]", Day = CampaignTime.Now.ToDays });
                }
                // Summarize group-only talkers too, or their history would grow without limit until a one-on-one talk
                try { if (x.Who.IsHero && x.Who.Memory.Turns.Count > cfg.SummarizeAfterTurns + cfg.HistoryTurnsInPrompt) MemorySummarizer.MaybeSummarize(x.Who, cfg.HistoryTurnsInPrompt); } catch { }
            }
            Log.Info($"[group] {string.Join(", ", members.Select(x => x.Who.Name))}: {summary}");
            SaveFiles.Flush();
        }

        private static void End(bool announce)
        {
            if (Circle.Count == 0 && !announce && !_waiting && _ready == null && BookLines.Count == 0) return;
            foreach (var x in Circle) { try { if (x.Agent != null && x.Agent.IsActive()) x.Agent.SetLookAgent(null); } catch { } }
            // Transcript is kept while you stay in the same scene, so a regrouped circle remembers it
            Circle.Clear();
            _gen++;
            _ready = null; _waiting = false; _liveRaw = null; _liveCircle = null; _livePainted = null;
            _idle = 0f;
            ResetFlow();
            _lastKey = null; _lastName = null;
            BookIfPending();
            if (announce) Say(L.T("AIL_a25b9c8e", "El grupo se deshace."), Colors.Gray);
        }
    }
}
