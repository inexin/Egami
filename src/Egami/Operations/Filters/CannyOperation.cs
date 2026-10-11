using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// <see cref="RasterOps.CannyEdges(float, float, float)"/>: Canny's (1986) detector on the Rec. 709 luma times alpha (transparent
/// is black). A Gaussian smooths the gray plane; Canny's 2 × 2 first differences give the gradient at each pixel's lower-right
/// corner; non-maximum suppression keeps the pixels whose magnitude no neighbour across the edge exceeds, in four directions; and
/// hysteresis keeps the pixels at least the high threshold and everything 8-connected to them at least the low one. Thresholds
/// are fractions of the largest magnitude left after suppression, as ImageMagick's -canny percentages are. The Gaussian is
/// ImageMagick's blur kernel (each weight the mean of three samples a third of a pixel apart, radius 4σ), so edge maps line up
/// pixel for pixel. Writes white edges on black and keeps alpha.
/// </summary>
internal sealed class CannyOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    // tan 67.5° and tan 22.5°: the slopes between the four directions.
    private const float Steep = 2.41421356237f;
    private const float Shallow = 0.414213562373f;

    private readonly float[] _weights;
    private readonly float _low;
    private readonly float _high;

    internal CannyOperation(float sigma, float low, float high)
    {
        _weights = sigma == 0 ? [1] : Weights(sigma);
        _low = low;
        _high = high;
    }

    /// <summary>Gets <see langword="null"/>: the thresholds depend on the whole image and hysteresis follows edges anywhere.</summary>
    public (int X, int Y)? Reach => null;

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    /// <summary>The smoothing weights: the Gaussian's mean over three points a third of a pixel apart, radius round(4σ), normalized.</summary>
    internal static float[] Weights(float sigma)
    {
        int r = Math.Max(1, (int)Math.Round(4.0 * sigma, MidpointRounding.AwayFromZero));
        double[] w = new double[(2 * r) + 1];
        double sum = 0, s2 = 2.0 * sigma * sigma;
        for (int i = -r; i <= r; i++)
        {
            double v = 0;
            for (int k = -1; k <= 1; k++)
            {
                double u = i + (k / 3.0);
                v += Math.Exp(-(u * u) / s2);
            }

            w[i + r] = v;
            sum += v;
        }

        return [.. w.Select(v => (float)(v / sum))];
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        IPixelMemorySource memory = (IPixelMemorySource?)input.Memory ?? input.Context.Memory;
        PixelPlane<GrayF32> gray = PixelPlane.Allocate<GrayF32>(width, height, memory);
        PixelPlane<GrayF32>? work = null;
        PixelPlane<Gray8>? marks = null;
        try
        {
            work = PixelPlane.Allocate<GrayF32>(width, height, memory);
            marks = PixelPlane.Allocate<Gray8>(width, height, memory);
            Detect(input, gray, work, marks);
        }
        finally
        {
            gray.Dispose();
            work?.Dispose();
            marks?.Dispose();
        }
    }

    private void Detect<TPixel>(OperationInput<TPixel> input, PixelPlane<GrayF32> gray, PixelPlane<GrayF32> work, PixelPlane<Gray8> marks)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        int width = source.Width, height = source.Height;
        long bytes = (long)width * 4;
        int threads = input.Context.MaxParallelism;
        CancellationToken ct = input.CancellationToken;

        // The luma of the straight color times alpha, from straight floats whatever the type, so every type that holds the same
        // values (premultiplied ones included) gives the same map: the map is a threshold, and a rounding step can move an edge.
        RowRunner.Run(height, bytes, threads, (start, end) =>
        {
            float[] line = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                for (int y = start; y < end; y++)
                {
                    PixelConvert.Convert(source.Row(y), MemoryMarshal.Cast<float, RgbaF32>(line.AsSpan(0, width * 4)));
                    Span<float> g = Floats(gray, y);
                    for (int x = 0; x < width; x++)
                    {
                        g[x] = ((0.2126f * line[x * 4]) + (0.7152f * line[(x * 4) + 1]) + (0.0722f * line[(x * 4) + 2])) * line[(x * 4) + 3];
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(line);
            }
        }, ct);

        if (_weights.Length > 1)
        {
            Smooth(gray, work, threads, ct);
        }

        Gradient(gray, work, marks, threads, ct);
        float max = Suppress(work, marks, gray, threads, ct);

        // Edges need a magnitude above 0 as well, so a flat image or a zero threshold does not mark everything.
        Classify(gray, marks, _low * max, _high * max, threads, ct);
        Trace(marks, ct);

        RowRunner.Run(height, bytes, threads, (start, end) =>
        {
            float[] line = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                for (int y = start; y < end; y++)
                {
                    Span<RgbaF32> pixels = MemoryMarshal.Cast<float, RgbaF32>(line.AsSpan(0, width * 4));
                    PixelConvert.Convert(source.Row(y), pixels);
                    ReadOnlySpan<Gray8> m = marks.Row(y);
                    for (int x = 0; x < width; x++)
                    {
                        float v = m[x].L == Edge ? 1 : 0;
                        pixels[x] = new RgbaF32(v, v, v, pixels[x].A);
                    }

                    PixelConvert.Convert<RgbaF32, TPixel>(pixels, input.Destination.Row(y));
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(line);
            }
        }, ct);
    }

    private const byte Edge = 255;
    private const byte Strong = 2;
    private const byte Weak = 1;

    private static Span<float> Floats(PixelPlane<GrayF32> plane, int y) => MemoryMarshal.Cast<GrayF32, float>(plane.Row(y));

    /// <summary>Blurs <paramref name="gray"/> in place, across into <paramref name="work"/> and down back, edges repeating the edge pixel.</summary>
    private void Smooth(PixelPlane<GrayF32> gray, PixelPlane<GrayF32> work, int threads, CancellationToken ct)
    {
        int width = gray.Width, height = gray.Height, r = _weights.Length / 2;
        float[] weights = _weights;
        RowRunner.Run(height, (long)width * 4, threads, (start, end) =>
        {
            float[] padded = ArrayPool<float>.Shared.Rent(width + (2 * r));
            try
            {
                Span<float> line = padded.AsSpan(0, width + (2 * r));
                for (int y = start; y < end; y++)
                {
                    FastBlur.LoadPadded(Floats(gray, y), line, width, 1, r);
                    Span<float> output = Floats(work, y);
                    for (int k = 0; k < weights.Length; k++)
                    {
                        ConvolutionKernels.Accumulate(line.Slice(k, width), weights[k], output, k == 0);
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(padded);
            }
        }, ct);

        RowRunner.Run(height, (long)width * 4, threads, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                Span<float> output = Floats(gray, y);
                for (int k = 0; k < weights.Length; k++)
                {
                    ConvolutionKernels.Accumulate(Floats(work, Math.Clamp(y - r + k, 0, height - 1)), weights[k], output, k == 0);
                }
            }
        }, ct);
    }

    /// <summary>
    /// Canny's first differences over the 2 × 2 square from each pixel to its lower right (edges repeat): the magnitude into
    /// <paramref name="magnitudes"/> and the direction across the edge, 0 to 3, into <paramref name="directions"/>.
    /// </summary>
    private static void Gradient(PixelPlane<GrayF32> gray, PixelPlane<GrayF32> magnitudes, PixelPlane<Gray8> directions, int threads, CancellationToken ct)
    {
        int width = gray.Width, height = gray.Height;
        RowRunner.Run(height, (long)width * 4, threads, (start, end) =>
        {
            Span<float> lanes = stackalloc float[Vector256<float>.Count];
            for (int y = start; y < end; y++)
            {
                ReadOnlySpan<float> top = Floats(gray, y), bottom = Floats(gray, Math.Min(y + 1, height - 1));
                Span<float> m = Floats(magnitudes, y);
                Span<Gray8> d = directions.Row(y);
                int x = 0;
                if (Vector256.IsHardwareAccelerated)
                {
                    // The same operations in the same order as the scalar loop, eight pixels at a time, up to the last column.
                    Vector256<float> half = Vector256.Create(0.5f), zero = Vector256<float>.Zero;
                    for (; x <= width - 1 - Vector256<float>.Count; x += Vector256<float>.Count)
                    {
                        Vector256<float> a = Vector256.Create(top[x..]), b = Vector256.Create(top[(x + 1)..]);
                        Vector256<float> c = Vector256.Create(bottom[x..]), e = Vector256.Create(bottom[(x + 1)..]);
                        Vector256<float> dx = half * ((b - a) + (e - c)), dy = half * ((a + b) - (c + e));
                        Vector256.Sqrt((dx * dx) + (dy * dy)).CopyTo(m[x..]);
                        Vector256<float> slope = dy / dx;
                        Vector256<float> falling = Vector256.ConditionalSelect(
                            Vector256.LessThan(slope, Vector256.Create(-Steep)), zero,
                            Vector256.ConditionalSelect(Vector256.LessThan(slope, Vector256.Create(-Shallow)), Vector256.Create(1f), Vector256.Create(2f)));
                        Vector256<float> rising = Vector256.ConditionalSelect(
                            Vector256.GreaterThan(slope, Vector256.Create(Steep)), zero,
                            Vector256.ConditionalSelect(Vector256.GreaterThan(slope, Vector256.Create(Shallow)), Vector256.Create(3f), Vector256.Create(2f)));
                        Vector256<float> direction = Vector256.ConditionalSelect(Vector256.LessThan(slope, zero), falling, rising);
                        Vector256.ConditionalSelect(Vector256.Equals(dx, zero), zero, direction).CopyTo(lanes);
                        for (int i = 0; i < lanes.Length; i++)
                        {
                            d[x + i].L = (byte)lanes[i];
                        }
                    }
                }

                for (; x < width; x++)
                {
                    int x1 = Math.Min(x + 1, width - 1);
                    float dx = 0.5f * ((top[x1] - top[x]) + (bottom[x1] - bottom[x]));
                    float dy = 0.5f * ((top[x] + top[x1]) - (bottom[x] + bottom[x1]));
                    m[x] = MathF.Sqrt((dx * dx) + (dy * dy));
                    d[x].L = Direction(dx, dy);
                }
            }
        }, ct);
    }

    /// <summary>The direction across an edge, with y up: 0 vertical, 1 down to the right, 2 horizontal, 3 up to the right.</summary>
    internal static byte Direction(float dx, float dy)
    {
        if (dx == 0)
        {
            return 0;
        }

        float slope = dy / dx;
        if (slope < 0)
        {
            return slope < -Steep ? (byte)0 : slope < -Shallow ? (byte)1 : (byte)2;
        }

        return slope > Steep ? (byte)0 : slope > Shallow ? (byte)3 : (byte)2;
    }

    /// <summary>
    /// Keeps each magnitude that neither neighbour along its direction exceeds (ties stay), writing 0 elsewhere, into
    /// <paramref name="kept"/>. Returns the largest magnitude kept.
    /// </summary>
    private static float Suppress(PixelPlane<GrayF32> magnitudes, PixelPlane<Gray8> directions, PixelPlane<GrayF32> kept, int threads, CancellationToken ct)
    {
        int width = magnitudes.Width, height = magnitudes.Height;
        object gate = new();
        float max = 0;
        RowRunner.Run(height, (long)width * 4, threads, (start, end) =>
        {
            float local = 0;
            for (int y = start; y < end; y++)
            {
                ReadOnlySpan<float> above = Floats(magnitudes, Math.Max(y - 1, 0)), row = Floats(magnitudes, y), below = Floats(magnitudes, Math.Min(y + 1, height - 1));
                Span<Gray8> d = directions.Row(y);
                Span<float> output = Floats(kept, y);
                for (int x = 0; x < width; x++)
                {
                    int left = Math.Max(x - 1, 0), right = Math.Min(x + 1, width - 1);
                    (float a, float b) = d[x].L switch
                    {
                        0 => (above[x], below[x]),
                        1 => (above[left], below[right]),
                        2 => (row[left], row[right]),
                        _ => (above[right], below[left]),
                    };
                    float m = row[x];
                    float v = m < a || m < b ? 0 : m;
                    output[x] = v;
                    local = Math.Max(local, v);
                }
            }

            lock (gate)
            {
                max = Math.Max(max, local);
            }
        }, ct);
        return max;
    }

    /// <summary>Sorts every pixel into the marks: 2 at least <paramref name="high"/>, 1 at least <paramref name="low"/>, else 0; only magnitudes above 0 count.</summary>
    private static void Classify(PixelPlane<GrayF32> kept, PixelPlane<Gray8> marks, float low, float high, int threads, CancellationToken ct)
    {
        int width = kept.Width;
        RowRunner.Run(kept.Height, (long)width * 4, threads, (start, end) =>
        {
            for (int y = start; y < end; y++)
            {
                ReadOnlySpan<float> row = Floats(kept, y);
                Span<Gray8> m = marks.Row(y);
                for (int x = 0; x < width; x++)
                {
                    float v = row[x];
                    m[x].L = v <= 0 ? (byte)0 : v >= high ? Strong : v >= low ? Weak : (byte)0;
                }
            }
        }, ct);
    }

    /// <summary>
    /// Hysteresis on the sorted marks: every strong pixel, and everything 8-connected to one through weak or strong pixels, becomes
    /// an edge. The result does not depend on the order pixels are visited.
    /// </summary>
    private static void Trace(PixelPlane<Gray8> marks, CancellationToken ct)
    {
        int width = marks.Width, height = marks.Height;
        int[] stack = ArrayPool<int>.Shared.Rent(2048);
        try
        {
            if (marks.TryGetContiguous(out Span<Gray8> all))
            {
                TraceFlat(MemoryMarshal.AsBytes(all), width, height, ref stack, ct);
                return;
            }

            for (int y = 0; y < height; y++)
            {
                ct.ThrowIfCancellationRequested();
                Span<byte> row = MemoryMarshal.AsBytes(marks.Row(y));
                for (int x = row.IndexOf(Strong); x >= 0; x = Next(row, x))
                {
                    row[x] = Edge;
                    int count = 0;
                    stack[count++] = x;
                    stack[count++] = y;
                    while (count > 0)
                    {
                        int py = stack[--count], px = stack[--count];
                        int u0 = Math.Max(px - 1, 0), u1 = Math.Min(px + 1, width - 1);
                        for (int v = Math.Max(py - 1, 0); v <= Math.Min(py + 1, height - 1); v++)
                        {
                            Span<byte> flags = MemoryMarshal.AsBytes(marks.Row(v));
                            for (int u = u0; u <= u1; u++)
                            {
                                if (flags[u] is Weak or Strong)
                                {
                                    flags[u] = Edge;
                                    if (count + 2 > stack.Length)
                                    {
                                        Grow(ref stack, count);
                                    }

                                    stack[count++] = u;
                                    stack[count++] = v;
                                }
                            }
                        }
                    }
                }
            }
        }
        finally
        {
            ArrayPool<int>.Shared.Return(stack);
        }

    }

    /// <summary>Returns the index of the next strong pixel after <paramref name="x"/>, or −1.</summary>
    private static int Next(ReadOnlySpan<byte> row, int x)
    {
        int i = row[(x + 1)..].IndexOf(Strong);
        return i < 0 ? -1 : x + 1 + i;
    }

    private static void Grow(ref int[] stack, int count)
    {
        int[] larger = ArrayPool<int>.Shared.Rent(stack.Length * 2);
        stack.AsSpan(0, count).CopyTo(larger);
        ArrayPool<int>.Shared.Return(stack);
        stack = larger;
    }

    /// <summary><see cref="Trace"/> on marks in one block of memory, row after row: neighbours are found by index.</summary>
    private static void TraceFlat(Span<byte> marks, int width, int height, ref int[] stack, CancellationToken ct)
    {
        for (int i = Next(marks, -1); i >= 0; i = Next(marks, i))
        {
            if (i % (width * 64) == 0)
            {
                ct.ThrowIfCancellationRequested();
            }

            marks[i] = Edge;
            int count = 0;
            stack[count++] = i;
            while (count > 0)
            {
                int at = stack[--count];
                int py = at / width, px = at - (py * width);
                int u0 = px > 0 ? -1 : 0, u1 = px < width - 1 ? 1 : 0;
                int v0 = py > 0 ? -width : 0, v1 = py < height - 1 ? width : 0;
                for (int v = v0; v <= v1; v += width)
                {
                    for (int u = u0; u <= u1; u++)
                    {
                        int n = at + v + u;
                        if (marks[n] is Weak or Strong)
                        {
                            marks[n] = Edge;
                            if (count == stack.Length)
                            {
                                Grow(ref stack, count);
                            }

                            stack[count++] = n;
                        }
                    }
                }
            }
        }
    }
}
