using System.Globalization;

using Egami.Formats.Extended.Raw;
using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Raw;

/// <summary>
/// RAW coverage plan task 6: the noise model Egami measures for a maker file (which has no NoiseProfile) against the one Adobe
/// DNG Converter writes into its DNG of the same file. Runs only when EGAMI_RAW_CORPUS names a folder of RAW files with Adobe's
/// DNGs of them in its dng subfolder (egami-compare's .cache/raw).
/// </summary>
public class RawNoiseTests
{
    /// <summary>Signal levels (0 to 1 of the white level) at which the two models' noise is compared.</summary>
    private static readonly double[] Levels = [0.02, 0.1, 0.3];

    [Theory]
    [InlineData(2e-4, 4e-6, false)]
    [InlineData(5e-5, 1e-6, false)]
    [InlineData(2e-4, 4e-6, true)]
    public void EstimateFindsTheNoiseOfASyntheticPlane(double s, double o, bool textured)
    {
        // A smooth ramp (and, textured, a quarter of strong detail, as a real image has) with Poisson-Gaussian noise of a
        // known model: the estimate is within 15 % of the true standard deviation across the brightness range.
        const int w = 1024, h = 1024;
        float[] plane = new float[w * h];
        Random random = new(1);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                double signal = 0.03 + (0.8 * x / w) + (0.05 * y / h);
                if (textured && y < h / 4)
                {
                    signal *= 1 + (0.3 * Math.Sin(x * 0.9) * Math.Cos(y * 1.3));
                }

                double gauss = Math.Sqrt(-2 * Math.Log(1 - random.NextDouble())) * Math.Cos(2 * Math.PI * random.NextDouble());
                plane[(y * w) + x] = (float)(signal + (gauss * Math.Sqrt((s * signal) + o)));
            }
        }

        (double es, double eo) = RawDenoise.Estimate(plane, w, h);
        foreach (double level in Levels)
        {
            double ratio = Math.Sqrt((es * level) + eo) / Math.Sqrt((s * level) + o);
            TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture, $"at {level}: {ratio:F3}"));
            Assert.InRange(ratio, 0.85, 1.15);
        }
    }

    [Fact]
    public void MeasuredNoiseIsNearAdobesNoiseProfile()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_RAW_CORPUS");
        Assert.SkipWhen(folder is null || !Directory.Exists(Path.Combine(folder, "dng")), "Set EGAMI_RAW_CORPUS to egami-compare's .cache/raw.");
        List<double> ratios = [];
        foreach (string path in Directory.EnumerateFiles(folder!).Where(p => Path.GetExtension(p) is not (".dng" or ".cr3" or ".tif")).Order())
        {
            string dng = Path.Combine(folder!, "dng", Path.GetFileNameWithoutExtension(path) + ".dng");
            if (!File.Exists(dng) || AdobeProfile(dng) is not double[] profile)
            {
                continue;
            }

            Raster<Gray16> mosaic;
            try
            {
                mosaic = Raster.Load<Gray16>(path, TestImages.Context, new RawReaderOptions { Output = RawOutput.Mosaic });
            }
            catch (Exception e) when (e is NotSupportedException or UnsupportedFormatException or InvalidDataException or CorruptDataException)
            {
                continue;
            }

            using (mosaic)
            {
                if (mosaic.Metadata.FormatDetails is not RawDetails { CfaPattern: string pattern, CfaWidth: 2, CfaHeight: 2 } details)
                {
                    continue;
                }

                List<string> cells = [];
                for (int phase = 0; phase < 4; phase++)
                {
                    // The pattern starts at the sensor's origin, the black levels (and the phases here) at the active area's.
                    Rect active = details.ActiveArea;
                    int color = "RGB".IndexOf(pattern[((((phase >> 1) + active.Y) & 1) * 2) + (((phase & 1) + active.X) & 1)]);
                    (double s, double o) = Measure(mosaic, details, phase);
                    int at = profile.Length >= 6 ? Math.Min(color, (profile.Length / 2) - 1) * 2 : 0;
                    foreach (double level in Levels)
                    {
                        double ratio = Math.Sqrt((s * level) + o) / Math.Sqrt((profile[at] * level) + profile[at + 1]);
                        ratios.Add(ratio);
                        cells.Add(ratio.ToString("F2", CultureInfo.InvariantCulture));
                    }
                }

                TestContext.Current.TestOutputHelper?.WriteLine($"{Path.GetFileName(path)}: noise / Adobe's at {string.Join(", ", Levels)} per CFA position: {string.Join(" ", cells)}");
            }
        }

        Assert.SkipWhen(ratios.Count == 0, "No maker file with Adobe's DNG in the corpus.");
        ratios.Sort();
        TestContext.Current.TestOutputHelper?.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{ratios.Count} comparisons: median {ratios[ratios.Count / 2]:F2}, 10th percentile {ratios[ratios.Count / 10]:F2}, 90th {ratios[ratios.Count * 9 / 10]:F2}"));
        Assert.InRange(ratios[ratios.Count / 2], 0.8, 1.5);
    }

    /// <summary>Measures one CFA position's noise model over the active area, its values scaled to 0 to 1 as the develop does.</summary>
    private static (double S, double O) Measure(Raster<Gray16> mosaic, RawDetails details, int phase)
    {
        Rect active = details.ActiveArea;
        int px = phase & 1, py = phase >> 1;
        int w = active.Width / 2, h = active.Height / 2;
        double black = details.BlackLevels.Count == 4 ? details.BlackLevels[phase] : details.BlackLevels.Count > 0 ? details.BlackLevels[0] : 0;
        double white = details.WhiteLevels.Count > 0 ? details.WhiteLevels[0] : 65535;
        float[] plane = new float[w * h];
        for (int y = 0; y < h; y++)
        {
            ReadOnlySpan<Gray16> row = mosaic.Row(active.Y + (2 * y) + py);
            for (int x = 0; x < w; x++)
            {
                plane[(y * w) + x] = (float)((row[active.X + (2 * x) + px].L - black) / (white - black));
            }
        }

        return RawDenoise.Estimate(plane, w, h);
    }

    /// <summary>Reads the NoiseProfile tag (51041) of a DNG's IFD0 or raw IFD, or <see langword="null"/>.</summary>
    private static double[]? AdobeProfile(string dng)
    {
        using RawFile file = RawFile.Open(new EgamiInput(File.OpenRead(dng), TestImages.Context, default));
        return file.Frame.NoiseProfile;
    }
}
