using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Runtime.Intrinsics.X86;

using Egami.Operations.Compose;
using Egami.Operations.Geometry;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Operations.Filters;

/// <summary>
/// The SVG 1.1 lighting filters (feDiffuseLighting and feSpecularLighting with a distant, point or spot light), with Skia's
/// parameters. The height channel times the surface scale is a surface; its normal comes from SVG's Sobel stencils, with SVG's
/// own stencils on the four edges and corners; then N·L (diffuse) or (N·H)^shininess (specular) scales the light's color. Light
/// positions are in pixels with pixel centers at x + 0.5, as Skia evaluates them; the spot light's cone has Skia's soft edge.
/// The light map is computed on stored values, as Skia does in its working space, and either replaces the image or is blended
/// onto it with a <see cref="BlendMode"/>, the image keeping its alpha. Lighting math runs eight pixels at a time on padded
/// rows, so every pixel is computed the same way wherever it falls in a row, and bands and scopes give the same values.
/// </summary>
internal sealed class LightingOperation : IRasterOperation, INeedsNewPlane, Scopes.INeighbourhoodStep, Scopes.IAreaTranslatable
{
    /// <summary>The width of Skia's soft cone edge, in cosine.</summary>
    internal const float ConeSoftness = 0.016f;

    private readonly LightKind _kind;
    private readonly Vector3 _direction;
    private readonly float _exponent;
    private readonly float _cosCone;
    private readonly Vector3 _color;
    private readonly bool _specular;
    private readonly float _shininess;
    private readonly float _scale;
    private readonly bool _luminance;
    private readonly BlendMode? _blend;
    private readonly float[]? _spotPower;
    private readonly float[]? _shinePower;
    private Vector3 _position;

    internal LightingOperation(Light light, LightingModel model, LightingOptions options)
    {
        switch (light)
        {
            case DistantLight distant:
                _kind = LightKind.Distant;
                _direction = distant.Direction;
                break;
            case PointLight point:
                _kind = LightKind.Point;
                _position = point.Position;
                break;
            case SpotLight spot:
                _kind = LightKind.Spot;
                _position = spot.Position;
                Vector3 axis = spot.Target - spot.Position;
                _direction = axis == Vector3.Zero ? Vector3.Zero : Vector3.Normalize(axis);
                _exponent = spot.Exponent;
                _cosCone = (float)Math.Cos(spot.ConeAngle * Math.PI / 180);
                break;
            default:
                throw new ArgumentException("Not a known light.", nameof(light));
        }

        float k = model.Constant / 255f;
        _color = new Vector3(light.Color.R * k, light.Color.G * k, light.Color.B * k);
        _specular = model.IsSpecular;
        _shininess = model.Shininess;
        _scale = options.SurfaceScale;
        _luminance = options.Height == HeightSource.Luminance;
        _blend = options.Blend;
        _spotPower = _kind == LightKind.Spot ? PowerTable(_exponent) : null;
        _shinePower = _specular ? PowerTable(_shininess) : null;
    }

    private enum LightKind
    {
        Distant,
        Point,
        Spot,
    }

    public (int X, int Y)? Reach => (1, 1);

    public bool NeedsNewPlane(Size input) => true;

    public Size OutputSize(Size input) => input;

    /// <summary>Returns the step as it runs on a view of <paramref name="area"/>: the light moves with the coordinates.</summary>
    public IRasterOperation? InArea(Rect area)
    {
        LightingOperation moved = (LightingOperation)MemberwiseClone();
        moved._position -= new Vector3(area.X, area.Y, 0);
        return moved;
    }

    public void Run<TPixel>(OperationInput<TPixel> input)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        int height = input.Source.Height;
        RowConverter<TPixel> converter = new(null, inksAsRgb: true);
        long bytes = (long)input.Source.Width * Unsafe.SizeOf<TPixel>() * height;
        Chunks.Run(height, bytes, input.Context.MaxParallelism, (start, end) => Band(input, converter, start, end), input.CancellationToken);
    }

    /// <summary>
    /// Writes SVG's surface normal for one row: the x and y parts of (Nx, Ny, 1) before it is normalized, with
    /// the surface scale in. <paramref name="up"/> and <paramref name="down"/> are empty on the first and last rows; the edge
    /// stencils drop the missing neighbours and scale up the rest, as SVG's table of edge and corner kernels does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static void Normals(ReadOnlySpan<float> up, ReadOnlySpan<float> center, ReadOnlySpan<float> down, float scale, Span<float> nx, Span<float> ny, Span<float> scratch)
    {
        int w = center.Length;
        bool hasUp = !up.IsEmpty, hasDown = !down.IsEmpty;

        // Nx: columns smoothed (1, 2, 1) over the rows there are, then right minus left; the factor is 2 / (column distance ×
        // row weights), SVG's 1/4 inside, 1/3 on the top and bottom rows, 1/2 on the side columns and 2/3 at the corners.
        Span<float> v = scratch[..w];
        int start = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            // The same sums as the loop below, eight columns at a time.
            Vector256<float> two = Vector256.Create(2f);
            for (; start <= w - Vector256<float>.Count; start += Vector256<float>.Count)
            {
                Vector256<float> sum = two * Vector256.Create(center[start..]);
                sum = hasUp ? sum + Vector256.Create(up[start..]) : sum;
                sum = hasDown ? sum + Vector256.Create(down[start..]) : sum;
                sum.CopyTo(v[start..]);
            }
        }

        for (int x = start; x < w; x++)
        {
            float s = 2 * center[x];
            if (hasUp)
            {
                s += up[x];
            }

            if (hasDown)
            {
                s += down[x];
            }

            v[x] = s;
        }

        float weights = 2 + (hasUp ? 1 : 0) + (hasDown ? 1 : 0);
        if (w == 1)
        {
            nx[0] = 0;
        }
        else
        {
            float inner = -scale / weights, edge = -2 * scale / weights;
            nx[0] = edge * (v[1] - v[0]);
            int x = 1;
            for (Vector256<float> factor = Vector256.Create(inner); x + 8 < w; x += 8)
            {
                (factor * (Vector256.Create(v[(x + 1)..]) - Vector256.Create(v[(x - 1)..]))).CopyTo(nx[x..]);
            }

            for (; x < w - 1; x++)
            {
                nx[x] = inner * (v[x + 1] - v[x - 1]);
            }

            nx[w - 1] = edge * (v[w - 1] - v[w - 2]);
        }

        // Ny: each column's lower row minus its upper one (the center row where one is missing), then smoothed (1, 2, 1) across.
        int rows = (hasUp ? 1 : 0) + (hasDown ? 1 : 0);
        if (rows == 0)
        {
            ny[..w].Clear();
            return;
        }

        Span<float> d = scratch[..w];
        ReadOnlySpan<float> lower = hasDown ? down : center, upper = hasUp ? up : center;
        start = 0;
        if (Vector256.IsHardwareAccelerated)
        {
            for (; start <= w - Vector256<float>.Count; start += Vector256<float>.Count)
            {
                (Vector256.Create(lower[start..]) - Vector256.Create(upper[start..])).CopyTo(d[start..]);
            }
        }

        for (int x = start; x < w; x++)
        {
            d[x] = lower[x] - upper[x];
        }

        if (w == 1)
        {
            ny[0] = -scale / rows * d[0] * 2;
            return;
        }

        float middle = -2 * scale / (rows * 4), side = -2 * scale / (rows * 3);
        ny[0] = side * ((2 * d[0]) + d[1]);
        int i = 1;
        for (Vector256<float> factor = Vector256.Create(middle), two = Vector256.Create(2f); i + 8 < w; i += 8)
        {
            (factor * (Vector256.Create(d[(i - 1)..]) + (two * Vector256.Create(d[i..])) + Vector256.Create(d[(i + 1)..]))).CopyTo(ny[i..]);
        }

        for (int x = i; x < w - 1; x++)
        {
            ny[x] = middle * (d[x - 1] + (2 * d[x]) + d[x + 1]);
        }

        ny[w - 1] = side * (d[w - 2] + (2 * d[w - 1]));
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private void Band<TPixel>(OperationInput<TPixel> input, RowConverter<TPixel> converter, int start, int end)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        PixelPlane<TPixel> source = input.Source, destination = input.Destination;
        int width = source.Width, height = source.Height;
        int padded = (width + 7) & ~7;
        float[] heightBuffer = ArrayPool<float>.Shared.Rent(3 * padded);
        float[] normalBuffer = ArrayPool<float>.Shared.Rent(3 * padded);
        float[] rowBuffer = ArrayPool<float>.Shared.Rent(2 * width * 4);
        try
        {
            Span<float> heights = heightBuffer.AsSpan(0, 3 * padded);
            heights.Clear();
            Span<float> nx = normalBuffer.AsSpan(0, padded), ny = normalBuffer.AsSpan(padded, padded), scratch = normalBuffer.AsSpan(2 * padded, padded);
            nx.Clear();
            ny.Clear();
            Span<float> straight = rowBuffer.AsSpan(0, width * 4), map = rowBuffer.AsSpan(width * 4, width * 4);
            int loaded = Math.Max(start - 1, 0) - 1;
            for (int y = start; y < end; y++)
            {
                if ((y & 15) == 0)
                {
                    input.CancellationToken.ThrowIfCancellationRequested();
                }

                for (int last = Math.Min(y + 1, height - 1); loaded < last;)
                {
                    loaded++;
                    Span<float> target = HeightRow(heights, loaded, padded)[..width];
                    if (!_luminance && StoredRows<TPixel>.Bytes && StoredRows<TPixel>.AlphaByte >= 0)
                    {
                        // Alpha straight from the bytes, as the float row would have it.
                        ReadOnlySpan<byte> bytes = MemoryMarshal.AsBytes(source.Row(loaded));
                        int stride = Unsafe.SizeOf<TPixel>(), at = StoredRows<TPixel>.AlphaByte, x = 0;
                        if (stride == 4 && Vector256.IsHardwareAccelerated)
                        {
                            ReadOnlySpan<uint> pixels = MemoryMarshal.Cast<byte, uint>(bytes);
                            Vector256<uint> low = Vector256.Create(0xFFu);
                            Vector256<float> unit = Vector256.Create(1f / 255);
                            for (; x <= width - Vector256<uint>.Count; x += Vector256<uint>.Count)
                            {
                                (Vector256.ConvertToSingle(((Vector256.Create(pixels[x..]) >>> (at * 8)) & low).AsInt32()) * unit).CopyTo(target[x..]);
                            }
                        }

                        for (; x < width; x++)
                        {
                            target[x] = bytes[(x * stride) + at] * (1f / 255);
                        }
                    }
                    else
                    {
                        ReliefRows.LoadStraight(source.Row(loaded), straight);
                        ReliefRows.Heights(straight, target, _luminance);
                    }
                }

                ReadOnlySpan<float> center = HeightRow(heights, y, padded);
                ReadOnlySpan<float> up = y > 0 ? HeightRow(heights, y - 1, padded)[..width] : default;
                ReadOnlySpan<float> down = y < height - 1 ? HeightRow(heights, y + 1, padded)[..width] : default;
                Normals(up, center[..width], down, _scale, nx, ny, scratch);
                Span<TPixel> row = destination.Row(y);
                if (_blend is null && RowConverter<TPixel>.FourByte)
                {
                    // Rgba8 and Bgra8 take the light straight to bytes.
                    Shade(y, center, nx, ny, map, MemoryMarshal.AsBytes(row), typeof(TPixel) == typeof(Bgra8));
                    continue;
                }

                Shade(y, center, nx, ny, map, default, false);
                if (_blend is BlendMode mode)
                {
                    ReliefRows.LoadStraight(source.Row(y), straight);
                    BlendKeepingAlpha(mode, map, straight);
                    ReliefRows.StoreStraight(straight, row);
                }
                else
                {
                    converter.Store(map, row, opaque: !_specular);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(heightBuffer);
            ArrayPool<float>.Shared.Return(normalBuffer);
            ArrayPool<float>.Shared.Return(rowBuffer);
        }
    }

    private static Span<float> HeightRow(Span<float> heights, int y, int padded) => heights.Slice(y % 3 * padded, padded);

    /// <summary>
    /// Blends the premultiplied light map onto the straight image row as source-atop: the row is blended as if opaque, and its own
    /// alpha is put back, so the light changes the color and never the shape.
    /// </summary>
    private static void BlendKeepingAlpha(BlendMode mode, ReadOnlySpan<float> map, Span<float> straight)
    {
        int n = straight.Length / 4;
        float[] alphaBuffer = ArrayPool<float>.Shared.Rent(n);
        try
        {
            for (int x = 0; x < n; x++)
            {
                alphaBuffer[x] = straight[(x * 4) + 3];
                straight[(x * 4) + 3] = 1;
            }

            BlendKernels.BlendFloat(mode, map, straight, default, 1);
            for (int x = 0; x < n; x++)
            {
                int i = x * 4;
                float a = straight[i + 3];
                float inverse = a > 0 ? 1 / a : 0;
                straight[i] = Math.Clamp(straight[i] * inverse, 0, 1);
                straight[i + 1] = Math.Clamp(straight[i + 1] * inverse, 0, 1);
                straight[i + 2] = Math.Clamp(straight[i + 2] * inverse, 0, 1);
                straight[i + 3] = alphaBuffer[x];
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(alphaBuffer);
        }
    }

    /// <summary>Lights row <paramref name="y"/> into <paramref name="map"/>: premultiplied RGBA, four floats per pixel.</summary>
    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private unsafe void Shade(int y, ReadOnlySpan<float> heights, ReadOnlySpan<float> nxRow, ReadOnlySpan<float> nyRow, Span<float> map, Span<byte> bytes, bool bgr)
    {
        fixed (float* spotPower = _spotPower, shinePower = _shinePower)
        {
            Shade(y, heights, nxRow, nyRow, map, bytes, bgr, spotPower, shinePower);
        }
    }

    /// <summary>
    /// Lights row <paramref name="y"/>, into <paramref name="map"/> as premultiplied RGBA floats, or with <paramref name="bytes"/>
    /// straight into them as Rgba8 (Bgra8 when <paramref name="bgr"/>), divided by alpha and rounded as the float map would be
    /// stored.
    /// </summary>
    private unsafe void Shade(int y, ReadOnlySpan<float> heights, ReadOnlySpan<float> nxRow, ReadOnlySpan<float> nyRow, Span<float> map, Span<byte> bytes, bool bgr, float* spotPower, float* shinePower)
    {
        int width = map.Length / 4;
        Vector256<float> one = Vector256<float>.One, zero = Vector256<float>.Zero;
        Vector256<float> iota = Vector256.Create(0.5f, 1.5f, 2.5f, 3.5f, 4.5f, 5.5f, 6.5f, 7.5f);
        Vector256<float> py = Vector256.Create(y + 0.5f);
        Vector256<float> scale = Vector256.Create(_scale);
        Vector256<float> colorR = Vector256.Create(_color.X), colorG = Vector256.Create(_color.Y), colorB = Vector256.Create(_color.Z);
        Span<float> lanes = stackalloc float[32];
        for (int x0 = 0; x0 < width; x0 += 8)
        {
            Vector256<float> nX = Vector256.Create(nxRow.Slice(x0, 8)), nY = Vector256.Create(nyRow.Slice(x0, 8));
            Vector256<float> inverse = InverseRoot((nX * nX) + (nY * nY) + one);
            nX *= inverse;
            nY *= inverse;
            Vector256<float> nZ = inverse;

            Vector256<float> lx, ly, lz;
            if (_kind == LightKind.Distant)
            {
                lx = Vector256.Create(_direction.X);
                ly = Vector256.Create(_direction.Y);
                lz = Vector256.Create(_direction.Z);
            }
            else
            {
                Vector256<float> z = scale * Vector256.Create(heights.Slice(x0, 8));
                lx = Vector256.Create(_position.X) - (iota + Vector256.Create((float)x0));
                ly = Vector256.Create(_position.Y) - py;
                lz = Vector256.Create(_position.Z) - z;
                Vector256<float> squared = (lx * lx) + (ly * ly) + (lz * lz);
                Vector256<float> unit = Vector256.ConditionalSelect(Vector256.GreaterThan(squared, zero), InverseRoot(squared), zero);
                lx *= unit;
                ly *= unit;
                lz *= unit;
            }

            Vector256<float> r = colorR, g = colorG, b = colorB;
            if (_kind == LightKind.Spot)
            {
                Vector256<float> cosAngle = -((lx * _direction.X) + (ly * _direction.Y) + (lz * _direction.Z));
                Vector256<float> cone = Vector256.Create(_cosCone);
                Vector256<float> spot = Power(cosAngle, spotPower);
                Vector256<float> soft = (cosAngle - cone) * (1 / ConeSoftness);
                spot = Vector256.ConditionalSelect(Vector256.LessThan(cosAngle, cone + Vector256.Create(ConeSoftness)), spot * soft, spot);
                spot = Vector256.ConditionalSelect(Vector256.LessThan(cosAngle, cone), zero, spot);
                r *= spot;
                g *= spot;
                b *= spot;
            }

            Vector256<float> coefficient;
            if (_specular)
            {
                Vector256<float> hz = lz + one;
                Vector256<float> squared = (lx * lx) + (ly * ly) + (hz * hz);
                Vector256<float> unit = Vector256.ConditionalSelect(Vector256.GreaterThan(squared, zero), InverseRoot(squared), zero);
                Vector256<float> dot = ((nX * lx) + (nY * ly) + (nZ * hz)) * unit;
                coefficient = Power(Vector256.Max(dot, zero), shinePower);
            }
            else
            {
                coefficient = (nX * lx) + (nY * ly) + (nZ * lz);
            }

            r = Vector256.Min(Vector256.Max(coefficient * r, zero), one);
            g = Vector256.Min(Vector256.Max(coefficient * g, zero), one);
            b = Vector256.Min(Vector256.Max(coefficient * b, zero), one);
            Vector256<float> a = _specular ? Vector256.Max(Vector256.Max(r, g), b) : one;
            if (!bytes.IsEmpty)
            {
                StoreBytes(bytes, x0, Math.Min(8, width - x0), bgr ? b : r, g, bgr ? r : b, a, !_specular);
                continue;
            }

            r.CopyTo(lanes);
            g.CopyTo(lanes[8..]);
            b.CopyTo(lanes[16..]);
            a.CopyTo(lanes[24..]);
            int count = Math.Min(8, width - x0);
            Span<float> target = map.Slice(x0 * 4, count * 4);
            for (int i = 0; i < count; i++)
            {
                target[i * 4] = lanes[i];
                target[(i * 4) + 1] = lanes[8 + i];
                target[(i * 4) + 2] = lanes[16 + i];
                target[(i * 4) + 3] = lanes[24 + i];
            }
        }
    }

    /// <summary>
    /// Returns x^<paramref name="e"/> at 4,096 even steps of x from 0 to 1, and a guard entry, for <see cref="Power"/>: 0^e is 0
    /// (1 when e is 0), as the light's cone and shine have it.
    /// </summary>
    internal static float[] PowerTable(float e)
    {
        // A handful of exponents are ever in use, so tables are kept by exponent and the cache simply emptied if it fills.
        if (Powers.TryGetValue(e, out float[]? known))
        {
            return known;
        }

        if (Powers.Count >= 64)
        {
            Powers.Clear();
        }

        return Powers.GetOrAdd(e, BuildPowerTable);
    }

    private static float[] BuildPowerTable(float e)
    {
        float[] table = new float[PowerSteps + 2];
        for (int i = 0; i <= PowerSteps; i++)
        {
            table[i] = e == 0 ? 1 : (float)Math.Pow(i / (double)PowerSteps, e);
        }

        table[PowerSteps + 1] = table[PowerSteps];
        return table;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<float, float[]> Powers = new();

    /// <summary>The steps of <see cref="PowerTable"/>: interpolating between them is within 0.03 of a level of x^e for e up to 128.</summary>
    private const int PowerSteps = 4096;

    /// <summary>x^e for each lane through a <see cref="PowerTable"/>, x clamped to 0..1, interpolated linearly between steps.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe Vector256<float> Power(Vector256<float> x, float* table)
    {
        Vector256<float> at = Vector256.Min(Vector256.Max(x, Vector256<float>.Zero), Vector256<float>.One) * PowerSteps;
        Vector256<int> i = Vector256.ConvertToInt32(at);
        Vector256<float> f = at - Vector256.ConvertToSingle(i), low, high;
        if (Avx2.IsSupported)
        {
            low = Avx2.GatherVector256(table, i, 4);
            high = Avx2.GatherVector256(table + 1, i, 4);
        }
        else
        {
            low = Vector256.Create(table[i[0]], table[i[1]], table[i[2]], table[i[3]], table[i[4]], table[i[5]], table[i[6]], table[i[7]]);
            high = Vector256.Create(table[i[0] + 1], table[i[1] + 1], table[i[2] + 1], table[i[3] + 1], table[i[4] + 1], table[i[5] + 1], table[i[6] + 1], table[i[7] + 1]);
        }

        return low + (f * (high - low));
    }

    /// <summary>1 / √x for each lane: the hardware's estimate refined by one Newton step where there is one, else exactly.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> InverseRoot(Vector256<float> x)
    {
        if (!Avx.IsSupported)
        {
            return Vector256<float>.One / Vector256.Sqrt(x);
        }

        Vector256<float> r = Avx.ReciprocalSqrt(x);
        return r * (Vector256.Create(1.5f) - (Vector256.Create(0.5f) * x * r * r));
    }

    /// <summary>
    /// Stores <paramref name="count"/> lit pixels from column <paramref name="x0"/> as four-byte pixels, the channels in the order
    /// given: premultiplied channels divided by alpha (a diffuse map's alpha is 1), clamped and rounded as the float store does.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static void StoreBytes(Span<byte> bytes, int x0, int count, Vector256<float> c0, Vector256<float> c1, Vector256<float> c2, Vector256<float> a, bool opaque)
    {
        Vector256<float> zero = Vector256<float>.Zero, one = Vector256<float>.One, scale = Vector256.Create(255f), half = Vector256.Create(0.5f);
        Vector256<int> pixel;
        if (opaque)
        {
            // A diffuse map is opaque: its colors are stored as they are, clamped.
            pixel = Vector256.Create(255 << 24);
            pixel |= Vector256.ConvertToInt32((Vector256.Min(Vector256.Max(c0, zero), one) * scale) + half);
            pixel |= Vector256.ConvertToInt32((Vector256.Min(Vector256.Max(c1, zero), one) * scale) + half) << 8;
            pixel |= Vector256.ConvertToInt32((Vector256.Min(Vector256.Max(c2, zero), one) * scale) + half) << 16;
        }
        else
        {
            // One division for the three colors.
            Vector256<float> positive = Vector256.GreaterThan(a, zero), inverse = one / a;
            pixel = Vector256.ConvertToInt32((Vector256.Min(Vector256.Max(a, zero), one) * scale) + half) << 24;
            pixel |= Channel(c0, inverse, positive);
            pixel |= Channel(c1, inverse, positive) << 8;
            pixel |= Channel(c2, inverse, positive) << 16;
        }

        Span<uint> to = MemoryMarshal.Cast<byte, uint>(bytes).Slice(x0, count);
        if (count == 8)
        {
            pixel.AsUInt32().CopyTo(to);
            return;
        }

        for (int i = 0; i < count; i++)
        {
            to[i] = pixel.AsUInt32()[i];
        }

        static Vector256<int> Channel(Vector256<float> c, Vector256<float> inverse, Vector256<float> positive)
        {
            Vector256<float> v = Vector256.ConditionalSelect(positive, Vector256.Min(Vector256.Max(c * inverse, Vector256<float>.Zero), Vector256<float>.One), Vector256<float>.Zero);
            return Vector256.ConvertToInt32((v * Vector256.Create(255f)) + Vector256.Create(0.5f));
        }
    }
}
