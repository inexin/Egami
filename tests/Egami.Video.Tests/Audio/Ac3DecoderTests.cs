using System.IO.Compression;
using System.Text.Json;

using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The AC-3 decoder against FFmpeg's float decode of FFmpeg-encoded streams (tools/video/make-audio-corpus.py --ac3-decode): 32 to
/// 640 kbit/s, every channel mode FFmpeg's encoder writes, LFE on and off, coupling on and off. Each clip must reach an SNR of 90 dB
/// and stay within 2^-14 of full scale on every sample.
/// </summary>
public class Ac3DecoderTests(ITestOutputHelper log)
{
    private const float Tolerance = 1f / 16384;

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "corpus", "audio", "ac3-decode");

    public static TheoryData<string> Clips => [.. Manifest().Keys.Where(n => !n.StartsWith("dither-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Clips))]
    public void DecodesLikeFfmpeg(string name)
    {
        JsonElement entry = Manifest()[name];
        int channels = entry.GetProperty("channels").GetInt32();
        float[] expected = ReadReference(Path.Combine(Directory, name + ".f32.gz"));
        float[][] got = Mp3FileTests.Decode(Path.Combine(Directory, name));
        Assert.Equal(channels, got.Length);
        int samples = expected.Length / channels;
        Assert.Equal(samples, got[0].Length);
        double signal = 0;
        double noise = 0;
        float worst = 0;
        for (int i = 0; i < samples; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                float e = expected[(i * channels) + c];
                float d = got[c][i] - e;
                signal += (double)e * e;
                noise += (double)d * d;
                worst = Math.Max(worst, Math.Abs(d));
            }
        }

        double snr = noise == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise);
        log.WriteLine($"{name}: SNR {snr:F1} dB, max difference {worst:E2}");
        Assert.True(snr >= 90, $"{name}: SNR {snr:F1} dB against FFmpeg.");
        Assert.True(worst <= Tolerance, $"{name}: samples differ from FFmpeg by up to {worst:E2}.");
    }

    [Fact]
    public void TracksHaveTheStreamLayout()
    {
        using AudioReader reader = AudioReader.Open(Path.Combine(Directory, "3-2-lfe-384k.ac3"));
        Assert.Equal(ChannelLayout.Surround51Side, reader.Layout);
        Assert.Equal(48000, reader.SampleRate);
    }

    [Theory]
    [InlineData("dither-stereo-96k.ac3")]
    [InlineData("dither-3-2-lfe-448k.ac3")]
    public void DithersZeroBitMantissasAsFfmpegDoes(string name)
    {
        // FFmpeg's dither is random, so only its level can be compared: the noise each decoder adds over the undithered decode.
        byte[] stream = File.ReadAllBytes(Path.Combine(Directory, name));
        float[][] dithered = Decode(stream);
        float[][] clear = Decode(Ac3Undither.ClearDither(stream));
        float[] ffmpeg = ReadReference(Path.Combine(Directory, name + ".f32.gz"));
        int channels = dithered.Length;
        double ours = 0;
        double theirs = 0;
        for (int i = 0; i < clear[0].Length; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                ours += Math.Pow(dithered[c][i] - clear[c][i], 2);
                theirs += Math.Pow(ffmpeg[(i * channels) + c] - clear[c][i], 2);
            }
        }

        double ratio = Math.Sqrt(ours / theirs);
        log.WriteLine($"{name}: dither level {ratio:F3} of FFmpeg's");
        Assert.InRange(ratio, 0.5, 2);
    }

    [Fact]
    public void MixesDownWithTheStreamsLevels()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Directory, "3-2-lfe-384k.ac3"));
        float[][] native = Decode(stream); // FL FR FC LFE SL SR
        float[][] stereo = Decode(stream, new Ac3DecoderOptions { Downmix = ChannelLayout.Stereo });
        float[][] mono = Decode(stream, new Ac3DecoderOptions { Downmix = ChannelLayout.Mono });
        Assert.Equal(2, stereo.Length);
        Assert.Single(mono);

        // FFmpeg's encoder writes cmixlev -4.5 dB and surmixlev -6 dB by default.
        const float Clev = 0.59460356f;
        const float Slev = 0.5f;
        const float Minus3 = 0.70710678f;
        float lo = 1 + Clev + Slev;
        float m = (2 * Minus3) + (Clev * 2 * Minus3) + (2 * Slev * Minus3);
        for (int i = 0; i < native[0].Length; i++)
        {
            float l = native[0][i], r = native[1][i], c = native[2][i], ls = native[4][i], rs = native[5][i];
            Assert.Equal((l + (Clev * c) + (Slev * ls)) / lo, stereo[0][i], 1e-5f);
            Assert.Equal((r + (Clev * c) + (Slev * rs)) / lo, stereo[1][i], 1e-5f);
            Assert.Equal(((Minus3 * (l + r)) + (Clev * 2 * Minus3 * c) + (Slev * Minus3 * (ls + rs))) / m, mono[0][i], 1e-5f);
        }
    }

    [Fact]
    public void MixesInLayoutsWithMoreSpeakers()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Directory, "stereo-160k-nocpl.ac3"));
        float[][] native = Decode(stream);
        float[][] surround = Decode(stream, new Ac3DecoderOptions { Downmix = ChannelLayout.Surround51Side });
        Assert.Equal(6, surround.Length);
        Assert.Equal(native[0], surround[0]);
        Assert.Equal(native[1], surround[1]);
        Assert.All(surround[2], v => Assert.Equal(0, v));
    }

    [Theory]
    [InlineData(0x00, 1f)]
    [InlineData(0xE0, 0.5f)] // X = -1: 0 dB from the shift, times one half
    [InlineData(0x20, 2f)] // X = 1: +12 dB, times one half
    [InlineData(0x1F, 63f / 32)]
    [InlineData(0x80, 1f / 16)] // X = -4: -18 dB, times one half
    public void ReadsDynamicRangeWords(int code, float gain)
    {
        Assert.Equal(gain, Egami.Video.Audio.Codecs.Ac3.Ac3FrameDecoder.DynamicGain(code, 1), 1e-6f);
        Assert.Equal(1f, Egami.Video.Audio.Codecs.Ac3.Ac3FrameDecoder.DynamicGain(code, 0));
        Assert.Equal(MathF.Sqrt(gain), Egami.Video.Audio.Codecs.Ac3.Ac3FrameDecoder.DynamicGain(code, 0.5f), 1e-5f);
    }

    [Fact]
    public void HandlesDamagedFramesAsTheOptionsSay()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Directory, "stereo-96k-cpl.ac3"));
        int frame = Egami.Video.Audio.Codecs.Ac3.Ac3Header.TryRead(stream, out var header) ? header.FrameBytes : 0;
        stream[frame + 100] ^= 0x40; // inside the second frame
        Assert.Throws<CorruptDataException>(() => Decode(stream, errors: DecodeErrorMode.Throw));

        float[][] concealed = Decode(stream, errors: DecodeErrorMode.Conceal);
        Assert.Equal(stream.Length / frame * 1536, concealed[0].Length);
        Assert.All(concealed[0].AsSpan(1536, 1536).ToArray(), v => Assert.Equal(0, v));

        float[][] skipped = Decode(stream, errors: DecodeErrorMode.Skip);
        Assert.Equal(concealed[0].Length - 1536, skipped[0].Length);
    }

    [Theory]
    [InlineData("ac3/ac3-stereo-48k.ac3", 2)]
    [InlineData("ac3/ac3-51-48k.ac3", 6)]
    [InlineData("ac3/ac3-mono-44k.ac3", 1)]
    [InlineData("ac3/mka-ac3-stereo.mka", 2)]
    [InlineData("ac3/mp4-ac3-51.mp4", 6)]
    [InlineData("caf/caf-ac3-stereo.caf", 2)]
    public void DecodesTracksInEveryContainer(string clip, int channels)
    {
        float[][] decoded = Mp3FileTests.Decode(Path.Combine(AppContext.BaseDirectory, "corpus", "audio", clip));
        Assert.Equal(channels, decoded.Length);
        Assert.True(decoded[0].Length >= 10 * 1536, $"{clip}: {decoded[0].Length} samples.");
        Assert.Contains(decoded[0], v => Math.Abs(v) > 0.05f);
    }

    [Fact]
    public void RefusesBadOptions()
    {
        AudioTrackInfo track = new() { Number = 1, Kind = TrackKind.Audio, CodecId = CodecIds.Ac3, TimeBase = new Rational(1, 48000), SampleRate = 48000, Channels = 2 };
        Assert.Throws<ArgumentException>(() => Ac3Codec.Instance.CreateDecoder(track, new AudioDecoderOptions { Ac3 = new Ac3DecoderOptions { DynamicRange = -1 } }, MediaContext.Default));
        Assert.Throws<ArgumentException>(() => Ac3Codec.Instance.CreateDecoder(track, new AudioDecoderOptions { Ac3 = new Ac3DecoderOptions { Downmix = ChannelLayout.Unpositioned(2) } }, MediaContext.Default));
    }

    private static float[][] Decode(byte[] stream, Ac3DecoderOptions? ac3 = null, DecodeErrorMode errors = DecodeErrorMode.Throw)
    {
        AudioReaderOptions options = new() { Errors = errors, Decoder = new AudioDecoderOptions { Ac3 = ac3 } };
        using AudioReader reader = AudioReader.Open(new MemoryStream(stream), options);
        List<float>[] channels = [.. Enumerable.Range(0, reader.Layout.ChannelCount).Select(_ => new List<float>())];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    channels[c].AddRange(frame.AsSpan<float>(c).ToArray());
                }
            }
        }

        return [.. channels.Select(c => c.ToArray())];
    }

    private static Dictionary<string, JsonElement> Manifest() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Directory, "manifest.json")))!;

    private static float[] ReadReference(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw.ToArray())];
    }
}

/// <summary>
/// Rewrites FFmpeg-encoded AC-3 streams with every dithflag cleared and both CRCs made good again, so that zero-bit mantissas
/// decode as zeros in every decoder: FFmpeg fills them with its own random dither otherwise, which no other decoder can match.
/// tools/video/make-audio-corpus.py --ac3-decode runs it (EGAMI_AC3_UNDITHER=folder) between encoding and FFmpeg's decode.
/// </summary>
public class Ac3Undither
{
    [Fact]
    public void RewritesTheFolder()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_AC3_UNDITHER");
        if (string.IsNullOrEmpty(folder))
        {
            return;
        }

        foreach (string path in System.IO.Directory.GetFiles(folder, "*.ac3"))
        {
            string name = Path.GetFileName(path);
            byte[] stream = File.ReadAllBytes(path);
            if (name.StartsWith("dual-mono-", StringComparison.Ordinal))
            {
                stream = ToDualMono(stream);
            }

            if (!name.StartsWith("dither-", StringComparison.Ordinal))
            {
                stream = ClearDither(stream);
            }

            File.WriteAllBytes(path, stream);
        }
    }

    [Fact]
    public void KeepsTheCrcsGood()
    {
        byte[] stream = File.ReadAllBytes(Path.Combine(Ac3DecoderTests.Directory, "dither-stereo-96k.ac3"));
        byte[] clear = ClearDither(stream);
        int at = 0;
        int frames = 0;
        while (at < clear.Length && Egami.Video.Audio.Codecs.Ac3.Ac3Header.TryRead(clear.AsSpan(at), out var h))
        {
            Egami.Video.Audio.Codecs.Ac3.Ac3Crc.Check(clear.AsSpan(at, h.FrameBytes), out bool crc1, out bool crc2);
            Assert.True(crc1 && crc2);
            at += h.FrameBytes;
            frames++;
        }

        Assert.True(frames > 0);
        Assert.NotEqual(stream, clear);
    }

    internal static byte[] ClearDither(byte[] stream)
    {
        byte[] output = (byte[])stream.Clone();
        Egami.Video.Audio.Codecs.Ac3.Ac3FrameDecoder decoder = new() { DitherFlagBits = [] };
        float[][] samples = [.. Enumerable.Range(0, 6).Select(_ => new float[1536])];
        int at = 0;
        while (at + 8 <= output.Length && Egami.Video.Audio.Codecs.Ac3.Ac3Header.TryRead(output.AsSpan(at), out var header))
        {
            Span<byte> frame = output.AsSpan(at, header.FrameBytes);
            decoder.DitherFlagBits!.Clear();
            Assert.True(decoder.Decode(frame, samples));
            foreach (long bit in decoder.DitherFlagBits)
            {
                frame[(int)(bit >> 3)] &= (byte)~(0x80 >> (int)(bit & 7));
            }

            FixCrcs(frame);
            at += header.FrameBytes;
        }

        return output;
    }

    /// <summary>
    /// Turns a 2/0 stream without coupling into 1+1 (dual mono), which FFmpeg's encoder does not write: acmod becomes 0, dsurmod
    /// goes, the second program's dialnorm, compr2e, langcod2e and audprodi2e come after the first's, each block gains dynrng2e
    /// and loses its rematrixing fields, and the auxiliary bits absorb the difference. The channels keep their coded spectra.
    /// </summary>
    internal static byte[] ToDualMono(byte[] stream)
    {
        byte[] output = (byte[])stream.Clone();
        Egami.Video.Audio.Codecs.Ac3.Ac3FrameDecoder decoder = new() { Marks = [] };
        float[][] samples = [.. Enumerable.Range(0, 6).Select(_ => new float[1536])];
        int at = 0;
        while (at + 8 <= stream.Length && Egami.Video.Audio.Codecs.Ac3.Ac3Header.TryRead(stream.AsSpan(at), out var header))
        {
            byte[] frame = stream.AsSpan(at, header.FrameBytes).ToArray();
            decoder.Marks!.Clear();
            Assert.True(decoder.Decode(frame, samples));
            Assert.Equal(2, decoder.Acmod);
            List<(string Name, long Bit)> marks = decoder.Marks;
            List<bool> bits = [];
            long from = 0;
            void Copy(long to)
            {
                for (long b = from; b < to; b++)
                {
                    bits.Add(((frame[(int)(b >> 3)] >> (7 - (int)(b & 7))) & 1) != 0);
                }

                from = to;
            }

            void Insert(int value, int count)
            {
                for (int b = count - 1; b >= 0; b--)
                {
                    bits.Add(((value >> b) & 1) != 0);
                }
            }

            foreach ((string name, long bit) in marks)
            {
                switch (name)
                {
                    case "dsurmod":
                        Copy(bit);
                        from = bit + 2;
                        break;
                    case "program2":
                        Copy(bit);
                        Insert(0b11111_000, 8); // dialnorm2 31, no compr2, langcod2 or audio production information
                        break;
                    case "dynrng2":
                        Copy(bit);
                        Insert(0, 1);
                        break;
                    case "remat":
                        Copy(bit);
                        break;
                    case "remat-end":
                        from = bit;
                        break;
                    case "audio-end":
                        Copy(bit);
                        break;
                }
            }

            int total = header.FrameBytes * 8;
            Assert.True(bits.Count <= total - 18, "The frame has too few auxiliary bits to take the second program's fields.");
            while (bits.Count < total)
            {
                bits.Add(false); // auxbits, auxdatae, crcrsv and crc2, set below
            }

            for (int b = 48; b < 51; b++)
            {
                bits[b] = false; // acmod 0
            }

            Span<byte> target = output.AsSpan(at, header.FrameBytes);
            for (int i = 0; i < target.Length; i++)
            {
                int v = 0;
                for (int b = 0; b < 8; b++)
                {
                    v = (v << 1) | (bits[(i * 8) + b] ? 1 : 0);
                }

                target[i] = (byte)v;
            }

            FixCrcs(target);
            at += header.FrameBytes;
        }

        return output;
    }

    /// <summary>Sets crc1 so the CRC of the first 5/8 is zero, then crc2 (the last word) so that of the whole frame is too.</summary>
    private static void FixCrcs(Span<byte> frame)
    {
        int words = frame.Length / 2;
        int end = ((words >> 1) + (words >> 3)) * 2;
        Span<byte> region = frame[2..end];
        region[0] = region[1] = 0;
        ushort target = Crc(region);

        // The CRC is linear: find the crc1 whose contribution through the rest of the region cancels the rest's own.
        Span<ushort> basis = stackalloc ushort[16];
        byte[] probe = new byte[region.Length];
        for (int b = 0; b < 16; b++)
        {
            Array.Clear(probe);
            probe[b >> 3] = (byte)(0x80 >> (b & 7));
            basis[b] = Crc(probe);
        }

        for (int v = 0; v < 65536; v++)
        {
            ushort t = 0;
            for (int b = 0; b < 16; b++)
            {
                if (((v >> (15 - b)) & 1) != 0)
                {
                    t ^= basis[b];
                }
            }

            if (t == target)
            {
                region[0] = (byte)(v >> 8);
                region[1] = (byte)v;
                break;
            }
        }

        ushort crc2 = Crc(frame[2..^2]);
        frame[^2] = (byte)(crc2 >> 8);
        frame[^1] = (byte)crc2;
    }

    private static ushort Crc(ReadOnlySpan<byte> data)
    {
        int crc = 0;
        foreach (byte b in data)
        {
            crc ^= b << 8;
            for (int i = 0; i < 8; i++)
            {
                crc = (crc & 0x8000) != 0 ? (crc << 1) ^ 0x8005 : crc << 1;
            }
        }

        return (ushort)crc;
    }
}

/// <summary>
/// The inverse transform against A/52's forward transform (section 8.2.3.2): blocks windowed, transformed long or as two short
/// halves at random and inverse transformed must overlap-add back to the input, which checks the 256-point path the FFmpeg
/// encoder never uses.
/// </summary>
public class Ac3TransformTests
{
    [Fact]
    public void LongAndShortBlocksReconstructTheInput()
    {
        const int Blocks = 24;
        Random random = new(7);
        double[] input = new double[256 * (Blocks + 1)];
        for (int i = 0; i < input.Length; i++)
        {
            input[i] = (random.NextDouble() * 2) - 1;
        }

        float[] w = Egami.Video.Audio.Codecs.Ac3.Ac3Tables.Window;
        Egami.Video.Audio.Codecs.Ac3.Ac3Imdct imdct = new();
        float[] delay = new float[256];
        float[] output = new float[256];
        float[] coefficients = new float[256];
        double worst = 0;
        for (int b = 0; b < Blocks; b++)
        {
            double[] x = new double[512];
            for (int n = 0; n < 512; n++)
            {
                x[n] = input[(256 * b) + n] * w[n < 256 ? n : 511 - n];
            }

            bool shortBlocks = random.Next(2) == 1;
            if (shortBlocks)
            {
                double[] first = Forward(x.AsSpan(0, 256), -1);
                double[] second = Forward(x.AsSpan(256, 256), 1);
                for (int k = 0; k < 128; k++)
                {
                    coefficients[2 * k] = (float)first[k];
                    coefficients[(2 * k) + 1] = (float)second[k];
                }
            }
            else
            {
                double[] all = Forward(x, 0);
                for (int k = 0; k < 256; k++)
                {
                    coefficients[k] = (float)all[k];
                }
            }

            imdct.Transform(coefficients, shortBlocks, delay, output);
            if (b > 0)
            {
                for (int n = 0; n < 256; n++)
                {
                    worst = Math.Max(worst, Math.Abs(output[n] - input[(256 * b) + n]));
                }
            }
        }

        Assert.True(worst < 1e-4, $"The transform pair is off by up to {worst:E2}.");
    }

    /// <summary>XD[k] = -2/N sum x[n] cos(2 pi / 4N (2n + 1)(2k + 1) + pi / 4 (2k + 1)(1 + alpha)), for k below N/2.</summary>
    private static double[] Forward(ReadOnlySpan<double> x, int alpha)
    {
        int n = x.Length;
        double[] result = new double[n / 2];
        for (int k = 0; k < n / 2; k++)
        {
            double sum = 0;
            for (int i = 0; i < n; i++)
            {
                sum += x[i] * Math.Cos((2 * Math.PI / (4 * n) * ((2 * i) + 1) * ((2 * k) + 1)) + (Math.PI / 4 * ((2 * k) + 1) * (1 + alpha)));
            }

            result[k] = -2.0 / n * sum;
        }

        return result;
    }
}
