using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Tone;

/// <summary>
/// Phase 2.5.3 against ImageMagick 7 at Q16, from tools/make-tone-references.py: tone steps within a level at 8 bits and two at
/// 16; thresholds identical, with the same automatic levels; the auto steps within a level (the same black and white points);
/// white balance, CLAHE and local contrast within the 1.2 closeness gate (PSNR 40 dB and 8 levels).
/// </summary>
public class ToneReferenceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "tone");
    private static readonly string OpsImages = Path.Combine(AppContext.BaseDirectory, "images", "ops");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "tone");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static readonly Lazy<Lut3D> Hald = new(() =>
    {
        using Raster raster = Raster.Load(Path.Combine(Images, "hald.png"));
        return Lut3D.FromHald(raster);
    });

    /// <summary>The Egami chain for each case, and its gate: the most levels any channel may differ, and the least PSNR.</summary>
    private static readonly Dictionary<string, (Action<RasterOps> Chain, int Levels, double Psnr)> Cases = new()
    {
        ["level"] = (o => o.Level(0.1f, 0.85f, 1.3f), 1, 0),
        ["plus-level"] = (o => o.Level(outputBlack: 0.2f, outputWhite: 0.7f), 1, 0),
        ["gamma"] = (o => o.Gamma(2.2f), 1, 0),
        ["sigmoidal"] = (o => o.SigmoidalContrast(5, 0.4f), 1, 0),
        ["sigmoidal-down"] = (o => o.SigmoidalContrast(5, 0.4f, increase: false), 1, 0),
        ["posterize"] = (o => o.Posterize(5), 1, 0),
        ["solarize"] = (o => o.Solarize(0.6f), 1, 0),
        ["colorize"] = (o => o.Colorize(new Rgba8(255, 128, 0, 255), 0.3f), 1, 0),
        ["tint"] = (o => o.Tint(new Rgba8(255, 200, 150, 255), 0.4f), 1, 0),
        ["tint-full"] = (o => o.Tint(new Rgba8(40, 90, 200, 255)), 1, 0),
        ["duotone"] = (o => o.Duotone(Rgba8.FromHex("#1b2a49"), Rgba8.FromHex("#f2c14e")), 1, 0),
        ["threshold"] = (o => o.Threshold(0.45f), 0, 0),
        ["threshold-channels"] = (o => o.Threshold(0.4f, ThresholdMode.Channels), 0, 0),
        ["auto-level"] = (o => o.AutoLevel(), 1, 0),
        ["auto-level-channels"] = (o => o.AutoLevel(linked: false), 1, 0),
        ["contrast-stretch"] = (o => o.ContrastStretch(0.02f, 0.01f), 1, 0),
        ["normalize"] = (o => o.Normalize(), 1, 0),
        ["auto-gamma"] = (o => o.AutoGamma(), 1, 0),
        ["auto-gamma-channels"] = (o => o.AutoGamma(linked: false), 1, 0),
        ["equalize-channels"] = (o => o.Equalize(EqualizeMode.Channels), 1, 0),
        ["white-balance"] = (o => o.AutoWhiteBalance(), 8, 40),
        ["clahe"] = (o => o.Clahe(), 8, 40),
        ["local-contrast"] = (o => o.LocalContrast(), 8, 40),
        ["local-contrast-strong"] = (o => o.LocalContrast(0.3f, 0.6f), 8, 40),
        ["hald-clut"] = (o => o.ApplyFilter(ColorFilter.FromLut(Hald.Value, LutInterpolation.Trilinear)), 1, 0),
        ["auto-threshold-otsu"] = (o => o.AutoThreshold(ThresholdMethod.Otsu), 0, 0),
        ["auto-threshold-triangle"] = (o => o.AutoThreshold(ThresholdMethod.Triangle), 0, 0),
        ["auto-threshold-kapur"] = (o => o.AutoThreshold(ThresholdMethod.Kapur), 0, 0),
    };

    public static TheoryData<string> EightBitCases() => [.. Lines().Where(l => !l.Name.StartsWith("ramp16.", StringComparison.Ordinal)).Select(l => l.Name)];

    public static TheoryData<string> SixteenBitCases() => [.. Lines().Where(l => l.Name.StartsWith("ramp16.", StringComparison.Ordinal)).Select(l => l.Name)];

    [Theory]
    [MemberData(nameof(EightBitCases))]
    public void MatchesImageMagick(string name)
    {
        (_, int width, int height, string? level) = Lines().Single(l => l.Name == name);
        string input = name[..name.IndexOf('.', StringComparison.Ordinal)], step = name[(input.Length + 1)..];
        (Action<RasterOps> chain, int levels, double minimum) = Cases[step];
        string path = input == "duck" ? Path.Combine(OpsImages, "duck.png") : Path.Combine(Images, input + ".png");
        using Raster<Rgba8> source = Raster.Load<Rgba8>(path);
        using Raster<Rgba8> result = source.Derive(chain, Ct);
        Rgba8[] expected = Reference<Rgba8>(name + ".rgba8", width * height);

        int worst = 0;
        double squares = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Rgba8> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                Rgba8 e = expected[(y * width) + x], a = row[x];
                worst = Math.Max(worst, Math.Max(Math.Abs(e.R - a.R), Math.Max(Math.Abs(e.G - a.G), Math.Abs(e.B - a.B))));
                squares += Sq(e.R - a.R) + Sq(e.G - a.G) + Sq(e.B - a.B);
            }
        }

        double psnr = squares == 0 ? double.PositiveInfinity : 10 * Math.Log10(255.0 * 255 * 3 * width * height / squares);
        Assert.True(worst <= levels && psnr >= minimum, $"{name}: worst {worst} levels (allowed {levels}), PSNR {psnr:F1} dB (needs {minimum})");

        if (level is not null)
        {
            // The level ImageMagick chose, as a percentage with its %g formatting.
            ThresholdMethod method = Enum.Parse<ThresholdMethod>(step["auto-threshold-".Length..], ignoreCase: true);
            float found = source.FindThreshold(method);
            Assert.Equal(level, (found * 100).ToString("G6", CultureInfo.InvariantCulture) + "%");
        }
    }

    [Theory]
    [MemberData(nameof(SixteenBitCases))]
    public void SixteenBitCurvesMatchImageMagick(string name)
    {
        (_, int width, int height, _) = Lines().Single(l => l.Name == name);
        string step = name["ramp16.".Length..];
        using Raster<Gray16> source = Raster.Load<Gray16>(Path.Combine(Images, "ramp16.png"));
        using Raster<Gray16> result = source.Derive(Cases[step].Chain, Ct);
        Rgba16[] expected = Reference<Rgba16>(name + ".rgba16", width * height);
        int worst = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<Gray16> row = result.Row(y);
            for (int x = 0; x < width; x++)
            {
                worst = Math.Max(worst, Math.Abs(expected[(y * width) + x].R - row[x].L));
            }
        }

        Assert.True(worst <= 2, $"{name}: worst {worst} 16-bit levels");
    }

    private static T[] Reference<T>(string name, int count)
        where T : unmanaged
    {
        using FileStream file = File.OpenRead(Path.Combine(References, name + ".gz"));
        using GZipStream gzip = new(file, CompressionMode.Decompress);
        T[] pixels = new T[count];
        gzip.ReadExactly(MemoryMarshal.AsBytes(pixels.AsSpan()));
        return pixels;
    }

    private static IEnumerable<(string Name, int Width, int Height, string? Level)> Lines()
    {
        foreach (string line in File.ReadLines(Path.Combine(References, "manifest.txt")))
        {
            if (line.StartsWith('#'))
            {
                continue;
            }

            string[] parts = line.Split(' ');
            yield return (parts[0], int.Parse(parts[1], CultureInfo.InvariantCulture), int.Parse(parts[2], CultureInfo.InvariantCulture), parts[3].EndsWith('%') ? parts[3] : null);
        }
    }

    private static double Sq(double v) => v * v;
}
