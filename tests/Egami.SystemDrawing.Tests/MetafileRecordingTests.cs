using System.Buffers.Binary;
using System.Runtime.InteropServices;

using Egami.Pixels;
using Egami.SystemDrawing.Drawing2D;

namespace Egami.SystemDrawing.Tests;

using Point = System.Drawing.Point;
using Size = System.Drawing.Size;

/// <summary>
/// Recording metafiles with Graphics.FromImage, checked against what GDI+ does on Windows (measured with System.Drawing: the
/// frames, the bounds, the errors and Save), and against GDI+'s own recordings of the same calls (RecordingScenes, recorded by
/// egami-compare into tests/images/metafiles/recorded) by playing both with Egami's player.
/// </summary>
public class MetafileRecordingTests
{
    private static readonly IntPtr Hdc = IntPtr.Zero;

    /// <summary>Every recording constructor: the target it writes to (0 memory, 1 file, 2 stream) and how it is called.</summary>
    public static TheoryData<string, int> Constructors => new(Factories.Keys.Select(k => (k, Factories[k].Target)).Select(t => new TheoryDataRow<string, int>(t.k, t.Target)));

    private static readonly RectangleF FrameF = new(0, 0, 120, 90);
    private static readonly Rectangle FrameI = new(0, 0, 3175, 2381);

    private static Dictionary<string, (int Target, Func<string, Stream, Metafile> Make)> Factories { get; } = new()
    {
        ["hdc,Rectangle"] = (0, (f, s) => new Metafile(Hdc, FrameI)),
        ["hdc,EmfType"] = (0, (f, s) => new Metafile(Hdc, EmfType.EmfPlusDual)),
        ["hdc,RectangleF"] = (0, (f, s) => new Metafile(Hdc, new RectangleF(0, 0, 3175, 2381))),
        ["hdc,RectangleF,unit"] = (0, (f, s) => new Metafile(Hdc, FrameF, MetafileFrameUnit.Pixel)),
        ["hdc,RectangleF,unit,type"] = (0, (f, s) => new Metafile(Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfOnly)),
        ["hdc,RectangleF,unit,type,desc"] = (0, (f, s) => new Metafile(Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfPlusOnly, "d")),
        ["hdc,Rectangle,unit"] = (0, (f, s) => new Metafile(Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel)),
        ["hdc,Rectangle,unit,type"] = (0, (f, s) => new Metafile(Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual)),
        ["hdc,EmfType,desc"] = (0, (f, s) => new Metafile(Hdc, EmfType.EmfOnly, "d")),
        ["hdc,Rectangle,unit,type,desc"] = (0, (f, s) => new Metafile(Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual, "d")),
        ["file,hdc"] = (1, (f, s) => new Metafile(f, Hdc)),
        ["file,hdc,type"] = (1, (f, s) => new Metafile(f, Hdc, EmfType.EmfOnly)),
        ["file,hdc,RectangleF"] = (1, (f, s) => new Metafile(f, Hdc, new RectangleF(0, 0, 3175, 2381))),
        ["file,hdc,RectangleF,unit"] = (1, (f, s) => new Metafile(f, Hdc, FrameF, MetafileFrameUnit.Pixel)),
        ["file,hdc,RectangleF,unit,type"] = (1, (f, s) => new Metafile(f, Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfPlusOnly)),
        ["file,hdc,RectangleF,unit,desc"] = (1, (f, s) => new Metafile(f, Hdc, FrameF, MetafileFrameUnit.Pixel, "d")),
        ["file,hdc,RectangleF,unit,type,desc"] = (1, (f, s) => new Metafile(f, Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfPlusDual, "d")),
        ["file,hdc,Rectangle"] = (1, (f, s) => new Metafile(f, Hdc, FrameI)),
        ["file,hdc,Rectangle,unit"] = (1, (f, s) => new Metafile(f, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel)),
        ["file,hdc,Rectangle,unit,type"] = (1, (f, s) => new Metafile(f, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, EmfType.EmfOnly)),
        ["file,hdc,Rectangle,unit,desc"] = (1, (f, s) => new Metafile(f, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, "d")),
        ["file,hdc,type,desc"] = (1, (f, s) => new Metafile(f, Hdc, EmfType.EmfPlusDual, "d")),
        ["file,hdc,Rectangle,unit,type,desc"] = (1, (f, s) => new Metafile(f, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual, "d")),
        ["stream,hdc"] = (2, (f, s) => new Metafile(s, Hdc)),
        ["stream,hdc,type"] = (2, (f, s) => new Metafile(s, Hdc, EmfType.EmfOnly)),
        ["stream,hdc,RectangleF"] = (2, (f, s) => new Metafile(s, Hdc, new RectangleF(0, 0, 3175, 2381))),
        ["stream,hdc,RectangleF,unit"] = (2, (f, s) => new Metafile(s, Hdc, FrameF, MetafileFrameUnit.Pixel)),
        ["stream,hdc,RectangleF,unit,type"] = (2, (f, s) => new Metafile(s, Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfPlusOnly)),
        ["stream,hdc,Rectangle"] = (2, (f, s) => new Metafile(s, Hdc, FrameI)),
        ["stream,hdc,Rectangle,unit"] = (2, (f, s) => new Metafile(s, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel)),
        ["stream,hdc,Rectangle,unit,type"] = (2, (f, s) => new Metafile(s, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, EmfType.EmfOnly)),
        ["stream,hdc,type,desc"] = (2, (f, s) => new Metafile(s, Hdc, EmfType.EmfPlusDual, "d")),
        ["stream,hdc,RectangleF,unit,type,desc"] = (2, (f, s) => new Metafile(s, Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfPlusDual, "d")),
        ["stream,hdc,Rectangle,unit,type,desc"] = (2, (f, s) => new Metafile(s, Hdc, new Rectangle(0, 0, 120, 90), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual, "d")),
    };

    [Fact]
    public void EveryRecordingConstructorIsCovered()
    {
        int recording = typeof(Metafile).GetConstructors().Count(c => c.GetParameters().Any(p => p.Name == "referenceHdc"));
        Assert.Equal(recording, Factories.Count);
    }

    [Theory]
    [MemberData(nameof(Constructors))]
    public void EveryRecordingConstructorRecordsADrawableMetafile(string name, int target)
    {
        string file = Path.Combine(Path.GetTempPath(), $"egami-recording-{Guid.NewGuid():N}.emf");
        using MemoryStream stream = new();
        stream.Write([1, 2, 3]);
        try
        {
            using Metafile metafile = Factories[name].Make(file, stream);
            if (target == 1)
            {
                // GDI+ creates the file at once, and writes it when the recording ends.
                Assert.Equal(0, new FileInfo(file).Length);
            }

            using (Graphics g = Graphics.FromImage(metafile))
            {
                g.FillRectangle(Brushes.Red, 10, 10, 60, 40);
            }

            byte[] written = target switch
            {
                1 => File.ReadAllBytes(file),
                2 => stream.ToArray()[3..],
                _ => [],
            };
            if (target != 0)
            {
                Assert.Equal(written.Length, metafile.GetMetafileHeader().MetafileSize);
                using Metafile again = new(new MemoryStream(written));
                Assert.Equal(metafile.Size, again.Size);
            }

            Assert.True(stream.CanWrite);
            Assert.True(metafile.GetMetafileHeader().IsEmfOrEmfPlus());
            using Bitmap bitmap = new(metafile.Width, metafile.Height);
            using (Graphics g = Graphics.FromImage(bitmap))
            {
                g.DrawImage(metafile, 0, 0, metafile.Width, metafile.Height);
            }

            Color c = bitmap.GetPixel(30, 30);
            Assert.True(c.R > 200 && c.G < 60 && c.A == 255, $"{name}: {c}");
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Theory]
    [InlineData(EmfType.EmfOnly, MetafileType.Emf)]
    [InlineData(EmfType.EmfPlusOnly, MetafileType.EmfPlusOnly)]
    [InlineData(EmfType.EmfPlusDual, MetafileType.EmfPlusDual)]
    public void TheEmfTypeIsTheKindOfFile(EmfType type, MetafileType expected)
    {
        using Metafile metafile = Recorded(new Metafile(Hdc, FrameF, MetafileFrameUnit.Pixel, type), g => g.FillEllipse(Brushes.Blue, 10, 10, 50, 30));
        MetafileHeader header = metafile.GetMetafileHeader();
        Assert.Equal(expected, header.Type);
        Assert.Equal((96f, 96f), (header.DpiX, header.DpiY));
        Assert.Equal(type == EmfType.EmfOnly ? 0 : 96, header.LogicalDpiX);
    }

    /// <summary>
    /// The frame (10, 20, 200, 100) in each unit, as GDI+ writes it (the rule measured with System.Drawing at its screen's DPI,
    /// here at 96): in 0.01 mm, the right and bottom one device pixel in from the edge, except in GDI's own unit.
    /// </summary>
    [Theory]
    [InlineData(MetafileFrameUnit.Pixel, 265, 529, 5530, 3149, 200, 100)]
    [InlineData(MetafileFrameUnit.Point, 353, 706, 7382, 4207, 267, 133)]
    [InlineData(MetafileFrameUnit.Inch, 25400, 50800, 533374, 304774, 19200, 9600)]
    [InlineData(MetafileFrameUnit.Document, 85, 169, 1752, 990, 64, 32)]
    [InlineData(MetafileFrameUnit.Millimeter, 1000, 2000, 20974, 11974, 756, 378)]
    [InlineData(MetafileFrameUnit.GdiCompatible, 10, 20, 210, 120, 9, 5)]
    public void FramesAreWrittenAsGdiPlusWritesThem(MetafileFrameUnit unit, int left, int top, int right, int bottom, int width, int height)
    {
        using MemoryStream stream = new();
        using Metafile metafile = Recorded(new Metafile(stream, Hdc, new RectangleF(10, 20, 200, 100), unit, EmfType.EmfPlusDual), g => g.FillRectangle(Brushes.Red, 30, 40, 50, 60));
        byte[] file = stream.ToArray();
        Assert.Equal((left, top, right, bottom), (I(file, 24), I(file, 28), I(file, 32), I(file, 36)));
        Assert.Equal((30, 40, 79, 99), (I(file, 8), I(file, 12), I(file, 16), I(file, 20)));
        Assert.Equal(new Size(width, height), metafile.Size);
    }

    [Fact]
    public void WithoutAFrameTheFrameIsTheBoundsOfWhatIsDrawn()
    {
        using MemoryStream stream = new();
        using Metafile metafile = Recorded(new Metafile(stream, Hdc), g =>
        {
            g.FillRectangle(Brushes.Red, 30, 40, 50, 60);
            g.Clear(Color.Blue); // a Clear adds nothing to the bounds, as in GDI+
        });
        byte[] file = stream.ToArray();
        Assert.Equal((30, 40, 79, 99), (I(file, 8), I(file, 12), I(file, 16), I(file, 20)));
        Assert.Equal((794, 1058, 2090, 2619), (I(file, 24), I(file, 28), I(file, 32), I(file, 36)));
        Assert.Equal(new Size(50, 60), metafile.Size);
        GraphicsUnit unit = GraphicsUnit.Display;
        RectangleF bounds = metafile.GetBounds(ref unit);
        Assert.Equal(30, bounds.X, 0.1f);
        Assert.Equal(40, bounds.Y, 0.1f);
    }

    [Fact]
    public void ANegativeOrEmptyFrameIsRefusedAsGdiPlusRefusesIt()
    {
        foreach (MetafileFrameUnit unit in new[] { MetafileFrameUnit.Pixel, MetafileFrameUnit.Point, MetafileFrameUnit.Inch, MetafileFrameUnit.Document, MetafileFrameUnit.Millimeter })
        {
            Assert.IsType<ExternalException>(Record.Exception(() => new Metafile(Hdc, RectangleF.Empty, unit)));
            Assert.IsType<ExternalException>(Record.Exception(() => new Metafile(Hdc, new RectangleF(5, 5, 0, 10), unit)));
        }

        Assert.IsType<ExternalException>(Record.Exception(() => new Metafile(Hdc, new RectangleF(0, 0, -10, 10))));

        // GDI's own unit takes an empty frame as it is.
        using MemoryStream stream = new();
        using Metafile empty = Recorded(new Metafile(stream, Hdc, RectangleF.Empty, MetafileFrameUnit.GdiCompatible), g => g.FillRectangle(Brushes.Red, 30, 40, 50, 60));
        byte[] file = stream.ToArray();
        Assert.Equal((0, 0, 0, 0), (I(file, 24), I(file, 28), I(file, 32), I(file, 36)));
    }

    [Fact]
    public void ArgumentsAreCheckedAsGdiPlusChecksThem()
    {
        Assert.Equal("stream", Assert.Throws<ArgumentNullException>(() => new Metafile((Stream)null!, Hdc)).ParamName);
        Assert.Equal("path", Assert.Throws<ArgumentNullException>(() => new Metafile((string)null!, Hdc)).ParamName);
        Assert.IsType<ArgumentException>(Record.Exception(() => new Metafile(Hdc, (EmfType)99)));
        Assert.IsType<ArgumentException>(Record.Exception(() => new Metafile(Hdc, FrameF, (MetafileFrameUnit)99)));
    }

    [Fact]
    public void OneGraphicsRecordsAndTheMetafileWaitsForIt()
    {
        using Metafile metafile = new(Hdc, FrameF, MetafileFrameUnit.Pixel);
        Graphics g = Graphics.FromImage(metafile);
        Assert.Equal((96f, 96f, GraphicsUnit.Display), (g.DpiX, g.DpiY, g.PageUnit));
        Assert.Equal(8388608, g.VisibleClipBounds.Width);

        // As GDI+: a second Graphics is refused, the size waits for the recording, the header is empty until then.
        ExternalException second = Assert.IsType<ExternalException>(Record.Exception(() => Graphics.FromImage(metafile)));
        Assert.Equal(unchecked((int)0x8000FFFF), second.ErrorCode);
        Assert.IsType<ArgumentException>(Record.Exception(() => metafile.Size));
        Assert.True(metafile.GetMetafileHeader().IsEmfOrEmfPlus());

        // Drawing the metafile onto itself draws nothing, where GDI+ does not fail either.
        g.DrawImage(metafile, 0, 0);
        g.DrawImage(metafile, new Rectangle(0, 0, 10, 10));
        g.FillRectangle(Brushes.Red, 0, 0, 10, 10);
        g.Dispose();
        g.Dispose();

        Assert.Equal(new Size(120, 90), metafile.Size);
        ExternalException after = Assert.IsType<ExternalException>(Record.Exception(() => Graphics.FromImage(metafile)));
        Assert.Equal(unchecked((int)0x8000FFFF), after.ErrorCode);
    }

    [Fact]
    public void ALoadedMetafileCannotBeRecordedInto()
    {
        using Metafile recorded = Recorded(new Metafile(Hdc, FrameF, MetafileFrameUnit.Pixel), g => g.FillRectangle(Brushes.Red, 0, 0, 10, 10));
        using Metafile loaded = (Metafile)recorded.Clone();
        ExternalException e = Assert.IsType<ExternalException>(Record.Exception(() => Graphics.FromImage(loaded)));
        Assert.Equal(unchecked((int)0x8000FFFF), e.ErrorCode);
    }

    [Fact]
    public void ARecordedMetafileSavesAsPngLikeGdiPlus()
    {
        string file = Path.Combine(Path.GetTempPath(), $"egami-recording-{Guid.NewGuid():N}.emf");
        try
        {
            using Metafile metafile = Recorded(new Metafile(Hdc, new RectangleF(0, 0, 100, 50), MetafileFrameUnit.Pixel), g => g.FillRectangle(Brushes.Red, 0, 0, 10, 10));
            using MemoryStream png = new();
            metafile.Save(png, ImageFormat.Png);
            using Bitmap back = new(new MemoryStream(png.ToArray()));
            Assert.Equal((100, 50), (back.Width, back.Height));
            Assert.Equal(Color.FromArgb(255, 255, 0, 0), back.GetPixel(5, 5));
            Assert.Equal(0, back.GetPixel(50, 30).A);

            // GDI+ has no EMF encoder: Save(path) writes PNG whatever the name, and Save(stream, Emf) finds no encoder.
            metafile.Save(file);
            Assert.Equal(0x89, File.ReadAllBytes(file)[0]);
            Assert.Throws<ArgumentNullException>(() => metafile.Save(new MemoryStream(), ImageFormat.Emf));
        }
        finally
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }
    }

    [Theory]
    [InlineData(EmfType.EmfPlusDual)]
    [InlineData(EmfType.EmfOnly)]
    public void ARecordedMetafileIsWalkedRecordByRecord(EmfType type)
    {
        using Metafile metafile = Recorded(new Metafile(Hdc, FrameF, MetafileFrameUnit.Pixel, type), g =>
        {
            g.FillRectangle(Brushes.Red, 30, 40, 50, 60);
            using Font font = new("Arial", 12);
            g.DrawString("Egami", font, Brushes.Black, 5, 5);
        });
        List<EmfPlusRecordType> types = [];
        using Bitmap bitmap = new(10, 10);
        using (Graphics g = Graphics.FromImage(bitmap))
        {
            g.EnumerateMetafile(metafile, new Point(0, 0), (t, flags, size, data, play) =>
            {
                types.Add(t);
                return true;
            });
        }

        Assert.Equal(EmfPlusRecordType.EmfHeader, types[0]);
        Assert.Equal(EmfPlusRecordType.EmfEof, types[^1]);
        if (type == EmfType.EmfPlusDual)
        {
            // GDI+ walks a dual file's EMF+ records only; the text is glyph records, as GDI+ records it.
            Assert.Equal(EmfPlusRecordType.Header, types[1]);
            Assert.Contains(EmfPlusRecordType.DrawDriverString, types);
            Assert.Equal(EmfPlusRecordType.EndOfFile, types[^2]);
            Assert.DoesNotContain(EmfPlusRecordType.EmfExtTextOutW, types);
        }
        else
        {
            Assert.Contains(EmfPlusRecordType.EmfExtTextOutW, types);
            Assert.DoesNotContain(EmfPlusRecordType.Header, types);
        }
    }

    [Fact]
    public void TheDescriptionIsInTheHeader()
    {
        using MemoryStream stream = new();
        using Metafile metafile = Recorded(new Metafile(stream, Hdc, FrameF, MetafileFrameUnit.Pixel, EmfType.EmfPlusDual, "Chart"), g => g.FillRectangle(Brushes.Red, 0, 0, 10, 10));
        byte[] file = stream.ToArray();
        int count = I(file, 64), offset = I(file, 68);
        Assert.Contains("Chart", System.Text.Encoding.Unicode.GetString(file, offset, count * 2));
    }

    [Fact]
    public void SettingsGdiPlusRecordsTakeEffect()
    {
        // Antialiasing, page units and scale apply as on a bitmap: the same picture either way.
        static void Draw(Graphics g)
        {
            g.Clear(Color.White);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PageUnit = GraphicsUnit.Millimeter;
            g.PageScale = 2;
            using Pen pen = new(Color.Black, 0.5f);
            g.DrawEllipse(pen, 2, 2, 20, 12);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
        }

        using Metafile metafile = Recorded(new Metafile(Hdc, new RectangleF(0, 0, 200, 120), MetafileFrameUnit.Pixel), Draw);
        using Bitmap direct = new(200, 120);
        using (Graphics g = Graphics.FromImage(direct))
        {
            Draw(g);
        }

        using Bitmap played = new(200, 120);
        using (Graphics g = Graphics.FromImage(played))
        {
            g.DrawImage(metafile, new Rectangle(0, 0, 200, 120));
        }

        Assert.True(Mean(direct, played) < 3, $"mean {Mean(direct, played)}");
    }

    /// <summary>
    /// The same calls recorded by GDI+ (tests/images/metafiles/recorded, by egami-compare on Windows) and by Egami, both played by
    /// Egami's player: the pictures agree. GDI+ wrote its files on a 106 DPI screen, which the frame maps away.
    /// </summary>
    [Theory]
    [MemberData(nameof(SceneNames))]
    public void RecordingsPlayAsGdiPlusRecordingsOfTheSameCalls(string scene)
    {
        byte[] gdiplus = File.ReadAllBytes(TestFiles.Image(Path.Combine("metafiles", "recorded"), scene + ".emf"));
        using MemoryStream stream = new();
        using (Metafile metafile = new(stream, Hdc, RecordingScenes.Frame, MetafileFrameUnit.Pixel, EmfType.EmfPlusDual))
        {
            using Graphics g = Graphics.FromImage(metafile);
            RecordingScenes.All.Single(s => s.Name == scene).Draw(g);
        }

        foreach (Egami.Metafiles.MetafilePlayback playback in new[] { Egami.Metafiles.MetafilePlayback.Auto, Egami.Metafiles.MetafilePlayback.Gdi })
        {
            Egami.Metafiles.MetafileRenderOptions options = new() { Width = RecordingScenes.Frame.Width, Height = RecordingScenes.Frame.Height, Playback = playback };
            using Raster<Rgba8> theirs = Egami.Metafiles.Metafile.Load(gdiplus).Rasterize<Rgba8>(options);
            using Raster<Rgba8> ours = Egami.Metafiles.Metafile.Load(stream.ToArray()).Rasterize<Rgba8>(options);
            // GDI+ leaves a GDI copy's background (its Clear) out and dithers translucent fills; seen over white, they agree.
            double mean = playback == Egami.Metafiles.MetafilePlayback.Gdi ? Mean(OverWhite(theirs), OverWhite(ours)) : Mean(theirs, ours);

            Assert.True(mean < SceneTolerance(scene, playback), $"{scene} {playback}: mean {mean:F3}");
        }
    }

    public static TheoryData<string> SceneNames => new(RecordingScenes.All.Select(s => s.Name));

    /// <summary>
    /// How far the two may differ, from the means measured (EMF+ 0.005 to 2.9, and 5.2 for gradients). A hatch is a GDI+ hatch
    /// brush in GDI+'s file and an 8 by 8 pattern image in Egami's, which the player samples half a pixel apart. GDI copies differ
    /// more: GDI+ dithers translucent fills and gradients in stripes, Egami writes opaque colors and gradient fills.
    /// </summary>
    private static double SceneTolerance(string scene, Egami.Metafiles.MetafilePlayback playback) => (playback, scene) switch
    {
        (Egami.Metafiles.MetafilePlayback.Gdi, "shapes" or "gradients") => 17,
        (Egami.Metafiles.MetafilePlayback.Gdi, _) => 6.5,
        (_, "gradients") => 6,
        (_, "paths" or "images") => 3.5,
        _ => 1.5,
    };

    private static Metafile Recorded(Metafile metafile, Action<Graphics> draw)
    {
        using (Graphics g = Graphics.FromImage(metafile))
        {
            draw(g);
        }

        return metafile;
    }

    private static Raster<Rgba8> OverWhite(Raster<Rgba8> r)
    {
        Raster<Rgba8> white = Raster.Create<Rgba8>(r.Width, r.Height);
        for (int y = 0; y < r.Height; y++)
        {
            ReadOnlySpan<Rgba8> p = r.Row(y);
            Span<Rgba8> q = white.Row(y);
            for (int x = 0; x < r.Width; x++)
            {
                int a = p[x].A;
                q[x] = new Rgba8((byte)(((p[x].R * a) + (255 * (255 - a)) + 127) / 255), (byte)(((p[x].G * a) + (255 * (255 - a)) + 127) / 255), (byte)(((p[x].B * a) + (255 * (255 - a)) + 127) / 255), 255);
            }
        }

        return white;
    }

    private static int I(byte[] b, int offset) => BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(offset));

    private static double Mean(Bitmap a, Bitmap b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Color p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                sum += Math.Abs(p.R - q.R) + Math.Abs(p.G - q.G) + Math.Abs(p.B - q.B) + Math.Abs(p.A - q.A);
            }
        }

        return sum / (a.Width * a.Height * 4);
    }

    private static double Mean(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<Rgba8> p = a.Row(y), q = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                sum += Math.Abs(p[x].R - q[x].R) + Math.Abs(p[x].G - q[x].G) + Math.Abs(p[x].B - q[x].B) + Math.Abs(p[x].A - q[x].A);
            }
        }

        return sum / (a.Width * a.Height * 4);
    }
}
