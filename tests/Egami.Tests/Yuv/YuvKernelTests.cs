using Egami.Pixels;

namespace Egami.Tests.Yuv;

/// <summary>
/// Phase 2.1.4 tasks 4 and 5: every SIMD kernel gives exactly the scalar path's samples, at every width (so every remainder), for
/// every matrix, range, pixel order and depth. CI reruns these with SIMD off and with AVX-512 off.
/// </summary>
public class YuvKernelTests
{
    private static readonly YuvConvert.Order[] Orders = [new(0, 1, 2, 3, 4), new(2, 1, 0, 3, 4), new(0, 1, 2, -1, 3), new(2, 1, 0, -1, 3)];

    public static TheoryData<YuvMatrix, YuvRange> Colors() => YuvConversionTests.Colors();

    [Theory]
    [MemberData(nameof(Colors))]
    public void EightBitToRgb(YuvMatrix matrix, YuvRange range)
    {
        Random random = new(HashCode.Combine(matrix, range));
        YuvKernels8.ToRgbCoefficients k = YuvKernels8.ToRgbCoefficients.For(new YuvColor(matrix, range, ChromaSiting.Center));
        for (int width = 1; width < 80; width++)
        {
            byte[] y = Bytes(random, width), a = Bytes(random, width);
            short[] cb = new short[width], cr = new short[width];
            for (int x = 0; x < width; x++)
            {
                (cb[x], cr[x]) = ((short)random.Next(4081), (short)random.Next(4081));
            }

            foreach (YuvConvert.Order order in Orders)
            {
                foreach (byte[] alpha in new[] { a, [] })
                {
                    byte[] fast = new byte[width * order.Size], slow = new byte[width * order.Size];
                    YuvKernels8.ToRgb(y, cb, cr, alpha, fast, order, in k);
                    YuvKernels8.ToRgbScalar(y, cb, cr, alpha, slow, order, in k);
                    Assert.Equal(slow, fast);
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Colors))]
    public void EightBitFromRgb(YuvMatrix matrix, YuvRange range)
    {
        Random random = new(HashCode.Combine(matrix, range, 1));
        YuvKernels8.FromRgbCoefficients k = YuvKernels8.FromRgbCoefficients.For(new YuvColor(matrix, range, ChromaSiting.Center));
        for (int width = 1; width < 80; width++)
        {
            foreach (YuvConvert.Order order in Orders)
            {
                byte[] rgb = Bytes(random, width * order.Size);
                byte[] fastY = new byte[width], slowY = new byte[width];
                int[] fastB = new int[width], fastR = new int[width], slowB = new int[width], slowR = new int[width];
                YuvKernels8.FromRgb(rgb, order, fastY, fastB, fastR, width, in k);
                YuvKernels8.FromRgbScalar(rgb, order, slowY, slowB, slowR, width, in k);
                Assert.Equal(slowY, fastY);
                Assert.Equal(slowB, fastB);
                Assert.Equal(slowR, fastR);

                // Luma only, and chroma only.
                YuvKernels8.FromRgb(rgb, order, fastY, default, default, width, in k);
                Assert.Equal(slowY, fastY);
                YuvKernels8.FromRgb(rgb, order, default, fastB, fastR, width, in k);
                Assert.Equal(slowB, fastB);
            }
        }
    }

    [Theory]
    [MemberData(nameof(Colors))]
    public void FloatToRgb(YuvMatrix matrix, YuvRange range)
    {
        foreach (int depth in new[] { 8, 10, 12, 16 })
        {
            Random random = new(HashCode.Combine(matrix, range, depth));
            int max = (1 << depth) - 1;
            foreach (int outMax in new[] { 255, 65535 })
            {
                YuvKernelsFloat.ToRgbCoefficients k = YuvKernelsFloat.ToRgbCoefficients.For(new YuvColor(matrix, range, ChromaSiting.Center), depth, outMax);
                for (int width = 1; width < 40; width++)
                {
                    int[] cb = new int[width], cr = new int[width];
                    for (int x = 0; x < width; x++)
                    {
                        (cb[x], cr[x]) = (random.Next((16 * max) + 1), random.Next((16 * max) + 1));
                    }

                    if (depth == 8)
                    {
                        byte[] y = Bytes(random, width), a = Bytes(random, width);
                        Check(y, a);
                    }
                    else
                    {
                        // Samples above the depth's maximum must clamp the same way on both paths.
                        ushort[] y = [.. Enumerable.Range(0, width).Select(_ => (ushort)random.Next(max + 40))];
                        ushort[] a = [.. Enumerable.Range(0, width).Select(_ => (ushort)random.Next(max + 1))];
                        Check(y, a);
                    }

                    void Check<TSample>(TSample[] y, TSample[] a)
                        where TSample : unmanaged
                    {
                        foreach (YuvConvert.Order order in outMax == 255 ? Orders : [Orders[0]])
                        {
                            foreach (TSample[] alpha in new[] { a, [] })
                            {
                                if (outMax == 255)
                                {
                                    byte[] fast = new byte[width * order.Size], slow = new byte[width * order.Size];
                                    YuvKernelsFloat.ToRgb<TSample>(y, cb, cr, alpha, max, fast, order, in k);
                                    YuvKernelsFloat.ToRgbScalar<TSample, byte>(y, cb, cr, alpha, max, slow, order, in k);
                                    Assert.Equal(slow, fast);
                                }
                                else
                                {
                                    ushort[] fast = new ushort[width * 4], slow = new ushort[width * 4];
                                    YuvKernelsFloat.ToRgb<TSample>(y, cb, cr, alpha, max, fast, order, in k);
                                    YuvKernelsFloat.ToRgbScalar<TSample, ushort>(y, cb, cr, alpha, max, slow, order, in k);
                                    Assert.Equal(slow, fast);
                                }
                            }
                        }
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Colors))]
    public void FloatFromRgb(YuvMatrix matrix, YuvRange range)
    {
        foreach (int depth in new[] { 8, 10, 12, 16 })
        {
            Random random = new(HashCode.Combine(matrix, range, depth, 2));
            int max = (1 << depth) - 1;
            foreach (int inMax in new[] { 255, 65535 })
            {
                YuvKernelsFloat.FromRgbCoefficients k = YuvKernelsFloat.FromRgbCoefficients.For(new YuvColor(matrix, range, ChromaSiting.Center), depth, inMax);
                for (int width = 1; width < 40; width++)
                {
                    foreach (YuvConvert.Order order in inMax == 255 ? Orders : [Orders[0]])
                    {
                        float[] fastB = new float[width], fastR = new float[width], slowB = new float[width], slowR = new float[width];
                        ushort[] fastY = new ushort[width], slowY = new ushort[width];
                        if (inMax == 255)
                        {
                            byte[] rgb = Bytes(random, width * order.Size);
                            YuvKernelsFloat.FromRgb<ushort>(rgb, order, fastY, fastB, fastR, width, max, in k);
                            YuvKernelsFloat.FromRgbScalar<byte, ushort>(rgb, order, slowY, slowB, slowR, width, max, in k);
                        }
                        else
                        {
                            ushort[] rgb = [.. Enumerable.Range(0, width * 4).Select(_ => (ushort)random.Next(65536))];
                            YuvKernelsFloat.FromRgb<ushort>(rgb, order, fastY, fastB, fastR, width, max, in k);
                            YuvKernelsFloat.FromRgbScalar<ushort, ushort>(rgb, order, slowY, slowB, slowR, width, max, in k);
                        }

                        Assert.Equal(slowY, fastY);
                        Assert.Equal(slowB, fastB);
                        Assert.Equal(slowR, fastR);
                    }
                }
            }
        }
    }

    [Theory]
    [MemberData(nameof(Colors))]
    public void BoxesOfRgbSumsGiveTheAveragedChroma(YuvMatrix matrix, YuvRange range)
    {
        if (matrix == YuvMatrix.Identity)
        {
            return;
        }

        Random random = new(HashCode.Combine(matrix, range, 5));
        YuvKernels8.FromRgbCoefficients k = YuvKernels8.FromRgbCoefficients.For(new YuvColor(matrix, range, ChromaSiting.Center));
        for (int width = 1; width < 70; width++)
        {
            foreach (YuvConvert.Order order in Orders[..2])
            {
                byte[] top = Bytes(random, width * 4), bottom = Bytes(random, width * 4);
                int chroma = (width + 1) / 2;
                foreach (bool twoRows in new[] { true, false })
                {
                    byte[] u = new byte[chroma], v = new byte[chroma];
                    YuvKernels8.BoxFromRgb(top, twoRows ? bottom : top, twoRows, order, u, v, width, in k);

                    // The definition: Q15 chroma per pixel, summed over the box, rounded once.
                    int[] cb0 = new int[width], cr0 = new int[width], cb1 = new int[width], cr1 = new int[width];
                    YuvKernels8.FromRgbScalar(top, order, default, cb0, cr0, width, in k);
                    YuvKernels8.FromRgbScalar(bottom, order, default, cb1, cr1, width, in k);
                    int shift = twoRows ? 17 : 16;
                    for (int i = 0; i < chroma; i++)
                    {
                        int a = 2 * i, b = Math.Min((2 * i) + 1, width - 1);
                        int su = cb0[a] + cb0[b] + (twoRows ? cb1[a] + cb1[b] : 0), sv = cr0[a] + cr0[b] + (twoRows ? cr1[a] + cr1[b] : 0);
                        Assert.Equal(Math.Clamp(((su + (1 << (shift - 1))) >> shift) + 128, 0, 255), u[i]);
                        Assert.Equal(Math.Clamp(((sv + (1 << (shift - 1))) >> shift) + 128, 0, 255), v[i]);
                    }
                }
            }
        }
    }

    [Fact]
    public void EightBitUpsampling()
    {
        Random random = new(3);
        for (int chroma = 1; chroma < 60; chroma++)
        {
            byte[] a = Bytes(random, chroma), b = Bytes(random, chroma);
            short[] t = new short[chroma];
            YuvKernels8.UpsampleVertical(a, b, 3, 1, t);
            Assert.Equal(a.Zip(b, (p, q) => (short)((3 * p) + q)), t);
            int[] wide = [.. t.Select(v => (int)v)];
            foreach (ChromaFormat format in new[] { ChromaFormat.Yuv420, ChromaFormat.Yuv444 })
            {
                foreach (ChromaSiting siting in Enum.GetValues<ChromaSiting>())
                {
                    foreach (ChromaUpsampling mode in Enum.GetValues<ChromaUpsampling>())
                    {
                        foreach (int width in format == ChromaFormat.Yuv444 ? [chroma] : new[] { (2 * chroma) - 1, 2 * chroma })
                        {
                            if (width < 1)
                            {
                                continue;
                            }

                            short[] fast = new short[width];
                            int[] slow = new int[width];
                            YuvKernels8.UpsampleHorizontal(t, fast, format, siting, mode);
                            YuvConvert.Upsample.Horizontal(wide, chroma, slow, format, siting, mode);
                            Assert.Equal(slow, fast.Select(v => (int)v));
                        }
                    }
                }
            }
        }
    }

    [Fact]
    public void WideResampling()
    {
        Random random = new(4);
        for (int chroma = 1; chroma < 40; chroma++)
        {
            int[] t = [.. Enumerable.Range(0, chroma).Select(_ => random.Next(4 * 65536))];
            foreach (ChromaSiting siting in Enum.GetValues<ChromaSiting>())
            {
                foreach (ChromaUpsampling mode in Enum.GetValues<ChromaUpsampling>())
                {
                    foreach (int width in new[] { (2 * chroma) - 1, 2 * chroma })
                    {
                        if (width < 1)
                        {
                            continue;
                        }

                        int[] actual = new int[width];
                        YuvConvert.Upsample.Horizontal(t, chroma, actual, ChromaFormat.Yuv420, siting, mode);
                        for (int x = 0; x < width; x++)
                        {
                            int i = x / 2, l = Math.Max(i - 1, 0), r = Math.Min(i + 1, chroma - 1);
                            int expected = mode == ChromaUpsampling.Nearest ? 4 * t[i]
                                : siting == ChromaSiting.Center ? (3 * t[i]) + t[x % 2 == 0 ? l : r]
                                : x % 2 == 0 ? 4 * t[i] : (2 * t[i]) + (2 * t[r]);
                            Assert.Equal(expected, actual[x]);
                        }
                    }
                }
            }
        }

        for (int width = 1; width < 60; width++)
        {
            int[] row = [.. Enumerable.Range(0, width).Select(_ => random.Next(-4_000_000, 4_000_000))];
            float[] rowF = [.. row.Select(v => v / 1000f)];
            int chroma = (width + 1) / 2;
            foreach (ChromaSiting siting in Enum.GetValues<ChromaSiting>())
            {
                foreach (bool fast in new[] { false, true })
                {
                    int[] sum = new int[chroma];
                    float[] sumF = new float[chroma];
                    YuvKernelsFloat.FilterRow(row, 3, sum, ChromaFormat.Yuv420, siting, fast);
                    YuvKernelsFloat.FilterRow(rowF, 3, sumF, ChromaFormat.Yuv420, siting, fast);
                    for (int i = 0; i < chroma; i++)
                    {
                        int a = Math.Max((2 * i) - 1, 0), b = 2 * i, c = Math.Min((2 * i) + 1, width - 1);
                        int expected = 3 * (fast ? row[b] : siting == ChromaSiting.Center ? row[b] + row[c] : row[a] + (2 * row[b]) + row[c]);
                        float expectedF = 3 * (fast ? rowF[b] : siting == ChromaSiting.Center ? rowF[b] + rowF[c] : rowF[a] + (2 * rowF[b]) + rowF[c]);
                        Assert.Equal(expected, sum[i]);
                        Assert.Equal(expectedF, sumF[i]);
                    }
                }
            }
        }
    }

    private static byte[] Bytes(Random random, int count)
    {
        byte[] bytes = new byte[count];
        random.NextBytes(bytes);
        return bytes;
    }
}
