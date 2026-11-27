using System.Runtime.Intrinsics;

using Egami.Formats.Jxl;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Roadmap 2.4.13: the 128-bit JPEG XL decode kernels (Arm64, and x86 without AVX2) give the scalar code's samples bit for bit:
/// the DCTs at every size they take, dequantization, chroma from luma, XYB to linear sRGB, the sRGB curve and the 8-bit output.
/// </summary>
public class JxlSimdTests
{
    private static readonly float[] Opsin =
    [
        11.031567f, -9.866944f, -0.16462299f, -3.2541473f, 4.4187703f, -0.16462299f, -3.6588513f, 2.7129230f, 1.9459282f,
        -0.0037930732f, -0.0037930732f, -0.0037930732f,
    ];

    public static TheoryData<int, int> Sizes
    {
        get
        {
            TheoryData<int, int> data = [];
            foreach (int w in (int[])[4, 8, 16, 32, 64, 128, 256])
            {
                foreach (int h in (int[])[4, 8, 16, 32, 64, 128, 256])
                {
                    if ((w <= 8 && h <= 8) || (w >= 8 && h >= 8))
                    {
                        data.Add(w, h);
                    }
                }
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Dct128EqualsScalar(int width, int height)
    {
        Assert.SkipUnless(JxlDct.Lanes4.Supported, "128-bit vectors are not available.");
        Random random = new((width * 1000) + height);
        int stride = width + 8;
        float[] samples = new float[(stride * height) + 8];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = (float)((random.NextDouble() * 2) - 1);
        }

        foreach (bool forward in (bool[])[true, false])
        {
            float[] expected = (float[])samples.Clone();
            float[] actual = (float[])samples.Clone();
            JxlDct.Dct2DScalar(expected, 3, stride, width, height, forward);
            bool done = width <= 8 && height <= 8 && !(width == 8 && height == 8)
                ? JxlDct.Lanes4.Dct2DSmall(actual, 3, stride, width, height, forward)
                : JxlDct.Lanes4.Dct2DVector(samples.AsSpan(3), stride, actual.AsSpan(3), stride, width, height, forward);
            Assert.True(done);
            Assert.Equal(expected, actual);
        }

        if (width == 8 && height == 8)
        {
            float[] expected = (float[])samples.Clone();
            float[] actual = (float[])samples.Clone();
            JxlDct.Dct2DScalar(expected, 3, stride, 8, 8, forward: false);
            JxlDct.Lanes4.Idct8x8(actual, 3, stride);
            Assert.Equal(expected, actual);
        }
        else if (width >= 8 && height >= 8)
        {
            // The transposed-coefficient entry the AC strategy search uses, and the tile transpose itself.
            float[] columns = new float[width * height];
            JxlDct.TransposeBlock(samples.AsSpan(3), stride, columns, height, height, width);
            float[] viaLanes = new float[width * height];
            JxlDct.Lanes4.TransposeBlock(samples.AsSpan(3), stride, viaLanes, height, height, width);
            for (int y = 0; y < height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Assert.Equal(samples[3 + (y * stride) + x], viaLanes[(x * height) + y]);
                }
            }

            float[] expected = new float[stride * height];
            for (int y = 0; y < height; y++)
            {
                Array.Copy(samples, 3 + (y * stride), expected, y * stride, width);
            }

            JxlDct.Dct2DScalar(expected, 0, stride, width, height, forward: false);
            float[] actual = new float[stride * height];
            Assert.Equal(!(width == 8 && height == 8), JxlDct.Lanes4.InverseTransposed(viaLanes, actual, stride, width, height));
            for (int y = 0; y < height; y++)
            {
                Assert.Equal(expected.AsSpan(y * stride, width).ToArray(), actual.AsSpan(y * stride, width).ToArray());
            }
        }
    }

    [Fact]
    public void Dequantization128EqualsScalar()
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated, "128-bit vectors are not available.");
        Random random = new(3);
        int[] coeffs = new int[256];
        float[] matrix = new float[256];
        for (int i = 0; i < coeffs.Length; i++)
        {
            coeffs[i] = i % 5 == 0 ? random.Next(-3, 4) : random.Next(-2000, 2001);
            matrix[i] = (float)random.NextDouble() * 4;
        }

        foreach ((float bias, float numerator, float mul) in ((float, float, float)[])[(0.9f, 0.145f, 1.3f), (0.65f, 0.2f, 0.003f), (1, 0, 1)])
        {
            float[] expected = new float[coeffs.Length];
            for (int x = 0; x < coeffs.Length; x++)
            {
                float q = coeffs[x];
                q = MathF.Abs(q) <= 1 ? q * bias : q - (numerator / q);
                expected[x] = q * matrix[x] * mul;
            }

            float[] actual = new float[coeffs.Length];
            Assert.Equal(coeffs.Length, JxlVarDct.DequantizeRow128(coeffs, matrix, actual, bias, numerator, mul));
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void ChromaFromLuma128EqualsScalar()
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated, "128-bit vectors are not available.");
        Random random = new(4);
        float[] x = [.. Enumerable.Range(0, 67).Select(_ => (float)random.NextDouble() - 0.5f)];
        float[] y = [.. Enumerable.Range(0, 67).Select(_ => (float)random.NextDouble())];
        float[] b = [.. Enumerable.Range(0, 67).Select(_ => (float)random.NextDouble() - 0.5f)];
        (float kx, float kb) = (-0.0123f, 0.937f);
        float[] ex = [.. x.Select((v, i) => v + (kx * y[i]))];
        float[] eb = [.. b.Select((v, i) => v + (kb * y[i]))];
        int done = JxlVarDct.ChromaFromLumaRow128(x, y, b, kx, kb);
        Assert.Equal(64, done);
        Assert.Equal(ex[..64], x[..64]);
        Assert.Equal(eb[..64], b[..64]);
    }

    [Fact]
    public void XybAndSrgbCurve128EqualScalar()
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated && (System.Runtime.Intrinsics.X86.Fma.IsSupported || System.Runtime.Intrinsics.Arm.AdvSimd.IsSupported), "128-bit FMA is not available.");
        (float[] px, float[] py, float[] pb) = Xyb(1003, 5);
        float c0 = MathF.Cbrt(Opsin[9]), c1 = MathF.Cbrt(Opsin[10]), c2 = MathF.Cbrt(Opsin[11]);
        float scale = 255 / 255f;
        float[] x = (float[])px.Clone(), y = (float[])py.Clone(), b = (float[])pb.Clone();
        int done = JxlColor.XybToLinear128(x, y, b, 0, px.Length, Opsin, c0, c1, c2, scale);
        Assert.Equal(1000, done);
        float[] m = Opsin;
        for (int i = 0; i < done; i++)
        {
            float gl = py[i] + px[i] - c0;
            float gm = py[i] - px[i] - c1;
            float gs = pb[i] - c2;
            float l = MathF.FusedMultiplyAdd(gl * gl, gl, m[9]) * scale;
            float mm = MathF.FusedMultiplyAdd(gm * gm, gm, m[10]) * scale;
            float s = MathF.FusedMultiplyAdd(gs * gs, gs, m[11]) * scale;
            Assert.Equal((m[0] * l) + (m[1] * mm) + (m[2] * s), x[i]);
            Assert.Equal((m[3] * l) + (m[4] * mm) + (m[5] * s), y[i]);
            Assert.Equal((m[6] * l) + (m[7] * mm) + (m[8] * s), b[i]);
        }

        // The sRGB curve on both signs, both segments and the edges between them.
        float[] linear = [.. x[..1000], 0, -0f, 0.0031308f, -0.0031308f, 0.00313081f, 1, -1, 2, 1e-30f, float.Epsilon];
        JxlColorEncoding srgb = new() { TransferFunction = 13 };
        for (int i = 0; i + 4 <= linear.Length; i += 4)
        {
            float[] curve = new float[4];
            JxlColor.EncodeSrgb4(Vector128.Create(linear.AsSpan(i, 4))).CopyTo(curve);
            for (int k = 0; k < 4; k++)
            {
                // One sample at a time: the scalar loop.
                float[] reference = [linear[i + k]];
                JxlColor.Encode(reference, srgb, 255);
                Assert.Equal(reference[0], curve[k]);
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void Bytes128EqualScalar(int samples)
    {
        Assert.SkipUnless(Vector128.IsHardwareAccelerated, "128-bit vectors are not available.");
        Random random = new(samples);
        foreach (int width in (int[])[8, 9, 15, 16, 31, 100])
        {
            float[][] color = [.. Enumerable.Range(0, 3).Select(_ => Enumerable.Range(0, width + 5).Select(_ => (float)((random.NextDouble() * 1.4) - 0.2)).ToArray())];
            color[0][5] = 0.5f / 255;
            color[0][6] = 1.5f / 255;
            color[0][7] = float.NaN;
            byte[] expected = new byte[width * samples];
            byte[] actual = new byte[width * samples];
            JxlGroupPipeline.WriteBytesTail(expected, color, 2, samples, 0);
            JxlGroupPipeline.WriteBytes128(actual, color, 2, samples);
            Assert.Equal(expected, actual);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(3)]
    [InlineData(4)]
    public void SrgbBytes128EqualTheAvx2Ones(int samples)
    {
        Assert.SkipUnless(JxlColor.Fused256 && System.Runtime.Intrinsics.X86.Avx2.IsSupported, "AVX2 and FMA are not available.");
        JxlXyb8 xyb8 = new(Opsin, 255);
        JxlXyb4 xyb4 = new(Opsin, 255);
        foreach (int width in (int[])[8, 9, 15, 16, 31, 100])
        {
            (float[] x, float[] y, float[] b) = Xyb(width + 3, width);
            byte[] expected = new byte[width * samples];
            byte[] actual = new byte[width * samples];
            JxlGroupPipeline.WriteSrgbBytes(expected, [x, y, b], 3, samples, xyb8, null!);
            JxlGroupPipeline.WriteSrgbBytes128(actual, [x, y, b], 3, samples, xyb4, null!);
            Assert.Equal(expected, actual);
        }
    }

    /// <summary>XYB samples in the range real images take: X about ±0.03, Y 0 to 0.85, B 0 to 0.9.</summary>
    private static (float[] X, float[] Y, float[] B) Xyb(int count, int seed)
    {
        Random random = new(seed);
        float[] x = new float[count], y = new float[count], b = new float[count];
        for (int i = 0; i < count; i++)
        {
            x[i] = (float)((random.NextDouble() * 0.06) - 0.03);
            y[i] = (float)(random.NextDouble() * 0.85);
            b[i] = (float)(random.NextDouble() * 0.9);
        }

        return (x, y, b);
    }
}
