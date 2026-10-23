using Egami.Video.Containers.Mp4;

namespace Egami.Video.Containers;

/// <summary>
/// Writes a file as segments for HLS and DASH (V4.3 Design Q6 and Q7): for each track a CMAF init segment (ftyp and moov) and
/// numbered media segments (styp, sidx, moof and mdat), cut at the first video keyframe after
/// <see cref="Mp4SegmentOptions.SegmentDuration"/> so every segment starts with one, plus an HLS multivariant playlist with a
/// media playlist a track and a static DASH MPD. Each segment, read after its track's init segment, is valid fragmented MP4.
/// A track is named after its kind and number: video1-init.mp4, video1-00001.m4s, video1.m3u8; the playlists are master.m3u8
/// and manifest.mpd. Holds the audio and video codecs <see cref="Mp4Format"/> writes, one rendition: bitrate ladders, live
/// playlists and encryption are not written (Q8, Q9).
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Video.cs" region="video-segments"/>
/// </example>
public sealed class Mp4SegmentWriter : IMuxer
{
    private readonly Func<string, Stream> _open;
    private readonly MediaMetadata _metadata;
    private readonly Mp4SegmentOptions _options;
    private readonly Mp4Fragmenter _fragmenter;
    private readonly Dictionary<FragmentTrack, FragmentTrack[]> _alone = [];
    private readonly BoxWriter _head = new();
    private readonly BoxWriter _moof = new();
    private readonly List<long> _offsets = [];
    private bool _initialized;
    private bool _finished;

    /// <summary>Creates a writer that puts its files in a folder, which is created when needed; files there with the same names are replaced.</summary>
    /// <param name="folder">The folder.</param>
    /// <param name="tracks">The tracks: audio and video only.</param>
    /// <param name="metadata">The tags and cover art for the init segments, or <see langword="null"/> for none.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="NotSupportedException">A track is not audio or video, or MP4 cannot hold its codec.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Video.cs" region="video-segments"/>
    /// </example>
    public Mp4SegmentWriter(string folder, IReadOnlyList<TrackInfo> tracks, MediaMetadata? metadata = null, Mp4SegmentOptions? options = null, MediaContext? context = null)
        : this(InFolder(folder), tracks, metadata, options, context)
    {
    }

    /// <summary>
    /// Creates a writer that asks <paramref name="open"/> for a stream for each file it writes, by name, as for blob storage: it
    /// writes the whole file and disposes the stream, so an upload stream commits when its file is done.
    /// </summary>
    /// <param name="open">Opens a stream to write the named file to.</param>
    /// <param name="tracks">The tracks: audio and video only.</param>
    /// <param name="metadata">The tags and cover art for the init segments, or <see langword="null"/> for none.</param>
    /// <param name="options">The options, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="NotSupportedException">A track is not audio or video, or MP4 cannot hold its codec.</exception>
    /// <example>
    /// <code language="csharp" source="../../../../tests/Egami.DocSamples/Video.cs" region="video-segments"/>
    /// </example>
    public Mp4SegmentWriter(Func<string, Stream> open, IReadOnlyList<TrackInfo> tracks, MediaMetadata? metadata = null, Mp4SegmentOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(tracks);
        _options = options ?? new Mp4SegmentOptions();
        if (_options.SegmentDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(options), "SegmentDuration must be positive.");
        }

        if (tracks.Count == 0 || tracks.Count > 1000)
        {
            throw new NotSupportedException("This writer holds 1 to 1000 tracks.");
        }

        foreach (TrackInfo track in tracks)
        {
            if (track.Kind is not (TrackKind.Audio or TrackKind.Video))
            {
                throw new NotSupportedException($"Segments hold audio and video; track {track.Number} is {track.Kind}.");
            }

            if (Mp4SampleEntries.Unsupported(track) is { } reason)
            {
                throw new NotSupportedException(reason + ".");
            }
        }

        _open = open;
        _metadata = metadata ?? MediaMetadata.Empty;
        _fragmenter = new Mp4Fragmenter(tracks, _options.SegmentDuration, long.MaxValue, (context ?? MediaContext.Default).Warnings, WriteSegments);
        foreach (FragmentTrack track in _fragmenter.Tracks)
        {
            _alone[track] = [track];
        }

        Tracks = [.. _fragmenter.Tracks.Select(t => t.Output)];
    }

    /// <inheritdoc/>
    public IReadOnlyList<TrackInfo> Tracks { get; }

    /// <inheritdoc/>
    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_finished)
        {
            throw new InvalidOperationException("The writer was finished.");
        }

        if (!_fragmenter.TryGet(packet.TrackNumber, out FragmentTrack? track))
        {
            throw new ArgumentException($"Track {packet.TrackNumber} was not given to the writer.", nameof(packet));
        }

        _fragmenter.Add(track, packet);
    }

    /// <summary>Writes the last segments, then the playlists <see cref="Mp4SegmentOptions.Playlists"/> asks for.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        _fragmenter.Finish();
        if (!_initialized)
        {
            WriteInits();
        }

        if ((_options.Playlists & SegmentPlaylists.Hls) != 0)
        {
            SegmentPlaylistWriter.Hls(_open, _fragmenter.Tracks);
        }

        if ((_options.Playlists & SegmentPlaylists.Dash) != 0)
        {
            SegmentPlaylistWriter.Dash(_open, _fragmenter.Tracks);
        }
    }

    /// <summary>Finishes the files if <see cref="Finish"/> was not called.</summary>
    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _fragmenter.Release();
            _head.Dispose();
            _moof.Dispose();
        }
    }

    internal static string NameOf(FragmentTrack track) => $"{(track.Input.Kind == TrackKind.Video ? "video" : "audio")}{track.Id}";

    private static Func<string, Stream> InFolder(string folder)
    {
        ArgumentNullException.ThrowIfNull(folder);
        Directory.CreateDirectory(folder);
        return name => new FileStream(Path.Combine(folder, name), FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);
    }

    private void WriteInits()
    {
        _initialized = true;
        foreach (FragmentTrack track in _fragmenter.Tracks)
        {
            _head.Clear();
            Mp4FragmentBoxes.Ftyp(_head, _alone[track], cmaf: true);
            Mp4FragmentBoxes.Moov(_head, _alone[track], _metadata, mehd: false);
            using Stream stream = _open($"{NameOf(track)}-init.mp4");
            _head.WriteTo(stream);
        }
    }

    private void WriteSegments()
    {
        if (!_initialized)
        {
            WriteInits();
        }

        foreach (FragmentTrack track in _fragmenter.Tracks)
        {
            if (track.Ready == 0)
            {
                continue;
            }

            long earliest = long.MaxValue;
            for (int i = 0; i < track.Ready; i++)
            {
                earliest = Math.Min(earliest, track.Samples[i].Dts + track.Samples[i].Composition);
            }

            bool startsWithKey = track.Samples[0].Key;
            uint number = track.Sequence++;
            _moof.Clear();
            long size = Mp4FragmentBoxes.Moof(_moof, number, _alone[track], -1, _offsets);
            long duration = track.Fragments[^1].Duration;
            if (size > int.MaxValue)
            {
                throw new NotSupportedException("A segment holds more than 2 GB; use a shorter SegmentDuration.");
            }

            _head.Clear();
            Mp4FragmentBoxes.Ftyp(_head, _alone[track], cmaf: true, segment: true);
            _head.StartFull("sidx", 1, 0);
            _head.U32((uint)track.Id);
            _head.U32(track.Timescale);
            _head.U64((ulong)Math.Max(0, earliest));
            _head.U64(0); // first_offset: the moof follows at once
            _head.U16(0);
            _head.U16(1);
            _head.U32((uint)size); // reference_type 0: media
            _head.U32((uint)Math.Min(duration, uint.MaxValue));
            _head.U32(startsWithKey ? 0x90000000u : 0); // starts_with_SAP, SAP type 1
            _head.End();
            using Stream stream = _open($"{NameOf(track)}-{number:D5}.m4s");
            _head.WriteTo(stream);
            _moof.WriteTo(stream);
            track.Data.WriteTo(stream, track.ReadyBytes);
        }
    }
}

/// <summary>Which playlists <see cref="Mp4SegmentWriter"/> writes with its segments (V4.3 Design Q7).</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Video.cs" region="video-segments"/>
/// </example>
[Flags]
public enum SegmentPlaylists
{
    /// <summary>Segments only.</summary>
    None = 0,

    /// <summary>An HLS multivariant playlist (master.m3u8) and a media playlist a track, with EXT-X-MAP for the init segment (RFC 8216).</summary>
    Hls = 1,

    /// <summary>A static DASH MPD (manifest.mpd) with a SegmentTemplate and SegmentTimeline a track (ISO/IEC 23009-1).</summary>
    Dash = 2,
}

/// <summary>How <see cref="Mp4SegmentWriter"/> cuts segments and which playlists it writes.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/Video.cs" region="video-segments"/>
/// </example>
public sealed record Mp4SegmentOptions
{
    /// <summary>Gets how long a segment lasts at least, up to the next video keyframe. Default 6 seconds, as Apple's HLS authoring spec suggests.</summary>
    public TimeSpan SegmentDuration { get; init; } = TimeSpan.FromSeconds(6);

    /// <summary>Gets which playlists are written. Default both HLS and DASH.</summary>
    public SegmentPlaylists Playlists { get; init; } = SegmentPlaylists.Hls | SegmentPlaylists.Dash;
}
