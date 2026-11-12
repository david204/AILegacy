using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using Newtonsoft.Json;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.World
{
    /// <summary>Quién ha reinado en un reino, o quién ha tenido un feudo, y desde cuándo.</summary>
    public class TenureRecord
    {
        [JsonProperty("who")] public string Who;            // nombre del soberano, o del clan dueño
        [JsonProperty("of")] public string Of;              // clan del soberano, o reino del clan dueño
        [JsonProperty("from")] public double FromDay = -1;  // -1 = ya estaba al empezar a llevar la cuenta
        [JsonProperty("how")] public string How;            // siege, gift, barter… (solo feudos)
    }

    /// <summary>
    /// Memoria política larga: los soberanos que ha tenido cada reino y las manos por las que ha pasado cada ciudad y
    /// castillo. El juego solo sabe quién manda hoy; con esto la gente puede decir «antes era de los Leonipardes» o
    /// «desde que murió el viejo rey». Se usa en los datos de lo que se nombra en una charla.
    /// </summary>
    internal static class History
    {
        private const int Keep = 6;

        private static WorldState St => WorldSim.State;

        /// <summary>Al arrancar la partida: apunta quién manda hoy en cada reino del que aún no se sabe nada.</summary>
        public static void Snapshot()
        {
            try
            {
                if (St == null) return;
                foreach (var k in Kingdom.All)
                {
                    if (k == null || k.IsEliminated || k.Leader == null) continue;
                    if (!St.Rulers.TryGetValue(k.StringId, out var list)) St.Rulers[k.StringId] = list = new List<TenureRecord>();
                    if (list.Count == 0) list.Add(new TenureRecord { Who = k.Leader.Name.ToString(), Of = k.RulingClan?.Name?.ToString() });
                }
            }
            catch (Exception ex) { Log.Error("Historia: foto inicial", ex); }
        }

        public static void OnRulingClanChanged(Kingdom k, Clan c)
        {
            try
            {
                if (!NewsTracker.Live || St == null || k == null || c?.Leader == null) return;
                if (!St.Rulers.TryGetValue(k.StringId, out var list)) St.Rulers[k.StringId] = list = new List<TenureRecord>();
                string name = c.Leader.Name.ToString();
                if (list.Count > 0 && list[list.Count - 1].Who == name) return;
                list.Add(new TenureRecord { Who = name, Of = c.Name?.ToString(), FromDay = CampaignTime.Now.ToDays });
                while (list.Count > Keep) list.RemoveAt(0);
            }
            catch (Exception ex) { Log.Error("Historia: soberano", ex); }
        }

        public static void OnSettlementOwnerChanged(Settlement s, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturer,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                if (!NewsTracker.Live || St == null || s == null || !(s.IsTown || s.IsCastle)) return;
                var clan = newOwner?.Clan;
                if (clan == null) return;                    // sin casa que lo reciba no hay relevo que apuntar
                if (!St.Owners.TryGetValue(s.StringId, out var list)) St.Owners[s.StringId] = list = new List<TenureRecord>();
                // La primera vez se apunta también a quién se lo quitan: si no, no habría «antes»
                if (list.Count == 0 && oldOwner?.Clan != null)
                    list.Add(new TenureRecord { Who = oldOwner.Clan.Name.ToString(), Of = (oldOwner.MapFaction as Kingdom)?.Name?.ToString() });
                string name = clan.Name.ToString();
                if (list.Count > 0 && list[list.Count - 1].Who == name) return;
                list.Add(new TenureRecord { Who = name, Of = (newOwner.MapFaction as Kingdom)?.Name?.ToString(), FromDay = CampaignTime.Now.ToDays, How = detail.ToString() });
                while (list.Count > Keep) list.RemoveAt(0);
            }
            catch (Exception ex) { Log.Error("Historia: feudo", ex); }
        }

        private static string Ago(double fromDay)
        {
            int d = (int)(CampaignTime.Now.ToDays - fromDay);
            return d <= 0 ? "today" : d == 1 ? "yesterday" : d < 84 ? $"{d} days ago" : $"about {Math.Max(1, d / 84)} year(s) ago";
        }

        /// <summary>«…; taken by siege 12 days ago from clan X (of Y)», o cadena vacía.</summary>
        public static string OfSettlement(Settlement s)
        {
            try
            {
                if (St == null || s == null || !St.Owners.TryGetValue(s.StringId, out var list) || list.Count < 2) return "";
                var now = list[list.Count - 1]; var before = list[list.Count - 2];
                if (now.FromDay < 0) return "";
                string how = (now.How ?? "").IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0 ? "taken by siege"
                           : (now.How ?? "").IndexOf("Rebellion", StringComparison.OrdinalIgnoreCase) >= 0 ? "seized in a rebellion"
                           : "it changed hands";
                return $"; {how} {Ago(now.FromDay)}, before that it belonged to clan {before.Who}" + (string.IsNullOrEmpty(before.Of) ? "" : $" ({before.Of})");
            }
            catch { return ""; }
        }

        /// <summary>«…; they have ruled since N days ago; before them ruled X of clan Y», o cadena vacía.</summary>
        public static string OfKingdom(Kingdom k)
        {
            try
            {
                if (St == null || k == null || !St.Rulers.TryGetValue(k.StringId, out var list) || list.Count < 2) return "";
                var now = list[list.Count - 1]; var before = list[list.Count - 2];
                if (now.FromDay < 0) return "";
                return $"; they took the throne {Ago(now.FromDay)}; before them ruled {before.Who}" + (string.IsNullOrEmpty(before.Of) ? "" : $" of clan {before.Of}");
            }
            catch { return ""; }
        }
    }
}
