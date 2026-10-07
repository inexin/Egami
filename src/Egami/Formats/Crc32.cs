using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using ArmCrc32 = System.Runtime.Intrinsics.Arm.Crc32;

namespace Egami.Formats;

/// <summary>
/// CRC-32 as used by PNG, zlib and gzip (ISO 3309 / ITU-T V.42: polynomial 0x04C11DB7, reflected, initial value and final XOR 0xFFFFFFFF).
/// Uses the Arm CRC32 instructions or x64 carry-less multiplication when available, and slicing-by-8 tables otherwise.
/// </summary>
internal static class Crc32
{
    /// <summary>The reflected polynomial.</summary>
    private const uint Polynomial = 0xEDB88320;

    /// <summary>Eight 256-entry tables for slicing-by-8: table k gives the CRC of a byte followed by k zero bytes.</summary>
    private static readonly uint[] Tables = BuildTables();

    /// <summary>Returns the CRC-32 of <paramref name="data"/>.</summary>
    internal static uint Compute(ReadOnlySpan<byte> data)
    {
        return Update(0, data);
    }

    /// <summary>
    /// Continues a CRC-32: <c>Update(Compute(a), b) == Compute(a + b)</c>. Start with 0.
    /// </summary>
    internal static uint Update(uint crc, ReadOnlySpan<byte> data)
    {
        return ~UpdateState(~crc, data, useHardware: true);
    }

    /// <summary>Runs the raw CRC register over <paramref name="data"/>, with or without hardware acceleration. For tests and benchmarks.</summary>
    internal static uint UpdateState(uint state, ReadOnlySpan<byte> data, bool useHardware)
    {
        if (useHardware)
        {
            if (ArmCrc32.Arm64.IsSupported)
            {
                return UpdateArm(state, data);
            }

            if (Pclmulqdq.IsSupported && Sse41.IsSupported && data.Length >= 64)
            {
                int folded = data.Length & ~15;
                state = UpdateClmul(state, data[..folded]);
                data = data[folded..];
            }
        }

        return UpdateTable(state, data);
    }

    /// <summary>Slicing-by-8: eight table lookups per eight input bytes.</summary>
    private static uint UpdateTable(uint state, ReadOnlySpan<byte> data)
    {
        ref uint t = ref MemoryMarshal.GetArrayDataReference(Tables);
        while (data.Length >= 8)
        {
            uint a = BinaryPrimitives.ReadUInt32LittleEndian(data) ^ state;
            uint b = BinaryPrimitives.ReadUInt32LittleEndian(data[4..]);
            state = Unsafe.Add(ref t, (7 * 256) + (int)(a & 0xFF))
                ^ Unsafe.Add(ref t, (6 * 256) + (int)((a >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, (5 * 256) + (int)((a >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, (4 * 256) + (int)(a >> 24))
                ^ Unsafe.Add(ref t, (3 * 256) + (int)(b & 0xFF))
                ^ Unsafe.Add(ref t, (2 * 256) + (int)((b >> 8) & 0xFF))
                ^ Unsafe.Add(ref t, (1 * 256) + (int)((b >> 16) & 0xFF))
                ^ Unsafe.Add(ref t, (int)(b >> 24));
            data = data[8..];
        }

        foreach (byte value in data)
        {
            state = Unsafe.Add(ref t, (int)((state ^ value) & 0xFF)) ^ (state >> 8);
        }

        return state;
    }

    /// <summary>The Arm CRC32 instructions compute this exact CRC, eight bytes at a time.</summary>
    private static uint UpdateArm(uint state, ReadOnlySpan<byte> data)
    {
        while (data.Length >= 8)
        {
            state = ArmCrc32.Arm64.ComputeCrc32(state, BinaryPrimitives.ReadUInt64LittleEndian(data));
            data = data[8..];
        }

        foreach (byte value in data)
        {
            state = ArmCrc32.ComputeCrc32(state, value);
        }

        return state;
    }

    /// <summary>
    /// Folds 16-byte blocks with carry-less multiplication, then reduces to 32 bits with a Barrett reduction.
    /// Method: Gopal et al., "Fast CRC Computation for Generic Polynomials Using PCLMULQDQ Instruction", Intel, 2009,
    /// in its bit-reflected form. The constants are x^n mod P(x) for the fold distances, bit-reflected and shifted
    /// as the paper describes; CrcTests recomputes each one from the polynomial.
    /// </summary>
    /// <param name="state">The CRC register.</param>
    /// <param name="data">At least 64 bytes, a multiple of 16.</param>
    private static uint UpdateClmul(uint state, ReadOnlySpan<byte> data)
    {
        // Fold distance 512 bits (four blocks): x^(512+32) and x^(512-32) mod P. Fold distance 128 bits: x^(128+32) and x^(128-32) mod P.
        Vector128<ulong> fold4 = Vector128.Create(ClmulConstants.Fold512Low, ClmulConstants.Fold512High);
        Vector128<ulong> fold1 = Vector128.Create(ClmulConstants.Fold128Low, ClmulConstants.Fold128High);

        ref byte p = ref MemoryMarshal.GetReference(data);
        int length = data.Length;
        Vector128<ulong> x0 = Vector128.LoadUnsafe(ref p).AsUInt64() ^ Vector128.CreateScalar(state).AsUInt64();
        Vector128<ulong> x1 = Vector128.LoadUnsafe(ref p, 16).AsUInt64();
        Vector128<ulong> x2 = Vector128.LoadUnsafe(ref p, 32).AsUInt64();
        Vector128<ulong> x3 = Vector128.LoadUnsafe(ref p, 48).AsUInt64();
        nuint offset = 64;

        for (; (int)offset + 64 <= length; offset += 64)
        {
            x0 = Fold(x0, fold4, Vector128.LoadUnsafe(ref p, offset).AsUInt64());
            x1 = Fold(x1, fold4, Vector128.LoadUnsafe(ref p, offset + 16).AsUInt64());
            x2 = Fold(x2, fold4, Vector128.LoadUnsafe(ref p, offset + 32).AsUInt64());
            x3 = Fold(x3, fold4, Vector128.LoadUnsafe(ref p, offset + 48).AsUInt64());
        }

        Vector128<ulong> x = Fold(x0, fold1, x1);
        x = Fold(x, fold1, x2);
        x = Fold(x, fold1, x3);

        for (; (int)offset + 16 <= length; offset += 16)
        {
            x = Fold(x, fold1, Vector128.LoadUnsafe(ref p, offset).AsUInt64());
        }

        // 128 bits to 64: multiply the low half by x^(128-32) mod P and add the high half, which also appends 32 zero bits.
        Vector128<ulong> mask32 = Vector128.Create(0xFFFFFFFFUL, 0);
        x = Pclmulqdq.CarrylessMultiply(x, fold1, 0x10) ^ Sse2.ShiftRightLogical128BitLane(x, 8);

        // 64 bits to 32 plus 32 zero bits: fold the low 32 bits by x^64 mod P.
        Vector128<ulong> low = x & mask32;
        x = Sse2.ShiftRightLogical128BitLane(x, 4) ^ Pclmulqdq.CarrylessMultiply(low, Vector128.CreateScalar(ClmulConstants.Fold64), 0x00);

        // Barrett reduction with mu = floor(x^64 / P) and P itself, both bit-reflected.
        Vector128<ulong> barrett = Vector128.Create(ClmulConstants.Polynomial, ClmulConstants.Mu);
        Vector128<ulong> t = Pclmulqdq.CarrylessMultiply(x & mask32, barrett, 0x10) & mask32;
        t = Pclmulqdq.CarrylessMultiply(t, barrett, 0x00);
        return (x ^ t).AsUInt32().GetElement(1);
    }

    /// <summary>Folds <paramref name="x"/> forward by the distance <paramref name="k"/> encodes and adds <paramref name="next"/>.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<ulong> Fold(Vector128<ulong> x, Vector128<ulong> k, Vector128<ulong> next)
    {
        return Pclmulqdq.CarrylessMultiply(x, k, 0x00) ^ Pclmulqdq.CarrylessMultiply(x, k, 0x11) ^ next;
    }

    private static uint[] BuildTables()
    {
        uint[] tables = new uint[8 * 256];
        for (uint i = 0; i < 256; i++)
        {
            uint c = i;
            for (int bit = 0; bit < 8; bit++)
            {
                c = (c & 1) != 0 ? (c >> 1) ^ Polynomial : c >> 1;
            }

            tables[i] = c;
        }

        for (int k = 1; k < 8; k++)
        {
            for (int i = 0; i < 256; i++)
            {
                uint previous = tables[((k - 1) * 256) + i];
                tables[(k * 256) + i] = (previous >> 8) ^ tables[previous & 0xFF];
            }
        }

        return tables;
    }

    /// <summary>Folding and reduction constants for the reflected CRC-32 polynomial, each 33 bits.</summary>
    internal static class ClmulConstants
    {
        /// <summary>Multiplies the low 64 bits of a block when folding four blocks ahead.</summary>
        internal const ulong Fold512Low = 0x1_5444_2BD4;

        /// <summary>Multiplies the high 64 bits of a block when folding four blocks ahead.</summary>
        internal const ulong Fold512High = 0x1_C6E4_1596;

        /// <summary>Multiplies the low 64 bits of a block when folding one block ahead.</summary>
        internal const ulong Fold128Low = 0x1_7519_97D0;

        /// <summary>Multiplies the high 64 bits of a block when folding one block ahead.</summary>
        internal const ulong Fold128High = 0x0_CCAA_009E;

        /// <summary>Folds 32 bits forward by 64.</summary>
        internal const ulong Fold64 = 0x1_63CD_6124;

        /// <summary>The polynomial, bit-reflected, with its x^32 term.</summary>
        internal const ulong Polynomial = 0x1_DB71_0641;

        /// <summary>floor(x^64 / P(x)), bit-reflected.</summary>
        internal const ulong Mu = 0x1_F701_1641;
    }
}
