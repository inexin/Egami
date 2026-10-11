using System.Numerics;

namespace Egami.Drawing;

/// <summary>What a <see cref="RoundRect"/> reduces to, so backends and the rasterizer can take a shortcut.</summary>
public enum RoundRectKind
{
    /// <summary>The bounds are empty: nothing is drawn.</summary>
    Empty = 0,

    /// <summary>Every radius is 0: a plain rectangle.</summary>
    Rect = 1,

    /// <summary>Every corner's radii are half the width and half the height: an ellipse.</summary>
    Oval = 2,

    /// <summary>All four corners have the same radii.</summary>
    Simple = 3,

    /// <summary>The corners differ.</summary>
    Complex = 4,
}

/// <summary>
/// The x and y radius of each corner of a <see cref="RoundRect"/>, clockwise from the top-left. A radius below 0 counts as 0, and a
/// corner with either radius 0 is square.
/// </summary>
/// <param name="TopLeft">The top-left corner's x and y radius.</param>
/// <param name="TopRight">The top-right corner's x and y radius.</param>
/// <param name="BottomRight">The bottom-right corner's x and y radius.</param>
/// <param name="BottomLeft">The bottom-left corner's x and y radius.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="shapes"/>
/// </example>
public readonly record struct CornerRadii(Vector2 TopLeft, Vector2 TopRight, Vector2 BottomRight, Vector2 BottomLeft)
{
    /// <summary>Initializes a new instance of the <see cref="CornerRadii"/> struct with one circular radius for every corner.</summary>
    /// <param name="radius">The radius of every corner.</param>
    public CornerRadii(float radius)
        : this(new Vector2(radius), new Vector2(radius), new Vector2(radius), new Vector2(radius))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CornerRadii"/> struct with one elliptical radius for every corner.</summary>
    /// <param name="radiusX">The x radius of every corner.</param>
    /// <param name="radiusY">The y radius of every corner.</param>
    public CornerRadii(float radiusX, float radiusY)
        : this(new Vector2(radiusX, radiusY), new Vector2(radiusX, radiusY), new Vector2(radiusX, radiusY), new Vector2(radiusX, radiusY))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="CornerRadii"/> struct with a circular radius per corner.</summary>
    /// <param name="topLeft">The top-left radius.</param>
    /// <param name="topRight">The top-right radius.</param>
    /// <param name="bottomRight">The bottom-right radius.</param>
    /// <param name="bottomLeft">The bottom-left radius.</param>
    public CornerRadii(float topLeft, float topRight, float bottomRight, float bottomLeft)
        : this(new Vector2(topLeft), new Vector2(topRight), new Vector2(bottomRight), new Vector2(bottomLeft))
    {
    }

    /// <summary>Gets the radii of no corner: a plain rectangle.</summary>
    public static CornerRadii Zero => default;

    /// <summary>Gets the radii of corner <paramref name="index"/>: 0 top-left, 1 top-right, 2 bottom-right, 3 bottom-left.</summary>
    /// <param name="index">The corner, 0 to 3.</param>
    /// <returns>The x and y radius.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="index"/> is outside 0 to 3.</exception>
    public Vector2 this[int index] => index switch
    {
        0 => TopLeft,
        1 => TopRight,
        2 => BottomRight,
        3 => BottomLeft,
        _ => throw new ArgumentOutOfRangeException(nameof(index), index, "A corner is 0 to 3."),
    };
}

/// <summary>
/// A rectangle with an x and y radius per corner. Radii that would overlap are scaled down together, as CSS and Skia do: every
/// radius is multiplied by the smallest ratio of a side's length to the sum of the two radii along it, when that ratio is below 1.
/// A radius below 0 counts as 0, and a corner with either radius 0 is square.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="shapes"/>
/// </example>
public readonly record struct RoundRect
{
    /// <summary>Initializes a new instance of the <see cref="RoundRect"/> struct.</summary>
    /// <param name="bounds">The rectangle.</param>
    /// <param name="radii">The corner radii, scaled down when they overlap.</param>
    /// <exception cref="ArgumentException">A coordinate or radius is not a finite number.</exception>
    public RoundRect(RectF bounds, CornerRadii radii)
    {
        if (!bounds.IsFinite)
        {
            throw new ArgumentException("The bounds must be finite.", nameof(bounds));
        }

        if (bounds.Width < 0 || bounds.Height < 0)
        {
            bounds = RectF.FromEdges(MathF.Min(bounds.Left, bounds.Right), MathF.Min(bounds.Top, bounds.Bottom), MathF.Max(bounds.Left, bounds.Right), MathF.Max(bounds.Top, bounds.Bottom));
        }

        Bounds = bounds;
        Span<Vector2> r = [radii.TopLeft, radii.TopRight, radii.BottomRight, radii.BottomLeft];
        for (int i = 0; i < 4; i++)
        {
            if (!float.IsFinite(r[i].X) || !float.IsFinite(r[i].Y))
            {
                throw new ArgumentException("Corner radii must be finite.", nameof(radii));
            }

            r[i] = r[i].X <= 0 || r[i].Y <= 0 ? Vector2.Zero : r[i];
        }

        if (bounds.IsEmpty)
        {
            Radii = CornerRadii.Zero;
            Kind = RoundRectKind.Empty;
            return;
        }

        // CSS Backgrounds 3, "Overlapping Curves": one factor for all corners, so the shape keeps its proportions. Sums run in double
        // so huge radii cannot overflow.
        double w = bounds.Width, h = bounds.Height;
        double f = 1;
        f = Ratio(f, w, r[0].X + (double)r[1].X);
        f = Ratio(f, w, r[3].X + (double)r[2].X);
        f = Ratio(f, h, r[0].Y + (double)r[3].Y);
        f = Ratio(f, h, r[1].Y + (double)r[2].Y);
        if (f < 1)
        {
            for (int i = 0; i < 4; i++)
            {
                r[i] = new Vector2((float)(r[i].X * f), (float)(r[i].Y * f));
            }
        }

        Radii = new CornerRadii(r[0], r[1], r[2], r[3]);
        Kind = KindOf(bounds, r);
    }

    /// <summary>Initializes a new instance of the <see cref="RoundRect"/> struct with one circular radius for every corner.</summary>
    /// <param name="bounds">The rectangle.</param>
    /// <param name="radius">The radius of every corner.</param>
    public RoundRect(RectF bounds, float radius)
        : this(bounds, new CornerRadii(radius))
    {
    }

    /// <summary>Initializes a new instance of the <see cref="RoundRect"/> struct with one elliptical radius for every corner.</summary>
    /// <param name="bounds">The rectangle.</param>
    /// <param name="radiusX">The x radius of every corner.</param>
    /// <param name="radiusY">The y radius of every corner.</param>
    public RoundRect(RectF bounds, float radiusX, float radiusY)
        : this(bounds, new CornerRadii(radiusX, radiusY))
    {
    }

    /// <summary>Gets the rectangle.</summary>
    public RectF Bounds { get; }

    /// <summary>Gets the corner radii, after scaling.</summary>
    public CornerRadii Radii { get; }

    /// <summary>Gets what the shape reduces to.</summary>
    public RoundRectKind Kind { get; }

    /// <summary>Gets a value indicating whether the shape covers no area.</summary>
    public bool IsEmpty => Kind == RoundRectKind.Empty;

    /// <summary>Creates the ellipse that fills a rectangle.</summary>
    /// <param name="bounds">The rectangle.</param>
    /// <returns>The oval as a round rectangle.</returns>
    public static RoundRect Oval(RectF bounds) => new(bounds, new CornerRadii(MathF.Abs(bounds.Width) / 2, MathF.Abs(bounds.Height) / 2));

    /// <summary>Returns whether a point lies inside the shape: inside the rectangle and not outside a corner's ellipse.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> if the point is inside.</returns>
    public bool Contains(Vector2 point)
    {
        RectF b = Bounds;
        if (!(point.X >= b.Left && point.X <= b.Right && point.Y >= b.Top && point.Y <= b.Bottom) || IsEmpty)
        {
            return false;
        }

        CornerRadii r = Radii;
        return InCorner(point, new Vector2(b.Left + r.TopLeft.X, b.Top + r.TopLeft.Y), r.TopLeft, point.X < b.Left + r.TopLeft.X && point.Y < b.Top + r.TopLeft.Y)
            && InCorner(point, new Vector2(b.Right - r.TopRight.X, b.Top + r.TopRight.Y), r.TopRight, point.X > b.Right - r.TopRight.X && point.Y < b.Top + r.TopRight.Y)
            && InCorner(point, new Vector2(b.Right - r.BottomRight.X, b.Bottom - r.BottomRight.Y), r.BottomRight, point.X > b.Right - r.BottomRight.X && point.Y > b.Bottom - r.BottomRight.Y)
            && InCorner(point, new Vector2(b.Left + r.BottomLeft.X, b.Bottom - r.BottomLeft.Y), r.BottomLeft, point.X < b.Left + r.BottomLeft.X && point.Y > b.Bottom - r.BottomLeft.Y);
    }

    /// <summary>Returns the shape moved by an offset.</summary>
    /// <param name="offset">The distance to move.</param>
    /// <returns>The moved shape.</returns>
    public RoundRect Offset(Vector2 offset) => new(Bounds.Offset(offset), Radii);

    /// <summary>Returns the shape grown by <paramref name="delta"/> on every side, with each radius grown by the same amount (shrunk when negative), as Skia's outset does.</summary>
    /// <param name="delta">The growth per side.</param>
    /// <returns>The grown shape.</returns>
    public RoundRect Inflate(float delta)
    {
        CornerRadii r = Radii;
        return new RoundRect(Bounds.Inflate(delta, delta), new CornerRadii(Grow(r.TopLeft, delta), Grow(r.TopRight, delta), Grow(r.BottomRight, delta), Grow(r.BottomLeft, delta)));
    }

    /// <inheritdoc/>
    public override string ToString() => $"RoundRect {{ Bounds = {Bounds}, Radii = {Radii}, Kind = {Kind} }}";

    private static Vector2 Grow(Vector2 radius, float delta) => radius == Vector2.Zero ? radius : Vector2.Max(radius + new Vector2(delta), Vector2.Zero);

    private static double Ratio(double f, double side, double sum) => sum > side ? Math.Min(f, side / sum) : f;

    private static bool InCorner(Vector2 point, Vector2 center, Vector2 radius, bool inCornerBox)
    {
        if (!inCornerBox || radius == Vector2.Zero)
        {
            return true;
        }

        float dx = (point.X - center.X) / radius.X, dy = (point.Y - center.Y) / radius.Y;
        return (dx * dx) + (dy * dy) <= 1;
    }

    private static RoundRectKind KindOf(RectF bounds, ReadOnlySpan<Vector2> r)
    {
        if (r[0] == Vector2.Zero && r[1] == Vector2.Zero && r[2] == Vector2.Zero && r[3] == Vector2.Zero)
        {
            return RoundRectKind.Rect;
        }

        if (r[0] == r[1] && r[1] == r[2] && r[2] == r[3])
        {
            // Half the side, to a float's rounding: radii scaled down from larger ones land within an ulp of it.
            bool half = MathF.Abs((2 * r[0].X) - bounds.Width) <= bounds.Width * 1e-6f && MathF.Abs((2 * r[0].Y) - bounds.Height) <= bounds.Height * 1e-6f;
            return half ? RoundRectKind.Oval : RoundRectKind.Simple;
        }

        return RoundRectKind.Complex;
    }
}
