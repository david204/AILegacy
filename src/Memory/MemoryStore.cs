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
        [JsonProperty("d")] public double Day;       // día de campaña (CampaignTime.Now.ToDays)

        /// <summary>
        /// No es algo dicho: es una nota de lo que ha pasado de verdad («[Done: …]», «[Did not happen: …]», «[Since you last spoke: …]»).
        /// Se guarda como turno del jugador para que llegue al modelo en su sitio dentro del historial.
        /// </summary>
        [JsonIgnore]
        public bool IsNote => Role == "player" && Text != null && Text.Length > 2 && Text[0] == '['
                              && (Text.StartsWith("[Done:") || Text.StartsWith("[Did not happen:") || Text.StartsWith("[Since you last spoke:") || Text.StartsWith("[Pending:"));
    }

    /// <summary>Un tramo del resumen de vuestra historia, con los días de campaña que cubre (-1 = sin fecha, de antes de fechar los tramos).</summary>
    public class SummaryPart
    {
        [JsonProperty("from")] public double FromDay = -1;
        [JsonProperty("to")] public double ToDay = -1;
        [JsonProperty("text")] public string Text = "";
    }

    public class CharacterProfile
    {
        [JsonProperty("given_name")] public string GivenName = "";   // solo para gente sin nombre propio (residentes)
        [JsonProperty("personality")] public string Personality = "";
        [JsonProperty("backstory")] public string Backstory = "";
        [JsonProperty("speech_style")] public string SpeechStyle = "";
        [JsonProperty("goals")] public string Goals = "";
        [JsonProperty("secret")] public string Secret = "";
        // Carácter ampliado (v0.11.1)
        [JsonProperty("drives")] public string Drives = "";                  // qué le mueve y qué teme
        [JsonProperty("values")] public string Values = "";                  // qué respeta y qué desprecia
        [JsonProperty("social_behavior")] public string SocialBehavior = ""; // cómo trata a superiores, iguales, inferiores y extraños
        [JsonProperty("cognitive_style")] public string CognitiveStyle = ""; // humor, cuánto miente, cómo decide, cuánto se fía
        [JsonProperty("religion")] public string Religion = "";
        [JsonProperty("reputation")] public string Reputation = "";          // qué se dice de él
        [JsonProperty("sample_lines")] public List<string> SampleLines = new List<string>();

        [JsonIgnore]
        public bool IsEmpty => string.IsNullOrWhiteSpace(Personality) && string.IsNullOrWhiteSpace(Backstory)
                               && string.IsNullOrWhiteSpace(SpeechStyle);

        /// <summary>Tiene lo básico pero le faltan varias partes (fichas antiguas o importadas de otro mod): se completan en la siguiente charla.</summary>
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

        /// <summary>Rellena solo lo que falta con lo que trae otra ficha; lo que ya había no se toca.</summary>
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

    /// <summary>Lo que un personaje guarda de otro: cuánto se fía de él y lo último que ha pasado entre los dos.</summary>
    public class OtherBond
    {
        [JsonProperty("name")] public string Name;
        [JsonProperty("trust")] public int Trust;                 // -100..100
        [JsonProperty("day")] public double Day = -1;
        [JsonProperty("notes")] public List<string> Notes = new List<string>();
    }

    /// <summary>Página del libro de memorias: un tramo de vuestra historia contado como un capítulo.</summary>
    public class MemoryPage
    {
        [JsonProperty("title")] public string Title;
        [JsonProperty("text")] public string Text;
        [JsonProperty("day")] public double Day;
    }

    public class HeroMemory
    {
        [JsonProperty("id")] public string HeroId;
        [JsonProperty("name")] public string HeroName;
        /// <summary>Resumen de lo hablado antes de las últimas conversaciones.</summary>
        [JsonProperty("summary")] public string Summary = "";
        /// <summary>El mismo resumen, por tramos fechados (lo mantiene MemorySummarizer; «summary» es su texto completo).</summary>
        [JsonProperty("summaries")] public List<SummaryPart> Summaries = new List<SummaryPart>();
        /// <summary>Cosas concretas que el personaje ha decidido recordar sobre el jugador.</summary>
        [JsonProperty("notes")] public List<string> Notes = new List<string>();
        [JsonProperty("turns")] public List<ChatTurn> Turns = new List<ChatTurn>();
        [JsonProperty("last_day")] public double LastConversationDay = -1;
        [JsonProperty("talks")] public int ConversationCount;
        [JsonProperty("rel_total")] public int RelationChangedTotal;
        /// <summary>Nombre con el que el jugador se presentó (puede ser falso). null = no se ha presentado.</summary>
        [JsonProperty("player_name")] public string KnownPlayerName;
        /// <summary>Personalidad inventada por la IA en la primera charla; fija desde entonces.</summary>
        [JsonProperty("profile")] public CharacterProfile Profile;
        [JsonProperty("lies")] public int LiesSuspected;

        // Conocimiento del mundo (world_data): se tira una vez por personaje
        [JsonProperty("knowledge_rolled")] public bool KnowledgeRolled;
        [JsonProperty("secrets")] public List<string> KnownSecretIds = new List<string>();
        [JsonProperty("info")] public List<string> KnownInfoIds = new List<string>();

        // Cartas
        [JsonProperty("letters_sent")] public int LettersSent;
        [JsonProperty("last_letter_day")] public double LastLetterDay = -1;
        [JsonProperty("owes_reply")] public bool OwesReplyToPlayer;

        // Instrucciones del jugador para este personaje (comando /nota o importadas de AIInfluence)
        [JsonProperty("custom")] public List<string> CustomInstructions = new List<string>();

        // Estado de ánimo actual
        [JsonProperty("mood")] public string Mood = "";
        [JsonProperty("mood_reason")] public string MoodReason = "";
        [JsonProperty("mood_day")] public double MoodDay = -1;

        [JsonProperty("imported")] public string ImportedFrom;

        // Confianza (aparte de la relación): -100..100. Se inicializa en la primera charla.
        [JsonProperty("trust")] public int Trust;
        [JsonProperty("trust_set")] public bool TrustSet;

        // Romance: 0..100
        [JsonProperty("romance")] public int Romance;
        [JsonProperty("romance_day")] public double LastRomanceDay = -1;
        [JsonProperty("intimacy_day")] public double LastIntimacyDay = -1;

        /// <summary>Foto de su situación la última vez que se escribió (informativa: el mod siempre lee el juego en vivo).</summary>
        [JsonProperty("snapshot")] public Dictionary<string, object> Snapshot;
        /// <summary>Última respuesta en bruto de la IA (para depurar).</summary>
        [JsonProperty("last_ai_response")] public string LastAiResponse;
        /// <summary>Archivo creado al empezar la campaña, antes de hablar con él (reyes, familia, personajes de la historia).</summary>
        [JsonProperty("seeded")] public bool Seeded;

        /// <summary>Lo que ha sabido del jugador por boca de otros NPC.</summary>
        [JsonProperty("heard")] public List<HeardItem> Heard = new List<HeardItem>();
        /// <summary>Última vez que se acercó por su cuenta a hablar con el jugador en el mapa, y por qué.</summary>
        [JsonProperty("initiative_day")] public double LastInitiativeDay = -1;
        [JsonProperty("initiative_reason")] public string InitiativeReason;

        // Molde de carácter (world_data\archetypes.json): se asigna una vez
        [JsonProperty("archetype")] public string Archetype;
        [JsonProperty("archetype_text")] public string ArchetypeText;
        [JsonProperty("morality")] public string Morality;
        [JsonProperty("profanity")] public string Profanity;
        [JsonProperty("piety")] public string Piety;
        // Evolución del carácter
        [JsonProperty("profile_revision")] public int ProfileRevision;
        [JsonProperty("development_day")] public double LastDevelopmentDay = -1;
        [JsonIgnore] public bool DevelopmentInFlight;
        /// <summary>Libro de memorias: capítulos escritos por la IA al resumir.</summary>
        [JsonProperty("pages")] public List<MemoryPage> Pages = new List<MemoryPage>();
        /// <summary>Frases antiguas que ya no están en el historial (se resumieron): se buscan en ellas los recuerdos relevantes.</summary>
        [JsonProperty("archive")] public List<ChatTurn> Archive = new List<ChatTurn>();
        /// <summary>Cuántas frases se han archivado en total (el archivo tiene tope; el índice de recuerdos, no).</summary>
        [JsonProperty("archived_total")] public int ArchivedTotal;
        /// <summary>Vector de significado de lo último que ha dicho el jugador, para el prompt de este turno (no se guarda).</summary>
        [JsonIgnore] public sbyte[] QueryVec;
        [JsonIgnore] public string QueryText;
        /// <summary>Lo que hay entre este personaje y OTROS personajes (no contigo): cómo se llevan y de qué han tratado últimamente.</summary>
        [JsonProperty("others")] public Dictionary<string, OtherBond> Others = new Dictionary<string, OtherBond>();

        // Tensión del momento: 0..100, baja sola con los días
        [JsonProperty("tension")] public int Tension;
        [JsonProperty("tension_day")] public double TensionDay = -1;

        [JsonIgnore] public bool SummarizeInFlight;
        /// <summary>Lo que estaba hablando con otro hace un momento en la escena (no se guarda).</summary>
        [JsonIgnore] public string RecentTalk;
        [JsonIgnore] public DateTime RecentTalkAt;
        [JsonIgnore] public bool IsResident;
    }

    /// <summary>Carta en camino (del jugador a un NPC o de un NPC al jugador).</summary>
    public class PendingLetter
    {
        [JsonProperty("hero")] public string HeroId;
        [JsonProperty("text")] public string Text;
        [JsonProperty("arrive")] public double ArriveDay;
        [JsonProperty("from_player")] public bool FromPlayer;
    }

    /// <summary>Todo lo que AI Legacy guarda dentro de la partida.</summary>
    public static class MemoryStore
    {
        private static Dictionary<string, HeroMemory> _byHero = new Dictionary<string, HeroMemory>();
        // Gente del pueblo: memoria solo durante la sesión
        private static Dictionary<string, HeroMemory> _residents = new Dictionary<string, HeroMemory>();

        public static double LastLetterDay = -1;
        public static double LastInitiativeDay = -1;
        public static List<PendingLetter> Letters = new List<PendingLetter>();

        public static HeroMemory Get(string id, string name, bool resident = false)
        {
            var dict = resident ? _residents : _byHero;
            if (!dict.TryGetValue(id, out var m))
            {
                // Los figurantes con los que nunca se habló (mirones, gente que pasa) no se acumulan sin límite en la sesión
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

        /// <summary>Rellena todo desde los archivos de la campaña (save_data).</summary>
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
            // Sin datos que cargar: el estado se queda como está, pero lo que estuviera a medias ya no vale
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
