using Egami.Formats.Jpeg;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;
using Egami.Video.Tests.Containers;

namespace Egami.Video.Tests.Codecs;

public class MotionJpegEncoderTests
{
    [Fact]
    public void DefaultsMatchEgamisJpegWriter()
    {
        using Raster<Rgb8> image = Gradient(96, 64, 0);
        using IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = 96, Height = 64 }, MediaContext.Default);
        using VideoFrame<Rgb8> frame = new(Gradient(96, 64, 0), new MediaTime(3, new Rational(1, 30)));
        encoder.Send(frame);
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        using (packet)
        {
            using MemoryStream expected = new();
            image.Save(expected, JpegFormat.Info, new JpegWriterOptions { Quality = 75, Subsampling = ChromaSubsampling.Subsample420, StripMetadata = true });
            Assert.Equal(expected.ToArray(), packet.Data.ToArray());
            Assert.True(packet.IsKeyframe);
            Assert.Equal(new MediaTime(3, new Rational(1, 30)), packet.Timestamp);
            Assert.Equal(CodecIds.MotionJpeg, encoder.Track.CodecId);
        }
    }

    [Theory]
    [InlineData(ChromaFormat.Yuv420)]
    [InlineData(ChromaFormat.Yuv422)]
    [InlineData(ChromaFormat.Yuv444)]
    public void PlanesKeepTheirSubsamplingAndRoundTrip(ChromaFormat chroma)
    {
        using Raster<Rgb8> image = Gradient(64, 48, 5);
        using YuvRaster<byte> planes = YuvRaster.FromRaster(image, chroma, YuvColor.Jpeg);
        using IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = 64, Height = 48, Quality = 95 }, MediaContext.Default);
        using YuvVideoFrame frame = new(YuvRaster.FromRaster(image, chroma, YuvColor.Jpeg), MediaTime.Zero);
        encoder.Send(frame);
        Assert.True(encoder.TryReceive(out EncodedPacket? packet));
        using (packet)
        {
            using YuvRaster decoded = YuvRaster.Load(packet.Data);
            Assert.Equal(chroma, decoded.ChromaFormat);
            Assert.True(Psnr(planes.Y, ((YuvRaster<byte>)decoded).Y) > 38);
        }
    }

    [Fact]
    public void WrongSizedFramesAreRefused()
    {
        using IVideoEncoder encoder = VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32 }, MediaContext.Default);
        using VideoFrame<Rgb8> frame = new(Raster.Create<Rgb8>(16, 16), MediaTime.Zero);
        Assert.Throws<ArgumentException>(() => encoder.Send(frame));
        Assert.Throws<ArgumentOutOfRangeException>(() => VideoCodecs.MotionJpeg.CreateEncoder(new VideoEncoderOptions { Width = 32, Height = 32, Quality = 0 }, MediaContext.Default));
    }

    private static Raster<Rgb8> Gradient(int width, int height, int shift)
    {
        Raster<Rgb8> raster = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgb8((byte)((x * 2) + shift), (byte)(y * 3), (byte)(((x + y) * 2) - shift));
            }
        }

        return raster;
    }

    private static double Psnr(YuvPlane<byte> a, YuvPlane<byte> b)
    {
        double sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            Span<byte> ra = a.Row(y), rb = b.Row(y);
            for (int x = 0; x < ra.Length; x++)
            {
                int d = ra[x] - rb[x];
                sum += d * d;
            }
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 / (sum / (a.Width * a.Height)));
    }
}
