using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// VP9's subpixel prediction (vpx_dsp/vpx_convolve.c): eight-tap filters horizontally, vertically or both (horizontal first into
/// an intermediate block, rounded and clipped), at a step of 16 per pixel or a scaled step, and the rounded average with the
/// first prediction for a compound block's second.
/// </summary>
internal static class Vp9Convolve<T>
    where T : unmanaged, IBinaryInteger<T>
{
    private const int FilterBits = 7;

    /// <summary>
    /// Predicts a <paramref name="w"/> by <paramref name="h"/> block at <paramref name="dst"/> from <paramref name="src"/>, whose
    /// offset is the reference pixel for the block's top-left sample (before the subpixel offsets <paramref name="x0Q4"/> and
    /// <paramref name="y0Q4"/>, in 1/16 pixels). <paramref name="average"/> is the compound block's second reference.
    /// </summary>
    public static void Predict(ReadOnlySpan<T> src, int srcOffset, int srcStride, Span<T> dst, int dstOffset, int dstStride,
        int x0Q4, int xStepQ4, int y0Q4, int yStepQ4, int w, int h, int average, ReadOnlySpan<short> kernel, int bitDepth,
        int[] temp, T[] predictTemp)
    {
        if (typeof(T) == typeof(byte) && xStepQ4 == 16 && yStepQ4 == 16 && Vp9ConvolveSimd.IsSupported)
        {
            Vp9ConvolveSimd.Predict(MemoryMarshal.Cast<T, byte>(src), srcOffset, srcStride, MemoryMarshal.Cast<T, byte>(dst), dstOffset,
                dstStride, x0Q4, y0Q4, w, h, average != 0, kernel, MemoryMarshal.AsBytes(temp.AsSpan()),
                MemoryMarshal.Cast<T, byte>(predictTemp.AsSpan()));
            return;
        }

        if (typeof(T) == typeof(ushort) && xStepQ4 == 16 && yStepQ4 == 16 && Vp9ConvolveSimd16.IsSupported)
        {
            Vp9ConvolveSimd16.Predict(MemoryMarshal.Cast<T, ushort>(src), srcOffset, srcStride, MemoryMarshal.Cast<T, ushort>(dst), dstOffset,
                dstStride, x0Q4, y0Q4, w, h, average != 0, kernel, bitDepth, MemoryMarshal.Cast<int, ushort>(temp.AsSpan()),
                MemoryMarshal.Cast<T, ushort>(predictTemp.AsSpan()));
            return;
        }

        bool filterX = x0Q4 != 0 || xStepQ4 != 16;
        bool filterY = y0Q4 != 0 || yStepQ4 != 16;
        int max = (1 << bitDepth) - 1;
        bool avg = average != 0;

        // Predict into the destination, or into a scratch block to average with it.
        Span<T> outBuffer = avg ? predictTemp : dst;
        int outOffset = avg ? 0 : dstOffset;
        int outStride = avg ? 64 : dstStride;

        if (filterX && filterY)
        {
            int intermediateHeight = ((((h - 1) * yStepQ4) + y0Q4) >> 4) + 8;
            Span<int> mid = temp;
            Horizontal(src, srcOffset - (3 * srcStride), srcStride, mid, 64, kernel, x0Q4, xStepQ4, w, intermediateHeight, max);
            Vertical(mid, 3 * 64, 64, outBuffer, outOffset, outStride, kernel, y0Q4, yStepQ4, w, h, max);
        }
        else if (filterX)
        {
            Span<int> mid = temp;
            Horizontal(src, srcOffset, srcStride, mid, 64, kernel, x0Q4, xStepQ4, w, h, max);
            for (int y = 0; y < h; y++)
            {
                Span<T> row = outBuffer.Slice(outOffset + (y * outStride), w);
                for (int x = 0; x < w; x++)
                {
                    row[x] = T.CreateTruncating(mid[(y * 64) + x]);
                }
            }
        }
        else if (filterY)
        {
            Span<int> mid = temp;
            int rows = ((((h - 1) * yStepQ4) + y0Q4) >> 4) + 8;
            for (int y = 0; y < rows; y++)
            {
                ReadOnlySpan<T> row = src.Slice(srcOffset + ((y - 3) * srcStride), w);
                for (int x = 0; x < w; x++)
                {
                    mid[(y * 64) + x] = int.CreateTruncating(row[x]);
                }
            }

            Vertical(mid, 3 * 64, 64, outBuffer, outOffset, outStride, kernel, y0Q4, yStepQ4, w, h, max);
        }
        else
        {
            for (int y = 0; y < h; y++)
            {
                src.Slice(srcOffset + (y * srcStride), w).CopyTo(outBuffer.Slice(outOffset + (y * outStride), w));
            }
        }

        if (avg)
        {
            for (int y = 0; y < h; y++)
            {
                Span<T> d = dst.Slice(dstOffset + (y * dstStride), w);
                ReadOnlySpan<T> p = outBuffer.Slice(y * 64, w);
                for (int x = 0; x < w; x++)
                {
                    d[x] = T.CreateTruncating((int.CreateTruncating(d[x]) + int.CreateTruncating(p[x]) + 1) >> 1);
                }
            }
        }
    }

    /// <summary>The horizontal pass (convolve_horiz): clipped, rounded sums of eight taps around each stepped position.</summary>
    private static void Horizontal(ReadOnlySpan<T> src, int srcOffset, int srcStride, Span<int> dst, int dstStride, ReadOnlySpan<short> kernel,
        int x0Q4, int xStepQ4, int w, int h, int max)
    {
        for (int y = 0; y < h; y++)
        {
            int rowStart = srcOffset + (y * srcStride) - 3;
            int xQ4 = x0Q4;
            for (int x = 0; x < w; x++)
            {
                int s = rowStart + (xQ4 >> 4);
                ReadOnlySpan<short> f = kernel.Slice((xQ4 & 15) * 8, 8);
                int sum = 0;
                for (int k = 0; k < 8; k++)
                {
                    sum += int.CreateTruncating(src[s + k]) * f[k];
                }

                dst[(y * dstStride) + x] = Math.Clamp((sum + (1 << (FilterBits - 1))) >> FilterBits, 0, max);
                xQ4 += xStepQ4;
            }
        }
    }

    /// <summary>The vertical pass (convolve_vert) from an intermediate block whose row <paramref name="srcOffset"/> is the block's first.</summary>
    private static void Vertical(ReadOnlySpan<int> src, int srcOffset, int srcStride, Span<T> dst, int dstOffset, int dstStride,
        ReadOnlySpan<short> kernel, int y0Q4, int yStepQ4, int w, int h, int max)
    {
        int start = srcOffset - (3 * srcStride);
        for (int x = 0; x < w; x++)
        {
            int yQ4 = y0Q4;
            for (int y = 0; y < h; y++)
            {
                int s = start + ((yQ4 >> 4) * srcStride) + x;
                ReadOnlySpan<short> f = kernel.Slice((yQ4 & 15) * 8, 8);
                int sum = 0;
                for (int k = 0; k < 8; k++)
                {
                    sum += src[s + (k * srcStride)] * f[k];
                }

                dst[dstOffset + (y * dstStride) + x] = T.CreateTruncating(Math.Clamp((sum + (1 << (FilterBits - 1))) >> FilterBits, 0, max));
                yQ4 += yStepQ4;
            }
        }
    }
}
