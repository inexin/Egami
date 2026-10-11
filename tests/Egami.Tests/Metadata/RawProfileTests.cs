using Egami.Formats.Png;
using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>
/// Phase 2.1.3 task 6: legacy PNG "Raw profile type" chunks read into Exif, Iptc and Xmp (and out of Text), the real chunks
/// winning, and IPTC written to PNG as a "Raw profile type iptc" chunk.
/// </summary>
public class RawProfileTests
{
    [Theory]
    [InlineData("synthetic/legacy-raw-exif.png")]
    [InlineData("synthetic/legacy-raw-app1.png")]
    public void LegacyExifChunksBecomeExif(string file)
    {
        using Raster png = Raster.Load(MetadataCorpus.Bytes(file));
        using Raster jpeg = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        Assert.Equal(jpeg.Metadata.Exif!.ToBytes().ToArray(), png.Metadata.Exif!.ToBytes().ToArray());
        Assert.DoesNotContain(png.Metadata.Text, t => t.Key.StartsWith("Raw profile type", StringComparison.Ordinal));
        Assert.Empty(png.Metadata.Warnings);
        Assert.Equal(ImageOrientation.Rotate90, png.Metadata.Exif.Orientation);
    }

    [Fact]
    public void ImageMagickPngsCarryTheSourcesBlocks()
    {
        using Raster iptcJpeg = Raster.Load(MetadataCorpus.Bytes("synthetic/iptc-utf8.jpg"));
        using Raster iptcPng = Raster.Load(MetadataCorpus.Bytes("synthetic/im-iptc.png"));
        Assert.Equal(iptcJpeg.Metadata.Iptc!.ToBytes().ToArray(), iptcPng.Metadata.Iptc!.ToBytes().ToArray());
        Assert.Contains(iptcPng.Metadata.Text, t => t.Key == "Raw profile type 8bim");   // other Photoshop resources stay text

        using Raster xmpJpeg = Raster.Load(MetadataCorpus.Bytes("synthetic/xmp-elements.jpg"));
        using Raster xmpPng = Raster.Load(MetadataCorpus.Bytes("synthetic/im-xmp.png"));
        Assert.Equal(xmpJpeg.Metadata.XmpData!.Title, xmpPng.Metadata.XmpData!.Title);
        Assert.Equal(xmpJpeg.Metadata.XmpData.Keywords, xmpPng.Metadata.XmpData.Keywords);

        using Raster exifJpeg = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        using Raster exifPng = Raster.Load(MetadataCorpus.Bytes("synthetic/im-exif.png"));
        Assert.Equal(exifJpeg.Metadata.Exif!.CameraModel, exifPng.Metadata.Exif!.CameraModel);
        Assert.Equal(exifJpeg.Metadata.XmpData!.Orientation, exifPng.Metadata.XmpData!.Orientation);
    }

    [Fact]
    public void TheRealChunkWins()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        ExifData other = new();
        other.Set(ExifKey.Model, "Legacy");
        raster.Metadata.Text.Add(new TextEntry("Raw profile type exif", RawProfiles.Encode("exif", other.ToBytes().Span)));
        using Raster again = Raster.Load(Save(raster));
        Assert.Equal("Corpus Phone", again.Metadata.Exif!.CameraModel);
        Assert.Contains(again.Metadata.Warnings, w => w.Contains("Raw profile type exif", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("\nexif\n      10\n0102")]
    [InlineData("\nexif\n      zz\n0102")]
    [InlineData("\nexif\n       2\n01xz")]
    [InlineData("no newlines")]
    public void MalformedChunksStayText(string body)
    {
        using Raster raster = Raster.Create<Egami.Pixels.Rgb8>(4, 4);
        raster.Metadata.Text.Add(new TextEntry("Raw profile type exif", body));
        using Raster again = Raster.Load(Save(raster));
        Assert.Null(again.Metadata.Exif);
        Assert.Contains(again.Metadata.Text, t => t.Key == "Raw profile type exif" && t.Value == body);
        Assert.Single(again.Metadata.Warnings);
    }

    [Fact]
    public void IptcIsWrittenAsARawProfile()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/iptc-utf8.jpg"));
        raster.Metadata.Iptc!.Add(IptcKey.Keywords, "png");
        byte[] png = Save(raster);
        Assert.True(Raster.Probe(png).HasMetadata);
        using Raster again = Raster.Load(png);
        Assert.Equal(["duck", "bird", "vann", "png"], again.Metadata.Iptc!.Keywords);
        Assert.DoesNotContain(again.Metadata.Text, t => t.Key == "Raw profile type iptc");

        // A second save writes the same chunk, not a second one from Text.
        using Raster third = Raster.Load(Save(again));
        Assert.Equal(again.Metadata.Iptc.ToBytes().ToArray(), third.Metadata.Iptc!.ToBytes().ToArray());
    }

    [Fact]
    public void TheDeclaredLengthIsCheckedBeforeAllocating()
    {
        Assert.Null(RawProfiles.Decode("\nexif\n999999999\n00", 1024, out string? problem));
        Assert.Contains("over the metadata limit", problem, StringComparison.Ordinal);
        Assert.Equal([0xAB, 0xCD], RawProfiles.Decode(RawProfiles.Encode("x", [0xAB, 0xCD]), 1024, out _));
    }

    private static byte[] Save(Raster raster)
    {
        using MemoryStream stream = new();
        raster.Save(stream, PngFormat.Info);
        return stream.ToArray();
    }
}
