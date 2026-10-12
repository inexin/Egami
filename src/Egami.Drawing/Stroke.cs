namespace Egami.Drawing;

/// <summary>How the ends of an open stroked contour look.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="strokes"/>
/// </example>
public enum LineCap
{
    /// <summary>The stroke stops square at the end point. The default, as in SVG, PDF and Skia.</summary>
    Butt = 0,

    /// <summary>A half circle of the stroke's width goes past the end point.</summary>
    Round = 1,

    /// <summary>The stroke goes on past the end point by half its width, ending square.</summary>
    Square = 2,
}

/// <summary>How a stroke turns the corner where two segments meet.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="strokes"/>
/// </example>
public enum LineJoin
{
    /// <summary>The outer edges are extended until they meet, unless that point is further than <see cref="Stroke.MiterLimit"/> allows; then as <see cref="Bevel"/>. The default.</summary>
    Miter = 0,

    /// <summary>A circular arc around the corner.</summary>
    Round = 1,

    /// <summary>The outer corners are joined by a straight line.</summary>
    Bevel = 2,
}

/// <summary>
/// How a path is outlined: the pen's width, caps, joins and miter limit. The settings travel with the drawing call as data, so a
/// vector backend can write them as they are (PDF's <c>w</c>, <c>J</c>, <c>j</c> and <c>M</c>). The pen is applied in the canvas's
/// local space and then transformed, so a non-uniform scale gives an elliptical pen, as in PDF, SVG and Skia.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="strokes"/>
/// </example>
public sealed record class Stroke
{
    private readonly float _width = 1;
    private readonly LineCap _cap;
    private readonly LineJoin _join;
    private readonly float _miterLimit = 4;

    /// <summary>Initializes a new instance of the <see cref="Stroke"/> class.</summary>
    /// <param name="width">The pen's width in local units; 0 is a <see cref="Hairline"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="width"/> is negative or not finite.</exception>
    public Stroke(float width = 1)
    {
        Width = width;
    }

    /// <summary>Gets a hairline: one device pixel wide whatever the transform, as PDF's thinnest line and Skia's width 0 are.</summary>
    public static Stroke Hairline { get; } = new(0);

    /// <summary>Gets the pen's width in local units. 0 draws a hairline. Defaults to 1.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or not finite.</exception>
    public float Width
    {
        get => _width;
        init => _width = value >= 0 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "A stroke's width must be finite and not negative.");
    }

    /// <summary>Gets how open contours end. Defaults to <see cref="LineCap.Butt"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public LineCap Cap
    {
        get => _cap;
        init => _cap = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>Gets how corners are joined. Defaults to <see cref="LineJoin.Miter"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is not defined.</exception>
    public LineJoin Join
    {
        get => _join;
        init => _join = Enum.IsDefined(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, null);
    }

    /// <summary>
    /// Gets the longest miter allowed, as a multiple of the width: a miter join whose point lies further out than this is drawn as
    /// a bevel, as SVG, PDF and Skia define it. Defaults to 4, as in SVG and Skia (PDF's default is 10).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1 or not finite.</exception>
    public float MiterLimit
    {
        get => _miterLimit;
        init => _miterLimit = value >= 1 && float.IsFinite(value) ? value : throw new ArgumentOutOfRangeException(nameof(value), value, "The miter limit must be finite and at least 1.");
    }

    /// <summary>
    /// Gets the dashes, or <see langword="null"/> for a solid line. The pattern travels with the drawing call as data, so a vector
    /// backend writes it as it is (PDF's <c>d</c>, SVG's <c>stroke-dasharray</c>); the raster backend cuts the path into dashes
    /// before outlining it. Lengths are in local units, like the width. Defaults to <see langword="null"/>.
    /// </summary>
    public DashPattern? Dash { get; init; }

    /// <summary>
    /// Gets an effect the canvas applies to the path before it is dashed and stroked, as Skia orders them, or <see langword="null"/>
    /// for none. Backends receive the effect's result as the path to stroke. Defaults to <see langword="null"/>.
    /// </summary>
    public PathEffect? Effect { get; init; }

    /// <summary>Gets a value indicating whether this is a hairline: width 0.</summary>
    public bool IsHairline => Width == 0;

    /// <summary>
    /// Returns the outline this pen draws around a path, as a path to fill with the nonzero rule: the effect and dashes applied,
    /// then each piece offset by half the width with the joins and caps, as the raster backend fills it. Curves become straight
    /// pieces within a tenth of a unit divided by <paramref name="scale"/>, so filling the outline at that scale gives exactly
    /// the pixels stroking does. The raw outline overlaps itself where the path turns sharply or crosses itself; with
    /// <paramref name="simplify"/> the overlaps are removed by <see cref="Path.Simplify"/>, for export or hit testing.
    /// </summary>
    /// <param name="path">The path to outline.</param>
    /// <param name="scale">How much larger the outline will be drawn, which sets how finely curves are cut.</param>
    /// <param name="simplify">Whether to remove overlaps.</param>
    /// <returns>The outline; empty for a hairline, which has no width in path units.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="scale"/> is not positive and finite.</exception>
    /// <exception cref="ArgumentException">The outline does not fit in float coordinates.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="dashes"/>
    /// </example>
    public Path ToPath(Path path, float scale = 1, bool simplify = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (!(scale > 0 && float.IsFinite(scale)))
        {
            throw new ArgumentOutOfRangeException(nameof(scale), scale, "The scale must be positive and finite.");
        }

        if (Effect is not null)
        {
            path = path.Apply(Effect);
        }

        if (Dash is not null)
        {
            path = Dash.Apply(path);
        }

        if (IsHairline || path.Points.Length == 0)
        {
            return Path.Empty;
        }

        Path outline = Geometry.Stroker.Outline(path, this, CanvasOptions.DefaultTolerance / scale);
        foreach (System.Numerics.Vector2 p in outline.Points)
        {
            if (!float.IsFinite(p.X) || !float.IsFinite(p.Y))
            {
                throw new ArgumentException("The outline does not fit in float coordinates.", nameof(path));
            }
        }

        return simplify ? outline.Simplify(PathOps.DefaultTolerance / scale) : outline;
    }
}
