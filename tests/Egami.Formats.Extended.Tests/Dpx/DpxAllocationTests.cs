using Egami.Pixels;

namespace Egami.Formats.Extended.Tests.Dpx;

/// <summary>DPX and Cineon keep nothing per row: twice the rows decode and encode with the same allocations.</summary>
public sealed class DpxAllocationTests
{
    [Theory]
    [Trait("Category", "Allocation")]
    [InlineData("DPX", 10)]
    [InlineData("DPX", 16)]
    [InlineData("DPX", 8)]
    [InlineData("Cineon", 10)]
    public void CodesWithoutAllocatingPerRow(string format, int bits)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        Formats.WriterOptions options = format == "DPX" ? new Extended.Dpx.DpxWriterOptions { BitsPerSample = bits } : Formats.WriterOptions.Default;
        using Raster<Rgb16> band = Raster.Create<Rgb16>(512, 16);
        using Raster<Rgb16> small = Raster.Create<Rgb16>(512, 128);
        using Raster<Rgb16> large = Raster.Create<Rgb16>(512, 256);

        long Encode(Raster<Rgb16> raster)
        {
            long fewest = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                raster.Save(Stream.Null, TestImages.Format(format), options);
                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return fewest;
        }

        long Decode(byte[] file)
        {
            long fewest = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                using RasterDecoder decoder = RasterDecoder.Open(file, TestImages.Context);
                decoder.NextFrame();
                while (decoder.ReadRows(band) > 0)
                {
                }

                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return fewest;
        }

        long encodeSmall = Encode(small), encodeLarge = Encode(large);
        Assert.True(Math.Abs(encodeLarge - encodeSmall) < 8 * 1024, $"Encode: {encodeSmall} B, then {encodeLarge} B for twice the rows");
        long decodeSmall = Decode(TestImages.Save(small, format, options)), decodeLarge = Decode(TestImages.Save(large, format, options));
        Assert.True(Math.Abs(decodeLarge - decodeSmall) < 8 * 1024, $"Decode: {decodeSmall} B, then {decodeLarge} B for twice the rows");
    }
}
