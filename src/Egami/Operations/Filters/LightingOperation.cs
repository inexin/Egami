using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

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
    internal static void Normals(ReadOnlySpan<float> up, ReadOnlySpan<float> center, ReadOnlySpan<float> down, float scale, Span<float> nx, Span<float> ny, Span<float> scratch)
    {
        int w = center.Length;
        bool hasUp = !up.IsEmpty, hasDown = !down.IsEmpty;

        // Nx: columns smoothed (1, 2, 1) over the rows there are, then right minus left; the factor is 2 / (column distance ×
        // row weights), SVG's 1/4 inside, 1/3 on the top and bottom rows, 1/2 on the side columns and 2/3 at the corners.
        Span<float> v = scratch[..w];
        for (int x = 0; x < w; x++)
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
        for (int x = 0; x < w; x++)
        {
            d[x] = (hasDown ? down[x] : center[x]) - (hasUp ? up[x] : center[x]);
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
                        int stride = Unsafe.SizeOf<TPixel>(), at = StoredRows<TPixel>.AlphaByte;
                        for (int x = 0; x < width; x++)
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
                Shade(y, center, nx, ny, map);
                Span<TPixel> row = destination.Row(y);
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
    private void Shade(int y, ReadOnlySpan<float> heights, ReadOnlySpan<float> nxRow, ReadOnlySpan<float> nyRow, Span<float> map)
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
            Vector256<float> inverse = one / Vector256.Sqrt((nX * nX) + (nY * nY) + one);
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
                Vector256<float> length = Vector256.Sqrt((lx * lx) + (ly * ly) + (lz * lz));
                Vector256<float> unit = Vector256.ConditionalSelect(Vector256.GreaterThan(length, zero), one / length, zero);
                lx *= unit;
                ly *= unit;
                lz *= unit;
            }

            Vector256<float> r = colorR, g = colorG, b = colorB;
            if (_kind == LightKind.Spot)
            {
                Vector256<float> cosAngle = -((lx * _direction.X) + (ly * _direction.Y) + (lz * _direction.Z));
                Vector256<float> cone = Vector256.Create(_cosCone);
                Vector256<float> spot = Pow(cosAngle, _exponent);
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
                Vector256<float> length = Vector256.Sqrt((lx * lx) + (ly * ly) + (hz * hz));
                Vector256<float> unit = Vector256.ConditionalSelect(Vector256.GreaterThan(length, zero), one / length, zero);
                Vector256<float> dot = ((nX * lx) + (nY * ly) + (nZ * hz)) * unit;
                coefficient = Pow(Vector256.Max(dot, zero), _shininess);
            }
            else
            {
                coefficient = (nX * lx) + (nY * ly) + (nZ * lz);
            }

            r = Vector256.Min(Vector256.Max(coefficient * r, zero), one);
            g = Vector256.Min(Vector256.Max(coefficient * g, zero), one);
            b = Vector256.Min(Vector256.Max(coefficient * b, zero), one);
            Vector256<float> a = _specular ? Vector256.Max(Vector256.Max(r, g), b) : one;

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

    /// <summary>x^e for x ≥ 0 (0 for x ≤ 0, or 1 when e is 0), as exp(e · ln x), the same on every lane.</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static Vector256<float> Pow(Vector256<float> x, float e)
    {
        if (e == 0)
        {
            return Vector256<float>.One;
        }

        if (e == 1)
        {
            return Vector256.Max(x, Vector256<float>.Zero);
        }

        Vector256<float> positive = Vector256.GreaterThan(x, Vector256<float>.Zero);
        Vector256<float> safe = Vector256.ConditionalSelect(positive, x, Vector256<float>.One);
        return Vector256.ConditionalSelect(positive, Vector256.Exp(Vector256.Log(safe) * e), Vector256<float>.Zero);
    }
}
