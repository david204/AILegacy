using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.World;
using Newtonsoft.Json;

namespace AILegacy.Memory
{
    public class ChatTurn
    {
        [JsonProperty("r")] public string Role;      // "player" | "npc"
        [JsonProperty("t")] public string Text;
        [JsonProperty("d")] public double Day;       // campaign day (CampaignTime.Now.ToDays)

        /// <summary>
        /// Not something said, but a note on what actually happened ("[Done: ...]", "[Did not happen: ...]", "[Since you last spoke: ...]").
        /// Stored as a player turn so the model sees it in the right place in the history.
        /// </summary>
        [JsonIgnore]
        public bool IsNote => Role == "player" && Text != null && Text.Length > 2 && Text[0] == '['
                              && (Text.StartsWith("[Done:") || Text.StartsWith("[Did not happen:") || Text.StartsWith("[Since you last spoke:") || Text.StartsWith("[Pending:"));
    }

    /// <summary>One part of the shared-history summary, with the campaign days it covers (-1 = undated, from older saves).</summary>
    public class SummaryPart
    {
        [JsonProperty("from")] public double FromDay = -1;
        [JsonProperty("to")] public double ToDay = -1;
        [JsonProperty("text")] public string Text = "";
    }

    public class CharacterProfile
    {
        [JsonProperty("given_name")] public string GivenName = "";   // only for people with no name of their own (residents)
        [JsonProperty("personality")] public string Personality = "";
        [JsonProperty("backstory")] public string Backstory = "";
        [JsonProperty("speech_style")] public string SpeechStyle = "";
        [JsonProperty("goals")] public string Goals = "";
        [JsonProperty("secret")] public string Secret = "";
        [JsonProperty("drives")] public string Drives = "";                  // what moves them and what they fear
        [JsonProperty("values")] public string Values = "";                  // what they respect and what they despise
        [JsonProperty("social_behavior")] public string SocialBehavior = ""; // how they treat superiors, equals, inferiors and strangers
        [JsonProperty("cognitive_style")] public string CognitiveStyle = ""; // humour, how much they lie, how they decide, how trusting
        [JsonProperty("religion")] public string Religion = "";
        [JsonProperty("reputation")] public string Reputation = "";          // what people say about them
        /// <summary>How easily they are fooled: 0 shrewd, 0.5 ordinary caution, 1 trusting. -1 = not set (older profiles).</summary>
        [JsonProperty("naivety")] public double Naivety = -1;
        [JsonProperty("sample_lines")] public List<string> SampleLines = new List<string>();

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrWhiteSpace(Personality) && string.IsNullOrWhiteSpace(Backstory)
                               && string.IsNullOrWhiteSpace(SpeechStyle);

        /// <summary>Has the basics but is missing several parts (old or imported profiles). They get filled in on the next talk.</summary>
        [JsonIgnore]
        public bool IsIncomplete
        {
            get
            {
                if (IsEmpty) return false;
                int missing = 0;
                foreach (var v in new[] { Goals, Secret, Drives, Values, SocialBehavior, Religion, Reputation })
                    if (string.IsNullOrWhiteSpace(v)) missing++;
                if (SampleLines == null || SampleLines.Count == 0) missing++;
                return missing >= 3;
            }
        }

        /// <summary>Fills only the empty fields from another profile and leaves existing ones alone. Returns how many were filled.</summary>
        public int FillMissingFrom(CharacterProfile o)
        {
            if (o == null) return 0;
            int n = 0;
            string F(string mine, string theirs) { if (string.IsNullOrWhiteSpace(mine) && !string.IsNullOrWhiteSpace(theirs)) { n++; return theirs; } return mine; }
            Personality = F(Personality, o.Personality); Backstory = F(Backstory, o.Backstory); SpeechStyle = F(SpeechStyle, o.SpeechStyle); Goals = F(Goals, o.Goals);
            Secret = F(Secret, o.Secret); Drives = F(Drives, o.Drives); Values = F(Values, o.Values); SocialBehavior = F(SocialBehavior, o.SocialBehavior);
            CognitiveStyle = F(CognitiveStyle, o.CognitiveStyle); Religion = F(Religion, o.Religion); Reputation = F(Reputation, o.Reputation);
            if ((SampleLines == null || SampleLines.Count == 0) && o.SampleLines != null && o.SampleLines.Count > 0) { SampleLines = o.SampleLines; n++; }
            return n;
        }
    }

    /// <summary>What a character keeps about another character: how much they trust them and what last happened between them.</summary>
    public class OtherBond
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("trust")] public int Trust;                 // -100..100
        [JsonProperty("day")] public double Day = -1;
        [JsonProperty("notes")] public List<string> Notes = new List<string>();
    }

    /// <summary>A page of the memory book: part of your shared history told as a chapter.</summary>
    public class MemoryPage
    {
        [JsonProperty("title")] public string Title;
        [JsonProperty("text")] public string Text;
        [JsonProperty("day")] public double Day;
        /// <summary>File name of its illustration in save_data\[campaign]\images, if it has one.</summary>
        [JsonProperty("image", NullValueHandling = NullValueHandling.Ignore)] public string Image;
        /// <summary>The visual description the illustration was drawn from. Kept so another try costs no AI request.</summary>
        [JsonProperty("image_prompt", NullValueHandling = NullValueHandling.Ignore)] public string ImagePrompt;
    }

    public class HeroMemory
    {
        [JsonProperty("id")] public string HeroId;
        [JsonProperty("name")] public string HeroName;
        /// <summary>Summary of what was said before the recent conversations.</summary>
        [JsonProperty("summary")] public string Summary = "";
        /// <summary>Same summary split into dated parts. MemorySummarizer keeps it; "summary" holds the full text.</summary>
        [JsonProperty("summaries")] public List<SummaryPart> Summaries = new List<SummaryPart>();
        /// <summary>Specific things the character chose to remember about the player.</summary>
        [JsonProperty("notes")] public List<string> Notes = new List<string>();
        [JsonProperty("turns")] public List<ChatTurn> Turns = new List<ChatTurn>();
        [JsonProperty("last_day")] public double LastConversationDay = -1;
        [JsonProperty("talks")] public int ConversationCount;
        [JsonProperty("rel_total")] public int RelationChangedTotal;
        /// <summary>Name the player introduced themselves with (may be fake). null = never introduced.</summary>
        [JsonProperty("player_name")] public string KnownPlayerName;
        /// <summary>What the player has said about themselves that this character could not check (clan, rank, wealth...).</summary>
        [JsonProperty("player_claims")] public string KnownPlayerClaims;
        /// <summary>Personality the AI made up on the first talk, fixed from then on.</summary>
        [JsonProperty("profile")] public CharacterProfile Profile;
        [JsonProperty("lies")] public int LiesSuspected;

        // World knowledge (world_data), rolled once per character
        [JsonProperty("knowledge_rolled")] public bool KnowledgeRolled;
        [JsonProperty("secrets")] public List<string> KnownSecretIds = new List<string>();
        [JsonProperty("info")] public List<string> KnownInfoIds = new List<string>();

        // Letters
        [JsonProperty("letters_sent")] public int LettersSent;
        [JsonProperty("last_letter_day")] public double LastLetterDay = -1;
        [JsonProperty("owes_reply")] public bool OwesReplyToPlayer;

        // Player instructions for this character (the /nota command, or imported)
        [JsonProperty("custom")] public List<string> CustomInstructions = new List<string>();

        // Current mood
        [JsonProperty("mood")] public string Mood = "";
        [JsonProperty("mood_reason")] public string MoodReason = "";
        /// <summary>Mood and reason as one phrase in the player's language, for the conversation panel.</summary>
        [JsonProperty("mood_shown")] public string MoodShown = "";
        [JsonProperty("mood_day")] public double MoodDay = -1;

        [JsonProperty("imported")] public string ImportedFrom;

        // Trust, separate from relation: -100..100. Set on the first talk.
        [JsonProperty("trust")] public int Trust;
        [JsonProperty("trust_set")] public bool TrustSet;

        // Romance: 0..100
        [JsonProperty("romance")] public int Romance;
        [JsonProperty("romance_day")] public double LastRomanceDay = -1;
        [JsonProperty("intimacy_day")] public double LastIntimacyDay = -1;

        /// <summary>Snapshot of their situation at the last write. Informational only, the mod always reads the live game.</summary>
        [JsonProperty("snapshot")] public Dictionary<string, object> Snapshot;
        /// <summary>Last raw AI reply, for debugging.</summary>
        [JsonProperty("last_ai_response")] public string LastAiResponse;
        /// <summary>File created at campaign start, before any talk (rulers, family, story characters).</summary>
        [JsonProperty("seeded")] public bool Seeded;

        /// <summary>What they heard about the player from other NPCs.</summary>
        [JsonProperty("heard")] public List<HeardItem> Heard = new List<HeardItem>();
        /// <summary>Last time they came up to the player on the map on their own, and why.</summary>
        [JsonProperty("initiative_day")] public double LastInitiativeDay = -1;
        [JsonProperty("initiative_reason")] public string InitiativeReason;

        // Character archetype (world_data\archetypes.json), assigned once
        [JsonProperty("archetype")] public string Archetype;
        [JsonProperty("archetype_text")] public string ArchetypeText;
        [JsonProperty("morality")] public string Morality;
        [JsonProperty("profanity")] public string Profanity;
        [JsonProperty("piety")] public string Piety;
        // Character development
        [JsonProperty("profile_revision")] public int ProfileRevision;
        [JsonProperty("development_day")] public double LastDevelopmentDay = -1;
        [JsonIgnore] public bool DevelopmentInFlight;
        /// <summary>Memory book: chapters the AI writes when summarizing.</summary>
        [JsonProperty("pages")] public List<MemoryPage> Pages = new List<MemoryPage>();
        /// <summary>Old lines that were summarized out of the history. Searched for relevant memories.</summary>
        [JsonProperty("archive")] public List<ChatTurn> Archive = new List<ChatTurn>();
        /// <summary>Total lines ever archived. The archive is capped, the memory index isn't.</summary>
        [JsonProperty("archived_total")] public int ArchivedTotal;
        /// <summary>Embedding of the player's last line, for this turn's prompt. Not saved.</summary>
        [JsonIgnore] public sbyte[] QueryVec;
        [JsonIgnore] public string QueryText;
        [JsonIgnore] public string QueryModel;
        /// <summary>This character's ties to OTHER characters (not the player): how they get on and what they dealt with lately.</summary>
        [JsonProperty("others")] public Dictionary<string, OtherBond> Others = new Dictionary<string, OtherBond>();

        // Tension of the moment: 0..100, decays over the days
        [JsonProperty("tension")] public int Tension;
        [JsonProperty("tension_day")] public double TensionDay = -1;

        [JsonIgnore] public bool SummarizeInFlight;
        /// <summary>What they were just talking about with someone else in the scene. Not saved.</summary>
        [JsonIgnore] public string RecentTalk;
        [JsonIgnore] public DateTime RecentTalkAt;
        [JsonIgnore] public bool IsResident;
    }

    /// <summary>A letter on its way (player to NPC or NPC to player).</summary>
    public class PendingLetter
    {
        [JsonProperty("hero")] public string HeroId;
        [JsonProperty("text")] public string Text;
        [JsonProperty("arrive")] public double ArriveDay;
        [JsonProperty("from_player")] public bool FromPlayer;
    }

    /// <summary>Everything AI Legacy keeps inside the save.</summary>
    public static class MemoryStore
    {
        private static Dictionary<string, HeroMemory> _byHero = new Dictionary<string, HeroMemory>();
        // Townsfolk: memory lasts for the session only
        private static Dictionary<string, HeroMemory> _residents = new Dictionary<string, HeroMemory>();

        public static double LastLetterDay = -1;
        public static double LastInitiativeDay = -1;
        public static List<PendingLetter> Letters = new List<PendingLetter>();

        public static HeroMemory Get(string id, string name, bool resident = false)
        {
            var dict = resident ? _residents : _byHero;
            if (!dict.TryGetValue(id, out var m))
            {
                // Extras nobody talked to (onlookers, passers-by) shouldn't pile up without limit during a session
                if (resident && _residents.Count > 400)
                    foreach (var k in _residents.Where(kv => kv.Value.Turns.Count == 0 && (kv.Value.Profile == null || kv.Value.Profile.IsEmpty)).Select(kv => kv.Key).ToList())
                        _residents.Remove(k);
                m = new HeroMemory { HeroId = id, HeroName = name, IsResident = resident };
                dict[id] = m;
            }
            if (!resident) m.HeroName = name;
            return m;
        }

        public static HeroMemory Find(string heroId) => _byHero.TryGetValue(heroId, out var m) ? m : null;
        public static void Put(HeroMemory m) { if (m?.HeroId != null) _byHero[m.HeroId] = m; }
        public static void Remove(string heroId) { _byHero.Remove(heroId); _residents.Remove(heroId); }
        public static IEnumerable<HeroMemory> AllHeroes => _byHero.Values;
        public static int Count => _byHero.Count;

        public static void Clear()
        {
            _byHero = new Dictionary<string, HeroMemory>();
            _residents = new Dictionary<string, HeroMemory>();
            LastLetterDay = -1;
            Letters = new List<PendingLetter>();
            OrderSystem.Orders = new List<PartyOrder>();
            QuestSystem.Quests = new List<AiQuest>();
            Belongings.Items = new List<RpItem>();
            WorldSim.State = new WorldState();
            WorldSim.ResetSession();
            InfoExchange.Messages = new List<NpcMessage>();
            LastInitiativeDay = -1;
            NewsStore.Clear();
        }

        /// <summary>Loads everything from the campaign files (save_data).</summary>
        public static void LoadFrom(IEnumerable<HeroMemory> heroes, List<NewsItem> news, double lastLetterDay, List<PendingLetter> letters, List<PartyOrder> orders,
            List<AiQuest> quests = null, List<RpItem> items = null, WorldState world = null, List<NpcMessage> messages = null)
        {
            QuestSystem.Quests = quests ?? new List<AiQuest>();
            Belongings.Items = items ?? new List<RpItem>();
            WorldSim.State = world ?? new WorldState();
            WorldSim.ResetSession();
            InfoExchange.Messages = messages ?? new List<NpcMessage>();
            _byHero = new Dictionary<string, HeroMemory>();
            _residents = new Dictionary<string, HeroMemory>();
            foreach (var h in heroes) if (!string.IsNullOrEmpty(h?.HeroId)) _byHero[h.HeroId] = h;
            NewsStore.Clear();
            NewsStore.Load(news);
            LastLetterDay = lastLetterDay;
            Letters = letters ?? new List<PendingLetter>();
            OrderSystem.Orders = orders ?? new List<PartyOrder>();
        }

        public static string Serialize()
        {
            return JsonConvert.SerializeObject(new SaveBlob
            {
                Version = 2,
                Heroes = _byHero.Values.ToList(),
                News = NewsStore.All.ToList(),
                LastLetterDay = LastLetterDay,
                Letters = Letters,
                Orders = OrderSystem.Orders,
                Quests = QuestSystem.Quests,
                Items = Belongings.Items,
                World = WorldSim.State,
                Messages = InfoExchange.Messages,
            }, Formatting.None);
        }

        public static void Deserialize(string json)
        {
            _byHero = new Dictionary<string, HeroMemory>();
            _residents = new Dictionary<string, HeroMemory>();
            NewsStore.Clear();
            OrderSystem.Orders = new List<PartyOrder>();
            // Nothing to load: keep the world state, but anything half-done in the session is no longer valid
            if (string.IsNullOrEmpty(json)) { WorldSim.ResetSession(); return; }
            try
            {
                var blob = JsonConvert.DeserializeObject<SaveBlob>(json);
                foreach (var h in blob?.Heroes ?? new List<HeroMemory>())
                    if (!string.IsNullOrEmpty(h.HeroId)) _byHero[h.HeroId] = h;
                NewsStore.Load(blob?.News);
                LastLetterDay = blob?.LastLetterDay ?? -1;
                Letters = blob?.Letters ?? new List<PendingLetter>();
                OrderSystem.Orders = blob?.Orders ?? new List<PartyOrder>();
                QuestSystem.Quests = blob?.Quests ?? new List<AiQuest>();
                Belongings.Items = blob?.Items ?? new List<RpItem>();
                WorldSim.State = blob?.World ?? new WorldState();
                WorldSim.ResetSession();
                InfoExchange.Messages = blob?.Messages ?? new List<NpcMessage>();
                Log.Info($"Memoria cargada de la partida: {_byHero.Count} personajes, {NewsStore.All.Count()} noticias");
            }
            catch (Exception ex)
            {
                Log.Error("No se pudo leer la memoria guardada en la partida", ex);
            }
        }

        private class SaveBlob
        {
            [JsonProperty("v")] public int Version;
            [JsonProperty("heroes")] public List<HeroMemory> Heroes;
            [JsonProperty("news")] public List<NewsItem> News;
            [JsonProperty("last_letter_day")] public double LastLetterDay = -1;
            [JsonProperty("letters")] public List<PendingLetter> Letters;
            [JsonProperty("orders")] public List<PartyOrder> Orders;
            [JsonProperty("quests")] public List<AiQuest> Quests;
            [JsonProperty("items")] public List<RpItem> Items;
            [JsonProperty("world")] public WorldState World;
            [JsonProperty("messages")] public List<NpcMessage> Messages;
        }
    }
}
