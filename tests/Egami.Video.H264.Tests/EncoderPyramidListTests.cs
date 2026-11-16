using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// With a B-pyramid a P picture's newest reference is the middle B picture, which it marks unused: list 0 starts with the P or
/// I picture before instead, by ref_pic_list_modification (8.2.4.3.1), and the stream decodes to the encoder's reconstruction.
/// </summary>
[Collection(nameof(EncoderStatsRunsAlone))]
public sealed class EncoderPyramidListTests
{
    [Theory]
    [InlineData(4, 3)]
    [InlineData(6, 3)]
    [InlineData(2, 3)]
    public void PPicturesPredictFromTheLastPFirst(int effort, int bFrames)
    {
        List<(bool[] Dropped, bool Reordered)> lists = [];
        H264PictureEncoder.PList = (dropped, reordered) =>
        {
            lock (lists)
            {
                lists.Add((dropped, reordered));
            }
        };
        try
        {
            VideoEncoderOptions options = new()
            {
                Width = 64,
                Height = 48,
                Effort = effort,
                KeyframeInterval = 100,
                H264 = new H264EncoderOptions { BFrames = bFrames },
            };
            EncoderInterTests.Check(options, 33, EncoderInterTests.Moving);
        }
        finally
        {
            H264PictureEncoder.PList = null;
        }

        // The B picture to drop is never first while another reference is there, and comes after all of them.
        Assert.All(lists, l => Assert.True(l.Dropped.Length == 1 || !l.Dropped[0]));
        Assert.All(lists, l => Assert.Equal(l.Dropped.Order(), l.Dropped));
        if (effort >= 4)
        {
            Assert.Contains(lists, l => l.Reordered);
        }
    }
}
