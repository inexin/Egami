using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Codecs;

/// <summary>
/// Turns packets into frames with a send and receive loop. One packet can give no frame yet (a codec that holds frames back)
/// or several, so after each <see cref="Send"/> call <see cref="TryReceive"/> until it returns <see langword="false"/>, and at
/// the end of the stream call <see cref="Flush"/> and receive what is left.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IVideoDecoder decoder = VideoCodecs.CreateDecoder(videoTrack);
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///     {
///         if (packet.TrackNumber != videoTrack.Number)
///             continue;
///         decoder.Send(packet);
///     }
///
///     while (decoder.TryReceive(out YuvVideoFrame? frame))
///         using (frame) Process(frame);
/// }
///
/// decoder.Flush();
/// while (decoder.TryReceive(out YuvVideoFrame? frame))
///     using (frame) Process(frame);
/// </code>
/// </example>
public interface IVideoDecoder : IDisposable
{
    /// <summary>Gives the decoder a packet. The decoder copies what it keeps, so the caller may dispose the packet afterwards.</summary>
    /// <param name="packet">The packet.</param>
    /// <exception cref="CorruptDataException">The packet cannot be decoded (raised here or from a later <see cref="TryReceive"/>).</exception>
    void Send(EncodedPacket packet);

    /// <summary>Tells the decoder no more packets come, so it releases every frame it holds.</summary>
    void Flush();

    /// <summary>Takes the next decoded frame, in presentation order.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame);

    /// <summary>Drops every packet and frame in flight, for example after a seek.</summary>
    void Reset();
}

/// <summary>A decoder that converts its frames to <typeparamref name="TPixel"/> pixels. See <see cref="IVideoDecoder"/>.</summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <example>
/// <code language="csharp">
/// using IVideoDecoder&lt;Rgb8&gt; decoder = VideoCodecs.CreateDecoder&lt;Rgb8&gt;(videoTrack);
/// decoder.Send(packet);
/// while (decoder.TryReceive(out VideoFrame&lt;Rgb8&gt;? frame))
///     using (frame) Process(frame);
/// </code>
/// </example>
public interface IVideoDecoder<TPixel> : IDisposable
    where TPixel : unmanaged, Pixels.IPixelType<TPixel>
{
    /// <summary>Gives the decoder a packet. See <see cref="IVideoDecoder.Send"/>.</summary>
    /// <param name="packet">The packet.</param>
    void Send(EncodedPacket packet);

    /// <summary>Tells the decoder no more packets come. See <see cref="IVideoDecoder.Flush"/>.</summary>
    void Flush();

    /// <summary>Takes the next decoded frame, converted to <typeparamref name="TPixel"/>.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out VideoFrame<TPixel>? frame);

    /// <summary>Drops every packet and frame in flight. See <see cref="IVideoDecoder.Reset"/>.</summary>
    void Reset();
}
