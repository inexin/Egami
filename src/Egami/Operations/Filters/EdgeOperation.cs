using System.Buffers;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// <see cref="RasterOps.Edges(EdgeOperator, bool)"/>: small fixed kernels on premultiplied float rows, edges repeating the edge
/// pixel. A gradient pair writes sqrt(gx² + gy²), the compass sets their largest response, the Laplacians their absolute value,
/// each times the operator's scale and clamped to 0 to 1; alpha is kept. In gray mode the rows are first reduced to Rec. 709
/// luma, and the magnitude goes to every color channel.
/// </summary>
internal sealed class EdgeOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep
{
    private readonly bool _grayscale;
    private readonly Kernels _kernels;

    internal EdgeOperation(EdgeOperator op, bool grayscale)
    {
        _grayscale = grayscale;
        _kernels = Kernels.For(op);
    }

    public (int X, int Y)? Reach => (_kernels.Radius, _kernels.Radius);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Band<TPixel> band = new(input, _kernels, _grayscale);
        input.ForEachBand(band.Run);
    }

    /// <summary>The kernels of an operator, all square with the output at the center, and how their responses combine.</summary>
    internal sealed class Kernels
    {
        private Kernels(int radius, float scale, Combine combine, float[][] values)
        {
            Radius = radius;
            Scale = scale;
            How = combine;
            Values = values;
        }

        internal enum Combine
        {
            Magnitude,
            Maximum,
            Absolute,
        }

        internal int Radius { get; }

        internal float Scale { get; }

        internal Combine How { get; }

        /// <summary>Gets each kernel's (2r + 1)² weights, row by row.</summary>
        internal float[][] Values { get; }

        internal static Kernels For(EdgeOperator op) => op switch
        {
            // The x kernel (right minus left) and its transpose; a step from 0 to 1 gives the sum of one column.
            EdgeOperator.Sobel => Pair([-1, 0, 1, -2, 0, 2, -1, 0, 1], 1f / 4),
            EdgeOperator.Scharr => Pair([-3, 0, 3, -10, 0, 10, -3, 0, 3], 1f / 16),
            EdgeOperator.Prewitt => Pair([-1, 0, 1, -1, 0, 1, -1, 0, 1], 1f / 3),

            // Roberts: the pixel minus the one below right, and the one right minus the one below; a straight step gives √2.
            EdgeOperator.RobertsCross => new(1, 1 / MathF.Sqrt(2), Combine.Magnitude, [[0, 0, 0, 0, 1, 0, 0, 0, -1], [0, 0, 0, 0, 0, 1, 0, -1, 0]]),

            // Compass kernels: the ring of eight turned one cell at a time. A step gives 3 × 5 for Kirsch, 1 + 2 + 1 for Robinson.
            EdgeOperator.Kirsch => Compass([5, 5, 5, -3, -3, -3, -3, -3], 1f / 15),
            EdgeOperator.Robinson => Compass([1, 2, 1, 0, -1, -2, -1, 0], 1f / 4),

            // Laplacians as absolute values; a step gives 3, 10 and 5 next to it.
            EdgeOperator.Laplacian3x3 => new(1, 1f / 3, Combine.Absolute, [[-1, -1, -1, -1, 8, -1, -1, -1, -1]]),
            EdgeOperator.Laplacian5x5 => new(2, 1f / 10, Combine.Absolute, [[.. Enumerable.Range(0, 25).Select(i => i == 12 ? 24f : -1f)]]),
            EdgeOperator.LaplacianOfGaussian => new(2, 1f / 5, Combine.Absolute, [
                [0, 0, -1, 0, 0, 0, -1, -2, -1, 0, -1, -2, 16, -2, -1, 0, -1, -2, -1, 0, 0, 0, -1, 0, 0]]),
            _ => throw new ArgumentOutOfRangeException(nameof(op), op, "The edge operator is not defined."),
        };

        private static Kernels Pair(float[] x, float scale)
        {
            float[] y = new float[9];
            for (int i = 0; i < 3; i++)
            {
                for (int j = 0; j < 3; j++)
                {
                    y[(i * 3) + j] = x[(j * 3) + i];
                }
            }

            return new(1, scale, Combine.Magnitude, [x, y]);
        }

        /// <summary>Eight kernels from the ring of a 3 × 3 kernel, clockwise from the top-left cell, turned one cell at a time.</summary>
        private static Kernels Compass(float[] ring, float scale)
        {
            ReadOnlySpan<int> cells = [0, 1, 2, 5, 8, 7, 6, 3];
            float[][] kernels = new float[8][];
            for (int k = 0; k < 8; k++)
            {
                kernels[k] = new float[9];
                for (int i = 0; i < 8; i++)
                {
                    kernels[k][cells[(i + k) % 8]] = ring[i];
                }
            }

            return new(1, scale, Combine.Maximum, kernels);
        }
    }

    /// <summary>One call's settings and the band loop; each band rents its own rows, so threads share it.</summary>
    private sealed class Band<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OperationInput<TPixel> _input;
        private readonly Kernels _kernels;
        private readonly RowConverter<TPixel> _converter;
        private readonly bool _gray;
        private readonly int _channels;
        private readonly bool _alpha;
        private readonly int _red;
        private readonly int _blue;

        internal Band(OperationInput<TPixel> input, Kernels kernels, bool grayscale)
        {
            _input = input;
            _kernels = kernels;
            _gray = grayscale;

            // CMYK loads its four inks as numbers, except in gray mode, which needs the color.
            _converter = new RowConverter<TPixel>(null, inksAsRgb: grayscale);
            bool inks = RowConverter<TPixel>.Ink && !grayscale;
            _channels = grayscale ? 1 : 4;
            _alpha = !inks;

            // The byte paths keep the stored channel order, so blue comes first for the BGRA types.
            bool bgr = TPixel.Layout.ChannelOrder == PixelChannelOrder.Bgra && (RowConverter<TPixel>.FourByte || RowConverter<TPixel>.Premul8);
            (_red, _blue) = bgr ? (2, 0) : (0, 2);
        }

        internal void Run(int start, int end)
        {
            int width = _input.Source.Width, r = _kernels.Radius, side = (2 * r) + 1;
            int padded = (width + (2 * r)) * _channels, rowFloats = width * _channels;
            int kernels = _kernels.Values.Length;
            float[] ringBuffer = ArrayPool<float>.Shared.Rent(side * padded);
            float[] alphaBuffer = ArrayPool<float>.Shared.Rent(side * width);
            float[] lineBuffer = ArrayPool<float>.Shared.Rent(width * 4);
            float[] sumBuffer = ArrayPool<float>.Shared.Rent(2 * rowFloats);
            bool[] opaque = ArrayPool<bool>.Shared.Rent(side);
            try
            {
                Span<float> ring = ringBuffer.AsSpan(0, side * padded), line = lineBuffer.AsSpan(0, width * 4);
                Span<float> result = sumBuffer.AsSpan(0, rowFloats), sum = sumBuffer.AsSpan(rowFloats, rowFloats);
                for (int v = start - r; v < start + r; v++)
                {
                    Load(v, side, padded, ring, alphaBuffer, opaque, line);
                }

                for (int y = start; y < end; y++)
                {
                    Load(y + r, side, padded, ring, alphaBuffer, opaque, line);
                    for (int k = 0; k < kernels; k++)
                    {
                        Correlate(_kernels.Values[k], ring, y, side, padded, k == 0 ? result : sum);
                        if (k > 0)
                        {
                            Merge(result, sum, _kernels.How == Kernels.Combine.Maximum);
                        }
                        else if (_kernels.How == Kernels.Combine.Magnitude)
                        {
                            Square(result);
                        }
                    }

                    int slot = Slot(y, side);
                    Finish(result, alphaBuffer.AsSpan(slot * width, width), line);
                    _converter.Store(line, _input.Destination.Row(y), opaque[slot] || !_alpha);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(ringBuffer);
                ArrayPool<float>.Shared.Return(alphaBuffer);
                ArrayPool<float>.Shared.Return(lineBuffer);
                ArrayPool<float>.Shared.Return(sumBuffer);
                ArrayPool<bool>.Shared.Return(opaque);
            }
        }

        private static int Slot(int v, int side) => ((v % side) + side) % side;

        /// <summary>Loads virtual row <paramref name="v"/> into its ring slot (gray or four floats a pixel), padded by repeating the edge pixels.</summary>
        private void Load(int v, int side, int padded, Span<float> ring, float[] alphas, bool[] opaque, Span<float> line)
        {
            PixelPlane<TPixel> source = _input.Source;
            int width = source.Width, r = _kernels.Radius, slot = Slot(v, side);
            opaque[slot] = _converter.Load(source.Row(Math.Clamp(v, 0, source.Height - 1)), line);
            Span<float> target = ring.Slice(slot * padded, padded);
            Span<float> alpha = alphas.AsSpan(slot * width, width);
            int c = _channels;
            if (_gray)
            {
                Span<float> middle = target.Slice(r, width);
                for (int x = 0; x < width; x++)
                {
                    middle[x] = (0.2126f * line[(x * 4) + _red]) + (0.7152f * line[(x * 4) + 1]) + (0.0722f * line[(x * 4) + _blue]);
                    alpha[x] = line[(x * 4) + 3];
                }
            }
            else
            {
                line.CopyTo(target.Slice(r * 4, width * 4));
                for (int x = 0; x < width; x++)
                {
                    alpha[x] = line[(x * 4) + 3];
                }
            }

            for (int i = 0; i < r; i++)
            {
                target.Slice(r * c, c).CopyTo(target.Slice(i * c, c));
                target.Slice((r + width - 1) * c, c).CopyTo(target.Slice((r + width + i) * c, c));
            }
        }

        /// <summary>Correlates the ring rows around <paramref name="y"/> with one kernel, tap by tap.</summary>
        private void Correlate(float[] kernel, Span<float> ring, int y, int side, int padded, Span<float> output)
        {
            int r = _kernels.Radius, c = _channels;
            bool first = true;
            for (int ky = 0; ky < side; ky++)
            {
                ReadOnlySpan<float> row = ring.Slice(Slot(y - r + ky, side) * padded, padded);
                for (int kx = 0; kx < side; kx++)
                {
                    float w = kernel[(ky * side) + kx];
                    if (w != 0)
                    {
                        ConvolutionKernels.Accumulate(row.Slice(kx * c, output.Length), w, output, first);
                        first = false;
                    }
                }
            }
        }

        /// <summary>Squares the first kernel's response, for a magnitude.</summary>
        private static void Square(Span<float> values)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float v = ref MemoryMarshal.GetReference(values);
                for (; i <= values.Length - Vector256<float>.Count; i += Vector256<float>.Count)
                {
                    Vector256<float> a = Vector256.LoadUnsafe(ref v, (nuint)i);
                    (a * a).StoreUnsafe(ref v, (nuint)i);
                }
            }

            for (; i < values.Length; i++)
            {
                values[i] *= values[i];
            }
        }

        /// <summary>Adds the square of a further response (a magnitude) or keeps the larger (the compass).</summary>
        private static void Merge(Span<float> result, ReadOnlySpan<float> next, bool maximum)
        {
            int i = 0;
            if (Vector256.IsHardwareAccelerated)
            {
                ref float a = ref MemoryMarshal.GetReference(result);
                ref float b = ref MemoryMarshal.GetReference(next);
                for (; i <= result.Length - Vector256<float>.Count; i += Vector256<float>.Count)
                {
                    Vector256<float> x = Vector256.LoadUnsafe(ref a, (nuint)i), n = Vector256.LoadUnsafe(ref b, (nuint)i);
                    (maximum ? Vector256.Max(x, n) : x + (n * n)).StoreUnsafe(ref a, (nuint)i);
                }
            }

            for (; i < result.Length; i++)
            {
                result[i] = maximum ? Math.Max(result[i], next[i]) : result[i] + (next[i] * next[i]);
            }
        }

        /// <summary>Turns the responses into clamped magnitudes and writes premultiplied pixels with the source's alpha.</summary>
        private void Finish(Span<float> result, ReadOnlySpan<float> alpha, Span<float> line)
        {
            float scale = _kernels.Scale;
            Kernels.Combine how = _kernels.How;
            for (int i = 0; i < result.Length; i++)
            {
                float m = how switch
                {
                    Kernels.Combine.Magnitude => MathF.Sqrt(result[i]),
                    Kernels.Combine.Absolute => Math.Abs(result[i]),
                    _ => result[i],
                };
                result[i] = Math.Clamp(m * scale, 0, 1);
            }

            int width = alpha.Length;
            if (_gray)
            {
                for (int x = 0; x < width; x++)
                {
                    float a = alpha[x], m = result[x] * a;
                    line[x * 4] = m;
                    line[(x * 4) + 1] = m;
                    line[(x * 4) + 2] = m;
                    line[(x * 4) + 3] = a;
                }
            }
            else if (_alpha)
            {
                for (int x = 0; x < width; x++)
                {
                    float a = alpha[x];
                    line[x * 4] = result[x * 4] * a;
                    line[(x * 4) + 1] = result[(x * 4) + 1] * a;
                    line[(x * 4) + 2] = result[(x * 4) + 2] * a;
                    line[(x * 4) + 3] = a;
                }
            }
            else
            {
                result.CopyTo(line);
            }
        }
    }
}
