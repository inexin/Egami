using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Tests.Audio;

/// <summary>The SILK decoder translated from libopus 1.5.2, checked against values and decodes from libopus itself.</summary>
public class SilkTests
{
    [Fact]
    public void TablesMatchLibopus()
    {
        Assert.Equal(129, SilkTables.LsfCosTabFixQ12.Length);
        Assert.Equal(8192, SilkTables.LsfCosTabFixQ12[0]);
        Assert.Equal(-8192, SilkTables.LsfCosTabFixQ12[128]);
        Assert.Equal(180, SilkTables.PulsesPerBlockIcdf.Length);
        Assert.Equal([224, 192, 160, 128, 96, 64, 32, 0], SilkTables.Uniform8Icdf.ToArray());
        Assert.Equal(-13732, SilkTables.StereoPredQuantQ13[0]);
        Assert.Equal(13732, SilkTables.StereoPredQuantQ13[15]);
        Assert.Equal([100, 240, 32, 100], SilkTables.QuantizationOffsetsQ10.ToArray());
        Assert.Equal(16, SilkNlsfCodebook.Wide.Order);
        Assert.Equal(17, SilkNlsfCodebook.Wide.DeltaMinQ15.Length);
        Assert.Equal(11, SilkNlsfCodebook.NarrowMedium.DeltaMinQ15.Length);
    }

    [Fact]
    public void Nlsf2AMatchesLibopus()
    {
        // silk_NLSF2A in libopus 1.5.2 on these vectors (tools/video/opusref, a small C harness).
        short[] nlsf10 = new short[10];
        for (int i = 0; i < 10; i++)
        {
            nlsf10[i] = (short)(2979 * (i + 1));
        }

        short[] a = new short[16];
        SilkSigProc.Nlsf2A(a, nlsf10, 10);
        Assert.Equal([-1, 3, 0, 2, 0, -1, 0, 1, 0, 1], a[..10]);

        short[] nlsf16 = [1200, 2400, 3000, 5000, 7200, 8000, 10500, 12000, 14800, 17000, 19300, 21000, 24000, 26500, 29000, 31000];
        SilkSigProc.Nlsf2A(a, nlsf16, 16);
        Assert.Equal([9101, -6212, -451, 3142, -2762, 2193, -849, -939, 1037, 27, -835, 655, -171, -534, 1245, -659], a);
    }

    [Fact]
    public void FixedPointHelpersMatchLibopus()
    {
        Assert.Equal(0, SilkSigProc.Lin2Log(1));
        Assert.Equal(1275, SilkSigProc.Lin2Log(1000));
        Assert.Equal(2048, SilkSigProc.Lin2Log(65536));
        Assert.Equal(3441, SilkSigProc.Lin2Log(123456789));
        Assert.Equal(1, SilkSigProc.Log2Lin(0));
        Assert.Equal(225, SilkSigProc.Log2Lin(1000));
        Assert.Equal(86528, SilkSigProc.Log2Lin(2100));
        Assert.Equal(2122317824, SilkSigProc.Log2Lin(3966));
        Assert.Equal(61356, SilkMath.Div32VarQ(65536, 70000, 16));
        Assert.Equal(2010535547, SilkMath.Inverse32VarQ(70000, 47));
        Assert.Equal(994, SilkMath.SqrtApprox(1000000));
        Assert.Equal(int.MaxValue, SilkMath.AddSat32(int.MaxValue, 1));
        Assert.Equal(int.MinValue, SilkMath.SubSat32(int.MinValue, 1));
    }

    [Theory]
    [InlineData(16000, 48000, 7, 40, 0xaabbf7c5u)]
    [InlineData(8000, 12000, 0, 10, 0x342f3e9bu)]
    [InlineData(16000, 8000, 0, 3, 0x927a7861u)]
    [InlineData(16000, 12000, 3, 7, 0x44770539u)]
    [InlineData(12000, 48000, 4, 42, 0x8e2fc09du)]
    public void ResamplerImpulseResponseMatchesLibopus(int rateIn, int rateOut, int delay, int firstNonZero, uint hash)
    {
        SilkResampler resampler = new();
        Assert.Equal(0, resampler.Init(rateIn, rateOut));
        Assert.Equal(delay, resampler.InputDelay);
        short[] input = new short[rateIn / 50];
        input[5] = 10000;
        short[] output = new short[rateOut / 50];
        resampler.Process(output, input, input.Length);
        Assert.Equal(firstNonZero, Array.FindIndex(output, s => s != 0));
        Assert.Equal(hash, Fnv(output, 2166136261u));
    }

    [Fact]
    public void ConstructsAndResets()
    {
        SilkDecoder decoder = new();
        Assert.Equal(65536, decoder[0].PrevGainQ16);
        Assert.Equal(1, decoder[1].FirstFrameAfterReset);
        decoder.Reset();
        Assert.Equal(0, decoder[0].FsKhz);
        SilkDecodeControl control = new()
        {
            ChannelsApi = 1,
            ChannelsInternal = 1,
            ApiSampleRate = 48000,
            InternalSampleRate = 16000,
            PayloadSizeMs = 20,
        };

        // A lost first packet: concealment from the reset state is silence.
        OpusRangeDecoder range = new();
        range.Init([], 0, 0);
        short[] pcm = new short[960];
        Assert.Equal(0, decoder.Decode(control, 1, true, range, pcm, out int samples));
        Assert.Equal(960, samples);
        Assert.All(pcm, s => Assert.Equal(0, s));
    }

    /// <summary>
    /// Decodes SILK-only packets that libopus 1.5.2 encoded from a synthetic signal and compares every call's output, final
    /// range and pitch lag with silk_Decode's (tools/video/opusref/silk-vectors.c): narrow, medium and wide band, 10 to 60 ms,
    /// mono and stereo streams at every API rate and channel count, bandwidth switches, concealment and LBRR (FEC) decoding.
    /// </summary>
    [Fact]
    public void DecodesLikeLibopus()
    {
        using BinaryReader reader = new(File.OpenRead(AudioCorpus.PathOf("silk-vectors.bin")));
        Assert.Equal("SLKV"u8.ToArray(), reader.ReadBytes(4));
        int configs = reader.ReadInt32();
        int[] rates = [8000, 12000, 16000];
        int[] durations = [10, 20, 40, 60];
        short[] pcm = new short[2 * 48 * 60];
        for (int c = 0; c < configs; c++)
        {
            int apiRate = reader.ReadInt32();
            int apiChannels = reader.ReadInt32();
            int packetCount = reader.ReadInt32();
            byte[][] packets = new byte[packetCount][];
            int[] actions = new int[packetCount];
            uint[] hashes = new uint[packetCount];
            int[] totals = new int[packetCount];
            uint[] ranges = new uint[packetCount];
            int[] pitchLags = new int[packetCount];
            int[] rets = new int[packetCount];
            for (int p = 0; p < packetCount; p++)
            {
                actions[p] = reader.ReadByte();
                packets[p] = reader.ReadBytes(reader.ReadUInt16());
                hashes[p] = reader.ReadUInt32();
                totals[p] = reader.ReadInt32();
                ranges[p] = reader.ReadUInt32();
                pitchLags[p] = reader.ReadInt32();
                rets[p] = reader.ReadInt32();
            }

            SilkDecoder decoder = new();
            OpusRangeDecoder range = new();
            for (int p = 0; p < packetCount; p++)
            {
                int toc = packets[p][0];
                int config = toc >> 3;
                int action = actions[p];
                byte[] data = action == 2 ? packets[p + 1] : packets[p];
                range.Init(data, 1, action == 1 ? 0 : data.Length - 1);
                SilkDecodeControl control = new()
                {
                    ChannelsApi = apiChannels,
                    ChannelsInternal = ((toc >> 2) & 1) + 1,
                    ApiSampleRate = apiRate,
                    InternalSampleRate = rates[config / 4],
                    PayloadSizeMs = durations[config % 4],
                };

                int want = apiRate / 1000 * control.PayloadSizeMs;
                int total = 0;
                int ret = 0;
                uint hash = 2166136261u;
                while (total < want)
                {
                    ret |= decoder.Decode(control, action, total == 0, range, pcm.AsSpan(total * apiChannels), out int n);
                    hash = Fnv(pcm.AsSpan(total * apiChannels, n * apiChannels), hash);
                    total += n;
                    if (n <= 0)
                    {
                        break;
                    }
                }

                string where = $"config {c}, packet {p}, action {action}";
                Assert.True(rets[p] == ret, where + ": return code");
                Assert.True(totals[p] == total, where + ": sample count");
                if (action != 1)
                {
                    Assert.True(ranges[p] == range.Range, where + ": final range");
                }

                Assert.True(hashes[p] == hash, where + ": samples");
                Assert.True(pitchLags[p] == control.PrevPitchLag, where + ": pitch lag");
            }
        }
    }

    private static uint Fnv(ReadOnlySpan<short> samples, uint hash)
    {
        foreach (short s in samples)
        {
            ushort v = (ushort)s;
            hash = (hash ^ (uint)(v & 0xFF)) * 16777619u;
            hash = (hash ^ (uint)(v >> 8)) * 16777619u;
        }

        return hash;
    }
}
