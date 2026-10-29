using Egami.SystemDrawing.Internal;

namespace Egami.SystemDrawing.Drawing2D;

/// <summary>
/// Walks a <see cref="GraphicsPath"/> by figure, by run of one point type within the current figure, or by marker, as GDI+ walks
/// it. The iterator copies the path when it is made.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/SystemDrawingPackage.cs" region="paths"/>
/// </example>
public sealed class GraphicsPathIterator : MarshalByRefObject, IDisposable
{
    private readonly PointF[] _points;
    private readonly byte[] _types;
    private readonly List<(int Start, int End)> _figures;
    private int _figure;
    private int _marker;
    private int _markerStart;
    private int _markerEnd;
    private (int Start, int End)? _current;
    private int _typeAt;

    /// <summary>Initializes a new instance of the <see cref="GraphicsPathIterator"/> class.</summary>
    /// <param name="path">The path, or null for an empty iterator.</param>
    public GraphicsPathIterator(GraphicsPath? path)
    {
        _points = path?.PathPoints ?? [];
        _types = path?.PathTypes ?? [];
        _figures = GraphicsPath.Figures(_types);
    }

    /// <summary>Gets the number of points.</summary>
    public int Count => _points.Length;

    /// <summary>Gets the number of figures.</summary>
    public int SubpathCount => _figures.Count;

    /// <summary>Whether the path has a Bezier.</summary>
    /// <returns><see langword="true"/> when it has one.</returns>
    public bool HasCurve() => Array.Exists(_types, t => (t & 0x07) == (byte)PathPointType.Bezier);

    /// <summary>Releases the iterator.</summary>
    public void Dispose()
    {
    }

    /// <summary>Starts again from the first figure and marker.</summary>
    public void Rewind()
    {
        _figure = 0;
        _marker = 0;
        _markerStart = _markerEnd = 0;
        _current = null;
        _typeAt = 0;
    }

    /// <summary>The next figure.</summary>
    /// <param name="startIndex">Its first point.</param>
    /// <param name="endIndex">Its last point.</param>
    /// <param name="isClosed">Whether it is closed; true past the end, as in GDI+.</param>
    /// <returns>Its number of points, or 0 past the end.</returns>
    public int NextSubpath(out int startIndex, out int endIndex, out bool isClosed)
    {
        if (_figure >= _figures.Count)
        {
            startIndex = endIndex = 0;
            isClosed = true;
            _current = null;
            return 0;
        }

        (startIndex, endIndex) = _figures[_figure++];
        isClosed = (_types[endIndex] & (byte)PathPointType.CloseSubpath) != 0;
        _current = (startIndex, endIndex);
        _typeAt = startIndex;
        return endIndex - startIndex + 1;
    }

    /// <summary>The next figure, copied into a path.</summary>
    /// <param name="path">The path that receives it, replacing what it held.</param>
    /// <param name="isClosed">Whether it is closed.</param>
    /// <returns>Its number of points, or 0 past the end.</returns>
    public int NextSubpath(GraphicsPath path, out bool isClosed)
    {
        ArgumentNullException.ThrowIfNull(path);
        int n = NextSubpath(out int s, out int e, out isClosed);
        Fill(path, s, e, n);
        return n;
    }

    /// <summary>The next run of points of one type within the figure <see cref="NextSubpath(out int, out int, out bool)"/> gave last.</summary>
    /// <param name="pathType">The type of the run.</param>
    /// <param name="startIndex">Its first point (the point it starts from).</param>
    /// <param name="endIndex">Its last point.</param>
    /// <returns>Its number of points, or 0 when the figure is done or none was given.</returns>
    public int NextPathType(out byte pathType, out int startIndex, out int endIndex)
    {
        pathType = 0;
        startIndex = endIndex = 0;
        if (_current is not (int s, int e) || _typeAt >= e)
        {
            return 0;
        }

        int from = _typeAt;
        byte type = (byte)(_types[from + 1] & 0x07);
        int to = from + 1;
        while (to + 1 <= e && (_types[to + 1] & 0x07) == type)
        {
            to++;
        }

        _ = s;
        pathType = type;
        startIndex = from;
        endIndex = to;
        _typeAt = to;
        return to - from + 1;
    }

    /// <summary>The points up to the next marker.</summary>
    /// <param name="startIndex">The first point.</param>
    /// <param name="endIndex">The last point, the one the marker follows.</param>
    /// <returns>The number of points, or 0 past the end (the last range stays in the outputs).</returns>
    public int NextMarker(out int startIndex, out int endIndex)
    {
        if (_marker >= _points.Length)
        {
            startIndex = _markerStart;
            endIndex = _markerEnd;
            return 0;
        }

        int from = _marker, to = from;
        while (to < _points.Length - 1 && (_types[to] & (byte)PathPointType.PathMarker) == 0)
        {
            to++;
        }

        _marker = to + 1;
        (_markerStart, _markerEnd) = (from, to);
        startIndex = from;
        endIndex = to;
        return to - from + 1;
    }

    /// <summary>The points up to the next marker, copied into a path.</summary>
    /// <param name="path">The path that receives them, replacing what it held.</param>
    /// <returns>The number of points, or 0 past the end.</returns>
    public int NextMarker(GraphicsPath path)
    {
        ArgumentNullException.ThrowIfNull(path);
        int n = NextMarker(out int s, out int e);
        Fill(path, s, e, n);
        return n;
    }

    /// <summary>Copies every point and type.</summary>
    /// <param name="points">The points; must hold the path.</param>
    /// <param name="types">The types; must hold the path.</param>
    /// <returns>The number copied.</returns>
    public int Enumerate(ref PointF[] points, ref byte[] types)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(types);
        return Enumerate(points.AsSpan(), types.AsSpan());
    }

    /// <summary>Copies every point and type.</summary>
    /// <param name="points">The points; must hold the path.</param>
    /// <param name="types">The types; must hold the path.</param>
    /// <returns>The number copied.</returns>
    /// <exception cref="ArgumentException">The two have different lengths.</exception>
    public int Enumerate(Span<PointF> points, Span<byte> types)
    {
        if (points.Length != types.Length)
        {
            throw GdiErrors.InvalidParameter();
        }

        int n = Math.Min(points.Length, _points.Length);
        _points.AsSpan(0, n).CopyTo(points);
        _types.AsSpan(0, n).CopyTo(types);
        return n;
    }

    /// <summary>Copies a range of points and types.</summary>
    /// <param name="points">Where the points go.</param>
    /// <param name="types">Where the types go.</param>
    /// <param name="startIndex">The first point.</param>
    /// <param name="endIndex">The last point.</param>
    /// <returns>The number copied.</returns>
    public int CopyData(ref PointF[] points, ref byte[] types, int startIndex, int endIndex)
    {
        ArgumentNullException.ThrowIfNull(points);
        ArgumentNullException.ThrowIfNull(types);
        return CopyData(points.AsSpan(), types.AsSpan(), startIndex, endIndex);
    }

    /// <summary>Copies a range of points and types.</summary>
    /// <param name="points">Where the points go.</param>
    /// <param name="types">Where the types go.</param>
    /// <param name="startIndex">The first point.</param>
    /// <param name="endIndex">The last point.</param>
    /// <returns>The number copied.</returns>
    /// <exception cref="ArgumentException">The range is out of order or out of the path, or the spans are too short.</exception>
    public int CopyData(Span<PointF> points, Span<byte> types, int startIndex, int endIndex)
    {
        int n = endIndex - startIndex + 1;
        if (startIndex < 0 || endIndex >= _points.Length || n <= 0 || points.Length < n || types.Length < n)
        {
            throw GdiErrors.InvalidParameter();
        }

        _points.AsSpan(startIndex, n).CopyTo(points);
        _types.AsSpan(startIndex, n).CopyTo(types);
        return n;
    }

    private void Fill(GraphicsPath path, int start, int end, int count)
    {
        FillMode mode = path.FillMode;
        path.Reset();
        path.FillMode = mode;
        if (count == 0)
        {
            return;
        }

        using GraphicsPath part = new(_points.AsSpan(start, end - start + 1), _types.AsSpan(start, end - start + 1), mode);
        path.AddPath(part, false);
    }
}
