using System.Numerics;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

public class GradientTests
{
    private static readonly RgbaF32 Red = new(1, 0, 0, 1);
    private static readonly RgbaF32 Green = new(0, 1, 0, 1);
    private static readonly RgbaF32 Blue = new(0, 0, 1, 1);

    /// <summary>A gradient from x = 0.5 to 100.5 gives pixel x the t of x / 100 exactly at its center.</summary>
    [Fact]
    public void StopsHitTheirColorsExactlyAtTheirOffsets()
    {
        LinearGradient gradient = new(new Vector2(0.5f, 0), new Vector2(100.5f, 0), new GradientStop(0, Red), new GradientStop(0.25f, Green), new GradientStop(1, Blue));
        RgbaF32[] row = Row(gradient, 101);
        Close(Red, row[0]);
        Close(Green, row[25]);
        Close(Blue, row[100]);
        Close(Lerp(Red, Green, 0.48f), row[12], 1e-5f);
        Close(Lerp(Green, Blue, 0.37f / 0.75f), row[62], 1e-5f);
    }

    [Fact]
    public void EqualOffsetsMakeAHardEdgeAndTheEndsRepeat()
    {
        LinearGradient gradient = new(new Vector2(0.5f, 0), new Vector2(100.5f, 0), new GradientStop(0.2f, Red), new GradientStop(0.5f, Red), new GradientStop(0.5f, Blue), new GradientStop(0.8f, Blue));
        RgbaF32[] row = Row(gradient, 101);
        Close(Red, row[0]);
        Close(Red, row[49]);
        Close(Blue, row[50]);
        Close(Blue, row[100]);

        // An offset below the one before it counts as equal to it.
        LinearGradient backwards = new(new Vector2(0.5f, 0), new Vector2(100.5f, 0), new GradientStop(0.6f, Red), new GradientStop(0.3f, Blue));
        RgbaF32[] b = Row(backwards, 101);
        Close(Red, b[59]);
        Close(Blue, b[60]);
    }

    [Fact]
    public void TileModesMatchTheirDefinitionsOnGeneratedRows()
    {
        // t runs from −1 to 2 across 301 pixels.
        foreach (TileMode mode in Enum.GetValues<TileMode>())
        {
            LinearGradient gradient = new(new Vector2(100.5f, 0), new Vector2(200.5f, 0), Red, Blue) { Tile = mode };
            RgbaF32[] row = Row(gradient, 301);
            for (int x = 0; x < 301; x += 7)
            {
                float t = (x - 100) / 100f;
                float? expected = mode switch
                {
                    TileMode.Clamp => Math.Clamp(t, 0, 1),
                    TileMode.Repeat => t - MathF.Floor(t),
                    TileMode.Mirror => 1 - MathF.Abs(((t % 2) + 2) % 2 - 1),
                    _ => t is >= 0 and <= 1 ? t : null,
                };
                if (expected is float u)
                {
                    Close(Lerp(Red, Blue, u), row[x], 0.002f);
                }
                else
                {
                    Assert.Equal(0, row[x].A);
                }
            }
        }
    }

    [Fact]
    public void AClampedGradientWithoutExtendLeavesThatSideTransparent()
    {
        LinearGradient gradient = new(new Vector2(100.5f, 0), new Vector2(200.5f, 0), Red, Blue) { ExtendStart = false };
        RgbaF32[] row = Row(gradient, 301);
        Assert.Equal(0, row[50].A);
        Close(Red, row[100]);
        Close(Blue, row[250]);
        RgbaF32[] end = Row(gradient with { ExtendStart = true, ExtendEnd = false }, 301);
        Close(Red, end[50]);
        Assert.Equal(0, end[250].A);
    }

    [Fact]
    public void OklabAndOklchMidpointsMatchTheColorConversions()
    {
        LinearGradient gradient = new(new Vector2(0.5f, 0), new Vector2(100.5f, 0), Red, Blue) { Interpolation = GradientInterpolation.Oklab };
        RgbaF32 mid = Row(gradient, 101)[50];
        OklabColor a = OklabColor.From(Red), b = OklabColor.From(Blue);
        RgbaF32 expected = new OklabColor((a.L + b.L) / 2, (a.A + b.A) / 2, (a.B + b.B) / 2).ToRgbaF32();
        Close(Clamp(expected), mid, 1e-4f);

        OklchColor ca = OklchColor.From(Red), cb = OklchColor.From(Blue);
        foreach (HueMethod method in Enum.GetValues<HueMethod>())
        {
            RgbaF32 m = Row(gradient with { Interpolation = GradientInterpolation.Oklch(method) }, 101)[50];
            float h1 = ca.H, h2 = cb.H, d = h2 - h1;
            (h1, h2) = method switch
            {
                HueMethod.Shorter when d > 180 => (h1 + 360, h2),
                HueMethod.Shorter when d < -180 => (h1, h2 + 360),
                HueMethod.Longer when d > 0 && d < 180 => (h1 + 360, h2),
                HueMethod.Longer when d > -180 && d <= 0 => (h1, h2 + 360),
                HueMethod.Increasing when d < 0 => (h1, h2 + 360),
                HueMethod.Decreasing when d > 0 => (h1 + 360, h2),
                _ => (h1, h2),
            };
            RgbaF32 lch = new OklchColor((ca.L + cb.L) / 2, (ca.C + cb.C) / 2, ((h1 + h2) / 2) % 360).ToRgbaF32();
            Close(Clamp(lch), m, 1e-4f);
        }

        // Linear-light mixing gives a brighter midpoint than encoded mixing.
        RgbaF32 linear = Row(gradient with { Interpolation = GradientInterpolation.LinearSrgb }, 101)[50];
        Assert.InRange(linear.R, 0.73f, 0.74f);
    }

    [Fact]
    public void PremultipliedMixingNeverDarkensTowardATransparentColor()
    {
        RgbaF32 clearBlue = new(0, 0, 1, 0);
        LinearGradient gradient = new(new Vector2(0.5f, 0), new Vector2(100.5f, 0), Red, clearBlue);
        RgbaF32 mid = Row(gradient, 101)[50];
        Close(new RgbaF32(1, 0, 0, 0.5f), mid);

        RgbaF32 straight = Row(gradient with { Premultiplied = false }, 101)[50];
        Close(new RgbaF32(0.5f, 0, 0.5f, 0.5f), straight);
    }

    [Fact]
    public void AConicalGradientWithOneCenterIsARadialGradient()
    {
        Vector2 c = new(40, 30);
        RgbaF32[] radial = Pixels(new RadialGradient(c, 25, Red, Blue), 80, 60);
        RgbaF32[] conical = Pixels(new ConicalGradient(c, 0, c, 25, new GradientStop(0, Red), new GradientStop(1, Blue)), 80, 60);
        for (int i = 0; i < radial.Length; i++)
        {
            Close(radial[i], conical[i], 1e-4f);
        }
    }

    [Fact]
    public void AConicalGradientTakesTheLargerRootAndHonorsExtend()
    {
        // A focal point inside the end circle: every pixel has a t, the focal point t = 0, the circle's edge t = 1.
        ConicalGradient focal = new(new Vector2(30, 40), 0, new Vector2(40, 40), 30, new GradientStop(0, Red), new GradientStop(1, Blue));
        RgbaF32[] pixels = Pixels(focal, 80, 80);
        Close(Red, pixels[(40 * 80) + 30], 0.05f);
        Close(Blue, pixels[(40 * 80) + 70], 0.05f);
        Assert.All(pixels, p => Assert.Equal(1, p.A, 3));

        // Two disjoint circles, not extended: only the cone between them draws.
        ConicalGradient cone = new(new Vector2(15, 40), 5, new Vector2(65, 40), 10, new GradientStop(0, Red), new GradientStop(1, Blue)) { ExtendStart = false, ExtendEnd = false };
        RgbaF32[] c = Pixels(cone, 80, 80);
        Assert.Equal(1, c[(40 * 80) + 40].A, 3);
        Assert.Equal(0, c[(5 * 80) + 40].A);
        Assert.Equal(0, c[(40 * 80) + 2].A);
        Assert.Equal(0, c[(40 * 80) + 78].A);
    }

    [Fact]
    public void ASweepGradientTurnsClockwiseFromTheXAxis()
    {
        SweepGradient sweep = new(new Vector2(50, 50), new GradientStop(0, Red), new GradientStop(1, Blue));
        RgbaF32[] pixels = Pixels(sweep, 100, 100);

        // Straight below the center is a quarter turn on a y-down screen.
        RgbaF32 below = pixels[(90 * 100) + 50];
        Close(Lerp(Red, Blue, 0.25f), below, 0.01f);
        RgbaF32 left = pixels[(50 * 100) + 10];
        Close(Lerp(Red, Blue, 0.5f), left, 0.01f);

        SweepGradient half = new(new Vector2(50, 50), 0, 180, new GradientStop(0, Red), new GradientStop(1, Blue));
        Close(Lerp(Red, Blue, 0.5f), Pixels(half, 100, 100)[(90 * 100) + 50], 0.01f);
    }

    [Fact]
    public void AGradientWithNoExtentDrawsAsSkiaDoes()
    {
        GradientStop[] stops = [new(0, Red), new(1, Blue)];
        Close(Blue, Row(new LinearGradient(new Vector2(5, 0), new Vector2(5, 0), stops), 10)[3]);
        Close(Lerp(Red, Blue, 0.5f), Row(new LinearGradient(new Vector2(5, 0), new Vector2(5, 0), stops) { Tile = TileMode.Repeat }, 10)[3], 0.002f);
        Assert.Equal(0, Row(new RadialGradient(new Vector2(5, 0), 0, stops) { Tile = TileMode.Decal }, 10)[3].A);
    }

    [Fact]
    public void TheLocalMatrixStretchesARadialGradientIntoAnEllipse()
    {
        RadialGradient gradient = new(Vector2.Zero, 10, Red, Blue) { LocalMatrix = Matrix3x2.CreateScale(3, 1) * Matrix3x2.CreateTranslation(50, 20) };
        RgbaF32[] pixels = Pixels(gradient, 100, 40);

        // Pixel (65, 20)'s center maps to paint (31 / 6, 0.5), and pixel (50, 25)'s to (1 / 6, 5.5).
        Close(Lerp(Red, Blue, new Vector2(31 / 6f, 0.5f).Length() / 10), pixels[(20 * 100) + 65], 1e-4f);
        Close(Lerp(Red, Blue, new Vector2(1 / 6f, 5.5f).Length() / 10), pixels[(25 * 100) + 50], 1e-4f);
    }

    [Fact]
    public void EightBitTargetsReadTheTableWithinOneLevelOfTheExactMix()
    {
        RadialGradient gradient = new(new Vector2(30, 30), 28, new GradientStop(0, Red), new GradientStop(0.4f, new RgbaF32(1, 1, 0, 0.5f)), new GradientStop(1, Blue))
        {
            Interpolation = GradientInterpolation.Oklab,
            Tile = TileMode.Mirror,
        };
        RgbaF32[] exact = Pixels(gradient, 60, 60);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(60, 60);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.DrawPaint(gradient with { BlendMode = Operations.BlendMode.Source });
        }

        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 60; x++)
            {
                Rgba8 a = raster.GetPixel(x, y), b = exact[(y * 60) + x].ToRgba8();
                Assert.True(Math.Abs(a.R - b.R) <= 2 && Math.Abs(a.G - b.G) <= 2 && Math.Abs(a.B - b.B) <= 2 && Math.Abs(a.A - b.A) <= 1, $"({x}, {y}): {a} against {b}");
            }
        }
    }

    [Fact]
    public void DitheringNeverChangesAFlatLevelAndStaysWithinALevel()
    {
        Rgba8 level = new(100, 150, 200, 255);
        LinearGradient flat = new(Vector2.Zero, new Vector2(64, 0), RgbaF32.FromRgba8(level), RgbaF32.FromRgba8(level)) { Dither = true };
        using Raster<Rgba8> a = Raster.Create<Rgba8>(64, 16);
        using (RasterCanvas<Rgba8> canvas = a.CreateCanvas())
        {
            canvas.DrawPaint(flat);
        }

        for (int x = 0; x < 64; x++)
        {
            Assert.Equal(level, a.GetPixel(x, 5));
        }

        // A shallow ramp: dithered and plain differ by at most one level, and the dithered one varies within each band.
        LinearGradient ramp = new(Vector2.Zero, new Vector2(256, 0), new RgbaF32(0.2f, 0.2f, 0.2f, 1), new RgbaF32(0.25f, 0.25f, 0.25f, 1));
        using Raster<Rgba8> plain = Raster.Create<Rgba8>(256, 8), dithered = Raster.Create<Rgba8>(256, 8);
        using (RasterCanvas<Rgba8> canvas = plain.CreateCanvas())
        {
            canvas.DrawPaint(ramp);
        }

        using (RasterCanvas<Rgba8> canvas = dithered.CreateCanvas())
        {
            canvas.DrawPaint(ramp with { Dither = true });
        }

        int differ = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 256; x++)
            {
                int d = Math.Abs(plain.GetPixel(x, y).R - dithered.GetPixel(x, y).R);
                Assert.True(d <= 1);
                differ += d;
            }
        }

        Assert.True(differ > 100);
    }

    [Fact]
    public void ShadingIsTheSameAtOneBandAndAtMany()
    {
        Paint[] paints =
        [
            new LinearGradient(new Vector2(3, 7), new Vector2(500, 300), Red, Blue) { Tile = TileMode.Mirror },
            new ConicalGradient(new Vector2(100, 100), 10, new Vector2(300, 200), 150, new GradientStop(0, Red), new GradientStop(1, Green)) { Dither = true },
            new SweepGradient(new Vector2(256, 256), new GradientStop(0, Red), new GradientStop(1, Blue)),
        ];
        foreach (Paint paint in paints)
        {
            using Raster<Rgba8> one = Raster.Create<Rgba8>(512, 512), many = Raster.Create<Rgba8>(512, 512);
            using (RasterCanvas<Rgba8> canvas = one.CreateCanvas())
            {
                canvas.ForcedBands = 1;
                canvas.FillCircle(new Vector2(256, 256), 240, paint);
            }

            using (RasterCanvas<Rgba8> canvas = many.CreateCanvas())
            {
                canvas.ForcedBands = 7;
                canvas.FillCircle(new Vector2(256, 256), 240, paint);
            }

            Assert.True(one.Row(300).SequenceEqual(many.Row(300)) && one.Row(17).SequenceEqual(many.Row(17)) && one.Row(480).SequenceEqual(many.Row(480)));
        }
    }

    [Fact]
    public void GradientsAreValueEqualAndChecked()
    {
        LinearGradient a = new(Vector2.Zero, Vector2.One, Red, Blue), b = new(Vector2.Zero, Vector2.One, Red, Blue);
        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, b with { Tile = TileMode.Repeat });
        Assert.NotEqual(a, new LinearGradient(Vector2.Zero, Vector2.One, Red, Green));
        Assert.Throws<ArgumentException>(() => new LinearGradient(Vector2.Zero, Vector2.One, ReadOnlySpan<GradientStop>.Empty));
        Assert.Throws<ArgumentOutOfRangeException>(() => new GradientStop(1.5f, Red));
        Assert.Throws<ArgumentOutOfRangeException>(() => new RadialGradient(Vector2.Zero, -1, Red, Blue));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SweepGradient(Vector2.Zero, 90, 90, new GradientStop(0, Red)));
        Assert.Throws<ArgumentException>(() => new LinearGradient(new Vector2(float.NaN, 0), Vector2.One, Red, Blue));
    }

    /// <summary>One float row of a paint drawn source over transparent, as straight colors.</summary>
    internal static RgbaF32[] Row(Paint paint, int width) => Pixels(paint, width, 1);

    internal static RgbaF32[] Pixels(Paint paint, int width, int height)
    {
        using Raster<RgbaF32> raster = Raster.Create<RgbaF32>(width, height);
        using (RasterCanvas<RgbaF32> canvas = raster.CreateCanvas())
        {
            canvas.DrawPaint(paint);
        }

        RgbaF32[] pixels = new RgbaF32[width * height];
        for (int y = 0; y < height; y++)
        {
            raster.Row(y).CopyTo(pixels.AsSpan(y * width, width));
        }

        return pixels;
    }

    internal static void Close(RgbaF32 expected, RgbaF32 actual, float tolerance = 1e-5f)
    {
        bool close = Math.Abs(expected.A - actual.A) <= tolerance
            && (actual.A == 0 || (Math.Abs(expected.R - actual.R) <= tolerance && Math.Abs(expected.G - actual.G) <= tolerance && Math.Abs(expected.B - actual.B) <= tolerance));
        Assert.True(close, $"expected {expected}, got {actual}");
    }

    private static RgbaF32 Lerp(RgbaF32 a, RgbaF32 b, float t) => new(a.R + ((b.R - a.R) * t), a.G + ((b.G - a.G) * t), a.B + ((b.B - a.B) * t), a.A + ((b.A - a.A) * t));

    private static RgbaF32 Clamp(RgbaF32 c) => new(Math.Clamp(c.R, 0, 1), Math.Clamp(c.G, 0, 1), Math.Clamp(c.B, 0, 1), 1);
}
