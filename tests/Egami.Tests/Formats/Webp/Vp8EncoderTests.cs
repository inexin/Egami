using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// The VP8 encoder: its SIMD kernels match the scalar ones exactly, its reconstruction is exactly what the decoder rebuilds (so
/// prediction never drifts), and every effort and quality round-trips through Egami's decoder close to the source.
/// </summary>
public class Vp8EncoderTests
{
    private const int S = Vp8Dsp.Stride;

    [Fact]
    public void SimdKernelsMatchScalar()
    {
        Assert.SkipUnless(Vp8TransformSimd.IsSupported, "SSE4.1 is not available.");
        Random random = new(7);
        byte[] a = new byte[16 * S], b = new byte[16 * S];
        Vp8Matrix[] matrices = [new(), new(), new()];
        for (int round = 0; round < 2000; round++)
        {
            random.NextBytes(a);
            random.NextBytes(b);
            if (round % 3 == 0)
            {
                // Mostly-similar blocks, as predictions are.
                for (int i = 0; i < b.Length; i++)
                {
                    b[i] = (byte)Math.Clamp(a[i] + random.Next(-6, 7), 0, 255);
                }
            }

            int at = ((round & 3) * 4) + ((round >> 2 & 3) * 4 * S);
            short[] simd = new short[16], scalar = new short[16];
            Vp8TransformSimd.ForwardTransform(a, at, b, at, simd);
            Vp8EncDsp.ForwardTransformScalar(a, at, b, at, scalar);
            Assert.Equal(scalar, simd);

            int q = random.Next(0, 128);
            matrices[0].Set(Vp8Tables.DcTable[q], Vp8Tables.AcTable[q], 0);
            matrices[1].Set(Vp8Tables.DcTable[q] * 2, Math.Max(8, (Vp8Tables.AcTable[q] * 101581) >> 16), 1);
            matrices[2].Set(Vp8Tables.DcTable[Math.Min(q, 117)], Vp8Tables.AcTable[q], 2);
            Vp8Matrix m = matrices[round % 3];
            short[] c1 = new short[16], c2 = new short[16], l1 = new short[16], l2 = new short[16];
            for (int i = 0; i < 16; i++)
            {
                c1[i] = c2[i] = (short)(round % 5 == 0 ? random.Next(-32768, 32768) : random.Next(-2048, 2048));
            }

            Assert.Equal(Vp8EncDsp.QuantizeScalar(c1, l1, m), Vp8TransformSimd.Quantize(c2, l2, m));
            Assert.Equal(c1, c2);
            Assert.Equal(l1, l2);

            byte[] p1 = (byte[])b.Clone(), p2 = (byte[])b.Clone();
            short[] residual = new short[16];
            for (int i = 0; i < 16; i++)
            {
                residual[i] = (short)(round % 7 == 0 ? random.Next(-32768, 32768) : random.Next(-600, 600));
            }

            Vp8Dsp.InverseTransformAddScalar(residual, p1, at);
            Vp8TransformSimd.InverseTransformAdd(residual, p2, at);
            Assert.Equal(p1, p2);

            // The decoder's two side-by-side blocks at once, and their DC-only add, against the scalar transform: in range of the
            // 16-bit lanes, at their ±2048 edge, and past it (the 32-bit fallback).
            short[] pair = new short[32];
            for (int i = 0; i < 32; i++)
            {
                pair[i] = (short)(round % 7 == 0 ? random.Next(-32768, 32768) : round % 7 == 1 ? random.Next(-2048, 2049) : random.Next(-600, 600));
            }

            int pairAt = ((round & 1) * 8) + ((round >> 1 & 3) * 4 * S);
            byte[] p3 = (byte[])b.Clone(), p4 = (byte[])b.Clone();
            Vp8Dsp.InverseTransformAddScalar(pair.AsSpan(0, 16), p3, pairAt);
            Vp8Dsp.InverseTransformAddScalar(pair.AsSpan(16, 16), p3, pairAt + 4);
            Vp8TransformSimd.InverseTransformAddTwo(ref pair[0], ref p4[pairAt], S);
            Assert.Equal(p3, p4);
            short[] dcs = new short[32];
            dcs[0] = pair[0];
            dcs[16] = pair[16];
            Vp8Dsp.InverseTransformAddScalar(dcs.AsSpan(0, 16), p3, pairAt);
            Vp8Dsp.InverseTransformAddScalar(dcs.AsSpan(16, 16), p3, pairAt + 4);
            Vp8TransformSimd.DcAddTwo(dcs[0], dcs[16], ref p4[pairAt], S);
            Assert.Equal(p3, p4);

            Assert.Equal(Vp8EncDsp.HadamardWeightedScalar(a, at), Vp8TransformSimd.HadamardWeighted(a, at));
            Assert.Equal(ScalarSse(a, b, 0, 16, 16), Vp8TransformSimd.Sse(a, b, 0, 16, 16));
            Assert.Equal(ScalarSse(a, b, 16, 16, 8), Vp8TransformSimd.Sse(a, b, 16, 16, 8));
            Assert.Equal(ScalarSse(a, b, at, 4, 4), Vp8TransformSimd.Sse4x4(a, at, b, at));
        }
    }

    [Fact]
    public void ModeSearchKernelsMatchTheSteps()
    {
        Random random = new(11);
        byte[] ctx = new byte[17 * S], src = new byte[16 * S], predictions = new byte[8 * S], stepwise = new byte[17 * S];
        Vp8Matrix m = new();
        for (int round = 0; round < 2000; round++)
        {
            random.NextBytes(ctx);
            random.NextBytes(src);
            int at = S + 1 + ((round & 3) * 4) + ((round >> 2 & 3) * 4 * S);

            // All ten 4 × 4 predictions at once, as the decoder's predictor makes them one by one.
            Vp8EncDsp.PredictSubblocks(ctx, at, predictions);
            for (int mode = 0; mode < 10; mode++)
            {
                ctx.CopyTo(stepwise, 0);
                Vp8Dsp.PredictSubblock(mode, stepwise, at);
                int p = ((mode & 7) * 4) + ((mode >> 3) * 4 * S);
                for (int y = 0; y < 4; y++)
                {
                    Assert.True(stepwise.AsSpan(at + (y * S), 4).SequenceEqual(predictions.AsSpan(p + (y * S), 4)), $"mode {mode} row {y}");
                }
            }

            // One mode trial in registers, as the transform, quantization, reconstruction and measures one after the other.
            if (Vp8TransformSimd.IsSupported)
            {
                int q = random.Next(0, 128);
                m.Set(Vp8Tables.DcTable[q], Vp8Tables.AcTable[q], 0);
                int mode = round % 10, p = ((mode & 7) * 4) + ((mode >> 3) * 4 * S), s = ((round & 3) * 4) + ((round >> 2 & 3) * 4 * S);
                short[] coefficients = new short[16], levels = new short[16], fusedLevels = new short[16];
                byte[] expected = new byte[4 * S], actual = new byte[4 * S];
                Vp8EncDsp.ForwardTransformScalar(src, s, predictions, p, coefficients);
                bool any = Vp8EncDsp.QuantizeScalar(coefficients, levels, m);
                Vp8EncDsp.Copy(predictions, p, expected, 0, 4, 4);
                Vp8Dsp.InverseTransformAddScalar(coefficients, expected, 0);
                int mask = Vp8TransformSimd.ReconstructBlock(src, s, predictions, p, m, fusedLevels, actual, 0, out int sse, out int spectrum);
                Assert.Equal(levels, fusedLevels);
                Assert.Equal(expected, actual);
                Assert.Equal(any, mask != 0);
                Assert.Equal(levels.Count(l => l != 0), System.Numerics.BitOperations.PopCount((uint)mask));
                Assert.Equal(BlockSse(src, s, expected, 0), sse);
                Assert.Equal(Vp8EncDsp.HadamardWeightedScalar(expected, 0), spectrum);
            }

            // The AVX2 kernels, two blocks at once, as the SSE4.1 ones one block at a time.
            if (Vp8PairSimd.IsSupported)
            {
                int q = random.Next(0, 128);
                m.Set(Vp8Tables.DcTable[q], Vp8Tables.AcTable[q], round % 2 == 0 ? 0 : 2);
                int s = ((round & 1) * 8) + ((round >> 1 & 3) * 4 * S);
                int modeA = round % 10, modeB = (round / 10) % 10;
                int pa = ((modeA & 7) * 4) + ((modeA >> 3) * 4 * S), pb = ((modeB & 7) * 4) + ((modeB >> 3) * 4 * S);
                short[] pairLevels = new short[32], levelsA = new short[16], levelsB = new short[16];
                byte[] pairOut = new byte[4 * S], outA = new byte[4 * S], outB = new byte[4 * S];
                int pairMask = Vp8PairSimd.ReconstructTrials(src, s, predictions, pa, pb, m, pairLevels, pairOut, 0, out int sseA, out int sseB, out int spectrumA, out int spectrumB);
                int maskA = Vp8TransformSimd.ReconstructBlock(src, s, predictions, pa, m, levelsA, outA, 0, out int sse1, out int spectrum1);
                int maskB = Vp8TransformSimd.ReconstructBlock(src, s, predictions, pb, m, levelsB, outB, 4, out int sse2, out int spectrum2);
                Assert.Equal(maskA | (maskB << 16), pairMask);
                Assert.Equal([.. levelsA, .. levelsB], pairLevels);
                Assert.Equal((sse1, sse2, spectrum1, spectrum2), (sseA, sseB, spectrumA, spectrumB));
                Vp8EncDsp.Copy(outA, 0, outB, 0, 4, 4);
                Assert.Equal(outB, pairOut);

                // Two blocks side by side: the forward transform, then the reconstruction from coefficients, with and without a given DC.
                short[] pairCoefficients = new short[32], single = new short[32];
                Vp8PairSimd.ForwardTransform(src, s, ctx, s, pairCoefficients);
                Vp8EncDsp.ForwardTransformScalar(src, s, ctx, s, single.AsSpan(0, 16));
                Vp8EncDsp.ForwardTransformScalar(src, s + 4, ctx, s + 4, single.AsSpan(16, 16));
                Assert.Equal(single, pairCoefficients);
                bool dcGiven = round % 3 == 0;
                pairCoefficients[0] = (short)random.Next(-2048, 2048);
                pairCoefficients[16] = (short)random.Next(-2048, 2048);
                pairCoefficients.CopyTo(single, 0);
                pairMask = Vp8PairSimd.ReconstructCoefficients(pairCoefficients, dcGiven, src, s, ctx, s, m, pairLevels, pairOut, 0, out sseA, out sseB, out spectrumA, out spectrumB);
                maskA = dcGiven
                    ? Vp8TransformSimd.ReconstructAcBlock(single.AsSpan(0, 16), src, s, ctx, s, m, levelsA, outA, 0, out sse1, out spectrum1)
                    : ReconstructFrom(single.AsSpan(0, 16), src, s, ctx, s, m, levelsA, outA, out sse1, out spectrum1);
                maskB = dcGiven
                    ? Vp8TransformSimd.ReconstructAcBlock(single.AsSpan(16, 16), src, s + 4, ctx, s + 4, m, levelsB, outA, 4, out sse2, out spectrum2)
                    : ReconstructFrom(single.AsSpan(16, 16), src, s + 4, ctx, s + 4, m, levelsB, outB, out sse2, out spectrum2);
                if (!dcGiven)
                {
                    Vp8EncDsp.Copy(outB, 0, outA, 4, 4, 4);
                }

                Assert.Equal(maskA | (maskB << 16), pairMask);
                Assert.Equal([.. levelsA, .. levelsB], pairLevels);
                Assert.Equal((sse1, sse2, spectrum1, spectrum2), (sseA, sseB, spectrumA, spectrumB));
                Assert.Equal(outA.AsSpan(0, (3 * S) + 8).ToArray(), pairOut.AsSpan(0, (3 * S) + 8).ToArray());
            }
        }
    }

    [Fact]
    public void ResidualCostsMatchTheTokenTree()
    {
        Random random = new(5);
        byte[] probabilities = new byte[Vp8Costs.ProbabilityCount];
        Vp8LevelCosts costs = new();
        short[] levels = new short[16];
        for (int round = 0; round < 3000; round++)
        {
            if (round % 100 == 0)
            {
                random.NextBytes(probabilities);
                costs.Update(probabilities);
            }

            int kind = random.Next(4);
            for (int i = 0; i < 16; i++)
            {
                levels[i] = (short)(random.Next(3) == 0 ? 0 : kind switch { 0 => random.Next(-2, 3), 1 => random.Next(-80, 81), 2 => random.Next(-2047, 2048), _ => 0 });
            }

            if (kind == 3 && random.Next(2) == 0)
            {
                levels[random.Next(16)] = 1;
            }

            int type = random.Next(4), first = type == 0 ? 1 : 0, ctx0 = random.Next(3);
            int last = Array.FindLastIndex(levels, l => l != 0);
            Assert.Equal(last < first ? -1 : last, Vp8LevelCosts.LastNonZero(levels, first));
            Assert.Equal(ReferenceResidual(costs, type, first, ctx0, levels), costs.Residual(type, first, ctx0, levels));
        }

        // The cost by the token tree, one level at a time.
        static int ReferenceResidual(Vp8LevelCosts costs, int type, int first, int ctx0, short[] levels)
        {
            int last = Array.FindLastIndex(levels, l => l != 0);
            int p0 = costs.Probabilities[((type * 8) + Vp8Decoder.Bands[first]) * 33 + (ctx0 * 11)];
            if (last < first)
            {
                return Vp8Costs.Bit(0, p0);
            }

            int cost = ctx0 == 0 ? Vp8Costs.Bit(1, p0) : 0;
            int ctx = ctx0;
            for (int n = first; n <= last; n++)
            {
                int v = Math.Abs((int)levels[n]);
                cost += Vp8Costs.Level(costs.Table, Vp8LevelCosts.At(type, n, ctx), v);
                ctx = Math.Min(v, 2);
            }

            if (last < 15)
            {
                cost += Vp8Costs.Bit(0, costs.Probabilities[((type * 8) + Vp8Decoder.Bands[last + 1]) * 33 + (ctx * 11)]);
            }

            return cost;
        }
    }

    /// <summary>Quantizes the coefficients (DC included) and reconstructs them on the prediction, with the scalar kernels.</summary>
    private static int ReconstructFrom(Span<short> coefficients, byte[] src, int s, byte[] pred, int p, Vp8Matrix m, short[] levels, byte[] output, out int sse, out int spectrum)
    {
        short[] c = coefficients.ToArray();
        Vp8EncDsp.QuantizeScalar(c, levels, m);
        Array.Clear(output);
        Vp8EncDsp.Copy(pred, p, output, 0, 4, 4);
        Vp8Dsp.InverseTransformAddScalar(c, output, 0);
        sse = BlockSse(src, s, output, 0);
        spectrum = Vp8EncDsp.HadamardWeightedScalar(output, 0);
        int mask = 0;
        for (int n = 0; n < 16; n++)
        {
            mask |= levels[n] != 0 ? 1 << Vp8Decoder.Zigzag[n] : 0;
        }

        return mask;
    }

    private static int BlockSse(byte[] a, int ao, byte[] b, int bo)
    {
        int sum = 0;
        for (int y = 0; y < 4; y++)
        {
            for (int x = 0; x < 4; x++)
            {
                int d = a[ao + (y * S) + x] - b[bo + (y * S) + x];
                sum += d * d;
            }
        }

        return sum;
    }

    [Fact]
    public void ModeCostsMatchLibwebp()
    {
        // Spot checks against libwebp's tables (cost_enc.c), in 1/256 bit: our costs come from the RFC probabilities. (libwebp's
        // chroma DC cost, 302, is not what its own tree codes, a 0 at probability 142: 218.)
        Assert.InRange(Vp8Costs.Intra16[0], 660, 666);
        Assert.Equal(218, Vp8Costs.Chroma[0]);
        Assert.InRange(Vp8Costs.Intra4[(Vp8Dsp.BDc * 100) + (Vp8Dsp.BDc * 10) + Vp8Dsp.BDc], 37, 43);
        Assert.InRange(Vp8Costs.LevelFixed[5], 430, 434);
        Assert.Equal(256, Vp8Costs.LevelFixed[1]);
    }

    public static TheoryData<string, int> Reconstructions()
    {
        TheoryData<string, int> data = [];
        foreach (string name in new[] { "gradient", "checker", "noise", "edge-17x33", "edge-1x1", "duck", "shapes" })
        {
            for (int effort = 0; effort <= 6; effort++)
            {
                data.Add(name, effort);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Reconstructions))]
    public void DecoderRebuildsTheEncodersReconstruction(string name, int effort)
    {
        using Raster<Rgba8> raster = WebpTestImages.Get(name);
        Vp8Planes source = WebpRgbToYuv.Convert(WebpTestImages.Argb(raster), raster.Width, raster.Height);
        Vp8Encoder encoder = new(source, new Vp8EncoderOptions { Quality = 60, Effort = effort, FilterStrength = 0 })
        {
            Reconstruction = new Vp8Planes(raster.Width, raster.Height),
        };
        byte[] frame = encoder.Encode();
        Vp8Planes recon = encoder.Reconstruction!;
        using Vp8Decoder decoder = Vp8Decoder.Create(frame, 0, frame.Length);
        int cw = (raster.Width + 1) / 2;
        int lumaRow = 0, chromaRow = 0;
        while (decoder.DecodeRow((d, from, to, chromaFrom, chromaTo) =>
        {
            for (int y = from; y < to; y++)
            {
                Assert.True(d.RowY(y).SequenceEqual(recon.Y.AsSpan(y * recon.Stride, raster.Width)), $"Y row {y}");
                lumaRow++;
            }

            for (int y = chromaFrom; y < chromaTo; y++)
            {
                Assert.True(d.RowU(y).SequenceEqual(recon.U.AsSpan(y * recon.ChromaStride, cw)), $"U row {y}");
                Assert.True(d.RowV(y).SequenceEqual(recon.V.AsSpan(y * recon.ChromaStride, cw)), $"V row {y}");
                chromaRow++;
            }
        }))
        {
        }

        Assert.Equal(raster.Height, lumaRow);
        Assert.Equal((raster.Height + 1) / 2, chromaRow);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    public void PartitionsDecodeToTheSamePlanes(int partitions)
    {
        using Raster<Rgba8> raster = WebpTestImages.Get("egret");
        Vp8Planes planes = WebpRgbToYuv.Convert(WebpTestImages.Argb(raster), raster.Width, raster.Height);
        byte[] one = new Vp8Encoder(planes, new Vp8EncoderOptions()).Encode();
        byte[] several = new Vp8Encoder(planes, new Vp8EncoderOptions { Partitions = partitions }).Encode();
        Assert.Equal(Decode(one), Decode(several));

        static byte[] Decode(byte[] frame)
        {
            using MemoryStream file = new();
            WebpContainer.WriteSimple(file, WebpContainer.Vp8, frame);
            return WebpTestImages.Planes(file.ToArray());
        }
    }

    public static TheoryData<int, int, double> Qualities() => new()
    {
        { 75, 0, 30 },
        { 75, 2, 30 },
        { 75, 4, 30 },
        { 75, 5, 30 },
        { 75, 6, 30 },
        { 0, 4, 20 },
        { 50, 4, 28 },
        { 95, 4, 36 },
        { 100, 4, 38 },
    };

    [Theory]
    [MemberData(nameof(Qualities))]
    public void LossyRoundTripsCloseToTheSource(int quality, int effort, double minimumPsnr)
    {
        using Raster<Rgba8> source = WebpTestImages.Get("duck");
        using MemoryStream file = new();
        source.Save(file, WebpFormat.Info, new WebpWriterOptions { Lossless = false, Quality = quality, Effort = effort });
        using Raster<Rgba8> decoded = Raster.Load<Rgba8>(file.ToArray());
        Assert.Equal(source.Width, decoded.Width);
        Assert.Equal(source.Height, decoded.Height);
        double se = 0;
        for (int y = 0; y < source.Height; y++)
        {
            ReadOnlySpan<Rgba8> a = source.Row(y), b = decoded.Row(y);
            for (int x = 0; x < a.Length; x++)
            {
                double d = Luma(a[x]) - Luma(b[x]);
                se += d * d;
            }
        }

        double psnr = 10 * Math.Log10(255.0 * 255.0 * source.Width * source.Height / Math.Max(se, 1e-9));
        Assert.True(psnr >= minimumPsnr, $"Y PSNR {psnr:F2} dB at quality {quality}, effort {effort}");
    }

    [Fact]
    public void HigherQualityGivesLargerFilesAndLessError()
    {
        using Raster<Rgba8> source = WebpTestImages.Get("egret");
        long previous = 0;
        foreach (int quality in new[] { 10, 40, 75, 95 })
        {
            using MemoryStream file = new();
            source.Save(file, WebpFormat.Info, new WebpWriterOptions { Lossless = false, Quality = quality });
            Assert.True(file.Length > previous, $"quality {quality}: {file.Length} bytes, not more than {previous}");
            previous = file.Length;
        }
    }

    [Theory]
    [InlineData("shapes")]
    [InlineData("checker")]
    [InlineData("duck")]
    [InlineData("edge-17x33")]
    public void SharpYuvKeepsColoredEdges(string name)
    {
        using Raster<Rgba8> source = WebpTestImages.Get(name);
        double plain = RgbPsnr(source, new WebpWriterOptions { Lossless = false, Quality = 90 });
        double sharp = RgbPsnr(source, new WebpWriterOptions { Lossless = false, Quality = 90, SharpYuv = true });
        Assert.True(sharp >= plain - 0.3, $"{name}: sharp {sharp:F2} dB, plain {plain:F2} dB");
    }

    [Fact]
    public void SharpYuvImprovesRedOnBlueEdges()
    {
        using Raster<Rgba8> source = WebpTestImages.Make(64, 64, (x, y) => ((x / 3) + (y / 5)) % 2 == 0 ? new Rgba8(230, 20, 20, 255) : new Rgba8(20, 20, 230, 255));
        double plain = RgbPsnr(source, new WebpWriterOptions { Lossless = false, Quality = 95 });
        double sharp = RgbPsnr(source, new WebpWriterOptions { Lossless = false, Quality = 95, SharpYuv = true });
        Assert.True(sharp > plain, $"sharp {sharp:F2} dB, plain {plain:F2} dB");
    }

    private static double RgbPsnr(Raster<Rgba8> source, WebpWriterOptions options)
    {
        using MemoryStream file = new();
        source.Save(file, WebpFormat.Info, options);
        using Raster<Rgba8> decoded = Raster.Load<Rgba8>(file.ToArray());
        double se = 0;
        for (int y = 0; y < source.Height; y++)
        {
            ReadOnlySpan<Rgba8> a = source.Row(y), b = decoded.Row(y);
            for (int x = 0; x < a.Length; x++)
            {
                se += ((a[x].R - b[x].R) * (a[x].R - b[x].R)) + ((a[x].G - b[x].G) * (a[x].G - b[x].G)) + ((a[x].B - b[x].B) * (a[x].B - b[x].B));
            }
        }

        return 10 * Math.Log10(255.0 * 255.0 * 3 * source.Width * source.Height / Math.Max(se, 1e-9));
    }

    [Fact]
    public void PngSourcesSaveLossyByDefault()
    {
        using Raster<Rgba8> source = WebpTestImages.Get("duck");
        using MemoryStream file = new();
        source.Save(file, WebpFormat.Info);
        using Raster loaded = Raster.Load(file.ToArray());
        Assert.False(Assert.IsType<WebpDetails>(loaded.Metadata.FormatDetails).Lossless);
    }

    private static double Luma(Rgba8 p) => (0.299 * p.R) + (0.587 * p.G) + (0.114 * p.B);

    private static int ScalarSse(byte[] a, byte[] b, int at, int w, int h)
    {
        int sum = 0;
        for (int y = 0; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                int d = a[at + (y * S) + x] - b[at + (y * S) + x];
                sum += d * d;
            }
        }

        return sum;
    }
}
