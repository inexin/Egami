using System.Numerics;

using Egami.Drawing;
using Egami.Drawing.Tracing;
using Egami.Pixels;
using Egami.Svg;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Tracing
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Trace()
    {
        using Raster<Rgba8> scan = Signature();

        #region trace
        // Trace a scan into shapes: each ink region becomes a black path of curves and corners, back to front.
        TraceResult result = scan.Trace(TraceOptions.BlackAndWhite);

        foreach (TracedShape shape in result.Shapes)
        {
            Console.WriteLine($"{shape.Color} {shape.Path.Bounds}");
        }

        // The shapes are in the scan's pixels; scale them like any path.
        Path ink = result.ToPath(Rgba8.Black).Transform(Matrix3x2.CreateScale(0.5f));
        #endregion

        Assert.NotEmpty(result.Shapes);
        Assert.False(ink.IsEmpty);
    }

    [Fact]
    public void Options()
    {
        using Raster<Rgba8> scan = Signature();

        #region options
        // Black and white with every setting spelled out.
        TraceResult ink = scan.Trace(new TraceOptions
        {
            Mode = TraceMode.BlackAndWhite,
            Threshold = null,      // null: Otsu's method, as Raster.FindThreshold; or a level from 0 (black) to 1 (white)
            PathFidelity = 0.5f,   // 0: fewest curves; 1: curves that follow the pixels most closely
            Corners = 0.75f,       // 0: round every corner; 1: keep every corner (a polygon)
            Noise = 4,             // regions under 4 pixels merge into the regions around them
            IgnoreWhite = true,    // the ink alone, no white background shape
        });

        // Or start from a preset and change one thing.
        TraceResult polygon = scan.Trace(TraceOptions.BlackAndWhite with { Corners = 1 });
        #endregion

        Assert.NotEmpty(ink.Shapes);
        Assert.DoesNotContain(PathVerb.Cubic, polygon.ToPath(Rgba8.Black).Verbs.ToArray());
    }

    [Fact]
    public void Presets()
    {
        using Raster<Rgba8> logo = Logo();
        using Raster<Rgba8> sketch = Signature();

        #region presets
        // A preset for the kind of image, then any change with `with`.
        TraceResult badge = logo.Trace(TraceOptions.Logo with { Colors = 4, IgnoreWhite = true });
        TraceResult poster = logo.Trace(TraceOptions.Poster);
        TraceResult lines = sketch.Trace(TraceOptions.LineArt with { StrokeWidth = 2 });
        #endregion

        Assert.Equal(2, badge.Palette.Count);
        Assert.NotEmpty(poster.Shapes);
        Assert.All(lines.Shapes.Where(s => s.Kind == TracedShapeKind.Stroke), s => Assert.Equal(2, s.StrokeWidth));
    }

    [Fact]
    public void Color()
    {
        using Raster<Rgba8> logo = Logo();

        #region color
        // A flat-colored logo: up to 8 of its own colors, one layer each, largest first, each layer covering those above it.
        TraceResult stacked = logo.Trace(new TraceOptions { Mode = TraceMode.Color, Colors = 8 });
        IReadOnlyList<Rgba8> colors = stacked.Palette;     // the colors found, back to front

        // Abutting layers: each color only its own area, neighbours sharing one edge, and white left out.
        TraceResult pieces = logo.Trace(new TraceOptions
        {
            Mode = TraceMode.Color,
            Layering = TraceLayering.Abutting,
            IgnoreWhite = true,
            SnapToLines = true,    // straight runs become lines
            SnapToArcs = true,     // runs on a circle become arcs, round outlines circles
        });

        // Grayscale to fixed levels, smoothed first as for a photo.
        TraceResult poster = logo.Trace(new TraceOptions
        {
            Mode = TraceMode.Grayscale,
            Palette = [new Rgba8(0, 0, 0, 255), new Rgba8(128, 128, 128, 255), new Rgba8(255, 255, 255, 255)],
            Smoothing = 0.5f,
        });
        #endregion

        Assert.Equal(3, colors.Count);
        Assert.Equal(2, pieces.Palette.Count);
        Assert.All(poster.Shapes, s => Assert.Equal(s.Color.R, s.Color.G));
    }

    [Fact]
    public void LogoMode()
    {
        using Raster<Rgba8> logo = Logo();
        using Raster<Rgba8> icon = Raster.Create(40, 40, Rgba8.Transparent);
        using (RasterCanvas<Rgba8> canvas = icon.CreateCanvas())
        {
            PathBuilder ring = new();
            ring.AddCircle(new Vector2(20, 20), 15);
            ring.AddCircle(new Vector2(20, 20), 6, PathDirection.CounterClockwise);
            canvas.FillPath(ring.ToPath(FillRule.EvenOdd), new Rgba8(220, 30, 30, 255));
        }

        #region logo
        // Flat-color art with antialiased edges: edges placed from smooth coverage fields, for rounder curves on small art.
        TraceResult traced = logo.Trace(new TraceOptions { Mode = TraceMode.Logo, Colors = 8 });

        // On a transparent icon the outside is left out; the hole the ring encloses is traced white unless IgnoreWhite.
        TraceResult donut = icon.Trace(new TraceOptions { Mode = TraceMode.Logo, Layering = TraceLayering.Abutting });

        // A photo or a gradient is not flat color, so logo mode refuses it: trace it with TraceMode.Color instead.
        #endregion

        Assert.Equal(3, traced.Palette.Count);
        Assert.Contains(Rgba8.White, donut.Palette);
    }

    [Fact]
    public void Centerline()
    {
        using Raster<Rgba8> sketch = Signature();

        #region centerline
        // Line art as strokes: the middle of each line, with the width its ink covers.
        TraceResult lines = sketch.Trace(new TraceOptions { Mode = TraceMode.Centerline, IgnoreWhite = true });
        foreach (TracedShape stroke in lines.Shapes)
        {
            Console.WriteLine($"{stroke.Kind} {stroke.StrokeWidth:F1} px {stroke.Path.Bounds}");
        }

        // Or every stroke with one pen, for a plotter or an engraving machine.
        TraceResult pen = sketch.Trace(new TraceOptions { Mode = TraceMode.Centerline, StrokeWidth = 1, IgnoreWhite = true });
        #endregion

        Assert.Contains(lines.Shapes, s => s.Kind == TracedShapeKind.Stroke);
        Assert.All(pen.Shapes.Where(s => s.Kind == TracedShapeKind.Stroke), s => Assert.Equal(1, s.StrokeWidth));
    }

    [Fact]
    public void PixelArt()
    {
        using Raster<Rgba8> sprite = Sprite();

        #region pixelart
        // A sprite as smooth shapes, one per color: diagonal steps become curves, blocks keep their corners.
        TraceResult smooth = sprite.Trace(TraceOptions.PixelArt);

        // Upscaling: draw the shapes eight times larger, with no blocks and no blur.
        using Raster<Rgba8> large = Raster.Create(sprite.Width * 8, sprite.Height * 8, Rgba8.Transparent);
        using (RasterCanvas<Rgba8> canvas = large.CreateCanvas())
        {
            canvas.Scale(8);
            smooth.Draw(canvas);
        }

        // Or the pixel squares themselves, merged into one shape per color: a crisp vector copy of the sprite.
        TraceResult squares = sprite.Trace(TraceOptions.PixelArt with { Exact = true });
        #endregion

        Assert.Equal(3, smooth.Shapes.Count);
        Assert.Equal(3, squares.Shapes.Count);
        Assert.Equal(new Rgba8(250, 210, 30, 255), large.GetPixel(44, 44));
    }

    [Fact]
    public void Shapes()
    {
        using Raster<Rgba8> scan = Signature();

        #region shapes
        // TraceOptions.Default also gives a white shape for the paper, under the ink.
        TraceResult result = scan.Trace();
        TracedShape paper = result.Shapes[0];          // Color white, Kind Fill, the whole image
        Path ink = result.ToPath(Rgba8.Black);          // every black shape as one path

        // Outer edges run clockwise and holes counter-clockwise, so either fill rule fills the same area.
        bool same = ink.Contains(new Vector2(40, 40)) == ink.WithFillRule(FillRule.EvenOdd).Contains(new Vector2(40, 40));
        #endregion

        Assert.Equal(Rgba8.White, paper.Color);
        Assert.Equal(TracedShapeKind.Fill, paper.Kind);
        Assert.True(same);
    }

    [Fact]
    public void Draw()
    {
        using Raster<Rgba8> scan = Signature();

        #region draw
        TraceResult result = scan.Trace(TraceOptions.BlackAndWhite);

        // Draw the shapes on any canvas, here four times as large.
        using Raster<Rgba8> large = Raster.Create(scan.Width * 4, scan.Height * 4, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = large.CreateCanvas())
        {
            canvas.Scale(4, 4);
            result.Draw(canvas);
        }

        // Or record them as a picture to replay later, render, or save as a drawing file.
        Picture picture = result.ToPicture();
        using Raster<Rgba8> sharp = picture.ToRaster<Rgba8>(scale: 8);
        #endregion

        Assert.Equal(scan.Width * 8, sharp.Width);
    }

    [Fact]
    public async Task Async()
    {
        using Raster<Rgba8> scan = Signature();
        CancellationToken cancellationToken = Ct;

        #region async
        // Trace on a worker thread; the token is checked between rows and between outlines.
        TraceResult result = await scan.TraceAsync(TraceOptions.BlackAndWhite, cancellationToken: cancellationToken);
        #endregion

        Assert.NotEmpty(result.Shapes);
    }

    [Fact]
    public void Svg()
    {
        using Raster<Rgba8> scan = Signature();
        string file = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"egami-signature-{Guid.NewGuid():N}.svg");

        #region svg
        // Egami.Svg writes a trace as SVG through SvgCanvas: one path element per shape.
        TraceResult result = scan.Trace(TraceOptions.BlackAndWhite);
        string markup = result.ToSvg(new SvgWriteOptions { Precision = 2 });
        result.SaveSvg(file);
        #endregion

        Assert.Contains("<path", markup, StringComparison.Ordinal);
        File.Delete(file);
    }

    /// <summary>A stand-in for a scanned signature: strokes drawn in black on white, antialiased.</summary>
    private static Raster<Rgba8> Logo()
    {
        Raster<Rgba8> raster = Raster.Create(120, 90, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        canvas.FillCircle(new Vector2(40, 45), 30, new Rgba8(220, 30, 30, 255));
        canvas.FillRect(new RectF(60, 20, 45, 50), new Rgba8(20, 60, 200, 255));
        return raster;
    }

    /// <summary>A small original sprite: a round face, its outline drawn in single diagonal steps.</summary>
    private static Raster<Rgba8> Sprite()
    {
        string[] rows =
        [
            "...KKKKK...",
            "..KYYYYYK..",
            ".KYYYYYYYK.",
            "KYYKYYYKYYK",
            "KYYKYYYKYYK",
            "KYYYYYYYYYK",
            "KYKYYYYYKYK",
            "KYYKKKKKYYK",
            ".KYYYYYYYK.",
            "..KYYYYYK..",
            "...KKKKK...",
        ];
        Raster<Rgba8> sprite = Raster.Create(rows[0].Length, rows.Length, Rgba8.White);
        for (int y = 0; y < rows.Length; y++)
        {
            for (int x = 0; x < rows[y].Length; x++)
            {
                sprite.SetPixel(x, y, rows[y][x] switch { 'K' => new Rgba8(20, 20, 20, 255), 'Y' => new Rgba8(250, 210, 30, 255), _ => Rgba8.White });
            }
        }

        return sprite;
    }

    private static Raster<Rgba8> Signature()
    {
        Raster<Rgba8> raster = Raster.Create(160, 80, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();
        Path stroke = new PathBuilder()
            .MoveTo(10, 60)
            .CubicTo(30, 0, 50, 0, 60, 50)
            .CubicTo(70, 80, 90, 10, 110, 40)
            .CubicTo(120, 55, 140, 30, 150, 20)
            .ToPath();
        canvas.StrokePath(stroke, new Stroke(4) { Cap = LineCap.Round, Join = LineJoin.Round }, Rgba8.Black);
        canvas.FillCircle(new Vector2(40, 40), 6, Rgba8.Black);
        return raster;
    }
}
