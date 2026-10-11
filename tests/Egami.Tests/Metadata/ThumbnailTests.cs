using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Metadata;

/// <summary>Phase 2.1.3 task 8: the IFD1 thumbnail as a raster, made from a raster, and dropped by the operations that turn the image.</summary>
public class ThumbnailTests
{
    [Fact]
    public void ReadThumbnailDecodesTheIfd1Jpeg()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        ExifData exif = raster.Metadata.Exif!;
        using Raster<Rgb8> thumbnail = exif.ReadThumbnail()!;
        using Raster<Rgb8> expected = Raster.Load<Rgb8>(exif.Thumbnail);
        Assert.Equal((160, 120), (thumbnail.Width, thumbnail.Height));
        Rgb8[] got = new Rgb8[160 * 120], want = new Rgb8[160 * 120];
        thumbnail.CopyPixelsTo(got);
        expected.CopyPixelsTo(want);
        Assert.Equal(want, got);

        Assert.Null(new ExifData().ReadThumbnail());
        Assert.Throws<LimitExceededException>(() => exif.ReadThumbnail(EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 100 } })?.Dispose());
    }

    [Fact]
    public void SetThumbnailMakesADcfThumbnail()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        ExifData exif = raster.Metadata.Exif!;
        Assert.True(exif.Thumbnail.IsEmpty);
        exif.SetThumbnail(raster);

        using MemoryStream saved = new();
        raster.Save(saved, JpegFormat.Info);
        using Raster again = Raster.Load(saved.ToArray());
        ExifData read = again.Metadata.Exif!;
        Assert.True(read.TryGet(new ExifKey(0x0103, ExifIfd.Thumbnail), out ExifEntry? compression));
        Assert.Equal(6, compression.AsInteger());
        using Raster<Rgb8> thumbnail = read.ReadThumbnail()!;
        Assert.Equal((120, 160), (thumbnail.Width, thumbnail.Height));   // upright, as the image now is

        // Smaller images are not enlarged; other sizes and qualities on request.
        using Raster tiny = Raster.Create<Rgb8>(40, 20);
        ExifData other = new();
        other.SetThumbnail(tiny);
        using (Raster<Rgb8> kept = other.ReadThumbnail()!)
        {
            Assert.Equal((40, 20), (kept.Width, kept.Height));
        }

        other.SetThumbnail(raster, maxSide: 64, quality: 50);
        using (Raster<Rgb8> small = other.ReadThumbnail()!)
        {
            Assert.Equal((48, 64), (small.Width, small.Height));
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => other.SetThumbnail(raster, maxSide: 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => other.SetThumbnail(raster, quality: 101));
    }

    public static TheoryData<string> Steps() => ["flip", "flip-vertical", "rotate90", "rotate180", "rotate270", "rotate-any", "crop", "resize"];

    [Theory]
    [MemberData(nameof(Steps))]
    public void TurnsAndSizeChangesDropTheThumbnail(string step)
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/subject-point.jpg"));
        raster.Metadata.Exif!.SetThumbnail(raster);
        raster.Apply(ops => Step(ops, step), TestContext.Current.CancellationToken);
        Assert.True(raster.Metadata.Exif.Thumbnail.IsEmpty);
    }

    [Theory]
    [InlineData("pad")]
    [InlineData("brightness")]
    public void OthersKeepIt(string step)
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/subject-point.jpg"));
        raster.Metadata.Exif!.SetThumbnail(raster);
        raster.Apply(ops => _ = step == "pad" ? ops.Pad(700, 500, new Rgba8(0, 0, 0, 255)) : ops.Brightness(1.1f), TestContext.Current.CancellationToken);
        Assert.False(raster.Metadata.Exif.Thumbnail.IsEmpty);
    }

    private static RasterOps Step(RasterOps ops, string step) => step switch
    {
        "flip" => ops.FlipHorizontal(),
        "flip-vertical" => ops.FlipVertical(),
        "rotate90" => ops.Rotate(90),
        "rotate180" => ops.Rotate(180),
        "rotate270" => ops.Rotate(270),
        "rotate-any" => ops.Rotate(17),
        "crop" => ops.Crop(new Rect(10, 10, 100, 100)),
        _ => ops.Resize(320, 240),
    };
}
