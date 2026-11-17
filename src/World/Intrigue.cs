using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Letters;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Party.PartyComponents;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>A scheme someone is working on against the player or another noble.</summary>
    public class Plot
    {
        [JsonProperty("id")] public string Id;
        [JsonProperty("plotter")] public string PlotterId;
        [JsonProperty("target")] public string TargetId;
        /// <summary>smear (false rumours), murder (an attempt on their life), seduce (their spouse)</summary>
        [JsonProperty("kind")] public string Kind = "smear";
        /// <summary>Why, in English, for prompts.</summary>
        [JsonProperty("motive")] public string Motive = "";
        [JsonProperty("stage")] public int Stage;
        [JsonProperty("next")] public double NextDay;
        [JsonProperty("started")] public double StartDay;
        [JsonProperty("accomplices")] public List<string> Accomplices = new List<string>();
        /// <summary>The player knows of it (warned, told, or found out).</summary>
        [JsonProperty("known")] public bool Known;
        /// <summary>It came out in public.</summary>
        [JsonProperty("exposed")] public bool Exposed;
        /// <summary>The player asked for it.</summary>
        [JsonProperty("instigated")] public bool ByPlayer;
        [JsonProperty("warned")] public bool Warned;
        [JsonProperty("over")] public bool Over;
        [JsonProperty("party", NullValueHandling = NullValueHandling.Ignore)] public string PartyId;
        /// <summary>Days the plotter has spent in a cell since it began.</summary>
        [JsonProperty("jailed", DefaultValueHandling = DefaultValueHandling.Ignore)] public int JailedDays;
        /// <summary>The target (another noble) has learned of it and will answer.</summary>
        [JsonProperty("target_knows", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool TargetKnows;
        /// <summary>It is someone's answer to a plot against them.</summary>
        [JsonProperty("payback", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Payback;
    }

    /// <summary>
    /// Intrigues. Someone with a real grudge and the character for it works on a plan step by step, on their own and without
    /// AI requests except to word a rumour: find accomplices, spread lies with no name on them, court the target's spouse,
    /// or hire a killer. The player can find out (a talkative accomplice, a friend's warning, the rumours themselves) and
    /// answer: confront the plotter, buy an accomplice, denounce it to the plotter's ruler, or plot in turn. An attempt on the
    /// player's life always comes with a warning first and a way out: an ambush that is fought, a poison that can be treated,
    /// or guards who stop the killer.
    /// </summary>
    internal static class Intrigue
    {
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;
        private static WorldState St => WorldSim.State;
        public static bool Enabled => S.Get.EnableIntrigues && WorldSim.Enabled;
        public static IEnumerable<Plot> Active => (St?.Plots ?? new List<Plot>()).Where(p => !p.Over);

        private static readonly Tpl TPlotter = new Tpl("intrigue/plotter.txt",
@"You are secretly plotting against {target}: {kind}. {stage} Why: {motive}.{accomplices}
Keep it hidden unless you choose otherwise. If they confront you or ask about it, deny it, bargain, threaten or confess, as fits you and what they seem to know. If you truly give it up, use abandon_plot.");
        private static readonly Tpl TAccomplice = new Tpl("intrigue/accomplice.txt",
@"You are part of {plotter}'s secret plot against {target}: {kind}. You are not the one behind it. If the price is right, the danger too great or your conscience too heavy, you may tell all about it to the one in front of you with betray_plot.");
        private static readonly Tpl TKnown = new Tpl("intrigue/they_know.txt",
            "{player} has learned, or strongly suspects, that you are plotting against {target}.");
        private static readonly Tpl TRuler = new Tpl("intrigue/ruler.txt",
@"{player} may come to you about {plotter}, who serves your realm and has been plotting against {target}: {kind}. Weigh what they show you and your opinion of each. If you believe it and will act, use condemn_plot:{plotter}.");
        private static readonly Tpl TVictim = new Tpl("intrigue/victim.txt",
@"{player} knows that {plotter} is secretly plotting against you: {kind}. You do not know it yet. If {player} tells you and you believe them, use believe_plot:{plotter}; you will then answer it in your own way.");
        private static readonly Tpl TKinds = new Tpl("intrigue/kinds.txt",
@"smear: spreading lies about them with no name on the rumours, to turn people against them
murder: having them killed by hired blades or poison
seduce: winning over their spouse behind their back");
        private static readonly Tpl TStages = new Tpl("intrigue/stages.txt",
@"smear0: You are still sounding out who would help you.
smear1: The first lies are already going round.
smear2: The lies have spread far; a last push will finish it.
murder0: You are looking for someone you can trust with it.
murder1: The killer is hired and paid; the attempt is days away.
murder2: The attempt is about to be made.
seduce0: You have begun to seek out their spouse's company.
seduce1: Their spouse is warming to you.
seduce2: You and their spouse are close to an affair.");
        private static readonly Tpl TRumour = new Tpl("intrigue/rumour.txt",
@"In Calradia, the medieval world of Mount & Blade II: Bannerlord, {plotter} is secretly spreading lies about {target} ({target_desc}) to ruin their name. {plotter}'s motive: {motive}.
Write ONE false rumour that people are now repeating about {target}: believable for this world, damaging, with no name of who started it. Not about magic, not about anything modern. Do not say it is false.
Answer ONLY with JSON: { ""summary"": ""the rumour in one English sentence"", ""title"": ""3 to 6 words in {language}"", ""text"": ""the rumour as people tell it, 1 or 2 sentences in {language}"" }");

        public static void Touch() { var _ = TPlotter.Key + TAccomplice.Key + TKnown.Key + TRuler.Key + TVictim.Key + TKinds.Key + TStages.Key + TRumour.Key; }

        private static string Line(Tpl t, string key) => Diplomacy.Line(t, key);

        // ============================================================ daily

        public static void OnDailyTick()
        {
            try
            {
                if (!Enabled || Campaign.Current == null || St == null || P == null) return;
                double now = CampaignTime.Now.ToDays;
                foreach (var p in Active.ToList())
                {
                    var plotter = Hero.Find(p.PlotterId); var target = Hero.Find(p.TargetId);
                    if (plotter == null || target == null || !plotter.IsAlive || !target.IsAlive) { End(p, "dead"); continue; }
                    // another noble's plot whose target has since become someone the player knows, a ruler or family is dropped
                    if (!p.ByPlayer && target != P && !NpcTargetAllowed(target)) { End(p, $"{target.Name} is no longer a target for another noble's plot"); continue; }
                    if (plotter.IsPrisoner)
                    {
                        p.NextDay = Math.Max(p.NextDay, now + 1);
                        if (++p.JailedDays >= Math.Max(1, S.Get.IntriguePrisonDays)) GiveUp(p, plotter, target, "prison");
                        continue;
                    }
                    string why = ReasonToStop(p, plotter, target);
                    if (why != null) { GiveUp(p, plotter, target, why); continue; }
                    if (p.TargetKnows && target != P) { TargetAnswers(p, plotter, target); continue; }
                    if (now >= p.NextDay) Advance(p, plotter, target, now);
                }
                St.Plots.RemoveAll(p => p.Over && now - p.NextDay > 120);
                var cfg = S.Get;
                if (now >= St.NextPlotDay && Active.Count() < cfg.IntrigueMaxActive)
                {
                    St.NextPlotDay = now + cfg.IntrigueDaysBetween * (0.7 + Rng.NextDouble() * 0.6);
                    TryStart(now);
                }
            }
            catch (Exception ex) { Log.Error("[plot] day", ex); }
        }

        private static int Trait(Hero h, TraitObject t) { try { return h.GetTraitLevel(t); } catch { return 0; } }

        /// <summary>The kind of person who would scheme: little honour, or a calculating mind.</summary>
        private static bool Schemer(Hero h) => Trait(h, DefaultTraits.Honor) < 0 || (Trait(h, DefaultTraits.Calculating) > 0 && Trait(h, DefaultTraits.Honor) <= 0);

        private static bool CanPlot(Hero h) =>
            h != null && h != P && h.IsAlive && !h.IsChild && h.IsLord && !h.IsPrisoner && h.Clan != null && h.Clan != Clan.PlayerClan
            && h.PartyBelongedTo != MobileParty.MainParty && !Active.Any(p => p.PlotterId == h.StringId);

        private static void TryStart(double now)
        {
            var cfg = S.Get;
            var options = new List<(Hero plotter, Hero target, string why, double w)>();
            // One list of nobles for both loops: scanning every living hero twice made this quadratic
            var lords = Hero.AllAliveHeroes.Where(o => o != null && o != P && o.IsLord && !o.IsChild).ToList();
            // Who another noble may plot against: only nobles the player has no story with (never talked to), never a
            // ruler, never the player's clan or family. Worked out once, not for every plotter.
            var npcTargets = cfg.IntrigueAgainstNpcs ? lords.Where(NpcTargetAllowed).ToList() : new List<Hero>();
            foreach (var h in lords)
            {
                if (!CanPlot(h) || !Schemer(h)) continue;
                // Against the player: a real grudge
                var mem = MemoryStore.Find(h.StringId);
                int rel = (int)h.GetRelationWithPlayer();
                string why = rel <= -40 ? "you hate them" : mem != null && mem.LiesSuspected > 0 && rel <= -15 ? "they lied to you and made a fool of you"
                           : mem != null && Bonds.Tension(mem) >= 60 && rel <= -10 ? "they humiliated and threatened you" : null;
                if (why != null && !Active.Any(p => p.TargetId == P.StringId && p.PlotterId == h.StringId))
                    options.Add((h, P, why, 2.0 + (-rel) / 40.0));
                // Against another noble they loathe
                if (!cfg.IntrigueAgainstNpcs) continue;
                try
                {
                    Hero foe = null; int worst = -60;
                    foreach (var o in npcTargets)
                    {
                        if (o == h || o.Clan == h.Clan) continue;
                        int r = h.GetRelation(o);
                        if (r <= worst) { worst = r; foe = o; }
                    }
                    if (foe != null && !Active.Any(p => p.TargetId == foe.StringId)) options.Add((h, foe, "you loathe them", 1.0));
                }
                catch { }
            }
            if (options.Count == 0) return;
            double total = options.Sum(o => o.w), roll = Rng.NextDouble() * total;
            foreach (var o in options)
            {
                if ((roll -= o.w) > 0) continue;
                Begin(o.plotter, o.target, Pick(o.plotter, o.target), o.why, false, now);
                return;
            }
        }

        /// <summary>
        /// Whether a noble may be the target of another noble's plot (the player's own plots are not held to this):
        /// never a ruler, never the player's clan or family, and never someone the player has talked with, so the
        /// characters the player knows are not killed or ruined behind his back.
        /// </summary>
        public static bool NpcTargetAllowed(Hero o)
        {
            try
            {
                if (o == null || P == null || o == P || !o.IsAlive || o.IsChild) return false;
                if (o.Clan == Clan.PlayerClan || o.IsKingdomLeader || (o.MapFaction is Kingdom k && k.Leader == o)) return false;
                if (o.Clan != null && o.Clan.Kingdom != null && o.Clan.Kingdom.RulingClan == o.Clan && o.Clan.Leader == o) return false;
                if (PlayerFamily(o)) return false;
                var mem = MemoryStore.Find(o.StringId);
                if (mem != null && (mem.ConversationCount > 0 || mem.LastConversationDay >= 0 || (mem.Turns != null && mem.Turns.Count > 0)
                                    || !string.IsNullOrWhiteSpace(mem.Summary))) return false;
                return true;
            }
            catch { return false; }
        }

        private static bool PlayerFamily(Hero o)
        {
            var p = P;
            if (o == p.Spouse || o == p.Father || o == p.Mother || o.Father == p || o.Mother == p) return true;
            if (p.ExSpouses != null && p.ExSpouses.Contains(o)) return true;
            if (p.Siblings != null && p.Siblings.Contains(o)) return true;
            // grandchildren and the families of the player's children
            if (o.Father != null && (o.Father.Father == p || o.Father.Mother == p)) return true;
            if (o.Mother != null && (o.Mother.Father == p || o.Mother.Mother == p)) return true;
            if (o.Spouse != null && (o.Spouse.Father == p || o.Spouse.Mother == p)) return true;
            return false;
        }

        private static string Pick(Hero plotter, Hero target)
        {
            var cfg = S.Get;
            int rel = target == P ? (int)plotter.GetRelationWithPlayer() : plotter.GetRelation(target);
            bool cruel = Trait(plotter, DefaultTraits.Mercy) < 0 && Trait(plotter, DefaultTraits.Honor) < 0;
            if (cfg.IntrigueMurder && cruel && rel <= -60 && Rng.Next(100) < 40) return "murder";
            var spouse = target.Spouse;
            if (cfg.IntrigueSeduction && spouse != null && spouse.IsAlive && spouse.IsFemale != plotter.IsFemale && plotter.Spouse == null && Rng.Next(100) < 30) return "seduce";
            return "smear";
        }

        private static double StageDays() => Math.Max(2, S.Get.IntrigueStageDays) * (0.7 + Rng.NextDouble() * 0.6);

        public static Plot Begin(Hero plotter, Hero target, string kind, string why, bool byPlayer, double now)
        {
            var p = new Plot
            {
                Id = WorldSim.NewId("plot"), PlotterId = plotter.StringId, TargetId = target.StringId, Kind = kind, Motive = why ?? "",
                StartDay = now, NextDay = now + StageDays(), ByPlayer = byPlayer, Known = byPlayer,
            };
            // Accomplices: their own people, who think ill of the target too
            try
            {
                foreach (var c in InfoExchange.Confidants(plotter, 12))
                {
                    if (p.Accomplices.Count >= 2) break;
                    if (c == target || c == P || c.Clan == Clan.PlayerClan || !c.IsAlive || c.IsChild) continue;
                    int toTarget = target == P ? (int)c.GetRelationWithPlayer() : c.GetRelation(target);
                    if (toTarget <= -10 && c.GetRelation(plotter) >= 10) p.Accomplices.Add(c.StringId);
                }
            }
            catch { }
            St.Plots.Add(p);
            Log.Info($"[plot] {plotter.Name} starts plotting against {target.Name} ({kind}; {why}; accomplices: {p.Accomplices.Count})");
            SaveFiles.Dirty();
            return p;
        }

        private static void Advance(Plot p, Hero plotter, Hero target, double now)
        {
            p.NextDay = now + StageDays();
            Leak(p, plotter, target);
            if (p.Over) return;
            switch (p.Kind)
            {
                case "murder": AdvanceMurder(p, plotter, target, now); break;
                case "seduce": AdvanceSeduce(p, plotter, target); break;
                default: AdvanceSmear(p, plotter, target); break;
            }
            if (!p.Over && p.ByPlayer) WorldSim.Say(L.F("AIL_e7b1c7e0", $"Tu conjura con {plotter.Name} contra {target.Name} avanza."), Colors.Gray);
            SaveFiles.Dirty();
        }

        // ---- a talkative accomplice

        private static void Leak(Plot p, Hero plotter, Hero target)
        {
            foreach (var id in p.Accomplices.ToList())
            {
                var a = Hero.Find(id);
                if (a == null || !a.IsAlive) { p.Accomplices.Remove(id); continue; }
                int chance = 6 + (Trait(a, DefaultTraits.Honor) > 0 ? 10 : 0);
                if (Rng.Next(100) >= chance) continue;
                if (target == P && !p.Known && !p.ByPlayer) { Warn(p, plotter, a); return; }
                if (target != P && !p.Exposed)
                {
                    int r = Rng.Next(100);
                    if (r < 40) { Expose(p, plotter, target); return; }
                    if (r < 65 && !p.ByPlayer) { p.TargetKnows = true; Log.Info($"[plot] {a.Name} lets {target.Name} know of {plotter.Name}'s plot"); return; }
                    if (!p.Known && !p.ByPlayer)
                    {
                        p.Known = true;
                        WorldSim.Say(L.F("AIL_e7b1df10", $"Te llega un rumor: {plotter.Name} trama contra {target.Name} ({KindWordOtherEs(p.Kind)}). {target.Name} no lo sabe."), Colors.Yellow);
                        Log.Info($"[plot] the player hears of {plotter.Name}'s plot against {target.Name}");
                        return;
                    }
                }
            }
        }

        /// <summary>The player learns of it: a friend's letter if they have one who would know, otherwise a feeling on the road.</summary>
        private static void Warn(Plot p, Hero plotter, Hero source = null)
        {
            p.Known = true;
            string what = KindWordEs(p.Kind);
            Hero friend = source != null && source.GetRelationWithPlayer() >= 0 ? source : null;
            try
            {
                if (friend == null)
                    friend = Hero.AllAliveHeroes.Where(h => h != P && h != plotter && h.IsLord && !h.IsChild && h.GetRelationWithPlayer() >= 20
                                                            && (h.Clan == plotter.Clan || h.MapFaction == plotter.MapFaction || plotter.GetRelation(h) >= 10))
                                                .OrderByDescending(h => h.GetRelationWithPlayer()).FirstOrDefault();
            }
            catch { }
            if (friend != null)
            {
                string text = L.F("AIL_e7b1c7e1", $"Un aviso, y que no salga de nosotros: {plotter.Name} trama algo contra ti: {what}. Ten cuidado con lo que comes y por dónde cabalgas.");
                if (!LetterNotice.Add(friend, text)) LetterSystem.Open(friend, text);
                Log.Info($"[plot] {friend.Name} warns the player of {plotter.Name}'s plot");
            }
            else WorldSim.Say(L.T("AIL_e7b1c7e2", "Tienes la sensación de que alguien sigue tus pasos y pregunta por ti. Mejor andarse con cuidado estos días."), Colors.Yellow);
        }

        private static void Expose(Plot p, Hero plotter, Hero target)
        {
            p.Exposed = true; p.Known = true;
            try { ChangeRelationAction.ApplyRelationChangeBetweenHeroes(plotter, target, -20, false); } catch { }
            try { var ruler = (plotter.MapFaction as Kingdom)?.Leader; if (ruler != null && ruler != plotter) ChangeRelationAction.ApplyRelationChangeBetweenHeroes(plotter, ruler, -10, false); } catch { }
            News(L.F("AIL_e7b1c7e3", $"Se descubre una conjura de {plotter.Name}"), L.F("AIL_e7b1c7e4", $"{plotter.Name} tramaba contra {target.Name}, y ha salido a la luz."),
                 $"{plotter.Name} was found to be plotting against {target.Name} ({p.Kind}).", 2, plotter, target);
            End(p, "exposed");
        }

        // ---- smear

        private static void AdvanceSmear(Plot p, Hero plotter, Hero target)
        {
            if (p.Stage == 0) { p.Stage = 1; Rumour(p, plotter, target); return; }
            if (p.Stage == 1) { p.Stage = 2; Rumour(p, plotter, target); return; }
            // The last push: their name is worse off in the plotter's realm, and it is done
            HurtName(plotter, target, 3, 4);
            if (target == P && !p.Known) WorldSim.Say(L.F("AIL_e7b1c7e5", $"Se habla mal de ti en la corte de {plotter.MapFaction?.Name}: alguien está esparciendo mentiras."), Colors.Yellow);
            if (p.ByPlayer) WorldSim.Say(L.F("AIL_e7b1c7e6", $"Las mentiras sobre {target.Name} han hecho su trabajo."), Colors.Green);
            End(p, "done");
        }

        /// <summary>A few nobles of the plotter's realm think a little worse of the target.</summary>
        private static void HurtName(Hero plotter, Hero target, int people, int amount)
        {
            try
            {
                var pool = Hero.AllAliveHeroes.Where(h => h != target && h != plotter && h != P && h.IsLord && !h.IsChild && h.MapFaction != null && h.MapFaction == plotter.MapFaction)
                                              .OrderBy(_ => Rng.Next()).Take(people).ToList();
                foreach (var h in pool)
                {
                    if (target == P) ChangeRelationAction.ApplyPlayerRelation(h, -amount, false, false);
                    else ChangeRelationAction.ApplyRelationChangeBetweenHeroes(h, target, -amount, false);
                }
            }
            catch (Exception ex) { Log.Error("[plot] reputation damage", ex); }
        }

        /// <summary>The one AI request of a smear: the lie itself. It goes round as a rumour with no author.</summary>
        private static void Rumour(Plot p, Hero plotter, Hero target)
        {
            if (!WorldSim.CanCall) { HurtName(plotter, target, 2, 2); return; }
            var cfg = S.Get;
            string desc = $"{(target.IsFemale ? "woman" : "man")}, {(int)target.Age}, {target.Culture?.Name}" + (target.Clan != null ? $", of clan {target.Clan.Name}" : "")
                          + (target == P ? ", a warrior who travels with their own company" : "");
            string prompt = TRumour.Format("plotter", plotter.Name.ToString(), "target", target.Name.ToString(), "target_desc", desc, "motive", p.Motive, "language", cfg.Language);
            int session = WorldSim.Session;
            string pid = p.Id;
            Llm.LlmClient.CompleteAsync(new List<Llm.ChatMessage> { new Llm.ChatMessage("user", prompt) }, wantJson: true, task: "world", maxTokensOverride: 300, preferredProvider: cfg.ProviderWorld)
                .ContinueWith(t =>
                {
                    var res = t.IsFaulted ? null : t.Result;
                    MainThread.Post(() =>
                    {
                        try
                        {
                            if (session != WorldSim.Session) return;
                            var plot = St.Plots.FirstOrDefault(x => x.Id == pid);
                            if (plot == null || plot.Over) return;
                            HurtName(plotter, target, 2, 2);
                            if (res == null || !res.Ok) return;
                            int a = res.Text.IndexOf('{'), b = res.Text.LastIndexOf('}');
                            if (a < 0 || b <= a) return;
                            var j = JObject.Parse(res.Text.Substring(a, b - a + 1));
                            string sum = WorldSim.Clean((string)j["summary"], 300), title = WorldSim.Clean((string)j["title"], 80), text = WorldSim.Clean((string)j["text"], 400);
                            if (sum.Length < 10) return;
                            NewsStore.Add(new NewsItem
                            {
                                Type = "rumor", Importance = 2, Text = $"A rumour about {target.Name}: {sum}",
                                TitleEs = title.Length > 2 ? title : L.F("AIL_e7b1c7e7", $"Lo que se dice de {target.Name}"), TextEs = text,
                                Kingdoms = plotter.MapFaction != null ? new List<string> { plotter.MapFaction.StringId } : new List<string>(),
                                Heroes = new List<string> { target.StringId },
                            });
                            // Those who repeat it to each other now carry it as something they heard
                            foreach (var h in InfoExchange.Confidants(plotter, 6).Where(h => h != target))
                                InfoExchange.AddHeard(MemoryStore.Get(h.StringId, h.Name.ToString()), new HeardItem
                                {
                                    FromName = "talk at court", Text = sum, Secrecy = "public", Day = CampaignTime.Now.ToDays,
                                    About = target == P ? null : target.Name.ToString(), Confidence = 45, IsDistorted = true, Importance = 2,
                                });
                            Log.Info($"[plot] rumour against {target.Name}: {sum}");
                        }
                        catch (Exception ex) { Log.Error("[plot] rumour", ex); }
                    });
                });
        }

        // ---- murder

        private static void AdvanceMurder(Plot p, Hero plotter, Hero target, double now)
        {
            if (p.Stage == 0) { p.Stage = 1; return; }
            if (p.Stage == 1)
            {
                // Always a warning before an attempt on the player, so there is time to act
                p.Stage = 2;
                p.NextDay = now + 2 + Rng.NextDouble() * 2;
                if (target == P && !p.Warned) { p.Warned = true; if (!p.Known) Warn(p, plotter); }
                return;
            }
            if (target == P) AttemptOnPlayer(p, plotter);
            else AttemptOnNpc(p, plotter, target);
        }

        private static void AttemptOnPlayer(Plot p, Hero plotter)
        {
            var main = MobileParty.MainParty;
            var st = main?.CurrentSettlement;
            // In a town or castle of your own clan, your guard is waiting
            if (st != null && st.OwnerClan == Clan.PlayerClan)
            {
                bool talks = Rng.Next(100) < 50;
                WorldSim.Say(L.F("AIL_e7b1c7e8", $"Tu guardia detiene a un asesino a sueldo en {st.Name}.") + (talks ? " " + L.F("AIL_e7b1c7e9", $"Antes de morir confiesa que lo envió {plotter.Name}.") : ""), Colors.Green);
                if (talks) { p.Known = true; Expose(p, plotter, P); } else End(p, "foiled");
                return;
            }
            // Inside a place that is not yours: poison in the food, which a physician can treat
            if (st != null)
            {
                WorldSim.Say(L.T("AIL_e7b1c7ea", "La comida de anoche tenía un sabor extraño. No te encuentras bien."), Colors.Red);
                Diseases.InfectHero(P, L.T("AIL_e7b1c7eb", "veneno lento"), "a slow poison", 3, false, null, CampaignTime.Now.ToDays + 12);
                End(p, "poison");
                return;
            }
            // On the road: hired blades bar the way, and it is fought
            var party = SpawnKillers(plotter);
            if (party == null) { p.NextDay = CampaignTime.Now.ToDays + 2; return; }
            p.PartyId = party.StringId;
            WorldSim.Say(L.T("AIL_e7b1c7ec", "Unos hombres armados os salen al paso. No son bandidos corrientes: vienen a por ti."), Colors.Red);
            End(p, "ambush");
        }

        private static MobileParty SpawnKillers(Hero plotter)
        {
            try
            {
                var main = MobileParty.MainParty;
                var clan = Clan.BanditFactions.FirstOrDefault(c => c.StringId == "looters") ?? Clan.BanditFactions.FirstOrDefault();
                var here = main.GetPosition2D;
                var near = Settlement.All.Where(s => s.IsVillage || s.IsTown).OrderBy(s => s.GetPosition2D.Distance(here)).FirstOrDefault();
                if (clan == null || near == null) return null;
                var party = BanditPartyComponent.CreateLooterParty("ailegacy_killers_" + (int)CampaignTime.Now.ToHours, clan, near, false, clan.DefaultPartyTemplate, main.Position);
                if (party == null) return null;
                int mine = Math.Max(5, main.MemberRoster.TotalHealthyCount);
                int want = Math.Max(8, Math.Min(80, (int)(mine * 0.7)));
                var troop = clan.Culture?.BanditBandit ?? clan.Culture?.BasicTroop;
                int have = party.MemberRoster.TotalManCount;
                if (troop != null && have < want) party.MemberRoster.AddToCounts(troop, want - have);
                party.Party.SetCustomName(new TextObject("{=!}" + L.T("AIL_e7b1c7ed", "Asesinos a sueldo")));
                party.SetMoveEngageParty(main, MobileParty.NavigationType.Default);
                Log.Info($"[plot] assassins of {plotter.Name} next to the player: {party.MemberRoster.TotalManCount} men");
                return party;
            }
            catch (Exception ex) { Log.Error("[plot] assassins", ex); return null; }
        }

        private static void AttemptOnNpc(Plot p, Hero plotter, Hero target)
        {
            // Not while they are held, fighting or on a throne: the killers wait for a better moment (and give up after a while)
            bool unreachable = target.IsPrisoner || target.PartyBelongedTo?.MapEvent != null || (target.MapFaction is Kingdom tk && tk.Leader == target) || target.Clan == Clan.PlayerClan;
            if (unreachable)
            {
                if (CampaignTime.Now.ToDays - p.StartDay > 90) { End(p, "no chance"); return; }
                p.NextDay = CampaignTime.Now.ToDays + 3;
                return;
            }
            bool guarded = target.CurrentSettlement != null && target.CurrentSettlement.OwnerClan == target.Clan;
            int chance = guarded ? 15 : 30;
            bool blamed = Rng.Next(100) < 45;
            if (Rng.Next(100) < chance)
            {
                try { KillCharacterAction.ApplyByMurder(target, null, false); }
                catch (Exception ex) { Log.Error("[plot] assassination", ex); End(p, "failed"); return; }
                News(L.F("AIL_e7b1c7ee", $"Muerte de {target.Name}"),
                     L.F("AIL_e7b1c7ef", $"{target.Name} ha aparecido muerto. Nadie sabe quién lo hizo.") + (blamed ? " " + L.F("AIL_e7b1c7f0", $"Se murmura que detrás está {plotter.Name}.") : ""),
                     $"{target.Name} was murdered.{(blamed ? $" People whisper that {plotter.Name} was behind it." : "")}", 3, plotter, target);
                if (p.ByPlayer) WorldSim.Say(L.F("AIL_e7b1c7f1", $"{target.Name} ha muerto, como tramasteis {plotter.Name} y tú."), Colors.Red);
                End(p, "done");
                return;
            }
            News(L.F("AIL_e7b1c7f2", $"Intentan matar a {target.Name}"),
                 L.F("AIL_e7b1c7f3", $"{target.Name} ha escapado de un intento de asesinato.") + (blamed ? " " + L.F("AIL_e7b1c7f4", $"Las sospechas apuntan a {plotter.Name}.") : ""),
                 $"{target.Name} survived an attempt on their life.{(blamed ? $" Suspicion falls on {plotter.Name}." : "")}", 2, plotter, target);
            if (blamed) { try { ChangeRelationAction.ApplyRelationChangeBetweenHeroes(plotter, target, -30, false); } catch { } }
            End(p, "failed");
        }

        // ---- seduce

        private static void AdvanceSeduce(Plot p, Hero plotter, Hero target)
        {
            var spouse = target.Spouse;
            if (spouse == null || !spouse.IsAlive) { End(p, "no spouse"); return; }
            try
            {
                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(plotter, spouse, p.Stage == 0 ? 5 : 8, false);
                if (target == P) ChangeRelationAction.ApplyPlayerRelation(spouse, -3, false, false);
                else ChangeRelationAction.ApplyRelationChangeBetweenHeroes(spouse, target, -3, false);
            }
            catch { }
            if (p.Stage < 2) { p.Stage++; return; }
            News(L.F("AIL_e7b1c7f5", $"Habladurías sobre {spouse.Name}"), L.F("AIL_e7b1c7f6", $"Se dice que {plotter.Name} y {spouse.Name} se ven a escondidas."),
                 $"People say {plotter.Name} and {spouse.Name}, the spouse of {target.Name}, meet in secret.", 2, plotter, target);
            End(p, "done");
        }

        // ============================================================ giving it up, and the victim's answer

        /// <summary>
        /// Why the plotter would drop it on their own, or null: they made peace with the target, they now share a house, or
        /// a seducer got married. The player's own commissions only stop when the player or the plotter calls them off.
        /// </summary>
        private static string ReasonToStop(Plot p, Hero plotter, Hero target)
        {
            if (p.ByPlayer) return null;
            try
            {
                int rel = target == P ? (int)plotter.GetRelationWithPlayer() : plotter.GetRelation(target);
                if (rel >= (target == P ? 0 : -20)) return "reconciled";
                if (plotter.Clan != null && plotter.Clan == target.Clan) return "same house";
                if (p.Kind == "seduce" && plotter.Spouse != null && plotter.Spouse.IsAlive) return "married";
            }
            catch { }
            return null;
        }

        private static void GiveUp(Plot p, Hero plotter, Hero target, string why)
        {
            End(p, "gave up: " + why);
            string tname = target == P ? "you" : target.Name.ToString();
            Note(plotter, why == "prison" ? $"[Done: your days in a cell made you give up your plot against {tname}.]"
                         : why == "reconciled" ? $"[Done: you gave up your plot against {tname}: things are better between you now.]"
                         : $"[Done: you gave up your plot against {tname}: you no longer need it.]");
            if (p.ByPlayer) WorldSim.Say(L.F("AIL_e7b1df11", $"Tu conjura con {plotter.Name} contra {target.Name} se deshace: lleva días preso."), Colors.Gray);
            else if (target == P && p.Known) WorldSim.Say(L.F("AIL_e7b1df12", $"{plotter.Name} ha dejado de tramar contra ti."), Colors.Green);
        }

        private static void Note(Hero h, string text)
        {
            try { MemoryStore.Get(h.StringId, h.Name.ToString()).Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays }); } catch { }
        }

        /// <summary>A plot against the one in front of the player, which the player knows and they do not.</summary>
        public static Plot AgainstUnaware(Hero target, string plotterName = null)
        {
            if (target == null || target == P) return null;
            string n = WorldSim.Norm(plotterName ?? "");
            return Active.FirstOrDefault(p => p.Known && !p.TargetKnows && !p.ByPlayer && p.TargetId == target.StringId && Hero.Find(p.PlotterId) is Hero pl
                                              && (n.Length == 0 || WorldSim.Norm(pl.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(pl.Name.ToString()))));
        }

        /// <summary>The player warned them and they believe it: grateful, and they will answer it from tomorrow.</summary>
        public static string Believe(Hero target, string plotterName)
        {
            var p = AgainstUnaware(target, plotterName);
            if (p == null) return null;
            var plotter = Hero.Find(p.PlotterId);
            if (plotter == null) return null;
            p.TargetKnows = true;
            try { ChangeRelationAction.ApplyPlayerRelation(target, 5, false, true); } catch { }
            Note(target, $"[Done: {P.Name} warned you that {plotter.Name} is plotting against you, and you believe it.]");
            Log.Info($"[plot] {target.Name} believes the player's warning about {plotter.Name}");
            SaveFiles.Dirty();
            return L.F("AIL_e7b1df13", $"{target.Name} te cree: ya sabe que {plotter.Name} trama a sus espaldas.");
        }

        /// <summary>
        /// A noble who has learned of a plot against them answers it: to their ruler if the plotter serves the same crown and
        /// the ruler thinks better of them than of the plotter; otherwise they strike back with a scheme of their own (when
        /// the plotter may be a target), or confront them openly. Either way the original plot is over: they are on guard.
        /// </summary>
        private static void TargetAnswers(Plot p, Hero plotter, Hero target)
        {
            try
            {
                var ruler = (target.MapFaction as Kingdom)?.Leader;
                if (ruler != null && ruler != plotter && ruler != target && ruler != P && plotter.MapFaction == target.MapFaction
                    && ruler.GetRelation(target) >= ruler.GetRelation(plotter))
                {
                    int fine = Math.Min(5000, Math.Max(0, plotter.Gold / 2));
                    ChangeRelationAction.ApplyRelationChangeBetweenHeroes(ruler, plotter, -20, false);
                    if (fine > 0) GiveGoldAction.ApplyBetweenCharacters(plotter, ruler, fine, true);
                    ChangeRelationAction.ApplyRelationChangeBetweenHeroes(plotter, target, -20, false);
                    p.Exposed = true;
                    News(L.F("AIL_e7b1c7fd", $"{ruler.Name} condena a {plotter.Name}"),
                         L.F("AIL_e7b1df14", $"{target.Name} denuncia ante {ruler.Name} que {plotter.Name} conspiraba a sus espaldas. {plotter.Name} pierde el favor de su señor y paga {fine} denarios."),
                         $"{target.Name} denounced {plotter.Name} to {ruler.Name} for plotting against them; {ruler.Name} condemned {plotter.Name}.", 2, plotter, target);
                    Note(target, $"[Done: you denounced {plotter.Name}'s plot against you to {ruler.Name}, who condemned them.]");
                    End(p, "denounced by the target");
                    return;
                }
                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(plotter, target, -15, false);
                bool canPayBack = S.Get.IntrigueAgainstNpcs && CanPlot(target) && NpcTargetAllowed(plotter) && !Active.Any(x => x.TargetId == plotter.StringId)
                                  && (Schemer(target) || Trait(target, DefaultTraits.Mercy) < 0);
                End(p, "the target found out");
                if (canPayBack)
                {
                    var back = Begin(target, plotter, Pick(target, plotter), "they plotted against you, and you are paying them back", false, CampaignTime.Now.ToDays);
                    back.Payback = true;
                    Note(target, $"[Done: you learned that {plotter.Name} plotted against you, and you set out to pay them back in kind.]");
                    Log.Info($"[plot] {target.Name} strikes back at {plotter.Name}");
                }
                else
                {
                    News(L.F("AIL_e7b1df15", $"{target.Name} se enfrenta a {plotter.Name}"),
                         L.F("AIL_e7b1df16", $"{target.Name} ha descubierto la conjura de {plotter.Name} y se lo ha echado en cara delante de todos."),
                         $"{target.Name} found out that {plotter.Name} was plotting against them and confronted them openly.", 2, plotter, target);
                    Note(target, $"[Done: you found out that {plotter.Name} was plotting against you and confronted them openly.]");
                }
            }
            catch (Exception ex) { Log.Error("[plot] the target's answer", ex); End(p, "the target found out"); }
        }

        // ============================================================ ends and news

        private static void End(Plot p, string why)
        {
            p.Over = true;
            Log.Info($"[plot] {p.Id} ends: {why}");
            SaveFiles.Dirty();
        }

        private static void News(string title, string text, string en, int importance, Hero plotter, Hero target)
        {
            try
            {
                var ks = new List<string>();
                if (plotter?.MapFaction != null) ks.Add(plotter.MapFaction.StringId);
                if (target?.MapFaction != null && !ks.Contains(target.MapFaction.StringId)) ks.Add(target.MapFaction.StringId);
                NewsStore.Add(new NewsItem
                {
                    Type = "news", Importance = importance, Text = en, TitleEs = title, TextEs = text, Kingdoms = ks,
                    Heroes = new[] { plotter, target }.Where(h => h != null).Select(h => h.StringId).ToList(),
                });
                if (importance >= S.Get.WorldEventNotifyImportance || target == P) WorldSim.Say(text, target == P ? Colors.Red : Colors.Cyan);
            }
            catch (Exception ex) { Log.Error("[plot] news", ex); }
        }

        public static string KindWordEs(string kind) =>
            kind == "murder" ? L.T("AIL_e7b1c7f7", "tu muerte") : kind == "seduce" ? L.T("AIL_e7b1c7f8", "seducir a tu cónyuge") : L.T("AIL_e7b1c7f9", "arruinar tu nombre con mentiras");

        private static string KindWordOtherEs(string kind) =>
            kind == "murder" ? L.T("AIL_e7b1c80e", "su muerte") : kind == "seduce" ? L.T("AIL_e7b1c80f", "seducir a su cónyuge") : L.T("AIL_e7b1c810", "arruinar su nombre con mentiras");

        // ============================================================ what characters know

        /// <summary>For a character's turn: the plot they run or are part of, and what the player may know. "" if none.</summary>
        public static string DescribeFor(Speaker s)
        {
            try
            {
                if (!Enabled || s == null || !s.IsHero) return "";
                var h = s.Hero;
                var lines = new List<string>();
                string player = P?.Name?.ToString() ?? "";
                foreach (var p in Active)
                {
                    var target = Hero.Find(p.TargetId); var plotter = Hero.Find(p.PlotterId);
                    if (target == null || plotter == null) continue;
                    string tname = target == P ? player : target.Name.ToString();
                    string kind = Line(TKinds, p.Kind);
                    if (p.PlotterId == h.StringId)
                    {
                        var acc = p.Accomplices.Select(Hero.Find).Where(x => x != null).Select(x => x.Name.ToString()).ToList();
                        lines.Add(TPlotter.Format("target", tname, "kind", kind, "stage", Line(TStages, p.Kind + Math.Min(2, p.Stage)), "motive", p.Motive,
                            "accomplices", acc.Count > 0 ? " Your accomplices: " + string.Join(", ", acc) + "." : ""));
                        if (p.Known && target == P) lines.Add(TKnown.Format("player", player, "target", tname));
                    }
                    else if (p.Accomplices.Contains(h.StringId))
                        lines.Add(TAccomplice.Format("plotter", plotter.Name.ToString(), "target", tname, "kind", kind));
                    else if (p.Known && h.MapFaction is Kingdom k && k.Leader == h && plotter.MapFaction == k && target != h)
                        lines.Add(TRuler.Format("player", player, "plotter", plotter.Name.ToString(), "target", tname, "kind", kind));
                    else if (p.Known && !p.TargetKnows && !p.ByPlayer && target == h)
                        lines.Add(TVictim.Format("player", player, "plotter", plotter.Name.ToString(), "kind", kind));
                }
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }

        // ============================================================ what the player can do about it

        public static Plot RunBy(Hero h) => h == null ? null : Active.FirstOrDefault(p => p.PlotterId == h.StringId);
        public static Plot HelpedBy(Hero h) => h == null ? null : Active.FirstOrDefault(p => p.Accomplices.Contains(h.StringId));
        public static Plot KnownBy(Hero ruler, string plotterName)
        {
            if (ruler == null || !(ruler.MapFaction is Kingdom k) || k.Leader != ruler) return null;
            string n = WorldSim.Norm(plotterName ?? "");
            return Active.FirstOrDefault(p => p.Known && !p.ByPlayer && p.TargetId != ruler.StringId && Hero.Find(p.PlotterId) is Hero pl && pl.MapFaction == k
                                              && (n.Length == 0 || WorldSim.Norm(pl.Name.ToString()).Contains(n) || n.Contains(WorldSim.Norm(pl.Name.ToString()))));
        }
        public static bool AnyKnownForRuler(Hero ruler) => KnownBy(ruler, null) != null;

        public static string Abandon(Hero plotter)
        {
            var p = RunBy(plotter);
            if (p == null) return null;
            var target = Hero.Find(p.TargetId);
            p.Known = true;
            End(p, "abandoned");
            return L.F("AIL_e7b1c7fa", $"{plotter.Name} abandona su conjura contra {(target == P ? L.T("AIL_e7b1c7fb", "ti") : target?.Name?.ToString() ?? "?")}.");
        }

        public static string Betray(Hero accomplice)
        {
            var p = HelpedBy(accomplice);
            if (p == null) return null;
            var plotter = Hero.Find(p.PlotterId); var target = Hero.Find(p.TargetId);
            if (plotter == null || target == null) return null;
            p.Known = true;
            p.Accomplices.Remove(accomplice.StringId);
            // with no hands left to do it, a murder or a smear stalls
            if (p.Accomplices.Count == 0 && p.Kind != "seduce") End(p, "betrayed");
            string what = target == P ? KindWordEs(p.Kind) : KindWordOtherEs(p.Kind);
            Log.Info($"[plot] {accomplice.Name} betrays {plotter.Name}'s plot");
            return L.F("AIL_e7b1c7fc", $"{accomplice.Name} te lo cuenta todo: {plotter.Name} trama contra {(target == P ? L.T("AIL_e7b1c7fb", "ti") : target.Name.ToString())} ({what}).");
        }

        public static string Condemn(Hero ruler, string plotterName)
        {
            var p = KnownBy(ruler, plotterName);
            if (p == null) return null;
            var plotter = Hero.Find(p.PlotterId);
            if (plotter == null) return null;
            int fine = Math.Min(5000, Math.Max(0, plotter.Gold / 2));
            try
            {
                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(ruler, plotter, -20, false);
                if (fine > 0) GiveGoldAction.ApplyBetweenCharacters(plotter, ruler, fine, true);
            }
            catch { }
            p.Exposed = true;
            var target = Hero.Find(p.TargetId) ?? P;
            if (target == P)
                News(L.F("AIL_e7b1c7fd", $"{ruler.Name} condena a {plotter.Name}"), L.F("AIL_e7b1c7fe", $"{ruler.Name} condena a {plotter.Name} por conspirar contra ti: pierde su favor y paga {fine} denarios."),
                     $"{ruler.Name} condemned {plotter.Name} for plotting against {P.Name}.", 2, plotter, P);
            else
            {
                try { ChangeRelationAction.ApplyPlayerRelation(target, 8, false, true); } catch { }
                News(L.F("AIL_e7b1c7fd", $"{ruler.Name} condena a {plotter.Name}"), L.F("AIL_e7b1df17", $"Por lo que le has contado, {ruler.Name} condena a {plotter.Name} por conspirar contra {target.Name}: pierde su favor y paga {fine} denarios."),
                     $"On {P.Name}'s word, {ruler.Name} condemned {plotter.Name} for plotting against {target.Name}.", 2, plotter, target);
            }
            End(p, "condemned");
            return "";
        }

        /// <summary>The player asks this character to scheme against someone. Returns the text to show, or null.</summary>
        public static string StartByPlayer(Hero plotter, string targetName, string kind)
        {
            if (!Enabled || plotter == null || RunBy(plotter) != null) return null;
            string n = WorldSim.Norm(targetName ?? "");
            if (n.Length < 2) return null;
            var target = Hero.AllAliveHeroes.FirstOrDefault(h => h != P && h != plotter && !h.IsChild && WorldSim.Norm(h.Name.ToString()) == n)
                         ?? Hero.AllAliveHeroes.FirstOrDefault(h => h != P && h != plotter && !h.IsChild && h.IsLord && WorldSim.Norm(h.Name.ToString()).Contains(n));
            if (target == null) return null;
            kind = (kind ?? "").Trim().ToLowerInvariant();
            if (kind != "murder" && kind != "seduce") kind = "smear";
            if (kind == "murder" && !S.Get.IntrigueMurder) kind = "smear";
            if (kind == "seduce" && (!S.Get.IntrigueSeduction || target.Spouse == null)) kind = "smear";
            Begin(plotter, target, kind, $"{P.Name} asked you to, and you agreed", true, CampaignTime.Now.ToDays);
            return L.F("AIL_e7b1c7ff", $"{plotter.Name} acepta tramar contra {target.Name}.");
        }
    }
}
