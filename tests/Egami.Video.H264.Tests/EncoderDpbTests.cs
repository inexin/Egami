using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// The VUI's max_num_reorder_frames and max_dec_frame_buffering (E.2.1): the reorder is the stream's real one, and the buffer
/// holds the references and fits the level's (A.3.1 h), for any number of B-frames.
/// </summary>
public sealed class EncoderDpbTests
{
    [Theory]
    [InlineData(0, 4)]
    [InlineData(1, 4)]
    [InlineData(2, 4)]
    [InlineData(3, 4)]
    [InlineData(3, 2)]
    [InlineData(5, 4)]
    [InlineData(16, 1)]
    public void TheReorderIsTheStreamsOwn(int bFrames, int effort)
    {
        VideoEncoderOptions options = new()
        {
            Width = 64,
            Height = 48,
            Effort = effort,
            KeyframeInterval = 100,
            H264 = new H264EncoderOptions { BFrames = bFrames },
        };
        using H264VideoEncoder encoder = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        List<EncodedPacket> packets = EncoderInterTests.Check(options, 2 * (bFrames + 1) + 1, EncoderInterTests.Moving);

        // Frames before each one in decoding order that are shown after it.
        int reorder = 0;
        for (int i = 0; i < packets.Count; i++)
        {
            reorder = Math.Max(reorder, packets.Take(i).Count(p => p.Timestamp.CompareTo(packets[i].Timestamp) > 0));
        }

        Assert.Equal(reorder, encoder.Config.ReorderFrames);
        Assert.True(encoder.Config.DecFrameBuffering >= Math.Max(encoder.Config.MaxRefFrames, reorder));
    }

    [Theory]
    [InlineData(1920, 1080, 40, 3)]
    [InlineData(1920, 1080, 40, 16)]
    [InlineData(1280, 720, 31, 16)]
    [InlineData(176, 144, 11, 16)]
    [InlineData(3840, 2160, 51, 16)]
    public void TheBufferFitsTheLevel(int width, int height, int level, int bFrames)
    {
        for (int effort = 0; effort <= 10; effort += 2)
        {
            VideoEncoderOptions options = new()
            {
                Width = width,
                Height = height,
                Effort = effort,
                H264 = new H264EncoderOptions { BFrames = bFrames, Level = level },
            };
            using H264VideoEncoder encoder = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
            H264StreamConfig config = encoder.Config;
            int frame = config.WidthMbs * config.HeightMbs;
            int levelFrames = Math.Min(H264Levels.Of(level).MaxDpbMbs / frame, 16);
            Assert.InRange(config.DecFrameBuffering, Math.Max(config.MaxRefFrames, config.ReorderFrames), levelFrames);
            Assert.InRange(config.ReorderFrames, 0, 2);
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(1)]
    public void KeyframesAfterUnreferencedBPicturesReuseTheirPlanes(int effort)
    {
        // Keyframes every 5 frames with 3 B-frames: each IDR picture follows an unreferenced B picture.
        VideoEncoderOptions options = new()
        {
            Width = 64,
            Height = 48,
            Effort = effort,
            KeyframeInterval = 5,
            H264 = new H264EncoderOptions { BFrames = 3 },
        };
        using H264VideoEncoder probe = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        int sets = 0;
        EncoderInterTests.Check(options, 41, EncoderInterTests.Moving, coder => coder.Coded += (_, _) => sets = Math.Max(sets, coder.PlaneSets));
        Assert.InRange(sets, 1, probe.Config.MaxRefFrames + 1);
    }
}
