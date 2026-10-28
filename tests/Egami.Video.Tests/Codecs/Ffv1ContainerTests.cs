using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>FFV1 in Matroska and AVI: track descriptions, header listing and stream copies (V6 task 8).</summary>
public class Ffv1ContainerTests
{
    [Theory]
    [InlineData("v3-420.mkv", 8, ChromaFormat.Yuv420)]
    [InlineData("v3-422p10.mkv", 10, ChromaFormat.Yuv422)]
    [InlineData("v3-420p9.mkv", 9, ChromaFormat.Yuv420)]
    [InlineData("v3-gbrp12.mkv", 12, ChromaFormat.Yuv444)]
    [InlineData("v3-411.mkv", 8, ChromaFormat.Yuv411)]
    [InlineData("v3-gray16.mkv", 16, ChromaFormat.Yuv400)]
    [InlineData("v3-420-avi.avi", 8, ChromaFormat.Yuv420)]
    public void Version3TracksDescribeTheirLayout(string file, int depth, ChromaFormat format)
    {
        MediaInfo info = MediaFile.Probe(Ffv1Tests.PathOf(file));
        VideoTrackInfo track = info.VideoTrack!;
        Assert.Equal(CodecIds.Ffv1, track.CodecId);
        Assert.Equal(depth, track.BitDepth);
        Assert.Equal(format, track.ChromaFormat);
    }

    [Fact]
    public void AnAviTrackIsFfv1WithItsConfigurationRecord()
    {
        MediaInfo info = MediaFile.Probe(Ffv1Tests.PathOf("v1-gop-rice.avi"));
        Assert.Equal(CodecIds.Ffv1, info.VideoTrack!.CodecId);
        Assert.True(info.VideoTrack.CodecPrivate.IsEmpty);
        Assert.Equal(191, MediaFile.Probe(Ffv1Tests.PathOf("v3-420-avi.avi")).VideoTrack!.CodecPrivate.Length);
    }

    [Fact]
    public void HeadersListTheParametersAndEverySlice()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Ffv1Tests.PathOf("v3-interlaced.mkv"));
        TrackInfo track = demuxer.Info.VideoTrack!;
        Assert.True(CodecHeaders.CanDescribe(track));
        using EncodedPacket packet = demuxer.ReadPacket()!;
        Dictionary<string, long> fields = [];
        foreach (CodecHeaderField field in CodecHeaders.Describe(track, packet))
        {
            fields.TryAdd(field.Name, field.Value);
        }

        Assert.Equal(3, fields["version"]);
        Assert.Equal(2, fields["coder_type"]);
        Assert.Equal(1, fields["ec"]);
        Assert.Equal(1, fields["keyframe"]);
        Assert.Equal(4, CodecHeaders.Describe(track, packet).Count(f => f.Name == "slice"));
        Assert.Equal(16, fields["sar_num"]);
        Assert.Equal(15, fields["sar_den"]);
        Assert.Equal(1, fields["picture_structure"]);

        using IDemuxer old = MediaFile.OpenDemuxer(Ffv1Tests.PathOf("v1-422-tab-large.mkv"));
        using EncodedPacket first = old.ReadPacket()!;
        IReadOnlyList<CodecHeaderField> v1 = CodecHeaders.Describe(old.Info.VideoTrack!, first);
        Assert.Equal(("keyframe", 1L), (v1[0].Name, v1[0].Value));
        Assert.Contains(new CodecHeaderField("version", 1), v1);
    }

    [Theory]
    [InlineData("v3-420.mkv", "avi")]
    [InlineData("v3-420-avi.avi", "mkv")]
    [InlineData("v1-bgra-range.avi", "avi")]
    public void StreamCopiesDecodeTheSame(string file, string format)
    {
        string clip = Path.GetFileNameWithoutExtension(file);
        MemoryStream output = new();
        using (FileStream input = File.OpenRead(Ffv1Tests.PathOf(file)))
        {
            MediaFile.Remux(input, output, format, leaveOpen: true, cancellationToken: TestContext.Current.CancellationToken);
        }

        (_, int depth, List<string> expected) = Ffv1Tests.ReadReference(clip);
        using VideoReader reader = VideoReader.Open(new MemoryStream(output.ToArray()));
        Assert.Equal(CodecIds.Ffv1, reader.Track.CodecId);
        List<string> ours = [];
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                ours.Add(Ffv1Tests.Md5(frame.Raster, depth));
            }
        }

        Assert.Equal(expected, ours);
    }
}
