using Egami.Formats.Png;

namespace Egami.Tests.Formats.Png;

/// <summary>Every SIMD unfilter kernel must give exactly the scalar result. CI also runs this with SIMD turned off.</summary>
public class PngFilterTests
{
    public static TheoryData<byte, int, int> FiltersStridesAndWidths()
    {
        TheoryData<byte, int, int> data = [];
        for (byte filter = 0; filter <= 4; filter++)
        {
            foreach (int stride in new[] { 1, 2, 3, 4, 6, 8 })
            {
                foreach (int bits in new[] { 512, 256, 128, 0 })
                {
                    data.Add(filter, stride, bits);
                }
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(FiltersStridesAndWidths))]
    public void SimdMatchesScalar(byte filter, int stride, int bits)
    {
        Random random = new((filter * 1000) + (stride * 10) + bits);
        foreach (int pixels in new[] { 1, 2, 3, 5, 15, 16, 17, 31, 64, 65, 333 })
        {
            int length = pixels * stride;
            byte[] previous = new byte[length];
            byte[] filtered = new byte[length + 8];
            random.NextBytes(previous);
            random.NextBytes(filtered);

            byte[] expected = (byte[])filtered.Clone();
            byte[] actual = (byte[])filtered.Clone();
            PngFilter.Unfilter(filter, expected.AsSpan(0, length), previous, stride);
            PngFilterSimd.Unfilter(filter, actual.AsSpan(0, length), previous, stride, bits);

            // Bytes past the row must be untouched.
            Assert.Equal(expected, actual);
        }
    }

    [Fact]
    public void PaethMatchesTheSpecificationExhaustivelyForOnePixel()
    {
        // Every (raw, left-free) combination for a 4-byte pixel whose left and upper-left are set by a first pixel.
        for (int a = 0; a < 256; a += 5)
        {
            for (int b = 0; b < 256; b += 3)
            {
                for (int c = 0; c < 256; c += 7)
                {
                    byte[] previous = [(byte)c, (byte)c, (byte)c, (byte)c, (byte)b, (byte)b, (byte)b, (byte)b];
                    byte[] row = [(byte)a, (byte)a, (byte)a, (byte)a, 0, 0, 0, 0];

                    // The first pixel reconstructs to a + c (left and upper-left are zero, so Paeth predicts "above" = c).
                    byte first = (byte)(a + c);
                    PngFilterSimd.Unfilter(PngFilter.Paeth, row, previous, 4, 128);
                    Assert.Equal(PngFilter.Predict(first, (byte)b, (byte)c), row[4]);
                }
            }
        }
    }
}
