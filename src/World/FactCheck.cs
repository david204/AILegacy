using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace AILegacy.World
{
    /// <summary>Whom a person or a clan answers to. Kingdom is the crown they are subjects of; HiredBy the crown paying them as mercenaries.</summary>
    public class FactTie
    {
        public string Kingdom = "";
        public string HiredBy = "";
    }

    /// <summary>Something the AI says its text relies on: an allegiance (Who + Kingdom), a war or an alliance (A + B).</summary>
    public class FactClaim
    {
        public string Kind = "who";      // who | war | allies
        public string Who, Kingdom;
        public bool Mercenary;
        public string A, B;
    }

    /// <summary>
    /// The world reduced to plain strings: kingdoms and their names, who is at war or allied with whom, and whom every
    /// lord and clan belongs to. No game types in here, so the checks below can be run outside the game.
    /// </summary>
    public class FactModel
    {
        public readonly Dictionary<string, List<string>> KingdomNames = new Dictionary<string, List<string>>();
        public readonly HashSet<string> Wars = new HashSet<string>();
        public readonly HashSet<string> Alliances = new HashSet<string>();
        public readonly Dictionary<string, List<FactTie>> Heroes = new Dictionary<string, List<FactTie>>();
        public readonly Dictionary<string, List<FactTie>> Clans = new Dictionary<string, List<FactTie>>();

        public void AddKingdom(string id, params string[] names)
        {
            if (string.IsNullOrEmpty(id)) return;
            if (!KingdomNames.TryGetValue(id, out var list)) KingdomNames[id] = list = new List<string>();
            foreach (var n in names) if (!string.IsNullOrWhiteSpace(n) && !list.Contains(n)) list.Add(n);
        }

        public void AddWar(string a, string b) { if (a != null && b != null && a != b) Wars.Add(FactCheck.Pair(a, b)); }
        public void AddAlliance(string a, string b) { if (a != null && b != null && a != b) Alliances.Add(FactCheck.Pair(a, b)); }
        public void AddHero(string name, string kingdom, string hiredBy) => Add(Heroes, name, kingdom, hiredBy);
        public void AddClan(string name, string kingdom, string hiredBy) => Add(Clans, name, kingdom, hiredBy);

        private static void Add(Dictionary<string, List<FactTie>> map, string name, string kingdom, string hiredBy)
        {
            string key = FactCheck.Norm(name);
            if (key.Length < 2) return;
            if (!map.TryGetValue(key, out var list)) map[key] = list = new List<FactTie>();
            list.Add(new FactTie { Kingdom = kingdom ?? "", HiredBy = hiredBy ?? "" });
        }

        /// <summary>Kingdom id from an id or a name in any of the forms we know. null when it names no kingdom.</summary>
        public string FindKingdom(string idOrName)
        {
            string n = FactCheck.Norm(idOrName);
            if (n.Length < 2) return null;
            foreach (var kv in KingdomNames) if (FactCheck.Norm(kv.Key) == n) return kv.Key;
            foreach (var kv in KingdomNames) if (kv.Value.Any(x => FactCheck.Norm(x) == n)) return kv.Key;
            if (n.Length < 4) return null;
            // Whole words only from here on, so "Nord" is not found inside "Nordvyg".
            // First a known name inside what was written ("the realm of the Northern Empire"): the longest one wins.
            string soft = FactCheck.Soft(idOrName);
            string best = null; int bestLen = 0; bool tie = false;
            foreach (var kv in KingdomNames)
                foreach (var x in kv.Value)
                {
                    string m = FactCheck.Soft(x);
                    if (m.Trim().Length < 4 || !soft.Contains(m)) continue;
                    if (m.Length > bestLen) { best = kv.Key; bestLen = m.Length; tie = false; }
                    else if (m.Length == bestLen && kv.Key != best) tie = true;
                }
            if (best != null) return tie ? null : best;
            // Then what was written as part of a known name ("Empire"): only when a single kingdom carries it
            foreach (var kv in KingdomNames)
                if (kv.Value.Any(x => FactCheck.Soft(x).Contains(soft)))
                {
                    if (best != null && best != kv.Key) return null;
                    best = kv.Key;
                }
            return best;
        }
    }

    /// <summary>Checks on what the AI writes about the world, against a FactModel. Each returns null when there is nothing wrong.</summary>
    public static class FactCheck
    {
        public static string Norm(string s) => new string((s ?? "").ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
        public static string Pair(string a, string b) => string.CompareOrdinal(a, b) < 0 ? a + "|" + b : b + "|" + a;

        /// <summary>Lowercase words separated by single spaces, with a space at each end.</summary>
        public static string Soft(string s) => Soft(s, false);

        /// <summary>The same, with a lone "|" as a word wherever a comma, a bracket or a dash split the sentence into clauses.</summary>
        private static string Soft(string s, bool clauseMarks)
        {
            var sb = new StringBuilder(" ");
            bool space = true;
            foreach (char c in (s ?? "").ToLowerInvariant())
            {
                if (char.IsLetterOrDigit(c)) { sb.Append(c); space = false; }
                else if (c == '\'' || c == '’') { }      // "Vlandia's" reads as one word
                else
                {
                    if (!space) { sb.Append(' '); space = true; }
                    bool mark = c == ',' || c == '(' || c == ')' || c == '—' || c == '–';
                    if (clauseMarks && mark && !(sb.Length >= 2 && sb[sb.Length - 2] == '|')) sb.Append("| ");
                }
            }
            if (!space) sb.Append(' ');
            return sb.ToString();
        }

        /// <summary>Kingdoms directly touched by an event, plus everyone at war or allied with them.</summary>
        public static HashSet<string> Connected(IEnumerable<string> direct, FactModel m)
        {
            var set = new HashSet<string>();
            foreach (var d in direct ?? Enumerable.Empty<string>()) if (d != null && m.KingdomNames.ContainsKey(d)) set.Add(d);
            foreach (var d in set.ToList())
                foreach (var other in m.KingdomNames.Keys)
                    if (other != d && (m.Wars.Contains(Pair(d, other)) || m.Alliances.Contains(Pair(d, other)))) set.Add(other);
            return set;
        }

        /// <summary>Keeps only the kingdoms the facts connect to the event. The rest come back in dropped.</summary>
        public static List<string> FilterKingdoms(IEnumerable<string> claimed, ICollection<string> allowed, List<string> dropped)
        {
            var kept = new List<string>();
            foreach (var id in claimed ?? Enumerable.Empty<string>())
            {
                if (string.IsNullOrEmpty(id) || kept.Contains(id)) continue;
                if (allowed != null && allowed.Contains(id)) kept.Add(id);
                else if (dropped != null && !dropped.Contains(id)) dropped.Add(id);
            }
            return kept;
        }

        private static readonly string[] Titles = { "clan", "house", "king", "queen", "lord", "lady", "the", "of", "sultan", "khan", "emperor", "empress", "prince", "princess", "ruler", "chief", "jarl" };

        /// <summary>Everyone the name can mean: the whole name, then the name without titles in front, then any single word of it.</summary>
        private static List<FactTie> Lookup(FactModel m, string name)
        {
            var words = Soft(name).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries).ToList();
            if (words.Count == 0) return null;
            for (int from = 0; from < words.Count; from++)
            {
                string key = string.Concat(words.Skip(from));
                var hit = Ties(m, key);
                if (hit != null) return hit;
                if (!Titles.Contains(words[from])) break;      // only titles may be dropped from the front
            }
            List<FactTie> all = null;
            foreach (var w in words)
            {
                if (w.Length < 4 || Titles.Contains(w)) continue;
                var hit = Ties(m, w);
                if (hit != null) (all ?? (all = new List<FactTie>())).AddRange(hit);
            }
            return all;
        }

        private static List<FactTie> Ties(FactModel m, string key)
        {
            m.Heroes.TryGetValue(key, out var a);
            m.Clans.TryGetValue(key, out var b);
            if (a == null) return b;
            if (b == null) return a;
            return a.Concat(b).ToList();
        }

        private static bool IsNone(string s)
        {
            string n = Norm(s);
            return n.Length == 0 || n == "none" || n == "nokingdom" || n == "null" || n == "independent" || n == "nobody" || n == "no";
        }

        /// <summary>
        /// Structured claims. A name we do not know, or a kingdom we cannot place, is let through: only a claim that
        /// plainly contradicts the world is an error.
        /// </summary>
        public static string CheckClaims(IEnumerable<FactClaim> claims, FactModel m)
        {
            foreach (var c in claims ?? Enumerable.Empty<FactClaim>())
            {
                if (c == null) continue;
                if (c.Kind == "war" || c.Kind == "allies")
                {
                    string a = m.FindKingdom(c.A), b = m.FindKingdom(c.B);
                    if (a == null || b == null || a == b) continue;
                    if (c.Kind == "war" && !m.Wars.Contains(Pair(a, b))) return $"says {a} and {b} are at war, and they are not";
                    if (c.Kind == "allies" && !m.Alliances.Contains(Pair(a, b))) return $"says {a} and {b} are allies, and they are not";
                    continue;
                }
                var ties = Lookup(m, c.Who);
                if (ties == null || ties.Count == 0) continue;
                bool none = IsNone(c.Kingdom);
                string k = none ? "" : m.FindKingdom(c.Kingdom);
                if (k == null) continue;
                if (ties.Any(t => t.Kingdom == k)) continue;
                if (!none && c.Mercenary && ties.Any(t => t.HiredBy == k)) continue;
                if (!none && ties.Any(t => t.HiredBy == k)) return $"calls {c.Who} a subject of {k}: they are mercenaries in its pay, not its people";
                var real = ties.Select(t => t.Kingdom.Length > 0 ? t.Kingdom : t.HiredBy.Length > 0 ? "no kingdom (hired by " + t.HiredBy + ")" : "no kingdom").Distinct();
                return $"places {c.Who} in {(none ? "no kingdom" : k)}; the world says {string.Join(" / ", real)}";
            }
            return null;
        }

        // Next to a war or alliance phrase, one of these makes it talk, fear, wish, past or future: not a statement
        // that the war or alliance exists today. Matched as the start of a word, unless listed in ExactHedges.
        private static readonly string[] Hedges =
        {
            "not", "no", "never", "nor", "neither", "if", "should", "would", "could", "may", "might", "threat", "fear", "rumo", "urg", "call", "demand", "risk",
            "brink", "verge", "talk", "whisper", "avoid", "prevent", "unless", "want", "wish", "hope", "plan", "prepar", "consider", "propos", "push", "press",
            "clamo", "soon", "loom", "future", "once", "former", "ended", "end", "peace", "before", "ago", "old", "last", "seek", "offer", "court", "broker",
            "negotiat", "possib", "potential", "rather", "instead", "whether", "wonder", "ask", "plead", "beg", "envoy", "suspect", "accus", "claim", "alleg",
            "said", "say", "despite", "although", "without", "until", "break", "broke", "betray", "truce", "weary", "tired", "question", "doubt", "debate",
            "will", "shall", "ready", "warn", "vow", "swear", "swore", "promis", "must",
            // past: "was at war with", "had been allied with", "used to be", "the war between them is over"
            "was", "were", "had", "used", "over", "previous", "earlier", "remember", "recall", "memor", "histor", "past", "ancient",
            // still to come or not quite: "is about to be", "is going to be", "nearly at war with"
            "about", "going", "almost", "nearly", "can", "cannot",
            // a noun, not a state: "its wars with", "their alliance with"
            "its", "their", "his", "her",
            // "isn't" and the like, which Soft writes without the apostrophe
            "isnt", "arent", "wasnt", "werent", "dont", "doesnt", "didnt", "wont", "cant", "couldnt", "wouldnt", "shouldnt", "hasnt", "havent", "hadnt", "aint",
            "ends", "ending",
        };
        private static readonly HashSet<string> ExactHedges = new HashSet<string>
        {
            "not", "no", "nor", "if", "may", "end", "old", "ago", "say", "ask", "beg", "once", "last", "said", "will", "must", "vow",
            "was", "were", "had", "used", "over", "past", "about", "going", "can", "cannot", "its", "their", "his", "her",
            "isnt", "arent", "wasnt", "werent", "dont", "doesnt", "didnt", "wont", "cant", "couldnt", "wouldnt", "shouldnt", "hasnt", "havent", "hadnt", "aint",
            "ends", "ending",
        };

        private static bool IsHedge(string w)
        {
            foreach (var h in Hedges)
                if (ExactHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal)) return true;
            return false;
        }

        private static readonly char[] Space = { ' ' };
        private static string[] Words(string soft) => soft.Split(Space, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>A hedge among the last <paramref name="max"/> words of this stretch, going back no further than its last clause mark.</summary>
        private static bool HedgeBefore(string stretch, int max)
        {
            var w = Words(stretch.Substring(stretch.LastIndexOf('|') + 1));
            for (int i = Math.Max(0, w.Length - max); i < w.Length; i++) if (IsHedge(w[i])) return true;
            return false;
        }

        // Words that open a new clause: what follows them is no longer about the war or alliance just named
        private static readonly HashSet<string> ClauseOpeners = new HashSet<string> { "and", "but", "while", "as", "so", "yet", "because", "which", "who", "whose", "where", "when" };

        /// <summary>A hedge among the first <paramref name="max"/> words of this stretch, stopping where the clause ends.</summary>
        private static bool HedgeAfter(string stretch, int max)
        {
            var w = Words(stretch);
            for (int i = 0; i < w.Length && i < max; i++)
            {
                if (w[i] == "|" || ClauseOpeners.Contains(w[i])) break;
                if (IsHedge(w[i])) return true;
            }
            return false;
        }

        /// <summary>A hedge anywhere in this stretch: the claim's own words, between its subject and its object.</summary>
        private static bool HedgeIn(string stretch) => Words(stretch).Any(IsHedge);

        // A sentence opening with one of these is a condition or a supposition from its first word to its last
        private static readonly HashSet<string> Suppositions = new HashSet<string> { "if", "unless", "should", "suppose", "imagine", "whether", "were", "had" };

        private struct Mention { public int Start, End; public string Id; }

        /// <summary>Where each kingdom is named in the text. The longest name wins where two overlap.</summary>
        private static List<Mention> Mentions(string soft, FactModel m)
        {
            var found = new List<Mention>();
            var names = new List<KeyValuePair<string, string>>();
            foreach (var kv in m.KingdomNames)
                foreach (var n in kv.Value)
                {
                    string s = Soft(n);
                    if (s.StartsWith(" the ")) s = s.Substring(4);
                    if (s.Trim().Length >= 4) names.Add(new KeyValuePair<string, string>(s, kv.Key));
                }
            foreach (var n in names.OrderByDescending(x => x.Key.Length))
            {
                int at = 0;
                while ((at = soft.IndexOf(n.Key, at, StringComparison.Ordinal)) >= 0)
                {
                    int start = at + 1, end = at + n.Key.Length - 1;
                    if (!found.Any(f => start < f.End && f.Start < end)) found.Add(new Mention { Start = start, End = end, Id = n.Value });
                    at = end;
                }
            }
            return found.OrderBy(f => f.Start).ToList();
        }

        private static readonly string[] WarWords = { " at war with ", " at war against ", " warring with ", " wars with ", " wars against " };
        private static readonly string[] WarBetween = { " war between ", " wars between " };
        private static readonly string[] AllyWords = { " allied with ", " allied to ", " ally of ", " allies of ", " alliance with ", " in league with " };
        private static readonly string[] AllyBetween = { " alliance between ", " alliance of " };

        /// <summary>
        /// Plain statements in an English text: "NAME of KINGDOM" for a lord or a clan that is not of that kingdom, and
        /// a war or an alliance between two kingdoms that does not exist. Text in another language simply finds nothing.
        /// </summary>
        public static string CheckSummary(string text, FactModel m)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var part in Sentences(text))
            {
                string sentence = part.Key;
                string soft = Soft(sentence, true);
                if (soft.Length < 8) continue;
                var ms = Mentions(soft, m);
                if (ms.Count == 0) continue;

                // "Hakon of the Aserai", "clan Wolfskins of Battania"
                foreach (var k in ms)
                {
                    string before = soft.Substring(0, k.Start);
                    if (before.EndsWith(" of the ")) before = before.Substring(0, before.Length - 7);
                    else if (before.EndsWith(" of ")) before = before.Substring(0, before.Length - 3);
                    else continue;
                    var words = before.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                    for (int take = Math.Min(3, words.Length); take >= 1; take--)
                    {
                        string key = string.Concat(words.Skip(words.Length - take));
                        if (key.Length < 4) continue;
                        var ties = Ties(m, key);
                        if (ties == null) continue;
                        if (ties.Any(t => t.Kingdom == k.Id)) break;
                        string who = string.Join(" ", words.Skip(words.Length - take));
                        if (ties.Any(t => t.HiredBy == k.Id)) return $"writes «{who} of {k.Id}»: they are mercenaries in its pay, not its people";
                        return $"writes «{who} of {k.Id}»: the world places them in {string.Join(" / ", ties.Select(t => t.Kingdom.Length > 0 ? t.Kingdom : "no kingdom").Distinct())}";
                    }
                }

                if (ms.Select(x => x.Id).Distinct().Count() < 2) continue;
                // A question, quoted speech or an outright supposition states nothing. Anything else is read phrase by
                // phrase, and only the words around each phrase can hedge it (see Between).
                if (part.Value == '?' || sentence.IndexOfAny(Quotes) >= 0) continue;
                var first = Words(soft).FirstOrDefault(w => w != "|");
                if (first != null && Suppositions.Contains(first)) continue;
                string bad = Between(soft, ms, WarWords, WarBetween, m.Wars);
                if (bad != null) return $"speaks of a war between {bad}, and they are not at war";
                bad = Between(soft, ms, AllyWords, AllyBetween, m.Alliances);
                if (bad != null) return $"speaks of an alliance between {bad}, and they are not allied";
            }
            return null;
        }

        private static readonly char[] Quotes = { '"', '“', '”', '«', '»', '„', '‹', '›' };

        /// <summary>The text cut into sentences, each with the mark that ended it ('.' when the text just stops).</summary>
        private static List<KeyValuePair<string, char>> Sentences(string text)
        {
            var list = new List<KeyValuePair<string, char>>();
            int from = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i < text.Length ? text[i] : '.';
                if (i < text.Length && c != '.' && c != ';' && c != '!' && c != '?' && c != '\n') continue;
                if (i > from) list.Add(new KeyValuePair<string, char>(text.Substring(from, i - from), c));
                from = i + 1;
            }
            return list;
        }

        // "at war with the realm of X": the second kingdom has to follow the phrase closely to be its object
        private const int NearChars = 16;
        // "Battania has long been at war with": the subject has to stand this close (in words) before the phrase
        private const int SubjectReach = 6;
        // How far the hedge is looked for: before the subject, before a "war between" phrase, and after the object
        private const int HedgeLead = 4, HedgeLeadBetween = 7, HedgeTail = 3;

        /// <summary>
        /// The two kingdoms a phrase joins, when the set says they are not joined that way. null when fine.
        /// Every place the phrase appears is read on its own. It only counts as a statement when both kingdoms stand
        /// right by it and nothing in its own clause denies it, doubts it or puts it in the past or the future: a hedge
        /// elsewhere in the sentence no longer excuses it, and anything unclear is let through.
        /// </summary>
        private static string Between(string soft, List<Mention> ms, string[] middle, string[] leading, HashSet<string> truth)
        {
            foreach (var w in middle)
                for (int at = soft.IndexOf(w, StringComparison.Ordinal); at >= 0; at = soft.IndexOf(w, at + 1, StringComparison.Ordinal))
                {
                    int phraseEnd = at + w.Length - 1;      // the space that closes the phrase
                    var a = ms.LastOrDefault(x => x.End <= at + 1);
                    var b = ms.FirstOrDefault(x => x.Start >= phraseEnd && x.Start <= at + w.Length + NearChars);
                    if (a.Id == null || b.Id == null || a.Id == b.Id) continue;
                    string own = soft.Substring(a.End, at + 1 - a.End);      // from the subject to the phrase
                    if (Words(own).Count(x => x != "|") > SubjectReach) continue;
                    if (HedgeIn(own) || HedgeIn(soft.Substring(phraseEnd, b.Start - phraseEnd))) continue;
                    if (HedgeBefore(soft.Substring(0, a.Start), HedgeLead) || HedgeAfter(soft.Substring(b.End), HedgeTail)) continue;
                    if (!truth.Contains(Pair(a.Id, b.Id))) return a.Id + " and " + b.Id;
                }
            foreach (var w in leading)
                for (int at = soft.IndexOf(w, StringComparison.Ordinal); at >= 0; at = soft.IndexOf(w, at + 1, StringComparison.Ordinal))
                {
                    int phraseEnd = at + w.Length - 1;
                    var after = ms.Where(x => x.Start >= phraseEnd && x.Start <= at + w.Length + NearChars * 3).ToList();
                    if (after.Count < 2 || after[0].Id == after[1].Id) continue;
                    if (HedgeIn(soft.Substring(phraseEnd, after[0].Start - phraseEnd)) || HedgeIn(soft.Substring(after[0].End, after[1].Start - after[0].End))) continue;
                    if (HedgeBefore(soft.Substring(0, at + 1), HedgeLeadBetween) || HedgeAfter(soft.Substring(after[1].End), HedgeTail)) continue;
                    if (!truth.Contains(Pair(after[0].Id, after[1].Id))) return after[0].Id + " and " + after[1].Id;
                }
            return null;
        }

        /// <summary>
        /// A ruler named these as their own people. own holds the names (any spelling) of their real lords and clans.
        /// A name that belongs to someone else in the world is an error. A name nobody has is let through.
        /// </summary>
        public static string CheckOwnPeople(IEnumerable<string> named, IEnumerable<string> own, FactModel m)
        {
            var mine = new HashSet<string>((own ?? Enumerable.Empty<string>()).Select(Norm).Where(x => x.Length >= 2));
            foreach (var raw in named ?? Enumerable.Empty<string>())
            {
                var words = Soft(raw).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length == 0) continue;
                string whole = string.Concat(words);
                if (mine.Contains(whole) || words.Any(w => w.Length >= 4 && mine.Contains(w))) continue;
                bool multi = false;
                for (int from = 1; from < words.Length && !multi; from++) multi = mine.Contains(string.Concat(words.Skip(from)));
                if (multi) continue;
                var ties = Lookup(m, raw);
                if (ties != null && ties.Count > 0) return $"claims {raw.Trim()} as their own, and they are not";
            }
            return null;
        }

        /// <summary>
        /// Length of a tribute in days. Only tribute_years is in years, and those are Calradian years
        /// (<paramref name="daysInYear"/> days, 84 in the base game). tribute_days is taken as written: 365 means 365
        /// days, not "one year", because nothing says the AI meant an Earth year. When both come, the years win.
        /// The result is then held between <paramref name="min"/> and <paramref name="max"/>. The caller's cap is
        /// 200 days, so two years (168) fit, and three years or a raw 365 days come out as 200.
        /// </summary>
        public static int TributeDays(int days, int years, int daysInYear, int min, int max)
        {
            int d = years > 0 ? years * daysInYear : days;
            return Math.Max(min, Math.Min(max, d));
        }
    }
}
