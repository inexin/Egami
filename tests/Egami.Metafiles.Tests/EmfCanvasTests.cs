using System.Numerics;

using Egami.Drawing;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Metafiles.Tests;

/// <summary>
/// EMF writing, stage A: every canvas call drawn straight onto a raster canvas and again through <see cref="EmfCanvas"/> then
/// Egami's own player, EMF+ and GDI parts, compared pixel by pixel.
/// </summary>
public class EmfCanvasTests(ITestOutputHelper output)
{
    internal const int W = 160, H = 120;

    private static readonly Rgba8 Teal = new(0, 128, 128, 255), Orange = new(255, 140, 0, 255), Navy = new(20, 30, 120, 255);

    /// <summary>The scenes, by name: each draws with canvas calls stage A writes as vectors.</summary>
    internal static readonly Dictionary<string, Action<Canvas>> Scenes = new()
    {
        ["fill-rect"] = c => c.FillRect(new RectF(10.5f, 12, 80, 50), Teal),
        ["fill-alpha"] = c =>
        {
            c.FillRect(new RectF(10, 10, 90, 70), Teal);
            c.FillOval(new RectF(50, 30, 90, 70), new SolidPaint(Orange) { Opacity = 0.5f });
            c.FillRect(new RectF(30, 60, 60, 40), new Rgba8(255, 0, 0, 100));
        },
        ["fill-oval-roundrect"] = c =>
        {
            c.FillOval(new RectF(8, 8, 70, 50), Navy);
            c.FillRoundRect(new RoundRect(new RectF(80, 20, 70, 80), 14, 9), Orange);
            c.FillCircle(new Vector2(40, 90), 22, Teal);
        },
        ["fill-nonzero-star"] = c => c.FillPath(Star(new Vector2(80, 60), 55, FillRule.NonZero), Navy),
        ["fill-evenodd-star"] = c => c.FillPath(Star(new Vector2(80, 60), 55, FillRule.EvenOdd), Navy),
        ["fill-nonzero-holes"] = c =>
        {
            // Two overlapping contours wound the same way and a counter-wound hole: nonzero keeps the overlap, fills the hole's area.
            PathBuilder b = new();
            b.AddRect(new RectF(10, 10, 80, 80)).AddRect(new RectF(50, 30, 90, 80));
            b.MoveTo(new Vector2(20, 20)).LineTo(new Vector2(20, 60)).LineTo(new Vector2(40, 60)).LineTo(new Vector2(40, 20)).Close();
            c.FillPath(b.ToPath(FillRule.NonZero), Teal);
        },
        ["fill-curves"] = c =>
        {
            PathBuilder b = new PathBuilder().MoveTo(new Vector2(10, 100)).QuadTo(new Vector2(40, 0), new Vector2(80, 100))
                .CubicTo(new Vector2(100, 20), new Vector2(140, 140), new Vector2(150, 30)).LineTo(new Vector2(150, 110)).Close();
            c.FillPath(b.ToPath(), Orange);
        },
        ["stroke-caps"] = c =>
        {
            float y = 20;
            foreach (LineCap cap in Enum.GetValues<LineCap>())
            {
                c.DrawLine(new Vector2(20, y), new Vector2(140, y), new Stroke(12) { Cap = cap }, Navy);
                y += 35;
            }
        },
        ["stroke-joins"] = c =>
        {
            float x = 10;
            foreach (LineJoin join in Enum.GetValues<LineJoin>())
            {
                Path zig = Path.Polygon([new Vector2(x, 100), new Vector2(x + 20, 20), new Vector2(x + 40, 100)], close: false);
                c.StrokePath(zig, new Stroke(10) { Join = join, MiterLimit = 8 }, Teal);
                x += 50;
            }
        },
        ["stroke-miter-limit"] = c =>
        {
            Path sharp = Path.Polygon([new Vector2(20, 100), new Vector2(50, 20), new Vector2(60, 100)], close: false);
            c.StrokePath(sharp, new Stroke(8) { MiterLimit = 2 }, Navy);
            c.StrokePath(sharp.Transform(Matrix3x2.CreateTranslation(70, 0)), new Stroke(8) { MiterLimit = 10 }, Navy);
        },
        ["stroke-dashes"] = c =>
        {
            c.DrawLine(new Vector2(10, 15), new Vector2(150, 15), new Stroke(4) { Dash = new DashPattern([12, 6]) }, Navy);
            c.DrawLine(new Vector2(10, 40), new Vector2(150, 40), new Stroke(6) { Dash = new DashPattern([10, 8], 5), Cap = LineCap.Round }, Orange);
            c.DrawLine(new Vector2(10, 65), new Vector2(150, 65), new Stroke(5) { Dash = new DashPattern([8, 10, 2, 10], 3), Cap = LineCap.Square }, Teal);
            c.StrokeRect(new RectF(20, 80, 120, 30), new Stroke(3) { Dash = new DashPattern([9, 5]) }, Navy);
        },
        ["stroke-shapes"] = c =>
        {
            c.StrokeRect(new RectF(10, 10, 60, 40), new Stroke(5), Navy);
            c.StrokeOval(new RectF(80, 10, 70, 50), new Stroke(4), Orange);
            c.StrokeRoundRect(new RoundRect(new RectF(10, 60, 140, 50), 12, 12), new Stroke(3) { Join = LineJoin.Round }, new SolidPaint(Teal) { Opacity = 0.7f });
        },
        ["stroke-hairline"] = c =>
        {
            c.DrawLine(new Vector2(10, 10), new Vector2(150, 110), Stroke.Hairline, Navy);
            c.StrokeRect(new RectF(20.5f, 20.5f, 100, 60), Stroke.Hairline, Orange);
        },
        ["transforms"] = c =>
        {
            c.Save();
            c.Translate(80, 60);
            c.Rotate(30);
            c.Scale(1.5f, 0.8f);
            c.FillRect(new RectF(-30, -20, 60, 40), Teal);
            c.StrokeOval(new RectF(-40, -30, 80, 60), new Stroke(3), Navy);
            c.Restore();
            c.Save();
            c.Skew(20, 0);
            c.FillRect(new RectF(5, 90, 40, 20), Orange);
            c.Restore();
            c.FillRect(new RectF(120, 90, 30, 20), Navy);
        },
        ["clip-rect"] = c =>
        {
            c.Save();
            c.ClipRect(new RectF(20, 20, 100, 60));
            c.FillOval(new RectF(0, 0, 160, 120), Teal);
            c.Restore();
            c.Save();
            c.ClipRect(new RectF(60, 40, 40, 40), ClipOp.Difference);
            c.StrokeRect(new RectF(40, 30, 80, 70), new Stroke(8), Orange);
            c.Restore();
            c.FillRect(new RectF(140, 100, 15, 15), Navy);
        },
        ["clip-path"] = c =>
        {
            c.Save();
            c.ClipPath(Path.Oval(new RectF(10, 10, 100, 90)));
            c.FillRect(new RectF(0, 0, 160, 120), Teal);
            c.ClipPath(Star(new Vector2(60, 55), 40, FillRule.NonZero), ClipOp.Difference);
            c.FillRect(new RectF(0, 0, 160, 120), Navy);
            c.Restore();
            c.Save();
            c.Rotate(10);
            c.ClipRect(new RectF(110, 10, 40, 80));
            c.FillRect(new RectF(0, 0, 160, 120), Orange);
            c.Restore();
        },
        ["aliased"] = c =>
        {
            c.FillOval(new RectF(10, 10, 80, 60), new SolidPaint(Navy) { Antialias = false });
            c.DrawLine(new Vector2(20, 100), new Vector2(150, 30), new Stroke(3), new SolidPaint(Orange) { Antialias = false });
            c.FillOval(new RectF(90, 60, 60, 50), Teal);
        },
        ["clear-and-paint"] = c =>
        {
            c.Clear(new Rgba8(240, 240, 230, 255));
            c.Save();
            c.ClipRect(new RectF(30, 30, 100, 60));
            c.DrawPaint(new SolidPaint(Teal));
            c.Clear(new Rgba8(255, 0, 0, 128));
            c.Restore();
        },
        ["points"] = c =>
        {
            Vector2[] pts = [new(20, 20), new(60, 80), new(100, 30), new(140, 100)];
            c.DrawPoints(PointMode.Points, pts, new Stroke(6) { Cap = LineCap.Round }, Navy);
            c.DrawPoints(PointMode.Lines, pts, new Stroke(2), Orange);
            c.Translate(0, 10);
            c.DrawPoints(PointMode.Polygon, pts, new Stroke(3) { Join = LineJoin.Bevel }, Teal);
        },
    };

    public static TheoryData<string> SceneNames => [.. Scenes.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The scenes without alpha, which GDI draws as Egami does but for antialiasing.</summary>
    public static TheoryData<string> OpaqueSceneNames => [.. Scenes.Keys.Where(k => k is not ("fill-alpha" or "clear-and-paint")).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(SceneNames))]
    public void EmfPlusPlaysLikeTheRasterCanvas(string scene)
    {
        // Exact but for GDI+'s one-pixel hairline and its curve flattening at a few edge pixels.
        (double mean, int max, int over) = Compare(scene, EmfWriteOptions.Default, MetafilePlayback.Auto);
        Assert.True(mean < 0.3 && over < 50, $"{scene}: mean {mean:F3}, max {max}, {over} pixels off by more than 32");
    }

    [Theory]
    [MemberData(nameof(OpaqueSceneNames))]
    public void GdiPartPlaysCloseToTheRasterCanvas(string scene)
    {
        // GDI has whole-pixel coordinates and GDI+ (whose frame rule the file follows) one pixel more than GDI across the frame,
        // so the GDI part is compared with coverage cut at half and a looser tolerance.
        (double mean, int max, int over) = Compare(scene, EmfWriteOptions.Default, MetafilePlayback.Gdi, opaque: true);
        Assert.True(mean < 6, $"{scene}: mean {mean:F3}, max {max}, {over} pixels off by more than 32");
    }

    private (double Mean, int Max, int Over) Compare(string scene, EmfWriteOptions options, MetafilePlayback playback, bool opaque = false)
    {
        Action<Canvas> draw = Scenes[scene];
        using Raster<Rgba8> direct = Direct(draw);
        byte[] file = Write(draw, options);
        using Raster<Rgba8> played = Play(file, playback);
        if (opaque)
        {
            Flatten(direct);
            Flatten(played);
        }

        (double mean, int max, int over) = Difference(direct, played);
        output.WriteLine($"{scene} {playback}: mean {mean:F3}, max {max}, over {over}");
        return (mean, max, over);
    }

    internal static Raster<Rgba8> Direct(Action<Canvas> draw, int w = W, int h = H)
    {
        Raster<Rgba8> r = Raster.Create<Rgba8>(w, h);
        using (RasterCanvas<Rgba8> c = r.CreateCanvas())
        {
            draw(c);
        }

        return r;
    }

    internal static byte[] Write(Action<Canvas> draw, EmfWriteOptions? options = null, int w = W, int h = H)
    {
        using MemoryStream ms = new();
        using (EmfCanvas c = new(ms, w, h, options))
        {
            draw(c);
        }

        return ms.ToArray();
    }

    internal static Raster<Rgba8> Play(byte[] file, MetafilePlayback playback = MetafilePlayback.Auto, int w = W, int h = H)
        => Metafile.Load(file).Rasterize<Rgba8>(new MetafileRenderOptions { Width = w, Height = h, Playback = playback });

    /// <summary>The mean channel difference, the largest, and how many pixels differ by more than 32 in a channel.</summary>
    internal static (double Mean, int Max, int Over) Difference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        long sum = 0;
        int max = 0, over = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                Rgba8 p = ra[x], q = rb[x];
                int d = Math.Max(Math.Max(Math.Abs(p.R - q.R), Math.Abs(p.G - q.G)), Math.Max(Math.Abs(p.B - q.B), Math.Abs(p.A - q.A)));
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) + Math.Abs(p.A - q.A);
                max = Math.Max(max, d);
                over += d > 32 ? 1 : 0;
            }
        }

        return (sum / (4.0 * a.Width * a.Height), max, over);
    }

    /// <summary>Every pixel over white, then any partly covered one as either its color or white: what GDI draws, roughly.</summary>
    private static void Flatten(Raster<Rgba8> r)
    {
        for (int y = 0; y < r.Height; y++)
        {
            Span<Rgba8> row = r.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                Rgba8 p = row[x];
                row[x] = p.A < 128 ? new Rgba8(255, 255, 255, 255) : new Rgba8(p.R, p.G, p.B, 255);
            }
        }
    }

    internal static Path Star(Vector2 center, float radius, FillRule rule)
    {
        PathBuilder b = new();
        for (int i = 0; i < 5; i++)
        {
            float a = (i * 4 * MathF.PI / 5) - (MathF.PI / 2);
            Vector2 p = center + (new Vector2(MathF.Cos(a), MathF.Sin(a)) * radius);
            if (i == 0)
            {
                b.MoveTo(p);
            }
            else
            {
                b.LineTo(p);
            }
        }

        return b.Close().ToPath(rule);
    }
}
