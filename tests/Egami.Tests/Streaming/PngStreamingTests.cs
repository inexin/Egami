using Egami.Formats;
using Egami.Pixels;
using Egami.Tests.Formats.Png;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>Phase 2.2 task 3: PNG and APNG stream, holding only what WorkingBytes says.</summary>
public class PngStreamingTests
{
    public static TheoryData<string> Files() => [.. StreamingCorpus.Files.Where(f => f.EndsWith(".png", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Files))]
    public void HeldBytesMatchWorkingBytes(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        RasterInfo info = Raster.Probe(data);
        Rect region = new(info.Width / 3, info.Height / 2, Math.Max(1, info.Width / 2), 3);
        foreach (ReaderOptions options in (ReaderOptions[])[ReaderOptions.Default, new ReaderOptions { Region = region }])
        {
            long working = 0;
            long band = 0;
            bool animated = false;
            MemoryPeak peak = PeakMemory.Measure(context =>
            {
                using RasterDecoder decoder = RasterDecoder.Open(data, context, options);
                Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
                working = decoder.Info.WorkingBytes;
                animated = decoder.Info.FrameCount > 1;
                using Raster<Rgba8> rows = Raster.Create<Rgba8>(decoder.Info.Width, 16, context);
                band = (long)rows.Width * 16 * 4;
                do
                {
                    while (decoder.ReadRows(rows) > 0)
                    {
                    }
                }
                while (decoder.NextFrame());
            });
            Assert.Equal(0, peak.LeftBytes);
            if (animated)
            {
                Assert.InRange(peak.PeakPixelBytes - band, 0, working);
            }
            else
            {
                Assert.Equal(working, peak.PeakPixelBytes - band);
            }
        }
    }

    [Fact]
    public void ARegionAtTheTopStopsInflatingAndStillReadsTheTrailingChunks()
    {
        // A text chunk after IDAT: the region read must skip the image data and still find it.
        PngBuilder builder = new(300, 300, Egami.Formats.Png.PngColorType.Rgb, 8) { IdatChunks = 5 };
        builder.Fill(new Random(3));
        builder.ChunksAfterImageData.Add(("tEXt", PngBuilder.Latin1("Comment", "after")));
        byte[] data = builder.Build();
        using Raster<Rgb8> expected = Raster.Load<Rgb8>(data);
        using RasterDecoder decoder = RasterDecoder.Open(data, options: new ReaderOptions { Region = new Rect(0, 0, 300, 2) });
        Assert.False(decoder.IsMetadataComplete);
        using Raster<Rgb8>? top = decoder.ReadFrame<Rgb8>();
        Assert.True(decoder.IsMetadataComplete);
        Assert.True(expected.Row(1).SequenceEqual(top!.Row(1)));
        Assert.Contains(decoder.Metadata.Text, t => t.Value == "after");
        Assert.Empty(decoder.Metadata.Warnings);
    }

    [Fact]
    public void ALargePngStreamsInAFewRows()
    {
        // 4,000 × 3,000 RGB: Load holds 36 MB; the decoder holds two scanlines.
        using Raster<Rgb8> image = Raster.Create(4000, 3000, new Rgb8(1, 2, 3));
        using MemoryStream stream = new();
        image.Save(stream, Egami.Formats.Png.PngFormat.Info);
        byte[] data = stream.ToArray();
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, context);
            using Raster<Rgb8> band = Raster.Create<Rgb8>(4000, 64, context);
            while (decoder.ReadRows(band) > 0)
            {
            }
        });
        Assert.InRange(peak.PeakPixelBytes, 1, (4000 * 64 * 3) + (2 * 12_001));
    }
}
