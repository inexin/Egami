using Egami.Video.Containers;

namespace Egami.Video.Cli.Host;

/// <summary>One demuxer whose packets a copy writes: the input track numbers it keeps and the output numbers they get.</summary>
/// <param name="Demuxer">The open demuxer.</param>
/// <param name="Numbers">Each kept input track number and its number in the output.</param>
internal sealed record TrackSource(IDemuxer Demuxer, IReadOnlyDictionary<int, int> Numbers);

/// <summary>
/// Stream copy for <c>extract</c>, <c>embed</c> and <c>metadata</c>: packets of the kept tracks of one or more demuxers, without
/// decoding, interleaved by timestamp across the demuxers, into one output.
/// </summary>
internal static class TrackCopy
{
    /// <summary>
    /// Writes the packets of <paramref name="sources"/> to <paramref name="target"/> with <paramref name="tracks"/> (numbered
    /// as the sources map them) and <paramref name="metadata"/>; a container that cannot hold the tracks fails with its reason.
    /// </summary>
    public static void Write(VideoSession session, IReadOnlyList<TrackSource> sources, MediaTarget target, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata, IProgress<MediaProgress>? progress, MediaTime? duration)
    {
        MediaIO.Write(session, target.Path, output =>
        {
            CountingStream counted = new(output);
            using IMuxer muxer = CreateMuxer(session, counted, target.Container, tracks, metadata);
            JobProgress job = new(progress, duration, tracks, session.Cancellation, counted);
            EncodedPacket?[] heads = new EncodedPacket?[sources.Count];
            try
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    heads[i] = Next(sources[i]);
                }

                while (Earliest(heads) is int pick)
                {
                    using EncodedPacket packet = heads[pick]!;
                    heads[pick] = null;
                    packet.TrackNumber = sources[pick].Numbers[packet.TrackNumber];
                    muxer.WritePacket(packet);
                    job.Packet(packet.TrackNumber, packet.Timestamp);
                    heads[pick] = Next(sources[pick]);
                }
            }
            finally
            {
                foreach (EncodedPacket? head in heads)
                {
                    head?.Dispose();
                }
            }

            muxer.Finish();
            job.Finish();
        });
    }

    /// <summary>Opens a muxer; a container that cannot hold the tracks fails with its reason and a container that can.</summary>
    public static IMuxer CreateMuxer(VideoSession session, Stream stream, ContainerFormat container, IReadOnlyList<TrackInfo> tracks, MediaMetadata metadata)
    {
        try
        {
            return MediaIO.CreateMuxer(session, stream, container, tracks, metadata);
        }
        catch (NotSupportedException error) when (container is not MatroskaFormat)
        {
            throw new NotSupportedException($"{error.Message} Write .mkv, which holds any codec.", error);
        }
    }

    /// <summary>Whether <paramref name="container"/> can hold <paramref name="track"/> on its own: its muxer takes it.</summary>
    public static bool CanHold(ContainerFormat container, TrackInfo track)
    {
        try
        {
            using IMuxer muxer = container.CreateMuxer(new MemoryStream(), [track], MediaMetadata.Empty, MediaContext.Default with { Warnings = null }, leaveOpen: false);
            return true;
        }
        catch (Exception error) when (error is NotSupportedException or ArgumentException or InvalidOperationException)
        {
            return false;
        }
    }

    private static EncodedPacket? Next(TrackSource source)
    {
        while (source.Demuxer.ReadPacket() is { } packet)
        {
            if (source.Numbers.ContainsKey(packet.TrackNumber))
            {
                return packet;
            }

            packet.Dispose();
        }

        return null;
    }

    private static int? Earliest(EncodedPacket?[] heads)
    {
        int? pick = null;
        double best = double.PositiveInfinity;
        for (int i = 0; i < heads.Length; i++)
        {
            if (heads[i] is { } packet && (packet.DecodeTimestamp ?? packet.Timestamp).Seconds is var seconds && (pick is null || seconds < best))
            {
                pick = i;
                best = seconds;
            }
        }

        return pick;
    }
}
