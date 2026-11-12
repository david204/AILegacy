using System;
using System.Linq;
using AILegacy.Core;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Localization;
using TaleWorlds.SaveSystem;

namespace AILegacy.World
{
    /// <summary>Registra en el guardado del juego la misión propia de AI Legacy (el número base debe ser único entre mods).</summary>
    public class AILegacyQuestTypeDefiner : SaveableTypeDefiner
    {
        public AILegacyQuestTypeDefiner() : base(7512040) { }

        protected override void DefineClassTypes()
        {
            AddClassDefinition(typeof(AILegacyQuest), 1);
        }
    }

    /// <summary>
    /// Un encargo de la IA visto en el diario de misiones del juego.
    /// Es solo el espejo: quién lo dio, plazo, recompensa, notas y progreso. La lógica vive en QuestSystem.
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
            try { QuestSystem.OnJournalTimedOut(AiQuestId); } catch (Exception ex) { Log.Error("Misión caducada", ex); }
        }

        public void Note(string text) { if (!string.IsNullOrWhiteSpace(text)) AddLog(T(text)); }

        public void SetProgress(string label, int current, int target)
        {
            if (target <= 0) return;
            if (_progressLog == null) _progressLog = AddDiscreteLog(T("Progreso"), T(string.IsNullOrWhiteSpace(label) ? "Progreso" : label), current, target);
            else UpdateQuestTaskStage(_progressLog, current);
        }
    }

    /// <summary>Puente entre los encargos de AI Legacy y el diario del juego.</summary>
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
                if (!Ready || Find(q.Id) != null) return;
                var giver = Hero.Find(q.GiverId);
                if (giver == null || !giver.IsAlive) return;
                double daysLeft = Math.Max(0.5, q.DueDay - CampaignTime.Now.ToDays);
                var gq = new AILegacyQuest("ailegacy_" + q.Id + "_" + (int)(q.CreatedDay * 100), giver, CampaignTime.DaysFromNow((float)daysLeft), q.RewardGold, q.Name, q.Id);
                gq.StartQuest();
                string goal = QuestObjectives.GoalEs(q);
                gq.Note(q.Description + (goal.Length > 0 ? "\n\n" + goal : "") + (q.RewardGold > 0 ? L.F($"\n\nRecompensa prometida: {q.RewardGold} denarios.") : ""));
                foreach (var obj in QuestObjectives.Trackables(q)) { try { gq.AddTrackedObject(obj); } catch { } }
                if (q.ProgressTarget > 0) gq.SetProgress(q.ProgressLabel, q.Progress, q.ProgressTarget);
                Log.Info($"Misión añadida al diario: {q.Name} [{q.Id}]");
            }
            catch (Exception ex) { Log.Error("No se pudo crear la misión en el diario", ex); }
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
            catch (Exception ex) { Log.Error("Actualizar misión del diario", ex); }
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
            catch (Exception ex) { Log.Error("Cerrar misión del diario", ex); }
        }

        /// <summary>Al cargar: cada encargo abierto tiene su misión en el diario, y no queda ninguna misión huérfana.</summary>
        public static void Reconcile()
        {
            try
            {
                if (!Ready) return;
                foreach (var q in QuestSystem.Quests.Where(x => x.Active).ToList())
                    if (Find(q.Id) == null) Create(q);
                foreach (var gq in Campaign.Current.QuestManager.Quests.OfType<AILegacyQuest>().Where(x => x.IsOngoing).ToList())
                    if (!QuestSystem.Quests.Any(x => x.Active && x.Id == gq.AiQuestId))
                        gq.CompleteQuestWithCancel(new TextObject(L.T("{=!}Este encargo ya no está en pie.")));
            }
            catch (Exception ex) { Log.Error("Sincronizando el diario de misiones", ex); }
        }
    }
}
