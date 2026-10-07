using System.Text;

using Egami.Formats;

namespace Egami.Tests.Formats;

public class Crc32Tests
{
    [Theory]
    [InlineData("", 0x00000000u)]
    [InlineData("a", 0xE8B7BE43u)]
    [InlineData("123456789", 0xCBF43926u)]
    [InlineData("The quick brown fox jumps over the lazy dog", 0x414FA339u)]
    [InlineData("IEND", 0xAE426082u)]
    public void MatchesKnownTestVectors(string text, uint expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, Crc32.Compute(data));
        Assert.Equal(expected, ~Crc32.UpdateState(~0u, data, useHardware: false));
    }

    [Fact]
    public void MatchesAKnownLongVector()
    {
        // 1 MiB of zero bytes.
        Assert.Equal(0xA738EA1Cu, Crc32.Compute(new byte[1024 * 1024]));
    }

    [Fact]
    public void HardwarePathEqualsTablePath()
    {
        Random random = new(42);
        byte[] buffer = new byte[5000];
        random.NextBytes(buffer);
        for (int length = 0; length <= 1100; length++)
        {
            for (int offset = 0; offset < 3; offset++)
            {
                ReadOnlySpan<byte> data = buffer.AsSpan(offset, length);
                uint state = (uint)random.Next();
                Assert.Equal(Crc32.UpdateState(state, data, useHardware: false), Crc32.UpdateState(state, data, useHardware: true));
            }
        }

        Assert.Equal(Crc32.UpdateState(~0u, buffer, useHardware: false), Crc32.UpdateState(~0u, buffer, useHardware: true));
    }

    [Fact]
    public void UpdateContinuesAComputation()
    {
        byte[] data = Encoding.ASCII.GetBytes("The quick brown fox jumps over the lazy dog, then naps for a while in the sun.");
        for (int split = 0; split <= data.Length; split++)
        {
            Assert.Equal(Crc32.Compute(data), Crc32.Update(Crc32.Compute(data.AsSpan(0, split)), data.AsSpan(split)));
        }
    }

    [Fact]
    public void ClmulConstantsFollowFromThePolynomial()
    {
        // Each fold constant is x^n mod P(x), bit-reflected over 32 bits and shifted left by one, for the fold distances
        // in the carry-less multiplication method: 512 ± 32, 128 ± 32 and 64 bits. Mu is floor(x^64 / P(x)), bit-reflected over 33 bits.
        Assert.Equal(Crc32.ClmulConstants.Fold512Low, FoldConstant(512 + 32));
        Assert.Equal(Crc32.ClmulConstants.Fold512High, FoldConstant(512 - 32));
        Assert.Equal(Crc32.ClmulConstants.Fold128Low, FoldConstant(128 + 32));
        Assert.Equal(Crc32.ClmulConstants.Fold128High, FoldConstant(128 - 32));
        Assert.Equal(Crc32.ClmulConstants.Fold64, FoldConstant(64));
        Assert.Equal(Crc32.ClmulConstants.Polynomial, Reflect(Polynomial, 33));
        Assert.Equal(Crc32.ClmulConstants.Mu, Reflect(PolyDivide(64), 33));
    }

    private const ulong Polynomial = 0x1_04C1_1DB7;

    private static ulong FoldConstant(int n)
    {
        ulong r = 1;
        for (int i = 0; i < n; i++)
        {
            r <<= 1;
            if ((r & (1UL << 32)) != 0)
            {
                r ^= Polynomial;
            }
        }

        return Reflect(r, 32) << 1;
    }

    /// <summary>Divides x^<paramref name="exponent"/> by the polynomial over GF(2), returning the quotient.</summary>
    private static ulong PolyDivide(int exponent)
    {
        ulong quotient = 0;
        System.Numerics.BigInteger a = System.Numerics.BigInteger.One << exponent;
        System.Numerics.BigInteger p = Polynomial;
        for (int shift = exponent - 32; shift >= 0; shift--)
        {
            if (!((a >> (shift + 32)) & 1).IsZero)
            {
                a ^= p << shift;
                quotient |= 1UL << shift;
            }
        }

        return quotient;
    }

    private static ulong Reflect(ulong value, int bits)
    {
        ulong result = 0;
        for (int i = 0; i < bits; i++)
        {
            result |= ((value >> i) & 1) << (bits - 1 - i);
        }

        return result;
    }
}
