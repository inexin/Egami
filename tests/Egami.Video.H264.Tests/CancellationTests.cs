using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.H264.Tests;

/// <summary>
/// The H.264 encoder's <see cref="VideoEncoderOptions.CancellationToken"/>, and the bit depth and subsampling a probe reads from
/// the avcC record of the files it writes.
/// </summary>
public sealed class CancellationTests
{
    [Fact]
    public void ACancelledTokenStopsTheEncoderAfterTheFrameItIsCoding()
    {
        using CancellationTokenSource source = new();
        VideoEncoderOptions options = new() { Width = 64, Height = 48, CancellationToken = source.Token };
        using H264VideoEncoder encoder = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        int coded = 0;
        Rational timeBase = options.FrameRate.Reciprocal();
        Assert.Throws<OperationCanceledException>(() =>
        {
            for (int i = 0; i < 8; i++)
            {
                using YuvRaster<byte> picture = EncoderIntraTests.Picture(64, 48, i);
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
        using IVideoEncoder encoder = H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        using YuvRaster<byte> picture = EncoderIntraTests.Picture(64, 48, 0);
        Assert.Throws<OperationCanceledException>(() => encoder.Send(new YuvVideoFrame(picture, new MediaTime(0, new Rational(1, 30)))));
    }

    [Theory]
    [InlineData(".mp4")]
    [InlineData(".mkv")]
    public void ProbeReportsTheBitDepth(string extension)
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-h264-depth-{Guid.NewGuid():N}{extension}");
        try
        {
            using (VideoWriter writer = VideoWriter.Create(path, new VideoWriterOptions { Codec = H264Codec.Instance }))
            {
                for (int i = 0; i < 2; i++)
                {
                    using YuvRaster<byte> picture = EncoderIntraTests.Picture(64, 48, i);
                    writer.WriteFrame(picture);
                }
            }

            VideoTrackInfo track = MediaFile.Probe(path).VideoTrack!;
            Assert.Equal(CodecIds.Avc, track.CodecId);
            Assert.Equal(8, track.BitDepth);
            Assert.Equal(ChromaFormat.Yuv420, track.ChromaFormat);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
