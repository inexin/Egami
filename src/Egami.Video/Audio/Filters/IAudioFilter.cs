using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Filters;

/// <summary>
/// A streaming audio filter: frames go in through <see cref="Send"/> and come out through <see cref="TryReceive"/>, the same
/// shape as <see cref="Resampler"/>, decoders and encoders, so filters, the resampler and encoders plug together. Frames of any
/// sample format go in; frames come out as <see cref="SampleFormat.F32Planar"/> with exact timestamps. Implement it to put a
/// filter of your own in an <see cref="AudioFilterChain"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-filter-chain"/>
/// </example>
public interface IAudioFilter : IDisposable
{
    /// <summary>Gets the time the filter holds input back before the output for it is ready, such as a lookahead.</summary>
    MediaTime Latency { get; }

    /// <summary>Gives the filter a frame. The caller keeps the frame.</summary>
    /// <param name="frame">The frame, in any sample format.</param>
    void Send(AudioFrame frame);

    /// <summary>Takes the next filtered frame.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    bool TryReceive([NotNullWhen(true)] out AudioFrame? frame);

    /// <summary>Tells the filter no more input comes, so it hands out what it still holds, such as a lookahead or a tail.</summary>
    void Flush();

    /// <summary>Drops the input held and the frames ready, for example after a seek; the next frame starts the stream again.</summary>
    void Reset();
}
