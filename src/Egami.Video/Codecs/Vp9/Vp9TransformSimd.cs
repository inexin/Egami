using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The 8-bit form of <see cref="Vp9Transform.InverseAdd{T}"/>, with the same values: the row transforms run one row at a time
/// (a row of zeros gives zeros and is skipped, as most rows are), then the column transforms run a column per 32-bit lane, four
/// or eight columns at a time, and the result is added to the prediction a row of pixels at a time.
/// </summary>
[SkipLocalsInit]
internal static partial class Vp9TransformSimd
{
    private const int C1 = 16364;
    private const int C2 = 16305;
    private const int C3 = 16207;
    private const int C4 = 16069;
    private const int C5 = 15893;
    private const int C6 = 15679;
    private const int C7 = 15426;
    private const int C8 = 15137;
    private const int C9 = 14811;
    private const int C10 = 14449;
    private const int C11 = 14053;
    private const int C12 = 13623;
    private const int C13 = 13160;
    private const int C14 = 12665;
    private const int C15 = 12140;
    private const int C16 = 11585;
    private const int C17 = 11003;
    private const int C18 = 10394;
    private const int C19 = 9760;
    private const int C20 = 9102;
    private const int C21 = 8423;
    private const int C22 = 7723;
    private const int C23 = 7005;
    private const int C24 = 6270;
    private const int C25 = 5520;
    private const int C26 = 4756;
    private const int C27 = 3981;
    private const int C28 = 3196;
    private const int C29 = 2404;
    private const int C30 = 1606;
    private const int C31 = 804;
    private const int S1 = 5283;
    private const int S2 = 9929;
    private const int S3 = 13377;
    private const int S4 = 15212;

    /// <summary>Gets whether the vector form runs here; otherwise the scalar one does.</summary>
    public static bool IsSupported => Vector256.IsHardwareAccelerated && Avx2.IsSupported;

    /// <summary>Inverse transforms an 8-bit block and adds it to <paramref name="dst"/>, as <see cref="Vp9Transform.InverseAdd{T}"/>.</summary>
    public static void InverseAdd(ReadOnlySpan<int> coefficients, Span<byte> dst, int stride, int txSize, int txType, int eob)
    {
        int n = 4 << txSize;
        int shift = txSize switch { 0 => 4, 1 => 5, _ => 6 };
        if (dst.Length < ((n - 1) * stride) + n)
        {
            throw new ArgumentOutOfRangeException(nameof(dst));
        }

        if (txType == Vp9Tables.DctDct && eob == 1)
        {
            // DC only (vpx_idctNxN_1_add).
            int dc = (int)Rs(coefficients[0] * (long)C16);
            dc = (int)Rs(dc * (long)C16);
            AddConstant(dst, stride, n, (dc + (1 << (shift - 1))) >> shift);
            return;
        }

        bool rowAdst = txType is 2 or 3 && txSize != Vp9Tables.Tx32x32;
        bool colAdst = txType is 1 or 3 && txSize != Vp9Tables.Tx32x32;
        if (n == 4)
        {
            InverseAdd4(coefficients, dst, stride, rowAdst, colAdst);
            return;
        }

        // The rows, eight at a time: each 8 by 8 piece transposed so a lane holds a row, the transform, transposed back.
        Span<int> rows = stackalloc int[32 * 32];
        Span<Vector256<int>> lanes = stackalloc Vector256<int>[32];
        Span<Vector256<int>> transformed = stackalloc Vector256<int>[32];
        for (int r = 0; r < n; r += 8)
        {
            ReadOnlySpan<int> group = coefficients.Slice(r * n, 8 * n);
            Span<int> outGroup = rows.Slice(r * n, 8 * n);
            if (!group.ContainsAnyExcept(0))
            {
                outGroup.Clear();
                continue;
            }

            for (int c = 0; c < n; c += 8)
            {
                Transpose8x8(group[c..], n, lanes.Slice(c, 8));
            }

            Transform(lanes[..n], transformed[..n], txSize, rowAdst);
            for (int c = 0; c < n; c += 8)
            {
                TransposeBack8x8(transformed.Slice(c, 8), outGroup[c..], n);
            }
        }

        Span<Vector256<int>> columns = stackalloc Vector256<int>[32];
        Span<Vector256<int>> result = stackalloc Vector256<int>[32];
        Vector256<int> half = Vector256.Create(1 << (shift - 1));
        for (int c = 0; c < n; c += 8)
        {
            for (int j = 0; j < n; j++)
            {
                columns[j] = Vector256.Create(rows.Slice((j * n) + c, 8));
            }

            Transform(columns[..n], result[..n], txSize, colAdst);
            for (int k = 0; k < n; k++)
            {
                ref byte pixel = ref MemoryMarshal.GetReference(dst.Slice((k * stride) + c));
                Vector256<int> value = Vector256.ShiftRightArithmetic(result[k] + half, shift);
                Vector256<int> sum = value + Avx2.ConvertToVector256Int32(Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref pixel)).AsByte());
                sum = Vector256.Min(Vector256.Max(sum, Vector256<int>.Zero), Vector256.Create(255));
                Vector128<short> narrow = Vector128.Narrow(sum.GetLower(), sum.GetUpper());
                Unsafe.WriteUnaligned(ref pixel, Vector128.Narrow(narrow, narrow).AsUInt64().ToScalar());
            }
        }
    }

    private static void Transform(ReadOnlySpan<Vector256<int>> input, Span<Vector256<int>> output, int txSize, bool adst)
    {
        switch (txSize)
        {
            case Vp9Tables.Tx8x8:
                if (adst)
                {
                    Iadst8(input, output);
                }
                else
                {
                    Idct8(input, output);
                }

                break;
            case Vp9Tables.Tx16x16:
                if (adst)
                {
                    Iadst16(input, output);
                }
                else
                {
                    Idct16(input, output);
                }

                break;
            default:
                Idct32(input, output);
                break;
        }
    }

    /// <summary>The 4 by 4 block: rows then columns a lane each, with 4 by 4 transposes between.</summary>
    private static void InverseAdd4(ReadOnlySpan<int> coefficients, Span<byte> dst, int stride, bool rowAdst, bool colAdst)
    {
        Span<Vector128<int>> a = stackalloc Vector128<int>[4];
        Span<Vector128<int>> b = stackalloc Vector128<int>[4];
        Transpose4x4(Vector128.Create(coefficients[..4]), Vector128.Create(coefficients.Slice(4, 4)), Vector128.Create(coefficients.Slice(8, 4)),
            Vector128.Create(coefficients.Slice(12, 4)), a);
        if (rowAdst)
        {
            Iadst4(a, b);
        }
        else
        {
            Idct4(a, b);
        }

        Transpose4x4(b[0], b[1], b[2], b[3], a);
        if (colAdst)
        {
            Iadst4(a, b);
        }
        else
        {
            Idct4(a, b);
        }

        Vector128<int> round = Vector128.Create(1 << 3);
        for (int k = 0; k < 4; k++)
        {
            ref byte pixel = ref MemoryMarshal.GetReference(dst.Slice(k * stride));
            Vector128<int> value = Vector128.ShiftRightArithmetic(b[k] + round, 4);
            Vector128<int> sum = value + Sse41.ConvertToVector128Int32(Vector128.CreateScalar(Unsafe.ReadUnaligned<uint>(ref pixel)).AsByte());
            Vector128<short> narrow = Sse2.PackSignedSaturate(sum, sum);
            Unsafe.WriteUnaligned(ref pixel, Sse2.PackUnsignedSaturate(narrow, narrow).AsUInt32().ToScalar());
        }
    }

    private static void Transpose4x4(Vector128<int> r0, Vector128<int> r1, Vector128<int> r2, Vector128<int> r3, Span<Vector128<int>> output)
    {
        Vector128<int> t0 = Sse2.UnpackLow(r0, r1), t1 = Sse2.UnpackLow(r2, r3), t2 = Sse2.UnpackHigh(r0, r1), t3 = Sse2.UnpackHigh(r2, r3);
        output[0] = Sse2.UnpackLow(t0.AsInt64(), t1.AsInt64()).AsInt32();
        output[1] = Sse2.UnpackHigh(t0.AsInt64(), t1.AsInt64()).AsInt32();
        output[2] = Sse2.UnpackLow(t2.AsInt64(), t3.AsInt64()).AsInt32();
        output[3] = Sse2.UnpackHigh(t2.AsInt64(), t3.AsInt64()).AsInt32();
    }

    /// <summary>Loads the 8 by 8 piece at <paramref name="source"/> (rows <paramref name="pitch"/> apart) as its 8 columns.</summary>
    private static void Transpose8x8(ReadOnlySpan<int> source, int pitch, Span<Vector256<int>> output)
    {
        Transpose(Vector256.Create(source[..8]), Vector256.Create(source.Slice(pitch, 8)), Vector256.Create(source.Slice(2 * pitch, 8)),
            Vector256.Create(source.Slice(3 * pitch, 8)), Vector256.Create(source.Slice(4 * pitch, 8)), Vector256.Create(source.Slice(5 * pitch, 8)),
            Vector256.Create(source.Slice(6 * pitch, 8)), Vector256.Create(source.Slice(7 * pitch, 8)), output);
    }

    /// <summary>Stores 8 vectors, each a column of an 8 by 8 piece, as its rows at <paramref name="target"/>.</summary>
    private static void TransposeBack8x8(ReadOnlySpan<Vector256<int>> columns, Span<int> target, int pitch)
    {
        Span<Vector256<int>> rows = stackalloc Vector256<int>[8];
        Transpose(columns[0], columns[1], columns[2], columns[3], columns[4], columns[5], columns[6], columns[7], rows);
        for (int i = 0; i < 8; i++)
        {
            rows[i].CopyTo(target.Slice(i * pitch, 8));
        }
    }

    private static void Transpose(Vector256<int> r0, Vector256<int> r1, Vector256<int> r2, Vector256<int> r3, Vector256<int> r4, Vector256<int> r5,
        Vector256<int> r6, Vector256<int> r7, Span<Vector256<int>> output)
    {
        Vector256<int> t0 = Avx2.UnpackLow(r0, r1), t1 = Avx2.UnpackHigh(r0, r1), t2 = Avx2.UnpackLow(r2, r3), t3 = Avx2.UnpackHigh(r2, r3);
        Vector256<int> t4 = Avx2.UnpackLow(r4, r5), t5 = Avx2.UnpackHigh(r4, r5), t6 = Avx2.UnpackLow(r6, r7), t7 = Avx2.UnpackHigh(r6, r7);
        Vector256<int> u0 = Avx2.UnpackLow(t0.AsInt64(), t2.AsInt64()).AsInt32(), u1 = Avx2.UnpackHigh(t0.AsInt64(), t2.AsInt64()).AsInt32();
        Vector256<int> u2 = Avx2.UnpackLow(t1.AsInt64(), t3.AsInt64()).AsInt32(), u3 = Avx2.UnpackHigh(t1.AsInt64(), t3.AsInt64()).AsInt32();
        Vector256<int> u4 = Avx2.UnpackLow(t4.AsInt64(), t6.AsInt64()).AsInt32(), u5 = Avx2.UnpackHigh(t4.AsInt64(), t6.AsInt64()).AsInt32();
        Vector256<int> u6 = Avx2.UnpackLow(t5.AsInt64(), t7.AsInt64()).AsInt32(), u7 = Avx2.UnpackHigh(t5.AsInt64(), t7.AsInt64()).AsInt32();
        output[0] = Avx2.Permute2x128(u0, u4, 0x20);
        output[1] = Avx2.Permute2x128(u1, u5, 0x20);
        output[2] = Avx2.Permute2x128(u2, u6, 0x20);
        output[3] = Avx2.Permute2x128(u3, u7, 0x20);
        output[4] = Avx2.Permute2x128(u0, u4, 0x31);
        output[5] = Avx2.Permute2x128(u1, u5, 0x31);
        output[6] = Avx2.Permute2x128(u2, u6, 0x31);
        output[7] = Avx2.Permute2x128(u3, u7, 0x31);
    }

    /// <summary>Adds the same value to every pixel of an n by n block, clamped to 0..255.</summary>
    private static void AddConstant(Span<byte> dst, int stride, int n, int value)
    {
        if (n == 4)
        {
            for (int y = 0; y < 4; y++)
            {
                Span<byte> row = dst.Slice(y * stride, 4);
                for (int x = 0; x < 4; x++)
                {
                    row[x] = (byte)Math.Clamp(row[x] + value, 0, 255);
                }
            }

            return;
        }

        // Saturating byte adds of |value| clamped to 255, up or down, give the clamped sum.
        bool add = value >= 0;
        Vector128<byte> amount = Vector128.Create((byte)Math.Min(Math.Abs(value), 255));
        for (int y = 0; y < n; y++)
        {
            ref byte row = ref MemoryMarshal.GetReference(dst.Slice(y * stride));
            if (n == 8)
            {
                Vector128<byte> v = Vector128.CreateScalarUnsafe(Unsafe.ReadUnaligned<ulong>(ref row)).AsByte();
                v = add ? AddSaturate(v, amount) : SubtractSaturate(v, amount);
                Unsafe.WriteUnaligned(ref row, v.AsUInt64().ToScalar());
                continue;
            }

            for (int x = 0; x < n; x += 16)
            {
                Vector128<byte> v = Vector128.LoadUnsafe(ref row, (nuint)x);
                v = add ? AddSaturate(v, amount) : SubtractSaturate(v, amount);
                v.StoreUnsafe(ref row, (nuint)x);
            }
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> AddSaturate(Vector128<byte> a, Vector128<byte> b) =>
        Vector128.Min(a, Vector128.Create((byte)255) - b) + b;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<byte> SubtractSaturate(Vector128<byte> a, Vector128<byte> b) => Vector128.Max(a, b) - b;

    /// <summary>dct_const_round_shift on a scalar.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static long Rs(long value) => (value + (1 << 13)) >> 14;

    /// <summary>dct_const_round_shift a lane at a time.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector128<int> Rs(Vector128<int> value) => Vector128.ShiftRightArithmetic(value + Vector128.Create(1 << 13), 14);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<int> Rs(Vector256<int> value) => Vector256.ShiftRightArithmetic(value + Vector256.Create(1 << 13), 14);
}
