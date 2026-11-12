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
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>
    /// Entrenamiento en la arena: en una ciudad, pagas y tus hombres pasan ocho horas entrenando. Ganan experiencia (más
    /// los novatos), alguno se lesiona, y esa arena no vuelve a admitiros hasta pasados unos días.
    /// </summary>
    internal static class ArenaTraining
    {
        private const float Hours = 8f;
        private static float _progress;
        private static bool _running;
        private static Dictionary<string, double> LastAt => WorldSim.State.ArenaLastAt;      // se guarda con la partida
        private static readonly Random Rng = new Random();

        private static int Cost()
        {
            var st = Settlement.CurrentSettlement;
            float f = st != null && st.OwnerClan == Clan.PlayerClan ? 0.6f : 1f;
            return (int)Math.Ceiling(MobileParty.MainParty.MemberRoster.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => (e.Number - e.WoundedNumber) * (8 + e.Character.Tier * 4)) * f * S.Get.ArenaTrainingCostPercent / 100f);
        }

        private static int Fit() => MobileParty.MainParty.MemberRoster.GetTroopRoster().Where(e => !e.Character.IsHero).Sum(e => e.Number - e.WoundedNumber);

        private static void Finish()
        {
            if (!_running) return;
            _running = false;
            try
            {
                var cfg = S.Get;
                var roster = MobileParty.MainParty.MemberRoster;
                int trained = 0, hurt = 0;
                foreach (var e in roster.GetTroopRoster().Where(e => !e.Character.IsHero && e.Number - e.WoundedNumber > 0).ToList())
                {
                    int n = e.Number - e.WoundedNumber;
                    // Los novatos aprenden más deprisa que los veteranos
                    int xpEach = (int)(cfg.ArenaTrainingXp * (1.5f - Math.Min(1f, e.Character.Tier / 6f)));
                    roster.AddXpToTroop(e.Character, xpEach * n);
                    trained += n;
                    int injured = 0;
                    for (int i = 0; i < n; i++) if (Rng.Next(100) < cfg.ArenaTrainingInjuryPercent) injured++;
                    if (injured > 0) { roster.WoundTroop(e.Character, injured); hurt += injured; }
                }
                var st = Settlement.CurrentSettlement;
                if (st != null) LastAt[st.StringId] = CampaignTime.Now.ToHours;
                InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_980877af", $"Tus hombres terminan el entrenamiento: {trained} han ganado experiencia") + (hurt > 0 ? L.F("AIL_66fb1576", $", {hurt} se han lesionado.") : "."), Colors.Green));
                Log.Info($"[arena] entrenamiento terminado: {trained} entrenados, {hurt} lesionados");
            }
            catch (Exception ex) { Log.Error("Fin del entrenamiento", ex); }
            try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = false; } catch { }
            GameMenu.SwitchToMenu("town_arena");
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
                    int cost = Cost();
                    var st = Settlement.CurrentSettlement;
                    if (Hero.MainHero.Gold < cost) return;
                    GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, st, cost);
                    _progress = 0f; _running = true;
                    GameMenu.SwitchToMenu("ailegacy_arena_training");
                }, false, 2);

            starter.AddWaitGameMenu("ailegacy_arena_training", L.T("AIL_76e00257", "{=!}Tus hombres se entrenan en la arena bajo los maestros de armas. Se oyen los golpes de las espadas de madera y los gritos de los instructores."),
                args => { try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = true; } catch { } },
                args => { args.MenuContext.GameMenu.SetTargetedWaitingTimeAndInitialProgress(Hours, _progress / Hours); return true; },
                args => Finish(),
                (args, dt) =>
                {
                    _progress += (float)dt.ToHours;
                    args.MenuContext.GameMenu.SetProgressOfWaitingInMenu(Math.Min(1f, _progress / Hours));
                    if (_progress >= Hours) Finish();
                },
                GameMenu.MenuAndOptionType.WaitMenuShowOnlyProgressOption, GameMenu.MenuOverlayType.None, Hours);

            starter.AddGameMenuOption("ailegacy_arena_training", "ailegacy_arena_stop", L.T("AIL_48173f1f", "{=!}Interrumpir el entrenamiento (no te devuelven el dinero)"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args =>
                {
                    _running = false;
                    try { if (PlayerEncounter.Current != null) PlayerEncounter.Current.IsPlayerWaiting = false; } catch { }
                    GameMenu.SwitchToMenu("town_arena");
                }, true);
        }
    }

    /// <summary>
    /// Personajes únicos: de vez en cuando llega a una ciudad cercana alguien nuevo, inventado por la IA para esta partida
    /// (un predicador, una capitana mercenaria, un médico huido, un pretendiente…), con nombre, oficio, carácter y motivo
    /// para estar allí. Vive como un errante más: puedes buscarle, tratar con él o tomarle a tu servicio.
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
                if (now < st.NextUniqueDay) return;
                st.NextUniqueDay = now + cfg.UniqueCharacterDays * (0.7 + Rng.NextDouble() * 0.6);
                st.UniqueIds.RemoveAll(id => Hero.Find(id) == null || !Hero.Find(id).IsAlive);
                if (st.UniqueIds.Count >= cfg.UniqueCharacterMax) return;

                var here = MobileParty.MainParty.GetPosition2D;
                var town = Settlement.All.Where(s => s.IsTown && !s.IsUnderSiege).OrderBy(s => s.GetPosition2D.Distance(here)).Take(4).OrderBy(_ => Rng.Next()).FirstOrDefault();
                if (town == null) return;
                string existing = st.UniqueIds.Count == 0 ? "(none yet)" : string.Join("; ", st.UniqueIds.Select(id => MemoryStore.Find(id)).Where(m => m != null).Select(m => m.HeroName + (string.IsNullOrWhiteSpace(m.Profile?.Reputation) ? "" : " — " + m.Profile.Reputation)).Take(8));
                var sb = new StringBuilder();
                sb.AppendLine($"- {town.Name}: {(town.Town.Prosperity < 2500 ? "poor" : town.Town.Prosperity > 5500 ? "thriving" : "ordinary")}, held by {town.OwnerClan?.Name}; {CampaignTime.Now.GetSeasonOfYear}.");
                try { var sick = Diseases.DescribeAt(town); if (sick.Length > 0) sb.AppendLine("- " + sick); } catch { }
                var wars = WorldSim.Kingdoms.Where(k => town.MapFaction != null && k != town.MapFaction && town.MapFaction.IsAtWarWith(k)).Select(k => k.Name.ToString()).ToList();
                if (wars.Count > 0) sb.AppendLine("- The realm is at war with: " + string.Join(", ", wars) + ".");
                sb.AppendLine("- Lately: " + WorldSim.DescribeRecentNews(5));
                string prompt = TUnique.Format("town", town.Name, "culture", town.Culture?.Name?.ToString() ?? "", "faction", town.MapFaction?.Name?.ToString() ?? "", "context", sb.ToString().TrimEnd(), "existing", existing, "language", cfg.Language);
                WorldSim.Ask(L.T("AIL_58e1ca60", "personaje único"), new List<ChatMessage> { new ChatMessage("user", prompt) }, 900, j => Create(town, j));
            }
            catch (Exception ex) { Log.Error("Personajes únicos", ex); }
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
                Log.Info($"[únicos] {name} ({role}) llega a {town.Name} [{hero.StringId}]");
            }
            catch (Exception ex) { Log.Error("Crear personaje único", ex); }
        }
    }

    /// <summary>
    /// Crónicas: cuando muere alguien con quien has tratado de verdad, la IA escribe su historia (quién fue, qué hubo entre
    /// vosotros, cómo acabó). Queda en Noticias y te sale al momento.
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
                sb.AppendLine($"{victim.Name}, {(victim.IsFemale ? "woman" : "man")}, died at {(int)victim.Age}; {victim.Culture?.Name}; {(victim.IsLord ? $"noble of house {victim.Clan?.Name}" : victim.IsNotable ? "a notable" : "a wanderer")}{(victim.MapFaction != null ? " of " + victim.MapFaction.Name : "")}.");
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
                WorldSim.Ask(L.T("AIL_8dc2db66", "crónica de una muerte"), new List<ChatMessage> { new ChatMessage("user", prompt) }, 800, j =>
                {
                    string title = WorldSim.Clean((string)j["title"], 80), text = WorldSim.Clean((string)j["text"], 1200);
                    if (text.Length < 40) return;
                    if (title.Length < 3) title = L.F("AIL_f8ac2cac", $"Crónica de {name}");
                    m.Pages.Add(new MemoryPage { Title = title, Text = text, Day = CampaignTime.Now.ToDays });
                    NewsStore.Add(new NewsItem { Type = "death", Importance = 2, Text = $"The chronicle of {name}, who has died, has been written.", TitleEs = title, TextEs = text, Heroes = new List<string> { id }, PlayerInvolved = true });
                    SaveFiles.Flush();
                    InformationManager.ShowInquiry(new InquiryData(title, text, true, false, L.T("AIL_e8438e2a", "Cerrar"), "", null, null), true);
                });
            }
            catch (Exception ex) { Log.Error("Crónica de una muerte", ex); }
        }
    }
}
