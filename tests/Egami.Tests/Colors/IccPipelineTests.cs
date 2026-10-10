using System.Text.Json;

using Egami.Colors;

namespace Egami.Tests.Colors;

/// <summary>
/// Phase 2.1.2 task 4: the reference pipeline against Little CMS 2's transforms of the reference profiles. Matrix profiles match
/// within 1e-4 of the device range (and 0.01 in L*a*b*). Profiles with tables match within 0.0025 (0.03 in L*a*b*): Little CMS
/// interpolates them in 16-bit fixed point, and a steep output curve can turn its last-bit rounding into that much.
/// </summary>
public class IccPipelineTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files() => [.. IccReferences.Files];

    public static TheoryData<string, string> BuiltIns() => new()
    {
        { "sRGB", "compact/sRGB-v4.icc" },
        { "Display P3", "compact/DisplayP3-v4.icc" },
        { "Rec. 2020", "compact/Rec2020-v4.icc" },
        { "ProPhoto RGB", "compact/ProPhoto-v4.icc" },
        { "Adobe RGB (1998)", "compact/AdobeCompat-v2.icc" },
        { "Linear sRGB", "compact/scRGB-v2.icc" },
    };

    /// <summary>
    /// A built-in encoding decodes to the same connection space XYZ as the ICC profile written for it, within the profile's
    /// s15.16 rounding. (Converted onward, that rounding can grow: ProPhoto's blue has Y = 0.0001, so its stored colorant
    /// barely fixes its chromaticity.)
    /// </summary>
    [Theory]
    [MemberData(nameof(BuiltIns))]
    public void BuiltInsDecodeLikeTheirIccProfiles(string name, string file)
    {
        ColorProfile builtIn = new[] { ColorProfile.Srgb, ColorProfile.DisplayP3, ColorProfile.Rec2020, ColorProfile.ProPhotoRgb, ColorProfile.AdobeRgb, ColorProfile.LinearSrgb }.Single(p => p.Name == name);
        ColorProfile icc = ColorProfile.FromIcc(IccReferences.Bytes(file));
        for (int i = 0; i < 6 * 6 * 6; i++)
        {
            double[] input = [i / 36 / 5.0, i / 6 % 6 / 5.0, i % 6 / 5.0];
            Vector3d a = ColorPipeline.DeviceToXyz(builtIn, RenderingIntent.Perceptual, input);
            Vector3d b = ColorPipeline.DeviceToXyz(icc, RenderingIntent.Perceptual, input);
            Assert.True(Math.Abs(a.X - b.X) < 2e-4 && Math.Abs(a.Y - b.Y) < 2e-4 && Math.Abs(a.Z - b.Z) < 2e-4, $"[{string.Join(", ", input)}]: {a} against {b}");
        }
    }

    [Fact]
    public void BuiltInsRoundTrip()
    {
        ColorPipeline there = ColorPipeline.Create(ColorProfile.Srgb, ColorProfile.DisplayP3, RenderingIntent.Perceptual, true);
        ColorPipeline back = ColorPipeline.Create(ColorProfile.DisplayP3, ColorProfile.Srgb, RenderingIntent.AbsoluteColorimetric, false);
        ColorPipeline gray = ColorPipeline.Create(ColorProfile.GraySrgb, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false);
        double[] p3 = new double[3], srgb = new double[3];
        for (int i = 0; i < 1000; i++)
        {
            double[] input = [i / 100 / 9.0, i / 10 % 10 / 9.0, i % 10 / 9.0];
            there.Evaluate(input, p3);
            back.Evaluate(p3, srgb);
            for (int c = 0; c < 3; c++)
            {
                Assert.Equal(input[c], srgb[c], 9);
            }

            gray.Evaluate([input[0]], srgb);
            Assert.Equal(input[0], srgb[0], 9);   // gray sRGB is sRGB's neutral axis
            Assert.Equal(input[0], srgb[2], 9);
        }

        // Display P3 red is outside sRGB: it comes out below 0 and above 1, as a float conversion keeps it.
        there = ColorPipeline.Create(ColorProfile.DisplayP3, ColorProfile.Srgb, RenderingIntent.RelativeColorimetric, false);
        there.Evaluate([1, 0, 0], srgb);
        Assert.True(srgb[0] > 1 && srgb[1] < 0 && srgb[2] < 0);
    }

    [Fact]
    public void TargetsNeedTables()
    {
        ColorProfile inputOnly = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/rgb-mab-xyz-v4.icc"));
        Assert.Throws<UnsupportedFormatException>(() => ColorPipeline.Create(ColorProfile.Srgb, inputOnly, RenderingIntent.Perceptual, false));
        Assert.Equal(3, ColorPipeline.Create(inputOnly, ColorProfile.Srgb, RenderingIntent.Perceptual, false).OutputChannels);
        ColorPipeline cmyk = ColorPipeline.Create(ColorProfile.Srgb, ColorProfile.FromIcc(IccReferences.Bytes("synthetic/cmyk-mab-v4.icc")), RenderingIntent.Perceptual, false);
        Assert.Equal((3, 4), (cmyk.InputChannels, cmyk.OutputChannels));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void BlackPointsMatchLittleCms(string file)
    {
        JsonElement lcms = IccReferences.Profile(file).GetProperty("lcms");
        ColorProfile profile = ColorProfile.FromIcc(IccReferences.Bytes(file));
        foreach ((string name, RenderingIntent intent) in new[] { ("blackPointPerceptual", RenderingIntent.Perceptual), ("blackPointRelative", RenderingIntent.RelativeColorimetric) })
        {
            // Little CMS reports no black point when it found none, which is black 0 here.
            double[] expected = lcms.TryGetProperty(name, out JsonElement e) ? [.. e.EnumerateArray().Select(v => v.GetDouble())] : [0, 0, 0];
            Vector3d actual = ColorPipeline.BlackPoint(profile, intent);
            Assert.True(Math.Abs(actual.X - expected[0]) < 2e-6 && Math.Abs(actual.Y - expected[1]) < 2e-6 && Math.Abs(actual.Z - expected[2]) < 2e-6, $"{intent}: {actual} against [{string.Join(", ", expected)}]");
        }
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void TransformsMatchLittleCms(string file)
    {
        ColorProfile profile = ColorProfile.FromIcc(IccReferences.Bytes(file));
        ColorProfile srgb = ColorProfile.FromIcc(IccReferences.Bytes("compact/sRGB-v4.icc"));
        bool tables = !profile.IccModel!.IsMatrixShaper;
        List<string> failures = [];
        foreach ((JsonElement c, float[] expected) in IccReferences.Cases(file))
        {
            string source = c.GetProperty("source").GetString()!, target = c.GetProperty("target").GetString()!;
            RenderingIntent intent = (RenderingIntent)c.GetProperty("intent").GetInt32();
            bool bpc = c.GetProperty("bpc").GetBoolean();
            ColorProfile from = source == "srgb" ? srgb : profile;
            ColorPipeline pipeline = target == "lab" ? ColorPipeline.ToConnectionSpace(from, intent, bpc) : ColorPipeline.Create(from, target == "srgb" ? srgb : profile, intent, bpc);
            float[] inputs = IccReferences.Inputs(c.GetProperty("inputs").GetString()!);
            int ci = pipeline.InputChannels, co = pipeline.OutputChannels;
            double[] result = new double[co];
            double worst = 0;
            string worstAt = "";
            for (int i = 0; i < c.GetProperty("count").GetInt32(); i++)
            {
                double[] input = [.. inputs.AsSpan(i * ci, ci).ToArray().Select(v => (double)v)];
                pipeline.Evaluate(input, result);
                if (target == "lab")
                {
                    Vector3d lab = Cie.XyzToLab(new Vector3d(result[0], result[1], result[2]), Cie.D50);
                    (result[0], result[1], result[2]) = (lab.X, lab.Y, lab.Z);
                }

                for (int o = 0; o < co; o++)
                {
                    // Lab is compared in L*a*b* units, device values on 0..100 so the tolerance reads the same.
                    double scale = target == "lab" ? 1 : 100;
                    double error = Math.Abs(result[o] - expected[(i * co) + o]) * scale;
                    if (error > worst)
                    {
                        worst = error;
                        worstAt = $"input [{string.Join(", ", input.Select(v => v.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture)))}] channel {o}: {result[o]:0.######} against {expected[(i * co) + o]:0.######}";
                    }
                }
            }

            string line = $"{source} -> {target}, {intent}, bpc {bpc}: worst {worst:0.####} at {worstAt}";
            output.WriteLine(line);
            if (worst > (target == "lab" ? (tables ? 0.03 : 0.01) : (tables ? 0.25 : 0.01)))
            {
                failures.Add(line);
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
