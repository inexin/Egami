using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Hinting;

/// <summary>A glyph hinted at one size: its points in pixels (y up, origin at the hinted left phantom point) and its hinted advance.</summary>
/// <param name="X">The x of every outline point in pixels.</param>
/// <param name="Y">The y of every outline point in pixels.</param>
/// <param name="OnCurve">Whether each point is on the curve.</param>
/// <param name="ContourEnds">The last point of each contour.</param>
/// <param name="Advance">The hinted advance in pixels: the right phantom point less the left, after the glyph's program.</param>
internal sealed record HintedGlyph(float[] X, float[] Y, bool[] OnCurve, int[] ContourEnds, float Advance)
{
    /// <summary>Gets a value indicating whether the glyph's program moved or touched its advance phantom point in x.</summary>
    public bool AdvanceTouched { get; init; }

    /// <summary>Records the contours as lines and quadratic curves, as the TrueType specification defines its outlines.</summary>
    public void Emit(IGlyphOutlineSink sink)
    {
        var points = new GlyphPoints();
        points.Reserve(X.Length);
        for (int i = 0; i < X.Length; i++)
        {
            points.SetX(i, X[i]);
            points.SetY(i, Y[i], OnCurve[i]);
        }

        points.Commit(X.Length);
        foreach (int end in ContourEnds)
        {
            points.AddContourEnd(end);
        }

        OutlineRecorder recorder = OutlineRecorder.Take();
        try
        {
            points.Emit(recorder);
            recorder.ReplayTo(sink);
        }
        finally
        {
            recorder.Release();
        }
    }
}

/// <summary>
/// Loads TrueType glyphs into zones and runs their programs (2.7.5 task 16): the outline's points with four phantom points
/// (left side bearing and advance points from hmtx, rounded to whole pixels, top and bottom from the typographic ascender and
/// descender), composite glyphs hinted component by component, their offsets rounded when ROUND_XY_TO_GRID asks, then the
/// composite's own program run on the assembled points with a scale of one. One hinter serves one face and target; it is not
/// thread-safe.
/// </summary>
internal sealed class GlyphHinter
{
    private const int ArgsAreWords = 0x0001;
    private const int ArgsAreXY = 0x0002;
    private const int RoundXYToGrid = 0x0004;
    private const int HaveScale = 0x0008;
    private const int MoreComponents = 0x0020;
    private const int HaveXYScale = 0x0040;
    private const int HaveTwoByTwo = 0x0080;
    private const int HaveInstructions = 0x0100;
    private const int UseMyMetrics = 0x0200;

    private readonly Typeface _face;
    private readonly GlyfTable _glyf;
    private readonly TrueTypeInterpreter _interpreter;

    public GlyphHinter(Typeface face, GlyfTable glyf, HintingData data, InterpreterTarget target)
    {
        _face = face;
        _glyf = glyf;
        _interpreter = new TrueTypeInterpreter(data, target);
    }

    /// <summary>Gets the interpreter, for tests.</summary>
    internal TrueTypeInterpreter Interpreter => _interpreter;

    /// <summary>Hints a glyph at a whole ppem with a 16.16 scale, or returns null when the size's programs failed.</summary>
    public HintedGlyph? Hint(ushort glyph, int ppem, long scale)
    {
        if (_interpreter.Ppem != ppem || _interpreter.Scale != scale)
        {
            _interpreter.SetSize(ppem, scale);
        }

        if (!_interpreter.IsReady)
        {
            return null;
        }

        Loaded g = Load(glyph, 0);
        int n = g.X.Length - 4;
        float left = g.X[n] / 64f;
        var x = new float[n];
        var y = new float[n];
        for (int i = 0; i < n; i++)
        {
            x[i] = (g.X[i] / 64f) - left;
            y[i] = g.Y[i] / 64f;
        }

        return new HintedGlyph(x, y, g.On[..n], g.Ends, (g.X[n + 1] - g.X[n]) / 64f) { AdvanceTouched = g.AdvanceTouched };
    }

    // A loaded glyph: current positions in 26.6 with its four phantom points last.
    private sealed record Loaded(int[] X, int[] Y, bool[] On, int[] Ends)
    {
        public bool AdvanceTouched { get; init; }
    }

    private Loaded Load(ushort glyph, int depth)
    {
        if (depth > GlyfTable.MaxCompositeDepth)
        {
            throw FontErrors.Corrupt(-1, "Composite glyphs nest too deep.");
        }

        ReadOnlySpan<byte> data = _glyf.GetGlyphData(glyph, out long offset);
        int contours = data.Length >= 10 ? BigEndian.Int16(data, 0) : 0;
        int xMin = data.Length >= 10 ? BigEndian.Int16(data, 2) : 0;
        return contours >= 0 ? LoadSimple(glyph, data, offset, contours, xMin) : LoadComposite(glyph, data, offset, xMin, depth);
    }

    private Loaded LoadSimple(ushort glyph, ReadOnlySpan<byte> data, long offset, int contours, int xMin)
    {
        var points = new GlyphPoints();
        byte[] program = [];
        if (data.Length >= 10 && contours > 0)
        {
            var r = new FontReader(data, offset);
            r.Skip(10);
            GlyfTable.ReadSimple(ref r, contours, points);
            int at = 10 + (2 * contours);
            int length = BigEndian.UInt16(data, at);
            program = data.Slice(at + 2, length).ToArray();
        }

        Zone zone = MakeZone(glyph, points.Count, xMin, i => (points.X(i), points.Y(i), points.IsOnCurve(i)));
        zone.ContourEnds = [.. points.ContourEnds];
        if (program.Length > 0)
        {
            _interpreter.RunGlyph(zone, program);
        }

        return new Loaded(zone.CurX, zone.CurY, zone.OnCurve, zone.ContourEnds) { AdvanceTouched = (zone.Touched[zone.Count - 3] & Zone.TouchX) != 0 };
    }

    private Loaded LoadComposite(ushort glyph, ReadOnlySpan<byte> data, long offset, int xMin, int depth)
    {
        var r = new FontReader(data, offset);
        r.Skip(10);
        var xs = new List<int>();
        var ys = new List<int>();
        var on = new List<bool>();
        var ends = new List<int>();
        int[]? metrics = null;
        int flags;
        bool instructions = false;
        do
        {
            flags = r.ReadUInt16();
            ushort component = r.ReadUInt16();
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

            instructions |= (flags & HaveInstructions) != 0;
            Loaded part = Load(component, depth + 1);
            int n = part.X.Length - 4;
            int start = xs.Count;
            bool transformed = a != 1 || b != 0 || c != 0 || d != 1;
            for (int i = 0; i < n; i++)
            {
                int px = part.X[i], py = part.Y[i];
                if (transformed)
                {
                    (px, py) = ((int)MathF.Round((a * px) + (c * py)), (int)MathF.Round((b * px) + (d * py)));
                }

                xs.Add(px);
                ys.Add(py);
                on.Add(part.On[i]);
            }

            int dx, dy;
            if ((flags & ArgsAreXY) != 0)
            {
                // Offsets are unscaled unless SCALED_COMPONENT_OFFSET asks (Microsoft's default), then rounded to the grid on request.
                dx = (int)TrueTypeInterpreter.MulFix(arg1, _interpreter.Scale);
                dy = (int)TrueTypeInterpreter.MulFix(arg2, _interpreter.Scale);
                if ((flags & RoundXYToGrid) != 0)
                {
                    dx = (dx + 32) & ~63;
                    dy = (dy + 32) & ~63;
                }
            }
            else
            {
                int parent = arg1, child = start + arg2;
                if (parent >= start || child >= xs.Count)
                {
                    throw FontErrors.Corrupt(-1, "A composite glyph matches a point it does not have.");
                }

                dx = xs[parent] - xs[child];
                dy = ys[parent] - ys[child];
            }

            for (int i = start; i < xs.Count; i++)
            {
                xs[i] += dx;
                ys[i] += dy;
            }

            foreach (int end in part.Ends)
            {
                ends.Add(start + end);
            }

            if ((flags & UseMyMetrics) != 0)
            {
                metrics = [part.X[n] + dx, part.X[n + 1] + dx];
            }
        }
        while ((flags & MoreComponents) != 0);

        byte[] program = [];
        if (instructions && r.Remaining >= 2)
        {
            int length = r.ReadUInt16();
            program = r.ReadBytes(Math.Min(length, r.Remaining)).ToArray();
        }

        int count = xs.Count;
        Zone zone = MakeZone(glyph, count, xMin, i => (0, 0, false));
        for (int i = 0; i < count; i++)
        {
            // The composite's program sees the assembled, hinted points as its originals, at a scale of one.
            zone.UnscaledX[i] = zone.OrgX[i] = zone.CurX[i] = xs[i];
            zone.UnscaledY[i] = zone.OrgY[i] = zone.CurY[i] = ys[i];
            zone.OnCurve[i] = on[i];
        }

        for (int i = count; i < count + 4; i++)
        {
            zone.UnscaledX[i] = zone.OrgX[i];
            zone.UnscaledY[i] = zone.OrgY[i];
        }

        if (metrics is not null)
        {
            zone.UnscaledX[count] = zone.OrgX[count] = zone.CurX[count] = metrics[0];
            zone.UnscaledX[count + 1] = zone.OrgX[count + 1] = zone.CurX[count + 1] = metrics[1];
        }

        zone.ContourEnds = [.. ends];
        if (program.Length > 0)
        {
            _interpreter.RunGlyph(zone, program, composite: true);
        }

        return new Loaded(zone.CurX, zone.CurY, zone.OnCurve, zone.ContourEnds) { AdvanceTouched = (zone.Touched[zone.Count - 3] & Zone.TouchX) != 0 };
    }

    // A zone of `count` outline points from font units plus the phantom points: pp1 at xMin − lsb and pp2 an advance to its right,
    // both rounded to whole pixels; pp3 and pp4 at the typographic ascender and descender, x 0.
    private Zone MakeZone(ushort glyph, int count, int xMin, Func<int, (float X, float Y, bool On)> point)
    {
        var zone = new Zone(count + 4, twilight: false) { OutlineCount = count };
        long scale = _interpreter.Scale;
        for (int i = 0; i < count; i++)
        {
            (float x, float y, bool on) = point(i);
            int ux = (int)MathF.Round(x), uy = (int)MathF.Round(y);
            zone.UnscaledX[i] = ux;
            zone.UnscaledY[i] = uy;
            zone.OrgX[i] = zone.CurX[i] = (int)TrueTypeInterpreter.MulFix(ux, scale);
            zone.OrgY[i] = zone.CurY[i] = (int)TrueTypeInterpreter.MulFix(uy, scale);
            zone.OnCurve[i] = on;
        }

        int lsb = (int)MathF.Round(_face.GetLeftSideBearing(glyph));
        int advance = (int)MathF.Round(_face.GetAdvance(glyph));
        int pp1 = xMin - lsb;
        VerticalMetrics typo = _face.TypoMetrics;
        (int ux, int uy)[] phantoms = [(pp1, 0), (pp1 + advance, 0), (0, typo.Ascent), (0, typo.Descent)];
        for (int k = 0; k < 4; k++)
        {
            int i = count + k;
            zone.UnscaledX[i] = phantoms[k].ux;
            zone.UnscaledY[i] = phantoms[k].uy;
            zone.OrgX[i] = zone.CurX[i] = Round((int)TrueTypeInterpreter.MulFix(phantoms[k].ux, scale));
            zone.OrgY[i] = zone.CurY[i] = Round((int)TrueTypeInterpreter.MulFix(phantoms[k].uy, scale));
        }

        return zone;
    }

    private static int Round(int v) => v >= 0 ? (v + 32) & ~63 : -((-v + 32) & ~63);
}
