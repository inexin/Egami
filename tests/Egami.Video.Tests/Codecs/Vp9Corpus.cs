using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;

using Egami.Video.Codecs.Vp9;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>The VP9 clips of tools/video/make-vp9-corpus.py, the fetched test vectors, and what libvpx gave for them.</summary>
internal static class Vp9Corpus
{
    /// <summary>The committed clips, by name without extension, from the manifest (alpha clips apart).</summary>
    public static IEnumerable<string> Clips(bool alpha = false)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Corpus.Directory, "vp9", "manifest.json")));
        return [.. json.RootElement.GetProperty("clips").EnumerateObject().Select(c => c.Name).Where(n => n.StartsWith("alpha-", StringComparison.Ordinal) == alpha)];
    }

    public static string PathOf(string name) => Path.Combine(Corpus.Directory, "vp9", name);

    /// <summary>The fetched test vectors (.cache/video/vp9/vectors), or an empty list when make-vp9-corpus.py has not fetched them.</summary>
    public static IReadOnlyList<string> Vectors
    {
        get
        {
            string? cache = Corpus.FindCache(Path.Combine("vp9", "vectors"));
            return cache is null
                ? []
                : [.. Directory.GetFiles(cache).Where(f => f.EndsWith(".webm", StringComparison.Ordinal) || f.EndsWith(".ivf", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The reference file of a clip or vector: beside a clip, or in .cache/video/vp9/reference for a vector.</summary>
    public static string ReferencePath(string file, string suffix)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string beside = Path.Combine(Path.GetDirectoryName(file)!, name + suffix);
        if (File.Exists(beside))
        {
            return beside;
        }

        return Path.Combine(Corpus.FindCache(Path.Combine("vp9", "reference"))!, name + suffix);
    }

    /// <summary>vpxdec's MD5 of every shown frame of a clip or vector.</summary>
    public static Vp9Reference ReadReference(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(ReferencePath(file, ".frames.json")));
        JsonElement root = json.RootElement;
        return new Vp9Reference(
            root.GetProperty("packets").GetInt32(),
            [.. root.GetProperty("shown").EnumerateArray().Select(e => e.GetInt32())],
            [.. root.GetProperty("errors").EnumerateArray().Select(e => e.GetInt32())],
            [.. root.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)],
            root.GetProperty("ffmpeg").ValueKind == JsonValueKind.True);
    }

    /// <summary>The frames vp9dump printed for a clip or vector (each frame libvpx decoded, superframes split), as field maps.</summary>
    public static List<Dictionary<string, string>> ReadDump(string file)
    {
        List<Dictionary<string, string>> frames = [];
        using FileStream stream = File.OpenRead(ReferencePath(file, ".dump.gz"));
        using GZipStream gzip = new(stream, CompressionMode.Decompress);
        using StreamReader reader = new(gzip);
        Dictionary<string, string>? frame = null;
        List<string>? blocks = null;
        while (reader.ReadLine() is { } line)
        {
            int space = line.IndexOf(' ', StringComparison.Ordinal);
            string key = space < 0 ? line : line[..space];
            string value = space < 0 ? "" : line[(space + 1)..];
            if (key == "frame")
            {
                frame = [];
                blocks = [];
                frames.Add(frame);
                frame["blocks"] = "";
            }
            else if (key == "b" && frame is not null)
            {
                blocks!.Add(value);
                frame["blocks"] = string.Join('\n', blocks);
            }
            else if (key is "packet" or "shown" or "error")
            {
                continue;
            }
            else if (frame is not null)
            {
                frame[key] = value;
            }
        }

        return frames;
    }

    /// <summary>The packets of a file's video track.</summary>
    public static List<byte[]> Packets(string path)
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        int track = demuxer.Info.VideoTrack!.Number;
        List<byte[]> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                if (packet.TrackNumber == track)
                {
                    packets.Add(packet.Data.ToArray());
                }
            }
        }

        return packets;
    }

    /// <summary>The MD5 of a frame's planes at its coded size, packed as libvpx's test MD5s hash them (16-bit little-endian above 8 bits).</summary>
    public static string Md5(object frame) => frame switch
    {
        Vp9Frame<byte> f => Md5(f),
        Vp9Frame<ushort> f => Md5(f),
        _ => throw new ArgumentException("Not a VP9 frame.", nameof(frame)),
    };

    private static string Md5<T>(Vp9Frame<T> frame)
        where T : unmanaged
    {
        using IncrementalHash md5 = IncrementalHash.CreateHash(HashAlgorithmName.MD5);
        for (int p = 0; p < 3; p++)
        {
            int w = frame.CropWidth(p), h = frame.CropHeight(p), stride = frame.Strides[p];
            Span<T> plane = frame.Plane(p);
            for (int y = 0; y < h; y++)
            {
                md5.AppendData(MemoryMarshal.AsBytes(plane.Slice(y * stride, w)));
            }
        }

        return Convert.ToHexStringLower(md5.GetHashAndReset());
    }

    /// <summary>Decodes a file with the VP9 core on up to <paramref name="threads"/> threads and returns the MD5 of every frame its packets show.</summary>
    public static List<string> ShownMd5s(string path, Action<object, byte[]>? frameDecoded = null, int threads = 1)
    {
        List<string> md5s = [];
        using Vp9StreamDecoder decoder = new(EgamiContext.Default.Memory) { FrameDecoded = frameDecoded, MaxParallelism = threads };
        foreach (byte[] packet in Packets(path))
        {
            try
            {
                decoder.Decode(packet);
            }
            catch (CorruptDataException)
            {
                continue;
            }

            if (!decoder.NeedsResync && (decoder.Shown8 ?? (object?)decoder.Shown16) is { } shown)
            {
                md5s.Add(Md5(shown));
            }
        }

        return md5s;
    }

    public static string FirstDifference(IReadOnlyList<string> expected, IReadOnlyList<string> actual)
    {
        for (int i = 0; i < Math.Min(expected.Count, actual.Count); i++)
        {
            if (expected[i] != actual[i])
            {
                return $"frame {i}";
            }
        }

        return expected.Count == actual.Count ? "none" : $"frame {Math.Min(expected.Count, actual.Count)} (count)";
    }
}

internal sealed record Vp9Reference(int Packets, IReadOnlyList<int> Shown, IReadOnlyList<int> Errors, IReadOnlyList<string> Md5, bool FfmpegAgrees);
