using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Dialogue;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.CharacterDevelopment;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;

namespace AILegacy.Prompt
{
    /// <summary>
    /// Lee del juego todo lo que el personaje sabría (debe llamarse en el hilo principal).
    /// </summary>
    internal static class ContextBuilder
    {
        // ================================================================ NPC

        public static string DescribeNpc(Speaker s)
        {
            return s.IsHero ? DescribeHero(s.Hero) : DescribeResident(s);
        }

        private static string DescribeHero(Hero npc)
        {
            var sb = new StringBuilder();
            sb.AppendLine($"Name: {npc.Name}");
            sb.AppendLine($"Gender: {(npc.IsFemale ? "woman" : "man")}, age {(int)npc.Age}");
            if (npc.Culture != null) sb.AppendLine($"Culture: {npc.Culture.Name}");
            sb.AppendLine($"Role in the world: {DescribeRole(npc)}");

            if (npc.Clan != null)
            {
                var clanLine = $"Clan: {npc.Clan.Name} (tier {npc.Clan.Tier})";
                if (npc.IsClanLeader) clanLine += " — you lead this clan";
                else if (npc.Clan.Leader != null) clanLine += $" — led by {npc.Clan.Leader.Name}";
                if (npc.Clan.IsMinorFaction) clanLine += ", a minor faction";
                if (npc.Clan.IsUnderMercenaryService) clanLine += ", serving as mercenaries";
                var banned = AILegacy.World.Banished.DescribeForClan(npc.Clan);
                if (banned.Length > 0) clanLine += ". Your clan is " + banned;
                sb.AppendLine(clanLine);
            }

            if (npc.MapFaction is Kingdom k)
            {
                var line = $"Kingdom: {k.Name}";
                if (npc.IsKingdomLeader) line += " — you are its ruler";
                else if (k.Leader != null) line += $", ruled by {k.Leader.Name}";
                sb.AppendLine(line);
            }

            var family = DescribeFamily(npc);
            if (family.Length > 0) sb.AppendLine("Your family (and where each one is now, as far as you know):\n" + family);

            var people = DescribePeople(npc);
            if (people.Length > 0) sb.AppendLine(people);

            var traits = DescribeTraits(npc);
            if (traits.Length > 0) sb.AppendLine("Personality traits: " + traits);

            sb.AppendLine("Wealth: " + DescribeWealth(npc.Gold));
            sb.AppendLine("Current situation: " + DescribeSituation(npc));
            try
            {
                // Dónde está exactamente y qué tropas lleva, con detalle: si le preguntan, lo sabe
                var own = npc.PartyBelongedTo;
                if (own != null && !own.IsMainParty)
                {
                    string where = DescribeWhereabouts(own);
                    if (where.Length > 0) sb.AppendLine("Where exactly you are: " + where);
                    if (own.LeaderHero == npc)
                    {
                        string men = DescribeTroops(own, true);
                        if (men.Length > 0) sb.AppendLine("Your men, exactly (you know your own host and can speak of it in detail):\n" + men);
                    }
                }
                // Sus tropas, unidad por unidad, solo si las tiene delante: a días de camino (o por carta) no las ve
                if (MobileParty.MainParty != null && (own == null || !own.IsMainParty) && SeesPlayerParty(npc))
                {
                    string theirs = DescribeTroops(MobileParty.MainParty, false);
                    if (theirs.Length > 0) sb.AppendLine("Their party, as far as you can see:\n" + theirs);
                }
            }
            catch { }

            var fiefs = DescribeFiefs(npc);
            if (fiefs.Length > 0) sb.AppendLine("Your clan holds: " + fiefs);

            var wars = DescribeWars(npc.MapFaction);
            if (wars.Length > 0) sb.AppendLine("Your faction is at war with: " + wars);

            var bio = SafeText(npc.EncyclopediaText);
            if (!string.IsNullOrWhiteSpace(bio)) sb.AppendLine("Known background: " + Clip(bio, 700));

            return sb.ToString().TrimEnd();
        }

        /// <summary>¿Tiene este personaje a la vista la partida del jugador? (mismo sitio, a su lado en el mapa, o va con ella)</summary>
        public static bool SeesPlayerParty(Hero npc)
        {
            try
            {
                var main = MobileParty.MainParty;
                if (npc == null || main == null) return false;
                if (npc.PartyBelongedTo == main || npc.PartyBelongedToAsPrisoner == main.Party) return true;
                var here = npc.CurrentSettlement ?? npc.PartyBelongedTo?.CurrentSettlement;
                if (here != null) return here == (Settlement.CurrentSettlement ?? main.CurrentSettlement);
                var p = npc.PartyBelongedTo;
                return p != null && p.GetPosition2D.Distance(main.GetPosition2D) <= 6f;
            }
            catch { return false; }
        }

        /// <summary>El jefe de una partida sin señor en el mapa: bandidos, una caravana, aldeanos de camino al mercado.</summary>
        private static string DescribeBandLeader(Speaker s)
        {
            var c = s.Character;
            var p = s.Band;
            var sb = new StringBuilder();
            var given = s.Memory.Profile?.GivenName;
            bool bandit = p.MapFaction != null && p.MapFaction.IsBanditFaction;
            string what = bandit ? "an outlaw band" : p.IsCaravan ? "a trade caravan" : p.IsVillager ? "a group of villagers taking goods to market" : p.IsMilitia ? "a militia" : "a party";
            sb.AppendLine(string.IsNullOrWhiteSpace(given) ? $"You are the leader of {what} ({p.Name}); you speak for all of them." : $"Name: {given}, leader of {what} ({p.Name}); you speak for all of them.");
            sb.AppendLine($"Gender: {(c.IsFemale ? "woman" : "man")}, age about {(int)c.Age}");
            if (c.Culture != null) sb.AppendLine($"Culture: {c.Culture.Name}");
            if (bandit)
                sb.AppendLine("You live outside the law: robbery, ransom and whatever the road gives. You respect strength and coin, not titles. You have no lord and owe nothing to any realm.");
            else if (p.IsCaravan)
                sb.AppendLine("You carry goods between towns for your master" + (p.Owner != null ? $", {p.Owner.Name}" : "") + ". You know prices, roads and the dangers on them, and you want no trouble.");
            else if (p.IsVillager)
                sb.AppendLine("You are peasants" + (p.HomeSettlement != null ? $" from {p.HomeSettlement.Name}" : "") + " carrying your produce to market. You are no warriors and you fear armed men.");
            try
            {
                string where = DescribeWhereabouts(p);
                if (where.Length > 0) sb.AppendLine("Where exactly you are: " + where);
                string men = DescribeTroops(p, true);
                if (men.Length > 0) sb.AppendLine("Your people, exactly:\n" + men);
                string theirs = DescribeTroops(MobileParty.MainParty, false);
                if (theirs.Length > 0) sb.AppendLine("Their party, as far as you can see:\n" + theirs);
                bool hostile = bandit || (p.MapFaction != null && Hero.MainHero.MapFaction != null && p.MapFaction.IsAtWarWith(Hero.MainHero.MapFaction));
                if (hostile) sb.AppendLine("They are armed and in front of you on the open road: this is a parley that can end in blood. Weigh your numbers against theirs.");
            }
            catch { }
            return sb.ToString().TrimEnd();
        }

        private static string DescribeResident(Speaker s)
        {
            if (s.Band != null) return DescribeBandLeader(s);
            var c = s.Character;
            var sb = new StringBuilder();
            var given = s.Memory.Profile?.GivenName;
            sb.AppendLine(string.IsNullOrWhiteSpace(given) ? $"You are an ordinary person ({c.Name})." : $"Name: {given} ({c.Name})");
            sb.AppendLine($"Gender: {(c.IsFemale ? "woman" : "man")}, age about {(int)c.Age}");
            if (c.Culture != null) sb.AppendLine($"Culture: {c.Culture.Name}");
            sb.AppendLine("Role in the world: " + ResidentRole(c));
            var st = s.ResidentSettlement;
            if (st != null)
            {
                string kind = st.IsTown ? "town" : st.IsCastle ? "castle" : st.IsVillage ? "village" : "place";
                sb.AppendLine($"You live in the {kind} of {st.Name}" + (st.OwnerClan?.Leader != null ? $", whose lord is {st.OwnerClan.Leader.Name}" : "") +
                              (st.MapFaction is Kingdom k ? $", in the realm of {k.Name}." : "."));
                if (st.IsUnderSiege) sb.AppendLine("The place is UNDER SIEGE right now.");
                if (st.Town != null)
                {
                    sb.AppendLine($"Life here: prosperity {(int)st.Town.Prosperity}, security {(int)st.Town.Security}, loyalty {(int)st.Town.Loyalty} — " +
                                  (st.Town.Prosperity < 2000 ? "times are hard" : st.Town.Prosperity > 6000 ? "the town is thriving" : "life is ordinary"));
                }
            }
            sb.AppendLine("You are a commoner: you know local gossip, prices and people, little of high politics, and you are careful around armed strangers and nobles.");
            var wars = DescribeWars(s.Faction);
            if (wars.Length > 0) sb.AppendLine("Your lord's realm is at war with: " + wars);
            return sb.ToString().TrimEnd();
        }

        private static string ResidentRole(CharacterObject c)
        {
            switch (c.Occupation.ToString())
            {
                case "Townsfolk": return "townsperson";
                case "Villager": return "villager (peasant)";
                case "Soldier": return "soldier";
                case "Guard": return "town guard";
                case "Tavernkeeper": return "tavern keeper";
                case "TavernWench": return "tavern maid";
                case "Musician": return "musician";
                case "Merchant": case "ShopWorker": return "shop worker / trader";
                case "Blacksmith": return "blacksmith";
                case "HorseTrader": return "horse trader";
                case "Armorer": return "armourer";
                case "Weaponsmith": return "weaponsmith";
                case "GoodsTrader": return "goods trader";
                case "RansomBroker": return "ransom broker";
                case "Mercenary": return "mercenary";
                default: return c.Occupation.ToString().ToLowerInvariant();
            }
        }

        // ============================================================== PLAYER

        /// <summary>Por qué este NPC sabe quién es el jugador. null = no lo sabe: es un forastero.</summary>
        public static string WhyKnowsPlayer(Speaker s)
        {
            var p = Hero.MainHero;
            var clan = Clan.PlayerClan;
            var cfg = S.Get;
            if (s.IsHero)
            {
                var npc = s.Hero;
                if (npc.Spouse == p || npc.Father == p || npc.Mother == p || p.Father == npc || p.Mother == npc || npc.Siblings.Contains(p))
                    return "family";
                if ((npc.IsPlayerCompanion && cfg.CompanionsRecognize) || (npc.Clan != null && npc.Clan == clan && !npc.IsPlayerCompanion)) return "clan";
                if (cfg.KingdomRecognize && clan != null && clan.Tier >= 2 && npc.IsLord && npc.MapFaction != null && npc.MapFaction == p.MapFaction && npc.MapFaction.IsKingdomFaction)
                    return "same_kingdom";
                if (Math.Abs((int)npc.GetRelationWithPlayer()) >= 15) return "past_dealings";
            }
            if (p.MapFaction is Kingdom pk && pk.Leader == p) return "ruler";
            if (clan != null && clan.Tier >= cfg.FameTierKnownByName) return "fame";
            // El señor del lugar: la gente de su feudo le conoce
            if (!s.IsHero && s.ResidentSettlement?.OwnerClan == clan) return "lord_here";
            return null;
        }

        /// <summary>Nombre con el que el NPC conoce al jugador, o null si para él es un desconocido.</summary>
        public static string PlayerNameFor(Speaker s)
        {
            if (WhyKnowsPlayer(s) != null) return Hero.MainHero.Name.ToString();
            return string.IsNullOrWhiteSpace(s.Memory.KnownPlayerName) ? null : s.Memory.KnownPlayerName;
        }

        public static string DescribePlayer(Speaker s)
        {
            var p = Hero.MainHero;
            var clan = Clan.PlayerClan;
            var mem = s.Memory;
            var sb = new StringBuilder();
            var why = WhyKnowsPlayer(s);

            if (why == null)
            {
                if (string.IsNullOrWhiteSpace(mem.KnownPlayerName))
                    sb.AppendLine("You do NOT know who this person is: they have not told you their name and you have never heard of them. " +
                                  "Do not call them by any name; address them as a stranger would (traveller, friend, stranger, my lord...). You may ask who they are.");
                else
                    sb.AppendLine($"They introduced themselves to you as \"{mem.KnownPlayerName}\". That is all you know about their identity; you cannot tell whether it is their real name.");
                sb.AppendLine("What you can see: " + DescribeAppearance(p));
            }
            else
            {
                string how = why == "fame" ? " (you recognise them by reputation)" : why == "same_kingdom" ? " (a known vassal of your realm)"
                           : why == "lord_here" ? " (the lord of this place)" : why == "ruler" ? " (a ruler — everyone knows who they are)" : "";
                sb.AppendLine($"Name: {p.Name}{how}");
                sb.AppendLine($"Gender: {(p.IsFemale ? "woman" : "man")}, age {(int)p.Age}");
                if (p.Culture != null) sb.AppendLine($"Culture: {p.Culture.Name}");
                if (clan != null)
                    sb.AppendLine($"Clan: {clan.Name} (tier {clan.Tier}, renown {(int)clan.Renown}) — {DescribeFame(clan.Tier)}");
                if (p.MapFaction is Kingdom pk)
                    sb.AppendLine(pk.Leader == p ? $"Ruler of {pk.Name}" : $"Sworn to {pk.Name}{(clan != null && clan.IsUnderMercenaryService ? " as a mercenary" : "")}");
                else
                    sb.AppendLine("Belongs to no kingdom");
                var pBanned = AILegacy.World.Banished.DescribeForClan(clan);
                if (pBanned.Length > 0) sb.AppendLine("Their clan is " + pBanned);
                var fiefs = clan != null ? string.Join(", ", clan.Settlements.Where(x => x.IsTown || x.IsCastle).Take(6).Select(x => x.Name.ToString())) : "";
                if (fiefs.Length > 0) sb.AppendLine("Holds: " + fiefs);
            }

            if (s.IsHero)
            {
                var npc = s.Hero;
                int rel = (int)npc.GetRelationWithPlayer();
                sb.AppendLine($"Your opinion of them (relation {rel} on a -100..100 scale): {DescribeRelation(rel)}");

                if (npc.Spouse == p) sb.AppendLine("They are your spouse.");
                else if (npc.Father == p || npc.Mother == p) sb.AppendLine("They are your parent.");
                else if (p.Father == npc || p.Mother == npc) sb.AppendLine("They are your child.");
                else if (npc.Siblings.Contains(p)) sb.AppendLine("They are your sibling.");
                if (npc.IsPlayerCompanion) sb.AppendLine("You serve in their clan as a companion.");
                else if (npc.Clan != null && npc.Clan == Clan.PlayerClan) sb.AppendLine("You belong to their clan.");

                if (npc.IsPrisoner && npc.PartyBelongedToAsPrisoner == MobileParty.MainParty?.Party)
                    sb.AppendLine("You are THEIR PRISONER. They decide whether you go free (perhaps for a ransom).");
                else if (npc.IsPrisoner)
                    sb.AppendLine("You are a prisoner (not theirs).");

                if (npc.IsWanderer && npc.CompanionOf == null)
                {
                    try
                    {
                        int price = Campaign.Current.Models.CompanionHiringPriceCalculationModel.GetCompanionHiringPrice(npc);
                        sb.AppendLine($"You are looking for employment; your usual price to join someone's band is about {price} denars.");
                    }
                    catch { }
                }
            }

            var myFaction = s.Faction;
            if (myFaction != null && p.MapFaction != null && myFaction != p.MapFaction)
            {
                if (myFaction.IsAtWarWith(p.MapFaction))
                    sb.AppendLine(why != null ? "Your factions are AT WAR: they are an enemy."
                                              : $"Their banners show they serve {p.MapFaction.Name}, a faction at war with yours: an enemy.");
            }
            else if (why != null && myFaction != null && myFaction == p.MapFaction && myFaction.IsKingdomFaction)
                sb.AppendLine("You serve the same kingdom.");

            sb.AppendLine($"Judging by their gear, they seem {DescribeWealth(p.Gold)}.");
            return sb.ToString().TrimEnd();
        }

        private static string DescribeAppearance(Hero p)
        {
            int age = (int)p.Age;
            string ageText = age < 25 ? "young" : age < 40 ? $"around {age / 5 * 5}" : age < 55 ? "middle-aged" : "old";
            var parts = new List<string> { $"a {ageText} {(p.IsFemale ? "woman" : "man")}" };
            if (p.Culture != null) parts.Add($"with the look and clothes of a {p.Culture.Name} person");
            parts.Add("armed like a warrior");
            var party = MobileParty.MainParty;
            if (party != null)
            {
                int men = party.MemberRoster.TotalManCount;
                parts.Add(men <= 3 ? "travelling almost alone" : men < 30 ? $"leading a small band of about {men / 5 * 5 + 5} men"
                                   : men < 120 ? $"leading a warband of roughly {men / 10 * 10} men" : $"leading a large host of {men / 50 * 50}+ men");
            }
            int tier = Clan.PlayerClan?.Tier ?? 0;
            if (tier >= 2) parts.Add("their bearing and gear suggest someone of some standing");
            return string.Join(", ", parts) + ".";
        }

        // =============================================================== WORLD

        public static string DescribeWorld(Speaker s)
        {
            var now = CampaignTime.Now;
            var sb = new StringBuilder();
            sb.Append($"Date: day {now.GetDayOfSeason + 1} of {now.GetSeasonOfYear}, year {now.GetYear}, around {now.GetHourOfDay}:00.");
            var st = s.Settlement;
            if (st != null) sb.Append($" You are in {st.Name}.");
            return sb.ToString();
        }

        /// <summary>Foto de la situación de un héroe para su archivo: dónde está, qué hace, fuerzas, relación.</summary>
        public static Dictionary<string, object> Snapshot(Hero h)
        {
            var d = new Dictionary<string, object>();
            void Put(string k, Func<string> f) { try { var v = f(); if (!string.IsNullOrWhiteSpace(v)) d[k] = v; } catch { } }
            void PutList(string k, Func<IEnumerable<string>> f) { try { var v = f()?.Where(x => !string.IsNullOrWhiteSpace(x)).ToList(); if (v != null && v.Count > 0) d[k] = v; } catch { } }
            List<string> Troops(MobileParty p) => p == null ? null : p.MemberRoster.GetTroopRoster()
                .Where(e => e.Character != null && !e.Character.IsHero && e.Number > 0).OrderByDescending(e => e.Number)
                .Select(e => $"{e.Character.Name} x{e.Number} ({KindOf(e.Character)}, tier {e.Character.Tier}{(e.WoundedNumber > 0 ? $", {e.WoundedNumber} wounded" : "")}) [{e.Character.StringId}]").ToList();
            Put("whereabouts", () => DescribeWhereabouts(h.PartyBelongedTo));
            Put("prisoners_held", () => h.PartyBelongedTo != null && h.PartyBelongedTo.LeaderHero == h && h.PartyBelongedTo.PrisonRoster.TotalManCount > 0 ? h.PartyBelongedTo.PrisonRoster.TotalManCount.ToString() : null);
            var now = CampaignTime.Now;
            Put("taken_on", () => $"day {now.GetDayOfSeason + 1} of {now.GetSeasonOfYear}, year {now.GetYear}");
            Put("gender_age", () => $"{(h.IsFemale ? "female" : "male")}, {(int)h.Age}");
            Put("culture", () => h.Culture?.Name?.ToString());
            Put("role", () => DescribeRole(h));
            Put("clan", () => h.Clan == null ? null : $"{h.Clan.Name} (tier {h.Clan.Tier}){(h.IsClanLeader ? ", leader" : "")}");
            Put("kingdom", () => (h.MapFaction as Kingdom)?.Name?.ToString());
            Put("situation", () => DescribeSituation(h));
            Put("party_size", () => h.PartyBelongedTo != null && h.PartyBelongedTo.LeaderHero == h ? h.PartyBelongedTo.MemberRoster.TotalManCount.ToString() : null);
            Put("player_party_size", () => MobileParty.MainParty?.MemberRoster.TotalManCount.ToString());
            Put("relation_with_player", () => { int r = (int)h.GetRelationWithPlayer(); return $"{r} ({DescribeRelation(r)})"; });
            Put("knows_player_because", () => WhyKnowsPlayer(Speaker.FromHero(h)) ?? "does not know who the player is");
            Put("wars", () => DescribeWars(h.MapFaction));
            Put("fiefs", () => DescribeFiefs(h));
            Put("wealth", () => DescribeWealth(h.Gold));
            Put("traits", () => DescribeTraits(h));
            Put("time_of_day", () => $"{now.GetHourOfDay}:00");
            Put("spouse", () => h.Spouse == null ? null : $"{h.Spouse.Name}{(h.Spouse.IsAlive ? "" : " (dead)")}");
            Put("is_prisoner", () => h.IsPrisoner ? "yes" + (h.PartyBelongedToAsPrisoner?.LeaderHero != null ? $", of {h.PartyBelongedToAsPrisoner.LeaderHero.Name}" : "") : null);
            Put("army", () => h.PartyBelongedTo?.Army == null ? null : $"{h.PartyBelongedTo.Army.Name} ({h.PartyBelongedTo.Army.TotalManCount} men), led by {h.PartyBelongedTo.Army.LeaderParty?.LeaderHero?.Name}");
            Put("party_wounded_percent", () => h.PartyBelongedTo != null && h.PartyBelongedTo.LeaderHero == h && h.PartyBelongedTo.MemberRoster.TotalManCount > 0
                ? (100 * h.PartyBelongedTo.MemberRoster.TotalWounded / h.PartyBelongedTo.MemberRoster.TotalManCount).ToString() : null);
            Put("information_access", () => h.IsKingdomLeader ? "high" : h.IsLord ? (h.IsClanLeader ? "high" : "medium") : h.IsNotable ? "medium" : "low");
            PutList("family", () => DescribeFamily(h).Split('\n').Select(x => x.TrimStart('-', ' ')));
            PutList("friends_and_enemies", () => DescribePeople(h).Split('\n'));
            PutList("troops", () => h.PartyBelongedTo != null && h.PartyBelongedTo.LeaderHero == h ? Troops(h.PartyBelongedTo) : null);
            PutList("player_troops", () => Troops(MobileParty.MainParty));
            PutList("recent_events_known", () => World.NewsStore.KnownBy(Speaker.FromHero(h), 15).Select(n => World.NewsStore.Describe(n)));
            PutList("open_tasks_with_player", () => World.QuestSystem.ActiveKnownTo(h.StringId).Select(q => $"{q.Name} ({q.Id})"));
            Put("current_order", () => World.OrderSystem.Describe(h));
            return d;
        }

        // ============================================================= helpers

        private static string DescribeRole(Hero h)
        {
            if (h.IsKingdomLeader) return "ruler of a kingdom";
            if (h.IsLord) return h.IsClanLeader ? "noble, head of a clan" : "noble of a clan";
            if (h.IsPlayerCompanion) return "companion in the player's clan (" + h.Occupation + ")";
            if (h.IsWanderer) return "wanderer / sellsword looking for work";
            if (h.IsMerchant) return "merchant, a notable of the town";
            if (h.IsArtisan) return "artisan, a notable of the town";
            if (h.IsGangLeader) return "gang leader of the town's underworld";
            if (h.IsHeadman) return "village headman";
            if (h.IsRuralNotable) return "landowner, a notable of the village";
            if (h.IsPreacher) return "preacher";
            return h.Occupation.ToString();
        }

        private static string DescribeFamily(Hero h)
        {
            var lines = new List<string>();
            var seen = new HashSet<Hero> { h };
            void Add(string role, Hero x)
            {
                if (x == null || !seen.Add(x)) return;
                lines.Add($"- {role}: {x.Name}{Age(x)} — {WhereIs(x, h)}");
            }

            if (h.Spouse != null) Add(h.Spouse.IsAlive ? (h.Spouse.IsFemale ? "wife" : "husband") : "late spouse", h.Spouse);
            Add("father", h.Father);
            Add("mother", h.Mother);
            foreach (var s in SafeList(() => h.Siblings).Take(6)) Add(s.IsFemale ? "sister" : "brother", s);
            foreach (var c in SafeList(() => h.Children).Take(8))
            {
                Add(c.IsFemale ? "daughter" : "son", c);
                if (c.Spouse != null) Add(c.IsFemale ? $"son-in-law ({c.Name}'s husband)" : $"daughter-in-law ({c.Name}'s wife)", c.Spouse);
                foreach (var gc in SafeList(() => c.Children).Take(4)) Add($"grandchild ({c.Name}'s child)", gc);
            }
            if (h.Spouse != null)
            {
                Add("father-in-law", h.Spouse.Father);
                Add("mother-in-law", h.Spouse.Mother);
            }
            return string.Join("\n", lines);
        }

        /// <summary>Amigos y enemigos reales según las relaciones del juego.</summary>
        private static string DescribePeople(Hero npc)
        {
            try
            {
                var cfgP = S.Get;
                if (!cfgP.IncludeFriends) return "";
                var family = new HashSet<Hero> { npc, npc.Spouse, npc.Father, npc.Mother };
                foreach (var x in SafeList(() => npc.Children)) family.Add(x);
                foreach (var x in SafeList(() => npc.Siblings)) family.Add(x);

                var scored = new List<(Hero h, int rel)>();
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || h == Hero.MainHero || family.Contains(h) || h.IsChild) continue;
                    if (!(h.IsLord || h.IsNotable || h.IsWanderer)) continue;
                    int r = npc.GetRelation(h);
                    if (r >= 20 || r <= -20) scored.Add((h, r));
                }
                var sb = new StringBuilder();
                var friends = scored.Where(x => x.rel > 0).OrderByDescending(x => x.rel).Take(cfgP.MaxFriends).ToList();
                var enemies = scored.Where(x => x.rel < 0).OrderBy(x => x.rel).Take(cfgP.MaxEnemies).ToList();
                if (friends.Count > 0)
                    sb.AppendLine("Your friends: " + string.Join("; ", friends.Select(x => $"{x.h.Name}{ClanOf(x.h)} ({(x.rel >= 50 ? "close friend" : "friend")}, now {WhereIs(x.h, npc)})")));
                if (enemies.Count > 0)
                    sb.AppendLine("People you dislike or hate: " + string.Join("; ", enemies.Select(x => $"{x.h.Name}{ClanOf(x.h)} ({(x.rel <= -50 ? "bitter enemy" : "you dislike them")})")));

                // Señor y compañeros de reino importantes
                if (npc.MapFaction is Kingdom k && k.Leader != null && k.Leader != npc && !family.Contains(k.Leader))
                    sb.AppendLine($"Your ruler: {k.Leader.Name} — {WhereIs(k.Leader, npc)}");
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        /// <summary>Dónde está y qué hace alguien, en pocas palabras.</summary>
        private static string WhereIs(Hero x, Hero viewer)
        {
            try
            {
                if (x.IsDead) return "dead";
                if (x.IsPrisoner)
                {
                    var captor = x.PartyBelongedToAsPrisoner?.LeaderHero;
                    var place = x.PartyBelongedToAsPrisoner?.Settlement ?? x.CurrentSettlement;
                    return "held prisoner" + (captor != null ? $" by {captor.Name}" : place != null ? $" in {place.Name}" : "");
                }
                var party = x.PartyBelongedTo;
                if (party != null && party.IsMainParty)
                    return x == Hero.MainHero ? "right here" : $"travelling in the warband of {Hero.MainHero.Name}";
                if (party != null)
                {
                    var parts = new List<string>();
                    if (party.LeaderHero == x) parts.Add($"leading a party of {party.MemberRoster.TotalManCount} men");
                    else if (party.LeaderHero != null) parts.Add($"riding with {party.LeaderHero.Name}");
                    if (party.Army != null) parts.Add(party.Army.LeaderParty?.LeaderHero == x ? "commanding an army" : "in an army");
                    var task = DescribeTask(party);
                    if (task.Length > 0) parts.Add(task);
                    else if (party.CurrentSettlement != null) parts.Add($"at {party.CurrentSettlement.Name}");
                    if (viewer != null && viewer.PartyBelongedTo == party) parts.Insert(0, "here with you");
                    return string.Join(", ", parts);
                }
                if (x.CurrentSettlement != null)
                    return (viewer?.CurrentSettlement == x.CurrentSettlement ? "here, in " : "in ") + x.CurrentSettlement.Name;
                return "somewhere in Calradia (you are not sure where)";
            }
            catch { return "whereabouts unknown"; }
        }

        private static string Age(Hero x) => x.IsDead ? "" : $" ({(int)x.Age})";
        private static string ClanOf(Hero h) => h.Clan != null && h.IsLord ? $" of clan {h.Clan.Name}" : h.IsNotable && h.CurrentSettlement != null ? $" of {h.CurrentSettlement.Name}" : "";

        private static List<Hero> SafeList(Func<IEnumerable<Hero>> f)
        {
            try { return (f() ?? Enumerable.Empty<Hero>()).Where(x => x != null).ToList(); }
            catch { return new List<Hero>(); }
        }

        private static string DescribeTraits(Hero h)
        {
            var parts = new List<string>();
            void Add(TraitObject t, string high, string low)
            {
                if (t == null) return;
                int v = h.GetTraitLevel(t);
                if (v >= 2) parts.Add("very " + high);
                else if (v == 1) parts.Add(high);
                else if (v == -1) parts.Add(low);
                else if (v <= -2) parts.Add("very " + low);
            }
            Add(DefaultTraits.Honor, "honorable", "deceitful");
            Add(DefaultTraits.Mercy, "merciful", "cruel");
            Add(DefaultTraits.Valor, "brave", "cautious");
            Add(DefaultTraits.Generosity, "generous", "greedy");
            Add(DefaultTraits.Calculating, "calculating", "impulsive");
            return string.Join(", ", parts);
        }

        private static string KindOf(CharacterObject c) => c.IsMounted && c.IsRanged ? "horse archer" : c.IsMounted ? "cavalry" : c.IsRanged ? "archer/crossbowman" : "infantry";

        /// <summary>
        /// El grupo, tropa por tropa (nombre, clase, nivel, cuántos y cuántos heridos), con el total por clases, los heridos,
        /// los prisioneros y, si es el suyo, la moral y los días de comida.
        /// </summary>
        public static string DescribeTroops(MobileParty p, bool own)
        {
            try
            {
                var roster = p.MemberRoster.GetTroopRoster().Where(e => e.Character != null && !e.Character.IsHero && e.Number > 0).OrderByDescending(e => e.Number).ToList();
                if (roster.Count == 0) return own ? "- no soldiers at all: you travel with only your closest people" : "- almost nobody: they travel nearly alone";
                var sb = new StringBuilder();
                var byKind = new Dictionary<string, int>();
                foreach (var e in roster)
                {
                    string k = KindOf(e.Character);
                    byKind.TryGetValue(k, out int n); byKind[k] = n + e.Number;
                }
                int total = p.MemberRoster.TotalManCount, wounded = p.MemberRoster.TotalWounded;
                sb.AppendLine($"- {total} in all ({string.Join(", ", byKind.OrderByDescending(kv => kv.Value).Select(kv => $"{kv.Value} {kv.Key}"))}); {(wounded > 0 ? wounded + " wounded" : "none wounded")}.");
                foreach (var e in roster.Take(own ? 16 : 10))
                    sb.AppendLine($"- {e.Number} {e.Character.Name} ({KindOf(e.Character)}, tier {e.Character.Tier}{(e.WoundedNumber > 0 ? $", {e.WoundedNumber} wounded" : "")})");
                if (roster.Count > (own ? 16 : 10)) sb.AppendLine($"- and {roster.Skip(own ? 16 : 10).Sum(e => e.Number)} more of other kinds");
                var heroes = p.MemberRoster.GetTroopRoster().Where(e => e.Character != null && e.Character.IsHero && e.Character.HeroObject != p.LeaderHero).Select(e => e.Character.Name.ToString()).ToList();
                if (heroes.Count > 0) sb.AppendLine("- with " + (own ? "you" : "them") + " ride: " + string.Join(", ", heroes.Take(8)));
                int prisoners = p.PrisonRoster.TotalManCount;
                if (prisoners > 0)
                {
                    var nobles = p.PrisonRoster.GetTroopRoster().Where(e => e.Character != null && e.Character.IsHero).Select(e => e.Character.Name.ToString()).ToList();
                    sb.AppendLine($"- prisoners held: {prisoners}" + (nobles.Count > 0 ? " (among them " + string.Join(", ", nobles.Take(5)) + ")" : ""));
                }
                if (own)
                {
                    try
                    {
                        int food = p.GetNumDaysForFoodToLast();
                        sb.AppendLine($"- morale {(p.Morale < 30 ? "low: the men grumble" : p.Morale > 70 ? "high" : "steady")}; food for about {Math.Max(0, food)} day(s)" + (p.Party.NumberOfMounts > 0 ? $"; {p.Party.NumberOfMounts} spare mounts" : ""));
                    }
                    catch { }
                }
                return sb.ToString().TrimEnd();
            }
            catch { return ""; }
        }

        /// <summary>Dónde está en el mapa con precisión: los lugares más cercanos, de quién son, y si va por tierra o por mar.</summary>
        public static string DescribeWhereabouts(MobileParty p)
        {
            try
            {
                if (p == null) return "";
                if (p.CurrentSettlement != null)
                {
                    var c = p.CurrentSettlement;
                    return $"inside {c.Name} ({(c.IsTown ? "town" : c.IsCastle ? "castle" : "village")} of {c.MapFaction?.Name}, held by {c.OwnerClan?.Name})";
                }
                var here = p.GetPosition2D;
                var near = Settlement.All.Where(s => s != null && !s.IsHideout && (s.IsTown || s.IsCastle || s.IsVillage))
                    .Select(s => (s, d: s.GetPosition2D.Distance(here))).OrderBy(x => x.d).Take(3).ToList();
                if (near.Count == 0) return "";
                string List() => string.Join(", ", near.Select(x => $"{x.s.Name} ({(x.s.IsTown ? "town" : x.s.IsCastle ? "castle" : "village")} of {x.s.MapFaction?.Name}{(x.s.IsUnderSiege ? ", under siege" : x.s.IsRaided ? ", raided" : "")}; {(x.d < 8f ? "very close" : x.d < 25f ? "a short ride away" : "about a day's ride")})"));
                string sea = "";
                try { sea = p.IsCurrentlyAtSea ? ", at sea" : ", on land"; } catch { }
                bool own = near[0].s.MapFaction == p.MapFaction, hostile = near[0].s.MapFaction != null && p.MapFaction != null && near[0].s.MapFaction.IsAtWarWith(p.MapFaction);
                return $"in the open{sea}, near {List()} — {(own ? "your own realm's lands" : hostile ? "ENEMY lands" : "foreign lands")}";
            }
            catch { return ""; }
        }

        private static string DescribeSituation(Hero h)
        {
            if (h.IsPrisoner) return "held as a prisoner";
            var party = h.PartyBelongedTo;
            Settlement s = h.CurrentSettlement ?? party?.CurrentSettlement;
            var parts = new List<string>();
            if (s != null)
            {
                string kind = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : "place";
                parts.Add($"in the {kind} of {s.Name}" + (s.IsUnderSiege ? " (UNDER SIEGE)" : ""));
            }
            if (party != null && !party.IsMainParty)
            {
                var roster = party.MemberRoster;
                int wounded = roster.TotalWounded;
                parts.Add(party.LeaderHero == h
                    ? $"leading a party of {roster.TotalManCount} men" + (wounded > roster.TotalManCount / 3 ? $" ({wounded} of them wounded)" : "")
                    : $"travelling with {party.Name}");
                if (party.Army != null)
                {
                    var al = party.Army.LeaderParty?.LeaderHero;
                    parts.Add(al == h ? $"commanding an army of {party.Army.TotalManCount} men" : $"part of the army of {al?.Name}");
                }
                var task = DescribeTask(party);
                if (task.Length > 0) parts.Add(task);
            }
            else if (party != null && party.IsMainParty)
                parts.Add("travelling in the player's own party");
            if (parts.Count == 0) parts.Add("travelling the roads of Calradia");
            try
            {
                var order = World.OrderSystem.Describe(h);
                if (order.Length > 0) parts.Add(order);
            }
            catch { }
            return string.Join(", ", parts);
        }

        private static string DescribeTask(MobileParty party)
        {
            try
            {
                var target = party.TargetSettlement;
                switch (party.ShortTermBehavior.ToString())
                {
                    case "BesiegeSettlement": return target != null ? $"besieging {target.Name}" : "besieging a fortress";
                    case "RaidSettlement": return target != null ? $"raiding {target.Name}" : "raiding";
                    case "DefendSettlement": return target != null ? $"marching to defend {target.Name}" : "defending";
                    case "EngageParty": return party.TargetParty != null ? $"chasing {party.TargetParty.Name}" : "chasing an enemy";
                    case "FleeToPoint": case "FleeToGate": case "FleeToParty": return "fleeing from enemies";
                    case "GoToSettlement": return target != null ? $"on the way to {target.Name}" : "travelling";
                    case "PatrolAroundPoint": return target != null ? $"patrolling around {target.Name}" : "patrolling";
                    case "EscortParty": return party.TargetParty != null ? $"escorting {party.TargetParty.Name}" : "escorting";
                    default: return "";
                }
            }
            catch { return ""; }
        }

        private static string DescribeFiefs(Hero h)
        {
            if (h.Clan == null || !h.IsLord) return "";
            try { return string.Join(", ", h.Clan.Settlements.Where(x => x.IsTown || x.IsCastle).Take(6).Select(x => x.Name.ToString())); }
            catch { return ""; }
        }

        private static string DescribeWars(IFaction f)
        {
            if (f == null) return "";
            try
            {
                return string.Join(", ", f.FactionsAtWarWith
                    .Where(x => x != null && x.IsKingdomFaction)
                    .Take(8)
                    .Select(x => x.Name.ToString()));
            }
            catch { return ""; }
        }

        private static string DescribeWealth(int gold)
        {
            if (gold < 500) return "poor";
            if (gold < 5000) return "of modest means";
            if (gold < 50000) return "comfortable";
            if (gold < 250000) return "rich";
            return "extremely rich";
        }

        private static string DescribeFame(int tier)
        {
            switch (tier)
            {
                case 0: return "an unknown nobody";
                case 1: return "a little-known adventurer";
                case 2: return "a known mercenary captain";
                case 3: return "a respected warlord";
                case 4: return "a famous lord";
                default: return "a legendary figure across Calradia";
            }
        }

        public static string DescribeRelation(int r)
        {
            if (r <= -50) return "you hate them";
            if (r <= -20) return "you dislike and distrust them";
            if (r < -5) return "you are wary of them";
            if (r <= 5) return "neutral";
            if (r < 20) return "you have a decent opinion of them";
            if (r < 50) return "you like and trust them";
            return "they are a close and trusted friend";
        }

        private static string SafeText(object t)
        {
            try { return t?.ToString() ?? ""; } catch { return ""; }
        }

        private static string Clip(string s, int n) => s.Length <= n ? s : s.Substring(0, n) + "…";
    }
}
