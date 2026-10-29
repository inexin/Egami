using System.Text;

using Egami.Video.Containers;
using Egami.Video.Subtitles;

namespace Egami.Video.Cli.Host;

/// <summary>
/// Subtitle files on the command line (V1.2 Build Plan task 13): SRT, WebVTT, ASS and SSA files named by their extension,
/// read as a one-track input so <c>info</c>, <c>embed --add</c> and the other stream-copy commands take them like any media
/// file, and the names <c>subs</c> writes them under.
/// </summary>
internal static class SubtitleFiles
{
    /// <summary>Whether a path names a subtitle file by its extension.</summary>
    public static bool IsSubtitlePath(string? path) => path is not null && FromName(Path.GetExtension(path)) is not null;

    /// <summary>The format a name or extension stands for (srt, vtt, webvtt, ass, ssa), or <see langword="null"/>.</summary>
    public static SubtitleFormat? FromName(string text) => text.TrimStart('.').ToLowerInvariant() switch
    {
        "srt" or "subrip" => SubtitleFormat.Srt,
        "vtt" or "webvtt" => SubtitleFormat.WebVtt,
        "ass" => SubtitleFormat.Ass,
        "ssa" => SubtitleFormat.Ssa,
        _ => null,
    };

    /// <summary>The extension a format is written with.</summary>
    public static string Extension(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Srt => "srt",
        SubtitleFormat.WebVtt => "vtt",
        SubtitleFormat.Ssa => "ssa",
        _ => "ass",
    };

    /// <summary>The format's name as the tool prints it.</summary>
    public static string Name(SubtitleFormat format) => format switch
    {
        SubtitleFormat.Srt => "SRT",
        SubtitleFormat.WebVtt => "WebVTT",
        SubtitleFormat.Ssa => "SSA",
        _ => "ASS",
    };

    /// <summary>The text format a subtitle codec keeps best when its track is written as a file: mov_text has no styles beyond SRT's.</summary>
    public static SubtitleFormat? FormatOf(string codecId) => codecId switch
    {
        CodecIds.SubRip or CodecIds.MovText => SubtitleFormat.Srt,
        CodecIds.WebVtt or CodecIds.WebMWebVtt or CodecIds.Mp4WebVtt => SubtitleFormat.WebVtt,
        CodecIds.Ass or "S_TEXT/SSA" => SubtitleFormat.Ass,
        _ => null,
    };

    /// <summary>Parses a <c>--format</c> value for a subtitle output.</summary>
    public static SubtitleFormat ParseFormat(string text) => FromName(text) switch
    {
        SubtitleFormat.Ssa => throw new UsageException("SSA is read only; write ass, which every player that reads SSA reads too."),
        { } format => format,
        null => throw new UsageException($"--format '{text}' is not a subtitle format: give srt, vtt or ass."),
    };

    /// <summary>The reader options from <c>--encoding</c> and <c>--strict</c>.</summary>
    public static SubtitleReaderOptions ReaderOptions(string? encoding, bool strict)
    {
        if (encoding is null)
        {
            return new SubtitleReaderOptions { Strict = strict };
        }

        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        try
        {
            return new SubtitleReaderOptions { Encoding = Encoding.GetEncoding(encoding), Strict = strict };
        }
        catch (ArgumentException)
        {
            throw new UsageException($"--encoding '{encoding}' is not an encoding .NET knows; give a name such as windows-1252, iso-8859-2 or shift_jis.");
        }
    }

    /// <summary>Reads a subtitle file, or standard input, as a document.</summary>
    public static SubtitleDocument Load(VideoSession session, InputFile input, SubtitleReaderOptions options)
    {
        using Stream stream = MediaIO.OpenStream(session, input);
        SubtitleReaderOptions withFormat = input.Path is { } path && options.Format is null && FromName(Path.GetExtension(path)) is { } format
            ? options with { Format = format }
            : options;
        return SubtitleDocument.Load(stream, withFormat, session.Context);
    }

    /// <summary>A subtitle file as a demuxer with one Matroska-style text track, numbered 1.</summary>
    public static IDemuxer OpenDemuxer(VideoSession session, InputFile input)
    {
        SubtitleDocument document = Load(session, input, new SubtitleReaderOptions());
        return new PacketDemuxer(new SubtitleTrackWriter(document, 1, context: session.Context), Name(document.Format));
    }

    /// <summary>A track's subtitles from an open demuxer, read from where the demuxer stands to its end.</summary>
    public static SubtitleDocument ReadTrack(VideoSession session, IDemuxer demuxer, TrackInfo track, SubtitleReaderOptions options, string display)
    {
        return SubtitleDocument.IsTextCodec(track.CodecId)
            ? SubtitleDocument.ReadTrack(demuxer, track, options, session.Context)
            : throw new UsageException($"{display}: track {track.Number} is {CodecNames.Name(track.CodecId)}, a picture subtitle; only text subtitles (SRT, WebVTT, ASS, SSA, mov_text) can be read as text.");
    }

    /// <summary>The packets of one subtitle track made in memory, read like a file.</summary>
    internal sealed class PacketDemuxer : IDemuxer
    {
        private readonly Queue<EncodedPacket> _packets;

        public PacketDemuxer(SubtitleTrackWriter writer, string formatName)
        {
            _packets = new Queue<EncodedPacket>(writer.Packets);
            MediaTime? end = writer.Packets.Count == 0 ? null
                : writer.Packets.Max(p => p.Duration is { } duration ? p.Timestamp + duration : p.Timestamp);
            TrackInfo track = end is { } duration ? writer.Track with { Duration = duration } : writer.Track;
            Tracks = [track];
            Info = new MediaInfo { FormatName = formatName, Tracks = Tracks, Duration = end };
        }

        public MediaInfo Info { get; }

        public IReadOnlyList<TrackInfo> Tracks { get; }

        public bool CanSeek => false;

        public EncodedPacket? ReadPacket() => _packets.TryDequeue(out EncodedPacket? packet) ? packet : null;

        public void Seek(MediaTime time, int? trackNumber = null) => throw new NotSupportedException("A subtitle file read as a track cannot seek.");

        public void Dispose()
        {
            while (_packets.TryDequeue(out EncodedPacket? packet))
            {
                packet.Dispose();
            }
        }
    }
}
