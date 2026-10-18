using System.Text.Json;

using Egami.Formats.Av1;

namespace Egami.Tests.Formats.Av1;

/// <summary>
/// The AV1 encoder's forward transforms: every size and type gives libaom 3.15.2's coefficients on the inputs of
/// tools/av1encref/fwdtxfm.c (reference/avif/av1-forward-transforms.json), and the inverse transform brings the input back.
/// </summary>
public class Av1ForwardTransformTests
{
    private static readonly int[] Wide = [4, 8, 16, 32, 64, 4, 8, 8, 16, 16, 32, 32, 64, 4, 16, 8, 32, 16, 64];
    private static readonly int[] High = [4, 8, 16, 32, 64, 8, 4, 16, 8, 32, 16, 64, 32, 16, 4, 32, 8, 64, 16];

    [Fact]
    public void MatchesLibaom()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "av1-forward-transforms.json")));
        int count = 0;
        int[] input = new int[64 * 64];
        int[] output = new int[64 * 64];
        foreach (JsonElement entry in document.RootElement.GetProperty("transforms").EnumerateArray())
        {
            int size = entry.GetProperty("size").GetInt32(), type = entry.GetProperty("type").GetInt32();
            int bd = entry.GetProperty("bd").GetInt32(), kind = entry.GetProperty("kind").GetInt32();
            bool lossless = size < 0;
            int w = lossless ? 4 : Wide[size], h = lossless ? 4 : High[size];
            uint seed = lossless ? (uint)(99000 + (bd * 4) + kind) : (uint)((size * 1000) + (type * 50) + (bd * 4) + kind);
            Make(input, w, h, bd, kind, seed);
            Array.Clear(output);
            Av1ForwardTransform.Forward(input, w, output, lossless ? Tx.T4x4 : size, type, lossless);
            int coded = Math.Min(w, 32) * Math.Min(h, 32);
            Assert.True(entry.GetProperty("hash").GetString() == Hash(output.AsSpan(0, coded)).ToString("x16", System.Globalization.CultureInfo.InvariantCulture),
                $"Size {size} type {type} at {bd} bits, input {kind}, differs from libaom.");
            count++;
        }

        Assert.Equal(2328, count);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void InverseBringsTheInputBack(int bitDepth)
    {
        Random random = new(bitDepth);
        int[] coeffs = new int[64 * 64];
        int[] residual = new int[64 * 64];
        ushort[] plane = new ushort[64 * 64];
        int mid = 1 << (bitDepth - 1);
        for (int txSize = 0; txSize < Tx.Count; txSize++)
        {
            int w = Av1Tables.TxSizeWide[txSize], h = Av1Tables.TxSizeHigh[txSize];
            for (int txType = 0; txType < 16; txType++)
            {
                if (!Allowed(h, Av1Tables.VtxTab[txType]) || !Allowed(w, Av1Tables.HtxTab[txType]))
                {
                    continue;
                }

                // A transform 64 samples on a side drops its high frequencies, so its input is smooth, and what it drops
                // still costs a little.
                bool smooth = w == 64 || h == 64;
                int worst = 0;
                for (int iteration = 0; iteration < 20; iteration++)
                {
                    int amplitude = (mid / 2) >> random.Next(0, 6);
                    int fx = random.Next(1, 3), fy = random.Next(1, 3);
                    for (int i = 0; i < w * h; i++)
                    {
                        int x = i % w, y = i / w;
                        residual[i] = smooth
                            ? (int)(amplitude * Math.Sin(fx * x * Math.PI / w) * Math.Cos(fy * y * Math.PI / h))
                            : random.Next(-amplitude, amplitude + 1);
                    }

                    Av1ForwardTransform.Forward(residual, w, coeffs, txSize, txType, false);
                    plane.AsSpan(0, w * h).Fill((ushort)mid);
                    Av1InverseTransform.Add(coeffs, txSize, txType, false, Math.Min(w, 32) * Math.Min(h, 32), plane, 0, w, bitDepth);
                    for (int i = 0; i < w * h; i++)
                    {
                        worst = Math.Max(worst, Math.Abs(plane[i] - mid - residual[i]));
                    }

                    Array.Clear(coeffs);
                }

                Assert.True(worst <= (smooth ? 4 + (mid >> 7) : 2), $"Size {txSize} type {txType}: {worst} levels off.");
            }
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(10)]
    [InlineData(12)]
    public void WalshHadamardIsExact(int bitDepth)
    {
        Random random = new(bitDepth + 1);
        int[] coeffs = new int[16];
        int[] residual = new int[16];
        ushort[] plane = new ushort[16];
        int max = (1 << bitDepth) - 1;
        for (int iteration = 0; iteration < 20000; iteration++)
        {
            ushort[] source = new ushort[16], prediction = new ushort[16];
            for (int i = 0; i < 16; i++)
            {
                source[i] = (ushort)random.Next(max + 1);
                prediction[i] = (ushort)(iteration % 3 == 0 ? random.Next(max + 1) : random.Next(2) * max);
                residual[i] = source[i] - prediction[i];
            }

            Av1ForwardTransform.Forward(residual, 4, coeffs, Tx.T4x4, TxType.DctDct, true);

            // Lossless quantization divides by 4 (the dequantizer of qindex 0) without loss.
            for (int i = 0; i < 16; i++)
            {
                Assert.Equal(0, coeffs[i] & 3);
            }

            prediction.CopyTo(plane, 0);
            Av1InverseTransform.Add(coeffs, Tx.T4x4, TxType.DctDct, true, 16, plane, 0, 4, bitDepth);
            Assert.Equal(source, plane);
        }
    }

    private static bool Allowed(int points, int type) => points <= 16 || (points == 32 && type is 0 or 3) || (points == 64 && type == 0);

    /// <summary>The inputs of tools/av1encref/fwdtxfm.c: an LCG, and four kinds of block.</summary>
    private static void Make(int[] input, int w, int h, int bd, int kind, uint seed)
    {
        uint state = seed;
        int max = (1 << bd) - 1;
        for (int i = 0; i < w * h; i++)
        {
            int v = kind switch
            {
                0 => (int)(Next(ref state) % (uint)((2 * max) + 1)) - max,
                1 => (int)(Next(ref state) % 33) - 16,
                2 => (Next(ref state) & 1) != 0 ? max : -max,
                _ => ((((i % w) * 37) + ((i / w) * 11)) % ((2 * max) + 1)) - max,
            };
            input[i] = v;
        }

        static uint Next(ref uint state)
        {
            state = unchecked((state * 1103515245u) + 12345u);
            return state >> 8;
        }
    }

    private static ulong Hash(ReadOnlySpan<int> values)
    {
        ulong h = 1469598103934665603;
        foreach (int value in values)
        {
            uint v = (uint)value;
            for (int b = 0; b < 4; b++)
            {
                h ^= (v >> (8 * b)) & 0xFF;
                h = unchecked(h * 1099511628211);
            }
        }

        return h;
    }
}
