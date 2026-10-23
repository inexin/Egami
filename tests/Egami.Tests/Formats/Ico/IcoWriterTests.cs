using System.Buffers.Binary;

using Egami.Formats;
using Egami.Formats.Ico;
using Egami.Formats.Png;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Formats.Ico;

/// <summary>The ICO and CUR writer: exact round trips, sizes and resizing, encodings, hotspots, limits and determinism.</summary>
public class IcoWriterTests
{
    private static Raster<Rgba8> Source(int width, int height)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(SimpleCorpus.Images, "ops", "duck.png"));
        Raster<Rgba8> fitted = photo.Derive(ops => ops.Resize(width, height), TestContext.Current.CancellationToken);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = fitted.Row(y);
            for (int x = 0; x < width; x++)
            {
                // Transparent corners, a soft edge and colors under alpha 0 that a round trip must keep.
                int d = Math.Min(Math.Min(x, width - 1 - x), Math.Min(y, height - 1 - y));
                row[x].A = (byte)(d == 0 ? 0 : Math.Min(255, d * 60));
            }
        }

        return fitted;
    }

    private static byte[] Save(IEnumerable<Raster> rasters, FormatInfo format, WriterOptions? options = null)
    {
        using MemoryStream stream = new();
        Raster.SaveAll(rasters, stream, format, options);
        return stream.ToArray();
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(16, 16)]
    [InlineData(13, 7)]
    [InlineData(33, 33)]
    [InlineData(255, 3)]
    [InlineData(256, 256)]
    public void OneRasterRoundTripsExactly(int width, int height)
    {
        using Raster<Rgba8> source = Source(width, height);
        byte[] data = Save([source], IcoFormat.Info);
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict });
        // A wholly transparent BMP entry (the 1 × 1 case) is written with black colors, which Windows needs to draw nothing.
        Rgba8[] expected = IcoReaderTests.Pixels(source).ToArray();
        if (expected.All(p => p.A == 0) && !(width == 256 && height == 256))
        {
            Array.Clear(expected);
        }

        Assert.True(expected.SequenceEqual(IcoReaderTests.Pixels(loaded)));
        IcoDetails details = (IcoDetails)loaded.Metadata.FormatDetails!;
        Assert.Equal(width == 256 && height == 256 ? IcoEntryEncoding.Png : IcoEntryEncoding.Bmp, details.Encoding);
        Assert.Equal(IcoFormat.Info, Raster.Probe(data).Format);
    }

    [Theory]
    [InlineData(IcoEntryEncoding.Png)]
    [InlineData(IcoEntryEncoding.Bmp)]
    public void ForcedEncodingsRoundTripExactly(IcoEntryEncoding encoding)
    {
        using Raster<Rgba8> small = Source(24, 24);
        using Raster<Rgba8> large = Source(256, 256);
        byte[] data = Save([large, small], IcoFormat.Info, new IcoWriterOptions { Encoding = encoding });
        using RasterList loaded = Raster.LoadAll<Rgba8>(data);
        Assert.Equal([24, 256], loaded.Select(r => r.Width));
        Assert.All(loaded, r => Assert.Equal(encoding, ((IcoDetails)r.Metadata.FormatDetails!).Encoding));
        Assert.True(IcoReaderTests.Pixels(small).SequenceEqual(IcoReaderTests.Pixels((Raster<Rgba8>)loaded[0])));
        Assert.True(IcoReaderTests.Pixels(large).SequenceEqual(IcoReaderTests.Pixels((Raster<Rgba8>)loaded[1])));
    }

    [Fact]
    public void ARoundTripKeepsEachEntrysEncoding()
    {
        byte[] original = File.ReadAllBytes(Path.Combine(IcoReaderTests.Images, "set-unsorted.ico"));
        using RasterList entries = Raster.LoadAll(original);
        byte[] again = Save(entries.Where(e => ((IcoDetails)e.Metadata.FormatDetails!).Index != 2), IcoFormat.Info);
        using RasterList reloaded = Raster.LoadAll(again);
        Assert.Equal([IcoEntryEncoding.Bmp, IcoEntryEncoding.Bmp, IcoEntryEncoding.Png], reloaded.Select(r => ((IcoDetails)r.Metadata.FormatDetails!).Encoding));
    }

    [Fact]
    public void SizesResizeFromTheLargestRasterAndPadNonSquareOnes()
    {
        using Raster<Rgba8> source = Source(120, 80);
        IcoWriterOptions options = new() { Sizes = IcoWriterOptions.WindowsSizes, Filter = ResampleFilter.Lanczos3 };
        byte[] data = Save([source], IcoFormat.Info, options);
        using RasterList loaded = Raster.LoadAll<Rgba8>(data);
        Assert.Equal([16, 24, 32, 48, 64, 256], loaded.Select(r => r.Width));
        foreach (Raster<Rgba8> entry in loaded.Cast<Raster<Rgba8>>())
        {
            int size = entry.Width;
            int w = (int)Math.Round(120.0 * size / 120);
            int h = (int)Math.Round(80.0 * size / 120);
            using Raster<Rgba8> expected = source.Derive(ops => ops.Resize(w, h, ResampleFilter.Lanczos3).Pad(size, size), TestContext.Current.CancellationToken);
            Assert.True(IcoReaderTests.Pixels(expected).SequenceEqual(IcoReaderTests.Pixels(entry)), $"The {size} entry differs from Resize and Pad.");
        }
    }

    [Fact]
    public void AGivenRasterOfASizeIsUsedAsDrawn()
    {
        using Raster<Rgba8> large = Source(64, 64);
        using Raster<Rgba8> drawn16 = Source(16, 16);
        drawn16.Row(5)[5] = new Rgba8(1, 2, 3, 255);
        byte[] data = Save([large, drawn16], IcoFormat.Info, new IcoWriterOptions { Sizes = IcoWriterOptions.FaviconSizes });
        using RasterList loaded = Raster.LoadAll<Rgba8>(data);
        Assert.Equal([16, 32, 48], loaded.Select(r => r.Width));
        Assert.True(IcoReaderTests.Pixels(drawn16).SequenceEqual(IcoReaderTests.Pixels((Raster<Rgba8>)loaded[0])));
    }

    [Fact]
    public void ALargeRasterIsFittedTo256WithAWarning()
    {
        List<EgamiDiagnostic> warnings = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = warnings.Add };
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(SimpleCorpus.Images, "ops", "duck.png"), context);
        byte[] data = Save([photo], IcoFormat.Info);
        using Raster loaded = Raster.Load(data);
        Assert.Equal((256, 192), (loaded.Width, loaded.Height));
        Assert.Contains(warnings, w => w.Id == "Save.IcoFitted");
    }

    [Fact]
    public void BadSizesThrowBeforeAnyWork()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new IcoWriterOptions { Sizes = [16, 257] });
        Assert.Throws<ArgumentOutOfRangeException>(() => new IcoWriterOptions { Sizes = [0] });
        Assert.Throws<ArgumentException>(() => new IcoWriterOptions { Sizes = [16, 16] });
        Assert.Throws<ArgumentException>(() => new IcoWriterOptions { Sizes = [] });
        using Raster<Rgba8> a = Source(16, 16);
        using Raster<Rgba8> b = Source(16, 16);
        Assert.Throws<ArgumentException>(() => Save([a, b], IcoFormat.Info));
    }

    [Fact]
    public void CursorsScaleTheirHotspot()
    {
        using Raster<Rgba8> source = Source(64, 64);
        byte[] data = Save([source], CurFormat.Info, new IcoWriterOptions { Sizes = [32, 64], Hotspot = new Point(40, 63) });
        Assert.Equal(CurFormat.Info, Raster.Probe(data).Format);
        using RasterList loaded = Raster.LoadAll(data);
        Assert.Equal(new Point(20, 31), ((IcoDetails)loaded[0].Metadata.FormatDetails!).Hotspot);
        Assert.Equal(new Point(40, 63), ((IcoDetails)loaded[1].Metadata.FormatDetails!).Hotspot);

        byte[] again = Save([loaded[1]], CurFormat.Info);
        using Raster reloaded = Raster.Load(again);
        Assert.Equal(new Point(40, 63), ((IcoDetails)reloaded.Metadata.FormatDetails!).Hotspot);
    }

    [Fact]
    public void AWhollyTransparentBmpEntryHasBlackColorsSoWindowsDrawsNothing()
    {
        // Windows ignores an alpha channel that is 0 everywhere and draws the colors through the AND mask instead.
        using Raster<Rgba8> clear = Raster.Create<Rgba8>(8, 8, new Rgba8(200, 100, 50, 0));
        byte[] data = Save([clear], CurFormat.Info, new IcoWriterOptions { Encoding = IcoEntryEncoding.Bmp });
        int offset = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(6 + 12));
        Assert.All(data.AsSpan(offset + 40, 8 * 8 * 4).ToArray(), b => Assert.Equal(0, b));
        Assert.All(data.AsSpan(offset + 40 + (8 * 8 * 4)).ToArray(), b => Assert.True(b is 0xFF or 0));
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(data);
        Assert.All(Enumerable.Range(0, 8), y => Assert.All(loaded.Row(y).ToArray(), p => Assert.Equal(0, p.A)));

        // One visible pixel keeps every color as given.
        clear.Row(3)[4] = new Rgba8(1, 2, 3, 4);
        byte[] kept = Save([clear], CurFormat.Info, new IcoWriterOptions { Encoding = IcoEntryEncoding.Bmp });
        Assert.Equal(50, kept[offset + 40]);
    }

    [Fact]
    public void TheSameInputGivesTheSameBytes()
    {
        using Raster<Rgba8> source = Source(100, 100);
        IcoWriterOptions options = new() { Sizes = IcoWriterOptions.WindowsSizes };
        Assert.Equal(Save([source], IcoFormat.Info, options), Save([source], IcoFormat.Info, options));
    }

    [Fact]
    public void OtherPixelTypesAreWrittenAt8Bits()
    {
        using Raster<Rgba8> source = Source(20, 20);
        using Raster<Rgba16> wide = source.As<Rgba16>();
        using Raster<Rgb8> opaque = source.As<Rgb8>();
        using Raster<Rgba8> fromWide = Raster.Load<Rgba8>(Save([wide], IcoFormat.Info));
        Assert.True(IcoReaderTests.Pixels(source).SequenceEqual(IcoReaderTests.Pixels(fromWide)));
        using Raster<Rgba8> fromOpaque = Raster.Load<Rgba8>(Save([opaque], IcoFormat.Info));
        Assert.All(IcoReaderTests.Pixels(fromOpaque).ToArray(), p => Assert.Equal(255, p.A));
    }

    [Fact]
    public void ExifXmpAndIptcAreNotWrittenButIccIsInPngEntries()
    {
        using Raster<Rgba8> source = Source(256, 256);
        source.Metadata.Exif = new ExifData();
        source.Metadata.Exif.Set(ExifKey.Artist, "someone");
        source.Metadata.Xmp = "<x:xmpmeta xmlns:x='adobe:ns:meta/'/>"u8.ToArray();
        source.Metadata.IccProfile = Egami.Colors.ColorProfile.DisplayP3.ToIcc();
        byte[] data;
        using (MemoryStream stream = new())
        {
            source.Save(stream, IcoFormat.Info);
            data = stream.ToArray();
        }

        using Raster loaded = Raster.Load(data);
        Assert.Null(loaded.Metadata.Exif);
        Assert.True(loaded.Metadata.Xmp.IsEmpty);
        Assert.False(loaded.Metadata.IccProfile.IsEmpty);
    }

    [Fact]
    public void SaveAllToAPathPicksTheFormatByExtension()
    {
        string directory = Path.Combine(Path.GetTempPath(), "egami-ico-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using Raster<Rgba8> a = Source(16, 16);
            using Raster<Rgba8> b = Source(32, 32);
            Raster.SaveAll([a, b], Path.Combine(directory, "x.ico"));
            Raster.SaveAll([a, b], Path.Combine(directory, "x.cur"));
            Assert.Equal(IcoFormat.Info, Raster.Probe(Path.Combine(directory, "x.ico")).Format);
            Assert.Equal(CurFormat.Info, Raster.Probe(Path.Combine(directory, "x.cur")).Format);
            Assert.Equal(2, Raster.Probe(Path.Combine(directory, "x.cur")).ImageCount);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
