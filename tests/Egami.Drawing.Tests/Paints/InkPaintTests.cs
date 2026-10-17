using System.Numerics;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

/// <summary>Phase 2.6.5 task 6: ink colours in paints, drawn on RGB canvases as their appearance.</summary>
public class InkPaintTests
{
    private static readonly InkColor Red186 = InkColor.Spot("PANTONE 186 C", 1, InkAppearance.Cmyk(0, 1, 0.81f, 0.04f));

    [Fact]
    public void AnInkPaintsColorIsItsAppearanceInSrgb()
    {
        // A CMYK appearance goes through FOGRA39, as a conversion of the same CMYK to sRGB does.
        SolidPaint paint = new(InkColor.Cmyk(0, 1, 0.81f, 0.04f));
        RgbaF32[] expected = new RgbaF32[1];
        ColorTransform.Create(ColorProfile.Fogra39, ColorProfile.Srgb).Apply<Cmyk16, RgbaF32>([new Cmyk16(0, 65535, 53084, 2621)], expected);
        Assert.True(Math.Abs(paint.Color.R - expected[0].R) < 0.01f && Math.Abs(paint.Color.G - expected[0].G) < 0.01f && Math.Abs(paint.Color.B - expected[0].B) < 0.01f, $"{paint.Color} against {expected[0]}");
        Assert.Equal(1, paint.Color.A);
        Assert.Same(paint.Ink, paint.Ink);

        // An sRGB appearance is itself; a technical ink has none and draws nothing; half a tint is lighter.
        Assert.Equal(new RgbaF32(0.2f, 0.4f, 0.6f, 1), Round(new SolidPaint(InkColor.Spot("Blue", 1, InkAppearance.Rgb(0.2f, 0.4f, 0.6f))).Color));
        Assert.Equal(0, new SolidPaint(InkColor.Spot(Colorant.Technical("Varnish"), 1)).Color.A);
        Assert.True(new SolidPaint(InkColor.Spot("PANTONE 186 C", 0.5f, InkAppearance.Cmyk(0, 1, 0.81f, 0.04f))).Color.G > paint.Color.G);

        // Setting Ink sets Color; equality takes the inks into account.
        SolidPaint other = new SolidPaint(new Rgba8(1, 2, 3, 255)) with { Ink = Red186 };
        Assert.Equal(new SolidPaint(Red186), other);
        Assert.NotEqual(new SolidPaint(Red186), new SolidPaint(new SolidPaint(Red186).Color));   // the same colour without inks
        Assert.Throws<ArgumentNullException>(() => new SolidPaint((InkColor)null!));
    }

    [Fact]
    public void RgbCanvasesDrawTheAppearanceAndIgnoreOverprint()
    {
        SolidPaint ink = new(Red186) { Overprint = Overprint.On };
        SolidPaint rgb = new(ink.Color);
        Assert.Equal(Draw(c => c.FillRect(new RectF(3, 4, 20, 10), rgb)), Draw(c => c.FillRect(new RectF(3, 4, 20, 10), ink)));

        // Gradient stops, mesh vertices and patch corners with inks draw as their appearances.
        InkColor cyan = InkColor.Cmyk(1, 0, 0, 0), yellow = InkColor.Cmyk(0, 0, 1, 0);
        LinearGradient inkGradient = new(Vector2.Zero, new Vector2(40, 0), [new GradientStop(0, cyan), new GradientStop(1, Red186)]);
        LinearGradient rgbGradient = new(Vector2.Zero, new Vector2(40, 0), [new GradientStop(0, Appearance(cyan)), new GradientStop(1, Appearance(Red186))]);
        Assert.Equal(Draw(c => c.DrawPaint(rgbGradient)), Draw(c => c.DrawPaint(inkGradient)));
        Assert.Same(cyan, inkGradient.Stops[0].Ink);

        Vector2[] corners = [new(2, 2), new(38, 4), new(6, 30)];
        Mesh plain = new(MeshMode.Triangles, corners, [Appearance(cyan), Appearance(yellow), Appearance(Red186)]);
        Mesh inked = new Mesh(MeshMode.Triangles, corners).WithInks([cyan, yellow, Red186]);
        Assert.Equal(3, inked.Inks.Length);
        Assert.Equal(plain.Colors.ToArray(), inked.Colors.ToArray());
        Assert.Equal(Draw(c => c.DrawMesh(plain)), Draw(c => c.DrawMesh(inked)));

        Vector2[] net = [new(2, 2), new(14, 0), new(26, 4), new(38, 2), new(40, 12), new(38, 22), new(36, 32), new(24, 30), new(12, 34), new(2, 32), new(4, 22), new(0, 12)];
        Patch patch = Patch.Coons(net, [Appearance(cyan), Appearance(yellow), Appearance(Red186), Appearance(InkColor.None)]);
        Patch inkPatch = Patch.Coons(net).WithInks([cyan, yellow, Red186, InkColor.None]);
        Assert.Equal(Draw(c => c.DrawPatch(patch)), Draw(c => c.DrawPatch(inkPatch)));
    }

    [Fact]
    public void BadInkPaintsAreRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SolidPaint(Red186) { Overprint = (Overprint)3 });
        Mesh mesh = new(MeshMode.Triangles, [Vector2.Zero, Vector2.One, Vector2.UnitX]);
        Assert.Throws<ArgumentException>(() => mesh.WithInks([Red186]));
        Mesh valued = new(MeshMode.Triangles, [Vector2.Zero, Vector2.One, Vector2.UnitX], values: [0, 1, 0.5f]);
        Assert.Throws<ArgumentException>(() => valued.WithInks([Red186, Red186, Red186]));
        Assert.Throws<ArgumentException>(() => Patch.Coons(new Vector2[12]).WithInks([Red186]));
        Assert.Throws<ArgumentNullException>(() => new GradientStop(0, (InkColor)null!));
    }

    private static RgbaF32 Appearance(InkColor ink) => new SolidPaint(ink).Color;

    private static RgbaF32 Round(RgbaF32 c) => new(MathF.Round(c.R, 3), MathF.Round(c.G, 3), MathF.Round(c.B, 3), MathF.Round(c.A, 3));

    private static Rgba8[] Draw(Action<Drawing.Canvas> draw)
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(44, 36);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            draw(canvas);
        }

        Rgba8[] pixels = new Rgba8[44 * 36];
        raster.CopyPixelsTo(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
        return pixels;
    }
}
