using Egami.Fonts.Parsing;

namespace Egami.Fonts.Tables;

/// <summary>TrueType glyph data ('glyf' with 'loca'): simple and composite glyphs, decoded to points and then to curves.</summary>
internal sealed class GlyfTable
{
    /// <summary>The deepest nesting of composite glyphs followed; deeper ones are treated as damaged.</summary>
    public const int MaxCompositeDepth = 16;

    /// <summary>The most points one glyph may expand to, so nested composites cannot multiply without bound.</summary>
    public const int MaxPoints = 1 << 20;

    private const int OnCurve = 0x01;
    private const int XShort = 0x02;
    private const int YShort = 0x04;
    private const int Repeat = 0x08;
    private const int XSameOrPositive = 0x10;
    private const int YSameOrPositive = 0x20;

    private const int ArgsAreWords = 0x0001;
    private const int ArgsAreXY = 0x0002;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;
    private const int HaveInstructions = 0x0100;
    private const int ScaledOffset = 0x0800;
    private const int UnscaledOffset = 0x1000;

    private readonly ReadOnlyMemory<byte> _glyf;
    private readonly uint[] _loca;
    private readonly long _glyfOffset;

    public GlyfTable(ReadOnlyMemory<byte> glyf, uint[] loca, long glyfOffset)
    {
        _glyf = glyf;
        _loca = loca;
        _glyfOffset = glyfOffset;
    }

    public int GlyphCount => Math.Max(0, _loca.Length - 1);

    public ReadOnlyMemory<byte> Data => _glyf;

    /// <summary>Reads 'loca' for <paramref name="glyphCount"/> glyphs in the short or long format.</summary>
    public static uint[] ReadLoca(ReadOnlySpan<byte> loca, int glyphCount, bool longFormat, long offset)
    {
        int entries = glyphCount + 1;
        int available = longFormat ? loca.Length / 4 : loca.Length / 2;
        if (available < entries)
        {
            throw FontErrors.Corrupt(offset, $"The loca table holds {available} entries; {entries} are needed.");
        }

        var result = new uint[entries];
        for (int i = 0; i < entries; i++)
        {
            result[i] = longFormat ? BigEndian.UInt32(loca, i * 4) : (uint)BigEndian.UInt16(loca, i * 2) * 2;
        }

        return result;
    }

    /// <summary>Returns the bytes of one glyph; empty for a glyph with no outline.</summary>
    public ReadOnlySpan<byte> GetGlyphData(int glyph, out long fileOffset)
    {
        fileOffset = _glyfOffset;
        if ((uint)glyph >= (uint)GlyphCount)
        {
            return default;
        }

        uint start = _loca[glyph], end = _loca[glyph + 1];
        if (end <= start)
        {
            if (end < start)
            {
                throw FontErrors.Corrupt(_glyfOffset, $"The loca entries of glyph {glyph} run backwards.");
            }

            return default;
        }

        if (end > _glyf.Length)
        {
            throw FontErrors.Corrupt(_glyfOffset + start, $"Glyph {glyph} runs past the end of the glyf table.");
        }

        fileOffset = _glyfOffset + start;
        return _glyf.Span[(int)start..(int)end];
    }

    /// <summary>Gets the bounds stored in a glyph's header, or empty for a glyph with no outline.</summary>
    public GlyphExtents GetHeaderBounds(int glyph)
    {
        ReadOnlySpan<byte> data = GetGlyphData(glyph, out _);
        if (data.Length < 10)
        {
            return GlyphExtents.Empty;
        }

        return new GlyphExtents(BigEndian.Int16(data, 2), BigEndian.Int16(data, 4), BigEndian.Int16(data, 6), BigEndian.Int16(data, 8));
    }

    /// <summary>Adds the component glyph ids of a composite glyph, not recursing.</summary>
    public void GetComponents(int glyph, List<int> components)
    {
        ReadOnlySpan<byte> data = GetGlyphData(glyph, out long offset);
        if (data.Length < 10 || BigEndian.Int16(data, 0) >= 0)
        {
            return;
        }

        var r = new FontReader(data, offset);
        r.Skip(10);
        int flags;
        do
        {
            flags = r.ReadUInt16();
            components.Add(r.ReadUInt16());
            r.Skip(ComponentArgumentBytes(flags));
        }
        while ((flags & MoreComponents) != 0);
    }

    /// <summary>Decodes a glyph's points, composites resolved, then records its contours as lines and quadratic curves.</summary>
    public void GetOutline(int glyph, OutlineRecorder recorder)
    {
        var points = new GlyphPoints();
        AddPoints(glyph, points, 0);
        points.Emit(recorder);
    }

    /// <summary>Decodes a glyph's points into <paramref name="points"/>, for the outline and for tests.</summary>
    public void AddPoints(int glyph, GlyphPoints points, int depth)
    {
        if (depth > MaxCompositeDepth)
        {
            throw FontErrors.Corrupt(_glyfOffset, $"Composite glyphs nest deeper than {MaxCompositeDepth} levels at glyph {glyph}.");
        }

        ReadOnlySpan<byte> data = GetGlyphData(glyph, out long offset);
        if (data.IsEmpty)
        {
            return;
        }

        var r = new FontReader(data, offset);
        int contours = r.ReadInt16();
        r.Skip(8);
        if (contours >= 0)
        {
            AddSimple(ref r, contours, points);
        }
        else
        {
            AddComposite(ref r, points, depth);
        }
    }

    private static int ComponentArgumentBytes(int flags)
    {
        int bytes = (flags & ArgsAreWords) != 0 ? 4 : 2;
        if ((flags & HaveScale) != 0)
        {
            bytes += 2;
        }
        else if ((flags & HaveXYScale) != 0)
        {
            bytes += 4;
        }
        else if ((flags & HaveTwoByTwo) != 0)
        {
            bytes += 8;
        }

        return bytes;
    }

    private static void AddSimple(ref FontReader r, int contours, GlyphPoints points)
    {
        if (contours == 0)
        {
            return;
        }

        int first = points.Count;
        Span<int> ends = contours <= 256 ? stackalloc int[contours] : new int[contours];
        int previous = -1;
        for (int i = 0; i < contours; i++)
        {
            ends[i] = r.ReadUInt16();
            if (ends[i] < previous)
            {
                throw FontErrors.Corrupt(-1, "A glyph's contour end points run backwards.");
            }

            previous = ends[i];
        }

        int count = ends[contours - 1] + 1;
        if (points.Count + count > MaxPoints)
        {
            throw FontErrors.Corrupt(-1, "A glyph has too many points.");
        }

        r.Skip(r.ReadUInt16());

        // Flags, with repeats expanded.
        byte[] flags = new byte[count];
        for (int i = 0; i < count;)
        {
            byte flag = r.ReadByte();
            flags[i++] = flag;
            if ((flag & Repeat) != 0)
            {
                int repeat = r.ReadByte();
                if (repeat > count - i)
                {
                    throw FontErrors.Corrupt(-1, "A glyph's flags repeat past its point count.");
                }

                for (int k = 0; k < repeat; k++)
                {
                    flags[i++] = flag;
                }
            }
        }

        points.Reserve(count);
        int x = 0;
        for (int i = 0; i < count; i++)
        {
            byte flag = flags[i];
            if ((flag & XShort) != 0)
            {
                int dx = r.ReadByte();
                x += (flag & XSameOrPositive) != 0 ? dx : -dx;
            }
            else if ((flag & XSameOrPositive) == 0)
            {
                x += r.ReadInt16();
            }

            points.SetX(first + i, x);
        }

        int y = 0;
        for (int i = 0; i < count; i++)
        {
            byte flag = flags[i];
            if ((flag & YShort) != 0)
            {
                int dy = r.ReadByte();
                y += (flag & YSameOrPositive) != 0 ? dy : -dy;
            }
            else if ((flag & YSameOrPositive) == 0)
            {
                y += r.ReadInt16();
            }

            points.SetY(first + i, y, (flag & OnCurve) != 0);
        }

        points.Commit(count);
        foreach (int end in ends)
        {
            points.AddContourEnd(first + end);
        }
    }

    private void AddComposite(ref FontReader r, GlyphPoints points, int depth)
    {
        int flags;
        do
        {
            flags = r.ReadUInt16();
            int component = r.ReadUInt16();
            int arg1, arg2;
            if ((flags & ArgsAreWords) != 0)
            {
                arg1 = (flags & ArgsAreXY) != 0 ? r.ReadInt16() : r.ReadUInt16();
                arg2 = (flags & ArgsAreXY) != 0 ? r.ReadInt16() : r.ReadUInt16();
            }
            else
            {
                arg1 = (flags & ArgsAreXY) != 0 ? r.ReadSByte() : r.ReadByte();
                arg2 = (flags & ArgsAreXY) != 0 ? r.ReadSByte() : r.ReadByte();
            }

            float a = 1, b = 0, c = 0, d = 1;
            if ((flags & HaveScale) != 0)
            {
                a = d = r.ReadF2Dot14();
            }
            else if ((flags & HaveXYScale) != 0)
            {
                a = r.ReadF2Dot14();
                d = r.ReadF2Dot14();
            }
            else if ((flags & HaveTwoByTwo) != 0)
            {
                a = r.ReadF2Dot14();
                b = r.ReadF2Dot14();
                c = r.ReadF2Dot14();
                d = r.ReadF2Dot14();
            }

            int start = points.Count;
            int contourStart = points.ContourCount;
            AddPoints(component, points, depth + 1);
            bool transformed = a != 1 || b != 0 || c != 0 || d != 1;
            if (transformed)
            {
                points.Transform(start, a, b, c, d);
            }

            float dx, dy;
            if ((flags & ArgsAreXY) != 0)
            {
                dx = arg1;
                dy = arg2;
                if (transformed && (flags & ScaledOffset) != 0 && (flags & UnscaledOffset) == 0)
                {
                    (dx, dy) = ((a * arg1) + (c * arg2), (b * arg1) + (d * arg2));
                }
            }
            else
            {
                // Point matching: the component's point arg2 lands on the parent's point arg1.
                int parent = arg1, child = start + arg2;
                if (parent >= start || child >= points.Count)
                {
                    throw FontErrors.Corrupt(-1, "A composite glyph matches a point it does not have.");
                }

                dx = points.X(parent) - points.X(child);
                dy = points.Y(parent) - points.Y(child);
            }

            if (dx != 0 || dy != 0)
            {
                points.Translate(start, dx, dy);
            }

            _ = contourStart;
        }
        while ((flags & MoreComponents) != 0);

        if ((flags & HaveInstructions) != 0)
        {
            // Instructions follow the last component; nothing reads them.
        }
    }
}

/// <summary>A glyph's points with on-curve flags and contour ends, in font units.</summary>
internal sealed class GlyphPoints
{
    private float[] _x = new float[64];
    private float[] _y = new float[64];
    private bool[] _on = new bool[64];
    private readonly List<int> _ends = [];

    public int Count { get; private set; }

    public int ContourCount => _ends.Count;

    public IReadOnlyList<int> ContourEnds => _ends;

    public float X(int i) => _x[i];

    public float Y(int i) => _y[i];

    public bool IsOnCurve(int i) => _on[i];

    public void Reserve(int extra)
    {
        int needed = Count + extra;
        if (needed > _x.Length)
        {
            int size = Math.Max(needed, _x.Length * 2);
            Array.Resize(ref _x, size);
            Array.Resize(ref _y, size);
            Array.Resize(ref _on, size);
        }
    }

    public void SetX(int i, float x) => _x[i] = x;

    public void SetY(int i, float y, bool onCurve)
    {
        _y[i] = y;
        _on[i] = onCurve;
    }

    public void Commit(int count) => Count += count;

    public void AddContourEnd(int end) => _ends.Add(end);

    public void Transform(int start, float a, float b, float c, float d)
    {
        for (int i = start; i < Count; i++)
        {
            float x = _x[i], y = _y[i];
            _x[i] = (a * x) + (c * y);
            _y[i] = (b * x) + (d * y);
        }
    }

    public void Translate(int start, float dx, float dy)
    {
        for (int i = start; i < Count; i++)
        {
            _x[i] += dx;
            _y[i] += dy;
        }
    }

    /// <summary>
    /// Records the contours: each starts at its first on-curve point (or the midpoint of its last and first points when all are
    /// off-curve), with an on-curve point implied between two off-curve ones, as the TrueType specification defines.
    /// </summary>
    public void Emit(OutlineRecorder sink)
    {
        int start = 0;
        foreach (int end in _ends)
        {
            int n = end - start + 1;
            if (n > 0)
            {
                EmitContour(sink, start, n);
            }

            start = end + 1;
        }
    }

    private void EmitContour(OutlineRecorder sink, int start, int n)
    {
        int first = -1;
        for (int i = 0; i < n; i++)
        {
            if (_on[start + i])
            {
                first = i;
                break;
            }
        }

        if (first < 0)
        {
            int last = start + n - 1;
            sink.MoveTo((_x[last] + _x[start]) / 2, (_y[last] + _y[start]) / 2);
            for (int i = 0; i < n; i++)
            {
                int p = start + i, q = start + ((i + 1) % n);
                sink.QuadTo(_x[p], _y[p], (_x[p] + _x[q]) / 2, (_y[p] + _y[q]) / 2);
            }

            sink.Close();
            return;
        }

        sink.MoveTo(_x[start + first], _y[start + first]);
        bool haveControl = false;
        float cx = 0, cy = 0;
        for (int k = 1; k <= n; k++)
        {
            int p = start + ((first + k) % n);
            float x = _x[p], y = _y[p];
            if (_on[p])
            {
                if (haveControl)
                {
                    sink.QuadTo(cx, cy, x, y);
                    haveControl = false;
                }
                else if (k < n)
                {
                    // The closing line back to the start is left to Close.
                    sink.LineTo(x, y);
                }
            }
            else
            {
                if (haveControl)
                {
                    sink.QuadTo(cx, cy, (cx + x) / 2, (cy + y) / 2);
                }

                cx = x;
                cy = y;
                haveControl = true;
            }
        }

        sink.Close();
    }
}
