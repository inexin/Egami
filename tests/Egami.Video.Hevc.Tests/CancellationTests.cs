using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// The HEVC encoder's <see cref="VideoEncoderOptions.CancellationToken"/>, and the bit depth and subsampling a probe reads from
/// the hvcC record of the files it writes.
/// </summary>
public sealed class CancellationTests
{
    [Fact]
    public void ACancelledTokenStopsTheEncoderAfterTheFrameItIsCoding()
    {
        using CancellationTokenSource source = new();
        VideoEncoderOptions options = new() { Width = 64, Height = 48, CancellationToken = source.Token };
        using HevcVideoEncoder encoder = (HevcVideoEncoder)HevcCodec.Instance.CreateEncoder(options, MediaContext.Default);
        int coded = 0;
        Rational timeBase = options.FrameRate.Reciprocal();
        Assert.Throws<OperationCanceledException>(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                using YuvRaster picture = EncoderTests.Picture(64, 48, i, 8);
                encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, timeBase)));
                encoder.Coder!.Coded ??= (_, _) =>
                {
                    coded++;
                    source.Cancel();
                };
            }

            encoder.Flush();
        });
        Assert.Equal(1, coded);
    }

    [Fact]
    public void ACancelledTokenRefusesTheFirstFrame()
    {
        VideoEncoderOptions options = new() { Width = 64, Height = 48, CancellationToken = new CancellationToken(true) };
        using IVideoEncoder encoder = HevcCodec.Instance.CreateEncoder(options, MediaContext.Default);
        using YuvRaster picture = EncoderTests.Picture(64, 48, 0, 8);
        Assert.Throws<OperationCanceledException>(() => encoder.Send(new YuvVideoFrame(picture, new MediaTime(0, new Rational(1, 30)))));
    }

    [Fact]
    public void TheWritersTokenReachesTheEncoder()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-hevc-cancel-{Guid.NewGuid():N}.mp4");
        try
        {
            VideoWriterOptions options = new() { Codec = HevcCodec.Instance, CancellationToken = new CancellationToken(true) };
            using VideoWriter writer = VideoWriter.Create(path, options);
            using YuvRaster picture = EncoderTests.Picture(64, 48, 0, 8);
            Assert.Throws<OperationCanceledException>(() => writer.WriteFrame(picture));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(".mp4", 8)]
    [InlineData(".mp4", 10)]
    [InlineData(".mov", 10)]
    [InlineData(".mkv", 10)]
    public void ProbeReportsTheBitDepth(string extension, int depth)
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-hevc-depth-{Guid.NewGuid():N}{extension}");
        try
        {
            using (VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = HevcCodec.Instance }))
            {
                for (int i = 0; i < 2; i++)
                {
                    using YuvRaster picture = EncoderTests.Picture(64, 48, i, depth);
                    writer.WriteFrame(picture);
                }
            }

            VideoTrackInfo track = MediaFile.Probe(path).VideoTrack!;
            Assert.Equal(CodecIds.Hevc, track.CodecId);
            Assert.Equal(depth, track.BitDepth);
            Assert.Equal(ChromaFormat.Yuv420, track.ChromaFormat);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
