using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;

using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Geometry;

/// <summary>
/// The sampling settings a transform or warp shares across bands: the filter's tables, how taps outside the source read, the
/// background in the working form, and the curve for linear light.
/// </summary>
internal sealed class SamplerSettings
{
    /// <summary>The plain kernel is tabulated at this many positions per pixel, as Rotate's is.</summary>
    internal const int KernelSteps = 1024;

    /// <summary>The radial EWA table has this many entries over Q (the squared, normalized radius) from 0 to 1.</summary>
    internal const int RadialSteps = 1024;

    /// <summary>
    /// The longest semi-axis an EWA footprint may have, in source pixels. A plane stretching to the horizon costs at most about
    /// π × 32² ≈ 3,200 taps per pixel; beyond the cap, the far region aliases slightly.
    /// </summary>
    internal const double MaxRadius = 32;

    private static readonly float[]?[] KernelTables = new float[]?[Enum.GetValues<ResampleFilter>().Length];
    private static readonly float[]?[] RadialTables = new float[]?[Enum.GetValues<ResampleFilter>().Length];

    internal SamplerSettings(ResampleFilter filter, EdgeMode edges, Rgba8 background, Colors.LinearCurve? curve)
    {
        Filter = filter;
        Edges = edges;
        Background = background;
        Curve = curve;
        Support = ResampleKernels.Support(filter);
        Reach = (int)Math.Ceiling(Support);
        Taps = filter == ResampleFilter.Nearest ? 1 : 2 * Reach;
        Kernel = filter == ResampleFilter.Nearest ? [] : KernelTable(filter, Taps, Reach);
        Radial = filter == ResampleFilter.Nearest ? [] : RadialTable(filter, Support);

        // The radial support never drops below one source pixel, so every footprint holds at least one pixel center (Box's
        // half-pixel support would otherwise miss them all between centers).
        MinScale = Math.Max(1, 1 / Math.Max(Support, 1e-9));
        MaxScale = MaxRadius / Math.Max(Support, 1e-9);
    }

    internal ResampleFilter Filter { get; }

    internal EdgeMode Edges { get; }

    internal Rgba8 Background { get; }

    internal Colors.LinearCurve? Curve { get; }

    /// <summary>Gets the filter's support at scale 1, in source pixels.</summary>
    internal double Support { get; }

    /// <summary>Gets the whole pixels the plain kernel reaches on each side.</summary>
    internal int Reach { get; }

    /// <summary>Gets the plain kernel's taps per axis: 1 for Nearest.</summary>
    internal int Taps { get; }

    /// <summary>Gets the plain kernel's normalized 1D weights: row s holds the taps for a sample s / KernelSteps past a center.</summary>
    internal float[] Kernel { get; }

    /// <summary>Gets the radial weights, indexed by Q × RadialSteps for Q, the squared radius over the squared support, below 1.</summary>
    internal float[] Radial { get; }

    /// <summary>Gets the smallest footprint scale (singular value) EWA uses.</summary>
    internal double MinScale { get; }

    /// <summary>Gets the largest footprint scale EWA uses: the cap divided by the support.</summary>
    internal double MaxScale { get; }

    /// <summary>
    /// Returns the footprint of a source-space Jacobian: whether it needs EWA and, if so, its ellipse. The Jacobian's columns are
    /// how far the source point moves for one output pixel in x and in y.
    /// </summary>
    internal Footprint FootprintOf(double dudx, double dudy, double dvdx, double dvdy)
    {
        // J Jᵀ's eigenvalues are the squared singular values; its eigenvectors are the ellipse's axes in source space.
        double a = (dudx * dudx) + (dudy * dudy);
        double b = (dudx * dvdx) + (dudy * dvdy);
        double c = (dvdx * dvdx) + (dvdy * dvdy);
        double mean = (a + c) / 2;
        double spread = Math.Sqrt((((a - c) / 2) * ((a - c) / 2)) + (b * b));
        double major = mean + spread;
        double minor = Math.Max(0, mean - spread);
        if (!double.IsFinite(major))
        {
            // A fold or tear (or a point at infinity): the largest footprint the cap allows.
            return Footprint.Ewa(MaxScale, MaxScale, 1, 0, Support);
        }

        // Nothing shrinks: the plain kernel, exactly as Rotate samples. The tolerance absorbs a float rotation's rounding.
        if (major <= 1 + 1e-6 || Filter == ResampleFilter.Nearest)
        {
            return Footprint.Plain;
        }

        // The major axis's direction: an eigenvector of [[a, b], [b, c]] for the larger eigenvalue.
        double ex, ey;
        if (spread == 0)
        {
            (ex, ey) = (1, 0);
        }
        else if (a >= c)
        {
            (ex, ey) = (major - c, b);
        }
        else
        {
            (ex, ey) = (b, major - a);
        }

        double length = Math.Sqrt((ex * ex) + (ey * ey));
        double s1 = Math.Clamp(Math.Sqrt(major), MinScale, MaxScale);
        double s2 = Math.Clamp(Math.Sqrt(minor), MinScale, MaxScale);
        return Footprint.Ewa(s1, s2, ex / length, ey / length, Support);
    }

    /// <summary>Tabulates the normalized 1D weights, as Rotate does.</summary>
    private static float[] KernelTable(ResampleFilter filter, int taps, int reach)
    {
        return KernelTables[(int)filter] ??= Build();

        float[] Build()
        {
            float[] table = new float[(KernelSteps + 1) * taps];
            Span<double> w = stackalloc double[taps];
            for (int s = 0; s <= KernelSteps; s++)
            {
                double fraction = s / (double)KernelSteps;
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

            return table;
        }
    }

    /// <summary>Tabulates the filter over the radius, sampled at the middle of each Q interval.</summary>
    private static float[] RadialTable(ResampleFilter filter, double support)
    {
        return RadialTables[(int)filter] ??= Build();

        float[] Build()
        {
            float[] table = new float[RadialSteps];
            for (int i = 0; i < RadialSteps; i++)
            {
                double r = Math.Sqrt((i + 0.5) / RadialSteps) * support;

                // Box is half-open for resizing; radially every point inside the support counts fully.
                table[i] = filter == ResampleFilter.Box ? 1f : (float)ResampleKernels.Evaluate(filter, r);
            }

            return table;
        }
    }
}

/// <summary>
/// How one output pixel is sampled: with the plain kernel, or by elliptical weighted averaging (Heckbert 1989) over the ellipse
/// <c>A·dx² + B·dx·dy + C·dy² &lt; 1</c> around the source point, which reaches <see cref="HalfWidth"/> and
/// <see cref="HalfHeight"/> pixels.
/// </summary>
internal readonly struct Footprint
{
    private Footprint(bool ewa, double a, double b, double c, double halfWidth, double halfHeight)
    {
        IsEwa = ewa;
        A = a;
        B = b;
        C = c;
        HalfWidth = halfWidth;
        HalfHeight = halfHeight;
    }

    internal static Footprint Plain => default;

    internal bool IsEwa { get; }

    internal double A { get; }

    internal double B { get; }

    internal double C { get; }

    internal double HalfWidth { get; }

    internal double HalfHeight { get; }

    /// <summary>
    /// Returns the ellipse with semi-axes <paramref name="s1"/> × support along (<paramref name="ux"/>, <paramref name="uy"/>) and
    /// <paramref name="s2"/> × support across it.
    /// </summary>
    internal static Footprint Ewa(double s1, double s2, double ux, double uy, double support)
    {
        double r1 = s1 * support;
        double r2 = s2 * support;
        double i1 = 1 / (r1 * r1);
        double i2 = 1 / (r2 * r2);

        // M = U diag(1/r1², 1/r2²) Uᵀ, with U's columns (ux, uy) and (−uy, ux).
        double a = (ux * ux * i1) + (uy * uy * i2);
        double b = 2 * ux * uy * (i1 - i2);
        double c = (uy * uy * i1) + (ux * ux * i2);
        double halfWidth = Math.Sqrt((ux * ux * r1 * r1) + (uy * uy * r2 * r2));
        double halfHeight = Math.Sqrt((uy * uy * r1 * r1) + (ux * ux * r2 * r2));
        return new Footprint(true, a, b, c, halfWidth, halfHeight);
    }
}

/// <summary>
/// Reads premultiplied working pixels from one source plane for one band: the plain 2D kernel (exactly as Rotate samples), point
/// sampling, and elliptical weighted averaging, with taps outside the source following the edge mode.
/// </summary>
internal readonly ref struct FootprintSampler<TPixel>
    where TPixel : unmanaged, IPixelType<TPixel>
{
    private readonly PixelPlane<TPixel> _plane;
    private readonly ReadOnlySpan<TPixel> _all;
    private readonly ReadOnlySpan<float> _kernel;
    private readonly ReadOnlySpan<float> _radial;
    private readonly int _taps;
    private readonly int _reach;
    private readonly EdgeMode _edges;
    private readonly Colors.LinearCurve? _curve;
    private readonly bool _hasAlpha;
    private readonly int _width;
    private readonly int _height;

    internal FootprintSampler(PixelPlane<TPixel> plane, SamplerSettings settings)
    {
        _plane = plane;
        _all = plane.TryGetContiguous(out Span<TPixel> all) ? all : default;
        _kernel = settings.Kernel;
        _radial = settings.Radial;
        _taps = settings.Taps;
        _reach = settings.Reach;
        _edges = settings.Edges;
        _curve = settings.Curve;
        _hasAlpha = TPixel.Layout.Alpha != AlphaKind.None;
        _width = plane.Width;
        _height = plane.Height;
        Background = Fetch(TPixel.FromRgba8(settings.Background));
    }

    /// <summary>Gets the background in the working form: what Transparent taps and unmapped pixels read.</summary>
    internal Vector4 Background { get; }

    /// <summary>Samples at (<paramref name="u"/>, <paramref name="v"/>), where pixel i's center is at i, as the footprint says.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector4 Sample(double u, double v, in Footprint footprint)
    {
        if (_taps == 1)
        {
            return Nearest(u, v);
        }

        return footprint.IsEwa ? Ewa(u, v, footprint) : Kernel(u, v);
    }

    /// <summary>The closest source pixel.</summary>
    internal Vector4 Nearest(double u, double v)
    {
        if (!Normalize(ref u, ref v, 1, 1))
        {
            return Background;
        }

        int x = (int)Math.Floor(u + 0.5);
        int y = (int)Math.Floor(v + 0.5);
        if ((uint)x < (uint)_width && (uint)y < (uint)_height)
        {
            return Fetch(Row(y)[x]);
        }

        return _edges == EdgeMode.Transparent ? Background : Fetch(Row(Edge(y, _height))[Edge(x, _width)]);
    }

    /// <summary>The filter's separable 2D kernel, unstretched: Rotate's sampling, with edge modes added.</summary>
    internal Vector4 Kernel(double u, double v)
    {
        if (!Normalize(ref u, ref v, _taps + 1, _taps + 1))
        {
            return Background;
        }

        double fu = Math.Floor(u);
        double fv = Math.Floor(v);
        int x0 = (int)fu - _reach + 1;
        int y0 = (int)fv - _reach + 1;
        bool transparent = _edges == EdgeMode.Transparent;
        if (transparent && (x0 + _taps <= 0 || y0 + _taps <= 0 || x0 >= _width || y0 >= _height))
        {
            return Background;
        }

        ReadOnlySpan<float> wx = _kernel.Slice((int)(((u - fu) * SamplerSettings.KernelSteps) + 0.5) * _taps, _taps);
        ReadOnlySpan<float> wy = _kernel.Slice((int)(((v - fv) * SamplerSettings.KernelSteps) + 0.5) * _taps, _taps);
        bool insideX = x0 >= 0 && x0 + _taps <= _width;
        Vector4 sum = Vector4.Zero;
        for (int j = 0; j < wy.Length; j++)
        {
            int sy = y0 + j;
            Vector4 line = Vector4.Zero;
            if ((uint)sy >= (uint)_height && transparent)
            {
                line = Background;
            }
            else
            {
                ReadOnlySpan<TPixel> pixels = Row((uint)sy < (uint)_height ? sy : Edge(sy, _height));
                if (insideX)
                {
                    pixels = pixels.Slice(x0, _taps);
                    for (int i = 0; i < pixels.Length; i++)
                    {
                        line += Fetch(pixels[i]) * wx[i];
                    }
                }
                else
                {
                    for (int i = 0; i < wx.Length; i++)
                    {
                        int sx = x0 + i;
                        Vector4 p = (uint)sx < (uint)_width ? Fetch(pixels[sx]) : transparent ? Background : Fetch(pixels[Edge(sx, _width)]);
                        line += p * wx[i];
                    }
                }
            }

            sum += line * wy[j];
        }

        return sum;
    }

    /// <summary>
    /// Elliptical weighted averaging: every source pixel center inside the footprint's ellipse around (u, v) weighs the filter at
    /// its radius, and the weights are normalized. Q steps incrementally along each row of the ellipse, as Heckbert's loop does.
    /// </summary>
    internal Vector4 Ewa(double u, double v, in Footprint footprint)
    {
        if (!Normalize(ref u, ref v, footprint.HalfWidth + 1, footprint.HalfHeight + 1))
        {
            return Background;
        }

        double a = footprint.A;
        double b = footprint.B;
        double c = footprint.C;
        int yLow = (int)Math.Ceiling(v - footprint.HalfHeight);
        int yHigh = (int)Math.Floor(v + footprint.HalfHeight);
        int xLow = (int)Math.Ceiling(u - footprint.HalfWidth);
        int xHigh = (int)Math.Floor(u + footprint.HalfWidth);
        bool transparent = _edges == EdgeMode.Transparent;
        bool inside = xLow >= 0 && yLow >= 0 && xHigh < _width && yHigh < _height;
        Vector4 sum = Vector4.Zero;
        float total = 0;
        double ddq = 2 * a;
        for (int y = yLow; y <= yHigh; y++)
        {
            double dy = y - v;

            // The row's span of the ellipse: A·dx² + B·dy·dx + C·dy² − 1 ≤ 0.
            double half = (b * b * dy * dy) - (4 * a * ((c * dy * dy) - 1));
            if (half < 0)
            {
                continue;
            }

            half = Math.Sqrt(half);
            int x0 = Math.Max(xLow, (int)Math.Ceiling(u + ((-b * dy) - half) / (2 * a)));
            int x1 = Math.Min(xHigh, (int)Math.Floor(u + ((-b * dy) + half) / (2 * a)));
            if (x0 > x1)
            {
                continue;
            }

            double dx = x0 - u;
            double q = (((a * dx) + (b * dy)) * dx) + (c * dy * dy);
            double dq = (a * ((2 * dx) + 1)) + (b * dy);
            ReadOnlySpan<TPixel> pixels = default;
            bool rowInside = (uint)y < (uint)_height;
            if (rowInside || !transparent)
            {
                pixels = Row(rowInside ? y : Edge(y, _height));
            }

            for (int x = x0; x <= x1; x++)
            {
                if (q < 1)
                {
                    float w = _radial[Math.Max(0, (int)(q * SamplerSettings.RadialSteps))];
                    Vector4 p = inside || ((uint)x < (uint)_width && rowInside)
                        ? Fetch(pixels[x])
                        : transparent ? Background : Fetch(pixels[Edge(x, _width)]);
                    sum += p * w;
                    total += w;
                }

                q += dq;
                dq += ddq;
            }
        }

        // A footprint always holds a pixel center (its radius is at least one pixel), so the total is 0 only for a filter whose
        // weights cancel there; the nearest pixel is the honest answer then.
        return total > 1e-6f ? sum / total : Nearest(u, v);
    }

    /// <summary>
    /// Returns a pixel premultiplied, in the channel order the row converter stores: memory order for Rgba8 and Bgra8 and RGBA for
    /// every other type, and in linear light when the settings ask for it.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal Vector4 Fetch(in TPixel pixel)
    {
        if (_curve is not null)
        {
            return FetchLinear(pixel);
        }

        if (RowConverter<TPixel>.Premul8)
        {
            Vector128<byte> raw = Vector128.CreateScalarUnsafe(Unsafe.As<TPixel, uint>(ref Unsafe.AsRef(in pixel))).AsByte();
            return (Vector128.ConvertToSingle(Vector128.WidenLower(Vector128.WidenLower(raw)).AsInt32()) * (1f / 255f)).AsVector4();
        }

        if (RowConverter<TPixel>.PremulFloat)
        {
            return Unsafe.As<TPixel, Vector4>(ref Unsafe.AsRef(in pixel));
        }

        if (RowConverter<TPixel>.Ink)
        {
            Span<float> ink = stackalloc float[4];
            CmykInk.Load(new ReadOnlySpan<TPixel>(in pixel), ink);
            return new Vector4(ink[0], ink[1], ink[2], ink[3]);
        }

        Vector4 p;
        if (RowConverter<TPixel>.FourByte)
        {
            Vector128<byte> bytes = Vector128.CreateScalarUnsafe(Unsafe.As<TPixel, uint>(ref Unsafe.AsRef(in pixel))).AsByte();
            Vector128<uint> wide = Vector128.WidenLower(Vector128.WidenLower(bytes));
            p = (Vector128.ConvertToSingle(wide.AsInt32()) * (1f / 255f)).AsVector4();
        }
        else
        {
            p = pixel.ToScaled();
        }

        return _hasAlpha ? p * new Vector4(p.W, p.W, p.W, 1f) : p;
    }

    /// <summary>Folds an index outside 0..<paramref name="size"/> − 1 back inside by the edge mode (never Transparent).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private int Edge(int index, int size)
    {
        switch (_edges)
        {
            case EdgeMode.Wrap:
                int wrapped = index % size;
                return wrapped < 0 ? wrapped + size : wrapped;
            case EdgeMode.Mirror:
                int period = 2 * size;
                int m = index % period;
                m = m < 0 ? m + period : m;
                return m < size ? m : period - 1 - m;
            default:
                return Math.Clamp(index, 0, size - 1);
        }
    }

    /// <summary>
    /// Brings a position within reach of the image without changing what it samples, so the integer arithmetic stays small: Wrap
    /// and Mirror move it by whole periods, Clamp pulls it to just past the edge (beyond <paramref name="marginX"/> every tap reads
    /// the edge anyway). Returns <see langword="false"/> when it reads only the background: not finite, or Transparent and more
    /// than the margin outside.
    /// </summary>
    private bool Normalize(ref double u, ref double v, double marginX, double marginY)
    {
        if (!double.IsFinite(u) || !double.IsFinite(v))
        {
            return false;
        }

        switch (_edges)
        {
            case EdgeMode.Transparent:
                return u > -marginX && v > -marginY && u < _width - 1 + marginX && v < _height - 1 + marginY;
            case EdgeMode.Wrap:
                u -= Math.Floor(u / _width) * _width;
                v -= Math.Floor(v / _height) * _height;
                return true;
            case EdgeMode.Mirror:
                u -= Math.Floor(u / (2.0 * _width)) * 2.0 * _width;
                v -= Math.Floor(v / (2.0 * _height)) * 2.0 * _height;
                return true;
            default:
                u = Math.Clamp(u, -marginX, _width - 1 + marginX);
                v = Math.Clamp(v, -marginY, _height - 1 + marginY);
                return true;
        }
    }

    private Vector4 FetchLinear(in TPixel pixel)
    {
        Colors.LinearCurve curve = _curve!;
        if (RowConverter<TPixel>.Ink)
        {
            Span<float> ink = stackalloc float[4];
            CmykInk.Load(new ReadOnlySpan<TPixel>(in pixel), ink);
            return new Vector4(ink[0], ink[1], ink[2], ink[3]);
        }

        Vector4 p;
        if (RowConverter<TPixel>.FourByte)
        {
            uint raw = Unsafe.As<TPixel, uint>(ref Unsafe.AsRef(in pixel));
            float[] table = curve.LinearFrom8;
            p = new Vector4(table[raw & 0xFF], table[(raw >> 8) & 0xFF], table[(raw >> 16) & 0xFF], (raw >> 24) * (1f / 255f));
        }
        else
        {
            p = pixel.ToScaled();
            if (TPixel.Layout.ChannelType == PixelChannelType.Unorm8)
            {
                float[] table = curve.LinearFrom8;
                p = new Vector4(table[(int)((p.X * 255f) + 0.5f)], table[(int)((p.Y * 255f) + 0.5f)], table[(int)((p.Z * 255f) + 0.5f)], p.W);
            }
            else
            {
                p = new Vector4((float)curve.ToLinear(p.X), (float)curve.ToLinear(p.Y), (float)curve.ToLinear(p.Z), p.W);
            }
        }

        return _hasAlpha ? p * new Vector4(p.W, p.W, p.W, 1f) : p;
    }

    private ReadOnlySpan<TPixel> Row(int y)
    {
        return _all.IsEmpty ? _plane.Row(y) : _all.Slice(y * _width, _width);
    }
}
