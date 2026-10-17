using Egami.Colors;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>
/// Phase 2.6.5 task 4: n-colour ICC profiles, on the synthetic seven-ink (v4, lutAtoB) and duotone (v2, lut16) profiles of
/// tests/images/icc/ncolor. Little CMS's values for them are checked in egami-compare.
/// </summary>
public class NColorTests(ITestOutputHelper output)
{
    private static readonly Lazy<ColorProfile> Seven = new(() => Load("7clr-mab-v4"));
    private static readonly Lazy<ColorProfile> Duo = new(() => Load("2clr-lut16-v2"));

    [Fact]
    public void NColourProfilesReadWithTheirInks()
    {
        ColorProfile seven = Seven.Value;
        Assert.Equal(ColorModel.NColor, seven.Model);
        Assert.Equal(7, seven.ChannelCount);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black", "Orange", "Green", "Violet"], seven.ColorantNames);
        Assert.Equal(ColorModel.NColor, Duo.Value.Model);
        Assert.Equal(2, Duo.Value.ChannelCount);
        Assert.Equal(["Black", "Warm Red"], Duo.Value.ColorantNames);
        Assert.Equal(["Cyan", "Magenta", "Yellow", "Black"], ColorProfile.Fogra39.ColorantNames);   // FOGRA39 has a clrt tag too
        Assert.Equal(4, ColorProfile.Fogra39.ChannelCount);
        Assert.Equal(3, ColorProfile.Srgb.ChannelCount);
    }

    [Fact]
    public void AnNColourProfileGivesItsInksWithTheirColours()
    {
        InkSet inks = InkSet.FromProfile(Seven.Value);
        Assert.Equal(7, inks.ProcessCount);
        Assert.All(inks, c => Assert.Equal(ColorantKind.Process, c.Kind));
        Assert.True(inks[0].IsCmyk);
        Assert.False(inks[4].IsCmyk);

        // The colorant table's colours, which the profile writer took from its model: orange is light, warm and yellowish.
        LabColor orange = inks[4].Appearance!.Value.ToLab();
        Assert.True(orange.L is > 70 and < 78 && orange.A > 20 && orange.B > 45, orange.ToString());
        Assert.Equal(InkAppearanceKind.Lab, inks[0].Appearance!.Value.Kind);

        // An ink set from it takes spots like any other.
        Assert.Equal(8, inks.WithSpot("Gold", null).Count);
    }

    [Fact]
    public void TablesOfTwoAndSevenInputsInterpolate()
    {
        // Paper is the white point: relative colorimetric, no ink, is D50 white.
        foreach (ColorProfile profile in (ColorProfile[])[Seven.Value, Duo.Value])
        {
            ColorPipeline toPcs = ColorPipeline.ToConnectionSpace(profile, RenderingIntent.RelativeColorimetric, false);
            double[] xyz = new double[3];
            toPcs.Evaluate(new double[profile.ChannelCount], xyz);
            Assert.True(Math.Abs(xyz[1] - 1) < 1e-3, $"{profile.Name}: paper Y {xyz[1]}");
        }

        // A darker ink gives less light: full black is darker than half black, which is darker than paper.
        double[] y = [.. new[] { 0, 0.5, 1 }.Select(k => Luminance(Duo.Value, [k, 0]))];
        Assert.True(y[0] > y[1] && y[1] > y[2], string.Join(", ", y));
    }

    [Fact]
    public void ColoursTheTablesSeparateRoundTrip()
    {
        // As tools/make-ink-profiles.py checks with Little CMS: random process colours to XYZ and back to inks give colours the
        // B2A table reaches; those go to XYZ, to inks and to XYZ again. Little CMS finds worst cases of 1.45 (seven inks) and 2.6
        // (the duotone, whose colours lie on a surface the B2A grid straddles), means of 0.41 and 0.53; Egami must do as well.
        foreach ((ColorProfile profile, double worstAllowed, double meanAllowed) in new[] { (Seven.Value, 1.6, 0.5), (Duo.Value, 3.0, 0.6) })
        {
            int n = profile.ChannelCount;
            ColorPipeline toPcs = ColorPipeline.ToConnectionSpace(profile, RenderingIntent.RelativeColorimetric, false);
            ColorPipeline fromPcs = ColorPipeline.FromConnectionSpace(profile, RenderingIntent.RelativeColorimetric);
            Random random = new(2605);
            double worst = 0, sum = 0;
            const int Samples = 1000;
            double[] inks = new double[n], back = new double[n], xyz = new double[3], again = new double[3];
            for (int s = 0; s < Samples; s++)
            {
                for (int i = 0; i < n; i++)
                {
                    inks[i] = i < 3 || n == 2 ? 0.05 + (0.9 * random.NextDouble()) : i == 3 ? 0.6 * random.NextDouble() : 0;
                }

                toPcs.Evaluate(inks, xyz);
                fromPcs.Evaluate(xyz, inks);          // a colour the table separates
                toPcs.Evaluate(inks, xyz);
                fromPcs.Evaluate(xyz, back);
                toPcs.Evaluate(back, again);
                double de = Lab(xyz).DeltaE2000(Lab(again));
                worst = Math.Max(worst, de);
                sum += de;
            }

            output.WriteLine($"{profile.Name}: round trip worst {worst:F3}, mean {sum / Samples:F3} dE2000");
            Assert.True(worst <= worstAllowed && sum / Samples <= meanAllowed, $"{profile.Name}: worst {worst:F3}, mean {sum / Samples:F3}");
        }
    }

    [Fact]
    public void FloatSpansConvertAnyChannelCount()
    {
        ColorTransform toScreen = ColorTransform.Create(Seven.Value, ColorProfile.Srgb);
        Assert.Equal((7, 3), (toScreen.InputChannels, toScreen.OutputChannels));
        float[] inks = [0, 0, 0, 0, 0, 0, 0, /* full orange */ 0, 0, 0, 0, 1, 0, 0];
        float[] rgb = new float[6];
        toScreen.Apply(inks, rgb);
        Assert.All(rgb[..3], v => Assert.InRange(v, 0.99f, 1.01f));       // paper is white
        Assert.True(rgb[3] > 0.8f && rgb[4] is > 0.4f and < 0.8f && rgb[5] < 0.4f, string.Join(", ", rgb));

        // sRGB into seven inks, and back: the direct path and a sampled grid agree to within a ΔE of a few tenths.
        ColorTransform toInks = ColorTransform.Create(ColorProfile.Srgb, Seven.Value);
        float[] colors = [0.8f, 0.4f, 0.2f, 0.3f, 0.6f, 0.3f];
        float[] separated = new float[14], seen = new float[6];
        toInks.Apply(colors, separated);
        toScreen.Apply(separated, seen);
        for (int i = 0; i < 2; i++)
        {
            double de = LabColor.From(new RgbaF32(colors[3 * i], colors[(3 * i) + 1], colors[(3 * i) + 2], 1))
                .DeltaE2000(LabColor.From(new RgbaF32(seen[3 * i], seen[(3 * i) + 1], seen[(3 * i) + 2], 1)));
            Assert.True(de < 2, $"color {i}: {de:F2}");
        }

        Assert.Throws<ArgumentException>(() => toScreen.Apply(new float[8], new float[3]));
        Assert.Throws<ArgumentException>(() => toScreen.Apply(new float[14], new float[3]));
        Assert.Throws<InvalidOperationException>(() => toScreen.Apply<Cmyk8, Rgba8>(new Cmyk8[1], new Rgba8[1]));
    }

    [Fact]
    public void AnNColourProfileInAFileIsNotThePixelsProfile()
    {
        Egami.Metadata.RasterMetadata metadata = new() { IccProfile = File.ReadAllBytes(Path("7clr-mab-v4")) };
        Assert.Same(ColorProfile.Srgb, metadata.GetColorProfile());
    }

    private static double Luminance(ColorProfile profile, double[] inks)
    {
        double[] xyz = new double[3];
        ColorPipeline.ToConnectionSpace(profile, RenderingIntent.RelativeColorimetric, false).Evaluate(inks, xyz);
        return xyz[1];
    }

    private static LabColor Lab(double[] xyz)
    {
        Vector3d lab = Cie.XyzToLab(new Vector3d(xyz[0], xyz[1], xyz[2]), Cie.D50);
        return new LabColor((float)lab.X, (float)lab.Y, (float)lab.Z);
    }

    private static string Path(string name) => System.IO.Path.Combine(AppContext.BaseDirectory, "images", "icc", "ncolor", name + ".icc");

    private static ColorProfile Load(string name) => ColorProfile.FromIcc(File.ReadAllBytes(Path(name)));
}
