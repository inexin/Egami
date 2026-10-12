using System.Globalization;
using System.Text;

namespace Egami.Css;

/// <summary>
/// Serializes tokens and component values (CSS Syntax 3 section 9 and CSSOM's "serialize an identifier" and "serialize a
/// string"), inserting an empty comment between two tokens that would otherwise run together.
/// </summary>
internal static class CssSerializer
{
    public static void Write(StringBuilder builder, CssComponentValue value)
    {
        Writer writer = new(builder);
        writer.Write(value);
    }

    public static void Write(StringBuilder builder, IEnumerable<CssComponentValue> values)
    {
        Writer writer = new(builder);
        foreach (CssComponentValue value in values)
        {
            writer.Write(value);
        }
    }

    public static void WriteToken(StringBuilder builder, CssToken token)
    {
        Writer writer = new(builder);
        writer.WriteToken(token);
    }

    public static bool SequenceEqual(IReadOnlyList<CssComponentValue> a, IReadOnlyList<CssComponentValue> b)
    {
        if (ReferenceEquals(a, b))
        {
            return true;
        }

        if (a.Count != b.Count)
        {
            return false;
        }

        for (int i = 0; i < a.Count; i++)
        {
            if (!a[i].Equals(b[i]))
            {
                return false;
            }
        }

        return true;
    }

    public static string Identifier(string name)
    {
        StringBuilder builder = new();
        WriteIdentifier(builder, name);
        return builder.ToString();
    }

    public static string QuotedString(string value)
    {
        StringBuilder builder = new();
        WriteString(builder, value);
        return builder.ToString();
    }

    public static void WriteIdentifier(StringBuilder builder, string name)
    {
        if (name == "-")
        {
            builder.Append("\\-");
            return;
        }

        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (c == '\0')
            {
                builder.Append('�');
            }
            else if (c is <= '\u001F' or '\u007F' || (i == 0 && CssText.IsDigit(c)) || (i == 1 && CssText.IsDigit(c) && name[0] == '-'))
            {
                WriteCodePointEscape(builder, c);
            }
            else if (c >= 0x80 || c is '-' or '_' || CssText.IsDigit(c) || c is >= 'a' and <= 'z' or >= 'A' and <= 'Z')
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('\\').Append(c);
            }
        }
    }

    // A hash name that is not an identifier: every character is an identifier character or escaped.
    private static void WriteName(StringBuilder builder, string name)
    {
        foreach (char c in name)
        {
            if (c is <= '\u001F' or '\u007F')
            {
                WriteCodePointEscape(builder, c);
            }
            else if (CssText.IsIdentChar(c))
            {
                builder.Append(c);
            }
            else
            {
                builder.Append('\\').Append(c);
            }
        }
    }

    public static void WriteString(StringBuilder builder, string value)
    {
        builder.Append('"');
        foreach (char c in value)
        {
            if (c == '\0')
            {
                builder.Append('�');
            }
            else if (c is <= '\u001F' or '\u007F')
            {
                WriteCodePointEscape(builder, c);
            }
            else if (c is '"' or '\\')
            {
                builder.Append('\\').Append(c);
            }
            else
            {
                builder.Append(c);
            }
        }

        builder.Append('"');
    }

    private static void WriteCodePointEscape(StringBuilder builder, char c)
    {
        builder.Append('\\').Append(((int)c).ToString("x", CultureInfo.InvariantCulture)).Append(' ');
    }

    private static void WriteUnit(StringBuilder builder, string unit)
    {
        // A unit like "e3" after a number would read back as an exponent.
        if (unit.Length > 1 && unit[0] is 'e' or 'E' && (CssText.IsDigit(unit[1]) || (unit[1] == '-' && unit.Length > 2 && CssText.IsDigit(unit[2]))))
        {
            WriteCodePointEscape(builder, unit[0]);
            WriteIdentifier(builder, unit[1..]);
            return;
        }

        WriteIdentifier(builder, unit);
    }

    // The serialization categories that matter for the comment table.
    private enum Kind : byte
    {
        Other,
        Ident,
        Function,
        Url,
        BadUrl,
        AtKeyword,
        Hash,
        Number,
        Percentage,
        Dimension,
        Cdc,
        Minus,
        HashDelim,
        At,
        Dot,
        Plus,
        Slash,
        Star,
        Percent,
        LeftParenthesis,
    }

    private struct Writer(StringBuilder builder)
    {
        private readonly StringBuilder _builder = builder;
        private Kind _previous;

        public void Write(CssComponentValue value)
        {
            switch (value)
            {
                case CssTokenValue token:
                    WriteToken(token.Token);
                    break;
                case CssFunction function:
                    Begin(Kind.Function);
                    WriteIdentifier(_builder, function.Name);
                    _builder.Append('(');
                    _previous = Kind.Other;
                    foreach (CssComponentValue argument in function.Arguments)
                    {
                        Write(argument);
                    }

                    _builder.Append(')');
                    _previous = Kind.Other;
                    break;
                case CssSimpleBlock block:
                    Begin(block.OpenToken == '(' ? Kind.LeftParenthesis : Kind.Other);
                    _builder.Append(block.OpenToken);
                    _previous = Kind.Other;
                    foreach (CssComponentValue item in block.Content)
                    {
                        Write(item);
                    }

                    _builder.Append(block.CloseToken);
                    _previous = Kind.Other;
                    break;
            }
        }

        public void WriteToken(CssToken token)
        {
            switch (token.Kind)
            {
                case CssTokenKind.Ident:
                    Begin(Kind.Ident);
                    WriteIdentifier(_builder, token.Value);
                    break;
                case CssTokenKind.Function:
                    Begin(Kind.Function);
                    WriteIdentifier(_builder, token.Value);
                    _builder.Append('(');
                    _previous = Kind.Other;
                    break;
                case CssTokenKind.AtKeyword:
                    Begin(Kind.AtKeyword);
                    _builder.Append('@');
                    WriteIdentifier(_builder, token.Value);
                    break;
                case CssTokenKind.Hash:
                    Begin(Kind.Hash);
                    _builder.Append('#');
                    if (token.IsIdHash)
                    {
                        WriteIdentifier(_builder, token.Value);
                    }
                    else
                    {
                        WriteName(_builder, token.Value);
                    }

                    break;
                case CssTokenKind.String:
                    Begin(Kind.Other);
                    WriteString(_builder, token.Value);
                    break;
                case CssTokenKind.BadString:
                    Begin(Kind.Other);
                    _builder.Append("\"\n");
                    break;
                case CssTokenKind.Url:
                    Begin(Kind.Url);
                    _builder.Append("url(");
                    foreach (char c in token.Value)
                    {
                        if (c is <= ' ' or '\u007F')
                        {
                            WriteCodePointEscape(_builder, c);
                        }
                        else if (c is '(' or ')' or '"' or '\'' or '\\')
                        {
                            _builder.Append('\\').Append(c);
                        }
                        else
                        {
                            _builder.Append(c);
                        }
                    }

                    _builder.Append(')');
                    break;
                case CssTokenKind.BadUrl:
                    Begin(Kind.BadUrl);
                    _builder.Append("url(()");
                    break;
                case CssTokenKind.Delim:
                    char d = token.Value.Length > 0 ? token.Value[0] : '?';
                    Begin(d switch
                    {
                        '-' => Kind.Minus,
                        '#' => Kind.HashDelim,
                        '@' => Kind.At,
                        '.' => Kind.Dot,
                        '+' => Kind.Plus,
                        '/' => Kind.Slash,
                        '*' => Kind.Star,
                        '%' => Kind.Percent,
                        _ => Kind.Other,
                    });
                    _builder.Append(d == '\\' ? "\\\n" : token.Value);
                    break;
                case CssTokenKind.Number:
                    Begin(Kind.Number);
                    _builder.Append(token.NumberText);
                    break;
                case CssTokenKind.Percentage:
                    Begin(Kind.Percentage);
                    _builder.Append(token.NumberText).Append('%');
                    break;
                case CssTokenKind.Dimension:
                    Begin(Kind.Dimension);
                    _builder.Append(token.NumberText);
                    WriteUnit(_builder, token.Unit);
                    break;
                case CssTokenKind.Whitespace:
                    Begin(Kind.Other);
                    _builder.Append(' ');
                    break;
                case CssTokenKind.Cdo:
                    Begin(Kind.Other);
                    _builder.Append("<!--");
                    break;
                case CssTokenKind.Cdc:
                    Begin(Kind.Cdc);
                    _builder.Append("-->");
                    break;
                case CssTokenKind.Colon:
                    Begin(Kind.Other);
                    _builder.Append(':');
                    break;
                case CssTokenKind.Semicolon:
                    Begin(Kind.Other);
                    _builder.Append(';');
                    break;
                case CssTokenKind.Comma:
                    Begin(Kind.Other);
                    _builder.Append(',');
                    break;
                case CssTokenKind.LeftSquare:
                    Begin(Kind.Other);
                    _builder.Append('[');
                    break;
                case CssTokenKind.RightSquare:
                    Begin(Kind.Other);
                    _builder.Append(']');
                    break;
                case CssTokenKind.LeftParenthesis:
                    Begin(Kind.LeftParenthesis);
                    _builder.Append('(');
                    break;
                case CssTokenKind.RightParenthesis:
                    Begin(Kind.Other);
                    _builder.Append(')');
                    break;
                case CssTokenKind.LeftCurly:
                    Begin(Kind.Other);
                    _builder.Append('{');
                    break;
                case CssTokenKind.RightCurly:
                    Begin(Kind.Other);
                    _builder.Append('}');
                    break;
                default:
                    Begin(Kind.Other);
                    break;
            }
        }

        private void Begin(Kind next)
        {
            if (NeedsComment(_previous, next))
            {
                _builder.Append("/**/");
            }

            _previous = next;
        }

        private static bool NeedsComment(Kind a, Kind b)
        {
            bool identLike = b is Kind.Ident or Kind.Function or Kind.Url or Kind.BadUrl;
            bool numeric = b is Kind.Number or Kind.Percentage or Kind.Dimension;
            return a switch
            {
                Kind.Ident => identLike || numeric || b is Kind.Minus or Kind.Cdc or Kind.LeftParenthesis,
                Kind.AtKeyword or Kind.Hash or Kind.Dimension => identLike || numeric || b is Kind.Minus or Kind.Cdc,
                Kind.HashDelim or Kind.Minus => identLike || numeric || b is Kind.Minus,
                Kind.Number => identLike || numeric || b is Kind.Percent,
                Kind.At => identLike || b is Kind.Minus,
                Kind.Dot or Kind.Plus => numeric,
                Kind.Slash => b is Kind.Star,
                _ => false,
            };
        }
    }
}
