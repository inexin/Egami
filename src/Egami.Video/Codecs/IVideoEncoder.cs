using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Codecs;

/// <summary>
/// Turns frames into packets with a send and receive loop, the mirror of <see cref="IVideoDecoder"/>. After each
/// <see cref="Send(VideoFrame)"/> call <see cref="TryReceive"/> until it returns <see langword="false"/>; at the end call
/// <see cref="Flush"/> and receive what is left.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = 640, Height = 480 }, MediaContext.Default);
/// using IMuxer muxer = MediaFile.CreateMuxer("out.avi", [encoder.Track]);
/// encoder.Send(frame);
/// while (encoder.TryReceive(out EncodedPacket? packet))
///     using (packet) muxer.WritePacket(packet);
/// </code>
/// </example>
public interface IVideoEncoder : IDisposable
{
    /// <summary>Gets the track the packets belong to, to hand to a muxer.</summary>
    VideoTrackInfo Track { get; }

    /// <summary>
    /// Encodes a frame: a <see cref="YuvVideoFrame"/> or a <see cref="VideoFrame{TPixel}"/> of the encoder's size. The frame's
    /// timestamp is rescaled to the track's time base. The caller keeps ownership of the frame.
    /// </summary>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentException">The frame's size differs from the encoder's.</exception>
    void Send(VideoFrame frame);

    /// <summary>
    /// Encodes a frame with per-frame settings, such as <see cref="EncodeFrameOptions.ForceKeyframe"/>. Encoders whose every
    /// packet is a keyframe (Motion JPEG, uncompressed video) treat it as <see cref="Send(VideoFrame)"/>.
    /// </summary>
    /// <param name="frame">The frame, as for <see cref="Send(VideoFrame)"/>.</param>
    /// <param name="options">The frame's settings.</param>
    /// <exception cref="ArgumentException">The frame's size differs from the encoder's.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-realtime"/>
    /// </example>
    void Send(VideoFrame frame, EncodeFrameOptions options) => Send(frame);

    /// <summary>Tells the encoder no more frames come, so it releases every packet it holds.</summary>
    void Flush();

    /// <summary>Takes the next packet.</summary>
    /// <param name="packet">The packet, which the caller must dispose.</param>
    /// <returns>Whether a packet was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet);
}
