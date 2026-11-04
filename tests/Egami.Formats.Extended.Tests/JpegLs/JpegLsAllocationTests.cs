using Egami.Formats.Extended.JpegLs;

namespace Egami.Formats.Extended.Tests.JpegLs;

/// <summary>The JPEG-LS coder keeps nothing per row: twice the rows code with the same allocations.</summary>
public sealed class JpegLsAllocationTests
{
    [Theory]
    [Trait("Category", "Allocation")]
    [InlineData(JpegLsInterleave.Line, 0)]
    [InlineData(JpegLsInterleave.Sample, 0)]
    [InlineData(JpegLsInterleave.Line, 2)]
    [InlineData(JpegLsInterleave.Sample, 2)]
    public void CodesWithoutAllocatingPerRow(JpegLsInterleave interleave, int near)
    {
        Assert.SkipUnless(Environment.GetEnvironmentVariable("EGAMI_ALLOCATION_TESTS") == "1", "Allocation budgets run on demand: set EGAMI_ALLOCATION_TESTS=1.");
        JpegLsWriterOptions options = new() { Interleave = interleave, Near = near, RestartInterval = 16 };
        // Noise, so that even near-lossless output passes the coder's first flush and its buffer stops growing.
        static ushort[] Image(int height)
        {
            Random random = new(5);
            return [.. Enumerable.Range(0, 3 * 512 * height).Select(_ => (ushort)random.Next(256))];
        }

        long Encode(ushort[] samples)
        {
            long fewest = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                JpegLsWriter.Write(Stream.Null, samples, 3, 512, 8, options);
                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return fewest;
        }

        static long Decode(byte[] file)
        {
            long fewest = long.MaxValue;
            for (int i = 0; i < 4; i++)
            {
                long before = GC.GetAllocatedBytesForCurrentThread();
                JpegLsDecoder decoder = new(file, file.Length, static (_, _) => { });
                Discard sink = new();
                while (!decoder.FirstScan.Done)
                {
                    decoder.NextRow(sink, true);
                }

                fewest = Math.Min(fewest, GC.GetAllocatedBytesForCurrentThread() - before);
            }

            return fewest;
        }

        ushort[] small = Image(128), large = Image(256);
        long encodeSmall = Encode(small), encodeLarge = Encode(large);
        Assert.True(Math.Abs(encodeLarge - encodeSmall) < 8 * 1024, $"Encode: {encodeSmall} B, then {encodeLarge} B for twice the rows");
        long decodeSmall = Decode(JpegLsTesting.Encode(small, 3, 512, 8, options)), decodeLarge = Decode(JpegLsTesting.Encode(large, 3, 512, 8, options));
        Assert.True(Math.Abs(decodeLarge - decodeSmall) < 8 * 1024, $"Decode: {decodeSmall} B, then {decodeLarge} B for twice the rows");
    }

    private sealed class Discard : JpegLsLineSink
    {
        public override void Line(int component, int y, ReadOnlySpan<int> samples)
        {
        }
    }
}
