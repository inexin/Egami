using System.Runtime.CompilerServices;

namespace Egami.Css.Tests;

public sealed class ColorSpaceTests
{
    private static readonly CssColorSpace[] s_spaces = Enum.GetValues<CssColorSpace>();

    private static CssColor Parse(string text)
    {
        Assert.True(CssColor.TryParse(text, out CssColor color), text);
        return color;
    }

    public static TheoryData<string, CssColorSpace, double, double, double> Spaces => new()
    {
        { "red", CssColorSpace.Srgb, 1, 0, 0 },
        { "#00ff0080", CssColorSpace.Srgb, 0, 1, 0 },
        { "rgb(0 0 255)", CssColorSpace.Srgb, 0, 0, 1 },
        { "hsl(120 100% 50%)", CssColorSpace.Srgb, 0, 1, 0 },
        { "hwb(0 0% 0%)", CssColorSpace.Srgb, 1, 0, 0 },
        { "color(srgb 1.2 -0.1 0.5)", CssColorSpace.Srgb, 1.2, -0.1, 0.5 },
        { "color(srgb-linear 0.2 0.3 0.4)", CssColorSpace.SrgbLinear, 0.2, 0.3, 0.4 },
        { "color(display-p3 1 0 0)", CssColorSpace.DisplayP3, 1, 0, 0 },
        { "color(a98-rgb 0.1 0.9 0.2)", CssColorSpace.A98Rgb, 0.1, 0.9, 0.2 },
        { "color(prophoto-rgb 50% 25% 0)", CssColorSpace.ProPhotoRgb, 0.5, 0.25, 0 },
        { "color(rec2020 0 1 0)", CssColorSpace.Rec2020, 0, 1, 0 },
        { "color(xyz 0.2 0.3 0.4)", CssColorSpace.XyzD65, 0.2, 0.3, 0.4 },
        { "color(xyz-d65 0.2 0.3 0.4)", CssColorSpace.XyzD65, 0.2, 0.3, 0.4 },
        { "color(xyz-d50 0.2 0.3 0.4)", CssColorSpace.XyzD50, 0.2, 0.3, 0.4 },
        { "lab(50% 40 -20)", CssColorSpace.Lab, 50, 40, -20 },
        { "lch(60 50 400)", CssColorSpace.Lch, 60, 50, 40 },
        { "oklab(0.5 0.1 -0.1)", CssColorSpace.Oklab, 0.5, 0.1, -0.1 },
        { "oklch(62.8% 0.258 29.2)", CssColorSpace.Oklch, 0.628, 0.258, 29.2 },
        { "oklch(0.5 none 30)", CssColorSpace.Oklch, 0.5, 0, 30 },
    };

    [Theory]
    [MemberData(nameof(Spaces))]
    public void ParsedColorsKeepTheirSpaceAndComponents(string text, CssColorSpace space, double c0, double c1, double c2)
    {
        CssColor color = Parse(text);
        Assert.Equal(space, color.Space);
        Assert.Equal(c0, color.C0, 1e-12);
        Assert.Equal(c1, color.C1, 1e-12);
        Assert.Equal(c2, color.C2, 1e-12);
    }

    // sRGB red in every space, from the CSS Color 4 equations (values to 4 decimals as the spec's examples give them).
    [Theory]
    [InlineData(CssColorSpace.Srgb, 1, 0, 0, 1e-12)]
    [InlineData(CssColorSpace.SrgbLinear, 1, 0, 0, 1e-12)]
    [InlineData(CssColorSpace.DisplayP3, 0.9175, 0.2003, 0.1386, 1e-4)]
    [InlineData(CssColorSpace.XyzD65, 0.4124, 0.2126, 0.0193, 1e-4)]
    [InlineData(CssColorSpace.XyzD50, 0.4361, 0.2225, 0.0139, 1e-4)]
    [InlineData(CssColorSpace.Lab, 54.2905, 80.8049, 69.8910, 1e-2)]
    [InlineData(CssColorSpace.Lch, 54.2905, 106.8372, 40.8526, 1e-2)]
    [InlineData(CssColorSpace.Oklab, 0.6280, 0.2249, 0.1258, 1e-4)]
    [InlineData(CssColorSpace.Oklch, 0.6280, 0.2577, 29.2339, 1e-3)]
    public void SrgbRedInEverySpace(CssColorSpace space, double c0, double c1, double c2, double tolerance)
    {
        (double a, double b, double c) = Parse("red").ToColorSpace(space);
        Assert.Equal(c0, a, tolerance);
        Assert.Equal(c1, b, tolerance);
        Assert.Equal(c2, c, tolerance);
    }

    [Theory]
    [InlineData(CssColorSpace.XyzD65, 0.9505, 1, 1.0891)]
    [InlineData(CssColorSpace.XyzD50, 0.9643, 1, 0.8251)]
    [InlineData(CssColorSpace.Lab, 100, 0, 0)]
    [InlineData(CssColorSpace.Oklab, 1, 0, 0)]
    [InlineData(CssColorSpace.DisplayP3, 1, 1, 1)]
    [InlineData(CssColorSpace.A98Rgb, 1, 1, 1)]
    [InlineData(CssColorSpace.ProPhotoRgb, 1, 1, 1)]
    [InlineData(CssColorSpace.Rec2020, 1, 1, 1)]
    public void WhiteInEverySpace(CssColorSpace space, double c0, double c1, double c2)
    {
        (double a, double b, double c) = Parse("white").ToColorSpace(space);
        Assert.Equal(c0, a, 1e-3);
        Assert.Equal(c1, b, 1e-3);
        Assert.Equal(c2, c, 1e-3);
    }

    [Fact]
    public void RoundTripsBetweenAllSpaces()
    {
        CssColor[] samples =
        [
            Parse("rebeccapurple"), Parse("#123456"), Parse("color(display-p3 0.9 0.1 0.3)"), Parse("color(rec2020 0.2 0.9 0.05)"),
            Parse("lab(30 -50 40)"), Parse("oklch(0.8 0.15 250)"), Parse("color(prophoto-rgb 0.01 0.5 0.9)"), Parse("color(srgb 1.3 -0.2 0.4)"),
        ];
        foreach (CssColor sample in samples)
        {
            (double x0, double y0, double z0) = sample.ToColorSpace(CssColorSpace.XyzD65);
            foreach (CssColorSpace space in s_spaces)
            {
                (double c0, double c1, double c2) = sample.ToColorSpace(space);
                CssColor converted = CssColor.FromColorSpace(space, c0, c1, c2);
                Assert.Equal(space, converted.Space);
                (double x, double y, double z) = converted.ToColorSpace(CssColorSpace.XyzD65);
                Assert.True(Math.Abs(x - x0) + Math.Abs(y - y0) + Math.Abs(z - z0) < 1e-9, $"{sample.ToString(true)} through {space}");

                // And back to where it started.
                (double b0, double b1, double b2) = converted.ToColorSpace(sample.Space);
                Assert.Equal(sample.C0, b0, 1e-7);
                Assert.Equal(sample.C1, b1, 1e-7);
                Assert.Equal(sample.C2, b2, 1e-7);

                // The clipped sRGB agrees however the color was given.
                Assert.Equal(sample.ToRgba8(), converted.ToRgba8());
            }
        }
    }

    [Fact]
    public void ClippedSrgbIsUnchanged()
    {
        // Q1: R, G and B clip each channel, as before; the components stay unclipped.
        CssColor p3 = Parse("color(display-p3 1 0 0)");
        Assert.Equal((1f, 0f, 0f), (p3.R, p3.G, p3.B));
        Assert.False(p3.IsInSrgbGamut);
        (double r, double g, double b) = p3.ToColorSpace(CssColorSpace.Srgb);
        Assert.True(r > 1.09 && g < -0.2 && b < -0.1);

        Assert.True(Parse("color(display-p3 0.5 0.5 0.5)").IsInSrgbGamut);
        Assert.True(Parse("white").IsInSrgbGamut);
        Assert.True(Parse("oklch(1 0 0)").IsInSrgbGamut);
        Assert.False(Parse("color(srgb 1.01 0 0)").IsInSrgbGamut);
        Assert.True(CssColor.CurrentColor.IsInSrgbGamut);
    }

    [Fact]
    public void GamutMappingLowersChroma()
    {
        CssColor p3 = Parse("color(display-p3 1 0 0)");
        CssColor mapped = p3.ToSrgbGamutMapped();
        Assert.Equal(CssColorSpace.Srgb, mapped.Space);
        Assert.True(mapped.IsInSrgbGamut);
        Assert.InRange(mapped.C0, 0, 1);
        Assert.InRange(mapped.C1, 0, 1);
        Assert.InRange(mapped.C2, 0, 1);

        // Lightness and hue hold (the clip at the end may move them a little); chroma drops.
        (double l0, double ch0, double h0) = p3.ToColorSpace(CssColorSpace.Oklch);
        (double l1, double ch1, double h1) = mapped.ToColorSpace(CssColorSpace.Oklch);
        Assert.Equal(l0, l1, 0.02);
        Assert.Equal(h0, h1, 1.0);
        Assert.True(ch1 < ch0);

        // It differs from the per-channel clip, which shifts hue.
        Assert.NotEqual(p3.ToRgba8(), mapped.ToRgba8());
    }

    [Theory]
    [InlineData("oklch(0.7 5 150)")]
    [InlineData("oklch(0.4 2 300)")]
    [InlineData("oklch(0.95 0.4 100)")]
    [InlineData("oklch(0.05 0.4 20)")]
    [InlineData("lch(50 400 10)")]
    [InlineData("color(rec2020 0 1 0)")]
    [InlineData("color(srgb 2 -1 0.5)")]
    public void VeryHighChromaMapsIntoTheGamut(string text)
    {
        CssColor color = Parse(text);
        CssColor mapped = color.ToSrgbGamutMapped();
        Assert.True(mapped.IsInSrgbGamut, text);
        Assert.InRange(mapped.C0, 0, 1);
        Assert.InRange(mapped.C1, 0, 1);
        Assert.InRange(mapped.C2, 0, 1);
        Assert.Equal(color.ToColorSpace(CssColorSpace.Oklch).C0, mapped.ToColorSpace(CssColorSpace.Oklch).C0, 0.05);
    }

    [Theory]
    [InlineData("oklch(1 0.4 30)", 1)]
    [InlineData("oklch(1.2 0.4 30)", 1)]
    [InlineData("lab(100 50 50)", 1)]
    [InlineData("oklch(0 0.4 30)", 0)]
    [InlineData("oklab(0 0.3 -0.3)", 0)]
    public void BlackAndWhite(string text, double expected)
    {
        CssColor mapped = Parse(text).ToSrgbGamutMapped();
        Assert.Equal((expected, expected, expected), (mapped.C0, mapped.C1, mapped.C2));
    }

    [Fact]
    public void GamutMappingKeepsColorsInGamutAndAlpha()
    {
        CssColor red = Parse("#ff000080");
        Assert.Equal(red, red.ToSrgbGamutMapped());
        Assert.Equal(CssColor.CurrentColor, CssColor.CurrentColor.ToSrgbGamutMapped());

        CssColor lab = Parse("lab(50 10 10 / 0.25)");
        CssColor mapped = lab.ToSrgbGamutMapped();
        Assert.Equal(CssColorSpace.Srgb, mapped.Space);
        Assert.Equal(lab.ToRgba8(), mapped.ToRgba8());
        Assert.Equal(0.25f, mapped.A);

        Assert.Equal(0.5f, Parse("color(display-p3 1 0 0 / 50%)").ToSrgbGamutMapped().A);
    }

    [Fact]
    public void EqualitySeesTheSpace()
    {
        // Q3: the space and the unclipped components are compared.
        Assert.NotEqual(Parse("color(display-p3 1 0 0)"), Parse("rgb(255 0 0)"));
        Assert.Equal(Parse("red"), Parse("rgb(255 0 0)"));
        Assert.Equal(Parse("red"), Parse("#f00"));
        Assert.Equal(Parse("red"), Parse("color(srgb 1 0 0)"));
        Assert.Equal(Parse("red").GetHashCode(), Parse("color(srgb 1 0 0)").GetHashCode());
        Assert.NotEqual(Parse("color(srgb 1.5 0 0)"), Parse("color(srgb 1 0 0)"));
        Assert.Equal(Parse("color(display-p3 1 0 0)"), CssColor.FromColorSpace(CssColorSpace.DisplayP3, 1, 0, 0));
        Assert.Equal(CssColor.FromColorSpace(CssColorSpace.Lab, 50, -0.0, 0), CssColor.FromColorSpace(CssColorSpace.Lab, 50, 0, 0));
        Assert.Equal(CssColor.FromColorSpace(CssColorSpace.Lab, 50, -0.0, 0).GetHashCode(), CssColor.FromColorSpace(CssColorSpace.Lab, 50, 0, 0).GetHashCode());
        Assert.NotEqual(CssColor.CurrentColor, Parse("black"));
    }

    [Theory]
    [InlineData("color(display-p3 1 0 0)", "color(display-p3 1 0 0)")]
    [InlineData("color(display-p3 1 0 0 / 50%)", "color(display-p3 1 0 0 / 0.5)")]
    [InlineData("color(srgb 0.5 0 1)", "color(srgb 0.5 0 1)")]
    [InlineData("color(srgb-linear 0.25 0.5 1)", "color(srgb-linear 0.25 0.5 1)")]
    [InlineData("color(a98-rgb 1 0 0)", "color(a98-rgb 1 0 0)")]
    [InlineData("color(prophoto-rgb 1 0 0)", "color(prophoto-rgb 1 0 0)")]
    [InlineData("color(rec2020 1 0 0)", "color(rec2020 1 0 0)")]
    [InlineData("color(xyz 0.2 0.3 0.4)", "color(xyz-d65 0.2 0.3 0.4)")]
    [InlineData("color(xyz-d50 0.2 0.3 0.4)", "color(xyz-d50 0.2 0.3 0.4)")]
    [InlineData("lab(50% 40 -20)", "lab(50 40 -20)")]
    [InlineData("lch(60 50 400)", "lch(60 50 40)")]
    [InlineData("oklab(0.5 0.1 -0.1)", "oklab(0.5 0.1 -0.1)")]
    [InlineData("oklch(62.8% 0.258 29.2 / 50%)", "oklch(0.628 0.258 29.2 / 0.5)")]
    [InlineData("oklch(0.3333333333 0.1 0)", "oklch(0.333333 0.1 0)")]
    [InlineData("red", "rgb(255, 0, 0)")]
    [InlineData("hsl(120 100% 25% / 0.5)", "rgba(0, 128, 0, 0.5)")]
    [InlineData("hwb(240 0% 0%)", "rgb(0, 0, 255)")]
    [InlineData("currentcolor", "currentcolor")]
    public void SerializationKeepsTheSpace(string text, string expected)
    {
        CssColor color = Parse(text);
        Assert.Equal(expected, color.ToString(keepSpace: true));
        Assert.Equal(color.ToString(), color.ToString(keepSpace: false));

        // What it writes parses back to the same color.
        Assert.Equal(color.ToRgba8(), Parse(color.ToString(true)).ToRgba8());
    }

    [Fact]
    public void ToStringStillWritesClippedSrgb()
    {
        Assert.Equal("rgb(255, 0, 0)", Parse("color(display-p3 1 0 0)").ToString());
        Assert.Equal("rgba(255, 0, 0, 0.5)", Parse("color(display-p3 1 0 0 / 0.5)").ToString());
    }

    [Fact]
    public void FromColorSpaceChecksItsArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => CssColor.FromColorSpace((CssColorSpace)12, 0, 0, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => Parse("red").ToColorSpace((CssColorSpace)(-1)));

        CssColor nan = CssColor.FromColorSpace(CssColorSpace.Oklch, 0.5, double.NaN, 10, double.NaN);
        Assert.Equal(0, nan.C1);
        Assert.Equal(0, nan.A);
        Assert.Equal(4, nan.Level);
    }

    [Fact]
    public void ConstructorKeepsUnclippedComponents()
    {
        CssColor color = new(1.5f, -0.5f, 0.25f, 2f);
        Assert.Equal(CssColorSpace.Srgb, color.Space);
        Assert.Equal((1.5, -0.5, 0.25), (color.C0, color.C1, color.C2));
        Assert.Equal((1f, 0f, 0.25f, 1f), (color.R, color.G, color.B, color.A));
        Assert.Equal(3, color.Level);
        Assert.Equal(CssColorSpace.Srgb, default(CssColor).Space);
    }

    [Fact]
    public void StructSize()
    {
        Assert.Equal(48, Unsafe.SizeOf<CssColor>());
    }
}
