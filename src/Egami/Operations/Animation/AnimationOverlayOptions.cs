namespace Egami.Operations;

/// <summary>How <see cref="RasterOps.DrawAnimation"/> pairs the overlay's frames with the base's.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationTools.cs" region="sticker"/>
/// </example>
public enum AnimationTiming
{
    /// <summary>
    /// By time: both timelines are cut at every frame boundary of either, so an overlay at another speed plays at its own speed.
    /// </summary>
    ByTime = 0,

    /// <summary>
    /// By index: base frame i takes overlay frame i and keeps its own delay, whatever the overlay's delays say, as ImageMagick's
    /// -layers Composite does.
    /// </summary>
    ByIndex = 1,
}

/// <summary>Options for <see cref="RasterOps.DrawAnimation"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AnimationTools.cs" region="sticker"/>
/// </example>
public sealed record AnimationOverlayOptions
{
    private readonly BlendMode _blend;
    private readonly float _opacity = 1;
    private readonly int _repeat;
    private readonly TimeSpan _start;
    private readonly AnimationTiming _timing;

    /// <summary>Gets the default options: source-over, opaque, looping for as long as the base runs, from the start, by time.</summary>
    public static AnimationOverlayOptions Default { get; } = new();

    /// <summary>Gets how the overlay is combined with each base frame. Defaults to <see cref="BlendMode.SourceOver"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public BlendMode Blend
    {
        get => _blend;
        init => _blend = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The blend mode is not defined.");
    }

    /// <summary>Gets the overlay's opacity, from 0 to 1. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Opacity
    {
        get => _opacity;
        init => _opacity = value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The opacity must be from 0 to 1.");
    }

    /// <summary>
    /// Gets how many times the overlay plays, or 0 (the default) to loop it for as long as the base runs. Over a still base, 0
    /// plays it once.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Repeat
    {
        get => _repeat;
        init => _repeat = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The repeat count must not be negative.");
    }

    /// <summary>Gets when the overlay starts, from the base's start. Defaults to zero. Ignored by <see cref="AnimationTiming.ByIndex"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan Start
    {
        get => _start;
        init => _start = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The start must not be negative.");
    }

    /// <summary>Gets how frames are paired. Defaults to <see cref="AnimationTiming.ByTime"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public AnimationTiming Timing
    {
        get => _timing;
        init => _timing = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The timing is not defined.");
    }
}
