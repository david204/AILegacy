using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;

namespace AILegacy.Dialogue
{
    /// <summary>
    /// Reads a spoken line for a deed stated as done by the speaker, right now and in the first person: "I swear
    /// fealty to you", "te entrego el castillo", "here is your gold". Only English and Spanish, and only the plain
    /// cases: a question, a condition, a refusal, a wish or anything still to come is not a deed. Anything unclear
    /// is let through. No game types in here, so it can be run outside the game.
    /// </summary>
    public static class PromiseCheck
    {
        public class Deed
        {
            /// <summary>fealty, fief, gold, marriage, join, peace, war, abdicate, free, leave</summary>
            public string Kind;
            /// <summary>The words that state it, as spoken.</summary>
            public string Quote;
        }

        private const RegexOptions Opt = RegexOptions.IgnoreCase | RegexOptions.CultureInvariant;
        private static Regex R(string p) => new Regex(p, Opt);

        private static readonly KeyValuePair<string, Regex[]>[] Patterns =
        {
            new KeyValuePair<string, Regex[]>("fealty", new[]
            {
                R(@"\bi (?:hereby |now |do |solemnly |humbly |gladly )*(?:swear|pledge|give) (?:you |to you |thee )?(?:my |mine |our )?(?:fealty|allegiance|oath of fealty|oath of loyalty)\b"),
                R(@"\bi (?:hereby |now |do )*bend (?:the|my) knee\b"),
                R(@"\bi am (?:now |henceforth )?your (?:vassal|sworn vassal|sworn man|sworn sword)\b"),
                R(@"\b(?:te |os |le )?juro (?:mi )?(?:lealtad|fidelidad|vasallaje)\b"),
                R(@"\b(?:te |os |le )?rindo (?:pleitesía|vasallaje|homenaje)\b"),
                R(@"\b(?:hinco|doblo) la rodilla\b"),
                R(@"\bsoy (?:ya |ahora |desde hoy )?(?:tu|vuestro|su) vasallo\b"),
            }),
            new KeyValuePair<string, Regex[]>("fief", new[]
            {
                R(@"\bi (?:hereby |now |do )*(?:grant|give|cede|yield|surrender|hand|sign) (?:you |over |to you |thee )*[^.!?;]{0,40}?\b(?:castle|town|city|fief|keep|fortress|stronghold)\b"),
                R(@"\b(?:castle|town|city|fief|keep|fortress|stronghold)(?: of [\w' ]{2,30}?)? is (?:now |hereby |henceforth )?yours\b"),
                R(@"\b(?:te |os |le )?(?:cedo|entrego|concedo|otorgo|doy) [^.!?;]{0,40}?\b(?:castillo|ciudad|villa|feudo|fortaleza|plaza)\b"),
                R(@"\b(?:castillo|ciudad|villa|feudo|fortaleza|plaza)(?: de [\wáéíóúüñ' ]{2,30}?)? es (?:ya |ahora |desde hoy )?(?:tuy[oa]|vuestr[oa]|suy[oa])\b"),
            }),
            new KeyValuePair<string, Regex[]>("gold", new[]
            {
                R(@"\bi (?:hereby |now )*(?:give|hand|pay|toss|pass) (?:you |thee |over )+[^.!?;]{0,30}?\b(?:denars?|gold|coins?|silver|purse)\b"),
                R(@"\bhere (?:is|are) (?:the|your|my) [^.!?;]{0,25}?\b(?:denars?|gold|coins?|silver|payment|purse)\b"),
                R(@"\btake (?:the|this|these|your) [^.!?;]{0,25}?\b(?:denars?|gold|coins?|silver|purse)\b"),
                R(@"\b(?:te |os |le )(?:doy|entrego|pago) [^.!?;]{0,30}?\b(?:denarios?|oro|monedas?|plata|bolsa)\b"),
                R(@"\baqu[ií] (?:tienes|ten[ée]is|tiene|est[áa]n?) [^.!?;]{0,25}?\b(?:denarios?|oro|monedas?|plata|pago|bolsa)\b"),
                R(@"\btomad? (?:el|la|los|las|este|esta|estos|estas|tu|tus|vuestro|vuestra) [^.!?;]{0,25}?\b(?:denarios?|oro|monedas?|plata|bolsa)\b"),
            }),
            new KeyValuePair<string, Regex[]>("marriage", new[]
            {
                R(@"\bi (?:take|accept) you (?:as|for|to be) my (?:lord |lady )?(?:husband|wife|spouse)\b"),
                R(@"\bi (?:gladly |humbly |do )*accept your (?:hand(?: in marriage)?|marriage proposal|proposal of marriage|offer of marriage)\b"),
                R(@"\bi am (?:now )?your (?:husband|wife)\b"),
                R(@"\bte (?:tomo|acepto|recibo) (?:por|como) (?:esposo|esposa|marido|mujer)\b"),
                R(@"\bacepto (?:casarme contigo|tu mano|vuestra mano|tu propuesta de matrimonio|ser tu espos[oa])\b"),
                R(@"\bme caso contigo\b"),
            }),
            new KeyValuePair<string, Regex[]>("join", new[]
            {
                R(@"\bi (?:hereby |now |gladly )*join (?:you\b|your (?:band|company|party|clan|house|cause|side|army|ranks)\b)"),
                R(@"\bi am (?:with you|yours|your man|your woman) (?:now|from this day|from today|from now on)\b"),
                R(@"\bi ride with you (?:now|from this day|from today|from now on)\b"),
                R(@"\bme uno a (?:ti|vos|vosotros|usted)\b"),
                R(@"\bme uno a (?:tu|vuestr[oa]|su|tus|vuestr[oa]s|sus) (?:compañía|grupo|banda|clan|casa|causa|hueste|ejército|partida|bando|filas)\b"),
                R(@"\bcabalgo contigo desde (?:hoy|ahora)\b"),
                R(@"\bsoy (?:de los tuyos|tu hombre|tu mujer) desde (?:hoy|ahora)\b"),
            }),
            new KeyValuePair<string, Regex[]>("peace", new[]
            {
                R(@"\b(?:i|we) (?:hereby |now |gladly )*(?:make|accept|sign|agree to|grant) (?:the |this |a |your |you |you a )*(?:peace|truce)\b"),
                R(@"\b(?:there is|we have|you have) (?:a )?(?:peace|truce) between us\b"),
                R(@"\b(?:firmo|acepto|hago|concedo|firmamos|aceptamos|hacemos) (?:la |esta |una |tu |vuestra |las )?(?:paz|paces|tregua)\b"),
                R(@"\bhay (?:paz|tregua) entre (?:nosotros|tú y yo|vos y yo)\b"),
            }),
            new KeyValuePair<string, Regex[]>("war", new[]
            {
                R(@"\b(?:i|we) (?:hereby |now )*declare war\b"),
                R(@"\b(?:te |os |le )?(?:declaro|declaramos) la guerra\b"),
            }),
            new KeyValuePair<string, Regex[]>("abdicate", new[]
            {
                R(@"\bi (?:hereby |now )*(?:abdicate|renounce (?:my|the) (?:crown|throne)|yield (?:my|the) (?:crown|throne) to you|give you (?:my|the) crown)\b"),
                R(@"\babdico\b"),
                R(@"\brenuncio a (?:mi|la|el) (?:corona|trono)\b"),
                R(@"\bte (?:cedo|entrego) (?:mi|la) corona\b"),
            }),
            new KeyValuePair<string, Regex[]>("free", new[]
            {
                R(@"\bi (?:hereby |now )*(?:release|free) (?:you|him|her|them)\b"),
                R(@"\bi set (?:you|him|her|them) free\b"),
                R(@"\b(?:you are|he is|she is|they are) free to go\b"),
                R(@"\b(?:te|os|lo|la|los|las) (?:libero|suelto|dejo libres?|dejo marchar)\b"),
                R(@"\b(?:quedas|queda|quedáis|quedan) libres?\b"),
            }),
            new KeyValuePair<string, Regex[]>("leave", new[]
            {
                R(@"\bi (?:hereby |now )*renounce (?:my )?(?:oath to|allegiance to|fealty to|king|queen|liege|ruler)\b"),
                R(@"\bi (?:hereby |now )*(?:leave|abandon|forsake) (?:my )?(?:king|queen|liege|ruler)\b"),
                R(@"\brenuncio a mi (?:rey|reina|señor|señora|juramento|vasallaje)\b"),
                R(@"\babandono a mi (?:rey|reina|señor|señora)\b"),
            }),
        };

        // Anywhere before the deed, in its own sentence: it is a condition, a refusal, a wish, something still to
        // come or somebody else's words.
        private static readonly Regex Before = R(
            @"\b(?:if|when|whenever|once|unless|until|before|after|should|would|could|might|may|will|shall|won't|wouldn't|not|never|cannot|can't|don't|didn't|"
            + @"want|wants|wish|ready|willing|let|ask|asks|asked|demand|demands|say|says|said|tell|told|expect|think|suppose|perhaps|maybe|why|how|"
            + @"si|cuando|hasta|antes|después|tras|salvo|no|nunca|jamás|ni|tampoco|quizá|quizás|acaso|podría|podrías|querría|quisiera|quieres|queréis|quiere|"
            + @"pides|pedís|pide|exiges|exigís|exige|dices|decís|dice|dijiste|esperas|crees|pretendes|voy|vamos|iba)\b|\b\w+'ll\b|\b\w+n't\b|\ben cuanto\b|\buna vez\b|\ba menos\b|\btal vez\b");

        // Right after it: "... if you free me", "... to no one", "... a cambio de mi libertad"
        private static readonly Regex After = R(
            @"\b(?:if|when|once|unless|until|only|provided|as soon|in exchange|in return|no one|nobody|none|neither|someday|one day|tomorrow|later|"
            + @"si|cuando|siempre que|en cuanto|una vez|solo|sólo|a cambio|nadie|ningún|ninguno|ninguna|algún día|mañana|luego|más adelante)\b");

        private const int AfterReach = 45;

        /// <summary>The deeds this line states as done, one per kind at most, in the order they are spoken.</summary>
        public static List<Deed> Find(string reply)
        {
            var found = new List<Deed>();
            if (string.IsNullOrWhiteSpace(reply)) return found;
            // gestures and stage directions between asterisks or brackets are not his words
            string text = Regex.Replace(reply, @"\*[^*]{0,300}\*|\([^)]{0,300}\)|\[[^\]]{0,300}\]", " ");
            text = text.Replace('’', '\'').Replace("I'm ", "I am ").Replace("i'm ", "i am ");
            foreach (var sentence in Sentences(text))
            {
                if (sentence.Value == '?' || sentence.Key.IndexOf('¿') >= 0) continue;
                // quoted speech is somebody else's, or his own from another day
                if (sentence.Key.IndexOfAny(new[] { '"', '“', '”', '«', '»' }) >= 0) continue;
                string s = sentence.Key;
                foreach (var kind in Patterns)
                {
                    if (found.Any(d => d.Kind == kind.Key)) continue;
                    foreach (var rx in kind.Value)
                    {
                        var m = rx.Match(s);
                        if (!m.Success) continue;
                        if (Before.IsMatch(s.Substring(0, m.Index))) continue;
                        int end = m.Index + m.Length;
                        if (After.IsMatch(s.Substring(end, Math.Min(AfterReach, s.Length - end)))) continue;
                        found.Add(new Deed { Kind = kind.Key, Quote = m.Value.Trim() });
                        break;
                    }
                }
            }
            return found;
        }

        private static List<KeyValuePair<string, char>> Sentences(string text)
        {
            var list = new List<KeyValuePair<string, char>>();
            int from = 0;
            for (int i = 0; i <= text.Length; i++)
            {
                char c = i < text.Length ? text[i] : '.';
                if (i < text.Length && c != '.' && c != ';' && c != '!' && c != '?' && c != '\n' && c != '…') continue;
                if (i > from) list.Add(new KeyValuePair<string, char>(text.Substring(from, i - from), c));
                from = i + 1;
            }
            return list;
        }
    }
}
