using System.Numerics;

using Egami.Drawing.Tests.Geometry;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Grayscale and color tracing end to end: palettes, layers, shared edges and options.</summary>
public class ColorTraceTests
{
    private static readonly Rgba8 Red = new(220, 30, 30, 255);
    private static readonly Rgba8 Blue = new(20, 60, 200, 255);
    private static readonly Rgba8 Yellow = new(250, 200, 0, 255);

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ShallowEdgesLeaveNoSlivers()
    {
        // A large red disc and an orange square on white: where the disc's edge runs nearly level, its blends with white lie
        // nearer the orange than either side, and they must not come back as slivers of orange.
        using Raster<Rgba8> raster = Raster.Create(520, 420, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(120, 120), 80, new Rgba8(220, 30, 40, 255));
            canvas.FillRect(new RectF(300, 250, 150, 120), new Rgba8(240, 140, 20, 255));
        }

        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color, Colors = 16, IgnoreWhite = true });
        Assert.Equal(2, result.Shapes.Count);
    }

    [Fact]
    public void ColorFindsThePicturesOwnColors()
    {
        // Antialiased edges blend the three colors with white and with each other; the palette is the flat colors alone.
        using Raster<Rgba8> raster = Logo(1);
        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color });
        Assert.Equal(4, result.Palette.Count);
        foreach (Rgba8 expected in new[] { Rgba8.White, Red, Blue, Yellow })
        {
            Assert.Contains(result.Palette, c => Distance(c, expected) <= 3);
        }

        Assert.Equal(4, result.Shapes.Count);
    }

    [Theory]
    [InlineData(TraceLayering.Stacked)]
    [InlineData(TraceLayering.Abutting)]
    public void ColorRoundTripIsClose(TraceLayering layering)
    {
        using Raster<Rgba8> raster = Logo(3);
        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color, Layering = layering });
        using Raster<Rgba8> back = result.ToPicture().ToRaster<Rgba8>();
        int differ = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                differ += Distance(raster.GetPixel(x, y), back.GetPixel(x, y)) > 96 ? 1 : 0;
            }
        }

        // Only pixels along edges may differ, and only some of them.
        Assert.True(differ < raster.Width * raster.Height / 200, $"{differ} pixels differ");
    }

    [Fact]
    public void StackedLayersCoverTheLayersAboveThem()
    {
        using Raster<Rgba8> raster = Logo(2);
        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color });

        // Largest color first: the white background covers the whole image, so nothing shows through anywhere.
        Assert.Equal(Rgba8.White, result.Shapes[0].Color);
        Assert.Equal(raster.Width * raster.Height, PathEffectTests.Area(result.Shapes[0].Path), 1e-3);

        // The red disc lies under the blue square where they meet: its shape reaches into the square.
        Assert.True(result.ToPath(Red).Contains(new Vector2(70 * 2, 50 * 2)));
        Assert.True(result.ToPath(Blue).Contains(new Vector2(70 * 2, 50 * 2)));
    }

    [Fact]
    public void AbuttingShapesShareTheirEdges()
    {
        using Raster<Rgba8> raster = Logo(2);
        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color, Layering = TraceLayering.Abutting });

        // Neighbours use the same fitted edge, one forward and one reversed: the areas add up to the image exactly, with no gap
        // or overlap anywhere.
        double total = result.Shapes.Sum(s => PathEffectTests.Area(s.Path));
        Assert.Equal(raster.Width * raster.Height, total, 1e-2);
        Assert.False(result.ToPath(Red).Contains(new Vector2(70 * 2, 50 * 2)));
    }

    [Theory]
    [InlineData(TraceLayering.Stacked)]
    [InlineData(TraceLayering.Abutting)]
    public void IgnoreWhiteLeavesWhiteEmpty(TraceLayering layering)
    {
        // A white bar inside red: with IgnoreWhite the bar is a hole in the red, not covered by it.
        using Raster<Rgba8> raster = Raster.Create(60, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(5, 5, 50, 30), Red);
            canvas.FillRect(new RectF(15, 15, 30, 10), Rgba8.White);
        }

        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color, IgnoreWhite = true, Layering = layering });
        TracedShape shape = Assert.Single(result.Shapes);
        Assert.Equal(Red, shape.Color);
        Assert.Equal((50 * 30) - (30 * 10), PathEffectTests.Area(shape.Path), 1e-3);
        Assert.False(shape.Path.Contains(new Vector2(30, 20)));
    }

    [Fact]
    public void GrayscaleTracesGrayLevels()
    {
        using Raster<Rgba8> raster = Raster.Create(80, 20, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 20, 20), new Rgba8(0, 0, 0, 255));
            canvas.FillRect(new RectF(20, 0, 20, 20), new Rgba8(200, 60, 60, 255));
            canvas.FillRect(new RectF(40, 0, 20, 20), new Rgba8(170, 170, 170, 255));
        }

        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Grayscale, Layering = TraceLayering.Abutting });
        Assert.Equal(4, result.Palette.Count);
        Assert.All(result.Palette, c => Assert.True(c.R == c.G && c.G == c.B, c.ToString()));

        // At most two levels: dark and light.
        Assert.Equal(2, raster.Trace(new TraceOptions { Mode = TraceMode.Grayscale, Colors = 2 }).Palette.Count);
    }

    [Fact]
    public void FixedPaletteColorsTheShapes()
    {
        using Raster<Rgba8> raster = Logo(1);
        Rgba8[] palette = [new Rgba8(255, 255, 255, 255), new Rgba8(255, 0, 0, 255), new Rgba8(0, 0, 255, 255)];
        TraceResult result = raster.Trace(new TraceOptions { Mode = TraceMode.Color, Palette = palette, Layering = TraceLayering.Abutting });
        Assert.All(result.Shapes, s => Assert.Contains(s.Color, palette));
        Assert.Equal(3, result.Palette.Count);
    }

    [Fact]
    public void SmoothingFlattensNoise()
    {
        Random random = new(5);
        using Raster<Rgba8> raster = Raster.Create(64, 64, Rgba8.White);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                int level = x < 32 ? 60 : 190;
                byte v = (byte)Math.Clamp(level + random.Next(-40, 41), 0, 255);
                raster.SetPixel(x, y, new Rgba8(v, v, v, 255));
            }
        }

        TraceOptions options = new() { Mode = TraceMode.Grayscale, Colors = 4, Noise = 0, Layering = TraceLayering.Abutting };
        int rough = raster.Trace(options).Shapes.Count;
        int smooth = raster.Trace(options with { Smoothing = 1 }).Shapes.Count;
        Assert.True(smooth < rough / 4, $"{smooth} shapes smoothed, {rough} not");
    }

    [Fact]
    public void NoiseMergesSmallRegionsIntoTheirNeighbour()
    {
        using Raster<Rgba8> raster = Raster.Create(30, 30, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(5, 5, 20, 20), Blue);
        }

        raster.SetPixel(10, 10, Red);
        raster.SetPixel(11, 10, Red);
        TraceOptions options = new() { Mode = TraceMode.Color, Palette = [Rgba8.White, Blue, Red], Layering = TraceLayering.Abutting, Noise = 3 };
        Assert.DoesNotContain(raster.Trace(options).Shapes, s => s.Color == Red);
        Assert.Contains(raster.Trace(options with { Noise = 2 }).Shapes, s => s.Color == Red);
    }

    [Fact]
    public void SnappingGivesLinesAndCircles()
    {
        using Raster<Rgba8> raster = Logo(3);
        TraceResult snapped = raster.Trace(new TraceOptions { Mode = TraceMode.Color, Layering = TraceLayering.Abutting });
        Path yellow = snapped.ToPath(snapped.Palette.First(c => Distance(c, Yellow) <= 3));
        Assert.Equal(4, yellow.Verbs.ToArray().Count(v => v == PathVerb.Cubic));
        Path blue = snapped.ToPath(snapped.Palette.First(c => Distance(c, Blue) <= 3));
        Assert.Contains(PathVerb.Line, blue.Verbs.ToArray());

        TraceOptions free = new() { Mode = TraceMode.Color, Layering = TraceLayering.Abutting, SnapToLines = false, SnapToArcs = false };
        TraceResult curves = raster.Trace(free);
        Assert.DoesNotContain(PathVerb.Line, curves.ToPath(curves.Palette.First(c => Distance(c, Blue) <= 3)).Verbs.ToArray());
    }

    [Fact]
    public void TracingIsTheSameEveryTime()
    {
        using Raster<Rgba8> raster = Logo(2);
        foreach (TraceLayering layering in new[] { TraceLayering.Stacked, TraceLayering.Abutting })
        {
            TraceOptions options = new() { Mode = TraceMode.Color, Layering = layering };
            TraceResult a = raster.Trace(options), b = raster.Trace(options);
            Assert.Equal(a.Shapes.Select(s => (s.Color, s.Path.ToSvg())), b.Shapes.Select(s => (s.Color, s.Path.ToSvg())));
        }
    }

    [Fact]
    public async Task ColorTraceCancels()
    {
        using Raster<Rgba8> raster = Logo(2);
        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => raster.TraceAsync(new TraceOptions { Mode = TraceMode.Color }, null, cancelled.Token));
        TraceResult result = await raster.TraceAsync(new TraceOptions { Mode = TraceMode.Grayscale }, null, Ct);
        Assert.NotEmpty(result.Shapes);
    }

    [Fact]
    public void OtherPixelTypesAndTinyImages()
    {
        using Raster<Rgba8> source = Logo(1);
        using Raster<RgbaF32> wide = source.As<RgbaF32>();
        Assert.Equal(4, wide.Trace(new TraceOptions { Mode = TraceMode.Color }).Palette.Count);

        using Raster<Rgba8> one = Raster.Create(1, 1, Red);
        TracedShape shape = Assert.Single(one.Trace(new TraceOptions { Mode = TraceMode.Color }).Shapes);
        Assert.Equal(new RectF(0, 0, 1, 1), shape.Path.TightBounds);
        using Raster<Rgba8> white = Raster.Create(8, 8, Rgba8.White);
        Assert.Empty(white.Trace(new TraceOptions { Mode = TraceMode.Color, IgnoreWhite = true }).Shapes);
    }

    [Fact]
    public void RandomColorsKeepTheirAreaWhenAbutting()
    {
        Random random = new(3);
        Rgba8[] colors = [Rgba8.White, Red, Blue, Yellow];
        using Raster<Rgba8> raster = Raster.Create(40, 32, Rgba8.White);
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                raster.SetPixel(x, y, colors[random.Next(4)]);
            }
        }

        TraceOptions options = new() { Mode = TraceMode.Color, Palette = colors, Noise = 0, Layering = TraceLayering.Abutting };
        TraceResult result = raster.Trace(options);
        Assert.Equal(40 * 32, result.Shapes.Sum(s => PathEffectTests.Area(s.Path)), 1e-2);
        Assert.NotEmpty(raster.Trace(options with { Layering = TraceLayering.Stacked }).Shapes);
    }

    /// <summary>A red disc, a blue square overlapping it and a yellow disc on the square, antialiased, on white.</summary>
    internal static Raster<Rgba8> Logo(int scale)
    {
        Raster<Rgba8> raster = Raster.Create(120 * scale, 100 * scale, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        canvas.Scale(scale, scale);
        canvas.FillCircle(new Vector2(40, 50), 30, Red);
        canvas.FillRect(new RectF(60.5f, 20.25f, 45, 50), Blue);
        canvas.FillCircle(new Vector2(90, 45), 12, Yellow);
        return raster;
    }

    private static int Distance(Rgba8 a, Rgba8 b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
}
