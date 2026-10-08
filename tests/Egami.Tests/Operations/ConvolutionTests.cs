using System.Buffers;
using System.Numerics;

using Egami.Operations;
using Egami.Operations.Filters;
using Egami.Pixels;

namespace Egami.Tests.Operations;

public class ConvolutionTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<EdgeMode> Edges() => [.. Enum.GetValues<EdgeMode>()];

    [Theory]
    [MemberData(nameof(Edges))]
    public void FullKernelsMatchTheReference(EdgeMode edge)
    {
        ConvolutionKernel kernel = new(5, 3, [0.02f, 0.05f, 0.1f, 0.05f, 0.03f, 0.04f, 0.1f, 0.2f, 0.1f, 0.06f, 0.01f, 0.06f, 0.1f, 0.05f, 0.03f], divisor: 1.0f);
        using Raster<Rgba8> raster = Noise(23, 11);
        using Raster<Rgba8> result = raster.Derive(ops => ops.Convolve(kernel, edge), Ct);
        AssertClose(Reference(raster, kernel, edge, preserveAlpha: false), result, 1);
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void SeparableKernelsMatchTheReference(EdgeMode edge)
    {
        ConvolutionKernel kernel = ConvolutionKernel.Separable([1, 2, 4, 2, 1], [1, 3, 1], divisor: 50, offset: 0.02f);
        using Raster<Rgba8> raster = Noise(17, 13);
        using Raster<Rgba8> result = raster.Derive(ops => ops.Convolve(kernel, edge), Ct);
        AssertClose(Reference(raster, kernel, edge, preserveAlpha: false), result, 1);
    }

    [Theory]
    [MemberData(nameof(Edges))]
    public void PreservedAlphaFiltersOnlyColor(EdgeMode edge)
    {
        // A Laplacian with an offset, as edge-detect filters use: negative weights would wreck premultiplied alpha.
        ConvolutionKernel kernel = new(3, 3, [0, -1, 0, -1, 4, -1, 0, -1, 0], offset: 0.5f);
        using Raster<Rgba8> raster = Noise(19, 9);
        using Raster<Rgba8> result = raster.Derive(ops => ops.Convolve(kernel, edge, preserveAlpha: true), Ct);
        using Raster<Rgba8> expected = Reference(raster, kernel, edge, preserveAlpha: true);
        AssertClose(expected, result, 1);
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                Assert.Equal(raster.GetPixel(x, y).A, result.GetPixel(x, y).A);
            }
        }
    }

    [Fact]
    public void BlurBoxAndSharpenMatchTheirDefinitions()
    {
        using Raster<Rgba8> raster = Noise(41, 37);
        float[] gauss = ConvolutionOperation.GaussianWeights(1.7f);
        Assert.Equal(13, gauss.Length);
        Assert.Equal(1f, gauss.Sum(), 5);

        using Raster<Rgba8> blurred = raster.Derive(ops => ops.Blur(1.7f), Ct);
        AssertClose(Reference(raster, ConvolutionKernel.Separable(gauss, gauss), EdgeMode.Clamp, false), blurred, 1);

        float[] box = [.. Enumerable.Repeat(1f / 7, 7)];
        using Raster<Rgba8> boxed = raster.Derive(ops => ops.BoxBlur(3), Ct);
        AssertClose(Reference(raster, ConvolutionKernel.Separable(box, box), EdgeMode.Clamp, false), boxed, 1);

        // Unsharp mask: (1 + amount) × src − amount × blur, as one kernel.
        float[] g = ConvolutionOperation.GaussianWeights(1);
        float[] mask = new float[g.Length * g.Length];
        for (int y = 0; y < g.Length; y++)
        {
            for (int x = 0; x < g.Length; x++)
            {
                mask[(y * g.Length) + x] = -0.8f * g[x] * g[y];
            }
        }

        mask[mask.Length / 2] += 1.8f;
        using Raster<Rgba8> sharp = raster.Derive(ops => ops.Sharpen(0.8f, 1), Ct);
        AssertClose(Reference(raster, new ConvolutionKernel(g.Length, g.Length, mask), EdgeMode.Clamp, false), sharp, 1);
    }

    [Fact]
    public void FlatImagesStayFlatExceptTransparentEdges()
    {
        Rgba8 color = new(30, 140, 220, 255);
        using Raster<Rgba8> raster = Raster.Create(20, 12, color);
        foreach (EdgeMode edge in new[] { EdgeMode.Clamp, EdgeMode.Mirror, EdgeMode.Wrap })
        {
            using Raster<Rgba8> blurred = raster.Derive(ops => ops.Convolve(ConvolutionKernel.Separable(Ones(7), Ones(5), divisor: 35), edge), Ct);
            Assert.All(Pixels(blurred), p => Assert.Equal(color, p));
        }

        using Raster<Rgba8> faded = raster.Derive(ops => ops.Convolve(ConvolutionKernel.Separable(Ones(3), Ones(3), divisor: 9), EdgeMode.Transparent), Ct);
        Assert.Equal(color, faded.GetPixel(10, 6));
        Rgba8 corner = faded.GetPixel(0, 0);
        Assert.Equal(113, corner.A);
        Assert.Equal((color.R, color.G, color.B), (corner.R, corner.G, corner.B));

        foreach (int radius in new[] { 1, 4, 30 })
        {
            using Raster<Rgba8> box = raster.Derive(ops => ops.BoxBlur(radius), Ct);
            Assert.All(Pixels(box), p => Assert.Equal(color, p));
        }

        using Raster<Rgba8> sharp = raster.Derive(ops => ops.Sharpen(2, 3), Ct);
        Assert.All(Pixels(sharp), p => Assert.Equal(color, p));
    }

    [Fact]
    public void WrapAndMirrorReadTheRightSide()
    {
        // Left column red, the rest blue: a horizontal 3-tap mean shows where the edge taps came from.
        using Raster<Rgb8> raster = Raster.Create(5, 1, new Rgb8(0, 0, 255));
        raster.SetPixel(0, 0, new Rgb8(255, 0, 0));
        ConvolutionKernel mean = new(3, 1, [1, 1, 1], divisor: 3);
        using Raster<Rgb8> wrap = raster.Derive(ops => ops.Convolve(mean, EdgeMode.Wrap), Ct);
        using Raster<Rgb8> mirror = raster.Derive(ops => ops.Convolve(mean, EdgeMode.Mirror), Ct);
        using Raster<Rgb8> clamp = raster.Derive(ops => ops.Convolve(mean, EdgeMode.Clamp), Ct);
        Assert.Equal(new Rgb8(85, 0, 170), wrap.GetPixel(4, 0));
        Assert.Equal(new Rgb8(0, 0, 255), mirror.GetPixel(4, 0));
        Assert.Equal(new Rgb8(170, 0, 85), mirror.GetPixel(0, 0));
        Assert.Equal(new Rgb8(170, 0, 85), clamp.GetPixel(0, 0));
        Assert.Equal(new Rgb8(85, 0, 170), wrap.GetPixel(0, 0));

        Assert.Equal(2, ConvolutionOperation.MapIndex(-3, 5, EdgeMode.Mirror));
        Assert.Equal(4, ConvolutionOperation.MapIndex(5, 5, EdgeMode.Mirror));
        Assert.Equal(0, ConvolutionOperation.MapIndex(9, 5, EdgeMode.Mirror));
        Assert.Equal(1, ConvolutionOperation.MapIndex(-4, 5, EdgeMode.Wrap));
        Assert.Equal(-1, ConvolutionOperation.MapIndex(5, 5, EdgeMode.Transparent));
    }

    [Fact]
    public void LargeBlursApproximateTheGaussian()
    {
        foreach (float sigma in new[] { 20.5f, 25f, 60f })
        {
            int[] radii = RasterOps.BoxRadii(sigma, 3);
            double variance = radii.Sum(r => (((2.0 * r) + 1) * ((2.0 * r) + 1) - 1) / 12);
            Assert.InRange(Math.Sqrt(variance) / sigma, 0.97, 1.03);
        }

        using Raster<Rgba8> raster = Smooth(160, 120);
        using Raster<Rgba8> boxes = raster.Derive(ops => ops.Blur(25), Ct);
        float[] g = ConvolutionOperation.GaussianWeights(25);
        using Raster<Rgba8> exact = raster.Derive(ops => ops.Convolve(ConvolutionKernel.Separable(g, g)), Ct);
        Assert.True(Psnr(exact, boxes) > 40, $"PSNR {Psnr(exact, boxes):F1}");
    }

    [Fact]
    public void AccumulateMatchesScalar()
    {
        Random random = new(4);
        foreach (int length in new[] { 1, 7, 8, 9, 64, 1003 })
        {
            float[] source = [.. Enumerable.Range(0, length).Select(_ => random.NextSingle())];
            float[] fast = [.. Enumerable.Range(0, length).Select(_ => random.NextSingle())];
            float[] scalar = [.. fast];
            ConvolutionKernels.Accumulate(source, 0.37f, fast, first: false);
            ConvolutionKernels.AccumulateScalar(source, 0.37f, scalar, first: false);
            Assert.Equal(scalar, fast);
            ConvolutionKernels.Accumulate(source, -1.5f, fast, first: true);
            ConvolutionKernels.AccumulateScalar(source, -1.5f, scalar, first: true);
            Assert.Equal(scalar, fast);
        }
    }

    [Fact]
    public void PlaneShapesThreadsAndColorSpacesAgree()
    {
        EgamiContext serial = EgamiContext.Default with { MaxParallelism = 1, Memory = new SmallBlocks(700) };
        foreach ((int w, int h) in new[] { (1, 1), (1, 50), (50, 1), (300, 700) })
        {
            using Raster<Rgba8> a = Noise(w, h);
            using Raster<Rgba8> b = Raster.Create<Rgba8>(w, h, serial);
            for (int y = 0; y < h; y++)
            {
                a.Row(y).CopyTo(b.Row(y));
            }

            static void Chain(RasterOps ops) => ops.Blur(2.5f, ColorSpace.Linear).Sharpen(0.5f).BoxBlur(2).Convolve(new ConvolutionKernel(3, 3, [0, 1, 0, 1, 2, 1, 0, 1, 0], 6), EdgeMode.Mirror);
            a.Apply(Chain, Ct);
            b.Apply(Chain, Ct);
            AssertClose(a, b, 0);
        }
    }

    [Fact]
    public void ArgumentsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => new ConvolutionKernel(2, 3, new float[6]));
        Assert.Throws<ArgumentException>(() => new ConvolutionKernel(3, 3, new float[8]));
        Assert.Throws<ArgumentException>(() => new ConvolutionKernel(1, 1, [1], divisor: 0));
        Assert.Throws<ArgumentException>(() => new ConvolutionKernel(1, 1, [float.NaN]));
        Assert.Throws<ArgumentException>(() => ConvolutionKernel.Separable([1, 1], [1]));
        Assert.True(ConvolutionKernel.Separable([1, 2, 1], [1]).IsSeparable);
        Assert.Equal(2f, ConvolutionKernel.Separable([1, 2, 1], [1])[1, 0]);

        using Raster<Rgba8> raster = Raster.Create<Rgba8>(4, 4);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Blur(-1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.BoxBlur(-1), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Sharpen(1, 0), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Sharpen(float.NaN), Ct));
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.Convolve(new ConvolutionKernel(1, 1, [1]), (EdgeMode)9), Ct));
    }

    /// <summary>Direct 2D correlation in double precision, premultiplied (or straight when keeping alpha), with edge mapping.</summary>
    private static Raster<Rgba8> Reference(Raster<Rgba8> source, ConvolutionKernel kernel, EdgeMode edge, bool preserveAlpha)
    {
        int w = source.Width, h = source.Height, rx = kernel.Width / 2, ry = kernel.Height / 2;
        Raster<Rgba8> result = Raster.Create<Rgba8>(w, h);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double r = 0, g = 0, b = 0, a = 0;
                for (int ky = 0; ky < kernel.Height; ky++)
                {
                    for (int kx = 0; kx < kernel.Width; kx++)
                    {
                        int sx = ConvolutionOperation.MapIndex(x + kx - rx, w, edge);
                        int sy = ConvolutionOperation.MapIndex(y + ky - ry, h, edge);
                        if (sx < 0 || sy < 0)
                        {
                            continue;
                        }

                        Rgba8 p = source.GetPixel(sx, sy);
                        double pa = p.A / 255.0;
                        double m = preserveAlpha ? 1 : pa;
                        double k = kernel[kx, ky] / (double)kernel.Divisor;
                        r += k * p.R / 255.0 * m;
                        g += k * p.G / 255.0 * m;
                        b += k * p.B / 255.0 * m;
                        a += k * pa;
                    }
                }

                double outA = preserveAlpha ? source.GetPixel(x, y).A / 255.0 : Math.Clamp(a, 0, 1);
                double div = preserveAlpha ? 1 : a;
                result.SetPixel(x, y, new Rgba8(
                    Channel(r, div, outA, kernel.Offset), Channel(g, div, outA, kernel.Offset), Channel(b, div, outA, kernel.Offset), (byte)Math.Round(outA * 255)));
            }
        }

        return result;

        static byte Channel(double v, double alpha, double outA, float offset)
        {
            if (outA <= 0 || alpha <= 0)
            {
                return 0;
            }

            return (byte)Math.Round(Math.Clamp((v / alpha) + offset, 0, 1) * 255);
        }
    }

    private static float[] Ones(int n) => [.. Enumerable.Repeat(1f, n)];

    private static IEnumerable<T> Pixels<T>(Raster<T> raster)
        where T : unmanaged, IPixelType<T>
    {
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (T p in raster.Row(y).ToArray())
            {
                yield return p;
            }
        }
    }

    private static Raster<Rgba8> Noise(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new((width * 13) + height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(40, 256));
            }
        }

        return raster;
    }

    private static Raster<Rgba8> Smooth(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new(3);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)((x * 255) / width), (byte)((y * 255) / height), (byte)random.Next(256), 255);
            }
        }

        return raster;
    }

    private static double Psnr(Raster<Rgba8> a, Raster<Rgba8> b)
    {
        double sum = 0;
        int count = 0;
        for (int y = 0; y < a.Height; y++)
        {
            for (int x = 0; x < a.Width; x++)
            {
                Vector4 d = (a.GetPixel(x, y).ToScaled() - b.GetPixel(x, y).ToScaled()) * 255;
                sum += (d.X * d.X) + (d.Y * d.Y) + (d.Z * d.Z);
                count += 3;
            }
        }

        return sum == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255.0 / (sum / count));
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

    private sealed class SmallBlocks(int maxBlockBytes) : IPixelMemorySource
    {
        public int MaxBlockBytes => maxBlockBytes;

        public IMemoryOwner<byte> Allocate(int byteLength, ClearMode clear)
        {
            return PooledPixelMemorySource.Shared.Allocate(byteLength, clear);
        }
    }
}
