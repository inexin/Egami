using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;

namespace Egami.GdiPlus;

/// <summary>
/// The serialized GDI+ forms the EMF+ specification documents ([MS-EMFPLUS] 2.2) that more than the EMF+ player reads: paths,
/// points, rectangles and region trees. Every read is bounded by the data and throws on a short object.
/// </summary>
internal static class GdiPlusData
{
    /// <summary>The point type of a figure's first point.</summary>
    public const byte PointStart = 0;

    /// <summary>The point type of a line's end point.</summary>
    public const byte PointLine = 1;

    /// <summary>The point type of each of a cubic Bezier's three points.</summary>
    public const byte PointBezier = 3;

    /// <summary>The flag on a point that closes its figure.</summary>
    public const byte PointClose = 0x80;

    public static ushort U16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt16LittleEndian(s[at..]);

    public static short I16(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt16LittleEndian(s[at..]);

    public static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    public static int I32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadInt32LittleEndian(s[at..]);

    public static float F32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadSingleLittleEndian(s[at..]);

    /// <summary>
    /// EmfPlusPath: version, count, flags (C: 16-bit points, R: run-length point types, P: relative points), the points, the
    /// types (start 0, line 1, Bezier 3; close-subpath 0x80).
    /// </summary>
    public static Path PathData(ReadOnlySpan<byte> d, ref int at)
    {
        at += 4; // version
        int count = I32(d, at), flags = I32(d, at + 4);
        at += 8;
        if (count < 0 || count > (d.Length - at))
        {
            throw new ArgumentException("An EMF+ path's point count does not fit its data.");
        }

        Vector2[] points = ReadPoints(d, ref at, count, (flags & 0x4000) != 0, (flags & 0x0800) != 0);
        byte[] types = new byte[count];
        if ((flags & 0x1000) != 0)
        {
            int filled = 0;
            while (filled < count && at + 2 <= d.Length)
            {
                int run = d[at] & 0x3F;
                byte value = d[at + 1];
                at += 2;
                for (int i = 0; i < run && filled < count; i++)
                {
                    types[filled++] = value;
                }
            }
        }
        else
        {
            d.Slice(at, count).CopyTo(types);
            at += count;
        }

        at = (at + 3) & ~3;
        return BuildPath(points, types);
    }

    /// <summary>Builds a path from GDI+ points and point types.</summary>
    public static Path BuildPath(ReadOnlySpan<Vector2> points, ReadOnlySpan<byte> types, FillRule rule = FillRule.EvenOdd)
    {
        PathBuilder b = new();
        int i = 0;
        while (i < points.Length)
        {
            int kind = types[i] & 0x07;
            if (kind == PointStart || b.IsEmpty)
            {
                b.MoveTo(points[i]);
                i++;
            }
            else if (kind == PointBezier && i + 2 < points.Length)
            {
                b.CubicTo(points[i], points[i + 1], points[i + 2]);
                i += 3;
            }
            else
            {
                b.LineTo(points[i]);
                i++;
            }

            if ((types[i - 1] & PointClose) != 0)
            {
                b.Close();
            }
        }

        return b.ToPath(rule);
    }

    /// <summary>EmfPlusPointF, EmfPlusPoint (16-bit) or EmfPlusPointR (relative, 7- or 15-bit) points.</summary>
    public static Vector2[] ReadPoints(ReadOnlySpan<byte> d, ref int at, int count, bool compressed, bool relative)
    {
        if (count < 0 || count > d.Length - at)
        {
            throw new ArgumentException("An EMF+ point count does not fit its data.");
        }

        Vector2[] points = new Vector2[count];
        if (relative)
        {
            Vector2 last = Vector2.Zero;
            for (int i = 0; i < count; i++)
            {
                float x = Relative(d, ref at), y = Relative(d, ref at);
                last += new Vector2(x, y);
                points[i] = last;
            }

            at = (at + 3) & ~3;
            return points;
        }

        int size = compressed ? 4 : 8;
        if (count > (d.Length - at) / size)
        {
            throw new ArgumentException("EMF+ points do not fit their data.");
        }

        for (int i = 0; i < count; i++)
        {
            points[i] = compressed ? new Vector2(I16(d, at), I16(d, at + 2)) : new Vector2(F32(d, at), F32(d, at + 4));
            at += size;
        }

        return points;
    }

    /// <summary>EmfPlusInteger7 or EmfPlusInteger15: a byte with its top bit clear holds a signed 7-bit value, else two bytes a 15-bit one.</summary>
    private static float Relative(ReadOnlySpan<byte> d, ref int at)
    {
        byte first = d[at++];
        if ((first & 0x80) == 0)
        {
            return (sbyte)(first << 1) >> 1;
        }

        int value = ((first & 0x7F) << 8) | d[at++];
        return (short)(value << 1) >> 1;
    }

    /// <summary>EmfPlusRectF or, compressed, EmfPlusRect (16-bit).</summary>
    public static RectF Rect(ReadOnlySpan<byte> d, ref int at, bool compressed)
    {
        RectF r = compressed
            ? new RectF(I16(d, at), I16(d, at + 2), I16(d, at + 4), I16(d, at + 6))
            : new RectF(F32(d, at), F32(d, at + 4), F32(d, at + 8), F32(d, at + 12));
        at += compressed ? 8 : 16;
        return r;
    }

    /// <summary>EmfPlusRegion: version, node count, then the node tree, as a path in world units, or null for an infinite region.</summary>
    public static Path? Region(ReadOnlySpan<byte> d, ref int at)
    {
        at += 8; // version, node count
        return RegionNode(d, ref at, 0);
    }

    /// <summary>A region node: a combine of two children, a rectangle, a path, empty, or infinite (null).</summary>
    private static Path? RegionNode(ReadOnlySpan<byte> d, ref int at, int depth)
    {
        if (depth > 64)
        {
            throw new ArgumentException("An EMF+ region is nested too deeply.");
        }

        uint type = U32(d, at);
        at += 4;
        switch (type)
        {
            case 0x10000000:
                return Path.Rect(Rect(d, ref at, false));
            case 0x10000001:
                {
                    int size = I32(d, at);
                    at += 4;
                    int end = at + size;
                    Path p = PathData(d, ref at);
                    at = end;
                    return p;
                }

            case 0x10000002:
                return Path.Empty;
            case 0x10000003:
                return null;
            case >= 1 and <= 6:
                {
                    Path? left = RegionNode(d, ref at, depth + 1), right = RegionNode(d, ref at, depth + 1);
                    return GdiPlusRegions.Combine(left, right, (int)type);
                }

            default:
                throw new ArgumentException($"EMF+ region node type 0x{type:X8} is not known.");
        }
    }
}
