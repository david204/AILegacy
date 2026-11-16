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
    /// <summary>Who ruled a kingdom or held a fief, and since when.</summary>
    public class TenureRecord
    {
        [JsonProperty("who")] public string Who;            // ruler's name, or owner clan
        [JsonProperty("of")] public string Of;              // ruler's clan, or the owner clan's kingdom
        /// <summary>Id of the owner clan's kingdom (fiefs only). Missing in records from before it was kept: those only have the name.</summary>
        [JsonProperty("of_id", NullValueHandling = NullValueHandling.Ignore)] public string OfId;
        [JsonProperty("from")] public double FromDay = -1;  // -1 = already there when tracking started
        [JsonProperty("how")] public string How;            // siege, gift, barter... (fiefs only)
        /// <summary>Who led the capture (fiefs taken by siege only). Missing in older records.</summary>
        [JsonProperty("by", NullValueHandling = NullValueHandling.Ignore)] public string By;
    }

    /// <summary>
    /// Long political memory: the rulers each kingdom has had, and the hands each town and castle has passed through.
    /// The game only knows who rules today. With this, people can say "it used to belong to the Leonipardes" or "since the
    /// old king died". Used in the facts given for places and realms named in a conversation.
    /// </summary>
    internal static class History
    {
        private const int Keep = 6;

        private static WorldState St => WorldSim.State;

        /// <summary>On campaign start, records the current ruler of every kingdom with no history yet.</summary>
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
            catch (Exception ex) { Log.Error("History: initial snapshot", ex); }
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
            catch (Exception ex) { Log.Error("History: ruler", ex); }
        }

        public static void OnSettlementOwnerChanged(Settlement s, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturer,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail)
        {
            try
            {
                if (!NewsTracker.Live || St == null || s == null || !(s.IsTown || s.IsCastle)) return;
                var clan = newOwner?.Clan;
                if (clan == null) return;                    // no receiving clan, nothing to record
                if (!St.Owners.TryGetValue(s.StringId, out var list)) St.Owners[s.StringId] = list = new List<TenureRecord>();
                // First time around, also record who lost it, otherwise there's no "before"
                if (list.Count == 0 && oldOwner?.Clan != null)
                    list.Add(new TenureRecord { Who = oldOwner.Clan.Name.ToString(), Of = (oldOwner.MapFaction as Kingdom)?.Name?.ToString(), OfId = (oldOwner.MapFaction as Kingdom)?.StringId });
                string name = clan.Name.ToString();
                if (list.Count > 0 && list[list.Count - 1].Who == name) return;
                list.Add(new TenureRecord
                {
                    Who = name, Of = (newOwner.MapFaction as Kingdom)?.Name?.ToString(), OfId = (newOwner.MapFaction as Kingdom)?.StringId, FromDay = CampaignTime.Now.ToDays, How = detail.ToString(),
                    By = detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.BySiege ? (capturer ?? newOwner)?.Name?.ToString() : null,
                });
                while (list.Count > Keep) list.RemoveAt(0);
            }
            catch (Exception ex) { Log.Error("History: fief", ex); }
        }

        /// <summary>A town or castle that changed hands, as the history of owners has it.</summary>
        public sealed class Change
        {
            public Settlement Place;
            public string FromClan, ToClan;
            /// <summary>Kingdom ids, "" when the clan was under no crown or the record is too old to say.</summary>
            public string FromKingdom, ToKingdom;
            public string FromKingdomName, ToKingdomName;
            public double Day;
            public string How, By;
        }

        /// <summary>Every change of hands of the last <paramref name="days"/> days, newest first.</summary>
        public static List<Change> RecentChanges(int days)
        {
            var res = new List<Change>();
            try
            {
                if (St?.Owners == null) return res;
                double now = CampaignTime.Now.ToDays;
                foreach (var kv in St.Owners)
                {
                    var list = kv.Value;
                    if (list == null || list.Count < 2) continue;
                    Settlement s = null;
                    for (int i = 1; i < list.Count; i++)
                    {
                        var r = list[i];
                        // FromDay > now: a record from a later save
                        if (r == null || r.FromDay < 0 || r.FromDay > now + 1 || now - r.FromDay > days) continue;
                        s = s ?? Settlement.Find(kv.Key);
                        if (s == null) break;
                        var b = list[i - 1];
                        res.Add(new Change
                        {
                            Place = s, FromClan = b?.Who, ToClan = r.Who, FromKingdom = KingdomId(b), ToKingdom = KingdomId(r),
                            FromKingdomName = b?.Of, ToKingdomName = r.Of, Day = r.FromDay, How = r.How, By = r.By,
                        });
                    }
                }
            }
            catch (Exception ex) { Log.Error("History: recent fief changes", ex); }
            return res.OrderByDescending(c => c.Day).ToList();
        }

        private static string KingdomId(TenureRecord r)
        {
            if (r == null) return "";
            if (!string.IsNullOrEmpty(r.OfId)) return r.OfId;
            if (string.IsNullOrEmpty(r.Of)) return "";
            return Kingdom.All.FirstOrDefault(k => k?.Name?.ToString() == r.Of)?.StringId ?? "";
        }

        /// <summary>How a fief changed hands, in English words, from the game's detail.</summary>
        public static string HowEn(string how)
        {
            switch (how ?? "")
            {
                case "BySiege": return "taken by siege";
                case "ByRebellion": return "seized in a rebellion";
                case "ByBarter": return "handed over in a bargain";
                case "ByGift": return "given away";
                case "ByKingDecision": return "granted by the crown";
                case "ByLeaveFaction": return "went with its clan when it left its realm";
                case "ByClanDestruction": return "passed on when its clan died out";
                default: return "changed hands";
            }
        }

        private static string Ago(double fromDay)
        {
            int d = (int)(CampaignTime.Now.ToDays - fromDay);
            return d <= 0 ? "today" : d == 1 ? "yesterday" : d < 84 ? $"{d} days ago" : $"about {Math.Max(1, d / 84)} year(s) ago";
        }

        /// <summary>"; taken by siege 12 days ago, before that it belonged to clan X (Y)", or empty.</summary>
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

        /// <summary>"; they took the throne N days ago; before them ruled X of clan Y", or empty.</summary>
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
