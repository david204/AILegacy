using System;
using System.Collections.Generic;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Letters;
using AILegacy.Memory;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.Localization;

namespace AILegacy.Behaviors
{
    /// <summary>Engancha AI Legacy a la campaña: diálogos, noticias, cartas y guardado.</summary>
    public class AILegacyBehavior : CampaignBehaviorBase
    {
        private const string SaveKey = "AILegacy_Memory_v1";

        public override void RegisterEvents()
        {
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, LetterSystem.OnDailyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, LetterSystem.OnHourlyTick);

            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Bonds.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, QuestSystem.OnDailyTick);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, QuestObjectives.OnSettlementEntered);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, Diseases.OnSettlementEntered);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, InfoExchange.OnHourlyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, MapInitiative.OnHourlyTick);
            // El mundo que se mueve solo: eventos, efectos económicos, cansancio de guerra y diplomacia de reyes
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, WorldSim.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Dialogue.Verdict.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.StorySecrets.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.UniqueCharacters.OnDailyTick);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, WorldSim.OnMapEventEnded);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, WorldSim.OnHeroKilled);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, WorldSim.OnPrisonerTaken);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, WorldSim.OnSettlementOwnerChanged);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, QuestObjectives.OnPartyDestroyed);
            NewsTracker.Register(this);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // Los datos viven en archivos por campaña (Memory/SaveFiles.cs): se escriben al guardar
            // y se leen al cargar (en OnSessionLaunched). Dentro de la partida solo va una copia de respaldo.
            // OJO: el guardado del juego apunta el tamaño de cada cadena en un "short". Una sola cadena de más de
            // ~32 KB corrompe la partida ("Las dimensiones de la matriz superaron el intervalo admitido" al cargar).
            // Por eso la copia va comprimida y troceada en cadenas pequeñas (clave v2); la clave vieja se deja vacía.
            string legacy = null;
            List<string> chunks = null;
            try
            {
                if (dataStore.IsSaving)
                {
                    legacy = "";
                    chunks = Pack(MemoryStore.Serialize());
                    SaveFiles.Save(Campaign.Current?.UniqueGameId, CampaignTime.Now.ToDays);
                }
                dataStore.SyncData(SaveKey, ref legacy);
                dataStore.SyncData(SaveKeyChunks, ref chunks);
                if (dataStore.IsLoading)
                    _inSaveCopy = chunks != null && chunks.Count > 0 ? Unpack(chunks) : legacy;
            }
            catch (Exception ex)
            {
                Log.Error("Error guardando/cargando la memoria de AI Legacy", ex);
            }
        }

        /// <summary>Copia que venía dentro de la partida; solo se usa si no existen los archivos de la campaña.</summary>
        private string _inSaveCopy;

        private void LoadCampaignData()
        {
            try
            {
                var id = Campaign.Current?.UniqueGameId;
                // Plantillas de instrucciones editables de esta campaña (save_data\<id>\prompts)
                Prompt.PromptBuilder.Touch();
                Prompt.Templates.UseCampaign(id);
                bool hasCopy = !string.IsNullOrEmpty(_inSaveCopy);
                if (hasCopy && S.Get.MemoryFollowsSave)
                {
                    // La memoria es la de ESTA partida guardada (sin recuerdos de otra rama).
                    // Lo que hayas editado a mano en los archivos se respeta.
                    var edited = SaveFiles.ManualEdits(id);
                    MemoryStore.Deserialize(_inSaveCopy);
                    foreach (var m in edited) MemoryStore.Put(m);
                    Log.Info($"Memoria restaurada de la partida guardada" + (edited.Count > 0 ? $" ({edited.Count} personajes editados a mano se conservan)" : ""));
                }
                else if (!SaveFiles.Load(id) && hasCopy)
                {
                    MemoryStore.Deserialize(_inSaveCopy);
                    Log.Info($"La campaña {id} no tenía archivos: memoria recuperada de la copia de la partida");
                }
                _inSaveCopy = null;
                SeedCharacters();
                GameJournal.Reconcile();
                SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Error cargando los datos de la campaña", ex); }
        }

        /// <summary>
        /// Al empezar o cargar la campaña ya existen los archivos de los personajes importantes
        /// (reyes, tu familia y tu clan, y los de la historia), con su conocimiento del mundo repartido, sin haber hablado con ellos.
        /// </summary>
        private static void SeedCharacters()
        {
            try
            {
                var p = Hero.MainHero;
                var wanted = new HashSet<Hero>();
                foreach (var k in Campaign.Current.Kingdoms) if (k != null && !k.IsEliminated && k.Leader != null) wanted.Add(k.Leader);
                if (Clan.PlayerClan != null) foreach (var h in Clan.PlayerClan.Heroes) wanted.Add(h);
                foreach (var h in new[] { p.Spouse, p.Father, p.Mother }) if (h != null) wanted.Add(h);
                foreach (var h in p.Siblings) wanted.Add(h);
                foreach (var h in p.Children) wanted.Add(h);
                foreach (var h in Hero.AllAliveHeroes)
                {
                    var id = h.StringId ?? "";
                    if (id.StartsWith("storymode_") || id.StartsWith("tutorial_npc") || id.Contains("storyline")) wanted.Add(h);
                }

                int created = 0;
                foreach (var h in wanted)
                {
                    if (h == null || h == p || !h.IsAlive) continue;   // los niños también (tus hermanos pequeños de la historia)
                    bool isNew = MemoryStore.Find(h.StringId) == null;
                    var s = Speaker.FromHero(h);
                    if (isNew) { s.Memory.Seeded = true; created++; }
                    s.Memory.Snapshot = Prompt.ContextBuilder.Snapshot(h);
                    try { Knowledge.EnsureRolled(s); } catch { }
                    try { World.Lore.EnsureArchetype(s); } catch { }      // su molde de carácter, sin esperar a hablar con él
                    Bonds.EnsureTrust(s);
                    Bonds.EnsureRomance(s);
                }
                if (created > 0) Log.Info($"Personajes importantes preparados sin hablar con ellos: {created} nuevos de {wanted.Count}");
            }
            catch (Exception ex) { Log.Error("Preparando personajes importantes", ex); }
        }

        private const string SaveKeyChunks = "AILegacy_Memory_v2";
        private const int ChunkSize = 12000;   // caracteres ASCII (base64) = bytes; muy por debajo del límite

        private static List<string> Pack(string json)
        {
            var list = new List<string>();
            if (string.IsNullOrEmpty(json)) return list;
            byte[] raw = System.Text.Encoding.UTF8.GetBytes(json);
            string b64;
            using (var ms = new System.IO.MemoryStream())
            {
                using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Compress, true))
                    gz.Write(raw, 0, raw.Length);
                b64 = Convert.ToBase64String(ms.ToArray());
            }
            for (int i = 0; i < b64.Length; i += ChunkSize)
                list.Add(b64.Substring(i, Math.Min(ChunkSize, b64.Length - i)));
            Log.Info($"Guardado: memoria de {raw.Length / 1024} KB comprimida en {list.Count} trozos");
            return list;
        }

        private static string Unpack(List<string> chunks)
        {
            byte[] packed = Convert.FromBase64String(string.Concat(chunks));
            using (var ms = new System.IO.MemoryStream(packed))
            using (var gz = new System.IO.Compression.GZipStream(ms, System.IO.Compression.CompressionMode.Decompress))
            using (var outMs = new System.IO.MemoryStream())
            {
                gz.CopyTo(outMs);
                return System.Text.Encoding.UTF8.GetString(outMs.ToArray());
            }
        }

        private void OnSessionLaunched(CampaignGameStarter starter)
        {
            LoadCampaignData();
            AddDialogStyleLines(starter);
            LetterSystem.AddMenu(starter);
            Diseases.AddMenus(starter);
            try { World.ArenaTraining.AddMenus(starter); } catch (Exception ex) { Log.Error("Menú de entrenamiento en la arena", ex); }

            // --- Héroes: lores, notables, compañeros, vagabundos (estilo ventana de chat)
            starter.AddPlayerLine(
                "ailegacy_talk_start", "hero_main_options", "ailegacy_npc_listen",
                L.T("AIL_d1c3cda0", "{=!}Quiero hablar contigo de algo. [Charla libre]"),
                () => !S.Get.UseDialogStyle && CanTalkHero(),
                () => MainThread.Post(() => ChatSession.Start(Speaker.FromHero(Hero.OneToOneConversationHero))),
                priority: 110);
            starter.AddDialogLine("ailegacy_npc_listen", "ailegacy_npc_listen", "hero_main_options", L.T("AIL_a5da1c9c", "{=!}*te escucha*"), null, null, priority: 110);

            // --- Gente corriente de ciudades y aldeas
            starter.AddPlayerLine(
                "ailegacy_res_start", "town_or_village_player", "ailegacy_res_listen",
                L.T("AIL_73c1647d", "{=!}Oye, ¿tienes un momento? [Charla libre]"),
                () => !S.Get.UseDialogStyle && CanTalkResident(),
                () => MainThread.Post(() =>
                {
                    var cm = Campaign.Current.ConversationManager;
                    var c = cm.OneToOneConversationCharacter;
                    if (c != null) ChatSession.Start(Speaker.FromResident(c, cm.OneToOneConversationAgent));
                }),
                priority: 110);
            starter.AddDialogLine("ailegacy_res_listen", "ailegacy_res_listen", "town_or_village_player", L.T("AIL_a5da1c9c", "{=!}*te escucha*"), null, null, priority: 110);

            Log.Info($"AI Legacy listo: diálogos añadidos, {MemoryStore.Count} personajes en memoria, {System.Linq.Enumerable.Count(NewsStore.All)} noticias");
        }

        /// <summary>
        /// La charla vive en el cuadro de diálogo del juego.
        /// Hablar → escribes → "(espera)" → "Ver su respuesta" → respuesta → Decir algo más / Volver / Marcharse.
        /// </summary>
        private void AddDialogStyleLines(CampaignGameStarter starter)
        {
            ChatSession Cur() => ChatSession.Active != null && ChatSession.IsBusy ? ChatSession.Active : null;

            // Iniciativa: el NPC se ha acercado él (un guardia tras una pelea…) y su primera frase ya está lista
            starter.AddDialogLine("ailegacy_init_start", "start", "ailegacy_d_opts", "{=!}{AIL_DLG}",
                () =>
                {
                    if (!Initiative.Matches()) return false;
                    var c = Initiative.Take();
                    if (c == null) return false;
                    MBTextManager.SetTextVariable("AIL_DLG", new TextObject("{=!}" + c.ConsumeDialogText()));
                    return true;
                },
                () => Initiative.Done(), priority: 10000);

            // Entrada desde héroes y desde gente del pueblo
            starter.AddPlayerLine("ailegacy_d_start_h", "hero_main_options", "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                () => S.Get.UseDialogStyle && CanTalkHero(),
                () =>
                {
                    var s = ChatSession.StartDialog(Speaker.FromHero(Hero.OneToOneConversationHero));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);
            // En el mapa: bajar del caballo y hablar cara a cara, en una escena del terreno
            starter.AddPlayerLine("ailegacy_maptalk", "hero_main_options", "close_window", L.T("AIL_29a99738", "{=!}Desmontemos y hablemos cara a cara. [AI Legacy: escena]"),
                () => Scenes.MapTalk.CanOffer(Hero.OneToOneConversationHero),
                () => Scenes.MapTalk.Queue(Hero.OneToOneConversationHero), priority: 119);
            starter.AddPlayerLine("ailegacy_d_start_r", "town_or_village_player", "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                () => S.Get.UseDialogStyle && CanTalkResident(),
                () =>
                {
                    var cm = Campaign.Current.ConversationManager;
                    var s = ChatSession.StartDialog(Speaker.FromResident(cm.OneToOneConversationCharacter, cm.OneToOneConversationAgent));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);

            // El NPC "espera" mientras escribes. Las opciones de aquí se calculan ANTES de que escribas,
            // así que solo puede haber "Ver su respuesta" / "Volver".
            starter.AddDialogLine("ailegacy_d_wait", "ailegacy_d_wait", "ailegacy_d_optw", "{=!}{AIL_DLG}",
                () => { MBTextManager.SetTextVariable("AIL_DLG", new TextObject(L.T("AIL_58ce073f", "{=!}(Envía tu mensaje y espera su respuesta)"))); return true; },
                null, priority: 120);
            starter.AddPlayerLine("ailegacy_d_view", "ailegacy_d_optw", "ailegacy_d_show", L.T("AIL_98823032", "{=!}Ver su respuesta"),
                () => Cur() != null, null, priority: 130);
            starter.AddPlayerLine("ailegacy_d_backw", "ailegacy_d_optw", "ailegacy_d_ret", L.T("AIL_0042ce81", "{=!}Volver"),
                () => true, null, priority: 110);

            // Tras ver la respuesta
            starter.AddPlayerLine("ailegacy_d_say", "ailegacy_d_opts", "ailegacy_d_wait", L.T("AIL_92eb34bb", "{=!}Decir algo más"),
                () => { var c = Cur(); return c != null && !c.Ended; },
                () => { var c = Cur(); if (c != null) MainThread.Post(c.AskInput); }, priority: 125);
            // Repasar toda la conversación en una ventana con barra de desplazamiento
            starter.AddPlayerLine("ailegacy_d_log", "ailegacy_d_opts", "ailegacy_d_show", L.T("AIL_cdf7046b", "{=!}Repasar lo hablado"),
                () => { var c = Cur(); return c != null && c.Who != null && c.Who.Memory.Turns.Count > 2; },
                () => { var c = Cur(); if (c != null) { var who = c.Who; MainThread.Post(() => UI.Books.OpenTranscript(who)); } }, priority: 109);
            starter.AddPlayerLine("ailegacy_d_leave", "ailegacy_d_opts", "close_window", L.T("AIL_41e43789", "{=!}Marcharse"),
                () => { var c = Cur(); return c != null && c.Ended; },
                () =>
                {
                    try { if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement && !Combat.KeepEncounter) PlayerEncounter.LeaveEncounter = true; }
                    catch (Exception ex) { Log.Error("Marcharse", ex); }
                }, priority: 124);
            starter.AddPlayerLine("ailegacy_d_back", "ailegacy_d_opts", "ailegacy_d_ret", L.T("AIL_0042ce81", "{=!}Volver"),
                () => { var c = Cur(); return c == null || !c.Ended; }, null, priority: 110);

            // La respuesta en el cuadro de diálogo. Si aún piensa, se vuelve a "Ver su respuesta".
            starter.AddDialogLine("ailegacy_d_think", "ailegacy_d_show", "ailegacy_d_optw", "{=!}{AIL_DLG}",
                () =>
                {
                    var c = Cur();
                    if (c == null || !c.IsWaiting) return false;
                    MBTextManager.SetTextVariable("AIL_DLG", new TextObject("{=!}" + c.ConsumeDialogText()));
                    return true;
                }, null, priority: 130);
            starter.AddDialogLine("ailegacy_d_show", "ailegacy_d_show", "ailegacy_d_opts", "{=!}{AIL_DLG}",
                () =>
                {
                    var c = Cur();
                    MBTextManager.SetTextVariable("AIL_DLG", new TextObject("{=!}" + (c?.ConsumeDialogText() ?? "...")));
                    return true;
                }, null, priority: 120);

            // Volver a las opciones normales del juego
            starter.AddDialogLine("ailegacy_d_ret_h", "ailegacy_d_ret", "hero_main_options", L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => Hero.OneToOneConversationHero != null, null, priority: 120);
            starter.AddDialogLine("ailegacy_d_ret_r", "ailegacy_d_ret", "town_or_village_player", L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => Hero.OneToOneConversationHero == null, null, priority: 110);
        }

        private static bool CanTalkHero()
        {
            var h = Hero.OneToOneConversationHero;
            return h != null && h != Hero.MainHero && h.IsAlive && !h.IsChild;
        }

        private static bool CanTalkResident()
        {
            if (!S.Get.EnableResidents) return false;
            var cm = Campaign.Current?.ConversationManager;
            var c = cm?.OneToOneConversationCharacter;
            return c != null && !c.IsHero && cm.OneToOneConversationAgent != null && c.Age >= 16;
        }
    }
}
