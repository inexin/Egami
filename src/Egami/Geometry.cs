namespace Egami;

/// <summary>A position in pixels. X grows to the right and Y downward; the top-left pixel is (0, 0).</summary>
/// <param name="X">The column.</param>
/// <param name="Y">The row.</param>
public readonly record struct Point(int X, int Y);

/// <summary>A rectangle of pixels: the top-left corner and a size.</summary>
/// <param name="X">The left column.</param>
/// <param name="Y">The top row.</param>
/// <param name="Width">The width, at least 0.</param>
/// <param name="Height">The height, at least 0.</param>
public readonly record struct Rect(int X, int Y, int Width, int Height)
{
    /// <summary>Initializes a new instance of the <see cref="Rect"/> struct from a corner and a size.</summary>
    /// <param name="position">The top-left corner.</param>
    /// <param name="size">The size.</param>
    public Rect(Point position, Size size)
        : this(position.X, position.Y, size.Width, size.Height)
    {
    }

    /// <summary>Gets the column just past the right edge.</summary>
    public int Right => X + Width;

    /// <summary>Gets the row just past the bottom edge.</summary>
    public int Bottom => Y + Height;

    /// <summary>Gets the top-left corner.</summary>
    public Point Position => new(X, Y);

    /// <summary>Gets the size.</summary>
    public Size Size => new(Width, Height);

    /// <summary>Gets a value indicating whether the rectangle covers no pixels.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>Returns whether the pixel at <paramref name="point"/> lies inside the rectangle.</summary>
    /// <param name="point">The pixel.</param>
    /// <returns><see langword="true"/> if the rectangle contains it.</returns>
    public bool Contains(Point point)
    {
        return point.X >= X && point.Y >= Y && point.X < Right && point.Y < Bottom;
    }

    /// <summary>Returns whether <paramref name="other"/> lies entirely inside this rectangle.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns><see langword="true"/> if this rectangle contains all of it.</returns>
    public bool Contains(Rect other)
    {
        return other.X >= X && other.Y >= Y && other.Right <= Right && other.Bottom <= Bottom;
    }

    /// <summary>Returns the overlap of two rectangles, which is empty when they do not overlap.</summary>
    /// <param name="other">The other rectangle.</param>
    /// <returns>The overlap.</returns>
    public Rect Intersect(Rect other)
    {
        int left = Math.Max(X, other.X);
        int top = Math.Max(Y, other.Y);
        int right = Math.Min(Right, other.Right);
        int bottom = Math.Min(Bottom, other.Bottom);
        return right > left && bottom > top ? new Rect(left, top, right - left, bottom - top) : new Rect(left, top, 0, 0);
    }
}
