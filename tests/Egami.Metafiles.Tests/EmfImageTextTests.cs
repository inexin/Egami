using System.Buffers.Binary;
using System.Numerics;

using Egami.Drawing;
using Egami.Fonts;
using Egami.Formats.Jpeg;
using Egami.Formats.Png;
using Egami.Metafiles.Reading;
using Egami.Operations;
using Egami.Pixels;

using Path = Egami.Drawing.Path;

namespace Egami.Metafiles.Tests;

/// <summary>EMF writing, stage C: images (PNG and JPEG kept as they are), glyph runs as text, color glyphs and pictures.</summary>
public class EmfImageTextTests(ITestOutputHelper output)
{
    private const int W = EmfCanvasTests.W, H = EmfCanvasTests.H;

    private static readonly Raster<Rgba8> Checker = MakeChecker();
    private static readonly Raster<Rgba8> Photo = MakePhoto();
    private static readonly byte[] PhotoPng = Encode(Photo, png: true);
    private static readonly byte[] PhotoJpeg = Encode(Photo, png: false);

    private static readonly string FontsRoot = System.IO.Path.Combine(TestFiles.Root, "tests", "fonts");
    private static readonly Typeface Noto = Typeface.FromFile(System.IO.Path.Combine(FontsRoot, "NotoSans-Regular.ttf"));
    private static readonly Typeface NotoBold = Typeface.FromFile(System.IO.Path.Combine(FontsRoot, "NotoSans-Bold.ttf"));

    /// <summary>Image scenes, each drawn directly and through an EMF file.</summary>
    internal static readonly Dictionary<string, Action<Canvas>> Images = new()
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

    public static TheoryData<string> ImageNames => [.. Images.Keys.Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(ImageNames))]
    public void ImagesRoundTripAsImageRecords(string scene)
    {
        Action<Canvas> draw = Images[scene];
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(draw);
        Assert.Empty(warnings);
        Metafile m = Metafile.Load(file);
        Assert.Empty(m.Warnings);
        Assert.Contains(m.Content.PlusRecords, r => r.Type == 0x401B); // DrawImagePoints
        Assert.DoesNotContain(m.Content.PlusRecords, r => r.Type == 0x401A); // no fallback image
        Assert.Contains(m.Content.Records, r => r.Type == 114); // EMR_ALPHABLEND

        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), plus = EmfCanvasTests.Play(file);
        (double mean, int max, int over) = EmfCanvasTests.Difference(direct, plus);
        output.WriteLine($"{scene} EMF+: mean {mean:F3}, max {max}, over {over}");
        Assert.True(mean < (scene == "encoded-jpeg" ? 1.5 : 1), $"{scene} EMF+: mean {mean:F3}, max {max}, over {over}");

        // The GDI part one to one; the player resamples its DIBs bilinearly, which blurs nearest-neighbor edges.
        using Raster<Rgba8> gdi = EmfCanvasTests.Play(file, MetafilePlayback.Gdi, W - 1, H - 1);
        using Raster<Rgba8> cropped = Crop(direct, W - 1, H - 1);
        (double gmean, int gmax, int gover) = EmfCanvasTests.Difference(cropped, gdi);
        output.WriteLine($"{scene} GDI: mean {gmean:F3}, max {gmax}, over {gover}");
        Assert.True(gmean < (scene.StartsWith("nearest", StringComparison.Ordinal) || scene == "opacity" ? 16 : 2), $"{scene} GDI: mean {gmean:F3}, max {gmax}, over {gover}");
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void PngAndJpegBytesPassThrough(bool png)
    {
        byte[] encoded = png ? PhotoPng : PhotoJpeg;
        (byte[] file, _) = Write(c => c.DrawImage(EncodedImage.Load(encoded), new RectF(10, 10, 120, 80)));
        MetafileContent content = Metafile.Load(file).Content;
        byte[] image = ImageObject(content);

        // EmfPlusImage: version, bitmap type, then EmfPlusBitmap's width, height, stride, pixel format, compressed type, and the file.
        Assert.Equal(1u, U32(image, 4));
        Assert.Equal((uint)Photo.Width, U32(image, 8));
        Assert.Equal((uint)Photo.Height, U32(image, 12));
        Assert.Equal(png ? 0x0026200Au : 0x00021808u, U32(image, 20));
        Assert.Equal(1u, U32(image, 24));
        Assert.True(image.AsSpan(28).StartsWith(encoded), "the file's bytes are kept as they are");
        Assert.True(image.Length - 28 - encoded.Length is >= 0 and < 4);
        Assert.All(image.AsSpan(28 + encoded.Length).ToArray(), b => Assert.Equal(0, b));
    }

    [Fact]
    public void EncodedImagesWithOpacityOrArithmeticCodingAreWrittenAsPng()
    {
        (byte[] file, _) = Write(c => c.DrawImage(EncodedImage.Load(PhotoJpeg), new RectF(10, 10, 120, 80), paint: new SolidPaint(new RgbaF32(0, 0, 0, 1)) { Opacity = 0.5f }));
        byte[] image = ImageObject(Metafile.Load(file).Content);
        Assert.Equal(0x0026200Au, U32(image, 20));
        Assert.True(image.AsSpan(28).StartsWith(PngSignature));

        using MemoryStream arithmetic = new();
        Photo.Save(arithmetic, JpegFormat.Info, new JpegWriterOptions { Arithmetic = true });
        (file, _) = Write(c => c.DrawImage(EncodedImage.Load(arithmetic.ToArray()), new RectF(10, 10, 120, 80)));
        image = ImageObject(Metafile.Load(file).Content);
        Assert.True(image.AsSpan(28).StartsWith(PngSignature), "GDI+ cannot decode arithmetic coding");
    }

    [Fact]
    public void ImageRecordLayout()
    {
        (byte[] file, _) = Write(c =>
        {
            c.Translate(5, 7);
            c.DrawImage(Checker, new RectF(1, 0, 2, 4), new RectF(10, 20, 40, 80), new SamplingOptions { Filter = ResampleFilter.Nearest });
        });
        MetafileContent content = Metafile.Load(file).Content;
        Assert.Contains(content.PlusRecords, r => r.Type == 0x4021 && r.Flags == 5); // nearest neighbor
        PlusRecord draw = Assert.Single(content.PlusRecords, r => r.Type == 0x401B);
        ReadOnlySpan<byte> d = draw.Data.Span;
        Assert.Equal(2, BinaryPrimitives.ReadInt32LittleEndian(d[4..])); // UnitPixel
        Assert.Equal([1f, 0, 2, 4], [F32(d, 8), F32(d, 12), F32(d, 16), F32(d, 20)]);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(d[24..]));
        Assert.Equal([10f, 20, 50, 20, 10, 100], [F32(d, 28), F32(d, 32), F32(d, 36), F32(d, 40), F32(d, 44), F32(d, 48)]);

        // The attributes mirror at the edges; the GDI copy holds only the source's 2 x 4 pixels.
        PlusRecord attributes = Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && (r.Flags >> 8 & 0x7F) == 8);
        Assert.Equal(3u, U32(attributes.Data.ToArray(), 8)); // WrapModeTileFlipXY
        byte[] b = Body(content, Assert.Single(content.Records, r => r.Type == 114));
        Assert.Equal(2u, U32(b, 92)); // cxSrc, as offsets from the record's data
        Assert.Equal(4u, U32(b, 96));
    }

    [Fact]
    public void EmfPlusOnlyDropsTheDib()
    {
        (byte[] file, _) = Write(Images["photo"], new EmfWriteOptions { Kind = MetafileKind.EmfPlusOnly });
        MetafileContent content = Metafile.Load(file).Content;
        Assert.Contains(content.PlusRecords, r => r.Type == 0x401B);
        Assert.DoesNotContain(content.Records, r => r.Type == 114);

        (file, _) = Write(Images["photo"], new EmfWriteOptions { Kind = MetafileKind.Emf });
        content = Metafile.Load(file).Content;
        Assert.Empty(content.PlusRecords);
        Assert.Contains(content.Records, r => r.Type == 114);
    }

    [Fact]
    public void ImagesWithOtherBlendModesFallBack()
    {
        (_, IReadOnlyList<EgamiDiagnostic> warnings) = Write(c => c.DrawImage(Checker, new RectF(20, 20, 80, 80), paint: new SolidPaint(new RgbaF32(0, 0, 0, 1)) { BlendMode = BlendMode.Multiply }));
        EgamiDiagnostic warning = Assert.Single(warnings);
        Assert.Contains("image with the Multiply blend mode", warning.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void BlendModesFallBackOverWhatIsBeneath(bool layer)
    {
        Action<Canvas> draw = c =>
        {
            c.FillRect(new RectF(0, 0, W, H), new Rgba8(255, 255, 255, 255));
            c.FillRect(new RectF(20, 20, 80, 60), new Rgba8(220, 40, 40, 255));
            SolidPaint blue = new(new Rgba8(40, 80, 220, 255));
            if (layer)
            {
                c.SaveLayer(new LayerOptions { BlendMode = BlendMode.Multiply });
                c.FillOval(new RectF(60, 40, 80, 60), blue);
                c.Restore();
            }
            else
            {
                c.FillOval(new RectF(60, 40, 80, 60), blue with { BlendMode = BlendMode.Multiply });
            }
        };
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(draw);
        Assert.Single(warnings);
        MetafileContent content = Metafile.Load(file).Content;
        Assert.Contains(content.PlusRecords, r => r.Type == 0x4023 && r.Flags == 1); // the image replaces the pixels beneath

        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), plus = EmfCanvasTests.Play(file);
        (double mean, int max, int over) = EmfCanvasTests.Difference(direct, plus);
        output.WriteLine($"layer {layer}: mean {mean:F3}, max {max}, over {over}");
        Assert.True(mean < 1, $"mean {mean:F3}, max {max}, over {over}");
    }

    // ---- Text ----

    private static readonly Action<Canvas> Text = c =>
    {
        c.FillRect(new RectF(0, 0, W, H), new Rgba8(255, 255, 255, 255));
        c.DrawText("Egami EMF", new Vector2(8, 40), new Font(Noto, 24), new SolidPaint(new Rgba8(20, 30, 120, 255)));
        c.DrawText("Bold text", new Vector2(8, 80), new Font(NotoBold, 18), new SolidPaint(new Rgba8(180, 20, 20, 255)));
        c.Save();
        c.Translate(100, 110);
        c.Rotate(-20);
        c.DrawText("turned", Vector2.Zero, new Font(Noto, 14), new SolidPaint(new Rgba8(0, 120, 60, 255)));
        c.Restore();
    };

    [Theory]
    [InlineData(EmfText.Outlines)]
    [InlineData(EmfText.Glyphs)]
    public void TextInBothModes(EmfText mode)
    {
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(Text, new EmfWriteOptions { Text = mode });
        Assert.Empty(warnings);
        Metafile m = Metafile.Load(file);
        bool glyphs = mode == EmfText.Glyphs;
        Assert.Equal(glyphs ? 3 : 0, m.Content.PlusRecords.Count(r => r.Type == 0x4036)); // DrawDriverString
        Assert.Equal(glyphs ? 3 : 0, m.Content.Records.Count(r => r.Type == 84)); // EMR_EXTTEXTOUTW
        Assert.Equal(glyphs ? 3 : 0, m.Content.Records.Count(r => r.Type == 82)); // one LOGFONT per face and size

        using Raster<Rgba8> direct = EmfCanvasTests.Direct(Text);
        using Raster<Rgba8> plus = Play(file, MetafilePlayback.Auto, W, H);
        (double mean, int max, int over) = EmfCanvasTests.Difference(direct, plus);
        output.WriteLine($"{mode} EMF+: mean {mean:F3}, max {max}, over {over}");
        Assert.True(mean < 0.5, $"{mode} EMF+: mean {mean:F3}, max {max}, over {over}");

        using Raster<Rgba8> gdi = Play(file, MetafilePlayback.Gdi, W - 1, H - 1);
        using Raster<Rgba8> cropped = Crop(direct, W - 1, H - 1);
        (double gmean, int gmax, int gover) = EmfCanvasTests.Difference(cropped, gdi);
        output.WriteLine($"{mode} GDI: mean {gmean:F3}, max {gmax}, over {gover}");
        Assert.True(gmean < 1, $"{mode} GDI: mean {gmean:F3}, max {gmax}, over {gover}");
    }

    [Fact]
    public void GlyphRecordLayout()
    {
        GlyphRun run = new Font(Noto, 20).Shape("Hit"); // an odd count: the positions follow the glyphs unaligned
        (byte[] file, _) = Write(c => c.DrawGlyphRun(run, new Vector2(10, 50), new SolidPaint(new Rgba8(10, 20, 30, 255))), new EmfWriteOptions { Text = EmfText.Glyphs });
        MetafileContent content = Metafile.Load(file).Content;

        // EmfPlusFont: em 20 in world units, regular, "Noto Sans".
        byte[] font = Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && (r.Flags >> 8 & 0x7F) == 6).Data.ToArray();
        Assert.Equal(20f, BitConverter.ToSingle(font, 4));
        Assert.Equal(0u, U32(font, 8));
        Assert.Equal(0u, U32(font, 12));
        Assert.Equal("Noto Sans", System.Text.Encoding.Unicode.GetString(font, 24, (int)U32(font, 20) * 2));

        PlusRecord text = Assert.Single(content.PlusRecords, r => r.Type == 0x4036);
        Assert.Equal(0x8000, text.Flags & 0x8000); // a color for the brush
        byte[] d = text.Data.ToArray();
        Assert.Equal(0xFF0A141Eu, U32(d, 0));
        Assert.Equal(0u, U32(d, 4)); // glyph indices
        Assert.Equal(3u, U32(d, 12));
        Assert.Equal(run.Glyphs[0], BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(16)));
        Assert.Equal(10 + run.Positions[1].X, BitConverter.ToSingle(d, 30));
        Assert.Equal(50f, BitConverter.ToSingle(d, 34));

        // The GDI part: LOGFONT height -320 (sixteenths), weight 400, "Noto Sans"; ExtTextOutW with ETO_GLYPH_INDEX.
        byte[] lf = Body(content, Assert.Single(content.Records, r => r.Type == 82));
        Assert.Equal(-320, BinaryPrimitives.ReadInt32LittleEndian(lf.AsSpan(4)));
        Assert.Equal(400, BinaryPrimitives.ReadInt32LittleEndian(lf.AsSpan(20)));
        Assert.StartsWith("Noto Sans\0", System.Text.Encoding.Unicode.GetString(lf, 32, 64), StringComparison.Ordinal);
        Assert.Contains(content.Records, r => r.Type == 22 && U32(Body(content, r), 0) == 24); // TA_BASELINE
        byte[] eto = Body(content, Assert.Single(content.Records, r => r.Type == 84));
        Assert.Equal(160, BinaryPrimitives.ReadInt32LittleEndian(eto.AsSpan(28))); // reference x
        Assert.Equal(800, BinaryPrimitives.ReadInt32LittleEndian(eto.AsSpan(32)));
        Assert.Equal(3u, U32(eto, 36));
        Assert.Equal(0x10u, U32(eto, 44));
        Assert.Equal((int)MathF.Round(run.Positions[1].X * 16), BinaryPrimitives.ReadInt32LittleEndian(eto.AsSpan((int)U32(eto, 64) - 8))); // the first advance
    }

    public static TheoryData<string> OutlineCases => ["synthetic-bold", "variable-instance", "turned-glyphs", "gradient", "mirrored"];

    [Theory]
    [MemberData(nameof(OutlineCases))]
    public void TextFallsBackToOutlines(string kind)
    {
        Typeface variable = Typeface.FromFile(System.IO.Path.Combine(FontsRoot, "test_glyphs-glyf_colr_1_variable.ttf"));
        Action<Canvas> draw = kind switch
        {
            "synthetic-bold" => c => c.DrawText("Bold", new Vector2(10, 50), new Font(Noto, 20) { Synthesis = FontSynthesis.Bold }, new SolidPaint(new Rgba8(0, 0, 0, 255))),
            "variable-instance" => c => c.DrawGlyphRun(GlyphRun.Create(new Font(variable.WithVariations(variable.VariationAxes.Select(a => new FontVariation(a.Tag, a.Max)).ToArray()), 20) { ColorGlyphs = false }, [1, 2], [Vector2.Zero, new Vector2(20, 0)]), new Vector2(10, 50), new SolidPaint(new Rgba8(0, 0, 0, 255))),
            "turned-glyphs" => c => c.DrawGlyphRun(GlyphRun.CreateTransformed(new Font(Noto, 20), new Font(Noto, 20).Shape("ab").Glyphs, [GlyphTransform.FromRotationScale(1, 30, Vector2.Zero), GlyphTransform.FromRotationScale(1, 0, new Vector2(20, 0))]), new Vector2(10, 50), new SolidPaint(new Rgba8(0, 0, 0, 255))),
            "gradient" => c => c.DrawText("Grad", new Vector2(10, 50), new Font(Noto, 20), new LinearGradient(new Vector2(10, 0), new Vector2(80, 0), new RgbaF32(1, 0, 0, 1), new RgbaF32(0, 0, 1, 1))),
            _ => c =>
            {
                c.Scale(1, -1);
                c.DrawText("Flip", new Vector2(10, -50), new Font(Noto, 20), new SolidPaint(new Rgba8(0, 0, 0, 255)));
            }
            ,
        };
        (byte[] file, _) = Write(draw, new EmfWriteOptions { Text = EmfText.Glyphs });
        MetafileContent content = Metafile.Load(file).Content;
        Assert.DoesNotContain(content.PlusRecords, r => r.Type == 0x4036);
        Assert.DoesNotContain(content.Records, r => r.Type == 84);
        Assert.Contains(content.PlusRecords, r => r.Type is 0x4014 or 0x4015); // FillPath of the outlines (a brush or a color)
    }

    public static TheoryData<string> ColorFonts => ["Twemoji-COLRv0-subset.ttf", "NotoColorEmoji-CBDT-subset.ttf", "Twemoji-sbix.ttf"];

    [Theory]
    [MemberData(nameof(ColorFonts))]
    public void ColorGlyphsAreDrawnAsLayersOrImages(string fontFile)
    {
        Typeface face = Typeface.FromFile(System.IO.Path.Combine(FontsRoot, "derived", fontFile));
        ushort glyph = Enumerable.Range(1, face.GlyphCount - 1).Select(g => (ushort)g).First(face.HasColorGlyph);
        GlyphRun run = GlyphRun.Create(new Font(face, 48), [glyph], [Vector2.Zero]);
        Action<Canvas> draw = c =>
        {
            c.FillRect(new RectF(0, 0, W, H), new Rgba8(255, 255, 255, 255));
            c.DrawGlyphRun(run, new Vector2(40, 80), new SolidPaint(new Rgba8(0, 0, 0, 255)));
        };
        (byte[] file, _) = Write(draw, new EmfWriteOptions { Text = EmfText.Glyphs });
        MetafileContent content = Metafile.Load(file).Content;
        Assert.DoesNotContain(content.PlusRecords, r => r.Type == 0x4036); // no text record for a color glyph
        bool bitmap = !fontFile.Contains("COLR", StringComparison.Ordinal);
        Assert.Equal(bitmap, content.PlusRecords.Any(r => r.Type == 0x401B));

        using Raster<Rgba8> direct = EmfCanvasTests.Direct(draw), plus = EmfCanvasTests.Play(file);
        (double mean, int max, int over) = EmfCanvasTests.Difference(direct, plus);
        output.WriteLine($"{fontFile}: mean {mean:F3}, max {max}, over {over}");
        Assert.True(mean < 1, $"{fontFile}: mean {mean:F3}, max {max}, over {over}");
    }

    // ---- Pictures ----

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PicturesArePlayedInline(bool cacheAsImage)
    {
        PictureRecorder recorder = new(new RectF(0, 0, 40, 40));
        recorder.FillOval(new RectF(0, 0, 40, 40), new Rgba8(200, 40, 40, 255));
        recorder.FillRect(new RectF(10, 10, 20, 20), new Rgba8(40, 40, 200, 255));
        Picture picture = recorder.Finish();
        Action<Canvas> draw = c =>
        {
            c.DrawPicture(picture, Matrix3x2.CreateTranslation(10, 10), cacheAsImage: cacheAsImage);
            c.DrawPicture(picture, Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(80, 30), cacheAsImage: cacheAsImage);
        };
        (byte[] file, IReadOnlyList<EgamiDiagnostic> warnings) = Write(draw);
        Assert.Empty(warnings);
        MetafileContent content = Metafile.Load(file).Content;
        Assert.DoesNotContain(content.PlusRecords, r => r.Type is 0x401A or 0x401B);
        Assert.Equal(2, content.PlusRecords.Count(r => r.Type == 0x400E)); // FillEllipse, one per picture
        using Raster<Rgba8> direct = EmfCanvasTests.Direct(c =>
        {
            c.DrawPicture(picture, Matrix3x2.CreateTranslation(10, 10));
            c.DrawPicture(picture, Matrix3x2.CreateScale(1.5f) * Matrix3x2.CreateTranslation(80, 30));
        });
        using Raster<Rgba8> plus = EmfCanvasTests.Play(file);
        (double mean, int max, int over) = EmfCanvasTests.Difference(direct, plus);
        Assert.True(mean < 0.5, $"mean {mean:F3}, max {max}, over {over}");
    }

    // ---- Helpers ----

    private static readonly byte[] PngSignature = [0x89, (byte)'P', (byte)'N', (byte)'G'];

    private static Raster<Rgba8> Play(byte[] file, MetafilePlayback playback, int w, int h)
    {
        FontCollection fonts = new();
        fonts.Add(Noto);
        fonts.Add(NotoBold);
        return Metafile.Load(file).Rasterize<Rgba8>(new MetafileRenderOptions { Width = w, Height = h, Playback = playback, Fonts = fonts });
    }

    /// <summary>The data of the file's one EMF+ image object, which the reader joins from its pieces.</summary>
    private static byte[] ImageObject(MetafileContent content)
        => Assert.Single(content.PlusRecords, r => r.Type == 0x4008 && (r.Flags >> 8 & 0x7F) == 5).Data.ToArray();

    /// <summary>An EMF record's data after its type and size.</summary>
    private static byte[] Body(MetafileContent content, MetaRecord r) => content.Data.AsSpan(r.Offset + 8, r.Size - 8).ToArray();

    private static (byte[] File, IReadOnlyList<EgamiDiagnostic> Warnings) Write(Action<Canvas> draw, EmfWriteOptions? options = null)
    {
        using MemoryStream ms = new();
        EmfCanvas canvas = new(ms, W, H, options);
        using (canvas)
        {
            draw(canvas);
        }

        return (ms.ToArray(), canvas.Warnings);
    }

    private static uint U32(byte[] d, int at) => BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at));

    private static float F32(ReadOnlySpan<byte> d, int at) => BinaryPrimitives.ReadSingleLittleEndian(d[at..]);

    private static Raster<Rgba8> Crop(Raster<Rgba8> r, int w, int h)
    {
        Raster<Rgba8> c = Raster.Create<Rgba8>(w, h);
        for (int y = 0; y < h; y++)
        {
            r.Row(y)[..w].CopyTo(c.Row(y));
        }

        return c;
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

    /// <summary>A smooth opaque image, for bilinear sampling and JPEG.</summary>
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
}
