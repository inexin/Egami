using Egami.Pixels;

namespace Egami.Drawing.Tracing;

/// <summary>
/// How a raster is traced into shapes. Start from a preset and change what you need with <c>with</c>; every setting is checked
/// when it is set.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Tracing.cs" region="options"/>
/// </example>
public sealed record class TraceOptions
{
    private readonly TraceMode _mode = TraceMode.BlackAndWhite;
    private readonly float? _threshold;
    private readonly float _pathFidelity = 0.5f;
    private readonly float _corners = 0.25f;
    private readonly int _noise = 2;
    private readonly int _colors = 8;
    private readonly Rgba8[]? _palette;
    private readonly TraceLayering _layering = TraceLayering.Stacked;
    private readonly float _smoothing;
    private readonly float? _strokeWidth;

    /// <summary>
    /// Gets the defaults: black and white at the Otsu threshold, with the paper traced as a white background shape, curves within
    /// half a pixel, turns of more than 60 degrees kept as corners and single-pixel specks dropped.
    /// </summary>
    public static TraceOptions Default { get; } = new();

    /// <summary>Gets the defaults with <see cref="IgnoreWhite"/> on: the ink alone, for signatures, line art and logos to place over other content.</summary>
    public static TraceOptions BlackAndWhite { get; } = new() { IgnoreWhite = true };

    /// <summary>Gets how pixels are turned into shapes. Defaults to <see cref="TraceMode.BlackAndWhite"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The mode is not defined.</exception>
    public TraceMode Mode
    {
        get => _mode;
        init => _mode = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The mode is not defined.");
    }

    /// <summary>
    /// Gets the level that splits ink from paper, from 0 (black) to 1 (white): a pixel is ink when its intensity, over white by
    /// its alpha, is at most this. <see langword="null"/> (the default) finds it with Otsu's method, as
    /// <see cref="Raster.FindThreshold"/> does. Used in black and white and centerline only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 1.</exception>
    public float? Threshold
    {
        get => _threshold;
        init => _threshold = value is null || value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold must be from 0 to 1.");
    }

    /// <summary>
    /// Gets how closely the fitted curves follow the pixel edges, from 0 (loose: fewest curves) to 1 (tight: most curves).
    /// Defaults to 0.5: in black and white, joined curves stay within 0.2 pixels of the curves they replace; in grayscale and
    /// color, the curves stay within 0.5 pixels of the edge points.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float PathFidelity
    {
        get => _pathFidelity;
        init => _pathFidelity = Unit(value, nameof(PathFidelity));
    }

    /// <summary>
    /// Gets how readily corners stay sharp, from 0 (round every corner) to 1 (keep every corner: the outline is a polygon).
    /// Defaults to 0.25, which keeps right angles sharp and, in grayscale and color, any turn of more than 60 degrees.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Corners
    {
        get => _corners;
        init => _corners = Unit(value, nameof(Corners));
    }

    /// <summary>
    /// Gets the smallest region kept, in pixels: a region of fewer pixels merges into the regions around it, smallest first.
    /// 0 or 1 keeps every pixel. Defaults to 2, dropping single-pixel specks and holes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative.</exception>
    public int Noise
    {
        get => _noise;
        init => _noise = value >= 0 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The noise size must not be negative.");
    }

    /// <summary>
    /// Gets whether white areas are left out. In black and white and centerline, when <see langword="false"/> (the default), a white shape the
    /// size of the image comes first, under the ink, wherever the image has paper. In grayscale, color and logo, the layer of each
    /// palette color whose red, green and blue are all at least 240 is left out, and its areas stay empty in the other layers;
    /// in logo, that includes the transparency the art encloses, which is otherwise traced as white.
    /// </summary>
    public bool IgnoreWhite { get; init; }

    /// <summary>
    /// Gets the most colors (in <see cref="TraceMode.Grayscale"/>, gray levels) a grayscale, color or logo trace reduces the image to,
    /// from 2 to 256. The colors are the image's own flat ones, found with the blends along edges left out; an image with fewer
    /// distinct colors gets fewer. Defaults to 8. Not used in black and white or when <see cref="Palette"/> is set.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 2 to 256.</exception>
    public int Colors
    {
        get => _colors;
        init => _colors = value is >= 2 and <= 256 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The number of colors must be from 2 to 256.");
    }

    /// <summary>
    /// Gets fixed colors for a grayscale, color or logo trace, or <see langword="null"/> (the default) to find them in the image.
    /// Each pixel takes the nearest of them (by intensity in grayscale; in logo, each of the image's own colors does), and the
    /// shapes take these colors exactly. The list is copied.
    /// </summary>
    /// <exception cref="ArgumentException">The list is empty or has more than 256 colors.</exception>
    public IReadOnlyList<Rgba8>? Palette
    {
        get => _palette;
        init => _palette = value is null ? null : value.Count is >= 1 and <= 256 ? [.. value] : throw new ArgumentException("The palette must have from 1 to 256 colors.", nameof(value));
    }

    /// <summary>Gets how the layers of a grayscale, color or logo trace fit together. Defaults to <see cref="TraceLayering.Stacked"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public TraceLayering Layering
    {
        get => _layering;
        init => _layering = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The layering is not defined.");
    }

    /// <summary>
    /// Gets whether the straight runs of a grayscale, color or logo outline become lines. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SnapToLines { get; init; } = true;

    /// <summary>
    /// Gets whether the runs of a grayscale, color or logo outline that lie on a circle become circular arcs, and round outlines
    /// circles. Defaults to <see langword="true"/>.
    /// </summary>
    public bool SnapToArcs { get; init; } = true;

    /// <summary>
    /// Gets how strongly a grayscale or color trace smooths the image before reducing its colors, from 0 (not at all, the
    /// default) to 1. The smoothing keeps edges between clearly different colors and flattens texture and noise, for photos. Not
    /// used in logo, which takes flat-color art only.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float Smoothing
    {
        get => _smoothing;
        init => _smoothing = Unit(value, nameof(Smoothing));
    }

    /// <summary>
    /// Gets the pen width of every stroke a <see cref="TraceMode.Centerline"/> trace gives, in pixels, or <see langword="null"/>
    /// (the default) to give each stroke the width its ink covers. Not used in the other modes.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The width is not positive and finite.</exception>
    public float? StrokeWidth
    {
        get => _strokeWidth;
        init => _strokeWidth = value is null || (value > 0 && float.IsFinite(value.Value)) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The stroke width must be positive and finite.");
    }

    private static float Unit(float value, string name)
    {
        return value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(name, value, "The value must be from 0 to 1.");
    }
}
