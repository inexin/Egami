using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

public class ImagePaintTests
{
    [Fact]
    public void AnIdentityClampedImagePaintEqualsDrawImage()
    {
        using Raster<Rgba8> image = Photo(37, 23);
        using Raster<Rgba8> painted = Raster.Create(64, 48, Rgba8.White), drawn = Raster.Create(64, 48, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = painted.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 37, 23), new ImagePaint(image));
        }

        using (RasterCanvas<Rgba8> canvas = drawn.CreateCanvas())
        {
            canvas.DrawImage(image, Vector2.Zero);
        }

        for (int y = 0; y < 48; y++)
        {
            Assert.True(painted.Row(y).SequenceEqual(drawn.Row(y)), $"row {y}");
        }

        // Moved by a fraction with bilinear sampling, it stays within two levels of DrawImage's transform.
        SamplingOptions bilinear = new() { Filter = ResampleFilter.Bilinear };
        Matrix3x2 shift = Matrix3x2.CreateTranslation(10.5f, 7.25f);
        using Raster<Rgba8> a = Raster.Create(64, 48, Rgba8.White), b = Raster.Create(64, 48, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = a.CreateCanvas())
        {
            canvas.FillRect(new RectF(12, 9, 30, 18), new ImagePaint(image, sampling: bilinear) { LocalMatrix = shift });
        }

        using (RasterCanvas<Rgba8> canvas = b.CreateCanvas())
        {
            canvas.ClipRect(new RectF(12, 9, 30, 18));
            canvas.DrawImage(image, new Vector2(10.5f, 7.25f), bilinear);
        }

        for (int y = 9; y < 27; y++)
        {
            for (int x = 12; x < 42; x++)
            {
                Rgba8 p = a.GetPixel(x, y), q = b.GetPixel(x, y);
                Assert.True(Math.Abs(p.R - q.R) <= 2 && Math.Abs(p.G - q.G) <= 2 && Math.Abs(p.B - q.B) <= 2, $"({x}, {y}): {p} against {q}");
            }
        }
    }

    [Fact]
    public void TileModesRepeatMirrorAndDecal()
    {
        using Raster<Rgba8> image = Photo(10, 6);
        Rgba8[] repeat = Fill(new ImagePaint(image, TileMode.Repeat, TileMode.Repeat), 40, 24);
        Rgba8[] mirror = Fill(new ImagePaint(image, TileMode.Mirror, TileMode.Mirror), 40, 24);
        Rgba8[] clamp = Fill(new ImagePaint(image), 40, 24);
        Rgba8[] mixed = Fill(new ImagePaint(image, TileMode.Repeat, TileMode.Decal), 40, 24);
        for (int y = 0; y < 6; y++)
        {
            for (int x = 0; x < 10; x++)
            {
                Rgba8 p = image.GetPixel(x, y);
                Assert.Equal(p, repeat[(y * 40) + x + 20]);
                Assert.Equal(p, repeat[((y + 12) * 40) + x + 10]);
                Assert.Equal(p, mirror[(y * 40) + (19 - x)]);
                Assert.Equal(p, mirror[((11 - y) * 40) + x]);
                Assert.Equal(p, mixed[(y * 40) + x + 30]);
            }
        }

        Assert.Equal(image.GetPixel(9, 5), clamp[(20 * 40) + 35]);
        Assert.Equal(0, mixed[(10 * 40) + 5].A);
    }

    [Fact]
    public void TheCopyPathEqualsTheFilteredPath()
    {
        using Raster<Rgba8> image = Photo(16, 16);
        ImagePaint paint = new(image, TileMode.Mirror, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Bilinear });
        Rgba8[] copied = Fill(paint with { LocalMatrix = Matrix3x2.CreateTranslation(-3, 5) }, 48, 48);
        Rgba8[] filtered = Fill(paint with { LocalMatrix = Matrix3x2.CreateTranslation(-3.0005f, 5.0005f) }, 48, 48);
        for (int i = 0; i < copied.Length; i++)
        {
            Assert.True(Math.Abs(copied[i].R - filtered[i].R) <= 1 && Math.Abs(copied[i].G - filtered[i].G) <= 1, $"pixel {i}: {copied[i]} against {filtered[i]}");
        }
    }

    [Fact]
    public void AShrunkImageReadsAMipLevelInsteadOfAliasing()
    {
        // One-pixel stripes shrunk eight times average to gray rather than landing on one stripe color.
        using Raster<Rgba8> stripes = Raster.Create<Rgba8>(256, 256);
        for (int y = 0; y < 256; y++)
        {
            Span<Rgba8> row = stripes.Row(y);
            for (int x = 0; x < 256; x++)
            {
                row[x] = x % 2 == 0 ? Rgba8.Black : Rgba8.White;
            }
        }

        Rgba8[] small = Fill(new ImagePaint(stripes, TileMode.Repeat, TileMode.Repeat, new SamplingOptions { Filter = ResampleFilter.Bilinear }) { LocalMatrix = Matrix3x2.CreateScale(1 / 8f) }, 32, 32);
        Assert.All(small, p => Assert.InRange(p.R, 110, 145));
    }

    internal static Raster<Rgba8> Photo(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)(x * 255 / width), (byte)(y * 255 / height), (byte)((((x / 3) + (y / 3)) % 2) * 200), 255);
            }
        }

        return raster;
    }

    private static Rgba8[] Fill(Paint paint, int width, int height)
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.DrawPaint(paint);
        }

        Rgba8[] pixels = new Rgba8[width * height];
        for (int y = 0; y < height; y++)
        {
            raster.Row(y).CopyTo(pixels.AsSpan(y * width, width));
        }

        return pixels;
    }
}
