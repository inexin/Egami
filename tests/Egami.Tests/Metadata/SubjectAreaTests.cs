using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Metadata;

/// <summary>
/// Phase 2.1.3 task 9: SubjectArea and SubjectLocation, in EXIF and their XMP copies, kept in step by every geometry operation,
/// against expectations computed by hand from the operations table (pixel indices, rounded to the nearest pixel).
/// </summary>
public class SubjectAreaTests
{
    private static readonly ExifKey Area = new(0x9214, ExifIfd.Exif);
    private static readonly ExifKey Location = new(0xA214, ExifIfd.Exif);

    // The phone photo is 640 × 480 with a rectangle centered at (320, 240), 200 × 120, and a location at (300, 200).
    public static TheoryData<string, long[]?, long[]?> PhoneSteps() => new()
    {
        { "crop 100 50 400 300", [220, 190, 200, 120], [200, 150] },
        { "crop 0 0 400 300", [310, 240, 180, 120], [300, 200] },
        { "crop 400 300 240 180", null, null },
        { "pad 840 680", [420, 340, 200, 120], [400, 300] },
        { "resize 320 240", [160, 120, 100, 60], [150, 100] },
        { "resize 1 1", [0, 0], [0, 0] },
        { "flip horizontal", [319, 240, 200, 120], [339, 200] },
        { "flip vertical", [320, 239, 200, 120], [300, 279] },
        { "rotate 90", [239, 320, 120, 200], [279, 300] },
        { "rotate 180", [319, 239, 200, 120], [339, 279] },
        { "rotate 270", [240, 319, 120, 200], [200, 339] },
        { "rotate 45", [396, 396, 226, 226], null },
        { "brightness", [320, 240, 200, 120], [300, 200] },
    };

    [Theory]
    [MemberData(nameof(PhoneSteps))]
    public void OperationsMoveTheSubject(string step, long[]? area, long[]? location)
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        raster.Apply(ops => Step(ops, step), TestContext.Current.CancellationToken);
        Assert.Equal(area, Values(raster.Metadata.Exif!, Area));
        if (step != "rotate 45")
        {
            Assert.Equal(location, Values(raster.Metadata.Exif!, Location));
        }

        // The XMP copy follows the EXIF value; a dropped shape is dropped there too.
        Assert.Equal(area, XmpValues(raster.Metadata));
    }

    [Fact]
    public void AutoOrientTurnsTheSubjectAndTheXmpCopies()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        using MemoryStream saved = new();
        raster.Save(saved, JpegFormat.Info);
        using Raster again = Raster.Load(saved.ToArray());
        Assert.Equal([239, 320, 120, 200], Values(again.Metadata.Exif!, Area)!);
        Assert.Equal([279, 300], Values(again.Metadata.Exif!, Location)!);
        XmpData xmp = again.Metadata.XmpData!;
        Assert.Equal(ImageOrientation.Normal, xmp.Orientation);
        Assert.Equal([239, 320, 120, 200], XmpValues(again.Metadata)!);
        Assert.Equal("480", xmp.GetText(new XmpName(XmpNamespaces.Exif, "PixelXDimension")));
        Assert.Equal("640", xmp.GetText(new XmpName(XmpNamespaces.Exif, "PixelYDimension")));
        Assert.Equal(480, again.Metadata.Exif!.TryGet(ExifKey.PixelXDimension, out ExifEntry? x) ? x.AsInteger() : 0);
    }

    [Theory]
    [InlineData("resize 320 240", new long[] { 250, 200, 30 })]
    [InlineData("crop 480 380 160 100", new long[] { 20, 20, 40 })]
    [InlineData("flip horizontal", new long[] { 139, 400, 60 })]
    public void CirclesScaleAndClip(string step, long[] expected)
    {
        // A circle at (500, 400), 60 across; the crop leaves 20 pixels on two sides, so it clips to 40.
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/subject-circle.jpg"));
        raster.Apply(ops => Step(ops, step), TestContext.Current.CancellationToken);
        Assert.Equal(expected, Values(raster.Metadata.Exif!, Area));
    }

    [Fact]
    public void PointsMoveOrLeave()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/subject-point.jpg"));
        raster.Apply(ops => ops.Crop(50, 25, 200, 100), TestContext.Current.CancellationToken);
        Assert.Equal([50, 25], Values(raster.Metadata.Exif!, Area)!);
        raster.Apply(ops => ops.Crop(100, 0, 100, 100), TestContext.Current.CancellationToken);
        Assert.Null(Values(raster.Metadata.Exif!, Area));
    }

    [Fact]
    public void ATagAbsentFromTheFileIsNeverAdded()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/xp-tags.jpg"));
        raster.Apply(ops => ops.Rotate(90).Crop(10, 10, 50, 50).Resize(20, 20), TestContext.Current.CancellationToken);
        Assert.Null(Values(raster.Metadata.Exif!, Area));
        Assert.Null(Values(raster.Metadata.Exif!, Location));
        Assert.True(raster.Metadata.Xmp.IsEmpty);
    }

    [Fact]
    public void TheTypeWidensWhenAValueNoLongerFits()
    {
        ExifData exif = new();
        exif.Set(Area, ExifType.Short, [0x10, 0x00, 0x20, 0x00]);   // (16, 32), little-endian SHORT
        long[]? moved = Egami.Operations.Geometry.SubjectUpkeep.Move([16, 32], Egami.Operations.Geometry.SubjectMap.Pad(new Point(70_000, 0), new Size(80_000, 100)));
        Assert.Equal([70_016, 32], moved!);
        using Raster raster = Raster.Create<Rgb8>(64, 64);
        raster.Metadata.Exif = exif;
        raster.Apply(ops => ops.Pad(64, 64), TestContext.Current.CancellationToken);
        Assert.True(exif.TryGet(Area, out ExifEntry? entry));
        Assert.Equal(ExifType.Short, entry.Type);
    }

    private static RasterOps Step(RasterOps ops, string step)
    {
        string[] p = step.Split(' ');
        int N(int i) => int.Parse(p[i], System.Globalization.CultureInfo.InvariantCulture);
        return p[0] switch
        {
            "crop" => ops.Crop(N(1), N(2), N(3), N(4)),
            "pad" => ops.Pad(N(1), N(2)),
            "resize" => ops.Resize(N(1), N(2)),
            "flip" => p[1] == "horizontal" ? ops.FlipHorizontal() : ops.FlipVertical(),
            "rotate" => ops.Rotate(N(1)),
            _ => ops.Brightness(1.1f),
        };
    }

    private static long[]? Values(ExifData exif, ExifKey key) =>
        exif.TryGet(key, out ExifEntry? entry) ? [.. Enumerable.Range(0, entry.Count).Select(i => entry.AsInteger(i))] : null;

    private static long[]? XmpValues(RasterMetadata metadata) =>
        metadata.XmpData?.TryGet(new XmpName(XmpNamespaces.Exif, "SubjectArea"), out object? value) == true
            ? [.. ((XmpArray)value!).Texts.Select(t => long.Parse(t, System.Globalization.CultureInfo.InvariantCulture))]
            : null;
}
