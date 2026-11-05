using System.Text;

using Egami.Formats.Extended.Fits;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Fits;

/// <summary>FITS writing: BITPIX from the pixel type, RGB cubes, the rows bottom-up, and the header cards kept.</summary>
public sealed class FitsWriterTests
{
    private static readonly FitsReaderOptions Raw = new() { Stretch = FitsStretch.None };

    [Fact]
    public void WritesAStandardPrimaryHeader()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(5, 3);
        raster.Row(0)[0] = new Gray8(200);
        byte[] file = TestImages.Save(raster, "FITS");
        Assert.Equal(0, file.Length % 2880);
        string header = Encoding.ASCII.GetString(file, 0, 2880);
        Assert.StartsWith("SIMPLE  =                    T", header, StringComparison.Ordinal);
        Assert.Equal("BITPIX  =                    8", header.Substring(80, 30));
        Assert.Equal("NAXIS1  =                    5", header.Substring(240, 30));
        Assert.Contains("END     ", header, StringComparison.Ordinal);

        // The picture's top row is the file's last.
        Assert.Equal(200, file[2880 + (2 * 5)]);
    }

    public static TheoryData<int?, string> Depths() => new()
    {
        { null, "Gray8" },
        { null, "Gray16" },
        { null, "GrayF32" },
        { null, "Rgb8" },
        { null, "Rgb16" },
        { null, "RgbF32" },
        { 16, "Gray8" },
        { -32, "Rgb16" },
        { 8, "Rgb16" },
    };

    [Theory]
    [MemberData(nameof(Depths))]
    public void RoundTripsEveryDepth(int? bitpix, string type)
    {
        using Raster<RgbF32> source = Raster.Create<RgbF32>(7, 4);
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 7; x++)
            {
                source.Row(y)[x] = new RgbF32(x / 6f, y / 3f, (x + y) / 9f);
            }
        }

        using Raster raster = type switch
        {
            "Gray8" => source.As<Gray8>(),
            "Gray16" => source.As<Gray16>(),
            "GrayF32" => source.As<GrayF32>(),
            "Rgb8" => source.As<Rgb8>(),
            "Rgb16" => source.As<Rgb16>(),
            _ => source.As<RgbF32>(),
        };
        byte[] file = TestImages.Save(raster, "FITS", new FitsWriterOptions { Bitpix = bitpix });
        FitsFile fits = FitsFile.Load(new MemoryStream(file));
        FitsHdu hdu = Assert.Single(fits.Hdus);
        int expected = bitpix ?? (type.EndsWith("F32", StringComparison.Ordinal) ? -32 : type.EndsWith("16", StringComparison.Ordinal) ? 16 : 8);
        Assert.Equal(expected, hdu.Bitpix);
        bool gray = type.StartsWith("Gray", StringComparison.Ordinal);
        Assert.Equal(gray ? [7L, 4] : [7L, 4, 3], hdu.Axes);

        // Every plane reads back as the pixel's value in the stored depth.
        using RasterList pages = Raster.LoadAll(file, TestImages.Context, Raw);
        Assert.Equal(gray ? 1 : 3, pages.Count);
        float max = expected switch { 8 => 255, 16 => 65535, _ => 1 };
        using Raster<RgbF32> wanted = raster.As<RgbF32>();
        using Raster<GrayF32> wantedGray = raster.As<GrayF32>();
        for (int p = 0; p < pages.Count; p++)
        {
            Raster<GrayF32> plane = (Raster<GrayF32>)pages[p];
            for (int y = 0; y < 4; y++)
            {
                for (int x = 0; x < 7; x++)
                {
                    RgbF32 w = wanted.Row(y)[x];
                    float v = gray ? wantedGray.Row(y)[x].L : p == 0 ? w.R : p == 1 ? w.G : w.B;
                    float tolerance = expected == -32 ? 1e-6f : 0.51f;
                    Assert.InRange(plane.Row(y)[x].L, (v * max) - tolerance, (v * max) + tolerance);
                }
            }
        }

        // RGB comes back whole when asked.
        if (!gray && expected != -32)
        {
            using Raster<Rgb16> back = Raster.Load<Rgb16>(file, TestImages.Context, new FitsReaderOptions { Rgb = true, Stretch = FitsStretch.MinMax });
            Assert.Equal(PixelChannelOrder.Rgb, back.Layout.ChannelOrder);
        }
    }

    [Fact]
    public void RoundTripsGraySixteenBitExactly()
    {
        using Raster<Gray16> raster = Raster.Create<Gray16>(6, 2);
        for (int x = 0; x < 6; x++)
        {
            raster.Row(0)[x] = new Gray16((ushort)(x * 13107));
            raster.Row(1)[x] = new Gray16((ushort)(65535 - (x * 1000)));
        }

        FitsFile fits = FitsFile.Load(new MemoryStream(TestImages.Save(raster, "FITS")));
        Assert.Equal(32768, fits.Hdus[0].Header.GetInteger("BZERO"));
        double[] values = fits.ReadValues(fits.Hdus[0]);
        Assert.Equal([.. Enumerable.Range(0, 6).Select(x => 65535d - (x * 1000)), .. Enumerable.Range(0, 6).Select(x => x * 13107d)], values);
    }

    [Fact]
    public void KeepsTheHeaderCards()
    {
        using Raster<GrayF32> raster = Raster.Create<GrayF32>(3, 2);
        raster.Metadata.FormatDetails = new FitsDetails
        {
            Header = new FitsHeader(
            [
                new FitsCard("SIMPLE", true),
                new FitsCard("BITPIX", 16L),
                new FitsCard("NAXIS1", 99L),
                new FitsCard("OBJECT", "M51 'Whirlpool'", "the target"),
                new FitsCard("EXPTIME", 300.0),
                new FitsCard("CRVAL1", 202.4696),
                new FitsCard("DATAMIN", 3L),
                new FitsCard("HISTORY", null, "flat fielded"),
                new FitsCard("ESO OBS NAME", "run 7"),
            ]),
            Stretch = FitsStretch.None,
        };
        string note = string.Concat(Enumerable.Repeat("a long note ", 12));
        byte[] file = TestImages.Save(raster, "FITS", new FitsWriterOptions { Keywords = [new FitsCard("NOTE", note), new FitsCard("observer", "Lars"), new FitsCard("BZERO", 5.0)] });
        FitsHeader header = FitsFile.Load(new MemoryStream(file)).Hdus[0].Header;
        Assert.Equal(-32, header.GetInteger("BITPIX"));
        Assert.Equal(3, header.GetInteger("NAXIS1"));
        Assert.Equal("M51 'Whirlpool'", header.GetString("OBJECT"));
        Assert.Equal("the target", header.Find("OBJECT")!.Comment);
        Assert.Equal(300.0, header.GetReal("EXPTIME"));
        Assert.Equal(202.4696, header.GetReal("CRVAL1"));
        Assert.Equal(3, header.GetInteger("DATAMIN"));
        Assert.Contains(header, c => c.Keyword == "HISTORY" && c.Comment == "flat fielded");
        Assert.Equal("run 7", header.GetString("ESO OBS NAME"));
        Assert.Equal(note.TrimEnd(), header.GetString("NOTE"));
        Assert.Equal("Lars", header.GetString("OBSERVER"));
        Assert.Null(header.Find("BZERO"));
        Assert.Single(header, c => c.Keyword == "SIMPLE");

        // A stretched raster drops the range keywords that no longer describe its values.
        raster.Metadata.FormatDetails = new FitsDetails { Header = new FitsHeader([new FitsCard("DATAMIN", 3L), new FitsCard("BUNIT", "ADU")]), Stretch = FitsStretch.MinMax };
        FitsHeader stretched = FitsFile.Load(new MemoryStream(TestImages.Save(raster, "FITS"))).Hdus[0].Header;
        Assert.False(stretched.Contains("DATAMIN"));
        Assert.False(stretched.Contains("BUNIT"));
        Assert.Throws<ArgumentOutOfRangeException>(() => TestImages.Save(raster, "FITS", new FitsWriterOptions { Bitpix = 32 }));
    }

    [Fact]
    public void ReportsWhatTheFormatLoses()
    {
        List<EgamiDiagnostic> seen = [];
        using Raster<Rgba16> translucent = Raster.Create<Rgba16>(4, 4, new EgamiContext { Diagnostics = seen.Add });
        translucent.Row(0)[0] = new Rgba16(1, 2, 3, 4);
        translucent.Save(new MemoryStream(), FitsFormat.Info);
        Assert.Equal(["Save.AlphaDropped"], seen.Select(d => d.Id));
        seen.Clear();
        translucent.Save(new MemoryStream(), FitsFormat.Info, new FitsWriterOptions { Bitpix = 8 });
        Assert.Equal(["Save.AlphaDropped", "Save.DepthReduced"], seen.Select(d => d.Id));
    }
}
