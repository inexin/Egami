namespace Egami.Video.Filters;

/// <summary>How a <see cref="Deinterlacer"/> works. The defaults deinterlace the frames the decoder flags, one frame out per frame in.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoFilters.cs" region="deinterlace"/>
/// </example>
public sealed record DeinterlacerOptions
{
    /// <summary>Gets the default options.</summary>
    public static DeinterlacerOptions Default { get; } = new();

    /// <summary>Gets how the missing lines are made. Default <see cref="DeinterlaceMode.Adaptive"/>.</summary>
    public DeinterlaceMode Mode { get; init; } = DeinterlaceMode.Adaptive;

    /// <summary>Gets how many frames come out per frame in. Default <see cref="DeinterlaceRate.Frame"/>.</summary>
    public DeinterlaceRate Rate { get; init; } = DeinterlaceRate.Frame;

    /// <summary>Gets which frames are deinterlaced. Default <see cref="DeinterlaceFrames.Flagged"/>.</summary>
    public DeinterlaceFrames Frames { get; init; } = DeinterlaceFrames.Flagged;

    /// <summary>
    /// Gets the field order to use for every frame, or <see langword="null"/> to take each frame's
    /// <see cref="VideoFrame.FieldOrder"/>; a frame that does not say takes the track's, then top field first.
    /// </summary>
    public FieldOrder? FieldOrder { get; init; }

    internal void Validate()
    {
        if (!Enum.IsDefined(Mode))
        {
            throw new ArgumentOutOfRangeException(nameof(Mode), Mode, "Unknown mode.");
        }

        if (!Enum.IsDefined(Rate))
        {
            throw new ArgumentOutOfRangeException(nameof(Rate), Rate, "Unknown rate.");
        }

        if (!Enum.IsDefined(Frames))
        {
            throw new ArgumentOutOfRangeException(nameof(Frames), Frames, "Unknown frame selection.");
        }

        if (FieldOrder is { } order && order is not (Video.FieldOrder.TopFieldFirst or Video.FieldOrder.BottomFieldFirst))
        {
            throw new ArgumentOutOfRangeException(nameof(FieldOrder), order, "The field order must be top or bottom field first.");
        }
    }
}

/// <summary>How a <see cref="Deinterlacer"/> makes the lines a field lacks.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoFilters.cs" region="deinterlace"/>
/// </example>
public enum DeinterlaceMode
{
    /// <summary>
    /// Keeps the other field's lines where the picture does not move, and elsewhere guesses along edges from the field's own
    /// lines, held within what the fields before and after allow. Sharp on still parts, smooth on moving ones.
    /// </summary>
    Adaptive,

    /// <summary>Guesses the missing lines along edges from the field's own lines only. Never combs, but halves the vertical detail.</summary>
    Bob,
}

/// <summary>How many frames a <see cref="Deinterlacer"/> hands out per frame in.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoFilters.cs" region="deinterlace"/>
/// </example>
public enum DeinterlaceRate
{
    /// <summary>One frame per frame, made from the first field in time: 50i becomes 25p.</summary>
    Frame,

    /// <summary>
    /// One frame per field, so the rate doubles (50i becomes 50p): the second frame starts half a frame later, and both last half
    /// as long. Frames that are not deinterlaced come out twice, so the rate stays even.
    /// </summary>
    Field,
}

/// <summary>Which frames a <see cref="Deinterlacer"/> deinterlaces.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoFilters.cs" region="deinterlace"/>
/// </example>
public enum DeinterlaceFrames
{
    /// <summary>Frames whose <see cref="VideoFrame.FieldOrder"/> is not <see cref="FieldOrder.Progressive"/>; others pass unchanged.</summary>
    Flagged,

    /// <summary>Every frame, for sources whose decoder or container does not flag interlaced frames.</summary>
    All,
}
