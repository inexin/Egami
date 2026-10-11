using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Blurs along a path through each pixel, reading between pixels bilinearly, edges repeating the edge pixel: a straight streak
/// (motion), an arc around a center (rotational) or a ray from it (zoom). Samples are averaged on premultiplied values, which is
/// ImageMagick's alpha-weighted mean.
/// </summary>
/// <remarks>
/// A motion streak moves every pixel by the same offsets, so its bilinear samples add up to one sparse kernel of whole-pixel taps,
/// run on rows as a convolution. A rotational blur resamples the image into polar rows, one sample per pixel of arc, blurs each
/// row with a circular box, and samples back. A zoom blur samples each pixel's ray with 2.5.2's footprint sampler.
/// </remarks>
internal sealed class DirectionalBlurOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    /// <summary>The most samples a zoom path takes.</summary>
    internal const int MaxTaps = 256;

    /// <summary>The radii one band of the rotational blur holds.</summary>
    private const int PolarBand = 16;

    private readonly DirectionalKind _kind;
    private readonly float _amount;
    private readonly Vector2? _center;
    private readonly (int Dx, int Dy, float Weight)[] _taps;

    private DirectionalBlurOperation(DirectionalKind kind, float amount, Vector2? center, (int, int, float)[] taps)
    {
        _kind = kind;
        _amount = amount;
        _center = center;
        _taps = taps;
    }

    internal enum DirectionalKind
    {
        Motion,
        Rotational,
        Zoom,
    }

    /// <summary>Gets a motion streak's reach; rotational and zoom blurs follow the image's center, so a scope runs them on the whole image.</summary>
    public (int X, int Y)? Reach
    {
        get
        {
            if (_kind != DirectionalKind.Motion)
            {
                return null;
            }

            int x = 0, y = 0;
            foreach ((int dx, int dy, _) in _taps)
            {
                x = Math.Max(x, Math.Abs(dx));
                y = Math.Max(y, Math.Abs(dy));
            }

            return (x, y);
        }
    }

    /// <summary>
    /// ImageMagick's -motion-blur: 2⌈r⌉ + 1 samples one pixel apart along <paramref name="degrees"/> (clockwise from the x axis, y
    /// down), weighted by a Gaussian of <paramref name="sigma"/> from the pixel itself; <paramref name="centered"/> puts the pixel
    /// in the middle of a streak of the same length. Each sample is split bilinearly over the four pixels around it.
    /// <paramref name="roundOffsets"/> rounds each sample to the nearest pixel instead, as ImageMagick does, which tests use to
    /// show the streaks are otherwise the same.
    /// </summary>
    internal static DirectionalBlurOperation Motion(float radius, float sigma, float degrees, bool centered, bool roundOffsets = false)
    {
        int steps = 2 * (int)Math.Ceiling(radius);
        double angle = degrees * Math.PI / 180, cos = Math.Cos(angle), sin = Math.Sin(angle);
        double[] weights = new double[steps + 1];
        double sum = 0;
        for (int i = 0; i <= steps; i++)
        {
            double t = centered ? i - (steps / 2.0) : i;
            weights[i] = Math.Exp(-(t * t) / (2.0 * sigma * sigma));
            sum += weights[i];
        }

        Dictionary<(int, int), double> taps = [];
        for (int i = 0; i <= steps; i++)
        {
            double t = centered ? i - (steps / 2.0) : i;
            double u = t * cos, v = t * sin, w = weights[i] / sum;
            if (roundOffsets)
            {
                Splat(taps, (int)Math.Ceiling(u - 0.5), (int)Math.Ceiling(v - 0.5), w);
                continue;
            }

            // Bilinear: the sample's weight goes to the four pixels around it, by how close it lies to each.
            int x0 = (int)Math.Floor(u), y0 = (int)Math.Floor(v);
            double fx = u - x0, fy = v - y0;
            Splat(taps, x0, y0, w * (1 - fx) * (1 - fy));
            Splat(taps, x0 + 1, y0, w * fx * (1 - fy));
            Splat(taps, x0, y0 + 1, w * (1 - fx) * fy);
            Splat(taps, x0 + 1, y0 + 1, w * fx * fy);
        }

        return new DirectionalBlurOperation(DirectionalKind.Motion, 0, null, [.. taps.Where(t => t.Value > 1e-9).OrderBy(t => t.Key.Item2).ThenBy(t => t.Key.Item1).Select(t => (t.Key.Item1, t.Key.Item2, (float)t.Value))]);

        static void Splat(Dictionary<(int, int), double> taps, int x, int y, double w)
        {
            taps[(x, y)] = taps.GetValueOrDefault((x, y)) + w;
        }
    }

    /// <summary>An arc of <paramref name="degrees"/> around the center, centered on the pixel, as ImageMagick's -rotational-blur.</summary>
    internal static DirectionalBlurOperation Rotational(float degrees, Vector2? center) => new(DirectionalKind.Rotational, degrees * MathF.PI / 180, center, []);

    /// <summary>A ray through the pixel toward and away from the center, <paramref name="amount"/> of its distance long.</summary>
    internal static DirectionalBlurOperation Zoom(float amount, Vector2? center) => new(DirectionalKind.Zoom, amount, center, []);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // Positions are in the sampler's coordinates, pixel centers at whole numbers; the public center has them at +0.5.
        Vector2 center = (_center ?? new Vector2(input.Source.Width / 2f, input.Source.Height / 2f)) - new Vector2(0.5f);
        switch (_kind)
        {
            case DirectionalKind.Motion:
                RunMotion(input);
                break;
            case DirectionalKind.Rotational:
                RunRotational(input, center);
                break;
            default:
                RunZoom(input, center);
                break;
        }
    }

    private void RunMotion<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        RowConverter<TPixel> converter = new(null);
        int rx = 0, top = int.MaxValue, bottom = int.MinValue;
        foreach ((int dx, int dy, _) in _taps)
        {
            rx = Math.Max(rx, Math.Abs(dx));
            top = Math.Min(top, dy);
            bottom = Math.Max(bottom, dy);
        }

        int rows = bottom - top + 1, width = source.Width;
        PaddedRows<TPixel> padded = new(source, converter, EdgeMode.Clamp, rx);
        input.ForEachBand((start, end) =>
        {
            float[] ring = ArrayPool<float>.Shared.Rent(rows * padded.Length);
            float[] output = ArrayPool<float>.Shared.Rent(width * 4);
            bool[] opaque = ArrayPool<bool>.Shared.Rent(rows);
            try
            {
                for (int v = start + top; v < start + bottom; v++)
                {
                    int slot = Mod(v, rows);
                    opaque[slot] = padded.Load(v, ring.AsSpan(slot * padded.Length, padded.Length));
                }

                for (int y = start; y < end; y++)
                {
                    int next = Mod(y + bottom, rows);
                    opaque[next] = padded.Load(y + bottom, ring.AsSpan(next * padded.Length, padded.Length));
                    Span<float> row = output.AsSpan(0, width * 4);
                    bool first = true, allOpaque = true;
                    foreach ((int dx, int dy, float weight) in _taps)
                    {
                        int slot = Mod(y + dy, rows);
                        allOpaque &= opaque[slot];
                        ConvolutionKernels.Accumulate(ring.AsSpan((slot * padded.Length) + ((rx + dx) * 4), width * 4), weight, row, first);
                        first = false;
                    }

                    converter.Store(row, destination.Row(y), allOpaque);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ring);
                ArrayPool<float>.Shared.Return(output);
                ArrayPool<bool>.Shared.Return(opaque);
            }
        });
    }

    private static int Mod(int v, int n) => ((v % n) + n) % n;

    /// <summary>
    /// The rotational blur through polar rows: row r holds about 2πr samples around the circle of radius r, read bilinearly; each
    /// row gets a circular box as wide as the arc (fractional at its ends), and each output pixel reads the two rows around its
    /// radius, linearly in angle and radius. Bands of radii run on separate threads and write disjoint rings of the output.
    /// </summary>
    private void RunRotational<TPixel>(OperationInput<TPixel> input, Vector2 center)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        int width = source.Width, height = source.Height;
        double cx = center.X, cy = center.Y;
        double farthest = 0;
        foreach ((double x, double y) in new[] { (0.0, 0.0), (width - 1.0, 0.0), (0.0, height - 1.0), (width - 1.0, height - 1.0) })
        {
            farthest = Math.Max(farthest, Math.Sqrt(((x - cx) * (x - cx)) + ((y - cy) * (y - cy))));
        }

        int radii = (int)Math.Ceiling(farthest) + 2;
        int bands = (radii + PolarBand - 1) / PolarBand;
        SamplerSettings settings = new(ResampleFilter.Bilinear, EdgeMode.Clamp, default, null);
        RowConverter<TPixel> converter = new(null);
        Chunks.Run(bands, (long)width * height * 64, input.Context.MaxParallelism, (start, end) =>
        {
            int capacity = Samples(Math.Min(radii, end * PolarBand) + 1);
            float[] polar = ArrayPool<float>.Shared.Rent((PolarBand + 1) * capacity * 4);
            float[] scratch = ArrayPool<float>.Shared.Rent(capacity * 4);
            float[] pixel = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                for (int band = start; band < end; band++)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                    int r0 = band * PolarBand, r1 = Math.Min(r0 + PolarBand, radii);

                    // Rows r0 to r1, the last one again in the next band, so every radius in [r0, r1) has the row past it.
                    FootprintSampler<TPixel> sampler = new(source, settings);
                    for (int r = r0; r <= r1; r++)
                    {
                        int n = Samples(r);
                        Span<float> row = polar.AsSpan((r - r0) * capacity * 4, n * 4);
                        // Around the circle by repeated rotation, which double precision keeps exact enough over a row.
                        double step = 2 * Math.PI / n, cosStep = Math.Cos(step), sinStep = Math.Sin(step), c = r, si = 0;
                        for (int j = 0; j < n; j++)
                        {
                            sampler.Kernel(cx + c, cy + si).CopyTo(row[(j * 4)..]);
                            (c, si) = ((c * cosStep) - (si * sinStep), (c * sinStep) + (si * cosStep));
                        }

                        CircularBox(row, scratch.AsSpan(0, n * 4), n * _amount / (2 * Math.PI));
                    }

                    WriteRing(input, converter, polar, capacity, r0, r1, cx, cy, pixel);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(polar);
                ArrayPool<float>.Shared.Return(scratch);
                ArrayPool<float>.Shared.Return(pixel);
            }
        }, input.CancellationToken);
    }

    /// <summary>The samples around the circle of radius <paramref name="r"/>: about one per pixel of arc, at least 8.</summary>
    private static int Samples(int r) => Math.Max(8, (int)Math.Ceiling(2 * Math.PI * r));

    /// <summary>
    /// Replaces each sample of a circular row with the mean over a window <paramref name="length"/> samples long centered on it:
    /// whole samples inside, and a fraction of the two at its ends.
    /// </summary>
    private static void CircularBox(Span<float> row, Span<float> scratch, double length)
    {
        int n = row.Length / 4;
        if (length <= 1)
        {
            return;
        }

        double half = Math.Min((length - 1) / 2, (n - 3) / 2.0);
        int k = (int)Math.Floor(half);
        float alpha = (float)(half - k), norm = (float)(1 / ((2 * k) + 1 + (2 * alpha)));
        row.CopyTo(scratch);
        Span<Vector4> s = MemoryMarshal.Cast<float, Vector4>(scratch);
        Span<Vector4> d = MemoryMarshal.Cast<float, Vector4>(row);
        Vector4 inner = Vector4.Zero;
        for (int i = -k; i <= k; i++)
        {
            inner += s[Mod(i, n)];
        }

        for (int i = 0; i < n; i++)
        {
            Vector4 far = s[Mod(i + k + 1, n)];
            d[i] = (inner + ((s[Mod(i - k - 1, n)] + far) * alpha)) * norm;
            inner += far - s[Mod(i - k, n)];
        }
    }

    /// <summary>Writes every output pixel whose distance from the center lies in [<paramref name="r0"/>, <paramref name="r1"/>).</summary>
    private static void WriteRing<TPixel>(OperationInput<TPixel> input, RowConverter<TPixel> converter, float[] polar, int capacity, int r0, int r1, double cx, double cy, float[] pixel)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int width = input.Source.Width, height = input.Source.Height;
        int yFirst = Math.Max(0, (int)Math.Floor(cy - r1)), yLast = Math.Min(height - 1, (int)Math.Ceiling(cy + r1));
        for (int y = yFirst; y <= yLast; y++)
        {
            double dy = y - cy, outer = (r1 * (double)r1) - (dy * dy);
            if (outer <= 0)
            {
                continue;
            }

            // The ring crosses this row in up to two runs, left and right of the hole in its middle.
            double reach = Math.Sqrt(outer), inner = (r0 * (double)r0) - (dy * dy), hole = inner > 0 ? Math.Sqrt(inner) : 0;
            int left = Math.Max(0, (int)Math.Floor(cx - reach)), right = Math.Min(width - 1, (int)Math.Ceiling(cx + reach));
            int holeLeft = (int)Math.Ceiling(cx - hole) + 1, holeRight = (int)Math.Floor(cx + hole) - 1;
            Span<TPixel> target = input.Destination.Row(y);
            int run = -1;
            for (int x = left; x <= right + 1; x++)
            {
                double dx = x - cx, r = Math.Sqrt((dx * dx) + (dy * dy));
                if (x > right || r < r0 || r >= r1)
                {
                    // A run of the ring's pixels ends: store it in one go.
                    if (run >= 0)
                    {
                        converter.Store(pixel.AsSpan(0, (x - run) * 4), target[run..x], opaque: false);
                        run = -1;
                    }

                    if (x >= holeLeft && x < holeRight)
                    {
                        x = holeRight - 1;
                    }

                    continue;
                }

                run = run < 0 ? x : run;

                int ri = (int)Math.Floor(r);
                double phi = Math.Atan2(dy, dx);
                if (phi < 0)
                {
                    phi += 2 * Math.PI;
                }

                Vector4 a = Around(polar, capacity, ri - r0, Samples(ri), phi), b = Around(polar, capacity, ri + 1 - r0, Samples(ri + 1), phi);
                (a + ((b - a) * (float)(r - ri))).CopyTo(pixel.AsSpan((x - run) * 4));
            }
        }
    }

    /// <summary>Reads polar row <paramref name="row"/> at angle <paramref name="phi"/>, linearly between its two nearest samples.</summary>
    private static Vector4 Around(float[] polar, int capacity, int row, int n, double phi)
    {
        double at = phi * n / (2 * Math.PI);
        int j = (int)Math.Floor(at);
        float f = (float)(at - j);
        Span<Vector4> samples = MemoryMarshal.Cast<float, Vector4>(polar.AsSpan(row * capacity * 4, n * 4));
        Vector4 a = samples[Mod(j, n)], b = samples[Mod(j + 1, n)];
        return a + ((b - a) * f);
    }

    private void RunZoom<TPixel>(OperationInput<TPixel> input, Vector2 center)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        SamplerSettings settings = new(ResampleFilter.Bilinear, EdgeMode.Clamp, default, null);
        RowConverter<TPixel> converter = new(null);
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int width = source.Width;
        input.ForEachBand((start, end) =>
        {
            float[] sums = ArrayPool<float>.Shared.Rent(width * 4);
            try
            {
                FootprintSampler<TPixel> sampler = new(source, settings);
                Span<float> row = sums.AsSpan(0, width * 4);
                for (int y = start; y < end; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        ZoomPath(ref sampler, x, y, center).CopyTo(row[(x * 4)..]);
                    }

                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(sums);
            }
        });
    }

    /// <summary>The mean of the samples on the ray through (x, y), scales spaced evenly in their logarithm.</summary>
    private Vector4 ZoomPath<TPixel>(ref FootprintSampler<TPixel> sampler, int x, int y, Vector2 center)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        double dx = x - center.X, dy = y - center.Y, distance = Math.Sqrt((dx * dx) + (dy * dy));
        int count = Math.Clamp((int)Math.Ceiling(_amount * distance) + 1, 1, MaxTaps);
        if (count == 1)
        {
            return sampler.Kernel(x, y);
        }

        Vector4 sum = Vector4.Zero;
        for (int i = 0; i < count; i++)
        {
            double scale = Math.Exp((((double)i / (count - 1)) - 0.5) * _amount);
            sum += sampler.Kernel(center.X + (dx * scale), center.Y + (dy * scale));
        }

        return sum / count;
    }
}
