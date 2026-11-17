using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>A task an NPC gave the player during a conversation.</summary>
    public class AiQuest
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("giver")] public string GiverId;
        [JsonProperty("giver_name")] public string GiverName;
        [JsonProperty("name")] public string Name;                 // player's language
        [JsonProperty("description")] public string Description;   // player's language
        [JsonProperty("brief")] public string Brief;               // English, for prompts
        [JsonProperty("created")] public double CreatedDay;
        [JsonProperty("due")] public double DueDay;
        [JsonProperty("reward")] public int RewardGold;
        [JsonProperty("progress_target")] public int ProgressTarget;
        [JsonProperty("progress")] public int Progress;
        [JsonProperty("progress_label")] public string ProgressLabel = "";
        [JsonProperty("log")] public List<string> Log = new List<string>();
        /// <summary>active | completed | failed | expired</summary>
        [JsonProperty("state")] public string State = "active";
        [JsonProperty("ended")] public double EndDay = -1;
        [JsonProperty("reason")] public string Reason = "";
        /// <summary>Other heroes involved (target, witness...). They know about the task too, and each is marked on the map until they report.</summary>
        [JsonProperty("targets")] public List<string> Targets = new List<string>();
        /// <summary>Targets who already gave their part (a quest "update"): their map marker is gone.</summary>
        [JsonProperty("reported")] public List<string> Reported = new List<string>();
        /// <summary>A third hero who may also close the task (complete or fail), besides the giver.</summary>
        [JsonProperty("completer", NullValueHandling = NullValueHandling.Ignore)] public string CompleterId;
        [JsonProperty("completer_name", NullValueHandling = NullValueHandling.Ignore)] public string CompleterName;

        // Objective checked by the game (see QuestObjectives)
        [JsonProperty("obj_type")] public string ObjType = "none";
        [JsonProperty("obj_target")] public string ObjTargetId;
        [JsonProperty("obj_target_name")] public string ObjTargetName;
        [JsonProperty("obj_amount")] public int ObjAmount;
        [JsonProperty("obj_met")] public bool ObjMet;
        [JsonProperty("party_id")] public string PartyId;        // band spawned for this task
        [JsonProperty("band_by_player")] public bool BandByPlayer;
        /// <summary>The band was reported beaten by someone else and came back once, so the player can still do the task.</summary>
        [JsonProperty("band_regrouped")] public bool BandRegrouped;
        [JsonProperty("doc_name")] public string DocName;        // document to deliver
        [JsonProperty("obj_item")] public string ObjItemId;      // deliver_item: which item
        [JsonProperty("obj_item_name")] public string ObjItemName;
        [JsonProperty("delivered")] public int Delivered;        // units already handed over

        [JsonIgnore] public bool Active => State == "active";
    }

    /// <summary>The "quest" field of the AI's reply.</summary>
    public class QuestRequest
    {
        public string Action = "";       // create | update | complete | fail
        public string QuestId = "";
        public string Name = "", Description = "", Brief = "", Note = "", Reason = "", ProgressLabel = "";
        public int Days, Reward, ProgressTarget, SetProgress = -1;
        public List<string> Targets = new List<string>();
        /// <summary>Name or id of the third hero who may close the task.</summary>
        public string Completer = "";
        public string ObjType = "", ObjTarget = "", ObjItem = "";
        public int ObjAmount;

        public static QuestRequest Parse(JToken t)
        {
            if (!(t is JObject j)) return null;
            string S(string k) { var v = j[k]; return v == null || v.Type == JTokenType.Null ? "" : v.ToString().Trim(); }
            int I(string k, int def = 0) { return int.TryParse(new string(S(k).Where(c => char.IsDigit(c) || c == '-').ToArray()), out var v) ? v : def; }
            var r = new QuestRequest
            {
                Action = S("action").ToLowerInvariant(), QuestId = S("quest_id"),
                Name = S("name"), Description = S("description"), Brief = S("brief"), Note = S("note"), Reason = S("reason"),
                ProgressLabel = S("progress_label"), Days = I("days"), Reward = I("reward_gold"), ProgressTarget = I("progress_target"),
                SetProgress = j["set_progress"] == null ? -1 : I("set_progress", -1),
            };
            if (j["targets"] is JArray arr) r.Targets = arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
            else if (j["targets"] is JValue one && one.Type == JTokenType.String && !string.IsNullOrWhiteSpace((string)one)) r.Targets = ((string)one).Split(',').Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            r.Completer = S("completer");
            if (r.Completer.Length == 0) r.Completer = S("completer_npc_id");
            if (j["objective"] is JObject o)
            {
                r.ObjType = (o["type"]?.ToString() ?? "").Trim();
                r.ObjTarget = (o["target"]?.ToString() ?? "").Trim();
                r.ObjItem = (o["item"]?.ToString() ?? "").Trim();
                int.TryParse(new string((o["amount"]?.ToString() ?? "").Where(char.IsDigit).ToArray()), out r.ObjAmount);
            }
            return r.Action.Length == 0 || r.Action == "none" ? null : r;
        }
    }

    internal static class QuestSystem
    {
        public static List<AiQuest> Quests = new List<AiQuest>();
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        public static AiQuest ActiveFrom(string giverId) => Quests.FirstOrDefault(q => q.Active && q.GiverId == giverId);
        public static IEnumerable<AiQuest> ActiveKnownTo(string heroId) => Quests.Where(q => q.Active && (q.GiverId == heroId || q.Targets.Contains(heroId) || q.CompleterId == heroId));
        private static bool IsCompleter(AiQuest q, string heroId) => !string.IsNullOrEmpty(q.CompleterId) && q.CompleterId == heroId;
        private static string NameOf(string heroId) => Hero.Find(heroId)?.Name?.ToString() ?? heroId;
        public static IEnumerable<AiQuest> PastWith(string giverId) => Quests.Where(q => !q.Active && q.GiverId == giverId).OrderByDescending(q => q.EndDay);

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));
        private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n);

        // ============================================================ prompt text

        public static string DescribeFor(Speaker s)
        {
            if (!S.Get.EnableQuests || !s.IsHero) return "";
            double now = CampaignTime.Now.ToDays;
            var sb = new StringBuilder();
            foreach (var q in ActiveKnownTo(s.Hero.StringId))
            {
                string me = s.Hero.StringId;
                bool mine = q.GiverId == me, closer = IsCompleter(q, me);
                int left = (int)Math.Ceiling(q.DueDay - now);
                sb.Append(mine ? "- A task YOU gave them" : $"- A task {q.GiverName} gave them, which concerns you")
                  .Append($" [quest_id {q.Id}]: {(string.IsNullOrWhiteSpace(q.Brief) ? q.Description : q.Brief)}")
                  .Append($" Given {(int)(now - q.CreatedDay)} day(s) ago; {(left > 0 ? left + " day(s) left" : "the deadline is today")}.");
                var others = q.Targets.Where(t => t != me).ToList();
                if (others.Count > 0)
                    sb.Append(" People it involves: " + string.Join(", ", others.Select(t => NameOf(t) + (q.Reported.Contains(t) ? " (has already given their part)" : ""))) + ".");
                if (mine) sb.Append(string.IsNullOrEmpty(q.CompleterId) ? " Completer: YOU." : $" Completer: {q.CompleterName ?? NameOf(q.CompleterId)}, who may also judge it and close it in your stead; you still may too.");
                else if (closer)
                    sb.Append($" [Your role: {q.GiverName} named you to judge this task. You may record progress (\"update\"), declare it \"complete\" when you are truly convinced it is done (the reward {q.GiverName} promised is paid by them), or \"fail\" it.]");
                else
                {
                    sb.Append(q.Reported.Contains(me) ? " [Your role: one of the people it involves. You have already given your part.]"
                        : $" [Your role: one of the people it involves. React to their coming as your character would. When you have done your part, or told them what they came to learn, record it with \"quest\" action \"update\" and a short note: that takes you off their map so {q.GiverName} can send them on.]");
                    sb.Append($" You cannot declare it complete or failed: only {q.GiverName}" + (string.IsNullOrEmpty(q.CompleterId) ? "" : $" or {q.CompleterName ?? NameOf(q.CompleterId)}") + " can.");
                }
                if (q.RewardGold > 0) sb.Append($" Promised reward: {q.RewardGold} denars.");
                if (q.ProgressTarget > 0 && q.ObjType != "bring_item" && q.ObjType != "deliver_item") sb.Append($" Progress: {q.Progress}/{q.ProgressTarget} {q.ProgressLabel}.");
                sb.Append(QuestObjectives.StatusLine(q));
                if (q.ObjType == "defeat_bandits" && q.ObjMet && !q.BandByPlayer) sb.Append(" (You heard the band was destroyed by someone else, not by them.)");
                if (q.Log.Count > 0) sb.Append(" Notes so far: " + string.Join(" | ", q.Log.Skip(Math.Max(0, q.Log.Count - 3))));
                sb.AppendLine();
            }
            foreach (var q in PastWith(s.Hero.StringId).Take(3))
                sb.AppendLine($"- Past task ({q.State}, {(int)(now - q.EndDay)} day(s) ago): {(string.IsNullOrWhiteSpace(q.Brief) ? q.Name : q.Brief)}" +
                              (string.IsNullOrWhiteSpace(q.Reason) ? "" : $" — {q.Reason}"));
            return sb.ToString().TrimEnd();
        }

        // ============================================================ apply the AI's request

        public static List<string> Apply(Speaker s, QuestRequest r)
        {
            var shown = new List<string>();
            var cfg = S.Get;
            if (r == null || !cfg.EnableQuests || !s.IsHero) return shown;
            double now = CampaignTime.Now.ToDays;
            var h = s.Hero;

            switch (r.Action)
            {
                case "create":
                {
                    if (ActiveFrom(h.StringId) != null) { Log.Warn($"{s.Name} already has an active quest: not creating another"); break; }
                    if (Quests.Count(q => q.Active) >= cfg.MaxActiveQuests) { Log.Warn("Too many active quests"); break; }
                    if (r.Name.Length < 3 || (r.Description.Length < 10 && r.Brief.Length < 10)) { Log.Warn("Quest without name or description: ignored"); break; }
                    int days = Math.Max(3, Math.Min(60, r.Days <= 0 ? 15 : r.Days));
                    int reward = Math.Max(0, Math.Min(r.Reward, Math.Min(cfg.MaxGoldPerAction, Math.Max(0, h.Gold))));
                    EnsureHooks();
                    var q = new AiQuest
                    {
                        Id = NewId(),
                        GiverId = h.StringId, GiverName = s.Name,
                        Name = Clip(r.Name, 60), Description = Clip(r.Description.Length > 0 ? r.Description : r.Brief, 500), Brief = Clip(r.Brief, 400),
                        CreatedDay = now, DueDay = now + days, RewardGold = reward,
                        ProgressTarget = Math.Max(0, Math.Min(999, r.ProgressTarget)), ProgressLabel = Clip(r.ProgressLabel, 30),
                    };
                    foreach (var t in r.Targets.Take(4))
                    {
                        var th = Hero.Find(t) ?? Hero.AllAliveHeroes.FirstOrDefault(x => string.Equals(x.Name.ToString(), t, StringComparison.OrdinalIgnoreCase));
                        if (th != null && th != h && th != P) q.Targets.Add(th.StringId);
                    }
                    if (r.Completer.Length > 0)
                    {
                        var ch = Hero.Find(r.Completer) ?? Hero.AllAliveHeroes.FirstOrDefault(x => string.Equals(x.Name.ToString(), r.Completer, StringComparison.OrdinalIgnoreCase));
                        if (ch != null && ch.IsAlive && ch != h && ch != P) { q.CompleterId = ch.StringId; q.CompleterName = ch.Name.ToString(); }
                        else Log.Warn($"Quest completer '{r.Completer}' not found or not valid: only {s.Name} can close it");
                    }
                    if (r.Note.Length > 0) q.Log.Add(Clip(r.Note, 200));
                    QuestObjectives.Resolve(q, r, s);
                    Quests.Add(q);
                    Log.Info($"Quest created by {s.Name}: {q.Name} ({days} days, {reward} denars) [{q.Id}]" + (q.Targets.Count > 0 ? ", targets: " + string.Join(", ", q.Targets.Select(NameOf)) : "") + (q.CompleterId != null ? ", completer: " + q.CompleterName : ""));
                    GameJournal.Create(q);
                    shown.Add(L.F("AIL_2bd6ef00", $"Nuevo encargo: «{q.Name}» ({days} días") + (reward > 0 ? L.F("AIL_de098566", $", {reward} denarios") : "") + ")");
                    break;
                }

                case "update":
                {
                    var q = Find(h, r.QuestId, allowTarget: true);
                    if (q == null) break;
                    Update(q, s, r.Note, r.SetProgress, shown);
                    break;
                }

                case "complete":
                {
                    var q = Find(h, r.QuestId, allowTarget: false, quiet: true);
                    if (q == null)
                    {
                        // Someone the task involves, but who may not close it: what they said counts as their report
                        var involved = Find(h, r.QuestId, allowTarget: true);
                        if (involved != null) { Log.Info($"{s.Name} may not close quest {involved.Id}: taken as an update"); Update(involved, s, r.Reason.Length > 0 ? r.Reason : r.Note, r.SetProgress, shown); }
                        break;
                    }
                    if (!QuestObjectives.Met(q))
                    {
                        // The NPC says it's done but the game says no, so no completion and no reward
                        Log.Warn($"{s.Name} tried to mark {q.Id} complete, but is missing: {QuestObjectives.MissingEs(q)}");
                        shown.Add(L.F("AIL_390cb390", $"«{q.Name}» aún no está cumplido: {QuestObjectives.MissingEs(q)}"));
                        break;
                    }
                    var handed = QuestObjectives.OnCompleted(q, s);
                    if (handed != null) shown.Add(handed);
                    End(q, "completed", r.Reason);
                    // The giver pays and is grateful, also when the third person named for it is the one who closes it
                    var giver = q.GiverId == h.StringId ? h : Hero.Find(q.GiverId);
                    int paid = giver != null && giver.IsAlive ? Math.Min(q.RewardGold, Math.Max(0, giver.Gold)) : 0;
                    if (paid > 0) GiveGoldAction.ApplyBetweenCharacters(giver, P, paid);
                    // As in AIInfluence the reward is the denars and some renown for the player's clan (2 to 10); relation
                    // and trust are left to what the characters themselves say. An extra relation can still be set in MCM
                    if (cfg.QuestRelationReward > 0 && giver != null && giver.IsAlive) ChangeRelationAction.ApplyPlayerRelation(giver, cfg.QuestRelationReward, affectRelatives: false, showQuickNotification: true);
                    int renown = Rng.Next(2, 11);
                    try
                    {
                        if (Clan.PlayerClan != null)
                        {
                            GainRenownAction.Apply(P, renown, true);
                            TaleWorlds.Core.MBInformationManager.AddQuickInformation(new TaleWorlds.Localization.TextObject("{=!}" + L.F("AIL_e7b1d832", $"El renombre de tu clan aumenta en {renown}")), 0, null, null, "");
                        }
                    }
                    catch (Exception ex) { Log.Error("Quest renown", ex); }
                    GameJournal.Close(q, "completed", string.IsNullOrWhiteSpace(r.Reason) ? L.F("AIL_01ad2df2", $"{s.Name} da el encargo por cumplido.") : r.Reason);
                    shown.Add(L.F("AIL_89bc1b1d", $"Encargo cumplido: «{q.Name}»") + (paid > 0 ? L.F("AIL_795b98cd", $", recibes {paid} denarios") : "") + L.F("AIL_e7b1d833", $", +{renown} de renombre"));
                    News(q, s, true);
                    break;
                }

                case "fail":
                {
                    var q = Find(h, r.QuestId, allowTarget: false, quiet: true);
                    if (q == null)
                    {
                        var involved = Find(h, r.QuestId, allowTarget: true);
                        if (involved != null) { Log.Info($"{s.Name} may not fail quest {involved.Id}: taken as an update"); Update(involved, s, r.Reason.Length > 0 ? r.Reason : r.Note, -1, shown); }
                        break;
                    }
                    Fail(q, s, "failed", r.Reason);
                    shown.Add(L.F("AIL_a0af06ce", $"Encargo fracasado: «{q.Name}»"));
                    break;
                }
            }
            return shown;
        }

        /// <summary>Id not used by any task (active or in history) or by any journal quest.</summary>
        private static string NewId()
        {
            for (int i = 0; i < 1000; i++)
            {
                string id = "q" + Rng.Next(100000, 1000000);
                if (Quests.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                try { if (GameJournal.Find(id) != null) continue; } catch { }
                return id;
            }
            // Practically never reached, fall back to time plus a counter
            return "q" + DateTime.UtcNow.Ticks.ToString("x") + Quests.Count;
        }

        /// <summary>
        /// The task this hero means. The giver and the completer may close it; with <paramref name="allowTarget"/> the
        /// people it involves count too (they may only record their part).
        /// </summary>
        private static AiQuest Find(Hero h, string id, bool allowTarget, bool quiet = false)
        {
            string me = h.StringId;
            var mine = Quests.Where(q => q.Active && (q.GiverId == me || IsCompleter(q, me) || (allowTarget && q.Targets.Contains(me)))).ToList();
            var q2 = mine.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.OrdinalIgnoreCase));
            // A wrong or missing id falls back to their only task, but never when the id names another real task
            // (giver of one and involved in another: closing "the other one" must not close theirs)
            if (q2 == null && mine.Count == 1 && !Quests.Any(q => q.Active && string.Equals(q.Id, id, StringComparison.OrdinalIgnoreCase))) q2 = mine[0];
            if (q2 == null && !quiet) Log.Warn($"Quest not found for {h.Name}: '{id}'");
            return q2;
        }

        /// <summary>
        /// A note on the task. When it comes from one of the people it involves, that is their report: their marker
        /// leaves the map so the giver can send the player on to the next step.
        /// </summary>
        private static void Update(AiQuest q, Speaker s, string note, int setProgress, List<string> shown)
        {
            string who = s.Hero.StringId;
            bool fromGiver = q.GiverId == who;
            note = note ?? "";
            if (note.Length > 0) q.Log.Add(Clip(fromGiver ? note : s.Name + ": " + note, 200));
            if (setProgress >= 0 && q.ProgressTarget > 0 && !QuestObjectives.HasObjective(q)) q.Progress = Math.Min(q.ProgressTarget, setProgress);
            while (q.Log.Count > 8) q.Log.RemoveAt(0);
            GameJournal.Update(q, note.Length == 0 ? null : fromGiver ? note : L.F("AIL_e7b1cf0a", $"{s.Name} informa: {note}"));
            bool reported = false;
            if (!fromGiver && q.Targets.Contains(who) && !IsCompleter(q, who))
            {
                if (!q.Reported.Contains(who)) { q.Reported.Add(who); reported = true; }
                GameJournal.Untrack(q, who);
            }
            Log.Info($"Quest {q.Id} updated by {s.Name}: {note} {(q.ProgressTarget > 0 ? q.Progress + "/" + q.ProgressTarget : "")}" + (reported ? " (their part is given: map marker removed)" : ""));
            shown.Add(L.F("AIL_0681e274", $"Encargo «{q.Name}»: ") + (q.ProgressTarget > 0 ? $"{q.Progress}/{q.ProgressTarget} {q.ProgressLabel}" : reported ? L.F("AIL_e7b1cf0b", $"{s.Name} ha dado su parte") : "anotado"));
        }

        private static void End(AiQuest q, string state, string reason)
        {
            q.State = state;
            q.EndDay = CampaignTime.Now.ToDays;
            q.Reason = Clip(reason, 200);
            QuestObjectives.OnEnded(q);
            Log.Info($"Quest {q.Id} «{q.Name}»: {state} ({reason})");
        }

        private static void Fail(AiQuest q, Speaker s, string state, string reason)
        {
            End(q, state, reason);
            var h = Hero.Find(q.GiverId);
            if (h != null && h.IsAlive)
            {
                ChangeRelationAction.ApplyPlayerRelation(h, -Math.Max(1, S.Get.QuestRelationReward / 2), affectRelatives: false, showQuickNotification: true);
                Bonds.ChangeTrust(s != null && s.IsHero && s.Hero == h ? s : Speaker.FromHero(h), -8);
            }
            GameJournal.Close(q, state, state == "expired" ? L.T("AIL_f991b6e7", "Se acabó el plazo.") : string.IsNullOrWhiteSpace(reason) ? L.T("AIL_c53c096c", "El encargo ha fracasado.") : reason);
        }

        /// <summary>The game timed out the journal quest before our daily tick did.</summary>
        public static void OnJournalTimedOut(string aiQuestId)
        {
            var q = Quests.FirstOrDefault(x => x.Active && x.Id == aiQuestId);
            if (q == null) return;
            End(q, "expired", "the deadline passed with no word from them");
            var giver = Hero.Find(q.GiverId);
            if (giver != null && giver.IsAlive)
            {
                ChangeRelationAction.ApplyPlayerRelation(giver, -Math.Max(1, S.Get.QuestRelationReward / 2), affectRelatives: false, showQuickNotification: true);
                Bonds.ChangeTrust(Speaker.FromHero(giver), -8);
            }
        }

        private static void News(AiQuest q, Speaker s, bool done)
        {
            try
            {
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 1,
                    Text = $"{P.Name} carried out a task for {q.GiverName}.",
                    TitleEs = L.T("AIL_4c5ed9ad", "Encargo cumplido"), TextEs = L.F("AIL_f0a2c329", $"{P.Name} ha cumplido un encargo de {q.GiverName}: {q.Name}."),
                    SettlementId = s.Settlement?.StringId,
                    Kingdoms = s.Faction != null ? new List<string> { s.Faction.StringId } : new List<string>(),
                    Heroes = new List<string> { q.GiverId, P.StringId },
                });
            }
            catch { }
        }

        // ============================================================ deaths: the task closes right away

        // Listens for hero deaths instead of waiting for the daily tick. If the giver dies, or the person you had to see
        // or deliver to, the task closes at once and its quest leaves the journal and the map.
        private static readonly object HookOwner = new object();
        private static WeakReference _hookedOn;

        /// <summary>Hooks the death event on the current campaign, once per campaign.</summary>
        public static void EnsureHooks()
        {
            try
            {
                if (Campaign.Current == null) return;
                var ev = CampaignEvents.HeroKilledEvent;
                if (ev == null || (_hookedOn != null && ReferenceEquals(_hookedOn.Target, ev))) return;
                ev.AddNonSerializedListener(HookOwner, OnHeroKilled);
                _hookedOn = new WeakReference(ev);
                Log.Info("Quests: listening for hero deaths");
            }
            catch (Exception ex) { Log.Error("Quests: hook deaths", ex); }
        }

        private static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (victim == null) return;
                CloseForDeath(victim.StringId);
            }
            catch (Exception ex) { Log.Error("Quests: hero death", ex); }
        }

        private static void CloseForDeath(string heroId)
        {
            foreach (var q in Quests.Where(x => x.Active).ToList())
            {
                if (q.GiverId == heroId)
                {
                    End(q, "failed", "the giver died");
                    GameJournal.Close(q, "cancelled", L.F("AIL_cb1eb4c2", $"{q.GiverName} ha muerto."));
                    continue;
                }
                bool objective = q.ObjTargetId == heroId && (q.ObjType == "talk_to" || q.ObjType == "deliver_item" || q.ObjType == "deliver_document");
                if (objective && !QuestObjectives.Met(q))
                {
                    // Can't be done anymore and it's not the player's fault, so cancel with no penalty
                    End(q, "failed", $"{q.ObjTargetName}, whom the task concerned, died");
                    GameJournal.Close(q, "cancelled", L.F("AIL_cb1eb4c2", $"{q.ObjTargetName} ha muerto."));
                    continue;
                }
                // The third person who could close it died: only the giver can now
                if (IsCompleter(q, heroId))
                {
                    string name = q.CompleterName ?? NameOf(heroId);
                    GameJournal.Untrack(q, heroId);
                    q.CompleterId = null; q.CompleterName = null;
                    GameJournal.Update(q, L.F("AIL_e7b1cf0c", $"{name}, que podía dar el encargo por cerrado, ha muerto. Ahora solo puede hacerlo {q.GiverName}."));
                    Log.Info($"Quest {q.Id}: the completer {name} died, only the giver can close it now");
                }
                // Still open (objective already met, or they were just involved), so only stop tracking them on the map
                if (objective || q.Targets.Contains(heroId)) GameJournal.Untrack(q, heroId);
            }
        }

        /// <summary>
        /// On load, closes tasks whose people died meanwhile (or in an old save) before touching the journal, and gives
        /// one whose deadline already passed 7 more days, as AIInfluence does, instead of failing it on the next tick.
        /// </summary>
        public static void CloseDeadOnLoad()
        {
            try
            {
                double now = CampaignTime.Now.ToDays;
                foreach (var q in Quests.Where(x => x.Active).ToList())
                {
                    if (!q.Active) continue;
                    if (now >= q.DueDay)
                    {
                        q.DueDay = now + 7;
                        try { GameJournal.Find(q.Id)?.ChangeQuestDueTime(CampaignTime.Days((float)q.DueDay)); }
                        catch (Exception ex) { Log.Warn("Quest deadline in the journal: " + ex.Message); }
                        Log.Info($"[quest] '{q.Name}' was already past its deadline on load: extended by 7 days");
                    }
                    var giver = Hero.Find(q.GiverId);
                    if (giver == null || giver.IsDead) { CloseForDeath(q.GiverId); continue; }
                    if (!string.IsNullOrEmpty(q.ObjTargetId) && (q.ObjType == "talk_to" || q.ObjType == "deliver_item" || q.ObjType == "deliver_document"))
                    {
                        var t = Hero.Find(q.ObjTargetId);
                        if (t == null || t.IsDead) CloseForDeath(q.ObjTargetId);
                    }
                    if (q.Active && !string.IsNullOrEmpty(q.CompleterId))
                    {
                        var c = Hero.Find(q.CompleterId);
                        if (c == null || c.IsDead) CloseForDeath(q.CompleterId);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Quests: dead on load", ex); }
        }

        // ============================================================ time

        public static void OnDailyTick()
        {
            try
            {
                EnsureHooks();
                double now = CampaignTime.Now.ToDays;
                QuestObjectives.OnDailyTick();
                foreach (var q in Quests.Where(x => x.Active).ToList())
                {
                    var giver = Hero.Find(q.GiverId);
                    // Fallback in case the death event never fired
                    if (giver == null || giver.IsDead) { End(q, "failed", "the giver died"); GameJournal.Close(q, "cancelled", L.F("AIL_cb1eb4c2", $"{q.GiverName} ha muerto.")); continue; }
                    if (now >= q.DueDay)
                    {
                        Fail(q, null, "expired", "the deadline passed with no word from them");
                        var mem = MemoryStore.Get(giver.StringId, giver.Name.ToString());
                        ChatSession.AddNote(mem, $"They let the deadline pass on the task I gave them ({(string.IsNullOrWhiteSpace(q.Brief) ? q.Name : q.Brief)}).", S.Get.MaxNotesPerHero, q.GiverName);
                    }
                    else if ((int)(q.DueDay - now) == 2) Say(L.F("AIL_b14d8a23", $"Quedan 2 días para el encargo de {q.GiverName}: {q.Name}"), Colors.Yellow);
                }
                // keep history short
                var old = Quests.Where(x => !x.Active).OrderByDescending(x => x.EndDay).Skip(60).ToList();
                foreach (var x in old) Quests.Remove(x);
            }
            catch (Exception ex) { Log.Error("Quests: daily tick", ex); }
            // Rides on this daily tick: memory book pages for histories lived without the player (MCM "whose memories")
            MemorySummarizer.OnDailyTick();
        }
    }

    /// <summary>Roleplay item the player carries (letter, contract, seal, token...). Not a game item, it only exists for the story.</summary>
    public class RpItem
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("desc")] public string Description;
        [JsonProperty("from")] public string FromId;
        [JsonProperty("from_name")] public string FromName;
        [JsonProperty("day")] public double Day;
        /// <summary>Which of the mod's document items stands for it in the inventory (1..40); 0 = none yet.</summary>
        [JsonProperty("slot", DefaultValueHandling = DefaultValueHandling.Ignore)] public int Slot;
        /// <summary>It has been put in the player's inventory: from then on, gone from there means gone.</summary>
        [JsonProperty("in_inventory", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Delivered;
    }

    internal static class Belongings
    {
        public static List<RpItem> Items = new List<RpItem>();

        public static RpItem Find(string name)
        {
            string n = Norm(name);
            if (n.Length < 2) return null;
            return Items.FirstOrDefault(i => Norm(i.Name) == n) ?? Items.FirstOrDefault(i => Norm(i.Name).Contains(n) || n.Contains(Norm(i.Name)));
        }

        public static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        /// <summary>For the prompt: what the player carries and could show.</summary>
        public static string Describe()
        {
            if (Items.Count == 0) return "";
            return string.Join("\n", Items.Skip(Math.Max(0, Items.Count - 10)).Select(i => $"- \"{i.Name}\" (from {i.FromName}): {i.Description}"));
        }
    }
}
