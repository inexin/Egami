using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// The inverse discrete wavelet transforms of T.800 Annex F: the reversible 5/3 filter in integers and the irreversible 9/7
/// filter in floats, by lifting with symmetric extension, rows first then columns at each level (2D_SR).
/// </summary>
/// <remarks>
/// Lifting runs on the subbands as they lie, low samples then high ones, so every step is a plain vector loop: X(2n) less a
/// multiple of X(2n−1) + X(2n+1) is L[k] less a multiple of H[k − 1] + H[k] (or H[k] + H[k + 1] when the row starts on an odd
/// coordinate). Symmetric extension mirrors each band onto itself, so one guard sample at each end of each band, a copy of the
/// band's edge sample, stands for it. The samples are interleaved once at the end.
/// </remarks>
internal static class J2kWavelet
{
    private const float Alpha = -1.586134342059924f;
    private const float Beta = -0.052980118572961f;
    private const float Gamma = 0.882911075530934f;
    private const float Delta = 0.443506852043971f;
    private const float K = 1.230174104914001f;
    private const float InverseK = 1 / K;

    /// <summary>
    /// Runs the inverse 5/3 transform in place on a tile-component's coefficients, laid out with each resolution's low band at
    /// the top left, up to resolution <paramref name="top"/>.
    /// </summary>
    internal static void Reverse53(Span<int> buffer, int stride, J2kResolution[] resolutions, int top)
    {
        int[] temp = ArrayPool<int>.Shared.Rent(Scratch(resolutions, top));
        try
        {
            for (int r = 1; r <= top; r++)
            {
                J2kResolution res = resolutions[r];
                J2kResolution low = resolutions[r - 1];
                int w = res.Width;
                int h = res.Height;
                if (w == 0 || h == 0)
                {
                    continue;
                }

                for (int y = 0; y < h; y++)
                {
                    Row53(buffer.Slice(y * stride, w), low.Width, res.X0 & 1, temp);
                }

                Columns53(buffer, stride, w, h, low.Height, res.Y0 & 1, temp);
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(temp);
        }
    }

    /// <summary>Runs the inverse 9/7 transform in place; see <see cref="Reverse53"/>.</summary>
    internal static void Reverse97(Span<float> buffer, int stride, J2kResolution[] resolutions, int top)
    {
        float[] temp = ArrayPool<float>.Shared.Rent(Scratch(resolutions, top));
        try
        {
            for (int r = 1; r <= top; r++)
            {
                J2kResolution res = resolutions[r];
                J2kResolution low = resolutions[r - 1];
                int w = res.Width;
                int h = res.Height;
                if (w == 0 || h == 0)
                {
                    continue;
                }

                for (int y = 0; y < h; y++)
                {
                    Row97(buffer.Slice(y * stride, w), low.Width, res.X0 & 1, temp);
                }

                Columns97(buffer, stride, w, h, low.Height, res.Y0 & 1, temp);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
        }
    }

    private static int Scratch(J2kResolution[] resolutions, int top)
    {
        int max = 8;
        for (int r = 1; r <= top; r++)
        {
            max = Math.Max(max, (resolutions[r].Width * resolutions[r].Height) + 8);
        }

        return max;
    }

    /// <summary>
    /// One row of the 5/3 synthesis (F.3.8.1): <paramref name="lowCount"/> low samples then the high ones, interleaved back
    /// into the row; <paramref name="parity"/> is 1 when the row starts on an odd coordinate, so with a high sample.
    /// </summary>
    private static void Row53(Span<int> row, int lowCount, int parity, int[] temp)
    {
        int n = row.Length;
        if (n == 1)
        {
            if (parity == 1)
            {
                row[0] >>= 1;
            }

            return;
        }

        int highCount = n - lowCount;

        // Bands with a guard at each end: [g L0 .. Ln-1 g][g H0 .. Hm-1 g].
        Span<int> low = temp.AsSpan(1, lowCount);
        Span<int> high = temp.AsSpan(lowCount + 3, highCount);
        row[..lowCount].CopyTo(low);
        row[lowCount..].CopyTo(high);
        Guard(temp.AsSpan(0, lowCount + 2));
        Guard(temp.AsSpan(lowCount + 2, highCount + 2));

        // X(2n) -= floor((X(2n-1) + X(2n+1) + 2) / 4) on the low band, then X(2n+1) += floor((X(2n) + X(2n+2)) / 2) on the high.
        ReadOnlySpan<int> highLeft = temp.AsSpan(lowCount + 2 + parity, lowCount);
        ReadOnlySpan<int> highRight = temp.AsSpan(lowCount + 3 + parity, lowCount);
        LowStep53(low, highLeft, highRight);
        Guard(temp.AsSpan(0, lowCount + 2));
        ReadOnlySpan<int> lowLeft = temp.AsSpan(1 - parity, highCount);
        ReadOnlySpan<int> lowRight = temp.AsSpan(2 - parity, highCount);
        HighStep53(high, lowLeft, lowRight);
        Interleave(low, high, row, parity);
    }

    /// <summary>One row of the 9/7 synthesis (F.3.8.2); see <see cref="Row53"/>.</summary>
    private static void Row97(Span<float> row, int lowCount, int parity, float[] temp)
    {
        int n = row.Length;
        if (n == 1)
        {
            if (parity == 1)
            {
                row[0] *= 0.5f;
            }

            return;
        }

        int highCount = n - lowCount;
        Span<float> low = temp.AsSpan(1, lowCount);
        Span<float> high = temp.AsSpan(lowCount + 3, highCount);
        Scale(row[..lowCount], low, K);
        Scale(row[lowCount..], high, InverseK);
        Span<float> lowGuarded = temp.AsSpan(0, lowCount + 2);
        Span<float> highGuarded = temp.AsSpan(lowCount + 2, highCount + 2);
        Guard(highGuarded);
        ReadOnlySpan<float> highLeft = temp.AsSpan(lowCount + 2 + parity, lowCount);
        ReadOnlySpan<float> highRight = temp.AsSpan(lowCount + 3 + parity, lowCount);
        ReadOnlySpan<float> lowLeft = temp.AsSpan(1 - parity, highCount);
        ReadOnlySpan<float> lowRight = temp.AsSpan(2 - parity, highCount);
        Lift(low, highLeft, highRight, Delta);
        Guard(lowGuarded);
        Lift(high, lowLeft, lowRight, Gamma);
        Guard(highGuarded);
        Lift(low, highLeft, highRight, Beta);
        Guard(lowGuarded);
        Lift(high, lowLeft, lowRight, Alpha);
        Interleave(low, high, row, parity);
    }

    /// <summary>Sets a guarded band's guards to copies of its edge samples: symmetric extension of the band onto itself.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void Guard<T>(Span<T> band)
    {
        band[0] = band[1];
        band[^1] = band[^2];
    }

    private static void LowStep53(Span<int> target, ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<int> two = new(2);
            for (; x + Vector<int>.Count <= target.Length; x += Vector<int>.Count)
            {
                Vector<int> sum = new Vector<int>(left[x..]) + new Vector<int>(right[x..]) + two;
                (new Vector<int>(target[x..]) - Vector.ShiftRightArithmetic(sum, 2)).CopyTo(target[x..]);
            }
        }

        for (; x < target.Length; x++)
        {
            target[x] -= (left[x] + right[x] + 2) >> 2;
        }
    }

    private static void HighStep53(Span<int> target, ReadOnlySpan<int> left, ReadOnlySpan<int> right)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x + Vector<int>.Count <= target.Length; x += Vector<int>.Count)
            {
                Vector<int> sum = new Vector<int>(left[x..]) + new Vector<int>(right[x..]);
                (new Vector<int>(target[x..]) + Vector.ShiftRightArithmetic(sum, 1)).CopyTo(target[x..]);
            }
        }

        for (; x < target.Length; x++)
        {
            target[x] += (left[x] + right[x]) >> 1;
        }
    }

    /// <summary>One 9/7 lifting step: <paramref name="target"/> less <paramref name="c"/> times its two neighbours' sum.</summary>
    private static void Lift(Span<float> target, ReadOnlySpan<float> left, ReadOnlySpan<float> right, float c)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<float> factor = new(c);
            for (; x + Vector<float>.Count <= target.Length; x += Vector<float>.Count)
            {
                Vector<float> sum = new Vector<float>(left[x..]) + new Vector<float>(right[x..]);
                (new Vector<float>(target[x..]) - (factor * sum)).CopyTo(target[x..]);
            }
        }

        for (; x < target.Length; x++)
        {
            target[x] -= c * (left[x] + right[x]);
        }
    }

    private static void Scale(ReadOnlySpan<float> source, Span<float> target, float factor)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x + Vector<float>.Count <= source.Length; x += Vector<float>.Count)
            {
                (new Vector<float>(source[x..]) * factor).CopyTo(target[x..]);
            }
        }

        for (; x < source.Length; x++)
        {
            target[x] = source[x] * factor;
        }
    }

    /// <summary>Interleaves the low and high bands into a row (2D_INTERLEAVE), the first sample low unless <paramref name="parity"/> is 1.</summary>
    private static void Interleave<T>(ReadOnlySpan<T> low, ReadOnlySpan<T> high, Span<T> row, int parity)
        where T : unmanaged
    {
        ReadOnlySpan<T> even = parity == 0 ? low : high;
        ReadOnlySpan<T> odd = parity == 0 ? high : low;
        int pairs = Math.Min(even.Length, odd.Length);
        int k = 0;
        if (Sse2.IsSupported && Unsafe.SizeOf<T>() == 4)
        {
            ref float e = ref Unsafe.As<T, float>(ref MemoryMarshal.GetReference(even));
            ref float o = ref Unsafe.As<T, float>(ref MemoryMarshal.GetReference(odd));
            ref float t = ref Unsafe.As<T, float>(ref MemoryMarshal.GetReference(row));
            for (; k + 4 <= pairs; k += 4)
            {
                Vector128<float> a = Vector128.LoadUnsafe(ref e, (nuint)k);
                Vector128<float> b = Vector128.LoadUnsafe(ref o, (nuint)k);
                Sse.UnpackLow(a, b).StoreUnsafe(ref t, (nuint)(2 * k));
                Sse.UnpackHigh(a, b).StoreUnsafe(ref t, (nuint)((2 * k) + 4));
            }
        }

        for (; k < pairs; k++)
        {
            row[2 * k] = even[k];
            row[(2 * k) + 1] = odd[k];
        }

        if (even.Length > pairs)
        {
            row[2 * pairs] = even[pairs];
        }
    }

    /// <summary>
    /// The vertical 5/3 synthesis over a level's columns, a whole row at a time: lifting on the low rows (the first
    /// <paramref name="lowCount"/>) and the high ones in place, then the rows interleaved through the scratch buffer.
    /// </summary>
    private static void Columns53(Span<int> buffer, int stride, int w, int h, int lowCount, int parity, int[] temp)
    {
        if (h == 1)
        {
            if (parity == 1)
            {
                Span<int> row = buffer[..w];
                for (int x = 0; x < w; x++)
                {
                    row[x] >>= 1;
                }
            }

            return;
        }

        int highCount = h - lowCount;
        for (int k = 0; k < lowCount; k++)
        {
            (int a, int b) = Neighbours(k, parity, highCount);
            LowStep53(buffer.Slice(k * stride, w), buffer.Slice((lowCount + a) * stride, w), buffer.Slice((lowCount + b) * stride, w));
        }

        for (int k = 0; k < highCount; k++)
        {
            (int a, int b) = Neighbours(k, 1 - parity, lowCount, high: true);
            HighStep53(buffer.Slice((lowCount + k) * stride, w), buffer.Slice(a * stride, w), buffer.Slice(b * stride, w));
        }

        InterleaveRows(buffer, stride, w, h, lowCount, parity, temp.AsSpan(0, w * h));
    }

    /// <summary>The vertical 9/7 synthesis; see <see cref="Columns53"/>.</summary>
    private static void Columns97(Span<float> buffer, int stride, int w, int h, int lowCount, int parity, float[] temp)
    {
        if (h == 1)
        {
            if (parity == 1)
            {
                Span<float> row = buffer[..w];
                for (int x = 0; x < w; x++)
                {
                    row[x] *= 0.5f;
                }
            }

            return;
        }

        int highCount = h - lowCount;
        for (int k = 0; k < lowCount; k++)
        {
            Span<float> row = buffer.Slice(k * stride, w);
            Scale(row, row, K);
        }

        for (int k = 0; k < highCount; k++)
        {
            Span<float> row = buffer.Slice((lowCount + k) * stride, w);
            Scale(row, row, InverseK);
        }

        RowsStep(buffer, stride, w, lowCount, highCount, parity, Delta, lowTarget: true);
        RowsStep(buffer, stride, w, lowCount, highCount, parity, Gamma, lowTarget: false);
        RowsStep(buffer, stride, w, lowCount, highCount, parity, Beta, lowTarget: true);
        RowsStep(buffer, stride, w, lowCount, highCount, parity, Alpha, lowTarget: false);
        InterleaveRows(buffer, stride, w, h, lowCount, parity, temp.AsSpan(0, w * h));
    }

    private static void RowsStep(Span<float> buffer, int stride, int w, int lowCount, int highCount, int parity, float c, bool lowTarget)
    {
        if (lowTarget)
        {
            for (int k = 0; k < lowCount; k++)
            {
                (int a, int b) = Neighbours(k, parity, highCount);
                Lift(buffer.Slice(k * stride, w), buffer.Slice((lowCount + a) * stride, w), buffer.Slice((lowCount + b) * stride, w), c);
            }
        }
        else
        {
            for (int k = 0; k < highCount; k++)
            {
                (int a, int b) = Neighbours(k, 1 - parity, lowCount, high: true);
                Lift(buffer.Slice((lowCount + k) * stride, w), buffer.Slice(a * stride, w), buffer.Slice(b * stride, w), c);
            }
        }
    }

    /// <summary>
    /// Returns the two rows of the other band next to row <paramref name="k"/> of a band, mirrored at the ends: for a low row
    /// k they are high rows k − 1 and k (or k and k + 1 when the level starts on an odd row); for a high row, low rows k and
    /// k + 1 (or k − 1 and k).
    /// </summary>
    private static (int A, int B) Neighbours(int k, int parity, int otherCount, bool high = false)
    {
        int a;
        int b;
        if (!high)
        {
            (a, b) = parity == 0 ? (k - 1, k) : (k, k + 1);
        }
        else
        {
            (a, b) = parity == 1 ? (k, k + 1) : (k - 1, k);
        }

        return (Math.Clamp(a, 0, otherCount - 1), Math.Clamp(b, 0, otherCount - 1));
    }

    /// <summary>Puts a level's low and high rows in coordinate order, through <paramref name="scratch"/>.</summary>
    private static void InterleaveRows<T>(Span<T> buffer, int stride, int w, int h, int lowCount, int parity, Span<T> scratch)
    {
        for (int k = 0, i = parity; i < h; k++, i += 2)
        {
            buffer.Slice(k * stride, w).CopyTo(scratch.Slice(i * w, w));
        }

        for (int k = 0, i = 1 - parity; i < h; k++, i += 2)
        {
            buffer.Slice((lowCount + k) * stride, w).CopyTo(scratch.Slice(i * w, w));
        }

        for (int i = 0; i < h; i++)
        {
            scratch.Slice(i * w, w).CopyTo(buffer.Slice(i * stride, w));
        }
    }
}
