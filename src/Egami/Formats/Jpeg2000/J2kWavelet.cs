using System.Buffers;
using System.Numerics;

namespace Egami.Formats.Jpeg2000;

/// <summary>
/// The inverse discrete wavelet transforms of T.800 Annex F: the reversible 5/3 filter in integers and the irreversible 9/7
/// filter in floats, by lifting with symmetric extension, rows first then columns at each level (2D_SR).
/// </summary>
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
        int max = 0;
        for (int r = 1; r <= top; r++)
        {
            max = Math.Max(max, resolutions[r].Width * resolutions[r].Height);
            max = Math.Max(max, resolutions[r].Width + resolutions[r].Height);
        }

        int[] temp = ArrayPool<int>.Shared.Rent(Math.Max(max, 1));
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

                int px = res.X0 & 1;
                int py = res.Y0 & 1;
                for (int y = 0; y < h; y++)
                {
                    Span<int> row = buffer.Slice(y * stride, w);
                    Interleave(row, temp.AsSpan(0, w), low.Width, px);
                    Lift53(temp.AsSpan(0, w), px);
                    temp.AsSpan(0, w).CopyTo(row);
                }

                Columns53(buffer, stride, w, h, low.Height, py, temp);
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
        int max = 0;
        for (int r = 1; r <= top; r++)
        {
            max = Math.Max(max, resolutions[r].Width * resolutions[r].Height);
            max = Math.Max(max, resolutions[r].Width + resolutions[r].Height);
        }

        float[] temp = ArrayPool<float>.Shared.Rent(Math.Max(max, 1));
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

                int px = res.X0 & 1;
                int py = res.Y0 & 1;
                for (int y = 0; y < h; y++)
                {
                    Span<float> row = buffer.Slice(y * stride, w);
                    Interleave(row, temp.AsSpan(0, w), low.Width, px);
                    Lift97(temp.AsSpan(0, w), px);
                    temp.AsSpan(0, w).CopyTo(row);
                }

                Columns97(buffer, stride, w, h, low.Height, py, temp);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(temp);
        }
    }

    /// <summary>Interleaves a row's low samples (its first <paramref name="lowCount"/>) with its high ones (2D_INTERLEAVE).</summary>
    private static void Interleave<T>(ReadOnlySpan<T> source, Span<T> target, int lowCount, int parity)
    {
        int n = target.Length;
        for (int k = 0, i = parity; i < n; k++, i += 2)
        {
            target[i] = source[k];
        }

        for (int k = 0, i = 1 - parity; i < n; k++, i += 2)
        {
            target[i] = source[lowCount + k];
        }
    }

    /// <summary>The 1D 5/3 synthesis (F.3.8.1) on interleaved samples; <paramref name="parity"/> 1 when the first is a high one.</summary>
    private static void Lift53(Span<int> x, int parity)
    {
        int n = x.Length;
        if (n == 1)
        {
            if (parity == 1)
            {
                x[0] >>= 1;
            }

            return;
        }

        // Even (low) samples: X(2n) -= floor((X(2n-1) + X(2n+1) + 2) / 4), mirroring at both ends.
        for (int i = parity; i < n; i += 2)
        {
            int left = i > 0 ? x[i - 1] : x[1];
            int right = i + 1 < n ? x[i + 1] : x[i - 1];
            x[i] -= (left + right + 2) >> 2;
        }

        // Odd (high) samples: X(2n+1) += floor((X(2n) + X(2n+2)) / 2).
        for (int i = 1 - parity; i < n; i += 2)
        {
            int left = i > 0 ? x[i - 1] : x[1];
            int right = i + 1 < n ? x[i + 1] : x[i - 1];
            x[i] += (left + right) >> 1;
        }
    }

    /// <summary>The 1D 9/7 synthesis (F.3.8.2) on interleaved samples.</summary>
    private static void Lift97(Span<float> x, int parity)
    {
        int n = x.Length;
        if (n == 1)
        {
            if (parity == 1)
            {
                x[0] *= 0.5f;
            }

            return;
        }

        for (int i = parity; i < n; i += 2)
        {
            x[i] *= K;
        }

        for (int i = 1 - parity; i < n; i += 2)
        {
            x[i] *= InverseK;
        }

        Step(x, parity, Delta);
        Step(x, 1 - parity, Gamma);
        Step(x, parity, Beta);
        Step(x, 1 - parity, Alpha);
    }

    /// <summary>One lifting step: every other sample from <paramref name="first"/> less <paramref name="c"/> times its neighbours' sum.</summary>
    private static void Step(Span<float> x, int first, float c)
    {
        int n = x.Length;
        for (int i = first; i < n; i += 2)
        {
            float left = i > 0 ? x[i - 1] : x[1];
            float right = i + 1 < n ? x[i + 1] : x[i - 1];
            x[i] -= c * (left + right);
        }
    }

    /// <summary>The vertical 5/3 synthesis over a level's columns, whole rows at a time.</summary>
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

        // Rows into interleaved order in the scratch buffer.
        Span<int> rows = temp.AsSpan(0, w * h);
        for (int k = 0, i = parity; i < h; k++, i += 2)
        {
            buffer.Slice(k * stride, w).CopyTo(rows.Slice(i * w, w));
        }

        for (int k = 0, i = 1 - parity; i < h; k++, i += 2)
        {
            buffer.Slice((lowCount + k) * stride, w).CopyTo(rows.Slice(i * w, w));
        }

        for (int i = parity; i < h; i += 2)
        {
            Span<int> row = rows.Slice(i * w, w);
            ReadOnlySpan<int> above = rows.Slice((i > 0 ? i - 1 : 1) * w, w);
            ReadOnlySpan<int> below = rows.Slice((i + 1 < h ? i + 1 : i - 1) * w, w);
            Low53(row, above, below);
        }

        for (int i = 1 - parity; i < h; i += 2)
        {
            Span<int> row = rows.Slice(i * w, w);
            ReadOnlySpan<int> above = rows.Slice((i > 0 ? i - 1 : 1) * w, w);
            ReadOnlySpan<int> below = rows.Slice((i + 1 < h ? i + 1 : i - 1) * w, w);
            High53(row, above, below);
        }

        for (int i = 0; i < h; i++)
        {
            rows.Slice(i * w, w).CopyTo(buffer.Slice(i * stride, w));
        }
    }

    private static void Low53(Span<int> row, ReadOnlySpan<int> above, ReadOnlySpan<int> below)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            Vector<int> two = new(2);
            for (; x + Vector<int>.Count <= row.Length; x += Vector<int>.Count)
            {
                Vector<int> sum = new Vector<int>(above[x..]) + new Vector<int>(below[x..]) + two;
                (new Vector<int>(row[x..]) - Vector.ShiftRightArithmetic(sum, 2)).CopyTo(row[x..]);
            }
        }

        for (; x < row.Length; x++)
        {
            row[x] -= (above[x] + below[x] + 2) >> 2;
        }
    }

    private static void High53(Span<int> row, ReadOnlySpan<int> above, ReadOnlySpan<int> below)
    {
        int x = 0;
        if (Vector.IsHardwareAccelerated)
        {
            for (; x + Vector<int>.Count <= row.Length; x += Vector<int>.Count)
            {
                Vector<int> sum = new Vector<int>(above[x..]) + new Vector<int>(below[x..]);
                (new Vector<int>(row[x..]) + Vector.ShiftRightArithmetic(sum, 1)).CopyTo(row[x..]);
            }
        }

        for (; x < row.Length; x++)
        {
            row[x] += (above[x] + below[x]) >> 1;
        }
    }

    /// <summary>The vertical 9/7 synthesis over a level's columns, whole rows at a time.</summary>
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

        Span<float> rows = temp.AsSpan(0, w * h);
        for (int k = 0, i = parity; i < h; k++, i += 2)
        {
            Scale(buffer.Slice(k * stride, w), rows.Slice(i * w, w), K);
        }

        for (int k = 0, i = 1 - parity; i < h; k++, i += 2)
        {
            Scale(buffer.Slice((lowCount + k) * stride, w), rows.Slice(i * w, w), InverseK);
        }

        RowStep(rows, w, h, parity, Delta);
        RowStep(rows, w, h, 1 - parity, Gamma);
        RowStep(rows, w, h, parity, Beta);
        RowStep(rows, w, h, 1 - parity, Alpha);
        for (int i = 0; i < h; i++)
        {
            rows.Slice(i * w, w).CopyTo(buffer.Slice(i * stride, w));
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

    private static void RowStep(Span<float> rows, int w, int h, int first, float c)
    {
        for (int i = first; i < h; i += 2)
        {
            Span<float> row = rows.Slice(i * w, w);
            ReadOnlySpan<float> above = rows.Slice((i > 0 ? i - 1 : 1) * w, w);
            ReadOnlySpan<float> below = rows.Slice((i + 1 < h ? i + 1 : i - 1) * w, w);
            int x = 0;
            if (Vector.IsHardwareAccelerated)
            {
                Vector<float> factor = new(c);
                for (; x + Vector<float>.Count <= w; x += Vector<float>.Count)
                {
                    Vector<float> sum = new Vector<float>(above[x..]) + new Vector<float>(below[x..]);
                    (new Vector<float>(row[x..]) - (factor * sum)).CopyTo(row[x..]);
                }
            }

            for (; x < w; x++)
            {
                row[x] -= c * (above[x] + below[x]);
            }
        }
    }
}
