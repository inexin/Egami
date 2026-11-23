using System.Numerics;

using Egami.Drawing.Tests.Geometry;
using Egami.Drawing.Tracing;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Tracing;

/// <summary>Tracing end to end: shapes with known outlines, options, the public API and round trips.</summary>
public class TraceTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void SquareTracesToItsOutline()
    {
        using Raster<Rgba8> raster = Raster.Create(40, 30, Rgba8.White);
        Fill(raster, 5, 6, 20, 15);
        TraceResult result = raster.Trace(TraceOptions.BlackAndWhite);
        TracedShape shape = Assert.Single(result.Shapes);
        Assert.Equal(Rgba8.Black, shape.Color);
        Assert.Equal(TracedShapeKind.Fill, shape.Kind);
        Assert.Equal(300, PathEffectTests.Area(shape.Path), 1e-3);
        Assert.Equal(new RectF(5, 6, 20, 15), shape.Path.Bounds);
        Assert.DoesNotContain(PathVerb.Cubic, shape.Path.Verbs.ToArray());
        Assert.Equal((40, 30), (result.Width, result.Height));
    }

    [Fact]
    public void CircleTracesToCurves()
    {
        using Raster<Rgba8> raster = Raster.Create(100, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(50, 50), 30, Rgba8.Black);
        }

        TracedShape shape = Assert.Single(raster.Trace(TraceOptions.BlackAndWhite).Shapes);

        // The antialiased edge places the outline to a fraction of a pixel: the radius comes back within 0.1 pixels.
        Assert.Equal(Math.PI * 900, PathEffectTests.Area(shape.Path), Math.PI * 900 * 0.007);
        Assert.DoesNotContain(PathVerb.Line, shape.Path.Verbs.ToArray());

        // A round outline snaps to a circle: four quarter arcs, where a circle of radius 30 has about 170 pixel edges.
        Assert.Equal(4, shape.Path.Verbs.ToArray().Count(v => v == PathVerb.Cubic));
    }

    [Fact]
    public void RingHasAHoleOfOppositeDirection()
    {
        using Raster<Rgba8> raster = Raster.Create(80, 80, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(40, 40), 30, Rgba8.Black);
            canvas.FillCircle(new Vector2(40, 40), 15, Rgba8.White);
        }

        TracedShape shape = Assert.Single(raster.Trace(TraceOptions.BlackAndWhite).Shapes);
        Assert.Equal(2, shape.Path.Contours.Count);
        double outer = PathEffectTests.Area(shape.Path.Contours[0]), hole = PathEffectTests.Area(shape.Path.Contours[1]);
        Assert.True(outer > 0);
        Assert.True(hole < 0);
        Assert.Equal(Math.PI * (900 - 225), outer + hole, Math.PI * 900 * 0.03);

        // Either fill rule fills the ring the same.
        Assert.False(shape.Path.Contains(new Vector2(40, 40)));
        Assert.False(shape.Path.WithFillRule(FillRule.EvenOdd).Contains(new Vector2(40, 40)));
        Assert.True(shape.Path.Contains(new Vector2(40, 18)));
    }

    [Fact]
    public void IslandInAHoleComesAfterItsParent()
    {
        using Raster<Rgba8> raster = Raster.Create(60, 60, Rgba8.White);
        Fill(raster, 5, 5, 50, 50);
        Fill(raster, 15, 15, 30, 30, Rgba8.White);
        Fill(raster, 25, 25, 10, 10);
        TraceResult result = raster.Trace(TraceOptions.BlackAndWhite);
        Assert.Equal(2, result.Shapes.Count);
        Assert.Equal(new RectF(5, 5, 50, 50), result.Shapes[0].Path.Bounds);
        Assert.Equal(new RectF(25, 25, 10, 10), result.Shapes[1].Path.Bounds);
    }

    [Fact]
    public void WhiteBackgroundUnlessIgnored()
    {
        using Raster<Rgba8> raster = Raster.Create(20, 10, Rgba8.White);
        Fill(raster, 2, 2, 5, 5);
        TraceResult result = raster.Trace();
        Assert.Equal(2, result.Shapes.Count);
        Assert.Equal(Rgba8.White, result.Shapes[0].Color);
        Assert.Equal(new RectF(0, 0, 20, 10), result.Shapes[0].Path.Bounds);
        Assert.Single(raster.Trace(TraceOptions.Default with { IgnoreWhite = true }).Shapes);
    }

    [Fact]
    public void EmptyFullAndOnePixelImages()
    {
        using Raster<Rgba8> white = Raster.Create(16, 16, Rgba8.White);
        Assert.Empty(white.Trace(TraceOptions.BlackAndWhite).Shapes);
        Assert.Equal(Rgba8.White, Assert.Single(white.Trace().Shapes).Color);

        using Raster<Rgba8> black = Raster.Create(16, 16, Rgba8.Black);
        TracedShape full = Assert.Single(black.Trace().Shapes);
        Assert.Equal(256, PathEffectTests.Area(full.Path), 1e-3);

        using Raster<Rgba8> dot = Raster.Create(1, 1, Rgba8.Black);
        TracedShape one = Assert.Single(dot.Trace(TraceOptions.BlackAndWhite).Shapes);
        Assert.Equal(new RectF(0, 0, 1, 1), one.Path.TightBounds);
        Assert.Empty(Raster.Create(1, 1, Rgba8.White).Trace(TraceOptions.BlackAndWhite).Shapes);
    }

    [Fact]
    public void DiagonalPixelsTraceAsOneShape()
    {
        using Raster<Rgba8> raster = Raster.Create(12, 12, Rgba8.White);
        for (int i = 1; i < 11; i++)
        {
            raster.SetPixel(i, i, Rgba8.Black);
        }

        TracedShape shape = Assert.Single(raster.Trace(TraceOptions.BlackAndWhite with { Noise = 0 }).Shapes);
        Assert.Single(shape.Path.Contours);
        Assert.InRange(PathEffectTests.Area(shape.Path), 6, 12);
    }

    [Fact]
    public void NoiseDropsSpecks()
    {
        using Raster<Rgba8> raster = Raster.Create(30, 30, Rgba8.White);
        Fill(raster, 5, 5, 10, 10);
        raster.SetPixel(25, 25, Rgba8.Black);
        Fill(raster, 20, 5, 2, 2);
        raster.SetPixel(9, 9, Rgba8.White);
        Assert.Equal(3, raster.Trace(TraceOptions.BlackAndWhite with { Noise = 0 }).Shapes.Count);
        TraceResult result = raster.Trace(TraceOptions.BlackAndWhite);
        Assert.Equal(2, result.Shapes.Count);
        Assert.Single(result.Shapes[0].Path.Contours);
        Assert.Single(raster.Trace(TraceOptions.BlackAndWhite with { Noise = 5 }).Shapes);
        _ = raster.Trace(TraceOptions.BlackAndWhite with { Noise = int.MaxValue });
    }

    [Fact]
    public void ThresholdChoosesTheInk()
    {
        using Raster<Rgba8> raster = Raster.Create(30, 10, Rgba8.White);
        Fill(raster, 2, 2, 6, 6, new Rgba8(60, 60, 60, 255));
        Fill(raster, 12, 2, 6, 6, new Rgba8(160, 160, 160, 255));
        Assert.Equal(2, raster.Trace(TraceOptions.BlackAndWhite with { Threshold = 0.9f }).Shapes.Count);
        Assert.Single(raster.Trace(TraceOptions.BlackAndWhite with { Threshold = 0.5f }).Shapes);
        Assert.Empty(raster.Trace(TraceOptions.BlackAndWhite with { Threshold = 0 }).Shapes);

        // At 1 even white is ink: one shape covers the image.
        Assert.Equal(new RectF(0, 0, 30, 10), Assert.Single(raster.Trace(TraceOptions.BlackAndWhite with { Threshold = 1 }).Shapes).Path.Bounds);
    }

    [Fact]
    public void CornersOneGivesAPolygonAndZeroNoCorners()
    {
        using Raster<Rgba8> raster = Raster.Create(100, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillCircle(new Vector2(50, 50), 30, Rgba8.Black);
            canvas.FillRect(new RectF(5, 5, 20, 20), Rgba8.Black);
        }

        Path polygon = raster.Trace(TraceOptions.BlackAndWhite with { Corners = 1 }).ToPath(Rgba8.Black);
        Assert.DoesNotContain(PathVerb.Cubic, polygon.Verbs.ToArray());
        Path round = raster.Trace(TraceOptions.BlackAndWhite with { Corners = 0, SnapToLines = false }).ToPath(Rgba8.Black);
        Assert.DoesNotContain(PathVerb.Line, round.Verbs.ToArray());
    }

    [Fact]
    public void PathFidelityTradesCurvesForAccuracy()
    {
        using Raster<Rgba8> raster = Raster.Create(200, 200, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillOval(new RectF(20, 40, 160, 110), Rgba8.Black);
        }

        int loose = Cubics(raster.Trace(TraceOptions.BlackAndWhite with { PathFidelity = 0 }));
        int middle = Cubics(raster.Trace(TraceOptions.BlackAndWhite));
        int tight = Cubics(raster.Trace(TraceOptions.BlackAndWhite with { PathFidelity = 1 }));
        Assert.True(loose <= middle, $"{loose} > {middle}");
        Assert.True(middle < tight, $"{middle} >= {tight}");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(8)]
    public void RoundTripAreaErrorIsSmall(int scale)
    {
        // Shapes drawn from known vectors, traced, drawn back and compared pixel by pixel.
        int size = 64 * scale;
        using Raster<Rgba8> source = Raster.Create(size, size, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = source.CreateCanvas())
        {
            canvas.Scale(scale, scale);
            canvas.FillCircle(new Vector2(18, 18), 12, Rgba8.Black);
            canvas.FillRect(new RectF(36, 6, 22, 14), Rgba8.Black);
            canvas.FillPath(Path.Star(new Vector2(46, 44), 16, 7, 5), Rgba8.Black);
            canvas.FillRing(new RoundRect(new RectF(4, 36, 24, 24), 6), new RoundRect(new RectF(10, 42, 12, 12), 3), Rgba8.Black);
        }

        TraceResult result = source.Trace();
        using Raster<Rgba8> back = result.ToPicture().ToRaster<Rgba8>();
        int ink = 0, differ = 0;
        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                bool a = source.GetPixel(x, y).R < 128, b = back.GetPixel(x, y).R < 128;
                ink += a ? 1 : 0;
                differ += a != b ? 1 : 0;
            }
        }

        // Pixels that differ come only along edges: a few percent of the ink at 1x (where the star's points are 2 pixels
        // wide), less as the image grows.
        double error = (double)differ / ink;
        Assert.True(error < (scale == 1 ? 0.08 : 0.025), $"area error {error:P2} at {scale}x");
    }

    [Fact]
    public void DrawToPictureAndToPathAgree()
    {
        using Raster<Rgba8> raster = Raster.Create(40, 40, Rgba8.White);
        Fill(raster, 4, 4, 10, 10);
        Fill(raster, 20, 20, 10, 12);
        TraceResult result = raster.Trace();
        Picture picture = result.ToPicture();
        Assert.Equal(new RectF(0, 0, 40, 40), picture.Bounds);
        Assert.Equal(3, picture.CallCount);

        using Raster<Rgba8> drawn = Raster.Create(40, 40, new Rgba8(255, 0, 0, 255));
        using (RasterCanvas<Rgba8> canvas = drawn.CreateCanvas())
        {
            result.Draw(canvas);
        }

        Assert.Equal(Rgba8.Black, drawn.GetPixel(5, 5));
        Assert.Equal(Rgba8.White, drawn.GetPixel(17, 17));

        Path ink = result.ToPath(Rgba8.Black);
        Assert.Equal(2, ink.Contours.Count);
        Assert.Equal(220, PathEffectTests.Area(ink), 1e-3);
        Assert.Same(Path.Empty, result.ToPath(new Rgba8(1, 2, 3, 255)));
    }

    [Fact]
    public void StrokedShapesDrawWithTheirWidth()
    {
        TraceResult result = new([new TracedShape(Path.Line(new Vector2(0, 5), new Vector2(10, 5)), Rgba8.Black, TracedShapeKind.Stroke, 2)], 10, 10);
        using Raster<Rgba8> raster = Raster.Create(10, 10, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            result.Draw(canvas);
        }

        Assert.Equal(Rgba8.Black, raster.GetPixel(5, 4));
        Assert.Equal(Rgba8.White, raster.GetPixel(5, 7));
    }

    [Fact]
    public void AnyPixelTypeTraces()
    {
        using Raster<Gray8> gray = Raster.Create(20, 20, new Gray8(255));
        using Raster<Rgb8> rgb = Raster.Create(20, 20, new Rgb8(255, 255, 255));
        using Raster<RgbaF32> floats = Raster.Create(20, 20, new RgbaF32(1, 1, 1, 1));
        for (int y = 5; y < 15; y++)
        {
            for (int x = 5; x < 15; x++)
            {
                gray.SetPixel(x, y, new Gray8(0));
                rgb.SetPixel(x, y, new Rgb8(0, 0, 0));
                floats.SetPixel(x, y, new RgbaF32(0, 0, 0, 1));
            }
        }

        foreach (Raster raster in new Raster[] { gray, rgb, floats })
        {
            TracedShape shape = Assert.Single(raster.Trace(TraceOptions.BlackAndWhite).Shapes);
            Assert.Equal(100, PathEffectTests.Area(shape.Path), 1e-3);
        }

        Assert.Single(gray.Trace(TraceOptions.BlackAndWhite).Shapes);
        Assert.Single(floats.Trace(TraceOptions.BlackAndWhite).Shapes);
    }

    [Fact]
    public async Task TraceAsyncTracesAndCancels()
    {
        using Raster<Rgba8> raster = Raster.Create(30, 30, Rgba8.White);
        Fill(raster, 5, 5, 10, 10);
        TraceResult result = await raster.TraceAsync(TraceOptions.BlackAndWhite, cancellationToken: Ct);
        Assert.Single(result.Shapes);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => raster.TraceAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public async Task TypedTraceAsyncMatchesTypedTrace()
    {
        using Raster<RgbaF32> floats = Raster.Create(20, 20, new RgbaF32(1, 1, 1, 1));
        for (int y = 5; y < 15; y++)
        {
            for (int x = 5; x < 15; x++)
            {
                floats.SetPixel(x, y, new RgbaF32(0, 0, 0, 1));
            }
        }

        TraceResult typed = await floats.TraceAsync(TraceOptions.BlackAndWhite, cancellationToken: Ct);
        TracedShape shape = Assert.Single(typed.Shapes);
        Assert.Equal(PathEffectTests.Area(floats.Trace(TraceOptions.BlackAndWhite).Shapes[0].Path), PathEffectTests.Area(shape.Path), 1e-6);
        TraceResult untyped = await ((Raster)floats).TraceAsync(TraceOptions.BlackAndWhite, cancellationToken: Ct);
        Assert.Single(untyped.Shapes);

        using CancellationTokenSource cancelled = new();
        await cancelled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => floats.TraceAsync(cancellationToken: cancelled.Token));
    }

    [Fact]
    public void ContextLimitsApply()
    {
        using Raster<Rgba8> raster = Raster.Create(30, 30, Rgba8.White);
        EgamiContext small = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 100 } };
        Assert.Throws<LimitExceededException>(() => raster.Trace(null, small));
    }

    [Fact]
    public void OptionsAreChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Threshold = 1.5f });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Threshold = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { PathFidelity = -0.1f });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Corners = float.PositiveInfinity });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Noise = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Mode = (TraceMode)99 });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Colors = 1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Colors = 257 });
        Assert.Throws<ArgumentException>(() => TraceOptions.Default with { Palette = [] });
        Assert.Throws<ArgumentException>(() => TraceOptions.Default with { Palette = [.. Enumerable.Repeat(Rgba8.Black, 257)] });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Layering = (TraceLayering)5 });
        Assert.Throws<ArgumentOutOfRangeException>(() => TraceOptions.Default with { Smoothing = 2 });
        Assert.Throws<ArgumentNullException>(() => ((Raster)null!).Trace());
        Assert.Throws<ArgumentOutOfRangeException>(() => new TracedShape(Path.Empty, Rgba8.Black, TracedShapeKind.Stroke, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TraceResult([], 0, 1));

        Assert.Null(TraceOptions.Default.Threshold);
        Assert.Equal(TraceMode.BlackAndWhite, TraceOptions.Default.Mode);
        Assert.False(TraceOptions.Default.IgnoreWhite);
        Assert.True(TraceOptions.BlackAndWhite.IgnoreWhite);
        Assert.Equal(8, TraceOptions.Default.Colors);
        Assert.Null(TraceOptions.Default.Palette);
        Assert.Equal(TraceLayering.Stacked, TraceOptions.Default.Layering);
        Assert.True(TraceOptions.Default.SnapToLines);
        Assert.True(TraceOptions.Default.SnapToArcs);
        Assert.Equal(0, TraceOptions.Default.Smoothing);
        Assert.Equal(TraceOptions.Default, new TraceOptions());
    }

    [Fact]
    public void ExtremeOptionsStillTrace()
    {
        using Raster<Rgba8> raster = Raster.Create(50, 50, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillPath(Path.Star(new Vector2(25, 25), 22, 8, 7), Rgba8.Black);
        }

        foreach (float fidelity in new[] { 0f, 1f })
        {
            foreach (float corners in new[] { 0f, 1f })
            {
                TraceResult result = raster.Trace(TraceOptions.BlackAndWhite with { PathFidelity = fidelity, Corners = corners, Noise = 0 });
                TracedShape shape = Assert.Single(result.Shapes);
                Assert.InRange(PathEffectTests.Area(shape.Path), 500, 1500);
            }
        }
    }

    [Fact]
    public void RandomBitmapsKeepTheirArea()
    {
        // Noise bitmaps stress the corner and curve fitting: the outline still encloses about the ink's area.
        Random random = new(11);
        using Raster<Rgba8> raster = Raster.Create(48, 40, Rgba8.White);
        int ink = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                if (random.Next(2) == 0)
                {
                    raster.SetPixel(x, y, Rgba8.Black);
                    ink++;
                }
            }
        }

        TraceResult result = raster.Trace(TraceOptions.BlackAndWhite with { Noise = 0, Corners = 1 });
        double area = result.Shapes.Sum(s => PathEffectTests.Area(s.Path));
        Assert.Equal(ink, area, ink * 0.15);
    }

    private static int Cubics(TraceResult result) => result.Shapes.Sum(s => s.Path.Verbs.ToArray().Count(v => v == PathVerb.Cubic));

    private static void Fill(Raster<Rgba8> raster, int x0, int y0, int w, int h, Rgba8? color = null)
    {
        for (int y = y0; y < y0 + h; y++)
        {
            for (int x = x0; x < x0 + w; x++)
            {
                raster.SetPixel(x, y, color ?? Rgba8.Black);
            }
        }
    }
}
