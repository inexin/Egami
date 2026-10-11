using System.Buffers;

using Egami.Operations.Scopes;
using Egami.Pixels;

namespace Egami.Operations.Tone;

/// <summary>A measured step that keeps the size, so it may run inside Within and OnChannels and measure only their scope.</summary>
internal interface IScopedMeasuredStep : IMeasuredStep
{
    /// <summary>Measures the frames inside <paramref name="scope"/> and returns the step that runs in this one's place.</summary>
    IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>What a measured step inside scopes reads: an area, the masks' coverage there, and the channels.</summary>
internal sealed record MeasureScope(Rect Area, IReadOnlyList<MaskCoverage> Masks, ColorChannels Channels)
{
    /// <summary>The whole frame, every channel.</summary>
    internal static MeasureScope Whole(int width, int height) => new(new Rect(0, 0, width, height), [], ColorChannels.All);

    /// <summary>Gets red, green and blue limited to the scope's channels; all three when it holds none of them.</summary>
    internal ColorChannels ColorChannels => (Channels & ColorChannels.Color) == 0 ? ColorChannels.Color : Channels & ColorChannels.Color;

    internal MeasureScope Within(Rect rect) => this with { Area = Area.Intersect(rect) };

    internal MeasureScope WithMask(MaskCoverage mask) => this with { Area = Area.Intersect(mask.Bounds), Masks = [.. Masks, mask] };

    internal MeasureScope OnChannels(ColorChannels channels) => this with { Channels = Channels & channels };
}

/// <summary>Runs a measured step's per-frame result on the matching frame, for perFrame.</summary>
internal sealed class PerFrameOperation(IRasterOperation[] steps) : IRasterOperation, IRunsOnViews, Geometry.IHoldsBuffers
{
    public Size OutputSize(Size input) => input;

    public void ReleaseBuffers()
    {
        foreach (IRasterOperation step in steps)
        {
            (step as Geometry.IHoldsBuffers)?.ReleaseBuffers();
        }
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        steps[Math.Min(input.FrameIndex, steps.Length - 1)].Run(input);
    }
}

/// <summary>Reads the pixels a measured step looks at, in parallel chunks of rows, as straight RGBA with a weight per pixel.</summary>
internal static class PixelReader
{
    /// <summary>Visits one row: its pixels, and each one's weight (alpha times the masks' coverage).</summary>
    internal delegate void RowVisitor<TState>(TState state, ReadOnlySpan<RgbaF32> pixels, ReadOnlySpan<float> weights);

    /// <summary>
    /// Visits every row of every frame inside the scope's area. Each parallel chunk gets its own state from
    /// <paramref name="create"/>; the states are returned for the caller to merge.
    /// </summary>
    internal static List<TState> Read<TPixel, TState>(
        IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, Func<TState> create, RowVisitor<TState> visit, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<TState> states = [];
        Rect area = scope.Area;
        if (area.IsEmpty)
        {
            return states;
        }

        int chunks = Math.Max(1, Math.Min(Math.Max(1, maxParallelism), area.Height / 64));
        foreach (PixelPlane<TPixel> frame in frames)
        {
            TState[] local = new TState[chunks];
            if (chunks == 1)
            {
                Chunk(0);
            }
            else
            {
                Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallelism), CancellationToken = cancellationToken }, Chunk);
            }

            states.AddRange(local);

            void Chunk(int chunk)
            {
                TState state = local[chunk] = create();
                int start = area.Y + (int)((long)area.Height * chunk / chunks), end = area.Y + (int)((long)area.Height * (chunk + 1) / chunks);
                RgbaF32[] row = ArrayPool<RgbaF32>.Shared.Rent(area.Width);
                float[] weights = ArrayPool<float>.Shared.Rent(area.Width);
                float[] coverage = ArrayPool<float>.Shared.Rent(area.Width);
                try
                {
                    Span<RgbaF32> pixels = row.AsSpan(0, area.Width);
                    Span<float> w = weights.AsSpan(0, area.Width);
                    for (int y = start; y < end; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        PixelConvert.Convert<TPixel, RgbaF32>(frame.Row(y).Slice(area.X, area.Width), pixels);
                        for (int x = 0; x < pixels.Length; x++)
                        {
                            w[x] = Math.Clamp(pixels[x].A, 0f, 1f);
                        }

                        foreach (MaskCoverage mask in scope.Masks)
                        {
                            Span<float> c = coverage.AsSpan(0, area.Width);
                            mask.RowFloat(y, area.X, c);
                            for (int x = 0; x < w.Length; x++)
                            {
                                w[x] *= c[x];
                            }
                        }

                        visit(state, pixels, w);
                    }
                }
                finally
                {
                    ArrayPool<RgbaF32>.Shared.Return(row);
                    ArrayPool<float>.Shared.Return(weights);
                    ArrayPool<float>.Shared.Return(coverage);
                }
            }
        }

        return states;
    }

    /// <summary>The number of histogram bins for a pixel type: 256 for 8-bit channels, 65,536 for the rest.</summary>
    internal static int Bins<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return TPixel.Layout.ChannelType == PixelChannelType.Unorm8 ? 256 : 65536;
    }

    /// <summary>The bin of a value: nearest, with values outside 0..1 counted at the ends.</summary>
    internal static int Bin(float value, int bins)
    {
        float v = (value * (bins - 1)) + 0.5f;
        return v <= 0 || float.IsNaN(v) ? 0 : v >= bins - 1 ? bins - 1 : (int)v;
    }
}

/// <summary>
/// Weighted histograms of red, green, blue and a fourth value (luminance, or another measure), summed over bands and frames. Counts
/// are weights, so transparent and masked-out pixels do not vote. Shaped so 2.5.5 can make it public as Raster.Histogram.
/// </summary>
internal sealed class Histogram : IDisposable
{
    private double[]? _sum;

    internal Histogram(int bins)
    {
        Bins = bins;
        Channels = [Rent(bins), Rent(bins), Rent(bins), Rent(bins)];
    }

    /// <summary>Gets the number of bins: 256 or 65,536.</summary>
    internal int Bins { get; }

    /// <summary>Gets the red, green, blue and fourth-value counts.</summary>
    internal double[][] Channels { get; }

    /// <summary>Gets the total weight counted.</summary>
    internal double Total { get; private set; }

    /// <summary>
    /// Measures the frames inside the scope: red, green and blue, and as the fourth value ImageMagick's intensity, L* scaled to 0..1,
    /// or what a threshold mode compares. Byte pixels read straight from memory when no mask weighs them.
    /// </summary>
    internal static Histogram Measure<TPixel>(
        IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, int bins, int maxParallelism, CancellationToken cancellationToken,
        HistogramValue value = HistogramValue.Intensity, ThresholdMode mode = ThresholdMode.Luminance)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int[]? map = ByteMap<TPixel>();
        List<Histogram> parts = map is not null && scope.Masks.Count == 0
            ? ReadBytes(frames, scope, map, bins, value, mode, maxParallelism, cancellationToken)
            : PixelReader.Read(frames, scope, () => new Histogram(bins), (h, pixels, weights) =>
            {
                double[] r = h.Channels[0], g = h.Channels[1], b = h.Channels[2], i = h.Channels[3];
                for (int x = 0; x < pixels.Length; x++)
                {
                    float w = weights[x];
                    if (w <= 0)
                    {
                        continue;
                    }

                    RgbaF32 p = pixels[x];
                    r[PixelReader.Bin(p.R, bins)] += w;
                    g[PixelReader.Bin(p.G, bins)] += w;
                    b[PixelReader.Bin(p.B, bins)] += w;
                    i[PixelReader.Bin(Fourth(value, mode, p.R, p.G, p.B), bins)] += w;
                    h.Total += w;
                }
            }, maxParallelism, cancellationToken);
        Histogram sum = parts.Count > 0 ? parts[0] : new(bins);
        for (int p = 1; p < parts.Count; p++)
        {
            Histogram part = parts[p];
            for (int c = 0; c < 4; c++)
            {
                for (int j = 0; j < bins; j++)
                {
                    sum.Channels[c][j] += part.Channels[c][j];
                }
            }

            sum.Total += part.Total;
            part.Dispose();
        }

        return sum;
    }

    /// <summary>The fourth value of a pixel, from 0 to 1.</summary>
    internal static float Fourth(HistogramValue value, ThresholdMode mode, float r, float g, float b)
    {
        return value switch
        {
            HistogramValue.None => 0,
            HistogramValue.Lightness => LabMath.Lightness(r, g, b) / 100,
            HistogramValue.Threshold => (float)(ThresholdStage.Measure(mode, r, g, b) / 65535),
            _ => (float)ToneMath.Intensity(r, g, b),
        };
    }

    /// <summary>
    /// The byte path: samples read from memory, the fourth value of every distinct pixel computed from the same floats the general
    /// path would see, so both paths count every pixel in the same bins.
    /// </summary>
    private static List<Histogram> ReadBytes<TPixel>(
        IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope scope, int[] map, int bins, HistogramValue value, ThresholdMode mode, int maxParallelism, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        float[] unit = ByteUnits.Value;
        int[] bin = bins == 256 ? Bins256.Value : Bins65536.Value;
        int channels = map.Length;

        // Intensity and the luminance threshold's measure are sums of one term per channel: tables, summed in the same order.
        double[]? terms = value is HistogramValue.Intensity ? IntensityTerms.Value
            : value is HistogramValue.Threshold && mode == ThresholdMode.Luminance ? ThresholdTerms.Value : null;

        double scale = value is HistogramValue.Threshold ? 1.0 / 65535 : 1;
        int ri = Array.IndexOf(map, 0), gi = Array.IndexOf(map, 1), bi = Array.IndexOf(map, 2), ai = Array.IndexOf(map, 3);
        if (map[0] < 0)
        {
            // Gray: the one sample is red, green and blue.
            ri = gi = bi = 0;
        }
        Rect area = scope.Area;
        List<Histogram> states = [];
        if (area.IsEmpty)
        {
            return states;
        }

        int chunks = Math.Max(1, Math.Min(Math.Max(1, maxParallelism), area.Height / 64));
        foreach (PixelPlane<TPixel> frame in frames)
        {
            Histogram[] local = new Histogram[chunks];
            if (chunks == 1)
            {
                Chunk(0);
            }
            else
            {
                Parallel.For(0, chunks, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, maxParallelism), CancellationToken = cancellationToken }, Chunk);
            }

            states.AddRange(local);

            void Chunk(int chunk)
            {
                Histogram h = local[chunk] = new Histogram(bins);
                double[] r = h.Channels[0], g = h.Channels[1], b = h.Channels[2], i = h.Channels[3];
                int start = area.Y + (int)((long)area.Height * chunk / chunks), end = area.Y + (int)((long)area.Height * (chunk + 1) / chunks);
                double total = 0;
                for (int y = start; y < end; y++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    ReadOnlySpan<byte> row = System.Runtime.InteropServices.MemoryMarshal.AsBytes(frame.Row(y).Slice(area.X, area.Width));
                    for (int at = 0; at + channels <= row.Length; at += channels)
                    {
                        float w = ai >= 0 ? unit[row[at + ai]] : 1;
                        if (w <= 0)
                        {
                            continue;
                        }

                        byte pr = row[at + ri], pg = row[at + gi], pb = row[at + bi];
                        r[bin[pr]] += w;
                        g[bin[pg]] += w;
                        b[bin[pb]] += w;
                        if (terms is not null)
                        {
                            i[PixelReader.Bin((float)((terms[pr] + terms[256 + pg] + terms[512 + pb]) * scale), bins)] += w;
                        }
                        else if (value != HistogramValue.None)
                        {
                            i[PixelReader.Bin(Fourth(value, mode, unit[pr], unit[pg], unit[pb]), bins)] += w;
                        }

                        total += w;
                    }
                }

                h.Total = total;
            }
        }

        return states;
    }

    /// <summary>The float each byte value converts to, as PixelConvert gives it, so both paths see the same values.</summary>
    private static readonly Lazy<float[]> ByteUnits = new(() =>
    {
        Rgba8[] grays = new Rgba8[256];
        RgbaF32[] floats = new RgbaF32[256];
        for (int v = 0; v < 256; v++)
        {
            grays[v] = new Rgba8((byte)v, (byte)v, (byte)v, (byte)v);
        }

        PixelConvert.Convert<Rgba8, RgbaF32>(grays, floats);
        return [.. floats.Select(f => f.R)];
    });

    private static readonly Lazy<int[]> Bins256 = new(() => [.. ByteUnits.Value.Select(u => PixelReader.Bin(u, 256))]);

    /// <summary>Each channel's term of ImageMagick's intensity, per byte value, and the same on 16-bit samples for thresholds.</summary>
    private static readonly Lazy<double[]> IntensityTerms = new(() => Terms(x => x));

    private static readonly Lazy<double[]> ThresholdTerms = new(() => Terms(ThresholdStage.Sample));

    private static double[] Terms(Func<double, double> sample)
    {
        ReadOnlySpan<double> weights = [0.212656, 0.715158, 0.072186];
        double[] terms = new double[3 * 256];
        for (int c = 0; c < 3; c++)
        {
            for (int v = 0; v < 256; v++)
            {
                terms[(c * 256) + v] = weights[c] * sample(ByteUnits.Value[v]);
            }
        }

        return terms;
    }

    private static readonly Lazy<int[]> Bins65536 = new(() => [.. ByteUnits.Value.Select(u => PixelReader.Bin(u, 65536))]);

    /// <summary>Gets, for types that store one byte per channel, which RGBA channel each byte holds (−1 for gray); null for others.</summary>
    private static int[]? ByteMap<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (TPixel.Layout.ChannelType != PixelChannelType.Unorm8 || TPixel.Layout.Alpha == AlphaKind.Premultiplied)
        {
            return null;
        }

        int[]? map = TPixel.Layout.ChannelOrder switch
        {
            PixelChannelOrder.Rgba => [0, 1, 2, 3],
            PixelChannelOrder.Bgra => [2, 1, 0, 3],
            PixelChannelOrder.Rgb => [0, 1, 2],
            PixelChannelOrder.Bgr => [2, 1, 0],
            PixelChannelOrder.Argb => [3, 0, 1, 2],
            PixelChannelOrder.Abgr => [3, 2, 1, 0],
            PixelChannelOrder.Gray => [-1],
            PixelChannelOrder.GrayAlpha => [-1, 3],
            _ => null,
        };
        return map is not null && System.Runtime.CompilerServices.Unsafe.SizeOf<TPixel>() == map.Length ? map : null;
    }

    /// <summary>Returns the counts of the chosen color channels summed, for linked steps; the histogram owns the array.</summary>
    internal double[] Sum(ColorChannels channels)
    {
        double[] sum = _sum ??= Rent(Bins);
        Array.Clear(sum, 0, Bins);
        for (int c = 0; c < 3; c++)
        {
            if (((int)channels & (1 << c)) != 0)
            {
                for (int j = 0; j < Bins; j++)
                {
                    sum[j] += Channels[c][j];
                }
            }
        }

        return sum;
    }

    /// <summary>Returns the arrays to the pool.</summary>
    public void Dispose()
    {
        foreach (double[] channel in Channels)
        {
            ArrayPool<double>.Shared.Return(channel);
        }

        if (_sum is not null)
        {
            ArrayPool<double>.Shared.Return(_sum);
        }
    }

    /// <summary>Rents a cleared array; bins are powers of two, which the pool hands out at exactly that length.</summary>
    private static double[] Rent(int bins)
    {
        double[] array = ArrayPool<double>.Shared.Rent(bins);
        Array.Clear(array, 0, bins);
        return array;
    }
}

/// <summary>
/// A scope whose sub-chain holds a measured step: at the top level it is measured like one. The sub-chain runs up to the
/// measured step on copies of the frames, the step measures the copies inside the scope, and the scope comes back with the step
/// it resolved to in its place.
/// </summary>
internal sealed class MeasuredScopeOperation(IMeasuredScope scope) : IMeasuredStep
{
    public Size OutputSize(Size input) => throw new InvalidOperationException("A measured scope is never sized up front.");

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        throw new InvalidOperationException("A measured scope never runs itself.");
    }

    internal IMeasuredScope Scope => scope;

    public IRasterOperation Measure<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        return scope.Resolve(frames, MeasureScope.Whole(frames[0].Width, frames[0].Height), context, cancellationToken);
    }

    /// <summary>Returns whether a planned sub-chain holds a measured step, directly or in a nested scope.</summary>
    internal static bool Holds(IReadOnlyList<IRasterOperation> steps) => steps.Any(s => s is IScopedMeasuredStep or MeasuredScopeOperation);

    /// <summary>
    /// Resolves every measured step of a sub-chain in order: the steps before one run on copies of the frames inside
    /// <paramref name="area"/>, then it measures the copies inside <paramref name="inner"/>.
    /// </summary>
    internal static List<IRasterOperation> ResolveSteps<TPixel>(
        IReadOnlyList<IRasterOperation> steps, IReadOnlyList<PixelPlane<TPixel>> frames, Rect area, MeasureScope inner, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        List<IRasterOperation> resolved = [.. steps];
        for (int k = 0; k < resolved.Count; k++)
        {
            if (resolved[k] is not (IScopedMeasuredStep or MeasuredScopeOperation))
            {
                continue;
            }

            List<PixelPlane<TPixel>> copies = [];
            try
            {
                for (int f = 0; f < frames.Count; f++)
                {
                    PixelPlane<TPixel> copy = PixelPlane.Allocate<TPixel>(frames[f].Width, frames[f].Height, context.Memory);
                    copies.Add(copy);
                    for (int y = 0; y < copy.Height; y++)
                    {
                        frames[f].Row(y).CopyTo(copy.Row(y));
                    }

                    if (k > 0)
                    {
                        ScopeRunner.Run(resolved.GetRange(0, k), new OperationInput<TPixel>(copy, copy, context, f, cancellationToken), area);
                    }
                }

                resolved[k] = resolved[k] is IScopedMeasuredStep leaf
                    ? leaf.Measure(copies, inner, context, cancellationToken)
                    : ((MeasuredScopeOperation)resolved[k]).Scope.Resolve(copies, inner, context, cancellationToken);
            }
            finally
            {
                foreach (PixelPlane<TPixel> copy in copies)
                {
                    copy.Dispose();
                }
            }
        }

        return resolved;
    }
}

/// <summary>A scope that can resolve the measured steps of its sub-chain.</summary>
internal interface IMeasuredScope
{
    /// <summary>Returns the scope with every measured step of its sub-chain resolved, measuring inside <paramref name="outer"/> narrowed to this scope.</summary>
    IRasterOperation Resolve<TPixel>(IReadOnlyList<PixelPlane<TPixel>> frames, MeasureScope outer, EgamiContext context, CancellationToken cancellationToken)
        where TPixel : unmanaged, IPixelType<TPixel>;
}

/// <summary>The fourth value a <see cref="Histogram"/> counts.</summary>
internal enum HistogramValue
{
    /// <summary>Nothing: the fourth histogram stays empty.</summary>
    None,

    /// <summary>ImageMagick's intensity: Rec. 709 luma of the stored values.</summary>
    Intensity,

    /// <summary>L* of Lab, scaled to 0..1.</summary>
    Lightness,

    /// <summary>What a threshold mode compares.</summary>
    Threshold,
}
