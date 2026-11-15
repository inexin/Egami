using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// CAVLC's level_prefix below High is at most 15 (9.2.2.1): at the lowest quantizers the DC levels of Intra16x16 luma and of
/// chroma could need 16 on hard edges and noise, which only High profile decoders read.
/// </summary>
[Collection(nameof(EncoderStatsRunsAlone))]
public sealed class EncoderCavlcLevelTests
{
    [Theory]
    [InlineData(0, 0)]
    [InlineData(0, 6)]
    [InlineData(1, 0)]
    [InlineData(2, 0)]
    public void LevelPrefixesStayWithinMainProfile(int content, int quantizer)
    {
        int longest = 0;
        H264MbSyntax.LongLevelPrefix = prefix =>
        {
            lock (this)
            {
                longest = Math.Max(longest, prefix);
            }
        };
        try
        {
            VideoEncoderOptions options = new()
            {
                Width = 64,
                Height = 64,
                Quantizer = quantizer,
                Effort = 4,
                KeyframeInterval = 2,
                H264 = new H264EncoderOptions
                {
                    Profile = H264Profile.Main,
                    Entropy = H264EntropyCoder.Cavlc,
                    AdaptiveQuantization = false,
                    MinQuantizer = 0,
                },
            };
            EncoderInterTests.Check(options, 3, (w, h, f) => Make(w, h, f, content));
        }
        finally
        {
            H264MbSyntax.LongLevelPrefix = null;
        }

        Assert.InRange(longest, 0, 15);
    }

    // Hard luma edges (4-pixel checks, random noise or 16-pixel checks) and saturated chroma stripes, moving each frame.
    internal static YuvRaster<byte> Make(int width, int height, int frame, int content)
    {
        YuvRaster<byte> picture = YuvRaster.Create(width, height, ChromaFormat.Yuv420, YuvColor.Bt601Limited);
        Random random = new((frame * 31) + content);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = picture.Y.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = content switch
                {
                    0 => (byte)(((x >> 2) + (y >> 2) + frame) % 2 == 0 ? 0 : 255),
                    1 => (byte)random.Next(256),
                    _ => (byte)(((x / 16) + (y / 16) + frame) % 2 == 0 ? 0 : 255),
                };
            }
        }

        for (int y = 0; y < height / 2; y++)
        {
            Span<byte> cb = picture.U!.Row(y), cr = picture.V!.Row(y);
            for (int x = 0; x < width / 2; x++)
            {
                cb[x] = (byte)(((x >> 3) + frame) % 2 == 0 ? 0 : 255);
                cr[x] = (byte)(((y >> 3) + frame) % 2 == 0 ? 255 : 0);
            }
        }

        return picture;
    }
}
