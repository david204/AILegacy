using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Dialogue;
using AILegacy.Memory;
using AILegacy.Prompt;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>Someone who lives with another as a concubine: where they came from, to send them back there.</summary>
    public class Concubine
    {
        [JsonProperty("hero")] public string HeroId;
        [JsonProperty("clan", NullValueHandling = NullValueHandling.Ignore)] public string OriginClanId;
        [JsonProperty("occupation")] public int OriginOccupation;
        [JsonProperty("notable")] public bool WasNotable;
        [JsonProperty("since")] public double SinceDay;
        /// <summary>Who they live with. Empty is the player (saves from before other characters could have one).</summary>
        [JsonProperty("partner", NullValueHandling = NullValueHandling.Ignore)] public string PartnerId;
    }

    /// <summary>
    /// Concubines. One who agrees in a conversation with the player (deep romance, unmarried, no kin) leaves their house
    /// for the player's: they join the player's clan and company, their old head of house takes it badly, and children
    /// may come, born out of wedlock with a known father. It ends when either decides, when one of them dies, or when
    /// the player marries them. A concubine cannot be married off to anyone else meanwhile.
    /// With its setting on, married lords of the AI who are unhappy at home can also take one, from another house,
    /// when they spend time together and like each other enough.
    /// </summary>
    internal static class Concubines
    {
        private static readonly Random Rng = new Random();
        private static Hero P => Hero.MainHero;
        private static List<Concubine> All => WorldSim.State?.Concubines ?? new List<Concubine>();
        public static bool Enabled => S.Get.EnableConcubines && Campaign.Current != null;

        private static readonly Tpl TCtx = new Tpl("concubines/ctx.txt",
            "You are {player}'s concubine and have lived in their household for {days} day(s). You left your own house for it. You are not their spouse, and the world knows it.");
        private static readonly Tpl TCtxPlayerSide = new Tpl("concubines/ctx_known.txt",
            "{name} lives with {player} as their concubine.");
        private static readonly Tpl TCtxNpc = new Tpl("concubines/ctx_npc.txt",
            "You are {partner}'s concubine and have lived in their household for {days} day(s). You left your own house, {house}, for it. You are not their spouse, and the world knows it.");
        private static readonly Tpl TCtxPartner = new Tpl("concubines/ctx_partner.txt",
            "You keep {name} as your concubine. {spouse} knows it and does not like it.");
        private static readonly Tpl TCtxKnownNpc = new Tpl("concubines/ctx_known_npc.txt",
            "{name} lives with {partner} as their concubine.");

        public static void Touch() { var _ = TCtx.Key + TCtxPlayerSide.Key + TCtxNpc.Key + TCtxPartner.Key + TCtxKnownNpc.Key; }

        private static Hero PartnerOf(Concubine c) => c == null ? null : string.IsNullOrEmpty(c.PartnerId) ? P : Hero.Find(c.PartnerId);
        private static bool WithPlayer(Concubine c) => c != null && string.IsNullOrEmpty(c.PartnerId);

        /// <summary>A concubine of anyone.</summary>
        public static bool Is(Hero h) => h != null && All.Any(c => c.HeroId == h.StringId);

        /// <summary>A concubine of the player.</summary>
        public static bool IsWithPlayer(Hero h) => h != null && All.Any(c => c.HeroId == h.StringId && WithPlayer(c));

        /// <summary>Who they live with as a concubine, or null.</summary>
        public static Hero PartnerOf(Hero h) => h == null ? null : PartnerOf(All.FirstOrDefault(c => c.HeroId == h.StringId));

        private static int CountOf(Hero partner) => partner == null ? 0 : All.Count(c => c.PartnerId == partner.StringId);

        /// <summary>
        /// Why they cannot become the player's concubine right now, or null if they can. Saying yes to a marriage with
        /// a player who is already married skips the romance needed (Illegitimate Children does not ask it there) and
        /// lets someone married go, since that marriage is ended first.
        /// </summary>
        public static string WhyNot(Hero h, bool checkRomance = true, bool allowMarried = false)
        {
            var cfg = S.Get;
            if (!Enabled || h == null || P == null || h == P) return "no";
            if (IsWithPlayer(h)) return "you already live with them as their concubine";
            if (Is(h)) return $"you already live with {PartnerOf(h)?.Name} as their concubine";
            if (CountOf(h) > 0) return "you keep a concubine of your own";
            if (!h.IsAlive || h.IsChild || h.Age < 18 || P.Age < 18) return "you are not of age for that";
            if (h.Spouse == P) return "you are already their spouse";
            if (!allowMarried && h.Spouse != null && h.Spouse.IsAlive) return "you are married";
            if (h.IsFemale == P.IsFemale && !cfg.RomanceSameSex) return "it is not done";
            if (Bonds.IsCloseFamily(h)) return "you are close kin";
            if (h.IsPrisoner) return "you are their prisoner";
            if (h.MapFaction is Kingdom k && k.Leader == h) return "you rule a kingdom and will not live as anyone's concubine";
            if (!checkRomance) return null;
            var mem = MemoryStore.Find(h.StringId);
            if (mem == null || mem.Romance < cfg.ConcubineMinRomance) return $"your feelings for them are not that deep yet (romance {mem?.Romance ?? 0} of {cfg.ConcubineMinRomance})";
            return null;
        }

        /// <summary>The concubine's old house loses them. Run after the caller has freed them from parties and posts.</summary>
        public static string Take(Hero h, bool checkRomance = true)
        {
            try
            {
                if (WhyNot(h, checkRomance) != null) return null;
                var c = new Concubine { HeroId = h.StringId, OriginClanId = h.Clan?.StringId, OriginOccupation = (int)h.Occupation, WasNotable = h.IsNotable, SinceDay = CampaignTime.Now.ToDays };
                var head = h.Clan?.Leader;
                if (head != null && head != h && head != P && head.IsAlive && head.GetRelationWithPlayer() < 30)
                    ChangeRelationAction.ApplyPlayerRelation(head, -25, false, true);
                if (h.IsPlayerCompanion) h.CompanionOf = null;
                if (h.Occupation != Occupation.Lord) { h.SetNewOccupation(Occupation.Lord); People.SyncOccupation(h); }
                if (h.Clan != Clan.PlayerClan) h.Clan = Clan.PlayerClan;
                WorldSim.State.Concubines.Add(c);
                // they ride with the player once the conversation is over
                try
                {
                    var cm = Campaign.Current.ConversationManager;
                    void Join() { try { if (h.IsAlive && IsWithPlayer(h) && h.Clan == Clan.PlayerClan && !h.IsPrisoner) MoveIntoPlayerParty(h); } catch (Exception ex) { Log.Error("Concubine: join party", ex); } }
                    if (cm != null && cm.IsConversationInProgress) cm.ConversationEndOneShot += Join; else Join();
                }
                catch { }
                Note(h, $"[Done: you left your house to live with {P.Name} as their concubine.]");
                Log.Info($"[concubines] {h.Name} moves in with the player");
                SaveFiles.Dirty();
                return L.F("AIL_e7b1c836", $"{h.Name} deja su casa para vivir contigo como concubina.");
            }
            catch (Exception ex) { Log.Error("Concubine: take", ex); return null; }
        }

        /// <summary>
        /// Into the player's party, as Illegitimate Children's household move does it: one who leads a party of their
        /// own brings its soldiers into the player's party, the party is broken up, and the heroes who rode in it
        /// come along too. Anyone else just joins the player's party, from wherever they were.
        /// </summary>
        private static void MoveIntoPlayerParty(Hero h)
        {
            var main = MobileParty.MainParty;
            if (h == null || main == null) return;
            if (h.GovernorOf != null) try { ChangeGovernorAction.RemoveGovernorOf(h); } catch (Exception ex) { Log.Warn("Concubine: governorship: " + ex.Message); }
            var own = h.PartyBelongedTo;
            // a party only lent to follow the player is simply let go
            if (own != null && own.LeaderHero == h && OrderSystem.IsTempParty(own)) { OrderSystem.DissolveTempParty(h, own); own = h.PartyBelongedTo; }
            if (own != null && own != main && own.LeaderHero == h && own.IsActive)
            {
                if (own.MapEvent != null || own.SiegeEvent != null) { Log.Warn($"Concubine: {h.Name}'s party is fighting; they join later"); return; }
                var troops = TroopRoster.CreateDummyTroopRoster();
                var heroes = new List<Hero>();
                foreach (var e in own.MemberRoster.GetTroopRoster().ToList())
                {
                    if (e.Character == null) continue;
                    if (e.Character.IsHero) { if (e.Character.HeroObject != null && !heroes.Contains(e.Character.HeroObject)) heroes.Add(e.Character.HeroObject); }
                    else troops.Add(e);
                }
                int men = troops.TotalManCount;
                OrderSystem.Cancel(h, silent: true);
                main.MemberRoster.Add(troops);
                DestroyPartyAction.Apply(own.Party, own);
                foreach (var x in heroes)
                    if (x?.CharacterObject != null && x.IsAlive && !x.IsPrisoner && main.MemberRoster.FindIndexOfTroop(x.CharacterObject) < 0) AddHeroToPartyAction.Apply(x, main, true);
                Log.Info($"[concubines] {h.Name} brings {men} soldier(s) and {heroes.Count} hero(es) into the player's party; their own party is broken up");
                if (men > 0) InformationManager.DisplayMessage(new InformationMessage(L.F("AIL_e7b1d609", $"{h.Name} se une a tu partida y trae consigo a {men} soldados."), Colors.Green));
            }
            else if (main.MemberRoster.FindIndexOfTroop(h.CharacterObject) < 0) AddHeroToPartyAction.Apply(h, main, true);
        }

        /// <summary>It ends: back to their house and their old life, with a hard word between them. Null if they were not one.</summary>
        public static string End(Hero h, bool quiet = false)
        {
            var c = All.FirstOrDefault(x => x.HeroId == h?.StringId);
            if (c == null) return null;
            var partner = PartnerOf(c);
            bool player = WithPlayer(c);
            WorldSim.State.Concubines.Remove(c);
            try
            {
                if (h.IsAlive)
                {
                    var party = h.PartyBelongedTo;
                    if (party != null && party.LeaderHero != h && (party == MobileParty.MainParty || party.LeaderHero == partner))
                        try { party.MemberRoster.RemoveTroop(h.CharacterObject); } catch { }
                    if (h.PartyBelongedTo == MobileParty.MainParty) Log.Warn($"Concubine: {h.Name} is still in your party; will leave when released");
                    var origin = c.OriginClanId != null ? Clan.All.FirstOrDefault(x => x.StringId == c.OriginClanId && !x.IsEliminated) : null;
                    // a notable goes the long way round, through wanderer, so the game places them again
                    if (c.WasNotable) { h.Clan = null; h.SetNewOccupation(Occupation.Wanderer); h.SetNewOccupation((Occupation)c.OriginOccupation); }
                    else
                    {
                        if (origin != null || player) h.Clan = origin;
                        if ((Occupation)c.OriginOccupation != h.Occupation) h.SetNewOccupation((Occupation)c.OriginOccupation);
                    }
                    var home = h.HomeSettlement ?? origin?.HomeSettlement ?? Settlement.All.FirstOrDefault(s => s.IsTown);
                    if (home != null && h.PartyBelongedTo == null) try { TeleportHeroAction.ApplyImmediateTeleportToSettlement(h, home); } catch { }
                    if (player) ChangeRelationAction.ApplyPlayerRelation(h, -25, false, !quiet);
                    else if (partner != null && partner.IsAlive) ChangeRelationAction.ApplyRelationChangeBetweenHeroes(h, partner, -25, false);
                    Note(h, $"[Done: you are no longer {partner?.Name}'s concubine and went back to your own house.]");
                }
            }
            catch (Exception ex) { Log.Error("Concubine: dismiss", ex); }
            Log.Info($"[concubines] {h?.Name} is no longer {(player ? "the player's" : partner?.Name + "'s")} concubine");
            SaveFiles.Dirty();
            if (quiet || !player) return "";
            return L.F("AIL_e7b1c837", $"{h.Name} deja de ser tu concubina y vuelve a su casa.");
        }

        /// <summary>Marrying one's concubine: the bond becomes a marriage, nobody goes home and nobody is wronged.</summary>
        public static void OnMarried(Hero a, Hero b, bool show)
        {
            try
            {
                if (WorldSim.State?.Concubines == null) return;
                WorldSim.State.Concubines.RemoveAll(c => c.HeroId == a?.StringId || c.HeroId == b?.StringId);
            }
            catch { }
        }

        public static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool show)
        {
            try
            {
                if (WorldSim.State?.Concubines == null || victim == null) return;
                if (victim == P) { WorldSim.State.Concubines.RemoveAll(WithPlayer); return; }
                WorldSim.State.Concubines.RemoveAll(c => c.HeroId == victim.StringId);
                // the one who kept them is gone: they go back to their house, once the death is done
                foreach (var c in All.Where(c => c.PartnerId == victim.StringId).ToList())
                {
                    var h = Hero.Find(c.HeroId);
                    if (h != null) MainThread.Post(() => { try { End(h, true); } catch (Exception ex) { Log.Error("[concubines] partner died", ex); } });
                    else WorldSim.State.Concubines.Remove(c);
                }
            }
            catch { }
        }

        /// <summary>Living together: children may come, as between spouses who share a roof (the game's own daily shape).</summary>
        public static void OnDailyTick()
        {
            try
            {
                if (!Enabled || P == null) return;
                foreach (var c in All.ToList())
                {
                    var h = Hero.Find(c.HeroId);
                    if (h == null || !h.IsAlive) { WorldSim.State.Concubines.Remove(c); continue; }
                    var partner = PartnerOf(c);
                    if (partner == null || !partner.IsAlive) { if (!WithPlayer(c)) End(h, true); continue; }
                    if (h.IsFemale == partner.IsFemale) continue;
                    var mother = h.IsFemale ? h : partner; var father = h.IsFemale ? partner : h;
                    if (mother.IsPregnant || mother.Age < 18 || mother.Age > 45) continue;
                    if (!Together(h, partner)) continue;
                    int kids = mother.Children.Count(k => k.Father == father);
                    double p = (1.2 - (mother.Age - 18) * 0.04) / ((kids + 1) * (kids + 1)) * 0.12 * S.Get.ConcubinePregnancyPercent / 100.0;
                    try { p *= Mood.FertilityFactor(mother); } catch { }
                    if (Rng.NextDouble() < p) Bastards.Conceive(mother, father);
                }
                if (S.Get.ConcubinesForNpcs) NpcRound();
            }
            catch (Exception ex) { Log.Error("[concubines] day", ex); }
        }

        private static bool Together(Hero a, Hero b) =>
            a.PartyBelongedTo != null && a.PartyBelongedTo == b.PartyBelongedTo
            || (a.CurrentSettlement != null && a.CurrentSettlement == b.CurrentSettlement);

        // ============================================================ between other characters

        /// <summary>Each married lord rolls once a day; only the few who pass look around for someone.</summary>
        private static void NpcRound()
        {
            var cfg = S.Get;
            if (cfg.ConcubineNpcYearlyPercent <= 0) return;
            double daily = 1.0 - Math.Pow(1.0 - Math.Min(0.99, cfg.ConcubineNpcYearlyPercent / 100.0), 1.0 / CampaignTime.DaysInYear);
            foreach (var lord in Hero.AllAliveHeroes.ToList())
            {
                try
                {
                    double p = lord.IsFemale ? daily * cfg.ConcubineNpcWomenPercent / 100.0 : daily;
                    if (p <= 0 || Rng.NextDouble() >= p) continue;
                    if (!MayKeep(lord)) continue;
                    var best = Nearby(lord).Where(x => Suitable(x, lord))
                        .Select(x => (h: x, like: Math.Min(Liking(lord, x) - Threshold(lord), Liking(x, lord) - cfg.ConcubineNpcLiking)))
                        .Where(x => x.like >= 0).OrderByDescending(x => x.like).FirstOrDefault();
                    if (best.h != null) TakeNpc(best.h, lord);
                }
                catch (Exception ex) { Log.Error("[concubines] lord's look", ex); }
            }
        }

        private static bool MayKeep(Hero lord) =>
            lord != null && lord != P && lord.IsAlive && lord.IsLord && !lord.IsChild && lord.Age >= 18 && lord.Clan != null
            && lord.Clan != Clan.PlayerClan && !lord.Clan.IsMinorFaction && !lord.IsPrisoner && !Is(lord)
            && lord.Spouse != null && lord.Spouse.IsAlive && lord.Spouse != P
            && lord.GetRelation(lord.Spouse) < S.Get.ConcubineNpcSpouseRelation
            && CountOf(lord) < S.Get.ConcubineNpcMaxPerLord;

        private static bool Suitable(Hero x, Hero lord)
        {
            var cfg = S.Get;
            if (x == null || x == lord || x == P || x == P?.Spouse || !x.IsAlive || x.IsChild || x.Age < 18 || x.Age > 40) return false;
            if (!x.IsLord || x.IsNotable || x.Clan == null || x.Clan == lord.Clan || x.Clan == Clan.PlayerClan || x.Clan.IsMinorFaction) return false;
            if (x.Clan.Leader == x || x.MapFaction is Kingdom k && k.Leader == x) return false;
            if (x.Spouse != null && x.Spouse.IsAlive) return false;
            if (Is(x) || CountOf(x) > 0 || x.IsPrisoner || x.GovernorOf != null || x.IsPlayerCompanion) return false;
            if (x.PartyBelongedTo != null && x.PartyBelongedTo.LeaderHero == x) return false;   // leads men of their own
            if (x.IsFemale == lord.IsFemale && !cfg.RomanceSameSex) return false;
            if (Bastards.Kin(x, lord)) return false;
            if (x.Culture != lord.Culture && Rng.Next(100) >= cfg.ConcubineNpcOtherCulturePercent) return false;
            return true;
        }

        /// <summary>How much one likes the other: the game's attraction (0 to 100) plus a quarter of their relation.</summary>
        private static int Liking(Hero from, Hero to)
        {
            int attraction = 0;
            try { attraction = Campaign.Current.Models.RomanceModel.GetAttractionValuePercentage(from, to); } catch { }
            return attraction + (int)Math.Round(from.GetRelation(to) / 4f);
        }

        private static int Threshold(Hero lord)
        {
            int t = S.Get.ConcubineNpcLiking;
            try { if (lord.GetTraitLevel(DefaultTraits.Honor) > 0) t += 30; if (lord.GetTraitLevel(DefaultTraits.Mercy) > 0) t += 20; } catch { }
            return t;
        }

        private static IEnumerable<Hero> Nearby(Hero lord)
        {
            var seen = new HashSet<Hero>();
            var st = lord.CurrentSettlement ?? lord.PartyBelongedTo?.CurrentSettlement;
            if (st != null)
            {
                foreach (var p in st.Parties) if (p?.LeaderHero != null && seen.Add(p.LeaderHero)) yield return p.LeaderHero;
                foreach (var h in st.HeroesWithoutParty) if (seen.Add(h)) yield return h;
            }
            var party = lord.PartyBelongedTo;
            if (party?.Army != null) foreach (var p in party.Army.Parties) if (p?.LeaderHero != null && seen.Add(p.LeaderHero)) yield return p.LeaderHero;
            if (party != null)
                foreach (var e in party.MemberRoster.GetTroopRoster())
                    if (e.Character?.HeroObject != null && seen.Add(e.Character.HeroObject)) yield return e.Character.HeroObject;
        }

        private static void TakeNpc(Hero x, Hero lord)
        {
            if (x.PartyBelongedTo?.LeaderHero == x) return;   // someone leading men of their own never moves house this way
            var oldClan = x.Clan;
            var c = new Concubine { HeroId = x.StringId, OriginClanId = oldClan?.StringId, OriginOccupation = (int)x.Occupation, WasNotable = false, SinceDay = CampaignTime.Now.ToDays, PartnerId = lord.StringId };
            var head = oldClan?.Leader;
            if (head != null && head != lord && head.IsAlive && lord.GetRelation(head) < 30)
                ChangeRelationAction.ApplyRelationChangeBetweenHeroes(lord, head, -25, false);
            var spouse = lord.Spouse;
            if (spouse != null && spouse.IsAlive) ChangeRelationAction.ApplyRelationChangeBetweenHeroes(lord, spouse, -15, false);
            // out of the party they rode with, into the lord's house, and at his side
            var party = x.PartyBelongedTo;
            if (party != null && party.LeaderHero != x) party.MemberRoster.RemoveTroop(x.CharacterObject);
            x.Clan = lord.Clan;
            WorldSim.State.Concubines.Add(c);
            var lp = lord.PartyBelongedTo;
            if (lp != null && lp.LeaderHero == lord && x.PartyBelongedTo == null) AddHeroToPartyAction.Apply(x, lp);
            else if (x.PartyBelongedTo == null)
            {
                var where = lord.CurrentSettlement ?? lord.HomeSettlement;
                if (where != null) TeleportHeroAction.ApplyImmediateTeleportToSettlement(x, where);
            }
            Note(x, $"[Done: you left your house, {oldClan?.Name}, to live with {lord.Name} as their concubine.]");
            Note(lord, $"[Done: you took {x.Name} of {oldClan?.Name} into your household as your concubine.]");
            if (spouse != null) Note(spouse, $"[Done: your spouse {lord.Name} took {x.Name} into the household as a concubine. It hurt you.]");
            if (head != null && head != x && head != lord) Note(head, $"[Done: {x.Name} left your house to live with {lord.Name} as their concubine.]");
            try
            {
                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = 2, Text = $"{lord.Name} has taken {x.Name} of {oldClan?.Name} as a concubine.",
                    TitleEs = L.F("AIL_e7b1c880", $"{lord.Name} toma una concubina"),
                    TextEs = L.F("AIL_e7b1c881", $"{x.Name}, de la casa {oldClan?.Name}, vive ahora con {lord.Name} como concubina."),
                    Kingdoms = new[] { lord.MapFaction, oldClan?.MapFaction }.Where(f => f != null).Select(f => f.StringId).Distinct().ToList(),
                    Heroes = new List<string> { lord.StringId, x.StringId },
                });
            }
            catch { }
            Log.Info($"[concubines] {lord.Name} takes {x.Name} of {oldClan?.Name} as a concubine");
            SaveFiles.Dirty();
        }

        // ============================================================ prompts

        public static string DescribeFor(Speaker s)
        {
            try
            {
                if (!Enabled || s == null || !s.IsHero || All.Count == 0) return "";
                var me = s.Hero;
                var lines = new List<string>();
                var mine = All.FirstOrDefault(x => x.HeroId == me.StringId);
                if (mine != null)
                {
                    int days = (int)(CampaignTime.Now.ToDays - mine.SinceDay);
                    if (WithPlayer(mine)) return TCtx.Format("player", P.Name.ToString(), "days", days);
                    var house = mine.OriginClanId != null ? Clan.All.FirstOrDefault(x => x.StringId == mine.OriginClanId)?.Name?.ToString() : null;
                    lines.Add(TCtxNpc.Format("partner", PartnerOf(mine)?.Name?.ToString() ?? "?", "days", days, "house", house ?? "?"));
                }
                foreach (var c in All)
                {
                    if (c == mine) continue;
                    var h = Hero.Find(c.HeroId); var partner = PartnerOf(c);
                    if (h == null || partner == null) continue;
                    if (WithPlayer(c))
                    {
                        // the player's spouse and those of their house know who shares the player's roof
                        if (me.Spouse == P || me.Clan == Clan.PlayerClan) lines.Add(TCtxPlayerSide.Format("name", h.Name.ToString(), "player", P.Name.ToString()));
                    }
                    else if (partner == me)
                        lines.Add(TCtxPartner.Format("name", h.Name.ToString(), "spouse", me.Spouse?.Name?.ToString() ?? "Your household"));
                    else if (me.Spouse == partner || me.Clan == partner.Clan || me.Clan == h.Clan
                             || (c.OriginClanId != null && me.Clan?.StringId == c.OriginClanId))
                        lines.Add(TCtxKnownNpc.Format("name", h.Name.ToString(), "partner", partner.Name.ToString()));
                }
                return string.Join("\n", lines);
            }
            catch { return ""; }
        }

        private static void Note(Hero h, string text)
        {
            try { MemoryStore.Get(h.StringId, h.Name.ToString()).Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays }); } catch { }
        }

        public static string Report()
        {
            if (All.Count == 0) return "Nobody lives as a concubine.";
            return string.Join("\n", All.Select(c =>
            {
                var h = Hero.Find(c.HeroId); var partner = PartnerOf(c);
                return $"{h?.Name} ({h?.Clan?.Name}) with {partner?.Name}, {(int)(CampaignTime.Now.ToDays - c.SinceDay)} day(s), from {c.OriginClanId ?? "no house"}";
            }));
        }

        /// <summary>For testing: the lord takes them now, if both meet the conditions (the yearly roll and the liking are skipped).</summary>
        public static string Force(Hero lord, Hero x)
        {
            if (!Enabled) return "Concubines are off.";
            if (!MayKeep(lord)) return $"{lord.Name} cannot keep one (married to someone alive, unhappy enough at home, under the most per lord, not your clan).";
            if (!Suitable(x, lord)) return $"{x.Name} is not suitable for {lord.Name} (age, house, married, kin, sex, culture roll...).";
            TakeNpc(x, lord);
            return $"{x.Name} now lives with {lord.Name} as a concubine. Liking: {Liking(lord, x)} / {Liking(x, lord)}, needed {Threshold(lord)} / {S.Get.ConcubineNpcLiking}.";
        }
    }

    public static class ConcubineCommands
    {
        [CommandLineFunctionality.CommandLineArgumentFunction("concubines", "ailegacy")]
        public static string List(List<string> args) => Campaign.Current == null ? "No campaign." : Concubines.Report();

        [CommandLineFunctionality.CommandLineArgumentFunction("take_concubine", "ailegacy")]
        public static string Take(List<string> args)
        {
            if (Campaign.Current == null || args == null || args.Count < 2 || string.IsNullOrWhiteSpace(args[0]) || string.IsNullOrWhiteSpace(args[1]))
                return "Usage: ailegacy.take_concubine <lord> <concubine> (names without spaces, or with _)";
            Hero Find(string n) { n = n.Replace('_', ' ').ToLowerInvariant(); return Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant() == n) ?? Hero.AllAliveHeroes.FirstOrDefault(h => h.Name.ToString().ToLowerInvariant().Contains(n)); }
            var lord = Find(args[0]); var x = Find(args[1]);
            if (lord == null || x == null) return "Cannot find one of the two.";
            return Concubines.Force(lord, x);
        }
    }
}
