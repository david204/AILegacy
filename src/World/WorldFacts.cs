using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AILegacy.Core;
using AILegacy.Prompt;
using TaleWorlds.CampaignSystem;
using TaleWorlds.CampaignSystem.Settlements;
using TaleWorlds.Library;
using TaleWorlds.Localization;

namespace AILegacy.World
{
    /// <summary>A real event waiting for the chronicler: what happened, the facts about who is in it, and the kingdoms those facts touch.</summary>
    internal class Trigger
    {
        public string Text;
        public List<string> Facts = new List<string>();
        public HashSet<string> Kingdoms = new HashSet<string>();
        // Where the facts came from, so they can be read again from the game when the reaction is finally written
        public NewsItem News;
        public Settlement Where;
        public bool Incident;
    }

    /// <summary>
    /// Who belongs to whom, built from the game and never guessed. A clan under mercenary service has Clan.Kingdom set
    /// to the crown that pays it, and Hero.MapFaction returns that crown too, so neither can be read as allegiance:
    /// everything that tells the AI whose man someone is goes through here.
    /// </summary>
    internal static class Facts
    {
        /// <summary>The crown this clan is a subject of. null for mercenaries, minor clans and rebels.</summary>
        public static Kingdom Subject(Clan c) => c != null && !c.IsUnderMercenaryService ? c.Kingdom : null;
        /// <summary>The crown paying this clan as mercenaries right now.</summary>
        public static Kingdom Employer(Clan c) => c != null && c.IsUnderMercenaryService ? c.Kingdom : null;
        public static bool IsHired(Hero h) => Employer(h?.Clan) != null;

        public static string ClanKind(Clan c)
        {
            if (c == null) return "no clan";
            if (c.IsBanditFaction) return "bandits, outside every kingdom";
            var k = c.Kingdom;
            if (k != null && c.IsUnderMercenaryService) return $"mercenary clan in the pay of {k.Name} (hired swords: neither its subjects nor its kin)";
            if (k != null && k.RulingClan == c) return $"ruling clan of {k.Name}";
            if (k != null) return $"vassal clan of {k.Name}";
            if (c.IsRebelClan) return "rebel clan, no kingdom";
            if (c.IsClanTypeMercenary) return "mercenary clan with no employer right now, no kingdom";
            if (c.IsMinorFaction) return "independent minor clan, no kingdom";
            return "independent clan, no kingdom";
        }

        /// <summary>" of Vlandia" for a subject, " (a mercenary in the pay of Vlandia)" for hired swords, nothing for the rest. English, for prompts.</summary>
        public static string Allegiance(Hero h)
        {
            var c = h?.Clan;
            var s = Subject(c);
            if (s != null) return $" of {s.Name}";
            var e = Employer(c);
            return e != null ? $" (a mercenary in the pay of {e.Name})" : "";
        }

        /// <summary>The same for what the player reads.</summary>
        public static string AllegianceLocal(Hero h)
        {
            var c = h?.Clan;
            var s = Subject(c);
            if (s != null) return $" ({s.Name})";
            var e = Employer(c);
            return e != null ? L.F("AIL_e7b1c729", $" (mercenario a sueldo de {e.Name})") : "";
        }

        private static string Tag(Kingdom k) => k == null ? "none" : $"{k.Name} [id: {k.StringId}]";

        public static string HeroFacts(Hero h)
        {
            if (h == null) return "";
            // The dead keep no office: said first and plainly, so nobody is written as still ruling or acting
            if (h.IsDead)
                return $"- {h.Name}: DEAD ({HowDied(h)})" + (h.Clan != null ? $"; was of clan {h.Clan.Name} ({ClanKind(h.Clan)})" : "") +
                       ". The dead hold no title and do nothing: nobody can obey, depose or plot against them.";
            var sb = new StringBuilder($"- {h.Name}: {(h.IsFemale ? "woman" : "man")}");
            if (h.Culture != null) sb.Append($"; {h.Culture.Name} by birth");
            var c = h.Clan;
            if (c != null)
            {
                var s = Subject(c); var e = Employer(c);
                string role = s != null && s.Leader == h ? $"ruler of {s.Name}" : c.Leader == h ? "head of the clan" : "member of the clan";
                sb.Append($"; clan {c.Name} ({ClanKind(c)}), {role}; kingdom: {Tag(s)}");
                if (e != null) sb.Append($"; paid by {Tag(e)} while the contract lasts");
            }
            else
            {
                var home = h.HomeSettlement ?? h.CurrentSettlement;
                sb.Append(h.IsNotable && home != null ? $"; a notable of {home.Name}, no clan" : h.IsWanderer ? "; a wanderer with no clan and no lord" : "; no clan");
                sb.Append("; kingdom: none");
            }
            if (h.IsPrisoner) sb.Append("; a prisoner right now");
            if (h == Hero.MainHero) sb.Append("; (this is the person the kingdom data calls \"the realm of ...\" when they rule)");
            return sb.Append('.').ToString();
        }

        public static string FactionFacts(IFaction f)
        {
            if (f is Kingdom k)
            {
                var wars = WorldSim.Kingdoms.Where(o => o != k && k.IsAtWarWith(o)).Select(Tag).ToList();
                var allies = WorldSim.Kingdoms.Where(o => o != k && Diplomacy.Allied(k, o)).Select(Tag).ToList();
                var hired = k.Clans.Where(c => !c.IsEliminated && c.IsUnderMercenaryService).Select(c => c.Name.ToString()).ToList();
                return $"- {Tag(k)}: ruler {k.Leader?.Name}; at war right now with: {(wars.Count > 0 ? string.Join(", ", wars) : "nobody")}; " +
                       $"allied with: {(allies.Count > 0 ? string.Join(", ", allies) : "nobody")}; " +
                       $"mercenary clans in its pay (hired, not subjects): {(hired.Count > 0 ? string.Join(", ", hired) : "none")}.";
            }
            if (f is Clan c && !c.IsBanditFaction)
            {
                var wars = WorldSim.Kingdoms.Where(o => c.IsAtWarWith(o)).Select(Tag).ToList();
                return $"- Clan {c.Name}: {ClanKind(c)}; led by {c.Leader?.Name}" + (c.Kingdom == null && wars.Count > 0 ? $"; at war with: {string.Join(", ", wars)}." : ".");
            }
            return "";
        }

        public static string PlaceFacts(Settlement s)
        {
            if (s == null) return "";
            string kind = s.IsTown ? "town" : s.IsCastle ? "castle" : s.IsVillage ? "village" : "place";
            var sb = new StringBuilder($"- {s.Name}: {kind} of {Tag(s.MapFaction as Kingdom)}");
            if (s.OwnerClan != null) sb.Append($", held by clan {s.OwnerClan.Name} ({ClanKind(s.OwnerClan)})");
            if (s.IsVillage && s.Village?.Bound != null) sb.Append($", bound to {s.Village.Bound.Name}");
            return sb.Append('.').ToString();
        }

        private static void AddHero(Trigger t, Hero h, HashSet<IFaction> factions)
        {
            if (h == null) return;
            string line = HeroFacts(h);
            if (line.Length > 0 && !t.Facts.Contains(line)) t.Facts.Add(line);
            var k = Subject(h.Clan) ?? Employer(h.Clan);
            if (k != null) factions.Add(k);
            else if (h.Clan != null) factions.Add(h.Clan);
        }

        private static Trigger Finish(Trigger t, HashSet<IFaction> factions, Settlement place)
        {
            if (place != null)
            {
                t.Facts.Add(PlaceFacts(place));
                if (place.MapFaction is Kingdom pk) factions.Add(pk);
            }
            // each kingdom named, and whoever it is fighting or allied with, so the wars are spelled out
            foreach (var f in factions.ToList())
            {
                string line = FactionFacts(f);
                if (line.Length > 0 && !t.Facts.Contains(line)) t.Facts.Add(line);
                if (f is Kingdom k && !k.IsEliminated) t.Kingdoms.Add(k.StringId);
            }
            return t;
        }

        /// <summary>The fact sheet of a real news item, from the ids it carries.</summary>
        public static Trigger ForNews(NewsItem n)
        {
            var t = new Trigger { Text = n.Text, News = n };
            var factions = new HashSet<IFaction>();
            try
            {
                foreach (var id in n.Heroes) AddHero(t, Hero.Find(id), factions);
                foreach (var id in n.Kingdoms)
                {
                    IFaction f = WorldSim.Kingdoms.FirstOrDefault(k => k.StringId == id);
                    if (f == null) f = Clan.All.FirstOrDefault(c => c.StringId == id && !c.IsEliminated);
                    if (f != null) factions.Add(f);
                }
                Finish(t, factions, string.IsNullOrEmpty(n.SettlementId) ? null : Settlement.Find(n.SettlementId));
            }
            catch (Exception ex) { Log.Error("Hechos de una noticia", ex); }
            return t;
        }

        /// <summary>The fact sheet of something the player did: the place, and the player when the text names them.</summary>
        public static Trigger ForIncident(string text, Settlement where)
        {
            var t = new Trigger { Text = text, Where = where, Incident = true };
            var factions = new HashSet<IFaction>();
            try
            {
                var p = Hero.MainHero;
                if (p != null && text.IndexOf(p.Name.ToString(), StringComparison.OrdinalIgnoreCase) >= 0) AddHero(t, p, factions);
                Finish(t, factions, where);
            }
            catch (Exception ex) { Log.Error("Hechos de un suceso", ex); }
            return t;
        }

        /// <summary>
        /// A trigger waits for hours before its reaction is written, and a war can start or end meanwhile. Its facts
        /// are read from the game again right before they are sent.
        /// </summary>
        public static void Refresh(Trigger t)
        {
            try
            {
                if (t == null) return;
                var fresh = t.News != null ? ForNews(t.News) : t.Incident ? ForIncident(t.Text, t.Where) : null;
                if (fresh == null) return;
                t.Facts = fresh.Facts;
                t.Kingdoms = fresh.Kingdoms;
            }
            catch (Exception ex) { Log.Error("Hechos de un suceso, al releerlos", ex); }
        }

        /// <summary>The date in the words of this world, for every prompt that reasons about time.</summary>
        public static string Today()
        {
            try
            {
                var now = CampaignTime.Now;
                return TToday.Format("day", now.GetDayOfSeason + 1, "season", now.GetSeasonOfYear, "year", now.GetYear);
            }
            catch { return ""; }
        }

        private static readonly Tpl TToday = new Tpl("world/today.txt", "## Today: day {day} of {season}, year {year}");

        /// <summary>The day caches below hold who serves whom and where the kingdoms lie. When either changes they are rebuilt at once.</summary>
        public static void Invalidate() { _rosterDay = -1; _geoDay = -1; }
        public static void OnClanChangedKingdom(Clan clan, Kingdom oldKingdom, Kingdom newKingdom, TaleWorlds.CampaignSystem.Actions.ChangeKingdomAction.ChangeKingdomActionDetail detail, bool show) => Invalidate();
        public static void OnKingdomChanged(Kingdom k) => Invalidate();
        public static void OnRulingClanChanged(Kingdom k, Clan c) => Invalidate();
        public static void OnHeroKilled(Hero victim, Hero killer, TaleWorlds.CampaignSystem.Actions.KillCharacterAction.KillCharacterActionDetail detail, bool show) => Invalidate();

        /// <summary>How and when someone died, in English: "executed by X 3 day(s) ago".</summary>
        public static string HowDied(Hero h)
        {
            string how;
            switch (h.DeathMark.ToString())
            {
                case "Executed": case "ExecutionAfterMapEvent": how = h.DeathMarkKillerHero != null ? $"executed by {h.DeathMarkKillerHero.Name}" : "executed"; break;
                case "Murdered": how = h.DeathMarkKillerHero != null ? $"killed by {h.DeathMarkKillerHero.Name}" : "murdered"; break;
                case "DiedInBattle": case "WoundedInBattle": how = "fell in battle"; break;
                case "DiedOfOldAge": how = "died of old age"; break;
                case "DiedInLabor": how = "died in childbirth"; break;
                default: how = "died"; break;
            }
            try { int ago = (int)(CampaignTime.Now.ToDays - h.DeathDay.ToDays); if (ago >= 0) how += ago == 0 ? " today" : $" {ago} day(s) ago"; } catch { }
            return how;
        }

        private static readonly Tpl TDead = new Tpl("world/the_dead.txt",
@"## The dead (exact)
These people are DEAD. Nobody can obey them, serve them, meet them, plot against them, depose them or wait for their answer, and they do nothing. Write of one of them only as dead: the death itself, who answers for it, and what it leaves behind.
{dead}");

        /// <summary>Lords who died lately, for the chronicler. Empty when nobody did.</summary>
        public static string RecentDead(int days = 120, int max = 25)
        {
            try
            {
                double now = CampaignTime.Now.ToDays;
                var lines = new List<KeyValuePair<double, string>>();
                foreach (var h in Hero.DeadOrDisabledHeroes)
                {
                    if (h == null || !h.IsDead || !h.IsLord || h.Clan == null || h.Clan.IsBanditFaction) continue;
                    double ago;
                    try { ago = now - h.DeathDay.ToDays; } catch { continue; }
                    if (ago < 0 || ago > days) continue;
                    var k = h.Clan.Kingdom;
                    lines.Add(new KeyValuePair<double, string>(ago, $"- {h.Name}, of clan {h.Clan.Name}{(k != null ? $" ({k.Name})" : "")}: {HowDied(h)}."));
                }
                if (lines.Count == 0) return "";
                return TDead.Format("dead", string.Join("\n", lines.OrderBy(l => l.Key).Take(max).Select(l => l.Value)));
            }
            catch (Exception ex) { Log.Error("Lista de muertos para la IA", ex); return ""; }
        }

        // ============================================================ the world as plain data, for the checks

        /// <summary>English text of a game name ("{=id}Northern Empire" keeps it after the id), or null.</summary>
        private static string English(TextObject t)
        {
            try
            {
                string v = t?.Value;
                if (string.IsNullOrEmpty(v)) return null;
                if (v.StartsWith("{=")) { int end = v.IndexOf('}'); v = end > 0 ? v.Substring(end + 1) : v; }
                return v.IndexOf('{') >= 0 ? null : v;
            }
            catch { return null; }
        }

        // Game thread only, like everything in this class: it reads live game objects and the day caches below
        // (roster, geography) are plain statics. Every caller is a campaign tick, a game event, WorldSim.Pump or an
        // action queued through MainThread; nothing here runs in an LLM continuation or on the editor's HTTP thread.
        public static FactModel BuildModel()
        {
            var m = new FactModel();
            foreach (var k in WorldSim.Kingdoms)
                m.AddKingdom(k.StringId, k.Name?.ToString(), k.InformalName?.ToString(), English(k.Name), English(k.InformalName));
            var ks = WorldSim.Kingdoms.ToList();
            for (int i = 0; i < ks.Count; i++)
                for (int j = i + 1; j < ks.Count; j++)
                {
                    if (ks[i].IsAtWarWith(ks[j])) m.AddWar(ks[i].StringId, ks[j].StringId);
                    if (Diplomacy.Allied(ks[i], ks[j])) m.AddAlliance(ks[i].StringId, ks[j].StringId);
                }
            foreach (var c in Clan.All)
            {
                if (c == null || c.IsEliminated || c.IsBanditFaction) continue;
                string subject = Subject(c)?.StringId ?? "", hired = Employer(c)?.StringId ?? "";
                m.AddClan(c.Name?.ToString(), subject, hired);
                string en = English(c.Name);
                if (en != null && en != c.Name?.ToString()) m.AddClan(en, subject, hired);
                foreach (var h in c.Heroes)
                {
                    if (h == null || !h.IsAlive) continue;
                    string name = h.Name?.ToString(), first = h.FirstName?.ToString();
                    m.AddHero(name, subject, hired);
                    if (!string.IsNullOrEmpty(first) && first != name) m.AddHero(first, subject, hired);
                }
            }
            // Dead lords, so a text that has one of them acting can be told apart. A name a living person also
            // carries is not held against anyone (FactModel.DeadOnly).
            try
            {
                foreach (var h in Hero.DeadOrDisabledHeroes)
                {
                    if (h == null || !h.IsDead || !h.IsLord || h.Clan == null) continue;
                    string name = h.Name?.ToString(), first = h.FirstName?.ToString();
                    m.AddDead(name, h.StringId);
                    if (!string.IsNullOrEmpty(first) && first != name) m.AddDead(first, h.StringId);
                }
            }
            catch (Exception ex) { Log.Error("Muertos para la comprobación de hechos", ex); }
            return m;
        }

        // ============================================================ roster: who serves whom

        private static List<KeyValuePair<string, string>> _roster;      // kingdom id (null for the clans under no crown) and its line
        private static int _rosterDay = -1;
        private static Campaign _rosterFor;

        // Most the roster and the map may add to a prompt. A modded world can have dozens of kingdoms
        private const int RosterBudget = 6000, GeographyBudget = 2500;

        private static string Led(Clan c) => c.Leader != null ? $"{c.Name} (led by {c.Leader.Name})" : c.Name.ToString();

        /// <summary>
        /// Lines that fit in <paramref name="budget"/> characters. They come most relevant first and are dropped whole
        /// from the end, never cut. A line whose id is in <paramref name="keep"/> always stays, even over the budget.
        /// What was dropped is counted in a last line.
        /// </summary>
        private static string Fit(List<KeyValuePair<string, string>> lines, int budget, ICollection<string> keep, string indent)
        {
            var left = lines.ToList();
            int total = left.Sum(l => l.Value.Length + 1), dropped = 0;
            const int tail = 60;      // room for the closing line
            for (int i = left.Count - 1; i >= 0 && total + (dropped > 0 ? tail : 0) > budget; i--)
            {
                if (left[i].Key != null && keep != null && keep.Contains(left[i].Key)) continue;
                total -= left[i].Value.Length + 1;
                left.RemoveAt(i);
                dropped++;
            }
            var sb = new StringBuilder();
            foreach (var l in left) sb.AppendLine(l.Value);
            if (dropped > 0) sb.AppendLine($"{indent}- (and {dropped} more, left out for length)");
            return sb.ToString().TrimEnd();
        }

        /// <summary>
        /// Every clan under its true banner, for the chronicler. Built once per campaign day, strongest kingdom first.
        /// When it does not fit, the weakest go first; the kingdoms in <paramref name="keep"/> (those tied to what
        /// the prompt is about) are never left out.
        /// </summary>
        public static string Roster(ICollection<string> keep = null)
        {
            int day = (int)CampaignTime.Now.ToDays;
            if (_roster == null || day != _rosterDay || _rosterFor != Campaign.Current)
            {
                var lines = new List<KeyValuePair<string, string>>();
                try
                {
                    foreach (var k in WorldSim.Kingdoms.OrderByDescending(x => x.CurrentTotalStrength))
                    {
                        var vassals = k.Clans.Where(c => !c.IsEliminated && c != k.RulingClan && !c.IsUnderMercenaryService).OrderByDescending(c => c.Tier).Take(20).Select(Led).ToList();
                        var hiredAll = k.Clans.Where(c => !c.IsEliminated && c.IsUnderMercenaryService).OrderByDescending(c => c.Tier).ToList();
                        var hired = hiredAll.Take(12).Select(Led).ToList();
                        if (hiredAll.Count > hired.Count) hired.Add($"and {hiredAll.Count - hired.Count} more hired clans");
                        lines.Add(new KeyValuePair<string, string>(k.StringId,
                            $"- {Tag(k)}: ruling clan {k.RulingClan?.Name} (ruler {k.Leader?.Name}); vassal clans: {(vassals.Count > 0 ? string.Join(", ", vassals) : "none")}; " +
                            $"mercenary clans in its pay (hired, not subjects): {(hired.Count > 0 ? string.Join(", ", hired) : "none")}."));
                    }
                    var free = Clan.All.Where(c => c != null && !c.IsEliminated && !c.IsBanditFaction && c.Kingdom == null && c.Leader != null && c.Leader.IsAlive).ToList();
                    var rebels = free.Where(c => c.IsRebelClan).Take(10).Select(Led).ToList();
                    var minor = free.Where(c => !c.IsRebelClan).OrderByDescending(c => c.Tier).Take(30).Select(Led).ToList();
                    if (minor.Count > 0) lines.Add(new KeyValuePair<string, string>(null, "- Under no crown (minor and independent clans; nobody's subjects): " + string.Join(", ", minor) + "."));
                    if (rebels.Count > 0) lines.Add(new KeyValuePair<string, string>(null, "- Rebel clans, under no crown: " + string.Join(", ", rebels) + "."));
                }
                catch (Exception ex) { Log.Error("Lista de clanes por reino", ex); }
                _roster = lines; _rosterDay = day; _rosterFor = Campaign.Current;
            }
            return Fit(_roster, RosterBudget, keep, "");
        }

        // ============================================================ calendar and geography

        private static readonly Tpl TCalendarMap = new Tpl("world/calendar_and_map.txt",
@"## Calendar and map of Calradia (exact)
- A week has {week} days, a season {season} days, a year {year} days (four seasons). A year is {year} days, never 365, and there are no months: count in days, weeks or seasons.
- Every length you give in days uses this calendar: a tribute for one year lasts {year} days, for half a year {half} days.
- Where the kingdoms lie, from where their towns and castles stand today:
{geography}
- Armies, envoys and merchants travel by road. An alliance or a trade agreement makes sense between neighbours, or between two crowns with a common neighbour they both have reason to fear. Crowns far apart, with other realms between them, have no reason to ally, to trade, to demand anything of each other or to go to war, and a distant war is not their concern.");

        public static void Touch() { var _ = TCalendarMap.Key + TToday.Key + TDead.Key; }

        private static Dictionary<string, float> _gap = new Dictionary<string, float>();
        private static float _span;
        private static int _geoDay = -1;
        private static Campaign _geoFor;

        /// <summary>
        /// Gap between every two kingdoms: the mean of the three shortest distances between a fief of one and a fief of
        /// the other. Recomputed once per campaign day.
        /// </summary>
        private static void EnsureGeography()
        {
            int day = (int)CampaignTime.Now.ToDays;
            if (day == _geoDay && _geoFor == Campaign.Current) return;
            _geoDay = day; _geoFor = Campaign.Current;
            var gap = new Dictionary<string, float>();
            float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
            try
            {
                var pts = new Dictionary<string, List<Vec2>>();
                foreach (var k in WorldSim.Kingdoms)
                {
                    var list = new List<Vec2>();
                    foreach (var f in k.Fiefs)
                    {
                        if (f?.Settlement == null) continue;
                        Vec2 p = f.Settlement.GetPosition2D;
                        list.Add(p);
                        minX = Math.Min(minX, p.x); maxX = Math.Max(maxX, p.x); minY = Math.Min(minY, p.y); maxY = Math.Max(maxY, p.y);
                    }
                    if (list.Count > 0) pts[k.StringId] = list;
                }
                var ids = pts.Keys.ToList();
                for (int i = 0; i < ids.Count; i++)
                    for (int j = i + 1; j < ids.Count; j++)
                    {
                        var d = new List<float>();
                        foreach (var a in pts[ids[i]]) foreach (var b in pts[ids[j]]) d.Add(a.Distance(b));
                        d.Sort();
                        gap[FactCheck.Pair(ids[i], ids[j])] = d.Take(3).Average();
                    }
            }
            catch (Exception ex) { Log.Error("Geografía de los reinos", ex); }
            _gap = gap;
            _span = maxX > minX ? (float)Math.Sqrt((maxX - minX) * (maxX - minX) + (maxY - minY) * (maxY - minY)) : 0f;
        }

        /// <summary>0 = neighbours, 1 = near, 2 = far, 3 = opposite ends of the map, -1 = unknown (a kingdom with no land).</summary>
        public static int Distance(Kingdom a, Kingdom b)
        {
            if (a == null || b == null || a == b) return -1;
            EnsureGeography();
            if (_span <= 0f || !_gap.TryGetValue(FactCheck.Pair(a.StringId, b.StringId), out float g)) return -1;
            float r = g / _span;
            float nearestA = Nearest(a.StringId), nearestB = Nearest(b.StringId);
            // a shared border, or the closest realm either of them has (as long as it is not half a map away)
            if (r <= 0.10f || (r <= 0.20f && (g <= nearestA * 1.6f || g <= nearestB * 1.6f))) return 0;
            return r <= 0.28f ? 1 : r <= 0.5f ? 2 : 3;
        }

        private static float Nearest(string id)
        {
            float best = float.MaxValue;
            foreach (var kv in _gap)
                if (kv.Value < best && (kv.Key.StartsWith(id + "|", StringComparison.Ordinal) || kv.Key.EndsWith("|" + id, StringComparison.Ordinal))) best = kv.Value;
            return best;
        }

        /// <summary>
        /// A line per kingdom, strongest first. Over the budget the weakest are left out, never those in
        /// <paramref name="keep"/>. A kingdom left out still shows in the lines of its neighbours.
        /// </summary>
        public static string Geography(ICollection<string> keep = null)
        {
            var lines = new List<KeyValuePair<string, string>>();
            try
            {
                var all = WorldSim.Kingdoms.OrderByDescending(x => x.CurrentTotalStrength).ToList();
                foreach (var k in all)
                {
                    var sb = new StringBuilder();
                    var by = new List<string>[4] { new List<string>(), new List<string>(), new List<string>(), new List<string>() };
                    bool any = false;
                    foreach (var o in all)
                    {
                        int d = Distance(k, o);
                        if (d < 0) continue;
                        by[d].Add(o.StringId); any = true;
                    }
                    if (!any) { lines.Add(new KeyValuePair<string, string>(k.StringId, $"  - {k.Name} [id: {k.StringId}]: holds no town or castle; no borders of its own.")); continue; }
                    sb.Append($"  - {k.Name} [id: {k.StringId}]: neighbours (shared border): {(by[0].Count > 0 ? string.Join(", ", by[0]) : "none")}");
                    if (by[1].Count > 0) sb.Append("; near: " + string.Join(", ", by[1]));
                    if (by[2].Count > 0) sb.Append("; far: " + string.Join(", ", by[2]));
                    if (by[3].Count > 0) sb.Append("; at the opposite end of the map: " + string.Join(", ", by[3]));
                    lines.Add(new KeyValuePair<string, string>(k.StringId, sb.Append('.').ToString()));
                }
            }
            catch (Exception ex) { Log.Error("Geografía para la IA", ex); }
            return lines.Count == 0 ? "  (unknown)" : Fit(lines, GeographyBudget, keep, "  ");
        }

        /// <param name="keep">Kingdoms the prompt is about: their line of the map is never left out for length.</param>
        public static string CalendarAndMap(ICollection<string> keep = null)
        {
            int year = CampaignTime.DaysInYear, season = CampaignTime.DaysInSeason, week = CampaignTime.DaysInWeek;
            if (year <= 0) { year = 84; season = 21; week = 7; }
            return TCalendarMap.Format("week", week, "season", season, "year", year, "half", year / 2, "geography", Geography(keep));
        }
    }
}
