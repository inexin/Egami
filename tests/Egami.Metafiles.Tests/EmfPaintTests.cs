using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Metafiles.Reading;
using Egami.Operations;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

using Path = Egami.Drawing.Path;

namespace Egami.Metafiles.Tests;

/// <summary>EMF writing, stage B: paints as EMF+ brushes, GDI gradient fills, and the folds that keep drawings vector.</summary>
public class EmfPaintTests(ITestOutputHelper output)
{
    private static readonly RgbaF32 Red = new(0.9f, 0.1f, 0.1f, 1), Blue = new(0.1f, 0.2f, 0.9f, 1), Yellow = new(1, 0.85f, 0.1f, 1);

    private static readonly GradientStop[] Three = [new(0, Red), new(0.4f, Yellow), new(1, Blue)];

    private static readonly Raster<Rgba8> Checker = EmfScenes.Checker;

    internal static Dictionary<string, Action<Canvas>> Exact => EmfScenes.Paints;

    public static TheoryData<string> ExactNames => [.. Exact.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(ExactNames))]
    public void PaintsRoundTripAsVectors(string scene)
    {
        Action<Canvas> draw = Exact[scene];
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(draw);
        Metafile m = Metafile.Load(file);
        Assert.Empty(m.Warnings);
        Assert.DoesNotContain(m.Content.PlusRecords, r => r.Type == 0x401A); // no fallback image in EMF+
        Assert.Empty(warnings);

        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), plus = EmfCanvasTests.Play(file);
        (double mean, int max, int over) = EmfCanvasTests.Difference(direct, plus);
        output.WriteLine($"{scene} EMF+: mean {mean:F3}, max {max}, over {over}");
        Assert.True(mean < 1, $"{scene} EMF+: mean {mean:F3}, max {max}, over {over}");

        // The GDI part, one pixel to one pixel: played at the frame's size, a pixel less than the canvas each way. GDI has no alpha,
        // so a translucent solid color (a folded layer's) draws opaque there.
        if (scene.StartsWith("layer-", StringComparison.Ordinal))
        {
            return;
        }

        using Raster<Rgba8> gdi = EmfCanvasTests.Play(file, MetafilePlayback.Gdi, EmfCanvasTests.W - 1, EmfCanvasTests.H - 1);
        using Raster<Rgba8> cropped = Crop(direct, EmfCanvasTests.W - 1, EmfCanvasTests.H - 1);
        (double gmean, int gmax, int gover) = EmfCanvasTests.Difference(cropped, gdi);
        output.WriteLine($"{scene} GDI: mean {gmean:F3}, max {gmax}, over {gover}");
        // Linear gradients and solid colors are vectors in GDI too; the rest are its images, which the player resamples.
        bool image = scene == "linear-alpha" || scene.StartsWith("radial-", StringComparison.Ordinal) || scene.StartsWith("image-", StringComparison.Ordinal) || scene.StartsWith("picture-", StringComparison.Ordinal);
        Assert.Equal(image, m.Content.Records.Any(r => r.Type == 114)); // EMR_ALPHABLEND
        Assert.Equal(scene.StartsWith("linear-", StringComparison.Ordinal) && scene != "linear-alpha", m.Content.Records.Any(r => r.Type == 118)); // EMR_GRADIENTFILL
        Assert.True(gmean < (image ? 12 : 1), $"{scene} GDI: mean {gmean:F3}, max {gmax}, over {gover}");
    }

    [Fact]
    public void OverlappingShapesInALayerFallBack()
    {
        Action<Canvas> draw = c =>
        {
            c.FillRect(new RectF(0, 0, 160, 120), new Rgba8(255, 255, 255, 255));
            c.SaveLayer(0.5f);
            c.FillOval(new RectF(20, 20, 80, 80), new Rgba8(200, 0, 0, 255));
            c.FillOval(new RectF(60, 20, 80, 80), new Rgba8(0, 0, 200, 255));
            c.Restore();
        };
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(draw);
        EgamiDiagnostic warning = Assert.Single(warnings);
        Assert.Equal("Metafile.FallbackImage", warning.Id);
        Assert.Contains("layer with opacity", warning.Message, StringComparison.Ordinal);
        MetafileContent content = Metafile.Load(file).Content;
        Assert.Single(content.PlusRecords, r => r.Type == 0x401A);
        Assert.DoesNotContain(content.PlusRecords, r => r.Type == 0x400E); // the ovals are only in the image

        // Folded, the overlap would show where the two halves meet: a darker purple than the layer's.
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), played = EmfCanvasTests.Play(file);
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < 1);
    }

    [Fact]
    public void FoldedLayersWriteTheOpacityIntoEachShape()
    {
        (byte[] file, _) = Write(Exact["layer-apart"]);
        MetafileContent content = Metafile.Load(file).Content;
        Assert.Equal([0x66C80000u, 0x66007800u], content.PlusRecords.Where(r => r.Type == 0x400A).Skip(1).Select(r => U32(r.Data.Span, 0)));

        // The oval's gradient carries the opacity in its preset colors' alpha.
        PlusRecord brush = content.PlusRecords.Single(r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 1);
        byte[] b = brush.Data.ToArray();
        int n = I32(b, 72);
        Assert.All(Enumerable.Range(0, n), i => Assert.Equal(0x66u, U32(b, 76 + (n * 4) + (i * 4)) >> 24));
    }

    [Fact]
    public void ALayerInsideAFoldedLayerFallsBack()
    {
        (_, IReadOnlyList<EgamiDiagnostic> warnings) = Write(c =>
        {
            c.SaveLayer(0.5f);
            c.SaveLayer(0.5f);
            c.FillRect(new RectF(10, 10, 50, 50), new Rgba8(200, 0, 0, 255));
            c.Restore();
            c.Restore();
        });
        Assert.Single(warnings);
    }

    [Theory]
    [InlineData("sweep", "a sweep gradient")]
    [InlineData("conical", "a two-point conical gradient")]
    [InlineData("clamped-image", "an image paint whose edges are clamped or mixed")]
    [InlineData("color-matrix-gradient", "a ColorMatrixPaint")]
    [InlineData("oklab", "a gradient mixed in Oklab")]
    [InlineData("decal-gradient", "a gradient that is transparent past its ends")]
    [InlineData("gradient-hairline", "a hairline with a LinearGradient")]
    public void PaintsWithoutAnExactFormFallBack(string name, string reason)
    {
        Paint paint = name switch
        {
            "sweep" => new SweepGradient(new Vector2(80, 60), Three),
            "conical" => new ConicalGradient(new Vector2(60, 50), 5, new Vector2(80, 60), 50, Three),
            "clamped-image" => new ImagePaint(Checker) { LocalMatrix = Matrix3x2.CreateScale(10) },
            "color-matrix-gradient" => new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), Red, Blue).WithColorMatrix(ColorMatrix.Invert()),
            "oklab" => new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), Red, Blue) { Interpolation = GradientInterpolation.Oklab },
            _ => new LinearGradient(new Vector2(10, 0), new Vector2(150, 0), Red, Blue) { Tile = TileMode.Decal },
        };
        Action<Canvas> draw = name == "gradient-hairline"
            ? c => c.DrawLine(new Vector2(10, 10), new Vector2(150, 100), new Stroke(0), paint)
            : c => c.FillOval(new RectF(10, 10, 140, 100), paint);
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(draw);
        EgamiDiagnostic warning = Assert.Single(warnings);
        Assert.Contains(reason, warning.Message, StringComparison.Ordinal);
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), played = EmfCanvasTests.Play(file);
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < 1.5);
    }

    [Fact]
    public void LinearGradientBrushHasItsLayout()
    {
        LinearGradient gradient = new(new Vector2(40, 10), new Vector2(120, 10), Three);
        (byte[] file, _) = Write(c =>
        {
            c.FillRect(new RectF(10, 10, 140, 50), gradient);
            c.FillRect(new RectF(10, 70, 140, 40), gradient);
        });
        MetafileContent content = Metafile.Load(file).Content;

        // EmfPlusLinearGradientBrushData ([MS-EMFPLUS] 2.2.2.24): transform and preset colors, WrapModeTile, a rectangle along the
        // line's own frame wide enough to cover the shapes (here -1 to 2 lengths of the line), start and end colors, the
        // transform onto the line, then EmfPlusBlendColors (2.2.2.4).
        PlusRecord brush = Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 1);
        byte[] b = brush.Data.ToArray();
        Assert.Equal((0xDBC01002u, 4, 0x06, 0), (U32(b, 0), I32(b, 4), I32(b, 8), I32(b, 12)));
        Assert.Equal((-1f, 0f, 3f, 1f), (F32(b, 16), F32(b, 20), F32(b, 24), F32(b, 28)));
        Assert.Equal((0xFFE61A1Au, 0xFF1A33E6u), (U32(b, 32), U32(b, 36)));
        Assert.Equal((80f, 0f, 0f, 80f, 40f, 10f), (F32(b, 48), F32(b, 52), F32(b, 56), F32(b, 60), F32(b, 64), F32(b, 68)));
        int n = I32(b, 72);
        float[] positions = [.. Enumerable.Range(0, n).Select(i => F32(b, 76 + (i * 4)))];
        Assert.Equal([0, 1f / 3, (1 + 0.4f) / 3, 2f / 3, 1], positions, (x, y) => MathF.Abs(x - y) < 1e-6f);
        Assert.Equal(76 + (n * 8), b.Length);

        // Two fills by brush index: FillPath with the brush object in BrushId.
        Assert.Equal(2, content.PlusRecords.Count(r => r.Type == 0x4014 && (r.Flags & 0x8000) == 0 && U32(r.Data.Span, 0) == (uint)(brush.Flags & 0xFF)));

        // EMR_GRADIENTFILL ([MS-EMF] 2.3.5.12) clipped to each shape: GRADIENT_FILL_TRIANGLE, TriVertex colors in 16 bits.
        ReadOnlySpan<byte> d = file;
        MetaRecord fill = content.Records.First(r => r.Type == 118);
        int o = fill.Offset;
        int vertices = I32(d, o + 24), triangles = I32(d, o + 28);
        Assert.Equal((2u, 16, 8), (U32(d, o + 32), vertices, triangles)); // four strips: before the line, two stops' spans, after it
        Assert.Equal((0xE600, 0x1A00, 0x1A00, 0xFF00), (U16(d, o + 44), U16(d, o + 46), U16(d, o + 48), U16(d, o + 50)));
        Assert.Equal(36 + (vertices * 16) + (triangles * 12), fill.Size);
        Assert.Equal([33, 59], content.Records.Take(content.Records.IndexOf(fill)).Where(r => r.Type is 33 or 59 or 67 or 34).Select(r => r.Type).Take(2));
        Assert.Equal(34, content.Records[content.Records.IndexOf(fill) + 1].Type); // EMR_RESTOREDC
    }

    [Fact]
    public void PathGradientBrushHasItsLayout()
    {
        (byte[] file, _) = Write(c => c.FillRect(new RectF(5, 5, 150, 110), new RadialGradient(new Vector2(70, 55), 40, Three)));
        PlusRecord brush = Assert.Single(Metafile.Load(file).Content.PlusRecords, r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 1);
        byte[] b = brush.Data.ToArray();

        // EmfPlusPathGradientBrushData ([MS-EMFPLUS] 2.2.2.29): boundary path, transform and preset colors; the center color and
        // point, one surrounding color, a circle three times the radius (the farthest corner is 2.2 radii away), then the blend.
        Assert.Equal((0xDBC01002u, 3, 0x07, 0), (U32(b, 0), I32(b, 4), I32(b, 8), I32(b, 12)));
        Assert.Equal((0xFFE61A1Au, 70f, 55f, 1, 0xFF1A33E6u), (U32(b, 16), F32(b, 20), F32(b, 24), I32(b, 28), U32(b, 32)));
        int size = I32(b, 36);
        int at = 40 + size + 24;
        int n = I32(b, at);
        float[] positions = [.. Enumerable.Range(0, n).Select(i => F32(b, at + 4 + (i * 4)))];
        Assert.Equal([0, 2f / 3, 1 - (0.4f / 3), 1], positions, (x, y) => MathF.Abs(x - y) < 1e-6f);
        Assert.Equal(at + 4 + (n * 8), b.Length);
    }

    [Fact]
    public void TextureBrushHasItsLayout()
    {
        (byte[] file, _) = Write(Exact["image-repeat"]);
        MetafileContent content = Metafile.Load(file).Content;
        PlusRecord brush = Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 1);
        byte[] b = brush.Data.ToArray();

        // EmfPlusTextureBrushData ([MS-EMFPLUS] 2.2.2.45): a transform, WrapModeTile, then the image: a 4 by 4 PNG bitmap.
        Assert.Equal((0xDBC01002u, 2, 0x02, 0), (U32(b, 0), I32(b, 4), I32(b, 8), I32(b, 12)));
        Assert.Equal((4f, 0f, 0f, 4f), (F32(b, 16), F32(b, 20), F32(b, 24), F32(b, 28)));
        Assert.Equal((0xDBC01002u, 1, 4, 4, 0x0026200Au, 1), (U32(b, 40), I32(b, 44), I32(b, 48), I32(b, 52), U32(b, 60), I32(b, 64)));
        Assert.Equal(0x89, b[68]);

        // Nearest-neighbor sampling: EmfPlusSetInterpolationMode 5.
        Assert.Contains(content.PlusRecords, r => r.Type == 0x4021 && r.Flags == 5);
    }

    [Fact]
    public void GdiOnlyFilesUseGradientFillsAndImages()
    {
        (byte[] linear, IReadOnlyList<EgamiDiagnostic> none) = Write(Exact["linear-repeat"], new EmfWriteOptions { Kind = MetafileKind.Emf });
        Assert.Empty(none);
        Assert.Contains(Metafile.Load(linear).Content.Records, r => r.Type == 118);

        // A radial gradient is an image in GDI: in a GDI-only file that is all a reader gets, so it warns.
        (byte[] radial, IReadOnlyList<EgamiDiagnostic> warnings) = Write(Exact["radial-clamp"], new EmfWriteOptions { Kind = MetafileKind.Emf });
        Assert.Single(warnings);
        Assert.Contains(Metafile.Load(radial).Content.Records, r => r.Type == 114);
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(Exact["radial-clamp"]), played = EmfCanvasTests.Play(radial, MetafilePlayback.Auto, EmfCanvasTests.W - 1, EmfCanvasTests.H - 1);
        using Raster<Rgba8> cropped = Crop(direct, EmfCanvasTests.W - 1, EmfCanvasTests.H - 1);
        Assert.True(EmfCanvasTests.Difference(cropped, played).Mean < 12);
    }

    [Fact]
    public void SolidPaintsMadeOfOthersBecomeTheirColor()
    {
        (byte[] file, _) = Write(Exact["blend-paint"]);
        PlusRecord fill = Assert.Single(Metafile.Load(file).Content.PlusRecords, r => r.Type == 0x400A);
        using Raster<RgbaF32> pixel = Raster.Create<RgbaF32>(1, 1);
        using (RasterCanvas<RgbaF32> c = pixel.CreateCanvas())
        {
            c.FillRect(new RectF(0, 0, 1, 1), new BlendPaint(new SolidPaint(new Rgba8(40, 160, 200, 255)), new SolidPaint(new Rgba8(250, 120, 30, 200)), BlendMode.Multiply));
        }

        Rgba8 expected = pixel.GetPixel(0, 0).ToRgba8();
        Assert.Equal(((uint)expected.A << 24) | ((uint)expected.R << 16) | ((uint)expected.G << 8) | expected.B, U32(fill.Data.Span, 0));
    }

    private static (byte[] File, IReadOnlyList<EgamiDiagnostic> Warnings) Write(Action<Canvas> draw, EmfWriteOptions? options = null)
    {
        using MemoryStream ms = new();
        EmfCanvas canvas = new(ms, EmfCanvasTests.W, EmfCanvasTests.H, options);
        using (canvas)
        {
            draw(canvas);
        }

        return (ms.ToArray(), canvas.Warnings);
    }

    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);

    private static Raster<Rgba8> Crop(Raster<Rgba8> r, int w, int h)
    {
        Raster<Rgba8> c = Raster.Create<Rgba8>(w, h);
        for (int y = 0; y < h; y++)
        {
            r.Row(y)[..w].CopyTo(c.Row(y));
        }

        return c;
    }
}
