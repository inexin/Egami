namespace Egami.Video.Containers;

/// <summary>Reads a container's tracks and packets. Packets come in file order, interleaved across tracks.</summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("in.avi");
/// demuxer.Seek(MediaTime.FromSeconds(30));
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///         Console.WriteLine($"{packet.TrackNumber} {packet.Timestamp} {(packet.IsKeyframe ? "key" : "")}");
/// }
/// </code>
/// </example>
public interface IDemuxer : IDisposable
{
    /// <summary>Gets what the headers say: format name, duration, tracks and tags.</summary>
    MediaInfo Info { get; }

    /// <summary>Gets the tracks, in container order.</summary>
    IReadOnlyList<TrackInfo> Tracks { get; }

    /// <summary>Gets whether <see cref="Seek"/> works: the stream must be seekable.</summary>
    bool CanSeek { get; }

    /// <summary>Reads the next packet, or returns <see langword="null"/> at the end of the file.</summary>
    /// <returns>The packet, which the caller must dispose, or <see langword="null"/>.</returns>
    /// <exception cref="CorruptDataException">The data breaks the format's rules beyond what the reader can skip.</exception>
    /// <exception cref="LimitExceededException">A packet is larger than <see cref="MediaLimits.MaxPacketBytes"/>.</exception>
    EncodedPacket? ReadPacket();

    /// <summary>
    /// Moves so that the next packet of the seek track is its last keyframe at or before <paramref name="time"/> (or its first
    /// keyframe when <paramref name="time"/> is earlier). Packets of other tracks follow from the same file position. Uses
    /// the container's index, building one with a single pass over the file when there is none.
    /// </summary>
    /// <param name="time">The target time.</param>
    /// <param name="trackNumber">The track to seek on, or <see langword="null"/> for the first video track (the first track when there is no video).</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    void Seek(MediaTime time, int? trackNumber = null);
}
