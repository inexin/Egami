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
        // Black and white with potrace-like settings.
        TraceResult ink = scan.Trace(new TraceOptions
        {
            Mode = TraceMode.BlackAndWhite,
            Threshold = null,      // null: Otsu's method, as Raster.FindThreshold; or a level from 0 (black) to 1 (white)
            PathFidelity = 0.5f,   // 0: fewest curves; 1: every curve kept
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
