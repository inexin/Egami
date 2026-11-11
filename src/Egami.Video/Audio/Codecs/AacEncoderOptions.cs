namespace Egami.Video.Audio.Codecs;

/// <summary>
/// How the AAC encoder codes: an average bit rate (the default, 64 kbit/s per channel) or a variable-bit-rate quality, and the
/// highest frequency it keeps. The encoder picks its tools (block switching, TNS, mid/side, intensity stereo and noise
/// substitution) per block from the rate.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Audio.cs" region="audio-aac-encode"/>
/// </example>
public sealed record AacEncoderOptions
{
    private readonly int? _bitrate;
    private readonly int? _quality;
    private readonly int? _bandwidth;

    /// <summary>Gets the defaults.</summary>
    public static AacEncoderOptions Default { get; } = new();

    /// <summary>
    /// Gets the average bit rate in bits per second for all channels together, 8000 to 1536000, or <see langword="null"/> (the
    /// default) for 64 kbit/s per channel. Blocks that need more bits take them from a reservoir that quieter blocks fill, so
    /// the rate holds over a few seconds. AAC LC carries at most 6144 bits per channel in a block, which caps the rate at
    /// about 6 bits per sample and channel.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 8000 to 1536000.</exception>
    public int? Bitrate
    {
        get => _bitrate;
        init => _bitrate = value is int v ? InRange(v, 8000, 1_536_000) : null;
    }

    /// <summary>
    /// Gets the variable-bit-rate quality, 1 (smallest) to 5 (best), or <see langword="null"/> (the default) to code at
    /// <see cref="Bitrate"/>. Each level keeps the noise at one level against the masking thresholds, so the rate follows the
    /// audio; music in stereo comes out at roughly 50, 80, 112, 170 and 235 kbit/s.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 5.</exception>
    public int? Quality
    {
        get => _quality;
        init => _quality = value is int v ? InRange(v, 1, 5) : null;
    }

    /// <summary>
    /// Gets the highest frequency coded in hertz, 1000 to 24000, or <see langword="null"/> (the default) to choose it from the
    /// rate: about 11 kHz at 16 kbit/s per channel, 15 kHz at 24 and 20 kHz from 32. It never exceeds half the sample rate.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1000 to 24000.</exception>
    public int? Bandwidth
    {
        get => _bandwidth;
        init => _bandwidth = value is int v ? InRange(v, 1000, 24_000) : null;
    }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}
