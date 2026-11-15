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
    /// Turns real campaign events into news. Text (English) goes into prompts, TitleEs/TextEs is what the player sees.
    /// </summary>
    internal static class NewsTracker
    {
        public static void Register(object owner)
        {
            Live = false;
            CampaignEvents.WarDeclared.AddNonSerializedListener(owner, OnWarDeclared);
            CampaignEvents.MakePeace.AddNonSerializedListener(owner, OnPeace);
            CampaignEvents.OnSettlementOwnerChangedEvent.AddNonSerializedListener(owner, OnSettlementOwnerChanged);
            CampaignEvents.HeroKilledEvent.AddNonSerializedListener(owner, OnHeroKilled);
            CampaignEvents.BeforeHeroesMarried.AddNonSerializedListener(owner, OnMarried);
            CampaignEvents.TournamentFinished.AddNonSerializedListener(owner, OnTournamentFinished);
            CampaignEvents.MapEventEnded.AddNonSerializedListener(owner, OnMapEventEnded);
            CampaignEvents.KingdomDestroyedEvent.AddNonSerializedListener(owner, OnKingdomDestroyed);
            CampaignEvents.OnClanChangedKingdomEvent.AddNonSerializedListener(owner, OnClanChangedKingdom);
            CampaignEvents.RulingClanChanged.AddNonSerializedListener(owner, OnRulingClanChanged);
            CampaignEvents.HeroPrisonerTaken.AddNonSerializedListener(owner, OnPrisonerTaken);
            CampaignEvents.OnSiegeEventStartedEvent.AddNonSerializedListener(owner, OnSiegeStarted);
            CampaignEvents.OnSiegeEventEndedEvent.AddNonSerializedListener(owner, OnSiegeEnded);
            CampaignEvents.HourlyTickEvent.AddNonSerializedListener(owner, OnHourlyTick);
            Captures.Clear();
            SiegeEnds.Clear();
        }

        /// <summary>
        /// The campaign is actually running. While the game builds the world (or loads) it fires the same events as a real
        /// death or war, and those aren't news.
        /// </summary>
        public static bool Live;

        private static bool On => Live && S.Get.EnableNews;

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
            // "Lost" is how the game quietly removes notables, so it's only news for a lord
            if (detail.ToString() == "Lost" && !victim.IsLord) return;
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
            // the head of a mercenary or minor clan is no kingdom's loss: court talk, not something that shakes a realm
            int imp = victim.IsKingdomLeader ? 4 : victim.IsClanLeader && Facts.Subject(victim.Clan) != null ? 3 : victim.IsLord ? 2 : 1;
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
                return; // the owner change already reports it
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

        private static void OnMarried(Hero a, Hero b, bool showNotification) => Safe("boda", () =>
        {
            if (a == null || b == null || a == Hero.MainHero || b == Hero.MainHero) return;      // the player's own wedding is reported by its action
            if (!a.IsLord && !b.IsLord) return;
            int imp = a.IsKingdomLeader || b.IsKingdomLeader ? 3 : a.IsClanLeader || b.IsClanLeader ? 2 : 1;
            NewsStore.Add(new NewsItem
            {
                Type = "marriage", Importance = imp, Kingdoms = Ids(a.MapFaction, b.MapFaction), Heroes = HeroIds(a, b),
                SettlementId = (a.CurrentSettlement ?? b.CurrentSettlement ?? a.HomeSettlement)?.StringId,
                Text = $"{a.Name}{TitleOf(a)} and {b.Name}{TitleOf(b)} have married.",
                TitleEs = L.F("AIL_d2e7a4c1", $"Boda de {a.Name} y {b.Name}"),
                TextEs = L.F("AIL_d2e7a4c2", $"{a.Name}{TitleOfEs(a)} y {b.Name}{TitleOfEs(b)} se han casado."),
            });
        });

        private static void OnTournamentFinished(CharacterObject winner, TaleWorlds.Library.MBReadOnlyList<CharacterObject> participants, Town town, TaleWorlds.Core.ItemObject prize) => Safe("torneo", () =>
        {
            var h = winner?.HeroObject;
            if (h == null || town?.Settlement == null || h == Hero.MainHero) return;
            NewsStore.Add(new NewsItem
            {
                Type = "tournament", Importance = h.IsLord ? 2 : 1, Kingdoms = Ids(town.Settlement.MapFaction, h.MapFaction), Heroes = HeroIds(h),
                SettlementId = town.Settlement.StringId,
                Text = $"{h.Name}{TitleOf(h)} has won the tournament of {town.Name}.",
                TitleEs = L.F("AIL_d2e7a4c3", $"{h.Name} gana el torneo de {town.Name}"),
                TextEs = L.F("AIL_d2e7a4c4", $"{h.Name}{TitleOfEs(h)} ha ganado el torneo de {town.Name}."),
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

        // ---- prisoners: the captives of one battle make one news item, and only notable ones are news at all

        private class Capture
        {
            public Hero Prisoner, By;
            public IFaction Captor;
            public Settlement Near;
            public int Rank;
        }
        // Filled by the game's events and emptied every hour, both on the game thread
        private static readonly List<Capture> Captures = new List<Capture>();
        // A rout can take dozens of lords in one hour. Past this the least notable captives are let go of first
        private const int MaxCaptures = 200;

        /// <summary>4 = a ruler (world news), 3 = the head of a kingdom's own noble clan (kingdom news), 1 = anyone else (local).</summary>
        private static int CaptiveRank(Hero h)
        {
            if (h.IsKingdomLeader) return 4;
            var c = h.Clan;
            // the head of a mercenary or minor clan is nobody's vassal: local talk, like any ordinary noble
            return c != null && c.Leader == h && Facts.Subject(c) != null ? 3 : 1;
        }

        private static void OnPrisonerTaken(PartyBase captor, Hero prisoner) => Safe("prisionero", () =>
        {
            if (prisoner == null || !prisoner.IsLord) return;
            Settlement near = null;
            try { near = captor?.MapEvent?.MapEventSettlement ?? captor?.Settlement; } catch { }
            var by = captor?.MapFaction;
            // the game can raise the same capture twice (taken in battle, then handed to a party or a dungeon)
            if (Captures.Any(c => c.Prisoner == prisoner && c.Captor == by)) return;
            Captures.Add(new Capture { Prisoner = prisoner, By = captor?.LeaderHero, Captor = by, Near = near, Rank = CaptiveRank(prisoner) });
            if (Captures.Count > MaxCaptures)
            {
                // the oldest of the least notable goes
                int low = Captures.Min(c => c.Rank);
                Captures.RemoveAt(Captures.FindIndex(c => c.Rank == low));
            }
        });

        // Both flushes take each entry out of its list before working on it and guard it on its own, so one that
        // fails costs that one piece of news and nothing else
        private static void OnHourlyTick()
        {
            if (!On) { Captures.Clear(); SiegeEnds.Clear(); return; }
            try { FlushCaptures(); } catch (Exception ex) { Log.Error("Noticia prisioneros", ex); }
            try { FlushSieges(); } catch (Exception ex) { Log.Error("Noticia fin de asedio", ex); }
        }

        private static string Short(Hero h)
        {
            var c = h.Clan;
            if (h.IsKingdomLeader && h.MapFaction != null) return $" (ruler of {h.MapFaction.Name})";
            if (c == null) return "";
            if (c.Leader != h) return $" (clan {c.Name})";
            var s = Facts.Subject(c);
            return s != null ? $" (head of clan {c.Name} of {s.Name})" : Facts.Employer(c) != null ? $" (head of the mercenary clan {c.Name})" : $" (head of clan {c.Name}, under no crown)";
        }

        private static string ShortEs(Hero h)
        {
            var c = h.Clan;
            if (h.IsKingdomLeader && h.MapFaction != null) return L.F("AIL_e7b1c730", $" (soberano de {h.MapFaction.Name})");
            if (c == null) return "";
            if (c.Leader != h) return L.F("AIL_e7b1c733", $" (clan {c.Name})");
            return Facts.Employer(c) != null ? L.F("AIL_e7b1c732", $" (jefe del clan mercenario {c.Name})") : L.F("AIL_e7b1c731", $" (jefe del clan {c.Name})");
        }

        private static void FlushCaptures()
        {
            if (Captures.Count == 0) return;
            var all = Captures.ToList();
            Captures.Clear();
            int mode = S.Get.PrisonerNewsMode();      // 0 all, 1 notable only, 2 rulers only
            // the captives of one battle are one piece of news; the key is read entry by entry so a bad one falls out alone
            var groups = new Dictionary<string, List<Capture>>();
            foreach (var c in all)
            {
                try
                {
                    if (c?.Prisoner == null) continue;
                    string key = (c.Captor?.StringId ?? "") + "|" + (c.Near?.StringId ?? "");
                    if (!groups.TryGetValue(key, out var same)) groups[key] = same = new List<Capture>();
                    same.Add(c);
                }
                catch (Exception ex) { Log.Error("Noticia prisioneros: una captura descartada", ex); }
            }
            foreach (var g in groups.Values)
            {
                try { FlushCaptureGroup(g, mode); }
                catch (Exception ex) { Log.Error("Noticia prisioneros: un grupo descartado", ex); }
            }
        }

        private static void FlushCaptureGroup(List<Capture> g, int mode)
        {
            {
                var list = g.GroupBy(c => c.Prisoner).Select(x => x.First()).OrderByDescending(c => c.Rank).ToList();
                int top = list[0].Rank;
                var by = list.Select(c => c.By).FirstOrDefault(h => h != null);
                var captor = list[0].Captor; var near = list[0].Near;
                // what the player did or suffered is always told
                bool player = list.Any(c => c.Prisoner == Hero.MainHero || c.By == Hero.MainHero);
                if (!player && ((mode >= 2 && top < 4) || (mode == 1 && top < 3))) return;
                // who gets named: everyone, or only those the setting keeps
                int floor = player || mode == 0 ? 1 : mode == 1 ? 3 : 4;
                var named = list.Where(c => c.Rank >= floor).Take(6).ToList();
                int others = list.Count - named.Count;

                var factions = new List<IFaction> { captor };
                foreach (var c in named) factions.Add((IFaction)Facts.Subject(c.Prisoner.Clan) ?? Facts.Employer(c.Prisoner.Clan));
                var heroes = named.Select(c => c.Prisoner).ToList();
                heroes.Add(by);
                var n = new NewsItem
                {
                    Type = "prisoner", Importance = top >= 3 ? top : 1, NoReaction = top < 3,
                    Kingdoms = Ids(factions.ToArray()), Heroes = HeroIds(heroes.ToArray()), SettlementId = near?.StringId,
                };
                if (list.Count == 1)
                {
                    var p = list[0].Prisoner;
                    n.Text = $"{p.Name}{TitleOf(p)} has been taken prisoner" + (by != null ? $" by {by.Name}{FactionOf(by)}." : ".");
                    n.TitleEs = L.F("AIL_0b611224", $"{p.Name}, capturado");
                    n.TextEs = L.F("AIL_c704d473", $"{p.Name}{TitleOfEs(p)} ha sido hecho prisionero") + (by != null ? L.F("AIL_c6442751", $" por {by.Name}{DeEs(by)}.") : ".");
                }
                else
                {
                    string who = by != null ? by.Name + FactionOf(by) : captor != null ? captor.Name.ToString() : "An enemy host";
                    string whoEs = by != null ? by.Name + DeEs(by) : captor != null ? captor.Name.ToString() : L.T("AIL_a2104812", "Un ejército enemigo");
                    string names = string.Join(", ", named.Select(c => c.Prisoner.Name + Short(c.Prisoner))) + (others > 0 ? $" and {others} more" : "");
                    string namesEs = string.Join(", ", named.Select(c => c.Prisoner.Name + ShortEs(c.Prisoner))) + (others > 0 ? L.F("AIL_e7b1c72f", $" y {others} más") : "");
                    n.Text = $"{who} took {list.Count} nobles prisoner" + (near != null ? $" near {near.Name}" : "") + $": {names}.";
                    n.TitleEs = L.F("AIL_e7b1c72c", $"{list.Count} nobles capturados");
                    n.TextEs = L.F("AIL_e7b1c72d", $"{whoEs} ha hecho prisioneros a {list.Count} nobles") + (near != null ? L.F("AIL_e7b1c72e", $" cerca de {near.Name}") : "") + $": {namesEs}.";
                }
                NewsStore.Add(n);
            }
        }

        // ---- sieges: how each one ended. The fall of the place is told by the change of owner

        private class SiegeEnd
        {
            public Settlement Place;
            public IFaction Owner, Besieger;
            public Hero Leader;
            /// <summary>true = beaten in the field, false = gave it up, null = the game would not say.</summary>
            public bool? Beaten;
            public double Hour;
        }
        private static readonly List<SiegeEnd> SiegeEnds = new List<SiegeEnd>();
        private static readonly System.Reflection.FieldInfo BesiegerDefeated = HarmonyLib.AccessTools.Field(typeof(SiegeEvent), "_isBesiegerDefeated");
        private static bool _siegeFieldWarned;

        /// <summary>How the besiegers fared, from a private field of the game. null when it cannot be read (another game version).</summary>
        private static bool? WasBesiegerBeaten(SiegeEvent siege)
        {
            try { if (BesiegerDefeated != null) return (bool)BesiegerDefeated.GetValue(siege); }
            catch { }
            if (!_siegeFieldWarned)
            {
                _siegeFieldWarned = true;
                Log.Warn("Noticias: no se puede leer SiegeEvent._isBesiegerDefeated en esta versión del juego; el fin de un asedio se contará sin decir si fue roto o levantado");
            }
            return null;
        }

        private static void OnSiegeEnded(SiegeEvent siege) => Safe("fin de asedio", () =>
        {
            var s = siege?.BesiegedSettlement;
            if (s == null) return;
            SiegeEnds.RemoveAll(x => x.Place == s);      // one pending verdict per place: the latest siege
            if (SiegeEnds.Count >= MaxSiegeEnds) SiegeEnds.RemoveAt(0);
            SiegeEnds.Add(new SiegeEnd
            {
                Place = s, Owner = s.MapFaction, Besieger = siege.BesiegerCamp?.MapFaction, Leader = siege.BesiegerCamp?.LeaderParty?.LeaderHero,
                Beaten = WasBesiegerBeaten(siege), Hour = CampaignTime.Now.ToHours,
            });
        });

        /// <summary>
        /// An hour after a siege ends, when the game has settled who holds the place: if it did not change hands, the
        /// siege was either broken by force or given up.
        /// </summary>
        private static void FlushSieges()
        {
            if (SiegeEnds.Count == 0) return;
            double hour = CampaignTime.Now.ToHours;
            foreach (var e in SiegeEnds.Where(x => hour - x.Hour >= 1 || hour < x.Hour).ToList())
            {
                SiegeEnds.Remove(e);
                try { TellSiegeEnd(e); }
                catch (Exception ex) { Log.Error("Noticia fin de asedio: una descartada", ex); }
            }
        }

        private const int MaxSiegeEnds = 100;

        private static void TellSiegeEnd(SiegeEnd e)
        {
            {
                double day = CampaignTime.Now.ToDays;
                var s = e.Place;
                // The place was held for an hour and the world moved on meanwhile. Nothing is told if it is no longer
                // the settlement the game knows by that id, if it changed hands, or if it is besieged again (that
                // siege has its own news, and its own verdict when it ends).
                if (s == null || e.Owner == null || Settlement.Find(s.StringId) != s) return;
                if (s.MapFaction != e.Owner) return;
                if (s.IsUnderSiege || s.SiegeEvent != null) return;
                // it fell, or was handed over, at any moment from the end of the siege (or just before it) until now
                double since = Math.Min(e.Hour / 24.0, day) - 0.5;
                if (NewsStore.All.Any(n => n.SettlementId == s.StringId && (n.Type == "capture" || n.Type == "fief") && n.Day >= since)) return;
                string who = e.Leader != null ? e.Leader.Name + FactionOf(e.Leader) : e.Besieger != null ? "the host of " + e.Besieger.Name : "the besiegers";
                string whoEs = e.Leader != null ? e.Leader.Name + DeEs(e.Leader) : e.Besieger != null ? e.Besieger.Name.ToString() : L.T("AIL_a2104812", "Un ejército enemigo");
                var n = new NewsItem { Type = "siege", SettlementId = s.StringId, Kingdoms = Ids(e.Owner, e.Besieger), Heroes = HeroIds(e.Leader, s.OwnerClan?.Leader) };
                if (e.Beaten == null)
                {
                    // the game would not say how it ended: only what is certain is told
                    n.Importance = 2;
                    n.Text = $"The siege of {s.Name} has ended and {s.Name} remains in the hands of {e.Owner?.Name}.";
                    n.TitleEs = L.F("AIL_e7b1c74d", $"Termina el asedio de {s.Name}");
                    n.TextEs = L.F("AIL_e7b1c74e", $"El asedio de {s.Name} ha terminado y {s.Name} sigue en manos de {e.Owner?.Name}.");
                }
                else if (e.Beaten == true)
                {
                    n.Importance = 3;
                    n.Text = $"The siege of {s.Name} has been broken: {who} was beaten in the field and {s.Name} remains in the hands of {e.Owner?.Name}.";
                    n.TitleEs = L.F("AIL_e7b1c734", $"Roto el asedio de {s.Name}");
                    n.TextEs = L.F("AIL_e7b1c735", $"El asedio de {s.Name} ha sido roto por las armas: {whoEs} ha sido derrotado y {s.Name} sigue en manos de {e.Owner?.Name}.");
                }
                else
                {
                    n.Importance = 2;
                    n.Text = $"The siege of {s.Name} has been lifted: {who} gave it up and withdrew, and {s.Name} remains in the hands of {e.Owner?.Name}.";
                    n.TitleEs = L.F("AIL_e7b1c736", $"Levantado el asedio de {s.Name}");
                    n.TextEs = L.F("AIL_e7b1c737", $"El asedio de {s.Name} se ha levantado: {whoEs} ha desistido y se ha retirado, y {s.Name} sigue en manos de {e.Owner?.Name}.");
                }
                NewsStore.Add(n);
            }
        }

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

        private static List<string> Ids(params IFaction[] fs) => fs.Where(f => f != null).Select(f => f.StringId).Distinct().ToList();
        private static List<string> HeroIds(params Hero[] hs) => hs.Where(h => h != null).Select(h => h.StringId).Distinct().ToList();

        // Hero.MapFaction is the paying kingdom for a mercenary in service, so the wording comes from Facts
        private static string FactionOf(Hero h) => Facts.Allegiance(h);
        private static string DeEs(Hero h) => Facts.AllegianceLocal(h);
        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        private static string TitleOf(Hero h)
        {
            if (h == null) return "";
            if (h.IsKingdomLeader && h.MapFaction != null) return $", ruler of {h.MapFaction.Name},";
            var subject = Facts.Subject(h.Clan); var employer = Facts.Employer(h.Clan);
            if (h.IsClanLeader && h.Clan != null)
                return subject != null ? $", head of clan {h.Clan.Name} of {subject.Name},"
                     : employer != null ? $", head of the mercenary clan {h.Clan.Name} (in the pay of {employer.Name}, not its vassal),"
                     : $", head of clan {h.Clan.Name} (which serves no kingdom),";
            if (h.Clan == null || !h.IsLord) return "";
            return subject != null ? $" of clan {h.Clan.Name} of {subject.Name}"
                 : employer != null ? $" of the mercenary clan {h.Clan.Name} (in the pay of {employer.Name})" : $" of clan {h.Clan.Name} (which serves no kingdom)";
        }

        private static string TitleOfEs(Hero h)
        {
            if (h == null) return "";
            if (h.IsKingdomLeader && h.MapFaction != null) return L.F("AIL_914cbfeb", $", soberano de {h.MapFaction.Name},");
            if (h.IsClanLeader && h.Clan != null && Facts.Employer(h.Clan) != null)
                return L.F("AIL_e7b1c72a", $", jefe del clan mercenario {h.Clan.Name}, a sueldo de {Facts.Employer(h.Clan).Name},");
            if (h.IsClanLeader && h.Clan != null && h.Clan.Kingdom == null && !h.Clan.IsBanditFaction)
                return L.F("AIL_e7b1c72b", $", jefe del clan {h.Clan.Name}, que no sirve a ningún reino,");
            if (h.IsClanLeader && h.Clan != null) return L.F("AIL_7282058a", $", jefe del clan {h.Clan.Name},");
            return h.Clan != null && h.IsLord ? L.F("AIL_a4990140", $", del clan {h.Clan.Name},") : "";
        }
    }
}
