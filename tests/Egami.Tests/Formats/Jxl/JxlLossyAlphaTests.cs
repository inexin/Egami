using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 11: lossy files keep alpha through Modular, exactly by default, at every effort and across groups, or
/// lossy (squeezed and quantized) at AlphaDistance; a
/// premultiplied raster is made straight first; KeepTransparentColors = false lets fully transparent pixels take their
/// neighbours' color; a CMYK raster keeps its black channel. djxl's decode of such files is checked in egami-compare.
/// </summary>
public class JxlLossyAlphaTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Theory]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(7)]
    public void AlphaRoundTripsExactly(int effort)
    {
        using Raster<Rgba8> source = WithAlpha(600, 300);
        using Raster<Rgba8> back = Raster.Load<Rgba8>(Save(source, new JxlWriterOptions { Lossless = false, Effort = effort }));
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source.Row(y)[x].A, back.Row(y)[x].A);
            }
        }
    }

    [Fact]
    public void SixteenBitAlphaRoundTripsExactly()
    {
        using Raster<Rgba16> source = Raster.Create<Rgba16>(80, 60);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.Row(y)[x] = new Rgba16((ushort)(x * 800), (ushort)(y * 1000), 30000, (ushort)((x * 811) + (y * 7)));
            }
        }

        using Raster<Rgba16> back = Raster.Load<Rgba16>(Save(source, new JxlWriterOptions { Lossless = false }));
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Assert.Equal(source.Row(y)[x].A, back.Row(y)[x].A);
            }
        }
    }

    [Fact]
    public void OpaqueAlphaIsLeftOut()
    {
        using Raster<Rgb8> rgb = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        using Raster<Rgba8> rgba = rgb.As<Rgba8>();
        byte[] withAlpha = Save(rgba, new JxlWriterOptions { Lossless = false });
        byte[] without = Save(rgb, new JxlWriterOptions { Lossless = false });
        Assert.Equal(without, withAlpha);
    }

    [Fact]
    public void PremultipliedColorIsMadeStraight()
    {
        using Raster<PremulRgba8> source = Raster.Create<PremulRgba8>(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                // Straight (200, 100, 50) at alpha 128.
                source.Row(y)[x] = new PremulRgba8(100, 50, 25, 128);
            }
        }

        using Raster<Rgba8> back = Raster.Load<Rgba8>(Save(source, new JxlWriterOptions { Lossless = false }));
        Rgba8 p = back.Row(32)[32];
        Assert.Equal(128, p.A);
        Assert.InRange(p.R, 195, 205);
        Assert.InRange(p.G, 95, 105);
    }

    [Fact]
    public void TransparentColorsMayBeSimplified()
    {
        using Raster<Rgba8> source = WithAlpha(256, 128);
        byte[] kept = Save(source, new JxlWriterOptions { Lossless = false });
        byte[] simplified = Save(source, new JxlWriterOptions { Lossless = false, KeepTransparentColors = false });
        Assert.True(simplified.Length < kept.Length, $"simplified {simplified.Length} bytes, kept {kept.Length}");
        using Raster<Rgba8> a = Raster.Load<Rgba8>(kept);
        using Raster<Rgba8> b = Raster.Load<Rgba8>(simplified);
        int visible = 0, near = 0;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                if (source.Row(y)[x].A == 255)
                {
                    visible++;
                    near += Math.Abs(a.Row(y)[x].G - b.Row(y)[x].G) <= 4 ? 1 : 0;
                }
            }
        }

        Assert.True(near > visible * 0.95, $"{near} of {visible} visible pixels alike");
    }

    [Fact]
    public void CmykKeepsItsBlackChannel()
    {
        using Raster<Cmyk8> source = Raster.Create<Cmyk8>(64, 48);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.Row(y)[x] = new Cmyk8((byte)(x * 3), (byte)(y * 4), 40, (byte)(x < 32 ? 0 : 200));
            }
        }

        source.Metadata.IccProfile = ColorProfile.Fogra39.ToIcc();
        using Raster loaded = Raster.Load(Save(source, new JxlWriterOptions { Lossless = false }));
        Assert.Equal(Cmyk8.Layout, loaded.Layout);
        Raster<Cmyk8> back = (Raster<Cmyk8>)loaded;
        Assert.InRange(back.Row(10)[10].K, 0, 1);
        Assert.InRange(back.Row(10)[50].K, 199, 201);
    }

    [Theory]
    [InlineData(1f)]
    [InlineData(4f)]
    public void LossyAlphaIsSmallAndClose(float distance)
    {
        using Raster<Rgba8> source = WithAlpha(600, 300);
        byte[] exact = Save(source, new JxlWriterOptions { Lossless = false });
        byte[] lossy = Save(source, new JxlWriterOptions { Lossless = false, AlphaDistance = distance });
        Assert.True(lossy.Length < exact.Length, $"lossy alpha {lossy.Length} bytes, exact {exact.Length}");
        using Raster<Rgba8> back = Raster.Load<Rgba8>(lossy);
        double se = 0;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                double d = back.Row(y)[x].A - source.Row(y)[x].A;
                se += d * d;
            }
        }

        double rmse = Math.Sqrt(se / (source.Width * source.Height));
        Assert.InRange(rmse, 0.01, 4 * distance);
    }

    [Fact]
    public void AlphaDistanceIsChecked()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { AlphaDistance = -1 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { AlphaDistance = 26 });
        Assert.Throws<ArgumentOutOfRangeException>(() => new JxlWriterOptions { AlphaDistance = float.NaN });
        Assert.Equal(25f, new JxlWriterOptions { AlphaDistance = 25 }.AlphaDistance);
    }

    private static Raster<Rgba8> WithAlpha(int w, int h)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        Raster<Rgba8> r = Raster.Create<Rgba8>(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                Rgb8 p = photo.Row(y % photo.Height)[x % photo.Width];
                byte a = x < w / 4 ? (byte)0 : x < w / 2 ? (byte)255 : (byte)((x * 255 / w) + y);
                r.Row(y)[x] = new Rgba8(p.R, p.G, p.B, a);
            }
        }

        return r;
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, JxlWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, options);
        return file.ToArray();
    }
}
