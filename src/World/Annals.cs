using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.World
{
    /// <summary>A great event of this campaign, as the land's chronicle keeps it.</summary>
    public class AnnalEntry
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("day")] public double Day;
        /// <summary>death, kingdom, ruler, marriage, battle, capture, event</summary>
        [JsonProperty("kind")] public string Kind;
        /// <summary>Title in the player's language, for the editor.</summary>
        [JsonProperty("title")] public string Title;
        [JsonProperty("title_en")] public string TitleEn;
        /// <summary>The entry as people remember it, in English, for prompts.</summary>
        [JsonProperty("text")] public string Text;
        /// <summary>Who may come to know it (the tags of world knowledge: all, lords, kingdom:id, culture:id…).</summary>
        [JsonProperty("who")] public List<string> Who = new List<string>();
        /// <summary>Chance (0-100) that one of them knows it, the year it happened. Lower as the years pass, never below 40% of it.</summary>
        [JsonProperty("chance")] public int Chance = 50;
        /// <summary>Heroes it is about: they know it for certain.</summary>
        [JsonProperty("about")] public List<string> About = new List<string>();
        [JsonProperty("place", NullValueHandling = NullValueHandling.Ignore)] public string PlaceId;
        [JsonProperty("importance")] public int Importance = 3;
        [JsonProperty("news", NullValueHandling = NullValueHandling.Ignore)] public string NewsId;
        [JsonProperty("edited", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Edited;
    }

    /// <summary>
    /// The land's chronicle. News fades after some weeks; the great events do not: a ruler's death and how it came, a royal
    /// wedding, a realm destroyed or changing its ruling house, a battle where hundreds fell, a town taken, a world event of the
    /// first rank. Each becomes a short entry the AI writes from the real facts, with who would come to know it and how widely.
    /// Characters roll for each once, like world_data\info.json, at a chance that lowers as the years go by (never below 40% of
    /// it), and keep what they get. The heroes in it always know. The entries can be read, corrected or deleted in the editor.
    /// </summary>
    internal static class Annals
    {
        private static readonly Random Rng = new Random();
        private const string Prefix = "ann_";
        private const int MaxEntries = 600;
        private static readonly List<NewsItem> Pending = new List<NewsItem>();
        private static bool _busy;
        private static int _day = -1, _today;

        public static bool Enabled => S.Get.EnableAnnals && WorldSim.Enabled;
        private static List<AnnalEntry> All => WorldSim.State?.Annals;

        private static readonly Tpl TArticle = new Tpl("annals/article.txt",
@"In Calradia, the medieval world of Mount & Blade II: Bannerlord, this has just happened ({today}):
{event}

Facts about who and where (true, use them, invent nothing that goes against them):
{facts}

Write it as an entry of the land's chronicle, the way people will still tell it years from now: what happened, who, where and how, and what makes it stay in memory (the toll, how someone died, what it changed). 2 or 3 sentences, plain words, no dates in numbers.
Then say who would come to know of it, from this list only: all, lords, faction_leaders, clan_leaders, merchants, townsfolk, villagers, guards{tags}. And ""chance"", from 10 to 100: how likely one of them is to know it. Something the whole world talks about (a ruler's death, a realm falling) is ""all"" with a high chance; a matter of one realm goes to that realm.
Answer ONLY with JSON: {""title"": ""3 to 7 words in {language}"", ""title_en"": ""3 to 7 words in English"", ""article"": ""the entry, in English"", ""who"": [""all""], ""chance"": 60}");

        private static readonly Tpl THeader = new Tpl("annals/prompt_header.txt",
            "The land's chronicle: great events of the past that people still talk about (you may bring them up when they matter)");

        public static void Touch() { var _ = TArticle.Key + THeader.Key; }

        public static void ResetSession() { lock (Pending) Pending.Clear(); _busy = false; }

        // ============================================================ what goes in

        /// <summary>Every news item passes here: the great ones are queued to be written into the chronicle.</summary>
        public static void Consider(NewsItem n)
        {
            try
            {
                if (!Enabled || n == null || string.IsNullOrWhiteSpace(n.Text) || WorldSim.State == null) return;
                if (!Great(n)) return;
                if (All.Any(a => a.NewsId == n.Id)) return;
                lock (Pending) { if (Pending.Any(p => p.Id == n.Id) || Pending.Count >= 20) return; Pending.Add(n); }
                Log.Info($"[annals] queued: {n.Text}");
            }
            catch (Exception ex) { Log.Error("[annals] consider", ex); }
        }

        private static bool Great(NewsItem n)
        {
            switch (n.Type)
            {
                case "kingdom":
                case "ruler":
                    return true;
                case "death":
                    if (n.Importance >= 4) return true;
                    return n.Importance >= 3 && (n.Text.Contains("executed") || n.Text.Contains("murdered") || n.Text.Contains("fell in battle"));
                case "marriage":
                    return n.Importance >= 3 || (n.Heroes ?? new List<string>()).Select(Hero.Find).Any(h => h != null && (Royal(h.Father) || Royal(h.Mother)));
                case "battle":
                    return n.Casualties >= Math.Max(50, S.Get.AnnalsBattleDead);
                case "capture":
                    return n.SettlementId != null && Settlement.Find(n.SettlementId) is Settlement s && s.IsTown;
                case "event":
                    return n.Importance >= 4;
                default:
                    return false;
            }
        }

        private static bool Royal(Hero h) => h != null && (h.IsKingdomLeader || (h.Clan?.Kingdom != null && h.Clan.Kingdom.RulingClan == h.Clan && h.Clan.Leader == h));

        /// <summary>Hourly: one entry written at a time, at most a few a day.</summary>
        public static void Pump()
        {
            try
            {
                if (!Enabled || _busy || WorldSim.State == null) return;
                int day = (int)CampaignTime.Now.ToDays;
                if (day != _day) { _day = day; _today = 0; }
                if (_today >= 4) return;
                NewsItem n;
                lock (Pending) { if (Pending.Count == 0) return; n = Pending[0]; Pending.RemoveAt(0); }
                _today++;
                if (!WorldSim.CanCall) { Add(Plain(n)); return; }
                Write(n);
            }
            catch (Exception ex) { Log.Error("[annals] pump", ex); _busy = false; }
        }

        private static void Write(NewsItem n)
        {
            var cfg = S.Get;
            var heroes = (n.Heroes ?? new List<string>()).Select(Hero.Find).Where(h => h != null).Distinct().Take(6).ToList();
            var facts = new List<string>();
            foreach (var h in heroes) { var f = Facts.HeroFacts(h); if (!string.IsNullOrWhiteSpace(f)) facts.Add(f); }
            var place = n.SettlementId != null ? Settlement.Find(n.SettlementId) : null;
            if (place != null) { var f = Facts.PlaceFacts(place); if (!string.IsNullOrWhiteSpace(f)) facts.Add(f); }
            var tags = TagsFor(n, heroes, place);
            string prompt = TArticle.Format("today", Facts.Today(), "event", n.Text + (n.Casualties > 0 ? $" About {n.Casualties} men fell." : ""),
                "facts", facts.Count > 0 ? string.Join("\n", facts) : "(none)", "tags", tags.Count > 0 ? ", " + string.Join(", ", tags) : "", "language", cfg.Language);
            int session = WorldSim.Session;
            _busy = true;
            Llm.LlmClient.CompleteAsync(new List<Llm.ChatMessage> { new Llm.ChatMessage("user", prompt) }, wantJson: true, task: "world", maxTokensOverride: 450, preferredProvider: cfg.ProviderWorld)
                .ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        _busy = false;
                        try
                        {
                            if (session != WorldSim.Session || WorldSim.State == null) return;
                            var e = Plain(n);
                            if (res != null && res.Ok)
                            {
                                int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                                if (a >= 0 && b > a)
                                {
                                    var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                                    string text = WorldSim.Clean((string)j["article"], 700);
                                    if (text.Length >= 30) e.Text = text;
                                    string title = WorldSim.Clean((string)j["title"], 90), titleEn = WorldSim.Clean((string)j["title_en"], 90);
                                    if (title.Length > 2) e.Title = title;
                                    if (titleEn.Length > 2) e.TitleEn = titleEn;
                                    var allowed = new HashSet<string>(Groups.Concat(tags), StringComparer.OrdinalIgnoreCase);
                                    var who = (j["who"] as JArray)?.Select(x => ((string)x ?? "").Trim()).Where(x => allowed.Contains(x)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                                    if (who != null && who.Count > 0) e.Who = who;
                                    int chance = (int?)j["chance"] ?? e.Chance;
                                    e.Chance = Math.Max(10, Math.Min(100, chance));
                                }
                            }
                            else Log.Warn("[annals] the AI did not write the entry; kept as the news told it");
                            Add(e);
                        }
                        catch (Exception ex) { Log.Error("[annals] entry", ex); }
                    });
                });
        }

        private static readonly string[] Groups = { "all", "lords", "faction_leaders", "clan_leaders", "merchants", "townsfolk", "villagers", "guards" };

        /// <summary>kingdom:id and culture:id of the realms and peoples in it.</summary>
        private static List<string> TagsFor(NewsItem n, List<Hero> heroes, Settlement place)
        {
            var t = new List<string>();
            foreach (var k in n.Kingdoms ?? new List<string>()) if (!string.IsNullOrWhiteSpace(k)) t.Add("kingdom:" + k);
            foreach (var h in heroes) if (h.Culture != null) t.Add("culture:" + h.Culture.StringId);
            if (place?.Culture != null) t.Add("culture:" + place.Culture.StringId);
            return t.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        }

        /// <summary>The entry as the news told it, for when the AI cannot write it.</summary>
        private static AnnalEntry Plain(NewsItem n)
        {
            bool world = n.Importance >= 4 || n.Type == "kingdom" || n.Type == "ruler";
            var e = new AnnalEntry
            {
                Id = Prefix + Guid.NewGuid().ToString("N").Substring(0, 10), Day = n.Day > 0 ? n.Day : CampaignTime.Now.ToDays, Kind = n.Type,
                Title = string.IsNullOrWhiteSpace(n.TitleEs) ? n.Text : n.TitleEs, TitleEn = null,
                Text = n.Text + (n.Casualties > 0 ? $" About {n.Casualties} men fell." : ""),
                Chance = world ? 70 : 55, About = (n.Heroes ?? new List<string>()).Where(x => x != Hero.MainHero?.StringId).ToList(),
                PlaceId = n.SettlementId, Importance = Math.Max(3, n.Importance), NewsId = n.Id,
            };
            if (world) e.Who.Add("all");
            else
            {
                foreach (var k in n.Kingdoms ?? new List<string>()) if (!string.IsNullOrWhiteSpace(k)) e.Who.Add("kingdom:" + k);
                e.Who.Add("lords");
            }
            return e;
        }

        private static void Add(AnnalEntry e)
        {
            var all = All;
            if (all == null || e == null) return;
            all.Add(e);
            if (all.Count > MaxEntries) all.RemoveRange(0, all.Count - MaxEntries);
            Log.Info($"[annals] written: {e.TitleEn ?? e.Title} | {e.Text} (who: {string.Join(", ", e.Who)}, {e.Chance}%)");
            SaveFiles.Dirty();
        }

        // ============================================================ who remembers it

        /// <summary>Rolls this character once for every entry they have not rolled yet, at the chance of today.</summary>
        public static void EnsureRolled(Speaker s)
        {
            try
            {
                if (!Enabled || s?.Memory == null || All == null || All.Count == 0) return;
                var known = s.Memory.KnownInfoIds;
                var tags = s.Tags;
                double now = CampaignTime.Now.ToDays;
                foreach (var e in All)
                {
                    if (string.IsNullOrWhiteSpace(e.Id) || known.Contains(e.Id) || known.Contains("-" + e.Id)) continue;
                    bool own = e.About != null && e.About.Any(tags.Contains);
                    bool fits = own || e.Who == null || e.Who.Count == 0 || e.Who.Any(tags.Contains);
                    if (!fits) continue;
                    if (own || Rng.NextDouble() * 100.0 < ChanceNow(e, now)) known.Add(e.Id);
                    else known.Add("-" + e.Id);
                }
            }
            catch (Exception ex) { Log.Error("[annals] roll", ex); }
        }

        internal static double ChanceNow(AnnalEntry e, double now)
        {
            double years = Math.Max(0, now - e.Day) / Math.Max(1, CampaignTime.DaysInYear);
            return e.Chance * Math.Max(0.4, 1.0 - years * 0.1);
        }

        /// <summary>The entries this character remembers, the weightiest and latest first, for the prompt.</summary>
        public static List<string> For(Speaker s)
        {
            var list = new List<string>();
            try
            {
                if (!Enabled || s?.Memory == null || All == null) return list;
                var known = new HashSet<string>(s.Memory.KnownInfoIds.Where(x => x.StartsWith(Prefix)));
                double now = CampaignTime.Now.ToDays;
                foreach (var e in All.Where(x => known.Contains(x.Id)).OrderByDescending(x => x.Importance * 1000 - (now - x.Day)).Take(Math.Max(0, S.Get.AnnalsInPrompt)))
                    list.Add($"{e.Text} ({Ago(now - e.Day)})");
            }
            catch (Exception ex) { Log.Error("[annals] prompt", ex); }
            return list;
        }

        public static string Header => THeader.Text;

        private static string Ago(double days)
        {
            int d = (int)Math.Max(0, days);
            int year = Math.Max(1, CampaignTime.DaysInYear);
            if (d < 7) return "a few days ago";
            if (d < year) return $"{d / 7} weeks ago";
            int y = d / year;
            return y == 1 ? "a year ago" : $"{y} years ago";
        }

        // ============================================================ editor

        public static object List()
        {
            double now = CampaignTime.Now.ToDays;
            return (All ?? new List<AnnalEntry>()).OrderByDescending(x => x.Day).Select(x => new
            {
                id = x.Id, title = x.Title, title_en = x.TitleEn, text = x.Text, chance = x.Chance, who = x.Who, kind = x.Kind,
                days = (int)(now - x.Day), year = Math.Max(1, CampaignTime.DaysInYear), edited = x.Edited,
                about = (x.About ?? new List<string>()).Select(i => Hero.Find(i)?.Name?.ToString() ?? i),
            }).ToList();
        }

        public static bool Save(string id, string text, int? chance)
        {
            var e = All?.FirstOrDefault(x => x.Id == id);
            if (e == null) return false;
            if (!string.IsNullOrWhiteSpace(text)) e.Text = text.Trim().Length > 1200 ? text.Trim().Substring(0, 1200) : text.Trim();
            if (chance.HasValue) e.Chance = Math.Max(0, Math.Min(100, chance.Value));
            e.Edited = true;
            SaveFiles.Dirty();
            return true;
        }

        public static bool Delete(string id)
        {
            int n = All?.RemoveAll(x => x.Id == id) ?? 0;
            if (n > 0) SaveFiles.Dirty();
            return n > 0;
        }
    }
}
