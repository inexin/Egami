using System.Buffers.Binary;

using Egami.Fonts.Cff;
using Egami.Fonts.Parsing;
using Egami.Fonts.Tables;
using Egami.Fonts.Variations;

namespace Egami.Fonts.Subsetting;

/// <summary>
/// Writes a variable face's position as static data for the subsetter: glyf glyphs with gvar's deltas applied and rounded (simple
/// glyphs re-encoded, composite offsets moved), metrics from the rounded phantom points or HVAR, MVAR's values in OS/2 and post,
/// and CFF2 written as CFF with blends resolved. fontTools' instancer is the reference.
/// </summary>
internal static class Instancer
{
    private const int ArgsAreWords = 0x0001;
    private const int ArgsAreXY = 0x0002;
    private const int RoundXYToGrid = 0x0004;
    private const int MoreComponents = 0x0020;
    private const int HaveInstructions = 0x0100;

    /// <summary>The variation tables an instance leaves out, even when asked to keep them.</summary>
    public static readonly FontTag[] VariationTables =
    [
        Tags.Fvar, Tags.Gvar, Tags.Avar, Tags.Hvar, Tags.Mvar, Tags.Stat, Tags.Cvar, Tags.Cff2, new("VVAR"),
    ];

    /// <summary>A glyph written at a position, with its rounded metrics.</summary>
    public readonly record struct StaticGlyph(byte[] Data, int Advance, int LeftBearing);

    /// <summary>One glyph of a TrueType face at <paramref name="instance"/>'s position, components renumbered by <paramref name="map"/>.</summary>
    public static StaticGlyph Glyph(Typeface face, VariationInstance instance, int glyph, Dictionary<ushort, ushort> map)
    {
        GlyfTable glyf = face.Glyf!;
        GvarTable? gvar = instance.Data.Gvar;
        HmtxTable? hmtx = face.Root.Hmtx;
        int advance = hmtx?.GetAdvance(glyph) ?? 0, lsb = hmtx?.GetLeftSideBearing(glyph) ?? 0;
        ReadOnlySpan<byte> data = glyf.GetGlyphData(glyph, out long offset);
        float left, right;
        byte[] bytes;
        int xMin = 0;
        if (data.IsEmpty)
        {
            Span<float> x = [-lsb, -lsb + advance, 0, 0];
            Span<float> y = stackalloc float[4];
            Span<float> dx = stackalloc float[4];
            Span<float> dy = stackalloc float[4];
            gvar?.AddDeltas(glyph, instance.Coords, x, y, [], dx, dy);
            (left, right) = (x[0] + dx[0], x[1] + dx[1]);
            bytes = [];
        }
        else
        {
            var r = new FontReader(data, offset);
            int contours = r.ReadInt16();
            int headerXMin = r.ReadInt16();
            r.Skip(6);
            float pp1 = headerXMin - lsb;
            if (contours >= 0)
            {
                (bytes, left, right, xMin) = Simple(ref r, data, contours, gvar, instance.Coords, glyph, pp1, advance);
            }
            else
            {
                (bytes, left, right) = Composite(ref r, data, gvar, instance.Coords, glyph, pp1, advance, map);
                xMin = int.MinValue;
            }
        }

        if (gvar is null && instance.Data.Hvar is HvarTable hvar)
        {
            // Without gvar the metrics come from HVAR alone.
            int adv = Round(advance + hvar.AdvanceDelta(glyph, instance.HvarScalars!));
            int bearing = Round(lsb + (hvar.LeftBearingDelta(glyph, instance.HvarScalars!) ?? 0));
            return new StaticGlyph(bytes, adv, bearing);
        }

        // As fontTools' instancer: the advance from the unrounded phantom points, the bearing from the bounds of the unrounded
        // (decomposed) outline, rounded, less the unrounded left phantom point.
        float boundsXMin = xMin;
        if (xMin == int.MinValue)
        {
            VariedGlyph varied = GlyfVariations.Build(glyf, gvar, hmtx, instance.Coords, glyph);
            boundsXMin = varied.Bounds.IsEmpty ? 0 : Round(varied.Bounds.XMin);
        }

        return new StaticGlyph(bytes, Round(right - left), Round(boundsXMin - left));
    }

    /// <summary>Sets each composite glyph's bounds from its components as written, as an instancer recalculating bounds does.</summary>
    public static void FinishComposites(byte[] glyf, uint[] loca)
    {
        var table = new GlyfTable(glyf, loca, 0);
        for (int g = 0; g < loca.Length - 1; g++)
        {
            int start = (int)loca[g];
            if (loca[g + 1] - loca[g] < 10 || BinaryPrimitives.ReadInt16BigEndian(glyf.AsSpan(start)) >= 0)
            {
                continue;
            }

            var points = new GlyphPoints();
            table.AddPoints(g, points, 0);
            if (points.Count == 0)
            {
                continue;
            }

            float fx0 = float.MaxValue, fy0 = float.MaxValue, fx1 = float.MinValue, fy1 = float.MinValue;
            for (int i = 0; i < points.Count; i++)
            {
                fx0 = MathF.Min(fx0, points.X(i));
                fy0 = MathF.Min(fy0, points.Y(i));
                fx1 = MathF.Max(fx1, points.X(i));
                fy1 = MathF.Max(fy1, points.Y(i));
            }

            int x0 = Round(fx0), y0 = Round(fy0), x1 = Round(fx1), y1 = Round(fy1);

            BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(start + 2), (short)x0);
            BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(start + 4), (short)y0);
            BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(start + 6), (short)x1);
            BinaryPrimitives.WriteInt16BigEndian(glyf.AsSpan(start + 8), (short)y1);
        }
    }

    private static (byte[] Data, float Left, float Right, int XMin) Simple(ref FontReader r, ReadOnlySpan<byte> data, int contours, GvarTable? gvar, ReadOnlySpan<float> coords, int glyph, float pp1, int advance)
    {
        var points = new GlyphPoints();
        int endsAt = r.Position;
        GlyfTable.ReadSimple(ref r, contours, points);
        int n = points.Count;
        float[] x = new float[n + 4], y = new float[n + 4], dx = new float[n + 4], dy = new float[n + 4];
        for (int i = 0; i < n; i++)
        {
            x[i] = points.X(i);
            y[i] = points.Y(i);
        }

        x[n] = pp1;
        x[n + 1] = pp1 + advance;
        gvar?.AddDeltas(glyph, coords, x, y, points.ContourEnds, dx, dy);

        // The original's ends, instructions and on-curve and overlap flags stay; coordinates are rounded as fontTools' otRound.
        var rounded = new List<(int X, int Y, bool On)>(n);
        int minX = int.MaxValue, minY = int.MaxValue, maxX = int.MinValue, maxY = int.MinValue;
        for (int i = 0; i < n; i++)
        {
            int px = Round(x[i] + dx[i]), py = Round(y[i] + dy[i]);
            rounded.Add((px, py, points.IsOnCurve(i)));
            minX = Math.Min(minX, px);
            minY = Math.Min(minY, py);
            maxX = Math.Max(maxX, px);
            maxY = Math.Max(maxY, py);
        }

        if (n == 0)
        {
            minX = minY = maxX = maxY = 0;
        }

        var ends = new List<int>(contours);
        for (int c = 0; c < contours; c++)
        {
            ends.Add(BigEndian.UInt16(data, endsAt + (c * 2)));
        }

        int instructionLength = BigEndian.UInt16(data, endsAt + (contours * 2));
        ReadOnlySpan<byte> instructions = data.Slice(endsAt + (contours * 2) + 2, instructionLength);
        bool overlap = n > 0 && (data[endsAt + (contours * 2) + 2 + instructionLength] & 0x40) != 0;
        var w = new ByteWriter(16 + (n * 5));
        Woff.Woff2Glyf.WriteSimple(w, contours, (short)minX, (short)minY, (short)maxX, (short)maxY, ends, instructions, rounded, overlap);
        return (w.ToArray(), x[n] + dx[n], x[n + 1] + dx[n + 1], minX);
    }

    private static (byte[] Data, float Left, float Right) Composite(ref FontReader r, ReadOnlySpan<byte> data, GvarTable? gvar, ReadOnlySpan<float> coords, int glyph, float pp1, int advance, Dictionary<ushort, ushort> map)
    {
        int start = r.Position;
        List<GlyphComponent> components = GlyfTable.ReadComponents(ref r);
        int end = r.Position;
        int m = components.Count;
        float[] x = new float[m + 4], y = new float[m + 4], dx = new float[m + 4], dy = new float[m + 4];
        for (int i = 0; i < m; i++)
        {
            if (components[i].ArgsAreXY)
            {
                x[i] = components[i].Arg1;
                y[i] = components[i].Arg2;
            }
        }

        x[m] = pp1;
        x[m + 1] = pp1 + advance;
        gvar?.AddDeltas(glyph, coords, x, y, [], dx, dy);

        // Each record again, with moved offsets in words when they no longer fit bytes; the transform bytes are copied.
        var w = new ByteWriter(data.Length + (m * 2));
        w.Bytes(data[..10]);
        var source = new FontReader(data[start..end]);
        bool haveInstructions = false;
        for (int i = 0; i < m; i++)
        {
            int flags = source.ReadUInt16();
            source.ReadUInt16();
            int argBytes = (flags & ArgsAreWords) != 0 ? 4 : 2;
            source.Skip(argBytes);
            int transformBytes = (flags & 0x0008) != 0 ? 2 : (flags & 0x0040) != 0 ? 4 : (flags & 0x0080) != 0 ? 8 : 0;
            ReadOnlySpan<byte> transform = source.ReadBytes(transformBytes);
            GlyphComponent c = components[i];
            haveInstructions |= (flags & HaveInstructions) != 0;
            int arg1 = c.Arg1, arg2 = c.Arg2;
            if (c.ArgsAreXY)
            {
                arg1 = Round(x[i] + dx[i]);
                arg2 = Round(y[i] + dy[i]);
            }

            bool words = c.ArgsAreXY ? arg1 is < sbyte.MinValue or > sbyte.MaxValue || arg2 is < sbyte.MinValue or > sbyte.MaxValue
                : arg1 > byte.MaxValue || arg2 > byte.MaxValue;
            flags = (words ? flags | ArgsAreWords : flags & ~ArgsAreWords) & ~RoundXYToGrid;
            if (i < m - 1)
            {
                flags |= MoreComponents;
            }

            w.U16(flags);
            w.U16(map.TryGetValue((ushort)c.Glyph, out ushort now) ? now : (ushort)0xFFFF);
            if (words)
            {
                w.U16(arg1);
                w.U16(arg2);
            }
            else
            {
                w.U8(arg1);
                w.U8(arg2);
            }

            w.Bytes(transform);
        }

        if (haveInstructions)
        {
            w.Bytes(data[end..]);
        }

        return (w.ToArray(), x[m] + dx[m], x[m + 1] + dx[m + 1]);
    }

    /// <summary>OS/2 at a position: MVAR's values, and usWeightClass and usWidthClass from wght and wdth, as fontTools sets them.</summary>
    public static byte[]? Os2(Typeface face, VariationInstance instance)
    {
        if (!face.TryGetTable(Tags.OS2, out ReadOnlyMemory<byte> original) || original.Length < 78)
        {
            return null;
        }

        byte[] os2 = original.ToArray();
        void Patch(int at, string tag, bool unsigned = false)
        {
            if (at + 2 > os2.Length)
            {
                return;
            }

            int value = unsigned ? BinaryPrimitives.ReadUInt16BigEndian(os2.AsSpan(at)) : BinaryPrimitives.ReadInt16BigEndian(os2.AsSpan(at));
            int varied = Round(value + instance.Mvar(tag));
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(at), (ushort)varied);
        }

        Patch(10, "sbxs");
        Patch(12, "sbys");
        Patch(14, "sbxo");
        Patch(16, "sbyo");
        Patch(18, "spxs");
        Patch(20, "spys");
        Patch(22, "spxo");
        Patch(24, "spyo");
        Patch(26, "strs");
        Patch(28, "stro");
        Patch(68, "hasc");
        Patch(70, "hdsc");
        Patch(72, "hlgp");
        Patch(74, "hcla", unsigned: true);
        Patch(76, "hcld", unsigned: true);
        if (os2.Length >= 90)
        {
            Patch(86, "xhgt");
            Patch(88, "cpht");
        }

        VariationSpace space = instance.Data.Space;
        if (space.AxisIndex(FontVariation.WeightAxis) is int w and >= 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(4), (ushort)Math.Clamp(Round(instance.User[w]), 1, 1000));
        }

        if (space.AxisIndex(FontVariation.WidthAxis) is int d and >= 0)
        {
            BinaryPrimitives.WriteUInt16BigEndian(os2.AsSpan(6), (ushort)Matching.StyleMatcher.WidthClassOf(instance.User[d]));
        }

        return os2;
    }

    /// <summary>post's underline from MVAR and its italic angle from slnt.</summary>
    public static byte[] Post(Typeface face, VariationInstance instance)
    {
        byte[] post = SfntWriter.Post(face);
        BinaryPrimitives.WriteInt16BigEndian(post.AsSpan(8), (short)Round(face.Post.UnderlinePosition + instance.Mvar("undo")));
        BinaryPrimitives.WriteInt16BigEndian(post.AsSpan(10), (short)Round(face.Post.UnderlineThickness + instance.Mvar("unds")));
        if (instance.Data.Space.AxisIndex(FontVariation.SlantAxis) is int s and >= 0)
        {
            BinaryPrimitives.WriteInt32BigEndian(post.AsSpan(4), (int)Math.Round(instance.User[s] * 65536.0));
        }

        return post;
    }

    /// <summary>
    /// A CFF2 face's glyphs as a CID-keyed CFF font (identity charset, one font dict per CFF2 font dict used), each charstring the
    /// glyph's outline at the position, hintless, with its advance as the width.
    /// </summary>
    public static byte[] CffFromCff2(Typeface face, ushort[] oldIds, Dictionary<ushort, ushort> map, SubsetOptions options, float[]? scalars)
    {
        CffFont cff = face.Cff!;
        CharStringInterpreter interpreter = CharStringInterpreter.For(cff);
        var fdMap = new SortedDictionary<int, int>();
        foreach (ushort old in map.Keys)
        {
            fdMap.TryAdd(cff.FontDictOf(old), 0);
        }

        int[] fds = [.. fdMap.Keys];
        for (int i = 0; i < fds.Length; i++)
        {
            fdMap[fds[i]] = i;
        }

        var charStrings = new List<byte[]>(oldIds.Length);
        for (int g = 0; g < oldIds.Length; g++)
        {
            ushort old = oldIds[g];
            if (!map.TryGetValue(old, out ushort now) || now != g)
            {
                charStrings.Add([14]);
                continue;
            }

            OutlineRecorder recorder = OutlineRecorder.Take();
            try
            {
                interpreter.Draw(old, recorder, null, scalars);
                byte[] code = CffEncoding.CharString(recorder, Math.Round(face.GetAdvance(old)));
                charStrings.Add(code);
            }
            finally
            {
                recorder.Release();
            }
        }

        var strings = new List<byte[]> { "Adobe"u8.ToArray(), "Identity"u8.ToArray() };
        int adobe = CffStandard.StandardStringCount, identity = adobe + 1;
        byte[] nameIndex = CffEncoding.Index([System.Text.Encoding.Latin1.GetBytes((options.FontNamePrefix ?? string.Empty) + face.PostScriptName)]);
        byte[] stringIndex = CffEncoding.Index(strings);
        byte[] globalIndex = CffEncoding.Index([]);
        byte[] charStringIndex = CffEncoding.Index(charStrings);

        // Charset: identity, glyph i is CID i.
        var charset = new ByteWriter();
        charset.U8(2);
        if (oldIds.Length > 1)
        {
            charset.U16(1);
            charset.U16(oldIds.Length - 2);
        }

        // FDSelect format 3.
        var fdSelect = new ByteWriter();
        var ranges = new List<(int First, int Fd)>();
        for (int g = 0; g < oldIds.Length; g++)
        {
            int fd = fdMap.TryGetValue(cff.FontDictOf(oldIds[g]), out int mapped) ? mapped : 0;
            if (ranges.Count == 0 || ranges[^1].Fd != fd)
            {
                ranges.Add((g, fd));
            }
        }

        fdSelect.U8(3);
        fdSelect.U16(ranges.Count);
        foreach ((int first, int fd) in ranges)
        {
            fdSelect.U16(first);
            fdSelect.U8(fd);
        }

        fdSelect.U16(oldIds.Length);

        // Private DICTs without CFF2's vsindex, blends (already resolved to defaults) or subroutines; nominal and default widths 0.
        var privates = new List<byte[]>();
        foreach (int fd in fds)
        {
            var entries = cff.Privates[fd].Dict.Entries.Where(e => e.Key is not (CffFont.OpSubrs or CffFont.OpVsIndex)).Select(e => (e.Key, e.Value)).ToList();
            privates.Add(CffEncoding.Dict(entries));
        }

        HashSet<int> offsets = [CffFont.OpCharset, CffFont.OpCharStrings, CffFont.OpPrivate, CffFont.OpFdArray, CffFont.OpFdSelect];
        List<(int, double[])> TopEntries(int charsetAt, int fdSelectAt, int charStringsAt, int fdArrayAt)
        {
            var entries = new List<(int, double[])> { (CffFont.OpRos, [adobe, identity, 0]) };
            if (cff.Top.Get(CffFont.OpFontMatrix) is double[] matrix)
            {
                entries.Add((CffFont.OpFontMatrix, matrix));
            }

            entries.Add((CffFont.OpFontBBox, [face.Head.XMin, face.Head.YMin, face.Head.XMax, face.Head.YMax]));
            entries.Add((CffFont.OpCharset, [charsetAt]));
            entries.Add((CffFont.OpCharStrings, [charStringsAt]));
            entries.Add((CffFont.OpCidCount, [oldIds.Length]));
            entries.Add((CffFont.OpFdSelect, [fdSelectAt]));
            entries.Add((CffFont.OpFdArray, [fdArrayAt]));
            return entries;
        }

        int topSize = CffEncoding.Index([CffEncoding.Dict(TopEntries(0, 0, 0, 0), offsets)]).Length;
        int at = 4 + nameIndex.Length + topSize + stringIndex.Length + globalIndex.Length;
        int charsetAt = at;
        at += charset.Length;
        int fdSelectAt = at;
        at += fdSelect.Length;
        int charStringsAt = at;
        at += charStringIndex.Length;
        List<byte[]> FdDicts(int privateStart)
        {
            var dicts = new List<byte[]>();
            int p = privateStart;
            foreach (byte[] priv in privates)
            {
                dicts.Add(CffEncoding.Dict([(CffFont.OpPrivate, [priv.Length, p])], offsets));
                p += priv.Length;
            }

            return dicts;
        }

        int fdArrayAt = at;
        byte[] fdArray = CffEncoding.Index(FdDicts(0));
        at += fdArray.Length;
        fdArray = CffEncoding.Index(FdDicts(at));
        byte[] topIndex = CffEncoding.Index([CffEncoding.Dict(TopEntries(charsetAt, fdSelectAt, charStringsAt, fdArrayAt), offsets)]);
        var w = new ByteWriter(at + privates.Sum(p => p.Length));
        w.Bytes([1, 0, 4, 4]);
        w.Bytes(nameIndex);
        w.Bytes(topIndex);
        w.Bytes(stringIndex);
        w.Bytes(globalIndex);
        w.Bytes(charset.Written);
        w.Bytes(fdSelect.Written);
        w.Bytes(charStringIndex);
        w.Bytes(fdArray);
        foreach (byte[] priv in privates)
        {
            w.Bytes(priv);
        }

        return w.ToArray();
    }

    /// <summary>fontTools' otRound: half up.</summary>
    public static int Round(float value) => (int)MathF.Floor(value + 0.5f);
}
