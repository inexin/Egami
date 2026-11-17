namespace Egami.Video.Codecs;

/// <summary>
/// The HEVC settings beyond <see cref="VideoEncoderOptions"/>, for experts (<see cref="VideoEncoderOptions.Hevc"/>): the profile
/// and level, quantizer limits, adaptive quantization, B-frames and the framing of the packets. Every one has a default that
/// suits most uses. The encoder lives in the Egami.Video.Hevc package.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoHevc.cs" region="encode"/>
/// </example>
public sealed record HevcEncoderOptions
{
    private readonly int? _level;
    private readonly int? _bFrames;
    private readonly int _minQuantizer;
    private readonly int _maxQuantizer = 51;

    /// <summary>Gets the defaults.</summary>
    public static HevcEncoderOptions Default { get; } = new();

    /// <summary>
    /// Gets the profile, or <see langword="null"/> (the default) to follow the first frame: <see cref="HevcProfile.Main10"/> for
    /// 16-bit planes, else <see cref="HevcProfile.Main"/>. 8-bit frames coded as Main 10 keep their values, shifted up two bits;
    /// deeper frames coded as Main are converted to 8 bits.
    /// </summary>
    public HevcProfile? Profile { get; init; }

    /// <summary>
    /// Gets the level, ten times the level number (41 for level 4.1), 10 to 62, or <see langword="null"/> (the default) for the
    /// lowest Main tier level that the frame size and rate fit. A stream that does not fit the level given is refused when the
    /// encoder is made.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a level of HEVC's Table A.8.</exception>
    public int? Level
    {
        get => _level;
        init => _level = value is int v && !IsLevel(v) ? throw new ArgumentOutOfRangeException(nameof(value), v, "The level must be one of HEVC's (10, 20, 21, 30, 31, 40, 41, 50, 51, 52, 60, 61 or 62).") : value;
    }

    /// <summary>Gets the smallest QP rate control may use, 0 (finest) to 51. Default 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 51.</exception>
    public int MinQuantizer
    {
        get => _minQuantizer;
        init => _minQuantizer = InRange(value, 0, 51);
    }

    /// <summary>Gets the largest QP rate control may use, 0 to 51. Default 51.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 51.</exception>
    public int MaxQuantizer
    {
        get => _maxQuantizer;
        init => _maxQuantizer = InRange(value, 0, 51);
    }

    /// <summary>
    /// Gets whether each block's quantizer follows how busy it is (adaptive quantization): detail and texture, where errors show
    /// less, are coded coarser and flat areas finer, which looks better at the same size. Default on; turn it off to measure
    /// PSNR, which it lowers.
    /// </summary>
    public bool AdaptiveQuantization { get; init; } = true;

    /// <summary>
    /// Gets the most B-frames between two reference frames, 0 to 16, or <see langword="null"/> (the default) for the effort's:
    /// none at effort 0, else 3. B-frames predict from the frames on both sides and take fewer bits. They need
    /// <see cref="LatencyMode.Quality"/> (the frames are coded out of order, and packets carry decode timestamps).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 16.</exception>
    public int? BFrames
    {
        get => _bFrames;
        init => _bFrames = value is int v ? InRange(v, 0, 16) : null;
    }

    /// <summary>
    /// Gets whether packets are Annex B byte streams (start codes, with the parameter sets in every keyframe's packet), as raw
    /// .h265 files, MPEG-TS and RTP senders want. Default off: packets are length-prefixed and the parameter sets go in the
    /// track's hvcC record (<see cref="TrackInfo.CodecPrivate"/>), as MP4 (hvc1) and Matroska store them.
    /// </summary>
    public bool AnnexB { get; init; }

    private static bool IsLevel(int level) => level is 10 or 20 or 21 or 30 or 31 or 40 or 41 or 50 or 51 or 52 or 60 or 61 or 62;

    private static int InRange(int value, int min, int max) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(nameof(value), value, $"The value must be {min} to {max}.") : value;
}

/// <summary>The HEVC profiles Egami encodes: 4:2:0 at 8 bits, or at up to 10.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoHevc.cs" region="encode"/>
/// </example>
public enum HevcProfile
{
    /// <summary>Main: 8-bit 4:2:0, which every HEVC decoder plays.</summary>
    Main,

    /// <summary>Main 10: 10-bit 4:2:0, for HDR and for fewer bands in smooth gradients.</summary>
    Main10,
}
