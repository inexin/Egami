using Egami.Colors;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 2: primaries, matrices, transfer functions, code points and built-in profiles against published values.</summary>
public class ColorCoreTests
{
    // CSS Color 4's exact rational matrices (linear RGB to XYZ), from the chromaticities and white points used here; sRGB's last
    // entry is IEC 61966-2-1's 0.9505 to eight places.
    public static TheoryData<string, double[]> PublishedMatrices() => new()
    {
        { "sRGB", [506752.0 / 1228815, 87881.0 / 245763, 12673.0 / 70218, 87098.0 / 409605, 175762.0 / 245763, 12673.0 / 175545, 7918.0 / 409605, 87881.0 / 737289, 0.95053215] },
        { "Display P3", [608311.0 / 1250200, 189793.0 / 714400, 198249.0 / 1000160, 35783.0 / 156275, 247089.0 / 357200, 198249.0 / 2500400, 0, 32229.0 / 714400, 5220557.0 / 5000800] },
        { "Rec. 2020", [63426534.0 / 99577255, 20160776.0 / 139408157, 47086771.0 / 278816314, 26158966.0 / 99577255, 472592308.0 / 697040785, 8267143.0 / 139408157, 0, 19567812.0 / 697040785, 295819943.0 / 278816314] },
        { "Adobe RGB (1998)", [573536.0 / 994567, 263643.0 / 1420810, 187206.0 / 994567, 591459.0 / 1989134, 6239551.0 / 9945670, 374412.0 / 4972835, 53769.0 / 1989134, 351524.0 / 4972835, 4929758.0 / 4972835] },
        { "ProPhoto RGB", [0.79776664490064230, 0.13518129740053308, 0.03134773412839220, 0.28807482881940130, 0.71183523424187300, 0.00008993693872564, 0, 0, 0.82510460251046020] },
    };

    [Theory]
    [MemberData(nameof(PublishedMatrices))]
    public void MatricesMatchCssColor4(string name, double[] expected)
    {
        ColorProfile profile = new[] { ColorProfile.Srgb, ColorProfile.DisplayP3, ColorProfile.Rec2020, ColorProfile.AdobeRgb, ColorProfile.ProPhotoRgb }.Single(p => p.Name == name);
        Matrix3d m = profile.ToXyz();
        for (int i = 0; i < 9; i++)
        {
            Assert.True(Math.Abs(m[i / 3, i % 3] - expected[i]) < 1e-6, $"{name} [{i / 3},{i % 3}]: {m[i / 3, i % 3]} against {expected[i]}");
        }
    }

    [Fact]
    public void BradfordMatchesCssColor4()
    {
        double[] expected = [1.0479298208405488, 0.022946793341019088, -0.05019222954313557, 0.029627815688159344, 0.990434484573249, -0.01707382502938514, -0.009243058152591178, 0.015055144896577895, 0.7518742899580008];
        Matrix3d m = Matrix3d.Bradford(Chromaticity.D65.ToXyz(), Chromaticity.D50.ToXyz());
        for (int i = 0; i < 9; i++)
        {
            Assert.True(Math.Abs(m[i / 3, i % 3] - expected[i]) < 1e-6, $"[{i / 3},{i % 3}]: {m[i / 3, i % 3]} against {expected[i]}");
        }
    }

    [Fact]
    public void SrgbIsExactlyTheOneZeroCurve()
    {
        for (int v = 0; v <= 1000; v++)
        {
            double x = v / 1000.0;
            Assert.Equal(Egami.Pixels.Srgb.ToLinear(x), TransferFunction.Srgb.ToLinear(x));
            Assert.Equal(Egami.Pixels.Srgb.FromLinear(x), TransferFunction.Srgb.FromLinear(x));
        }

        Assert.Equal(-TransferFunction.Srgb.ToLinear(0.5), TransferFunction.Srgb.ToLinear(-0.5));
    }

    [Theory]
    [InlineData(100, 0.5080784)]
    [InlineData(203, 0.5806888)]
    [InlineData(1000, 0.7518271)]
    [InlineData(10000, 1.0)]
    public void PqMatchesSt2084(double nits, double signal)
    {
        Assert.Equal(signal, TransferFunction.Pq.FromLinear(nits / 203), 6);
        Assert.Equal(nits / 203, TransferFunction.Pq.ToLinear(signal), 3);
    }

    [Fact]
    public void PqAndHlgRoundTripEverySixteenBitValue()
    {
        foreach (TransferFunction curve in new[] { TransferFunction.Pq, TransferFunction.Hlg, TransferFunction.Srgb, TransferFunction.Rec709 })
        {
            for (int v = 0; v < 65536; v++)
            {
                double back = curve.FromLinear(curve.ToLinear(v / 65535.0));
                Assert.Equal(v, (int)Math.Round(back * 65535));
            }
        }
    }

    [Fact]
    public void HlgReferenceWhiteIsOne()
    {
        // BT.2408: a 75% HLG signal on a 1,000 cd/m² display is reference white, 203 cd/m².
        Assert.Equal(1.0, TransferFunction.Hlg.ToLinear(0.75), 2);
        Assert.Equal(1000 / 203.0, TransferFunction.Hlg.ToLinear(1.0), 5);   // BT.2100's eight-digit constants put 1.0 at 1.00000003
    }

    public static TheoryData<string> Curves() => ["Srgb", "Rec709", "Gamma", "Parametric", "Pq", "Hlg", "Table"];

    [Theory]
    [MemberData(nameof(Curves))]
    public void RowsMatchTheDoubleCurves(string name)
    {
        TransferFunction curve = Curve(name);
        bool hdr = curve.IsHdr;
        Random random = new(name.Length);
        float[] encoded = new float[1003];
        for (int i = 0; i < encoded.Length; i++)
        {
            encoded[i] = (float)(hdr ? random.NextDouble() : (random.NextDouble() * 1.4) - 0.2);
        }

        float[] linear = new float[encoded.Length];
        curve.ToLinear(encoded, linear);
        float[] back = new float[encoded.Length];
        curve.FromLinear(linear, back);
        for (int i = 0; i < encoded.Length; i++)
        {
            double expected = curve.ToLinear(encoded[i]);
            Assert.True(Math.Abs(linear[i] - expected) <= 1e-5 * Math.Max(1, Math.Abs(expected)), $"{name} decode {encoded[i]}: {linear[i]} against {expected}");
            double expectedEncode = curve.FromLinear(linear[i]);
            Assert.True(Math.Abs(back[i] - expectedEncode) <= 1e-5 * Math.Max(1, Math.Abs(expectedEncode)), $"{name} encode {linear[i]}: {back[i]} against {expectedEncode}");
        }
    }

    [Fact]
    public void RowsAreTheSameAtAnyLengthAndOffset()
    {
        float[] values = [.. Enumerable.Range(0, 37).Select(i => i / 36f)];
        float[] whole = new float[values.Length];
        TransferFunction.Srgb.ToLinear(values, whole);
        for (int start = 0; start < values.Length; start++)
        {
            float[] part = new float[values.Length - start];
            TransferFunction.Srgb.ToLinear(values.AsSpan(start), part);
            Assert.Equal(whole[start..], part);
        }
    }

    [Fact]
    public void CodePointsNameTheBuiltIns()
    {
        Assert.Same(ColorProfile.Srgb, ColorProfile.FromCodePoints(ColorCodePoints.Srgb));
        Assert.Same(ColorProfile.Rec2100Pq, ColorProfile.FromCodePoints(ColorCodePoints.Rec2100Pq));
        Assert.Same(ColorProfile.Rec2100Hlg, ColorProfile.FromCodePoints(ColorCodePoints.Rec2100Hlg));
        Assert.Same(ColorProfile.DisplayP3, ColorProfile.FromCodePoints(new ColorCodePoints(12, 13, 0, true)));
        Assert.Null(ColorProfile.FromCodePoints(new ColorCodePoints(2, 13, 0, true)));
        Assert.Null(ColorProfile.FromCodePoints(new ColorCodePoints(1, 13, 1, false)));
        foreach (ColorProfile p in new[] { ColorProfile.Srgb, ColorProfile.LinearSrgb, ColorProfile.DisplayP3, ColorProfile.Rec2020, ColorProfile.Rec2100Pq, ColorProfile.Rec2100Hlg })
        {
            Assert.Same(p, ColorProfile.FromCodePoints(p.ToCodePoints()!.Value));
        }

        Assert.Null(ColorProfile.AdobeRgb.ToCodePoints());
    }

    [Fact]
    public void EquivalenceIgnoresHowACurveIsWritten()
    {
        ColorProfile parametricSrgb = ColorProfile.Create(ColorPrimaries.Rec709, TransferFunction.Parametric(2.4, 1 / 1.055, 0.055 / 1.055, 1 / 12.92, 0.04045, 0, 0));
        Assert.True(parametricSrgb.IsEquivalentTo(ColorProfile.Srgb));
        Assert.False(ColorProfile.DisplayP3.IsEquivalentTo(ColorProfile.Srgb));
        Assert.False(ColorProfile.LinearSrgb.IsEquivalentTo(ColorProfile.Srgb));
        Assert.True(ColorProfile.Rec2100Pq.IsHdr);
        Assert.Throws<ArgumentException>(() => ColorProfile.Create(new ColorPrimaries(new(0.3, 0.3), new(0.3, 0.3), new(0.3, 0.3), Chromaticity.D65), TransferFunction.Srgb));
        Assert.Throws<ArgumentOutOfRangeException>(() => TransferFunction.Gamma(0));
        Assert.Throws<ArgumentException>(() => TransferFunction.Table([0.5f, 0.2f]));
    }

    private static TransferFunction Curve(string name) => name switch
    {
        "Srgb" => TransferFunction.Srgb,
        "Rec709" => TransferFunction.Rec709,
        "Gamma" => TransferFunction.Gamma(2.2),
        "Parametric" => ColorProfile.ProPhotoRgb.Transfer!,
        "Pq" => TransferFunction.Pq,
        "Hlg" => TransferFunction.Hlg,
        _ => TransferFunction.Table([.. Enumerable.Range(0, 1024).Select(i => (float)Math.Pow(i / 1023.0, 2.2))]),
    };
}
