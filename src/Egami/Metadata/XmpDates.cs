using System.Globalization;

namespace Egami.Metadata;

/// <summary>XMP dates: the ISO 8601 subset the XMP specification part 1 allows.</summary>
internal static class XmpDates
{
    public static bool TryParse(string text, out DateTimeOffset value)
    {
        value = default;
        ReadOnlySpan<char> s = text.AsSpan().Trim();
        if (s.Length < 4 || !Int(s[..4], out int year))
        {
            return false;
        }

        int month = 1, day = 1, hour = 0, minute = 0, second = 0;
        long ticks = 0;
        TimeSpan offset = TimeSpan.Zero;
        int at = 4;
        if (at < s.Length && s[at] == '-')
        {
            if (s.Length < at + 3 || !Int(s.Slice(at + 1, 2), out month))
            {
                return false;
            }

            at += 3;
            if (at < s.Length && s[at] == '-')
            {
                if (s.Length < at + 3 || !Int(s.Slice(at + 1, 2), out day))
                {
                    return false;
                }

                at += 3;
            }
        }

        if (at < s.Length && s[at] == 'T')
        {
            if (s.Length < at + 6 || s[at + 3] != ':' || !Int(s.Slice(at + 1, 2), out hour) || !Int(s.Slice(at + 4, 2), out minute))
            {
                return false;
            }

            at += 6;
            if (at < s.Length && s[at] == ':')
            {
                if (s.Length < at + 3 || !Int(s.Slice(at + 1, 2), out second))
                {
                    return false;
                }

                at += 3;
                if (at < s.Length && s[at] == '.')
                {
                    int start = ++at;
                    while (at < s.Length && char.IsAsciiDigit(s[at]))
                    {
                        at++;
                    }

                    ReadOnlySpan<char> fraction = s[start..at];
                    if (fraction.IsEmpty)
                    {
                        return false;
                    }

                    ReadOnlySpan<char> seven = fraction.Length > 7 ? fraction[..7] : fraction;
                    ticks = long.Parse(seven, NumberStyles.None, CultureInfo.InvariantCulture) * (long)Math.Pow(10, 7 - seven.Length);
                }
            }

            if (at < s.Length)
            {
                if (s[at] == 'Z')
                {
                    at++;
                }
                else if (s[at] is '+' or '-' && s.Length >= at + 6 && s[at + 3] == ':'
                    && Int(s.Slice(at + 1, 2), out int oh) && Int(s.Slice(at + 4, 2), out int om) && oh <= 14 && om < 60)
                {
                    offset = new TimeSpan(oh, om, 0) * (s[at] == '-' ? -1 : 1);
                    at += 6;
                }
                else
                {
                    return false;
                }
            }
        }

        if (at != s.Length || year < 1 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month) || hour > 23 || minute > 59 || second > 59)
        {
            return false;
        }

        value = new DateTimeOffset(year, month, day, hour, minute, second, offset).AddTicks(ticks);
        return true;
    }

    public static string Format(DateTimeOffset value)
    {
        string zone = value.Offset == TimeSpan.Zero ? "Z" : value.ToString("zzz", CultureInfo.InvariantCulture);
        string seconds = value.Ticks % TimeSpan.TicksPerSecond == 0 ? "ss" : "ss.FFFFFFF";
        return value.ToString($"yyyy-MM-ddTHH:mm:{seconds}", CultureInfo.InvariantCulture) + zone;
    }

    private static bool Int(ReadOnlySpan<char> s, out int value) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out value);
}
