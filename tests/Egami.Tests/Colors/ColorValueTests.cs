using Egami.Colors;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 11: color values against published values, round trips, and the row conversions.</summary>
public class ColorValueTests
{
    [Theory]
    [InlineData(0.950, 1.000, 1.089, 1.000, 0.000, 0.000)]
    [InlineData(1.000, 0.000, 0.000, 0.450, 1.236, -0.019)]
    [InlineData(0.000, 1.000, 0.000, 0.922, -0.671, 0.263)]
    [InlineData(0.000, 0.000, 1.000, 0.153, -1.415, -0.449)]
    public void OklabMatchesOttossonsTable(double x, double y, double z, double l, double a, double b)
    {
        OklabColor lab = OklabColor.FromXyz(new XyzColor((float)x, (float)y, (float)z));
        Assert.Equal(l, lab.L, 2.5e-3);
        Assert.Equal(a, lab.A, 2.5e-3);
        Assert.Equal(b, lab.B, 2.5e-3);
    }

    [Fact]
    public void CssColor4SampleValues()
    {
        // CSS Color 4's conversions of sRGB red and lime, and of white.
        LabColor red = LabColor.From(new Rgba8(255, 0, 0, 255));
        Assert.True(red.IsCloseTo(new LabColor(54.2905f, 80.8049f, 69.8910f), 2e-3f), $"{red}");
        LabColor lime = LabColor.From(new Rgba8(0, 255, 0, 255));
        Assert.True(lime.IsCloseTo(new LabColor(87.8185f, -79.2711f, 80.9946f), 2e-3f), $"{lime}");
        OklchColor oklch = OklchColor.From(new Rgba8(255, 0, 0, 255));
        Assert.True(oklch.IsCloseTo(new OklchColor(0.627955f, 0.257683f, 29.2339f), 2e-3f), $"{oklch}");
        Assert.True(LabColor.From(new Rgba8(255, 255, 255, 255)).IsCloseTo(new LabColor(100, 0, 0), 1e-3f));
        Assert.True(OklabColor.From(new Rgba8(255, 255, 255, 255)).IsCloseTo(new OklabColor(1, 0, 0), 1e-4f));
        XyzColor white = XyzColor.From(new Rgba8(255, 255, 255, 255));
        Assert.True(white.IsCloseTo(new XyzColor(0.95045593f, 1, 1.0890577f), 1e-5f), $"{white}");
        Assert.True(white.ToD50().IsCloseTo(new XyzColor(0.9642957f, 1, 0.8251046f), 1e-4f), $"{white.ToD50()}");
    }

    [Theory]
    [InlineData(50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425)]
    [InlineData(50.0000, 3.1571, -77.2803, 50.0000, 0.0000, -82.7485, 2.8615)]
    [InlineData(50.0000, 2.8361, -74.0200, 50.0000, 0.0000, -82.7485, 3.4412)]
    [InlineData(50.0000, -1.3802, -84.2814, 50.0000, 0.0000, -82.7485, 1.0000)]
    [InlineData(50.0000, 0.0000, 0.0000, 50.0000, -1.0000, 2.0000, 2.3669)]
    [InlineData(50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0009, 7.1792)]
    [InlineData(60.2574, -34.0099, 36.2677, 60.4626, -34.1751, 39.4387, 1.2644)]
    [InlineData(63.0109, -31.0961, -5.8663, 62.8187, -29.7946, -4.0864, 1.2630)]
    [InlineData(35.0831, -44.1164, 3.7933, 35.0232, -40.0716, 1.5901, 1.8645)]
    [InlineData(22.7233, 20.0904, -46.6940, 23.0331, 14.9730, -42.5619, 2.0373)]
    [InlineData(2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082)]
    public void DeltaE2000MatchesSharmasData(double l1, double a1, double b1, double l2, double a2, double b2, double expected)
    {
        LabColor x = new((float)l1, (float)a1, (float)b1), y = new((float)l2, (float)a2, (float)b2);
        Assert.Equal(expected, x.DeltaE2000(y), 3e-4);
        Assert.Equal(expected, y.DeltaE2000(x), 3e-4);
        Assert.Equal(0, x.DeltaE2000(x), 9);
    }

    [Fact]
    public void RgbModels()
    {
        Rgba8 red = new(255, 0, 0, 255), cyan = new(0, 255, 255, 255), gray = new(128, 128, 128, 255);
        Assert.Equal(new HslColor(0, 1, 0.5f), HslColor.From(red));
        Assert.Equal(new HslColor(180, 1, 0.5f), HslColor.From(cyan));
        Assert.Equal(new HsvColor(0, 0, 128 / 255f), HsvColor.From(gray));
        Assert.Equal(new HsvColor(180, 1, 1), HsvColor.From(cyan));
        Assert.True(YCbCrColor.From(red).IsCloseTo(new YCbCrColor(0.299f, 0.331264f, 1), 1e-5f));
        Assert.True(YCbCrColor.From(new Rgba8(255, 255, 255, 255), YuvMatrix.Bt709, YuvRange.Limited).IsCloseTo(new YCbCrColor(235 / 255f, 128 / 255f, 128 / 255f), 1e-5f));
        Assert.Equal(red, YCbCrColor.From(red, YuvMatrix.Bt2020Ncl).ToRgba8(YuvMatrix.Bt2020Ncl));
        Assert.Equal(new YCbCrColor(1, 0, 0.5f), YCbCrColor.From(new Rgba8(127, 255, 0, 255), YuvMatrix.Identity) with { Cr = 0.5f });
        Assert.Equal(new CmykColor(0, 1, 1, 0), CmykColor.From(red));
        Assert.Equal(new CmykColor(0, 0, 0, 1), CmykColor.From(new Rgba8(0, 0, 0, 255)));
    }

    public static TheoryData<string> Models() => ["Hsl", "Hsv", "Xyz", "Lab", "Lch", "Oklab", "Oklch", "YCbCr", "Cmyk"];

    /// <summary>Every model gives every 8-bit color back, and the row conversion matches the single one.</summary>
    [Theory]
    [MemberData(nameof(Models))]
    public void EveryModelRoundTripsAndRowsAgree(string model)
    {
        Random random = new(model.Length);
        Rgba8[] pixels = new Rgba8[5000];
        random.NextBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(pixels.AsSpan()));
        for (int i = 0; i < pixels.Length; i++)
        {
            pixels[i] = pixels[i] with { A = 255 };
        }

        switch (model)
        {
            case "Hsl": Check<HslColor>(pixels); break;
            case "Hsv": Check<HsvColor>(pixels); break;
            case "Xyz": Check<XyzColor>(pixels); break;
            case "Lab": Check<LabColor>(pixels); break;
            case "Lch": Check<LchColor>(pixels); break;
            case "Oklab": Check<OklabColor>(pixels); break;
            case "Oklch": Check<OklchColor>(pixels); break;
            case "YCbCr": Check<YCbCrColor>(pixels); break;
            default: Check<CmykColor>(pixels); break;
        }
    }

    [Fact]
    public void ConvertGoesThroughXyz()
    {
        Rgba8 orange = new(240, 128, 32, 255);
        HslColor hsl = HslColor.From(orange);
        Assert.True(ColorConvert.Convert<HslColor, LabColor>(hsl).IsCloseTo(LabColor.From(orange), 1e-3f));
        Assert.True(ColorConvert.Convert<LabColor, HsvColor>(LabColor.From(orange)).IsCloseTo(HsvColor.From(orange), 1e-4f));
        Assert.Equal(hsl, ColorConvert.Convert<HslColor, HslColor>(hsl));
        Assert.True(LchColor.FromLab(LabColor.From(orange)).ToLab().IsCloseTo(LabColor.From(orange), 1e-4f));
    }

    [Fact]
    public void EncodingsAreOneArgumentAway()
    {
        // Display P3 red is outside sRGB: its Lab is more saturated than sRGB red's, and through RgbaF32 the trip back is exact.
        Rgba8[] p3Red = [new(255, 0, 0, 255)];
        LabColor[] lab = new LabColor[1];
        ColorConvert.FromPixels(p3Red, lab, ColorProfile.DisplayP3);
        Assert.True(lab[0].A > LabColor.From(new Rgba8(255, 0, 0, 255)).A + 5);
        RgbaF32[] back = new RgbaF32[1];
        ColorConvert.ToPixels(lab, back, ColorProfile.DisplayP3);
        Assert.True(Math.Abs(back[0].R - 1) < 1e-4 && Math.Abs(back[0].G) < 1e-4 && Math.Abs(back[0].B) < 1e-4, $"{back[0]}");

        ColorProfile cmyk = ColorProfile.FromIcc(IccReferences.Bytes("synthetic/cmyk-mab-v4.icc"));
        CmykColor inks = CmykColor.From(new Rgba8(200, 60, 30, 255), cmyk);
        Rgba8 printed = inks.ToRgba8(cmyk);
        Assert.True(Math.Abs(printed.R - 200) < 12 && Math.Abs(printed.G - 60) < 12 && Math.Abs(printed.B - 30) < 12, $"{inks} gives {printed}");
        Assert.Throws<ArgumentException>(() => CmykColor.From(new Rgba8(1, 2, 3, 255), ColorProfile.Srgb));
    }

    private static void Check<TColor>(Rgba8[] pixels)
        where TColor : struct, IColorValue<TColor>
    {
        TColor[] row = new TColor[pixels.Length];
        ColorConvert.FromPixels<Rgba8, TColor>(pixels, row);
        Rgba8[] back = new Rgba8[pixels.Length];
        ColorConvert.ToPixels<TColor, Rgba8>(row, back);
        RgbaF32[] wide = new RgbaF32[pixels.Length];
        ColorConvert.ToPixels<TColor, RgbaF32>(row, wide);
        for (int i = 0; i < pixels.Length; i++)
        {
            TColor single = TColor.From(pixels[i]);
            Assert.True(single.IsCloseTo(row[i], 2e-4f) || Hueless(single, row[i]), $"{pixels[i]}: row {row[i]}, single {single}");
            Assert.Equal(pixels[i], single.ToRgba8());
            Assert.Equal(pixels[i], back[i]);
            Assert.Equal(pixels[i], wide[i].ToRgba8());
            Assert.True(TColor.FromXyz(single.ToXyz()).IsCloseTo(single, 2e-3f) || Hueless(single, TColor.FromXyz(single.ToXyz())));
        }

        // Grays have no hue, so float noise can put it anywhere; their other components must still agree.
        static bool Hueless(TColor a, TColor b) => a switch
        {
            LchColor x when b is LchColor y => x.C < 1e-2 && Math.Abs(x.L - y.L) < 1e-3 && Math.Abs(x.C - y.C) < 1e-2,
            OklchColor x when b is OklchColor y => x.C < 1e-4 && Math.Abs(x.L - y.L) < 1e-4 && Math.Abs(x.C - y.C) < 1e-4,
            _ => false,
        };
    }
}
