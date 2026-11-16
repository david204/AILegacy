using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using Newtonsoft.Json.Linq;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Unfinished business. When a talk in a scene ends (a group talk you were in, or a circle you could hear) one short
    /// request asks whether something was left undone that one of those still here would act on now: an order not carried
    /// out, a threat or accusation left hanging, an unanswered question, a promise to deal with someone. If so, that person
    /// goes to the other one now: to the player, they walk up and speak first; to someone else, they go and talk to them.
    /// At most twice for the same pair and once every 90 seconds for the same person in a scene.
    /// </summary>
    internal static class SceneMatters
    {
        private static readonly Tpl TMatter = new Tpl("scene/scene_matter.txt",
@"### A talk has just ended ###
Below are the place, the last lines of a talk that has just ended and the people still here. Decide whether something was left undone that one of these people would really act on now, here.
- Undone means: an order given and not carried out, a threat or an accusation left hanging, a question nobody answered, a promise to deal with someone, a suspicion someone means to act on.
- Judge by who they are (rank, duty, character, ties) and by everything that was said. Most talks end with nothing left to do; then answer nobody.
- Whoever acts goes to the other person now. The player speaks for themselves and never acts here, but can be the one someone goes to.
- A line written ""A (to B): ..."" was said by A to B.

Answer with JSON only: {""actor"": ""<id of the one who acts>"", ""target"": ""<id of the one they go to>"", ""matter"": ""what they go to do and why, in a few words, as they see it""} or {""actor"": ""nobody""}.

### The scene ###
Place: {place}

The talk (oldest first; it has just ended):
{transcript}

People here now:
{people}");

        private static readonly Tpl TUnfinished = new Tpl("scene/unfinished_business.txt",
@"### Unfinished business ###
A talk here has just ended, but something was left undone: {matter}. You have gone to {target} to deal with it now. Speak to them about it, as yourself.");

        public static void Touch() { var _ = TMatter.Key + TUnfinished.Key; }

        private const float Reach = 30f, Cooldown = 90f;
        private const int PerPair = 2, Lines = 12;

        private static Mission _mission;
        private static readonly Dictionary<string, int> Pairs = new Dictionary<string, int>();
        private static readonly Dictionary<Agent, float> LastAt = new Dictionary<Agent, float>();
        private static volatile bool _asking;

        public static void ResetSession() { _mission = null; Pairs.Clear(); LastAt.Clear(); _asking = false; }

        /// <summary>
        /// A talk in a scene ended. <paramref name="people"/>: who took part; <paramref name="said"/>: its lines, "Name (to X): line".
        /// <paramref name="playerIn"/>: the player was one of them; otherwise one of them must have been within earshot.
        /// </summary>
        public static void After(string what, string place, IList<(Agent agent, Speaker who, string desc)> people, IList<string> said, bool playerIn)
        {
            try
            {
                var m = Mission.Current;
                var me = Agent.Main;
                if (m == null || me == null || !me.IsActive() || people == null || said == null) return;
                if (m != _mission) { _mission = m; Pairs.Clear(); LastAt.Clear(); }
                var lines = said.Where(l => !string.IsNullOrWhiteSpace(l) && !l.StartsWith("(")).ToList();
                if (lines.Count < 3 || _asking) return;
                var here = people.Where(p => p.agent != null && p.who != null && p.agent.IsActive() && p.agent != me && p.agent.Position.Distance(me.Position) <= Reach)
                                 .GroupBy(p => p.agent).Select(g => g.First()).ToList();
                if (here.Count == 0) return;
                if (!playerIn && !here.Any(p => p.agent.Position.Distance(me.Position) <= Math.Max(1, S.Get.OverhearRadius))) return;

                var ids = new Dictionary<string, (Agent agent, Speaker who, string desc)>(StringComparer.OrdinalIgnoreCase);
                var sb = new System.Text.StringBuilder();
                int n = 0;
                foreach (var p in here)
                {
                    string id = "p" + (++n);
                    ids[id] = p;
                    sb.AppendLine($"- {id}: {p.who.Name}" + (string.IsNullOrWhiteSpace(p.desc) ? "" : ", " + p.desc.Trim()));
                }
                string player = TaleWorlds.CampaignSystem.Hero.MainHero?.Name?.ToString() ?? "the player";
                sb.AppendLine($"- player: {player}, the player");
                string transcript = string.Join("\n", lines.Skip(Math.Max(0, lines.Count - Lines)).Select(l => "- " + l));
                string ask = TMatter.Format("place", place ?? "here", "transcript", transcript, "people", sb.ToString().TrimEnd());
                int session = WorldSim.Session;
                _asking = true;
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", ask) }, wantJson: true, maxTokensOverride: 200, task: "scene_matter")
                    .ContinueWith(t =>
                    {
                        var r = t.IsFaulted ? null : t.Result;
                        _asking = false;        // here, not on the main thread, so a scene that ends meanwhile cannot leave it stuck
                        MainThread.Post(() =>
                        {
                            if (session != WorldSim.Session || Mission.Current != m) return;
                            Take(what, r, ids, m);
                        });
                    });
            }
            catch (Exception ex) { _asking = false; Log.Error("Unfinished business: question", ex); }
        }

        private static void Take(string what, LlmResult r, Dictionary<string, (Agent agent, Speaker who, string desc)> ids, Mission m)
        {
            try
            {
                if (r == null || !r.Ok) { Log.Warn($"[scene] unfinished business after {what}: no answer ({r?.Error})"); return; }
                int a = r.Text.IndexOf('{'), b = r.Text.LastIndexOf('}');
                if (a < 0 || b <= a) { Log.Info($"[scene] unfinished business after {what}: the answer is not JSON"); return; }
                var j = JObject.Parse(r.Text.Substring(a, b - a + 1));
                string actorId = ((string)j["actor"] ?? "").Trim(), targetId = ((string)j["target"] ?? "").Trim();
                string matter = ((string)j["matter"] ?? "").Trim();
                if (actorId.Length == 0 || actorId.Equals("nobody", StringComparison.OrdinalIgnoreCase)) { Log.Info($"[scene] after {what}: nothing left unfinished"); return; }
                if (matter.Length == 0) matter = "something was left unsettled";
                if (!ids.TryGetValue(actorId, out var actor) || actor.agent == null || !actor.agent.IsActive())
                { Log.Info($"[scene] after {what}: unusable unfinished business ({actorId} -> {targetId})"); return; }
                bool toPlayer = targetId.Equals("player", StringComparison.OrdinalIgnoreCase);
                Agent target = toPlayer ? Agent.Main : ids.TryGetValue(targetId, out var t) ? t.agent : null;
                if (target == null || !target.IsActive() || target == actor.agent)
                { Log.Info($"[scene] after {what}: unusable unfinished business ({actorId} -> {targetId})"); return; }

                string pair = actor.who.Key + ">" + (toPlayer ? "player" : ids[targetId].who.Key);
                float now = m.CurrentTime;
                Pairs.TryGetValue(pair, out int times);
                if (times >= PerPair || (LastAt.TryGetValue(actor.agent, out float last) && now - last < Cooldown))
                { Log.Info($"[scene] after {what}: {actor.who.Name} already went after this, leaves it for now"); return; }

                string targetName = toPlayer ? (TaleWorlds.CampaignSystem.Hero.MainHero?.Name?.ToString() ?? "the player") : ids[targetId].who.Name;
                bool went;
                if (toPlayer)
                {
                    var s = actor.who;
                    s.SituationKind = "initiative";
                    s.Situation = TUnfinished.Format("matter", matter, "target", targetName);
                    went = Initiative.Start(actor.agent, s, s.Situation + " Speak first.");
                }
                else
                {
                    went = !Ambient.ErrandBusy;
                    if (went) Ambient.SendTo(actor.agent, target, TUnfinished.Format("matter", matter, "target", targetName), false);
                }
                if (!went) { Log.Info($"[scene] after {what}: {actor.who.Name} could not go to {targetName}"); return; }
                Pairs[pair] = times + 1;
                LastAt[actor.agent] = now;
                Log.Info($"[scene] after {what}: {actor.who.Name} goes to {targetName} about unfinished business: {matter}");
            }
            catch (Exception ex) { Log.Error("Unfinished business: answer", ex); }
        }
    }
}
