using System.Globalization;
using System.Text;

namespace Egami.Css;

/// <summary>The CSS Syntax 3 tokenizer (section 4), with its input preprocessing.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
/// </example>
public static class CssTokenizer
{
    /// <summary>Splits CSS text into tokens. Never throws: malformed input gives bad-string, bad-url or delim tokens and errors.</summary>
    /// <param name="css">The CSS text.</param>
    /// <param name="options">Options whose <see cref="CssParseOptions.Errors"/> receives the tokenizer's parse errors.</param>
    /// <returns>The tokens, without a final <see cref="CssTokenKind.EndOfFile"/> token.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Css.cs" region="tokens"/>
    /// </example>
    public static IReadOnlyList<CssToken> Tokenize(string css, CssParseOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(css);
        Reader reader = new(Preprocess(css), options ?? CssParseOptions.Default);
        List<CssToken> tokens = [];
        while (true)
        {
            CssToken token = reader.Next();
            if (token.Kind == CssTokenKind.EndOfFile)
            {
                return tokens;
            }

            tokens.Add(token);
        }
    }

    /// <summary>
    /// Replaces CR LF, CR and FF with LF, and NUL and lone surrogates with U+FFFD, as CSS Syntax 3 section 3.3 says.
    /// </summary>
    internal static string Preprocess(string css)
    {
        int i = 0;
        for (; i < css.Length; i++)
        {
            char c = css[i];
            if (c is '\r' or '\f' or '\0' || char.IsSurrogate(c))
            {
                break;
            }
        }

        if (i == css.Length)
        {
            return css;
        }

        StringBuilder builder = new(css.Length);
        builder.Append(css, 0, i);
        for (; i < css.Length; i++)
        {
            char c = css[i];
            switch (c)
            {
                case '\r':
                    builder.Append('\n');
                    if (i + 1 < css.Length && css[i + 1] == '\n')
                    {
                        i++;
                    }

                    break;
                case '\f':
                    builder.Append('\n');
                    break;
                case '\0':
                    builder.Append('�');
                    break;
                default:
                    if (char.IsHighSurrogate(c) && i + 1 < css.Length && char.IsLowSurrogate(css[i + 1]))
                    {
                        builder.Append(c).Append(css[i + 1]);
                        i++;
                    }
                    else if (char.IsSurrogate(c))
                    {
                        builder.Append('�');
                    }
                    else
                    {
                        builder.Append(c);
                    }

                    break;
            }
        }

        return builder.ToString();
    }

    private sealed class Reader(string s, CssParseOptions options)
    {
        private readonly string _s = s;
        private readonly CssParseOptions _options = options;
        private readonly StringBuilder _buffer = new();
        private int _pos;

        // Line tracking: the line and the index where it starts, advanced lazily to each token's start.
        private int _trackedPos;
        private int _line = 1;
        private int _lineStart;

        private char Peek(int offset = 0) => _pos + offset < _s.Length ? _s[_pos + offset] : '\0';

        private bool AtEnd(int offset = 0) => _pos + offset >= _s.Length;

        private (int Line, int Column) Position(int pos)
        {
            for (; _trackedPos < pos; _trackedPos++)
            {
                if (_s[_trackedPos] == '\n')
                {
                    _line++;
                    _lineStart = _trackedPos + 1;
                }
            }

            return (_line, pos - _lineStart + 1);
        }

        private void Error(int pos, string message)
        {
            if (_options.Errors is not null)
            {
                (int line, int column) = Position(Math.Min(pos, _s.Length));
                _options.Report(line, column, message);
            }
        }

        public CssToken Next()
        {
            ConsumeComments();
            int start = _pos;
            (int line, int column) = Position(start);
            if (AtEnd())
            {
                return new CssToken(CssTokenKind.EndOfFile, line: line, column: column);
            }

            char c = _s[_pos++];
            switch (c)
            {
                case ' ' or '\t' or '\n':
                    while (!AtEnd() && CssText.IsWhitespace(Peek()))
                    {
                        _pos++;
                    }

                    return new CssToken(CssTokenKind.Whitespace, line: line, column: column);
                case '"' or '\'':
                    return ConsumeString(c, line, column);
                case '#':
                    if ((!AtEnd() && CssText.IsIdentChar(Peek())) || IsValidEscape(0))
                    {
                        bool isId = WouldStartIdent(0);
                        string name = ConsumeIdentSequence();
                        return new CssToken(CssTokenKind.Hash, name, isIdHash: isId, line: line, column: column);
                    }

                    return Delim(c, line, column);
                case '(':
                    return new CssToken(CssTokenKind.LeftParenthesis, line: line, column: column);
                case ')':
                    return new CssToken(CssTokenKind.RightParenthesis, line: line, column: column);
                case '[':
                    return new CssToken(CssTokenKind.LeftSquare, line: line, column: column);
                case ']':
                    return new CssToken(CssTokenKind.RightSquare, line: line, column: column);
                case '{':
                    return new CssToken(CssTokenKind.LeftCurly, line: line, column: column);
                case '}':
                    return new CssToken(CssTokenKind.RightCurly, line: line, column: column);
                case ',':
                    return new CssToken(CssTokenKind.Comma, line: line, column: column);
                case ':':
                    return new CssToken(CssTokenKind.Colon, line: line, column: column);
                case ';':
                    return new CssToken(CssTokenKind.Semicolon, line: line, column: column);
                case '+' or '.':
                    if (StartsNumber(-1))
                    {
                        _pos--;
                        return ConsumeNumeric(line, column);
                    }

                    return Delim(c, line, column);
                case '-':
                    if (StartsNumber(-1))
                    {
                        _pos--;
                        return ConsumeNumeric(line, column);
                    }

                    if (Peek() == '-' && Peek(1) == '>')
                    {
                        _pos += 2;
                        return new CssToken(CssTokenKind.Cdc, line: line, column: column);
                    }

                    if (WouldStartIdent(-1))
                    {
                        _pos--;
                        return ConsumeIdentLike(line, column);
                    }

                    return Delim(c, line, column);
                case '<':
                    if (Peek() == '!' && Peek(1) == '-' && Peek(2) == '-')
                    {
                        _pos += 3;
                        return new CssToken(CssTokenKind.Cdo, line: line, column: column);
                    }

                    return Delim(c, line, column);
                case '@':
                    if (WouldStartIdent(0))
                    {
                        return new CssToken(CssTokenKind.AtKeyword, ConsumeIdentSequence(), line: line, column: column);
                    }

                    return Delim(c, line, column);
                case '\\':
                    if (IsValidEscape(-1))
                    {
                        _pos--;
                        return ConsumeIdentLike(line, column);
                    }

                    Error(start, "A backslash that starts no escape.");
                    return Delim(c, line, column);
                default:
                    if (CssText.IsDigit(c))
                    {
                        _pos--;
                        return ConsumeNumeric(line, column);
                    }

                    if (CssText.IsIdentStart(c))
                    {
                        _pos--;
                        return ConsumeIdentLike(line, column);
                    }

                    return Delim(c, line, column);
            }
        }

        private static CssToken Delim(char c, int line, int column) => new(CssTokenKind.Delim, c.ToString(), line: line, column: column);

        private void ConsumeComments()
        {
            while (Peek() == '/' && Peek(1) == '*')
            {
                int start = _pos;
                int end = _s.IndexOf("*/", _pos + 2, StringComparison.Ordinal);
                if (end < 0)
                {
                    Error(start, "A comment not closed before the end of the input.");
                    _pos = _s.Length;
                    return;
                }

                _pos = end + 2;
            }
        }

        // Whether the two code points at _pos + offset form a valid escape.
        private bool IsValidEscape(int offset)
        {
            return Peek(offset) == '\\' && !AtEnd(offset) && Peek(offset + 1) != '\n';
        }

        // Whether the three code points at _pos + offset would start an identifier.
        private bool WouldStartIdent(int offset)
        {
            if (AtEnd(offset))
            {
                return false;
            }

            char c = Peek(offset);
            if (c == '-')
            {
                char next = Peek(offset + 1);
                return (!AtEnd(offset + 1) && (CssText.IsIdentStart(next) || next == '-')) || IsValidEscape(offset + 1);
            }

            if (CssText.IsIdentStart(c))
            {
                return true;
            }

            return IsValidEscape(offset);
        }

        // Whether the three code points at _pos + offset would start a number.
        private bool StartsNumber(int offset)
        {
            char c = Peek(offset);
            if (AtEnd(offset))
            {
                return false;
            }

            if (c is '+' or '-')
            {
                char next = Peek(offset + 1);
                if (!AtEnd(offset + 1) && CssText.IsDigit(next))
                {
                    return true;
                }

                return next == '.' && !AtEnd(offset + 2) && CssText.IsDigit(Peek(offset + 2));
            }

            if (c == '.')
            {
                return !AtEnd(offset + 1) && CssText.IsDigit(Peek(offset + 1));
            }

            return CssText.IsDigit(c);
        }

        private string ConsumeIdentSequence()
        {
            _buffer.Clear();
            while (!AtEnd())
            {
                char c = Peek();
                if (CssText.IsIdentChar(c))
                {
                    _buffer.Append(c);
                    _pos++;
                }
                else if (IsValidEscape(0))
                {
                    _pos++;
                    AppendEscape(_buffer);
                }
                else
                {
                    break;
                }
            }

            return _buffer.ToString();
        }

        // Consumes an escaped code point; _pos is just after the backslash.
        private void AppendEscape(StringBuilder builder)
        {
            if (AtEnd())
            {
                Error(_pos, "An escape at the end of the input.");
                builder.Append('�');
                return;
            }

            char c = _s[_pos++];
            if (!CssText.IsHexDigit(c))
            {
                builder.Append(c);
                return;
            }

            int value = CssText.HexValue(c);
            for (int i = 0; i < 5 && !AtEnd() && CssText.IsHexDigit(Peek()); i++)
            {
                value = (value * 16) + CssText.HexValue(_s[_pos++]);
            }

            if (!AtEnd() && CssText.IsWhitespace(Peek()))
            {
                _pos++;
            }

            if (value == 0 || value is >= 0xD800 and <= 0xDFFF || value > 0x10FFFF)
            {
                builder.Append('�');
            }
            else
            {
                builder.Append(char.ConvertFromUtf32(value));
            }
        }

        private CssToken ConsumeString(char ending, int line, int column)
        {
            StringBuilder builder = new();
            while (true)
            {
                if (AtEnd())
                {
                    Error(_pos, "A string not closed before the end of the input.");
                    return new CssToken(CssTokenKind.String, builder.ToString(), line: line, column: column);
                }

                char c = _s[_pos++];
                if (c == ending)
                {
                    return new CssToken(CssTokenKind.String, builder.ToString(), line: line, column: column);
                }

                if (c == '\n')
                {
                    Error(_pos - 1, "A newline in a string.");
                    _pos--;
                    return new CssToken(CssTokenKind.BadString, line: line, column: column);
                }

                if (c == '\\')
                {
                    if (AtEnd())
                    {
                        continue;
                    }

                    if (Peek() == '\n')
                    {
                        _pos++;
                        continue;
                    }

                    AppendEscape(builder);
                    continue;
                }

                builder.Append(c);
            }
        }

        private CssToken ConsumeNumeric(int line, int column)
        {
            int start = _pos;
            bool isInteger = true;
            if (Peek() is '+' or '-')
            {
                _pos++;
            }

            while (CssText.IsDigit(Peek()) && !AtEnd())
            {
                _pos++;
            }

            if (Peek() == '.' && !AtEnd(1) && CssText.IsDigit(Peek(1)))
            {
                isInteger = false;
                _pos++;
                while (CssText.IsDigit(Peek()) && !AtEnd())
                {
                    _pos++;
                }
            }

            if (Peek() is 'e' or 'E')
            {
                char next = Peek(1);
                if ((CssText.IsDigit(next) && !AtEnd(1)) || (next is '+' or '-' && !AtEnd(2) && CssText.IsDigit(Peek(2))))
                {
                    isInteger = false;
                    _pos += CssText.IsDigit(next) ? 1 : 2;
                    while (CssText.IsDigit(Peek()) && !AtEnd())
                    {
                        _pos++;
                    }
                }
            }

            string repr = _s[start.._pos];
            double value = ParseNumber(repr);
            if (WouldStartIdent(0))
            {
                string unit = ConsumeIdentSequence();
                return new CssToken(CssTokenKind.Dimension, repr, value, unit, isInteger, line: line, column: column);
            }

            if (Peek() == '%' && !AtEnd())
            {
                _pos++;
                return new CssToken(CssTokenKind.Percentage, repr, value, isInteger: isInteger, line: line, column: column);
            }

            return new CssToken(CssTokenKind.Number, repr, value, isInteger: isInteger, line: line, column: column);
        }

        private CssToken ConsumeIdentLike(int line, int column)
        {
            string name = ConsumeIdentSequence();
            if (Peek() == '(' && !AtEnd())
            {
                _pos++;
                if (CssText.EqualsIgnoreAsciiCase(name, "url"))
                {
                    while (CssText.IsWhitespace(Peek()) && CssText.IsWhitespace(Peek(1)) && !AtEnd(1))
                    {
                        _pos++;
                    }

                    char c = Peek();
                    if (!AtEnd() && (c is '"' or '\'' || (CssText.IsWhitespace(c) && Peek(1) is '"' or '\'' && !AtEnd(1))))
                    {
                        return new CssToken(CssTokenKind.Function, name, line: line, column: column);
                    }

                    return ConsumeUrl(line, column);
                }

                return new CssToken(CssTokenKind.Function, name, line: line, column: column);
            }

            return new CssToken(CssTokenKind.Ident, name, line: line, column: column);
        }

        private CssToken ConsumeUrl(int line, int column)
        {
            StringBuilder builder = new();
            while (!AtEnd() && CssText.IsWhitespace(Peek()))
            {
                _pos++;
            }

            while (true)
            {
                if (AtEnd())
                {
                    Error(_pos, "A url not closed before the end of the input.");
                    return new CssToken(CssTokenKind.Url, builder.ToString(), line: line, column: column);
                }

                char c = _s[_pos++];
                if (c == ')')
                {
                    return new CssToken(CssTokenKind.Url, builder.ToString(), line: line, column: column);
                }

                if (CssText.IsWhitespace(c))
                {
                    while (!AtEnd() && CssText.IsWhitespace(Peek()))
                    {
                        _pos++;
                    }

                    if (AtEnd())
                    {
                        Error(_pos, "A url not closed before the end of the input.");
                        return new CssToken(CssTokenKind.Url, builder.ToString(), line: line, column: column);
                    }

                    if (Peek() == ')')
                    {
                        _pos++;
                        return new CssToken(CssTokenKind.Url, builder.ToString(), line: line, column: column);
                    }

                    Error(_pos, "Whitespace inside an unquoted url.");
                    return ConsumeBadUrl(line, column);
                }

                if (c is '"' or '\'' or '(' || IsNonPrintable(c))
                {
                    Error(_pos - 1, "A character an unquoted url may not contain.");
                    return ConsumeBadUrl(line, column);
                }

                if (c == '\\')
                {
                    if (IsValidEscape(-1))
                    {
                        AppendEscape(builder);
                        continue;
                    }

                    Error(_pos - 1, "A backslash that starts no escape in a url.");
                    return ConsumeBadUrl(line, column);
                }

                builder.Append(c);
            }
        }

        private CssToken ConsumeBadUrl(int line, int column)
        {
            while (!AtEnd())
            {
                char c = _s[_pos++];
                if (c == ')')
                {
                    break;
                }

                if (c == '\\' && IsValidEscape(-1))
                {
                    AppendEscape(new StringBuilder());
                }
            }

            return new CssToken(CssTokenKind.BadUrl, line: line, column: column);
        }

        private static bool IsNonPrintable(char c) => c is <= '\u0008' or '\u000B' or >= '\u000E' and <= '\u001F' or '\u007F';
    }

    internal static double ParseNumber(string repr)
    {
        double value = double.Parse(repr, NumberStyles.Float, CultureInfo.InvariantCulture);
        return double.IsPositiveInfinity(value) ? double.MaxValue : double.IsNegativeInfinity(value) ? double.MinValue : value;
    }
}
