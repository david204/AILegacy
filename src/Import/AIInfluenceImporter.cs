using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.Import
{
    /// <summary>
    /// Trae tus propios datos de partida de AIInfluence (save_data/[ID de campaña]/*.json) a la memoria de AI Legacy:
    /// resúmenes, últimas líneas, personalidad, recuerdos, nombre con el que te conocen e instrucciones tuyas.
    /// </summary>
    internal static class AIInfluenceImporter
    {
        private static IEnumerable<string> SaveDataRoots()
        {
            var game = Path.GetFullPath(Path.Combine(Paths.ModuleDir, "..", ".."));
            yield return Path.Combine(game, "Modules", "AIInfluence", "save_data");
        }

        public static void ImportCurrentCampaign()
        {
            MainThread.Post(() =>
            {
                try
                {
                    if (Campaign.Current == null) { Show("AI Legacy", L.T("Carga primero una partida.")); return; }
                    var id = Campaign.Current.UniqueGameId;
                    var dir = SaveDataRoots().Select(r => Path.Combine(r, id)).FirstOrDefault(Directory.Exists);
                    if (dir != null) { Import(dir, true); return; }

                    // No hay carpeta para esta campaña: ofrecer las que existen
                    var options = new List<InquiryElement>();
                    foreach (var root in SaveDataRoots().Where(Directory.Exists))
                        foreach (var d in Directory.GetDirectories(root))
                        {
                            int n = Directory.GetFiles(d, "*(*).json").Length;
                            if (n == 0) continue;
                            var last = Directory.GetFiles(d).Select(File.GetLastWriteTime).DefaultIfEmpty().Max();
                            options.Add(new InquiryElement(d, $"{Path.GetFileName(d)} — {n} NPC — {last:dd/MM/yyyy}", null));
                        }
                    if (options.Count == 0) { Show("AI Legacy", "No he encontrado datos de AIInfluence."); return; }

                    MBInformationManager.ShowMultiSelectionInquiry(new MultiSelectionInquiryData(
                        L.T("Importar de AIInfluence"),
                        L.F($"No hay datos de AIInfluence para ESTA campaña ({id}).\n\nPuedes importar otra campaña: solo se usarán los NPC que existan en esta partida ") +
                        L.T("(los lores del juego tienen el mismo id en todas). Ojo: recordarán cosas de aquella partida."),
                        options, true, 1, 1, "Importar", "Cancelar",
                        sel => { if (sel?.Count > 0) MainThread.Post(() => Import((string)sel[0].Identifier, false)); }, null), true);
                }
                catch (Exception ex) { Log.Error("Importador", ex); Show("AI Legacy", "Error importando: " + ex.Message); }
            });
        }

        /// <summary>Quita de esta campaña todo lo importado de AIInfluence (los archivos se apartan a una subcarpeta, no se borran).</summary>
        public static void UndoImport()
        {
            MainThread.Post(() =>
            {
                try
                {
                    if (Campaign.Current == null) { Show("AI Legacy", L.T("Carga primero una partida.")); return; }
                    var imported = MemoryStore.AllHeroes.Where(m => m.ImportedFrom != null).ToList();
                    if (imported.Count == 0) { Show("AI Legacy", L.T("En esta campaña no hay memorias importadas.")); return; }
                    InformationManager.ShowInquiry(new InquiryData(L.T("Deshacer importación"),
                        L.F($"Se van a quitar de esta campaña las memorias de {imported.Count} personajes que vinieron de AIInfluence ") +
                        L.T("(incluido lo que hayas hablado con ellos después). Sus archivos se apartan a una subcarpeta de la campaña, no se borran.\n\n¿Seguir?"),
                        true, true, "Quitar", "Cancelar", () =>
                        {
                            var dest = SaveFiles.BackupHeroFiles(Campaign.Current.UniqueGameId, imported, "importados");
                            foreach (var m in imported) MemoryStore.Remove(m.HeroId);
                            SaveFiles.Flush();
                            Log.Info($"Importación deshecha: {imported.Count} personajes apartados a {dest}");
                            Show("AI Legacy", L.F($"Quitadas las memorias importadas de {imported.Count} personajes.\nCopia en:\n{dest}\n\nGuarda la partida para que quede así."));
                        }, null), true);
                }
                catch (Exception ex) { Log.Error("Deshacer importación", ex); Show("AI Legacy", "Error: " + ex.Message); }
            });
        }

        private static void Import(string dir, bool sameCampaign)
        {
            int imported = 0, skipped = 0, missing = 0, failed = 0;
            var names = new List<string>();
            foreach (var file in Directory.GetFiles(dir, "*(*).json"))
            {
                try
                {
                    var j = JObject.Parse(File.ReadAllText(file, Encoding.UTF8));
                    var heroId = (string)j["StringId"];
                    var hero = string.IsNullOrEmpty(heroId) ? null : Hero.Find(heroId);
                    if (hero == null || hero == Hero.MainHero) { missing++; continue; }

                    var existing = MemoryStore.Find(heroId);
                    if (existing != null && (existing.Turns.Count > 0 || !string.IsNullOrEmpty(existing.Summary))) { skipped++; continue; }

                    var m = Convert(j, hero);
                    if (m == null) { missing++; continue; }
                    m.ImportedFrom = Path.GetFileName(dir) + (sameCampaign ? "" : L.T(" (otra campaña)"));
                    MemoryStore.Put(m);
                    imported++;
                    if (names.Count < 12) names.Add(hero.Name.ToString());
                }
                catch (Exception ex) { failed++; Log.Error("Importando " + Path.GetFileName(file), ex); }
            }
            Log.Info($"Importación de {dir}: {imported} importados, {skipped} ya tenían memoria, {missing} sin héroe/datos, {failed} con error");
            Show(L.T("Importación de AIInfluence"),
                L.F($"Importados: {imported}\nYa tenían memoria en AI Legacy (no tocados): {skipped}\nSin datos útiles o no existen en esta partida: {missing}\nCon error: {failed}") +
                (names.Count > 0 ? "\n\n" + string.Join(", ", names) + (imported > names.Count ? "…" : "") : "") +
                L.T("\n\nGuarda la partida para conservarlo."));
        }

        private static HeroMemory Convert(JObject j, Hero hero)
        {
            var m = new HeroMemory { HeroId = hero.StringId, HeroName = hero.Name.ToString() };

            // Historial: líneas "MEMORY (day N): ..." = resúmenes; el resto, turnos
            var summaries = new List<string>();
            foreach (var t in (j["ConversationHistory"] as JArray ?? new JArray()).Select(x => x?.ToString() ?? ""))
            {
                var line = t.Trim();
                if (line.Length == 0 || line.StartsWith("Your last conversation", StringComparison.OrdinalIgnoreCase)) continue;
                if (line.StartsWith("MEMORY", StringComparison.OrdinalIgnoreCase))
                {
                    int c = line.IndexOf("):", StringComparison.Ordinal);
                    summaries.Add((c > 0 ? line.Substring(c + 2) : line).Trim());
                    continue;
                }
                if (line.StartsWith("[")) continue; // notas de sistema/eventos
                // "David (`main_hero`): texto" / "I (Rhagaea, `lord_1_14`): texto" / "Rhagaea: texto"
                int sep2 = line.IndexOf(": ", StringComparison.Ordinal);
                if (sep2 <= 0 || sep2 > 80) continue; // no parece una línea de diálogo
                string head = line.Substring(0, sep2);
                string text = line.Substring(sep2 + 2);
                bool player = head.Contains("main_hero");
                m.Turns.Add(new ChatTurn { Role = player ? "player" : "npc", Text = text.Trim(), Day = -1 });
            }
            if (summaries.Count > 0)
            {
                var sum = string.Join(" ", summaries);
                m.Summary = sum.Length > 1800 ? sum.Substring(sum.Length - 1800) : sum;
            }
            if (m.Turns.Count > 16) m.Turns = m.Turns.Skip(m.Turns.Count - 16).ToList();

            // Personalidad
            string P(string k) => ((string)j[k] ?? "").Trim();
            var prof = new CharacterProfile
            {
                Personality = P("AIGeneratedPersonality"),
                Backstory = P("AIGeneratedBackstory"),
                SpeechStyle = P("AIGeneratedSpeechQuirks"),
                Goals = P("AIGeneratedDrives"),
                Secret = P("AIGeneratedSecret"),
                Drives = P("AIGeneratedDrives"),
                Values = P("AIGeneratedValues"),
                SocialBehavior = P("AIGeneratedSocialBehavior"),
                CognitiveStyle = P("AIGeneratedCognitiveStyle"),
                Religion = P("AIGeneratedReligion"),
                Reputation = P("AIGeneratedReputation"),
            };
            if (!prof.IsEmpty) m.Profile = prof;

            // Recuerdos (Memory Book)
            foreach (var mem in (j["Memories"] as JArray ?? new JArray()).OfType<JObject>())
            {
                var title = ((string)mem["title"] ?? "").Trim();
                var summary = ((string)mem["summary"] ?? "").Trim();
                var note = title.Length > 0 ? $"{title}: {summary}" : summary;
                if (note.Length > 220) note = note.Substring(0, 220) + "…";
                if (note.Length > 3) m.Notes.Add(note);
            }
            int maxNotes = S.Get.MaxNotesPerHero;
            if (m.Notes.Count > maxNotes) m.Notes = m.Notes.Skip(m.Notes.Count - maxNotes).ToList();

            // Cómo te conoce
            var social = j["CounterpartySocial"]?["main_hero"];
            if (social != null)
            {
                var claimed = ((string)social["claimed_name"] ?? "").Trim();
                if (claimed.Length > 0) m.KnownPlayerName = claimed;
                m.ConversationCount = (int?)social["interaction_count"] ?? 0;
                m.LastConversationDay = (double?)social["last_dialogue_campaign_days"] ?? -1;
                if (((double?)social["lie_penalty_sum"] ?? 0) > 0) m.LiesSuspected = 1;
            }
            if (m.LastConversationDay < 0) m.LastConversationDay = (double?)j["LastInteractionTimeDays"] ?? -1;
            if (m.ConversationCount == 0 && (m.Turns.Count > 0 || summaries.Count > 0)) m.ConversationCount = 1;

            // Instrucciones tuyas (no los contextos automáticos)
            var extra = P("AdditionalContext");
            if (extra.Length > 0 && !extra.StartsWith("[CONTEXT:", StringComparison.OrdinalIgnoreCase))
                m.CustomInstructions.Add(extra.Length > 1500 ? extra.Substring(0, 1500) : extra);

            // Ánimo
            var mood = j["EmotionalState"];
            if (mood != null)
            {
                m.Mood = ((string)mood["Mood"] ?? "").Trim();
                m.MoodReason = ((string)mood["Reason"] ?? "").Trim();
                m.MoodDay = CampaignTime.Now.ToDays;
            }

            m.KnowledgeRolled = false;
            bool useful = m.Turns.Count > 0 || summaries.Count > 0 || m.Profile != null || m.Notes.Count > 0 || m.CustomInstructions.Count > 0;
            return useful ? m : null;
        }

        private static string Join(params string[] parts) => string.Join(" ", parts.Where(p => !string.IsNullOrWhiteSpace(p)));

        private static void Show(string title, string text) =>
            InformationManager.ShowInquiry(new InquiryData(title, text, true, false, L.T("Vale"), "", null, null), true);
    }
}
