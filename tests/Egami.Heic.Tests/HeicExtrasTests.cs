using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Heic;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.4 tasks 11 and 12: gain maps (ISO 21496-1 tmap and Apple's HDRGainMap auxiliary image), the file's items with
/// <see cref="HeicImage.ReadItem(string, uint, ReaderOptions?, EgamiContext?)"/>, and image sequences.
/// </summary>
public class HeicExtrasTests
{
    private static readonly EgamiContext Context = new EgamiContext().WithHeic();

    private static string Corpus(string name) => Path.Combine(HevcTestFiles.Corpus, name);

    [Fact]
    public void AnAppleGainMapWithoutHeadroomIsDroppedWithAWarning()
    {
        using Raster raster = Raster.Load(Corpus("duck-hm-apple-gainmap-urn.heic"), Context);
        Assert.Null(raster.Metadata.GainMap);
        Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Heic.GainMapDropped");
        HeicDetails details = Assert.IsType<HeicDetails>(raster.Metadata.FormatDetails);
        Assert.Contains(details.Items, i => i.Role == HeicItemRole.GainMap && i.AuxiliaryType == "urn:com:apple:photo:2020:aux:hdrgainmap");
    }

    [Fact]
    public void ItemsAreListedWithTheirRoles()
    {
        using Raster depth = Raster.Load(Corpus("duck-hm-depth.heic"), Context);
        HeicDetails details = Assert.IsType<HeicDetails>(depth.Metadata.FormatDetails);
        Assert.Equal(HeicItemRole.Primary, details.Items.Single(i => i.Role == HeicItemRole.Primary).Role);
        HeicItem map = details.Items.Single(i => i.Role == HeicItemRole.Depth);
        Assert.True(map.IsImage);
        using Raster image = HeicImage.ReadItem(Corpus("duck-hm-depth.heic"), map.Id, context: Context);
        Assert.Equal((map.Size.Width, map.Size.Height), (image.Width, image.Height));

        using Raster thumbed = Raster.Load(Corpus("duck-hm-thumb.heic"), Context);
        HeicItem thumbnail = Assert.IsType<HeicDetails>(thumbed.Metadata.FormatDetails).Items.Single(i => i.Role == HeicItemRole.Thumbnail);
        using Raster small = HeicImage.ReadItem(Corpus("duck-hm-thumb.heic"), thumbnail.Id, context: Context);
        Assert.True(small.Width < thumbed.Width);
    }

    [Fact]
    public void ReadItemRefusesWhatIsNotAnImage()
    {
        string path = Corpus("duck-exif-xmp.heic");
        using Raster raster = Raster.Load(path, Context);
        HeicItem exif = Assert.IsType<HeicDetails>(raster.Metadata.FormatDetails).Items.First(i => i.Role == HeicItemRole.Metadata);
        Assert.Throws<ArgumentException>(() => HeicImage.ReadItem(path, exif.Id, context: Context));
        Assert.Throws<ArgumentException>(() => HeicImage.ReadItem(path, 999, context: Context));
    }

    [Fact]
    public void AnIntraSequenceLoadsEveryFrame()
    {
        string path = Corpus("pan-sequence-intra.heic");
        RasterInfo info = Raster.Probe(path, Context);
        Assert.True(info.FrameCount > 1);
        using Raster raster = Raster.Load(path, Context);
        Assert.Equal(info.FrameCount, raster.FrameCount);
        Assert.True(Assert.IsType<HeicDetails>(raster.Metadata.FormatDetails).IsSequence);
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Fact]
    public void ASequenceWithInterFramesLoadsEveryFrame()
    {
        string path = Corpus("pan-sequence.heic");
        RasterInfo info = Raster.Probe(path, Context);
        Assert.Equal(4, info.FrameCount);
        using Raster raster = Raster.Load(path, Context);
        Assert.Equal(4, raster.FrameCount);
        Assert.Equal((256, 192), (raster.Width, raster.Height));
        Assert.Empty(raster.Metadata.Warnings);
    }

    /// <summary>Phase 2.9.10 task 24: every frame of the corpus's sequences equals heif-dec's (its -S output's planes).</summary>
    [Theory]
    [InlineData("pan-sequence")]
    [InlineData("pan-sequence-intra")]
    public void SequenceFramesEqualHeifDec(string name)
    {
        string[] expected = [.. System.Text.Json.JsonDocument.Parse(File.ReadAllBytes(Path.Combine(HevcTestFiles.References, name + ".frames.json")))
            .RootElement.GetProperty("frames").EnumerateArray().Select(e => e.GetString()!)];
        byte[] bytes = File.ReadAllBytes(Corpus(name + ".heic"));
        Egami.Formats.Heif.HeifFile heif = Egami.Formats.Heif.HeifFile.Parse(bytes, "HEIC");
        HeicSequence sequence = HeicSequence.Find(heif)!;
        Hevc.HevcSequenceDecoder decoder = new(SafetyLimits.Default);
        List<(Hevc.HevcDpbPicture Picture, int Poc)> output = [];
        foreach (ReadOnlyMemory<byte> unit in sequence.Config.NalUnits)
        {
            decoder.Send(Hevc.HevcNal.Parse(unit.Span), output, TestContext.Current.CancellationToken);
        }

        foreach (Egami.Formats.Heif.HeifSample sample in sequence.Color.Samples)
        {
            foreach (ReadOnlyMemory<byte> unit in Egami.Nal.NalFraming.SplitLengthPrefixed(bytes.AsMemory((int)sample.Offset, sample.Size), sequence.Config.LengthSize))
            {
                decoder.Send(Hevc.HevcNal.Parse(unit.Span), output, TestContext.Current.CancellationToken);
            }
        }

        decoder.Flush(output, TestContext.Current.CancellationToken);
        Assert.Equal(expected, output.Select(o => HevcDecodeTests.Md5(o.Picture.Picture)));
    }

    [Fact]
    public void PhoneGainMapsLoad()
    {
        if (HevcTestFiles.Cache() is not string cache)
        {
            return;
        }

        // An iPhone photo with Apple's HDRGainMap (its headroom in the maker note), and one with an ISO 21496-1 tmap as well.
        foreach ((string file, GainMapFormat kind) in (ReadOnlySpan<(string, GainMapFormat)>)[
            ("kashiwa-sogo/IMG_0303.HEIC", GainMapFormat.Apple), ("img-1119_202603/IMG_1067.HEIC", GainMapFormat.Iso21496)])
        {
            string path = Path.Combine(cache, "phones", file);
            using Raster sdr = Raster.Load(path, Context, new ReaderOptions { Region = new Rect(0, 0, 64, 64) });
            GainMap map = sdr.Metadata.GainMap!;
            Assert.Equal(kind, map.Info.Format);
            Assert.True(map.Info.HdrCapacityMax > 1);

            using Raster hdr = Raster.Load(path, Context, new ReaderOptions { Hdr = new HdrDecode { Headroom = 2 } });
            Assert.IsType<Raster<RgbaF16>>(hdr);
        }
    }
}
