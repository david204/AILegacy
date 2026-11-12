using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Memory;
using AILegacy.Settings;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.Library;

namespace AILegacy.World
{
    /// <summary>Un clan echado de un reino: no puede volver a él hasta que su rey lo perdone.</summary>
    public class Banishment
    {
        [JsonProperty("clan")] public string ClanId;
        [JsonProperty("kingdom")] public string KingdomId;
        [JsonProperty("day")] public double Day;
        [JsonProperty("by")] public string RulerId;        // el rey que lo echó
        [JsonProperty("by_name")] public string RulerName;
    }

    /// <summary>
    /// Destierros. Cuando un rey expulsa a un clan (por su propia decisión en AI Legacy o por una votación del juego), ese clan
    /// no puede volver a jurarle lealtad, pasarse a él ni servirle como mercenario hasta que el rey lo perdone. El destierro
    /// acaba también si el reino cambia de rey (ajuste). El rey lo sabe y el desterrado también.
    /// </summary>
    internal static class Banished
    {
        private static List<Banishment> All => WorldSim.State.Banished;
        public static bool Enabled => S.Get.BanishedClansCannotReturn;

        public static Banishment Find(Clan clan, Kingdom k) =>
            clan == null || k == null || WorldSim.State == null ? null : All.FirstOrDefault(b => b.ClanId == clan.StringId && b.KingdomId == k.StringId);

        public static bool IsBanned(Clan clan, Kingdom k)
        {
            try { return Enabled && Find(clan, k) != null; } catch { return false; }
        }

        public static bool HasAny(Clan clan)
        {
            try { return clan != null && WorldSim.State != null && All.Any(b => b.ClanId == clan.StringId); } catch { return false; }
        }

        public static void Ban(Clan clan, Kingdom k)
        {
            try
            {
                if (clan == null || k == null || WorldSim.State == null) return;
                All.RemoveAll(b => b.ClanId == clan.StringId && b.KingdomId == k.StringId);
                All.Add(new Banishment { ClanId = clan.StringId, KingdomId = k.StringId, Day = CampaignTime.Now.ToDays, RulerId = k.Leader?.StringId, RulerName = k.Leader?.Name?.ToString() });
                Log.Info($"Destierro: el clan {clan.Name} queda desterrado de {k.Name}");
            }
            catch (Exception ex) { Log.Error("Destierro", ex); }
        }

        public static bool Pardon(Clan clan, Kingdom k)
        {
            try
            {
                if (clan == null || k == null || WorldSim.State == null) return false;
                bool any = All.RemoveAll(b => b.ClanId == clan.StringId && b.KingdomId == k.StringId) > 0;
                if (any) Log.Info($"Destierro: {k.Name} perdona al clan {clan.Name}");
                return any;
            }
            catch { return false; }
        }

        /// <summary>El intento de volver que el juego iba a hacer: ¿se deja?</summary>
        public static bool AllowJoin(Clan clan, Kingdom k)
        {
            if (!IsBanned(clan, k)) return true;
            try
            {
                if (clan == Clan.PlayerClan)
                    WorldSim.Say(L.F("AIL_b7d2f010", $"Tu clan está desterrado de {k.Name}. No puedes volver hasta que su soberano te perdone."), Colors.Red);
                Log.Info($"Destierro: se impide que el clan {clan.Name} vuelva a {k.Name}");
            }
            catch { }
            return false;
        }

        public static void OnDailyTick()
        {
            try
            {
                if (WorldSim.State == null || All.Count == 0) return;
                bool endsWithRuler = S.Get.BanishmentEndsWithRuler;
                foreach (var b in All.ToList())
                {
                    var k = WorldSim.FindKingdom(b.KingdomId);
                    var clan = Clan.All.FirstOrDefault(c => c.StringId == b.ClanId);
                    if (k == null || k.IsEliminated || clan == null || clan.IsEliminated) { All.Remove(b); continue; }
                    if (endsWithRuler && !string.IsNullOrEmpty(b.RulerId) && k.Leader != null && k.Leader.StringId != b.RulerId)
                    {
                        All.Remove(b);
                        if (clan == Clan.PlayerClan)
                            WorldSim.Say(L.F("AIL_b7d2f011", $"{k.Name} tiene nuevo soberano: el destierro de tu clan queda sin efecto."), Colors.Green);
                    }
                }
            }
            catch (Exception ex) { Log.Error("Destierros: repaso diario", ex); }
        }

        private static string Ago(double day)
        {
            int d = (int)Math.Max(0, CampaignTime.Now.ToDays - day);
            return d <= 0 ? "today" : d == 1 ? "yesterday" : d + " days ago";
        }

        /// <summary>Para los datos del rey: a quién tiene desterrado.</summary>
        public static string DescribeForRuler(Kingdom k)
        {
            try
            {
                if (k == null || WorldSim.State == null) return "";
                var list = All.Where(b => b.KingdomId == k.StringId)
                              .Select(b => (b, c: Clan.All.FirstOrDefault(x => x.StringId == b.ClanId))).Where(x => x.c != null && !x.c.IsEliminated).ToList();
                if (list.Count == 0) return "";
                return "Clans banished from your realm (they cannot return unless you pardon them): " +
                       string.Join("; ", list.Select(x => $"{x.c.Name}{(x.c == Clan.PlayerClan ? " (the clan of " + Hero.MainHero.Name + ")" : "")}, cast out {Ago(x.b.Day)}"));
            }
            catch { return ""; }
        }

        /// <summary>Para la ficha de alguien de un clan desterrado.</summary>
        public static string DescribeForClan(Clan clan)
        {
            try
            {
                if (clan == null || WorldSim.State == null) return "";
                var list = All.Where(b => b.ClanId == clan.StringId).Select(b => (b, k: WorldSim.FindKingdom(b.KingdomId))).Where(x => x.k != null).ToList();
                return list.Count == 0 ? "" : "banished from " + string.Join(" and ", list.Select(x => $"{x.k.Name} ({Ago(x.b.Day)}, by {x.b.RulerName ?? "its ruler"})")) + ": it may not return there unless pardoned";
            }
            catch { return ""; }
        }

        /// <summary>¿Hay que conservar este clan aunque el juego quiera disolverlo por no tener reino ni tierras?</summary>
        public static bool KeepAlive(Clan clan)
        {
            try
            {
                if (clan == null || !S.Get.KeepKnownClansAlive) return false;
                if (HasAny(clan)) return true;                                       // desterrado: puede ser perdonado
                foreach (var h in clan.Heroes)
                {
                    if (h == null || !h.IsAlive) continue;
                    if (h.Spouse == Hero.MainHero) return true;
                    var m = MemoryStore.Find(h.StringId);
                    if (m != null && m.ConversationCount > 0) return true;           // gente con la que has tratado
                }
            }
            catch { }
            return false;
        }
    }
}
