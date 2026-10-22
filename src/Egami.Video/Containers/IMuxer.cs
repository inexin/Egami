namespace Egami.Video.Containers;

/// <summary>
/// Writes packets into a container. Packets should come in timestamp order across tracks, as a demuxer returns them.
/// <see cref="IDisposable.Dispose"/> finishes the file if <see cref="Finish"/> was not called.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("in.avi");
/// using IMuxer muxer = MediaFile.CreateMuxer("out.mkv", demuxer.Tracks);
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///         muxer.WritePacket(packet);
/// }
///
/// muxer.Finish();
/// </code>
/// </example>
public interface IMuxer : IDisposable
{
    /// <summary>Gets the tracks as the file numbers them, in the order they were given.</summary>
    IReadOnlyList<TrackInfo> Tracks { get; }

    /// <summary>
    /// Writes a packet. Its <see cref="EncodedPacket.TrackNumber"/> is the number of the track as it was given to the muxer;
    /// the bytes are copied, so the caller keeps ownership of the packet.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <exception cref="ArgumentException">The packet's track was not given to the muxer.</exception>
    /// <exception cref="InvalidOperationException">The muxer was finished.</exception>
    void WritePacket(EncodedPacket packet);

    /// <summary>Writes the index and patches sizes and durations. Further packets are refused. Called by <see cref="IDisposable.Dispose"/> when needed.</summary>
    void Finish();
}
