using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Decodes the ETC1, ETC2 and EAC blocks, written from the Khronos Data Format Specification (its ETC1, ETC2 and EAC chapters).
/// Each block is 4 × 4 texels, decoded into 16 values in row order. ETC1 decodes as the ETC2 RGB block it is a subset of: the
/// T, H and planar modes ETC2 adds are differential blocks whose second colour overflows, which ETC1 leaves undefined.
/// </summary>
internal static class Etc
{
    /// <summary>The intensity modifier tables of the individual and differential modes: the small and large value of each.</summary>
    internal static ReadOnlySpan<byte> Modifiers => [2, 8, 5, 17, 9, 29, 13, 42, 18, 60, 24, 80, 33, 106, 47, 183];

    /// <summary>The distances of the T and H modes.</summary>
    internal static ReadOnlySpan<byte> Distances => [3, 6, 11, 16, 23, 32, 41, 64];

    /// <summary>The EAC modifier tables, 8 values each.</summary>
    internal static ReadOnlySpan<sbyte> EacTables =>
    [
        -3, -6, -9, -15, 2, 5, 8, 14,
        -3, -7, -10, -13, 2, 6, 9, 12,
        -2, -5, -8, -13, 1, 4, 7, 12,
        -2, -4, -6, -13, 1, 3, 5, 12,
        -3, -6, -8, -12, 2, 5, 7, 11,
        -3, -7, -9, -11, 2, 6, 8, 10,
        -4, -7, -8, -11, 3, 6, 7, 10,
        -3, -5, -8, -11, 2, 4, 7, 10,
        -2, -6, -8, -10, 1, 5, 7, 9,
        -2, -5, -8, -10, 1, 4, 7, 9,
        -2, -4, -8, -10, 1, 3, 7, 9,
        -2, -5, -7, -10, 1, 4, 6, 9,
        -3, -4, -7, -10, 2, 3, 6, 9,
        -1, -2, -3, -10, 0, 1, 2, 9,
        -4, -6, -8, -9, 3, 5, 7, 8,
        -3, -5, -7, -9, 2, 4, 6, 8,
    ];

    /// <summary>
    /// Decodes an ETC1 or ETC2 colour block (8 bytes) into 16 pixels of <paramref name="pixel"/> bytes (3 for RGB, 4 for RGBA,
    /// whose alpha is set to 255 unless <paramref name="punchThrough"/> makes a texel transparent). With
    /// <paramref name="punchThrough"/> (ETC2 RGB8A1) the differential bit is the opaque bit and there is no individual mode.
    /// </summary>
    public static void DecodeRgb(ReadOnlySpan<byte> block, Span<byte> pixels, int pixel, bool punchThrough)
    {
        ulong bits = BinaryPrimitives.ReadUInt64BigEndian(block);
        uint hi = (uint)(bits >> 32);
        uint indices = (uint)bits;
        bool differential = punchThrough || ((hi >> 1) & 1) != 0;
        bool opaque = !punchThrough || ((hi >> 1) & 1) != 0;
        if (!differential)
        {
            int r1 = Extend4((int)(hi >> 28) & 15), r2 = Extend4((int)(hi >> 24) & 15);
            int g1 = Extend4((int)(hi >> 20) & 15), g2 = Extend4((int)(hi >> 16) & 15);
            int b1 = Extend4((int)(hi >> 12) & 15), b2 = Extend4((int)(hi >> 8) & 15);
            Subblocks(hi, indices, r1, g1, b1, r2, g2, b2, true, pixels, pixel);
            return;
        }

        int r = (int)(hi >> 27) & 31, g = (int)(hi >> 19) & 31, b = (int)(hi >> 11) & 31;
        int rd = r + SignExtend3((int)(hi >> 24) & 7);
        int gd = g + SignExtend3((int)(hi >> 16) & 7);
        int bd = b + SignExtend3((int)(hi >> 8) & 7);
        if ((uint)rd > 31)
        {
            TMode(hi, indices, opaque, pixels, pixel);
        }
        else if ((uint)gd > 31)
        {
            HMode(hi, indices, opaque, pixels, pixel);
        }
        else if ((uint)bd > 31)
        {
            Planar(bits, pixels, pixel);
        }
        else
        {
            Subblocks(hi, indices, Extend5(r), Extend5(g), Extend5(b), Extend5(rd), Extend5(gd), Extend5(bd), opaque, pixels, pixel);
        }
    }

    /// <summary>
    /// Decodes an EAC block (8 bytes) of 8-bit values, the alpha of ETC2 RGBA8, into 16 bytes, every <paramref name="stride"/>th
    /// byte of <paramref name="values"/>.
    /// </summary>
    public static void DecodeEac8(ReadOnlySpan<byte> block, Span<byte> values, int stride)
    {
        ulong bits = BinaryPrimitives.ReadUInt64BigEndian(block);
        int baseValue = (int)(bits >> 56);
        int multiplier = (int)(bits >> 52) & 15;
        ReadOnlySpan<sbyte> table = EacTables.Slice(((int)(bits >> 48) & 15) * 8, 8);
        for (int k = 0; k < 16; k++)
        {
            int index = (int)(bits >> (45 - (3 * k))) & 7;
            values[Position(k) * stride] = (byte)Math.Clamp(baseValue + (table[index] * multiplier), 0, 255);
        }
    }

    /// <summary>
    /// Decodes an EAC R11 block (8 bytes) into 16 16-bit values, every <paramref name="stride"/>th value of
    /// <paramref name="values"/>. Unsigned values widen to 16 bits by bit replication; signed ones (−1023 to 1023) map −1 to 1
    /// onto 0 to 65535, rounding once.
    /// </summary>
    public static void DecodeEac11(ReadOnlySpan<byte> block, Span<ushort> values, int stride, bool signed)
    {
        ulong bits = BinaryPrimitives.ReadUInt64BigEndian(block);
        int multiplier = (int)(bits >> 52) & 15;
        ReadOnlySpan<sbyte> table = EacTables.Slice(((int)(bits >> 48) & 15) * 8, 8);
        if (signed)
        {
            int baseValue = Math.Max((int)(sbyte)(bits >> 56), -127) * 8;
            for (int k = 0; k < 16; k++)
            {
                int t = table[(int)(bits >> (45 - (3 * k))) & 7];
                int v = Math.Clamp(baseValue + (multiplier == 0 ? t : t * multiplier * 8), -1023, 1023);
                values[Position(k) * stride] = (ushort)((((v + 1023) * 65535 * 2) + 2046) / 4092);
            }
        }
        else
        {
            int baseValue = ((int)(bits >> 56) * 8) + 4;
            for (int k = 0; k < 16; k++)
            {
                int t = table[(int)(bits >> (45 - (3 * k))) & 7];
                int v = Math.Clamp(baseValue + (multiplier == 0 ? t : t * multiplier * 8), 0, 2047);
                values[Position(k) * stride] = (ushort)((v << 5) | (v >> 6));
            }
        }
    }

    /// <summary>Returns the row-order position of the texel the block's column-order index <paramref name="k"/> names.</summary>
    private static int Position(int k) => ((k & 3) * 4) + (k >> 2);

    private static void Subblocks(uint hi, uint indices, int r1, int g1, int b1, int r2, int g2, int b2, bool opaque, Span<byte> pixels, int pixel)
    {
        bool flip = (hi & 1) != 0;
        int table1 = (int)(hi >> 5) & 7;
        int table2 = (int)(hi >> 2) & 7;
        for (int k = 0; k < 16; k++)
        {
            int x = k >> 2, y = k & 3;
            bool second = flip ? y >= 2 : x >= 2;
            int table = second ? table2 : table1;
            int index = (int)(((indices >> (16 + k)) & 1) << 1 | ((indices >> k) & 1));
            Span<byte> p = pixels.Slice(((y * 4) + x) * pixel, pixel);
            if (!opaque && index == 2)
            {
                Transparent(p);
                continue;
            }

            int modifier = index switch
            {
                0 => opaque ? Modifiers[table * 2] : 0,
                1 => Modifiers[(table * 2) + 1],
                2 => -Modifiers[table * 2],
                _ => -Modifiers[(table * 2) + 1],
            };
            Write(p, second ? r2 : r1, second ? g2 : g1, second ? b2 : b1, modifier);
        }
    }

    private static void TMode(uint hi, uint indices, bool opaque, Span<byte> pixels, int pixel)
    {
        int r1 = Extend4((int)((((hi >> 27) & 3) << 2) | ((hi >> 24) & 3)));
        int g1 = Extend4((int)(hi >> 20) & 15), b1 = Extend4((int)(hi >> 16) & 15);
        int r2 = Extend4((int)(hi >> 12) & 15), g2 = Extend4((int)(hi >> 8) & 15), b2 = Extend4((int)(hi >> 4) & 15);
        int d = Distances[(int)((((hi >> 2) & 3) << 1) | (hi & 1))];
        Span<int> paint = stackalloc int[12];
        Set(paint, 0, r1, g1, b1, 0);
        Set(paint, 1, r2, g2, b2, d);
        Set(paint, 2, r2, g2, b2, 0);
        Set(paint, 3, r2, g2, b2, -d);
        Paint(indices, paint, opaque, pixels, pixel);
    }

    private static void HMode(uint hi, uint indices, bool opaque, Span<byte> pixels, int pixel)
    {
        int r1 = (int)(hi >> 27) & 15;
        int g1 = (int)((((hi >> 24) & 7) << 1) | ((hi >> 20) & 1));
        int b1 = (int)((((hi >> 19) & 1) << 3) | ((hi >> 15) & 7));
        int r2 = (int)(hi >> 11) & 15, g2 = (int)(hi >> 7) & 15, b2 = (int)(hi >> 3) & 15;
        int order = ((r1 << 8) | (g1 << 4) | b1) >= ((r2 << 8) | (g2 << 4) | b2) ? 1 : 0;
        int d = Distances[(int)((((hi >> 2) & 1) << 2) | ((hi & 1) << 1)) | order];
        Span<int> paint = stackalloc int[12];
        Set(paint, 0, Extend4(r1), Extend4(g1), Extend4(b1), d);
        Set(paint, 1, Extend4(r1), Extend4(g1), Extend4(b1), -d);
        Set(paint, 2, Extend4(r2), Extend4(g2), Extend4(b2), d);
        Set(paint, 3, Extend4(r2), Extend4(g2), Extend4(b2), -d);
        Paint(indices, paint, opaque, pixels, pixel);
    }

    private static void Planar(ulong bits, Span<byte> pixels, int pixel)
    {
        int ro = Extend6((int)(bits >> 57) & 63);
        int go = Extend7((int)((((bits >> 56) & 1) << 6) | ((bits >> 49) & 63)));
        int bo = Extend6((int)((((bits >> 48) & 1) << 5) | (((bits >> 43) & 3) << 3) | ((bits >> 39) & 7)));
        int rh = Extend6((int)((((bits >> 34) & 31) << 1) | ((bits >> 32) & 1)));
        int gh = Extend7((int)(bits >> 25) & 127);
        int bh = Extend6((int)(bits >> 19) & 63);
        int rv = Extend6((int)(bits >> 13) & 63);
        int gv = Extend7((int)(bits >> 6) & 127);
        int bv = Extend6((int)bits & 63);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                Span<byte> p = pixels.Slice(((y * 4) + x) * pixel, pixel);
                p[0] = Clamp(((x * (rh - ro)) + (y * (rv - ro)) + (4 * ro) + 2) >> 2);
                p[1] = Clamp(((x * (gh - go)) + (y * (gv - go)) + (4 * go) + 2) >> 2);
                p[2] = Clamp(((x * (bh - bo)) + (y * (bv - bo)) + (4 * bo) + 2) >> 2);
                if (pixel == 4)
                {
                    p[3] = 255;
                }
            }
        }
    }

    private static void Set(Span<int> paint, int i, int r, int g, int b, int d)
    {
        paint[i * 3] = Math.Clamp(r + d, 0, 255);
        paint[(i * 3) + 1] = Math.Clamp(g + d, 0, 255);
        paint[(i * 3) + 2] = Math.Clamp(b + d, 0, 255);
    }

    private static void Paint(uint indices, ReadOnlySpan<int> paint, bool opaque, Span<byte> pixels, int pixel)
    {
        for (int k = 0; k < 16; k++)
        {
            int index = (int)(((indices >> (16 + k)) & 1) << 1 | ((indices >> k) & 1));
            Span<byte> p = pixels.Slice(Position(k) * pixel, pixel);
            if (!opaque && index == 2)
            {
                Transparent(p);
                continue;
            }

            p[0] = (byte)paint[index * 3];
            p[1] = (byte)paint[(index * 3) + 1];
            p[2] = (byte)paint[(index * 3) + 2];
            if (pixel == 4)
            {
                p[3] = 255;
            }
        }
    }

    private static void Write(Span<byte> p, int r, int g, int b, int modifier)
    {
        p[0] = Clamp(r + modifier);
        p[1] = Clamp(g + modifier);
        p[2] = Clamp(b + modifier);
        if (p.Length == 4)
        {
            p[3] = 255;
        }
    }

    private static void Transparent(Span<byte> p) => p.Clear();

    private static byte Clamp(int v) => (byte)Math.Clamp(v, 0, 255);

    private static int SignExtend3(int v) => (v << 29) >> 29;

    internal static int Extend4(int v) => v * 17;

    internal static int Extend5(int v) => (v << 3) | (v >> 2);

    internal static int Extend6(int v) => (v << 2) | (v >> 4);

    internal static int Extend7(int v) => (v << 1) | (v >> 6);
}
