using System.Numerics;

using Egami.Formats.Extended.Raw;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// The lossless JPEG writer's CFA coding, which DNG tiles use, read back by the RAW reader's own lossless JPEG decoder: a
/// mosaic coded as two components half as wide comes back photosite for photosite.
/// </summary>
public class DngTileCodingTests
{
    [Theory]
    [InlineData(256, 256, 14)]
    [InlineData(256, 256, 16)]
    [InlineData(32, 7, 12)]
    [InlineData(2, 1, 10)]
    public void ACfaTileDecodesThroughTheRawReader(int width, int height, int bits)
    {
        ushort[] mosaic = Mosaic(width, height, bits, seed: width + bits);
        using MemoryStream output = new();
        JpegLosslessWriter.WriteCfa(output, mosaic, width, bits);
        byte[] tile = output.ToArray();

        LosslessJpeg jpeg = LosslessJpeg.Open(tile, 0, tile.Length);
        Assert.Equal((width / 2, height, 2, bits), (jpeg.Width, jpeg.Height, jpeg.Components, jpeg.Precision));
        ushort[] back = new ushort[jpeg.SampleCount];
        Assert.True(jpeg.Decode(back));
        Assert.Equal(mosaic, back);
    }

    /// <summary>
    /// Measures the two-component coding against one component (both predictor 1) on the CFA files of a RAW corpus, cut into
    /// 256 × 256 tiles as DNG writes them. Runs only when EGAMI_RAW_CORPUS names a folder of RAW files.
    /// </summary>
    [Fact]
    public void TwoComponentsAgainstOneOnACorpus()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_RAW_CORPUS");
        Assert.SkipWhen(folder is null || !Directory.Exists(folder), "Set EGAMI_RAW_CORPUS to a folder of RAW files to measure.");
        long oneTotal = 0, twoTotal = 0;
        foreach (string path in Directory.EnumerateFiles(folder!).Order())
        {
            Raster<Gray16> mosaic;
            try
            {
                mosaic = Raster.Load<Gray16>(path, TestImages.Context, new RawReaderOptions { Output = RawOutput.Mosaic });
            }
            catch (Exception e) when (e is NotSupportedException or UnsupportedFormatException or InvalidDataException or CorruptDataException or InvalidCastException)
            {
                continue;
            }

            using (mosaic)
            {
                if (mosaic.Metadata.FormatDetails is not RawDetails { CfaPattern: not null })
                {
                    continue;
                }

                (long one, long two) = Measure(mosaic);
                oneTotal += one;
                twoTotal += two;
                TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: {mosaic.Width} × {mosaic.Height}, one component {one:N0} B, two {two:N0} B, {100.0 * (two - one) / one:+0.00;-0.00} %");
            }
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"Total: one component {oneTotal:N0} B, two {twoTotal:N0} B, {100.0 * (twoTotal - oneTotal) / Math.Max(1, oneTotal):+0.00;-0.00} %");
    }

    /// <summary>Codes a mosaic in 256 × 256 tiles, edges repeated, both ways; returns the total bytes of each.</summary>
    private static (long One, long Two) Measure(Raster<Gray16> mosaic)
    {
        const int Tile = 256;
        int max = 0;
        for (int y = 0; y < mosaic.Height; y++)
        {
            foreach (Gray16 p in mosaic.Row(y))
            {
                max = Math.Max(max, p.L);
            }
        }

        int bits = Math.Max(2, 32 - BitOperations.LeadingZeroCount((uint)max));
        ushort[] tile = new ushort[Tile * Tile];
        long one = 0, two = 0;
        using MemoryStream output = new();
        for (int ty = 0; ty < mosaic.Height; ty += Tile)
        {
            for (int tx = 0; tx < mosaic.Width; tx += Tile)
            {
                for (int y = 0; y < Tile; y++)
                {
                    ReadOnlySpan<Gray16> row = mosaic.Row(Math.Min(ty + y, mosaic.Height - 1));
                    for (int x = 0; x < Tile; x++)
                    {
                        // Past the edge, repeat the last pair so the colours stay in their columns.
                        int sx = tx + x < mosaic.Width ? tx + x : mosaic.Width - 2 + ((tx + x - mosaic.Width) & 1);
                        tile[(y * Tile) + x] = row[Math.Max(0, sx)].L;
                    }
                }

                output.SetLength(0);
                JpegLosslessWriter.Write(output, tile, 1, Tile, bits, 1);
                one += output.Length;
                output.SetLength(0);
                JpegLosslessWriter.WriteCfa(output, tile, Tile, bits);
                two += output.Length;
            }
        }

        return (one, two);
    }

    private static ushort[] Mosaic(int width, int height, int bits, int seed)
    {
        Random random = new(seed);
        int max = (1 << bits) - 1;
        ushort[] mosaic = new ushort[width * height];
        for (int i = 0; i < mosaic.Length; i++)
        {
            int x = i % width, y = i / width;
            int level = (int)((long)max * (x + y + 1) / (width + height + 1)) >> ((x & 1) + (y & 1));
            mosaic[i] = (ushort)Math.Clamp(level + random.Next(-64, 65), 0, max);
        }

        return mosaic;
    }
}
