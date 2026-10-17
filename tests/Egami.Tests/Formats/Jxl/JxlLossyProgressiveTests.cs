using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 13: Progressive files (cjxl -p: an LF frame and three HF passes) decode to the same image as plain ones
/// within lossy rounding, at every effort, with alpha and at a half-size frame; their LF image decodes alone at MaxDownscale
/// 8; ReadPreview finds no preview in them. djxl's decode of them was checked in Docker.
/// </summary>
public class JxlLossyProgressiveTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Theory]
    [InlineData(3, 1f)]
    [InlineData(5, 1f)]
    [InlineData(7, 3f)]
    [InlineData(5, 12f)]
    public void ProgressiveDecodesLikePlain(int effort, float distance)
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        byte[] progressive = Save(source, new JxlWriterOptions { Lossless = false, Effort = effort, Distance = distance, Progressive = true });
        byte[] plain = Save(source, new JxlWriterOptions { Lossless = false, Effort = effort, Distance = distance });
        Assert.InRange(progressive.Length, plain.Length * 0.95, plain.Length * 1.15);
        using Raster<Rgb8> a = Raster.Load<Rgb8>(progressive);
        using Raster<Rgb8> b = Raster.Load<Rgb8>(plain);
        double se = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                double d = a.Row(y)[x].G - b.Row(y)[x].G;
                se += d * d;
            }
        }

        Assert.InRange(se / (a.Width * a.Height), 0, 2 * distance);
    }

    [Fact]
    public void LfImageDecodesAlone()
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        byte[] file = Save(source, new JxlWriterOptions { Lossless = false, Progressive = true });
        using Raster<RgbF32> full = Raster.Load<RgbF32>(file);
        using Raster<RgbF32> lf = Raster.Load<RgbF32>(file, options: new JxlReaderOptions { MaxDownscale = 8 });
        Assert.Equal(((full.Width + 7) / 8, (full.Height + 7) / 8), (lf.Width, lf.Height));
        double total = 0;
        for (int by = 0; by < full.Height / 8; by++)
        {
            for (int bx = 0; bx < full.Width / 8; bx++)
            {
                float sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        sum += full.Row((by * 8) + y)[(bx * 8) + x].G;
                    }
                }

                total += Math.Abs((sum / 64) - lf.Row(by)[bx].G);
            }
        }

        Assert.InRange(total / ((full.Width / 8) * (full.Height / 8)), 0, 0.02);
        Assert.Null(JxlImage.ReadPreview(new MemoryStream(file)));
    }

    [Fact]
    public void AlphaStaysExact()
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        using Raster<Rgba8> source = Raster.Create<Rgba8>(photo.Width, photo.Height);
        for (int y = 0; y < photo.Height; y++)
        {
            for (int x = 0; x < photo.Width; x++)
            {
                Rgb8 p = photo.Row(y)[x];
                source.Row(y)[x] = new Rgba8(p.R, p.G, p.B, (byte)(x + y));
            }
        }

        using Raster<Rgba8> back = Raster.Load<Rgba8>(Save(source, new JxlWriterOptions { Lossless = false, Effort = 7, Progressive = true }));
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source.Row(y)[x].A, back.Row(y)[x].A);
            }
        }
    }

    [Fact]
    public void TinyFrameWithPatchesLoads()
    {
        // Task 16's fuzz finding: a 2×2 frame at effort 7 has patches, more than the 2.9.3 decoder's limit of one per 16 pixels
        // allowed; djxl takes up to libjxl's 1,024 plus one per 4 pixels, and so does Egami now.
        using Raster<Rgba8> source = Raster.Create<Rgba8>(2, 2);
        source.Row(0)[0] = new Rgba8(0xFF, 0x00, 0x06, 0x04);
        source.Row(0)[1] = new Rgba8(0x42, 0x52, 0x40, 0x39);
        source.Row(1)[0] = new Rgba8(0x37, 0xE1, 0x69, 0x1E);
        source.Row(1)[1] = new Rgba8(0x0B, 0xC6, 0x63, 0x2E);
        byte[] file = Save(source, new JxlWriterOptions { Lossless = false, Effort = 7, Distance = 4.6f, Progressive = true });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
        Assert.Equal((2, 2), (back.Width, back.Height));
        Assert.Equal(source.Row(1)[1].A, back.Row(1)[1].A);
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, JxlWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, options);
        return file.ToArray();
    }
}
