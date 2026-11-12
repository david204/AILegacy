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
            // Ánimo por causas: lo que les pasa les dura unos días
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.Mood.OnDailyTick);
            CampaignEvents.TournamentFinished.AddNonSerializedListener(this, World.Mood.OnTournamentFinished);
            CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, World.Mood.OnPrisonerReleased);
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, World.Mood.OnWarDeclared);
            CampaignEvents.OnGivenBirthEvent.AddNonSerializedListener(this, World.Mood.OnGivenBirth);
            CampaignEvents.OnChildConceivedEvent.AddNonSerializedListener(this, World.Mood.OnChildConceived);
            // Memoria política: soberanos y dueños de cada feudo
            CampaignEvents.RulingClanChanged.AddNonSerializedListener(this, World.History.OnRulingClanChanged);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, World.History.OnSettlementOwnerChanged);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, WorldSim.OnHeroKilled);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, WorldSim.OnPrisonerTaken);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, World.Mood.OnPrisonerTaken);
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
            string stamp = null;
            try
            {
                if (dataStore.IsSaving)
                {
                    legacy = "";
                    chunks = Pack(MemoryStore.Serialize());
                    SaveFiles.Save(Campaign.Current?.UniqueGameId, CampaignTime.Now.ToDays);
                    stamp = SaveFiles.Stamp ?? "";   // sello de los archivos en este momento (para saber al cargar si son de otra rama)
                }
                dataStore.SyncData(SaveKey, ref legacy);
                dataStore.SyncData(SaveKeyChunks, ref chunks);
                dataStore.SyncData(SaveKeyStamp, ref stamp);
                if (dataStore.IsLoading)
                {
                    _inSaveCopy = chunks != null && chunks.Count > 0 ? Unpack(chunks) : legacy;
                    _inSaveStamp = stamp;
                }
            }
            catch (Exception ex)
            {
                Log.Error("Error guardando/cargando la memoria de AI Legacy", ex);
            }
        }

        /// <summary>Copia que venía dentro de la partida; solo se usa si no existen los archivos de la campaña.</summary>
        private string _inSaveCopy;
        /// <summary>Sello de los archivos en el momento de guardar esa partida (null en partidas de versiones anteriores).</summary>
        private string _inSaveStamp;

        private void LoadCampaignData()
        {
            try
            {
                var id = Campaign.Current?.UniqueGameId;
                // Plantillas de instrucciones editables de esta campaña (save_data\<id>\prompts)
                Prompt.PromptBuilder.Touch();
                Prompt.Templates.UseCampaign(id);
                bool hasCopy = !string.IsNullOrEmpty(_inSaveCopy);
                bool adopt = false;
                if (hasCopy && S.Get.MemoryFollowsSave)
                {
                    // La memoria es la de ESTA partida guardada (sin recuerdos de otra rama).
                    // Lo que hayas editado a mano en los archivos se respeta.
                    var edited = SaveFiles.ManualEdits(id);
                    // Si los archivos son de otra rama (seguiste jugando tras este guardado), se archivan antes de pisarlos
                    SaveFiles.ArchiveIfDiverged(id, _inSaveStamp, CampaignTime.Now.ToDays);
                    MemoryStore.Deserialize(_inSaveCopy);
                    foreach (var m in edited) MemoryStore.Put(m);
                    SaveFiles.AdoptStamp(id, _inSaveStamp);
                    adopt = !string.IsNullOrEmpty(_inSaveStamp);
                    Log.Info($"Memoria restaurada de la partida guardada" + (edited.Count > 0 ? $" ({edited.Count} personajes editados a mano se conservan)" : ""));
                }
                else if (!SaveFiles.Load(id) && hasCopy)
                {
                    MemoryStore.Deserialize(_inSaveCopy);
                    SaveFiles.AdoptStamp(id, _inSaveStamp);
                    adopt = !string.IsNullOrEmpty(_inSaveStamp);
                    Log.Info($"La campaña {id} no tenía archivos: memoria recuperada de la copia de la partida");
                }
                _inSaveCopy = null;
                _inSaveStamp = null;
                SeedCharacters();
                GameJournal.Reconcile();
                // Los archivos quedan como los de esta partida y con su sello: recargarla no se toma por otra rama
                SaveFiles.Flush(keepStamp: adopt);
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
        private const string SaveKeyStamp = "AILegacy_FilesStamp";
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
            World.NewsTracker.Live = true;       // a partir de aquí, lo que pase es noticia
            World.History.Snapshot();
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
            // Héroes que llevas presos en tu partida: el juego no pasa por hero_main_options sino por su rama de presos
            // (PrisonerRecruitCampaignBehavior: "start" → "prisoner_recruit_start_player", cuya única salida es la amenaza y cerrar)
            starter.AddPlayerLine(
                "ailegacy_talk_start_p", PrisonerOptions, "ailegacy_npc_listen_p",
                L.T("AIL_d1c3cda0", "{=!}Quiero hablar contigo de algo. [Charla libre]"),
                () => !S.Get.UseDialogStyle && CanTalkHero(),
                () => MainThread.Post(() => ChatSession.Start(Speaker.FromHero(Hero.OneToOneConversationHero))),
                priority: 110);
            starter.AddDialogLine("ailegacy_npc_listen_p", "ailegacy_npc_listen_p", PrisonerOptions, L.T("AIL_a5da1c9c", "{=!}*te escucha*"), null, null, priority: 110);

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

            // --- Partidas sin señor en el mapa (bandidos, caravanas, aldeanos de camino), con la ventana de charla
            foreach (var token in BandTokens)
            {
                string back = token;
                starter.AddPlayerLine("ailegacy_band_start_" + token, token, "ailegacy_band_listen_" + token,
                    L.T("AIL_73c1647d", "{=!}Oye, ¿tienes un momento? [Charla libre]"),
                    () => !S.Get.UseDialogStyle && CanTalkBand(),
                    () => MainThread.Post(() => { var sp = BandSpeaker(); if (sp != null) ChatSession.Start(sp); }),
                    priority: 110);
                starter.AddDialogLine("ailegacy_band_listen_" + token, "ailegacy_band_listen_" + token, back, L.T("AIL_a5da1c9c", "{=!}*te escucha*"), null, null, priority: 110);
            }

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
                    _fromPrisonerBranch = false;
                    _bandReturn = null;
                    var s = ChatSession.StartDialog(Speaker.FromHero(Hero.OneToOneConversationHero));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);
            // Lo mismo desde la rama de presos de tu partida; «Volver» regresa a esa rama (ailegacy_d_ret_p)
            starter.AddPlayerLine("ailegacy_d_start_p", PrisonerOptions, "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                () => S.Get.UseDialogStyle && CanTalkHero(),
                () =>
                {
                    _fromPrisonerBranch = true;
                    _bandReturn = null;
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
                    _bandReturn = null;
                    var cm = Campaign.Current.ConversationManager;
                    var s = ChatSession.StartDialog(Speaker.FromResident(cm.OneToOneConversationCharacter, cm.OneToOneConversationAgent));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);

            // Partidas sin señor en el mapa: el jefe de unos bandidos (te asalten ellos o los abordes tú), de una caravana
            // o de unos aldeanos. Cada una tiene su rama en el juego; «Volver» regresa a ella.
            foreach (var token in BandTokens)
            {
                string back = token;
                starter.AddPlayerLine("ailegacy_d_start_" + token, token, "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                    () => S.Get.UseDialogStyle && CanTalkBand(),
                    () =>
                    {
                        _fromPrisonerBranch = false;
                        _bandReturn = back;
                        var s = ChatSession.StartDialog(BandSpeaker());
                        if (s != null) MainThread.Post(s.AskInput);
                    }, priority: 120);
                starter.AddDialogLine("ailegacy_d_ret_" + token, "ailegacy_d_ret", back, L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                    () => _bandReturn == back && Hero.OneToOneConversationHero == null && BandParty() != null, null, priority: 126);
            }

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
            // (a la rama de presos si se entró por ella: allí está la salida del juego, «No hagas ninguna estupidez…» → cerrar)
            starter.AddDialogLine("ailegacy_d_ret_p", "ailegacy_d_ret", PrisonerOptions, L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => _fromPrisonerBranch && IsMainPartyPrisoner(), null, priority: 125);
            starter.AddDialogLine("ailegacy_d_ret_h", "ailegacy_d_ret", "hero_main_options", L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => Hero.OneToOneConversationHero != null, null, priority: 120);
            starter.AddDialogLine("ailegacy_d_ret_r", "ailegacy_d_ret", "town_or_village_player", L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => Hero.OneToOneConversationHero == null, null, priority: 110);
        }

        /// <summary>Opciones del jugador al hablar con un preso de su partida (TaleWorlds.CampaignSystem, PrisonerRecruitCampaignBehavior).</summary>
        private const string PrisonerOptions = "prisoner_recruit_start_player";
        /// <summary>La charla de AI Legacy en curso empezó desde la rama de presos.</summary>
        private static bool _fromPrisonerBranch;

        /// <summary>El héroe con el que hablas va preso en tu partida (la misma condición que usa el juego para su rama).</summary>
        private static bool IsMainPartyPrisoner()
        {
            try
            {
                var c = CharacterObject.OneToOneConversationCharacter;
                return c != null && c.IsHero && Hero.OneToOneConversationHero != null
                       && TaleWorlds.CampaignSystem.Party.MobileParty.MainParty?.PrisonRoster?.Contains(c) == true;
            }
            catch { return false; }
        }

        private static bool CanTalkHero()
        {
            var h = Hero.OneToOneConversationHero;
            return h != null && h != Hero.MainHero && h.IsAlive && !h.IsChild;
        }

        /// <summary>
        /// Ramas del juego para partidas sin señor (TaleWorlds.CampaignSystem 1.4.8): BanditInteractionsCampaignBehavior
        /// («bandit_attacker» si los abordas tú, «bandit_defender» si te asaltan), CaravansCampaignBehavior y VillagerCampaignBehavior.
        /// </summary>
        private static readonly string[] BandTokens = { "bandit_attacker", "bandit_defender", "caravan_talk", "village_farmer_talk" };
        /// <summary>Rama a la que vuelve «Volver» cuando la charla en curso es con el jefe de una partida sin señor.</summary>
        private static string _bandReturn;

        /// <summary>La partida sin señor con la que hablas ahora en el mapa.</summary>
        private static TaleWorlds.CampaignSystem.Party.MobileParty BandParty()
        {
            try
            {
                if (TaleWorlds.MountAndBlade.Mission.Current != null) return null;
                // En el asalto de unos bandidos la partida de la charla puede venir solo por el encuentro
                var p = TaleWorlds.CampaignSystem.Party.MobileParty.ConversationParty ?? (PlayerEncounter.Current != null ? PlayerEncounter.EncounteredMobileParty : null);
                return p != null && p.IsActive && !p.IsMainParty && p.LeaderHero == null ? p : null;
            }
            catch { return null; }
        }

        private static bool CanTalkBand()
        {
            try
            {
                if (!S.Get.EnableResidents || Hero.OneToOneConversationHero != null) return false;
                var c = Campaign.Current?.ConversationManager?.OneToOneConversationCharacter;
                return c != null && !c.IsHero && BandParty() != null;
            }
            catch { return false; }
        }

        private static Speaker BandSpeaker()
        {
            var p = BandParty();
            var c = Campaign.Current?.ConversationManager?.OneToOneConversationCharacter;
            return p == null || c == null ? null : Speaker.FromBand(c, p);
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
