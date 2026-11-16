using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Dialogue;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.World
{
    public class NewsItem
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("day")] public double Day;
        [JsonProperty("type")] public string Type;           // war, peace, siege, capture, battle, death, ruler, clan, prisoner, rumor...
        [JsonProperty("text")] public string Text;
        /// <summary>1 = the place, 2 = the court (kingdom nobles), 3 = the whole kingdom, 4 = the whole world.</summary>
        [JsonProperty("imp")] public int Importance;
        [JsonProperty("kingdoms")] public List<string> Kingdoms = new List<string>();
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("heroes")] public List<string> Heroes = new List<string>();
        [JsonProperty("player")] public bool PlayerInvolved;
        /// <summary>Player-facing version. Text stays in English for prompts.</summary>
        [JsonProperty("title_es")] public string TitleEs;
        [JsonProperty("text_es")] public string TextEs;

        /// <summary>Language the player-facing title and text were written in. AI-written news has none, it uses the reply language.</summary>
        [JsonProperty("lang")] public string Lang;
        /// <summary>What a ruler's statement carried out, each with its kind. Null on older saves: there the results are inside TextEs.</summary>
        [JsonProperty("decisions", NullValueHandling = NullValueHandling.Ignore)] public List<NewsDecision> Decisions;
        /// <summary>A chapter of a world event's story: the event id and its number (1 = how it began). Null/0 on older saves.</summary>
        [JsonProperty("story", NullValueHandling = NullValueHandling.Ignore)] public string StoryId;
        [JsonProperty("chapter", DefaultValueHandling = DefaultValueHandling.Ignore)] public int Chapter;
        /// <summary>Sudden blows the event dealt once, when it was told (they are not kept as effects).</summary>
        [JsonProperty("hits", NullValueHandling = NullValueHandling.Ignore)] public List<NewsHit> Hits;
        /// <summary>The outbreak the event started, as it was then.</summary>
        [JsonProperty("disease", NullValueHandling = NullValueHandling.Ignore)] public NewsDisease Disease;
        /// <summary>Local talk that must never set off a world reaction, whatever the importance threshold is.</summary>
        [JsonIgnore] public bool NoReaction;
        /// <summary>Local title and text are only valid if they're still in the current language.</summary>
        [JsonIgnore] private bool LocalOk => string.IsNullOrEmpty(Lang) || Lang == AILegacy.Core.L.Code;
        [JsonIgnore] public string DisplayTitle => string.IsNullOrWhiteSpace(TitleEs) || !LocalOk ? Headline(Text) : TitleEs;
        [JsonIgnore] public string DisplayText => string.IsNullOrWhiteSpace(TextEs) || !LocalOk ? Text : TextEs;
        private static string Headline(string t)
        {
            t = t ?? "";
            int dot = t.IndexOf('.');
            if (dot > 10 && dot <= 70) return t.Substring(0, dot);
            return t.Length > 60 ? t.Substring(0, 60) + "…" : t;
        }
    }

    /// <summary>One decision of a statement: its kind (war, peace, alliance, reject, trade, territory, tribute, tax, quarantine, clan, law, other) and what was done.</summary>
    public class NewsDecision
    {
        [JsonProperty("kind")] public string Kind;
        [JsonProperty("text")] public string Text;
    }

    /// <summary>Nobles of one realm taken together by another, waiting to see whether they are still held two weeks later.</summary>
    public class CaptiveWatch
    {
        [JsonProperty("day")] public double Day;
        [JsonProperty("captor")] public string CaptorId;
        [JsonProperty("own")] public string OwnId;
        [JsonProperty("heroes")] public List<string> Heroes = new List<string>();
        [JsonProperty("by", NullValueHandling = NullValueHandling.Ignore)] public string ById;
        [JsonProperty("settlement", NullValueHandling = NullValueHandling.Ignore)] public string SettlementId;
    }

    /// <summary>A one-time change an event made to a settlement.</summary>
    public class NewsHit
    {
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("prosperity", DefaultValueHandling = DefaultValueHandling.Ignore)] public float Prosperity;
        [JsonProperty("food", DefaultValueHandling = DefaultValueHandling.Ignore)] public float Food;
        [JsonProperty("security", DefaultValueHandling = DefaultValueHandling.Ignore)] public float Security;
        [JsonProperty("loyalty", DefaultValueHandling = DefaultValueHandling.Ignore)] public float Loyalty;
    }

    /// <summary>An outbreak an event set off, copied when it began so the card can still tell it after it ends.</summary>
    public class NewsDisease
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("name")] public string Name;
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("severity")] public int Severity;
        [JsonProperty("days")] public int Days;
    }

    /// <summary>
    /// World news and rumors. Whether an NPC "hears" a piece of news depends on its importance,
    /// whether they're involved, whether they're in the affected place or kingdom, and how much time has passed.
    /// </summary>
    public static class NewsStore
    {
        private static List<NewsItem> _items = new List<NewsItem>();
        private const int MaxItems = 400;

        public static IEnumerable<NewsItem> All => _items;

        public static void Clear() => _items = new List<NewsItem>();

        public static bool Contains(NewsItem n) => n != null && _items.Contains(n);

        public static void Delete(string id) => _items.RemoveAll(n => n.Id == id);

        /// <summary>The newest item with that id, or null.</summary>
        public static NewsItem Find(string id) => string.IsNullOrEmpty(id) ? null : _items.LastOrDefault(n => n.Id == id);

        public static void Load(List<NewsItem> items) => _items = items ?? new List<NewsItem>();

        public static void Add(NewsItem n)
        {
            if (n == null || string.IsNullOrWhiteSpace(n.Text)) return;
            n.Id = n.Id ?? Guid.NewGuid().ToString("N").Substring(0, 10);
            n.Day = CampaignTime.Now.ToDays;
            // News written by the mod itself (not the AI) records its language, so it isn't shown after a language switch
            if (n.Lang == null && n.Type != "rumor" && n.Type != "event" && n.Type != "statement" && n.Type != "verdict") n.Lang = AILegacy.Core.L.Code;
            n.Importance = Math.Max(1, Math.Min(4, n.Importance));
            if (n.Heroes.Contains(Hero.MainHero?.StringId)) n.PlayerInvolved = true;
            _items.Add(n);
            if (_items.Count > MaxItems) _items.RemoveRange(0, _items.Count - MaxItems);
            Core.Log.Info($"News [{n.Type}, imp {n.Importance}]: {n.Text}");
            try { WorldSim.OnRealNews(n); } catch { }
        }

        /// <summary>News this character knows right now, most relevant first.</summary>
        public static List<NewsItem> KnownBy(Speaker s, int max)
        {
            if (max <= 0) return new List<NewsItem>();
            double now = CampaignTime.Now.ToDays;
            int lifetime = S.Get.NewsLifetimeDays;

            var scored = new List<(NewsItem item, double score)>();
            foreach (var n in _items)
            {
                double age = now - n.Day;
                if (age < 0 || age > lifetime) continue;
                int how = Reach(s, n, age);
                if (how <= 0) continue;
                double score = how * 10 + n.Importance * 3 - age * 0.15;
                scored.Add((n, score));
            }
            return scored.OrderByDescending(x => x.score).Take(max).Select(x => x.item).ToList();
        }

        // Hero.Find walks the game's whole hero list. Reach asks for the same few ids for every news item of every
        // prompt, so the answers are kept for the campaign day. Game thread only: KnownBy is called while a prompt is
        // built (ticks, UI callbacks, MainThread.Post) or by the editor through MainThread.Invoke, never from a reply's continuation.
        private static readonly Dictionary<string, Hero> HeroCache = new Dictionary<string, Hero>();
        private static int _heroCacheDay = -1;
        private static Campaign _heroCacheFor;

        private static Hero FindHero(string id)
        {
            if (string.IsNullOrEmpty(id)) return null;
            int day = (int)CampaignTime.Now.ToDays;
            // Ids come from news text the AI or the editor wrote, so the size is capped as well
            if (day != _heroCacheDay || _heroCacheFor != Campaign.Current || HeroCache.Count > MaxHeroCache) { HeroCache.Clear(); _heroCacheDay = day; _heroCacheFor = Campaign.Current; }
            if (!HeroCache.TryGetValue(id, out var h)) HeroCache[id] = h = Hero.Find(id);
            return h;
        }

        private const int MaxHeroCache = 2000;

        /// <summary>Campaign loaded, started or swapped in: the heroes kept for the day belong to the one before.</summary>
        public static void ResetSession()
        {
            HeroCache.Clear();
            _heroCacheDay = -1;
            _heroCacheFor = null;
        }

        /// <summary>0 = hasn't heard, 1..4 = how close it hits home.</summary>
        private static int Reach(Speaker s, NewsItem n, double age)
        {
            // Directly involved (them or their clan)
            if (s.IsHero)
            {
                if (n.Heroes.Contains(s.Hero.StringId)) return 4;
                var clan = s.Hero.Clan;
                if (clan != null && n.Heroes.Any(id => FindHero(id)?.Clan == clan)) return 3;
            }

            // At the place (or its bound town/villages)
            if (!string.IsNullOrEmpty(n.SettlementId) && SameArea(s.Settlement ?? s.HomeSettlement, n.SettlementId))
                return 3;

            bool inKingdom = s.Faction != null && n.Kingdoms.Contains(s.Faction.StringId);
            bool court = s.IsHero && (s.Hero.IsLord || s.Hero.IsNotable);

            // News travels. Far-off events take time to arrive and small ones never do. A noble hears from further
            // away than a villager, and a ruler from further still (they have people bringing them news).
            if (Settings.S.Get.RealisticNewsSpread && !string.IsNullOrEmpty(n.SettlementId))
            {
                try
                {
                    var at = Settlement.Find(n.SettlementId);
                    var me = Where(s);
                    if (at != null && me.HasValue)
                    {
                        float d = me.Value.Distance(at.GetPosition2D);
                        float reach = s.IsHero && s.Hero.IsKingdomLeader ? 2f : court ? 1.5f : 1f;
                        float local = 15f * reach, regional = 50f * reach, far = 150f * reach;
                        // A rider covers about 60 map units a day. Big news moves twice as fast (relays, pigeons, criers)
                        if (age < d / (n.Importance >= 4 ? 120.0 : 60.0)) return 0;
                        // A friend or enemy of someone involved hears about it even if it's none of their business
                        bool personal = s.IsHero && n.Heroes.Any(id => { var o = FindHero(id); return o != null && o != s.Hero && Math.Abs(CharacterRelationManager.GetHeroRelation(s.Hero, o)) >= 20; });
                        if (n.Importance <= 1) return d <= local || (personal && d <= far) ? 2 : 0;
                        if (n.Importance == 2)
                        {
                            if (d <= regional) return court || inKingdom ? 2 : 1;
                            if (d > far && !personal) return 0;
                        }
                        if (personal) return 2;
                    }
                }
                catch { }
            }

            switch (n.Importance)
            {
                case 4:
                    return inKingdom ? 3 : (age >= 1 ? 2 : 0);
                case 3:
                    if (inKingdom) return 2;
                    return age >= 7 ? 1 : 0;           // the rest of the world hears later
                case 2:
                    if (inKingdom && court) return 2;
                    if (inKingdom && age >= 3) return 1; // commoners hear it late
                    return 0;
                default:
                    return 0;                          // importance 1: the place only
            }
        }

        /// <summary>Speaker's position: their settlement, their party, or the player's party if traveling with them.</summary>
        private static TaleWorlds.Library.Vec2? Where(Speaker s)
        {
            try
            {
                var st = s.Settlement ?? s.HomeSettlement;
                if (s.IsHero && s.Hero.PartyBelongedTo != null) return s.Hero.PartyBelongedTo.GetPosition2D;
                if (st != null) return st.GetPosition2D;
                if (s.Band != null) return s.Band.GetPosition2D;
            }
            catch { }
            return null;
        }

        private static bool SameArea(Settlement a, string settlementId)
        {
            if (a == null || string.IsNullOrEmpty(settlementId)) return false;
            if (a.StringId == settlementId) return true;
            // village <-> the town/castle it belongs to
            if (a.IsVillage && a.Village?.Bound?.StringId == settlementId) return true;
            var other = Settlement.Find(settlementId);
            if (other != null && other.IsVillage && other.Village?.Bound == a) return true;
            return false;
        }

        public static string Describe(NewsItem n)
        {
            int days = (int)(CampaignTime.Now.ToDays - n.Day);
            string when = days <= 0 ? "today" : days == 1 ? "yesterday" : $"{days} days ago";
            string kind = n.Type == "rumor" ? "Rumour" : "News";
            return $"{kind} ({when}): {n.Text}";
        }
    }
}
