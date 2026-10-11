using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Filters;

/// <summary>Inputs and comparisons shared by the 2.5.4 filter tests.</summary>
internal static class FilterAssert
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "ops");

    internal static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>Loads one of the 1.2 operation images (duck, egret, shapes, zone).</summary>
    internal static Raster<Rgba8> Load(string name, EgamiContext? context = null)
    {
        return Raster.Load<Rgba8>(Path.Combine(Images, name + ".png"), context);
    }

    /// <summary>Asserts a chain gives the same pixels at one thread as at all cores.</summary>
    internal static void SameOnOneThread(string image, Action<RasterOps> chain)
    {
        using Raster<Rgba8> all = Load(image);
        using Raster<Rgba8> one = Load(image, EgamiContext.Default with { MaxParallelism = 1 });
        all.Apply(chain, Ct);
        one.Apply(chain, Ct);
        Same(all, one);
    }

    /// <summary>Asserts a chain run inside <paramref name="area"/> changes the area as it changes the whole image, and nothing else.</summary>
    internal static void SameInsideArea(string image, Rect area, Action<RasterOps> chain)
    {
        using Raster<Rgba8> source = Load(image);
        using Raster<Rgba8> whole = source.Derive(chain, Ct);
        using Raster<Rgba8> scoped = source.Derive(o => o.Within(area, chain), Ct);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                Rgba8 expected = area.Contains(new Point(x, y)) ? whole.Row(y)[x] : source.Row(y)[x];
                if (expected != scoped.Row(y)[x])
                {
                    Assert.Fail($"({x}, {y}): expected {expected}, got {scoped.Row(y)[x]}");
                }
            }
        }
    }

    /// <summary>The PSNR over premultiplied RGB and alpha, in dB; infinity when equal.</summary>
    internal static double Psnr(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        double squares = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<Rgba8> e = expected.Row(y), a = actual.Row(y);
            for (int x = 0; x < e.Length; x++)
            {
                squares += Square(e[x].R * e[x].A, a[x].R * a[x].A) + Square(e[x].G * e[x].A, a[x].G * a[x].A) + Square(e[x].B * e[x].A, a[x].B * a[x].A);
                double da = e[x].A - a[x].A;
                squares += da * da;
            }
        }

        double mse = squares / (4.0 * expected.Width * expected.Height);
        return mse == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / mse);

        static double Square(int e, int a)
        {
            double d = (e - a) / 255.0;
            return d * d;
        }
    }

    /// <summary>The largest difference of any channel, in levels.</summary>
    internal static int MaxDifference(Raster<Rgba8> expected, Raster<Rgba8> actual)
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        int worst = 0;
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<Rgba8> e = expected.Row(y), a = actual.Row(y);
            for (int x = 0; x < e.Length; x++)
            {
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(e[x].R - a[x].R), Math.Abs(e[x].G - a[x].G)), Math.Max(Math.Abs(e[x].B - a[x].B), Math.Abs(e[x].A - a[x].A))));
            }
        }

        return worst;
    }

    /// <summary>Asserts two rasters hold the same pixels, naming the first that differs.</summary>
    internal static void Same<TPixel>(Raster<TPixel> expected, Raster<TPixel> actual)
        where TPixel : unmanaged, IPixelType<TPixel>, IEquatable<TPixel>
    {
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < expected.Height; y++)
        {
            ReadOnlySpan<TPixel> e = expected.Row(y), a = actual.Row(y);
            for (int x = 0; x < e.Length; x++)
            {
                if (!e[x].Equals(a[x]))
                {
                    Assert.Fail($"({x}, {y}): expected {e[x]}, got {a[x]}");
                }
            }
        }
    }
}
