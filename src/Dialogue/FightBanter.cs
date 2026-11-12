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
    /// Lo que se dice durante una pelea o un duelo en una escena: tu rival cuando recibe, cuando te da, cuando se ve perdido,
    /// y los mirones de alrededor. Se pide TODO de una vez al empezar (una sola petición) y se va soltando según va la pelea.
    /// </summary>
    internal static class FightBanter
    {
        private static readonly Tpl TLines = new Tpl("combat/fight_lines.txt",
@"A fight has just broken out in {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord: {kind}.
One fighter is {opponent_name}: {opponent}
What lies between {opponent_name} and the other fighter, {player}: {between}
Around them stand onlookers (townsfolk, soldiers, whoever was near).
Write what {opponent_name} blurts out as the fight goes, in their own voice and temper, and what the onlookers shout. These are things gasped or barked mid-fight: very short, under {max_chars} characters each, in {language}. No narration, no asterisks. A fist fight is rough and half in sport; drawn steel is deadly earnest; a duel to the death is grim.
Answer ONLY with JSON: { ""opening"": ""as it starts"", ""when_hit"": [""on taking a blow"", ""another"", ""another""], ""when_hitting"": [""on landing a blow"", ""another"", ""another""], ""losing"": ""when clearly getting the worst of it"", ""victory"": ""standing over the beaten {player}"", ""defeat"": ""beaten, from the ground"", ""crowd"": [""an onlooker's shout"", ""another"", ""another"", ""another""] }");

        public static void Touch() { var _ = TLines.Key; }

        private static readonly Random Rng = new Random();
        private static Agent _opp;
        private static string _name = "";
        private static Mission _mission;
        private static float _oppHp, _myHp, _gap, _crowd;
        private static bool _saidLosing;
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

        public static void Begin(Speaker who, Agent opp, string kind)
        {
            try
            {
                Clear();
                var cfg = S.Get;
                if (!cfg.EnableFightBanter || who == null || opp == null || Agent.Main == null) return;
                _opp = opp; _name = who.Name; _mission = Mission.Current;
                _oppHp = opp.Health; _myHp = Agent.Main.Health; _gap = 2f; _crowd = 4f + (float)Rng.NextDouble() * 3f;
                int gen = ++_gen;

                string desc = Ambient.Describe(opp, out var _sp);
                string player = ContextBuilder.PlayerNameFor(who) ?? "a stranger";
                string between = (string.IsNullOrWhiteSpace(who.Memory.Summary) ? "they hardly know each other" : who.Memory.Summary)
                                 + (who.IsHero ? $" How {who.Name} feels about them: {ContextBuilder.DescribeRelation((int)who.Hero.GetRelationWithPlayer())}." : "");
                string what = kind == "brawl" ? "a fist fight, no weapons" : kind == "death" ? "a duel to the death with drawn steel" : "a duel with drawn steel, until one yields or falls";
                string prompt = TLines.Format("place", Settlement.CurrentSettlement?.Name?.ToString() ?? "the camp", "kind", what, "opponent_name", who.Name, "opponent", desc,
                    "player", player, "between", between, "max_chars", 70, "language", cfg.Language);

                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, maxTokensOverride: 450,
                    preferredProvider: who.IsHero ? cfg.ProviderHeroes : cfg.ProviderResidents).ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        try
                        {
                            if (gen != _gen || _opp == null || res == null || !res.Ok) return;
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            void Fill(List<string> l, string key) { if (j[key] is JArray arr) l.AddRange(arr.Select(x => (x?.ToString() ?? "").Trim()).Where(x => x.Length > 1)); }
                            Fill(Hit, "when_hit"); Fill(Hitting, "when_hitting"); Fill(Crowd, "crowd");
                            _losing = ((string)j["losing"] ?? "").Trim(); _victory = ((string)j["victory"] ?? "").Trim(); _defeat = ((string)j["defeat"] ?? "").Trim();
                            Show(_opp, _name, ((string)j["opening"] ?? "").Trim(), Colors.Red);
                            _gap = 3f;
                        }
                        catch (Exception ex) { Log.Error("Frases de pelea", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Frases de pelea: empezar", ex); Clear(); }
        }

        public static void Tick(float dt)
        {
            if (_opp == null) return;
            try
            {
                if (Mission.Current != _mission || Agent.Main == null) { Clear(); return; }
                if (!_opp.IsActive() || !Agent.Main.IsActive()) return;      // el final lo anuncia End()
                _gap -= dt; _crowd -= dt;
                float oh = _opp.Health, mh = Agent.Main.Health;
                bool oppHurt = oh < _oppHp - 1f, meHurt = mh < _myHp - 1f;
                _oppHp = oh; _myHp = mh;

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
            catch (Exception ex) { Log.Error("Frases de pelea: tick", ex); Clear(); }
        }

        public static void End(bool playerWon)
        {
            try { if (_opp != null) Show(_opp, _name, playerWon ? _defeat : _victory, Colors.Yellow); }
            catch { }
            Clear();
        }

        private static void Clear()
        {
            _gen++;
            _opp = null; _mission = null; _saidLosing = false; _losing = _victory = _defeat = null;
            Hit.Clear(); Hitting.Clear(); Crowd.Clear();
        }
    }
}
