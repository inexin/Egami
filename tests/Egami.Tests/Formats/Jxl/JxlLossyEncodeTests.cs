using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 4: lossy JPEG XL files (VarDCT in XYB, DCT8, one quantization value, effort 1 and 2) load back in Egami close
/// to their source at every size, gray and color, and the same bytes come out at any thread count. Decoding in djxl and
/// jxl-oxide to the same pixels and the size against cjxl at equal SSIMULACRA2 are egami-compare's JxlRateTests.
/// </summary>
public class JxlLossyEncodeTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Fact]
    public void DuckComesBackCloseAtDistanceOne()
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "duck.png"));
        byte[] lossy = Save(source, new JxlWriterOptions { Lossless = false, Effort = 1 });
        byte[] lossless = Save(source, new JxlWriterOptions { Effort = 1 });
        using Raster<Rgb8> back = Raster.Load<Rgb8>(lossy);
        Assert.Equal((source.Width, source.Height), (back.Width, back.Height));
        double psnr = Psnr(source, back);
        Assert.True(psnr > 35, $"PSNR {psnr:F2} dB");
        Assert.True(lossy.Length * 3 < lossless.Length, $"lossy {lossy.Length} bytes, lossless {lossless.Length}");
        Assert.True(((JxlDetails)back.Metadata.FormatDetails!).IsXybEncoded);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(7, 9)]
    [InlineData(257, 3)]
    [InlineData(300, 600)]
    [InlineData(2100, 40)]
    public void EverySizeComesBack(int width, int height)
    {
        using Raster<Rgb8> source = Smooth(width, height);
        byte[] file = Save(source, new JxlWriterOptions { Lossless = false, Effort = 2 });
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        Assert.Equal((width, height), (back.Width, back.Height));
        double psnr = Psnr(source, back);
        Assert.True(psnr > 25, $"{width}x{height}: PSNR {psnr:F2} dB");
    }

    [Fact]
    public void GrayStaysGray()
    {
        using Raster<Rgb8> color = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "duck.png"));
        using Raster<Gray8> source = color.As<Gray8>();
        byte[] file = Save(source, new JxlWriterOptions { Lossless = false });
        RasterInfo info = Raster.Probe(file);
        Assert.Equal(PixelChannelOrder.Gray, info.Layout.ChannelOrder);
        using Raster<Gray8> back = Raster.Load<Gray8>(file);
        double psnr = Psnr(source, back);
        Assert.True(psnr > 36, $"PSNR {psnr:F2} dB");
    }

    [Fact]
    public void LargerDistancesGiveSmallerFiles()
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        long last = long.MaxValue;
        foreach (float d in (float[])[0.5f, 1, 2, 4, 8])
        {
            long size = Save(source, new JxlWriterOptions { Lossless = false, Distance = d }).Length;
            Assert.True(size < last, $"distance {d}: {size} bytes, not below {last}");
            last = size;
        }
    }

    [Fact]
    public void OutputDoesNotDependOnThreads()
    {
        using Raster<Rgb8> source = Smooth(700, 530);
        byte[] one = Save(source, new JxlWriterOptions { Lossless = false }, new EgamiContext { MaxParallelism = 1 });
        byte[] many = Save(source, new JxlWriterOptions { Lossless = false }, new EgamiContext { MaxParallelism = 8 });
        Assert.Equal(one, many);
    }

    [Fact]
    public void DistanceOutsideItsRangeIsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { Distance = 0.05f });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { Distance = 26 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { Distance = float.NaN });
    }

    private static byte[] Save<T>(Raster<T> raster, JxlWriterOptions options, EgamiContext? context = null)
        where T : unmanaged, IPixelType<T>
    {
        using MemoryStream file = new();
        if (context is null)
        {
            raster.Save(file, JxlFormat.Info, options);
        }
        else
        {
            using Raster<T> copy = Raster.Create<T>(raster.Width, raster.Height, context);
            for (int y = 0; y < raster.Height; y++)
            {
                raster.Row(y).CopyTo(copy.Row(y));
            }

            copy.Save(file, JxlFormat.Info, options);
        }

        return file.ToArray();
    }

    /// <summary>Gradients with hard-edged blue stripes (cjxl's files of it decode to the same pixels; the stripes keep PSNR low).</summary>
    private static Raster<Rgb8> Smooth(int width, int height)
    {
        Raster<Rgb8> r = Raster.Create<Rgb8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgb8> row = r.Row(y);
            for (int x = 0; x < width; x++)
            {
                byte red = (byte)(128 + (100 * Math.Sin(x / 37.0)));
                byte green = (byte)(255 * y / Math.Max(1, height - 1));
                byte blue = (byte)((x + y) % 97 < 48 ? 60 : 190);
                row[x] = new Rgb8(red, green, blue);
            }
        }

        return r;
    }

    private static double Psnr<T>(Raster<T> a, Raster<T> b)
        where T : unmanaged, IPixelType<T>
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

        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / se);
    }
}
