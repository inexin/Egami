using Egami.Video.Codecs;

namespace Egami.Video.H264.Tests;

/// <summary>Steady-state encoding allocates little beyond the packets it hands out. Run in Release: Debug inflates the counts.</summary>
public sealed class EncoderBudgetTests
{
    // At 320x192 a frame takes 9 to 19 KB besides its packet (the lookahead's analysis included), a keyframe about 100 KB.
    [Theory]
    [Trait("Category", "Allocation")]
    [InlineData(1, 1, 0, 40_000)]
    [InlineData(1, 1, null, 40_000)]
    [InlineData(4, 4, null, 40_000)]
    public void SteadyStateEncodingAllocatesLittleBeyondThePackets(int slices, int threads, int? bFrames, long budget)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        const int W = 320, H = 192, Frames = 100, From = 60;
        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        VideoEncoderOptions options = new()
        {
            Width = W,
            Height = H,
            KeyframeInterval = 10,
            SceneCutKeyframes = false,
            H264 = new H264EncoderOptions { Slices = slices, BFrames = bFrames },
        };
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, context);
        YuvRaster<byte>[] pictures = [.. Enumerable.Range(0, Frames).Select(i => EncoderInterTests.Moving(W, H, i))];
        Rational timeBase = options.FrameRate.Reciprocal();
        long used = 0, packets = 0;
        for (int i = 0; i < Frames; i++)
        {
            long start = GC.GetAllocatedBytesForCurrentThread();
            encoder.Send(new YuvVideoFrame(pictures[i], new MediaTime(i, timeBase)));
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                packets += i >= From ? packet.Data.Length : 0;
                packet.Dispose();
            }

            used += i >= From ? GC.GetAllocatedBytesForCurrentThread() - start : 0;
        }

        foreach (YuvRaster<byte> picture in pictures)
        {
            picture.Dispose();
        }

        double perFrame = (used - packets) / (double)(Frames - From);
        Assert.True(perFrame < budget, $"{perFrame:F0} bytes a frame besides the packets");
    }
}
