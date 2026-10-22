using System.Globalization;
using System.IO.Compression;
using System.Text.Json;

using Egami.Video.Containers;

namespace Egami.Video.Tests;

/// <summary>The clips in tests/video/corpus and their ffprobe dumps (see tools/video/make-corpus.py).</summary>
internal static class Corpus
{
    private static readonly string[] PlaneNames = ["y", "u", "v", "a"];

    public static string Directory => Path.Combine(AppContext.BaseDirectory, "corpus");

    /// <summary>The OpenDML clip made by make-corpus.py --large, or null when it was not made.</summary>
    public static string? LargeAvi
    {
        get
        {
            for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, ".cache", "video", "corpus-large", "opendml.avi");
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }

            return null;
        }
    }

    public static string PathOf(string name) => Path.Combine(Directory, name);

    /// <summary>The VP8 clips of tools/video/make-vp8-corpus.py, by name without extension, from its manifest.</summary>
    public static IEnumerable<string> Vp8Clips(bool alpha = false)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(Directory, "vp8", "manifest.json")));
        return [.. json.RootElement.GetProperty("clips").EnumerateObject().Select(c => c.Name).Where(n => n.StartsWith("alpha-", StringComparison.Ordinal) == alpha)];
    }

    public static string Vp8PathOf(string name) => Path.Combine(Directory, "vp8", name);

    /// <summary>The fetched VP8 test vectors (.cache/video/vp8/vectors), or an empty list when make-vp8-corpus.py has not fetched them.</summary>
    public static IReadOnlyList<string> Vp8Vectors
    {
        get
        {
            string? cache = FindCache(Path.Combine("vp8", "vectors"));
            return cache is null ? [] : [.. System.IO.Directory.GetFiles(cache, "*.ivf").Order(StringComparer.Ordinal)];
        }
    }

    /// <summary>The expected frames of a VP8 file: its .frames.json beside a clip, or in .cache/video/vp8/reference for a vector.</summary>
    public static Vp8Reference ReadVp8Reference(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Vp8ReferencePath(file, ".frames.json")));
        JsonElement root = json.RootElement;
        if (root.TryGetProperty("packets", out JsonElement packets))
        {
            return new Vp8Reference(
                root.GetProperty("width").GetInt32(),
                root.GetProperty("height").GetInt32(),
                packets.GetInt32(),
                [.. root.GetProperty("shown").EnumerateArray().Select(e => e.GetInt32())],
                [.. root.GetProperty("errors").EnumerateArray().Select(e => e.GetInt32())],
                [.. root.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)],
                root.GetProperty("ffmpeg").ValueKind == JsonValueKind.True,
                []);
        }

        // Alpha clips: an MD5 per plane, y, u, v and a.
        List<string[]> planes = [.. root.GetProperty("md5").EnumerateArray().Select(f => PlaneNames.Select(p => f.GetProperty(p).GetString()!).ToArray())];
        return new Vp8Reference(root.GetProperty("width").GetInt32(), root.GetProperty("height").GetInt32(), planes.Count,
            [.. Enumerable.Range(0, planes.Count)], [], [], false, planes);
    }

    /// <summary>The lines vp8dump printed for each packet of a VP8 file (its .dump.gz), by packet index.</summary>
    public static List<List<string>> ReadVp8Dump(string file)
    {
        using StreamReader reader = new(new GZipStream(File.OpenRead(Vp8ReferencePath(file, ".dump.gz")), CompressionMode.Decompress));
        List<List<string>> packets = [];
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("frame ", StringComparison.Ordinal))
            {
                packets.Add([]);
            }

            if (packets.Count > 0)
            {
                packets[^1].Add(line);
            }
        }

        return packets;
    }

    /// <summary>A folder under the repo's .cache, or null when it does not exist.</summary>
    public static string? FindCache(string relative)
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "video", relative);
            if (System.IO.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private static string Vp8ReferencePath(string file, string extension)
    {
        string name = Path.GetFileNameWithoutExtension(file);
        string beside = Path.Combine(Path.GetDirectoryName(file)!, name + extension);
        return File.Exists(beside) ? beside : Path.Combine(FindCache(Path.Combine("vp8", "reference"))!, name + extension);
    }

    public static IEnumerable<string> Clips(params string[] extensions) =>
        System.IO.Directory.GetFiles(Directory)
            .Where(f => extensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .Order(StringComparer.Ordinal)!;

    public static Probe ReadProbe(string clipPath)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(clipPath + ".json"));
        JsonElement root = json.RootElement;
        List<ProbeStream> streams = [];
        HashSet<int> attachments = [];
        foreach (JsonElement s in root.GetProperty("streams").EnumerateArray())
        {
            // Matroska attachments show up in ffprobe as streams (cover art as a video stream with one packet); they are not tracks.
            if (s.GetProperty("codec_type").GetString() == "attachment" || s.TryGetProperty("attached_pic", out _))
            {
                attachments.Add(s.GetProperty("index").GetInt32());
                continue;
            }

            streams.Add(new ProbeStream(
                s.GetProperty("index").GetInt32(),
                s.GetProperty("codec_type").GetString()!,
                s.GetProperty("codec_name").GetString()!,
                Rational.Parse(s.GetProperty("time_base").GetString()!),
                s.TryGetProperty("width", out JsonElement w) ? w.GetInt32() : 0,
                s.TryGetProperty("height", out JsonElement h) ? h.GetInt32() : 0,
                s.TryGetProperty("sample_rate", out JsonElement sr) ? int.Parse(sr.GetString()!, CultureInfo.InvariantCulture) : 0,
                s.TryGetProperty("channels", out JsonElement ch) ? ch.GetInt32() : 0));
        }

        List<ProbePacket> packets = [];
        string? previousPos = null;
        foreach (JsonElement p in root.GetProperty("packets").EnumerateArray())
        {
            // Frames after the first in a Matroska lace share their block's position.
            string? pos = p.TryGetProperty("pos", out JsonElement position) ? position.GetString() : null;
            packets.Add(new ProbePacket(
                p.GetProperty("stream_index").GetInt32(),
                p.TryGetProperty("pts", out JsonElement pts) ? pts.GetInt64() : null,
                int.Parse(p.GetProperty("size").GetString()!, CultureInfo.InvariantCulture),
                p.GetProperty("flags").GetString()!.Contains('K', StringComparison.Ordinal),
                pos is not null && pos == previousPos));
            previousPos = pos;
        }

        return new Probe(streams, [.. packets.Where(p => !attachments.Contains(p.Stream))]);
    }

    /// <summary>Reads every packet as (track index in Tracks, timestamp, size, keyframe, bytes hash).</summary>
    public static List<ReadPacket> ReadAll(IDemuxer demuxer)
    {
        Dictionary<int, int> indexOf = demuxer.Tracks.Select((t, i) => (t.Number, i)).ToDictionary();
        List<ReadPacket> packets = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                packets.Add(new ReadPacket(indexOf[packet.TrackNumber], packet.Timestamp, packet.Data.Length, packet.IsKeyframe,
                    HashCode.Combine(packet.Data.Length, Hash(packet.Data.Span))));
            }
        }

        return packets;
    }

    /// <summary>Asserts our packets match ffprobe's: same order, stream, timestamp, size and keyframe flag.</summary>
    /// <remarks>
    /// Frames after the first in a lace carry no timestamp of their own. FFmpeg derives one by parsing the codec; we add the
    /// track's DefaultDuration when it has one. Pass <paramref name="checkLacedTimestamps"/> false for tracks without it.
    /// </remarks>
    public static void AssertMatchesProbe(Probe probe, IReadOnlyList<ReadPacket> ours, bool checkKeyframes = true, bool checkLacedTimestamps = true)
    {
        // Compared per stream: FFmpeg's codec parsers can hold a packet back, so its order across streams may differ.
        Assert.Equal(probe.Packets.Count, ours.Count);
        foreach (int stream in probe.Packets.Select(p => p.Stream).Distinct())
        {
            List<ProbePacket> expectedPackets = [.. probe.Packets.Where(p => p.Stream == stream)];
            List<ReadPacket> actualPackets = [.. ours.Where(p => p.Track == stream)];
            Assert.True(expectedPackets.Count == actualPackets.Count, $"stream {stream}: {actualPackets.Count} packets, ffprobe {expectedPackets.Count}");
            for (int i = 0; i < actualPackets.Count; i++)
            {
                ProbePacket expected = expectedPackets[i];
                ReadPacket actual = actualPackets[i];
                Assert.True(expected.Size == actual.Size, $"stream {stream} packet {i}: size {actual.Size}, ffprobe {expected.Size}");
                if (expected.Pts is { } pts && (checkLacedTimestamps || !expected.LacedFollower))
                {
                    MediaTime want = new(pts, probe.Streams[stream].TimeBase);
                    Assert.True(want == actual.Timestamp, $"stream {stream} packet {i}: time {actual.Timestamp}, ffprobe {want}");
                }

                if (checkKeyframes)
                {
                    Assert.True(expected.Key == actual.Key, $"stream {stream} packet {i}: keyframe {actual.Key}, ffprobe {expected.Key}");
                }
            }
        }
    }

    internal static int Hash(ReadOnlySpan<byte> data)
    {
        HashCode hash = new();
        hash.AddBytes(data);
        return hash.ToHashCode();
    }
}

internal sealed record Probe(IReadOnlyList<ProbeStream> Streams, IReadOnlyList<ProbePacket> Packets);

internal sealed record ProbeStream(int Index, string Type, string Codec, Rational TimeBase, int Width, int Height, int SampleRate, int Channels);

internal sealed record ProbePacket(int Stream, long? Pts, int Size, bool Key, bool LacedFollower = false);

internal sealed record ReadPacket(int Track, MediaTime Timestamp, int Size, bool Key, int Hash);

/// <summary>What libvpx decoded from a VP8 file: which packets are shown or broken, and each shown frame's MD5 (packed I420).</summary>
internal sealed record Vp8Reference(int Width, int Height, int Packets, IReadOnlyList<int> Shown, IReadOnlyList<int> Errors,
    IReadOnlyList<string> Md5, bool FfmpegAgrees, IReadOnlyList<string[]> PlaneMd5);
