using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// The VP8 stream encoder: its frames decode, the decoder's reconstruction is the encoder's, every effort and reference plan
/// works, and the output does not depend on the thread count.
/// </summary>
public class Vp8StreamEncoderTests
{
    /// <summary>Decodes a committed clip into frames to encode again: real motion at a small size.</summary>
    internal static List<YuvRaster<byte>> Source(string clip, int count = int.MaxValue)
    {
        List<YuvRaster<byte>> frames = [];
        using Vp8StreamDecoder decoder = new();
        foreach (byte[] packet in Vp8VideoCorpus.ReadIvf(Path.Combine(Vp8VideoCorpus.Clips, clip + ".ivf")))
        {
            Vp8FrameInfo info = decoder.Decode(packet);
            if (!info.IsShown)
            {
                continue;
            }

            YuvRaster<byte> frame = YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
            decoder.CopyFrameTo(frame);
            frames.Add(frame);
            if (frames.Count == count)
            {
                break;
            }
        }

        return frames;
    }

    private static long LumaError(YuvRaster<byte> a, YuvRaster<byte> b)
    {
        long sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> ra = a.Y.Row(y), rb = b.Y.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                int d = ra[x] - rb[x];
                sum += d * d;
            }
        }

        return sum;
    }

    [Theory]
    [InlineData("v0-pan", 0)]
    [InlineData("v0-pan", 2)]
    [InlineData("v0-pan", 4)]
    [InlineData("v0-pan", 6)]
    [InlineData("v0-pan", 8)]
    [InlineData("v0-pan", 10)]
    [InlineData("v0-odd-97x61", 4)]
    [InlineData("v0-odd-17x9", 9)]
    public void FramesDecodeAsTheEncoderReconstructedThem(string clip, int effort)
    {
        List<YuvRaster<byte>> source = Source(clip, 12);
        int w = source[0].Width, h = source[0].Height;
        using Vp8StreamEncoder encoder = new(w, h);
        Vp8Planes recon = new(w, h);
        encoder.Reconstruction = recon;
        using Vp8StreamDecoder decoder = new();
        using YuvRaster<byte> decoded = YuvRaster.Create(w, h, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        long keyBits = 0, interBits = 0;
        for (int i = 0; i < source.Count; i++)
        {
            Vp8EncodedFrame frame = encoder.Encode(source[i], new Vp8FramePlan { Effort = effort, FilterLevel = 0, Quality = 60 });
            Assert.Equal(i == 0, frame.IsKeyFrame);
            decoder.Decode(frame.Data.Span);
            decoder.CopyFrameTo(decoded);
            for (int y = 0; y < h; y++)
            {
                Assert.True(decoded.Y.Row(y).SequenceEqual(recon.Y.AsSpan(y * recon.Stride, w)), $"frame {i} luma row {y}");
            }

            for (int y = 0; y < (h + 1) / 2; y++)
            {
                Assert.True(decoded.U!.Row(y).SequenceEqual(recon.U.AsSpan(y * recon.ChromaStride, (w + 1) / 2)), $"frame {i} U row {y}");
                Assert.True(decoded.V!.Row(y).SequenceEqual(recon.V.AsSpan(y * recon.ChromaStride, (w + 1) / 2)), $"frame {i} V row {y}");
            }

            Assert.Equal(LumaError(source[i], decoded), frame.LumaSquaredError);
            Assert.True(frame.LumaPsnr > 28, $"frame {i}: {frame.LumaPsnr:F2} dB");
            if (i == 0)
            {
                keyBits = frame.Bits;
            }
            else
            {
                interBits += frame.Bits;
            }
        }

        Assert.True(interBits / (source.Count - 1) < keyBits, $"inter {interBits / (source.Count - 1)} bits a frame, key {keyBits}");
        source.ForEach(f => f.Dispose());
    }

    [Fact]
    public void ReferencePlansDecodeAsReconstructed()
    {
        // Hidden alternate frames, golden refreshes, copies, sign biases, partitions, error resilience and a mid-stream key frame.
        foreach ((string _, string clip, Func<int, Vp8FramePlan?> plan) in Vp8EncoderInteropFiles.Streams().Where(s => !s.Name.StartsWith("pan.", StringComparison.Ordinal)))
        {
            List<YuvRaster<byte>> source = Source(clip, 16);
            int w = source[0].Width, h = source[0].Height;
            using Vp8StreamEncoder encoder = new(w, h);
            Vp8Planes recon = new(w, h);
            encoder.Reconstruction = recon;
            using Vp8StreamDecoder decoder = new();
            for (int i = 0; i < source.Count; i++)
            {
                if (plan(i) is not { } p)
                {
                    continue;
                }

                Vp8EncodedFrame frame = encoder.Encode(source[i], p with { FilterLevel = 0 });
                Vp8FrameInfo info = decoder.Decode(frame.Data.Span);
                Assert.Equal(p.Show, info.IsShown);
                Assert.Equal(p.Show, frame.IsShown);
                Assert.Equal(p.KeyFrame || i == 0, info.IsKeyFrame);
                if (!info.IsShown)
                {
                    continue;
                }

                using YuvRaster<byte> decoded = YuvRaster.Create(w, h, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
                decoder.CopyFrameTo(decoded);
                for (int y = 0; y < h; y++)
                {
                    Assert.True(decoded.Y.Row(y).SequenceEqual(recon.Y.AsSpan(y * recon.Stride, w)), $"{clip} frame {i} row {y}");
                }
            }

            source.ForEach(f => f.Dispose());
        }
    }

    [Fact]
    public void OutputDoesNotDependOnThreads()
    {
        List<YuvRaster<byte>> source = Source("v0-pan", 10);
        byte[][] Encode(int threads)
        {
            using Vp8StreamEncoder encoder = new(source[0].Width, source[0].Height, EgamiContext.Default with { MaxParallelism = threads });
            return [.. source.Select((f, i) => encoder.Encode(f, new Vp8FramePlan { Effort = 6, Partitions = 4, RefreshGolden = i == 5 }).Data.ToArray())];
        }

        byte[][] one = Encode(1), many = Encode(8);
        for (int i = 0; i < one.Length; i++)
        {
            Assert.Equal(one[i], many[i]);
        }

        source.ForEach(f => f.Dispose());
    }

    [Fact]
    public void MoreEffortCostsFewerBits()
    {
        List<YuvRaster<byte>> source = Source("v0-pan", 16);
        long Bits(int effort)
        {
            using Vp8StreamEncoder encoder = new(source[0].Width, source[0].Height);
            return source.Sum(f => encoder.Encode(f, new Vp8FramePlan { Effort = effort, Quantizer = 40 }).Bits);
        }

        Assert.True(Bits(8) < Bits(0));
        source.ForEach(f => f.Dispose());
    }

    [Fact]
    public void StatisticsDescribeTheFrame()
    {
        List<YuvRaster<byte>> source = Source("v0-pan", 3);
        using Vp8StreamEncoder encoder = new(source[0].Width, source[0].Height);
        Vp8EncodedFrame key = encoder.Encode(source[0], new Vp8FramePlan { Quantizer = 30, FilterLevel = 12 });
        Assert.True(key.IsKeyFrame);
        Assert.Equal(30, key.Quantizer);
        Assert.Equal(12, key.FilterLevel);
        Assert.Equal(key.Macroblocks, key.IntraMacroblocks);
        Assert.Equal(11 * 9, key.Macroblocks);
        Assert.Equal(key.Data.Length * 8L, key.Bits);

        // The same picture again: nothing moves, so most macroblocks have nothing to code.
        Vp8EncodedFrame still = encoder.Encode(source[0], new Vp8FramePlan { Quantizer = 30 });
        Assert.False(still.IsKeyFrame);
        Assert.True(still.SkippedMacroblocks > still.Macroblocks / 2, $"{still.SkippedMacroblocks} of {still.Macroblocks} skipped");
        Assert.True(still.Bits * 5 < key.Bits);
        source.ForEach(f => f.Dispose());
    }

    [Fact]
    public void BadArgumentsThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8StreamEncoder(0, 16));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8StreamEncoder(16, 16384));
        Assert.Throws<LimitExceededException>(() => new Vp8StreamEncoder(4000, 4000, EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxPixels = 1000 } }));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8FramePlan { Quality = 101 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8FramePlan { Quantizer = 128 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8FramePlan { Effort = 11 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8FramePlan { Partitions = 3 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new Vp8FramePlan { FilterLevel = 64 });

        using Vp8StreamEncoder encoder = new(32, 32);
        using YuvRaster<byte> wrongSize = YuvRaster.Create(16, 32, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        using YuvRaster<byte> wrongFormat = YuvRaster.Create(32, 32, ChromaFormat.Yuv444, YuvColor.Bt601Limited);
        using YuvRaster<byte> right = YuvRaster.Create(32, 32, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        Assert.Throws<ArgumentException>(() => encoder.Encode(wrongSize, Vp8FramePlan.Default));
        Assert.Throws<ArgumentException>(() => encoder.Encode(wrongFormat, Vp8FramePlan.Default));
        Assert.Throws<ArgumentException>(() => encoder.Encode(right, new Vp8FramePlan { CopyToGolden = Vp8ReferenceCopy.Golden }));
        Assert.Throws<ArgumentException>(() => encoder.Encode(right, new Vp8FramePlan { CopyToAlternate = Vp8ReferenceCopy.Alternate }));
        Assert.True(encoder.Encode(right, Vp8FramePlan.Default).IsKeyFrame);
        encoder.Dispose();
        Assert.Throws<ObjectDisposedException>(() => encoder.Encode(right, Vp8FramePlan.Default));
    }
}
