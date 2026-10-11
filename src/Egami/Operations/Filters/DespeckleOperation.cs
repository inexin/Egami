using System.Buffers;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Crimmins' complementary hulling (Crimmins, 1985), as ImageMagick's -despeckle runs it: each color channel, framed by a
/// one-pixel border of zeros, goes through 16 hulling passes, four along each of the vertical, horizontal and two diagonal
/// directions (lighten along +d and −d, then darken along −d and +d). A pass moves a value at most one 8-bit level per half,
/// toward neighbours at least two levels away, so isolated specks shrink while edges stay. Values are 16-bit counts, a level
/// being 257 of them, so 8 and 16-bit images come out exactly as ImageMagick's Q16 build gives them; alpha is kept.
/// </summary>
internal sealed class DespeckleOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    private const float Step = 257;
    private const float Threshold = 514;

    private static readonly (int X, int Y)[] Directions = [(0, 1), (1, 0), (1, 1), (-1, 1)];

    /// <summary>Gets <see langword="null"/>: the zero frame lies at the image's edge, so a scope runs on the whole image.</summary>
    public (int X, int Y)? Reach => null;

    public bool NeedsNewPlane(Size input) => false;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int width = source.Width, height = source.Height, stride = width + 2, size = stride * (height + 2);
        bool whole = TPixel.Layout.ChannelType is PixelChannelType.Unorm8 or PixelChannelType.Unorm16;
        int channels = TPixel.Layout.HasAlpha || TPixel.Layout.ChannelOrder != PixelChannelOrder.Cmyk ? 3 : 4;
        PixelPlane<RgbaF32> straight = PixelPlane.Allocate<RgbaF32>(width, height, (IPixelMemorySource?)input.Memory ?? input.Context.Memory);
        float[] a = ArrayPool<float>.Shared.Rent(size), b = ArrayPool<float>.Shared.Rent(size);
        Hulls<TPixel> hull = new(input, a, b, width, height);
        try
        {
            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    StoredRows<TPixel>.Load(source.Row(y), System.Runtime.InteropServices.MemoryMarshal.Cast<RgbaF32, float>(straight.Row(y)));
                }
            });

            for (int c = 0; c < channels; c++)
            {
                a.AsSpan(0, size).Clear();
                b.AsSpan(0, size).Clear();
                int channel = c;
                input.ForEachBand((start, end) =>
                {
                    for (int y = start; y < end; y++)
                    {
                        Span<RgbaF32> row = straight.Row(y);
                        Span<float> plane = a.AsSpan(((y + 1) * stride) + 1, width);
                        for (int x = 0; x < width; x++)
                        {
                            float value = Channel(row[x], channel) * 65535f;
                            plane[x] = whole ? MathF.Round(value) : value;
                        }
                    }
                });

                foreach ((int dx, int dy) in Directions)
                {
                    hull.Run(dx, dy, 1);
                    hull.Run(-dx, -dy, 1);
                    hull.Run(-dx, -dy, -1);
                    hull.Run(dx, dy, -1);
                }

                input.ForEachBand((start, end) =>
                {
                    for (int y = start; y < end; y++)
                    {
                        Span<RgbaF32> row = straight.Row(y);
                        ReadOnlySpan<float> plane = a.AsSpan(((y + 1) * stride) + 1, width);
                        for (int x = 0; x < width; x++)
                        {
                            SetChannel(ref row[x], channel, plane[x] / 65535f);
                        }
                    }
                });
            }

            input.ForEachBand((start, end) =>
            {
                for (int y = start; y < end; y++)
                {
                    StoredRows<TPixel>.Store(System.Runtime.InteropServices.MemoryMarshal.Cast<RgbaF32, float>(straight.Row(y)), destination.Row(y));
                }
            });
        }
        finally
        {
            ArrayPool<float>.Shared.Return(a);
            ArrayPool<float>.Shared.Return(b);
            straight.Dispose();
        }
    }

    private static float Channel(RgbaF32 p, int c) => c switch { 0 => p.R, 1 => p.G, 2 => p.B, _ => p.A };

    private static void SetChannel(ref RgbaF32 p, int c, float value)
    {
        switch (c)
        {
            case 0:
                p.R = value;
                break;
            case 1:
                p.G = value;
                break;
            case 2:
                p.B = value;
                break;
            default:
                p.A = value;
                break;
        }
    }

    /// <summary>
    /// The hulling passes over one channel's framed plane <c>f</c>, through scratch <c>g</c> and back. Lightening (polarity 1): a
    /// value rises a level when the neighbour ahead is at least two levels brighter; then it rises again when the neighbour behind
    /// is two levels brighter and the one ahead is brighter at all. Darkening mirrors it. The band actions are made once, so the
    /// 16 passes allocate nothing.
    /// </summary>
    private sealed class Hulls<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OperationInput<TPixel> _input;
        private readonly float[] _f;
        private readonly float[] _g;
        private readonly int _width;
        private readonly int _height;
        private readonly RowBandAction _first;
        private readonly RowBandAction _second;
        private int _offset;
        private int _polarity;

        internal Hulls(OperationInput<TPixel> input, float[] f, float[] g, int width, int height)
        {
            _input = input;
            _f = f;
            _g = g;
            _width = width;
            _height = height;
            _first = FirstBand;
            _second = SecondBand;
        }

        /// <summary>One pass along (<paramref name="dx"/>, <paramref name="dy"/>).</summary>
        internal void Run(int dx, int dy, int polarity)
        {
            _offset = (dy * (_width + 2)) + dx;
            _polarity = polarity;
            RowRunner.Run(_height, (long)_width * 8, _input.Context.MaxParallelism, _first, _input.CancellationToken);
            RowRunner.Run(_height, (long)_width * 8, _input.Context.MaxParallelism, _second, _input.CancellationToken);
        }

        private void FirstBand(int start, int end)
        {
            for (int y = start; y < end; y++)
            {
                int at = ((y + 1) * (_width + 2)) + 1;
                First(_f.AsSpan(at, _width), _f.AsSpan(at + _offset, _width), _g.AsSpan(at, _width), _polarity);
            }
        }

        private void SecondBand(int start, int end)
        {
            for (int y = start; y < end; y++)
            {
                int at = ((y + 1) * (_width + 2)) + 1;
                Second(_g.AsSpan(at, _width), _g.AsSpan(at + _offset, _width), _g.AsSpan(at - _offset, _width), _f.AsSpan(at, _width), _polarity);
            }
        }
    }

    /// <summary>The first half of a pass: a level toward the neighbour ahead when it is two levels away in the pass's direction.</summary>
    private static void First(ReadOnlySpan<float> values, ReadOnlySpan<float> ahead, Span<float> output, int polarity)
    {
        int x = 0;
        float step = polarity * Step, threshold = polarity * Threshold;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> vs = Vector256.Create(step), vt = Vector256.Create(threshold);
            for (; x <= values.Length - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                Vector256<float> v = Vector256.Create(values[x..]), a = Vector256.Create(ahead[x..]);
                Vector256<float> moves = polarity > 0 ? Vector256.GreaterThanOrEqual(a, v + vt) : Vector256.LessThanOrEqual(a, v + vt);
                (v + (moves & vs)).CopyTo(output[x..]);
            }
        }

        for (; x < values.Length; x++)
        {
            float v = values[x], a = ahead[x];
            output[x] = (polarity > 0 ? a >= v + threshold : a <= v + threshold) ? v + step : v;
        }
    }

    /// <summary>The second half: a level more when the neighbour behind is two levels away and the one ahead lies that way too.</summary>
    private static void Second(ReadOnlySpan<float> values, ReadOnlySpan<float> ahead, ReadOnlySpan<float> behind, Span<float> output, int polarity)
    {
        int x = 0;
        float step = polarity * Step, threshold = polarity * Threshold;
        if (Vector256.IsHardwareAccelerated)
        {
            Vector256<float> vs = Vector256.Create(step), vt = Vector256.Create(threshold);
            for (; x <= values.Length - Vector256<float>.Count; x += Vector256<float>.Count)
            {
                Vector256<float> v = Vector256.Create(values[x..]), a = Vector256.Create(ahead[x..]), b = Vector256.Create(behind[x..]);
                Vector256<float> moves = polarity > 0
                    ? Vector256.GreaterThanOrEqual(b, v + vt) & Vector256.GreaterThan(a, v)
                    : Vector256.LessThanOrEqual(b, v + vt) & Vector256.LessThan(a, v);
                (v + (moves & vs)).CopyTo(output[x..]);
            }
        }

        for (; x < values.Length; x++)
        {
            float v = values[x], a = ahead[x], b = behind[x];
            bool moves = polarity > 0 ? b >= v + threshold && a > v : b <= v + threshold && a < v;
            output[x] = moves ? v + step : v;
        }
    }
}
