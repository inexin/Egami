using Egami.Formats.Av1;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>AV1 packet helpers and header dumps (V4.2 task 15).</summary>
public class Av1PacketsTests
{
    public static TheoryData<string> Clips() => [.. Av1Corpus.Clips()];

    /// <summary>Our clips and, when fetched, libaom's test vectors (scalable ones included).</summary>
    private static IEnumerable<string> Streams()
    {
        foreach (string clip in Av1Corpus.Clips())
        {
            yield return Av1Corpus.PathOf(clip + ".ivf");
        }

        if (Av1Corpus.Vectors is { } folder)
        {
            foreach (string vector in Directory.GetFiles(folder, "av1-1-b8-*.ivf").Order(StringComparer.Ordinal).Where(v => !v.Contains("-01-size-", StringComparison.Ordinal)))
            {
                yield return vector;
            }
        }
    }

    [Fact]
    public void SplitThenMergeGivesTheUnits()
    {
        int units = 0, split = 0;
        foreach (string path in Streams())
        {
            using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
            while (demuxer.ReadPacket() is { } packet)
            {
                using (packet)
                {
                    IReadOnlyList<ReadOnlyMemory<byte>> frames = Av1Packets.SplitFrames(packet.Data);
                    Assert.Equal(packet.Data.ToArray(), Av1Packets.MergeFrames(frames));

                    // One frame header (or frame OBU) per part; the temporal delimiter goes with the first.
                    int headers = Av1Obus.Split(packet.Data.Span).Count(o => o.StartsFrame);
                    Assert.Equal(Math.Max(1, headers), frames.Count);
                    Assert.All(frames, f => Assert.Equal(1, Av1Obus.Split(f.Span).Count(o => o.StartsFrame)));
                    Assert.Equal(Av1Obus.TemporalDelimiter, Av1Obus.Split(frames[0].Span)[0].Type);
                    units++;
                    split += frames.Count > 1 ? 1 : 0;
                }
            }
        }

        // libaom keeps hidden frames in the unit of the next shown one, so some units hold several frames.
        Assert.True(split > 0, $"{units} units, none with more than one frame");
    }

    [Fact]
    public void SequenceHeadersAndMetadataGoWithTheFrameAfterThem()
    {
        // TD, frame A (header + tile group), metadata, sequence header, frame B, padding.
        byte[] unit = [0x12, 0x00, 0x1A, 0x01, 0x10, 0x22, 0x01, 0xAA, 0x2A, 0x02, 0x01, 0x80, 0x0A, 0x01, 0x00, 0x32, 0x01, 0x30, 0x7A, 0x01, 0x00];
        IReadOnlyList<ReadOnlyMemory<byte>> frames = Av1Packets.SplitFrames(unit);
        Assert.Equal(2, frames.Count);
        Assert.Equal(unit[..8], frames[0].ToArray());
        Assert.Equal(unit[8..], frames[1].ToArray());
        Assert.Single(Av1Packets.SplitFrames(new byte[] { 0x12, 0x00 }));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void ExtractConfigEqualsFfmpegsAv1C(string clip)
    {
        using IDemuxer webm = MediaFile.OpenDemuxer(Av1Corpus.PathOf(clip + ".webm"));
        using IDemuxer ivf = MediaFile.OpenDemuxer(Av1Corpus.PathOf(clip + ".ivf"));
        using EncodedPacket first = ivf.ReadPacket()!;
        byte[] ours = Av1Packets.ExtractConfig(first.Data.Span)!;
        byte[] ffmpeg = webm.Tracks[0].CodecPrivate.ToArray();

        // FFmpeg also copies the first unit's metadata OBUs (HDR) into configOBUs; ours holds the sequence header only.
        Assert.Equal(ffmpeg[..ours.Length], ours);
        Assert.All(Av1Obus.Split(ffmpeg.AsSpan(ours.Length)), o => Assert.Equal(Av1Obus.Metadata, o.Type));
        Assert.Null(Av1Packets.ExtractConfig(new byte[] { 0x12, 0x00 }));
    }

    [Theory]
    [MemberData(nameof(Clips))]
    public void HeaderDumpsAgreeWithTheDecoder(string clip)
    {
        // The decoder's fields match dav1d's (Egami's own AV1 header tests); here each listed field must hold the decoder's value.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf(clip + ".webm"));
        TrackInfo track = demuxer.Tracks[0];
        Assert.True(CodecHeaders.CanDescribe(track));
        using Av1StreamDecoder decoder = new();
        using CodecHeaderReader reader = new(track);
        int compared = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                IReadOnlyList<CodecHeaderField> start = CodecHeaders.Describe(track, packet);
                IReadOnlyList<CodecHeaderField> whole = reader.Describe(packet);
                decoder.Send(packet.Data.Span);
                List<KeyValuePair<string, long>> decoded = [.. decoder.LastHeaders];

                // The decoder lists the sequence header, then each frame's header from show_existing_frame on.
                Dictionary<string, long> sequence = decoded.TakeWhile(f => f.Key != "show_existing_frame").ToDictionary(f => f.Key, f => f.Value);
                List<Dictionary<string, long>> frames = [];
                foreach (KeyValuePair<string, long> field in decoded.SkipWhile(f => f.Key != "show_existing_frame"))
                {
                    if (field.Key == "show_existing_frame")
                    {
                        frames.Add([]);
                    }

                    frames[^1][field.Key] = field.Value;
                }

                int frame = -1;
                foreach (CodecHeaderField field in start)
                {
                    if (field.Name == "obu_type" && field.Value is Av1Obus.FrameHeader or Av1Obus.Frame)
                    {
                        frame++;
                    }

                    Dictionary<string, long> expected = frame < 0 ? sequence : frames[frame];
                    if (expected.TryGetValue(field.Name, out long value))
                    {
                        Assert.True(value == field.Value, $"{field.Name} is {field.Value}, the decoder read {value}");
                        compared++;
                    }
                }

                // The whole listing carries the decoder's frame headers in place of their starts.
                List<KeyValuePair<string, long>> flat = [.. decoded.SkipWhile(f => f.Key != "show_existing_frame")];
                int at = 0;
                foreach (CodecHeaderField field in whole)
                {
                    if (at < flat.Count && field.Name == flat[at].Key && field.Value == flat[at].Value)
                    {
                        at++;
                    }
                }

                Assert.Equal(flat.Count, at);
            }
        }

        Assert.True(compared > 40, $"{compared} fields compared");
    }

    [Fact]
    public void HeaderReaderListsWholeFrameHeaders()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("forward-keyframes.mkv"));
        using CodecHeaderReader reader = new(demuxer.Tracks[0]);
        int frames = 0, existing = 0;
        while (demuxer.ReadPacket() is { } packet)
        {
            using (packet)
            {
                IReadOnlyList<CodecHeaderField> fields = reader.Describe(packet);
                int headers = fields.Count(f => f.Name == "obu_type" && f.Value is Av1Obus.FrameHeader or Av1Obus.Frame);
                Assert.Equal(headers, fields.Count(f => f.Name == "show_existing_frame"));
                frames += fields.Count(f => f.Name == "base_q_idx");
                existing += fields.Count(f => f.Name == "frame_to_show_map_idx");
            }
        }

        Assert.True(frames > 20, $"{frames} frame headers with base_q_idx");
        Assert.True(existing > 0);

        // A packet on its own lists only the start of an inter frame's header.
        using IDemuxer again = MediaFile.OpenDemuxer(Av1Corpus.PathOf("forward-keyframes.mkv"));
        using EncodedPacket first = again.ReadPacket()!;
        using EncodedPacket second = again.ReadPacket()!;
        IReadOnlyList<CodecHeaderField> alone = CodecHeaders.Describe(again.Tracks[0], second);
        Assert.Contains(alone, f => f.Name == "frame_type");
        Assert.DoesNotContain(alone, f => f.Name == "base_q_idx");
    }

    [Fact]
    public void ACutHeaderThrows()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("tools-8bit.ivf"));
        using EncodedPacket packet = demuxer.ReadPacket()!;
        using EncodedPacket cut = new(packet.TrackNumber, packet.Data[..6], packet.Timestamp);
        Assert.Throws<CorruptDataException>(() => CodecHeaders.Describe(demuxer.Tracks[0], cut));
    }
}
