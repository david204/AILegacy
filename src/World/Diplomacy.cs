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
    /// AI diplomacy between rulers: public statements backed by real actions (war, peace with terms, alliances, trade,
    /// tribute, reparations, territory, fiefs, banishment, taxes), pending proposals between crowns, and the player's own
    /// statements when they rule. A ruler's words change nothing unless an action comes with them.
    /// </summary>
    internal static class Diplomacy
    {
        private static readonly Random Rng = new Random();
        public static bool Enabled => S.Get.EnableDiplomacy && WorldSim.Enabled;

        // Depth of our own war and peace calls. The patch that stops the game and other mods from changing wars
        // between AI kingdoms behind our back lets these through.
        [ThreadStatic] private static int _ours;
        public static bool IsOurs => _ours > 0;
        public static void Ours(Action act)
        {
            _ours++;
            try { act(); } finally { _ours--; }
        }
        private static WorldState St => WorldSim.State;
        private static Kingdom PlayerRealm => Hero.MainHero?.MapFaction as Kingdom;
        private static bool PlayerRules(Kingdom k) => k != null && k.Leader == Hero.MainHero;

        // ============================================================ alliances and trade (the game's own)

        private static IAllianceCampaignBehavior Alli { get { try { return Campaign.Current?.GetCampaignBehavior<IAllianceCampaignBehavior>(); } catch { return null; } } }
        private static ITradeAgreementsCampaignBehavior Trade { get { try { return Campaign.Current?.GetCampaignBehavior<ITradeAgreementsCampaignBehavior>(); } catch { return null; } } }

        /// <summary>
        /// A kingdom the game's pacts can be asked about and made with: it exists and someone rules it. The game's
        /// own alliance code reads the ruler and the ruling clan of both sides without checking them.
        /// </summary>
        private static bool Usable(Kingdom k) => k != null && !k.IsEliminated && k.Leader != null && k.RulingClan != null;

        public static bool Allied(Kingdom a, Kingdom b) { try { return Usable(a) && Usable(b) && Alli != null && Alli.IsAllyWithKingdom(a, b); } catch { return false; } }
        public static bool HasTrade(Kingdom a, Kingdom b) { try { return Usable(a) && Usable(b) && Trade != null && Trade.HasTradeAgreement(a, b, out _); } catch { return false; } }

        private static readonly HashSet<string> PactListsUnread = new HashSet<string>();

        /// <summary>
        /// The pacts the game keeps in one of its private lists: each entry with its two kingdoms. null when the
        /// list is not there or its entries are not what we expect (another game version, another mod's pacts):
        /// nothing is guessed from an entry we cannot read, the caller goes by the public checks instead, and it
        /// is logged once per list.
        /// </summary>
        private static List<Tuple<object, Kingdom, Kingdom>> PactEntries(object behavior, string field, out System.Collections.IList list)
        {
            list = null;
            if (behavior == null) return null;
            const System.Reflection.BindingFlags any = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            string why = null;
            try
            {
                list = behavior.GetType().GetField(field, any)?.GetValue(behavior) as System.Collections.IList;
                if (list == null) why = "no existe o no es una lista";
                else
                {
                    var found = new List<Tuple<object, Kingdom, Kingdom>>();
                    foreach (object item in list)
                    {
                        if (item == null) continue;
                        var t = item.GetType();
                        var f1 = t.GetField("Kingdom1", any); var f2 = t.GetField("Kingdom2", any);
                        // a field of another type would read as "no kingdom" and the entry would be taken for a broken one
                        if (f1 == null || f2 == null || !typeof(Kingdom).IsAssignableFrom(f1.FieldType) || !typeof(Kingdom).IsAssignableFrom(f2.FieldType))
                        {
                            why = $"sus entradas ({t.Name}) no tienen Kingdom1 y Kingdom2 de tipo Kingdom";
                            break;
                        }
                        found.Add(Tuple.Create(item, f1.GetValue(item) as Kingdom, f2.GetValue(item) as Kingdom));
                    }
                    if (why == null) return found;
                }
            }
            catch (Exception ex) { why = ex.GetType().Name + ": " + ex.Message; }
            list = null;
            if (PactListsUnread.Add(field))
                Log.Warn($"[diplomacia] la lista {field} del juego no se puede leer ({why}): los pactos con reinos destruidos se buscan par a par con las consultas públicas");
            return null;
        }

        /// <summary>
        /// Every pair of kingdoms the game knows where at least one is destroyed or has no ruler: the pairs a pact
        /// must not exist between. For when the game's own list cannot be read.
        /// </summary>
        private static IEnumerable<KeyValuePair<Kingdom, Kingdom>> BrokenPairs()
        {
            var all = Campaign.Current.Kingdoms.Where(x => x != null).ToList();
            for (int i = 0; i < all.Count; i++)
                for (int j = i + 1; j < all.Count; j++)
                    if (!Usable(all[i]) || !Usable(all[j])) yield return new KeyValuePair<Kingdom, Kingdom>(all[i], all[j]);
        }

        /// <summary>
        /// Ends every alliance and trade agreement the game still holds with a kingdom that is destroyed or has no
        /// ruler. The game ends them itself when a kingdom falls, but a save can carry one, and its daily renewal of
        /// an alliance reads the ruler of both sides. Run on load, once a day and when a kingdom is destroyed.
        /// </summary>
        public static void CleanPacts()
        {
            try
            {
                if (Campaign.Current == null) return;
                int ended = 0;
                var alli = Alli;
                var alliances = PactEntries(alli, "_alliances", out var allianceList);
                if (alliances != null)
                    foreach (var e in alliances.Where(x => !Usable(x.Item2) || !Usable(x.Item3)))
                    {
                        // the game's own call needs both kingdoms; an entry that lost one is taken out by hand
                        if (e.Item2 != null && e.Item3 != null) alli.EndAlliance(e.Item2, e.Item3);
                        else allianceList.Remove(e.Item1);
                        ended++;
                    }
                else if (alli != null)
                {
                    // the list could not be read (another version or another mod's alliances): the ones the kingdoms themselves list...
                    foreach (var k in Campaign.Current.Kingdoms.Where(x => x != null && !x.IsEliminated).ToList())
                        foreach (var o in k.AlliedKingdoms.ToList())
                            if (o != null && (!Usable(k) || !Usable(o))) { alli.EndAlliance(k, o); ended++; }
                    // ...and whatever the game still answers yes to, pair by pair
                    foreach (var p in BrokenPairs().ToList())
                    {
                        bool allied = false;
                        try { allied = alli.IsAllyWithKingdom(p.Key, p.Value); } catch { }
                        if (!allied) continue;
                        try { alli.EndAlliance(p.Key, p.Value); ended++; }
                        catch (Exception ex) { Log.Error($"[diplomacia] terminando la alianza entre {p.Key.StringId} y {p.Value.StringId}", ex); }
                    }
                }
                var trade = Trade;
                var pacts = PactEntries(trade, "_tradeAgreements", out var tradeList);
                if (pacts != null)
                    foreach (var e in pacts.Where(x => !Usable(x.Item2) || !Usable(x.Item3)))
                    {
                        if (e.Item2 != null && e.Item3 != null) trade.EndTradeAgreement(e.Item2, e.Item3);
                        else tradeList.Remove(e.Item1);
                        ended++;
                    }
                else if (trade != null)
                    foreach (var p in BrokenPairs().ToList())
                    {
                        bool agreed = false;
                        try { agreed = trade.HasTradeAgreement(p.Key, p.Value, out _); } catch { }
                        if (!agreed) continue;
                        try { trade.EndTradeAgreement(p.Key, p.Value); ended++; }
                        catch (Exception ex) { Log.Error($"[diplomacia] terminando el acuerdo comercial entre {p.Key.StringId} y {p.Value.StringId}", ex); }
                    }
                if (ended > 0) Log.Info($"[diplomacia] {ended} pacto(s) con reinos destruidos o sin soberano, terminados");
            }
            catch (Exception ex) { Log.Error("[diplomacia] limpiando pactos de reinos que ya no están", ex); }
        }

        private static readonly Tpl TAllianceBrokenByWar = new Tpl("world/alliance_broken_by_war.txt",
            "{a} and {b} are at war with each other: the alliance that bound them is broken.");
        private static readonly Tpl TTradeBrokenByWar = new Tpl("world/trade_broken_by_war.txt",
            "{a} and {b} are at war with each other: their trade agreement is void.");

        /// <summary>
        /// Two kingdoms at war cannot stay allied or keep trading. The game ends both pacts when a war is declared;
        /// this catches whatever slipped past it (another mod, a save from before).
        /// </summary>
        private static void EndPactsAtWar()
        {
            var ks = WorldSim.Kingdoms.ToList();
            for (int i = 0; i < ks.Count; i++)
                for (int j = i + 1; j < ks.Count; j++)
                {
                    Kingdom a = ks[i], b = ks[j];
                    if (!a.IsAtWarWith(b)) continue;
                    var heroes = new[] { a.Leader, b.Leader }.Where(h => h != null).Select(h => h.StringId).ToList();
                    if (Allied(a, b))
                    {
                        EndAlliance(a, b);
                        if (!Allied(a, b))
                        {
                            Log.Info($"[diplomacia] {a.Name} y {b.Name} están en guerra: su alianza queda rota");
                            NewsStore.Add(new NewsItem
                            {
                                Type = "alliance", Importance = 3, Kingdoms = new List<string> { a.StringId, b.StringId }, Heroes = heroes,
                                Text = TAllianceBrokenByWar.Format("a", a.Name, "b", b.Name),
                                TitleEs = L.F("AIL_e7b1c790", $"Se rompe la alianza entre {a.Name} y {b.Name}"),
                                TextEs = L.F("AIL_e7b1c791", $"{a.Name} y {b.Name} están en guerra: la alianza que los unía queda rota."),
                            });
                        }
                    }
                    if (HasTrade(a, b))
                    {
                        EndTrade(a, b);
                        if (!HasTrade(a, b))
                        {
                            Log.Info($"[diplomacia] {a.Name} y {b.Name} están en guerra: su acuerdo comercial queda sin efecto");
                            NewsStore.Add(new NewsItem
                            {
                                Type = "custom", Importance = 2, Kingdoms = new List<string> { a.StringId, b.StringId }, Heroes = heroes,
                                Text = TTradeBrokenByWar.Format("a", a.Name, "b", b.Name),
                                TitleEs = L.F("AIL_e7b1c792", $"Se rompe el acuerdo comercial entre {a.Name} y {b.Name}"),
                                TextEs = L.F("AIL_e7b1c793", $"{a.Name} y {b.Name} están en guerra: su acuerdo comercial queda sin efecto."),
                            });
                        }
                    }
                }
        }

        // ============================================================ a ruler held prisoner

        private static readonly Tpl TRulerCaptive = new Tpl("world/ruler_captive.txt",
            " (a captive of {captor} right now: this ruler issues no statements of their own and can only answer a peace offered to them)");
        private static readonly Tpl TRulerCaptiveUnknown = new Tpl("world/ruler_captive_unknown.txt",
            " (a captive right now: this ruler issues no statements of their own and can only answer a peace offered to them)");
        private static readonly Tpl TTaskCaptive = new Tpl("kingdom_statement/task_captive.txt",
            "You are a prisoner{captor} and cannot rule freely. From your cell you may only answer the peace proposals addressed to you: accept_peace, reject_peace, or propose_peace with other terms. Any other action is thrown away. Issue your statement now.");
        private static readonly Tpl TTaskCaptiveOf = new Tpl("kingdom_statement/task_captive_of.txt", " of {captor}");

        /// <summary>An AI ruler who is somebody's prisoner: he takes no turn of his own and only answers a peace offered to him.</summary>
        public static bool RulerCaptive(Kingdom k) => k?.Leader != null && k.Leader.IsPrisoner && !PlayerRules(k);

        /// <summary>Who holds this hero: "Derthert (Vlandia)", or the town or castle whose dungeon it is. null when the game does not say.</summary>
        private static string Captor(Hero h)
        {
            var p = h?.PartyBelongedToAsPrisoner;
            if (p == null) return null;
            string who = p.LeaderHero?.Name?.ToString() ?? p.Settlement?.Name?.ToString() ?? p.Name?.ToString();
            string of = p.MapFaction?.Name?.ToString();
            if (string.IsNullOrEmpty(who)) return string.IsNullOrEmpty(of) ? null : of;
            return string.IsNullOrEmpty(of) || of == who ? who : $"{who} ({of})";
        }

        /// <summary>Said next to the name of a captive ruler wherever the kingdoms are listed, so those who deal with his realm know.</summary>
        public static string CaptiveNote(Kingdom k)
        {
            try
            {
                if (!RulerCaptive(k)) return "";
                string captor = Captor(k.Leader);
                return captor == null ? TRulerCaptiveUnknown.Text : TRulerCaptive.Format("captor", captor);
            }
            catch { return ""; }
        }

        // ============================================================ a kingdom that is gone

        public static void OnKingdomDestroyed(Kingdom k)
        {
            if (Campaign.Current == null || St == null) return;
            CleanPacts();
            try { Heal(); PruneGone(k?.StringId); }
            catch (Exception ex) { Log.Error("[diplomacia] reino destruido", ex); }
            try { WarTolls.OnKingdomGone(k?.StringId); }
            catch (Exception ex) { Log.Error("[diplomacia] bajas de un reino destruido", ex); }
        }

        /// <summary>
        /// What a destroyed kingdom leaves in our own state: proposals, turns to speak, weariness, tributes, stances.
        /// <paramref name="gone"/> is the kingdom just destroyed: it is pruned by its id even if the kingdom list is
        /// empty at that moment, or still counts it. Everything else is pruned against the list, when there is one.
        /// </summary>
        private static void PruneGone(string gone = null)
        {
            var alive = ExistingKingdomIds();
            if (!string.IsNullOrEmpty(gone))
            {
                if (alive.Count > 0) alive.Remove(gone);
                else
                {
                    // no list to go by: only what names the destroyed kingdom is taken out
                    Func<string, bool> pair = x => { var ids = (x ?? "").Split('|'); return ids.Length == 2 && (ids[0] == gone || ids[1] == gone); };
                    int n = St.Proposals.RemoveAll(p => p.From == gone || p.To == gone);
                    n += St.StatementQueue.RemoveAll(q => q.KingdomId == gone);
                    n += St.Tributes.RemoveAll(t => t.Payer == gone || t.Receiver == gone);
                    foreach (var e in St.Events) e.Engagement.Remove(gone);
                    St.WarFatigue.Remove(gone);
                    St.LastStatementDay.Remove(gone);
                    foreach (var key in St.Stance.Keys.Where(pair).ToList()) St.Stance.Remove(key);
                    if (n > 0) Log.Info($"[diplomacia] {n} propuesta(s), turno(s) o tributo(s) del reino destruido {gone}, retirados");
                    return;
                }
            }
            if (alive.Count == 0) return;
            int dropped = St.Proposals.RemoveAll(p => !alive.Contains(p.From) || !alive.Contains(p.To));
            dropped += St.StatementQueue.RemoveAll(q => !alive.Contains(q.KingdomId));
            dropped += St.Tributes.RemoveAll(t => t.Payer == null || t.Receiver == null || !alive.Contains(t.Payer) || !alive.Contains(t.Receiver));
            foreach (var e in St.Events)
                foreach (var id in e.Engagement.Keys.Where(x => !alive.Contains(x)).ToList()) e.Engagement.Remove(id);
            foreach (var id in St.WarFatigue.Keys.Where(x => !alive.Contains(x)).ToList()) St.WarFatigue.Remove(id);
            foreach (var id in St.LastStatementDay.Keys.Where(x => !alive.Contains(x)).ToList()) St.LastStatementDay.Remove(id);
            foreach (var key in St.Stance.Keys.Where(x => { var ids = (x ?? "").Split('|'); return ids.Length != 2 || !alive.Contains(ids[0]) || !alive.Contains(ids[1]); }).ToList()) St.Stance.Remove(key);
            if (dropped > 0) Log.Info($"[diplomacia] {dropped} propuesta(s), turno(s) o tributo(s) de reinos que ya no existen, retirados");
        }

        // ============================================================ house rules (limits set by the player, 0 = none)

        public static int WarCount(Kingdom k) => WorldSim.Kingdoms.Count(o => o != k && k.IsAtWarWith(o));
        public static int AllianceCount(Kingdom k) => WorldSim.Kingdoms.Count(o => o != k && Allied(k, o));
        public static int TradeCount(Kingdom k) => WorldSim.Kingdoms.Count(o => o != k && HasTrade(k, o));

        // A peace forced by exhaustion carries a truce of its own, even with the house rule at 0: without it the
        // same war could be declared again the next day. One year of Calradia, or the house rule if that is longer.
        private const int ForcedTruceDays = 84;
        private static int ForcedTruceLength => Math.Max(ForcedTruceDays, S.Get.MinTruceDays);

        /// <summary>Days left before either may declare war on the other again after their last peace. 0 = no truce in force.</summary>
        public static int TruceDaysLeft(Kingdom a, Kingdom b)
        {
            try
            {
                if (a == null || b == null || a == b || a.IsAtWarWith(b)) return 0;
                int left = 0, min = S.Get.MinTruceDays;
                var link = min > 0 ? a.GetStanceWith(b) : null;
                if (link != null)
                {
                    // the game stamps the day a war between two factions ends, however it ended
                    double since = CampaignTime.Now.ToDays - link.PeaceDeclarationDate.ToDays;
                    if (since >= 0 && since < min) left = (int)Math.Ceiling(min - since);
                }
                return Math.Max(left, ForcedTruceDaysLeft(a, b));
            }
            catch { return 0; }
        }

        /// <summary>Days left of the truce that a peace forced by exhaustion left between these two. 0 = none.</summary>
        private static int ForcedTruceDaysLeft(Kingdom a, Kingdom b)
        {
            try
            {
                if (a == null || b == null || a == b || St?.ForcedTruceUntil == null) return 0;
                if (!St.ForcedTruceUntil.TryGetValue(FactCheck.Pair(a.StringId, b.StringId), out double until)) return 0;
                double left = until - CampaignTime.Now.ToDays;
                // longer than any truce we set: the date comes from a later save
                return left > 0 && left <= 400 ? (int)Math.Ceiling(left) : 0;
            }
            catch { return 0; }
        }

        /// <summary>
        /// Why an AI ruler may not declare war on that kingdom under the house rules, or null. The rules are between AI
        /// crowns: the realm the player rules is never bound by them, and never shielded by them either. Whether an AI
        /// ruler may declare war on the player at all is the AllowAiWarOnPlayer setting, checked by the callers.
        /// </summary>
        public static string WarBlocked(Kingdom k, Kingdom o)
        {
            // The player is never stopped from declaring war. As a target the realm the player rules is covered like any
            // other: no AI king breaks a truce with it or piles onto it.
            if (k == null || o == null || PlayerRules(k)) return null;
            int truce = TruceDaysLeft(k, o);
            if (truce > 0) return $"tregua tras la paz con {o.Name}: faltan {truce} días";
            int max = S.Get.MaxWarsPerKingdom;
            if (max > 0 && WarCount(k) >= max) return $"{k.Name} ya está en guerra con {WarCount(k)} reinos (límite {max})";
            if (max > 0 && WarCount(o) >= max) return $"{o.Name} ya está en guerra con {WarCount(o)} reinos (límite {max})";
            return null;
        }

        private static string AllianceBlocked(Kingdom k, Kingdom o)
        {
            int max = S.Get.MaxAlliancesPerKingdom;
            if (max <= 0 || k == null || o == null || PlayerRules(k) || PlayerRules(o)) return null;
            // with "a new alliance replaces the old one" a kingdom at its limit still has room: once the new one is
            // sealed, its oldest one goes
            bool swap = S.Get.AllianceReplacesOldest;
            if (AllianceCount(k) >= max && !(swap && OldestAlly(k, o) != null)) return $"{k.Name} ya tiene {AllianceCount(k)} alianzas (límite {max})";
            return AllianceCount(o) >= max && !(swap && OldestAlly(o, k) != null) ? $"{o.Name} ya tiene {AllianceCount(o)} alianzas (límite {max})" : null;
        }

        /// <summary>The ally this kingdom has had longest, leaving out <paramref name="except"/> and any kingdom the player rules. null when it has none to drop.</summary>
        private static Kingdom OldestAlly(Kingdom k, Kingdom except)
        {
            Kingdom best = null;
            double bestDay = double.MaxValue;
            foreach (var o in WorldSim.Kingdoms.Where(x => x != k && x != except && !PlayerRules(x) && Allied(k, x)))
            {
                // An alliance made before we kept dates is older than any we dated. Among those, the game only knows
                // when each runs out, and they all last the same.
                if (!St.AllianceSince.TryGetValue(FactCheck.Pair(k.StringId, o.StringId), out double day))
                {
                    try { day = Alli.GetAllianceEndDate(k, o).ToDays - 1e7; } catch { day = -1e7; }
                }
                if (day < bestDay) { best = o; bestDay = day; }
            }
            return best;
        }

        /// <summary>
        /// Once a new alliance between these two is sealed and seen to exist: each AI kingdom it left over its limit
        /// ends its oldest other alliance. Never called before the new one stands, so a failed alliance costs nobody
        /// an old one. Returns what was done, to show. The kingdom the player rules is never touched.
        /// </summary>
        private static List<string> DropOldestAlliances(Kingdom k, Kingdom o)
        {
            var done = new List<string>();
            int max = S.Get.MaxAlliancesPerKingdom;
            if (max <= 0 || !S.Get.AllianceReplacesOldest || !Allied(k, o)) return done;
            foreach (var pair in new[] { new[] { k, o }, new[] { o, k } })
            {
                Kingdom who = pair[0], partner = pair[1];
                if (PlayerRules(who)) continue;
                for (int guard = 0; guard < 10 && AllianceCount(who) > max; guard++)
                {
                    var old = OldestAlly(who, partner);
                    if (old == null) break;
                    EndAlliance(who, old);
                    if (Allied(who, old)) break;
                    WorldSim.ChangeStance(old.StringId, who.StringId, -20);
                    Log.Info($"[diplomacia] {who.Name} deja su alianza con {old.Name} para aliarse con {partner.Name}");
                    string text = L.F("AIL_e7b1c761", $"{who.Name} pone fin a su alianza con {old.Name} para sellar una nueva con {partner.Name}.");
                    done.Add(text);
                    NewsStore.Add(new NewsItem
                    {
                        Type = "alliance", Importance = 3, Kingdoms = new List<string> { who.StringId, old.StringId, partner.StringId },
                        Heroes = new[] { who.Leader, old.Leader }.Where(h => h != null).Select(h => h.StringId).ToList(),
                        Text = $"{who.Name} has ended its alliance with {old.Name}, its oldest, to make room for a new alliance with {partner.Name}.",
                        TitleEs = L.F("AIL_e7b1c760", $"{who.Name} deja a {old.Name}"), TextEs = text,
                    });
                }
            }
            return done;
        }

        public static void OnAllianceStarted(Kingdom a, Kingdom b)
        {
            try { if (a != null && b != null && St != null) St.AllianceSince[FactCheck.Pair(a.StringId, b.StringId)] = CampaignTime.Now.ToDays; } catch { }
        }

        public static void OnAllianceEnded(Kingdom a, Kingdom b)
        {
            try { if (a != null && b != null && St != null) St.AllianceSince.Remove(FactCheck.Pair(a.StringId, b.StringId)); } catch { }
        }

        /// <summary>With the setting off, AI rulers do not open alliances or trade with the kingdom the player rules. A vassal player changes nothing.</summary>
        private static string PactOfferBlocked(Kingdom k, Kingdom o) =>
            !S.Get.AiOffersPactsToPlayer && !PlayerRules(k) && PlayerRules(o) ? "las ofertas de alianza y comercio al reino del jugador están desactivadas" : null;

        // ============================================================ a refused proposal has to wait

        private static readonly string[] CooledTypes = { "peace", "alliance", "trade" };
        private static string RefusalKey(string type, string a, string b) => type + "|" + FactCheck.Pair(a, b);

        private static void MarkRefused(string type, string from, string to, double now)
        {
            if (from == null || to == null || !CooledTypes.Contains(type)) return;
            St.Refused[RefusalKey(type, from, to)] = now;
        }

        /// <summary>Days left before that kind of proposal may be made again between these two. 0 = free.</summary>
        public static int RefusalDaysLeft(string type, Kingdom a, Kingdom b)
        {
            try
            {
                int wait = S.Get.RefusedProposalCooldownDays;
                if (wait <= 0 || a == null || b == null || a == b) return 0;
                if (!St.Refused.TryGetValue(RefusalKey(type, a.StringId, b.StringId), out double day)) return 0;
                double since = CampaignTime.Now.ToDays - day;
                if (since < 0 || since >= wait) return 0;
                // a refusal from an earlier war says nothing about this one
                if (type == "peace") { int war = WarDays(a, b); if (war >= 0 && since > war + 1) return 0; }
                return (int)Math.Ceiling(wait - since);
            }
            catch { return 0; }
        }

        /// <summary>Why this ruler may not make that proposal yet, or null. AI rulers only: the player may insist.</summary>
        private static string ProposalBlocked(string type, Kingdom k, Kingdom o)
        {
            if (PlayerRules(k)) return null;
            int left = RefusalDaysLeft(type, k, o);
            return left > 0 ? $"esa propuesta ({type}) entre {k.Name} y {o.Name} se rechazó o caducó hace poco: faltan {left} días" : null;
        }

        /// <summary>
        /// Ids of the kingdoms that still exist. The game keeps a destroyed kingdom in its list, flagged; a kingdom
        /// that has no ruler for the moment still exists, which is why this is wider than WorldSim.Kingdoms.
        /// </summary>
        private static HashSet<string> ExistingKingdomIds() =>
            new HashSet<string>(Campaign.Current.Kingdoms.Where(k => k != null && !k.IsEliminated && k.StringId != null).Select(k => k.StringId));

        private static void PruneRefusals(double now)
        {
            int wait = Math.Max(0, S.Get.RefusedProposalCooldownDays);
            foreach (var key in St.Refused.Where(kv => kv.Key == null || now - kv.Value >= wait || kv.Value > now).Select(kv => kv.Key).ToList()) St.Refused.Remove(key);
            // captures, alliance dates and truces of kingdoms that are gone
            var alive = ExistingKingdomIds();
            if (alive.Count == 0) return;
            bool Gone(string pairKey) { var ids = (pairKey ?? "").Split('|'); return ids.Length != 2 || !alive.Contains(ids[0]) || !alive.Contains(ids[1]); }
            foreach (var key in St.WarLastCapture.Keys.Where(Gone).ToList()) St.WarLastCapture.Remove(key);
            foreach (var key in St.AllianceSince.Keys.Where(Gone).ToList()) St.AllianceSince.Remove(key);
            foreach (var key in St.ForcedTruceUntil.Where(kv => Gone(kv.Key) || kv.Value <= now).Select(kv => kv.Key).ToList()) St.ForcedTruceUntil.Remove(key);
            foreach (var key in St.PeaceNotes.Keys.Where(id => !alive.Contains(id)).ToList()) St.PeaceNotes.Remove(key);
        }

        // ============================================================ time: how long a war has lasted, the last peace, how old a kingdom is

        // A stance that never changed carries day zero of the calendar, a thousand years back
        private const double SaneDays = 84 * 100;
        private const int RecentChangeDays = 21, StalemateShownFrom = 42;

        /// <summary>Whole days this war has lasted, from the game's own date. -1 = not at war, or no date.</summary>
        public static int WarDays(Kingdom a, Kingdom b)
        {
            try
            {
                if (a == null || b == null || a == b || !a.IsAtWarWith(b)) return -1;
                double d = CampaignTime.Now.ToDays - a.GetStanceWith(b).WarStartDate.ToDays;
                return d >= 0 && d < SaneDays ? (int)d : -1;
            }
            catch { return -1; }
        }

        /// <summary>Whole days since these two last made peace. -1 = at war, never fought, or no date.</summary>
        public static int PeaceDays(Kingdom a, Kingdom b)
        {
            try
            {
                if (a == null || b == null || a == b || a.IsAtWarWith(b)) return -1;
                double d = CampaignTime.Now.ToDays - a.GetStanceWith(b).PeaceDeclarationDate.ToDays;
                return d >= 0 && d < SaneDays ? (int)d : -1;
            }
            catch { return -1; }
        }

        /// <summary>"40 day(s)" under a year, "200 days, 2.4 years" over it. Years are years of this world.</summary>
        public static string Span(int days)
        {
            int year = Math.Max(1, CampaignTime.DaysInYear);
            return days < year ? $"{days} day(s)" : $"{days} days, {(days / (double)year).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture)} years";
        }

        public static string WarSpan(Kingdom a, Kingdom b)
        {
            int days = WarDays(a, b);
            return days < 0 ? "" : $" (for {Span(days)})";
        }

        /// <summary>Kingdoms this one made peace with in the last <paramref name="within"/> days, most recent first.</summary>
        public static List<string> RecentPeaces(Kingdom k, int within)
        {
            var list = new List<KeyValuePair<int, string>>();
            foreach (var o in WorldSim.Kingdoms.Where(x => x != k))
            {
                int d = PeaceDays(k, o);
                if (d >= 0 && d <= within) list.Add(new KeyValuePair<int, string>(d, $"{o.StringId} ({d} day(s) ago)"));
            }
            return list.OrderBy(x => x.Key).Select(x => x.Value).ToList();
        }

        private static readonly Tpl TNewKingdom = new Tpl("world/kingdom_newly_founded.txt",
            "NEWLY FOUNDED {days} day(s) ago (a young realm that is only beginning: holding little land is where it starts, not a sign that it is collapsing or has lost a war)");

        /// <summary>How old the kingdom is, for the fact sheets. The game keeps no founding date, so it is the day we first saw it.</summary>
        public static string Age(Kingdom k)
        {
            try
            {
                if (k == null || !St.KingdomSeenDay.TryGetValue(k.StringId, out double day) || day < 0) return "an old, long-established realm";
                int d = Math.Max(0, (int)(CampaignTime.Now.ToDays - day));
                return d < Math.Max(1, CampaignTime.DaysInYear) ? TNewKingdom.Format("days", d) : $"founded {Span(d)} ago";
            }
            catch { return "an old, long-established realm"; }
        }

        /// <summary>
        /// Notes the day each kingdom is first seen. The first time ever, every kingdom already there is marked as
        /// old: nothing says when it was founded, and calling it new would be the worse mistake.
        /// </summary>
        public static void TrackKingdoms()
        {
            try
            {
                if (Campaign.Current == null || St == null) return;
                Heal();
                var seen = St.KingdomSeenDay;
                bool first = seen.Count == 0;
                double now = CampaignTime.Now.ToDays;
                // every kingdom that exists, with or without a ruler today: one that is only waiting for an heir
                // must not come back later as newly founded
                var ids = ExistingKingdomIds();
                if (ids.Count == 0) return;
                foreach (var id in ids) if (!seen.ContainsKey(id)) seen[id] = first ? -1 : now;
                // destroyed, or no longer in the game at all (a mod took it out)
                foreach (var id in seen.Keys.Where(x => !ids.Contains(x)).ToList()) seen.Remove(id);
                SeedCaptures();
            }
            catch (Exception ex) { Log.Error("[diplomacia] edad de los reinos", ex); }
        }

        private static int _seededSession = -1;

        /// <summary>
        /// Once per loaded campaign: a save from before captures were dated still has them in the history of each
        /// town and castle (World/History.cs). Without this, every old war would look like a stalemate from its
        /// first day and the forced peace would end wars in which towns are still falling.
        /// </summary>
        private static void SeedCaptures()
        {
            if (_seededSession == WorldSim.Session) return;
            _seededSession = WorldSim.Session;
            try
            {
                var byName = new Dictionary<string, string>();
                foreach (var k in WorldSim.Kingdoms) byName[k.Name.ToString()] = k.StringId;
                if (St.Owners == null) return;
                foreach (var list in St.Owners.Values)
                    for (int i = 1; list != null && i < list.Count; i++)
                    {
                        var before = list[i - 1]; var after = list[i];
                        if (before == null || after == null) continue;
                        if (after.FromDay < 0 || string.IsNullOrEmpty(before.Of) || string.IsNullOrEmpty(after.Of) || before.Of == after.Of) continue;
                        if (!byName.TryGetValue(before.Of, out var a) || !byName.TryGetValue(after.Of, out var b)) continue;
                        string key = FactCheck.Pair(a, b);
                        if (!St.WarLastCapture.TryGetValue(key, out double day) || after.FromDay > day) St.WarLastCapture[key] = after.FromDay;
                    }
            }
            catch (Exception ex) { Log.Error("[diplomacia] capturas anteriores", ex); }
        }

        public static void OnKingdomCreated(Kingdom k)
        {
            try
            {
                if (k == null || Campaign.Current == null || St == null) return;
                TrackKingdoms();
                St.KingdomSeenDay[k.StringId] = CampaignTime.Now.ToDays;
                Facts.Invalidate();
            }
            catch (Exception ex) { Log.Error("[diplomacia] reino nuevo", ex); }
        }

        // ============================================================ wars that wear a realm down

        /// <summary>Days since a town or castle last changed hands between two kingdoms at war, or since the war began. -1 = not at war or no date.</summary>
        public static int StalemateDays(Kingdom a, Kingdom b)
        {
            try
            {
                if (a == null || b == null || a == b || !a.IsAtWarWith(b)) return -1;
                string key = FactCheck.Pair(a.StringId, b.StringId);
                double now = CampaignTime.Now.ToDays;
                int war = WarDays(a, b);
                if (war < 0)
                {
                    // The game has no usable date for this war (WarDays). It is counted from the day we first saw
                    // it (TrackWars), never from the game's date: that one would make it centuries old at once.
                    if (!St.WarSeenDay.TryGetValue(key, out double seen)) return -1;
                    double known = now - seen;
                    if (known < 0 || known >= SaneDays) return -1;
                    war = (int)known;
                }
                // a capture from before this war began belongs to an earlier one and says nothing here
                if (St.WarLastCapture.TryGetValue(key, out double day))
                {
                    double since = now - day;
                    if (since >= 0 && since < war) return (int)since;
                }
                return war;
            }
            catch { return -1; }
        }

        /// <summary>Notes the day a war with no usable start date is first seen, and forgets the ones that are over.</summary>
        private static void TrackWars(double now)
        {
            var seen = St.WarSeenDay;
            var ks = WorldSim.Kingdoms.ToList();
            var open = new HashSet<string>();
            for (int i = 0; i < ks.Count; i++)
                for (int j = i + 1; j < ks.Count; j++)
                {
                    if (!ks[i].IsAtWarWith(ks[j])) continue;
                    string key = FactCheck.Pair(ks[i].StringId, ks[j].StringId);
                    open.Add(key);
                    if (WarDays(ks[i], ks[j]) >= 0) seen.Remove(key);
                    else if (!seen.TryGetValue(key, out double day) || day > now) seen[key] = now;      // day > now: an older save was loaded
                }
            // A kingdom without a ruler for the moment is not in the list above: its wars keep their date.
            var alive = ExistingKingdomIds();
            var ruled = new HashSet<string>(ks.Select(x => x.StringId));
            foreach (var key in seen.Keys.ToList())
            {
                if (open.Contains(key)) continue;
                var ids = (key ?? "").Split('|');
                bool waiting = ids.Length == 2 && alive.Contains(ids[0]) && alive.Contains(ids[1]) && !(ruled.Contains(ids[0]) && ruled.Contains(ids[1]));
                if (!waiting) seen.Remove(key);
            }
        }

        /// <summary>Daily chance (0-60) that a ruler takes a turn about his wars: a tired realm, too many fronts, a war that drags on.</summary>
        private static int PeaceUrge(Kingdom k)
        {
            var foes = WorldSim.Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).ToList();
            if (foes.Count == 0) return 0;
            // nothing to decide while every war already has his offer on the table or a refusal still cooling
            if (!foes.Any(o => Pending("peace", k, o) == null && RefusalDaysLeft("peace", k, o) == 0)) return 0;
            float f = WorldSim.Fatigue(k);
            int urge = f < 35 ? 0 : f < 60 ? 10 : f < 85 ? 25 : 40;
            if (foes.Count >= 3) urge += 10 * (foes.Count - 2);
            if (foes.Max(o => WarDays(k, o)) >= 2 * Math.Max(1, CampaignTime.DaysInYear)) urge += 10;
            return Math.Min(60, urge);
        }

        /// <summary>
        /// The hard stop. A war between two AI crowns in which nothing has changed hands for the set number of days
        /// ends here, whatever their rulers say: the longest stalemate first, one a day. The realm the player rules is
        /// left alone; a realm the player only serves is not.
        /// </summary>
        private static void ForcedPeace(double now)
        {
            try
            {
                int limit = S.Get.ForcedPeaceStalemateDays;
                if (limit <= 0) return;
                var ks = WorldSim.Kingdoms.Where(k => !PlayerRules(k)).ToList();
                Kingdom a = null, b = null;
                int worst = -1;
                for (int i = 0; i < ks.Count; i++)
                    for (int j = i + 1; j < ks.Count; j++)
                    {
                        int stale = StalemateDays(ks[i], ks[j]);
                        if (stale >= limit && stale > worst) { a = ks[i]; b = ks[j]; worst = stale; }
                    }
                if (a == null) return;
                int lasted = Math.Max(0, WarDays(a, b));
                // our own news tells it better than the plain "they made peace"
                bool live = NewsTracker.Live;
                NewsTracker.Live = false;
                try { Ours(() => MakePeaceAction.Apply(a, b)); }
                finally { NewsTracker.Live = live; }
                if (a.IsAtWarWith(b)) { Log.Warn($"[diplomacia] paz forzosa entre {a.Name} y {b.Name}: el juego no la ha aplicado"); return; }
                St.Proposals.RemoveAll(p => p.Type == "peace" && ((p.From == a.StringId && p.To == b.StringId) || (p.From == b.StringId && p.To == a.StringId)));
                St.Refused.Remove(RefusalKey("peace", a.StringId, b.StringId));
                St.WarSeenDay.Remove(FactCheck.Pair(a.StringId, b.StringId));
                // its own truce, enforced in WarBlocked and told to both rulers (DescribeHouseRules)
                int truce = ForcedTruceLength;
                St.ForcedTruceUntil[FactCheck.Pair(a.StringId, b.StringId)] = now + truce;
                Log.Info($"[diplomacia] paz forzosa entre {a.Name} y {b.Name}: {lasted} días de guerra, {worst} sin que nada cambie de manos (límite {limit}); tregua de {truce} días");
                NewsStore.Add(new NewsItem
                {
                    Type = "peace", Importance = 3, Kingdoms = new List<string> { a.StringId, b.StringId }, Heroes = new List<string> { a.Leader.StringId, b.Leader.StringId },
                    Text = TForcedPeaceNews.Format("a", a.Name, "b", b.Name, "lasted", lasted, "stale", worst, "truce", truce),
                    TitleEs = L.F("AIL_e7b1c75d", $"Paz por agotamiento entre {a.Name} y {b.Name}"),
                    TextEs = L.F("AIL_e7b1c75e", $"Tras {lasted} días de guerra, y {worst} sin que una sola ciudad o castillo cambiara de manos, {a.Name} y {b.Name} han depuesto las armas. Ninguno ha vencido y no hay condiciones: ya no podían sostenerla."),
                });
                WorldSim.Say(L.F("AIL_e7b1c75f", $"{a.Name} y {b.Name} firman la paz por agotamiento, tras {lasted} días de una guerra estancada."), Colors.Yellow);
            }
            catch (Exception ex) { Log.Error("[diplomacia] paz forzosa", ex); }
        }

        // The English line of the news item, which is what the AI reads of it later
        private static readonly Tpl TForcedPeaceNews = new Tpl("world/forced_peace_news.txt",
            "{a} and {b} have laid down their arms out of sheer exhaustion, after {lasted} days of war and {stale} days in which no town or castle changed hands. Neither won and no terms were set. Neither may declare war on the other again for {truce} days.");

        private static readonly Tpl TWeariness = new Tpl("kingdom_statement/war_weariness.txt",
@"## Your wars and how tired your realm is (exact, right now)
- War weariness of {kingdom}: {level}, {score} out of 100. {plain}
{fronts}
{wars}
{forced}");

        // One line per level, picked by the word before the colon
        private static readonly Tpl TWearinessLevels = new Tpl("kingdom_statement/war_weariness_levels.txt",
@"fresh: Your realm is rested. War costs you little yet.
strained: The strain is beginning to show: the levies grumble and coin runs thinner. You can go on, but not for ever.
weary: Your realm is weary of war. Your lords and your towns want at least some of the fighting to end: find the wars you gain least from and close them.
exhausted: Your realm is exhausted. It cannot keep these wars going: you should be asking for peace now, and taking a fair offer when one comes.
breaking: Your realm is at breaking point. Every further week of war risks ruin and revolt: make peace wherever it can be had, even on poor terms, and take any peace offered that does not destroy you.");

        private static readonly Tpl TWarFronts = new Tpl("kingdom_statement/war_fronts.txt",
            "- You are fighting {count} kingdoms at once. No realm holds that many fronts for long: every extra war makes all of them wear you down faster. Closing some of them comes before everything else.");

        private static readonly Tpl TWarLine = new Tpl("kingdom_statement/war_line.txt",
            "- War with {enemy} [id: {id}]: it has lasted {span}. Strength: yours {mine}, theirs {theirs}. Towns and castles taken in this war: you {won}, they {lost}. Their weariness: {their_level}.");

        private static readonly Tpl TWarStale = new Tpl("kingdom_statement/war_stalemate.txt",
            " No town or castle has changed hands between you for {days} days: a stalemate that gains you nothing.");

        private static readonly Tpl TWarForcedSoon = new Tpl("kingdom_statement/war_forced_soon.txt",
            " In {left} more day(s) like that, exhaustion ends this war by itself with no terms for either side: better to make your own peace first.");

        private static readonly Tpl TWarPeaceRefused = new Tpl("kingdom_statement/war_peace_refused.txt",
            " A peace between you was refused or left unanswered not long ago: you may not propose it again for {left} more day(s).");

        // What a peace is worth in each war, so a ruler who needs it knows he can pay for it and one who is winning knows what to ask
        private static readonly Tpl TWarBuy = new Tpl("kingdom_statement/war_peace_buy.txt",
            " You are the weaker side of this war: you need not only ask for peace, you can BUY it. In propose_peace, offer_reparations pays them a sum at once, offer_daily_tribute (with offer_tribute_days or offer_tribute_years) pays them by the day, offer_settlement hands them a town or castle of yours. As things stand a fair price is about {reparations} denars at once, or {daily} denars a day for {days} days{fief}. Your treasury holds {gold} denars: an offer of more than that at once is thrown away.");
        private static readonly Tpl TWarBuyFief = new Tpl("kingdom_statement/war_peace_buy_fief.txt",
            ", or one of your towns or castles if the coin is not there");
        private static readonly Tpl TWarSell = new Tpl("kingdom_statement/war_peace_sell.txt",
            " You hold the upper hand in this war: you can DEMAND a price for peace. In propose_peace, reparations, daily_tribute (with tribute_days or tribute_years) and settlement are what they would give you. As things stand a fair price is about {reparations} denars at once, or {daily} denars a day for {days} days; ask for much more and they will rather fight on.");
        private static readonly Tpl TWarEven = new Tpl("kingdom_statement/war_peace_even.txt",
            " Neither side holds the upper hand: the fair peace here is one with no terms either way.");

        /// <summary>
        /// Who is losing a war, from -1 (this side is winning clearly) to 1 (this side is losing clearly):
        /// strength, towns and castles taken, and how tired each realm is.
        /// </summary>
        private static float WarEdge(Kingdom k, Kingdom o, int won, int lost)
        {
            double mine = Math.Max(1f, k.CurrentTotalStrength), theirs = Math.Max(1f, o.CurrentTotalStrength);
            double e = Math.Log(theirs / mine, 2) * 0.5 + (lost - won) * 0.15 + (WorldSim.Fatigue(k) - WorldSim.Fatigue(o)) / 100.0 * 0.5;
            return (float)Math.Max(-1, Math.Min(1, e));
        }

        private static string PeacePriceLine(Kingdom k, Kingdom o, int won, int lost)
        {
            var cfg = S.Get;
            float edge = WarEdge(k, o, won, lost);
            if (Math.Abs(edge) < 0.2f) return TWarEven.Text;
            // half the caps for a war lost or won outright, less for a narrower one
            float share = Math.Abs(edge) * 0.5f;
            int reparations = Math.Min(cfg.MaxReparations, Math.Max(500, (int)(cfg.MaxReparations * share / 500) * 500));
            int daily = Math.Min(cfg.MaxDailyTribute, Math.Max(10, (int)(cfg.MaxDailyTribute * share / 10) * 10));
            int days = Math.Max(10, Math.Min(200, CampaignTime.DaysInYear));
            if (edge < 0) return TWarSell.Format("reparations", reparations, "daily", daily, "days", days);
            int gold = Treasury(k);
            return TWarBuy.Format("reparations", reparations, "daily", daily, "days", days, "gold", gold, "fief", gold < reparations || edge >= 0.7f ? TWarBuyFief.Text : "");
        }

        private static readonly Tpl TForcedRule =new Tpl("kingdom_statement/forced_peace_rule.txt",
            "- A war in which no town or castle changes hands for {days} days ends by itself, from exhaustion on both sides, with no terms.");

        private static readonly Tpl TTaskWars = new Tpl("kingdom_statement/task_wars.txt",
            "No event calls you today: you speak because of your wars. Read «Your wars and how tired your realm is» and decide, war by war, whether to fight on, to propose peace or to answer an offer. Each war says what a fair peace is: if you are the one who needs it, offer a price for it when you can pay and ask for it with no terms when you cannot; if you hold the upper hand, you may demand one. If your realm is weary or worse, or you are fighting several kingdoms at once, a statement that closes no war and offers no peace fails your realm. Issue your statement now.");

        private static readonly Tpl TRecentChanges = new Tpl("kingdom_statement/recent_changes.txt",
            "- Changed in the last {days} days: {changes}. A chronicle, a rumour or an earlier statement of yours from before a change may speak of a peace or a war that is no longer so: the lines above are what is true today.");

        private static readonly Tpl TRuleAllianceSwap = new Tpl("kingdom_statement/rule_alliance_swap.txt",
            "- A kingdom at its limit of alliances may still seal a new one: its oldest alliance is then ended to make room.{oldest}");
        private static readonly Tpl TRuleAllianceSwapYours = new Tpl("kingdom_statement/rule_alliance_swap_yours.txt", " You are at your limit: yours to end would be the one with {ally}.");
        private static readonly Tpl TRuleAllianceFull = new Tpl("kingdom_statement/rule_alliance_full.txt",
            "- A kingdom at its limit of alliances cannot make a new one until an old one ends: such a proposal is thrown away.");
        private static readonly Tpl TRuleNoPactsToPlayer = new Tpl("kingdom_statement/rule_no_pacts_to_player.txt",
            "- You may NOT propose an alliance or a trade agreement to {kingdom}, the realm ruled by {ruler}: wait for them to propose it. You may still answer what they propose.");
        private static readonly Tpl TRuleForcedTruce = new Tpl("kingdom_statement/rule_forced_truce.txt",
            "- A war that ends by itself from exhaustion leaves a truce of {days} days: until it is over neither side may declare war on the other.");
        private static readonly Tpl TRuleForcedTruceEntry = new Tpl("kingdom_statement/rule_forced_truce_entry.txt",
            "{id} (truce after your war with them ended from exhaustion, {days} more days)");
        private static readonly Tpl TWantedPlain = new Tpl("kingdom_statement/proposal_wanted_plain.txt",
            " You asked them for a peace with no terms and they hold to these: a plain peace is not on offer from them. Accept these terms, reject them, or fight on.");
        private static readonly Tpl TCounterPeace = new Tpl("kingdom_statement/proposal_counter.txt",
            " This is their answer to the peace you had proposed: yours is off the table and only these terms stand. Accept them, reject them, or propose other terms of your own (which would replace these).");
        private static readonly Tpl TRuleRefused =new Tpl("kingdom_statement/rule_refused.txt",
            "- A proposal of peace, alliance or trade that was refused, or left unanswered until it ran out, may not be made again between the same two kingdoms for {days} days. Right now you may NOT propose: {barred}.");

        private static string PlainWeariness(float f) =>
            PickLine(TWearinessLevels, f < 15 ? "fresh" : f < 35 ? "strained" : f < 60 ? "weary" : f < 85 ? "exhausted" : "breaking");

        /// <summary>The line of a template that begins with "key:", without the key.</summary>
        /// <summary>The line of a tag template whose first word is <paramref name="key"/>, or "".</summary>
        internal static string Line(Tpl t, string key) => PickLine(t, key);

        private static string PickLine(Tpl t, string key)
        {
            key += ":";
            foreach (var raw in t.Text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith(key, StringComparison.OrdinalIgnoreCase)) return line.Substring(key.Length).Trim();
            }
            return "";
        }

        // ---- territory a ruler can hold: only what lies by his own lands

        private static readonly Tpl TWarReach = new Tpl("kingdom_statement/war_reach.txt",
            " Their towns and castles within your reach, the only ones you may demand of them: {list}.");
        private static readonly Tpl TWarReachNone = new Tpl("kingdom_statement/war_reach_none.txt",
            " None of their towns or castles lies within your reach: demand no territory of them.");
        // One line per tag, picked by the word before the colon
        private static readonly Tpl TReachTags = new Tpl("kingdom_statement/war_reach_tags.txt",
@"border: on your border
near: near your lands
once: far away, but once yours
siege: far away, but under your siege right now");
        private static readonly Tpl TWhyFar = new Tpl("kingdom_statement/peace_why_far.txt",
            "{name} lies too far from the lands of {realm} for it to demand: only a town or castle on its border or near its lands, one it once held, or one it is besieging right now, may be demanded");

        private static Why WhyFar(Settlement st, Kingdom realm) => new Why(
            L.F("AIL_e7b1c796", $"{st.Name} queda demasiado lejos de las tierras de {realm.Name} para exigirla"),
            TWhyFar.Format("name", st.Name, "realm", realm.Name));

        /// <summary>The town or castle belonged to that kingdom at some point of the history we keep (World/History.cs).</summary>
        private static bool OnceHeld(Settlement st, Kingdom k)
        {
            try
            {
                // By kingdom id where the record has it. Records from before the id was kept only have the name the
                // kingdom went by then, which is all there is to compare.
                string name = k.Name?.ToString();
                return St.Owners != null && St.Owners.TryGetValue(st.StringId, out var list) && list != null
                       && list.Any(t => t != null && (!string.IsNullOrEmpty(t.OfId) ? t.OfId == k.StringId : !string.IsNullOrEmpty(name) && t.Of == name));
            }
            catch { return false; }
        }

        /// <summary>
        /// Whether <paramref name="k"/> may demand that town or castle: it lies on his border or near his lands, or
        /// it was his once. A realm with no land has nothing to measure from and is not held back. The player is
        /// never restricted.
        /// </summary>
        private static bool WithinReach(Settlement st, Kingdom k)
        {
            if (st == null || k == null || PlayerRules(k)) return true;
            int reach = Facts.Reach(st, k, out _);
            return reach < 0 || reach <= 1 || OnceHeld(st, k) || Besieging(st, k);
        }

        /// <summary>His own army is before its walls right now: he can ask for what he is about to take, however far from home.</summary>
        private static bool Besieging(Settlement st, Kingdom k)
        {
            try
            {
                var camp = st.IsUnderSiege ? st.SiegeEvent?.BesiegerCamp : null;
                if (camp == null) return false;
                if (camp.LeaderParty?.MapFaction == k) return true;
                return camp.GetInvolvedPartiesForEventType().Any(x => x != null && x.MapFaction == k);
            }
            catch { return false; }
        }

        /// <summary>For a ruler's war with <paramref name="o"/>: which of their towns and castles he may demand.</summary>
        private static string ReachLine(Kingdom k, Kingdom o)
        {
            try
            {
                var within = new List<Tuple<int, float, string>>();
                int seen = 0;
                foreach (var f in o.Fiefs)
                {
                    var st = f?.Settlement;
                    if (st == null) continue;
                    int reach = Facts.Reach(st, k, out float gap);
                    if (reach < 0) return "";
                    seen++;
                    string tag = reach == 0 ? "border" : reach == 1 ? "near" : OnceHeld(st, k) ? "once" : Besieging(st, k) ? "siege" : null;
                    if (tag == null) continue;
                    // a tag file edited before a tag existed has no line for it: the place is still listed
                    string label = PickLine(TReachTags, tag);
                    within.Add(Tuple.Create(reach, gap, label.Length > 0 ? $"{st.Name} ({label})" : st.Name.ToString()));
                }
                if (seen == 0) return "";
                return within.Count == 0 ? TWarReachNone.Text
                    : TWarReach.Format("list", string.Join(", ", within.OrderBy(x => x.Item1).ThenBy(x => x.Item2).Take(20).Select(x => x.Item3)));
            }
            catch (Exception ex) { Log.Error("[diplomacia] plazas al alcance", ex); return ""; }
        }

        /// <summary>The ruler's wars one by one and how tired the realm is, in plain words. Empty when at peace and rested.</summary>
        private static string DescribeWarsFor(Kingdom k)
        {
            var cfg = S.Get;
            float f = WorldSim.Fatigue(k);
            var foes = WorldSim.Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).OrderByDescending(o => WarDays(k, o)).ToList();
            if (foes.Count == 0 && f < 15) return "";
            var lines = new List<string>();
            foreach (var o in foes)
            {
                int days = WarDays(k, o), stale = StalemateDays(k, o), won = 0, lost = 0;
                try { var link = k.GetStanceWith(o); won = link.GetSuccessfulSieges(k); lost = link.GetSuccessfulSieges(o); } catch { }
                string line = TWarLine.Format("enemy", o.Name, "id", o.StringId, "span", days >= 0 ? Span(days) : "longer than anyone has counted",
                    "mine", (int)k.CurrentTotalStrength, "theirs", (int)o.CurrentTotalStrength, "won", won, "lost", lost, "their_level", WorldSim.FatigueWord(o));
                line += WarTolls.Trend(k, o) + ReachLine(k, o);
                if (stale >= StalemateShownFrom)
                {
                    line += TWarStale.Format("days", stale);
                    if (cfg.ForcedPeaceStalemateDays > 0 && !PlayerRules(k) && !PlayerRules(o)) line += TWarForcedSoon.Format("left", Math.Max(1, cfg.ForcedPeaceStalemateDays - stale));
                }
                int wait = PlayerRules(k) ? 0 : RefusalDaysLeft("peace", k, o);
                if (wait > 0) line += TWarPeaceRefused.Format("left", wait);
                else line += PeacePriceLine(k, o, won, lost);
                lines.Add(line);
            }
            string text = TWeariness.Format("kingdom", k.Name, "level", WorldSim.FatigueWord(k), "score", (int)f, "plain", PlainWeariness(f),
                "fronts", foes.Count >= 3 ? TWarFronts.Format("count", foes.Count) : "",
                "wars", string.Join("\n", lines),
                "forced", cfg.ForcedPeaceStalemateDays > 0 && foes.Count > 0 ? TForcedRule.Format("days", cfg.ForcedPeaceStalemateDays) : "");
            return string.Join("\n", text.Split('\n').Where(l => l.Trim().Length > 0));
        }

        private static string TradeBlocked(Kingdom k, Kingdom o)
        {
            int max = S.Get.MaxTradePactsPerKingdom;
            if (max <= 0 || k == null || o == null || PlayerRules(k) || PlayerRules(o)) return null;
            if (TradeCount(k) >= max) return $"{k.Name} ya tiene {TradeCount(k)} acuerdos comerciales (límite {max})";
            return TradeCount(o) >= max ? $"{o.Name} ya tiene {TradeCount(o)} acuerdos comerciales (límite {max})" : null;
        }

        /// <summary>The limits in force and what they forbid this ruler right now, for their prompt.</summary>
        private static string DescribeHouseRules(Kingdom k)
        {
            var cfg = S.Get;
            var lines = new List<string>();
            if (cfg.MaxWarsPerKingdom > 0) lines.Add($"- No kingdom may be at war with more than {cfg.MaxWarsPerKingdom} kingdom(s) at once. You are at war with {WarCount(k)}.");
            if (cfg.MinTruceDays > 0) lines.Add($"- After a peace, neither side may declare war on the other for {cfg.MinTruceDays} days.");
            if (cfg.ForcedPeaceStalemateDays > 0 && !PlayerRules(k)) lines.Add(TRuleForcedTruce.Format("days", ForcedTruceLength));
            {
                var no = new List<string>();
                // the realm the player rules is protected like any other target (WarBlocked); nothing is listed for the player himself
                foreach (var o in WorldSim.Kingdoms.Where(x => x != k && !k.IsAtWarWith(x) && !PlayerRules(k)))
                {
                    int truce = TruceDaysLeft(k, o), forced = ForcedTruceDaysLeft(k, o);
                    if (truce > 0 && forced >= truce) no.Add(TRuleForcedTruceEntry.Format("id", o.StringId, "days", truce));
                    else if (truce > 0) no.Add($"{o.StringId} (truce, {truce} more days)");
                    else if (cfg.MaxWarsPerKingdom > 0 && WarCount(k) >= cfg.MaxWarsPerKingdom) no.Add($"{o.StringId} (you are at your limit of wars)");
                    else if (cfg.MaxWarsPerKingdom > 0 && WarCount(o) >= cfg.MaxWarsPerKingdom) no.Add($"{o.StringId} (it is already fighting {WarCount(o)}: no piling on)");
                }
                // a truce left by a forced peace is listed even with every house rule off
                if (cfg.MaxWarsPerKingdom > 0 || cfg.MinTruceDays > 0 || no.Count > 0)
                    lines.Add("- Right now you may NOT declare war on: " + (no.Count > 0 ? string.Join(", ", no) : "nobody is barred") + ".");
            }
            if (cfg.MaxAlliancesPerKingdom > 0)
            {
                var full = WorldSim.Kingdoms.Where(x => x != k && !PlayerRules(x) && AllianceCount(x) >= cfg.MaxAlliancesPerKingdom).Select(x => x.StringId).ToList();
                lines.Add($"- At most {cfg.MaxAlliancesPerKingdom} alliance(s) per kingdom. You have {AllianceCount(k)}." + (full.Count > 0 ? " Already at their limit: " + string.Join(", ", full) + "." : ""));
                if (cfg.AllianceReplacesOldest)
                {
                    var old = AllianceCount(k) >= cfg.MaxAlliancesPerKingdom ? OldestAlly(k, null) : null;
                    lines.Add(TRuleAllianceSwap.Format("oldest", old != null ? TRuleAllianceSwapYours.Format("ally", old.StringId) : ""));
                }
                else lines.Add(TRuleAllianceFull.Text);
            }
            if (cfg.MaxTradePactsPerKingdom > 0)
            {
                var full = WorldSim.Kingdoms.Where(x => x != k && !PlayerRules(x) && TradeCount(x) >= cfg.MaxTradePactsPerKingdom).Select(x => x.StringId).ToList();
                lines.Add($"- At most {cfg.MaxTradePactsPerKingdom} trade agreement(s) per kingdom. You have {TradeCount(k)}." + (full.Count > 0 ? " Already at their limit: " + string.Join(", ", full) + "." : ""));
            }
            var mine = PlayerRealm;
            if (!cfg.AiOffersPactsToPlayer && PlayerRules(mine) && mine != k) lines.Add(TRuleNoPactsToPlayer.Format("kingdom", mine.StringId, "ruler", Hero.MainHero.Name));
            if (cfg.RefusedProposalCooldownDays > 0)
            {
                var barred = new List<string>();
                foreach (var o in WorldSim.Kingdoms.Where(x => x != k))
                    foreach (var type in CooledTypes)
                    {
                        int left = RefusalDaysLeft(type, k, o);
                        if (left > 0) barred.Add($"{TypeEn(type)} with {o.StringId} ({left} more days)");
                    }
                lines.Add(TRuleRefused.Format("days", cfg.RefusedProposalCooldownDays, "barred", barred.Count > 0 ? string.Join(", ", barred) : "nothing is barred"));
            }
            return lines.Count == 0 ? "" : "House rules of this world (enforced: an action that breaks one is thrown away, so do not try):\n" + string.Join("\n", lines);
        }

        // ============================================================ a new campaign that starts at peace

        // Set when the game creates a campaign and cleared when one is loaded, so a save never gets this
        private static bool _newCampaign;
        public static void OnNewGameCreated(CampaignGameStarter starter) { _newCampaign = true; }
        public static void OnGameLoaded(CampaignGameStarter starter) { _newCampaign = false; }

        /// <summary>The game raises this ten times in a row with 0 to 9. We act on the last, after everyone else.</summary>
        public static void OnCharacterCreationIsOver(int pass) { if (pass >= 9) PeaceAtStart(); }

        /// <summary>
        /// Once, on a campaign that has just been created: every war between two kingdoms ends. The game's starting
        /// wars come from its kingdom data, long before this runs. Also called from the first daily tick in case a
        /// mod skipped character creation.
        /// </summary>
        public static void PeaceAtStart()
        {
            if (!_newCampaign) return;
            _newCampaign = false;
            try
            {
                if (!S.Get.PeaceAtCampaignStart || Campaign.Current == null) return;
                // Campaign.Kingdoms holds crowns only: bandits, minor factions and rebels are clans and never appear in
                // it. WorldSim.Kingdoms drops the destroyed and the leaderless. What is left to rule out here is a
                // crown in the hands of rebels or outlaws (mods), and the realm the player starts in, whose wars
                // are the player's to end.
                var mine = PlayerRealm;
                var ks = WorldSim.Kingdoms.Where(k => k != mine && k.RulingClan != null && !k.RulingClan.IsRebelClan && !k.RulingClan.IsBanditFaction).ToList();
                int made = 0;
                bool live = NewsTracker.Live;
                NewsTracker.Live = false;      // a dozen treaties signed at once are one piece of news, not a dozen
                try
                {
                    for (int i = 0; i < ks.Count; i++)
                        for (int j = i + 1; j < ks.Count; j++)
                        {
                            if (!ks[i].IsAtWarWith(ks[j])) continue;
                            Kingdom a = ks[i], b = ks[j];
                            Ours(() => MakePeaceAction.Apply(a, b));
                            if (!a.IsAtWarWith(b)) made++;
                        }
                }
                finally { NewsTracker.Live = live; }
                Log.Info($"[diplomacia] partida nueva en paz: {made} guerras entre reinos terminadas al empezar");
                // a world that starts at peace starts rested
                if (St?.WarFatigue != null) foreach (var k in ks.Where(x => WarCount(x) == 0)) St.WarFatigue.Remove(k.StringId);
                if (made == 0) return;
                // "no kingdom is at war" is only said when it is true: a war left alone (the player's realm) or one that
                // would not end keeps it quiet
                var all = WorldSim.Kingdoms.ToList();
                if (all.Any(a => all.Any(b => a != b && a.IsAtWarWith(b)))) { Log.Info("[diplomacia] quedan guerras entre reinos (las del reino del jugador no se tocan): no se anuncia la paz general"); return; }
                NewsStore.Add(new NewsItem
                {
                    Type = "custom", Importance = 3, Kingdoms = ks.Select(x => x.StringId).ToList(),
                    Text = "For the first time in years, no kingdom of Calradia is at war with another: every crown has laid down its arms.",
                    TitleEs = L.T("AIL_e7b1c738", "Calradia en paz"),
                    TextEs = L.T("AIL_e7b1c739", "Por primera vez en años, ningún reino de Calradia está en guerra con otro: todas las coronas han depuesto las armas."),
                });
                WorldSim.Say(L.T("AIL_e7b1c73a", "AI Legacy: la partida empieza con todos los reinos en paz."), Colors.Cyan);
            }
            catch (Exception ex) { Log.Error("Paz al empezar la partida", ex); }
        }

        // ============================================================ proposals

        private static Proposal Pending(string type, Kingdom from, Kingdom to) =>
            St.Proposals.FirstOrDefault(p => p.Type == type && p.From == from.StringId && p.To == to.StringId);

        private static Proposal AddProposal(string type, Kingdom from, Kingdom to)
        {
            St.Proposals.RemoveAll(p => p.Type == type && p.From == from.StringId && p.To == to.StringId);
            var p = new Proposal { Id = WorldSim.NewId("pr"), Type = type, From = from.StringId, To = to.StringId, Day = CampaignTime.Now.ToDays, ExpiresDay = CampaignTime.Now.ToDays + S.Get.ProposalLifetimeDays };
            St.Proposals.Add(p);
            return p;
        }

        // How the terms of a peace read to a ruler: who gives and who takes, never left to be guessed
        private static readonly Tpl TTermsDemanded = new Tpl("kingdom_statement/peace_terms_demanded.txt",
            "the proposer DEMANDS of the receiver: {terms}");
        private static readonly Tpl TTermsOffered = new Tpl("kingdom_statement/peace_terms_offered.txt",
            "the proposer OFFERS to give the receiver: {terms}");

        private static readonly Tpl TTermLastFief = new Tpl("kingdom_statement/peace_term_last_fief.txt",
            " (the LAST town or castle of the realm that gives it: that realm is left with none)");

        private static List<string> TermList(int daily, int days, int amount, string settlementId, bool last = false)
        {
            var parts = new List<string>();
            if (daily > 0) parts.Add($"a tribute of {daily} denars a day for {days} days");
            if (amount > 0) parts.Add($"{amount} denars in reparations");
            if (!string.IsNullOrEmpty(settlementId)) parts.Add($"the handing over of {Settlement.Find(settlementId)?.Name}" + (last ? TTermLastFief.Text : ""));
            return parts;
        }

        private static List<string> TermListEs(int daily, int days, int amount, string settlementId, bool last = false)
        {
            var parts = new List<string>();
            if (daily > 0) parts.Add(L.F("AIL_fbf8b45d", $"un tributo de {daily} denarios al día durante {days} días"));
            if (amount > 0) parts.Add(L.F("AIL_555e7801", $"{amount} denarios de reparaciones"));
            if (!string.IsNullOrEmpty(settlementId))
                parts.Add(last ? L.F("AIL_e7b1c77c", $"la entrega de {Settlement.Find(settlementId)?.Name}, que es la última ciudad o castillo de quien la entrega")
                               : L.F("AIL_5c924813", $"la entrega de {Settlement.Find(settlementId)?.Name}"));
            return parts;
        }

        private static List<string> Demands(Proposal p, bool es) =>
            es ? TermListEs(p.DailyTribute, p.TributeDays, p.Reparations, p.SettlementId, p.SettlementLast) : TermList(p.DailyTribute, p.TributeDays, p.Reparations, p.SettlementId, p.SettlementLast);
        private static List<string> Offers(Proposal p, bool es) =>
            es ? TermListEs(p.OfferDailyTribute, p.OfferTributeDays, p.OfferReparations, p.OfferSettlementId, p.OfferSettlementLast)
               : TermList(p.OfferDailyTribute, p.OfferTributeDays, p.OfferReparations, p.OfferSettlementId, p.OfferSettlementLast);

        private static string Terms(Proposal p)
        {
            var parts = Demands(p, false);
            if (p.Type == "peace")
            {
                // a peace can go either way, so each side of it is labelled
                var offered = Offers(p, false);
                var both = new List<string>();
                if (offered.Count > 0) both.Add(TTermsOffered.Format("terms", string.Join(", ", offered)));
                if (parts.Count > 0) both.Add(TTermsDemanded.Format("terms", string.Join(", ", parts)));
                return both.Count == 0 ? "" : " (" + string.Join("; ", both) + ")";
            }
            if (p.TradeYears > 0) parts.Add($"for {p.TradeYears} year(s)");
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
        }

        private static string TermsEs(Proposal p)
        {
            var parts = Demands(p, true);
            if (p.Type == "peace")
            {
                var offered = Offers(p, true);
                var both = new List<string>();
                if (offered.Count > 0) both.Add(L.F("AIL_e7b1c766", $"ofrece {string.Join(", ", offered)}"));
                if (parts.Count > 0) both.Add(L.F("AIL_e7b1c767", $"exige {string.Join(", ", parts)}"));
                return both.Count == 0 ? "" : " (" + string.Join("; ", both) + ")";
            }
            if (p.TradeYears > 0) parts.Add(L.F("AIL_fd19d606", $"durante {p.TradeYears} año(s)"));
            return parts.Count == 0 ? "" : " (" + string.Join(", ", parts) + ")";
        }

        /// <summary>The terms of a peace as its recipient, the player, reads them: what he is offered and what is asked of him.</summary>
        private static string TermsForPlayer(Proposal p)
        {
            var both = new List<string>();
            var offered = Offers(p, true); var demanded = Demands(p, true);
            if (offered.Count > 0) both.Add(L.F("AIL_e7b1c768", $"te ofrece {string.Join(", ", offered)}"));
            if (demanded.Count > 0) both.Add(L.F("AIL_e7b1c769", $"te exige {string.Join(", ", demanded)}"));
            return both.Count == 0 ? "" : " (" + string.Join("; ", both) + ")";
        }

        private static string TypeEn(string t) => t == "peace" ? "peace" : t == "alliance" ? "an alliance" : t == "trade" ? "a trade agreement" : t == "tribute" ? "a tribute" : t == "reparations" ? "reparations" : "territory";
        private static string TypeEs(string t) => t == "peace" ? L.T("AIL_695a2835", "la paz") : t == "alliance" ? L.T("AIL_27bd042f", "una alianza") : t == "trade" ? L.T("AIL_08285b4c", "un acuerdo comercial") : t == "tribute" ? L.T("AIL_2d5ee223", "un tributo") : t == "reparations" ? "reparaciones" : L.T("AIL_81b3cd7d", "un territorio");
        private static bool IsDemand(string t) => t == "tribute" || t == "reparations" || t == "territory";

        public static string DescribeAllProposals()
        {
            double now = CampaignTime.Now.ToDays;
            return string.Join("\n", St.Proposals.Select(p => $"- {p.From} {(IsDemand(p.Type) ? "demands" : "proposes")} {TypeEn(p.Type)}{Terms(p)} {(IsDemand(p.Type) ? "from" : "to")} {p.To} ({(int)(now - p.Day)} day(s) ago, unanswered)"));
        }

        // ============================================================ daily tick

        /// <summary>One step of the daily tick. Whatever it throws is logged and the next step still runs: nothing reaches the campaign tick.</summary>
        private static void Step(string what, Action act)
        {
            try { act(); }
            catch (Exception ex) { Log.Error("[diplomacia] tick diario, " + what, ex); }
        }

        /// <summary>
        /// A state read from a damaged or hand-edited file can carry null where a list belongs, or null entries.
        /// Everything in this file takes those lists for granted, so they are put right here, once a day.
        /// </summary>
        private static void Heal()
        {
            var st = St;
            if (st == null) return;
            if (st.Proposals == null) st.Proposals = new List<Proposal>();
            st.Proposals.RemoveAll(p => p == null || p.Type == null || p.From == null || p.To == null);
            if (st.StatementQueue == null) st.StatementQueue = new List<QueuedStatement>();
            st.StatementQueue.RemoveAll(q => q == null || q.KingdomId == null);
            if (st.Statements == null) st.Statements = new List<RulerStatement>();
            st.Statements.RemoveAll(s => s == null);
            if (st.Events == null) st.Events = new List<WorldEvent>();
            st.Events.RemoveAll(e => e == null);
            foreach (var e in st.Events) if (e.Engagement == null) e.Engagement = new Dictionary<string, int>();
            if (st.Tributes == null) st.Tributes = new List<Tribute>();
            st.Tributes.RemoveAll(t => t == null);
            if (st.Refused == null) st.Refused = new Dictionary<string, double>();
            if (st.PeaceNotes == null) st.PeaceNotes = new Dictionary<string, List<string>>();
            if (st.WarLastCapture == null) st.WarLastCapture = new Dictionary<string, double>();
            if (st.AllianceSince == null) st.AllianceSince = new Dictionary<string, double>();
            if (st.KingdomSeenDay == null) st.KingdomSeenDay = new Dictionary<string, double>();
            if (st.WarSeenDay == null) st.WarSeenDay = new Dictionary<string, double>();
            if (st.ForcedTruceUntil == null) st.ForcedTruceUntil = new Dictionary<string, double>();
            if (st.LastStatementDay == null) st.LastStatementDay = new Dictionary<string, double>();
            if (st.WarFatigue == null) st.WarFatigue = new Dictionary<string, float>();
            if (st.Stance == null) st.Stance = new Dictionary<string, int>();
        }

        public static void OnDailyTick(double now)
        {
            if (Campaign.Current == null || St == null) return;
            Step("estado cargado", Heal);
            Step("propuestas caducadas", () => ExpireProposals(now));
            Step("esperas y fechas de reinos que ya no están", () => PruneRefusals(now));
            Step("guerras sin fecha", () => TrackWars(now));
            CleanPacts();           // catches and logs by itself
            Step("restos de reinos que ya no están", () => PruneGone());
            bool on = false;
            Step("ajustes", () => on = Enabled);
            if (!on) return;
            Step("pactos entre reinos en guerra", EndPactsAtWar);
            ForcedPeace(now);       // catches and logs by itself
            Step("propuestas al jugador", NotifyPlayer);
            Step("turnos de declaración", () => QueueStatements(now));
        }

        private static void ExpireProposals(double now)
        {
            foreach (var p in St.Proposals.Where(x => now >= x.ExpiresDay).ToList())
            {
                St.Proposals.Remove(p);
                // Left unanswered by someone who had a turn (or, for the player, was shown it): the same wait as a
                // refusal before it can be made again. One whose ruler never got to speak was not refused.
                bool hadTurn = p.Seen || p.Notified;
                if (hadTurn) MarkRefused(p.Type, p.From, p.To, now);
                Log.Info($"[diplomacia] propuesta caducada{(hadTurn ? "" : " sin que su destinatario llegara a tener turno (no cuenta como rechazo)")}: {p.From} → {p.To} ({p.Type})");
            }
        }

        /// <summary>Proposals aimed at the player as ruler: the player decides through a popup.</summary>
        private static void NotifyPlayer()
        {
            var mine = PlayerRealm;
            if (!PlayerRules(mine) || !WorldSim.OnMap()) return;
            foreach (var p in St.Proposals.Where(x => x.To == mine.StringId && !x.Notified).ToList())
            {
                p.Notified = true;
                AskPlayer(p);
            }
        }

        // A proposal this close to running out, whose ruler has not had a turn yet, no longer waits on the dice
        private const int LastCallDays = 3;

        private static bool IsAnswer(QueuedStatement q) => q.EventId == null && q.Why != "wars";

        /// <summary>Whole days since this ruler last spoke. One who never has waits longest of all.</summary>
        private static int Waited(Kingdom k, double now) =>
            St.LastStatementDay.TryGetValue(k.StringId, out var d) && d <= now ? (int)(now - d) : int.MaxValue;

        /// <summary>
        /// Who speaks today. Several rulers can respond to the same thing: they get queued and speak one at a time,
        /// a few game hours apart, and each one knows what the earlier ones said (OnHourlyTick).
        /// </summary>
        private static void QueueStatements(double now)
        {
            var cfg = S.Get;
            var queue = St.StatementQueue;
            queue.RemoveAll(q => now * 24 - q.DueHour > 48);       // turns that never got to go out
            var rulers = WorldSim.Kingdoms.Where(x => !PlayerRules(x)).ToList();
            var answers = new List<(Kingdom k, double expires)>();
            var candidates = new List<(Kingdom k, WorldEvent ev, int weight, string why)>();

            // 1) Pressure to answer: rulers with an unanswered proposal go first, the one closest to running out ahead
            foreach (var k in rulers)
            {
                // a ruler held prisoner still answers a peace offered to him, and nothing else
                bool held = RulerCaptive(k);
                var waiting = St.Proposals.Where(p => p.To == k.StringId && (!held || p.Type == "peace")).ToList();
                if (waiting.Count == 0 || !Ready(k, now, 1)) continue;
                bool lastCall = waiting.Any(p => !p.Seen && p.ExpiresDay - now <= LastCallDays);
                if (lastCall || Rng.Next(100) < cfg.ResponsePressurePercent) answers.Add((k, waiting.Min(p => p.ExpiresDay)));
            }

            // 2) Wars that wear a realm down. Nothing else gives a ruler a turn to think about his wars: without it a
            // king only speaks when an event or a proposal calls him, and a war nobody mentions goes on for years.
            if (cfg.WearyKingsSeekPeace)
                foreach (var k in rulers)
                {
                    if (RulerCaptive(k)) continue;
                    int urge = PeaceUrge(k);
                    if (urge > 0 && Ready(k, now, cfg.MinDaysBetweenStatements) && Rng.Next(100) < urge) candidates.Add((k, null, 900 + urge, "wars"));
                }

            // 3) Events that call for a ruler to speak out
            foreach (var ev in St.Events.Where(e => now - e.Day <= cfg.EngagementDays && e.Engagement.Count > 0).OrderByDescending(e => e.Importance))
                foreach (var kv in ev.Engagement.ToList())
                {
                    var k = WorldSim.FindKingdom(kv.Key);
                    if (k == null || PlayerRules(k) || RulerCaptive(k) || !Ready(k, now, cfg.MinDaysBetweenStatements)) continue;
                    if (Rng.Next(100) < kv.Value) candidates.Add((k, ev, ev.Importance * 100 + kv.Value, null));
                }

            int room = Math.Max(1, cfg.MaxStatementsPerDay) - queue.Count;
            double due = Math.Max(now * 24 + 1, queue.Count == 0 ? 0 : queue.Max(q => q.DueHour) + cfg.HoursBetweenStatements);
            // one turn per ruler, and none while their previous statement is still in flight
            bool Taken(Kingdom k) => queue.Any(q => q.KingdomId == k.StringId) || WorldSim.IsReserved("statement:" + k.StringId);
            void Add(Kingdom k, WorldEvent ev, string why)
            {
                queue.Add(new QueuedStatement { KingdomId = k.StringId, EventId = ev?.Id, DueHour = due, Why = why });
                due += Math.Max(1, cfg.HoursBetweenStatements);
                room--;
            }

            // Answers are outside the world-wide gap: held back by it they ran out unanswered, and that counted as
            // a refusal. They still share the cap per day and go out hours apart like everyone.
            foreach (var c in answers.OrderBy(c => c.expires))
            {
                if (room <= 0) return;
                if (!Taken(c.k)) Add(c.k, null, null);
            }
            if (room <= 0) return;

            if (cfg.MinDaysBetweenAnyStatements > 0)
            {
                // A gap for the whole world: one ruler at a time, and nobody until it has passed. The single turn
                // goes to whoever has waited longest since he last spoke, so no kingdom can keep it; how pressing
                // the matter is only breaks ties.
                if (queue.Any(q => !IsAnswer(q)) || !WorldGapPassed(now)) return;
                var open = candidates.Where(c => !Taken(c.k)).ToList();
                if (open.Count == 0) return;
                var pick = open.OrderByDescending(c => Waited(c.k, now)).ThenByDescending(c => c.weight).ThenBy(c => Rng.Next()).First();
                Add(pick.k, pick.ev, pick.why);
                return;
            }
            foreach (var c in candidates.OrderByDescending(c => c.weight + Rng.Next(50)))
            {
                if (room <= 0) break;
                if (!Taken(c.k)) Add(c.k, c.ev, c.why);
            }
        }

        // A statement asked for and not yet back. With a world-wide gap nobody else speaks until it lands, or two
        // rulers could speak an hour apart. It stops counting after ten real minutes or a change of campaign, in
        // case the reply never comes.
        private static DateTime _askedAt = DateTime.MinValue;
        private static int _askedSession = -1;
        private static bool _askedAnswer;
        private static bool InFlight => _askedSession == WorldSim.Session && (DateTime.UtcNow - _askedAt).TotalMinutes < 10;

        /// <summary>New or loaded campaign: nothing asked in the previous one counts here.</summary>
        public static void ResetSession()
        {
            _askedAt = DateTime.MinValue;
            _askedSession = -1;
            _askedAnswer = false;
            _seededSession = -1;
            // the state just loaded is put right before anything reads it
            try { Heal(); } catch (Exception ex) { Log.Error("[diplomacia] estado cargado", ex); }
        }

        /// <summary>
        /// The minimum days between two AI statements have passed, and none that counts is on its way. Answers to
        /// a pending proposal neither wait for this gap nor restart it. Always true with the setting at 0.
        /// </summary>
        private static bool WorldGapPassed(double now)
        {
            int gap = S.Get.MinDaysBetweenAnyStatements;
            if (gap <= 0) return true;
            if (InFlight && !_askedAnswer) return false;
            double last = St.LastStatementAnyDay;
            return last < 0 || last > now || now - last >= gap;      // last > now: an older save was loaded
        }

        /// <summary>Game hours since any AI ruler last spoke, answer or not. A huge number when nobody has.</summary>
        private static double HoursSinceAnyone(double hour)
        {
            double last = -1;
            foreach (var d in St.LastStatementDay.Values) if (d * 24 <= hour && d > last) last = d;
            return last < 0 ? double.MaxValue : hour - last * 24;
        }

        /// <summary>Every game hour: if a ruler's turn is due and the request queue is free, they speak.</summary>
        public static void OnHourlyTick()
        {
            try
            {
                if (Campaign.Current == null || St == null || St.StatementQueue.Count == 0) return;
                if (!Enabled) { St.StatementQueue.Clear(); return; }
                if (!WorldSim.CanCall) return;
                double hour = CampaignTime.Now.ToHours, now = CampaignTime.Now.ToDays;
                St.StatementQueue.RemoveAll(q => q == null || q.KingdomId == null);
                QueuedStatement next = null;
                if (S.Get.MinDaysBetweenAnyStatements <= 0) next = St.StatementQueue.OrderBy(q => q.DueHour).FirstOrDefault(q => q.DueHour <= hour);
                else
                {
                    // Enforced here whatever was queued and whatever the AI asked for: one at a time, hours apart,
                    // and the gap in days for everyone who is not answering a proposal.
                    if (InFlight || HoursSinceAnyone(hour) < S.Get.HoursBetweenStatements) return;
                    bool gapPassed = WorldGapPassed(now);
                    next = St.StatementQueue.OrderBy(q => q.DueHour).FirstOrDefault(q => q.DueHour <= hour && (gapPassed || IsAnswer(q)));
                }
                if (next == null) return;
                if (WorldSim.IsReserved("statement:" + next.KingdomId)) return;   // previous one hasn't landed yet, wait
                St.StatementQueue.Remove(next);
                var k = WorldSim.FindKingdom(next.KingdomId);
                if (k == null || k.IsEliminated || k.Leader == null || PlayerRules(k) || !Ready(k, now, 1)) return;
                WorldEvent ev = null;
                bool wars = next.Why == "wars";
                // taken prisoner after his turn was queued: he keeps it only to answer a peace
                bool held = RulerCaptive(k);
                if (held && (wars || next.EventId != null))
                {
                    // Not queued again: nothing is used up by dropping it. The event keeps his name on it and his
                    // wars are weighed again every day (QueueStatements), so he gets his turn once he is free.
                    Log.Info($"[diplomacia] turno de {k.Leader.Name} ({k.Name}) retirado: está preso{(Captor(k.Leader) is string c0 ? " de " + c0 : "")}. " +
                             (wars ? "Sus guerras se vuelven a sopesar cada día" : "El suceso sigue abierto para él") + " y hablará cuando quede libre.");
                    return;
                }
                if (wars)
                {
                    if (WarCount(k) == 0) return;                                          // the wars ended meanwhile
                }
                else if (next.EventId != null)
                {
                    ev = St.Events.FirstOrDefault(e => e.Id == next.EventId);
                    if (ev == null || !ev.Engagement.ContainsKey(k.StringId)) return;      // event is gone or they already spoke about it
                }
                else if (!St.Proposals.Any(p => p.To == k.StringId && (!held || p.Type == "peace"))) return;   // nothing left waiting for them
                MakeStatement(k, ev, wars);     // the occasion is used up when the reply is applied (ApplyStatement)
            }
            catch (Exception ex) { Log.Error("[diplomacia] turno de declaraciones", ex); }
        }

        private static bool Ready(Kingdom k, double now, int minDays) =>
            !St.LastStatementDay.TryGetValue(k.StringId, out var d) || now - d >= minDays;

        // ============================================================ ruler statement

        private static readonly Tpl TStatement = new Tpl("kingdom_statement/statement.txt",
@"You are {ruler}, ruler of {kingdom} in Calradia, the medieval world of Mount & Blade II: Bannerlord. You are about to issue a public statement as sovereign.

How a sovereign acts:
- The technical data below is the truth right now. Chronicles and earlier statements are only context: what was said may never have happened.
- Words are not actions. Announcing war, peace, an alliance or a demand changes NOTHING unless you put the matching action in ""actions"". If you only want to speak, use no actions.
- Every decision must serve your realm: its safety, its treasury, its honour, or your grip on the throne. Weigh strength, war weariness, who is already fighting whom, and your stance towards each crown. A weary or losing realm seeks peace; a strong and rested one presses its advantage.
- Your wars are listed one by one, with how long each has lasted and how tired your realm is. Tiredness is not a mood to be talked away: a weary realm that keeps every war going loses its lords, its towns and in the end its throne. When you are weary or worse, or fighting several kingdoms at once, seek peace in the wars you gain least from. When a weary enemy offers a fair peace and you have little left to gain, take it.
- If a proposal or demand is addressed to you, answer it now: accept it or reject it (and say why). Accepting a peace means accepting its terms.
- You speak as yourself: your character and culture show in the words. No modern language, no game terms, no numbers of troops or percentages.
- Your people are exactly the clans listed as yours in the hard facts about your realm. A lord of another realm, of a minor clan or of a mercenary company you did not hire is not yours, whatever his culture: do not call him yours, mourn him as yours, answer for him or fear him as a rival inside your realm. The mercenaries you did hire are paid swords, not vassals and not kin.
- Speak only of wars, truces and alliances that the data shows. Never say you are at peace, or that a truce holds, with a crown the data lists you as at war with: a statement that does is not published. An action aimed at a crown you cannot aim it at is thrown away: war on someone you are already fighting, peace with someone you are not at war with, anything the house rules forbid.

Actions you may take (each is an object in ""actions""; target_kingdom is always a kingdom id from the data):
- declare_war (target_kingdom): war, at once, no consent needed. It breaks any alliance or trade agreement with them.
- propose_peace (target_kingdom). Only against a realm you are at war with. A peace can carry a price, paid in either direction. Terms you DEMAND from them: daily_tribute + tribute_days (or tribute_years, in years of this world), reparations, settlement (a town or castle of theirs that lies within your reach: each war lists them, and a demand for any other is thrown away). Terms you OFFER to give them, to buy the peace: offer_daily_tribute + offer_tribute_days (or offer_tribute_years), offer_reparations (never more than your treasury holds), offer_settlement (a town or castle of your own realm, not under siege). Demands are for the side that is winning; the side that is losing or worn out and needs the peace offers a price when it can pay one, or asks with no terms at all; the fair price of each war is written next to it. Never demand and offer the same kind of term at once: such a proposal is thrown away, and so is one that offers what you do not have. If they have already proposed a peace to you and you propose one that asks no more than theirs gives, theirs is signed; if you propose different terms, yours replace theirs and they must answer yours.
- accept_peace / reject_peace (target_kingdom): answer THEIR pending peace proposal.
- propose_alliance / accept_alliance / reject_alliance / break_alliance (target_kingdom): allies stand together in war. Propose only to a crown you are on good terms with and share an enemy or an interest with.
- propose_trade / accept_trade / reject_trade / end_trade (target_kingdom, trade_years 1-5): only between realms at peace.
- demand_tribute (target_kingdom, daily_tribute, tribute_days or tribute_years) / accept_tribute / reject_tribute / end_tribute: outside a peace treaty, a tribute is paid only by a realm that fears you.
- demand_reparations (target_kingdom, reparations) / accept_reparations / reject_reparations: a single payment.
- The demand_ actions only ever take from the other realm and end no war. The offer_ fields belong to propose_peace alone: to pay for a peace, use propose_peace with them.
- demand_territory (target_kingdom, settlement = a town or castle of theirs on your border or near your lands; a demand for a distant one is thrown away) / transfer_territory / reject_territory.
- expel_clan (clan = a vassal clan of yours, never your own): cast it out of the realm for treason or defiance. A clan cast out is banished: it cannot return until you pardon it.
- pardon_clan (clan = one of the clans you banished): lift its banishment, so it may swear to you again. Only if it has made amends or you need its swords.
- grant_fief (settlement = a town or castle held by your own clan, clan = a vassal clan of yours): reward loyalty.
- confiscate_fief (settlement = a town or castle of one of your vassal clans): take it for the crown; they will resent it.
- set_tax (tax_percent from {tax_min} to {tax_max}; 0 removes it): an extra crown tax on your towns. More gold, but loyalty and prosperity suffer.
- enact_law (law_name, law_text in {language}, law_effects = a list of effects taken from the catalogue of law effects below): proclaim a law of your own. It is in force at once, with no vote. A law that gives with one hand takes with the other; at most {max_laws} laws at a time.
- repeal_law (law_name): abolish one of your laws in force.
- enact_policy / repeal_policy (policy = the id of an established policy from the lists below): adopt or abolish one of the old policies of the realm by decree, with no vote.
- declare_war_on_clan / make_peace_with_clan (clan = an independent clan that belongs to no kingdom, such as one you expelled or that rebelled).
- quarantine_settlement (settlement = a town, castle or village of yours where there is sickness, quarantine_days 5-60) / lift_quarantine (settlement): closing it stops the sickness from spreading but hurts trade and tempers.

Answer ONLY with JSON:
{ ""thoughts"": ""private, in English, 2-5 sentences: facts first (who am I at war with, who proposed what to me, how tired is my realm), then what I want and why"",
  ""statement"": ""the public statement, {min_len}-{max_len} characters, in {language}"",
  ""summary"": ""what you said and did, in ONE English sentence"",
  ""importance"": 1-4,
  ""actions"": [ { ""type"": ""..."", ""target_kingdom"": ""id"", ""settlement"": ""name"", ""clan"": ""name"", ""daily_tribute"": 0, ""tribute_days"": 0, ""tribute_years"": 0, ""reparations"": 0, ""offer_daily_tribute"": 0, ""offer_tribute_days"": 0, ""offer_tribute_years"": 0, ""offer_reparations"": 0, ""offer_settlement"": ""name"", ""trade_years"": 0, ""tax_percent"": 0, ""law_name"": ""..."", ""law_text"": ""..."", ""law_effects"": [ { ""effect"": ""id"", ""value"": 0, ""scope"": ""all"", ""target"": ""all"", ""min_clan_tier"": 0 } ], ""policy"": ""id"", ""quarantine_days"": 0 } ],
  ""relation_changes"": [ { ""kingdom"": ""id"", ""change"": -{max_stance} to {max_stance} } ],
  ""own_people"": [ ""every lord or clan you speak of as your own"" ],
  ""kingdom_engagement"": { ""kingdom_id"": 0-100 } }
Use only the fields an action needs. relation_changes: how your stance towards other crowns shifts because of this. own_people: each lord or clan your statement speaks of as yours (vassal, kin, sworn man, hired company); it is checked against the world, and a statement that claims someone who is not yours is not published. kingdom_engagement: the daily chance that another ruler answers you; only crowns this truly concerns (a neighbour, an enemy, an ally, the one you address).");

        private static readonly Tpl TRealmFacts = new Tpl("kingdom_statement/realm_facts.txt",
@"## Hard facts about your realm (exact, right now; nothing else is true)
- Your own clan, the ruling house: {ruling}.
- Your vassal clans, all of them: {vassals}.
- Mercenary companies in your pay (hired swords: not vassals, not kin; they leave when the contract ends): {mercenaries}.
- Nobody else is yours. Culture says where someone was born, not whom they serve: lords of your culture serve other crowns, and lords of other cultures may serve you.
- You are at war with: {wars}.
- You are allied with: {allies}.
- You have trade agreements with: {trade}.
- Last peace signed with each crown you have fought: {truces}.
- With every other kingdom you are simply at peace: {peace}.
{limits}");


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
            // Recent dealings with the player count
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
                sb.AppendLine($"- Clan {c.Name}{(c == k.RulingClan ? " (YOUR clan, the ruling house)" : c.IsUnderMercenaryService ? " (mercenaries in your pay: hired swords, not vassals)" : " (your vassals)")}, led by {c.Leader?.Name}" +
                              $"{(c == Clan.PlayerClan ? " — the clan of " + Hero.MainHero.Name : "")}: {(fiefs.Count > 0 ? string.Join(", ", fiefs) : "no fiefs")}" +
                              (c != k.RulingClan && c.Leader != null ? $"; relation with you {(int)c.Leader.GetRelation(k.Leader)}" : ""));
            }
            var banished = Banished.DescribeForRuler(k);
            if (banished.Length > 0) sb.AppendLine(banished);
            return sb.ToString().TrimEnd();
        }

        /// <summary>The exact lists a ruler needs to not claim what is not theirs: own clans, hired companies, wars, truces, alliances, limits.</summary>
        private static string DescribeFacts(Kingdom k)
        {
            string Led(Clan c) => c.Leader != null ? $"{c.Name} (led by {c.Leader.Name})" : c.Name.ToString();
            string Join(IEnumerable<string> xs, string none) { var l = xs.ToList(); return l.Count > 0 ? string.Join(", ", l) : none; }
            var clans = k.Clans.Where(c => !c.IsEliminated).ToList();
            var others = WorldSim.Kingdoms.Where(o => o != k).ToList();
            var truces = new List<string>();
            foreach (var o in others.Where(o => !k.IsAtWarWith(o)))
            {
                int since = PeaceDays(k, o);
                if (since < 0) continue;
                int left = TruceDaysLeft(k, o);
                truces.Add($"{o.StringId} ({since} day(s) ago{(left > 0 ? $"; truce, no war for {left} more days" : "")})");
            }
            // what changed lately, spelled out: older chronicles and statements still speak of how things were
            var changes = new List<string>();
            foreach (var o in others)
            {
                int war = WarDays(k, o), peace = PeaceDays(k, o);
                if (war >= 0 && war <= RecentChangeDays) changes.Add($"your war with {o.StringId} began {war} day(s) ago");
                else if (peace >= 0 && peace <= RecentChangeDays) changes.Add($"your peace with {o.StringId} was made {peace} day(s) ago");
            }
            string facts = TRealmFacts.Format(
                "ruling", k.RulingClan != null ? Led(k.RulingClan) : "none",
                "vassals", Join(clans.Where(c => c != k.RulingClan && !c.IsUnderMercenaryService).OrderByDescending(c => c.Tier).Select(Led), "none"),
                "mercenaries", Join(clans.Where(c => c.IsUnderMercenaryService).Select(Led), "none"),
                "wars", Join(others.Where(o => k.IsAtWarWith(o)).Select(o => o.StringId + WarSpan(k, o)), "nobody"),
                "allies", Join(others.Where(o => Allied(k, o)).Select(o => o.StringId), "nobody"),
                "trade", Join(others.Where(o => HasTrade(k, o)).Select(o => o.StringId), "nobody"),
                "truces", Join(truces, "none"),
                "peace", Join(others.Where(o => !k.IsAtWarWith(o)).Select(o => o.StringId), "nobody"),
                "limits", DescribeHouseRules(k)).TrimEnd();
            return changes.Count == 0 ? facts : facts + "\n" + TRecentChanges.Format("days", RecentChangeDays, "changes", string.Join("; ", changes));
        }

        /// <summary>Every name this ruler may rightly call their own: their clans (vassals and hired) and the people in them.</summary>
        private static List<string> OwnNames(Kingdom k)
        {
            var names = new List<string>();
            foreach (var c in k.Clans)
            {
                if (c == null || c.IsEliminated) continue;
                names.Add(c.Name?.ToString());
                foreach (var h in c.Heroes)
                {
                    if (h == null) continue;
                    names.Add(h.Name?.ToString());
                    names.Add(h.FirstName?.ToString());
                }
            }
            return names.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct().ToList();
        }

        /// <summary>
        /// A ruler's statement against the world, before anything in it is carried out: someone claimed who is not
        /// theirs, or a war or alliance stated that does not exist. Returns what is wrong, or null.
        /// </summary>
        private static string StatementContradiction(Kingdom k, JObject j)
        {
            if (!S.Get.FactCheckEvents) return null;
            try
            {
                var model = Facts.BuildModel();
                // what this statement itself does is true by the time anyone reads it
                if (j["actions"] is JArray acts)
                    foreach (var a in acts.OfType<JObject>())
                    {
                        string type = ((string)(a["type"]?.Type == JTokenType.String ? a["type"] : null) ?? "").Trim().ToLowerInvariant();
                        var o = WorldSim.FindKingdom(Target(a));
                        if (o == null || o == k) continue;
                        if (type == "declare_war") model.AddWar(k.StringId, o.StringId);
                        else if (type == "accept_alliance") model.AddAlliance(k.StringId, o.StringId);
                        // a peace this statement really signs: it accepts their offer, or both ask for a plain one
                        else if (type == "accept_peace" || type == "propose_peace")
                        {
                            var theirs = Pending("peace", o, k);
                            // the same reading Resolve makes of it: terms offered or demanded count like any others
                            bool signs = theirs != null && TermsBroken(theirs, o, k) == null;
                            if (signs && type == "propose_peace")
                            {
                                var mine = ReadPeaceTerms(k, o, a, out _);
                                signs = mine != null && Covers(mine, theirs);
                            }
                            if (signs) model.Wars.Remove(FactCheck.Pair(k.StringId, o.StringId));
                        }
                    }
                var named = j["own_people"] is JArray arr ? arr.Where(x => x != null && x.Type == JTokenType.String).Select(x => (string)x).Take(20).ToList() : new List<string>();
                return FactCheck.CheckOwnPeople(named, OwnNames(k), model)
                       ?? FactCheck.CheckSummary((string)(j["summary"]?.Type == JTokenType.String ? j["summary"] : null), model, k.StringId);
            }
            catch (Exception ex) { Log.Error("[diplomacia] comprobando una declaración", ex); return null; }
        }

        /// <param name="peaceOnly">For a ruler held prisoner: only the peace proposals, the ones he may answer.</param>
        private static string DescribeProposalsTo(Kingdom k, bool peaceOnly = false)
        {
            double now = CampaignTime.Now.ToDays;
            var list = St.Proposals.Where(p => p.To == k.StringId && (!peaceOnly || p.Type == "peace")).ToList();
            return list.Count == 0 ? "(none)" : string.Join("\n", list.Select(p =>
                $"- {WorldSim.FindKingdom(p.From)?.Name} [id: {p.From}] {(IsDemand(p.Type) ? "DEMANDS" : "PROPOSES")} {TypeEn(p.Type)}{Terms(p)}, {(int)(now - p.Day)} day(s) ago → answer with " +
                (p.Type == "peace" ? "accept_peace or reject_peace" : p.Type == "alliance" ? "accept_alliance or reject_alliance" : p.Type == "trade" ? "accept_trade or reject_trade"
                 : p.Type == "tribute" ? "accept_tribute or reject_tribute" : p.Type == "reparations" ? "accept_reparations or reject_reparations" : "transfer_territory or reject_territory")
                + (p.Type == "peace" && p.WantedPlain && !IsPlain(p) ? "." + TWantedPlain.Text : p.Type == "peace" && p.Counter ? "." + TCounterPeace.Text : "")));
        }

        /// <param name="wars">Nothing called this ruler: the realm is worn by its wars and the turn is to decide about them.</param>
        public static void MakeStatement(Kingdom k, WorldEvent about, bool wars = false)
        {
            var cfg = S.Get;
            var ruler = k.Leader;
            bool held = RulerCaptive(k);
            var sys = new StringBuilder();
            sys.AppendLine(TStatement.Format("ruler", ruler.Name, "kingdom", k.Name, "language", cfg.Language, "min_len", cfg.StatementMinLength, "max_len", cfg.StatementMaxLength,
                "tax_min", cfg.KingdomTaxMinPercent, "tax_max", cfg.KingdomTaxMaxPercent, "max_stance", cfg.MaxStanceChange, "max_laws", cfg.MaxLawsPerKingdom));
            var lore = WorldLore.Text;
            if (!string.IsNullOrWhiteSpace(lore)) { sys.AppendLine(); sys.AppendLine("## The world"); sys.AppendLine(lore); }
            sys.AppendLine(); sys.AppendLine("## Who you are"); sys.AppendLine(DescribeRuler(ruler));
            sys.AppendLine(); sys.AppendLine($"## Your realm, {k.Name} [id: {k.StringId}] — clans and their fiefs"); sys.AppendLine(DescribeRealm(k));
            sys.AppendLine(); sys.AppendLine(Facts.Today());
            sys.AppendLine(); sys.AppendLine(DescribeFacts(k));
            var warsText = DescribeWarsFor(k);
            if (warsText.Length > 0) { sys.AppendLine(); sys.AppendLine(warsText); }
            sys.AppendLine(); sys.AppendLine("## All kingdoms (technical truth, right now)"); sys.AppendLine(WorldSim.DescribeKingdoms(k));
            sys.AppendLine(); sys.AppendLine(Facts.CalendarAndMap(new[] { k.StringId }));
            var lawsText = Laws.ForRuler(k);
            if (lawsText.Length > 0) { sys.AppendLine(); sys.AppendLine(lawsText); }
            sys.AppendLine(); sys.AppendLine("## PENDING PROPOSALS DIRECTED AT YOU (answer them)"); sys.AppendLine(DescribeProposalsTo(k, held));
            // the ones this ruler is shown now: he has had his turn on them, whatever he does with it
            var shown = St.Proposals.Where(p => p.To == k.StringId && (!held || p.Type == "peace")).ToList();
            // a peace of his that could not be signed: told once, on the first turn whose answer arrives
            var fell = St.PeaceNotes.TryGetValue(k.StringId, out var notes) && notes != null ? notes.ToList() : new List<string>();
            if (fell.Count > 0)
            {
                sys.AppendLine(); sys.AppendLine(TPeaceFellHeader.Text);
                foreach (var line in fell) sys.AppendLine("- " + line);
            }
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
            // What other rulers just said (above all on the same event), so this one can answer, back it or stand apart
            var others = St.Statements.Where(s => s.KingdomId != k.StringId && CampaignTime.Now.ToDays - s.Day <= 2 && !string.IsNullOrWhiteSpace(s.SummaryEn))
                                      .OrderByDescending(s => s.Day).Take(4).ToList();
            if (others.Count > 0)
            {
                sys.AppendLine(); sys.AppendLine("## What other rulers have said in the last two days (you have heard it: answer it, back it or set yourself apart, but do not echo it)");
                foreach (var s in others) sys.AppendLine($"- {s.RulerName} of {WorldSim.FindKingdom(s.KingdomId)?.Name}: {s.SummaryEn}");
            }
            string captor = held ? Captor(ruler) : null;
            string task = held ? TTaskCaptive.Format("captor", captor != null ? TTaskCaptiveOf.Format("captor", captor) : "")
                : wars ? TTaskWars.Text
                : about != null
                ? $"Speak out now about this development: {(string.IsNullOrWhiteSpace(about.SummaryEn) ? about.Title : about.SummaryEn)}"
                : "Proposals are waiting for your answer. Issue your statement now.";

            // a turn given only to answer proposals: outside the world-wide gap, and it does not restart it
            bool answer = about == null && !wars;
            bool asked = WorldSim.Ask(L.F("AIL_3b33d5b8", $"declaración de {ruler.Name}"), new List<ChatMessage> { new ChatMessage("system", sys.ToString()), new ChatMessage("user", task) }, 1100,
                j =>
                {
                    // Only use up the turn when a reply arrives. If the call fails, the ruler can speak another day
                    _askedSession = -1;
                    // Written for the ruler he was when it was asked. Taken prisoner, set free, dead or dethroned
                    // while the request was in flight, the answer is another man's: it is thrown away with the turn
                    // unspent, and whatever called him (a proposal, an event, his wars) calls him again as he is now.
                    if (k.IsEliminated || k.Leader != ruler || RulerCaptive(k) != held)
                    {
                        Log.Info($"[diplomacia] declaración de {ruler.Name} ({k.Name}) descartada: " +
                                 (k.IsEliminated ? "el reino ya no existe" : k.Leader != ruler ? "ya no es el soberano"
                                  : held ? "se pidió estando preso y ya está libre" : "ha caído preso mientras se escribía") + ". Su turno no se gasta.");
                        return;
                    }
                    St.LastStatementDay[k.StringId] = CampaignTime.Now.ToDays;
                    if (!answer) St.LastStatementAnyDay = CampaignTime.Now.ToDays;
                    if (about != null) about.Engagement.Remove(k.StringId);
                    if (fell.Count > 0 && St.PeaceNotes.TryGetValue(k.StringId, out var left) && left != null)
                    {
                        left.RemoveAll(fell.Contains);
                        if (left.Count == 0) St.PeaceNotes.Remove(k.StringId);
                    }
                    // a statement thrown away for contradicting the world answered nothing: those proposals still wait for a turn
                    if (ApplyStatement(k, j, null)) foreach (var p in shown) p.Seen = true;
                }, key: "statement:" + k.StringId, failed: () => _askedSession = -1);
            if (asked) { _askedAt = DateTime.UtcNow; _askedSession = WorldSim.Session; _askedAnswer = answer; }
        }

        // ============================================================ applying a statement

        /// <summary>Publishes a statement and carries out its actions. False when it was thrown away and nothing was done.</summary>
        private static bool ApplyStatement(Kingdom k, JObject j, string playerText)
        {
            if (k == null || k.IsEliminated || k.Leader == null) return false;
            var cfg = S.Get;
            var s = new RulerStatement
            {
                Id = WorldSim.NewId("st"), Day = CampaignTime.Now.ToDays, KingdomId = k.StringId, RulerName = k.Leader.Name.ToString(),
                Text = playerText ?? WorldSim.Clean((string)j["statement"], 1600), SummaryEn = WorldSim.Clean((string)j["summary"], 350),
                Importance = Math.Max(1, Math.Min(4, (int?)j["importance"] ?? 2)),
            };
            if (s.Text.Length < 5) return false;
            var thoughts = (string)j["thoughts"];
            if (!string.IsNullOrWhiteSpace(thoughts)) Log.Info($"[diplomacia] [piensa] {s.RulerName}: {thoughts}");
            // An AI ruler who claims someone else's lord, or a war that does not exist, is not heard. The player's words are theirs
            if (playerText == null)
            {
                string wrong = StatementContradiction(k, j);
                if (wrong != null) { Log.Warn($"[diplomacia] declaración de {s.RulerName} descartada por contradecir el mundo: {wrong} | {s.SummaryEn}"); return false; }
            }

            // an AI ruler held prisoner can answer a peace and nothing more
            bool held = playerText == null && RulerCaptive(k);
            if (j["actions"] is JArray acts)
                foreach (var a in acts.OfType<JObject>().Take(4))
                {
                    try
                    {
                        if (held)
                        {
                            string type = (Str(a["type"]) ?? "").Trim().ToLowerInvariant();
                            if (type != "accept_peace" && type != "reject_peace" && type != "propose_peace")
                            { Log.Warn($"[diplomacia] {s.RulerName} está preso: acción {type} ignorada"); continue; }
                        }
                        var r = Resolve(k, a);
                        if (r != null) { s.Results.Add(r); if (s.Importance < 3) s.Importance = 3; }
                    }
                    catch (Exception ex) { Log.Error($"[diplomacia] acción de {s.RulerName}", ex); }
                }

            if (j["relation_changes"] is JArray rc)
                foreach (var x in rc.OfType<JObject>())
                {
                    var o = WorldSim.FindKingdom(Target(x));
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
            return true;
        }

        private static bool HasDemands(Proposal p) => p.DailyTribute > 0 || p.Reparations > 0 || !string.IsNullOrEmpty(p.SettlementId);
        private static bool HasOffers(Proposal p) => p.OfferDailyTribute > 0 || p.OfferReparations > 0 || !string.IsNullOrEmpty(p.OfferSettlementId);
        private static bool IsPlain(Proposal p) => !HasDemands(p) && !HasOffers(p);

        /// <summary>
        /// What a realm can hand over at once. Reparations come out of its ruler's purse and nowhere else (Pay moves
        /// only that), so the gold of its other clans is not counted: it could not be collected.
        /// </summary>
        private static int Treasury(Kingdom k) => Math.Max(0, k?.Leader?.Gold ?? 0);

        /// <summary>An action that carries any of the offer_ fields of propose_peace. A demand that does was meant as something else.</summary>
        private static bool CarriesOffer(JObject a)
        {
            string st = Str(a["offer_settlement"]);
            return I(a, "offer_daily_tribute", 0, 1) > 0 || I(a, "offer_reparations", 0, 1) > 0
                   || (!string.IsNullOrWhiteSpace(st) && WorldSim.Norm(st) != "name" && WorldSim.Norm(st) != "none");
        }

        private static string Str(JToken t) => t != null && t.Type == JTokenType.String ? (string)t : null;

        /// <summary>The terms of a peace as its proposer states them: what he demands and what he offers.</summary>
        private class PeaceTerms
        {
            public int Daily, Days, Reparations, OfferDaily, OfferDays, OfferReparations;
            public Settlement Fief, OfferFief;
            // the settlement is its giver's last town or castle, and the terms will say so
            public bool FiefLast, OfferFiefLast;
            public bool Plain => Daily == 0 && Reparations == 0 && Fief == null && OfferDaily == 0 && OfferReparations == 0 && OfferFief == null;
        }

        /// <summary>Why a peace cannot be: for the player in his language, for an AI ruler or a character in English.</summary>
        private class Why
        {
            public readonly string Es, En;
            public Why(string es, string en) { Es = es; En = en; }
        }

        private static readonly Tpl TWhyBothWays = new Tpl("kingdom_statement/peace_why_both_ways.txt",
            "the same kind of term cannot be demanded and offered at once");
        private static readonly Tpl TWhyGold = new Tpl("kingdom_statement/peace_why_gold.txt",
            "{payer} would have to pay {amount} denars and its treasury holds only {has}");
        private static readonly Tpl TWhyFief = new Tpl("kingdom_statement/peace_why_fief.txt",
            "«{name}» is not exactly one town or castle of {realm} that can be handed over now: no such place of theirs, more than one that the name fits, or it is besieged by a third realm");
        private static readonly Tpl TWhyLast = new Tpl("kingdom_statement/peace_why_last_fief.txt",
            "{name} has become the last town or castle of {realm}, and the terms did not say so");
        private static readonly Tpl TPeaceFell = new Tpl("kingdom_statement/peace_fell.txt",
            "The peace with {other} that {proposer} had proposed was NOT signed, and the proposal is off the table: {reason}. Nothing was paid or handed over and the war goes on. This is not a refusal: either of you may propose peace again, with terms that can be kept.");
        private static readonly Tpl TPeaceFellHeader = new Tpl("kingdom_statement/peace_fell_header.txt",
            "## PEACE THAT COULD NOT BE SIGNED (you are told this once)");

        private static Why WhyGold(Kingdom payer, int amount) => new Why(
            L.F("AIL_e7b1c77d", $"{payer.Name} tendría que pagar {amount} denarios y su tesoro solo tiene {Treasury(payer)}"),
            TWhyGold.Format("payer", payer.Name, "amount", amount, "has", Treasury(payer)));
        private static Why WhyFief(string name, Kingdom realm) => new Why(
            L.F("AIL_e7b1c76a", $"«{name}» no es una ciudad o castillo de {realm.Name} que se pueda entregar ahora"),
            TWhyFief.Format("name", name, "realm", realm.Name));
        private static Why WhyLast(Settlement st, Kingdom realm) => new Why(
            L.F("AIL_e7b1c77e", $"{st.Name} ha pasado a ser la última ciudad o castillo de {realm.Name}, y las condiciones no lo decían"),
            TWhyLast.Format("name", st.Name, "realm", realm.Name));

        /// <summary>The settlement named in a term, or null when none is: the placeholders of the answer format are not names.</summary>
        private static string FiefName(JObject a, string key)
        {
            var tk = a?[key];
            if (tk == null || tk.Type == JTokenType.Null) return null;
            // anything that is not text is kept as written, so it fails the lookup and the action is refused
            string name = (tk.Type == JTokenType.String ? (string)tk : tk.ToString(Newtonsoft.Json.Formatting.None)).Trim(), n = WorldSim.Norm(name);
            return name.Length == 0 || n == "name" || n == "none" || n == "null" || n == "0" || n == "false" ? null : name;
        }

        /// <summary>
        /// The one town or castle of <paramref name="realm"/> that the name means: by id, else by its whole name
        /// whatever the case, else the only one whose name begins with it. null when none or more than one fits.
        /// </summary>
        private static Settlement FindFief(string name, Kingdom realm)
        {
            name = (name ?? "").Trim();
            var all = Settlement.All.Where(x => x != null && (x.IsTown || x.IsCastle) && x.MapFaction == realm).ToList();
            var byId = all.Where(x => x.StringId == name).ToList();
            if (byId.Count == 1) return byId[0];
            string n = WorldSim.Norm(name);
            if (n.Length < 2) return null;
            var whole = all.Where(x => WorldSim.Norm(x.Name?.ToString()) == n).ToList();
            if (whole.Count > 0) return whole.Count == 1 ? whole[0] : null;
            var begins = all.Where(x => WorldSim.Norm(x.Name?.ToString()).StartsWith(n, StringComparison.Ordinal)).ToList();
            return begins.Count == 1 ? begins[0] : null;
        }

        private static bool IsLastFief(Settlement st, Kingdom realm) =>
            !Settlement.All.Any(x => x != null && x != st && (x.IsTown || x.IsCastle) && x.MapFaction == realm);

        /// <summary>
        /// Whether <paramref name="giver"/> can hand this town or castle to <paramref name="receiver"/> as a term of
        /// their peace. A siege laid by the receiver alone does not stand in the way (the besieger demanding the
        /// castle he is besieging): the game lifts it when the two make peace. SiegeEventCampaignBehavior.OnMakePeace
        /// calls FactionHelper.FinishAllRelatedHostileActions, which takes every besieging lord's party of one realm
        /// out of the camp before a settlement of the other, and a camp left empty ends the siege. A siege by anyone
        /// else would go on under the new owner, so that one blocks the handover.
        /// </summary>
        private static bool CanChangeHands(Settlement st, Kingdom giver, Kingdom receiver)
        {
            if (st == null || giver == null || receiver == null || !(st.IsTown || st.IsCastle) || st.MapFaction != giver) return false;
            if (!st.IsUnderSiege) return true;
            var camp = st.SiegeEvent?.BesiegerCamp;
            return camp != null && camp.GetInvolvedPartiesForEventType().All(x => x != null && x.MapFaction == receiver);
        }

        /// <summary>
        /// Reads the terms of a propose_peace from <paramref name="k"/> to <paramref name="o"/>, within the caps.
        /// null, with the reason, when they cannot stand: the same kind of term demanded and offered at once, a
        /// settlement named that is not exactly one town or castle of the realm that would give it (or that cannot
        /// change hands now), or a sum offered that the proposer does not have. A name that resolves to nothing is
        /// never read as "no such term": that would turn a peace with a price into a plain one.
        /// </summary>
        private static PeaceTerms ReadPeaceTerms(Kingdom k, Kingdom o, JObject a, out Why why)
        {
            var cfg = S.Get;
            why = null;
            int daysInYear = Math.Max(1, CampaignTime.DaysInYear);
            var t = new PeaceTerms
            {
                Daily = I(a, "daily_tribute", 0, cfg.MaxDailyTribute), Reparations = I(a, "reparations", 0, cfg.MaxReparations),
                OfferDaily = I(a, "offer_daily_tribute", 0, cfg.MaxDailyTribute), OfferReparations = I(a, "offer_reparations", 0, cfg.MaxReparations),
            };
            string demanded = FiefName(a, "settlement"), offered = FiefName(a, "offer_settlement");
            // judged on what was written, before any lookup
            if ((t.Daily > 0 && t.OfferDaily > 0) || (t.Reparations > 0 && t.OfferReparations > 0) || (demanded != null && offered != null))
            { why = new Why(L.T("AIL_e7b1c76b", "no se puede exigir y ofrecer a la vez la misma clase de condición"), TWhyBothWays.Text); return null; }
            if (demanded != null)
            {
                t.Fief = FindFief(demanded, o);
                if (!CanChangeHands(t.Fief, o, k)) { why = WhyFief(demanded, o); return null; }
                if (!WithinReach(t.Fief, k)) { why = WhyFar(t.Fief, k); return null; }
                t.FiefLast = IsLastFief(t.Fief, o);
            }
            if (offered != null)
            {
                t.OfferFief = FindFief(offered, k);
                if (!CanChangeHands(t.OfferFief, k, o)) { why = WhyFief(offered, k); return null; }
                t.OfferFiefLast = IsLastFief(t.OfferFief, k);
            }
            if (t.OfferReparations > Treasury(k))
            {
                why = new Why(L.F("AIL_e7b1c76c", $"{k.Name} ofrece {t.OfferReparations} denarios y su tesoro solo tiene {Treasury(k)}"),
                              TWhyGold.Format("payer", k.Name, "amount", t.OfferReparations, "has", Treasury(k)));
                return null;
            }
            t.Days = t.Daily > 0 ? TributeLength(a) : 0;
            t.OfferDays = t.OfferDaily > 0 ? FactCheck.TributeDays(I(a, "offer_tribute_days", 0, 100000), I(a, "offer_tribute_years", 0, 20), daysInYear, 10, 200) : 0;
            return t;
        }

        /// <summary>
        /// True when the peace <paramref name="theirs"/> already on the table gives this side everything it now
        /// asks for and asks of it no more than it now offers. Two plain ones are the simplest case.
        /// </summary>
        private static bool Covers(PeaceTerms mine, Proposal theirs)
        {
            if (mine.Daily > theirs.OfferDailyTribute || (mine.Daily > 0 && mine.Days > theirs.OfferTributeDays)) return false;
            if (mine.Reparations > theirs.OfferReparations) return false;
            if (mine.Fief != null && mine.Fief.StringId != theirs.OfferSettlementId) return false;
            if (theirs.DailyTribute > mine.OfferDaily || (theirs.DailyTribute > 0 && theirs.TributeDays > mine.OfferDays)) return false;
            if (theirs.Reparations > mine.OfferReparations) return false;
            return string.IsNullOrEmpty(theirs.SettlementId) || (mine.OfferFief != null && mine.OfferFief.StringId == theirs.SettlementId);
        }

        /// <summary>
        /// Why the peace <paramref name="p"/>, proposed by <paramref name="from"/> to <paramref name="to"/>, cannot
        /// be carried out in full right now, or null. Demands and offers are held to the same rule: the treasury and
        /// the map move on while a proposal waits, and a peace whose price cannot be paid whole is not signed.
        /// A realm's last town or castle only goes when the terms said it was the last (SettlementLast).
        /// </summary>
        private static Why TermsBroken(Proposal p, Kingdom from, Kingdom to)
        {
            if (p == null || from == null || to == null) return null;
            if (p.OfferReparations > Treasury(from)) return WhyGold(from, p.OfferReparations);
            if (p.Reparations > Treasury(to)) return WhyGold(to, p.Reparations);
            return FiefBroken(p.OfferSettlementId, from, to, p.OfferSettlementLast) ?? FiefBroken(p.SettlementId, to, from, p.SettlementLast);
        }

        private static Why FiefBroken(string settlementId, Kingdom giver, Kingdom receiver, bool saidLast)
        {
            if (string.IsNullOrEmpty(settlementId)) return null;
            var st = Settlement.Find(settlementId);
            if (!CanChangeHands(st, giver, receiver)) return WhyFief(st?.Name?.ToString() ?? settlementId, giver);
            return !saidLast && IsLastFief(st, giver) ? WhyLast(st, giver) : null;
        }

        /// <summary>
        /// A peace that could not be signed because its terms can no longer be kept: the proposal is dropped, which
        /// is no refusal (nothing has to wait), and both realms learn why. The player reads it on screen, as the
        /// outcome of his own action when he is <paramref name="k"/>, the one accepting; an AI ruler is told on his
        /// next turn. Returns the text for whoever was accepting.
        /// </summary>
        private static string DropBrokenPeace(Proposal p, Kingdom k, Kingdom o, Why broken)
        {
            St.Proposals.Remove(p);
            Log.Warn($"[diplomacia] la paz que {o.Name} proponía a {k.Name} no se firma y queda sin efecto: {broken.Es}");
            string text = L.F("AIL_e7b1c77f", $"La paz entre {k.Name} y {o.Name} no se firma y la propuesta queda sin efecto: {broken.Es}. No cuenta como rechazo: cualquiera de los dos puede volver a proponerla.");
            void Tell(Kingdom side, Kingdom other)
            {
                if (PlayerRules(side)) { if (side != k) WorldSim.Say(text, Colors.Red); }
                else AddPeaceNote(side, TPeaceFell.Format("other", other.Name, "proposer", o.Name, "reason", broken.En));
            }
            Tell(k, o); Tell(o, k);
            return text;
        }

        private static void AddPeaceNote(Kingdom k, string line)
        {
            if (k?.StringId == null || St?.PeaceNotes == null || string.IsNullOrWhiteSpace(line)) return;
            if (!St.PeaceNotes.TryGetValue(k.StringId, out var list) || list == null) St.PeaceNotes[k.StringId] = list = new List<string>();
            list.Add(line);
            while (list.Count > 4) list.RemoveAt(0);
        }

        /// <summary>
        /// Signs the peace <paramref name="p"/> that <paramref name="o"/> proposed and <paramref name="k"/> accepts,
        /// and carries out its terms in both directions. All or nothing: when a payment could not be made whole or
        /// a settlement could not change hands, nothing is signed and <paramref name="broken"/> says why. Returns
        /// what happened, or null (with no reason) when the game made no peace.
        /// </summary>
        private static string SignPeace(Kingdom k, Kingdom o, Proposal p, out Why broken)
        {
            broken = TermsBroken(p, o, k);
            if (broken != null) return null;
            string kn = k.Name.ToString();
            St.Proposals.RemoveAll(x => x.Type == "peace" && ((x.From == k.StringId && x.To == o.StringId) || (x.From == o.StringId && x.To == k.StringId)));
            St.Refused.Remove(RefusalKey("peace", k.StringId, o.StringId));
            // The game makes its first faction the payer (ApplyInternal: stance.SetDailyTributePaid(faction1, ...)).
            // A demanded tribute is paid by whoever accepts, an offered one by whoever proposed.
            if (p.DailyTribute > 0) Ours(() => MakePeaceAction.ApplyByKingdomDecision(k, o, p.DailyTribute, p.TributeDays));
            else if (p.OfferDailyTribute > 0) Ours(() => MakePeaceAction.ApplyByKingdomDecision(o, k, p.OfferDailyTribute, p.OfferTributeDays));
            else Ours(() => MakePeaceAction.Apply(k, o));
            // nothing is paid or handed over for a peace the game did not make
            if (k.IsAtWarWith(o)) { Log.Warn($"[diplomacia] el juego no ha aplicado la paz entre {kn} y {o.Name}: no se paga ni se entrega nada"); return null; }

            var fromK = new List<string>(); var fromO = new List<string>();
            if (p.Reparations > 0) { int paid = Pay(k, o, p.Reparations); if (paid > 0) fromK.Add(L.F("AIL_555e7801", $"{paid} denarios de reparaciones")); }
            if (p.OfferReparations > 0) { int paid = Pay(o, k, p.OfferReparations); if (paid > 0) fromO.Add(L.F("AIL_555e7801", $"{paid} denarios de reparaciones")); }
            HandOver(string.IsNullOrEmpty(p.SettlementId) ? null : Settlement.Find(p.SettlementId), k, o, fromK);
            HandOver(string.IsNullOrEmpty(p.OfferSettlementId) ? null : Settlement.Find(p.OfferSettlementId), o, k, fromO);
            if (p.DailyTribute > 0) fromK.Add(L.F("AIL_fbf8b45d", $"un tributo de {p.DailyTribute} denarios al día durante {p.TributeDays} días"));
            else if (p.OfferDailyTribute > 0) fromO.Add(L.F("AIL_fbf8b45d", $"un tributo de {p.OfferDailyTribute} denarios al día durante {p.OfferTributeDays} días"));
            WorldSim.ChangeStance(k.StringId, o.StringId, 10); WorldSim.ChangeStance(o.StringId, k.StringId, 10);
            Log.Info($"[diplomacia] paz entre {kn} y {o.Name}: {kn} da [{string.Join(", ", fromK)}], {o.Name} da [{string.Join(", ", fromO)}]");
            string text = L.F("AIL_64b1d2d4", $"{kn} y {o.Name} firman la paz") + ".";
            if (fromK.Count > 0) text += " " + L.F("AIL_e7b1c76d", $"{kn} da a {o.Name}: {string.Join(", ", fromK)}.");
            if (fromO.Count > 0) text += " " + L.F("AIL_e7b1c76d", $"{o.Name} da a {kn}: {string.Join(", ", fromO)}.");
            return text;
        }

        /// <summary>A settlement changes hands as a term of the peace just made. TermsBroken has already let it through.</summary>
        private static void HandOver(Settlement st, Kingdom giver, Kingdom receiver, List<string> given)
        {
            if (st == null) return;
            // The receiver's own siege of it ends with the peace. If a camp is somehow still there, its parties
            // (all the receiver's, or the terms would not have passed) leave it the way the game makes them leave.
            if (st.IsUnderSiege)
                try
                {
                    foreach (var party in st.SiegeEvent.BesiegerCamp.GetInvolvedPartiesForEventType().ToList())
                        if (party?.MobileParty != null) party.MobileParty.BesiegerCamp = null;
                }
                catch (Exception ex) { Log.Error("[diplomacia] levantando el asedio de una plaza cedida", ex); }
            if (st.MapFaction != giver || st.IsUnderSiege)
            { Log.Warn($"[diplomacia] {st.Name} no ha podido cambiar de manos tras la paz entre {giver.Name} y {receiver.Name}"); return; }
            ChangeOwnerOfSettlementAction.ApplyByDefault(receiver.Leader, st);
            given.Add(L.F("AIL_5c924813", $"la entrega de {st.Name}"));
        }

        /// <summary>
        /// War between two kingdoms, declared by the first: the pacts between them end, what was pending between
        /// them is dropped, and the game's own action runs as ours so the patch that guards AI wars lets it through.
        /// Main thread only. The caller has already decided the declaration is allowed.
        /// </summary>
        public static void DeclareWar(Kingdom k, Kingdom o)
        {
            if (k == null || o == null || k == o || k.IsAtWarWith(o)) return;
            EndAlliance(k, o); EndTrade(k, o);
            if (St?.Proposals != null)
                St.Proposals.RemoveAll(p => p != null && ((p.From == k.StringId && p.To == o.StringId) || (p.From == o.StringId && p.To == k.StringId)));
            Ours(() => DeclareWarAction.ApplyByDefault(k, o));
        }

        /// <summary>
        /// Why an AI ruler may not declare war on that kingdom right now, or null: the setting that shields the
        /// kingdom the player rules, then the house rules. A declaration the player makes is never stopped.
        /// </summary>
        public static string AiWarBlocked(Kingdom k, Kingdom o)
        {
            if (k == null || o == null || PlayerRules(k)) return null;
            if (PlayerRules(o) && !S.Get.AllowAiWarOnPlayer) return "guerra contra el jugador desactivada";
            return WarBlocked(k, o);
        }

        /// <summary>
        /// A number from an action, however the model wrote it: 1000, "1000", "1,000", "1.000", "1k", "5000 denars".
        /// Only the first number in a text counts. Always held between <paramref name="min"/> and <paramref name="max"/>.
        /// <paramref name="alt"/> are other keys the same value may come under, tried in order when the first is missing.
        /// </summary>
        private static int I(JObject a, string key, int min, int max, params string[] alt)
        {
            long v = 0;
            try
            {
                var t = a[key];
                for (int i = 0; (t == null || t.Type == JTokenType.Null) && alt != null && i < alt.Length; i++) t = a[alt[i]];
                if (t != null && t.Type != JTokenType.Null)
                {
                    if (t.Type == JTokenType.Integer) v = (long)t;
                    else if (t.Type == JTokenType.Float)
                    {
                        // clamp before the cast: an out-of-range double turns into long.MinValue
                        double d = (double)t;
                        v = double.IsNaN(d) ? 0 : (long)Math.Round(Math.Max(int.MinValue, Math.Min(int.MaxValue, d)));
                    }
                    else if (t.Type == JTokenType.String) v = FactCheck.ParseSignedAmount((string)t);
                    // a list or an object is not a number: it reads as none, and the caps decide
                }
            }
            catch { }
            return (int)Math.Max(min, Math.Min(max, v));
        }

        /// <summary>The kingdom an action is aimed at, under any of the names models give that field.</summary>
        private static string Target(JObject a)
        {
            foreach (var key in new[] { "target_kingdom", "target", "kingdom", "to_kingdom", "to" })
            {
                var t = a?[key];
                if (t == null || t.Type != JTokenType.String) continue;
                string s = ((string)t).Trim();
                if (s.Length > 0) return s;
            }
            return null;
        }

        /// <summary>A tribute's length in days of this world: "a year" is one Calradian year, however the AI wrote it.</summary>
        private static int TributeLength(JObject a) =>
            FactCheck.TributeDays(I(a, "tribute_days", 0, 100000), I(a, "tribute_years", 0, 20), Math.Max(1, CampaignTime.DaysInYear), 10, 200);

        /// <summary>Validates and runs one ruler action. Returns what happened (to show) or null if it doesn't apply.</summary>
        private static string Resolve(Kingdom k, JObject a)
        {
            var cfg = S.Get;
            string type = ((string)a["type"] ?? "").Trim().ToLowerInvariant();
            if (type.Length == 0 || type == "none") return null;
            var o = WorldSim.FindKingdom(Target(a));
            string kn = k.Name.ToString();
            string Bad(string why) { Log.Warn($"[diplomacia] {kn}: acción {type} ignorada ({why})"); return null; }
            // The game's own policies, through the same calls its votes end in, so they show in Kingdom > Policies
            string EnactPolicy(PolicyObject policy)
            {
                if (!Laws.PoliciesEnabled) return Bad("políticas del juego desactivadas");
                if (k.HasPolicy(policy)) return Bad("esa política ya está en vigor");
                k.AddPolicy(policy);
                Log.Info($"[leyes] {kn} adopta la política {policy.StringId}");
                return L.F("AIL_e7b1c5e8", $"{kn} adopta la política «{policy.Name}».");
            }
            string RepealPolicy(PolicyObject policy)
            {
                if (!Laws.PoliciesEnabled) return Bad("políticas del juego desactivadas");
                if (!k.HasPolicy(policy)) return Bad("esa política no está en vigor");
                k.RemovePolicy(policy);
                Log.Info($"[leyes] {kn} deroga la política {policy.StringId}");
                return L.F("AIL_e7b1c5e9", $"{kn} deroga la política «{policy.Name}».");
            }

            switch (type)
            {
                // ---------------------------------------------------- war
                case "declare_war":
                    if (o == null || o == k) return Bad(L.T("AIL_8a207caf", "reino destino no válido"));
                    if (k.IsAtWarWith(o)) return Bad(L.T("AIL_acfa98a8", "ya están en guerra"));
                    if (PlayerRules(o) && !cfg.AllowAiWarOnPlayer) return Bad(L.T("AIL_b809052a", "guerra contra el jugador desactivada"));
                    { string no = WarBlocked(k, o); if (no != null) return Bad(no); }
                    DeclareWar(k, o);
                    if (!k.IsAtWarWith(o)) return Bad("el juego no ha aplicado la declaración de guerra");
                    WorldSim.ChangeStance(o.StringId, k.StringId, -30);
                    return L.F("AIL_75b4abfe", $"{kn} declara la guerra a {o.Name}.");

                // ---------------------------------------------------- peace
                case "propose_peace":
                {
                    if (o == null || o == k || !k.IsAtWarWith(o)) return Bad(L.T("AIL_a305fa83", "no están en guerra"));
                    { string no = ProposalBlocked("peace", k, o); if (no != null) return Bad(no); }
                    var t = ReadPeaceTerms(k, o, a, out Why senseless);
                    if (t == null)
                    {
                        Bad(senseless.Es);
                        // the player is told why his proposal went nowhere; an AI ruler's is only logged
                        return PlayerRules(k) ? L.F("AIL_e7b1c76e", $"Tu propuesta de paz a {o.Name} no sale: {senseless.Es}.") : null;
                    }
                    var theirs = Pending("peace", o, k);
                    if (theirs != null && Covers(t, theirs))
                    {
                        // The peace they already proposed gives this side all it now asks for and asks of it no
                        // more than it now offers: two plain ones, or an offer to pay answered with a plain one.
                        // The player proposing a peace that is already offered to them has not accepted it: the
                        // offer is put in front of them again and they decide there. Nothing is signed for them.
                        if (PlayerRules(k))
                        {
                            theirs.Notified = true;
                            AskPlayer(theirs);
                            return IsPlain(theirs) ? L.F("AIL_e7b1c763", $"{o.Name} ya te había ofrecido la paz sin condiciones: tú decides si la aceptas.")
                                                   : L.F("AIL_e7b1c76f", $"{o.Name} ya te había propuesto esa paz{TermsForPlayer(theirs)}: tú decides si la aceptas.");
                        }
                        // Both asking for the same peace is a peace. Before, each offer sat waiting for the other side to
                        // say "accept" in so many words, and two rulers who wanted the same thing never got it. When
                        // the offer on the table is the player's, this is an AI ruler accepting what the player proposed:
                        // accept_peace signs the pending proposal exactly as its proposer wrote it (Pending(o, k)), never
                        // the terms just read here, which only had to be covered by it. The player consented to those
                        // terms when he proposed them, so no popup; accept_peace checks again that he can still keep
                        // them and tells him what was signed.
                        return Resolve(k, new JObject { ["type"] = "accept_peace", ["target_kingdom"] = o.StringId });
                    }
                    if (theirs != null && t.Plain)
                    {
                        // They want terms and this side asks for none. Theirs stays as the one offer on the table,
                        // no second proposal is left beside it, and this side is told so on its next turn.
                        theirs.WantedPlain = true;
                        return L.F("AIL_e7b1c764", $"{kn} pide la paz a {o.Name}, que mantiene sus condiciones{TermsEs(theirs)}.");
                    }
                    bool overPlain = false, counter = false;
                    if (theirs != null)
                    {
                        // Different terms from theirs: this proposal takes the place of theirs, whichever way the
                        // payments go, and they are told why. One peace on the table between two realms, the latest.
                        // Not a refusal: nothing has to wait.
                        overPlain = IsPlain(theirs); counter = !overPlain;
                        St.Proposals.Remove(theirs);
                    }
                    var p = AddProposal("peace", k, o);
                    p.DailyTribute = t.Daily; p.TributeDays = t.Days;
                    p.Reparations = t.Reparations;
                    p.SettlementId = t.Fief?.StringId;
                    p.OfferDailyTribute = t.OfferDaily; p.OfferTributeDays = t.OfferDays;
                    p.OfferReparations = t.OfferReparations;
                    p.OfferSettlementId = t.OfferFief?.StringId;
                    p.SettlementLast = t.FiefLast; p.OfferSettlementLast = t.OfferFiefLast;
                    p.WantedPlain = overPlain; p.Counter = counter;
                    return L.F("AIL_cab1eaa4", $"{kn} propone la paz a {o.Name}{TermsEs(p)}.");
                }
                case "accept_peace":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("peace", o, k);
                    if (p == null) return Bad("no hay propuesta de paz de ese reino");
                    if (!k.IsAtWarWith(o)) { St.Proposals.Remove(p); return Bad(L.T("AIL_e904786b", "ya no están en guerra")); }
                    // The peace signed is p, whole, as its proposer wrote it. SignPeace first checks that both sides
                    // can still keep it: every payment in full, every settlement still its giver's to hand over.
                    string signed = SignPeace(k, o, p, out Why broken);
                    if (broken != null) return DropBrokenPeace(p, k, o, broken);
                    if (signed == null) return Bad("el juego no ha aplicado la paz");
                    // an AI ruler took the peace the player had proposed: he is told so, with what was signed
                    if (PlayerRules(o)) WorldSim.Say(L.F("AIL_e7b1c780", $"{kn} acepta la paz que tú habías propuesto. {signed}"), Colors.Green);
                    return signed;
                }
                case "reject_peace":
                    return Reject("peace", k, o, L.F("AIL_c1de8329", $"{kn} rechaza la paz que ofrecía {o?.Name}."));

                // ---------------------------------------------------- alliances
                case "propose_alliance":
                    if (o == null || o == k || k.IsAtWarWith(o) || Allied(k, o)) return Bad("alianza no posible");
                    { string no = PactOfferBlocked(k, o) ?? ProposalBlocked("alliance", k, o) ?? AllianceBlocked(k, o); if (no != null) return Bad(no); }
                    AddProposal("alliance", k, o);
                    return L.F("AIL_f1ae2294", $"{kn} propone una alianza a {o.Name}.");
                case "accept_alliance":
                {
                    if (o == null) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    var p = Pending("alliance", o, k);
                    if (p == null) return Bad("no hay propuesta de alianza");
                    St.Proposals.Remove(p);
                    if (k.IsAtWarWith(o) || Alli == null) return Bad(L.T("AIL_1d0a19e2", "alianza imposible ahora"));
                    { string no = AllianceBlocked(k, o); if (no != null) return Bad(no); }
                    // The new alliance is sealed and seen to exist before anything else: only then, with "a new
                    // alliance replaces the old one", does a kingdom left over its limit end its oldest other one.
                    try { Ours(() => Alli.StartAlliance(o, k)); }
                    catch (Exception ex) { Log.Error($"[diplomacia] sellando la alianza entre {kn} y {o.Name}", ex); }
                    if (!Allied(k, o)) return Bad("el juego no ha sellado la alianza: no se rompe ninguna otra");
                    {
                        // normally set by the game's own event (OnAllianceStarted)
                        string pair = FactCheck.Pair(k.StringId, o.StringId);
                        if (!St.AllianceSince.ContainsKey(pair)) St.AllianceSince[pair] = CampaignTime.Now.ToDays;
                    }
                    St.Refused.Remove(RefusalKey("alliance", k.StringId, o.StringId));
                    WorldSim.ChangeStance(k.StringId, o.StringId, 15); WorldSim.ChangeStance(o.StringId, k.StringId, 15);
                    var done = new List<string> { L.F("AIL_47c0f5a2", $"{kn} y {o.Name} sellan una alianza.") };
                    done.AddRange(DropOldestAlliances(k, o));
                    return string.Join(" ", done);
                }
                case "reject_alliance":
                    return Reject("alliance", k, o, L.F("AIL_ba80efa7", $"{kn} rechaza la alianza que proponía {o?.Name}."));
                case "break_alliance":
                    if (o == null || !Allied(k, o)) return Bad("no son aliados");
                    EndAlliance(k, o);
                    WorldSim.ChangeStance(o.StringId, k.StringId, -20);
                    return L.F("AIL_9701c185", $"{kn} rompe su alianza con {o.Name}.");

                // ---------------------------------------------------- trade
                case "propose_trade":
                {
                    if (o == null || o == k || k.IsAtWarWith(o) || HasTrade(k, o)) return Bad("acuerdo comercial no posible");
                    { string no = PactOfferBlocked(k, o) ?? ProposalBlocked("trade", k, o) ?? TradeBlocked(k, o); if (no != null) return Bad(no); }
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
                    { string no = TradeBlocked(k, o); if (no != null) return Bad(no); }
                    Ours(() => Trade.MakeTradeAgreement(o, k, CampaignTime.YearsFromNow(Math.Max(1, p.TradeYears))));
                    St.Refused.Remove(RefusalKey("trade", k.StringId, o.StringId));
                    WorldSim.ChangeStance(k.StringId, o.StringId, 5); WorldSim.ChangeStance(o.StringId, k.StringId, 5);
                    return L.F("AIL_4e5e61ea", $"{kn} y {o.Name} firman un acuerdo comercial de {Math.Max(1, p.TradeYears)} año(s).");
                }
                case "reject_trade":
                    return Reject("trade", k, o, L.F("AIL_dace2712", $"{kn} rechaza el acuerdo comercial de {o?.Name}."));
                case "end_trade":
                    if (o == null || !HasTrade(k, o)) return Bad("no hay acuerdo comercial");
                    EndTrade(k, o);
                    return L.F("AIL_270778c1", $"{kn} da por terminado su acuerdo comercial con {o.Name}.");

                // ---------------------------------------------------- tribute
                case "demand_tribute":
                {
                    if (o == null || o == k) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    if (CarriesOffer(a)) return Bad("los campos offer_ solo valen en propose_peace: una exigencia no ofrece nada");
                    var p = AddProposal("tribute", k, o);
                    p.DailyTribute = I(a, "daily_tribute", 10, cfg.MaxDailyTribute); p.TributeDays = TributeLength(a);
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

                // ---------------------------------------------------- reparations
                case "demand_reparations":
                {
                    if (o == null || o == k) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    if (CarriesOffer(a)) return Bad("los campos offer_ solo valen en propose_peace: una exigencia no ofrece nada");
                    var p = AddProposal("reparations", k, o);
                    p.Reparations = I(a, "reparations", 100, cfg.MaxReparations, "amount");
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

                // ---------------------------------------------------- territory
                case "demand_territory":
                {
                    if (o == null || o == k) return Bad(L.T("AIL_52a68aba", "sin reino"));
                    if (CarriesOffer(a)) return Bad("los campos offer_ solo valen en propose_peace: una exigencia no ofrece nada");
                    var st = OrderSystem.FindSettlement((string)a["settlement"], x => (x.IsTown || x.IsCastle) && x.MapFaction == o);
                    if (st == null) return Bad(L.T("AIL_9b65eab4", "asentamiento no válido"));
                    if (!WithinReach(st, k)) return Bad(WhyFar(st, k).Es);
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

                // ---------------------------------------------------- internal politics
                case "expel_clan":
                {
                    var clan = FindClan(k, (string)a["clan"]);
                    if (clan == null || clan == k.RulingClan || clan.IsUnderMercenaryService) return Bad(L.T("AIL_ae4ebcf0", "clan no válido"));
                    if (clan == Clan.PlayerClan && !cfg.AllowAiExpelPlayer) return Bad(L.T("AIL_007c510b", "expulsar al jugador desactivado"));
                    ChangeKingdomAction.ApplyByLeaveKingdom(clan);
                    if (clan.Kingdom != k) Banished.Ban(clan, k);
                    return L.F("AIL_0d7f7775", $"{kn} expulsa del reino al clan {clan.Name}.");
                }
                case "pardon_clan":
                {
                    string cn = WorldSim.Norm((string)a["clan"]);
                    var clan = cn.Length < 2 ? null : Clan.All.FirstOrDefault(c => !c.IsEliminated && Banished.Find(c, k) != null
                                   && (c.StringId == (string)a["clan"] || WorldSim.Norm(c.Name.ToString()) == cn || WorldSim.Norm(c.Name.ToString()).Contains(cn) || cn.Contains(WorldSim.Norm(c.Name.ToString()))));
                    if (clan == null || !Banished.Pardon(clan, k)) return Bad(L.T("AIL_b7d2f012", "ese clan no está desterrado"));
                    return L.F("AIL_b7d2f013", $"{kn} perdona al clan {clan.Name}: su destierro queda levantado.");
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
                    // A ruler who names an old policy of the realm as a law wants that policy
                    var named = Laws.FindPolicy(name);
                    if (named != null && a["law_effects"] == null) return EnactPolicy(named);
                    bool replaces = St.Laws.Any(l => l.KingdomId == k.StringId && WorldSim.Norm(l.Name) == WorldSim.Norm(name));
                    if (!replaces && St.Laws.Count(l => l.KingdomId == k.StringId) >= cfg.MaxLawsPerKingdom) return Bad(L.T("AIL_8a3f11e5", "demasiadas leyes en vigor"));
                    var problems = new List<string>();
                    var effects = Laws.ParseEffects(a["law_effects"], problems);
                    if (problems.Count > 0) Log.Warn($"[leyes] {kn}, «{name}»: {string.Join("; ", problems)}");
                    St.Laws.RemoveAll(l => l.KingdomId == k.StringId && WorldSim.Norm(l.Name) == WorldSim.Norm(name));
                    var law = new KingdomLaw
                    {
                        Id = WorldSim.NewId("law"), KingdomId = k.StringId, Name = name, Text = text, EnactedDay = CampaignTime.Now.ToDays, AuthorName = k.Leader.Name.ToString(),
                        Effects = effects,
                    };
                    St.Laws.Add(law);
                    Laws.Invalidate();
                    var parts = Laws.DescribeAll(law);
                    Log.Info($"[leyes] {kn} promulga «{name}»: {(parts.Count > 0 ? string.Join("; ", parts) : "sin efectos")}");
                    return L.F("AIL_203a7d74", $"{kn} promulga la ley «{name}»") + (parts.Count > 0 ? " (" + string.Join("; ", parts) + ")" : "") + ".";
                }
                case "repeal_law":
                {
                    string n = WorldSim.Norm((string)a["law_name"] ?? (string)a["policy"]);
                    var law = St.Laws.FirstOrDefault(l => l.KingdomId == k.StringId && (WorldSim.Norm(l.Name) == n || (n.Length > 2 && (WorldSim.Norm(l.Name).Contains(n) || n.Contains(WorldSim.Norm(l.Name))))));
                    if (law == null)
                    {
                        var old = Laws.FindPolicy((string)a["law_name"] ?? (string)a["policy"]);
                        return old != null ? RepealPolicy(old) : Bad("ley no encontrada");
                    }
                    St.Laws.Remove(law);
                    Laws.Invalidate();
                    return L.F("AIL_59559c9d", $"{kn} deroga la ley «{law.Name}».");
                }
                case "enact_policy":
                {
                    var policy = Laws.FindPolicy((string)a["policy"] ?? (string)a["law_name"]);
                    return policy == null ? Bad("política no encontrada") : EnactPolicy(policy);
                }
                case "repeal_policy":
                {
                    var policy = Laws.FindPolicy((string)a["policy"] ?? (string)a["law_name"]);
                    return policy == null ? Bad("política no encontrada") : RepealPolicy(policy);
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
                        Diplomacy.Ours(() => DeclareWarAction.ApplyByDefault(k, clan));
                        return L.F("AIL_30aa9ee2", $"{kn} declara la guerra al clan {clan.Name}.");
                    }
                    if (!k.IsAtWarWith(clan)) return Bad(L.T("AIL_a305fa83", "no están en guerra"));
                    Diplomacy.Ours(() => MakePeaceAction.Apply(k, clan));
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

        /// <summary>An event says a clan rises against its crown. Only carried out if there really is a break.</summary>
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
            // the same proposal between these two now has to wait (RefusedProposalCooldownDays)
            MarkRefused(type, o.StringId, k.StringId, CampaignTime.Now.ToDays);
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

        // ============================================================ player as ruler

        /// <summary>Another crown proposes or demands something from the player, who decides through a popup.</summary>
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
                // a peace says which way each payment goes: what they offer the player and what they ask of him
                L.F("AIL_ab129b00", $"{from.Name} {verb} {TypeEs(p.Type)}{(p.Type == "peace" ? TermsForPlayer(p) : TermsEs(p))}.\n\nSi no contestas ahora, la propuesta sigue en pie {S.Get.ProposalLifetimeDays} días y puedes responder con una declaración.")
                + (p.Type == "peace" && p.Counter ? "\n\n" + L.T("AIL_e7b1c771", "Es su respuesta a la paz que tú habías propuesto: tu propuesta queda retirada y solo valen estas condiciones.") : "")
                + (p.Type == "peace" ? "\n\n" + L.T("AIL_e7b1c772", "Con una declaración también puedes proponer otras condiciones: exigir u ofrecer un pago, un tributo o una plaza. Tu propuesta sustituiría a esta.") : ""),
                true, true, "Aceptar", "Rechazar", () => Answer(accept), () => Answer(reject)), true);
        }

        // ============================================================ a peace talked out face to face

        private static readonly Tpl TChatNotAtWar = new Tpl("kingdom_statement/chat_peace_not_at_war.txt", "your realms are not at war, or one of you no longer rules");
        private static readonly Tpl TChatNoTreaty = new Tpl("kingdom_statement/chat_peace_no_treaty.txt", "no treaty can be drawn up right now");
        private static readonly Tpl TChatNotApplied = new Tpl("kingdom_statement/chat_peace_not_applied.txt", "the peace did not take effect");

        /// <summary>
        /// A peace that an AI ruler and the player, who rules, settled in a chat, with terms or with none. It is
        /// built and checked here, shown to the player by the caller, and signed only on his yes (SignChatPeace): it
        /// never enters the list of pending proposals. <paramref name="terms"/> has the fields of propose_peace as
        /// the AI ruler would write them: the plain ones are what the player gives, the offer_ ones what the AI
        /// ruler gives. Sums are held to the caps, so the treaty may carry less than was said: the player is shown
        /// the real figures. null, with the reason in English for the character, when it cannot be.
        /// </summary>
        public static Proposal DraftChatPeace(Kingdom theirs, Kingdom mine, JObject terms, out string why)
        {
            why = null;
            try
            {
                if (theirs == null || mine == null || theirs == mine || !theirs.IsAtWarWith(mine)) { why = TChatNotAtWar.Text; return null; }
                if (St?.Proposals == null || St.Refused == null) { why = TChatNoTreaty.Text; return null; }
                var t = ReadPeaceTerms(theirs, mine, terms ?? new JObject(), out Why no);
                if (t == null) { why = no.En; return null; }
                double now = CampaignTime.Now.ToDays;
                var p = new Proposal
                {
                    Id = WorldSim.NewId("pr"), Type = "peace", From = theirs.StringId, To = mine.StringId, Day = now, ExpiresDay = now, Notified = true,
                    DailyTribute = t.Daily, TributeDays = t.Days, Reparations = t.Reparations, SettlementId = t.Fief?.StringId, SettlementLast = t.FiefLast,
                    OfferDailyTribute = t.OfferDaily, OfferTributeDays = t.OfferDays, OfferReparations = t.OfferReparations, OfferSettlementId = t.OfferFief?.StringId, OfferSettlementLast = t.OfferFiefLast,
                };
                // a treaty that could not be signed as it stands is not put in front of the player
                var broken = TermsBroken(p, theirs, mine);
                if (broken != null) { why = broken.En; return null; }
                return p;
            }
            catch (Exception ex) { Log.Error("[diplomacia] redactando una paz hablada", ex); why = TChatNoTreaty.Text; return null; }
        }

        /// <summary>The terms of a chat peace as the player reads them before signing: who pays whom, or that nobody does.</summary>
        public static string ChatPeaceText(Proposal p)
        {
            var from = WorldSim.FindKingdom(p.From);
            return IsPlain(p)
                ? L.F("AIL_e7b1c781", $"{from?.Name} te propone firmar la paz ahora, sin condiciones: nadie paga ni entrega nada.\n\nSi firmas, la guerra termina en el acto. Si no, no cambia nada.")
                : L.F("AIL_e7b1c774", $"{from?.Name} te propone firmar la paz ahora{TermsForPlayer(p)}.\n\nSi firmas, se paga y se entrega en el acto. Si no, no cambia nada.");
        }

        /// <summary>The terms of a chat peace in English, for the character's own record of it.</summary>
        public static string ChatPeaceNote(Proposal p) => IsPlain(p) ? "none" : Terms(p).Trim(' ', '(', ')');

        /// <summary>
        /// The player said yes to a chat peace: checked again and signed, all of it or none. null when it no longer
        /// can be, with the reason in English for the character and, when there is one to show, for the player.
        /// </summary>
        public static string SignChatPeace(Proposal p, out string why, out string whyForPlayer)
        {
            why = null; whyForPlayer = null;
            try
            {
                var theirs = WorldSim.FindKingdom(p.From); var mine = WorldSim.FindKingdom(p.To);
                if (theirs == null || mine == null || !PlayerRules(mine) || theirs.Leader == null || !theirs.IsAtWarWith(mine)) { why = TChatNotAtWar.Text; return null; }
                string r = SignPeace(mine, theirs, p, out Why broken);
                if (broken != null)
                {
                    Log.Warn($"[diplomacia] la paz hablada entre {mine.Name} y {theirs.Name} no se firma: {broken.Es}");
                    why = broken.En; whyForPlayer = broken.Es; return null;
                }
                if (r == null) { why = TChatNotApplied.Text; return null; }
                NewsStore.Add(new NewsItem
                {
                    Type = "statement", Importance = 3,
                    Text = $"{Hero.MainHero.Name}, ruler of {mine.Name}, and {theirs.Leader?.Name}, ruler of {theirs.Name}, signed a peace face to face. Terms, with {theirs.Name} as the proposer and {mine.Name} as the receiver: {(IsPlain(p) ? "none" : Terms(p).Trim(' ', '(', ')'))}.",
                    TitleEs = L.F("AIL_e7b1c775", $"Paz entre {mine.Name} y {theirs.Name}"), TextEs = r, Kingdoms = new List<string> { mine.StringId, theirs.StringId },
                });
                return r;
            }
            catch (Exception ex) { Log.Error("[diplomacia] firmando una paz hablada", ex); why = TChatNotApplied.Text; return null; }
        }

        private static readonly Tpl TPlayerStatement =new Tpl("kingdom_statement/player_statement_analysis.txt",
@"{ruler}, ruler of {kingdom} [id: {kingdom_id}], has issued the public statement below. You are the chancellor: turn the words into the actions they really contain, nothing more.
- Use only what the statement clearly says. If it only speaks and decides nothing, return no actions.
- A peace can carry a price in either direction, and the two must never be mixed up. What the ruler DEMANDS that the other realm pay or hand over goes in daily_tribute, tribute_days or tribute_years, reparations, settlement (a town or castle of theirs). What the ruler OFFERS to pay or hand over himself goes in offer_daily_tribute, offer_tribute_days or offer_tribute_years, offer_reparations, offer_settlement (a town or castle of his own realm). ""I will pay you 5000 denars for peace"" is offer_reparations 5000; ""pay me 5000 and there will be peace"" is reparations 5000. Use an offer_ field only when the statement plainly says the ruler himself pays or gives. Accepting a proposal that is pending is accept_peace, not a new propose_peace.
- The same action list and fields as any sovereign: declare_war, propose_peace (with optional daily_tribute, tribute_days or tribute_years, reparations, settlement, offer_daily_tribute, offer_tribute_days or offer_tribute_years, offer_reparations, offer_settlement), accept_peace, reject_peace, propose_alliance, accept_alliance, reject_alliance, break_alliance, propose_trade (trade_years), accept_trade, reject_trade, end_trade, demand_tribute (daily_tribute, tribute_days or tribute_years), accept_tribute, reject_tribute, end_tribute, demand_reparations (reparations), accept_reparations, reject_reparations, demand_territory (settlement), transfer_territory, reject_territory, expel_clan (clan), grant_fief (settlement, clan), confiscate_fief (settlement), set_tax (tax_percent), enact_law (law_name, law_text, law_effects = a list of effects from the catalogue of law effects below), repeal_law (law_name), enact_policy (policy = id of an established policy from the lists below), repeal_policy (policy), declare_war_on_clan (clan), make_peace_with_clan (clan), quarantine_settlement (settlement, quarantine_days), lift_quarantine (settlement).
- target_kingdom must be a kingdom id from the data.
Answer ONLY with JSON: { ""summary"": ""what was said and decided, ONE English sentence"", ""importance"": 1-4, ""actions"": [ { ""type"": ""..."", ""target_kingdom"": ""id"", ""settlement"": ""name"", ""clan"": ""name"", ""daily_tribute"": 0, ""tribute_days"": 0, ""reparations"": 0, ""offer_daily_tribute"": 0, ""offer_tribute_days"": 0, ""offer_reparations"": 0, ""offer_settlement"": ""name"", ""trade_years"": 0, ""tax_percent"": 0, ""law_name"": ""..."", ""law_text"": ""..."", ""law_effects"": [ { ""effect"": ""id"", ""value"": 0, ""scope"": ""all"", ""target"": ""all"", ""min_clan_tier"": 0 } ], ""policy"": ""id"", ""quarantine_days"": 0 } ], ""kingdom_engagement"": { ""kingdom_id"": 0-100 } }
For a law, law_name and law_text are the ruler's own words; choose law_effects that do what those words say, within the ranges.");

        public static void Touch() { var _ = TStatement.Key + TPlayerStatement.Key + TRealmFacts.Key; }

        public static bool PlayerCanDeclare => Enabled && PlayerRules(PlayerRealm);

        /// <summary>Player's diplomatic statement (rulers only): free text that the AI turns into actions.</summary>
        public static void PlayerStatement()
        {
            var k = PlayerRealm;
            if (!PlayerCanDeclare) { WorldSim.Say(L.T("AIL_76f7a56d", "Solo quien reina puede hacer una declaración."), Colors.Red); return; }
            InformationManager.ShowTextInquiry(new TextInquiryData(L.T("AIL_fec92cb7", "Declaración de la corona"),
                L.T("AIL_f00983f3", "Escribe lo que proclamas ante Calradia. Lo que decidas con claridad (guerra, paz, alianza, tributo, impuestos, feudos…) se llevará a cabo; lo demás quedará como palabras.")
                + "\n\n" + L.T("AIL_e7b1c773", "Para la paz puedes exigir o también ofrecer: un pago de una vez, un tributo diario o una ciudad o castillo de tu reino. Di con claridad quién paga a quién."),
                true, true, L.T("AIL_e5c9b3ad", "Proclamar"), L.T("AIL_e5c9b3a4", "Cancelar"), text =>
                {
                    text = (text ?? "").Trim();
                    if (text.Length < 10) return;
                    var sys = new StringBuilder();
                    sys.AppendLine(TPlayerStatement.Format("ruler", Hero.MainHero.Name, "kingdom", k.Name, "kingdom_id", k.StringId));
                    sys.AppendLine(); sys.AppendLine($"## The realm of {k.Name}: clans and fiefs"); sys.AppendLine(DescribeRealm(k));
                    sys.AppendLine(); sys.AppendLine(Facts.Today());
                    sys.AppendLine(); sys.AppendLine("## All kingdoms (technical truth)"); sys.AppendLine(WorldSim.DescribeKingdoms());
                    sys.AppendLine(); sys.AppendLine(Facts.CalendarAndMap(new[] { k.StringId }));
                    var lawsText = Laws.ForRuler(k);
                    if (lawsText.Length > 0) { sys.AppendLine(); sys.AppendLine(lawsText); }
                    sys.AppendLine(); sys.AppendLine("## Pending proposals directed at this realm"); sys.AppendLine(DescribeProposalsTo(k));
                    WorldSim.Say(L.T("AIL_ebf54145", "Tus heraldos llevan la declaración a todos los rincones…"), Colors.Cyan);
                    // With important: true it waits behind a running request instead of being dropped, and retries once if it fails
                    WorldSim.Ask(L.T("AIL_100794a8", "declaración del jugador"), new List<ChatMessage> { new ChatMessage("system", sys.ToString()), new ChatMessage("user", text) }, 700,
                        j => ApplyStatement(k, j, WorldSim.Clean(text, 1600)), important: true);
                }, null, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t) && t.Trim().Length >= 10, L.T("AIL_49569563", "Escribe algo más"))), true);
        }
    }
}
