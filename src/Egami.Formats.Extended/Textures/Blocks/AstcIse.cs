namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// ASTC's integer sequence encoding, from the Khronos Data Format Specification: the 21 ranges, from 2 to 256 levels, each a
/// count of plain bits with or without a trit (3 levels) or a quint (5 levels) on top, packed five trits to 8 bits and three
/// quints to 7. Shared by the decoder and the encoder, which also uses the unquantisation tables to search.
/// </summary>
internal static class AstcIse
{
    /// <summary>The number of levels of each range, in increasing order.</summary>
    public static ReadOnlySpan<short> Levels => [2, 3, 4, 5, 6, 8, 10, 12, 16, 20, 24, 32, 40, 48, 64, 80, 96, 128, 160, 192, 256];

    /// <summary>The plain bits of each range.</summary>
    public static ReadOnlySpan<byte> Bits => [1, 0, 2, 0, 1, 3, 1, 2, 4, 2, 3, 5, 3, 4, 6, 4, 5, 7, 5, 6, 8];

    /// <summary>Whether each range has a trit (1), a quint (2) or neither (0).</summary>
    public static ReadOnlySpan<byte> Kind => [0, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0, 2, 1, 0];

    /// <summary>The range index of 6 levels, the fewest colour endpoints may use.</summary>
    public const int Range6 = 4;

    private static readonly byte[][] TritTable = BuildTrits();
    private static readonly byte[][] QuintTable = BuildQuints();

    /// <summary>Gets each colour range's unquantised values (0 to 255), indexed [range][value].</summary>
    public static byte[][] ColorUnquantized { get; } = BuildColorUnquantized();

    /// <summary>Gets each weight range's unquantised values (0 to 64), indexed [range][value], for ranges 0 to 11.</summary>
    public static byte[][] WeightUnquantized { get; } = BuildWeightUnquantized();

    /// <summary>Returns the bits <paramref name="count"/> values of range <paramref name="range"/> take.</summary>
    public static int BitCount(int count, int range)
    {
        int bits = count * Bits[range];
        return Kind[range] switch
        {
            1 => bits + (((8 * count) + 4) / 5),
            2 => bits + (((7 * count) + 2) / 3),
            _ => bits,
        };
    }

    /// <summary>
    /// Decodes <paramref name="output"/>.Length values of range <paramref name="range"/> from <paramref name="data"/>, starting
    /// at bit <paramref name="offset"/>. A last, partial trit or quint group reads the bits past the sequence as zero, as the
    /// specification pads it.
    /// </summary>
    public static void Decode(int range, ReadOnlySpan<byte> data, int offset, Span<int> output)
    {
        int bits = Bits[range];
        int count = output.Length;
        int end = offset + BitCount(count, range);
        Span<byte> clean = stackalloc byte[16];
        scoped ReadOnlySpan<byte> source = data;
        if (Kind[range] != 0 && data.Length <= clean.Length && end < data.Length * 8)
        {
            data.CopyTo(clean);
            clean[(end >> 3)] &= (byte)((1 << (end & 7)) - 1);
            clean[((end >> 3) + 1)..].Clear();
            source = clean[..data.Length];
        }
        switch (Kind[range])
        {
            case 1:
                Span<int> m5 = stackalloc int[5];
                for (int i = 0; i < count; i += 5)
                {
                    Span<int> m = m5;
                    int t = 0;
                    m[0] = Read(source, ref offset, bits);
                    t |= Read(source, ref offset, 2);
                    m[1] = Read(source, ref offset, bits);
                    t |= Read(source, ref offset, 2) << 2;
                    m[2] = Read(source, ref offset, bits);
                    t |= Read(source, ref offset, 1) << 4;
                    m[3] = Read(source, ref offset, bits);
                    t |= Read(source, ref offset, 2) << 5;
                    m[4] = Read(source, ref offset, bits);
                    t |= Read(source, ref offset, 1) << 7;
                    byte[] trits = TritTable[t];
                    for (int j = 0; j < 5 && i + j < count; j++)
                    {
                        output[i + j] = (trits[j] << bits) | m[j];
                    }
                }

                break;
            case 2:
                Span<int> m3 = stackalloc int[3];
                for (int i = 0; i < count; i += 3)
                {
                    Span<int> m = m3;
                    int q = 0;
                    m[0] = Read(source, ref offset, bits);
                    q |= Read(source, ref offset, 3);
                    m[1] = Read(source, ref offset, bits);
                    q |= Read(source, ref offset, 2) << 3;
                    m[2] = Read(source, ref offset, bits);
                    q |= Read(source, ref offset, 2) << 5;
                    byte[] quints = QuintTable[q];
                    for (int j = 0; j < 3 && i + j < count; j++)
                    {
                        output[i + j] = (quints[j] << bits) | m[j];
                    }
                }

                break;
            default:
                for (int i = 0; i < count; i++)
                {
                    output[i] = Read(source, ref offset, bits);
                }

                break;
        }
    }

    /// <summary>
    /// Encodes <paramref name="values"/> of range <paramref name="range"/> into <paramref name="data"/> from bit
    /// <paramref name="offset"/>, ORing the bits in (the destination bits must be clear).
    /// </summary>
    public static void Encode(int range, ReadOnlySpan<int> values, Span<byte> data, int offset)
    {
        int bits = Bits[range];
        int count = values.Length;
        int mask = (1 << bits) - 1;
        switch (Kind[range])
        {
            case 1:
                Span<int> t = stackalloc int[5];
                Span<int> tm = stackalloc int[5];
                for (int i = 0; i < count; i += 5)
                {
                    Span<int> m = tm;
                    for (int j = 0; j < 5; j++)
                    {
                        int v = i + j < count ? values[i + j] : 0;
                        t[j] = v >> bits;
                        m[j] = v & mask;
                    }

                    int packed = TritPack[(((((t[4] * 3) + t[3]) * 3) + t[2]) * 3 + t[1]) * 3 + t[0]];
                    int n = Math.Min(5, count - i);
                    Write(data, ref offset, m[0], bits);
                    Write(data, ref offset, packed & 3, 2);
                    if (n == 1)
                    {
                        continue;
                    }

                    Write(data, ref offset, m[1], bits);
                    Write(data, ref offset, (packed >> 2) & 3, 2);
                    if (n == 2)
                    {
                        continue;
                    }

                    Write(data, ref offset, m[2], bits);
                    Write(data, ref offset, (packed >> 4) & 1, 1);
                    if (n == 3)
                    {
                        continue;
                    }

                    Write(data, ref offset, m[3], bits);
                    Write(data, ref offset, (packed >> 5) & 3, 2);
                    if (n == 4)
                    {
                        continue;
                    }

                    Write(data, ref offset, m[4], bits);
                    Write(data, ref offset, (packed >> 7) & 1, 1);
                }

                break;
            case 2:
                Span<int> q = stackalloc int[3];
                Span<int> qm = stackalloc int[3];
                for (int i = 0; i < count; i += 3)
                {
                    Span<int> m = qm;
                    for (int j = 0; j < 3; j++)
                    {
                        int v = i + j < count ? values[i + j] : 0;
                        q[j] = v >> bits;
                        m[j] = v & mask;
                    }

                    int packed = QuintPack[(((q[2] * 5) + q[1]) * 5) + q[0]];
                    int n = Math.Min(3, count - i);
                    Write(data, ref offset, m[0], bits);
                    Write(data, ref offset, packed & 7, 3);
                    if (n == 1)
                    {
                        continue;
                    }

                    Write(data, ref offset, m[1], bits);
                    Write(data, ref offset, (packed >> 3) & 3, 2);
                    if (n == 2)
                    {
                        continue;
                    }

                    Write(data, ref offset, m[2], bits);
                    Write(data, ref offset, (packed >> 5) & 3, 2);
                }

                break;
            default:
                for (int i = 0; i < count; i++)
                {
                    Write(data, ref offset, values[i], bits);
                }

                break;
        }
    }

    /// <summary>Reads <paramref name="count"/> bits (up to 16) of little-endian <paramref name="data"/> at <paramref name="offset"/>.</summary>
    public static int Read(ReadOnlySpan<byte> data, ref int offset, int count)
    {
        int value = 0;
        for (int i = 0; i < count; i++, offset++)
        {
            int at = offset >> 3;
            if (at < data.Length)
            {
                value |= ((data[at] >> (offset & 7)) & 1) << i;
            }
        }

        return value;
    }

    /// <summary>Reads <paramref name="count"/> bits of <paramref name="data"/> at <paramref name="offset"/>.</summary>
    public static int Read(ReadOnlySpan<byte> data, int offset, int count) => Read(data, ref offset, count);

    /// <summary>ORs the low <paramref name="count"/> bits of <paramref name="value"/> into <paramref name="data"/> at <paramref name="offset"/>.</summary>
    public static void Write(Span<byte> data, ref int offset, int value, int count)
    {
        for (int i = 0; i < count; i++, offset++)
        {
            if (((value >> i) & 1) != 0)
            {
                data[offset >> 3] |= (byte)(1 << (offset & 7));
            }
        }
    }

    private static readonly short[] TritPack = BuildTritPack();
    private static readonly byte[] QuintPack = BuildQuintPack();

    private static byte[][] BuildTrits()
    {
        byte[][] table = new byte[256][];
        for (int t = 0; t < 256; t++)
        {
            int c, t3, t4;
            if (((t >> 2) & 7) == 7)
            {
                c = (((t >> 5) & 7) << 2) | (t & 3);
                t4 = 2;
                t3 = 2;
            }
            else
            {
                c = t & 31;
                if (((t >> 5) & 3) == 3)
                {
                    t4 = 2;
                    t3 = (t >> 7) & 1;
                }
                else
                {
                    t4 = (t >> 7) & 1;
                    t3 = (t >> 5) & 3;
                }
            }

            int t0, t1, t2;
            if ((c & 3) == 3)
            {
                t2 = 2;
                t1 = (c >> 4) & 1;
                t0 = (((c >> 3) & 1) << 1) | (((c >> 2) & 1) & ~((c >> 3) & 1));
            }
            else if (((c >> 2) & 3) == 3)
            {
                t2 = 2;
                t1 = 2;
                t0 = c & 3;
            }
            else
            {
                t2 = (c >> 4) & 1;
                t1 = (c >> 2) & 3;
                t0 = (((c >> 1) & 1) << 1) | ((c & 1) & ~((c >> 1) & 1));
            }

            table[t] = [(byte)t0, (byte)t1, (byte)t2, (byte)t3, (byte)t4];
        }

        return table;
    }

    private static byte[][] BuildQuints()
    {
        byte[][] table = new byte[128][];
        for (int q = 0; q < 128; q++)
        {
            int q0, q1, q2;
            if (((q >> 1) & 3) == 3 && ((q >> 5) & 3) == 0)
            {
                int b0 = q & 1;
                q2 = (b0 << 2) | ((((q >> 4) & 1) & ~b0) << 1) | (((q >> 3) & 1) & ~b0);
                q1 = 4;
                q0 = 4;
            }
            else
            {
                int c;
                if (((q >> 1) & 3) == 3)
                {
                    q2 = 4;
                    c = (((q >> 3) & 3) << 3) | ((~(q >> 5) & 3) << 1) | (q & 1);
                }
                else
                {
                    q2 = (q >> 5) & 3;
                    c = q & 31;
                }

                if ((c & 7) == 5)
                {
                    q1 = 4;
                    q0 = (c >> 3) & 3;
                }
                else
                {
                    q1 = (c >> 3) & 3;
                    q0 = c & 7;
                }
            }

            table[q] = [(byte)q0, (byte)q1, (byte)q2];
        }

        return table;
    }

    // The encodings: for each combination of five trits (three quints), the first packed value that decodes to it.
    private static short[] BuildTritPack()
    {
        short[] pack = new short[243];
        for (int t = 255; t >= 0; t--)
        {
            byte[] v = TritTable[t];
            pack[(((((v[4] * 3) + v[3]) * 3) + v[2]) * 3 + v[1]) * 3 + v[0]] = (short)t;
        }

        return pack;
    }

    private static byte[] BuildQuintPack()
    {
        byte[] pack = new byte[125];
        for (int q = 127; q >= 0; q--)
        {
            byte[] v = QuintTable[q];
            pack[(((v[2] * 5) + v[1]) * 5) + v[0]] = (byte)q;
        }

        return pack;
    }

    private static byte[][] BuildColorUnquantized()
    {
        byte[][] table = new byte[21][];
        for (int r = 0; r < 21; r++)
        {
            int levels = Levels[r];
            int bits = Bits[r];
            table[r] = new byte[levels];
            for (int v = 0; v < levels; v++)
            {
                table[r][v] = (byte)(Kind[r] == 0 ? Replicate(v, bits, 8) : ColorTritQuint(Kind[r], bits, v));
            }
        }

        return table;
    }

    private static int ColorTritQuint(int kind, int bits, int value)
    {
        int m = value & ((1 << bits) - 1);
        int d = value >> bits;
        int a = (m & 1) != 0 ? 0x1FF : 0;
        int x = m >> 1;
        (int b, int c) = (kind, bits) switch
        {
            (1, 1) => (0, 204),
            (1, 2) => ((x << 8) | (x << 4) | (x << 2) | (x << 1), 93),
            (1, 3) => ((x << 7) | (x << 2) | x, 44),
            (1, 4) => ((x << 6) | x, 22),
            (1, 5) => ((x << 5) | (x >> 2), 11),
            (1, 6) => ((x << 4) | (x >> 4), 5),
            (2, 1) => (0, 113),
            (2, 2) => ((x << 8) | (x << 3) | (x << 2), 54),
            (2, 3) => ((x << 7) | (x << 1) | (x >> 1), 26),
            (2, 4) => ((x << 6) | (x >> 1), 13),
            (2, 5) => ((x << 5) | (x >> 3), 6),

            // 3 and 5 levels (no plain bits) are not colour ranges; spread them evenly.
            _ => (-1, 0),
        };
        if (b < 0)
        {
            return d * 255 / (kind == 1 ? 2 : 4);
        }

        int t = (d * c) + b;
        t ^= a;
        return (a & 0x80) | (t >> 2);
    }

    private static byte[][] BuildWeightUnquantized()
    {
        byte[][] table = new byte[12][];
        for (int r = 0; r < 12; r++)
        {
            int levels = Levels[r];
            int bits = Bits[r];
            table[r] = new byte[levels];
            for (int v = 0; v < levels; v++)
            {
                int w = Kind[r] == 0 ? Replicate(v, bits, 6) : WeightTritQuint(Kind[r], bits, v);
                table[r][v] = (byte)(w > 32 ? w + 1 : w);
            }
        }

        return table;
    }

    private static int WeightTritQuint(int kind, int bits, int value)
    {
        int m = value & ((1 << bits) - 1);
        int d = value >> bits;
        if (bits == 0)
        {
            return kind == 1 ? (d == 0 ? 0 : d == 1 ? 32 : 63) : d switch { 0 => 0, 1 => 16, 2 => 32, 3 => 47, _ => 63 };
        }

        int a = (m & 1) != 0 ? 0x7F : 0;
        int x = m >> 1;
        (int b, int c) = (kind, bits) switch
        {
            (1, 1) => (0, 50),
            (1, 2) => ((x << 6) | (x << 2) | x, 23),
            (1, 3) => ((x << 5) | x, 11),
            (2, 1) => (0, 28),
            (2, 2) => ((x << 6) | (x << 1), 13),
            _ => ((x << 5) | (x >> 1), 6),
        };
        int t = (d * c) + b;
        t ^= a;
        return (a & 0x20) | (t >> 2);
    }

    private static int Replicate(int value, int bits, int to)
    {
        if (bits == 0)
        {
            return 0;
        }

        int result = 0;
        int have = 0;
        while (have < to)
        {
            result = (result << bits) | value;
            have += bits;
        }

        return result >> (have - to);
    }
}
