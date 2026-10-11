using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>
/// Phase 2.3.1 task 8: lossy files load with no channel more than 1 level from libwebp's RGB (fancy and replicated chroma), alpha
/// identical for every ALPH filter and compression, and the planes come out of <see cref="YuvRaster.Load(byte[], EgamiContext?, ReaderOptions?)"/> as libwebp decodes them.
/// </summary>
public class WebpLossyDecodeTests
{
    public static TheoryData<string> Lossy() => [.. WebpCorpus.Lossy];

    public static TheoryData<string> NoFancy() => [.. WebpCorpus.Lossy.Where(f => WebpCorpus.NoFancy(f) is not null)];

    [Theory]
    [MemberData(nameof(Lossy))]
    public void RgbIsWithinOneLevelOfLibwebp(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(WebpCorpus.Read(file));
        Compare(file, raster, WebpCorpus.Rgba(file));
        Assert.Empty(raster.Metadata.Warnings);
    }

    [Theory]
    [MemberData(nameof(NoFancy))]
    public void FastUpsamplingIsWithinOneLevelOfLibwebpsNoFancy(string file)
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(WebpCorpus.Read(file), options: new WebpReaderOptions { FastUpsampling = true });
        Compare(file, raster, WebpCorpus.NoFancy(file)!);
    }

    [Theory]
    [MemberData(nameof(Lossy))]
    public void PlanesLoadAsLibwebpDecodesThem(string file)
    {
        using YuvRaster yuv = YuvRaster.Load(WebpCorpus.Read(file));
        YuvRaster<byte> planes = Assert.IsType<YuvRaster<byte>>(yuv);
        Assert.Equal(ChromaFormat.Yuv420, planes.ChromaFormat);
        Assert.Equal(WebpReader.Vp8Color, planes.Color);
        (int width, int height) = WebpCorpus.Size(file);
        byte[] expected = WebpCorpus.Yuv(file);
        int cw = (width + 1) / 2, ch = (height + 1) / 2;
        for (int y = 0; y < height; y++)
        {
            Assert.True(planes.Y.Row(y).SequenceEqual(expected.AsSpan(y * width, width)), $"{file}: Y row {y}");
        }

        for (int y = 0; y < ch; y++)
        {
            Assert.True(planes.U!.Row(y).SequenceEqual(expected.AsSpan((width * height) + (y * cw), cw)), $"{file}: U row {y}");
            Assert.True(planes.V!.Row(y).SequenceEqual(expected.AsSpan((width * height) + (cw * ch) + (y * cw), cw)), $"{file}: V row {y}");
        }

        Assert.Equal(WebpCorpus.Info(file).GetProperty("alpha").GetBoolean(), planes.HasAlpha);
        YuvRasterInfo info = YuvRaster.Probe(WebpCorpus.Read(file));
        Assert.True(info.CanLoad);
    }

    [Fact]
    public void LosslessFilesHaveNoPlanes()
    {
        Assert.Throws<NotSupportedException>(() => YuvRaster.Load(WebpCorpus.Read("lossless-z6.webp")));
        Assert.False(YuvRaster.Probe(WebpCorpus.Read("lossless-z6.webp")).CanLoad);
    }

    [Fact]
    public void NaturalTypeFollowsTheAlphaChunk()
    {
        using Raster opaque = Raster.Load(WebpCorpus.Read("lossy-q75.webp"));
        using Raster alpha = Raster.Load(WebpCorpus.Read("lossy-alpha-m1-f1.webp"));
        Assert.IsType<Raster<Rgb8>>(opaque);
        Assert.IsType<Raster<Rgba8>>(alpha);
        Assert.False(Assert.IsType<WebpDetails>(alpha.Metadata.FormatDetails).Lossless);
    }

    private static void Compare(string file, Raster<Rgba8> raster, byte[] expected)
    {
        (int width, int height) = WebpCorpus.Size(file);
        Assert.Equal((width, height), (raster.Width, raster.Height));
        int worst = 0;
        for (int y = 0; y < height; y++)
        {
            ReadOnlySpan<byte> row = MemoryMarshal.AsBytes(raster.Row(y));
            ReadOnlySpan<byte> reference = expected.AsSpan(y * width * 4, width * 4);
            for (int i = 0; i < row.Length; i++)
            {
                int d = Math.Abs(row[i] - reference[i]);
                if (i % 4 == 3)
                {
                    Assert.True(d == 0, $"{file}: alpha at ({i / 4}, {y}) is {row[i]}, libwebp gives {reference[i]}.");
                }

                worst = Math.Max(worst, d);
            }
        }

        Assert.True(worst <= 1, $"{file}: a channel is {worst} levels from libwebp.");
    }
}
