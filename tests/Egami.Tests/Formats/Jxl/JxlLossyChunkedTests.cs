using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;
using Egami.Streaming;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 14: lossy files are the same bytes at every thread count; images libjxl codes in chunks (more than 8
/// groups, not effort 7 from distance 3, not progressive or at half size) are coded per LF group, decode, keep alpha exact,
/// and come out the same whether saved from a raster or written as rows. The memory bound on a 16k by 16k image and the size
/// against cjxl's streaming mode are egami-compare's.
/// </summary>
public class JxlLossyChunkedTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData(600, 400, 3)]
    [InlineData(600, 400, 7)]
    [InlineData(2300, 1100, 3)]
    [InlineData(2300, 1100, 6)]
    public void SameBytesAtEveryThreadCount(int width, int height, int effort)
    {
        using Raster<Rgb8> source = Tiled(width, height);
        JxlWriterOptions options = new() { Lossless = false, Effort = effort };
        byte[] one = Save(source, options, 1);
        foreach (int threads in (int[])[2, 3, 8])
        {
            Assert.Equal(one, Save(source, options, threads));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(7)]
    public void ChunkedFilesDecode(int effort)
    {
        using Raster<Rgb8> source = Tiled(2300, 1100);
        byte[] file = Save(source, new JxlWriterOptions { Lossless = false, Effort = effort }, 4);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        Assert.True(Psnr(source, back) > 33, $"PSNR {Psnr(source, back):F1}");
    }

    [Trait("Category", "Slow")]
    [Fact]
    public void AlphaStaysExactInChunks()
    {
        using Raster<Rgb8> photo = Tiled(2300, 1100);
        using Raster<Rgba8> source = Raster.Create<Rgba8>(photo.Width, photo.Height);
        for (int y = 0; y < photo.Height; y++)
        {
            for (int x = 0; x < photo.Width; x++)
            {
                Rgb8 p = photo.Row(y)[x];
                source.Row(y)[x] = new Rgba8(p.R, p.G, p.B, (byte)((x / 3) + y));
            }
        }

        using Raster<Rgba8> back = Raster.Load<Rgba8>(Save(source, new JxlWriterOptions { Lossless = false, Effort = 5 }, 4));
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source.Row(y)[x].A, back.Row(y)[x].A);
            }
        }
    }

    [Fact]
    public void RowsGiveTheSameFileAsARaster()
    {
        using Raster<Rgb8> source = Tiled(2300, 1100);
        JxlWriterOptions options = new() { Lossless = false, Effort = 4 };
        byte[] whole = Save(source, options, 2);
        using MemoryStream file = new();
        using (RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(file, JxlFormat.Info, source.Width, source.Height, options, context: new EgamiContext { MaxParallelism = 2 }))
        {
            using Raster<Rgb8> band = Raster.Create<Rgb8>(source.Width, 100);
            for (int y = 0; y < source.Height; y += 100)
            {
                int rows = Math.Min(100, source.Height - y);
                for (int r = 0; r < rows; r++)
                {
                    source.Row(y + r).CopyTo(band.Row(r));
                }

                encoder.WriteRows(band, rows);
            }

            encoder.Complete();
        }

        Assert.Equal(whole, file.ToArray());
    }

    [Fact]
    public void SmallImagesNeedTheWholeRaster()
    {
        using MemoryStream file = new();
        Assert.Throws<NotSupportedException>(() =>
        {
            using RasterEncoder<Rgb8> encoder = RasterEncoder.Create<Rgb8>(file, JxlFormat.Info, 300, 200, new JxlWriterOptions { Lossless = false });
        });
    }

    private static Raster<Rgb8> Tiled(int width, int height)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        Raster<Rgb8> r = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // Mirrored tiles, so the image has no seams of its own.
                int tx = x / photo.Width % 2 == 0 ? x % photo.Width : photo.Width - 1 - (x % photo.Width);
                int ty = y / photo.Height % 2 == 0 ? y % photo.Height : photo.Height - 1 - (y % photo.Height);
                r.Row(y)[x] = photo.Row(ty)[tx];
            }
        }

        return r;
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, JxlWriterOptions options, int threads)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        using Raster<TPixel> copy = Raster.Create<TPixel>(raster.Width, raster.Height, new EgamiContext { MaxParallelism = threads });
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(copy.Row(y));
        }

        copy.Save(file, JxlFormat.Info, options);
        return file.ToArray();
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double se = 0;
        long n = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> ra = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.Row(y));
            ReadOnlySpan<byte> rb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Row(y));
            for (int i = 0; i < ra.Length; i++)
            {
                double d = ra[i] - rb[i];
                se += d * d;
                n++;
            }
        }

        return 10 * Math.Log10(255.0 * 255 / (se / n));
    }
}
