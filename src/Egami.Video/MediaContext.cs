using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>
/// The registered containers and codecs, the safety limits for reading, and the Egami context frames are allocated from.
/// Every entry point takes one, or uses <see cref="Default"/>. Immutable: derive a new one with <c>with</c> or
/// <see cref="WithContainer"/> and <see cref="WithCodec"/>.
/// </summary>
/// <example>
/// <code language="csharp">
/// MediaContext context = MediaContext.Default with
/// {
///     Egami = EgamiContext.Default with { MaxParallelism = 2 },
///     Limits = MediaLimits.Default with { MaxTracks = 8 },
/// };
/// MediaInfo info = MediaFile.Probe("in.mkv", context);
/// </code>
/// </example>
public sealed record MediaContext
{
    private static MediaContext? s_default;

    /// <summary>Gets the containers, tried in order when detecting a file's format.</summary>
    public IReadOnlyList<ContainerFormat> Containers { get; init; } = [AviFormat.Instance, MatroskaFormat.Instance, MatroskaFormat.WebM, Y4mFormat.Instance, IvfFormat.Instance, Mp4Format.Instance];

    /// <summary>Gets the video codecs.</summary>
    public IReadOnlyList<VideoCodec> Codecs { get; init; } = [MotionJpegCodec.Instance, Vp8Codec.Instance, Vp9Codec.Instance, Av1Codec.Instance, RawVideoCodec.Instance];

    /// <summary>
    /// Gets the Egami context: the memory source decoded frames come from, the parallelism limit for decoding, and the image
    /// size limits checked on every frame.
    /// </summary>
    public EgamiContext Egami { get; init; } = EgamiContext.Default;

    /// <summary>Gets the limits that reject hostile container structures.</summary>
    public MediaLimits Limits { get; init; } = MediaLimits.Default;

    /// <summary>
    /// Gets where readers and writers report things they handled but the caller may want to know, such as an AVI writer
    /// rounding variable frame rate timestamps to its fixed frame grid, or <see langword="null"/> to ignore them.
    /// </summary>
    public Action<MediaWarning>? Warnings { get; init; }

    /// <summary>Gets the shared default context, with AVI, Matroska, WebM, YUV4MPEG2, IVF and MP4, Motion JPEG, VP8, VP9, AV1 and uncompressed video registered.</summary>
    public static MediaContext Default => s_default ??= new MediaContext();

    /// <summary>Replaces the shared default context.</summary>
    /// <param name="context">The new default.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is <see langword="null"/>.</exception>
    public static void SetDefault(MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        s_default = context;
    }

    /// <summary>Returns a copy with a container added in front of the others, or replacing one of the same name.</summary>
    /// <param name="format">The container.</param>
    /// <returns>The new context.</returns>
    public MediaContext WithContainer(ContainerFormat format)
    {
        ArgumentNullException.ThrowIfNull(format);
        return this with { Containers = [format, .. Containers.Where(f => !string.Equals(f.Name, format.Name, StringComparison.OrdinalIgnoreCase))] };
    }

    /// <summary>Returns a copy with a codec added in front of the others, or replacing one with the same codec id.</summary>
    /// <param name="codec">The codec.</param>
    /// <returns>The new context.</returns>
    public MediaContext WithCodec(VideoCodec codec)
    {
        ArgumentNullException.ThrowIfNull(codec);
        return this with { Codecs = [codec, .. Codecs.Where(c => !string.Equals(c.CodecId, codec.CodecId, StringComparison.Ordinal))] };
    }

    /// <summary>Finds the codec for a codec id, or <see langword="null"/>.</summary>
    /// <param name="track">The track whose codec to find.</param>
    /// <returns>The codec, or <see langword="null"/> when none is registered.</returns>
    public VideoCodec? FindCodec(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return Codecs.FirstOrDefault(c => c.CanDecode(track));
    }

    /// <summary>Finds the container for a name or file extension (with or without the dot), or <see langword="null"/>.</summary>
    /// <param name="nameOrExtension">"AVI", ".mkv", "webm" and so on.</param>
    /// <returns>The container, or <see langword="null"/>.</returns>
    public ContainerFormat? FindContainer(string nameOrExtension)
    {
        ArgumentNullException.ThrowIfNull(nameOrExtension);
        string key = nameOrExtension.TrimStart('.');
        return Containers.FirstOrDefault(f => string.Equals(f.Name, key, StringComparison.OrdinalIgnoreCase)
            || f.Extensions.Any(e => string.Equals(e.TrimStart('.'), key, StringComparison.OrdinalIgnoreCase)));
    }
}

/// <summary>Limits that make readers reject hostile files early instead of allocating or looping without bound.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-limits"/>
/// </example>
public sealed record MediaLimits
{
    /// <summary>Gets the default limits.</summary>
    public static MediaLimits Default { get; } = new();

    /// <summary>Gets the most tracks a file may declare. Default 128.</summary>
    public int MaxTracks { get; init; } = 128;

    /// <summary>Gets the largest packet in bytes. Default 256 MB, far above any real Motion JPEG frame.</summary>
    public int MaxPacketBytes { get; init; } = 256 << 20;

    /// <summary>Gets the largest header structure read whole into memory (codec private data, an EBML element, an AVI header chunk). Default 16 MB.</summary>
    public int MaxHeaderBytes { get; init; } = 16 << 20;

    /// <summary>Gets the most index entries kept in memory. Default 100 million.</summary>
    public long MaxIndexEntries { get; init; } = 100_000_000;

    /// <summary>Gets the deepest nesting of container elements. Default 64.</summary>
    public int MaxNesting { get; init; } = 64;
}

/// <summary>Something a reader or writer handled but reports, through <see cref="MediaContext.Warnings"/>.</summary>
/// <param name="Code">A stable identifier, such as "avi-vfr".</param>
/// <param name="Message">What happened, in a sentence.</param>
/// <param name="TrackNumber">The track it concerns, or <see langword="null"/> for the whole file.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-limits"/>
/// </example>
public sealed record MediaWarning(string Code, string Message, int? TrackNumber = null);
