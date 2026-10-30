namespace Egami;

/// <summary>
/// How far a save such as <see cref="Raster.Save(string, Formats.WriterOptions?, IProgress{SaveProgress}?, CancellationToken)"/>
/// has come. A save reports once as it starts, then at most every 250 ms, then once more with <see cref="IsFinal"/> set.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/GettingStarted.cs" region="save-progress"/>
/// </example>
public readonly record struct SaveProgress
{
    /// <summary>
    /// Gets how much of the save is done, from 0 to 1, or <see langword="null"/> when the format's writer does not say (it then
    /// reports only as it starts and ends). Formats that encode the whole image at the end, such as AVIF, JPEG XL and lossy WebP,
    /// count their own work, not the rows handed to them. It never goes backwards, and the final report has 1.
    /// </summary>
    public double? Fraction { get; init; }

    /// <summary>Gets how many frames of an animation were written; 0 for a still image until the final report, which has 1.</summary>
    public int Frames { get; init; }

    /// <summary>Gets how many bytes were written to the output, or 0 when it cannot seek (such as a pipe).</summary>
    public long BytesWritten { get; init; }

    /// <summary>Gets the wall-clock time since the save started.</summary>
    public TimeSpan Elapsed { get; init; }

    /// <summary>Gets whether this is the last report: the save finished.</summary>
    public bool IsFinal { get; init; }
}
