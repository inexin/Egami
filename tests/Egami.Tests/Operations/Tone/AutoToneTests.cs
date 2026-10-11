using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations.Tone;

/// <summary>Phase 2.5.3 stage C: measured tone steps in chains, scopes and animations, and the local steps.</summary>
public class AutoToneTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void AChainWithAMeasuredStepEqualsItsHalvesRunByHand()
    {
        using Raster<Rgba8> raster = Dull(80, 60);
        using Raster<Rgba8> chained = raster.Derive(o => o.Brightness(0.8f).AutoLevel().Gamma(1.2f), Ct);
        using Raster<Rgba8> first = raster.Derive(o => o.Brightness(0.8f), Ct);
        ColorFilter level = first.MeasureAutoLevel();
        using Raster<Rgba8> second = first.Derive(o => o.ApplyFilter(level).Gamma(1.2f), Ct);
        Assert.Equal(0, ColorFilterTests.MaxDifference(second, chained));

        (int min, int max) = Range(chained);
        Assert.True(min <= 1 && max >= 250, $"{min}..{max}");
    }

    [Fact]
    public void OneThreadGivesThePixelsOfAllCores()
    {
        using Raster<Rgba8> source = Dull(700, 500);
        Action<RasterOps> chain = o => o.AutoLevel().Equalize().AutoWhiteBalance().Clahe(5, 4).LocalContrast(0.2f, 0.3f).AdaptiveThreshold(0.1f, 41);
        using Raster<Rgba8> all = source.Derive(chain, Ct);
        using Raster<Rgba8> one = Raster.Create<Rgba8>(700, 500, EgamiContext.Default with { MaxParallelism = 1 });
        for (int y = 0; y < 500; y++)
        {
            source.Row(y).CopyTo(one.Row(y));
        }

        one.Apply(chain, Ct);
        Assert.Equal(0, ColorFilterTests.MaxDifference(all, one));
    }

    [Fact]
    public void AutoLevelStretchesToTheFullRange()
    {
        using Raster<Rgba8> raster = Dull(64, 64);
        raster.Apply(o => o.AutoLevel(), Ct);
        Assert.Equal((0, 255), Range(raster));
    }

    [Fact]
    public void FlatImagesAreLeftAlone()
    {
        using Raster<Rgba8> flat = Raster.Create(30, 20, new Rgba8(90, 90, 90, 255));
        using Raster<Rgba8> result = flat.Derive(o => o.AutoLevel().Normalize().Equalize().Equalize(EqualizeMode.Channels).AutoLevel(linked: false), Ct);
        Assert.Equal(0, ColorFilterTests.MaxDifference(flat, result));
        Assert.Equal(0.5f, flat.FindThreshold());
        using Raster<Rgba8> black = Raster.Create(10, 10, new Rgba8(0, 0, 0, 255));
        using Raster<Rgba8> gamma = black.Derive(o => o.AutoGamma(), Ct);
        Assert.Equal(0, ColorFilterTests.MaxDifference(black, gamma));
    }

    [Fact]
    public void ScopedStepsMeasureOnlyTheirScope()
    {
        // A dull left half and a full-range right half: inside the left rectangle, AutoLevel stretches the left alone.
        using Raster<Rgba8> raster = Dull(64, 32);
        for (int y = 0; y < 32; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            row[40] = new Rgba8(0, 0, 0, 255);
            row[41] = new Rgba8(255, 255, 255, 255);
        }

        Rect left = new(0, 0, 32, 32);
        using Raster<Rgba8> scoped = raster.Derive(o => o.Within(left, w => w.AutoLevel()), Ct);
        using Raster<Rgba8> whole = raster.Derive(o => o.AutoLevel(), Ct);
        using Raster<Rgba8> leftOnly = raster.Derive(o => o.Crop(left).AutoLevel(), Ct);
        for (int y = 0; y < 32; y++)
        {
            Assert.True(scoped.Row(y)[..32].SequenceEqual(leftOnly.Row(y)), $"row {y}");
            Assert.True(scoped.Row(y)[32..].SequenceEqual(raster.Row(y)[32..]), $"row {y} outside");
        }

        Assert.Equal(0, ColorFilterTests.MaxDifference(raster, whole));

        // OnChannels measures only its channels.
        using Raster<Rgba8> red = raster.Derive(o => o.Crop(left).OnChannels(ColorChannels.Red, c => c.AutoLevel()), Ct);
        (int min, int max) = (red.Frames[0].Plane.Row(0).ToArray().Min(p => p.R), red.Frames[0].Plane.Row(0).ToArray().Max(p => p.R));
        Assert.True(min >= 0 && max <= 255);
        using Raster<Rgba8> cropped = raster.Derive(o => o.Crop(left), Ct);
        Assert.True(cropped.Row(5).ToArray().Select(p => p.G).SequenceEqual(red.Row(5).ToArray().Select(p => p.G)));

        // Nested scopes and steps before the measured one: the prefix runs first, then the scope is measured.
        using Raster<Rgba8> nested = raster.Derive(o => o.Within(left, w => w.Brightness(0.5f).OnChannels(ColorChannels.Color, c => c.AutoLevel())), Ct);
        using Raster<Rgba8> byHand = raster.Derive(o => o.Crop(left).Brightness(0.5f).AutoLevel(), Ct);
        Assert.True(nested.Row(7)[..32].SequenceEqual(byHand.Row(7)));
    }

    [Fact]
    public void MasksWeighWhatIsMeasured()
    {
        using Raster<Rgba8> raster = Dull(40, 40);
        raster.Row(0)[0] = new Rgba8(0, 0, 0, 255);
        using Raster<Gray8> mask = Raster.Create(40, 40, new Gray8(255));
        mask.Row(0)[0] = new Gray8(0);
        using Raster<Rgba8> masked = raster.Derive(o => o.Within(new Mask(mask), w => w.AutoLevel()), Ct);
        // The black pixel has no coverage, so it neither counts nor changes; the rest stretch to their own range.
        Assert.Equal(new Rgba8(0, 0, 0, 255), masked.Row(0)[0]);
        Assert.Equal(255, masked.Frames[0].Plane.Row(39).ToArray().Max(p => p.R));
    }

    [Fact]
    public void AnimationFramesShareOneCurveUnlessPerFrame()
    {
        using Raster<Rgba8> animation = Dull(20, 20);
        Frame<Rgba8> second = animation.Frames.Add(animation.Frames[0]);
        for (int y = 0; y < 20; y++)
        {
            second.Row(y).Fill(new Rgba8(100, 100, 100, 255));
        }

        second.Row(0)[0] = new Rgba8(120, 120, 120, 255);
        using Raster<Rgba8> shared = animation.Derive(o => o.AutoLevel(), Ct);
        using Raster<Rgba8> perFrame = animation.Derive(o => o.AutoLevel(perFrame: true), Ct);
        Assert.NotEqual(0, shared.Frames[1].Row(5)[5].R);
        Assert.Equal(0, perFrame.Frames[1].Row(5)[5].R);
        Assert.Equal(255, perFrame.Frames[1].Row(0)[0].R);
    }

    [Fact]
    public void ThresholdMethodsSplitABimodalHistogram()
    {
        using Raster<Gray8> page = Raster.Create(200, 100, new Gray8(220));
        Random random = new(3);
        for (int y = 0; y < 100; y++)
        {
            Span<Gray8> row = page.Row(y);
            for (int x = 0; x < 200; x++)
            {
                row[x] = new Gray8((byte)((x / 7 + y / 9) % 5 == 0 ? 40 + random.Next(20) : 200 + random.Next(30)));
            }
        }

        foreach (ThresholdMethod method in Enum.GetValues<ThresholdMethod>())
        {
            float level = page.FindThreshold(method);
            Assert.InRange(level, 55 / 255f, 200 / 255f);
            using Raster<Gray8> result = page.Derive(o => o.AutoThreshold(method), Ct);
            foreach (Gray8 p in result.Row(3))
            {
                Assert.True(p.L is 0 or 255);
            }
        }
    }

    [Fact]
    public void AdaptiveThresholdSurvivesUnevenLight()
    {
        using Raster<Gray8> page = Raster.Create<Gray8>(240, 120);
        for (int y = 0; y < 120; y++)
        {
            Span<Gray8> row = page.Row(y);
            for (int x = 0; x < 240; x++)
            {
                int paper = 60 + (x * 180 / 240);
                bool ink = y % 20 is >= 8 and < 11 && x % 12 < 7;
                row[x] = new Gray8((byte)(ink ? paper * 6 / 10 : paper));
            }
        }

        using Raster<Gray8> result = page.Derive(o => o.AdaptiveThreshold(window: 30), Ct);
        int wrong = 0;
        for (int y = 0; y < 120; y++)
        {
            for (int x = 0; x < 240; x++)
            {
                bool ink = y % 20 is >= 8 and < 11 && x % 12 < 7;
                wrong += (result.Row(y)[x].L == 0) != ink ? 1 : 0;
            }
        }

        Assert.True(wrong < 240 * 120 / 50, $"{wrong} pixels misread");
    }

    [Fact]
    public void WhiteBalanceByTemperatureNeutralizesAPlanckianLitGray()
    {
        foreach (float kelvin in new[] { 2856f, 4000f, 9000f })
        {
            Vector3 lit = LitGray(kelvin, 0.4);
            Rgba8 pixel = new((byte)(lit.X * 255 + 0.5f), (byte)(lit.Y * 255 + 0.5f), (byte)(lit.Z * 255 + 0.5f), 255);
            using Raster<Rgba8> raster = Raster.Create(4, 4, pixel);
            raster.Apply(o => o.WhiteBalance(kelvin), Ct);
            Assert.True(Chroma(raster.Row(0)[0]) < 1.5, $"{kelvin} K: {raster.Row(0)[0]}, chroma {Chroma(raster.Row(0)[0]):F2}");
        }

        using Raster<Rgba8> picked = Raster.Create(4, 4, new Rgba8(180, 150, 120, 255));
        picked.Apply(o => o.WhiteBalance(new Rgba8(180, 150, 120, 255)), Ct);
        Assert.True(Chroma(picked.Row(0)[0]) < 1, $"{picked.Row(0)[0]}");
    }

    [Fact]
    public void AutoWhiteBalanceRemovesACast()
    {
        foreach (WhiteBalanceMethod method in Enum.GetValues<WhiteBalanceMethod>())
        {
            using Raster<Rgba8> raster = Dull(60, 60);
            raster.Apply(o => o.Colorize(new Rgba8(255, 160, 60, 255), 0.25f), Ct);
            double before = MeanChroma(raster);
            raster.Apply(o => o.AutoWhiteBalance(method), Ct);
            Assert.True(MeanChroma(raster) < before * 0.6, $"{method}: {before:F1} -> {MeanChroma(raster):F1}");
        }
    }

    [Fact]
    public void EqualizeFlattensTheHistogram()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(64, 64);
        for (int y = 0; y < 64; y++)
        {
            for (int x = 0; x < 64; x++)
            {
                raster.Row(y)[x] = new Gray8((byte)(100 + ((x + (y * 64)) % 40)));
            }
        }

        raster.Apply(o => o.Equalize(EqualizeMode.Channels), Ct);
        (int min, int max) = (255, 0);
        for (int y = 0; y < 64; y++)
        {
            foreach (Gray8 p in raster.Row(y))
            {
                min = Math.Min(min, p.L);
                max = Math.Max(max, p.L);
            }
        }

        Assert.InRange(min, 0, 255 / 40 + 1);
        Assert.Equal(255, max);
    }

    [Fact]
    public void ClaheMatchesTheDoubleReference()
    {
        using Raster<Gray8> raster = Raster.Create<Gray8>(97, 61);
        Random random = new(11);
        for (int y = 0; y < 61; y++)
        {
            for (int x = 0; x < 97; x++)
            {
                raster.Row(y)[x] = new Gray8((byte)Math.Clamp(90 + (x / 3) + random.Next(-8, 9) - (y / 2), 0, 255));
            }
        }

        using Raster<Gray8> result = raster.Derive(o => o.Clahe(4, 3, 2.5f, 128), Ct);
        double[,] reference = ClaheReference(raster, 4, 3, 2.5, 128);
        int worst = 0;
        for (int y = 0; y < 61; y++)
        {
            for (int x = 0; x < 97; x++)
            {
                worst = Math.Max(worst, Math.Abs(result.Row(y)[x].L - (int)Math.Round(reference[y, x])));
            }
        }

        Assert.True(worst <= 1, $"{worst} levels");
    }

    [Fact]
    public void LocalContrastRaisesDetailAndLeavesFlatImagesAlone()
    {
        // ImageMagick's triangle leaves out one tap of weight 1 in (w + 1)², so a flat image brightens by strength / (w + 1)²: well
        // under a level once the radius spans a few pixels.
        using Raster<Rgba8> flat = Raster.Create(400, 400, new Rgba8(120, 130, 140, 255));
        using Raster<Rgba8> same = flat.Derive(o => o.LocalContrast(), Ct);
        Assert.True(ColorFilterTests.MaxDifference(flat, same) <= 1);

        using Raster<Rgba8> raster = Dull(80, 80);
        using Raster<Rgba8> result = raster.Derive(o => o.LocalContrast(0.2f, 1), Ct);
        Assert.True(Spread(result) > Spread(raster) * 1.05, $"{Spread(raster):F1} -> {Spread(result):F1}");
    }

    /// <summary>A low-contrast picture: values from about 70 to 180, with some color.</summary>
    internal static Raster<Rgba8> Dull(int width, int height)
    {
        Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        Random random = new((width * 3) + height);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                int v = 70 + ((x + y) * 110 / (width + height)) + random.Next(-5, 6);
                row[x] = new Rgba8((byte)(v + 10), (byte)v, (byte)(v - 5), 255);
            }
        }

        return raster;
    }

    private static (int Min, int Max) Range(Raster<Rgba8> raster)
    {
        int min = 255, max = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                min = Math.Min(min, Math.Min(p.R, Math.Min(p.G, p.B)));
                max = Math.Max(max, Math.Max(p.R, Math.Max(p.G, p.B)));
            }
        }

        return (min, max);
    }

    private static double Spread(Raster<Rgba8> raster)
    {
        double sum = 0, squares = 0;
        int n = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                sum += p.G;
                squares += p.G * p.G;
                n++;
            }
        }

        return Math.Sqrt((squares / n) - ((sum / n) * (sum / n)));
    }

    /// <summary>A gray of the given linear reflectance lit by a Planckian light, in encoded sRGB with the light's white at Y = 1.</summary>
    private static Vector3 LitGray(float kelvin, double reflectance)
    {
        // Planck's law integrated against the CIE 1931 2° observer (Wyman, Sloan and Shirley's multi-lobe fit), so the test does not
        // reuse the step's own locus approximation.
        double x = 0, y = 0, z = 0;
        for (double nm = 380; nm <= 780; nm += 1)
        {
            double l = nm * 1e-9;
            double power = 1 / (Math.Pow(l, 5) * (Math.Exp(1.4387769e-2 / (l * kelvin)) - 1));
            x += power * Xbar(nm);
            y += power * Ybar(nm);
            z += power * Zbar(nm);
        }

        double X = x / y * reflectance, Y = reflectance, Z = z / y * reflectance;
        double r = (3.2409699 * X) - (1.5373832 * Y) - (0.4986108 * Z);
        double g = (-0.9692436 * X) + (1.8759675 * Y) + (0.0415551 * Z);
        double b = (0.0556301 * X) - (0.2039770 * Y) + (1.0569715 * Z);
        return new Vector3((float)Srgb.FromLinear(Math.Clamp(r, 0, 1)), (float)Srgb.FromLinear(Math.Clamp(g, 0, 1)), (float)Srgb.FromLinear(Math.Clamp(b, 0, 1)));

        static double G(double v, double mu, double s1, double s2) => Math.Exp(-0.5 * Math.Pow((v - mu) / (v < mu ? s1 : s2), 2));
        static double Xbar(double l) => (1.056 * G(l, 599.8, 37.9, 31.0)) + (0.362 * G(l, 442.0, 16.0, 26.7)) - (0.065 * G(l, 501.1, 20.4, 26.2));
        static double Ybar(double l) => (0.821 * G(l, 568.8, 46.9, 40.5)) + (0.286 * G(l, 530.9, 16.3, 31.1));
        static double Zbar(double l) => (1.217 * G(l, 437.0, 11.8, 36.0)) + (0.681 * G(l, 459.0, 26.0, 13.8));
    }

    /// <summary>The chroma √(a*² + b*²) of a pixel, which bounds its ΔE2000 from the gray of the same lightness.</summary>
    private static double Chroma(Rgba8 p)
    {
        double r = Srgb.ToLinear(p.R / 255.0), g = Srgb.ToLinear(p.G / 255.0), b = Srgb.ToLinear(p.B / 255.0);
        double x = ((0.4123908 * r) + (0.3575843 * g) + (0.1804808 * b)) / 0.95047;
        double y = (0.2126390 * r) + (0.7151687 * g) + (0.0721923 * b);
        double z = ((0.0193308 * r) + (0.1191948 * g) + (0.9505322 * b)) / 1.08883;
        double fx = Math.Cbrt(x), fy = Math.Cbrt(y), fz = Math.Cbrt(z);
        return Math.Sqrt(Math.Pow(500 * (fx - fy), 2) + Math.Pow(200 * (fy - fz), 2));
    }

    private static double MeanChroma(Raster<Rgba8> raster)
    {
        double sum = 0;
        int n = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (Rgba8 p in raster.Row(y))
            {
                sum += Chroma(p);
                n++;
            }
        }

        return sum / n;
    }

    /// <summary>
    /// CLAHE on gray pixels in double precision, written from Zuiderveld's chapter, with ImageMagick's 16-bit binning of L*, for gray
    /// pixels (where L* is the whole color).
    /// </summary>
    private static double[,] ClaheReference(Raster<Gray8> raster, int tilesX, int tilesY, double clip, int bins)
    {
        int width = raster.Width, height = raster.Height;
        double[,] l = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                double lin = Srgb.ToLinear(raster.Row(y)[x].L / 255.0);
                l[y, x] = lin > 216.0 / 24389 ? (116 * Math.Cbrt(lin)) - 16 : 24389.0 / 27 * lin;
            }
        }

        double[,,] maps = new double[tilesY, tilesX, bins];
        for (int j = 0; j < tilesY; j++)
        {
            for (int i = 0; i < tilesX; i++)
            {
                int x0 = width * i / tilesX, x1 = width * (i + 1) / tilesX, y0 = height * j / tilesY, y1 = height * (j + 1) / tilesY;
                double[] h = new double[bins];
                for (int y = y0; y < y1; y++)
                {
                    for (int x = x0; x < x1; x++)
                    {
                        h[Bin(l[y, x])]++;
                    }
                }

                // Zuiderveld's clipping: an even share of the excess to bins that stay under the limit, bins near it filled to it, and
                // the rest one count at a time to bins still under it.
                double pixels = (x1 - x0) * (y1 - y0), limit = Math.Max(1, Math.Floor(clip * pixels / bins)), excess = 0;
                for (int k = 0; k < bins; k++)
                {
                    excess += Math.Max(0, h[k] - limit);
                }

                double step = Math.Floor(excess / bins);
                for (int k = 0; k < bins; k++)
                {
                    if (h[k] > limit)
                    {
                        h[k] = limit;
                    }
                    else if (h[k] > limit - step)
                    {
                        excess -= limit - h[k];
                        h[k] = limit;
                    }
                    else
                    {
                        excess -= step;
                        h[k] += step;
                    }
                }

                while (excess >= 1)
                {
                    double before = excess;
                    int stride = Math.Max(1, (int)(bins / excess));
                    for (int k = 0; k < bins && excess >= 1; k += stride)
                    {
                        if (h[k] < limit)
                        {
                            h[k]++;
                            excess--;
                        }
                    }

                    if (excess >= before)
                    {
                        break;
                    }
                }

                double cumulative = 0;
                for (int k = 0; k < bins; k++)
                {
                    cumulative += h[k];
                    maps[j, i, k] = Math.Min(1, cumulative / pixels);
                }
            }
        }

        double[,] result = new double[height, width];
        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                // From tile center to tile center, a center being the first pixel past half a tile.
                double tw = width / (double)tilesX, th = height / (double)tilesY;
                double gx = Math.Clamp((x - Math.Floor(tw / 2)) / tw, 0, tilesX - 1), gy = Math.Clamp((y - Math.Floor(th / 2)) / th, 0, tilesY - 1);
                int i0 = Math.Min((int)gx, tilesX - 2), j0 = Math.Min((int)gy, tilesY - 2);
                double fx = gx - i0, fy = gy - j0;
                int k = Bin(l[y, x]);
                double top = maps[j0, i0, k] + ((maps[j0, i0 + 1, k] - maps[j0, i0, k]) * fx);
                double bottom = maps[j0 + 1, i0, k] + ((maps[j0 + 1, i0 + 1, k] - maps[j0 + 1, i0, k]) * fx);
                double lightness = (top + ((bottom - top) * fy)) * 100;
                double f = (lightness + 16) / 116;
                double lin = lightness > 8 ? f * f * f : lightness * 27 / 24389.0;
                result[y, x] = Srgb.FromLinear(Math.Clamp(lin, 0, 1)) * 255;
            }
        }

        return result;

        // L* in 16-bit steps, in bins of 65535 / bins + 1 steps.
        int Bin(double lightness) => Math.Min(bins - 1, (int)Math.Round(Math.Clamp(lightness / 100, 0, 1) * 65535) / ((65535 / bins) + 1));
    }
}
