using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats;

/// <summary>The Adler-32 checksum from RFC 1950, section 8.2, which ends every zlib stream.</summary>
/// <remarks>
/// The vector paths split each block into chunks and use, for a chunk of n bytes b0..b(n-1) starting from sums (a, b):
/// a' = a + Σ bi and b' = b + n·a + Σ (n - i)·bi. The second term over many chunks is accumulated as running lane sums
/// and multiplied out once per block, so no lane overflows before the block's modulo.
/// </remarks>
internal static class Adler32
{
    private const uint Modulus = 65521;

    /// <summary>The largest number of bytes that can be summed before the 32-bit sums must be reduced (RFC 1950 and zlib's NMAX).</summary>
    private const int BlockSize = 5552;

    /// <summary>Continues a checksum. Start with 1.</summary>
    internal static uint Update(uint adler, ReadOnlySpan<byte> data)
    {
        return Update(adler, data, useVectors: true);
    }

    /// <summary>Continues a checksum, with or without SIMD. For tests and benchmarks.</summary>
    internal static uint Update(uint adler, ReadOnlySpan<byte> data, bool useVectors)
    {
        uint a = adler & 0xFFFF;
        uint b = adler >> 16;
        if (useVectors && Avx2.IsSupported)
        {
            data = UpdateAvx2(ref a, ref b, data);
        }
        else if (useVectors && Vector128.IsHardwareAccelerated)
        {
            data = UpdateVector128(ref a, ref b, data);
        }

        while (!data.IsEmpty)
        {
            ReadOnlySpan<byte> block = data[..Math.Min(BlockSize, data.Length)];
            foreach (byte value in block)
            {
                a += value;
                b += a;
            }

            a %= Modulus;
            b %= Modulus;
            data = data[block.Length..];
        }

        return (b << 16) | a;
    }

    /// <summary>32-byte chunks with SAD for the byte sums and multiply-add for the weighted sums.</summary>
    /// <returns>The bytes left over, fewer than 32.</returns>
    private static ReadOnlySpan<byte> UpdateAvx2(scoped ref uint a, scoped ref uint b, ReadOnlySpan<byte> data)
    {
        const int Chunk = 32;
        const int BlockChunks = BlockSize / Chunk;
        Vector256<sbyte> weights = Vector256.Create(
            (sbyte)32, 31, 30, 29, 28, 27, 26, 25, 24, 23, 22, 21, 20, 19, 18, 17, 16, 15, 14, 13, 12, 11, 10, 9, 8, 7, 6, 5, 4, 3, 2, 1);
        Vector256<short> ones = Vector256.Create((short)1);
        ref byte p = ref MemoryMarshal.GetReference(data);
        int chunks = data.Length / Chunk;
        nuint offset = 0;
        while (chunks > 0)
        {
            int n = Math.Min(chunks, BlockChunks);
            Vector256<uint> sums = Vector256<uint>.Zero;
            Vector256<uint> previous = Vector256<uint>.Zero;
            Vector256<int> weighted = Vector256<int>.Zero;
            for (int c = 0; c < n; c++, offset += Chunk)
            {
                Vector256<byte> bytes = Vector256.LoadUnsafe(ref p, offset);
                previous += sums;
                sums += Avx2.SumAbsoluteDifferences(bytes, Vector256<byte>.Zero).AsUInt32();
                weighted += Avx2.MultiplyAddAdjacent(Avx2.MultiplyAddAdjacent(bytes, weights), ones);
            }

            ulong newB = b + ((ulong)n * Chunk * a) + (Chunk * (ulong)Vector256.Sum(previous)) + (ulong)Vector256.Sum(weighted);
            a = (uint)((a + (ulong)Vector256.Sum(sums)) % Modulus);
            b = (uint)(newB % Modulus);
            chunks -= n;
        }

        return data[(int)offset..];
    }

    /// <summary>16-byte chunks with portable vector operations, for Arm and x86 without AVX2.</summary>
    /// <returns>The bytes left over, fewer than 16.</returns>
    private static ReadOnlySpan<byte> UpdateVector128(scoped ref uint a, scoped ref uint b, ReadOnlySpan<byte> data)
    {
        const int Chunk = 16;
        const int BlockChunks = BlockSize / Chunk;
        Vector128<ushort> weightsLow = Vector128.Create((ushort)16, 15, 14, 13, 12, 11, 10, 9);
        Vector128<ushort> weightsHigh = Vector128.Create((ushort)8, 7, 6, 5, 4, 3, 2, 1);
        ref byte p = ref MemoryMarshal.GetReference(data);
        int chunks = data.Length / Chunk;
        nuint offset = 0;
        while (chunks > 0)
        {
            int n = Math.Min(chunks, BlockChunks);
            Vector128<uint> sums = Vector128<uint>.Zero;
            Vector128<uint> previous = Vector128<uint>.Zero;
            Vector128<uint> weighted = Vector128<uint>.Zero;
            for (int c = 0; c < n; c++, offset += Chunk)
            {
                (Vector128<ushort> low, Vector128<ushort> high) = Vector128.Widen(Vector128.LoadUnsafe(ref p, offset));
                previous += sums;
                sums += WidenAndAdd(low + high);
                weighted += WidenAndAdd((low * weightsLow) + (high * weightsHigh));
            }

            ulong newB = b + ((ulong)n * Chunk * a) + (Chunk * (ulong)Vector128.Sum(previous)) + Vector128.Sum(weighted);
            a = (uint)((a + (ulong)Vector128.Sum(sums)) % Modulus);
            b = (uint)(newB % Modulus);
            chunks -= n;
        }

        return data[(int)offset..];
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<uint> WidenAndAdd(Vector128<ushort> value)
    {
        (Vector128<uint> low, Vector128<uint> high) = Vector128.Widen(value);
        return low + high;
    }
}
