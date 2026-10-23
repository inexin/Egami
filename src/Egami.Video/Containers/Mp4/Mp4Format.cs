using System.Buffers.Binary;

using Egami.Video.Containers.Mp4;

namespace Egami.Video.Containers;

/// <summary>
/// MP4 and QuickTime (ISO/IEC 14496-12 and 14496-14, and Apple's QuickTime File Format): .mp4, .m4v, .m4a and .mov files, flat or
/// fragmented (CMAF, DASH segments). Reads every track: AV1, VP9, VP8, Motion JPEG, Opus, FLAC, PCM and mov_text map to their
/// codec ids, and any other track (H.264, HEVC, AAC, timecode) is passed through so a remux keeps it. Edit lists are applied, the
/// display matrix becomes <see cref="VideoTrackInfo.Orientation"/>, and tags, chapters and cover art are read.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = Mp4Format.Instance.OpenDemuxer(File.OpenRead("phone.mov"), MediaContext.Default, leaveOpen: false);
/// foreach (TrackInfo track in demuxer.Tracks)
///     Console.WriteLine($"{track.Number}: {track.Kind} {track.CodecId}");
/// </code>
/// </example>
public sealed class Mp4Format : ContainerFormat
{
    private readonly bool _moovAtEnd;
    private readonly Mp4FragmentOptions? _fragments;

    /// <summary>Gets the MP4 format, which applies edit lists.</summary>
    public static Mp4Format Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "MP4";

    /// <inheritdoc/>
    public override IReadOnlyList<string> Extensions { get; } = [".mp4", ".m4v", ".m4a", ".mov", ".3gp", ".m4s", ".cmfv", ".cmfa"];

    /// <inheritdoc/>
    public override int SignatureLength => 12;

    /// <summary>
    /// Gets whether readers ignore edit lists (V4.1 Design Q5). By default an edit list is applied: packet timestamps follow it,
    /// an empty edit delays the track, and packets wholly outside the edit are marked <see cref="EncodedPacket.IsDiscardable"/>.
    /// Ignored, timestamps are the media's own. Register a format with this set through <see cref="MediaContext.WithContainer"/>.
    /// </summary>
    public bool IgnoreEditLists { get; init; }

    /// <summary>
    /// Gets whether writers put moov after the media (V4.1 Design Q4). By default moov comes first, so the file plays while it
    /// downloads: on a stream that can seek and read, the media is written in place and moved forward at the end, else it waits
    /// in a temporary file. With moov at the end the media goes straight to the output, which must be able to seek.
    /// </summary>
    /// <exception cref="ArgumentException">Set together with <see cref="Fragments"/>.</exception>
    public bool MoovAtEnd
    {
        get => _moovAtEnd;
        init
        {
            if (value && _fragments is not null)
            {
                throw new ArgumentException("Fragmented MP4 has no moov at the end; set MoovAtEnd or Fragments, not both.", nameof(MoovAtEnd));
            }

            _moovAtEnd = value;
        }
    }

    /// <summary>
    /// Gets how writers fragment the file (V4.3 Design Q1), or <see langword="null"/> for flat MP4. Fragmented, moov holds only
    /// the setup and the media follows as moof and mdat pairs, written as packets come: nothing waits on disk or is moved, so
    /// it suits cloud blobs and browsers' Media Source Extensions. A seekable output gets its durations written back at the end.
    /// </summary>
    /// <exception cref="ArgumentException">Set together with <see cref="MoovAtEnd"/>.</exception>
    public Mp4FragmentOptions? Fragments
    {
        get => _fragments;
        init
        {
            if (value is not null && _moovAtEnd)
            {
                throw new ArgumentException("Fragmented MP4 has no moov at the end; set MoovAtEnd or Fragments, not both.", nameof(Fragments));
            }

            _fragments = value;
        }
    }

    /// <summary>
    /// Gets what writers do when <see cref="Fragments"/> is not set and the output cannot seek and read back, as with an upload
    /// stream (V4.3 Design Q2). By default the file is fragmented with the default <see cref="Mp4FragmentOptions"/> and an
    /// mp4-fragmented warning; <see cref="Mp4NonSeekableLayout.TempFile"/> writes flat MP4 with the media held in a temporary file.
    /// </summary>
    public Mp4NonSeekableLayout NonSeekable { get; init; }

    /// <inheritdoc/>
    public override bool CanMux => true;

    /// <inheritdoc/>
    public override bool IsMatch(ReadOnlySpan<byte> header)
    {
        if (header.Length < 8)
        {
            return false;
        }

        // A file starts with ftyp, or in old QuickTime files with moov, mdat, free, skip, wide or pnot.
        uint size = BinaryPrimitives.ReadUInt32BigEndian(header);
        ReadOnlySpan<byte> type = header[4..8];
        return (type.SequenceEqual("ftyp"u8) && size >= 8)
            || ((type.SequenceEqual("moov"u8) || type.SequenceEqual("mdat"u8) || type.SequenceEqual("free"u8) || type.SequenceEqual("skip"u8)
                || type.SequenceEqual("wide"u8) || type.SequenceEqual("pnot"u8)) && (size is 0 or 1 or >= 8));
    }

    /// <inheritdoc/>
    public override IDemuxer OpenDemuxer(Stream stream, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(context);
        return new Mp4Demuxer(stream, context, leaveOpen, IgnoreEditLists);
    }

    /// <summary>
    /// Creates a muxer that writes ISO MP4: tracks numbered from 1 in the order given, chunks of about half a second interleaved
    /// by time, edit lists for start offsets and codec delays, the display matrix from <see cref="VideoTrackInfo.Orientation"/>,
    /// tags as iTunes items, cover art, and chapters both as Nero chpl and as a QuickTime chapter track. Holds AV1, VP9, VP8,
    /// Motion JPEG, H.264, HEVC, MPEG-4 Part 2, Opus, FLAC, AAC, MP3, PCM, mov_text, and tracks read from MP4 or QuickTime with
    /// any other codec. A file read as QuickTime is written as MP4 (Q11). With <see cref="Fragments"/> set, or on an output that
    /// cannot seek and read back (see <see cref="NonSeekable"/>), the file is fragmented MP4 instead, with Nero chapters only.
    /// </summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="tracks">The tracks.</param>
    /// <param name="metadata">The tags, chapters and cover art.</param>
    /// <param name="context">The context.</param>
    /// <param name="leaveOpen">Whether disposing the muxer leaves the stream open.</param>
    /// <returns>The muxer.</returns>
    /// <exception cref="NotSupportedException">MP4 cannot hold one of the tracks, or <see cref="MoovAtEnd"/> is set and the stream cannot seek.</exception>
    public override IMuxer CreateMuxer(Stream stream, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, MediaContext context, bool leaveOpen)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(tracks);
        ArgumentNullException.ThrowIfNull(metadata);
        ArgumentNullException.ThrowIfNull(context);
        if (Fragments is { } fragments)
        {
            return new Mp4FragmentMuxer(stream, tracks, metadata, context, leaveOpen, fragments);
        }

        if (!MoovAtEnd && !(stream.CanSeek && stream.CanRead) && NonSeekable == Mp4NonSeekableLayout.Fragment)
        {
            context.Warnings?.Invoke(new MediaWarning("mp4-fragmented", "The output cannot seek and read back, so the MP4 file is written fragmented, which needs no temporary file; set Mp4Format.NonSeekable to TempFile for flat MP4."));
            return new Mp4FragmentMuxer(stream, tracks, metadata, context, leaveOpen, new Mp4FragmentOptions());
        }

        return new Mp4Muxer(stream, tracks, metadata, context, leaveOpen, MoovAtEnd);
    }
}
