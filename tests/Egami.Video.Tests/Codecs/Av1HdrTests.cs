using System.Text.Json;

using Egami.Colors;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video.Tests.Codecs;

/// <summary>
/// AV1 HDR and color (V4.2 task 16): hdr10.ivf is SVT-AV1's 10-bit BT.2020 PQ clip with mastering display and content light level
/// metadata OBUs; hdr10plus.ivf adds an ITU-T T.35 HDR10+ metadata OBU to each unit (tools/video/make-av1-corpus.py).
/// </summary>
public class Av1HdrTests
{
    [Theory]
    [InlineData("hdr10.ivf")]
    [InlineData("hdr10.webm")]
    [InlineData("hdr10plus.webm")]
    public void HdrMetadataMatchesFfprobe(string file)
    {
        using JsonDocument side = JsonDocument.Parse(File.ReadAllText(Av1Corpus.PathOf(Path.GetFileNameWithoutExtension(file) + ".side_data.json")));
        using VideoReader reader = VideoReader.Open(Av1Corpus.PathOf(file));
        int index = 0;
        foreach (YuvVideoFrame frame in reader.ReadFrames())
        {
            using (frame)
            {
                JsonElement[] data = [.. side.RootElement.GetProperty("frames")[index++].EnumerateArray()];
                JsonElement mastering = data.Single(e => e.GetProperty("side_data_type").GetString() == "Mastering display metadata");
                JsonElement light = data.Single(e => e.GetProperty("side_data_type").GetString() == "Content light level metadata");
                HdrMetadata hdr = Assert.IsType<HdrMetadata>(frame.Hdr);
                ColorPrimaries primaries = hdr.MasteringPrimaries!;
                Assert.Equal(Vp9Tests.Rational(mastering, "red_x"), primaries.Red.X, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "red_y"), primaries.Red.Y, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "green_x"), primaries.Green.X, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "green_y"), primaries.Green.Y, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "blue_x"), primaries.Blue.X, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "blue_y"), primaries.Blue.Y, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "white_point_x"), primaries.White.X, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "white_point_y"), primaries.White.Y, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "max_luminance"), hdr.MasteringMaxLuminance!.Value, 9);
                Assert.Equal(Vp9Tests.Rational(mastering, "min_luminance"), hdr.MasteringMinLuminance!.Value, 9);
                Assert.Equal(light.GetProperty("max_content").GetInt32(), hdr.MaxContentLightLevel);
                Assert.Equal(light.GetProperty("max_average").GetInt32(), hdr.MaxFrameAverageLightLevel);

                // The sequence header's BT.2020 PQ, on the frame and its planes.
                Assert.Equal(new ColorCodePoints(9, 16, 9, false), frame.Color);
                Assert.Equal(frame.Color, frame.Raster.Metadata.CodePoints);
                Assert.Same(hdr, frame.Raster.Metadata.Hdr);
            }
        }

        Assert.Equal(side.RootElement.GetProperty("frames").GetArrayLength(), index);
    }

    [Fact]
    public void Hdr10PlusMetadataObusGiveTheFramesHdr10Plus()
    {
        // FFmpeg 5.1 does not show an AV1 stream's HDR10+, so the payloads are checked against the same payloads in the VP9 corpus's
        // hdr10plus.webm, whose values equal ffprobe's (Vp9Tests.Hdr10PlusMatchesFfprobe).
        List<byte[]> vp9 = [];
        using (VideoReader reference = VideoReader.Open(Vp9Corpus.PathOf("hdr10plus.webm")))
        {
            foreach (YuvVideoFrame frame in reference.ReadFrames())
            {
                using (frame)
                {
                    vp9.Add(frame.Hdr10Plus!.Raw.ToArray());
                }
            }
        }

        foreach (string file in (string[])["hdr10plus.ivf", "hdr10plus.webm"])
        {
            using VideoReader reader = VideoReader.Open(Av1Corpus.PathOf(file));
            int index = 0;
            foreach (YuvVideoFrame frame in reader.ReadFrames())
            {
                using (frame)
                {
                    Hdr10PlusMetadata meta = Assert.IsType<Hdr10PlusMetadata>(frame.Hdr10Plus);
                    Assert.Equal(1, meta.ApplicationVersion);
                    Assert.Equal(400, meta.TargetDisplayMaxLuminance);
                    if (index < vp9.Count)
                    {
                        Assert.Equal(vp9[index], meta.Raw.ToArray());
                    }

                    index++;
                }
            }

            Assert.Equal(10, index);
        }

        using VideoReader plain = VideoReader.Open(Av1Corpus.PathOf("hdr10.ivf"));
        Assert.All(plain.ReadFrames(), f => { using (f) { Assert.Null(f.Hdr10Plus); } });
    }

    [Fact]
    public void TheHeaderDumpListsTheMetadata()
    {
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("hdr10plus.ivf"));
        using EncodedPacket packet = demuxer.ReadPacket()!;
        Dictionary<string, long> fields = CodecHeaders.Describe(demuxer.Tracks[0], packet).GroupBy(f => f.Name).ToDictionary(g => g.Key, g => g.First().Value);
        Assert.Equal(0xB5, fields["itu_t_t35_country_code"]);
        Assert.Equal(1000, fields["max_cll"]);
        Assert.Equal(400, fields["max_fall"]);
        Assert.Equal(44564, fields["primary_chromaticity_x[0]"]);
        Assert.Equal(1000 * 256, fields["luminance_max"]);
        Assert.Equal(16, fields["transfer_characteristics"]);
    }

    [Fact]
    public void HdrMetadataInAv1CHoldsBeforeTheStreamSendsAny()
    {
        // FFmpeg copies the first unit's metadata OBUs into av1C; units without them still give the track's HDR.
        using IDemuxer demuxer = MediaFile.OpenDemuxer(Av1Corpus.PathOf("hdr10.webm"));
        using IVideoDecoder decoder = VideoCodecs.CreateDecoder(demuxer.Info.VideoTrack!);
        using EncodedPacket first = demuxer.ReadPacket()!;
        byte[] stripped = WithoutMetadata(first.Data.Span);
        Assert.True(stripped.Length < first.Data.Length);
        decoder.Send(new EncodedPacket(first.TrackNumber, stripped, first.Timestamp) { IsKeyframe = true });
        Assert.True(decoder.TryReceive(out YuvVideoFrame? frame));
        using (frame)
        {
            Assert.Equal(1000, frame.Hdr!.MaxContentLightLevel);
            Assert.Equal(1000, frame.Hdr.MasteringMaxLuminance);
        }
    }

    [Fact]
    public void FilmGrainCanBeTurnedOff()
    {
        string? folder = Av1Corpus.Vectors;
        Assert.SkipWhen(folder is null, "The libaom test vectors are not fetched (python tools/make-av1-references.py).");
        string vector = "av1-1-b8-23-film_grain-50.ivf";
        string reference = Path.Combine(folder!, "..", "reference", "av1", vector + ".json");
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(reference));
        JsonElement[] frames = [.. json.RootElement.GetProperty("frames").EnumerateArray()];
        Assert.Contains(frames, f => f[4].GetString() != f[5].GetString());
        using (VideoReader grain = VideoReader.Open(Path.Combine(folder!, vector)))
        {
            Assert.Equal(frames.Select(f => f[4].GetString()), Av1Corpus.Md5s(grain));
        }

        using VideoReader plain = VideoReader.Open(Path.Combine(folder!, vector), new VideoReaderOptions { Decoder = new VideoDecoderOptions { ApplyFilmGrain = false } });
        Assert.Equal(frames.Select(f => f[5].GetString()), Av1Corpus.Md5s(plain));
    }

    private static byte[] WithoutMetadata(ReadOnlySpan<byte> unit)
    {
        using MemoryStream stream = new();
        foreach (Video.Codecs.Av1.Av1Obu obu in Video.Codecs.Av1.Av1Obus.Split(unit))
        {
            if (obu.Type != Video.Codecs.Av1.Av1Obus.Metadata)
            {
                stream.Write(unit[obu.Start..obu.End]);
            }
        }

        return stream.ToArray();
    }
}
