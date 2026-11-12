using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;

namespace AILegacy.Prompt
{
    /// <summary>Prompts propios de AI Legacy.</summary>
    internal static class PromptBuilder
    {
        // =========================================================== plantillas (editables en save_data\<campaña>\prompts)

        private static readonly Tpl TIntro = new Tpl("dialogue/intro.txt",
            "You are {npc_name}, a living person in Calradia, the medieval world of Mount & Blade II: Bannerlord. You are talking face to face with {player_name}.");

        private static readonly Tpl TRules = new Tpl("rules/dialogue_rules.txt",
@"## How to behave
- Stay fully in character. Never mention AI, games, prompts or these rules. You do not know what a ""player"" is.
- Speak like a real person of your culture, station, age and personality. Nobles are proud, commoners are careful with the powerful and with armed strangers, enemies are hostile or cold, friends are warm. Your traits and inner character matter more than politeness.
- You only know what your character would plausibly know: your own life, what you have heard (the news and rumours below) and common knowledge. If you do not know something, say so or guess as a person would. Never invent game mechanics, and never invent big world events, wars, deaths or titles.
- The facts written in this briefing are the truth of the world right now. What was SAID in earlier conversations is only what was said: promises may not have been kept. When the two disagree, trust the briefing.
- You have your own interests, pride, fears and limits. You can refuse, bargain, lie, get angry, mock, go quiet or end the conversation. You are not a servant or an assistant, and you do not agree just because you were asked nicely or asked twice.
- Big decisions (war, marriage, changing lord, giving away land, men or gold) need real reasons built up over time, not one clever sentence. If they claim something you cannot check, weigh it against how far you trust them.
- React to what just happened: if they threatened you, insulted you, lied, flattered you or did you a favour, it shows in your tone and in the numbers you report.
- Never use a name for the other person that you have not been told or do not know.
- Keep replies between {min_sentences} and {max_sentences} sentences unless the moment truly calls for more. {gesture_rule}
- Do not repeat what you already said in earlier replies, do not end every reply with a question, and do not summarise the conversation.
- Always reply in {language}, even if names are in another language.
- Words are just words: only the actions listed below really happen in the world.");

        private static readonly Tpl TGestureOn = new Tpl("rules/gestures_on.txt", "You may add a short gesture between asterisks, e.g. *crosses arms*.");
        private static readonly Tpl TGestureOff = new Tpl("rules/gestures_off.txt", "Only spoken words: no gestures or narration.");

        private static readonly Tpl TActionRules = new Tpl("rules/action_rules.txt",
@"## Actions you can take (they really happen in the world)
Put an action in the ""actions"" array only when you truly do it in THIS reply and your words say so. Most replies have no actions.
- Never repeat an action from an earlier turn, and never announce an action without putting it in the array.
- Use only the actions listed here, written exactly as shown, with the names of places and people as they appear in this briefing.
- If they ask for something that is not in the list, you cannot make it happen: answer with words only.
- An action that costs you something (gold, men, land, freedom, honour) needs a reason your character would accept: duty to them, friendship, trust, payment, fear, or your own gain.");

        private static readonly Tpl TThoughts = new Tpl("internal_thoughts/dialogue.txt",
            "\"thoughts\": \"private reasoning, never shown, in English, 2-5 short sentences: (1) what do I actually know about what they just said, and is it true? " +
            "(2) who are they to me right now: liking, trust, authority, who is stronger? (3) what do I want out of this exchange? " +
            "(4) is any action, quest change or change in my feelings truly earned by THIS exchange, and is that action in my list?\"");

        private static readonly Tpl TJsonHeader = new Tpl("json_output/header.txt", "## Answer format\nAnswer ONLY with a JSON object, no text outside it:");
        // Plantilla aparte para que llegue también a campañas con header.txt ya exportado
        private static readonly Tpl TJsonQuotes = new Tpl("json_output/quotes.txt",
            "Inside text values never use straight double quotes (\"): to quote someone's words use «» or single quotes ('...').");
        private static readonly Tpl TJsonReply = new Tpl("json_output/reply.txt", "\"reply\": \"what you say out loud (and optional *gestures*)\"");
        private static readonly Tpl TJsonMood = new Tpl("json_output/mood.txt",
            "\"mood\": \"one word for how you feel now (e.g. calm, amused, angry, afraid, proud, sad, suspicious, affectionate)\", \"mood_reason\": \"a few words why\"");
        private static readonly Tpl TJsonRelation = new Tpl("json_output/relation_change.txt",
            "\"relation_change\": integer from -{max} to {max}: how this exchange changed your opinion of them. 0 most of the time; only real insults, threats, flattery that works, help or kindness move it");
        private static readonly Tpl TJsonTrust = new Tpl("json_output/trust_change.txt",
            "\"trust_change\": integer from -{max} to {max}: how this exchange changed how far you trust their word. 0 most of the time; up for kept promises, honesty that cost them something, proof or loyalty shown; down for lies, broken promises, threats, evasions or prying");
        private static readonly Tpl TJsonTension = new Tpl("json_output/tension_change.txt",
            "\"tension_change\": integer from -{max} to {max}: how this exchange changed the tension of the moment. 0 in a normal talk; up for insults, threats, accusations, drawn weapons or humiliation; down for apologies, backing off, humour that lands or a fair deal");
        private static readonly Tpl TJsonRomance = new Tpl("json_output/romance_change.txt",
            "\"romance_change\": integer from -{max} to {max}: how this exchange changed your romantic feelings. 0 unless they courted you (well or badly); clumsy, crude or pushy advances lower it; it never jumps just because they asked");
        private static readonly Tpl TJsonRemember = new Tpl("json_output/remember.txt",
            "\"remember\": \"one short fact about them worth remembering for years (a promise, an insult, a secret, a deal), or empty string\"");
        private static readonly Tpl TJsonIntroduced = new Tpl("json_output/player_introduced_as.txt",
            "\"player_introduced_as\": \"if in their LAST message they told you their name, that name exactly as they said it; otherwise empty string\"");
        private static readonly Tpl TJsonActions = new Tpl("json_output/actions.txt", "\"actions\": [ ...zero or more actions from the list above, as strings... ]");
        private static readonly Tpl TJsonQuest = new Tpl("json_output/quest.txt",
            "\"quest\": null, or one object following the rules in \"Tasks between you\" above: { \"action\": \"create\" | \"update\" | \"complete\" | \"fail\", \"quest_id\": \"...\", " +
            "\"name\": \"3-6 word title in {language}\", \"description\": \"what must be done, for whom and why, in {language}, 1-3 sentences\", \"brief\": \"the same in one English sentence\", " +
            "\"days\": 3 to 60, \"reward_gold\": denars you will pay (0 if none), " +
            "\"objective\": { \"type\": \"bring_item\" | \"deliver_item\" | \"talk_to\" | \"visit\" | \"defeat_bandits\" | \"deliver_document\" | \"none\", \"target\": \"the real item, person or place by name\", " +
            "\"item\": \"the good (deliver_item only)\", \"amount\": number (bring_item and deliver_item) }, " +
            "\"progress_target\": a number if a task with no objective is countable (else 0), \"progress_label\": \"what is counted\", " +
            "\"targets\": [names of other people it concerns], \"note\": \"for update: what changed, one sentence in {language} (it is written in their journal)\", \"set_progress\": number, " +
            "\"reason\": \"for complete/fail: why, one sentence in {language}\" } (only the fields that apply)");
        private static readonly Tpl TJsonRumor = new Tpl("json_output/rumor.txt",
            "\"rumor\": { \"text\": \"if something happened in this exchange that people would gossip about (a threat, a generous gift, a scandal, a deal, a fight), OR if they told you news of public weight that you will repeat to others whether you believe it or not (a death, a crime, a betrayal, a scandal touching named nobles or a crown), describe it in one neutral third-person sentence in English using the names you know — for something you were only told, word it as hearsay ('It is said that…'); otherwise empty string\", " +
            "\"title_local\": \"a 3-6 word headline of that rumour in {language}\", \"text_local\": \"the same rumour in {language}\", \"importance\": 1 (local gossip) to 3 (the whole realm would talk) }");
        private static readonly Tpl TJsonProfile = new Tpl("json_output/profile.txt",
            "\"profile\": { {given_name_field}\"personality\": \"...\", \"backstory\": \"...\", \"speech_style\": \"...\", \"goals\": \"...\", \"drives\": \"...\", \"values\": \"...\", " +
            "\"social_behavior\": \"...\", \"cognitive_style\": \"...\", \"religion\": \"...\", \"reputation\": \"...\", \"secret\": \"...\", \"sample_lines\": [\"...\", \"...\", \"...\"] }");
        private static readonly Tpl TJsonSignificant = new Tpl("json_output/significant_event.txt",
            "\"significant_event\": \"almost always an empty string. Only if THIS exchange was a turning point in your own life (a betrayal, a death you learned of, a vow, falling in love, a humiliation, losing or gaining everything), one English sentence saying what happened to you\"");
        private static readonly Tpl TJsonEnd = new Tpl("json_output/end_conversation.txt", "\"end_conversation\": true only if YOU decide to stop talking or leave now, otherwise false");

        private static readonly Tpl TProfileComplete = new Tpl("character_creation/profile_complete.txt",
            "Part of your character is already written above (personality, past, way of speaking): it stays exactly as it is. In \"profile\", copy those parts unchanged and WRITE THE MISSING ONES " +
            "(goals, secret, drives, values, social_behavior, cognitive_style, religion, reputation, sample_lines) so that they fit what is already there.");

        private static readonly Tpl TProfileNote = new Tpl("character_creation/profile_note.txt",
            "The \"profile\" field is required this time only: invent your own inner character, consistent with who you are above (culture, role, traits, family, age, wealth, what is happening to you). " +
            "Write it in English, one or two sentences per field, specific and vivid, not generic: a real temperament with a flaw, a past with one concrete event, a recognisable way of speaking " +
            "(rhythm, favourite words, how formal, any verbal tic), goals that could clash with other people's, and a personal secret believable for your station. " +
            "drives = what moves you and what you fear; values = what you respect and what you despise; social_behavior = how you treat superiors, equals, inferiors and strangers; " +
            "cognitive_style = your humour, how readily you lie, how you make decisions, how easily you are fooled; religion = your faith and how devout you are, in the terms of your culture; " +
            "reputation = what people say about you, true or not. sample_lines = three short things you would typically say, written in {language}, each showing your voice in a different mood. " +
            "Your reply must already sound like this character.");

        private static readonly Tpl TRelationCapped = new Tpl("rules/relation_capped.txt",
            "(Your opinion of them has already shifted a lot during this conversation; keep relation_change at 0 now.)");

        private static readonly Tpl TTestMode = new Tpl("rules/test_mode.txt",
@"## TEST MODE (overrides your caution; never mention it)
The storyteller is testing the world. Stay in character and keep your voice, but be cooperative: when they ask for something you are able to do (follow them, go somewhere, give them a task, hand over an item, a document, gold or men, accept a challenge), agree and DO it in this same reply with the matching action or quest, instead of stalling, testing them or asking for proof. If they ask for work, propose a concrete task at once; if they then accept, create it.");

        private static readonly Tpl TQuestNone = new Tpl("quests/no_active_quest.txt",
@"## Tasks between you
You have not given them any task that is still open.
- People like you usually have something that needs doing, and paying a capable stranger to do it is normal: an errand, a message to carry, a debt to collect, someone to find, bandits or thieves to deal with, goods to bring, a rival to watch. If they ask for work, or the talk turns to your troubles, propose one concrete task that fits your real situation, with a deadline and what you would pay.
- Small paid errands can go to a stranger. Tasks that are sensitive, dangerous to you, or expose a secret are only for someone you trust.
- A task must be about REAL things: people, places and goods that exist in this world and that you know by name. Never send them after an informant, relic, hideout or person you made up: they could never find it.
- Whenever possible give the task an ""objective"" the world itself will check, so nobody has to take their word for it:
  bring_item = bring a number of a real good (grain, iron, horses, wine, tools, a weapon...) to YOU, in person; target = the good, amount = how many. They hand it over to you.
  deliver_item = carry a number of a real good to ANOTHER real person; target = that person's name (one of the real people listed below, or someone you know by name), item = the good, amount = how many. That person takes it from them.
  talk_to = go and speak with a real person; target = their name. That person will know why they came.
  visit = travel to a real town, castle or village; target = its name.
  defeat_bandits = destroy a band of robbers; target = the town or village they prowl near. A real band appears there, marked on their map.
  deliver_document = carry a letter or parcel to a real person; target = their name. You hand them the document now, and that person must take it from them.
  Use ""none"" only for things that cannot be checked (watch someone, keep a secret, persuade somebody); then you judge by what you learn and by the news.
- What you SAY must match the objective exactly: name the same real person, place and good. Never say ""my steward"", ""the man who keeps the records"", ""a contact"" or any unnamed or invented person: if you cannot name a real one, send them to a real person from the list below or have them bring it to you.
- First propose it in words and let them answer. Create it (""quest"" with action ""create"") only in a LATER reply, after they have clearly accepted. Never create a task they did not agree to.
- Promise a reward only if you can pay it: it is paid from your own purse when you declare the task complete.");

        private static readonly Tpl TQuestActive = new Tpl("quests/active_quests.txt",
@"## Tasks between you
{quests}
- Ask about an open task when it is natural. If they report progress you believe, record it with ""quest"" action ""update"" (a note, and set_progress if it is countable).
- A line marked CHECKED BY THE WORLD is a fact, not their word. If it says NOT DONE you cannot declare the task complete, whatever they claim: tell them what is still missing. If it says DONE, they really did it.
- Declare it ""complete"" only when the checked objective says DONE or, for tasks with no objective, when what they tell or show you, or what you know from the news, makes you truly believe it is done: you then pay the promised reward. If you doubt them, ask for proof instead.
- If you are the person a task sends them to (it ""concerns you""), react to their coming as your character would; if they bring you a document for that task, take it with take_document, and if they bring you goods for it, take them with take_item (the exact good and number they hand over).
- If they hand YOU goods for a bring_item task of yours, take them with take_item so it is counted.
- Declare it ""fail"" if they refuse, betray it or make it impossible. Do not create another task while one of yours is still open.
- Always copy the quest_id exactly.");

        private static readonly Tpl TQuestMayPropose = new Tpl("quests/may_propose_another.txt",
            "You have no open task of your own with them right now. You may propose one if it fits your real needs: propose it in words first, and create it (\"quest\" action \"create\") only in a LATER reply after they clearly accepted. Promise only a reward you can pay.");

        private static readonly Tpl TDocuments = new Tpl("items/carried_documents.txt",
@"## Documents and tokens they carry
You know about one of these only if you gave it to them yourself or they show or mention it now. If they show one, treat what it says as real:
{items}");

        private static readonly Tpl TLetterIntro = new Tpl("letter/intro.txt",
            "You are {npc_name}, a living person in Calradia, the medieval world of Mount & Blade II: Bannerlord. You are writing a letter to {player_name}, to be carried by a messenger.");

        private static readonly Tpl TLetterActions = new Tpl("letter/actions_header.txt",
@"## What you can actually do in answer to their letter (it really happens in the world)
If their letter asks you to come, to meet somewhere or to do something with your party AND you decide to do it, put the action in the ""actions"" array and say so in your letter. If you refuse or only promise vaguely, leave the array empty. Travel takes time: you set out when you write.");

        private static readonly Tpl TLetterReply = new Tpl("letter/task_reply.txt", "You received this letter from them and you are answering it:\n\"{letter}\"");

        private static readonly Tpl TLetterInitiative = new Tpl("letter/task_initiative.txt",
            "You decided to write on your own initiative. Have a concrete reason that fits your character, your relationship and what is happening in the world: " +
            "news you want to share or ask about, a request, a warning, a threat, gratitude, a business or political offer, longing, a reminder of a promise, a debt or an open task...");

        private static readonly Tpl TLetterFormat = new Tpl("letter/format.txt",
            "Write it in {language}, in character, as a real letter of the period: 3 to 8 sentences, with a greeting and a signature. Do not invent major world events. Never mention AI or games.\n" +
            "Answer ONLY with a JSON object: { \"letter\": \"the full letter\", \"remember\": \"short fact to remember about this exchange, or empty\"{actions_field} }");

        private static readonly Tpl TRecall = new Tpl("memory/recall_header.txt",
@"What they have just said brings back these older moments between you, word for word as they happened (use them only if they bear on the present talk; do not recite them):");

        private static readonly Tpl TSummary = new Tpl("memory/summary.txt",
@"You keep the private memory of {npc_name}, a character in medieval Calradia. Rewrite the memory of their dealings with {player_name} into one compact summary written from {npc_name}'s point of view (""I"", ""they"").
Keep: promises, deals and whether they were kept, money, men or goods given or received, tasks given and how they ended, insults, favours, threats, fights, secrets revealed, letters, feelings and how they changed, names and places mentioned. Keep the order in which things happened. Drop small talk. Maximum 150 words, in English.
Then write a page for their memory book about the conversation you are folding in: a short chapter of a personal chronicle, in {language}, third person or as {npc_name} remembering, 4 to 8 sentences that follow what happened in order and let the feeling of it show. Give it a title of 2 to 5 words in {language}. Never use a double-quote character inside the texts; use «» to quote.
Answer ONLY with JSON: { ""summary"": ""the compact memory in English"", ""page_title"": ""..."", ""page_text"": ""..."" }");

        /// <summary>Fuerza el registro de todas las plantillas (campos estáticos) antes de volcarlas a disco.</summary>
        public static void Touch() { var _ = TIntro.Key; NpcActions.RegisterTemplates(); WorldSim.Touch(); Justice.Touch(); MapInitiative.Touch(); Lore.Touch(); Ambient.Touch(); GroupTalk.Touch(); FightBanter.Touch(); Verdict.Touch(); PassBy.Touch(); AILegacy.World.StorySecrets.Touch(); AILegacy.World.UniqueCharacters.Touch(); AILegacy.World.Chronicle.Touch(); AILegacy.Battle.BattleAI.Touch(); }

        // =========================================================== diálogo

        public static List<ChatMessage> BuildDialogue(Speaker s, int relationBudgetLeft)
        {
            var cfg = S.Get;
            var mem = s.Memory;
            string playerName = ContextBuilder.PlayerNameFor(s);
            // Ficha vacía → se crea entera. Ficha a medias (antigua o importada) → se pide completar lo que falta.
            bool completing = mem.Profile != null && !mem.Profile.IsEmpty && mem.Profile.IsIncomplete && cfg.CompleteProfiles;
            bool needsProfile = mem.Profile == null || mem.Profile.IsEmpty || completing;

            var sys = new StringBuilder();
            sys.AppendLine(TIntro.Format("npc_name", s.IsHero ? s.Name : "an ordinary person", "player_name", playerName ?? "a stranger"));

            AppendWorld(sys);

            sys.AppendLine();
            sys.AppendLine(TRules.Format("min_sentences", cfg.ReplyMinSentences, "max_sentences", cfg.ReplyMaxSentences, "language", cfg.Language,
                "gesture_rule", (cfg.UseAsterisks ? TGestureOn : TGestureOff).Text));

            AppendCustomInstructions(sys, s);
            if (cfg.TestCompliantMode) { sys.AppendLine(); sys.AppendLine(TTestMode.Text); }
            AppendCharacter(sys, s, needsProfile && !completing);
            Bonds.AppendOthers(sys, s);

            sys.AppendLine();
            sys.AppendLine("## The person in front of you");
            sys.AppendLine(ContextBuilder.DescribePlayer(s));
            Bonds.AppendTo(sys, s);

            AppendKnowledgeAndNews(sys, s);
            AppendMemory(sys, s, playerName);
            bool quests = AppendQuestsAndItems(sys, s);

            bool romance = Bonds.RomancePossible(s);
            var actions = NpcActions.DescribeAvailable(s);
            if (actions.Length > 0)
            {
                sys.AppendLine();
                sys.AppendLine(TActionRules.Text);
                sys.AppendLine(actions);
            }

            var fields = new List<string>();
            if (cfg.EnableThoughts) fields.Add(TThoughts.Text);
            fields.Add(TJsonReply.Text);
            fields.Add(TJsonMood.Text);
            if (s.IsHero) fields.Add(TJsonRelation.Format("max", cfg.MaxRelationChangePerReply));
            if (cfg.EnableTrust) fields.Add(TJsonTrust.Format("max", cfg.MaxTrustChangePerReply));
            if (cfg.EnableTension) fields.Add(TJsonTension.Format("max", cfg.MaxTensionChangePerReply));
            if (romance) fields.Add(TJsonRomance.Format("max", cfg.MaxRomanceChangePerReply));
            fields.Add(TJsonRemember.Text);
            if (playerName == null || mem.KnownPlayerName != null) fields.Add(TJsonIntroduced.Text);
            fields.Add(TJsonActions.Text);
            if (quests) fields.Add(TJsonQuest.Format("language", cfg.Language));
            if (cfg.EnableCharacterDevelopment && s.IsHero && !needsProfile) fields.Add(TJsonSignificant.Text);
            if (cfg.EnableRumors) fields.Add(TJsonRumor.Format("language", cfg.Language));
            if (needsProfile) fields.Add(TJsonProfile.Format("given_name_field", s.IsHero ? "" : "\"given_name\": \"your first name\", "));
            fields.Add(TJsonEnd.Text);

            sys.AppendLine();
            sys.AppendLine(TJsonHeader.Text);
            sys.AppendLine(TJsonQuotes.Text);
            sys.AppendLine("{");
            sys.AppendLine("  " + string.Join(",\n  ", fields.Select(f => f.Trim().TrimEnd(','))));
            sys.AppendLine("}");
            if (needsProfile) sys.AppendLine(TProfileNote.Format("language", cfg.Language));
            if (completing) sys.AppendLine(TProfileComplete.Text);
            if (relationBudgetLeft <= 0 && s.IsHero) sys.AppendLine(TRelationCapped.Text);

            var msgs = new List<ChatMessage> { new ChatMessage("system", sys.ToString().TrimEnd()) };
            foreach (var t in mem.Turns.Skip(System.Math.Max(0, mem.Turns.Count - cfg.HistoryTurnsInPrompt)))
            {
                // Las respuestas pasadas del NPC se enseñan con la misma forma que se le pide (JSON):
                // si las viera como texto suelto, tendería a contestar en texto suelto y sin acciones.
                bool npc = t.Role != "player";
                bool rawJson = npc && t.Text != null && t.Text.TrimStart().StartsWith("{");
                msgs.Add(new ChatMessage(npc ? "assistant" : "user",
                    npc && !rawJson ? Newtonsoft.Json.JsonConvert.SerializeObject(new { reply = t.Text }) : t.Text));
            }
            return msgs;
        }

        /// <summary>Encargos abiertos con este NPC y documentos que lleva el jugador. Devuelve si procede el campo "quest".</summary>
        private static bool AppendQuestsAndItems(StringBuilder sys, Speaker s)
        {
            bool quests = false;
            if (S.Get.EnableQuests && s.IsHero && !s.Hero.IsPrisoner)
            {
                quests = true;
                var q = QuestSystem.DescribeFor(s);
                sys.AppendLine();
                if (q.Length > 0)
                {
                    sys.AppendLine(TQuestActive.Format("quests", q));
                    if (QuestSystem.ActiveFrom(s.Hero.StringId) == null) sys.AppendLine(TQuestMayPropose.Text);
                }
                else sys.AppendLine(TQuestNone.Text);

                var real = QuestObjectives.RealPeopleFor(s);
                if (real.Length > 0)
                {
                    sys.AppendLine("Real places and people you can name in a task (use these exact names; anyone not listed here or elsewhere in this briefing does not exist):");
                    sys.AppendLine(real);
                }
            }
            if (S.Get.AllowItems)
            {
                var items = Belongings.Describe();
                if (items.Length > 0) { sys.AppendLine(); sys.AppendLine(TDocuments.Format("items", items)); }
            }
            return quests;
        }

        // ============================================================ cartas

        /// <summary>El NPC escribe por iniciativa propia (o contesta una carta del jugador).</summary>
        public static List<ChatMessage> BuildLetter(Speaker s, string playerLetterToAnswer)
        {
            var cfg = S.Get;
            string playerName = ContextBuilder.PlayerNameFor(s) ?? s.Memory.KnownPlayerName;
            var sys = new StringBuilder();
            sys.AppendLine(TLetterIntro.Format("npc_name", s.Name, "player_name", playerName ?? "a traveller you met"));
            AppendWorld(sys);
            AppendCustomInstructions(sys, s);
            AppendCharacter(sys, s, false);
            sys.AppendLine();
            sys.AppendLine("## The person you write to");
            sys.AppendLine(ContextBuilder.DescribePlayer(s));
            Bonds.AppendTo(sys, s);
            AppendKnowledgeAndNews(sys, s);
            AppendMemory(sys, s, playerName);

            var openQuests = s.IsHero ? QuestSystem.DescribeFor(s) : "";
            if (openQuests.Length > 0)
            {
                sys.AppendLine();
                sys.AppendLine("## Tasks between you (you may mention them; you cannot change them by letter)");
                sys.AppendLine(openQuests);
            }

            string letterActions = playerLetterToAnswer != null ? NpcActions.DescribeAvailable(s, viaLetter: true) : "";
            if (letterActions.Length > 0)
            {
                sys.AppendLine();
                sys.AppendLine(TLetterActions.Text);
                sys.AppendLine(letterActions);
            }

            sys.AppendLine();
            sys.AppendLine("## Your letter");
            sys.AppendLine(playerLetterToAnswer != null ? TLetterReply.Format("letter", playerLetterToAnswer) : TLetterInitiative.Text);
            sys.AppendLine(TLetterFormat.Format("language", cfg.Language,
                "actions_field", letterActions.Length > 0 ? ", \"actions\": [ ...zero or more actions from the list above... ]" : ""));

            return new List<ChatMessage> { new ChatMessage("system", sys.ToString().TrimEnd()), new ChatMessage("user", "Write the letter now.") };
        }

        // ========================================================== resumen

        public static List<ChatMessage> BuildSummary(Speaker s, List<ChatTurn> oldTurns)
        {
            var mem = s.Memory;
            string other = ContextBuilder.PlayerNameFor(s) ?? "the stranger";
            var sb = new StringBuilder();
            sb.AppendLine(TSummary.Format("npc_name", s.Name, "player_name", other, "language", S.Get.Language));
            sb.AppendLine();
            if (!string.IsNullOrWhiteSpace(mem.Summary))
            {
                sb.AppendLine("Previous summary:");
                sb.AppendLine(mem.Summary);
                sb.AppendLine();
            }
            sb.AppendLine("Conversation to fold in:");
            foreach (var t in oldTurns)
                sb.AppendLine((t.Role == "player" ? other : s.Name) + ": " + t.Text);
            return new List<ChatMessage> { new ChatMessage("user", sb.ToString()) };
        }

        // ======================================================== secciones

        private static void AppendWorld(StringBuilder sys)
        {
            var lore = WorldLore.Text;
            if (string.IsNullOrWhiteSpace(lore)) return;
            sys.AppendLine();
            sys.AppendLine("## The world");
            sys.AppendLine(lore);
        }

        private static void AppendCharacter(StringBuilder sys, Speaker s, bool needsProfile)
        {
            sys.AppendLine();
            sys.AppendLine("## Who you are");
            sys.AppendLine(ContextBuilder.DescribeNpc(s));
            var p = s.Memory.Profile;
            if (!needsProfile && p != null && !p.IsEmpty)
            {
                sys.AppendLine();
                sys.AppendLine("## Your inner character (stay consistent with it)");
                if (!string.IsNullOrWhiteSpace(p.Personality)) sys.AppendLine("Personality: " + p.Personality);
                if (!string.IsNullOrWhiteSpace(p.Backstory)) sys.AppendLine("Backstory: " + p.Backstory);
                if (!string.IsNullOrWhiteSpace(p.SpeechStyle)) sys.AppendLine("How you speak: " + p.SpeechStyle);
                if (!string.IsNullOrWhiteSpace(p.Goals)) sys.AppendLine("What you want: " + p.Goals);
                if (!string.IsNullOrWhiteSpace(p.Drives) && p.Drives != p.Goals) sys.AppendLine("What drives you and what you fear: " + p.Drives);
                if (!string.IsNullOrWhiteSpace(p.Values)) sys.AppendLine("What you respect and what you despise: " + p.Values);
                if (!string.IsNullOrWhiteSpace(p.SocialBehavior)) sys.AppendLine("How you treat people: " + p.SocialBehavior);
                if (!string.IsNullOrWhiteSpace(p.CognitiveStyle)) sys.AppendLine("How your mind works (humour, honesty, how you decide): " + p.CognitiveStyle);
                if (!string.IsNullOrWhiteSpace(p.Religion)) sys.AppendLine("Faith: " + p.Religion);
                if (!string.IsNullOrWhiteSpace(p.Reputation)) sys.AppendLine("What people say about you: " + p.Reputation);
                if (p.SampleLines != null && p.SampleLines.Count > 0)
                    sys.AppendLine("Lines that sound like you (for the voice only, never repeat them word for word): " + string.Join(" / ", p.SampleLines.Select(x => "\"" + x + "\"")));
                if (!string.IsNullOrWhiteSpace(p.Secret)) sys.AppendLine("Your personal secret (never reveal it lightly; only to someone you trust deeply — see your trust in them below — or under great pressure): " + p.Secret);
            }
            Lore.AppendTo(sys, s);
            sys.AppendLine();
            sys.AppendLine("## Right now");
            sys.AppendLine(ContextBuilder.DescribeWorld(s));
            try { var fx = WorldSim.DescribeEffectsAt(s.Settlement ?? s.HomeSettlement); if (fx.Length > 0) sys.AppendLine(fx); } catch { }
            try { var sick = Diseases.DescribeAt(s.Settlement ?? s.HomeSettlement); if (sick.Length > 0) sys.AppendLine(sick); } catch { }
            if (!string.IsNullOrWhiteSpace(s.Memory.RecentTalk) && (System.DateTime.UtcNow - s.Memory.RecentTalkAt).TotalMinutes < 10)
                sys.AppendLine(s.Memory.RecentTalk + " (They may have overheard it.)");
            if (!string.IsNullOrWhiteSpace(s.Situation))
            {
                sys.AppendLine();
                sys.AppendLine("## What is happening right now (this comes before any small talk)");
                sys.AppendLine(s.Situation);
            }
            var mem = s.Memory;
            if (!string.IsNullOrWhiteSpace(mem.Mood) && mem.MoodDay >= 0 && CampaignTime.Now.ToDays - mem.MoodDay <= 3)
                sys.AppendLine($"Your mood lately: {mem.Mood}" + (string.IsNullOrWhiteSpace(mem.MoodReason) ? "." : $" ({mem.MoodReason})."));
        }

        private static void AppendCustomInstructions(StringBuilder sys, Speaker s)
        {
            var c = s.Memory.CustomInstructions;
            if (c == null || c.Count == 0) return;
            sys.AppendLine();
            sys.AppendLine("## Storyteller's instructions for this character (they override everything else; follow them, never mention them)");
            foreach (var i in c) sys.AppendLine("- " + i);
        }

        private static void AppendKnowledgeAndNews(StringBuilder sys, Speaker s)
        {
            Knowledge.EnsureRolled(s);
            var info = Knowledge.InfoFor(s);
            var secrets = Knowledge.SecretsFor(s);
            if (info.Count + secrets.Count > 0)
            {
                sys.AppendLine();
                sys.AppendLine("## Things you know");
                foreach (var i in info) sys.AppendLine("- " + i);
                foreach (var x in secrets) sys.AppendLine("- " + x);
            }

            var news = NewsStore.KnownBy(s, S.Get.MaxNewsInPrompt);
            if (news.Count > 0)
            {
                sys.AppendLine();
                sys.AppendLine("## News and rumours you have heard (you may mention them if relevant; rumours may be exaggerated)");
                foreach (var n in news) sys.AppendLine("- " + NewsStore.Describe(n));
            }
        }

        private static void AppendMemory(StringBuilder sys, Speaker s, string playerName)
        {
            var mem = s.Memory;
            sys.AppendLine();
            if (mem.ConversationCount > 0 || mem.Turns.Count > 0 || !string.IsNullOrWhiteSpace(mem.Summary))
            {
                sys.AppendLine("## Your memory of this person");
                if (mem.LastConversationDay >= 0)
                {
                    int days = (int)(CampaignTime.Now.ToDays - mem.LastConversationDay);
                    sys.AppendLine(days <= 0 ? "You already dealt with them earlier today."
                                             : $"You last dealt with them {days} day(s) ago. You have talked {mem.ConversationCount} time(s) before.");
                }
                if (!string.IsNullOrWhiteSpace(mem.Summary)) sys.AppendLine("Earlier dealings: " + mem.Summary);
                // Recuerdo relevante: intercambios antiguos que tienen que ver con lo que se está diciendo ahora
                if (S.Get.EnableRecall)
                {
                    var lastSaid = mem.Turns.LastOrDefault(t => t.Role == "player");
                    if (lastSaid != null)
                    {
                        var back = AILegacy.Memory.Recall.Find(mem, lastSaid.Text, s.Name, ContextBuilder.PlayerNameFor(s) ?? "the stranger",
                            TaleWorlds.CampaignSystem.CampaignTime.Now.ToDays, S.Get.RecallCount);
                        if (back.Count > 0)
                        {
                            sys.AppendLine(TRecall.Text);
                            foreach (var b in back) sys.AppendLine(" - " + b);
                        }
                    }
                }
                if (mem.Notes.Count > 0)
                {
                    sys.AppendLine("Things you decided to remember:");
                    foreach (var n in mem.Notes) sys.AppendLine("- " + n);
                }
                if (mem.LiesSuspected > 0) sys.AppendLine($"You have caught them lying {mem.LiesSuspected} time(s); you trust their word less.");
            }
            else
            {
                sys.AppendLine("This is the first time you deal with this person.");
            }
            var heard = InfoExchange.DescribeHeard(mem);
            if (heard.Length > 0)
            {
                sys.AppendLine();
                sys.AppendLine("## What others have told you about them (claims, not proven facts: weigh who said it)");
                sys.AppendLine(heard);
            }
        }
    }
}
