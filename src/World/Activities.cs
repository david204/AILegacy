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
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>
    /// Cosas que haces en un asentamiento y que llevan su tiempo, con la barra de espera del propio juego (la misma de
    /// «Buscas entre los caídos»): preguntar por ahí qué se cuenta, o pasar la velada con tus hombres. Un solo menú de espera
    /// sirve para todas: cada actividad dice su texto, cuántas horas dura y qué pasa al acabar.
    /// </summary>
    internal static class Activities
    {
        private const string WaitMenu = "ailegacy_activity";
        private static readonly Random Rng = new Random();

        // ---- la actividad en curso
        private static string _text = "";
        private static float _hours = 1f, _progress;
        private static string _backMenu = "town";
        private static Action _onDone;
        private static bool _running;

        // ---- pausas (no se guardan: al cargar partida se puede repetir, no pasa nada)
        private static readonly Dictionary<string, double> AskedAt = new Dictionary<string, double>();
        private static double _lastEvening = -100;

        // ---- lo que se cuenta: la petición sale al empezar, para que esté lista al acabar la espera
        private static List<string> _rumours;
        private static bool _rumoursFailed, _showWhenReady;
        private static string _rumoursPlace = "";
        private static int _askSerial;

        private static readonly Tpl TAsk = new Tpl("activities/ask_around.txt",
@"{player} has spent a few hours asking around in {place} ({kind}), in Calradia, the medieval world of Mount & Blade II: Bannerlord: buying a drink, listening at the market, talking to whoever would talk.
Write what they came away with: 3 to 5 pieces of talk, as the locals tell them. Use ONLY the facts below; people here care most about what touches {place} and its lords, and tell the rest as distant news. One may be exaggerated or told with a local slant, but none is invented from nothing, and nobody knows what happened far away today.
Each piece is one or two sentences, in {language}, in the voice of common folk, without naming who said it.

What is true around here:
{facts}

Answer ONLY with JSON: { ""rumours"": [ ""..."", ""..."" ] }");

        public static void Touch() { var _ = TAsk.Key; }

        private static void Start(string text, float hours, string backMenu, Action onDone)
        {
            _text = text; _hours = Math.Max(0.5f, hours); _progress = 0f; _backMenu = backMenu; _onDone = onDone; _running = true;
            MBTextManager.SetTextVariable("AIL_ACTIVITY", text);
            GameMenu.SwitchToMenu(WaitMenu);
        }

        private static void Finish(bool completed)
        {
            if (!_running) return;
            _running = false;
            try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = false; } catch { }
            var done = _onDone; _onDone = null;
            try { if (completed) done?.Invoke(); } catch (Exception ex) { Log.Error("Actividad: final", ex); }
            try { GameMenu.SwitchToMenu(_backMenu); } catch (Exception ex) { Log.Error("Actividad: volver al menú", ex); }
        }

        // ============================================================ preguntar por ahí

        private static string Facts(Settlement st)
        {
            var sb = new StringBuilder();
            try
            {
                sb.AppendLine($"- {st.Name} belongs to {st.OwnerClan?.Name} of {st.MapFaction?.Name}." + (st.Town != null ? $" Prosperity {(int)st.Town.Prosperity}, loyalty {(int)st.Town.Loyalty}, security {(int)st.Town.Security}." : ""));
                var here = new List<string>();
                foreach (var h in st.HeroesWithoutParty.Where(h => h != Hero.MainHero && h.IsLord).Take(4)) here.Add(h.Name.ToString());
                foreach (var p in st.Parties.Where(p => p.LeaderHero != null && p.LeaderHero != Hero.MainHero).Take(4)) here.Add(p.LeaderHero.Name + " (with their party)");
                if (here.Count > 0) sb.AppendLine("- Nobles in " + st.Name + " right now: " + string.Join(", ", here.Distinct()) + ".");
                string sick = Diseases.DescribeAt(st); if (sick.Length > 0) sb.AppendLine("- " + sick);
                string fx = WorldSim.DescribeEffectsAt(st); if (fx.Length > 0) sb.AppendLine("- " + fx);
            }
            catch { }
            try { string news = WorldSim.DescribeRecentNews(10); if (!string.IsNullOrWhiteSpace(news)) { sb.AppendLine("Recent news that has reached here:"); sb.AppendLine(news); } } catch { }
            try { string ev = WorldSim.DescribeActiveEvents(5); if (!string.IsNullOrWhiteSpace(ev)) { sb.AppendLine("Developments people talk about:"); sb.AppendLine(ev); } } catch { }
            return sb.ToString().TrimEnd();
        }

        private static void RequestRumours(Settlement st)
        {
            _rumours = null; _rumoursFailed = false; _showWhenReady = false; _rumoursPlace = st.Name.ToString();
            int serial = ++_askSerial;
            try
            {
                var cfg = S.Get;
                string prompt = TAsk.Format("player", Hero.MainHero.Name, "place", st.Name, "kind", st.IsTown ? "a town" : st.IsVillage ? "a village" : "a castle", "language", cfg.Language, "facts", Facts(st));
                bool sent = WorldSim.Ask(L.F("AIL_e6b1c001", $"lo que se cuenta en {st.Name}"), new List<ChatMessage> { new ChatMessage("user", prompt) }, 600,
                    j =>
                    {
                        if (serial != _askSerial) return;
                        var list = (j["rumours"] as JArray)?.Select(x => (x ?? "").ToString().Trim()).Where(x => x.Length > 3).Take(5).ToList();
                        if (list == null || list.Count == 0) _rumoursFailed = true; else _rumours = list;
                        if (_showWhenReady) ShowRumours();
                    },
                    key: "ask_around", failed: () => { if (serial != _askSerial) return; _rumoursFailed = true; if (_showWhenReady) ShowRumours(); });
                if (!sent) _rumoursFailed = true;
            }
            catch (Exception ex) { Log.Error("Preguntar por ahí", ex); _rumoursFailed = true; }
        }

        /// <summary>Sin IA (o si falla): las últimas noticias tal cual han llegado.</summary>
        private static List<string> PlainNews()
        {
            try
            {
                return NewsStore.All.OrderByDescending(n => n.Day).Take(4)
                    .Select(n => string.IsNullOrWhiteSpace(n.TextEs) ? n.Text : n.TextEs).Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
            }
            catch { return new List<string>(); }
        }

        private static void ShowRumours()
        {
            _showWhenReady = false;
            try
            {
                var list = _rumours ?? (_rumoursFailed ? PlainNews() : null);
                if (list == null) { _showWhenReady = true; return; }       // aún no ha llegado: saldrá en cuanto llegue
                string body = list.Count == 0 ? L.T("AIL_e6b1c002", "Nadie cuenta nada que no supieras ya.") : string.Join("\n\n", list.Select(r => "• " + r));
                InformationManager.ShowInquiry(new InquiryData(L.F("AIL_e6b1c003", $"Lo que se cuenta en {_rumoursPlace}"), body, true, false,
                    L.T("AIL_f6d0c4b4", "Enterado"), null, null, null), true);
                _rumours = null; _rumoursFailed = false;
            }
            catch (Exception ex) { Log.Error("Enseñar lo que se cuenta", ex); }
        }

        // ============================================================ velada con tus hombres

        private static int EveningCost()
        {
            int men = MobileParty.MainParty?.MemberRoster?.TotalHealthyCount ?? 1;
            return Math.Max(20, Math.Min(2500, men * Math.Max(1, S.Get.EveningCostPerMan)));
        }

        private static void EveningDone()
        {
            var cfg = S.Get;
            _lastEvening = CampaignTime.Now.ToDays;
            int morale = Math.Max(1, cfg.EveningMorale);
            try { MobileParty.MainParty.RecentEventsMorale += morale; } catch (Exception ex) { Log.Error("Velada: moral", ex); }
            // Los compañeros que cabalgan contigo te cogen algo más de confianza
            int closer = 0;
            try
            {
                if (cfg.EnableTrust)
                    foreach (var e in MobileParty.MainParty.MemberRoster.GetTroopRoster())
                    {
                        var h = e.Character?.HeroObject;
                        if (h == null || h == Hero.MainHero || !h.IsAlive) continue;
                        var m = MemoryStore.Find(h.StringId);
                        if (m == null || m.ConversationCount == 0) continue;
                        m.Trust = Math.Max(-100, Math.Min(100, m.Trust + 2)); m.TrustSet = true;
                        m.Notes.Add($"You spent an evening drinking and talking with {Hero.MainHero.Name} and the rest of the party in {Settlement.CurrentSettlement?.Name}.");
                        if (m.Notes.Count > 60) m.Notes.RemoveAt(0);
                        closer++;
                    }
            }
            catch (Exception ex) { Log.Error("Velada: compañeros", ex); }
            InformationManager.DisplayMessage(new InformationMessage(
                L.F("AIL_e6b1c004", $"La velada sienta bien a la tropa: moral +{morale}.") + (closer > 0 ? " " + L.F("AIL_e6b1c005", $"{closer} de tus compañeros te tienen algo más de confianza.") : ""), Colors.Green));
        }

        // ============================================================ menús

        public static void AddMenus(CampaignGameStarter starter)
        {
            starter.AddWaitGameMenu(WaitMenu, "{=!}{AIL_ACTIVITY}",
                args =>
                {
                    MBTextManager.SetTextVariable("AIL_ACTIVITY", _text);
                    try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = true; } catch { }
                },
                args => { args.MenuContext.GameMenu.SetTargetedWaitingTimeAndInitialProgress(_hours, Math.Min(1f, _progress / _hours)); return true; },
                args => Finish(true),
                (args, dt) =>
                {
                    if (!_running) return;
                    _progress += (float)dt.ToHours;
                    args.MenuContext.GameMenu.SetProgressOfWaitingInMenu(Math.Min(1f, _progress / _hours));
                    if (_progress >= _hours) Finish(true);
                },
                GameMenu.MenuAndOptionType.WaitMenuShowOnlyProgressOption, GameMenu.MenuOverlayType.None, 0f);

            starter.AddGameMenuOption(WaitMenu, "ailegacy_activity_stop", L.T("AIL_e6b1c006", "{=!}Dejarlo"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args => Finish(false), true);

            foreach (var menu in new[] { "town", "village" })
            {
                string back = menu;
                starter.AddGameMenuOption(menu, "ailegacy_ask_around_" + menu, L.T("AIL_e6b1c007", "{=!}Preguntar por ahí qué se cuenta ({AIL_ASK_HOURS} horas)"),
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Wait;
                        var cfg = S.Get; var st = Settlement.CurrentSettlement;
                        if (!cfg.EnableActivities || st == null) return false;
                        MBTextManager.SetTextVariable("AIL_ASK_HOURS", st.IsTown ? 3 : 2);
                        if (AskedAt.TryGetValue(st.StringId, out double at) && CampaignTime.Now.ToDays - at < 1.0)
                        { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_e6b1c008", "{=!}Hoy ya has preguntado aquí. Mañana habrá algo nuevo que contar.")); }
                        else args.Tooltip = new TextObject(L.T("AIL_e6b1c009", "{=!}Pasas un rato en la taberna y el mercado y te enteras de lo que se dice: noticias que han llegado, quién anda por aquí, qué preocupa a la gente."));
                        return true;
                    },
                    args =>
                    {
                        var st = Settlement.CurrentSettlement;
                        if (st == null) return;
                        AskedAt[st.StringId] = CampaignTime.Now.ToDays;
                        RequestRumours(st);
                        Start(L.F("AIL_e6b1c00a", $"Te mezclas con la gente de {st.Name}: invitas a una ronda, escuchas en el mercado y dejas hablar a quien quiera hablar."),
                            st.IsTown ? 3f : 2f, back, ShowRumours);
                    }, false, 3);

                starter.AddGameMenuOption(menu, "ailegacy_evening_" + menu, L.T("AIL_e6b1c00b", "{=!}Pasar la velada con tus hombres, 4 horas ({AIL_EVENING_COST}{GOLD_ICON})"),
                    args =>
                    {
                        args.optionLeaveType = GameMenuOption.LeaveType.Wait;
                        var cfg = S.Get; var st = Settlement.CurrentSettlement;
                        if (!cfg.EnableActivities || st == null) return false;
                        int cost = EveningCost();
                        MBTextManager.SetTextVariable("AIL_EVENING_COST", cost);
                        double since = CampaignTime.Now.ToDays - _lastEvening;
                        if (since < cfg.EveningCooldownDays)
                        { args.IsEnabled = false; args.Tooltip = new TextObject(L.F("AIL_e6b1c00c", $"{{=!}}Tus hombres aún recuerdan la última. Otra vez dentro de {(int)Math.Ceiling(cfg.EveningCooldownDays - since)} días.")); }
                        else if (Hero.MainHero.Gold < cost) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_e6b1c00d", "{=!}No te llega para pagar la bebida de todos.")); }
                        else args.Tooltip = new TextObject(L.T("AIL_e6b1c00e", "{=!}Pagas la bebida y la cena de la partida. Sube la moral, y los compañeros que cabalgan contigo te cogen algo más de confianza."));
                        return true;
                    },
                    args =>
                    {
                        var st = Settlement.CurrentSettlement;
                        int cost = EveningCost();
                        if (st == null || Hero.MainHero.Gold < cost) return;
                        GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, st, cost);
                        Start(L.F("AIL_e6b1c00f", $"Pagas la bebida y os sentáis juntos. Alguien empieza una canción de {st.Name}; para cuando acaba, ya nadie se acuerda de los últimos días de marcha."),
                            4f, back, EveningDone);
                    }, false, 3);
            }
        }
    }
}
