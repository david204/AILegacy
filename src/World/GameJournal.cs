using System;
using System.Linq;
using AILegacy.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace AILegacy.World
{
    /// <summary>Registers the AI Legacy quest type with the save system. The base id must be unique across all mods.</summary>
    public class AILegacyQuestTypeDefiner : SaveableTypeDefiner
    {
        public AILegacyQuestTypeDefiner() : base(7512040) { }

        protected override void DefineClassTypes()
        {
            AddClassDefinition(typeof(AILegacyQuest), 1);
        }
    }

    /// <summary>
    /// An AI quest as shown in the game's quest journal. It only mirrors giver, deadline, reward, notes and progress.
    /// The logic lives in QuestSystem.
    /// </summary>
    public class AILegacyQuest : QuestBase
    {
        [SaveableField(1)] private string _title;
        [SaveableField(2)] public string AiQuestId;
        [SaveableField(3)] private JournalLog _progressLog;

        public AILegacyQuest(string questId, Hero giver, CampaignTime due, int reward, string title, string aiQuestId)
            : base(questId, giver, due, reward)
        {
            _title = title;
            AiQuestId = aiQuestId;
        }

        private static TextObject T(string s) => new TextObject("{=!}" + (s ?? "").Replace("{", "(").Replace("}", ")"));

        public override TextObject Title => T(_title);
        public override bool IsRemainingTimeHidden => false;

        protected override void SetDialogs() { }
        protected override void InitializeQuestOnGameLoad() { SetDialogs(); }

        protected override void OnTimedOut()
        {
            try { QuestSystem.OnJournalTimedOut(AiQuestId); } catch (Exception ex) { Log.Error("Quest expired", ex); }
        }

        public void Note(string text) { if (!string.IsNullOrWhiteSpace(text)) AddLog(T(text)); }

        public void SetProgress(string label, int current, int target)
        {
            if (target <= 0) return;
            if (_progressLog == null) _progressLog = AddDiscreteLog(T("Progreso"), T(string.IsNullOrWhiteSpace(label) ? "Progreso" : label), current, target);
            else UpdateQuestTaskStage(_progressLog, current);
        }
    }

    internal static class GameJournal
    {
        private static bool Ready => Campaign.Current?.QuestManager != null;

        public static AILegacyQuest Find(string aiQuestId)
        {
            if (!Ready || string.IsNullOrEmpty(aiQuestId)) return null;
            return Campaign.Current.QuestManager.Quests.OfType<AILegacyQuest>().FirstOrDefault(q => q.AiQuestId == aiQuestId && q.IsOngoing);
        }

        public static void Create(AiQuest q)
        {
            try
            {
                QuestSystem.EnsureHooks();
                if (!Ready || Find(q.Id) != null) return;
                var giver = Hero.Find(q.GiverId);
                if (giver == null || !giver.IsAlive) return;
                double daysLeft = Math.Max(0.5, q.DueDay - CampaignTime.Now.ToDays);
                var gq = new AILegacyQuest("ailegacy_" + q.Id + "_" + (int)(q.CreatedDay * 100), giver, CampaignTime.DaysFromNow((float)daysLeft), q.RewardGold, q.Name, q.Id);
                gq.StartQuest();
                string goal = QuestObjectives.GoalEs(q);
                gq.Note(q.Description + (goal.Length > 0 ? "\n\n" + goal : "") + (q.RewardGold > 0 ? L.F("AIL_41b3740a", $"\n\nRecompensa prometida: {q.RewardGold} denarios.") : ""));
                foreach (var obj in QuestObjectives.Trackables(q)) { try { gq.AddTrackedObject(obj); } catch { } }
                if (q.ProgressTarget > 0) gq.SetProgress(q.ProgressLabel, q.Progress, q.ProgressTarget);
                Log.Info($"Quest added to journal: {q.Name} [{q.Id}]");
            }
            catch (Exception ex) { Log.Error("Could not create the quest in the journal", ex); }
        }

        public static void Update(AiQuest q, string note)
        {
            try
            {
                var gq = Find(q.Id);
                if (gq == null) return;
                gq.Note(note);
                if (q.ProgressTarget > 0) gq.SetProgress(q.ProgressLabel, q.Progress, q.ProgressTarget);
            }
            catch (Exception ex) { Log.Error("Update journal quest", ex); }
        }

        public static void Untrack(AiQuest q, string heroId) => Untrack(q, Hero.Find(heroId));

        /// <summary>Stops tracking something on the map (a dead hero, a destroyed band) without closing the quest.</summary>
        public static void Untrack(AiQuest q, ITrackableCampaignObject obj)
        {
            try
            {
                var gq = Find(q.Id);
                if (gq == null || obj == null || ReferenceEquals(obj, gq.QuestGiver)) return;
                if (gq.IsTracked(obj)) gq.RemoveTrackedObject(obj);
            }
            catch (Exception ex) { Log.Error("Remove quest tracking", ex); }
        }

        public static void Close(AiQuest q, string state, string text)
        {
            try
            {
                var gq = Find(q.Id);
                if (gq == null) return;
                var log = new TextObject("{=!}" + (text ?? "").Replace("{", "(").Replace("}", ")"));
                if (state == "completed") { gq.Note(text); gq.CompleteQuestWithSuccess(); }
                else if (state == "expired") gq.CompleteQuestWithTimeOut(log);
                else if (state == "cancelled") gq.CompleteQuestWithCancel(log);
                else gq.CompleteQuestWithFail(log);
            }
            catch (Exception ex) { Log.Error("Close journal quest", ex); }
        }

        /// <summary>On load: every open AI quest gets its journal entry, and orphaned journal quests are closed.</summary>
        public static void Reconcile()
        {
            try
            {
                QuestSystem.EnsureHooks();
                if (!Ready) return;
                // Close quests whose giver or target died first, so nothing gets created in the journal for a dead hero
                QuestSystem.CloseDeadOnLoad();
                foreach (var q in QuestSystem.Quests.Where(x => x.Active).ToList())
                    if (Find(q.Id) == null) Create(q);
                foreach (var gq in Campaign.Current.QuestManager.Quests.OfType<AILegacyQuest>().Where(x => x.IsOngoing).ToList())
                    if (!QuestSystem.Quests.Any(x => x.Active && x.Id == gq.AiQuestId))
                        gq.CompleteQuestWithCancel(new TextObject(L.T("AIL_175eb081", "{=!}Este encargo ya no está en pie.")));
            }
            catch (Exception ex) { Log.Error("Syncing quest journal", ex); }
        }
    }
}
