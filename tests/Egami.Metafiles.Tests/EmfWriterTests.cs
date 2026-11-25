using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Metafiles.Reading;
using Egami.Metafiles.Writing;
using Egami.Pixels;

using static Egami.Metafiles.Reading.MetafileReader;

using Path = Egami.Drawing.Path;

namespace Egami.Metafiles.Tests;

/// <summary>EMF writing, stage A: record layouts against [MS-EMF] and [MS-EMFPLUS], the three kinds, EMZ, options and fallbacks.</summary>
public class EmfWriterTests
{
    private static readonly Rgba8 Teal = new(0, 128, 128, 255);

    [Fact]
    public void TheHeaderDescribesTheFile()
    {
        byte[] file = EmfCanvasTests.Write(c => c.FillRect(new RectF(10.5f, 12, 80, 50), Teal), new EmfWriteOptions { Description = "chart" });
        Metafile m = Metafile.Load(file);
        MetafileContent content = m.Content;
        ReadOnlySpan<byte> d = file;

        // EMR_HEADER ([MS-EMF] 2.3.4.2): the base header, extensions 1 and 2, then the description.
        Assert.Equal(1u, U32(d, 0));
        Assert.Equal(108 + 28, I32(d, 4)); // "Egami\0chart\0\0" is 13 UTF-16 characters, 26 bytes padded to 28
        Assert.Equal(EmfSignature, U32(d, 40));
        Assert.Equal(0x00010000u, U32(d, 44));
        Assert.Equal(file.Length, I32(d, 48));
        Assert.Equal(content.Records.Count, I32(d, 52));
        Assert.Equal(13, I32(d, 60));
        Assert.Equal(108, I32(d, 64));
        Assert.Equal((960, 960), (I32(d, 72), I32(d, 76)));
        Assert.Equal((254, 254), (I32(d, 80), I32(d, 84)));
        Assert.Equal((0, 0, 0), (I32(d, 88), I32(d, 92), I32(d, 96)));
        Assert.Equal((254000, 254000), (I32(d, 100), I32(d, 104)));
        Assert.Equal("Egami\nchart", m.Description);

        // The frame is inclusive of the last pixel, as GDI+ writes it: 159 by 119 pixels at 96 DPI in 0.01 mm.
        Assert.Equal((0, 0, 4207, 3149), content.Frame);
        (int l, int t, int r, int b) = content.Bounds;
        Assert.True(l <= 10 && t <= 12 && r >= 90 && b >= 61 && l >= 0 && r < EmfCanvasTests.W, $"bounds {content.Bounds}");
        Assert.Equal(new Vector2(96), m.Dpi);
        Assert.Equal(MetafileKind.EmfPlusDual, m.Kind);
        Assert.Empty(m.Warnings);

        // EMR_EOF ([MS-EMF] 2.3.4.1): no palette, the offset of where one would be, and its own size last.
        MetaRecord eof = content.Records[^1];
        Assert.Equal((EmrEof, 20), (eof.Type, eof.Size));
        Assert.Equal((0, 16, 20), (I32(d, eof.Offset + 8), I32(d, eof.Offset + 12), I32(d, eof.Offset + 16)));
    }

    [Fact]
    public void TheEmfPlusHeaderComesFirstAloneAndTheEndOfFileLast()
    {
        byte[] file = EmfCanvasTests.Write(c => c.FillRect(new RectF(10, 10, 20, 20), Teal));
        MetafileContent content = Metafile.Load(file).Content;
        ReadOnlySpan<byte> d = file;

        // EMR_COMMENT_EMFPLUS right after the header, holding only EmfPlusHeader ([MS-EMFPLUS] 2.3.3.3): dual flag, version,
        // video display, 96 DPI.
        MetaRecord comment = content.Records[1];
        Assert.Equal((EmrComment, 16 + 28), (comment.Type, comment.Size));
        Assert.Equal((4 + 28, EmfPlusIdentifier), (I32(d, comment.Offset + 8), U32(d, comment.Offset + 12)));
        int at = comment.Offset + 16;
        Assert.Equal((0x4001, 1, 28, 16), (U16(d, at), U16(d, at + 2), I32(d, at + 4), I32(d, at + 8)));
        Assert.Equal((0xDBC01002u, 1, 96, 96), (U32(d, at + 12), I32(d, at + 16), I32(d, at + 20), I32(d, at + 24)));

        Assert.Equal(0x4001, content.PlusRecords[0].Type);
        Assert.Equal(0x4002, content.PlusRecords[^1].Type);
        Assert.Equal(0, content.PlusRecords[^1].Data.Length);
        Assert.Equal(content.Records.Count - 2, content.PlusRecords[^1].EmfIndex);
    }

    [Fact]
    public void ShapesWriteTheirOwnRecords()
    {
        byte[] file = EmfCanvasTests.Write(c =>
        {
            c.FillRect(new RectF(10, 12, 80, 50), Teal);
            c.FillOval(new RectF(1, 2, 3, 4), new SolidPaint(Teal) { Opacity = 0.5f });
        });
        MetafileContent content = Metafile.Load(file).Content;

        // EmfPlusFillRects ([MS-EMFPLUS] 2.3.4.16): S flag, ARGB color, count, EmfPlusRectF.
        PlusRecord rects = content.PlusRecords.Single(r => r.Type == 0x400A);
        Assert.Equal(0x8000, rects.Flags);
        ReadOnlySpan<byte> r = rects.Data.Span;
        Assert.Equal(24, r.Length);
        Assert.Equal((0xFF008080u, 1), (U32(r, 0), I32(r, 4)));
        Assert.Equal((10f, 12f, 80f, 50f), (F32(r, 8), F32(r, 12), F32(r, 16), F32(r, 20)));

        // EmfPlusFillEllipse (2.3.4.13): the color's alpha carries the opacity.
        PlusRecord ellipse = content.PlusRecords.Single(r => r.Type == 0x400E);
        Assert.Equal((0x8000, 0x80008080u), (ellipse.Flags, U32(ellipse.Data.Span, 0)));

        // The GDI part: EMR_CREATEBRUSHINDIRECT (BS_SOLID, COLORREF 0x00BBGGRR), the path bracket and EMR_FILLPATH.
        ReadOnlySpan<byte> d = file;
        MetaRecord brush = content.Records.First(x => x.Type == 39);
        Assert.Equal(24, brush.Size);
        Assert.Equal((0u, 0x00808000u, 0u), (U32(d, brush.Offset + 12), U32(d, brush.Offset + 16), U32(d, brush.Offset + 20)));
        int[] types = [.. content.Records.Select(x => x.Type)];
        int begin = Array.IndexOf(types, 59);
        Assert.Equal([59, 27, 89, 61, 60], types[begin..(begin + 5)]); // BEGINPATH, MOVETOEX, POLYLINETO16, CLOSEFIGURE, ENDPATH
        Assert.Contains(62, types); // EMR_FILLPATH
        Assert.Equal(24, content.Records.First(x => x.Type == 62).Size);

        // The oval's Beziers: EMR_POLYBEZIERTO16 ([MS-EMF] 2.3.5.20), bounds, count and 16-bit points in sixteenths.
        MetaRecord bezier = content.Records.First(x => x.Type == 88);
        int n = I32(d, bezier.Offset + 24);
        Assert.Equal(0, n % 3);
        Assert.Equal(28 + (n * 4), bezier.Size);

        // The world transform scales the sixteenths back: EMR_SETWORLDTRANSFORM with 1/16.
        MetaRecord world = content.Records.First(x => x.Type == 35);
        Assert.Equal((0.0625f, 0f, 0f, 0.0625f), (F32(d, world.Offset + 8), F32(d, world.Offset + 12), F32(d, world.Offset + 16), F32(d, world.Offset + 20)));
    }

    [Fact]
    public void PensCarryEveryStrokeSetting()
    {
        Stroke stroke = new(4) { Cap = LineCap.Round, Join = LineJoin.Bevel, MiterLimit = 6, Dash = new DashPattern([10, 6], 3) };
        byte[] file = EmfCanvasTests.Write(c => c.DrawLine(new Vector2(10, 10), new Vector2(150, 10), stroke, Teal));
        MetafileContent content = Metafile.Load(file).Content;

        // EmfPlusPen ([MS-EMFPLUS] 2.2.1.7) and EmfPlusPenData (2.2.2.33): flags for start and end cap, join, miter limit, line
        // style, dashed line cap, offset and dashes; world unit; then a solid brush.
        PlusRecord pen = content.PlusRecords.Single(r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 2);
        ReadOnlySpan<byte> p = pen.Data.Span;
        Assert.Equal((0xDBC01002u, 0, 0x1FE, 0), (U32(p, 0), I32(p, 4), I32(p, 8), I32(p, 12)));
        Assert.Equal(4f, F32(p, 16));
        Assert.Equal((2, 2, 1, 6f), (I32(p, 20), I32(p, 24), I32(p, 28), F32(p, 32)));
        Assert.Equal((5, 2, 3f / 4, 2), (I32(p, 36), I32(p, 40), F32(p, 44), I32(p, 48)));

        // A round dash cap lies inside GDI+'s dash: 10 + 4 long, the gap 6 - 4, in widths.
        Assert.Equal((14f / 4, 2f / 4), (F32(p, 52), F32(p, 56)));
        Assert.Equal((0xDBC01002u, 0, 0xFF008080u), (U32(p, 60), I32(p, 64), U32(p, 68)));
        Assert.Equal(72, p.Length);

        // EMR_EXTCREATEPEN ([MS-EMF] 2.3.7.9): no bitmap, PS_GEOMETRIC | PS_ENDCAP_ROUND | PS_JOIN_BEVEL | PS_USERSTYLE, the width and
        // dash entries in sixteenths, the phase rotated into the pattern.
        ReadOnlySpan<byte> d = file;
        MetaRecord gdi = content.Records.Single(x => x.Type == 95);
        int o = gdi.Offset;
        Assert.Equal((0, 0, 0, 0), (I32(d, o + 12), I32(d, o + 16), I32(d, o + 20), I32(d, o + 24)));
        Assert.Equal((0x00011007u, 64), (U32(d, o + 28), I32(d, o + 32)));
        Assert.Equal((0u, 0x00808000u, 0u), (U32(d, o + 36), U32(d, o + 40), U32(d, o + 44)));
        Assert.Equal(4, I32(d, o + 48));
        Assert.Equal([112u, 96u, 48u, 0u], [U32(d, o + 52), U32(d, o + 56), U32(d, o + 60), U32(d, o + 64)]);
        Assert.Equal(68, gdi.Size);
        Assert.Contains(content.Records, x => x.Type == 64); // EMR_STROKEPATH
    }

    [Theory]
    [InlineData(new float[] { 10, 6 }, 0, new float[] { 10, 6 })]
    [InlineData(new float[] { 10, 6 }, 3, new float[] { 7, 6, 3, 0 })]
    [InlineData(new float[] { 10, 6 }, 12, new float[] { 0, 4, 10, 2 })]
    [InlineData(new float[] { 10, 6 }, -3, new float[] { 0, 3, 10, 3 })]
    [InlineData(new float[] { 4, 2, 1, 2 }, 5, new float[] { 0, 1, 1, 2, 4, 1 })]
    public void DashPhasesRotateIntoTheGdiPattern(float[] dashes, float phase, float[] expected)
        => Assert.Equal(expected, EmfCanvas.Rotate(dashes, phase));

    [Fact]
    public void ClipsAndStatesWriteTheirRecords()
    {
        byte[] file = EmfCanvasTests.Write(c =>
        {
            c.Save();
            c.ClipRect(new RectF(10, 10, 50, 50));
            c.ClipPath(Path.Oval(new RectF(20, 20, 30, 30)), ClipOp.Difference);
            c.Rotate(15);
            c.FillRect(new RectF(0, 0, 100, 100), Teal);
            c.Restore();
        });
        MetafileContent content = Metafile.Load(file).Content;
        ReadOnlySpan<byte> d = file;

        // EMF+: Save and Restore with the same stack index; SetClipRect intersect (CombineMode 1 in bits 8 to 11) and SetClipPath
        // exclude (4) naming a path object; the rotation as SetWorldTransform.
        PlusRecord save = content.PlusRecords.Single(r => r.Type == 0x4025), restore = content.PlusRecords.Single(r => r.Type == 0x4026);
        Assert.Equal(U32(save.Data.Span, 0), U32(restore.Data.Span, 0));
        Assert.Equal(0x0100, content.PlusRecords.Single(r => r.Type == 0x4032).Flags);
        PlusRecord clipPath = content.PlusRecords.Single(r => r.Type == 0x4033);
        Assert.Equal(0x0400, clipPath.Flags & 0xFF00);
        Assert.Contains(content.PlusRecords, r => r.Type == 0x4008 && (r.Flags & 0xFF) == (clipPath.Flags & 0xFF) && ((r.Flags >> 8) & 0x7F) == 3);
        Assert.Contains(content.PlusRecords, r => r.Type == 0x402A);

        // GDI: SaveDC, EMR_EXTSELECTCLIPRGN with one device rectangle and RGN_AND, a path with EMR_SELECTCLIPPATH RGN_DIFF,
        // RestoreDC -1.
        Assert.Contains(content.Records, x => x.Type == 33);
        MetaRecord region = content.Records.Single(x => x.Type == 75);
        int o = region.Offset;
        Assert.Equal((48, 1), (I32(d, o + 8), I32(d, o + 12)));
        Assert.Equal((32, 1, 1, 16), (I32(d, o + 16), I32(d, o + 20), I32(d, o + 24), I32(d, o + 28)));
        Assert.Equal((10, 10, 60, 60), (I32(d, o + 48), I32(d, o + 52), I32(d, o + 56), I32(d, o + 60)));
        MetaRecord select = content.Records.Single(x => x.Type == 67);
        Assert.Equal(4, I32(d, select.Offset + 8));
        Assert.Equal(-1, I32(d, content.Records.Single(x => x.Type == 34).Offset + 8));
    }

    [Fact]
    public void EqualObjectsAreWrittenOnce()
    {
        byte[] file = EmfCanvasTests.Write(c =>
        {
            for (int i = 0; i < 10; i++)
            {
                c.StrokeRect(new RectF(5 + i, 5 + i, 50, 50), new Stroke(2), Teal);
                c.FillPath(EmfCanvasTests.Star(new Vector2(80, 60), 40, FillRule.EvenOdd), Teal);
            }
        });
        MetafileContent content = Metafile.Load(file).Content;
        Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 2);
        Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 3);
        Assert.Single(content.Records, x => x.Type == 95);
        Assert.Single(content.Records, x => x.Type == 39);
        Assert.Equal(10, content.PlusRecords.Count(r => r.Type == 0x400B));
    }

    [Fact]
    public void ManyObjectsReuseTheTables()
    {
        // More pens than the EMF+ table's 64 slots and the GDI table's live limit: slots are replaced, old handles deleted.
        Action<Canvas> draw = c =>
        {
            for (int i = 0; i < 1100; i++)
            {
                c.DrawLine(new Vector2(0, i % 120), new Vector2(160, i % 120), new Stroke(1 + (i * 0.01f)), new Rgba8((byte)i, (byte)(i >> 8), 0, 255));
            }
        };
        byte[] file = EmfCanvasTests.Write(draw);
        Metafile m = Metafile.Load(file);
        Assert.All(m.Content.PlusRecords.Where(r => r.Type == 0x4008), r => Assert.True((r.Flags & 0xFF) < 64));
        Assert.Contains(m.Content.Records, x => x.Type == 40); // EMR_DELETEOBJECT
        Assert.True(m.Content.ObjectCount <= 1025, $"{m.Content.ObjectCount} handles");
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), played = EmfCanvasTests.Play(file), gdi = EmfCanvasTests.Play(file, MetafilePlayback.Gdi);
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < 1);
        Assert.Empty(m.Warnings);
    }

    [Theory]
    [InlineData(MetafileKind.EmfPlusDual)]
    [InlineData(MetafileKind.EmfPlusOnly)]
    [InlineData(MetafileKind.Emf)]
    public void EveryKindRoundTrips(MetafileKind kind)
    {
        Action<Canvas> draw = EmfCanvasTests.Scenes["clip-path"];
        byte[] file = EmfCanvasTests.Write(draw, new EmfWriteOptions { Kind = kind });
        Metafile m = Metafile.Load(file);
        Assert.Equal(kind, m.Kind);
        Assert.Empty(m.Warnings);
        bool plus = kind != MetafileKind.Emf, gdi = kind != MetafileKind.EmfPlusOnly;
        Assert.Equal(plus, m.Content.PlusRecords.Count > 0);
        Assert.Equal(gdi, m.Content.Records.Any(r => r.Type is not (EmrHeader or EmrComment or EmrEof)));
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), played = EmfCanvasTests.Play(file);
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < (plus ? 0.1 : 6));
    }

    [Fact]
    public void CompressWritesEmz()
    {
        Action<Canvas> draw = EmfCanvasTests.Scenes["stroke-dashes"];
        byte[] plain = EmfCanvasTests.Write(draw), packed = EmfCanvasTests.Write(draw, new EmfWriteOptions { Compress = true });
        Assert.Equal(new byte[] { 0x1F, 0x8B, 8 }, packed[..3]);
        Assert.True(packed.Length < plain.Length);
        Metafile m = Metafile.Load(packed);
        Assert.Equal(plain, m.Content.Data);
        using Raster<Rgba8> a = EmfCanvasTests.Play(plain), b = EmfCanvasTests.Play(packed);
        Assert.Equal(0, EmfCanvasTests.Difference(a, b).Max);
    }

    [Fact]
    public void DpiSetsTheFrame()
    {
        byte[] file = EmfCanvasTests.Write(c => c.FillRect(new RectF(0, 0, 10, 10), Teal), new EmfWriteOptions { Dpi = 192 }, 193, 97);
        Metafile m = Metafile.Load(file);
        Assert.Equal((0, 0, 2540, 1270), m.Content.Frame);
        Assert.Equal(new Vector2(192), m.Dpi);
        Assert.Equal((192, 192), m.Content.PlusDpi);
    }

    [Fact]
    public void OptionsAndArgumentsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => new EmfWriteOptions { Kind = MetafileKind.Wmf });
        Assert.Throws<ArgumentException>(() => new EmfWriteOptions { Kind = (MetafileKind)9 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmfWriteOptions { Dpi = 0 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmfWriteOptions { FallbackScale = float.NaN });
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmfWriteOptions { Text = (EmfText)5 });
        Assert.Equal(MetafileKind.EmfPlusDual, EmfWriteOptions.Default.Kind);
        Assert.Equal((96f, 2f, EmfText.Outlines, false), (EmfWriteOptions.Default.Dpi, EmfWriteOptions.Default.FallbackScale, EmfWriteOptions.Default.Text, EmfWriteOptions.Default.Compress));
        Assert.Throws<ArgumentNullException>(() => new EmfCanvas(null!, 10, 10));
        Assert.Throws<ArgumentException>(() => new EmfCanvas(new MemoryStream([], writable: false), 10, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EmfCanvas(new MemoryStream(), 0, 10));

        MemoryStream stream = new();
        using (EmfCanvas canvas = new(stream, 10, 10))
        {
            Assert.Same(EmfWriteOptions.Default, canvas.Options);
        }

        Assert.True(stream.CanWrite); // left open
        Assert.True(stream.Length > 108);
    }

    [Fact]
    public void AnEmptyCanvasWritesAValidFile()
    {
        byte[] file = EmfCanvasTests.Write(_ => { });
        Metafile m = Metafile.Load(file);
        Assert.Equal((0, 0, -1, -1), m.Content.Bounds);
        Assert.Empty(m.Warnings);
        using Raster<Rgba8> r = EmfCanvasTests.Play(file);
        Assert.All(Enumerable.Range(0, r.Height), y => Assert.All(r.Row(y).ToArray(), p => Assert.Equal(0, p.A)));
    }

    // ---- Fallbacks ----

    [Fact]
    public void PaintsOtherThanSolidFallBackToImagesWithOneWarningPerKind()
    {
        SweepGradient gradient = new(new Vector2(80, 60), new GradientStop(0, new RgbaF32(1, 0, 0, 1)), new GradientStop(1, new RgbaF32(0, 0, 1, 1)));
        Action<Canvas> draw = c =>
        {
            c.FillRect(new RectF(5, 5, 60, 40), Teal);
            c.FillOval(new RectF(10, 20, 140, 80), gradient);
            c.StrokeRect(new RectF(20, 30, 100, 60), new Stroke(6), gradient);
            c.FillRect(new RectF(100, 80, 40, 30), new SolidPaint(Teal) { BlendMode = Operations.BlendMode.Multiply });
        };
        (Metafile m, EmfCanvas canvas, byte[] file) = WriteWithCanvas(draw);
        Assert.Equal(["Metafile.FallbackImage", "Metafile.FallbackImage"], canvas.Warnings.Select(w => w.Id));
        Assert.Contains("sweep gradient", canvas.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Contains("Multiply", canvas.Warnings[1].Message, StringComparison.Ordinal);
        Assert.All(canvas.Warnings, w => Assert.Equal(DiagnosticLevel.Warning, w.Level));
        Assert.Equal(3, m.Content.PlusRecords.Count(r => r.Type == 0x401A));
        Assert.Equal(3, m.Content.Records.Count(r => r.Type == 114));
        Assert.Empty(m.Warnings);

        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), played = EmfCanvasTests.Play(file), gdi = EmfCanvasTests.Play(file, MetafilePlayback.Gdi);

        // The images are drawn at twice the resolution and resampled; the sweep's hard seam is where they differ most.
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < 2, $"EMF+ {EmfCanvasTests.Difference(direct, played)}");
        Assert.True(EmfCanvasTests.Difference(direct, gdi).Mean < 6, $"GDI {EmfCanvasTests.Difference(direct, gdi)}");
    }

    [Fact]
    public void FallbackImagesHaveTheirLayout()
    {
        (Metafile m, _, byte[] file) = WriteWithCanvas(c => c.FillRect(new RectF(10, 20, 30, 15), new SweepGradient(new Vector2(25, 27), new GradientStop(0, new RgbaF32(1, 0, 0, 1)), new GradientStop(1, new RgbaF32(0, 0, 1, 0.5f)))));
        ReadOnlySpan<byte> d = file;

        // EMR_ALPHABLEND ([MS-EMF] 2.3.1.1): destination in sixteenths, AC_SRC_ALPHA with constant alpha 255, identity source
        // transform, DIB_RGB_COLORS, a 40-byte BITMAPINFOHEADER at 108 and 32-bit bottom-up pixels at 148.
        MetaRecord blend = m.Content.Records.Single(r => r.Type == 114);
        int o = blend.Offset;
        int w = I32(d, o + 100), h = I32(d, o + 104);
        Assert.True(w >= 60 && h >= 30, $"{w} by {h}");
        Assert.Equal(0x01FF0000u, U32(d, o + 40));
        Assert.Equal((1f, 0f, 0f, 1f), (F32(d, o + 52), F32(d, o + 56), F32(d, o + 60), F32(d, o + 64)));
        Assert.Equal((0u, 108, 40, 148, w * h * 4), (U32(d, o + 80), I32(d, o + 84), I32(d, o + 88), I32(d, o + 92), I32(d, o + 96)));
        Assert.Equal((40, w, h, 1, 32, 0), (I32(d, o + 108), I32(d, o + 112), I32(d, o + 116), (int)U16(d, o + 120), (int)U16(d, o + 122), I32(d, o + 124)));
        Assert.Equal(148 + (w * h * 4), blend.Size);

        // EmfPlusImage (2.2.1.4) with a compressed EmfPlusBitmap (2.2.2.2): a PNG, drawn by EmfPlusDrawImage (2.3.4.8) in pixels.
        PlusRecord image = m.Content.PlusRecords.Single(r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 5);
        ReadOnlySpan<byte> p = image.Data.Span;
        Assert.Equal((0xDBC01002u, 1, w, h, 0x0026200Au, 1), (U32(p, 0), I32(p, 4), I32(p, 8), I32(p, 12), U32(p, 20), I32(p, 24)));
        Assert.Equal(0x89, p[28]);
        Assert.Equal((byte)'P', p[29]);
        PlusRecord draw = m.Content.PlusRecords.Single(r => r.Type == 0x401A);
        Assert.Equal(image.Flags & 0xFF, draw.Flags & 0xFF);
        ReadOnlySpan<byte> q = draw.Data.Span;
        Assert.Equal((2, 0f, 0f, (float)w, (float)h), (I32(q, 4), F32(q, 8), F32(q, 12), F32(q, 16), F32(q, 20)));
        Assert.Equal(40, q.Length);
    }

    [Fact]
    public void LargeObjectsContinueOverSeveralRecords()
    {
        // A noisy fallback image whose PNG is larger than one EmfPlusObject record: pieces with the continue flag and the total size.
        Raster<Rgba8> noise = Raster.Create<Rgba8>(200, 200);
        Random random = new(7);
        for (int y = 0; y < 200; y++)
        {
            random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(noise.Row(y)));
        }

        byte[] file = EmfCanvasTests.Write(c => c.DrawImage(noise, new RectF(0, 0, 160, 120)));
        ReadOnlySpan<byte> d = file;
        MetafileContent content = Metafile.Load(file).Content;
        PlusRecord image = content.PlusRecords.Single(r => r.Type == 0x4008 && ((r.Flags >> 8) & 0x7F) == 5);
        Assert.Equal(0, image.Flags & 0x8000);
        Assert.True(image.Data.Length > EmfWriter.MaxObjectPiece);
        int pieces = 0;
        foreach (MetaRecord comment in content.Records.Where(r => r.Type == EmrComment))
        {
            for (int at = comment.Offset + 16; at < comment.Offset + comment.Size; at += I32(d, at + 4))
            {
                if (U16(d, at) == 0x4008 && (U16(d, at + 2) & 0x8000) != 0)
                {
                    Assert.Equal(image.Data.Length, I32(d, at + 12));
                    pieces++;
                }
            }
        }

        Assert.Equal((image.Data.Length + EmfWriter.MaxObjectPiece - 1) / EmfWriter.MaxObjectPiece, pieces);
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(c => c.DrawImage(noise, new RectF(0, 0, 160, 120))), played = EmfCanvasTests.Play(file);
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < 25); // noise resampled twice
    }

    [Fact]
    public void LayersWithOpacityFallBackAndPlainLayersStayVectors()
    {
        Action<Canvas> draw = c =>
        {
            c.SaveLayer(new LayerOptions { Bounds = new RectF(0, 0, 100, 100) });
            c.FillRect(new RectF(10, 10, 120, 50), Teal);
            c.Restore();
            c.SaveLayer(0.5f);
            c.FillOval(new RectF(30, 30, 100, 80), new Rgba8(255, 0, 0, 255));
            c.FillOval(new RectF(60, 50, 80, 60), new Rgba8(0, 0, 255, 255));
            c.Restore();
        };
        (Metafile m, EmfCanvas canvas, byte[] file) = WriteWithCanvas(draw);
        Assert.Single(canvas.Warnings);
        Assert.Contains("layer with opacity", canvas.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Single(m.Content.PlusRecords, r => r.Type == 0x400A); // the plain layer's rectangle
        Assert.Single(m.Content.PlusRecords, r => r.Type == 0x401A); // the opacity layer as one image
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), played = EmfCanvasTests.Play(file);
        Assert.True(EmfCanvasTests.Difference(direct, played).Mean < 1.5, $"{EmfCanvasTests.Difference(direct, played)}");
    }

    [Fact]
    public void ImagesFallBackForNow()
    {
        Raster<Rgba8> image = Raster.Create<Rgba8>(4, 4);
        image.Row(1)[2] = new Rgba8(255, 0, 0, 255);
        (Metafile m, EmfCanvas canvas, _) = WriteWithCanvas(c => c.DrawImage(image, new RectF(20, 20, 80, 80)));
        Assert.Single(canvas.Warnings);
        Assert.Contains("image", canvas.Warnings[0].Message, StringComparison.Ordinal);
        Assert.Single(m.Content.PlusRecords, r => r.Type == 0x401A);
    }

    private static (Metafile Metafile, EmfCanvas Canvas, byte[] File) WriteWithCanvas(Action<Canvas> draw, EmfWriteOptions? options = null)
    {
        using MemoryStream ms = new();
        EmfCanvas canvas = new(ms, EmfCanvasTests.W, EmfCanvasTests.H, options);
        using (canvas)
        {
            draw(canvas);
        }

        byte[] file = ms.ToArray();
        return (Metafile.Load(file), canvas, file);
    }

    private static uint U32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d[at..]);
}
