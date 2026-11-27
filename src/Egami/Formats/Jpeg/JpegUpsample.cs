using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Simd;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Chroma upsampling kernels, each with a scalar form and, where it pays, an AVX2 form and a 128-bit form (x86 and Arm64) that
/// give exactly the same bytes.
/// The smooth (triangle) filters are libjpeg's: 3/4 of the nearer sample and 1/4 of the next, with its rounding biases.
/// </summary>
internal static class JpegUpsample
{
    /// <summary>
    /// Doubles a row horizontally with the triangle filter. <paramref name="sums"/> holds the n samples (or column sums) at 1..n,
    /// with the first and last repeated at 0 and n + 1; output 2i is (3 s[i] + s[i - 1] + <paramref name="evenBias"/>) &gt;&gt;
    /// <paramref name="shift"/>, output 2i + 1 uses s[i + 1] and <paramref name="oddBias"/>, and the edges repeat their sample.
    /// </summary>
    internal static void Double(ReadOnlySpan<short> sums, int n, Span<byte> output, int evenBias, int oddBias, int shift)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            i = DoubleAvx2(sums, n, output, evenBias, oddBias, shift);
        }
        else if (Simd128.IsSupported)
        {
            i = Double128(sums, n, output, evenBias, oddBias, shift);
        }

        DoubleScalar(sums, i, n, output, evenBias, oddBias, shift);
    }

    /// <summary>The scalar form of <see cref="Double"/> from sample <paramref name="start"/> on.</summary>
    internal static void DoubleScalar(ReadOnlySpan<short> sums, int start, int n, Span<byte> output, int evenBias, int oddBias, int shift)
    {
        for (int i = start; i < n; i++)
        {
            int m = 3 * sums[i + 1];
            output[2 * i] = (byte)((m + sums[i] + evenBias) >> shift);
            if ((2 * i) + 1 < output.Length)
            {
                output[(2 * i) + 1] = (byte)((m + sums[i + 2] + oddBias) >> shift);
            }
        }
    }

    /// <summary>Doubles 16 samples at a time while whole outputs fit; returns how many samples it did.</summary>
    internal static int DoubleAvx2(ReadOnlySpan<short> sums, int n, Span<byte> output, int evenBias, int oddBias, int shift)
    {
        ref short s = ref MemoryMarshal.GetReference(sums);
        ref byte o = ref MemoryMarshal.GetReference(output);
        Vector256<short> even = Vector256.Create((short)evenBias);
        Vector256<short> odd = Vector256.Create((short)oddBias);
        Vector256<byte> interleave = Vector256.Create((byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15, 0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);
        int i = 0;

        // Loads reach sums[i + 17], which must exist: the sums row has n + 2 entries.
        for (; i + 16 <= n && (2 * i) + 32 <= output.Length; i += 16)
        {
            Vector256<short> left = Vector256.LoadUnsafe(ref s, (nuint)i);
            Vector256<short> middle = Vector256.LoadUnsafe(ref s, (nuint)(i + 1));
            Vector256<short> right = Vector256.LoadUnsafe(ref s, (nuint)(i + 2));
            Vector256<short> m = middle + middle + middle;
            Vector256<short> e = Vector256.ShiftRightArithmetic(m + left + even, shift);
            Vector256<short> d = Vector256.ShiftRightArithmetic(m + right + odd, shift);
            Vector256<byte> packed = Avx2.Shuffle(Avx2.PackUnsignedSaturate(e, d), interleave);
            packed.StoreUnsafe(ref o, (nuint)(2 * i));
        }

        return i;
    }

    /// <summary>Doubles 8 samples at a time in 128-bit vectors while whole outputs fit; returns how many samples it did.</summary>
    internal static int Double128(ReadOnlySpan<short> sums, int n, Span<byte> output, int evenBias, int oddBias, int shift)
    {
        ref short s = ref MemoryMarshal.GetReference(sums);
        ref byte o = ref MemoryMarshal.GetReference(output);
        Vector128<short> even = Vector128.Create((short)evenBias);
        Vector128<short> odd = Vector128.Create((short)oddBias);
        Vector128<byte> interleave = Vector128.Create((byte)0, 8, 1, 9, 2, 10, 3, 11, 4, 12, 5, 13, 6, 14, 7, 15);
        int i = 0;

        // Loads reach sums[i + 9], which must exist: the sums row has n + 2 entries.
        for (; i + 8 <= n && (2 * i) + 16 <= output.Length; i += 8)
        {
            Vector128<short> left = Vector128.LoadUnsafe(ref s, (nuint)i);
            Vector128<short> middle = Vector128.LoadUnsafe(ref s, (nuint)(i + 1));
            Vector128<short> right = Vector128.LoadUnsafe(ref s, (nuint)(i + 2));
            Vector128<short> m = middle + middle + middle;
            Vector128<short> e = Vector128.ShiftRightArithmetic(m + left + even, shift);
            Vector128<short> d = Vector128.ShiftRightArithmetic(m + right + odd, shift);
            Simd128.Shuffle(Simd128.PackUnsignedSaturate(e, d), interleave).StoreUnsafe(ref o, (nuint)(2 * i));
        }

        return i;
    }

    /// <summary>Fills <paramref name="sums"/> for <see cref="Double"/> from a row of n samples, padding both ends.</summary>
    internal static void Widen(ReadOnlySpan<byte> row, int n, Span<short> sums)
    {
        for (int i = 0; i < n; i++)
        {
            sums[i + 1] = row[i];
        }

        Pad(sums, n);
    }

    /// <summary>Fills <paramref name="sums"/> with the column sums 3 × near + far of two rows of n samples, padding both ends.</summary>
    internal static void ColumnSums(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, int n, Span<short> sums)
    {
        int i = 0;
        if (Avx2.IsSupported)
        {
            ref byte a = ref MemoryMarshal.GetReference(near);
            ref byte b = ref MemoryMarshal.GetReference(far);
            ref short s = ref Unsafe.Add(ref MemoryMarshal.GetReference(sums), 1);
            for (; i + 16 <= n; i += 16)
            {
                Vector256<short> x = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref a, (nuint)i));
                Vector256<short> y = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref b, (nuint)i));
                (x + x + x + y).StoreUnsafe(ref s, (nuint)i);
            }
        }
        else if (Simd128.IsSupported)
        {
            ref byte a = ref MemoryMarshal.GetReference(near);
            ref byte b = ref MemoryMarshal.GetReference(far);
            ref short s = ref Unsafe.Add(ref MemoryMarshal.GetReference(sums), 1);
            for (; i + 16 <= n; i += 16)
            {
                Vector128<byte> x = Vector128.LoadUnsafe(ref a, (nuint)i);
                Vector128<byte> y = Vector128.LoadUnsafe(ref b, (nuint)i);
                Vector128<short> xl = Vector128.WidenLower(x).AsInt16(), yl = Vector128.WidenLower(y).AsInt16();
                Vector128<short> xh = Vector128.WidenUpper(x).AsInt16(), yh = Vector128.WidenUpper(y).AsInt16();
                (xl + xl + xl + yl).StoreUnsafe(ref s, (nuint)i);
                (xh + xh + xh + yh).StoreUnsafe(ref s, (nuint)(i + 8));
            }
        }

        for (; i < n; i++)
        {
            sums[i + 1] = (short)((3 * near[i]) + far[i]);
        }

        Pad(sums, n);
    }

    /// <summary>
    /// The vertical triangle filter: output x is (3 near[x] + far[x] + <paramref name="bias"/>) &gt;&gt; 2, for rows of the same
    /// width (4:4:0 sampling).
    /// </summary>
    internal static void Vertical(ReadOnlySpan<byte> near, ReadOnlySpan<byte> far, Span<byte> output, int bias)
    {
        int x = 0;
        if (Avx2.IsSupported)
        {
            ref byte a = ref MemoryMarshal.GetReference(near);
            ref byte b = ref MemoryMarshal.GetReference(far);
            ref byte o = ref MemoryMarshal.GetReference(output);
            Vector256<short> round = Vector256.Create((short)bias);
            Vector256<byte> order = Vector256.Create((byte)0, 2, 4, 6, 8, 10, 12, 14, 255, 255, 255, 255, 255, 255, 255, 255, 0, 2, 4, 6, 8, 10, 12, 14, 255, 255, 255, 255, 255, 255, 255, 255);
            for (; x + 16 <= output.Length && x + 16 <= near.Length && x + 16 <= far.Length; x += 16)
            {
                Vector256<short> n = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref a, (nuint)x));
                Vector256<short> f = Avx2.ConvertToVector256Int16(Vector128.LoadUnsafe(ref b, (nuint)x));
                Vector256<short> v = Vector256.ShiftRightArithmetic(n + n + n + f + round, 2);
                Vector256<ulong> bytes = Avx2.Shuffle(v.AsByte(), order).AsUInt64();
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, x), bytes.GetElement(0));
                Unsafe.WriteUnaligned(ref Unsafe.Add(ref o, x + 8), bytes.GetElement(2));
            }
        }
        else if (Simd128.IsSupported)
        {
            ref byte a = ref MemoryMarshal.GetReference(near);
            ref byte b = ref MemoryMarshal.GetReference(far);
            ref byte o = ref MemoryMarshal.GetReference(output);
            Vector128<short> round = Vector128.Create((short)bias);
            for (; x + 16 <= output.Length && x + 16 <= near.Length && x + 16 <= far.Length; x += 16)
            {
                Vector128<byte> n = Vector128.LoadUnsafe(ref a, (nuint)x);
                Vector128<byte> f = Vector128.LoadUnsafe(ref b, (nuint)x);
                Vector128<short> nl = Vector128.WidenLower(n).AsInt16(), fl = Vector128.WidenLower(f).AsInt16();
                Vector128<short> nh = Vector128.WidenUpper(n).AsInt16(), fh = Vector128.WidenUpper(f).AsInt16();
                Vector128<short> low = Vector128.ShiftRightArithmetic(nl + nl + nl + fl + round, 2);
                Vector128<short> high = Vector128.ShiftRightArithmetic(nh + nh + nh + fh + round, 2);

                // Every value is 0..255, so the narrowing keeps it whole.
                Vector128.Narrow(low, high).AsByte().StoreUnsafe(ref o, (nuint)x);
            }
        }

        for (; x < output.Length; x++)
        {
            output[x] = (byte)(((3 * near[x]) + far[x] + bias) >> 2);
        }
    }

    /// <summary>Replicates each sample <paramref name="factor"/> times across, filling <paramref name="output"/>.</summary>
    internal static void Replicate(ReadOnlySpan<byte> row, int factor, Span<byte> output)
    {
        int width = output.Length;
        if (factor == 1)
        {
            row[..width].CopyTo(output);
            return;
        }

        int x = 0;
        if (factor == 2)
        {
            if (Simd128.IsSupported)
            {
                ref byte r = ref MemoryMarshal.GetReference(row);
                ref byte o = ref MemoryMarshal.GetReference(output);
                for (; x + 32 <= width && (x / 2) + 16 <= row.Length; x += 32)
                {
                    Vector128<byte> v = Vector128.LoadUnsafe(ref r, (nuint)(x / 2));
                    Simd128.ZipLow(v, v).StoreUnsafe(ref o, (nuint)x);
                    Simd128.ZipHigh(v, v).StoreUnsafe(ref o, (nuint)(x + 16));
                }
            }

            for (; x < width; x++)
            {
                output[x] = row[x >> 1];
            }

            return;
        }

        for (; x < width; x++)
        {
            output[x] = row[x / factor];
        }
    }

    private static void Pad(Span<short> sums, int n)
    {
        sums[0] = sums[1];
        sums[n + 1] = sums[n];
    }
}
