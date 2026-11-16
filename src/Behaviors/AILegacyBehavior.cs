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
    /// <summary>Hooks AI Legacy into the campaign: dialogue, news, letters and saving.</summary>
    public class AILegacyBehavior : CampaignBehaviorBase
    {
        private const string SaveKey = "AILegacy_Memory_v1";

        public override void RegisterEvents()
        {
            _creationDone = false; _seedAfterCreation = false;
            CampaignEvents.OnSessionLaunchedEvent.AddNonSerializedListener(this, OnSessionLaunched);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, LetterSystem.OnDailyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, LetterSystem.OnHourlyTick);

            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Bonds.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, QuestSystem.OnDailyTick);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, QuestObjectives.OnSettlementEntered);
            CampaignEvents.SettlementEntered.AddNonSerializedListener(this, Diseases.OnSettlementEntered);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, Diseases.OnHourlyTick);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, Diseases.OnMapEventEnded);
            // Quarantine really closes the place: map AI parties stay away and the player is stopped at the gate
            CampaignEvents.AiHourlyTickEvent.AddNonSerializedListener(this, QuarantineRules.OnAiHourlyTick);
            CampaignEvents.GameMenuOpened.AddNonSerializedListener(this, QuarantineRules.OnGameMenuOpened);
            CampaignEvents.OnSettlementLeftEvent.AddNonSerializedListener(this, QuarantineRules.OnSettlementLeft);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, InfoExchange.OnHourlyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, Diplomacy.OnHourlyTick);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(this, MapInitiative.OnHourlyTick);
            // World simulation
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, WorldSim.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Intrigue.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Dialogue.Verdict.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.StorySecrets.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.UniqueCharacters.OnDailyTick);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(this, WorldSim.OnMapEventEnded);
            // Mood causes (each one lasts a few days)
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.Mood.OnDailyTick);
            CampaignEvents.TournamentFinished.AddNonSerializedListener(this, World.Mood.OnTournamentFinished);
            CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, World.Mood.OnPrisonerReleased);
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, World.Mood.OnWarDeclared);
            CampaignEvents.WarDeclared.AddNonSerializedListener(this, Diplomacy.OnWarDeclared);
            CampaignEvents.OnGivenBirthEvent.AddNonSerializedListener(this, World.Mood.OnGivenBirth);
            CampaignEvents.OnGivenBirthEvent.AddNonSerializedListener(this, Bastards.OnGivenBirth);
            CampaignEvents.BeforeHeroesMarried.AddNonSerializedListener(this, Bastards.OnMarried);
            CampaignEvents.OnClanLeaderChangedEvent.AddNonSerializedListener(this, Bastards.OnClanLeaderChanged);
            CampaignEvents.BeforeHeroKilledEvent.AddNonSerializedListener(this, Bastards.OnHeroKilled);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Bastards.OnDailyTick);
            CampaignEvents.DailyTickHeroEvent.AddNonSerializedListener(this, Bastards.OnDailyTickHero);
            CampaignEvents.BeforeHeroesMarried.AddNonSerializedListener(this, Concubines.OnMarried);
            CampaignEvents.BeforeHeroKilledEvent.AddNonSerializedListener(this, Concubines.OnHeroKilled);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Concubines.OnDailyTick);
            CampaignEvents.OnChildConceivedEvent.AddNonSerializedListener(this, World.Mood.OnChildConceived);
            // Political history: rulers and fief owners
            CampaignEvents.RulingClanChanged.AddNonSerializedListener(this, World.History.OnRulingClanChanged);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, World.History.OnSettlementOwnerChanged);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, WorldSim.OnHeroKilled);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, WorldSim.OnPrisonerTaken);
            CampaignEvents.HeroPrisonerReleased.AddNonSerializedListener(this, WorldSim.OnPrisonerReleased);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(this, World.Mood.OnPrisonerTaken);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(this, WorldSim.OnSettlementOwnerChanged);
            CampaignEvents.MobilePartyDestroyed.AddNonSerializedListener(this, QuestObjectives.OnPartyDestroyed);
            // Remembered townsfolk get their face back right before the game spawns the people of a place
            CampaignEvents.LocationCharactersAreReadyToSpawnEvent.AddNonSerializedListener(this, World.ResidentFaces.OnLocationCharactersReady);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.LandlessPatrols.OnDailyTick);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, World.AiArenaTraining.OnDailyTick);
            NewsTracker.Register(this);
            // A campaign that starts with every kingdom at peace: only ever on creation, after the game has set its starting wars
            CampaignEvents.OnNewGameCreatedEvent.AddNonSerializedListener(this, Diplomacy.OnNewGameCreated);
            CampaignEvents.OnGameLoadedEvent.AddNonSerializedListener(this, Diplomacy.OnGameLoaded);
            CampaignEvents.OnCharacterCreationIsOverEvent.AddNonSerializedListener(this, Diplomacy.OnCharacterCreationIsOver);
            CampaignEvents.OnCharacterCreationIsOverEvent.AddNonSerializedListener(this, OnCreationOver);
            CampaignEvents.DailyTickEvent.AddNonSerializedListener(this, Diplomacy.PeaceAtStart);
            // Dates the game does not keep: when a kingdom was founded and when an alliance began
            CampaignEvents.KingdomCreatedEvent.AddNonSerializedListener(this, Diplomacy.OnKingdomCreated);
            CampaignEvents.OnAllianceStartedEvent.AddNonSerializedListener(this, Diplomacy.OnAllianceStarted);
            CampaignEvents.OnAllianceEndedEvent.AddNonSerializedListener(this, Diplomacy.OnAllianceEnded);
            // The lists of who serves whom are built once a day: a clan changing crown rebuilds them at once
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(this, World.Facts.OnClanChangedKingdom);
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(this, World.Facts.OnKingdomChanged);
            // A kingdom that falls leaves nothing behind it: pacts, proposals, turns to speak
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(this, Diplomacy.OnKingdomDestroyed);
            CampaignEvents.RulingClanChanged.AddNonSerializedListener(this, World.Facts.OnRulingClanChanged);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(this, World.Facts.OnHeroKilled);
            // Whom the player puts to death, read before the game hands their clan and crown to someone else
            CampaignEvents.BeforeHeroKilledEvent.AddNonSerializedListener(this, World.Captivity.OnBeforeHeroKilled);
        }

        public override void SyncData(IDataStore dataStore)
        {
            // The data lives in per-campaign files (Memory/SaveFiles.cs), written on save and read on load
            // (in OnSessionLaunched). The save itself only carries a backup copy.
            // The game's save format stores each string's length in a short. One string over ~32 KB corrupts the
            // save ("Array dimensions exceeded supported range" on load). So the copy is gzipped and split into
            // small strings (v2 key), and the old key is left empty.
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
                    stamp = SaveFiles.Stamp ?? "";   // current files stamp, so on load we can tell if the files come from another branch
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
                Log.Error("Error saving/loading AI Legacy memory", ex);
            }
        }

        /// <summary>Copy that came inside the save. Used when the save should own the memory or the campaign files are missing.</summary>
        private string _inSaveCopy;
        /// <summary>Files stamp at the moment that save was made (null for saves from older versions).</summary>
        private string _inSaveStamp;

        private void LoadCampaignData()
        {
            try
            {
                var id = Campaign.Current?.UniqueGameId;
                // Editable prompt templates for this campaign (save_data\<id>\prompts)
                Prompt.PromptBuilder.Touch();
                Prompt.Templates.UseCampaign(id);
                bool hasCopy = !string.IsNullOrEmpty(_inSaveCopy);
                bool adopt = false;
                if (hasCopy && S.Get.MemoryFollowsSave)
                {
                    // Memory comes from THIS save, with nothing from another branch. Hand edits to the files are kept.
                    var edited = SaveFiles.ManualEdits(id);
                    // If the files belong to another branch (you kept playing after this save), archive them before overwriting
                    SaveFiles.ArchiveIfDiverged(id, _inSaveStamp, CampaignTime.Now.ToDays);
                    if (MemoryStore.Deserialize(_inSaveCopy))
                    {
                        foreach (var m in edited) MemoryStore.Put(m);
                        SaveFiles.AdoptStamp(id, _inSaveStamp);
                        adopt = !string.IsNullOrEmpty(_inSaveStamp);
                        Log.Info($"Memory restored from the save" + (edited.Count > 0 ? $" ({edited.Count} hand-edited characters kept)" : ""));
                    }
                    // Unreadable copy in the save: the files or a backup, and whatever is on disk is set aside before
                    // anything gets written over it. The files keep their own stamp
                    else SaveFiles.Recover(id);
                }
                else if (!SaveFiles.Load(id))
                {
                    if (hasCopy && MemoryStore.Deserialize(_inSaveCopy))
                    {
                        SaveFiles.AdoptStamp(id, _inSaveStamp);
                        adopt = !string.IsNullOrEmpty(_inSaveStamp);
                        Log.Info($"Campaign {id} had no files: memory recovered from the save copy");
                    }
                    // Files that are there but did not load, or a copy that did not read: same fallback
                    else if (hasCopy || SaveFiles.Exists(id)) SaveFiles.Recover(id);
                }
                _inSaveCopy = null;
                _inSaveStamp = null;
                SeedCharacters();
                GameJournal.Reconcile();
                // The files now match this save and carry its stamp, so reloading it isn't taken as another branch
                SaveFiles.Flush(keepStamp: adopt, wait: true);
            }
            catch (Exception ex) { Log.Error("Error loading campaign data", ex); }
        }

        private static bool _seedAfterCreation, _creationDone;

        /// <summary>A new campaign: the important characters are prepared once the player's character exists.</summary>
        private static void OnCreationOver(int _)
        {
            _creationDone = true;
            if (!_seedAfterCreation) return;
            _seedAfterCreation = false;
            SeedCharacters(afterCreation: true);
            try { SaveFiles.Flush(); } catch (Exception ex) { Log.Error("Files", ex); }
        }

        /// <summary>
        /// On new game or load, creates the files for important characters (rulers, your family and clan, story characters)
        /// with their share of world knowledge, before you ever talk to them.
        /// </summary>
        private static void SeedCharacters(bool afterCreation = false)
        {
            // Before the player's character exists there is nobody to be family or clan of: wait for it
            bool newGame = false;
            try { newGame = Campaign.Current?.CampaignGameLoadingType == Campaign.GameLoadingType.NewCampaign; } catch { }
            // (the event of its end comes while that screen is still the active one)
            if (!afterCreation && !_creationDone && (SubModule.InCharacterCreation || newGame))
            { _seedAfterCreation = true; Log.Info("Important characters: will be prepared once character creation ends"); return; }
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
                    if (h == null || h == p || !h.IsAlive) continue;   // children too (your little siblings in the story)
                    bool isNew = MemoryStore.Find(h.StringId) == null;
                    var s = Speaker.FromHero(h);
                    if (isNew) { s.Memory.Seeded = true; created++; }
                    s.Memory.Snapshot = Prompt.ContextBuilder.Snapshot(h);
                    try { Knowledge.EnsureRolled(s); } catch { }
                    try { World.Lore.EnsureArchetype(s); } catch { }      // character archetype, without waiting for a first talk
                    Bonds.EnsureTrust(s);
                    Bonds.EnsureRomance(s);
                }
                if (created > 0) Log.Info($"Important characters prepared without talking to them: {created} new of {wanted.Count}");
            }
            catch (Exception ex) { Log.Error("Preparing important characters", ex); }
        }

        private const string SaveKeyChunks = "AILegacy_Memory_v2";
        private const string SaveKeyStamp = "AILegacy_FilesStamp";
        private const int ChunkSize = 12000;   // base64 is ASCII, so chars = bytes; well under the limit

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
            Log.Info($"Saved: {raw.Length / 1024} KB of memory compressed into {list.Count} chunks");
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
            SubModule.PatchForCampaign();
            World.QuestJournalGuards.Apply();
            LoadCampaignData();
            World.NewsTracker.Live = true;       // from here on, what happens counts as news
            World.History.Snapshot();
            Diplomacy.CleanPacts();               // a save may carry an alliance with a kingdom that is gone or has no ruler
            QuarantineRules.NotePlayerInside();   // loading a save inside a quarantined place doesn't kick you out
            AddDialogStyleLines(starter);
            LetterSystem.AddMenu(starter);
            Diseases.AddMenus(starter);
            try { World.ArenaTraining.AddMenus(starter); } catch (Exception ex) { Log.Error("Arena training menu", ex); }
            try { World.Activities.AddMenus(starter); } catch (Exception ex) { Log.Error("Activities menus", ex); }

            // --- Heroes (lords, notables, companions, wanderers), chat window style
            starter.AddPlayerLine(
                "ailegacy_talk_start", "hero_main_options", "ailegacy_npc_listen",
                L.T("AIL_d1c3cda0", "{=!}Quiero hablar contigo de algo. [Charla libre]"),
                () => !S.Get.UseDialogStyle && CanTalkHero(),
                () => MainThread.Post(() => ChatSession.Start(Speaker.FromHero(Hero.OneToOneConversationHero))),
                priority: 110);
            starter.AddDialogLine("ailegacy_npc_listen", "ailegacy_npc_listen", "hero_main_options", L.T("AIL_a5da1c9c", "{=!}*te escucha*"), null, null, priority: 110);
            // Heroes held prisoner in your party: the game skips hero_main_options and uses its prisoner branch
            // (PrisonerRecruitCampaignBehavior: "start" -> "prisoner_recruit_start_player", whose only exit is the threat line and close)
            starter.AddPlayerLine(
                "ailegacy_talk_start_p", PrisonerOptions, "ailegacy_npc_listen_p",
                L.T("AIL_d1c3cda0", "{=!}Quiero hablar contigo de algo. [Charla libre]"),
                () => !S.Get.UseDialogStyle && CanTalkHero(),
                () => MainThread.Post(() => ChatSession.Start(Speaker.FromHero(Hero.OneToOneConversationHero))),
                priority: 110);
            starter.AddDialogLine("ailegacy_npc_listen_p", "ailegacy_npc_listen_p", PrisonerOptions, L.T("AIL_a5da1c9c", "{=!}*te escucha*"), null, null, priority: 110);

            // --- Common people in towns and villages
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

            // --- Lordless parties on the map (bandits, caravans, villagers on the road), chat window style
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

            Log.Info($"AI Legacy ready: dialogues added, {MemoryStore.Count} characters in memory, {System.Linq.Enumerable.Count(NewsStore.All)} news items");
        }

        /// <summary>
        /// The chat inside the game's own dialogue box.
        /// Talk -> you type -> "(wait)" -> "See their reply" -> reply -> Say something else / Back / Leave.
        /// </summary>
        private void AddDialogStyleLines(CampaignGameStarter starter)
        {
            ChatSession Cur() => ChatSession.Active != null && ChatSession.IsBusy ? ChatSession.Active : null;

            // Initiative: the NPC walked up to you (a guard after a fight, say) and their first line is already written
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

            // Talk on foot by default: meeting a lord on the map you are not at war with opens the scene straight away
            starter.AddDialogLine("ailegacy_maptalk_auto", "start", "close_window", L.T("AIL_e7b1c870", "{=!}*desmonta y te hace un gesto para que hagas lo mismo*"),
                () =>
                {
                    try
                    {
                        var h = Hero.OneToOneConversationHero;
                        if (!S.Get.MapTalkAutomatic || h == null || !h.IsLord || h.IsPrisoner || h.Clan == Clan.PlayerClan || h.PartyBelongedTo?.LeaderHero != h) return false;
                        if (h.PartyBelongedTo.Army != null && h.PartyBelongedTo.Army == TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.Army) return false;
                        if (Campaign.Current.CurrentConversationContext != ConversationContext.PartyEncounter || Initiative.Matches()) return false;
                        if (h.MapFaction != null && Hero.MainHero.MapFaction != null && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction)) return false;
                        return Scenes.MapTalk.CanOffer(h);
                    }
                    catch { return false; }
                },
                () => Scenes.MapTalk.Queue(Hero.OneToOneConversationHero), priority: 9500);

            // Bandits who block the road speak first, in their own words. The game's own opening still runs first (its
            // condition sets the texts its later lines use), so "Back" leads to its usual choices.
            starter.AddDialogLine("ailegacy_band_ambush", "start", "ailegacy_d_optw", "{=!}{AIL_DLG}",
                () =>
                {
                    try
                    {
                        if (!S.Get.UseDialogStyle || !S.Get.BanditsSpeakFirst || !CanTalkBand() || !BanditAmbush()) return false;
                        var who = BandSpeaker();
                        if (who == null) return false;
                        who.SituationKind = "initiative";
                        who.Situation = TAmbush.Format("theirs", BandParty().MemberRoster.TotalManCount, "yours", TaleWorlds.CampaignSystem.Party.MobileParty.MainParty.MemberRoster.TotalManCount,
                            "purse", Hero.MainHero.Gold, "sea", BandParty().IsCurrentlyAtSea ? TAmbushSea.Text : "", "player", Hero.MainHero.Name.ToString());
                        _fromPrisonerBranch = false; _fromSurrenderDemand = false; _bandReturn = "bandit_defender";
                        var s = ChatSession.StartDialog(who);
                        if (s == null) return false;
                        s.AskOpening("You and your men have just blocked their way on the road. Speak first.");
                        MBTextManager.SetTextVariable("AIL_DLG", new TextObject("{=!}" + L.F("AIL_e7b1c7d8", $"*{who.Name} se adelanta con los suyos y os corta el paso…*")));
                        return true;
                    }
                    catch (Exception ex) { Log.Error("Bandits who speak first", ex); return false; }
                }, null, priority: 9000);

            // Entry points from heroes and from townsfolk
            starter.AddPlayerLine("ailegacy_d_start_h", "hero_main_options", "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                () => S.Get.UseDialogStyle && CanTalkHero(),
                () =>
                {
                    _fromPrisonerBranch = false;
                    _fromSurrenderDemand = false;
                    _bandReturn = null;
                    var s = ChatSession.StartDialog(Speaker.FromHero(Hero.OneToOneConversationHero));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);
            // A lord who caught you and demands your surrender: the game goes straight to this branch and never
            // shows hero_main_options, so Talk needs its own entry here. "Back" returns to the demand.
            starter.AddPlayerLine("ailegacy_d_start_s", SurrenderDemandOptions, "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                () => S.Get.UseDialogStyle && CanTalkHero(),
                () =>
                {
                    _fromPrisonerBranch = false;
                    _fromSurrenderDemand = true;
                    _bandReturn = null;
                    var s = ChatSession.StartDialog(Speaker.FromHero(Hero.OneToOneConversationHero));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);
            // Same from your party's prisoner branch. "Back" returns to that branch (ailegacy_d_ret_p)
            starter.AddPlayerLine("ailegacy_d_start_p", PrisonerOptions, "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                () => S.Get.UseDialogStyle && CanTalkHero(),
                () =>
                {
                    _fromPrisonerBranch = true;
                    _fromSurrenderDemand = false;
                    _bandReturn = null;
                    var s = ChatSession.StartDialog(Speaker.FromHero(Hero.OneToOneConversationHero));
                    if (s != null) MainThread.Post(s.AskInput);
                }, priority: 120);
            // The same, with a picture of each moment (only with an image service set up)
            starter.AddPlayerLine("ailegacy_d_start_hi", "hero_main_options", "ailegacy_d_wait", L.T("AIL_e7b1c7d0", "{=!}Hablar (con imagen de escena) [AI Legacy]"),
                () => S.Get.UseDialogStyle && World.SceneImages.Enabled && CanTalkHero(),
                () =>
                {
                    _fromPrisonerBranch = false;
                    _fromSurrenderDemand = false;
                    _bandReturn = null;
                    var s = ChatSession.StartDialog(Speaker.FromHero(Hero.OneToOneConversationHero));
                    if (s != null) { s.WithPictures = true; MainThread.Post(s.AskInput); }
                }, priority: 119);
            starter.AddPlayerLine("ailegacy_d_start_ri", "town_or_village_player", "ailegacy_d_wait", L.T("AIL_e7b1c7d0", "{=!}Hablar (con imagen de escena) [AI Legacy]"),
                () => S.Get.UseDialogStyle && World.SceneImages.Enabled && CanTalkResident(),
                () =>
                {
                    _bandReturn = null;
                    var cm = Campaign.Current.ConversationManager;
                    var s = ChatSession.StartDialog(Speaker.FromResident(cm.OneToOneConversationCharacter, cm.OneToOneConversationAgent));
                    if (s != null) { s.WithPictures = true; MainThread.Post(s.AskInput); }
                }, priority: 119);
            // An adult child of yours born out of wedlock: you acknowledge them before everyone
            starter.AddPlayerLine("ailegacy_acknowledge", "hero_main_options", "ailegacy_acknowledge_reply", L.T("AIL_e7b1c823", "{=!}Te reconozco como hijo mío, legítimo ante todos."),
                () => Bastards.CanAcknowledge(Hero.OneToOneConversationHero),
                () => Bastards.Acknowledge(Hero.OneToOneConversationHero), priority: 101);
            starter.AddDialogLine("ailegacy_acknowledge_reply", "ailegacy_acknowledge_reply", "lord_pretalk", L.T("AIL_e7b1c824", "{=!}*se te queda mirando un largo momento* Toda la vida esperando oír eso."),
                null, null, priority: 100);
            // On the map: dismount and talk face to face in a terrain scene
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

            // Lordless parties on the map: the leader of some bandits (whether they attack you or you approach them),
            // a caravan or villagers. Each has its own branch in the game, and "Back" returns to it.
            foreach (var token in BandTokens)
            {
                string back = token;
                starter.AddPlayerLine("ailegacy_d_start_" + token, token, "ailegacy_d_wait", L.T("AIL_9a8d90d8", "{=!}Hablar [AI Legacy]"),
                    () => S.Get.UseDialogStyle && CanTalkBand(),
                    () =>
                    {
                        _fromPrisonerBranch = false;
                        _fromSurrenderDemand = false;
                        _bandReturn = back;
                        var s = ChatSession.StartDialog(BandSpeaker());
                        if (s != null) MainThread.Post(s.AskInput);
                    }, priority: 120);
                starter.AddDialogLine("ailegacy_d_ret_" + token, "ailegacy_d_ret", back, L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                    () => _bandReturn == back && Hero.OneToOneConversationHero == null && BandParty() != null, null, priority: 126);
            }

            // The NPC "waits" while you type. The options here are evaluated BEFORE you type,
            // so they can only be "See their reply" / "Back".
            starter.AddDialogLine("ailegacy_d_wait", "ailegacy_d_wait", "ailegacy_d_optw", "{=!}{AIL_DLG}",
                () => { MBTextManager.SetTextVariable("AIL_DLG", new TextObject(L.T("AIL_58ce073f", "{=!}(Envía tu mensaje y espera su respuesta)"))); return true; },
                null, priority: 120);
            starter.AddPlayerLine("ailegacy_d_view", "ailegacy_d_optw", "ailegacy_d_show", L.T("AIL_98823032", "{=!}Ver su respuesta"),
                () => Cur() != null, null, priority: 130);
            starter.AddPlayerLine("ailegacy_d_backw", "ailegacy_d_optw", "ailegacy_d_ret", L.T("AIL_0042ce81", "{=!}Volver"),
                () => true, null, priority: 110);

            // After seeing the reply
            starter.AddPlayerLine("ailegacy_d_say", "ailegacy_d_opts", "ailegacy_d_wait", L.T("AIL_92eb34bb", "{=!}Decir algo más"),
                () => { var c = Cur(); return c != null && !c.Ended; },
                () => { var c = Cur(); if (c != null) MainThread.Post(c.AskInput); }, priority: 125);
            // Whole conversation in a scrollable window
            starter.AddPlayerLine("ailegacy_d_log", "ailegacy_d_opts", "ailegacy_d_show", L.T("AIL_cdf7046b", "{=!}Repasar lo hablado"),
                () => { var c = Cur(); return c != null && c.Who != null && c.Who.Memory.Turns.Count > 2; },
                () => { var c = Cur(); if (c != null) { var who = c.Who; MainThread.Post(() => UI.Books.OpenTranscript(who)); } }, priority: 109);
            starter.AddPlayerLine("ailegacy_d_video_make", "ailegacy_d_opts", "ailegacy_d_show", L.T("AIL_e7b1c85f", "{=!}Grabar esta escena en vídeo"),
                () => { var c = Cur(); return c != null && c.CanMakeVideo && !c.HasVideo; },
                () => { var c = Cur(); if (c != null) MainThread.Post(c.MakeVideo); }, priority: 106);
            starter.AddPlayerLine("ailegacy_d_video_show", "ailegacy_d_opts", "ailegacy_d_show", L.T("AIL_e7b1c860", "{=!}Ver el vídeo"),
                () => { var c = Cur(); return c != null && c.HasVideo; },
                () => { var c = Cur(); if (c != null) MainThread.Post(c.ShowVideo); }, priority: 122);
            starter.AddPlayerLine("ailegacy_d_picture", "ailegacy_d_opts", "ailegacy_d_show", L.T("AIL_e7b1c7d1", "{=!}Ver la escena"),
                () => { var c = Cur(); return c != null && c.HasPicture; },
                () => { var c = Cur(); if (c != null) MainThread.Post(c.ShowPicture); }, priority: 123);
            // The last reply went wrong: the player says how, and it may become a lesson
            starter.AddPlayerLine("ailegacy_d_flag", "ailegacy_d_opts", "ailegacy_d_show", L.T("AIL_e7b1c7b6", "{=!}Señalar un error de la IA"),
                () => { var c = Cur(); return c != null && c.CanReport; },
                () => { var c = Cur(); if (c != null) MainThread.Post(c.ReportMistake); }, priority: 108);
            starter.AddPlayerLine("ailegacy_d_leave", "ailegacy_d_opts", "close_window", L.T("AIL_41e43789", "{=!}Marcharse"),
                () => { var c = Cur(); return c != null && c.Ended; },
                () =>
                {
                    try { if (PlayerEncounter.Current != null && !PlayerEncounter.InsideSettlement && !Combat.KeepEncounter) PlayerEncounter.LeaveEncounter = true; }
                    catch (Exception ex) { Log.Error("Leaving", ex); }
                }, priority: 124);
            starter.AddPlayerLine("ailegacy_d_back", "ailegacy_d_opts", "ailegacy_d_ret", L.T("AIL_0042ce81", "{=!}Volver"),
                () => { var c = Cur(); return c == null || !c.Ended; }, null, priority: 110);

            // The reply in the dialogue box. If they're still thinking, loop back to "See their reply".
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

            // Back to the game's normal options
            // (to the prisoner branch if we came from there, since the game's exit lives in it: "Don't do anything stupid..." -> close)
            starter.AddDialogLine("ailegacy_d_ret_p", "ailegacy_d_ret", PrisonerOptions, L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => _fromPrisonerBranch && IsMainPartyPrisoner(), null, priority: 125);
            starter.AddDialogLine("ailegacy_d_ret_s", "ailegacy_d_ret", SurrenderDemandOptions, L.T("AIL_e7b1c4d7", "{=!}¿Y bien? ¿Os rendís o no?"),
                () => _fromSurrenderDemand && Hero.OneToOneConversationHero != null, null, priority: 124);
            starter.AddDialogLine("ailegacy_d_ret_h", "ailegacy_d_ret", "hero_main_options", L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => Hero.OneToOneConversationHero != null, null, priority: 120);
            starter.AddDialogLine("ailegacy_d_ret_r", "ailegacy_d_ret", "town_or_village_player", L.T("AIL_fb96809a", "{=!}¿Algo más?"),
                () => Hero.OneToOneConversationHero == null, null, priority: 110);
        }

        /// <summary>Player options when talking to a prisoner in your party (PrisonerRecruitCampaignBehavior).</summary>
        private const string PrisonerOptions = "prisoner_recruit_start_player";
        /// <summary>The current AI Legacy chat was started from the prisoner branch.</summary>
        private static bool _fromPrisonerBranch;
        /// <summary>Player options after a hostile lord's surrender demand (LordConversationsCampaignBehavior).</summary>
        private const string SurrenderDemandOptions = "player_responds_to_surrender_demand";
        private static bool _fromSurrenderDemand;

        /// <summary>The hero you're talking to is a prisoner in your party (same check the game uses for its branch).</summary>
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
        /// Game dialogue branches for lordless parties (TaleWorlds.CampaignSystem 1.4.8): BanditInteractionsCampaignBehavior
        /// ("bandit_attacker" if you approach them, "bandit_defender" if they attack you), CaravansCampaignBehavior and VillagerCampaignBehavior.
        /// </summary>
        private static readonly string[] BandTokens = { "bandit_attacker", "bandit_defender", "caravan_talk", "village_farmer_talk" };
        /// <summary>Branch that "Back" returns to when the current chat is with the leader of a lordless party.</summary>
        private static string _bandReturn;

        /// <summary>The lordless party you're talking to on the map right now.</summary>
        private static TaleWorlds.CampaignSystem.Party.MobileParty BandParty()
        {
            try
            {
                if (TaleWorlds.MountAndBlade.Mission.Current != null) return null;
                // When bandits attack you, the conversation party may only be reachable through the encounter
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

        private static readonly Prompt.Tpl TAmbush = new Prompt.Tpl("npc_initiative/bandit_ambush.txt",
@"You lead a band of outlaws ({theirs} men) and you have just caught {player}'s party ({yours} men) on the road and barred its way. They carry {purse} denars.{sea}
Speak first, as the leader of such men would: make your demand (their purse, their goods, a toll, or their lives), boast, or test them. Weigh the odds honestly: with fewer men than them you bluff, bargain or look for a way out; with many more you press hard.
How it ends is decided by what is said, with your actions: take their money (take_gold), let them pass (let_pass), attack them (attack), surrender to them if you are beaten, or, if they offer something better than the road, join them with your whole band. Do not settle it in your very first words.");
        private static readonly Prompt.Tpl TAmbushSea = new Prompt.Tpl("npc_initiative/bandit_ambush_sea.txt", " You are pirates, and it is at sea: you have come alongside their ship.");

        /// <summary>Bandits stopped the player (the game's "bandit_defender" branch). Runs the game's own condition so its later lines have their texts.</summary>
        private static bool BanditAmbush()
        {
            try
            {
                var party = PlayerEncounter.EncounteredParty;
                if (PlayerEncounter.Current == null || party == null || !party.IsMobile || party.MapFaction == null || !party.MapFaction.IsBanditFaction) return false;
                if (PlayerEncounter.PlayerIsAttacker) return false;
                var behavior = Campaign.Current.GetCampaignBehavior<TaleWorlds.CampaignSystem.CampaignBehaviors.BanditInteractionsCampaignBehavior>();
                var cond = HarmonyLib.AccessTools.Method(typeof(TaleWorlds.CampaignSystem.CampaignBehaviors.BanditInteractionsCampaignBehavior), "bandit_start_defender_condition");
                if (behavior == null || cond == null) return false;      // without its texts, its own lines would come out empty
                return (bool)cond.Invoke(behavior, null);
            }
            catch (Exception ex) { Log.Error("Bandit ambush?", ex); return false; }
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
