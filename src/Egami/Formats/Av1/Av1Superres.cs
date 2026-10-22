namespace Egami.Formats.Av1;

/// <summary>
/// Superres upscaling, after libaom's av1_upscale_normative_rows (resize.c and convolve.c, BSD 2-clause, see
/// THIRD-PARTY-NOTICES): each tile column's rows resampled with the 8-tap normative filter, the frame's left and right edges
/// extended.
/// </summary>
internal static class Av1Superres
{
    private const int ScaleSubpelBits = 14;
    private const int ExtraBits = 8;

    /// <summary>Upscales a plane to the frame's upscaled width; returns the new plane and its stride.</summary>
    internal static (T[] Plane, int Stride) Upscale<T>(Av1Frame frame, T[] src, int srcStride, int plane)
        where T : unmanaged
    {
        Av1FrameHeader h = frame.Header;
        int ssx = plane > 0 ? frame.Sequence.SubsamplingX : 0, ssy = plane > 0 ? frame.Sequence.SubsamplingY : 0;
        int downscaledWidth = (h.FrameWidth + ssx) >> ssx, upscaledWidth = (h.UpscaledWidth + ssx) >> ssx;
        int rows = (h.FrameHeight + ssy) >> ssy;
        int stride = (upscaledWidth + 63) & ~63;
        int allocatedRows = src.Length / srcStride;
        T[] dst = new T[(stride * allocatedRows) + Av1Samples.Padding];
        int max = (1 << frame.Sequence.BitDepth) - 1;
        int step = ((downscaledWidth << ScaleSubpelBits) + (upscaledWidth / 2)) / upscaledWidth;
        int err = (upscaledWidth * step) - (downscaledWidth << ScaleSubpelBits);
        int x0 = (int)((uint)(((-((upscaledWidth - downscaledWidth) << (ScaleSubpelBits - 1))) + (upscaledWidth / 2)) / upscaledWidth
            + (1 << (ExtraBits - 1)) - (err / 2)) & ((1 << ScaleSubpelBits) - 1));
        ReadOnlySpan<short> filters = Av1Tables.ResizeFilterNormative;
        for (int col = 0; col < h.TileCols; col++)
        {
            int downX0 = h.MiColStarts[col] << (2 - ssx);
            int downX1 = Math.Min(h.MiColStarts[col + 1], frame.MiCols) << (2 - ssx);
            int srcWidth = downX1 - downX0;
            int upX0 = downX0 * h.SuperresDenom / 8;
            int upX1 = col == h.TileCols - 1 ? upscaledWidth : downX1 * h.SuperresDenom / 8;
            int dstWidth = upX1 - upX0;
            bool padLeft = col == 0, padRight = col == h.TileCols - 1;
            for (int y = 0; y < rows; y++)
            {
                int row = y * srcStride;
                int xq = x0;
                for (int x = 0; x < dstWidth; x++)
                {
                    // The taps start four samples left of the position (input - 1, then UPSCALE_NORMATIVE_TAPS / 2 - 1 more).
                    int position = (xq >> ScaleSubpelBits) - 4;
                    ReadOnlySpan<short> filter = filters.Slice(((xq & ((1 << ScaleSubpelBits) - 1)) >> ExtraBits) * 8, 8);
                    int sum = 0;
                    for (int k = 0; k < 8; k++)
                    {
                        int sx = position + k;
                        if (padLeft && sx < 0)
                        {
                            sx = 0;
                        }

                        if (padRight && sx > srcWidth - 1)
                        {
                            sx = srcWidth - 1;
                        }

                        sum += Av1Samples.Get(src[row + downX0 + sx]) * filter[k];
                    }

                    dst[(y * stride) + upX0 + x] = Av1Samples.From<T>(Math.Clamp((sum + 64) >> 7, 0, max));
                    xq += step;
                }
            }

            x0 += (dstWidth * step) - (srcWidth << ScaleSubpelBits);
        }

        return (dst, stride);
    }
}
