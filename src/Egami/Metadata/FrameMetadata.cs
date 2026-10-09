namespace Egami.Metadata;

/// <summary>What happens to a frame's area before the next frame is drawn.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="frame-list"/>
/// </example>
public enum FrameDisposal
{
    /// <summary>Leave the frame in place.</summary>
    None,

    /// <summary>Clear the frame's area to transparent.</summary>
    ClearToBackground,

    /// <summary>Restore the area to what it was before the frame was drawn.</summary>
    RestorePrevious,
}

/// <summary>How a frame is combined with what is already on the canvas.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="frame-list"/>
/// </example>
public enum FrameBlend
{
    /// <summary>Replace the pixels.</summary>
    Source,

    /// <summary>Alpha-blend over the existing pixels.</summary>
    Over,
}

/// <summary>Metadata that belongs to one frame.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/ApiExamples.cs" region="frame-metadata"/>
/// </example>
public sealed class FrameMetadata
{
    /// <summary>Gets or sets how long the frame is shown in an animation.</summary>
    public TimeSpan Delay { get; set; }

    /// <summary>Gets or sets what happens to the frame's area before the next frame.</summary>
    public FrameDisposal Disposal { get; set; }

    /// <summary>Gets or sets how the frame is combined with the canvas.</summary>
    public FrameBlend Blend { get; set; }

    /// <summary>
    /// Gets or sets the rectangle the frame was stored as in its file, or <see langword="null"/>. For information: writers work out
    /// their own from the pixels, and operations that change the size or layout clear it.
    /// </summary>
    public Rect? Bounds { get; set; }

    /// <summary>
    /// Gets or sets format-specific details of the frame from the reader, such as a GIF frame's own palette, or
    /// <see langword="null"/>. Operations that change the size or layout clear it.
    /// </summary>
    public FormatDetails? FormatDetails { get; set; }

    /// <summary>Returns a deep copy.</summary>
    /// <returns>The copy.</returns>
    public FrameMetadata Copy()
    {
        return new FrameMetadata { Delay = Delay, Disposal = Disposal, Blend = Blend, Bounds = Bounds, FormatDetails = FormatDetails?.Copy() };
    }
}
