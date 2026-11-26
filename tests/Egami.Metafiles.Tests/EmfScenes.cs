// The drawings EMF writing is tested and gated with: Egami.Metafiles.Tests draws each straight onto a raster canvas and through
// EmfCanvas, and egami-compare links this file (EGAMI_COMPARE) to play the same files with GDI, GDI+, Word and LibreOffice.
using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

#if EGAMI_COMPARE
namespace Egami.Compare;
#else
namespace Egami.Metafiles.Tests;
#endif

/// <summary>Scenes drawn on a canvas 160 by 120, by family.</summary>
internal static class EmfScenes
{
    /// <summary>The canvas size every scene is drawn at.</summary>
    public const int W = 160, H = 120;

    /// <summary>A 4 by 4 checker with one red corner, for nearest-neighbor sampling.</summary>
    public static readonly Raster<Rgba8> Checker = MakeChecker();

    /// <summary>A smooth opaque image, for bilinear sampling and JPEG.</summary>
    public static readonly Raster<Rgba8> Photo = MakePhoto();

    /// <summary>The photo as PNG and as a baseline JPEG.</summary>
    public static readonly byte[] PhotoPng = Encode(Photo, png: true), PhotoJpeg = Encode(Photo, png: false);

    private static readonly Rgba8 Teal = new(0, 128, 128, 255), Orange = new(255, 140, 0, 255), Navy = new(20, 30, 120, 255);
    private static readonly RgbaF32 Red = new(0.9f, 0.1f, 0.1f, 1), Blue = new(0.1f, 0.2f, 0.9f, 1), Yellow = new(1, 0.85f, 0.1f, 1);
    private static readonly GradientStop[] Three = [new(0, Red), new(0.4f, Yellow), new(1, Blue)];

    /// <summary>Shapes, strokes, transforms and clips, which stage A writes as vectors.</summary>
    public static readonly Dictionary<string, Action<Canvas>> Vectors = new()
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

    /// <summary>Scenes whose paint EMF+ writes as a brush: no fallback image, no warning.</summary>
    public static readonly Dictionary<string, Action<Canvas>> Paints = new()
    {
        ["linear-clamp"] = c => c.FillRect(new RectF(10, 10, 140, 100), new LinearGradient(new Vector2(40, 0), new Vector2(120, 0), Three)),
        ["linear-diagonal-transformed"] = c =>
        {
            c.Translate(80, 60);
            c.Rotate(20);
            c.Scale(1.2f, 0.8f);
            c.FillOval(new RectF(-60, -50, 120, 100), new LinearGradient(new Vector2(-30, -30), new Vector2(30, 20), Three) { LocalMatrix = Matrix3x2.CreateSkew(0.2f, 0) });
        },
        ["linear-repeat"] = c => c.FillRect(new RectF(10, 10, 140, 100), new LinearGradient(new Vector2(30, 30), new Vector2(60, 45), Red, Blue) { Tile = TileMode.Repeat }),
        ["linear-mirror"] = c => c.FillPath(Star(new Vector2(80, 60), 55, FillRule.NonZero), new LinearGradient(new Vector2(0, 20), new Vector2(0, 50), Three) { Tile = TileMode.Mirror }),
        ["linear-alpha"] = c =>
        {
            c.FillRect(new RectF(0, 0, 160, 120), new Rgba8(250, 250, 240, 255));
            c.FillRect(new RectF(10, 10, 140, 100), new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), new RgbaF32(0.9f, 0.1f, 0.1f, 1), new RgbaF32(0.1f, 0.2f, 0.9f, 0.1f)) { Opacity = 0.8f });
        },
        ["linear-light"] = c => c.FillRect(new RectF(10, 10, 140, 100), new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), Red, Blue) { Interpolation = GradientInterpolation.LinearSrgb }),
        ["linear-stroke"] = c => c.StrokePath(Star(new Vector2(80, 60), 45, FillRule.NonZero), new Stroke(9) { Join = LineJoin.Round }, new LinearGradient(new Vector2(20, 20), new Vector2(140, 100), Three)),
        ["radial-clamp"] = c => c.FillRect(new RectF(5, 5, 150, 110), new RadialGradient(new Vector2(70, 55), 40, Three)),
        ["radial-inside"] = c => c.FillOval(new RectF(30, 10, 100, 100), new RadialGradient(new Vector2(80, 60), 50, Yellow, Blue)),
        ["radial-repeat"] = c => c.FillRect(new RectF(5, 5, 150, 110), new RadialGradient(new Vector2(80, 60), 18, Red, Yellow) { Tile = TileMode.Mirror, LocalMatrix = Matrix3x2.CreateScale(1.5f, 1, new Vector2(80, 60)) }),
        ["radial-alpha"] = c =>
        {
            c.FillRect(new RectF(0, 0, 160, 120), new Rgba8(30, 30, 30, 255));
            c.FillOval(new RectF(20, 10, 120, 100), new RadialGradient(new Vector2(80, 60), 60, new RgbaF32(1, 1, 1, 1), new RgbaF32(1, 1, 1, 0)));
        },
        ["image-repeat"] = c => c.FillRect(new RectF(10, 10, 140, 100), new ImagePaint(Checker, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Nearest }) { LocalMatrix = Matrix3x2.CreateScale(4) }),
        ["image-mirror-decal"] = c =>
        {
            c.FillRect(new RectF(10, 10, 70, 100), new ImagePaint(Checker, TileMode.Mirror, TileMode.Mirror, new SamplingOptions { Filter = ResampleFilter.Nearest }) { LocalMatrix = Matrix3x2.CreateScale(3) * Matrix3x2.CreateTranslation(10, 10) });
            c.FillRect(new RectF(85, 10, 70, 100), new ImagePaint(Checker, TileMode.Decal, TileMode.Decal, new SamplingOptions { Filter = ResampleFilter.Nearest }) { LocalMatrix = Matrix3x2.CreateScale(5) * Matrix3x2.CreateTranslation(95, 30), Opacity = 0.6f });
        },
        ["picture-pattern"] = c =>
        {
            PictureRecorder recorder = new(new RectF(0, 0, 20, 20));
            recorder.FillRect(new RectF(0, 0, 20, 20), new Rgba8(240, 240, 255, 255));
            recorder.FillOval(new RectF(3, 3, 14, 14), new Rgba8(200, 40, 40, 255));
            c.FillRect(new RectF(10, 10, 140, 100), new PicturePaint(recorder.Finish()));
        },
        ["color-matrix"] = c => c.FillOval(new RectF(20, 10, 120, 100), new SolidPaint(new Rgba8(200, 120, 40, 255)).WithColorMatrix(ColorMatrix.Sepia().Then(ColorMatrix.HueRotate(90)))),
        ["blend-paint"] = c => c.FillRect(new RectF(20, 20, 120, 80), new BlendPaint(new SolidPaint(new Rgba8(40, 160, 200, 255)), new SolidPaint(new Rgba8(250, 120, 30, 200)), BlendMode.Multiply)),
        ["layer-one-shape"] = c =>
        {
            c.FillRect(new RectF(0, 0, 160, 120), new Rgba8(255, 255, 255, 255));
            c.SaveLayer(0.5f);
            c.StrokePath(Star(new Vector2(80, 60), 50, FillRule.NonZero), new Stroke(12), new Rgba8(0, 90, 200, 255));
            c.Restore();
        },
        ["layer-apart"] = c =>
        {
            c.FillRect(new RectF(0, 0, 160, 120), new Rgba8(255, 255, 255, 255));
            c.SaveLayer(new LayerOptions { Opacity = 0.4f, Bounds = new RectF(0, 0, 160, 120) });
            c.FillRect(new RectF(10, 10, 60, 40), new Rgba8(200, 0, 0, 255));
            c.FillOval(new RectF(90, 10, 60, 50), new LinearGradient(new Vector2(90, 0), new Vector2(150, 0), Red, Blue));
            c.FillRect(new RectF(10.5f, 70.5f, 140, 40), new Rgba8(0, 120, 0, 255));
            c.Restore();
        },
    };

    /// <summary>Images drawn with each sampling, turned, with opacity, kept encoded, and as lattices and atlases.</summary>
    public static readonly Dictionary<string, Action<Canvas>> Images = new()
    {
        ["nearest"] = c => c.DrawImage(Checker, new RectF(20, 20, 80, 80), new SamplingOptions { Filter = ResampleFilter.Nearest }),
        ["nearest-source"] = c => c.DrawImage(Checker, new RectF(1, 1, 2, 3), new RectF(30, 10, 60, 90), new SamplingOptions { Filter = ResampleFilter.Nearest }),
        ["nearest-rotated"] = c =>
        {
            c.Translate(80, 60);
            c.Rotate(30);
            c.DrawImage(Checker, new RectF(-40, -40, 80, 80), new SamplingOptions { Filter = ResampleFilter.Nearest });
        },
        ["opacity"] = c => c.DrawImage(Checker, new RectF(20, 20, 80, 80), new SamplingOptions { Filter = ResampleFilter.Nearest }, new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Opacity = 0.5f }),
        ["photo"] = c => c.DrawImage(Photo, new RectF(10, 10, 120, 80), new SamplingOptions { Filter = ResampleFilter.GdiPlusBilinear }),
        ["encoded-png"] = c => c.DrawImage(EncodedImage.Load(PhotoPng), new RectF(10, 10, 120, 80), new SamplingOptions { Filter = ResampleFilter.GdiPlusBilinear }),
        ["encoded-jpeg"] = c => c.DrawImage(EncodedImage.Load(PhotoJpeg), new RectF(10, 10, 120, 80), new SamplingOptions { Filter = ResampleFilter.GdiPlusBilinear }),
        ["lattice"] = c => c.DrawImageLattice(Photo, Lattice.NinePatch(new Rect(6, 4, 12, 8)), new RectF(10, 10, 140, 100), new SamplingOptions { Filter = ResampleFilter.Nearest }),
        ["atlas"] = c => c.DrawAtlas(
            Checker,
            [new SpriteTransform(10, 0, 10, 10), new SpriteTransform(0, 10, 120, 20)],
            [new RectF(0, 0, 2, 2), new RectF(2, 2, 2, 2)],
            sampling: new SamplingOptions { Filter = ResampleFilter.Nearest }),
    };

    /// <summary>What EMF+ cannot say, which the writer draws as an image with a warning.</summary>
    public static readonly Dictionary<string, Action<Canvas>> Fallbacks = new()
    {
        ["sweep"] = c => c.FillOval(new RectF(10, 10, 140, 100), new SweepGradient(new Vector2(80, 60), Three)),
        ["conical"] = c => c.FillOval(new RectF(10, 10, 140, 100), new ConicalGradient(new Vector2(60, 50), 5, new Vector2(80, 60), 50, Three)),
        ["oklab"] = c => c.FillOval(new RectF(10, 10, 140, 100), new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), Red, Blue) { Interpolation = GradientInterpolation.Oklab }),
        ["blend-multiply"] = c =>
        {
            c.FillRect(new RectF(0, 0, W, H), new Rgba8(255, 255, 255, 255));
            c.FillRect(new RectF(20, 20, 80, 60), new Rgba8(220, 40, 40, 255));
            c.FillOval(new RectF(60, 40, 80, 60), new SolidPaint(new Rgba8(40, 80, 220, 255)) { BlendMode = BlendMode.Multiply });
        },
        ["layer-overlap"] = c =>
        {
            c.FillRect(new RectF(0, 0, W, H), new Rgba8(255, 255, 255, 255));
            c.SaveLayer(0.5f);
            c.FillOval(new RectF(20, 20, 80, 80), new Rgba8(200, 0, 0, 255));
            c.FillOval(new RectF(60, 20, 80, 80), new Rgba8(0, 0, 200, 255));
            c.Restore();
        },
    };

    /// <summary>Text in two faces and turned, on white.</summary>
    public static Action<Canvas> Text(Typeface regular, Typeface bold) => c =>
    {
        c.FillRect(new RectF(0, 0, W, H), new Rgba8(255, 255, 255, 255));
        c.DrawText("Egami EMF", new Vector2(8, 40), new Font(regular, 24), new SolidPaint(new Rgba8(20, 30, 120, 255)));
        c.DrawText("Bold text", new Vector2(8, 80), new Font(bold, 18), new SolidPaint(new Rgba8(180, 20, 20, 255)));
        c.Save();
        c.Translate(100, 110);
        c.Rotate(-20);
        c.DrawText("turned", Vector2.Zero, new Font(regular, 14), new SolidPaint(new Rgba8(0, 120, 60, 255)));
        c.Restore();
    };

    /// <summary>
    /// A drawing of <paramref name="count"/> shapes for write speed: rectangles, ovals, stroked lines and filled stars in turn,
    /// in colors with alpha, at places a fixed seed picks.
    /// </summary>
    public static Action<Canvas> Many(int count)
    {
        Random random = new(7);
        (int Kind, RectF Box, Rgba8 Color)[] shapes = new (int, RectF, Rgba8)[count];
        for (int i = 0; i < count; i++)
        {
            float x = random.NextSingle() * (W - 20), y = random.NextSingle() * (H - 20);
            RectF box = new(x, y, 4 + (random.NextSingle() * 16), 4 + (random.NextSingle() * 16));
            shapes[i] = (i % 4, box, new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)(128 + random.Next(128))));
        }

        Path star = Star(new Vector2(10, 10), 10, FillRule.NonZero);
        Stroke stroke = new(1.5f);
        return c =>
        {
            foreach ((int kind, RectF box, Rgba8 color) in shapes)
            {
                switch (kind)
                {
                    case 0:
                        c.FillRect(box, color);
                        break;
                    case 1:
                        c.FillOval(box, color);
                        break;
                    case 2:
                        c.DrawLine(new Vector2(box.X, box.Y), new Vector2(box.Right, box.Bottom), stroke, color);
                        break;
                    default:
                        c.Save();
                        c.Translate(box.X, box.Y);
                        c.FillPath(star, color);
                        c.Restore();
                        break;
                }
            }
        };
    }

    public static Path Star(Vector2 center, float radius, FillRule rule)
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

    private static Raster<Rgba8> MakeChecker()
    {
        Raster<Rgba8> r = Raster.Create<Rgba8>(4, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                r.Row(y)[x] = ((x + y) & 1) == 0 ? new Rgba8(30, 60, 160, 255) : new Rgba8(250, 200, 60, 255);
            }
        }

        r.Row(0)[0] = new Rgba8(220, 30, 30, 255);
        return r;
    }

    private static Raster<Rgba8> MakePhoto()
    {
        Raster<Rgba8> r = Raster.Create<Rgba8>(24, 16);
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 24; x++)
            {
                r.Row(y)[x] = new Rgba8((byte)(40 + (x * 8)), (byte)(60 + (y * 10)), (byte)(200 - (x * 4) - (y * 3)), 255);
            }
        }

        return r;
    }

    private static byte[] Encode(Raster<Rgba8> image, bool png)
    {
        using MemoryStream ms = new();
        if (png)
        {
            image.Save(ms, PngFormat.Info);
        }
        else
        {
            image.Save(ms, JpegFormat.Info, new JpegWriterOptions { Quality = 95 });
        }

        return ms.ToArray();
    }
}
