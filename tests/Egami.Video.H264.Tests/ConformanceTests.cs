using System.Text.Json;

using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.H264.Tests;

/// <summary>
/// Decodes the interlaced and FRExt conformance bitstreams of ITU-T H.264.1 that tools/video/make-h264-conformance.py fetches
/// into .cache (they are not committed) and compares each frame with FFmpeg's decode. Skipped when they are not fetched.
/// </summary>
public sealed class ConformanceTests
{
    private static readonly string? Root = FindRoot();

    public static TheoryData<string> Streams
    {
        get
        {
            TheoryData<string> streams = [];
            if (Root is null)
            {
                streams.Add("");
                return streams;
            }

            using JsonDocument refs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root, "refs.json")));
            foreach (JsonProperty stream in refs.RootElement.EnumerateObject())
            {
                streams.Add(stream.Name);
            }

            return streams;
        }
    }

    [Theory]
    [MemberData(nameof(Streams))]
    public void StreamMatchesFfmpeg(string stream)
    {
        Assert.SkipWhen(Root is null, "The conformance streams are not fetched (python tools/video/make-h264-conformance.py).");
        using JsonDocument refs = JsonDocument.Parse(File.ReadAllText(Path.Combine(Root!, "refs.json")));
        string[] expected = [.. refs.RootElement.GetProperty(stream).EnumerateArray().Select(e => e.GetString()!)];
        using FileStream input = File.OpenRead(Path.Combine(Root!, stream));
        using IDemuxer demuxer = H264Format.Instance.OpenDemuxer(input, MediaContext.Default, leaveOpen: true);
        using IVideoDecoder decoder = H264Codec.Instance.CreateDecoder(demuxer.Info.VideoTrack!, null, MediaContext.Default.WithH264());
        List<string> actual = [];
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                decoder.Send(packet);
            }

            Receive(decoder, actual);
        }

        decoder.Flush();
        Receive(decoder, actual);
        Assert.Equal(expected.Length, actual.Count);
        for (int i = 0; i < expected.Length; i++)
        {
            Assert.True(expected[i] == actual[i], $"{stream}: frame {i} of {expected.Length} differs from FFmpeg's.");
        }
    }

    private static void Receive(IVideoDecoder decoder, List<string> hashes)
    {
        while (decoder.TryReceive(out YuvVideoFrame? frame))
        {
            using (frame)
            {
                hashes.Add(DecodeTests.Md5(frame.Raster));
            }
        }
    }

    private static string? FindRoot()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "video", "h264conf");
            if (File.Exists(Path.Combine(candidate, "refs.json")))
            {
                return candidate;
            }
        }

        return null;
    }
}
