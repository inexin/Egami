using System.IO.Compression;
using System.Text.Json;

using Egami.Video.Audio;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The AAC LC decoder (V5.10 task 3) against FFmpeg's float decode of the clips in tests/video/corpus/audio/aac
/// (tools/video/make-audio-corpus.py --aac): FFmpeg's and Fraunhofer's encoders, 8 to 96 kHz, mono to 7.1, ADTS and MP4. Each LC
/// clip must reach an SNR of 90 dB and stay within 2^-14 of full scale on every sample. Perceptual noise substitution fills its
/// bands from a random generator, which no two decoders share, so the clip that uses it is compared by level instead. HE-AAC
/// clips decode their AAC LC core, upsampled to the output rate, with one "aac-sbr-ignored" warning.
/// </summary>
public class AacDecoderTests(ITestOutputHelper log)
{
    private const float Tolerance = 1f / 16384;

    public static string Directory => Path.Combine(AudioCorpus.Directory, "aac");

    public static TheoryData<string> LowComplexityClips =>
        [.. Manifest().Keys.Where(n => !IsHighEfficiency(n) && !UsesNoise(n)).Order(StringComparer.Ordinal)];

    public static TheoryData<string> HighEfficiencyClips => [.. Manifest().Keys.Where(IsHighEfficiency).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(LowComplexityClips))]
    public void DecodesLikeFfmpeg(string name)
    {
        JsonElement entry = Manifest()[name];
        int channels = entry.GetProperty("decodedChannels").GetInt32();
        float[] expected = ReadReference(name);
        float[][] got = Decode(name, out int rate, out _);
        Assert.Equal(entry.GetProperty("decodedRate").GetInt32(), rate);
        Assert.Equal(channels, got.Length);
        Assert.Equal(expected.Length / channels, got[0].Length);
        (double snr, float worst) = Compare(expected, got);
        log.WriteLine($"{name}: SNR {snr:F1} dB, max difference {worst:E2}");
        Assert.True(snr >= 90, $"{name}: SNR {snr:F1} dB against FFmpeg.");
        Assert.True(worst <= Tolerance, $"{name}: samples differ from FFmpeg by up to {worst:E2}.");
    }

    [Fact]
    public void NoiseSubstitutionHasTheReferenceLevel()
    {
        const string Name = "ff-lc-low-is-pns.aac";
        float[] expected = ReadReference(Name);
        float[][] got = Decode(Name, out _, out _);
        int channels = got.Length;
        double ours = 0;
        double theirs = 0;
        for (int i = 0; i < got[0].Length; i++)
        {
            for (int c = 0; c < channels; c++)
            {
                ours += (double)got[c][i] * got[c][i];
                theirs += (double)expected[(i * channels) + c] * expected[(i * channels) + c];
            }
        }

        double ratio = Math.Sqrt(ours / theirs);
        log.WriteLine($"{Name}: level {ratio:F4} of FFmpeg's");
        Assert.InRange(ratio, 0.97, 1.03);
    }

    [Theory]
    [MemberData(nameof(HighEfficiencyClips))]
    public void DecodesTheCoreOfHighEfficiencyStreams(string name)
    {
        JsonElement entry = Manifest()[name];
        float[] expected = ReadReference(name);
        int channels = entry.GetProperty("decodedChannels").GetInt32();
        float[][] got = Decode(name, out int rate, out List<MediaWarning> warnings);
        Assert.Equal(entry.GetProperty("decodedRate").GetInt32(), rate);
        Assert.Equal(expected.Length / channels, got[0].Length);
        Assert.Equal("aac-sbr-ignored", Assert.Single(warnings).Code);
    }

    [Fact]
    public void DecodesMp4AndAdtsAlike()
    {
        // FFmpeg wrote the same packets into both; the MP4 edit list drops the priming that ADTS keeps.
        float[][] adts = Decode("ff-lc-stereo-44k.aac", out _, out _);
        float[][] mp4 = Decode("ff-lc-stereo.m4a", out _, out _);
        for (int c = 0; c < 2; c++)
        {
            Assert.Equal(adts[c].AsSpan(1024, mp4[c].Length).ToArray(), mp4[c]);
        }
    }

    /// <summary>Decodes a clip through AudioReader into planar float, with its rate and the warnings raised.</summary>
    internal static float[][] Decode(string name, out int rate, out List<MediaWarning> warnings)
    {
        List<MediaWarning> raised = [];
        warnings = raised;
        using AudioReader reader = AudioReader.Open(Path.Combine(Directory, name), new AudioReaderOptions { Errors = DecodeErrorMode.Throw },
            MediaContext.Default with { Warnings = raised.Add });
        rate = reader.SampleRate;
        List<float>[] channels = [.. Enumerable.Range(0, reader.Layout.ChannelCount).Select(_ => new List<float>())];
        foreach (AudioFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                for (int c = 0; c < channels.Length; c++)
                {
                    channels[c].AddRange(frame.AsSpan<float>(c));
                }
            }
        }

        return [.. channels.Select(c => c.ToArray())];
    }

    private static (double Snr, float Worst) Compare(float[] expected, float[][] got)
    {
        int channels = got.Length;
        double signal = 0;
        double noise = 0;
        float worst = 0;
        for (int i = 0; i < got[0].Length; i++)
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

        return (noise == 0 ? double.PositiveInfinity : 10 * Math.Log10(signal / noise), worst);
    }

    private static bool IsHighEfficiency(string name) => name.Contains("-he", StringComparison.Ordinal);

    private static bool UsesNoise(string name) => name.Contains("pns", StringComparison.Ordinal);

    private static Dictionary<string, JsonElement> Manifest() =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(Path.Combine(Directory, "manifest.json")))!;

    private static float[] ReadReference(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(Directory, name + ".f32.gz")), CompressionMode.Decompress);
        using MemoryStream raw = new();
        gzip.CopyTo(raw);
        return [.. System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(raw.ToArray())];
    }
}
