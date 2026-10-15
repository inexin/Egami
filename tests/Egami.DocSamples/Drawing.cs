using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Drawing
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Canvas()
    {
        #region canvas
        // A canvas draws onto a raster. Calls draw when they are made; dispose the canvas when done.
        using Raster<Rgba8> raster = Raster.Create(400, 300, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.FillRect(new RectF(20, 20, 160, 100), new Rgba8(30, 90, 200, 255));

            // Save and Restore bracket transform and clip changes.
            canvas.Save();
            canvas.Translate(260, 150);
            canvas.Rotate(30);
            canvas.FillRect(new RectF(-50, -30, 100, 60), new Rgba8(200, 60, 30, 200));
            canvas.Restore();

            canvas.FillCircle(new Vector2(100, 220), 50, Rgba8.Black);
        }

        // Blend edges in linear light, which keeps thin lines and translucent colors from darkening.
        using RasterCanvas<Rgba8> linear = raster.CreateCanvas(new CanvasOptions { ColorSpace = ColorSpace.Linear });
        #endregion

        Assert.Equal(Rgba8.Black, raster.GetPixel(100, 220));
    }

    [Fact]
    public void Shapes()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();

        #region shapes
        canvas.FillRect(new RectF(10, 10, 80, 40), Rgba8.Red);
        canvas.FillRoundRect(new RoundRect(new RectF(100, 10, 80, 40), 12), new Rgba8(0, 128, 0, 255));

        // Each corner can have its own radii; radii that would overlap are scaled down together, as CSS does.
        CornerRadii tab = new(topLeft: 16, topRight: 16, bottomRight: 0, bottomLeft: 0);
        canvas.FillRoundRect(new RoundRect(new RectF(190, 10, 100, 40), tab), Rgba8.Black);

        canvas.FillOval(new RectF(10, 60, 120, 60), new Rgba8(255, 200, 0, 255));
        canvas.FillCircle(new Vector2(170, 90), 30, new Rgba8(80, 80, 200, 255));
        canvas.FillArc(new RectF(210, 60, 60, 60), startDegrees: -90, sweepDegrees: 120, Rgba8.Red);
        canvas.FillRing(new RoundRect(new RectF(10, 130, 60, 60), 30), new RoundRect(new RectF(25, 145, 30, 30), 15), Rgba8.Black);

        // The same shapes as paths, to build on or to reuse.
        Path hexagon = Path.RegularPolygon(new Vector2(120, 160), 30, sides: 6);
        Path star = Path.Star(new Vector2(200, 160), outerRadius: 30, innerRadius: 12, points: 5);
        canvas.FillPath(hexagon, new Rgba8(0, 150, 150, 255));
        canvas.FillPath(star, new Rgba8(220, 160, 0, 255));
        #endregion

        Assert.Equal(Rgba8.Red, raster.GetPixel(20, 20));
    }

    [Fact]
    public void Paths()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();

        #region paths
        // A path is built once and never changes, so it can be drawn many times and from many threads.
        Path heart = new PathBuilder()
            .MoveTo(150, 60)
            .CubicTo(150, 30, 100, 20, 90, 60)
            .CubicTo(80, 100, 130, 130, 150, 160)
            .CubicTo(170, 130, 220, 100, 210, 60)
            .CubicTo(200, 20, 150, 30, 150, 60)
            .Close()
            .ToPath();
        canvas.FillPath(heart, Rgba8.Red);

        // SVG's arc command, and the even-odd rule: an inner contour becomes a hole.
        Path frame = new PathBuilder()
            .AddRect(new RectF(10, 10, 60, 60))
            .AddCircle(new Vector2(40, 40), 20)
            .MoveTo(10, 120)
            .ArcTo(radii: new Vector2(30, 20), rotationDegrees: 0, largeArc: false, clockwise: true, end: new Vector2(70, 120))
            .Close()
            .ToPath(FillRule.EvenOdd);
        canvas.FillPath(frame, Rgba8.Black);

        bool hit = heart.Contains(new Vector2(150, 100));   // hit testing on the curves themselves
        RectF box = heart.TightBounds;                       // curves measured at their extremes
        #endregion

        Assert.True(hit);
        Assert.True(box.Width > 100);
    }

    [Fact]
    public void Strokes()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();

        #region strokes
        // The pen is data: width, caps, joins and miter limit. Width 1, butt caps and miter joins by default.
        Stroke thick = new(12) { Cap = LineCap.Round, Join = LineJoin.Round };
        canvas.DrawLine(new Vector2(20, 20), new Vector2(280, 20), thick, new Rgba8(30, 90, 200, 255));

        Path zigzag = Path.Polygon([new(20, 120), new(80, 60), new(140, 120), new(200, 60), new(260, 120)], close: false);
        canvas.StrokePath(zigzag, new Stroke(8) { Join = LineJoin.Miter, MiterLimit = 4 }, Rgba8.Black);

        // A hairline is one device pixel wide whatever the transform.
        canvas.StrokeCircle(new Vector2(150, 160), 30, Stroke.Hairline, Rgba8.Red);
        #endregion

        Assert.NotEqual(Rgba8.White, raster.GetPixel(150, 20));
    }

    [Fact]
    public void Paints()
    {
        using Raster<RgbaF32> raster = Raster.Create<RgbaF32>(200, 100);
        using RasterCanvas<RgbaF32> canvas = raster.CreateCanvas();

        #region paints
        // A color converts to a SolidPaint. A paint also carries a blend mode, an opacity and whether edges are antialiased.
        canvas.FillRect(new RectF(0, 0, 200, 100), Rgba8.White);
        SolidPaint glaze = new(new RgbaF32(0.9f, 0.2f, 0.1f, 1)) { BlendMode = BlendMode.Multiply, Opacity = 0.6f };
        canvas.FillCircle(new Vector2(60, 50), 40, glaze);

        // Pixel art: no antialiasing, so every pixel is fully drawn or not at all.
        canvas.FillCircle(new Vector2(150, 50), 30, new SolidPaint(Rgba8.Black) { Antialias = false });
        #endregion

        Assert.Equal(0, raster.GetPixel(150, 50).R);
    }

    [Fact]
    public void Clips()
    {
        using Raster<Rgba8> raster = Raster.Create(200, 200, Rgba8.White);
        using RasterCanvas<Rgba8> canvas = raster.CreateCanvas();

        #region clips
        // Clips are antialiased and last until the matching Restore.
        canvas.Save();
        canvas.ClipPath(Path.Circle(new Vector2(100, 100), 80));
        canvas.ClipRect(new RectF(60, 60, 80, 80), ClipOp.Difference);   // a square hole
        canvas.DrawPaint(new Rgba8(30, 90, 200, 255));                    // fills the whole clip
        canvas.Restore();

        // Only whole pixels inside the clip are drawn after a pixel-aligned rectangle clip, at no extra cost.
        canvas.ClipRect(new RectF(0, 0, 100, 200));
        canvas.FillCircle(new Vector2(100, 190), 40, Rgba8.Black);
        #endregion

        Assert.Equal(Rgba8.White, raster.GetPixel(100, 100));
    }

    [Fact]
    public void Images()
    {
        using SampleFiles files = new();

        #region images
        using Raster photo = Raster.Load("duck.png");
        using Raster<Rgba8> page = Raster.Create(800, 600, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = page.CreateCanvas())
        {
            // At its own size: whole-pixel placement blends exactly as DrawImage does.
            canvas.DrawImage(photo, new Vector2(10, 10));

            // Scaled into a rectangle and turned, sampled with any of Egami's filters.
            canvas.Save();
            canvas.Rotate(-8, new Vector2(500, 300));
            canvas.DrawImage(photo, new RectF(350, 150, 300, 300 * photo.Height / photo.Width), new SamplingOptions { Filter = ResampleFilter.Lanczos3 });
            canvas.Restore();

            // A part of the image, at half opacity.
            canvas.DrawImage(photo, source: new RectF(0, 0, 100, 100), destination: new RectF(10, 450, 140, 140), paint: new SolidPaint(Rgba8.Black) { Opacity = 0.5f });
        }
        #endregion

        Assert.Equal(800, page.Width);
    }

    [Fact]
    public void Pipeline()
    {
        using Raster<Rgba8> raster = Raster.Create(300, 200, Rgba8.White);

        #region pipeline
        // Draw as a step in an operation chain: inside Within the canvas covers the area, with its origin at the area's corner.
        raster.Apply(ops => ops
            .Draw(canvas => canvas.FillRect(new RectF(0, 0, 300, 200), new Rgba8(240, 240, 255, 255)))
            .Within(new Rect(100, 50, 100, 100), area => area.Draw(canvas => canvas.FillCircle(new Vector2(50, 50), 40, Rgba8.Red))),
            Ct);

        // On an animation the step runs once per frame, and can be told which.
        raster.Apply(ops => ops.Draw((canvas, frame) => canvas.FillCircle(new Vector2(20 + (frame * 4), 20), 10, Rgba8.Black)), Ct);
        #endregion

        Assert.Equal(Rgba8.Red, raster.GetPixel(150, 100));
    }

    [Fact]
    public void Backend()
    {
        using SvgCanvas svg = new(200, 100);
        svg.FillRect(new RectF(10, 10, 80, 40), Rgba8.Red);
        svg.StrokePath(Path.Circle(new Vector2(150, 50), 30), new Stroke(4), Rgba8.Black);
        Assert.Contains("<rect", svg.Document, StringComparison.Ordinal);
    }

    #region backend
    // A backend overrides hooks; the base class has already checked the arguments and tracks the transform and clip bounds.
    // This one writes SVG elements, keeping rectangles as rectangles and strokes as stroke settings.
    public sealed class SvgCanvas(float width, float height) : Canvas(width, height)
    {
        private readonly System.Text.StringBuilder _body = new();

        public string Document => $"<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"{Width}\" height=\"{Height}\">{_body}</svg>";

        protected override void OnDrawRect(RectF rect, Stroke? stroke, Paint paint)
            => _body.Append(FormattableString.Invariant($"<rect x=\"{rect.X}\" y=\"{rect.Y}\" width=\"{rect.Width}\" height=\"{rect.Height}\" {Style(stroke, paint)}/>"));

        protected override void OnDrawPath(Path path, Stroke? stroke, Paint paint)
        {
            System.Text.StringBuilder d = new();
            int p = 0;
            foreach (PathVerb verb in path.Verbs)
            {
                int count = verb switch { PathVerb.Quad => 2, PathVerb.Cubic => 3, PathVerb.Close => 0, _ => 1 };
                d.Append(verb switch { PathVerb.Move => "M", PathVerb.Line => "L", PathVerb.Quad => "Q", PathVerb.Cubic => "C", _ => "Z" });
                foreach (Vector2 point in path.Points.Slice(p, count))
                {
                    d.Append(FormattableString.Invariant($"{point.X},{point.Y} "));
                }

                p += count;
            }

            _body.Append(System.Globalization.CultureInfo.InvariantCulture, $"<path d=\"{d}\" {Style(stroke, paint)}/>");
        }

        protected override void OnClipPath(Path path, ClipOp op, bool antialias)
        {
            // Clips would become <clipPath> elements; this sketch leaves them out.
        }

        protected override void OnDrawImage(Raster image, RectF source, RectF destination, SamplingOptions sampling, Paint? paint)
        {
            // An image would be embedded as a PNG data URI.
        }

        private string Style(Stroke? stroke, Paint paint)
        {
            Rgba8 c = paint is SolidPaint solid ? solid.Color.ToRgba8() : Rgba8.Black;
            string color = $"#{c.R:x2}{c.G:x2}{c.B:x2}";
            string transform = Matrix.IsIdentity ? "" : FormattableString.Invariant($" transform=\"matrix({Matrix.M11} {Matrix.M12} {Matrix.M21} {Matrix.M22} {Matrix.M31} {Matrix.M32})\"");
            return stroke is null
                ? $"fill=\"{color}\"{transform}"
                : FormattableString.Invariant($"fill=\"none\" stroke=\"{color}\" stroke-width=\"{stroke.Width}\" stroke-linecap=\"{stroke.Cap.ToString().ToLowerInvariant()}\"{transform}");
        }
    }
    #endregion

    [Fact]
    public void Booleans()
    {
        #region booleans
        // A frame: one round rectangle cut out of another, kept as curves, so a PDF or SVG backend writes it exactly.
        Path frame = Path.RoundRect(new RoundRect(new RectF(0, 0, 200, 140), 24))
            .Difference(Path.RoundRect(new RoundRect(new RectF(20, 20, 160, 100), 12)));

        // Many shapes in one pass, applied left to right; overlaps and self-intersections removed; an area grown by 4 units.
        Path dots = Path.Combine([Path.Circle(new Vector2(40, 40), 30), Path.Circle(new Vector2(80, 40), 30), Path.Circle(new Vector2(60, 75), 30)], PathOp.Union);
        Path clean = Path.Star(new Vector2(50, 50), 45, 20, 5).Simplify();
        Path halo = dots.Inflate(4, LineJoin.Round);
        #endregion

        Assert.Equal(2, frame.Contours.Count);
        Assert.Single(dots.Contours);
        Assert.True(halo.TightBounds.Width > dots.TightBounds.Width + 7);
        Assert.False(clean.IsEmpty);
    }

    [Fact]
    public void Dashes()
    {
        #region dashes
        // A dashed route: the pattern travels with the stroke, so a PDF backend writes [8 4] 0 d instead of hundreds of pieces.
        Path route = new PathBuilder().MoveTo(10, 90).CubicTo(60, 0, 140, 160, 190, 60).ToPath();
        Stroke dashed = new(3) { Cap = LineCap.Round, Dash = new DashPattern([8, 4]) };
        using Raster<Rgba8> map = Raster.Create(200, 120, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = map.CreateCanvas())
        {
            canvas.StrokePath(route, dashed, new Rgba8(200, 60, 30, 255));
        }

        // The outline the stroke fills, as a path: for hit testing, or to export without strokes.
        Path outline = dashed.ToPath(route, simplify: true);
        #endregion

        Assert.True(outline.Contours.Count > 10);
    }

    [Fact]
    public void Measure()
    {
        #region measure
        // Measure a path: its length, and a point and direction at any distance, curves included.
        Path route = new PathBuilder().MoveTo(10, 90).CubicTo(60, 0, 140, 160, 190, 60).ToPath();
        PathMeasure measure = new(route);
        ContourMeasure contour = measure.Contours[0];
        Vector2 halfway = contour.GetPosition(contour.Length / 2);
        Vector2 heading = contour.GetTangent(contour.Length / 2);

        // An arrowhead placed at the end, turned along the path, and the middle third as a path of its own (still a cubic).
        Path arrow = Path.Polygon([new(-6, -4), new(0, 0), new(-6, 4)]).Transform(contour.GetMatrix(contour.Length));
        Path middle = contour.GetSegment(contour.Length / 3, 2 * contour.Length / 3);
        #endregion

        Assert.Equal(1, heading.Length(), 1e-3);
        Assert.True(halfway.X is > 60 and < 140);
        Assert.Equal(PathVerb.Cubic, middle.Verbs[1]);
        Assert.False(arrow.IsEmpty);
    }

    [Fact]
    public void Effects()
    {
        #region effects
        // A hand-drawn chart line: jittered the same way on every run, then its corners rounded.
        Path data = Path.Polygon([new(10, 80), new(50, 30), new(90, 60), new(130, 20), new(170, 50)], close: false);
        PathEffect sketchy = PathEffect.Jitter(segmentLength: 6, deviation: 1.5f, seed: 7).Then(PathEffect.Corner(3));
        using Raster<Rgba8> chart = Raster.Create(180, 100, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = chart.CreateCanvas())
        {
            canvas.StrokePath(data, new Stroke(2) { Effect = sketchy }, new Rgba8(40, 40, 40, 255));

            // Dots along the line, and hatching inside a circle.
            canvas.FillPath(data.Apply(PathEffect.Stamp(Path.Circle(Vector2.Zero, 2), advance: 12, style: StampStyle.Translate)), Rgba8.Black);
            canvas.FillPath(Path.Circle(new Vector2(150, 75), 20).Apply(PathEffect.Hatch(Matrix3x2.CreateScale(1, 5) * Matrix3x2.CreateRotation(0.6f), width: 1)), Rgba8.Black);
        }

        // A line that draws itself: trim it to a growing fraction of its length, frame by frame.
        Path frame30 = data.Apply(PathEffect.Trim(0, 0.3f));
        #endregion

        Assert.Equal(0.3f * new PathMeasure(data).TotalLength, new PathMeasure(frame30).TotalLength, 1e-3f);
    }

    [Fact]
    public void Svg()
    {
        #region svg
        // An icon from SVG path data, drawn four times larger, and written back as compact relative data.
        Path heart = Path.ParseSvg("M12 21.35l-1.45-1.32C5.4 15.36 2 12.28 2 8.5 2 5.42 4.42 3 7.5 3c1.74 0 3.41.81 4.5 2.09" +
                                   "C13.09 3.81 14.76 3 16.5 3 19.58 3 22 5.42 22 8.5c0 3.78-3.4 6.86-8.55 11.54L12 21.35z");
        string d = heart.Transform(Matrix3x2.CreateScale(4)).ToSvg(new SvgPathFormat { Relative = true, Compact = true });

        // Untrusted data: TryParseSvg says whether it is valid; lenient keeps what comes before an error, as browsers draw it.
        bool valid = Path.TryParseSvg("M0 0 L10 10 L20", out Path? strict);
        Path drawn = Path.ParseSvg("M0 0 L10 10 L20", lenient: true);
        try
        {
            Path.ParseSvg("M0 0 X");
        }
        catch (SvgPathException e)
        {
            Console.WriteLine($"{e.Problem} at character {e.Position}");
        }
        #endregion

        Assert.StartsWith("m48 85.4", d, StringComparison.Ordinal);
        Assert.False(valid);
        Assert.Null(strict);
        Assert.Equal(2, drawn.Points.Length);
    }

    [Fact]
    public void DrawItself()
    {
        #region draw-itself
        // Frames of a signature drawing itself: each frame strokes a longer part of the path.
        Path signature = Path.ParseSvg("M10 60 C30 10 50 110 70 60 S110 10 130 60 S170 110 190 40");
        List<Raster<Rgba8>> frames = [];
        for (int i = 1; i <= 24; i++)
        {
            Raster<Rgba8> frame = Raster.Create(200, 100, Rgba8.White);
            using (RasterCanvas<Rgba8> canvas = frame.CreateCanvas())
            {
                Stroke pen = new(3) { Cap = LineCap.Round, Effect = PathEffect.Trim(0, i / 24f) };
                canvas.StrokePath(signature, pen, new Rgba8(20, 40, 120, 255));
            }

            frames.Add(frame);
        }
        #endregion

        Assert.Equal(Rgba8.White, frames[0].GetPixel(190, 40));
        Assert.NotEqual(Rgba8.White, frames[^1].GetPixel(190, 40));
        frames.ForEach(f => f.Dispose());
    }

    [Fact]
    public void Badge()
    {
        #region badge
        // A rounded badge with an outline and a dot, drawn at twice the size for a sharp result on high-density screens.
        using Raster<Rgba8> badge = Raster.Create<Rgba8>(240, 80);
        using (RasterCanvas<Rgba8> canvas = badge.CreateCanvas())
        {
            canvas.Scale(2);
            RoundRect pill = new(new RectF(2, 2, 116, 36), 18);
            canvas.FillRoundRect(pill, new Rgba8(46, 160, 67, 255));
            canvas.StrokeRoundRect(pill, new Stroke(2), new Rgba8(20, 100, 40, 255));
            canvas.FillCircle(new Vector2(22, 20), 7, Rgba8.White);
        }
        #endregion

        Assert.Equal(Rgba8.White, badge.GetPixel(44, 40));
    }

    [Fact]
    public void ChartAxes()
    {
        #region chart-axes
        // Axes, ticks and a data line for a chart: hairlines stay one pixel wide whatever the scale.
        double[] values = [3, 7, 4, 9, 6, 11, 8];
        using Raster<Rgba8> chart = Raster.Create(420, 260, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = chart.CreateCanvas())
        {
            Rgba8 axis = new(60, 60, 60, 255);
            canvas.DrawLine(new Vector2(40.5f, 20), new Vector2(40.5f, 230.5f), Stroke.Hairline, axis);
            canvas.DrawLine(new Vector2(40, 230.5f), new Vector2(400, 230.5f), Stroke.Hairline, axis);
            for (int i = 0; i < values.Length; i++)
            {
                float x = 40.5f + (i * 55);
                canvas.DrawLine(new Vector2(x, 230), new Vector2(x, 236), Stroke.Hairline, axis);
            }

            Vector2[] points = [.. values.Select((v, i) => new Vector2(40 + (i * 55), 230 - (float)(v * 18)))];
            canvas.DrawPoints(PointMode.Polygon, points, new Stroke(3) { Join = LineJoin.Round, Cap = LineCap.Round }, new Rgba8(30, 90, 200, 255));
            canvas.DrawPoints(PointMode.Points, points, new Stroke(8) { Cap = LineCap.Round }, new Rgba8(30, 90, 200, 255));
        }
        #endregion

        Assert.Equal(new Rgba8(60, 60, 60, 255), chart.GetPixel(40, 100));
    }

    [Fact]
    public void ClippedPhoto()
    {
        using SampleFiles files = new();

        #region clipped-photo
        // A round avatar: clip to a circle, draw the photo scaled to fill it, then ring it.
        using Raster photo = Raster.Load("duck.png");
        using Raster<Rgba8> avatar = Raster.Create<Rgba8>(256, 256);
        using (RasterCanvas<Rgba8> canvas = avatar.CreateCanvas())
        {
            Path circle = Path.Circle(new Vector2(128, 128), 120);
            canvas.Save();
            canvas.ClipPath(circle);
            int side = Math.Min(photo.Width, photo.Height);
            RectF square = new((photo.Width - side) / 2, (photo.Height - side) / 2, side, side);
            canvas.DrawImage(photo, square, new RectF(8, 8, 240, 240));
            canvas.Restore();
            canvas.StrokePath(circle, new Stroke(6), Rgba8.White);
        }

        avatar.Save("avatar.png");
        #endregion

        Assert.Equal(0, avatar.GetPixel(2, 2).A);
    }
}
