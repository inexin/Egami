using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Hardware.Tests;

/// <summary>
/// Streams whose size or bit depth changes at a keyframe: every frame of every sequence comes out, the same as when each clip
/// is decoded on its own. The last pictures of a sequence are still references when the next one starts, and are output then.
/// </summary>
[Trait("Category", "Gpu")]
public sealed class SequenceChangeTests
{
    [Theory]
    [InlineData("b.h265", "ctu16.h265", "b.h265")]
    [InlineData("p.h265", "main10.h265", "b.h265", "main10.h265")]
    public void HevcSequencesAllComeOut(params string[] clips)
    {
        HevcDecoderTests.RequireDecoder();
        Check("hevc", clips, (stream, name, codec) => HevcDecoderTests.Decode(stream, name, codec), HevcCodec.Instance, HardwareVideoCodecs.Hevc);
    }

    [Theory]
    [InlineData("cabac-b-spatial.h264", "cif-intra.h264", "cabac-p.h264")]
    public void H264SequencesAllComeOut(params string[] clips)
    {
        H264DecoderTests.RequireDecoder();
        Check("h264", clips, (stream, _, codec) => H264DecoderTests.Decode(stream, codec), H264Codec.Instance, HardwareVideoCodecs.H264);
    }

    private static void Check(string folder, string[] clips, Func<byte[], string, VideoCodec, List<YuvVideoFrame>> decode, VideoCodec software, VideoCodec hardware)
    {
        List<YuvVideoFrame> expected = [];
        List<YuvVideoFrame> actual = [];
        try
        {
            List<byte> joined = [];
            foreach (string clip in clips)
            {
                byte[] stream = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "corpus", folder, clip));
                expected.AddRange(decode(stream, clip, software));
                joined.AddRange(stream);
            }

            Assert.True(expected.Select(f => (f.Width, f.Height, f.Planes!.BitDepth)).Distinct().Count() > 1, "the clips must differ in size or bit depth");
            actual = decode([.. joined], clips[0], hardware);
            Assert.Equal(expected.Count, actual.Count);
            for (int i = 0; i < expected.Count; i++)
            {
                Assert.True(SameY(expected[i].Planes!, actual[i].Planes!), $"frame {i}");
            }
        }
        finally
        {
            expected.ForEach(f => f.Dispose());
            actual.ForEach(f => f.Dispose());
        }
    }

    private static bool SameY(YuvRaster a, YuvRaster b) => (a, b) switch
    {
        (YuvRaster<byte> x, YuvRaster<byte> y) => x.Width == y.Width && x.Height == y.Height && Enumerable.Range(0, x.Height).All(r => x.Y.Row(r).SequenceEqual(y.Y.Row(r))),
        (YuvRaster<ushort> x, YuvRaster<ushort> y) => x.Width == y.Width && x.Height == y.Height && Enumerable.Range(0, x.Height).All(r => x.Y.Row(r).SequenceEqual(y.Y.Row(r))),
        _ => false,
    };
}
