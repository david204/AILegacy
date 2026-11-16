using System;
using System.Collections.Generic;
using AILegacy.Memory;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace AILegacy.Dialogue
{
    /// <summary>Who the player is talking to: a hero (lord, notable, companion...) or a regular resident of a settlement.</summary>
    public class Speaker
    {
        public Hero Hero { get; private set; }
        public CharacterObject Character { get; private set; }
        public string Key { get; private set; }
        public HeroMemory Memory { get; private set; }
        public Settlement ResidentSettlement { get; private set; }
        /// <summary>Lordless party (bandits, caravan, villagers on the road) whose leader is speaking. Null otherwise.</summary>
        public TaleWorlds.CampaignSystem.Party.MobileParty Band { get; private set; }

        public bool IsHero => Hero != null;

        /// <summary>What's happening right now and drives the talk (e.g. a guard coming to confront you). Not saved.</summary>
        public string Situation;
        public string SituationKind;

        /// <summary>
        /// What a resident or party leader does, from the game branch the player met them in (keeper of this tavern,
        /// master of the arena...). English, for the prompt. Null: worked out from their occupation. Not saved.
        /// </summary>
        public string Role;

        /// <summary>Met in one of the game's branches for children, whatever age their character template says.</summary>
        public bool KnownChild;

        /// <summary>A child: no romance, no actions, and a prompt that keeps them a child.</summary>
        public bool IsMinor => KnownChild || (IsHero ? (Hero.IsChild || Hero.Age < 16f) : Character != null && Character.Age < 16f);

        public string Name
        {
            get
            {
                if (IsHero) return Hero.Name.ToString();
                var given = Memory?.Profile?.GivenName;
                return string.IsNullOrWhiteSpace(given) ? Character.Name.ToString() : given;
            }
        }

        public bool IsFemale => IsHero ? Hero.IsFemale : Character.IsFemale;
        public float Age => IsHero ? Hero.Age : Character.Age;
        public CultureObject Culture => IsHero ? Hero.Culture : Character.Culture;

        public Settlement Settlement => IsHero ? (Hero.CurrentSettlement ?? Hero.PartyBelongedTo?.CurrentSettlement) : ResidentSettlement;

        /// <summary>Used for local news.</summary>
        public Settlement HomeSettlement => IsHero ? (Hero.HomeSettlement ?? Settlement) : ResidentSettlement;

        public IFaction Faction => IsHero ? Hero.MapFaction : (ResidentSettlement?.MapFaction ?? Band?.MapFaction);

        public static Speaker FromHero(Hero h)
        {
            return new Speaker { Hero = h, Character = h.CharacterObject, Key = h.StringId, Memory = MemoryStore.Get(h.StringId, h.Name.ToString()) };
        }

        public static Speaker FromResident(CharacterObject c, IAgent agent)
        {
            var settlement = Settlement.CurrentSettlement;
            var key = $"res:{settlement?.StringId ?? "none"}:{c?.StringId}:{FaceKey(agent)}";
            var mem = MemoryStore.Get(key, c.Name.ToString(), resident: true);
            World.ResidentFaces.Attach(mem, agent, settlement, c);
            return new Speaker
            {
                Character = c,
                Key = key,
                ResidentSettlement = settlement,
                Memory = mem,
            };
        }

        /// <summary>
        /// A resident's identity within the session is their face. The agent changes when they move from the street to the
        /// tavern, but the face stays the same, so they still remember you. Falls back to the agent if it can't be read.
        /// </summary>
        private static string FaceKey(IAgent agent)
        {
            try
            {
                if (agent is TaleWorlds.MountAndBlade.Agent a)
                {
                    var f = a.BodyPropertiesValue.StaticProperties;
                    ulong h = 1469598103934665603UL;
                    foreach (ulong k in new[] { f.KeyPart1, f.KeyPart2, f.KeyPart3, f.KeyPart4, f.KeyPart5, f.KeyPart6, f.KeyPart7, f.KeyPart8 })
                        h = (h ^ k) * 1099511628211UL;
                    return h.ToString("x");
                }
            }
            catch { }
            return agent != null ? Math.Abs(agent.GetHashCode()).ToString() : "0";
        }

        /// <summary>Leader of a lordless party met on the map. Memory only lasts the session.</summary>
        public static Speaker FromBand(CharacterObject c, TaleWorlds.CampaignSystem.Party.MobileParty party)
        {
            var key = $"band:{party?.StringId ?? "none"}";
            return new Speaker
            {
                Character = c,
                Key = key,
                Band = party,
                Memory = MemoryStore.Get(key, c.Name.ToString(), resident: true),
            };
        }

        /// <summary>Tags matched against "applicable" in world_data to pick which world knowledge this speaker gets.</summary>
        public HashSet<string> Tags
        {
            get
            {
                var t = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "all" };
                if (Culture != null) t.Add("culture:" + Culture.StringId);
                if (Faction != null) t.Add("kingdom:" + Faction.StringId);
                if (IsHero)
                {
                    var h = Hero;
                    t.Add(h.StringId);
                    if (h.IsLord) t.Add("lords");
                    if (h.IsKingdomLeader) t.Add("faction_leaders");
                    if (h.IsClanLeader) t.Add("clan_leaders");
                    if (h.IsWanderer) t.Add("wanderers");
                    if (h.IsPlayerCompanion || h.IsWanderer) t.Add("companions");
                    if (h.IsMerchant) t.Add("merchants");
                    if (h.IsGangLeader) t.Add("gang_leaders");
                    if (h.IsNotable)
                    {
                        if (h.IsHeadman || h.IsRuralNotable) t.Add("village_notables");
                        else t.Add("town_notables");
                    }
                }
                else
                {
                    var s = ResidentSettlement;
                    if (Band != null) t.Add(Band.MapFaction != null && Band.MapFaction.IsBanditFaction ? "bandits" : Band.IsCaravan ? "merchants" : "villagers");
                    else if (s != null && s.IsVillage) t.Add("villagers"); else t.Add("townsfolk");
                    if (Character.Occupation == Occupation.Soldier || Character.Occupation == Occupation.Guard) t.Add("guards");
                }
                return t;
            }
        }
    }
}
