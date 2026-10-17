using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 tasks 6 to 8: the lossy encoder's visual tools. Adaptive quantization gives flat areas finer steps than texture;
/// chroma from luma finds the factor that predicts chroma from luma; the inverse Gaborish and the decoder's Gaborish leave
/// smooth content where it was; the block-size search merges smooth areas into large DCTs and keeps detail in 8×8 transforms;
/// effort 5 files, which use them all, decode. Equality with libjxl's choices was checked with docker/jxldev in egami-compare,
/// the size against cjxl at efforts 5 to 7 is egami-compare's.
/// </summary>
public class JxlLossyToolTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    [Fact]
    public void FlatAreasGetFinerSteps()
    {
        // Left half flat gray, right half noise.
        int w = 128, h = 64;
        float[][] xyb = [new float[w * h], new float[w * h], new float[w * h]];
        Random random = new(3);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = x < w / 2 ? 0.4f : 0.4f + ((float)random.NextDouble() * 0.2f) - 0.1f;
                xyb[1][(y * w) + x] = v;
                xyb[2][(y * w) + x] = v;
            }
        }

        JxlAdaptiveQuantization aq = JxlAdaptiveQuantization.Compute(xyb, w, h, 1, new ParallelOptions { MaxDegreeOfParallelism = 1 });
        int bw = w / 8;
        float flat = aq.QuantField[(4 * bw) + 2];
        float busy = aq.QuantField[(4 * bw) + 13];
        Assert.True(flat > busy * 1.2f, $"flat block {flat}, textured block {busy}");
        Assert.All(aq.QuantField, q => Assert.True(q > 0 && float.IsFinite(q)));
        Assert.All(aq.Mask1x1, m => Assert.True(m > 0 && float.IsFinite(m)));
    }

    [Theory]
    [InlineData(0.0f, 0.5f)]
    [InlineData(0.0f, -0.25f)]
    [InlineData(1.0f, 1.3f)]
    public void ChromaFromLumaFindsTheFactor(float baseCorrelation, float factor)
    {
        Random random = new(11);
        float[] luma = new float[512];
        float[] chroma = new float[512];
        for (int i = 0; i < luma.Length; i++)
        {
            luma[i] = ((float)random.NextDouble() * 200) - 100;
            chroma[i] = luma[i] * factor;
        }

        foreach (bool fast in (bool[])[true, false])
        {
            int found = JxlChromaFromLuma.FindBestMultiplier(luma, chroma, baseCorrelation, fast);
            // The search pulls its answer 2.6 steps towards zero.
            float exact = (factor - baseCorrelation) * 84;
            float expected = exact - (MathF.Sign(exact) * 2.6f);
            Assert.InRange(found, expected - 3, expected + 3);
        }
    }

    [Fact]
    public void InverseGaborishThenGaborishKeepsSmoothContent()
    {
        int w = 96, h = 72;
        float[][] planes = [new float[w * h], new float[w * h], new float[w * h]];
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                for (int c = 0; c < 3; c++)
                {
                    planes[c][(y * w) + x] = 0.3f + (0.1f * MathF.Sin((x + (c * 7)) / 9f)) + (0.05f * MathF.Cos(y / 11f));
                }
            }
        }

        float[][] original = [.. planes.Select(p => (float[])p.Clone())];
        JxlAdaptiveQuantization.GaborishInverse(planes, w, h, new ParallelOptions());
        JxlFilters.Gaborish(planes, w, h, w, [.. JxlLoopFilter.DefaultGaborish], JxlParallel.Single);
        for (int c = 0; c < 3; c++)
        {
            float worst = 0;
            for (int i = 0; i < w * h; i++)
            {
                worst = MathF.Max(worst, MathF.Abs(planes[c][i] - original[c][i]));
            }

            Assert.True(worst < 2e-3f, $"plane {c}: {worst}");
        }
    }

    [Fact]
    public void SmoothAreasGetLargeTransformsAndDetailSmallOnes()
    {
        // Left 64×64 tile a gentle gradient, right tile noise.
        int w = 128, h = 64;
        float[][] xyb = [new float[w * h], new float[w * h], new float[w * h]];
        Random random = new(7);
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                float v = x < 64 ? 0.3f + (0.002f * x) + (0.001f * y) : 0.25f + ((float)random.NextDouble() * 0.3f);
                xyb[1][(y * w) + x] = v;
                xyb[2][(y * w) + x] = v;
            }
        }

        ParallelOptions single = new() { MaxDegreeOfParallelism = 1 };
        JxlAdaptiveQuantization aq = JxlAdaptiveQuantization.Compute(xyb, w, h, 1, single);
        JxlAdaptiveQuantization.GaborishInverse(xyb, w, h, single);
        sbyte[] strategy = new sbyte[16 * 8];
        bool[] first = new bool[16 * 8];
        Array.Fill(first, true);
        JxlAcStrategySearch search = new(xyb, 16, 8, aq.QuantField, aq.Mask1x1, 1, 6, strategy, first);
        search.ProcessTile(0, 0, 8, 8, 0, 1);
        search.ProcessTile(8, 0, 8, 8, 0, 1);
        int smoothFirsts = 0, detailFirsts = 0;
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                if (first[(y * 16) + x])
                {
                    _ = x < 8 ? smoothFirsts++ : detailFirsts++;
                }
            }
        }

        Assert.True(smoothFirsts <= 4, $"the gradient tile has {smoothFirsts} varblocks");
        Assert.True(detailFirsts >= 48, $"the noise tile has {detailFirsts} varblocks");
    }

    [Fact]
    public void EffortFiveIsSmallerAtAboutTheSameQuality()
    {
        using Raster<Rgb8> source = Raster.Load<Rgb8>(Path.Combine(Images, "ops", "egret.png"));
        byte[] e4 = Save(source, 4);
        byte[] e5 = Save(source, 5);
        using Raster<Rgb8> back = Raster.Load<Rgb8>(e5);
        Assert.True(e5.Length < e4.Length, $"effort 5 {e5.Length} bytes, effort 4 {e4.Length}");
        Assert.True(Psnr(source, back) > 33);
    }

    private static byte[] Save(Raster<Rgb8> raster, int effort)
    {
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = false, Effort = effort });
        return file.ToArray();
    }

    private static double Psnr(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        double se = 0;
        long n = 0;
        for (int y = 0; y < a.Height; y++)
        {
            ReadOnlySpan<byte> ra = System.Runtime.InteropServices.MemoryMarshal.AsBytes(a.Row(y));
            ReadOnlySpan<byte> rb = System.Runtime.InteropServices.MemoryMarshal.AsBytes(b.Row(y));
            for (int i = 0; i < ra.Length; i++)
            {
                double d = ra[i] - rb[i];
                se += d * d;
                n++;
            }
        }

        return se == 0 ? 99 : 10 * Math.Log10(255.0 * 255 * n / se);
    }
}
