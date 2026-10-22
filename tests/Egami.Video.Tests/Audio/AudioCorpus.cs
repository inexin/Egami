using System.Security.Cryptography;
using System.Text.Json;

using Egami.Video.Audio;

namespace Egami.Video.Tests.Audio;

/// <summary>The clips in tests/video/corpus/audio and their decoded MD5s (see tools/video/make-audio-corpus.py).</summary>
internal static class AudioCorpus
{
    public static string Directory => Path.Combine(AppContext.BaseDirectory, "corpus", "audio");

    public static string PathOf(string name) => Path.Combine(Directory, name);

    public static IReadOnlyDictionary<string, Reference> Manifest { get; } = Load(Path.Combine(Directory, "manifest.json"));

    public static IEnumerable<object[]> Clips => Manifest.Keys.Order(StringComparer.Ordinal).Select(n => new object[] { n });

    /// <summary>The IETF flac-test-files fetched by make-audio-corpus.py --ietf, or null when they are not there.</summary>
    public static string? IetfDirectory
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ".cache", "audio", "flac-test-files");
                if (File.Exists(Path.Combine(candidate, "reference.json")))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static Dictionary<string, Reference> Load(string path)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(path));
        Dictionary<string, Reference> result = [];
        foreach (JsonProperty clip in json.RootElement.EnumerateObject())
        {
            JsonElement e = clip.Value;
            if (e.TryGetProperty("decodes", out JsonElement decodes) && !decodes.GetBoolean())
            {
                result[clip.Name] = new Reference(0, 0, 0, 0, false, "");
                continue;
            }

            result[clip.Name] = new Reference(
                e.GetProperty("samples").GetInt64(),
                e.GetProperty("rate").GetInt32(),
                e.GetProperty("channels").GetInt32(),
                e.GetProperty("bits").GetInt32(),
                e.TryGetProperty("float", out JsonElement f) && f.GetBoolean(),
                e.GetProperty("md5").GetString()!);
        }

        return result;
    }

    /// <summary>
    /// Hashes frames as FLAC's STREAMINFO MD5 does: every sample interleaved, little-endian, signed, in whole bytes of
    /// <paramref name="bits"/>; float frames as 32-bit floats.
    /// </summary>
    public static (string Md5, long Samples) Hash(IEnumerable<AudioFrame> frames, int bits, bool isFloat)
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        long samples = 0;
        int width = isFloat ? 4 : (bits + 7) / 8;
        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                using AudioFrame planar = frame.Convert(isFloat ? SampleFormat.F32Planar : SampleFormat.S64Planar);
                byte[] buffer = new byte[frame.SampleCount * frame.ChannelCount * width];
                int p = 0;
                int shift = isFloat ? 0 : 64 - bits;
                for (int i = 0; i < frame.SampleCount; i++)
                {
                    for (int c = 0; c < frame.ChannelCount; c++)
                    {
                        if (isFloat)
                        {
                            BitConverter.TryWriteBytes(buffer.AsSpan(p), planar.AsSpan<float>(c)[i]);
                            p += 4;
                        }
                        else
                        {
                            // S64 holds the sample scaled up to 64 bits; back down to the stream's depth.
                            long v = planar.AsSpan<long>(c)[i] >> shift;
                            for (int k = 0; k < width; k++)
                            {
                                buffer[p++] = (byte)(v >> (8 * k));
                            }
                        }
                    }
                }

                md5.AppendData(buffer);
                samples += frame.SampleCount;
            }
        }

        return (Convert.ToHexStringLower(md5.GetHashAndReset()), samples);
    }

    public sealed record Reference(long Samples, int Rate, int Channels, int Bits, bool IsFloat, string Md5);
}
