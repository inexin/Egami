using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Egami.Formats.Av1;

/// <summary>
/// A reference frame's luma plane with its edges repeated into a wide border, so a motion search reads any place within the border
/// without clamping: repeating the edge is what the decoder's clamped reads give (the block inter prediction process). Its
/// <see cref="Half"/> is the plane at half the size each way, border and all, for the motion pre-pass (<see cref="Av1MotionPrepass"/>).
/// </summary>
internal sealed class Av1ReferencePlane
{
    /// <summary>Samples past the last row a vector load may read.</summary>
    private const int Padding = 32;

    private Av1ReferencePlane? _half;

    /// <summary>Builds the bordered plane of a frame's luma.</summary>
    internal Av1ReferencePlane(Av1Frame frame)
        : this(frame.Plane<ushort>(0), frame.Strides[0], frame.Header.UpscaledWidth, frame.Header.FrameHeight)
    {
    }

    /// <summary>Builds the bordered plane of a luma plane of <paramref name="width"/> by <paramref name="height"/> samples at a stride.</summary>
    internal Av1ReferencePlane(ushort[] plane, int frameStride, int width, int height)
    {
        Width = width;
        Height = height;
        BorderSize = 192;
        Stride = Width + (2 * BorderSize);
        Samples = new ushort[(Stride * (Height + (2 * BorderSize))) + Padding];
        for (int y = 0; y < Height + (2 * BorderSize); y++)
        {
            int from = Math.Clamp(y - BorderSize, 0, Height - 1) * frameStride;
            Span<ushort> row = Samples.AsSpan(y * Stride, Stride);
            plane.AsSpan(from, Width).CopyTo(row[BorderSize..]);
            row[..BorderSize].Fill(plane[from]);
            row[(BorderSize + Width)..].Fill(plane[from + Width - 1]);
        }
    }

    /// <summary>Builds a plane at half the size of <paramref name="full"/>, each sample the rounded mean of four.</summary>
    private Av1ReferencePlane(Av1ReferencePlane full)
    {
        Width = (full.Width + 1) >> 1;
        Height = (full.Height + 1) >> 1;
        BorderSize = full.BorderSize >> 1;
        Stride = full.Stride >> 1;
        int rows = ((full.Samples.Length - Padding) / full.Stride) >> 1;
        Samples = new ushort[(Stride * rows) + Padding];
        Av1MotionPrepass.Halve(full.Samples, full.Stride, Samples, Stride, Stride, rows);
    }

    /// <summary>Gets the frame's width.</summary>
    internal int Width { get; }

    /// <summary>Gets the frame's height.</summary>
    internal int Height { get; }

    /// <summary>Gets the border's width on each side, in samples.</summary>
    internal int BorderSize { get; }

    /// <summary>Gets the stride of <see cref="Samples"/>.</summary>
    internal int Stride { get; }

    /// <summary>Gets the samples, the frame's top left at (<see cref="BorderSize"/>, <see cref="BorderSize"/>).</summary>
    internal ushort[] Samples { get; }

    /// <summary>Gets the plane at half the size each way, made the first time it is asked for (not thread-safe).</summary>
    internal Av1ReferencePlane Half => _half ??= new Av1ReferencePlane(this);

    /// <summary>Gets the index of a place in the frame's coordinates.</summary>
    internal int At(int x, int y) => ((y + BorderSize) * Stride) + x + BorderSize;

    /// <summary>Gets whether a block at a place, with the filter's reach of three samples before and four after, lies inside the border.</summary>
    internal bool Holds(int x, int y, int w, int h) =>
        x - 3 >= -BorderSize && y - 3 >= -BorderSize && x + w + 4 <= Width + BorderSize && y + h + 4 <= Height + BorderSize;
}

/// <summary>
/// The sample measures of the AV1 motion search: sums of absolute differences between a source block and a whole-sample place in a
/// reference, and the luma prediction at an eighth-sample place with the regular eight-tap filter, rounded as the decoder rounds a
/// single prediction, by the decoder's own filter passes (the ideas of libaom's mcomp.c; BSD 2-clause, see THIRD-PARTY-NOTICES).
/// </summary>
internal static class Av1MotionSearch
{
    /// <summary>Gets the sum of absolute differences between a source block and a reference block.</summary>
    internal static int Sad(ushort[] source, int sourceAt, int sourceStride, ushort[] reference, int referenceAt, int referenceStride, int w, int h)
    {
        ref ushort s0 = ref MemoryMarshal.GetArrayDataReference(source);
        ref ushort r0 = ref MemoryMarshal.GetArrayDataReference(reference);
        if (Vector256.IsHardwareAccelerated && (w & 15) == 0)
        {
            // Sums of up to four 12-bit differences a lane fit 16 bits before they widen.
            Vector256<uint> total = Vector256<uint>.Zero;
            for (int r = 0; r < h; r++)
            {
                ref ushort a = ref Unsafe.Add(ref s0, sourceAt + (r * sourceStride));
                ref ushort b = ref Unsafe.Add(ref r0, referenceAt + (r * referenceStride));
                Vector256<ushort> acc = Vector256<ushort>.Zero;
                for (int c = 0; c < w; c += 16)
                {
                    Vector256<ushort> x = Vector256.LoadUnsafe(ref a, (nuint)c), y = Vector256.LoadUnsafe(ref b, (nuint)c);
                    acc += Vector256.Max(x, y) - Vector256.Min(x, y);
                    if ((c & 63) == 48)
                    {
                        (Vector256<uint> lo, Vector256<uint> hi) = Vector256.Widen(acc);
                        total += lo + hi;
                        acc = Vector256<ushort>.Zero;
                    }
                }

                (Vector256<uint> l, Vector256<uint> u) = Vector256.Widen(acc);
                total += l + u;
            }

            return (int)Vector256.Sum(total);
        }

        if (Vector128.IsHardwareAccelerated && (w & 7) == 0)
        {
            Vector128<uint> total = Vector128<uint>.Zero;
            for (int r = 0; r < h; r++)
            {
                ref ushort a = ref Unsafe.Add(ref s0, sourceAt + (r * sourceStride));
                ref ushort b = ref Unsafe.Add(ref r0, referenceAt + (r * referenceStride));
                Vector128<ushort> acc = Vector128<ushort>.Zero;
                for (int c = 0; c < w; c += 8)
                {
                    Vector128<ushort> x = Vector128.LoadUnsafe(ref a, (nuint)c), y = Vector128.LoadUnsafe(ref b, (nuint)c);
                    acc += Vector128.Max(x, y) - Vector128.Min(x, y);
                    if ((c & 31) == 24)
                    {
                        (Vector128<uint> lo, Vector128<uint> hi) = Vector128.Widen(acc);
                        total += lo + hi;
                        acc = Vector128<ushort>.Zero;
                    }
                }

                (Vector128<uint> l, Vector128<uint> u) = Vector128.Widen(acc);
                total += l + u;
            }

            return (int)Vector128.Sum(total);
        }

        int sum = 0;
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
    /// for sides of 4), into <paramref name="prediction"/> row by row, unclamped; the place must lie inside the plane's border.
    /// </summary>
    internal static void Predict(Av1ReferencePlane plane, int x8, int y8, int w, int h, int bitDepth, short[] intermediate, int[] prediction)
    {
        int round0 = bitDepth == 12 ? 5 : 3, round1 = bitDepth == 12 ? 9 : 11;
        int fx = (x8 & 7) * 2, fy = (y8 & 7) * 2;
        short[] filterX = w <= 4 ? Av1Tables.SubpelFilters4 : Av1Tables.SubpelFilters8;
        short[] filterY = h <= 4 ? Av1Tables.SubpelFilters4 : Av1Tables.SubpelFilters8;
        ushort[] src = plane.Samples;
        int stride = plane.Stride;
        int at = plane.At((x8 >> 3) - 3, (y8 >> 3) - 3);
        if (fy == 0)
        {
            // The vertical filter's only tap is the middle one, 128.
            Av1Reconstructor<ushort>.Horizontal(src, at + (3 * stride), stride, fx, filterX, w, h, round0, intermediate);
            int half = 1 << (round1 - 1);
            for (int i = 0; i < w * h; i++)
            {
                prediction[i] = ((intermediate[i] * 128) + half) >> round1;
            }

            return;
        }

        Av1Reconstructor<ushort>.Horizontal(src, at, stride, fx, filterX, w, h + 7, round0, intermediate);
        Av1Reconstructor<ushort>.Vertical(intermediate, fy, filterY, w, h, round1, prediction);
    }

    /// <summary>Gets the sum of squared differences between two planes of the same stride over a w × h block.</summary>
    internal static long SquaredError(ushort[] a, ushort[] b, int at, int stride, int w, int h)
    {
        long sum = 0;
        ref ushort a0 = ref MemoryMarshal.GetArrayDataReference(a);
        ref ushort b0 = ref MemoryMarshal.GetArrayDataReference(b);
        int vectorW = Vector256.IsHardwareAccelerated ? w & ~7 : 0;
        for (int r = 0; r < h; r++)
        {
            int row = at + (r * stride);
            int c = 0;
            if (vectorW > 0)
            {
                // Squares of 12-bit differences fit 25 bits; eight of them a lane sum to under 2^28.
                Vector256<int> acc = Vector256<int>.Zero;
                for (; c < vectorW; c += 8)
                {
                    Vector256<int> x = Vector256.WidenLower(Vector128.LoadUnsafe(ref a0, (nuint)(row + c)).ToVector256Unsafe()).AsInt32();
                    Vector256<int> y = Vector256.WidenLower(Vector128.LoadUnsafe(ref b0, (nuint)(row + c)).ToVector256Unsafe()).AsInt32();
                    Vector256<int> d = x - y;
                    acc += d * d;
                    if ((c & 63) == 56)
                    {
                        (Vector256<long> lo, Vector256<long> hi) = Vector256.Widen(acc);
                        sum += Vector256.Sum(lo + hi);
                        acc = Vector256<int>.Zero;
                    }
                }

                (Vector256<long> l, Vector256<long> u) = Vector256.Widen(acc);
                sum += Vector256.Sum(l + u);
            }

            for (; c < w; c++)
            {
                int d = a[row + c] - b[row + c];
                sum += d * d;
            }
        }

        return sum;
    }

    /// <summary>
    /// Gets whether eight transformed coefficients from <paramref name="pos"/> all quantize to 0: each magnitude, scaled up by
    /// <paramref name="shift"/>, plus its rounding stays under its step.
    /// </summary>
    internal static bool AllBelow(int[] transformed, int[] rounding, int[] steps, int pos, int shift)
    {
        if (!Vector256.IsHardwareAccelerated)
        {
            for (int i = pos; i < pos + 8; i++)
            {
                if (((long)Math.Abs(transformed[i]) << shift) + rounding[i] >= steps[i])
                {
                    return false;
                }
            }

            return true;
        }

        // Transformed magnitudes stay under 2^24 at every bit depth, so the shifted sum fits 32 bits.
        Vector256<int> v = Vector256.Abs(Vector256.LoadUnsafe(ref transformed[0], (nuint)pos));
        Vector256<int> sum = Vector256.ShiftLeft(v, shift) + Vector256.LoadUnsafe(ref rounding[0], (nuint)pos);
        return Vector256.LessThanAll(sum, Vector256.LoadUnsafe(ref steps[0], (nuint)pos));
    }

    /// <summary>
    /// Gets the sum of squared differences between a source block and an unclamped prediction held row by row
    /// (<see cref="Predict"/>), each predicted sample clamped to the bit depth's range first.
    /// </summary>
    internal static long Sse(ushort[] source, int sourceAt, int sourceStride, int[] prediction, int w, int h, int bitDepth)
    {
        int max = (1 << bitDepth) - 1;
        long sum = 0;
        ref ushort s0 = ref MemoryMarshal.GetArrayDataReference(source);
        ref int p0 = ref MemoryMarshal.GetArrayDataReference(prediction);
        if (Vector256.IsHardwareAccelerated && (w & 7) == 0)
        {
            Vector256<int> maxV = Vector256.Create(max);
            for (int r = 0; r < h; r++)
            {
                ref ushort a = ref Unsafe.Add(ref s0, sourceAt + (r * sourceStride));
                ref int p = ref Unsafe.Add(ref p0, r * w);
                Vector256<long> row = Vector256<long>.Zero;
                for (int c = 0; c < w; c += 8)
                {
                    Vector256<int> s = Vector256.WidenLower(Vector128.LoadUnsafe(ref a, (nuint)c).ToVector256Unsafe()).AsInt32();
                    Vector256<int> q = Vector256.Min(Vector256.Max(Vector256.LoadUnsafe(ref p, (nuint)c), Vector256<int>.Zero), maxV);
                    Vector256<int> d = s - q;
                    (Vector256<long> lo, Vector256<long> hi) = Vector256.Widen(d * d);
                    row += lo + hi;
                }

                sum += Vector256.Sum(row);
            }

            return sum;
        }

        for (int r = 0; r < h; r++)
        {
            int a = sourceAt + (r * sourceStride);
            long row = 0;
            for (int c = 0; c < w; c++)
            {
                int d = source[a + c] - Math.Clamp(prediction[(r * w) + c], 0, max);
                row += d * d;
            }

            sum += row;
        }

        return sum;
    }
}
