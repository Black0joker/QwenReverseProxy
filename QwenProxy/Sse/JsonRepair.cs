using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace QwenProxy.Sse;

/// <summary>
/// C# port of jsonrepair.py (itself a port of jsonrepair v3.5.0).
/// Repairs invalid JSON documents.
/// </summary>
public sealed class JsonRepairException : Exception
{
    public int Position { get; }

    public JsonRepairException(string message, int position)
        : base($"{message} at position {position}")
    {
        Position = position;
    }
}

public static partial class JsonRepair
{
    private const int CodeComma = ',';
    private const int CodeOpeningBrace = '{';
    private const int CodeClosingBrace = '}';
    private const int CodeOpeningBracket = '[';
    private const int CodeClosingBracket = ']';
    private const int CodeColon = ':';
    private const int CodeSlash = '/';
    private const int CodeAsterisk = '*';
    private const int CodeNewline = '\n';
    private const int CodeBackslash = '\\';
    private const int CodeDoubleQuote = '"';
    private const int CodePlus = '+';
    private const int CodeMinus = '-';
    private const int CodeDot = '.';
    private const int CodeLowercaseE = 'e';
    private const int CodeUppercaseE = 'E';
    private const int CodeOpenParenthesis = '(';
    private const int CodeCloseParenthesis = ')';
    private const int CodeSemicolon = ';';
    private const int CodeBacktick = '`';

    private static readonly Dictionary<char, string> ControlCharacters = new()
    {
        ['\b'] = "\\b",
        ['\f'] = "\\f",
        ['\n'] = "\\n",
        ['\r'] = "\\r",
        ['\t'] = "\\t",
    };

    private static readonly Dictionary<char, char> EscapeCharacters = new()
    {
        ['"'] = '"',
        ['\\'] = '\\',
        ['/'] = '/',
        ['b'] = '\b',
        ['f'] = '\f',
        ['n'] = '\n',
        ['r'] = '\r',
        ['t'] = '\t',
    };

    private static bool IsWhitespace(int code) => code == 32 || code == 9 || code == 10 || code == 13;

    private static bool IsSpecialWhitespace(int code) =>
        (code >= 0 && code <= 31 && code != 9 && code != 10 && code != 13) || code == 160;

    private static bool IsDoubleQuote(int code) => code == CodeDoubleQuote;
    private static bool IsSingleQuote(int code) => code == 39;
    private static bool IsSingleQuoteLike(int code) => code == 8216 || code == 8217;
    private static bool IsDoubleQuoteLike(int code) => code == 8220 || code == 8221;

    private static bool IsQuote(int code) =>
        IsDoubleQuote(code) || IsSingleQuote(code) || IsSingleQuoteLike(code) || IsDoubleQuoteLike(code);

    private static bool IsUppercase(int code) => code >= 65 && code <= 90;

    private static bool IsStartOfValue(char c)
    {
        if (c == '\0') return false;
        int code = c;
        return IsQuote(code)
               || c is '{' or '[' or 't' or 'f' or 'n'
               || char.IsDigit(c)
               || c == '-'
               || IsUppercase(code);
    }

    private static bool EndsWithCommaOrNewline(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int idx = text.Length - 1;
        while (idx >= 0 && (text[idx] == ' ' || text[idx] == '\t')) idx--;
        if (idx < 0) return false;
        return text[idx] == ',' || text[idx] == '\n';
    }

    private static string InsertBeforeLastWhitespace(string text, string insertion)
    {
        var stripped = text.TrimEnd();
        var whitespace = text[stripped.Length..];
        return stripped + insertion + whitespace;
    }

    private static string StripLastOccurrence(string text, char ch, bool stripBeforeWs = false)
    {
        int idx = text.LastIndexOf(ch);
        if (idx == -1) return text;
        if (stripBeforeWs)
        {
            int wsStart = idx;
            while (wsStart > 0 && IsWhitespace(text[wsStart - 1])) wsStart--;
            return text[..wsStart] + text[(idx + 1)..];
        }
        return text[..idx] + text[(idx + 1)..];
    }

    private static bool IsDelimiter(char c)
    {
        if (c == '\0') return true;
        int code = c;
        return code is CodeComma or CodeOpeningBrace or CodeClosingBrace
            or CodeOpeningBracket or CodeClosingBracket or CodeColon or CodeSemicolon
            || IsWhitespace(code);
    }

    private static bool IsHex(int code) =>
        (code >= 48 && code <= 57) || (code >= 65 && code <= 70) || (code >= 97 && code <= 102);

    private static bool IsDigit(int code) => code >= 48 && code <= 57;

    private static bool IsControlCharacter(int code) => code >= 0 && code <= 31;

    private static bool IsValidStringCharacter(int code) => code >= 32;

    private static char NextNonWhitespaceCharacter(string text, int start)
    {
        int idx = start;
        while (idx < text.Length && IsWhitespace(text[idx])) idx++;
        return idx < text.Length ? text[idx] : '\0';
    }

    private static bool AtEndOfBlockComment(string text, int idx) =>
        idx + 1 < text.Length && text[idx] == '*' && text[idx + 1] == '/';

    /// <summary>
    /// JSON-encodes a string value exactly like json.dumps(ensure_ascii=True):
    /// ASCII passes through (except JSON-required escapes), control characters
    /// and non-ASCII become \uXXXX (lowercase hex).
    /// </summary>
    private static string JsonDumps(string value)
    {
        var sb = new StringBuilder(value.Length + 2);
        sb.Append('"');
        foreach (char ch in value)
        {
            switch (ch)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\b': sb.Append("\\b"); break;
                case '\f': sb.Append("\\f"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (ch < 0x20 || ch > 0x7e)
                        sb.Append("\\u").Append(((int)ch).ToString("x4"));
                    else
                        sb.Append(ch);
                    break;
            }
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string ExtractJsonFromMarkdown(string text)
    {
        string stripped = text.Trim();
        // ```json ... ``` or ``` ... ``` (greedy: match the LAST closing fence)
        var m = FenceRegex().Match(stripped);
        if (m.Success) return m.Groups[1].Value.Trim();
        // Opening fence with no closing fence (truncated input)
        m = OpenFenceRegex().Match(stripped);
        if (m.Success) return m.Groups[1].Value.Trim();
        // Closing fence only (no opening fence)
        m = CloseFenceRegex().Match(stripped);
        if (m.Success) return stripped[..m.Index];
        return text;
    }

    [GeneratedRegex("```(?:json)?\\s*\\n(.*)\\n\\s*```", RegexOptions.Singleline)]
    private static partial Regex FenceRegex();

    [GeneratedRegex("\\A```(?:json)?\\s*\\n(.*)", RegexOptions.Singleline)]
    private static partial Regex OpenFenceRegex();

    [GeneratedRegex("\\n\\s*```\\s*$")]
    private static partial Regex CloseFenceRegex();

    /// <summary>
    /// Repair a string containing an invalid JSON document.
    /// <para>
    /// <b>Behavioral note:</b> Unlike the upstream <c>jsonrepair</c> library (which throws a
    /// <c>JsonRepairException</c> when unexpected content follows the first complete JSON value),
    /// this implementation silently discards all trailing garbage after the first valid JSON
    /// structure and returns only the repaired prefix. This is intentional: the proxy processes
    /// SSE stream chunks that may contain partial or concatenated payloads, and strict trailing
    /// content validation would reject otherwise-recoverable fragments mid-stream.
    /// </para>
    /// </summary>
    public static string Repair(string input)
    {
        var p = new Repairer(ExtractJsonFromMarkdown(input));
        return p.Run();
    }

    /// <summary>Repair JSON text and validate the result parses as JSON.</summary>
    public static string RepairAndValidate(string input)
    {
        string repaired = Repair(input);
        // validation: result must be valid JSON
        using var doc = JsonDocument.Parse(repaired);
        return repaired;
    }

    private sealed class Repairer
    {
        private const int CodeEof = -1;

        private readonly string _text;
        private readonly int _length;
        private int _i;
        private string _output = "";

        public Repairer(string text)
        {
            _text = text;
            _length = text.Length;
        }

        private int CharCode(int index) => index >= 0 && index < _length ? _text[index] : -1;

        private char CurrentChar() => _i >= 0 && _i < _length ? _text[_i] : '\0';

        private string Got() => _i < _length ? $"but got '{_text[_i]}'" : "but reached end of input";

        private void ThrowInvalidCharacter(char ch) =>
            throw new JsonRepairException($"Invalid character {JsonDumps(ch.ToString())}", _i);

        private void ThrowUnexpectedEnd() =>
            throw new JsonRepairException("Unexpected end of json string", _length);

        private void ThrowColonExpected() =>
            throw new JsonRepairException("Colon expected", _i);

        private void ThrowInvalidUnicodeCharacter()
        {
            string chars = _text.Substring(_i, Math.Min(6, _length - _i));
            throw new JsonRepairException($"Invalid unicode character \"{chars}\"", _i);
        }

        public string Run()
        {
            bool processed = ParseValue();
            if (!processed) ThrowUnexpectedEnd();

            bool processedComma = ParseCharacter(CodeComma);
            if (processedComma) ParseWhitespaceAndSkipComments();

            if (IsStartOfValue(CurrentChar()) && EndsWithCommaOrNewline(_output))
            {
                if (!processedComma) _output = InsertBeforeLastWhitespace(_output, ",");
                ParseNewlineDelimitedJson();
            }
            else if (processedComma)
            {
                _output = StripLastOccurrence(_output, ',');
            }

            while (CharCode(_i) is CodeClosingBrace or CodeClosingBracket)
            {
                _i++;
                ParseWhitespaceAndSkipComments();
            }

            if (_i >= _length) return _output;

            // Skip trailing markdown code fence if present
            ParseWhitespaceAndSkipComments();
            if (_i + 2 < _length && _text.Substring(_i, 3) == "```")
            {
                _i = _length;
                return _output;
            }

            // DEV NOTE: Upstream jsonrepair throws JsonRepairException here
            // ("Unexpected character ... at position N") when non-whitespace
            // content follows the first complete JSON value. We intentionally
            // skip all trailing garbage instead of throwing. Rationale: SSE
            // stream chunks may carry concatenated or partially-overlapping
            // JSON payloads; tolerating trailing data lets the proxy recover
            // the leading valid structure without aborting the stream.
            // If strict validation is ever needed, use RepairAndValidate()
            // or restore the upstream throw here.
            _i = _length;
            return _output;
        }

        private bool ParseValue()
        {
            ParseWhitespaceAndSkipComments();
            bool processed = ParseObject() || ParseArray() || ParseString() ||
                             ParseNumber() || ParseKeywords() || ParseUnquotedString();
            ParseWhitespaceAndSkipComments();
            return processed;
        }

        private bool ParseWhitespaceAndSkipComments()
        {
            int start = _i;
            ParseWhitespace();
            while (true)
            {
                bool changed = ParseComment();
                if (changed) ParseWhitespace();
                else break;
            }
            return _i > start;
        }

        private bool ParseWhitespace()
        {
            var whitespace = new StringBuilder();
            while (true)
            {
                int code = CharCode(_i);
                bool normal = IsWhitespace(code);
                bool special = IsSpecialWhitespace(code);
                if (normal || special)
                {
                    whitespace.Append(normal ? _text[_i] : ' ');
                    _i++;
                }
                else break;
            }
            if (whitespace.Length > 0)
            {
                _output += whitespace.ToString();
                return true;
            }
            return false;
        }

        private bool ParseComment()
        {
            if (CharCode(_i) == CodeSlash && CharCode(_i + 1) == CodeAsterisk)
            {
                while (_i < _length && !AtEndOfBlockComment(_text, _i)) _i++;
                _i += 2;
                return true;
            }
            if (CharCode(_i) == CodeSlash && CharCode(_i + 1) == CodeSlash)
            {
                while (_i < _length && CharCode(_i) != CodeNewline) _i++;
                return true;
            }
            return false;
        }

        private bool ParseCharacter(int code)
        {
            if (CharCode(_i) == code)
            {
                _output += _text[_i];
                _i++;
                return true;
            }
            return false;
        }

        private bool SkipCharacter(int code)
        {
            if (CharCode(_i) == code)
            {
                _i++;
                return true;
            }
            return false;
        }

        private bool SkipEscapeCharacter() => SkipCharacter(CodeBackslash);

        private bool ParseObject()
        {
            if (CharCode(_i) != CodeOpeningBrace) return false;
            _output += "{";
            _i++;
            ParseWhitespaceAndSkipComments();
            bool initial = true;
            while (_i < _length && CharCode(_i) != CodeClosingBrace)
            {
                bool processedComma;
                if (!initial)
                {
                    processedComma = ParseCharacter(CodeComma);
                    if (!processedComma) _output = InsertBeforeLastWhitespace(_output, ",");
                    ParseWhitespaceAndSkipComments();
                }
                else
                {
                    processedComma = true;
                    initial = false;
                }

                bool processedKey = ParseString() || ParseUnquotedString();
                if (!processedKey)
                {
                    int cc = CharCode(_i);
                    if (cc is CodeClosingBrace or CodeOpeningBrace or CodeClosingBracket or CodeOpeningBracket || _i >= _length)
                    {
                        _output = StripLastOccurrence(_output, ',');
                        break;
                    }
                    // Skip unexpected character in object key position and retry
                    _i++;
                    continue;
                }

                ParseWhitespaceAndSkipComments();
                bool processedColon = ParseCharacter(CodeColon);
                bool truncated = _i >= _length;
                if (!processedColon)
                {
                    if (IsStartOfValue(CurrentChar()) || truncated)
                    {
                        _output = InsertBeforeLastWhitespace(_output, ":");
                    }
                    else
                    {
                        // Skip unexpected character where colon was expected and retry
                        _i++;
                        continue;
                    }
                }

                bool processedValue = ParseValue();
                if (!processedValue)
                {
                    if (processedColon || truncated) _output += "null";
                    else ThrowColonExpected();
                }
            }

            if (CharCode(_i) == CodeClosingBrace)
            {
                _output += "}";
                _i++;
            }
            else
            {
                _output = InsertBeforeLastWhitespace(_output, "}");
            }
            return true;
        }

        private bool ParseArray()
        {
            if (CharCode(_i) != CodeOpeningBracket) return false;
            _output += "[";
            _i++;
            ParseWhitespaceAndSkipComments();
            bool initial = true;
            while (_i < _length && CharCode(_i) != CodeClosingBracket)
            {
                if (!initial)
                {
                    bool processedComma = ParseCharacter(CodeComma);
                    if (!processedComma) _output = InsertBeforeLastWhitespace(_output, ",");
                }
                else
                {
                    initial = false;
                }

                bool processedValue = ParseValue();
                if (!processedValue)
                {
                    _output = StripLastOccurrence(_output, ',');
                    break;
                }
            }

            if (CharCode(_i) == CodeClosingBracket)
            {
                _output += "]";
                _i++;
            }
            else
            {
                _output = InsertBeforeLastWhitespace(_output, "]");
            }
            return true;
        }

        private void ParseNewlineDelimitedJson()
        {
            bool initial = true;
            bool processedValue = true;
            while (processedValue)
            {
                if (!initial)
                {
                    bool processedComma = ParseCharacter(CodeComma);
                    if (!processedComma) _output = InsertBeforeLastWhitespace(_output, ",");
                }
                else
                {
                    initial = false;
                }
                processedValue = ParseValue();
            }
            if (!processedValue) _output = StripLastOccurrence(_output, ',');
            _output = $"[\n{_output}\n]";
        }

        private bool ParseString(bool stopAtDelimiter = false)
        {
            bool skipEscapeChars = CharCode(_i) == CodeBackslash;
            if (skipEscapeChars)
            {
                _i++;
                skipEscapeChars = true;
            }

            if (!IsQuote(CharCode(_i))) return false;

            int cc = CharCode(_i);
            Func<int, bool> isEndQuoteFn;
            if (IsDoubleQuote(cc)) isEndQuoteFn = IsDoubleQuote;
            else if (IsSingleQuote(cc)) isEndQuoteFn = IsSingleQuote;
            else if (IsSingleQuoteLike(cc)) isEndQuoteFn = IsSingleQuoteLike;
            else isEndQuoteFn = IsDoubleQuoteLike;

            int iBefore = _i;
            var s = new StringBuilder("\"");
            _i++;

            bool IsEndOfString(int idx)
            {
                if (stopAtDelimiter)
                {
                    // In stop_at_delimiter mode, only stop at EOF.
                    return idx >= _length;
                }
                return isEndQuoteFn(CharCode(idx));
            }

            while (_i < _length && !IsEndOfString(_i))
            {
                if (stopAtDelimiter && isEndQuoteFn(CharCode(_i)))
                {
                    // In stop_at_delimiter mode, an unescaped quote that is NOT
                    // followed by a structural delimiter/EOF is inner content.
                    int j = _i + 1;
                    while (j < _length && IsWhitespace(CharCode(j))) j++;
                    int nextAfter = j < _length ? CharCode(j) : CodeEof;
                    if (nextAfter is CodeComma or CodeClosingBrace or CodeClosingBracket or CodeColon or CodeEof)
                    {
                        break; // valid end quote
                    }
                    // Not a valid end quote; escape it and continue
                    s.Append('\\').Append(_text[_i]);
                    _i++;
                    continue;
                }

                if (CharCode(_i) == CodeBackslash)
                {
                    char ch = _i + 1 < _length ? _text[_i + 1] : '\0';
                    if (EscapeCharacters.TryGetValue(ch, out _))
                    {
                        s.Append(_text, _i, 2);
                        _i += 2;
                    }
                    else if (ch == 'u')
                    {
                        int j = 2;
                        while (j < 6 && _i + j < _length && IsHex(CharCode(_i + j))) j++;
                        if (j == 6)
                        {
                            s.Append(_text, _i, 6);
                            _i += 6;
                        }
                        else if (_i + j >= _length)
                        {
                            _i = _length;
                        }
                        else
                        {
                            ThrowInvalidUnicodeCharacter();
                        }
                    }
                    else
                    {
                        if (IsSingleQuote(ch) || IsSingleQuoteLike(ch))
                        {
                            s.Append(ch);
                        }
                        else if (IsDoubleQuoteLike(ch))
                        {
                            s.Append("\\\"");
                        }
                        else if (IsQuote(ch) || ch == '\\' || ch is 'b' or 'f' or 'n' or 'r' or 't' or 'u' or '/')
                        {
                            s.Append(_text, _i, 2);
                        }
                        else
                        {
                            s.Append("\\\\").Append(ch);
                        }
                        _i += 2;
                    }
                }
                else
                {
                    char ch = _text[_i];
                    int code = CharCode(_i);
                    if (code == CodeDoubleQuote && !(stopAtDelimiter && isEndQuoteFn(code)))
                    {
                        // Unescaped double quote inside string: heuristic lookahead.
                        int j = _i + 1;
                        while (j < _length && CharCode(j) != CodeDoubleQuote) j++;
                        if (j < _length)
                        {
                            int nextJ = j + 1;
                            while (nextJ < _length && IsWhitespace(CharCode(nextJ))) nextJ++;
                            int afterNext = nextJ < _length ? CharCode(nextJ) : -1;

                            if (nextJ < _length && CharCode(nextJ) == CodeDoubleQuote)
                            {
                                // Adjacent quotes ("")
                                int k = nextJ + 1;
                                while (k < _length && IsWhitespace(CharCode(k))) k++;
                                int afterSecond = k < _length ? CharCode(k) : CodeEof;
                                if (afterSecond is CodeComma or CodeClosingBrace or CodeClosingBracket or CodeColon or CodeEof)
                                {
                                    s.Append('\\').Append(ch);
                                    _i++;
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else if (afterNext == CodePlus)
                            {
                                break; // string concatenation: current quote is the real end
                            }
                            else if (afterNext is CodeComma or CodeClosingBrace or CodeClosingBracket or CodeColon or CodeEof)
                            {
                                if (j > _i + 1)
                                {
                                    s.Append('\\').Append(ch);
                                    _i++;
                                }
                                else
                                {
                                    break;
                                }
                            }
                            else
                            {
                                break;
                            }
                        }
                        else
                        {
                            break;
                        }
                    }
                    else if (IsControlCharacter(code))
                    {
                        s.Append(ControlCharacters.TryGetValue(ch, out var esc) ? esc : ch.ToString());
                        _i++;
                    }
                    else
                    {
                        if (!IsValidStringCharacter(code)) ThrowInvalidCharacter(ch);
                        s.Append(ch);
                        _i++;
                    }
                }

                if (skipEscapeChars) SkipEscapeCharacter();
            }

            bool hasEndQuote = IsQuote(CharCode(_i));
            char nextNws = hasEndQuote ? NextNonWhitespaceCharacter(_text, _i + 1) : '\0';
            bool valid = hasEndQuote && (
                _i + 1 >= _length
                || nextNws is ',' or '}' or ']' or ':'
                || nextNws == '+' // string concatenation is valid termination
            );
            if (!valid && !stopAtDelimiter)
            {
                _i = iBefore;
                return ParseString(true);
            }

            if (hasEndQuote)
            {
                s.Append('"');
                _i++;
            }
            else
            {
                var tmp = s.ToString();
                tmp = InsertBeforeLastWhitespace(tmp, "\"");
                s.Clear().Append(tmp);
            }

            _output += s.ToString();
            ParseConcatenatedString();
            return true;
        }

        private bool ParseConcatenatedString()
        {
            bool processed = false;
            ParseWhitespaceAndSkipComments();
            while (CharCode(_i) == CodePlus)
            {
                processed = true;
                _i++;
                ParseWhitespaceAndSkipComments();
                // Remove the closing quote of the previous string
                string stripped = _output.TrimEnd();
                if (stripped.EndsWith('"'))
                {
                    _output = stripped[..^1];
                }
                else
                {
                    _output = StripLastOccurrence(_output, '"');
                }
                int start = _output.Length;
                bool parsedStr = ParseString();
                if (parsedStr)
                {
                    // Remove the opening quote added by parse_string to merge contents
                    if (start < _output.Length && _output[start] == '"')
                    {
                        _output = _output[..start] + _output[(start + 1)..];
                    }
                }
                else
                {
                    _output = InsertBeforeLastWhitespace(_output, "\"");
                }
            }
            return processed;
        }

        private bool ParseNumber()
        {
            int start = _i;
            if (CharCode(_i) == CodeMinus)
            {
                _i++;
                if (ExpectDigitOrRepair(start)) return true;
            }

            while (IsDigit(CharCode(_i))) _i++;

            if (CharCode(_i) == CodeDot)
            {
                _i++;
                if (ExpectDigitOrRepair(start)) return true;
                while (IsDigit(CharCode(_i))) _i++;
            }

            if (CharCode(_i) is CodeLowercaseE or CodeUppercaseE)
            {
                _i++;
                if (CharCode(_i) is CodeMinus or CodePlus) _i++;
                if (ExpectDigitOrRepair(start)) return true;
                while (IsDigit(CharCode(_i))) _i++;
            }

            if (_i > start)
            {
                string num = _text[start.._i];
                bool hasInvalidLeadingZero = Regex.IsMatch(num, "^0[0-9]");
                _output += hasInvalidLeadingZero ? $"\"{num}\"" : num;
                return true;
            }
            return false;
        }

        private bool ParseKeywords() =>
            ParseKeyword("true", "true") || ParseKeyword("false", "false") ||
            ParseKeyword("null", "null") || ParseKeyword("True", "true") ||
            ParseKeyword("False", "false") || ParseKeyword("None", "null");

        private bool ParseKeyword(string name, string value)
        {
            if (_i + name.Length <= _length && _text.AsSpan(_i, name.Length).SequenceEqual(name))
            {
                _output += value;
                _i += name.Length;
                return true;
            }
            return false;
        }

        private bool ParseUnquotedString()
        {
            int start = _i;
            while (_i < _length && !IsDelimiter(_text[_i])) _i++;
            if (_i > start)
            {
                if (CharCode(_i) == CodeOpenParenthesis)
                {
                    _i++;
                    ParseValue();
                    if (CharCode(_i) == CodeCloseParenthesis)
                    {
                        _i++;
                        if (CharCode(_i) == CodeSemicolon) _i++;
                    }
                    return true;
                }

                while (_i > 0 && IsWhitespace(CharCode(_i - 1))) _i--;
                string symbol = _text[start.._i];
                _output += symbol == "undefined" ? "null" : JsonDumps(symbol);
                if (CharCode(_i) == CodeDoubleQuote) _i++;
                return true;
            }
            return false;
        }

        private bool ExpectDigitOrRepair(int start)
        {
            if (_i >= _length)
            {
                _output += _text[start.._i] + "0";
                return true;
            }
            if (!IsDigit(CharCode(_i)))
            {
                // Invalid number; backtrack so parse_unquoted_string can handle it
                int charsConsumed = _i - start;
                _i = start;
                if (charsConsumed > 0 && _output.Length >= charsConsumed)
                {
                    _output = _output[..^charsConsumed];
                }
            }
            return false;
        }
    }
}
