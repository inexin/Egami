using Egami.Formats;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 task 13: groups and rows decode on several threads with the same output at every thread count, and a Region
/// decodes only the groups it touches with the same pixels as a crop of the whole image (design question 10).
/// </summary>
public class JxlThreadingTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");

    public static TheoryData<string> Files =>
    [
        "duck-d1.jxl", "egret-d1.jxl", "duck-d1-progressive.jxl", "egret-d2-epf3.jxl", "egret-d1-noise.jxl", "duck-alpha-d1.jxl",
        "duck-lossless-e7.jxl", "duck-gray-d1.jxl", "duck-p3-d1.jxl", "pan-anim-d1.jxl", "libjxl/splines.jxl",
    ];

    [Theory]
    [MemberData(nameof(Files))]
    public void OutputIsTheSameAtEveryThreadCount(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        using Raster one = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 1 });
        byte[] expected = TiffReference.Bytes(one);
        foreach (int threads in (int[])[2, 3, 8])
        {
            using Raster many = Raster.Load(data, EgamiContext.Default with { MaxParallelism = threads });
            Assert.Equal(expected, TiffReference.Bytes(many));
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RegionEqualsACropOfTheWhole(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        using Raster whole = Raster.Load(data);
        int w = whole.Width;
        int h = whole.Height;
        Rect[] regions =
        [
            new(0, 0, Math.Min(w, 37), Math.Min(h, 29)),
            new(w / 3, h / 4, Math.Max(1, w / 2), Math.Max(1, h / 3)),
            new(Math.Max(0, Math.Min(w - 20, 246)), Math.Max(0, Math.Min(h - 20, 250)), Math.Min(w, 20), Math.Min(h, 20)),
            new(w - 1, h - 1, 1, 1),
        ];
        foreach (Rect region in regions)
        {
            using Raster cropped = Raster.Load(data, options: new ReaderOptions { Region = region });
            using Raster expected = whole.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
            Assert.True(TiffReference.Bytes(expected).AsSpan().SequenceEqual(TiffReference.Bytes(cropped)), $"{file} {region}");
        }
    }

    [Fact]
    public void LargeConformanceImageIsTheSameOnOneThreadAndInRegions()
    {
        if (JxlReferenceTests.Cache() is not string cache)
        {
            return;
        }

        byte[] data = File.ReadAllBytes(Path.Combine(cache, "conformance", "testcases", "bike", "input.jxl"));
        using Raster all = Raster.Load(data);
        using Raster one = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 1 });
        Assert.Equal(TiffReference.Bytes(one), TiffReference.Bytes(all));
        Rect region = new(1000, 1300, 300, 200);
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add, DiagnosticLevel = DiagnosticLevel.Debug };
        using Raster cropped = Raster.Load(data, context, new ReaderOptions { Region = region });
        // 300×200 pixels plus the filter border touch 3×3 of the 8×10 groups.
        Assert.Contains(seen, d => d.Id == "Jxl.RegionGroups" && d.Message.Contains(" 9 of 80 ", StringComparison.Ordinal));
        using Raster expected = all.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(cropped));
    }
}
