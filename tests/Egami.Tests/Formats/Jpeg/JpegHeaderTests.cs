using System.Text;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

public class JpegHeaderTests
{
    private static readonly (int, int, int)[] Ycc420 = [(1, 2, 2), (2, 1, 1), (3, 1, 1)];

    [Fact]
    public void ProbeReadsSizeLayoutAndEstimate()
    {
        byte[] file = JpegBuilder.Baseline(4000, 3000, Ycc420).Huffman(0, 0).Scan(1, 2, 3);
        RasterInfo info = Raster.Probe(file);
        Assert.Equal(JpegFormat.Info, info.Format);
        Assert.Equal((4000, 3000), (info.Width, info.Height));
        Assert.Equal(Rgb8.Layout, info.Layout);
        Assert.Equal(1, info.FrameCount);
        Assert.Equal(4000L * 3000 * 3, info.EstimatedBytes);
        Assert.Equal(ImageOrientation.Normal, info.Orientation);

        RasterInfo gray = Raster.Probe(JpegBuilder.Baseline(17, 9, (1, 1, 1)).Scan(1));
        Assert.Equal(Gray8.Layout, gray.Layout);
        Assert.Equal(17L * 9, gray.EstimatedBytes);
    }

    [Fact]
    public void ProgressiveEstimateCountsEveryCoefficient()
    {
        byte[] file = new JpegBuilder().Quant(0, 90).Frame(0xC2, 8, 100, 50, Ycc420).Scan(1);
        RasterInfo info = Raster.Probe(file);

        // 4:2:0 MCUs are 16×16: 7 × 4 of them. Luma has 2×2 blocks per MCU, each chroma 1, at 64 coefficients of 2 bytes.
        long coefficients = 7 * 4 * (4 + 1 + 1) * 64 * 2;
        Assert.Equal((100L * 50 * 3) + coefficients, info.EstimatedBytes);
    }

    [Fact]
    public void TargetSizePicksTheScaleForTheEstimate()
    {
        byte[] file = JpegBuilder.Baseline(4000, 3000, Ycc420).Scan(1, 2, 3);
        RasterInfo eighth = Raster.Probe(file, options: new ReaderOptions { TargetSize = new Size(500, 375) });
        RasterInfo quarter = Raster.Probe(file, options: new ReaderOptions { TargetSize = new Size(501, 375) });
        Assert.Equal(500L * 375 * 3, eighth.EstimatedBytes);
        Assert.Equal(1000L * 750 * 3, quarter.EstimatedBytes);

        // The reported size is the stored one; the scale only changes the decode.
        Assert.Equal(4000, eighth.Width);
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(6, false)]
    [InlineData(8, true)]
    [InlineData(3, true)]
    public void OrientationComesFromExif(int orientation, bool littleEndian)
    {
        byte[] file = JpegBuilder.Baseline(30, 20, Ycc420).ExifOrientation(orientation, littleEndian).Scan(1, 2, 3);
        Assert.Equal((ImageOrientation)orientation, Raster.Probe(file).Orientation);
    }

    [Fact]
    public void BadOrientationsReadAsNormal()
    {
        Assert.Equal(ImageOrientation.Normal, ExifOrientation.Read([]));
        Assert.Equal(ImageOrientation.Normal, ExifOrientation.Read("MM\0\x2A\0\0\0\x08"u8));
        Assert.Equal(ImageOrientation.Normal, ExifOrientation.Read("XX\0\x2A\0\0\0\x08\0\0"u8));
        byte[] file = JpegBuilder.Baseline(30, 20, Ycc420).ExifOrientation(9, false).Scan(1, 2, 3);
        Assert.Equal(ImageOrientation.Normal, Raster.Probe(file).Orientation);

        // The offset to IFD0 points past the end.
        byte[] far = [.. "MM\0\x2A"u8, 0x7F, 0, 0, 0];
        Assert.Equal(ImageOrientation.Normal, ExifOrientation.Read(far));
    }

    [Theory]
    [InlineData(0xC3, 8, "Lossless")]
    [InlineData(0xC5, 8, "Hierarchical")]
    [InlineData(0xC9, 8, "Arithmetic")]
    [InlineData(0xCA, 8, "Arithmetic")]
    [InlineData(0xC1, 12, "12-bit")]
    [InlineData(0xC2, 12, "12-bit")]
    public void UnsupportedModesThrow(byte marker, int precision, string message)
    {
        byte[] file = new JpegBuilder().Frame(marker, precision, 8, 8, (1, 1, 1)).Scan(1);
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(file));
        Assert.Contains(message, e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void OtherUnsupportedFilesThrow()
    {
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(new JpegBuilder().Segment(0xCC, [0, 0x11]).Frame(0xC0, 8, 8, 8, (1, 1, 1)).Scan(1)));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(new JpegBuilder().Frame(0xC0, 8, 8, 0, (1, 1, 1)).Scan(1)));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(new JpegBuilder().Frame(0xC0, 8, 8, 8, (1, 1, 1), (2, 1, 1)).Scan(1)));
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(new JpegBuilder().Segment(0xDE, [0, 0, 0, 8, 0, 8, 1]).Scan(1)));
    }

    [Fact]
    public void DamagedHeadersThrowCorruptData()
    {
        Assert.Throws<CorruptDataException>(() => Raster.Probe(new JpegBuilder().Quant(0, 50).Scan(1)));
        Assert.Throws<CorruptDataException>(() => Raster.Probe(new JpegBuilder().Raw(0xFF, 0xD9).Scan(1)));
        Assert.Throws<CorruptDataException>(() => Raster.Probe(new JpegBuilder().Frame(0xC0, 8, 8, 8, (1, 1, 1), (1, 1, 1), (3, 1, 1)).Scan(1)));
        Assert.Throws<CorruptDataException>(() => Raster.Probe(new JpegBuilder().Frame(0xC0, 8, 8, 8, (1, 5, 1)).Scan(1)));
        Assert.Throws<CorruptDataException>(() => Raster.Probe(JpegBuilder.Baseline(8, 8, (1, 1, 1)).Scan(7)));
        byte[] file = JpegBuilder.Baseline(8, 8, (1, 1, 1)).Scan(1);
        CorruptDataException truncated = Assert.Throws<CorruptDataException>(() => Raster.Probe(file.AsSpan(0, 30).ToArray()));
        Assert.True(truncated.IsTruncation);
    }

    [Fact]
    public void LenientReadsSkipGarbageBetweenSegmentsAndStrictThrows()
    {
        byte[] file = JpegBuilder.Baseline(8, 8, (1, 1, 1)).Raw(0x00, 0x12, 0xFF, 0xFF).Raw(0xFF).Scan(1);
        Assert.Equal(8, Raster.Probe(file).Width);
        Assert.Throws<CorruptDataException>(() => Raster.Probe(file, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));

        // Fill bytes before a marker are allowed either way.
        byte[] filled = JpegBuilder.Baseline(8, 8, (1, 1, 1)).Raw(0xFF, 0xFF).Scan(1);
        Assert.Equal(8, Raster.Probe(filled, options: new ReaderOptions { Recovery = RecoveryMode.Strict }).Width);
    }

    [Fact]
    public void MetadataSegmentsAreRead()
    {
        byte[] icc = [.. Enumerable.Range(0, 300).Select(i => (byte)i)];
        byte[] file = JpegBuilder.Baseline(16, 16, Ycc420)
            .Jfif(1, 300, 300)
            .Icc(2, 2, icc[150..])
            .Icc(1, 2, icc[..150])
            .Xmp("<x:xmpmeta/>")
            .ExtendedXmp("0123456789ABCDEF0123456789ABCDEF", 10, 5, [5, 6, 7, 8, 9])
            .ExtendedXmp("0123456789ABCDEF0123456789ABCDEF", 10, 0, [0, 1, 2, 3, 4])
            .Comment(Encoding.UTF8.GetBytes("Grüße"))
            .Comment([0x47, 0x72, 0xFC, 0xDF, 0x65])
            .Scan(1, 2, 3);
        JpegHeader header = Read(file);
        Assert.Equal(new Resolution(300, 300, ResolutionUnit.PerInch), header.Metadata.Resolution);
        Assert.Equal(icc, header.Metadata.IccProfile.ToArray());
        Assert.Equal(ColorSpaceHint.EmbeddedIcc, header.Metadata.ColorSpace);
        Assert.Equal("<x:xmpmeta/>", Encoding.UTF8.GetString(header.Metadata.Xmp.Span));
        Assert.Equal([0, 1, 2, 3, 4, 5, 6, 7, 8, 9], header.Details.ExtendedXmp.ToArray());
        Assert.Equal(["Grüße", "Grüße"], header.Metadata.Text.Select(t => t.Value));
        Assert.All(header.Metadata.Text, t => Assert.Equal("Comment", t.Key));
        Assert.Empty(header.Metadata.Warnings);
        Assert.Same(header.Details, header.Metadata.FormatDetails);
        Assert.True(Raster.Probe(file).HasMetadata);
    }

    [Fact]
    public void IncompleteMetadataIsDroppedWithAWarning()
    {
        byte[] file = JpegBuilder.Baseline(16, 16, Ycc420)
            .Jfif(0, 1, 1)
            .Icc(1, 3, [1, 2, 3])
            .ExtendedXmp("0123456789ABCDEF0123456789ABCDEF", 10, 0, [0, 1, 2])
            .ExtendedXmp("0123456789ABCDEF0123456789ABCDEF", 2_000_000_000, 0, [0])
            .Scan(1, 2, 3);
        JpegHeader header = Read(file);
        Assert.Null(header.Metadata.Resolution);
        Assert.True(header.Metadata.IccProfile.IsEmpty);
        Assert.True(header.Details.ExtendedXmp.IsEmpty);
        Assert.Equal(2, header.Metadata.Warnings.Count);

        // A packet claiming to be larger than the metadata limit is never allocated.
        JpegHeader huge = Read(JpegBuilder.Baseline(16, 16, Ycc420).ExtendedXmp("0123456789ABCDEF0123456789ABCDEF", 2_000_000_000, 0, [0]).Scan(1, 2, 3));
        Assert.Contains("over the metadata limit", huge.Metadata.Warnings.Single(), StringComparison.Ordinal);
    }

    [Fact]
    public void MetadataLimitsAndOptionsApply()
    {
        byte[] file = JpegBuilder.Baseline(16, 16, Ycc420).Xmp(new string('x', 5000)).Scan(1, 2, 3);
        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxMetadataBytes = 1000 } };
        Assert.Throws<LimitExceededException>(() => Raster.Probe(file, tight));
        JpegHeader skipped = Read(file, new ReaderOptions { ReadMetadata = false }, tight);
        Assert.True(skipped.Metadata.Xmp.IsEmpty);
    }

    [Theory]
    [InlineData(1, -1, JpegColorSpace.Gray)]
    [InlineData(3, -1, JpegColorSpace.YCbCr)]
    [InlineData(3, 1, JpegColorSpace.YCbCr)]
    [InlineData(3, 0, JpegColorSpace.Rgb)]
    [InlineData(4, -1, JpegColorSpace.Cmyk)]
    [InlineData(4, 0, JpegColorSpace.Cmyk)]
    [InlineData(4, 2, JpegColorSpace.Ycck)]
    public void ColorSpaceFollowsTheAdobeSegment(int components, int transform, JpegColorSpace expected)
    {
        (int, int, int)[] ids = [.. Enumerable.Range(1, components).Select(i => (i, 1, 1))];
        JpegBuilder builder = JpegBuilder.Baseline(8, 8, ids);
        if (transform >= 0)
        {
            builder.Adobe(transform);
        }

        JpegHeader header = Read(builder.Scan(1));
        Assert.Equal(expected, header.Details.ColorSpace);
        Assert.Equal(components == 1 ? Gray8.Layout : Rgb8.Layout, JpegSegmentReader.Layout(header));
    }

    [Fact]
    public void RgbComponentIdsWithoutJfifMeanRgb()
    {
        Assert.Equal(JpegColorSpace.Rgb, Read(JpegBuilder.Baseline(8, 8, ('R', 1, 1), ('G', 1, 1), ('B', 1, 1)).Scan('R')).Details.ColorSpace);
        Assert.Equal(JpegColorSpace.YCbCr, Read(JpegBuilder.Baseline(8, 8, ('R', 1, 1), ('G', 1, 1), ('B', 1, 1)).Jfif(0, 1, 1).Scan('R')).Details.ColorSpace);
    }

    [Fact]
    public void DetailsDescribeTheFrame()
    {
        foreach (int quality in new[] { 10, 50, 75, 90, 100 })
        {
            byte[] file = new JpegBuilder().Quant(0, quality).Frame(0xC2, 8, 64, 48, Ycc420).Segment(0xDD, [0, 4]).Scan(1);
            JpegHeader header = Read(file);
            Assert.Equal(quality, header.Details.EstimatedQuality);
            Assert.True(header.Details.IsProgressive);
            Assert.Equal(4, header.Details.RestartInterval);
            Assert.Equal([new JpegComponent(1, 2, 2), new JpegComponent(2, 1, 1), new JpegComponent(3, 1, 1)], header.Details.Components);
        }

        JpegDetails copy = (JpegDetails)Read(JpegBuilder.Baseline(8, 8, (1, 1, 1)).Scan(1)).Details.Copy();
        Assert.Single(copy.Components);
        Assert.Equal(1, copy.ScaleDenominator);
    }

    [Fact]
    public void TablesAndScanHeaderAreParsed()
    {
        byte[] file = JpegBuilder.Baseline(8, 8, Ycc420).Huffman(0, 1).Huffman(1, 3).Scan(1, 3);
        JpegHeader header = Read(file);
        Assert.NotNull(header.QuantTables[0]);
        Assert.NotNull(header.DcTables[1]);
        Assert.NotNull(header.AcTables[3]);
        Assert.Null(header.DcTables[0]);
        Assert.Equal([0, 2], header.FirstScan!.Components.Select(c => c.ComponentIndex));
        Assert.Equal((0, 63), (header.FirstScan.SpectralStart, header.FirstScan.SpectralEnd));
        Assert.Throws<CorruptDataException>(() => Read(JpegBuilder.Baseline(8, 8, Ycc420).Segment(0xC4, [0x20, 1]).Scan(1)));
        Assert.Throws<CorruptDataException>(() => Read(JpegBuilder.Baseline(8, 8, Ycc420).Segment(0xDB, [0x05, 1]).Scan(1)));
    }

    private static JpegHeader Read(byte[] file, ReaderOptions? options = null, EgamiContext? context = null)
    {
        using MemoryStream stream = new(file);
        using EgamiInput input = new(stream, context ?? EgamiContext.Default, TestContext.Current.CancellationToken);
        return JpegSegmentReader.ReadHeader(input, options ?? ReaderOptions.Default);
    }
}
