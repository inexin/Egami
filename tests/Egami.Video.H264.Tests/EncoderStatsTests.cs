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

        // An I picture has no inter work, and each of its macroblocks is one intra pass that RD-codes the best of I16, I4 and
        // I8 by SATD, and the next when it comes close.
        Assert.Equal(0, At(counts, 2, H264EncodeStat.BlockSearches));
        Assert.Equal(Mbs, At(counts, 2, H264EncodeStat.IntraPasses));
        Assert.InRange(At(counts, 2, H264EncodeStat.RdCodings), Mbs, 2 * Mbs);
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

    [Fact]
    public void SplitsEachPictureKindsBitsAndMacroblocks()
    {
        Assert.SkipUnless(H264EncodeStats.Enabled, "The counters compile away in this build (Release).");
        long bytes = 0;
        Encode(effort: 4, b => bytes += b);
        long[,] pictures = H264EncodeStats.PictureSnapshot();
        int parts = Enum.GetValues<H264BitPart>().Length;
        int shares = Enum.GetValues<H264BlockShare>().Length;
        Assert.Equal(H264EncodeStats.PictureColumns.Length, pictures.GetLength(1));

        long count = 0, bits = 0;
        for (int k = 0; k < H264EncodeStats.PictureKinds.Length; k++)
        {
            count += pictures[k, 0];
            bits += pictures[k, 1];
            if (pictures[k, 0] == 0)
            {
                continue;
            }

            // The parts are the rate counter's estimate of the macroblock layer: a little under the NAL units' bits, which add
            // the slice headers.
            long estimate = 0;
            for (int i = 0; i < parts; i++)
            {
                estimate += pictures[k, 2 + i];
            }

            Assert.InRange(estimate / 32768.0, 0.9 * pictures[k, 1], pictures[k, 1]);
            long mbs = 0;
            for (int i = 0; i < shares; i++)
            {
                mbs += pictures[k, 2 + parts + i];
            }

            Assert.Equal(pictures[k, 0] * Mbs, mbs);
        }

        Assert.Equal(Frames, count);
        Assert.Equal(1, pictures[0, 0]);
        Assert.Equal(Mbs, pictures[0, 2 + parts + (int)H264BlockShare.Intra]);
        Assert.True(pictures[1, 2 + (int)H264BitPart.Vectors] > 0, "no vector bits in P pictures");
        Assert.True(pictures[2, 0] + pictures[3, 0] > 0, "no B pictures");

        // The NAL units' bits, without the parameter sets and start codes the stream adds.
        Assert.InRange(bits, 0.8 * 8 * bytes, 8 * bytes);
    }

    private static long At(long[,] counts, int type, H264EncodeStat stat) => counts[type, (int)stat];

    private static long Sum(long[,] counts, H264EncodeStat stat) => At(counts, 0, stat) + At(counts, 1, stat) + At(counts, 2, stat);

    private static long[,] Encode(int effort, Action<long>? bytes = null)
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
                Drain(encoder, bytes);
            }

            encoder.Flush();
            Drain(encoder, bytes);
        }

        foreach (YuvRaster<byte> picture in pictures)
        {
            picture.Dispose();
        }

        return H264EncodeStats.Snapshot();
    }

    private static void Drain(IVideoEncoder encoder, Action<long>? bytes)
    {
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            bytes?.Invoke(packet.Data.Length);
            packet.Dispose();
        }
    }
}
