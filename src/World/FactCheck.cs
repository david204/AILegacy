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

        /// <summary>Dead people by name, with the name as written and their id.</summary>
        public readonly Dictionary<string, KeyValuePair<string, string>> Dead = new Dictionary<string, KeyValuePair<string, string>>();

        public void AddDead(string name, string id)
        {
            string key = FactCheck.Norm(name);
            if (key.Length >= 4 && !Dead.ContainsKey(key)) Dead[key] = new KeyValuePair<string, string>(name.Trim(), id ?? "");
        }

        /// <summary>The dead whose name no living lord and no clan carries: only those can be told apart by name.</summary>
        public IEnumerable<KeyValuePair<string, KeyValuePair<string, string>>> DeadOnly =>
            Dead.Where(d => !Heroes.ContainsKey(d.Key) && !Clans.ContainsKey(d.Key));

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

        // Next to a peace or truce phrase the same words hedge it, except the ones that are the claim itself
        // ("truce", "peace", "its truce with"), plus the ways a peace is refused, broken or only longed for.
        private static readonly HashSet<string> NotPeaceHedges = new HashSet<string> { "peace", "truce", "weary", "tired", "its", "their", "his", "her" };
        private static readonly string[] PeaceOnlyHedges =
        {
            "refus", "reject", "declin", "spurn", "denounc", "violat", "void", "renounc", "abandon", "imposs", "collaps", "shatter", "fail", "fell", "dead",
            "gone", "lost", "distant", "elusive", "unlikely", "war", "fight", "fought", "battle", "sieg", "raid", "attack", "invad", "sue", "sues", "sued",
            "yearn", "crav", "need", "longs", "longed", "longing", "dream", "pray", "restor", "return", "lasting", "terms", "price",
            // a peace still to be made: "let there be peace between", "time for a truce with", "to make peace between"
            "let", "lets", "time", "path", "road", "way", "chance", "bring", "make", "making", "achiev", "secur", "forg",
            // a peace still to be paid for: "to pay a tribute in exchange for peace between", "to buy a truce with".
            // These count only where UntiePayments leaves them: tied to a peace or a tribute in the same clause.
            "exchang", "pay", "buy", "purchas",
        };
        private static readonly HashSet<string> ExactPeaceHedges = new HashSet<string>
        {
            "war", "sue", "sues", "sued", "fell", "dead", "gone", "lost", "longs", "longed", "longing", "need", "terms", "price",
            "let", "lets", "time", "path", "road", "way", "chance", "make", "making", "pay", "buy",
        };

        // "a truce with X", "any truce with X": one that is wished for, offered or refused, not one in force
        // ("the truce with X", "our truce with X").
        private static readonly HashSet<string> OpenTruce = new HashSet<string> { "a", "any", "another", "new", "some", "no" };

        private static bool IsPeaceHedge(string w)
        {
            foreach (var h in Hedges)
                if (!NotPeaceHedges.Contains(h) && (ExactHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal))) return true;
            foreach (var h in PeaceOnlyHedges)
                if (ExactPeaceHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal)) return true;
            return false;
        }

        private static bool IsPaymentWord(string w) =>
            w == "pay" || w == "buy" || w.StartsWith("exchang", StringComparison.Ordinal) || w.StartsWith("purchas", StringComparison.Ordinal);
        private static readonly HashSet<string> PaidFor = new HashSet<string> { "peace", "truce", "tribute", "tributes" };
        private const int PaidForReach = 8;

        /// <summary>
        /// The sentence with every pay / buy / purchase / exchange word blanked out unless a peace, a truce or a
        /// tribute follows it within a few words of the same clause ("pay ... for peace", "buy a truce", "in
        /// exchange for peace", "pay tribute"). "We pay our soldiers and X is at peace with Y" hedges nothing.
        /// Blanked words keep their length, so positions in the sentence still hold.
        /// </summary>
        private static string UntiePayments(string soft)
        {
            var starts = new List<int>(); var words = new List<string>();
            for (int i = 0; i < soft.Length;)
            {
                if (soft[i] == ' ') { i++; continue; }
                int j = soft.IndexOf(' ', i);
                if (j < 0) j = soft.Length;
                starts.Add(i); words.Add(soft.Substring(i, j - i));
                i = j;
            }
            StringBuilder sb = null;
            for (int i = 0; i < words.Count; i++)
            {
                if (!IsPaymentWord(words[i])) continue;
                bool tied = false;
                for (int j = i + 1; j < words.Count && j <= i + PaidForReach; j++)
                {
                    if (words[j] == "|" || ClauseOpeners.Contains(words[j])) break;
                    if (PaidFor.Contains(words[j])) { tied = true; break; }
                }
                if (tied) continue;
                if (sb == null) sb = new StringBuilder(soft);
                for (int c = 0; c < words[i].Length; c++) sb[starts[i] + c] = '_';
            }
            return sb == null ? soft : sb.ToString();
        }

        private static readonly char[] Space = { ' ' };
        private static string[] Words(string soft) => soft.Split(Space, StringSplitOptions.RemoveEmptyEntries);

        /// <summary>A hedge among the last <paramref name="max"/> words of this stretch, going back no further than its last clause mark.</summary>
        private static bool HedgeBefore(string stretch, int max, Func<string, bool> hedge = null)
        {
            hedge = hedge ?? IsHedge;
            var w = Words(stretch.Substring(stretch.LastIndexOf('|') + 1));
            for (int i = Math.Max(0, w.Length - max); i < w.Length; i++) if (hedge(w[i])) return true;
            return false;
        }

        // Words that open a new clause: what follows them is no longer about the war or alliance just named
        private static readonly HashSet<string> ClauseOpeners = new HashSet<string> { "and", "but", "while", "as", "so", "yet", "because", "which", "who", "whose", "where", "when" };

        /// <summary>A hedge among the first <paramref name="max"/> words of this stretch, stopping where the clause ends.</summary>
        private static bool HedgeAfter(string stretch, int max, Func<string, bool> hedge = null)
        {
            hedge = hedge ?? IsHedge;
            var w = Words(stretch);
            for (int i = 0; i < w.Length && i < max; i++)
            {
                if (w[i] == "|" || ClauseOpeners.Contains(w[i])) break;
                if (hedge(w[i])) return true;
            }
            return false;
        }

        /// <summary>A hedge anywhere in this stretch: the claim's own words, between its subject and its object.</summary>
        private static bool HedgeIn(string stretch, Func<string, bool> hedge = null) => Words(stretch).Any(hedge ?? IsHedge);

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
        private static readonly string[] PeaceWords = { " at peace with ", " truce with ", " no quarrel with ", " not at war with ", " no war with ", " peace holds with " };
        private static readonly string[] PeaceBetween = { " peace between ", " truce between ", " peace holds between ", " truce holds between " };

        /// <summary>
        /// Plain statements in an English text: "NAME of KINGDOM" for a lord or a clan that is not of that kingdom, and
        /// a war or an alliance between two kingdoms that does not exist, or a peace or truce between two that are at
        /// war. Text in another language simply finds nothing. <paramref name="speaker"/> is the kingdom whose ruler
        /// is talking, when there is one: "the truce with Vlandia holds" is then read as theirs.
        /// </summary>
        public static string CheckSummary(string text, FactModel m, string speaker = null)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            foreach (var part in Sentences(text))
            {
                string sentence = part.Key;
                string soft = Soft(sentence, true);
                if (soft.Length < 8) continue;
                var ms = Mentions(soft, m);
                if (ms.Count == 0) continue;
                bool states = part.Value != '?' && sentence.IndexOfAny(Quotes) < 0;
                var opener = Words(soft).FirstOrDefault(w => w != "|");
                if (opener != null && Suppositions.Contains(opener)) states = false;

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

                // A question, quoted speech or an outright supposition states nothing. Anything else is read phrase by
                // phrase, and only the words around each phrase can hedge it (see Between).
                if (!states) continue;
                string bad;
                if (ms.Select(x => x.Id).Distinct().Count() >= 2)
                {
                    bad = Between(soft, ms, WarWords, WarBetween, m.Wars);
                    if (bad != null) return $"speaks of a war between {bad}, and they are not at war";
                    bad = Between(soft, ms, AllyWords, AllyBetween, m.Alliances);
                    if (bad != null) return $"speaks of an alliance between {bad}, and they are not allied";
                }
                bad = Peace(soft, ms, m.Wars, speaker != null && m.KingdomNames.ContainsKey(speaker) ? speaker : null);
                if (bad != null) return $"speaks of peace between {bad}, and they are at war";
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
        /// The two kingdoms a peace or truce phrase joins, when they are at war. null when fine. Read like Between,
        /// with the hedges that fit a peace. When no kingdom stands before the phrase as its subject, the kingdom
        /// speaking is the subject; a subject too far away, or some other kingdom named earlier in the sentence,
        /// leaves it unclear and it is let through.
        /// </summary>
        private static string Peace(string soft, List<Mention> ms, HashSet<string> wars, string speaker)
        {
            Func<string, bool> hedge = IsPeaceHedge;
            // what the hedges are read from: paying or buying only counts where it is a peace that is paid for
            string hs = UntiePayments(soft);
            foreach (var w in PeaceWords)
                for (int at = soft.IndexOf(w, StringComparison.Ordinal); at >= 0; at = soft.IndexOf(w, at + 1, StringComparison.Ordinal))
                {
                    int phraseEnd = at + w.Length - 1;
                    var b = ms.FirstOrDefault(x => x.Start >= phraseEnd && x.Start <= at + w.Length + NearChars);
                    if (b.Id == null) continue;
                    if (w == " truce with ")
                    {
                        var lead = Words(soft.Substring(0, at + 1));
                        if (lead.Length > 0 && OpenTruce.Contains(lead[lead.Length - 1])) continue;
                    }
                    if (HedgeIn(hs.Substring(phraseEnd, b.Start - phraseEnd), hedge) || HedgeAfter(hs.Substring(b.End), HedgeTail, hedge)) continue;
                    var a = ms.LastOrDefault(x => x.End <= at + 1);
                    string subject;
                    if (a.Id != null && Words(soft.Substring(a.End, at + 1 - a.End)).Count(x => x != "|") <= SubjectReach)
                    {
                        if (HedgeIn(hs.Substring(a.End, at + 1 - a.End), hedge) || HedgeBefore(hs.Substring(0, a.Start), HedgeLead, hedge)) continue;
                        subject = a.Id;
                    }
                    else
                    {
                        // no kingdom close enough to be the subject: it is the speaker's own peace, unless the
                        // sentence has already named some other kingdom
                        if (speaker == null || ms.Any(x => x.End <= at + 1 && x.Id != speaker)) continue;
                        if (HedgeBefore(hs.Substring(0, at + 1), SubjectReach, hedge)) continue;
                        subject = speaker;
                    }
                    if (subject != b.Id && wars.Contains(Pair(subject, b.Id))) return subject + " and " + b.Id;
                }
            foreach (var w in PeaceBetween)
                for (int at = soft.IndexOf(w, StringComparison.Ordinal); at >= 0; at = soft.IndexOf(w, at + 1, StringComparison.Ordinal))
                {
                    int phraseEnd = at + w.Length - 1;
                    var after = ms.Where(x => x.Start >= phraseEnd && x.Start <= at + w.Length + NearChars * 3).ToList();
                    if (after.Count < 2 || after[0].Id == after[1].Id) continue;
                    if (HedgeIn(hs.Substring(phraseEnd, after[0].Start - phraseEnd), hedge) || HedgeIn(hs.Substring(after[0].End, after[1].Start - after[0].End), hedge)) continue;
                    if (HedgeBefore(hs.Substring(0, at + 1), HedgeLeadBetween, hedge) || HedgeAfter(hs.Substring(after[1].End), HedgeTail, hedge)) continue;
                    if (wars.Contains(Pair(after[0].Id, after[1].Id))) return after[0].Id + " and " + after[1].Id;
                }
            return null;
        }

        // Whole words that say a named person is dead, wherever they stand in the sentence that names them.
        private static readonly HashSet<string> DeathWords = new HashSet<string>
        {
            "dead", "death", "deaths", "died", "dies", "dying", "late", "deceased", "slain", "slew", "slaying", "killed", "killing", "murder", "murdered",
            "assassinated", "assassination", "executed", "execution", "beheaded", "beheading", "hanged", "hanging", "fell", "fallen", "perished",
            "mourn", "mourns", "mourned", "mourning", "mourners", "grief", "grieve", "grieves", "grieved", "grieving", "funeral", "funerals", "burial",
            "buried", "bury", "grave", "tomb", "corpse", "ghost", "widow", "widowed", "widower", "orphan", "orphans", "orphaned",
            "avenge", "avenges", "avenged", "avenging", "vengeance", "revenge", "martyr", "martyred", "passing", "demise", "gone", "ashes", "pyre", "requiem",
        };
        // Words about what a dead person left. They only count when tied to the name: "the heir of X", "successor to
        // X", "X was succeeded by". "X names his heir" has X alive.
        private static readonly HashSet<string> LegacyWords = new HashSet<string>
        {
            "heir", "heirs", "heiress", "successor", "successors", "succession", "succeed", "succeeds", "succeeded", "succeeding",
            "inherit", "inherits", "inherited", "inheritance", "legacy", "memory", "memorial",
        };
        // The death words a name can be the subject of: "X died", "X fell". Any other one right after the name has
        // him doing it to someone else ("X executed the envoy", "X mourns his son").
        private static readonly HashSet<string> OwnDeath = new HashSet<string> { "died", "dies", "dying", "fell", "perished" };
        private static readonly HashSet<string> BeforeName = new HashSet<string>
        {
            "of", "to", "for", "by", "with", "from", "after", "since", "without", "over", "against", "like", "than", "upon", "at", "about",
        };
        private static readonly HashSet<string> TiesLegacy = new HashSet<string> { "of", "to", "for" };
        private static readonly HashSet<string> PassiveBefore = new HashSet<string> { "is", "was", "be", "been", "being", "are", "were" };
        // Words ending in "s" that can follow a name without being something he does today
        private static readonly HashSet<string> NotActing = new HashSet<string>
        {
            "is", "was", "has", "dies", "lies", "rests", "falls", "perishes", "thus", "perhaps", "always", "his", "this", "its", "as", "plus", "across",
            "towards", "besides", "unless", "sometimes", "alas", "less", "whereas", "afterwards",
        };
        // In a sentence that names nobody dead, one of these can stand for the dead person a claim relies on
        private static readonly HashSet<string> RoleNouns = new HashSet<string>
        {
            "king", "queen", "lord", "lady", "sultan", "khan", "emperor", "empress", "prince", "princess", "ruler", "chief", "jarl", "sovereign", "monarch",
            "leader", "liege", "master", "commander", "father", "mother", "husband", "wife", "brother", "sister", "son", "daughter",
        };

        private struct DeadMention { public int From, To; public string Who; }

        /// <summary>The same person under every name the model holds for them (full name and first name share the id).</summary>
        private static string DeadIdentity(KeyValuePair<string, KeyValuePair<string, string>> d) => string.IsNullOrEmpty(d.Value.Value) ? d.Key : d.Value.Value;

        /// <summary>The word in front of a name, past any title or article: "to Emperor X" gives "to". null at the start.</summary>
        private static string WordBefore(string[] w, DeadMention at)
        {
            int i = at.From - 1;
            while (i >= 0 && w[i] != "of" && Titles.Contains(w[i])) i--;
            return i >= 0 ? w[i] : null;
        }

        /// <summary>He is the subject of a verb in the present: "X musters", "X rules", "X names his heir".</summary>
        private static bool ActsNow(string[] w, DeadMention at)
        {
            if (at.To >= w.Length) return false;
            string before = WordBefore(w, at);
            if (before != null && BeforeName.Contains(before)) return false;      // "the murder of X shocks the empire"
            string next = w[at.To];
            return next.Length >= 4 && next.EndsWith("s", StringComparison.Ordinal) && !next.EndsWith("ss", StringComparison.Ordinal) && !NotActing.Contains(next);
        }

        /// <summary>Whether the word at <paramref name="i"/> says that the person named at <paramref name="at"/> is dead.</summary>
        private static bool SpeaksOfDeath(string[] w, int i, DeadMention at)
        {
            if (DeathWords.Contains(w[i]))
            {
                if (OwnDeath.Contains(w[i])) return true;
                if (i == at.To) return false;                                                           // "X killed the envoy"
                if (i == at.To + 1 && (w[at.To] == "has" || w[at.To] == "had")) return false;           // "X has executed"
                return true;
            }
            if (!LegacyWords.Contains(w[i])) return false;
            if (i < at.From) return at.From - i <= 5 && TiesLegacy.Contains(WordBefore(w, at) ?? "");            // "the heir of X"
            return i >= at.To && i - at.To <= 3 && PassiveBefore.Contains(w[i - 1]);                    // "X was succeeded by"
        }

        /// <summary>The dead person a written name means, when nobody alive answers to it. Titles in front are dropped.</summary>
        private static bool NamesDead(FactModel m, string written, out KeyValuePair<string, string> who)
        {
            who = default(KeyValuePair<string, string>);
            var words = Soft(written).Split(Space, StringSplitOptions.RemoveEmptyEntries).ToList();
            for (int from = 0; from < words.Count; from++)
            {
                string key = string.Concat(words.Skip(from));
                if (m.Heroes.ContainsKey(key) || m.Clans.ContainsKey(key)) return false;
                if (m.Dead.TryGetValue(key, out who)) return true;
                if (!Titles.Contains(words[from])) break;
            }
            return false;
        }

        /// <summary>
        /// An event that has a dead person alive: acting, ruling, being served or plotted against. Read from the
        /// claims (each one names someone the text relies on, today) and from the English summary. The evidence is
        /// tied to each dead person: a sentence that names him must also speak of his death or of what he left
        /// (whole words; when a sentence names several dead, each word goes to the name it stands nearest). A death
        /// told in another sentence, or the death of somebody else, excuses nothing. The possessive ("X's heirs")
        /// is not a mention. A dead person only the claims name needs a sentence that speaks of a death, names no
        /// other dead and points at him by his rank or his realm. A name a living lord or a clan also carries is
        /// never held against the text. null when fine.
        /// </summary>
        public static string CheckDead(IEnumerable<FactClaim> claims, string summary, FactModel m)
        {
            if (m == null || m.Dead.Count == 0) return null;
            var dead = m.DeadOnly.Select(d => new { Who = DeadIdentity(d), Words = Words(Soft(d.Value.Key)), Written = d.Value }).Where(d => string.Concat(d.Words).Length >= 4).ToList();
            if (dead.Count == 0) return null;
            var named = new Dictionary<string, KeyValuePair<string, string>>();     // the dead the summary names, by identity
            var known = new HashSet<string>();                                       // those it knows to be dead
            var unnamedDeaths = new List<string>();                                  // sentences that speak of a death and name nobody dead
            foreach (var part in Sentences(summary ?? ""))
            {
                string soft = Soft(part.Key, true);
                if (soft.Length < 4) continue;
                var w = Words(soft);
                var ms = new List<DeadMention>();
                foreach (var d in dead)
                    for (int i = 0; i + d.Words.Length <= w.Length; i++)
                    {
                        bool same = true;
                        for (int k = 0; k < d.Words.Length && same; k++) same = w[i + k] == d.Words[k];
                        if (!same) continue;
                        ms.Add(new DeadMention { From = i, To = i + d.Words.Length, Who = d.Who });
                        if (!named.ContainsKey(d.Who)) named[d.Who] = d.Written;
                    }
                if (ms.Count == 0)
                {
                    if (w.Any(DeathWords.Contains)) unnamedDeaths.Add(soft);
                    continue;
                }
                for (int i = 0; i < w.Length; i++)
                {
                    if (!DeathWords.Contains(w[i]) && !LegacyWords.Contains(w[i])) continue;
                    Func<DeadMention, int> far = x => i < x.From ? x.From - i : i >= x.To ? i - x.To + 1 : 0;
                    var nearest = ms.OrderBy(far).First();
                    foreach (var x in ms)
                    {
                        // the nearest name, the same person under another name, and a name joined to it ("X and Y were slain")
                        int gap = x.From >= nearest.To ? x.From - nearest.To : nearest.From >= x.To ? nearest.From - x.To : 0;
                        if (x.Who != nearest.Who && gap > 2) continue;
                        if (!ActsNow(w, x) && SpeaksOfDeath(w, i, x)) known.Add(x.Who);
                    }
                }
            }
            foreach (var n in named)
                if (!known.Contains(n.Key)) return $"writes of {n.Value.Key} [{n.Value.Value}] as if alive, and they are dead";
            foreach (var c in claims ?? Enumerable.Empty<FactClaim>())
            {
                if (c == null || c.Kind == "war" || c.Kind == "allies" || string.IsNullOrWhiteSpace(c.Who)) continue;
                if (!NamesDead(m, c.Who, out var who)) continue;
                // named in the summary: his own sentences were read above
                if (named.ContainsKey(string.IsNullOrEmpty(who.Value) ? Norm(who.Key) : who.Value)) continue;
                string realm = string.IsNullOrWhiteSpace(c.Kingdom) ? null : m.FindKingdom(c.Kingdom);
                if (unnamedDeaths.Any(s => Words(s).Any(RoleNouns.Contains) || (realm != null && Mentions(s, m).Any(x => x.Id == realm)))) continue;
                return $"relies on {who.Key} [{who.Value}] as if alive, and they are dead";
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

        // ============================================================ names written slightly wrong

        /// <summary>Letters changed, added or dropped to turn one word into the other, ignoring case. Past max it gives max + 1.</summary>
        public static int EditDistance(string a, string b, int max)
        {
            a = (a ?? "").ToLowerInvariant(); b = (b ?? "").ToLowerInvariant();
            if (Math.Abs(a.Length - b.Length) > max) return max + 1;
            var prev = new int[b.Length + 1];
            var cur = new int[b.Length + 1];
            for (int j = 0; j <= b.Length; j++) prev[j] = j;
            for (int i = 1; i <= a.Length; i++)
            {
                cur[0] = i;
                int best = i;
                for (int j = 1; j <= b.Length; j++)
                {
                    cur[j] = Math.Min(Math.Min(prev[j] + 1, cur[j - 1] + 1), prev[j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1));
                    best = Math.Min(best, cur[j]);
                }
                if (best > max) return max + 1;
                var t = prev; prev = cur; cur = t;
            }
            return Math.Min(prev[b.Length], max + 1);
        }

        /// <summary>The words of a name that can stand for it alone: four letters or more, titles left out ("Omor", "Sturgia").</summary>
        public static List<string> NameWords(string name)
        {
            var res = new List<string>();
            var sb = new StringBuilder();
            foreach (char c in (name ?? "") + " ")
            {
                if (char.IsLetter(c)) { sb.Append(c); continue; }
                if (sb.Length >= 4 && !Titles.Contains(sb.ToString().ToLowerInvariant())) res.Add(sb.ToString());
                sb.Clear();
            }
            return res;
        }

        /// <summary>
        /// A proper name written slightly wrong set right ("Opor's fields" in the title of an event about Omor).
        /// <paramref name="right"/> holds the names the event itself relies on: those its other texts use and its own
        /// places, kingdoms and people. <paramref name="known"/> holds every word of every real name in the world (Norm
        /// form), so a real name is never touched. A word of four letters or more that starts with a capital, is no real
        /// name and is not written in <paramref name="reference"/> either is replaced by the one word of
        /// <paramref name="right"/> it is closest to: one letter off for a word of four letters, two for a longer one
        /// (two only when the first letter matches). A word as close to two different names is left alone. Each change
        /// goes into <paramref name="changes"/>.
        /// </summary>
        public static string FixNames(string text, IEnumerable<string> right, ICollection<string> known, string reference, List<string> changes)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var cands = (right ?? Enumerable.Empty<string>()).SelectMany(NameWords)
                .GroupBy(x => x.ToLowerInvariant()).Select(g => g.First()).ToList();
            if (cands.Count == 0) return text;
            string refSoft = Soft(reference);
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (!char.IsLetter(text[i])) { sb.Append(text[i]); i++; continue; }
                int j = i;
                while (j < text.Length && char.IsLetter(text[j])) j++;
                string word = text.Substring(i, j - i);
                string fixedWord = FixWord(word, cands, known, refSoft);
                if (fixedWord != word) changes?.Add(word + " -> " + fixedWord);
                sb.Append(fixedWord);
                i = j;
            }
            return sb.ToString();
        }

        private static string FixWord(string word, List<string> cands, ICollection<string> known, string refSoft)
        {
            if (word.Length < 4 || !char.IsUpper(word[0])) return word;
            string low = word.ToLowerInvariant();
            if ((known != null && known.Contains(low)) || refSoft.Contains(" " + low + " ")) return word;
            int limit = word.Length <= 4 ? 1 : 2;
            string best = null;
            int bestD = limit + 1;
            bool tie = false;
            foreach (var c in cands)
            {
                string cl = c.ToLowerInvariant();
                // the name itself, or the name with an ending ("Sturgians", "Vlandian", "Khuzaits")
                if (cl == low || low.StartsWith(cl, StringComparison.Ordinal)) return word;
                // a real word that only begins a longer name ("North" of "Northern"); a name one or two letters short
                // ("Praven" for Pravend) is a misspelling and goes on to the distance below
                if (cl.StartsWith(low, StringComparison.Ordinal) && cl.Length - low.Length > 2) return word;
                int d = EditDistance(low, cl, limit);
                // two letters off only for longer words that start the same ("Burning" is not "Bruning")
                if (d > limit || (d == 2 && (low.Length < 6 || cl[0] != low[0] || cl[1] != low[1]))) continue;
                if (d < bestD) { best = c; bestD = d; tie = false; }
                else if (d == bestD && !string.Equals(best, c, StringComparison.OrdinalIgnoreCase)) tie = true;
            }
            if (best == null || tie) return word;
            return word.ToUpperInvariant() == word && word.Length > 1 ? best.ToUpperInvariant() : best;
        }

        // ============================================================ who holds a place

        // "Sturgia must recover Omor": a realm that sets out to win back a place it already holds
        private static readonly HashSet<string> RecoverWords = new HashSet<string>
        {
            "recover", "recovers", "recovering", "retake", "retakes", "retaking", "reclaim", "reclaims", "reclaiming", "regain", "regains", "regaining",
            "recapture", "recaptures", "recapturing", "reconquer", "reconquers", "reconquering", "reconquest", "liberate", "liberates", "liberating", "liberation",
            "recuperar", "recupere", "recuperen", "recuperara", "recuperará", "recuperación", "recuperacion", "reconquistar", "reconquiste", "reconquisten",
            "reconquista", "retomar", "retome", "retomen", "liberar", "libere", "liberen", "liberación", "liberacion", "recobrar", "recobre",
        };
        // Freeing one's own town from a siege, or a village from raiders, is no contradiction: these only count for a place left in peace
        private static readonly HashSet<string> FreeWords = new HashSet<string>
        {
            "liberate", "liberates", "liberating", "liberation", "liberated", "liberar", "libere", "liberen", "liberación", "liberacion", "liberada", "liberado",
        };
        private static readonly HashSet<string> BackVerbs = new HashSet<string> { "take", "takes", "taking", "win", "wins", "winning", "get", "gets", "getting" };
        // "Omor must be retaken by Sturgia": the passive, only after "be" (a plain "recovered" is the past)
        private static readonly HashSet<string> RecoveredWords = new HashSet<string>
        {
            "recovered", "retaken", "reclaimed", "regained", "recaptured", "reconquered", "liberated", "recuperada", "recuperado", "reconquistada", "reconquistado", "liberada", "liberado",
        };
        private static readonly HashSet<string> BeWords = new HashSet<string> { "be", "being", "ser", "sea" };
        // "Sturgia need not retake Omor", "Sturgia no tiene que recuperar Omor"
        private static readonly HashSet<string> NotWords = new HashSet<string>
        {
            "not", "no", "never", "nor", "neednt", "doesnt", "dont", "didnt", "cannot", "cant", "wont", "nunca", "jamás", "jamas", "ni",
        };
        // "Omor, held by the Khuzaits": who holds it now, said outright
        private static readonly string[][] HeldBy =
        {
            new[] { "held", "by" }, new[] { "occupied", "by" }, new[] { "controlled", "by" }, new[] { "ruled", "by" }, new[] { "in", "the", "hands", "of" },
            new[] { "under", "the", "rule", "of" }, new[] { "under", "the", "control", "of" }, new[] { "en", "manos", "de" }, new[] { "en", "poder", "de" },
            new[] { "bajo", "el", "control", "de" }, new[] { "ocupada", "por" }, new[] { "ocupado", "por" }, new[] { "controlada", "por" }, new[] { "controlado", "por" },
            new[] { "gobernada", "por" }, new[] { "gobernado", "por" },
        };
        // "once held by", "until lately held by", "que fue de": the past, which proves nothing about today
        private static readonly HashSet<string> PastWords = new HashSet<string>
        {
            "once", "formerly", "previously", "was", "were", "had", "been", "until", "long", "lately", "recently", "ago", "fue", "era", "estuvo", "antes", "antaño", "hasta",
        };

        private struct Place { public string[] Words; public string Name, Owner; }
        private struct Realm { public int From, To; public string Id; }

        /// <summary>
        /// A text that has a kingdom set out to recover, retake, reclaim or liberate a town, castle or village it already
        /// holds, or says a place is held by a kingdom that does not hold it. <paramref name="owners"/> maps each
        /// place's name to the id of the kingdom that holds it now. Kingdoms are found by their names in the model and
        /// by the names of their people ("the Sturgians", "los khuzaits"). English and Spanish; questions, negations and
        /// the past are let through. <paramref name="threatened"/> are the places under siege or being raided right now:
        /// a realm may well set out to liberate its own. null when fine.
        /// </summary>
        public static string CheckOwnership(string text, IDictionary<string, string> owners, FactModel m, ICollection<string> threatened = null)
        {
            if (string.IsNullOrWhiteSpace(text) || owners == null || owners.Count == 0 || m == null) return null;
            var places = owners.Where(kv => !string.IsNullOrWhiteSpace(kv.Key) && !string.IsNullOrEmpty(kv.Value))
                .Select(kv => new Place { Words = Words(Soft(kv.Key)), Name = kv.Key.Trim(), Owner = kv.Value })
                .Where(p => p.Words.Length > 0 && string.Concat(p.Words).Length >= 3).ToList();
            foreach (var part in Sentences(text))
            {
                if (part.Value == '?') continue;
                var w = Words(Soft(part.Key, true));
                if (w.Length < 3) continue;
                var realms = Realms(w, m);
                if (realms.Count == 0) continue;
                for (int i = 0; i < w.Length; i++)
                {
                    // a realm that sets out to win back a place
                    bool recover = RecoverWords.Contains(w[i]) || (w[i] == "back" && i > 0 && BackVerbs.Contains(w[i - 1]))
                                   || (i > 0 && RecoveredWords.Contains(w[i]) && BeWords.Contains(w[i - 1]));
                    if (recover)
                    {
                        // the place: after the verb, or before it in the passive ("Omor must be retaken by Sturgia")
                        bool passive = RecoveredWords.Contains(w[i]);
                        int at = -1; Place place = default(Place);
                        for (int j = i + 1; j <= Math.Min(w.Length - 1, i + 5) && at < 0; j++)
                            foreach (var p in places)
                                if (Matches(w, j, p.Words)) { at = j; place = p; break; }
                        for (int j = i - 1; passive && j >= Math.Max(0, i - 6) && at < 0; j--)
                            foreach (var p in places)
                                if (Matches(w, j, p.Words)) { at = j; place = p; break; }
                        if (at < 0) continue;
                        if (FreeWords.Contains(w[i]) && threatened != null && threatened.Contains(place.Name)) continue;
                        // its subject: the nearest realm before the verb (and before the place in the passive), or the one after "by"
                        var subject = realms.Where(r => r.To <= Math.Min(i, at) && i - r.To <= 8).OrderByDescending(r => r.To).FirstOrDefault();
                        int subjectEnd = subject.Id != null ? subject.To : -1;
                        if (subject.Id == null)
                        {
                            int end = Math.Max(at + place.Words.Length, i + 1);
                            for (int j = end; j <= Math.Min(w.Length - 1, end + 3); j++)
                                if (w[j] == "by" || w[j] == "por")
                                {
                                    subject = realms.FirstOrDefault(r => r.From > j && r.From <= j + 3);
                                    break;
                                }
                        }
                        if (subject.Id == null || subject.Id != place.Owner) continue;
                        if (subjectEnd >= 0 && Range(w, subjectEnd, i).Any(NotWords.Contains)) continue;
                        return $"has {subject.Id} set out to recover {place.Name}, and {place.Name} already belongs to {place.Owner}";
                    }
                }
                // a place said to be held by a realm that does not hold it
                foreach (var p in places)
                    for (int j = 0; j + p.Words.Length <= w.Length; j++)
                    {
                        if (!Matches(w, j, p.Words)) continue;
                        int end = j + p.Words.Length;
                        if (end < w.Length && w[end] == "|") end++;
                        foreach (var phrase in HeldBy)
                        {
                            int k = end;
                            if (k < w.Length && (w[k] == "now" || w[k] == "still" || w[k] == "ahora" || w[k] == "aún" || w[k] == "aun")) k++;
                            int lead = k;
                            if (!Matches(w, k, phrase)) continue;
                            k += phrase.Length;
                            if (k < w.Length && (w[k] == "the" || w[k] == "los" || w[k] == "las" || w[k] == "el" || w[k] == "la")) k++;
                            var holder = realms.FirstOrDefault(r => r.From == k);
                            if (holder.Id == null || holder.Id == p.Owner) continue;
                            if (Range(w, Math.Max(0, lead - 3), lead + phrase.Length).Any(x => PastWords.Contains(x) || NotWords.Contains(x))) continue;
                            return $"says {p.Name} is held by {holder.Id}, and it belongs to {p.Owner}";
                        }
                    }
            }
            return null;
        }

        private static IEnumerable<string> Range(string[] w, int from, int to)
        {
            for (int i = Math.Max(0, from); i < Math.Min(w.Length, to); i++) yield return w[i];
        }

        private static bool Matches(string[] w, int at, string[] words)
        {
            if (at < 0 || at + words.Length > w.Length) return false;
            for (int k = 0; k < words.Length; k++) if (w[at + k] != words[k]) return false;
            return true;
        }

        /// <summary>
        /// Every kingdom named in the words: by any of its names, or a one-word name followed by the ending of its people
        /// ("sturgians", "vlandianos", "khuzaits"). A word several kingdoms could be is nobody's.
        /// </summary>
        private static List<Realm> Realms(string[] w, FactModel m)
        {
            var names = new List<KeyValuePair<string[], string>>();
            foreach (var kv in m.KingdomNames)
                foreach (var n in kv.Value)
                {
                    var words = Words(Soft(n));
                    if (words.Length > 0 && words[0] == "the") words = words.Skip(1).ToArray();
                    if (words.Length > 0 && string.Concat(words).Length >= 4) names.Add(new KeyValuePair<string[], string>(words, kv.Key));
                }
            var found = new List<Realm>();
            for (int i = 0; i < w.Length; i++)
            {
                if (found.Any(f => i >= f.From && i < f.To)) continue;
                int len = 0; string id = null; bool tie = false;
                foreach (var n in names.OrderByDescending(x => x.Key.Length))
                {
                    bool hit = n.Key.Length == 1 ? People(w[i], n.Key[0]) : Matches(w, i, n.Key);
                    if (!hit || n.Key.Length < len) continue;
                    if (id == null || n.Key.Length > len) { id = n.Value; len = n.Key.Length; tie = false; }
                    else if (id != n.Value) tie = true;
                }
                if (id != null && !tie) found.Add(new Realm { From = i, To = i + len, Id = id });
            }
            return found;
        }

        private static bool People(string word, string name)
        {
            if (word == name || word == name + "s") return true;
            string stem = name.TrimEnd('a', 'e', 'i', 'o', 'u');
            return stem.Length >= 5 && word.StartsWith(stem, StringComparison.Ordinal) && word.Length <= name.Length + 4;
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

        private static bool IsThousandMark(string s, int at) =>
            at < s.Length && (s[at] == 'k' || s[at] == 'K') && (at + 1 >= s.Length || !char.IsLetter(s[at + 1]));

        /// <summary>
        /// Only the FIRST number in the text ("100 now and 100 later" -> 100, "200-300" -> 200, "5000 denars" -> 5000).
        /// Accepts thousands separators ("1.000", "1,000", "1 000" -> 1000) and "5k" -> 5000, "2.5k" -> 2500.
        /// Any other decimals are dropped ("2.5" -> 2). No number is 0. Never negative.
        /// </summary>
        public static int ParseAmount(string text)
        {
            var s = text ?? "";
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            if (i >= s.Length) return 0;
            long v = 0;
            int j = i;
            while (j < s.Length && char.IsDigit(s[j])) { v = v * 10 + (s[j] - '0'); j++; if (v > int.MaxValue) return int.MaxValue; }
            bool grouped = false;
            // Thousands groups: only if the first group has 1-3 digits and each later group exactly 3, with the same separator
            if (j - i <= 3)
            {
                char sep = '\0';
                // j is the separator; s[j+1..j+3] must be digits
                while (j + 3 < s.Length)
                {
                    char c = s[j];
                    bool isSep = c == '.' || c == ',' || c == ' ' || c == '\u00A0' || c == '\u202F' || c == '\'';
                    if (!isSep || (sep != '\0' && c != sep)) break;
                    if (!char.IsDigit(s[j + 1]) || !char.IsDigit(s[j + 2]) || !char.IsDigit(s[j + 3])) break;
                    if (j + 4 < s.Length && char.IsDigit(s[j + 4])) break;              // four or more digits: not a thousands group
                    sep = c;
                    v = v * 1000 + (s[j + 1] - '0') * 100 + (s[j + 2] - '0') * 10 + (s[j + 3] - '0');
                    if (v > int.MaxValue) return int.MaxValue;
                    j += 4;
                    grouped = true;
                }
            }
            // "2.5k" = 2500: a decimal part only counts right in front of the k
            if (!grouped && j + 1 < s.Length && (s[j] == '.' || s[j] == ',') && char.IsDigit(s[j + 1]))
            {
                int f = j + 1, digits = 0; long frac = 0;
                while (f < s.Length && char.IsDigit(s[f])) { if (digits < 3) { frac = frac * 10 + (s[f] - '0'); digits++; } f++; }
                if (IsThousandMark(s, f))
                {
                    for (; digits < 3; digits++) frac *= 10;
                    v = v * 1000 + frac;
                    return v > int.MaxValue ? int.MaxValue : (int)v;
                }
            }
            // "5k" = 5000
            if (IsThousandMark(s, j)) v *= 1000;
            return v > int.MaxValue ? int.MaxValue : (int)v;
        }

        /// <summary>The same, negative when a minus sign stands right before the number ("-500").</summary>
        public static int ParseSignedAmount(string text)
        {
            var s = text ?? "";
            int i = 0;
            while (i < s.Length && !char.IsDigit(s[i])) i++;
            int v = ParseAmount(s);
            return i > 0 && i < s.Length && (s[i - 1] == '-' || s[i - 1] == '\u2212') ? -v : v;
        }

        // ============================================================ grounding: names, falls, ports, people as places

        // Words that often stand capitalised in a chronicle (a sentence start, a title) and are no name. A name written
        // wrong is never "corrected" from one of these, and none of them makes a made-up clan or place.
        private static readonly HashSet<string> CommonWords = new HashSet<string>
        {
            "the", "their", "there", "these", "those", "this", "that", "then", "than", "they", "them", "when", "where", "while", "which", "whose",
            "what", "with", "without", "within", "after", "before", "since", "until", "under", "over", "above", "below", "between", "among", "against",
            "despite", "though", "although", "because", "every", "each", "many", "some", "most", "other", "others", "another", "such", "only", "even",
            "still", "again", "never", "always", "often", "perhaps", "maybe", "meanwhile", "elsewhere", "however", "nevertheless", "indeed", "worse",
            "word", "words", "news", "rumour", "rumours", "rumor", "rumors", "whispers", "talk", "tales", "merchants", "merchant", "traders", "caravans",
            "caravan", "lords", "nobles", "noble", "ladies", "peasants", "villagers", "farmers", "soldiers", "troops", "scouts", "envoys", "envoy",
            "priests", "elders", "guards", "guard", "people", "folk", "crowds", "hunger", "famine", "plague", "grain", "bread", "prices", "price", "trade",
            "taxes", "coin", "silver", "gold", "debts", "war", "wars", "peace", "truce", "battle", "battles", "siege", "sieges", "raid", "raids", "army",
            "armies", "host", "banners", "banner", "crown", "throne", "court", "council", "guild", "guilds", "market", "markets", "temple", "fields",
            "roads", "road", "river", "rivers", "valley", "hills", "plains", "coast", "forest", "woods", "mountains", "north", "south", "east", "west",
            "northern", "southern", "eastern", "western", "great", "grand", "high", "old", "new", "young", "black", "white", "red", "golden", "iron",
            "rival", "rivals", "royal", "ruling", "minor", "mercenary", "mercenaries", "free", "proud", "ancient", "holy", "sacred", "fear", "fears",
            "anger", "unrest", "tension", "tensions", "calls", "demands", "threats", "refugees", "bandits", "raiders", "deserters", "smugglers", "spies",
            "thieves", "fire", "flood", "storm", "winter", "summer", "spring", "autumn", "harvest", "fair", "feast", "wedding", "funeral", "death", "heir",
            "heirs", "sons", "daughters", "children", "brothers", "sisters", "father", "mother", "widow", "house", "houses", "clans", "clan", "family",
            "families", "realm", "realms", "kingdom", "kingdoms", "empire", "lands", "land", "city", "cities", "town", "towns", "castle", "castles",
            "village", "villages", "fortress", "walls", "gates", "streets", "square", "hall", "keep", "harbour", "harbor", "port", "docks", "fleet",
            "ships", "sea", "seas", "lake", "bridge", "pass", "border", "borders", "frontier", "imperial", "imperials", "calradia", "calradian", "calradic",
            "vizier", "chancellor", "marshal", "steward", "captain", "commander", "general", "governor", "magistrate", "champion", "herald", "emissary",
            "master", "mistress", "keeper", "warden", "duke", "duchess", "count", "countess", "baron", "baroness", "earl", "knight", "knights", "sergeant",
            "thane", "boyar", "emir", "sheikh", "senate", "senator", "senators", "guildmaster", "elder", "chieftain", "warlord",
            "los", "las", "del", "una", "unos", "unas", "este", "esta", "estos", "estas", "ese", "esa", "esos", "esas", "aquel", "cuando", "donde",
            "mientras", "seg\u00fan", "segun", "tras", "ante", "desde", "hasta", "pese", "aunque", "porque", "cada", "muchos", "muchas", "algunos", "algunas",
            "otros", "otras", "otro", "otra", "ahora", "entonces", "nunca", "siempre", "todav\u00eda", "todavia", "a\u00fan", "adem\u00e1s", "ademas", "incluso", "sin",
            "embargo", "rumores", "mercaderes", "comerciantes", "caravanas", "se\u00f1ores", "senores", "damas", "campesinos", "aldeanos", "soldados", "tropas",
            "exploradores", "emisarios", "enviados", "sacerdotes", "ancianos", "guardias", "gente", "pueblo", "hambre", "peste", "grano", "trigo", "precios",
            "comercio", "impuestos", "plata", "deudas", "guerra", "tregua", "batalla", "asedio", "saqueo", "ej\u00e9rcito", "ejercito", "ej\u00e9rcitos", "hueste",
            "estandartes", "corona", "trono", "corte", "consejo", "gremio", "gremios", "mercado", "templo", "campos", "caminos", "camino", "valle",
            "colinas", "llanuras", "costa", "bosque", "monta\u00f1as", "norte", "oeste", "gran", "grande", "alto", "viejo", "vieja", "nuevo", "nueva", "negro",
            "negra", "blanco", "rojo", "dorado", "hierro", "rivales", "menor", "mercenario", "libre", "antiguo", "sagrado", "miedo", "tensi\u00f3n", "tensiones",
            "refugiados", "bandidos", "saqueadores", "desertores", "contrabandistas", "esp\u00edas", "espias", "ladrones", "fuego", "incendio", "inundaci\u00f3n",
            "tormenta", "invierno", "verano", "primavera", "oto\u00f1o", "cosecha", "feria", "fiesta", "boda", "funeral", "muerte", "heredero", "herederos",
            "hijos", "hijas", "hermanos", "padre", "madre", "viuda", "casa", "casas", "clanes", "familia", "reino", "reinos", "imperio", "imperiales",
            "tierras", "tierra", "ciudad", "ciudades", "castillo", "castillos", "aldea", "aldeas", "fortaleza", "murallas", "puertas", "calles", "plaza",
            "sal\u00f3n", "puerto", "flota", "barcos", "lago", "puente", "paso", "frontera", "fronteras",
        };

        /// <summary>A word (any case) that stands capitalised in texts without being a name.</summary>
        public static bool IsCommon(string word)
        {
            string low = (word ?? "").ToLowerInvariant();
            return CommonWords.Contains(low) || Titles.Contains(low) || (low.EndsWith("s") && Titles.Contains(low.Substring(0, low.Length - 1)));
        }

        /// <summary>A real name word, or one with the ending of its people ("Sturgians", "Vlandian", "khuzaits").</summary>
        private static bool KnownWord(string low, ICollection<string> known)
        {
            if (known == null || low.Length < 3) return false;
            if (known.Contains(low)) return true;
            for (int cut = 1; cut <= 3 && low.Length - cut >= 4; cut++)
                if (known.Contains(low.Substring(0, low.Length - cut))) return true;
            return false;
        }

        /// <summary>
        /// The second pass over a text, against every real name of the world rather than the event's own: a
        /// capitalised word of five letters or more that is no real name and no common word is set to the one real
        /// name it is closest to, with the same first letter, one letter off, or two for a word of six letters or more
        /// whose first two letters match ("Asinai" for Aserai, "Praven" for Pravend). A word as close to two names is
        /// left alone. <paramref name="names"/> holds the names to set it to (places, realms, cultures, clans: not
        /// people, there are too many of them and they are short); <paramref name="known"/> every word of every real
        /// name (lowercase), which is never touched.
        /// </summary>
        public static string FixNamesGlobal(string text, IEnumerable<string> names, ICollection<string> known, List<string> changes)
        {
            if (string.IsNullOrEmpty(text)) return text;
            var cands = (names ?? Enumerable.Empty<string>()).SelectMany(NameWords)
                .GroupBy(x => x.ToLowerInvariant()).Select(g => g.First()).ToList();
            if (cands.Count == 0) return text;
            var sb = new StringBuilder(text.Length);
            int i = 0;
            while (i < text.Length)
            {
                if (!char.IsLetter(text[i])) { sb.Append(text[i]); i++; continue; }
                int j = i;
                while (j < text.Length && char.IsLetter(text[j])) j++;
                string word = text.Substring(i, j - i);
                string fixedWord = FixWordGlobal(word, cands, known);
                if (fixedWord != word) changes?.Add(word + " -> " + fixedWord);
                sb.Append(fixedWord);
                i = j;
            }
            return sb.ToString();
        }

        private static string FixWordGlobal(string word, List<string> cands, ICollection<string> known)
        {
            if (word.Length < 5 || !char.IsUpper(word[0])) return word;
            string low = word.ToLowerInvariant();
            if (KnownWord(low, known) || IsCommon(low)) return word;
            string best = null;
            int bestD = 3;
            bool tie = false;
            foreach (var c in cands)
            {
                string cl = c.ToLowerInvariant();
                if (cl.Length < 4 || cl[0] != low[0]) continue;
                if (cl == low || low.StartsWith(cl, StringComparison.Ordinal)) return word;
                if (cl.StartsWith(low, StringComparison.Ordinal) && cl.Length - low.Length > 2) continue;
                int d = EditDistance(low, cl, 2);
                if (d > 2 || (d == 2 && (low.Length < 6 || cl[1] != low[1]))) continue;
                if (d < bestD) { best = c; bestD = d; tie = false; }
                else if (d == bestD && !string.Equals(best, c, StringComparison.OrdinalIgnoreCase)) tie = true;
            }
            if (best == null || tie) return word;
            return word.ToUpperInvariant() == word ? best.ToUpperInvariant() : best;
        }

        // ---- made-up names that survive the corrections

        private struct Token { public string Text, Low; public bool Cap, First; }

        /// <summary>The words of a text with their case, and whether each one opens a sentence.</summary>
        private static List<Token> Tokens(string text)
        {
            var list = new List<Token>();
            bool first = true;
            int i = 0;
            text = text ?? "";
            while (i < text.Length)
            {
                char c = text[i];
                if (char.IsLetter(c))
                {
                    int j = i;
                    while (j < text.Length && (char.IsLetter(text[j]) || ((text[j] == '\'' || text[j] == '\u2019') && j + 1 < text.Length && char.IsLetter(text[j + 1])))) j++;
                    string w = text.Substring(i, j - i);
                    // "Tasynor's" is Tasynor
                    if (w.EndsWith("'s") || w.EndsWith("\u2019s")) w = w.Substring(0, w.Length - 2);
                    list.Add(new Token { Text = w, Low = w.ToLowerInvariant(), Cap = char.IsUpper(w[0]), First = first });
                    first = false;
                    i = j;
                    continue;
                }
                if (c == '.' || c == '!' || c == '?' || c == ';' || c == ':' || c == '\n' || c == '\u00ab' || c == '\u00bb' || c == '"' || c == '\u201c' || c == '\u201d' || c == '\u00a1' || c == '\u00bf') first = true;
                i++;
            }
            return list;
        }

        private static readonly HashSet<string> BandNouns = new HashSet<string>
        {
            "brotherhood", "sisterhood", "order", "company", "band", "legion", "circle", "hand", "fist", "host", "league", "covenant", "fellowship", "brethren",
        };
        private static readonly HashSet<string> ClanLinks =new HashSet<string> { "de", "los", "las", "del", "of", "the", "la", "el" };
        private static readonly HashSet<string> PlaceOfNouns = new HashSet<string>
        {
            "town", "city", "castle", "village", "fortress", "stronghold", "fort", "port", "harbour", "harbor",
            "ciudad", "castillo", "aldea", "villa", "fortaleza", "puerto", "pueblo",
        };

        /// <summary>The capitalised words of a run that are names (sentence openers left out), lowercase.</summary>
        private static List<string> CapRun(List<Token> t, int from, int step, int max)
        {
            var run = new List<string>();
            for (int i = from; i >= 0 && i < t.Count && run.Count < max; i += step)
            {
                // a sentence opener is capitalised anyway: it ends the run and is left out of it
                if (!t[i].Cap || ClanLinks.Contains(t[i].Low) || t[i].First) break;
                run.Add(t[i].Low);
            }
            return run;
        }

        /// <summary>A run made up: none of its words is a real name or a person's name, and one of them is no common word either.</summary>
        private static bool MadeUp(List<string> run, ICollection<string> known, ICollection<string> people)
        {
            if (run.Count == 0) return false;
            if (run.Any(w => KnownWord(w, known) || (people != null && people.Contains(w)))) return false;
            return run.Any(w => w.Length >= 3 && !IsCommon(w));
        }

        /// <summary>
        /// A clan, a house or a place with a name of its own that exists nowhere in the world, after the names written
        /// wrong were set right: "the Black Riding clan", "clan Ironhand", "House Varro", "the castle of Dunmoor",
        /// "the Black Riders of Sturgia". <paramref name="known"/> holds every word of every real name (lowercase),
        /// <paramref name="people"/> the words of the names of living lords. Capitalised words that only open a
        /// sentence do not count. null when fine.
        /// </summary>
        public static string CheckInventedNames(string text, ICollection<string> known, ICollection<string> people)
        {
            var t = Tokens(text);
            for (int i = 0; i < t.Count; i++)
            {
                string w = t[i].Low;
                if (w == "clan" || w == "clans")
                {
                    // "the Black Riding clan"
                    var before = CapRun(t, i - 1, -1, 3);
                    before.Reverse();
                    if (MadeUp(before, known, people)) return $"names a clan that does not exist: \u00ab{string.Join(" ", before)} clan\u00bb";
                    // "clan Ironhand", "el clan de los Jinetes Negros"
                    int j = i + 1;
                    while (j < t.Count && !t[j].Cap && ClanLinks.Contains(t[j].Low) && j - i <= 3) j++;
                    var after = CapRun(t, j, 1, 3);
                    if (MadeUp(after, known, people)) return $"names a clan that does not exist: \u00abclan {string.Join(" ", after)}\u00bb";
                }
                else if (w == "house" && t[i].Cap && !t[i].First)
                {
                    var after = CapRun(t, i + 1, 1, 2);
                    if (MadeUp(after, known, people)) return $"names a noble house that does not exist: \u00abHouse {string.Join(" ", after)}\u00bb";
                }
                else if (PlaceOfNouns.Contains(w) && i + 1 < t.Count && (t[i + 1].Low == "of" || t[i + 1].Low == "de"))
                {
                    // "the castle of Dunmoor"; a person after it is a check of its own (CheckHeroAsPlace)
                    var after = CapRun(t, i + 2, 1, 3);
                    if (MadeUp(after, known, people)) return $"names a place that does not exist: \u00ab{w} of {string.Join(" ", after)}\u00bb";
                }
                else if (w == "the" && !t[i].Cap)
                {
                    // "the Black Riders of Sturgia": a band with a name of its own, tied to a real realm or place
                    var run = CapRun(t, i + 1, 1, 3);
                    int of = i + 1 + run.Count;
                    if (run.Count == 0 || of + 1 >= t.Count || t[of].Low != "of" || !t[of + 1].Cap || !KnownWord(t[of + 1].Low, known)) continue;
                    // a band is many ("the Black Riders", "the Iron Brotherhood"); one person's byname ("the Lion of
                    // Vlandia") is a way of speaking of a real lord
                    string last = run[run.Count - 1];
                    if (!(last.EndsWith("s") || BandNouns.Contains(last))) continue;
                    if (MadeUp(run, known, people)) return $"names a band or a house that does not exist: \u00abthe {string.Join(" ", run)} of {t[of + 1].Text}\u00bb";
                }
            }
            return null;
        }

        // ---- people written as places

        private static readonly HashSet<string> FiefNouns = new HashSet<string>
        {
            "castle", "town", "city", "fief", "fiefs", "fortress", "stronghold", "walls", "gates", "garrison", "port", "harbour", "harbor", "siege", "village",
            "castillo", "ciudad", "feudo", "feudos", "fortaleza", "murallas", "guarnici\u00f3n", "guarnicion", "asedio", "aldea", "villa", "puerto",
        };
        private static readonly HashSet<string> FiefAfterName = new HashSet<string> { "castle", "town", "city", "fortress", "stronghold", "keep", "fief", "harbour", "harbor", "port" };
        private static readonly HashSet<string> StrikeVerbs = new HashSet<string>
        {
            "besieged", "besieging", "besiege", "besieges", "stormed", "storm", "storms", "sacked", "sack", "sacks", "raided", "raid", "raids", "conquered",
            "conquer", "conquers", "asedi\u00f3", "asedio", "asediaron", "asedian", "asediar", "conquist\u00f3", "conquisto", "conquistaron", "conquistar", "saque\u00f3",
            "saqueo", "saquearon", "saquear", "took", "taken", "seized", "tom\u00f3", "tomo", "tomaron",
        };
        private static readonly HashSet<string> TakeOfPeople = new HashSet<string> { "took", "taken", "seized", "tom\u00f3", "tomo", "tomaron" };
        private static readonly HashSet<string> CaptiveWords = new HashSet<string>
        {
            "prisoner", "prisoners", "captive", "captives", "hostage", "hostages", "alive", "custody", "chains", "aside", "prisionero", "prisionera",
            "prisioneros", "cautivo", "cautiva", "reh\u00e9n", "rehen", "vivo", "viva", "as", "como",
        };
        private static readonly HashSet<string> StrikeFillers = new HashSet<string> { "the", "town", "castle", "city", "of", "la", "el", "de", "ciudad", "castillo" };
        private static readonly HashSet<string> TrustIn = new HashSet<string> { "faith", "trust", "confidence", "belief", "believe", "believes", "believed", "hope", "hopes", "interest", "pride", "proud" };

        /// <summary>
        /// A living lord written as if he were a place: "the fief of Tasynor", "Tasynor castle", "they besieged
        /// Tasynor", "took Tasynor" (unless as a prisoner), "the lords gathered in Tasynor". "The lands of Tasynor" and
        /// "Tasynor's castle" are his and are fine. <paramref name="people"/> holds the words of living lords' names that
        /// are no place, realm or clan word (lowercase). null when fine.
        /// </summary>
        public static string CheckHeroAsPlace(string text, ICollection<string> people)
        {
            if (string.IsNullOrWhiteSpace(text) || people == null || people.Count == 0) return null;
            foreach (var part in Sentences(text))
            {
                var w = Words(Soft(part.Key, true));
                for (int i = 0; i < w.Length; i++)
                {
                    if (!people.Contains(w[i])) continue;
                    string who = w[i];
                    // "the fief of Tasynor", "el castillo de Tasynor"
                    int b = i - 1;
                    if (b >= 0 && (w[b] == "the" || w[b] == "la" || w[b] == "el")) b--;
                    if (b >= 1 && (w[b] == "of" || w[b] == "de" || w[b] == "del") && FiefNouns.Contains(w[b - 1]))
                        return $"writes \u00ab{w[b - 1]} of {who}\u00bb: {who} is a person, not a place";
                    // "Tasynor castle"
                    if (i + 1 < w.Length && FiefAfterName.Contains(w[i + 1])) return $"writes \u00ab{who} {w[i + 1]}\u00bb: {who} is a person, not a place";
                    // "besieged Tasynor", "took the castle of Tasynor"
                    int v = i - 1;
                    while (v >= 0 && StrikeFillers.Contains(w[v]) && i - v <= 4) v--;
                    if (v >= 0 && StrikeVerbs.Contains(w[v]))
                    {
                        bool asCaptive = TakeOfPeople.Contains(w[v]) && Range(w, i + 1, i + 7).Any(CaptiveWords.Contains);
                        if (!asCaptive) return $"writes \u00ab{w[v]} {who}\u00bb: {who} is a person, not a place to strike or take";
                    }
                    // "gathered in Tasynor" (not "faith in Tasynor")
                    if (i >= 1 && w[i - 1] == "in" && !(i >= 2 && TrustIn.Contains(w[i - 2]))) return $"writes \u00abin {who}\u00bb: {who} is a person, not a place";
                }
            }
            return null;
        }

        // ---- places

        private struct PlaceForm { public string[] Words; public PlaceFact Place; }

        private static readonly HashSet<string> PlaceKindWords = new HashSet<string> { "castle", "castillo", "fortress", "fortaleza", "fort", "keep", "town", "city", "ciudad" };

        /// <summary>Each place under its full name and under its name without "Castle" / "Castillo de", longest first.</summary>
        private static List<PlaceForm> PlaceForms(IEnumerable<PlaceFact> places)
        {
            var list = new List<PlaceForm>();
            var seen = new HashSet<string>();
            foreach (var p in places ?? Enumerable.Empty<PlaceFact>())
            {
                if (p == null || string.IsNullOrWhiteSpace(p.Name)) continue;
                var w = Words(Soft(p.Name)).ToList();
                void Add(List<string> f)
                {
                    if (f.Count == 0 || string.Concat(f).Length < 4) return;
                    if (seen.Add(string.Join(" ", f))) list.Add(new PlaceForm { Words = f.ToArray(), Place = p });
                }
                Add(w);
                var cut = w.ToList();
                while (cut.Count > 1 && PlaceKindWords.Contains(cut[cut.Count - 1])) cut.RemoveAt(cut.Count - 1);
                while (cut.Count > 1 && (PlaceKindWords.Contains(cut[0]) || cut[0] == "de" || cut[0] == "del" || cut[0] == "of" || cut[0] == "the")) cut.RemoveAt(0);
                Add(cut);
            }
            return list.OrderByDescending(f => f.Words.Length).ToList();
        }

        /// <summary>The place named at this word, and how many words its name takes. With <paramref name="possessive"/> "myzeas" counts as Myzea.</summary>
        private static PlaceFact PlaceAt(string[] w, int at, List<PlaceForm> forms, bool possessive, out int len)
        {
            len = 0;
            if (at < 0 || at >= w.Length) return null;
            foreach (var f in forms)
            {
                if (at + f.Words.Length > w.Length) continue;
                bool ok = true;
                for (int k = 0; k < f.Words.Length && ok; k++)
                {
                    string x = w[at + k], y = f.Words[k];
                    ok = x == y || (possessive && k == f.Words.Length - 1 && x == y + "s");
                }
                if (ok) { len = f.Words.Length; return f.Place; }
            }
            return null;
        }

        private static readonly HashSet<string> PortNouns = new HashSet<string>
        {
            "port", "ports", "harbour", "harbours", "harbor", "harbors", "docks", "dockyard", "dockyards", "shipyard", "shipyards", "wharf", "wharves",
            "quay", "quays", "seaport", "fleet", "fleets", "navy", "naval", "coastal", "seaside", "puerto", "puertos", "astillero", "astilleros", "muelle",
            "muelles", "d\u00e1rsena", "darsena", "flota", "flotas", "costera", "costero", "mar\u00edtima", "maritima", "mar\u00edtimo", "maritimo",
        };
        private static readonly HashSet<string> PortLinks = new HashSet<string> { "of", "at", "in", "de", "del", "en", "the", "la", "el", "town", "city", "castle", "ciudad", "castillo", "villa", "base" };
        private static readonly HashSet<string> NoPortWords = new HashSet<string> { "no", "not", "without", "lacks", "lack", "landlocked", "inland", "never", "nor", "sin", "interior", "nunca" };

        /// <summary>
        /// A town or castle called a port, a harbour, a naval base or given a fleet or docks, when it has no port:
        /// "the port of Myzea", "Myzea's harbour", "the coastal town of Myzea". "Myzea has no port" is fine. Villages
        /// are left alone. null when fine.
        /// </summary>
        public static string CheckPortClaims(string text, IEnumerable<PlaceFact> places)
        {
            if (string.IsNullOrWhiteSpace(text)) return null;
            var forms = PlaceForms((places ?? Enumerable.Empty<PlaceFact>()).Where(p => p.Kind == "town" || p.Kind == "castle"));
            if (forms.Count == 0) return null;
            foreach (var part in Sentences(text))
            {
                var w = Words(Soft(part.Key, true));
                for (int i = 0; i < w.Length; i++)
                {
                    if (!PortNouns.Contains(w[i])) continue;
                    if (Range(w, i - 3, i).Any(NoPortWords.Contains)) continue;
                    // "the port (city) of Myzea", "harbour at Myzea"
                    int j = i + 1;
                    while (j < w.Length && PortLinks.Contains(w[j]) && j - i <= 4) j++;
                    var p = PlaceAt(w, j, forms, false, out _);
                    // "Myzea's harbour", "Myzea docks"
                    if (p == null)
                        for (int k = Math.Max(0, i - 4); k < i && p == null; k++)
                        {
                            var q = PlaceAt(w, k, forms, true, out int len);
                            if (q != null && k + len == i) p = q;
                        }
                    if (p != null && !p.Port && !Range(w, i + 1, i + 3).Any(NoPortWords.Contains))
                        return $"calls {p.Name} a port (\u00ab{w[i]}\u00bb), and {p.Name} has no harbour";
                }
            }
            return null;
        }

        // ---- towns and castles said to have fallen

        private static readonly HashSet<string> TakeVerbs = new HashSet<string>
        {
            "took", "taken", "captured", "seized", "conquered", "stormed", "wrested", "won", "lost", "tom\u00f3", "tomo", "tomaron", "tomado", "tomada",
            "captur\u00f3", "capturo", "capturaron", "capturado", "conquist\u00f3", "conquisto", "conquistaron", "conquistado", "arrebat\u00f3", "arrebato", "arrebataron",
            "arrebatado", "perdi\u00f3", "perdio", "perdieron", "perdido", "gan\u00f3", "gano", "ganaron",
        };
        private static readonly HashSet<string> LoseVerbs = new HashSet<string> { "lost", "perdi\u00f3", "perdio", "perdieron", "perdido" };
        private static readonly HashSet<string> FellVerbs = new HashSet<string>
        {
            "fell", "fallen", "falls", "taken", "captured", "seized", "conquered", "stormed", "lost", "cay\u00f3", "cayo", "ca\u00eddo", "caido", "cae", "tomada",
            "tomado", "capturada", "capturado", "conquistada", "conquistado", "perdida", "perdido", "arrebatada", "arrebatado",
        };
        private static readonly HashSet<string> FallNouns = new HashSet<string> { "fall", "capture", "conquest", "loss", "seizure", "ca\u00edda", "caida", "toma", "conquista", "p\u00e9rdida", "perdida", "captura" };
        private static readonly HashSet<string> TakeFillers = new HashSet<string> { "the", "town", "city", "castle", "fortress", "stronghold", "keep", "of", "back", "la", "el", "ciudad", "castillo", "fortaleza", "de", "del" };
        private static readonly HashSet<string> FellFillers = new HashSet<string>
        {
            "has", "have", "had", "was", "were", "is", "are", "been", "finally", "now", "at", "last", "already", "itself", "ha", "han", "hab\u00eda", "habia",
            "fue", "finalmente", "ya", "por", "fin", "al", "town", "castle", "city", "sido", "|",
        };
        private static readonly HashSet<string> FallVerbs = new HashSet<string> { "fell", "fallen", "falls", "cayó", "cayo", "caído", "caido", "cae" };
        private static readonly HashSet<string> Passive = new HashSet<string> { "was", "were", "been", "is", "are", "fue", "sido", "|" };
        // "Myzea falls into despair" is no fall of Myzea
        private static readonly HashSet<string> NotFalling = new HashSet<string>
        {
            "into", "silent", "quiet", "ill", "sick", "behind", "apart", "short", "prey", "victim", "asleep", "in", "on", "upon", "over", "under", "en",
            "presa", "v\u00edctima", "victima", "enfermo", "enferma", "silencio", "sobre", "bajo",
        };
        private static readonly HashSet<string> ToRealm = new HashSet<string> { "to", "by", "a", "ante", "por" };
        // Next to a fall, these make it a fear, a threat, an attempt, a wish or a story of long ago. Talk and rumour do not:
        // a fall that never happened is false news however it is told.
        private static readonly HashSet<string> NotCaptureHedges = new HashSet<string>
        {
            "was", "were", "had", "its", "their", "his", "her", "last", "end", "ended", "ends", "ending", "over", "peace", "truce", "old", "before", "said",
            "say", "claim", "alleg", "accus", "suspect", "rumo", "whisper", "talk", "despite", "although", "without", "weary", "tired", "court", "offer",
            "propos", "envoy", "call", "demand", "break", "broke", "betray", "question", "doubt", "debate", "until", "used",
        };
        private static readonly string[] CaptureOnlyHedges =
        {
            "fail", "attempt", "try", "tried", "trying", "si", "no", "nunca", "jam\u00e1s", "jamas", "podr", "pued", "quiz\u00e1", "quiza", "teme", "temor",
            "miedo", "amenaz", "planea", "prepar", "pronto", "intent", "busc", "quier", "desea", "desean", "deseo", "esper", "casi", "anta\u00f1o", "hace",
            "a\u00f1os", "years", "centur", "generation", "siglo", "generacion", "generaci\u00f3n",
        };
        private static readonly HashSet<string> ExactCaptureHedges = new HashSet<string> { "si", "no", "hace", "a\u00f1os", "casi", "try" };
        private static readonly HashSet<string> LongAgo = new HashSet<string> { "ago", "years", "hace", "a\u00f1os", "anta\u00f1o", "siglos", "generations", "centuries", "generaciones" };

        private static bool CaptureHedge(string w)
        {
            foreach (var h in Hedges)
                if (!NotCaptureHedges.Contains(h) && (ExactHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal))) return true;
            foreach (var h in CaptureOnlyHedges)
                if (ExactCaptureHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>The nearest realm ending at or before <paramref name="at"/>, no further than <paramref name="reach"/> words back.</summary>
        private static string RealmBefore(List<Realm> realms, int at, int reach) =>
            realms.Where(r => r.To <= at && at - r.To <= reach).OrderByDescending(r => r.To).Select(r => r.Id).FirstOrDefault();

        /// <summary>The realm after "to", "by", "ante", "into the hands of" within a few words after <paramref name="from"/>.</summary>
        private static string RealmAfter(string[] w, List<Realm> realms, int from)
        {
            for (int j = from; j < w.Length && j <= from + 3; j++)
            {
                if (w[j] == "|") break;
                if (!ToRealm.Contains(w[j]) && w[j] != "hands" && w[j] != "manos") continue;
                var r = realms.FirstOrDefault(x => x.From > j && x.From <= j + 3);
                if (r.Id != null) return r.Id;
            }
            return null;
        }

        /// <summary>
        /// A town or castle said to have fallen, been taken, captured, seized, conquered or lost, in English or
        /// Spanish ("Vlandia took Myzea", "Myzea has fallen to the Vlandians", "the fall of Myzea", "Myzea cay\u00f3 en
        /// manos de Vlandia"), when the world has no such change of hands in the last days: <paramref name="changes"/>
        /// holds every one, with the kingdoms on both sides. When the text names the realm that took it (or lost it),
        /// that realm must be one of the two sides. Questions, conditions, negations, attempts, fears and falls long
        /// ago are let through; rumour and talk are not. null when fine.
        /// </summary>
        public static string CheckCaptureClaims(string text, IEnumerable<PlaceFact> places, IEnumerable<OwnerChange> changes, FactModel m, int days)
        {
            if (string.IsNullOrWhiteSpace(text) || m == null) return null;
            var forms = PlaceForms((places ?? Enumerable.Empty<PlaceFact>()).Where(p => p.Kind == "town" || p.Kind == "castle"));
            if (forms.Count == 0) return null;
            var all = (changes ?? Enumerable.Empty<OwnerChange>()).ToList();
            foreach (var part in Sentences(text))
            {
                if (part.Value == '?') continue;
                var w = Words(Soft(part.Key, true));
                if (w.Length < 2) continue;
                var opener = w.FirstOrDefault(x => x != "|");
                if (opener != null && (Suppositions.Contains(opener) || opener == "si")) continue;
                var realms = Realms(w, m);
                for (int i = 0; i < w.Length; i++)
                {
                    PlaceFact place = null; int start = i, end = i; string realm = null;
                    if (TakeVerbs.Contains(w[i]))
                    {
                        // "Vlandia took (the castle of) Myzea"
                        int j = i + 1;
                        while (j < w.Length && TakeFillers.Contains(w[j]) && j - i <= 4) j++;
                        place = PlaceAt(w, j, forms, false, out int len);
                        if (place != null)
                        {
                            end = j + len;
                            realm = RealmBefore(realms, i, 8) ?? RealmAfter(w, realms, end);
                        }
                    }
                    if (place == null && FallNouns.Contains(w[i]) && i + 1 < w.Length && (w[i + 1] == "of" || w[i + 1] == "de" || w[i + 1] == "del"))
                    {
                        // "the fall of Myzea (to Vlandia)", "Vlandia's capture of Myzea"
                        int j = i + 2;
                        while (j < w.Length && TakeFillers.Contains(w[j]) && j - i <= 5) j++;
                        place = PlaceAt(w, j, forms, false, out int len);
                        if (place != null)
                        {
                            end = j + len;
                            realm = RealmAfter(w, realms, end) ?? RealmBefore(realms, i, 3);
                        }
                    }
                    if (place == null)
                    {
                        // "Myzea (has) fallen (to Vlandia)", "Myzea was taken by Vlandia", "Myzea cay\u00f3 en manos de Vlandia"
                        var p = PlaceAt(w, i, forms, false, out int len);
                        if (p != null)
                        {
                            int j = i + len;
                            while (j < w.Length && FellFillers.Contains(w[j]) && j - i - len <= 3) j++;
                            if (j < w.Length && FellVerbs.Contains(w[j]))
                            {
                                bool falls = FallVerbs.Contains(w[j]);
                                bool hands = Range(w, j + 1, j + 4).Any(x => x == "hands" || x == "manos");
                                // "Myzea lost its trade", "Myzea has lost men" have Myzea doing it: a participle is a
                                // fall only in the passive ("was taken", "ha sido tomada") or set apart ("Myzea, taken by")
                                bool passive = falls || Range(w, i + len, j).Any(Passive.Contains);
                                if (passive && !(falls && j + 1 < w.Length && NotFalling.Contains(w[j + 1]) && !hands))
                                {
                                    place = p; start = i; end = j + 1;
                                    realm = RealmAfter(w, realms, end);
                                    i = j;
                                }
                            }
                        }
                    }
                    if (place == null) continue;
                    // a fear, a threat, an attempt, a condition, a negation, or long ago
                    string lead = string.Join(" ", w.Take(start));
                    if (HedgeBefore(" " + lead + " ", 5, CaptureHedge)) continue;
                    if (Range(w, end, end + 5).TakeWhile(x => x != "|" && !ClauseOpeners.Contains(x)).Any(LongAgo.Contains)) continue;
                    var ch = all.Where(c => string.Equals(c.Place, place.Name, StringComparison.OrdinalIgnoreCase)).ToList();
                    if (ch.Count == 0)
                        return $"says {place.Name} fell or was taken, and it has not changed hands in the last {days} days (it is held by {(string.IsNullOrEmpty(place.Owner) ? "no kingdom" : place.Owner)})";
                    if (realm != null && !ch.Any(c => c.To == realm || c.From == realm))
                    {
                        var last = ch.OrderBy(c => c.DaysAgo).First();
                        return $"says {place.Name} changed hands with {realm}; it really passed from {(string.IsNullOrEmpty(last.From) ? "no kingdom" : last.From)} to {(string.IsNullOrEmpty(last.To) ? "no kingdom" : last.To)}";
                    }
                }
            }
            return null;
        }

        // ============================================================ what a ruler says he has done

        private static readonly string[] WarDeeds =
        {
            " declare war ", " declares war ", " declared war ", " declaring war ", " war is declared ", " war has been declared ",
            " go to war ", " goes to war ", " march to war ", " now at war with ", " at war with ",
            " declaro la guerra ", " declaramos la guerra ", " declara la guerra ", " declaran la guerra ", " declarado la guerra ", " guerra está declarada ",
            " guerra esta declarada ", " vamos a la guerra ", " en guerra con ",
        };
        private static readonly string[] PeaceDeeds =
        {
            " make peace ", " made peace ", " makes peace ", " sign peace ", " signed peace ", " signs peace ", " sign the peace ", " signed the peace ",
            " signs the peace ", " peace is signed ", " peace is made ", " peace has been signed ", " peace has been made ", " accept peace ",
            " accepts peace ", " accepted peace ", " accept the peace ", " accepts the peace ", " accepted the peace ", " accept their peace ",
            " seal peace ", " sealed peace ", " seal the peace ", " sealed the peace ", " now at peace with ",
            " firmamos la paz ", " firmo la paz ", " firmado la paz ", " firma la paz ", " aceptamos la paz ", " acepto la paz ", " acepta la paz ",
            " aceptado la paz ", " hacemos las paces ", " hacemos la paz ", " sellamos la paz ", " paz está firmada ", " paz esta firmada ",
            " paz queda firmada ", " ahora en paz con ",
        };
        private static readonly string[] AllianceDeeds =
        {
            " seal an alliance ", " sealed an alliance ", " seals an alliance ", " seal the alliance ", " sealed the alliance ", " seal our alliance ",
            " accept the alliance ", " accepts the alliance ", " accepted the alliance ", " accept their alliance ", " accept your alliance ",
            " alliance is sealed ", " alliance has been sealed ", " alliance is forged ", " forge an alliance ", " forged an alliance ", " now allies ",
            " are now allied ", " now allied with ", " we are allies ",
            " sellamos una alianza ", " sellamos la alianza ", " sellado una alianza ", " aceptamos la alianza ", " acepto la alianza ",
            " aceptado la alianza ", " somos aliados ", " ahora aliados ", " alianza queda sellada ", " alianza está sellada ",
        };
        private static readonly HashSet<string> FirstPerson = new HashSet<string> { "i", "we", "my", "our", "us", "yo", "nosotros", "mi", "nuestro", "nuestra" };
        private static readonly HashSet<string> StatePhrases = new HashSet<string>
        {
            " now at war with ", " at war with ", " en guerra con ", " now at peace with ", " ahora en paz con ", " now allied with ", " are now allied ", " now allies ",
        };
        // Next to a deed these make it a threat, an intention, a wish or a condition: nothing said to be done
        private static readonly HashSet<string> NotDeedHedges = new HashSet<string> { "peace", "truce", "its", "their", "his", "her", "weary", "tired", "end", "ended", "ends", "ending", "over" };
        private static readonly string[] DeedOnlyHedges =
        {
            "to", "si", "no", "nunca", "jamás", "jamas", "podr", "pued", "quizá", "quiza", "dispuest", "listo", "listos", "amenaz", "advier", "advert",
            "prepar", "pronto", "quier", "desea", "deseo", "busc", "propon", "ofrec", "esper", "antes", "tal", "debe", "deber", "debemos", "seek", "ready",
            "willing", "intend", "aim",
        };
        private static readonly HashSet<string> ExactDeedHedges = new HashSet<string> { "to", "si", "no", "tal", "antes", "aim" };
        private static readonly HashSet<string> DeedConditions = new HashSet<string> { "if", "unless", "should", "si", "salvo", "menos", "until", "hasta" };

        private static bool DeedHedge(string w)
        {
            foreach (var h in Hedges)
                if (!NotDeedHedges.Contains(h) && (ExactHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal))) return true;
            foreach (var h in DeedOnlyHedges)
                if (ExactDeedHedges.Contains(h) ? w == h : w.StartsWith(h, StringComparison.Ordinal)) return true;
            return false;
        }

        /// <summary>A war declared, a peace signed or an alliance sealed that a ruler's words say is done, and the kingdom it is with (null when none is named).</summary>
        public class Deed
        {
            public string Kind;      // war | peace | alliance
            public string Target;    // kingdom id, or null
            public string Phrase;
        }

        /// <summary>
        /// Every war, peace or alliance a ruler's statement says is done, in English or Spanish: "I declare war on
        /// Sturgia", "we have signed the peace", "sellamos una alianza con Vlandia". Threats, intentions, wishes,
        /// conditions, negations and questions are not deeds ("I will declare war if", "we are ready to make peace").
        /// <paramref name="speaker"/> is the ruler's own kingdom: never the other side of a deed.
        /// </summary>
        public static List<Deed> ClaimedDeeds(string text, FactModel m, string speaker)
        {
            var res = new List<Deed>();
            if (string.IsNullOrWhiteSpace(text) || m == null) return res;
            foreach (var part in Sentences(text))
            {
                if (part.Value == '?') continue;
                string soft = Soft(part.Key, true);
                var opener = Words(soft).FirstOrDefault(x => x != "|");
                if (opener != null && (Suppositions.Contains(opener) || opener == "si")) continue;
                var ms = Mentions(soft, m).Where(x => x.Id != speaker).ToList();
                foreach (var kind in new[] { "war", "peace", "alliance" })
                {
                    var phrases = kind == "war" ? WarDeeds : kind == "peace" ? PeaceDeeds : AllianceDeeds;
                    foreach (var ph in phrases)
                        for (int at = soft.IndexOf(ph, StringComparison.Ordinal); at >= 0; at = soft.IndexOf(ph, at + 1, StringComparison.Ordinal))
                        {
                            if (HedgeBefore(soft.Substring(0, at + 1), 4, DeedHedge)) continue;
                            int end = at + ph.Length - 1;
                            if (Words(soft.Substring(end)).TakeWhile(x => x != "|").Take(5).Any(DeedConditions.Contains)) continue;
                            // the other side: named right after the phrase, or else before it in the same sentence
                            var after = ms.FirstOrDefault(x => x.Start >= end && x.Start <= end + NearChars * 2);
                            var before = ms.LastOrDefault(x => x.End <= at + 1);
                            // "Vlandia, now at war with the Empire": a state whose subject is another realm is not the speaker's
                            if (StatePhrases.Contains(ph) && before.Id != null) continue;
                            // "Vlandia declared war on the Empire": another realm right before the deed is the one doing it
                            // ("Sturgia, I declare war on you" is the speaker's: a first person stands between)
                            if (before.Id != null)
                            {
                                var gap = Words(soft.Substring(before.End, at + 1 - before.End)).Where(x => x != "|").ToList();
                                if (gap.Count <= 3 && !gap.Any(FirstPerson.Contains)) continue;
                            }
                            string target = after.Id ?? before.Id;
                            if (!res.Any(d => d.Kind == kind && d.Target == target)) res.Add(new Deed { Kind = kind, Target = target, Phrase = ph.Trim() });
                        }
                }
            }
            return res;
        }
    }

    /// <summary>A town, castle or village as the checks see it.</summary>
    public class PlaceFact
    {
        public string Name;
        /// <summary>town | castle | village</summary>
        public string Kind;
        /// <summary>Id of the kingdom that holds it, "" for none.</summary>
        public string Owner = "";
        public bool Port;
    }

    /// <summary>A town or castle that changed hands lately, with the kingdoms on both sides ("" for none).</summary>
    public class OwnerChange
    {
        public string Place;
        public string From = "", To = "";
        public double DaysAgo;
    }
}
