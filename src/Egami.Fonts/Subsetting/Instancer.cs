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
    private const int OverlapCompound = 0x0400;
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

        // The original's ends, instructions and on-curve flags stay; coordinates are rounded as fontTools' otRound. The contours keep
        // their overlaps, so, as fontTools' instancer does, the glyph is marked OVERLAP_SIMPLE for rasterizers that must merge them.
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
        ReadOnlySpan<byte> instructions = BigEndian.Slice(data, endsAt + (contours * 2) + 2, instructionLength);
        bool overlap = n > 0;
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

            if (i == 0)
            {
                flags |= OverlapCompound;
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
    /// A CFF2 face's glyphs as a CID-keyed CFF font (identity charset, one font dict per CFF2 font dict used): each charstring
    /// rewritten operator by operator with its blends resolved at the position and rounded, its hints kept unless
    /// <see cref="SubsetOptions.DropHinting"/> is set, the shortest operators, and the subroutines kept when every call of one
    /// writes the same charstring; the Private DICTs' blends resolved too, and the widths against new nominal and default widths.
    /// </summary>
    public static byte[] CffFromCff2(Typeface face, ushort[] oldIds, Dictionary<ushort, ushort> map, SubsetOptions options, float[]? scalars)
    {
        CffFont cff = face.Cff!;
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

        // Widths: each font dict's most common as its default, and the nominal width that writes the others shortest.
        var advances = new Dictionary<ushort, int>();
        var byDict = new Dictionary<int, List<int>>();
        foreach (ushort old in map.Keys)
        {
            int advance = Round(face.GetAdvance(old));
            advances[old] = advance;
            int fd = cff.FontDictOf(old);
            if (!byDict.TryGetValue(fd, out List<int>? list))
            {
                byDict[fd] = list = [];
            }

            list.Add(advance);
        }

        var widthDefaults = new Dictionary<int, (int Default, int Nominal)>();
        foreach (int fd in fds)
        {
            widthDefaults[fd] = ChooseWidths(byDict.GetValueOrDefault(fd) ?? []);
        }

        double? WidthOperand(int old)
        {
            (int defaultWidth, int nominal) = widthDefaults[cff.FontDictOf(old)];
            int advance = advances.TryGetValue((ushort)old, out int a) ? a : Round(face.GetAdvance((ushort)old));
            return advance == defaultWidth ? null : advance - nominal;
        }

        byte[] Fallback(int old, double? width)
        {
            // A charstring the writer does not model: its outline drawn at the position and encoded again.
            OutlineRecorder recorder = OutlineRecorder.Take();
            try
            {
                CharStringInterpreter.For(cff).Draw(old, recorder, null, scalars);
                return CffEncoding.CharString(recorder, width);
            }
            finally
            {
                recorder.Release();
            }
        }

        var writer = new CharStringWriter(cff, scalars, round: true, options.DropHinting, allowSeac: false);
        writer.Write(oldIds, map, WidthOperand, Fallback);

        var strings = new List<byte[]> { "Adobe"u8.ToArray(), "Identity"u8.ToArray() };
        int adobe = CffStandard.StandardStringCount, identity = adobe + 1;
        byte[] nameIndex = CffEncoding.Index([System.Text.Encoding.Latin1.GetBytes((options.FontNamePrefix ?? string.Empty) + face.PostScriptName)]);
        byte[] stringIndex = CffEncoding.Index(strings);
        byte[] globalIndex = CffEncoding.Index(writer.GlobalSubrs);
        byte[] charStringIndex = CffEncoding.Index(writer.Glyphs);

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

        // Private DICTs with the blends resolved, without vsindex; each followed by its local subroutines.
        HashSet<int> offsets = [CffFont.OpCharset, CffFont.OpCharStrings, CffFont.OpPrivate, CffFont.OpFdArray, CffFont.OpFdSelect, CffFont.OpSubrs];
        var privates = new List<(byte[] Dict, byte[] Subrs)>();
        foreach (int fd in fds)
        {
            List<(int, double[])> entries = PrivateAt(cff, fd, scalars, options.DropHinting);
            (int defaultWidth, int nominal) = widthDefaults[fd];
            if (defaultWidth != 0)
            {
                entries.Add((CffFont.OpDefaultWidthX, [defaultWidth]));
            }

            if (nominal != 0)
            {
                entries.Add((CffFont.OpNominalWidthX, [nominal]));
            }

            List<byte[]> subrs = writer.LocalSubrs.GetValueOrDefault(fd) ?? [];
            byte[] dict = CffEncoding.Dict(entries);
            if (subrs.Count > 0)
            {
                entries.Add((CffFont.OpSubrs, [0]));
                dict = CffEncoding.Dict(entries, offsets);
                entries[^1] = (CffFont.OpSubrs, [dict.Length]);
                dict = CffEncoding.Dict(entries, offsets);
            }

            privates.Add((dict, subrs.Count > 0 ? CffEncoding.Index(subrs) : []));
        }

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
            foreach ((byte[] dict, byte[] subrs) in privates)
            {
                dicts.Add(CffEncoding.Dict([(CffFont.OpPrivate, [dict.Length, p])], offsets));
                p += dict.Length + subrs.Length;
            }

            return dicts;
        }

        int fdArrayAt = at;
        byte[] fdArray = CffEncoding.Index(FdDicts(0));
        at += fdArray.Length;
        fdArray = CffEncoding.Index(FdDicts(at));
        byte[] topIndex = CffEncoding.Index([CffEncoding.Dict(TopEntries(charsetAt, fdSelectAt, charStringsAt, fdArrayAt), offsets)]);
        var w = new ByteWriter(at + privates.Sum(p => p.Dict.Length + p.Subrs.Length));
        w.Bytes([1, 0, 4, 4]);
        w.Bytes(nameIndex);
        w.Bytes(topIndex);
        w.Bytes(stringIndex);
        w.Bytes(globalIndex);
        w.Bytes(charset.Written);
        w.Bytes(fdSelect.Written);
        w.Bytes(charStringIndex);
        w.Bytes(fdArray);
        foreach ((byte[] dict, byte[] subrs) in privates)
        {
            w.Bytes(dict);
            w.Bytes(subrs);
        }

        return w.ToArray();
    }

    /// <summary>
    /// Private DICT entries that only hinting uses, which a subset without hints leaves out: the blue zones, StdHW, StdVW,
    /// BlueScale, BlueShift, BlueFuzz, the stem snaps and ForceBold.
    /// </summary>
    public static readonly HashSet<int> HintingPrivateEntries = [6, 7, 8, 9, 10, 11, 1209, 1210, 1211, 1212, 1213, 1214];

    // Private DICT operators whose values are delta-encoded positions, and those whose values are whole units.
    private static readonly HashSet<int> s_deltaArrays = [6, 7, 8, 9, 1212, 1213];
    private static readonly HashSet<int> s_wholeUnits = [10, 11, 1210, 1211];

    // A CFF2 Private DICT at the position: blends resolved, positions and widths rounded, without vsindex, subroutines and widths.
    private static List<(int, double[])> PrivateAt(CffFont cff, int fd, float[]? scalars, bool dropHinting)
    {
        PrivateData priv = cff.Privates[fd];
        CffDict dict = priv.Dict;
        if (priv.PrivateOffset >= 0 && cff.VariationStore is ItemVariationStore store)
        {
            float[] Weights(int vsindex)
            {
                ReadOnlySpan<ushort> regions = store.RegionIndicesOf(vsindex);
                float[] weights = new float[regions.Length];
                for (int j = 0; j < regions.Length; j++)
                {
                    weights[j] = scalars is not null && regions[j] < scalars.Length ? scalars[regions[j]] : 0;
                }

                return weights;
            }

            dict = CffDict.Read(cff.Data.Span.Slice(priv.PrivateOffset, priv.PrivateSize), priv.PrivateOffset, store.RegionCountOf, Weights);
        }

        var entries = new List<(int, double[])>();
        foreach ((int op, double[] values) in dict.Entries)
        {
            if (op is CffFont.OpSubrs or CffFont.OpVsIndex or CffFont.OpDefaultWidthX or CffFont.OpNominalWidthX || (dropHinting && HintingPrivateEntries.Contains(op)))
            {
                continue;
            }

            double[] written = values;
            if (s_deltaArrays.Contains(op))
            {
                written = new double[values.Length];
                double position = 0, rounded = 0;
                for (int i = 0; i < values.Length; i++)
                {
                    position += values[i];
                    double r = Math.Floor(position + 0.5);
                    written[i] = r - rounded;
                    rounded = r;
                }
            }
            else if (s_wholeUnits.Contains(op))
            {
                written = [.. values.Select(v => Math.Floor(v + 0.5))];
            }

            entries.Add((op, written));
        }

        return entries;
    }

    // The default width (the most common) and the nominal width that writes the other widths in the fewest bytes.
    private static (int Default, int Nominal) ChooseWidths(List<int> widths)
    {
        if (widths.Count == 0)
        {
            return (0, 0);
        }

        var counts = new Dictionary<int, int>();
        foreach (int width in widths)
        {
            counts[width] = counts.GetValueOrDefault(width) + 1;
        }

        int defaultWidth = counts.OrderByDescending(p => p.Value).ThenBy(p => p.Key).First().Key;
        var others = counts.Where(p => p.Key != defaultWidth).ToList();
        if (others.Count == 0)
        {
            return (defaultWidth, 0);
        }

        static int Size(int v) => v is >= -107 and <= 107 ? 1 : v is >= -1131 and <= 1131 ? 2 : 3;
        int bestNominal = 0, bestCost = int.MaxValue;
        foreach (int candidate in others.SelectMany(p => (int[])[p.Key, p.Key - 107, p.Key + 107, p.Key - 1131, p.Key + 1131]).Prepend(0).Distinct())
        {
            int cost = 0;
            foreach ((int width, int count) in others)
            {
                cost += Size(width - candidate) * count;
            }

            if (cost < bestCost || (cost == bestCost && Math.Abs(candidate) < Math.Abs(bestNominal)))
            {
                (bestCost, bestNominal) = (cost, candidate);
            }
        }

        return (defaultWidth, bestNominal);
    }

    /// <summary>fontTools' otRound: half up.</summary>
    public static int Round(float value) => (int)MathF.Floor(value + 0.5f);
}
