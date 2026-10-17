using System.Numerics;

using Egami.Colors;
using Egami.Drawing.Tests.Canvas;
using Egami.Fonts;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Inks;

/// <summary>
/// Phase 2.6.5 task 7: the ink canvas covers exactly the pixels the RGB canvas does, plane by plane, and turns paints into tints:
/// inks by name, missing inks through their appearance, RGB through the profile.
/// </summary>
public class InkCanvasTests
{
    private static readonly InkAppearance RedLook = InkAppearance.Cmyk(0, 1, 0.81f, 0.04f);

    private static readonly InkSet Inks = InkSet.FromProfile(ColorProfile.Fogra39).WithSpot("PANTONE 186 C", RedLook).WithSpot("Gold", null);

    public static TheoryData<string> Shapes() => [.. Drawings.Keys];

    private static readonly Dictionary<string, Action<Drawing.Canvas, Paint>> Drawings = new()
    {
        ["rect"] = (c, p) => c.FillRect(new RectF(3.3f, 4.7f, 40.2f, 21.6f), p),
        ["circle"] = (c, p) => c.FillCircle(new Vector2(30, 25), 17.4f, p),
        ["curves"] = (c, p) => c.FillPath(Path.ParseSvg("M5 40 C 20 -10, 50 60, 60 10 Q 40 50 5 40 Z"), p),
        ["stroke"] = (c, p) => c.StrokePath(Path.ParseSvg("M4 6 L 58 44 L 10 46"), new Stroke(3.5f) { Join = LineJoin.Round }, p),
        ["thin"] = (c, p) => c.StrokePath(Path.ParseSvg("M2 2 L 60 30"), new Stroke(0.4f), p),
        ["rotated"] = (c, p) =>
        {
            c.Rotate(17, new Vector2(32, 25));
            c.FillRoundRect(new RoundRect(new RectF(12, 10, 40, 28), 6), p);
        },
        ["clipped"] = (c, p) =>
        {
            c.ClipPath(Path.Circle(new Vector2(30, 24), 19));
            c.ClipRect(new RectF(4, 4, 50, 30), ClipOp.Difference);
            c.FillRect(new RectF(0, 0, 64, 50), p);
        },
        ["aliased"] = (c, p) => c.FillCircle(new Vector2(30, 25), 13.3f, p with { Antialias = false }),
        ["text"] = (c, p) => c.DrawText("Ink 186", new Vector2(2, 34), new Font(TextTests.Noto.Value, 22), p),
    };

    [Theory]
    [MemberData(nameof(Shapes))]
    public void FillsAndStrokesCoverWhatTheRgbCanvasCovers(string shape)
    {
        foreach (InkDepth depth in Enum.GetValues<InkDepth>())
        {
            using Raster<Gray16> coverage = Raster.Create<Gray16>(64, 50);
            using (RasterCanvas<Gray16> canvas = coverage.CreateCanvas())
            {
                Drawings[shape](canvas, new SolidPaint(new RgbaF32(1, 1, 1, 1)));
            }

            using InkRaster plates = new(Inks, 64, 50, depth);
            using (InkCanvas canvas = new(plates))
            {
                Drawings[shape](canvas, new SolidPaint(InkColor.Spot(Inks[5], 1)));
            }

            int covered = 0;
            for (int y = 0; y < 50; y++)
            {
                for (int x = 0; x < 64; x++)
                {
                    int expected = coverage.GetPixel(x, y).L;
                    covered += expected > 0 ? 1 : 0;
                    Assert.Equal(expected, Value(plates.Plate("Gold")!, x, y));
                    Assert.Equal(expected, Value(plates.Alpha, x, y));
                    Assert.Equal(0, Value(plates.Plate("Cyan")!, x, y));
                    Assert.Equal(0, Value(plates.Plate("PANTONE 186 C")!, x, y));
                }
            }

            Assert.True(covered > 20, $"{shape}: {covered} pixels covered");
        }
    }

    [Fact]
    public void TintsAndOpacityBlendSourceOverOnEveryPlate()
    {
        using InkRaster plates = new(Inks, 8, 4);
        using (InkCanvas canvas = new(plates))
        {
            canvas.Clear(InkColor.None);
            canvas.FillRect(new RectF(0, 0, 8, 4), new SolidPaint(InkColor.Cmyk(0.2f, 0.4f, 0, 0)));
            canvas.FillRect(new RectF(4, 0, 4, 4), new SolidPaint(InkColor.Spot(Inks[4], 0.8f)) { Opacity = 0.5f });
        }

        // Paper is alpha 255 and no ink; the CMYK fill sets its plates; the spot at half opacity halves what was there.
        Assert.Equal(255, plates.Alpha.Row8(0)[0]);
        Assert.Equal((51, 102, 0), (plates.Plate("Cyan")!.Row8(1)[1], plates.Plate("Magenta")!.Row8(1)[1], plates.Plate("PANTONE 186 C")!.Row8(1)[1]));
        Assert.Equal(26, plates.Plate("Cyan")!.Row8(1)[5]);                  // 0.2 · 0.5
        Assert.Equal(51, plates.Plate("Magenta")!.Row8(1)[5]);               // 0.4 · 0.5
        Assert.Equal(102, plates.Plate("PANTONE 186 C")!.Row8(1)[5]);        // 0.8 · 0.5
        Assert.Equal(255, plates.Alpha.Row8(1)[5]);
    }

    [Fact]
    public void RgbColoursConvertThroughTheProfile()
    {
        Rgba8 red = new(220, 40, 30, 255);
        float[] expected = new float[4];
        ColorTransform.Create(ColorProfile.Srgb, ColorProfile.Fogra39, new ColorTransformOptions { Intent = RenderingIntent.RelativeColorimetric, BlackPointCompensation = true })
            .Apply([220 / 255f, 40 / 255f, 30 / 255f], expected);
        using InkRaster plates = new(Inks, 6, 6);
        using (InkCanvas canvas = new(plates))
        {
            canvas.FillRect(new RectF(0, 0, 6, 3), red);
            canvas.FillRect(new RectF(0, 3, 6, 3), new LinearGradient(Vector2.Zero, new Vector2(6, 0), RgbaF32.FromRgba8(red), RgbaF32.FromRgba8(red)));
        }

        string[] names = ["Cyan", "Magenta", "Yellow", "Black"];
        for (int c = 0; c < 4; c++)
        {
            foreach (int y in (int[])[1, 4])
            {
                Assert.True(Math.Abs(plates.Plate(names[c])!.Row8(y)[2] - (expected[c] * 255)) <= 1, $"{names[c]} at row {y}: {plates.Plate(names[c])!.Row8(y)[2]} against {expected[c] * 255:F1}");
            }
        }

        Assert.Equal(0, plates.Plate("PANTONE 186 C")!.Row8(1)[2]);
    }

    [Fact]
    public void InksTheRasterLacksPrintThroughTheirAppearance()
    {
        InkColor blue = InkColor.Spot("PANTONE 300 C", 1, InkAppearance.Cmyk(1, 0.44f, 0, 0));
        using InkRaster plates = new(Inks.WithDisabled("PANTONE 186 C"), 4, 4);
        using (InkCanvas canvas = new(plates))
        {
            canvas.FillRect(new RectF(0, 0, 4, 2), new SolidPaint(blue));

            // A disabled spot is printed through its appearance too, over the black it is mixed with.
            canvas.FillRect(new RectF(0, 2, 4, 2), new SolidPaint(InkColor.Mix(new InkTint(Colorant.Black, 0.5f), new InkTint(Inks[4], 1))));
        }

        Assert.Equal((255, 112, 0, 0), (plates.Plate("Cyan")!.Row8(0)[0], plates.Plate("Magenta")!.Row8(0)[0], plates.Plate("Yellow")!.Row8(0)[0], plates.Plate("Black")!.Row8(0)[0]));
        Assert.Null(plates.Plate("PANTONE 186 C"));
        Assert.Equal((0, 255, 207), (plates.Plate("Cyan")!.Row8(3)[0], plates.Plate("Magenta")!.Row8(3)[0], plates.Plate("Yellow")!.Row8(3)[0]));
        Assert.Equal(InkTarget8(1 - (0.5f * 0.96f)), plates.Plate("Black")!.Row8(3)[0]);
    }

    [Fact]
    public void LayersAndImagesDrawThroughTheInks()
    {
        using InkRaster direct = new(Inks, 10, 10), layered = new(Inks, 10, 10);
        using (InkCanvas canvas = new(direct))
        {
            canvas.FillCircle(new Vector2(5, 5), 4, new SolidPaint(InkColor.Spot(Inks[4], 1)) { Opacity = 0.6f });
        }

        using (InkCanvas canvas = new(layered))
        {
            canvas.SaveLayer(new LayerOptions { Opacity = 0.6f });
            canvas.FillCircle(new Vector2(5, 5), 4, new SolidPaint(InkColor.Spot(Inks[4], 1)));
            canvas.Restore();
        }

        for (int y = 0; y < 10; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                Assert.True(Math.Abs(direct.Plate("PANTONE 186 C")!.Row8(y)[x] - layered.Plate("PANTONE 186 C")!.Row8(y)[x]) <= 1, $"({x}, {y})");
            }
        }

        // An RGB image converts like an RGB fill of its colour.
        using Raster<Rgba8> image = Raster.Create(4, 4, new Rgba8(220, 40, 30, 255));
        using InkRaster fill = new(Inks, 4, 4), drawn = new(Inks, 4, 4);
        using (InkCanvas canvas = new(fill))
        {
            canvas.FillRect(new RectF(0, 0, 4, 4), new Rgba8(220, 40, 30, 255));
        }

        using (InkCanvas canvas = new(drawn))
        {
            canvas.DrawImage(image, Vector2.Zero);
        }

        Assert.Equal(fill.Plate("Magenta")!.Row8(2)[2], drawn.Plate("Magenta")!.Row8(2)[2]);
    }

    [Fact]
    public void WhatIsNotYetBuiltSaysSo()
    {
        using InkRaster plates = new(Inks, 4, 4);
        using InkCanvas canvas = new(plates);
        Assert.Throws<NotSupportedException>(() => canvas.FillRect(new RectF(0, 0, 4, 4), new SolidPaint(InkColor.Cmyk(1, 0, 0, 0)) { BlendMode = Operations.BlendMode.Multiply }));
        Assert.Throws<ArgumentException>(() => new InkCanvasOptions { RgbSource = ColorProfile.Fogra39 });
    }

    private static byte InkTarget8(float v) => (byte)((v * 255) + 0.5f);

    private static int Value(InkPlate plate, int x, int y) => plate.Depth == InkDepth.Unorm8 ? plate.Row8(y)[x] * 257 : plate.Row16(y)[x];
}
