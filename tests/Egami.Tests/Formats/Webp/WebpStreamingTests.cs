using Egami.Formats;
using Egami.Formats.Webp;
using Egami.Pixels;
using Egami.Tests.Memory;
using Egami.Tests.Streaming;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// Phase 2.3.1 tasks 6 and 8: lossless and lossy WebP stream. Rows, regions and reductions equal Load's (the shared streaming tests run over the
/// corpus too); here, non-seekable streams, held memory, metadata after the image data, and a window smaller than the image.
/// </summary>
public class WebpStreamingTests
{
    public static TheoryData<string> Stills() => [.. WebpCorpus.Lossless.Concat(WebpCorpus.Lossy)];

    [Theory]
    [MemberData(nameof(Stills))]
    public void NonSeekableStreamsGiveLoadsPixels(string file)
    {
        byte[] data = WebpCorpus.Read(file);
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
        Rect?[] regions = expected.Width < 3 ? [null] : [null, new Rect(1, expected.Height / 3, expected.Width - 2, Math.Max(1, expected.Height / 2))];
        foreach (Rect? region in regions)
        {
            using RasterDecoder decoder = RasterDecoder.Open(new SimpleStreamingTests.NonSeekableStream(data), options: new ReaderOptions { Region = region });
            Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
            using Raster<Rgba8>? frame = decoder.ReadFrame<Rgba8>();
            Assert.NotNull(frame);
            using Raster<Rgba8> cropped = region is Rect r ? expected.Derive(ops => ops.Crop(r), TestContext.Current.CancellationToken) : expected.Copy();
            StreamingCorpus.Equal(cropped, 0, frame, $"{file} {region}");
            Assert.True(decoder.IsMetadataComplete);
            Assert.Equal(expected.Metadata.Warnings, decoder.Metadata.Warnings);
        }
    }

    [Theory]
    [MemberData(nameof(Stills))]
    public void HeldBytesStayWithinWorkingBytes(string file)
    {
        byte[] data = WebpCorpus.Read(file);
        long working = 0;
        long band = 0;
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, context);
            working = decoder.Info.WorkingBytes;
            using Raster<Rgba8> rows = Raster.Create<Rgba8>(decoder.Info.Width, 4, context);
            band = (long)rows.Width * 4 * 4;
            while (decoder.ReadRows(rows) > 0)
            {
            }
        });
        Assert.Equal(0, peak.LeftBytes);
        Assert.InRange(peak.PeakPixelBytes - band, 0, working);
    }

    [Fact]
    public void MetadataAfterTheImageArrivesAtTheEnd()
    {
        byte[] data = WebpCorpus.Read("meta-exif.webp");
        using RasterDecoder decoder = RasterDecoder.Open(data);
        Assert.Null(decoder.Metadata.Exif);
        using Raster<Rgba8>? frame = decoder.ReadFrame<Rgba8>();
        Assert.True(decoder.IsMetadataComplete);
        Assert.NotNull(decoder.Metadata.Exif);
    }

    [Fact]
    public void ATallImageSlidesItsWindow()
    {
        // 100 pixels wide, so a backward reference can reach about 10,500 rows back: 16,384 rows make the window slide.
        Random random = new(3);
        using Raster<Rgba8> tall = WebpTestImages.Make(100, 16_384, (x, y) => (y / 7 % 3 == 0) ? new Rgba8((byte)x, (byte)(y & 0xFF), 0, 255) : new Rgba8((byte)random.Next(4), 9, 9, 255));
        using MemoryStream file = new();
        tall.Save(file, WebpFormat.Info, new WebpWriterOptions { Lossless = true, Effort = 2 });
        using RasterDecoder decoder = RasterDecoder.Open(file.ToArray());
        using Raster<Rgba8>? back = decoder.ReadFrame<Rgba8>();
        Assert.NotNull(back);
        StreamingCorpus.Equal(tall, 0, back, "tall");
        Assert.True(decoder.Info.WorkingBytes < 100L * 16_384 * 4);
    }

    [Theory]
    [InlineData("lossless-z6.webp")]
    [InlineData("lossy-q90.webp")]
    [InlineData("lossy-alpha-m1-f2.webp")]
    public void IncrementalDecodingGivesRowsBeforeTheEnd(string file)
    {
        byte[] data = WebpCorpus.Read(file);
        using RasterDecoder decoder = RasterDecoder.OpenIncremental();
        decoder.Append(data.AsSpan(0, data.Length / 2));
        using Raster<Rgba8> rows = Raster.Create<Rgba8>(160, 8);
        int got = decoder.ReadRows(rows);
        Assert.True(got > 0, "no rows from half the file");
        decoder.Append(data.AsSpan(data.Length / 2));
        decoder.EndOfInput();
        int total = got;
        while ((got = decoder.ReadRows(rows)) > 0)
        {
            total += got;
        }

        Assert.Equal(120, total);
    }
}
