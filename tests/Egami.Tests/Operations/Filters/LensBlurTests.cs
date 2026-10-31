using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 4: the FFT, the aperture, and LensBlur against direct convolution.</summary>
public class LensBlurTests
{
    private static readonly global::Egami.Colors.LinearCurve Curve = global::Egami.Colors.LinearCurve.Srgb;

    [Theory]
    [InlineData(16)]
    [InlineData(512)]
    public void FftRoundTripsWithin1e5(int n)
    {
        Random random = new(n);
        float[] re = new float[n * n], im = new float[n * n];
        for (int i = 0; i < re.Length; i++)
        {
            re[i] = random.NextSingle();
            im[i] = random.NextSingle();
        }

        float[] r0 = [.. re], i0 = [.. im];
        Fft fft = Fft.For(n);
        fft.Transform2D(re, im, inverse: false);
        fft.Transform2D(re, im, inverse: true);
        float scale = 1f / ((float)n * n);
        for (int i = 0; i < re.Length; i++)
        {
            Assert.InRange(Math.Abs((re[i] * scale) - r0[i]), 0, 1e-5);
            Assert.InRange(Math.Abs((im[i] * scale) - i0[i]), 0, 1e-5);
        }
    }

    [Fact]
    public void FftMatchesTheDefinition()
    {
        int n = 32;
        Random random = new(3);
        float[] re = new float[n], im = new float[n];
        for (int i = 0; i < n; i++)
        {
            re[i] = random.NextSingle() - 0.5f;
            im[i] = random.NextSingle() - 0.5f;
        }

        float[] r0 = [.. re], i0 = [.. im];
        Fft.For(n).Transform(re, im, inverse: false);
        for (int k = 0; k < n; k++)
        {
            double sr = 0, si = 0;
            for (int t = 0; t < n; t++)
            {
                double angle = -2 * Math.PI * k * t / n;
                sr += (r0[t] * Math.Cos(angle)) - (i0[t] * Math.Sin(angle));
                si += (r0[t] * Math.Sin(angle)) + (i0[t] * Math.Cos(angle));
            }

            Assert.Equal(sr, re[k], 1e-4);
            Assert.Equal(si, im[k], 1e-4);
        }
    }

    [Theory]
    [InlineData(10f, 0, 0f)]
    [InlineData(23.5f, 6, 15f)]
    [InlineData(4f, 5, 0f)]
    public void ApertureSumsToOneAndIsSymmetricWhereItShouldBe(float radius, int blades, float rotation)
    {
        float[,] cells = LensBlurOperation.Aperture(radius, blades, rotation);
        double sum = 0;
        foreach (float c in cells)
        {
            sum += c;
        }

        Assert.Equal(1, sum, 1e-5);
        int r = cells.GetLength(0) / 2;
        Assert.True(cells[r, r] > 0);
        Assert.Equal(0f, cells[0, 0]);
        if (blades == 0)
        {
            Assert.Equal(cells[r, 0], cells[0, r]);
        }
    }

    [Theory]
    [InlineData(7)]
    [InlineData(31)]
    public void TilesEqualDirectConvolutionForRandomKernels(int radius)
    {
        // Random, unsymmetric kernels: the tiles must equal a direct convolution, edges repeating the edge pixel.
        Random random = new(radius);
        float[,] cells = new float[(2 * radius) + 1, (2 * radius) + 1];
        float total = 0;
        for (int y = 0; y < cells.GetLength(0); y++)
        {
            for (int x = 0; x < cells.GetLength(1); x++)
            {
                cells[y, x] = random.NextSingle() * random.NextSingle();
                total += cells[y, x];
            }
        }

        for (int y = 0; y < cells.GetLength(0); y++)
        {
            for (int x = 0; x < cells.GetLength(1); x++)
            {
                cells[y, x] /= total;
            }
        }

        using Raster<Rgba8> photo = Load("duck");
        using Raster<RgbaF32> source = photo.As<RgbaF32>();
        using Raster<RgbaF32> result = source.Derive(o => o.Add(new LensBlurOperation(cells, LensBlurOptions.Default with { ColorSpace = ColorSpace.Encoded })), Ct);
        double worst = 0;
        for (int y = 0; y < source.Height; y += 7)
        {
            for (int x = 0; x < source.Width; x += 5)
            {
                RgbaF32 expected = Direct(source, cells, x, y);
                RgbaF32 actual = result.Row(y)[x];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(expected.R - actual.R), Math.Abs(expected.G - actual.G)), Math.Max(Math.Abs(expected.B - actual.B), Math.Abs(expected.A - actual.A))));
            }
        }

        Assert.True(worst < 2e-5, $"worst {worst}");
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData(12f, 0)]
    [InlineData(20f, 6)]
    public void CloseToDirectConvolutionWithTheAperture(float radius, int blades)
    {
        // Gate: PSNR 50 dB to direct 2D convolution with the same aperture, in linear light, on 8-bit output.
        LensBlurOptions options = new() { Blades = blades, Rotation = 10 };
        float[,] cells = LensBlurOperation.Aperture(radius, blades, 10);
        using Raster<Rgba8> photo = Load("egret");
        using Raster<Rgba8> result = photo.Derive(o => o.LensBlur(radius, options), Ct);
        using Raster<RgbaF32> linear = photo.As<RgbaF32>();
        for (int y = 0; y < linear.Height; y++)
        {
            foreach (ref RgbaF32 p in linear.Row(y))
            {
                p = new RgbaF32((float)Curve.ToLinear(p.R), (float)Curve.ToLinear(p.G), (float)Curve.ToLinear(p.B), p.A);
            }
        }

        using Raster<Rgba8> expected = Raster.Create<Rgba8>(photo.Width, photo.Height);
        for (int y = 0; y < photo.Height; y++)
        {
            for (int x = 0; x < photo.Width; x++)
            {
                RgbaF32 v = Direct(linear, cells, x, y);
                static byte Encode(float c) => (byte)Math.Round(Math.Clamp(Curve.FromLinear(c), 0, 1) * 255);
                expected.Row(y)[x] = new Rgba8(Encode(v.R), Encode(v.G), Encode(v.B), (byte)Math.Round(Math.Clamp(v.A, 0, 1) * 255));
            }
        }

        double psnr = Psnr(expected, result);
        Assert.True(psnr >= 50, $"PSNR {psnr:F2} dB");
    }

    [Fact]
    public void APointBecomesTheAperture()
    {
        // One bright pixel in the dark: the blur draws the aperture around it, and nothing outside.
        using Raster<RgbaF32> raster = Raster.Create(101, 101, new RgbaF32(0, 0, 0, 1));
        raster.Row(50)[50] = new RgbaF32(1, 1, 1, 1);
        float[,] cells = LensBlurOperation.Aperture(15, 6, 0);
        raster.Apply(o => o.LensBlur(15, new LensBlurOptions { Blades = 6, ColorSpace = ColorSpace.Encoded }), Ct);
        for (int y = 0; y < 101; y++)
        {
            for (int x = 0; x < 101; x++)
            {
                int dy = y - 50, dx = x - 50;
                float expected = Math.Abs(dx) <= 15 && Math.Abs(dy) <= 15 ? cells[dy + 15, dx + 15] : 0;
                Assert.Equal(expected, raster.Row(y)[x].R, 1e-5);
            }
        }
    }

    [Fact]
    public void HighlightGainBrightensSmallLights()
    {
        using Raster<Rgba8> lights = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "filters", "lights.png"));
        using Raster<Rgba8> plain = lights.Derive(o => o.LensBlur(8), Ct);
        using Raster<Rgba8> lifted = lights.Derive(o => o.LensBlur(8, new LensBlurOptions { HighlightGain = 6, HighlightThreshold = 0.6f }), Ct);

        // Inside the disc around the light at (20, 20) the lifted blur is brighter; far from every light it is the same.
        Assert.True(lifted.Row(20)[24].R > plain.Row(20)[24].R + 20, $"{lifted.Row(20)[24]} against {plain.Row(20)[24]}");
        Assert.Equal(plain.Row(60)[10], lifted.Row(60)[10]);
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("egret", o => o.LensBlur(9, new LensBlurOptions { Blades = 7 }));
        SameInsideArea("duck", new Rect(60, 40, 300, 200), o => o.LensBlur(6));
    }

    [Fact]
    public void RejectsBadOptions()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.LensBlur(129), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.LensBlur(5, new LensBlurOptions { Blades = 4 }), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.LensBlur(5, new LensBlurOptions { HighlightGain = 0.5f }), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(o => o.LensBlur(float.NaN), Ct));
    }

    private static RgbaF32 Direct(Raster<RgbaF32> source, float[,] cells, int x, int y)
    {
        int r = cells.GetLength(0) / 2;
        double cr = 0, cg = 0, cb = 0, ca = 0;
        for (int dy = -r; dy <= r; dy++)
        {
            for (int dx = -r; dx <= r; dx++)
            {
                // Convolution: the tap at offset d reads the pixel at −d, so a point becomes the kernel.
                RgbaF32 p = source.Row(Math.Clamp(y - dy, 0, source.Height - 1))[Math.Clamp(x - dx, 0, source.Width - 1)];
                double w = cells[dy + r, dx + r];
                cr += p.R * p.A * w;
                cg += p.G * p.A * w;
                cb += p.B * p.A * w;
                ca += p.A * w;
            }
        }

        return ca == 0 ? default : new RgbaF32((float)(cr / ca), (float)(cg / ca), (float)(cb / ca), (float)ca);
    }
}
