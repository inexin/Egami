namespace Egami.Operations;

/// <summary>
/// The shape <see cref="RasterOps.Morphology(MorphologyOp, StructuringElement, int)"/> takes its maximum or minimum over: a set of
/// cells around an origin, which sits on the pixel being computed. The factories make the shapes ImageMagick's -morphology kernels
/// of the same names make (its Plus is <see cref="Cross"/> here). The default value is the origin alone, which changes nothing.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/EdgesAndMorphology.cs" region="shapes"/>
/// </example>
public readonly struct StructuringElement
{
    /// <summary>The largest radius the factories take, and the largest mask side is twice it plus one.</summary>
    internal const int MaxRadius = 1000;

    // Each row's runs of cells, as (dy, first dx, last dx) triples relative to the origin, top row first.
    private readonly int[]? _runs;
    private readonly int _width;
    private readonly int _height;
    private readonly bool _rectangle;

    private StructuringElement(int width, int height, Point origin, int[] runs, bool rectangle)
    {
        _width = width;
        _height = height;
        Origin = origin;
        _runs = runs;
        _rectangle = rectangle;
    }

    /// <summary>Gets the width of the shape's bounding box in pixels.</summary>
    public int Width => _runs is null ? 1 : _width;

    /// <summary>Gets the height of the shape's bounding box in pixels.</summary>
    public int Height => _runs is null ? 1 : _height;

    /// <summary>Gets the origin's cell in the bounding box, from its top-left corner: the cell that sits on the pixel being computed.</summary>
    public Point Origin { get; }

    /// <summary>Gets whether every cell of the bounding box is set, so the shape runs as two passes of running maxima.</summary>
    internal bool IsRectangle => _runs is null || _rectangle;

    /// <summary>Gets the runs as (dy, first dx, last dx) triples relative to the origin.</summary>
    internal ReadOnlySpan<int> Runs => _runs ?? [0, 0, 0];

    /// <summary>A (2r + 1) square: ImageMagick's Square:r.</summary>
    /// <param name="radius">The radius in pixels, from 0 (the origin alone) to 1000.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 1000.</exception>
    public static StructuringElement Square(int radius)
    {
        CheckRadius(radius, nameof(radius));
        return Rectangle(radius, radius);
    }

    /// <summary>A (2rx + 1) × (2ry + 1) rectangle centered on the origin: ImageMagick's Rectangle with those sizes, and Skia's dilate and erode radii.</summary>
    /// <param name="radiusX">The horizontal radius in pixels, from 0 to 1000.</param>
    /// <param name="radiusY">The vertical radius in pixels, from 0 to 1000.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException">A radius is negative or above 1000.</exception>
    public static StructuringElement Rectangle(int radiusX, int radiusY)
    {
        CheckRadius(radiusX, nameof(radiusX));
        CheckRadius(radiusY, nameof(radiusY));
        int[] runs = new int[((2 * radiusY) + 1) * 3];
        for (int dy = -radiusY, i = 0; dy <= radiusY; dy++, i += 3)
        {
            runs[i] = dy;
            runs[i + 1] = -radiusX;
            runs[i + 2] = radiusX;
        }

        return new((2 * radiusX) + 1, (2 * radiusY) + 1, new Point(radiusX, radiusY), runs, rectangle: true);
    }

    /// <summary>The cells whose centers lie within <paramref name="radius"/> of the origin's, x² + y² ≤ r²: ImageMagick's Disk:r.</summary>
    /// <param name="radius">The radius in pixels, from 0 to 1000.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 1000.</exception>
    public static StructuringElement Disk(int radius)
    {
        CheckRadius(radius, nameof(radius));
        return Symmetric(radius, dy => (int)Math.Sqrt(((long)radius * radius) - ((long)dy * dy)));
    }

    /// <summary>The cells with |x| + |y| ≤ r, a square standing on a corner: ImageMagick's Diamond:r.</summary>
    /// <param name="radius">The radius in pixels, from 0 to 1000.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 1000.</exception>
    public static StructuringElement Diamond(int radius)
    {
        CheckRadius(radius, nameof(radius));
        return Symmetric(radius, dy => radius - Math.Abs(dy));
    }

    /// <summary>A plus sign: the row and the column through the origin, each 2r + 1 long. ImageMagick calls it Plus:r.</summary>
    /// <param name="radius">The length of each arm in pixels, from 0 to 1000.</param>
    /// <returns>The shape.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="radius"/> is negative or above 1000.</exception>
    public static StructuringElement Cross(int radius)
    {
        CheckRadius(radius, nameof(radius));
        return Symmetric(radius, dy => dy == 0 ? radius : 0);
    }

    /// <summary>Any shape: the set cells of <paramref name="cells"/>, indexed [y, x], with <paramref name="origin"/> on the pixel being computed.</summary>
    /// <param name="cells">The cells, [row, column]; at least one must be set, and each side at most 2001.</param>
    /// <param name="origin">The origin's cell, (column, row); it need not be set.</param>
    /// <returns>The shape. It keeps its own copy of the cells.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="cells"/> is <see langword="null"/>.</exception>
    /// <exception cref="ArgumentException">No cell is set, or a side is 0 or above 2001.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="origin"/> lies outside the cells.</exception>
    public static StructuringElement FromMask(bool[,] cells, Point origin)
    {
        ArgumentNullException.ThrowIfNull(cells);
        int height = cells.GetLength(0), width = cells.GetLength(1);
        if (width == 0 || height == 0 || width > (2 * MaxRadius) + 1 || height > (2 * MaxRadius) + 1)
        {
            throw new ArgumentException($"The mask is {width} × {height}; each side must be 1 to {(2 * MaxRadius) + 1}.", nameof(cells));
        }

        if ((uint)origin.X >= (uint)width || (uint)origin.Y >= (uint)height)
        {
            throw new ArgumentOutOfRangeException(nameof(origin), origin, $"The origin must lie inside the {width} × {height} mask.");
        }

        List<int> runs = [];
        bool full = true;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                if (!cells[y, x])
                {
                    full = false;
                    continue;
                }

                int first = x;
                while (x + 1 < width && cells[y, x + 1])
                {
                    x++;
                }

                runs.AddRange([y - origin.Y, first - origin.X, x - origin.X]);
            }
        }

        if (runs.Count == 0)
        {
            throw new ArgumentException("At least one cell of the mask must be set.", nameof(cells));
        }

        return new(width, height, origin, [.. runs], full);
    }

    /// <summary>Gets how far the shape reaches from the origin, the larger of either side, across and down.</summary>
    internal (int X, int Y) Reach
    {
        get
        {
            int x = 0, y = 0;
            ReadOnlySpan<int> runs = Runs;
            for (int i = 0; i < runs.Length; i += 3)
            {
                y = Math.Max(y, Math.Abs(runs[i]));
                x = Math.Max(x, Math.Max(Math.Abs(runs[i + 1]), Math.Abs(runs[i + 2])));
            }

            return (x, y);
        }
    }

    private static StructuringElement Symmetric(int radius, Func<int, int> halfWidth)
    {
        int[] runs = new int[((2 * radius) + 1) * 3];
        for (int dy = -radius, i = 0; dy <= radius; dy++, i += 3)
        {
            int w = halfWidth(dy);
            runs[i] = dy;
            runs[i + 1] = -w;
            runs[i + 2] = w;
        }

        return new((2 * radius) + 1, (2 * radius) + 1, new Point(radius, radius), runs, rectangle: radius == 0);
    }

    private static void CheckRadius(int radius, string name)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(radius, name);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(radius, MaxRadius, name);
    }
}
