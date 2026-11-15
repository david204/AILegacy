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
    /// Actions the AI can ask for in the "actions" array of its reply.
    /// Only the ones that make sense right now are offered, and all of them are validated before they run.
    /// </summary>
    internal static class NpcActions
    {
        public class ActionContext
        {
            public bool EndConversation;
            public int RelationDelta;
            public int TrustDelta;
            public int TensionDelta;
            /// <summary>The action came by letter, so the NPC isn't in front of the player.</summary>
            public bool ViaLetter;
            /// <summary>The leader of a hostile party decided to let the player pass.</summary>
            public bool LetPass;
            /// <summary>Every action asked for in this reply, by its real name, whether or not it could be carried out.</summary>
            public readonly List<string> Tried = new List<string>();
            /// <summary>The ones that really took effect.</summary>
            public readonly List<string> Executed = new List<string>();
        }

        private class ActionDef
        {
            public string Name;
            public string Usage;
            public string Description;
            public Func<Speaker, bool> IsAvailable;
            /// <summary>Can also be chosen when answering a letter (no need to be face to face).</summary>
            public bool Letter;
            /// <summary>Extra text that depends on the moment (for example, enemies nearby).</summary>
            public Func<Speaker, string> Extra;
            /// <summary>Returns a line for the game log, or null.</summary>
            public Func<Speaker, string, ActionContext, string> Execute;
            /// <summary>
            /// For the few actions a talk is often about (fealty, land, peace, marriage, joining): while it is not
            /// available, why not, in English and addressed to the character. null when it does not concern them.
            /// </summary>
            public Func<Speaker, string> WhyNot;
        }

        /// <summary>A real action name, or a name models use for one.</summary>
        internal static bool Knows(string name) => !string.IsNullOrEmpty(name) && (All.Any(a => a.Name == name) || Aliases.ContainsKey(name));

        private static Hero P => Hero.MainHero;

        private static readonly HashSet<string> DeathKinds = new HashSet<string> { "executed", "killed", "suicide", "poison", "wounds", "illness", "accident" };

        /// <summary>
        /// Where a death by roleplay can happen: held by the player, in the player's party, in a scene with the player,
        /// inside a settlement without leading a party, or alone on the road. Never for someone at the head of troops
        /// or held by another party.
        /// </summary>
        private static bool CanDieHere(Speaker s)
        {
            try
            {
                var h = s.Hero;
                if (S.Get.RoleplayDeathAnywhere || HeldByPlayer(s) || h.IsWounded) return true;
                if (h.IsPrisoner) return false;
                var party = h.PartyBelongedTo;
                if (party == MobileParty.MainParty) return true;
                if (party != null && party.LeaderHero == h && party.MemberRoster.TotalManCount >= 2) return false;
                if (TaleWorlds.MountAndBlade.Mission.Current != null && SceneAgent() != null) return true;
                if (party == null && h.CurrentSettlement != null) return true;
                return party != null && party.MemberRoster.TotalManCount <= 1;
            }
            catch { return false; }
        }

        /// <summary>The character you're talking to, if you're in a scene and the game AI controls them.</summary>
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

        // Repeat guard: the AI sometimes repeats in its next reply what it already did (take payment, hand over an item,
        // give troops...). The same action on the same thing, same amount and same person won't run again within a few
        // minutes. Covers gold, items, troops, prisoners, trade and documents.
        private static readonly Dictionary<string, (int amount, DateTime at)> LastDone = new Dictionary<string, (int, DateTime)>();

        public static void ResetSession() => LastDone.Clear();

        private static bool Repeated(Speaker s, string what, int amount)
        {
            if (LastDone.TryGetValue(s.Key + "|" + what, out var last) && last.amount == amount && (DateTime.UtcNow - last.at).TotalSeconds < S.Get.GoldRepeatGuardSeconds)
            {
                Log.Warn($"Acción repetida ignorada: {what} x{amount} con {s.Name} (ya se hizo hace {(int)(DateTime.UtcNow - last.at).TotalSeconds} s)");
                _repeated = true;
                return true;
            }
            return false;
        }

        /// <summary>Records what was done and leaves it in their history (in English, for the model) so they don't repeat it or ask again.</summary>
        private static void NoteDone(Speaker s, string what, int amount, string doneText)
        {
            LastDone[s.Key + "|" + what] = (amount, DateTime.UtcNow);
            if (string.IsNullOrEmpty(doneText)) return;
            try
            {
                s.Memory.Turns.Add(new Memory.ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays, Text = "[Done: " + doneText + "]" });
            }
            catch { }
        }

        private static bool RepeatedGold(Speaker s, string dir, int amount) => Repeated(s, "gold|" + dir, amount);

        // Outcome of the current action (all of this runs on the main thread, one action at a time)
        private static bool _repeated;      // skipped because it was just done
        private static string _why;         // reason in English why it did NOT happen (even if the player is shown a message)

        /// <summary>The action didn't happen. "shown" is what the player sees (or null) and "why" is what the character is told.</summary>
        private static string Nope(string shown, string why) { _why = why; return shown; }

        /// <summary>Puts what really happened with their action into the character's history, so their next line starts from there.</summary>
        private static void NoteOutcome(Speaker s, string text)
        {
            try { s.Memory.Turns.Add(new Memory.ChatTurn { Role = "player", Day = CampaignTime.Now.ToDays, Text = text }); }
            catch { }
        }

        // Names models bring from elsewhere for actions we do have
        private static readonly Dictionary<string, string> Aliases = new Dictionary<string, string>
        {
            { "transfer_gold", "give_gold" }, { "pay_gold", "give_gold" }, { "give_money", "give_gold" }, { "pay", "give_gold" },
            { "receive_gold", "take_gold" }, { "take_money", "take_gold" }, { "accept_gold", "take_gold" }, { "accept_payment", "take_gold" },
            { "follow", "follow_me" }, { "follow_player", "follow_me" }, { "go_to_settlement", "go_to" }, { "travel_to", "go_to" },
            { "wait", "wait_here" }, { "wait_near", "wait_here" }, { "siege", "besiege" }, { "return", "resume_duties" }, { "stop", "resume_duties" },
            { "raise_army", "form_army" }, { "create_army", "form_army" }, { "disband_army", "leave_army" },
            { "marry", "accept_marriage" }, { "intimate", "intimacy" }, { "fist_fight", "brawl" }, { "fight", "brawl" },
            { "release", "set_free" }, { "free", "set_free" }, { "recruit", "join_party" }, { "join", "join_party" },
            { "end_conversation", "leave" }, { "goodbye", "leave" }, { "let_them_pass", "let_pass" }, { "let_them_go", "let_pass" },
            { "give_items", "give_item" }, { "take_items", "take_item" }, { "transfer_troops", "give_troops" },
        };

        // Only one action per group per reply: fighting, surrendering, dying and letting go can't be combined
        private static readonly string[][] Exclusive =
        {
            new[] { "brawl", "duel", "duel_to_death", "attack", "surrender", "accept_surrender", "die", "let_pass", "call_guards", "arrest", "let_go" },
            new[] { "propose_marriage", "accept_marriage" },
            new[] { "set_free", "die" },
            new[] { "declare_war", "make_peace" },
        };

        // Action RAG. These depend on the topic (orders, economy, kingdom). With vectors available, only the ones related to
        // the conversation get a full description. The rest are just named on one line and still work if the character
        // uses them. The basic ones (money, relations, fighting, leaving) are always described in full.
        private static readonly HashSet<string> ByTopic = new HashSet<string>
        {
            "sell_goods", "buy_goods", "give_item", "take_item", "give_document", "take_document", "sell_workshop",
            "follow_me", "meet_at", "go_to", "wait_here", "patrol", "attack_party", "besiege", "raid", "escort", "lead_party",
            "join_army", "form_army", "leave_army", "give_troops", "take_troops", "give_prisoners", "take_prisoners",
            "swear_fealty", "serve_as_mercenary", "declare_war", "make_peace", "grant_fief", "accept_fief", "accept_vassal",
            "hire_as_mercenary", "dismiss_from_kingdom", "leave_kingdom", "abdicate", "accept_crown", "join_clan", "leave_clan",
            "send_word", "give_message", "be_remembered", "join_their_company",
        };

        private static readonly Dictionary<string, sbyte[]> ActionVecs = new Dictionary<string, sbyte[]>();
        private static bool _actionVecsBusy;
        private static string _actionVecsModel;

        /// <summary>Embeds each topic-dependent action once per session, and again if the vector model changes. Main thread.</summary>
        public static void EnsureActionVectors()
        {
            if (_actionVecsBusy || !S.Get.EnableActionRag || !Llm.Embeddings.Available) return;
            string working = Llm.Embeddings.Working;
            if (ActionVecs.Count > 0 && (working == null || Llm.Embeddings.SameModel(_actionVecsModel, working))) return;
            var list = All.Where(a => ByTopic.Contains(a.Name)).ToList();
            if (list.Count == 0) return;
            _actionVecsBusy = true;
            var texts = list.Select(a => a.Name.Replace('_', ' ') + ": " + a.Description).ToList();
            Llm.Embeddings.EmbedAsync(texts, "embed").ContinueWith(t =>
            {
                var r = t.IsFaulted ? null : t.Result;
                var v = r?.Vecs;
                MainThread.Post(() =>
                {
                    _actionVecsBusy = false;
                    if (v == null || v.Length != list.Count) return;
                    ActionVecs.Clear();
                    _actionVecsModel = r.Model;
                    for (int i = 0; i < list.Count; i++) ActionVecs[list[i].Name] = v[i];
                    Log.Info($"Acciones por tema: {list.Count} acciones con vector");
                });
            });
        }

        // Actions with no effect worth remembering: they leave no outcome note
        private static readonly HashSet<string> NoNote = new HashSet<string> { "leave", "suspect_lie" };

        private static void NoteGold(Speaker s, string dir, int amount) =>
            NoteDone(s, "gold|" + dir, amount, dir == "take" ? $"they have handed you {amount} denars. It is paid in full — do not take or ask for it again."
                                                             : $"you have handed them {amount} denars. It is given — do not give it again.");

        // --- trade with local people
        private static Settlement Market()
        {
            var st = Settlement.CurrentSettlement;
            return st != null && (st.IsTown || st.IsVillage) ? st : null;
        }

        /// <summary>Someone who buys and sells (merchants, artisans, tavern keepers, villagers; not guards or nobles).</summary>
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

        /// <summary>Price of that item here. selling = what they pay you (a bit less than the buying price).</summary>
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

        /// <summary>On the map, facing their party: a fight or duel happens in a terrain scene.</summary>
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

        /// <summary>
        /// Whoever enters the player's clan or party does so as a free man: a captivity still open is ended first,
        /// or the game is left with someone who is a companion and a prisoner at once. Our own actions already ask
        /// for a free man before they are offered; this is for whatever changed since.
        /// </summary>
        private static void FreeBeforeJoining(Hero h)
        {
            try
            {
                if (h == null || !h.IsPrisoner) return;
                Log.Info($"{h.Name} estaba preso al unirse al jugador: se le da la libertad primero");
                if (Captivity.HeldByPlayer(h)) EndCaptivityAction.ApplyByReleasedByChoice(h, P);
                else EndCaptivityAction.ApplyByEscape(h, null, false);
            }
            catch (Exception ex) { Log.Error("Liberar antes de unirse", ex); }
        }

        // --- deals forced out of a prisoner
        /// <summary>A prisoner in the player's hands (party or own dungeon) who can be made to sign things from his cell.</summary>
        private static bool Duress(Speaker s) => S.Get.AllowPrisonerDeals && s.IsHero && Captivity.HeldByPlayer(s.Hero);
        /// <summary>Free to act, or a captive of the player who can be forced to.</summary>
        private static bool FreeOrDuress(Speaker s) => s.IsHero && (!s.Hero.IsPrisoner || Duress(s));

        /// <summary>
        /// Land is only wrung out of an enemy: between two sides at peace a town signed over from a cell would be a
        /// cession nobody is bound by. The player needs no kingdom for it: the game lets a clan without one hold
        /// towns and castles (DefaultKingdomCreationModel asks an independent clan to own some before it can found
        /// a kingdom), and a clan can be at war with a kingdom on its own.
        /// </summary>
        private static bool ForcedCessionMakesSense(Hero h)
        {
            var his = h?.MapFaction; var theirs = P?.MapFaction;
            if (his == null || theirs == null) return false;
            return his == theirs || his.IsAtWarWith(theirs);
        }

        /// <summary>Towns and castles a captive can sign over: his clan's if he leads it, and the whole realm's if he rules it.</summary>
        private static List<Settlement> CedableUnderDuress(Speaker s)
        {
            var list = new List<Settlement>();
            try
            {
                if (!Duress(s) || s.Hero.Clan == null || s.Hero.Clan == PC) return list;
                var h = s.Hero;
                if (!ForcedCessionMakesSense(h)) return list;
                if (h.Clan.Leader == h) list.AddRange(FiefsOf(h.Clan));
                var k = World.Facts.Subject(h.Clan);
                if (k != null && k.Leader == h && k != P.MapFaction)
                    foreach (var f in k.Settlements) if ((f.IsTown || f.IsCastle) && !list.Contains(f)) list.Add(f);
                list.RemoveAll(x => x.IsUnderSiege || x.OwnerClan == null || x.OwnerClan == PC);
            }
            catch { }
            return list;
        }

        /// <summary>
        /// After a captive did what was forced out of him. When it left him on the player's side or at peace with
        /// it, the game frees him on its own (PrisonerReleaseCampaignBehavior releases prisoners who are no longer
        /// enemies when a clan changes kingdom); if he is somehow still held, he is let go here. The deal leaves its
        /// mark either way: he remembers it was forced, trusts and likes the player less.
        /// </summary>
        private static void AfterForcedDeal(Speaker s, ActionContext ctx, string whatEn, bool release)
        {
            try
            {
                var h = s.Hero;
                if (release)
                {
                    if (h.IsPrisoner && !AtWarWithPlayer(s))
                    {
                        try { EndCaptivityAction.ApplyByReleasedByChoice(h, P); }
                        catch (Exception ex) { Log.Error("Soltar al prisionero tras el trato", ex); }
                    }
                    if (!h.IsPrisoner)
                    {
                        Say(L.F("AIL_e7b1c785", $"{s.Name} deja de ser tu prisionero y queda libre."));
                        ctx.EndConversation = true;
                    }
                }
                ctx.TrustDelta -= 15;
                ctx.RelationDelta -= 3;
                string note = $"Forced under threat of death while their prisoner: {whatEn}. It was not given freely.";
                if (!s.Memory.Notes.Contains(note))
                {
                    s.Memory.Notes.Add(note);
                    while (s.Memory.Notes.Count > Math.Max(1, S.Get.MaxNotesPerHero)) s.Memory.Notes.RemoveAt(0);
                }
                NoteOutcome(s, $"[Done under duress: {whatEn}. It has really taken effect in the world" + (release && !h.IsPrisoner ? ", and you are no longer their prisoner" : "") +
                               ". You did it to save your life, not out of loyalty: the resentment stays with you.]");
                Say(L.F("AIL_e7b1c786", $"Arrancado bajo amenaza: {s.Name} no lo olvidará."));
            }
            catch (Exception ex) { Log.Error("Trato forzado con un prisionero", ex); }
        }

        // --- orders on the map
        // With an order running, only resume_duties is offered, so half-finished orders don't pile up
        private static bool CanOrder(Speaker s) => s.IsHero && OrderSystem.CanTakeOrders(s.Hero) && OrderSystem.Find(s.Hero) == null;
        private static bool AtWarWithPlayer(Speaker s) => s.Faction != null && P.MapFaction != null && s.Faction != P.MapFaction && s.Faction.IsAtWarWith(P.MapFaction);
        private static bool HasWars(Speaker s) { try { return s.Faction != null && s.Faction.FactionsAtWarWith.Any(f => f != null && f.IsKingdomFaction); } catch { return false; } }
        private static bool Hostile(Speaker s, Settlement st) => st?.MapFaction != null && s.Faction != null && st.MapFaction != s.Faction && st.MapFaction.IsAtWarWith(s.Faction);

        // --- transfers
        private static MobileParty Led(Speaker s) => s.IsHero ? OrderSystem.PartyLedBy(s.Hero) : null;
        private static bool CanTransfer(Speaker s) => S.Get.AllowTransfers && Led(s) != null && !s.Hero.IsPrisoner && MobileParty.MainParty != null && !AtWarWithPlayer(s);

        /// <summary>Matches a troop class (infantry, archers, cavalry, horse archers) or part of the troop name. Accepts English and Spanish.</summary>
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

        /// <summary>"20:archers" -> 20 and "archers".</summary>
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

        /// <summary>A named prisoner (a hero) inside a party.</summary>
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

        /// <summary>"Sargot:3" -> name "Sargot", 3 days. "5" -> no name, 5 days.</summary>
        private static void SplitArg(string arg, out string name, out double days)
        {
            name = (arg ?? "").Trim(); days = 0;
            int c = name.LastIndexOf(':');
            string tail = (c >= 0 ? name.Substring(c + 1) : name).Trim();
            // Only the first number at the end ("1.000", "3 days"...), never all the digits glued together
            if (tail.Length > 0 && tail.Length <= 10 && char.IsDigit(tail[0]) && tail.All(ch => char.IsLetterOrDigit(ch) || char.IsWhiteSpace(ch) || ch == '.' || ch == ','))
            {
                days = ParseAmount(tail);
                name = c >= 0 ? name.Substring(0, c).Trim() : "";
            }
        }

        private static readonly List<ActionDef> All = new List<ActionDef>
        {
            // ----------------------------------------------------------- money
            new ActionDef
            {
                Name = "give_gold", Usage = "give_gold:<amount>  or  give_gold:<amount>:<name of someone else who is here>",
                Description = "You hand the other person that many denars from your own purse, right now. Only when you have truly decided to give or pay them " +
                              "(a gift, a reward, a ransom you agreed to pay, payment for a deal you accepted). Never while still negotiating. " +
                              "With a name, the money goes to that other person instead, if they are here with you.",
                IsAvailable = s => (s.IsHero && s.Hero.Gold > 0) || (s.Band != null && s.Band.PartyTradeGold > 0),
                Extra = s => s.Band != null ? $"Your band's purse holds {s.Band.PartyTradeGold} denars." : "",
                Execute = (s, arg, ctx) =>
                {
                    int purse = s.IsHero ? s.Hero.Gold : s.Band.PartyTradeGold;
                    int amount = Math.Min(ParseAmount(arg), Math.Min(purse, S.Get.MaxGoldPerAction));
                    if (amount <= 0) return Nope(null, "no valid amount was given, or your purse is empty");
                    // For a third person who is here? ("give_gold:200:Name")
                    int sep = (arg ?? "").IndexOf(':');
                    string toName = sep >= 0 ? arg.Substring(sep + 1).Trim() : "";
                    if (toName.Length > 0 && s.IsHero && NormName(toName) != NormName(P.Name.ToString()) && NormName(toName) != NormName(P.FirstName?.ToString()))
                    {
                        var third = FindPresent(s, toName);
                        if (third == null) return Nope(null, $"{toName} is not here with you: you can only hand coin to someone present");
                        if (Repeated(s, "gold|give|" + third.StringId, amount)) return null;
                        GiveGoldAction.ApplyBetweenCharacters(s.Hero, third, amount);
                        NoteDone(s, "gold|give|" + third.StringId, amount, $"you have handed {amount} denars to {third.Name}. It is given — do not give it again.");
                        try { Bonds.TouchOther(Speaker.FromHero(third), s, 4, $"{s.Name} handed them {amount} denars in front of {P.Name}"); } catch { }
                        return L.F("AIL_c7a9d5e3", $"{s.Name} da {amount} denarios a {third.Name}.");
                    }
                    if (RepeatedGold(s, "give", amount)) return null;
                    if (s.IsHero) GiveGoldAction.ApplyBetweenCharacters(s.Hero, P, amount);
                    else { s.Band.PartyTradeGold -= amount; P.ChangeHeroGold(amount); }
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
                    if (amount <= 0) return Nope(null, "no valid amount was given");
                    if (RepeatedGold(s, "take", amount)) return null;
                    if (P.Gold < amount) return Nope(L.F("AIL_2d756f08", $"No tienes {amount} denarios para pagar a {s.Name}."), $"they do not have {amount} denars: they carry only {P.Gold}. Nothing was paid");
                    if (s.IsHero) GiveGoldAction.ApplyBetweenCharacters(P, s.Hero, amount);
                    else { P.ChangeHeroGold(-amount); if (s.Band != null) s.Band.PartyTradeGold += amount; }
                    NoteGold(s, "take", amount);
                    return L.F("AIL_800a1324", $"Pagas {amount} denarios a {s.Name}.");
                }
            },

            // ----------------------------------------------------------- companions
            new ActionDef
            {
                Name = "join_party", Usage = "join_party",
                Description = "You join the other person's band as a companion and travel with them from now on. Only if they asked you to join AND you accept " +
                              "(usually after being paid your price with take_gold in the same reply or earlier).",
                IsAvailable = s => s.IsHero && s.Hero.IsWanderer && s.Hero.CompanionOf == null && !s.Hero.IsPrisoner && s.Hero.PartyBelongedTo == null
                                   && Clan.PlayerClan != null && Clan.PlayerClan.Companions.Count < Clan.PlayerClan.CompanionLimit,
                WhyNot = s =>
                {
                    var h = s.Hero;
                    if (h.Clan == PC || h.CompanionOf != null || h.IsChild) return null;
                    if (h.IsLord) return h.IsPrisoner ? "you are a noble and their prisoner: you cannot ride in their band, as a companion or otherwise, while you are held"
                                                      : "you are a noble with a house of your own: you do not enter anyone's band as a hired companion";
                    if (!h.IsWanderer) return "you have your own life and place here and are not for hire as a companion";
                    if (h.IsPrisoner) return "you are a prisoner: you cannot take up arms in their band until you are set free";
                    if (h.PartyBelongedTo != null) return "you are already travelling with a party";
                    if (PC != null && PC.Companions.Count >= PC.CompanionLimit) return "their company has no room for another companion";
                    return null;
                },
                Execute = (s, arg, ctx) =>
                {
                    FreeBeforeJoining(s.Hero);
                    if (s.Hero.IsPrisoner) return Nope(null, "you are still held prisoner and could not join them");
                    AddCompanionAction.Apply(Clan.PlayerClan, s.Hero);
                    AddHeroToPartyAction.Apply(s.Hero, MobileParty.MainParty);
                    return L.F("AIL_85a42b1a", $"{s.Name} se une a tu grupo como compañero.");
                }
            },

            // ----------------------------------------------------------- prisoners
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

            // ----------------------------------------------------------- politics
            new ActionDef
            {
                Name = "swear_fealty", Usage = "swear_fealty",
                Description = "You and your whole clan swear fealty to the other person's kingdom and become their vassals, leaving your current realm if you have one. " +
                              "A huge decision: only after real persuasion, good relations and a clear offer from them, and only if it suits your interests.",
                // A captive of the player can be made to swear: the liking it normally takes is not asked of him
                IsAvailable = s => S.Get.AllowFealty && s.IsHero && s.Hero.IsLord && s.Hero.IsClanLeader && !s.Hero.IsKingdomLeader && FreeOrDuress(s)
                                   && PlayerIsRuler && s.Hero.Clan != null && !s.Hero.Clan.IsMinorFaction && s.Hero.Clan.Kingdom != PlayerKingdom && (Rel(s) >= 0 || Duress(s)),
                Extra = s => Duress(s) ? "As their prisoner you can swear it from your chains to save your life: the oath is real, it binds your whole clan, and it sets you free." : "",
                WhyNot = s =>
                {
                    var h = s.Hero;
                    if (!h.IsLord || h.Clan == null || h.Clan == PC || h.Clan.IsBanditFaction) return null;
                    if (!S.Get.AllowFealty) return "oaths of fealty cannot be sworn in a talk in this world";
                    if (!PlayerIsRuler) return "they rule no kingdom, so there is no crown of theirs for a clan to swear to";
                    if (h.Clan.Kingdom == PlayerKingdom) return h.Clan.IsUnderMercenaryService ? null : "your clan is already sworn to their kingdom";
                    if (h.IsKingdomLeader) return $"you are the ruler of {h.MapFaction?.Name}: a crown cannot swear itself to another crown. What a ruler can do is make peace, with whatever it costs written into the treaty";
                    if (!h.IsClanLeader) return $"you are not the head of clan {h.Clan.Name}: only {h.Clan.Leader?.Name} can swear for it, and your word alone binds nobody";
                    if (h.Clan.IsMinorFaction) return "yours is a free company, not a landed house: it can be hired, not sworn as a vassal";
                    if (h.IsPrisoner) return "you are a prisoner and cannot bind your house from a cell";
                    if (Rel(s) < 0) return $"you think too ill of them to swear to them (relation {Rel(s)}; it would have to be 0 or better)";
                    return null;
                },
                Execute = (s, arg, ctx) =>
                {
                    var clan = s.Hero.Clan;
                    bool forced = Duress(s);
                    AILegacy.World.Banished.Pardon(clan, PlayerKingdom);      // you took them in, so they're pardoned
                    if (clan.Kingdom != null) ChangeKingdomAction.ApplyByJoinToKingdomByDefection(clan, clan.Kingdom, PlayerKingdom);
                    else ChangeKingdomAction.ApplyByJoinToKingdom(clan, PlayerKingdom);
                    if (clan.Kingdom != PlayerKingdom) return Nope(null, "the oath did not take effect: your clan is not in their kingdom");
                    if (forced) AfterForcedDeal(s, ctx, $"you swore fealty with your whole clan to {PlayerKingdom.Name}, their kingdom, and are now their vassal", true);
                    return L.F("AIL_bdc30505", $"El clan {clan.Name} jura lealtad a {PlayerKingdom.Name}.");
                }
            },
            new ActionDef
            {
                Name = "serve_as_mercenary", Usage = "serve_as_mercenary",
                Description = "Your clan enters the other person's kingdom as paid mercenaries. Only after you agreed to the deal.",
                IsAvailable = s => S.Get.AllowFealty && s.IsHero && s.Hero.IsClanLeader && FreeOrDuress(s) && s.Hero.Clan != null && s.Hero.Clan.IsMinorFaction
                                   && s.Hero.Clan.Kingdom == null && !s.Hero.Clan.IsBanditFaction && PlayerIsRuler,
                Extra = s => Duress(s) ? "As their prisoner you can agree to it from your chains: the contract is real and it sets you free." : "",
                WhyNot = s =>
                {
                    var h = s.Hero;
                    if (h.Clan == null || !h.Clan.IsMinorFaction || h.Clan.IsBanditFaction || h.Clan == PC) return null;
                    if (!S.Get.AllowFealty) return "mercenary contracts cannot be made in a talk in this world";
                    if (!PlayerIsRuler) return "they rule no kingdom that could hire a company";
                    if (h.Clan.Kingdom == PlayerKingdom) return null;
                    if (h.Clan.Kingdom != null) return $"your company is under contract with {h.Clan.Kingdom.Name} and cannot take a second one";
                    if (!h.IsClanLeader) return $"you do not lead your company: only {h.Clan.Leader?.Name} can sign a contract for it";
                    if (h.IsPrisoner) return "you are a prisoner and cannot sign for your company from a cell";
                    return null;
                },
                Execute = (s, arg, ctx) =>
                {
                    bool forced = Duress(s);
                    AILegacy.World.Banished.Pardon(s.Hero.Clan, PlayerKingdom);
                    ChangeKingdomAction.ApplyByJoinFactionAsMercenary(s.Hero.Clan, PlayerKingdom);
                    if (s.Hero.Clan.Kingdom != PlayerKingdom) return Nope(null, "the contract did not take effect");
                    if (forced) AfterForcedDeal(s, ctx, $"you put your company in the service of {PlayerKingdom.Name}, their kingdom, as mercenaries", true);
                    return L.F("AIL_cdf9a5fc", $"El clan {s.Hero.Clan.Name} sirve ahora a {PlayerKingdom.Name} como mercenarios.");
                }
            },
            new ActionDef
            {
                Name = "declare_war", Usage = "declare_war",
                Description = "As ruler, you declare war on the other person's kingdom, effective immediately. Only for grave insults, threats or when it truly serves your realm. " +
                              "Also use it when THEY declare war on you to your face in this talk and you take up the challenge: a war that is only spoken of does not exist, and nobody in either realm will know of it unless this action is used.",
                IsAvailable = s => S.Get.AllowDiplomacy && s.IsHero && s.Hero.IsKingdomLeader && PlayerIsRuler && s.Hero.MapFaction != PlayerKingdom
                                   && s.Hero.MapFaction is Kingdom && !s.Hero.MapFaction.IsAtWarWith(PlayerKingdom) && WarPeaceSettled(s.Hero.MapFaction)
                                   && World.Diplomacy.AiWarBlocked(s.Hero.MapFaction as Kingdom, PlayerKingdom) == null,
                Execute = (s, arg, ctx) =>
                {
                    // An AI ruler declaring war on the kingdom the player rules: the setting and the house rules are
                    // checked again here, in case something changed since the action was offered.
                    var theirs = s.Hero.MapFaction as Kingdom;
                    string no = theirs == null ? "no kingdom" : World.Diplomacy.AiWarBlocked(theirs, PlayerKingdom);
                    if (no != null) return Nope(L.F("AIL_e7b1c765", $"{s.Hero.MapFaction?.Name} no puede declarar la guerra a {PlayerKingdom.Name} ahora."), "war on them is not allowed right now: " + no);
                    World.Diplomacy.DeclareWar(theirs, PlayerKingdom);
                    if (!theirs.IsAtWarWith(PlayerKingdom)) return Nope(L.F("AIL_e7b1c765", $"{s.Hero.MapFaction?.Name} no puede declarar la guerra a {PlayerKingdom.Name} ahora."), "the declaration of war did not take effect");
                    MarkWarPeace(theirs);
                    ctx.EndConversation = true;
                    return L.F("AIL_ae73db8a", $"¡{theirs.Name} declara la guerra a {PlayerKingdom.Name}!");
                }
            },
            new ActionDef
            {
                Name = "make_peace", Usage = "make_peace  or  make_peace:<terms>",
                Description = "As ruler, you agree to peace between your kingdom and theirs. Only when you have truly accepted the terms. " +
                              "When the two of you settled a price for the peace in this talk, in either direction, write it after the colon " +
                              "as terms separated by semicolons, and do NOT use take_gold or give_gold for it: " +
                              "they_pay=<denars they pay you at once>; you_pay=<denars you pay them at once>; they_pay_daily=<denars a day>x<days>; you_pay_daily=<denars a day>x<days>; " +
                              "they_cede=<a town or castle of their realm>; you_cede=<a town or castle of your realm>. " +
                              "Example: make_peace:they_pay=8000;they_cede=Varcheg — or, if you are the one buying the peace: make_peace:you_pay=5000. " +
                              "Only terms that both of you agreed to in so many words. The side that is losing the war is the one that pays; you never pay more than your treasury holds. " +
                              "Each term at most once, sums as plain whole numbers above zero, and a town or castle by its full name. " +
                              "With or without terms, they are shown the treaty and nothing is signed, paid or handed over until they confirm it.",
                IsAvailable = s => S.Get.AllowDiplomacy && s.IsHero && s.Hero.IsKingdomLeader && PlayerIsRuler && s.Hero.MapFaction.IsAtWarWith(PlayerKingdom) && WarPeaceSettled(s.Hero.MapFaction),
                Extra = s => $"Your treasury holds {Math.Max(0, s.Hero.Gold)} denars." +
                             (Duress(s) ? " You are their prisoner: a peace you agree to from your chains is as binding as any other, and what they wring out of you goes into its terms (you_pay, you_pay_daily, you_cede)." : ""),
                WhyNot = s =>
                {
                    var h = s.Hero;
                    if (!h.IsLord || !AtWarWithPlayer(s)) return null;
                    if (!S.Get.AllowDiplomacy) return "war and peace cannot be settled in a talk in this world";
                    if (!(h.MapFaction is Kingdom k)) return "your side is no kingdom: there is no treaty for you to sign";
                    if (!h.IsKingdomLeader) return $"you are not the ruler of {k.Name}: only {k.Leader?.Name} can make peace for it. You can say you will speak for it, never that it is agreed";
                    if (!PlayerIsRuler) return $"they do not rule their side: peace is made between rulers, and theirs is {(P.MapFaction as Kingdom)?.Leader?.Name}";
                    if (!WarPeaceSettled(h.MapFaction)) return "war or peace between your two realms was settled only days ago: it is too soon to change it again";
                    return null;
                },
                Execute = (s, arg, ctx) =>
                {
                    // With a price either way or with none, the peace is drawn up, shown to the player, and signed
                    // only on his yes: a talk never ends a war without a click. Whatever the character wrote after
                    // the colon, nothing here may throw into the dialogue.
                    try
                    {
                        var terms = PeaceTermsFrom(arg, out string badTerm);
                        if (badTerm != null) return Nope(null, badTerm);
                        return OfferTreaty(s, terms ?? new Newtonsoft.Json.Linq.JObject());
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Tratado de paz en una charla", ex);
                        return Nope(null, "the treaty could not be drawn up; nothing was signed, paid or handed over");
                    }
                }
            },

            // ----------------------------------------------------------- marriage and romance
            new ActionDef
            {
                Name = "propose_marriage", Usage = "propose_marriage",
                Description = "You ask the other person to marry you. Only if you truly want this union and the moment is right; they will answer yes or no themselves.",
                IsAvailable = s => S.Get.AllowMarriage && s.IsHero && !s.Hero.IsPrisoner && Rel(s) >= S.Get.MinRelationForMarriage
                                   && (!S.Get.EnableRomance || s.Memory.Romance >= S.Get.RomanceForMarriage)
                                   && CanWed(s.Hero),
                Execute = (s, arg, ctx) =>
                {
                    if (P.Spouse == s.Hero || s.Hero.Spouse == P) return null;
                    if (Repeated(s, "propose_marriage", 1)) return null;
                    LastDone[s.Key + "|propose_marriage"] = (1, DateTime.UtcNow);
                    NoteOutcome(s, "[Pending: you have asked them to marry you. They have not answered yet: the wedding happens only if they say yes.]");
                    Ask(L.F("AIL_b8f2e6a1", $"{s.Name} te pide matrimonio"),
                        L.F("AIL_b8f2e6a2", $"Si aceptas, la boda se celebra ahora y es para siempre: {s.Name} pasará a ser de tu casa."),
                        L.T("AIL_b8f2e6a3", "Aceptar"), L.T("AIL_b8f2e6a4", "Rechazar"),
                        () => FinishWedding(s),
                        () =>
                        {
                            NoteOutcome(s, "[Did not happen: they refused your marriage proposal. You are not married; it hurts your pride.]");
                            try { Bonds.ChangeRomance(s, -10); ChangeRelationAction.ApplyPlayerRelation(s.Hero, -2, false, true); } catch { }
                            Say(L.F("AIL_b8f2e6a5", $"Rechazas la propuesta de {s.Name}."));
                            Memory.SaveFiles.Flush();
                        });
                    return L.F("AIL_b8f2e6a6", $"{s.Name} te pide matrimonio.");
                }
            },
            new ActionDef
            {
                Name = "accept_marriage", Usage = "accept_marriage",
                Description = "You say yes to the other person's marriage proposal. Only if they explicitly proposed and you truly love or want this union. The wedding takes place if they confirm it.",
                IsAvailable = s => S.Get.AllowMarriage && s.IsHero && !s.Hero.IsPrisoner && Rel(s) >= S.Get.MinRelationForMarriage
                                   && (!S.Get.EnableRomance || s.Memory.Romance >= S.Get.RomanceForMarriage)
                                   && CanWed(s.Hero),
                // Covers propose_marriage too: both ask for the same things
                WhyNot = s =>
                {
                    var h = s.Hero;
                    if (h.IsChild || h.Age < 18 || P.Spouse == h || Bonds.IsCloseFamily(h)) return null;
                    if (h.IsFemale == P.IsFemale && !S.Get.RomanceSameSex) return null;
                    if (!S.Get.AllowMarriage) return "no marriage can be made in a talk in this world";
                    if (h.IsPrisoner) return "you are a prisoner: no wedding can be held from a cell, and a promise of one is only a promise";
                    if (!CanWed(h))
                        return h.Clan != null && h.Clan.Leader == h && h.Clan != Clan.PlayerClan ? "you are the head of your own house and will not leave it to marry into theirs"
                             : h.Spouse != null && h.Spouse.IsAlive ? $"you are married to {h.Spouse.Name}"
                             : P.Spouse != null && P.Spouse.IsAlive ? "they are already married"
                             : "a marriage between the two of you is not possible";
                    if (Rel(s) < S.Get.MinRelationForMarriage) return $"you do not think well enough of them to marry them (relation {Rel(s)}; it would have to be {S.Get.MinRelationForMarriage} or better)";
                    if (S.Get.EnableRomance && s.Memory.Romance < S.Get.RomanceForMarriage) return "you do not love them enough to marry them yet";
                    return null;
                },
                Execute = (s, arg, ctx) =>
                {
                    var h = s.Hero;
                    // Already married: nothing to do (checked before touching any marriage)
                    if (P.Spouse == h || h.Spouse == P) return null;
                    if (Repeated(s, "accept_marriage", 1)) return null;
                    LastDone[s.Key + "|accept_marriage"] = (1, DateTime.UtcNow);
                    // Second step: the wedding needs the player's explicit yes (a stray line doesn't marry anyone)
                    NoteOutcome(s, "[Pending: you have said yes to marrying them. The wedding takes place only if they confirm it now.]");
                    Ask(L.F("AIL_b8f2e6a7", $"{s.Name} acepta casarse contigo"),
                        L.F("AIL_b8f2e6a2", $"Si aceptas, la boda se celebra ahora y es para siempre: {s.Name} pasará a ser de tu casa."),
                        L.T("AIL_b8f2e6a8", "Casarse"), L.T("AIL_b8f2e6a9", "Todavía no"),
                        () => FinishWedding(s),
                        () =>
                        {
                            NoteOutcome(s, "[Did not happen: they drew back at the last moment and did not go through with the wedding. You are not married.]");
                            Memory.SaveFiles.Flush();
                        });
                    return L.F("AIL_b8f2e6aa", $"{s.Name} acepta casarse contigo.");
                }
            },
            new ActionDef
            {
                // Internal, never offered to the AI. The wedding itself, once the player confirms.
                Name = "wed_now", Usage = "wed_now", Description = "(internal)",
                IsAvailable = s => false,
                Execute = (s, arg, ctx) =>
                {
                    var h = s.Hero;
                    if (P.Spouse == h || h.Spouse == P) return null;
                    Hero hadH = h.Spouse, hadP = P.Spouse;
                    try
                    {
                        FreeBeforeJoining(h);
                        // A non-noble (companion, wanderer, notable without a clan) joins your clan as a noble first
                        if (!SafeSuitable(h))
                        {
                            if (h.GovernorOf != null) ChangeGovernorAction.RemoveGovernorOf(h);
                            if (h.IsPlayerCompanion) h.CompanionOf = null;
                            if (h.Occupation != Occupation.Lord) { h.SetNewOccupation(Occupation.Lord); World.People.SyncOccupation(h); }
                            if (h.Clan == null || h.Clan != Clan.PlayerClan && h.Clan.Leader != h) h.Clan = Clan.PlayerClan;
                        }
                        // If either one was still married (the "romance with married" setting), that marriage ends
                        if (h.Spouse != null && h.Spouse != P) EndMarriage(h);
                        if (P.Spouse != null && P.Spouse != h) EndMarriage(P);
                        // The head of another house hands it over first. Otherwise their old clan is left with a
                        // leader who now belongs to yours.
                        StepDownAsClanLeader(h);
                    }
                    catch (Exception ex) { Log.Error("Preparar la boda", ex); }
                    Marry(P, h);
                    if (P.Spouse != h)
                    {
                        // Nobody is left single because of a wedding that did not happen
                        try
                        {
                            if (hadH != null && hadH.IsAlive && h.Spouse == null && hadH.Spouse == null) { h.Spouse = hadH; RemoveExSpouse(h, hadH); RemoveExSpouse(hadH, h); }
                            if (hadP != null && hadP.IsAlive && P.Spouse == null && hadP.Spouse == null) { P.Spouse = hadP; RemoveExSpouse(P, hadP); RemoveExSpouse(hadP, P); }
                        }
                        catch (Exception ex) { Log.Error("Deshacer la boda fallida", ex); }
                        Log.Warn($"La boda con {s.Name} no se ha podido celebrar");
                        return null;
                    }
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

            // ----------------------------------------------------------- real trade with local people
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
                    string key = "sell_goods|" + item.StringId; int asked = qty;
                    if (Repeated(s, key, asked)) return null;
                    qty = Math.Min(qty, have);
                    int cost = PriceOf(item, false) * qty;
                    if (P.Gold < cost) return Nope(L.F("AIL_3c14bb80", $"No te llega para {qty} de {item.Name} ({cost} denarios)."), $"they cannot afford it: {qty} x {item.Name} costs {cost} denars and they carry {P.Gold}. Nothing was sold");
                    GiveGoldAction.ApplyForCharacterToSettlement(P, st, cost, true);
                    st.ItemRoster.AddToCounts(item, -qty);
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, qty);
                    NoteDone(s, key, asked, $"you have sold them {qty} x {item.Name} for {cost} denars; they have paid and have the goods — do not sell it again.");
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
                    string key = "buy_goods|" + item.StringId; int asked = qty;
                    if (Repeated(s, key, asked)) return null;
                    qty = Math.Min(qty, have);
                    int pay = PriceOf(item, true) * qty;
                    int purse = st.Town != null ? st.Town.Gold : st.SettlementComponent != null ? st.SettlementComponent.Gold : pay;
                    if (purse < pay) { qty = Math.Max(0, purse / Math.Max(1, PriceOf(item, true))); pay = PriceOf(item, true) * qty; }
                    if (qty <= 0) return Nope(L.T("AIL_b720cde3", "El mercado no tiene dinero para comprártelo."), "there is no coin here to pay for it. Nothing was bought");
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, -qty);
                    st.ItemRoster.AddToCounts(item, qty);
                    GiveGoldAction.ApplyForSettlementToCharacter(st, P, pay, true);
                    NoteDone(s, key, asked, $"you have bought {qty} x {item.Name} from them for {pay} denars; it is paid and the goods are yours — do not buy it again.");
                    return L.F("AIL_fba371fc", $"Vendes {qty} de {item.Name} por {pay} denarios.");
                }
            },

            // ----------------------------------------------------------- from extra to character
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

            // ----------------------------------------------------------- in a scene: walk with you
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

            new ActionDef
            {
                Name = "go_into", Usage = "go_into:<place>",
                Description = "You take the other person, on foot, to another part of this settlement and go in with them: you walk to the door and they come through it with you. " +
                              "Use it when they ask you to show them the way or to go there together, in the SAME reply in which you agree. Only to one of the places listed.",
                IsAvailable = s => SceneAgent() != null && SceneTravel.CanLead(SceneAgent()),
                Extra = s => "Places you can take them to from here: " + SceneTravel.Places() + ".",
                Execute = (s, arg, ctx) =>
                {
                    var a = SceneAgent();
                    if (a == null) return null;
                    return SceneActions.Apply(a, "go_into:" + arg, new List<TaleWorlds.MountAndBlade.Agent>()) ? L.F("AIL_e7b1c64b", $"{s.Name} te espera en la puerta cuando termines de hablar.") : null;
                }
            },

            // ----------------------------------------------------------- orders on the map
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
                IsAvailable = s => s.IsHero && s.Hero.Clan == Clan.PlayerClan && s.Hero.PartyBelongedTo == MobileParty.MainParty && s.Hero != P && !s.Hero.IsWounded
                                   && Clan.PlayerClan.WarPartyComponents.Count < Clan.PlayerClan.WarPartyLimit,
                Execute = (s, arg, ctx) =>
                {
                    try
                    {
                        if (Clan.PlayerClan.WarPartyComponents.Count >= Clan.PlayerClan.WarPartyLimit) return Nope(null, "their clan already fields as many war parties as it can");
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
                    // plenty of time to get there; the wait only starts counting on arrival
                    if (!OrderSystem.Give(s.Hero, "meet", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays)) return null;
                    return L.F("AIL_6a69d3fd", $"{s.Name} irá a {st.Name} y te esperará allí.");
                }
            },
            new ActionDef
            {
                Name = "go_to", Usage = "go_to:<town, castle or village>  or  go_to:<place>:<days to stay there>  or either with :return at the end", Letter = true,
                Description = "You take your party to that place and then go back to your own business. Give a number of days if you agreed to stay there a while. " +
                              "Add \":return\" at the end if you agreed to come back to the other person afterwards and ride with them again.",
                IsAvailable = s => CanOrder(s),
                Execute = (s, arg, ctx) =>
                {
                    arg = (arg ?? "").Trim();
                    bool back = false;
                    foreach (var suffix in new[] { ":return", ":back", ":and return", ":then return" })
                        if (arg.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { back = true; arg = arg.Substring(0, arg.Length - suffix.Length); break; }
                    if (back && AtWarWithPlayer(s)) back = false;
                    SplitArg(arg, out var name, out var stay);
                    var st = OrderSystem.FindSettlement(name, x => !Hostile(s, x));
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "goto", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays, back ? "follow" : null, stay)) return null;
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
                Name = "patrol", Usage = "patrol:<town, castle or village>:<days>  (add :return to come back to them afterwards)", Letter = true,
                Description = "You patrol with your party around that place for that many days, protecting it.",
                IsAvailable = s => CanOrder(s),
                Execute = (s, arg, ctx) =>
                {
                    bool back = TakeReturn(ref arg) && !AtWarWithPlayer(s);
                    SplitArg(arg, out var name, out var days);
                    var st = OrderSystem.FindSettlement(name);
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "patrol", st.StringId, st.Name.ToString(), days, back ? "follow" : null)) return null;
                    return L.F("AIL_611c4786", $"{s.Name} patrullará los alrededores de {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "attack_party", Usage = "attack_party:<name of the enemy party>  (add :return to come back to them afterwards)",
                Description = "You ride out with your party to hunt down and attack that enemy party. Only against parties you could plausibly beat or must stop.",
                IsAvailable = s => CanOrder(s) && OrderSystem.HostilesNear(s.Hero, 1).Count > 0,
                Extra = s => "Enemy parties near you right now: " + string.Join("; ", OrderSystem.HostilesNear(s.Hero).Select(p => $"{p.Name} ({p.MemberRoster.TotalManCount} men)")) +
                             $". Your own party has {OrderSystem.PartyLedBy(s.Hero)?.MemberRoster.TotalManCount} men.",
                Execute = (s, arg, ctx) =>
                {
                    bool back = TakeReturn(ref arg) && !AtWarWithPlayer(s);
                    var target = OrderSystem.FindHostile(s.Hero, arg);
                    if (target == null) return null;
                    if (!OrderSystem.Give(s.Hero, "attack", target.StringId, target.Name.ToString(), S.Get.OrderDefaultDays, back ? "follow" : null)) return null;
                    return L.F("AIL_1fb2c5a1", $"{s.Name} sale a dar caza a {target.Name}.");
                }
            },
            new ActionDef
            {
                Name = "besiege", Usage = "besiege:<enemy town or castle>  (add :return to come back to them afterwards)", Letter = true,
                Description = "You march your party to lay siege to that town or castle of a realm you are at war with. A serious military commitment: only with enough men or as part of a real plan.",
                IsAvailable = s => CanOrder(s) && HasWars(s),
                Execute = (s, arg, ctx) =>
                {
                    bool back = TakeReturn(ref arg) && !AtWarWithPlayer(s);
                    SplitArg(arg, out var name, out _);
                    var st = OrderSystem.FindSettlement(name, x => x.IsFortification && Hostile(s, x));
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "besiege", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays, back ? "follow" : null)) return null;
                    return L.F("AIL_77e4bc34", $"{s.Name} marcha a asediar {st.Name}.");
                }
            },
            new ActionDef
            {
                Name = "raid", Usage = "raid:<enemy village>  (add :return to come back to them afterwards)", Letter = true,
                Description = "You take your party to raid and burn that village of a realm you are at war with. Merciful or honourable characters refuse to prey on peasants.",
                IsAvailable = s => CanOrder(s) && HasWars(s),
                Execute = (s, arg, ctx) =>
                {
                    bool back = TakeReturn(ref arg) && !AtWarWithPlayer(s);
                    SplitArg(arg, out var name, out _);
                    var st = OrderSystem.FindSettlement(name, x => x.IsVillage && Hostile(s, x) && !x.IsRaided);
                    if (st == null) return null;
                    if (!OrderSystem.Give(s.Hero, "raid", st.StringId, st.Name.ToString(), S.Get.OrderMaxDays, back ? "follow" : null)) return null;
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

            // ----------------------------------------------------------- troop and prisoner transfers
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
                    string key = "give_troops|" + NormName(kind);
                    if (Repeated(s, key, want)) return null;
                    int n = Math.Min(want, Math.Min(S.Get.MaxTroopsPerTransfer, p.MemberRoster.TotalRegulars / 3));
                    if (n <= 0) return null;
                    int moved = MoveTroops(p.MemberRoster, MobileParty.MainParty.MemberRoster, n, false, kind);
                    if (moved <= 0) return null;
                    NoteDone(s, key, want, $"you have handed {moved} of your soldiers{(kind.Length > 0 ? " (" + kind + ")" : "")} over to them. They are theirs now — do not give them again.");
                    return L.F("AIL_24ba54ab", $"{s.Name} te cede {moved} soldados{(kind.Length > 0 ? " (" + kind + ")" : "")}.");
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
                    string key = "take_troops|" + NormName(kind);
                    if (Repeated(s, key, want)) return null;
                    int n = Math.Min(want, S.Get.MaxTroopsPerTransfer);
                    if (n <= 0) return null;
                    int moved = MoveTroops(MobileParty.MainParty.MemberRoster, Led(s).MemberRoster, n, false, kind);
                    if (moved <= 0) return null;
                    NoteDone(s, key, want, $"they have handed {moved} soldiers{(kind.Length > 0 ? " (" + kind + ")" : "")} over to you. They are in your party now — do not take them again.");
                    return L.F("AIL_bca1e090", $"Cedes {moved} soldados{(kind.Length > 0 ? " (" + kind + ")" : "")} a {s.Name}.");
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
                    if (noble != null)
                    {
                        if (Repeated(s, "give_prisoners|" + noble.StringId, 1)) return null;
                        if (SameSide(noble.HeroObject, P))
                        {
                            EndCaptivityAction.ApplyByReleasedByChoice(noble.HeroObject, s.Hero);
                            NoteDone(s, "give_prisoners|" + noble.StringId, 1, $"you have set your prisoner {noble.Name} free and handed them back to their own side. {noble.Name} is free now.");
                            return L.F("AIL_b1e0a6d2", $"{s.Name} pone en libertad a {noble.Name}, que es de los tuyos.");
                        }
                        TransferPrisonerAction.Apply(noble, p.Party, MobileParty.MainParty.Party);
                        NoteDone(s, "give_prisoners|" + noble.StringId, 1, $"you have handed your prisoner {noble.Name} over to them.");
                        return L.F("AIL_f378719a", $"{s.Name} te entrega a su prisionero {noble.Name}.");
                    }
                    int want = ParseAmount(arg);
                    if (Repeated(s, "give_prisoners|common", want)) return null;
                    int n = Math.Min(want, 500);
                    if (n <= 0) return null;
                    int moved = MoveTroops(p.PrisonRoster, MobileParty.MainParty.PrisonRoster, n, true);
                    if (moved <= 0) return null;
                    NoteDone(s, "give_prisoners|common", want, $"you have handed {moved} prisoners over to them — do not give them again.");
                    return L.F("AIL_dec042f4", $"{s.Name} te entrega {moved} prisioneros.");
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
                        if (Repeated(s, "take_prisoners|" + noble.StringId, 1)) return null;
                        if (SameSide(noble.HeroObject, s.Hero))
                        {
                            EndCaptivityAction.ApplyByReleasedByChoice(noble.HeroObject, P);
                            NoteDone(s, "take_prisoners|" + noble.StringId, 1, $"they have handed {noble.Name}, one of your own side, back to you. {noble.Name} is free now, not a prisoner.");
                            return L.F("AIL_4c9d7e31", $"Entregas a {noble.Name} a los suyos: queda en libertad.");
                        }
                        TransferPrisonerAction.Apply(noble, main.Party, Led(s).Party);
                        NoteDone(s, "take_prisoners|" + noble.StringId, 1, $"they have handed their prisoner {noble.Name} over to you.");
                        return L.F("AIL_7ed3beae", $"Entregas a {s.Name} a tu prisionero {noble.Name}.");
                    }
                    int want = ParseAmount(arg);
                    if (Repeated(s, "take_prisoners|common", want)) return null;
                    int n = Math.Min(want, 500);
                    if (n <= 0) return null;
                    int moved = MoveTroops(main.PrisonRoster, Led(s).PrisonRoster, n, true);
                    if (moved <= 0) return null;
                    NoteDone(s, "take_prisoners|common", want, $"they have handed {moved} prisoners over to you — do not take them again.");
                    return L.F("AIL_2d97f1db", $"Entregas {moved} prisioneros a {s.Name}.");
                }
            },

            // ----------------------------------------------------------- items, documents and workshops
            new ActionDef
            {
                Name = "give_item", Usage = "give_item:<item name>:<amount>",
                Description = "You hand the other person that many of an item you really have (goods, food, a weapon, a horse), for good. Only when you truly decided to: a gift, a payment in kind, " +
                              "a reward, or a sale they already paid for with take_gold. Never give more than you would plausibly part with.",
                IsAvailable = s => S.Get.AllowItems && MobileParty.MainParty != null && !(s.IsHero && s.Hero.IsPrisoner) && HasAny(GiveSource(s, out _)),
                Extra = s => { var src = GiveSource(s, out var where); return $"What you can hand over ({where}): " + ListItems(src, 12) + ". Nothing else: you cannot give what is not listed."; },
                Execute = (s, arg, ctx) =>
                {
                    SplitArg(arg, out var name, out var amount);
                    int asked = Math.Max(1, (int)amount), n = asked;
                    // Always from a real inventory (their party or the local stock). If they don't have it, there's nothing to give
                    var src = GiveSource(s, out _);
                    if (src == null) return null;
                    var item = FindInRoster(src, name, out int have);
                    if (item == null || have <= 0) return null;
                    string key = "give_item|" + item.StringId;
                    if (Repeated(s, key, asked)) return null;
                    n = Math.Min(n, have);
                    if (item.Value > 0) n = Math.Min(n, Math.Max(1, S.Get.MaxItemValuePerAction / item.Value));
                    if (n <= 0) return null;
                    src.AddToCounts(item, -n);
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, n);
                    NoteDone(s, key, asked, $"you have handed them {n} x {item.Name}. It is given — do not give it again.");
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
                    if (item == null) return Nope(L.F("AIL_e00ce40a", $"No llevas «{name}» para dárselo a {s.Name}."), $"they do not carry «{name}». Nothing was handed over");
                    int asked = Math.Max(1, (int)amount);
                    string key = "take_item|" + item.StringId;
                    if (Repeated(s, key, asked)) return null;
                    int n = Math.Min(asked, have);
                    MobileParty.MainParty.ItemRoster.AddToCounts(item, -n);
                    Led(s)?.ItemRoster.AddToCounts(item, n);
                    QuestObjectives.OnItemTaken(s, item, n);
                    NoteDone(s, key, asked, $"they have handed you {n} x {item.Name}. You have it now — do not take or ask for it again.");
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
                    string key = "give_document|" + Belongings.Norm(name);
                    if (Repeated(s, key, 1)) return null;
                    Belongings.Items.RemoveAll(i => Belongings.Norm(i.Name) == Belongings.Norm(name));
                    Belongings.Items.Add(new RpItem { Name = name, Description = desc, FromId = s.Hero.StringId, FromName = s.Name, Day = CampaignTime.Now.ToDays });
                    NoteDone(s, key, 1, $"you have handed them \"{name}\". They carry it now — do not give it again.");
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
                    string key = "take_document|" + Belongings.Norm(it.Name);
                    if (Repeated(s, key, 1)) return null;
                    Belongings.Items.Remove(it);
                    QuestObjectives.OnDocumentTaken(s, it);
                    NoteDone(s, key, 1, $"they have handed you \"{it.Name}\". You keep it now.");
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
                    string wanted = NormName(name);
                    var ws = wanted.Length < 2 ? null : mine.FirstOrDefault(w => NormName(w.Name.ToString()).Contains(wanted) || wanted.Contains(NormName(w.Name.ToString())));
                    if (ws == null && mine.Count == 1) ws = mine[0];          // only one here, so no doubt which
                    if (ws == null) return Nope(null, "you did not name which of your workshops; write its exact name");
                    if (price <= 0) return Nope(null, "no price was given; write sell_workshop:<name>:<price>");
                    int worth = Campaign.Current.Models.WorkshopModel.GetCostForPlayer(ws);
                    if (price < worth / 2) return Nope(null, $"{price} denars is far too little: the workshop is worth about {worth}, and you would never sell your livelihood for under {worth / 2}");
                    if (P.Gold < price) return Nope(L.F("AIL_20cbe084", $"No tienes {price} denarios para comprar el taller de {s.Name}."), $"they do not have {price} denars. Nothing was sold");
                    string wname = ws.Name.ToString();
                    GiveGoldAction.ApplyBetweenCharacters(P, s.Hero, price);
                    ChangeOwnerOfWorkshopAction.ApplyByWar(ws, P, ws.WorkshopType);
                    return L.F("AIL_c70d2ac3", $"Compras a {s.Name} el taller «{wname}» por {price} denarios.");
                }
            },

            // ----------------------------------------------------------- kingdom and clan
            new ActionDef
            {
                Name = "grant_fief", Usage = "grant_fief:<town or castle of yours>",
                Description = "You give one of your clan's towns or castles to the other person's clan, for good. A lord's lands are his power: only as a ruler rewarding great service, " +
                              "to settle a heavy debt of honour, or under real duress. Never your last fief unless you are truly beaten.",
                // Freely: a clan head to a lord of his own realm. Forced: a captive of the player signs over a fief of his
                // clan, or of his whole realm if he rules it, whatever realm he belongs to.
                IsAvailable = s => S.Get.AllowFiefs && s.IsHero && s.Hero.Clan != PC
                                   && ((s.Hero.IsClanLeader && !s.Hero.IsPrisoner && KingdomOf(s) != null && KingdomOf(s) == P.MapFaction && !PlayerMercenary && FiefsOf(s.Hero.Clan).Count > 0)
                                       || CedableUnderDuress(s).Count > 0),
                Extra = s =>
                {
                    if (!Duress(s)) return "Your clan holds: " + string.Join(", ", FiefsOf(s.Hero.Clan).Select(x => x.Name.ToString())) + ".";
                    var can = CedableUnderDuress(s);
                    return "As their prisoner you can sign one over from your chains to buy your life or your freedom; it changes hands at once. You can sign over: " +
                           string.Join(", ", can.Take(14).Select(x => x.Name.ToString())) + (can.Count > 14 ? $" and {can.Count - 14} more" : "") + ".";
                },
                WhyNot = s =>
                {
                    var h = s.Hero;
                    if (!h.IsLord || h.Clan == null || h.Clan == PC) return null;
                    if (!S.Get.AllowFiefs) return "land cannot change hands in a talk in this world";
                    bool rules = h.IsKingdomLeader;
                    if (h.IsPrisoner && !Duress(s)) return "you are a prisoner and can sign nothing over from a cell";
                    if (h.IsPrisoner && (h.IsClanLeader || rules) && !ForcedCessionMakesSense(h))
                        return "your side is at peace with theirs: land is only forced out of an enemy in war, and a town signed over from a cell in peacetime would bind nobody";
                    if (!h.IsClanLeader && !rules) return $"the lands of clan {h.Clan.Name} are {h.Clan.Leader?.Name}'s to give, not yours";
                    if (FiefsOf(h.Clan).Count == 0 && !rules) return "your clan holds no town or castle";
                    if (!h.IsPrisoner && (KingdomOf(s) == null || KingdomOf(s) != P.MapFaction))
                        return "you can only grant land to a lord of your own realm; between realms a town or castle changes hands in a peace treaty" + (rules ? " (make_peace with you_cede, when you are at war)" : ", which only your ruler can make");
                    if (!h.IsPrisoner && PlayerMercenary) return "they only serve your realm for pay: land is not granted to a hired company";
                    return null;
                },
                Execute = (s, arg, ctx) =>
                {
                    if (Duress(s))
                    {
                        var can = CedableUnderDuress(s);
                        var mine = OrderSystem.FindSettlement(arg, x => can.Contains(x));
                        if (mine == null) return Nope(null, "that is not a town or castle you can sign over (not yours or your realm's, or it is under siege)");
                        var from = mine.OwnerClan;
                        // Whose last holding it is: the realm's when he rules it, his house's otherwise. It still goes, and everyone is told
                        var realm = World.Facts.Subject(s.Hero.Clan);
                        bool rules = realm != null && realm.Leader == s.Hero;
                        bool last = (rules ? realm.Settlements.Count(x => x.IsTown || x.IsCastle) : FiefsOf(s.Hero.Clan).Count) <= 1;
                        string holder = rules ? realm.Name.ToString() : s.Hero.Clan.Name.ToString();
                        // Across realms it changes hands the way a ceded town does in a peace; inside one realm it is a gift
                        if (mine.MapFaction != P.MapFaction) ChangeOwnerOfSettlementAction.ApplyByDefault(P, mine);
                        else ChangeOwnerOfSettlementAction.ApplyByGift(mine, P);
                        if (mine.OwnerClan != PC) return Nope(null, $"{mine.Name} could not change hands");
                        bool others = from != null && from != s.Hero.Clan && from.Leader != null && from.Leader != s.Hero && from.Leader != P;
                        if (others)
                        {
                            // The house that held it was robbed by its own ruler, for the benefit of his captor
                            try { ChangeRelationAction.ApplyRelationChangeBetweenHeroes(s.Hero, from.Leader, -15, false); } catch { }
                            try { ChangeRelationAction.ApplyRelationChangeBetweenHeroes(P, from.Leader, -10, true); } catch { }
                            try
                            {
                                NewsStore.Add(new NewsItem
                                {
                                    Type = "fief", Importance = 3, PlayerInvolved = true,
                                    Text = $"{s.Name}, held prisoner by {P.Name}, has signed {mine.Name} over to their captor. It belonged to clan {from.Name}, who were not asked and resent both of them for it.",
                                    TextEs = L.F("AIL_e7b1c79d", $"{s.Name}, prisionero de {P.Name}, le ha cedido {mine.Name}. Era del clan {from.Name}, al que nadie preguntó y que se lo guarda a los dos."),
                                    TitleEs = L.F("AIL_e7b1c79e", $"{s.Name} cede {mine.Name} desde su celda"),
                                    Heroes = new List<string> { s.Hero.StringId, P.StringId, from.Leader.StringId },
                                    Kingdoms = new[] { s.Hero.MapFaction, P.MapFaction }.Where(f => f != null).Select(f => f.StringId).Distinct().ToList(),
                                    SettlementId = mine.StringId,
                                });
                            }
                            catch (Exception ex) { Log.Error("Noticia de la cesión forzada", ex); }
                        }
                        AfterForcedDeal(s, ctx, $"you signed {mine.Name} over to them" + (others ? $", taking it from clan {from.Name} of your own realm, who now hold it against you and against them" : "") +
                                                (last ? $". It was the last holding of {holder}: there is no town or castle left to it" : ""), false);
                        return last ? L.F("AIL_e7b1c79c", $"{s.Name} te cede {mine.Name} desde su cautiverio: era la última plaza de {holder}.")
                                    : L.F("AIL_e7b1c787", $"{s.Name} te cede {mine.Name} desde su cautiverio.");
                    }
                    var st = OrderSystem.FindSettlement(arg, x => (x.IsTown || x.IsCastle) && x.OwnerClan == s.Hero.Clan && !x.IsUnderSiege);
                    if (st == null) return Nope(null, "that is not a town or castle your clan holds (or it is under siege)");
                    if (FiefsOf(s.Hero.Clan).Count <= 1) return Nope(null, $"{st.Name} is the last fief of your house: you cannot give it away");
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
                                   && !AtWarWithPlayer(s) && PC.Tier >= 2 && !AILegacy.World.Banished.IsBanned(PC, KingdomOf(s)),
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
                IsAvailable = s => S.Get.AllowFealty && NpcIsRuler(s) && PC != null && PC.Kingdom == null && !AtWarWithPlayer(s) && PC.Tier >= 1
                                   && !AILegacy.World.Banished.IsBanned(PC, KingdomOf(s)),
                Execute = (s, arg, ctx) =>
                {
                    ChangeKingdomAction.ApplyByJoinFactionAsMercenary(PC, KingdomOf(s));
                    return L.F("AIL_f362d5cb", $"{s.Name} contrata a tu clan como mercenarios de {KingdomOf(s).Name}.");
                }
            },
            new ActionDef
            {
                Name = "pardon_banishment", Usage = "pardon_banishment",
                Description = "As ruler, you lift the banishment you (or your predecessor) placed on the other person's clan, so that it may serve your kingdom again. " +
                              "Only if they have truly made amends, paid for it, or you have real need of them. It does not take them back by itself: that is a separate decision.",
                IsAvailable = s => S.Get.AllowFealty && NpcIsRuler(s) && PC != null && AILegacy.World.Banished.Find(PC, KingdomOf(s)) != null,
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s);
                    if (!AILegacy.World.Banished.Pardon(PC, k)) return Nope(null, "their clan is not banished from your realm");
                    return L.F("AIL_b7d2f014", $"{s.Name} levanta el destierro de tu clan: puedes volver a servir a {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "dismiss_from_kingdom", Usage = "dismiss_from_kingdom",
                Description = "As ruler, you cast the other person's clan out of your kingdom (ending their vassalage or their mercenary contract), effective now. A vassal clan cast out is banished and cannot return until you pardon it. " +
                              "Only for treason, grave insult, open defiance, or if they themselves ask to be released.",
                IsAvailable = s => S.Get.AllowFealty && NpcIsRuler(s) && PC != null && PC.Kingdom == KingdomOf(s) && KingdomOf(s).RulingClan != PC,
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s);
                    if (PlayerMercenary) ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(PC);
                    else { ChangeKingdomAction.ApplyByLeaveKingdom(PC); if (PC.Kingdom != k) AILegacy.World.Banished.Ban(PC, k); }    // an expelled vassal stays banished
                    return L.F("AIL_5a577403", $"{s.Name} expulsa a tu clan de {k.Name}.");
                }
            },
            new ActionDef
            {
                Name = "leave_kingdom", Usage = "leave_kingdom",
                Description = "You take your clan out of the kingdom you serve, breaking your oath (or ending your mercenary contract), effective now. You lose your place at court and may lose your lands. " +
                              "Only when you have truly broken with your ruler: betrayal, ruin, or a far better future elsewhere.",
                IsAvailable = s => S.Get.AllowFealty && s.IsHero && s.Hero.IsClanLeader && FreeOrDuress(s) && KingdomOf(s) != null && KingdomOf(s).Leader != s.Hero
                                   && s.Hero.Clan != PC,
                Extra = s => Duress(s) ? "As their prisoner you can renounce your ruler from your chains: your clan leaves that kingdom and its wars, and you stop being their enemy." : "",
                Execute = (s, arg, ctx) =>
                {
                    var k = KingdomOf(s); var c = s.Hero.Clan;
                    bool forced = Duress(s);
                    if (c.IsUnderMercenaryService) ChangeKingdomAction.ApplyByLeaveKingdomAsMercenary(c);
                    else ChangeKingdomAction.ApplyByLeaveKingdom(c);
                    if (c.Kingdom == k) return Nope(null, $"your clan is still in {k.Name}: it could not leave, and nothing has changed");
                    if (forced) AfterForcedDeal(s, ctx, $"you took your clan out of {k.Name}, renouncing your ruler", true);
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
                    // A governor who just switches clan leaves the town with a foreign governor (and the game crashes when you open it)
                    try { if (s.Hero.GovernorOf != null) ChangeGovernorAction.RemoveGovernorOf(s.Hero); }
                    catch (Exception ex) { Log.Error("Quitar la gobernación antes de cambiar de clan", ex); }
                    FreeBeforeJoining(s.Hero);
                    if (s.Hero.IsPrisoner) return Nope(null, "you are still held prisoner and could not join their clan");
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

            // ----------------------------------------------------------- combat
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
                Execute = (s, arg, ctx) => Challenge(s, "duel")
            },
            new ActionDef
            {
                Name = "duel_to_death", Usage = "duel_to_death",
                Description = "A duel with weapons where the loser dies. Only for mortal insults, blood feuds or matters you would truly die for, and only when both of you clearly agreed to those terms. " +
                              "Cautious characters and those with much to lose refuse.",
                IsAvailable = s => (Combat.CanFightHere() || MapFight(s)) && s.IsHero && !s.Hero.IsPrisoner,
                Execute = (s, arg, ctx) => Challenge(s, "death")
            },
            new ActionDef
            {
                Name = "die", Usage = "die:<kind>|<how, in a few words>|<what is found>",
                Description = "Your life ends here and now, as the direct outcome of this scene. EXTREMELY rare and never for effect. Only when one of these is truly happening, and <kind> says which: " +
                              "executed (you are their prisoner and they have just plainly carried out your execution), killed (they have just killed you with their own hand in this scene and you could not stop it), " +
                              "suicide (you take your own life), poison, wounds (you were already dying of your wounds and this is your last breath), illness, accident. " +
                              "<what is found> is one sentence, in the language you speak, about what those who come afterwards find on you or beside you (a letter, a ring, a weapon, nothing at all); leave it empty if there is nothing worth telling. " +
                              "Say your last words in the same reply. If in any doubt, do not use it.",
                IsAvailable = s => S.Get.AllowRoleplayDeath && s.IsHero && s.Hero.IsAlive && s.Hero != P && CanDieHere(s)
                                   && !(s.Hero.Clan == Clan.PlayerClan && !S.Get.RoleplayDeathAnywhere),
                Execute = (s, arg, ctx) =>
                {
                    ctx.EndConversation = true;
                    var parts = (arg ?? "").Split('|');
                    string kind = parts.Length > 1 ? parts[0].Trim().ToLowerInvariant() : "";
                    string how = (parts.Length > 1 ? parts[1] : parts[0]).Trim();
                    string found = parts.Length > 2 ? string.Join("|", parts.Skip(2)).Trim() : "";
                    if (!DeathKinds.Contains(kind)) kind = HeldByPlayer(s) ? "executed" : s.Hero.IsWounded ? "wounds" : "suicide";
                    // an execution needs a prisoner of yours; without one it is a killing by your hand
                    if (kind == "executed" && !HeldByPlayer(s)) kind = "killed";
                    Combat.QueueDeath(s.Hero, kind, how, found);
                    return $"{s.Name} muere: {(string.IsNullOrWhiteSpace(how) ? L.T("AIL_4b487b93", "aquí acaba su historia") : how)}.";
                }
            },
            new ActionDef
            {
                Name = "attack", Usage = "attack",
                Description = "You end the parley and attack them with your party right now: there will be a battle. Use it when you have decided to fight (you are at war and see no reason to let them go).",
                IsAvailable = s => Combat.EnemyOnMap(s),
                Extra = s => $"Your party: {Combat.Men(Combat.MapFoeParty(s))} fighting men. Theirs: {Combat.Men(MobileParty.MainParty)}.",
                // The battle starts on its own when the chat closes (with lords and with lordless parties)
                Execute = (s, arg, ctx) => { Combat.QueueMapAttack(s); ctx.EndConversation = true; return L.F("AIL_53ea433d", $"{s.Name} da la charla por terminada y se lanza al ataque."); }
            },
            new ActionDef
            {
                Name = "surrender", Usage = "surrender",
                Description = "You yield: you and all your men lay down arms and become their prisoners, without a battle. Only when the fight is clearly hopeless or they gave you terms you accept; " +
                              "proud or brave characters would rather fight.",
                IsAvailable = s => Combat.EnemyOnMap(s) && Combat.Men(Combat.MapFoeParty(s)) * 2 <= Combat.Men(MobileParty.MainParty),
                Execute = (s, arg, ctx) => { ctx.EndConversation = true; return Combat.QueueSurrender(s); }
            },
            new ActionDef
            {
                Name = "let_pass", Usage = "let_pass",
                Description = "You call your people off and let them go on their way unharmed: there will be no fight. Use it when you have decided not to fight them " +
                              "(they paid what you asked, they are too strong, they talked you round, or they are not worth the blood).",
                IsAvailable = s => Combat.EnemyOnMap(s),
                Execute = (s, arg, ctx) =>
                {
                    ctx.LetPass = true; ctx.EndConversation = true;
                    Combat.KeepEncounter = false;
                    // Their word is given: for one day nobody (theirs or anyone else) goes after the player
                    try { if (s.IsHero) MobileParty.MainParty.IgnoreByOtherPartiesTill(CampaignTime.HoursFromNow(24)); } catch { }
                    return L.F("AIL_7a3f52c8", $"{s.Name} aparta a los suyos y os deja pasar.");
                }
            },
            new ActionDef
            {
                Name = "accept_surrender", Usage = "accept_surrender",
                Description = "You accept THEIR surrender: they and their whole party lay down arms and become your prisoners, without a battle. ONLY if in their last message they clearly said they yield or surrender to you.",
                IsAvailable = s => Combat.EnemyOnMap(s),
                Execute = (s, arg, ctx) =>
                {
                    if (Repeated(s, "accept_surrender", 1)) return null;
                    LastDone[s.Key + "|accept_surrender"] = (1, DateTime.UtcNow);
                    NoteOutcome(s, "[Pending: you are ready to take their surrender. It happens only if they go through with it now.]");
                    Ask(L.F("AIL_b8f2e6ab", $"Rendirte a {s.Name}"),
                        L.F("AIL_b8f2e6ac", $"Tú y todos tus hombres quedaréis prisioneros de {s.Name}. No hay vuelta atrás."),
                        L.T("AIL_b8f2e6ad", "Rendirse"), L.T("AIL_b8f2e6ae", "No"),
                        () =>
                        {
                            NoteOutcome(s, "[Done: they have surrendered to you. They and their men are your prisoners now.]");
                            Memory.SaveFiles.Flush();
                            Combat.QueuePlayerSurrender();
                            ChatSession.EndFromOutside();
                        },
                        () => { NoteOutcome(s, "[Did not happen: they did not surrender after all.]"); Memory.SaveFiles.Flush(); });
                    return L.F("AIL_b8f2e6af", $"{s.Name} está dispuesto a aceptar tu rendición.");
                }
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
                    // If a guard is in earshot, they come over and decide. If not, the report stands
                    bool came = Justice.Summon($"Moments ago {(s.IsHero ? s.Name : "a local")} shouted for the guard, accusing the armed person in front of you of threatening them.", null, true);
                    if (came) return L.F("AIL_c8741a3a", $"{s.Name} llama a gritos a la guardia.");
                    ChangeCrimeRatingAction.Apply(s.Settlement.MapFaction, 15f, true);
                    return L.F("AIL_e9f35fb7", $"{s.Name} llama a la guardia: te has ganado mala fama ante {s.Settlement.MapFaction.Name}.");
                }
            },

            // ----------------------------------------------------------- common people becoming someone
            new ActionDef
            {
                Name = "join_with_band", Usage = "join_with_band",
                Description = "Your whole band throws in its lot with them: every one of your men joins their party as soldiers, and you with them. Very rare. Only when they made a real offer of service " +
                              "(pay, food, a pardon, plunder, a cause) that beats the life you lead, your men would follow you into it, and you believe they can deliver. " +
                              "A threat alone is not enough unless you are plainly beaten and this is the way out they offered.",
                IsAvailable = s => S.Get.AllowBandRecruit && s.Band != null && s.Band.IsActive && s.Band.MapFaction != null && s.Band.MapFaction.IsBanditFaction
                                   && s.Band.MapEvent == null && !Combat.BandJoinPending,
                Extra = s => $"Your band: {s.Band.MemberRoster.TotalManCount} men. Their party: {MobileParty.MainParty.MemberRoster.TotalManCount} of {MobileParty.MainParty.Party.PartySizeLimit} they can feed and lead.",
                Execute = (s, arg, ctx) =>
                {
                    int men = s.Band.MemberRoster.TotalManCount;
                    Combat.QueueBandJoin(s.Band);
                    ctx.EndConversation = true;
                    return L.F("AIL_e7b1c4fb", $"La banda entera ({men} hombres) se une a tu partida.");
                }
            },
            new ActionDef
            {
                Name = "join_their_company", Usage = "join_their_company",
                Description = "You leave your life here for good and go with the other person as one of their companions. Very rare: only if they clearly and seriously offered to take you into their company, " +
                              "you have a real reason to go (nothing holding you here, a debt, a dream, love, hunger) and you have come to trust them. A passing remark or a joke is not an offer.",
                IsAvailable = s => S.Get.AllowPromotions && !s.IsHero && s.Band == null && s.Age >= 18 && Clan.PlayerClan != null && Clan.PlayerClan.Companions.Count < Clan.PlayerClan.CompanionLimit
                                   && s.Memory.Profile != null && !s.Memory.Profile.IsEmpty && s.Memory.Turns.Count >= S.Get.PromotionMinTurns,
                Execute = (s, arg, ctx) =>
                {
                    var agent = SceneAgent();
                    var hero = World.People.Promote(s, agent, true, out string perr) ?? Promote(s);
                    if (hero == null) return null;
                    // One of your own soldiers leaves the ranks: the party has one fewer of that troop
                    try
                    {
                        var troop = s.Character;
                        bool yours = agent != null && agent.Origin != null && agent.Origin.BattleCombatant == PartyBase.MainParty;
                        if (yours && troop != null && !troop.IsHero && MobileParty.MainParty.MemberRoster.GetTroopCount(troop) > 0)
                        {
                            MobileParty.MainParty.MemberRoster.AddToCounts(troop, -1);
                            Log.Info($"{hero.Name} sale de filas ({troop.Name}) y pasa a ser compañero");
                        }
                    }
                    catch (Exception ex) { Log.Warn("Sacar de filas al soldado ascendido: " + ex.Message); }
                    ctx.EndConversation = true;
                    try { if (agent != null) Combat.FadeAfterTalk(agent); } catch { }
                    return L.F("AIL_f65463d8", $"{hero.Name} deja su vida aquí y se une a tu compañía.");
                }
            },

            // ----------------------------------------------------------- messages to other NPCs
            new ActionDef
            {
                Name = "send_word", Usage = "send_word:<name of a real person>|<what you tell them, one or two sentences in English>|<public, private or secret>|<answer, or empty>|about=<name>|pass_to=<name>|importance=<1-3>",
                Description = "You pass on to another real person something about the one in front of you, or about someone else: what they said, did, asked of you, promised, threatened or confessed. " +
                              "Use it when your duty, loyalty or interest calls for it (warn your lord, tell your spouse, alert a friend, denounce them), or when they ask you to carry a message. " +
                              "It travels at the speed of a rider. public = you will tell it around; private = for that person only; secret = in confidence. They do not see this unless you tell them. " +
                              "Write answer when you need that person's reply (you are asking them something, or carrying a question for the one in front of you): their word comes back to you by the same road, and you can pass it on when you next speak. " +
                              "The last three parts are optional: about = who it is about when it is not the one in front of you; pass_to = someone the recipient must hand it on to; importance = 1 small talk, 2 worth telling, 3 urgent.",
                IsAvailable = s => S.Get.EnableInfoExchange && s.IsHero && !s.Hero.IsPrisoner && InfoExchange.Confidants(s.Hero, 1).Count > 0,
                Extra = s => InfoExchange.DescribeConfidants(s.Hero),
                Execute = (s, arg, ctx) =>
                {
                    var parts = (arg ?? "").Split('|');
                    if (parts.Length < 2) return null;
                    string secrecy = "private", about = null, passTo = null; bool wants = false; int importance = 2;
                    // after the first two, parts may come in order (secrecy, answer) or as key=value, in any order
                    for (int i = 2; i < parts.Length; i++)
                    {
                        string p = parts[i].Trim();
                        int eq = p.IndexOf('=');
                        string key = eq > 0 ? p.Substring(0, eq).Trim().ToLowerInvariant() : "", val = eq > 0 ? p.Substring(eq + 1).Trim() : p;
                        if (key == "about") about = val;
                        else if (key == "pass_to" || key == "relay_to" || key == "forward_to") passTo = val;
                        else if (key == "importance") int.TryParse(new string(val.Where(char.IsDigit).ToArray()), out importance);
                        else if (key == "secrecy") secrecy = val;
                        else if (key == "answer") wants = true;
                        else if (i == 2) secrecy = val;
                        else if (val.ToLowerInvariant().StartsWith("answer")) wants = true;
                    }
                    if (importance == 0) importance = 2;
                    return InfoExchange.Send(s, parts[0].Trim(), parts[1].Trim(), secrecy, wants, about, passTo, importance);
                }
            },
            new ActionDef
            {
                Name = "give_message", Usage = "give_message:<name of a real person>|<what the one in front of you must tell them, in English>",
                Description = "You ask the one in front of you to carry word by hand to another real person, because you trust them more than a rider or you want them to hear it from that mouth. " +
                              "Only when they have agreed to carry it. It reaches that person when they next speak to them, and you will learn whether it was delivered.",
                IsAvailable = s => S.Get.EnableInfoExchange && s.IsHero && !s.Hero.IsPrisoner && !(Hero.MainHero?.IsPrisoner ?? true),
                Execute = (s, arg, ctx) =>
                {
                    var parts = (arg ?? "").Split('|');
                    if (parts.Length < 2) return null;
                    return InfoExchange.GiveToPlayer(s, parts[0].Trim(), string.Join("|", parts.Skip(1)).Trim());
                }
            },

            // ----------------------------------------------------------- intrigue
            new ActionDef
            {
                Name = "abandon_plot", Usage = "abandon_plot",
                Description = "You give up the scheme you are secretly running, for good. Only if you truly mean to: they caught you out, frightened you, bought you off or talked you out of it.",
                IsAvailable = s => s.IsHero && Intrigue.Enabled && Intrigue.RunBy(s.Hero) != null,
                Execute = (s, arg, ctx) => Intrigue.Abandon(s.Hero)
            },
            new ActionDef
            {
                Name = "betray_plot", Usage = "betray_plot",
                Description = "You tell the one in front of you everything about the secret plot you are part of: who is behind it, against whom, and what it is. Only if you have decided to (for a price, out of fear, or out of conscience).",
                IsAvailable = s => s.IsHero && Intrigue.Enabled && Intrigue.HelpedBy(s.Hero) != null,
                Execute = (s, arg, ctx) => Intrigue.Betray(s.Hero)
            },
            new ActionDef
            {
                Name = "condemn_plot", Usage = "condemn_plot:<name of the plotter>",
                Description = "As ruler, you condemn a noble of your realm for plotting against the one in front of you: they lose your favour and pay a heavy fine. Only if you believe the accusation and will act on it.",
                IsAvailable = s => s.IsHero && Intrigue.Enabled && Intrigue.AnyKnownForRuler(s.Hero),
                Execute = (s, arg, ctx) => Intrigue.Condemn(s.Hero, arg)
            },
            new ActionDef
            {
                Name = "start_plot", Usage = "start_plot:<name of a real noble>|<smear, murder or seduce>",
                Description = "At the request of the one in front of you, you agree to scheme in secret against another real noble: smear = spread lies to ruin their name; murder = have them killed; seduce = win over their spouse. " +
                              "Only if you are the kind of person who would, you have your own reasons or a good price, and they truly asked you to.",
                IsAvailable = s => s.IsHero && Intrigue.Enabled && s.Hero.IsLord && !s.Hero.IsPrisoner && Intrigue.RunBy(s.Hero) == null
                                   && s.Hero.GetTraitLevel(TaleWorlds.CampaignSystem.CharacterDevelopment.DefaultTraits.Honor) <= 0,
                Execute = (s, arg, ctx) =>
                {
                    var parts = (arg ?? "").Split('|');
                    return Intrigue.StartByPlayer(s.Hero, parts[0].Trim(), parts.Length > 1 ? parts[1] : "smear");
                }
            },

            // ----------------------------------------------------------- the guard decides (only the guard handling the case)
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

            // ----------------------------------------------------------- armies
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

            // ----------------------------------------------------------- conversation
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
        /// Can you two marry? The game's rules only cover nobles. Here it's also open to companions, wanderers and notables,
        /// with the same basic conditions: adults, alive, not close kin, and unmarried (unless the married setting is on).
        /// </summary>
        /// <summary>Ends h's current marriage and records each as the other's former spouse, as the game does on a death.</summary>
        private static void EndMarriage(Hero h)
        {
            var ex = h.Spouse;
            if (ex == null) return;
            h.Spouse = null;
            if (ex.Spouse == h) ex.Spouse = null;
            AddExSpouse(h, ex); AddExSpouse(ex, h);
        }

        private static void AddExSpouse(Hero of, Hero ex)
        {
            try
            {
                // Hero.ExSpouses is read-only; the list behind it is private
                var list = HarmonyLib.AccessTools.Field(typeof(Hero), "_exSpouses")?.GetValue(of) as System.Collections.IList;
                if (list != null && !list.Contains(ex)) list.Add(ex);
            }
            catch (Exception e) { Log.Warn("No se pudo anotar al ex cónyuge: " + e.Message); }
        }

        private static void RemoveExSpouse(Hero of, Hero ex)
        {
            try { (HarmonyLib.AccessTools.Field(typeof(Hero), "_exSpouses")?.GetValue(of) as System.Collections.IList)?.Remove(ex); } catch { }
        }

        /// <summary>If h leads a clan that is not the player's, the clan gets a new head before h marries out of it.</summary>
        private static void StepDownAsClanLeader(Hero h)
        {
            try
            {
                var clan = h.Clan;
                if (clan == null || clan == Clan.PlayerClan || clan.Leader != h) return;
                var heir = clan.Heroes.Where(x => x != h && x.IsAlive && !x.IsChild && x.IsLord).OrderByDescending(x => x.Age).FirstOrDefault();
                if (heir != null) ChangeClanLeaderAction.ApplyWithSelectedNewLeader(clan, heir);
                else ChangeClanLeaderAction.ApplyWithoutSelectedNewLeader(clan);
                Log.Info($"{h.Name} deja la jefatura del clan {clan.Name} antes de casarse{(heir != null ? ": pasa a " + heir.Name : "")}");
            }
            catch (Exception e) { Log.Error("Dejar la jefatura del clan antes de la boda", e); }
        }

        /// <summary>
        /// The wedding, with fallbacks. The game's own action refuses some pairs (same clan, same sex, odd clan
        /// states) without saying why, so if it leaves them unmarried it is tried the other way round, and then by
        /// hand.
        /// </summary>
        private static void Marry(Hero a, Hero b)
        {
            bool sameClan = a.Clan != null && a.Clan == b.Clan;
            if (!sameClan)
            {
                try { MarriageAction.Apply(a, b); } catch (Exception e) { Log.Warn("Boda, primer intento: " + e.Message); }
                if (a.Spouse == b) return;
                try { MarriageAction.Apply(b, a); } catch (Exception e) { Log.Warn("Boda, segundo intento: " + e.Message); }
                if (a.Spouse == b) return;
            }
            try
            {
                try { CampaignEventDispatcher.Instance.OnBeforeHeroesMarried(a, b, true); } catch { }
                a.Spouse = b;
                if (b.Spouse != a) b.Spouse = a;
                if (b.Clan != a.Clan && a.Clan != null && (b.Clan == null || b.Clan.Leader != b)) b.Clan = a.Clan;
                Log.Info($"Boda de {a.Name} y {b.Name} hecha a mano: el juego no la aceptó por su vía normal");
            }
            catch (Exception e) { Log.Error("Boda a mano", e); }
        }

        private static bool CanWed(Hero h)
        {
            try
            {
                // Already your spouse, no second wedding
                if (h == null || h == P || P.Spouse == h || h.Spouse == P) return false;
                if (SafeSuitable(h)) return true;
                var cfg = S.Get;
                if (!cfg.MarryCommoners && !(h.IsLord)) return false;
                if (h == null || !h.IsAlive || h.IsChild || h.Age < 18 || P.Age < 18 || h == P || Bonds.IsCloseFamily(h)) return false;
                if (h.IsFemale == P.IsFemale && !cfg.RomanceSameSex) return false;
                if ((h.Spouse != null && h.Spouse != P || P.Spouse != null && P.Spouse != h) && !cfg.RomanceWithMarried) return false;
                if (h.Clan != null && h.Clan.Leader == h && h.Clan != Clan.PlayerClan) return false;      // another clan's leader won't leave it for a wedding
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
        /// A plain townsperson becomes a real character (a hero) and your companion, keeping their name,
        /// their personality and everything you talked about.
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

                // Move their resident memory over to the new hero record
                var old = s.Memory;
                var mem = Memory.MemoryStore.Get(hero.StringId, hero.Name.ToString());
                mem.Profile = old.Profile; mem.Turns = old.Turns.ToList(); mem.Notes = old.Notes.ToList(); mem.Summary = old.Summary;
                mem.KnownPlayerName = old.KnownPlayerName; mem.Trust = old.Trust; mem.TrustSet = old.TrustSet; mem.Romance = old.Romance;
                mem.Mood = old.Mood; mem.MoodReason = old.MoodReason; mem.MoodDay = old.MoodDay; mem.ConversationCount = old.ConversationCount + 1;
                mem.LastConversationDay = CampaignTime.Now.ToDays; mem.Archetype = old.Archetype; mem.ArchetypeText = old.ArchetypeText;
                mem.Morality = old.Morality; mem.Profanity = old.Profanity; mem.Piety = old.Piety;
                if (mem.Profile != null)
                    mem.Profile.Backstory = ((mem.Profile.Backstory ?? "") + $" I left {born?.Name} to follow {Hero.MainHero.Name}.").Trim();
                World.ResidentFaces.Forget(s.Key);
                Log.Info($"{name} deja de ser un vecino de {born?.Name} y pasa a ser héroe ({hero.StringId}) y compañero");
                return hero;
            }
            catch (Exception ex) { Log.Error("Convertir a un vecino en compañero", ex); return null; }
        }

        /// <summary>Registers one editable template per action (prompts\actions\).</summary>
        public static void RegisterTemplates()
        {
            foreach (var a in All) Prompt.Templates.GetOrAdd("actions/" + a.Name + ".txt", a.Description);
            var _ = TDeedNames.Key + TNotDone.Key + TPromiseNudge.Key;
        }

        // --- items
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

        /// <summary>
        /// Where the NPC's gifts come from: always a real inventory. Their party (or the clan party they travel with).
        /// With no party, the stock of the place they're in, if it's theirs (their clan owns it, or they're one of its notables)
        /// or if they live off selling it (merchant, artisan, villager...). Otherwise null, so they can't give anything.
        /// </summary>
        private static ItemRoster GiveSource(Speaker s, out string where)
        {
            where = "nothing";
            try
            {
                var p = Led(s);
                if (p != null) { where = "your party's baggage"; return p.ItemRoster; }
                if (s.IsHero)
                {
                    var bp = s.Hero.PartyBelongedTo;
                    if (bp != null && !bp.IsMainParty && bp.ActualClan != null && bp.ActualClan == s.Hero.Clan && bp.ActualClan != Clan.PlayerClan)
                    { where = "your clan's baggage"; return bp.ItemRoster; }
                }
                var st = s.Settlement;
                if (st?.ItemRoster == null) return null;
                bool own = s.IsHero && ((st.OwnerClan != null && s.Hero.Clan == st.OwnerClan) || st.Notables.Contains(s.Hero));
                bool livesOffIt = !s.IsHero && s.ResidentSettlement == st && s.Character != null && LivesOffStock(s.Character.Occupation);
                if (!own && !livesOffIt) return null;
                where = $"the stores of {st.Name}";
                return st.ItemRoster;
            }
            catch { return null; }
        }

        private static bool LivesOffStock(Occupation o)
        {
            switch (o)
            {
                case Occupation.Merchant: case Occupation.GoodsTrader: case Occupation.Artisan: case Occupation.ShopWorker: case Occupation.Tavernkeeper:
                case Occupation.Villager: case Occupation.Blacksmith: case Occupation.HorseTrader:
                    return true;
            }
            return false;
        }

        private static bool HasAny(ItemRoster r)
        {
            try { return r != null && r.Any(e => e.EquipmentElement.Item != null && e.Amount > 0); }
            catch { return false; }
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

        // --- kingdom and clan
        private static Clan PC => Clan.PlayerClan;
        private static Kingdom KingdomOf(Speaker s) => s.IsHero ? s.Hero.MapFaction as Kingdom : null;
        private static bool NpcIsRuler(Speaker s) => s.IsHero && KingdomOf(s) != null && KingdomOf(s).Leader == s.Hero && !s.Hero.IsPrisoner;
        private static bool PlayerMercenary => PC != null && PC.IsUnderMercenaryService;
        private static List<Settlement> FiefsOf(Clan c) => c == null ? new List<Settlement>() : c.Settlements.Where(x => x.IsTown || x.IsCastle).ToList();

        // ---- actions registered by other mods (AILegacy.Api.AILegacyApi.RegisterAction)
        internal static bool RegisterModAction(string name, string description, string argumentHint, Func<Hero, bool> isAvailable, Func<Hero, string, string> execute)
        {
            if (string.IsNullOrEmpty(name) || !name.StartsWith("mod_") || execute == null) return false;
            All.RemoveAll(a => a.Name == name);
            All.Add(new ActionDef
            {
                Name = name,
                Usage = string.IsNullOrEmpty(argumentHint) ? name : name + ":<" + argumentHint + ">",
                Description = description,
                // Another mod's failure stays in the log. For the conversation, the action is just unavailable or didn't happen
                IsAvailable = s =>
                {
                    try { return s.IsHero && (isAvailable == null || isAvailable(s.Hero)); }
                    catch (Exception ex) { Log.Error("API: fallo en la condición de la acción " + name, ex); return false; }
                },
                Execute = (s, arg, ctx) =>
                {
                    string shown;
                    try { shown = execute(s.Hero, arg ?? ""); }
                    catch (Exception ex) { Log.Error("API: fallo al ejecutar la acción " + name, ex); shown = null; }
                    return shown ?? Nope(null, "it could not be done");
                },
            });
            Log.Info("API: acción registrada por otro mod: " + name);
            return true;
        }

        internal static int UnregisterModActions(string prefix) => string.IsNullOrEmpty(prefix) || !prefix.StartsWith("mod_") ? 0 : All.RemoveAll(a => a.Name.StartsWith(prefix));

        /// <summary>Short list of actions open to that character (syntax and first sentence), for group chats.</summary>
        public static string DescribeBrief(Speaker s, int max)
        {
            if (!S.Get.EnableActions || s == null || !s.IsHero) return "";
            var parts = new List<string>();
            foreach (var a in All.ToList())
            {
                bool ok;
                try { ok = a.IsAvailable(s); } catch { ok = false; }
                if (!ok || NoNote.Contains(a.Name)) continue;          // actions that change nothing (leave, suspect) aren't needed here
                string d = Prompt.Templates.GetOrAdd("actions/" + a.Name + ".txt", a.Description).Text ?? "";
                int dot = d.IndexOf(". ", StringComparison.Ordinal);
                if (dot > 20) d = d.Substring(0, dot);
                if (d.Length > 120) d = d.Substring(0, 120) + "…";
                parts.Add(a.Usage + " (" + d.Trim().TrimEnd('.') + ")");
                if (parts.Count >= max) break;
            }
            return string.Join("; ", parts);
        }

        public static string DescribeAvailable(Speaker s, bool viaLetter = false)
        {
            if (!S.Get.EnableActions) return "";
            var open = new List<ActionDef>();
            foreach (var a in All.ToList())      // copy: another mod's availability check could register or remove actions
            {
                if (viaLetter && !a.Letter) continue;
                bool ok;
                try { ok = a.IsAvailable(s); } catch { ok = false; }
                if (ok) open.Add(a);
            }
            // By topic: of the topic-dependent actions, only the closest get full text; the rest are just named
            var brief = new List<ActionDef>();
            try
            {
                var q = s.Memory.QueryVec;
                int top = Math.Max(1, S.Get.ActionRagTopK);
                var topical = open.Where(a => ByTopic.Contains(a.Name) && ActionVecs.ContainsKey(a.Name)).ToList();
                if (!viaLetter && S.Get.EnableActionRag && q != null && topical.Count > top && Llm.Embeddings.SameModel(_actionVecsModel, s.Memory.QueryModel))
                {
                    var keep = new HashSet<string>(topical.OrderByDescending(a => Llm.Embeddings.Cosine(q, ActionVecs[a.Name])).Take(top).Select(a => a.Name));
                    brief = topical.Where(a => !keep.Contains(a.Name)).ToList();
                }
            }
            catch { brief.Clear(); }
            var sb = new StringBuilder();
            foreach (var a in open)
            {
                if (brief.Contains(a)) continue;
                string extra = "";
                try { if (a.Extra != null) extra = " " + a.Extra(s); } catch { }
                // The description comes from the editable template prompts\actions\<action>.txt
                sb.AppendLine($"- {a.Usage}: {Prompt.Templates.GetOrAdd("actions/" + a.Name + ".txt", a.Description).Text}{extra}");
            }
            if (brief.Count > 0)
                sb.AppendLine("- Also open to you if the talk turns to them (same rules; written exactly like this): " + string.Join(" · ", brief.Select(a => a.Usage)));
            return sb.ToString().TrimEnd();
        }

        private static readonly Prompt.Tpl TCannot = new Prompt.Tpl("rules/actions_unavailable.txt",
@"## What you CANNOT do right now, and why
These are not in your list of actions, so they cannot happen in this talk:
{list}
If the talk turns to one of them, say plainly and in character that it is not in your power, and why. Never promise it, never speak or narrate as if it were done or being arranged, and do not send them away with ""it will be seen to"": with no action for it, nothing happens.");

        private static readonly Prompt.Tpl TActNow = new Prompt.Tpl("rules/actions_act_now.txt",
            "When you agree to something that one of the actions above carries out, put that action in THIS reply, together with the words. Never put it off (\"before the moon sets\", \"my scribes will see to it\", \"it will be entered in the ledgers\"): " +
            "nothing happens later by itself, and agreeing without the action changes nothing in the world. In \"remember\", never record as done something you used no action for: write it as said or promised.");

        /// <summary>The instruction that goes under the list of open actions.</summary>
        public static string ActNowRule => TActNow.Text;

        /// <summary>
        /// The important things this character cannot do right now (fealty, land, peace, marriage, joining), each
        /// with its reason, so that he says so instead of promising or narrating them. Empty when there are none.
        /// </summary>
        public static string DescribeUnavailable(Speaker s)
        {
            if (!S.Get.EnableActions || s == null || !s.IsHero) return "";
            var lines = new List<string>();
            foreach (var a in All.ToList())
            {
                if (a.WhyNot == null) continue;
                try
                {
                    if (a.IsAvailable(s)) continue;
                    // one line for a marriage, whichever of the two is asked
                    if (a.Name == "accept_marriage" && All.Any(o => o.Name == "propose_marriage" && o.IsAvailable(s))) continue;
                    string why = a.WhyNot(s);
                    if (string.IsNullOrWhiteSpace(why)) continue;
                    string what = a.Name == "swear_fealty" ? "Swear fealty to them with your clan" : a.Name == "serve_as_mercenary" ? "Enter their service as mercenaries"
                                : a.Name == "grant_fief" ? "Give or sign over a town or castle to them" : a.Name == "make_peace" ? "Make peace between your side and theirs"
                                : a.Name == "accept_marriage" ? "Marry them" : a.Name == "join_party" ? "Join their band as a companion" : a.Name.Replace('_', ' ');
                    lines.Add($"- {what}: {why}.");
                }
                catch { }
                if (lines.Count >= 6) break;
            }
            return lines.Count == 0 ? "" : TCannot.Format("list", string.Join("\n", lines));
        }

        /// <summary>Runs the requested actions (main thread). Returns the messages to show.</summary>
        public static List<string> Execute(Speaker s, IEnumerable<string> commands, ActionContext ctx)
        {
            var messages = new List<string>();
            if (!S.Get.EnableActions || commands == null) return messages;

            // Money first, then the rest (e.g. take_gold before join_party)
            var ordered = commands.Where(c => !string.IsNullOrWhiteSpace(c)).Take(5)
                .OrderBy(c => c.Trim().StartsWith("take_gold") || c.Trim().StartsWith("give_gold") ? 0 : 1).ToList();

            var done = new HashSet<string>();
            var executed = new HashSet<string>();      // only actions that really happened block others in their group
            int failNotes = 0;
            // The character is told what did NOT happen (at most three notices per reply)
            void Failed(string cmd, string why)
            {
                // No-effect actions (leave, suspect) get no notice
                string n = cmd.Split(':')[0].Trim().ToLowerInvariant();
                if (NoNote.Contains(n) || (Aliases.TryGetValue(n, out var an) && NoNote.Contains(an)) || failNotes++ >= 3) return;
                NoteOutcome(s, $"[Did not happen: you tried «{Clip(cmd, 90)}» but {why}. Whatever your words announced about it has NOT happened: do not speak or act as if it had. " +
                               "If it still matters, say plainly that you cannot, or do it properly with a listed action.]");
            }
            foreach (var raw in ordered)
            {
                var cmd = raw.Trim();
                int colon = cmd.IndexOf(':');
                var name = (colon >= 0 ? cmd.Substring(0, colon) : cmd).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
                var arg = colon >= 0 ? cmd.Substring(colon + 1).Trim() : "";
                if (Aliases.TryGetValue(name, out var real) && All.All(a => a.Name != name)) { Log.Info($"Acción «{name}» entendida como «{real}»"); name = real; }
                if (!done.Add(name)) continue;
                ctx.Tried.Add(name);

                var def = All.FirstOrDefault(a => a.Name == name);
                if (def == null) { Log.Warn($"Acción desconocida ignorada: {cmd}"); Failed(cmd, "there is no such action: only the actions listed in your briefing exist"); continue; }
                if (ctx.ViaLetter && !def.Letter) { Log.Warn($"Acción no válida por carta, ignorada: {cmd}"); Failed(cmd, "that cannot be done by letter: you are not there in person"); continue; }
                var clash = Exclusive.FirstOrDefault(g => g.Contains(name) && g.Any(o => o != name && executed.Contains(o)));
                if (clash != null) { Log.Warn($"Acción incompatible con otra de la misma respuesta, ignorada: {cmd}"); continue; }
                try
                {
                    if (!def.IsAvailable(s)) { Log.Warn($"Acción no disponible ahora, ignorada: {cmd}"); Failed(cmd, "it is not possible right now (it was not among the actions open to you)"); continue; }
                    _why = null; _repeated = false;
                    int turnsBefore = s.Memory.Turns.Count;
                    var msg = def.Execute(s, arg, ctx);
                    bool happened = msg != null && _why == null;
                    Log.Info($"Acción {cmd} de {s.Name}" + (msg != null ? ": " + msg : "") + (happened ? "" : _repeated ? " (repetida)" : " (no ha ocurrido" + (_why != null ? ": " + _why : "") + ")"));
                    if (!string.IsNullOrEmpty(msg)) messages.Add(msg);          // empty string: it happened, but there's nothing to show
                    else if (msg == null && def.Usage.Contains(":")) Log.Warn($"La acción {cmd} no se pudo cumplir (objetivo no encontrado o no válido)");
                    if (happened)
                    {
                        executed.Add(name);
                        ctx.Executed.Add(name);
                        if (s.IsHero) AILegacy.Api.AILegacyApi.RaiseActionExecuted(s.Hero, name, arg, msg);
                        // News only if it really worked (otherwise you'd get, say, a wedding that never happened)
                        RecordAsNews(s, name);
                        // Actions that already leave their own note (gold, items, troops...) aren't noted twice
                        if (!NoNote.Contains(name) && s.Memory.Turns.Count == turnsBefore)
                            NoteOutcome(s, $"[Done: your action «{Clip(cmd, 90)}» has really taken effect in the world — do not do it again.]");
                    }
                    else if (!_repeated)
                        Failed(cmd, _why ?? (def.Usage.Contains(":") ? "what you named could not be found or is not valid for it" : "the conditions for it were not met"));
                }
                catch (Exception ex) { Log.Error($"Error ejecutando la acción {cmd}", ex); Failed(cmd, "something went wrong and it could not be carried out"); }
                finally { _why = null; _repeated = false; }
            }
            return messages;
        }

        private static string Clip(string t, int max) => t == null ? "" : t.Length <= max ? t : t.Substring(0, max) + "…";

        // ============================================================ words spoken as deeds

        // The actions that carry out each kind of deed PromiseCheck can find in a spoken line
        private static readonly Dictionary<string, string[]> DeedActions = new Dictionary<string, string[]>
        {
            { "fealty", new[] { "swear_fealty", "serve_as_mercenary" } },
            { "fief", new[] { "grant_fief" } },
            // any action that puts the character's money in the other's hand: a sale or a purchase pays too
            { "gold", new[] { "give_gold", "buy_goods", "sell_goods", "take_item", "take_prisoners", "take_troops", "sell_workshop" } },
            { "marriage", new[] { "accept_marriage", "propose_marriage", "wed_now" } },
            { "join", new[] { "join_party", "join_clan", "join_their_company", "join_with_band", "come_along", "follow_me", "join_army", "be_remembered", "escort" } },
            { "peace", new[] { "make_peace" } },
            { "war", new[] { "declare_war" } },
            { "abdicate", new[] { "abdicate" } },
            { "free", new[] { "give_prisoners", "let_go", "let_pass", "set_free" } },
            { "leave", new[] { "leave_kingdom", "swear_fealty", "serve_as_mercenary" } },
        };

        // One line per kind of deed, picked by the word before the colon: how the note names what was only said
        private static readonly Prompt.Tpl TDeedNames = new Prompt.Tpl("rules/promise_deeds.txt",
@"fealty: sworn fealty to them
fief: handed a town or castle over to them
gold: handed them gold
marriage: married them, or accepted the marriage
join: joined them
peace: made peace with them
war: declared war on them
abdicate: given up your crown
free: set someone free
leave: renounced your ruler");

        private static readonly Prompt.Tpl TNotDone = new Prompt.Tpl("rules/promise_not_done.txt",
            "you spoke as if you had {deed} («{quote}»), but you put no action with the words, so in the world it has NOT happened: it was said and nothing more. Do not speak or act as if it were done.");

        private static readonly Prompt.Tpl TPromiseNudge = new Prompt.Tpl("rules/promise_nudge.txt",
            "[In your last reply you spoke as if you had {deed}, but you put no action with the words, so nothing happened. It is in your power right now: either carry it out in this reply with the action {usage}, or tell them plainly that you will not do it, and why. Do not say the words alone again.]");

        private static string DeedName(string kind)
        {
            foreach (var raw in TDeedNames.Text.Split('\n'))
            {
                string line = raw.Trim();
                if (line.StartsWith(kind + ":", StringComparison.OrdinalIgnoreCase)) return line.Substring(kind.Length + 1).Trim();
            }
            return kind;
        }

        /// <summary>What the words claim is already so in the world: then they are a statement of fact, not a deed left undone.</summary>
        private static bool AlreadySo(Speaker s, string kind)
        {
            try
            {
                var h = s.IsHero ? s.Hero : null;
                switch (kind)
                {
                    case "fealty": return h?.Clan != null && PlayerKingdom != null && h.Clan.Kingdom == PlayerKingdom;
                    case "marriage": return h != null && P.Spouse == h;
                    case "join": return h != null && (h.Clan == PC || h.CompanionOf == PC || h.PartyBelongedTo == MobileParty.MainParty);
                    case "peace": return !AtWarWithPlayer(s);
                    case "war": return AtWarWithPlayer(s);
                    case "leave": return h == null || KingdomOf(s) == null || KingdomOf(s).Leader == h;
                }
            }
            catch { }
            return false;
        }

        /// <summary>
        /// After a reply: a deed spoken as done ("I swear fealty to you", "te entrego el castillo") with no action
        /// for it in the same reply did not happen. It is written into the history as "[Not done: ...]", so the
        /// character's next prompt and the summary of the talk take it for words. When the action for it was open
        /// to him, the text of one corrective nudge for his next turn is returned (once per kind of deed and talk:
        /// <paramref name="nudged"/> remembers which). Local, no request to the AI. Main thread.
        /// </summary>
        /// <param name="paidByQuest">The same reply settles or rewards an errand: gold named in it is that reward.</param>
        public static string CheckSpokenDeeds(Speaker s, string reply, ActionContext ctx, ISet<string> nudged, bool paidByQuest = false)
        {
            string nudge = null;
            try
            {
                if (!S.Get.EnableActions || !S.Get.CatchEmptyPromises || s == null || ctx == null) return null;
                foreach (var d in PromiseCheck.Find(reply))
                {
                    if (!DeedActions.TryGetValue(d.Kind, out var names) || (paidByQuest && d.Kind == "gold")) continue;
                    // asked for in this reply: it either happened or already has its own "did not happen" note
                    if (ctx.Tried.Any(names.Contains) || AlreadySo(s, d.Kind)) continue;
                    var open = new List<ActionDef>();
                    foreach (var a in All)
                    {
                        if (a.Name == "wed_now" || !names.Contains(a.Name)) continue;
                        try { if (a.IsAvailable(s)) open.Add(a); } catch { }
                    }
                    // "you are free to go" is also how a lord ends an audience: only a deed when there is someone he can free
                    if (d.Kind == "free" && open.Count == 0) continue;
                    string deed = DeedName(d.Kind);
                    string note = "[Not done: " + TNotDone.Format("deed", deed, "quote", Clip(d.Quote, 120)) + "]";
                    var turns = s.Memory.Turns;
                    // said twice in a row: one note is enough
                    if (turns.Skip(Math.Max(0, turns.Count - 6)).Any(t => t.Text == note)) continue;
                    NoteOutcome(s, note);
                    Log.Warn($"{s.Name} da por hecho algo que no ha hecho ({d.Kind}: «{d.Quote}»), sin acción" + (open.Count > 0 ? $"; podía hacerlo con {open[0].Name}" : "; no tenía acción para ello"));
                    if (nudge == null && open.Count > 0 && nudged != null && nudged.Add(d.Kind))
                        nudge = TPromiseNudge.Format("deed", deed, "usage", string.Join("  or  ", open.Take(2).Select(a => a.Usage.Split(new[] { "  or  " }, StringSplitOptions.None)[0])));
                }
            }
            catch (Exception ex) { Log.Error("Promesas sin acción", ex); }
            return nudge;
        }

        /// <summary>A trailing ":return" means they agreed to come back to the player afterwards. Strips it from the argument.</summary>
        private static bool TakeReturn(ref string arg)
        {
            arg = (arg ?? "").Trim();
            foreach (var suffix in new[] { ":return", ":back", ":and return", ":then return", ",then:return" })
                if (arg.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { arg = arg.Substring(0, arg.Length - suffix.Length).Trim(); return true; }
            return false;
        }

        /// <summary>A named hero who is right here: same place as the player, in the player's party, or in the character's.</summary>
        private static Hero FindPresent(Speaker s, string name)
        {
            string n = NormName(name);
            if (n.Length < 3) return null;
            try
            {
                var here = Settlement.CurrentSettlement;
                var mine = MobileParty.MainParty;
                var theirs = s.IsHero ? s.Hero.PartyBelongedTo : null;
                foreach (var h in Hero.AllAliveHeroes)
                {
                    if (h == null || h == P || (s.IsHero && h == s.Hero) || h.IsChild || h.IsPrisoner) continue;
                    bool present = (here != null && h.CurrentSettlement == here) || (h.PartyBelongedTo != null && (h.PartyBelongedTo == mine || h.PartyBelongedTo == theirs));
                    if (!present) continue;
                    string hn = NormName(h.Name.ToString()), fn = NormName(h.FirstName?.ToString());
                    if (hn == n || fn == n || (n.Length >= 4 && (hn.Contains(n) || n.Contains(fn) && fn.Length >= 4))) return h;
                }
            }
            catch { }
            return null;
        }

        private static void Say(string msg) => TaleWorlds.Library.InformationManager.DisplayMessage(new TaleWorlds.Library.InformationMessage(msg, TaleWorlds.Library.Colors.Yellow));

        /// <summary>A decision that needs the player's word: a yes/no popup over the dialogue.</summary>
        private static void Ask(string title, string text, string yes, string no, Action onYes, Action onNo)
        {
            int session = WorldSim.Session;
            MainThread.Post(() =>
            {
                try
                {
                    TaleWorlds.Library.InformationManager.ShowInquiry(new TaleWorlds.Library.InquiryData(title, text, true, true, yes, no,
                        () => MainThread.Post(() => { try { if (session == WorldSim.Session) onYes?.Invoke(); } catch (Exception ex) { Log.Error("Decisión del jugador", ex); } }),
                        () => MainThread.Post(() => { try { if (session == WorldSim.Session) onNo?.Invoke(); } catch (Exception ex) { Log.Error("Decisión del jugador", ex); } })), true);
                }
                catch (Exception ex) { Log.Error("Ventana de decisión", ex); }
            });
        }

        /// <summary>The wedding, once both have said yes.</summary>
        private static void FinishWedding(Speaker s)
        {
            try
            {
                // Something may have changed between the yes and the confirmation, so check the wedding is still possible
                if (!s.IsHero || !s.Hero.IsAlive || s.Hero.IsPrisoner || !CanWed(s.Hero))
                {
                    NoteOutcome(s, "[Did not happen: the wedding could not take place after all. You are not married.]");
                    return;
                }
                var def = All.First(a => a.Name == "wed_now");
                var msg = def.Execute(s, "", new ActionContext());
                if (msg == null)
                {
                    NoteOutcome(s, "[Did not happen: the wedding could not take place after all. You are not married.]");
                    return;
                }
                NoteOutcome(s, "[Done: the two of you are married now, husband and wife from this day.]");
                Say(msg);
                RecordAsNews(s, "accept_marriage");
                Memory.SaveFiles.Flush();
            }
            catch (Exception ex) { Log.Error("Boda", ex); }
        }

        /// <summary>An armed duel doesn't just start: the player accepts or backs out.</summary>
        private static string Challenge(Speaker s, string kind)
        {
            if (Repeated(s, "challenge|" + kind, 1)) return null;
            LastDone[s.Key + "|challenge|" + kind] = (1, DateTime.UtcNow);
            NoteOutcome(s, kind == "death" ? "[Pending: a duel to the death stands between you. It begins only if they take it up now.]"
                                           : "[Pending: a duel stands between you. It begins only if they take it up now.]");
            Ask(L.F("AIL_b8f2e6b0", $"Duelo con {s.Name}"),
                kind == "death" ? L.T("AIL_b8f2e6b1", "A muerte: quien caiga no se levanta. ¿Aceptas?") : L.T("AIL_b8f2e6b2", "Con armas, hasta que uno caiga. Nadie muere. ¿Aceptas?"),
                L.T("AIL_b8f2e6b3", "Luchar"), L.T("AIL_b8f2e6b4", "Echarse atrás"),
                () =>
                {
                    // Checked when accepting: the chat with them must still be open and the place must allow it
                    string msg = null;
                    var talk = ChatSession.Active;
                    if (talk != null && talk.Who != null && talk.Who.Key == s.Key && !(s.IsHero && (!s.Hero.IsAlive || s.Hero.IsPrisoner)))
                        msg = Combat.CanFightHere() ? Combat.QueueFight(s, kind) : MapFight(s) ? StartMapFight(s, kind) : null;
                    if (msg == null) { NoteOutcome(s, "[Did not happen: the duel could not be fought after all.]"); Memory.SaveFiles.Flush(); return; }
                    NoteOutcome(s, "[Done: they took up the duel; it is being fought now.]");
                    Say(msg);
                    ChatSession.EndFromOutside();
                },
                () =>
                {
                    NoteOutcome(s, "[Did not happen: they backed down from the duel in front of you. No fight took place.]");
                    try { if (s.IsHero) Bonds.ChangeTension(s, -10); } catch { }
                    Say(L.F("AIL_b8f2e6b5", $"Te echas atrás ante {s.Name}."));
                    Memory.SaveFiles.Flush();
                });
            return L.F("AIL_b8f2e6b6", $"{s.Name} está dispuesto a batirse contigo.");
        }

        private static readonly string[] PeaceTermKeys = { "they_pay", "you_pay", "they_pay_daily", "you_pay_daily", "they_cede", "you_cede" };
        // a whole number, plain or in groups of three, with nothing after it but the coin's name
        private static readonly System.Text.RegularExpressions.Regex TermAmount = new System.Text.RegularExpressions.Regex(
            @"^(\d{1,3}(?:[.,' ]\d{3})+|\d+)(?:\s*(?:denars?|denarii|gold|coins?))?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        private static readonly System.Text.RegularExpressions.Regex TermDays = new System.Text.RegularExpressions.Regex(
            @"^(\d+)(?:\s*days?)?$", System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);

        /// <summary>A sum or a count written cleanly, above zero. "200xabc", "-500", "0" and "a lot" are not one.</summary>
        private static bool TermNumber(System.Text.RegularExpressions.Regex shape, string val, out int n)
        {
            n = 0;
            var m = shape.Match((val ?? "").Trim());
            if (!m.Success) return false;
            long v = 0;
            foreach (char c in m.Groups[1].Value)
                if (c >= '0' && c <= '9') { v = v * 10 + (c - '0'); if (v > int.MaxValue) v = int.MaxValue; }
            n = (int)v;
            return n > 0;
        }

        /// <summary>
        /// The terms written after "make_peace:", turned into the fields of a ruler's propose_peace as the character
        /// would write them: what "they" (the player) give goes in the plain fields, what "you" (the character) give
        /// in the offer_ ones. null when there are none, which is a plain peace. <paramref name="bad"/> says, in
        /// English for the character, what could not be read: a term not written name=value, an unknown or repeated
        /// name, a sum or a length that is not a clean number above zero, a missing value. Sums over the caps and
        /// settlement names are settled where the treaty is drawn up (Diplomacy.DraftChatPeace).
        /// </summary>
        private static Newtonsoft.Json.Linq.JObject PeaceTermsFrom(string arg, out string bad)
        {
            bad = null;
            try
            {
                arg = (arg ?? "").Trim().Trim(';', '|').Trim();
                if (arg.Length == 0) return null;
                if (arg.IndexOf('=') < 0)
                {
                    // Loose words with no figure and no term in them ("make_peace:agreed") stay a plain peace, which
                    // the player still has to sign. Anything that looks like a price must be written properly.
                    string low = arg.ToLowerInvariant().Replace(' ', '_').Replace('-', '_');
                    if (!arg.Any(char.IsDigit) && !PeaceTermKeys.Any(k => low.Contains(k))) return null;
                    bad = "the terms must be written as name=value separated by semicolons, for example they_pay=8000;they_cede=Varcheg. For a peace with no terms write make_peace alone";
                    return null;
                }
                var j = new Newtonsoft.Json.Linq.JObject();
                var seen = new HashSet<string>();
                foreach (var raw in arg.Split(';', '|', '\n'))
                {
                    string part = raw.Trim();
                    if (part.Length == 0) continue;
                    int eq = part.IndexOf('=');
                    if (eq <= 0) { bad = $"the term «{Clip(part, 40)}» is not written as name=value"; return null; }
                    string key = part.Substring(0, eq).Trim().ToLowerInvariant().Replace(' ', '_').Replace('-', '_'), val = part.Substring(eq + 1).Trim();
                    // two terms run together with a comma would lose the second one
                    if (val.IndexOf('=') >= 0) { bad = "the terms must be separated by semicolons, one name=value each"; return null; }
                    if (Array.IndexOf(PeaceTermKeys, key) < 0)
                    {
                        bad = $"«{Clip(key, 30)}» is not a term of a peace: only they_pay, you_pay, they_pay_daily, you_pay_daily, they_cede and you_cede exist";
                        return null;
                    }
                    if (!seen.Add(key)) { bad = $"the term {key} is written twice: each term goes once, with its whole amount"; return null; }
                    if (val.Length == 0) { bad = $"the term {key} has no value"; return null; }
                    switch (key)
                    {
                        case "they_pay": case "you_pay":
                        {
                            if (!TermNumber(TermAmount, val, out int amount)) { bad = $"«{Clip(part, 40)}» is not a sum: write a whole number of denars above zero, like {key}=8000"; return null; }
                            j[key == "you_pay" ? "offer_reparations" : "reparations"] = amount;
                            break;
                        }
                        case "they_pay_daily": case "you_pay_daily":
                        {
                            int cut = val.IndexOfAny(new[] { 'x', 'X', '×', '*', '/' });
                            // no length given at all: one year of this world, and the treaty shows the days
                            int days = Math.Max(1, CampaignTime.DaysInYear);
                            if (!TermNumber(TermAmount, cut >= 0 ? val.Substring(0, cut) : val, out int daily) || (cut >= 0 && !TermNumber(TermDays, val.Substring(cut + 1), out days)))
                            { bad = $"«{Clip(part, 40)}» is not a tribute: write denars a day and days as whole numbers above zero, like {key}=200x90"; return null; }
                            string pre = key == "you_pay_daily" ? "offer_" : "";
                            j[pre + "daily_tribute"] = daily; j[pre + "tribute_days"] = days;
                            break;
                        }
                        default:
                            j[key == "you_cede" ? "offer_settlement" : "settlement"] = val;
                            break;
                    }
                }
                return j.Count > 0 ? j : null;
            }
            catch (Exception ex)
            {
                Log.Error("Condiciones de una paz hablada", ex);
                bad = "the terms could not be read";
                return null;
            }
        }

        /// <summary>
        /// A peace settled in the talk, with a price or without: the player is shown who pays whom and decides.
        /// Nothing is signed, paid or handed over without his yes.
        /// </summary>
        private static string OfferTreaty(Speaker s, Newtonsoft.Json.Linq.JObject terms)
        {
            var theirs = s.Hero.MapFaction as Kingdom;
            var p = World.Diplomacy.DraftChatPeace(theirs, PlayerKingdom, terms, out string why);
            if (p == null) return Nope(null, (why ?? "the treaty could not be drawn up") + ". Nothing was signed, paid or handed over");
            if (Repeated(s, "treaty", 1)) return null;
            LastDone[s.Key + "|treaty"] = (1, DateTime.UtcNow);
            NoteOutcome(s, $"[Pending: you have put a treaty of peace in front of them. Its terms as written in it, with your realm as the proposer and theirs as the receiver: {World.Diplomacy.ChatPeaceNote(p)}. " +
                           "Nothing is signed, paid or handed over unless they confirm it now.]");
            Ask(L.F("AIL_e7b1c776", $"Tratado de paz con {theirs.Name}"), World.Diplomacy.ChatPeaceText(p),
                L.T("AIL_e7b1c777", "Firmar"), L.T("AIL_e7b1c778", "No firmar"),
                () =>
                {
                    string done = World.Diplomacy.SignChatPeace(p, out string no, out string noForPlayer);
                    if (done == null)
                    {
                        NoteOutcome(s, $"[Did not happen: the peace was not signed, because {no}. Your realms are still at war and nothing was paid or handed over. This is not a refusal by them.]");
                        Say(noForPlayer != null ? L.F("AIL_e7b1c782", $"La paz no se ha firmado: {noForPlayer}. No se ha pagado ni entregado nada.")
                                                : L.T("AIL_e7b1c779", "La paz no se ha firmado: lo acordado ya no se puede cumplir."));
                        Memory.SaveFiles.Flush();
                        return;
                    }
                    MarkWarPeace(theirs);
                    NoteOutcome(s, $"[Done: they signed and the war between your realms is over. The terms, with your realm as the proposer and theirs as the receiver: {World.Diplomacy.ChatPeaceNote(p)}. " +
                                   "All of it has been paid and handed over already: do not use make_peace, take_gold or give_gold for it again.]");
                    Say(done);
                    Memory.SaveFiles.Flush();
                },
                () =>
                {
                    NoteOutcome(s, "[Did not happen: they read the treaty and would not sign it. Your realms are still at war and nothing was paid or handed over.]");
                    Say(L.F("AIL_e7b1c77a", $"No firmas la paz con {theirs.Name}."));
                    Memory.SaveFiles.Flush();
                });
            return L.F("AIL_e7b1c77b", $"{s.Name} te pone delante un tratado de paz.");
        }

        // War and peace decided in a chat: a few days must pass between one and the other with the same kingdom
        private const double WarPeaceCooldownDays = 7;
        private static string WarPeaceKey(IFaction f) => f == null || PlayerKingdom == null ? null : string.CompareOrdinal(f.StringId, PlayerKingdom.StringId) < 0 ? f.StringId + "|" + PlayerKingdom.StringId : PlayerKingdom.StringId + "|" + f.StringId;
        private static bool WarPeaceSettled(IFaction f)
        {
            try { var k = WarPeaceKey(f); return k == null || WorldSim.State == null || !WorldSim.State.WarPeaceDay.TryGetValue(k, out double d) || CampaignTime.Now.ToDays - d >= WarPeaceCooldownDays; }
            catch { return true; }
        }
        private static void MarkWarPeace(IFaction f) { try { var k = WarPeaceKey(f); if (k != null && WorldSim.State != null) WorldSim.State.WarPeaceDay[k] = CampaignTime.Now.ToDays; } catch { } }

        /// <summary>Same side (same clan, kingdom or faction). You don't keep one of your own as a prisoner.</summary>
        private static bool SameSide(Hero a, Hero b)
        {
            try { return a != null && b != null && (a == b || (a.Clan != null && a.Clan == b.Clan) || (a.MapFaction != null && a.MapFaction == b.MapFaction)); }
            catch { return false; }
        }

        /// <summary>Big actions become news (the game doesn't always announce them).</summary>
        private static void RecordAsNews(Speaker s, string action)
        {
            if (!s.IsHero) return;
            // Check in the world that it really happened
            try
            {
                bool happened = action == "join_party" ? s.Hero.CompanionOf == Clan.PlayerClan
                              : action == "set_free" ? !s.Hero.IsPrisoner
                              : action == "accept_marriage" ? P.Spouse == s.Hero
                              : true;
                if (!happened) { Log.Warn($"Noticia de {action} con {s.Name} descartada: no ha ocurrido"); return; }
            }
            catch { return; }
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

        /// <summary>
        /// Only the FIRST number in the text ("100 now and 100 later" -> 100, "200-300" -> 200).
        /// Accepts thousands separators ("1.000", "1,000", "1 000" -> 1000) and "5k" -> 5000. Decimals are dropped ("2.5" -> 2).
        /// The parsing lives in FactCheck so that it can be tested outside the game.
        /// </summary>
        internal static int ParseAmount(string arg) => FactCheck.ParseAmount(arg);
    }
}
