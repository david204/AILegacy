using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.World;
using Newtonsoft.Json.Linq;
using TaleWorlds.Library;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// The player flags a mistake in the last reply. The same prompt is sent again with a review task: the model says
    /// whether it is really a complaint about that reply, whether the prompt had what it needed, and proposes one lesson.
    /// Nothing is saved until the player accepts it. Lessons go into the shared part of every dialogue prompt.
    /// They belong to the campaign, or to every campaign if the setting says so (a text file, one per line).
    /// </summary>
    internal static class Lessons
    {
        private static readonly Tpl TBlock = new Tpl("lessons/block.txt",
            "## Lessons from the player (each line corrects a mistake made before: follow every one; where a lesson and another rule disagree, the lesson wins)");

        private static readonly Tpl TReview = new Tpl("lessons/review.txt",
@"[Stop playing the character. You are now the editor of this game's dialogue, reviewing your own last reply above.]
The player has flagged it with this complaint: «{complaint}»
Lessons already saved ({count} of at most {max}):
{lessons}

Work through it in this order, writing your steps in ""reasoning"":
1. Is this really a complaint about the reply above: what it said, how it said it, what it did or failed to do? A line of role-play, a threat to the character, a new request or a question for the character is NOT a complaint. If it is not, verdict = not_a_complaint and stop there.
2. Quote the part of the reply that went wrong and say what was wrong with it.
3. Look in the prompt above for the rule or the fact that should have prevented it. If it was there and the reply ignored it: verdict = missed. If nothing in the prompt gave you what you needed (a fact you could not know, a rule that does not exist): verdict = missing. If the reply broke no rule and the player simply wants it done another way: verdict = preference.
4. Only for missed and preference: write ONE lesson, a short imperative sentence of at most 200 characters that would have prevented it. Make it general enough to apply to any character and any scene: no names, no places, nothing only true of this moment. Test it against three other requests you can imagine: it must not make them worse. If one of the saved lessons already says nearly the same, improve that one instead and set replaces to its number.
Answer ONLY with JSON: { ""reasoning"": ""..."", ""verdict"": ""missed|missing|preference|not_a_complaint"", ""lesson"": ""the lesson in English, or empty"", ""lesson_local"": ""the same lesson in {language}, or empty"", ""replaces"": 0, ""for_player"": ""one or two sentences to the player, in {language}: what went wrong, or why nothing is saved"" }");

        public static void Touch() { var _ = TBlock.Key + TReview.Key; }

        private static string GlobalFile => Path.Combine(Paths.ModuleDir, "lessons.txt");
        private static List<string> _global;
        private static DateTime _globalStamp;

        /// <summary>The lessons in force: this campaign's, or the shared file when lessons are global.</summary>
        public static List<string> Current()
        {
            try
            {
                if (!S.Get.PlayerLessonsGlobal) return WorldSim.State?.Lessons ?? new List<string>();
                var f = GlobalFile;
                if (!File.Exists(f)) return _global = new List<string>();
                var stamp = File.GetLastWriteTimeUtc(f);
                if (_global == null || stamp != _globalStamp)
                {
                    _global = File.ReadAllLines(f, Encoding.UTF8).Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith("#")).ToList();
                    _globalStamp = stamp;
                }
                return _global;
            }
            catch (Exception ex) { Log.Error("Leer lecciones", ex); return new List<string>(); }
        }

        private static void Store(List<string> list)
        {
            if (!S.Get.PlayerLessonsGlobal) { if (WorldSim.State != null) WorldSim.State.Lessons = list; return; }
            try
            {
                var lines = new List<string> { "# AI Legacy: lessons for every campaign, one per line. You can edit or delete lines.", "# Lines that start with # are ignored." };
                lines.AddRange(list);
                File.WriteAllLines(GlobalFile, lines, new UTF8Encoding(false));
                _global = list.ToList(); _globalStamp = File.GetLastWriteTimeUtc(GlobalFile);
            }
            catch (Exception ex) { Log.Error("Guardar lecciones", ex); }
        }

        /// <summary>The block for the shared part of the dialogue prompt, or "".</summary>
        public static string Block()
        {
            if (!S.Get.EnablePlayerLessons) return "";
            var list = Current();
            if (list.Count == 0) return "";
            var sb = new StringBuilder();
            sb.AppendLine(TBlock.Text.Trim());
            foreach (var l in list) sb.AppendLine("- " + l);
            return sb.ToString().TrimEnd();
        }

        /// <summary>Adds a lesson, or replaces one (1-based). Drops the oldest past the cap. Returns its number.</summary>
        public static int Add(string lesson, int replaces)
        {
            var list = Current().ToList();
            lesson = lesson.Replace("\r", " ").Replace("\n", " ").Trim();
            if (lesson.Length > 240) lesson = lesson.Substring(0, 240);
            int at;
            if (replaces >= 1 && replaces <= list.Count) { list[replaces - 1] = lesson; at = replaces; }
            else
            {
                list.Add(lesson);
                int max = Math.Max(5, S.Get.PlayerLessonsMax);
                while (list.Count > max) list.RemoveAt(0);
                at = list.Count;
            }
            Store(list);
            Log.Info($"[lección] guardada ({at} de {list.Count}): {lesson}");
            return at;
        }

        public static bool Remove(int number)
        {
            var list = Current().ToList();
            if (number < 1 || number > list.Count) return false;
            Log.Info($"[lección] borrada: {list[number - 1]}");
            list.RemoveAt(number - 1);
            Store(list);
            return true;
        }

        /// <summary>The lessons as the player sees them, numbered.</summary>
        public static string Listing()
        {
            var list = Current();
            if (list.Count == 0) return L.T("AIL_e7b1c7c4", "No hay lecciones guardadas.");
            var sb = new StringBuilder(L.F("AIL_e7b1c7c3", $"Lecciones guardadas ({list.Count} de {Math.Max(5, S.Get.PlayerLessonsMax)}):"));
            for (int i = 0; i < list.Count; i++) sb.Append("\n").Append(i + 1).Append(". ").Append(list[i]);
            return sb.ToString();
        }

        /// <summary>
        /// Sends the review. <paramref name="asked"/> is the exact prompt of the last reply and <paramref name="raw"/> what
        /// came back. <paramref name="say"/> shows text to the player (on the main thread).
        /// </summary>
        public static void Review(List<ChatMessage> asked, string raw, string complaint, string npcName, Action<string, bool> say)
        {
            var cfg = S.Get;
            var list = Current();
            var msgs = asked.Select(m => new ChatMessage(m.Role, m.Content)).ToList();
            msgs.Add(new ChatMessage("assistant", raw ?? ""));
            string numbered = list.Count == 0 ? "(none)" : string.Join("\n", list.Select((l, i) => $"{i + 1}. {l}"));
            msgs.Add(new ChatMessage("user", TReview.Format("complaint", complaint.Replace("«", "\"").Replace("»", "\""), "count", list.Count,
                "max", Math.Max(5, cfg.PlayerLessonsMax), "lessons", numbered, "language", cfg.Language)));
            int session = WorldSim.Session;
            Log.Info($"[lección] revisando la última respuesta de {npcName}: {complaint}");
            LlmClient.CompleteAsync(msgs, wantJson: true, task: "lesson", maxTokensOverride: 900, preferredProvider: cfg.ProviderHeroes).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    try
                    {
                        if (session != WorldSim.Session) return;
                        if (res == null || !res.Ok) { say(L.T("AIL_e7b1c7c1", "La revisión no ha llegado (la IA no ha respondido)."), true); return; }
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a < 0 || b <= a) { say(L.T("AIL_e7b1c7c1", "La revisión no ha llegado (la IA no ha respondido)."), true); return; }
                        var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                        string verdict = ((string)j["verdict"] ?? "").Trim().ToLowerInvariant();
                        string lesson = ((string)j["lesson"] ?? "").Trim(), local = ((string)j["lesson_local"] ?? "").Trim();
                        string forPlayer = ((string)j["for_player"] ?? "").Trim();
                        int replaces = 0;
                        try { replaces = (int?)j["replaces"] ?? 0; } catch { }
                        Log.Info($"[lección] veredicto {verdict}: {((string)j["reasoning"] ?? "").Replace("\n", " ")}");
                        if (verdict == "missing")
                        {
                            Log.Warn($"[lección] FALTABA EN EL PROMPT ({npcName}): {complaint} | {forPlayer}");
                            say(L.F("AIL_e7b1c7bf", $"{forPlayer}\n\nEl fallo es del mod: le faltaba información. Queda anotado en el registro para corregirlo."), false);
                            return;
                        }
                        if (verdict != "missed" && verdict != "preference" || lesson.Length < 8)
                        {
                            say(L.F("AIL_e7b1c7c0", $"{forPlayer}\n\nNo parece una queja sobre su última respuesta, así que no se guarda nada."), false);
                            return;
                        }
                        if (local.Length < 8) local = lesson;
                        InformationManager.ShowInquiry(new InquiryData(L.T("AIL_e7b1c7ba", "Lección propuesta"),
                            L.F("AIL_e7b1c7bb", $"{forPlayer}\n\nLección propuesta:\n{local}"), true, true,
                            L.T("AIL_e7b1c7bc", "Guardar"), L.T("AIL_e7b1c7bd", "Descartar"),
                            () =>
                            {
                                int at = Add(lesson, replaces);
                                say(L.F("AIL_e7b1c7be", $"Lección guardada ({at} de {Current().Count})."), false);
                            },
                            () => Log.Info("[lección] descartada por el jugador")), true);
                    }
                    catch (Exception ex) { Log.Error("Revisión de una respuesta", ex); say(L.T("AIL_e7b1c7c1", "La revisión no ha llegado (la IA no ha respondido)."), true); }
                });
            });
        }
    }
}
