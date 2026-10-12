using System.Numerics;

using Egami.Operations;
using Egami.Pixels;
using Egami.Svg.Filters;

namespace Egami.Svg.Rendering;

/// <summary>
/// feConvolveMatrix and the lighting primitives as SVG 1.1 defines them, on straight-alpha float pixels (width × height, row by
/// row), where Egami's general operations do not fit: kernels of any order with a target pixel, and the specification's edge
/// kernels for surface normals. Where SVG leaves a value open (a negative constant, a spot light pointing at itself) the result
/// is what resvg draws.
/// </summary>
internal static class SvgFilterKernels
{
    /// <summary>
    /// feConvolveMatrix: each result is the sum of the kernel (rotated 180°) over the pixels around it, with the kernel's target
    /// on the pixel, divided by the divisor, plus the bias times alpha. Colors are premultiplied, or straight with alpha kept when
    /// preserveAlpha is set.
    /// </summary>
    public static RgbaF32[] Convolve(RgbaF32[] pixels, int width, int height, SvgConvolve c)
    {
        Vector4[] source = new Vector4[pixels.Length];
        for (int i = 0; i < pixels.Length; i++)
        {
            RgbaF32 p = pixels[i];
            source[i] = c.PreserveAlpha ? new Vector4(p.R, p.G, p.B, p.A) : new Vector4(p.R * p.A, p.G * p.A, p.B * p.A, p.A);
        }

        RgbaF32[] result = new RgbaF32[pixels.Length];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector4 sum = Vector4.Zero;
                for (int j = 0; j < c.OrderY; j++)
                {
                    for (int i = 0; i < c.OrderX; i++)
                    {
                        int sx = x - c.TargetX + i, sy = y - c.TargetY + j;
                        if (!Edge(ref sx, width, c.Edge) || !Edge(ref sy, height, c.Edge))
                        {
                            continue;
                        }

                        sum += source[(sy * width) + sx] * c.Kernel[((c.OrderY - 1 - j) * c.OrderX) + (c.OrderX - 1 - i)];
                    }
                }

                float alpha = c.PreserveAlpha ? source[(y * width) + x].W : (sum.W / c.Divisor) + c.Bias;
                float a = Math.Clamp(alpha, 0, 1);
                Vector3 color = (new Vector3(sum.X, sum.Y, sum.Z) / c.Divisor) + new Vector3(c.Bias * alpha);
                color = Vector3.Clamp(c.PreserveAlpha ? color * a : color, Vector3.Zero, Vector3.One);

                // Back to straight alpha; a premultiplied color above its alpha is clamped to it, as an 8-bit pipeline does.
                result[(y * width) + x] = a > 0
                    ? new RgbaF32(Math.Min(color.X / a, 1), Math.Min(color.Y / a, 1), Math.Min(color.Z / a, 1), a)
                    : default;
            }
        }

        return result;
    }

    private static bool Edge(ref int v, int size, EdgeMode mode)
    {
        if ((uint)v < (uint)size)
        {
            return true;
        }

        switch (mode)
        {
            case EdgeMode.Transparent:
                return false;
            case EdgeMode.Wrap:
                v = ((v % size) + size) % size;
                return true;
            default:
                v = Math.Clamp(v, 0, size - 1);
                return true;
        }
    }

    /// <summary>
    /// feDiffuseLighting and feSpecularLighting: the alpha channel as a surface, its normals from SVG's Sobel kernels (one-sided
    /// at the edges), lit by a distant, point or spot light whose position is already in pixels.
    /// </summary>
    public static RgbaF32[] Light(RgbaF32[] pixels, int width, int height, SvgLighting lighting, SvgLight light, RgbaF32 color)
    {
        RgbaF32[] result = new RgbaF32[pixels.Length];
        float scale = lighting.SurfaceScale;
        Vector3 lightColor = new(color.R, color.G, color.B);
        Vector3 distant = Vector3.Zero, spotDirection = Vector3.Zero;
        float coneCos = -1;
        bool spotValid = true;
        switch (light)
        {
            case SvgDistantLight d:
            {
                float az = d.Azimuth * MathF.PI / 180, el = d.Elevation * MathF.PI / 180;
                distant = new Vector3(MathF.Cos(az) * MathF.Cos(el), MathF.Sin(az) * MathF.Cos(el), MathF.Sin(el));
                break;
            }

            case SvgSpotLight s:
            {
                Vector3 direction = s.PointsAt - s.Position;
                spotValid = direction.LengthSquared() > 0;
                spotDirection = spotValid ? Vector3.Normalize(direction) : Vector3.Zero;
                if (s.ConeAngle is float cone)
                {
                    coneCos = MathF.Cos(Math.Abs(cone) * MathF.PI / 180);
                }

                break;
            }
        }

        float A(int x, int y) => pixels[(y * width) + x].A;
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                Vector2 slope = Normal(pixels, width, height, x, y);
                Vector3 normal = Vector3.Normalize(new Vector3(-scale * slope.X, -scale * slope.Y, 1));
                Vector3 l;
                Vector3 lit = lightColor;
                switch (light)
                {
                    case SvgDistantLight:
                        l = distant;
                        break;
                    case SvgPointLight p:
                        l = SafeNormalize(p.Position - new Vector3(x, y, scale * A(x, y)));
                        break;
                    case SvgSpotLight s:
                    {
                        l = SafeNormalize(s.Position - new Vector3(x, y, scale * A(x, y)));
                        float minusLDotS = -Vector3.Dot(l, spotDirection);
                        if (!spotValid || minusLDotS <= 0 || minusLDotS < coneCos)
                        {
                            lit = Vector3.Zero;
                        }
                        else
                        {
                            lit *= MathF.Pow(minusLDotS, s.Exponent);
                        }

                        break;
                    }

                    default:
                        l = Vector3.UnitZ;
                        break;
                }

                float factor;
                if (lighting.Specular)
                {
                    Vector3 h = SafeNormalize(l + Vector3.UnitZ);
                    float nh = Vector3.Dot(normal, h);
                    factor = lighting.Constant * (nh > 0 ? MathF.Pow(nh, lighting.Exponent) : 0);
                }
                else
                {
                    factor = lighting.Constant * Vector3.Dot(normal, l);
                }

                Vector3 c = Vector3.Clamp(lit * factor, Vector3.Zero, Vector3.One);
                if (lighting.Specular)
                {
                    // The specular result is premultiplied with alpha the brightest channel.
                    float a = Math.Max(c.X, Math.Max(c.Y, c.Z));
                    result[(y * width) + x] = a > 0 ? new RgbaF32(c.X / a, c.Y / a, c.Z / a, a) : default;
                }
                else
                {
                    result[(y * width) + x] = new RgbaF32(c.X, c.Y, c.Z, 1);
                }
            }
        }

        return result;
    }

    private static Vector3 SafeNormalize(Vector3 v) => v.LengthSquared() > 0 ? Vector3.Normalize(v) : Vector3.Zero;

    /// <summary>
    /// The alpha surface's slope at a pixel by SVG's kernels: Sobel weights 1, 2, 1 across, a central difference where both
    /// neighbours exist and a one-sided one at an edge, scaled by the factors the specification's table gives.
    /// </summary>
    private static Vector2 Normal(RgbaF32[] pixels, int width, int height, int x, int y)
    {
        float A(int px, int py) => pixels[(py * width) + px].A;
        bool left = x > 0, right = x < width - 1, up = y > 0, down = y < height - 1;

        float nx = 0, weightY = 0;
        for (int dy = -1; dy <= 1; dy++)
        {
            if ((dy < 0 && !up) || (dy > 0 && !down))
            {
                continue;
            }

            float w = dy == 0 ? 2 : 1;
            weightY += w;
            int py = y + dy;
            float diff = left && right ? A(x + 1, py) - A(x - 1, py) : right ? A(x + 1, py) - A(x, py) : left ? A(x, py) - A(x - 1, py) : 0;
            nx += w * diff;
        }

        float ny = 0, weightX = 0;
        for (int dx = -1; dx <= 1; dx++)
        {
            if ((dx < 0 && !left) || (dx > 0 && !right))
            {
                continue;
            }

            float w = dx == 0 ? 2 : 1;
            weightX += w;
            int px = x + dx;
            float diff = up && down ? A(px, y + 1) - A(px, y - 1) : down ? A(px, y + 1) - A(px, y) : up ? A(px, y) - A(px, y - 1) : 0;
            ny += w * diff;
        }

        nx *= (left && right ? 1 : 2) / weightY;
        ny *= (up && down ? 1 : 2) / weightX;
        return new Vector2(nx, ny);
    }
}
