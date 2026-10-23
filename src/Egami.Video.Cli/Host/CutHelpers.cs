namespace Egami.Video.Cli.Host;

/// <summary>
/// An output file of a command that writes several at once (<c>segment</c>): written under a temporary name in the same folder
/// that replaces the target on <see cref="Commit"/>, and deleted when disposed without one, so a failed or cancelled job leaves
/// no partial file. <see cref="MediaIO.Write"/> does the same for one file at a time.
/// </summary>
internal sealed class CutFile : IDisposable
{
    private readonly VideoSession _session;
    private readonly string _temporary;
    private FileStream? _file;
    private bool _committed;

    private CutFile(VideoSession session, string path, string temporary, FileStream file)
    {
        _session = session;
        Path = path;
        _temporary = temporary;
        _file = file;
        Stream = new CountingStream(file);
    }

    /// <summary>Gets the file's final path.</summary>
    public string Path { get; }

    /// <summary>Gets the stream to write to, which counts the bytes written.</summary>
    public CountingStream Stream { get; }

    /// <summary>Starts writing <paramref name="path"/> through a temporary file.</summary>
    public static CutFile Create(VideoSession session, string path)
    {
        session.Paths?.Check(System.IO.Path.GetFullPath(path, session.WorkingDirectory));
        string? folder = System.IO.Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(folder))
        {
            Directory.CreateDirectory(folder);
        }

        string temporary = System.IO.Path.Combine(folder ?? string.Empty, $".{System.IO.Path.GetFileName(path)}.{Environment.ProcessId}.{Guid.NewGuid():N}.egami-video-tmp");
        return new CutFile(session, path, temporary, new FileStream(temporary, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16));
    }

    /// <summary>Closes the temporary file and moves it to the target.</summary>
    public void Commit()
    {
        _file?.Dispose();
        _file = null;
        File.Move(_temporary, Path, overwrite: true);
        _committed = true;
        _session.Written.Enqueue(Path);
        _session.Info($"wrote {Path}");
    }

    /// <summary>Deletes the temporary file unless the output was committed.</summary>
    public void Dispose()
    {
        _file?.Dispose();
        _file = null;
        if (!_committed && File.Exists(_temporary))
        {
            File.Delete(_temporary);
        }
    }
}

/// <summary>Time arithmetic shared by trim, concat, segment and timestamps.</summary>
internal static class CutTimes
{
    /// <summary>A time in nanoseconds.</summary>
    public static long Nanoseconds(MediaTime time) => time.Rescale(Rational.Nanosecond).Ticks;

    /// <summary>A time from nanoseconds.</summary>
    public static MediaTime FromNanoseconds(long nanoseconds) => new(nanoseconds, Rational.Nanosecond);

    /// <summary>When a packet decodes: its decode timestamp, or its timestamp when it has none.</summary>
    public static MediaTime DecodeTime(EncodedPacket packet) => packet.DecodeTimestamp ?? packet.Timestamp;

    /// <summary>Moves a packet's timestamps <paramref name="shift"/> earlier.</summary>
    public static void ShiftBack(EncodedPacket packet, MediaTime shift)
    {
        if (shift.Ticks == 0)
        {
            return;
        }

        packet.Timestamp -= shift;
        if (packet.DecodeTimestamp is { } decode)
        {
            packet.DecodeTimestamp = decode - shift;
        }
    }

    /// <summary>
    /// The chapters that overlap <c>[start, end)</c>, clipped to it and moved <paramref name="shift"/> earlier; an open
    /// <paramref name="end"/> keeps everything after <paramref name="start"/>.
    /// </summary>
    public static IReadOnlyList<ChapterInfo> Clip(IReadOnlyList<ChapterInfo> chapters, MediaTime start, MediaTime? end, MediaTime shift)
    {
        List<ChapterInfo> clipped = [];
        foreach (ChapterInfo chapter in chapters)
        {
            MediaTime? chapterEnd = chapter.End ?? end;
            if ((end is { } stop && chapter.Start >= stop) || (chapterEnd is { } last && last <= start))
            {
                continue;
            }

            MediaTime from = chapter.Start < start ? start : chapter.Start;
            MediaTime? to = chapterEnd is { } e && end is { } limit && e > limit ? limit : chapterEnd;
            clipped.Add(chapter with { Start = from - shift, End = to is { } t ? t - shift : null });
        }

        return clipped;
    }

    /// <summary>The metadata of a cut: the input's tags and attachments with its chapters clipped to the cut.</summary>
    public static MediaMetadata Metadata(MediaInfo info, MediaTime start, MediaTime? end, MediaTime shift)
    {
        return info.Metadata with { Chapters = Clip(info.Chapters, start, end, shift) };
    }

    /// <summary>The track a cut follows: the first video track, or the first track when there is no video.</summary>
    public static TrackInfo MainTrack(IReadOnlyList<TrackInfo> tracks) => tracks.FirstOrDefault(t => t.Kind == TrackKind.Video) ?? tracks[0];

    /// <summary>A packet's length: its duration, or the frame rate's frame time for video, or <see langword="null"/>.</summary>
    public static MediaTime? Length(EncodedPacket packet, TrackInfo track)
    {
        return packet.Duration ?? (track is VideoTrackInfo { FrameRate: { Numerator: > 0 } rate } ? new MediaTime(1, rate.Reciprocal()) : null);
    }
}
