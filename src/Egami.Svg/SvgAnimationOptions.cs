using Egami.Pixels;

namespace Egami.Svg;

/// <summary>
/// How <see cref="SvgDocument.RasterizeAnimation{TPixel}(SvgAnimationOptions?)"/> and
/// <see cref="SvgDocument.RenderFrames{TPixel}(SvgAnimationOptions?)"/> turn a document's animations into frames: the frame
/// rate, where to start, how long to go on and how each frame is drawn.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="rasterize-animation"/>
/// </example>
public sealed record class SvgAnimationOptions
{
    private readonly double _frameRate = 30;
    private readonly TimeSpan _start;
    private readonly TimeSpan? _length;
    private readonly TimeSpan _maxLength = TimeSpan.FromSeconds(10);

    /// <summary>Gets the defaults: 30 frames a second from the start, one loop or the whole animation, still stretches merged.</summary>
    public static SvgAnimationOptions Default { get; } = new();

    /// <summary>Gets how many frames are drawn a second; 30 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive, or is more than 1000.</exception>
    public double FrameRate
    {
        get => _frameRate;
        init => _frameRate = value > 0 && value <= 1000 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The frame rate must be more than 0 and at most 1000.");
    }

    /// <summary>Gets the moment of the document the first frame shows; zero (when it loads) by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public TimeSpan Start
    {
        get => _start;
        init => _start = value >= TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The start must not be negative.");
    }

    /// <summary>
    /// Gets how long the frames go on from <see cref="Start"/>, or <see langword="null"/> (the default) to let the document
    /// decide: one <see cref="SvgAnimationInfo.LoopPeriod"/> when every animation loops, else up to the end of the last
    /// animation (<see cref="SvgAnimationInfo.Duration"/>), at most <see cref="MaxLength"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan? Length
    {
        get => _length;
        init => _length = value is null || value.Value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The length must be positive.");
    }

    /// <summary>
    /// Gets the longest the document may make the frames go on when <see cref="Length"/> is not set: an animation that runs
    /// forever without a common loop period, ends later or loops more slowly, is cut there (and then plays once) and the
    /// context's diagnostics get an <c>Svg.AnimationLength</c> warning. 10 seconds by default.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive.</exception>
    public TimeSpan MaxLength
    {
        get => _maxLength;
        init => _maxLength = value > TimeSpan.Zero ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The maximum length must be positive.");
    }

    /// <summary>
    /// Gets a value indicating whether frames whose pixels equal the frame before are merged into it, which then lasts longer;
    /// <see langword="true"/> by default. Turn it off for a constant frame rate, as video wants.
    /// </summary>
    public bool MergeDuplicates { get; init; } = true;

    /// <summary>
    /// Gets how each frame is drawn (size, background, languages, canvas and context), or <see langword="null"/> for
    /// <see cref="SvgRenderOptions.Default"/>. Its <see cref="SvgRenderOptions.Time"/> is ignored: each frame sets its own.
    /// </summary>
    public SvgRenderOptions? Render { get; init; }
}

/// <summary>
/// One frame of <see cref="SvgDocument.RenderFrames{TPixel}(SvgAnimationOptions?)"/>: the picture, the moment it shows and
/// how long it lasts. The raster belongs to the caller, who disposes it.
/// </summary>
/// <typeparam name="TPixel">The pixel type.</typeparam>
/// <param name="Raster">The frame's pixels, one frame.</param>
/// <param name="Time">The moment of the document it shows.</param>
/// <param name="Duration">How long it lasts: one frame at the frame rate, or more where still frames merged into it.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="render-frames"/>
/// </example>
public readonly record struct SvgFrame<TPixel>(Raster<TPixel> Raster, TimeSpan Time, TimeSpan Duration)
    where TPixel : unmanaged, IPixelType<TPixel>;
