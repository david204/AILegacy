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
    /// Conversación de grupo en una escena: hablas a todos los que tienes cerca y la IA
    /// decide quién contesta y en qué orden. Las respuestas salen sobre sus cabezas y en el registro del juego.
    /// </summary>
    internal static class GroupTalk
    {
        private class Member { public Agent Agent; public Speaker Who; public string Desc; }

        private static readonly List<Member> Circle = new List<Member>();
        private static readonly List<string> Transcript = new List<string>();     // «Nombre: frase», en orden
        private static Mission _mission;
        private static bool _waiting;
        private static float _idle;
        private static Agent _addressed;      // a quién mirabas al abrir la ventana
        // Si te quedas callado, alguien sigue la conversación por su cuenta (un par de veces como mucho)
        private static float _silence;
        private static int _followUps;
        private static bool _silentTurn;
        private static DateTime _lastRumor = DateTime.MinValue;
        private const int SlowId = 751204;
        private static Mission _slowed;

        private static void Unslow()
        {
            var m = _slowed; _slowed = null;
            try { if (m != null && Mission.Current == m) m.RemoveTimeSpeedRequest(SlowId); } catch { }
        }

        /// <summary>El personaje al que estás mirando (el más centrado en tu mirada, a esa distancia como mucho).</summary>
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

Everything under 'The talk so far' and each person's 'JUST NOW' and latest words HAS HAPPENED and they remember it: nobody asks again what they were already told, nobody is surprised twice by the same thing, and someone who has just come back from speaking to another person answers from what they learned there.
Decide who answers, as it would really happen: usually the one addressed by name or the one it concerns most, sometimes a second person cuts in, agrees, contradicts or mocks; people with nothing to say stay silent. Nobles speak before commoners dare to.
Length by station: lords, ladies and other people listed by name may speak fully, up to the limit. Soldiers, guards and common folk answer with ONE short sentence (under 80 characters).
Going to someone: if what was said gives a person here a real reason to go and speak to someone listed under 'Elsewhere' (they are told that person is looking for them, they are sent with a message, they must warn or ask them), and they believe it enough to bother, they say so in their line and you set ""goes_to"" to that person's exact name: they will walk there and talk to them. Set ""returns"" to true if they would come back afterwards to tell {player} what came of it. Someone with no reason to trust {player}, or of too high a station to be sent on errands, may refuse or send {player} instead: then leave ""goes_to"" empty. Never use it without a reason given in what was said.
Other things a speaker can do:
{actions}
A line may carry ""goes_to"" or ""action"", never both.
Each line also carries ""mood"": how the speaker looks while saying it, one of: {moods}.
Rules: 1 to {max_lines} lines in total, each under {max_chars} characters, in {language}. Each line is spoken words only (a brief *gesture* is allowed). Nobody speaks for {player}. Nobody uses a name they have not been told or would not know. Stay in character; never mention games or AI.
Answer ONLY with JSON: { ""lines"": [ { ""who"": <number of the person from the list>, ""text"": ""..."", ""mood"": ""..."", ""goes_to"": """", ""returns"": false, ""action"": """" } ], ""summary"": ""what was said in this exchange, ONE English sentence"", ""between"": [ { ""from"": <number>, ""to"": <number>, ""change"": -2 to 2, ""why"": ""few English words"" } ] (only if in this exchange one person present was hurt, insulted, defended, shamed or won over by ANOTHER person present, not by {player}; usually empty), ""toward_player"": [ { ""who"": <number>, ""relation"": -2 to 2, ""trust"": -3 to 3 } ] (only for someone whose opinion of {player}, or trust in their word, truly moved because of what {player} just said or did; usually empty), ""rumor_title"": ""a 3-6 word headline of that rumour in {language}, or empty"", ""rumor_local"": ""the rumour itself in {language}, or empty"", ""rumor"": ""almost always empty. Only if {player} has just told these people news of public weight that they will repeat to others whether they believe it or not (a death, a crime, a betrayal, a scandal touching named nobles or a crown), or if something happened here that the whole place will talk about: ONE neutral English sentence, worded as hearsay when it is only something they were told"" }");

        public static void Touch() { var _ = TGroup.Key; }

        /// <summary>¿Está ese personaje en tu conversación de grupo? (mientras lo esté, no se pone a charlar por su cuenta)</summary>
        internal static bool Has(Agent a) => Circle.Any(x => x.Agent == a);

        /// <summary>Algo dicho fuera del turno (alguien que vuelve de un recado) queda en lo hablado.</summary>
        /// <summary>Alguien a quien han llamado llega y entra en la conversación.</summary>
        internal static void Join(Agent a)
        {
            try
            {
                if (a == null || !a.IsActive() || Circle.Any(x => x.Agent == a) || Circle.Count > S.Get.GroupMaxParticipants) return;
                if (Mission.Current != _mission) { Transcript.Clear(); _mission = Mission.Current; }
                string desc = Ambient.Describe(a, out var sp);
                Circle.Add(new Member { Agent = a, Who = sp, Desc = desc });
                Transcript.Add($"({sp.Name} has come over and joined.)");
                _idle = 0f;
            }
            catch (Exception ex) { Log.Error("Entrar en el grupo", ex); }
        }

        internal static void Note(string line) { if (_mission != null && Mission.Current == _mission && !string.IsNullOrWhiteSpace(line)) Transcript.Add(line); }

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

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

        public static void Tick(float dt)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableGroupTalk || !SceneOk()) { if (Circle.Count > 0) End(false); return; }

                // El grupo se deshace si te alejas, si cambias de escena o si pasa mucho rato sin hablar
                if (Circle.Count > 0)
                {
                    _idle += dt;
                    Circle.RemoveAll(x => x.Agent == null || !x.Agent.IsActive() || x.Agent.Position.Distance(Agent.Main.Position) > cfg.GroupGraceRadius);
                    if (Mission.Current != _mission || Circle.Count == 0 || _idle > cfg.GroupIdleSeconds) End(true);
                }

                // Silencio: si no dices nada tras una ronda, la IA decide si alguien sigue hablando
                if (Circle.Count > 0 && cfg.GroupSilenceSeconds > 0 && Transcript.Count > 0 && _followUps < cfg.GroupSilenceFollowUps)
                {
                    bool busy = _waiting || Ambient.IsPlaying || Ambient.ErrandBusy || ChatSession.IsBusy || Initiative.Busy || Campaign.Current.ConversationManager.IsConversationInProgress;
                    try { busy = busy || InformationManager.IsAnyInquiryActive(); } catch { }
                    if (busy) _silence = 0f;
                    else if ((_silence += dt) >= cfg.GroupSilenceSeconds)
                    {
                        _silence = 0f; _followUps++;
                        _silentTurn = true;
                        OnSaid("…");
                        _silentTurn = false;
                    }
                }

                // Por si la ventana se cerró sin pasar por sus botones: que no se quede el mundo a cámara lenta
                if (_slowed != null) { bool open = true; try { open = InformationManager.IsAnyInquiryActive(); } catch { } if (!open) Unslow(); }

                bool alt = Input.IsKeyDown(InputKey.LeftAlt) || Input.IsKeyDown(InputKey.RightAlt);
                // Alt+G: das la conversación por terminada
                if (alt && Input.IsKeyPressed(InputKey.G) && Circle.Count > 0 && !_waiting) { End(true); return; }
                if (!alt || !Input.IsKeyPressed(InputKey.T)) return;
                if (_waiting || ChatSession.IsBusy || Initiative.Busy || Campaign.Current.ConversationManager.IsConversationInProgress) return;
                Open();
            }
            catch (Exception ex) { Log.Error("Conversación de grupo", ex); End(false); }
        }

        private static void Open()
        {
            var cfg = S.Get;
            if (!Config.LegacyConfig.Current.Providers.Any(p => p.IsUsable)) { Say("AI Legacy: no hay ninguna api_key configurada.", Colors.Red); return; }
            var m = Mission.Current;
            var me = Agent.Main.Position;

            // A quien miras se le habla a él, aunque esté algo más lejos: se acerca
            _addressed = LookedAt(cfg.GroupRadius * 2f);
            if (_addressed != null && !Circle.Any(x => x.Agent == _addressed) && !(_addressed.Team != null && Agent.Main.Team != null && _addressed.IsEnemyOf(Agent.Main)))
            {
                string d0 = Ambient.Describe(_addressed, out var sp0);
                Circle.Insert(0, new Member { Agent = _addressed, Who = sp0, Desc = d0 });
                if (_addressed.Position.Distance(me) > 3.5f) SceneActions.Summon(_addressed);
            }

            // Se suman los que tengas cerca ahora (hasta el máximo); los que ya estaban, siguen
            foreach (var a in m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject
                                                  && a.Position.Distance(me) <= cfg.GroupRadius && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main)))
                                      .OrderBy(a => a.Position.Distance(me)))
            {
                if (Circle.Count >= cfg.GroupMaxParticipants) break;
                if (Circle.Any(x => x.Agent == a)) continue;
                string desc = Ambient.Describe(a, out var sp);
                Circle.Add(new Member { Agent = a, Who = sp, Desc = desc });
            }
            if (Circle.Count == 0) { Say(L.F("AIL_71b565dc", $"No hay nadie a menos de {cfg.GroupRadius} metros a quien hablar."), Colors.Gray); return; }
            // Se ponen a tu alrededor y te miran: los que están algo apartados se acercan (quien está sentado u ocupado, se queda)
            SceneActions.Arrange(Circle.Select(x => x.Agent).ToList());
            if (_mission != m) { Transcript.Clear(); _mission = m; }
            _idle = 0f;

            string names = string.Join(", ", Circle.Select(x => x.Who.Name));
            string last = Transcript.Count == 0 ? "" : L.T("AIL_cbe9f6a8", "\n\nLo último:\n") + string.Join("\n", Transcript.Skip(Math.Max(0, Transcript.Count - 4)));
            var looked = _addressed != null ? Circle.FirstOrDefault(x => x.Agent == _addressed) : null;
            string title = looked != null ? L.F("AIL_6e7730a9", $"Hablas a {looked.Who.Name}") : L.T("AIL_6d3c2db8", "Hablas al grupo");
            string how = L.T("AIL_dd02342f", "\n\n(Empieza con * para susurrárselo solo a quien miras, o con ! para gritarlo. Alt+G termina la conversación.)");
            // Mientras escribes el mundo no se para: va a cámara lenta
            bool slow = cfg.GroupSlowMotion;
            if (slow) { try { m.AddTimeSpeedRequest(new Mission.TimeSpeedRequest(0.2f, SlowId)); _slowed = m; } catch { slow = false; } }
            InformationManager.ShowTextInquiry(new TextInquiryData(title, L.F("AIL_26906366", $"Te oyen: {names}.{last}{how}"), true, true, L.T("AIL_aa23f794", "Decir"), L.T("AIL_b8f9f7be", "Callar"),
                t => { Unslow(); OnSaid(t); }, Unslow, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_e07caced", "Escribe algo"))), !slow);
        }

        private static void OnSaid(string text)
        {
            text = (text ?? "").Trim();
            if (text.Length == 0 || _waiting || Circle.Count == 0) return;
            var cfg = S.Get;
            var m = Mission.Current;
            var st = Settlement.CurrentSettlement;
            bool silent = _silentTurn;
            if (!silent) { _idle = 0f; _followUps = 0; }
            _silence = 0f;

            // Susurro (*) o grito (!)
            bool whisper = !silent && text.StartsWith("*"), shout = !silent && !whisper && text.StartsWith("!");
            if (whisper || shout) text = text.Substring(1).Trim();
            if (text.Length == 0) return;
            var addressed = _addressed != null ? Circle.FirstOrDefault(x => x.Agent == _addressed && x.Agent.IsActive()) : null;
            if (whisper && addressed == null) addressed = Circle.OrderBy(x => x.Agent.Position.Distance(Agent.Main.Position)).First();
            if (shout)
            {
                // Un grito llega al doble de distancia: los que lo oyen entran en la conversación
                foreach (var a in m.Agents.Where(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject
                                                      && a.Position.Distance(Agent.Main.Position) <= cfg.GroupRadius * 2f && !Circle.Any(x => x.Agent == a)
                                                      && !(a.Team != null && Agent.Main.Team != null && a.IsEnemyOf(Agent.Main))).OrderBy(a => a.Position.Distance(Agent.Main.Position)))
                {
                    if (Circle.Count >= cfg.GroupMaxParticipants) break;
                    string dsc = Ambient.Describe(a, out var spx);
                    Circle.Add(new Member { Agent = a, Who = spx, Desc = dsc });
                }
            }
            string manner = whisper ? $"{{player}} WHISPERS this to {addressed.Who.Name} alone. The others see the two of them whispering but do NOT hear the words: only {addressed.Who.Name} can answer what was said (in a low voice); another person may at most remark on the whispering itself."
                          : shout ? "{player} SHOUTS this so that everyone around hears it; people react to being shouted at as their station and temper dictate."
                          : addressed != null ? $"{{player}} is looking at {addressed.Who.Name} while speaking: it is {addressed.Who.Name} who is being addressed and who answers first; others join in only if they have something to add."
                          : "{player} speaks to the group at large.";

            // Los que están cerca sin estar en el corro: han oído, y pueden meterse si les va algo en ello
            var bystanders = new List<Agent>();
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

            string playerName = Hero.MainHero.Name.ToString();
            var members = new StringBuilder();
            for (int i = 0; i < Circle.Count; i++)
            {
                var x = Circle[i];
                string knows = ContextBuilder.PlayerNameFor(x.Who) != null ? "knows who they are" : "does not know who they are";
                string mem = x.Who.IsHero && !string.IsNullOrWhiteSpace(x.Who.Memory.Summary) ? $" Past dealings with them: {x.Who.Memory.Summary}" : "";
                string rel = x.Who.IsHero ? $" Opinion of them: {ContextBuilder.DescribeRelation((int)x.Who.Hero.GetRelationWithPlayer())}." : "";
                // Lo reciente: sin esto, quien acaba de volver de hablar con otro contesta como si no hubiera pasado nada
                var xm = x.Who.Memory;
                string recent = "";
                if (!string.IsNullOrWhiteSpace(xm.RecentTalk) && (DateTime.UtcNow - xm.RecentTalkAt).TotalMinutes < 30) recent += $" JUST NOW: {xm.RecentTalk}.";
                var lastTurns = xm.Turns.Skip(Math.Max(0, xm.Turns.Count - 6)).Select(t => (t.Role == "npc" ? x.Who.Name : playerName) + ": " + (t.Text.Length > 160 ? t.Text.Substring(0, 160) + "…" : t.Text)).ToList();
                if (lastTurns.Count > 0) recent += " Their latest words with them — " + string.Join(" / ", lastTurns) + ".";
                var heardP = InfoExchange.AboutPlayer(xm).ToList();
                var heard = heardP.Skip(Math.Max(0, heardP.Count - 2)).Where(h => !string.IsNullOrWhiteSpace(h.Text)).Select(h => $"{h.FromName}: {h.Text}").ToList();
                if (heard.Count > 0) recent += " Lately heard from others — " + string.Join(" / ", heard) + ".";
                // Lo que hay entre él y los demás presentes
                string among = "";
                if (cfg.EnableNpcBonds && x.Who.IsHero)
                    foreach (var y in Circle)
                        if (y != x && y.Who.IsHero && x.Who.Memory.Others.TryGetValue(y.Who.Hero.StringId, out var ob))
                            among += $" Towards {y.Who.Name}: {Bonds.OtherWord(ob.Trust)}" + (ob.Notes.Count > 0 ? $" (last: {ob.Notes[ob.Notes.Count - 1]})" : "") + ".";
                members.AppendLine($"{i + 1}. {x.Who.Name} — {x.Desc}. {knows}.{rel}{mem}{recent}{among}");
            }
            string transcript = Transcript.Count == 0 ? "(nothing yet)" : string.Join("\n", Transcript.Skip(Math.Max(0, Transcript.Count - 16)));
            string context = st != null ? Ambient.Context(st, Circle[0].Who) : "(on the road)";
            // Gente con nombre que está en el lugar pero no en el corro: se puede ir a buscarla
            var away = new List<Agent>();
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
            if (silent)
            {
                text = "(says nothing)";
                manner = "{player} has said NOTHING for a while and just stands there listening. The others do NOT wait for them, do NOT ask why they are silent and do NOT speak to them: " +
                         "they carry on talking BETWEEN THEMSELVES, as people do when a bystander goes quiet — picking up what was last said, disagreeing, or turning to their own concerns (family, money, the war, gossip about someone they both know). " +
                         "Each line is addressed to another person present, never to {player}. Prefer someone who has spoken little so far. At most 2 lines. " +
                         "Only if there is one single person present (nobody else to talk to) may they say one short thing to {player} or simply go back to their business; if nobody would naturally speak, return an empty \"lines\" list.";
            }
            manner = manner.Replace("{player}", Hero.MainHero.Name.ToString());
            string prompt = TGroup.Format("manner", manner, "bystanders", byText, "actions", cfg.SceneErrands ? SceneActions.Help.Replace("{player}", playerName) : "(nothing else)", "moods", Gestures.Moods,
                "place", st?.Name?.ToString() ?? "the camp", "player", playerName, "members", members.ToString().TrimEnd(), "context", context, "elsewhere", elsewhere,
                "transcript", transcript, "said", text.Replace("«", "\"").Replace("»", "\""), "max_lines", cfg.GroupMaxLines, "max_chars", cfg.GroupMaxChars, "language", cfg.Language);

            if (!silent)
            {
                Transcript.Add($"{playerName}: {text}");
                Bubbles.Show(Agent.Main, text, Math.Max(2.5f, Math.Min(7f, 1.5f + text.Length * 0.05f)));
                if (whisper) Ambient.Overhear(addressed.Who, "");      // un susurro no lo oye nadie más
                else Ambient.Overhear(Circle.Select(x => x.Who).ToList(), text);   // una vez por frase; los del corro no la oyen de pasada
            }
            _waiting = true;
            var circle = Circle.ToList();

            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "group", maxTokensOverride: 500, preferredProvider: cfg.ProviderHeroes).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    _waiting = false;
                    try
                    {
                        if (res == null || !res.Ok) { if (!silent) Say(L.T("AIL_38907a94", "Nadie del grupo contesta (la IA no ha respondido)."), Colors.Red); Log.Warn("Grupo: sin respuesta: " + res?.Error); return; }
                        if (Mission.Current != m) return;
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a < 0 || b <= a) return;
                        var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                        var lines = new List<(Agent who, Agent to, string text, string mood)>();
                        var spoke = new List<Member>();
                        var heard = new HashSet<string>();
                        if (j["lines"] is JArray arr)
                            foreach (var l in arr.OfType<JObject>().Take(cfg.GroupMaxLines))
                            {
                                string said = ((string)l["text"] ?? "").Trim();
                                int idx = 0;
                                string whoRaw = (l["who"]?.ToString() ?? "").Trim();
                                int.TryParse(new string(whoRaw.Where(char.IsDigit).ToArray()), out idx);
                                if (said.Length < 2 || idx < 1) continue;
                                Member member;
                                if (whoRaw.StartsWith("B", StringComparison.OrdinalIgnoreCase))
                                {
                                    // Alguien de alrededor se mete: se acerca y entra en el corro
                                    if (idx > bystanders.Count || !bystanders[idx - 1].IsActive()) continue;
                                    var ba = bystanders[idx - 1];
                                    Join(ba);
                                    member = Circle.FirstOrDefault(x => x.Agent == ba);
                                    if (member == null) continue;
                                    SceneActions.Summon(ba);
                                    Say(L.F("AIL_2215a755", $"{member.Who.Name} se mete en la conversación."), Colors.Gray);
                                }
                                else
                                {
                                    if (idx > circle.Count) continue;
                                    member = circle[idx - 1];
                                }
                                if (member.Agent == null || !member.Agent.IsActive()) continue;
                                if (said.Length > cfg.GroupMaxChars + 80) said = said.Substring(0, cfg.GroupMaxChars + 80);
                                lines.Add((member.Agent, Agent.Main, said, (string)l["mood"]));
                                Transcript.Add($"{member.Who.Name}: {said}");
                                Say($"{member.Who.Name}: {said}", Colors.Yellow);
                                if (!spoke.Contains(member)) spoke.Add(member);
                                // ¿Sale a buscar a alguien?
                                string goes = ((string)l["goes_to"] ?? "").Trim();
                                if (goes.Length > 1 && !Ambient.ErrandBusy && member.Agent.IsAIControlled)
                                {
                                    var target = away.FirstOrDefault(x => x.IsActive() && string.Equals(x.Name, goes, StringComparison.OrdinalIgnoreCase))
                                                 ?? away.FirstOrDefault(x => x.IsActive() && (x.Name.IndexOf(goes, StringComparison.OrdinalIgnoreCase) >= 0 || goes.IndexOf(x.Name, StringComparison.OrdinalIgnoreCase) >= 0));
                                    if (target != null && target != member.Agent)
                                    {
                                        bool back = l["returns"] != null && l["returns"].Type == JTokenType.Boolean && (bool)l["returns"];
                                        Ambient.SendTo(member.Agent, target, text, back);
                                        Circle.RemoveAll(x => x.Agent == member.Agent);
                                        Say(L.F("AIL_e9023c1f", $"{member.Who.Name} va a buscar a {target.Name}."), Colors.Gray);
                                    }
                                }
                                else if (SceneActions.Apply(member.Agent, (l["action"] ?? "").ToString(), away, said))
                                    Circle.RemoveAll(x => x.Agent == member.Agent);
                                // Quien habla lo recuerda como parte de vuestra historia
                                // (lo que dijiste se le apunta una sola vez por ronda, aunque él hable dos veces)
                                if (!silent && heard.Add(member.Who.Key)) member.Who.Memory.Turns.Add(new ChatTurn { Role = "player", Text = "[Said to the group you were in] " + text, Day = CampaignTime.Now.ToDays });
                                member.Who.Memory.Turns.Add(new ChatTurn { Role = "npc", Text = said, Day = CampaignTime.Now.ToDays });
                            }
                        if (lines.Count == 0) { if (!silent) Say(L.T("AIL_156ada7f", "El grupo se queda callado."), Colors.Gray); return; }
                        Ambient.Enqueue(m, lines);
                        string summary = ((string)j["summary"] ?? "").Trim();
                        // Lo que ha pasado ENTRE ellos en esta ronda queda entre ellos
                        try
                        {
                            if (j["between"] is JArray bt)
                                foreach (var e in bt.OfType<JObject>().Take(4))
                                {
                                    int.TryParse(new string((e["from"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int fi);
                                    int.TryParse(new string((e["to"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int ti);
                                    int.TryParse((e["change"] ?? "0").ToString().Trim(), out int ch);
                                    if (fi < 1 || ti < 1 || fi > circle.Count || ti > circle.Count || fi == ti) continue;
                                    ch = Math.Max(-2, Math.Min(2, ch));
                                    string why = ((string)e["why"] ?? "").Trim();
                                    Bonds.TouchOther(circle[fi - 1].Who, circle[ti - 1].Who, ch * 4, why.Length > 2 ? $"in front of {playerName}: {why}" : null);
                                }
                        }
                        catch { }
                        // Lo que cada uno pasa a pensar de TI por lo que acabas de decir o hacer delante de todos
                        try
                        {
                            if (!silent && j["toward_player"] is JArray tp)
                                foreach (var e in tp.OfType<JObject>().Take(4))
                                {
                                    int.TryParse(new string((e["who"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out int wi);
                                    if (wi < 1 || wi > circle.Count) continue;
                                    var who = circle[wi - 1].Who;
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
                        catch (Exception ex) { Log.Error("Grupo: opinión sobre el jugador", ex); }
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
                                Heroes = circle.Where(x => x.Who.IsHero).Select(x => x.Who.Hero.StringId).Take(4).ToList(),
                            });
                            WorldSim.OnIncident($"A rumour has started to spread among those who were present ({string.Join(", ", circle.Select(x => x.Who.Name).Take(4))}): {rumor}", stn, false);
                        }
                        foreach (var x in circle)
                        {
                            if (summary.Length > 5) { x.Who.Memory.RecentTalk = $"Moments ago, in a group with them: {summary}"; x.Who.Memory.RecentTalkAt = DateTime.UtcNow; }
                            if (x.Who.IsHero && spoke.Contains(x)) { x.Who.Memory.ConversationCount = Math.Max(1, x.Who.Memory.ConversationCount); x.Who.Memory.LastConversationDay = CampaignTime.Now.ToDays; }
                            // Quien solo habla en grupo también resume: si no, su historial crecería sin fin hasta un cara a cara
                            try { if (x.Who.IsHero && x.Who.Memory.Turns.Count > cfg.SummarizeAfterTurns + cfg.HistoryTurnsInPrompt) MemorySummarizer.MaybeSummarize(x.Who, cfg.HistoryTurnsInPrompt); } catch { }
                        }
                        while (Transcript.Count > 40) Transcript.RemoveAt(0);
                        Log.Info($"[grupo] {string.Join(", ", circle.Select(x => x.Who.Name))}: {summary}");
                        SaveFiles.Flush();
                    }
                    catch (Exception ex) { Log.Error("Respuesta del grupo", ex); }
                });
            });
        }

        private static void End(bool announce)
        {
            if (Circle.Count == 0) return;
            foreach (var x in Circle) { try { if (x.Agent != null && x.Agent.IsActive()) x.Agent.SetLookAgent(null); } catch { } }
            // Lo hablado se conserva mientras sigas en la misma escena: si vuelves a juntarlos, se acuerdan
            Circle.Clear();
            _idle = 0f; _waiting = false;
            if (announce) Say(L.T("AIL_a25b9c8e", "El grupo se deshace."), Colors.Gray);
        }
    }
}
