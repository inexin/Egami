using System.Runtime.InteropServices;

using Egami.Pixels;

namespace Egami.Formats.Jpeg;

/// <summary>
/// Upsampling and color conversion for samples deeper than 8 bits (12-bit DCT and lossless files), held as 16-bit values at their
/// own precision. The arithmetic is libjpeg's at every precision: the same triangle filters as <see cref="JpegUpsample"/>, and
/// YCbCr to RGB with 16 fraction bits around the center 2^(P − 1). Output goes through Gray16, Rgb16 or Cmyk16, each sample
/// scaled from 0..2^P − 1 to the full 16-bit range with rounding, so it converts back exactly.
/// </summary>
internal static class JpegWide
{
    private const int ScaleBits = 16;
    private const long Half = 1L << (ScaleBits - 1);
    private const long CrToR = 91881;
    private const long CbToB = 116130;
    private const long CrToG = -46802;
    private const long CbToG = -22554;

    /// <summary>Scales a sample of <paramref name="precision"/> bits to 16 bits: 0 stays 0 and 2^P − 1 becomes 65535.</summary>
    internal static ushort Widen(int value, int precision)
    {
        int max = (1 << precision) - 1;
        return (ushort)((((long)value * 65535) + (max >> 1)) / max);
    }

    /// <summary>Replicates each sample <paramref name="factor"/> times across, filling <paramref name="output"/>.</summary>
    internal static void Replicate(ReadOnlySpan<ushort> row, int factor, Span<ushort> output)
    {
        for (int x = 0; x < output.Length; x++)
        {
            output[x] = row[x / factor];
        }
    }

    /// <summary>The vertical triangle filter, (3 near + far + <paramref name="bias"/>) &gt;&gt; 2, for 4:4:0 sampling.</summary>
    internal static void Vertical(ReadOnlySpan<ushort> near, ReadOnlySpan<ushort> far, Span<ushort> output, int bias)
    {
        for (int x = 0; x < output.Length; x++)
        {
            output[x] = (ushort)(((3 * near[x]) + far[x] + bias) >> 2);
        }
    }

    /// <summary>Fills <paramref name="sums"/> with the samples of a row at 1..n, padding both ends, for <see cref="Double"/>.</summary>
    internal static void Widen(ReadOnlySpan<ushort> row, int n, Span<int> sums)
    {
        for (int i = 0; i < n; i++)
        {
            sums[i + 1] = row[i];
        }

        Pad(sums, n);
    }

    /// <summary>Fills <paramref name="sums"/> with the column sums 3 × near + far of two rows, padding both ends.</summary>
    internal static void ColumnSums(ReadOnlySpan<ushort> near, ReadOnlySpan<ushort> far, int n, Span<int> sums)
    {
        for (int i = 0; i < n; i++)
        {
            sums[i + 1] = (3 * near[i]) + far[i];
        }

        Pad(sums, n);
    }

    /// <summary>Doubles a row with the triangle filter, as <see cref="JpegUpsample.Double"/> does for 8-bit samples.</summary>
    internal static void Double(ReadOnlySpan<int> sums, int n, Span<ushort> output, int evenBias, int oddBias, int shift)
    {
        for (int i = 0; i < n; i++)
        {
            int m = 3 * sums[i + 1];
            output[2 * i] = (ushort)((m + sums[i] + evenBias) >> shift);
            if ((2 * i) + 1 < output.Length)
            {
                output[(2 * i) + 1] = (ushort)((m + sums[i + 2] + oddBias) >> shift);
            }
        }
    }

    /// <summary>
    /// Converts a row of component samples at <paramref name="precision"/> bits into <paramref name="target"/>, through Gray16 for
    /// gray, Cmyk16 inks for CMYK and YCCK (stored inverted, as Adobe writes them), and Rgb16 otherwise. <paramref name="scratch"/>
    /// holds four samples per pixel when the target is another type.
    /// </summary>
    internal static void ConvertRow<TOut>(
        JpegColorSpace space, int precision, ReadOnlySpan<ushort> a, ReadOnlySpan<ushort> b, ReadOnlySpan<ushort> c, ReadOnlySpan<ushort> k, Span<TOut> target, Span<ushort> scratch)
        where TOut : unmanaged, IPixelType<TOut>
    {
        int width = target.Length;
        int max = (1 << precision) - 1;
        if (space == JpegColorSpace.Gray)
        {
            Span<ushort> gray = typeof(TOut) == typeof(Gray16) ? MemoryMarshal.Cast<TOut, ushort>(target) : scratch[..width];
            for (int x = 0; x < width; x++)
            {
                gray[x] = Widen(a[x], precision);
            }

            Finish(MemoryMarshal.Cast<ushort, Gray16>(gray), target);
            return;
        }

        if (space is JpegColorSpace.Cmyk or JpegColorSpace.Ycck)
        {
            Span<ushort> inks = typeof(TOut) == typeof(Cmyk16) ? MemoryMarshal.Cast<TOut, ushort>(target) : scratch[..(width * 4)];
            for (int x = 0; x < width; x++)
            {
                int c0 = a[x], m0 = b[x], y0 = c[x];
                if (space == JpegColorSpace.Ycck)
                {
                    // YCCK holds the inverted CMY as YCbCr: convert, then invert back.
                    YccToRgb(a[x], b[x], c[x], precision, out int r, out int g, out int bl);
                    (c0, m0, y0) = (max - r, max - g, max - bl);
                }

                inks[4 * x] = Widen(max - c0, precision);
                inks[(4 * x) + 1] = Widen(max - m0, precision);
                inks[(4 * x) + 2] = Widen(max - y0, precision);
                inks[(4 * x) + 3] = Widen(max - k[x], precision);
            }

            Finish(MemoryMarshal.Cast<ushort, Cmyk16>(inks), target);
            return;
        }

        Span<ushort> rgb = typeof(TOut) == typeof(Rgb16) ? MemoryMarshal.Cast<TOut, ushort>(target) : scratch[..(width * 3)];
        for (int x = 0; x < width; x++)
        {
            int r = a[x], g = b[x], bl = c[x];
            if (space == JpegColorSpace.YCbCr)
            {
                YccToRgb(a[x], b[x], c[x], precision, out r, out g, out bl);
            }

            rgb[3 * x] = Widen(r, precision);
            rgb[(3 * x) + 1] = Widen(g, precision);
            rgb[(3 * x) + 2] = Widen(bl, precision);
        }

        Finish(MemoryMarshal.Cast<ushort, Rgb16>(rgb), target);
    }

    /// <summary>Full-range BT.601 (JFIF) YCbCr to RGB at <paramref name="precision"/> bits, clamped, with libjpeg's rounding.</summary>
    private static void YccToRgb(int y, int cb, int cr, int precision, out int r, out int g, out int b)
    {
        int max = (1 << precision) - 1;
        long center = 1L << (precision - 1);
        long blue = cb - center;
        long red = cr - center;
        r = Math.Clamp(y + (int)(((CrToR * red) + Half) >> ScaleBits), 0, max);
        g = Math.Clamp(y + (int)(((CbToG * blue) + Half + (CrToG * red)) >> ScaleBits), 0, max);
        b = Math.Clamp(y + (int)(((CbToB * blue) + Half) >> ScaleBits), 0, max);
    }

    /// <summary>Converts a finished row into the target, unless the row was written there already.</summary>
    private static void Finish<TFrom, TOut>(Span<TFrom> row, Span<TOut> target)
        where TFrom : unmanaged, IPixelType<TFrom>
        where TOut : unmanaged, IPixelType<TOut>
    {
        if (typeof(TFrom) != typeof(TOut))
        {
            PixelConvert.Convert<TFrom, TOut>(row, target);
        }
    }

    private static void Pad(Span<int> sums, int n)
    {
        sums[0] = sums[1];
        sums[n + 1] = sums[n];
    }
}
