using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>Runs the counter tests alone: the counters are process-wide, so another encoder running beside them would add to them.</summary>
[CollectionDefinition(nameof(EncoderStatsRunsAlone), DisableParallelization = true)]
public sealed class EncoderStatsRunsAlone;

/// <summary>The encoder's work counters (H264EncodeStats), which count in Debug builds and compile away in Release.</summary>
[Collection(nameof(EncoderStatsRunsAlone))]
public sealed class EncoderStatsTests
{
    private const int W = 320, H = 192, Mbs = W / 16 * (H / 16), Frames = 8;

    [Fact]
    public void CountsEveryMacroblockAndTheWorkInIt()
    {
        Assert.SkipUnless(H264EncodeStats.Enabled, "The counters compile away in this build (Release).");
        long[,] counts = Encode(effort: 4);

        long macroblocks = 0;
        for (int t = 0; t < 3; t++)
        {
            macroblocks += At(counts, t, H264EncodeStat.Macroblocks);
        }

        Assert.Equal(Frames * Mbs, macroblocks);
        Assert.Equal(Mbs, At(counts, 2, H264EncodeStat.Macroblocks));
        for (int t = 0; t < 2; t++)
        {
            Assert.True(At(counts, t, H264EncodeStat.Macroblocks) > 0, $"no {H264EncodeStats.Types[t]} macroblocks");
            Assert.True(At(counts, t, H264EncodeStat.RdCodings) >= At(counts, t, H264EncodeStat.Macroblocks));
            Assert.True(At(counts, t, H264EncodeStat.BlockSearches) > 0);
            Assert.True(At(counts, t, H264EncodeStat.IntegerCosts) > At(counts, t, H264EncodeStat.BlockSearches));
            Assert.True(At(counts, t, H264EncodeStat.SubpelCosts) > At(counts, t, H264EncodeStat.BlockSearches));
            Assert.True(At(counts, t, H264EncodeStat.InterLumaCodings) >= At(counts, t, H264EncodeStat.InterCandidates));
        }

        // An I picture has no inter work, and each of its macroblocks is one intra pass of three RD codings (I16, I4, I8).
        Assert.Equal(0, At(counts, 2, H264EncodeStat.BlockSearches));
        Assert.Equal(Mbs, At(counts, 2, H264EncodeStat.IntraPasses));
        Assert.Equal(3 * Mbs, At(counts, 2, H264EncodeStat.RdCodings));
        Assert.True(At(counts, 1, H264EncodeStat.PartCosts) > 0);
        Assert.True(At(counts, 0, H264EncodeStat.RdoqBlocks) > 0);
        Assert.True(At(counts, 0, H264EncodeStat.RdoqRateCounts) >= At(counts, 0, H264EncodeStat.RdoqBlocks));
        Assert.True(At(counts, 0, H264EncodeStat.SatdSamples) >= 16 * At(counts, 0, H264EncodeStat.SatdCalls));
    }

    [Fact]
    public void RdoqStartsAtEffortThree()
    {
        Assert.SkipUnless(H264EncodeStats.Enabled, "The counters compile away in this build (Release).");
        Assert.Equal(0, Sum(Encode(effort: 2), H264EncodeStat.RdoqBlocks));
        Assert.True(Sum(Encode(effort: 3), H264EncodeStat.RdoqBlocks) > 0);
    }

    private static long At(long[,] counts, int type, H264EncodeStat stat) => counts[type, (int)stat];

    private static long Sum(long[,] counts, H264EncodeStat stat) => At(counts, 0, stat) + At(counts, 1, stat) + At(counts, 2, stat);

    private static long[,] Encode(int effort)
    {
        MediaContext context = new() { Egami = EgamiContext.Default with { MaxParallelism = 1 } };
        VideoEncoderOptions options = new() { Width = W, Height = H, Effort = effort, KeyframeInterval = 100, SceneCutKeyframes = false };
        Rational timeBase = options.FrameRate.Reciprocal();
        YuvRaster<byte>[] pictures = [.. Enumerable.Range(0, Frames).Select(i => EncoderInterTests.Moving(W, H, i))];
        H264EncodeStats.Reset();
        using (IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, context))
        {
            for (int i = 0; i < Frames; i++)
            {
                encoder.Send(new YuvVideoFrame(pictures[i], new MediaTime(i, timeBase)));
                Drain(encoder);
            }

            encoder.Flush();
            Drain(encoder);
        }

        foreach (YuvRaster<byte> picture in pictures)
        {
            picture.Dispose();
        }

        return H264EncodeStats.Snapshot();
    }

    private static void Drain(IVideoEncoder encoder)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            packet.Dispose();
        }
    }
}
