using System.Numerics;

using Egami.Drawing;
using Egami.GdiPlus;
using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>
/// Figures of lines and cubic Beziers, as GDI+ keeps them: points with <see cref="PathPointType"/> types. Lines, arcs, Beziers
/// and curves join the open figure; rectangles, ellipses, pies, polygons and closed curves are figures of their own.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="paths"/>
/// </example>
public sealed partial class GraphicsPath : MarshalByRefObject, ICloneable, IDisposable
{
    private const byte Start = (byte)PathPointType.Start;
    private const byte LineType = (byte)PathPointType.Line;
    private const byte Close = (byte)PathPointType.CloseSubpath;
    private const byte Marker = (byte)PathPointType.PathMarker;

    private readonly List<PointF> _points = [];
    private readonly List<byte> _types = [];
    private bool _newFigure = true;

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class, empty, filled even-odd.</summary>
    public GraphicsPath()
        : this(FillMode.Alternate)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class, empty.</summary>
    /// <param name="fillMode">How the path fills.</param>
    public GraphicsPath(FillMode fillMode)
    {
        FillMode = fillMode;
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class from points and their types.</summary>
    /// <param name="pts">The points.</param>
    /// <param name="types">Their <see cref="PathPointType"/> values.</param>
    public GraphicsPath(PointF[] pts, byte[] types)
        : this(pts, types, FillMode.Alternate)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class from points and their types.</summary>
    /// <param name="pts">The points.</param>
    /// <param name="types">Their <see cref="PathPointType"/> values.</param>
    /// <param name="fillMode">How the path fills.</param>
    public GraphicsPath(PointF[] pts, byte[] types, FillMode fillMode)
        : this((pts ?? throw new ArgumentNullException(nameof(pts))).AsSpan(), (types ?? throw new ArgumentNullException(nameof(types))).AsSpan(), fillMode)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class from points and their types.</summary>
    /// <param name="pts">The points.</param>
    /// <param name="types">Their <see cref="PathPointType"/> values.</param>
    /// <param name="fillMode">How the path fills.</param>
    /// <exception cref="ArgumentException">The two have different lengths.</exception>
    public GraphicsPath(ReadOnlySpan<PointF> pts, ReadOnlySpan<byte> types, FillMode fillMode = FillMode.Alternate)
    {
        if (pts.Length != types.Length)
        {
            throw GdiErrors.InvalidParameter();
        }

        FillMode = fillMode;
        _points.AddRange(pts);
        _types.AddRange(types);
        _newFigure = _types.Count == 0 || (_types[^1] & Close) != 0;
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class from points and their types.</summary>
    /// <param name="pts">The points.</param>
    /// <param name="types">Their <see cref="PathPointType"/> values.</param>
    public GraphicsPath(Point[] pts, byte[] types)
        : this(pts, types, FillMode.Alternate)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class from points and their types.</summary>
    /// <param name="pts">The points.</param>
    /// <param name="types">Their <see cref="PathPointType"/> values.</param>
    /// <param name="fillMode">How the path fills.</param>
    public GraphicsPath(Point[] pts, byte[] types, FillMode fillMode)
        : this((pts ?? throw new ArgumentNullException(nameof(pts))).AsSpan(), (types ?? throw new ArgumentNullException(nameof(types))).AsSpan(), fillMode)
    {
    }

    /// <summary>Initializes a new instance of the <see cref="GraphicsPath"/> class from points and their types.</summary>
    /// <param name="pts">The points.</param>
    /// <param name="types">Their <see cref="PathPointType"/> values.</param>
    /// <param name="fillMode">How the path fills.</param>
    public GraphicsPath(ReadOnlySpan<Point> pts, ReadOnlySpan<byte> types, FillMode fillMode = FillMode.Alternate)
        : this(ToF(pts), types, fillMode)
    {
    }

    /// <summary>Gets or sets how the path fills.</summary>
    public FillMode FillMode { get; set; }

    /// <summary>Gets the number of points.</summary>
    public int PointCount => _points.Count;

    /// <summary>Gets a copy of the points.</summary>
    public PointF[] PathPoints => [.. _points];

    /// <summary>Gets a copy of the point types.</summary>
    public byte[] PathTypes => [.. _types];

    /// <summary>Gets a copy of the points and their types.</summary>
    public PathData PathData => new() { Points = PathPoints, Types = PathTypes };

    /// <summary>Gets the points as Egami vectors.</summary>
    internal ReadOnlySpan<PointF> Points => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_points);

    /// <summary>Gets the types.</summary>
    internal ReadOnlySpan<byte> Types => System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_types);

    /// <summary>A copy.</summary>
    /// <returns>The copy.</returns>
    public object Clone()
    {
        GraphicsPath copy = new(FillMode);
        copy._points.AddRange(_points);
        copy._types.AddRange(_types);
        copy._newFigure = _newFigure;
        return copy;
    }

    /// <summary>Releases the path.</summary>
    public void Dispose()
    {
    }

    /// <summary>Empties the path and sets it to fill even-odd.</summary>
    public void Reset()
    {
        _points.Clear();
        _types.Clear();
        _newFigure = true;
        FillMode = FillMode.Alternate;
    }

    /// <summary>Copies the points into a span.</summary>
    /// <param name="destination">Where to copy them.</param>
    /// <returns>The number copied.</returns>
    /// <exception cref="OutOfMemoryException">The span is shorter than the path, as GDI+ reports it.</exception>
    public int GetPathPoints(Span<PointF> destination)
    {
        if (destination.Length < _points.Count)
        {
            throw GdiErrors.OutOfMemory();
        }

        Points.CopyTo(destination);
        return _points.Count;
    }

    /// <summary>Copies the point types into a span.</summary>
    /// <param name="destination">Where to copy them.</param>
    /// <returns>The number copied.</returns>
    /// <exception cref="OutOfMemoryException">The span is shorter than the path, as GDI+ reports it.</exception>
    public int GetPathTypes(Span<byte> destination)
    {
        if (destination.Length < _types.Count)
        {
            throw GdiErrors.OutOfMemory();
        }

        Types.CopyTo(destination);
        return _types.Count;
    }

    /// <summary>The last point, or (0, 0) for an empty path.</summary>
    /// <returns>The point.</returns>
    public PointF GetLastPoint() => _points.Count == 0 ? PointF.Empty : _points[^1];

    /// <summary>Starts a new figure without closing the open one.</summary>
    public void StartFigure() => _newFigure = true;

    /// <summary>Closes the open figure: a line back to its first point.</summary>
    public void CloseFigure()
    {
        if (_types.Count > 0)
        {
            _types[^1] |= Close;
        }

        _newFigure = true;
    }

    /// <summary>Closes every figure.</summary>
    public void CloseAllFigures()
    {
        for (int i = 0; i < _types.Count; i++)
        {
            if (i + 1 == _types.Count || (_types[i + 1] & 0x07) == Start)
            {
                _types[i] |= Close;
            }
        }

        _newFigure = true;
    }

    /// <summary>Puts a marker after the last point, for <see cref="GraphicsPathIterator.NextMarker(out int, out int)"/>.</summary>
    public void SetMarkers()
    {
        if (_types.Count > 0)
        {
            _types[^1] |= Marker;
        }
    }

    /// <summary>Removes every marker.</summary>
    public void ClearMarkers()
    {
        for (int i = 0; i < _types.Count; i++)
        {
            _types[i] &= unchecked((byte)~Marker);
        }
    }

    /// <summary>Adds another path's figures, the first joined to the open figure when <paramref name="connect"/> is set.</summary>
    /// <param name="addingPath">The path to add.</param>
    /// <param name="connect">Whether its first figure continues the open one.</param>
    public void AddPath(GraphicsPath addingPath, bool connect)
    {
        ArgumentNullException.ThrowIfNull(addingPath);
        if (addingPath._points.Count == 0)
        {
            return;
        }

        int at = _points.Count;
        bool join = connect && !_newFigure && at > 0;
        _points.AddRange(addingPath._points);
        _types.AddRange(addingPath._types);
        if (join)
        {
            _types[at] = (byte)((_types[at] & ~0x07) | LineType);
        }

        _newFigure = (_types[^1] & Close) != 0;
    }

    /// <summary>Transforms every point.</summary>
    /// <param name="matrix">The transform.</param>
    public void Transform(Matrix matrix)
    {
        ArgumentNullException.ThrowIfNull(matrix);
        Matrix.Points(Points, matrix.MatrixElements);
    }

    /// <summary>Reverses the order of the points, and of the figures, keeping each segment's kind, the closes and the markers.</summary>
    public void Reverse()
    {
        int n = _points.Count;
        if (n == 0)
        {
            return;
        }

        PointF[] points = [.. _points];
        byte[] types = [.. _types];
        List<(int Start, int End)> figures = Figures(types);
        int to = 0;
        for (int f = figures.Count - 1; f >= 0; f--)
        {
            (int s, int e) = figures[f];
            int k = e - s;
            for (int j = 0; j <= k; j++)
            {
                _points[to + j] = points[e - j];
                byte type = j == 0 ? Start : (byte)(types[e - j + 1] & 0x07);
                // A marker after original point i lies after reversed point k - i - 1.
                if (j < k && (types[e - j - 1] & Marker) != 0)
                {
                    type |= Marker;
                }

                _types[to + j] = type;
            }

            if ((types[e] & Close) != 0)
            {
                _types[to + k] |= Close;
            }

            to += k + 1;
        }
    }

    /// <summary>The path's figures as GDI+ fills them.</summary>
    internal Path ToPath() => ToPath(Matrix3x2.Identity);

    /// <summary>The path's figures, transformed, as an Egami path.</summary>
    internal Path ToPath(Matrix3x2 m)
    {
        if (_points.Count == 0)
        {
            return Path.Empty;
        }

        Vector2[] v = new Vector2[_points.Count];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = Vector2.Transform(new Vector2(_points[i].X, _points[i].Y), m);
        }

        return GdiPlusData.BuildPath(v, Types, FillMode == FillMode.Winding ? FillRule.NonZero : FillRule.EvenOdd);
    }

    /// <summary>The start and end of each figure.</summary>
    internal static List<(int Start, int End)> Figures(ReadOnlySpan<byte> types)
    {
        List<(int, int)> figures = [];
        int start = 0;
        for (int i = 0; i < types.Length; i++)
        {
            if (i > start && (types[i] & 0x07) == Start)
            {
                figures.Add((start, i - 1));
                start = i;
            }
        }

        if (types.Length > 0)
        {
            figures.Add((start, types.Length - 1));
        }

        return figures;
    }

    /// <summary>Appends built points: an open shape joins the open figure (skipping a repeated point), a closed one starts its own.</summary>
    private void Append(GdiShapes shape, bool closed)
    {
        ReadOnlySpan<Vector2> p = shape.Points;
        ReadOnlySpan<byte> t = shape.Types;
        if (p.Length == 0)
        {
            return;
        }

        bool join = !closed && !_newFigure && _points.Count > 0;
        for (int i = 0; i < p.Length; i++)
        {
            PointF q = new(p[i].X, p[i].Y);
            byte type = t[i];
            if (i == 0 && join)
            {
                if (q == _points[^1])
                {
                    continue;
                }

                type = (byte)((type & ~0x07) | LineType);
            }

            _points.Add(q);
            _types.Add(type);
        }

        _newFigure = closed;
    }

    private static PointF[] ToF(ReadOnlySpan<Point> pts)
    {
        PointF[] f = new PointF[pts.Length];
        for (int i = 0; i < f.Length; i++)
        {
            f[i] = pts[i];
        }

        return f;
    }

    private static Vector2[] V(ReadOnlySpan<PointF> pts)
    {
        Vector2[] v = new Vector2[pts.Length];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = new Vector2(pts[i].X, pts[i].Y);
        }

        return v;
    }

    private static Vector2[] V(ReadOnlySpan<Point> pts)
    {
        Vector2[] v = new Vector2[pts.Length];
        for (int i = 0; i < v.Length; i++)
        {
            v[i] = new Vector2(pts[i].X, pts[i].Y);
        }

        return v;
    }
}

/// <summary>A path's points and their <see cref="PathPointType"/> types.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="paths"/>
/// </example>
public sealed class PathData
{
    /// <summary>Gets or sets the points.</summary>
#pragma warning disable CA1819 // System.Drawing's PathData holds arrays.
    public PointF[]? Points { get; set; }

    /// <summary>Gets or sets the point types.</summary>
    public byte[]? Types { get; set; }
#pragma warning restore CA1819
}
