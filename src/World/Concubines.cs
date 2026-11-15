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
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>Someone who lives with the player as a concubine: where they came from, to send them back there.</summary>
    public class Concubine
    {
        [JsonProperty("hero")] public string HeroId;
        [JsonProperty("clan", NullValueHandling = NullValueHandling.Ignore)] public string OriginClanId;
        [JsonProperty("occupation")] public int OriginOccupation;
        [JsonProperty("notable")] public bool WasNotable;
        [JsonProperty("since")] public double SinceDay;
    }

    /// <summary>
    /// Concubines of the player. One who agrees in a conversation (deep romance, unmarried, no kin) leaves their house for
    /// the player's: they join the player's clan and company, their old head of house takes it badly, and children may
    /// come, born out of wedlock with a known father. It ends when either decides, when one of them dies, or when the
    /// player marries them. A concubine cannot be married off to anyone else meanwhile.
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

        public static void Touch() { var _ = TCtx.Key + TCtxPlayerSide.Key; }

        public static bool Is(Hero h) => h != null && All.Any(c => c.HeroId == h.StringId);

        /// <summary>Why they cannot become the player's concubine right now, or null if they can.</summary>
        public static string WhyNot(Hero h)
        {
            var cfg = S.Get;
            if (!Enabled || h == null || P == null || h == P) return "no";
            if (Is(h)) return "you already live with them as their concubine";
            if (!h.IsAlive || h.IsChild || h.Age < 18 || P.Age < 18) return "you are not of age for that";
            if (h.Spouse != null && h.Spouse.IsAlive) return "you are married";
            if (h.Spouse == P) return "you are already their spouse";
            if (h.IsFemale == P.IsFemale && !cfg.RomanceSameSex) return "it is not done";
            if (Bonds.IsCloseFamily(h)) return "you are close kin";
            if (h.IsPrisoner) return "you are their prisoner";
            if (h.MapFaction is Kingdom k && k.Leader == h) return "you rule a kingdom and will not live as anyone's concubine";
            var mem = MemoryStore.Find(h.StringId);
            if (mem == null || mem.Romance < cfg.ConcubineMinRomance) return $"your feelings for them are not that deep yet (romance {mem?.Romance ?? 0} of {cfg.ConcubineMinRomance})";
            return null;
        }

        /// <summary>The concubine's old house loses them. Run after the caller has freed them from parties and posts.</summary>
        public static string Take(Hero h)
        {
            try
            {
                if (WhyNot(h) != null) return null;
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
                    void Join() { try { if (h.IsAlive && Is(h) && h.Clan == Clan.PlayerClan && h.PartyBelongedTo == null && !h.IsPrisoner) AddHeroToPartyAction.Apply(h, MobileParty.MainParty); } catch (Exception ex) { Log.Error("Concubina: unirse a la partida", ex); } }
                    if (cm != null && cm.IsConversationInProgress) cm.ConversationEndOneShot += Join; else Join();
                }
                catch { }
                Note(h, $"[Done: you left your house to live with {P.Name} as their concubine.]");
                Log.Info($"[concubinas] {h.Name} pasa a vivir con el jugador");
                SaveFiles.Dirty();
                return L.F("AIL_e7b1c836", $"{h.Name} deja su casa para vivir contigo como concubina.");
            }
            catch (Exception ex) { Log.Error("Concubina: tomar", ex); return null; }
        }

        /// <summary>It ends: back to their house and their old life, with a hard word between them. Null if they were not one.</summary>
        public static string End(Hero h, bool quiet = false)
        {
            var c = All.FirstOrDefault(x => x.HeroId == h?.StringId);
            if (c == null) return null;
            WorldSim.State.Concubines.Remove(c);
            try
            {
                if (h.IsAlive)
                {
                    if (h.PartyBelongedTo == MobileParty.MainParty) try { MobileParty.MainParty.MemberRoster.RemoveTroop(h.CharacterObject); } catch { }
                    if (h.PartyBelongedTo == MobileParty.MainParty) Log.Warn($"Concubina: {h.Name} sigue en tu partida; se irá al soltarla");
                    var origin = c.OriginClanId != null ? Clan.All.FirstOrDefault(x => x.StringId == c.OriginClanId && !x.IsEliminated) : null;
                    // a notable goes the long way round, through wanderer, so the game places them again
                    if (c.WasNotable) { h.Clan = null; h.SetNewOccupation(Occupation.Wanderer); h.SetNewOccupation((Occupation)c.OriginOccupation); }
                    else
                    {
                        h.Clan = origin;
                        if ((Occupation)c.OriginOccupation != h.Occupation) h.SetNewOccupation((Occupation)c.OriginOccupation);
                    }
                    var home = h.HomeSettlement ?? origin?.HomeSettlement ?? Settlement.All.FirstOrDefault(s => s.IsTown);
                    if (home != null && h.PartyBelongedTo == null) try { TeleportHeroAction.ApplyImmediateTeleportToSettlement(h, home); } catch { }
                    ChangeRelationAction.ApplyPlayerRelation(h, -25, false, !quiet);
                    Note(h, $"[Done: you are no longer {P.Name}'s concubine and went back to your own house.]");
                }
            }
            catch (Exception ex) { Log.Error("Concubina: dejar", ex); }
            Log.Info($"[concubinas] {h?.Name} deja de ser concubina");
            SaveFiles.Dirty();
            return quiet ? "" : L.F("AIL_e7b1c837", $"{h.Name} deja de ser tu concubina y vuelve a su casa.");
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
                if (victim == P) { WorldSim.State.Concubines.Clear(); return; }
                WorldSim.State.Concubines.RemoveAll(c => c.HeroId == victim.StringId);
            }
            catch { }
        }

        /// <summary>Living together: children may come, as between spouses who share a roof (the game's own daily shape).</summary>
        public static void OnDailyTick()
        {
            try
            {
                if (!Enabled || P == null || All.Count == 0) return;
                foreach (var c in All.ToList())
                {
                    var h = Hero.Find(c.HeroId);
                    if (h == null || !h.IsAlive) { WorldSim.State.Concubines.Remove(c); continue; }
                    if (h.IsFemale == P.IsFemale) continue;
                    var mother = h.IsFemale ? h : P; var father = h.IsFemale ? P : h;
                    if (mother.IsPregnant || mother.Age < 18 || mother.Age > 45) continue;
                    bool together = h.PartyBelongedTo != null && h.PartyBelongedTo == P.PartyBelongedTo
                                    || (h.CurrentSettlement != null && h.CurrentSettlement == P.CurrentSettlement);
                    if (!together) continue;
                    int kids = mother.Children.Count(k => k.Father == father);
                    double p = (1.2 - (mother.Age - 18) * 0.04) / ((kids + 1) * (kids + 1)) * 0.12 * S.Get.ConcubinePregnancyPercent / 100.0;
                    try { p *= Mood.FertilityFactor(mother); } catch { }
                    if (Rng.NextDouble() < p) Bastards.Conceive(mother, father);
                }
            }
            catch (Exception ex) { Log.Error("[concubinas] día", ex); }
        }

        public static string DescribeFor(Speaker s)
        {
            try
            {
                if (!Enabled || s == null || !s.IsHero || All.Count == 0) return "";
                var c = All.FirstOrDefault(x => x.HeroId == s.Hero.StringId);
                if (c != null) return TCtx.Format("player", P.Name.ToString(), "days", (int)(CampaignTime.Now.ToDays - c.SinceDay));
                // the player's spouse and those of their house know who shares the player's roof
                if (s.Hero.Spouse == P || s.Hero.Clan == Clan.PlayerClan)
                    return string.Join("\n", All.Select(x => Hero.Find(x.HeroId)).Where(x => x != null && x != s.Hero)
                        .Select(x => TCtxPlayerSide.Format("name", x.Name.ToString(), "player", P.Name.ToString())));
                return "";
            }
            catch { return ""; }
        }

        private static void Note(Hero h, string text)
        {
            try { MemoryStore.Get(h.StringId, h.Name.ToString()).Turns.Add(new ChatTurn { Role = "player", Text = text, Day = CampaignTime.Now.ToDays }); } catch { }
        }
    }
}
