using System.Buffers;
using System.Runtime.InteropServices;

using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// Convolves with an aperture (an antialiased disc or regular polygon) by FFT on overlapping tiles (overlap-save): each N × N
/// tile is transformed, multiplied by the aperture's spectrum and transformed back, and its middle, N − 2R square, is kept.
/// Two channels share each complex transform, red with green and blue with alpha. Edges repeat the edge pixel.
/// </summary>
internal sealed class LensBlurOperation : IRasterOperation, INeedsNewPlane, ILinearLight, Scopes.INeighbourhoodStep
{
    /// <summary>The largest radius: its tiles are 1024 values on a side, 16 MB per thread.</summary>
    internal const float MaxRadius = 128;

    // The last apertures' spectra, so a chain run on many images transforms its aperture once.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(float, int, float), Kernel> Kernels = new();

    private readonly float _radius;
    private readonly LensBlurOptions _options;
    private readonly Colors.LinearCurve? _curve;
    private readonly Kernel? _fixed;

    internal LensBlurOperation(float radius, LensBlurOptions options, Colors.LinearCurve? curve = null)
    {
        _radius = radius;
        _options = options;
        _curve = curve;
    }

    /// <summary>Convolves with <paramref name="cells"/> as given (rows from −R), for tests of the tiles against direct convolution.</summary>
    internal LensBlurOperation(float[,] cells, LensBlurOptions options)
        : this(cells.GetLength(0) / 2, options)
    {
        _fixed = new Kernel(cells);
    }

    public bool UsesLinearLight => _options.ColorSpace == ColorSpace.Linear;

    /// <summary>Gets <see langword="null"/>: tiles are laid out from the image's corner, so a scope runs the blur on the whole image.</summary>
    public (int X, int Y)? Reach => null;

    public IRasterOperation WithCurve(Colors.LinearCurve curve) => _fixed is null ? new LensBlurOperation(_radius, _options, curve) : this;

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    /// <summary>
    /// The aperture: a disc (0 blades) or a regular polygon of <paramref name="blades"/> corners with circumradius
    /// <paramref name="radius"/>, each cell weighted by how much of it lies inside, normalized to sum to 1. Rows are y from −R.
    /// </summary>
    internal static float[,] Aperture(float radius, int blades, float rotation)
    {
        int r = (int)Math.Ceiling(radius);
        float[,] cells = new float[(2 * r) + 1, (2 * r) + 1];
        double apothem = blades == 0 ? radius : radius * Math.Cos(Math.PI / blades);
        double sum = 0;
        for (int y = -r; y <= r; y++)
        {
            for (int x = -r; x <= r; x++)
            {
                // The signed distance inside the shape; a cell is covered by how far its center lies inside, over one pixel.
                double inside;
                if (blades == 0)
                {
                    inside = radius - Math.Sqrt((x * x) + (y * y));
                }
                else
                {
                    inside = double.MaxValue;
                    for (int b = 0; b < blades; b++)
                    {
                        // Edge b's outward normal points between corners b and b + 1; corner 0 points up at rotation 0.
                        double angle = ((rotation * Math.PI) / 180) + (((b + 0.5) * 2 * Math.PI) / blades);
                        double nx = Math.Sin(angle), ny = -Math.Cos(angle);
                        inside = Math.Min(inside, apothem - ((x * nx) + (y * ny)));
                    }
                }

                double cover = Math.Clamp(inside + 0.5, 0, 1);
                cells[y + r, x + r] = (float)cover;
                sum += cover;
            }
        }

        for (int y = 0; y <= 2 * r; y++)
        {
            for (int x = 0; x <= 2 * r; x++)
            {
                cells[y, x] = (float)(cells[y, x] / sum);
            }
        }

        return cells;
    }

    /// <summary>The tile size for an aperture of radius <paramref name="r"/>: at least two apertures across, and at least 512.</summary>
    internal static int TileSize(int r) => Math.Max(512, (int)System.Numerics.BitOperations.RoundUpToPowerOf2((uint)(2 * ((2 * r) + 1))));

    /// <summary>Returns the aperture's spectrum, from a small cache.</summary>
    internal static Kernel SpectrumOf(float radius, int blades, float rotation)
    {
        if (Kernels.TryGetValue((radius, blades, rotation), out Kernel? kernel))
        {
            return kernel;
        }

        if (Kernels.Count >= 8)
        {
            Kernels.Clear();
        }

        return Kernels.GetOrAdd((radius, blades, rotation), k => new Kernel(Aperture(k.Item1, k.Item2, k.Item3)));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Kernel kernel = _fixed ?? SpectrumOf(_radius, _options.Blades, _options.Rotation);
        RowConverter<TPixel> converter = new(UsesLinearLight ? _curve ?? Colors.LinearCurve.Srgb : null);
        int width = input.Source.Width, height = input.Source.Height, step = kernel.Size - (2 * kernel.Radius);
        int across = (width + step - 1) / step, down = (height + step - 1) / step;
        Chunks.Run(across * down, (long)width * height * 64, input.Context.MaxParallelism, (start, end) =>
        {
            using Tile<TPixel> tile = new(input, converter, kernel, _options);
            for (int t = start; t < end; t++)
            {
                input.CancellationToken.ThrowIfCancellationRequested();
                tile.Run(t % across * step, t / across * step);
            }
        }, input.CancellationToken);
    }

    /// <summary>The aperture's spectrum on the tile grid, scaled by 1 / N² so the inverse transform comes out right.</summary>
    internal sealed class Kernel
    {
        internal Kernel(float[,] cells)
        {
            Radius = cells.GetLength(0) / 2;
            Size = TileSize(Radius);
            int n = Size;
            Real = new float[n * n];
            Imaginary = new float[n * n];
            float scale = 1f / ((float)n * n);
            for (int y = -Radius; y <= Radius; y++)
            {
                for (int x = -Radius; x <= Radius; x++)
                {
                    // Tap (x, y) sits at (x mod N, y mod N), so a point becomes the aperture centered on it.
                    Real[(((y + n) % n) * n) + ((x + n) % n)] = cells[y + Radius, x + Radius] * scale;
                }
            }

            Fft.For(n).Transform2D(Real, Imaginary, inverse: false);
        }

        internal int Radius { get; }

        internal int Size { get; }

        internal float[] Real { get; }

        internal float[] Imaginary { get; }
    }

    /// <summary>One thread's tile buffers.</summary>
    private sealed class Tile<TPixel> : IDisposable
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly OperationInput<TPixel> _input;
        private readonly RowConverter<TPixel> _converter;
        private readonly Kernel _kernel;
        private readonly LensBlurOptions _options;
        private readonly float[][] _planes;
        private readonly float[] _row;

        internal Tile(OperationInput<TPixel> input, RowConverter<TPixel> converter, Kernel kernel, LensBlurOptions options)
        {
            _input = input;
            _converter = converter;
            _kernel = kernel;
            _options = options;
            int n = kernel.Size;
            _planes = new float[4][];
            for (int i = 0; i < 4; i++)
            {
                _planes[i] = ArrayPool<float>.Shared.Rent(n * n);
            }

            _row = ArrayPool<float>.Shared.Rent(n * 4);
        }

        /// <summary>Blurs the output square whose top-left corner is (<paramref name="x0"/>, <paramref name="y0"/>).</summary>
        internal void Run(int x0, int y0)
        {
            PixelPlane<TPixel> source = _input.Source;
            int n = _kernel.Size, r = _kernel.Radius, width = source.Width, height = source.Height;
            Span<float> re0 = _planes[0].AsSpan(0, n * n), im0 = _planes[1].AsSpan(0, n * n);
            Span<float> re1 = _planes[2].AsSpan(0, n * n), im1 = _planes[3].AsSpan(0, n * n);
            Span<float> row = _row.AsSpan(0, n * 4);
            bool opaque = true;
            float gain = _options.HighlightGain, threshold = _options.HighlightThreshold;

            // The tile covers (x0 − R, y0 − R) to N past it; past the image, edges repeat.
            int left = x0 - r, first = Math.Max(left, 0), last = Math.Min(left + n, width);
            for (int ty = 0; ty < n; ty++)
            {
                int y = Math.Clamp(y0 - r + ty, 0, height - 1);
                Span<float> loaded = row[((first - left) * 4)..];
                opaque &= _converter.Load(source.Row(y)[first..last], loaded);
                for (int tx = 0; tx < first - left; tx++)
                {
                    row.Slice((first - left) * 4, 4).CopyTo(row[(tx * 4)..]);
                }

                for (int tx = last - left; tx < n; tx++)
                {
                    row.Slice((last - left - 1) * 4, 4).CopyTo(row[(tx * 4)..]);
                }

                int at = ty * n;
                for (int tx = 0; tx < n; tx++)
                {
                    re0[at + tx] = Lift(row[tx * 4], gain, threshold);
                    im0[at + tx] = Lift(row[(tx * 4) + 1], gain, threshold);
                    re1[at + tx] = Lift(row[(tx * 4) + 2], gain, threshold);
                    im1[at + tx] = row[(tx * 4) + 3];
                }
            }

            Fft fft = Fft.For(n);
            fft.Transform2D(re0, im0, inverse: false);
            fft.Transform2D(re1, im1, inverse: false);
            Multiply(re0, im0, _kernel.Real, _kernel.Imaginary);
            Multiply(re1, im1, _kernel.Real, _kernel.Imaginary);
            fft.Transform2D(re0, im0, inverse: true);
            fft.Transform2D(re1, im1, inverse: true);

            int step = n - (2 * r), count = Math.Min(step, width - x0), rows = Math.Min(step, height - y0);
            for (int ty = 0; ty < rows; ty++)
            {
                int at = ((ty + r) * n) + r;
                Span<float> output = row[..(count * 4)];
                for (int tx = 0; tx < count; tx++)
                {
                    output[tx * 4] = re0[at + tx];
                    output[(tx * 4) + 1] = im0[at + tx];
                    output[(tx * 4) + 2] = re1[at + tx];
                    output[(tx * 4) + 3] = im1[at + tx];
                }

                _converter.Store(output, _input.Destination.Row(y0 + ty).Slice(x0, count), opaque);
            }
        }

        public void Dispose()
        {
            foreach (float[] plane in _planes)
            {
                ArrayPool<float>.Shared.Return(plane);
            }

            ArrayPool<float>.Shared.Return(_row);
        }

        private static float Lift(float value, float gain, float threshold)
        {
            return value > threshold ? threshold + ((value - threshold) * gain) : value;
        }

        private static void Multiply(Span<float> re, Span<float> im, ReadOnlySpan<float> kr, ReadOnlySpan<float> ki)
        {
            Span<System.Numerics.Vector<float>> vr = MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(re);
            Span<System.Numerics.Vector<float>> vi = MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(im);
            ReadOnlySpan<System.Numerics.Vector<float>> wr = MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(kr);
            ReadOnlySpan<System.Numerics.Vector<float>> wi = MemoryMarshal.Cast<float, System.Numerics.Vector<float>>(ki);
            for (int i = 0; i < vr.Length; i++)
            {
                System.Numerics.Vector<float> a = vr[i], b = vi[i];
                vr[i] = (a * wr[i]) - (b * wi[i]);
                vi[i] = (a * wi[i]) + (b * wr[i]);
            }

            for (int i = vr.Length * System.Numerics.Vector<float>.Count; i < re.Length; i++)
            {
                float a = re[i], b = im[i];
                re[i] = (a * kr[i]) - (b * ki[i]);
                im[i] = (a * ki[i]) + (b * kr[i]);
            }
        }
    }
}
