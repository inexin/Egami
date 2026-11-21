using System.Numerics;

using Egami.Drawing.Tests.Geometry;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Logo mode end to end: edges placed from coverage fields, transparency, refusal of photos, and the stages.</summary>
public class LogoTraceTests
{
    private static readonly Rgba8 Red = new(220, 30, 30, 255);
    private static readonly Rgba8 Blue = new(20, 60, 200, 255);
    private static readonly Rgba8 Yellow = new(250, 200, 0, 255);

    private static readonly TraceOptions Logo = new() { Mode = TraceMode.Logo };

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void LogoFindsTheFlatColors()
    {
        using Raster<Rgba8> raster = ColorTraceTests.Logo(1);
        TraceResult result = raster.Trace(Logo);
        Assert.Equal(4, result.Palette.Count);
        foreach (Rgba8 expected in new[] { Rgba8.White, Red, Blue, Yellow })
        {
            Assert.Contains(result.Palette, c => Distance(c, expected) <= 3);
        }

        Assert.Equal(4, result.Shapes.Count);
        Assert.Equal((120, 100), (result.Width, result.Height));
    }

    [Theory]
    [InlineData(6f)]
    [InlineData(10.3f)]
    [InlineData(25f)]
    public void SmallDiscsComeBackRound(float radius)
    {
        // An antialiased disc at one pixel to the pixel: Logo mode places its edge within a few hundredths of a pixel, closer than
        // color tracing does from the same pixels.
        using Raster<Rgba8> raster = Raster.Create(64, 64, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(31.7f, 32.2f), radius, Blue);
        }

        // The disc as drawn: the rasterizer's own coverage sets its radius, a few hundredths under the one asked for.
        double drawn = Math.Sqrt(Coverage(raster, Blue) / Math.PI);
        double logo = RadiusError(raster.Trace(Logo with { IgnoreWhite = true }), new Vector2(31.7f, 32.2f), drawn);
        double color = RadiusError(raster.Trace(Logo with { Mode = TraceMode.Color, IgnoreWhite = true }), new Vector2(31.7f, 32.2f), drawn);
        Assert.True(logo < 0.06, $"logo {logo}");
        Assert.True(logo <= color + 0.005, $"logo {logo}, color {color}");
    }

    [Fact]
    public void RotatedEdgesLandWhereTheyWereDrawn()
    {
        // A square turned 20 degrees, antialiased: every point of the traced outline lies within the gate's half pixel of the
        // true outline, and on average within a tenth.
        Vector2[] square = [.. new Vector2[] { new(-14, -14), new(14, -14), new(14, 14), new(-14, 14) }
            .Select(v => Vector2.Transform(v, Matrix3x2.CreateRotation(20 * MathF.PI / 180)) + new Vector2(32.3f, 31.6f))];
        using Raster<Rgba8> raster = Raster.Create(64, 64, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillPath(Path.Polygon(square), Red);
        }

        TraceResult result = raster.Trace(Logo with { IgnoreWhite = true });
        TracedShape shape = Assert.Single(result.Shapes);
        List<double> distances = [.. Outline(shape.Path).Select(p => PolygonDistance(square, p))];
        Assert.True(distances.Average() < 0.1, $"mean {distances.Average()}");
        Assert.True(distances.Max() < 0.5, $"max {distances.Max()}");
    }

    [Theory]
    [InlineData(TraceLayering.Stacked)]
    [InlineData(TraceLayering.Abutting)]
    public void TransparencyAroundTheArtIsLeftOut(TraceLayering layering)
    {
        using Raster<Rgba8> raster = Raster.Create(60, 50, Rgba8.Transparent);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(20, 25), 12, Red);
            canvas.FillRect(new RectF(35.5f, 10.25f, 18, 30), Blue);
        }

        TraceResult result = raster.Trace(Logo with { Layering = layering });
        Assert.Equal(2, result.Shapes.Count);
        Assert.DoesNotContain(result.Shapes, s => s.Color == Rgba8.White);

        // Each shape's area is the alpha the art put down.
        double red = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < 34; x++)
            {
                red += raster.GetPixel(x, y).A / 255.0;
            }
        }

        Assert.Equal(red, PathEffectTests.Area(result.ToPath(result.Palette.First(c => Distance(c, Red) <= 3))), 0.5);
        Assert.Equal(18 * 30, PathEffectTests.Area(result.ToPath(result.Palette.First(c => Distance(c, Blue) <= 3))), 1.0);
    }

    [Fact]
    public void EnclosedTransparencyIsPaintedWhite()
    {
        // A red ring on transparency: its hole is traced as white, unless white is ignored, when the hole stays empty.
        using Raster<Rgba8> raster = Raster.Create(60, 60, Rgba8.Transparent);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            PathBuilder ring = new();
            ring.AddCircle(new Vector2(30, 30), 20);
            ring.AddCircle(new Vector2(30, 30), 10, PathDirection.CounterClockwise);
            canvas.FillPath(ring.ToPath(FillRule.EvenOdd), Red);
        }

        TraceResult painted = raster.Trace(Logo);
        TracedShape white = Assert.Single(painted.Shapes, s => s.Color == Rgba8.White);
        double hole = 0;
        for (int y = 15; y < 45; y++)
        {
            for (int x = 15; x < 45; x++)
            {
                hole += 1 - (raster.GetPixel(x, y).A / 255.0);
            }
        }

        // The rasterizer leaves a faint tint inside the hole's edge, which takes a little of it.
        Assert.Equal(hole, PathEffectTests.Area(white.Path), hole * 0.025);
        Assert.False(painted.ToPath(painted.Palette.First(c => Distance(c, Red) <= 3)).Contains(new Vector2(-5, 30)));

        TraceResult ignored = raster.Trace(Logo with { IgnoreWhite = true });
        TracedShape red = Assert.Single(ignored.Shapes);
        Assert.False(red.Path.Contains(new Vector2(30, 30)));
        Assert.True(red.Path.Contains(new Vector2(30, 15)));
    }

    [Fact]
    public void PhotosAreRefused()
    {
        using Raster<Rgba8> gradient = Raster.Create(80, 60, Rgba8.White);
        for (int y = 0; y < 60; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                gradient.SetPixel(x, y, new Rgba8((byte)(3 * x), (byte)(4 * y), (byte)(x + y), 255));
            }
        }

        ArgumentException refused = Assert.Throws<ArgumentException>(() => gradient.Trace(Logo));
        Assert.Equal("raster", refused.ParamName);
        Assert.Contains("TraceMode.Color", refused.Message, StringComparison.Ordinal);
        Assert.NotEmpty(gradient.Trace(Logo with { Mode = TraceMode.Color }).Shapes);
    }

    [Fact]
    public void CompressedLogosAreFlatEnough()
    {
        // JPEG's ringing and blurred chroma leave tints and fringes; they neither refuse the image nor become colors of their own.
        using Raster<Rgba8> source = ColorTraceTests.Logo(3);
        using MemoryStream stream = new();
        source.Save(stream, "jpeg");
        stream.Position = 0;
        using Raster jpeg = Raster.Load(stream);
        TraceResult result = jpeg.Trace(Logo);
        Assert.True(result.Palette.Count == 4, string.Join(", ", result.Shapes.Select(s => s.Color)));
        Assert.InRange(result.Shapes.Count, 4, 6);
    }

    [Fact]
    public void AbuttingShapesShareTheirEdges()
    {
        using Raster<Rgba8> raster = ColorTraceTests.Logo(2);
        TraceResult result = raster.Trace(Logo with { Layering = TraceLayering.Abutting });
        Assert.Equal(raster.Width * raster.Height, result.Shapes.Sum(s => PathEffectTests.Area(s.Path)), 1e-2);
        Assert.False(result.ToPath(result.Palette.First(c => Distance(c, Red) <= 3)).Contains(new Vector2(70 * 2, 50 * 2)));
    }

    [Fact]
    public void FixedPaletteColorsTheShapes()
    {
        using Raster<Rgba8> raster = ColorTraceTests.Logo(1);
        Rgba8[] palette = [Rgba8.White, new(200, 0, 0, 255), new(0, 0, 200, 255), new(255, 255, 0, 255)];
        TraceResult result = raster.Trace(Logo with { Palette = palette });
        Assert.All(result.Shapes, s => Assert.Contains(s.Color, palette));
        Assert.Equal(4, result.Shapes.Count);
    }

    [Fact]
    public void TracingIsTheSameEveryTime()
    {
        using Raster<Rgba8> raster = ColorTraceTests.Logo(2);
        foreach (TraceLayering layering in new[] { TraceLayering.Stacked, TraceLayering.Abutting })
        {
            TraceOptions options = Logo with { Layering = layering };
            TraceResult a = raster.Trace(options), b = raster.Trace(options);
            Assert.Equal(a.Shapes.Select(s => (s.Color, s.Path.ToSvg())), b.Shapes.Select(s => (s.Color, s.Path.ToSvg())));
        }
    }

    [Fact]
    public async Task LogoTraceCancels()
    {
        using Raster<Rgba8> raster = ColorTraceTests.Logo(2);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => raster.TraceAsync(Logo, null, cancelled.Token));
        TraceResult result = await raster.TraceAsync(Logo, null, Ct);
        Assert.NotEmpty(result.Shapes);
    }

    [Fact]
    public void OtherPixelTypesAndTinyImages()
    {
        using Raster<Rgba8> source = ColorTraceTests.Logo(1);
        using Raster<RgbaF32> wide = source.As<RgbaF32>();
        Assert.Equal(4, wide.Trace(Logo).Palette.Count);
        using Raster<Gray8> gray = source.As<Gray8>();
        Assert.NotEmpty(gray.Trace(Logo).Shapes);

        using Raster<Rgba8> one = Raster.Create(1, 1, Red);
        TracedShape shape = Assert.Single(one.Trace(Logo).Shapes);
        Assert.Equal(1, PathEffectTests.Area(shape.Path), 1e-3);
        using Raster<Rgba8> clear = Raster.Create(8, 8, Rgba8.Transparent);
        Assert.Empty(clear.Trace(Logo).Shapes);
    }

    [Fact]
    public void TinyLoopsWithALongReachKeepTheirCorners()
    {
        // A closed run of 10 points, fewer than the corner reach of a 4x finer grid: the reach must not wrap past the run.
        Vec[] run = [new(0, 0), new(1, 0), new(2, 0), new(3, 0), new(3, 1), new(3, 2), new(2, 2), new(1, 2), new(0, 2), new(0, 1)];
        List<int> corners = CubicFitter.Corners(run, true, Math.PI / 3, 12);
        Assert.Subset(corners.ToHashSet(), new HashSet<int> { 0, 3, 5, 8 });
        Assert.All(corners, c => Assert.InRange(c, 0, run.Length - 1));
    }

    [Fact]
    public void TinySpecksTraceWithNoNoiseFilter()
    {
        // Specks of a pixel or two, faint and strong, on a small image traced 4x finer with every region kept.
        using Raster<Rgba8> raster = Raster.Create(20, 20, Rgba8.White);
        for (int k = 0; k < 16; k++)
        {
            byte level = (byte)(255 - (k * 15));
            Rgba8 speck = new(level, (byte)(30 + (k * 14)), 30, 255);
            int x = 1 + ((k % 4) * 5), y = 1 + ((k / 4) * 5);
            raster.SetPixel(x, y, speck);
            if (k % 2 == 1)
            {
                raster.SetPixel(x + 1, y, Red);
            }
        }

        foreach (TraceLayering layering in new[] { TraceLayering.Stacked, TraceLayering.Abutting })
        {
            TraceResult result = raster.Trace(Logo with { Noise = 0, Colors = 16, Layering = layering });
            Assert.NotEmpty(result.Shapes);
        }
    }

    [Fact]
    public void PixelArtKeepsItsAreaWhenAbutting()
    {
        Random random = new(5);
        Rgba8[] colors = [Rgba8.White, Red, Blue, Yellow];
        using Raster<Rgba8> raster = Raster.Create(24, 20, Rgba8.White);
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                raster.SetPixel(x, y, colors[random.Next(4)]);
            }
        }

        TraceResult result = raster.Trace(Logo with { Palette = colors, Noise = 0, Layering = TraceLayering.Abutting });
        Assert.Equal(24 * 20, result.Shapes.Sum(s => PathEffectTests.Area(s.Path)), 1e-2);
    }

    [Fact]
    public void ThePrefilteredSplinePassesThroughTheSamples()
    {
        float[] samples = [0, 0, 1, 1, 0.25f, 0, 1, 0.6f];
        float[] c = [.. samples];
        CoverageField.Prefilter(c);
        for (int k = 0; k < samples.Length; k++)
        {
            // Mirrored ends, as the prefilter assumes.
            double value = 0;
            for (int m = k - 2; m <= k + 2; m++)
            {
                int i = m < 0 ? -m : m >= c.Length ? (2 * (c.Length - 1)) - m : m;
                value += c[i] * CoverageField.Beta3(k - m);
            }

            Assert.Equal(samples[k], value, 4);
        }
    }

    [Fact]
    public void FieldsSampleFinerAndLeadByTheirMargin()
    {
        // Six black pixels, one a third black, five white: the edge lies a third of the way into the seventh pixel, so the 25th
        // of the 48 fine samples along the row (at 6.125) is black and the 26th on (from 6.375) white.
        byte[] labels = new byte[36];
        float[] weights = new float[36];
        for (int i = 0; i < 12; i++)
        {
            (labels[3 * i], weights[3 * i]) = i < 6 ? ((byte)0, 1f) : ((byte)1, i == 6 ? 2 / 3f : 1f);
        }

        (labels[19], weights[19]) = (0, 1 / 3f);
        (byte[] fine, float[] margins) = CoverageField.Sample(12, 1, 2, labels, weights, 4, Ct);
        Assert.Equal(48 * 4, fine.Length);
        Assert.All(fine[..25], f => Assert.Equal(0, f));
        Assert.All(fine[25..48], f => Assert.Equal(1, f));
        Assert.All(margins, m => Assert.True(m >= 0));

        // The leads either side of the crossing place it between the two samples, linearly, where the edge is: at 6⅓.
        double t = margins[24] / (margins[24] + margins[25]);
        Assert.Equal(6 + (1 / 3.0), 6.125 + (0.25 * t), 2);
    }

    [Fact]
    public void PixelsSplitIntoTheColorsTheyBlend()
    {
        Rgba8[] palette = [new(0, 0, 0, 255), new(255, 255, 255, 255)];
        byte[] rgb = [0, 0, 0, 64, 64, 64, 255, 255, 255, 255, 255, 255];
        byte[] alpha = [255, 255, 255, 128];
        byte[] nearest = [0, 0, 1, 1];
        (byte[] labels, float[] weights, long unexplained, long opaque) = LogoTracer.Split(rgb, alpha, 4, 1, palette, nearest, 2, Ct);

        // The gray pixel is a quarter white; the half-transparent white pixel is half transparency.
        Assert.Equal((0, 1), (labels[3], labels[4]));
        Assert.Equal(0.75, weights[3], 2);
        Assert.Equal(0.25, weights[4], 2);
        Assert.Equal((1, 2), (labels[9], labels[11]));
        Assert.Equal(128 / 255.0, weights[9], 3);
        Assert.Equal(1 - (128 / 255.0), weights[11], 3);
        Assert.Equal((0, 4), (unexplained, opaque));
    }

    [Fact]
    public void FringesAndTintsLeaveThePalette()
    {
        // Mostly black and white, with a rare dark gray between them and a rare near-white tint: both go.
        Rgba8[] palette = [new(0, 0, 0, 255), new(255, 255, 255, 255), new(110, 110, 110, 255), new(240, 250, 255, 255), new(200, 0, 0, 255)];
        int n = 1000;
        byte[] rgb = new byte[3 * n], alpha = new byte[n];
        Array.Fill(alpha, (byte)255);
        for (int i = 0; i < n; i++)
        {
            Rgba8 c = palette[i < 450 ? 0 : i < 900 ? 1 : i < 905 ? 2 : i < 910 ? 3 : 4];
            (rgb[3 * i], rgb[(3 * i) + 1], rgb[(3 * i) + 2]) = (c.R, c.G, c.B);
        }

        byte[] nearest = [.. Enumerable.Range(0, n).Select(i => (byte)(i < 450 ? 0 : i < 900 ? 1 : i < 905 ? 2 : i < 910 ? 3 : 4))];
        Rgba8[] kept = LogoTracer.Prune(palette, ref nearest, rgb, alpha, n, Ct);

        // The red is rare too, but lies between no two colors and near none: it stays.
        Assert.Equal([palette[0], palette[1], palette[4]], kept);
    }

    [Fact]
    public void FinerSamplingForSmallerImages()
    {
        Assert.Equal(4, LogoTracer.Scale(130, 105));
        Assert.Equal(2, LogoTracer.Scale(1000, 1000));
        Assert.Equal(1, LogoTracer.Scale(3000, 3000));
    }

    /// <summary>How many pixels' worth of a color over white an image holds, read from its green.</summary>
    private static double Coverage(Raster<Rgba8> raster, Rgba8 color)
    {
        double sum = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                sum += (255.0 - raster.GetPixel(x, y).G) / (255.0 - color.G);
            }
        }

        return sum;
    }

    private static double RadiusError(TraceResult result, Vector2 center, double radius)
    {
        TracedShape shape = Assert.Single(result.Shapes);
        return Outline(shape.Path).Average(p => Math.Abs(Vector2.Distance(p, center) - radius));
    }

    private static double PolygonDistance(Vector2[] polygon, Vector2 p)
    {
        double best = double.MaxValue;
        for (int i = 0; i < polygon.Length; i++)
        {
            Vector2 a = polygon[i], ab = polygon[(i + 1) % polygon.Length] - a;
            float s = Math.Clamp(Vector2.Dot(p - a, ab) / ab.LengthSquared(), 0, 1);
            best = Math.Min(best, Vector2.Distance(p, a + (s * ab)));
        }

        return best;
    }

    /// <summary>Points along a path's lines and cubics, twenty to a segment.</summary>
    private static List<Vector2> Outline(Path path)
    {
        ReadOnlySpan<Vector2> points = path.Points;
        List<Vector2> output = [];
        int k = 0;
        Vector2 current = default, start = default;
        foreach (PathVerb verb in path.Verbs)
        {
            switch (verb)
            {
                case PathVerb.Move:
                    current = start = points[k++];
                    break;
                case PathVerb.Line:
                    {
                        Vector2 end = points[k++];
                        output.AddRange(Enumerable.Range(1, 20).Select(i => Vector2.Lerp(current, end, i / 20f)));
                        current = end;
                        break;
                    }

                case PathVerb.Cubic:
                    {
                        Vector2 c1 = points[k], c2 = points[k + 1], end = points[k + 2];
                        for (int i = 1; i <= 20; i++)
                        {
                            float t = i / 20f, u = 1 - t;
                            output.Add((u * u * u * current) + (3 * u * u * t * c1) + (3 * u * t * t * c2) + (t * t * t * end));
                        }

                        current = end;
                        k += 3;
                        break;
                    }

                case PathVerb.Close:
                    output.AddRange(Enumerable.Range(1, 20).Select(i => Vector2.Lerp(current, start, i / 20f)));
                    current = start;
                    break;
                default:
                    k += 2;
                    break;
            }
        }

        return output;
    }

    private static int Distance(Rgba8 a, Rgba8 b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
