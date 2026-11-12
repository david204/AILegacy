using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;

namespace AILegacy.World
{
    /// <summary>Un secreto nacido de la historia de ESTA partida: quién lo sabe, de quién trata y cuánto cuesta sonsacarlo.</summary>
    public class StorySecret
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("text")] public string Text;              // inglés, para los prompts
        [JsonProperty("text_local")] public string TextLocal;   // idioma del jugador, para el editor
        [JsonProperty("about")] public List<string> About = new List<string>();        // ids de héroes
        [JsonProperty("known_by")] public List<string> KnownBy = new List<string>();   // ids de héroes
        [JsonProperty("access")] public string Access = "high"; // low | medium | high
        [JsonProperty("day")] public double Day;
        [JsonProperty("source")] public string Source = "origin"; // origin | event
        [JsonProperty("kingdom")] public string KingdomId;
    }

    /// <summary>
    /// Secretos por historia. En vez de repartir a dados unos secretos escritos de antemano, la IA los inventa a partir de
    /// la gente real de cada reino (sus casas, bodas, rencores, deudas, quién odia a quién) y de lo que va pasando. Cada
    /// secreto trata de personas concretas y lo conocen unas pocas, que solo lo sueltan con confianza.
    /// </summary>
    internal static class StorySecrets
    {
        private static readonly Random Rng = new Random();

        private static readonly Tpl TOrigin = new Tpl("world_data/secrets_from_story.txt",
@"You are the keeper of the hidden history of Calradia, the medieval world of Mount & Blade II: Bannerlord. Below are the real people of one realm as they stand today. Invent {count} SECRETS that could well be true of these very people: things hidden that would matter if they came out.

Rules:
- Every secret is about one or two of the NAMED people below and grows out of the data: a marriage with a wide gap in age, a house in debt, two leaders who hate each other, a rich clan under a poor crown, a child who does not resemble the father, an heir passed over, a lord far from home, a rival's sudden rise.
- Kinds of secret: a debt or a bribe, an affair or a child of doubtful blood, a betrayal planned or already sold, a crime covered up, cowardice or failure dressed as glory, a forged claim, a pact with an enemy, a sickness hidden, a vow broken.
- Never contradict the data and never state what only the world itself can decide: nobody is secretly dead, secretly a ruler, secretly of another realm, secretly at war.
- ""known_by"": the few named people who know it (the one it is about almost always knows; add a spouse, a rival who found out, a kinsman). 1 to 4 names, all from the list below.
- ""access"": how hard it is to get it out of someone who knows: low (gossip they enjoy sharing), medium (told to those they trust), high (dangerous: only to someone they would trust with their life).
- ""text"": one or two sentences in English, concrete (names, a sum, a place, a year). ""text_local"": the same in {language}.
- Spread them over different houses; at most one about the ruler.

## The realm: {kingdom}
{data}

Answer ONLY with JSON: { ""secrets"": [ { ""text"": ""..."", ""text_local"": ""..."", ""about"": [""name""], ""known_by"": [""name""], ""access"": ""low|medium|high"" } ] }");

        public static void Touch() { var _ = TOrigin.Key; }

        private static List<StorySecret> All => WorldSim.State.Secrets;

        private static string Traits(Hero h)
        {
            var t = new List<string>();
            void A(TraitObject tr, string plus, string minus) { int v = h.GetTraitLevel(tr); if (v > 0) t.Add(plus); else if (v < 0) t.Add(minus); }
            A(DefaultTraits.Honor, "honourable", "devious"); A(DefaultTraits.Mercy, "merciful", "cruel"); A(DefaultTraits.Valor, "brave", "cautious");
            A(DefaultTraits.Generosity, "generous", "greedy"); A(DefaultTraits.Calculating, "calculating", "impulsive");
            return t.Count == 0 ? "" : " (" + string.Join(", ", t) + ")";
        }

        private static string Describe(Kingdom k)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Ruler: {k.Leader.Name}, {(int)k.Leader.Age}{Traits(k.Leader)}. At war with: {string.Join(", ", WorldSim.Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).Select(o => o.Name.ToString()).DefaultIfEmpty("nobody"))}.");
            foreach (var c in k.Clans.Where(c => c != null && !c.IsEliminated && c.Leader != null && c != Clan.PlayerClan).OrderByDescending(c => c.Tier).Take(9))
            {
                var l = c.Leader;
                string wealth = c.Gold < 15000 ? "short of coin" : c.Gold > 250000 ? "very rich" : "comfortable";
                int rel = l == k.Leader ? 0 : l.GetRelation(k.Leader);
                sb.Append($"- House {c.Name} (rank {c.Tier}, {wealth}, {c.Fiefs.Count} holding(s)){(c == k.RulingClan ? " — the ruling house" : rel <= -20 ? " — on bad terms with the crown" : rel >= 30 ? " — close to the crown" : "")}: ");
                var people = c.Heroes.Where(h => h.IsAlive && !h.IsChild).OrderByDescending(h => h == l).ThenByDescending(h => h.Age).Take(6)
                    .Select(h => $"{h.Name} ({(h.IsFemale ? "woman" : "man")}, {(int)h.Age}{(h == l ? ", head of the house" : "")}{(h.Spouse != null ? $", married to {h.Spouse.Name}" : ", unmarried")}{(h.IsPrisoner ? ", held prisoner" : "")}){Traits(h)}");
                sb.AppendLine(string.Join("; ", people) + ".");
            }
            // Rencores y amistades entre cabezas de casa
            var leaders = k.Clans.Where(c => c != null && !c.IsEliminated && c.Leader != null && c != Clan.PlayerClan).Select(c => c.Leader).ToList();
            var ties = new List<string>();
            for (int i = 0; i < leaders.Count; i++)
                for (int j = i + 1; j < leaders.Count; j++)
                {
                    int r = leaders[i].GetRelation(leaders[j]);
                    if (r <= -25) ties.Add($"{leaders[i].Name} and {leaders[j].Name} hate each other");
                    else if (r >= 40) ties.Add($"{leaders[i].Name} and {leaders[j].Name} are close friends");
                }
            if (ties.Count > 0) sb.AppendLine("Between the heads of houses: " + string.Join("; ", ties.Take(8)) + ".");
            return sb.ToString().TrimEnd();
        }

        private static Hero FindHero(string name, Kingdom prefer)
        {
            string n = WorldSim.Norm(name);
            if (n.Length < 2) return null;
            var pool = Hero.AllAliveHeroes.Where(h => h != null && h != Hero.MainHero && WorldSim.Norm(h.Name.ToString()) == n).ToList();
            if (pool.Count == 0) pool = Hero.AllAliveHeroes.Where(h => h != null && h != Hero.MainHero && WorldSim.Norm(h.Name.ToString()).StartsWith(n)).ToList();
            return pool.FirstOrDefault(h => prefer != null && h.MapFaction == prefer) ?? pool.FirstOrDefault();
        }

        /// <summary>Guarda un secreto que ha devuelto la IA (de origen o nacido de un suceso).</summary>
        public static StorySecret Add(JObject x, Kingdom k, string source)
        {
            try
            {
                var cfg = S.Get;
                string text = WorldSim.Clean((string)x["text"], 400);
                if (text.Length < 15) return null;
                var s = new StorySecret
                {
                    Id = WorldSim.NewId("sec"), Text = text, TextLocal = WorldSim.Clean((string)x["text_local"], 400), Day = CampaignTime.Now.ToDays, Source = source, KingdomId = k?.StringId,
                    Access = new[] { "low", "medium", "high" }.Contains(((string)x["access"] ?? "").Trim().ToLowerInvariant()) ? ((string)x["access"]).Trim().ToLowerInvariant() : "high",
                };
                foreach (var n in (x["about"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>()) { var h = FindHero(n, k); if (h != null && !s.About.Contains(h.StringId)) s.About.Add(h.StringId); }
                foreach (var n in (x["known_by"] as JArray)?.Select(t => t.ToString()) ?? Enumerable.Empty<string>()) { var h = FindHero(n, k); if (h != null && !s.KnownBy.Contains(h.StringId)) s.KnownBy.Add(h.StringId); }
                // De quien trata lo sabe; y a veces, también su cónyuge
                foreach (var id in s.About.ToList())
                {
                    if (!s.KnownBy.Contains(id)) s.KnownBy.Add(id);
                    var sp = Hero.Find(id)?.Spouse;
                    if (sp != null && sp != Hero.MainHero && !s.KnownBy.Contains(sp.StringId) && Rng.Next(100) < 40) s.KnownBy.Add(sp.StringId);
                }
                if (s.KnownBy.Count == 0) return null;      // un secreto que no sabe nadie no sirve
                All.Add(s);
                while (All.Count > cfg.StorySecretsMax) All.RemoveAt(0);
                Log.Info($"[secretos] nuevo ({source}, {s.Access}): {text} — lo saben: {string.Join(", ", s.KnownBy.Select(id => Hero.Find(id)?.Name?.ToString() ?? id))}");
                return s;
            }
            catch (Exception ex) { Log.Error("Guardar secreto", ex); return null; }
        }

        /// <summary>Un reino por día de juego, hasta tenerlos todos: así el gasto se reparte y no hay parón al empezar.</summary>
        public static void OnDailyTick()
        {
            try
            {
                var cfg = S.Get;
                if (!cfg.StorySecrets || Campaign.Current == null || !WorldSim.CanCall) return;
                var k = WorldSim.Kingdoms.FirstOrDefault(x => !WorldSim.State.SecretKingdoms.Contains(x.StringId) && !WorldSim.IsReserved("secrets:" + x.StringId) && x.Clans.Count(c => !c.IsEliminated) >= 2);
                if (k == null) return;
                // El reino se marca como hecho solo cuando llegan sus secretos (si la llamada falla, se reintenta otro día)
                string prompt = TOrigin.Format("count", cfg.StorySecretsPerKingdom, "language", cfg.Language, "kingdom", k.Name, "data", Describe(k));
                WorldSim.Ask($"secretos de {k.Name}", new List<ChatMessage> { new ChatMessage("user", prompt) }, 300 + cfg.StorySecretsPerKingdom * 220, j =>
                {
                    int n = 0;
                    if (j["secrets"] is JArray arr) foreach (var x in arr.OfType<JObject>().Take(cfg.StorySecretsPerKingdom + 1)) if (Add(x, k, "origin") != null) n++;
                    Log.Info($"[secretos] {k.Name}: {n} secreto(s) nacidos de su gente");
                    if (n > 0 && !WorldSim.State.SecretKingdoms.Contains(k.StringId)) { WorldSim.State.SecretKingdoms.Add(k.StringId); Memory.SaveFiles.Flush(); }
                }, key: "secrets:" + k.StringId);
            }
            catch (Exception ex) { Log.Error("Secretos por historia", ex); }
        }

        /// <summary>Lo que este personaje sabe en secreto (para su prompt): lo suyo se marca como propio.</summary>
        public static List<string> For(Speaker s)
        {
            var list = new List<string>();
            try
            {
                if (!S.Get.StorySecrets || !s.IsHero) return list;
                string id = s.Hero.StringId;
                foreach (var x in All.Where(x => x.KnownBy.Contains(id)).OrderByDescending(x => x.Day).Take(S.Get.StorySecretsInPrompt))
                    list.Add($"{x.Text} ({(x.About.Contains(id) ? "this is YOUR OWN secret: you guard it and would deny it" : "you know this about others")}; trust needed to share it: {x.Access})");
            }
            catch { }
            return list;
        }
    }
}
