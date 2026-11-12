using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CampaignBehaviors;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>
    /// Diplomacia de reyes por IA: declaraciones públicas con acciones reales (guerra, paz con condiciones, alianzas,
    /// comercio, tributos, reparaciones, territorios, feudos, expulsiones, impuestos), propuestas pendientes entre coronas
    /// y las declaraciones del jugador cuando es rey. Las palabras de un rey no cambian nada sin su acción.
    /// </summary>
    internal static class Diplomacy
    {
        private static readonly Random Rng = new Random();
        public static bool Enabled => S.Get.EnableDiplomacy && WorldSim.Enabled;
        private static WorldState St => WorldSim.State;
        private static Kingdom PlayerRealm => Hero.MainHero?.MapFaction as Kingdom;
        private static bool PlayerRules(Kingdom k) => k != null && k.Leader == Hero.MainHero;

        // ============================================================ alianzas y comercio (los del propio juego)

        private static IAllianceCampaignBehavior Alli { get { try { return Campaign.Current?.GetCampaignBehavior<IAllianceCampaignBehavior>(); } catch { return null; } } }
        private static ITradeAgreementsCampaignBehavior Trade { get { try { return Campaign.Current?.GetCampaignBehavior<ITradeAgreementsCampaignBehavior>(); } catch { return null; } } }

        public static bool Allied(Kingdom a, Kingdom b) { try { return Alli != null && Alli.IsAllyWithKingdom(a, b); } catch { return false; } }
        public static bool HasTrade(Kingdom a, Kingdom b) { try { return Trade != null && Trade.HasTradeAgreement(a, b, out _); } catch { return false; } }

        // ============================================================ propuestas

        private static Proposal Pending(string type, Kingdom from, Kingdom to) =>
            St.Proposals.FirstOrDefault(p => p.Type == type && p.From == from.StringId && p.To == to.StringId);

        private static Proposal AddProposal(string type, Kingdom from, Kingdom to)
        {
            St.Proposals.RemoveAll(p => p.Type == type && p.From == from.StringId && p.To == to.StringId);
            var p = new Proposal { Id = WorldSim.NewId("pr"), Type = type, From = from.StringId, To = to.StringId, Day = CampaignTime.Now.ToDays, ExpiresDay = CampaignTime.Now.ToDays + S.Get.ProposalLifetimeDays };
            St.Proposals.Add(p);
            return p;
        }

        private static string Terms(Proposal p)
        {
            var parts = new List<string>();
            if (p.DailyTribute > 0) parts.Add($"a tribute of {p.DailyTribute} denars a day for {p.TributeDays} days");
            if (p.Reparations > 0) parts.Add($"{p.Reparations} denars in reparations");
            if (!string.IsNullOrEmpty(p.SettlementId)) parts.Add($"the handing over of {Settlement.Find(p.SettlementId)?.Name}");
            if (p.TradeYears > 0) parts.Add($"for {p.TradeYears} year(s)");
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
        }

        private static string TermsEs(Proposal p)
        {
            var parts = new List<string>();
            if (p.DailyTribute > 0) parts.Add(L.F("AIL_fbf8b45d", $"un tributo de {p.DailyTribute} denarios al día durante {p.TributeDays} días"));
            if (p.Reparations > 0) parts.Add(L.F("AIL_555e7801", $"{p.Reparations} denarios de reparaciones"));
            if (!string.IsNullOrEmpty(p.SettlementId)) parts.Add(L.F("AIL_5c924813", $"la entrega de {Settlement.Find(p.SettlementId)?.Name}"));
            if (p.TradeYears > 0) parts.Add(L.F("AIL_fd19d606", $"durante {p.TradeYears} año(s)"));
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
        }

        private static string TypeEn(string t) => t == "peace" ? "peace" : t == "alliance" ? "an alliance" : t == "trade" ? "a trade agreement" : t == "tribute" ? "a tribute" : t == "reparations" ? "reparations" : "territory";
        private static string TypeEs(string t) => t == "peace" ? L.T("AIL_695a2835", "la paz") : t == "alliance" ? L.T("AIL_27bd042f", "una alianza") : t == "trade" ? L.T("AIL_08285b4c", "un acuerdo comercial") : t == "tribute" ? L.T("AIL_2d5ee223", "un tributo") : t == "reparations" ? "reparaciones" : L.T("AIL_81b3cd7d", "un territorio");
        private static bool IsDemand(string t) => t == "tribute" || t == "reparations" || t == "territory";

        public static string DescribeAllProposals()
        {
            double now = CampaignTime.Now.ToDays;
            return string.Join("\n", St.Proposals.Select(p => $"- {p.From} {(IsDemand(p.Type) ? "demands" : "proposes")} {TypeEn(p.Type)}{Terms(p)} {(IsDemand(p.Type) ? "from" : "to")} {p.To} ({(int)(now - p.Day)} day(s) ago, unanswered)"));
        }

        // ============================================================ tick diario

        public static void OnDailyTick(double now)
        {
            foreach (var p in St.Proposals.Where(x => now >= x.ExpiresDay).ToList())
            {
                St.Proposals.Remove(p);
                Log.Info($"[diplomacia] propuesta caducada: {p.From} → {p.To} ({p.Type})");
            }
            if (!Enabled) return;

            // Lo que va dirigido a ti como rey: decides tú, con una ventana
            var mine = PlayerRealm;
            if (PlayerRules(mine))
                foreach (var p in St.Proposals.Where(x => x.To == mine.StringId && !x.Notified).ToList())
                {
                    p.Notified = true;
                    AskPlayer(p);
                }

            if (!WorldSim.CanCall) return;
            var cfg = S.Get;
            var candidates = new List<(Kingdom k, WorldEvent ev, bool mustAnswer)>();

            // 1) Presión de respuesta: quien tiene una propuesta sin contestar
            foreach (var k in WorldSim.Kingdoms.Where(x => !PlayerRules(x)))
                if (St.Proposals.Any(p => p.To == k.StringId) && Ready(k, now, 1) && Rng.Next(100) < cfg.ResponsePressurePercent)
                    candidates.Add((k, null, true));

            // 2) Eventos que piden que un rey se pronuncie
            if (candidates.Count == 0)
                foreach (var ev in St.Events.Where(e => now - e.Day <= cfg.EngagementDays && e.Engagement.Count > 0).OrderByDescending(e => e.Importance))
                    foreach (var kv in ev.Engagement.ToList())
                    {
                        var k = WorldSim.FindKingdom(kv.Key);
                        if (k == null || PlayerRules(k) || !Ready(k, now, cfg.MinDaysBetweenStatements)) continue;
                        if (Rng.Next(100) < kv.Value) candidates.Add((k, ev, false));
                    }

            if (candidates.Count == 0) return;
            var pick = candidates[Rng.Next(candidates.Count)];
            MakeStatement(pick.k, pick.ev);     // la ocasión se gasta al aplicar la respuesta (ApplyStatement)
        }

        private static bool Ready(Kingdom k, double now, int minDays) =>
            !St.LastStatementDay.TryGetValue(k.StringId, out var d) || now - d >= minDays;

        // ============================================================ declaración de un rey

        private static readonly Tpl TStatement = new Tpl("kingdom_statement/statement.txt",
@"You are {ruler}, ruler of {kingdom} in Calradia, the medieval world of Mount & Blade II: Bannerlord. You are about to issue a public statement as sovereign.

How a sovereign acts:
- The technical data below is the truth right now. Chronicles and earlier statements are only context: what was said may never have happened.
- Words are not actions. Announcing war, peace, an alliance or a demand changes NOTHING unless you put the matching action in ""actions"". If you only want to speak, use no actions.
- Every decision must serve your realm: its safety, its treasury, its honour, or your grip on the throne. Weigh strength, war weariness, who is already fighting whom, and your stance towards each crown. A weary or losing realm seeks peace; a strong and rested one presses its advantage.
- If a proposal or demand is addressed to you, answer it now: accept it or reject it (and say why). Accepting a peace means accepting its terms.
- You speak as yourself: your character and culture show in the words. No modern language, no game terms, no numbers of troops or percentages.

Actions you may take (each is an object in ""actions""; target_kingdom is always a kingdom id from the data):
- declare_war (target_kingdom): war, at once, no consent needed. It breaks any alliance or trade agreement with them.
- propose_peace (target_kingdom) with optional terms you DEMAND from them: daily_tribute + tribute_days, reparations, settlement (a town or castle of theirs). Only against a realm you are at war with.
- accept_peace / reject_peace (target_kingdom): answer THEIR pending peace proposal.
- propose_alliance / accept_alliance / reject_alliance / break_alliance (target_kingdom): allies stand together in war. Propose only to a crown you are on good terms with and share an enemy or an interest with.
- propose_trade / accept_trade / reject_trade / end_trade (target_kingdom, trade_years 1-5): only between realms at peace.
- demand_tribute (target_kingdom, daily_tribute, tribute_days) / accept_tribute / reject_tribute / end_tribute: outside a peace treaty, a tribute is paid only by a realm that fears you.
- demand_reparations (target_kingdom, reparations) / accept_reparations / reject_reparations: a single payment.
- demand_territory (target_kingdom, settlement = a town or castle of theirs) / transfer_territory / reject_territory.
- expel_clan (clan = a vassal clan of yours, never your own): cast it out of the realm for treason or defiance.
- grant_fief (settlement = a town or castle held by your own clan, clan = a vassal clan of yours): reward loyalty.
- confiscate_fief (settlement = a town or castle of one of your vassal clans): take it for the crown; they will resent it.
- set_tax (tax_percent from {tax_min} to {tax_max}; 0 removes it): an extra crown tax on your towns. More gold, but loyalty and prosperity suffer.
- enact_law (law_name, law_text in {language}, law_effects = small daily changes in all your towns and castles: loyalty -1..1, prosperity -1.5..1.5, security -1..1, food -3..3): proclaim a law of your own. A law that gives with one hand takes with the other; at most {max_laws} laws at a time.
- repeal_law (law_name): abolish one of your laws in force.
- declare_war_on_clan / make_peace_with_clan (clan = an independent clan that belongs to no kingdom, such as one you expelled or that rebelled).
- quarantine_settlement (settlement = a town, castle or village of yours where there is sickness, quarantine_days 5-60) / lift_quarantine (settlement): closing it stops the sickness from spreading but hurts trade and tempers.

Answer ONLY with JSON:
{ ""thoughts"": ""private, in English, 2-5 sentences: facts first (who am I at war with, who proposed what to me, how tired is my realm), then what I want and why"",
  ""statement"": ""the public statement, {min_len}-{max_len} characters, in {language}"",
  ""summary"": ""what you said and did, in ONE English sentence"",
  ""importance"": 1-4,
  ""actions"": [ { ""type"": ""..."", ""target_kingdom"": ""id"", ""settlement"": ""name"", ""clan"": ""name"", ""daily_tribute"": 0, ""tribute_days"": 0, ""reparations"": 0, ""trade_years"": 0, ""tax_percent"": 0, ""law_name"": ""..."", ""law_text"": ""..."", ""law_effects"": { ""loyalty"": 0, ""prosperity"": 0, ""security"": 0, ""food"": 0 }, ""quarantine_days"": 0 } ],
  ""relation_changes"": [ { ""kingdom"": ""id"", ""change"": -{max_stance} to {max_stance} } ],
  ""kingdom_engagement"": { ""kingdom_id"": 0-100 } }
Use only the fields an action needs. relation_changes: how your stance towards other crowns shifts because of this. kingdom_engagement: the daily chance that another ruler answers you.");

        private static string DescribeRuler(Hero ruler)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"{ruler.Name}, {(ruler.IsFemale ? "woman" : "man")}, age {(int)ruler.Age}, {ruler.Culture?.Name} culture.");
            var snap = ContextBuilder.Snapshot(ruler);
            if (snap.TryGetValue("traits", out var traits)) sb.AppendLine("Traits: " + traits);
            var mem = MemoryStore.Find(ruler.StringId);
            var p = mem?.Profile;
            if (p != null && !p.IsEmpty)
            {
                sb.AppendLine("Personality: " + p.Personality);
                if (!string.IsNullOrWhiteSpace(p.Values)) sb.AppendLine("Values: " + p.Values);
                if (!string.IsNullOrWhiteSpace(p.Goals)) sb.AppendLine("Wants: " + p.Goals);
                if (!string.IsNullOrWhiteSpace(p.SpeechStyle)) sb.AppendLine("Speaks: " + p.SpeechStyle);
            }
            // Lo tratado hace poco con el jugador cuenta
            if (mem != null && mem.LastConversationDay >= 0 && CampaignTime.Now.ToDays - mem.LastConversationDay <= 15)
            {
                if (!string.IsNullOrWhiteSpace(mem.Summary)) sb.AppendLine($"Recent private dealings with {Hero.MainHero.Name}: {mem.Summary}");
                foreach (var n in mem.Notes.Skip(Math.Max(0, mem.Notes.Count - 3))) sb.AppendLine("- You remember: " + n);
            }
            return sb.ToString().TrimEnd();
        }

        private static string DescribeRealm(Kingdom k)
        {
            var sb = new StringBuilder();
            foreach (var c in k.Clans.Where(x => !x.IsEliminated).OrderByDescending(x => x.Tier).Take(12))
            {
                var fiefs = c.Settlements.Where(s => s.IsTown || s.IsCastle).Select(s => s.Name.ToString()).ToList();
                sb.AppendLine($"- Clan {c.Name}{(c == k.RulingClan ? " (YOUR clan, the ruling house)" : c.IsUnderMercenaryService ? " (mercenaries)" : "")}, led by {c.Leader?.Name}" +
                              $"{(c == Clan.PlayerClan ? " — the clan of " + Hero.MainHero.Name : "")}: {(fiefs.Count > 0 ? string.Join(", ", fiefs) : "no fiefs")}" +
                              (c != k.RulingClan && c.Leader != null ? $"; relation with you {(int)c.Leader.GetRelation(k.Leader)}" : ""));
            }
            return sb.ToString().TrimEnd();
        }

        private static string DescribeProposalsTo(Kingdom k)
        {
            double now = CampaignTime.Now.ToDays;
            var list = St.Proposals.Where(p => p.To == k.StringId).ToList();
            return list.Count == 0 ? "(none)" : string.Join("\n", list.Select(p =>
                $"- {WorldSim.FindKingdom(p.From)?.Name} [id: {p.From}] {(IsDemand(p.Type) ? "DEMANDS" : "PROPOSES")} {TypeEn(p.Type)}{Terms(p)}, {(int)(now - p.Day)} day(s) ago → answer with " +
                (p.Type == "peace" ? "accept_peace or reject_peace" : p.Type == "alliance" ? "accept_alliance or reject_alliance" : p.Type == "trade" ? "accept_trade or reject_trade"
                 : p.Type == "tribute" ? "accept_tribute or reject_tribute" : p.Type == "reparations" ? "accept_reparations or reject_reparations" : "transfer_territory or reject_territory")));
        }

        public static void MakeStatement(Kingdom k, WorldEvent about)
        {
            var cfg = S.Get;
            var ruler = k.Leader;
            var sys = new StringBuilder();
            sys.AppendLine(TStatement.Format("ruler", ruler.Name, "kingdom", k.Name, "language", cfg.Language, "min_len", cfg.StatementMinLength, "max_len", cfg.StatementMaxLength,
                "tax_min", cfg.KingdomTaxMinPercent, "tax_max", cfg.KingdomTaxMaxPercent, "max_stance", cfg.MaxStanceChange, "max_laws", cfg.MaxLawsPerKingdom));
            var lore = WorldLore.Text;
            if (!string.IsNullOrWhiteSpace(lore)) { sys.AppendLine(); sys.AppendLine("## The world"); sys.AppendLine(lore); }
            sys.AppendLine(); sys.AppendLine("## Who you are"); sys.AppendLine(DescribeRuler(ruler));
            sys.AppendLine(); sys.AppendLine($"## Your realm, {k.Name} [id: {k.StringId}] — clans and their fiefs"); sys.AppendLine(DescribeRealm(k));
            sys.AppendLine(); sys.AppendLine("## All kingdoms (technical truth, right now)"); sys.AppendLine(WorldSim.DescribeKingdoms(k));
            sys.AppendLine(); sys.AppendLine("## PENDING PROPOSALS DIRECTED AT YOU (answer them)"); sys.AppendLine(DescribeProposalsTo(k));
            var own = St.Proposals.Where(p => p.From == k.StringId).ToList();
            if (own.Count > 0)
            {
                sys.AppendLine(); sys.AppendLine("## YOUR PROPOSALS STILL UNANSWERED (do not repeat them)");
                foreach (var p in own) sys.AppendLine($"- to {p.To}: {TypeEn(p.Type)}{Terms(p)}");
            }
            sys.AppendLine(); sys.AppendLine("## What really happened lately"); sys.AppendLine(WorldSim.DescribeRecentNews(12));
            sys.AppendLine(); sys.AppendLine("## Developments in the air"); sys.AppendLine(WorldSim.DescribeActiveEvents(6));
            var prev = St.Statements.Where(s => s.KingdomId == k.StringId).OrderByDescending(s => s.Day).Take(3).ToList();
            if (prev.Count > 0)
            {
                sys.AppendLine(); sys.AppendLine("## Your previous statements (do not repeat yourself)");
                foreach (var s in prev) sys.AppendLine($"- {(int)(CampaignTime.Now.ToDays - s.Day)} day(s) ago: {s.SummaryEn}");
            }
            string task = about != null
                ? $"Speak out now about this development: {(string.IsNullOrWhiteSpace(about.SummaryEn) ? about.Title : about.SummaryEn)}"
                : "Proposals are waiting for your answer. Issue your statement now.";

            WorldSim.Ask(L.F("AIL_3b33d5b8", $"declaración de {ruler.Name}"), new List<ChatMessage> { new ChatMessage("system", sys.ToString()), new ChatMessage("user", task) }, 1100,
                j =>
                {
                    // Agenda consumida solo si la respuesta llega: si falla, el rey puede hablar otro día
                    St.LastStatementDay[k.StringId] = CampaignTime.Now.ToDays;
                    if (about != null) about.Engagement.Remove(k.StringId);   // ya ha hablado sobre esto
                    ApplyStatement(k, j, null);
                }, key: "statement:" + k.StringId);
        }

        // ============================================================ aplicar una declaración

        private static void ApplyStatement(Kingdom k, JObject j, string playerText)
        {
            if (k == null || k.IsEliminated || k.Leader == null) return;
            var cfg = S.Get;
            var s = new RulerStatement
            {
                Id = WorldSim.NewId("st"), Day = CampaignTime.Now.ToDays, KingdomId = k.StringId, RulerName = k.Leader.Name.ToString(),
                Text = playerText ?? WorldSim.Clean((string)j["statement"], 1600), SummaryEn = WorldSim.Clean((string)j["summary"], 350),
                Importance = Math.Max(1, Math.Min(4, (int?)j["importance"] ?? 2)),
            };
            if (s.Text.Length < 5) return;
            var thoughts = (string)j["thoughts"];
            if (!string.IsNullOrWhiteSpace(thoughts)) Log.Info($"[diplomacia] [piensa] {s.RulerName}: {thoughts}");

            if (j["actions"] is JArray acts)
                foreach (var a in acts.OfType<JObject>().Take(4))
                {
                    try
                    {
                        var r = Resolve(k, a);
                        if (r != null) { s.Results.Add(r); if (s.Importance < 3) s.Importance = 3; }
                    }
                    catch (Exception ex) { Log.Error($"[diplomacia] acción de {s.RulerName}", ex); }
                }

            if (j["relation_changes"] is JArray rc)
                foreach (var x in rc.OfType<JObject>())
                {
                    var o = WorldSim.FindKingdom((string)x["kingdom"]);
                    if (o == null || o == k) continue;
                    WorldSim.ChangeStance(k.StringId, o.StringId, Math.Max(-cfg.MaxStanceChange, Math.Min(cfg.MaxStanceChange, (int?)x["change"] ?? 0)));
                }

            St.Statements.Add(s);
            while (St.Statements.Count > 120) St.Statements.RemoveAt(0);

            var involved = new List<string> { k.StringId };
            var ev = new WorldEvent { Id = WorldSim.NewId("ev"), Day = s.Day, Type = "political", Source = "statement", Title = L.F("AIL_acbbf598", $"Declaración de {s.RulerName}"), Importance = s.Importance, SummaryEn = s.SummaryEn };
            if (j["kingdom_engagement"] is JObject eng)
                foreach (var p in eng.Properties())
                {
                    var o = WorldSim.FindKingdom(p.Name);
                    if (o == null || o == k || PlayerRules(o)) continue;
                    ev.Engagement[o.StringId] = Math.Max(0, Math.Min(100, (int?)p.Value ?? 0));
                    if (!involved.Contains(o.StringId)) involved.Add(o.StringId);
                }
            ev.Kingdoms = involved;
            St.Events.Add(ev);

            string body = s.Text + (s.Results.Count > 0 ? "\n\n" + string.Join("\n", s.Results.Select(x => "» " + x)) : "");
            NewsStore.Add(new NewsItem
            {
                Type = "statement", Importance = s.Importance, Text = $"{s.RulerName}, ruler of {k.Name}, declared: {s.SummaryEn}",
                TitleEs = L.F("AIL_96136d3a", $"Declaración de {s.RulerName} ({k.Name})"), TextEs = body, Kingdoms = involved, Heroes = new List<string> { k.Leader.StringId },
            });
            Log.Info($"[diplomacia] {s.RulerName}: {s.SummaryEn} | {string.Join(" | ", s.Results)}");
            WorldSim.Say(L.F("AIL_bd7dfc75", $"{s.RulerName}, de {k.Name}, ha hecho una declaración") + (s.Results.Count > 0 ? ": " + s.Results[0] : "."), s.Results.Count > 0 ? Colors.Yellow : Colors.Cyan);
        }

        private static int I(JObject a, string key, int min, int max)
        {
            int v = 0;
            try { var t = a[key]; if (t != null && t.Type != JTokenType.Null) int.TryParse(new string(t.ToString().Where(c => char.IsDigit(c) || c == '-').ToArray()), out v); } catch { }
            return Math.Max(min, Math.Min(max, v));
        }

        /// <summary>Valida y ejecuta una acción de un rey. Devuelve lo ocurrido (para enseñar) o null si no procede.</summary>
        private static string Resolve(Kingdom k, JObject a)
        {
            var cfg = S.Get;
            string type = ((string)a["type"] ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0 || type == "none") return null;
            var o = WorldSim.FindKingdom((string)a["target_kingdom"]);
            string kn = k.Name.ToString();
            string Bad(string why) { Log.Warn($"[diplomacia] {kn}: acción {type} ignorada ({why})"); return null; }

            switch (type)
            {
                // ---------------------------------------------------- guerra
                case "declare_war":
                    if (o == null || o == k) return Bad(L.T("AIL_8a207caf", "reino destino no válido"));
                    if (k.IsAtWarWith(o)) return Bad(L.T("AIL_acfa98a8", "ya están en guerra"));
                    if (PlayerRules(o) && !cfg.AllowAiWarOnPlayer) return Bad(L.T("AIL_b809052a", "guerra contra el jugador desactivada"));
                    EndAlliance(k, o); EndTrade(k, o);
                    St.Proposals.RemoveAll(p => (p.From == k.StringId && p.To == o.StringId) || (p.From == o.StringId && p.To == k.StringId));
                    DeclareWarAction.ApplyByDefault(k, o);
                    WorldSim.ChangeStance(o.StringId, k.StringId, -30);
                    return L.F("AIL_75b4abfe", $"{kn} declara la guerra a {o.Name}.");

                // ---------------------------------------------------- paz
                case "propose_peace":
                {
                    if (o == null || o == k || !k.IsAtWarWith(o)) return Bad(L.T("AIL_a305fa83", "no están en guerra"));
                    var p = AddProposal("peace", k, o);
                    p.DailyTribute = I(a, "daily_tribute", 0, cfg.MaxDailyTribute); p.TributeDays = p.DailyTribute > 0 ? I(a, "tribute_days", 10, 200) : 0;
                    p.Reparations = I(a, "reparations", 0, cfg.MaxReparations);
                    var st = OrderSystem.FindSettlement((string)a["settlement"], x => (x.IsTown || x.IsCastle) && x.MapFaction == o);
                    p.SettlementId = st?.StringId;
                    return L.F("AIL_cab1eaa4", $"{kn} propone la paz a {o.Name}{TermsEs(p)}.");
                }
                case "accept_peace":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("peace", o, k);
                    if (p == null) return Bad("no hay propuesta de paz de ese reino");
                    if (!k.IsAtWarWith(o)) { St.Proposals.Remove(p); return Bad(L.T("AIL_e904786b", "ya no están en guerra")); }
                    St.Proposals.Remove(p);
                    St.Proposals.RemoveAll(x => x.Type == "peace" && x.From == k.StringId && x.To == o.StringId);
                    // quien acepta paga lo que se le exigió
                    if (p.DailyTribute > 0) MakePeaceAction.ApplyByKingdomDecision(k, o, p.DailyTribute, p.TributeDays);
                    else MakePeaceAction.Apply(k, o);
                    var extra = new List<string>();
                    if (p.Reparations > 0) { int paid = Pay(k, o, p.Reparations); if (paid > 0) extra.Add(L.F("AIL_555e7801", $"{paid} denarios de reparaciones")); }
                    var st = string.IsNullOrEmpty(p.SettlementId) ? null : Settlement.Find(p.SettlementId);
                    if (st != null && st.MapFaction == k) { ChangeOwnerOfSettlementAction.ApplyByDefault(o.Leader, st); extra.Add(L.F("AIL_5c924813", $"la entrega de {st.Name}")); }
                    if (p.DailyTribute > 0) extra.Add(L.F("AIL_fbf8b45d", $"un tributo de {p.DailyTribute} denarios al día durante {p.TributeDays} días"));
                    WorldSim.ChangeStance(k.StringId, o.StringId, 10); WorldSim.ChangeStance(o.StringId, k.StringId, 10);
                    return L.F("AIL_64b1d2d4", $"{kn} y {o.Name} firman la paz") + (extra.Count > 0 ? $": {kn} acepta " + string.Join(", ", extra) : "") + ".";
                }
                case "reject_peace":
                    return Reject("peace", k, o, L.F("AIL_c1de8329", $"{kn} rechaza la paz que ofrecía {o?.Name}."));

                // ---------------------------------------------------- alianzas
                case "propose_alliance":
                    if (o == null || o == k || k.IsAtWarWith(o) || Allied(k, o)) return Bad("alianza no posible");
                    AddProposal("alliance", k, o);
                    return L.F("AIL_f1ae2294", $"{kn} propone una alianza a {o.Name}.");
                case "accept_alliance":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("alliance", o, k);
                    if (p == null) return Bad("no hay propuesta de alianza");
                    St.Proposals.Remove(p);
                    if (k.IsAtWarWith(o) || Alli == null) return Bad(L.T("AIL_1d0a19e2", "alianza imposible ahora"));
                    Alli.StartAlliance(o, k);
                    WorldSim.ChangeStance(k.StringId, o.StringId, 15); WorldSim.ChangeStance(o.StringId, k.StringId, 15);
                    return L.F("AIL_47c0f5a2", $"{kn} y {o.Name} sellan una alianza.");
                }
                case "reject_alliance":
                    return Reject("alliance", k, o, L.F("AIL_ba80efa7", $"{kn} rechaza la alianza que proponía {o?.Name}."));
                case "break_alliance":
                    if (o == null || !Allied(k, o)) return Bad("no son aliados");
                    EndAlliance(k, o);
                    WorldSim.ChangeStance(o.StringId, k.StringId, -20);
                    return L.F("AIL_9701c185", $"{kn} rompe su alianza con {o.Name}.");

                // ---------------------------------------------------- comercio
                case "propose_trade":
                {
                    if (o == null || o == k || k.IsAtWarWith(o) || HasTrade(k, o)) return Bad("acuerdo comercial no posible");
                    var p = AddProposal("trade", k, o);
                    p.TradeYears = I(a, "trade_years", 1, 5);
                    return L.F("AIL_6e09fc5c", $"{kn} propone a {o.Name} un acuerdo comercial de {p.TradeYears} año(s).");
                }
                case "accept_trade":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("trade", o, k);
                    if (p == null) return Bad("no hay propuesta comercial");
                    St.Proposals.Remove(p);
                    if (k.IsAtWarWith(o) || Trade == null) return Bad(L.T("AIL_6ee119bd", "comercio imposible ahora"));
                    Trade.MakeTradeAgreement(o, k, CampaignTime.YearsFromNow(Math.Max(1, p.TradeYears)));
                    WorldSim.ChangeStance(k.StringId, o.StringId, 5); WorldSim.ChangeStance(o.StringId, k.StringId, 5);
                    return L.F("AIL_4e5e61ea", $"{kn} y {o.Name} firman un acuerdo comercial de {Math.Max(1, p.TradeYears)} año(s).");
                }
                case "reject_trade":
                    return Reject("trade", k, o, L.F("AIL_dace2712", $"{kn} rechaza el acuerdo comercial de {o?.Name}."));
                case "end_trade":
                    if (o == null || !HasTrade(k, o)) return Bad("no hay acuerdo comercial");
                    EndTrade(k, o);
                    return L.F("AIL_270778c1", $"{kn} da por terminado su acuerdo comercial con {o.Name}.");

                // ---------------------------------------------------- tributo
                case "demand_tribute":
                {
                    if (o == null || o == k) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = AddProposal("tribute", k, o);
                    p.DailyTribute = I(a, "daily_tribute", 10, cfg.MaxDailyTribute); p.TributeDays = I(a, "tribute_days", 10, 200);
                    return L.F("AIL_c61b5727", $"{kn} exige a {o.Name} un tributo de {p.DailyTribute} denarios al día durante {p.TributeDays} días.");
                }
                case "accept_tribute":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("tribute", o, k);
                    if (p == null) return Bad("no hay exigencia de tributo");
                    St.Proposals.Remove(p);
                    St.Tributes.RemoveAll(t => t.Payer == k.StringId && t.Receiver == o.StringId);
                    St.Tributes.Add(new Tribute { Payer = k.StringId, Receiver = o.StringId, Daily = p.DailyTribute, UntilDay = CampaignTime.Now.ToDays + p.TributeDays });
                    return L.F("AIL_aa38cc8d", $"{kn} acepta pagar a {o.Name} {p.DailyTribute} denarios al día durante {p.TributeDays} días.");
                }
                case "reject_tribute":
                    return Reject("tribute", k, o, L.F("AIL_6458f7e4", $"{kn} se niega a pagar tributo a {o?.Name}."));
                case "end_tribute":
                    if (o == null || St.Tributes.RemoveAll(t => (t.Payer == k.StringId && t.Receiver == o.StringId) || (t.Payer == o.StringId && t.Receiver == k.StringId)) == 0) return Bad("no hay tributo");
                    return L.F("AIL_a7a5c567", $"Se acaba el tributo entre {kn} y {o.Name}.");

                // ---------------------------------------------------- reparaciones
                case "demand_reparations":
                {
                    if (o == null || o == k) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = AddProposal("reparations", k, o);
                    p.Reparations = I(a, "reparations", 100, cfg.MaxReparations);
                    return L.F("AIL_cb234369", $"{kn} exige a {o.Name} {p.Reparations} denarios en reparaciones.");
                }
                case "accept_reparations":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("reparations", o, k);
                    if (p == null) return Bad("no hay exigencia de reparaciones");
                    St.Proposals.Remove(p);
                    int paid = Pay(k, o, p.Reparations);
                    return L.F("AIL_f12fa594", $"{kn} paga a {o.Name} {paid} denarios en reparaciones.");
                }
                case "reject_reparations":
                    return Reject("reparations", k, o, L.F("AIL_0b583a3b", $"{kn} se niega a pagar reparaciones a {o?.Name}."));

                // ---------------------------------------------------- territorio
                case "demand_territory":
                {
                    if (o == null || o == k) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var st = OrderSystem.FindSettlement((string)a["settlement"], x => (x.IsTown || x.IsCastle) && x.MapFaction == o);
                    if (st == null) return Bad(L.T("AIL_9b65eab4", "asentamiento no válido"));
                    var p = AddProposal("territory", k, o);
                    p.SettlementId = st.StringId;
                    return L.F("AIL_3cfa625f", $"{kn} exige a {o.Name} la entrega de {st.Name}.");
                }
                case "transfer_territory":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("territory", o, k);
                    var st = p != null ? Settlement.Find(p.SettlementId) : OrderSystem.FindSettlement((string)a["settlement"], x => (x.IsTown || x.IsCastle) && x.MapFaction == k);
                    if (p != null) St.Proposals.Remove(p);
                    if (st == null || st.MapFaction != k || st.IsUnderSiege) return Bad(L.T("AIL_9b65eab4", "asentamiento no válido"));
                    ChangeOwnerOfSettlementAction.ApplyByDefault(o.Leader, st);
                    return L.F("AIL_e50ca8f9", $"{kn} entrega {st.Name} a {o.Name}.");
                }
                case "reject_territory":
                    return Reject("territory", k, o, L.F("AIL_0d2e0018", $"{kn} se niega a ceder territorio a {o?.Name}."));

                // ---------------------------------------------------- política interna
                case "expel_clan":
                {
                    var clan = FindClan(k, (string)a["clan"]);
                    if (clan == null || clan == k.RulingClan || clan.IsUnderMercenaryService) return Bad(L.T("AIL_ae4ebcf0", "clan no válido"));
                    if (clan == Clan.PlayerClan && !cfg.AllowAiExpelPlayer) return Bad(L.T("AIL_007c510b", "expulsar al jugador desactivado"));
                    ChangeKingdomAction.ApplyByLeaveKingdom(clan);
                    return L.F("AIL_0d7f7775", $"{kn} expulsa del reino al clan {clan.Name}.");
                }
                case "grant_fief":
                {
                    var clan = FindClan(k, (string)a["clan"]);
                    var st = OrderSystem.FindSettlement((string)a["settlement"], x => (x.IsTown || x.IsCastle) && x.OwnerClan == k.RulingClan && !x.IsUnderSiege);
                    if (clan == null || clan == k.RulingClan || clan.IsUnderMercenaryService || clan.Leader == null || st == null) return Bad(L.T("AIL_46e19d91", "feudo o clan no válidos"));
                    if (k.RulingClan.Settlements.Count(x => x.IsTown || x.IsCastle) <= 1) return Bad(L.T("AIL_44dd0aa7", "es el último feudo de la corona"));
                    ChangeOwnerOfSettlementAction.ApplyByGift(st, clan.Leader);
                    return L.F("AIL_9b15365c", $"{kn} concede {st.Name} al clan {clan.Name}.");
                }
                case "confiscate_fief":
                {
                    var st = OrderSystem.FindSettlement((string)a["settlement"], x => (x.IsTown || x.IsCastle) && x.MapFaction == k && x.OwnerClan != k.RulingClan && !x.IsUnderSiege);
                    if (st == null) return Bad(L.T("AIL_9b65eab4", "asentamiento no válido"));
                    if (st.OwnerClan == Clan.PlayerClan && !cfg.AllowAiExpelPlayer) return Bad(L.T("AIL_9bd88abd", "confiscar al jugador desactivado"));
                    var old = st.OwnerClan;
                    ChangeOwnerOfSettlementAction.ApplyByDefault(k.Leader, st);
                    if (old?.Leader != null && old.Leader != Hero.MainHero) ChangeRelationAction.ApplyRelationChangeBetweenHeroes(k.Leader, old.Leader, -20, false);
                    return L.F("AIL_3d1f0c15", $"{kn} confisca {st.Name} al clan {old?.Name}.");
                }
                case "enact_law":
                {
                    if (!cfg.EnableKingdomLaws) return Bad("leyes desactivadas");
                    string name = WorldSim.Clean((string)a["law_name"], 60), text = WorldSim.Clean((string)a["law_text"], 500);
                    if (name.Length < 3) return Bad(L.T("AIL_f48c59a0", "ley sin nombre"));
                    if (St.Laws.Count(l => l.KingdomId == k.StringId) >= cfg.MaxLawsPerKingdom) return Bad(L.T("AIL_8a3f11e5", "demasiadas leyes en vigor"));
                    var fx = a["law_effects"] as JObject ?? new JObject();
                    float F(string key, float max) { float v = 0f; try { v = (float?)fx[key] ?? 0f; } catch { } return Math.Max(-max, Math.Min(max, v)); }
                    St.Laws.RemoveAll(l => l.KingdomId == k.StringId && WorldSim.Norm(l.Name) == WorldSim.Norm(name));
                    var law = new KingdomLaw
                    {
                        Id = WorldSim.NewId("law"), KingdomId = k.StringId, Name = name, Text = text, EnactedDay = CampaignTime.Now.ToDays, AuthorName = k.Leader.Name.ToString(),
                        LoyaltyPerDay = F("loyalty", 1f), ProsperityPerDay = F("prosperity", 1.5f), SecurityPerDay = F("security", 1f), FoodPerDay = F("food", 3f),
                    };
                    St.Laws.Add(law);
                    var parts = new List<string>();
                    if (law.LoyaltyPerDay != 0) parts.Add($"lealtad {law.LoyaltyPerDay:+0.#;-0.#}"); if (law.ProsperityPerDay != 0) parts.Add($"prosperidad {law.ProsperityPerDay:+0.#;-0.#}");
                    if (law.SecurityPerDay != 0) parts.Add($"seguridad {law.SecurityPerDay:+0.#;-0.#}"); if (law.FoodPerDay != 0) parts.Add($"comida {law.FoodPerDay:+0.#;-0.#}");
                    return L.F("AIL_203a7d74", $"{kn} promulga la ley «{name}»") + (parts.Count > 0 ? L.F("AIL_782deaf5", $" (al día en sus ciudades y castillos: {string.Join(", ", parts)})") : "") + ".";
                }
                case "repeal_law":
                {
                    string n = WorldSim.Norm((string)a["law_name"]);
                    var law = St.Laws.FirstOrDefault(l => l.KingdomId == k.StringId && (WorldSim.Norm(l.Name) == n || (n.Length > 2 && (WorldSim.Norm(l.Name).Contains(n) || n.Contains(WorldSim.Norm(l.Name))))));
                    if (law == null) return Bad("ley no encontrada");
                    St.Laws.Remove(law);
                    return L.F("AIL_59559c9d", $"{kn} deroga la ley «{law.Name}».");
                }
                case "declare_war_on_clan":
                case "make_peace_with_clan":
                {
                    string n = WorldSim.Norm((string)a["clan"]);
                    if (n.Length < 2) return Bad(L.T("AIL_ef08b937", "sin clan"));
                    var clan = Clan.All.FirstOrDefault(c => c != null && !c.IsEliminated && c.Kingdom == null && !c.IsBanditFaction && c.Leader != null
                                                             && (WorldSim.Norm(c.Name.ToString()) == n || WorldSim.Norm(c.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(c.Name.ToString()))));
                    if (clan == null) return Bad("clan independiente no encontrado");
                    if (clan == Clan.PlayerClan && !cfg.AllowAiWarOnPlayer) return Bad(L.T("AIL_b809052a", "guerra contra el jugador desactivada"));
                    if (type == "declare_war_on_clan")
                    {
                        if (k.IsAtWarWith(clan)) return Bad(L.T("AIL_acfa98a8", "ya están en guerra"));
                        DeclareWarAction.ApplyByDefault(k, clan);
                        return L.F("AIL_30aa9ee2", $"{kn} declara la guerra al clan {clan.Name}.");
                    }
                    if (!k.IsAtWarWith(clan)) return Bad(L.T("AIL_a305fa83", "no están en guerra"));
                    MakePeaceAction.Apply(k, clan);
                    return L.F("AIL_83ce96ea", $"{kn} hace las paces con el clan {clan.Name}.");
                }
                case "quarantine_settlement":
                case "lift_quarantine":
                {
                    var st = OrderSystem.FindSettlement((string)a["settlement"], x => x.MapFaction == k && Diseases.At(x) != null);
                    if (st == null) return Bad(L.T("AIL_1b485942", "asentamiento sin enfermedad o ajeno"));
                    return type == "lift_quarantine" ? Diseases.Lift(st, kn) : Diseases.Quarantine(st, I(a, "quarantine_days", 5, 60), kn);
                }
                case "set_tax":
                {
                    int pct = I(a, "tax_percent", cfg.KingdomTaxMinPercent, cfg.KingdomTaxMaxPercent);
                    if (pct == 0) { St.KingdomTaxPercent.Remove(k.StringId); return L.F("AIL_8441426c", $"{kn} retira el impuesto extraordinario de la corona."); }
                    St.KingdomTaxPercent[k.StringId] = pct;
                    return pct > 0 ? L.F("AIL_07bd9260", $"{kn} impone un impuesto extraordinario del {pct} % en sus ciudades.") : L.F("AIL_63e55204", $"{kn} rebaja los impuestos de sus ciudades un {-pct} %.");
                }
            }
            return Bad(L.T("AIL_0a156f01", "acción desconocida"));
        }

        /// <summary>Un evento dice que un clan se alza contra su corona: se ejecuta solo si de verdad hay ruptura.</summary>
        public static void Rebellion(string clanName)
        {
            try
            {
                var cfg = S.Get;
                if (!Enabled || !cfg.AllowClanRebellions) return;
                string n = WorldSim.Norm(clanName);
                if (n.Length < 2) return;
                var clan = Clan.All.FirstOrDefault(c => c != null && !c.IsEliminated && c.Kingdom != null && c.Leader != null
                                                         && (WorldSim.Norm(c.Name.ToString()) == n || WorldSim.Norm(c.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(c.Name.ToString()))));
                if (clan == null || clan == Clan.PlayerClan || clan == clan.Kingdom.RulingClan || clan.IsUnderMercenaryService || clan.IsMinorFaction) return;
                var k = clan.Kingdom;
                int rel = clan.Leader.GetRelation(k.Leader);
                if (rel > cfg.RebellionMaxRelation) { Log.Info($"[diplomacia] rebelión de {clan.Name} descartada: su relación con {k.Leader.Name} es {rel}"); return; }
                ChangeKingdomAction.ApplyByLeaveWithRebellionAgainstKingdom(clan);
                Log.Info($"[diplomacia] el clan {clan.Name} se alza contra {k.Name}");
                WorldSim.Say(L.F("AIL_d6fd303c", $"El clan {clan.Name} se alza en rebelión contra {k.Name}."), Colors.Red);
            }
            catch (Exception ex) { Log.Error("Rebelión de clan", ex); }
        }

        private static string Reject(string type, Kingdom k, Kingdom o, string text)
        {
            if (o == null) return null;
            var p = Pending(type, o, k);
            if (p == null) return null;
            St.Proposals.Remove(p);
            WorldSim.ChangeStance(o.StringId, k.StringId, -5);
            return text;
        }

        private static int Pay(Kingdom from, Kingdom to, int amount)
        {
            int paid = Math.Max(0, Math.Min(amount, from.Leader.Gold));
            if (paid > 0) GiveGoldAction.ApplyBetweenCharacters(from.Leader, to.Leader, paid, true);
            return paid;
        }

        private static void EndAlliance(Kingdom a, Kingdom b) { try { if (Allied(a, b)) Alli.EndAlliance(a, b); } catch (Exception ex) { Log.Error("Fin de alianza", ex); } }
        private static void EndTrade(Kingdom a, Kingdom b) { try { if (HasTrade(a, b)) Trade.EndTradeAgreement(a, b); } catch (Exception ex) { Log.Error("Fin de comercio", ex); } }

        private static Clan FindClan(Kingdom k, string name)
        {
            string n = WorldSim.Norm(name);
            if (n.Length < 2) return null;
            return k.Clans.FirstOrDefault(c => !c.IsEliminated && (c.StringId == name || WorldSim.Norm(c.Name.ToString()) == n))
                   ?? k.Clans.FirstOrDefault(c => !c.IsEliminated && (WorldSim.Norm(c.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(c.Name.ToString()))));
        }

        // ============================================================ el jugador como rey

        /// <summary>Otra corona te propone o te exige algo: decides con una ventana.</summary>
        private static void AskPlayer(Proposal p)
        {
            var from = WorldSim.FindKingdom(p.From); var mine = PlayerRealm;
            if (from == null || mine == null) return;
            string verb = IsDemand(p.Type) ? L.T("AIL_3958d81a", "te exige") : L.T("AIL_be07e5b5", "te propone");
            string accept = p.Type == "peace" ? "accept_peace" : p.Type == "alliance" ? "accept_alliance" : p.Type == "trade" ? "accept_trade" : p.Type == "tribute" ? "accept_tribute"
                          : p.Type == "reparations" ? "accept_reparations" : "transfer_territory";
            string reject = p.Type == "peace" ? "reject_peace" : p.Type == "alliance" ? "reject_alliance" : p.Type == "trade" ? "reject_trade" : p.Type == "tribute" ? "reject_tribute"
                          : p.Type == "reparations" ? "reject_reparations" : "reject_territory";
            void Answer(string type)
            {
                MainThread.Post(() =>
                {
                    try
                    {
                        var r = Resolve(mine, new JObject { ["type"] = type, ["target_kingdom"] = from.StringId });
                        if (r == null) return;
                        WorldSim.Say(r, Colors.Yellow);
                        NewsStore.Add(new NewsItem
                        {
                            Type = "statement", Importance = 3, Text = $"{Hero.MainHero.Name}, ruler of {mine.Name}, answered {from.Name}: {type.Replace('_', ' ')}.",
                            TitleEs = L.F("AIL_3f2c88e6", $"Respuesta de {mine.Name} a {from.Name}"), TextEs = r, Kingdoms = new List<string> { mine.StringId, from.StringId },
                        });
                        SaveFiles.Flush(heroes: false);
                    }
                    catch (Exception ex) { Log.Error("Respuesta del jugador a una propuesta", ex); }
                });
            }
            InformationManager.ShowInquiry(new InquiryData(L.F("AIL_f85f4507", $"Mensaje de {from.Leader.Name}, de {from.Name}"),
                L.F("AIL_ab129b00", $"{from.Name} {verb} {TypeEs(p.Type)}{TermsEs(p)}.\n\nSi no contestas ahora, la propuesta sigue en pie {S.Get.ProposalLifetimeDays} días y puedes responder con una declaración."),
                true, true, "Aceptar", "Rechazar", () => Answer(accept), () => Answer(reject)), true);
        }

        private static readonly Tpl TPlayerStatement = new Tpl("kingdom_statement/player_statement_analysis.txt",
@"{ruler}, ruler of {kingdom} [id: {kingdom_id}], has issued the public statement below. You are the chancellor: turn the words into the actions they really contain, nothing more.
- Use only what the statement clearly says. If it only speaks and decides nothing, return no actions.
- The same action list and fields as any sovereign: declare_war, propose_peace (with optional daily_tribute, tribute_days, reparations, settlement), accept_peace, reject_peace, propose_alliance, accept_alliance, reject_alliance, break_alliance, propose_trade (trade_years), accept_trade, reject_trade, end_trade, demand_tribute (daily_tribute, tribute_days), accept_tribute, reject_tribute, end_tribute, demand_reparations (reparations), accept_reparations, reject_reparations, demand_territory (settlement), transfer_territory, reject_territory, expel_clan (clan), grant_fief (settlement, clan), confiscate_fief (settlement), set_tax (tax_percent), enact_law (law_name, law_text, law_effects { loyalty -1..1, prosperity -1.5..1.5, security -1..1, food -3..3 }), repeal_law (law_name), declare_war_on_clan (clan), make_peace_with_clan (clan), quarantine_settlement (settlement, quarantine_days), lift_quarantine (settlement).
- target_kingdom must be a kingdom id from the data.
Answer ONLY with JSON: { ""summary"": ""what was said and decided, ONE English sentence"", ""importance"": 1-4, ""actions"": [ { ""type"": ""..."", ""target_kingdom"": ""id"", ""settlement"": ""name"", ""clan"": ""name"", ""daily_tribute"": 0, ""tribute_days"": 0, ""reparations"": 0, ""trade_years"": 0, ""tax_percent"": 0 } ], ""kingdom_engagement"": { ""kingdom_id"": 0-100 } }");

        public static void Touch() { var _ = TStatement.Key + TPlayerStatement.Key; }

        public static bool PlayerCanDeclare => Enabled && PlayerRules(PlayerRealm);

        /// <summary>Declaración diplomática del jugador (solo si reina): escribe libremente y la IA la traduce a acciones.</summary>
        public static void PlayerStatement()
        {
            var k = PlayerRealm;
            if (!PlayerCanDeclare) { WorldSim.Say(L.T("AIL_76f7a56d", "Solo quien reina puede hacer una declaración."), Colors.Red); return; }
            InformationManager.ShowTextInquiry(new TextInquiryData(L.T("AIL_fec92cb7", "Declaración de la corona"),
                L.T("AIL_f00983f3", "Escribe lo que proclamas ante Calradia. Lo que decidas con claridad (guerra, paz, alianza, tributo, impuestos, feudos…) se llevará a cabo; lo demás quedará como palabras."),
                true, true, "Proclamar", "Cancelar", text =>
                {
                    text = (text ?? "").Trim();
                    if (text.Length < 10) return;
                    var sys = new StringBuilder();
                    sys.AppendLine(TPlayerStatement.Format("ruler", Hero.MainHero.Name, "kingdom", k.Name, "kingdom_id", k.StringId));
                    sys.AppendLine(); sys.AppendLine($"## The realm of {k.Name}: clans and fiefs"); sys.AppendLine(DescribeRealm(k));
                    sys.AppendLine(); sys.AppendLine("## All kingdoms (technical truth)"); sys.AppendLine(WorldSim.DescribeKingdoms());
                    sys.AppendLine(); sys.AppendLine("## Pending proposals directed at this realm"); sys.AppendLine(DescribeProposalsTo(k));
                    WorldSim.Say(L.T("AIL_ebf54145", "Tus heraldos llevan la declaración a todos los rincones…"), Colors.Cyan);
                    // Importante: si hay otra petición en curso espera en cola (no se descarta) y se reintenta una vez si falla
                    WorldSim.Ask(L.T("AIL_100794a8", "declaración del jugador"), new List<ChatMessage> { new ChatMessage("system", sys.ToString()), new ChatMessage("user", text) }, 700,
                        j => ApplyStatement(k, j, WorldSim.Clean(text, 1600)), important: true);
                }, null, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 10, L.T("AIL_49569563", "Escribe algo más"))), true);
        }
    }
}
