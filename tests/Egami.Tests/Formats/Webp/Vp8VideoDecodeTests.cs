using System.Text.Json;

using Egami.Formats.Webp;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// VP8 video decoding against libvpx 1.14.1 (Phase 2.3.5): every packet's header fields and macroblock modes and vectors equal
/// vp8dump's, and every shown frame equals vpxdec's, on the committed clips and on the libvpx test vectors when fetched.
/// </summary>
public class Vp8VideoDecodeTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files() => Vp8VideoCorpus.All();

    [Theory]
    [MemberData(nameof(Files))]
    public void HeadersAndModesMatchLibvpx(string id)
    {
        List<byte[]> packets = Vp8VideoCorpus.ReadIvf(Vp8VideoCorpus.IvfPath(id));
        List<List<string>> dump = Vp8VideoCorpus.Dump(id);
        Assert.Equal(dump.Count, packets.Count);
        using Vp8VideoDecoder video = new();
        for (int i = 0; i < packets.Count; i++)
        {
            video.Decode(packets[i], 0, packets[i].Length);
            List<string> ours = Vp8VideoCorpus.Describe(video, i, packets[i].Length);
            List<string> theirs = dump[i];
            for (int l = 0; l < Math.Max(ours.Count, theirs.Count); l++)
            {
                string? a = l < ours.Count ? ours[l] : null;
                string? b = l < theirs.Count ? theirs[l] : null;
                // The segment probabilities only mean something in a frame that updates the map; libvpx starts them at 0, Egami at 255.
                bool unused = a is not null && a.StartsWith("segment_prob", StringComparison.Ordinal) && ours.Contains("update_mb_segmentation_map = 0");
                if (a != b && !unused)
                {
                    Assert.Fail($"{id} packet {i} line {l}:\n  libvpx: {b}\n  Egami:  {a}");
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FramesMatchLibvpx(string id) => FramesMatch(id, 1);

    [Theory]
    [MemberData(nameof(Files))]
    public void FramesMatchLibvpxOnEightThreads(string id) => FramesMatch(id, 8);

    private void FramesMatch(string id, int threads)
    {
        List<byte[]> packets = Vp8VideoCorpus.ReadIvf(Vp8VideoCorpus.IvfPath(id));
        JsonElement expected = Vp8VideoCorpus.Expected(id);
        int[] shown = [.. expected.GetProperty("shown").EnumerateArray().Select(e => e.GetInt32())];
        string[] md5 = [.. expected.GetProperty("md5").EnumerateArray().Select(e => e.GetString()!)];
        using Vp8VideoDecoder video = new(threads);
        int frame = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            video.Decode(packets[i], 0, packets[i].Length);
            Assert.Equal(shown.Contains(i), video.Shown is not null);
            if (video.Shown is Vp8FrameBuffer buffer)
            {
                string ours = Vp8VideoCorpus.Md5(buffer);
                if (ours != md5[frame])
                {
                    Assert.Fail($"{id}: shown frame {frame} (packet {i}) differs from libvpx");
                }

                frame++;
            }
        }

        Assert.Equal(md5.Length, frame);
        output.WriteLine($"{id}: {packets.Count} packets, {frame} frames equal libvpx's on {threads} thread(s)");
    }
}
