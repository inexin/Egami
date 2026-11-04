using Egami.Formats.Jxl;
using Egami.Pixels;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// The group pipeline straight into pixels (roadmap 2.4.11, D1, D2 and D7) gives the same pixels as the frame rendered in planes
/// and converted after, bit for bit: on the lossy files of the corpus (alpha, progressive, 16-bit, float, gray, HDR, wide gamut,
/// every EPF setting) and on odd sizes whose groups and seams fall at the frame's edges, in the natural type, as floats and on
/// one thread. A damaged file fails on both.
/// </summary>
public class JxlGroupPipelineTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");

    public static TheoryData<string> Files => [.. Directory.GetFiles(Corpus, "*.jxl").Select(f => Path.GetFileName(f)).Where(f => !f.Contains("lossless", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    [Theory]
    [MemberData(nameof(Files))]
    public void PixelsAreTheSameAsThePlanePath(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        Same(data, file);
    }

    [Theory]
    [InlineData(5, 3)]
    [InlineData(260, 9)]
    [InlineData(257, 300)]
    [InlineData(513, 40)]
    [InlineData(8, 600)]
    public void OddSizesAreTheSameAsThePlanePath(int width, int height)
    {
        using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, height);
        uint seed = 7;
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                seed = (seed * 1664525) + 1013904223;
                row[x] = new Rgba8((byte)((x * 7) + (seed >> 28)), (byte)(y * 3), (byte)(seed >> 24), (byte)((x + y) * 5));
            }
        }

        foreach (JxlWriterOptions options in (JxlWriterOptions[])[new() { Distance = 1f, Effort = 7 }, new() { Distance = 2f, Effort = 3, Progressive = true }])
        {
            using MemoryStream stream = new();
            raster.Save(stream, JxlFormat.Info, options);
            Same(stream.ToArray(), $"{width}×{height} {options.Effort}");
        }
    }

    private static void Same(byte[] data, string name)
    {
        JxlGroupPipeline.Disabled = true;
        byte[] expected;
        byte[] expectedFloat;
        try
        {
            using Raster whole = Raster.Load(data);
            using Raster<RgbaF32> wholeFloat = Raster.Load<RgbaF32>(data, options: new JxlReaderOptions { LinearOutput = true });
            expected = TiffReference.Bytes(whole);
            expectedFloat = TiffReference.Bytes(wholeFloat);
        }
        catch (CorruptDataException)
        {
            // A damaged file is damaged on both paths.
            JxlGroupPipeline.Disabled = false;
            Assert.Throws<CorruptDataException>(() => Raster.Load(data).Dispose());
            return;
        }
        finally
        {
            JxlGroupPipeline.Disabled = false;
        }

        using Raster groups = Raster.Load(data);
        using Raster one = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 1 });
        using Raster<RgbaF32> groupsFloat = Raster.Load<RgbaF32>(data, options: new JxlReaderOptions { LinearOutput = true });
        Assert.True(expected.AsSpan().SequenceEqual(TiffReference.Bytes(groups)), name);
        Assert.True(expected.AsSpan().SequenceEqual(TiffReference.Bytes(one)), $"{name}, one thread");
        Assert.True(expectedFloat.AsSpan().SequenceEqual(TiffReference.Bytes(groupsFloat)), $"{name}, linear floats");
    }
}
