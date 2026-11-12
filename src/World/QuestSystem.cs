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
    /// <summary>Encargo que un NPC le ha hecho al jugador en una charla.</summary>
    public class AiQuest
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("giver")] public string GiverId;
        [JsonProperty("giver_name")] public string GiverName;
        [JsonProperty("name")] public string Name;                 // en el idioma del jugador
        [JsonProperty("description")] public string Description;   // en el idioma del jugador
        [JsonProperty("brief")] public string Brief;               // en inglés, para los prompts
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
        /// <summary>Otros héroes implicados (objetivo, testigo…): también saben del encargo.</summary>
        [JsonProperty("targets")] public List<string> Targets = new List<string>();

        // Objetivo que comprueba el juego (ver QuestObjectives)
        [JsonProperty("obj_type")] public string ObjType = "none";
        [JsonProperty("obj_target")] public string ObjTargetId;
        [JsonProperty("obj_target_name")] public string ObjTargetName;
        [JsonProperty("obj_amount")] public int ObjAmount;
        [JsonProperty("obj_met")] public bool ObjMet;
        [JsonProperty("party_id")] public string PartyId;        // banda creada para el encargo
        [JsonProperty("band_by_player")] public bool BandByPlayer;
        [JsonProperty("doc_name")] public string DocName;        // documento a entregar
        [JsonProperty("obj_item")] public string ObjItemId;      // deliver_item: qué objeto
        [JsonProperty("obj_item_name")] public string ObjItemName;
        [JsonProperty("delivered")] public int Delivered;        // unidades ya entregadas en mano

        [JsonIgnore] public bool Active => State == "active";
    }

    /// <summary>Lo que pide la IA en el campo "quest" de su respuesta.</summary>
    public class QuestRequest
    {
        public string Action = "";       // create | update | complete | fail
        public string QuestId = "";
        public string Name = "", Description = "", Brief = "", Note = "", Reason = "", ProgressLabel = "";
        public int Days, Reward, ProgressTarget, SetProgress = -1;
        public List<string> Targets = new List<string>();
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

    /// <summary>Misiones creadas por la IA: crear, anotar progreso, completar (con recompensa) y fracasar.</summary>
    internal static class QuestSystem
    {
        public static List<AiQuest> Quests = new List<AiQuest>();
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;

        public static AiQuest ActiveFrom(string giverId) => Quests.FirstOrDefault(q => q.Active && q.GiverId == giverId);
        public static IEnumerable<AiQuest> ActiveKnownTo(string heroId) => Quests.Where(q => q.Active && (q.GiverId == heroId || q.Targets.Contains(heroId)));
        public static IEnumerable<AiQuest> PastWith(string giverId) => Quests.Where(q => !q.Active && q.GiverId == giverId).OrderByDescending(q => q.EndDay);

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));
        private static string Clip(string s, int n) => string.IsNullOrEmpty(s) ? "" : s.Length <= n ? s : s.Substring(0, n);

        // ============================================================ texto para el prompt

        public static string DescribeFor(Speaker s)
        {
            if (!S.Get.EnableQuests || !s.IsHero) return "";
            double now = CampaignTime.Now.ToDays;
            var sb = new StringBuilder();
            foreach (var q in ActiveKnownTo(s.Hero.StringId))
            {
                bool mine = q.GiverId == s.Hero.StringId;
                int left = (int)Math.Ceiling(q.DueDay - now);
                sb.Append(mine ? "- A task YOU gave them" : $"- A task {q.GiverName} gave them, which concerns you")
                  .Append($" [quest_id {q.Id}]: {(string.IsNullOrWhiteSpace(q.Brief) ? q.Description : q.Brief)}")
                  .Append($" Given {(int)(now - q.CreatedDay)} day(s) ago; {(left > 0 ? left + " day(s) left" : "the deadline is today")}.");
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

        // ============================================================ aplicar lo que pide la IA

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
                    if (ActiveFrom(h.StringId) != null) { Log.Warn($"{s.Name} ya tiene un encargo activo: no se crea otro"); break; }
                    if (Quests.Count(q => q.Active) >= cfg.MaxActiveQuests) { Log.Warn("Demasiados encargos activos"); break; }
                    if (r.Name.Length < 3 || (r.Description.Length < 10 && r.Brief.Length < 10)) { Log.Warn("Encargo sin nombre o descripción: ignorado"); break; }
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
                    if (r.Note.Length > 0) q.Log.Add(Clip(r.Note, 200));
                    QuestObjectives.Resolve(q, r, s);
                    Quests.Add(q);
                    Log.Info($"Encargo creado por {s.Name}: {q.Name} ({days} días, {reward} denarios) [{q.Id}]");
                    GameJournal.Create(q);
                    shown.Add(L.F("AIL_2bd6ef00", $"Nuevo encargo: «{q.Name}» ({days} días") + (reward > 0 ? L.F("AIL_de098566", $", {reward} denarios") : "") + ")");
                    break;
                }

                case "update":
                {
                    var q = Find(h, r.QuestId, allowTarget: true);
                    if (q == null) break;
                    if (r.Note.Length > 0) q.Log.Add(Clip(r.Note, 200));
                    if (r.SetProgress >= 0 && q.ProgressTarget > 0 && !QuestObjectives.HasObjective(q)) q.Progress = Math.Min(q.ProgressTarget, r.SetProgress);
                    while (q.Log.Count > 8) q.Log.RemoveAt(0);
                    GameJournal.Update(q, r.Note);
                    Log.Info($"Encargo {q.Id} actualizado por {s.Name}: {r.Note} {(q.ProgressTarget > 0 ? q.Progress + "/" + q.ProgressTarget : "")}");
                    shown.Add(L.F("AIL_0681e274", $"Encargo «{q.Name}»: ") + (q.ProgressTarget > 0 ? $"{q.Progress}/{q.ProgressTarget} {q.ProgressLabel}" : "anotado"));
                    break;
                }

                case "complete":
                {
                    var q = Find(h, r.QuestId, allowTarget: false);
                    if (q == null) break;
                    if (!QuestObjectives.Met(q))
                    {
                        // El NPC lo da por hecho, pero el mundo dice que no: no se cumple ni se paga
                        Log.Warn($"{s.Name} quiso dar por cumplido {q.Id}, pero falta: {QuestObjectives.MissingEs(q)}");
                        shown.Add(L.F("AIL_390cb390", $"«{q.Name}» aún no está cumplido: {QuestObjectives.MissingEs(q)}"));
                        break;
                    }
                    var handed = QuestObjectives.OnCompleted(q, s);
                    if (handed != null) shown.Add(handed);
                    End(q, "completed", r.Reason);
                    int paid = Math.Min(q.RewardGold, Math.Max(0, h.Gold));
                    if (paid > 0) GiveGoldAction.ApplyBetweenCharacters(h, P, paid);
                    if (cfg.QuestRelationReward > 0) ChangeRelationAction.ApplyPlayerRelation(h, cfg.QuestRelationReward, affectRelatives: false, showQuickNotification: true);
                    Bonds.ChangeTrust(s, 10);
                    GameJournal.Close(q, "completed", string.IsNullOrWhiteSpace(r.Reason) ? L.F("AIL_01ad2df2", $"{s.Name} da el encargo por cumplido.") : r.Reason);
                    shown.Add(L.F("AIL_89bc1b1d", $"Encargo cumplido: «{q.Name}»") + (paid > 0 ? L.F("AIL_795b98cd", $", recibes {paid} denarios") : ""));
                    News(q, s, true);
                    break;
                }

                case "fail":
                {
                    var q = Find(h, r.QuestId, allowTarget: false);
                    if (q == null) break;
                    Fail(q, s, "failed", r.Reason);
                    shown.Add(L.F("AIL_a0af06ce", $"Encargo fracasado: «{q.Name}»"));
                    break;
                }
            }
            return shown;
        }

        /// <summary>Id que no usa ningún encargo (ni activo ni del historial) ni ninguna misión del diario.</summary>
        private static string NewId()
        {
            for (int i = 0; i < 1000; i++)
            {
                string id = "q" + Rng.Next(100000, 1000000);
                if (Quests.Any(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase))) continue;
                try { if (GameJournal.Find(id) != null) continue; } catch { }
                return id;
            }
            // Casi imposible: se recurre a la hora y a un contador
            return "q" + DateTime.UtcNow.Ticks.ToString("x") + Quests.Count;
        }

        private static AiQuest Find(Hero h, string id, bool allowTarget)
        {
            var mine = Quests.Where(q => q.Active && (q.GiverId == h.StringId || (allowTarget && q.Targets.Contains(h.StringId)))).ToList();
            var q2 = mine.FirstOrDefault(q => string.Equals(q.Id, id, StringComparison.OrdinalIgnoreCase)) ?? (mine.Count == 1 ? mine[0] : null);
            if (q2 == null) Log.Warn($"Encargo no encontrado para {h.Name}: '{id}'");
            return q2;
        }

        private static void End(AiQuest q, string state, string reason)
        {
            q.State = state;
            q.EndDay = CampaignTime.Now.ToDays;
            q.Reason = Clip(reason, 200);
            QuestObjectives.OnEnded(q);
            Log.Info($"Encargo {q.Id} «{q.Name}»: {state} ({reason})");
        }

        private static void Fail(AiQuest q, Speaker s, string state, string reason)
        {
            End(q, state, reason);
            var h = Hero.Find(q.GiverId);
            if (h != null && h.IsAlive)
            {
                ChangeRelationAction.ApplyPlayerRelation(h, -Math.Max(1, S.Get.QuestRelationReward / 2), affectRelatives: false, showQuickNotification: true);
                Bonds.ChangeTrust(s ?? Speaker.FromHero(h), -8);
            }
            GameJournal.Close(q, state, state == "expired" ? L.T("AIL_f991b6e7", "Se acabó el plazo.") : string.IsNullOrWhiteSpace(reason) ? L.T("AIL_c53c096c", "El encargo ha fracasado.") : reason);
        }

        /// <summary>El juego ha dado por vencida la misión del diario antes que nuestro tick diario.</summary>
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

        // ============================================================ muertes: el encargo se cierra al momento

        // Se escucha la muerte de héroes del juego (sin esperar al tick diario): si muere quien lo dio, o la persona a la que
        // había que ver o entregar algo, el encargo se cierra en el acto y su misión sale del diario (y del mapa).
        private static readonly object HookOwner = new object();
        private static WeakReference _hookedOn;

        /// <summary>Engancha el aviso de muerte en la campaña actual (una sola vez por campaña).</summary>
        public static void EnsureHooks()
        {
            try
            {
                if (Campaign.Current == null) return;
                var ev = CampaignEvents.HeroKilledEvent;
                if (ev == null || (_hookedOn != null && ReferenceEquals(_hookedOn.Target, ev))) return;
                ev.AddNonSerializedListener(HookOwner, OnHeroKilled);
                _hookedOn = new WeakReference(ev);
                Log.Info("Encargos: escuchando las muertes de héroes");
            }
            catch (Exception ex) { Log.Error("Encargos: enganchar muertes", ex); }
        }

        private static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification)
        {
            try
            {
                if (victim == null) return;
                CloseForDeath(victim.StringId);
            }
            catch (Exception ex) { Log.Error("Encargos: muerte de un héroe", ex); }
        }

        /// <summary>Cierra los encargos que ya no pueden seguir porque ha muerto quien los dio o su objetivo.</summary>
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
                    // Ya no se puede cumplir, y no es culpa del jugador: se cancela sin castigo
                    End(q, "failed", $"{q.ObjTargetName}, whom the task concerned, died");
                    GameJournal.Close(q, "cancelled", L.F("AIL_cb1eb4c2", $"{q.ObjTargetName} ha muerto."));
                    continue;
                }
                // Sigue en pie (objetivo ya cumplido o solo era un implicado): deja de marcarse en el mapa
                if (objective || q.Targets.Contains(heroId)) GameJournal.Untrack(q, heroId);
            }
        }

        /// <summary>Al cargar: lo que haya muerto mientras tanto (o en una partida vieja) se cierra ya, antes de tocar el diario.</summary>
        public static void CloseDeadOnLoad()
        {
            try
            {
                foreach (var q in Quests.Where(x => x.Active).ToList())
                {
                    if (!q.Active) continue;
                    var giver = Hero.Find(q.GiverId);
                    if (giver == null || giver.IsDead) { CloseForDeath(q.GiverId); continue; }
                    if (!string.IsNullOrEmpty(q.ObjTargetId) && (q.ObjType == "talk_to" || q.ObjType == "deliver_item" || q.ObjType == "deliver_document"))
                    {
                        var t = Hero.Find(q.ObjTargetId);
                        if (t == null || t.IsDead) CloseForDeath(q.ObjTargetId);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Encargos: muertos al cargar", ex); }
        }

        // ============================================================ tiempo

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
                    // Red de seguridad por si el aviso de muerte no llegó
                    if (giver == null || giver.IsDead) { End(q, "failed", "the giver died"); GameJournal.Close(q, "cancelled", L.F("AIL_cb1eb4c2", $"{q.GiverName} ha muerto.")); continue; }
                    if (now >= q.DueDay)
                    {
                        Fail(q, null, "expired", "the deadline passed with no word from them");
                        var mem = MemoryStore.Get(giver.StringId, giver.Name.ToString());
                        ChatSession.AddNote(mem, $"They let the deadline pass on the task I gave them ({(string.IsNullOrWhiteSpace(q.Brief) ? q.Name : q.Brief)}).", S.Get.MaxNotesPerHero, q.GiverName);
                    }
                    else if ((int)(q.DueDay - now) == 2) Say(L.F("AIL_b14d8a23", $"Quedan 2 días para el encargo de {q.GiverName}: {q.Name}"), Colors.Yellow);
                }
                // historial corto
                var old = Quests.Where(x => !x.Active).OrderByDescending(x => x.EndDay).Skip(60).ToList();
                foreach (var x in old) Quests.Remove(x);
            }
            catch (Exception ex) { Log.Error("Encargos: tick diario", ex); }
        }
    }

    /// <summary>Objeto de rol (carta, contrato, sello, prenda…) que lleva el jugador. No es un objeto del juego: solo existe para la historia.</summary>
    public class RpItem
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("desc")] public string Description;
        [JsonProperty("from")] public string FromId;
        [JsonProperty("from_name")] public string FromName;
        [JsonProperty("day")] public double Day;
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

        /// <summary>Para el prompt: lo que el jugador lleva encima y podría enseñar.</summary>
        public static string Describe()
        {
            if (Items.Count == 0) return "";
            return string.Join("\n", Items.Skip(Math.Max(0, Items.Count - 10)).Select(i => $"- \"{i.Name}\" (from {i.FromName}): {i.Description}"));
        }
    }
}
