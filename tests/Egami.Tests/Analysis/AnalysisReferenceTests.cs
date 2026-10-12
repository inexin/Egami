using System.IO.Compression;
using System.Text.Json;

using Egami.Analysis;
using Egami.Colors;
using Egami.Operations;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Analysis;

/// <summary>
/// Phase 2.5.5 stage A against ImageMagick 7 Q16-HDRI and Python imagehash, from tools/make-analysis-references.py: compare
/// metrics within 1e-6 relative (AE identical), SSIM within 1e-4, statistics within 1e-6 relative, colors, opacity and gray
/// type identical, hashes within 4 bits, and the highlight picture identical at 8 bits.
/// </summary>
public class AnalysisReferenceTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "analysis");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "analysis");

    private static readonly Lazy<JsonElement> Json = new(() => JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "analysis.json"))).RootElement);

    public static TheoryData<string> Pairs() => [.. Json.Value.GetProperty("compare").EnumerateObject().Select(p => p.Name)];

    public static TheoryData<string> StatisticsImages() => [.. Json.Value.GetProperty("statistics").EnumerateObject().Select(p => p.Name)];

    public static TheoryData<string> HashImages() => [.. Json.Value.GetProperty("hashes").EnumerateObject().Select(p => p.Name)];

    /// <summary>Loads a corpus image; the 16-bit RGB gradients load as Rgba16, so they become Rgb16 as ImageMagick sees them (without alpha).</summary>
    private static Raster Load(string name)
    {
        Raster raster = Raster.Load(Path.Combine(Images, name));
        if (!name.StartsWith("grad16", StringComparison.Ordinal))
        {
            return raster;
        }

        using (raster)
        {
            return raster.As<Rgb16>();
        }
    }

    private static void Near(double actual, double expected, double relative, string what)
    {
        double tolerance = Math.Max(Math.Abs(expected) * relative, 1e-12);
        Assert.True(Math.Abs(actual - expected) <= tolerance, $"{what}: {actual:R}, ImageMagick {expected:R}");
    }

    private static double Value(JsonElement line) =>
        line.GetProperty("normalized").ValueKind == JsonValueKind.Number ? line.GetProperty("normalized").GetDouble() : line.GetProperty("raw").GetDouble();

    [Theory]
    [MemberData(nameof(Pairs))]
    public void CompareMatchesImageMagick(string pair)
    {
        string[] names = pair.Split('~');
        using Raster a = Load(names[0]);
        using Raster b = Load(names[1]);
        CompareResult result = Raster.Compare(a, b);

        // ImageMagick 7.1.1 computes SSIM on straight channels (the other metrics premultiplied), and gives alpha an SSIM of 0
        // when only one image has alpha, which "all" then averages in.
        CompareResult straight = Raster.Compare(a, b, new CompareOptions { Ssim = true, Alpha = AlphaCompare.Straight });
        bool oneAlpha = a.Layout.HasAlpha != b.Layout.HasAlpha;
        JsonElement metrics = Json.Value.GetProperty("compare").GetProperty(pair);
        foreach (JsonProperty metric in metrics.EnumerateObject())
        {
            foreach (JsonProperty line in metric.Value.EnumerateObject())
            {
                // ImageMagick 7.1.1 prints 0 for alpha whatever it is, but counts the real difference in "all".
                if (line.Name == "alpha")
                {
                    continue;
                }

                ChannelDifference channel = line.Name switch
                {
                    "red" => result.Red,
                    "green" => result.Green,
                    "blue" => result.Blue,
                    _ => result.Overall,
                };
                ChannelDifference ssim = line.Name switch
                {
                    "red" => straight.Red,
                    "green" => straight.Green,
                    "blue" => straight.Blue,
                    _ => straight.Overall,
                };
                if (line.Name == "all" && oneAlpha)
                {
                    ssim = new ChannelDifference { Ssim = (straight.Red.Ssim + straight.Green.Ssim + straight.Blue.Ssim) / 4 };
                }

                string what = $"{pair} {metric.Name} {line.Name}";
                double expected = Value(line.Value);
                switch (metric.Name)
                {
                    case "MAE":
                        Near(channel.Mae, expected, 1e-6, what);
                        break;
                    case "MSE":
                        Near(channel.Mse, expected, 1e-6, what);
                        break;
                    case "RMSE":
                        Near(channel.Rmse, expected, 1e-6, what);
                        break;
                    case "PSNR":
                        Near(channel.Psnr, expected, 1e-6, what);
                        break;
                    case "PAE":
                        Near(channel.PeakError, expected, 1e-6, what);
                        break;
                    case "SSIM":
                        Assert.True(Math.Abs(ssim.Ssim - expected) <= 1e-4, $"{what}: {ssim.Ssim:R}, ImageMagick {expected:R}");
                        break;
                    case "DSSIM":
                        Assert.True(Math.Abs(ssim.Dssim - expected) <= 1e-4, $"{what}: {ssim.Dssim:R}, ImageMagick {expected:R}");
                        break;
                }
            }
        }

        Assert.Equal(metrics.GetProperty("AE").GetProperty("all").GetProperty("raw").GetDouble(), result.DifferentPixels);
        CompareResult fuzzy = Raster.Compare(a, b, new CompareOptions { Fuzz = 0.05f });
        Assert.Equal(metrics.GetProperty("AE@5%").GetProperty("all").GetProperty("raw").GetDouble(), fuzzy.DifferentPixels);
    }

    [Theory]
    [MemberData(nameof(Pairs))]
    public void HighlightMatchesImageMagick(string pair)
    {
        Highlight(pair, "mask", new DifferenceOptions { LowlightOpacity = 0 });
        Highlight(pair, "diff", DifferenceOptions.Default);
    }

    private static void Highlight(string pair, string reference, DifferenceOptions options)
    {
        string[] names = pair.Split('~');
        using Raster a = Load(names[0]);
        using Raster b = Load(names[1]);
        using Raster difference = Raster.Difference(a, b, DifferenceStyle.Highlight, options);
        using Raster<Rgba8> rgba = difference.As<Rgba8>();
        byte[] expected = ReadGz(Path.Combine(References, $"{pair}.{reference}.rgba.gz"));
        byte[] actual = new byte[expected.Length];
        rgba.CopyPixelsTo(actual.AsSpan());
        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(StatisticsImages))]
    public void StatisticsMatchImageMagick(string name)
    {
        using Raster image = Load(name);
        RasterStatistics stats = image.Statistics();
        JsonElement reference = Json.Value.GetProperty("statistics").GetProperty(name);
        foreach (JsonProperty channel in reference.GetProperty("channels").EnumerateObject())
        {
            ChannelStatistics ours = channel.Name switch
            {
                "red" or "gray" => stats.Red,
                "green" => stats.Green,
                "blue" => stats.Blue,
                "alpha" => stats.Alpha,
                _ => default,
            };
            if (channel.Name == "overall")
            {
                continue;
            }

            JsonElement c = channel.Value;
            string what = $"{name} {channel.Name}";
            Near(ours.Min, Value(c.GetProperty("min")), 1e-6, what + " min");
            Near(ours.Max, Value(c.GetProperty("max")), 1e-6, what + " max");
            Near(ours.Mean, Value(c.GetProperty("mean")), 1e-6, what + " mean");
            Near(ours.StdDev, Value(c.GetProperty("standard_deviation")), 1e-6, what + " std dev");
            Near(ours.Skewness, c.GetProperty("skewness").GetProperty("raw").GetDouble(), 1e-6, what + " skewness");
            Near(ours.Kurtosis, c.GetProperty("kurtosis").GetProperty("raw").GetDouble(), 1e-6, what + " kurtosis");
            Near(ours.Entropy, c.GetProperty("entropy").GetProperty("raw").GetDouble(), 1e-6, what + " entropy");
        }

        Assert.Equal(reference.GetProperty("colors").GetInt64(), image.CountColors());
        Assert.Equal(reference.GetProperty("opaque").GetString() == "True", image.IsOpaque());
        Assert.Equal(reference.GetProperty("type").GetString() is "Grayscale" or "GrayscaleAlpha" or "Bilevel", image.IsGray());
    }

    [Theory]
    [MemberData(nameof(HashImages))]
    public void HashesWithinFourBitsOfImagehash(string name)
    {
        using Raster image = Load(name);
        JsonElement reference = Json.Value.GetProperty("hashes").GetProperty(name);
        ulong phash = Convert.ToUInt64(reference.GetProperty("phash").GetString(), 16);
        ulong dhash = Convert.ToUInt64(reference.GetProperty("dhash").GetString(), 16);
        int dct = PerceptualHash.Distance(PerceptualHash.Dct(image), phash);
        int difference = PerceptualHash.Distance(PerceptualHash.Difference(image), dhash);
        Assert.True(dct <= 4, $"{name}: DCT hash {PerceptualHash.Dct(image):x16} is {dct} bits from imagehash's {phash:x16}");
        Assert.True(difference <= 4, $"{name}: difference hash {PerceptualHash.Difference(image):x16} is {difference} bits from imagehash's {dhash:x16}");
    }

    /// <summary>
    /// Each dither costs at most 0.5 dB more than ImageMagick's own does, measured against each library's undithered result,
    /// so the gate is about the dither and not about the two quantizers' palettes.
    /// </summary>
    [Theory]
    [InlineData("duck.png")]
    [InlineData("egret.png")]
    public void DithersWithinHalfADecibelOfImageMagick(string name)
    {
        using Raster image = Load(name);
        using Raster<Rgb8> rgb = image.As<Rgb8>();
        JsonElement reference = Json.Value.GetProperty("quantize");
        foreach (int colors in new[] { 16, 64 })
        {
            double Ours(DitherMode mode)
            {
                using Raster quantized = rgb.Derive(o => o.Quantize(new PaletteOptions { MaxColors = colors, Dither = mode }), TestContext.Current.CancellationToken);
                return Raster.Compare(rgb, quantized).Overall.Psnr;
            }

            double oursNone = Ours(DitherMode.None);
            double magickNone = reference.GetProperty($"{name}:None:{colors}").GetDouble();
            Assert.True(oursNone >= magickNone - 0.5, $"{name} {colors} undithered: {oursNone:F2} dB, ImageMagick {magickNone:F2} dB");
            foreach ((string dither, DitherMode mode) in new[] { ("Riemersma", DitherMode.Riemersma), ("FloydSteinberg", DitherMode.FloydSteinberg) })
            {
                double ours = oursNone - Ours(mode);
                double magick = magickNone - reference.GetProperty($"{name}:{dither}:{colors}").GetDouble();
                Assert.True(ours <= magick + 0.5, $"{name} {dither} {colors}: costs {ours:F2} dB, ImageMagick's {magick:F2} dB");
            }
        }
    }

    [Theory]
    [InlineData("duck.png")]
    [InlineData("egret.png")]
    public void DominantColorsNearImageMagickKmeans(string name)
    {
        using Raster image = Load(name);
        IReadOnlyList<WeightedColor> ours = image.DominantColors(5);
        List<OklabColor> theirs = [.. Json.Value.GetProperty("kmeans").GetProperty(name).EnumerateArray()
            .Select(e => OklabColor.From(Rgba8.FromHex(e.GetProperty("color").GetString()!)))
            .OrderBy(c => c.L)];
        List<OklabColor> mine = [.. ours.Select(c => OklabColor.From(c.Color)).OrderBy(c => c.L)];
        Assert.Equal(theirs.Count, mine.Count);
        for (int i = 0; i < mine.Count; i++)
        {
            double de = Math.Sqrt(Math.Pow(mine[i].L - theirs[i].L, 2) + Math.Pow(mine[i].A - theirs[i].A, 2) + Math.Pow(mine[i].B - theirs[i].B, 2));
            // The design's gate was 0.05; ImageMagick clusters in RGB and Egami in Oklab, which moves the edge of the duck's
            // orange cluster by ΔE 0.062 (every other color is within 0.05). Lars accepted 0.07 at the API review (2026-10-12).
            Assert.True(de <= 0.07, $"{name} color {i}: {mine[i]} vs ImageMagick {theirs[i]}, ΔE {de:F3}");
        }
    }

    private static byte[] ReadGz(string path)
    {
        using FileStream file = File.OpenRead(path);
        using GZipStream gz = new(file, CompressionMode.Decompress);
        using MemoryStream memory = new();
        gz.CopyTo(memory);
        return memory.ToArray();
    }
}
