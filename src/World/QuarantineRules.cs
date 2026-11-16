using System;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using Helpers;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Encounters;
using TaleWorlds.CampaignSystem.GameMenus;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Core;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>
    /// Quarantine that actually closes the gates. While it lasts, map parties don't pick the place as a destination,
    /// caravans and villagers stay away and no tournaments are called. The player who comes to a town finds a menu
    /// with only "Leave"; at a castle the guard sergeant turns them back. Inside a quarantined town or castle the
    /// streets, arena, market, tavern, back streets, port, prison and garrison are closed, and whoever is ill cannot
    /// leave. The owner, the ruler of the owner's realm and enemies at war are not stopped at the gate.
    /// </summary>
    internal static class QuarantineRules
    {
        // The gate menu keeps the id of the old one, so a save made in it still finds it
        private const string GateMenu = "ailegacy_quarantine_gate";
        private const string CastleMenu = "ailegacy_quarantine_castle_denied";
        private static string _passFor;       // settlement the player was inside of when it closed

        // The game's menu options a quarantine closes for anyone inside, and the ones that leave a settlement
        private static readonly string[] Closes =
        {
            "town_streets", "town_arena", "trade", "town_backstreet", "town_tavern", "town_port", "go_to_port", "visit_port", "take_a_walk_around_the_castle", "castle_prison",
            "castle_prison_cheat", "manage_garrison", "manage_production", "open_stash", "leave_troops_to_garrison", "castle_return_to_army",
        };
        private static readonly string[] Leaves = { "town_leave", "castle_leave", "leave" };

        public static bool Closed(Settlement st)
        {
            try { return st != null && Diseases.Enabled && S.Get.QuarantineClosesGates && Diseases.IsQuarantined(st); }
            catch { return false; }
        }

        private static bool AnyClosed()
        {
            try { return Diseases.Enabled && S.Get.QuarantineClosesGates && Diseases.AnyQuarantine; }
            catch { return false; }
        }

        /// <summary>The owner, the ruler of the owner's realm, and whoever comes at war, are let through the gate.</summary>
        public static bool PlayerExempt(Settlement st)
        {
            if (st == null) return true;
            if (st.OwnerClan == Clan.PlayerClan) return true;
            var me = Hero.MainHero;
            var realm = st.OwnerClan?.Kingdom;
            if (me != null && me.IsKingdomLeader && realm != null && realm == me.Clan?.Kingdom) return true;
            if (MobileParty.MainParty?.BesiegedSettlement == st) return true;
            var pf = me?.MapFaction;
            return pf != null && st.MapFaction != null && pf.IsAtWarWith(st.MapFaction);      // the game already keeps enemies out
        }

        private static bool PlayerIll()
        {
            try { return Hero.MainHero != null && Diseases.OfHero(Hero.MainHero) != null; } catch { return false; }
        }

        /// <summary>
        /// The player is turned away at the gate. Someone who was already inside when it closed and has fallen ill stays
        /// in: they may not leave, and turning them out to the gate menu would leave them no way to wait it out.
        /// </summary>
        public static bool StopsPlayer(Settlement st)
        {
            try { return Closed(st) && !PlayerExempt(st) && !(_passFor == st.StringId && PlayerIll()); } catch { return false; }
        }

        /// <summary>The player is ill inside a quarantined place that is not their clan's: the ways out are shut.</summary>
        public static bool ExitBlocked()
        {
            try
            {
                var st = Settlement.CurrentSettlement;
                bool held = st != null && st.OwnerClan != Clan.PlayerClan && Closed(st) && PlayerIll();
                if (!held) { _heldSince = -1; _heldIn = null; return false; }
                // never for ever: a quarantine "until lifted" that nobody lifts, or an illness that will not pass, lets
                // the player go after a fortnight at most
                double now = CampaignTime.Now.ToDays;
                if (_heldIn != st.StringId || _heldSince < 0) { _heldIn = st.StringId; _heldSince = now; }
                return now - _heldSince < MaxHeldDays;
            }
            catch { return false; }
        }
        private const double MaxHeldDays = 15;
        private static double _heldSince = -1;
        private static string _heldIn;

        /// <summary>Inside a quarantined town or castle, whoever owns it: the closed options stay closed.</summary>
        public static bool InsideClosed()
        {
            try
            {
                var st = Settlement.CurrentSettlement;
                return st != null && (st.IsTown || st.IsCastle) && Closed(st);
            }
            catch { return false; }
        }

        /// <summary>For the game's menu options (Patches\DiseaseExtraPatch.cs): grey out what the quarantine closes. Null when it stays as it is.</summary>
        public static string GreyOut(string optionId, bool isLeave)
        {
            if (string.IsNullOrEmpty(optionId) || !AnyClosed()) return null;
            if (isLeave) return Leaves.Contains(optionId) && ExitBlocked() ? L.T("AIL_e7b1d70e", "Estás infectado y no puedes salir del asentamiento en cuarentena.") : null;
            return Closes.Contains(optionId) && InsideClosed() ? L.T("AIL_e7b1d70d", "Cerrado por cuarentena.") : null;
        }

        public static bool Barred(MobileParty party, Settlement st)
        {
            try
            {
                if (party == null || st == null || party.IsMainParty || !Closed(st)) return false;
                if (party.CurrentSettlement == st || party.IsGarrison || party.IsMilitia) return false;
                if (party.HomeSettlement == st) return false;                                  // going home
                var f = party.MapFaction;
                if (f != null && st.MapFaction != null && f.IsAtWarWith(st.MapFaction)) return false;   // coming to besiege or raid
                var clan = party.LeaderHero?.Clan ?? party.ActualClan;
                if (clan != null && clan == st.OwnerClan) return false;                        // owners can enter their own place
                return true;
            }
            catch { return false; }
        }

        /// <summary>Runs after the game's party AI scoring and zeroes the score for going to a quarantined place.</summary>
        public static void OnAiHourlyTick(MobileParty party, PartyThinkParams p)
        {
            try
            {
                if (party == null || p == null || party.IsMainParty || !AnyClosed()) return;
                foreach (var entry in p.AIBehaviorScores.ToList())
                {
                    var data = entry.Item1;
                    if (entry.Item2 <= 0f || data.AiBehavior != AiBehavior.GoToSettlement) continue;
                    if (data.Party is Settlement st && Barred(party, st)) p.SetBehaviorScore(in data, 0f);
                }
            }
            catch (Exception ex) { Log.Error("[quarantine] map AI", ex); }
        }

        /// <summary>The quarantine starts while the player is inside, or a save loads inside: noted for StopsPlayer.</summary>
        public static void NotePlayerInside()
        {
            try { _passFor = Settlement.CurrentSettlement?.StringId; } catch { _passFor = null; }
        }

        public static void OnSettlementLeft(MobileParty party, Settlement st)
        {
            if (party != null && party.IsMainParty) _passFor = null;
        }

        private static Settlement Here => PlayerEncounter.LocationEncounter?.Settlement ?? Settlement.CurrentSettlement ?? PlayerEncounter.EncounterSettlement;

        /// <summary>The town's menus give way to the closed gate, the castle's to the guard sergeant who turns the player back.</summary>
        public static void OnGameMenuOpened(MenuCallbackArgs args)
        {
            try
            {
                string id = args?.MenuContext?.GameMenu?.StringId;
                bool town = id == "town" || id == "town_outside" || id == "town_outside_ended";
                if (!town && id != "castle") return;
                var st = Here;
                if (st == null || (town ? !st.IsTown : !st.IsCastle) || !StopsPlayer(st)) return;
                GameMenu.SwitchToMenu(town ? GateMenu : CastleMenu);
            }
            catch (Exception ex) { Log.Error("[quarantine] gate", ex); }
        }

        /// <summary>The castle guard turns away a request to enter (from the game's "Request entry to the castle").</summary>
        public static bool DenyCastleEntry()
        {
            try
            {
                var st = Settlement.CurrentSettlement;
                if (st == null || !st.IsCastle || !StopsPlayer(st)) return false;
                GameMenu.SwitchToMenu(CastleMenu);
                return true;
            }
            catch (Exception ex) { Log.Error("[quarantine] castle request", ex); return false; }
        }

        private static void GateInit(MenuCallbackArgs args)
        {
            var st = Here;
            try { if (st?.Town != null) args.MenuContext.SetBackgroundMeshName(st.Town.WaitMeshName); } catch { }
            string text = st == null
                ? L.T("AIL_e7b1d70a", "Este asentamiento está cerrado por cuarentena. No se te permite entrar.")
                : L.F("AIL_e7b1d709", $"{st.Name} está cerrada por cuarentena. No se te permite entrar.");
            MBTextManager.SetTextVariable("AIL_QGATE", BraceSafe(text));
        }

        private static void CastleInit(MenuCallbackArgs args)
        {
            try { args.MenuContext.SetBackgroundMeshName("encounter_guards"); } catch { }
            var st = Settlement.CurrentSettlement;
            TextObject text;
            if (st != null)
            {
                text = new TextObject("{=!}" + L.T("AIL_e7b1d70b", "El sargento de la guardia levanta la mano antes de que llegues a la puerta. «{?PLAYER.GENDER}Mi señora{?}Mi señor{\\?}, {SETTLEMENT_NAME} está en cuarentena por orden de {LORD.LINK}. Nadie entra ni sale hasta que se levante la cuarentena. Debo pediros que deis media vuelta.»"));
                text.SetTextVariable("SETTLEMENT_NAME", st.EncyclopediaLinkWithName);
                try { if (Hero.MainHero != null) StringHelpers.SetCharacterProperties("PLAYER", Hero.MainHero.CharacterObject, text); } catch { }
                try { var lord = st.OwnerClan?.Leader; if (lord != null) StringHelpers.SetCharacterProperties("LORD", lord.CharacterObject, text); } catch { }
            }
            else text = new TextObject("{=!}" + L.T("AIL_e7b1d70c", "La guardia te cierra el paso. Este castillo está en cuarentena. No se te permite entrar."));
            MBTextManager.SetTextVariable("AIL_QCASTLE", text);
        }

        /// <summary>Braces in a settlement's name would be read as text variables.</summary>
        private static string BraceSafe(string s) => (s ?? "").Replace("{", "(").Replace("}", ")");

        public static void AddMenus(CampaignGameStarter starter)
        {
            starter.AddGameMenu(GateMenu, "{=!}{AIL_QGATE}", GateInit, GameMenu.MenuOverlayType.SettlementWithBoth);
            starter.AddGameMenuOption(GateMenu, "ailegacy_qgate_leave", L.T("AIL_e7b1d710", "{=!}Irse"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args =>
                {
                    try
                    {
                        var party = MobileParty.MainParty;
                        if (party?.CurrentSettlement != null)
                        {
                            try { party.Position = party.CurrentSettlement.GatePosition; } catch { }
                            PlayerEncounter.LeaveSettlement();
                        }
                        PlayerEncounter.Finish();
                        try { Campaign.Current.SaveHandler.SignalAutoSave(); } catch { }
                    }
                    catch (Exception ex)
                    {
                        Log.Error("[quarantine] leaving the gate", ex);
                        // whatever failed, the player must not stay on a menu with no way out
                        try { if (PlayerEncounter.Current != null) PlayerEncounter.Finish(); } catch { }
                        try { GameMenu.ExitToLast(); } catch { }
                    }
                }, true, -1);

            starter.AddGameMenu(CastleMenu, "{=!}{AIL_QCASTLE}", CastleInit);
            starter.AddGameMenuOption(CastleMenu, "ailegacy_qcastle_back", "{=veWOovVv}Continue...",
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args => GameMenu.SwitchToMenu("castle_outside"), false, -1);
        }
    }
}
