using System;
using System.Collections.Generic;
using System.Linq;
using AILegacy.Core;
using AILegacy.Settings;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Actions;
using TaleWorlds.CampaignSystem.MapEvents;
using TaleWorlds.CampaignSystem.Party;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.CampaignSystem.Siege;

namespace AILegacy.World
{
    /// <summary>
    /// Convierte lo que pasa de verdad en la campaña en noticias.
    /// Text (inglés) va a los prompts; TitleEs/TextEs es lo que ve el jugador.
    /// </summary>
    internal static class NewsTracker
    {
        public static void Register(object owner)
        {
            CampaignEvents.WarDeclared.AddNonSerializedListener(owner, OnWarDeclared);
            CampaignEvents.MakePeace.AddNonSerializedListener(owner, OnPeace);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(owner, OnSettlementOwnerChanged);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(owner, OnHeroKilled);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(owner, OnMapEventEnded);
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(owner, OnKingdomDestroyed);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(owner, OnClanChangedKingdom);
            CampaignEvents.RulingClanChanged.AddNonSerializedListener(owner, OnRulingClanChanged);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(owner, OnPrisonerTaken);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(owner, OnSiegeStarted);
        }

        private static bool On => S.Get.EnableNews;

        private static void Safe(string what, Action a)
        {
            if (!On) return;
            try { a(); } catch (Exception ex) { Log.Error("Noticia " + what, ex); }
        }

        private static void OnWarDeclared(IFaction f1, IFaction f2, DeclareWarAction.DeclareWarDetail detail) => Safe("guerra", () =>
        {
            if (!f1.IsKingdomFaction && !f2.IsKingdomFaction) return;
            NewsStore.Add(new NewsItem
            {
                Type = "war", Importance = (f1.IsKingdomFaction && f2.IsKingdomFaction) ? 3 : 2,
                Text = $"{f1.Name} has declared war on {f2.Name}.",
                TitleEs = L.F("AIL_b3ce76cf", $"¡Guerra entre {f1.Name} y {f2.Name}!"),
                TextEs = L.F("AIL_60575e78", $"{f1.Name} ha declarado la guerra a {f2.Name}. Los ejércitos de ambos bandos se preparan para la campaña."),
                Kingdoms = Ids(f1, f2), Heroes = HeroIds(f1.Leader, f2.Leader),
            });
        });

        private static void OnPeace(IFaction f1, IFaction f2, MakePeaceAction.MakePeaceDetail detail) => Safe("paz", () =>
        {
            if (!f1.IsKingdomFaction && !f2.IsKingdomFaction) return;
            NewsStore.Add(new NewsItem
            {
                Type = "peace", Importance = 3,
                Text = $"{f1.Name} and {f2.Name} have made peace.",
                TitleEs = L.F("AIL_397b4d4e", $"Paz entre {f1.Name} y {f2.Name}"),
                TextEs = L.F("AIL_6f600589", $"{f1.Name} y {f2.Name} han firmado la paz. Por ahora, las armas callan entre ambos reinos."),
                Kingdoms = Ids(f1, f2), Heroes = HeroIds(f1.Leader, f2.Leader),
            });
        });

        private static void OnSettlementOwnerChanged(Settlement s, bool openToClaim, Hero newOwner, Hero oldOwner, Hero capturer,
            ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail detail) => Safe("feudo", () =>
        {
            if (s == null || s.IsVillage) return;
            string kind = s.IsTown ? "town" : "castle";
            string kindEs = s.IsTown ? L.T("AIL_e3b035c2", "la ciudad") : L.T("AIL_b9583293", "el castillo");
            bool siege = detail.ToString().IndexOf("Siege", StringComparison.OrdinalIgnoreCase) >= 0;
            var n = new NewsItem
            {
                SettlementId = s.StringId,
                Kingdoms = Ids(newOwner?.MapFaction, oldOwner?.MapFaction),
                Heroes = HeroIds(newOwner, oldOwner, capturer),
            };
            if (siege)
            {
                var who = capturer ?? newOwner;
                n.Type = "capture"; n.Importance = 3;
                n.Text = $"The {kind} of {s.Name} has fallen to {who?.Name}{FactionOf(who)} after a siege" +
                         (oldOwner != null ? $"; it belonged to {oldOwner.Name}{FactionOf(oldOwner)}." : ".");
                n.TitleEs = L.F("AIL_3e887326", $"Cae {s.Name}");
                n.TextEs = L.F("AIL_2a6395bc", $"{Cap(kindEs)} de {s.Name} ha caído tras un asedio en manos de {who?.Name}{DeEs(who)}") +
                           (oldOwner != null ? L.F("AIL_06be773b", $". Antes pertenecía a {oldOwner.Name}{DeEs(oldOwner)}.") : ".");
            }
            else
            {
                n.Type = "fief"; n.Importance = 2;
                n.Text = $"The {kind} of {s.Name} now belongs to {newOwner?.Name}{FactionOf(newOwner)}" +
                         (oldOwner != null && oldOwner != newOwner ? $" (before, {oldOwner.Name})." : ".");
                n.TitleEs = L.F("AIL_946c5eaa", $"{s.Name} cambia de señor");
                n.TextEs = L.F("AIL_114e50bb", $"{Cap(kindEs)} de {s.Name} pertenece ahora a {newOwner?.Name}{DeEs(newOwner)}") +
                           (oldOwner != null && oldOwner != newOwner ? L.F("AIL_5b7a333a", $" (antes era de {oldOwner.Name}).") : ".");
            }
            NewsStore.Add(n);
        });

        private static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification) => Safe("muerte", () =>
        {
            if (victim == null || !(victim.IsLord || victim.IsNotable || victim.IsPlayerCompanion)) return;
            string how, howEs;
            switch (detail.ToString())
            {
                case "DiedOfOldAge": how = "died of old age"; howEs = L.T("AIL_feb8d02f", "ha muerto de vejez"); break;
                case "DiedInLabor": how = "died in childbirth"; howEs = L.T("AIL_5d51e858", "ha muerto en el parto"); break;
                case "DiedInBattle":
                    how = killer != null ? $"fell in battle against {killer.Name}" : "fell in battle";
                    howEs = killer != null ? L.F("AIL_8740b99c", $"ha caído en combate contra {killer.Name}") : L.T("AIL_7fd0d7e5", "ha caído en combate"); break;
                case "Executed":
                    how = killer != null ? $"was executed by {killer.Name}" : "was executed";
                    howEs = killer != null ? L.F("AIL_923a5224", $"ha sido ejecutado por {killer.Name}") : L.T("AIL_93c01ba7", "ha sido ejecutado"); break;
                case "Murdered": how = "was murdered"; howEs = L.T("AIL_7c907518", "ha sido asesinado"); break;
                case "Lost": how = "has disappeared and is presumed dead"; howEs = L.T("AIL_6c0ce598", "ha desaparecido y se le da por muerto"); break;
                default: how = "has died"; howEs = L.T("AIL_00ec5af6", "ha muerto"); break;
            }
            int imp = victim.IsKingdomLeader ? 4 : victim.IsClanLeader ? 3 : victim.IsLord ? 2 : 1;
            NewsStore.Add(new NewsItem
            {
                Type = "death", Importance = imp,
                Text = $"{victim.Name}{TitleOf(victim)} {how}.",
                TitleEs = L.F("AIL_65ad4195", $"Muere {victim.Name}"),
                TextEs = $"{victim.Name}{TitleOfEs(victim)} {howEs}.",
                Kingdoms = Ids(victim.MapFaction), Heroes = HeroIds(victim, killer),
                SettlementId = (victim.CurrentSettlement ?? victim.HomeSettlement)?.StringId,
            });
        });

        private static void OnMapEventEnded(MapEvent e) => Safe("batalla", () =>
        {
            if (e == null || !e.HasWinner) return;
            var winner = e.Winner;
            var loser = winner == e.AttackerSide ? e.DefenderSide : e.AttackerSide;
            var wl = winner?.LeaderParty?.LeaderHero;
            var ll = loser?.LeaderParty?.LeaderHero;
            if (wl == null || ll == null) return;
            if (winner.MapFaction == null || loser.MapFaction == null) return;
            if (winner.MapFaction.IsBanditFaction || loser.MapFaction.IsBanditFaction) return;
            bool player = e.IsPlayerMapEvent;
            if (!player && !(wl.IsLord && ll.IsLord)) return;

            var place = e.MapEventSettlement;
            var n = new NewsItem { Kingdoms = Ids(winner.MapFaction, loser.MapFaction), Heroes = HeroIds(wl, ll), SettlementId = place?.StringId };
            if (e.IsRaid && place != null)
            {
                var raider = e.AttackerSide == winner ? wl : ll;
                n.Type = "raid"; n.Importance = 2;
                n.Text = $"{raider.Name}{FactionOf(raider)} raided the village of {place.Name}.";
                n.TitleEs = L.F("AIL_1230749f", $"Saquean {place.Name}");
                n.TextEs = L.F("AIL_a0a85eed", $"{raider.Name}{DeEs(raider)} ha saqueado la aldea de {place.Name}. Los campesinos han perdido cosechas y ganado.");
            }
            else if (e.IsSiegeAssault && place != null)
            {
                return; // ya lo cuenta el cambio de dueño
            }
            else
            {
                n.Type = "battle"; n.Importance = (wl.IsKingdomLeader || ll.IsKingdomLeader) ? 3 : 2;
                n.Text = $"{wl.Name}{FactionOf(wl)} defeated {ll.Name}{FactionOf(ll)} in battle" + (place != null ? $" near {place.Name}." : ".");
                n.TitleEs = L.F("AIL_161727d1", $"Victoria de {wl.Name}");
                n.TextEs = L.F("AIL_b4431c37", $"{wl.Name}{DeEs(wl)} ha derrotado en batalla a {ll.Name}{DeEs(ll)}") + (place != null ? L.F("AIL_2d23d9be", $" cerca de {place.Name}.") : ".");
            }
            if (player) { n.PlayerInvolved = true; if (!n.Heroes.Contains(Hero.MainHero.StringId)) n.Heroes.Add(Hero.MainHero.StringId); }
            NewsStore.Add(n);
        });

        private static void OnKingdomDestroyed(Kingdom k) => Safe("reino", () =>
        {
            NewsStore.Add(new NewsItem
            {
                Type = "kingdom", Importance = 4, Kingdoms = Ids(k),
                Text = $"The realm of {k.Name} has been destroyed; it is no more.",
                TitleEs = L.F("AIL_1d3c2297", $"Cae el reino de {k.Name}"), TextEs = L.F("AIL_c7fbb570", $"El reino de {k.Name} ha sido destruido. Ya no existe."),
            });
        });

        private static void OnClanChangedKingdom(Clan clan, Kingdom oldK, Kingdom newK, ChangeKingdomAction.ChangeKingdomActionDetail detail, bool show) => Safe("clan", () =>
        {
            if (clan == null || clan.IsBanditFaction) return;
            string d = detail.ToString();
            bool merc = d.IndexOf("Mercenary", StringComparison.OrdinalIgnoreCase) >= 0;
            bool rebel = d.IndexOf("Rebellion", StringComparison.OrdinalIgnoreCase) >= 0;
            string text, title, es;
            if (newK != null && oldK != null)
            {
                text = $"Clan {clan.Name} has left {oldK.Name} and sworn to {newK.Name}" + (merc ? " as mercenaries." : ".");
                title = L.F("AIL_266f75ee", $"El clan {clan.Name} cambia de bando");
                es = L.F("AIL_56bb73a9", $"El clan {clan.Name} ha abandonado {oldK.Name} y ahora sirve a {newK.Name}") + (merc ? L.T("AIL_ce9503db", " como mercenarios.") : ".");
            }
            else if (newK != null)
            {
                text = $"Clan {clan.Name} has joined {newK.Name}" + (merc ? " as mercenaries." : ".");
                title = L.F("AIL_5b4803a4", $"El clan {clan.Name} se une a {newK.Name}");
                es = L.F("AIL_c1c7d32a", $"El clan {clan.Name} se ha unido a {newK.Name}") + (merc ? L.T("AIL_ce9503db", " como mercenarios.") : ".");
            }
            else if (oldK != null)
            {
                text = rebel ? $"Clan {clan.Name} has rebelled against {oldK.Name}!" : $"Clan {clan.Name} has left {oldK.Name}" + (merc ? " (their mercenary contract ended)." : ".");
                title = rebel ? L.F("AIL_648fc689", $"¡Rebelión del clan {clan.Name}!") : L.F("AIL_7d9b4b55", $"El clan {clan.Name} abandona {oldK.Name}");
                es = rebel ? L.F("AIL_5ffce42d", $"El clan {clan.Name} se ha rebelado contra {oldK.Name}.") : L.F("AIL_70c30ed9", $"El clan {clan.Name} ha abandonado {oldK.Name}") + (merc ? L.T("AIL_84e6eeec", " (terminó su contrato de mercenarios).") : ".");
            }
            else return;
            NewsStore.Add(new NewsItem
            {
                Type = "clan", Importance = merc ? 1 : (rebel ? 3 : 2),
                Text = text, TitleEs = title, TextEs = es, Kingdoms = Ids(oldK, newK), Heroes = HeroIds(clan.Leader),
            });
        });

        private static void OnRulingClanChanged(Kingdom k, Clan c) => Safe("rey", () =>
        {
            if (k == null || c?.Leader == null) return;
            NewsStore.Add(new NewsItem
            {
                Type = "ruler", Importance = 4, Kingdoms = Ids(k), Heroes = HeroIds(c.Leader),
                Text = $"{c.Leader.Name} of clan {c.Name} is now the ruler of {k.Name}.",
                TitleEs = L.F("AIL_6b587fde", $"Nuevo soberano en {k.Name}"),
                TextEs = L.F("AIL_50ac9cd1", $"{c.Leader.Name}, del clan {c.Name}, es ahora quien gobierna {k.Name}."),
            });
        });

        private static void OnPrisonerTaken(PartyBase captor, Hero prisoner) => Safe("prisionero", () =>
        {
            if (prisoner == null || !prisoner.IsLord) return;
            var by = captor?.LeaderHero;
            NewsStore.Add(new NewsItem
            {
                Type = "prisoner", Importance = prisoner.IsKingdomLeader ? 4 : prisoner.IsClanLeader ? 3 : 2,
                Text = $"{prisoner.Name}{TitleOf(prisoner)} has been taken prisoner" + (by != null ? $" by {by.Name}{FactionOf(by)}." : "."),
                TitleEs = L.F("AIL_0b611224", $"{prisoner.Name}, capturado"),
                TextEs = L.F("AIL_c704d473", $"{prisoner.Name}{TitleOfEs(prisoner)} ha sido hecho prisionero") + (by != null ? L.F("AIL_c6442751", $" por {by.Name}{DeEs(by)}.") : "."),
                Kingdoms = Ids(prisoner.MapFaction, captor?.MapFaction), Heroes = HeroIds(prisoner, by),
            });
        });

        private static void OnSiegeStarted(SiegeEvent siege) => Safe("asedio", () =>
        {
            var s = siege?.BesiegedSettlement;
            var leader = siege?.BesiegerCamp?.LeaderParty?.LeaderHero;
            if (s == null) return;
            NewsStore.Add(new NewsItem
            {
                Type = "siege", Importance = 2,
                Text = $"{(leader != null ? leader.Name + FactionOf(leader) : "An enemy army")} has laid siege to {s.Name}.",
                TitleEs = L.F("AIL_ef0295b5", $"Asedio a {s.Name}"),
                TextEs = L.F("AIL_8f57f4c0", $"{(leader != null ? leader.Name + DeEs(leader) : L.T("AIL_a2104812", "Un ejército enemigo"))} ha puesto sitio a {s.Name}."),
                SettlementId = s.StringId, Kingdoms = Ids(s.MapFaction, leader?.MapFaction), Heroes = HeroIds(leader, s.OwnerClan?.Leader),
            });
        });

        // ------------------------------------------------------------------

        private static List<string> Ids(params IFaction[] fs) => fs.Where(f => f != null).Select(f => f.StringId).Distinct().ToList();
        private static List<string> HeroIds(params Hero[] hs) => hs.Where(h => h != null).Select(h => h.StringId).Distinct().ToList();

        private static string FactionOf(Hero h) => h?.MapFaction != null && h.MapFaction.IsKingdomFaction ? $" of {h.MapFaction.Name}" : "";
        private static string DeEs(Hero h) => h?.MapFaction != null && h.MapFaction.IsKingdomFaction ? $" ({h.MapFaction.Name})" : "";
        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        private static string TitleOf(Hero h)
        {
            if (h == null) return "";
            if (h.IsKingdomLeader && h.MapFaction != null) return $", ruler of {h.MapFaction.Name},";
            if (h.IsClanLeader && h.Clan != null) return $", head of clan {h.Clan.Name},";
            return h.Clan != null && h.IsLord ? $" of clan {h.Clan.Name}" : "";
        }

        private static string TitleOfEs(Hero h)
        {
            if (h == null) return "";
            if (h.IsKingdomLeader && h.MapFaction != null) return L.F("AIL_914cbfeb", $", soberano de {h.MapFaction.Name},");
            if (h.IsClanLeader && h.Clan != null) return L.F("AIL_7282058a", $", jefe del clan {h.Clan.Name},");
            return h.Clan != null && h.IsLord ? L.F("AIL_a4990140", $", del clan {h.Clan.Name},") : "";
        }
    }
}
