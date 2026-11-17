using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.GameState;
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
        // Letters that arrived and wait to pop up, oldest first. One window at a time.
        // reply = it answers a letter of the player (no "keep receiving letters?" question after it)
        private static readonly Queue<(Hero from, string text, bool reply)> ToShow = new Queue<(Hero, string, bool)>();
        private static bool _generating;
        private static bool _showing;
        private static int _lostChecks;

        /// <summary>New, loaded or imported save: drop queued letters and half-finished generations from the previous one.</summary>
        public static void ResetSession() { ToShow.Clear(); _generating = false; _showing = false; _lostChecks = 0; }

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
            if (Hero.MainHero.IsPrisoner)
            {
                InformationManager.DisplayMessage(new InformationMessage(L.T("AIL_e7b1de56", "Eres prisionero: no puedes enviar cartas."), Colors.Red));
                return;
            }
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
                    // Writing to someone opens the door again; a letter that tells them to stop writing closes it
                    Unmute(hero, "the player wrote to them");
                    if (AsksToStopWriting(text)) Mute(hero, "the player's letter asks them to stop writing");
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
                var arrived = MemoryStore.Letters.Where(l => l.ArriveDay <= now).OrderBy(l => l.ArriveDay).ToList();
                foreach (var l in arrived)
                {
                    MemoryStore.Letters.Remove(l);
                    // an answer to the player's letter is told apart from a letter of their own, and the mark goes with it
                    bool reply = !l.FromPlayer && l.HeroId != null && (World.WorldSim.State.LettersReplying?.Remove(l.HeroId) ?? false);
                    var hero = Hero.Find(l.HeroId);
                    if (hero == null || hero.IsDead) continue;
                    // a messenger cannot reach a prisoner: the letter comes back unread and its writer waits for the player to be free
                    if (!l.FromPlayer && Hero.MainHero.IsPrisoner)
                    {
                        Lost(hero, $"[System: Your messenger came back with your letter to {Hero.MainHero.Name}: he searched but could not find them anywhere, and the letter never reached them.]");
                        continue;
                    }
                    if (l.FromPlayer) Deliver(hero, l.Text);
                    else Receive(hero, l.Text, reply);
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
                var lost = World.WorldSim.State?.LettersLost;
                if (lost != null && lost.Count > 0 && !Hero.MainHero.IsPrisoner)
                {
                    // free again: those who could not reach the player may write at once
                    foreach (var id in lost) { var m = MemoryStore.Find(id); if (m != null) m.LastLetterDay = -1; }
                    lost.Clear(); Log.Info("Letters: the player is free again; everyone may write");
                }
                if (!cfg.EnableLetters || _generating) return;
                double now = CampaignTime.Now.ToDays;
                if (MemoryStore.LastLetterDay >= 0 && now - MemoryStore.LastLetterDay < cfg.MinDaysBetweenLetters) return;
                if (Rng.Next(100) >= cfg.LetterChancePercent) return;

                var candidates = MemoryStore.AllHeroes
                    .Where(m => m.ConversationCount >= 1 && !m.OwesReplyToPlayer && (m.LastLetterDay < 0 || now - m.LastLetterDay >= 10))
                    .Select(m => (mem: m, hero: Hero.Find(m.HeroId)))
                    .Where(x => CanWrite(x.hero) && !IsMuted(x.hero))
                    .ToList();
                if (candidates.Count == 0) return;

                double Weight((HeroMemory mem, Hero hero) x) =>
                    1 + Math.Abs(x.hero.GetRelationWithPlayer()) / 10.0 + x.mem.Notes.Count * 0.5 + (now - x.mem.LastConversationDay > 10 ? 1 : 0);
                double total = candidates.Sum(Weight), roll = Rng.NextDouble() * total;
                foreach (var c in candidates)
                {
                    roll -= Weight(c);
                    if (roll <= 0)
                    {
                        // the player is a prisoner: the messenger finds nobody, and the writer stops trying until they are free
                        if (Hero.MainHero.IsPrisoner) { Lost(c.hero, $"[System: You sent a messenger to {Hero.MainHero.Name}, but he could not find them anywhere and came back with nothing.]"); return; }
                        Generate(c.hero, false); return;
                    }
                }
            }
            catch (Exception ex) { Log.Error("Letters: daily tick", ex); }
        }

        private static bool CanWrite(Hero h) =>
            h != null && h.IsAlive && !h.IsChild && h != Hero.MainHero && !h.IsPrisoner && h.PartyBelongedTo != MobileParty.MainParty && !HoldsPlayer(h)
            && World.WorldSim.State?.LettersLost?.Contains(h.StringId) != true;

        /// <summary>The writer learns the messenger found nobody, and does not write again while the player is a prisoner.</summary>
        private static void Lost(Hero hero, string note)
        {
            try
            {
                var lost = World.WorldSim.State?.LettersLost;
                if (lost != null && lost.Contains(hero.StringId)) return;      // they already know; once is enough
                var mem = MemoryStore.Get(hero.StringId, hero.Name.ToString());
                mem.Turns.Add(new ChatTurn { Role = "player", Text = note, Day = CampaignTime.Now.ToDays });
                mem.LastLetterDay = CampaignTime.Now.ToDays;
                if (lost != null) lost.Add(hero.StringId);
                Log.Info($"Letters: {hero.Name}'s messenger could not find the imprisoned player");
                SaveFiles.Dirty();
            }
            catch (Exception ex) { Log.Error("Letters: messenger lost", ex); }
        }

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
            // As AIInfluence's allows_letters: the one answering says when the player wants no more letters from them
            if (isReply && msgs.Count > 0 && msgs[0] != null) msgs[0].Content = (msgs[0].Content ?? "").TrimEnd() + "\n" + StopField;

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
                    bool stop = false;
                    try
                    {
                        var a = res.Text.IndexOf('{'); var b = res.Text.LastIndexOf('}');
                        if (a >= 0 && b > a)
                        {
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            letter = ((string)j["letter"] ?? letter).Trim();
                            remember = ((string)j["remember"] ?? "").Trim();
                            if (j["actions"] is JArray arr) actions = arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                            stop = IsTrue(j["stop_letters"]) || IsFalse(j["allows_letters"]);
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

                    // Their answer still reaches the player; after it, they write no more until the player seeks them out
                    if (isReply && (stop || AsksToStopWriting(playerLetter))) Mute(hero, stop ? "their answer says the player asked them to stop writing" : "the player's letter asks them to stop writing");
                    if (isReply)
                    {
                        var replying = World.WorldSim.State.LettersReplying ?? (World.WorldSim.State.LettersReplying = new List<string>());
                        replying.Add(hero.StringId);     // one entry per answer on the road: List.Remove takes one away as each arrives
                    }

                    var (_, hours) = Quote(hero);
                    MemoryStore.Letters.Add(new PendingLetter { HeroId = hero.StringId, Text = letter, FromPlayer = false, ArriveDay = now + hours / 24.0 });
                    Log.Info($"{s.Name} writes a letter ({(isReply ? "reply" : "own initiative")}); arrives in {hours:0} h");
                    SaveFiles.Flush();
                });
            });
        }

        // ============================================================ no more letters from someone

        // As AIInfluence's allows_letters, asked of the one answering the player's letter
        private static readonly Tpl TStopField = new Tpl("letter/stop_letters.txt",
            "If their letter asks you to stop writing to them (no more letters), also put \"stop_letters\": true in the JSON object: this answer is then your last letter until they speak with you or write to you again. Otherwise leave it out.");
        private static string StopField => TStopField.Text;

        private static bool IsTrue(JToken t) => t != null && (t.Type == JTokenType.Boolean ? (bool)t : string.Equals(t.ToString().Trim(), "true", StringComparison.OrdinalIgnoreCase));
        private static bool IsFalse(JToken t) => t != null && (t.Type == JTokenType.Boolean ? !(bool)t : string.Equals(t.ToString().Trim(), "false", StringComparison.OrdinalIgnoreCase));

        /// <summary>
        /// The player asked them to write no more. Lasts until the player talks to them (a conversation after the day
        /// it was set) or writes to them. Kept per campaign in the world file.
        /// </summary>
        public static bool IsMuted(Hero h)
        {
            try
            {
                var map = World.WorldSim.State?.LettersMuted;
                if (h == null || map == null || !map.TryGetValue(h.StringId, out double day)) return false;
                var mem = MemoryStore.Find(h.StringId);
                if (mem != null && mem.LastConversationDay > day)
                {
                    map.Remove(h.StringId);
                    Log.Info($"Letters: {h.Name} may write again, the player has talked to them");
                    return false;
                }
                return true;
            }
            catch { return false; }
        }

        internal static void Mute(Hero h, string why)
        {
            try
            {
                var st = World.WorldSim.State;
                if (h == null || st == null || IsMuted(h)) return;
                if (st.LettersMuted == null) st.LettersMuted = new Dictionary<string, double>();
                st.LettersMuted[h.StringId] = CampaignTime.Now.ToDays;
                Log.Info($"Letters: {h.Name} will send no letters until the player talks or writes to them ({why})");
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1d903", $"{h.Name} no volverá a escribirte hasta que vuelvas a hablarle o a escribirle."), Colors.Yellow));
                SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Letters: no more letters", ex); }
        }

        internal static void Unmute(Hero h, string why)
        {
            try
            {
                var map = World.WorldSim.State?.LettersMuted;
                if (h != null && map != null && map.Remove(h.StringId)) Log.Info($"Letters: {h.Name} may write again ({why})");
            }
            catch { }
        }

        /// <summary>After an unanswered letter of their own: the choice of no more letters from them.</summary>
        private static void AskMute(Hero hero)
        {
            try
            {
                InformationManager.ShowInquiry(new InquiryData(
                    L.F("AIL_da85936f", $"Carta de {hero.Name}"),
                    L.F("AIL_e7b1d901", $"Puedes pedirle a {hero.Name} que deje de escribirte: no te mandará más cartas hasta que vuelvas a hablarle o a escribirle."),
                    true, true, L.F("AIL_e7b1d900", $"No más cartas de {hero.Name}"), L.T("AIL_e7b1d902", "Guardar la carta"),
                    () => { _showing = false; Mute(hero, "the player chose no more letters from them"); },
                    () => { _showing = false; }), true);
            }
            catch (Exception ex)
            {
                _showing = false;
                Log.Error("Letters: ask about more letters", ex);
            }
        }

        // Plain ways to say it in English and Spanish, folded (lower case, no accents, no apostrophes). The one answering
        // the letter flags it too (stop_letters), which covers any other wording or language.
        private static readonly string[] StopPhrases =
        {
            "stop writing", "stop sending me", "stop sending letters", "stop sending your letters", "stop your letters", "no more letters", "no more of your letters",
            "dont write to me", "do not write to me", "dont write me", "do not write me", "never write to me", "never write me", "dont ever write", "do not ever write",
            "dont send me letters", "do not send me letters", "dont send me any more", "do not send me any more", "quit writing", "cease writing", "write to me no more",
            "deja de escribir", "dejes de escribir", "dejad de escribir", "deje de escribir", "dejen de escribir", "deja de mandarme", "deja de enviarme",
            "no me escribas", "no me escriba", "no me escribais", "no me vuelvas a escribir", "no vuelvas a escribirme", "no me mandes mas cartas", "no me envies mas cartas",
            "no mas cartas", "basta de cartas", "ni una carta mas",
        };
        private static readonly string[] StopNegations = { " no", " not", " dont", " never", " nunca", " jamas" };

        /// <summary>The player's letter plainly asks them to stop writing ("stop sending me letters", "deja de escribirme"). "Don't stop writing" does not.</summary>
        public static bool AsksToStopWriting(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return false;
            string t = " " + Fold(text) + " ";
            foreach (var p in StopPhrases)
                for (int at = t.IndexOf(" " + p, StringComparison.Ordinal); at >= 0; at = t.IndexOf(" " + p, at + 1, StringComparison.Ordinal))
                {
                    string before = t.Substring(0, at);
                    if (!p.StartsWith("no ") && StopNegations.Any(n => before.EndsWith(n, StringComparison.Ordinal))) continue;
                    return true;
                }
            return false;
        }

        private static string Fold(string s)
        {
            var sb = new StringBuilder();
            bool space = true;
            foreach (char c in s.Normalize(NormalizationForm.FormD).ToLowerInvariant())
            {
                if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark || c == '\'' || c == '’') continue;
                if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
                else if (!space) { sb.Append(' '); space = true; }
            }
            return sb.ToString().Trim();
        }

        // ============================================================ showing received letters

        /// <summary>A letter for the player has arrived: it pops up at once if the player is free on the map, otherwise as soon as they are.</summary>
        /// <param name="reply">It answers a letter of the player. A muted character's answer still arrives; any other letter of theirs is dropped.</param>
        internal static void Receive(Hero from, string text, bool reply = false)
        {
            if (from == null) return;
            if (!reply && IsMuted(from)) { Log.Info($"Letters: a letter from {from.Name} dropped, the player asked them to stop writing"); return; }
            ToShow.Enqueue((from, text, reply));
            Log.Info($"Letters: a letter from {from.Name} arrived ({ToShow.Count} waiting to be shown)");
            TryShowPending();
        }

        /// <summary>
        /// The player is on the open map with nothing else on screen, so a popup can open right now: no scene, no
        /// conversation, no settlement or encounter menu, no battle, no other window, no pause menu.
        /// </summary>
        internal static bool PlayerFreeOnMap()
        {
            try
            {
                if (Campaign.Current == null || Hero.MainHero == null || MobileParty.MainParty == null) return false;
                if (Mission.Current != null) return false;
                if (Campaign.Current.ConversationManager?.IsConversationInProgress == true) return false;
                if (ChatSession.IsBusy) return false;
                if (!(GameStateManager.Current?.ActiveState is MapState ms) || ms.AtMenu) return false;
                if (PlayerEncounter.Current != null || MobileParty.MainParty.MapEvent != null) return false;
                if (SandBox.View.Map.MapScreen.Instance?.IsEscapeMenuOpened == true) return false;
                if (InformationManager.IsAnyInquiryActive()) return false;      // a new one would only queue behind it
                if (MBInformationManager.GetIsAnySceneNotificationActive() == true) return false;
                return true;
            }
            catch { return false; }
        }

        /// <summary>Called every second. Pops up the next waiting letter once the player is free on the map.</summary>
        public static void TryShowPending()
        {
            // The window went away without either button (closed by the game): don't let it block the queue for good
            if (_showing)
            {
                bool up = true;
                try { up = InformationManager.IsAnyInquiryActive(); } catch { }
                _lostChecks = up ? 0 : _lostChecks + 1;
                if (_lostChecks < 2) return;
                Log.Warn("Letters: the letter window closed on its own, moving on");
                _showing = false; _lostChecks = 0;
            }
            if (ToShow.Count == 0) return;
            if (!PlayerFreeOnMap()) return;

            var (hero, text, reply) = ToShow.Dequeue();
            if (hero == null) return;
            // muted while it waited in the queue
            if (!reply && IsMuted(hero)) { Log.Info($"Letters: a waiting letter from {hero.Name} dropped, the player asked them to stop writing"); return; }
            text = (text ?? "").Replace("\\r\\n", "\n").Replace("\\n", "\n");      // some letters were saved with literal "\n"
            InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_97e2efc7", $"Un mensajero te trae una carta de {hero.Name}."), Colors.Yellow));
            Open(hero, text, reply);
        }

        /// <summary>
        /// Shows a received letter in a window that pauses the game, with the reply button if a messenger can be sent back.
        /// Only one at a time: the next waits in the queue until this one (and the reply box, if any) is closed.
        /// </summary>
        /// <param name="reply">It answers a letter of the player: closing it asks nothing more.</param>
        internal static void Open(Hero hero, string text, bool reply = false)
        {
            if (hero == null) return;
            if (_showing) { ToShow.Enqueue((hero, text, reply)); return; }
            _showing = true; _lostChecks = 0;
            try
            {
                // Reply on the spot (the messenger waits) or just acknowledge. A letter they wrote on their own, put away
                // unanswered, brings the choice of no more letters from them (the game's window has room for two buttons only).
                bool canReply = CanWrite(hero);
                InformationManager.ShowInquiry(new InquiryData(
                    L.F("AIL_da85936f", $"Carta de {hero.Name}"), text, true, canReply,
                    canReply ? L.T("AIL_f6d0c4b3", "Responder") : L.T("AIL_f6d0c4b4", "Enterado"), L.T("AIL_f6d0c4b4", "Enterado"),
                    () =>
                    {
                        if (canReply) { _showing = false; MainThread.Post(() => Compose(hero, reply: true)); }
                        else if (reply) _showing = false;
                        else MainThread.Post(() => AskMute(hero));
                    },
                    () => { if (reply) _showing = false; else MainThread.Post(() => AskMute(hero)); }), true);
                Log.Info($"Letters: showing the letter from {hero.Name}");
            }
            catch (Exception ex)
            {
                _showing = false;
                Log.Error("Letters: open the letter window", ex);
            }
        }
    }
}
