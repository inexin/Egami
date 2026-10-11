using System.Numerics;

namespace Egami.Drawing;

/// <summary>
/// A rectangle in float pixels, y down: the left edge at <see cref="X"/>, the top at <see cref="Y"/>. A width or height of 0 or
/// less makes it empty. Pixel (0, 0) covers the square from (0, 0) to (1, 1), so a rectangle with whole-number edges covers whole
/// pixels.
/// </summary>
/// <param name="X">The left edge.</param>
/// <param name="Y">The top edge.</param>
/// <param name="Width">The width; empty when 0 or less.</param>
/// <param name="Height">The height; empty when 0 or less.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Drawing.cs" region="shapes"/>
/// </example>
public readonly record struct RectF(float X, float Y, float Width, float Height)
{
    /// <summary>Gets the empty rectangle at the origin.</summary>
    public static RectF Empty => default;

    /// <summary>Gets the left edge, <see cref="X"/>.</summary>
    public float Left => X;

    /// <summary>Gets the top edge, <see cref="Y"/>.</summary>
    public float Top => Y;

    /// <summary>Gets the right edge, <see cref="X"/> + <see cref="Width"/>.</summary>
    public float Right => X + Width;

    /// <summary>Gets the bottom edge, <see cref="Y"/> + <see cref="Height"/>.</summary>
    public float Bottom => Y + Height;

    /// <summary>Gets the top-left corner.</summary>
    public Vector2 Position => new(X, Y);

    /// <summary>Gets the width and height.</summary>
    public Vector2 Size => new(Width, Height);

    /// <summary>Gets the center.</summary>
    public Vector2 Center => new(X + (Width / 2), Y + (Height / 2));

    /// <summary>Gets a value indicating whether the rectangle covers no area: its width or height is 0 or less, or not a number.</summary>
    public bool IsEmpty => !(Width > 0 && Height > 0);

    /// <summary>Gets a value indicating whether every coordinate is a finite number.</summary>
    public bool IsFinite => float.IsFinite(X) && float.IsFinite(Y) && float.IsFinite(Width) && float.IsFinite(Height)
        && float.IsFinite(X + Width) && float.IsFinite(Y + Height);

    /// <summary>Converts an integer rectangle.</summary>
    /// <param name="rect">The rectangle in whole pixels.</param>
    public static implicit operator RectF(Rect rect) => new(rect.X, rect.Y, rect.Width, rect.Height);

    /// <summary>Creates a rectangle from its four edges.</summary>
    /// <param name="left">The left edge.</param>
    /// <param name="top">The top edge.</param>
    /// <param name="right">The right edge.</param>
    /// <param name="bottom">The bottom edge.</param>
    /// <returns>The rectangle; empty when <paramref name="right"/> is not past <paramref name="left"/> or <paramref name="bottom"/> not below <paramref name="top"/>.</returns>
    public static RectF FromEdges(float left, float top, float right, float bottom) => new(left, top, right - left, bottom - top);

    /// <summary>Creates a rectangle from a corner and a size.</summary>
    /// <param name="position">The top-left corner.</param>
    /// <param name="size">The width and height.</param>
    /// <returns>The rectangle.</returns>
    public static RectF FromPosition(Vector2 position, Vector2 size) => new(position.X, position.Y, size.X, size.Y);

    /// <summary>Creates the smallest rectangle holding every point.</summary>
    /// <param name="points">The points.</param>
    /// <returns>The bounds, or <see cref="Empty"/> when there are no points.</returns>
    public static RectF FromPoints(ReadOnlySpan<Vector2> points)
    {
        if (points.IsEmpty)
        {
            return Empty;
        }

        Vector2 min = points[0], max = points[0];
        foreach (Vector2 p in points[1..])
        {
            min = Vector2.Min(min, p);
            max = Vector2.Max(max, p);
        }

        return FromEdges(min.X, min.Y, max.X, max.Y);
    }

    /// <summary>Returns whether a point lies inside: left and top edges included, right and bottom excluded.</summary>
    /// <param name="point">The point.</param>
    /// <returns><see langword="true"/> if the point is inside.</returns>
    public bool Contains(Vector2 point) => point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;

    /// <summary>Returns whether another rectangle lies wholly inside this one. An empty rectangle is inside none.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns><see langword="true"/> if <paramref name="other"/> is inside.</returns>
    public bool Contains(RectF other) => !other.IsEmpty && !IsEmpty && other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;

    /// <summary>Returns whether the two rectangles share any area.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns><see langword="true"/> if they overlap.</returns>
    public bool IntersectsWith(RectF other) => !Intersect(other).IsEmpty;

    /// <summary>Returns the area both rectangles cover.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns>The overlap, or <see cref="Empty"/> when there is none.</returns>
    public RectF Intersect(RectF other)
    {
        float left = MathF.Max(X, other.X), top = MathF.Max(Y, other.Y);
        float right = MathF.Min(Right, other.Right), bottom = MathF.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? FromEdges(left, top, right, bottom) : Empty;
    }

    /// <summary>Returns the smallest rectangle holding both. An empty rectangle adds nothing.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns>The union.</returns>
    public RectF Union(RectF other)
    {
        if (other.IsEmpty)
        {
            return this;
        }

        if (IsEmpty)
        {
            return other;
        }

        return FromEdges(MathF.Min(X, other.X), MathF.Min(Y, other.Y), MathF.Max(Right, other.Right), MathF.Max(Bottom, other.Bottom));
    }

    /// <summary>Returns the rectangle grown by <paramref name="dx"/> on the left and right and <paramref name="dy"/> on the top and bottom; negative values shrink it.</summary>
    /// <param name="dx">The horizontal growth per side.</param>
    /// <param name="dy">The vertical growth per side.</param>
    /// <returns>The grown rectangle.</returns>
    public RectF Inflate(float dx, float dy) => new(X - dx, Y - dy, Width + (2 * dx), Height + (2 * dy));

    /// <summary>Returns the rectangle moved by an offset.</summary>
    /// <param name="offset">The distance to move.</param>
    /// <returns>The moved rectangle.</returns>
    public RectF Offset(Vector2 offset) => new(X + offset.X, Y + offset.Y, Width, Height);

    /// <summary>Returns the smallest integer rectangle holding this one: edges rounded outwards.</summary>
    /// <returns>The covering pixel rectangle; empty when this one is empty.</returns>
    public Rect RoundOut()
    {
        if (IsEmpty || !IsFinite)
        {
            return default;
        }

        int left = (int)Math.Clamp(MathF.Floor(X), int.MinValue / 2, int.MaxValue / 2);
        int top = (int)Math.Clamp(MathF.Floor(Y), int.MinValue / 2, int.MaxValue / 2);
        int right = (int)Math.Clamp(MathF.Ceiling(Right), int.MinValue / 2, int.MaxValue / 2);
        int bottom = (int)Math.Clamp(MathF.Ceiling(Bottom), int.MinValue / 2, int.MaxValue / 2);
        return new Rect(left, top, right - left, bottom - top);
    }
}
