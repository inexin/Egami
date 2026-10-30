using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dds.Blocks;

/// <summary>
/// Decodes BC6H blocks (16 bytes, 4 × 4 RGB half-float pixels), unsigned (UF16) and signed (SF16), written from the Direct3D 11
/// BC6H format specification. The decode is exact integer arithmetic down to the half-float bits. A block with a reserved mode
/// decodes to black, as the specification requires.
/// </summary>
internal static class Bc6h
{
    // Mode number (the 2- or 5-bit mode field) to its index in the tables below, or -1 for a reserved mode.
    private static ReadOnlySpan<sbyte> ModeIndex =>
    [
        0, 1, 2, 10, -1, -1, 3, 11, -1, -1, 4, 12, -1, -1, 5, 13,
        -1, -1, 6, -1, -1, -1, 7, -1, -1, -1, 8, -1, -1, -1, 9, -1,
    ];

    // Per mode index: regions (1 or 2), transformed (0 or 1), endpoint bits, then the delta bits of red, green and blue.
    private static ReadOnlySpan<byte> Modes =>
    [
        2, 1, 10, 5, 5, 5,
        2, 1, 7, 6, 6, 6,
        2, 1, 11, 5, 4, 4,
        2, 1, 11, 4, 5, 4,
        2, 1, 11, 4, 4, 5,
        2, 1, 9, 5, 5, 5,
        2, 1, 8, 6, 5, 5,
        2, 1, 8, 5, 6, 5,
        2, 1, 8, 5, 5, 6,
        2, 0, 6, 6, 6, 6,
        1, 0, 10, 10, 10, 10,
        1, 1, 11, 9, 9, 9,
        1, 1, 12, 8, 8, 8,
        1, 1, 16, 4, 4, 4,
    ];

    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];

    private static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Decodes one block into 16 pixels of four half floats (red, green, blue, alpha 1), as raw half bits.</summary>
    public static void Decode(ReadOnlySpan<byte> block, Span<ushort> rgba, bool signed)
    {
        ulong low = BinaryPrimitives.ReadUInt64LittleEndian(block);
        ulong high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        int mode = (int)(low & 3);
        if (mode >= 2)
        {
            mode = (int)(low & 31);
        }

        int index = ModeIndex[mode];
        if (index < 0)
        {
            Fill(rgba, 0);
            return;
        }

        ReadOnlySpan<byte> info = Modes.Slice(index * 6, 6);
        int regions = info[0];
        bool transformed = info[1] != 0;
        int endpointBits = info[2];

        // Endpoints: w x (region 0) y z (region 1), three channels each.
        Span<int> e = stackalloc int[12];
        int shape = 0;
        byte[] header = BcTables.Bc6hHeaders[index];
        int headerBits = regions == 2 ? 82 : 65;
        for (int bit = 0; bit < headerBits; bit++)
        {
            if (Bit(low, high, bit) == 0)
            {
                continue;
            }

            int field = header[bit] >> 4;
            int which = header[bit] & 15;
            if (field == 2)
            {
                shape |= 1 << which;
            }
            else if (field >= 3)
            {
                // Fields 3 to 14: red w x y z, green w x y z, blue w x y z.
                int channel = (field - 3) / 4;
                int endpoint = (field - 3) % 4;
                e[(endpoint * 3) + channel] |= 1 << which;
            }
        }

        // Sign extension: endpoint w when signed; the others when signed or stored as deltas.
        for (int c = 0; c < 3; c++)
        {
            if (signed)
            {
                e[c] = SignExtend(e[c], endpointBits);
            }

            if (signed || transformed)
            {
                int deltaBits = transformed ? info[3 + c] : endpointBits;
                for (int p = 1; p < regions * 2; p++)
                {
                    e[(p * 3) + c] = SignExtend(e[(p * 3) + c], deltaBits);
                }
            }
        }

        if (transformed)
        {
            int mask = (1 << endpointBits) - 1;
            for (int c = 0; c < 3; c++)
            {
                for (int p = 1; p < regions * 2; p++)
                {
                    int v = (e[(p * 3) + c] + e[c]) & mask;
                    e[(p * 3) + c] = signed ? SignExtend(v, endpointBits) : v;
                }
            }
        }

        for (int i = 0; i < 12; i++)
        {
            e[i] = Unquantize(e[i], endpointBits, signed);
        }

        int bitPos = headerBits;
        int indexBits = regions == 2 ? 3 : 4;
        uint partition = regions == 2 ? BcTables.Partitions2[shape] : 0;
        int anchor = regions == 2 ? BcTables.Anchors2[shape] : -1;
        ReadOnlySpan<byte> weights = regions == 2 ? Weights3 : Weights4;
        for (int i = 0; i < 16; i++)
        {
            int count = i == 0 || i == anchor ? indexBits - 1 : indexBits;
            int idx = (int)Bits(low, high, bitPos, count);
            bitPos += count;
            int region = (int)((partition >> (2 * i)) & 3);
            int w = weights[idx];
            int a = region * 6;
            int b = a + 3;
            Span<ushort> pixel = rgba.Slice(i * 4, 4);
            for (int c = 0; c < 3; c++)
            {
                int v = (((64 - w) * e[a + c]) + (w * e[b + c]) + 32) >> 6;
                pixel[c] = Finish(v, signed);
            }

            pixel[3] = 0x3C00;
        }
    }

    private static void Fill(Span<ushort> rgba, ushort value)
    {
        for (int i = 0; i < 16; i++)
        {
            rgba[i * 4] = value;
            rgba[(i * 4) + 1] = value;
            rgba[(i * 4) + 2] = value;
            rgba[(i * 4) + 3] = 0x3C00;
        }
    }

    private static int Bit(ulong low, ulong high, int bit) => (int)((bit < 64 ? low >> bit : high >> (bit - 64)) & 1);

    private static ulong Bits(ulong low, ulong high, int start, int count)
    {
        ulong value = start >= 64 ? high >> (start - 64) : (low >> start) | (start == 0 ? 0 : high << (64 - start));
        return value & ((1UL << count) - 1);
    }

    private static int SignExtend(int value, int bits)
    {
        int shift = 32 - bits;
        return (value << shift) >> shift;
    }

    private static int Unquantize(int value, int bits, bool signed)
    {
        if (!signed)
        {
            if (bits >= 15)
            {
                return value;
            }

            if (value == 0)
            {
                return 0;
            }

            if (value == (1 << bits) - 1)
            {
                return 0xFFFF;
            }

            return ((value << 16) + 0x8000) >> bits;
        }

        if (bits >= 16)
        {
            return value;
        }

        bool negative = value < 0;
        int magnitude = negative ? -value : value;
        int result;
        if (magnitude == 0)
        {
            result = 0;
        }
        else if (magnitude >= (1 << (bits - 1)) - 1)
        {
            result = 0x7FFF;
        }
        else
        {
            result = ((magnitude << 15) + 0x4000) >> (bits - 1);
        }

        return negative ? -result : result;
    }

    private static ushort Finish(int value, bool signed)
    {
        if (!signed)
        {
            return (ushort)((value * 31) >> 6);
        }

        return value < 0 ? (ushort)(0x8000 | (((-value) * 31) >> 5)) : (ushort)((value * 31) >> 5);
    }
}
