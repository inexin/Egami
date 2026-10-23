namespace Egami.Video.Codecs;

/// <summary>How an encoder spends bits: on a quality, on a bitrate or on a fixed quantizer.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-bitrate"/>
/// </example>
public enum RateControlMode
{
    /// <summary>
    /// Constant quality, the default: every frame aims at <see cref="VideoEncoderOptions.Quality"/>, so files are as large as the
    /// content needs. Best for files.
    /// </summary>
    Quality,

    /// <summary>
    /// A target average bitrate (<see cref="VideoEncoderOptions.Bitrate"/>) that bits are spread over by frame complexity: one
    /// pass, or two with <see cref="VideoEncoderOptions.FirstPassStats"/>.
    /// </summary>
    Bitrate,

    /// <summary>
    /// A constant bitrate that a decoder buffer of <see cref="VideoEncoderOptions.BufferSize"/> never runs dry on, as live streams
    /// and WebRTC want.
    /// </summary>
    ConstantBitrate,

    /// <summary>One quantizer for every frame (<see cref="VideoEncoderOptions.Quantizer"/>, or the one <see cref="VideoEncoderOptions.Quality"/> maps to).</summary>
    Quantizer,
}

/// <summary>Whether an encoder may hold frames back to code them better, as WebCodecs' latencyMode.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-realtime"/>
/// </example>
public enum LatencyMode
{
    /// <summary>
    /// The default: the encoder looks 25 frames ahead to place keyframes at scene cuts and to code hidden alternate reference
    /// frames, so a packet comes out up to 25 frames after its frame went in.
    /// </summary>
    Quality,

    /// <summary>No lookahead and no hidden frames: every frame's packet comes out as soon as it is sent, as live streams need.</summary>
    Realtime,
}

/// <summary>What one <see cref="IVideoEncoder.Send(VideoFrame, EncodeFrameOptions)"/> call asks for beyond the frame.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-realtime"/>
/// </example>
public sealed record EncodeFrameOptions
{
    /// <summary>Gets the defaults: nothing beyond the frame.</summary>
    public static EncodeFrameOptions Default { get; } = new();

    /// <summary>Gets whether the frame must be a keyframe, as WebCodecs' keyFrame flag: a point where decoding can start.</summary>
    public bool ForceKeyframe { get; init; }
}

/// <summary>
/// The VP8 settings beyond <see cref="VideoEncoderOptions"/>, for experts: quantizer limits, alternate reference frames,
/// golden frame spacing, token partitions and error resilience. Every one has a default that suits most uses.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-vp8"/>
/// </example>
public sealed record Vp8EncoderOptions
{
    private readonly int _minQuantizer = 4;
    private readonly int _maxQuantizer = 127;
    private readonly int? _goldenInterval;
    private readonly int _temporalFilterFrames = 7;
    private readonly int _temporalFilterStrength = 5;
    private readonly int? _partitions;

    /// <summary>Gets the defaults.</summary>
    public static Vp8EncoderOptions Default { get; } = new();

    /// <summary>Gets the smallest quantizer index rate control may use, 0 (finest) to 127. Default 4.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 127.</exception>
    public int MinQuantizer
    {
        get => _minQuantizer;
        init => _minQuantizer = InRange(value, 0, 127);
    }

    /// <summary>Gets the largest quantizer index rate control may use, 0 to 127. Default 127.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 127.</exception>
    public int MaxQuantizer
    {
        get => _maxQuantizer;
        init => _maxQuantizer = InRange(value, 0, 127);
    }

    /// <summary>
    /// Gets whether <see cref="LatencyMode.Quality"/> codes hidden alternate reference frames: a temporally filtered copy of a
    /// frame ahead that the frames before it predict from. Default on; <see cref="LatencyMode.Realtime"/> never codes them.
    /// </summary>
    public bool AlternateReference { get; init; } = true;

    /// <summary>
    /// Gets the most frames between golden frame refreshes (and alternate reference frames), 4 to 64, or <see langword="null"/>
    /// (the default) for 8 to 16 chosen by how much the picture moves.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 4 to 64.</exception>
    public int? GoldenInterval
    {
        get => _goldenInterval;
        init => _goldenInterval = value is int v ? InRange(v, 4, 64) : null;
    }

    /// <summary>Gets how many frames the temporal filter blends into an alternate reference frame, 1 (none) to 15. Default 7.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 15.</exception>
    public int TemporalFilterFrames
    {
        get => _temporalFilterFrames;
        init => _temporalFilterFrames = InRange(value, 1, 15);
    }

    /// <summary>Gets how strongly the temporal filter blends, 0 (weakest) to 6. Default 5.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 6.</exception>
    public int TemporalFilterStrength
    {
        get => _temporalFilterStrength;
        init => _temporalFilterStrength = InRange(value, 0, 6);
    }

    /// <summary>
    /// Gets the token partitions per frame, 1, 2, 4 or 8, or <see langword="null"/> (the default) for 1, and in
    /// <see cref="LatencyMode.Realtime"/> 2 from 720 lines and 4 from 1080: more partitions let decoders use more threads.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 1, 2, 4 or 8.</exception>
    public int? Partitions
    {
        get => _partitions;
        init => _partitions = value is null or 1 or 2 or 4 or 8 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The partition count must be 1, 2, 4 or 8.");
    }

    /// <summary>
    /// Gets whether frames keep no probability updates for the frames after them, so a lost packet spoils only its own frame,
    /// or <see langword="null"/> (the default) for on in <see cref="LatencyMode.Realtime"/> and off otherwise.
    /// </summary>
    public bool? ErrorResilient { get; init; }

    /// <summary>
    /// Gets whether frames with alpha are coded with their alpha plane as a second VP8 stream, as WebM carries it, or
    /// <see langword="null"/> (the default) to code alpha when the first frame has it.
    /// </summary>
    public bool? Alpha { get; init; }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}

/// <summary>
/// The VP9 settings beyond <see cref="VideoEncoderOptions"/>, for experts: quantizer limits, alternate reference frames,
/// golden frame spacing, tile columns, adaptive quantization, error resilience, the bit depth and alpha. Every one has a
/// default that suits most uses.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-vp9"/>
/// </example>
public sealed record Vp9EncoderOptions
{
    private readonly int _minQuantizer;
    private readonly int _maxQuantizer = 255;
    private readonly int? _goldenInterval;
    private readonly int _temporalFilterFrames = 7;
    private readonly int _temporalFilterStrength = 5;
    private readonly int? _tileColumns;
    private readonly int? _bitDepth;

    /// <summary>Gets the defaults.</summary>
    public static Vp9EncoderOptions Default { get; } = new();

    /// <summary>Gets the smallest quantizer index rate control may use, 0 (finest, lossless) to 255. Default 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 255.</exception>
    public int MinQuantizer
    {
        get => _minQuantizer;
        init => _minQuantizer = InRange(value, 0, 255);
    }

    /// <summary>Gets the largest quantizer index rate control may use, 0 to 255. Default 255.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 255.</exception>
    public int MaxQuantizer
    {
        get => _maxQuantizer;
        init => _maxQuantizer = InRange(value, 0, 255);
    }

    /// <summary>
    /// Gets whether <see cref="LatencyMode.Quality"/> codes hidden alternate reference frames: a temporally filtered copy of a
    /// frame ahead that the frames before it predict from, sent in a superframe with the next shown frame. Default on;
    /// <see cref="LatencyMode.Realtime"/> never codes them.
    /// </summary>
    public bool AlternateReference { get; init; } = true;

    /// <summary>
    /// Gets the most frames between golden frame refreshes (and alternate reference frames), 4 to 64, or <see langword="null"/>
    /// (the default) for 8 to 16 chosen by how much the picture moves.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 4 to 64.</exception>
    public int? GoldenInterval
    {
        get => _goldenInterval;
        init => _goldenInterval = value is int v ? InRange(v, 4, 64) : null;
    }

    /// <summary>Gets how many frames the temporal filter blends into an alternate reference frame, 1 (none) to 15. Default 7.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 15.</exception>
    public int TemporalFilterFrames
    {
        get => _temporalFilterFrames;
        init => _temporalFilterFrames = InRange(value, 1, 15);
    }

    /// <summary>Gets how strongly the temporal filter blends, 0 (weakest) to 6. Default 5.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 6.</exception>
    public int TemporalFilterStrength
    {
        get => _temporalFilterStrength;
        init => _temporalFilterStrength = InRange(value, 0, 6);
    }

    /// <summary>
    /// Gets the tile columns per frame, a power of two from 1 to 64, or <see langword="null"/> (the default) for as many as the
    /// width allows with each at least 256 pixels wide, up to 4. Tiles let encoders and decoders use more threads; the frame
    /// width caps them. The output never depends on the thread count.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not a power of two from 1 to 64.</exception>
    public int? TileColumns
    {
        get => _tileColumns;
        init => _tileColumns = value is null or 1 or 2 or 4 or 8 or 16 or 32 or 64 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The tile columns must be a power of two from 1 to 64.");
    }

    /// <summary>
    /// Gets whether blocks get finer or coarser quantizers by their detail, through segments (libvpx's aq-mode 1): flat areas,
    /// where banding shows, get finer steps. Default off.
    /// </summary>
    public bool AdaptiveQuantization { get; init; }

    /// <summary>
    /// Gets whether frames keep no probability updates for the frames after them, so a lost packet spoils only its own frame,
    /// or <see langword="null"/> (the default) for on in <see cref="LatencyMode.Realtime"/> and off otherwise.
    /// </summary>
    public bool? ErrorResilient { get; init; }

    /// <summary>
    /// Gets the bit depth to code, 8, 10 or 12, or <see langword="null"/> (the default) for the frames' own: 10 or 12 for 16-bit
    /// planes of that depth, 8 for everything else. 10 and 12 bits make profile 2 (4:2:0) or 3 streams.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 8, 10 or 12.</exception>
    public int? BitDepth
    {
        get => _bitDepth;
        init => _bitDepth = value is null or 8 or 10 or 12 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bit depth must be 8, 10 or 12.");
    }

    /// <summary>
    /// Gets whether frames with alpha are coded with their alpha plane as a second VP9 stream, as WebM carries it, or
    /// <see langword="null"/> (the default) to code alpha when the first frame has it.
    /// </summary>
    public bool? Alpha { get; init; }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}

/// <summary>
/// The AV1 settings beyond <see cref="VideoEncoderOptions"/>, for experts: quantizer limits, hidden alternate reference frames
/// and their pyramid, golden frame spacing, the temporal filter, adaptive quantization, screen content tools, error resilience
/// and the bit depth. Every one has a default that suits most uses.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-av1"/>
/// </example>
public sealed record Av1EncoderOptions
{
    private readonly int _minQuantizer;
    private readonly int _maxQuantizer = 255;
    private readonly int? _goldenInterval;
    private readonly int _temporalFilterFrames = 7;
    private readonly int _temporalFilterStrength = 5;
    private readonly int? _bitDepth;

    /// <summary>Gets the defaults.</summary>
    public static Av1EncoderOptions Default { get; } = new();

    /// <summary>Gets the smallest base_q_idx rate control may use, 0 (finest, lossless) to 255. Default 0.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 255.</exception>
    public int MinQuantizer
    {
        get => _minQuantizer;
        init => _minQuantizer = InRange(value, 0, 255);
    }

    /// <summary>Gets the largest base_q_idx rate control may use, 0 to 255. Default 255.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 255.</exception>
    public int MaxQuantizer
    {
        get => _maxQuantizer;
        init => _maxQuantizer = InRange(value, 0, 255);
    }

    /// <summary>
    /// Gets whether <see cref="LatencyMode.Quality"/> codes hidden alternate reference frames: a temporally filtered copy of a
    /// frame ahead that the frames before it predict from, shown later with show_existing_frame at its own place. Default on;
    /// <see cref="LatencyMode.Realtime"/> never codes them.
    /// </summary>
    public bool AlternateReference { get; init; } = true;

    /// <summary>
    /// Gets whether a group with an alternate reference also codes a hidden frame halfway to it (ALTREF2), which the frames on
    /// either side predict from, and shows it later the same way: a small pyramid, as libaom builds. Default on.
    /// </summary>
    public bool Pyramid { get; init; } = true;

    /// <summary>
    /// Gets the most frames between golden frame refreshes (and alternate reference frames), 4 to 64, or <see langword="null"/>
    /// (the default) for 8 to 16 chosen by how much the picture moves.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 4 to 64.</exception>
    public int? GoldenInterval
    {
        get => _goldenInterval;
        init => _goldenInterval = value is int v ? InRange(v, 4, 64) : null;
    }

    /// <summary>Gets how many frames the temporal filter blends into an alternate reference frame, 1 (none) to 15. Default 7.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 15.</exception>
    public int TemporalFilterFrames
    {
        get => _temporalFilterFrames;
        init => _temporalFilterFrames = InRange(value, 1, 15);
    }

    /// <summary>Gets how strongly the temporal filter blends, 0 (weakest) to 6. Default 5.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 6.</exception>
    public int TemporalFilterStrength
    {
        get => _temporalFilterStrength;
        init => _temporalFilterStrength = InRange(value, 0, 6);
    }

    /// <summary>
    /// Gets whether each 64x64 superblock gets its own quantizer by its detail (delta q, Egami's Variance Boost): flat areas,
    /// where banding shows, get finer steps. Default on.
    /// </summary>
    public bool AdaptiveQuantization { get; init; } = true;

    /// <summary>
    /// Gets whether the screen content tools (palettes, and intra block copy in keyframes) are on, or <see langword="null"/>
    /// (the default) to turn them on per frame when the frame looks like screen content.
    /// </summary>
    public bool? ScreenContent { get; init; }

    /// <summary>
    /// Gets whether frames start from the default probabilities and keep no motion vectors for the frames after them, so a lost
    /// packet spoils only its own frame, or <see langword="null"/> (the default) for on in <see cref="LatencyMode.Realtime"/> and
    /// off otherwise.
    /// </summary>
    public bool? ErrorResilient { get; init; }

    /// <summary>
    /// Gets the bit depth to code, 8, 10 or 12, or <see langword="null"/> (the default) for the frames' own: 10 or 12 for 16-bit
    /// planes of that depth, 8 for everything else. 8 and 10-bit 4:2:0 make Main profile streams, 4:4:4 High, and 12 bits or
    /// 4:2:2 Professional.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not 8, 10 or 12.</exception>
    public int? BitDepth
    {
        get => _bitDepth;
        init => _bitDepth = value is null or 8 or 10 or 12 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The bit depth must be 8, 10 or 12.");
    }

    private static int InRange(int value, int min, int max, [System.Runtime.CompilerServices.CallerArgumentExpression(nameof(value))] string? name = null) =>
        value < min || value > max ? throw new ArgumentOutOfRangeException(name, value, $"The value must be {min} to {max}.") : value;
}
