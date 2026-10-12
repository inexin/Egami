using System.Numerics;

using Egami.Fonts.Parsing;
using Egami.Fonts.Variations;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Fonts.Color;

/// <summary>
/// The color table ('COLR', versions 0 and 1, OpenType 1.9.1): base glyph and layer records, the BaseGlyphList, LayerList and
/// ClipList, every paint format with its variable form, read into <see cref="ColorPaint"/> trees with deltas taken at a position
/// and palette colors resolved. Paint graphs deeper than 64 levels or with cycles are cut where they go wrong.
/// </summary>
internal sealed class ColrTable
{
    public const int MaxDepth = 64;
    private const uint NoVariation = 0xFFFFFFFF;

    private static readonly BlendMode[] s_modes =
    [
        BlendMode.Clear, BlendMode.Source, BlendMode.Destination, BlendMode.SourceOver, BlendMode.DestinationOver, BlendMode.SourceIn,
        BlendMode.DestinationIn, BlendMode.SourceOut, BlendMode.DestinationOut, BlendMode.SourceAtop, BlendMode.DestinationAtop,
        BlendMode.Xor, BlendMode.Plus, BlendMode.Screen, BlendMode.Overlay, BlendMode.Darken, BlendMode.Lighten, BlendMode.ColorDodge,
        BlendMode.ColorBurn, BlendMode.HardLight, BlendMode.SoftLight, BlendMode.Difference, BlendMode.Exclusion, BlendMode.Multiply,
        BlendMode.Hue, BlendMode.Saturation, BlendMode.Color, BlendMode.Luminosity,
    ];

    private readonly ReadOnlyMemory<byte> _data;
    private readonly long _offset;
    private readonly int _baseRecords;
    private readonly int _baseCount;
    private readonly int _layerRecords;
    private readonly int _layerCount;
    private readonly int _baseList;
    private readonly int _baseListCount;
    private readonly int _layerList;
    private readonly int _layerListCount;
    private readonly int _clipList;
    private readonly DeltaSetIndexMap? _varIndexMap;

    private ColrTable(ReadOnlyMemory<byte> data, long offset, int version, int baseRecords, int baseCount, int layerRecords, int layerCount, int baseList, int baseListCount, int layerList, int layerListCount, int clipList, DeltaSetIndexMap? varIndexMap, ItemVariationStore? store)
    {
        _data = data;
        _offset = offset;
        Version = version;
        _baseRecords = baseRecords;
        _baseCount = baseCount;
        _layerRecords = layerRecords;
        _layerCount = layerCount;
        _baseList = baseList;
        _baseListCount = baseListCount;
        _layerList = layerList;
        _layerListCount = layerListCount;
        _clipList = clipList;
        _varIndexMap = varIndexMap;
        Store = store;
    }

    public int Version { get; }

    public bool HasV0 => _baseCount > 0;

    public bool HasV1 => _baseListCount > 0;

    public ItemVariationStore? Store { get; }

    public static ColrTable Read(ReadOnlyMemory<byte> table, long offset)
    {
        ReadOnlySpan<byte> data = table.Span;
        var r = new FontReader(data, offset);
        int version = r.ReadUInt16();
        int baseCount = r.ReadUInt16();
        int baseRecords = (int)r.ReadUInt32();
        int layerRecords = (int)r.ReadUInt32();
        int layerCount = r.ReadUInt16();
        if ((long)baseRecords + (baseCount * 6L) > data.Length || (long)layerRecords + (layerCount * 4L) > data.Length)
        {
            throw FontErrors.Corrupt(offset, "The COLR base glyph or layer records run past the end of the table.");
        }

        int baseList = 0, baseListCount = 0, layerList = 0, layerListCount = 0, clipList = 0;
        DeltaSetIndexMap? map = null;
        ItemVariationStore? store = null;
        if (version >= 1)
        {
            baseList = (int)r.ReadUInt32();
            layerList = (int)r.ReadUInt32();
            clipList = (int)r.ReadUInt32();
            int mapOffset = (int)r.ReadUInt32();
            int storeOffset = (int)r.ReadUInt32();
            if (baseList != 0)
            {
                baseListCount = (int)Math.Min(BigEndian.UInt32(data, baseList), (uint)((data.Length - baseList - 4) / 6));
            }

            if (layerList != 0)
            {
                layerListCount = (int)Math.Min(BigEndian.UInt32(data, layerList), (uint)((data.Length - layerList - 4) / 4));
            }

            map = mapOffset != 0 ? DeltaSetIndexMap.Read(data, mapOffset, offset) : null;
            store = storeOffset != 0 ? ItemVariationStore.Read(table, storeOffset, offset) : null;
        }

        return new ColrTable(table, offset, version, baseRecords, baseCount, layerRecords, layerCount, baseList, baseListCount, layerList, layerListCount, clipList, map, store);
    }

    /// <summary>Gets whether the glyph has a version 1 paint graph.</summary>
    public bool HasV1Glyph(int glyph) => FindV1(glyph) >= 0;

    /// <summary>Gets whether the glyph has version 0 layers.</summary>
    public bool HasV0Glyph(int glyph) => FindV0(glyph, out _, out _);

    /// <summary>Reads a glyph's version 1 paint graph, or <see langword="null"/>.</summary>
    public PaintColorGlyph? ReadV1(ushort glyph, ColorContext context)
    {
        int paint = FindV1(glyph);
        if (paint < 0)
        {
            return null;
        }

        var reader = new PaintReader(this, context);
        ColorPaint? root = reader.Paint(paint, 0);
        return root is null ? null : new PaintColorGlyph(glyph, ColorGlyphFormats.ColrV1, root, ClipBox(glyph, context));
    }

    /// <summary>Reads a glyph's version 0 layers as layers of solid glyph fills, or <see langword="null"/>.</summary>
    public PaintColorGlyph? ReadV0(ushort glyph, ColorContext context)
    {
        if (!FindV0(glyph, out int first, out int count))
        {
            return null;
        }

        ReadOnlySpan<byte> data = _data.Span;
        var layers = new List<ColorPaint>(count);
        for (int i = 0; i < count && first + i < _layerCount; i++)
        {
            int at = _layerRecords + ((first + i) * 4);
            ushort layerGlyph = BigEndian.UInt16(data, at);
            int palette = BigEndian.UInt16(data, at + 2);
            layers.Add(new ColorGlyphPaint(layerGlyph, new ColorSolidPaint(context.Color(palette, 1))));
        }

        return new PaintColorGlyph(glyph, ColorGlyphFormats.ColrV0, new ColorLayersPaint(layers), null);
    }

    private int FindV1(int glyph)
    {
        if (_baseListCount == 0)
        {
            return -1;
        }

        ReadOnlySpan<byte> data = _data.Span;
        int lo = 0, hi = _baseListCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int at = _baseList + 4 + (mid * 6);
            int g = BigEndian.UInt16(data, at);
            if (g == glyph)
            {
                uint paint = BigEndian.UInt32(data, at + 2);
                return paint == 0 || _baseList + (long)paint >= data.Length ? -1 : _baseList + (int)paint;
            }

            if (g < glyph)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        return -1;
    }

    private bool FindV0(int glyph, out int first, out int count)
    {
        ReadOnlySpan<byte> data = _data.Span;
        int lo = 0, hi = _baseCount - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int at = _baseRecords + (mid * 6);
            int g = BigEndian.UInt16(data, at);
            if (g == glyph)
            {
                first = BigEndian.UInt16(data, at + 2);
                count = BigEndian.UInt16(data, at + 4);
                return count > 0;
            }

            if (g < glyph)
            {
                lo = mid + 1;
            }
            else
            {
                hi = mid - 1;
            }
        }

        first = count = 0;
        return false;
    }

    private GlyphExtents? ClipBox(int glyph, ColorContext context)
    {
        if (_clipList == 0)
        {
            return null;
        }

        ReadOnlySpan<byte> data = _data.Span;
        if (BigEndian.UInt8(data, _clipList) != 1)
        {
            return null;
        }

        int count = (int)Math.Min(BigEndian.UInt32(data, _clipList + 1), (uint)((data.Length - _clipList - 5) / 7));
        int lo = 0, hi = count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            int at = _clipList + 5 + (mid * 7);
            int start = BigEndian.UInt16(data, at), end = BigEndian.UInt16(data, at + 2);
            if (glyph < start)
            {
                hi = mid - 1;
            }
            else if (glyph > end)
            {
                lo = mid + 1;
            }
            else
            {
                int box = _clipList + (int)BigEndian.UInt24(data, at + 4);
                int format = BigEndian.UInt8(data, box);
                float x0 = BigEndian.Int16(data, box + 1), y0 = BigEndian.Int16(data, box + 3);
                float x1 = BigEndian.Int16(data, box + 5), y1 = BigEndian.Int16(data, box + 7);
                if (format == 2)
                {
                    uint baseIndex = BigEndian.UInt32(data, box + 9);
                    x0 += Delta(baseIndex, 0, context);
                    y0 += Delta(baseIndex, 1, context);
                    x1 += Delta(baseIndex, 2, context);
                    y1 += Delta(baseIndex, 3, context);
                }

                return new GlyphExtents(x0, y0, x1, y1);
            }
        }

        return null;
    }

    private float Delta(uint varIndexBase, int field, ColorContext context)
    {
        if (varIndexBase == NoVariation || Store is null || context.Scalars is not float[] scalars)
        {
            return 0;
        }

        uint index = varIndexBase + (uint)field;
        uint packed = _varIndexMap?.Get((int)Math.Min(index, int.MaxValue)) ?? index;
        return Store.GetDelta(packed, scalars);
    }

    /// <summary>Reads one glyph's paint graph, tracking depth and the paints on the current path to cut cycles.</summary>
    private sealed class PaintReader(ColrTable table, ColorContext context)
    {
        private static readonly ColorLayersPaint Nothing = new([]);

        private readonly HashSet<int> _open = [];

        public ColorPaint? Paint(int offset, int depth)
        {
            // A cut part of the graph draws nothing; the rest of the glyph still draws.
            if (depth > MaxDepth)
            {
                context.Report($"A COLR paint graph is deeper than {MaxDepth} levels; the rest is left out.");
                return Nothing;
            }

            if (!_open.Add(offset))
            {
                context.Report("A COLR paint graph refers back to itself; the cycle is cut.");
                return Nothing;
            }

            try
            {
                return Read(offset, depth);
            }
            finally
            {
                _open.Remove(offset);
            }
        }

        private ColorPaint? Read(int at, int depth)
        {
            ReadOnlySpan<byte> data = table._data.Span;
            if ((uint)at >= (uint)data.Length)
            {
                throw FontErrors.Corrupt(table._offset + at, "A COLR paint lies outside the table.");
            }

            int format = data[at];
            switch (format)
            {
                case 1:
                    {
                        int count = BigEndian.UInt8(data, at + 1);
                        uint first = BigEndian.UInt32(data, at + 2);
                        var layers = new List<ColorPaint>(count);
                        for (int i = 0; i < count; i++)
                        {
                            long index = first + (long)i;
                            if (index >= table._layerListCount)
                            {
                                throw FontErrors.Corrupt(table._offset + at, "A COLR layers paint runs past the LayerList.");
                            }

                            int layer = table._layerList + (int)BigEndian.UInt32(data, table._layerList + 4 + ((int)index * 4));
                            if (Paint(layer, depth + 1) is ColorPaint paint)
                            {
                                layers.Add(paint);
                            }
                        }

                        return new ColorLayersPaint(layers);
                    }

                case 2:
                case 3:
                    {
                        int palette = BigEndian.UInt16(data, at + 1);
                        float alpha = F2Dot14(data, at + 3);
                        if (format == 3)
                        {
                            alpha += Delta(data, at + 5, 0) / 16384f;
                        }

                        return new ColorSolidPaint(context.Color(palette, alpha));
                    }

                case 4:
                case 5:
                    {
                        ColorLine line = Line(at + (int)BigEndian.UInt24(data, at + 1), format == 5);
                        float[] v = Values(data, at + 4, 6, format == 5 ? at + 16 : -1, 1);
                        return new ColorLinearGradientPaint(new Vector2(v[0], v[1]), new Vector2(v[2], v[3]), new Vector2(v[4], v[5]), line);
                    }

                case 6:
                case 7:
                    {
                        ColorLine line = Line(at + (int)BigEndian.UInt24(data, at + 1), format == 7);
                        float x0 = BigEndian.Int16(data, at + 4), y0 = BigEndian.Int16(data, at + 6), r0 = BigEndian.UInt16(data, at + 8);
                        float x1 = BigEndian.Int16(data, at + 10), y1 = BigEndian.Int16(data, at + 12), r1 = BigEndian.UInt16(data, at + 14);
                        if (format == 7)
                        {
                            x0 += Delta(data, at + 16, 0);
                            y0 += Delta(data, at + 16, 1);
                            r0 += Delta(data, at + 16, 2);
                            x1 += Delta(data, at + 16, 3);
                            y1 += Delta(data, at + 16, 4);
                            r1 += Delta(data, at + 16, 5);
                        }

                        return new ColorRadialGradientPaint(new Vector2(x0, y0), Math.Max(0, r0), new Vector2(x1, y1), Math.Max(0, r1), line);
                    }

                case 8:
                case 9:
                    {
                        ColorLine line = Line(at + (int)BigEndian.UInt24(data, at + 1), format == 9);
                        float cx = BigEndian.Int16(data, at + 4), cy = BigEndian.Int16(data, at + 6);
                        float start = F2Dot14(data, at + 8), end = F2Dot14(data, at + 10);
                        if (format == 9)
                        {
                            cx += Delta(data, at + 12, 0);
                            cy += Delta(data, at + 12, 1);
                            start += Delta(data, at + 12, 2) / 16384f;
                            end += Delta(data, at + 12, 3) / 16384f;
                        }

                        // Sweep angles are biased: add 1, then 180 degrees per unit.
                        return new ColorSweepGradientPaint(new Vector2(cx, cy), (start + 1) * 180, (end + 1) * 180, line);
                    }

                case 10:
                    {
                        int child = at + (int)BigEndian.UInt24(data, at + 1);
                        ushort glyph = BigEndian.UInt16(data, at + 4);
                        return Paint(child, depth + 1) is ColorPaint paint ? new ColorGlyphPaint(glyph, paint) : null;
                    }

                case 11:
                    {
                        ushort glyph = BigEndian.UInt16(data, at + 1);
                        int paint = table.FindV1(glyph);
                        if (paint < 0)
                        {
                            return null;
                        }

                        return Paint(paint, depth + 1) is ColorPaint reused ? new ColorGlyphReferencePaint(glyph, reused) : null;
                    }

                case 12:
                case 13:
                    {
                        int child = at + (int)BigEndian.UInt24(data, at + 1);
                        int affine = at + (int)BigEndian.UInt24(data, at + 4);
                        float[] m = new float[6];
                        for (int i = 0; i < 6; i++)
                        {
                            m[i] = (int)BigEndian.UInt32(data, affine + (i * 4)) / 65536f;
                            if (format == 13)
                            {
                                m[i] += Delta(data, affine + 24, i) / 65536f;
                            }
                        }

                        return Transformed(child, depth, new Matrix3x2(m[0], m[1], m[2], m[3], m[4], m[5]));
                    }

                case 14:
                case 15:
                    {
                        float[] v = Values(data, at + 4, 2, format == 15 ? at + 8 : -1, 1);
                        return Transformed(at + (int)BigEndian.UInt24(data, at + 1), depth, Matrix3x2.CreateTranslation(v[0], v[1]));
                    }

                case >= 16 and <= 23:
                    {
                        bool variable = format % 2 == 1, uniform = format >= 20, center = format is 18 or 19 or 22 or 23;
                        int p = at + 4;
                        int scales = uniform ? 1 : 2;
                        int varAt = p + (scales * 2) + (center ? 4 : 0);
                        float sx = F2Dot14(data, p) + (variable ? Delta(data, varAt, 0) / 16384f : 0);
                        float sy = uniform ? sx : F2Dot14(data, p + 2) + (variable ? Delta(data, varAt, 1) / 16384f : 0);
                        Vector2 c = Vector2.Zero;
                        if (center)
                        {
                            int ca = p + (scales * 2);
                            c = new Vector2(
                                BigEndian.Int16(data, ca) + (variable ? Delta(data, varAt, scales) : 0),
                                BigEndian.Int16(data, ca + 2) + (variable ? Delta(data, varAt, scales + 1) : 0));
                        }

                        return Transformed(at + (int)BigEndian.UInt24(data, at + 1), depth, Matrix3x2.CreateScale(sx, sy, c));
                    }

                case >= 24 and <= 27:
                    {
                        bool variable = format % 2 == 1, center = format >= 26;
                        int p = at + 4;
                        int varAt = p + 2 + (center ? 4 : 0);
                        float angle = F2Dot14(data, p) + (variable ? Delta(data, varAt, 0) / 16384f : 0);
                        Vector2 c = Vector2.Zero;
                        if (center)
                        {
                            c = new Vector2(
                                BigEndian.Int16(data, p + 2) + (variable ? Delta(data, varAt, 1) : 0),
                                BigEndian.Int16(data, p + 4) + (variable ? Delta(data, varAt, 2) : 0));
                        }

                        return Transformed(at + (int)BigEndian.UInt24(data, at + 1), depth, Matrix3x2.CreateRotation(angle * MathF.PI, c));
                    }

                case >= 28 and <= 31:
                    {
                        bool variable = format % 2 == 1, center = format >= 30;
                        int p = at + 4;
                        int varAt = p + 4 + (center ? 4 : 0);
                        float xSkew = F2Dot14(data, p) + (variable ? Delta(data, varAt, 0) / 16384f : 0);
                        float ySkew = F2Dot14(data, p + 2) + (variable ? Delta(data, varAt, 1) / 16384f : 0);
                        Vector2 c = Vector2.Zero;
                        if (center)
                        {
                            c = new Vector2(
                                BigEndian.Int16(data, p + 4) + (variable ? Delta(data, varAt, 2) : 0),
                                BigEndian.Int16(data, p + 6) + (variable ? Delta(data, varAt, 3) : 0));
                        }

                        // COLR skews x by -tan(xSkewAngle) per unit of y and y by tan(ySkewAngle) per unit of x, about the center.
                        var skew = new Matrix3x2(1, MathF.Tan(ySkew * MathF.PI), -MathF.Tan(xSkew * MathF.PI), 1, 0, 0);
                        Matrix3x2 m = Matrix3x2.CreateTranslation(-c) * skew * Matrix3x2.CreateTranslation(c);
                        return Transformed(at + (int)BigEndian.UInt24(data, at + 1), depth, m);
                    }

                case 32:
                    {
                        int source = at + (int)BigEndian.UInt24(data, at + 1);
                        int mode = BigEndian.UInt8(data, at + 4);
                        int backdrop = at + (int)BigEndian.UInt24(data, at + 5);
                        ColorPaint? s = Paint(source, depth + 1), b = Paint(backdrop, depth + 1);
                        if (mode >= s_modes.Length)
                        {
                            context.Report($"A COLR composite uses the unknown mode {mode}; it is drawn source-over.");
                        }

                        BlendMode blend = mode < s_modes.Length ? s_modes[mode] : BlendMode.SourceOver;
                        return s is null && b is null ? null : new ColorCompositePaint(blend, s ?? new ColorLayersPaint([]), b ?? new ColorLayersPaint([]));
                    }

                default:
                    context.Report($"A COLR paint has the unknown format {format}; it is left out.");
                    return null;
            }
        }

        private ColorTransformPaint? Transformed(int child, int depth, Matrix3x2 matrix)
            => Paint(child, depth + 1) is ColorPaint paint ? new ColorTransformPaint(matrix, paint) : null;

        private ColorLine Line(int at, bool variable)
        {
            ReadOnlySpan<byte> data = table._data.Span;
            int extend = BigEndian.UInt8(data, at);
            int count = BigEndian.UInt16(data, at + 1);
            int size = variable ? 10 : 6;
            if (at + 3 + ((long)count * size) > data.Length)
            {
                throw FontErrors.Corrupt(table._offset + at, "A COLR color line runs past the end of the table.");
            }

            var stops = new ColorStop[count];
            for (int i = 0; i < count; i++)
            {
                int s = at + 3 + (i * size);
                float offset = F2Dot14(data, s);
                int palette = BigEndian.UInt16(data, s + 2);
                float alpha = F2Dot14(data, s + 4);
                if (variable)
                {
                    offset += Delta(data, s + 6, 0) / 16384f;
                    alpha += Delta(data, s + 6, 1) / 16384f;
                }

                stops[i] = new ColorStop(offset, context.Color(palette, alpha));
            }

            // Stops are in offset order; equal offsets keep their order, as the spec asks.
            ColorStop[] sorted = [.. stops.Select((stop, i) => (stop, i)).OrderBy(p => p.stop.Offset).ThenBy(p => p.i).Select(p => p.stop)];
            ColorExtend kind = extend switch
            {
                1 => ColorExtend.Repeat,
                2 => ColorExtend.Reflect,
                _ => ColorExtend.Pad,
            };
            return new ColorLine(sorted, kind);
        }

        // Reads count FWORDs from at, each plus its delta when varAt points at a varIndexBase.
        private float[] Values(ReadOnlySpan<byte> data, int at, int count, int varAt, float unit)
        {
            var values = new float[count];
            for (int i = 0; i < count; i++)
            {
                values[i] = BigEndian.Int16(data, at + (i * 2)) + (varAt >= 0 ? Delta(data, varAt, i) / unit : 0);
            }

            return values;
        }

        private float Delta(ReadOnlySpan<byte> data, int varIndexBaseAt, int field) => table.Delta(BigEndian.UInt32(data, varIndexBaseAt), field, context);

        private static float F2Dot14(ReadOnlySpan<byte> data, int at) => BigEndian.Int16(data, at) / 16384f;
    }
}

/// <summary>What reading a color glyph needs: the palette's colors, the position's region scalars and where problems go.</summary>
internal sealed class ColorContext(Rgba8[] palette, float[]? scalars, Action<string> report)
{
    public float[]? Scalars { get; } = scalars;

    public void Report(string message) => report(message);

    /// <summary>A palette entry with an alpha factor; 0xFFFF is the foreground, an entry past the palette transparent black.</summary>
    public GlyphColor Color(int index, float alpha)
    {
        alpha = Math.Clamp(alpha, 0, 1);
        if (index == 0xFFFF)
        {
            return new GlyphColor(new RgbaF32(0, 0, 0, alpha), true);
        }

        if ((uint)index >= (uint)palette.Length)
        {
            return new GlyphColor(new RgbaF32(0, 0, 0, 0), false);
        }

        RgbaF32 c = RgbaF32.FromRgba8(palette[index]);
        return new GlyphColor(c with { A = c.A * alpha }, false);
    }
}
