using System.Buffers;

using Egami.Operations;
using Egami.Operations.Resampling;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class ResizeTests
{
    private static readonly ResampleFilter[] Filters = Enum.GetValues<ResampleFilter>();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<ResampleFilter> AllFilters()
    {
        return [.. Filters];
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void WeightRowsSumToOneAndNeverStepBack(ResampleFilter filter)
    {
        foreach (int source in new[] { 1, 2, 3, 7, 64, 101, 1000 })
        {
            foreach (int size in new[] { 1, 2, 5, 33, 64, 250, 2000 })
            {
                ResizeWeights weights = ResizeWeights.Build(filter, size, 0, source, 0, source - 1);
                int lastFirst = 0;
                int lastEnd = 0;
                for (int i = 0; i < size; i++)
                {
                    float sum = 0;
                    foreach (float w in weights.Row(i))
                    {
                        sum += w;
                    }

                    Assert.True(Math.Abs(sum - 1f) < 1e-6f, $"{filter} {source}→{size} row {i} sums to {sum}");
                    Assert.InRange(weights.First[i], lastFirst, source - 1);
                    int end = weights.First[i] + weights.Count[i];
                    Assert.InRange(end, lastEnd, source);
                    Assert.InRange(weights.Count[i], 1, weights.Stride);
                    lastFirst = weights.First[i];
                    lastEnd = end;
                }
            }
        }
    }

    [Fact]
    public void KernelsMatchTheirDefinitions()
    {
        // Interpolating kernels are 1 at 0 and 0 at every other integer.
        foreach (ResampleFilter f in new[] { ResampleFilter.Bilinear, ResampleFilter.Bicubic, ResampleFilter.Lanczos3 })
        {
            Assert.Equal(1, ResampleKernels.Evaluate(f, 0), 12);
            for (int x = 1; x <= 3; x++)
            {
                Assert.Equal(0, ResampleKernels.Evaluate(f, x), 12);
            }
        }

        // Catmull-Rom at 0.5 is 0.5625 and at 1.5 is −0.0625; Mitchell at 0 is 8/9 and at 1 is 1/18.
        Assert.Equal(0.5625, ResampleKernels.Evaluate(ResampleFilter.Bicubic, 0.5), 12);
        Assert.Equal(-0.0625, ResampleKernels.Evaluate(ResampleFilter.Bicubic, -1.5), 12);
        Assert.Equal(8 / 9.0, ResampleKernels.Evaluate(ResampleFilter.Mitchell, 0), 12);
        Assert.Equal(1 / 18.0, ResampleKernels.Evaluate(ResampleFilter.Mitchell, 1), 12);
        Assert.Equal(0.5, ResampleKernels.Evaluate(ResampleFilter.Bilinear, 0.5), 12);
        Assert.Equal(1, ResampleKernels.Evaluate(ResampleFilter.Box, 0.49), 12);
        Assert.Equal(0, ResampleKernels.Evaluate(ResampleFilter.Box, 0.51), 12);
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void FlatImagesStayFlat(ResampleFilter filter)
    {
        foreach (ColorSpace space in Enum.GetValues<ColorSpace>())
        {
            CheckFlat(new Rgba8(200, 17, 90, 255), filter, space);
            CheckFlat(new Rgba8(3, 250, 128, 77), filter, space);
            CheckFlat(new Bgra8(10, 20, 30, 40), filter, space);
            CheckFlat(new Rgb8(1, 128, 254), filter, space);
            CheckFlat(new Gray8(173), filter, space);
            CheckFlat(new GrayAlpha8(9, 200), filter, space);
            CheckFlat(new Gray16(40000), filter, space);
            CheckFlat(new Rgba16(1000, 30000, 65535, 50000), filter, space);
            CheckFlat(new RgbaF32(0.25f, 0.5f, 0.75f, 1f), filter, space);
        }
    }

    [Theory]
    [MemberData(nameof(AllFilters))]
    public void TransparentEdgesGetNoFringe(ResampleFilter filter)
    {
        // A red square on fully transparent black: any blending of straight alpha would darken the edge.
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(40, 40);
        raster.Apply(ops => ops.FillColor(Rgba8.Red, new Rect(10, 10, 20, 20)), Ct);
        foreach ((int w, int h) in new[] { (17, 17), (13, 29), (97, 97) })
        {
            foreach (ColorSpace space in Enum.GetValues<ColorSpace>())
            {
                using Raster<Rgba8> resized = raster.Derive(ops => ops.Resize(w, h, filter, space), Ct);
                int partial = 0;
                for (int y = 0; y < h; y++)
                {
                    foreach (Rgba8 p in resized.Row(y))
                    {
                        if (p.A > 0)
                        {
                            Assert.True(p.R >= 254 && p.G == 0 && p.B == 0, $"{filter} {space} {w}×{h}: {p}");
                            partial += p.A < 255 ? 1 : 0;
                        }
                    }
                }

                // Box enlarges like Nearest, so only reductions are sure to blend.
                Assert.True(filter == ResampleFilter.Nearest || w > 40 || partial > 0, "The edge should be blended.");
            }
        }
    }

    [Fact]
    public void LinearLightKeepsTheBrightnessOfFineDetail()
    {
        // One-pixel black and white columns, halved with Box: encoded averages the stored values, linear averages the light.
        using Raster<Gray8> stripes = Raster.Create<Gray8>(64, 4);
        for (int y = 0; y < 4; y++)
        {
            Span<Gray8> row = stripes.Row(y);
            for (int x = 0; x < row.Length; x++)
            {
                row[x] = new Gray8((byte)(x % 2 == 0 ? 0 : 255));
            }
        }

        using Raster<Gray8> encoded = stripes.Derive(ops => ops.Resize(32, 2, ResampleFilter.Box), Ct);
        using Raster<Gray8> linear = stripes.Derive(ops => ops.Resize(32, 2, ResampleFilter.Box, ColorSpace.Linear), Ct);
        Assert.All(encoded.Row(1).ToArray(), p => Assert.Equal(128, p.L));
        Assert.All(linear.Row(1).ToArray(), p => Assert.Equal(188, p.L));
    }

    [Fact]
    public void OutputSizes()
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(400, 300);
        Assert.Equal(new Size(800, 600), SizeOf(raster, ops => ops.Resize(800, 0)));
        Assert.Equal(new Size(133, 100), SizeOf(raster, ops => ops.Resize(0, 100)));
        Assert.Equal(new Size(10, 20), SizeOf(raster, ops => ops.Resize(10, 20)));
        Assert.Equal(new Size(200, 150), SizeOf(raster, ops => ops.Fit(200, 200)));
        Assert.Equal(new Size(400, 300), SizeOf(raster, ops => ops.Fit(1000, 1000, enlarge: false)));
        Assert.Equal(new Size(1000, 750), SizeOf(raster, ops => ops.Fit(1000, 1000)));
        Assert.Equal(new Size(100, 75), SizeOf(raster, ops => ops.Fit(100, 1000, enlarge: false)));
        Assert.Equal(new Size(256, 256), SizeOf(raster, ops => ops.Fill(256, 256)));
        Assert.Equal(new Size(1, 1), SizeOf(raster, ops => ops.Resize(1, 0).Resize(0, 1)));
        Assert.Equal(new Size(50, 1), SizeOf(raster, ops => ops.Crop(0, 0, 400, 1).Resize(50, 0)));

        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Resize(0, 0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Resize(-1, 5), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Fit(0, 5), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Fill(5, 0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Resize(5, 5, (ResampleFilter)99), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Derive(ops => ops.Crop(390, 0, 20, 20).Resize(5, 5), Ct));
    }

    [Fact]
    public void ResizeToTheSameSizeChangesNothing()
    {
        using Raster<Rgba8> raster = Noise<Rgba8>(31, 17, EgamiContext.Default);
        using Raster<Rgba8> before = raster.Copy();
        foreach (ResampleFilter filter in Filters)
        {
            raster.Apply(ops => ops.Resize(31, 17, filter).Fit(31, 17, filter).Fill(31, 17, Anchor.Left, filter), Ct);
            AssertEqual(before, raster, 0);
        }
    }

    [Fact]
    public void CropThenResizeMatchesTheTwoSteps()
    {
        using Raster<Rgba8> raster = Noise<Rgba8>(120, 90, EgamiContext.Default);
        foreach (ResampleFilter filter in Filters)
        {
            using Raster<Rgba8> merged = raster.Derive(ops => ops.Crop(13, 7, 70, 50).Resize(33, 77, filter), Ct);
            using Raster<Rgba8> cropped = raster.Derive(ops => ops.Crop(13, 7, 70, 50), Ct);
            using Raster<Rgba8> separate = cropped.Derive(ops => ops.Resize(33, 77, filter), Ct);
            AssertEqual(separate, merged, 1);
        }
    }

    [Fact]
    public void FillKeepsThePartAtTheAnchor()
    {
        using Raster<Rgba8> raster = Noise<Rgba8>(200, 100, EgamiContext.Default);
        foreach ((Anchor anchor, int x) in new[] { (Anchor.Center, 50), (Anchor.Left, 0), (Anchor.BottomRight, 100) })
        {
            using Raster<Rgba8> filled = raster.Derive(ops => ops.Fill(100, 100, anchor), Ct);
            using Raster<Rgba8> cropped = raster.Derive(ops => ops.Crop(x, 0, 100, 100), Ct);
            AssertEqual(cropped, filled, 0);
        }

        using Raster<Rgba8> small = raster.Derive(ops => ops.Fill(50, 50, Anchor.Right), Ct);
        // Fill reads past the kept area's edge where the image continues, like resizing everything and then cropping.
        using Raster<Rgba8> expected = raster.Derive(ops => ops.Resize(100, 50).Crop(50, 0, 50, 50), Ct);
        AssertEqual(expected, small, 1);
    }

    [Fact]
    public void NearestPicksExactPixels()
    {
        using Raster<Rgba16> raster = Noise<Rgba16>(10, 6, EgamiContext.Default);
        using Raster<Rgba16> half = raster.Derive(ops => ops.Resize(5, 3, ResampleFilter.Nearest), Ct);
        using Raster<Rgba16> twice = raster.Derive(ops => ops.Resize(20, 12, ResampleFilter.Nearest), Ct);
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal(raster.GetPixel((2 * x) + 1, (2 * y) + 1), half.GetPixel(x, y));
            }
        }

        for (int y = 0; y < 12; y++)
        {
            for (int x = 0; x < 20; x++)
            {
                Assert.Equal(raster.GetPixel(x / 2, y / 2), twice.GetPixel(x, y));
            }
        }
    }

    [Fact]
    public void BilinearInterpolatesBetweenTwoPixels()
    {
        using Raster<Gray16> raster = Raster.Create<Gray16>(2, 1);
        raster.SetPixel(0, 0, new Gray16(0));
        raster.SetPixel(1, 0, new Gray16(40000));
        using Raster<Gray16> wide = raster.Derive(ops => ops.Resize(4, 1, ResampleFilter.Bilinear), Ct);

        // Output centers sit at source positions −0.25, 0.25, 0.75 and 1.25; the outer two clamp to the edges.
        Assert.Equal([0, 10000, 30000, 40000], wide.Row(0).ToArray().Select(p => (int)p.L));
    }

    [Fact]
    public void PlaneShapesAndThreadsGiveTheSameResult()
    {
        EgamiContext chunked = EgamiContext.Default with { Memory = new SmallBlocks(512), MaxParallelism = 1 };
        foreach ((int w, int h, int ow, int oh) in new[] { (1, 1, 5, 3), (1, 40, 3, 7), (37, 1, 8, 2), (300, 900, 257, 1201), (513, 77, 100, 300) })
        {
            using Raster<Rgba8> a = Noise<Rgba8>(w, h, EgamiContext.Default);
            using Raster<Rgba8> b = Noise<Rgba8>(w, h, chunked);
            using Raster<Rgba8> ra = a.Derive(ops => ops.Resize(ow, oh, ResampleFilter.Lanczos3), Ct);
            using Raster<Rgba8> rb = b.Derive(ops => ops.Resize(ow, oh, ResampleFilter.Lanczos3), Ct);
            AssertEqual(ra, rb, 0);
            a.Apply(ops => ops.Fill(ow, oh, Anchor.Bottom, ResampleFilter.Mitchell, ColorSpace.Linear), Ct);
            b.Apply(ops => ops.Fill(ow, oh, Anchor.Bottom, ResampleFilter.Mitchell, ColorSpace.Linear), Ct);
            AssertEqual(a, b, 0);
        }
    }

    private static void CheckFlat<TPixel>(TPixel color, ResampleFilter filter, ColorSpace space)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using Raster<TPixel> raster = Raster.Create(23, 11, color);
        foreach ((int w, int h) in new[] { (5, 3), (1, 1), (64, 37), (23, 40), (7, 11) })
        {
            using Raster<TPixel> resized = raster.Derive(ops => ops.Resize(w, h, filter, space), Ct);
            for (int y = 0; y < h; y++)
            {
                foreach (TPixel p in resized.Row(y))
                {
                    Assert.True(Close(color, p), $"{typeof(TPixel).Name} {filter} {space} {w}×{h}: {p} != {color}");
                }
            }
        }
    }

    private static bool Close<TPixel>(TPixel expected, TPixel actual)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        // Exact for integer types; float types may differ in the last bits of the weight sums.
        return TPixel.Layout.ChannelType == PixelChannelType.Float32
            ? Vector4Close(expected.ToScaled(), actual.ToScaled())
            : expected.Equals(actual);
    }

    private static bool Vector4Close(System.Numerics.Vector4 a, System.Numerics.Vector4 b)
    {
        return System.Numerics.Vector4.Abs(a - b) is var d && d.X < 1e-5f && d.Y < 1e-5f && d.Z < 1e-5f && d.W < 1e-5f;
    }

    private static Size SizeOf(Raster<Rgba8> raster, Action<RasterOps> build)
    {
        using Raster<Rgba8> result = raster.Derive(build, Ct);
        return new Size(result.Width, result.Height);
    }

    private static Raster<TPixel> Noise<TPixel>(int width, int height, EgamiContext context)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Raster<TPixel> raster = Raster.Create<TPixel>(width, height, context);
        Random random = new(width * 31 + height);
        for (int y = 0; y < height; y++)
        {
            Span<TPixel> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = TPixel.FromRgba8(new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        return raster;
    }

    private static void AssertEqual<TPixel>(Raster<TPixel> expected, Raster<TPixel> actual, int tolerance)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        Assert.Equal(expected.Width, actual.Width);
        Assert.Equal(expected.Height, actual.Height);
        for (int y = 0; y < expected.Height; y++)
        {
            Span<TPixel> e = expected.Row(y);
            Span<TPixel> a = actual.Row(y);
            for (int x = 0; x < e.Length; x++)
            {
                Rgba8 pe = e[x].ToRgba8();
                Rgba8 pa = a[x].ToRgba8();
                if (pe.A == 0 && pa.A == 0)
                {
                    // Premultiplied resampling leaves fully transparent pixels black; their color was never visible.
                    continue;
                }

                int diff = Math.Max(Math.Max(Math.Abs(pe.R - pa.R), Math.Abs(pe.G - pa.G)), Math.Max(Math.Abs(pe.B - pa.B), Math.Abs(pe.A - pa.A)));
                Assert.True(diff <= tolerance, $"({x}, {y}): {pe} vs {pa}");
            }
        }
    }

    /// <summary>Pooled memory in small blocks, so planes span many blocks.</summary>
    private sealed class SmallBlocks(int maxBlockBytes) : IPixelMemorySource
    {
        public int MaxBlockBytes => maxBlockBytes;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            return PooledPixelMemorySource.Shared.Allocate(byteLength, clear);
        }
    }
}
