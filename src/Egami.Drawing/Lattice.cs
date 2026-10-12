using System.Numerics;

using Egami.Pixels;

namespace Egami.Drawing;

/// <summary>What a cell of a <see cref="Lattice"/> draws.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
/// </example>
public enum LatticeCellKind
{
    /// <summary>The cell's part of the image.</summary>
    Default = 0,

    /// <summary>Nothing: the cell is left as it is.</summary>
    Transparent = 1,

    /// <summary>The cell's color in <see cref="Lattice.CellColors"/>, filling the cell.</summary>
    FixedColor = 2,
}

/// <summary>
/// A grid over an image for <see cref="Canvas.DrawImageLattice"/>, as Skia's lattice: the x and y divisions cut the image into
/// columns and rows that alternate fixed and stretchable, starting with fixed, so a division at the image's left or top edge makes
/// the first column or row stretchable. Fixed columns and rows keep their size when the destination is large enough and shrink in
/// proportion when it is not; stretchable ones share what is left. A nine-patch is the lattice with two divisions each way.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
/// </example>
public sealed record class Lattice
{
    private readonly int[] _xDivs;
    private readonly int[] _yDivs;
    private readonly Rect? _bounds;
    private readonly LatticeCellKind[]? _kinds;
    private readonly RgbaF32[]? _colors;

    /// <summary>Initializes a new instance of the <see cref="Lattice"/> class.</summary>
    /// <param name="xDivs">The x coordinates, in image pixels, between columns: increasing, inside the bounds' width.</param>
    /// <param name="yDivs">The y coordinates between rows.</param>
    /// <exception cref="ArgumentException">The divisions are not increasing or a division is negative.</exception>
    public Lattice(ReadOnlySpan<int> xDivs, ReadOnlySpan<int> yDivs)
    {
        _xDivs = Divisions(xDivs, nameof(xDivs));
        _yDivs = Divisions(yDivs, nameof(yDivs));
    }

    /// <summary>Gets the x divisions.</summary>
    public IReadOnlyList<int> XDivs => Array.AsReadOnly(_xDivs);

    /// <summary>Gets the y divisions.</summary>
    public IReadOnlyList<int> YDivs => Array.AsReadOnly(_yDivs);

    /// <summary>Gets the part of the image the lattice covers, in its pixels; the whole image when <see langword="null"/>. Divisions are relative to the image, not to the bounds.</summary>
    /// <exception cref="ArgumentException">The bounds are empty.</exception>
    public Rect? Bounds
    {
        get => _bounds;
        init => _bounds = value is Rect r && r.IsEmpty ? throw new ArgumentException("The bounds must not be empty.", nameof(value)) : value;
    }

    /// <summary>
    /// Gets what each cell draws, row by row from the top, (XDivs.Count + 1) · (YDivs.Count + 1) of them; every cell draws its
    /// part of the image when <see langword="null"/>.
    /// </summary>
    public IReadOnlyList<LatticeCellKind>? CellKinds
    {
        get => _kinds is null ? null : Array.AsReadOnly(_kinds);
        init
        {
            if (value is not null && value.Any(k => !Enum.IsDefined(k)))
            {
                throw new ArgumentOutOfRangeException(nameof(value), "A cell kind is not defined.");
            }

            _kinds = value?.ToArray();
        }
    }

    /// <summary>Gets the colors of <see cref="LatticeCellKind.FixedColor"/> cells, one per cell in the order of <see cref="CellKinds"/>.</summary>
    public IReadOnlyList<RgbaF32>? CellColors
    {
        get => _colors is null ? null : Array.AsReadOnly(_colors);
        init => _colors = value?.ToArray();
    }

    /// <summary>Gets the number of cells: (XDivs.Count + 1) · (YDivs.Count + 1).</summary>
    public int CellCount => (_xDivs.Length + 1) * (_yDivs.Length + 1);

    /// <summary>Creates the lattice of a nine-patch: the center stretches both ways, the edges one way, and the corners keep their size.</summary>
    /// <param name="center">The stretchable center, in image pixels.</param>
    /// <returns>The lattice.</returns>
    /// <exception cref="ArgumentException"><paramref name="center"/> is empty or negative.</exception>
    public static Lattice NinePatch(Rect center)
    {
        if (center.IsEmpty || center.X < 0 || center.Y < 0)
        {
            throw new ArgumentException("The center must not be empty or negative.", nameof(center));
        }

        return new Lattice([center.X, center.Right], [center.Y, center.Bottom]);
    }

    /// <inheritdoc/>
    public bool Equals(Lattice? other)
        => other is not null && _xDivs.AsSpan().SequenceEqual(other._xDivs) && _yDivs.AsSpan().SequenceEqual(other._yDivs) && _bounds == other._bounds
            && Same(_kinds, other._kinds) && Same(_colors, other._colors);

    /// <inheritdoc/>
    public override int GetHashCode()
    {
        HashCode hash = default;
        foreach (int x in _xDivs)
        {
            hash.Add(x);
        }

        foreach (int y in _yDivs)
        {
            hash.Add(y);
        }

        hash.Add(_bounds);
        return hash.ToHashCode();
    }

    /// <summary>The kind of cell (column, row), checked against the colors.</summary>
    internal LatticeCellKind Kind(int column, int row) => _kinds is null ? LatticeCellKind.Default : _kinds[(row * (_xDivs.Length + 1)) + column];

    internal RgbaF32 Color(int column, int row) => _colors is null ? default : _colors[(row * (_xDivs.Length + 1)) + column];

    /// <summary>Checks the lattice against an image and returns the source bounds.</summary>
    internal Rect Check(Raster image, string name)
    {
        Rect bounds = _bounds ?? new Rect(0, 0, image.Width, image.Height);
        if (bounds.X < 0 || bounds.Y < 0 || bounds.Right > image.Width || bounds.Bottom > image.Height)
        {
            throw new ArgumentException("The lattice's bounds lie outside the image.", name);
        }

        foreach (int x in _xDivs)
        {
            if (x < bounds.X || x > bounds.Right)
            {
                throw new ArgumentException("An x division lies outside the lattice's bounds.", name);
            }
        }

        foreach (int y in _yDivs)
        {
            if (y < bounds.Y || y > bounds.Bottom)
            {
                throw new ArgumentException("A y division lies outside the lattice's bounds.", name);
            }
        }

        if (_kinds is not null && _kinds.Length != CellCount)
        {
            throw new ArgumentException($"The lattice has {CellCount} cells but {_kinds.Length} cell kinds.", name);
        }

        if (_colors is not null && _colors.Length != CellCount)
        {
            throw new ArgumentException($"The lattice has {CellCount} cells but {_colors.Length} cell colors.", name);
        }

        return bounds;
    }

    /// <summary>
    /// Lays one axis out: the source edges of each column or row and where they go in the destination. Fixed spans keep their
    /// size if the destination holds them all, else shrink in proportion and stretchable spans vanish; with no stretchable span,
    /// the fixed ones scale to fit.
    /// </summary>
    internal static (float[] Source, float[] Destination) Layout(ReadOnlySpan<int> divs, int start, int end, float destinationStart, float destinationEnd)
    {
        float[] source = new float[divs.Length + 2];
        source[0] = start;
        for (int i = 0; i < divs.Length; i++)
        {
            source[i + 1] = divs[i];
        }

        source[^1] = end;
        float fixedLength = 0, stretchLength = 0;
        for (int i = 0; i + 1 < source.Length; i++)
        {
            float length = source[i + 1] - source[i];
            if (i % 2 == 0)
            {
                fixedLength += length;
            }
            else
            {
                stretchLength += length;
            }
        }

        float available = destinationEnd - destinationStart;
        float fixedScale, stretchScale;
        if (stretchLength <= 0)
        {
            fixedScale = fixedLength > 0 ? available / fixedLength : 0;
            stretchScale = 0;
        }
        else if (fixedLength <= available)
        {
            fixedScale = 1;
            stretchScale = (available - fixedLength) / stretchLength;
        }
        else
        {
            fixedScale = available / fixedLength;
            stretchScale = 0;
        }

        float[] destination = new float[source.Length];
        destination[0] = destinationStart;
        for (int i = 0; i + 1 < source.Length; i++)
        {
            destination[i + 1] = destination[i] + ((source[i + 1] - source[i]) * (i % 2 == 0 ? fixedScale : stretchScale));
        }

        destination[^1] = destinationEnd;
        return (source, destination);
    }

    private static int[] Divisions(ReadOnlySpan<int> divs, string name)
    {
        for (int i = 0; i < divs.Length; i++)
        {
            if (divs[i] < 0 || (i > 0 && divs[i] <= divs[i - 1]))
            {
                throw new ArgumentException("The divisions must be increasing and not negative.", name);
            }
        }

        return divs.ToArray();
    }

    private static bool Same<T>(T[]? a, T[]? b) => a is null ? b is null : b is not null && a.AsSpan().SequenceEqual(b);
}

/// <summary>An image with its <see cref="Lattice"/>, as Android's nine-patch (.9.png) files hold them.</summary>
/// <param name="Image">The image without its marker border.</param>
/// <param name="Lattice">The stretchable columns and rows the markers set.</param>
/// <param name="Padding">The content area the right and bottom markers set, in the image's pixels, or <see langword="null"/> when the file has none.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
/// </example>
public sealed record class NinePatch(Raster Image, Lattice Lattice, Rect? Padding)
{
    /// <summary>
    /// Reads an Android nine-patch: the image has a one-pixel border whose opaque black pixels mark, on the top edge, the
    /// stretchable columns, on the left edge the stretchable rows, and on the right and bottom edges the content area. Other border
    /// pixels must be fully transparent, as Android requires.
    /// </summary>
    /// <param name="raster">The whole .9.png image, border included; its first frame is read.</param>
    /// <returns>The image inside the border, in the raster's pixel type, with its lattice and padding.</returns>
    /// <exception cref="ArgumentException">The image is smaller than 3×3, or a border pixel is neither opaque black nor transparent.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="lattice"/>
    /// </example>
    public static NinePatch FromAndroid(Raster raster)
    {
        ArgumentNullException.ThrowIfNull(raster);
        int w = raster.Width, h = raster.Height;
        if (w < 3 || h < 3)
        {
            throw new ArgumentException("A nine-patch is at least 3×3 pixels: a one-pixel border around the image.", nameof(raster));
        }

        using Raster<Rgba8> pixels = raster.As<Rgba8>();
        bool[] top = Markers(pixels, w - 2, i => pixels.GetPixel(i + 1, 0));
        bool[] left = Markers(pixels, h - 2, i => pixels.GetPixel(0, i + 1));
        bool[] bottom = Markers(pixels, w - 2, i => pixels.GetPixel(i + 1, h - 1));
        bool[] right = Markers(pixels, h - 2, i => pixels.GetPixel(w - 1, i + 1));

        Rect? padding = null;
        (int px0, int px1) = Span(bottom);
        (int py0, int py1) = Span(right);
        if (px1 > px0 || py1 > py0)
        {
            int l = px1 > px0 ? px0 : 0, t = py1 > py0 ? py0 : 0, r = px1 > px0 ? px1 : w - 2, b = py1 > py0 ? py1 : h - 2;
            padding = new Rect(l, t, r - l, b - t);
        }

        Raster inner = raster.Derive(ops => ops.Crop(1, 1, w - 2, h - 2));
        return new NinePatch(inner, new Lattice(Edges(top), Edges(left)), padding);
    }

    private static bool[] Markers(Raster<Rgba8> pixels, int length, Func<int, Rgba8> read)
    {
        bool[] marked = new bool[length];
        for (int i = 0; i < length; i++)
        {
            Rgba8 p = read(i);
            if (p == new Rgba8(0, 0, 0, 255))
            {
                marked[i] = true;
            }
            else if (p.A != 0)
            {
                throw new ArgumentException($"A nine-patch border pixel is {p}, neither opaque black nor transparent.", nameof(pixels));
            }
        }

        return marked;
    }

    /// <summary>The divisions that make each marked run stretchable: its start and end.</summary>
    private static int[] Edges(bool[] marked)
    {
        List<int> divs = [];
        for (int i = 0; i < marked.Length; i++)
        {
            if (marked[i] && (i == 0 || !marked[i - 1]))
            {
                divs.Add(i);
            }

            if (marked[i] && (i == marked.Length - 1 || !marked[i + 1]))
            {
                divs.Add(i + 1);
            }
        }

        return [.. divs];
    }

    /// <summary>The first and last marked pixels, as a half-open span, or (0, 0).</summary>
    private static (int Start, int End) Span(bool[] marked)
    {
        int first = Array.IndexOf(marked, true), last = Array.LastIndexOf(marked, true);
        return first < 0 ? (0, 0) : (first, last + 1);
    }
}

/// <summary>
/// A sprite's placement in <see cref="Canvas.DrawAtlas"/>, as Skia's RSXform: a rotation with a uniform scale, then a move. A
/// point (x, y) of the sprite, measured from its source rectangle's top-left corner, goes to
/// (ScaleCos·x − ScaleSin·y + Tx, ScaleSin·x + ScaleCos·y + Ty).
/// </summary>
/// <param name="ScaleCos">The scale times the cosine of the angle.</param>
/// <param name="ScaleSin">The scale times the sine of the angle.</param>
/// <param name="Tx">The horizontal move.</param>
/// <param name="Ty">The vertical move.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Advanced.cs" region="atlas"/>
/// </example>
public readonly record struct SpriteTransform(float ScaleCos, float ScaleSin, float Tx, float Ty)
{
    /// <summary>Creates a placement that scales and turns the sprite about an anchor and puts the anchor at a position.</summary>
    /// <param name="scale">The scale.</param>
    /// <param name="degrees">The clockwise turn on a y-down screen, as <see cref="Canvas.Rotate(float)"/>.</param>
    /// <param name="position">Where the anchor goes, in local coordinates.</param>
    /// <param name="anchor">The point of the sprite that stays put, from its source rectangle's top-left corner.</param>
    /// <returns>The placement.</returns>
    public static SpriteTransform FromRotationScale(float scale, float degrees, Vector2 position, Vector2 anchor = default)
    {
        float radians = degrees * MathF.PI / 180;
        float c = scale * MathF.Cos(radians), s = scale * MathF.Sin(radians);
        return new SpriteTransform(c, s, position.X - (c * anchor.X) + (s * anchor.Y), position.Y - (s * anchor.X) - (c * anchor.Y));
    }

    /// <summary>Returns the placement as a matrix from sprite coordinates to local coordinates.</summary>
    /// <returns>The matrix.</returns>
    public Matrix3x2 ToMatrix() => new(ScaleCos, ScaleSin, -ScaleSin, ScaleCos, Tx, Ty);

    internal bool IsFinite => float.IsFinite(ScaleCos) && float.IsFinite(ScaleSin) && float.IsFinite(Tx) && float.IsFinite(Ty);
}
