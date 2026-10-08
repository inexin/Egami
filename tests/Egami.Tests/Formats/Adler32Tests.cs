using System.Text;

using Egami.Formats;

namespace Egami.Tests.Formats;

public class Adler32Tests
{
    [Theory]
    [InlineData("", 0x00000001u)]
    [InlineData("a", 0x00620062u)]
    [InlineData("abc", 0x024D0127u)]
    [InlineData("Wikipedia", 0x11E60398u)]
    public void MatchesKnownValues(string text, uint expected)
    {
        byte[] data = Encoding.ASCII.GetBytes(text);
        Assert.Equal(expected, Adler32.Update(1, data, useVectors: false));
        Assert.Equal(expected, Adler32.Update(1, data, useVectors: true));
    }

    [Fact]
    public void VectorPathEqualsScalarPath()
    {
        Random random = new(7);
        byte[] buffer = new byte[40_000];
        random.NextBytes(buffer);
        foreach (int length in Enumerable.Range(0, 200).Concat([5551, 5552, 5553, 5584, 11104, 11105, 20000, 39_997]))
        {
            uint start = (uint)random.Next(65521) | ((uint)random.Next(65521) << 16);
            ReadOnlySpan<byte> data = buffer.AsSpan(random.Next(3), length);
            Assert.Equal(Adler32.Update(start, data, useVectors: false), Adler32.Update(start, data, useVectors: true));
        }
    }

    [Fact]
    public void AllOnesStressesTheSums()
    {
        // 0xFF bytes give the largest sums, so they are the overflow worst case.
        byte[] data = new byte[1_000_003];
        Array.Fill(data, (byte)0xFF);
        Assert.Equal(Adler32.Update(0xFFF0FFF0, data, useVectors: false), Adler32.Update(0xFFF0FFF0, data, useVectors: true));
    }
}
