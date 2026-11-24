using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Tests.Codecs;

/// <summary>
/// <see cref="VideoEncoderOptions.CancellationToken"/> in the encoders of Egami.Video, and the bit depth and subsampling read
/// from H.264 and HEVC configuration records.
/// </summary>
public sealed class EncoderCancellationTests
{
    public static TheoryData<string> Codecs => new() { "vp8", "vp9", "av1", "mjpeg", "ffv1", "raw" };

    [Theory]
    [MemberData(nameof(Codecs))]
    public void ACancelledTokenRefusesFrames(string name)
    {
        VideoEncoderOptions options = new() { Width = 32, Height = 32, CancellationToken = new CancellationToken(true) };
        using IVideoEncoder encoder = Codec(name).CreateEncoder(options, MediaContext.Default);
        using YuvRaster<byte> picture = Gray(32, 32);
        Assert.Throws<OperationCanceledException>(() => encoder.Send(new YuvVideoFrame(picture, new MediaTime(0, new Rational(1, 30)))));
    }

    [Fact]
    public void ACancelledTokenStopsFlush()
    {
        using CancellationTokenSource source = new();
        VideoEncoderOptions options = new() { Width = 32, Height = 32, CancellationToken = source.Token };
        using IVideoEncoder encoder = VideoCodecs.Vp9.CreateEncoder(options, MediaContext.Default);
        using YuvRaster<byte> picture = Gray(32, 32);
        for (int i = 0; i < 4; i++)
        {
            encoder.Send(new YuvVideoFrame(picture, new MediaTime(i, new Rational(1, 30))));
        }

        source.Cancel();
        Assert.Throws<OperationCanceledException>(encoder.Flush);
    }

    [Fact]
    public void TheWritersTokenReachesTheEncoder()
    {
        using MemoryStream output = new();
        VideoWriterOptions options = new() { Codec = VideoCodecs.Vp8, CancellationToken = new CancellationToken(true) };
        using VideoWriter writer = VideoWriter.Create(output, "webm", options, leaveOpen: true);
        using YuvRaster<byte> picture = Gray(32, 32);
        Assert.Throws<OperationCanceledException>(() => writer.WriteFrame(picture));
    }

    public static TheoryData<string, byte[], int, ChromaFormat?> Records => new()
    {
        // avcC: Baseline and Main are 8-bit 4:2:0 by definition.
        { CodecIds.Avc, [1, 66, 0xC0, 30, 0xFF, 0xE0, 0], 8, ChromaFormat.Yuv420 },
        { CodecIds.Avc, [1, 77, 0x40, 31, 0xFF, 0xE0, 0], 8, ChromaFormat.Yuv420 },

        // High without the extension is still 8-bit 4:2:0; High 4:2:2 with it says 10-bit 4:2:2; without it, unknown.
        { CodecIds.Avc, [1, 100, 0, 40, 0xFF, 0xE1, 0, 2, 0x67, 0x64, 1, 0, 1, 0x68], 8, ChromaFormat.Yuv420 },
        { CodecIds.Avc, [1, 122, 0, 40, 0xFF, 0xE1, 0, 2, 0x67, 0x7A, 1, 0, 1, 0x68, 0xFE, 0xFA, 0xFA, 0], 10, ChromaFormat.Yuv422 },
        { CodecIds.Avc, [1, 122, 0, 40, 0xFF, 0xE1, 0, 2, 0x67, 0x7A, 1, 0, 1, 0x68], 0, null },

        // hvcC: chroma_format_idc in byte 16, the bit depths less 8 in bytes 17 and 18.
        { CodecIds.Hevc, [1, 2, 0x20, 0, 0, 0, 0x90, 0, 0, 0, 0, 0, 93, 0xF0, 0, 0xFC, 0xFD, 0xFA, 0xFA, 0, 0, 0x0F, 0], 10, ChromaFormat.Yuv420 },
        { CodecIds.Hevc, [1, 4, 0x20, 0, 0, 0, 0x90, 0, 0, 0, 0, 0, 93, 0xF0, 0, 0xFC, 0xFE, 0xF8, 0xF8, 0, 0, 0x0F, 0], 8, ChromaFormat.Yuv422 },

        // Too short, or another codec: unknown.
        { CodecIds.Hevc, [1, 1, 0x60], 0, null },
        { CodecIds.Vp9, [1, 1, 0x60], 0, null },
    };

    [Theory]
    [MemberData(nameof(Records))]
    public void ConfigurationRecordsGiveTheDepthAndSubsampling(string codecId, byte[] record, int depth, ChromaFormat? chroma) =>
        Assert.Equal((depth, chroma), DecoderConfigRecords.Read(codecId, record));

    private static VideoCodec Codec(string name) => name switch
    {
        "vp8" => VideoCodecs.Vp8,
        "vp9" => VideoCodecs.Vp9,
        "av1" => VideoCodecs.Av1,
        "mjpeg" => VideoCodecs.MotionJpeg,
        "ffv1" => VideoCodecs.Ffv1,
        _ => RawVideoCodec.Instance,
    };

    private static YuvRaster<byte> Gray(int width, int height)
    {
        YuvRaster<byte> raster = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        foreach (YuvPlane<byte> plane in (YuvPlane<byte>[])[raster.Y, raster.U!, raster.V!])
        {
            for (int y = 0; y < plane.Height; y++)
            {
                plane.Row(y).Fill(128);
            }
        }

        return raster;
    }
}
