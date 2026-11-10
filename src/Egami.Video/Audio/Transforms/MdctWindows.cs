namespace Egami.Video.Audio.Transforms;

/// <summary>
/// MDCT windows that meet the Princen-Bradley condition, w[n]^2 + w[n + N/2]^2 = 1, so that overlapped blocks add back to their
/// input. Each is symmetric, so only its rising first half of N/2 values is returned.
/// </summary>
internal static class MdctWindows
{
    /// <summary>Gets the first half of the sine window, w[n] = sin(pi / N (n + 1/2)).</summary>
    /// <param name="length">N, the block length.</param>
    /// <returns>The N/2 rising values.</returns>
    public static float[] Sine(int length)
    {
        float[] window = new float[length / 2];
        for (int n = 0; n < window.Length; n++)
        {
            window[n] = (float)Math.Sin(Math.PI / length * (n + 0.5));
        }

        return window;
    }

    /// <summary>
    /// Gets the first half of the Kaiser-Bessel-derived window (Fielder et al., "AC-2 and AC-3: Low-Complexity Transform-Based
    /// Audio Coding", AES 1996): w[n] = sqrt(sum of the Kaiser kernel up to n / its total over N/2 + 1 points), with the kernel
    /// I0(pi alpha sqrt(1 - (4n / N - 1)^2)). A larger alpha trades a wider main lobe for more rejection far from it.
    /// </summary>
    /// <param name="length">N, the block length.</param>
    /// <param name="alpha">The Kaiser kernel's alpha.</param>
    /// <returns>The N/2 rising values.</returns>
    public static float[] KaiserBesselDerived(int length, double alpha)
    {
        int half = length / 2;
        double[] kernel = new double[half + 1];
        double total = 0;
        for (int n = 0; n <= half; n++)
        {
            double x = (2.0 * n / half) - 1;
            kernel[n] = BesselI0(Math.PI * alpha * Math.Sqrt(1 - (x * x)));
            total += kernel[n];
        }

        float[] window = new float[half];
        double sum = 0;
        for (int n = 0; n < half; n++)
        {
            sum += kernel[n];
            window[n] = (float)Math.Sqrt(sum / total);
        }

        return window;
    }

    /// <summary>The modified Bessel function of the first kind, order zero, by its power series.</summary>
    private static double BesselI0(double x)
    {
        double sum = 1;
        double term = 1;
        double q = x * x / 4;
        for (int k = 1; k < 100 && term > sum * 1e-17; k++)
        {
            term *= q / ((double)k * k);
            sum += term;
        }

        return sum;
    }
}
