namespace Egami.Fonts.Parsing;

/// <summary>
/// Records an outline so a glyph that fails halfway sends nothing, then replays it to a sink. One per thread, reused.
/// </summary>
internal sealed class OutlineRecorder : IGlyphOutlineSink
{
    [ThreadStatic]
    private static OutlineRecorder? t_shared;

    private OutlineVerb[] _verbs = new OutlineVerb[64];
    private float[] _coordinates = new float[256];
    private int _verbCount;
    private int _coordinateCount;
    private float _startX;
    private float _startY;
    private float _lastX;
    private float _lastY;
    private bool _open;

    public int VerbCount => _verbCount;

    public ReadOnlySpan<OutlineVerb> Verbs => _verbs.AsSpan(0, _verbCount);

    public ReadOnlySpan<float> Coordinates => _coordinates.AsSpan(0, _coordinateCount);

    private bool _inUse;

    /// <summary>Takes the calling thread's recorder, cleared, or a new one when it is in use; hand it back with <see cref="Release"/>.</summary>
    public static OutlineRecorder Take()
    {
        OutlineRecorder recorder = t_shared ??= new OutlineRecorder();
        if (recorder._inUse)
        {
            recorder = new OutlineRecorder();
        }

        recorder._inUse = true;
        recorder.Clear();
        return recorder;
    }

    public void Release() => _inUse = false;

    public void Clear()
    {
        _verbCount = 0;
        _coordinateCount = 0;
        _open = false;
        _startX = _startY = _lastX = _lastY = 0;
    }

    public void MoveTo(float x, float y)
    {
        if (_open)
        {
            Close();
        }

        Add(OutlineVerb.Move, x, y);
        _startX = _lastX = x;
        _startY = _lastY = y;
        _open = true;
    }

    public void LineTo(float x, float y)
    {
        EnsureOpen();
        Add(OutlineVerb.Line, x, y);
        _lastX = x;
        _lastY = y;
    }

    public void QuadTo(float cx, float cy, float x, float y)
    {
        EnsureOpen();
        Add(OutlineVerb.Quad, cx, cy, x, y);
        _lastX = x;
        _lastY = y;
    }

    public void CubicTo(float c1x, float c1y, float c2x, float c2y, float x, float y)
    {
        EnsureOpen();
        Add(OutlineVerb.Cubic, c1x, c1y, c2x, c2y, x, y);
        _lastX = x;
        _lastY = y;
    }

    /// <summary>Closes the contour, first dropping a final line that only returns to the start point.</summary>
    public void Close()
    {
        if (!_open)
        {
            return;
        }

        if (_verbCount > 0 && _verbs[_verbCount - 1] == OutlineVerb.Line && _lastX == _startX && _lastY == _startY)
        {
            _verbCount--;
            _coordinateCount -= 2;
        }

        AddVerb(OutlineVerb.Close);
        _open = false;
    }

    /// <summary>Closes an open contour, as CFF's endchar and a TrueType contour's end do.</summary>
    public void Finish()
    {
        if (_open)
        {
            Close();
        }
    }

    public void ReplayTo(IGlyphOutlineSink sink)
    {
        int c = 0;
        for (int i = 0; i < _verbCount; i++)
        {
            switch (_verbs[i])
            {
                case OutlineVerb.Move:
                    sink.MoveTo(_coordinates[c], _coordinates[c + 1]);
                    c += 2;
                    break;
                case OutlineVerb.Line:
                    sink.LineTo(_coordinates[c], _coordinates[c + 1]);
                    c += 2;
                    break;
                case OutlineVerb.Quad:
                    sink.QuadTo(_coordinates[c], _coordinates[c + 1], _coordinates[c + 2], _coordinates[c + 3]);
                    c += 4;
                    break;
                case OutlineVerb.Cubic:
                    sink.CubicTo(_coordinates[c], _coordinates[c + 1], _coordinates[c + 2], _coordinates[c + 3], _coordinates[c + 4], _coordinates[c + 5]);
                    c += 6;
                    break;
                default:
                    sink.Close();
                    break;
            }
        }
    }

    /// <summary>Returns the exact bounds of the recorded outline, solving each curve's extremes; empty when nothing was drawn.</summary>
    public GlyphExtents ComputeBounds()
    {
        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        float x0 = 0, y0 = 0;
        int c = 0;
        bool any = false;
        for (int i = 0; i < _verbCount; i++)
        {
            switch (_verbs[i])
            {
                case OutlineVerb.Move:
                case OutlineVerb.Line:
                    x0 = _coordinates[c];
                    y0 = _coordinates[c + 1];
                    Include(x0, y0);
                    c += 2;
                    break;
                case OutlineVerb.Quad:
                    {
                        float cx = _coordinates[c], cy = _coordinates[c + 1], x = _coordinates[c + 2], y = _coordinates[c + 3];
                        Include(x, y);
                        QuadExtreme(x0, cx, x, out float tx);
                        QuadExtreme(y0, cy, y, out float ty);
                        if (tx > 0 && tx < 1)
                        {
                            Include(Quad(x0, cx, x, tx), Quad(y0, cy, y, tx));
                        }

                        if (ty > 0 && ty < 1)
                        {
                            Include(Quad(x0, cx, x, ty), Quad(y0, cy, y, ty));
                        }

                        x0 = x;
                        y0 = y;
                        c += 4;
                        break;
                    }

                case OutlineVerb.Cubic:
                    {
                        float c1x = _coordinates[c], c1y = _coordinates[c + 1], c2x = _coordinates[c + 2], c2y = _coordinates[c + 3];
                        float x = _coordinates[c + 4], y = _coordinates[c + 5];
                        Include(x, y);
                        Span<float> roots = stackalloc float[4];
                        int n = CubicExtremes(x0, c1x, c2x, x, roots);
                        n += CubicExtremes(y0, c1y, c2y, y, roots[n..]);
                        for (int k = 0; k < n; k++)
                        {
                            float t = roots[k];
                            Include(Cubic(x0, c1x, c2x, x, t), Cubic(y0, c1y, c2y, y, t));
                        }

                        x0 = x;
                        y0 = y;
                        c += 6;
                        break;
                    }
            }
        }

        return any ? new GlyphExtents(minX, minY, maxX, maxY) : GlyphExtents.Empty;

        void Include(float x, float y)
        {
            any = true;
            minX = MathF.Min(minX, x);
            minY = MathF.Min(minY, y);
            maxX = MathF.Max(maxX, x);
            maxY = MathF.Max(maxY, y);
        }
    }

    private static void QuadExtreme(float p0, float p1, float p2, out float t)
    {
        float d = p0 - (2 * p1) + p2;
        t = d == 0 ? -1 : (p0 - p1) / d;
    }

    private static float Quad(float p0, float p1, float p2, float t)
    {
        float u = 1 - t;
        return (u * u * p0) + (2 * u * t * p1) + (t * t * p2);
    }

    private static float Cubic(float p0, float p1, float p2, float p3, float t)
    {
        float u = 1 - t;
        return (u * u * u * p0) + (3 * u * u * t * p1) + (3 * u * t * t * p2) + (t * t * t * p3);
    }

    // Roots in (0, 1) of the derivative of a cubic Bézier in one coordinate.
    private static int CubicExtremes(float p0, float p1, float p2, float p3, Span<float> roots)
    {
        double a = -p0 + (3.0 * p1) - (3.0 * p2) + p3;
        double b = 2.0 * (p0 - (2.0 * p1) + p2);
        double c = p1 - p0;
        int n = 0;
        if (Math.Abs(a) < 1e-12)
        {
            return Math.Abs(b) > 1e-12 ? AddRoot(roots, n, -c / b) : n;
        }

        double disc = (b * b) - (4 * a * c);
        if (disc < 0)
        {
            return 0;
        }

        double sq = Math.Sqrt(disc);
        n = AddRoot(roots, n, (-b + sq) / (2 * a));
        return AddRoot(roots, n, (-b - sq) / (2 * a));
    }

    private static int AddRoot(Span<float> roots, int n, double t)
    {
        if (t > 0 && t < 1)
        {
            roots[n++] = (float)t;
        }

        return n;
    }

    private void EnsureOpen()
    {
        if (!_open)
        {
            // A drawing operator without a moveto starts at the last point, as Type 2 charstrings allow.
            MoveTo(_lastX, _lastY);
        }
    }

    private void Add(OutlineVerb verb, float a, float b)
    {
        AddVerb(verb);
        EnsureCoordinates(2);
        _coordinates[_coordinateCount++] = a;
        _coordinates[_coordinateCount++] = b;
    }

    private void Add(OutlineVerb verb, float a, float b, float c, float d)
    {
        AddVerb(verb);
        EnsureCoordinates(4);
        _coordinates[_coordinateCount++] = a;
        _coordinates[_coordinateCount++] = b;
        _coordinates[_coordinateCount++] = c;
        _coordinates[_coordinateCount++] = d;
    }

    private void Add(OutlineVerb verb, float a, float b, float c, float d, float e, float f)
    {
        AddVerb(verb);
        EnsureCoordinates(6);
        _coordinates[_coordinateCount++] = a;
        _coordinates[_coordinateCount++] = b;
        _coordinates[_coordinateCount++] = c;
        _coordinates[_coordinateCount++] = d;
        _coordinates[_coordinateCount++] = e;
        _coordinates[_coordinateCount++] = f;
    }

    private void AddVerb(OutlineVerb verb)
    {
        if (_verbCount == _verbs.Length)
        {
            Array.Resize(ref _verbs, _verbs.Length * 2);
        }

        _verbs[_verbCount++] = verb;
    }

    private void EnsureCoordinates(int count)
    {
        if (_coordinateCount + count > _coordinates.Length)
        {
            Array.Resize(ref _coordinates, Math.Max(_coordinates.Length * 2, _coordinateCount + count));
        }
    }
}

internal enum OutlineVerb : byte
{
    Move,
    Line,
    Quad,
    Cubic,
    Close,
}
