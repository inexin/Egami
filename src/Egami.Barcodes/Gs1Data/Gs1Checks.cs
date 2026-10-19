using System.Globalization;

namespace Egami.Barcodes.Gs1Data;

/// <summary>
/// Checks a data field against its AI's components: character sets, lengths, and the content checks of the GS1 Barcode Syntax
/// Tests that this package implements (check digits, dates, times and the small enumerations). Checks that need outside tables
/// (country, currency and company prefix lists) are not applied.
/// </summary>
internal static class Gs1Checks
{
    private const string Cset82 = "!\"%&'()*+,-./0123456789:;<=>?ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";
    private const string Cset39 = "#-/0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>Returns what is wrong with the data, or <see langword="null"/>.</summary>
    public static string? Check(Gs1AiSpec spec, string value)
    {
        if (value.Length == 0)
        {
            return "the data is empty.";
        }

        int at = 0;
        foreach (Gs1Component component in spec.Components)
        {
            if (at == value.Length && component.Optional)
            {
                break;
            }

            int length = component.Variable ? Math.Min(component.Length, value.Length - at) : component.Length;
            if (length == 0 || at + length > value.Length)
            {
                return $"the data is too short; it needs {Describe(spec)}.";
            }

            ReadOnlySpan<char> part = value.AsSpan(at, length);
            if (Characters(component.Type, part) is string bad)
            {
                return $"'{bad}' at position {at + part.IndexOf(bad[0]) + 1} is not allowed; it needs {Describe(spec)}.";
            }

            foreach (string check in component.Checks)
            {
                if (Content(check, part) is string error)
                {
                    return error;
                }
            }

            at += length;
        }

        return at < value.Length ? $"the data is too long; it needs {Describe(spec)}." : null;
    }

    /// <summary>Returns the GS1 mod 10 check digit of digits, as GTINs, SSCCs and GLNs use (weights 3 and 1 from the right).</summary>
    public static int CheckDigit(ReadOnlySpan<char> digits)
    {
        int sum = 0;
        for (int i = 0; i < digits.Length; i++)
        {
            int digit = digits[digits.Length - 1 - i] - '0';
            sum += i % 2 == 0 ? digit * 3 : digit;
        }

        return (10 - (sum % 10)) % 10;
    }

    private static string? Characters(char type, ReadOnlySpan<char> part)
    {
        foreach (char c in part)
        {
            bool ok = type switch
            {
                'N' => char.IsAsciiDigit(c),
                'X' => Cset82.Contains(c, StringComparison.Ordinal),
                'Y' => Cset39.Contains(c, StringComparison.Ordinal),
                _ => char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '=',
            };
            if (!ok)
            {
                return c.ToString();
            }
        }

        return null;
    }

    private static string? Content(string check, ReadOnlySpan<char> part)
    {
        switch (check)
        {
            case "csum":
                int expected = CheckDigit(part[..^1]);
                return part[^1] - '0' == expected ? null : $"the check digit should be {expected}, not {part[^1]}.";
            case "yymmd0":
                return Date(part, 2, allowZeroDay: true);
            case "yymmdd":
                return Date(part, 2, allowZeroDay: false);
            case "yyyymmdd":
                return Date(part, 4, allowZeroDay: false);
            case "hhmi":
                return Number(part[..2], 0, 23, "hour") ?? Number(part[2..], 0, 59, "minute");
            case "hh":
                return Number(part, 0, 23, "hour");
            case "mi":
                return Number(part, 0, 59, "minute");
            case "ss":
                return Number(part, 0, 59, "second");
            case "yesno":
                return part is "0" or "1" ? null : $"'{part}' should be 0 or 1.";
            case "zero":
                return part is "0" ? null : $"'{part}' should be 0.";
            case "nonzero":
                return part.ContainsAnyExcept('0') ? null : "the value should not be zero.";
            case "winding":
                return part is "0" or "1" or "9" ? null : $"'{part}' should be 0, 1 or 9.";
            case "iso5218":
                return part is "0" or "1" or "2" or "9" ? null : $"'{part}' should be 0, 1, 2 or 9.";
            case "hyphen":
                return part.ContainsAnyExcept('-') ? $"'{part}' should be a hyphen." : null;
            case "nozeroprefix":
                return part.Length > 1 && part[0] == '0' ? $"'{part}' should not start with 0." : null;
            case "hasnondigit":
                return part.ContainsAnyExceptInRange('0', '9') ? null : $"'{part}' should hold a character other than a digit.";
            case "pieceoftotal":
                int half = part.Length / 2;
                if (part.Length % 2 != 0 || !int.TryParse(part[..half], CultureInfo.InvariantCulture, out int piece) || !int.TryParse(part[half..], CultureInfo.InvariantCulture, out int total))
                {
                    return $"'{part}' should be a piece number and a total of equal length.";
                }

                return piece >= 1 && piece <= total ? null : $"piece {piece} of {total} is not possible.";
            default:
                return null;
        }
    }

    private static string? Date(ReadOnlySpan<char> part, int yearDigits, bool allowZeroDay)
    {
        int year = int.Parse(part[..yearDigits], CultureInfo.InvariantCulture), month = int.Parse(part.Slice(yearDigits, 2), CultureInfo.InvariantCulture), day = int.Parse(part.Slice(yearDigits + 2, 2), CultureInfo.InvariantCulture);
        if (month is < 1 or > 12)
        {
            return $"month {month:00} in '{part}' is not a month.";
        }

        // A two-digit year is in a window around today (GS1 General Specifications 7.12); for days, a leap year every four
        // years is close enough between 1951 and 2099.
        int fullYear = yearDigits == 4 ? year : 2000 + year;
        int days = DateTime.DaysInMonth(fullYear, month);
        return (day == 0 && allowZeroDay) || (day >= 1 && day <= days) ? null : $"day {day:00} in '{part}' is not in month {month:00}.";
    }

    private static string? Number(ReadOnlySpan<char> part, int min, int max, string name) =>
        int.Parse(part, CultureInfo.InvariantCulture) is int n && n >= min && n <= max ? null : $"{name} {part} is not between {min:00} and {max:00}.";

    private static string Describe(Gs1AiSpec spec) => string.Join(" then ", spec.Components.Select(c =>
        $"{(c.Optional ? "optionally " : "")}{(c.Variable ? "up to " : "")}{c.Length} {c.Type switch { 'N' => "digits", 'X' => "characters", 'Y' => "characters of set 39", _ => "base64url characters" }}"));
}
