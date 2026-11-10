using Egami.Video.Audio.Codecs.Ac3;
using Egami.Video.Audio.Transforms;

namespace Egami.Video.Tests.Audio;

/// <summary>
/// The shared MDCT (V5.10, for AAC): both directions against the defining sums in double precision, perfect reconstruction with
/// sine and Kaiser-Bessel-derived windows, and the windows' Princen-Bradley condition.
/// </summary>
public class MdctTests
{
    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(2048)]
    public void ForwardMatchesTheDefinition(int n)
    {
        float[] x = Signal(n, seed: n);
        float[] ours = new float[n / 2];
        new Mdct(n).Forward(x, ours);
        double[] expected = new double[n / 2];
        double n0 = ((n / 2.0) + 1) / 2;
        for (int k = 0; k < n / 2; k++)
        {
            for (int i = 0; i < n; i++)
            {
                expected[k] += 2 * x[i] * Math.Cos(2 * Math.PI / n * (i + n0) * (k + 0.5));
            }
        }

        AssertClose(expected, ours, 4e-6 * n);
    }

    [Theory]
    [InlineData(16)]
    [InlineData(64)]
    [InlineData(256)]
    [InlineData(2048)]
    public void InverseMatchesTheDefinition(int n)
    {
        float[] spectrum = Signal(n / 2, seed: n + 1);
        float[] ours = new float[n];
        new Mdct(n).Inverse(spectrum, ours);
        double[] expected = new double[n];
        double n0 = ((n / 2.0) + 1) / 2;
        for (int i = 0; i < n; i++)
        {
            for (int k = 0; k < n / 2; k++)
            {
                expected[i] += 2.0 / n * spectrum[k] * Math.Cos(2 * Math.PI / n * (i + n0) * (k + 0.5));
            }
        }

        AssertClose(expected, ours, 1e-6);
    }

    [Theory]
    [InlineData(2048, "sine")]
    [InlineData(2048, "kbd4")]
    [InlineData(256, "sine")]
    [InlineData(256, "kbd6")]
    [InlineData(512, "kbd5")]
    public void ReconstructsItsInputWithOverlapAdd(int n, string window)
    {
        float[] w = window switch
        {
            "sine" => MdctWindows.Sine(n),
            "kbd4" => MdctWindows.KaiserBesselDerived(n, 4),
            "kbd5" => MdctWindows.KaiserBesselDerived(n, 5),
            _ => MdctWindows.KaiserBesselDerived(n, 6),
        };
        int half = n / 2;
        const int Blocks = 12;
        float[] input = Signal(half * (Blocks + 1), seed: 7);
        float[] output = new float[input.Length];
        Mdct mdct = new(n);
        float[] block = new float[n], windowed = new float[n], spectrum = new float[half], overlap = new float[half];
        for (int b = 0; b < Blocks; b++)
        {
            for (int i = 0; i < half; i++)
            {
                windowed[i] = input[(b * half) + i] * w[i];
                windowed[half + i] = input[(b * half) + half + i] * w[half - 1 - i];
            }

            mdct.Forward(windowed, spectrum);
            mdct.InverseOverlap(spectrum, w, w, overlap, output.AsSpan(b * half, half), block);
        }

        // Every half block after the first has both of its overlapping blocks.
        double worst = 0;
        for (int i = half; i < half * Blocks; i++)
        {
            worst = Math.Max(worst, Math.Abs(output[i] - input[i]));
        }

        Assert.True(worst < 2e-6, $"Largest error {worst}");
    }

    [Fact]
    public void InverseOverlapWindowsBothHalves()
    {
        const int N = 64;
        Mdct mdct = new(N);
        float[] spectrum = Signal(N / 2, seed: 3);
        float[] rising = MdctWindows.Sine(N), falling = MdctWindows.KaiserBesselDerived(N, 4);
        float[] previous = Signal(N / 2, seed: 4);
        float[] overlap = [.. previous];
        float[] output = new float[N / 2], block = new float[N], plain = new float[N];
        mdct.InverseOverlap(spectrum, rising, falling, overlap, output, block);
        mdct.Inverse(spectrum, plain);
        for (int i = 0; i < N / 2; i++)
        {
            Assert.Equal(previous[i] + (plain[i] * rising[i]), output[i], 1e-6f);
            Assert.Equal(plain[(N / 2) + i] * falling[(N / 2) - 1 - i], overlap[i], 1e-6f);
        }
    }

    [Theory]
    [InlineData(2048)]
    [InlineData(256)]
    public void WindowsMeetThePrincenBradleyCondition(int n)
    {
        foreach (float[] w in new[] { MdctWindows.Sine(n), MdctWindows.KaiserBesselDerived(n, 4), MdctWindows.KaiserBesselDerived(n, 6) })
        {
            Assert.Equal(n / 2, w.Length);
            for (int i = 0; i < n / 2; i++)
            {
                // The second half mirrors the first, so w[i + N/2] = w[N/2 - 1 - i].
                Assert.Equal(1.0, (w[i] * (double)w[i]) + (w[(n / 2) - 1 - i] * (double)w[(n / 2) - 1 - i]), 1e-6);
            }
        }
    }

    [Fact]
    public void KaiserBesselDerivedMatchesAc3sTable()
    {
        // ATSC A/52:2018 Table 7.33 prints the 512-point, alpha 5 window to five decimals; AC-3 takes its window from here.
        float[] printed = [0.00014f, 0.00024f, 0.00037f, 0.00051f, 0.00067f, 0.00086f, 0.00107f, 0.00130f];
        float[] w = MdctWindows.KaiserBesselDerived(512, 5);
        for (int i = 0; i < printed.Length; i++)
        {
            Assert.Equal(printed[i], w[i], 0.000005f);
        }

        Assert.Equal(1f, w[255], 0.000005f);
        Assert.Equal(w, Ac3Tables.Window);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(100)]
    [InlineData(0)]
    public void RefusesOtherLengths(int n) => Assert.Throws<ArgumentOutOfRangeException>(() => new Mdct(n));

    private static float[] Signal(int length, int seed)
    {
        Random random = new(seed);
        float[] x = new float[length];
        for (int i = 0; i < length; i++)
        {
            x[i] = (float)((random.NextDouble() * 2) - 1);
        }

        return x;
    }

    private static void AssertClose(double[] expected, float[] actual, double tolerance)
    {
        double worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        }

        Assert.True(worst < tolerance, $"Largest error {worst}, allowed {tolerance}");
    }
}
