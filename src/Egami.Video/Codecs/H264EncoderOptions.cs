namespace Egami.Video.Codecs;

/// <summary>
/// The H.264 settings beyond <see cref="VideoEncoderOptions"/>, for experts (<see cref="VideoEncoderOptions.H264"/>): the profile
/// and level, the entropy coder, quantizer limits and the framing of the packets. Every one has a default that suits most uses.
/// The encoder lives in the Egami.Video.H264 package.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoH264.cs" region="encode"/>
/// </example>
public sealed record H264EncoderOptions
{
    private readonly int? _level;
    private readonly int? _bFrames;
    private readonly int? _slices;
    private readonly int _minQuantizer;
    private readonly int _maxQuantizer = 51;

    /// <summary>Gets the defaults.</summary>
    public static H264EncoderOptions Default { get; } = new();

    /// <summary>Gets the profile. Default <see cref="H264Profile.High"/>, which every current decoder plays.</summary>
    public H264Profile Profile { get; init; } = H264Profile.High;

    /// <summary>
    /// Gets the level as level_idc, ten times the level number (31 for level 3.1), 10 to 62, or <see langword="null"/> (the
    /// default) for the lowest level that the frame size and rate fit, and for <see cref="RateControlMode.Bitrate"/> and
    /// <see cref="RateControlMode.ConstantBitrate"/> the peak bitrate and buffer too. A stream that does not fit the level given
    /// is refused when the encoder is made.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a level of H.264's Table A-1.</exception>
    public int? Level
    {
        get => _level;
        init => _level = value is int v && !IsLevel(v) ? throw new ArgumentOutOfRangeException(nameof(value), v, "The level must be one of H.264's (10, 11, 12, 13, 20, 21, 22, 30, 31, 32, 40, 41, 42, 50, 51, 52, 60, 61 or 62).") : value;
    }

    /// <summary>
    /// Gets the entropy coder, or <see langword="null"/> (the default) for <see cref="H264EntropyCoder.Cabac"/>, which codes about
    /// a tenth smaller; Constrained Baseline allows <see cref="H264EntropyCoder.Cavlc"/> only.
    /// </summary>
    public H264EntropyCoder? Entropy { get; init; }

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
    /// Gets whether each macroblock's quantizer follows how busy it is (adaptive quantization): detail and texture, where errors
    /// show less, are coded coarser and flat areas finer, which looks better at the same size. Default on; turn it off to
    /// measure PSNR, which it lowers.
    /// </summary>
    public bool AdaptiveQuantization { get; init; } = true;

    /// <summary>
    /// Gets the most B-frames between two reference frames, 0 to 16, or <see langword="null"/> (the default) for the effort's:
    /// none at effort 0, else 3. B-frames predict from the frames on both sides and take fewer bits; how many each run has
    /// follows the content. They need <see cref="LatencyMode.Quality"/> (the frames are coded out of order, and packets carry
    /// decode timestamps) and are not in <see cref="H264Profile.ConstrainedBaseline"/>. From effort 4 a run of two or three is
    /// coded as a pyramid: its middle B-frame is a reference for the others.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 16.</exception>
    public int? BFrames
    {
        get => _bFrames;
        init => _bFrames = value is int v ? InRange(v, 0, 16) : null;
    }

    /// <summary>
    /// Gets the slices of each picture, 1 to 32, or <see langword="null"/> (the default) to follow the frame size: one below
    /// about 1280x720, two there, four at 1920x1080 and up to eight above. Slices are bands of macroblock rows coded in parallel
    /// (up to <see cref="EgamiContext.MaxParallelism"/>); each costs a little compression, as nothing is predicted across them.
    /// The stream depends only on this count, never on the number of threads. A picture with fewer macroblock rows has fewer.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 32.</exception>
    public int? Slices
    {
        get => _slices;
        init => _slices = value is int v ? InRange(v, 1, 32) : null;
    }

    /// <summary>
    /// Gets whether packets are Annex B byte streams (start codes, with the parameter sets in every keyframe's packet), as raw
    /// .h264 files, MPEG-TS and RTP senders want. Default off: packets are length-prefixed and the parameter sets go in the
    /// track's avcC record (<see cref="TrackInfo.CodecPrivate"/>), as MP4 and Matroska store them.
    /// </summary>
    public bool AnnexB { get; init; }

    private static bool IsLevel(int level) => level is 10 or 11 or 12 or 13 or 20 or 21 or 22 or 30 or 31 or 32 or 40 or 41 or 42 or 50 or 51 or 52 or 60 or 61 or 62;

    private static int InRange(int value, int min, int max) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(nameof(value), value, $"The value must be {min} to {max}.") : value;
}

/// <summary>The H.264 profiles Egami encodes: which coding tools a stream may use, and so which decoders play it.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoH264.cs" region="encode"/>
/// </example>
public enum H264Profile
{
    /// <summary>Constrained Baseline: CAVLC, no B-frames and no 8x8 transform; for the simplest decoders, such as older phones and WebRTC.</summary>
    ConstrainedBaseline,

    /// <summary>Main: adds CABAC and B-frames.</summary>
    Main,

    /// <summary>High: adds the 8x8 transform; the default, which every current decoder plays.</summary>
    High,
}

/// <summary>The entropy coder of an H.264 stream.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoH264.cs" region="encode"/>
/// </example>
public enum H264EntropyCoder
{
    /// <summary>Context-adaptive binary arithmetic coding: about a tenth smaller (Main and High).</summary>
    Cabac,

    /// <summary>Context-adaptive variable-length coding: simpler to decode.</summary>
    Cavlc,
}
