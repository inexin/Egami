using Egami.Video.Containers;

namespace Egami.Video.Subtitles;

/// <summary>
/// Turns a <see cref="SubtitleDocument"/> into a subtitle track for a container: the <see cref="Track"/> to give the muxer and
/// the <see cref="Packets"/> to write, in time order. Matroska takes SRT, WebVTT and ASS tracks; WebM takes WebVTT
/// (<see cref="CodecIds.WebMWebVtt"/>); MP4 takes mov_text and WebVTT (<see cref="CodecIds.MovText"/>,
/// <see cref="CodecIds.Mp4WebVtt"/>). What the codec cannot hold is reported as <c>Subtitle.Dropped</c>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-tracks"/>
/// </example>
public sealed class SubtitleTrackWriter
{
    /// <summary>Creates the track and its packets.</summary>
    /// <param name="document">The subtitles.</param>
    /// <param name="trackNumber">The number the track has among the muxer's tracks.</param>
    /// <param name="codecId">
    /// The codec, or <see langword="null"/> for the one that keeps the document's own format in Matroska:
    /// <see cref="CodecIds.SubRip"/>, <see cref="CodecIds.WebVtt"/> or <see cref="CodecIds.Ass"/>. <see cref="CodecIdFor"/> picks
    /// one for a container.
    /// </param>
    /// <param name="context">Where <c>Subtitle.Dropped</c> diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="NotSupportedException">The codec is not one of the six text codecs Egami.Video writes.</exception>
    public SubtitleTrackWriter(SubtitleDocument document, int trackNumber, string? codecId = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        (Track, List<EncodedPacket> packets) = SubtitlePackets.Write(document, trackNumber, codecId ?? CodecIdFor("mkv", document.Format), context);
        Packets = packets;
    }

    /// <summary>
    /// Gets the track to give the muxer. It is a record: set <see cref="TrackInfo.Language"/>, <see cref="TrackInfo.Name"/> or
    /// <see cref="TrackInfo.IsDefault"/> with <c>with</c>.
    /// </summary>
    public TrackInfo Track { get; }

    /// <summary>
    /// Gets the packets, in time order, each with its duration. MP4 tracks get a packet for every gap too, since MP4 samples
    /// follow each other without holes, and one for each span of time where the same cues show, since they cannot overlap.
    /// </summary>
    public IReadOnlyList<EncodedPacket> Packets { get; }

    /// <summary>Picks the codec for subtitles in a container: what keeps the most of <paramref name="format"/> there.</summary>
    /// <param name="formatNameOrExtension">The container, as a format name or file extension: mkv, mka, webm, mp4, m4v or mov.</param>
    /// <param name="format">The subtitles' format.</param>
    /// <returns>The codec id.</returns>
    /// <exception cref="NotSupportedException">The container holds no text subtitles.</exception>
    public static string CodecIdFor(string formatNameOrExtension, SubtitleFormat format)
    {
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        return formatNameOrExtension.TrimStart('.').ToLowerInvariant() switch
        {
            "mkv" or "mka" or "mks" or "matroska" => format switch
            {
                SubtitleFormat.Srt => CodecIds.SubRip,
                SubtitleFormat.WebVtt => CodecIds.WebVtt,
                _ => CodecIds.Ass,
            },
            "webm" => CodecIds.WebMWebVtt,
            "mp4" or "m4v" or "mov" or "3gp" => format == SubtitleFormat.WebVtt ? CodecIds.Mp4WebVtt : CodecIds.MovText,
            _ => throw new NotSupportedException($"'{formatNameOrExtension}' holds no text subtitle tracks Egami.Video writes; use Matroska, WebM or MP4."),
        };
    }
}
