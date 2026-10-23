using System.Runtime.Intrinsics;

namespace Egami.Formats.Av1;

/// <summary>
/// A reference frame's luma plane with its edges repeated into a wide border, so a motion search reads any place within the border
/// without clamping: repeating the edge is what the decoder's clamped reads give (the block inter prediction process).
/// </summary>
internal sealed class Av1ReferencePlane
{
    /// <summary>Gets the border's width on each side, in samples.</summary>
    internal const int Border = 192;

    /// <summary>Builds the bordered plane of a frame's luma.</summary>
    internal Av1ReferencePlane(Av1Frame frame)
    {
        Width = frame.Header.UpscaledWidth;
        Height = frame.Header.FrameHeight;
        Stride = Width + (2 * Border);
        Samples = new ushort[Stride * (Height + (2 * Border))];
        ushort[] plane = frame.Plane<ushort>(0);
        int frameStride = frame.Strides[0];
        for (int y = 0; y < Height + (2 * Border); y++)
        {
            int from = Math.Clamp(y - Border, 0, Height - 1) * frameStride;
            Span<ushort> row = Samples.AsSpan(y * Stride, Stride);
            plane.AsSpan(from, Width).CopyTo(row[Border..]);
            row[..Border].Fill(plane[from]);
            row[(Border + Width)..].Fill(plane[from + Width - 1]);
        }
    }

    /// <summary>Gets the frame's width.</summary>
    internal int Width { get; }

    /// <summary>Gets the frame's height.</summary>
    internal int Height { get; }

    /// <summary>Gets the stride of <see cref="Samples"/>.</summary>
    internal int Stride { get; }

    /// <summary>Gets the samples, the frame's top left at (<see cref="Border"/>, <see cref="Border"/>).</summary>
    internal ushort[] Samples { get; }

    /// <summary>Gets the index of a place in the frame's coordinates.</summary>
    internal int At(int x, int y) => ((y + Border) * Stride) + x + Border;

    /// <summary>Gets whether a block at a place, with the filter's reach of three samples before and four after, lies inside the border.</summary>
    internal bool Holds(int x, int y, int w, int h) =>
        x - 3 >= -Border && y - 3 >= -Border && x + w + 4 <= Width + Border && y + h + 4 <= Height + Border;
}

/// <summary>
/// The sample measures of the AV1 motion search: sums of absolute differences between a source block and a whole-sample place in a
/// reference, and the luma prediction at an eighth-sample place with the regular eight-tap filter, rounded as the decoder rounds a
/// single prediction (the ideas of libaom's mcomp.c; BSD 2-clause, see THIRD-PARTY-NOTICES).
/// </summary>
internal static class Av1MotionSearch
{
    /// <summary>Gets the sum of absolute differences between a source block and a reference block.</summary>
    internal static int Sad(ushort[] source, int sourceAt, int sourceStride, ushort[] reference, int referenceAt, int referenceStride, int w, int h)
    {
        int sum = 0;
        if (Vector128.IsHardwareAccelerated && w >= 8)
        {
            for (int r = 0; r < h; r++)
            {
                int a = sourceAt + (r * sourceStride), b = referenceAt + (r * referenceStride);
                Vector128<ushort> acc = Vector128<ushort>.Zero;
                int c = 0;
                for (; c + 8 <= w; c += 8)
                {
                    Vector128<ushort> x = Vector128.LoadUnsafe(ref source[a + c]), y = Vector128.LoadUnsafe(ref reference[b + c]);
                    acc += Vector128.Max(x, y) - Vector128.Min(x, y);
                }

                (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(acc);
                sum += (int)Vector128.Sum(lo + hi);
                for (; c < w; c++)
                {
                    sum += Math.Abs(source[a + c] - reference[b + c]);
                }
            }

            return sum;
        }

        for (int r = 0; r < h; r++)
        {
            int a = sourceAt + (r * sourceStride), b = referenceAt + (r * referenceStride);
            for (int c = 0; c < w; c++)
            {
                sum += Math.Abs(source[a + c] - reference[b + c]);
            }
        }

        return sum;
    }

    /// <summary>
    /// Predicts a w × h luma block at an eighth-sample place, given in eighth samples, with the regular filter (its four-tap form
    /// for sides of 4), into <paramref name="prediction"/> row by row; the place must lie inside the plane's border.
    /// </summary>
    internal static void Predict(Av1ReferencePlane plane, int x8, int y8, int w, int h, int bitDepth, int[] intermediate, ushort[] prediction)
    {
        int round0 = bitDepth == 12 ? 5 : 3, round1 = bitDepth == 12 ? 9 : 11;
        int max = (1 << bitDepth) - 1;
        int fx = (x8 & 7) * 2 * 8, fy = (y8 & 7) * 2 * 8;
        short[] filterX = w <= 4 ? Av1Tables.SubpelFilters4 : Av1Tables.SubpelFilters8;
        short[] filterY = h <= 4 ? Av1Tables.SubpelFilters4 : Av1Tables.SubpelFilters8;
        ushort[] src = plane.Samples;
        int stride = plane.Stride;
        int x0 = x8 >> 3, y0 = y8 >> 3;
        int half0 = 1 << (round0 - 1), half1 = 1 << (round1 - 1);
        for (int r = 0; r < h + 7; r++)
        {
            int at = plane.At(x0 - 3, y0 + r - 3);
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 8; t++)
                {
                    s += filterX[fx + t] * src[at + c + t];
                }

                intermediate[(r * w) + c] = (s + half0) >> round0;
            }
        }

        for (int r = 0; r < h; r++)
        {
            for (int c = 0; c < w; c++)
            {
                int s = 0;
                for (int t = 0; t < 8; t++)
                {
                    s += filterY[fy + t] * intermediate[((r + t) * w) + c];
                }

                prediction[(r * w) + c] = (ushort)Math.Clamp((s + half1) >> round1, 0, max);
            }
        }
    }

    /// <summary>Gets the sum of squared differences between a source block and a prediction held row by row.</summary>
    internal static long Sse(ushort[] source, int sourceAt, int sourceStride, ushort[] prediction, int w, int h)
    {
        long sum = 0;
        for (int r = 0; r < h; r++)
        {
            int a = sourceAt + (r * sourceStride);
            int row = 0;
            for (int c = 0; c < w; c++)
            {
                int d = source[a + c] - prediction[(r * w) + c];
                row += d * d;
            }

            sum += row;
        }

        return sum;
    }
}
