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

    /// <summary>
    /// Gets the defaults: black and white at the Otsu threshold, with the paper traced as a white background shape. The
    /// defaults match potrace's published ones (corner threshold 1, curve tolerance 0.2, speckles under 2 pixels dropped).
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
    /// <see cref="Raster.FindThreshold"/> does.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The level is outside 0 to 1.</exception>
    public float? Threshold
    {
        get => _threshold;
        init => _threshold = value is null || value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The threshold must be from 0 to 1.");
    }

    /// <summary>
    /// Gets how closely joined curves follow the shorter curves they replace, from 0 (loose: fewest curves) to 1 (tight: every
    /// curve kept). Defaults to 0.5, a tolerance of 0.2 pixels.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 1.</exception>
    public float PathFidelity
    {
        get => _pathFidelity;
        init => _pathFidelity = Unit(value, nameof(PathFidelity));
    }

    /// <summary>
    /// Gets how readily corners stay sharp, from 0 (round every corner) to 1 (keep every corner: the outline is a polygon).
    /// Defaults to 0.25.
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
    /// Gets whether white areas are left out. When <see langword="false"/> (the default), a white shape the size of the image
    /// comes first, under the ink, wherever the image has paper.
    /// </summary>
    public bool IgnoreWhite { get; init; }

    /// <summary>The curve tolerance in pixels <see cref="PathFidelity"/> sets: 0.8 at 0, 0.2 at 0.5, 0 (no joining) at 1.</summary>
    internal double Tolerance => 0.8 * (1 - PathFidelity) * (1 - PathFidelity);

    /// <summary>The corner threshold <see cref="Corners"/> sets: 4/3 (no corners) at 0, 1 at 0.25, 0 (all corners) at 1.</summary>
    internal double AlphaMax => 4.0 / 3 * (1 - Corners);

    private static float Unit(float value, string name)
    {
        return value is >= 0 and <= 1 ? value : throw new ArgumentOutOfRangeException(name, value, "The value must be from 0 to 1.");
    }
}
