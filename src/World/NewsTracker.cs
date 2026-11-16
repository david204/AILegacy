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
            CampaignEvents.SettlementEntered.AddNonSerializedListener(owner, VisitNews.OnSettlementEntered);
            CampaignEvents.OnSettlementLeftEvent.AddNonSerializedListener(owner, VisitNews.OnSettlementLeft);
            VisitNews.Reset();
            Captures.Clear();
            SiegeEnds.Clear();
            _watchDay = -1;
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
            try { a(); } catch (Exception ex) { Log.Error("News " + what, ex); }
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
            bool rebellion = detail == ChangeOwnerOfSettlementAction.ChangeOwnerOfSettlementDetail.ByRebellion;
            var newK = newOwner?.MapFaction as Kingdom; var oldK = oldOwner?.MapFaction as Kingdom;
            if (siege)
            {
                // A real fall is news of its own, a card in the news window and a line on screen ("X falls to Y"),
                // with who led the siege. It sets off no AI event: the next batch sees it in its list of changes of hands.
                var who = capturer ?? newOwner;
                n.Type = "capture"; n.Importance = 3;
                string to = newK != null ? newK.Name.ToString() : who?.Name?.ToString();
                n.Text = $"{s.Name} ({kind}{(oldK != null ? " of " + oldK.Name : "")}) has fallen to {to} after a siege led by {who?.Name}{FactionOf(who)}" +
                         (oldOwner != null ? $"; it belonged to {oldOwner.Name}{FactionOf(oldOwner)}." : ".");
                n.TitleEs = L.F("AIL_e7b1da00", $"{s.Name} cae ante {to}");
                n.TextEs = L.F("AIL_e7b1da01", $"{Cap(kindEs)} de {s.Name} ha caído ante {to} tras un asedio dirigido por {who?.Name}{DeEs(who)}") +
                           (oldOwner != null ? L.F("AIL_06be773b", $". Antes pertenecía a {oldOwner.Name}{DeEs(oldOwner)}.") : ".");
            }
            else if (rebellion)
            {
                n.Type = "capture"; n.Importance = 3;
                string clan = newOwner?.Clan?.Name?.ToString() ?? newOwner?.Name?.ToString();
                n.Text = $"{s.Name} ({kind}{(oldK != null ? " of " + oldK.Name : "")}) has been seized in a rebellion by clan {clan}" +
                         (oldOwner != null ? $"; it belonged to {oldOwner.Name}{FactionOf(oldOwner)}." : ".");
                n.TitleEs = L.F("AIL_e7b1da02", $"{s.Name}, en manos de los rebeldes");
                n.TextEs = L.F("AIL_e7b1da03", $"{Cap(kindEs)} de {s.Name} ha caído en una rebelión en manos del clan {clan}") +
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
            // on screen at the importance the player chose for world events (as their cards are)
            if (n.Type == "capture" && NewsStore.Contains(n) && n.Importance >= S.Get.WorldEventNotifyImportance)
                WorldSim.Say(L.F("AIL_e7b1da04", $"Noticia: {n.TitleEs}"), CaptureColor);
        });

        private static readonly TaleWorlds.Library.Color CaptureColor = TaleWorlds.Library.Color.FromUint(0xFFE05A47u);

        /// <summary>
        /// A death the caller tells itself, with a richer record (a death in a conversation, Dialogue/Combat.cs): the
        /// game's own death event for this hero adds no second news item. Cleared by the next death reported.
        /// </summary>
        public static string ToldElsewhere;

        private static void OnHeroKilled(Hero victim, Hero killer, KillCharacterAction.KillCharacterActionDetail detail, bool showNotification) => Safe("muerte", () =>
        {
            if (victim == null) return;
            if (ToldElsewhere != null && ToldElsewhere == victim.StringId) { ToldElsewhere = null; return; }
            if (!(victim.IsLord || victim.IsNotable || victim.IsPlayerCompanion)) return;
            // "Lost" is how the game quietly removes notables, so it's only news for a lord
            if (detail.ToString() == "Lost" && !victim.IsLord) return;
            string how, howEs;
            // Killed by a disease (it kills through the game's old-age death): the banner at the top of the screen,
            // "X died from Y", is shown by the disease code itself (DiseasesPeople); here it is only the news text,
            // which feeds what people know and is no card unless the dead was a sovereign
            string disease = null;
            try { disease = Diseases.DiedOf(victim, detail); } catch { }
            switch (disease != null ? "Disease" : detail.ToString())
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
                case "Disease": how = $"died of {disease}"; howEs = L.F("AIL_e7b1d80c", $"ha muerto de {disease}"); break;
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

        // ---- prisoners: the lords of one realm taken by one enemy in one rout make one news item, and only notable
        // ones are news at all. Nobody reacts at once: most are ransomed or escape within days. Those still held two
        // weeks later are what the world reacts to (CheckWatches).

        private class Capture
        {
            public Hero Prisoner, By;
            public IFaction Captor;
            public Settlement Near;
            public int Rank;
            public double Hour;
            /// <summary>The prisoner's realm (sworn or paying), or his own clan when he serves none.</summary>
            public string Own;
        }
        // Filled by the game's events and emptied by the hourly tick, both on the game thread
        private static readonly List<Capture> Captures = new List<Capture>();
        // A rout can take dozens of lords in one hour. Past this the least notable captives are let go of first
        private const int MaxCaptures = 200;
        // A rout is told once no lord of that realm has been taken by that enemy for this long, or once it has gone on this long
        private const double CaptureQuietHours = 3, CaptureMaxHours = 24;
        /// <summary>Days a notable lord must stay in chains before the world reacts to his capture.</summary>
        public const int HeldDaysBeforeReaction = 14;
        private const int MaxWatches = 60, WatchForgetDays = 40;

        /// <summary>4 = a ruler (world news), 3 = the head of a kingdom's own noble clan (kingdom news), 1 = anyone else (local).</summary>
        private static int CaptiveRank(Hero h) => WarTolls.CaptiveRank(h);

        private static string OwnKey(Hero h)
        {
            var c = h?.Clan;
            IFaction f = (IFaction)Facts.Subject(c) ?? Facts.Employer(c);
            return f?.StringId ?? c?.StringId ?? "";
        }

        private static void OnPrisonerTaken(PartyBase captor, Hero prisoner) => Safe("prisionero", () =>
        {
            if (prisoner == null || !prisoner.IsLord) return;
            Settlement near = null;
            try { near = captor?.MapEvent?.MapEventSettlement ?? captor?.Settlement; } catch { }
            var by = captor?.MapFaction;
            // the game can raise the same capture twice (taken in battle, then handed to a party or a dungeon)
            if (Captures.Any(c => c.Prisoner == prisoner && c.Captor == by)) return;
            Captures.Add(new Capture { Prisoner = prisoner, By = captor?.LeaderHero, Captor = by, Near = near, Rank = CaptiveRank(prisoner), Hour = CampaignTime.Now.ToHours, Own = OwnKey(prisoner) });
            if (Captures.Count > MaxCaptures)
            {
                // the oldest of the least notable goes
                int low = Captures.Min(c => c.Rank);
                Captures.RemoveAt(Captures.FindIndex(c => c.Rank == low));
            }
        });

        private static int _watchDay = -1;

        // Both flushes take each entry out of its list before working on it and guard it on its own, so one that
        // fails costs that one piece of news and nothing else
        private static void OnHourlyTick()
        {
            if (!On) { Captures.Clear(); SiegeEnds.Clear(); return; }
            try { FlushCaptures(); } catch (Exception ex) { Log.Error("News prisoners", ex); }
            try { FlushSieges(); } catch (Exception ex) { Log.Error("News siege end", ex); }
            try { CheckWatches(); } catch (Exception ex) { Log.Error("News prisoners held two weeks", ex); }
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

        /// <summary>
        /// The lords of one realm taken by one enemy are one piece of news: the group is told once it has been quiet
        /// for a few hours (the rest of the rout has been rounded up by then), not one item per lord or per hour.
        /// </summary>
        private static void FlushCaptures()
        {
            if (Captures.Count == 0) return;
            double hour = CampaignTime.Now.ToHours;
            int mode = S.Get.PrisonerNewsMode();      // 0 all, 1 notable only, 2 rulers only
            // the key is read entry by entry so a bad one falls out alone
            var groups = new Dictionary<string, List<Capture>>();
            foreach (var c in Captures.ToList())
            {
                try
                {
                    if (c?.Prisoner == null) { Captures.Remove(c); continue; }
                    string key = (c.Captor?.StringId ?? "") + "|" + (c.Own ?? "");
                    if (!groups.TryGetValue(key, out var same)) groups[key] = same = new List<Capture>();
                    same.Add(c);
                }
                catch (Exception ex) { Captures.Remove(c); Log.Error("News prisoners: one capture discarded", ex); }
            }
            foreach (var g in groups.Values)
            {
                double first = g.Min(c => c.Hour), last = g.Max(c => c.Hour);
                // hour < first: an older save was loaded
                if (!(hour < first || hour - last >= CaptureQuietHours || hour - first >= CaptureMaxHours)) continue;
                foreach (var c in g) Captures.Remove(c);
                try { FlushCaptureGroup(g, mode); }
                catch (Exception ex) { Log.Error("News prisoners: one party discarded", ex); }
            }
        }

        private static void FlushCaptureGroup(List<Capture> g, int mode)
        {
            {
                var list = g.GroupBy(c => c.Prisoner).Select(x => x.First()).OrderByDescending(c => c.Rank).ToList();
                int top = list[0].Rank;
                var by = list.Select(c => c.By).FirstOrDefault(h => h != null);
                var captor = list[0].Captor; var near = list.Select(c => c.Near).FirstOrDefault(s => s != null);
                // watched whatever the news setting keeps: the reaction, if any, comes two weeks later
                Watch(list, by, near);
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
                // Never a reaction now: lords are ransomed, exchanged or escape all the time. One still held two
                // weeks later is what the world answers (CheckWatches).
                var n = new NewsItem
                {
                    Type = "prisoner", Importance = top >= 3 ? top : 1, NoReaction = true,
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

        /// <summary>A rout with a ruler or the head of a sworn clan among the captives is watched for two weeks.</summary>
        private static void Watch(List<Capture> list, Hero by, Settlement near)
        {
            try
            {
                // only the capture of a ruler or of the head of a sworn clan can ever set off a reaction
                if (list.Count == 0 || list.Max(c => c.Rank) < 3) return;
                var st = WorldSim.State;
                if (st == null) return;
                if (st.CaptiveWatch == null) st.CaptiveWatch = new List<CaptiveWatch>();
                st.CaptiveWatch.Add(new CaptiveWatch
                {
                    Day = CampaignTime.Now.ToDays, CaptorId = list[0].Captor?.StringId, OwnId = list[0].Own, ById = by?.StringId, SettlementId = near?.StringId,
                    Heroes = list.Select(c => c.Prisoner.StringId).Distinct().Take(60).ToList(),
                });
                while (st.CaptiveWatch.Count > MaxWatches) st.CaptiveWatch.RemoveAt(0);
            }
            catch (Exception ex) { Log.Error("News prisoners: watching a capture", ex); }
        }

        /// <summary>Once a day: the routs taken two weeks ago whose notable captives are still in chains set off one reaction each.</summary>
        private static void CheckWatches()
        {
            var st = WorldSim.State;
            if (st?.CaptiveWatch == null || st.CaptiveWatch.Count == 0) return;
            double now = CampaignTime.Now.ToDays;
            int day = (int)now;
            if (day == _watchDay) return;
            _watchDay = day;
            foreach (var w in st.CaptiveWatch.ToList())
            {
                // w.Day > now: an older save was loaded
                if (w == null || w.Heroes == null || w.Day > now + 1 || now - w.Day > WatchForgetDays) { st.CaptiveWatch.Remove(w); continue; }
                if (now - w.Day < HeldDaysBeforeReaction) continue;
                st.CaptiveWatch.Remove(w);
                try { ReactToHeld(w, now); }
                catch (Exception ex) { Log.Error("News prisoners: two weeks in chains", ex); }
            }
        }

        /// <summary>
        /// The captives of one rout, two weeks on. Only those still held count (not freed, ransomed, escaped or dead),
        /// and only the ranks the reaction threshold lets through: at 4 rulers alone, at 3 rulers and the heads of
        /// sworn clans, below that everyone, as before. One reaction for all of them, never one per lord.
        /// </summary>
        private static void ReactToHeld(CaptiveWatch w, double now)
        {
            int min = Math.Max(1, Math.Min(4, S.Get.ReactiveMinImportance));
            int floor = min >= 4 ? 4 : min >= 3 ? 3 : 1;
            var held = new List<Hero>();
            foreach (var id in w.Heroes)
            {
                if (string.IsNullOrEmpty(id)) continue;
                var h = Hero.Find(id);
                if (WarTolls.StillHeld(h) && CaptiveRank(h) >= floor) held.Add(h);
            }
            int top = held.Count == 0 ? 0 : held.Max(CaptiveRank);
            if (top < 3 || top < min)
            {
                Log.Info($"[world] captives taken {(int)(now - w.Day)} days ago: none of note is still held, no reaction");
                return;
            }
            held = held.OrderByDescending(CaptiveRank).ToList();
            IFaction holder = WarTolls.HolderOf(held[0]) ?? held[0].PartyBelongedToAsPrisoner?.MapFaction;
            var by = string.IsNullOrEmpty(w.ById) ? null : Hero.Find(w.ById);
            int days = (int)(now - w.Day);
            string names = string.Join(", ", held.Take(6).Select(h => h.Name + Short(h))) + (held.Count > 6 ? $" and {held.Count - 6} more" : "");
            var factions = new List<IFaction> { holder };
            foreach (var h in held) factions.Add((IFaction)Facts.Subject(h.Clan) ?? Facts.Employer(h.Clan));
            var heroes = held.ToList();
            if (by != null && by.IsAlive) heroes.Add(by);
            // Not stored as news (the capture already is): it only calls for the world's answer
            var n = new NewsItem
            {
                Type = "prisoner", Importance = top, Day = now,
                Text = $"{names} {(held.Count == 1 ? "has" : "have")} now been held prisoner" + (holder != null ? $" by {holder.Name}" : "") +
                       $" for {days} days: no ransom, exchange or escape has freed {(held.Count == 1 ? "this captive" : "them")} yet. This is the ordinary business of war: what follows is talk of ransom, an exchange, envoys or revenge sought in the field.",
                Kingdoms = Ids(factions.ToArray()), Heroes = HeroIds(heroes.ToArray()), SettlementId = w.SettlementId,
            };
            Log.Info($"[world] still in chains after {days} days: {names}");
            WorldSim.OnRealNews(n);
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
                Log.Warn("News: cannot read SiegeEvent._isBesiegerDefeated in this game version; a siege end will be reported without saying whether it was broken or lifted");
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
                catch (Exception ex) { Log.Error("News siege end: one discarded", ex); }
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

    /// <summary>
    /// Everything the player did during one visit to a settlement (attacks, street fights, duels, the dead, the guard,
    /// the alarm), told as ONE piece of news once the player has left it, the way AIInfluence keeps a fight record per
    /// visit and lets the world hear of it when the player is back on the map. The consequences of each incident (crime,
    /// relations, what the witnesses and the victims remember) are applied where they happen; only the news waits here.
    /// With world events on, the whole record goes to the AI in one request; otherwise a summary is written here.
    /// </summary>
    internal static class VisitNews
    {
        public class Fight
        {
            public string VictimName = "";
            public string HeroId, FactionId;
            public bool IsHero, IsLord, Female;
            /// <summary>brawl | duel | death</summary>
            public string Kind;
            public bool Unprovoked, Tavern, PlayerWon;
            public string Ending;
            /// <summary>killed | senseless | down (beaten, alive) | standing (the attacker got the worst, or it was broken off)</summary>
            public string Fate;
            /// <summary>-1 = an agreed fight (nobody hunts anyone), 0 = nobody saw it, 1 = seen but not named, 2 = recognised by name.</summary>
            public int SeenLevel = -1;
            public int Witnesses;
            public string ByWhom = "";
            public int Defenders, DefendersDown, DefendersKilled;
        }

        private static Settlement _where;
        private static bool _open;
        private static Campaign _campaign;
        private static readonly List<Fight> Fights = new List<Fight>();
        private static bool _alarm;
        private static int _guards;
        // what the guard did, in order: let_go, fine, arrest, resist_won, resist_lost, escaped, caught, jailed
        private static readonly List<string> GuardSteps = new List<string>();
        private static int _fine;

        private static Hero P => Hero.MainHero;

        public static void Reset()
        {
            Fights.Clear(); GuardSteps.Clear();
            _where = null; _open = false; _alarm = false; _guards = 0; _fine = 0;
            _campaign = Campaign.Current;
        }

        /// <summary>Something happened here: the buffer belongs to the settlement the player is in now.</summary>
        private static void Touch()
        {
            if (_campaign != Campaign.Current) Reset();
            var here = Settlement.CurrentSettlement;
            if (_open && here != _where) Flush();
            _where = here; _open = true; _campaign = Campaign.Current;
        }

        public static void AddFight(Fight f)
        {
            if (f == null) return;
            try { Touch(); Fights.Add(f); Log.Info($"[visit news] fight #{Fights.Count} kept for the end of the visit: {f.Kind}{(f.Unprovoked ? ", unprovoked" : "")} with {f.VictimName} ({f.Fate})"); }
            catch (Exception ex) { Log.Error("Visit news: keep a fight", ex); }
        }

        public static void AlarmRung() { try { Touch(); _alarm = true; } catch { } }

        public static void GuardsCame(int n) { try { if (n <= 0) return; Touch(); _guards += n; } catch { } }

        public static void Guard(string step, int fine = 0)
        {
            try
            {
                Touch();
                GuardSteps.Add(step);
                if (fine > 0) _fine += fine;
                Log.Info($"[visit news] the guard: {step}{(fine > 0 ? " " + fine : "")}");
            }
            catch (Exception ex) { Log.Error("Visit news: the guard", ex); }
        }

        public static void OnSettlementEntered(MobileParty party, Settlement st, Hero hero)
        {
            try
            {
                if (party == null || party != MobileParty.MainParty) return;
                if (_open && st != _where) Flush();
                NewsStore.BeginVisit(st?.StringId);
            }
            catch (Exception ex) { Log.Error("Visit news: entering", ex); }
        }

        public static void OnSettlementLeft(MobileParty party, Settlement st)
        {
            try
            {
                if (party == null || party != MobileParty.MainParty) return;
                if (_open) Flush();
                NewsStore.EndVisit();
            }
            catch (Exception ex) { Log.Error("Visit news: leaving", ex); }
        }

        /// <summary>Game thread, often. If the leaving was missed (jailed, a scene outside any settlement), the scene being
        /// closed with the player no longer in that place is the end of the visit.</summary>
        public static void Tick()
        {
            if (!_open) return;
            try
            {
                if (_campaign != Campaign.Current) { Reset(); return; }
                if (TaleWorlds.MountAndBlade.Mission.Current != null) return;
                if (Campaign.Current?.ConversationManager?.IsConversationInProgress == true) return;
                if (_where == null || (MobileParty.MainParty?.CurrentSettlement != _where && Settlement.CurrentSettlement != _where)) Flush();
            }
            catch (Exception ex) { Log.Error("Visit news: tick", ex); Reset(); }
        }

        // fists against a commoner are a scuffle, not news, unless the player went from one to the next
        private static bool Newsworthy(Fight f, bool spree) => !(f.Kind == "brawl" && !f.IsHero) || (spree && f.Unprovoked && !f.Tavern);

        private static string Cap(string s) => string.IsNullOrEmpty(s) ? s : char.ToUpper(s[0]) + s.Substring(1);

        /// <summary>Tells the whole visit, once, and empties the buffer.</summary>
        public static void Flush()
        {
            var st = _where;
            var fights = Fights.ToList(); var steps = GuardSteps.ToList();
            bool alarm = _alarm; int guards = _guards, fine = _fine;
            Reset();
            try
            {
                if (Campaign.Current == null || P == null) return;
                bool spree = fights.Count(f => f.Unprovoked && f.Kind == "brawl" && !f.Tavern) >= 2;
                var worthy = fights.Where(f => Newsworthy(f, spree)).ToList();
                var attacks = worthy.Where(f => f.Unprovoked && f.SeenLevel >= 0).ToList();
                var agreed = worthy.Where(f => !(f.Unprovoked && f.SeenLevel >= 0)).ToList();
                if (attacks.Count == 0 && agreed.Count == 0 && steps.Count == 0) return;

                string place = st?.Name?.ToString();
                string placeLocal = place ?? L.T("AIL_e7b1d400", "los caminos");
                int level = attacks.Count > 0 ? attacks.Max(f => f.SeenLevel) : 2;
                string byWhom = attacks.FirstOrDefault(f => f.SeenLevel == 2)?.ByWhom ?? "";
                int witnesses = attacks.Count > 0 ? attacks.Max(f => f.Witnesses) : 0;
                bool anyArmed = attacks.Any(f => f.Kind != "brawl"), anyFists = attacks.Any(f => f.Kind == "brawl");
                int killed = attacks.Count(f => f.Fate == "killed") + attacks.Sum(f => f.DefendersKilled);
                int senseless = attacks.Count(f => f.Fate == "senseless");
                int wounded = attacks.Count(f => f.Fate == "down") + attacks.Sum(f => Math.Max(0, f.DefendersDown - f.DefendersKilled));
                int defenders = attacks.Sum(f => f.Defenders), defendersDown = attacks.Sum(f => f.DefendersDown);
                var heroVictims = attacks.Where(f => f.IsHero).Select(f => f.VictimName).Distinct().ToList();

                // ---- English: the record the prompts read (and the AI gets, whole)
                string doerEn = level == 2 ? $"{P.Name} of clan {Clan.PlayerClan?.Name}"
                              : level == 1 ? (anyArmed ? "an armed stranger whom witnesses saw but nobody could name" : "a brawling stranger whom witnesses saw but nobody could name")
                              : "someone nobody saw";
                var en = new System.Text.StringBuilder();
                if (attacks.Count > 0)
                {
                    en.Append($"During one visit to {place ?? "the place"}, {doerEn} attacked {attacks.Count} {(attacks.Count == 1 ? "person" : "people")} without provocation, in the order it happened: ");
                    int i = 0;
                    foreach (var f in attacks)
                    {
                        string victim = f.IsHero ? $"{f.VictimName}{(f.IsLord ? " (a noble)" : "")}" : $"{(f.Female ? "a local woman" : "a local man")} ({f.VictimName})";
                        string fate = f.Fate == "killed" ? "killed" : f.Fate == "senseless" ? "knocked senseless" : f.Fate == "down" ? "beaten and left on the ground, alive" : "not hurt: the attacker got the worst of it";
                        en.Append(i > 0 ? "; " : "");
                        i++;
                        en.Append($"{i}) {victim}, {(f.Kind == "brawl" ? "with bare fists" : "with drawn steel")}{(f.Ending == "struck_down" ? " at the first blow" : "")}: {fate}");
                        if (f.Defenders > 0) en.Append($", {f.Defenders} came to the defence (guards or kin), {f.DefendersDown} of them cut down{(f.DefendersKilled > 0 ? $" ({f.DefendersKilled} dead)" : "")}");
                    }
                    en.Append(". ");
                    en.Append($"Dead in all: {killed}. Wounded: {wounded}. Knocked senseless: {senseless}. ");
                    if (alarm) en.Append("The alarm bell was rung and the whole guard came running. ");
                    else if (guards > 0) en.Append($"The guard heard it and came ({guards} guards). ");
                    en.Append(level == 2 ? $"{byWhom} recognised the attacker: people know it was {P.Name}. "
                            : level == 1 ? $"{witnesses} witness(es) saw the attacker, but nobody knows who it was. "
                            : "Nobody alive saw who did it: the victims were simply found. ");
                }
                foreach (var f in agreed)
                {
                    string what = f.Kind == "brawl" ? "a fist fight" : f.Kind == "death" ? "a duel to the death" : "a duel";
                    string winner = f.PlayerWon ? P.Name.ToString() : f.VictimName, loser = f.PlayerWon ? f.VictimName : P.Name.ToString();
                    en.Append($"{winner} defeated {loser} in {what}{(f.Tavern ? " in the tavern" : "")}{(place != null && attacks.Count == 0 ? $" at {place}" : "")}. ");
                }
                string doerSteps = attacks.Count > 0 && level < 2 ? "the attacker" : P.Name.ToString();
                foreach (var s in steps) en.Append(StepEn(s, doerSteps, place, fine));
                string textEn = en.ToString().Trim();

                var kingdoms = new List<string>();
                if (st?.MapFaction != null) kingdoms.Add(st.MapFaction.StringId);
                kingdoms.AddRange(worthy.Where(f => !string.IsNullOrEmpty(f.FactionId)).Select(f => f.FactionId));
                kingdoms = kingdoms.Distinct().ToList();
                var heroes = worthy.Where(f => f.IsHero && !string.IsNullOrEmpty(f.HeroId)).Select(f => f.HeroId).ToList();
                // the player's name only goes with the news when people here know it
                if (level == 2 || agreed.Count > 0 || attacks.Count == 0) heroes.Add(P.StringId);
                heroes = heroes.Distinct().ToList();

                // ---- The AI writes it: one request with everything that happened here
                var cfg = S.Get;
                if (attacks.Count > 0 && WorldSim.Enabled && cfg.ReactiveEvents && cfg.IncidentEvents)
                {
                    WorldSim.OnIncident(textEn + " This is the whole record of one visit: write ONE event that tells all of it together, never one per victim.", st, anyArmed || killed > 0);
                    Log.Info($"[visit news] {fights.Count} fight(s) in {place ?? "a scene"}: one world event requested");
                    return;
                }

                // ---- Otherwise the summary, written here
                string doer = level == 2 ? P.Name.ToString() : level == 1 ? (anyArmed ? L.T("AIL_e7b1d401", "un forastero armado") : L.T("AIL_e7b1d402", "un forastero")) : L.T("AIL_e7b1d403", "alguien a quien nadie vio");
                var es = new List<string>();
                if (attacks.Count > 0)
                {
                    if (attacks.Count == 1)
                    {
                        var f = attacks[0];
                        string victim = f.IsHero ? f.VictimName : (f.Female ? L.T("AIL_53ab6227", "una vecina") : L.T("AIL_1c6f47d4", "un vecino"));
                        es.Add(L.F("AIL_e7b1d404", $"{Cap(doer)} atacó a {victim} en {placeLocal}."));
                    }
                    else
                    {
                        es.Add(L.F("AIL_e7b1d405", $"{Cap(doer)} atacó a {attacks.Count} personas en {placeLocal}."));
                        if (heroVictims.Count > 0) es.Add(L.F("AIL_e7b1d406", $"Entre ellas, {string.Join(", ", heroVictims)}."));
                    }
                    es.Add(anyArmed && anyFists ? L.T("AIL_e7b1d407", "Empezó a puñetazos y acabó con el acero en la mano.")
                         : anyArmed ? L.T("AIL_e7b1d408", "Fue con el acero desenvainado.") : L.T("AIL_e7b1d409", "Fue a puñetazos, sin armas."));
                    if (killed + wounded + senseless == 0) es.Add(L.T("AIL_e7b1d40a", "Nadie cayó: el agresor se llevó la peor parte."));
                    else
                    {
                        if (killed > 0) es.Add(L.F("AIL_e7b1d40b", $"Muertos: {killed}."));
                        if (wounded > 0) es.Add(L.F("AIL_e7b1d40c", $"Heridos: {wounded}."));
                        if (senseless > 0) es.Add(L.F("AIL_e7b1d40d", $"Dejados sin sentido: {senseless}."));
                    }
                    if (defenders > 0)
                    {
                        es.Add(L.F("AIL_e7b1d40e", $"Salieron a defender a las víctimas {defenders} personas, entre guardias y parientes."));
                        if (defendersDown > 0) es.Add(L.F("AIL_e7b1d40f", $"De ellas cayeron {defendersDown}."));
                    }
                    if (alarm) es.Add(L.T("AIL_e7b1d410", "Sonó la campana de alarma y acudió toda la guardia."));
                    else if (guards > 0) es.Add(L.T("AIL_e7b1d411", "La guardia oyó la pelea y acudió."));
                    es.Add(level == 2 ? L.F("AIL_e7b1d412", $"{byWhom} reconoció al agresor: se sabe que fue {P.Name}.")
                         : level == 1 ? L.F("AIL_e7b1d413", $"Hubo testigos ({witnesses}), pero nadie sabe quién era.")
                         : L.T("AIL_e7b1d414", "Nadie vio quién lo hizo: solo se encontró a las víctimas."));
                }
                foreach (var f in agreed)
                {
                    string whatEs = f.Kind == "brawl" ? L.T("AIL_03bb5d20", "una pelea a puñetazos") : f.Kind == "death" ? L.T("AIL_58f9d028", "un duelo a muerte") : L.T("AIL_a65a644d", "un duelo");
                    string winner = f.PlayerWon ? P.Name.ToString() : f.VictimName, loser = f.PlayerWon ? f.VictimName : P.Name.ToString();
                    es.Add(L.F("AIL_c140427d", $"{winner} venció a {loser} en {whatEs}") + (place != null && attacks.Count == 0 ? L.F("AIL_d63eaabc", $" en {place}.") : "."));
                }
                string doerStepsEs = attacks.Count > 0 && level < 2 ? L.T("AIL_e7b1d415", "el agresor") : P.Name.ToString();
                foreach (var s in steps) { string line = StepLocal(s, doerStepsEs, placeLocal, fine); if (line != null) es.Add(line); }

                string title;
                if (attacks.Count > 0)
                    title = killed >= 3 ? L.F("AIL_e7b1d416", $"Matanza en {placeLocal}")
                          : killed >= 1 ? L.F("AIL_e7b1d417", $"Sangre en {placeLocal}")
                          : attacks.Count >= 2 ? L.F("AIL_e7b1d418", $"Ataques en {placeLocal}")
                          : level == 2 ? L.F("AIL_e7b1d419", $"Agresión de {P.Name}")
                          : level == 1 ? L.T("AIL_f7d8e495", "Agresión de un forastero") : L.T("AIL_0aaea1bc", "Agresión sin testigos");
                else if (agreed.Count >= 2) title = L.F("AIL_e7b1d41a", $"Peleas en {placeLocal}");
                else if (agreed.Count == 1) title = agreed[0].Kind == "brawl" ? L.T("AIL_a0bdbcf2", "Pelea a puñetazos") : L.T("AIL_0db9b33c", "Duelo");
                else
                {
                    string last = steps.LastOrDefault() ?? "";
                    title = last == "let_go" ? L.T("AIL_ca1dd233", "La guardia lo deja pasar")
                          : last == "fine" ? L.T("AIL_74ddb46b", "Multa de la guardia")
                          : last == "resist_won" || last == "escaped" ? L.T("AIL_e7b1d41b", "Huida de la guardia")
                          : L.T("AIL_efa2c194", "Detenido por la guardia");
                }

                // how far it travels: a slaughter or a noble killed reaches the realm, a noble hurt or the guard cut down the court
                bool heroKilled = attacks.Any(f => f.IsHero && f.Fate == "killed");
                int imp = heroKilled || killed >= 5 ? 3 : attacks.Any(f => f.IsHero || f.DefendersKilled > 0) || alarm || killed >= 2 ? 2 : 1;
                if (agreed.Any(f => f.Kind == "death" && f.IsHero)) imp = Math.Max(imp, 3);
                else if (agreed.Any(f => f.IsHero || f.Kind != "brawl")) imp = Math.Max(imp, 2);

                NewsStore.Add(new NewsItem
                {
                    Type = "rumor", Importance = imp, Text = textEn, TitleEs = title, TextEs = string.Join(" ", es),
                    SettlementId = st?.StringId, Kingdoms = kingdoms, Heroes = heroes,
                });
                Log.Info($"[visit news] {fights.Count} fight(s), {steps.Count} guard step(s) in {place ?? "a scene"}: one news item");
            }
            catch (Exception ex) { Log.Error("Visit news: tell the visit", ex); }
        }

        private static string StepEn(string step, string doer, string place, int fine)
        {
            switch (step)
            {
                case "let_go": return $"The guard questioned {doer} and let them go. ";
                case "fine": return $"The guard fined {doer} {fine} denars. ";
                case "arrest": return $"The guard arrested {doer}. ";
                case "resist_won": return $"{Cap(doer)} fought the guard to avoid arrest and got away. ";
                case "resist_lost": return $"{Cap(doer)} fought the guard to avoid arrest and was overpowered. ";
                case "escaped": return $"{Cap(doer)} ran from the guard and escaped from {place ?? "the place"}. ";
                case "caught": return $"{Cap(doer)} tried to run from the guard but was caught. ";
                case "jailed": return $"{Cap(doer)} was taken to the cells of {place ?? "the place"}. ";
                default: return "";
            }
        }

        private static string StepLocal(string step, string doer, string place, int fine)
        {
            switch (step)
            {
                case "let_go": return L.F("AIL_e7b1d41c", $"{Cap(doer)} fue interrogado por la guardia, que lo dejó marchar.");
                case "fine": return L.F("AIL_e7b1d41d", $"{Cap(doer)} tuvo que pagar a la guardia una multa de {fine} denarios.");
                case "arrest": return L.F("AIL_e7b1d41e", $"{Cap(doer)} fue detenido por la guardia.");
                case "resist_won": return L.F("AIL_e7b1d41f", $"{Cap(doer)} se resistió a la guardia y logró escapar.");
                case "resist_lost": return L.F("AIL_e7b1d420", $"{Cap(doer)} se resistió a la guardia y acabó reducido.");
                case "escaped": return L.F("AIL_e7b1d421", $"{Cap(doer)} echó a correr y escapó de {place}.");
                case "caught": return L.F("AIL_e7b1d422", $"{Cap(doer)} intentó huir de la guardia, pero lo alcanzaron.");
                case "jailed": return L.F("AIL_e7b1d423", $"{Cap(doer)} acabó en los calabozos de {place}.");
                default: return null;
            }
        }
    }
}
