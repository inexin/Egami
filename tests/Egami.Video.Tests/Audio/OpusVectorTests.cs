using System.Buffers.Binary;

using Egami.Video.Audio.Codecs.Opus;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The RFC 8251 Opus test vectors (fetched by make-audio-corpus.py --opus into .cache/audio/opus-vectors): every vector decodes
/// with the range coder ending each packet where libopus's does, and passes opus_compare against its reference, in mono and stereo.
/// </summary>
public class OpusVectorTests(ITestOutputHelper log)
{
    public static TheoryData<int, int> Vectors()
    {
        TheoryData<int, int> data = [];
        for (int v = 1; v <= 12; v++)
        {
            data.Add(v, 1);
            data.Add(v, 2);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Vectors))]
    public void PassesTheConformanceTest(int vector, int channels)
    {
        if (Directory() is not { } directory)
        {
            Assert.Skip("Run python tools/video/make-audio-corpus.py --opus to fetch the RFC 8251 test vectors.");
            return;
        }

        string name = $"testvector{vector:00}";
        (short[] output, int rangeErrors, int firstMismatch) = Decode(Path.Combine(directory, name + ".bit"), channels);
        Assert.True(rangeErrors == 0, $"{rangeErrors} packets end with another range coder state than libopus's (first at packet {firstMismatch}).");
        double quality = Math.Max(
            OpusCompare.Quality(Read(Path.Combine(directory, name + ".dec")), output, channels),
            OpusCompare.Quality(Read(Path.Combine(directory, name + "m.dec")), output, channels));
        log.WriteLine($"{name} {(channels == 1 ? "mono" : "stereo")}: quality {quality:F1} %");
        Assert.True(quality >= 0, $"Opus quality {quality:F1} %");
    }

    /// <summary>Decodes an opus_demo bitstream as opus_demo -d 48000 does, checking each packet's final range.</summary>
    internal static (short[] Samples, int RangeErrors, int FirstMismatch) Decode(string path, int channels)
    {
        byte[] file = File.ReadAllBytes(path);
        OpusStreamDecoder decoder = new(channels);
        List<short> output = [];
        float[] pcm = new float[5760 * 2 * channels];
        int p = 0;
        int errors = 0, first = -1, packet = 0;
        bool lostPrevious = true;
        while (p + 8 <= file.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(file.AsSpan(p));
            uint range = BinaryPrimitives.ReadUInt32BigEndian(file.AsSpan(p + 4));
            p += 8;
            if (length < 0 || p + length > file.Length)
            {
                break;
            }

            ReadOnlySpan<byte> data = file.AsSpan(p, length);
            p += length;
            bool lost = length == 0;
            int samples = lost
                ? decoder.Decode([], pcm, decoder.LastPacketDuration, false, false, out _)
                : decoder.Decode(data, pcm, 5760 * 2, false, false, out _);
            for (int i = 0; i < Math.Max(0, samples) * channels; i++)
            {
                // FLOAT2INT16, as opus_decode converts.
                float x = pcm[i] * 32768f;
                output.Add((short)Math.Clamp(MathF.Round(x, MidpointRounding.ToEven), -32768, 32767));
            }

            if (range != 0 && !lost && !lostPrevious && decoder.FinalRange != range)
            {
                errors++;
                first = first < 0 ? packet : first;
            }

            lostPrevious = lost;
            packet++;
        }

        return ([.. output], errors, first);
    }

    private static short[] Read(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        short[] samples = new short[bytes.Length / 2];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = BinaryPrimitives.ReadInt16LittleEndian(bytes.AsSpan(2 * i));
        }

        return samples;
    }

    internal static string? Directory()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "audio", "opus-vectors", "opus_newvectors");
            if (File.Exists(Path.Combine(candidate, "testvector01.bit")))
            {
                return candidate;
            }
        }

        return null;
    }
}
