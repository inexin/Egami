using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Filters;

/// <summary>Pixelate, OilPaint, Kuwahara and Spread: exact averages, ImageMagick's oil paint rules, and Spread's properties.</summary>
public class ArtisticTests
{
    private static CancellationToken Ct => FilterAssert.Ct;

    /// <summary>The premultiplied average of each cell, in whole numbers rounded to nearest, written independently of the filter.</summary>
    private static Rgba8 CellAverage(Raster<Rgba8> source, int x0, int y0, int x1, int y1)
    {
        long r = 0, g = 0, b = 0, a = 0, n = 0;
        for (int y = Math.Max(0, y0); y < Math.Min(source.Height, y1); y++)
        {
            for (int x = Math.Max(0, x0); x < Math.Min(source.Width, x1); x++)
            {
                Rgba8 p = source.Row(y)[x];
                r += p.R * p.A;
                g += p.G * p.A;
                b += p.B * p.A;
                a += p.A;
                n++;
            }
        }

        static byte Round(long sum, long count) => (byte)Math.Floor((sum / (double)count) + 0.5);
        return a == 0 ? new Rgba8(0, 0, 0, Round(0, n)) : new Rgba8(Round(r, a), Round(g, a), Round(b, a), Round(a, n));
    }

    [Trait("Category", "Slow")]
    [Theory]
    [InlineData(8, 0, 0)]
    [InlineData(7, 3, -2)]
    [InlineData(16, -5, 9)]
    [InlineData(500, 0, 0)]
    public void PixelateGivesEachCellItsExactPremultipliedAverage(int cell, int ox, int oy)
    {
        using Raster<Rgba8> source = FilterAssert.Load("shapes");
        using Raster<Rgba8> result = source.Derive(o => o.Pixelate(cell, new Point(ox, oy)), Ct);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                int cx = ox + (int)Math.Floor((x - ox) / (double)cell) * cell, cy = oy + (int)Math.Floor((y - oy) / (double)cell) * cell;
                Rgba8 expected = CellAverage(source, cx, cy, cx + cell, cy + cell);
                Assert.True(expected == result.Row(y)[x], $"({x}, {y}): expected {expected}, got {result.Row(y)[x]}");
            }
        }
    }

    [Fact]
    public void PixelateIgnoresTransparentColor()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(4, 2);
        source.Row(0)[0] = new Rgba8(255, 0, 0, 0);
        source.Row(0)[1] = new Rgba8(0, 0, 255, 255);
        source.Row(1)[0] = new Rgba8(0, 0, 255, 255);
        source.Row(1)[1] = new Rgba8(0, 0, 255, 128);
        source.Apply(o => o.Pixelate(2), Ct);
        Assert.Equal(new Rgba8(0, 0, 255, 160), source.Row(0)[0]);
        Assert.Equal(new Rgba8(0, 0, 0, 0), source.Row(1)[3]);
    }

    [Fact]
    public void PixelateOnFloatsMatchesTheBytes()
    {
        using Raster<Rgba8> source = FilterAssert.Load("shapes");
        using Raster<Rgba8> bytes = source.Derive(o => o.Pixelate(6, new Point(2, 1)), Ct);
        using Raster<RgbaF32> floats = source.As<RgbaF32>();
        floats.Apply(o => o.Pixelate(6, new Point(2, 1)), Ct);
        using Raster<Rgba8> back = floats.As<Rgba8>();
        Assert.True(FilterAssert.MaxDifference(bytes, back) <= 1);
    }

    [Fact]
    public void OilPaintPicksImageMagicksPixelOnTies()
    {
        // A 3 × 3 window holding three bins twice each and three once: bins 10, 200 and 90 have the highest count (2); their last
        // pixels in the scan are at indices 4, 6 and 7, so bin 10's last pixel (index 4, the centre) wins.
        byte[] grays = [10, 200, 90, 30, 10, 60, 200, 90, 250];
        using Raster<Rgba8> source = Raster.Create<Rgba8>(3, 3);
        for (int i = 0; i < 9; i++)
        {
            byte v = grays[i];
            source.Row(i / 3)[i % 3] = new Rgba8(v, v, v, (byte)(100 + i));
        }

        using Raster<Rgba8> result = source.Derive(o => o.OilPaint(1, 256), Ct);
        Assert.Equal(source.Row(1)[1], result.Row(1)[1]);
    }

    [Fact]
    public void OilPaintOutputsPixelsOfTheImage()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> result = source.Derive(o => o.OilPaint(4, 20), Ct);
        HashSet<Rgba8> colors = [];
        for (int y = 0; y < source.Height; y++)
        {
            foreach (Rgba8 p in source.Row(y))
            {
                colors.Add(p);
            }
        }

        for (int y = 0; y < result.Height; y++)
        {
            foreach (Rgba8 p in result.Row(y))
            {
                Assert.Contains(p, colors);
            }
        }
    }

    [Fact]
    public void OilPaintIsTheSameForBgra()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> rgba = source.Derive(o => o.OilPaint(3, 256), Ct);
        using Raster<Bgra8> bgra = source.As<Bgra8>();
        bgra.Apply(o => o.OilPaint(3, 256), Ct);
        using Raster<Rgba8> back = bgra.As<Rgba8>();
        FilterAssert.Same(rgba, back);

        using Raster<Rgba16> wide = source.As<Rgba16>();
        wide.Apply(o => o.OilPaint(3, 256), Ct);
        using Raster<Rgba8> narrow = wide.As<Rgba8>();
        FilterAssert.Same(rgba, narrow);
    }

    [Fact]
    public void KuwaharaKeepsAFlatImage()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(30, 20);
        source.Apply(o => o.FillColor(new Rgba8(90, 140, 200, 255)), Ct);
        using Raster<Rgba8> result = source.Derive(o => o.Kuwahara(4, 1), Ct);
        FilterAssert.Same(source, result);
    }

    [Fact]
    public void KuwaharaKeepsAStepSharp()
    {
        using Raster<Rgba8> source = Raster.Create<Rgba8>(40, 10);
        source.Apply(o => o.FillColor(Rgba8.White).FillColor(Rgba8.Black, new Rect(0, 0, 20, 10)), Ct);
        using Raster<Rgba8> result = source.Derive(o => o.Kuwahara(3), Ct);
        FilterAssert.Same(source, result);
    }

    [Theory]
    [InlineData(1.5f, 0UL)]
    [InlineData(4f, 7UL)]
    [InlineData(10.7f, 12345UL)]
    public void SpreadMovesNoPixelFurtherThanTheRadius(float radius, ulong seed)
    {
        // Every pixel's color names its position, so each output pixel tells where it came from.
        using Raster<Rgba8> source = Raster.Create<Rgba8>(160, 120);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.Row(y)[x] = new Rgba8((byte)x, (byte)y, 0, 255);
            }
        }

        using Raster<Rgba8> result = source.Derive(o => o.Spread(radius, seed), Ct);
        int moved = 0;
        for (int y = 0; y < result.Height; y++)
        {
            for (int x = 0; x < result.Width; x++)
            {
                Rgba8 p = result.Row(y)[x];
                double distance = Math.Sqrt(((p.R - x) * (p.R - x)) + ((p.G - y) * (p.G - y)));
                Assert.True(distance <= radius, $"({x}, {y}) took ({p.R}, {p.G})");
                moved += distance > 0 ? 1 : 0;
            }
        }

        Assert.True(moved > result.Width * result.Height / 2);
    }

    [Fact]
    public void SpreadDependsOnTheSeedOnly()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        using Raster<Rgba8> a = source.Derive(o => o.Spread(3, 1), Ct);
        using Raster<Rgba8> b = source.Derive(o => o.Spread(3, 1), Ct);
        using Raster<Rgba8> c = source.Derive(o => o.Spread(3, 2), Ct);
        FilterAssert.Same(a, b);
        Assert.True(FilterAssert.MaxDifference(a, c) > 0);
    }

    [Fact]
    public void SpreadIsTheSameOnLargeImagesAtOneThread()
    {
        using Raster<Rgba8> all = Raster.Create<Rgba8>(1500, 900);
        using Raster<Rgba8> one = Raster.Create<Rgba8>(1500, 900, EgamiContext.Default with { MaxParallelism = 1 });
        for (int y = 0; y < all.Height; y++)
        {
            for (int x = 0; x < all.Width; x++)
            {
                all.Row(y)[x] = one.Row(y)[x] = new Rgba8((byte)x, (byte)y, (byte)(x >> 8), 255);
            }
        }

        all.Apply(o => o.Spread(6, 99), Ct);
        one.Apply(o => o.Spread(6, 99), Ct);
        FilterAssert.Same(all, one);
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        using Raster<Rgba8> source = FilterAssert.Load("duck");
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Pixelate(0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Pixelate(5000), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.OilPaint(0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.OilPaint(51), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.OilPaint(3, 1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.OilPaint(3, 257), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Kuwahara(0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Kuwahara(3, -1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Kuwahara(3, float.NaN), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Spread(-1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => source.Apply(o => o.Spread(float.PositiveInfinity), Ct));
    }

    public static TheoryData<string> Chains() => ["pixelate", "oil-paint", "oil-paint-256", "kuwahara", "kuwahara-blur", "spread"];

    private static Action<RasterOps> Chain(string name) => name switch
    {
        "pixelate" => o => o.Pixelate(9, new Point(4, -3)),
        "oil-paint" => o => o.OilPaint(3),
        "oil-paint-256" => o => o.OilPaint(5, 256),
        "kuwahara" => o => o.Kuwahara(3),
        "kuwahara-blur" => o => o.Kuwahara(2, 1.2f),
        _ => o => o.Spread(4.5f, 3),
    };

    [Theory]
    [MemberData(nameof(Chains))]
    public void SameOnOneThread(string name)
    {
        FilterAssert.SameOnOneThread("duck", Chain(name));
    }

    [Theory]
    [MemberData(nameof(Chains))]
    public void SameInsideArea(string name)
    {
        FilterAssert.SameInsideArea("shapes", new Rect(37, 21, 90, 70), Chain(name));
    }
}
