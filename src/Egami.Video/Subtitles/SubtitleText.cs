using System.Globalization;
using System.Text;

namespace Egami.Video.Subtitles;

/// <summary>A line of a text file: where its content starts, how long it is, and where the next line starts.</summary>
internal readonly record struct TextLine(int Start, int Length, int Next)
{
    public ReadOnlySpan<char> Of(string text) => text.AsSpan(Start, Length);

    public string Raw(string text) => text[Start..Next];
}

/// <summary>Text helpers the subtitle readers and writers share: decoding, lines, times and numbers.</summary>
internal static class SubtitleText
{
    public static readonly Rational Milliseconds = new(1, 1000);
    public static readonly Rational Centiseconds = new(1, 100);

    // Windows-1252 0x80-0x9F; the five unassigned bytes map to the C1 controls, as browsers decode them.
    private static readonly char[] Cp1252High =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021', '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014', '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    ];

    /// <summary>
    /// Decodes a subtitle file: a byte order mark first, then UTF-8 if the bytes are valid UTF-8, then the caller's encoding,
    /// then UTF-16 when every other byte is zero, then Windows-1252. WebVTT without a byte order mark is UTF-8, as its spec says,
    /// with bad bytes replaced. A guess is reported as <c>Subtitle.Encoding</c>.
    /// </summary>
    public static string Decode(ReadOnlySpan<byte> data, Encoding? encoding, SubtitleLog log, out bool byteOrderMark)
    {
        byteOrderMark = true;
        if (data.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            return Checked(Encoding.UTF8, data[3..], log);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
        {
            return Checked(Encoding.Unicode, data[2..], log);
        }

        if (data.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
        {
            return Checked(Encoding.BigEndianUnicode, data[2..], log);
        }

        byteOrderMark = false;
        if (System.Text.Unicode.Utf8.IsValid(data))
        {
            return Encoding.UTF8.GetString(data);
        }

        if (encoding is not null)
        {
            return encoding.GetString(data);
        }

        if (data.StartsWith("WEBVTT"u8))
        {
            return Checked(Encoding.UTF8, data, log);
        }

        if (LooksLikeUtf16(data, out bool bigEndian))
        {
            log.Encoding($"The file has no byte order mark and is not UTF-8; it was read as UTF-16 {(bigEndian ? "big" : "little")}-endian.");
            return (bigEndian ? Encoding.BigEndianUnicode : Encoding.Unicode).GetString(data);
        }

        log.Encoding("The file is not UTF-8 and has no byte order mark; it was read as Windows-1252. Set SubtitleReaderOptions.Encoding if it is another code page.");
        return DecodeWindows1252(data);
    }

    /// <summary>Decodes in a Unicode encoding, reporting bytes that are not valid in it, which become U+FFFD.</summary>
    private static string Checked(Encoding encoding, ReadOnlySpan<byte> data, SubtitleLog log)
    {
        string text = encoding.GetString(data);
        if (encoding.GetByteCount(text) != data.Length || !encoding.GetBytes(text).AsSpan().SequenceEqual(data))
        {
            log.Encoding($"The file is not valid {encoding.WebName.ToUpperInvariant()}; the bytes that are not were read as U+FFFD.");
        }

        return text;
    }

    public static string DecodeWindows1252(ReadOnlySpan<byte> data)
    {
        return string.Create(data.Length, data.ToArray(), static (chars, bytes) =>
        {
            for (int i = 0; i < bytes.Length; i++)
            {
                byte b = bytes[i];
                chars[i] = b is >= 0x80 and <= 0x9F ? Cp1252High[b - 0x80] : (char)b;
            }
        });
    }

    private static bool LooksLikeUtf16(ReadOnlySpan<byte> data, out bool bigEndian)
    {
        bigEndian = false;
        int sample = Math.Min(data.Length & ~1, 512);
        if (sample < 4)
        {
            return false;
        }

        int evenZeros = 0, oddZeros = 0;
        for (int i = 0; i < sample; i += 2)
        {
            evenZeros += data[i] == 0 ? 1 : 0;
            oddZeros += data[i + 1] == 0 ? 1 : 0;
        }

        int pairs = sample / 2;
        bigEndian = evenZeros > oddZeros;
        return Math.Max(evenZeros, oddZeros) * 10 >= pairs * 7 && Math.Min(evenZeros, oddZeros) * 10 < pairs;
    }

    /// <summary>
    /// Replaces each line break (CR LF, CR or LF, the ones subtitle formats know) with <paramref name="newLine"/>. Unlike
    /// <see cref="string.ReplaceLineEndings(string)"/>, form feeds, NEL and the Unicode line and paragraph separators stay text.
    /// </summary>
    public static string ReplaceLineBreaks(this string text, string newLine)
    {
        if (text.AsSpan().IndexOfAny('\r', '\n') < 0)
        {
            return text;
        }

        StringBuilder output = new(text.Length);
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\r' or '\n')
            {
                output.Append(newLine);
                i += c == '\r' && i + 1 < text.Length && text[i + 1] == '\n' ? 1 : 0;
            }
            else
            {
                output.Append(c);
            }
        }

        return output.ToString();
    }

    /// <summary>Splits text into lines at CR LF, CR or LF, keeping where each line's terminator ends.</summary>
    public static List<TextLine> Lines(string text)
    {
        List<TextLine> lines = [];
        int start = 0;
        while (start < text.Length)
        {
            int end = text.AsSpan(start).IndexOfAny('\r', '\n');
            if (end < 0)
            {
                lines.Add(new TextLine(start, text.Length - start, text.Length));
                break;
            }

            end += start;
            int next = end + (text[end] == '\r' && end + 1 < text.Length && text[end + 1] == '\n' ? 2 : 1);
            lines.Add(new TextLine(start, end - start, next));
            start = next;
        }

        return lines;
    }

    /// <summary>Gets the first line ending in the text, or <see langword="null"/> when it has none.</summary>
    public static string? NewLineOf(string text)
    {
        int at = text.AsSpan().IndexOfAny('\r', '\n');
        return at < 0 ? null : text[at] == '\n' ? "\n" : at + 1 < text.Length && text[at + 1] == '\n' ? "\r\n" : "\r";
    }

    public static bool IsBlank(ReadOnlySpan<char> line) => line.IsWhiteSpace();

    /// <summary>Formats a time as hours, minutes, seconds and a fraction: <c>01:02:03,456</c> (SRT), <c>01:02:03.456</c> (WebVTT), <c>1:02:03.45</c> (ASS).</summary>
    public static string FormatTime(MediaTime time, Rational unit, char separator, int hourDigits)
    {
        long ticks = Math.Max(0, time.Rescale(unit).Ticks);
        long perSecond = unit.Denominator / unit.Numerator;
        int fractionDigits = perSecond == 1000 ? 3 : 2;
        long fraction = ticks % perSecond;
        long seconds = ticks / perSecond;
        string hours = (seconds / 3600).ToString(new string('0', hourDigits), CultureInfo.InvariantCulture);
        return string.Create(CultureInfo.InvariantCulture, $"{hours}:{seconds / 60 % 60:00}:{seconds % 60:00}{separator}{fraction.ToString(new string('0', fractionDigits), CultureInfo.InvariantCulture)}");
    }

    /// <summary>Formats a number the shortest way that reads back the same: 50, 12.5, -1.</summary>
    public static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    /// <summary>The latest time a subtitle file may give, in hours (about 11,400 years); a later one is malformed.</summary>
    public const long MaxHours = 100_000_000;

    /// <summary>
    /// Adds up a time given as hours, minutes, seconds and a fraction in <paramref name="perSecond"/> units, or returns
    /// <see langword="false"/> when it is later than <see cref="MaxHours"/>, so retiming it later cannot overflow.
    /// </summary>
    public static bool TryTotal(long hours, long minutes, long seconds, long fraction, long perSecond, out long total)
    {
        Int128 sum = (((((Int128)hours * 60) + minutes) * 60) + seconds) * perSecond + fraction;
        total = sum <= (Int128)MaxHours * 3600 * perSecond ? (long)sum : 0;
        return sum <= (Int128)MaxHours * 3600 * perSecond;
    }

    /// <summary>Reads digits from <paramref name="at"/>, returning their value (saturating) and how many there were.</summary>
    public static int Digits(ReadOnlySpan<char> s, ref int at, out long value)
    {
        int start = at;
        value = 0;
        while (at < s.Length && char.IsAsciiDigit(s[at]))
        {
            value = value > (long.MaxValue / 20) ? long.MaxValue / 2 : (value * 10) + (s[at] - '0');
            at++;
        }

        return at - start;
    }
}
