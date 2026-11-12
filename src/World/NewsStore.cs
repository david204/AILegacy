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
        /// <summary>1 = el lugar, 2 = la corte (nobles del reino), 3 = todo el reino, 4 = todo el mundo.</summary>
        [JsonProperty("imp")] public int Importance;
        [JsonProperty("kingdoms")] public List<string> Kingdoms = new List<string>();
        [JsonProperty("settlement")] public string SettlementId;
        [JsonProperty("heroes")] public List<string> Heroes = new List<string>();
        [JsonProperty("player")] public bool PlayerInvolved;
        /// <summary>Versión para enseñar al jugador (en español); Text sigue en inglés para los prompts.</summary>
        [JsonProperty("title_es")] public string TitleEs;
        [JsonProperty("text_es")] public string TextEs;

        /// <summary>Idioma en que se escribieron el titular y el texto para enseñar (los de la IA no llevan: van en el idioma de las respuestas).</summary>
        [JsonProperty("lang")] public string Lang;
        /// <summary>El titular y el texto locales valen si siguen estando en el idioma de ahora.</summary>
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

    /// <summary>
    /// Noticias del mundo y rumores. Cada NPC "se entera" de una noticia según su importancia,
    /// si está implicado, si está en el lugar o en el reino afectado, y el tiempo que ha pasado.
    /// </summary>
    public static class NewsStore
    {
        private static List<NewsItem> _items = new List<NewsItem>();
        private const int MaxItems = 400;

        public static IEnumerable<NewsItem> All => _items;

        public static void Clear() => _items = new List<NewsItem>();

        public static void Delete(string id) => _items.RemoveAll(n => n.Id == id);

        public static void Load(List<NewsItem> items) => _items = items ?? new List<NewsItem>();

        public static void Add(NewsItem n)
        {
            if (n == null || string.IsNullOrWhiteSpace(n.Text)) return;
            n.Id = n.Id ?? Guid.NewGuid().ToString("N").Substring(0, 10);
            n.Day = CampaignTime.Now.ToDays;
            // Las noticias que escribe el propio mod (no la IA) llevan su idioma, para no enseñarlas en otro si se cambia
            if (n.Lang == null && n.Type != "rumor" && n.Type != "event" && n.Type != "statement" && n.Type != "verdict") n.Lang = AILegacy.Core.L.Code;
            n.Importance = Math.Max(1, Math.Min(4, n.Importance));
            if (n.Heroes.Contains(Hero.MainHero?.StringId)) n.PlayerInvolved = true;
            _items.Add(n);
            if (_items.Count > MaxItems) _items.RemoveRange(0, _items.Count - MaxItems);
            Core.Log.Info($"Noticia [{n.Type}, imp {n.Importance}]: {n.Text}");
            try { WorldSim.OnRealNews(n); } catch { }
        }

        /// <summary>Noticias que este personaje conoce ahora mismo, las más relevantes primero.</summary>
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
                // más importante, más cercana a él y más reciente = antes
                double score = how * 10 + n.Importance * 3 - age * 0.15;
                scored.Add((n, score));
            }
            return scored.OrderByDescending(x => x.score).Take(max).Select(x => x.item).ToList();
        }

        /// <summary>0 = no se ha enterado; 1..4 cuánto le toca de cerca.</summary>
        private static int Reach(Speaker s, NewsItem n, double age)
        {
            // Implicado directamente (él o su clan)
            if (s.IsHero)
            {
                if (n.Heroes.Contains(s.Hero.StringId)) return 4;
                var clan = s.Hero.Clan;
                if (clan != null && n.Heroes.Any(id => Hero.Find(id)?.Clan == clan)) return 3;
            }

            // En el lugar (o en su ciudad/aldeas vinculadas)
            if (!string.IsNullOrEmpty(n.SettlementId) && SameArea(s.Settlement ?? s.HomeSettlement, n.SettlementId))
                return 3;

            bool inKingdom = s.Faction != null && n.Kingdoms.Contains(s.Faction.StringId);
            bool court = s.IsHero && (s.Hero.IsLord || s.Hero.IsNotable);

            // Las noticias viajan: lo que pasa lejos tarda en llegar, y lo menudo no llega nunca. Un noble se entera de más
            // lejos que un aldeano, y un soberano de más lejos aún (tienen quien les traiga nuevas).
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
                        // Un jinete hace unas 60 unidades de mapa al día; lo gordo corre el doble (relevos, palomas, pregones)
                        if (age < d / (n.Importance >= 4 ? 120.0 : 60.0)) return 0;
                        // Un amigo o un enemigo de los implicados se entera aunque no sea asunto suyo
                        bool personal = s.IsHero && n.Heroes.Any(id => { var o = Hero.Find(id); return o != null && o != s.Hero && Math.Abs(CharacterRelationManager.GetHeroRelation(s.Hero, o)) >= 20; });
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
                    return age >= 7 ? 1 : 0;           // el resto del mundo se entera más tarde
                case 2:
                    if (inKingdom && court) return 2;
                    if (inKingdom && age >= 3) return 1; // al pueblo llano le llega con retraso
                    return 0;
                default:
                    return 0;                          // importancia 1: solo el lugar
            }
        }

        /// <summary>Dónde está quien habla (su asentamiento, su partida o la del jugador si va con él).</summary>
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
            // aldea <-> ciudad/castillo al que pertenece
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
