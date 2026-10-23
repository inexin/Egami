using System.Globalization;

using Egami.Cli.Hosting;

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
                "width" => limits with { MaxWidth = (int)Math.Min(int.MaxValue, LimitText.Count(value, source)) },
                "height" => limits with { MaxHeight = (int)Math.Min(int.MaxValue, LimitText.Count(value, source)) },
                "frames" => limits with { MaxFrames = (int)Math.Min(int.MaxValue, LimitText.Count(value, source)) },
                "memory" => limits with { MaxTotalBytes = LimitText.Bytes(value, source) },
                "metadata" => limits with { MaxMetadataBytes = LimitText.Bytes(value, source) },
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

        return LimitText.Count(value, source);
    }
}
