using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

using static Egami.Tests.Operations.Filters.FilterAssert;

namespace Egami.Tests.Operations.Filters;

/// <summary>Phase 2.5.4 task 3: Median on every path against a direct sort, and its properties.</summary>
public class MedianTests
{
    [Theory]
    [InlineData(9)]
    [InlineData(25)]
    [InlineData(49)]
    public void NetworkFindsTheMedian(int count)
    {
        SelectionNetwork network = SelectionNetwork.Median(count);
        Random random = new(count);
        byte[] values = new byte[count];
        for (int trial = 0; trial < 20000; trial++)
        {
            // Small value ranges give many ties, which networks get wrong first.
            int range = trial % 3 == 0 ? 3 : 256;
            for (int i = 0; i < count; i++)
            {
                values[i] = (byte)random.Next(range);
            }

            byte[] sorted = [.. values.Order()];
            network.Run(values);
            Assert.Equal(sorted[count / 2], values[count / 2]);
        }
    }

    [Fact]
    public void NetworkForNineHoldsForEveryZeroOneInput()
    {
        // The 0-1 principle: a comparator network that selects correctly on every 0-1 input selects correctly on any input.
        SelectionNetwork network = SelectionNetwork.Median(9);
        byte[] values = new byte[9];
        for (int bits = 0; bits < 512; bits++)
        {
            int ones = 0;
            for (int i = 0; i < 9; i++)
            {
                values[i] = (byte)((bits >> i) & 1);
                ones += values[i];
            }

            network.Run(values);
            Assert.Equal(ones >= 5 ? 1 : 0, values[4]);
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(2, true)]
    [InlineData(3, false)]
    [InlineData(5, true)]
    [InlineData(12, false)]
    public void BytePathsMatchADirectSort(int radius, bool includeAlpha)
    {
        // Noise with few values so windows have ties, and a size that is not a multiple of the vector width or the tile.
        using Raster<Rgba8> source = Noise<Rgba8>(203, 71, 5);
        using Raster<Rgba8> result = source.Derive(o => o.Median(radius, includeAlpha), Ct);
        using Raster<Rgba8> expected = Direct(source, radius, includeAlpha);
        Same(expected, result);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(4)]
    public void GrayAndThreeByteTypesMatchADirectSort(int radius)
    {
        using Raster<Rgba8> noise = Noise<Rgba8>(97, 43, 7);
        using Raster<Gray8> gray = noise.As<Gray8>();
        using Raster<Rgb8> rgb = noise.As<Rgb8>();
        using Raster<Gray8> grayResult = gray.Derive(o => o.Median(radius), Ct);
        using Raster<Rgb8> rgbResult = rgb.Derive(o => o.Median(radius), Ct);
        using Raster<Rgba8> grayExpected = Direct(gray.As<Rgba8>(), radius, false);
        using Raster<Rgba8> rgbExpected = Direct(rgb.As<Rgba8>(), radius, false);
        using Raster<Rgba8> grayActual = grayResult.As<Rgba8>();
        using Raster<Rgba8> rgbActual = rgbResult.As<Rgba8>();
        Same(grayExpected, grayActual);
        Same(rgbExpected, rgbActual);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    public void FloatPathMatchesADirectSort(int radius)
    {
        using Raster<Rgba8> noise = Noise<Rgba8>(41, 29, 256);
        using Raster<Rgba16> wide = noise.As<Rgba16>();
        using Raster<Rgba16> result = wide.Derive(o => o.Median(radius, includeAlpha: true), Ct);
        using Raster<Rgba8> narrow = result.As<Rgba8>();
        using Raster<Rgba8> expected = Direct(noise, radius, true);
        Same(expected, narrow);
    }

    [Fact]
    public void ConstantImageStaysTheSame()
    {
        foreach (int radius in new[] { 1, 2, 3, 9 })
        {
            using Raster<Rgba8> flat = Raster.Create(57, 33, new Rgba8(12, 200, 99, 140));
            using Raster<Rgba8> result = flat.Derive(o => o.Median(radius, includeAlpha: true), Ct);
            Same(flat, result);
        }
    }

    [Fact]
    public void SameOnOneThreadAndInsideAnArea()
    {
        SameOnOneThread("duck", o => o.Median(6));
        SameInsideArea("duck", new Rect(100, 70, 150, 120), o => o.Median(2));
        SameInsideArea("egret", new Rect(30, 300, 200, 100), o => o.Median(5));
    }

    private static Raster<TPixel> Noise<TPixel>(int width, int height, int levels)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Random random = new(width + height);
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)(random.Next(levels) * 255 / Math.Max(1, levels - 1)), (byte)random.Next(256), (byte)(random.Next(levels) * 50), (byte)random.Next(256));
            }
        }

        return raster.As<TPixel>();
    }

    /// <summary>The median by sorting each window, edges clamped.</summary>
    private static Raster<Rgba8> Direct(Raster<Rgba8> source, int r, bool includeAlpha)
    {
        Raster<Rgba8> result = Raster.Create<Rgba8>(source.Width, source.Height);
        List<byte>[] windows = [[], [], [], []];
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                foreach (List<byte> w in windows)
                {
                    w.Clear();
                }

                for (int dy = -r; dy <= r; dy++)
                {
                    for (int dx = -r; dx <= r; dx++)
                    {
                        Rgba8 p = source.Row(Math.Clamp(y + dy, 0, source.Height - 1))[Math.Clamp(x + dx, 0, source.Width - 1)];
                        windows[0].Add(p.R);
                        windows[1].Add(p.G);
                        windows[2].Add(p.B);
                        windows[3].Add(p.A);
                    }
                }

                byte M(int c) => windows[c].Order().ElementAt(windows[c].Count / 2);
                result.Row(y)[x] = new Rgba8(M(0), M(1), M(2), includeAlpha ? M(3) : source.Row(y)[x].A);
            }
        }

        return result;
    }
}
