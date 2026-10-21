using System.Globalization;

namespace Egami.Cli.Host;

/// <summary>
/// Parses <c>--limit</c> and <c>EGAMI_LIMITS</c>: comma-separated <c>name=value</c> pairs over the library's
/// <see cref="SafetyLimits"/>. Names: <c>pixels</c> (a count such as <c>50M</c>, or <c>WxH</c>), <c>width</c>, <c>height</c>,
/// <c>frames</c>, <c>memory</c> and <c>metadata</c> (bytes, such as <c>512MB</c> or <c>2GB</c>; KB, MB and GB count in 1024s).
/// </summary>
internal static class LimitSpec
{
    /// <summary>The names a limit takes.</summary>
    public static readonly string[] Names = ["pixels", "width", "height", "frames", "memory", "metadata"];

    /// <summary>Applies a limit list to a set of limits; <see langword="null"/> or empty text changes nothing.</summary>
    public static SafetyLimits Apply(SafetyLimits limits, string? text, string source)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return limits;
        }

        foreach (string part in text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            int equals = part.IndexOf('=', StringComparison.Ordinal);
            if (equals <= 0)
            {
                throw new UsageException($"{source}: '{part}' is not name=value; names are {string.Join(", ", Names)}.");
            }

            string name = part[..equals].Trim().ToLowerInvariant();
            string value = part[(equals + 1)..].Trim();
            limits = name switch
            {
                "pixels" => limits with { MaxPixels = Pixels(value, source) },
                "width" => limits with { MaxWidth = (int)Math.Min(int.MaxValue, Count(value, source)) },
                "height" => limits with { MaxHeight = (int)Math.Min(int.MaxValue, Count(value, source)) },
                "frames" => limits with { MaxFrames = (int)Math.Min(int.MaxValue, Count(value, source)) },
                "memory" => limits with { MaxTotalBytes = Bytes(value, source) },
                "metadata" => limits with { MaxMetadataBytes = Bytes(value, source) },
                _ => throw new UsageException($"{source}: unknown limit '{name}'; names are {string.Join(", ", Names)}."),
            };
        }

        return limits;
    }

    private static long Pixels(string value, string source)
    {
        int x = value.IndexOfAny(['x', 'X']);
        if (x > 0 && long.TryParse(value.AsSpan(0, x), NumberStyles.None, CultureInfo.InvariantCulture, out long w)
            && long.TryParse(value.AsSpan(x + 1), NumberStyles.None, CultureInfo.InvariantCulture, out long h) && w > 0 && h > 0)
        {
            return checked(w * h);
        }

        return Count(value, source);
    }

    /// <summary>A positive count with an optional k, M or G (thousands, millions, billions).</summary>
    private static long Count(string value, string source)
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
    private static long Bytes(string value, string source)
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
