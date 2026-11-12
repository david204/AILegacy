using System;
using System.Collections.Generic;
using AILegacy.Memory;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace AILegacy.Dialogue
{
    /// <summary>Con quién hablamos: un héroe (lord, notable, compañero...) o una persona corriente de un asentamiento.</summary>
    public class Speaker
    {
        public Hero Hero { get; private set; }
        public CharacterObject Character { get; private set; }
        public string Key { get; private set; }
        public HeroMemory Memory { get; private set; }
        public Settlement ResidentSettlement { get; private set; }
        /// <summary>Partida sin señor (bandidos, caravana, aldeanos de camino) cuyo jefe es quien habla. null en los demás casos.</summary>
        public TaleWorlds.CampaignSystem.Party.MobileParty Band { get; private set; }

        public bool IsHero => Hero != null;

        /// <summary>Lo que está pasando ahora mismo y manda sobre la charla (p. ej. un guardia que viene a pedir cuentas). No se guarda.</summary>
        public string Situation;
        public string SituationKind;

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

        /// <summary>Asentamiento donde está ahora (si está en alguno).</summary>
        public Settlement Settlement => IsHero ? (Hero.CurrentSettlement ?? Hero.PartyBelongedTo?.CurrentSettlement) : ResidentSettlement;

        /// <summary>Asentamiento "de casa" (para noticias locales).</summary>
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
            return new Speaker
            {
                Character = c,
                Key = key,
                ResidentSettlement = settlement,
                Memory = MemoryStore.Get(key, c.Name.ToString(), resident: true),
            };
        }

        /// <summary>
        /// Identidad de un vecino dentro de la sesión: su cara. El agente cambia al pasar de la calle a la taberna, pero la
        /// persona (su aspecto) es la misma, así que te sigue recordando. Si no se puede leer, vale el propio agente.
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

        /// <summary>El jefe de una partida sin señor con la que hablas en el mapa (bandidos, caravana, aldeanos). Memoria de sesión.</summary>
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

        /// <summary>Etiquetas para decidir qué conocimiento del mundo le corresponde (applicable en world_data).</summary>
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
