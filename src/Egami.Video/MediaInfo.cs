namespace Egami.Video;

/// <summary>What a media file holds, read from its headers without decoding any frame. Returned by <c>MediaFile.Probe</c>.</summary>
/// <example>
/// <code language="csharp">
/// MediaInfo info = MediaFile.Probe("in.avi");
/// Console.WriteLine($"{info.FormatName}, {info.Duration?.ToTimeSpan()}, {info.Tracks.Count} tracks");
/// </code>
/// </example>
public sealed record MediaInfo
{
    /// <summary>Gets the container format's name, such as "AVI", "Matroska" or "WebM".</summary>
    public required string FormatName { get; init; }

    /// <summary>Gets the duration of the longest track, or <see langword="null"/> when the headers do not say.</summary>
    public MediaTime? Duration { get; init; }

    /// <summary>Gets the tracks, in container order.</summary>
    public required IReadOnlyList<TrackInfo> Tracks { get; init; }

    /// <summary>
    /// Gets the file's text tags, keyed by Matroska tag names (TITLE, ARTIST, COMMENT, DATE, ENCODER, GENRE, COPYRIGHT and so
    /// on). AVI INFO chunks are mapped to those names; INFO chunks without one keep their FourCC (such as "ISBJ").
    /// </summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the chapters, in time order (Matroska Chapters; AVI has none).</summary>
    public IReadOnlyList<ChapterInfo> Chapters { get; init; } = [];

    /// <summary>Gets the attached files, such as fonts and cover art (Matroska Attachments; AVI has none).</summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];

    /// <summary>
    /// Gets the start timecode as SMPTE text, such as "01:00:00:00" (";" before the frames for drop-frame timecode), from a
    /// QuickTime or MP4 timecode track, or <see langword="null"/> when the file has none.
    /// </summary>
    public string? Timecode { get; init; }

    /// <summary>Gets the tags, chapters and attachments, to hand to a muxer so a remux keeps them.</summary>
    public MediaMetadata Metadata => new() { Tags = Tags, Chapters = Chapters, Attachments = Attachments };

    /// <summary>Gets the first video track, or <see langword="null"/> when there is none.</summary>
    public VideoTrackInfo? VideoTrack => Tracks.OfType<VideoTrackInfo>().FirstOrDefault();
}

/// <summary>A file's tags, chapters and attachments, as a demuxer reads them and a muxer writes them.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public sealed record MediaMetadata
{
    /// <summary>Gets nothing: no tags, chapters or attachments.</summary>
    public static MediaMetadata Empty { get; } = new();

    /// <summary>Gets the text tags, keyed by Matroska tag names. See <see cref="MediaInfo.Tags"/>.</summary>
    public IReadOnlyDictionary<string, string> Tags { get; init; } = new Dictionary<string, string>();

    /// <summary>Gets the chapters.</summary>
    public IReadOnlyList<ChapterInfo> Chapters { get; init; } = [];

    /// <summary>Gets the attached files.</summary>
    public IReadOnlyList<AttachmentInfo> Attachments { get; init; } = [];
}

/// <summary>A chapter: where it starts and ends, and its title.</summary>
/// <param name="Start">When the chapter starts.</param>
/// <param name="End">When it ends, or <see langword="null"/> when the file does not say.</param>
/// <param name="Title">The title, or <see langword="null"/>.</param>
/// <param name="Language">The title's language, or <see langword="null"/>.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public sealed record ChapterInfo(MediaTime Start, MediaTime? End, string? Title, string? Language = null);

/// <summary>A file stored inside the container, such as a font for subtitles or cover art ("cover.jpg").</summary>
/// <param name="Name">The file name.</param>
/// <param name="MediaType">The media type, such as "image/jpeg" or "font/ttf".</param>
/// <param name="Data">The file's bytes.</param>
/// <param name="Description">A description, or <see langword="null"/>.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-probe"/>
/// </example>
public sealed record AttachmentInfo(string Name, string MediaType, ReadOnlyMemory<byte> Data, string? Description = null)
{
    /// <summary>Gets whether this is cover art by Matroska's naming convention (cover, small_cover, cover_land, small_cover_land).</summary>
    public bool IsCoverArt => Path.GetFileNameWithoutExtension(Name) is "cover" or "small_cover" or "cover_land" or "small_cover_land";
}

