using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>Every lossless file in the corpus decodes to libwebp's pixels exactly; headers, metadata and damage behave as the design says.</summary>
public class WebpLosslessDecodeTests
{
    public static TheoryData<string> Lossless() => [.. WebpCorpus.Lossless];

    [Theory]
    [MemberData(nameof(Lossless))]
    public void DecodesLikeLibwebp(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(WebpCorpus.Read(file));
        (int width, int height) = WebpCorpus.Size(file);
        Assert.Equal((width, height), (raster.Width, raster.Height));
        byte[] expected = WebpCorpus.Rgba(file);
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            if (!row.SequenceEqual(expected.AsSpan(y * width * 4, width * 4)))
            {
                int x = row.CommonPrefixLength(expected.AsSpan(y * width * 4, width * 4)) / 4;
                Assert.Fail($"{file}: pixel ({x}, {y}) is {raster.Row(y)[x]}, libwebp gives {MemoryMarshal.Cast<byte, Rgba8>(expected.AsSpan((y * width * 4) + (x * 4), 4))[0]}.");
            }
        }

        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [MemberData(nameof(Lossless))]
    public void ProbeMatchesTheHeader(string file)
    {
        RasterInfo info = Raster.Probe(WebpCorpus.Read(file));
        Assert.Equal(WebpFormat.Info, info.Format);
        Assert.Equal(WebpCorpus.Size(file), (info.Width, info.Height));
        Assert.Equal(1, info.FrameCount);
        bool alpha = WebpCorpus.Info(file).GetProperty("alpha").GetBoolean();
        Assert.Equal(alpha ? Rgba8.Layout : Rgb8.Layout, info.Layout);
    }

    [Fact]
    public void NaturalTypeFollowsTheAlphaHint()
    {
        using Raster opaque = Raster.Load(WebpCorpus.Read("lossless-z6.webp"));
        using Raster alpha = Raster.Load(WebpCorpus.Read("lossless-rgba32.webp"));
        Assert.IsType<Raster<Rgb8>>(opaque);
        Assert.IsType<Raster<Rgba8>>(alpha);
        WebpDetails details = Assert.IsType<WebpDetails>(alpha.Metadata.FormatDetails);
        Assert.True(details.Lossless);
        Assert.True(details.HasAlpha);
    }
}
