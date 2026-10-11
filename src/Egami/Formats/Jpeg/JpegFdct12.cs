namespace Egami.Formats.Jpeg;

/// <summary>
/// The forward DCT for 12-bit samples (T.81 A.3.3), in double precision: rows then columns with the cosine matrix, and
/// quantization with rounding to nearest. The 8-bit encoder's fixed-point transform packs 16-bit lanes, which 12-bit samples would
/// overflow; 12-bit files are rare enough that the plain separable form is fast enough.
/// </summary>
internal static class JpegFdct12
{
    /// <summary>The largest coefficient magnitude 12-bit Huffman coding takes: 15 bits for DC differences, 14 for AC (T.81 F.1.2).</summary>
    private const int MaxMagnitude = (1 << 14) - 1;

    /// <summary>Basis[u * 8 + x] = C(u) / 2 × cos((2x + 1)uπ / 16), so one pass over rows and one over columns give the 2-D DCT.</summary>
    private static readonly double[] Basis = CreateBasis();

    /// <summary>
    /// Transforms 8 rows of 8 samples (0 to 4095, <paramref name="stride"/> apart) and quantizes them with <paramref name="quant"/>
    /// (natural order) into <paramref name="output"/>, in natural order too.
    /// </summary>
    internal static void Forward(ReadOnlySpan<ushort> samples, int stride, ReadOnlySpan<ushort> quant, Span<short> output)
    {
        Span<double> rows = stackalloc double[64];
        ReadOnlySpan<double> basis = Basis;
        for (int y = 0; y < 8; y++)
        {
            ReadOnlySpan<ushort> s = samples.Slice(y * stride, 8);
            for (int u = 0; u < 8; u++)
            {
                double sum = 0;
                for (int x = 0; x < 8; x++)
                {
                    sum += basis[(u * 8) + x] * (s[x] - 2048);
                }

                rows[(y * 8) + u] = sum;
            }
        }

        for (int u = 0; u < 8; u++)
        {
            for (int v = 0; v < 8; v++)
            {
                double sum = 0;
                for (int y = 0; y < 8; y++)
                {
                    sum += basis[(v * 8) + y] * rows[(y * 8) + u];
                }

                int n = (v * 8) + u;
                int value = (int)Math.Round(sum / quant[n], MidpointRounding.AwayFromZero);
                output[n] = (short)Math.Clamp(value, -MaxMagnitude, MaxMagnitude);
            }
        }
    }

    private static double[] CreateBasis()
    {
        double[] basis = new double[64];
        for (int u = 0; u < 8; u++)
        {
            double scale = u == 0 ? Math.Sqrt(0.5) / 2 : 0.5;
            for (int x = 0; x < 8; x++)
            {
                basis[(u * 8) + x] = scale * Math.Cos(((2 * x) + 1) * u * Math.PI / 16);
            }
        }

        return basis;
    }
}
