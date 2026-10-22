using System.Globalization;

using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

public class CodecHeadersTests
{
    // Fields we list that vp8dump does not print: the partition size, color space and the update counts.
    private static readonly HashSet<string> OnlyOurs = ["first_part_size", "color_space", "coeff_prob_updates", "mv_prob_updates", "intra_16x16_prob", "intra_chroma_prob"];

    public static TheoryData<string> Clips => [.. Corpus.Vp8Clips()];

    [Theory]
    [MemberData(nameof(Clips))]
    public void FieldsMatchLibvpx(string clip)
    {
        HashSet<string> seen = Compare(Corpus.Vp8PathOf(clip + ".ivf"));
        Assert.Contains("refresh_entropy_probs", seen);
    }

    [Fact]
    public void TestVectorFieldsMatchLibvpxAndCoverEveryField()
    {
        IReadOnlyList<string> vectors = Corpus.Vp8Vectors;
        Assert.SkipWhen(vectors.Count == 0, "The VP8 test vectors are not fetched (python tools/video/make-vp8-corpus.py --vectors).");
        HashSet<string> seen = [];
        HashSet<string> dumped = [];
        foreach (string vector in vectors)
        {
            seen.UnionWith(Compare(vector, dumped));
        }

        // Every header field libvpx reads from the vectors is one we list.
        Assert.Empty(dumped.Except(seen).Order());
    }

    [Fact]
    public void OtherCodecsHaveNoFields()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.PathOf("mkv-mjpeg-pcm.mkv"));
        using EncodedPacket packet = demuxer.ReadPacket()!;
        Assert.False(CodecHeaders.CanDescribe(demuxer.Tracks[0]));
        Assert.Empty(CodecHeaders.Describe(demuxer.Tracks[0], packet));
    }

    [Fact]
    public void CutOffHeadersAreCorrupt()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Corpus.Vp8PathOf("v0-keyframes.ivf"));
        using EncodedPacket packet = demuxer.ReadPacket()!;
        foreach (int length in (int[])[2, 9, 14])
        {
            using EncodedPacket cut = new(packet.TrackNumber, packet.Data[..length], packet.Timestamp);
            Assert.Throws<CorruptDataException>(() => CodecHeaders.Describe(demuxer.Tracks[0], cut));
        }
    }

    /// <summary>Compares our fields with vp8dump's for every packet; returns the field names we listed (without indexes).</summary>
    private static HashSet<string> Compare(string path, HashSet<string>? dumpedNames = null)
    {
        List<List<string>> dump = Corpus.ReadVp8Dump(path);
        using IDemuxer demuxer = MediaFile.OpenDemuxer(path);
        TrackInfo track = demuxer.Tracks[0];
        HashSet<string> seen = [];
        for (int index = 0; demuxer.ReadPacket() is { } packet; index++)
        {
            using (packet)
            {
                Dictionary<string, long[]> expected = Fields(dump[index]);
                dumpedNames?.UnionWith(expected.Keys);
                foreach (CodecHeaderField field in CodecHeaders.Describe(track, packet))
                {
                    int bracket = field.Name.IndexOf('[', StringComparison.Ordinal);
                    string name = bracket < 0 ? field.Name : field.Name[..bracket];
                    int at = bracket < 0 ? 0 : int.Parse(field.Name[(bracket + 1)..^1], CultureInfo.InvariantCulture);
                    seen.Add(name);
                    if (OnlyOurs.Contains(name))
                    {
                        continue;
                    }

                    Assert.True(expected.TryGetValue(name, out long[]? values), $"{Path.GetFileName(path)} packet {index}: libvpx prints no {name}");
                    Assert.True(values[at] == field.Value, $"{Path.GetFileName(path)} packet {index}: {field.Name} = {field.Value}, libvpx {values[at]}");
                }
            }
        }

        return seen;
    }

    private static Dictionary<string, long[]> Fields(List<string> lines)
    {
        Dictionary<string, long[]> fields = [];
        foreach (string line in lines)
        {
            int equals = line.IndexOf(" = ", StringComparison.Ordinal);
            if (equals < 0 || line.StartsWith("next_", StringComparison.Ordinal))
            {
                continue;
            }

            fields[line[..equals]] = [.. line[(equals + 3)..].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => long.Parse(v, CultureInfo.InvariantCulture))];
        }

        return fields;
    }
}
