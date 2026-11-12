using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// El juicio. Cuando la guardia te encierra, el señor del lugar decide qué hacer contigo: soltarte, multarte o tenerte
    /// unos días en la mazmorra, y cuánto pesa en tu fama allí. Su sentencia se pregona: es noticia.
    /// </summary>
    internal static class Verdict
    {
        private static readonly Tpl TVerdict = new Tpl("justice/lord_verdict.txt",
@"You are {lord}, lord of {settlement}, of {faction}, in Calradia, the medieval world of Mount & Blade II: Bannerlord.
Who you are: {lord_desc}
Your guard has thrown a prisoner into your cells and it falls to you to judge them.
The prisoner: {player} — {standing}. Their record with the law of your realm before this: {crime}. What you personally think of them: {relation}.{memory}
What they did: {case}
What passed between them and your guard:
{talk}

Judge as this lord would, by temperament, by interest and by what is wise: a harsh lord punishes, a merciful or pragmatic one weighs who the prisoner is and what they could be worth as a friend or cost as an enemy, a greedy one prefers coin. Fists in the street are small; drawn steel is serious; blood or death is grave; insolence to your guard makes everything worse.
Choose ONE sentence:
- ""release"": they walk free, with or without a warning.
- ""fine"": they pay ""fine"" denars and walk free (they carry about {gold}; a fine they cannot pay keeps them locked up).
- ""prison"": they stay in your cells for ""days"" days (1 to {max_days}).
Also decide ""crime_change"": how this changes their record in your realm, from -15 (you wipe the slate) to +30 (a marked offender), or up to +45 ONLY when blood was spilled and you declare them an outlaw, barred from your lands; and ""relation_change"": how it changes what you think of them, from -5 to +2.
Then write the decree as your herald cries it in the square: 1 to 3 sentences in {language}, in your voice, naming the offence and the sentence.
Answer ONLY with JSON: { ""sentence"": ""release|fine|prison"", ""fine"": 0, ""days"": 0, ""crime_change"": 0, ""relation_change"": 0, ""decree"": ""..."", ""decree_en"": ""the same in ONE English sentence"" }");

        public static void Touch() { var _ = TVerdict.Key; }

        private static Hero P => Hero.MainHero;
        // La condena en curso se guarda con la partida (no se pierde al cargar ni se cuela en otra)
        private static Settlement _in
        {
            get { var id = WorldSim.State.JailSettlementId; return string.IsNullOrEmpty(id) ? null : Settlement.Find(id); }
            set { WorldSim.State.JailSettlementId = value?.StringId; }
        }
        private static double _releaseDay
        {
            get => WorldSim.State.JailReleaseDay;
            set => WorldSim.State.JailReleaseDay = value;
        }
        private static bool _busy;

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        public static void ResetSession() { _busy = false; }

        /// <summary>Te acaban de encerrar: el señor del lugar dicta sentencia.</summary>
        public static void Judge(Settlement st, string caseEn, Speaker guard, string standing, string crime)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.AllowVerdict || _busy || st == null) return;
                var lord = st.OwnerClan?.Leader;
                if (lord == null || !lord.IsAlive || lord == P || lord.IsPrisoner) return;
                if (st.OwnerClan == Clan.PlayerClan || (st.MapFaction != null && st.MapFaction == P.MapFaction && P.MapFaction is Kingdom)) return;     // en tu feudo o tu reino no te juzgan
                var judge = Speaker.FromHero(lord);
                string player = ContextBuilder.PlayerNameFor(judge) ?? (guard != null ? ContextBuilder.PlayerNameFor(guard) : null);
                if (player == null) { Log.Info($"[juicio] nadie en {st.Name} sabe quién eres: no hay sentencia con tu nombre"); return; }

                var p = judge.Memory.Profile;
                string desc = $"{(lord.IsFemale ? "woman" : "man")}, {(int)lord.Age}, {lord.Culture?.Name}" + (p != null && !p.IsEmpty ? $". {p.Personality}" : "")
                              + $" Honor {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor)}, mercy {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Mercy)}, "
                              + $"generosity {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Generosity)}, calculating {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Calculating)} (each from -2 to 2).";
                string memory = string.IsNullOrWhiteSpace(judge.Memory.Summary) ? "" : " Your past dealings: " + judge.Memory.Summary;
                string talk = guard == null || guard.Memory.Turns.Count == 0 ? "(the guard reports they were taken without much talk)"
                    : string.Join("\n", guard.Memory.Turns.Skip(Math.Max(0, guard.Memory.Turns.Count - 8)).Select(t => (t.Role == "npc" ? "Guard: " : "Prisoner: ") + (t.Text.Length > 220 ? t.Text.Substring(0, 220) + "…" : t.Text)));
                string prompt = TVerdict.Format("lord", lord.Name, "settlement", st.Name, "faction", st.MapFaction?.Name?.ToString() ?? "no realm", "lord_desc", desc, "player", player,
                    "standing", standing ?? "an outsider", "crime", crime ?? "unknown", "relation", ContextBuilder.DescribeRelation((int)lord.GetRelationWithPlayer()), "memory", memory,
                    "case", string.IsNullOrWhiteSpace(caseEn) ? "a disturbance of the peace" : caseEn.Replace("the armed person in front of you", "the prisoner").Replace("You saw the end of it.", "The guard saw the end of it."),
                    "talk", talk, "gold", P.Gold, "max_days", cfg.VerdictMaxDays, "language", cfg.Language);

                _busy = true;
                int session = WorldSim.Session;
                Say(L.F($"Esperas en los calabozos de {st.Name} a que {lord.Name} decida qué hacer contigo…"), Colors.Gray);
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, maxTokensOverride: 500, preferredProvider: cfg.ProviderHeroes).ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        _busy = false;
                        try
                        {
                            if (session != WorldSim.Session) { Log.Info("[juicio] sentencia de otra partida, descartada"); return; }
                            if (res == null || !res.Ok) { Log.Warn("[juicio] la IA no ha dado sentencia: " + res?.Error); return; }
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            Apply(st, lord, judge, player, JObject.Parse(res.Text.Substring(a, b - a + 1)));
                        }
                        catch (Exception ex) { Log.Error("Sentencia", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Juicio", ex); _busy = false; }
        }

        private static int Int(JToken t, int min, int max)
        {
            int v = 0;
            try { if (t != null) int.TryParse(new string(t.ToString().Where(c => char.IsDigit(c) || c == '-').ToArray()), out v); } catch { }
            return Math.Max(min, Math.Min(max, v));
        }

        private static bool HeldIn(Settlement st)
        {
            try { return PlayerCaptivity.IsCaptive && PlayerCaptivity.CaptorParty != null && PlayerCaptivity.CaptorParty.Settlement == st; }
            catch { return false; }
        }

        private static void Apply(Settlement st, Hero lord, Speaker judge, string player, JObject j)
        {
            var cfg = S.Get;
            string sentence = ((string)j["sentence"] ?? "prison").Trim().ToLowerInvariant();
            int fine = Int(j["fine"], 0, 1000000), days = Int(j["days"], 0, cfg.VerdictMaxDays);
            int crime = Int(j["crime_change"], -15, 45), rel = Int(j["relation_change"], -5, 2);
            string decree = ((string)j["decree"] ?? "").Trim(), decreeEn = ((string)j["decree_en"] ?? "").Trim();
            bool held = HeldIn(st);
            string outcome;

            if (sentence.StartsWith("fine") && fine > 0)
            {
                if (P.Gold >= fine)
                {
                    GiveGoldAction.ApplyForCharacterToSettlement(P, st, fine);
                    if (held) PlayerCaptivity.EndCaptivity();
                    outcome = L.F($"Pagas {fine} denarios y quedas libre.");
                }
                else
                {
                    days = Math.Max(1, Math.Min(cfg.VerdictMaxDays, 2 + fine / 500));
                    _in = st; _releaseDay = CampaignTime.Now.ToDays + days;
                    outcome = L.F($"No puedes pagar los {fine} denarios de multa: te quedas {days} día(s) en la mazmorra.");
                }
            }
            else if (sentence.StartsWith("release"))
            {
                if (held) PlayerCaptivity.EndCaptivity();
                outcome = L.T("Quedas libre.");
            }
            else
            {
                days = Math.Max(1, days);
                _in = st; _releaseDay = CampaignTime.Now.ToDays + days;
                outcome = L.F($"{days} día(s) de mazmorra.");
            }

            // Tu fama allí: nunca tanto como para que te declaren enemigo solo por la sentencia
            try
            {
                if (st.MapFaction != null && crime != 0)
                {
                    float cur = st.MapFaction.MainHeroCrimeRating;
                    float delta = crime > 0 ? Math.Max(0f, Math.Min(crime, 85f - cur)) : crime;      // puede dejarte proscrito, pero no sin límite
                    if (Math.Abs(delta) >= 1f) ChangeCrimeRatingAction.Apply(st.MapFaction, delta, true);
                }
                if (rel != 0) ChangeRelationAction.ApplyPlayerRelation(lord, rel, affectRelatives: false, showQuickNotification: true);
            }
            catch (Exception ex) { Log.Error("Sentencia: efectos", ex); }

            if (decree.Length < 5) decree = outcome;
            if (decreeEn.Length < 5) decreeEn = $"{lord.Name} judged {player} at {st.Name}: {sentence}.";
            judge.Memory.Turns.Add(new ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays, Text = $"[What happened: {player} was brought before you as a prisoner at {st.Name} and you judged them. Your decree: {decreeEn}]" });
            ChatSession.AddNote(judge.Memory, $"I judged {player} at {st.Name}: {decreeEn}", cfg.MaxNotesPerHero, judge.Name);
            NewsStore.Add(new NewsItem
            {
                Type = "decree", Importance = 2, Text = $"{lord.Name}, lord of {st.Name}, passed judgment on {player}: {decreeEn}", PlayerInvolved = true,
                TitleEs = L.F($"Sentencia de {lord.Name}"), TextEs = decree, SettlementId = st.StringId,
                Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(), Heroes = new List<string> { lord.StringId, P.StringId },
            });
            SaveFiles.Flush();
            Log.Info($"[juicio] {lord.Name} en {st.Name}: {sentence} (multa {fine}, días {days}, fama {crime:+0;-0}, relación {rel:+0;-0})");
            InformationManager.ShowInquiry(new InquiryData(L.F($"Sentencia de {lord.Name}"), $"«{decree}»\n\n{outcome}", true, false, L.T("Entendido"), null, null, null), true);
        }

        /// <summary>Cumplida la condena, te sueltan (si no te has escapado antes).</summary>
        public static void OnDailyTick()
        {
            try
            {
                if (_releaseDay < 0 || _in == null) return;
                if (!HeldIn(_in)) { _releaseDay = -1; _in = null; return; }
                if (CampaignTime.Now.ToDays < _releaseDay) return;
                var st = _in; _releaseDay = -1; _in = null;
                PlayerCaptivity.EndCaptivity();
                Say(L.F($"Has cumplido tu condena: te sueltan de la mazmorra de {st.Name}."), Colors.Yellow);
            }
            catch (Exception ex) { Log.Error("Fin de condena", ex); _releaseDay = -1; _in = null; }
        }
    }
}
