using System.Numerics;

using Egami.Operations;
using Egami.Operations.Color;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class ColorTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Adjustments() => ["brightness", "contrast", "saturation", "hue", "sepia", "grayscale", "grayscale601", "invert", "opacity"];

    [Theory]
    [MemberData(nameof(Adjustments))]
    public void AdjustmentsFollowTheirFormulas(string name)
    {
        Rgba8[] samples = [new(200, 100, 50, 255), new(10, 240, 130, 77), new(128, 128, 128, 255), new(0, 0, 255, 0), new(255, 255, 255, 255)];
        foreach (Rgba8 sample in samples)
        {
            using Raster<Rgba8> raster = Raster.Create(3, 2, sample);
            raster.Apply(ops => _ = name switch
            {
                "brightness" => ops.Brightness(0.7f),
                "contrast" => ops.Contrast(1.4f),
                "saturation" => ops.Saturation(0.3f),
                "hue" => ops.HueRotate(120),
                "sepia" => ops.Sepia(0.8f),
                "grayscale" => ops.Grayscale(),
                "grayscale601" => ops.Grayscale(GrayscaleMode.Rec601, 0.5f),
                "invert" => ops.Invert(),
                _ => ops.Opacity(0.25f),
            }, Ct);

            double[] expected = Expected(name, sample);
            Rgba8 actual = raster.GetPixel(2, 1);
            byte[] got = [actual.R, actual.G, actual.B, actual.A];
            for (int c = 0; c < 4; c++)
            {
                double want = Math.Clamp(expected[c], 0, 1) * 255;
                Assert.True(Math.Abs(got[c] - want) <= 0.501, $"{name} {sample} channel {c}: {got[c]} vs {want:F3}");
            }
        }
    }

    [Fact]
    public void LinearGrayscaleMatchesTheGrayConversion()
    {
        using Raster<Rgba8> raster = Noise(64, 16);
        using Raster<Gray8> gray = raster.As<Gray8>();
        raster.Apply(ops => ops.Grayscale(colorSpace: ColorSpace.Linear), Ct);
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                Rgba8 p = raster.GetPixel(x, y);
                Assert.Equal(p.R, p.G);
                Assert.Equal(p.R, p.B);
                Assert.InRange(p.R - gray.GetPixel(x, y).L, -1, 1);
            }
        }
    }

    [Fact]
    public void AChainFusesIntoOneMatrixAndMatchesTheSteps()
    {
        static void Chain(RasterOps ops) => ops.Brightness(1.05f).Contrast(0.9f).Saturation(1.1f).HueRotate(20).Sepia(0.2f).Opacity(0.9f);
        using OperationPipeline.Plan plan = OperationPipeline.Build(Chain, new Size(4, 4));
        Assert.Single(plan.Steps);

        // Mid-range values, so no step clips on its own and fusing changes only rounding.
        using Raster<Rgba8> raster = Noise(97, 31, low: 80, high: 170);
        using Raster<Rgba8> fused = raster.Derive(Chain, Ct);
        // The steps run one at a time in float, so they clamp after each step but do not round to 8 bits in between; rounding
        // six times in 8 bits would drift by more than the fused result's error.
        using Raster<RgbaF32> steps = raster.As<RgbaF32>();
        steps.Apply(ops => ops.Brightness(1.05f), Ct);
        steps.Apply(ops => ops.Contrast(0.9f), Ct);
        steps.Apply(ops => ops.Saturation(1.1f), Ct);
        steps.Apply(ops => ops.HueRotate(20), Ct);
        steps.Apply(ops => ops.Sepia(0.2f), Ct);
        steps.Apply(ops => ops.Opacity(0.9f), Ct);
        using Raster<Rgba8> stepsRounded = steps.As<Rgba8>();
        AssertClose(stepsRounded, fused, 1);
    }

    [Fact]
    public void ThenComposesInOrder()
    {
        ColorMatrix a = ColorMatrix.Contrast(1.5f);
        ColorMatrix b = ColorMatrix.Invert();
        ColorMatrix ab = a.Then(b);
        Vector4 v = new(0.2f, 0.4f, 0.9f, 0.5f);
        Vector4 expected = Apply(b, Apply(a, v));
        Vector4 actual = Apply(ab, v);
        Assert.True(Vector4.Distance(expected, actual) < 1e-6f);
        Assert.Equal(ColorMatrix.Identity, ColorMatrix.Identity.Then(ColorMatrix.Identity));
        Assert.Equal(a, a.Then(ColorMatrix.Identity));
        Assert.True(ColorMatrix.Brightness(2).Then(ColorMatrix.Opacity(0.5f)).IsPerChannel);
        Assert.False(ColorMatrix.Saturation(0.5f).IsPerChannel);
        Assert.Equal(1.5f, ColorMatrix.Contrast(1.5f)[0, 0]);
        Assert.Equal(-0.25f, ColorMatrix.Contrast(1.5f)[1, 4]);
    }

    [Fact]
    public void TablesAndMatrixKernelsMatchTheScalarReference()
    {
        Random random = new(9);
        byte[] bytes = new byte[4 * 999];
        random.NextBytes(bytes);
        foreach (ColorMatrix m in new[] { ColorMatrix.Brightness(1.3f).Then(ColorMatrix.Invert(0.3f)).Then(ColorMatrix.Opacity(0.6f)), ColorMatrix.HueRotate(77), ColorMatrix.Sepia() })
        {
            Vector4[] columns = ColorKernels.MemoryOrderColumns(m, [0, 1, 2, 3]);
            byte[] scalar = new byte[bytes.Length];
            byte[] fast = new byte[bytes.Length];
            ColorKernels.Apply4Scalar(bytes, scalar, columns);
            ColorKernels.Apply4(bytes, fast, columns);
            Assert.Equal(scalar, fast);
            if (m.IsPerChannel)
            {
                byte[] table = new byte[bytes.Length];
                ColorKernels.ApplyTables(bytes, table, ColorKernels.BuildTables(columns, 4), 4);
                Assert.Equal(scalar, table);
            }
        }
    }

    [Fact]
    public void PixelTypesAgree()
    {
        static void Chain(RasterOps ops) => ops.HueRotate(45).Contrast(1.2f).Invert(0.1f).Opacity(0.7f);
        using Raster<Rgba8> rgba = Noise(33, 9);
        using Raster<Rgba8> expected = rgba.Derive(Chain, Ct);

        using Raster<Bgra8> bgra = rgba.As<Bgra8>();
        bgra.Apply(Chain, Ct);
        using Raster<Rgba8> fromBgra = bgra.As<Rgba8>();
        AssertClose(expected, fromBgra, 0);

        using Raster<RgbaF32> floats = rgba.As<RgbaF32>();
        floats.Apply(Chain, Ct);
        using Raster<Rgba8> fromFloats = floats.As<Rgba8>();
        AssertClose(expected, fromFloats, 1);

        using Raster<Rgba16> wide = rgba.As<Rgba16>();
        wide.Apply(Chain, Ct);
        using Raster<Rgba8> fromWide = wide.As<Rgba8>();
        AssertClose(expected, fromWide, 1);

        // Without alpha the color math is the same, with alpha read as 1.
        using Raster<Rgba8> opaque = Noise(33, 9, opaque: true);
        using Raster<Rgba8> opaqueExpected = opaque.Derive(Chain, Ct);
        foreach (bool bgr in new[] { false, true })
        {
            using Raster<Rgba8> roundTrip = bgr ? ThroughType<Bgr8>(opaque, Chain) : ThroughType<Rgb8>(opaque, Chain);
            for (int y = 0; y < opaque.Height; y++)
            {
                for (int x = 0; x < opaque.Width; x++)
                {
                    Rgba8 e = opaqueExpected.GetPixel(x, y);
                    Rgba8 a = roundTrip.GetPixel(x, y);
                    Assert.Equal((e.R, e.G, e.B), (a.R, a.G, a.B));
                }
            }
        }

        using Raster<GrayAlpha8> gray = rgba.As<GrayAlpha8>();
        gray.Apply(ops => ops.Invert().Opacity(0.5f), Ct);
        Assert.Equal(255 - rgba.As<GrayAlpha8>().GetPixel(0, 0).L, gray.GetPixel(0, 0).L, 1.0);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Brightness(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Contrast(float.NaN));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Saturation(float.PositiveInfinity));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Sepia(1.5f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Invert(-0.1f));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Opacity(2));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Grayscale((GrayscaleMode)7));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.HueRotate(float.NaN));
        Assert.Throws<ArgumentException>(() => new ColorMatrix(new float[19]));
        Assert.Throws<ArgumentException>(() => new ColorMatrix([.. new float[19], float.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => ColorMatrix.Identity[4, 0]);
    }

    private static Raster<Rgba8> ThroughType<TPixel>(Raster<Rgba8> source, Action<RasterOps> chain)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> typed = source.As<TPixel>();
        typed.Apply(chain, Ct);
        return typed.As<Rgba8>();
    }

    private static double[] Expected(string name, Rgba8 p)
    {
        double r = p.R / 255.0, g = p.G / 255.0, b = p.B / 255.0, a = p.A / 255.0;
        switch (name)
        {
            case "brightness":
                return [r * 0.7, g * 0.7, b * 0.7, a];
            case "contrast":
                return [((r - 0.5) * 1.4) + 0.5, ((g - 0.5) * 1.4) + 0.5, ((b - 0.5) * 1.4) + 0.5, a];
            case "saturation":
                return Mix(0.213, 0.715, 0.072, 0.3);
            case "hue":
                double c = Math.Cos(120 * Math.PI / 180), s = Math.Sin(120 * Math.PI / 180);
                return
                [
                    ((0.213 + (c * 0.787) - (s * 0.213)) * r) + ((0.715 - (c * 0.715) - (s * 0.715)) * g) + ((0.072 - (c * 0.072) + (s * 0.928)) * b),
                    ((0.213 - (c * 0.213) + (s * 0.143)) * r) + ((0.715 + (c * 0.285) + (s * 0.140)) * g) + ((0.072 - (c * 0.072) - (s * 0.283)) * b),
                    ((0.213 - (c * 0.213) - (s * 0.787)) * r) + ((0.715 - (c * 0.715) + (s * 0.715)) * g) + ((0.072 + (c * 0.928) + (s * 0.072)) * b),
                    a,
                ];
            case "sepia":
                double t = 0.2;
                return
                [
                    ((0.393 + (0.607 * t)) * r) + ((0.769 - (0.769 * t)) * g) + ((0.189 - (0.189 * t)) * b),
                    ((0.349 - (0.349 * t)) * r) + ((0.686 + (0.314 * t)) * g) + ((0.168 - (0.168 * t)) * b),
                    ((0.272 - (0.272 * t)) * r) + ((0.534 - (0.534 * t)) * g) + ((0.131 + (0.869 * t)) * b),
                    a,
                ];
            case "grayscale":
                double y = (0.2126 * r) + (0.7152 * g) + (0.0722 * b);
                return [y, y, y, a];
            case "grayscale601":
                double y601 = (0.299 * r) + (0.587 * g) + (0.114 * b);
                return [(r + y601) / 2, (g + y601) / 2, (b + y601) / 2, a];
            case "invert":
                return [1 - r, 1 - g, 1 - b, a];
            default:
                return [r, g, b, a * 0.25];
        }

        double[] Mix(double wr, double wg, double wb, double amount)
        {
            double luma = (wr * r) + (wg * g) + (wb * b);
            return [luma + ((r - luma) * amount), luma + ((g - luma) * amount), luma + ((b - luma) * amount), a];
        }
    }

    private static Vector4 Apply(ColorMatrix m, Vector4 v)
    {
        Span<float> o = stackalloc float[4];
        for (int r = 0; r < 4; r++)
        {
            o[r] = (m[r, 0] * v.X) + (m[r, 1] * v.Y) + (m[r, 2] * v.Z) + (m[r, 3] * v.W) + m[r, 4];
        }

        return new Vector4(o[0], o[1], o[2], o[3]);
    }

    private static Raster<Rgba8> Noise(int width, int height, int low = 0, int high = 256, bool opaque = false)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new(width + (7 * height));
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(low, high), (byte)random.Next(low, high), (byte)random.Next(low, high), opaque ? (byte)255 : (byte)random.Next(low, high));
            }
        }

        return raster;
    }

    private static void AssertClose(Raster<Rgba8> expected, Raster<Rgba8> actual, int tolerance)
    {
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba8 e = expected.GetPixel(x, y);
                Rgba8 a = actual.GetPixel(x, y);
                int diff = Math.Max(Math.Max(Math.Abs(e.R - a.R), Math.Abs(e.G - a.G)), Math.Max(Math.Abs(e.B - a.B), Math.Abs(e.A - a.A)));
                Assert.True(diff <= tolerance, $"({x}, {y}): {e} vs {a}");
            }
        }
    }
}
