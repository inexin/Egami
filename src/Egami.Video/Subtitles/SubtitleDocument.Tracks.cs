using Egami.Video.Containers;

namespace Egami.Video.Subtitles;

public sealed partial class SubtitleDocument
{
    /// <summary>
    /// Reads a subtitle track from a demuxer: the packets from where the demuxer is to the end of the file. Packets of other
    /// tracks are read past and disposed, so open a demuxer for this alone, or collect the packets and use
    /// <see cref="FromPackets"/> while remuxing.
    /// </summary>
    /// <param name="demuxer">The demuxer.</param>
    /// <param name="track">The subtitle track, one of <see cref="IDemuxer.Tracks"/>.</param>
    /// <param name="options">The strictness, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The document, in the format the track's codec carries (SRT for mov_text).</returns>
    /// <exception cref="NotSupportedException">The track's codec is not a text subtitle codec: see <see cref="IsTextCodec"/>.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-tracks"/>
    /// </example>
    public static SubtitleDocument ReadTrack(IDemuxer demuxer, TrackInfo track, SubtitleReaderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(demuxer);
        ArgumentNullException.ThrowIfNull(track);
        if (!IsTextCodec(track.CodecId))
        {
            throw new NotSupportedException($"Track {track.Number} has codec '{track.CodecId}', which is not a text subtitle codec Egami.Video reads.");
        }

        List<EncodedPacket> packets = [];
        try
        {
            while (demuxer.ReadPacket() is { } packet)
            {
                if (packet.TrackNumber == track.Number)
                {
                    packets.Add(packet);
                }
                else
                {
                    packet.Dispose();
                }
            }

            return FromPackets(track, packets, options, context);
        }
        finally
        {
            foreach (EncodedPacket packet in packets)
            {
                packet.Dispose();
            }
        }
    }

    /// <summary>Reads a subtitle track from its packets; packets of other tracks are skipped.</summary>
    /// <param name="track">The subtitle track.</param>
    /// <param name="packets">The packets, in file order.</param>
    /// <param name="options">The strictness, or <see langword="null"/> for the defaults.</param>
    /// <param name="context">Where diagnostics go, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The document.</returns>
    /// <exception cref="NotSupportedException">The track's codec is not a text subtitle codec.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Subtitles.cs" region="subtitles-tracks"/>
    /// </example>
    public static SubtitleDocument FromPackets(TrackInfo track, IEnumerable<EncodedPacket> packets, SubtitleReaderOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(packets);
        return SubtitlePackets.Read(track, packets, options, context);
    }

    /// <summary>
    /// Gets whether a codec is one of the text subtitle codecs Egami.Video reads and writes: <see cref="CodecIds.SubRip"/>,
    /// <see cref="CodecIds.WebVtt"/>, the WebM <c>D_WEBVTT</c> kinds, <see cref="CodecIds.Ass"/>, <see cref="CodecIds.Ssa"/>
    /// (read only), <see cref="CodecIds.MovText"/> and <see cref="CodecIds.Mp4WebVtt"/>.
    /// </summary>
    /// <param name="codecId">The codec id.</param>
    /// <returns>Whether <see cref="FromPackets"/> reads it.</returns>
    public static bool IsTextCodec(string codecId)
    {
        ArgumentNullException.ThrowIfNull(codecId);
        return SubtitlePackets.IsText(codecId);
    }
}
