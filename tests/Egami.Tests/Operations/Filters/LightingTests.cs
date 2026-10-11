using System.Numerics;

using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>
/// Phase 2.5.4 tasks 11 and 12: Lighting against SVG 1.1's formulas evaluated directly in double precision, its normals against
/// SVG's table of stencils, and Emboss's and Shade's properties. Shade's identity with ImageMagick is in FilterReferenceTests.
/// </summary>
public class LightingTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "filters");

    public static TheoryData<string> Lights() => [.. ReferenceCases.Keys];

    private static readonly Dictionary<string, (Light Light, LightingModel Model, float Scale)> ReferenceCases = new()
    {
        ["distant-diffuse"] = (new DistantLight(45, 30) { Color = new Rgba8(255, 230, 200, 255) }, LightingModel.Diffuse(1.2f), 3),
        ["distant-specular"] = (new DistantLight(210, 40), LightingModel.Specular(1, 20), 5),
        ["point-diffuse"] = (new PointLight(new Vector3(60, 40, 50)), LightingModel.Diffuse(), 2),
        ["point-specular"] = (new PointLight(new Vector3(100, 30, 30)) { Color = new Rgba8(200, 255, 220, 255) }, LightingModel.Specular(1.5f, 8), 4),
        ["spot-diffuse"] = (new SpotLight(new Vector3(20, 10, 80), new Vector3(100, 80, 0), 3, 30), LightingModel.Diffuse(1.5f), 3),
        ["spot-specular"] = (new SpotLight(new Vector3(140, 20, 60), new Vector3(60, 70, 0), 1, 45), LightingModel.Specular(2, 4), 6),
        ["spot-wide"] = (new SpotLight(new Vector3(80, 60, 20), new Vector3(80, 60, -10), 0, 120), LightingModel.Diffuse(), 2),
    };

    [Theory]
    [InlineData(9, 6, 3.7f)]
    [InlineData(2, 2, 1)]
    [InlineData(3, 7, -2.5f)]
    public void NormalsFollowSvgStencils(int width, int height, float scale)
    {
        Random random = new(width * height);
        double[,] field = new double[height, width];
        float[][] rows = new float[height][];
        for (int y = 0; y < height; y++)
        {
            rows[y] = new float[width];
            for (int x = 0; x < width; x++)
            {
                rows[y][x] = (float)random.NextDouble();
                field[y, x] = rows[y][x];
            }
        }

        float[] nx = new float[width], ny = new float[width], scratch = new float[width];
        for (int y = 0; y < height; y++)
        {
            LightingOperation.Normals(y > 0 ? rows[y - 1] : default, rows[y], y < height - 1 ? rows[y + 1] : default, scale, nx, ny, scratch);
            for (int x = 0; x < width; x++)
            {
                (double ex, double ey) = SvgNormal(field, x, y, scale);
                Assert.True(Math.Abs(ex - nx[x]) <= 1e-5 && Math.Abs(ey - ny[x]) <= 1e-5, $"({x}, {y}): expected ({ex}, {ey}), got ({nx[x]}, {ny[x]})");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Lights))]
    public void MatchesTheSvgFormulas(string name)
    {
        (Light light, LightingModel model, float scale) = ReferenceCases[name];
        using Raster<Rgba8> logo = Raster.Load<Rgba8>(Path.Combine(Images, "logo.png"));
        using Raster<Rgba8> actual = logo.Derive(o => o.Lighting(light, model, new LightingOptions { SurfaceScale = scale }), Ct);
        using Raster<Rgba8> expected = Reference(logo, light, model, scale, luminance: false);
        double psnr = Psnr(expected, actual);
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: PSNR {psnr:F2} dB, worst {MaxDifference(expected, actual)} levels");
        Assert.True(psnr >= 50, $"{name}: PSNR {psnr:F2} dB, worst {MaxDifference(expected, actual)} levels");
    }

    [Fact]
    public void LuminanceHeightMatchesTheSvgFormulas()
    {
        using Raster<Rgba8> duck = Raster.Load<Rgba8>(Path.Combine(Images, "duck.png"));
        Light light = new PointLight(new Vector3(30, 20, 40));
        LightingModel model = LightingModel.Specular(1, 6);
        using Raster<Rgba8> actual = duck.Derive(o => o.Lighting(light, model, new LightingOptions { SurfaceScale = 8, Height = HeightSource.Luminance }), Ct);
        using Raster<Rgba8> expected = Reference(duck, light, model, 8, luminance: true);
        Assert.True(Psnr(expected, actual) >= 50, $"PSNR {Psnr(expected, actual):F2} dB");
    }

    [Theory]
    [InlineData(BlendMode.Multiply, false)]
    [InlineData(BlendMode.Screen, true)]
    public void BlendKeepsTheImagesAlpha(BlendMode mode, bool specular)
    {
        using Raster<Rgba8> logo = Raster.Load<Rgba8>(Path.Combine(Images, "logo.png"));
        Light light = new DistantLight(225, 35);
        LightingModel model = specular ? LightingModel.Specular(1.3f, 10) : LightingModel.Diffuse();
        using Raster<Rgba8> map = Reference(logo, light, model, 4, luminance: false);
        using Raster<Rgba8> actual = logo.Derive(o => o.Lighting(light, model, new LightingOptions { SurfaceScale = 4, Blend = mode }), Ct);
        using Raster<Rgba8> expected = Raster.Create<Rgba8>(logo.Width, logo.Height);
        for (int y = 0; y < logo.Height; y++)
        {
            for (int x = 0; x < logo.Width; x++)
            {
                Rgba8 c = logo.Row(y)[x], l = map.Row(y)[x];
                double la = l.A / 255.0;
                double Mix(byte cb, byte cs)
                {
                    double b = cb / 255.0, s = cs / 255.0;
                    double blended = mode == BlendMode.Multiply ? b * s : b + s - (b * s);
                    return (b * (1 - la)) + (la * blended);
                }

                expected.Row(y)[x] = new Rgba8(ToByte(Mix(c.R, l.R)), ToByte(Mix(c.G, l.G)), ToByte(Mix(c.B, l.B)), c.A);
                Assert.Equal(c.A, actual.Row(y)[x].A);
            }
        }

        Assert.True(Psnr(expected, actual) >= 45, $"PSNR {Psnr(expected, actual):F2} dB");
    }

    [Fact]
    public void DiffuseIsOpaqueAndFlatAlphaGetsTheLightsZ()
    {
        using Raster<Rgba8> flat = Raster.Create<Rgba8>(20, 10);
        flat.Apply(o => o.FillColor(new Rgba8(10, 20, 30, 255)), Ct);
        using Raster<Rgba8> lit = flat.Derive(o => o.Lighting(new DistantLight(0, 30), LightingModel.Diffuse(2)), Ct);
        foreach (Rgba8 p in lit.Row(5))
        {
            // N = (0, 0, 1), so N·L = sin 30° = 0.5, times kd 2: white.
            Assert.Equal(new Rgba8(255, 255, 255, 255), p);
        }
    }

    [Theory]
    [InlineData("distant")]
    [InlineData("spot")]
    [InlineData("blend")]
    public void LightingIsTheSameOnOneThreadAndInsideAnArea(string kind)
    {
        Action<RasterOps> chain = kind switch
        {
            "distant" => o => o.Lighting(new DistantLight(60, 30), LightingModel.Specular(1, 12), new LightingOptions { SurfaceScale = 5 }),
            "spot" => o => o.Lighting(new SpotLight(new Vector3(70, 50, 60), new Vector3(120, 100, 0), 2, 40), LightingModel.Diffuse(), new LightingOptions { SurfaceScale = 3 }),
            _ => o => o.Lighting(new PointLight(new Vector3(30, 120, 25)), LightingModel.Diffuse(1.2f), new LightingOptions { Height = HeightSource.Luminance, Blend = BlendMode.Overlay }),
        };
        SameOnOneThread("shapes", chain);
        SameInsideArea("shapes", new Rect(37, 21, 90, 77), chain);
        SameInsideArea("shapes", new Rect(0, 0, 50, 150), chain);
    }

    [Fact]
    public void ReliefIsTheSameOnOneThreadAndInsideAnArea()
    {
        foreach (Action<RasterOps> chain in new Action<RasterOps>[] { o => o.Emboss(), o => o.Emboss(30, 3, 2.5f, false), o => o.Shade(), o => o.Shade(200, 50, true) })
        {
            SameOnOneThread("duck", chain);
            SameInsideArea("duck", new Rect(101, 57, 160, 90), chain);
            SameInsideArea("shapes", new Rect(5, 0, 120, 70), chain);
        }
    }

    [Fact]
    public void ReliefAndLightingWorkOnChannels()
    {
        foreach (Action<RasterOps> chain in new Action<RasterOps>[]
        {
            o => o.Shade(),
            o => o.Emboss(depth: 2),
            o => o.Lighting(new PointLight(new Vector3(90, 60, 40)), LightingModel.Diffuse(), new LightingOptions { Blend = BlendMode.Multiply }),
        })
        {
            using Raster<Rgba8> source = FilterAssert.Load("shapes");
            using Raster<Rgba8> whole = source.Derive(chain, Ct);
            using Raster<Rgba8> red = source.Derive(o => o.OnChannels(ColorChannels.Red, chain), Ct);
            for (int y = 0; y < source.Height; y++)
            {
                for (int x = 0; x < source.Width; x++)
                {
                    Rgba8 s = source.Row(y)[x], w = whole.Row(y)[x], r = red.Row(y)[x];
                    Assert.Equal((w.R, s.G, s.B, s.A), (r.R, r.G, r.B, r.A));
                }
            }
        }
    }

    [Fact]
    public void EmbossTurnsFlatAreasMidGray()
    {
        using Raster<Rgba8> flat = Raster.Create<Rgba8>(30, 20);
        flat.Apply(o => o.FillColor(new Rgba8(200, 40, 90, 170)), Ct);
        flat.Apply(o => o.Emboss(depth: 5, sigma: 2, grayscale: false), Ct);
        for (int y = 0; y < flat.Height; y++)
        {
            foreach (Rgba8 p in flat.Row(y))
            {
                Assert.InRange(p.R, 127, 128);
                Assert.Equal(p.R, p.G);
                Assert.Equal(p.R, p.B);
                Assert.Equal(170, p.A);
            }
        }
    }

    [Theory]
    [InlineData(180, true, 0.5 + (3.0 / 255))] // light from the left: a slope rising to the right faces it
    [InlineData(0, true, 0.5 - (3.0 / 255))]
    [InlineData(90, false, 0.5 + (3.0 / 255))] // light from the top: a slope rising downward faces it
    [InlineData(270, false, 0.5 - (3.0 / 255))]
    public void EmbossTakesTheSlopeTowardTheLight(float angle, bool horizontal, double expected)
    {
        using Raster<Rgba8> ramp = Raster.Create<Rgba8>(40, 40);
        for (int y = 0; y < 40; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                byte v = (byte)(60 + (3 * (horizontal ? x : y)));
                ramp.Row(y)[x] = new Rgba8(v, v, v, 255);
            }
        }

        ramp.Apply(o => o.Emboss(angle, depth: 1, sigma: 1), Ct);
        for (int y = 5; y < 35; y++)
        {
            for (int x = 5; x < 35; x++)
            {
                Assert.InRange(ramp.Row(y)[x].R, (expected * 255) - 0.6, (expected * 255) + 0.6);
            }
        }
    }

    [Fact]
    public void ShadeOfAFlatImageIsTheSineOfTheElevation()
    {
        using Raster<Rgba8> flat = Raster.Create<Rgba8>(12, 9);
        flat.Apply(o => o.FillColor(new Rgba8(90, 90, 90, 255)), Ct);
        using Raster<Rgba8> gray = flat.Derive(o => o.Shade(77, 45), Ct);
        using Raster<Rgba8> colored = flat.Derive(o => o.Shade(77, 45, colored: true), Ct);
        Assert.Equal(new Rgba8(180, 180, 180, 255), gray.Row(4)[6]);
        Assert.Equal(new Rgba8(64, 64, 64, 255), colored.Row(4)[6]);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentNullException>(() => image.Apply(o => o.Lighting(null!, LightingModel.Diffuse()), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightingModel.Diffuse(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightingModel.Specular(1, 0.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => LightingModel.Specular(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistantLight(0, 91));
        Assert.Throws<ArgumentOutOfRangeException>(() => new DistantLight(float.PositiveInfinity, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PointLight(new Vector3(0, float.NaN, 1)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpotLight(Vector3.One, Vector3.One));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpotLight(Vector3.Zero, Vector3.One, coneAngle: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SpotLight(Vector3.Zero, Vector3.One, exponent: -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new LightingOptions { SurfaceScale = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LightingOptions { Height = (HeightSource)7 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new LightingOptions { Blend = (BlendMode)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Apply(o => o.Emboss(depth: -1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Apply(o => o.Emboss(sigma: 101), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Apply(o => o.Emboss(float.NaN), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Apply(o => o.Shade(elevation: 91), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => image.Apply(o => o.Shade(float.NaN), Ct));
    }

    /// <summary>SVG 1.1's normal at (x, y), from its table of interior, edge and corner stencils as written there.</summary>
    internal static (double X, double Y) SvgNormal(double[,] field, int x, int y, double s)
    {
        int h = field.GetLength(0), w = field.GetLength(1);
        double I(int dx, int dy) => field[y + dy, x + dx];
        bool top = y == 0, bottom = y == h - 1, left = x == 0, right = x == w - 1;
        const double third = 1.0 / 3, half = 0.5, quarter = 0.25, twoThirds = 2.0 / 3;
        if (top && left)
        {
            return (-s * twoThirds * ((2 * I(1, 0)) + I(1, 1) - (2 * I(0, 0)) - I(0, 1)), -s * twoThirds * ((2 * I(0, 1)) + I(1, 1) - (2 * I(0, 0)) - I(1, 0)));
        }

        if (top && right)
        {
            return (-s * twoThirds * ((2 * I(0, 0)) + I(0, 1) - (2 * I(-1, 0)) - I(-1, 1)), -s * twoThirds * (I(-1, 1) + (2 * I(0, 1)) - I(-1, 0) - (2 * I(0, 0))));
        }

        if (bottom && left)
        {
            return (-s * twoThirds * (I(1, -1) + (2 * I(1, 0)) - I(0, -1) - (2 * I(0, 0))), -s * twoThirds * ((2 * I(0, 0)) + I(1, 0) - (2 * I(0, -1)) - I(1, -1)));
        }

        if (bottom && right)
        {
            return (-s * twoThirds * (I(0, -1) + (2 * I(0, 0)) - I(-1, -1) - (2 * I(-1, 0))), -s * twoThirds * (I(-1, 0) + (2 * I(0, 0)) - I(-1, -1) - (2 * I(0, -1))));
        }

        if (top)
        {
            return (-s * third * ((2 * I(1, 0)) + I(1, 1) - (2 * I(-1, 0)) - I(-1, 1)), -s * half * (I(-1, 1) + (2 * I(0, 1)) + I(1, 1) - I(-1, 0) - (2 * I(0, 0)) - I(1, 0)));
        }

        if (bottom)
        {
            return (-s * third * (I(1, -1) + (2 * I(1, 0)) - I(-1, -1) - (2 * I(-1, 0))), -s * half * (I(-1, 0) + (2 * I(0, 0)) + I(1, 0) - I(-1, -1) - (2 * I(0, -1)) - I(1, -1)));
        }

        if (left)
        {
            return (-s * half * (I(1, -1) + (2 * I(1, 0)) + I(1, 1) - I(0, -1) - (2 * I(0, 0)) - I(0, 1)), -s * third * ((2 * I(0, 1)) + I(1, 1) - (2 * I(0, -1)) - I(1, -1)));
        }

        if (right)
        {
            return (-s * half * (I(0, -1) + (2 * I(0, 0)) + I(0, 1) - I(-1, -1) - (2 * I(-1, 0)) - I(-1, 1)), -s * third * (I(-1, 1) + (2 * I(0, 1)) - I(-1, -1) - (2 * I(0, -1))));
        }

        return (
            -s * quarter * (I(1, -1) + (2 * I(1, 0)) + I(1, 1) - I(-1, -1) - (2 * I(-1, 0)) - I(-1, 1)),
            -s * quarter * (I(-1, 1) + (2 * I(0, 1)) + I(1, 1) - I(-1, -1) - (2 * I(0, -1)) - I(1, -1)));
    }

    /// <summary>The light map by SVG 1.1's formulas, per pixel in double precision, with Skia's pixel centers and soft cone edge.</summary>
    private static Raster<Rgba8> Reference(Raster<Rgba8> image, Light light, LightingModel model, double scale, bool luminance)
    {
        int w = image.Width, h = image.Height;
        double[,] field = new double[h, w];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgba8 p = image.Row(y)[x];
                field[y, x] = luminance ? Math.Clamp(((0.212656 * p.R) + (0.715158 * p.G) + (0.072186 * p.B)) / 255, 0, 1) : p.A / 255.0;
            }
        }

        Raster<Rgba8> result = Raster.Create<Rgba8>(w, h);
        double k = model.Constant;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                (double nx, double ny) = SvgNormal(field, x, y, scale);
                double length = Math.Sqrt((nx * nx) + (ny * ny) + 1);
                (double Nx, double Ny, double Nz) = (nx / length, ny / length, 1 / length);
                double z = scale * field[y, x];
                (double lx, double ly, double lz) = light switch
                {
                    DistantLight d => (Math.Cos(Rad(d.Azimuth)) * Math.Cos(Rad(d.Elevation)), Math.Sin(Rad(d.Azimuth)) * Math.Cos(Rad(d.Elevation)), Math.Sin(Rad(d.Elevation))),
                    PointLight p => Unit(p.Position.X - (x + 0.5), p.Position.Y - (y + 0.5), p.Position.Z - z),
                    SpotLight s => Unit(s.Position.X - (x + 0.5), s.Position.Y - (y + 0.5), s.Position.Z - z),
                    _ => throw new InvalidOperationException(),
                };
                double spot = 1;
                if (light is SpotLight sl)
                {
                    (double sx, double sy, double sz) = Unit(sl.Target.X - sl.Position.X, sl.Target.Y - sl.Position.Y, sl.Target.Z - sl.Position.Z);
                    double cos = -((lx * sx) + (ly * sy) + (lz * sz));
                    double cone = Math.Cos(Rad(sl.ConeAngle));
                    spot = cos < cone ? 0 : (cos > 0 ? Math.Pow(cos, sl.Exponent) : (sl.Exponent == 0 ? 1 : 0)) * (cos < cone + 0.016 ? (cos - cone) / 0.016 : 1);
                }

                double factor;
                if (model.IsSpecular)
                {
                    (double hx, double hy, double hz) = Unit(lx, ly, lz + 1);
                    factor = Math.Pow(Math.Max(0, (Nx * hx) + (Ny * hy) + (Nz * hz)), model.Shininess);
                }
                else
                {
                    factor = (Nx * lx) + (Ny * ly) + (Nz * lz);
                }

                double r = Math.Clamp(k * light.Color.R / 255 * spot * factor, 0, 1);
                double g = Math.Clamp(k * light.Color.G / 255 * spot * factor, 0, 1);
                double b = Math.Clamp(k * light.Color.B / 255 * spot * factor, 0, 1);
                double a = model.IsSpecular ? Math.Max(r, Math.Max(g, b)) : 1;
                result.Row(y)[x] = a == 0 ? default : new Rgba8(ToByte(r / a), ToByte(g / a), ToByte(b / a), ToByte(a));
            }
        }

        return result;

        static double Rad(double degrees) => degrees * Math.PI / 180;

        static (double, double, double) Unit(double x, double y, double z)
        {
            double length = Math.Sqrt((x * x) + (y * y) + (z * z));
            return length == 0 ? (0, 0, 0) : (x / length, y / length, z / length);
        }
    }

    private static byte ToByte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
}
