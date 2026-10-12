namespace Egami.Svg.Reading;

/// <summary>
/// The limits an SVG load works within, taken from the context's <see cref="SafetyLimits"/> so one set of limits governs every
/// untrusted input: the file (after gzip) is bounded by MaxTotalBytes, entity expansion by MaxMetadataBytes, and the element
/// count scales with MaxTotalBytes. Nesting and reference depths are fixed: deeper files are not drawn by browsers either.
/// </summary>
internal sealed class SvgLimits
{
    /// <summary>The deepest element nesting read, 256 levels.</summary>
    public const int MaxDepth = 256;

    /// <summary>How many use, pattern, marker, mask, clip path, filter and nested image levels a reference chain may take.</summary>
    public const int MaxReferenceDepth = 32;

    public SvgLimits(SafetyLimits limits)
    {
        MaxBytes = limits.MaxTotalBytes;
        MaxEntityCharacters = limits.MaxMetadataBytes;
        MaxElements = Math.Max(10_000, limits.MaxTotalBytes / 256);
        MaxPathCommands = Math.Max(100_000, limits.MaxTotalBytes / 64);
    }

    /// <summary>Gets the largest file read, in bytes after gzip.</summary>
    public long MaxBytes { get; }

    /// <summary>Gets the most characters entities may expand to, in total.</summary>
    public long MaxEntityCharacters { get; }

    /// <summary>Gets the most elements a document may hold, counting copies made by use.</summary>
    public long MaxElements { get; }

    /// <summary>Gets the most path commands one path may have.</summary>
    public long MaxPathCommands { get; }
}
