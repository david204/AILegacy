using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.UI;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Lines during a scene fight or duel, from the opponent and the onlookers. Everything is requested at once when the
    /// fight starts (one request) and handed out as the fight goes.
    /// </summary>
    internal static class FightBanter
    {
        private static readonly Tpl TLines = new Tpl("combat/fight_lines.txt",
@"A fight has just broken out in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord: {kind}.
One fighter is {opponent_name}: {opponent}
What lies between {opponent_name} and the other fighter, {player}: {between}
Around them stand onlookers (townsfolk, soldiers, whoever was near).
Write what {opponent_name} blurts out as the fight goes, in their own voice and temper, and what the onlookers shout. These are things gasped or barked mid-fight: very short, under {max_chars} characters each, in {language}. No narration, no asterisks. A fist fight is rough and half in sport; drawn steel is deadly earnest; a duel to the death is grim.
Also decide, as {opponent_name} would, how they conduct this fight (""conduct""). Numbers are the share of their own strength they have LEFT (100 = unhurt) at which they do it; 0 means never. A proud or desperate fighter never yields; a sensible one stops while they can still walk; a coward runs early; someone who started it in anger may call it off once blood is drawn. In a duel to the death yielding means begging for their life.
- ""yields_at"": they give up and admit defeat. ""yield_line"": what they say then.
- ""flees_at"": they break off and run. ""flee_line"".
- ""truce_at"": they call a halt as equals, nobody the winner. ""truce_line"".
- ""calls_help_at"": they shout for whoever nearby would stand with them (guards if they have the right, kin, friends). ""help_line"". 0 if nobody here would come or they are too proud.
- ""accepts_truce"": true or false, if {player} asks to stop as equals. ""truce_answer"": what they say to it.
- ""accepts_yield"": true or false, if {player} yields. In a fist fight or an ordinary duel it is always true. ""yield_answer"": what they say to it.
- ""helpers"": those fighting at {opponent_name}'s side right now: {helpers}. A short list, nearly always empty, of the ones who would not see it through. For each: ""who"": their exact name from that list, or ""anyone"" for whoever is nearest among those fighting beside {opponent_name} by then; ""does"": ""stand_down"" (they lower their weapon, step back and take no further part) or ""turn"" (they change sides and fight for {player}); ""at"": when {opponent_name} has that share of strength left; ""line"": what that person says as they do it. A hired blade or a frightened neighbour stands down once it goes badly; kin, sworn men and guards doing their duty almost never do; turning on {opponent_name} takes a real reason (an old grudge, a friend or their own lord on the other side, plain murder being done in front of them). Always empty in a fist fight or a friendly duel.
- ""onlooker"": does someone watching step in? ""side"": ""theirs"", ""yours"" or """" (nobody: the usual answer; people keep out of other people's fights unless it is their kin, their lord, or it has plainly become murder). ""at"": when {opponent_name} has that share of strength left. ""line"": what that person shouts as they come in.
Answer ONLY with JSON: { ""conduct"": { ""yields_at"": 0, ""yield_line"": """", ""flees_at"": 0, ""flee_line"": """", ""truce_at"": 0, ""truce_line"": """", ""calls_help_at"": 0, ""help_line"": """", ""accepts_truce"": false, ""truce_answer"": """", ""accepts_yield"": true, ""yield_answer"": """", ""helpers"": [], ""onlooker"": { ""side"": """", ""at"": 0, ""line"": """" } }, ""opening"": ""as it starts"", ""when_hit"": [""on taking a blow"", ""another"", ""another""], ""when_hitting"": [""on landing a blow"", ""another"", ""another""], ""losing"": ""when clearly getting the worst of it"", ""victory"": ""standing over the beaten {player}"", ""defeat"": ""beaten, from the ground"", ""crowd"": [""an onlooker's shout"", ""another"", ""another"", ""another""] }");

        public static void Touch() { var _ = TLines.Key; }

        private static readonly Random Rng = new Random();
        private static Agent _opp;
        private static string _name = "";
        private static Mission _mission;
        private static float _oppHp, _myHp, _gap, _crowd;
        private static bool _saidLosing;

        // How the opponent conducts the fight, decided by the AI when it starts. Shares of health left, 0 = never
        private static int _yieldsAt, _fleesAt, _truceAt, _helpAt, _onlookerAt;
        private static string _yieldLine, _fleeLine, _truceLine, _helpLine, _onlookerLine, _onlookerSide, _truceAnswer, _yieldAnswer;
        private static bool _didYield, _didFlee, _didTruce, _didHelp, _didOnlooker;
        // What those fighting beside the opponent would do, by the opponent's share of health left
        private class HelperPlan { public string Who, Line; public bool Turns, Done; public int At; }
        private static readonly List<HelperPlan> Plans = new List<HelperPlan>();
        /// <summary>What they answer if the player asks to stop. Before the AI has answered: friendly fights stop, others go on.</summary>
        public static bool AcceptsTruce { get; private set; }
        public static bool AcceptsYield { get; private set; } = true;
        public static string TruceAnswer => _truceAnswer ?? "";
        public static string YieldAnswer => _yieldAnswer ?? "";
        public static string OpponentName => _name;
        public static Agent Opponent => _opp;
        public static void Shout(string text) { if (_opp != null) Show(_opp, _name, text, Colors.Red); }
        private static string _losing, _victory, _defeat;
        private static readonly List<string> Hit = new List<string>(), Hitting = new List<string>(), Crowd = new List<string>();
        private static int _gen;

        private static void Show(Agent a, string who, string text, Color c)
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            try { if (a != null && a.IsActive()) Bubbles.Show(a, text, Math.Max(2.5f, Math.Min(5f, 1.5f + text.Length * 0.05f))); } catch { }
            InformationManager.DisplayMessage(new InformationMessage($"{who}: {text}", c));
        }

        private static string Pop(List<string> l)
        {
            if (l.Count == 0) return null;
            int i = Rng.Next(l.Count);
            string s = l[i];
            l.RemoveAt(i);
            return s;
        }

        private static string DescribeHelpers(IList<Agent> helpers)
        {
            if (helpers == null) return "nobody, this one is between the two of them";
            var names = new List<string>();
            foreach (var h in helpers)
            {
                try
                {
                    if (h == null || !h.IsActive()) continue;
                    var c = h.Character as CharacterObject;
                    names.Add(c?.HeroObject != null ? $"{h.Name} (of clan {c.HeroObject.Clan?.Name})" : c != null ? $"{h.Name} ({c.Occupation})" : h.Name);
                }
                catch { }
            }
            return names.Count == 0 ? "nobody yet, though others may join" : string.Join(", ", names);
        }

        /// <summary>helpers: who fights beside the opponent as it starts. Null in a friendly fight, where nobody else takes part.</summary>
        public static void Begin(Speaker who, Agent opp, string kind, IList<Agent> helpers = null)
        {
            try
            {
                Clear();
                var cfg = S.Get;
                if (!cfg.EnableFightBanter || who == null || opp == null || Agent.Main == null) return;
                _opp = opp; _name = who.Name; _mission = Mission.Current;
                AcceptsTruce = Combat.ActiveIsFriendly;
                _oppHp = opp.Health; _myHp = Agent.Main.Health; _gap = 2f; _crowd = 4f + (float)Rng.NextDouble() * 3f;
                int gen = ++_gen;

                string desc = Ambient.Describe(opp, out var _sp);
                string player = ContextBuilder.PlayerNameFor(who) ?? "a stranger";
                string between = (string.IsNullOrWhiteSpace(who.Memory.Summary) ? "they hardly know each other" : who.Memory.Summary)
                                 + (who.IsHero ? $" How {who.Name} feels about them: {ContextBuilder.DescribeRelation((int)who.Hero.GetRelationWithPlayer())}." : "");
                string what = kind == "brawl" ? "a fist fight, no weapons" : kind == "death" ? "a duel to the death with drawn steel" : "a duel with drawn steel, until one yields or falls";
                string prompt = TLines.Format("place", Settlement.CurrentSettlement?.Name?.ToString() ?? "the camp", "kind", what, "opponent_name", who.Name, "opponent", desc,
                    "player", player, "between", between, "max_chars", 70, "language", cfg.Language, "helpers", DescribeHelpers(helpers));

                int session = AILegacy.World.WorldSim.Session;
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "banter", maxTokensOverride: 520,
                    preferredProvider: who.IsHero ? cfg.ProviderHeroes : cfg.ProviderResidents).ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        try
                        {
                            if (session != AILegacy.World.WorldSim.Session || gen != _gen || _opp == null || res == null || !res.Ok) return;
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            void Fill(List<string> l, string key) { if (j[key] is JArray arr) l.AddRange(arr.Select(x => (x?.ToString() ?? "").Trim()).Where(x => x.Length > 1)); }
                            Fill(Hit, "when_hit"); Fill(Hitting, "when_hitting"); Fill(Crowd, "crowd");
                            _losing = ((string)j["losing"] ?? "").Trim(); _victory = ((string)j["victory"] ?? "").Trim(); _defeat = ((string)j["defeat"] ?? "").Trim();
                            if (j["conduct"] is JObject c)
                            {
                                int Pct(string key) { int.TryParse(new string((c[key]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int v); return Math.Max(0, Math.Min(90, v)); }
                                string Txt(JToken t) { string x = (t?.ToString() ?? "").Trim(); return x.Length > 120 ? x.Substring(0, 120) : x; }
                                bool Yes(string key, bool def) { var t = c[key]; return t == null ? def : t.Type == JTokenType.Boolean ? (bool)t : string.Equals(t.ToString().Trim(), "true", StringComparison.OrdinalIgnoreCase); }
                                _yieldsAt = Pct("yields_at"); _fleesAt = Pct("flees_at"); _truceAt = Pct("truce_at"); _helpAt = Pct("calls_help_at");
                                _yieldLine = Txt(c["yield_line"]); _fleeLine = Txt(c["flee_line"]); _truceLine = Txt(c["truce_line"]); _helpLine = Txt(c["help_line"]);
                                _truceAnswer = Txt(c["truce_answer"]); _yieldAnswer = Txt(c["yield_answer"]);
                                AcceptsTruce = Yes("accepts_truce", AcceptsTruce); AcceptsYield = Yes("accepts_yield", true) || Combat.ActiveIsFriendly;
                                if (c["onlooker"] is JObject o)
                                {
                                    _onlookerSide = Txt(o["side"]).ToLowerInvariant(); _onlookerLine = Txt(o["line"]);
                                    int.TryParse(new string((o["at"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out _onlookerAt);
                                    _onlookerAt = Math.Max(0, Math.Min(95, _onlookerAt));
                                }
                                if (c["helpers"] is JArray hs && !Combat.ActiveIsFriendly)
                                {
                                    foreach (var h in hs.OfType<JObject>().Take(4))
                                    {
                                        string does = Txt(h["does"]).ToLowerInvariant();
                                        int.TryParse(new string((h["at"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int at);
                                        if (at <= 0 || (does != "stand_down" && does != "turn")) continue;
                                        Plans.Add(new HelperPlan { Who = Txt(h["who"]), Line = Txt(h["line"]), Turns = does == "turn", At = Math.Min(95, at) });
                                    }
                                    if (Plans.Count > 0) Log.Info("[fight] those helping: " + string.Join("; ", Plans.Select(p => $"{p.Who} {(p.Turns ? "switches sides" : "quits")} at {p.At} %")));
                                }
                                Log.Info($"[fight] behaviour of {_name}: yields at {_yieldsAt} %, flees at {_fleesAt} %, truce at {_truceAt} %, asks for help at {_helpAt} %; accepts truce: {AcceptsTruce}; accepts yield: {AcceptsYield}; onlooker: {_onlookerSide} at {_onlookerAt} %");
                            }
                            Show(_opp, _name, ((string)j["opening"] ?? "").Trim(), Colors.Red);
                            _gap = 3f;
                        }
                        catch (Exception ex) { Log.Error("Fight lines", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Fight lines: start", ex); Clear(); }
        }

        public static void Tick(float dt)
        {
            if (_opp == null) return;
            try
            {
                if (Mission.Current != _mission || Agent.Main == null) { Clear(); return; }
                if (!_opp.IsActive() || !Agent.Main.IsActive()) return;      // End() announces the outcome
                _gap -= dt; _crowd -= dt;
                float oh = _opp.Health, mh = Agent.Main.Health;
                bool oppHurt = oh < _oppHp - 1f, meHurt = mh < _myHp - 1f;
                _oppHp = oh; _myHp = mh;

                // What they decided to do as the fight turns against them. One thing at a time, each only once
                int left = _opp.HealthLimit > 0f ? (int)(oh * 100f / _opp.HealthLimit) : 100;
                if (!_didOnlooker && _onlookerAt > 0 && left <= _onlookerAt && (_onlookerSide == "theirs" || _onlookerSide == "yours"))
                {
                    _didOnlooker = true;
                    Combat.OnlookerStepsIn(_onlookerSide == "yours", _onlookerLine);
                }
                foreach (var p in Plans)
                {
                    if (p.Done || left > p.At) continue;
                    p.Done = true;
                    Combat.HelperWavers(p.Who, p.Turns, p.Line);
                }
                if (!_didHelp && _helpAt > 0 && left <= _helpAt) { _didHelp = true; Show(_opp, _name, _helpLine, Colors.Red); Combat.OpponentCallsHelp(); _gap = 4f; return; }
                if (!_didFlee && _fleesAt > 0 && left <= _fleesAt) { _didFlee = true; Show(_opp, _name, _fleeLine, Colors.Red); if (Combat.OpponentFlees()) return; }
                if (!_didTruce && _truceAt > 0 && left <= _truceAt) { _didTruce = true; Show(_opp, _name, _truceLine, Colors.Red); if (Combat.OpponentOffersTruce()) return; }
                if (!_didYield && _yieldsAt > 0 && left <= _yieldsAt) { _didYield = true; Show(_opp, _name, _yieldLine, Colors.Red); if (Combat.OpponentYields()) return; }

                if (!_saidLosing && !string.IsNullOrEmpty(_losing) && oh < _opp.HealthLimit * 0.35f)
                {
                    _saidLosing = true; _gap = 4f;
                    Show(_opp, _name, _losing, Colors.Red);
                    return;
                }
                if (_gap <= 0f && (oppHurt || meHurt))
                {
                    string line = Pop(oppHurt ? Hit : Hitting);
                    if (line != null) { Show(_opp, _name, line, Colors.Red); _gap = 4f + (float)Rng.NextDouble() * 2f; }
                }
                if (_crowd <= 0f && Crowd.Count > 0)
                {
                    _crowd = 6f + (float)Rng.NextDouble() * 5f;
                    var near = _mission.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a != _opp && a.IsAIControlled
                                                          && a.Position.Distance(Agent.Main.Position) <= 15f).ToList();
                    if (near.Count > 0)
                    {
                        var who = near[Rng.Next(near.Count)];
                        Show(who, who.Name, Pop(Crowd), new Color(0.78f, 0.78f, 0.7f));
                    }
                }
            }
            catch (Exception ex) { Log.Error("Fight lines: tick", ex); Clear(); }
        }

        /// <summary>
        /// The opponent's last word. Someone knocked out cold or dead says nothing (Combat narrates it). After a truce or
        /// a flight they already spoke their line. If the AI never answered, a short fixed line, so it is never a random remark.
        /// </summary>
        public static void End(bool playerWon, string ending = null)
        {
            try
            {
                if (_opp != null && _opp.IsActive() && (ending == null || ending == "yielded" || ending == "player_yielded"))
                {
                    string line = playerWon ? _defeat : _victory;
                    if (string.IsNullOrWhiteSpace(line))
                        line = playerWon ? L.T("AIL_e7b1c9a5", "Basta... Tú ganas.") : L.T("AIL_e7b1c9a6", "¡Y que te sirva de lección!");
                    Show(_opp, _name, line, Colors.Yellow);
                }
            }
            catch { }
            Clear();
        }

        private static void Clear()
        {
            _gen++;
            _opp = null; _mission = null; _saidLosing = false; _losing = _victory = _defeat = null;
            _yieldsAt = _fleesAt = _truceAt = _helpAt = _onlookerAt = 0;
            _yieldLine = _fleeLine = _truceLine = _helpLine = _onlookerLine = _onlookerSide = _truceAnswer = _yieldAnswer = null;
            _didYield = _didFlee = _didTruce = _didHelp = _didOnlooker = false;
            AcceptsTruce = false; AcceptsYield = true;
            Hit.Clear(); Hitting.Clear(); Crowd.Clear(); Plans.Clear();
        }
    }
}
