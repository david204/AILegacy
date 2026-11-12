using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Llm;
using AILegacy.Prompt;
using AILegacy.Settings;
using AILegacy.UI;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.MountAndBlade;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Vida al pasar: cuando caminas junto a alguien en una escena (un guardia, un vecino, un mercader, un noble que no te
    /// conoce), suelta algo de pasada: un saludo, un aviso, un pregón, una queja al de al lado, un comentario sobre ti.
    /// Para no gastar, se pide UN lote de frases por visita (una sola petición) y se van usando según con quién te cruzas.
    /// </summary>
    internal static class PassBy
    {
        private static readonly Tpl TPool = new Tpl("scene_conversations/passing_remarks.txt",
@"A person is walking through {place}, in Calradia, the medieval world of Mount & Blade II: Bannerlord. As they pass, the people there say things in passing: to them, about them, or to each other within their hearing.
Who is walking by, as the people here see them: {walker}
The place right now:
{context}
Write what would be heard, as short loose remarks that need no answer: a guard's curt word or warning, a hawker's cry, a neighbour's grumble, a greeting, a mutter about the stranger, a comment on the day's troubles. Ground some of them in what is going on above (prices, the war, sickness, the lord, the hour, the weather of the season); keep others plain daily life. Vary tone and length; nothing addressed by a personal name; no questions that wait for a reply; no two alike.
Each under {max_chars} characters, in {language}, spoken words only.
Answer ONLY with JSON: { ""guard"": [{n_guard} remarks by guards or soldiers on duty], ""townsfolk"": [{n_folk} remarks by ordinary men and women], ""merchant"": [{n_merchant} remarks by people selling or working a trade], ""noble"": [{n_noble} remarks by people of rank who do not know the walker] }");

        public static void Touch() { var _ = TPool.Key; }

        private static readonly Random Rng = new Random();
        private static readonly Dictionary<string, List<string>> Pool = new Dictionary<string, List<string>>();
        private static readonly HashSet<Agent> Spoke = new HashSet<Agent>();
        private static Mission _mission;
        private static bool _busy, _asked;
        private static int _refills;
        private static float _delay, _cool, _scan;
        private static Agent _looker; private static float _lookLeft;

        private static string Kind(Agent a)
        {
            var c = a.Character as CharacterObject;
            if (c == null) return null;
            if (c.IsHero) return c.HeroObject != null && !c.HeroObject.IsPlayerCompanion && c.HeroObject.Clan != Clan.PlayerClan ? "noble" : null;
            switch (c.Occupation)
            {
                case Occupation.Guard: case Occupation.Soldier: case Occupation.PrisonGuard: case Occupation.Mercenary: case Occupation.CaravanGuard: return "guard";
                case Occupation.Merchant: case Occupation.GoodsTrader: case Occupation.Artisan: case Occupation.ShopWorker: case Occupation.Tavernkeeper: case Occupation.Blacksmith:
                case Occupation.Weaponsmith: case Occupation.Armorer: case Occupation.HorseTrader: case Occupation.TavernWench: case Occupation.RansomBroker: return "merchant";
                case Occupation.Bandit: case Occupation.Gangster: case Occupation.GangLeader: return "townsfolk";
            }
            return c.Age >= 14f ? "townsfolk" : null;
        }

        private static string Walker(Settlement st)
        {
            var p = Hero.MainHero;
            var parts = new List<string>();
            if (st.OwnerClan == Clan.PlayerClan) parts.Add("the lord of this very place, known to all");
            else if (Clan.PlayerClan != null && Clan.PlayerClan.Tier >= 4) parts.Add($"{p.Name}, a noble of renown whom most would recognise");
            else if (Clan.PlayerClan != null && Clan.PlayerClan.Tier >= 2) parts.Add("an armed captain with men of their own, not known by name here");
            else parts.Add("an armed traveller nobody here knows");
            parts.Add(p.IsFemale ? "a woman" : "a man");
            if (st.MapFaction != null && p.MapFaction != null && st.MapFaction != p.MapFaction && st.MapFaction.IsAtWarWith(p.MapFaction)) parts.Add("wearing the colours of a realm at war with this one");
            try { float crime = st.MapFaction?.MainHeroCrimeRating ?? 0f; if (crime >= 30f) parts.Add("with a face that matches the talk of a wanted troublemaker"); } catch { }
            if (p.HitPoints < p.MaxHitPoints * 0.5f) parts.Add("visibly wounded");
            if (Agent.Main != null && !Agent.Main.WieldedWeapon.IsEmpty) parts.Add("with a weapon drawn in hand");
            return string.Join(", ", parts) + ".";
        }

        private static void Request(Settlement st, Mission m)
        {
            var cfg = S.Get;
            if (!Config.LegacyConfig.Current.Providers.Any(p => p.IsUsable)) return;
            var any = m.Agents.FirstOrDefault(a => a != null && a.IsHuman && a.IsActive() && a != Agent.Main && a.Character is CharacterObject co && !co.IsHero);
            Speaker sp = null;
            try { if (any != null) sp = Speaker.FromResident((CharacterObject)any.Character, any); } catch { }
            string prompt = TPool.Format("place", st.Name, "walker", Walker(st), "context", sp != null ? Ambient.Context(st, sp) : st.Name.ToString(),
                "max_chars", cfg.PassByMaxChars, "language", cfg.Language, "n_guard", 5, "n_folk", 9, "n_merchant", 5, "n_noble", 3);
            _busy = true; _asked = true;
            LlmClient.CompleteAsync(new List<ChatMessage> { new ChatMessage("user", prompt) }, wantJson: true, task: "passby", maxTokensOverride: 900, preferredProvider: cfg.ProviderResidents).ContinueWith(t =>
            {
                var res = t.IsFaulted ? null : t.Result;
                MainThread.Post(() =>
                {
                    _busy = false;
                    try
                    {
                        if (res == null || !res.Ok) { Log.Warn("[al pasar] la IA no ha dado las frases: " + res?.Error); return; }
                        if (Mission.Current != m) return;
                        int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                        if (a < 0 || b <= a) return;
                        var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                        int total = 0;
                        foreach (var key in new[] { "guard", "townsfolk", "merchant", "noble" })
                        {
                            if (!(j[key] is JArray arr)) continue;
                            if (!Pool.TryGetValue(key, out var list)) Pool[key] = list = new List<string>();
                            foreach (var x in arr) { string s = (x?.ToString() ?? "").Trim(); if (s.Length > 2 && s.Length <= cfg.PassByMaxChars + 40) { list.Add(s); total++; } }
                        }
                        Log.Info($"[al pasar] {total} frases listas para {st.Name}");
                    }
                    catch (Exception ex) { Log.Error("Frases al pasar", ex); }
                });
            });
        }

        public static void Tick(float dt)
        {
            try
            {
                // Sin partida cargada (menú principal, batalla personalizada) no hay nada que hacer: ni siquiera se puede
                // preguntar por el asentamiento, que falla fuera de una campaña.
                if (Campaign.Current == null)
                {
                    if (_mission != null) { _mission = null; Pool.Clear(); Spoke.Clear(); }
                    return;
                }
                var cfg = S.Get;
                var m = Mission.Current;
                if (!cfg.EnablePassBy || m == null) { if (m == null && _mission != null) { _mission = null; Pool.Clear(); Spoke.Clear(); } return; }
                var st = Settlement.CurrentSettlement;
                if (st == null || Agent.Main == null || !Agent.Main.IsActive() || m.Mode != MissionMode.StartUp)
                {
                    if (m == null && _mission != null) { _mission = null; Pool.Clear(); Spoke.Clear(); }
                    return;
                }
                if (_mission != m)
                {
                    _mission = m; Pool.Clear(); Spoke.Clear(); _asked = false; _busy = false; _refills = 0;
                    _delay = 6f; _cool = 8f; _looker = null;
                }
                if (_looker != null && (_lookLeft -= dt) <= 0f) { try { if (_looker.IsActive()) _looker.SetLookAgent(null); } catch { } _looker = null; }

                if (!_asked) { if ((_delay -= dt) <= 0f && !_busy && Bubbles.Available) Request(st, m); return; }
                if (_busy) return;

                _cool -= dt;
                if (_cool > 0f || (_scan -= dt) > 0f) return;
                _scan = 0.4f;
                if (!Bubbles.Available || Ambient.IsPlaying || ChatSession.IsBusy || Initiative.Busy || Campaign.Current.ConversationManager?.IsConversationInProgress == true) return;
                try { if (InformationManager.IsAnyInquiryActive()) return; } catch { }
                var fh = m.GetMissionBehavior<SandBox.Missions.MissionLogics.MissionFightHandler>();
                if (fh != null && fh.IsThereActiveFight()) return;

                var me = Agent.Main.Position;
                Agent pick = null; string kind = null; float best = cfg.PassByDistance;
                foreach (var a in m.Agents)
                {
                    if (a == null || a == Agent.Main || !a.IsHuman || !a.IsActive() || !a.IsAIControlled || Spoke.Contains(a) || GroupTalk.Has(a) || SceneActions.Busy(a)) continue;
                    float d = a.Position.Distance(me);
                    if (d > best || Math.Abs(a.Position.z - me.z) > 2f) continue;
                    string k = Kind(a);
                    if (k == null || !Pool.TryGetValue(k, out var l) || l.Count == 0) continue;
                    // Un noble que ya te conoce no usa estas frases: tiene las suyas
                    if (k == "noble") { var h = ((CharacterObject)a.Character).HeroObject; if (Memory.MemoryStore.Find(h.StringId)?.ConversationCount > 0) continue; }
                    best = d; pick = a; kind = k;
                }
                if (pick == null)
                {
                    // Si ya se han gastado casi todas y sigues aquí, un lote más (los que se indiquen en MCM)
                    if (_refills < cfg.PassByRefills && Pool.Values.Sum(l => l.Count) <= 3 && Spoke.Count >= 6) { _refills++; Spoke.Clear(); Request(st, m); }
                    return;
                }
                var list = Pool[kind];
                int i = Rng.Next(list.Count);
                string line = list[i];
                list.RemoveAt(i);
                Spoke.Add(pick);
                float secs = Math.Max(2.5f, Math.Min(6f, 1.5f + line.Length * 0.05f));
                Bubbles.Show(pick, line, secs);
                if (cfg.AmbientToLog) InformationManager.DisplayMessage(new InformationMessage($"{pick.Name}: {line}", new Color(0.78f, 0.78f, 0.7f)));
                try { pick.SetLookAgent(Agent.Main); _looker = pick; _lookLeft = secs; } catch { }
                _cool = cfg.PassBySeconds * (0.7f + (float)Rng.NextDouble() * 0.6f);
            }
            catch (Exception ex) { Log.Error("Vida al pasar", ex); _busy = false; _cool = 30f; }
        }
    }
}
