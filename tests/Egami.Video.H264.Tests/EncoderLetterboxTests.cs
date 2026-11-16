using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// Adaptive quantization with letterbox bars: the flat black bars are left out of the picture's mean energy, so the content
/// between them is coded about as finely as the same content without bars (it was several QPs coarser).
/// </summary>
[Collection(nameof(EncoderStatsRunsAlone))]
public sealed class EncoderLetterboxTests
{
    private const int Width = 128, Height = 96, Bar = 32, Frames = 6;

    [Fact]
    public void BarsDoNotCoarsenTheContent()
    {
        double plain = ContentPsnr(0, out long plainBytes);
        double boxed = ContentPsnr(Bar, out long boxedBytes);
        Assert.True(boxed > plain - 0.5, $"Content PSNR-Y {boxed:F2} dB with bars, {plain:F2} dB without.");
        Assert.True(boxedBytes > plainBytes * 0.9, $"{boxedBytes} bytes with bars, {plainBytes} without.");
    }

    // The mean PSNR-Y of the content rows (between the bars) over the frames, encoded at effort 4 with adaptive quantization.
    private static double ContentPsnr(int bar, out long bytes)
    {
        VideoEncoderOptions options = new()
        {
            Width = Width,
            Height = Height + (2 * bar),
            Effort = 4,
            KeyframeInterval = 100,
            H264 = new H264EncoderOptions { AdaptiveQuantization = true },
        };
        List<YuvRaster<byte>> sources = [.. Enumerable.Range(0, Frames).Select(f => Boxed(f, bar))];
        List<EncodedPacket> packets = EncoderInterTests.Check(options, Frames, (_, _, f) => Boxed(f, bar));
        bytes = packets.Sum(p => (long)p.Data.Length);
        VideoTrackInfo track = Track(options, packets);
        List<YuvRaster<byte>> decoded = H264EncoderTests.Decode(track, packets);
        double sum = 0;
        for (int f = 0; f < Frames; f++)
        {
            long squares = 0;
            for (int y = bar; y < bar + Height; y++)
            {
                ReadOnlySpan<byte> a = sources[f].Y.Row(y), b = decoded[f].Y.Row(y);
                for (int x = 0; x < Width; x++)
                {
                    int d = a[x] - b[x];
                    squares += d * d;
                }
            }

            sum += 10 * Math.Log10(255.0 * 255 / Math.Max((double)squares / (Width * Height), 1e-10));
            sources[f].Dispose();
            decoded[f].Dispose();
        }

        foreach (EncodedPacket packet in packets)
        {
            packet.Dispose();
        }

        return sum / Frames;
    }

    private static VideoTrackInfo Track(VideoEncoderOptions options, List<EncodedPacket> packets)
    {
        // The track after the first frame (its parameter sets), as the encoder states it.
        using H264VideoEncoder encoder = (H264VideoEncoder)H264Codec.Instance.CreateEncoder(options, MediaContext.Default);
        using YuvRaster<byte> first = Boxed(0, (options.Height - Height) / 2);
        encoder.Send(new YuvVideoFrame(first, new MediaTime(0, options.FrameRate.Reciprocal())));
        return encoder.Track;
    }

    // EncoderInterTests.Moving's content between black bars of limited-range black.
    private static YuvRaster<byte> Boxed(int frame, int bar)
    {
        using YuvRaster<byte> content = EncoderInterTests.Moving(Width, Height, frame);
        YuvRaster<byte> picture = YuvRaster.Create(Width, Height + (2 * bar), ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        for (int y = 0; y < Height + (2 * bar); y++)
        {
            Span<byte> row = picture.Y.Row(y);
            if (y < bar || y >= bar + Height)
            {
                row.Fill(16);
            }
            else
            {
                content.Y.Row(y - bar).CopyTo(row);
            }
        }

        for (int y = 0; y < (Height / 2) + bar; y++)
        {
            bool inBar = y < bar / 2 || y >= (bar / 2) + (Height / 2);
            Span<byte> cb = picture.U!.Row(y), cr = picture.V!.Row(y);
            if (inBar)
            {
                cb.Fill(128);
                cr.Fill(128);
            }
            else
            {
                content.U!.Row(y - (bar / 2)).CopyTo(cb);
                content.V!.Row(y - (bar / 2)).CopyTo(cr);
            }
        }

        return picture;
    }
}
