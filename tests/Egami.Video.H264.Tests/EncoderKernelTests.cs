using Egami.Video.Codecs.H264;

namespace Egami.Video.H264.Tests;

/// <summary>
/// The encoder's SIMD block costs give exactly what their scalar forms give, on every block size the encoder asks for, with
/// random and extreme (0 against 255) samples.
/// </summary>
public sealed class EncoderKernelTests
{
    private const int Stride = 48;

    public static TheoryData<int, int> Sizes => new() { { 4, 4 }, { 8, 4 }, { 4, 8 }, { 8, 8 }, { 16, 8 }, { 8, 16 }, { 16, 16 } };

    [Theory]
    [MemberData(nameof(Sizes))]
    public void SatdMatchesScalar(int w, int h)
    {
        foreach ((byte[] a, byte[] b) in Planes(w * h))
        {
            for (int at = 0; at < 8; at += 3)
            {
                Assert.Equal(H264EncodeSimd.SatdScalar(a, at, Stride, b, at + 1, Stride, w, h), H264EncodeSimd.Satd(a, at, Stride, b, at + 1, Stride, w, h));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void Sa8dMatchesScalar(int w, int h)
    {
        if (w % 8 != 0 || h % 8 != 0)
        {
            return;
        }

        foreach ((byte[] a, byte[] b) in Planes(w * h))
        {
            for (int at = 0; at < 8; at += 3)
            {
                Assert.Equal(H264EncodeSimd.Sa8dScalar(a, at, Stride, b, at + 2, Stride, w, h), H264EncodeSimd.Sa8d(a, at, Stride, b, at + 2, Stride, w, h));
            }
        }
    }

    [Theory]
    [MemberData(nameof(Sizes))]
    public void SseMatchesScalar(int w, int h)
    {
        foreach ((byte[] a, byte[] b) in Planes(w * h))
        {
            for (int at = 0; at < 8; at += 3)
            {
                long expected = 0;
                for (int y = 0; y < h; y++)
                {
                    for (int x = 0; x < w; x++)
                    {
                        int e = a[at + (y * Stride) + x] - b[at + (y * Stride) + x];
                        expected += e * e;
                    }
                }

                Assert.Equal(expected, H264EncodeSimd.Sse(a, b, at, Stride, w, h));
            }
        }
    }

    [Theory]
    [InlineData(4)]
    [InlineData(8)]
    public void IntraPredictionMatchesTheFormulas(int n)
    {
        // Each mode against the standard's per-sample formulas (the encoder's earlier code), on random neighbours.
        Random random = new(n);
        int[] t = new int[33], l = new int[17];
        byte[] edges = new byte[H264IntraPredict.EdgeSize], fast = new byte[64];
        byte[] expected = new byte[64];
        for (int round = 0; round < 500; round++)
        {
            for (int i = 0; i < t.Length; i++)
            {
                t[i] = round < 2 ? round * 255 : random.Next(256);
            }

            for (int i = 0; i < l.Length; i++)
            {
                l[i] = round < 2 ? 255 - (round * 255) : random.Next(256);
            }

            l[0] = t[0];
            H264IntraPredict.Edges(n, t, l, edges);
            for (int mode = 0; mode < 9; mode++)
            {
                bool top = (round & 4) == 0 || mode is not (0 or 2), left = (round & 8) == 0 || mode is not (1 or 2);
                ReferenceNxN(expected, n, n, mode, t, l, top, left);
                H264IntraPredict.PredictNxN(fast, n, n, mode, t, l, top, left, edges);
                Assert.True(expected.AsSpan(0, n * n).SequenceEqual(fast.AsSpan(0, n * n)), $"mode {mode}");
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void QuantizersMatchScalar(bool eight)
    {
        Random random = new(eight ? 8 : 4);
        int size = eight ? 64 : 16, limit = eight ? 64 * 64 * 255 : 36 * 255;
        int[] w = new int[size], q = new int[64];
        for (int round = 0; round < 2000; round++)
        {
            for (int i = 0; i < size; i++)
            {
                w[i] = round < 2 ? (round == 0 ? limit : -limit) : random.Next(-limit, limit + 1) >> random.Next(12);
            }

            int qp = random.Next(52), m = qp % 6, round256 = random.Next(129);
            int qbits = (eight ? H264Forward.Shift8 : H264Forward.Shift4) + (qp / 6);
            ReadOnlySpan<int> mf = eight ? H264Forward.Mf8.AsSpan(m * 64, 64) : H264Forward.Mf4.AsSpan(m * 16, 16);
            if (eight)
            {
                H264MbEncoder.Quantize8(w, mf, qbits, round256, q);
            }
            else
            {
                H264MbEncoder.Quantize4(w, mf, qbits, round256, q);
            }

            for (int i = 0; i < size; i++)
            {
                Assert.Equal(H264Forward.Quantize(w[i], mf[i], qbits, round256), q[i]);
            }
        }
    }

    private static IEnumerable<(byte[] A, byte[] B)> Planes(int seed)
    {
        Random random = new(seed);
        for (int round = 0; round < 200; round++)
        {
            byte[] a = new byte[Stride * 20], b = new byte[Stride * 20];
            random.NextBytes(a);
            random.NextBytes(b);
            yield return (a, b);
        }

        // The extremes: the largest differences, of one sign and of alternating signs (a checkerboard).
        byte[] white = new byte[Stride * 20], black = new byte[Stride * 20], board = new byte[Stride * 20];
        Array.Fill(white, (byte)255);
        for (int i = 0; i < board.Length; i++)
        {
            board[i] = (byte)((((i % Stride) + (i / Stride)) & 1) * 255);
        }

        yield return (white, black);
        yield return (black, white);
        yield return (board, black);
        yield return (white, board);
    }

    private static void ReferenceNxN(Span<byte> dst, int dstStride, int n, int mode, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        // T(x) is p[x, -1] and L(y) p[-1, y], both with -1 the corner.
        int last = (2 * n) - 3;
        for (int y = 0; y < n; y++)
        {
            Span<byte> row = dst.Slice(y * dstStride, n);
            for (int x = 0; x < n; x++)
            {
                int v;
                switch (mode)
                {
                    case 0:
                        v = t[x + 1];
                        break;
                    case 1:
                        v = l[y + 1];
                        break;
                    case 2:
                        v = Dc(n, t, l, top, left);
                        break;
                    case 3:
                        v = x == n - 1 && y == n - 1
                            ? (t[(2 * n) - 1] + (3 * t[2 * n]) + 2) >> 2
                            : (t[x + y + 1] + (2 * t[x + y + 2]) + t[x + y + 3] + 2) >> 2;
                        break;
                    case 4:
                        v = x > y ? (t[x - y - 1] + (2 * t[x - y]) + t[x - y + 1] + 2) >> 2
                            : x < y ? (l[y - x - 1] + (2 * l[y - x]) + l[y - x + 1] + 2) >> 2
                            : (t[1] + (2 * t[0]) + l[1] + 2) >> 2;
                        break;
                    case 5:
                        {
                            int z = (2 * x) - y, h = x - (y >> 1);
                            v = z >= 0 && (z & 1) == 0 ? (t[h] + t[h + 1] + 1) >> 1
                                : z >= 0 ? (t[h - 1] + (2 * t[h]) + t[h + 1] + 2) >> 2
                                : z == -1 ? (l[1] + (2 * l[0]) + t[1] + 2) >> 2
                                : (l[y - (2 * x)] + (2 * l[y - (2 * x) - 1]) + l[y - (2 * x) - 2] + 2) >> 2;
                            break;
                        }

                    case 6:
                        {
                            int z = (2 * y) - x, h = y - (x >> 1);
                            v = z >= 0 && (z & 1) == 0 ? (l[h] + l[h + 1] + 1) >> 1
                                : z >= 0 ? (l[h - 1] + (2 * l[h]) + l[h + 1] + 2) >> 2
                                : z == -1 ? (l[1] + (2 * l[0]) + t[1] + 2) >> 2
                                : (t[x - (2 * y)] + (2 * t[x - (2 * y) - 1]) + t[x - (2 * y) - 2] + 2) >> 2;
                            break;
                        }

                    case 7:
                        {
                            int h = x + (y >> 1);
                            v = (y & 1) == 0 ? (t[h + 1] + t[h + 2] + 1) >> 1 : (t[h + 1] + (2 * t[h + 2]) + t[h + 3] + 2) >> 2;
                            break;
                        }

                    default:
                        {
                            int z = x + (2 * y), h = y + (x >> 1);
                            v = z > last ? l[n]
                                : z == last ? (l[n - 1] + (3 * l[n]) + 2) >> 2
                                : (z & 1) == 0 ? (l[h + 1] + l[h + 2] + 1) >> 1
                                : (l[h + 1] + (2 * l[h + 2]) + l[h + 3] + 2) >> 2;
                            break;
                        }
                }

                row[x] = (byte)v;
            }
        }
    }

    private static int Dc(int n, ReadOnlySpan<int> t, ReadOnlySpan<int> l, bool top, bool left)
    {
        int st = 0, sl = 0;
        for (int i = 1; i <= n; i++)
        {
            st += t[i];
            sl += l[i];
        }

        int log = n == 4 ? 2 : n == 8 ? 3 : 4;
        return top && left ? (st + sl + n) >> (log + 1)
            : left ? (sl + (n >> 1)) >> log
            : top ? (st + (n >> 1)) >> log
            : 128;
    }
}
