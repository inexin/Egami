using System.Globalization;

namespace Egami.Svg.Animation;

/// <summary>What a begin or end value waits for (SMIL 3.0 Timing, section 5.4.3, "Timing attribute value grammars").</summary>
internal enum SvgTimingKind : byte
{
    /// <summary>A time from the document's begin: <c>2s</c>, <c>-1s</c>.</summary>
    Offset,

    /// <summary>Another animation's begin: <c>a.begin+1s</c>.</summary>
    SyncBegin,

    /// <summary>Another animation's end: <c>a.end-0.5s</c>.</summary>
    SyncEnd,

    /// <summary>The start of another animation's n-th repetition: <c>a.repeat(2)</c>.</summary>
    Repeat,

    /// <summary>A user event, an access key or a wall-clock time: they never fire in a document nobody touches.</summary>
    Event,

    /// <summary><c>indefinite</c>: only a script's beginElement or endElement would resolve it.</summary>
    Indefinite,
}

/// <summary>One value of a begin or end list, its offset in seconds.</summary>
/// <param name="Kind">What the value waits for.</param>
/// <param name="Offset">The offset in seconds, added to what the value refers to.</param>
/// <param name="Id">The element a syncbase or repeat value names.</param>
/// <param name="Iteration">The repetition a repeat value names.</param>
internal readonly record struct SvgTimingValue(SvgTimingKind Kind, double Offset, string? Id = null, long Iteration = 0);

/// <summary>Reads SMIL clock values and begin and end lists.</summary>
internal static class SvgTiming
{
    /// <summary>
    /// Reads a clock value: <c>02:30:03</c>, <c>02:33.5</c>, <c>10h</c>, <c>45min</c>, <c>30s</c>, <c>5ms</c> or <c>12.467</c>
    /// (seconds), with whitespace around (SMIL 3.0 Timing, "Clock values").
    /// </summary>
    public static bool TryParseClock(ReadOnlySpan<char> text, out double seconds)
    {
        seconds = 0;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        int colon = text.IndexOf(':');
        if (colon >= 0)
        {
            int second = text[(colon + 1)..].IndexOf(':');
            if (second >= 0)
            {
                // Full clock: hours (any digits), then two-digit minutes and seconds.
                ReadOnlySpan<char> hoursText = text[..colon];
                ReadOnlySpan<char> rest = text[(colon + 1)..];
                if (!Digits(hoursText) || !long.TryParse(hoursText, NumberStyles.None, CultureInfo.InvariantCulture, out long hours) ||
                    !TryMinutesSeconds(rest, out double ms))
                {
                    return false;
                }

                seconds = (hours * 3600.0) + ms;
                return double.IsFinite(seconds);
            }

            return TryMinutesSeconds(text, out seconds);
        }

        // Timecount: a number with an optional fraction and metric.
        int end = 0;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] == '.'))
        {
            end++;
        }

        ReadOnlySpan<char> number = text[..end];
        ReadOnlySpan<char> metric = text[end..];
        if (!Decimal(number, out double value))
        {
            return false;
        }

        double scale = metric switch
        {
            "" or "s" => 1,
            "ms" => 0.001,
            "min" => 60,
            "h" => 3600,
            _ => double.NaN,
        };
        seconds = value * scale;
        return double.IsFinite(seconds);
    }

    /// <summary>
    /// Reads a begin or end list: values separated by semicolons. Values that cannot be read are left out and counted in
    /// <paramref name="errors"/>; the rest are kept, as browsers keep them.
    /// </summary>
    public static List<SvgTimingValue> ParseList(string text, out int errors)
    {
        List<SvgTimingValue> values = [];
        errors = 0;
        foreach (string part in text.Split(';'))
        {
            ReadOnlySpan<char> item = part.AsSpan().Trim();
            if (item.IsEmpty)
            {
                continue;
            }

            if (TryParseValue(item, out SvgTimingValue value))
            {
                values.Add(value);
            }
            else
            {
                errors++;
            }
        }

        return values;
    }

    /// <summary>Reads one begin or end value.</summary>
    public static bool TryParseValue(ReadOnlySpan<char> text, out SvgTimingValue value)
    {
        value = default;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return false;
        }

        if (text.SequenceEqual("indefinite"))
        {
            value = new SvgTimingValue(SvgTimingKind.Indefinite, 0);
            return true;
        }

        // An offset value: an optional sign, then a clock value.
        char first = text[0];
        if (first is '+' or '-' or '.' || char.IsAsciiDigit(first))
        {
            double sign = 1;
            ReadOnlySpan<char> clock = text;
            if (first is '+' or '-')
            {
                sign = first == '-' ? -1 : 1;
                clock = text[1..];
            }

            if (!TryParseClock(clock, out double seconds))
            {
                return false;
            }

            value = new SvgTimingValue(SvgTimingKind.Offset, sign * seconds);
            return true;
        }

        if (text.StartsWith("wallclock(") || text.StartsWith("accessKey("))
        {
            int close = text.IndexOf(')');
            if (close < 0 || !TryOffset(text[(close + 1)..], out double offset))
            {
                return false;
            }

            value = new SvgTimingValue(SvgTimingKind.Event, offset);
            return true;
        }

        // Id-value "." token, with "\" escaping a dot or dash in the id; without an id, the token names an event or repeat of
        // the animation's target, which never fires.
        int dot = -1;
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\')
            {
                i++;
                continue;
            }

            if (text[i] == '.')
            {
                dot = i;
                break;
            }
        }

        string? id = null;
        ReadOnlySpan<char> token = text;
        if (dot >= 0)
        {
            id = Unescape(text[..dot]);
            token = text[(dot + 1)..];
            if (id.Length == 0)
            {
                return false;
            }
        }

        // The token's name runs to a sign (an offset) or whitespace; repeat(n) keeps its parentheses.
        int nameEnd = 0;
        while (nameEnd < token.Length && token[nameEnd] is not ('+' or '-' or ' ' or '\t' or '\r' or '\n'))
        {
            if (token[nameEnd] == '(')
            {
                int close = token[nameEnd..].IndexOf(')');
                if (close < 0)
                {
                    return false;
                }

                nameEnd += close + 1;
                break;
            }

            nameEnd++;
        }

        ReadOnlySpan<char> name = token[..nameEnd];
        if (name.IsEmpty || !TryOffset(token[nameEnd..], out double delta))
        {
            return false;
        }

        if (name.StartsWith("repeat(") && name.EndsWith(")"))
        {
            ReadOnlySpan<char> count = name[7..^1].Trim();
            if (!Digits(count) || !long.TryParse(count, NumberStyles.None, CultureInfo.InvariantCulture, out long iteration))
            {
                return false;
            }

            value = id is null ? new SvgTimingValue(SvgTimingKind.Event, delta) : new SvgTimingValue(SvgTimingKind.Repeat, delta, id, iteration);
            return true;
        }

        if (name.Contains('(') || name.Contains(')') || name.Contains('\\'))
        {
            return false;
        }

        if (id is not null && name.SequenceEqual("begin"))
        {
            value = new SvgTimingValue(SvgTimingKind.SyncBegin, delta, id);
            return true;
        }

        if (id is not null && name.SequenceEqual("end"))
        {
            value = new SvgTimingValue(SvgTimingKind.SyncEnd, delta, id);
            return true;
        }

        if (!char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        value = new SvgTimingValue(SvgTimingKind.Event, delta);
        return true;
    }

    /// <summary>Reads an optional offset after a syncbase or event: whitespace, a sign, whitespace, a clock value.</summary>
    private static bool TryOffset(ReadOnlySpan<char> text, out double offset)
    {
        offset = 0;
        text = text.Trim();
        if (text.IsEmpty)
        {
            return true;
        }

        if (text[0] is not ('+' or '-'))
        {
            return false;
        }

        double sign = text[0] == '-' ? -1 : 1;
        if (!TryParseClock(text[1..], out double seconds))
        {
            return false;
        }

        offset = sign * seconds;
        return true;
    }

    private static bool TryMinutesSeconds(ReadOnlySpan<char> text, out double seconds)
    {
        seconds = 0;
        int colon = text.IndexOf(':');
        if (colon != 2)
        {
            return false;
        }

        ReadOnlySpan<char> minutesText = text[..2];
        ReadOnlySpan<char> secondsText = text[3..];
        int dot = secondsText.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? secondsText : secondsText[..dot];
        if (!Digits(minutesText) || whole.Length != 2 || !Digits(whole) || (dot >= 0 && (dot == secondsText.Length - 1 || !Digits(secondsText[(dot + 1)..]))))
        {
            return false;
        }

        int minutes = int.Parse(minutesText, NumberStyles.None, CultureInfo.InvariantCulture);
        if (!Decimal(secondsText, out double s) || minutes > 59 || s >= 60)
        {
            return false;
        }

        seconds = (minutes * 60) + s;
        return true;
    }

    /// <summary>Digits with an optional fraction: <c>12</c>, <c>12.5</c>, <c>.5</c> is not allowed by SMIL, but browsers read it.</summary>
    private static bool Decimal(ReadOnlySpan<char> text, out double value)
    {
        value = 0;
        if (text.IsEmpty || text[^1] == '.' || text.Count('.') > 1)
        {
            return false;
        }

        return double.TryParse(text, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
    }

    private static bool Digits(ReadOnlySpan<char> text)
    {
        if (text.IsEmpty)
        {
            return false;
        }

        foreach (char c in text)
        {
            if (!char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }

    private static string Unescape(ReadOnlySpan<char> text)
    {
        if (!text.Contains('\\'))
        {
            return text.ToString();
        }

        System.Text.StringBuilder b = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] == '\\' && i + 1 < text.Length)
            {
                i++;
            }

            b.Append(text[i]);
        }

        return b.ToString();
    }
}
