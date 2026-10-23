using System.Buffers.Binary;
using System.Text.Json;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// V5.9 stage C gates: Egami.Video's ALAC encoder must write the packets Apple's reference encoder writes for the same
/// signal, at both levels, and its decoder must give the signal back from Apple's streams. The streams in
/// tests/video/corpus/audio/alac come from tools/video/make-alac-references.py; <see cref="Signal"/> repeats its generator.
/// </summary>
public class AlacReferenceTests
{
    private static readonly string Folder = Path.Combine(AudioCorpus.Directory, "alac");

    private static readonly Dictionary<string, Clip> Manifest = LoadManifest();

    public static IEnumerable<object[]> Clips => Manifest.Keys.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    [Theory]
    [MemberData(nameof(Clips))]
    public void EncodesLikeApple(string name)
    {
        Clip clip = Manifest[name];
        (byte[] config, List<byte[]> expected) = ReadStream(name);
        int[][] signal = Signal(clip);
        AudioEncoderOptions options = new()
        {
            SampleRate = clip.Rate,
            Layout = AlacLayout(clip.Channels),
            BitsPerSample = clip.Bits,
            Level = clip.Fast ? 0 : 1,
            FrameSize = clip.FrameSize,
        };
        using IAudioEncoder encoder = AlacCodec.Instance.CreateEncoder(options, MediaContext.Default);
        using (AudioFrame frame = Frame(signal, clip))
        {
            encoder.Send(frame);
        }

        encoder.Flush();
        List<byte[]> packets = [];
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                packets.Add(packet.Data.ToArray());
            }
        }

        Assert.Equal(expected.Count, packets.Count);
        for (int i = 0; i < packets.Count; i++)
        {
            Assert.True(expected[i].AsSpan().SequenceEqual(packets[i]), $"Packet {i} of {packets.Count} differs ({packets[i].Length} bytes, Apple's {expected[i].Length}).");
        }

        // The config matches Apple's but for the maximum frame size and bit rate, which Apple's tool leaves at 0.
        byte[] ours = encoder.Track.CodecPrivate.ToArray();
        Assert.Equal(config.AsSpan(0, 12).ToArray(), ours.AsSpan(0, 12).ToArray());
        Assert.Equal(config.AsSpan(20, 4).ToArray(), ours.AsSpan(20, 4).ToArray());
        Assert.Equal(packets.Max(p => p.Length), BinaryPrimitives.ReadInt32BigEndian(ours.AsSpan(12)));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void DecodesAppleStreams(string name)
    {
        Clip clip = Manifest[name];
        (byte[] config, List<byte[]> packets) = ReadStream(name);
        int[][] signal = Signal(clip);
        AudioTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Audio,
            CodecId = CodecIds.Alac,
            CodecPrivate = config,
            TimeBase = new Rational(1, clip.Rate),
            SampleRate = clip.Rate,
            Channels = clip.Channels,
        };
        using IAudioDecoder decoder = AlacCodec.Instance.CreateDecoder(track, null, MediaContext.Default);
        Assert.True(MediaContext.Default.FindAudioCodec(track) is AlacCodec);
        int at = 0;
        long time = 0;
        int[] order = Order(clip.Channels);
        foreach (byte[] data in packets)
        {
            using EncodedPacket packet = EncodedPacket.Rent(1, data.Length, new MediaTime(time, track.TimeBase));
            data.CopyTo(packet.GetWritableData().Span);
            decoder.Send(packet);
            while (decoder.TryReceive(out AudioFrame? frame))
            {
                using (frame)
                {
                    Assert.Equal(clip.Bits, frame.ValidBits);
                    Assert.Equal(AlacLayout(clip.Channels), frame.Layout);
                    for (int c = 0; c < clip.Channels; c++)
                    {
                        int[] decoded = frame.Format == SampleFormat.S16Planar
                            ? [.. frame.AsSpan<short>(order[c]).ToArray().Select(s => (int)s)]
                            : frame.AsSpan<int>(order[c]).ToArray();
                        Assert.Equal(signal[c].AsSpan(at, frame.SampleCount).ToArray(), decoded);
                    }

                    at += frame.SampleCount;
                    time += frame.SampleCount;
                }
            }
        }

        Assert.Equal(clip.Samples, at);
    }

    /// <summary>The layout ALAC gives a channel count, which the encoder takes its input in.</summary>
    internal static ChannelLayout AlacLayout(int channels) => channels switch
    {
        1 => ChannelLayout.Mono,
        2 => ChannelLayout.Stereo,
        3 => ChannelLayout.Surround30,
        4 => new ChannelLayout(Speakers.FrontLeft | Speakers.FrontRight | Speakers.FrontCenter | Speakers.BackCenter),
        5 => ChannelLayout.Surround50,
        6 => ChannelLayout.Surround51,
        7 => new ChannelLayout(ChannelLayout.Surround51.Speakers | Speakers.BackCenter),
        _ => new ChannelLayout(ChannelLayout.Surround51.Speakers | Speakers.FrontLeftOfCenter | Speakers.FrontRightOfCenter),
    };

    /// <summary>Where each ALAC stream channel (C L R ... order) sits in Egami's order.</summary>
    private static int[] Order(int channels) => channels switch
    {
        1 => [0],
        2 => [0, 1],
        3 => [2, 0, 1],
        4 => [2, 0, 1, 3],
        5 => [2, 0, 1, 3, 4],
        6 => [2, 0, 1, 4, 5, 3],
        7 => [2, 0, 1, 4, 5, 6, 3],
        _ => [2, 6, 7, 0, 1, 4, 5, 3],
    };

    /// <summary>The signal of make-alac-references.py, per stream channel, right-aligned.</summary>
    private static int[][] Signal(Clip clip)
    {
        int[][] channels = [.. Enumerable.Range(0, clip.Channels).Select(_ => new int[clip.Samples])];
        uint state = (uint)clip.Seed;
        long full = 1L << (clip.Bits - 1);
        for (int i = 0; i < clip.Samples; i++)
        {
            for (int c = 0; c < clip.Channels; c++)
            {
                state = unchecked((state * 1664525) + 1013904223);
                long noise = (state >> 16) - 32768L;
                long v;
                if (clip.Kind == "noise")
                {
                    v = state >> (32 - clip.Bits);
                    if (v >= full)
                    {
                        v -= 1L << clip.Bits;
                    }
                }
                else
                {
                    int period = 97 + (31 * c) + (i / 1500 * 7);
                    int t = i % period;
                    v = (Math.Abs((2 * t) - period) * full / period) - (full / 2) + ((noise * (full >> 10)) >> 15);
                    if (clip.Kind == "burst" && i is not (>= 3000 and < 3500))
                    {
                        v = 0;
                    }
                }

                channels[c][i] = (int)v;
            }
        }

        return channels;
    }

    private static AudioFrame Frame(int[][] signal, Clip clip)
    {
        int[] order = Order(clip.Channels);
        AudioFrame frame = new(SampleFormat.S32Planar, AlacLayout(clip.Channels), clip.Rate, clip.Samples, MediaTime.Zero, MediaContext.Default) { ValidBits = clip.Bits };
        for (int c = 0; c < clip.Channels; c++)
        {
            signal[c].CopyTo(frame.GetSpan<int>(order[c]));
        }

        return frame;
    }

    private static (byte[] Config, List<byte[]> Packets) ReadStream(string name)
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Folder, name + ".alac"));
        List<byte[]> packets = [];
        int at = 24;
        while (at < file.Length)
        {
            int length = BinaryPrimitives.ReadInt32LittleEndian(file.AsSpan(at));
            packets.Add(file.AsSpan(at + 4, length).ToArray());
            at += 4 + length;
        }

        return (file[..24], packets);
    }

    private static Dictionary<string, Clip> LoadManifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Folder, "manifest.json")));
        Dictionary<string, Clip> result = [];
        foreach (JsonProperty clip in json.RootElement.EnumerateObject())
        {
            JsonElement e = clip.Value;
            result[clip.Name] = new Clip(
                e.GetProperty("bits").GetInt32(),
                e.GetProperty("channels").GetInt32(),
                e.GetProperty("rate").GetInt32(),
                e.GetProperty("fast").GetBoolean(),
                e.GetProperty("kind").GetString()!,
                e.GetProperty("frameSize").GetInt32(),
                e.GetProperty("samples").GetInt32(),
                e.GetProperty("seed").GetInt32());
        }

        return result;
    }

    private sealed record Clip(int Bits, int Channels, int Rate, bool Fast, string Kind, int FrameSize, int Samples, int Seed);
}
