using System.Globalization;

namespace Egami.Svg.Values;

/// <summary>
/// SVG's number grammar over spans, without allocating: <c>[+-]? (digits [. digits?] | . digits) ([eE] [+-]? digits)?</c>.
/// An exponent is taken only when digits follow, so <c>1em</c> reads as 1 and the unit <c>em</c>. Lists are separated by
/// whitespace and at most one comma, and a sign or a second decimal point may start the next number (<c>10-5</c>, <c>.5.5</c>).
/// </summary>
internal static class SvgNumbers
{
    /// <summary>Reads a number at <paramref name="position"/>, moving past it; leaves the position alone on failure.</summary>
    public static bool TryRead(ReadOnlySpan<char> text, ref int position, out double value)
    {
        int i = position;
        int start = i;
        if (i < text.Length && (text[i] == '+' || text[i] == '-'))
        {
            i++;
        }

        int digits = 0;
        while (i < text.Length && char.IsAsciiDigit(text[i]))
        {
            i++;
            digits++;
        }

        if (i < text.Length && text[i] == '.' && i + 1 < text.Length && char.IsAsciiDigit(text[i + 1]))
        {
            i++;
            while (i < text.Length && char.IsAsciiDigit(text[i]))
            {
                i++;
                digits++;
            }
        }
        else if (i < text.Length && text[i] == '.' && digits > 0)
        {
            // "5." is a number in SVG 1.1's grammar.
            i++;
        }

        if (digits == 0)
        {
            value = 0;
            return false;
        }

        if (i < text.Length && (text[i] == 'e' || text[i] == 'E'))
        {
            int e = i + 1;
            if (e < text.Length && (text[e] == '+' || text[e] == '-'))
            {
                e++;
            }

            if (e < text.Length && char.IsAsciiDigit(text[e]))
            {
                while (e < text.Length && char.IsAsciiDigit(text[e]))
                {
                    e++;
                }

                i = e;
            }
        }

        if (!double.TryParse(text[start..i], NumberStyles.Float, CultureInfo.InvariantCulture, out value) || !double.IsFinite(value))
        {
            value = 0;
            return false;
        }

        position = i;
        return true;
    }

    /// <summary>Skips whitespace and at most one comma, as between the numbers of a list.</summary>
    public static void SkipSeparator(ReadOnlySpan<char> text, ref int position)
    {
        SkipSpace(text, ref position);
        if (position < text.Length && text[position] == ',')
        {
            position++;
            SkipSpace(text, ref position);
        }
    }

    /// <summary>Skips XML whitespace (space, tab, CR, LF).</summary>
    public static void SkipSpace(ReadOnlySpan<char> text, ref int position)
    {
        while (position < text.Length && IsSpace(text[position]))
        {
            position++;
        }
    }

    /// <summary>Gets whether a character is XML whitespace.</summary>
    public static bool IsSpace(char c) => c is ' ' or '\t' or '\r' or '\n' or '\f';

    /// <summary>Reads a whole attribute as one number, with optional surrounding whitespace.</summary>
    public static bool TryParse(ReadOnlySpan<char> text, out double value)
    {
        int i = 0;
        SkipSpace(text, ref i);
        if (!TryRead(text, ref i, out value))
        {
            return false;
        }

        SkipSpace(text, ref i);
        return i == text.Length;
    }

    /// <summary>
    /// Reads a list of numbers. Returns false at the first thing that is not a number; <paramref name="values"/> then holds the
    /// numbers before it, which is what SVG renders for points lists ("render up to the error").
    /// </summary>
    public static bool TryParseList(ReadOnlySpan<char> text, List<double> values)
    {
        int i = 0;
        SkipSpace(text, ref i);
        while (i < text.Length)
        {
            if (!TryRead(text, ref i, out double value))
            {
                return false;
            }

            values.Add(value);
            int before = i;
            SkipSeparator(text, ref i);
            if (i < text.Length && i == before && !(text[i] is '+' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
