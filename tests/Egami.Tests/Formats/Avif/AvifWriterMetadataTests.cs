using Egami.Colors;
using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>What the AVIF writer keeps beside the pixels: EXIF, XMP, orientation, ICC profiles, HDR luminances and gain maps.</summary>
public class AvifWriterMetadataTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");

    [Theory]
    [InlineData(ImageOrientation.Rotate90)]
    [InlineData(ImageOrientation.FlipHorizontal)]
    [InlineData(ImageOrientation.Transpose)]
    [InlineData(ImageOrientation.Rotate180)]
    public void ExifXmpAndOrientationLoadBack(ImageOrientation orientation)
    {
        using Raster<Rgba8> source = AvifWriterTests.Pattern<Rgba8>(40, 24, (x, y) => new Rgba8((byte)(x * 6), (byte)(y * 9), 80, (byte)(255 - x)));
        source.Metadata.Exif = new ExifData { Orientation = orientation };
        source.Metadata.Exif.Set(ExifKey.Artist, "Egami");
        source.Metadata.Xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>"u8.ToArray();
        byte[] file = AvifWriterTests.Save(source, new AvifWriterOptions());
        using Raster loaded = Raster.Load(file);
        Assert.Equal(orientation, loaded.Metadata.Exif?.Orientation);
        Assert.True(loaded.Metadata.Exif!.TryGet(ExifKey.Artist, out _));
        Assert.Equal(source.Metadata.Xmp.ToArray(), loaded.Metadata.Xmp.ToArray());

        // The rotation and mirror apply to the color and alpha items alike.
        HeifFile heif = HeifFile.Parse(file, "AVIF");
        Assert.Equal(Transforms(heif, 1), Transforms(heif, 2));
        Assert.NotEqual(0, Transforms(heif, 1));
    }

    private static int Transforms(HeifFile heif, uint item) =>
        heif.PropertiesOf(heif.Item(item)!).Count(p => p.Property.Type == FourCC.Of("irot") || p.Property.Type == FourCC.Of("imir"));

    [Fact]
    public void StripMetadataLeavesThemOut()
    {
        using Raster<Rgb8> source = AvifWriterTests.Pattern<Rgb8>(16, 16, (x, y) => new Rgb8((byte)x, (byte)y, 0));
        source.Metadata.Exif = new ExifData { Orientation = ImageOrientation.Rotate90 };
        using Raster loaded = Raster.Load(AvifWriterTests.Save(source, new AvifWriterOptions { StripMetadata = true }));
        Assert.Null(loaded.Metadata.Exif);
    }

    [Fact]
    public void AnIccProfileIsWrittenOnlyWhenNeeded()
    {
        using Raster<Rgb8> source = AvifWriterTests.Pattern<Rgb8>(16, 16, (x, y) => new Rgb8((byte)(x * 16), (byte)(y * 16), 99));
        source.Metadata.IccProfile = ColorProfile.AdobeRgb.ToIcc();
        using (Raster loaded = Raster.Load(AvifWriterTests.Save(source, new AvifWriterOptions())))
        {
            Assert.Equal(source.Metadata.IccProfile.ToArray(), loaded.Metadata.IccProfile.ToArray());
        }

        // sRGB needs no profile: the nclx box says it.
        source.Metadata.IccProfile = ColorProfile.Srgb.ToIcc();
        HeifFile heif = HeifFile.Parse(AvifWriterTests.Save(source, new AvifWriterOptions()), "AVIF");
        Assert.DoesNotContain(heif.PropertiesOf(heif.Item(1)!), p => p.Property.Type == FourCC.Of("colr") && heif.Content(p.Property)[..4].SequenceEqual("prof"u8));
    }

    [Fact]
    public void HdrLuminancesLoadBack()
    {
        using Raster<Rgba16> source = AvifWriterTests.Pattern<Rgba16>(32, 16, (x, y) => new Rgba16((ushort)(x * 2000), (ushort)(y * 4000), 30000, 65535));
        source.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
        source.Metadata.Hdr = new HdrMetadata
        {
            MasteringPrimaries = ColorPrimaries.Rec2020,
            MasteringMaxLuminance = 1000,
            MasteringMinLuminance = 0.005,
            MaxContentLightLevel = 800,
            MaxFrameAverageLightLevel = 300,
        };
        using Raster loaded = Raster.Load(AvifWriterTests.Save(source, new AvifWriterOptions { Quality = 90 }));
        Assert.Equal((9, 16), (loaded.Metadata.CodePoints!.Value.Primaries, loaded.Metadata.CodePoints.Value.TransferCharacteristics));
        HdrMetadata hdr = Assert.IsType<HdrMetadata>(loaded.Metadata.Hdr);
        Assert.Equal((800.0, 300.0, 1000.0), (hdr.MaxContentLightLevel, hdr.MaxFrameAverageLightLevel, hdr.MasteringMaxLuminance));
        Assert.Equal(0.005, hdr.MasteringMinLuminance!.Value, 4);
        Assert.Equal(0.708, hdr.MasteringPrimaries!.Red.X, 4);
    }

    [Theory]
    [InlineData("gainmap-sdr-base.avif")]
    [InlineData("gainmap-hdr-base.avif")]
    public void GainMapsLoadBack(string name)
    {
        using Raster source = Raster.Load(Path.Combine(Corpus, name));
        GainMap original = source.Metadata.GainMap!;
        using Raster loaded = Raster.Load(AvifWriterTests.Save(source, new AvifWriterOptions { Quality = 90 }));
        GainMap map = Assert.IsType<GainMap>(loaded.Metadata.GainMap);
        Assert.DoesNotContain(loaded.Metadata.Warnings, w => w.Id == "Avif.GainMapDropped");
        Assert.Equal((original.Width, original.Height, original.Channels), (map.Width, map.Height, map.Channels));
        Assert.Equal(original.Info.BaseRenditionIsHdr, map.Info.BaseRenditionIsHdr);
        Assert.Equal(original.Info.HdrCapacityMin, map.Info.HdrCapacityMin, 4);
        Assert.Equal(original.Info.HdrCapacityMax, map.Info.HdrCapacityMax, 4);
        Assert.Equal(original.Info.GainMapMax[0], map.Info.GainMapMax[0], 4);
    }
}
