using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;

using Egami.Video.Audio.Codecs.Vorbis;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The Vorbis decoder against libvorbisfile's ov_read_float on the clips in tests/video/corpus/audio/vorbis (see
/// tools/video/make-vorbis-corpus.py): every sample within 2^-16 of full scale.
/// </summary>
public class VorbisTests
{
    private const float Tolerance = 1f / 65536;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "corpus", "audio", "vorbis");

    public static TheoryData<string> Clips => [.. Manifest().Keys.Order(StringComparer.Ordinal)];

    private static Dictionary<string, (int Channels, int Rate, long Samples)> Manifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "manifest.json")));
        Dictionary<string, (int, int, long)> result = [];
        foreach (JsonProperty clip in json.RootElement.EnumerateObject())
        {
            result[clip.Name] = (clip.Value.GetProperty("channels").GetInt32(), clip.Value.GetProperty("rate").GetInt32(),
                clip.Value.GetProperty("samples").GetInt64());
        }

        return result;
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void DecodesLikeLibvorbis(string name)
    {
        (int channels, int rate, long samples) = Manifest()[name];
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, name)));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        Assert.Equal(channels, decoder.Channels);
        Assert.Equal(rate, decoder.SampleRate);

        float[][] ours = DecodeTrimmed(decoder, packets.Skip(3));
        Assert.Equal(samples, ours[0].Length);

        float[] reference = ReadReference(Path.Combine(Directory, name[..^4] + ".f32.gz"));
        Assert.Equal(samples * channels, reference.Length);
        double max = 0;
        for (int i = 0; i < samples; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                max = Math.Max(max, Math.Abs(ours[c][i] - reference[(i * channels) + c]));
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: max difference {max:E3} ({Math.Log2(Math.Max(max, 1e-30)):F1} bits)");
        Assert.True(max < Tolerance, $"{name}: max difference {max} against libvorbis");
    }

    [Fact]
    public void ReadsTheHeaders()
    {
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, "q0-stereo-22k.ogg")));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        Assert.Equal("Corpus tone", decoder.Comments["TITLE"]);
        Assert.Equal("Egami", decoder.Comments["ARTIST"]);
        Assert.StartsWith("Xiph.Org libVorbis", decoder.Vendor, StringComparison.Ordinal);
        Assert.True(decoder.BlockSize0 <= decoder.BlockSize1, $"{decoder.BlockSize0} {decoder.BlockSize1}");
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void PacketSamplesMatchesDecode(string name)
    {
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, name)));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        int previous = 0;
        foreach (OggPacket packet in packets.Skip(3))
        {
            int expected = decoder.PacketSamples(packet.Data, ref previous);
            Assert.Equal(expected, decoder.Decode(packet.Data));
        }
    }

    [Fact]
    public void ResetPrimesAgain()
    {
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, "q3-stereo-44k-bursts.ogg")));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        Assert.Equal(0, decoder.Decode(packets[3].Data));
        Assert.True(decoder.Decode(packets[4].Data) > 0);
        decoder.Reset();
        Assert.Equal(0, decoder.Decode(packets[5].Data));

        // After the reset, the stream continues exactly as a decoder that started at that packet.
        VorbisDecoder fresh = new([.. packets.Take(3).Select(p => p.Data)]);
        Assert.Equal(0, fresh.Decode(packets[5].Data));
        int n = decoder.Decode(packets[6].Data);
        Assert.Equal(n, fresh.Decode(packets[6].Data));
        Assert.True(decoder.GetSamples(0).SequenceEqual(fresh.GetSamples(0)));
    }

    [Fact]
    public void RejectsBrokenHeaders()
    {
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, "q3-51-16k.ogg")));
        byte[][] headers = [.. packets.Take(3).Select(p => p.Data)];
        Random random = new(5);
        for (int trial = 0; trial < 3000; trial++)
        {
            byte[][] broken = [.. headers.Select(h => (byte[])h.Clone())];
            int which = trial % 3 == 0 ? random.Next(3) : 2;
            byte[] h = broken[which];
            if (trial % 4 == 0)
            {
                broken[which] = h[..random.Next(h.Length)];
            }
            else
            {
                for (int k = random.Next(1, 4); k > 0; k--)
                {
                    h[random.Next(h.Length)] ^= (byte)(1 << random.Next(8));
                }
            }

            try
            {
                VorbisDecoder decoder = new(broken);
                foreach (OggPacket packet in packets.Skip(3).Take(8))
                {
                    try
                    {
                        decoder.Decode(packet.Data);
                    }
                    catch (CorruptDataException)
                    {
                    }
                }
            }
            catch (CorruptDataException)
            {
            }
            catch (UnsupportedFormatException)
            {
            }
        }
    }

    [Fact]
    public void SurvivesBrokenPackets()
    {
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, "q3-stereo-44k-bursts.ogg")));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        Random random = new(7);
        foreach (OggPacket packet in packets.Skip(3))
        {
            byte[] data = (byte[])packet.Data.Clone();
            switch (random.Next(3))
            {
                case 0:
                    data = data[..random.Next(data.Length + 1)];
                    break;
                case 1:
                    random.NextBytes(data);
                    break;
                default:
                    data[random.Next(data.Length)] ^= 0x10;
                    break;
            }

            try
            {
                int n = decoder.Decode(data);
                Assert.InRange(n, 0, decoder.MaxSamplesPerPacket);
            }
            catch (CorruptDataException)
            {
            }

            // A good packet always decodes after a broken one.
            decoder.Decode(packet.Data);
        }
    }

    [Fact]
    public void MixesBlockSizes()
    {
        // The bursts make oggenc switch between short and long blocks, so every window transition is decoded above.
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, "q3-stereo-44k-bursts.ogg")));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        HashSet<(int, int)> transitions = [];
        int previous = 0;
        foreach (OggPacket packet in packets.Skip(3))
        {
            int before = previous;
            decoder.PacketSamples(packet.Data, ref previous);
            transitions.Add((before, previous));
        }

        int s = decoder.BlockSize0;
        int l = decoder.BlockSize1;
        Assert.Superset(new HashSet<(int, int)> { (s, s), (s, l), (l, s), (l, l) }, transitions);
    }

    [Fact]
    public void DoesNotAllocatePerPacket()
    {
        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(Path.Combine(Directory, "q3-51-16k.ogg")));
        VorbisDecoder decoder = new([.. packets.Take(3).Select(p => p.Data)]);
        byte[][] audio = [.. packets.Skip(3).Select(p => p.Data)];
        for (int run = 0; run < 50; run++)
        {
            foreach (byte[] packet in audio)
            {
                decoder.Decode(packet);
            }

            decoder.Reset();
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        foreach (byte[] packet in audio)
        {
            decoder.Decode(packet);
        }

        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    /// <summary>Times the decode of make-vorbis-corpus.py --speed's 60 s clip when EGAMI_VORBIS_SPEED=1 (one thread).</summary>
    [Fact]
    public void Speed()
    {
        string? path = FindCache("speed.ogg");
        if (Environment.GetEnvironmentVariable("EGAMI_VORBIS_SPEED") != "1" || path is null)
        {
            return;
        }

        List<OggPacket> packets = OggPacket.ReadAll(File.ReadAllBytes(path));
        byte[][] headers = [.. packets.Take(3).Select(p => p.Data)];
        double best = double.MaxValue;
        long samples = 0;
        for (int run = 0; run < 8; run++)
        {
            Stopwatch watch = Stopwatch.StartNew();
            VorbisDecoder decoder = new(headers);
            samples = 0;
            foreach (OggPacket packet in packets.Skip(3))
            {
                samples += decoder.Decode(packet.Data);
            }

            best = Math.Min(best, watch.Elapsed.TotalMilliseconds);
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Vorbis decode: {best:F1} ms for {samples} samples per channel");
        Console.WriteLine($"Vorbis decode: {best:F1} ms for {samples} samples per channel");
    }

    private static string? FindCache(string name)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "vorbis", name);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Decodes the audio packets and trims the output as libvorbis does by granule position: on the first page with a granule,
    /// samples beyond it come off the start (or off the end, for the end-of-stream page); on the end-of-stream page, samples
    /// beyond its granule come off the end.
    /// </summary>
    private static float[][] DecodeTrimmed(VorbisDecoder decoder, IEnumerable<OggPacket> packets)
    {
        List<float>[] output = [.. Enumerable.Range(0, decoder.Channels).Select(_ => new List<float>())];
        long position = -1;
        long produced = 0;
        foreach (OggPacket packet in packets)
        {
            int n = decoder.Decode(packet.Data);
            produced += n;
            int skipStart = 0;
            int keep = n;
            if (position == -1)
            {
                if (packet.Granule != -1)
                {
                    position = packet.Granule;
                    long extra = produced - packet.Granule;
                    if (extra > 0)
                    {
                        if (packet.EndOfStream)
                        {
                            keep = (int)Math.Max(0, n - extra);
                        }
                        else
                        {
                            // Trim the beginning; the output so far is at most this packet's (earlier ones wait for it).
                            long fromEarlier = Math.Min(extra, output[0].Count);
                            foreach (List<float> list in output)
                            {
                                list.RemoveRange(0, (int)fromEarlier);
                            }

                            skipStart = (int)Math.Min(n, extra - fromEarlier);
                        }
                    }
                }
            }
            else
            {
                position += n;
                if (packet.Granule != -1 && position != packet.Granule)
                {
                    if (position > packet.Granule && packet.EndOfStream)
                    {
                        keep = (int)Math.Max(0, n - (position - packet.Granule));
                    }

                    position = packet.Granule;
                }
            }

            for (int c = 0; c < decoder.Channels; c++)
            {
                output[c].AddRange(decoder.GetSamples(c)[skipStart..keep]);
            }
        }

        return [.. output.Select(l => l.ToArray())];
    }

    private static float[] ReadReference(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        byte[] bytes = raw.ToArray();
        float[] values = new float[bytes.Length / 4];
        for (int i = 0; i < values.Length; i++)
        {
            values[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(i * 4));
        }

        return values;
    }

    /// <summary>A packet from an Ogg stream (RFC 3533): its data and, if it ends a page, that page's granule position.</summary>
    private sealed record OggPacket(byte[] Data, long Granule, bool EndOfStream)
    {
        public static List<OggPacket> ReadAll(byte[] file)
        {
            List<OggPacket> packets = [];
            List<byte> current = [];
            int p = 0;
            while (p + 27 <= file.Length)
            {
                Assert.True(file.AsSpan(p, 4).SequenceEqual("OggS"u8), "Ogg page sync");
                bool eos = (file[p + 5] & 4) != 0;
                long granule = BinaryPrimitives.ReadInt64LittleEndian(file.AsSpan(p + 6));
                int segments = file[p + 26];
                int body = p + 27 + segments;
                int last = -1;
                for (int s = 0; s < segments; s++)
                {
                    if (file[p + 27 + s] < 255)
                    {
                        last = s;
                    }
                }

                for (int s = 0; s < segments; s++)
                {
                    int length = file[p + 27 + s];
                    current.AddRange(file.AsSpan(body, length));
                    body += length;
                    if (length < 255)
                    {
                        bool lastOnPage = s == last;
                        packets.Add(new OggPacket([.. current], lastOnPage ? granule : -1, lastOnPage && eos));
                        current.Clear();
                    }
                }

                p = body;
            }

            return packets;
        }
    }
}
