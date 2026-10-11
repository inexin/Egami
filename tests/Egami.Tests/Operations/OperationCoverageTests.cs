using System.Buffers;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// Every operation, on every plane shape and with an allocation budget. Exact results are tested elsewhere; here the same chain
/// must give the same pixels on contiguous, chunked and caller-owned memory and at sizes down to one pixel, and its managed
/// allocations must not grow with the image.
/// </summary>
public sealed class OperationCoverageTests : IDisposable
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Raster<Rgba8> _logo = Logo();

    public static TheoryData<string> Operations() => [.. Chains.Keys];

    /// <summary>One chain per operation and mode, each sized relative to its input so it runs on any shape.</summary>
    internal static readonly Dictionary<string, Func<Size, Raster<Rgba8>, Action<RasterOps>>> Chains = new()
    {
        ["Resize-Nearest"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height), ResampleFilter.Nearest),
        ["Resize-Box"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height), ResampleFilter.Box),
        ["Resize-Bilinear"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height), ResampleFilter.Bilinear),
        ["Resize-Bicubic"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height)),
        ["Resize-Mitchell"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height), ResampleFilter.Mitchell),
        ["Resize-Lanczos3"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height), ResampleFilter.Lanczos3),
        ["Resize-Linear"] = (s, _) => ops => ops.Resize(Half(s.Width), Half(s.Height), ResampleFilter.Bicubic, ColorSpace.Linear),
        ["Upscale"] = (s, _) => ops => ops.Resize(s.Width * 3, s.Height * 2),
        ["Fit"] = (s, _) => ops => ops.Fit(Half(s.Width), s.Height),
        ["Fill"] = (s, _) => ops => ops.Cover(Half(s.Width), Math.Max(1, s.Height / 3), Anchor.BottomRight),
        ["Crop-Resize"] = (s, _) => ops => ops.Crop(0, 0, Half(s.Width), s.Height).Resize(s.Width, Half(s.Height)),
        ["Crop"] = (s, _) => ops => ops.Crop(s.Width / 3, s.Height / 4, Half(s.Width), Half(s.Height)),
        ["Pad"] = (s, _) => ops => ops.Pad(s.Width + 3, s.Height + 2, Rgba8.Blue, Anchor.Left),
        ["Flip-Horizontal"] = (_, _) => ops => ops.FlipHorizontal(),
        ["Flip-Vertical"] = (_, _) => ops => ops.FlipVertical(),
        ["Flip-Both"] = (_, _) => ops => ops.FlipHorizontal().FlipVertical(),
        ["Rotate90"] = (_, _) => ops => ops.Rotate(90),
        ["Rotate270"] = (_, _) => ops => ops.Rotate(270),
        ["Rotate15"] = (_, _) => ops => ops.Rotate(15),
        ["Rotate-Nearest"] = (_, _) => ops => ops.Rotate(-40, ResampleFilter.Nearest, Rgba8.Red),
        ["Transform-Shrink"] = (_, _) => ops => ops.Transform(System.Numerics.Matrix3x2.CreateRotation(0.3f) * System.Numerics.Matrix3x2.CreateScale(0.4f)),
        ["Transform-Enlarge"] = (_, _) => ops => ops.Transform(System.Numerics.Matrix3x2.CreateSkew(0.2f, 0.1f) * System.Numerics.Matrix3x2.CreateScale(1.7f, 1.2f)),
        ["Transform-Wrap"] = (s, _) => ops => ops.Transform(System.Numerics.Matrix3x2.CreateRotation(0.5f), new TransformOptions { Viewport = new Rect(0, 0, s.Width, s.Height), Edges = EdgeMode.Wrap }),
        ["Transform-Linear"] = (_, _) => ops => ops.Transform(System.Numerics.Matrix3x2.CreateScale(0.6f, -0.7f), new TransformOptions { ColorSpace = ColorSpace.Linear, Edges = EdgeMode.Mirror }),
        ["QuadDistort"] = (s, _) => ops => ops.QuadDistort(new(s.Width * 0.2f, 0), new(s.Width * 0.8f, s.Height * 0.1f), new(s.Width, s.Height), new(0, s.Height * 0.9f), new TransformOptions { Background = Rgba8.White }),
        ["Skew"] = (_, _) => ops => ops.Skew(20, -5, new TransformOptions { Filter = ResampleFilter.Lanczos3 }),
        ["FillColor"] = (s, _) => ops => ops.FillColor(Rgba8.Lime, new Rect(0, 0, Half(s.Width), Half(s.Height))),
        ["Color-Matrix"] = (_, _) => ops => ops.Brightness(1.1f).Contrast(1.2f).Saturation(1.3f).HueRotate(20),
        ["Color-Tables"] = (_, _) => ops => ops.Brightness(1.1f).Invert(0.2f).Opacity(0.8f),
        ["Grayscale-Linear"] = (_, _) => ops => ops.Grayscale(LumaWeights.Rec601, 0.7f, ColorSpace.Linear),
        ["Sepia"] = (_, _) => ops => ops.Sepia(),
        ["Blur"] = (_, _) => ops => ops.Blur(2),
        ["Blur-Linear"] = (_, _) => ops => ops.Blur(1.5f, ColorSpace.Linear),
        ["Blur-Boxes"] = (_, _) => ops => ops.Blur(120),
        ["BoxBlur"] = (_, _) => ops => ops.BoxBlur(3),
        ["Sharpen"] = (_, _) => ops => ops.Sharpen(0.8f, 1.2f),
        ["Convolve-Full"] = (_, _) => ops => ops.Convolve(new ConvolutionKernel(3, 3, [0, -1, 0, -1, 5, -1, 0, -1, 0]), EdgeMode.Mirror),
        ["Convolve-Separable"] = (_, _) => ops => ops.Convolve(ConvolutionKernel.Separable([1, 2, 1], [1, 2, 1], divisor: 16), EdgeMode.Wrap),
        ["Convolve-KeepAlpha"] = (_, _) => ops => ops.Convolve(new ConvolutionKernel(3, 3, [0, -1, 0, -1, 4, -1, 0, -1, 0], offset: 0.5f), EdgeMode.Transparent, preserveAlpha: true),
        ["DrawImage"] = (s, logo) => ops => ops.DrawImage(logo, new Point(s.Width / 3, -2), 0.7f),
        ["DrawImage-Multiply"] = (s, logo) => ops => ops.DrawImage(logo, new Point(s.Width / 4, 1), BlendMode.Multiply, 0.8f),
        ["DrawImage-DestinationIn"] = (s, logo) => ops => ops.DrawImage(logo, new Point(-1, s.Height / 3), BlendMode.DestinationIn),
        ["DrawImage-Hue"] = (s, logo) => ops => ops.DrawImage(logo, new Point(s.Width / 3, 0), BlendMode.Hue),
        ["DrawImage-SoftLight-Linear"] = (s, logo) => ops => ops.DrawImage(logo, new Point(0, 0), BlendMode.SoftLight, 1, ColorSpace.Linear),
        ["BlendColor"] = (_, _) => ops => ops.BlendColor(new Rgba8(200, 120, 40, 180), BlendMode.Screen),
        ["DrawArithmetic"] = (s, logo) => ops => ops.DrawArithmetic(logo, new Point(-3, s.Height / 3), 0.2f, 0.5f, 0.6f, 0.01f),
    };

    [Theory]
    [MemberData(nameof(Operations))]
    public void EveryPlaneShapeGivesTheSamePixels(string operation)
    {
        foreach ((int width, int height) in new[] { (1, 1), (1, 17), (17, 1), (13, 7), (130, 70) })
        {
            using Raster<Rgba8> contiguous = Noise(width, height, EgamiContext.Default);
            Action<RasterOps> chain = Chains[operation](new Size(width, height), _logo);
            using Raster<Rgba8> expected = contiguous.Derive(chain, Ct);

            // Chunked: rows spread over many small blocks, one thread.
            EgamiContext chunked = EgamiContext.Default with { Memory = new SmallBlocks(96), MaxParallelism = 1 };
            using Raster<Rgba8> blocks = Noise(width, height, chunked);
            blocks.Apply(chain, Ct);
            AssertSame(expected, blocks, $"{operation} {width}×{height} chunked");

            // Caller-owned memory with a stride wider than the image, changed in place.
            Rgba8[] memory = new Rgba8[(width + 5) * height];
            using Raster<Rgba8> wrapped = Raster.Wrap<Rgba8>(memory, width, height, width + 5);
            for (int y = 0; y < height; y++)
            {
                contiguous.Row(y).CopyTo(wrapped.Row(y));
            }

            wrapped.Apply(chain, Ct);
            AssertSame(expected, wrapped, $"{operation} {width}×{height} wrapped");

            // Other pixel types run the same chain without failing and keep its size.
            using Raster<Rgb8> rgb = contiguous.As<Rgb8>();
            using Raster<Rgb8> rgbResult = rgb.Derive(chain, Ct);
            using Raster<Gray16> gray = contiguous.As<Gray16>();
            using Raster<Gray16> grayResult = gray.Derive(chain, Ct);
            using Raster<RgbaF32> floats = contiguous.As<RgbaF32>();
            using Raster<RgbaF32> floatResult = floats.Derive(chain, Ct);
            Assert.Equal((expected.Width, expected.Height), (rgbResult.Width, rgbResult.Height));
            Assert.Equal((expected.Width, expected.Height), (grayResult.Width, grayResult.Height));
            Assert.Equal((expected.Width, expected.Height), (floatResult.Width, floatResult.Height));
        }
    }

    [Theory]
    [MemberData(nameof(Operations))]
    public void AllocationsDoNotGrowWithTheImage(string operation)
    {
        // One thread, so every allocation is on this one; pixel memory comes from the memory source, not the managed heap.
        EgamiContext context = EgamiContext.Default with { MaxParallelism = 1 };
        using Raster<Rgba8> small = Noise(120, 90, context);
        using Raster<Rgba8> large = Noise(1200, 900, context);
        long smallBytes = Allocated(small, Chains[operation](new Size(120, 90), _logo));
        long largeBytes = Allocated(large, Chains[operation](new Size(1200, 900), _logo));
        Assert.True(largeBytes - smallBytes < 1024, $"{operation}: {largeBytes:N0} bytes for 1200×900 against {smallBytes:N0} for 120×90.");
        Assert.True(largeBytes < 8 * 1024, $"{operation}: {largeBytes:N0} bytes per call.");
    }

    public void Dispose()
    {
        _logo.Dispose();
    }

    /// <summary>The fewest bytes one Derive allocated over several tries; see AllocationTests for why the minimum.</summary>
    private static long Allocated(Raster<Rgba8> raster, Action<RasterOps> chain)
    {
        for (int i = 0; i < 3; i++)
        {
            raster.Derive(chain, Ct).Dispose();
        }

        long fewest = long.MaxValue;
        for (int i = 0; i < 6; i++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            raster.Derive(chain, Ct).Dispose();
            fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
        }

        return fewest;
    }

    private static int Half(int size) => Math.Max(1, size / 2);

    private static void AssertSame(Raster<Rgba8> expected, Raster<Rgba8> actual, string label)
    {
        Assert.True(expected.Width == actual.Width && expected.Height == actual.Height, $"{label}: {actual.Width}×{actual.Height}");
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"{label}: row {y} differs");
        }
    }

    internal static Raster<Rgba8> Noise(int width, int height, EgamiContext context)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height, context);
        Random random = new((width * 7) + height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        return raster;
    }

    internal static Raster<Rgba8> Logo()
    {
        Raster<Rgba8> logo = Raster.Create<Rgba8>(40, 30);
        for (int y = 0; y < 30; y++)
        {
            Span<Rgba8> row = logo.Row(y);
            for (int x = 0; x < 40; x++)
            {
                row[x] = new Rgba8((byte)(x * 6), (byte)(y * 8), 90, (byte)((x + y) * 4));
            }
        }

        return logo;
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
