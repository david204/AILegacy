using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Settings;
using AILegacy.World;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Roster;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Acciones que la IA puede pedir en el array "actions" de su respuesta.
    /// Solo se ofrecen las que tienen sentido ahora mismo y todas se validan antes de ejecutarse.
    /// </summary>
    internal static class NpcActions
    {
        public class ActionContext
        {
            public bool EndConversation;
            public int RelationDelta;
            public int TrustDelta;
            public int TensionDelta;
            /// <summary>La acción llega por carta: el NPC no está delante del jugador.</summary>
            public bool ViaLetter;
        }

        private class ActionDef
        {
            public string Name;
            public string Usage;
            public string Description;
            public Func<Speaker, bool> IsAvailable;
            /// <summary>También se puede decidir al contestar una carta (no hace falta estar cara a cara).</summary>
            public bool Letter;
            /// <summary>Texto extra que depende del momento (por ejemplo, los enemigos que hay cerca).</summary>
            public Func<Speaker, string> Extra;
            /// <summary>Devuelve un texto para el registro del juego, o null.</summary>
            public Func<Speaker, string, ActionContext, string> Execute;
        }

        private static Hero P => Hero.MainHero;

        /// <summary>El personaje con el que hablas ahora, si estáis en una escena y lo mueve la IA.</summary>
        private static TaleWorlds.MountAndBlade.Agent SceneAgent()
        {
            try
            {
                if (TaleWorlds.MountAndBlade.Mission.Current == null) return null;
                var a = Campaign.Current?.ConversationManager?.OneToOneConversationAgent as TaleWorlds.MountAndBlade.Agent;
                return a != null && a.IsActive() && a.IsHuman && a.IsAIControlled ? a : null;
            }
            catch { return null; }
        }

        // Dinero: la IA a veces repite en la respuesta siguiente el cobro que ya hizo. El mismo importe, en el mismo sentido y
        // con la misma persona no se cobra dos veces en pocos minutos.
        private static readonly Dictionary<string, (int amount, DateTime at)> LastGold = new Dictionary<string, (int, DateTime)>();

        public static void ResetSession() => LastGold.Clear();

        private static bool RepeatedGold(Speaker s, string dir, int amount)
        {
            if (LastGold.TryGetValue(s.Key + "|" + dir, out var last) && last.amount == amount && (DateTime.UtcNow - last.at).TotalSeconds < S.Get.GoldRepeatGuardSeconds)
            {
                Log.Warn($"Cobro repetido ignorado: {dir} {amount} con {s.Name} (ya se hizo hace {(int)(DateTime.UtcNow - last.at).TotalSeconds} s)");
                return true;
            }
            return false;
        }

        private static void NoteGold(Speaker s, string dir, int amount)
        {
            LastGold[s.Key + "|" + dir] = (amount, DateTime.UtcNow);
            // Que lo vea en su historial: así no vuelve a pedirlo ni a cobrarlo
            try
            {
                s.Memory.Turns.Add(new Memory.ChatTurn
                {
                    Role = "player", Day = CampaignTime.Now.ToDays,
                    Text = dir == "take" ? $"[Done: they have handed you {amount} denars. It is paid in full — do not take or ask for it again.]"
                                         : $"[Done: you have handed them {amount} denars. It is given — do not give it again.]"
                });
            }
            catch { }
        }

        // --- comercio con la gente del lugar
        private static Settlement Market()
        {
            var st = Settlement.CurrentSettlement;
            return st != null && (st.IsTown || st.IsVillage) ? st : null;
        }

        /// <summary>¿Es alguien que vende y compra? (mercaderes, artesanos, taberneros, aldeanos; no guardias ni nobles)</summary>
        private static bool Trader(Speaker s)
        {
            if (!S.Get.AllowRealTrade || s.IsHero || s.Character == null || Market() == null || TaleWorlds.MountAndBlade.Mission.Current == null) return false;
            switch (s.Character.Occupation)
            {
                case Occupation.Merchant: case Occupation.GoodsTrader: case Occupation.Artisan: case Occupation.ShopWorker: case Occupation.Tavernkeeper:
                case Occupation.Villager: case Occupation.Townsfolk: case Occupation.Blacksmith: case Occupation.HorseTrader: case Occupation.TavernWench:
                    return true;
            }
            return false;
        }

        /// <summary>Precio de esa mercancía aquí. selling = lo que te pagan a ti (algo menos que lo que cuesta comprarla).</summary>
        private static int PriceOf(ItemObject item, bool selling)
        {
            try
            {
                var st = Market();
                int p = st?.Town != null ? st.Town.GetItemPrice(new EquipmentElement(item)) : st?.Village != null ? st.Village.GetItemPrice(item) : item.Value;
                return Math.Max(1, selling ? (int)(p * 0.85f) : p);
            }
            catch { return Math.Max(1, item.Value); }
        }

        private static bool HeldByPlayer(Speaker s) => s.IsHero && s.Hero.IsPrisoner && s.Hero.PartyBelongedToAsPrisoner == PartyBase.MainParty;

        /// <summary>En el mapa, frente a su partida: una pelea o un duelo se libra en la escena del terreno.</summary>
        private static bool MapFight(Speaker s) => s.IsHero && TaleWorlds.MountAndBlade.Mission.Current == null && Scenes.MapTalk.CanOffer(s.Hero);

        private static string StartMapFight(Speaker s, string kind)
        {
            Scenes.MapTalk.Queue(s.Hero, kind);
            return kind == "brawl" ? L.F("AIL_adc42c0a", $"{s.Name} desmonta y se remanga: lo arregláis a puñetazos ahí mismo.") : kind == "death" ? L.F("AIL_8322c30f", $"{s.Name} desmonta: duelo a muerte, aquí y ahora.") : L.F("AIL_ad1d7e50", $"{s.Name} desmonta y acepta el duelo, aquí y ahora.");
        }

        private static Kingdom PlayerKingdom => P.MapFaction as Kingdom;
        private static bool PlayerIsRuler => PlayerKingdom != null && PlayerKingdom.Leader == P;
        private static int Rel(Speaker s) => s.IsHero ? (int)s.Hero.GetRelationWithPlayer() : 0;
        private static bool IsPlayersPrisoner(Hero h) => h.IsPrisoner && h.PartyBelongedToAsPrisoner == MobileParty.MainParty?.Party;

        // --- órdenes en el mapa
        // Con una orden en curso solo se ofrece dejarla (resume_duties): así no encadena órdenes a medias
        private static bool CanOrder(Speaker s) => s.IsHero && OrderSystem.CanTakeOrders(s.Hero) && OrderSystem.Find(s.Hero) == null;
        private static bool AtWarWithPlayer(Speaker s) => s.Faction != null && P.MapFaction != null && s.Faction != P.MapFaction && s.Faction.IsAtWarWith(P.MapFaction);
        private static bool HasWars(Speaker s) { try { return s.Faction != null && s.Faction.FactionsAtWarWith.Any(f => f != null && f.IsKingdomFaction); } catch { return false; } }
        private static bool Hostile(Speaker s, Settlement st) => st?.MapFaction != null && s.Faction != null && st.MapFaction != s.Faction && st.MapFaction.IsAtWarWith(s.Faction);

        // --- traspasos
        private static MobileParty Led(Speaker s) => s.IsHero ? OrderSystem.PartyLedBy(s.Hero) : null;
        private static bool CanTransfer(Speaker s) => S.Get.AllowTransfers && Led(s) != null && !s.Hero.IsPrisoner && MobileParty.MainParty != null && !AtWarWithPlayer(s);

        /// <summary>Mueve soldados rasos (los de menor rango primero; con sanos basta). Devuelve cuántos.</summary>
        /// <summary>¿Es de esa clase de tropa? (infantería, arqueros, caballería, arqueros a caballo, o parte del nombre de la tropa)</summary>
        private static bool OfKind(CharacterObject c, string kind)
        {
            string k = new string((kind ?? "").ToLowerInvariant().Where(char.IsLetter).ToArray());
            if (k.Length < 3) return true;
            bool mounted = c.IsMounted, ranged = c.IsRanged;
            if (k.Contains("horsearch") || k.Contains("arquerosacaballo") || k.Contains("mountedarch")) return mounted && ranged;
            if (k.Contains("caval") || k.Contains("cabal") || k.Contains("horse") || k.Contains("jinete") || k.Contains("mounted")) return mounted && !ranged;
            if (k.Contains("arch") || k.Contains("arquer") || k.Contains("ballest") || k.Contains("crossbow") || k.Contains("ranged") || k.Contains("bow")) return ranged && !mounted;
            if (k.Contains("infant") || k.Contains("foot") || k.Contains("spear") || k.Contains("lancer") || k.Contains("pie")) return !mounted && !ranged;
            string name = new string(c.Name.ToString().ToLowerInvariant().Where(char.IsLetter).ToArray());
            return name.Contains(k) || k.Contains(name);
        }

        /// <summary>«20:archers» → 20 y «archers».</summary>
        private static void SplitTroops(string arg, out int count, out string kind)
        {
            arg = arg ?? "";
            int c = arg.IndexOf(':');
            count = ParseAmount(c >= 0 ? arg.Substring(0, c) : arg);
            kind = c >= 0 ? arg.Substring(c + 1).Trim() : "";
            if (count == 0 && c >= 0) { count = ParseAmount(arg.Substring(c + 1)); kind = arg.Substring(0, c).Trim(); }
        }

        private static int MoveTroops(TroopRoster from, TroopRoster to, int count, bool prisoners, string kind = null)
        {
            int moved = 0;
            foreach (var e in from.GetTroopRoster().Where(x => x.Character != null && !x.Character.IsHero && OfKind(x.Character, kind)).OrderBy(x => x.Character.Tier).ToList())
            {
                if (moved >= count) break;
                int have = prisoners ? e.Number : e.Number - e.WoundedNumber;
                int n = Math.Min(have, count - moved);
                if (n <= 0) continue;
                from.AddToCounts(e.Character, -n);
                to.AddToCounts(e.Character, n);
                moved += n;
            }
            return moved;
        }

        /// <summary>Prisionero con nombre (un héroe) dentro de un grupo.</summary>
        private static CharacterObject FindHeroPrisoner(MobileParty party, string name)
        {
            string n = new string((name ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            if (n.Length < 2 || n.All(char.IsDigit)) return null;
            foreach (var e in party.PrisonRoster.GetTroopRoster())
            {
                if (e.Character == null || !e.Character.IsHero) continue;
                string hn = new string(e.Character.Name.ToString().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
                if (hn == n || hn.Contains(n) || n.Contains(hn)) return e.Character;
            }
            return null;
        }

        private static bool Tense(Speaker s) => !S.Get.EnableTension || Bonds.Tension(s.Memory) >= S.Get.TensionForViolence;

        /// <summary>"Sargot:3" → nombre "Sargot", 3 días. "5" → sin nombre, 5 días.</summary>
        private static void SplitArg(string arg, out string name, out double days)
        {
            name = (arg ?? "").Trim(); days = 0;
            int c = name.LastIndexOf(':');
            string tail = c >= 0 ? name.Substring(c + 1) : name;
            var digits = new string(tail.Where(char.IsDigit).ToArray());
            if (digits.Length > 0 && tail.Trim().All(ch => char.IsDigit(ch) || char.IsWhiteSpace(ch) || char.IsLetter(ch)) && tail.Trim().Length <= 8 && char.IsDigit(tail.Trim()[0]))
            {
                double.TryParse(digits, out days);
                name = c >= 0 ? name.Substring(0, c).Trim() : "";
            }
        }

        private static readonly List<ActionDef> All = new List<ActionDef>
        {
            // ----------------------------------------------------------- dinero
            new ActionDef
            {
                Name = "give_gold", Usage = "give_gold:<amount>",
                Description = "You hand the other person that many denars from your own purse, right now. Only when you have truly decided to give or pay them " +
                              "(a gift, a reward, a ransom you agreed to pay, payment for a deal you accepted). Never while still negotiating.",
                IsAvailable = s => s.IsHero && s.Hero.Gold > 0,
                Execute = (s, arg, ctx) =>
                {
                    int amount = Math.Min(ParseAmount(arg), Math.Min(s.Hero.Gold, S.Get.MaxGoldPerAction));
                    if (amount <= 0) return null;
                    if (RepeatedGold(s, "give", amount)) return null;
                    GiveGoldAction.ApplyBetweenCharacters(s.Hero, P, amount);
                    NoteGold(s, "give", amount);
                    return L.F("AIL_4bf3e0c5", $"{s.Name} te da {amount} denarios.");
                }
            },
            new ActionDef
            {
                Name = "take_gold", Usage = "take_gold:<amount>",
                Description = "The other person pays you that many denars. ONLY when they clearly agreed to pay that exact amount in their last message " +
                              "(a price, a bribe, a tip, a debt). Never take money they did not offer or accept to give.",
                IsAvailable = s => P.Gold > 0,
                Execute = (s, arg, ctx) =>
                {
                    int amount = Math.Min(ParseAmount(arg), S.Get.MaxGoldPerAction);
                    if (amount <= 0) return null;
                    if (RepeatedGold(s, "take", amount)) return null;
                    if (P.Gold < amount) return L.F("AIL_2d756f08", $"No tienes {amount} denarios para pagar a {s.Name}.");
                    if (s.IsHero) GiveGoldAction.ApplyBetweenCharacters(P, s.Hero, amount);
                    else P.ChangeHeroGold(-amount);
                    NoteGold(s, "take", amount);
                    return L.F("AIL_800a1324", $"Pagas {amount} denarios a {s.Name}.");
                }
            },

            // ----------------------------------------------------------- compañeros
            new ActionDef
            {
                Name = "join_party", Usage = "join_party",
                Description = "You join the other person's band as a companion and travel with them from now on. Only if they asked you to join AND you accept " +
                              "(usually after being paid your price with take_gold in the same reply or earlier).",
                IsAvailable = s => s.IsHero && s.Hero.IsWanderer && s.Hero.CompanionOf == null && !s.Hero.IsPrisoner && s.Hero.PartyBelongedTo == null
                                   && Clan.PlayerClan != null && Clan.PlayerClan.Companions.Count < Clan.PlayerClan.CompanionLimit,
                Execute = (s, arg, ctx) =>
                {
                    AddCompanionAction.Apply(Clan.PlayerClan, s.Hero);
                    AddHeroToPartyAction.Apply(s.Hero, MobileParty.MainParty);
                    return L.F("AIL_85a42b1a", $"{s.Name} se une a tu grupo como compañero.");
                }
            },

            // ----------------------------------------------------------- prisioneros
            new ActionDef
            {
                Name = "set_free", Usage = "set_free",
                Description = "You (their prisoner) are released and walk free. Only when your captor clearly said, in their last message, that they let you go " +
                              "(out of mercy, a deal, or after your ransom was paid with give_gold).",
                IsAvailable = s => s.IsHero && IsPlayersPrisoner(s.Hero),
                Execute = (s, arg, ctx) =>
                {
                    EndCaptivityAction.ApplyByReleasedByChoice(s.Hero, P);
                    ctx.EndConversation = true;
                    return L.F("AIL_77ec28e0", $"Liberas a {s.Name}.");
                }
            },

            // ----------------------------------------------------------- política
            new ActionDef
            {
                Name = "swear_fealty", Usage = "swear_fealty",
                Description = "You and your whole clan swear fealty to the other person's kingdom and become their vassals, leaving your current realm if you have one. " +
                              "A huge decision: only after real persuasion, good relations and a clear offer from them, and only if it suits your interests.",
                IsAvailable = s => S.Get.AllowFealty && s.IsHero && s.Hero.IsLord && s.Hero.IsClanLeader && !s.Hero.IsKingdomLeader && !s.Hero.IsPrisoner
                                   && PlayerIsRuler && s.Hero.Clan != null && !s.Hero.Clan.IsMinorFaction && s.Hero.Clan.Kingdom != PlayerKingdom && Rel(s) >= 0,
                Execute = (s, arg, ctx) =>
                {
                    var clan = s.Hero.Clan;
                    if (clan.Kingdom != null) ChangeKingdomAction.ApplyByJoinToKingdomByDefection(clan, clan.Kingdom, PlayerKingdom);
                    else ChangeKingdomAction.ApplyByJoinToKingdom(clan, PlayerKingdom);
                    return L.F("AIL_bdc30505", $"El clan {clan.Name} jura lealtad a {PlayerKingdom.Name}.");
                }
            },
            new ActionDef
            {
                Name = "serve_as_mercenary", Usage = "serve_as_mercenary",
                Description = "Your clan enters the other person's kingdom as paid mercenaries. Only after you agreed to the deal.",
                IsAvailable = s => S.Get.AllowFealty && s.IsHero && s.Hero.IsClanLeader && !s.Hero.IsPrisoner && s.Hero.Clan != null && s.Hero.Clan.IsMinorFaction
                                   && s.Hero.Clan.Kingdom == null && !s.Hero.Clan.IsBanditFaction && PlayerIsRuler,
                Execute = (s, arg, ctx) =>
                {
                    ChangeKingdomAction.ApplyByJoinFactionAsMercenary(s.Hero.Clan, PlayerKingdom);
                    return L.F("AIL_cdf9a5fc", $"El clan {s.Hero.Clan.Name} sirve ahora a {PlayerKingdom.Name} como mercenarios.");
                }
            },
            new ActionDef
            {
                Name = "declare_war", Usage = "declare_war",
                Description = "As ruler, you declare war on the other person's kingdom, effective immediately. Only for grave insults, threats or when it truly serves your realm.",
                IsAvailable = s => S.Get.AllowDiplomacy && s.IsHero && s.Hero.IsKingdomLeader && PlayerIsRuler && s.Hero.MapFaction != PlayerKingdom
                                   && !s.Hero.MapFaction.IsAtWarWith(PlayerKingdom),
                Execute = (s, arg, ctx) =>
                {
                    DeclareWarAction.ApplyByDefault(s.Hero.MapFaction, PlayerKingdom);
                    ctx.EndConversation = true;
                    return L.F("AIL_ae73db8a", $"¡{s.Hero.MapFaction.Name} declara la guerra a {PlayerKingdom.Name}!");
                }
            },
            new ActionDef
            {
                Name = "make_peace", Usage = "make_peace",
                Description = "As ruler, you agree to peace between your kingdom and theirs, effective immediately. Only when you have truly accepted the terms " +
                              "(often after tribute was paid with take_gold).",
                IsAvailable = s => S.Get.AllowDiplomacy && s.IsHero && s.Hero.IsKingdomLeader && PlayerIsRuler && s.Hero.MapFaction.IsAtWarWith(PlayerKingdom),
                Execute = (s, arg, ctx) =>
                {
                    MakePeaceAction.Apply(s.Hero.MapFaction, PlayerKingdom);
                    return L.F("AIL_7def1f8e", $"{s.Hero.MapFaction.Name} y {PlayerKingdom.Name} firman la paz.");
                }
            },

            // ----------------------------------------------------------- matrimonio y romance
            new ActionDef
            {
                Name = "accept_marriage", Usage = "accept_marriage",
                Description = "You accept the other person's marriage proposal and marry them right now. Only if they explicitly proposed and you truly love or want this union.",
                IsAvailable = s => S.Get.AllowMarriage && s.IsHero && !s.Hero.IsPrisoner && Rel(s) >= S.Get.MinRelationForMarriage
                                   && (!S.Get.EnableRomance || s.Memory.Romance >= S.Get.RomanceForMarriage)
                                   && CanWed(s.Hero),
                Execute = (s, arg, ctx) =>
                {
                    var h = s.Hero;
                    try
                    {
                        // Quien no es noble (compañero, errante, notable sin casa) entra antes en tu casa como noble
                        if (!SafeSuitable(h))
                        {
                            if (h.GovernorOf != null) ChangeGovernorAction.RemoveGovernorOf(h);
                            if (h.IsPlayerCompanion) h.CompanionOf = null;
                            if (h.Occupation != Occupation.Lord) h.SetNewOccupation(Occupation.Lord);
                            if (h.Clan == null || h.Clan != Clan.PlayerClan && h.Clan.Leader != h) h.Clan = Clan.PlayerClan;
                        }
                        // Si alguno seguía casado (ajuste «romance con casados»), ese matrimonio termina
                        if (h.Spouse != null && h.Spouse != P) { var ex = h.Spouse; h.Spouse = null; if (ex.Spouse == h) ex.Spouse = null; }
                        if (P.Spouse != null && P.Spouse != h) { var ex = P.Spouse; P.Spouse = null; if (ex.Spouse == P) ex.Spouse = null; }
                    }
                    catch (Exception ex) { Log.Error("Preparar la boda", ex); }
                    if (P.Spouse == h) return null;
                    MarriageAction.Apply(P, h);
                    Bonds.ChangeRomance(s, 10);
                    return L.F("AIL_fd4aafcb", $"¡Te casas con {s.Name}!");
                }
            },
            new ActionDef
            {
                Name = "intimacy", Usage = "intimacy",
                Description = "You withdraw with the other person to be alone together as lovers. Only if they clearly asked or the moment clearly led there, you want it, " +
                              "and the place and time allow it. Your reply only hints at it and fades out — never describe it.",
                IsAvailable = s => S.Get.AllowIntimacy && Bonds.RomancePossible(s) && !s.Hero.IsPrisoner && s.Memory.Romance >= S.Get.RomanceForIntimacy
                                   && (s.Memory.LastIntimacyDay < 0 || CampaignTime.Now.ToDays - s.Memory.LastIntimacyDay >= 1),
                Execute = (s, arg, ctx) => Bonds.Intimacy(s)
            },

            // ----------------------------------------------------------- comercio de verdad con la gente del lugar
            new ActionDef
            {
                Name = "sell_goods", Usage = "sell_goods:<item name>:<amount>",
                Description = "You sell them that many units of something from this place's market, at the going price (they pay at once, the goods go to their baggage). " +
                              "Only when they clearly asked to buy it and accepted the price you named. You can only sell what is listed as in stock.",
                IsAvailable = s => Trader(s) && Market()?.ItemRoster != null && Market().ItemRoster.Count > 0,
                Extra = s => "In stock here (price each): " + string.Join(", ", Market().ItemRoster.Where(e => e.Amount > 0 && e.EquipmentElement.Item != null && (e.EquipmentElement.Item.IsTradeGood || e.EquipmentElement.Item.IsFood || e.EquipmentElement.Item.IsAnimal))
                                 .OrderByDescending(e => e.Amount).Take(12).Select(e => $"{e.EquipmentElement.Item.Name} {PriceOf(e.EquipmentElement.Item, false)} ({e.Amount})")) + ".",
                Execute = (s, arg, ctx) =>
                {
                    var st = Market(); if (st == null) return null;
                    int c = (arg ?? "").LastIndexOf(':');
                    string name = c > 0 ? arg.Substring(0, c) : arg; int qty = Math.Max(1, c > 0 ? ParseAmount(arg.Substring(c + 1)) : 1);
                    var item = FindInRoster(st.ItemRoster, name, out int have);
                    if (item == null || have <= 0) return null;
                    qty = Math.Min(qty, have);
                    int cost = PriceOf(item, false) * qty;
                    if (P.Gold < cost) return L.F("AIL_3c14bb80", $"No te llega para {qty} de {item.Name} ({cost} denarios).");
                    GiveGoldAction.ApplyForCharacterToSettlement(P, st, cost, true);
                    st.ItemRoster.AddToCounts(item, -qty);
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, qty);
                    return L.F("AIL_b83bfea6", $"Compras {qty} de {item.Name} por {cost} denarios.");
                }
            },
            new ActionDef
            {
                Name = "buy_goods", Usage = "buy_goods:<item name>:<amount>",
                Description = "You buy from them that many units of something they carry, at the going price here (you pay at once from the market's purse). " +
                              "Only when they clearly offered to sell it and you agreed on it.",
                IsAvailable = s => Trader(s) && MobileParty.MainParty.ItemRoster.Count > 0,
                Extra = s => "They carry (what it fetches here, each): " + string.Join(", ", MobileParty.MainParty.ItemRoster.Where(e => e.Amount > 0 && e.EquipmentElement.Item != null && (e.EquipmentElement.Item.IsTradeGood || e.EquipmentElement.Item.IsFood || e.EquipmentElement.Item.IsAnimal))
                                 .OrderByDescending(e => e.Amount).Take(12).Select(e => $"{e.EquipmentElement.Item.Name} {PriceOf(e.EquipmentElement.Item, true)} ({e.Amount})")) + ".",
                Execute = (s, arg, ctx) =>
                {
                    var st = Market(); if (st == null) return null;
                    int c = (arg ?? "").LastIndexOf(':');
                    string name = c > 0 ? arg.Substring(0, c) : arg; int qty = Math.Max(1, c > 0 ? ParseAmount(arg.Substring(c + 1)) : 1);
                    var item = FindInRoster(MobileParty.MainParty.ItemRoster, name, out int have);
                    if (item == null || have <= 0) return null;
                    qty = Math.Min(qty, have);
                    int pay = PriceOf(item, true) * qty;
                    int purse = st.Town != null ? st.Town.Gold : pay;
                    if (purse < pay) { qty = Math.Max(0, purse / Math.Max(1, PriceOf(item, true))); pay = PriceOf(item, true) * qty; }
                    if (qty <= 0) return L.T("AIL_b720cde3", "El mercado no tiene dinero para comprártelo.");
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, -qty);
                    st.ItemRoster.AddToCounts(item, qty);
                    GiveGoldAction.ApplyForSettlementToCharacter(st, P, pay, true);
                    return L.F("AIL_fba371fc", $"Vendes {qty} de {item.Name} por {pay} denarios.");
                }
            },

            // ----------------------------------------------------------- de figurante a personaje
            new ActionDef
            {
                Name = "be_remembered", Usage = "be_remembered",
                Description = "From now on you are someone they know by name and can find again in this place: you become a lasting person in their story. " +
                              "ONLY if they clearly said they want to see you again, to keep dealing with you, or to court you, and you agree to it. Not for a passing chat.",
                IsAvailable = s => S.Get.AllowPromotion && !s.IsHero && s.Age >= 18f && s.Memory.Turns.Count >= 4 && s.ResidentSettlement != null,
                Execute = (s, arg, ctx) =>
                {
                    var h = World.People.Promote(s, SceneAgent(), false, out string err);
                    if (h == null) { Log.Warn("No se pudo convertir en personaje: " + err); return null; }
                    return L.F("AIL_24fc64b9", $"{h.Name} ya es alguien que conoces: podrás volver a encontrarle en {s.ResidentSettlement.Name}.");
                }
            },

            // ----------------------------------------------------------- dentro de una escena: ir contigo andando
            new ActionDef
            {
                Name = "come_along", Usage = "come_along",
                Description = "You walk along with the other person through this place, on foot, from now on: they asked you to come with them somewhere nearby and you agree to go. " +
                              "Use it in the SAME reply in which you say you will follow them; saying it without this action means you stay where you are.",
                IsAvailable = s => SceneAgent() != null && !SceneActions.Busy(SceneAgent()),
                Execute = (s, arg, ctx) =>
                {
                    var a = SceneAgent();
                    if (a == null) return null;
                    SceneActions.Apply(a, "follow", new List<TaleWorlds.MountAndBlade.Agent>());
                    return SceneActions.Busy(a) ? L.F("AIL_a9fce24e", $"{s.Name} te acompañará cuando termines de hablar.") : null;
                }
            },
            new ActionDef
            {
                Name = "stay_here", Usage = "stay_here",
                Description = "You stop walking along with the other person and remain here (they told you to stay, or you will go no further).",
                IsAvailable = s => SceneAgent() != null && SceneActions.Busy(SceneAgent()),
                Execute = (s, arg, ctx) =>
                {
                    var a = SceneAgent();
                    if (a == null) return null;
                    SceneActions.Apply(a, "stay", new List<TaleWorlds.MountAndBlade.Agent>());
                    return L.F("AIL_78680a96", $"{s.Name} se queda aquí.");
                }
            },

            // ----------------------------------------------------------- órdenes en el mapa
            new ActionDef
            {
                Name = "escort", Usage = "escort:<name of the party or of its leader>:<days>", Letter = true,
                Description = "You ride with your party beside ANOTHER party (not theirs) to protect it for that many days: a caravan, a villager party, a kinsman's or an ally's war band. " +
                              "Use it when they ask you to guard or accompany someone else.",
                IsAvailable = s => CanOrder(s) && !AtWarWithPlayer(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var who, out var days);
                    var target = OrderSystem.FindFriendlyParty(s.Hero, who);
                    if (target == null) return null;
                    if (!OrderSystem.Give(s.Hero, "escort", target.StringId, target.Name.ToString(), days)) return null;
                    return L.F("AIL_0107d6be", $"{s.Name} irá escoltando a {target.Name}.");
                }
            },
            new ActionDef
            {
                Name = "lead_party", Usage = "lead_party",
                Description = "You take command of a new war party of their clan and ride out on your own from now on. ONLY if you are one of their companions riding in their party, " +
                              "they have clearly asked you to lead a party of your own, and you accept.",
                IsAvailable = s => s.IsHero && s.Hero.Clan == Clan.PlayerClan && s.Hero.PartyBelongedTo == MobileParty.MainParty && s.Hero != P && !s.Hero.IsWounded,
                Execute = (s, arg, ctx) =>
                {
                    try
                    {
                        var party = Helpers.MobilePartyHelper.CreateNewClanMobileParty(s.Hero, Clan.PlayerClan);
                        if (party == null) return null;
                        ctx.EndConversation = true;
                        return L.F("AIL_411fbc76", $"{s.Name} toma el mando de una partida propia del clan.");
                    }
                    catch (Exception ex) { Log.Error("Crear partida del clan", ex); return null; }
                }
            },
            new ActionDef
            {
                Name = "follow_me", Usage = "follow_me:<days>", Letter = true,
                Description = "You lead your party to the other person and ride alongside their party, following them for that many days. " +
                              "By letter this means: you set out now to join them wherever they are.",
                IsAvailable = s => CanOrder(s) && !AtWarWithPlayer(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out _, out var days);
                    if (!OrderSystem.Give(s.Hero, "follow", null, P.Name.ToString(), days)) return null;
                    return ctx.ViaLetter ? L.F("AIL_3e251e73", $"{s.Name} se pone en camino para reunirse contigo.") : L.F("AIL_cc47eaca", $"{s.Name} te seguirá con su grupo.");
                }
            },
            new ActionDef
            {
                Name = "meet_at", Usage = "meet_at:<town, castle or village>", Letter = true,
                Description = "You travel with your party to that place and wait there for the other person for a few days, to meet them in person.",
                IsAvailable = s => CanOrder(s) && !AtWarWithPlayer(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out var days);
                    var st = OrderSystem.FindSettlement(name, x => !Hostile(s, x));
                    if (st == null) return null;
                    // tiempo de sobra para llegar; la espera empieza a contar al llegar
                    if (!OrderSystem.Give(s.Hero, "meet", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays)) return null;
                    return L.F("AIL_6a69d3fd", $"{s.Name} irá a {st.Name} y te esperará allí.");
                }
            },
            new ActionDef
            {
                Name = "go_to", Usage = "go_to:<town, castle or village>  or  go_to:<place>:return", Letter = true,
                Description = "You take your party to that place and then go back to your own business. Add \":return\" if you agreed to come back to the other person " +
                              "afterwards and ride with them again.",
                IsAvailable = s => CanOrder(s),
                Execute = (s, arg, ctx) =>
                {
                    arg = (arg ?? "").Trim();
                    bool back = false;
                    foreach (var suffix in new[] { ":return", ":back", ":and return", ":then return" })
                        if (arg.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { back = true; arg = arg.Substring(0, arg.Length - suffix.Length); break; }
                    if (back && AtWarWithPlayer(s)) back = false;
                    SplitArg(arg, out var name, out _);
                    var st = OrderSystem.FindSettlement(name, x => !Hostile(s, x));
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "goto", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays, back ? "follow" : null)) return null;
                    return back ? L.F("AIL_9c4ee846", $"{s.Name} parte hacia {st.Name} y después volverá contigo.") : L.F("AIL_e70b1e8a", $"{s.Name} parte hacia {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "wait_here", Usage = "wait_here:<days>", Letter = true,
                Description = "You hold your party where it is now and wait for that many days.",
                IsAvailable = s => CanOrder(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out _, out var days);
                    if (!OrderSystem.Give(s.Hero, "wait", null, "", days)) return null;
                    return L.F("AIL_df1f7dd9", $"{s.Name} esperará aquí con su grupo.");
                }
            },
            new ActionDef
            {
                Name = "patrol", Usage = "patrol:<town, castle or village>:<days>", Letter = true,
                Description = "You patrol with your party around that place for that many days, protecting it.",
                IsAvailable = s => CanOrder(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out var days);
                    var st = OrderSystem.FindSettlement(name);
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "patrol", st.StringId, st.Name.ToString(), days)) return null;
                    return L.F("AIL_611c4786", $"{s.Name} patrullará los alrededores de {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "attack_party", Usage = "attack_party:<name of the enemy party>",
                Description = "You ride out with your party to hunt down and attack that enemy party. Only against parties you could plausibly beat or must stop.",
                IsAvailable = s => CanOrder(s) && OrderSystem.HostilesNear(s.Hero, 1).Count > 0,
                Extra = s => "Enemy parties near you right now: " + string.Join("; ", OrderSystem.HostilesNear(s.Hero).Select(p => $"{p.Name} ({p.MemberRoster.TotalManCount} men)")) +
                             $". Your own party has {OrderSystem.PartyLedBy(s.Hero)?.MemberRoster.TotalManCount} men.",
                Execute = (s, arg, ctx) =>
                {
                    var target = OrderSystem.FindHostile(s.Hero, arg);
                    if (target == null) return null;
                    if (!OrderSystem.Give(s.Hero, "attack", target.StringId, target.Name.ToString(), S.Get.OrderDefaultDays)) return null;
                    return L.F("AIL_1fb2c5a1", $"{s.Name} sale a dar caza a {target.Name}.");
                }
            },
            new ActionDef
            {
                Name = "besiege", Usage = "besiege:<enemy town or castle>", Letter = true,
                Description = "You march your party to lay siege to that town or castle of a realm you are at war with. A serious military commitment: only with enough men or as part of a real plan.",
                IsAvailable = s => CanOrder(s) && HasWars(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out _);
                    var st = OrderSystem.FindSettlement(name, x => x.IsFortification && Hostile(s, x));
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "besiege", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays)) return null;
                    return L.F("AIL_77e4bc34", $"{s.Name} marcha a asediar {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "raid", Usage = "raid:<enemy village>", Letter = true,
                Description = "You take your party to raid and burn that village of a realm you are at war with. Merciful or honourable characters refuse to prey on peasants.",
                IsAvailable = s => CanOrder(s) && HasWars(s),
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out _);
                    var st = OrderSystem.FindSettlement(name, x => x.IsVillage && Hostile(s, x) && !x.IsRaided);
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "raid", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays)) return null;
                    return L.F("AIL_8279d8bb", $"{s.Name} parte a saquear {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "resume_duties", Usage = "resume_duties", Letter = true,
                Description = "You stop doing what you had agreed with them (following, waiting, patrolling, marching...) and go back to your own business.",
                IsAvailable = s => s.IsHero && OrderSystem.Find(s.Hero) != null,
                Extra = s => $"Right now: {OrderSystem.Describe(s.Hero)}. While that stands you can take no other order with your party: if they want something else, use resume_duties now and agree the new thing in your next reply.",
                Execute = (s, arg, ctx) => OrderSystem.Cancel(s.Hero, silent: true) ? L.F("AIL_7cd9f263", $"{s.Name} vuelve a sus asuntos.") : null
            },

            // ----------------------------------------------------------- traspaso de tropas y prisioneros
            new ActionDef
            {
                Name = "give_troops", Usage = "give_troops:<number>  or  give_troops:<number>:<infantry | archers | cavalry | horse archers | a troop name>",
                Description = "You hand that many of your own soldiers over to the other person's party, for good; optionally only of one kind. Only when you truly agreed to (an order from your liege, " +
                              "a loan to a friend, a paid deal). You never give more than a third of your men at once.",
                IsAvailable = s => CanTransfer(s) && Led(s).MemberRoster.TotalRegulars >= 6,
                Extra = s => "Your men: " + string.Join(", ", Led(s).MemberRoster.GetTroopRoster().Where(e => !e.Character.IsHero && e.Number > 0).OrderByDescending(e => e.Number).Take(8).Select(e => $"{e.Number} {e.Character.Name}")) + ".",
                Execute = (s, arg, ctx) =>
                {
                    var p = Led(s);
                    SplitTroops(arg, out int want, out string kind);
                    int n = Math.Min(want, Math.Min(S.Get.MaxTroopsPerTransfer, p.MemberRoster.TotalRegulars / 3));
                    if (n <= 0) return null;
                    int moved = MoveTroops(p.MemberRoster, MobileParty.MainParty.MemberRoster, n, false, kind);
                    return moved > 0 ? L.F("AIL_24ba54ab", $"{s.Name} te cede {moved} soldados{(kind.Length > 0 ? " (" + kind + ")" : "")}.") : null;
                }
            },
            new ActionDef
            {
                Name = "take_troops", Usage = "take_troops:<number>  or  take_troops:<number>:<infantry | archers | cavalry | horse archers | a troop name>",
                Description = "You take into your party that many soldiers the other person is handing over to you (optionally only of the kind they named). ONLY when they clearly offered those men in their last message and you accept.",
                IsAvailable = s => CanTransfer(s) && MobileParty.MainParty.MemberRoster.TotalRegulars > 0,
                Execute = (s, arg, ctx) =>
                {
                    SplitTroops(arg, out int want, out string kind);
                    int n = Math.Min(want, S.Get.MaxTroopsPerTransfer);
                    if (n <= 0) return null;
                    int moved = MoveTroops(MobileParty.MainParty.MemberRoster, Led(s).MemberRoster, n, false, kind);
                    return moved > 0 ? L.F("AIL_bca1e090", $"Cedes {moved} soldados{(kind.Length > 0 ? " (" + kind + ")" : "")} a {s.Name}.") : null;
                }
            },
            new ActionDef
            {
                Name = "give_prisoners", Usage = "give_prisoners:<number, or the name of a noble prisoner>",
                Description = "You hand prisoners you hold over to the other person: that many common prisoners, or one named noble captive. Only when you truly agreed to.",
                IsAvailable = s => CanTransfer(s) && Led(s).PrisonRoster.TotalManCount > 0,
                Extra = s => { var names = Led(s).PrisonRoster.GetTroopRoster().Where(e => e.Character != null && e.Character.IsHero).Select(e => e.Character.Name.ToString()).ToList();
                               return $"You hold {Led(s).PrisonRoster.TotalRegulars} common prisoners" + (names.Count > 0 ? " and these nobles: " + string.Join(", ", names) + "." : "."); },
                Execute = (s, arg, ctx) =>
                {
                    var p = Led(s);
                    var noble = FindHeroPrisoner(p, arg);
                    if (noble != null) { TransferPrisonerAction.Apply(noble, p.Party, MobileParty.MainParty.Party); return L.F("AIL_f378719a", $"{s.Name} te entrega a su prisionero {noble.Name}."); }
                    int n = Math.Min(ParseAmount(arg), 500);
                    if (n <= 0) return null;
                    int moved = MoveTroops(p.PrisonRoster, MobileParty.MainParty.PrisonRoster, n, true);
                    return moved > 0 ? L.F("AIL_dec042f4", $"{s.Name} te entrega {moved} prisioneros.") : null;
                }
            },
            new ActionDef
            {
                Name = "take_prisoners", Usage = "take_prisoners:<number, or the name of a noble prisoner>",
                Description = "You take charge of prisoners the other person is handing over to you. ONLY when they clearly offered them in their last message and you accept.",
                IsAvailable = s => CanTransfer(s) && MobileParty.MainParty.PrisonRoster.TotalManCount > 0,
                Execute = (s, arg, ctx) =>
                {
                    var main = MobileParty.MainParty;
                    var noble = FindHeroPrisoner(main, arg);
                    if (noble != null)
                    {
                        if (noble.HeroObject == s.Hero) return null;
                        TransferPrisonerAction.Apply(noble, main.Party, Led(s).Party);
                        return L.F("AIL_7ed3beae", $"Entregas a {s.Name} a tu prisionero {noble.Name}.");
                    }
                    int n = Math.Min(ParseAmount(arg), 500);
                    if (n <= 0) return null;
                    int moved = MoveTroops(main.PrisonRoster, Led(s).PrisonRoster, n, true);
                    return moved > 0 ? L.F("AIL_2d97f1db", $"Entregas {moved} prisioneros a {s.Name}.") : null;
                }
            },

            // ----------------------------------------------------------- objetos, documentos y talleres
            new ActionDef
            {
                Name = "give_item", Usage = "give_item:<item name>:<amount>",
                Description = "You hand the other person that many of an item you really have (goods, food, a weapon, a horse), for good. Only when you truly decided to: a gift, a payment in kind, " +
                              "a reward, or a sale they already paid for with take_gold. Never give more than you would plausibly part with.",
                IsAvailable = s => S.Get.AllowItems && MobileParty.MainParty != null && !(s.IsHero && s.Hero.IsPrisoner),
                Extra = s => Led(s) != null ? "What your party carries: " + ListItems(Led(s).ItemRoster, 12) + "."
                                            : "You have no baggage train here: you can only hand over everyday goods a person like you would have at hand (food, cloth, tools, trade goods).",
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out var amount);
                    int n = Math.Max(1, (int)amount);
                    var p = Led(s);
                    ItemObject item; int have;
                    if (p != null) item = FindInRoster(p.ItemRoster, name, out have);
                    else { item = FindCommonGood(name); have = 20; }
                    if (item == null) return null;
                    n = Math.Min(n, have);
                    if (item.Value > 0) n = Math.Min(n, Math.Max(1, S.Get.MaxItemValuePerAction / item.Value));
                    if (n <= 0) return null;
                    if (p != null) p.ItemRoster.AddToCounts(item, -n);
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, n);
                    return L.F("AIL_8fb29764", $"{s.Name} te da {n} × {item.Name}.");
                }
            },
            new ActionDef
            {
                Name = "take_item", Usage = "take_item:<item name>:<amount>",
                Description = "You accept that many of an item the other person is handing you. ONLY when they clearly offered that item in their last message (a gift, a bribe, goods you asked for, " +
                              "payment in kind) and you take it. If they do not actually have it, nothing happens.",
                IsAvailable = s => S.Get.AllowItems && MobileParty.MainParty != null && MobileParty.MainParty.ItemRoster.Count > 0,
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out var amount);
                    var item = FindInRoster(MobileParty.MainParty.ItemRoster, name, out int have);
                    if (item == null) return L.F("AIL_e00ce40a", $"No llevas «{name}» para dárselo a {s.Name}.");
                    int n = Math.Min(Math.Max(1, (int)amount), have);
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, -n);
                    Led(s)?.ItemRoster.AddToCounts(item, n);
                    QuestObjectives.OnItemTaken(s, item, n);
                    return L.F("AIL_a3c89c8a", $"Entregas {n} × {item.Name} a {s.Name}.");
                }
            },
            new ActionDef
            {
                Name = "give_document", Usage = "give_document:<short name>|<what it is and what it says, one or two sentences>",
                Description = "You write or hand over a document or token that the other person will carry and can show to others: a letter of introduction, a safe-conduct, a signed contract, " +
                              "a receipt for a debt, a sealed order, a ring or keepsake. Only when you truly decided to give it. Write the name and contents in English.",
                IsAvailable = s => S.Get.AllowItems && s.IsHero && !s.Hero.IsPrisoner && Belongings.Items.Count < 40,
                Execute = (s, arg, ctx) =>
                {
                    var parts = (arg ?? "").Split(new[] { '|' }, 2);
                    string name = parts[0].Trim(), desc = parts.Length > 1 ? parts[1].Trim() : "";
                    if (name.Length < 2) return null;
                    if (name.Length > 60) name = name.Substring(0, 60);
                    if (desc.Length > 300) desc = desc.Substring(0, 300);
                    Belongings.Items.RemoveAll(i => Belongings.Norm(i.Name) == Belongings.Norm(name));
                    Belongings.Items.Add(new RpItem { Name = name, Description = desc, FromId = s.Hero.StringId, FromName = s.Name, Day = CampaignTime.Now.ToDays });
                    return L.F("AIL_0fbba7b1", $"{s.Name} te entrega: {name}.");
                }
            },
            new ActionDef
            {
                Name = "take_document", Usage = "take_document:<name of the document or token>",
                Description = "You take and keep one of the documents or tokens the other person carries (listed above). ONLY when they clearly handed it to you in their last message.",
                IsAvailable = s => S.Get.AllowItems && Belongings.Items.Count > 0,
                Execute = (s, arg, ctx) =>
                {
                    var it = Belongings.Find(arg);
                    if (it == null) return null;
                    Belongings.Items.Remove(it);
                    QuestObjectives.OnDocumentTaken(s, it);
                    return L.F("AIL_a15797e0", $"Entregas a {s.Name}: {it.Name}.");
                }
            },
            new ActionDef
            {
                Name = "sell_workshop", Usage = "sell_workshop:<workshop name>:<agreed price in denars>",
                Description = "You sell one of your workshops in this town to the other person for the price you both agreed, and they pay you now. Only after the price was clearly agreed by both; " +
                              "a workshop is a livelihood, so you do not sell cheap or to someone you dislike.",
                IsAvailable = s => S.Get.AllowItems && s.IsHero && !s.Hero.IsPrisoner && s.Settlement != null && s.Settlement.IsTown
                                   && s.Hero.OwnedWorkshops.Any(w => w.Settlement == s.Settlement)
                                   && P.OwnedWorkshops.Count < Campaign.Current.Models.WorkshopModel.GetMaxWorkshopCountForClanTier(PC.Tier),
                Extra = s => "Your workshops here: " + string.Join("; ", s.Hero.OwnedWorkshops.Where(w => w.Settlement == s.Settlement)
                                 .Select(w => $"{w.Name} (worth about {Campaign.Current.Models.WorkshopModel.GetCostForPlayer(w)} denars)")) + ".",
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out var priceD);
                    int price = (int)priceD;
                    var mine = s.Hero.OwnedWorkshops.Where(w => w.Settlement == s.Settlement).ToList();
                    var ws = mine.FirstOrDefault(w => NormName(w.Name.ToString()).Contains(NormName(name)) || NormName(name).Contains(NormName(w.Name.ToString()))) ?? mine.FirstOrDefault();
                    if (ws == null || price <= 0) return null;
                    if (P.Gold < price) return L.F("AIL_20cbe084", $"No tienes {price} denarios para comprar el taller de {s.Name}.");
                    string wname = ws.Name.ToString();
                    GiveGoldAction.ApplyBetweenCharacters(P, s.Hero, price);
                    ChangeOwnerOfWorkshopAction.ApplyByWar(ws, P, ws.WorkshopType);
                    return L.F("AIL_c70d2ac3", $"Compras a {s.Name} el taller «{wname}» por {price} denarios.");
                }
            },

            // ----------------------------------------------------------- reino y clan
            new ActionDef
            {
                Name = "grant_fief", Usage = "grant_fief:<town or castle of yours>",
                Description = "You give one of your clan's towns or castles to the other person's clan, for good. A lord's lands are his power: only as a ruler rewarding great service, " +
                              "to settle a heavy debt of honour, or under real duress. Never your last fief unless you are truly beaten.",
                IsAvailable = s => S.Get.AllowFiefs && s.IsHero && s.Hero.IsClanLeader && !s.Hero.IsPrisoner && KingdomOf(s) != null && KingdomOf(s) == P.MapFaction
                                   && !PlayerMercenary && s.Hero.Clan != PC && FiefsOf(s.Hero.Clan).Count > 0,
                Extra = s => "Your clan holds: " + string.Join(", ", FiefsOf(s.Hero.Clan).Select(x => x.Name.ToString())) + ".",
                Execute = (s, arg, ctx) =>
                {
                    var st = OrderSystem.FindSettlement(arg, x => (x.IsTown || x.IsCastle) && x.OwnerClan == s.Hero.Clan && !x.IsUnderSiege);
                    if (st == null) return null;
                    ChangeOwnerOfSettlementAction.ApplyByGift(st, P);
                    return L.F("AIL_3b64d032", $"{s.Name} te concede {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "accept_fief", Usage = "accept_fief:<town or castle of theirs>",
                Description = "You accept a town or castle the other person is giving to your clan. ONLY when they clearly offered that exact place in their last message.",
                IsAvailable = s => S.Get.AllowFiefs && s.IsHero && !s.Hero.IsPrisoner && s.Hero.Clan != null && s.Hero.Clan != PC && s.Hero.Clan.Leader != null
                                   && KingdomOf(s) != null && KingdomOf(s) == P.MapFaction && !s.Hero.Clan.IsUnderMercenaryService && FiefsOf(PC).Count > 0,
                Execute = (s, arg, ctx) =>
                {
                    var st = OrderSystem.FindSettlement(arg, x => (x.IsTown || x.IsCastle) && x.OwnerClan == PC && !x.IsUnderSiege);
                    if (st == null) return null;
                    ChangeOwnerOfSettlementAction.ApplyByGift(st, s.Hero.Clan.Leader);
                    return L.F("AIL_566b6114", $"Entregas {st.Name} al clan {s.Hero.Clan.Name}.");
                }
            },
            new ActionDef
            {
                Name = "accept_vassal", Usage = "accept_vassal",
                Description = "As ruler, you take the other person and their clan as sworn vassals of your kingdom, effective now. Only if they asked for it or accepted your offer, " +
                              "and you judge them worthy (name, deeds, how far you trust them).",
                IsAvailable = s => S.Get.AllowFealty && NpcIsRuler(s) && PC != null && (PC.Kingdom == null || (PC.Kingdom == KingdomOf(s) && PlayerMercenary))
                                   && !AtWarWithPlayer(s) && PC.Tier >= 2,
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s);
                    if (PC.Kingdom == k && PlayerMercenary) ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(PC, false);
                    ChangeKingdomAction.ApplyByJoinToKingdom(PC, k);
                    return L.F("AIL_4c63a92c", $"{s.Name} te acepta como vasallo de {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "hire_as_mercenary", Usage = "hire_as_mercenary",
                Description = "As ruler, you take the other person's clan into your kingdom's service as paid mercenaries, effective now. Only if they offered their swords or accepted your offer.",
                IsAvailable = s => S.Get.AllowFealty && NpcIsRuler(s) && PC != null && PC.Kingdom == null && !AtWarWithPlayer(s) && PC.Tier >= 1,
                Execute = (s, arg, ctx) =>
                {
                    ChangeKingdomAction.ApplyByJoinFactionAsMercenary(PC, KingdomOf(s));
                    return L.F("AIL_f362d5cb", $"{s.Name} contrata a tu clan como mercenarios de {KingdomOf(s).Name}.");
                }
            },
            new ActionDef
            {
                Name = "dismiss_from_kingdom", Usage = "dismiss_from_kingdom",
                Description = "As ruler, you cast the other person's clan out of your kingdom (ending their vassalage or their mercenary contract), effective now. " +
                              "Only for treason, grave insult, open defiance, or if they themselves ask to be released.",
                IsAvailable = s => S.Get.AllowFealty && NpcIsRuler(s) && PC != null && PC.Kingdom == KingdomOf(s) && KingdomOf(s).RulingClan != PC,
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s);
                    if (PlayerMercenary) ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(PC);
                    else ChangeKingdomAction.ApplyByLeaveKingdom(PC);
                    return L.F("AIL_5a577403", $"{s.Name} expulsa a tu clan de {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "leave_kingdom", Usage = "leave_kingdom",
                Description = "You take your clan out of the kingdom you serve, breaking your oath (or ending your mercenary contract), effective now. You lose your place at court and may lose your lands. " +
                              "Only when you have truly broken with your ruler: betrayal, ruin, or a far better future elsewhere.",
                IsAvailable = s => S.Get.AllowFealty && s.IsHero && s.Hero.IsClanLeader && !s.Hero.IsPrisoner && KingdomOf(s) != null && KingdomOf(s).Leader != s.Hero
                                   && s.Hero.Clan != PC,
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s); var c = s.Hero.Clan;
                    if (c.IsUnderMercenaryService) ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(c);
                    else ChangeKingdomAction.ApplyByLeaveKingdom(c);
                    return L.F("AIL_3677d2a1", $"El clan {c.Name} abandona {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "abdicate", Usage = "abdicate",
                Description = "You give up your crown: the other person's clan becomes the ruling house of your kingdom, effective now. Almost unthinkable: only when you are broken, dying, " +
                              "utterly convinced they are the rightful heir, or forced beyond all hope.",
                IsAvailable = s => S.Get.AllowAbdication && NpcIsRuler(s) && PC != null && PC.Kingdom == KingdomOf(s) && !PlayerMercenary && s.Hero.Clan != PC,
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s);
                    ChangeRulingClanAction.Apply(k, PC);
                    return L.F("AIL_3dce3fe1", $"{s.Name} abdica: tu clan pasa a reinar en {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "accept_crown", Usage = "accept_crown",
                Description = "You accept the crown the other person is handing you: your clan becomes the ruling house of their kingdom. ONLY when they, as ruler, clearly offered you the throne in their last message.",
                IsAvailable = s => S.Get.AllowAbdication && PlayerIsRuler && s.IsHero && s.Hero.IsClanLeader && !s.Hero.IsPrisoner && s.Hero.Clan != PC
                                   && s.Hero.Clan.Kingdom == PlayerKingdom && !s.Hero.Clan.IsUnderMercenaryService,
                Execute = (s, arg, ctx) =>
                {
                    var k = PlayerKingdom;
                    ChangeRulingClanAction.Apply(k, s.Hero.Clan);
                    return L.F("AIL_479941aa", $"Cedes la corona: el clan {s.Hero.Clan.Name} pasa a reinar en {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "join_clan", Usage = "join_clan",
                Description = "You leave your own house and join the other person's clan as one of their own, for good. A rare step: only if you are not the head of your clan, " +
                              "have little to lose where you are, and are bound to them by deep loyalty, love or debt.",
                IsAvailable = s => S.Get.AllowClanChanges && s.IsHero && s.Hero.IsLord && !s.Hero.IsClanLeader && !s.Hero.IsPrisoner && s.Hero.Clan != null && s.Hero.Clan != PC
                                   && !s.Hero.Clan.IsMinorFaction && s.Hero.PartyBelongedTo == null && s.Hero.Spouse != s.Hero.Clan.Leader && !AtWarWithPlayer(s),
                Execute = (s, arg, ctx) =>
                {
                    var old = s.Hero.Clan;
                    s.Hero.Clan = PC;
                    return L.F("AIL_7765746a", $"{s.Name} deja el clan {old.Name} y se une al tuyo.");
                }
            },
            new ActionDef
            {
                Name = "leave_clan", Usage = "leave_clan",
                Description = "You leave the other person's service for good and go your own way. Only when you have truly had enough: unpaid, betrayed, shamed, or asked to do what you will not do.",
                IsAvailable = s => S.Get.AllowClanChanges && s.IsHero && s.Hero.IsPlayerCompanion && !s.Hero.IsPrisoner,
                Execute = (s, arg, ctx) =>
                {
                    RemoveCompanionAction.ApplyByFire(PC, s.Hero);
                    ctx.EndConversation = true;
                    return L.F("AIL_08b5bd20", $"{s.Name} abandona tu clan.");
                }
            },

            // ----------------------------------------------------------- combate
            new ActionDef
            {
                Name = "brawl", Usage = "brawl",
                Description = "You fight the other person with bare fists, right here, as soon as you stop talking. Use it if they challenged you to a fist fight and you accept, " +
                              "or if you have had enough of them and decide to settle it with your fists. Nobody dies in a brawl.",
                IsAvailable = s => (Combat.CanFightHere() || MapFight(s)) && !(s.IsHero && s.Hero.IsPrisoner),
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Combat.CanFightHere() ? Combat.QueueFight(s, "brawl") : StartMapFight(s, "brawl"); }
            },
            new ActionDef
            {
                Name = "duel", Usage = "duel",
                Description = "You fight the other person in a duel with weapons until one of you falls, as a matter of honour or sport, starting as soon as you stop talking. " +
                              "Only if they challenged you and you accept, or you challenge them and they have just agreed.",
                IsAvailable = s => (Combat.CanFightHere() || MapFight(s)) && !(s.IsHero && s.Hero.IsPrisoner),
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Combat.CanFightHere() ? Combat.QueueFight(s, "duel") : StartMapFight(s, "duel"); }
            },
            new ActionDef
            {
                Name = "duel_to_death", Usage = "duel_to_death",
                Description = "A duel with weapons where the loser dies. Only for mortal insults, blood feuds or matters you would truly die for, and only when both of you clearly agreed to those terms. " +
                              "Cautious characters and those with much to lose refuse.",
                IsAvailable = s => (Combat.CanFightHere() || MapFight(s)) && s.IsHero && !s.Hero.IsPrisoner,
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Combat.CanFightHere() ? Combat.QueueFight(s, "death") : StartMapFight(s, "death"); }
            },
            new ActionDef
            {
                Name = "die", Usage = "die:<how, in a few words>",
                Description = "Your life ends here and now, as the direct outcome of this scene. EXTREMELY rare and never for effect. Only when one of these is truly happening: " +
                              "you are their prisoner and they have just plainly carried out your execution; you take your own life; or you are already dying of your wounds and this is your last breath. " +
                              "Say your last words in the same reply. If in any doubt, do not use it.",
                IsAvailable = s => S.Get.AllowRoleplayDeath && s.IsHero && s.Hero.IsAlive && s.Hero != P && (HeldByPlayer(s) || s.Hero.IsWounded || S.Get.RoleplayDeathAnywhere)
                                   && !(s.Hero.Clan == Clan.PlayerClan && !S.Get.RoleplayDeathAnywhere),
                Execute = (s, arg, ctx) =>
                {
                    ctx.EndConversation = true;
                    Combat.QueueDeath(s.Hero, HeldByPlayer(s), arg);
                    return $"{s.Name} muere: {(string.IsNullOrWhiteSpace(arg) ? L.T("AIL_4b487b93", "aquí acaba su historia") : arg.Trim())}.";
                }
            },
            new ActionDef
            {
                Name = "attack", Usage = "attack",
                Description = "You end the parley and attack them with your party right now: there will be a battle. Use it when you have decided to fight (you are at war and see no reason to let them go).",
                IsAvailable = s => Combat.EnemyOnMap(s),
                Extra = s => $"Your party: {Combat.Men(Led(s))} fighting men. Theirs: {Combat.Men(MobileParty.MainParty)}.",
                Execute = (s, arg, ctx) => { Combat.KeepEncounter = true; ctx.EndConversation = true; return L.F("AIL_53ea433d", $"{s.Name} da la charla por terminada y se lanza al ataque."); }
            },
            new ActionDef
            {
                Name = "surrender", Usage = "surrender",
                Description = "You yield: you and all your men lay down arms and become their prisoners, without a battle. Only when the fight is clearly hopeless or they gave you terms you accept; " +
                              "proud or brave characters would rather fight.",
                IsAvailable = s => Combat.EnemyOnMap(s) && Combat.Men(Led(s)) * 2 <= Combat.Men(MobileParty.MainParty),
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Combat.QueueSurrender(s); }
            },
            new ActionDef
            {
                Name = "call_guards", Usage = "call_guards",
                Description = "You shout for the guards and denounce the other person as a criminal: it ends the conversation and marks them as wanted in your realm. " +
                              "Only when they threatened you, attacked someone, confessed a crime or will not leave you alone.",
                IsAvailable = s => S.Get.AllowCombat && s.Settlement != null && s.Settlement.MapFaction != null && s.Settlement.MapFaction != P.MapFaction
                                   && s.Settlement.OwnerClan != Clan.PlayerClan && !(s.IsHero && s.Hero.IsPrisoner) && Tense(s),
                Execute = (s, arg, ctx) =>
                {
                    ctx.RelationDelta -= 3; ctx.TrustDelta -= 10; ctx.EndConversation = true;
                    // Si hay un guardia que pueda oírlo, acude y decide él; si no, queda la denuncia
                    bool came = Justice.Summon($"Moments ago {(s.IsHero ? s.Name : "a local")} shouted for the guard, accusing the armed person in front of you of threatening them.", null, true);
                    if (came) return L.F("AIL_c8741a3a", $"{s.Name} llama a gritos a la guardia.");
                    ChangeCrimeRatingAction.Apply(s.Settlement.MapFaction, 15f, true);
                    return L.F("AIL_e9f35fb7", $"{s.Name} llama a la guardia: te has ganado mala fama ante {s.Settlement.MapFaction.Name}.");
                }
            },

            // ----------------------------------------------------------- gente corriente que pasa a ser alguien
            new ActionDef
            {
                Name = "join_their_company", Usage = "join_their_company",
                Description = "You leave your life here for good and go with the other person as one of their companions. Very rare: only if they clearly and seriously offered to take you into their company, " +
                              "you have a real reason to go (nothing holding you here, a debt, a dream, love, hunger) and you have come to trust them. A passing remark or a joke is not an offer.",
                IsAvailable = s => S.Get.AllowPromotions && !s.IsHero && s.Age >= 18 && Clan.PlayerClan != null && Clan.PlayerClan.Companions.Count < Clan.PlayerClan.CompanionLimit
                                   && s.Memory.Profile != null && !s.Memory.Profile.IsEmpty && s.Memory.Turns.Count >= S.Get.PromotionMinTurns,
                Execute = (s, arg, ctx) =>
                {
                    var agent = SceneAgent();
                    var hero = World.People.Promote(s, agent, true, out string perr) ?? Promote(s);
                    if (hero == null) return null;
                    ctx.EndConversation = true;
                    try { if (agent != null) Combat.FadeAfterTalk(agent); } catch { }
                    return L.F("AIL_f65463d8", $"{hero.Name} deja su vida aquí y se une a tu compañía.");
                }
            },

            // ----------------------------------------------------------- recados a otros NPC
            new ActionDef
            {
                Name = "send_word", Usage = "send_word:<name of a real person>|<what you tell them, one or two sentences in English>|<public, private or secret>",
                Description = "You pass on to another real person something about the one in front of you: what they said, did, asked of you, promised, threatened or confessed. " +
                              "Use it when your duty, loyalty or interest calls for it (warn your lord, tell your spouse, alert a friend, denounce them), or when they ask you to carry a message. " +
                              "It travels at the speed of a rider. public = you will tell it around; private = for that person only; secret = in confidence. They do not see this unless you tell them.",
                IsAvailable = s => S.Get.EnableInfoExchange && s.IsHero && !s.Hero.IsPrisoner && InfoExchange.Confidants(s.Hero, 1).Count > 0,
                Extra = s => InfoExchange.DescribeConfidants(s.Hero),
                Execute = (s, arg, ctx) =>
                {
                    var parts = (arg ?? "").Split('|');
                    if (parts.Length < 2) return null;
                    return InfoExchange.Send(s, parts[0].Trim(), parts[1].Trim(), parts.Length > 2 ? parts[2] : "private");
                }
            },

            // ----------------------------------------------------------- la guardia decide (solo el guardia que lleva el caso)
            new ActionDef
            {
                Name = "let_go", Usage = "let_go",
                Description = "You let the other person walk free: their account satisfies you, or the matter is not worth your time. It closes the case.",
                IsAvailable = s => Justice.Active(s),
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Justice.LetGo(s); }
            },
            new ActionDef
            {
                Name = "fine", Usage = "fine:<denars>",
                Description = "You make the other person pay a fine on the spot for the disturbance and then let them go. It closes the case.",
                IsAvailable = s => Justice.Active(s),
                Execute = (s, arg, ctx) =>
                {
                    var r = Justice.Fine(s, ParseAmount(arg));
                    if (r != null && r.StartsWith("Pagas")) ctx.EndConversation = true;
                    return r;
                }
            },
            new ActionDef
            {
                Name = "arrest", Usage = "arrest",
                Description = "You arrest the other person and take them to the cells of this place. For serious blood, lies, threats, refusal to pay, or a wanted criminal. It closes the case.",
                IsAvailable = s => Justice.Active(s),
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Justice.Arrest(s); }
            },

            // ----------------------------------------------------------- ejércitos
            new ActionDef
            {
                Name = "join_army", Usage = "join_army", Letter = true,
                Description = "You bring your party into the army the other person commands (or is gathering) and march under their orders.",
                IsAvailable = s =>
                {
                    if (!S.Get.AllowArmies || !CanOrder(s) || !(P.MapFaction is Kingdom) || s.Faction != P.MapFaction) return false;
                    var main = MobileParty.MainParty; var p = OrderSystem.PartyLedBy(s.Hero);
                    return main != null && p != null && (main.Army == null || main.Army.LeaderParty == main) && (p.Army == null || p.Army != main.Army);
                },
                Execute = (s, arg, ctx) => OrderSystem.JoinPlayerArmy(s.Hero)
            },
            new ActionDef
            {
                Name = "form_army", Usage = "form_army:<target town, castle or village>", Letter = true,
                Description = "You call the lords of your realm and gather your own army, which you command. Name an enemy fortress to besiege, an enemy village to raid, " +
                              "a friendly place to defend, or leave it empty to patrol. A major undertaking: only for a real military purpose.",
                IsAvailable = s => S.Get.AllowArmies && CanOrder(s) && s.Hero.IsLord && s.Faction is Kingdom && s.Hero.Clan != null && !s.Hero.Clan.IsUnderMercenaryService
                                   && OrderSystem.PartyLedBy(s.Hero)?.Army == null,
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out _);
                    return OrderSystem.FormOwnArmy(s.Hero, OrderSystem.FindSettlement(name));
                }
            },
            new ActionDef
            {
                Name = "leave_army", Usage = "leave_army", Letter = true,
                Description = "You pull your party out of the army you are in (if you command it, you disband it).",
                IsAvailable = s => S.Get.AllowArmies && S.Get.EnableMapOrders && s.IsHero && !s.Hero.IsPrisoner && OrderSystem.PartyLedBy(s.Hero)?.Army != null,
                Execute = (s, arg, ctx) => OrderSystem.LeaveArmy(s.Hero)
            },

            // ----------------------------------------------------------- conversación
            new ActionDef
            {
                Name = "suspect_lie", Usage = "suspect_lie",
                Description = "You believe the other person just lied to you. Use only when what they said clearly contradicts what you know or is absurd. " +
                              "Gullible or friendly characters use it rarely.",
                IsAvailable = s => true,
                Execute = (s, arg, ctx) => { s.Memory.LiesSuspected++; ctx.RelationDelta -= 1; ctx.TrustDelta -= S.Get.TrustLostPerLie; return null; }
            },
            new ActionDef
            {
                Name = "leave", Usage = "leave",
                Description = "You end the conversation and walk away now.",
                IsAvailable = s => true,
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return null; }
            },
        };

        /// <summary>
        /// ¿Podéis casaros? Las reglas del juego valen entre nobles; aquí se abre también a compañeros, errantes y notables,
        /// con las mismas condiciones de fondo: adultos, vivos, sin parentesco cercano, y solteros (salvo el ajuste de casados).
        /// </summary>
        private static bool CanWed(Hero h)
        {
            try
            {
                if (SafeSuitable(h)) return true;
                var cfg = S.Get;
                if (!cfg.MarryCommoners && !(h.IsLord)) return false;
                if (h == null || !h.IsAlive || h.IsChild || h.Age < 18 || P.Age < 18 || h == P || Bonds.IsCloseFamily(h)) return false;
                if (h.IsFemale == P.IsFemale && !cfg.RomanceSameSex) return false;
                if ((h.Spouse != null && h.Spouse != P || P.Spouse != null && P.Spouse != h) && !cfg.RomanceWithMarried) return false;
                if (h.Clan != null && h.Clan.Leader == h && h.Clan != Clan.PlayerClan) return false;      // el jefe de otra casa no la deja por una boda
                if (h.MapFaction is Kingdom k && k.Leader == h) return false;
                return h.IsLord || h.IsWanderer || h.IsNotable || h.IsPlayerCompanion;
            }
            catch { return false; }
        }

        private static bool SafeSuitable(Hero h)
        {
            try { return Campaign.Current.Models.MarriageModel.IsCoupleSuitableForMarriage(P, h); }
            catch { return false; }
        }

        /// <summary>
        /// Un vecino cualquiera pasa a ser un personaje de verdad (héroe) y compañero tuyo, conservando su nombre,
        /// su carácter y todo lo que habéis hablado.
        /// </summary>
        private static Hero Promote(Speaker s)
        {
            try
            {
                var culture = s.Culture ?? Hero.MainHero.Culture;
                CharacterObject template = null;
                if (template == null)
                    template = CharacterObject.All.FirstOrDefault(t => t.IsTemplate && t.Occupation == Occupation.Wanderer && t.IsFemale == s.IsFemale && t.Culture == culture)
                               ?? CharacterObject.All.FirstOrDefault(t => t.IsTemplate && t.Occupation == Occupation.Wanderer && t.IsFemale == s.IsFemale);
                if (template == null) { Log.Warn("No hay plantilla de compañero para convertir a " + s.Name); return null; }

                var born = s.ResidentSettlement ?? Settlement.CurrentSettlement;
                var hero = HeroCreator.CreateSpecialHero(template, born, null, null, Math.Max(18, (int)s.Age));
                if (hero == null) return null;
                string name = s.Name;
                if (!string.IsNullOrWhiteSpace(name) && name.Length <= 40)
                {
                    var t = new TaleWorlds.Localization.TextObject("{=!}" + name.Replace("{", "").Replace("}", ""));
                    hero.SetName(t, t);
                }
                hero.ChangeState(Hero.CharacterStates.Active);
                AddCompanionAction.Apply(Clan.PlayerClan, hero);
                AddHeroToPartyAction.Apply(hero, MobileParty.MainParty);

                // Su memoria de vecino pasa a su ficha de héroe
                var old = s.Memory;
                var mem = Memory.MemoryStore.Get(hero.StringId, hero.Name.ToString());
                mem.Profile = old.Profile; mem.Turns = old.Turns.ToList(); mem.Notes = old.Notes.ToList(); mem.Summary = old.Summary;
                mem.KnownPlayerName = old.KnownPlayerName; mem.Trust = old.Trust; mem.TrustSet = old.TrustSet; mem.Romance = old.Romance;
                mem.Mood = old.Mood; mem.MoodReason = old.MoodReason; mem.MoodDay = old.MoodDay; mem.ConversationCount = old.ConversationCount + 1;
                mem.LastConversationDay = CampaignTime.Now.ToDays; mem.Archetype = old.Archetype; mem.ArchetypeText = old.ArchetypeText;
                mem.Morality = old.Morality; mem.Profanity = old.Profanity; mem.Piety = old.Piety;
                if (mem.Profile != null)
                    mem.Profile.Backstory = ((mem.Profile.Backstory ?? "") + $" I left {born?.Name} to follow {Hero.MainHero.Name}.").Trim();
                Log.Info($"{name} deja de ser un vecino de {born?.Name} y pasa a ser héroe ({hero.StringId}) y compañero");
                return hero;
            }
            catch (Exception ex) { Log.Error("Convertir a un vecino en compañero", ex); return null; }
        }

        /// <summary>Registra una plantilla editable por cada acción (prompts\actions\).</summary>
        public static void RegisterTemplates()
        {
            foreach (var a in All) Prompt.Templates.GetOrAdd("actions/" + a.Name + ".txt", a.Description);
        }

        // --- objetos
        private static string NormName(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

        private static ItemObject FindInRoster(ItemRoster roster, string name, out int have)
        {
            have = 0;
            string n = NormName(name);
            if (n.Length < 2 || roster == null) return null;
            ItemObject best = null; int bestScore = 0;
            foreach (var e in roster)
            {
                var item = e.EquipmentElement.Item;
                if (item == null || e.Amount <= 0) continue;
                string a = NormName(item.Name.ToString()), b = NormName(item.StringId);
                int score = a == n || b == n ? 3 : a.Contains(n) || n.Contains(a) ? 2 : b.Contains(n) ? 1 : 0;
                if (score > bestScore) { best = item; bestScore = score; }
            }
            if (best != null) have = roster.GetItemNumber(best);
            return best;
        }

        /// <summary>Bienes corrientes que cualquiera puede tener a mano aunque no lleve un grupo (comida, mercancía).</summary>
        private static ItemObject FindCommonGood(string name)
        {
            string n = NormName(name);
            if (n.Length < 3) return null;
            try
            {
                return TaleWorlds.ObjectSystem.MBObjectManager.Instance.GetObjectTypeList<ItemObject>()
                    .Where(i => i != null && (i.IsFood || i.IsTradeGood))
                    .OrderBy(i => i.Value)
                    .FirstOrDefault(i => { string a = NormName(i.Name.ToString()); return a == n || a.Contains(n) || n.Contains(a) || NormName(i.StringId) == n; });
            }
            catch { return null; }
        }

        private static string ListItems(ItemRoster roster, int max)
        {
            try
            {
                return string.Join(", ", roster.Where(e => e.EquipmentElement.Item != null && e.Amount > 0)
                    .OrderByDescending(e => e.EquipmentElement.Item.Value * e.Amount).Take(max)
                    .Select(e => $"{e.EquipmentElement.Item.Name} x{e.Amount}"));
            }
            catch { return ""; }
        }

        // --- reino y clan
        private static Clan PC => Clan.PlayerClan;
        private static Kingdom KingdomOf(Speaker s) => s.IsHero ? s.Hero.MapFaction as Kingdom : null;
        private static bool NpcIsRuler(Speaker s) => s.IsHero && KingdomOf(s) != null && KingdomOf(s).Leader == s.Hero && !s.Hero.IsPrisoner;
        private static bool PlayerMercenary => PC != null && PC.IsUnderMercenaryService;
        private static List<Settlement> FiefsOf(Clan c) => c == null ? new List<Settlement>() : c.Settlements.Where(x => x.IsTown || x.IsCastle).ToList();

        /// <summary>Texto con las acciones disponibles ahora mismo (para el prompt).</summary>
        public static string DescribeAvailable(Speaker s, bool viaLetter = false)
        {
            if (!S.Get.EnableActions) return "";
            var sb = new StringBuilder();
            foreach (var a in All)
            {
                if (viaLetter && !a.Letter) continue;
                bool ok;
                try { ok = a.IsAvailable(s); } catch { ok = false; }
                if (!ok) continue;
                string extra = "";
                try { if (a.Extra != null) extra = " " + a.Extra(s); } catch { }
                // La descripción sale de la plantilla editable prompts\actions\<acción>.txt
                sb.AppendLine($"- {a.Usage}: {Prompt.Templates.GetOrAdd("actions/" + a.Name + ".txt", a.Description).Text}{extra}");
            }
            return sb.ToString().TrimEnd();
        }

        /// <summary>Ejecuta las acciones pedidas (en el hilo principal). Devuelve los mensajes a enseñar.</summary>
        public static List<string> Execute(Speaker s, IEnumerable<string> commands, ActionContext ctx)
        {
            var messages = new List<string>();
            if (!S.Get.EnableActions || commands == null) return messages;

            // Orden lógico: primero el dinero, luego lo demás (ej: take_gold antes de join_party)
            var ordered = commands.Where(c => !string.IsNullOrWhiteSpace(c)).Take(5)
                .OrderBy(c => c.Trim().StartsWith("take_gold") || c.Trim().StartsWith("give_gold") ? 0 : 1).ToList();

            var done = new HashSet<string>();
            foreach (var raw in ordered)
            {
                var cmd = raw.Trim();
                int colon = cmd.IndexOf(':');
                var name = (colon >= 0 ? cmd.Substring(0, colon) : cmd).Trim().ToLowerInvariant();
                var arg = colon >= 0 ? cmd.Substring(colon + 1).Trim() : "";
                if (!done.Add(name)) continue;

                var def = All.FirstOrDefault(a => a.Name == name);
                if (def == null) { Log.Warn($"Acción desconocida ignorada: {cmd}"); continue; }
                if (ctx.ViaLetter && !def.Letter) { Log.Warn($"Acción no válida por carta, ignorada: {cmd}"); continue; }
                try
                {
                    if (!def.IsAvailable(s)) { Log.Warn($"Acción no disponible ahora, ignorada: {cmd}"); continue; }
                    var msg = def.Execute(s, arg, ctx);
                    Log.Info($"Acción {cmd} de {s.Name}" + (msg != null ? ": " + msg : ""));
                    if (msg != null) messages.Add(msg);
                    else if (def.Usage.Contains(":")) Log.Warn($"La acción {cmd} no se pudo cumplir (objetivo no encontrado o no válido)");
                    RecordAsNews(s, name);
                }
                catch (Exception ex) { Log.Error($"Error ejecutando la acción {cmd}", ex); }
            }
            return messages;
        }

        /// <summary>Las acciones gordas se convierten en noticia (el juego no siempre las anuncia).</summary>
        private static void RecordAsNews(Speaker s, string action)
        {
            if (!s.IsHero) return;
            string text = null, es = null, title = null; int imp = 1;
            switch (action)
            {
                case "join_party": text = $"{s.Name} has joined the band of {P.Name} as a companion."; es = L.F("AIL_ed904e98", $"{s.Name} se ha unido a la compañía de {P.Name}."); title = L.F("AIL_92de31ad", $"{s.Name} se une a {P.Name}"); imp = 1; break;
                case "set_free": text = $"{P.Name} has released {s.Name} from captivity."; es = L.F("AIL_309ea6a0", $"{P.Name} ha liberado a {s.Name}."); title = L.F("AIL_6a695bcb", $"{s.Name}, liberado"); imp = 2; break;
                case "accept_marriage": text = $"{s.Name} and {P.Name} have married."; es = L.F("AIL_fe5df831", $"{s.Name} y {P.Name} se han casado."); title = L.F("AIL_162d77c2", $"Boda de {s.Name} y {P.Name}"); imp = 3; break;
            }
            if (text == null) return;
            NewsStore.Add(new NewsItem
            {
                Type = action, Importance = imp, Text = text, TextEs = es, TitleEs = title,
                Heroes = new List<string> { s.Hero.StringId, P.StringId },
                Kingdoms = new[] { s.Hero.MapFaction, P.MapFaction }.Where(f => f != null).Select(f => f.StringId).Distinct().ToList(),
                SettlementId = s.Settlement?.StringId,
            });
        }

        private static int ParseAmount(string arg)
        {
            var digits = new string((arg ?? "").Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var v) ? v : 0;
        }
    }
}
