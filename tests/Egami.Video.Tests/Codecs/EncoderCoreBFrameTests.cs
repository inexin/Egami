using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Encoding;

using Clip = Egami.Video.Tests.Codecs.Vp8EncoderTests.Clip;

namespace Egami.Video.Tests.Codecs;

/// <summary>The encoder core's B-frame order: runs of frames coded anchor first, B-pyramids and decode timestamps.</summary>
public class EncoderCoreBFrameTests
{
    private static readonly Rational Rate = new(30, 1);

    [Fact]
    public void RunsAreCodedAnchorFirstWithDecodeTimestampsBehindPresentation()
    {
        using Clip clip = Clip.Moving(64, 48, 20, speed: 0);
        (List<FrameDecision> decisions, List<Packet> packets, int delay) = Encode(clip, new Options(BFrames: 3));
        Assert.Equal(3, delay);
        Assert.Equal([0, 4, 1, 2, 3, 8, 5, 6, 7, 12, 9, 10, 11, 16, 13, 14, 15, 19, 17, 18], decisions.Select(d => d.Index));
        Assert.Equal(FrameType.Key, decisions[0].Type);
        foreach (FrameDecision d in decisions.Skip(1))
        {
            bool anchor = d.Index % 4 == 0 || d.Index == 19;
            Assert.Equal(anchor ? FrameType.Inter : FrameType.Bidirectional, d.Type);
            Assert.Equal(anchor, d.Referenced);
        }

        CheckTimestamps(packets, delay);
        Assert.Equal(-3, packets[0].Decode);
    }

    [Fact]
    public void APyramidCodesTheRunsMiddleFirstAsAReference()
    {
        using Clip clip = Clip.Moving(64, 48, 9, speed: 0);
        (List<FrameDecision> decisions, List<Packet> packets, int delay) = Encode(clip, new Options(BFrames: 3, Pyramid: true));
        Assert.Equal([0, 4, 2, 1, 3, 8, 6, 5, 7], decisions.Select(d => d.Index));
        FrameDecision middle = decisions[2];
        Assert.Equal((FrameType.Bidirectional, 1, true), (middle.Type, middle.Layer, middle.Referenced));
        Assert.Equal((FrameType.Bidirectional, 2, false), (decisions[3].Type, decisions[3].Layer, decisions[3].Referenced));
        CheckTimestamps(packets, delay);
    }

    [Fact]
    public void NoRunReachesPastAKeyframe()
    {
        using Clip clip = Clip.Moving(64, 48, 14, speed: 0);
        (List<FrameDecision> decisions, List<Packet> packets, int delay) = Encode(clip, new Options(BFrames: 3), forceKeyAt: 6);
        Assert.Equal([0, 4, 1, 2, 3, 5, 6, 10, 7, 8, 9, 13, 11, 12], decisions.Select(d => d.Index));
        Assert.Equal(FrameType.Inter, decisions[5].Type);
        Assert.Equal(FrameType.Key, decisions[6].Type);
        Assert.True(packets.Single(p => p.Presentation == 6).Key);
        CheckTimestamps(packets, delay);
    }

    [Fact]
    public void AdaptiveRunsShortenWhereFramesDoNotPredictEachOther()
    {
        using Clip still = Clip.Moving(64, 48, 17, speed: 0);
        (List<FrameDecision> calm, _, _) = Encode(still, new Options(BFrames: 3, Adaptive: true));
        Assert.Equal(12, calm.Count(d => d.Type == FrameType.Bidirectional));

        // Every frame new noise: nothing predicts across a run, so every frame is an anchor.
        using Clip noise = Noise(64, 48, 17);
        (List<FrameDecision> busy, List<Packet> packets, int delay) = Encode(noise, new Options(BFrames: 3, Adaptive: true, SceneCuts: false));
        Assert.DoesNotContain(busy, d => d.Type == FrameType.Bidirectional);
        Assert.Equal(Enumerable.Range(0, 17), busy.Select(d => d.Index));
        CheckTimestamps(packets, delay);
    }

    [Fact]
    public void RealtimeCodesInDisplayOrderWithoutDecodeTimestamps()
    {
        using Clip clip = Clip.Moving(64, 48, 8, speed: 0);
        (List<FrameDecision> decisions, List<Packet> packets, int delay) = Encode(clip, new Options(BFrames: 3, Realtime: true));
        Assert.Equal(0, delay);
        Assert.Equal(Enumerable.Range(0, 8), decisions.Select(d => d.Index));
        Assert.DoesNotContain(decisions, d => d.Type == FrameType.Bidirectional);
        Assert.All(packets, p => Assert.Null(p.Decode));
    }

    [Theory]
    [InlineData(RateControlMode.Quality)]
    [InlineData(RateControlMode.Bitrate)]
    [InlineData(RateControlMode.ConstantBitrate)]
    public void BFramesGetCoarserStepsAndRateControlKeepsUp(RateControlMode mode)
    {
        using Clip clip = Clip.Moving(64, 48, 30, speed: 1);
        (List<FrameDecision> decisions, List<Packet> packets, int delay) = Encode(clip, new Options(BFrames: 2, Mode: mode));
        Assert.Equal(30, packets.Count);
        CheckTimestamps(packets, delay);
        if (mode == RateControlMode.Quality)
        {
            int p = decisions.First(d => d.Type == FrameType.Inter).Quantizer;
            Assert.All(decisions.Where(d => d.Type == FrameType.Bidirectional), d => Assert.True(d.Quantizer > p));
        }
    }

    [Fact]
    public void BFramesAreCheckedAgainstTheLimit()
    {
        using EncoderPictures pictures = new(16, 16, EncoderPictures.Vp8Color, EgamiContext.Default);
        using FakeCoder coder = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncoderCore(Settings(16, 16, new Options(BFrames: 17)), coder, pictures, false));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EncoderCore(Settings(16, 16, new Options(BFrames: -1)), coder, pictures, false));
    }

    /// <summary>Every frame shown once, decode timestamps rising and never past their presentation, and no further behind than the delay.</summary>
    private static void CheckTimestamps(List<Packet> packets, int delay)
    {
        Assert.Equal(Enumerable.Range(0, packets.Count).Select(i => (long)i), packets.Select(p => p.Presentation).Order());
        for (int i = 0; i < packets.Count; i++)
        {
            long decode = Assert.NotNull(packets[i].Decode);
            Assert.True(decode <= packets[i].Presentation, $"Packet {i} decodes at {decode}, after it is shown at {packets[i].Presentation}.");
            Assert.True(packets[i].Presentation - decode <= 2 * delay);
            if (i > 0)
            {
                Assert.True(decode > packets[i - 1].Decode);
            }
        }
    }

    private static (List<FrameDecision> Decisions, List<Packet> Packets, int Delay) Encode(Clip clip, Options options, int forceKeyAt = -1)
    {
        int width = clip.Frames[0].Width, height = clip.Frames[0].Height;
        using EncoderPictures pictures = new(width, height, EncoderPictures.Vp8Color, EgamiContext.Default);
        using FakeCoder coder = new();
        using EncoderCore core = new(Settings(width, height, options), coder, pictures, false);
        List<Packet> packets = [];
        for (int i = 0; i < clip.Frames.Count; i++)
        {
            using (YuvVideoFrame frame = clip.Frame(i))
            {
                core.Send(frame, i == forceKeyAt);
            }

            Drain(core, packets);
        }

        core.Flush();
        Drain(core, packets);
        return (coder.Decisions, packets, core.ReorderDelay);
    }

    private static void Drain(EncoderCore core, List<Packet> packets)
    {
        while (core.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                packets.Add(new Packet(packet!.Timestamp.Ticks, packet.DecodeTimestamp?.Ticks, packet.IsKeyframe));
            }
        }
    }

    private static CoreSettings Settings(int width, int height, Options o) => new()
    {
        Width = width,
        Height = height,
        FrameRate = Rate,
        KeyframeInterval = 1000,
        SceneCuts = o.SceneCuts,
        LatencyMode = o.Realtime ? LatencyMode.Realtime : LatencyMode.Quality,
        BFrames = o.BFrames,
        BPyramid = o.Pyramid,
        AdaptiveBFrames = o.Adaptive,
        Rate = new RateSettings
        {
            Mode = o.Mode,
            BaseQuantizer = 30,
            MaxQuantizer = 127,
            Bitrate = o.Mode == RateControlMode.Quality ? 0 : 100_000,
            MaxBitrate = 1_000_000,
            BufferSize = 100_000,
            FrameRate = 30,
            Pixels = width * height,
            Steps = FakeCoder.StepTable,
        },
    };

    private static Clip Noise(int width, int height, int count)
    {
        Clip clip = Clip.Moving(width, height, count, speed: 0);
        uint seed = 99;
        foreach (YuvRaster<byte> picture in clip.Frames)
        {
            for (int y = 0; y < height; y++)
            {
                Span<byte> row = picture.Y.Row(y);
                for (int x = 0; x < width; x++)
                {
                    seed = (seed * 1664525) + 1013904223;
                    row[x] = (byte)(16 + ((seed >> 24) % 220));
                }
            }
        }

        return clip;
    }

    private readonly record struct Options(int BFrames, bool Pyramid = false, bool Adaptive = false, bool Realtime = false, bool SceneCuts = true, RateControlMode Mode = RateControlMode.Quality);

    private readonly record struct Packet(long Presentation, long? Decode, bool Key);

    /// <summary>A coder that codes nothing: it records each decision and returns a size that falls as the step grows.</summary>
    private sealed class FakeCoder : IFrameCoder
    {
        public static readonly short[] StepTable = [.. Enumerable.Range(0, 128).Select(q => (short)(4 + (q * 4)))];

        public List<FrameDecision> Decisions { get; } = [];

        public short[] Steps => StepTable;

        public CodedFrame Code(YuvRaster<byte> picture, FrameDecision decision)
        {
            Decisions.Add(decision);
            int size = decision.Type == FrameType.Key ? 40_000 : decision.Type == FrameType.Bidirectional ? 4_000 : 10_000;
            return new CodedFrame(new byte[Math.Max(1, size / StepTable[decision.Quantizer])], null, decision.Quantizer);
        }

        public (long Key, long Next) Probe(YuvRaster<byte> key, YuvRaster<byte>? next, int quantizer) => (40_000 * 8 / StepTable[quantizer], 10_000 * 8 / StepTable[quantizer]);

        public void Dispose()
        {
        }
    }
}
