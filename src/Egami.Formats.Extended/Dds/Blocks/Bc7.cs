using System.Buffers.Binary;

namespace Egami.Formats.Extended.Dds.Blocks;

/// <summary>
/// Decodes BC7 blocks (16 bytes, 4 × 4 RGBA pixels), written from the Direct3D 11 BC7 format specification. The decode is exact
/// integer arithmetic, so every conforming decoder gives the same bytes. A block with a reserved mode decodes to transparent
/// black, as the specification requires.
/// </summary>
internal static class Bc7
{
    // Per mode: subsets, partition bits, rotation bits, index selection bits, colour bits, alpha bits, endpoint p-bits,
    // shared p-bits, index bits and secondary index bits.
    private static ReadOnlySpan<byte> Modes =>
    [
        3, 4, 0, 0, 4, 0, 1, 0, 3, 0,
        2, 6, 0, 0, 6, 0, 0, 1, 3, 0,
        3, 6, 0, 0, 5, 0, 0, 0, 2, 0,
        2, 6, 0, 0, 7, 0, 1, 0, 2, 0,
        1, 0, 2, 1, 5, 6, 0, 0, 2, 3,
        1, 0, 2, 0, 7, 8, 0, 0, 2, 2,
        1, 0, 0, 0, 7, 7, 1, 0, 4, 0,
        2, 6, 0, 0, 5, 5, 1, 0, 2, 0,
    ];

    private static ReadOnlySpan<byte> Weights2 => [0, 21, 43, 64];

    private static ReadOnlySpan<byte> Weights3 => [0, 9, 18, 27, 37, 46, 55, 64];

    private static ReadOnlySpan<byte> Weights4 => [0, 4, 9, 13, 17, 21, 26, 30, 34, 38, 43, 47, 51, 55, 60, 64];

    /// <summary>Decodes one block into 16 RGBA pixels, 4 bytes each, in row order.</summary>
    public static void Decode(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        BitReader bits = new(block);
        int mode = 0;
        while (mode < 8 && bits.Read(1) == 0)
        {
            mode++;
        }

        if (mode == 8)
        {
            rgba[..64].Clear();
            return;
        }

        ReadOnlySpan<byte> m = Modes.Slice(mode * 10, 10);
        int subsets = m[0];
        int partition = bits.Read(m[1]);
        int rotation = bits.Read(m[2]);
        int indexSelection = bits.Read(m[3]);
        int colorBits = m[4];
        int alphaBits = m[5];

        // Endpoints: e[subset * 2 + end][channel].
        Span<int> e = stackalloc int[6 * 4];
        for (int c = 0; c < 3; c++)
        {
            for (int i = 0; i < subsets * 2; i++)
            {
                e[(i * 4) + c] = bits.Read(colorBits);
            }
        }

        for (int i = 0; i < subsets * 2; i++)
        {
            e[(i * 4) + 3] = alphaBits > 0 ? bits.Read(alphaBits) : 255;
        }

        int extra = 0;
        if (m[6] != 0)
        {
            for (int i = 0; i < subsets * 2; i++)
            {
                int p = bits.Read(1);
                for (int c = 0; c < 4; c++)
                {
                    e[(i * 4) + c] = (e[(i * 4) + c] << 1) | p;
                }
            }

            extra = 1;
        }
        else if (m[7] != 0)
        {
            for (int s = 0; s < subsets; s++)
            {
                int p = bits.Read(1);
                for (int i = s * 2; i < (s * 2) + 2; i++)
                {
                    for (int c = 0; c < 4; c++)
                    {
                        e[(i * 4) + c] = (e[(i * 4) + c] << 1) | p;
                    }
                }
            }

            extra = 1;
        }

        for (int i = 0; i < subsets * 2; i++)
        {
            for (int c = 0; c < 3; c++)
            {
                e[(i * 4) + c] = Expand(e[(i * 4) + c], colorBits + extra);
            }

            e[(i * 4) + 3] = alphaBits > 0 ? Expand(e[(i * 4) + 3], alphaBits + extra) : 255;
        }

        uint shape = subsets switch
        {
            2 => BcTables.Partitions2[partition],
            3 => BcTables.Partitions3[partition],
            _ => 0,
        };
        int anchor1 = subsets == 2 ? BcTables.Anchors2[partition] : subsets == 3 ? BcTables.Anchors3Second[partition] : -1;
        int anchor2 = subsets == 3 ? BcTables.Anchors3Third[partition] : -1;

        int indexBits = m[8];
        Span<byte> primary = stackalloc byte[16];
        for (int i = 0; i < 16; i++)
        {
            bool anchor = i == 0 || i == anchor1 || i == anchor2;
            primary[i] = (byte)bits.Read(anchor ? indexBits - 1 : indexBits);
        }

        int secondaryBits = m[9];
        Span<byte> secondary = stackalloc byte[16];
        if (secondaryBits > 0)
        {
            for (int i = 0; i < 16; i++)
            {
                secondary[i] = (byte)bits.Read(i == 0 ? secondaryBits - 1 : secondaryBits);
            }
        }

        ReadOnlySpan<byte> colorWeights = WeightsFor(indexBits);
        ReadOnlySpan<byte> alphaWeights = WeightsFor(secondaryBits);
        bool swap = indexSelection != 0;
        for (int i = 0; i < 16; i++)
        {
            int subset = (int)((shape >> (2 * i)) & 3);
            int e0 = subset * 8;
            int e1 = e0 + 4;
            int cw;
            int aw;
            if (secondaryBits == 0)
            {
                cw = aw = colorWeights[primary[i]];
            }
            else if (swap)
            {
                cw = alphaWeights[secondary[i]];
                aw = colorWeights[primary[i]];
            }
            else
            {
                cw = colorWeights[primary[i]];
                aw = alphaWeights[secondary[i]];
            }

            int r = Interpolate(e[e0], e[e1], cw);
            int g = Interpolate(e[e0 + 1], e[e1 + 1], cw);
            int b = Interpolate(e[e0 + 2], e[e1 + 2], cw);
            int a = Interpolate(e[e0 + 3], e[e1 + 3], aw);
            switch (rotation)
            {
                case 1:
                    (a, r) = (r, a);
                    break;
                case 2:
                    (a, g) = (g, a);
                    break;
                case 3:
                    (a, b) = (b, a);
                    break;
            }

            Span<byte> p = rgba.Slice(i * 4, 4);
            p[0] = (byte)r;
            p[1] = (byte)g;
            p[2] = (byte)b;
            p[3] = (byte)a;
        }
    }

    private static ReadOnlySpan<byte> WeightsFor(int bits) => bits switch
    {
        2 => Weights2,
        3 => Weights3,
        _ => Weights4,
    };

    private static int Expand(int value, int bits)
    {
        value <<= 8 - bits;
        return value | (value >> bits);
    }

    private static int Interpolate(int e0, int e1, int weight) => (((64 - weight) * e0) + (weight * e1) + 32) >> 6;

    /// <summary>Reads a 128-bit block from bit 0 upwards.</summary>
    private ref struct BitReader
    {
        private ulong _low;
        private ulong _high;

        public BitReader(ReadOnlySpan<byte> block)
        {
            _low = BinaryPrimitives.ReadUInt64LittleEndian(block);
            _high = BinaryPrimitives.ReadUInt64LittleEndian(block[8..]);
        }

        public int Read(int count)
        {
            if (count == 0)
            {
                return 0;
            }

            int value = (int)(_low & ((1UL << count) - 1));
            _low = (_low >> count) | (_high << (64 - count));
            _high >>= count;
            return value;
        }
    }
}
