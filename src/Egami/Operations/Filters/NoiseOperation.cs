using System.Buffers;
using System.Runtime.CompilerServices;

using Egami.Operations.Geometry;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Adds noise to the color channels of straight values, never alpha. Every random draw is a hash of (seed, frame, x, y, channel,
/// draw) through SplitMix64's mixer (Steele, Lea and Flood, 2014), so bands and threads share no state and the pixels do not
/// depend on the thread count; inside a scope the coordinates stay the raster's, so the noise in an area is the noise the whole
/// image would get there.
/// </summary>
internal sealed class NoiseOperation : IRasterOperation, INeedsNewPlane, Scopes.IAreaStep
{
    private readonly NoiseKind _kind;
    private readonly double _amount;
    private readonly bool _monochrome;
    private readonly ulong _seed;

    internal NoiseOperation(NoiseKind kind, float amount, bool monochrome, ulong seed)
    {
        _kind = kind;
        _amount = amount;
        _monochrome = monochrome;
        _seed = seed;
    }

    public bool NeedsNewPlane(Size input) => false;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rows(input, new Rect(0, 0, input.Source.Width, input.Source.Height));
    }

    public void RunArea<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Rows(input, area.Intersect(new Rect(0, 0, input.Destination.Width, input.Destination.Height)));
    }

    private void Rows<TPixel>(OperationInput<TPixel> input, Rect area)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        if (area.IsEmpty)
        {
            return;
        }

        bool alpha = TPixel.Layout.HasAlpha;
        int channels = alpha || TPixel.Layout.ChannelOrder != PixelChannelOrder.Cmyk ? 3 : 4;
        ulong frame = Mix(_seed + (0x9E3779B97F4A7C15UL * (ulong)(input.FrameIndex + 1)));
        RowRunner.Run(area.Height, (long)area.Width * Unsafe.SizeOf<TPixel>(), input.Context.MaxParallelism, (start, end) =>
        {
            float[] buffer = ArrayPool<float>.Shared.Rent(area.Width * 4);
            try
            {
                Span<float> values = buffer.AsSpan(0, area.Width * 4);
                for (int i = start; i < end; i++)
                {
                    int y = area.Y + i;
                    StoredRows<TPixel>.Load(input.Source.Row(y).Slice(area.X, area.Width), values);
                    ulong row = Mix(frame ^ (uint)y);
                    for (int j = 0; j < area.Width; j++)
                    {
                        ulong pixel = Mix(row ^ (uint)(area.X + j));
                        for (int c = 0; c < channels; c++)
                        {
                            ref float v = ref values[(j * 4) + c];
                            v = (float)Noise(v, Mix(pixel ^ (ulong)(_monochrome ? 0 : c + 1)));
                        }
                    }

                    StoredRows<TPixel>.Store(values, input.Destination.Row(y).Slice(area.X, area.Width));
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(buffer);
            }
        }, input.CancellationToken);
    }

    /// <summary>One noisy value from <paramref name="v"/>; <paramref name="key"/> seeds this value's draws.</summary>
    internal double Noise(double v, ulong key)
    {
        double a = _amount;
        switch (_kind)
        {
            case NoiseKind.Uniform:
                return v + (a / 64 * (Uniform(key, 0) - 0.5));
            case NoiseKind.Gaussian:
            {
                // Box–Muller: one radius, its cosine and sine two independent normals, as ImageMagick draws them.
                (double s, double t) = Normals(key);
                return v + (a / 64 * s * Math.Sqrt(Math.Max(v, 0) / 65536)) + (5 * a / 64 * t);
            }

            case NoiseKind.Impulse:
            {
                double alpha = Uniform(key, 0);
                return alpha < 0.05 * a ? 0 : alpha >= 1 - (0.05 * a) ? 1 : v;
            }

            case NoiseKind.Laplacian:
            {
                double alpha = Uniform(key, 0), scale = 5 * a / 128;
                if (alpha <= 0.5)
                {
                    return alpha <= 0 ? v - 1 : v + (scale * Math.Log(2 * alpha));
                }

                double beta = 1 - alpha;
                return v - (scale * Math.Log(2 * beta));
            }

            case NoiseKind.Multiplicative:
                return v * (1 + (a / 4 * Normals(key).Cos));
            default:
            {
                // Knuth's multiplication method: count uniforms until their product drops below e^−λ.
                double lambda = 12.5 * a * Math.Max(v, 0), limit = Math.Exp(-lambda), product = Uniform(key, 0);
                int count = 0;
                while (product > limit)
                {
                    count++;
                    product *= Uniform(key, count);
                }

                return count / (12.5 * a);
            }
        }
    }

    private static (double Cos, double Sin) Normals(ulong key)
    {
        double alpha = Uniform(key, 0), beta = Uniform(key, 1);
        double gamma = Math.Sqrt(-2 * Math.Log(alpha <= 0 ? 1 : alpha));
        (double sin, double cos) = Math.SinCos(2 * Math.PI * beta);
        return (gamma * cos, gamma * sin);
    }

    /// <summary>Draw <paramref name="index"/> for a key, uniform on [0, 1) from 53 bits.</summary>
    private static double Uniform(ulong key, int index) => (Mix(key + (0x9E3779B97F4A7C15UL * (ulong)(index + 1))) >> 11) * (1.0 / (1UL << 53));

    /// <summary>SplitMix64's output function: a bijective mix of 64 bits.</summary>
    internal static ulong Mix(ulong z)
    {
        z = (z ^ (z >> 30)) * 0xBF58476D1CE4E5B9UL;
        z = (z ^ (z >> 27)) * 0x94D049BB133111EBUL;
        return z ^ (z >> 31);
    }
}
