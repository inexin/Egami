using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// A detector that looks at decoded pictures one at a time, such as <see cref="SceneDetector"/>. Feed it every frame in
/// presentation order with <see cref="Add"/>, then call <see cref="Complete"/> once at the end; <see cref="MediaAnalysis.Run"/>
/// does both for every detector from one decode, and an app with its own decode loop can do the same. A detector reads the frame
/// while <see cref="Add"/> runs and keeps no reference to it, so the caller may dispose the frame right after.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-detectors"/>
/// </example>
public interface IVideoDetector
{
    /// <summary>Takes the next frame.</summary>
    /// <param name="frame">The frame, in presentation order.</param>
    void Add(YuvVideoFrame frame);

    /// <summary>Tells the detector that no more frames come, so it can settle what it was still weighing.</summary>
    /// <param name="endTime">The time the last frame ends: its timestamp plus its duration, or the file's duration.</param>
    void Complete(MediaTime endTime);
}

/// <summary>
/// A detector that listens to decoded audio one frame at a time. Feed it every frame in time order with <see cref="Add"/>, then
/// call <see cref="Complete"/> once at the end; <see cref="MediaAnalysis.Run"/> does both from one decode. A detector reads the
/// samples while <see cref="Add"/> runs and keeps no reference to the frame.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detector"/>
/// </example>
public interface IAudioDetector
{
    /// <summary>Takes the next frame of samples.</summary>
    /// <param name="frame">The frame, in time order, in any sample format.</param>
    void Add(AudioFrame frame);

    /// <summary>Tells the detector that no more audio comes.</summary>
    /// <param name="endTime">The time the last sample ends.</param>
    void Complete(MediaTime endTime);
}
