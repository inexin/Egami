using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>Alpha in lossy files: the ALPH chunk, its filters and level reduction, the VP8X flags and transparent-color flattening.</summary>
public class WebpLossyAlphaTests
{
    public static TheoryData<string, int> Pictures()
    {
        TheoryData<string, int> data = [];
        foreach (string name in new[] { "half-clear", "mixed-alpha", "edge-17x33", "edge-1x1", "noise" })
        {
            foreach (int effort in new[] { 0, 3, 4, 6 })
            {
                data.Add(name, effort);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Pictures))]
    public void AlphaIsExactAtAlphaQuality100(string name, int effort)
    {
        using Raster<Rgba8> source = WebpTestImages.Get(name);
        byte[] file = Save(source, new WebpWriterOptions { Lossless = false, Effort = effort });
        using Raster<Rgba8> decoded = Raster.Load<Rgba8>(file);
        for (int y = 0; y < source.Height; y++)
        {
            ReadOnlySpan<Rgba8> a = source.Row(y), b = decoded.Row(y);
            for (int x = 0; x < a.Length; x++)
            {
                Assert.Equal(a[x].A, b[x].A);
            }
        }

        WebpDetails details = Assert.IsType<WebpDetails>(decoded.Metadata.FormatDetails);
        Assert.False(details.Lossless);
        bool transparent = Enumerable.Range(0, source.Height).Any(y => source.Row(y).ToArray().Any(p => p.A < 255));
        Assert.Equal(transparent, details.HasAlpha);
        Assert.Equal(transparent, details.Extended);
    }

    [Fact]
    public void OpaqueImagesUseTheSimpleFormat()
    {
        using Raster<Rgba8> source = WebpTestImages.Get("gradient");
        byte[] file = Save(source, new WebpWriterOptions { Lossless = false });
        Assert.Equal("VP8 ", System.Text.Encoding.ASCII.GetString(file, 12, 4));
    }

    [Theory]
    [InlineData("shapes")]
    [InlineData("noise")]
    [InlineData("edge-17x33")]
    public void Vp8ChunksHaveEvenSizes(string name)
    {
        // libwebp pads the VP8 payload itself; some readers cannot read an odd VP8 chunk followed by its padding byte.
        using Raster<Rgba8> source = WebpTestImages.Get(name);
        foreach (int quality in new[] { 10, 75, 100 })
        {
            byte[] file = Save(source, new WebpWriterOptions { Lossless = false, Quality = quality });
            for (int at = 12; at + 8 <= file.Length;)
            {
                int size = BitConverter.ToInt32(file, at + 4);
                if (System.Text.Encoding.ASCII.GetString(file, at, 4) == "VP8 ")
                {
                    Assert.Equal(0, size & 1);
                }

                at += 8 + size + (size & 1);
            }
        }
    }

    [Theory]
    [InlineData(0, 2)]
    [InlineData(50, 12)]
    [InlineData(70, 16)]
    [InlineData(90, 176)]
    public void LowerAlphaQualityReducesTheLevels(int quality, int levels)
    {
        Random random = new(5);
        using Raster<Rgba8> source = WebpTestImages.Make(256, 64, (x, y) => new Rgba8(100, 150, 200, (byte)Math.Clamp(x + random.Next(-6, 7), 0, 255)));
        byte[] exact = Save(source, new WebpWriterOptions { Lossless = false });
        byte[] reduced = Save(source, new WebpWriterOptions { Lossless = false, AlphaQuality = quality });
        Assert.True(quality > 70 || reduced.Length < exact.Length, $"{reduced.Length} bytes at alpha quality {quality}, {exact.Length} at 100");
        using Raster<Rgba8> decoded = Raster.Load<Rgba8>(reduced);
        HashSet<byte> values = [];
        for (int y = 0; y < decoded.Height; y++)
        {
            foreach (Rgba8 p in decoded.Row(y))
            {
                values.Add(p.A);
            }
        }

        Assert.True(values.Count <= levels, $"{values.Count} alpha values at quality {quality}");
        Assert.Contains((byte)0, values);
        Assert.Contains((byte)255, values);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void FiltersAreUndoneByTheReader(int filter)
    {
        Random random = new(filter);
        int width = 37, height = 23;
        byte[] plane = new byte[width * height];
        random.NextBytes(plane);
        byte[] filtered = new byte[plane.Length];
        WebpAlphaEncoder.Filter(plane, width, height, filter, filtered);
        for (int y = 0; y < height; y++)
        {
            Span<byte> row = filtered.AsSpan(y * width, width);
            WebpAlpha.Unfilter(filter, y == 0 ? default : filtered.AsSpan((y - 1) * width, width), row);
        }

        Assert.Equal(plane, filtered);
    }

    [Fact]
    public void FlatteningTransparentColorsShrinksTheFile()
    {
        Random random = new(3);
        using Raster<Rgba8> source = WebpTestImages.Make(128, 128, (x, y) => x < 64
            ? new Rgba8(200, 30, 30, 255)
            : new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 0));
        byte[] kept = Save(source, new WebpWriterOptions { Lossless = false });
        byte[] flattened = Save(source, new WebpWriterOptions { Lossless = false, KeepTransparentColors = false });
        Assert.True(flattened.Length < kept.Length / 2, $"{flattened.Length} bytes flattened, {kept.Length} kept");
    }

    private static byte[] Save(Raster<Rgba8> raster, WebpWriterOptions options)
    {
        using MemoryStream file = new();
        raster.Save(file, WebpFormat.Info, options);
        return file.ToArray();
    }
}
