using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>Intra pictures from the encoder's picture coder decode in Egami's decoder to exactly the encoder's reconstruction.</summary>
public sealed class EncoderIntraTests
{
    public static TheoryData<int, bool, bool, int, int, int> Cases => new()
    {
        { 66, false, false, 26, 64, 48 },
        { 77, true, false, 26, 64, 48 },
        { 100, true, true, 26, 64, 48 },
        { 100, false, true, 26, 64, 48 },
        { 100, true, true, 10, 48, 32 },
        { 100, false, true, 10, 48, 32 },
        { 100, true, true, 45, 80, 64 },
        { 77, true, false, 0, 32, 32 },
        { 66, false, false, 0, 32, 32 },
        { 100, true, true, 51, 32, 32 },
        { 100, true, true, 30, 70, 38 },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void DecodesToTheReconstruction(int profile, bool cabac, bool t8, int qp, int width, int height) =>
        Check(profile, cabac, t8, qp, width, height, 4);

    [Theory]
    [InlineData(0, true)]
    [InlineData(0, false)]
    [InlineData(2, true)]
    [InlineData(2, false)]
    [InlineData(10, true)]
    [InlineData(10, false)]
    public void EveryEffortDecodesToTheReconstruction(int effort, bool cabac) => Check(100, cabac, true, 28, 64, 48, effort);

    private static void Check(int profile, bool cabac, bool t8, int qp, int width, int height, int effort)
    {
        H264StreamConfig config = new()
        {
            Width = width,
            Height = height,
            ProfileIdc = profile,
            LevelIdc = 40,
            Cabac = cabac,
            Transform8x8 = t8,
            FrameRate = new Rational(25, 1),
            Color = new ColorCodePoints(1, 1, 1, false),
        };
        H264PictureEncoder encoder = new(config, effort);
        byte[] sps = H264StreamWriter.Sps(config), pps = H264StreamWriter.Pps(config);
        VideoTrackInfo track = new()
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.Avc,
            TimeBase = new Rational(1, 25),
            Width = width,
            Height = height,
            CodecPrivate = H264StreamWriter.AvcC(config, sps, pps),
        };
        using H264VideoDecoder decoder = new(track, VideoDecoderOptions.Default, MediaContext.Default);
        for (int frame = 0; frame < 3; frame++)
        {
            using YuvRaster<byte> picture = Picture(width, height, frame);
            encoder.Load(picture);
            byte[][] nals = encoder.EncodeIntra(true, frame, 0, 0, qp);
            byte[] data = H264StreamWriter.AccessUnit(nals, annexB: false);
            decoder.Send(new EncodedPacket(1, data, new MediaTime(frame, track.TimeBase)) { IsKeyframe = true });
            decoder.Flush();
            Assert.True(decoder.TryReceive(out YuvVideoFrame? decoded));
            using (decoded)
            {
                YuvRaster<byte> planes = (YuvRaster<byte>)decoded.Planes;
                Compare(encoder.Recon.Y<byte>(), encoder.Recon.Origin, encoder.Recon.Stride, planes.Y, width, height, "Y");
                Compare(encoder.Recon.Cb<byte>(), encoder.Recon.ChromaOrigin, encoder.Recon.ChromaStride, planes.U!, width / 2, height / 2, "Cb");
                Compare(encoder.Recon.Cr<byte>(), encoder.Recon.ChromaOrigin, encoder.Recon.ChromaStride, planes.V!, width / 2, height / 2, "Cr");
                double psnr = Psnr(picture.Y, encoder.Recon.Y<byte>(), encoder.Recon.Origin, encoder.Recon.Stride);
                Assert.True(psnr > MinPsnr(qp), $"PSNR {psnr:F2}");
            }

            decoder.Reset();
        }
    }

    private static double MinPsnr(int qp) => qp switch { <= 10 => 42, <= 26 => 33, <= 30 => 30, <= 45 => 19, _ => 15 };

    private static void Compare(byte[] recon, int origin, int stride, YuvPlane<byte> decoded, int width, int height, string plane)
    {
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = decoded.Row(y);
            for (int x = 0; x < width; x++)
            {
                if (row[x] != recon[origin + (y * stride) + x])
                {
                    Assert.Fail($"{plane} differs at ({x}, {y}): decoded {row[x]}, reconstructed {recon[origin + (y * stride) + x]}.");
                }
            }
        }
    }

    private static double Psnr(YuvPlane<byte> source, byte[] recon, int origin, int stride)
    {
        double sse = 0;
        for (int y = 0; y < source.Height; y++)
        {
            Span<byte> row = source.Row(y);
            for (int x = 0; x < source.Width; x++)
            {
                int e = row[x] - recon[origin + (y * stride) + x];
                sse += e * e;
            }
        }

        double mse = Math.Max(sse / (source.Width * source.Height), 1e-9);
        return 10 * Math.Log10(255.0 * 255.0 / mse);
    }

    /// <summary>A picture with gradients, edges and noise, different per frame.</summary>
    internal static YuvRaster<byte> Picture(int width, int height, int seed)
    {
        YuvRaster<byte> picture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        Random random = new(seed + 1);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = picture.Y.Row(y);
            for (int x = 0; x < width; x++)
            {
                int v = ((x * 3) + (y * 2) + (seed * 7)) & 255;
                v = ((x / 9) + (y / 7)) % 3 == 0 ? 255 - v : v;
                row[x] = (byte)Math.Clamp(v + random.Next(-12, 13), 0, 255);
            }
        }

        for (int y = 0; y < (height + 1) / 2; y++)
        {
            Span<byte> u = picture.U!.Row(y), v = picture.V!.Row(y);
            for (int x = 0; x < (width + 1) / 2; x++)
            {
                u[x] = (byte)(128 + (((x - y) * 3) % 60) + random.Next(-5, 6));
                v[x] = (byte)Math.Clamp(90 + (y * 4 % 120) + random.Next(-5, 6), 0, 255);
            }
        }

        return picture;
    }
}
