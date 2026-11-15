using System;
using System.Collections.Generic;
using System.Text;
using Newtonsoft.Json.Linq;

namespace AILegacy.Llm
{
    /// <summary>
    /// Turns the bytes of a Server-Sent Events response into the payloads of its "data:" lines.
    /// Bytes can be cut anywhere: inside a multibyte character, inside a line, between "\r" and "\n".
    /// </summary>
    internal sealed class SseReader
    {
        private readonly Decoder _decoder = new UTF8Encoding(false, false).GetDecoder();
        private readonly StringBuilder _line = new StringBuilder();
        private readonly StringBuilder _data = new StringBuilder();
        private char[] _chars = new char[8200];
        private bool _lastWasCr, _hasData;

        /// <summary>Lines that were not SSE fields (a provider that ignored "stream" and sent plain JSON). Capped.</summary>
        public readonly StringBuilder Other = new StringBuilder();

        public void Feed(byte[] buffer, int count, List<string> events)
        {
            int max = _decoder.GetCharCount(buffer, 0, count, false);
            if (_chars.Length < max) _chars = new char[max + 64];
            int n = _decoder.GetChars(buffer, 0, count, _chars, 0, false);
            for (int i = 0; i < n; i++)
            {
                char c = _chars[i];
                if (c == '\n')
                {
                    if (_lastWasCr) { _lastWasCr = false; continue; }      // second half of "\r\n"
                    EndLine(events);
                }
                else if (c == '\r') { _lastWasCr = true; EndLine(events); }
                else { _lastWasCr = false; _line.Append(c); }
            }
        }

        /// <summary>The connection closed: hand over whatever was left without its closing blank line.</summary>
        public void Finish(List<string> events)
        {
            if (_line.Length > 0) EndLine(events);
            Dispatch(events);
        }

        private void EndLine(List<string> events)
        {
            string line = _line.ToString();
            _line.Clear();
            if (line.Length > 0 && line[0] == '﻿') line = line.Substring(1);
            if (line.Length == 0) { Dispatch(events); return; }
            if (line[0] == ':') return;                                     // comment, used as a keep-alive
            int colon = line.IndexOf(':');
            string field = colon < 0 ? line : line.Substring(0, colon);
            if (field == "data")
            {
                string value = line.Substring(colon + 1);
                if (value.Length > 0 && value[0] == ' ') value = value.Substring(1);
                // Some servers leave out the blank line between events: a new object after a finished one is a new event
                if (_hasData && LooksFinished(_data) && (value.StartsWith("{") || value.StartsWith("[DONE]"))) Dispatch(events);
                if (_hasData) _data.Append('\n');
                _data.Append(value);
                _hasData = true;
            }
            else if (field == "event" || field == "id" || field == "retry") { }
            else if (Other.Length < 262144) Other.Append(line).Append('\n');
        }

        private static bool LooksFinished(StringBuilder sb)
        {
            for (int i = sb.Length - 1; i >= 0; i--)
            {
                char c = sb[i];
                if (c == ' ' || c == '\t') continue;
                return c == '}' || c == ']';
            }
            return false;
        }

        private void Dispatch(List<string> events)
        {
            if (!_hasData) return;
            events.Add(_data.ToString());
            _data.Clear();
            _hasData = false;
        }
    }

    /// <summary>Puts together the chunks of an OpenAI-compatible streamed completion.</summary>
    internal sealed class StreamAssembler
    {
        public readonly StringBuilder Content = new StringBuilder();
        /// <summary>"data: [DONE]" was received.</summary>
        public bool Done;
        public string FinishReason = "";
        public string Error;
        public bool HasUsage;
        public int PromptTokens, CachedTokens, CompletionTokens;
        public int Events, Unreadable;

        public bool Complete => Done || FinishReason.Length > 0;

        /// <summary>Takes one event payload and returns the reply text it added (empty if none).</summary>
        public string OnEvent(string data)
        {
            data = (data ?? "").Trim();
            if (data.Length == 0) return "";
            if (data == "[DONE]") { Done = true; return ""; }
            JObject j;
            try { j = JObject.Parse(data); }
            catch { Unreadable++; return ""; }
            Events++;

            var error = j["error"];
            if (error != null && error.Type != JTokenType.Null)
            {
                Error = error.Type == JTokenType.Object ? ((string)error["message"] ?? error.ToString(Newtonsoft.Json.Formatting.None)) : error.ToString();
                return "";
            }

            var usage = j["usage"];
            if (usage != null && usage.Type == JTokenType.Object)
            {
                HasUsage = true;
                PromptTokens = Int(usage["prompt_tokens"]) ?? PromptTokens;
                CompletionTokens = Int(usage["completion_tokens"]) ?? CompletionTokens;
                CachedTokens = Int((usage["prompt_tokens_details"] as JObject)?["cached_tokens"]) ?? Int(usage["cached_tokens"]) ?? Int(usage["prompt_cache_hit_tokens"]) ?? CachedTokens;
            }

            var choice = (j["choices"] as JArray)?.Count > 0 ? j["choices"][0] as JObject : null;
            if (choice == null) return "";
            var finish = choice["finish_reason"];
            if (finish != null && finish.Type == JTokenType.String && ((string)finish).Length > 0) FinishReason = (string)finish;

            string added = Text((choice["delta"] as JObject)?["content"]);
            // A few servers send the whole message in one chunk instead of deltas
            if (added.Length == 0 && Content.Length == 0) added = Text((choice["message"] as JObject)?["content"]);
            if (added.Length > 0) Content.Append(added);
            return added;
        }

        private static int? Int(JToken t)
        {
            if (t == null || (t.Type != JTokenType.Integer && t.Type != JTokenType.Float)) return null;
            try { return (int)t; } catch { return null; }
        }

        private static string Text(JToken content)
        {
            if (content == null) return "";
            if (content.Type == JTokenType.String) return (string)content ?? "";
            if (content.Type != JTokenType.Array) return "";
            var sb = new StringBuilder();
            foreach (var part in content)
                if (part is JObject o && o["text"]?.Type == JTokenType.String) sb.Append((string)o["text"]);
            return sb.ToString();
        }
    }

    /// <summary>
    /// Reads a JSON reply as it is being written and pulls out the value of its top-level "reply" field, decoded.
    /// It only ever hands over whole characters: an escape or a surrogate pair cut in half waits for its other half.
    /// The field does not have to come first, and text before the object (a think block, a code fence) is skipped.
    /// </summary>
    internal sealed class ReplyFieldScanner
    {
        private enum State { Outside, InString, InStringEscape, InTarget }

        private readonly StringBuilder _out = new StringBuilder();
        private readonly StringBuilder _key = new StringBuilder();
        private readonly StringBuilder _held = new StringBuilder();
        private readonly StringBuilder _tail = new StringBuilder();
        private State _state = State.Outside;
        private int _depth;
        private string _lastString;
        private bool _expectValue, _quoteHeld, _inThink;
        private int _escape;            // 0 none, 1 just after the backslash, 2 reading the four hex digits
        private int _hexCount, _hexValue;
        private char _highSurrogate;
        private string _cached = "";
        private bool _dirty;

        /// <summary>The opening quote of the reply has been seen.</summary>
        public bool Started { get; private set; }
        /// <summary>The closing quote of the reply has been seen.</summary>
        public bool Ended { get; private set; }

        /// <summary>The reply so far.</summary>
        public string Text
        {
            get
            {
                if (_dirty) { _cached = _out.ToString(); _dirty = false; }
                return _cached;
            }
        }

        public void Feed(string chunk)
        {
            if (string.IsNullOrEmpty(chunk) || Ended) return;
            foreach (char c in chunk)
            {
                Step(c);
                if (Ended) return;
            }
        }

        private static bool IsTargetKey(string key) => key == "reply" || key == "response";

        private void Step(char c)
        {
            switch (_state)
            {
                case State.Outside:
                    if (_depth == 0 && SkipPreamble(c)) return;
                    StepOutside(c);
                    return;

                case State.InString:
                    if (c == '\\') _state = State.InStringEscape;
                    else if (c == '"') { _state = State.Outside; _lastString = _depth == 1 ? _key.ToString() : null; }
                    else if (_depth == 1 && _key.Length < 40) _key.Append(c);
                    return;

                case State.InStringEscape:
                    _state = State.InString;
                    return;

                case State.InTarget:
                    StepTarget(c);
                    return;
            }
        }

        /// <summary>Before the object starts: swallow a think block so a brace inside it is not taken for the reply.</summary>
        private bool SkipPreamble(char c)
        {
            if (_tail.Length >= 8) _tail.Remove(0, 1);
            _tail.Append(char.ToLowerInvariant(c));
            string t = _tail.ToString();
            if (_inThink)
            {
                if (t.EndsWith("</think>")) { _inThink = false; _tail.Clear(); }
                return true;
            }
            if (t.EndsWith("<think>")) { _inThink = true; return true; }
            return false;
        }

        private void StepOutside(char c)
        {
            switch (c)
            {
                case '"':
                    if (_depth == 0) return;        // prose before the object, its quotes mean nothing
                    if (_expectValue) { _expectValue = false; _state = State.InTarget; Started = true; }
                    else { _state = State.InString; _key.Clear(); }
                    return;
                case '{': case '[':
                    _depth++; _expectValue = false; _lastString = null;
                    return;
                case '}': case ']':
                    if (_depth > 0) _depth--;
                    _expectValue = false; _lastString = null;
                    return;
                case ':':
                    _expectValue = _depth == 1 && !Started && _lastString != null && IsTargetKey(_lastString);
                    _lastString = null;
                    return;
                case ',':
                    _expectValue = false; _lastString = null;
                    return;
                case ' ': case '\t': case '\r': case '\n':
                    return;
                default:
                    _expectValue = false;       // the value is not a string (null, a number)
                    return;
            }
        }

        private void StepTarget(char c)
        {
            if (_quoteHeld)
            {
                // A quote ends the reply only if what follows closes the field. Otherwise the model forgot to escape it.
                if (c == ' ' || c == '\t' || c == '\r' || c == '\n') { _held.Append(c); return; }
                _quoteHeld = false;
                if (c == ',' || c == '}') { Ended = true; _state = State.Outside; return; }
                _out.Append('"').Append(_held.ToString());
                _held.Clear();
                _dirty = true;
            }

            if (_escape == 1)
            {
                _escape = 0;
                switch (c)
                {
                    case 'n': Emit('\n'); return;
                    case 't': Emit('\t'); return;
                    case 'r': return;
                    case 'b': case 'f': return;
                    case 'u': _escape = 2; _hexCount = 0; _hexValue = 0; return;
                    default: Emit(c); return;       // \" \\ \/ and anything a model made up
                }
            }
            if (_escape == 2)
            {
                int digit = c >= '0' && c <= '9' ? c - '0' : c >= 'a' && c <= 'f' ? c - 'a' + 10 : c >= 'A' && c <= 'F' ? c - 'A' + 10 : -1;
                if (digit < 0) { _escape = 0; StepTarget(c); return; }      // broken escape: drop it, keep the character
                _hexValue = _hexValue * 16 + digit;
                if (++_hexCount == 4) { _escape = 0; Emit((char)_hexValue); }
                return;
            }

            if (c == '\\') { _escape = 1; return; }
            if (c == '"') { _quoteHeld = true; _held.Clear(); return; }
            Emit(c);
        }

        private void Emit(char c)
        {
            if (_highSurrogate != '\0')
            {
                char high = _highSurrogate;
                _highSurrogate = '\0';
                if (char.IsLowSurrogate(c)) { _out.Append(high).Append(c); _dirty = true; return; }
                // a lone half is dropped
            }
            if (char.IsHighSurrogate(c)) { _highSurrogate = c; return; }
            if (char.IsLowSurrogate(c)) return;
            _out.Append(c);
            _dirty = true;
        }
    }
}
