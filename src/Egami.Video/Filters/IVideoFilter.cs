using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Filters;

/// <summary>
/// A streaming video filter on decoded planes: frames go in through <see cref="Send"/> and come out through
/// <see cref="TryReceive"/>, the same shape as decoders, encoders and <see cref="Audio.Filters.IAudioFilter"/>, so a filter sits
/// between a decoder and an encoder. A filter may hold frames back (<see cref="Latency"/>) and may hand out more or fewer
/// frames than it took, such as one per field.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoFilters.cs" region="deinterlace"/>
/// </example>
public interface IVideoFilter : IDisposable
{
    /// <summary>Gets the time the filter holds input back before the output for it is ready, such as the next frame it looks at.</summary>
    MediaTime Latency { get; }

    /// <summary>Gives the filter a frame. The caller keeps the frame.</summary>
    /// <param name="frame">The frame.</param>
    void Send(YuvVideoFrame frame);

    /// <summary>Takes the next filtered frame.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame);

    /// <summary>Tells the filter no more input comes, so it hands out the frames it still holds.</summary>
    void Flush();

    /// <summary>Drops the input held and the frames ready, for example after a seek; the next frame starts the stream again.</summary>
    void Reset();
}
