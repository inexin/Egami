using System.Buffers.Binary;

namespace Egami.Formats.Extended.Textures.Blocks;

/// <summary>
/// Decodes UASTC LDR 4 × 4 blocks, written from Binomial's UASTC LDR 4x4 Texture Specification (public domain) and the ASTC
/// chapter of the Khronos Data Format Specification, whose endpoint unquantization, weights and interpolation UASTC shares.
/// A block is 128 bits read from the least significant bit of its first byte: a Huffman-coded mode (19 of them), hints for
/// transcoders (skipped here), the partition pattern and component selector, the endpoints (trits and quints first, as whole
/// values, then the plain bits) and the weights, whose anchor texels store one bit less.
/// </summary>
internal static class Uastc
{
    /// <summary>Gets the number of modes; mode 19 is reserved.</summary>
    public const int Modes = 19;

    /// <summary>The Huffman code and its length of each mode, the code's first bit in bit 0.</summary>
    internal static ReadOnlySpan<byte> ModeCodes => [0x1, 0x35, 0x1D, 0x3, 0x13, 0xB, 0x1B, 0x7, 0x17, 0xF, 0x2, 0x0, 0x6, 0x1F, 0xD, 0x5, 0x15, 0x25, 0x9];

    internal static ReadOnlySpan<byte> ModeLengths => [4, 6, 5, 5, 5, 5, 5, 5, 5, 5, 3, 2, 3, 5, 5, 7, 6, 6, 4];

    /// <summary>Per mode: weight bits, weight range, endpoint range, subsets, planes, components.</summary>
    internal static ReadOnlySpan<byte> WeightBits => [4, 2, 3, 2, 2, 3, 2, 2, 0, 2, 4, 2, 3, 1, 2, 4, 2, 2, 5];

    internal static ReadOnlySpan<byte> WeightRanges => [8, 2, 5, 2, 2, 5, 2, 2, 0, 2, 8, 2, 5, 0, 2, 8, 2, 2, 11];

    internal static ReadOnlySpan<byte> EndpointRanges => [19, 20, 8, 7, 12, 20, 18, 12, 0, 8, 13, 13, 19, 20, 20, 20, 20, 20, 11];

    internal static ReadOnlySpan<byte> Subsets => [1, 1, 2, 3, 2, 1, 1, 2, 0, 2, 1, 1, 1, 1, 1, 1, 2, 1, 1];

    internal static ReadOnlySpan<byte> Planes => [1, 1, 1, 1, 1, 1, 2, 1, 0, 1, 1, 2, 1, 2, 1, 1, 1, 2, 1];

    internal static ReadOnlySpan<byte> Components => [3, 3, 3, 3, 3, 3, 3, 3, 4, 4, 4, 4, 4, 4, 4, 2, 2, 2, 3];

    /// <summary>The bits of transcoder hints after each mode's code: BC1, ETC1 and ETC1 bias hints, and ETC2 alpha hints.</summary>
    internal static ReadOnlySpan<byte> HintBits => [15, 15, 15, 15, 15, 15, 15, 15, 0, 23, 17, 17, 17, 23, 23, 23, 23, 23, 15];

    /// <summary>The ASTC partition seeds of the 2-subset patterns (modes 2, 4, 9 and 16), the 3-subset ones (mode 3) and mode 7's.</summary>
    internal static ReadOnlySpan<short> Seeds2 =>
        [28, 20, 16, 29, 91, 9, 107, 72, 149, 204, 50, 114, 496, 17, 78, 39, 252, 828, 43, 156, 116, 210, 476, 273, 684, 359, 246, 195, 694, 524];

    internal static ReadOnlySpan<short> Seeds3 => [260, 74, 32, 156, 183, 15, 745, 0, 335, 902, 254];

    internal static ReadOnlySpan<short> Seeds7 => [36, 48, 61, 137, 161, 183, 226, 281, 302, 307, 479, 495, 593, 594, 605, 799, 812, 988, 993];

    private static readonly byte[] ModeTable = BuildModeTable();

    /// <summary>Returns the mode of a block from its first byte, or 19 (reserved).</summary>
    public static int Mode(byte first) => ModeTable[first & 127];

    /// <summary>Returns the seeds of the partition patterns of <paramref name="mode"/>, empty for one subset.</summary>
    internal static ReadOnlySpan<short> SeedsOf(int mode) => mode switch
    {
        2 or 4 or 9 or 16 => Seeds2,
        3 => Seeds3,
        7 => Seeds7,
        _ => [],
    };

    /// <summary>
    /// Decodes a block into 16 texels of <paramref name="pixel"/> bytes (3 for RGB, 4 for RGBA) in row order. A reserved mode
    /// or pattern decodes as magenta, as ASTC's error colour does.
    /// </summary>
    public static void Decode(ReadOnlySpan<byte> block, Span<byte> pixels, int pixel)
    {
        Span<byte> rgba = stackalloc byte[64];
        if (!Unpack(block, rgba))
        {
            for (int i = 0; i < 64; i += 4)
            {
                rgba[i] = 255;
                rgba[i + 1] = 0;
                rgba[i + 2] = 255;
                rgba[i + 3] = 255;
            }
        }

        if (pixel == 4)
        {
            rgba.CopyTo(pixels);
            return;
        }

        for (int i = 0; i < 16; i++)
        {
            pixels[i * 3] = rgba[i * 4];
            pixels[(i * 3) + 1] = rgba[(i * 4) + 1];
            pixels[(i * 3) + 2] = rgba[(i * 4) + 2];
        }
    }

    /// <summary>Decodes a block into 16 RGBA texels; returns <see langword="false"/> for a reserved mode or pattern.</summary>
    public static bool Unpack(ReadOnlySpan<byte> block, Span<byte> rgba)
    {
        UInt128 bits = new(BinaryPrimitives.ReadUInt64LittleEndian(block[8..]), BinaryPrimitives.ReadUInt64LittleEndian(block));
        int mode = Mode(block[0]);
        if (mode >= Modes)
        {
            return false;
        }

        int at = ModeLengths[mode];
        if (mode == 8)
        {
            for (int i = 0; i < 64; i += 4)
            {
                rgba[i] = (byte)Read(bits, 5, 8);
                rgba[i + 1] = (byte)Read(bits, 13, 8);
                rgba[i + 2] = (byte)Read(bits, 21, 8);
                rgba[i + 3] = (byte)Read(bits, 29, 8);
            }

            return true;
        }

        at += HintBits[mode];
        int subsets = Subsets[mode];
        ReadOnlySpan<byte> partition = [];
        Span<byte> anchors = stackalloc byte[3];
        anchors[1] = anchors[2] = 255;

        // Each subset's first texel is its anchor; texel 0 always is one.
        if (subsets > 1)
        {
            ReadOnlySpan<short> seeds = SeedsOf(mode);
            int bitsForPattern = mode == 3 ? 4 : 5;
            int pattern = (int)Read(bits, at, bitsForPattern);
            at += bitsForPattern;
            if (pattern >= seeds.Length)
            {
                return false;
            }

            partition = Astc.For(4, 4)!.PartitionTable(subsets, seeds[pattern]);
            for (int s = 0; s < subsets; s++)
            {
                anchors[s] = (byte)partition.IndexOf((byte)s);
            }
        }

        int planes = Planes[mode];
        int ccs = -1;
        if (planes == 2)
        {
            if (mode == 17)
            {
                ccs = 3;
            }
            else
            {
                ccs = (int)Read(bits, at, 2);
                at += 2;
            }
        }

        // Endpoints: the trits or quints of every value in groups (5 trits in 8 bits, 3 quints in 7; a last short group in as
        // few bits as its values need), then each value's plain bits.
        int components = Components[mode];
        int count = components * 2 * subsets;
        int range = EndpointRanges[mode];
        int plain = AstcIse.Bits[range];
        int kind = AstcIse.Kind[range];
        Span<int> values = stackalloc int[18];
        if (kind != 0)
        {
            int per = kind == 1 ? 5 : 3;
            for (int first = 0; first < count; first += per)
            {
                int n = Math.Min(per, count - first);
                int width = kind == 1 ? n switch { 1 => 2, 2 => 4, 3 => 5, 4 => 7, _ => 8 } : n switch { 1 => 3, 2 => 5, _ => 7 };
                int packed = (int)Read(bits, at, width);
                at += width;
                for (int i = 0; i < n; i++)
                {
                    values[first + i] = packed % (kind == 1 ? 3 : 5);
                    packed /= kind == 1 ? 3 : 5;
                }
            }
        }

        ReadOnlySpan<byte> unquantize = AstcIse.ColorUnquantized[range];
        Span<int> endpoints = stackalloc int[18];
        for (int i = 0; i < count; i++)
        {
            int value = (values[i] << plain) | (int)Read(bits, at, plain);
            at += plain;
            if (value >= unquantize.Length)
            {
                return false;
            }

            endpoints[i] = unquantize[value];
        }

        // Weights: plain bits in texel order (both planes of a texel together); an anchor's top bit is zero and not stored.
        int weightBits = WeightBits[mode];
        ReadOnlySpan<byte> weightTable = AstcIse.WeightUnquantized[WeightRanges[mode]];
        Span<byte> weights = stackalloc byte[32];
        for (int t = 0; t < 16; t++)
        {
            bool anchor = t == anchors[0] || t == anchors[1] || t == anchors[2];
            for (int p = 0; p < planes; p++)
            {
                int n = anchor ? weightBits - 1 : weightBits;
                weights[(t * planes) + p] = weightTable[(int)Read(bits, at, n)];
                at += n;
            }
        }

        for (int t = 0; t < 16; t++)
        {
            int s = partition.IsEmpty ? 0 : partition[t];
            ReadOnlySpan<int> e = endpoints.Slice(s * components * 2, components * 2);
            for (int c = 0; c < 4; c++)
            {
                // RGB modes are opaque; the LA modes store luminance and alpha.
                int pair = components switch
                {
                    2 => c < 3 ? 0 : 1,
                    3 => c < 3 ? c : -1,
                    _ => c,
                };
                if (pair < 0)
                {
                    rgba[(t * 4) + c] = 255;
                    continue;
                }

                int w = weights[(t * planes) + (c == ccs ? 1 : 0)];
                rgba[(t * 4) + c] = (byte)Interpolate(e[pair * 2], e[(pair * 2) + 1], w);
            }
        }

        return true;
    }

    /// <summary>ASTC's interpolation of 8-bit endpoints widened to 16 bits by replication, as UASTC's encoder assumes.</summary>
    internal static int Interpolate(int low, int high, int weight)
    {
        int l = (low << 8) | low;
        int h = (high << 8) | high;
        return (((l * (64 - weight)) + (h * weight) + 32) >> 6) >> 8;
    }

    private static uint Read(UInt128 bits, int at, int count) => count == 0 ? 0 : (uint)((bits >> at) & ((UInt128.One << count) - 1));

    private static byte[] BuildModeTable()
    {
        byte[] table = new byte[128];
        for (int i = 0; i < 128; i++)
        {
            table[i] = Modes;
            for (int m = 0; m < Modes; m++)
            {
                if ((i & ((1 << ModeLengths[m]) - 1)) == ModeCodes[m])
                {
                    table[i] = (byte)m;
                }
            }
        }

        return table;
    }
}
