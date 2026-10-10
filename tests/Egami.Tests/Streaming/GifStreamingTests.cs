using Egami.Formats;
using Egami.Formats.Gif;
using Egami.Pixels;
using Egami.Tests.Memory;

namespace Egami.Tests.Streaming;

/// <summary>Phase 2.2 task 5: GIF frames come one at a time from the reused canvas, so held memory does not grow with them.</summary>
public class GifStreamingTests
{
    public static TheoryData<string> Files() => [.. StreamingCorpus.Files.Where(f => f.EndsWith(".gif", StringComparison.Ordinal))];

    [Theory]
    [MemberData(nameof(Files))]
    public void HeldBytesStayWithinWorkingBytes(string file)
    {
        byte[] data = StreamingCorpus.Read(file);
        (long held, long working) = Held(data);
        Assert.InRange(held, 0, working);
    }

    [Fact]
    public void HeldMemoryIsFlatFromTenToFiveHundredFrames()
    {
        (long ten, _) = Held(Animation(10));
        (long many, _) = Held(Animation(500));
        Assert.Equal(ten, many);
    }

    [Fact]
    public void InterlacedFramesStreamAsLoadDecodesThem()
    {
        int interlaced = 0;
        foreach (string file in StreamingCorpus.Files.Where(f => f.EndsWith(".gif", StringComparison.Ordinal)))
        {
            byte[] data = StreamingCorpus.Read(file);
            using Raster<Rgba8> expected = Raster.Load<Rgba8>(data);
            if (!expected.Frames.Any(f => f.Metadata.FormatDetails is GifFrameDetails { Interlaced: true }))
            {
                continue;
            }

            interlaced++;
            using RasterDecoder decoder = RasterDecoder.Open(data);
            using Raster<Rgba8> canvas = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height);
            for (int f = 0; f < expected.FrameCount; f++)
            {
                Assert.NotNull(decoder.ReadFrame(canvas));
                StreamingCorpus.Equal(expected, f, canvas, $"{file} frame {f}");
            }

            Assert.Null(decoder.ReadFrame(canvas));
            Assert.True(decoder.IsMetadataComplete);
        }

        Assert.True(interlaced > 0);
    }

    private static (long Held, long Working) Held(byte[] data)
    {
        long working = 0;
        long band = 0;
        MemoryPeak peak = PeakMemory.Measure(context =>
        {
            using RasterDecoder decoder = RasterDecoder.Open(data, context);
            Assert.Equal(RasterAccess.Streamed, decoder.Info.Access);
            working = decoder.Info.WorkingBytes;
            using Raster<Rgba8> canvas = Raster.Create<Rgba8>(decoder.Info.Width, decoder.Info.Height, context);
            band = (long)canvas.Width * canvas.Height * 4;
            while (decoder.ReadFrame(canvas) is not null)
            {
            }
        });
        Assert.Equal(0, peak.LeftBytes);
        return (peak.PeakPixelBytes - band, working);
    }

    private static byte[] Animation(int frames)
    {
        using Raster<Rgba8> animation = Raster.Create<Rgba8>(48, 32);
        for (int i = 1; i < frames; i++)
        {
            animation.Frames.Add(animation.Frames[0]);
        }

        for (int i = 0; i < frames; i++)
        {
            Span<Rgba8> row = animation.Frames[i].Row(i % 32);
            row.Fill(new Rgba8((byte)(i * 7), 50, 200, 255));
            animation.Frames[i].Metadata.Disposal = i % 3 == 0 ? Egami.Metadata.FrameDisposal.RestorePrevious : Egami.Metadata.FrameDisposal.None;
        }

        using MemoryStream stream = new();
        animation.Save(stream, GifFormat.Info);
        return stream.ToArray();
    }
}
