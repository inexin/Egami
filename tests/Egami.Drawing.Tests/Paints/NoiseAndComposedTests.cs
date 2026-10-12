using System.Numerics;

using Egami.Drawing.Tests.Canvas;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

public class NoiseAndComposedTests
{
    [Fact]
    public void SvgNoiseIsDeterministicSeededAndInRange()
    {
        RgbaF32[] a = GradientTests.Pixels(NoisePaint.Fractal(0.05f, 0.05f, 4, seed: 3), 64, 64);
        RgbaF32[] b = GradientTests.Pixels(NoisePaint.Fractal(0.05f, 0.05f, 4, seed: 3), 64, 64);
        RgbaF32[] c = GradientTests.Pixels(NoisePaint.Fractal(0.05f, 0.05f, 4, seed: 4), 64, 64);
        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.All(a, p => Assert.InRange(p.A, 0.05f, 0.95f));

        // Turbulence sums absolute values: dark where the noise crosses zero, never below 0.
        RgbaF32[] t = GradientTests.Pixels(NoisePaint.Turbulence(0.05f, 0.05f, 2, seed: 1), 64, 64);
        Assert.Contains(t, p => p.A < 0.1f);
        Assert.All(t, p => Assert.InRange(p.A, 0, 1));
    }

    [Fact]
    public void StitchedSvgNoiseRepeatsOverItsTile()
    {
        // Stitching wraps the lattice once, so the noise repeats over the first two tiles; samples sit one pixel past each corner.
        RgbaF32[] p = GradientTests.Pixels(NoisePaint.Fractal(0.07f, 0.07f, 3, 9, new Size(32, 32)), 64, 64);
        for (int y = 0; y < 31; y++)
        {
            for (int x = 0; x < 31; x++)
            {
                GradientTests.Close(p[(y * 64) + x], p[((y + 32) * 64) + x + 32], 1e-4f);
            }
        }
    }

    [Fact]
    public void SimplexNoiseStaysInRangeAndTilesExactly()
    {
        RgbaF32[] free = GradientTests.Pixels(NoisePaint.Simplex(0.04f, 5, 11), 96, 96);
        double mean = free.Average(p => p.A);
        Assert.InRange(mean, 0.4, 0.6);
        Assert.All(free, p => Assert.InRange(p.A, 0, 1));
        Assert.Equal(free, GradientTests.Pixels(NoisePaint.Simplex(0.04f, 5, 11), 96, 96));

        RgbaF32[] tiled = GradientTests.Pixels(NoisePaint.Simplex(0.1f, 3, 2, new Size(40, 30)), 80, 60);
        for (int y = 0; y < 30; y++)
        {
            for (int x = 0; x < 40; x++)
            {
                GradientTests.Close(tiled[(y * 80) + x], tiled[((y + 30) * 80) + x + 40], 1e-4f);
            }
        }

        // Neighbors differ little: the noise is smooth at this frequency.
        for (int x = 1; x < 96; x++)
        {
            Assert.True(Math.Abs(free[x].A - free[x - 1].A) < 0.15f);
        }
    }

    [Fact]
    public void ABlendPaintEqualsDrawingBothWithTheBlendMode()
    {
        LinearGradient under = new(Vector2.Zero, new Vector2(64, 0), new RgbaF32(1, 0.5f, 0, 1), new RgbaF32(0, 0.2f, 1, 0.6f));
        RadialGradient over = new(new Vector2(32, 32), 30, new RgbaF32(0.2f, 1, 0.3f, 0.9f), new RgbaF32(1, 1, 1, 0.3f));
        foreach (BlendMode mode in new[] { BlendMode.Multiply, BlendMode.Screen, BlendMode.SourceIn, BlendMode.Hue, BlendMode.Xor })
        {
            using Raster<RgbaF32> blended = Raster.Create<RgbaF32>(64, 64), twice = Raster.Create<RgbaF32>(64, 64);
            using (RasterCanvas<RgbaF32> canvas = blended.CreateCanvas())
            {
                canvas.DrawPaint(new BlendPaint(under, over, mode));
            }

            using (RasterCanvas<RgbaF32> canvas = twice.CreateCanvas())
            {
                canvas.DrawPaint(under);
                canvas.DrawPaint(over with { BlendMode = mode });
            }

            for (int y = 0; y < 64; y += 5)
            {
                for (int x = 0; x < 64; x += 3)
                {
                    GradientTests.Close(twice.GetPixel(x, y), blended.GetPixel(x, y), 2e-4f);
                }
            }
        }
    }

    [Fact]
    public void AColorMatrixPaintEqualsTheColorMatrixOperation()
    {
        LinearGradient gradient = new(Vector2.Zero, new Vector2(64, 64), new RgbaF32(1, 0.5f, 0, 1), new RgbaF32(0, 0.2f, 1, 1));
        ColorMatrix matrix = ColorMatrix.Sepia().Then(ColorMatrix.Contrast(1.2f));
        using Raster<Rgba8> painted = Raster.Create<Rgba8>(64, 64), filtered = Raster.Create<Rgba8>(64, 64);
        using (RasterCanvas<Rgba8> canvas = painted.CreateCanvas())
        {
            canvas.DrawPaint(gradient.WithColorMatrix(matrix));
        }

        using (RasterCanvas<Rgba8> canvas = filtered.CreateCanvas())
        {
            canvas.DrawPaint(gradient);
        }

        filtered.Apply(o => o.ColorMatrix(matrix), TestContext.Current.CancellationToken);
        for (int y = 0; y < 64; y += 3)
        {
            for (int x = 0; x < 64; x += 3)
            {
                Rgba8 a = painted.GetPixel(x, y), b = filtered.GetPixel(x, y);
                Assert.True(Math.Abs(a.R - b.R) <= 1 && Math.Abs(a.G - b.G) <= 1 && Math.Abs(a.B - b.B) <= 1, $"({x}, {y}): {a} against {b}");
            }
        }

        Paint solid = new SolidPaint(new RgbaF32(1, 0, 0, 1)).WithColorMatrix(ColorMatrix.Grayscale());
        Assert.IsType<SolidPaint>(solid);
    }

    [Fact]
    public void ACustomPaintIsTheSameAtAnyCoreCount()
    {
        Checker paint = new();
        using Raster<Rgba8> one = Raster.Create<Rgba8>(400, 400), many = Raster.Create<Rgba8>(400, 400);
        using (RasterCanvas<Rgba8> canvas = one.CreateCanvas())
        {
            canvas.ForcedBands = 1;
            canvas.Rotate(10);
            canvas.FillCircle(new Vector2(200, 200), 190, paint);
        }

        using (RasterCanvas<Rgba8> canvas = many.CreateCanvas())
        {
            canvas.ForcedBands = 9;
            canvas.Rotate(10);
            canvas.FillCircle(new Vector2(200, 200), 190, paint);
        }

        for (int y = 0; y < 400; y++)
        {
            Assert.True(one.Row(y).SequenceEqual(many.Row(y)));
        }

        // The checker squares follow the canvas transform: 8 local units apart.
        Assert.NotEqual(one.GetPixel(200, 200), default);
    }

    [Fact]
    public void ANonRasterBackendDrawsACustomPaintThroughItsFallback()
    {
        using TraceCanvas canvas = new(100, 100);
        canvas.FillCircle(new Vector2(50, 50), 30, new Checker());
        Assert.Contains(canvas.Calls, c => c.StartsWith("Fallback 100x100", StringComparison.Ordinal));
    }

    /// <summary>An 8-unit checkerboard in paint coordinates.</summary>
    private sealed record class Checker : CustomPaint
    {
        public override void Shade(int y, int x, Span<PremulRgbaF32> row, in Matrix3x2 inverse)
        {
            for (int i = 0; i < row.Length; i++)
            {
                Vector2 p = Vector2.Transform(new Vector2(x + i + 0.5f, y + 0.5f), inverse);
                bool dark = (((int)MathF.Floor(p.X / 8) + (int)MathF.Floor(p.Y / 8)) & 1) == 0;
                row[i] = dark ? new PremulRgbaF32(0, 0, 0, 1) : new PremulRgbaF32(0.5f, 0.25f, 0, 0.5f);
            }
        }
    }
}
