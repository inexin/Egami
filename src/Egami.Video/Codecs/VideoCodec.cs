using Egami.Pixels;

namespace Egami.Video.Codecs;

/// <summary>
/// A video codec: which tracks it decodes and how to create its decoder and encoder. Register a new one with
/// <see cref="MediaContext.WithCodec(VideoCodec)"/>.
/// </summary>
/// <example>
/// <code language="csharp">
/// VideoCodec? codec = MediaContext.Default.FindCodec(videoTrack);
/// using IVideoDecoder decoder = codec!.CreateDecoder(videoTrack, null, MediaContext.Default);
/// </code>
/// </example>
public abstract class VideoCodec
{
    /// <summary>Gets the codec's name, such as "Motion JPEG".</summary>
    public abstract string Name { get; }

    /// <summary>Gets the Matroska codec id that encoders write (<see cref="CodecIds"/>).</summary>
    public abstract string CodecId { get; }

    /// <summary>Gets whether <see cref="CreateEncoder"/> is supported.</summary>
    public virtual bool CanEncode => false;

    /// <summary>Returns whether this codec decodes the track. By default, whether the track's codec id is <see cref="CodecId"/>.</summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether the codec decodes it.</returns>
    public virtual bool CanDecode(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return string.Equals(track.CodecId, CodecId, StringComparison.Ordinal);
    }

    /// <summary>Creates a decoder for a track.</summary>
    /// <param name="track">The track, as the demuxer describes it.</param>
    /// <param name="options">How to decode, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context frames are allocated from.</param>
    /// <returns>The decoder.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-codecs"/>
    /// </example>
    public abstract IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context);

    /// <summary>Creates an encoder.</summary>
    /// <param name="options">The picture size, time base and quality.</param>
    /// <param name="context">The context.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="NotSupportedException">The codec cannot encode.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-encode"/>
    /// </example>
    public virtual IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context) =>
        throw new NotSupportedException($"{Name} cannot encode.");

    /// <inheritdoc/>
    public override string ToString() => Name;
}

/// <summary>How a decoder decodes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-options"/>
/// </example>
public sealed record VideoDecoderOptions
{
    /// <summary>Gets the default options.</summary>
    public static VideoDecoderOptions Default { get; } = new();

    /// <summary>
    /// Gets the scale denominator: 1 for full size, or 2, 4 or 8 to decode at 1/2, 1/4 or 1/8 size where the codec supports it
    /// (Motion JPEG does, through Egami's scaled JPEG decode). Default 1.
    /// </summary>
    public int ScaleDenominator { get; init; } = 1;

    /// <summary>
    /// Gets what to do with a frame that cannot be decoded: throw (the default), skip it and report it, or conceal it where the
    /// codec can. See <see cref="DecodeErrorMode"/>. <see cref="VideoReader"/> sets this from <see cref="VideoReaderOptions.Errors"/>.
    /// </summary>
    public DecodeErrorMode Errors { get; init; }

    /// <summary>
    /// Gets whether a track's alpha plane is decoded when it has one (WebM VP8 with alpha decodes a second stream for it). Turn it
    /// off to save that work when only the color is needed. Default on.
    /// </summary>
    public bool DecodeAlpha { get; init; } = true;

    /// <summary>
    /// Gets whether film grain a stream asks for is added to its frames (AV1). Turn it off to get the frames as coded, for
    /// measuring or re-encoding them. Default on, as players show them.
    /// </summary>
    public bool ApplyFilmGrain { get; init; } = true;
}

/// <summary>What an encoder makes.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-encode"/>
/// </example>
public sealed record VideoEncoderOptions
{
    /// <summary>Gets the picture width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Gets the picture height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Gets the frame rate, which also sets the time base of the packets (its reciprocal). Default 30/1.</summary>
    public Rational FrameRate { get; init; } = new(30, 1);

    /// <summary>
    /// Gets the quality, or <see langword="null"/> for the codec's default: 1 to 100 for Motion JPEG (default 75, as Egami's JPEG
    /// writer), 0 to 100 for VP8, VP9 and AV1 (default 60, the scale of Egami's AVIF writer, where equal numbers look alike). With
    /// <see cref="RateControlMode.Quality"/> it is what every frame aims at.
    /// </summary>
    public int? Quality { get; init; }

    /// <summary>
    /// Gets how hard the encoder works, 0 (fastest) to 10 (smallest files), or <see langword="null"/> for the codec's default
    /// (4 for VP8, VP9 and AV1). Motion JPEG has no effort.
    /// </summary>
    public int? Effort { get; init; }

    /// <summary>Gets how the encoder spends bits (VP8, VP9, AV1). Default <see cref="RateControlMode.Quality"/>: constant quality.</summary>
    public RateControlMode RateControl { get; init; }

    /// <summary>
    /// Gets the target bitrate in bits per second for <see cref="RateControlMode.Bitrate"/> and
    /// <see cref="RateControlMode.ConstantBitrate"/>, which need it.
    /// </summary>
    public long? Bitrate { get; init; }

    /// <summary>
    /// Gets the highest bitrate in bits per second that <see cref="RateControlMode.Bitrate"/> may reach over one second, or
    /// <see langword="null"/> for twice <see cref="Bitrate"/>.
    /// </summary>
    public long? MaxBitrate { get; init; }

    /// <summary>
    /// Gets the decoder buffer in bits that <see cref="RateControlMode.ConstantBitrate"/> keeps from running dry, or
    /// <see langword="null"/> for one second of <see cref="Bitrate"/>.
    /// </summary>
    public long? BufferSize { get; init; }

    /// <summary>
    /// Gets the codec's own quantizer for <see cref="RateControlMode.Quantizer"/> (0 to 127 for VP8, 0 to 255 for VP9 and AV1), or <see langword="null"/>
    /// to take the one <see cref="Quality"/> maps to.
    /// </summary>
    public int? Quantizer { get; init; }

    /// <summary>Gets whether the encoder may hold frames back to code them better (VP8, VP9, AV1). Default <see cref="LatencyMode.Quality"/>.</summary>
    public LatencyMode LatencyMode { get; init; }

    /// <summary>
    /// Gets the most frames from one keyframe to the next, or <see langword="null"/> for 10 seconds' worth. Keyframes also come
    /// at scene cuts unless <see cref="SceneCutKeyframes"/> is off, and where <see cref="EncodeFrameOptions.ForceKeyframe"/> asks.
    /// </summary>
    public int? KeyframeInterval { get; init; }

    /// <summary>
    /// Gets whether a scene cut starts a keyframe. Default on; off, with <see cref="KeyframeInterval"/>, gives keyframes at a
    /// fixed spacing.
    /// </summary>
    public bool SceneCutKeyframes { get; init; } = true;

    /// <summary>
    /// Gets the first pass's statistics for a two-pass <see cref="RateControlMode.Bitrate"/> encode, from
    /// <see cref="VideoWriter.AnalyzeFirstPass(IEnumerable{VideoFrame}, IProgress{MediaProgress}?, CancellationToken)"/>,
    /// or <see langword="null"/> for one pass.
    /// </summary>
    public FirstPassStats? FirstPassStats { get; init; }

    /// <summary>Gets the VP8 expert settings, or <see langword="null"/> for <see cref="Vp8EncoderOptions.Default"/>.</summary>
    public Vp8EncoderOptions? Vp8 { get; init; }

    /// <summary>Gets the VP9 expert settings, or <see langword="null"/> for <see cref="Vp9EncoderOptions.Default"/>.</summary>
    public Vp9EncoderOptions? Vp9 { get; init; }

    /// <summary>Gets the AV1 expert settings, or <see langword="null"/> for <see cref="Av1EncoderOptions.Default"/>.</summary>
    public Av1EncoderOptions? Av1 { get; init; }

    /// <summary>Gets the FFV1 expert settings, or <see langword="null"/> for <see cref="Ffv1EncoderOptions.Default"/>.</summary>
    public Ffv1EncoderOptions? Ffv1 { get; init; }

    /// <summary>
    /// Gets the largest packet size in bytes to aim for, or <see langword="null"/> for none. Motion JPEG then picks, per frame,
    /// the highest quality up to <see cref="Quality"/> (100 when unset) whose frame fits, by a binary search of about seven
    /// encodes; a frame that does not fit even at quality 1 is written at quality 1.
    /// </summary>
    public int? TargetFrameBytes { get; init; }

    /// <summary>
    /// Gets the chroma subsampling for frames given as RGB rasters, or <see langword="null"/> for the codec's default (4:2:0 for
    /// Motion JPEG). Frames given as planes keep their own.
    /// </summary>
    public ChromaFormat? ChromaFormat { get; init; }
}
