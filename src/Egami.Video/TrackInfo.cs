using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;
using Egami.Video.Audio;

namespace Egami.Video;

/// <summary>What a track holds.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public enum TrackKind
{
    /// <summary>Pictures.</summary>
    Video,

    /// <summary>Sound.</summary>
    Audio,

    /// <summary>Subtitles or captions.</summary>
    Subtitle,

    /// <summary>Anything else, such as timecode or data tracks. Copied through untouched.</summary>
    Other,
}

/// <summary>
/// A track in a container: its number, kind and codec, as a demuxer reports it and a muxer takes it. Codecs are named with
/// Matroska codec ids (<see cref="CodecIds"/>), which every container maps to and from, so a track read from AVI can be
/// written to Matroska unchanged.
/// </summary>
/// <example>
/// <code language="csharp">
/// MediaInfo info = MediaFile.Probe("in.mkv");
/// foreach (TrackInfo track in info.Tracks)
///     Console.WriteLine($"{track.Number}: {track.Kind} {track.CodecId} {track.Language}");
/// </code>
/// </example>
public record TrackInfo
{
    /// <summary>Gets the track number packets carry in <see cref="EncodedPacket.TrackNumber"/>.</summary>
    public required int Number { get; init; }

    /// <summary>Gets what the track holds.</summary>
    public required TrackKind Kind { get; init; }

    /// <summary>
    /// Gets the codec as a Matroska codec id, such as <see cref="CodecIds.MotionJpeg"/> or <see cref="CodecIds.PcmLittleEndian"/>.
    /// Codecs with no id of their own use the Matroska compatibility ids <see cref="CodecIds.VfwFourCC"/> and
    /// <see cref="CodecIds.AcmAudio"/>, with the Windows header in <see cref="CodecPrivate"/>.
    /// </summary>
    public required string CodecId { get; init; }

    /// <summary>Gets the codec's setup bytes (Matroska CodecPrivate), or empty when it has none.</summary>
    public ReadOnlyMemory<byte> CodecPrivate { get; init; }

    /// <summary>Gets the length of one tick of the track's packet timestamps.</summary>
    public required Rational TimeBase { get; init; }

    /// <summary>Gets the track's duration, or <see langword="null"/> when the container does not say.</summary>
    public MediaTime? Duration { get; init; }

    /// <summary>Gets the language as an IETF BCP 47 or ISO 639-2 tag, or <see langword="null"/> when not given.</summary>
    public string? Language { get; init; }

    /// <summary>Gets the track's name, or <see langword="null"/> when not given.</summary>
    public string? Name { get; init; }

    /// <summary>
    /// Gets the codec's built-in delay (Matroska CodecDelay, such as Opus pre-skip), or <see langword="null"/> when it has
    /// none. Demuxers have already subtracted it from packet timestamps, so the first packet may start before zero;
    /// muxers that store it add it back.
    /// </summary>
    public MediaTime? CodecDelay { get; init; }

    /// <summary>Gets the track's own text tags (Matroska Tags targeted at the track), keyed by tag name.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets whether players should pick this track by default. True unless the container says otherwise.</summary>
    public bool IsDefault { get; init; } = true;
}

/// <summary>A video track: a <see cref="TrackInfo"/> with the picture size, frame rate and color.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public sealed record VideoTrackInfo : TrackInfo
{
    /// <summary>Gets the coded width in pixels.</summary>
    public required int Width { get; init; }

    /// <summary>Gets the coded height in pixels.</summary>
    public required int Height { get; init; }

    /// <summary>Gets the nominal frame rate, or <see langword="null"/> for variable or unknown rates.</summary>
    public Rational? FrameRate { get; init; }

    /// <summary>Gets the shape of one pixel, width over height (1/1 for square pixels).</summary>
    public Rational PixelAspectRatio { get; init; } = new(1, 1);

    /// <summary>Gets the color description from the container, or <see langword="null"/> when it gives none.</summary>
    public ColorCodePoints? Color { get; init; }

    /// <summary>Gets how the frames are interlaced, or <see cref="FieldOrder.Progressive"/>.</summary>
    public FieldOrder FieldOrder { get; init; }

    /// <summary>Gets the bits per sample of the decoded planes, or 0 when the container does not say.</summary>
    public int BitDepth { get; init; }

    /// <summary>Gets the chroma subsampling of the decoded planes, or <see langword="null"/> when the container does not say.</summary>
    public ChromaFormat? ChromaFormat { get; init; }

    /// <summary>Gets where chroma samples sit relative to luma, or <see langword="null"/> when the container does not say.</summary>
    public ChromaSiting? ChromaSiting { get; init; }

    /// <summary>Gets whether the frames carry an alpha plane (Matroska AlphaMode, used by WebM VP8 and VP9 with alpha).</summary>
    public bool HasAlpha { get; init; }

    /// <summary>
    /// Gets the pixel layout of an uncompressed track (<see cref="CodecIds.Uncompressed"/>) as a FourCC such as "I420", "UYVY"
    /// or "v210" (Matroska ColourSpace, AVI biCompression), or <see langword="null"/> for plain planar YUV described by
    /// <see cref="ChromaFormat"/>, <see cref="BitDepth"/> and <see cref="HasAlpha"/>, as in YUV4MPEG2.
    /// </summary>
    public string? FourCC { get; init; }

    /// <summary>Gets the HDR mastering display and content light levels (Matroska Colour), or <see langword="null"/>.</summary>
    public HdrMetadata? Hdr { get; init; }

    /// <summary>
    /// Gets how the coded frames must be turned or mirrored to show upright, from an MP4 or QuickTime display matrix, as EXIF
    /// orientation says it for photos. Frames come out as coded; <see cref="VideoReaderOptions.AutoOrient"/> applies it.
    /// </summary>
    public ImageOrientation Orientation { get; init; } = ImageOrientation.Normal;
}

/// <summary>An audio track: a <see cref="TrackInfo"/> with the sample format.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public sealed record AudioTrackInfo : TrackInfo
{
    /// <summary>Gets the sample rate in hertz.</summary>
    public required int SampleRate { get; init; }

    /// <summary>Gets the number of channels.</summary>
    public required int Channels { get; init; }

    /// <summary>Gets the bits per sample, or 0 for compressed formats that have none.</summary>
    public int BitsPerSample { get; init; }

    /// <summary>
    /// Gets the speaker of each channel when the container names them (a WAVE_FORMAT_EXTENSIBLE channel mask, an MP4 chnl box,
    /// FLAC's WAVEFORMATEXTENSIBLE_CHANNEL_MASK tag), or <see langword="null"/> when it does not; decoders then assume
    /// <see cref="ChannelLayout.ForChannelCount"/>.
    /// </summary>
    public ChannelLayout? Layout { get; init; }

    /// <summary>
    /// Gets how much audio before a seek target must be decoded for the output to be right from the target on (Matroska
    /// SeekPreRoll: 80 ms for Opus), or <see langword="null"/> for none, as for PCM and FLAC.
    /// </summary>
    public MediaTime? SeekPreRoll { get; init; }

    /// <summary>Gets the layout: <see cref="Layout"/> when known, otherwise the default for the channel count.</summary>
    internal ChannelLayout EffectiveLayout => Layout is { ChannelCount: > 0 } layout && layout.ChannelCount == Channels
        ? layout
        : ChannelLayout.ForChannelCount(Math.Clamp(Channels, 1, ChannelLayout.MaxChannels));
}

/// <summary>How a video track's frames are interlaced.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public enum FieldOrder
{
    /// <summary>Progressive, or not stated.</summary>
    Progressive,

    /// <summary>Interlaced, top field first.</summary>
    TopFieldFirst,

    /// <summary>Interlaced, bottom field first.</summary>
    BottomFieldFirst,
}
