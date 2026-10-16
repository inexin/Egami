using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>Every page of the corpus decodes as libtiff decodes it: identical samples for lossless pages, within the gates otherwise.</summary>
public class TiffReaderTests
{
    public static TheoryData<string, int> Pages() => PagesOf(TiffCorpus.Main);

    public static TheoryData<string, int> PicsPages() => TiffCorpus.Pics is { } pics ? PagesOf(pics) : new() { { "(none)", 0 } };

    private static TheoryData<string, int> PagesOf(TiffCorpus corpus)
    {
        TheoryData<string, int> data = [];
        foreach (string file in corpus.Files)
        {
            if (corpus.Entry(file).TryGetProperty("error", out _))
            {
                continue;
            }

            int full = 0;
            foreach (JsonElement page in corpus.Pages(file))
            {
                if (page.GetProperty("sub").GetInt32() < 0 && (page.GetProperty("subfileType").GetInt32() & 5) == 0
                    && page.GetProperty("photometric").GetInt32() != 4)
                {
                    data.Add(file, full++);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pages))]
    public void PagesMatchLibtiff(string file, int pageNumber) => Check(TiffCorpus.Main, file, pageNumber);

    [Theory]
    [MemberData(nameof(PicsPages))]
    public void LibtiffSamplesMatchLibtiff(string file, int pageNumber)
    {
        if (TiffCorpus.Pics is null)
        {
            throw LocalCorpus.Skip("tiff");
        }

        Check(TiffCorpus.Pics, file, pageNumber);
    }

    private static void Check(TiffCorpus corpus, string file, int pageNumber)
    {
        JsonElement page = corpus.Pages(file).Where(p => p.GetProperty("sub").GetInt32() < 0 && (p.GetProperty("subfileType").GetInt32() & 5) == 0
            && p.GetProperty("photometric").GetInt32() != 4).ElementAt(pageNumber);
        int index = page.GetProperty("index").GetInt32();
        int compression = page.GetProperty("compression").GetInt32();
        if (compression is 50000)
        {
            NotSupportedException e = Assert.Throws<NotSupportedException>(() => Raster.Load(corpus.Read(file), options: new TiffReaderOptions { Page = pageNumber }));
            Assert.Contains("ZSTD", e.Message, StringComparison.Ordinal);
            return;
        }

        using Raster raster = Raster.Load(corpus.Read(file), options: new TiffReaderOptions { Page = pageNumber });
        Assert.Equal("TIFF", raster.Metadata.FormatName);
        Assert.Equal(page.GetProperty("width").GetInt32(), raster.Width);
        Assert.Equal(page.GetProperty("height").GetInt32(), raster.Height);
        byte[] actual = TiffReference.Bytes(raster);
        int photometric = page.GetProperty("photometric").GetInt32();
        bool lossy = compression is 6 or 7 || (compression == 50001 && !file.Contains("lossless", StringComparison.Ordinal));
        // tiffcp 4.6 cannot write a big-endian file with the floating-point predictor as Adobe's note has it, so the script
        // wrote this one; it holds the same samples as rgbf32.
        byte[]? raw = file == "rgbf32-zip-fp-be.tif" ? corpus.Reference("rgbf32.tif", 0, "raw") : corpus.Reference(file, index, "raw");
        if (file == "inks2.tif")
        {
            // Inks that are not CMYK: the first is read as gray, 0 ink as white.
            Assert.Equal(Gray8.Layout, raster.Layout);
            byte[] first = [.. Enumerable.Range(0, raw!.Length / 2).Select(i => (byte)(255 - raw[i * 2]))];
            Assert.Equal(first, actual);
            Assert.Contains(raster.Metadata.Warnings, w => w.Message.Contains("inks", StringComparison.Ordinal));
            return;
        }

        byte[]? expected = raw is null ? null : TiffReference.Expected(page, raw, raster.Layout);
        if (expected is not null)
        {
            double tolerance = lossy ? 1 : raster.Layout.ChannelType is PixelChannelType.Float32 ? 1e-6 : 0;
            double worst = TiffReference.MaxDifference(actual, expected, raster.Layout.ChannelType);
            Assert.True(worst <= tolerance, $"{file} page {pageNumber} ({raster.Layout}): off by {worst} from libtiff's samples.");
            return;
        }

        if (photometric is 8 or 9 or 10 && corpus == TiffCorpus.Main)
        {
            // Lab: back to the RGB the corpus made it from (through D50, as Photoshop does), within the 8-bit Lab steps. ImageMagick
            // 7.1.1 reads TIFF Lab as D65 and misreads ICCLab, and libtiff's RGBA interface uses its own approximation, so
            // neither is the reference here.
            using Raster<Rgba8> lab = Raster.Load<Rgba8>(corpus.Read(file));
            using Raster<Rgba8> source = Raster.Load<Rgba8>(corpus.Read("rgb8.tif"));
            double off = TiffReference.MaxDifference(TiffReference.Bytes(lab), TiffReference.Bytes(source), PixelChannelType.Unorm8);
            Assert.True(off <= (page.GetProperty("bits").GetInt32() == 8 ? 2 : 1), $"{file}: off by {off} levels from the source.");
            return;
        }

        // Color conversions (YCbCr, other inks): within a level of ImageMagick, or libtiff's RGBA where ImageMagick made none.
        // ImageMagick 7.1.1 misreads uncompressed subsampled YCbCr, and libtiff's RGBA interface has its own Lab conversion.
        byte[]? rgba = photometric == 6 ? corpus.Reference(file, index, "rgba") : corpus.Reference(file, index, "magick") ?? corpus.Reference(file, index, "rgba");
        Assert.True(rgba is not null, $"{file} page {pageNumber} has no reference to compare with.");
        using Raster<Rgba8> loaded = Raster.Load<Rgba8>(corpus.Read(file), options: new TiffReaderOptions { Page = pageNumber });
        byte[] mine = TiffReference.Bytes(loaded);
        // libtiff converts YCbCr with fixed-point tables, a few levels from the exact formula when ReferenceBlackWhite is not
        // the default; old-style JPEG is best effort.
        int limit = compression == 6 ? 4 : photometric == 6 && file.Contains("bt709", StringComparison.Ordinal) ? 3 : 1;
        int rows = TruncatedRows.TryGetValue(file, out int whole) ? whole : loaded.Height;
        int worstLevel = 0;
        for (int i = 0; i < rows * loaded.Width * 4; i++)
        {
            worstLevel = Math.Max(worstLevel, Math.Abs(mine[i] - rgba![i]));
        }

        Assert.True(worstLevel <= limit, $"{file} page {pageNumber}: off by {worstLevel} levels from the reference.");
    }

    /// <summary>Files whose data ends early, with the rows libtiff decodes; past them libtiff's RGBA interface leaves black.</summary>
    private static readonly Dictionary<string, int> TruncatedRows = [];

    [Theory]
    [InlineData("icclab8.tif", "cielab8.tif")]
    [InlineData("icclab16.tif", "cielab16.tif")]
    public void IccLabMatchesCieLab(string icc, string cie)
    {
        using Raster<Rgba16> a = Raster.Load<Rgba16>(TiffCorpus.Main.Read(icc));
        using Raster<Rgba16> b = Raster.Load<Rgba16>(TiffCorpus.Main.Read(cie));
        Assert.True(TiffReference.MaxDifference(TiffReference.Bytes(a), TiffReference.Bytes(b), PixelChannelType.Unorm16) <= 257);
    }

    [Fact]
    public void ProbeReadsTheHeaderOnly()
    {
        RasterInfo info = Raster.Probe(TiffCorpus.Main.Read("rgb16-lzw-pred.tif"));
        Assert.Equal(TiffFormat.Info, info.Format);
        Assert.Equal((64, 48), (info.Width, info.Height));
        Assert.Equal(Rgb16.Layout, info.Layout);
    }

    [Fact]
    public void BigTiffIsDetectedBeforeTga()
    {
        RasterInfo info = Raster.Probe(TiffCorpus.Main.Read("rgb8-bigtiff.tif"));
        Assert.Equal("TIFF", info.Format.Name);
    }
}
