using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>
/// Rotates by any angle onto a canvas the size of the rotated bounding box. Each output pixel maps back to a source point and is
/// sampled with the filter's 2D kernel (the product of the 1D kernel in x and y, unstretched, since rotation keeps scale).
/// Sampling is premultiplied, and taps that fall outside the source read the background, so the image edges are antialiased.
/// </summary>
internal sealed class RotateOperation(double degrees, ResampleFilter filter, Rgba8 background) : IRasterOperation, INeedsNewPlane, IUpdatesMetadata
{
    public void UpdateMetadata(Metadata.RasterMetadata metadata, Size input, Size output)
    {
        ExifUpkeep.Upright(metadata);
        ExifUpkeep.Dimensions(metadata, output);
    }

    /// <summary>
    /// Kernel weights are tabulated at this many positions per pixel. Rounding a sample position to 1/1024 pixel moves it by at most
    /// 1/2048, far below what 8 bits can show.
    /// </summary>
    private const int Steps = 1024;

    private readonly double _cos = Math.Cos(degrees * Math.PI / 180);
    private readonly double _sin = Math.Sin(degrees * Math.PI / 180);

    public bool NeedsNewPlane(Size input)
    {
        return true;
    }

    public Size OutputSize(Size input)
    {
        // The bounding box is rounded to the nearest pixel, so a tiny angle keeps the size instead of growing a pixel.
        double width = (input.Width * Math.Abs(_cos)) + (input.Height * Math.Abs(_sin));
        double height = (input.Width * Math.Abs(_sin)) + (input.Height * Math.Abs(_cos));
        return new Size(Math.Max(1, (int)Math.Round(width, MidpointRounding.AwayFromZero)), Math.Max(1, (int)Math.Round(height, MidpointRounding.AwayFromZero)));
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        bool hasAlpha = TPixel.Layout.Alpha != AlphaKind.None;
        Vector4 back = Fetch(TPixel.FromRgba8(background), hasAlpha);
        RowConverter<TPixel> converter = new(linear: false);
        int reach = (int)Math.Ceiling(ResampleKernels.Support(filter));
        int taps = filter == ResampleFilter.Nearest ? 1 : 2 * reach;
        float[] table = ArrayPool<float>.Shared.Rent((Steps + 1) * taps);
        BuildTable(table, taps, reach);
        try
        {
            RunBands(input, converter, table, taps, reach, back, hasAlpha);
        }
        finally
        {
            ArrayPool<float>.Shared.Return(table);
        }
    }

    private void RunBands<TPixel>(OperationInput<TPixel> input, RowConverter<TPixel> converter, float[] table, int taps, int reach, Vector4 back, bool hasAlpha)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source;
        PixelPlane<TPixel> destination = input.Destination;

        // Output pixel centers, relative to the output center, rotate back onto source positions relative to the source center.
        // Pixel i covers i..i+1, so its center is at i + 0.5; subtracting 0.5 gives positions where pixel i's center is i.
        double ocx = destination.Width / 2.0;
        double ocy = destination.Height / 2.0;
        double scx = (source.Width / 2.0) - 0.5;
        double scy = (source.Height / 2.0) - 0.5;
        input.ForEachBand((start, end) =>
        {
            float[] rowBuffer = ArrayPool<float>.Shared.Rent(destination.Width * 4);
            try
            {
                Sampler<TPixel> sampler = new(source, table, taps, reach, back, hasAlpha);
                Span<float> row = rowBuffer.AsSpan(0, destination.Width * 4);
                for (int y = start; y < end; y++)
                {
                    double dy = y + 0.5 - ocy;
                    for (int x = 0; x < destination.Width; x++)
                    {
                        double dx = x + 0.5 - ocx;
                        double u = scx + (_cos * dx) + (_sin * dy);
                        double v = scy - (_sin * dx) + (_cos * dy);
                        Vector4 sum = taps == 1 ? sampler.Nearest(u, v) : sampler.Sample(u, v);
                        sum.CopyTo(row[(x * 4)..]);
                    }

                    converter.Store(row, destination.Row(y), opaque: false);
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(rowBuffer);
            }
        });
    }

    /// <summary>
    /// Tabulates the normalized 1D weights: row s holds the <paramref name="taps"/> weights for a sample at fraction s / Steps past a
    /// pixel center, for the taps starting <paramref name="reach"/> − 1 pixels to its left.
    /// </summary>
    private void BuildTable(float[] table, int taps, int reach)
    {
        if (filter == ResampleFilter.Nearest)
        {
            return;
        }

        Span<double> w = stackalloc double[taps];
        for (int s = 0; s <= Steps; s++)
        {
            double fraction = s / (double)Steps;
            double total = 0;
            for (int k = 0; k < taps; k++)
            {
                w[k] = ResampleKernels.Evaluate(filter, k - reach + 1 - fraction);
                total += w[k];
            }

            for (int k = 0; k < taps; k++)
            {
                table[(s * taps) + k] = (float)(w[k] / total);
            }
        }
    }

    /// <summary>
    /// Returns a pixel premultiplied, in the channel order the row converter stores: memory order for Rgba8 and Bgra8, which are
    /// read straight from their bytes, and RGBA for every other type.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector4 Fetch<TPixel>(in TPixel pixel, bool hasAlpha)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Vector4 p;
        if (RowConverter<TPixel>.Ink)
        {
            Span<float> ink = stackalloc float[4];
            CmykInk.Load(new ReadOnlySpan<TPixel>(in pixel), ink);
            return new Vector4(ink[0], ink[1], ink[2], ink[3]);
        }

        if (typeof(TPixel) == typeof(Rgba8) || typeof(TPixel) == typeof(Bgra8))
        {
            Vector128<byte> bytes = Vector128.CreateScalarUnsafe(Unsafe.As<TPixel, uint>(ref Unsafe.AsRef(in pixel))).AsByte();
            Vector128<uint> wide = Vector128.WidenLower(Vector128.WidenLower(bytes));
            p = (Vector128.ConvertToSingle(wide.AsInt32()) * (1f / 255f)).AsVector4();
        }
        else
        {
            p = pixel.ToScaled();
        }

        return hasAlpha ? p * new Vector4(p.W, p.W, p.W, 1f) : p;
    }

    /// <summary>Reads premultiplied source pixels for one band, through one span when the plane is contiguous.</summary>
    private readonly ref struct Sampler<TPixel>
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        private readonly PixelPlane<TPixel> _plane;
        private readonly ReadOnlySpan<TPixel> _all;
        private readonly ReadOnlySpan<float> _table;
        private readonly int _taps;
        private readonly int _reach;
        private readonly Vector4 _background;
        private readonly bool _hasAlpha;
        private readonly int _width;
        private readonly int _height;

        internal Sampler(PixelPlane<TPixel> plane, float[] table, int taps, int reach, Vector4 background, bool hasAlpha)
        {
            _plane = plane;
            _all = plane.TryGetContiguous(out Span<TPixel> all) ? all : default;
            _table = table;
            _taps = taps;
            _reach = reach;
            _background = background;
            _hasAlpha = hasAlpha;
            _width = plane.Width;
            _height = plane.Height;
        }

        internal Vector4 Nearest(double u, double v)
        {
            int x = (int)Math.Floor(u + 0.5);
            int y = (int)Math.Floor(v + 0.5);
            return (uint)x < (uint)_width && (uint)y < (uint)_height ? Fetch(Row(y)[x], _hasAlpha) : _background;
        }

        internal Vector4 Sample(double u, double v)
        {
            double fu = Math.Floor(u);
            double fv = Math.Floor(v);
            int x0 = (int)fu - _reach + 1;
            int y0 = (int)fv - _reach + 1;
            if (x0 + _taps <= 0 || y0 + _taps <= 0 || x0 >= _width || y0 >= _height)
            {
                return _background;
            }

            ReadOnlySpan<float> wx = _table.Slice((int)(((u - fu) * Steps) + 0.5) * _taps, _taps);
            ReadOnlySpan<float> wy = _table.Slice((int)(((v - fv) * Steps) + 0.5) * _taps, _taps);
            bool insideX = x0 >= 0 && x0 + _taps <= _width;
            Vector4 sum = Vector4.Zero;
            for (int j = 0; j < wy.Length; j++)
            {
                int sy = y0 + j;
                Vector4 line = Vector4.Zero;
                if ((uint)sy >= (uint)_height)
                {
                    line = _background;
                }
                else if (insideX)
                {
                    ReadOnlySpan<TPixel> pixels = Row(sy).Slice(x0, _taps);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        line += Fetch(pixels[i], _hasAlpha) * wx[i];
                    }
                }
                else
                {
                    ReadOnlySpan<TPixel> pixels = Row(sy);
                    for (int i = 0; i < wx.Length; i++)
                    {
                        int sx = x0 + i;
                        Vector4 p = (uint)sx < (uint)pixels.Length ? Fetch(pixels[sx], _hasAlpha) : _background;
                        line += p * wx[i];
                    }
                }

                sum += line * wy[j];
            }

            return sum;
        }

        private ReadOnlySpan<TPixel> Row(int y)
        {
            return _all.IsEmpty ? _plane.Row(y) : _all.Slice(y * _width, _width);
        }
    }
}
