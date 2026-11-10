using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Audio.Codecs.Aac;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The AAC LC encoder through its own decoder: files that decode to the input's exact length in every container that holds AAC,
/// every channel configuration and rate, the average bit rate held, the variable-bit-rate levels ordered, attacks coded in short
/// windows, and the bitstream pieces (the codebook costs match what is written, the section search never loses to one section
/// per band). FFmpeg's decode of the same files is the egami-compare encoder gate.
/// </summary>
public class AacEncoderTests(ITestOutputHelper log)
{
    public static TheoryData<string, int, int> Files() => new()
    {
        { ".m4a", 2, 48000 },
        { ".m4a", 1, 44100 },
        { ".mka", 2, 32000 },
        { ".aac", 2, 22050 },
        { ".m4a", 6, 48000 },
        { ".mka", 8, 48000 },
        { ".m4a", 2, 96000 },
        { ".aac", 1, 8000 },
    };

    [Theory]
    [MemberData(nameof(Files))]
    public void DecodesToTheInputLength(string extension, int channels, int rate)
    {
        using AudioFrame input = Music(channels, rate, (rate * 3 / 2) + 77);
        string path = AudioRoundTripTests.OutputPath($"aac-{channels}ch-{rate}{extension}");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = AacCodec.Instance }))
        {
            for (int start = 0; start < input.SampleCount; start += 1777)
            {
                using AudioFrame part = input.Slice(start, Math.Min(1777, input.SampleCount - start));
                writer.Write(part);
            }
        }

        AudioTrackInfo track = Assert.Single(MediaFile.Probe(path).Tracks.OfType<AudioTrackInfo>());
        Assert.Equal(CodecIds.Aac, track.CodecId);
        Assert.Equal(channels, track.Channels);
        // ADTS has no edit list: the priming and padding stay in the file.
        bool adts = extension == ".aac";
        double snr = DecodedSnr(input, path, adts ? 1024 : 0, out int length);
        log.WriteLine($"{extension} {channels} ch {rate} Hz: {snr:F1} dB");
        if (adts)
        {
            Assert.InRange(length, input.SampleCount, input.SampleCount + 1023);
        }
        else
        {
            Assert.Equal(input.SampleCount, length);
        }

        // A perceptual coder keeps the noise under the masking threshold, not far below the signal.
        Assert.True(snr > 8, $"The decoded audio is {snr:F1} dB from the input.");
    }

    [Theory]
    [InlineData(64_000, 2)]
    [InlineData(128_000, 2)]
    [InlineData(32_000, 1)]
    [InlineData(256_000, 2)]
    public void HoldsTheAverageBitrate(int bitrate, int channels)
    {
        int[] sizes = PacketSizes(new AacEncoderOptions { Bitrate = bitrate }, channels, 48000 * 8, Music);
        double average = sizes.Average() * 8 * 48000 / 1024;
        log.WriteLine($"{bitrate} bit/s asked, {average:F0} bit/s coded");
        Assert.InRange(average, bitrate * 0.9, bitrate * 1.03);
    }

    [Fact]
    public void QualityLevelsGrowInSize()
    {
        double previous = 0;
        for (int quality = 1; quality <= 5; quality++)
        {
            double average = PacketSizes(new AacEncoderOptions { Quality = quality }, 2, 48000 * 4, Music).Average() * 8 * 48000 / 1024;
            log.WriteLine($"quality {quality}: {average:F0} bit/s");
            Assert.True(average > previous * 1.05, $"Quality {quality} codes {average:F0} bit/s, not more than {previous:F0}.");
            previous = average;
        }
    }

    [Fact]
    public void AttacksUseShortWindowsAndStayClean()
    {
        // Clicks over silence: the noise of a long window would smear 20 ms ahead of each click.
        int rate = 48000;
        using AudioFrame input = new(SampleFormat.F32Planar, ChannelLayout.Mono, rate, rate * 2, MediaTime.Zero);
        Span<float> plane = input.GetSpan<float>(0);
        for (int click = 5000; click < plane.Length - 2000; click += 9000)
        {
            for (int i = 0; i < 1500; i++)
            {
                plane[click + i] = 0.7f * MathF.Exp(-i / 200f) * MathF.Sin(i * 0.3f);
            }
        }

        string path = AudioRoundTripTests.OutputPath("aac-clicks.m4a");
        using (AudioWriter writer = AudioWriter.Create(path, new AudioWriterOptions { Codec = AacCodec.Instance, Aac = new AacEncoderOptions { Bitrate = 96_000 } }))
        {
            writer.Write(input);
        }

        // The coding noise stays within the short window of each click: the silence from a long window's span before it to
        // two short windows before it stays silent.
        float[] decoded = DecodeMono(path);
        double ahead = 0;
        for (int click = 5000; click < plane.Length - 2000; click += 9000)
        {
            for (int i = click - 1024; i < click - 256; i++)
            {
                ahead = Math.Max(ahead, Math.Abs(decoded[i]));
            }
        }

        log.WriteLine($"largest sample ahead of a click: {ahead:F4}");
        Assert.True(ahead < 0.002, $"Pre-echo of {ahead:F4} ahead of the clicks.");
    }

    [Fact]
    public void SilenceCodesSmallBlocks()
    {
        int[] sizes = PacketSizes(AacEncoderOptions.Default, 2, 48000, (channels, rate, samples) =>
            new AudioFrame(SampleFormat.F32Planar, ChannelLayout.ForChannelCount(channels), rate, samples, MediaTime.Zero));
        Assert.All(sizes, size => Assert.True(size < 16, $"A silent block took {size} bytes."));
    }

    [Fact]
    public void CodebookCostsMatchTheWrittenBits()
    {
        Random random = new(3);
        AacBitWriter writer = new(64);
        for (int cb = 1; cb <= 11; cb++)
        {
            int largest = cb == 11 ? 3000 : AacSpectrumCoder.Largest[cb];
            int[] lines = new int[32];
            for (int trial = 0; trial < 50; trial++)
            {
                for (int i = 0; i < lines.Length; i++)
                {
                    lines[i] = random.Next(-largest, largest + 1);
                }

                writer.Reset();
                AacSpectrumCoder.Write(writer, cb, lines);
                Assert.Equal(writer.Position, AacSpectrumCoder.Bits(cb, lines));
            }
        }
    }

    [Fact]
    public void SectionsNeverCostMoreThanOnePerBand()
    {
        Random random = new(5);
        int bands = 40;
        int[] starts = new int[bands + 1];
        for (int b = 0; b < bands; b++)
        {
            starts[b + 1] = starts[b] + (4 * (1 + random.Next(4)));
        }

        int[] lines = new int[starts[bands]];
        byte[] books = new byte[bands];
        int naive = 0;
        for (int b = 0; b < bands; b++)
        {
            int largest = random.Next(4) == 0 ? 0 : random.Next(1, 20);
            for (int k = starts[b]; k < starts[b + 1]; k++)
            {
                lines[k] = random.Next(-largest, largest + 1);
            }

            int peak = AacSpectrumCoder.Peak(lines.AsSpan(starts[b], starts[b + 1] - starts[b]));
            books[b] = (byte)(peak == 0 ? 0 : 1);
            naive += AacSpectrumCoder.SectionBits(1, shortWindows: false)
                + (peak == 0 ? 0 : AacSpectrumCoder.Bits(AacSpectrumCoder.FirstBook(peak), lines.AsSpan(starts[b], starts[b + 1] - starts[b])));
        }

        int best = AacSpectrumCoder.ChooseSections(lines, starts, books, shortWindows: false, new int[13 * (bands + 1)]);
        log.WriteLine($"one section per band {naive} bits, chosen {best}");
        Assert.True(best <= naive);

        // The chosen books code their bands and the written section data matches the count.
        AacBitWriter writer = new(1024);
        AacSpectrumCoder.WriteSections(writer, books, shortWindows: false);
        int spectral = 0;
        for (int b = 0; b < bands; b++)
        {
            ReadOnlySpan<int> band = lines.AsSpan(starts[b], starts[b + 1] - starts[b]);
            Assert.True(books[b] != 0 || AacSpectrumCoder.Peak(band) == 0);
            if (books[b] != 0)
            {
                Assert.True(AacSpectrumCoder.Peak(band) <= AacSpectrumCoder.Largest[books[b]]);
                spectral += AacSpectrumCoder.Bits(books[b], band);
            }
        }

        int joinedZeros = 0;
        for (int b = 0; b < bands; b++)
        {
            if (books[b] != 0 && AacSpectrumCoder.Peak(lines.AsSpan(starts[b], starts[b + 1] - starts[b])) == 0)
            {
                joinedZeros++;
            }
        }

        // A zero band that joins a coded section is priced with its one-bit scalefactor delta.
        Assert.Equal(best, writer.Position + spectral + joinedZeros);
    }

    /// <summary>A melody of harmonic notes with a soft attack and decay, two a second, and a little noise.</summary>
    internal static AudioFrame Music(int channels, int rate, int samples)
    {
        AudioFrame frame = new(SampleFormat.F32Planar, ChannelLayout.ForChannelCount(channels), rate, samples, MediaTime.Zero);
        double[] notes = [220, 247, 262, 294, 330, 349, 392, 440];
        Random random = new(7);
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / rate;
            int note = (int)(t * 2);
            double f = notes[note * 5 % notes.Length];
            double local = t - (note / 2.0);
            double envelope = Math.Min(1, local * 50) * Math.Exp(-local * 3);
            double v = 0;
            for (int h = 1; h <= 6; h++)
            {
                if (f * h < rate / 2.0)
                {
                    v += Math.Sin(2 * Math.PI * f * h * t) / h;
                }
            }

            for (int c = 0; c < channels; c++)
            {
                double noise = (random.NextDouble() - 0.5) * 0.02;
                frame.GetSpan<float>(c)[i] = (float)((0.3 * envelope * v * (1 - (0.1 * c))) + noise);
            }
        }

        return frame;
    }

    private static int[] PacketSizes(AacEncoderOptions options, int channels, int samples, Func<int, int, int, AudioFrame> signal)
    {
        using AudioFrame input = signal(channels, 48000, samples);
        using IAudioEncoder encoder = AacCodec.Instance.CreateEncoder(new AudioEncoderOptions { SampleRate = 48000, Layout = input.Layout, Aac = options }, MediaContext.Default);
        List<int> sizes = [];
        encoder.Send(input);
        encoder.Flush();
        while (encoder.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                sizes.Add(packet.Data.Length);
            }
        }

        return [.. sizes];
    }

    private static float[] DecodeMono(string path)
    {
        List<float> samples = [];
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = SampleFormat.F32Planar });
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                samples.AddRange(frame.AsSpan<float>(0).ToArray());
            }
        }

        return [.. samples];
    }

    /// <summary>The signal-to-noise ratio of the decoded file against the input in dB, and the decoded length, after <paramref name="skip"/> samples.</summary>
    private static double DecodedSnr(AudioFrame input, string path, int skip, out int length)
    {
        using AudioReader reader = AudioReader.Open(path, new AudioReaderOptions { SampleFormat = SampleFormat.F32Planar, Errors = DecodeErrorMode.Throw });
        double signal = 0, noise = 0;
        int offset = -skip;
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                for (int c = 0; c < input.ChannelCount; c++)
                {
                    ReadOnlySpan<float> got = frame.AsSpan<float>(c);
                    ReadOnlySpan<float> want = input.AsSpan<float>(c);
                    for (int i = Math.Max(0, -offset); i < frame.SampleCount && offset + i < want.Length; i++)
                    {
                        double d = got[i] - want[offset + i];
                        signal += want[offset + i] * (double)want[offset + i];
                        noise += d * d;
                    }
                }

                offset += frame.SampleCount;
            }
        }

        length = offset;
        return 10 * Math.Log10(signal / Math.Max(noise, 1e-30));
    }
}
