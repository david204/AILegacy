using System;
using System.Collections.Generic;
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
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>
    /// Arena training: in a town you pay and your troops train for eight hours. They gain XP (recruits gain more), a few
    /// get hurt, and that arena won't take you again for a few days. As in AIInfluence you can also pay for the troops
    /// of another party of your clan staying in the town (a companion's), or for all of them and yours together; each of
    /// those parties waits its own hours before training again.
    /// </summary>
    internal static class ArenaTraining
    {
        private const float Hours = 8f;
        private const string PartyKey = "party:";      // a party's own wait in WorldState.ArenaLastAt (shared with AiArenaTraining)
        private static float _progress;
        private static bool _running;
        private static int _heldHour = -1;
        // Who is training this time: the player's own men and/or the parties of the player's clan in town (by id)
        private static bool _withMine = true;
        private static readonly List<string> _parties = new List<string>();
        private static Dictionary<string, double> LastAt => WorldSim.State.ArenaLastAt;      // saved with the campaign
        private static readonly Random Rng = new Random();

        private static int CostOf(MobileParty p)
        {
            var st = Settlement.CurrentSettlement;
            if (p == null) return 0;
            float f = st != null && st.OwnerClan == Clan.PlayerClan ? 0.6f : 1f;
            return (int)Math.Ceiling(p.MemberRoster.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => (e.Number - e.WoundedNumber) * (8 + e.Character.Tier * 4)) * f * S.Get.ArenaTrainingCostPercent / 100f);
        }

        private static int Cost() => CostOf(MobileParty.MainParty);

        private static int FitOf(MobileParty p) => p == null ? 0 : p.MemberRoster.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => e.Number - e.WoundedNumber);

        private static int Fit() => FitOf(MobileParty.MainParty);

        private static bool Waiting(string key, out double hoursLeft)
        {
            hoursLeft = 0;
            if (!LastAt.TryGetValue(key, out double at)) return false;
            hoursLeft = S.Get.ArenaTrainingCooldownHours - (CampaignTime.Now.ToHours - at);
            return hoursLeft > 0;
        }

        /// <summary>The player's own arena wait is kept per town, as before.</summary>
        private static bool MineWaiting(Settlement st) => st != null && Waiting(st.StringId, out _);

        /// <summary>
        /// Other parties of the player's clan staying in this town that could train now: led by a hero, healthy men,
        /// not fighting, not in an army, not lent for an order, and past their own wait. As AIInfluence's companion parties.
        /// </summary>
        private static List<MobileParty> ClanPartiesHere(Settlement st)
        {
            if (st == null || Clan.PlayerClan == null) return new List<MobileParty>();
            return st.Parties.Where(p => p != null && p.IsActive && !p.IsMainParty && p.ActualClan == Clan.PlayerClan && p.LeaderHero != null
                                         && p.IsLordParty && p.MapEvent == null && p.Army == null && !OrderSystem.IsTempParty(p)
                                         && FitOf(p) > 0 && !Waiting(PartyKey + p.StringId, out _)).ToList();
        }

        private static List<MobileParty> Training() =>
            _parties.Select(id => MobileParty.All.FirstOrDefault(p => p.StringId == id)).Where(p => p != null && p.IsActive).ToList();

        private static void TrainRoster(TroopRoster roster, ref int trained, ref int hurt)
        {
            var cfg = S.Get;
            foreach (var e in roster.GetTroopRoster().Where(e => !e.Character.IsHero && e.Number - e.WoundedNumber > 0).ToList())
            {
                int n = e.Number - e.WoundedNumber;
                // Low tier troops learn faster than veterans
                int xpEach = (int)(cfg.ArenaTrainingXp * (1.5f - Math.Min(1f, e.Character.Tier / 6f)));
                roster.AddXpToTroop(e.Character, xpEach * n);
                trained += n;
                int injured = 0;
                for (int i = 0; i < n; i++) if (Rng.Next(100) < cfg.ArenaTrainingInjuryPercent) injured++;
                if (injured > 0) { roster.WoundTroop(e.Character, injured); hurt += injured; }
            }
        }

        private static void Finish()
        {
            if (!_running) return;
            _running = false;
            try
            {
                var st = Settlement.CurrentSettlement;
                int trained = 0, hurt = 0;
                if (_withMine)
                {
                    TrainRoster(MobileParty.MainParty.MemberRoster, ref trained, ref hurt);
                    if (st != null) LastAt[st.StringId] = CampaignTime.Now.ToHours;
                }
                int others = 0;
                foreach (var p in Training())
                {
                    // only those who stayed in town for it
                    if (p.CurrentSettlement != st) { Log.Info($"[arena] {p.Name} left town during the training and gains nothing"); continue; }
                    TrainRoster(p.MemberRoster, ref trained, ref hurt);
                    LastAt[PartyKey + p.StringId] = CampaignTime.Now.ToHours;
                    others++;
                }
                string head = _withMine && others == 0 ? L.F("AIL_980877af", $"Tus hombres terminan el entrenamiento: {trained} han ganado experiencia")
                                                       : L.F("AIL_e7b1db1b", $"Las tropas terminan el entrenamiento: {trained} han ganado experiencia");
                InformationManager.DisplayMessage(new InformationMessage(head + (hurt > 0 ? L.F("AIL_66fb1576", $", {hurt} se han lesionado.") : "."), Colors.Green));
                Log.Info($"[arena] training finished: {trained} trained, {hurt} injured (own troops: {_withMine}, clan parties: {others})");
            }
            catch (Exception ex) { Log.Error("Training end", ex); }
            _parties.Clear();
            try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = false; } catch { }
            GameMenu.SwitchToMenu("town_arena");
        }

        /// <summary>A clan party in training is told to stay in town (AIInfluence holds it the same way), once an hour.</summary>
        private static void HoldParties()
        {
            int hour = (int)_progress;
            if (hour == _heldHour) return;
            _heldHour = hour;
            var st = Settlement.CurrentSettlement;
            if (st == null) return;
            foreach (var p in Training())
            {
                try
                {
                    if (p.CurrentSettlement != st || p.Army != null || p.MapEvent != null) continue;
                    if (p.DefaultBehavior == AiBehavior.GoToSettlement && p.TargetSettlement == st) continue;
                    var nav = p.DesiredAiNavigationType != MobileParty.NavigationType.None ? p.DesiredAiNavigationType : MobileParty.NavigationType.All;
                    p.SetMoveGoToSettlement(st, nav, p.IsTargetingPort);
                    p.RecalculateShortTermBehavior();
                }
                catch (Exception ex) { Log.Warn($"[arena] could not keep {p.Name} in town: {ex.Message}"); }
            }
        }

        private static string WaitText()
        {
            var list = Training();
            if (!_withMine && list.Count == 1)
                return L.F("AIL_e7b1db19", $"Las tropas de {list[0].LeaderHero?.Name} se entrenan en la arena bajo los maestros de armas. Se oyen los golpes de las espadas de madera y los gritos de los instructores.");
            if (list.Count > 0)
                return L.T("AIL_e7b1db1a", "Todas tus tropas se entrenan juntas en la arena bajo los maestros de armas. Se oyen los golpes de las espadas de madera y los gritos de los instructores.");
            return L.T("AIL_76e00257", "{=!}Tus hombres se entrenan en la arena bajo los maestros de armas. Se oyen los golpes de las espadas de madera y los gritos de los instructores.").Replace("{=!}", "");
        }

        /// <summary>Pays and starts: the wait menu runs the eight hours.</summary>
        private static void Begin(bool mine, List<MobileParty> parties, int cost)
        {
            var st = Settlement.CurrentSettlement;
            if (st == null || Hero.MainHero.Gold < cost) return;
            if (cost > 0) GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, st, cost);
            _withMine = mine;
            _parties.Clear();
            _parties.AddRange(parties.Select(p => p.StringId));
            _progress = 0f; _heldHour = -1; _running = true;
            HoldParties();
            try
            {
                if (mine) Patches.ArenaNotice.Show(st, Hero.MainHero);
                foreach (var p in parties) Patches.ArenaNotice.Show(st, p.LeaderHero);
            }
            catch { }
            MBTextManager.SetTextVariable("AIL_TRAIN_WAIT", WaitText());
            Log.Info($"[arena] training starts in {st.Name}: own troops {mine}, clan parties {string.Join(", ", parties.Select(p => p.Name.ToString()))}, {cost} denars");
            GameMenu.SwitchToMenu("ailegacy_arena_training");
        }

        /// <summary>One clan party: picked from a list when there are several.</summary>
        private static void PickClanParty()
        {
            var st = Settlement.CurrentSettlement;
            var list = ClanPartiesHere(st);
            if (list.Count == 0) return;
            if (list.Count == 1) { Begin(false, list, CostOf(list[0])); return; }
            var items = list.Select(p => new InquiryElement(p.StringId, L.F("AIL_e7b1db18", $"{p.LeaderHero.Name}: {FitOf(p)} soldados, {CostOf(p)} denarios"), null,
                                                            Hero.MainHero.Gold >= CostOf(p), "")).ToList();
            MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                L.T("AIL_e7b1db16", "Elige la partida"), L.T("AIL_e7b1db17", "¿Las tropas de quién van a entrenar?"),
                items, true, 1, 1, L.T("AIL_e5c9b3a3", "Confirmar"), L.T("AIL_e5c9b3a4", "Cancelar"),
                sel => MainThread.Post(() =>
                {
                    try
                    {
                        string id = sel != null && sel.Count > 0 ? sel[0].Identifier as string : null;
                        var p = ClanPartiesHere(Settlement.CurrentSettlement).FirstOrDefault(x => x.StringId == id);
                        if (p != null) Begin(false, new List<MobileParty> { p }, CostOf(p));
                    }
                    catch (Exception ex) { Log.Error("Arena: pick a clan party", ex); }
                }), null, "", false), true);
        }

        public static void AddMenus(CampaignGameStarter starter)
        {
            starter.AddGameMenuOption("town_arena", "ailegacy_arena_train", L.T("AIL_2e04a874", "{=!}Entrenar a tus tropas en la arena, 8 horas ({AIL_TRAIN_COST}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Wait;
                    var cfg = S.Get;
                    var st = Settlement.CurrentSettlement;
                    if (!cfg.EnableArenaTraining || st == null || !st.IsTown) return false;
                    int cost = Cost();
                    MBTextManager.SetTextVariable("AIL_TRAIN_COST", cost);
                    if (Fit() < 1) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_96e9202d", "{=!}No tienes soldados sanos que entrenar.")); }
                    else if (st.Town.HasTournament) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_42b92c67", "{=!}Hay torneo: la arena está ocupada.")); }
                    else if (LastAt.TryGetValue(st.StringId, out double at) && CampaignTime.Now.ToHours - at < cfg.ArenaTrainingCooldownHours)
                    { args.IsEnabled = false; args.Tooltip = new TextObject(L.F("AIL_d9b2a7c5", $"{{=!}}Esta arena no os admite de nuevo hasta dentro de {(int)Math.Ceiling(cfg.ArenaTrainingCooldownHours - (CampaignTime.Now.ToHours - at))} horas.")); }
                    else if (Hero.MainHero.Gold < cost) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_73936b3a", "{=!}No tienes dinero para pagar a los maestros de armas.")); }
                    else args.Tooltip = new TextObject(L.T("AIL_2b8dd572", "{=!}Tus hombres sanos ganan experiencia (más los novatos). Alguno puede lesionarse. Más barato en tus propias ciudades."));
                    return true;
                },
                args =>
                {
                    if (Fit() < 1) return;
                    Begin(true, new List<MobileParty>(), Cost());
                }, false, 2);

            // The troops of another party of your clan in town (a companion's), paid by you
            starter.AddGameMenuOption("town_arena", "ailegacy_arena_train_clan", L.T("AIL_e7b1db10", "{=!}Entrenar a las tropas de un compañero, 8 horas"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Wait;
                    var st = Settlement.CurrentSettlement;
                    if (!S.Get.EnableArenaTraining || st == null || !st.IsTown) return false;
                    var list = ClanPartiesHere(st);
                    if (st.Town.HasTournament) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_42b92c67", "{=!}Hay torneo: la arena está ocupada.")); }
                    else if (list.Count == 0) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_e7b1db12", "{=!}No hay ninguna partida de tu clan en la ciudad con soldados sanos que entrenar ahora.")); }
                    else if (Hero.MainHero.Gold < list.Min(CostOf)) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_73936b3a", "{=!}No tienes dinero para pagar a los maestros de armas.")); }
                    else args.Tooltip = new TextObject(L.T("AIL_e7b1db14", "{=!}Pagas tú. Ganan experiencia los soldados de la partida que elijas, que se queda en la ciudad mientras dura."));
                    return true;
                },
                args => PickClanParty(), false, 3);

            // Yours and every clan party in town at once
            starter.AddGameMenuOption("town_arena", "ailegacy_arena_train_all", L.T("AIL_e7b1db11", "{=!}Entrenar a todas tus tropas juntas, 8 horas ({AIL_TRAIN_COST_ALL}{GOLD_ICON})"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Wait;
                    var st = Settlement.CurrentSettlement;
                    if (!S.Get.EnableArenaTraining || st == null || !st.IsTown) return false;
                    var list = ClanPartiesHere(st);
                    bool mine = Fit() > 0 && !MineWaiting(st);
                    int cost = (mine ? Cost() : 0) + list.Sum(CostOf);
                    MBTextManager.SetTextVariable("AIL_TRAIN_COST_ALL", cost);
                    if (st.Town.HasTournament) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_42b92c67", "{=!}Hay torneo: la arena está ocupada.")); }
                    else if (list.Count == 0) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_e7b1db13", "{=!}«Todas juntas» solo sirve cuando hay otras partidas de tu clan en la ciudad. Para tus hombres solos, usa «Entrenar a tus tropas».")); }
                    else if (Hero.MainHero.Gold < cost) { args.IsEnabled = false; args.Tooltip = new TextObject(L.T("AIL_73936b3a", "{=!}No tienes dinero para pagar a los maestros de armas.")); }
                    else args.Tooltip = new TextObject(L.T("AIL_e7b1db15", "{=!}Pagas tú. Entrenan a la vez tus hombres y los de las partidas de tu clan que hay en la ciudad."));
                    return true;
                },
                args =>
                {
                    var st = Settlement.CurrentSettlement;
                    var list = ClanPartiesHere(st);
                    if (list.Count == 0) return;
                    bool mine = Fit() > 0 && !MineWaiting(st);
                    Begin(mine, list, (mine ? Cost() : 0) + list.Sum(CostOf));
                }, false, 4);

            starter.AddWaitGameMenu("ailegacy_arena_training", "{=!}{AIL_TRAIN_WAIT}",
                args =>
                {
                    try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = true; } catch { }
                    MBTextManager.SetTextVariable("AIL_TRAIN_WAIT", WaitText());
                },
                args => { args.MenuContext.GameMenu.SetTargetedWaitingTimeAndInitialProgress(Hours, _progress / Hours); return true; },
                args => Finish(),
                (args, dt) =>
                {
                    _progress += (float)dt.ToHours;
                    args.MenuContext.GameMenu.SetProgressOfWaitingInMenu(Math.Min(1f, _progress / Hours));
                    try { HoldParties(); } catch { }
                    if (_progress >= Hours) Finish();
                },
                GameMenu.MenuAndOptionType.WaitMenuShowOnlyProgressOption, GameMenu.MenuOverlayType.None, Hours);

            starter.AddGameMenuOption("ailegacy_arena_training", "ailegacy_arena_stop", L.T("AIL_48173f1f", "{=!}Interrumpir el entrenamiento (no te devuelven el dinero)"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args =>
                {
                    _running = false;
                    _parties.Clear();
                    try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = false; } catch { }
                    GameMenu.SwitchToMenu("town_arena");
                }, true);
        }
    }

    /// <summary>
    /// Unique characters: every so often someone new shows up in a nearby town, made up by the AI for this campaign (a
    /// preacher, a mercenary captain, a runaway doctor, a pretender...), with a name, a trade, a personality and a reason
    /// to be there. They live as a regular wanderer, so you can look for them, deal with them or hire them.
    /// </summary>
    internal static class UniqueCharacters
    {
        private static readonly Random Rng = new Random();

        private static readonly Tpl TUnique = new Tpl("unique_role_characters/new_arrival.txt",
@"A newcomer has just arrived in {town}, a {culture} town of {faction}, in Calradia, the medieval world of Mount & Blade II: Bannerlord. Invent this person: someone with a trade or a calling that makes them worth meeting, and a reason to be here now.
The world around them:
{context}
People like this already about (make someone clearly different): {existing}
Not a lord and not a common labourer: a wandering physician, a disgraced captain, a preacher, a smuggler turned honest, a mapmaker, a siege engineer, a runaway noble's bastard, a bard who spies, a horse-breaker, a widow with a grudge and a ledger — or anything else that fits this place and this moment. Grounded and human; no magic, no prophecy, nothing only the world itself could decide.
Write everything in {language} except ""role_en"".
Answer ONLY with JSON: { ""name"": ""a first name that fits the culture (and a byname if natural)"", ""female"": true or false, ""age"": 20 to 60, ""role"": ""their trade or calling, 2 to 5 words"", ""role_en"": ""the same in English"",
  ""personality"": ""..."", ""backstory"": ""what brought them here, with one concrete event"", ""speech_style"": ""how they talk"", ""goals"": ""what they want now"", ""secret"": ""something true they hide"",
  ""arrival"": ""one sentence of gossip about their arrival, as the townsfolk tell it"" }");

        public static void Touch() { var _ = TUnique.Key; }

        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                var st = WorldSim.State;
                if (!cfg.EnableUniqueCharacters || Campaign.Current == null || !WorldSim.CanCall) return;
                double now = CampaignTime.Now.ToDays;
                if (st.NextUniqueDay < 0) { st.NextUniqueDay = now + cfg.UniqueCharacterDays * (0.5 + Rng.NextDouble() * 0.5); return; }
                if (now < st.NextUniqueDay || WorldSim.IsReserved("unique")) return;
                double next = now + cfg.UniqueCharacterDays * (0.7 + Rng.NextDouble() * 0.6);
                st.UniqueIds.RemoveAll(id => Hero.Find(id) == null || !Hero.Find(id).IsAlive);
                if (st.UniqueIds.Count >= cfg.UniqueCharacterMax) { st.NextUniqueDay = next; return; }

                var here = MobileParty.MainParty.GetPosition2D;
                var town = Settlement.All.Where(s => s.IsTown && !s.IsUnderSiege).OrderBy(s => s.GetPosition2D.Distance(here)).Take(4).OrderBy(_ => Rng.Next()).FirstOrDefault();
                if (town == null) { st.NextUniqueDay = next; return; }
                string existing = st.UniqueIds.Count == 0 ? "(none yet)" : string.Join("; ", st.UniqueIds.Select(id => MemoryStore.Find(id)).Where(m => m != null).Select(m => m.HeroName + (string.IsNullOrWhiteSpace(m.Profile?.Reputation) ? "" : " — " + m.Profile.Reputation)).Take(8));
                var sb = new StringBuilder();
                sb.AppendLine($"- {town.Name}: {(town.Town.Prosperity < 2500 ? "poor" : town.Town.Prosperity > 5500 ? "thriving" : "ordinary")}, held by {town.OwnerClan?.Name}; {CampaignTime.Now.GetSeasonOfYear}.");
                try { var sick = Diseases.DescribeAt(town); if (sick.Length > 0) sb.AppendLine("- " + sick); } catch { }
                var wars = WorldSim.Kingdoms.Where(k => town.MapFaction != null && k != town.MapFaction && town.MapFaction.IsAtWarWith(k)).Select(k => k.Name.ToString()).ToList();
                if (wars.Count > 0) sb.AppendLine("- The realm is at war with: " + string.Join(", ", wars) + ".");
                sb.AppendLine("- Lately: " + WorldSim.DescribeRecentNews(5));
                string prompt = TUnique.Format("town", town.Name, "culture", town.Culture?.Name?.ToString() ?? "", "faction", town.MapFaction?.Name?.ToString() ?? "", "context", sb.ToString().TrimEnd(), "existing", existing, "language", cfg.Language);
                // The schedule only advances when the reply arrives, so a failed call is retried another day
                WorldSim.Ask(L.T("AIL_58e1ca60", "personaje único"), new List<ChatMessage> { new ChatMessage("user", prompt) }, 900,
                    j => { WorldSim.State.NextUniqueDay = next; Create(town, j); }, key: "unique");
            }
            catch (Exception ex) { Log.Error("Unique characters", ex); }
        }

        private static void Create(Settlement town, JObject j)
        {
            try
            {
                string name = WorldSim.Clean((string)j["name"], 40);
                if (name.Length < 2 || town == null) return;
                bool female = j["female"] != null && j["female"].Type == JTokenType.Boolean && (bool)j["female"];
                int age = 30; try { age = Math.Max(20, Math.Min(60, (int?)j["age"] ?? 30)); } catch { }
                var templates = CharacterObject.All.Where(c => c.IsTemplate && c.Occupation == Occupation.Wanderer && c.IsFemale == female).ToList();
                var template = templates.Where(c => c.Culture == town.Culture).OrderBy(_ => Guid.NewGuid()).FirstOrDefault() ?? templates.OrderBy(_ => Guid.NewGuid()).FirstOrDefault();
                if (template == null) return;
                var hero = HeroCreator.CreateSpecialHero(template, town, null, null, age);
                var full = new TextObject("{=!}" + name);
                hero.SetName(full, full);
                hero.SetNewOccupation(Occupation.Wanderer);
                hero.ChangeState(Hero.CharacterStates.Active);
                EnterSettlementAction.ApplyForCharacterOnly(hero, town);

                var m = MemoryStore.Get(hero.StringId, name);
                string role = WorldSim.Clean((string)j["role"], 80), roleEn = WorldSim.Clean((string)j["role_en"], 80);
                m.Profile = new CharacterProfile
                {
                    Personality = WorldSim.Clean((string)j["personality"], 500), Backstory = WorldSim.Clean((string)j["backstory"], 600), SpeechStyle = WorldSim.Clean((string)j["speech_style"], 300),
                    Goals = WorldSim.Clean((string)j["goals"], 300), Secret = WorldSim.Clean((string)j["secret"], 300), Reputation = role,
                };
                m.CustomInstructions.Add($"Your trade or calling: {(roleEn.Length > 2 ? roleEn : role)}. You arrived in {town.Name} only recently.");
                WorldSim.State.UniqueIds.Add(hero.StringId);
                string arrival = WorldSim.Clean((string)j["arrival"], 300);
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 1, Text = $"A newcomer has arrived in {town.Name}: {name}, {(roleEn.Length > 2 ? roleEn : role)}.",
                    TitleEs = L.F("AIL_c3a3b332", $"Llega a {town.Name}: {name}"), TextEs = arrival.Length > 10 ? arrival : L.F("AIL_e95b93ac", $"{name}, {role}, ha llegado a {town.Name}."), SettlementId = town.StringId,
                    Kingdoms = town.MapFaction != null ? new List<string> { town.MapFaction.StringId } : new List<string>(), Heroes = new List<string> { hero.StringId },
                });
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_1c42ff4a", $"Se cuenta por {town.Name}: ha llegado {name}, {role}."), new Color(0.4f, 0.9f, 0.95f)));
                SaveFiles.Flush();
                Log.Info($"[uniques] {name} ({role}) arrives in {town.Name} [{hero.StringId}]");
            }
            catch (Exception ex) { Log.Error("Create unique character", ex); }
        }
    }

    /// <summary>
    /// Chronicles: when someone you really dealt with dies, the AI writes their story (who they were, what happened between
    /// you, how it ended). It goes into the News and pops up right away.
    /// </summary>
    internal static class Chronicle
    {
        private static readonly Tpl TLife = new Tpl("memory/death_chronicle.txt",
@"{name} has died in Calradia, the medieval world of Mount & Blade II: Bannerlord. Write the short chronicle of this life as a court scribe would set it down: who they were, what they did, what there was between them and {player}, and how it ended. Use ONLY what is given below; invent no battles, titles or kin that are not there. Plain and humane, not flowery. {min_len} to {max_len} characters, in {language}.
WHO THEY WERE
{facts}
HOW THEY DIED
{death}
WHAT THERE WAS BETWEEN THEM AND {player}
{history}
Answer ONLY with JSON: { ""title"": ""3 to 7 words"", ""text"": ""the chronicle"" }");

        public static void Touch() { var _ = TLife.Key; }

        public static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.EnableChronicles || victim == null || victim == Hero.MainHero || Campaign.Current == null) return;
                var m = MemoryStore.Find(victim.StringId);
                if (m == null || m.ConversationCount < cfg.ChronicleMinTalks) return;
                var sb = new StringBuilder();
                sb.AppendLine($"{victim.Name}, {(victim.IsFemale ? "woman" : "man")}, died at {(int)victim.Age}; {victim.Culture?.Name}; {(victim.IsLord ? $"noble of house {victim.Clan?.Name}" : victim.IsNotable ? "a notable" : "a wanderer")}{(victim.Clan != null ? Facts.Allegiance(victim) : victim.MapFaction != null ? " of " + victim.MapFaction.Name : "")}.");
                if (victim.Spouse != null) sb.AppendLine($"Married to {victim.Spouse.Name}.");
                if (victim.Children.Count > 0) sb.AppendLine("Children: " + string.Join(", ", victim.Children.Take(6).Select(c => c.Name.ToString())) + ".");
                if (m.Profile != null && !m.Profile.IsEmpty) sb.AppendLine($"Character: {m.Profile.Personality} Past: {m.Profile.Backstory}");
                string death = detail.ToString() + (killer != null ? $", at the hands of {killer.Name}" : "");
                var hist = new StringBuilder();
                if (!string.IsNullOrWhiteSpace(m.Summary)) hist.AppendLine(m.Summary);
                foreach (var n in m.Notes.Skip(Math.Max(0, m.Notes.Count - 8))) hist.AppendLine("- " + n);
                foreach (var p in m.Pages.Skip(Math.Max(0, m.Pages.Count - 3))) hist.AppendLine($"- {p.Title}: {(p.Text.Length > 300 ? p.Text.Substring(0, 300) : p.Text)}");
                if (hist.Length == 0) hist.AppendLine("(they spoke a few times; nothing of note is recorded)");
                string player = Hero.MainHero.Name.ToString(), name = victim.Name.ToString(), id = victim.StringId;
                string prompt = TLife.Format("name", name, "player", player, "facts", sb.ToString().TrimEnd(), "death", death, "history", hist.ToString().TrimEnd(), "min_len", 300, "max_len", 900, "language", cfg.Language);
                // Queued if another request is in flight
                WorldSim.Ask(L.T("AIL_8dc2db66", "crónica de una muerte"), new List<ChatMessage> { new ChatMessage("user", prompt) }, 800, j =>
                {
                    string title = WorldSim.Clean((string)j["title"], 80), text = WorldSim.Clean((string)j["text"], 1200);
                    if (text.Length < 40) return;
                    if (title.Length < 3) title = L.F("AIL_f8ac2cac", $"Crónica de {name}");
                    m.Pages.Add(new MemoryPage { Title = title, Text = text, Day = CampaignTime.Now.ToDays });
                    while (m.Pages.Count > Math.Max(5, S.Get.MaxMemoryPages)) m.Pages.RemoveAt(0);
                    NewsStore.Add(new NewsItem { Type = "death", Importance = 2, Text = $"The chronicle of {name}, who has died, has been written.", TitleEs = title, TextEs = text, Heroes = new List<string> { id }, PlayerInvolved = true });
                    SaveFiles.Flush();
                    InformationManager.ShowInquiry(new InquiryData(title, text, true, false, L.T("AIL_e8438e2a", "Cerrar"), "", null, null), true);
                }, key: "chronicle:" + id);
            }
            catch (Exception ex) { Log.Error("Death chronicle", ex); }
        }
    }
}
