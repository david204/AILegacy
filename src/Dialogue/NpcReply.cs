using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using AILegacy.Memory;
using Newtonsoft.Json.Linq;

namespace AILegacy.Dialogue
{
    internal class NpcReply
    {
        public string Reply = "";
        public int RelationChange;
        public int TrustChange;
        public int RomanceChange;
        public int TensionChange;
        public World.QuestRequest Quest;
        public string SignificantEvent = "";
        public string Remember = "";
        public bool EndConversation;
        public string PlayerIntroducedAs = "";
        public List<string> Actions = new List<string>();
        public CharacterProfile Profile;
        public string RumorText = "";
        public string RumorTitleLocal = "";
        public string RumorTextLocal = "";
        public int RumorImportance;
        public string Thoughts = "";
        public string Mood = "";
        public string MoodReason = "";

        /// <summary>Lee la respuesta JSON del modelo; si no es JSON válido, usa el texto tal cual.</summary>
        public static NpcReply Parse(string raw)
        {
            var r = new NpcReply();
            if (string.IsNullOrWhiteSpace(raw)) return r;

            var json = ExtractJson(raw);
            if (json != null)
            {
                try
                {
                    JObject j;
                    try { j = JObject.Parse(json); }
                    catch { j = JObject.Parse(Repair(json)); }   // JSON casi válido: campo sin valor, coma de más, sin cerrar
                    r.Reply = Str(j["reply"]) ?? Str(j["response"]) ?? Str(j["text"]) ?? "";
                    r.RelationChange = ToInt(j["relation_change"]);
                    r.TrustChange = ToInt(j["trust_change"]);
                    r.RomanceChange = ToInt(j["romance_change"]);
                    r.TensionChange = ToInt(j["tension_change"]);
                    r.Quest = World.QuestRequest.Parse(j["quest"] ?? j["quest_action"]);
                    r.SignificantEvent = j["significant_event"]?.Type == JTokenType.String ? (Str(j["significant_event"]) ?? "") : "";
                    r.Remember = Str(j["remember"]) ?? "";
                    r.EndConversation = ToBool(j["end_conversation"]);
                    r.PlayerIntroducedAs = Str(j["player_introduced_as"]) ?? "";
                    r.Thoughts = Str(j["thoughts"]) ?? "";
                    r.Mood = Str(j["mood"]) ?? "";
                    r.MoodReason = Str(j["mood_reason"]) ?? "";

                    if (j["actions"] is JArray arr)
                        r.Actions = arr.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).ToList();
                    else if (j["actions"]?.Type == JTokenType.String)
                        r.Actions = j["actions"].ToString().Split(new[] { ';', '|' }, StringSplitOptions.RemoveEmptyEntries).ToList();

                    if (j["profile"] is JObject p)
                    {
                        r.Profile = new CharacterProfile
                        {
                            GivenName = Str(p["given_name"]) ?? Str(p["name"]) ?? "",
                            Personality = Str(p["personality"]) ?? "",
                            Backstory = Str(p["backstory"]) ?? "",
                            SpeechStyle = Str(p["speech_style"]) ?? "",
                            Goals = Str(p["goals"]) ?? "",
                            Secret = Str(p["secret"]) ?? "",
                            Drives = Str(p["drives"]) ?? "",
                            Values = Str(p["values"]) ?? "",
                            SocialBehavior = Str(p["social_behavior"]) ?? "",
                            CognitiveStyle = Str(p["cognitive_style"]) ?? "",
                            Religion = Str(p["religion"]) ?? "",
                            Reputation = Str(p["reputation"]) ?? "",
                        };
                        if (p["sample_lines"] is JArray lines)
                            r.Profile.SampleLines = lines.Select(x => x?.ToString()).Where(x => !string.IsNullOrWhiteSpace(x)).Take(5).ToList();
                        if (r.Profile.IsEmpty) r.Profile = null;
                    }

                    if (j["rumor"] is JObject rumor)
                    {
                        r.RumorText = Str(rumor["text"]) ?? "";
                        r.RumorImportance = ToInt(rumor["importance"]);
                        r.RumorTitleLocal = Str(rumor["title_local"]) ?? "";
                        r.RumorTextLocal = Str(rumor["text_local"]) ?? "";
                    }
                    else if (j["rumor"]?.Type == JTokenType.String)
                    {
                        r.RumorText = Str(j["rumor"]) ?? "";
                        r.RumorImportance = 1;
                    }

                    if (r.Reply.Length > 0) return r;
                }
                catch { /* cae al modo texto */ }
            }

            // JSON irrecuperable: al menos se rescata lo que dice (nunca se enseña el JSON en bruto al jugador)
            if (json != null)
            {
                var m = Regex.Match(raw, "\"(?:reply|response)\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"", RegexOptions.Singleline);
                if (m.Success)
                {
                    try { r.Reply = (string)JToken.Parse("\"" + m.Groups[1].Value + "\""); } catch { r.Reply = m.Groups[1].Value; }
                    var acts = Regex.Match(raw, "\"actions\"\\s*:\\s*\\[(.*?)\\]", RegexOptions.Singleline);
                    if (acts.Success)
                        r.Actions = Regex.Matches(acts.Groups[1].Value, "\"((?:\\\\.|[^\"\\\\])*)\"").Cast<Match>().Select(x => x.Groups[1].Value).Where(x => x.Length > 0).ToList();
                    r.EndConversation = Regex.IsMatch(raw, "\"end_conversation\"\\s*:\\s*true");
                    if (r.Reply.Length > 0) return r;
                }
                r.Reply = "";
                return r;   // ChatSession pone un gesto neutro; mejor eso que un volcado de JSON
            }

            // Sin JSON: todo el texto es lo que dice
            r.Reply = Regex.Replace(raw, @"^```\w*|```$", "", RegexOptions.Multiline).Trim();
            return r;
        }

        /// <summary>Arregla los fallos típicos de un modelo al escribir JSON.</summary>
        private static string Repair(string json)
        {
            // "clave": seguido de , } ] → null
            json = Regex.Replace(json, "(\"[A-Za-z_]+\"\\s*:)\\s*(?=[,}\\]])", "$1 null");
            // comas sobrantes antes de cerrar
            json = Regex.Replace(json, ",\\s*([}\\]])", "$1");
            // llaves o corchetes sin cerrar (respuesta cortada)
            int braces = 0, brackets = 0; bool inStr = false, esc = false;
            foreach (char c in json)
            {
                if (esc) { esc = false; continue; }
                if (c == '\\') { esc = true; continue; }
                if (c == '"') { inStr = !inStr; continue; }
                if (inStr) continue;
                if (c == '{') braces++; else if (c == '}') braces--;
                else if (c == '[') brackets++; else if (c == ']') brackets--;
            }
            if (inStr) json += "\"";
            while (brackets-- > 0) json += "]";
            while (braces-- > 0) json += "}";
            return json;
        }

        private static string Str(JToken t)
        {
            if (t == null || t.Type == JTokenType.Null) return null;
            var s = t.ToString().Trim();
            return s.Length == 0 ? null : s;
        }

        private static string ExtractJson(string s)
        {
            int a = s.IndexOf('{');
            int b = s.LastIndexOf('}');
            if (a < 0) return null;
            // sin llave de cierre = respuesta cortada: se coge hasta el final y Repair la cierra
            return b > a ? s.Substring(a, b - a + 1) : s.Substring(a);
        }

        private static int ToInt(JToken t)
        {
            if (t == null) return 0;
            if (t.Type == JTokenType.Integer) return (int)t;
            if (t.Type == JTokenType.Float) return (int)Math.Round((double)t);
            return int.TryParse(t.ToString().Replace("+", "").Trim(), out var v) ? v : 0;
        }

        private static bool ToBool(JToken t)
        {
            if (t == null) return false;
            if (t.Type == JTokenType.Boolean) return (bool)t;
            var s = t.ToString().Trim().ToLowerInvariant();
            return s == "true" || s == "yes" || s == "1";
        }
    }
}
