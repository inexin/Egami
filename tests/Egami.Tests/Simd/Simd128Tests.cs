using System.Runtime.Intrinsics;

using Egami.Simd;

namespace Egami.Tests.Simd;

/// <summary>
/// Each <see cref="Simd128"/> operation against its scalar definition, on random lanes and on the edges where saturation and
/// rounding act. On x64 this checks the SSE forms; CI's Arm64 jobs run the same tests on the AdvSimd forms.
/// </summary>
public class Simd128Tests
{
    private const int Trials = 2000;

    private static readonly short[] EdgeShorts = [short.MinValue, short.MinValue + 1, -256, -255, -1, 0, 1, 127, 128, 255, 256, short.MaxValue];
    private static readonly int[] EdgeInts = [int.MinValue, -65536, -32769, -32768, -1, 0, 1, 255, 256, 32767, 32768, 65535, 65536, int.MaxValue];

    [Fact]
    public void PacksSaturate()
    {
        Assert.SkipUnless(Simd128.IsSupported, "128-bit SIMD is not available.");
        Random random = new(1);
        for (int trial = 0; trial < Trials; trial++)
        {
            int[] a = Ints(random), b = Ints(random), c = Ints(random), d = Ints(random);
            short[] s = Shorts(random), t = Shorts(random);
            Assert.Equal([.. a.Concat(b).Select(v => (short)Math.Clamp(v, short.MinValue, short.MaxValue))],
                Lanes(Simd128.PackSignedSaturate(Vector(a), Vector(b))));
            Assert.Equal([.. a.Concat(b).Select(v => (ushort)Math.Clamp(v, 0, ushort.MaxValue))],
                Lanes(Simd128.PackUnsignedSaturate(Vector(a), Vector(b))));
            Assert.Equal([.. a.Concat(b).Concat(c).Concat(d).Select(v => (byte)Math.Clamp((int)v, 0, 255))],
                Lanes(Simd128.PackUnsignedSaturate(Vector(a), Vector(b), Vector(c), Vector(d))));
            Assert.Equal([.. s.Concat(t).Select(v => (sbyte)Math.Clamp((int)v, sbyte.MinValue, sbyte.MaxValue))],
                Lanes(Simd128.PackSignedSaturate(Vector(s), Vector(t))));
            Assert.Equal([.. s.Concat(t).Select(v => (byte)Math.Clamp((int)v, 0, 255))],
                Lanes(Simd128.PackUnsignedSaturate(Vector(s), Vector(t))));
        }
    }

    [Fact]
    public void MultipliesMatchTheirDefinitions()
    {
        Assert.SkipUnless(Simd128.IsSupported, "128-bit SIMD is not available.");
        Random random = new(2);
        for (int trial = 0; trial < Trials; trial++)
        {
            short[] a = Shorts(random), b = Shorts(random);
            Assert.Equal([.. a.Zip(b, (x, y) => (short)((x * y) >> 16))], Lanes(Simd128.MultiplyHigh(Vector(a), Vector(b))));
            ushort[] ua = [.. a.Select(v => (ushort)v)], ub = [.. b.Select(v => (ushort)v)];
            Assert.Equal([.. ua.Zip(ub, (x, y) => (ushort)(((uint)x * y) >> 16))], Lanes(Simd128.MultiplyHigh(Vector(ua), Vector(ub))));

            // The architectures differ only for -32768 × -32768 (see the remarks), which this keeps out.
            short[] safe = [.. b.Select(v => v == short.MinValue ? (short)(short.MinValue + 1) : v)];
            Assert.Equal([.. a.Zip(safe, (x, y) => (short)(((x * y) + (1 << 14)) >> 15))], Lanes(Simd128.MultiplyHighRoundScale(Vector(a), Vector(safe))));
            int[] pairs = new int[4];
            for (int i = 0; i < 4; i++)
            {
                pairs[i] = unchecked((a[2 * i] * b[2 * i]) + (a[(2 * i) + 1] * b[(2 * i) + 1]));
            }

            Assert.Equal(pairs, Lanes(Simd128.MultiplyAddAdjacent(Vector(a), Vector(b))));
        }
    }

    [Fact]
    public void SumsOfAbsoluteDifferencesAndPairs()
    {
        Assert.SkipUnless(Simd128.IsSupported, "128-bit SIMD is not available.");
        Random random = new(3);
        byte[] a = new byte[16], b = new byte[16];
        for (int trial = 0; trial < Trials; trial++)
        {
            random.NextBytes(a);
            random.NextBytes(b);
            if (trial == 0)
            {
                Array.Fill(a, (byte)255);
                Array.Fill(b, (byte)0);
            }

            ulong low = 0, high = 0;
            for (int i = 0; i < 8; i++)
            {
                low += (ulong)Math.Abs(a[i] - b[i]);
                high += (ulong)Math.Abs(a[i + 8] - b[i + 8]);
            }

            Assert.Equal([low, high], Lanes(Simd128.SumAbsoluteDifferences(Vector(a), Vector(b))));
            Assert.Equal([.. Enumerable.Range(0, 8).Select(i => (ushort)(a[2 * i] + a[(2 * i) + 1]))], Lanes(Simd128.AddPairwiseWidening(Vector(a))));
        }
    }

    [Fact]
    public void ShufflePicksAndZeroes()
    {
        Assert.SkipUnless(Simd128.IsSupported, "128-bit SIMD is not available.");
        Random random = new(4);
        byte[] value = new byte[16], indices = new byte[16];
        for (int trial = 0; trial < Trials; trial++)
        {
            random.NextBytes(value);
            for (int i = 0; i < 16; i++)
            {
                indices[i] = random.Next(4) == 0 ? (byte)random.Next(0x80, 256) : (byte)random.Next(16);
            }

            Assert.Equal([.. indices.Select(i => i >= 0x80 ? (byte)0 : value[i])], Lanes(Simd128.Shuffle(Vector(value), Vector(indices))));
        }
    }

    [Fact]
    public void ZipsAndTransposes()
    {
        Assert.SkipUnless(Simd128.IsSupported, "128-bit SIMD is not available.");
        Random random = new(5);
        for (int trial = 0; trial < 200; trial++)
        {
            byte[] a = new byte[16], b = new byte[16];
            random.NextBytes(a);
            random.NextBytes(b);
            Assert.Equal([.. Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? a[i / 2] : b[i / 2])], Lanes(Simd128.ZipLow(Vector(a), Vector(b))));
            Assert.Equal([.. Enumerable.Range(0, 16).Select(i => i % 2 == 0 ? a[8 + (i / 2)] : b[8 + (i / 2)])], Lanes(Simd128.ZipHigh(Vector(a), Vector(b))));
            short[] s = Shorts(random), t = Shorts(random);
            Assert.Equal([.. Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? s[i / 2] : t[i / 2])], Lanes(Simd128.ZipLow(Vector(s), Vector(t))));
            Assert.Equal([.. Enumerable.Range(0, 8).Select(i => i % 2 == 0 ? s[4 + (i / 2)] : t[4 + (i / 2)])], Lanes(Simd128.ZipHigh(Vector(s), Vector(t))));
            int[] p = Ints(random), q = Ints(random);
            Assert.Equal([p[0], q[0], p[1], q[1]], Lanes(Simd128.ZipLow(Vector(p), Vector(q))));
            Assert.Equal([p[2], q[2], p[3], q[3]], Lanes(Simd128.ZipHigh(Vector(p), Vector(q))));
            long[] u = [random.NextInt64(), random.NextInt64()], v = [random.NextInt64(), random.NextInt64()];
            Assert.Equal([u[0], v[0]], Lanes(Simd128.ZipLow(Vector(u), Vector(v))));
            Assert.Equal([u[1], v[1]], Lanes(Simd128.ZipHigh(Vector(u), Vector(v))));

            int[][] rows = [Ints(random), Ints(random), Ints(random), Ints(random)];
            Vector128<int> r0 = Vector(rows[0]), r1 = Vector(rows[1]), r2 = Vector(rows[2]), r3 = Vector(rows[3]);
            Simd128.Transpose(ref r0, ref r1, ref r2, ref r3);
            Vector128<int>[] columns = [r0, r1, r2, r3];
            for (int c = 0; c < 4; c++)
            {
                Assert.Equal([rows[0][c], rows[1][c], rows[2][c], rows[3][c]], Lanes(columns[c]));
            }
        }
    }

    [Fact]
    public void SaturatingArithmeticAndAverages()
    {
        Assert.SkipUnless(Simd128.IsSupported, "128-bit SIMD is not available.");
        Random random = new(6);
        byte[] a = new byte[16], b = new byte[16];
        for (int trial = 0; trial < Trials; trial++)
        {
            short[] s = Shorts(random), t = Shorts(random);
            Assert.Equal([.. s.Zip(t, (x, y) => (short)Math.Clamp(x + y, short.MinValue, short.MaxValue))], Lanes(Simd128.AddSaturate(Vector(s), Vector(t))));
            Assert.Equal([.. s.Zip(t, (x, y) => (short)Math.Clamp(x - y, short.MinValue, short.MaxValue))], Lanes(Simd128.SubtractSaturate(Vector(s), Vector(t))));
            random.NextBytes(a);
            random.NextBytes(b);
            Assert.Equal([.. a.Zip(b, (x, y) => (byte)Math.Min(x + y, 255))], Lanes(Simd128.AddSaturate(Vector(a), Vector(b))));
            Assert.Equal([.. a.Zip(b, (x, y) => (byte)Math.Max(x - y, 0))], Lanes(Simd128.SubtractSaturate(Vector(a), Vector(b))));
            Assert.Equal([.. a.Zip(b, (x, y) => (byte)((x + y + 1) >> 1))], Lanes(Simd128.Average(Vector(a), Vector(b))));
            ushort[] ua = [.. s.Select(v => (ushort)v)], ub = [.. t.Select(v => (ushort)v)];
            Assert.Equal([.. ua.Zip(ub, (x, y) => (ushort)((x + y + 1) >> 1))], Lanes(Simd128.Average(Vector(ua), Vector(ub))));
        }
    }

    private static short[] Shorts(Random random) =>
        [.. Enumerable.Range(0, 8).Select(_ => random.Next(3) == 0 ? EdgeShorts[random.Next(EdgeShorts.Length)] : (short)random.Next(short.MinValue, short.MaxValue + 1))];

    private static int[] Ints(Random random) =>
        [.. Enumerable.Range(0, 4).Select(_ => random.Next(3) == 0 ? EdgeInts[random.Next(EdgeInts.Length)] : random.Next(-100000, 100000))];

    private static Vector128<T> Vector<T>(T[] lanes) => Vector128.Create(lanes);

    private static T[] Lanes<T>(Vector128<T> vector)
    {
        T[] lanes = new T[Vector128<T>.Count];
        vector.CopyTo(lanes);
        return lanes;
    }
}
