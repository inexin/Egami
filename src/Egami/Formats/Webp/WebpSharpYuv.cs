namespace Egami.Formats.Webp;

/// <summary>
/// Sharp RGB to YUV (WebpWriterOptions.SharpYuv), after libwebp's sharpyuv (BSD; see THIRD-PARTY-NOTICES): instead of averaging
/// each 2 × 2 block's chroma, luma and chroma are refined together a few times so that the image a decoder rebuilds (chroma
/// upsampled bilinearly, added to luma) comes back closest to the source in linear light. Sharp colored edges then keep their
/// color instead of bleeding into the luma. Samples are held at 10 bits (8-bit values times 4) throughout.
/// </summary>
internal static class WebpSharpYuv
{
    private const int Max = 1023;
    private const int Iterations = 4;
    private const int LinearBits = 16;

    private static readonly int[] GammaToLinearTable = BuildGammaToLinear();
    private static readonly ushort[] LinearToGammaTable = BuildLinearToGamma();

    /// <summary>Converts ARGB into <paramref name="planes"/> (already allocated for the size), luma and chroma for every macroblock.</summary>
    internal static void Convert(ReadOnlySpan<uint> argb, int width, int height, Vp8Planes planes, in YuvKernels8.FromRgbCoefficients k)
    {
        int w = (width + 1) & ~1, h = (height + 1) & ~1, uvw = w >> 1, uvh = h >> 1;
        ushort[] bestY = new ushort[w * h], targetY = new ushort[w * h];
        short[] bestUv = new short[uvw * 3 * uvh], targetUv = new short[uvw * 3 * uvh];
        ushort[] rows = new ushort[w * 3 * 2];
        ushort[] rgbY = new ushort[w * 2];
        short[] rgbUv = new short[uvw * 3];

        // The source in W/RGB form: gray (W) and each block's chroma as colors minus W.
        for (int j = 0; j < h; j += 2)
        {
            Span<ushort> src1 = rows.AsSpan(0, 3 * w), src2 = rows.AsSpan(3 * w, 3 * w);
            ImportRow(argb, width, Math.Min(j, height - 1), w, src1);
            ImportRow(argb, width, Math.Min(j + 1, height - 1), w, src2);
            StoreGray(src1, bestY.AsSpan(j * w, w), w);
            StoreGray(src2, bestY.AsSpan((j + 1) * w, w), w);
            UpdateW(src1, targetY.AsSpan(j * w, w), w);
            UpdateW(src2, targetY.AsSpan((j + 1) * w, w), w);
            Span<short> uv = targetUv.AsSpan((j >> 1) * 3 * uvw, 3 * uvw);
            UpdateChroma(src1, src2, uv, uvw);
            uv.CopyTo(bestUv.AsSpan((j >> 1) * 3 * uvw, 3 * uvw));
        }

        // Rebuild the image as a decoder would, compare with the target, and move luma and chroma by the difference.
        long previous = long.MaxValue;
        for (int iteration = 0; iteration < Iterations; iteration++)
        {
            long diff = 0;
            for (int j = 0; j < h; j += 2)
            {
                int row = j >> 1;
                ReadOnlySpan<short> cur = bestUv.AsSpan(row * 3 * uvw, 3 * uvw);
                ReadOnlySpan<short> prev = bestUv.AsSpan(Math.Max(row - 1, 0) * 3 * uvw, 3 * uvw);
                ReadOnlySpan<short> next = bestUv.AsSpan(Math.Min(row + 1, uvh - 1) * 3 * uvw, 3 * uvw);
                Span<ushort> src1 = rows.AsSpan(0, 3 * w), src2 = rows.AsSpan(3 * w, 3 * w);
                Interpolate(bestY.AsSpan(j * w, w), prev, cur, uvw, w, src1);
                Interpolate(bestY.AsSpan((j + 1) * w, w), next, cur, uvw, w, src2);
                UpdateW(src1, rgbY.AsSpan(0, w), w);
                UpdateW(src2, rgbY.AsSpan(w, w), w);
                UpdateChroma(src1, src2, rgbUv, uvw);
                Span<ushort> best = bestY.AsSpan(j * w, 2 * w);
                ReadOnlySpan<ushort> target = targetY.AsSpan(j * w, 2 * w);
                for (int i = 0; i < best.Length; i++)
                {
                    int d = target[i] - rgbY[i];
                    best[i] = (ushort)Math.Clamp(best[i] + d, 0, Max);
                    diff += Math.Abs(d);
                }

                Span<short> uv = bestUv.AsSpan(row * 3 * uvw, 3 * uvw);
                ReadOnlySpan<short> targetRow = targetUv.AsSpan(row * 3 * uvw, 3 * uvw);
                for (int i = 0; i < uv.Length; i++)
                {
                    uv[i] = (short)(uv[i] + (targetRow[i] - rgbUv[i]));
                }
            }

            if (iteration > 0 && (diff < 3L * w * h || diff > previous))
            {
                break;
            }

            previous = diff;
        }

        // The final planes through the matrix: luma from W plus the block's chroma, chroma from the colors minus W alone.
        for (int j = 0; j < height; j++)
        {
            ReadOnlySpan<short> uv = bestUv.AsSpan((j >> 1) * 3 * uvw, 3 * uvw);
            Span<byte> y = planes.Y.AsSpan(j * planes.Stride, width);
            for (int i = 0; i < width; i++)
            {
                int wv = bestY[(j * w) + i];
                int r = uv[i >> 1] + wv, g = uv[uvw + (i >> 1)] + wv, b = uv[(2 * uvw) + (i >> 1)] + wv;
                y[i] = (byte)Math.Clamp(((k.YR * r) + (k.YG * g) + (k.YB * b) + (k.YBias << 2)) >> 17, 0, 255);
            }
        }

        int chromaWidth = (width + 1) >> 1, chromaHeight = (height + 1) >> 1;
        for (int j = 0; j < chromaHeight; j++)
        {
            ReadOnlySpan<short> uv = bestUv.AsSpan(j * 3 * uvw, 3 * uvw);
            Span<byte> u = planes.U.AsSpan(j * planes.ChromaStride, chromaWidth), v = planes.V.AsSpan(j * planes.ChromaStride, chromaWidth);
            for (int i = 0; i < chromaWidth; i++)
            {
                int r = uv[i], g = uv[uvw + i], b = uv[(2 * uvw) + i];
                u[i] = (byte)Math.Clamp((((k.BR * r) + (k.BG * g) + (k.BB * b) + (1 << 16)) >> 17) + k.COff, 0, 255);
                v[i] = (byte)Math.Clamp((((k.RR * r) + (k.RG * g) + (k.RB * b) + (1 << 16)) >> 17) + k.COff, 0, 255);
            }
        }
    }

    /// <summary>Reads one row as three 10-bit planes of <paramref name="w"/> samples, the last column repeated when the width is odd.</summary>
    private static void ImportRow(ReadOnlySpan<uint> argb, int width, int y, int w, Span<ushort> row)
    {
        ReadOnlySpan<uint> source = argb.Slice(y * width, width);
        for (int i = 0; i < w; i++)
        {
            uint p = source[Math.Min(i, width - 1)];
            row[i] = (ushort)(((p >> 16) & 0xFF) << 2);
            row[w + i] = (ushort)(((p >> 8) & 0xFF) << 2);
            row[(2 * w) + i] = (ushort)((p & 0xFF) << 2);
        }
    }

    private static int Gray(long r, long g, long b) => (int)(((13933 * r) + (46871 * g) + (4732 * b) + (1 << 15)) >> 16);

    private static void StoreGray(ReadOnlySpan<ushort> rgb, Span<ushort> y, int w)
    {
        for (int i = 0; i < w; i++)
        {
            y[i] = (ushort)Gray(rgb[i], rgb[w + i], rgb[(2 * w) + i]);
        }
    }

    /// <summary>The gray of each pixel computed in linear light, back in gamma: what luma should come out as.</summary>
    private static void UpdateW(ReadOnlySpan<ushort> rgb, Span<ushort> y, int w)
    {
        for (int i = 0; i < w; i++)
        {
            y[i] = ToGamma(Gray(ToLinear(rgb[i]), ToLinear(rgb[w + i]), ToLinear(rgb[(2 * w) + i])));
        }
    }

    /// <summary>Each 2 × 2 block's colors averaged in linear light, minus their gray.</summary>
    private static void UpdateChroma(ReadOnlySpan<ushort> src1, ReadOnlySpan<ushort> src2, Span<short> uv, int uvw)
    {
        int w = 2 * uvw;
        for (int i = 0; i < uvw; i++)
        {
            int r = ScaleDown(src1[2 * i], src1[(2 * i) + 1], src2[2 * i], src2[(2 * i) + 1]);
            int g = ScaleDown(src1[w + (2 * i)], src1[w + (2 * i) + 1], src2[w + (2 * i)], src2[w + (2 * i) + 1]);
            int b = ScaleDown(src1[(2 * w) + (2 * i)], src1[(2 * w) + (2 * i) + 1], src2[(2 * w) + (2 * i)], src2[(2 * w) + (2 * i) + 1]);
            int gray = Gray(r, g, b);
            uv[i] = (short)(r - gray);
            uv[uvw + i] = (short)(g - gray);
            uv[(2 * uvw) + i] = (short)(b - gray);
        }
    }

    private static int ScaleDown(int a, int b, int c, int d) => ToGamma((ToLinear(a) + ToLinear(b) + ToLinear(c) + ToLinear(d) + 2) >> 2);

    /// <summary>One row rebuilt from its luma and the chroma rows (bilinear, 9:3:3:1 between samples, 3:1 at the edges).</summary>
    private static void Interpolate(ReadOnlySpan<ushort> y, ReadOnlySpan<short> near, ReadOnlySpan<short> cur, int uvw, int w, Span<ushort> rgb)
    {
        for (int c = 0; c < 3; c++)
        {
            ReadOnlySpan<short> a = cur.Slice(c * uvw, uvw), b = near.Slice(c * uvw, uvw);
            Span<ushort> output = rgb.Slice(c * w, w);
            output[0] = Clip((((a[0] * 3) + b[0] + 2) >> 2) + y[0]);
            for (int i = 0; i < uvw - 1; i++)
            {
                int v0 = ((a[i] * 9) + (a[i + 1] * 3) + (b[i] * 3) + b[i + 1] + 8) >> 4;
                int v1 = ((a[i + 1] * 9) + (a[i] * 3) + (b[i + 1] * 3) + b[i] + 8) >> 4;
                output[(2 * i) + 1] = Clip(y[(2 * i) + 1] + v0);
                output[(2 * i) + 2] = Clip(y[(2 * i) + 2] + v1);
            }

            output[w - 1] = Clip((((a[uvw - 1] * 3) + b[uvw - 1] + 2) >> 2) + y[w - 1]);
        }
    }

    private static ushort Clip(int v) => (ushort)Math.Clamp(v, 0, Max);

    private static int ToLinear(int v) => GammaToLinearTable[Math.Clamp(v, 0, Max)];

    private static ushort ToGamma(int linear) => LinearToGammaTable[Math.Clamp(linear, 0, 1 << LinearBits)];

    /// <summary>The BT.709 transfer function, inverted: 10-bit gamma samples to linear light in 16 bits.</summary>
    private static int[] BuildGammaToLinear()
    {
        const double A = 0.09929682680944, Threshold = 0.018053968510807;
        int[] table = new int[Max + 1];
        for (int v = 0; v <= Max; v++)
        {
            double g = v / (double)Max;
            double linear = g <= Threshold * 4.5 ? g / 4.5 : Math.Pow((g + A) / (1 + A), 1 / 0.45);
            table[v] = (int)Math.Round(linear * (1 << LinearBits));
        }

        return table;
    }

    /// <summary>The BT.709 transfer function: linear light in 16 bits to 10-bit gamma samples.</summary>
    private static ushort[] BuildLinearToGamma()
    {
        const double A = 0.09929682680944, Threshold = 0.018053968510807;
        ushort[] table = new ushort[(1 << LinearBits) + 1];
        for (int v = 0; v < table.Length; v++)
        {
            double l = v / (double)(1 << LinearBits);
            double g = l <= Threshold ? 4.5 * l : ((1 + A) * Math.Pow(l, 0.45)) - A;
            table[v] = (ushort)Math.Clamp((int)Math.Round(g * Max), 0, Max);
        }

        return table;
    }
}
