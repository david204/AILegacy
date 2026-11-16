using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>A map order an NPC agreed to carry out with their own party.</summary>
    public class PartyOrder
    {
        [JsonProperty("hero")] public string HeroId;
        /// <summary>follow | goto | meet | wait | patrol | attack | besiege | raid | escort | approach | return (ride back to the player, then the next leg)</summary>
        [JsonProperty("type")] public string Type;
        /// <summary>StringId of the target settlement or party.</summary>
        [JsonProperty("target")] public string TargetId;
        [JsonProperty("target_name")] public string TargetName;
        [JsonProperty("until")] public double UntilDay;
        [JsonProperty("arrived")] public bool Arrived;
        /// <summary>Days the agreement lasts once they arrive (following you, waiting for you).</summary>
        [JsonProperty("stay")] public double StayDays;
        /// <summary>What to do when done: "follow" = go back to the player.</summary>
        [JsonProperty("then")] public string Then;
        /// <summary>Days to stay on arrival before the order counts as done ("go to X and wait two days").</summary>
        [JsonProperty("wait")] public double WaitDays;
        /// <summary>Not agreed with the player: a landless clan patrolling on its own (World/LandlessPatrols.cs). Silent, and anything the player asks replaces it.</summary>
        [JsonProperty("auto", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool Auto;
        /// <summary>Why an Auto order was given, when it is not a landless patrol: "care" = a sick party riding to be treated (World/DiseasesExtra.cs).</summary>
        [JsonProperty("why", DefaultValueHandling = DefaultValueHandling.Ignore)] public string Reason;
        /// <summary>
        /// What they agreed to do afterwards, in order ("go to A, wait two days, then go to B"). Each leg keeps its
        /// type, target, days (StayDays), Then and WaitDays; its deadline is set when it starts.
        /// </summary>
        [JsonProperty("next", NullValueHandling = NullValueHandling.Ignore)] public List<PartyOrder> Next;
        /// <summary>A notable, wanderer or lord on foot agreed to follow: their party is made once the player is out on the map.</summary>
        [JsonProperty("need_party", DefaultValueHandling = DefaultValueHandling.Ignore)] public bool NeedParty;
        /// <summary>The party made for them only to follow the player; sent home and broken up when the order ends.</summary>
        [JsonProperty("temp_party", NullValueHandling = NullValueHandling.Ignore)] public string TempPartyId;
        /// <summary>Where they came from (settlement), to go back to.</summary>
        [JsonProperty("home", NullValueHandling = NullValueHandling.Ignore)] public string HomeId;
    }

    /// <summary>
    /// Map orders: the NPC moves their party as agreed (follow you, go somewhere, wait, patrol, attack,
    /// besiege, raid) until the order expires or is done. While it lasts, the game AI doesn't make decisions for them.
    /// </summary>
    internal static class OrderSystem
    {
        public static List<PartyOrder> Orders = new List<PartyOrder>();

        private static readonly Prompt.Tpl TOwnPatrol = new Prompt.Tpl("orders/landless_patrol.txt",
            "your clan holds no land, so these days you are patrolling the country around {place} with your men, of your own accord");
        private static readonly Prompt.Tpl TOwnCare = new Prompt.Tpl("orders/seeking_care.txt",
            "there is sickness in your party, so you are taking it to {place} to have the physicians there treat you and your men, of your own accord");
        private static readonly Prompt.Tpl TGoingHome = new Prompt.Tpl("orders/going_home.txt",
            "you have stopped riding with {player} and are on your way back home to {place} with the few companions you set out with");
        private static readonly Prompt.Tpl TWaitingToFollow = new Prompt.Tpl("orders/waiting_to_follow.txt",
            "you agreed to follow {player} on the road: as soon as they leave this place you will set out after them with a small party of your own, and ride with them until one of you calls it off");

        /// <summary>Length of an order that only ends when it is called off (AIInfluence's follow_player has no limit).</summary>
        public const double OpenEnded = 100000;
        private const int OrderMaxShown = 1000;

        // ============================================================ queries

        /// <summary>The party this hero leads. Not the player's party, and not one they just ride along in.</summary>
        public static MobileParty PartyLedBy(Hero h)
        {
            var p = h?.PartyBelongedTo;
            if (p == null || p.IsMainParty || p.LeaderHero != h || !p.IsActive) return null;
            return p;
        }

        /// <summary>Leads a party and isn't tied to someone else's army.</summary>
        public static bool CanTakeOrders(Hero h, bool auto = false)
        {
            if ((!auto && !S.Get.EnableMapOrders) || h == null || h.IsPrisoner || !h.IsAlive) return false;
            var p = PartyLedBy(h);
            if (p == null) return false;
            return p.Army == null || p.Army.LeaderParty == p || p.Army.LeaderParty == MobileParty.MainParty;
        }

        /// <summary>The order this hero has agreed with the player. A patrol of their own does not count.</summary>
        public static PartyOrder Find(Hero h) => h == null ? null : Orders.FirstOrDefault(o => o.HeroId == h.StringId && !o.Auto);

        /// <summary>Any order moving this hero's party, their own patrols included.</summary>
        public static PartyOrder FindAny(Hero h) => h == null ? null : Orders.FirstOrDefault(o => o.HeroId == h.StringId);

        /// <summary>For the prompt: what they promised to do with their party, and what comes after it.</summary>
        public static string Describe(Hero h)
        {
            string now = DescribeCurrent(h);
            var o = FindAny(h);
            if (now.Length == 0 || o?.Next == null || o.Next.Count == 0) return now;
            return now + "; after that, as agreed: " + string.Join(", then ", o.Next.Select(LegEn));
        }

        private static string DescribeCurrent(Hero h)
        {
            var o = FindAny(h);
            if (o == null) return "";
            string who = Hero.MainHero.Name.ToString();
            if (o.Type == "home") return TGoingHome.Format("player", who, "place", o.TargetName ?? "");
            if (o.Auto) return (o.Reason == "care" ? TOwnCare : TOwnPatrol).Format("place", o.TargetName ?? "");
            if (o.NeedParty) return TWaitingToFollow.Format("player", who);
            int days = Math.Max(0, (int)Math.Ceiling(o.UntilDay - CampaignTime.Now.ToDays));
            string left = days <= 0 ? "ending today" : days > OrderMaxShown ? "until one of you calls it off" : $"for about {days} more day(s)";
            switch (o.Type)
            {
                case "follow": return o.Arrived ? $"you agreed to ride with {who}'s party and follow them ({left})" : $"you are on your way to join {who}'s party, as agreed";
                case "goto": return $"you agreed with {who} to travel to {o.TargetName}" + (o.WaitDays > 0 ? $", stay there {o.WaitDays:0.#} day(s)" : "") + (o.Then == "follow" ? " and then come back to them" : "");
                case "meet": return o.Arrived ? $"you are waiting at {o.TargetName} to meet {who}, as agreed ({left})" : $"you are travelling to {o.TargetName} to meet {who} there, as agreed";
                case "wait": return $"you agreed with {who} to hold your position and wait ({left})";
                case "approach": return $"you are riding over to {who}'s party to speak with them";
                case "patrol": return $"you agreed with {who} to patrol around {o.TargetName} ({left})";
                case "attack": return $"you agreed with {who} to hunt down {o.TargetName}";
                case "besiege": return $"you agreed with {who} to besiege {o.TargetName}";
                case "raid": return $"you agreed with {who} to raid {o.TargetName}";
                case "return": return $"you are riding back to {who}, as agreed";
                default: return "";
            }
        }

        private static string LegEn(PartyOrder l)
        {
            string who = Hero.MainHero?.Name?.ToString() ?? "them";
            string back = l.Then == "follow" ? $" and come back to {who}" : "";
            switch (l.Type)
            {
                case "goto": return $"go to {l.TargetName}" + (l.WaitDays > 0 ? $" and stay there {l.WaitDays:0.#} day(s)" : "") + back;
                case "meet": return $"wait for {who} at {l.TargetName}";
                case "follow": return $"join {who}'s party and ride with them";
                case "return": return $"ride back to {who}";
                case "wait": return $"hold your position for {l.StayDays:0.#} day(s)";
                case "patrol": return $"patrol around {l.TargetName} for {l.StayDays:0.#} day(s)" + back;
                case "attack": return $"hunt down {l.TargetName}" + back;
                case "besiege": return $"besiege {l.TargetName}" + back;
                case "raid": return $"raid {l.TargetName}" + back;
                case "escort": return $"escort {l.TargetName}" + back;
                default: return l.Type + " " + l.TargetName;
            }
        }

        /// <summary>A leg in the player's language, for the messages.</summary>
        private static string LegText(PartyOrder l)
        {
            string back = l.Then == "follow" ? L.T("AIL_e7b1cf10", " y volver contigo") : "";
            switch (l.Type)
            {
                case "goto": return (l.WaitDays > 0 ? L.F("AIL_e7b1cf11", $"ir a {l.TargetName} y quedarse allí {l.WaitDays:0.#} día(s)") : L.F("AIL_e7b1cf12", $"ir a {l.TargetName}")) + back;
                case "meet": return L.F("AIL_e7b1cf13", $"esperarte en {l.TargetName}");
                case "follow": return L.T("AIL_e7b1cf14", "unirse a tu marcha");
                case "return": return L.T("AIL_e7b1cf15", "volver contigo");
                case "wait": return L.F("AIL_e7b1cf16", $"esperar donde esté {l.StayDays:0.#} día(s)");
                case "patrol": return L.F("AIL_e7b1cf17", $"patrullar en torno a {l.TargetName}") + back;
                case "attack": return L.F("AIL_e7b1cf18", $"dar caza a {l.TargetName}") + back;
                case "besiege": return L.F("AIL_e7b1cf19", $"asediar {l.TargetName}") + back;
                case "raid": return L.F("AIL_e7b1cf1a", $"saquear {l.TargetName}") + back;
                case "escort": return L.F("AIL_e7b1cf1b", $"escoltar a {l.TargetName}") + back;
                default: return l.Type + " " + l.TargetName;
            }
        }

        // ============================================================ target lookup

        private static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        public static Settlement FindSettlement(string name, Func<Settlement, bool> filter = null)
        {
            var n = Norm(name);
            if (n.Length < 2) return null;
            var all = Settlement.All.Where(s => s != null && !s.IsHideout && (filter == null || filter(s))).ToList();
            return all.FirstOrDefault(s => s.StringId == name)
                   ?? all.FirstOrDefault(s => Norm(s.Name.ToString()) == n)
                   ?? all.FirstOrDefault(s => Norm(s.Name.ToString()).StartsWith(n))
                   ?? all.FirstOrDefault(s => n.Contains(Norm(s.Name.ToString())) || Norm(s.Name.ToString()).Contains(n));
        }

        /// <summary>Hostile parties near the NPC, nearest first (attack targets).</summary>
        public static List<MobileParty> HostilesNear(Hero h, int max = 6)
        {
            var result = new List<MobileParty>();
            var own = PartyLedBy(h);
            if (own == null || own.MapFaction == null) return result;
            try
            {
                float radius = S.Get.OrderAttackRadius;
                var here = own.GetPosition2D;
                result = MobileParty.All
                    .Where(p => p != null && p != own && p.IsActive && !p.IsMainParty && !p.IsGarrison && !p.IsMilitia && p.CurrentSettlement == null
                                && p.MapFaction != null && p.MapFaction != own.MapFaction && p.MapFaction.IsAtWarWith(own.MapFaction)
                                && p.MemberRoster.TotalManCount > 0 && p.GetPosition2D.Distance(here) <= radius)
                    .OrderBy(p => p.GetPosition2D.Distance(here)).Take(max).ToList();
            }
            catch (Exception ex) { Log.Error("Orders: looking for enemies", ex); }
            return result;
        }

        public static MobileParty FindHostile(Hero h, string name)
        {
            var n = Norm(name);
            var list = HostilesNear(h, 30);
            if (list.Count == 0) return null;
            if (n.Length < 2) return list[0];
            return list.FirstOrDefault(p => Norm(p.Name.ToString()) == n)
                   ?? list.FirstOrDefault(p => Norm(p.Name.ToString()).Contains(n) || n.Contains(Norm(p.Name.ToString())))
                   ?? list.FirstOrDefault(p => p.LeaderHero != null && (n.Contains(Norm(p.LeaderHero.Name.ToString())) || Norm(p.LeaderHero.Name.ToString()).Contains(n)));
        }

        // ============================================================ giving and cancelling orders

        /// <summary>Nearest friendly party matching its name or its leader's name (escort target).</summary>
        public static MobileParty FindFriendlyParty(Hero h, string name)
        {
            try
            {
                string n = WorldSim.Norm(name);
                if (n.Length < 2) return null;
                var mine = PartyLedBy(h);
                if (mine == null) return null;
                return MobileParty.All.Where(p => p != null && p.IsActive && p != mine && p != MobileParty.MainParty && p.MapFaction != null && h.MapFaction != null && !p.MapFaction.IsAtWarWith(h.MapFaction)
                                                  && (WorldSim.Norm(p.Name.ToString()).Contains(n) || (p.LeaderHero != null && WorldSim.Norm(p.LeaderHero.Name.ToString()).Contains(n))))
                                      .OrderBy(p => p.GetPosition2D.Distance(mine.GetPosition2D)).FirstOrDefault();
            }
            catch { return null; }
        }

        /// <summary>Legs an order can carry after it, so one reply cannot queue a whole campaign.</summary>
        private const int MaxNextLegs = 4;
        // Orders given to the same hero in the same burst (one reply's actions run one after another in a single frame)
        // are one chain: the later ones become the next legs instead of replacing the first.
        private static string _chainHero;
        private static int _chainTick;
        private const int ChainWindowMs = 3000;

        private static bool ChainOpen(Hero h) => h != null && _chainHero == h.StringId && unchecked(Environment.TickCount - _chainTick) < ChainWindowMs;

        /// <summary>
        /// True right after an order was given to this hero, while the same reply is still being carried out: another
        /// order now is queued after it. Action availability checks can use it to let a second order through.
        /// </summary>
        public static bool AcceptsChainedLeg(Hero h) => ChainOpen(h) && Find(h) is PartyOrder o && o.Type != "approach" && (o.Next == null || o.Next.Count < MaxNextLegs);

        /// <summary>True when the last order given to this hero was queued after another rather than started.</summary>
        public static bool LastGiveWasChained { get; private set; }

        public static bool Give(Hero h, string type, string targetId, string targetName, double days, string then = null, double waitDays = 0, bool auto = false, string reason = null)
        {
            LastGiveWasChained = false;
            if (!CanTakeOrders(h, auto)) return false;
            var cfg = S.Get;
            if (days <= 0) days = cfg.OrderDefaultDays;
            days = Math.Max(0.5, Math.Min(cfg.OrderMaxDays, days));
            waitDays = Math.Max(0, Math.Min(cfg.OrderMaxDays, waitDays));
            bool chainable = !auto && type != "approach";

            // "Go to A and wait, then go to B": a second order in the same reply goes after the first
            if (chainable && ChainOpen(h) && Find(h) is PartyOrder cur && cur.Type != "approach")
            {
                if (cur.Next == null) cur.Next = new List<PartyOrder>();
                if (cur.Next.Count >= MaxNextLegs) { Log.Warn($"Orders: {h.Name} already has {MaxNextLegs} legs queued, {type} {targetName} ignored"); return false; }
                var leg = new PartyOrder { HeroId = h.StringId, Type = type, TargetId = targetId, TargetName = targetName, StayDays = days, Then = then, WaitDays = waitDays };
                cur.Next.Add(leg);
                _chainTick = Environment.TickCount;
                LastGiveWasChained = true;
                Log.Info($"Order for {h.Name}, queued after {cur.Type} {cur.TargetName}: {type} {targetName} ({days:0.#} days)");
                Say(L.F("AIL_e7b1cf1c", $"{h.Name}, después: {LegText(leg)}."));
                return true;
            }

            // A party lent to follow the player stays theirs under the new order
            var was = FindAny(h);
            string tempParty = was?.TempPartyId, home = was?.HomeId;
            if (was != null) Release(was, h, null, sendHome: false);
            var o = Start(h, type, targetId, targetName, days, then, waitDays, auto, reason, null);
            if (tempParty != null && o != null && PartyLedBy(h)?.StringId == tempParty) { o.TempPartyId = tempParty; o.HomeId = home; }
            if (chainable) { _chainHero = h.StringId; _chainTick = Environment.TickCount; }
            Log.Info(auto && reason == "care" ? $"[disease] {h.Name} ({h.Clan?.Name}) takes their party to {targetName} for treatment"
                   : auto ? $"[landless] {h.Name} ({h.Clan?.Name}) patrols on their own around {targetName} ({days:0.#} days)" : $"Order for {h.Name}: {type} {targetName} ({days:0.#} days)");
            return o != null;
        }

        /// <summary>Puts an order (or the next leg of a chain) in force and, outside a scene, sets the party moving.</summary>
        private static PartyOrder Start(Hero h, string type, string targetId, string targetName, double days, string then, double waitDays, bool auto, string reason, List<PartyOrder> next)
        {
            var cfg = S.Get;
            var o = new PartyOrder { HeroId = h.StringId, Type = type, TargetId = targetId, TargetName = targetName, UntilDay = CampaignTime.Now.ToDays + days, StayDays = days, Then = then, WaitDays = waitDays, Auto = auto, Reason = auto ? reason : null,
                                     Next = next != null && next.Count > 0 ? next : null };
            // To join you or keep a meeting they have to get there first. The real deadline starts on arrival
            if (type == "follow" || type == "return") o.UntilDay = CampaignTime.Now.ToDays + Math.Max(days, cfg.OrderMaxDays);
            // Following the player has no end of its own: it lasts until it is called off
            if (type == "follow" && !auto) { o.StayDays = OpenEnded; o.UntilDay = CampaignTime.Now.ToDays + OpenEnded; }
            if (type == "meet") o.StayDays = cfg.OrderDefaultDays;
            Orders.Add(o);
            // Don't move the party while inside a scene (pulling it out of the settlement while you talk breaks the scene).
            // The order starts on the first tick after you leave.
            if (TaleWorlds.MountAndBlade.Mission.Current == null)
            {
                try { var p = PartyLedBy(h); if (p != null) Apply(o, h, p); } catch (Exception ex) { Log.Error("Orders: apply", ex); }
            }
            return o;
        }

        public static bool Cancel(Hero h, bool silent = false)
        {
            var o = FindAny(h);
            if (o == null) return false;
            Release(o, h, silent ? null : L.F("AIL_7cd9f263", $"{h.Name} vuelve a sus asuntos."));
            return true;
        }

        /// <param name="sendHome">A party lent only to follow the player rides back home and is broken up there.</param>
        private static void Release(PartyOrder o, Hero h, string message, bool sendHome = true)
        {
            Orders.Remove(o);
            try
            {
                var p = h?.PartyBelongedTo;
                if (p != null && !p.IsMainParty && p.LeaderHero == h) p.Ai.SetDoNotMakeNewDecisions(false);
            }
            catch (Exception ex) { Log.Error("Orders: release", ex); }
            if (message != null && !o.Auto) Say(message);
            Log.Info($"Order finished ({o.Type} {o.TargetName}) of {h?.Name}");
            if (sendHome && o.TempPartyId != null && o.Type != "home") StartHome(h, o.TempPartyId, o.HomeId);
        }

        // ============================================================ following without a party of one's own

        /// <summary>
        /// A notable, wanderer or lord without a party of their own can still agree to follow the player: AIInfluence
        /// gives them a party for it (a lord a real one of their clan, the others a small temporary one).
        /// </summary>
        public static bool CanFollowWithoutParty(Hero h)
        {
            try
            {
                if (!S.Get.EnableMapOrders || h == null || h == Hero.MainHero || !h.IsAlive || h.IsPrisoner || h.IsChild || h.Age < 18) return false;
                if (h.PartyBelongedTo != null || h.CurrentSettlement == null || FindAny(h) != null && !FindAny(h).Auto) return false;
                return h.IsNotable || h.IsWanderer || h.IsLord;
            }
            catch { return false; }
        }

        /// <summary>The order to follow, for someone on foot: the party is made once the player is out on the map.</summary>
        public static bool FollowWithoutParty(Hero h)
        {
            if (!CanFollowWithoutParty(h)) return false;
            var was = FindAny(h);
            if (was != null) Release(was, h, null, sendHome: false);
            var st = h.CurrentSettlement;
            Orders.Add(new PartyOrder
            {
                HeroId = h.StringId, Type = "follow", TargetName = Hero.MainHero.Name.ToString(), NeedParty = true, HomeId = st?.StringId,
                StayDays = OpenEnded, UntilDay = CampaignTime.Now.ToDays + OpenEnded,
            });
            Log.Info($"Order for {h.Name}: follow the player; their party is made once the player leaves {st?.Name}");
            return true;
        }

        /// <summary>True for a party this system made only so that someone could follow the player.</summary>
        public static bool IsTempParty(MobileParty p) => p != null && Orders.Any(o => o.TempPartyId == p.StringId);

        /// <summary>Makes the party for someone who agreed to follow on foot. Null if it cannot be made now.</summary>
        private static MobileParty MakeFollowParty(Hero h, PartyOrder o)
        {
            var st = h.CurrentSettlement ?? (o.HomeId != null ? Settlement.Find(o.HomeId) : null);
            if (st == null) return null;
            // a lord gets a real party of their clan, the way the game spawns one
            if (h.IsLord && h.Clan != null && !h.Clan.IsEliminated)
            {
                try
                {
                    var real = Helpers.MobilePartyHelper.CreateNewClanMobileParty(h, h.Clan);
                    if (real != null && real.LeaderHero == h) { Log.Info($"Orders: party made for {h.Name} of clan {h.Clan.Name} to follow the player"); return real; }
                }
                catch (Exception ex) { Log.Warn($"Orders: could not make a clan party for {h.Name}: {ex.Message}"); }
            }
            if (h.PartyBelongedTo != null) return null;
            var name = new TaleWorlds.Localization.TextObject(L.F("AIL_e7b1d60a", $"Partida de {h.Name} (temporal)"));
            var party = TaleWorlds.CampaignSystem.Party.PartyComponents.CustomPartyComponent.CreateCustomPartyWithTroopRoster(
                st.GatePosition, 0.5f, st, name, Clan.PlayerClan,
                TaleWorlds.CampaignSystem.Roster.TroopRoster.CreateDummyTroopRoster(), TaleWorlds.CampaignSystem.Roster.TroopRoster.CreateDummyTroopRoster(), h);
            if (party == null) return null;
            AddHeroToPartyAction.Apply(h, party, false);
            party.PartyComponent.ChangePartyLeader(h);
            if (party.LeaderHero != h) { Log.Warn($"Orders: {h.Name} could not lead the party made for them; it is undone"); DissolveTempParty(h, party); return null; }
            o.TempPartyId = party.StringId;
            Log.Info($"Orders: temporary party {party.StringId} made for {h.Name} to follow the player, from {st.Name}");
            return party;
        }

        /// <summary>The lent party goes back to where they came from, and is broken up on arrival.</summary>
        private static void StartHome(Hero h, string partyId, string homeId)
        {
            try
            {
                var p = PartyLedBy(h);
                if (h == null || !h.IsAlive || p == null || p.StringId != partyId) return;
                var home = (homeId != null ? Settlement.Find(homeId) : null) ?? h.HomeSettlement
                           ?? Settlement.All.Where(s => s.IsTown).OrderBy(s => s.GetPosition2D.Distance(p.GetPosition2D)).FirstOrDefault();
                if (home == null) { DissolveTempParty(h, p); return; }
                var o = new PartyOrder { HeroId = h.StringId, Type = "home", TargetId = home.StringId, TargetName = home.Name.ToString(), TempPartyId = partyId, HomeId = home.StringId,
                                         Auto = true, Reason = "home", StayDays = 30, UntilDay = CampaignTime.Now.ToDays + 30 };
                Orders.Add(o);
                if (TaleWorlds.MountAndBlade.Mission.Current == null) Apply(o, h, p);
                Log.Info($"Orders: {h.Name} rides back to {home.Name} with the party lent to follow the player");
            }
            catch (Exception ex) { Log.Error("Orders: send the lent party home", ex); }
        }

        /// <summary>Breaks up a party made only to follow the player. The hero is left on foot (the caller places them).</summary>
        public static void DissolveTempParty(Hero h, MobileParty p)
        {
            try
            {
                Orders.RemoveAll(o => o.HeroId == h?.StringId || p != null && o.TempPartyId == p.StringId);
                if (p == null || !p.IsActive || p.IsMainParty) return;
                if (h != null && h.PartyBelongedTo == p) p.MemberRoster.RemoveTroop(h.CharacterObject);
                DestroyPartyAction.Apply(null, p);
                Log.Info($"Orders: temporary party of {h?.Name} broken up");
            }
            catch (Exception ex) { Log.Error("Orders: break up the lent party", ex); }
        }

        /// <summary>Home again: the hero stays in their settlement and the lent party is gone.</summary>
        private static void ArriveHome(PartyOrder o, Hero h, MobileParty p, Settlement home)
        {
            Orders.Remove(o);
            try
            {
                if (p.MapEvent != null) { Orders.Add(o); return; }
                home = home ?? Settlement.All.Where(s => s.IsTown).OrderBy(s => s.GetPosition2D.Distance(p.GetPosition2D)).FirstOrDefault();
                if (home == null) { DissolveTempParty(h, p); return; }
                TeleportHeroAction.ApplyImmediateTeleportToSettlement(h, home);
                if (p.IsActive) DestroyPartyAction.Apply(null, p);
                Log.Info($"Orders: {h.Name} is back in {home.Name}; the lent party is broken up");
            }
            catch (Exception ex) { Log.Error("Orders: arrive home", ex); }
        }

        private static void Say(string msg) => InformationManager.DisplayMessage(new InformationMessage(msg, Colors.Yellow));

        // ============================================================ tick

        private static double _lastHour = -1;
        private static readonly Dictionary<string, string> LastState = new Dictionary<string, string>();

        /// <summary>Call often. Runs the order tick once per game hour.</summary>
        public static void Pump()
        {
            if (Campaign.Current == null || Orders.Count == 0) return;
            double hour = CampaignTime.Now.ToHours;
            if (_lastHour >= 0 && hour >= _lastHour && hour - _lastHour < 1.0) return;
            _lastHour = hour;
            OnHourlyTick();
        }

        /// <summary>Logs what the party is doing, only when it changes. Useful for debugging stuck orders.</summary>
        private static void Trace(PartyOrder o, Hero h, MobileParty p)
        {
            try
            {
                string state = L.F("AIL_d7c17160", $"{o.Type} {o.TargetName} | en={p.CurrentSettlement?.Name?.ToString() ?? "-"} def={p.DefaultBehavior} corto={p.ShortTermBehavior} obj={p.TargetSettlement?.Name?.ToString() ?? p.TargetParty?.Name?.ToString() ?? "-"} llegó={o.Arrived} batalla={p.MapEvent != null}");
                if (LastState.TryGetValue(o.HeroId, out var old) && old == state) return;
                LastState[o.HeroId] = state;
                Log.Info($"[order] {h.Name}: {state}");
            }
            catch { }
        }

        public static void OnHourlyTick()
        {
            if (Orders.Count == 0) return;
            double now = CampaignTime.Now.ToDays;
            foreach (var o in Orders.ToList())
            {
                try
                {
                    var h = Hero.Find(o.HeroId);
                    if (h == null || h.IsDead) { Orders.Remove(o); continue; }
                    // On foot, waiting to set out: the party is made once the player is out on the map, not in a scene
                    // or a settlement (AIInfluence defers it the same way)
                    if (o.NeedParty)
                    {
                        if (h.IsPrisoner || h.PartyBelongedTo != null && PartyLedBy(h) == null) { Release(o, h, null); continue; }
                        if (PartyLedBy(h) == null)
                        {
                            if (TaleWorlds.MountAndBlade.Mission.Current != null || MobileParty.MainParty == null || MobileParty.MainParty.CurrentSettlement != null) continue;
                            if (Hero.MainHero.MapFaction != null && h.MapFaction != null && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction)) { Release(o, h, null); continue; }
                            if (MakeFollowParty(h, o) == null) { Release(o, h, L.F("AIL_e7b1d60b", $"{h.Name} no ha podido salir tras de ti.")); continue; }
                            Say(L.F("AIL_e7b1d60c", $"La partida de {h.Name} se pone en camino para seguirte."));
                        }
                        o.NeedParty = false;
                    }
                    var p = PartyLedBy(h);
                    // Defeated, captured or without a party: the deal is left undone, and both sides know it
                    if (p == null || h.IsPrisoner)
                    {
                        if (o.Type != "approach" && !o.Auto) Remember(h, h.IsPrisoner ? $"you were taken prisoner and could not do what you had agreed with them ({o.Type} {o.TargetName})"
                                                 : $"you lost your party and could not do what you had agreed with them ({o.Type} {o.TargetName})");
                        Release(o, h, o.Type == "approach" ? null : L.F("AIL_5e0b93a7", $"{h.Name} ya no puede cumplir lo que acordasteis: {(h.IsPrisoner ? L.T("AIL_91c2d4f6", "ha caído prisionero") : L.T("AIL_2f7a8b13", "se ha quedado sin partida"))}."));
                        continue;
                    }
                    // The game pulled them into someone else's army: say so instead of dropping the order silently
                    if (p.Army != null && p.Army.LeaderParty != p && p.Army.LeaderParty != MobileParty.MainParty)
                    {
                        string boss = p.Army.LeaderParty?.LeaderHero?.Name?.ToString() ?? p.Army.Name?.ToString() ?? "?";
                        if (o.Type != "approach" && !o.Auto) Remember(h, $"you were called into the army of {boss} and had to leave undone what you had agreed with them ({o.Type} {o.TargetName}). You owe them an explanation");
                        Release(o, h, o.Type == "approach" ? null : L.F("AIL_c4d81e09", $"{h.Name} ha sido llamado al ejército de {boss} y deja lo que acordasteis."));
                        continue;
                    }
                    if (h.MapFaction != null && Hero.MainHero.MapFaction != null && h.MapFaction != Hero.MainHero.MapFaction
                        && h.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction) && (o.Type == "follow" || o.Type == "meet" || o.Type == "return"))
                    { Release(o, h, L.F("AIL_35617af2", $"{h.Name} ya no acude a tu llamada: ahora sois enemigos.")); continue; }
                    if (o.Type == "home" && now >= o.UntilDay) { if (p.MapEvent == null) ArriveHome(o, h, p, Settlement.Find(o.TargetId) ?? h.HomeSettlement ?? p.HomeSettlement); continue; }
                    if (now >= o.UntilDay) { Done(o, h, EndText(o, h)); continue; }
                    if (p.MapEvent != null) continue;                                                     // in a battle
                    Apply(o, h, p);
                    if (Orders.Contains(o)) Trace(o, h, p);
                }
                catch (Exception ex) { Log.Error("Orders: tick", ex); }
            }
        }

        /// <summary>Notes in the character's memory what happened with an order, so they know it next time you talk.</summary>
        private static void Remember(Hero h, string what)
        {
            try
            {
                var mem = Memory.MemoryStore.Find(h.StringId);
                if (mem == null) return;
                mem.Turns.Add(new Memory.ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays, Text = "[Since you last spoke: " + what + ".]" });
            }
            catch { }
        }

        /// <summary>
        /// Normal end of an order (time's up, target reached or lost). The next agreed leg starts, if there is one;
        /// otherwise, if they agreed to come back afterwards, they do.
        /// </summary>
        private static void Done(PartyOrder o, Hero h, string message)
        {
            bool back = o.Then == "follow" && o.Type != "follow" && o.Type != "return" && !o.Auto;
            var rest = o.Next != null ? new List<PartyOrder>(o.Next) : new List<PartyOrder>();
            Release(o, h, message, sendHome: false);
            try
            {
                // a lent party keeps going with the next leg; with nothing left to do, it rides home
                if (o.TempPartyId != null)
                {
                    ContinueAfter(o, h, back, rest);
                    var next = FindAny(h);
                    if (next != null) { next.TempPartyId = o.TempPartyId; next.HomeId = o.HomeId; }
                    else StartHome(h, o.TempPartyId, o.HomeId);
                    return;
                }
                ContinueAfter(o, h, back, rest);
            }
            catch (Exception ex) { Log.Error("Orders: return", ex); }
        }

        /// <summary>After an order ends: the next agreed leg, or riding back to the player when that was agreed.</summary>
        private static void ContinueAfter(PartyOrder o, Hero h, bool back, List<PartyOrder> rest)
        {
            try
            {
                if (h == null || h.IsPrisoner || !h.IsAlive || !CanTakeOrders(h)) return;
                var mine = Hero.MainHero.MapFaction;
                bool enemies = h.MapFaction != null && mine != null && h.MapFaction != mine && h.MapFaction.IsAtWarWith(mine);
                if (rest.Count > 0)
                {
                    // "...and come back" in the middle of a chain: ride back to the player first, then go on
                    if (back && !enemies) rest.Insert(0, new PartyOrder { HeroId = h.StringId, Type = "return", TargetName = Hero.MainHero.Name.ToString(), StayDays = S.Get.OrderMaxDays });
                    // legs that only make sense with a friend are dropped once you are enemies
                    if (enemies) rest.RemoveAll(l => l.Type == "follow" || l.Type == "meet" || l.Type == "return");
                    if (rest.Count == 0) return;
                    var leg = rest[0];
                    var after = rest.Skip(1).ToList();
                    if (enemies) foreach (var l in after) l.Then = null;
                    Start(h, leg.Type, leg.TargetId, leg.TargetName, leg.StayDays > 0 ? leg.StayDays : S.Get.OrderDefaultDays, enemies ? null : leg.Then, leg.WaitDays, false, null, after);
                    Log.Info($"Order for {h.Name}, next leg: {leg.Type} {leg.TargetName}" + (after.Count > 0 ? $" ({after.Count} more after it)" : ""));
                    Say(L.F("AIL_e7b1cf1d", $"{h.Name} sigue con lo acordado: {LegText(leg)}."));
                    return;
                }
                if (!back || enemies) return;
                if (Give(h, "follow", null, Hero.MainHero.Name.ToString(), 0)) Say(L.F("AIL_c7a9d5e1", $"{h.Name} vuelve contigo, como acordasteis."));
            }
            catch (Exception ex) { Log.Error("Orders: return", ex); }
        }

        private static string EndText(PartyOrder o, Hero h)
        {
            // with more legs to go, the next one's own message says what happens
            if (o.Next != null && o.Next.Count > 0 && !o.Auto) return null;
            switch (o.Type)
            {
                case "return": return L.F("AIL_23bfd455", $"{h.Name} no ha conseguido alcanzarte y vuelve a sus asuntos.");
                case "follow": return o.Arrived ? L.F("AIL_0a0689c9", $"{h.Name} deja de seguirte y vuelve a sus asuntos.") : L.F("AIL_23bfd455", $"{h.Name} no ha conseguido alcanzarte y vuelve a sus asuntos.");
                case "meet": return o.Arrived ? L.F("AIL_b0a43892", $"{h.Name} se cansa de esperarte en {o.TargetName} y se marcha.") : L.F("AIL_5c3efc53", $"{h.Name} no ha podido llegar a {o.TargetName} y lo deja.");
                case "wait": return L.F("AIL_645733f1", $"{h.Name} deja de esperar y vuelve a sus asuntos.");
                case "patrol": return L.F("AIL_55479028", $"{h.Name} termina su patrulla en {o.TargetName}.");
                case "approach": return null;      // never caught up with you, just drop it
                default: return L.F("AIL_7cd9f263", $"{h.Name} vuelve a sus asuntos.");
            }
        }

        /// <summary>Sets (or keeps) the party doing what was agreed. Ends the order if it no longer makes sense.</summary>
        private static void Apply(PartyOrder o, Hero h, MobileParty p)
        {
            var nav = p.IsCurrentlyAtSea ? p.NavigationCapability : MobileParty.NavigationType.Default;
            var st = o.Type == "follow" || o.Type == "wait" || o.Type == "attack" || o.Type == "approach" || o.Type == "return" || o.Type == "escort" || string.IsNullOrEmpty(o.TargetId) ? null : Settlement.Find(o.TargetId);
            p.Ai.SetDoNotMakeNewDecisions(true);
            // A party lent to a notable or wanderer has no soldiers: nobody goes after it while it rides with the player
            // or back home (AIInfluence keeps such parties out of fights the same way)
            if (o.TempPartyId != null && p.StringId == o.TempPartyId) try { p.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(2f)); } catch { }

            switch (o.Type)
            {
                case "home":
                {
                    var home = st ?? h.HomeSettlement;
                    if (home == null) { DissolveTempParty(h, p); return; }
                    if (p.CurrentSettlement == home || p.GetPosition2D.Distance(home.GatePosition.ToVec2()) <= 2f) { ArriveHome(o, h, p, home); return; }
                    if (p.DefaultBehavior != AiBehavior.GoToSettlement || p.TargetSettlement != home) p.SetMoveGoToSettlement(home, nav, false);
                    break;
                }

                case "follow":
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != MobileParty.MainParty)
                        p.SetMoveEscortParty(MobileParty.MainParty, nav, false);
                    if (!o.Arrived && p.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) <= 4f)
                    {
                        o.Arrived = true;
                        o.UntilDay = CampaignTime.Now.ToDays + Math.Max(0.5, o.StayDays);
                        Say(L.F("AIL_85e6e5d4", $"{h.Name} se une a tu marcha con {p.MemberRoster.TotalManCount} hombres."));
                    }
                    break;

                case "wait":
                    if (p.DefaultBehavior != AiBehavior.Hold) p.SetMoveModeHold();
                    break;

                case "return":
                    // A leg of a chain: ride back to the player; once beside them this leg is done and the next begins
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != MobileParty.MainParty)
                        p.SetMoveEscortParty(MobileParty.MainParty, nav, false);
                    if (p.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) <= 4f)
                    { Done(o, h, L.F("AIL_e7b1cf1e", $"{h.Name} ha vuelto a tu lado, como acordasteis.")); return; }
                    break;

                case "escort":
                {
                    // Escort ANOTHER party (a caravan, villagers, a relative) by riding alongside it
                    var ward = MobileParty.All.FirstOrDefault(x => x != null && x.StringId == o.TargetId);
                    if (ward == null || !ward.IsActive || ward.MapEvent != null && ward.MapEvent.HasWinner) { Done(o, h, o.Next != null && o.Next.Count > 0 ? null : L.F("AIL_49b6ffff", $"{h.Name} ya no tiene a quién escoltar y vuelve a sus asuntos.")); return; }
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != ward) p.SetMoveEscortParty(ward, nav, false);
                    break;
                }

                case "goto":
                case "meet":
                    if (st == null) { Done(o, h, null); return; }      // that place is gone: go on with the rest of the chain, if any
                    // Arrived if inside or standing at the gate (in some places the party never actually "enters")
                    bool there = p.CurrentSettlement == st || p.GetPosition2D.Distance(st.GatePosition.ToVec2()) <= 2f;
                    if (there)
                    {
                        if (!o.Arrived)
                        {
                            o.Arrived = true;
                            if (o.Type == "meet")
                            {
                                // the wait starts counting on arrival
                                o.UntilDay = CampaignTime.Now.ToDays + Math.Max(0.5, o.StayDays);
                                Say(L.F("AIL_7d054e89", $"{h.Name} ha llegado a {st.Name} y te espera allí."));
                            }
                            else if (o.WaitDays > 0)
                            {
                                // "Go and wait N days": stays there, and the time counts from arrival
                                o.UntilDay = CampaignTime.Now.ToDays + o.WaitDays;
                                if (!o.Auto) Say(L.F("AIL_c7a9d5e2", $"{h.Name} ha llegado a {st.Name} y se queda allí {o.WaitDays:0.#} día(s)."));
                            }
                            else if (o.Next != null && o.Next.Count > 0 && !o.Auto)
                            {
                                // a chain: this leg is done on arrival, and the next one starts
                                Done(o, h, L.F("AIL_518def5c", $"{h.Name} ha llegado a {st.Name}."));
                                return;
                            }
                            else
                            {
                                bool back = o.Then == "follow";
                                Release(o, h, back ? L.F("AIL_d65def98", $"{h.Name} ha llegado a {st.Name} y emprende el regreso hacia ti.") : L.F("AIL_518def5c", $"{h.Name} ha llegado a {st.Name}."));
                                if (back) Give(h, "follow", null, Hero.MainHero.Name.ToString(), 0);
                                return;
                            }
                        }
                        // hold in place: inside stays inside, and at the gate it doesn't keep going in and out
                        if (p.DefaultBehavior != AiBehavior.Hold) p.SetMoveModeHold();
                    }
                    else if (p.DefaultBehavior != AiBehavior.GoToSettlement || p.TargetSettlement != st)
                        p.SetMoveGoToSettlement(st, nav, false);
                    break;

                case "patrol":
                    if (st == null) { Release(o, h, null); return; }
                    if (p.DefaultBehavior != AiBehavior.PatrolAroundPoint || p.TargetSettlement != st)
                        p.SetMovePatrolAroundSettlement(st, nav, false);
                    break;

                case "attack":
                {
                    var target = MobileParty.All.FirstOrDefault(x => x != null && x.StringId == o.TargetId);
                    if (target == null || !target.IsActive || target.MemberRoster.TotalManCount == 0)
                    { Done(o, h, L.F("AIL_9480373b", $"{h.Name} ya no tiene a quién perseguir ({o.TargetName}).")); return; }
                    if (target.MapFaction == null || !target.MapFaction.IsAtWarWith(p.MapFaction) || target.CurrentSettlement != null)
                    { Done(o, h, L.F("AIL_6f87960d", $"{h.Name} abandona la persecución de {o.TargetName}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.EngageParty || p.TargetParty != target)
                        p.SetMoveEngageParty(target, nav);
                    break;
                }

                case "besiege":
                    if (st == null || !st.IsFortification) { Release(o, h, null); return; }
                    if (st.MapFaction == null || !st.MapFaction.IsAtWarWith(p.MapFaction))
                    { Done(o, h, st.MapFaction == p.MapFaction ? L.F("AIL_dd1a051b", $"{st.Name} ha caído. {h.Name} da el asedio por terminado.") : L.F("AIL_4eae4e56", $"{h.Name} levanta el asedio de {st.Name}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.BesiegeSettlement || p.TargetSettlement != st)
                        p.SetMoveBesiegeSettlement(st, nav);
                    break;

                case "raid":
                    if (st == null || !st.IsVillage) { Release(o, h, null); return; }
                    if (st.Village.VillageState == Village.VillageStates.Looted || st.IsRaided)
                    { Done(o, h, L.F("AIL_399e0d5f", $"{st.Name} ha sido saqueada. {h.Name} vuelve a sus asuntos.")); return; }
                    if (st.MapFaction == null || !st.MapFaction.IsAtWarWith(p.MapFaction))
                    { Done(o, h, L.F("AIL_c351bc8a", $"{h.Name} renuncia a saquear {st.Name}.")); return; }
                    if (p.DefaultBehavior != AiBehavior.RaidSettlement || p.TargetSettlement != st)
                        p.SetMoveRaidSettlement(st, nav, false);
                    break;

                case "approach":
                    // Coming over on their own to talk. They keep riding at your side, also once they are close, while
                    // their opening line is prepared and they wait to be received: releasing them here let the game AI
                    // take them away again before the "wants to talk" window came up. Dialogue.Initiative ends the order.
                    if (p.DefaultBehavior != AiBehavior.EscortParty || p.TargetParty != MobileParty.MainParty)
                        p.SetMoveEscortParty(MobileParty.MainParty, nav, false);
                    if (o.Arrived)
                    {
                        // Close already, but nobody is handling the talk any more (a reload, or it could not start): let them go
                        if (!Dialogue.Initiative.IsApproaching(h)) { Log.Info($"Map initiative: {h.Name} no longer has a talk waiting, released"); Release(o, h, null); }
                        return;
                    }
                    if (p.GetPosition2D.Distance(MobileParty.MainParty.GetPosition2D) <= 3.5f)
                    {
                        o.Arrived = true;
                        // Safety net only: the initiative gives up much sooner if they never get close enough to ask
                        o.UntilDay = Math.Max(o.UntilDay, CampaignTime.Now.ToDays + 1.0);
                        MapInitiative.Arrived(h);
                        if (!Dialogue.Initiative.IsApproaching(h)) Release(o, h, null);
                        return;
                    }
                    break;

                default:
                    Release(o, h, null);
                    break;
            }
        }

        // ============================================================ armies

        /// <summary>The NPC's party joins the player's army. Creates the army if the player has none and is allowed to.</summary>
        public static string JoinPlayerArmy(Hero h)
        {
            var p = PartyLedBy(h);
            var main = MobileParty.MainParty;
            if (p == null || main == null) return null;
            if (main.Army == null)
            {
                if (!(Hero.MainHero.MapFaction is Kingdom k)) return null;
                var gather = Settlement.All.Where(s => s.IsFortification || s.IsVillage).OrderBy(s => s.GetPosition2D.Distance(main.GetPosition2D)).FirstOrDefault();
                k.CreateArmy(Hero.MainHero, gather, Army.ArmyTypes.Patrolling);
            }
            if (main.Army == null || main.Army.LeaderParty != main) return null;
            Cancel(h, silent: true);
            if (p.Army != null && p.Army != main.Army)
            {
                if (p.Army.LeaderParty == p) DisbandArmyAction.ApplyByUnknownReason(p.Army); else p.Army = null;
            }
            p.Army = main.Army;
            p.SetMoveEscortParty(main, p.IsCurrentlyAtSea ? p.NavigationCapability : MobileParty.NavigationType.Default, false);
            return L.F("AIL_675de4ca", $"{h.Name} se une a tu ejército con {p.MemberRoster.TotalManCount} hombres.");
        }

        /// <summary>The NPC gathers their own army. The game AI runs it afterwards.</summary>
        public static string FormOwnArmy(Hero h, Settlement target)
        {
            var p = PartyLedBy(h);
            if (p == null || p.Army != null || !(h.MapFaction is Kingdom k)) return null;
            Cancel(h, silent: true);
            bool hostile = target != null && target.MapFaction != null && target.MapFaction.IsAtWarWith(k);
            var type = hostile ? (target.IsVillage ? Army.ArmyTypes.Raider : Army.ArmyTypes.Besieger)
                               : (target != null ? Army.ArmyTypes.Defender : Army.ArmyTypes.Patrolling);
            // The settlement passed to CreateArmy is the GATHERING POINT (Army.Gather), not the target. Never gather at
            // the enemy's gates: with a hostile target (or none) use the nearest friendly fortification.
            Settlement gather = hostile || target == null ? null : target;
            if (gather == null)
            {
                var from = hostile ? target.GetPosition2D : p.GetPosition2D;
                gather = Settlement.All.Where(s => s.IsFortification && s.MapFaction == k && !s.IsUnderSiege).OrderBy(s => s.GetPosition2D.Distance(from)).FirstOrDefault()
                         ?? Settlement.All.Where(s => (s.IsFortification || s.IsVillage) && s.MapFaction == k).OrderBy(s => s.GetPosition2D.Distance(p.GetPosition2D)).FirstOrDefault();
            }
            if (gather == null) return null;
            k.CreateArmy(h, gather, type);
            if (p.Army == null) return null;
            return L.F("AIL_55cee6ef", $"{h.Name} reúne un ejército") + (target != null ? L.F("AIL_927a75ae", $" con la vista puesta en {target.Name}.") : ".");
        }

        public static string LeaveArmy(Hero h)
        {
            var p = PartyLedBy(h);
            if (p?.Army == null) return null;
            if (p.Army.LeaderParty == p) { DisbandArmyAction.ApplyByUnknownReason(p.Army); return L.F("AIL_ae49ce20", $"{h.Name} disuelve su ejército."); }
            p.Army = null;
            p.Ai.SetDoNotMakeNewDecisions(false);
            return L.F("AIL_a7d1636b", $"{h.Name} abandona el ejército con sus hombres.");
        }
    }
}
