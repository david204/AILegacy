using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Letters
{
    /// <summary>
    /// Cartas por mensajero. Tú las envías desde el distrito de tabernas (coste y tiempo según la distancia);
    /// los NPC con los que tienes historia te escriben por su cuenta. Todo viaja: nada llega al instante.
    /// </summary>
    internal static class LetterSystem
    {
        private static readonly Random Rng = new Random();
        private static readonly Queue<(Hero from, string text)> ToShow = new Queue<(Hero, string)>();
        private static bool _generating;
        private static bool _showing;

        /// <summary>Partida nueva, cargada o importada: ni cartas por enseñar ni generaciones a medias de la anterior.</summary>
        public static void ResetSession() { ToShow.Clear(); _generating = false; _showing = false; }

        // ============================================================ menú de la taberna

        public static void AddMenu(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town_backstreet", "ailegacy_send_letter", L.T("{=!}Enviar una carta"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Submenu;
                    return S.Get.EnableLetters;
                },
                args => MainThread.Post(PickRecipient), false, 1);
        }

        private static void PickRecipient()
        {
            // Solo a quien conoces: has hablado con él (AI Legacy) o el juego dice que os conocéis
            var heroes = Hero.AllAliveHeroes
                .Where(h => h != Hero.MainHero && !h.IsChild && h.PartyBelongedTo != MobileParty.MainParty)
                .Select(h => (h, known: MemoryStore.Find(h.StringId)))
                .Where(x => (x.known != null && (x.known.ConversationCount > 0 || x.known.Turns.Count > 0)) || x.h.HasMet)
                .OrderByDescending(x => x.known != null && x.known.ConversationCount > 0)
                .ThenBy(x => x.h.Name.ToString())
                .Take(300).ToList();
            if (heroes.Count == 0)
            {
                InformationManager.ShowInquiry(new InquiryData(L.T("Enviar una carta"), L.T("No conoces a nadie a quien escribir. Habla antes con la gente."), true, false, L.T("Vale"), "", null, null), true);
                return;
            }

            var items = heroes.Select(x =>
            {
                var (cost, hours) = Quote(x.h);
                string tag = x.known != null && x.known.ConversationCount > 0 ? "★ " : "";
                return new InquiryElement(x.h, L.F($"{tag}{x.h.Name}{(x.h.Clan != null ? " (" + x.h.Clan.Name + ")" : "")} — {cost} denarios, {TravelText(hours)}"), null);
            }).ToList();

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                L.T("Enviar una carta"), L.T("Solo puedes escribir a quien conoces. ★ = alguien con quien ya has hablado. El coste y el tiempo dependen de la distancia."),
                items, true, 1, 1, "Escribir", "Cancelar",
                sel => { if (sel?.Count > 0) MainThread.Post(() => Compose((Hero)sel[0].Identifier)); }, null, "", true), true);
        }

        private static void Compose(Hero hero)
        {
            var (cost, hours) = Quote(hero);
            InformationManager.ShowTextInquiry(new TextInquiryData(
                L.F($"Carta para {hero.Name}"),
                L.F($"El mensajero cobra {cost} denarios y tardará {TravelText(hours)} en llegar.\nSi te contesta, la respuesta tardará lo mismo en volver."),
                true, true, "Enviar", "Cancelar",
                text =>
                {
                    text = (text ?? "").Trim();
                    if (text.Length == 0) return;
                    if (Hero.MainHero.Gold < cost)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(L.F($"No tienes {cost} denarios para pagar al mensajero."), Colors.Red));
                        return;
                    }
                    if (cost > 0) Hero.MainHero.ChangeHeroGold(-cost);
                    MemoryStore.Letters.Add(new PendingLetter { HeroId = hero.StringId, Text = text, FromPlayer = true, ArriveDay = CampaignTime.Now.ToDays + hours / 24.0 });
                    InformationManager.DisplayMessage(new InformationMessage(L.F($"El mensajero parte hacia {hero.Name}. Llegará en {TravelText(hours)}."), Colors.Yellow));
                    Log.Info($"Carta para {hero.Name} ({cost} denarios, {hours:0} h)");
                    SaveFiles.Flush();
                },
                null, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("Escribe algo"))), true);
        }

        // ============================================================ distancia, coste y tiempo

        private static Vec2? WhereIs(Hero h)
        {
            try
            {
                if (h == Hero.MainHero) return MobileParty.MainParty.GetPosition2D;
                if (h.PartyBelongedTo != null) return h.PartyBelongedTo.GetPosition2D;
                if (h.IsPrisoner && h.PartyBelongedToAsPrisoner?.MobileParty != null) return h.PartyBelongedToAsPrisoner.MobileParty.GetPosition2D;
                var s = h.CurrentSettlement ?? h.HomeSettlement;
                return s?.GetPosition2D;
            }
            catch { return null; }
        }

        public static (int cost, double hours) Quote(Hero h)
        {
            var a = WhereIs(Hero.MainHero); var b = WhereIs(h);
            float dist = a.HasValue && b.HasValue ? a.Value.Distance(b.Value) : 50f;
            var cfg = S.Get;
            int cost = (int)Math.Round(dist * cfg.MessengerCostPerDistance / 10.0) * 10; // redondeado a decenas
            double hours = Math.Max(2, dist * cfg.MessengerHoursPerDistanceX10 / 10.0);
            return (Math.Max(0, cost), hours);
        }

        private static string TravelText(double hours)
        {
            if (hours < 24) return $"unas {Math.Max(1, (int)Math.Round(hours))} horas";
            double days = hours / 24.0;
            return days < 1.5 ? L.T("un día") : L.F($"unos {(int)Math.Round(days)} días");
        }

        // ============================================================ tiempo de juego

        /// <summary>Cada hora: entrega cartas que han llegado.</summary>
        public static void OnHourlyTick()
        {
            try
            {
                double now = CampaignTime.Now.ToDays;
                var arrived = MemoryStore.Letters.Where(l => l.ArriveDay <= now).ToList();
                foreach (var l in arrived)
                {
                    MemoryStore.Letters.Remove(l);
                    var hero = Hero.Find(l.HeroId);
                    if (hero == null || hero.IsDead) continue;
                    if (l.FromPlayer) Deliver(hero, l.Text);
                    else { ToShow.Enqueue((hero, l.Text)); TryShowPending(); }
                }
            }
            catch (Exception ex) { Log.Error("Cartas: tick horario", ex); }
        }

        /// <summary>La carta del jugador llega: el NPC la lee y contesta; la respuesta viaja de vuelta.</summary>
        private static void Deliver(Hero hero, string text)
        {
            var mem = MemoryStore.Get(hero.StringId, hero.Name.ToString());
            mem.Turns.Add(new ChatTurn { Role = "player", Text = "[Letter] " + text, Day = CampaignTime.Now.ToDays });
            mem.OwesReplyToPlayer = true;
            Bonds.Touch(mem);
            Log.Info($"Carta entregada a {hero.Name}");
            SaveFiles.Flush();
            Generate(hero, true);
        }

        /// <summary>Cada día: alguien con historia contigo puede escribirte por su cuenta.</summary>
        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableLetters || _generating) return;
                double now = CampaignTime.Now.ToDays;
                if (MemoryStore.LastLetterDay >= 0 && now - MemoryStore.LastLetterDay < cfg.MinDaysBetweenLetters) return;
                if (Rng.Next(100) >= cfg.LetterChancePercent) return;

                var candidates = MemoryStore.AllHeroes
                    .Where(m => m.ConversationCount >= 1 && !m.OwesReplyToPlayer && (m.LastLetterDay < 0 || now - m.LastLetterDay >= 10))
                    .Select(m => (mem: m, hero: Hero.Find(m.HeroId)))
                    .Where(x => CanWrite(x.hero))
                    .ToList();
                if (candidates.Count == 0) return;

                double Weight((HeroMemory mem, Hero hero) x) =>
                    1 + Math.Abs(x.hero.GetRelationWithPlayer()) / 10.0 + x.mem.Notes.Count * 0.5 + (now - x.mem.LastConversationDay > 10 ? 1 : 0);
                double total = candidates.Sum(Weight), roll = Rng.NextDouble() * total;
                foreach (var c in candidates)
                {
                    roll -= Weight(c);
                    if (roll <= 0) { Generate(c.hero, false); return; }
                }
            }
            catch (Exception ex) { Log.Error("Cartas: tick diario", ex); }
        }

        private static bool CanWrite(Hero h) =>
            h != null && h.IsAlive && !h.IsChild && h != Hero.MainHero && !h.IsPrisoner && h.PartyBelongedTo != MobileParty.MainParty;

        private static void Generate(Hero hero, bool isReply)
        {
            var s = Speaker.FromHero(hero);
            string playerLetter = null;
            if (isReply) playerLetter = s.Memory.Turns.LastOrDefault(t => t.Role == "player")?.Text?.Replace("[Letter] ", "");

            List<ChatMessage> msgs;
            try { msgs = PromptBuilder.BuildLetter(s, playerLetter); }
            catch (Exception ex) { Log.Error("Cartas: prompt", ex); return; }

            _generating = true;
            int session = World.WorldSim.Session;
            LlmClient.CompleteAsync(msgs, wantJson: true, preferredProvider: S.Get.ProviderLetters).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    if (session != World.WorldSim.Session) { Log.Info("Cartas: respuesta de otra partida, descartada"); return; }
                    _generating = false;
                    if (res == null || !res.Ok) { Log.Warn("Cartas: sin respuesta de la IA: " + res?.Error); return; }

                    string letter = res.Text, remember = "";
                    var actions = new List<string>();
                    try
                    {
                        var a = res.Text.IndexOf('{'); var b = res.Text.LastIndexOf('}');
                        if (a >= 0 && b > a)
                        {
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            letter = ((string)j["letter"] ?? letter).Trim();
                            remember = ((string)j["remember"] ?? "").Trim();
                            if (j["actions"] is JArray arr) actions = arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                        }
                    }
                    catch { }
                    if (string.IsNullOrWhiteSpace(letter)) return;

                    var mem = s.Memory;
                    double now = CampaignTime.Now.ToDays;
                    mem.Turns.Add(new ChatTurn { Role = "npc", Text = "[Letter] " + letter, Day = now });
                    mem.LettersSent++;
                    mem.LastLetterDay = now;
                    mem.OwesReplyToPlayer = false;
                    MemoryStore.LastLetterDay = now;
                    ChatSession.AddNote(mem, remember, S.Get.MaxNotesPerHero, s.Name);
                    Bonds.Touch(mem);

                    // Lo que decide hacer al leer tu carta (acudir, quedar en un sitio, marchar...) empieza ya
                    if (isReply && actions.Count > 0)
                    {
                        try { NpcActions.Execute(s, actions, new NpcActions.ActionContext { ViaLetter = true }); }
                        catch (Exception ex) { Log.Error("Cartas: acciones", ex); }
                    }

                    // La carta viaja hasta ti
                    var (_, hours) = Quote(hero);
                    MemoryStore.Letters.Add(new PendingLetter { HeroId = hero.StringId, Text = letter, FromPlayer = false, ArriveDay = now + hours / 24.0 });
                    Log.Info($"{s.Name} escribe una carta ({(isReply ? "respuesta" : "por iniciativa propia")}); llegará en {hours:0} h");
                    SaveFiles.Flush();
                });
            });
        }

        // ============================================================ mostrar cartas recibidas

        /// <summary>Llamar a menudo: enseña la siguiente carta cuando el jugador está en el mapa.</summary>
        public static void TryShowPending()
        {
            if (_showing || ToShow.Count == 0) return;
            if (Mission.Current != null) return;
            if (Campaign.Current?.ConversationManager?.IsConversationInProgress == true) return;
            if (ChatSession.IsBusy) return;

            var (hero, text) = ToShow.Dequeue();
            _showing = true;
            InformationManager.DisplayMessage(new InformationMessage(L.F($"Un mensajero te trae una carta de {hero.Name}."), Colors.Yellow));
            InformationManager.ShowInquiry(new InquiryData(
                L.F($"Carta de {hero.Name}"), text, true, false, "Guardar", "",
                () => { _showing = false; }, null), true);
        }
    }
}
