using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.ObjectSystem;

namespace AILegacy.World
{
    /// <summary>
    /// Objetivos de encargo que comprueba el juego, no la palabra del jugador:
    ///   bring_item        traer N unidades de un objeto real (se entregan al cumplir)
    ///   talk_to           hablar con una persona real (marcada en el mapa)
    ///   visit             ir a un asentamiento real
    ///   defeat_bandits    acabar con una banda que aparece de verdad junto a un asentamiento
    ///   deliver_document  llevar un documento a una persona real, que debe recogerlo
    /// Sin objetivo ("none") el encargo es libre y lo juzga quien lo dio.
    /// </summary>
    internal static class QuestObjectives
    {
        private static Hero P => Hero.MainHero;
        private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        // ============================================================ crear

        /// <summary>Convierte lo que pidió la IA en un objetivo real. Si no se puede, el encargo queda libre.</summary>
        public static void Resolve(AiQuest q, QuestRequest r, Speaker giver)
        {
            q.ObjType = "none";
            string type = (r.ObjType ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0 || type == "none") return;
            try
            {
                switch (type)
                {
                    case "bring_item":
                    {
                        var item = FindItem(r.ObjTarget);
                        if (item == null) break;
                        q.ObjType = type; q.ObjTargetId = item.StringId; q.ObjTargetName = item.Name.ToString();
                        q.ObjAmount = Math.Max(1, Math.Min(200, r.ObjAmount <= 0 ? 1 : r.ObjAmount));
                        q.ProgressTarget = q.ObjAmount; q.ProgressLabel = q.ObjTargetName;
                        q.Progress = Math.Min(q.ObjAmount, Carried(q));
                        break;
                    }
                    case "deliver_item":
                    {
                        var h = FindHero(r.ObjTarget, giver.Hero);
                        var item = FindItem(r.ObjItem);
                        if (h == null || item == null) break;
                        q.ObjType = type; q.ObjTargetId = h.StringId; q.ObjTargetName = h.Name.ToString();
                        q.ObjItemId = item.StringId; q.ObjItemName = item.Name.ToString();
                        q.ObjAmount = Math.Max(1, Math.Min(200, r.ObjAmount <= 0 ? 1 : r.ObjAmount));
                        q.ProgressTarget = q.ObjAmount; q.ProgressLabel = q.ObjItemName;
                        if (!q.Targets.Contains(h.StringId)) q.Targets.Add(h.StringId);
                        break;
                    }
                    case "talk_to":
                    case "deliver_document":
                    {
                        var h = FindHero(r.ObjTarget, giver.Hero);
                        if (h == null) break;
                        q.ObjType = type; q.ObjTargetId = h.StringId; q.ObjTargetName = h.Name.ToString();
                        if (!q.Targets.Contains(h.StringId)) q.Targets.Add(h.StringId);
                        if (type == "deliver_document")
                        {
                            q.DocName = q.Name;
                            Belongings.Items.RemoveAll(i => Belongings.Norm(i.Name) == Belongings.Norm(q.DocName));
                            Belongings.Items.Add(new RpItem
                            {
                                Name = q.DocName, FromId = giver.Hero.StringId, FromName = giver.Name, Day = CampaignTime.Now.ToDays,
                                Description = $"To be delivered to {h.Name} on behalf of {giver.Name}. " + (string.IsNullOrWhiteSpace(q.Brief) ? q.Description : q.Brief),
                            });
                            Say(L.F("AIL_c573f4b8", $"{giver.Name} te entrega: {q.DocName} (para {h.Name})."), Colors.Yellow);
                        }
                        break;
                    }
                    case "visit":
                    {
                        var st = OrderSystem.FindSettlement(r.ObjTarget);
                        if (st == null) break;
                        q.ObjType = type; q.ObjTargetId = st.StringId; q.ObjTargetName = st.Name.ToString();
                        break;
                    }
                    case "defeat_bandits":
                    {
                        var st = OrderSystem.FindSettlement(r.ObjTarget) ?? giver.Settlement ?? giver.HomeSettlement;
                        if (st == null) break;
                        var party = SpawnBand(q, st);
                        if (party == null) break;
                        q.ObjType = type; q.ObjTargetId = st.StringId; q.ObjTargetName = st.Name.ToString(); q.PartyId = party.StringId;
                        break;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Objetivo del encargo", ex); q.ObjType = "none"; }

            if (q.ObjType == "none")
            {
                Log.Warn($"Objetivo '{type}' con destino '{r.ObjTarget}' {r.ObjItem} no encontrado: el encargo queda libre (lo juzga quien lo dio)");
                Say(L.T("AIL_1191e28d", "(Este encargo nombra a alguien o algo que no existe en el mundo: no se puede comprobar solo y lo juzgará quien te lo dio.)"), Colors.Gray);
            }
            else Log.Info($"Objetivo del encargo {q.Id}: {q.ObjType} → {q.ObjTargetName}" + (q.ObjAmount > 0 ? $" x{q.ObjAmount}" : ""));
        }

        private static ItemObject FindItem(string name)
        {
            string n = Norm(name);
            if (n.Length < 3) return null;
            var all = MBObjectManager.Instance.GetObjectTypeList<ItemObject>().Where(i => i != null && i.Name != null).ToList();
            // primero lo corriente (mercancía, comida, animales); después cualquier objeto
            foreach (var pool in new[] { all.Where(i => i.IsTradeGood || i.IsFood || i.IsAnimal).ToList(), all })
            {
                var hit = pool.FirstOrDefault(i => Norm(i.Name.ToString()) == n || Norm(i.StringId) == n)
                          ?? pool.Where(i => { var a = Norm(i.Name.ToString()); return a.Length > 2 && (a.Contains(n) || n.Contains(a)); }).OrderBy(i => i.Value).FirstOrDefault();
                if (hit != null) return hit;
            }
            return null;
        }

        private static Hero FindHero(string name, Hero notThis)
        {
            string n = Norm(name);
            if (n.Length < 2) return null;
            var byId = Hero.Find(name);
            if (byId != null && byId.IsAlive && byId != notThis && byId != P) return byId;
            var all = Hero.AllAliveHeroes.Where(h => h != notThis && h != P && !h.IsChild).ToList();
            return all.FirstOrDefault(h => Norm(h.Name.ToString()) == n)
                   ?? all.FirstOrDefault(h => { var a = Norm(h.Name.ToString()); return a.Length > 2 && (n.Contains(a) || a.Contains(n)); });
        }

        /// <summary>Una banda real, con nombre, junto al asentamiento: es el objetivo que hay que derrotar.</summary>
        private static MobileParty SpawnBand(AiQuest q, Settlement st)
        {
            var clan = Clan.BanditFactions.FirstOrDefault(c => c.StringId == "looters") ?? Clan.BanditFactions.FirstOrDefault();
            if (clan == null) return null;
            var party = BanditPartyComponent.CreateLooterParty("ailegacy_band_" + q.Id + "_" + (int)CampaignTime.Now.ToHours, clan, st, false, clan.DefaultPartyTemplate, st.GatePosition);
            if (party == null) return null;
            try
            {
                // Que sea un rival a la medida: ni un paseo ni imposible
                int mine = Math.Max(5, MobileParty.MainParty.MemberRoster.TotalHealthyCount);
                int want = Math.Max(8, Math.Min(120, (int)(mine * 0.8)));
                var basic = clan.Culture?.BanditBandit ?? clan.Culture?.BasicTroop;
                int have = party.MemberRoster.TotalManCount;
                if (basic != null && have < want) party.MemberRoster.AddToCounts(basic, want - have);
                party.Party.SetCustomName(new TextObject("{=!}" + L.F("AIL_51f2c203", $"Banda buscada por {q.GiverName}").Replace("{", "(").Replace("}", ")")));
                party.SetPartyUsedByQuest(true);
                party.SetMovePatrolAroundSettlement(st, MobileParty.NavigationType.Default, false);
            }
            catch (Exception ex) { Log.Error("Preparando la banda del encargo", ex); }
            Log.Info($"Banda creada junto a {st.Name} para el encargo {q.Id}: {party.MemberRoster.TotalManCount} hombres");
            return party;
        }

        public static MobileParty BandOf(AiQuest q) =>
            string.IsNullOrEmpty(q.PartyId) ? null : MobileParty.All.FirstOrDefault(p => p != null && p.StringId == q.PartyId && p.IsActive);

        // ============================================================ estado

        private static int Carried(AiQuest q)
        {
            try
            {
                var item = MBObjectManager.Instance.GetObject<ItemObject>(q.ObjTargetId);
                return item == null || MobileParty.MainParty == null ? 0 : MobileParty.MainParty.ItemRoster.GetItemNumber(item);
            }
            catch { return 0; }
        }

        public static bool HasObjective(AiQuest q) => !string.IsNullOrEmpty(q.ObjType) && q.ObjType != "none";

        public static bool Met(AiQuest q)
        {
            if (!HasObjective(q)) return true;
            if (q.ObjType == "bring_item") return q.Delivered + Carried(q) >= q.ObjAmount;
            if (q.ObjType == "deliver_item") return q.Delivered >= q.ObjAmount;
            return q.ObjMet;
        }

        /// <summary>
        /// Alguien acaba de aceptar objetos del jugador (acción take_item). Si es a quien había que llevárselos
        /// (el destinatario de un deliver_item, o el propio dador en un bring_item), cuenta para el encargo.
        /// </summary>
        public static void OnItemTaken(Speaker taker, ItemObject item, int amount)
        {
            try
            {
                if (taker == null || !taker.IsHero || item == null || amount <= 0) return;
                string who = taker.Hero.StringId;
                foreach (var q in QuestSystem.Quests.Where(x => x.Active).ToList())
                {
                    bool deliver = q.ObjType == "deliver_item" && q.ObjTargetId == who && q.ObjItemId == item.StringId;
                    bool bring = q.ObjType == "bring_item" && q.GiverId == who && q.ObjTargetId == item.StringId;
                    if (!deliver && !bring) continue;
                    q.Delivered = Math.Min(q.ObjAmount, q.Delivered + amount);
                    q.Progress = q.Delivered;
                    bool done = q.Delivered >= q.ObjAmount;
                    string note = deliver
                        ? (done ? L.F("AIL_d8f59858", $"{taker.Name} ha recibido {q.ObjAmount} × {item.Name}. Vuelve con {q.GiverName}.") : L.F("AIL_6c1f353e", $"{taker.Name} ha recibido {q.Delivered} de {q.ObjAmount} × {item.Name}."))
                        : $"Has entregado {q.Delivered} de {q.ObjAmount} × {item.Name} a {q.GiverName}.";
                    if (done && deliver) MarkMet(q, note);
                    else { GameJournal.Update(q, note); Say($"{q.Name}: {note}", Colors.Green); }
                }
            }
            catch (Exception ex) { Log.Error("Encargos: objeto entregado", ex); }
        }

        /// <summary>
        /// Gente y sitios REALES que este NPC puede nombrar en un encargo (para que no se invente administradores ni informadores):
        /// los notables de sus tierras o del lugar donde está, con su oficio.
        /// </summary>
        public static string RealPeopleFor(Speaker s)
        {
            try
            {
                var places = new List<Settlement>();
                if (s.Settlement != null) places.Add(s.Settlement);
                if (s.IsHero && s.Hero.Clan != null && s.Hero.Clan != Clan.PlayerClan)
                    foreach (var st in s.Hero.Clan.Settlements.Where(x => x.IsTown || x.IsCastle).Take(3))
                    {
                        places.Add(st);
                        if (st.BoundVillages != null) foreach (var v in st.BoundVillages.Take(3)) places.Add(v.Settlement);
                    }
                if (s.IsHero && s.Hero.HomeSettlement != null) places.Add(s.Hero.HomeSettlement);
                var lines = new List<string>();
                foreach (var st in places.Where(x => x != null).Distinct().Take(8))
                {
                    var people = st.Notables.Where(n => n != null && n.IsAlive && (!s.IsHero || n != s.Hero)).Take(4)
                        .Select(n => $"{n.Name} ({(n.IsMerchant ? "merchant" : n.IsArtisan ? "artisan" : n.IsGangLeader ? "gang leader" : n.IsHeadman ? "headman" : n.IsRuralNotable ? "landowner" : n.IsPreacher ? "preacher" : "notable")})").ToList();
                    if (people.Count > 0) lines.Add($"- {st.Name} ({(st.IsTown ? "town" : st.IsCastle ? "castle" : "village")}): {string.Join(", ", people)}");
                    else lines.Add($"- {st.Name} ({(st.IsTown ? "town" : st.IsCastle ? "castle" : "village")})");
                }
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }

        /// <summary>Para el prompt: el hecho comprobado, que el NPC no puede contradecir.</summary>
        public static string StatusLine(AiQuest q)
        {
            if (!HasObjective(q)) return "";
            bool met = Met(q);
            string what;
            switch (q.ObjType)
            {
                case "bring_item": what = $"bring YOU {q.ObjAmount} x {q.ObjTargetName}; handed over so far {q.Delivered}, and they carry {Carried(q)} right now"; break;
                case "deliver_item": what = $"hand {q.ObjAmount} x {q.ObjItemName} to {q.ObjTargetName} (who must take them with take_item); received so far {q.Delivered}"; break;
                case "talk_to": what = $"speak with {q.ObjTargetName}"; break;
                case "visit": what = $"go to {q.ObjTargetName}"; break;
                case "defeat_bandits": what = $"destroy the band prowling near {q.ObjTargetName}"; break;
                case "deliver_document": what = $"hand the document \"{q.DocName}\" to {q.ObjTargetName}"; break;
                default: what = q.ObjType; break;
            }
            return $" CHECKED BY THE WORLD: {what} → {(met ? "DONE" : "NOT DONE")}.";
        }

        /// <summary>Para el diario del jugador.</summary>
        public static string GoalEs(AiQuest q)
        {
            switch (q.ObjType)
            {
                case "bring_item": return $"Objetivo: llevarle {q.ObjAmount} × {q.ObjTargetName} a {q.GiverName}.";
                case "deliver_item": return L.F("AIL_52b80370", $"Objetivo: entregar {q.ObjAmount} × {q.ObjItemName} a {q.ObjTargetName} (marcado en el mapa) y volver con {q.GiverName}.");
                case "talk_to": return L.F("AIL_cb8abbac", $"Objetivo: hablar con {q.ObjTargetName} y volver.");
                case "visit": return L.F("AIL_98dfb3c6", $"Objetivo: ir a {q.ObjTargetName} y volver.");
                case "defeat_bandits": return L.F("AIL_b2e0ea9d", $"Objetivo: acabar con la banda que ronda {q.ObjTargetName} (marcada en el mapa) y volver.");
                case "deliver_document": return L.F("AIL_dc14749e", $"Objetivo: entregar «{q.DocName}» a {q.ObjTargetName} y volver.");
                default: return "";
            }
        }

        public static string MissingEs(AiQuest q)
        {
            switch (q.ObjType)
            {
                case "bring_item": return L.F("AIL_f1406603", $"has entregado {q.Delivered} y llevas {Carried(q)} de {q.ObjAmount} × {q.ObjTargetName}");
                case "deliver_item": return L.F("AIL_08d1b4a4", $"{q.ObjTargetName} ha recibido {q.Delivered} de {q.ObjAmount} × {q.ObjItemName}");
                case "talk_to": return L.F("AIL_e0d57f33", $"aún no has hablado con {q.ObjTargetName}");
                case "visit": return L.F("AIL_378f2647", $"aún no has ido a {q.ObjTargetName}");
                case "defeat_bandits": return L.F("AIL_e0a76c57", $"la banda de {q.ObjTargetName} sigue en pie");
                case "deliver_document": return L.F("AIL_5eeedbdf", $"{q.ObjTargetName} aún no ha recibido «{q.DocName}»");
                default: return "";
            }
        }

        private static void MarkMet(AiQuest q, string note)
        {
            if (q.ObjMet) return;
            q.ObjMet = true;
            if (q.ProgressTarget > 0) q.Progress = q.ProgressTarget;
            GameJournal.Update(q, note);
            Say($"{q.Name}: {note}", Colors.Green);
            Log.Info($"Objetivo cumplido del encargo {q.Id}: {note}");
        }

        // ============================================================ sucesos del juego

        public static void OnSettlementEntered(MobileParty party, Settlement st, Hero hero)
        {
            try
            {
                if (party == null || !party.IsMainParty || st == null) return;
                foreach (var q in QuestSystem.Quests.Where(x => x.Active && x.ObjType == "visit" && x.ObjTargetId == st.StringId).ToList())
                    MarkMet(q, L.F("AIL_ff5c778e", $"Has llegado a {st.Name}. Vuelve con {q.GiverName}."));
            }
            catch (Exception ex) { Log.Error("Encargos: entrada en asentamiento", ex); }
        }

        public static void OnPartyDestroyed(MobileParty destroyed, PartyBase destroyer)
        {
            try
            {
                if (destroyed == null) return;
                foreach (var q in QuestSystem.Quests.Where(x => x.Active && x.ObjType == "defeat_bandits" && x.PartyId == destroyed.StringId).ToList())
                {
                    bool mine = destroyer != null && (destroyer == PartyBase.MainParty || (MobileParty.MainParty?.Army != null && destroyer.MobileParty?.Army == MobileParty.MainParty.Army));
                    q.BandByPlayer = mine;
                    MarkMet(q, mine ? L.F("AIL_d83e63f4", $"Has acabado con la banda de {q.ObjTargetName}. Vuelve con {q.GiverName}.")
                                    : L.F("AIL_8525c8a9", $"Otros han acabado con la banda de {q.ObjTargetName} antes que tú. Vuelve con {q.GiverName}."));
                }
            }
            catch (Exception ex) { Log.Error("Encargos: grupo destruido", ex); }
        }

        /// <summary>Has hablado (de verdad, con al menos un intercambio) con este héroe.</summary>
        public static void OnTalked(Hero h)
        {
            try
            {
                if (h == null) return;
                foreach (var q in QuestSystem.Quests.Where(x => x.Active && x.ObjType == "talk_to" && x.ObjTargetId == h.StringId).ToList())
                    MarkMet(q, L.F("AIL_a615d39e", $"Has hablado con {h.Name}. Vuelve con {q.GiverName}."));
            }
            catch (Exception ex) { Log.Error("Encargos: charla", ex); }
        }

        public static void OnDocumentTaken(Speaker taker, RpItem item)
        {
            try
            {
                if (taker == null || !taker.IsHero || item == null) return;
                foreach (var q in QuestSystem.Quests.Where(x => x.Active && x.ObjType == "deliver_document" && x.ObjTargetId == taker.Hero.StringId
                                                              && Belongings.Norm(x.DocName) == Belongings.Norm(item.Name)).ToList())
                    MarkMet(q, L.F("AIL_8281a6ef", $"{taker.Name} ha recibido «{item.Name}». Vuelve con {q.GiverName}."));
            }
            catch (Exception ex) { Log.Error("Encargos: documento", ex); }
        }

        /// <summary>Cada día: refresca lo que llevas (para el diario) y detecta bandas que han desaparecido sin avisar.</summary>
        public static void OnDailyTick()
        {
            try
            {
                foreach (var q in QuestSystem.Quests.Where(x => x.Active && HasObjective(x)).ToList())
                {
                    if (q.ObjType == "bring_item")
                    {
                        int now = Math.Min(q.ObjAmount, q.Delivered + Carried(q));
                        if (now != q.Progress) { q.Progress = now; GameJournal.Update(q, null); }
                    }
                    else if (q.ObjType == "defeat_bandits" && !q.ObjMet && BandOf(q) == null)
                        MarkMet(q, L.F("AIL_cf0db558", $"La banda de {q.ObjTargetName} ya no existe. Vuelve con {q.GiverName}."));
                }
            }
            catch (Exception ex) { Log.Error("Encargos: objetivos, tick diario", ex); }
        }

        // ============================================================ cierre

        /// <summary>Al cumplir: se entrega lo traído. Devuelve un texto para enseñar, o null.</summary>
        public static string OnCompleted(AiQuest q, Speaker giver)
        {
            try
            {
                if (q.ObjType == "bring_item")
                {
                    var item = MBObjectManager.Instance.GetObject<ItemObject>(q.ObjTargetId);
                    if (item != null)
                    {
                        int n = Math.Max(0, Math.Min(q.ObjAmount - q.Delivered, Carried(q)));
                        if (n == 0) return null;
                        MobileParty.MainParty.ItemRoster.AddToCounts(item, -n);
                        if (giver != null && giver.IsHero) OrderSystem.PartyLedBy(giver.Hero)?.ItemRoster.AddToCounts(item, n);
                        return L.F("AIL_76df6ddb", $"Entregas {n} × {item.Name}.");
                    }
                }
            }
            catch (Exception ex) { Log.Error("Encargos: entrega", ex); }
            return null;
        }

        /// <summary>Al cerrar un encargo (como sea): la banda deja de estar reservada y el documento sin entregar se retira.</summary>
        public static void OnEnded(AiQuest q)
        {
            try
            {
                var band = BandOf(q);
                if (band != null) band.SetPartyUsedByQuest(false);
                if (q.ObjType == "deliver_document" && !q.ObjMet && !string.IsNullOrEmpty(q.DocName))
                    Belongings.Items.RemoveAll(i => Belongings.Norm(i.Name) == Belongings.Norm(q.DocName));
            }
            catch (Exception ex) { Log.Error("Encargos: limpieza", ex); }
        }

        /// <summary>Qué hay que marcar en el mapa para este encargo.</summary>
        public static IEnumerable<ITrackableCampaignObject> Trackables(AiQuest q)
        {
            var list = new List<ITrackableCampaignObject>();
            try
            {
                switch (q.ObjType)
                {
                    case "talk_to":
                    case "deliver_document":
                    case "deliver_item":
                        var h = Hero.Find(q.ObjTargetId); if (h != null) list.Add(h); break;
                    case "visit":
                        var st = Settlement.Find(q.ObjTargetId); if (st != null) list.Add(st); break;
                    case "defeat_bandits":
                        var b = BandOf(q); if (b != null) list.Add(b); break;
                }
            }
            catch { }
            return list;
        }
    }
}
