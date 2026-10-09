namespace Egami;

/// <summary>
/// Limits that protect against hostile or broken files. Readers check sizes from the header before allocating any pixel memory.
/// The defaults let normal photos and screenshots through. Change them with a <c>with</c> expression.
/// </summary>
/// <remarks>
/// Limits apply to reading. Operations (<c>Apply</c>, <c>Derive</c>) and adding frames check <see cref="MaxTotalBytes"/> too;
/// writers check nothing. Every limit must be at least 1; setting a smaller value throws <see cref="ArgumentOutOfRangeException"/>.
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/MemoryAndSecurity.cs" region="untrusted"/>
/// </example>
public sealed record SafetyLimits
{
    private readonly int _maxWidth = 65_535;
    private readonly int _maxHeight = 65_535;
    private readonly long _maxPixels = 16_384L * 16_384;
    private readonly int _maxFrames = 10_000;
    private readonly long _maxTotalBytes = 2L * 1024 * 1024 * 1024;
    private readonly long _maxMetadataBytes = 16L * 1024 * 1024;

    /// <summary>Gets the default limits.</summary>
    public static SafetyLimits Default { get; } = new();

    /// <summary>Gets the largest width, in pixels. Defaults to 65,535.</summary>
    public int MaxWidth { get => _maxWidth; init => _maxWidth = Positive(value); }

    /// <summary>Gets the largest height, in pixels. Defaults to 65,535.</summary>
    public int MaxHeight { get => _maxHeight; init => _maxHeight = Positive(value); }

    /// <summary>Gets the largest number of pixels in one frame. Defaults to 268,435,456 (16,384 × 16,384).</summary>
    public long MaxPixels { get => _maxPixels; init => _maxPixels = Positive(value); }

    /// <summary>Gets the largest number of frames. Defaults to 10,000.</summary>
    public int MaxFrames { get => _maxFrames; init => _maxFrames = Positive(value); }

    /// <summary>
    /// Gets the most memory, in bytes, that one load or one chain of operations may hold at once, counting all frames and temporary
    /// buffers. Defaults to 2 GB.
    /// </summary>
    public long MaxTotalBytes { get => _maxTotalBytes; init => _maxTotalBytes = Positive(value); }

    /// <summary>Gets the largest total size, in bytes, of metadata such as EXIF, ICC, XMP and text. Defaults to 16 MB.</summary>
    public long MaxMetadataBytes { get => _maxMetadataBytes; init => _maxMetadataBytes = Positive(value); }

    /// <summary>Throws if a frame of this size breaks <see cref="MaxWidth"/>, <see cref="MaxHeight"/> or <see cref="MaxPixels"/>.</summary>
    internal void CheckFrameSize(long width, long height)
    {
        if (width > MaxWidth)
        {
            throw new LimitExceededException(nameof(MaxWidth), MaxWidth, width);
        }

        if (height > MaxHeight)
        {
            throw new LimitExceededException(nameof(MaxHeight), MaxHeight, height);
        }

        if (width * height > MaxPixels)
        {
            throw new LimitExceededException(nameof(MaxPixels), MaxPixels, width * height);
        }
    }

    /// <summary>Throws if <paramref name="frames"/> breaks <see cref="MaxFrames"/>.</summary>
    internal void CheckFrameCount(long frames)
    {
        if (frames > MaxFrames)
        {
            throw new LimitExceededException(nameof(MaxFrames), MaxFrames, frames);
        }
    }

    /// <summary>Throws if <paramref name="bytes"/> of metadata breaks <see cref="MaxMetadataBytes"/>.</summary>
    internal void CheckMetadataBytes(long bytes)
    {
        if (bytes > MaxMetadataBytes)
        {
            throw new LimitExceededException(nameof(MaxMetadataBytes), MaxMetadataBytes, bytes);
        }
    }

    private static int Positive(int value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }

    private static long Positive(long value)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
        return value;
    }
}
