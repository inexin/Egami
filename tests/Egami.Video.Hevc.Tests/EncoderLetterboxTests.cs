using Egami.Pixels;
using Egami.Video.Codecs;

namespace Egami.Video.Hevc.Tests;

/// <summary>
/// Adaptive quantization with letterbox bars: the flat black bars are left out of the picture's mean variance, so the content
/// between them is coded about as finely as the same content without bars, as the H.264 encoder's (it was coarser).
/// </summary>
public sealed class EncoderLetterboxTests
{
    private const int Width = 128, Height = 96, Bar = 32, Frames = 4;

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
        int height = Height + (2 * bar);
        VideoEncoderOptions options = new()
        {
            Width = Width,
            Height = height,
            Effort = 4,
            KeyframeInterval = 100,
            Hevc = new HevcEncoderOptions { AdaptiveQuantization = true },
        };
        (List<EncodedPacket> packets, _, VideoTrackInfo track) = EncoderTests.Encode(options, Frames, f => Boxed(f, bar));
        bytes = packets.Sum(p => (long)p.Data.Length);
        List<ushort[]> decoded = EncoderTests.Decode(track, packets);
        double sum = 0;
        for (int f = 0; f < Frames; f++)
        {
            using YuvRaster<byte> source = Boxed(f, bar);
            long squares = 0;
            for (int y = bar; y < bar + Height; y++)
            {
                ReadOnlySpan<byte> a = source.Y.Row(y);
                for (int x = 0; x < Width; x++)
                {
                    int d = a[x] - decoded[f][(y * Width) + x];
                    squares += d * d;
                }
            }

            sum += 10 * Math.Log10(255.0 * 255 / Math.Max((double)squares / (Width * Height), 1e-10));
        }

        EncoderTests.Dispose(packets);
        return sum / Frames;
    }

    // EncoderTests.Picture's content between black bars of limited-range black.
    private static YuvRaster<byte> Boxed(int frame, int bar)
    {
        using YuvRaster<byte> content = (YuvRaster<byte>)EncoderTests.Picture(Width, Height, frame, 8);
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
            Span<byte> cb = picture.U!.Row(y), cr = picture.V!.Row(y);
            if (y < bar / 2 || y >= (bar / 2) + (Height / 2))
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
