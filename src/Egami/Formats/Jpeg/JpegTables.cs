using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jpeg;

/// <summary>Fixed tables from ITU-T T.81.</summary>
internal static class JpegTables
{
    /// <summary>Gets the natural (row-major) index of each coefficient in zigzag order (T.81 figure A.6).</summary>
    internal static ReadOnlySpan<byte> ZigZag =>
    [
        0, 1, 8, 16, 9, 2, 3, 10, 17, 24, 32, 25, 18, 11, 4, 5,
        12, 19, 26, 33, 40, 48, 41, 34, 27, 20, 13, 6, 7, 14, 21, 28,
        35, 42, 49, 56, 57, 50, 43, 36, 29, 22, 15, 23, 30, 37, 44, 51,
        58, 59, 52, 45, 38, 31, 39, 46, 53, 60, 61, 54, 47, 55, 62, 63,
    ];

    /// <summary>Gets the example luminance quantization table (T.81 table K.1), in natural order.</summary>
    internal static ReadOnlySpan<byte> LumaQuant =>
    [
        16, 11, 10, 16, 24, 40, 51, 61,
        12, 12, 14, 19, 26, 58, 60, 55,
        14, 13, 16, 24, 40, 57, 69, 56,
        14, 17, 22, 29, 51, 87, 80, 62,
        18, 22, 37, 56, 68, 109, 103, 77,
        24, 35, 55, 64, 81, 104, 113, 92,
        49, 64, 78, 87, 103, 121, 120, 101,
        72, 92, 95, 98, 112, 100, 103, 99,
    ];

    /// <summary>Gets the code counts per length (1 to 16 bits) of the luminance DC table (T.81 table K.3).</summary>
    internal static ReadOnlySpan<byte> DcLumaCounts => [0, 1, 5, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0, 0, 0];

    /// <summary>Gets the code counts per length of the chrominance DC table (T.81 table K.4).</summary>
    internal static ReadOnlySpan<byte> DcChromaCounts => [0, 3, 1, 1, 1, 1, 1, 1, 1, 1, 1, 0, 0, 0, 0, 0];

    /// <summary>Gets the symbols of both DC tables: the sizes 0 to 11.</summary>
    internal static ReadOnlySpan<byte> DcSymbols => [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11];

    /// <summary>Gets the code counts per length of the luminance AC table (T.81 table K.5).</summary>
    internal static ReadOnlySpan<byte> AcLumaCounts => [0, 2, 1, 3, 3, 2, 4, 3, 5, 5, 4, 4, 0, 0, 1, 0x7D];

    /// <summary>Gets the symbols of the luminance AC table (run in the high nibble, size in the low), shortest code first.</summary>
    internal static ReadOnlySpan<byte> AcLumaSymbols =>
    [
        0x01, 0x02, 0x03, 0x00, 0x04, 0x11, 0x05, 0x12, 0x21, 0x31, 0x41, 0x06, 0x13, 0x51, 0x61, 0x07,
        0x22, 0x71, 0x14, 0x32, 0x81, 0x91, 0xA1, 0x08, 0x23, 0x42, 0xB1, 0xC1, 0x15, 0x52, 0xD1, 0xF0,
        0x24, 0x33, 0x62, 0x72, 0x82, 0x09, 0x0A, 0x16, 0x17, 0x18, 0x19, 0x1A, 0x25, 0x26, 0x27, 0x28,
        0x29, 0x2A, 0x34, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48, 0x49,
        0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68, 0x69,
        0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x83, 0x84, 0x85, 0x86, 0x87, 0x88, 0x89,
        0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5, 0xA6, 0xA7,
        0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3, 0xC4, 0xC5,
        0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA, 0xE1, 0xE2,
        0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF1, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    /// <summary>Gets the code counts per length of the chrominance AC table (T.81 table K.6).</summary>
    internal static ReadOnlySpan<byte> AcChromaCounts => [0, 2, 1, 2, 4, 4, 3, 4, 7, 5, 4, 4, 0, 1, 2, 0x77];

    /// <summary>Gets the symbols of the chrominance AC table, shortest code first.</summary>
    internal static ReadOnlySpan<byte> AcChromaSymbols =>
    [
        0x00, 0x01, 0x02, 0x03, 0x11, 0x04, 0x05, 0x21, 0x31, 0x06, 0x12, 0x41, 0x51, 0x07, 0x61, 0x71,
        0x13, 0x22, 0x32, 0x81, 0x08, 0x14, 0x42, 0x91, 0xA1, 0xB1, 0xC1, 0x09, 0x23, 0x33, 0x52, 0xF0,
        0x15, 0x62, 0x72, 0xD1, 0x0A, 0x16, 0x24, 0x34, 0xE1, 0x25, 0xF1, 0x17, 0x18, 0x19, 0x1A, 0x26,
        0x27, 0x28, 0x29, 0x2A, 0x35, 0x36, 0x37, 0x38, 0x39, 0x3A, 0x43, 0x44, 0x45, 0x46, 0x47, 0x48,
        0x49, 0x4A, 0x53, 0x54, 0x55, 0x56, 0x57, 0x58, 0x59, 0x5A, 0x63, 0x64, 0x65, 0x66, 0x67, 0x68,
        0x69, 0x6A, 0x73, 0x74, 0x75, 0x76, 0x77, 0x78, 0x79, 0x7A, 0x82, 0x83, 0x84, 0x85, 0x86, 0x87,
        0x88, 0x89, 0x8A, 0x92, 0x93, 0x94, 0x95, 0x96, 0x97, 0x98, 0x99, 0x9A, 0xA2, 0xA3, 0xA4, 0xA5,
        0xA6, 0xA7, 0xA8, 0xA9, 0xAA, 0xB2, 0xB3, 0xB4, 0xB5, 0xB6, 0xB7, 0xB8, 0xB9, 0xBA, 0xC2, 0xC3,
        0xC4, 0xC5, 0xC6, 0xC7, 0xC8, 0xC9, 0xCA, 0xD2, 0xD3, 0xD4, 0xD5, 0xD6, 0xD7, 0xD8, 0xD9, 0xDA,
        0xE2, 0xE3, 0xE4, 0xE5, 0xE6, 0xE7, 0xE8, 0xE9, 0xEA, 0xF2, 0xF3, 0xF4, 0xF5, 0xF6, 0xF7, 0xF8,
        0xF9, 0xFA,
    ];

    /// <summary>Gets the example chrominance quantization table (T.81 table K.2), in natural order.</summary>
    internal static ReadOnlySpan<byte> ChromaQuant =>
    [
        17, 18, 24, 47, 99, 99, 99, 99,
        18, 21, 26, 66, 99, 99, 99, 99,
        24, 26, 56, 99, 99, 99, 99, 99,
        47, 66, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
        99, 99, 99, 99, 99, 99, 99, 99,
    ];
}

/// <summary>Helpers on blocks of 64 coefficients.</summary>
internal static class JpegBlock
{
    /// <summary>Returns a mask with bit k set where coefficient k of the 64 is nonzero.</summary>
    internal static ulong NonZero(ReadOnlySpan<short> block)
    {
        block = block[..64];
        if (Vector128.IsHardwareAccelerated)
        {
            ref short b = ref MemoryMarshal.GetReference(block);
            ulong mask = 0;
            for (int i = 0; i < 8; i++)
            {
                Vector128<short> v = Vector128.LoadUnsafe(ref b, (nuint)(i * 8));
                mask |= (ulong)(~Vector128.Equals(v, Vector128<short>.Zero)).ExtractMostSignificantBits() << (i * 8);
            }

            return mask;
        }

        ulong scalar = 0;
        for (int i = 0; i < 64; i++)
        {
            if (block[i] != 0)
            {
                scalar |= 1UL << i;
            }
        }

        return scalar;
    }

    // For each output byte k of a 32-byte half of the zigzag order: which byte of 16-byte source lane j holds natural index
    // ZigZag[k], or 0x80 (zero) when another lane does. Four masks per half, one per source lane.
    private static readonly Vector256<byte>[] ZigZagShuffles = BuildZigZagShuffles();

    /// <summary>
    /// Returns a mask with bit k set where coefficient k in zigzag order is nonzero, for a block stored in natural order. With
    /// AVX2 the compare results are moved into zigzag order with byte shuffles, sixteen bytes per source lane.
    /// </summary>
    internal static ulong NonZeroZigZag(ReadOnlySpan<short> natural)
    {
        natural = natural[..64];
        if (!Avx2.IsSupported)
        {
            ReadOnlySpan<byte> zigzag = JpegTables.ZigZag;
            ulong scalar = 0;
            for (int k = 0; k < 64; k++)
            {
                if (natural[zigzag[k]] != 0)
                {
                    scalar |= 1UL << k;
                }
            }

            return scalar;
        }

        ref short b = ref MemoryMarshal.GetReference(natural);
        Vector256<short> zero = Vector256<short>.Zero;
        Vector256<short> m0 = ~Vector256.Equals(Vector256.LoadUnsafe(ref b, 0), zero);
        Vector256<short> m1 = ~Vector256.Equals(Vector256.LoadUnsafe(ref b, 16), zero);
        Vector256<short> m2 = ~Vector256.Equals(Vector256.LoadUnsafe(ref b, 32), zero);
        Vector256<short> m3 = ~Vector256.Equals(Vector256.LoadUnsafe(ref b, 48), zero);

        // Bytes of natural indices 0-31 and 32-63, in order (the pack interleaves 64-bit groups; the permute puts them back).
        Vector256<byte> low = Avx2.Permute4x64(Avx2.PackSignedSaturate(m0, m1).AsInt64(), 0b11_01_10_00).AsByte();
        Vector256<byte> high = Avx2.Permute4x64(Avx2.PackSignedSaturate(m2, m3).AsInt64(), 0b11_01_10_00).AsByte();

        // Each source lane in both halves of a vector, so one shuffle can pick from it into any output byte.
        Vector256<byte> s0 = Avx2.Permute2x128(low, low, 0x00);
        Vector256<byte> s1 = Avx2.Permute2x128(low, low, 0x11);
        Vector256<byte> s2 = Avx2.Permute2x128(high, high, 0x00);
        Vector256<byte> s3 = Avx2.Permute2x128(high, high, 0x11);
        Vector256<byte>[] masks = ZigZagShuffles;
        Vector256<byte> first = Avx2.Shuffle(s0, masks[0]) | Avx2.Shuffle(s1, masks[1]) | Avx2.Shuffle(s2, masks[2]) | Avx2.Shuffle(s3, masks[3]);
        Vector256<byte> second = Avx2.Shuffle(s0, masks[4]) | Avx2.Shuffle(s1, masks[5]) | Avx2.Shuffle(s2, masks[6]) | Avx2.Shuffle(s3, masks[7]);
        return (uint)Avx2.MoveMask(first) | ((ulong)(uint)Avx2.MoveMask(second) << 32);
    }

    private static Vector256<byte>[] BuildZigZagShuffles()
    {
        Vector256<byte>[] masks = new Vector256<byte>[8];
        Span<byte> bytes = stackalloc byte[32];
        for (int half = 0; half < 2; half++)
        {
            for (int lane = 0; lane < 4; lane++)
            {
                for (int k = 0; k < 32; k++)
                {
                    int n = JpegTables.ZigZag[(half * 32) + k];
                    bytes[k] = n / 16 == lane ? (byte)(n % 16) : (byte)0x80;
                }

                masks[(half * 4) + lane] = Vector256.Create((ReadOnlySpan<byte>)bytes);
            }
        }

        return masks;
    }
}
