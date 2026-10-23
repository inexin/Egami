using System.Globalization;

namespace Egami.Cli.Hosting;

/// <summary>Reads the values of <c>--limit</c> lists: counts such as <c>50M</c> and sizes such as <c>512MB</c>.</summary>
internal static class LimitText
{
    /// <summary>A positive count with an optional k, M or G (thousands, millions, billions).</summary>
    public static long Count(string value, string source)
    {
        (string number, long scale) = value.Length > 0 && char.ToUpperInvariant(value[^1]) is 'K' or 'M' or 'G'
            ? (value[..^1], char.ToUpperInvariant(value[^1]) switch { 'K' => 1_000L, 'M' => 1_000_000L, _ => 1_000_000_000L })
            : (value, 1L);
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double count) || count < 1 || count * scale > long.MaxValue)
        {
            throw new UsageException($"{source}: '{value}' is not a positive count.");
        }

        return (long)(count * scale);
    }

    /// <summary>A positive byte count with an optional KB, MB or GB (or K, M, G), counted in 1024s.</summary>
    public static long Bytes(string value, string source)
    {
        string text = value.ToUpperInvariant();
        if (text.EndsWith('B'))
        {
            text = text[..^1];
        }

        (string number, long scale) = text.Length > 0 && text[^1] is 'K' or 'M' or 'G'
            ? (text[..^1], text[^1] switch { 'K' => 1L << 10, 'M' => 1L << 20, _ => 1L << 30 })
            : (text, 1L);
        if (!double.TryParse(number, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out double count) || count < 1 || count * scale > long.MaxValue)
        {
            throw new UsageException($"{source}: '{value}' is not a size in bytes, such as 512MB.");
        }

        return (long)(count * scale);
    }
}
