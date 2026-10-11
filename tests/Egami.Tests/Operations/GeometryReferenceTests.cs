using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// Phase 2.5.2 against ImageMagick, from tools/make-geometry-references.py: point mapping of every warp and transform on a
/// coordinate-coded image (the same source pixel away from ties at half pixels), the closeness of EWA sampling where an image
/// shrinks, and Trim's rectangles against -trim with -fuzz.
/// </summary>
public class GeometryReferenceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "geometry");
    private static readonly string OpsImages = Path.Combine(AppContext.BaseDirectory, "images", "ops");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "geometry");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static SamplingOptions Point => new() { Filter = ResampleFilter.Nearest };

    private static readonly Lazy<Raster> DisplacementMap = new(() => Raster.Load(Path.Combine(Images, "dmap.png")));

    /// <summary>The Egami chain for each case.</summary>
    private static readonly Dictionary<string, Action<RasterOps>> Chains = new()
    {
        ["coded.barrel"] = ops => ops.Warp(Warps.Barrel(0.1, -0.2, 0.05), Point),
        ["coded.barrel-center"] = ops => ops.Warp(Warps.Barrel(0.05, 0, -0.1, 1.1, new Vector2(70, 60)), Point),
        ["coded.barrel-inverse"] = ops => ops.Warp(Warps.BarrelInverse(0.05, -0.05, 0.02), Point),
        ["coded.polar"] = ops => ops.Warp(Warps.Polar(), Point),
        ["coded.polar-args"] = ops => ops.Warp(Warps.Polar(60, 10, new Vector2(100, 75), -90, 180), Point),
        ["coded.depolar"] = ops => ops.Warp(Warps.DePolar(), Point),
        ["coded.depolar-args"] = ops => ops.Warp(Warps.DePolar(70, 20, new Vector2(90, 80)), Point),
        ["coded.arc"] = ops => ops.Warp(Warps.Arc(60), Point),
        ["coded.arc-args"] = ops => ops.Warp(Warps.Arc(180, 30, 120, 40), Point),
        ["coded.swirl"] = ops => ops.Warp(Warps.Swirl(120), Point),
        ["coded.implode"] = ops => ops.Warp(Warps.Implode(0.6f), Point),
        ["coded.explode"] = ops => ops.Warp(Warps.Implode(-0.6f), Point),
        ["coded.wave"] = ops => ops.Warp(Warps.Wave(8, 40), Point),
        ["coded.roll"] = ops => ops.Offset(13, 7),
        ["coded.affine"] = ops => ops.Transform(new Homography(1.1037, 0.4113, 0, -0.2971, 0.9089, 0, 12.31, -5.17, 1), new TransformOptions { Filter = ResampleFilter.Nearest, Viewport = new Rect(-30, -20, 260, 220) }),
        ["coded.quad"] = ops => ops.QuadDistort(new(20, 10), new(210, 0), new(230, 170), new(0, 160), new TransformOptions { Filter = ResampleFilter.Nearest, Viewport = new Rect(0, 0, 230, 180) }),
        ["coded.displace"] = ops => ops.Displace(DisplacementMap.Value, 20, 20, options: Point),
        ["zone.shrink"] = ops => ops.Transform(new Matrix3x2(0.383f, 0.117f, -0.117f, 0.383f, 25, 15), new TransformOptions { Viewport = new Rect(0, 0, 110, 100) }),
        ["duck.shrink"] = ops => ops.Transform(new Matrix3x2(0.383f, 0.117f, -0.117f, 0.383f, 60, 10), new TransformOptions { Viewport = new Rect(0, 0, 300, 260) }),
        ["duck.quad"] = ops => ops.QuadDistort(new(200, 40), new(440, 40), new(640, 440), new(0, 440), new TransformOptions { Viewport = new Rect(0, 0, 640, 480) }),
        ["duck.skew"] = ops => ops.Transform(new Matrix3x2(1, 0, 0.2f, 1, 0, 0), new TransformOptions { Viewport = new Rect(0, 0, 740, 480) }),
        ["duck.barrel"] = ops => ops.Warp(Warps.Barrel(0, -0.06, 0)),
    };

    public static TheoryData<string> PointCases() => [.. Lines().Where(l => l.Name.StartsWith("coded.", StringComparison.Ordinal)).Select(l => l.Name)];

    public static TheoryData<string> ClosenessCases() => [.. Lines().Where(l => (l.Name.StartsWith("zone.", StringComparison.Ordinal) || l.Name.StartsWith("duck.", StringComparison.Ordinal)) && Chains.ContainsKey(l.Name)).Select(l => l.Name)];

    public static TheoryData<string> FilterCases() => [.. Lines().Where(l => l.Name.Contains(".down-", StringComparison.Ordinal) || l.Name.Contains(".up-", StringComparison.Ordinal)).Select(l => l.Name)];

    public static TheoryData<ResampleFilter> NewFilters() => [ResampleFilter.Lanczos2, ResampleFilter.Hermite, ResampleFilter.Gaussian, ResampleFilter.Spline];

    public static TheoryData<string> TrimCases() => [.. Lines().Where(l => l.Name.Contains(".trim-", StringComparison.Ordinal)).Select(l => l.Name)];

    [Theory]
    [MemberData(nameof(PointCases))]
    public void PointMappingMatchesImageMagick(string name)
    {
        (int width, int height, Rgba8[] expected) = Reference(name);
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(Images, "coded.png"));
        using Raster<Rgba8> result = source.Derive(Chains[name], Ct);
        Assert.Equal((width, height), (result.Width, result.Height));

        // A pixel may pick the neighbouring source pixel where the mapped point lies within float rounding of a half pixel; any
        // other difference is a mapping error.
        int ties = 0, covered = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba8> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                Rgba8 e = expected[(y * width) + x], a = row[x];
                covered += e.A > 0 ? 1 : 0;
                if (e == a)
                {
                    continue;
                }

                bool neighbour = e.A == 255 && a.A == 255 && Math.Abs(e.R - a.R) <= 1 && Math.Abs(e.G - a.G) <= 1;
                bool edge = (e.A == 0) != (a.A == 0);
                Assert.True(neighbour || edge, $"{name} ({x}, {y}): {a}, ImageMagick {e}");
                ties++;
            }
        }

        // Ties and edge pixels (where the source point lies within rounding of the image's border) stay rare.
        Assert.True(ties <= Math.Max(4, covered / 200), $"{name}: {ties} of {covered} pixels differ by a neighbour");
    }

    [Theory]
    [MemberData(nameof(ClosenessCases))]
    public void EwaIsCloseToImageMagicks(string name)
    {
        (int width, int height, Rgba8[] expected) = Reference(name);
        string input = name[..name.IndexOf('.', StringComparison.Ordinal)];
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(OpsImages, input + ".png"));
        using Raster<Rgba8> result = source.Derive(Chains[name], Ct);
        Assert.Equal((width, height), (result.Width, result.Height));

        // Compared as premultiplied color: ImageMagick blends straight color with the transparent background at the edges.
        double squares = 0;
        long count = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba8> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                Rgba8 e = expected[(y * width) + x], a = row[x];
                squares += Sq((e.R * e.A / 255.0) - (a.R * a.A / 255.0)) + Sq((e.G * e.A / 255.0) - (a.G * a.A / 255.0))
                    + Sq((e.B * e.A / 255.0) - (a.B * a.A / 255.0)) + Sq(e.A - a.A);
                count += 4;
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(squares / count, 1e-12));
        Assert.True(psnr >= 40, $"{name}: PSNR {psnr:F2} dB");
    }

    [Theory]
    [MemberData(nameof(FilterCases))]
    public void ResizeFiltersAreCloseToImageMagicks(string name)
    {
        (int width, int height, Rgba8[] expected) = Reference(name);
        string input = name[..name.IndexOf('.', StringComparison.Ordinal)];
        string kind = name[(input.Length + 1)..];
        ResampleFilter filter = Enum.Parse<ResampleFilter>(kind[(kind.IndexOf('-', StringComparison.Ordinal) + 1)..], ignoreCase: true);
        using Raster<Rgba8> source = Raster.Load<Rgba8>(Path.Combine(OpsImages, input + ".png"));
        using Raster<Rgba8> result = source.Derive(
            ops =>
            {
                if (kind.StartsWith("up-", StringComparison.Ordinal))
                {
                    ops.Crop(260, 200, 120, 90);
                }

                ops.Resize(width, height, filter);
            },
            Ct);
        double squares = 0;
        long count = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba8> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                Rgba8 e = expected[(y * width) + x], a = row[x];
                squares += Sq(e.R - a.R) + Sq(e.G - a.G) + Sq(e.B - a.B) + Sq(e.A - a.A);
                count += 4;
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255.0 / Math.Max(squares / count, 1e-12));
        Assert.True(psnr >= 40, $"{name}: PSNR {psnr:F2} dB");
    }

    [Theory]
    [MemberData(nameof(NewFilters))]
    public void KernelsMatchImageMagicksTables(ResampleFilter filter)
    {
        // ImageMagick prints its kernel to six significant digits; its Lanczos2 is a fast approximation of sinc, within 1e-6 of
        // the true sinc Egami uses.
        string[] lines = File.ReadAllLines(Path.Combine(References, $"filter-{filter.ToString().ToLowerInvariant()}.txt"));
        double support = double.Parse(lines.Single(l => l.StartsWith("# support = ", StringComparison.Ordinal))[12..], CultureInfo.InvariantCulture);
        Assert.Equal(support, Egami.Operations.Resampling.ResampleKernels.Support(filter));
        int checkedValues = 0;
        foreach (string line in lines.Where(l => l.Length > 0 && !l.StartsWith('#')))
        {
            string[] parts = line.Trim().Split('\t');
            double x = double.Parse(parts[0], CultureInfo.InvariantCulture), value = double.Parse(parts[1], CultureInfo.InvariantCulture);
            double ours = Egami.Operations.Resampling.ResampleKernels.Evaluate(filter, x);
            Assert.True(Math.Abs(ours - value) <= 1e-6 + (5e-6 * Math.Abs(value)), $"{filter} at {x}: {ours}, ImageMagick {value}");
            checkedValues++;
        }

        Assert.True(checkedValues > 100);
    }

    [Theory]
    [MemberData(nameof(TrimCases))]
    public void TrimMatchesImageMagick(string name)
    {
        string[] parts = Lines().Single(l => l.Name == name).Line.Split(' ');
        string[] geometry = parts[1].Split('x', '+');
        Rect expected = new(int.Parse(geometry[2], CultureInfo.InvariantCulture), int.Parse(geometry[3], CultureInfo.InvariantCulture), int.Parse(geometry[0], CultureInfo.InvariantCulture), int.Parse(geometry[1], CultureInfo.InvariantCulture));
        float tolerance = float.Parse(parts[3].TrimEnd('%'), CultureInfo.InvariantCulture) / 100;
        string input = name[..name.IndexOf('.', StringComparison.Ordinal)];
        using Raster raster = Raster.Load(Path.Combine(Images, input + ".png"));
        Rect bounds = raster.TrimBounds(tolerance);

        // ImageMagick keeps the whole image when nothing is border-free... and when everything is: -trim of an image with no
        // border returns it whole, which TrimBounds reports as the full rectangle too.
        Assert.Equal(expected, bounds.IsEmpty ? new Rect(0, 0, raster.Width, raster.Height) : bounds);
    }

    private static double Sq(double v) => v * v;

    private static IEnumerable<(string Name, string Line)> Lines()
    {
        return File.ReadLines(Path.Combine(References, "manifest.txt")).Where(l => !l.StartsWith('#')).Select(l => (l.Split(' ')[0], l));
    }

    private static (int Width, int Height, Rgba8[] Pixels) Reference(string name)
    {
        string[] parts = Lines().Single(l => l.Name == name).Line.Split(' ');
        int width = int.Parse(parts[1], CultureInfo.InvariantCulture);
        int height = int.Parse(parts[2], CultureInfo.InvariantCulture);
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name + ".rgba8.gz")), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return (width, height, MemoryMarshal.Cast<byte, Rgba8>(buffer.ToArray()).ToArray());
    }
}
