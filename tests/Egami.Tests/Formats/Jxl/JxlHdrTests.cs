using System.Text.Json;

using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;
using Egami.Tests.Formats.Jpeg;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 task 14: PQ and HLG kept as the file says, with intensity_target in HdrMetadata (djxl equality is
/// <see cref="JxlDecodeTests.CorpusDecodesLikeDjxl"/>), and jhgm gain maps read into 2.9.1's GainMap: the SDR base loads with
/// the map, and ReaderOptions.Hdr renders HDR within 2.9.1's tolerance of libultrahdr's rendition of the JPEG the file was made
/// from, at its three headrooms (design question 12).
/// </summary>
public class JxlHdrTests(ITestOutputHelper output)
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string GainMapImages = Path.Combine(AppContext.BaseDirectory, "images", "gainmap");

    public static TheoryData<string, double> Renditions()
    {
        TheoryData<string, double> data = [];
        foreach (string name in (string[])["uhdr-default", "uhdr-mono-map", "uhdr-quarter-map"])
        {
            foreach (JsonElement boost in GainMapTests.Manifest().GetProperty(name + ".jpg").GetProperty("boosts").EnumerateArray())
            {
                data.Add(name, boost.GetDouble());
            }
        }

        return data;
    }

    [Theory]
    [InlineData("duck-pq-d1.jxl", 4000)]
    [InlineData("duck-pq-lossless.jxl", 10000)]
    public void PqIsKeptWithItsIntensityTarget(string file, double nits)
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        Assert.True(raster.Metadata.GetColorProfile().IsEquivalentTo(ColorProfile.Rec2100Pq));
        Assert.Equal(nits, raster.Metadata.Hdr!.MaxContentLightLevel);
    }

    [Fact]
    public void HlgIsKeptWithItsIntensityTarget()
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, "duck-hlg-d1.jxl"));
        Assert.True(raster.Metadata.GetColorProfile().IsEquivalentTo(ColorProfile.Rec2100Hlg));
        Assert.Equal(1000, raster.Metadata.Hdr!.MaxContentLightLevel);
    }

    [Fact]
    public void SdrFilesHaveNoHdrMetadata()
    {
        using Raster raster = Raster.Load(Path.Combine(Corpus, "duck-d1.jxl"));
        Assert.Null(raster.Metadata.Hdr);
        Assert.Null(raster.Metadata.GainMap);
    }

    [Theory]
    [InlineData("uhdr-default")]
    [InlineData("uhdr-mono-map")]
    [InlineData("uhdr-quarter-map")]
    public void GainMapIsTheJpegsMap(string name)
    {
        using Raster jpeg = Raster.Load(Path.Combine(GainMapImages, name + ".jpg"));
        using Raster jxl = Raster.Load(Path.Combine(Corpus, $"gainmap-{name}.jxl"));
        GainMap want = jpeg.Metadata.GainMap!;
        GainMap map = Assert.IsType<GainMap>(jxl.Metadata.GainMap);
        Assert.Equal((want.Width, want.Height, want.Channels), (map.Width, map.Height, map.Channels));
        Assert.Equal(want.Info with { AlternateProfile = null }, map.Info with { AlternateProfile = null });
        Assert.Equal(PixelChannelType.Unorm8, jxl.Layout.ChannelType);
    }

    [Theory]
    [MemberData(nameof(Renditions))]
    public void HdrIsCloseToLibultrahdr(string name, double boost)
    {
        string path = Path.Combine(Corpus, $"gainmap-{name}.jxl");
        Assert.Equal(RgbaF16.Layout, Raster.Probe(path, options: new ReaderOptions { Hdr = new HdrDecode { Headroom = boost } }).Layout);
        using Raster raster = Raster.Load(path, options: new ReaderOptions { Hdr = new HdrDecode { Headroom = boost } });
        GainMapTests.AssertCloseToLibultrahdr(raster, name + ".jpg", boost, output.WriteLine);
    }

    [Fact]
    public void DamagedGainMapLoadsTheSdrImageWithAWarning()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "gainmap-uhdr-default.jxl"));
        int at = data.AsSpan().IndexOf("jhgm"u8);
        data[at + 4] = 7;
        using Raster raster = Raster.Load(data);
        Assert.Null(raster.Metadata.GainMap);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Jxl.GainMapDropped");
    }
}
