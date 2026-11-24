using Egami.Formats;

namespace Egami.Svg;

/// <summary>Options for reading SVG through <see cref="SvgFormat"/> with <c>Raster.Load</c> and <c>Raster.Probe</c>.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="svg-format"/>
/// </example>
public sealed record SvgDecodeOptions : ReaderOptions
{
    /// <summary>
    /// Gets how many raster pixels each of the document's pixels becomes: 2 draws an icon at twice its size. The raster is the
    /// document's size times this, rounded up. Defaults to 1.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive and finite.</exception>
    public float Scale
    {
        get;
        init => field = value > 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The scale must be positive and finite.");
    } = 1;

    /// <summary>
    /// Gets the frame rate an animated SVG is loaded at, or <see langword="null"/> (the default) to load the static document
    /// as one frame. With a rate, a document whose SMIL or CSS animations move loads as an animation, as
    /// <see cref="SvgDocument.RasterizeAnimation{TPixel}(SvgAnimationOptions?)"/> draws it with
    /// <see cref="SvgAnimationOptions.FrameRate"/> set to this: one loop, or up to the end of the last animation, at most
    /// 10 seconds, still stretches merged; each frame's delay is how long it lasts, and the loop count is 0 (forever) when
    /// the document loops. A document without animations still loads as one frame.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not positive, or is more than 1000.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Svg.cs" region="load-animation"/>
    /// </example>
    public double? AnimationFrameRate
    {
        get;
        init => field = value is null || (value > 0 && value <= 1000) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The frame rate must be more than 0 and at most 1000.");
    }
}
