using Egami.Video.Codecs;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// Steady-state encoding stays within an allocation budget beyond the packets it hands out. Run in Release: Debug inflates the
/// counts. The budgets hold today's numbers with some room, so a change that allocates more per coding unit shows here.
/// </summary>
public sealed class EncoderBudgetTests
{
    // At 320x192 a frame takes about 3.5 MB at effort 1 (which tries more units since the effort tuning) and 5.2 MB at effort
    // 4 (7.5 MB on four threads, each with its own search state) besides its packet: the frame's source and picture, and the
    // coding and transform tree nodes of the units tried. Reference planes are reused once a reference leaves the buffer.
    [Theory]
    [Trait("Category", "Allocation")]
    [InlineData(1, 1, 4_200_000)]
    [InlineData(4, 1, 6_200_000)]
    [InlineData(4, 4, 9_000_000)]
    public void SteadyStateEncodingStaysWithinItsBudget(int effort, int threads, long budget)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        const int W = 320, H = 192, Frames = 40, From = 24;
        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = threads } };
        VideoEncoderOptions options = new() { Width = W, Height = H, Effort = effort, KeyframeInterval = 8, SceneCutKeyframes = false };
        using IVideoEncoder encoder = HevcCodec.Instance.CreateEncoder(options, context);
        YuvRaster[] pictures = [.. Enumerable.Range(0, Frames).Select(i => EncoderTests.Picture(W, H, i, 8))];
        Rational timeBase = options.FrameRate.Reciprocal();
        long used = 0, packets = 0;
        for (int i = 0; i < Frames; i++)
        {
            long start = GC.GetTotalAllocatedBytes(precise: true);
            encoder.Send(new YuvVideoFrame(pictures[i], new MediaTime(i, timeBase)));
            while (encoder.TryReceive(out EncodedPacket? packet))
            {
                packets += i >= From ? packet.Data.Length : 0;
                packet.Dispose();
            }

            used += i >= From ? GC.GetTotalAllocatedBytes(precise: true) - start : 0;
        }

        foreach (YuvRaster picture in pictures)
        {
            picture.Dispose();
        }

        double perFrame = (used - packets) / (double)(Frames - From);
        TestContext.Current.SendDiagnosticMessage($"effort {effort}, {threads} threads: {perFrame:F0} bytes a frame besides the packets");
        Assert.True(perFrame < budget, $"{perFrame:F0} bytes a frame besides the packets");
    }
}
