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
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// When the guard locks the player up, the local lord judges them, as in AIInfluence: release, some days in the
    /// dungeon (1 to 20 by default) or, if allowed, execution, plus the effect on their crime rating there. There is
    /// no fine and no pursuit. While the term runs, now and then a chance to escape comes (a game menu: try, with odds
    /// from Roguery, or stay), at most twice per imprisonment; caught or not, the lord judges again. Every outcome is
    /// a game menu with "Continue". The decree is cried in public, so it becomes news.
    /// </summary>
    internal static class Verdict
    {
        private static readonly Tpl TVerdict = new Tpl("justice/lord_verdict.txt",
@"You are {lord}, lord of {settlement}, of {faction}, in Calradia, the medieval world of Mount & Blade II: Bannerlord.
Who you are: {lord_desc}
{situation}
The prisoner: {player} — {standing}. Their record with the law of your realm before this: {crime}. What you personally think of them: {relation}.{memory}
What they did: {case}
What passed between them and your guard:
{talk}

Judge as yourself — by your character, your mood and what the player is to you — not as a fair-minded stranger: a harsh lord is harsh, a greedy one weighs what it can bring in, a merciful one forgives, a proud one avenges a slight. Weigh what was done and to whom, who started it, whether the player gave themselves up or ran from your guard, and who the player is: harming a noble guest, a lord of another realm or the kin of a strong house brings consequences on you too. Fists in the street are small; drawn steel is serious; blood or death is grave; insolence to your guard makes everything worse.
{sentence_rules}
Also decide ""crime_change"": how this changes their record in your realm, from -15 (you wipe the slate) to +30 (a marked offender), or up to +45 ONLY when blood was spilled and you declare them an outlaw, barred from your lands; and ""relation_change"": how it changes what you think of them, from -5 to +2.
Your judgement is final and done now: there is no later hearing, plea, bargain, fine or payment that can shorten or lift it. Do not promise in the decree any reduction, pardon or release on conditions the player could meet later, and do not invite them to ask, pay or offer anything to change it.
Then write the decree as your herald cries it in the square: 1 to 3 sentences in {language}, in your voice, naming the offence and the sentence. Name the player by name.
Answer ONLY with JSON: {json_shape}");

        private static readonly Tpl TSituationNew = new Tpl("justice/verdict_situation_new.txt",
            "Your guard has thrown a prisoner into your cells and it falls to you to judge them.");
        private static readonly Tpl TSituationAgain = new Tpl("justice/verdict_situation_caught.txt",
            "The prisoner you sentenced earlier tried to escape from your dungeon and was caught by the guards. They are already serving a term in your cells ({left} days left). Judge them again for this.");
        private static readonly Tpl TSituationAway = new Tpl("justice/verdict_situation_escaped.txt",
            "The prisoner you sentenced earlier escaped from your dungeon and is gone. Judge them for it.");

        private static readonly Tpl TRulesHeld = new Tpl("justice/verdict_rules_held.txt",
@"The prisoner is in your dungeon at {settlement}, and you decide their fate. Choose ONE sentence:
- ""release"": let them go now, with or without a warning.
- ""prison"": keep them in the dungeon; ""prison_days"" for how many days, {min_days} to {max_days}. Their men stay taken while they sit.
{execute}");
        private static readonly Tpl TRulesAgain = new Tpl("justice/verdict_rules_caught.txt",
@"The prisoner is back in your dungeon at {settlement}, and you decide their fate. Choose ONE sentence:
- ""release"": let them go now; the rest of their term is forgiven.
- ""prison"": they are already serving a term in your dungeon ({left} days left); ""prison_days"" is how many days to ADD to it, {min_days} to {max_days}.
{execute}");
        private static readonly Tpl TRulesExecute = new Tpl("justice/verdict_rules_execute.txt",
            "- \"execute\": have them put to death. It cannot be undone, and their kin, clan and realm will remember who did it.");
        private static readonly Tpl TRulesAway = new Tpl("justice/verdict_rules_escaped.txt",
@"The player is not in your hands, so there is no sentence on their body now: ""sentence"" is ""none"". Your decree may say what awaits them if they are caught; if your guard brings them in, you will judge them again.");

        public static void Touch() { var _ = TVerdict.Key + TSituationNew.Key + TSituationAgain.Key + TSituationAway.Key + TRulesHeld.Key + TRulesAgain.Key + TRulesExecute.Key + TRulesAway.Key; }

        private static Hero P => Hero.MainHero;
        private static WorldState St => WorldSim.State;
        // The current sentence lives in the save, so it survives a load and doesn't leak into another campaign
        private static Settlement _in
        {
            get { var id = St.JailSettlementId; return string.IsNullOrEmpty(id) ? null : Settlement.Find(id); }
            set { St.JailSettlementId = value?.StringId; }
        }
        private static double _releaseDay
        {
            get => St.JailReleaseDay;
            set => St.JailReleaseDay = value;
        }
        private static bool _busy;

        private static void Say(string msg, Color c) => InformationManager.DisplayMessage(new InformationMessage(msg, c));

        public static void ResetSession() { _busy = false; }

        private const string MenuReleased = "ailegacy_verdict_released", MenuServed = "ailegacy_sentence_served", MenuExecution = "ailegacy_verdict_execution",
                             MenuEscape = "ailegacy_escape_chance", MenuEscaped = "ailegacy_escape_success", MenuCaught = "ailegacy_escape_caught";
        // the captivity menu the game keeps the player in while held in a dungeon
        private const string CellMenu = "menu_captivity_castle_remain";

        private static Hero Judge_ => string.IsNullOrEmpty(St.JailJudgeId) ? null : Hero.Find(St.JailJudgeId);
        private static string JudgeName => Judge_?.Name?.ToString() ?? L.T("AIL_e7b1d80d", "el señor");
        private static string JailName => _in?.Name?.ToString() ?? Settlement.Find(St.JailSettlementId ?? "")?.Name?.ToString() ?? "?";

        /// <summary>The player was just thrown into the dungeon of <paramref name="st"/>: its lord judges them.</summary>
        public static void Judge(Settlement st, string caseEn, Speaker guard, string standing, string crime)
        {
            // a new imprisonment: new chances to escape
            St.JailEscapeTries = 0;
            St.JailCase = caseEn;
            St.JailStanding = standing;
            St.JailCrime = crime;
            Ask(st, caseEn, guard, standing, crime, "new");
        }

        /// <param name="how">new | caught | escaped</param>
        private static void Ask(Settlement st, string caseEn, Speaker guard, string standing, string crime, string how)
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.AllowVerdict || _busy || st == null) return;
                var lord = st.OwnerClan?.Leader;
                if (lord == null || !lord.IsAlive || lord == P || lord.IsPrisoner) return;
                if (st.OwnerClan == Clan.PlayerClan || (st.MapFaction != null && st.MapFaction == P.MapFaction && P.MapFaction is Kingdom)) return;     // no trial in your own fief or kingdom
                var judge = Speaker.FromHero(lord);
                string player = ContextBuilder.PlayerNameFor(judge) ?? (guard != null ? ContextBuilder.PlayerNameFor(guard) : null);
                if (player == null) { Log.Info($"[trial] nobody in {st.Name} knows who you are: no sentence under your name"); return; }
                St.JailJudgeId = lord.StringId;

                var p = judge.Memory.Profile;
                string desc = $"{(lord.IsFemale ? "woman" : "man")}, {(int)lord.Age}, {lord.Culture?.Name}" + (p != null && !p.IsEmpty ? $". {p.Personality}" : "")
                              + $" Honor {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor)}, mercy {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Mercy)}, "
                              + $"generosity {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Generosity)}, calculating {lord.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Calculating)} (each from -2 to 2).";
                string memory = string.IsNullOrWhiteSpace(judge.Memory.Summary) ? "" : " Your past dealings: " + judge.Memory.Summary;
                string talk = guard == null || guard.Memory.Turns.Count == 0 ? "(the guard reports they were taken without much talk)"
                    : string.Join("\n", guard.Memory.Turns.Skip(Math.Max(0, guard.Memory.Turns.Count - 8)).Select(t => (t.Role == "npc" ? "Guard: " : "Prisoner: ") + (t.Text.Length > 220 ? t.Text.Substring(0, 220) + "…" : t.Text)));
                int minDays = Math.Max(1, cfg.VerdictMinDays), maxDays = Math.Max(minDays, cfg.VerdictMaxDays);
                int left = Math.Max(0, (int)Math.Ceiling(_releaseDay - CampaignTime.Now.ToDays));
                string exec = cfg.VerdictAllowExecution ? TRulesExecute.Text.Trim() : "";
                string situation = how == "caught" ? TSituationAgain.Format("left", left) : how == "escaped" ? TSituationAway.Text.Trim() : TSituationNew.Text.Trim();
                string rules = how == "escaped" ? TRulesAway.Text.Trim()
                             : (how == "caught" ? TRulesAgain : TRulesHeld).Format("settlement", st.Name, "min_days", minDays, "max_days", maxDays, "left", left, "execute", exec).TrimEnd();
                string shape = how == "escaped" ? "{ \"sentence\": \"none\", \"crime_change\": 0, \"relation_change\": 0, \"decree\": \"...\", \"decree_en\": \"the same in ONE English sentence\" }"
                             : "{ \"sentence\": \"" + (cfg.VerdictAllowExecution ? "release|prison|execute" : "release|prison") + "\", \"prison_days\": 0, \"crime_change\": 0, \"relation_change\": 0, \"decree\": \"...\", \"decree_en\": \"the same in ONE English sentence\" }";
                string caseText = string.IsNullOrWhiteSpace(caseEn) ? "a disturbance of the peace" : caseEn.Replace("the armed person in front of you", "the prisoner").Replace("You saw the end of it.", "The guard saw the end of it.");
                if (how == "caught") caseText += " Then, while serving the term you gave them, they tried to break out of your dungeon and were caught.";
                if (how == "escaped") caseText += " Then, while serving the term you gave them, they broke out of your dungeon and got away.";
                string prompt = TVerdict.Format("lord", lord.Name, "settlement", st.Name, "faction", st.MapFaction?.Name?.ToString() ?? "no realm", "lord_desc", desc, "player", player,
                    "situation", situation, "standing", standing ?? "an outsider", "crime", crime ?? "unknown", "relation", ContextBuilder.DescribeRelation((int)lord.GetRelationWithPlayer()), "memory", memory,
                    "case", caseText, "talk", talk, "sentence_rules", rules, "json_shape", shape, "language", cfg.Language);

                _busy = true;
                int session = WorldSim.Session;
                if (how != "escaped") Say(L.F("AIL_7576bfa9", $"Esperas en los calabozos de {st.Name} a que {lord.Name} decida qué hacer contigo…"), Colors.Gray);
                LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "verdict", maxTokensOverride: 500, preferredProvider: cfg.ProviderHeroes).ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        _busy = false;
                        try
                        {
                            if (session != WorldSim.Session) { Log.Info("[trial] sentence from another game, discarded"); return; }
                            if (res == null || !res.Ok) { Log.Warn("[trial] the AI gave no sentence: " + res?.Error); return; }
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            Apply(st, lord, judge, player, JObject.Parse(res.Text.Substring(a, b - a + 1)), how);
                        }
                        catch (Exception ex) { Log.Error("Sentence", ex); }
                    });
                });
            }
            catch (Exception ex) { Log.Error("Trial", ex); _busy = false; }
        }

        private static int Int(JToken t, int min, int max)
        {
            int v = 0;
            try { if (t != null) int.TryParse(new string(t.ToString().Where(c => char.IsDigit(c) || c == '-').ToArray()), out v); } catch { }
            return Math.Max(min, Math.Min(max, v));
        }

        private static bool HeldIn(Settlement st)
        {
            try { return st != null && PlayerCaptivity.IsCaptive && PlayerCaptivity.CaptorParty != null && PlayerCaptivity.CaptorParty.Settlement == st; }
            catch { return false; }
        }

        private static void Apply(Settlement st, Hero lord, Speaker judge, string player, JObject j, string how)
        {
            var cfg = S.Get;
            string sentence = ((string)j["sentence"] ?? "prison").Trim().ToLowerInvariant();
            int minDays = Math.Max(1, cfg.VerdictMinDays), maxDays = Math.Max(minDays, cfg.VerdictMaxDays);
            int days = Int(j["prison_days"] ?? j["days"], 0, maxDays);
            int crime = Int(j["crime_change"], -15, 45), rel = Int(j["relation_change"], -5, 2);
            string decree = ((string)j["decree"] ?? "").Trim(), decreeEn = ((string)j["decree_en"] ?? "").Trim();
            bool held = how != "escaped" && HeldIn(st);
            string outcome, menu = null;

            if (how == "escaped" || !held)
            {
                // not in his hands: no sentence on the body, and nobody is sent after them
                sentence = "none";
                outcome = L.F("AIL_e7b1d80e", $"{lord.Name} no te persigue.");
            }
            else if (sentence.StartsWith("exec") && !cfg.VerdictAllowExecution)
            {
                // execution off: the longest term stands in for it
                sentence = "prison";
                days = maxDays;
                outcome = null;
            }
            else outcome = null;

            if (outcome == null)
            {
                if (sentence.StartsWith("exec"))
                {
                    outcome = L.T("AIL_e7b1d80f", "Te condenan a muerte.");
                    menu = MenuExecution;
                }
                else if (sentence.StartsWith("release") || sentence.StartsWith("free"))
                {
                    outcome = L.T("AIL_e7b1d810", "Te sacarán de la mazmorra.");
                    menu = MenuReleased;
                }
                else
                {
                    days = Math.Max(minDays, days);
                    double now = CampaignTime.Now.ToDays;
                    // caught escaping: the days are added to what is left of the term
                    double from = how == "caught" && _releaseDay > now ? _releaseDay : now;
                    _in = st; _releaseDay = from + days;
                    St.JailDays = how == "caught" ? St.JailDays + days : days;
                    outcome = how == "caught" ? L.F("AIL_e7b1d811", $"Se añaden {days} día(s) a tu condena en la mazmorra de {st.Name}.")
                                              : L.F("AIL_e7b1d812", $"Pasarás {days} día(s) en la mazmorra de {st.Name}.");
                }
            }

            string crimeLine = "";
            try
            {
                if (st.MapFaction != null && crime != 0)
                {
                    float cur = st.MapFaction.MainHeroCrimeRating;
                    float delta = crime > 0 ? Math.Max(0f, Math.Min(crime, 85f - cur)) : crime;      // can make you an outlaw, but crime rating is capped at 85
                    if (Math.Abs(delta) >= 1f)
                    {
                        ChangeCrimeRatingAction.Apply(st.MapFaction, delta, true);
                        if (delta > 0) crimeLine = L.F("AIL_e7b1d813", $"Tu fama de criminal en {st.MapFaction.Name} crece en {(int)delta}.");
                    }
                }
                if (rel != 0) ChangeRelationAction.ApplyPlayerRelation(lord, rel, affectRelatives: false, showQuickNotification: true);
            }
            catch (Exception ex) { Log.Error("Sentence: effects", ex); }

            if (decree.Length < 5) decree = outcome;
            if (decreeEn.Length < 5) decreeEn = $"{lord.Name} judged {player} at {st.Name}: {sentence}.";
            judge.Memory.Turns.Add(new ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays, Text = $"[What happened: {player} was {(how == "escaped" ? "judged in their absence after escaping your dungeon" : "brought before you as a prisoner")} at {st.Name} and you judged them. Your decree: {decreeEn}]" });
            ChatSession.AddNote(judge.Memory, $"I judged {player} at {st.Name}: {decreeEn}", cfg.MaxNotesPerHero, judge.Name);
            NewsStore.Add(new NewsItem
            {
                Type = "decree", Importance = 2, Text = $"{lord.Name}, lord of {st.Name}, passed judgment on {player}: {decreeEn}", PlayerInvolved = true,
                TitleEs = L.F("AIL_6c33ff94", $"Sentencia de {lord.Name}"), TextEs = decree, SettlementId = st.StringId,
                Kingdoms = st.MapFaction != null ? new List<string> { st.MapFaction.StringId } : new List<string>(), Heroes = new List<string> { lord.StringId, P.StringId },
            });
            SaveFiles.Flush();
            Log.Info($"[trial] {lord.Name} in {st.Name} ({how}): {sentence} (days {days}, crime {crime:+0;-0}, relationship {rel:+0;-0})");
            string body = $"«{Clean(decree)}»\n\n{outcome}" + (crimeLine.Length > 0 ? "\n" + crimeLine : "");
            // The judgement is read in a window; what it does to the player follows as a game menu with "Continue"
            InformationManager.ShowInquiry(new InquiryData(L.F("AIL_e7b1d814", $"Juicio de {lord.Name}"), body, true, false, L.T("AIL_e7b1d815", "Cerrar"), null,
                () => { if (menu != null) { St.JailMenu = menu; Pump(); } }, null), true);
        }

        private static string Clean(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");

        // ============================================================ game menus

        /// <summary>The menus of the dungeon: every outcome of a trial and the chance to escape. Called once per session.</summary>
        public static void AddMenus(CampaignGameStarter starter)
        {
            void Outcome(string id, Func<string> text, Action onContinue)
            {
                starter.AddGameMenu(id, "{=!}{AIL_JAIL_TEXT}", args => { try { MBTextManager.SetTextVariable("AIL_JAIL_TEXT", Clean(text())); } catch { } });
                starter.AddGameMenuOption(id, id + "_continue", "{=!}" + L.T("AIL_e7b1d816", "Continuar"),
                    args => { args.optionLeaveType = GameMenuOption.LeaveType.Continue; return true; },
                    args => { St.JailMenu = null; try { onContinue(); } catch (Exception ex) { Log.Error("Dungeon menu: " + id, ex); } }, true);
            }
            Outcome(MenuReleased, () => L.F("AIL_e7b1d817", $"Por orden de {JudgeName}, los guardias abren tu celda y te sacan de la mazmorra de {JailName}."), Free);
            Outcome(MenuServed, () => L.F("AIL_e7b1d818", $"Quedan atrás {St.JailDays} día(s) en la mazmorra de {JailName}. Como ordenó {JudgeName}, los guardias te dejan marchar."), Free);
            Outcome(MenuExecution, () => L.F("AIL_e7b1d819", $"Al amanecer vienen a por ti. {JudgeName} te ha condenado a muerte, y la sentencia se cumple ante la gente de {JailName}."), Execute);
            Outcome(MenuEscaped, () => L.F("AIL_e7b1d81a", $"Te escurres junto al guardia dormido, subes las escaleras y sales de la mazmorra de {JailName}. Al amanecer estás lejos de sus muros, pero {JudgeName} se enterará."), Escaped);
            Outcome(MenuCaught, () => L.F("AIL_e7b1d81b", $"Llegas hasta las escaleras antes de que los guardias te atrapen. Te dan una paliza y te devuelven a la celda de {JailName}. {JudgeName} volverá a juzgarte por esto."), Caught);

            starter.AddGameMenu(MenuEscape, "{=!}{AIL_JAIL_TEXT}", args =>
            {
                try
                {
                    int left = Math.Max(0, Math.Max(0, S.Get.VerdictEscapeMaxAttempts) - St.JailEscapeTries - 1);
                    MBTextManager.SetTextVariable("AIL_JAIL_TEXT", Clean(L.F("AIL_e7b1d81c", $"De noche, el guardia de tu celda se queda dormido y el viejo cerrojo cede un poco bajo tu mano. Podrías intentar escapar de la mazmorra de {JailName}. Si los guardias te atrapan, {JudgeName} volverá a juzgarte. Intentos que te quedan después de este: {left}.")));
                    MBTextManager.SetTextVariable("AIL_ESCAPE_ODDS", EscapeOdds());
                }
                catch { }
            });
            starter.AddGameMenuOption(MenuEscape, MenuEscape + "_try", "{=!}" + L.T("AIL_e7b1d81d", "Intentar escapar ({AIL_ESCAPE_ODDS} % de probabilidad)"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Escape; return true; },
                args =>
                {
                    St.JailEscapeTries++;
                    bool away = MBRandom.RandomFloat * 100f < EscapeOdds();
                    Log.Info($"[trial] the player tries to escape (attempt {St.JailEscapeTries}): {(away ? "gets away" : "is caught")}");
                    St.JailMenu = away ? MenuEscaped : MenuCaught;
                    Switch(St.JailMenu);
                }, false);
            starter.AddGameMenuOption(MenuEscape, MenuEscape + "_stay", "{=!}" + L.T("AIL_e7b1d81e", "Quedarte en la celda"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Wait; return true; },
                args =>
                {
                    St.JailMenu = null;
                    Log.Info("[trial] the player lets the chance to escape pass");
                    Switch(CellMenu);
                }, true);
        }

        /// <summary>Chance to get away, in percent: 30 plus a fifth of Roguery, at most 80.</summary>
        private static int EscapeOdds()
        {
            int skill = 0;
            try { skill = P?.GetSkillValue(DefaultSkills.Roguery) ?? 0; } catch { }
            return Math.Max(0, Math.Min(80, 30 + skill / 5));
        }

        private static void Switch(string menu)
        {
            try
            {
                if (Campaign.Current?.CurrentMenuContext != null) GameMenu.SwitchToMenu(menu);
                else GameMenu.ActivateGameMenu(menu);
            }
            catch (Exception ex) { Log.Error("Dungeon menu: switching to " + menu, ex); }
        }

        private static void ClearTerm()
        {
            _releaseDay = -1; _in = null;
            St.JailDays = 0;
        }

        private static void Free()
        {
            ClearTerm();
            if (PlayerCaptivity.IsCaptive) PlayerCaptivity.EndCaptivity();
            else GameMenu.ExitToLast();
        }

        private static void Execute()
        {
            var judge = Judge_;
            ClearTerm();
            Log.Info($"[trial] the player is executed on the order of {judge?.Name}");
            // as after a death in battle: the game goes on with the heir, or ends
            if (PlayerCaptivity.IsCaptive) PlayerCaptivity.EndCaptivity();
            KillCharacterAction.ApplyByExecution(P, judge, true, true);
        }

        private static void Escaped()
        {
            var st = _in;
            ClearTerm();
            if (PlayerCaptivity.IsCaptive) PlayerCaptivity.EndCaptivity();
            else GameMenu.ExitToLast();
            // gone from his hands: the lord judges in absentia (a decree, the crime rating; nobody is sent after them)
            if (st != null) Ask(st, St.JailCase, null, St.JailStanding, St.JailCrime, "escaped");
        }

        private static void Caught()
        {
            Switch(CellMenu);
            var st = _in;
            if (st != null) Ask(st, St.JailCase, null, St.JailStanding, St.JailCrime, "caught");
        }

        /// <summary>Opens a pending dungeon menu once the player is on the map and nothing else is on screen. Call often.</summary>
        public static void Pump()
        {
            try
            {
                string menu = St?.JailMenu;
                if (string.IsNullOrEmpty(menu) || Campaign.Current == null || Mission.Current != null) return;
                if (Campaign.Current.ConversationManager?.IsConversationInProgress == true) return;
                // a menu about the cell only while still in it; the outcome menus of a sentence are shown in any case
                if ((menu == MenuEscape || menu == MenuServed) && !HeldIn(_in)) { St.JailMenu = null; return; }
                if (Campaign.Current.CurrentMenuContext?.GameMenu?.StringId == menu) return;
                Switch(menu);
            }
            catch (Exception ex) { Log.Error("Dungeon menu", ex); }
        }

        /// <summary>Daily: the term served lets the player out; while it runs, now and then a chance to escape comes.</summary>
        public static void OnDailyTick()
        {
            try
            {
                if (_releaseDay < 0 || _in == null) return;
                if (!HeldIn(_in)) { ClearTerm(); return; }        // freed some other way
                if (!string.IsNullOrEmpty(St.JailMenu)) return;
                if (CampaignTime.Now.ToDays >= _releaseDay)
                {
                    St.JailMenu = MenuServed;
                    Pump();
                    return;
                }
                var cfg = S.Get;
                int chance = Math.Max(0, Math.Min(25, cfg.VerdictEscapeChancePerDay));
                if (chance > 0 && St.JailEscapeTries < Math.Max(0, Math.Min(5, cfg.VerdictEscapeMaxAttempts)) && MBRandom.RandomInt(100) < chance)
                {
                    Log.Info($"[trial] a chance to escape from {_in.Name} (attempt {St.JailEscapeTries + 1} of {cfg.VerdictEscapeMaxAttempts})");
                    St.JailMenu = MenuEscape;
                    Pump();
                }
            }
            catch (Exception ex) { Log.Error("Dungeon: daily", ex); }
        }
    }
}
