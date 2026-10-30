using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Psd;
using Egami.Metadata;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Formats.Psd;

/// <summary>PSD layer records against psd-tools, metadata against exiftool, details, warnings, limits and damaged files.</summary>
public class PsdDetailsTests
{
    private static string File(string name) => Path.Combine(PsdReaderTests.Images, name);

    public static TheoryData<string> LayerFiles() =>
        [.. Directory.GetFiles(PsdReaderTests.References, "*.layers.json").Select(f => Path.GetFileName(f)![..^".layers.json".Length]).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(LayerFiles))]
    public void LayersMatchPsdTools(string name)
    {
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(PsdReaderTests.References, name + ".layers.json")));
        var expected = document.RootElement.EnumerateArray()
            .Select(e => (Name: e.GetProperty("name").GetString()!, Group: e.GetProperty("kind").GetString() == "group", Visible: e.GetProperty("visible").GetBoolean(),
                Opacity: e.GetProperty("opacity").GetInt32() / 255f, Clipped: e.GetProperty("clipping").GetBoolean(), Level: e.GetProperty("depth").GetInt32()))
            .Distinct()
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        using Raster raster = Raster.Load(File(name));
        PsdDetails details = (PsdDetails)raster.Metadata.FormatDetails!;
        var actual = details.Layers.Where(l => l.Kind != PsdLayerKind.GroupEnd)
            .Select(l => (l.Name, Group: l.Kind != PsdLayerKind.Layer, Visible: l.IsVisible, l.Opacity, Clipped: l.IsClipped, l.Level))
            .Distinct()
            .OrderBy(e => e.Name, StringComparer.Ordinal).ToList();
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void LayerRecordsCarryEverythingTheFileSays()
    {
        using Raster raster = Raster.Load(File("layers.psd"));
        IList<PsdLayer> layers = ((PsdDetails)raster.Metadata.FormatDetails!).Layers;
        Assert.Equal(9, layers.Count);
        Assert.Equal([PsdLayerKind.Layer, PsdLayerKind.GroupEnd, PsdLayerKind.Layer, PsdLayerKind.GroupEnd, PsdLayerKind.Layer, PsdLayerKind.Layer, PsdLayerKind.ClosedGroup, PsdLayerKind.OpenGroup, PsdLayerKind.Layer], layers.Select(l => l.Kind));
        PsdLayer inner = layers[2];
        Assert.Equal(("Inner", 128 / 255f, "mul ", (BlendMode?)BlendMode.Multiply, new Rect(3, 2, 6, 4)), (inner.Name, inner.Opacity, inner.BlendModeKey, inner.BlendMode, inner.Bounds));
        Assert.Equal("Dyp æøå 深", layers[4].Name);
        Assert.False(layers[4].IsVisible);
        Assert.True(layers[5].IsClipped);
        Assert.Equal(("pass", (BlendMode?)null), (layers[7].BlendModeKey, layers[7].BlendMode));
        Assert.Equal(BlendMode.Difference, layers[8].BlendMode);
    }

    [Fact]
    public void HundredsOfLayersStayWithinTheMetadataLimit()
    {
        using Raster raster = Raster.Load(File("many-layers.psd"));
        Assert.Equal(300, ((PsdDetails)raster.Metadata.FormatDetails!).Layers.Count);

        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxMetadataBytes = 4096 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(File("many-layers.psd"), tight));
        using Raster without = Raster.Load(File("many-layers.psd"), tight, new ReaderOptions { ReadMetadata = false });
        Assert.Empty(((PsdDetails)without.Metadata.FormatDetails!).Layers);
    }

    [Fact]
    public void MetadataMatchesExiftool()
    {
        using JsonDocument document = JsonDocument.Parse(System.IO.File.ReadAllBytes(Path.Combine(PsdReaderTests.References, "metadata.psd.exif.json")));
        JsonElement exif = document.RootElement[0];
        RasterInfo info = Raster.Probe(File("metadata.psd"));
        Assert.True(info.HasMetadata);
        Assert.Equal((ImageOrientation)exif.GetProperty("IFD0:Orientation").GetInt32(), info.Orientation);

        using Raster raster = Raster.Load(File("metadata.psd"));
        RasterMetadata metadata = raster.Metadata;
        Assert.Equal(new Resolution(exif.GetProperty("Photoshop:XResolution").GetDouble(), exif.GetProperty("Photoshop:YResolution").GetDouble(), ResolutionUnit.PerInch), metadata.Resolution);
        Assert.Equal(exif.GetProperty("IFD0:Orientation").GetInt32(), (int)metadata.Exif!.Orientation);
        Assert.Equal(exif.GetProperty("IPTC:ObjectName").GetString(), metadata.Iptc!.Get(IptcKey.ObjectName));
        Assert.Equal(exif.GetProperty("IPTC:Caption-Abstract").GetString(), metadata.Iptc.Get(IptcKey.Caption));
        Assert.Contains(exif.GetProperty("XMP-dc:Format").GetString()!, System.Text.Encoding.UTF8.GetString(metadata.Xmp.Span), StringComparison.Ordinal);
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, metadata.ColorSpace);
        byte[] icc = System.IO.File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "icc", "compact", "DisplayP3-v2-magic.icc"));
        Assert.True(metadata.IccProfile.Span.SequenceEqual(icc));
        PsdDetails details = (PsdDetails)metadata.FormatDetails!;
        Assert.Contains(details.OtherResources, r => r.Id == 1036);
        Assert.Contains(details.OtherResources, r => r.Id == 9999 && r.Name == "Mine" && r.Data.Span.SequenceEqual("private"u8));
        Assert.DoesNotContain(details.OtherResources, r => r.Id is 1039 or 1058 or 1060 or 1028 or 1005 or 1057);
    }

    [Fact]
    public void DetailsDescribeTheFile()
    {
        using Raster psb = Raster.Load(File("gray16-alpha.psb"));
        PsdDetails details = (PsdDetails)psb.Metadata.FormatDetails!;
        Assert.Equal((2, PsdColorMode.Grayscale, 16, 2, PsdCompression.PackBits, true), (details.Version, details.ColorMode, details.BitsPerChannel, details.ChannelCount, details.Compression, details.HasRealComposite));
        Assert.Equal(PsdFormat.Info, Raster.Probe(File("gray16-alpha.psb")).Format);

        using Raster extra = Raster.Load(File("rgb8-extra.psd"));
        Assert.Equal(["Spot mask"], ((PsdDetails)extra.Metadata.FormatDetails!).ExtraChannels);
        Assert.IsType<Raster<Rgb8>>(extra);

        using Raster duotone = Raster.Load(File("duotone8.psd"));
        Assert.Equal(32, ((PsdDetails)duotone.Metadata.FormatDetails!).ColorModeData.Length);

        using Raster lab = Raster.Load(File("lab8.psd"));
        Assert.Equal(ColorSpaceHint.Srgb, lab.Metadata.ColorSpace);
        using Raster linear = Raster.Load(File("rgb32-raw.psd"));
        Assert.Equal(ColorSpaceHint.Linear, linear.Metadata.ColorSpace);
    }

    [Fact]
    public void WarningsSayWhatWasLeftOut()
    {
        using Raster placeholder = Raster.Load(File("no-composite.psd"), EgamiContext.Default, new PsdReaderOptions { Composite = PsdComposite.Stored });
        Assert.False(((PsdDetails)placeholder.Metadata.FormatDetails!).HasRealComposite);
        Assert.Contains(placeholder.Metadata.Warnings, w => w.Id == "Psd.NoComposite");

        using Raster multichannel = Raster.Load(File("multichannel8.psd"));
        Assert.Contains(multichannel.Metadata.Warnings, w => w.Id == "Psd.ChannelsDropped");
    }

    [Fact]
    public void ProbeAgreesWithLoad()
    {
        foreach (string file in Directory.GetFiles(PsdReaderTests.Images))
        {
            RasterInfo info = Raster.Probe(file);
            using Raster raster = Raster.Load(file);
            Assert.Equal((raster.Width, raster.Height, raster.Layout), (info.Width, info.Height, info.Layout));
            Assert.Equal(1, info.ImageCount);
            using RasterList all = Raster.LoadAll(file);
            Assert.Single(all);
        }
    }

    [Fact]
    public void AStreamThatCannotSeekGivesTheSamePixels()
    {
        foreach (string file in Directory.GetFiles(PsdReaderTests.Images))
        {
            byte[] data = System.IO.File.ReadAllBytes(file);
            using Raster<Rgba16> seekable = Raster.Load<Rgba16>(data);
            using Raster<Rgba16> forward = Raster.Load<Rgba16>(new PsdForwardOnlyStream(data));
            using FileStream stream = System.IO.File.OpenRead(file);
            using Raster<Rgba16> fromFile = Raster.Load<Rgba16>(stream);
            Assert.True(PsdReaderTests.Rows(seekable).AsSpan().SequenceEqual(PsdReaderTests.Rows(forward)), Path.GetFileName(file));
            Assert.True(PsdReaderTests.Rows(seekable).AsSpan().SequenceEqual(PsdReaderTests.Rows(fromFile)), Path.GetFileName(file));
        }
    }

    [Theory]
    [InlineData("signature.psd", typeof(UnsupportedFormatException), typeof(CorruptDataException))]
    [InlineData("version3.psd", typeof(UnsupportedFormatException), typeof(CorruptDataException))]
    [InlineData("no-channels.psd", typeof(CorruptDataException), typeof(CorruptDataException))]
    [InlineData("depth7.psd", typeof(CorruptDataException), typeof(CorruptDataException))]
    [InlineData("mode5.psd", typeof(CorruptDataException), typeof(CorruptDataException))]
    [InlineData("resources-too-long.psd", typeof(CorruptDataException), typeof(CorruptDataException))]
    [InlineData("rle-counts.psd", typeof(CorruptDataException), typeof(CorruptDataException))]
    [InlineData("huge.psd", typeof(CorruptDataException), typeof(CorruptDataException))]
    public void DamagedFilesFail(string name, Type detected, Type named)
    {
        byte[] data = System.IO.File.ReadAllBytes(Path.Combine(PsdReaderTests.Images, "bad", name));
        Assert.IsType(detected, Record.Exception(() => Raster.Load(data)), exactMatch: false);
        Assert.IsType(named, Record.Exception(() => Raster.Load(data, PsdFormat.Info)), exactMatch: false);
    }

    [Fact]
    public void PsbSidesPastTheLimitsThrowBeforeAnyPixelMemory()
    {
        byte[] data = System.IO.File.ReadAllBytes(File("rgb8-rle.psb"));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(14), 200_000);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(18), 200_000);
        Assert.Throws<LimitExceededException>(() => Raster.Probe(data));
        Assert.Throws<LimitExceededException>(() => Raster.Load(data));
        EgamiContext wide = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxWidth = 300_000, MaxHeight = 300_000, MaxPixels = long.MaxValue, MaxTotalBytes = 1 << 20 } };
        Assert.Throws<LimitExceededException>(() => Raster.Load(data, wide));
    }

    [Fact]
    public void ATruncatedFileLoadsLenientlyWithAWarning()
    {
        byte[] data = System.IO.File.ReadAllBytes(Path.Combine(PsdReaderTests.Images, "bad", "truncated.psd"));
        using (Raster raster = Raster.Load(data))
        {
            Assert.Contains(raster.Metadata.Warnings, w => w.Id == "Psd.DataTruncated");
        }

        CorruptDataException e = Assert.Throws<CorruptDataException>(() => Raster.Load(data, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));
        Assert.True(e.IsTruncation);
    }
}

/// <summary>A read-only stream that cannot seek or tell its length.</summary>
internal sealed class PsdForwardOnlyStream(byte[] data) : MemoryStream(data, writable: false)
{
    public override bool CanSeek => false;

    public override long Length => throw new NotSupportedException();

    public override long Position { get => base.Position; set => throw new NotSupportedException(); }

    public override long Seek(long offset, SeekOrigin loc) => throw new NotSupportedException();
}
