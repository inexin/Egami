using Egami.Formats;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>Phase 2.2 task 4: JPEG streams per MCU row, or from its coefficients when progressive, within WorkingBytes.</summary>
public class JpegStreamingTests
{
    public static TheoryData<string> Files() => [.. StreamingCorpus.Files.Where(f => f.EndsWith(".jpg", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Files))]
    public void HeldBytesStayWithinWorkingBytes(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        long working = 0;
        long band = 0;
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, context);
            Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
            working = decoder.Info.WorkingBytes;
            using Raster<Rgb8> rows = Raster.Create<Rgb8>(decoder.Info.Width, 8, context);
            band = (long)rows.Width * 8 * 3;
            while (decoder.ReadRows(rows) > 0)
            {
            }
        });
        Assert.Equal(0, peak.LeftBytes);
        Assert.InRange(peak.PeakPixelBytes - band, 0, working);
    }

    [Theory]
    [InlineData("jpeg/duck-restart-rows.jpg")]
    [InlineData("jpeg/duck-restart-blocks.jpg")]
    [InlineData("jpeg/duck-420.jpg")]
    public void EveryBandOfRowsSkippedOrNotEqualsLoad(string file)
    {
        // Regions starting at every MCU row: those after the first skip rows, at restart markers where the file has them.
        byte[] data = StreamingCorpus.Read(file);
        using Raster<Rgb8> full = Raster.Load<Rgb8>(data);
        for (int top = 0; top < full.Height; top += 5)
        {
            Rect region = new(7, top, full.Width - 20, Math.Min(9, full.Height - top));
            using Raster<Rgb8> part = Raster.Load<Rgb8>(data, options: new ReaderOptions { Region = region });
            for (int y = 0; y < region.Height; y++)
            {
                Assert.True(full.Row(top + y).Slice(7, region.Width).SequenceEqual(part.Row(y)), $"region at {top}, row {y}");
            }

            Assert.Equal(full.Metadata.Warnings, part.Metadata.Warnings);
        }
    }
}
