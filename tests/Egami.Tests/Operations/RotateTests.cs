using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class RotateTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void CanvasIsTheRoundedBoundingBox()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(100, 50);
        Assert.Equal(new Size(112, 93), SizeOf(raster, 30));
        Assert.Equal(new Size(110, 74), SizeOf(raster, 15));
        Assert.Equal(new Size(100, 50), SizeOf(raster, 0.001f));
        Assert.Equal(new Size(112, 93), SizeOf(raster, -30));
        Assert.Equal(new Size(93, 112), SizeOf(raster, 60));
    }

    [Fact]
    public void MultiplesOfNinetyStayExact()
    {
        using Raster<Rgba8> raster = Gradient(31, 17);
        using Raster<Rgba8> turned = raster.Derive(ops => ops.Rotate(450, ResampleFilter.Lanczos3, Rgba8.Red), Ct);
        using Raster<Rgba8> exact = raster.Derive(ops => ops.Rotate(90), Ct);
        AssertClose(exact, turned, 0);
    }

    [Theory]
    [InlineData(ResampleFilter.Bicubic)]
    [InlineData(ResampleFilter.Bilinear)]
    [InlineData(ResampleFilter.Mitchell)]
    [InlineData(ResampleFilter.Lanczos3)]
    [InlineData(ResampleFilter.Box)]
    [InlineData(ResampleFilter.Nearest)]
    public void FlatImagesKeepTheirColorAndGetBackgroundCorners(ResampleFilter filter)
    {
        Rgba8 color = new(200, 40, 90, 255);
        using Raster<Rgba8> raster = Raster.Create(60, 40, color);
        using Raster<Rgba8> rotated = raster.Derive(ops => ops.Rotate(20, filter), Ct);

        // The center is far from every edge, the corners far outside the rotated image.
        Assert.Equal(color, rotated.GetPixel(rotated.Width / 2, rotated.Height / 2));
        Assert.Equal(default, rotated.GetPixel(0, 0));
        Assert.Equal(default, rotated.GetPixel(rotated.Width - 1, rotated.Height - 1));

        // Edges blend toward transparent without darkening: every visible pixel keeps the color.
        int partial = 0;
        for (int y = 0; y < rotated.Height; y++)
        {
            foreach (Rgba8 p in rotated.Row(y))
            {
                if (p.A > 0)
                {
                    Assert.True(Math.Abs(p.R - color.R) <= 1 && Math.Abs(p.G - color.G) <= 1 && Math.Abs(p.B - color.B) <= 1, $"{filter}: {p}");
                    partial += p.A < 255 ? 1 : 0;
                }
            }
        }

        // Nearest and Box take one tap at scale 1, so only the blending filters antialias.
        Assert.True(filter is ResampleFilter.Nearest or ResampleFilter.Box || partial > 0, "The edges should be antialiased.");
    }

    [Fact]
    public void BackgroundFillsTheCorners()
    {
        using Raster<Rgb8> raster = Raster.Create(30, 30, new Rgb8(0, 0, 255));
        using Raster<Rgb8> rotated = raster.Derive(ops => ops.Rotate(45, background: Rgba8.Yellow), Ct);
        Assert.Equal(new Rgb8(255, 255, 0), rotated.GetPixel(0, 0));
        Assert.Equal(new Rgb8(0, 0, 255), rotated.GetPixel(rotated.Width / 2, rotated.Height / 2));
    }

    [Fact]
    public void ChannelOrderIsKept()
    {
        using Raster<Rgba8> rgba = Gradient(50, 40);
        using Raster<Bgra8> bgra = rgba.As<Bgra8>();
        using Raster<Rgba8> a = rgba.Derive(ops => ops.Rotate(25), Ct);
        using Raster<Bgra8> b = bgra.Derive(ops => ops.Rotate(25), Ct);
        using Raster<Rgba8> c = b.As<Rgba8>();
        AssertClose(a, c, 0);
    }

    [Fact]
    public void RotatingThereAndBackKeepsTheImage()
    {
        using Raster<Rgba8> raster = Gradient(120, 80);
        using Raster<Rgba8> back = raster.Derive(ops => ops.Rotate(15).Rotate(-15), Ct);

        // The canvas grows twice; the original sits in its center. Compare away from the edges.
        int ox = (back.Width - raster.Width) / 2;
        int oy = (back.Height - raster.Height) / 2;
        double squared = 0;
        int count = 0;
        for (int y = 20; y < raster.Height - 20; y++)
        {
            for (int x = 20; x < raster.Width - 20; x++)
            {
                Rgba8 a = raster.GetPixel(x, y);
                Rgba8 b = back.GetPixel(x + ox, y + oy);
                squared += Sq(a.R - b.R) + Sq(a.G - b.G) + Sq(a.B - b.B);
                count += 3;
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255.0 / (squared / count));
        Assert.True(psnr > 40, $"PSNR {psnr:F1} dB");

        static double Sq(int v) => (double)v * v;
    }

    [Fact]
    public void ThreadsAndPixelTypesAgree()
    {
        using Raster<Rgba8> raster = Gradient(301, 257);
        using Raster<Rgba8> all = raster.Derive(ops => ops.Rotate(33.3f), Ct);
        using Raster<RgbaF32> floats = raster.As<RgbaF32>();
        using Raster<RgbaF32> rotatedFloats = floats.Derive(ops => ops.Rotate(33.3f), Ct);
        using Raster<Rgba8> fromFloats = rotatedFloats.As<Rgba8>();
        AssertClose(all, fromFloats, 1);

        EgamiContext serial = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> serialRaster = Raster.Create<Rgba8>(raster.Width, raster.Height, serial);
        for (int y = 0; y < raster.Height; y++)
        {
            raster.Row(y).CopyTo(serialRaster.Row(y));
        }

        using Raster<Rgba8> serialRotated = serialRaster.Derive(ops => ops.Rotate(33.3f), Ct);
        AssertClose(all, serialRotated, 0);
    }

    private static Size SizeOf(Raster<Rgba8> raster, float degrees)
    {
        using Raster<Rgba8> result = raster.Derive(ops => ops.Rotate(degrees), Ct);
        return new Size(result.Width, result.Height);
    }

    private static Raster<Rgba8> Gradient(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                double fx = x / (double)width;
                double fy = y / (double)height;
                row[x] = new Rgba8((byte)(255 * fx), (byte)(255 * fy), (byte)(127 + (127 * Math.Sin(6 * (fx + fy)))), 255);
            }
        }

        return raster;
    }

    private static void AssertClose(Raster<Rgba8> expected, Raster<Rgba8> actual, int tolerance)
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            for (int x = 0; x < expected.Width; x++)
            {
                Rgba8 e = expected.GetPixel(x, y);
                Rgba8 a = actual.GetPixel(x, y);
                int diff = Math.Max(Math.Max(Math.Abs(e.R - a.R), Math.Abs(e.G - a.G)), Math.Max(Math.Abs(e.B - a.B), Math.Abs(e.A - a.A)));
                Assert.True(diff <= tolerance || (e.A == 0 && a.A == 0), $"({x}, {y}): {e} vs {a}");
            }
        }
    }
}
