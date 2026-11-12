using System;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
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
    /// Una cuarentena que cierra de verdad: mientras dura, las partidas del mapa no eligen ese sitio como destino, las
    /// caravanas y los aldeanos no van, no se convocan torneos, y a ti te paran en la puerta (te vuelves, o sobornas a la
    /// guardia y entras por tu cuenta y riesgo). No afecta a quien es dueño del lugar, ni a un enemigo que venga a tomarlo.
    /// </summary>
    internal static class QuarantineRules
    {
        private const string GateMenu = "ailegacy_quarantine_gate";
        private static string _passFor;       // asentamiento en el que ya estás dentro con permiso (o desde antes del cierre)
        private static string _backMenu;      // menú del juego al que se vuelve tras pasar

        public static bool Closed(Settlement st)
        {
            try { return st != null && Diseases.Enabled && S.Get.QuarantineClosesGates && Diseases.At(st)?.Quarantined == true; }
            catch { return false; }
        }

        private static bool AnyClosed()
        {
            try { return Diseases.Enabled && S.Get.QuarantineClosesGates && WorldSim.State != null && WorldSim.State.Outbreaks.Any(o => o.Quarantined); }
            catch { return false; }
        }

        private static bool PlayerExempt(Settlement st)
        {
            if (st.OwnerClan == Clan.PlayerClan) return true;
            if (st.MapFaction is Kingdom k && k.Leader == Hero.MainHero) return true;
            var pf = Hero.MainHero?.MapFaction;
            return pf != null && st.MapFaction != null && pf.IsAtWarWith(st.MapFaction);      // a un enemigo ya le para el juego
        }

        /// <summary>¿A ti te pararían hoy en la puerta de ese sitio?</summary>
        public static bool StopsPlayer(Settlement st)
        {
            try { return Closed(st) && !PlayerExempt(st) && _passFor != st.StringId; } catch { return false; }
        }

        /// <summary>¿Esa partida del mapa tiene vetado ir a ese sitio?</summary>
        public static bool Barred(MobileParty party, Settlement st)
        {
            try
            {
                if (party == null || st == null || party.IsMainParty || !Closed(st)) return false;
                if (party.CurrentSettlement == st || party.IsGarrison || party.IsMilitia) return false;
                if (party.HomeSettlement == st) return false;                                  // vuelve a su casa
                var f = party.MapFaction;
                if (f != null && st.MapFaction != null && f.IsAtWarWith(st.MapFaction)) return false;   // viene a sitiar o saquear
                var clan = party.LeaderHero?.Clan ?? party.ActualClan;
                if (clan != null && clan == st.OwnerClan) return false;                        // el dueño entra en lo suyo
                return true;
            }
            catch { return false; }
        }

        /// <summary>Tras la cuenta de la IA del juego: ir a un sitio en cuarentena deja de puntuar.</summary>
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
            catch (Exception ex) { Log.Error("[cuarentena] IA del mapa", ex); }
        }

        /// <summary>Si la cuarentena se decreta contigo dentro, o cargas partida dentro, no te echan.</summary>
        public static void NotePlayerInside()
        {
            try { _passFor = Settlement.CurrentSettlement?.StringId; } catch { _passFor = null; }
        }

        public static void OnSettlementLeft(MobileParty party, Settlement st)
        {
            if (party != null && party.IsMainParty) _passFor = null;
        }

        public static void OnGameMenuOpened(MenuCallbackArgs args)
        {
            try
            {
                string id = args?.MenuContext?.GameMenu?.StringId;
                if (id != "town" && id != "castle" && id != "village" && id != "town_outside" && id != "castle_outside") return;
                var st = Settlement.CurrentSettlement ?? PlayerEncounter.EncounterSettlement;
                if (st == null || !StopsPlayer(st)) return;
                _backMenu = id;
                GameMenu.SwitchToMenu(GateMenu);
            }
            catch (Exception ex) { Log.Error("[cuarentena] puerta", ex); }
        }

        private static Settlement Here => Settlement.CurrentSettlement ?? PlayerEncounter.EncounterSettlement;

        private static int BribeCost(Settlement st)
        {
            int b = S.Get.QuarantineBribeGold;
            var o = Diseases.At(st);
            return b <= 0 || o == null ? 0 : b * Math.Max(1, o.Severity);
        }

        private static string GateText()
        {
            var st = Here; var o = Diseases.At(st);
            if (st == null || o == null) return "";
            int left = (int)Math.Max(1, Math.Ceiling(o.QuarantineUntilDay - CampaignTime.Now.ToDays));
            return L.F("AIL_b7d2f001", $"{st.Name} está en cuarentena por {o.Name}. La guardia tiene las puertas cerradas y no deja pasar a nadie de fuera. Quedan unos {left} días.");
        }

        public static void AddMenus(CampaignGameStarter starter)
        {
            starter.AddGameMenu(GateMenu, "{=!}{AIL_QGATE}", args => MBTextManager.SetTextVariable("AIL_QGATE", GateText()));

            starter.AddGameMenuOption(GateMenu, "ailegacy_qgate_bribe", L.T("AIL_b7d2f002", "{=!}Sobornar a la guardia de la puerta ({AIL_QBRIBE} denarios)"),
                args =>
                {
                    args.optionLeaveType = GameMenuOption.LeaveType.Bribe;
                    int cost = BribeCost(Here);
                    if (cost <= 0) return false;
                    MBTextManager.SetTextVariable("AIL_QBRIBE", cost);
                    if (Hero.MainHero.Gold < cost)
                    {
                        args.IsEnabled = false;
                        args.Tooltip = new TextObject("{=!}" + L.T("AIL_b7d2f003", "No llevas tanto oro."));
                    }
                    return true;
                },
                args =>
                {
                    var st = Here;
                    int cost = BribeCost(st);
                    if (st == null || Hero.MainHero.Gold < cost) return;
                    GiveGoldAction.ApplyForCharacterToSettlement(Hero.MainHero, st, cost);
                    _passFor = st.StringId;
                    WorldSim.Say(L.F("AIL_b7d2f004", $"La guardia mira hacia otro lado. Entras en {st.Name} por tu cuenta y riesgo."), Colors.Yellow);
                    Diseases.SickenAtGate(st);
                    GameMenu.SwitchToMenu(string.IsNullOrEmpty(_backMenu) ? (st.IsTown ? "town" : st.IsCastle ? "castle" : "village") : _backMenu);
                }, false, -1);

            starter.AddGameMenuOption(GateMenu, "ailegacy_qgate_leave", L.T("AIL_b7d2f005", "{=!}Dar media vuelta"),
                args => { args.optionLeaveType = GameMenuOption.LeaveType.Leave; return true; },
                args =>
                {
                    try
                    {
                        if (MobileParty.MainParty.CurrentSettlement != null) PlayerEncounter.LeaveSettlement();
                        PlayerEncounter.Finish();
                    }
                    catch (Exception ex) { Log.Error("[cuarentena] salir de la puerta", ex); }
                }, true, -1);
        }
    }
}
