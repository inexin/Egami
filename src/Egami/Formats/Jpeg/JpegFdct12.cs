using System.Runtime.Intrinsics;

namespace Egami.Formats.Jpeg;

/// <summary>
/// The forward DCT for 12-bit samples (T.81 A.3.3), in single precision: rows then columns with the cosine matrix, eight
/// frequencies at a time in one vector, and quantization with rounding to nearest. The 8-bit encoder's fixed-point transform packs
/// 16-bit lanes, which 12-bit samples would overflow. Products and sums stay separate (never fused), so every machine computes the
/// same coefficients.
/// </summary>
internal static class JpegFdct12
{
    /// <summary>The largest coefficient magnitude 12-bit Huffman coding takes: 15 bits for DC differences, 14 for AC (T.81 F.1.2).</summary>
    private const int MaxMagnitude = (1 << 14) - 1;

    /// <summary>Columns[x] holds C(u) / 2 × cos((2x + 1)uπ / 16) for u = 0 to 7: one row pass is the sum of sample x times it.</summary>
    private static readonly Vector256<float>[] Columns = CreateColumns();

    /// <summary>
    /// Transforms 8 rows of 8 samples (0 to 4095, <paramref name="stride"/> apart) and quantizes them with <paramref name="quant"/>
    /// (natural order) into <paramref name="output"/>, in natural order too.
    /// </summary>
    internal static void Forward(ReadOnlySpan<ushort> samples, int stride, ReadOnlySpan<ushort> quant, Span<short> output)
    {
        Vector256<float>[] columns = Columns;

        // Rows: rows[y] holds the 1-D DCT of sample row y, frequency u in lane u.
        Span<Vector256<float>> rows = stackalloc Vector256<float>[8];
        for (int y = 0; y < 8; y++)
        {
            ReadOnlySpan<ushort> s = samples.Slice(y * stride, 8);
            Vector256<float> sum = Vector256<float>.Zero;
            for (int x = 0; x < 8; x++)
            {
                sum += columns[x] * Vector256.Create((float)(s[x] - 2048));
            }

            rows[y] = sum;
        }

        // Columns: frequency v is the sum over y of the basis value (v, y) times row y.
        for (int v = 0; v < 8; v++)
        {
            Vector256<float> sum = Vector256<float>.Zero;
            for (int y = 0; y < 8; y++)
            {
                sum += rows[y] * Vector256.Create(columns[y].GetElement(v));
            }

            for (int u = 0; u < 8; u++)
            {
                int n = (v * 8) + u;
                int value = (int)MathF.Round(sum.GetElement(u) / quant[n], MidpointRounding.AwayFromZero);
                output[n] = (short)Math.Clamp(value, -MaxMagnitude, MaxMagnitude);
            }
        }
    }

    private static Vector256<float>[] CreateColumns()
    {
        Vector256<float>[] columns = new Vector256<float>[8];
        Span<float> lanes = stackalloc float[8];
        for (int x = 0; x < 8; x++)
        {
            for (int u = 0; u < 8; u++)
            {
                double scale = u == 0 ? Math.Sqrt(0.5) / 2 : 0.5;
                lanes[u] = (float)(scale * Math.Cos(((2 * x) + 1) * u * Math.PI / 16));
            }

            columns[x] = Vector256.Create<float>(lanes);
        }

        return columns;
    }
}
