using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Svg.Tests;

/// <summary>Task 13's done-when: canvas calls written by SvgCanvas, read back and drawn by Egami.Svg, look like the same calls drawn straight onto a raster.</summary>
public class SvgCanvasRoundTripTests(ITestOutputHelper output)
{
    private const int Width = 200;
    private const int Height = 150;

    private static readonly GradientStop[] s_rainbow = [new(0, new RgbaF32(1, 0, 0, 1)), new(0.5f, new RgbaF32(1, 1, 0, 1)), new(1, new RgbaF32(0, 0.4f, 1, 1))];

    private static readonly Dictionary<string, Action<Canvas>> s_scenes = new(StringComparer.Ordinal)
    {
        ["paths"] = c =>
        {
            c.FillPath(Path.Star(new Vector2(50, 50), 40, 15, 7), new Rgba8(200, 40, 40, 255));
            Path evenOdd = new PathBuilder().AddRect(new RectF(100, 10, 80, 80)).AddOval(new RectF(115, 25, 50, 50)).ToPath(FillRule.EvenOdd);
            c.FillPath(evenOdd, new Rgba8(30, 90, 200, 255));
            Path curve = new PathBuilder().MoveTo(10, 140).CubicTo(40, 90, 80, 160, 110, 110).QuadTo(150, 80, 190, 140).Close().ToPath();
            c.FillPath(curve, new Rgba8(20, 160, 90, 200));
        },
        ["strokes"] = c =>
        {
            c.StrokePath(Path.Polygon([new(10, 20), new(60, 70), new(110, 20)], close: false), new Stroke(10) { Join = LineJoin.Miter, Cap = LineCap.Square }, new Rgba8(0, 0, 0, 255));
            c.StrokePath(Path.Polygon([new(120, 20), new(150, 70), new(190, 20)], close: false), new Stroke(10) { Join = LineJoin.Round, Cap = LineCap.Round }, new Rgba8(0, 90, 0, 255));
            c.StrokePath(Path.Line(new Vector2(10, 95), new Vector2(190, 95)), new Stroke(6) { Dash = new DashPattern([12, 6, 3, 6]) }, new Rgba8(200, 0, 0, 255));
            c.StrokePath(Path.Oval(new RectF(20, 105, 160, 40)), new Stroke(4) { Dash = new DashPattern([10, 5], 4), Cap = LineCap.Round }, new Rgba8(40, 40, 160, 255));
        },
        ["gradients"] = c =>
        {
            c.FillRect(new RectF(5, 5, 90, 60), new LinearGradient(new Vector2(5, 0), new Vector2(95, 0), s_rainbow));
            c.FillRect(new RectF(105, 5, 90, 60), new LinearGradient(new Vector2(130, 0), new Vector2(160, 0), s_rainbow) { Tile = TileMode.Repeat });
            c.FillCircle(new Vector2(50, 110), 35, new RadialGradient(new Vector2(50, 110), 18, s_rainbow) { Tile = TileMode.Mirror });
            c.FillCircle(new Vector2(150, 110), 35, new ConicalGradient(new Vector2(140, 100), 5, new Vector2(150, 110), 35, s_rainbow));
        },
        ["sweep-fallback"] = c => c.FillCircle(new Vector2(100, 75), 60, new SweepGradient(new Vector2(100, 75), s_rainbow)),
        ["patterns"] = c =>
        {
            using Raster<Rgba8> checker = Checker();
            c.FillRect(new RectF(5, 5, 90, 140), new ImagePaint(checker, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Nearest }) { LocalMatrix = Matrix3x2.CreateScale(2) });
            PictureRecorder recorder = new(new RectF(0, 0, 20, 20));
            recorder.FillCircle(new Vector2(10, 10), 7, new Rgba8(0, 0, 200, 255));
            recorder.FillRect(new RectF(0, 0, 5, 5), new Rgba8(220, 120, 0, 255));
            c.FillRoundRect(new RoundRect(new RectF(105, 5, 90, 140), 15), new PicturePaint(recorder.Finish()));
        },
        ["pictures"] = c =>
        {
            PictureRecorder recorder = new(new RectF(-20, -20, 40, 40));
            recorder.FillPath(Path.Star(Vector2.Zero, 18, 8, 5), new Rgba8(220, 120, 0, 255));
            recorder.StrokePath(Path.Star(Vector2.Zero, 18, 8, 5), new Stroke(2), new Rgba8(0, 0, 0, 255));
            Picture star = recorder.Finish();
            for (int i = 0; i < 4; i++)
            {
                c.DrawPicture(star, Matrix3x2.CreateRotation(i * 0.4f) * Matrix3x2.CreateTranslation(30 + (i * 45), 40));
            }

            c.DrawPicture(star, Matrix3x2.CreateScale(2) * Matrix3x2.CreateTranslation(100, 105), new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Opacity = 0.5f });
        },
        ["clips"] = c =>
        {
            c.Save();
            c.ClipPath(Path.Oval(new RectF(10, 10, 80, 60)));
            c.FillRect(new RectF(0, 0, 100, 80), new LinearGradient(new Vector2(0, 0), new Vector2(100, 80), s_rainbow));
            c.Restore();
            c.Save();
            c.ClipPath(Path.Oval(new RectF(120, 20, 60, 40)), ClipOp.Difference);
            c.FillRect(new RectF(110, 10, 80, 60), new Rgba8(40, 120, 40, 255));
            c.Restore();
            c.Save();
            c.Translate(100, 110);
            c.Rotate(20);
            c.ClipRect(new RectF(-60, -20, 120, 40));
            c.ClipPath(Path.Star(Vector2.Zero, 40, 18, 6));
            c.FillRect(new RectF(-100, -50, 200, 100), new Rgba8(160, 30, 160, 255));
            c.Restore();
        },
        ["deep-clips"] = c =>
        {
            for (int i = 0; i < 40; i++)
            {
                c.Save();
                c.Translate(100, 75);
                c.Rotate(i * 4.5f);
                c.ClipRect(new RectF(-90 + i, -70 + i, 180 - (2 * i), 140 - (2 * i)));
                if (i % 5 == 0)
                {
                    c.ClipPath(Path.Oval(new RectF(-30, -20, 60, 40)), ClipOp.Difference);
                }

                c.Translate(-100, -75);
                c.FillRect(new RectF(0, 0, 200, 150), new Rgba8((byte)(i * 6), 80, (byte)(250 - (i * 6)), 60));
            }

            c.RestoreToCount(1);
        },
        ["layers"] = c =>
        {
            c.SaveLayer(new LayerOptions { Opacity = 0.6f });
            c.FillCircle(new Vector2(40, 40), 30, new Rgba8(255, 0, 0, 255));
            c.FillCircle(new Vector2(70, 40), 30, new Rgba8(0, 0, 255, 255));
            c.Restore();

            c.FillRect(new RectF(110, 10, 80, 60), new Rgba8(0, 160, 220, 255));
            c.SaveLayer(new LayerOptions { BlendMode = BlendMode.Multiply });
            c.FillCircle(new Vector2(150, 40), 30, new Rgba8(255, 200, 0, 255));
            c.Restore();
            c.SaveLayer(new LayerOptions { BlendMode = BlendMode.Screen });
            c.FillRect(new RectF(160, 20, 30, 50), new Rgba8(200, 0, 100, 255));
            c.Restore();

            c.SaveLayer(new LayerOptions { Mask = new SoftMask(m => m.FillRect(new RectF(10, 90, 180, 50), new LinearGradient(new Vector2(10, 0), new Vector2(190, 0), new RgbaF32(1, 1, 1, 1), new RgbaF32(0, 0, 0, 1)))) });
            c.FillRect(new RectF(10, 90, 180, 50), new Rgba8(0, 150, 80, 255));
            c.FillCircle(new Vector2(100, 115), 20, new Rgba8(250, 250, 0, 255));
            c.Restore();
        },
        ["images"] = c =>
        {
            using Raster<Rgba8> checker = Checker();
            using Raster<Rgba8> ramp = Raster.Create(16, 16, new Rgba8(0, 0, 0, 0));
            for (int y = 0; y < 16; y++)
            {
                for (int x = 0; x < 16; x++)
                {
                    ramp.Row(y)[x] = new Rgba8((byte)(x * 16), (byte)(y * 16), 128, (byte)(128 + (x * 8)));
                }
            }

            c.DrawImage(checker, new RectF(5, 5, 64, 64), new SamplingOptions { Filter = ResampleFilter.Nearest });
            c.DrawImage(checker, new RectF(2, 2, 4, 4), new RectF(80, 5, 40, 40), new SamplingOptions { Filter = ResampleFilter.Nearest });
            c.DrawImage(ramp, new RectF(130, 5, 64, 64), new SamplingOptions { Filter = ResampleFilter.Nearest });
            c.Save();
            c.Translate(100, 80);
            c.Rotate(15);
            c.DrawImage(checker, new RectF(0, 0, 48, 48), new SamplingOptions { Filter = ResampleFilter.Nearest });
            c.Restore();
        },
        ["blur"] = c =>
        {
            c.FillCircle(new Vector2(50, 75), 30, new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Blur = new MaskBlur(4) });
            c.SaveLayer(new LayerOptions { Filter = o => o.Blur(3) });
            c.FillRect(new RectF(110, 40, 70, 70), new Rgba8(120, 0, 160, 255));
            c.Restore();
        },
        ["text"] = c =>
        {
            Typeface noto = Typeface.FromFile(SvgCanvasTests.FontPath("NotoSans-Regular.ttf"));
            c.DrawText("Office fié Øre", new Vector2(8, 40), new Font(noto, 26), new Rgba8(20, 20, 20, 255));
            c.DrawText("Gradient", new Vector2(8, 85), new Font(noto, 30) { Synthesis = FontSynthesis.Bold }, new LinearGradient(new Vector2(8, 0), new Vector2(150, 0), s_rainbow));
            c.Save();
            c.Translate(100, 130);
            c.Rotate(-8);
            c.DrawText("rotated", new Vector2(0, 0), new Font(noto, 20), new Rgba8(160, 0, 0, 255));
            c.Restore();
        },
    };

    public static TheoryData<string, SvgTextMode> Cases()
    {
        TheoryData<string, SvgTextMode> data = [];
        foreach (string name in s_scenes.Keys)
        {
            data.Add(name, SvgTextMode.Outlines);
        }

        data.Add("text", SvgTextMode.Text);
        data.Add("scene", SvgTextMode.Outlines);
        data.Add("scene", SvgTextMode.Text);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RendersCloseToTheDirectDrawing(string name, SvgTextMode mode)
    {
        (int width, int height) = name == "scene" ? (400, 300) : (Width, Height);
        Action<Canvas> draw = name == "scene" ? c => SvgCanvasTests.Scene(c, "Egami") : s_scenes[name];

        using Raster<Rgba8> direct = Raster.Create(width, height, new Rgba8(0, 0, 0, 0));
        using (RasterCanvas<Rgba8> canvas = direct.CreateCanvas())
        {
            draw(canvas);
        }

        (string text, SvgDocument document) = SvgCanvasTests.Write(draw, new SvgWriteOptions { Text = mode }, width, height, $"RoundTrip-{name}-{mode}");
        using Raster<Rgba8> read = document.Rasterize<Rgba8>();
        Assert.Equal(width, read.Width);
        Assert.Equal(height, read.Height);

        string? folder = Environment.GetEnvironmentVariable("EGAMI_SVG_OUT");
        if (!string.IsNullOrEmpty(folder))
        {
            direct.Save(System.IO.Path.Combine(folder, $"RoundTrip-{name}-{mode}.direct.png"));
            read.Save(System.IO.Path.Combine(folder, $"RoundTrip-{name}-{mode}.read.png"));
        }

        Assert.Equal(mode == SvgTextMode.Text && name is "text" or "scene", text.Contains("<text", StringComparison.Ordinal));
        (double mean, double far) = Difference(direct, read);
        output.WriteLine($"{name} {mode}: mean {mean:F3}, far {far:P3}");
        Assert.True(mean < 1.5, $"Mean difference {mean:F3} of 255.");
        Assert.True(far < 0.005, $"{far:P3} of pixels differ by more than 48 of 255.");
    }

    /// <summary>The mean channel difference of premultiplied pixels, in 0 to 255, and the share of pixels that differ by more than 48 in a channel.</summary>
    internal static (double Mean, double Far) Difference(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        double sum = 0;
        long far = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> rowA = a.Row(y);
            ReadOnlySpan<Rgba8> rowB = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                int worst = 0;
                Span<int> pa = [rowA[x].R * rowA[x].A / 255, rowA[x].G * rowA[x].A / 255, rowA[x].B * rowA[x].A / 255, rowA[x].A];
                Span<int> pb = [rowB[x].R * rowB[x].A / 255, rowB[x].G * rowB[x].A / 255, rowB[x].B * rowB[x].A / 255, rowB[x].A];
                for (int i = 0; i < 4; i++)
                {
                    int d = Math.Abs(pa[i] - pb[i]);
                    sum += d;
                    worst = Math.Max(worst, d);
                }

                if (worst > 48)
                {
                    far++;
                }
            }
        }

        double pixels = (double)a.Width * a.Height;
        return (sum / (pixels * 4), far / pixels);
    }

    private static Raster<Rgba8> Checker()
    {
        Raster<Rgba8> checker = Raster.Create(8, 8, new Rgba8(255, 255, 255, 255));
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                if ((x / 4 + y / 4) % 2 == 0)
                {
                    checker.Row(y)[x] = new Rgba8(30, 30, 30, 255);
                }
            }
        }

        return checker;
    }
}
