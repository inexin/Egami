using System.Buffers;

using Egami.Operations;
using Egami.Pixels;
using Egami.Tests.Memory;

using static Egami.Tests.Operations.OperationCoverageTests;

namespace Egami.Tests.Operations;

/// <summary>
/// Every operation, on every plane shape and with an allocation budget. Exact results are tested elsewhere; here the same chain
/// must give the same pixels on contiguous, chunked and caller-owned memory and at sizes down to one pixel, and its managed
/// allocations must not grow with the image.
/// </summary>
public abstract class OperationCoverageTests<TPart> : IDisposable
    where TPart : IPart
{
    private const int PartCount = 8;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private readonly Raster<Rgba8> _logo = Logo();

    public static TheoryData<string> Operations() => Parts.Take(Chains.Keys, TPart.Index, PartCount);

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

        // ZoomBlur's work grows with its blur length, which grows with the image; at a quarter of the area any growth still shows.
        Size big = operation == "ZoomBlur" ? new Size(600, 450) : new Size(1200, 900);
        using Raster<Rgba8> large = Noise(big.Width, big.Height, context);

        // Tests running alongside can empty the shared array pool for a while, so an over-budget measurement is taken again.
        long smallBytes = 0;
        long largeBytes = 0;
        for (int attempt = 0; attempt < 5; attempt++)
        {
            smallBytes = Allocated(small, Chains[operation](new Size(120, 90), _logo));
            largeBytes = Allocated(large, Chains[operation](big, _logo));
            if (largeBytes - smallBytes < 1024 && largeBytes < 8 * 1024)
            {
                break;
            }
        }

        Assert.True(largeBytes - smallBytes < 1024, $"{operation}: {largeBytes:N0} bytes for {big.Width}×{big.Height} against {smallBytes:N0} for 120×90.");
        Assert.True(largeBytes < 8 * 1024, $"{operation}: {largeBytes:N0} bytes per call.");
    }

    public void Dispose()
    {
        _logo.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The fewest bytes one Derive allocated over several tries; see <see cref="AllocationMeter"/>.</summary>
    private static long Allocated(Raster<Rgba8> raster, Action<RasterOps> chain) =>
        AllocationMeter.Fewest(() => raster.Derive(chain, Ct).Dispose(), warmUps: 3, tries: 6);

    private static void AssertSame(Raster<Rgba8> expected, Raster<Rgba8> actual, string label)
    {
        Assert.True(expected.Width == actual.Width && expected.Height == actual.Height, $"{label}: {actual.Width}×{actual.Height}");
        for (int y = 0; y < expected.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(actual.Row(y)), $"{label}: row {y} differs");
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

/// <summary>The operation chains and test images the coverage tests share with the other operation tests.</summary>
internal static class OperationCoverageTests
{
    private static readonly Lut3D Grade = Lut3D.FromFunction(c => new System.Numerics.Vector3(c.Y, MathF.Sqrt(c.X), c.Z * c.Z), 9);

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
        ["Warp-Swirl"] = (_, _) => ops => ops.Warp(Warps.Swirl(100)),
        ["Warp-Barrel-Linear"] = (_, _) => ops => ops.Warp(Warps.Barrel(0.02, -0.1, 0), new SamplingOptions { ColorSpace = ColorSpace.Linear, Edges = EdgeMode.Mirror }),
        ["Warp-Polar"] = (_, _) => ops => ops.Warp(Warps.Polar(-1)),
        ["Warp-Wave"] = (_, _) => ops => ops.Warp(Warps.Wave(3, 9), new SamplingOptions { Background = Rgba8.White }),
        ["Displace"] = (s, _) => ops => ops.Displace(MapFor(s), 8, -6, ColorChannel.Blue, ColorChannel.Luminance),
        ["Offset"] = (s, _) => ops => ops.Offset((s.Width / 3) + 1, -2),
        ["Tile"] = (s, _) => ops => ops.Tile((s.Width * 2) + 1, s.Height + 3, new Point(-1, 2)),
        ["FillColor"] = (s, _) => ops => ops.FillColor(Rgba8.Lime, new Rect(0, 0, Half(s.Width), Half(s.Height))),
        ["Color-Matrix"] = (_, _) => ops => ops.Brightness(1.1f).Contrast(1.2f).Saturation(1.3f).HueRotate(20),
        ["Color-Tables"] = (_, _) => ops => ops.Brightness(1.1f).Invert(0.2f).Opacity(0.8f),
        ["Grayscale-Linear"] = (_, _) => ops => ops.Grayscale(LumaWeights.Rec601, 0.7f, ColorSpace.Linear),
        ["Sepia"] = (_, _) => ops => ops.Sepia(),
        ["Blur"] = (_, _) => ops => ops.Blur(2),
        ["Blur-Linear"] = (_, _) => ops => ops.Blur(1.5f, ColorSpace.Linear),
        ["Blur-Boxes"] = (_, _) => ops => ops.Blur(120),
        ["BoxBlur"] = (_, _) => ops => ops.BoxBlur(3),
        ["Blur-Fast"] = (_, _) => ops => ops.Blur(6, ColorSpace.Encoded, BlurMethod.Fast),
        ["Median-Network"] = (_, _) => ops => ops.Median(2),
        ["Median-Histogram"] = (_, _) => ops => ops.Median(4, includeAlpha: true),
        ["LensBlur"] = (_, _) => ops => ops.LensBlur(5),
        ["LensBlur-Blades"] = (_, _) => ops => ops.LensBlur(3.5f, new LensBlurOptions { Blades = 6, Rotation = 30, HighlightGain = 3, ColorSpace = ColorSpace.Encoded }),
        ["BilateralBlur"] = (_, _) => ops => ops.BilateralBlur(2, 0.15f),
        ["BilateralBlur-Strided"] = (_, _) => ops => ops.BilateralBlur(6, 0.3f),
        ["MotionBlur"] = (_, _) => ops => ops.MotionBlur(4, 2, 30),
        ["MotionBlur-Centered"] = (_, _) => ops => ops.MotionBlur(3, 5, 270, centered: true),
        ["RotationalBlur"] = (_, _) => ops => ops.RotationalBlur(20),
        ["ZoomBlur"] = (_, _) => ops => ops.ZoomBlur(0.3f, new System.Numerics.Vector2(2, 3)),
        ["AddNoise-Gaussian"] = (_, _) => ops => ops.AddNoise(NoiseKind.Gaussian, 2, seed: 3),
        ["AddNoise-Poisson-Mono"] = (_, _) => ops => ops.AddNoise(NoiseKind.Poisson, 0.5f, monochrome: true),
        ["Despeckle"] = (_, _) => ops => ops.Despeckle(),
        ["Blur-Fast-Linear"] = (_, _) => ops => ops.Blur(3.5f, ColorSpace.Linear, BlurMethod.Fast),
        ["Lighting-Distant"] = (_, _) => ops => ops.Lighting(new DistantLight(45, 30), LightingModel.Diffuse(), new LightingOptions { SurfaceScale = 3 }),
        ["Lighting-Spot-Specular"] = (s, _) => ops => ops.Lighting(new SpotLight(new(s.Width / 2f, s.Height / 3f, 40), new(s.Width, s.Height, 0), 2, 60), LightingModel.Specular(1.5f, 10)),
        ["Lighting-Blend"] = (s, _) => ops => ops.Lighting(new PointLight(new(s.Width * 0.3f, 0, 30)), LightingModel.Diffuse(1.2f), new LightingOptions { Height = HeightSource.Luminance, Blend = BlendMode.Multiply }),
        ["Emboss"] = (_, _) => ops => ops.Emboss(),
        ["Emboss-Color"] = (_, _) => ops => ops.Emboss(30, 2, 2.5f, grayscale: false),
        ["Shade"] = (_, _) => ops => ops.Shade(),
        ["Shade-Colored"] = (_, _) => ops => ops.Shade(120, 45, colored: true),
        ["Edges-Sobel"] = (_, _) => ops => ops.Edges(),
        ["Edges-Kirsch-Color"] = (_, _) => ops => ops.Edges(EdgeOperator.Kirsch, grayscale: false),
        ["Edges-LoG"] = (_, _) => ops => ops.Edges(EdgeOperator.LaplacianOfGaussian),
        ["CannyEdges"] = (_, _) => ops => ops.CannyEdges(1.2f, 0.05f, 0.2f),
        ["Dilate-Square"] = (_, _) => ops => ops.Dilate(3),
        ["Erode-Rectangle"] = (_, _) => ops => ops.Erode(5, 1),
        ["Morphology-Close-Disk"] = (_, _) => ops => ops.Morphology(MorphologyOp.Close, StructuringElement.Disk(2), 2),
        ["Morphology-TopHat-Diamond"] = (_, _) => ops => ops.Morphology(MorphologyOp.TopHat, StructuringElement.Diamond(3)),
        ["Vignette"] = (_, _) => ops => ops.Vignette(0.6f, 0.5f, new Rgba8(20, 40, 160, 200)),
        ["Glow"] = (_, _) => ops => ops.Glow(3, new Rgba8(255, 200, 0, 220), 0.3f),
        ["Glow-Inner"] = (_, _) => ops => ops.Glow(2.5f, Rgba8.White, inner: true),
        ["InnerShadow"] = (_, _) => ops => ops.InnerShadow(2, -1.6f, 2, new Rgba8(0, 0, 40, 180)),
        ["Bloom"] = (_, _) => ops => ops.Bloom(0.6f, 4, 1.5f),
        ["Pixelate"] = (_, _) => ops => ops.Pixelate(5, new Point(2, -1)),
        ["OilPaint"] = (_, _) => ops => ops.OilPaint(2),
        ["OilPaint-256"] = (_, _) => ops => ops.OilPaint(3, 256),
        ["Kuwahara"] = (_, _) => ops => ops.Kuwahara(2),
        ["Kuwahara-Blur"] = (_, _) => ops => ops.Kuwahara(3, 1),
        ["Spread"] = (_, _) => ops => ops.Spread(3.5f, 11),
        ["DropShadow"] = (_, _) => ops => ops.DropShadow(3, 2, 2.5f, new Rgba8(10, 20, 80, 150)),
        ["DropShadow-Exact"] = (_, _) => ops => ops.DropShadow(-2, 4, 1.5f, Rgba8.Black, new DropShadowOptions { Method = BlurMethod.Exact, SigmaY = 3 }),
        ["DropShadow-Kept"] = (_, _) => ops => ops.DropShadow(2, 2, 4, new Rgba8(200, 0, 0, 255), new DropShadowOptions { Expand = false, ShadowOnly = true }),
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
        ["Tone-Curves"] = (_, _) => ops => ops.Solarize(0.9f).Level(0.1f, 0.9f, 1.2f).SigmoidalContrast(5).Tint(new Rgba8(255, 200, 150, 255), 0.5f),
        ["Tone-Mixed"] = (_, _) => ops => ops.Posterize(5).Saturation(1.2f).Gamma(1.3f).Duotone(Rgba8.Navy, Rgba8.Yellow),
        ["Threshold"] = (_, _) => ops => ops.Threshold(0.4f, ThresholdMode.Saturation, Rgba8.Red, Rgba8.Blue),
        ["Posterize-Ordered"] = (_, _) => ops => ops.Posterize(4, Egami.Quantization.DitherMode.Ordered),
        ["Posterize-Diffused"] = (_, _) => ops => ops.Posterize(3, Egami.Quantization.DitherMode.FloydSteinberg),
        ["ColorBlindness"] = (_, _) => ops => ops.SimulateColorBlindness(ColorVisionDeficiency.Deuteranopia, 0.7f),
        ["HighContrast"] = (_, _) => ops => ops.HighContrast(true, ContrastInvert.Lightness, 0.3f),
        ["Lut"] = (_, _) => ops => ops.ApplyFilter(ColorFilter.FromLut(Grade)).OnChannels(ColorChannels.Red | ColorChannels.Alpha, c => c.Gamma(1.4f).HueRotate(30)),
        ["WhiteBalance"] = (_, _) => ops => ops.WhiteBalance(3400, 0.2f).WhiteBalance(new Rgba8(200, 180, 150, 255)),
        ["AutoLevel"] = (_, _) => ops => ops.AutoLevel(),
        ["AutoGamma"] = (_, _) => ops => ops.AutoGamma(linked: false),
        ["Normalize"] = (_, _) => ops => ops.Normalize(),
        ["Equalize"] = (_, _) => ops => ops.Equalize(),
        ["Equalize-Channels"] = (_, _) => ops => ops.Equalize(EqualizeMode.Channels),
        ["AutoWhiteBalance"] = (_, _) => ops => ops.AutoWhiteBalance().AutoWhiteBalance(WhiteBalanceMethod.WhitePatch),
        ["AutoThreshold"] = (_, _) => ops => ops.AutoThreshold(ThresholdMethod.Kapur),
        ["AdaptiveThreshold"] = (_, _) => ops => ops.AdaptiveThreshold(0.1f),
        ["Clahe"] = (_, _) => ops => ops.Clahe(3, 2),
        ["LocalContrast"] = (_, _) => ops => ops.LocalContrast(0.3f, 0.4f),
    };

    /// <summary>A displacement map of the given size, made once per size and kept for the test run.</summary>
    private static Raster<Rgba8> MapFor(Size size)
    {
        return Maps.GetOrAdd(size, s => Noise(s.Width, s.Height, EgamiContext.Default));
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Size, Raster<Rgba8>> Maps = new();

    private static int Half(int size) => Math.Max(1, size / 2);

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
}

/// <summary>Part 1 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart0 : OperationCoverageTests<Part0>;

/// <summary>Part 2 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart1 : OperationCoverageTests<Part1>;

/// <summary>Part 3 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart2 : OperationCoverageTests<Part2>;

/// <summary>Part 4 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart3 : OperationCoverageTests<Part3>;

/// <summary>Part 5 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart4 : OperationCoverageTests<Part4>;

/// <summary>Part 6 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart5 : OperationCoverageTests<Part5>;

/// <summary>Part 7 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart6 : OperationCoverageTests<Part6>;

/// <summary>Part 8 of 8 of <see cref="OperationCoverageTests{TPart}"/>.</summary>
public sealed class OperationCoverageTestsPart7 : OperationCoverageTests<Part7>;
