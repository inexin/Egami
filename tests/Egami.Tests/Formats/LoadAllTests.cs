using Egami.Formats;
using Egami.Formats.Png;
using Egami.Formats.Tiff;
using Egami.Pixels;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats;

/// <summary>Raster.LoadAll, RasterList, Raster.SaveAll and RasterInfo.ImageCount.</summary>
public class LoadAllTests
{
    public static TheoryData<string> SingleImageFiles() =>
    [
        "pngsuite/basn2c08.png", "jpeg/duck-q75.jpg", "gif/colors-16.gif", "webp/lossless-z6.webp", "bmp/duck-24.bmp", "qoi/duck-rgb.qoi",
        "pnm/duck-p6.ppm", "tga/duck-24.tga", "apng/pan-rgb.png",
    ];

    private static string ImagePath(string name) => Path.Combine(AppContext.BaseDirectory, "images", name);

    [Theory]
    [MemberData(nameof(SingleImageFiles))]
    public void SingleImageFormatsGiveTheirOneImage(string name)
    {
        string path = ImagePath(name);
        if (!File.Exists(path))
        {
            path = Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + Path.GetExtension(path)).Order(StringComparer.Ordinal).First();
        }

        byte[] data = File.ReadAllBytes(path);
        using Raster single = Raster.Load(data);
        using RasterList all = Raster.LoadAll(data);
        Assert.Single(all);
        Assert.Equal(TiffReference.Bytes(single), TiffReference.Bytes(all[0]));
        Assert.Equal(single.FrameCount, all[0].FrameCount);
        Assert.Equal(1, Raster.Probe(data).ImageCount);
    }

    [Fact]
    public void TiffPagesComeAtTheirOwnSizes()
    {
        byte[] data = TiffCorpus.Main.Read("pages3.tif");
        using RasterList pages = Raster.LoadAll(data);
        Assert.Equal(3, pages.Count);
        Assert.Equal([(64, 48), (64, 48), (160, 120)], pages.Select(p => (p.Width, p.Height)));
        Assert.Equal([Rgb8.Layout, Gray16.Layout, Rgb8.Layout], pages.Select(p => p.Layout));
        Assert.Equal(3, Raster.Probe(data).ImageCount);
        for (int i = 0; i < 3; i++)
        {
            using Raster one = Raster.Load(data, options: new TiffReaderOptions { Page = i });
            Assert.Equal(TiffReference.Bytes(one), TiffReference.Bytes(pages[i]));
            Assert.Equal(i, ((TiffDetails)pages[i].Metadata.FormatDetails!).PageIndex);
        }
    }

    [Theory]
    [InlineData("pages-reduced.tif", false, 2)]
    [InlineData("pages-reduced.tif", true, 3)]
    [InlineData("pyramid-subifd.tif", false, 1)]
    [InlineData("pyramid-subifd.tif", true, 3)]
    [InlineData("pages-mask.tif", true, 1)]
    public void ReducedPagesAndSubIfdsOnlyWhenAsked(string file, bool reduced, int count)
    {
        byte[] data = TiffCorpus.Main.Read(file);
        TiffReaderOptions options = new() { IncludeReducedPages = reduced };
        using RasterList pages = Raster.LoadAll(data, options: options);
        Assert.Equal(count, pages.Count);
        Assert.Equal(count, Raster.Probe(data, options: options).ImageCount);
        Assert.Equal(reduced && count > 1, pages.Any(p => ((TiffDetails)p.Metadata.FormatDetails!).IsReducedResolution));
    }

    [Fact]
    public void SubIfdPagesMatchLibtiff()
    {
        byte[] data = TiffCorpus.Main.Read("pyramid-subifd.tif");
        using RasterList pages = Raster.LoadAll(data, options: new TiffReaderOptions { IncludeReducedPages = true });
        for (int sub = 0; sub < 2; sub++)
        {
            System.Text.Json.JsonElement page = TiffCorpus.Main.Pages("pyramid-subifd.tif").First(p => p.GetProperty("sub").GetInt32() == sub);
            byte[] raw = TiffCorpus.Main.Reference("pyramid-subifd.tif", 0, "raw", sub)!;
            Assert.Equal(TiffReference.Expected(page, raw, Rgb8.Layout), TiffReference.Bytes(pages[sub + 1]));
        }
    }

    [Fact]
    public void LoadAllAsTypeConvertsEveryImage()
    {
        using RasterList pages = Raster.LoadAll<Rgba16>(TiffCorpus.Main.Read("pages3.tif"));
        Assert.All(pages, p => Assert.IsType<Raster<Rgba16>>(p));
    }

    [Fact]
    public void LimitsApplyToTheCountAndTheSum()
    {
        byte[] data = TiffCorpus.Main.Read("pages3.tif");
        EgamiContext fewFrames = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxFrames = 2 } };
        Assert.Throws<LimitExceededException>(() => Raster.LoadAll(data, fewFrames));

        // Each page fits alone (160 × 120 × 3 bytes is the largest), but not all three together.
        EgamiContext fewBytes = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 70_000 } };
        using (Raster page = Raster.Load(data, fewBytes, new TiffReaderOptions { Page = 2 }))
        {
            Assert.Equal(160, page.Width);
        }

        Assert.Throws<LimitExceededException>(() => Raster.LoadAll(data, fewBytes));
    }

    [Fact]
    public async Task NonSeekableStreamsAndAsyncWork()
    {
        byte[] data = TiffCorpus.Main.Read("pages3.tif");
        using RasterList fromPipe = Raster.LoadAll(new NonSeekableStream(data));
        Assert.Equal(3, fromPipe.Count);
        using RasterList fromAsync = await Raster.LoadAllAsync(new MemoryStream(data), cancellationToken: TestContext.Current.CancellationToken);
        Assert.Equal(3, fromAsync.Count);
        using CancellationTokenSource canceled = new();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Raster.LoadAllAsync(new MemoryStream(data), cancellationToken: canceled.Token));
    }

    [Fact]
    public void DetachedRastersOutliveTheList()
    {
        Raster first;
        using (RasterList pages = Raster.LoadAll(TiffCorpus.Main.Read("pages3.tif")))
        {
            first = pages.Detach(0);
            Assert.Throws<InvalidOperationException>(() => pages[0]);
        }

        using (first)
        {
            Assert.Equal(64, first.Width);
        }
    }

    [Fact]
    public void SaveAllOfOneRasterIsSave()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(7, 5, new Rgba8(10, 20, 30, 255));
        using MemoryStream saved = new();
        raster.Save(saved, PngFormat.Info);
        using MemoryStream all = new();
        Raster.SaveAll([raster], all, PngFormat.Info);
        Assert.Equal(saved.ToArray(), all.ToArray());
    }

    [Fact]
    public void SaveAllOfSeveralToASingleImageFormatThrows()
    {
        using Raster<Rgba8> a = Raster.Create<Rgba8>(4, 4);
        using Raster<Rgba8> b = Raster.Create<Rgba8>(8, 8);
        Assert.Throws<ArgumentException>(() => Raster.SaveAll([a, b], new MemoryStream(), PngFormat.Info));
        Assert.Throws<ArgumentException>(() => Raster.SaveAll([], new MemoryStream(), PngFormat.Info));
    }

    private sealed class NonSeekableStream(byte[] data) : MemoryStream(data)
    {
        public override bool CanSeek => false;

        public override long Position { get => base.Position; set => throw new NotSupportedException(); }

        public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();

        public override long Length => throw new NotSupportedException();
    }
}
