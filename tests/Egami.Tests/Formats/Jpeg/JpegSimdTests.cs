using System.Runtime.Intrinsics.X86;

using Egami.Formats.Jpeg;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>The vector paths of the JPEG decoder must give exactly what the scalar ones give, for any input.</summary>
public class JpegSimdTests
{
    public static TheoryData<string> Kinds() => ["typical", "sparse", "dc-only", "flat-columns", "extreme", "random"];

    [Theory]
    [MemberData(nameof(Kinds))]
    public void IdctVectorEqualsScalar(string kind)
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");
        Random random = new(kind.GetHashCode(StringComparison.Ordinal) & 0xFFFF);
        short[] block = new short[64];
        ushort[] quant = new ushort[64];
        byte[] scalar = new byte[8 * 11];
        byte[] vector = new byte[8 * 11];
        for (int trial = 0; trial < 5000; trial++)
        {
            Fill(kind, random, block, quant);
            Array.Clear(scalar);
            Array.Clear(vector);
            JpegIdct.TransformScalar(block, quant, scalar, 11);
            JpegIdct.TransformAvx2(block, quant, vector, 11);
            Assert.Equal(scalar, vector);
        }
    }

    [Fact]
    public void DcFillEqualsTheTransform()
    {
        short[] block = new short[64];
        byte[] filled = new byte[64];
        byte[] transformed = new byte[64];
        foreach (int dc in (int[])[-32768, -2048, -1024, -1, 0, 1, 7, 1023, 2047, 32767])
        {
            foreach (ushort q in (ushort[])[1, 2, 16, 255, 65535])
            {
                block[0] = (short)dc;
                ushort[] quant = Enumerable.Repeat(q, 64).ToArray();
                JpegIdct.TransformScalar(block, quant, transformed, 8);
                JpegIdct.FillDc(dc * q, filled, 8);
                Assert.Equal(transformed, filled);
            }
        }
    }

    [Fact]
    public void ColorVectorEqualsScalar()
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");
        Random random = new(7);
        foreach (int width in (int[])[1, 15, 16, 17, 33, 100, 1000])
        {
            byte[] y = new byte[width], cb = new byte[width], cr = new byte[width];
            random.NextBytes(y);
            random.NextBytes(cb);
            random.NextBytes(cr);

            // Every corner of the cube too, where the clamps act.
            for (int i = 0; i < Math.Min(width, 8); i++)
            {
                (y[i], cb[i], cr[i]) = ((byte)((i & 1) * 255), (byte)(((i >> 1) & 1) * 255), (byte)(((i >> 2) & 1) * 255));
            }

            byte[] scalar = new byte[width * 3], vector = new byte[width * 3];
            JpegColor.YCbCrScalar(y, cb, cr, scalar, 0);
            int done = JpegColor.YCbCrAvx2(y, cb, cr, vector);
            JpegColor.YCbCrScalar(y, cb, cr, vector, done);
            Assert.Equal(scalar, vector);
        }
    }

    [Fact]
    public void UpsamplingVectorEqualsScalar()
    {
        Assert.SkipUnless(Avx2.IsSupported, "AVX2 is not available.");
        Random random = new(11);
        foreach (int n in (int[])[1, 2, 15, 16, 17, 31, 40, 333])
        {
            byte[] near = new byte[n + 32], far = new byte[n + 32];
            random.NextBytes(near);
            random.NextBytes(far);
            short[] sums = new short[n + 2];
            foreach (bool odd in (bool[])[false, true])
            {
                int width = (2 * n) - (odd ? 1 : 0);
                JpegUpsample.ColumnSums(near, far, n, sums);
                for (int i = 0; i < n; i++)
                {
                    Assert.Equal((3 * near[i]) + far[i], sums[i + 1]);
                }

                byte[] scalar = new byte[width], vector = new byte[width];
                JpegUpsample.DoubleScalar(sums, 0, n, scalar, 8, 7, 4);
                JpegUpsample.Double(sums, n, vector, 8, 7, 4);
                Assert.Equal(scalar, vector);

                JpegUpsample.Widen(near, n, sums);
                JpegUpsample.DoubleScalar(sums, 0, n, scalar, 1, 2, 2);
                JpegUpsample.Double(sums, n, vector, 1, 2, 2);
                Assert.Equal(scalar, vector);

                JpegUpsample.Replicate(near, 2, vector);
                Assert.Equal(Enumerable.Range(0, width).Select(x => near[x / 2]), vector);
            }

            byte[] vertical = new byte[n];
            JpegUpsample.Vertical(near.AsSpan(0, n), far.AsSpan(0, n), vertical, 1);
            Assert.Equal(Enumerable.Range(0, n).Select(x => (byte)(((3 * near[x]) + far[x] + 1) >> 2)), vertical);
        }
    }

    private static void Fill(string kind, Random random, short[] block, ushort[] quant)
    {
        Array.Clear(block);
        for (int i = 0; i < 64; i++)
        {
            quant[i] = kind switch
            {
                "extreme" => (ushort)random.Next(1, 65536),
                "random" => (ushort)random.Next(1, 256),
                _ => (ushort)(1 + (i / 4)),
            };
        }

        switch (kind)
        {
            case "typical":
                // Energy falls off with frequency, as in real images.
                for (int i = 0; i < 64; i++)
                {
                    int limit = 1024 >> Math.Min(10, (i % 8) + (i / 8));
                    block[i] = (short)random.Next(-limit, limit + 1);
                }

                break;
            case "sparse":
                for (int n = random.Next(1, 6); n > 0; n--)
                {
                    block[random.Next(64)] = (short)random.Next(-300, 301);
                }

                break;
            case "dc-only":
                block[0] = (short)random.Next(-1024, 1024);
                break;
            case "flat-columns":
                // Some columns or rows hold only a DC term, which the scalar code shortcuts.
                for (int c = 0; c < 8; c++)
                {
                    block[c] = (short)random.Next(-500, 500);
                    if (random.Next(2) == 0)
                    {
                        block[(8 * random.Next(1, 8)) + c] = (short)random.Next(-200, 200);
                    }
                }

                break;
            default:
                for (int i = 0; i < 64; i++)
                {
                    block[i] = (short)random.Next(short.MinValue, short.MaxValue + 1);
                }

                break;
        }
    }
}
