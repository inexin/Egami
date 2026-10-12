using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Gain maps through saves and operations (Phase 2.9.1 task 12, HDR Design questions 11 and 12): JPEG writes the map back,
/// geometry moves it with the picture, pixel operations and other formats drop it with a warning.
/// </summary>
public class GainMapRoundTripTests(ITestOutputHelper output)
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "gainmap");

    public static TheoryData<string> Files() => [.. Directory.GetFiles(Images, "*.jpg").Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void ASavedJpegKeepsTheMap(string name)
    {
        string path = Path.Combine(Images, name);
        using Raster sdr = Raster.Load(path);
        GainMap before = sdr.Metadata.GainMap!;
        byte[] saved = Save(sdr);

        using Raster again = Raster.Load(saved);
        GainMap after = Assert.IsType<GainMap>(again.Metadata.GainMap);
        Assert.Equal((before.Width, before.Height, before.Channels), (after.Width, after.Height, after.Channels));
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(before.Info.GainMapMin[c], after.Info.GainMapMin[c], 1e-6);
            Assert.Equal(before.Info.GainMapMax[c], after.Info.GainMapMax[c], 1e-6);
            Assert.Equal(before.Info.Gamma[c], after.Info.Gamma[c], 1e-6);
            Assert.Equal(before.Info.OffsetSdr[c], after.Info.OffsetSdr[c], 1e-6);
            Assert.Equal(before.Info.OffsetHdr[c], after.Info.OffsetHdr[c], 1e-6);
        }

        Assert.Equal(before.Info.HdrCapacityMax, after.Info.HdrCapacityMax, 1e-6);
        Assert.Equal(GainMapFormat.Iso21496, after.Info.Format);

        // The stored map image is copied, not encoded again.
        Assert.True(before.Pixels.AsSpan().SequenceEqual(after.Pixels));

        // Readers of Ultra HDR 1.0 find it too: the hdrgm XMP alone, with the ISO segments taken out, gives the same values.
        byte[] adobeOnly = WithoutIso(saved);
        using Raster adobe = Raster.Load(adobeOnly);
        Assert.Equal(GainMapFormat.AdobeXmp, adobe.Metadata.GainMap!.Info.Format);
        Assert.Equal(before.Info.GainMapMax.Red, adobe.Metadata.GainMap.Info.GainMapMax.Red, 1e-6);
    }

    [Theory]
    [InlineData("uhdr-default.jpg", 0.015)]
    [InlineData("uhdr-quarter-map.jpg", 0.07)]
    public void CropAndTurnMoveTheMapWithThePicture(string name, double tolerance)
    {
        // A map the image's size moves pixel for pixel. A smaller one is sampled again where the picture's pixels land, which
        // softens its edges by up to a map pixel; at a headroom of 4 that is a few percent.
        string path = Path.Combine(Images, name);
        Action<RasterOps> ops = o => o.Crop(37, 21, 150, 101).Rotate(90).FlipHorizontal();
        ReaderOptions hdrOptions = new() { Hdr = new HdrDecode { Headroom = 4 } };
        using Raster<RgbaF16> hdr = Raster.Load<RgbaF16>(path, options: hdrOptions);
        using Raster<RgbaF16> want = hdr.Derive(ops, TestContext.Current.CancellationToken);

        using Raster sdr = Raster.Load(path);
        using Raster moved = sdr.Derive(ops, TestContext.Current.CancellationToken);
        Assert.NotNull(moved.Metadata.GainMap);
        using Raster<RgbaF16> got = Raster.Load<RgbaF16>(Save(moved), options: hdrOptions);
        Assert.Equal((want.Width, want.Height), (got.Width, got.Height));
        double error = MeanRelativeError(want, got);
        output.WriteLine($"mean relative error {error:P2}");
        Assert.True(error < tolerance, $"mean relative error {error:P2}");
    }

    [Fact]
    public void AResizeKeepsTheMapAsItIs()
    {
        using Raster sdr = Raster.Load(Path.Combine(Images, "uhdr-quarter-map.jpg"));
        using Raster small = sdr.Derive(o => o.Resize(128, 96), TestContext.Current.CancellationToken);
        Assert.Same(sdr.Metadata.GainMap, small.Metadata.GainMap);
    }

    [Fact]
    public void APixelOperationDropsTheMapWithAWarning()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        using Raster sdr = Raster.Load(Path.Combine(Images, "uhdr-default.jpg"), context);
        using Raster brighter = sdr.Derive(o => o.Brightness(0.1f), TestContext.Current.CancellationToken);
        Assert.Null(brighter.Metadata.GainMap);
        Assert.Contains(seen, d => d.Id == "Operation.GainMapDropped");
        Assert.NotNull(sdr.Metadata.GainMap);
    }

    [Fact]
    public void OtherFormatsDropTheMapWithAWarning()
    {
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = new() { Diagnostics = seen.Add };
        using Raster sdr = Raster.Load(Path.Combine(Images, "uhdr-default.jpg"), context);
        using MemoryStream png = new();
        sdr.Save(png, "png");
        Assert.Contains(seen, d => d.Id == "Save.GainMapDropped");
    }

    [Fact]
    public void KeepGainMapFalseLeavesItOut()
    {
        using Raster sdr = Raster.Load(Path.Combine(Images, "uhdr-default.jpg"));
        using MemoryStream stream = new();
        sdr.Save(stream, JpegFormat.Info, new JpegWriterOptions { KeepGainMap = false });
        using Raster again = Raster.Load(stream.ToArray());
        Assert.Null(again.Metadata.GainMap);
        Assert.Null(((JpegDetails)again.Metadata.FormatDetails!).MultiPicture);
    }

    private static byte[] Save(Raster raster)
    {
        using MemoryStream stream = new();
        raster.Save(stream, JpegFormat.Info, new JpegWriterOptions { Quality = 100, Subsampling = ChromaSubsampling.Subsample444 });
        return stream.ToArray();
    }

    /// <summary>The file with every ISO 21496-1 APP2 segment, in both images, overwritten as an unknown APP15.</summary>
    private static byte[] WithoutIso(byte[] file)
    {
        byte[] copy = (byte[])file.Clone();
        ReadOnlySpan<byte> id = "urn:iso:std:iso:ts:21496:-1\0"u8;
        for (int at = copy.AsSpan().IndexOf(id); at >= 0;)
        {
            copy[at - 3] = 0xEF;
            int next = copy.AsSpan(at + 1).IndexOf(id);
            at = next < 0 ? -1 : at + 1 + next;
        }

        return copy;
    }

    private static double MeanRelativeError(Raster<RgbaF16> a, Raster<RgbaF16> b)
    {
        double sum = 0;
        long count = 0;
        for (int y = 0; y < a.Height; y++)
        {
            Span<RgbaF16> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                foreach ((float p, float q) in new[] { ((float)ra[x].R, (float)rb[x].R), ((float)ra[x].G, (float)rb[x].G), ((float)ra[x].B, (float)rb[x].B) })
                {
                    sum += Math.Abs(p - q) / Math.Max(Math.Max(p, q), 0.05);
                    count++;
                }
            }
        }

        return sum / count;
    }
}
