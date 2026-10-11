using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Filters;

/// <summary>
/// The Phase 2.5.4 filters against ImageMagick outputs made by tools/make-filter-references.py. Each case names its Egami chain
/// and its gate: identity for filters that pick values, the 1.2 closeness gate (PSNR 40 dB and 8 levels) for filters that mix
/// them, or a looser closeness where the definitions differ, with the reason.
/// </summary>
public class FilterReferenceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "filters");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "filters");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    /// <summary>
    /// What a case must reach: the largest difference in levels (of its bit depth) and the least PSNR; or, for edge maps, that every
    /// edge pixel of ImageMagick's is one of Egami's and at least 99% of pixels agree.
    /// </summary>
    private sealed record Gate(int MaxLevels, double MinPsnr, bool EdgeMap = false)
    {
        internal static readonly Gate Identical = new(0, double.PositiveInfinity);
        internal static readonly Gate Close = new(8, 40);

        // ImageMagick rounds each motion and rotational sample to the nearest pixel; Egami reads between pixels with 2.5.2's
        // bilinear sampler (design question 5), which smooths diagonal streaks. With rounded offsets Egami's motion streaks are
        // the same to within float rounding (the "-rounded" cases, one level); the bilinear results are gated by PSNR alone.
        internal static readonly Gate Sampled = new(255, 30);
        internal static readonly Gate Rounded = new(1, 90);

        // BilateralBlur weighs neighbours by their RGB difference (design question 4); ImageMagick weighs by the 8-bit difference
        // of their intensity. On gray images the two agree (Close); on photos they differ at a few color edges, so only the PSNR
        // is gated; on colored salt-and-pepper noise, which intensity cannot tell from its surroundings, they differ by design.
        internal static readonly Gate BilateralColor = new(255, 40);
        internal static readonly Gate BilateralNoise = new(255, 20);

        /// <summary>No gate: the definitions differ by design, and the PSNR is only written to the test output.</summary>
        internal static readonly Gate Reported = new(255, 0);

        // ImageMagick 7.1.1's -canny keeps its tracing stack in one image row, so a trace that goes deeper than the image is
        // wide stops part way; Egami follows every connected pixel, so it marks a superset (see CannyTests).
        internal static readonly Gate Edges = new(255, 0, EdgeMap: true);
    }

    private static readonly Dictionary<string, (Action<RasterOps> Chain, Gate Gate)> Chains = new()
    {
        ["median-1"] = (o => o.Median(1), Gate.Identical),
        ["median-2"] = (o => o.Median(2), Gate.Identical),
        ["median-3"] = (o => o.Median(3), Gate.Identical),
        ["median-4"] = (o => o.Median(4), Gate.Identical),
        ["median-10"] = (o => o.Median(10), Gate.Identical),
        ["median-3-alpha"] = (o => o.Median(3, includeAlpha: true), Gate.Identical),
        ["bilateral-3-0.1"] = (o => o.BilateralBlur(3, 0.1f), Gate.BilateralColor),
        ["bilateral-2-0.2"] = (o => o.BilateralBlur(2, 0.2f), Gate.BilateralColor),
        ["bilateral-gray-3-0.1"] = (o => o.BilateralBlur(3, 0.1f), Gate.Close),
        ["motion-6-2-30"] = (o => o.MotionBlur(6, 2, 30), Gate.Sampled),
        ["motion-10-4-0"] = (o => o.MotionBlur(10, 4, 0), Gate.Close),
        ["motion-8-3-110"] = (o => o.MotionBlur(8, 3, 110), Gate.Sampled),
        ["motion-6-2-30-rounded"] = (o => o.Add(Egami.Operations.Filters.DirectionalBlurOperation.Motion(6, 2, 30, false, roundOffsets: true)), Gate.Rounded),
        ["motion-8-3-110-rounded"] = (o => o.Add(Egami.Operations.Filters.DirectionalBlurOperation.Motion(8, 3, 110, false, roundOffsets: true)), Gate.Rounded),
        ["rotational-10"] = (o => o.RotationalBlur(10), Gate.Sampled),
        ["rotational-4"] = (o => o.RotationalBlur(4), Gate.Sampled),
        ["despeckle"] = (o => o.Despeckle(), Gate.Identical),
        ["bilateral-noise-2-0.2"] = (o => o.BilateralBlur(2, 0.2f), Gate.BilateralNoise),
        ["shade-30x30"] = (o => o.Shade(30, 30), Gate.Identical),
        ["shade-120x45"] = (o => o.Shade(120, 45), Gate.Identical),
        ["shade-300x15"] = (o => o.Shade(300, 15), Gate.Identical),
        ["shade-colored-30x30"] = (o => o.Shade(30, 30, colored: true), Gate.Identical),
        ["shade-colored-210x60"] = (o => o.Shade(210, 60, colored: true), Gate.Identical),

        // ImageMagick's -emboss sharpens along a diagonal and then equalizes the histogram; Egami's is a directional relief on
        // mid gray (design question 11), so the two only share a name (about 9 dB).
        ["emboss"] = (o => o.Emboss(grayscale: false), Gate.Reported),
        ["canny-1"] = (o => o.CannyEdges(1, 0.1f, 0.3f), Gate.Edges),
        ["canny-1.4"] = (o => o.CannyEdges(1.4f, 0.05f, 0.2f), Gate.Edges),
        ["canny-2"] = (o => o.CannyEdges(2, 0.1f, 0.3f), Gate.Edges),
        ["dilate-disk-3"] = (o => o.Morphology(MorphologyOp.Dilate, StructuringElement.Disk(3)), Gate.Identical),
        ["dilate-square-5"] = (o => o.Dilate(5), Gate.Identical),
        ["erode-square-5"] = (o => o.Erode(5), Gate.Identical),
        ["dilate-square-25"] = (o => o.Dilate(25), Gate.Identical),
        ["dilate-rect-4x2"] = (o => o.Dilate(4, 2), Gate.Identical),
        ["erode-rect-1x3"] = (o => o.Erode(1, 3), Gate.Identical),
        ["erode-disk-12"] = (o => o.Morphology(MorphologyOp.Erode, StructuringElement.Disk(12)), Gate.Identical),
        ["open-disk-2"] = (o => o.Morphology(MorphologyOp.Open, StructuringElement.Disk(2)), Gate.Identical),
        ["close-diamond-3"] = (o => o.Morphology(MorphologyOp.Close, StructuringElement.Diamond(3)), Gate.Identical),
        ["close-cross-2-x2"] = (o => o.Morphology(MorphologyOp.Close, StructuringElement.Cross(2), 2), Gate.Identical),
        ["open-square-1"] = (o => o.Morphology(MorphologyOp.Open, StructuringElement.Square(1)), Gate.Identical),
        ["gradient-disk-2"] = (o => o.Morphology(MorphologyOp.Gradient, StructuringElement.Disk(2)), Gate.Identical),
        ["tophat-disk-4"] = (o => o.Morphology(MorphologyOp.TopHat, StructuringElement.Disk(4)), Gate.Identical),
        ["bottomhat-square-3"] = (o => o.Morphology(MorphologyOp.BottomHat, StructuringElement.Square(3)), Gate.Identical),
        ["dilate-disk-2"] = (o => o.Morphology(MorphologyOp.Dilate, StructuringElement.Disk(2)), Gate.Identical),
        ["dilate-disk-4"] = (o => o.Morphology(MorphologyOp.Dilate, StructuringElement.Disk(4)), Gate.Identical),
        ["erode-mask"] = (o => o.Morphology(MorphologyOp.Erode, Corner), Gate.Identical),
        ["dilate-mask"] = (o => o.Morphology(MorphologyOp.Dilate, Corner), Gate.Identical),
        ["close-square-3"] = (o => o.Morphology(MorphologyOp.Close, StructuringElement.Square(3)), Gate.Identical),
    };

    /// <summary>ImageMagick's kernel 3x3+0+1:1,1,-,-,1,1,1,-,-: lopsided, so dilation and erosion read it differently.</summary>
    private static StructuringElement Corner => StructuringElement.FromMask(
        new[,] { { true, true, false }, { false, true, true }, { true, false, false } }, new Point(0, 1));

    public static TheoryData<string> Cases()
    {
        return [.. File.ReadLines(Path.Combine(References, "manifest.txt")).Where(l => !l.StartsWith('#')).Select(l => l.Split(' ')[0])];
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void MatchesImageMagick(string name)
    {
        string line = File.ReadLines(Path.Combine(References, "manifest.txt")).Single(l => l.StartsWith(name + " ", StringComparison.Ordinal));
        string[] parts = line.Split(' ', 5);
        int bits = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int width = int.Parse(parts[2], CultureInfo.InvariantCulture);
        int height = int.Parse(parts[3], CultureInfo.InvariantCulture);
        string input = name[..name.IndexOf('.', StringComparison.Ordinal)];
        (Action<RasterOps> chain, Gate gate) = Chains[name[(input.Length + 1)..]];
        byte[] expected = Read(Path.Combine(References, $"{name}.rgba{bits}.gz"));

        // Compared on the stored values at the reference's depth, every channel, alpha included.
        ushort[] actual;
        if (bits == 16)
        {
            using Raster<Rgba16> source = Raster.Load<Rgba16>(Path.Combine(Images, input + ".png"));
            using Raster<Rgba16> result = source.Derive(chain, Ct);
            Assert.Equal((width, height), (result.Width, result.Height));
            actual = Values(result, (p, c) => c switch { 0 => p.R, 1 => p.G, 2 => p.B, _ => p.A });
        }
        else
        {
            using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, input + ".png"));
            using Raster<Rgba8> result = source.Derive(chain, Ct);
            Assert.Equal((width, height), (result.Width, result.Height));
            actual = Values(result, (p, c) => c switch { 0 => p.R, 1 => p.G, 2 => p.B, _ => p.A });
        }

        if (gate.EdgeMap)
        {
            AssertEdgeMap(name, expected, actual, width);
            return;
        }

        int worst = 0, at = -1;
        double squares = 0;
        for (int i = 0; i < actual.Length; i++)
        {
            int e = bits == 16 ? BinaryPrimitives.ReadUInt16BigEndian(expected.AsSpan(i * 2)) : expected[i];
            int d = Math.Abs(e - actual[i]);
            if (d > worst)
            {
                worst = d;
                at = i;
            }

            squares += (double)d * d;
        }

        double max = (1 << bits) - 1;
        double psnr = squares == 0 ? double.PositiveInfinity : 10 * Math.Log10(max * max / (squares / actual.Length));
        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: worst {worst}, PSNR {psnr:F2} dB");
        int levels = bits == 16 ? gate.MaxLevels * 257 : gate.MaxLevels;
        Assert.True(
            worst <= levels && psnr >= gate.MinPsnr,
            $"{name}: worst {worst} at pixel ({at / 4 % width}, {at / 4 / width}) channel {at % 4}, PSNR {psnr:F2} dB");
    }

    /// <summary>Asserts every edge of ImageMagick's map is one of Egami's, alpha stays opaque, and at least 99% of pixels agree.</summary>
    private static void AssertEdgeMap(string name, byte[] expected, ushort[] actual, int width)
    {
        int pixels = actual.Length / 4, theirs = 0, ours = 0, agree = 0;
        for (int i = 0; i < pixels; i++)
        {
            bool e = expected[i * 4] > 127, a = actual[i * 4] > 127;
            Assert.True(actual[(i * 4) + 3] == 255, $"{name}: alpha changed at ({i % width}, {i / width})");
            Assert.True(!e || a, $"{name}: ImageMagick's edge at ({i % width}, {i / width}) is missing");
            theirs += e ? 1 : 0;
            ours += a ? 1 : 0;
            agree += e == a ? 1 : 0;
        }

        TestContext.Current.TestOutputHelper?.WriteLine($"{name}: ImageMagick {theirs} edge pixels, Egami {ours}, {100.0 * agree / pixels:F2}% of pixels agree");
        Assert.True(agree >= 0.99 * pixels, $"{name}: only {100.0 * agree / pixels:F2}% of pixels agree (ImageMagick {theirs} edges, Egami {ours})");
    }

    private static ushort[] Values<TPixel>(Raster<TPixel> raster, Func<TPixel, int, int> channel)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ushort[] values = new ushort[raster.Width * raster.Height * 4];
        int i = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (TPixel p in raster.Row(y))
            {
                for (int c = 0; c < 4; c++)
                {
                    values[i++] = (ushort)channel(p, c);
                }
            }
        }

        return values;
    }

    private static byte[] Read(string path)
    {
        using GZipStream gzip = new(File.OpenRead(path), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }
}
