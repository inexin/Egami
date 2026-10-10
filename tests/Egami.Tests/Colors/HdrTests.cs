using Egami.Colors;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Colors;

/// <summary>Phase 2.1.2 task 9: tone mapping and HDR conversions.</summary>
public class HdrTests
{
    // BT.2390's EETF written out again here from the recommendation, in nits: the gate's double-precision reference.
    private static double Eetf(double nits, double sourcePeak, double targetPeak)
    {
        static double Pq(double n) => Math.Pow((0.8359375 + (18.8515625 * Math.Pow(n / 10000, 0.1593017578125))) / (1 + (18.6875 * Math.Pow(n / 10000, 0.1593017578125))), 78.84375);

        static double PqInverse(double e)
        {
            double p = Math.Pow(e, 1 / 78.84375);
            return 10000 * Math.Pow(Math.Max(p - 0.8359375, 0) / (18.8515625 - (18.6875 * p)), 1 / 0.1593017578125);
        }

        double e1 = Pq(Math.Min(nits, sourcePeak)) / Pq(sourcePeak);
        double maxLum = Pq(targetPeak) / Pq(sourcePeak);
        double ks = (1.5 * maxLum) - 0.5;
        if (e1 >= ks)
        {
            double t = (e1 - ks) / (1 - ks);
            e1 = (((2 * t * t * t) - (3 * t * t) + 1) * ks) + (((t * t * t) - (2 * t * t) + t) * (1 - ks)) + (((-2 * t * t * t) + (3 * t * t)) * maxLum);
        }

        return PqInverse(e1 * Pq(sourcePeak));
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(4000)]
    [InlineData(10000)]
    [InlineData(400)]
    public void Bt2390MatchesTheRecommendation(double peak)
    {
        double previous = -1;
        ToneTable table = new(ToneMapping.Bt2390, peak / 203);
        for (int i = 0; i <= 2000; i++)
        {
            double nits = peak * Math.Pow(i / 2000.0, 2);
            double expected = Eetf(nits, peak, 203) / 203;
            double ours = ToneCurves.Map(ToneMapping.Bt2390, nits / 203, peak / 203);
            Assert.Equal(expected, ours, 9);
            Assert.True(ours >= previous, $"{nits} nits: {ours} after {previous}");
            previous = ours;

            // The compiled table is within 1e-4 of it, as the gate asks.
            float m = (float)(nits / 203);
            Assert.True(Math.Abs((table.Ratio(m) * m) - expected) <= 1e-4 * Math.Max(1, expected), $"{nits} nits: {table.Ratio(m) * m} against {expected}");
        }

        Assert.Equal(1, ToneCurves.Map(ToneMapping.Bt2390, peak / 203, peak / 203), 9);
        Assert.Equal(1, ToneCurves.Map(ToneMapping.Bt2390, 2 * peak / 203, peak / 203), 9);
        Assert.Equal(20 / 203.0, ToneCurves.Map(ToneMapping.Bt2390, 20 / 203.0, peak / 203), 12);   // dark tones untouched
    }

    [Fact]
    public void OtherCurves()
    {
        Assert.Equal(1, ToneCurves.Map(ToneMapping.Reinhard, 4.0, 4.0), 12);
        Assert.Equal(0.5, ToneCurves.Map(ToneMapping.Clip, 0.5, 4.0));
        Assert.Equal(1, ToneCurves.Map(ToneMapping.Clip, 3, 4.0));
        Assert.Equal(0.7, ToneCurves.Map(ToneMapping.Bt2390, 0.7, 1), 12);   // a source no brighter than white needs nothing
        double previous = 0;
        for (int i = 1; i <= 400; i++)
        {
            double v = ToneCurves.Map(ToneMapping.Reinhard, i / 100.0, 4.0);
            Assert.True(v > previous);
            previous = v;
        }
    }

    [Fact]
    public void PqToSrgbToneMapsByDefault()
    {
        // PQ signals for 50, 203, 1000 and 4000 cd/m², as gray, into sRGB floats.
        float[] signals = [.. new[] { 50.0, 203, 1000, 4000 }.Select(n => (float)TransferFunction.Pq.FromLinear(n / 203))];
        RgbaF32[] pq = [.. signals.Select(s => new RgbaF32(s, s, s, 1))];
        RgbaF32[] mapped = new RgbaF32[pq.Length], clipped = new RgbaF32[pq.Length];
        ColorTransform.Create(ColorProfile.Rec2100Pq, ColorProfile.Srgb).Apply<RgbaF32, RgbaF32>(pq, mapped);
        ColorTransform.Create(ColorProfile.Rec2100Pq, ColorProfile.Srgb, new ColorTransformOptions { ToneMapping = ToneMapping.Clip }).Apply<RgbaF32, RgbaF32>(pq, clipped);

        // Clip keeps the HDR values above 1; BT.2390 (peak 1000 by default) puts 1000 cd/m² at white and keeps 50 cd/m², below
        // its knee.
        Assert.Equal(1f, clipped[1].R, 4);
        Assert.True(clipped[2].R > 1.5f);
        Assert.Equal(TransferFunction.Srgb.FromLinear(50 / 203.0), mapped[0].R, 4);
        Assert.Equal(1f, mapped[2].R, 4);
        Assert.Equal(1f, mapped[3].R, 4);
        Assert.True(mapped[1].R < 1 && mapped[1].R > mapped[0].R);
        Assert.Equal(mapped[1].R, mapped[1].B, 5);   // gray stays gray

        // The peak from the options moves the curve; a brighter source darkens its mid-tones.
        RgbaF32[] bright = new RgbaF32[pq.Length];
        ColorTransform.Create(ColorProfile.Rec2100Pq, ColorProfile.Srgb, new ColorTransformOptions { SourcePeakNits = 4000 }).Apply<RgbaF32, RgbaF32>(pq, bright);
        Assert.True(bright[2].R < mapped[2].R);
        Assert.Equal(1f, bright[3].R, 4);

        // With reference white at 100 cd/m², the 50 cd/m² signal is half of white.
        RgbaF32[] white100 = new RgbaF32[1];
        ColorTransform.Create(ColorProfile.Rec2100Pq, ColorProfile.LinearSrgb, new ColorTransformOptions { ReferenceWhiteNits = 100, ToneMapping = ToneMapping.Clip }).Apply<RgbaF32, RgbaF32>(pq.AsSpan(0, 1), white100);
        Assert.Equal(0.5f, white100[0].R, 4);
    }

    [Fact]
    public void ToneMappedColorsStayInGamutWithTheirHue()
    {
        // Saturated Rec. 2020 green at 1000 cd/m² has no sRGB match: it comes out in range, still green.
        float s = (float)TransferFunction.Pq.FromLinear(1000 / 203.0);
        float half = (float)TransferFunction.Pq.FromLinear(500 / 203.0);
        RgbaF32[] green = [new RgbaF32(0, s, 0, 1), new RgbaF32(s, half, 0, 1)];
        ColorTransform.Create(ColorProfile.Rec2100Pq, ColorProfile.Srgb).Apply<RgbaF32>(green);
        foreach (RgbaF32 p in green)
        {
            Assert.InRange(p.R, 0f, 1f);
            Assert.InRange(p.G, 0f, 1f);
            Assert.InRange(p.B, 0f, 1f);
        }

        Assert.True(green[0].G > green[0].R && green[0].G > green[0].B);
        Assert.True(green[1].R > green[1].G && green[1].G > green[1].B);
    }

    [Fact]
    public void ConvertColorTakesThePeakFromTheMetadata()
    {
        float s = (float)TransferFunction.Pq.FromLinear(1000 / 203.0);
        using Raster<RgbaF32> plain = Raster.Create<RgbaF32>(1, 1, new RgbaF32(s, s, s, 1));
        plain.Metadata.CodePoints = ColorCodePoints.Rec2100Pq;
        using Raster<RgbaF32> mastered = plain.Copy();
        mastered.Metadata.Hdr = new HdrMetadata { MaxContentLightLevel = 4000 };
        plain.Apply(ops => ops.ConvertColor(ColorProfile.Srgb), TestContext.Current.CancellationToken);
        mastered.Apply(ops => ops.ConvertColor(ColorProfile.Srgb), TestContext.Current.CancellationToken);
        Assert.Equal(1f, plain.GetPixel(0, 0).R, 4);
        Assert.True(mastered.GetPixel(0, 0).R < 0.99f);
        Assert.Null(mastered.Metadata.Hdr);   // the pixels are SDR now
        using Raster<Rgb8> typed = plain.ConvertColor<Rgb8>(ColorProfile.DisplayP3);
        Assert.Equal(255, typed.GetPixel(0, 0).R);
    }

    [Theory]
    [InlineData("pattern-pq", "Rec. 2100 PQ", 4000)]
    [InlineData("pattern-hlg", "Rec. 2100 HLG", 4000)]
    [InlineData("duck-pq", "Rec. 2100 PQ", 2030)]
    [InlineData("duck-hlg", "Rec. 2100 HLG", 2030)]
    public void HdrTestImagesLoadAsHdr(string file, string encoding, double peak)
    {
        using Raster<Rgb16> hdr = Raster.Load<Rgb16>(Path.Combine(AppContext.BaseDirectory, "images", "hdr", file + ".png"));
        Assert.Equal(encoding, hdr.Metadata.GetColorProfile().Name);
        Assert.Equal(peak, hdr.Metadata.Hdr!.MaxContentLightLevel);
        Assert.True(ColorProfile.FromIcc(hdr.Metadata.IccProfile.Span).IsHdr);   // the v4.4 profile names it through cicp too
        using Raster<Rgb8> sdr = hdr.ConvertColor<Rgb8>(ColorProfile.Srgb);
        Assert.Null(sdr.Metadata.Hdr);
        Assert.True(sdr.Metadata.IccProfile.IsEmpty);
    }

    [Fact]
    public void ToneMapWorksOnLinearPixels()
    {
        using Raster<RgbaF32> raster = Raster.Create<RgbaF32>(4, 1);
        raster.SetPixel(0, 0, new RgbaF32(0.05f, 0.1f, 0.02f, 1));
        raster.SetPixel(1, 0, new RgbaF32(2, 1, 0.5f, 0.5f));
        raster.SetPixel(2, 0, new RgbaF32(10, 10, 10, 1));
        raster.SetPixel(3, 0, new RgbaF32(1, 0.5f, 0.25f, 1));
        raster.Metadata.Hdr = new HdrMetadata { MaxContentLightLevel = 2030 };
        raster.Apply(ops => ops.ToneMap(), TestContext.Current.CancellationToken);
        Assert.Equal(new RgbaF32(0.05f, 0.1f, 0.02f, 1), raster.GetPixel(0, 0));   // below the knee: unchanged
        RgbaF32 p = raster.GetPixel(1, 0);
        Assert.True(p.R <= 1 && p.R > p.G && Math.Abs((p.G / p.R) - 0.5f) < 1e-5 && p.A == 0.5f);   // ratios and alpha kept
        Assert.Equal(1f, raster.GetPixel(2, 0).R, 4);   // the peak, 10 × 203, lands on white
        Assert.Null(raster.Metadata.Hdr);
        Assert.Throws<ArgumentOutOfRangeException>(() => raster.Apply(ops => ops.ToneMap(ToneMapping.Reinhard, -1), TestContext.Current.CancellationToken));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ColorTransformOptions { ReferenceWhiteNits = 0 });
    }
}
