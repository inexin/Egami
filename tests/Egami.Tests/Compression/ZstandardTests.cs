using Egami.Compression;

namespace Egami.Tests.Compression;

/// <summary>The public Zstandard surface: it wraps the managed encoder and decoder, checking its arguments.</summary>
public class ZstandardTests
{
    [Theory]
    [InlineData(Zstandard.MinLevel)]
    [InlineData(Zstandard.DefaultLevel)]
    [InlineData(19)]
    public void RoundTrips(int level)
    {
        byte[] data = new byte[100_000];
        new Random(7).NextBytes(data.AsSpan(0, 5_000));
        byte[] packed = new byte[Zstandard.MaxCompressedLength(data.Length)];
        int written = Zstandard.Compress(data, packed, level);
        byte[] back = new byte[data.Length];
        Assert.True(written < 10_000);
        Assert.Equal(data.Length, Zstandard.Decompress(packed.AsSpan(0, written), back));
        Assert.Equal(data, back);
    }

    [Fact]
    public void ChecksItsArguments()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.MaxCompressedLength(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => Zstandard.Compress(new byte[4], new byte[100], Zstandard.MaxLevel + 1));
        Assert.Throws<ArgumentException>(() => Zstandard.Compress(new byte[1000], new byte[10]));
    }

    [Fact]
    public void RefusesDataThatDoesNotFit()
    {
        byte[] data = new byte[1000];
        byte[] packed = new byte[Zstandard.MaxCompressedLength(data.Length)];
        int written = Zstandard.Compress(data, packed);
        Assert.Throws<CorruptDataException>(() => Zstandard.Decompress(packed.AsSpan(0, written), new byte[999]));
        Assert.Throws<CorruptDataException>(() => Zstandard.Decompress([1, 2, 3, 4, 5, 6, 7, 8], new byte[10]));
    }
}
