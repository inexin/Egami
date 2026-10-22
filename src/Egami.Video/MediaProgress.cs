namespace Egami.Video;

/// <summary>
/// How far a job such as <see cref="MediaFile.Remux(string, string, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
/// has come. A job reports once its output is open, then at most every 250 ms, then once more with <see cref="IsFinal"/> set.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-progress"/>
/// </example>
public readonly record struct MediaProgress
{
    /// <summary>Gets the newest timestamp written, across all tracks.</summary>
    public MediaTime Position { get; init; }

    /// <summary>Gets the input's duration, or <see langword="null"/> when it is unknown.</summary>
    public MediaTime? Duration { get; init; }

    /// <summary>
    /// Gets how much of the job is done, from 0 to 1: <see cref="Position"/> over <see cref="Duration"/>, else bytes read over
    /// the input's length, else <see langword="null"/> (a pipe with no duration). It never goes backwards, and the final report
    /// has 1.
    /// </summary>
    public double? Fraction { get; init; }

    /// <summary>Gets how many video frames were written.</summary>
    public long Frames { get; init; }

    /// <summary>Gets how many packets were written, on all tracks.</summary>
    public long Packets { get; init; }

    /// <summary>Gets how many bytes were read from the input.</summary>
    public long BytesRead { get; init; }

    /// <summary>Gets how many bytes were written to the output.</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the wall-clock time since the job started.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>
    /// Gets media seconds written per wall-clock second, like FFmpeg's <c>speed=</c>, or <see langword="null"/> before any time
    /// has passed.
    /// </summary>
    public double? Speed { get; init; }

    /// <summary>Gets whether this is the last report: the job finished.</summary>
    public bool IsFinal { get; init; }
}
