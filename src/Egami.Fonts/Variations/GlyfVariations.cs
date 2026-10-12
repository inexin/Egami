using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;

namespace Egami.Fonts.Variations;

/// <summary>
/// Varies TrueType glyphs: gvar deltas on each simple glyph's points and four phantom points (with IUP for the points a tuple
/// leaves out), and on each composite glyph's component offsets, its components varied in turn.
/// </summary>
internal static class GlyfVariations
{
    /// <summary>Builds a glyph's outline, control box and phantom-point metrics at a position.</summary>
    public static VariedGlyph Build(GlyfTable glyf, GvarTable? gvar, HmtxTable? hmtx, ReadOnlySpan<float> coords, int glyph)
    {
        var points = new GlyphPoints();
        (float left, float right) = AddPoints(glyf, gvar, hmtx, coords, glyph, points, 0);
        GlyphExtents box = ControlBox(points);
        OutlineRecorder recorder = OutlineRecorder.Take();
        try
        {
            points.Emit(recorder);
            float xMin = box.IsEmpty && points.Count == 0 ? 0 : box.XMin;
            return new VariedGlyph(recorder.Verbs.ToArray(), recorder.Coordinates.ToArray(), box, right - left, xMin - left);
        }
        finally
        {
            recorder.Release();
        }
    }

    // Adds the glyph's varied points, returning its varied left and right phantom points.
    private static (float Left, float Right) AddPoints(GlyfTable glyf, GvarTable? gvar, HmtxTable? hmtx, ReadOnlySpan<float> coords, int glyph, GlyphPoints points, int depth)
    {
        if (depth > GlyfTable.MaxCompositeDepth)
        {
            throw FontErrors.Corrupt(-1, $"Composite glyphs nest deeper than {GlyfTable.MaxCompositeDepth} levels at glyph {glyph}.");
        }

        int advance = hmtx?.GetAdvance(glyph) ?? 0, lsb = hmtx?.GetLeftSideBearing(glyph) ?? 0;
        ReadOnlySpan<byte> data = glyf.GetGlyphData(glyph, out long offset);
        if (data.IsEmpty)
        {
            return Phantoms(gvar, coords, glyph, -lsb, advance);
        }

        var r = new FontReader(data, offset);
        int contours = r.ReadInt16();
        int xMin = r.ReadInt16();
        r.Skip(6);
        float left = xMin - lsb;
        if (contours >= 0)
        {
            int start = points.Count, firstContour = points.ContourCount;
            GlyfTable.ReadSimple(ref r, contours, points);
            int n = points.Count - start;
            if (gvar is null)
            {
                return (left, left + advance);
            }

            float[] x = new float[n + 4], y = new float[n + 4], dx = new float[n + 4], dy = new float[n + 4];
            for (int i = 0; i < n; i++)
            {
                x[i] = points.X(start + i);
                y[i] = points.Y(start + i);
            }

            x[n] = left;
            x[n + 1] = left + advance;
            var ends = new int[points.ContourCount - firstContour];
            for (int c = 0; c < ends.Length; c++)
            {
                ends[c] = points.ContourEnds[firstContour + c] - start;
            }

            if (gvar.AddDeltas(glyph, coords, x, y, ends, dx, dy))
            {
                for (int i = 0; i < n; i++)
                {
                    points.SetPoint(start + i, x[i] + dx[i], y[i] + dy[i]);
                }
            }

            return (left + dx[n], left + advance + dx[n + 1]);
        }

        List<GlyphComponent> components = GlyfTable.ReadComponents(ref r);
        int m = components.Count;
        float[] cx = new float[m + 4], cy = new float[m + 4], cdx = new float[m + 4], cdy = new float[m + 4];
        for (int i = 0; i < m; i++)
        {
            if (components[i].ArgsAreXY)
            {
                cx[i] = components[i].Arg1;
                cy[i] = components[i].Arg2;
            }
        }

        cx[m] = left;
        cx[m + 1] = left + advance;
        gvar?.AddDeltas(glyph, coords, cx, cy, [], cdx, cdy);
        foreach ((GlyphComponent component, int i) in components.Select((c, i) => (c, i)))
        {
            int start = points.Count;
            AddPoints(glyf, gvar, hmtx, coords, component.Glyph, points, depth + 1);
            if (points.Count > GlyfTable.MaxPoints)
            {
                throw FontErrors.Corrupt(-1, "A glyph has too many points.");
            }

            component.Place(points, start, component.ArgsAreXY ? cx[i] + cdx[i] : component.Arg1, component.ArgsAreXY ? cy[i] + cdy[i] : component.Arg2);
        }

        return (left + cdx[m], left + advance + cdx[m + 1]);
    }

    private static (float Left, float Right) Phantoms(GvarTable? gvar, ReadOnlySpan<float> coords, int glyph, float left, int advance)
    {
        if (gvar is null)
        {
            return (left, left + advance);
        }

        Span<float> x = [left, left + advance, 0, 0];
        Span<float> y = stackalloc float[4];
        Span<float> dx = stackalloc float[4];
        Span<float> dy = stackalloc float[4];
        gvar.AddDeltas(glyph, coords, x, y, [], dx, dy);
        return (left + dx[0], left + advance + dx[1]);
    }

    private static GlyphExtents ControlBox(GlyphPoints points)
    {
        if (points.Count == 0)
        {
            return GlyphExtents.Empty;
        }

        float minX = float.MaxValue, minY = float.MaxValue, maxX = float.MinValue, maxY = float.MinValue;
        for (int i = 0; i < points.Count; i++)
        {
            minX = MathF.Min(minX, points.X(i));
            minY = MathF.Min(minY, points.Y(i));
            maxX = MathF.Max(maxX, points.X(i));
            maxY = MathF.Max(maxY, points.Y(i));
        }

        return new GlyphExtents(minX, minY, maxX, maxY);
    }
}
