using Egami.Formats;

namespace Egami;

/// <summary>
/// Every setting that is not specific to one call: where memory comes from, how many threads an operation may use, and the safety limits.
/// Contexts are immutable and safe to share. Change one with a <c>with</c> expression.
/// </summary>
/// <example>
/// <code>
/// var context = EgamiContext.Default with
/// {
///     Limits = SafetyLimits.Default with { MaxPixels = 100_000_000 },
///     MaxParallelism = 4,
/// };
/// </code>
/// </example>
public sealed record EgamiContext
{
    private readonly IPixelMemorySource _memory = PooledPixelMemorySource.Shared;
    private readonly int _maxParallelism = Environment.ProcessorCount;
    private readonly SafetyLimits _limits = SafetyLimits.Default;
    private readonly IReadOnlyList<FormatInfo> _formats = [Egami.Formats.Png.PngFormat.Info, Egami.Formats.Jpeg.JpegFormat.Info, Egami.Formats.Gif.GifFormat.Info, Egami.Formats.Qoi.QoiFormat.Info, Egami.Formats.Pnm.PnmFormat.Info];

    /// <summary>Gets the context used when a call passes none.</summary>
    public static EgamiContext Default { get; } = new();

    /// <summary>Gets the source of all pixel memory. Defaults to <see cref="PooledPixelMemorySource.Shared"/>.</summary>
    public IPixelMemorySource Memory { get => _memory; init => _memory = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>Gets the most threads one operation may use. 1 means fully sequential. Defaults to the processor count.</summary>
    public int MaxParallelism
    {
        get => _maxParallelism;
        init
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            _maxParallelism = value;
        }
    }

    /// <summary>Gets the safety limits applied when reading. Defaults to <see cref="SafetyLimits.Default"/>.</summary>
    public SafetyLimits Limits { get => _limits; init => _limits = value ?? throw new ArgumentNullException(nameof(value)); }

    /// <summary>Gets the registered formats, in detection order. Defaults to PNG, JPEG, GIF, QOI and PNM.</summary>
    public IReadOnlyList<FormatInfo> Formats
    {
        get => _formats;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            _formats = [.. value];
        }
    }

    /// <summary>
    /// Returns a copy of this context with <paramref name="format"/> registered. A format with the same name is replaced in place;
    /// otherwise the new format is detected last.
    /// </summary>
    /// <param name="format">The format to add.</param>
    /// <returns>The new context.</returns>
    public EgamiContext WithFormat(FormatInfo format)
    {
        ArgumentNullException.ThrowIfNull(format);
        List<FormatInfo> formats = [.. _formats];
        int existing = formats.FindIndex(f => string.Equals(f.Name, format.Name, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
        {
            formats[existing] = format;
        }
        else
        {
            formats.Add(format);
        }

        return this with { Formats = formats };
    }

    /// <summary>Finds a registered format by name or file extension, with or without a leading dot, ignoring case.</summary>
    internal FormatInfo? FindFormat(string nameOrExtension)
    {
        string key = nameOrExtension.TrimStart('.');
        foreach (FormatInfo format in _formats)
        {
            if (string.Equals(format.Name, key, StringComparison.OrdinalIgnoreCase))
            {
                return format;
            }
        }

        foreach (FormatInfo format in _formats)
        {
            foreach (string extension in format.Extensions)
            {
                if (string.Equals(extension.TrimStart('.'), key, StringComparison.OrdinalIgnoreCase))
                {
                    return format;
                }
            }
        }

        return null;
    }
}
