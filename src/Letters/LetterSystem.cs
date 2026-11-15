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
    /// Letters by messenger. The player sends them from the town backstreet menu (cost and travel time depend on distance),
    /// and NPCs with some history write on their own. Every letter travels, nothing arrives instantly.
    /// </summary>
    internal static class LetterSystem
    {
        private static readonly Random Rng = new Random();
        private static readonly Queue<(Hero from, string text)> ToShow = new Queue<(Hero, string)>();
        private static bool _generating;
        private static bool _showing;
        // Letters opened from their map notice while another letter's window was still up
        private static readonly Queue<(Hero from, string text)> ToRead = new Queue<(Hero, string)>();

        /// <summary>New, loaded or imported save: drop queued letters and half-finished generations from the previous one.</summary>
        public static void ResetSession() { ToShow.Clear(); ToRead.Clear(); _generating = false; _showing = false; }

        // ============================================================ backstreet menu

        public static void AddMenu(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town_backstreet", "ailegacy_send_letter", L.T("AIL_3ae61921", "{=!}Enviar una carta"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Submenu;
                    return S.Get.EnableLetters;
                },
                args => MainThread.Post(PickRecipient), false, 1);
        }

        private static void PickRecipient()
        {
            // Only people you know: you've talked to them in AI Legacy, or the game says you've met
            var heroes = Hero.AllAliveHeroes
                .Where(h => h != Hero.MainHero && !h.IsChild && h.PartyBelongedTo != MobileParty.MainParty)
                .Select(h => (h, known: MemoryStore.Find(h.StringId)))
                .Where(x => (x.known != null && (x.known.ConversationCount > 0 || x.known.Turns.Count > 0)) || x.h.HasMet)
                .OrderByDescending(x => x.known != null && x.known.ConversationCount > 0)
                .ThenBy(x => x.h.Name.ToString())
                .Take(300).ToList();
            if (heroes.Count == 0)
            {
                InformationManager.ShowInquiry(new InquiryData(L.T("AIL_629058ab", "Enviar una carta"), L.T("AIL_ab845913", "No conoces a nadie a quien escribir. Habla antes con la gente."), true, false, L.T("AIL_d36c447b", "Vale"), "", null, null), true);
                return;
            }

            var items = heroes.Select(x =>
            {
                var (cost, hours) = Quote(x.h);
                string tag = x.known != null && x.known.ConversationCount > 0 ? "★ " : "";
                return new InquiryElement(x.h, L.F("AIL_bc5de8fe", $"{tag}{x.h.Name}{(x.h.Clan != null ? " (" + x.h.Clan.Name + ")" : "")} — {cost} denarios, {TravelText(hours)}"), null);
            }).ToList();

            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                L.T("AIL_629058ab", "Enviar una carta"), L.T("AIL_9dedec74", "Solo puedes escribir a quien conoces. ★ = alguien con quien ya has hablado. El coste y el tiempo dependen de la distancia."),
                items, true, 1, 1, L.T("AIL_e5c9b3ab", "Escribir"), L.T("AIL_e5c9b3a4", "Cancelar"),
                sel => { if (sel?.Count > 0) MainThread.Post(() => Compose((Hero)sel[0].Identifier)); }, null, "", true), true);
        }

        /// <param name="reply">Answering a letter you just got. The messenger who brought it carries the reply for free.</param>
        private static void Compose(Hero hero, bool reply = false)
        {
            var (cost, hours) = Quote(hero);
            if (reply) cost = 0;
            InformationManager.ShowTextInquiry(new TextInquiryData(
                reply ? L.F("AIL_f6d0c4b1", $"Tu respuesta a {hero.Name}") : L.F("AIL_27a8d342", $"Carta para {hero.Name}"),
                reply ? L.F("AIL_f6d0c4b2", $"El mensajero que la ha traído espera tu respuesta y la lleva de vuelta sin cobrar. Tardará {TravelText(hours)} en llegar.")
                      : L.F("AIL_dcd92686", $"El mensajero cobra {cost} denarios y tardará {TravelText(hours)} en llegar.\nSi te contesta, la respuesta tardará lo mismo en volver."),
                true, true, L.T("AIL_e5c9b3ac", "Enviar"), L.T("AIL_e5c9b3a4", "Cancelar"),
                text =>
                {
                    text = (text ?? "").Trim();
                    if (text.Length == 0) return;
                    if (Hero.MainHero.Gold < cost)
                    {
                        InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_4a84320d", $"No tienes {cost} denarios para pagar al mensajero."), Colors.Red));
                        return;
                    }
                    if (cost > 0) Hero.MainHero.ChangeHeroGold(-cost);
                    MemoryStore.Letters.Add(new PendingLetter { HeroId = hero.StringId, Text = text, FromPlayer = true, ArriveDay = CampaignTime.Now.ToDays + hours / 24.0 });
                    InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_c27c3eed", $"El mensajero parte hacia {hero.Name}. Llegará en {TravelText(hours)}."), Colors.Yellow));
                    Log.Info($"Letter for {hero.Name} ({cost} denars, {hours:0} h)");
                    SaveFiles.Flush();
                },
                null, false, t => new Tuple<bool, string>(!string.IsNullOrWhiteSpace(t), L.T("AIL_e07caced", "Escribe algo"))), true);
        }

        // ============================================================ distance, cost, travel time

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
            int cost = (int)Math.Round(dist * cfg.MessengerCostPerDistance / 10.0) * 10; // rounded to tens
            double hours = Math.Max(2, dist * cfg.MessengerHoursPerDistanceX10 / 10.0);
            return (Math.Max(0, cost), hours);
        }

        private static string TravelText(double hours)
        {
            if (hours < 24) return $"unas {Math.Max(1, (int)Math.Round(hours))} horas";
            double days = hours / 24.0;
            return days < 1.5 ? L.T("AIL_7278010d", "un día") : L.F("AIL_2003bc66", $"unos {(int)Math.Round(days)} días");
        }

        // ============================================================ campaign ticks

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
            catch (Exception ex) { Log.Error("Letters: hourly tick", ex); }
        }

        /// <summary>The player's letter arrives. The NPC reads it and writes back, and the reply travels too.</summary>
        private static void Deliver(Hero hero, string text)
        {
            var mem = MemoryStore.Get(hero.StringId, hero.Name.ToString());
            mem.Turns.Add(new ChatTurn { Role = "player", Text = "[Letter] " + text, Day = CampaignTime.Now.ToDays });
            mem.OwesReplyToPlayer = true;
            Bonds.Touch(mem);
            Log.Info($"Letter delivered to {hero.Name}");
            SaveFiles.Flush();
            Generate(hero, true);
        }

        /// <summary>Once a day, someone with history with the player may write to them unprompted.</summary>
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
            catch (Exception ex) { Log.Error("Letters: daily tick", ex); }
        }

        private static bool CanWrite(Hero h) =>
            h != null && h.IsAlive && !h.IsChild && h != Hero.MainHero && !h.IsPrisoner && h.PartyBelongedTo != MobileParty.MainParty && !HoldsPlayer(h);

        // Nobody writes a letter to the prisoner riding in their own column or sitting in their own dungeon
        private static bool HoldsPlayer(Hero h)
        {
            try
            {
                var captor = PlayerCaptivity.CaptorParty;
                if (captor == null || !PlayerCaptivity.IsCaptive) return false;
                if (captor.IsMobile) return h.PartyBelongedTo != null && h.PartyBelongedTo == captor.MobileParty;
                return captor.IsSettlement && h.CurrentSettlement == captor.Settlement;
            }
            catch { return false; }
        }

        private static void Generate(Hero hero, bool isReply)
        {
            var s = Speaker.FromHero(hero);
            string playerLetter = null;
            if (isReply) playerLetter = (s.Memory.Turns.LastOrDefault(t => t.Role == "player" && t.Text != null && t.Text.StartsWith("[Letter] "))
                                         ?? s.Memory.Turns.LastOrDefault(t => t.Role == "player" && !t.IsNote))?.Text?.Replace("[Letter] ", "");

            List<ChatMessage> msgs;
            try { msgs = PromptBuilder.BuildLetter(s, playerLetter); }
            catch (Exception ex) { Log.Error("Letters: prompt", ex); return; }

            _generating = true;
            int session = World.WorldSim.Session;
            LlmClient.CompleteAsync(msgs, wantJson: true, task: "letter", preferredProvider: S.Get.ProviderLetters).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    if (session != World.WorldSim.Session) { Log.Info("Letters: response from another game, discarded"); return; }
                    _generating = false;
                    if (res == null || !res.Ok) { Log.Warn("Letters: no response from the AI: " + res?.Error); return; }

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
                    letter = (letter ?? "").Replace("\\r\\n", "\n").Replace("\\n", "\n").Replace("\\r", "").Trim();
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

                    // Whatever they decide after reading your letter (come over, meet somewhere, march...) starts now
                    if (isReply && actions.Count > 0)
                    {
                        try { NpcActions.Execute(s, actions, new NpcActions.ActionContext { ViaLetter = true }); }
                        catch (Exception ex) { Log.Error("Letters: actions", ex); }
                    }

                    var (_, hours) = Quote(hero);
                    MemoryStore.Letters.Add(new PendingLetter { HeroId = hero.StringId, Text = letter, FromPlayer = false, ArriveDay = now + hours / 24.0 });
                    Log.Info($"{s.Name} writes a letter ({(isReply ? "reply" : "own initiative")}); arrives in {hours:0} h");
                    SaveFiles.Flush();
                });
            });
        }

        // ============================================================ showing received letters

        /// <summary>Call often. Shows the next letter once the player is free on the map.</summary>
        public static void TryShowPending()
        {
            if (_showing || (ToShow.Count == 0 && ToRead.Count == 0)) return;
            if (Mission.Current != null) return;
            if (Campaign.Current?.ConversationManager?.IsConversationInProgress == true) return;
            if (ChatSession.IsBusy) return;

            // A letter clicked on the map while another window was up: wait until no window is (the reply box is one)
            if (ToRead.Count > 0)
            {
                bool busy = false;
                try { busy = InformationManager.IsAnyInquiryActive(); } catch { }
                if (!busy) { var (h, t) = ToRead.Dequeue(); Open(h, t); }
                return;
            }

            var (hero, text) = ToShow.Dequeue();
            text = (text ?? "").Replace("\\r\\n", "\n").Replace("\\n", "\n");      // some letters were saved with literal "\n"
            InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_97e2efc7", $"Un mensajero te trae una carta de {hero.Name}."), Colors.Yellow));
            // As a notice on the map, opened when the player clicks it. The window comes at once only if asked for,
            // or when the notice could not be placed
            var cfg = S.Get;
            bool noticed = cfg.LetterMapNotification && LetterNotice.Add(hero, text);
            if (noticed && !cfg.LetterPopup) return;
            Open(hero, text);
        }

        /// <summary>Shows a received letter in a window, with the reply button if a messenger can be sent back.</summary>
        internal static void Open(Hero hero, string text)
        {
            if (hero == null) return;
            if (_showing) { ToRead.Enqueue((hero, text)); return; }     // one window at a time, the rest wait their turn
            _showing = true;
            // Reply on the spot (the messenger waits) or just acknowledge
            bool canReply = CanWrite(hero);
            InformationManager.ShowInquiry(new InquiryData(
                L.F("AIL_da85936f", $"Carta de {hero.Name}"), text, true, canReply,
                canReply ? L.T("AIL_f6d0c4b3", "Responder") : L.T("AIL_f6d0c4b4", "Enterado"), L.T("AIL_f6d0c4b4", "Enterado"),
                () => { _showing = false; if (canReply) MainThread.Post(() => Compose(hero, reply: true)); },
                () => { _showing = false; }), true);
        }
    }
}
