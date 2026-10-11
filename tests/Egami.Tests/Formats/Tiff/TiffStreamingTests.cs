using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Tests.Formats.Tiff;

/// <summary>TIFF-specific streaming: pyramids for reduced decodes, regions of tiled pages, CMYK through its profile.</summary>
public class TiffStreamingTests
{
    [Theory]
    [InlineData(64, 48, 0)]
    [InlineData(40, 30, 0)]
    [InlineData(32, 24, 1)]
    public void ReducedDecodesUseThePyramid(int width, int height, int sub)
    {
        byte[] data = TiffCorpus.Main.Read("pyramid-exact.tif");
        using RasterList pages = Raster.LoadAll<Rgb8>(data, options: new TiffReaderOptions { IncludeReducedPages = true });
        using Raster<Rgb8> reduced = Raster.Load<Rgb8>(data, options: new ReaderOptions { MinimumSize = new Size(width, height) });
        Raster level = pages[sub + 1];
        Assert.Equal((level.Width, level.Height), (reduced.Width, reduced.Height));
        Assert.Equal(TiffReference.Bytes(level), TiffReference.Bytes(reduced));
        Assert.Equal(0, ((TiffDetails)reduced.Metadata.FormatDetails!).PageIndex);
        Assert.False(((TiffDetails)reduced.Metadata.FormatDetails!).IsReducedResolution);
    }

    [Fact]
    public void BelowThePyramidTheSmallestLevelIsBoxAveraged()
    {
        byte[] data = TiffCorpus.Main.Read("pyramid-exact.tif");
        using RasterList pages = Raster.LoadAll<Rgba8>(data, options: new TiffReaderOptions { IncludeReducedPages = true });
        using Raster<Rgba8> reduced = Raster.Load<Rgba8>(data, options: new ReaderOptions { MinimumSize = new Size(16, 12) });
        using Raster<Rgba8> expected = Raster.Load<Rgba8>(Save(pages[2]), options: new ReaderOptions { MinimumSize = new Size(16, 12) });
        Assert.Equal((16, 12), (reduced.Width, reduced.Height));
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(reduced));
    }

    private static byte[] Save(Raster raster)
    {
        using MemoryStream png = new();
        raster.Save(png, Egami.Formats.Png.PngFormat.Info);
        return png.ToArray();
    }

    [Fact]
    public void RegionsOfTiledPagesMatchACroppedLoad()
    {
        byte[] data = TiffCorpus.Main.Read("rgb8-tiles16.tif");
        using Raster<Rgb8> whole = Raster.Load<Rgb8>(data);
        Rect region = new(13, 5, 30, 20);
        using Raster<Rgb8> part = Raster.Load<Rgb8>(data, options: new ReaderOptions { Region = region });
        for (int y = 0; y < region.Height; y++)
        {
            Assert.True(whole.Row(region.Y + y).Slice(region.X, region.Width).SequenceEqual(part.Row(y)));
        }
    }

    [Fact]
    public void CmykPagesConvertThroughTheirProfile()
    {
        byte[] data = TiffCorpus.Main.Read("meta-cmyk-icc.tif");
        using Raster<Cmyk8> cmyk = Raster.Load<Cmyk8>(data);
        Assert.False(cmyk.Metadata.IccProfile.IsEmpty);
        using Raster<Rgb8> converted = Raster.Load<Rgb8>(data, options: new ReaderOptions { ConvertColorTo = ColorProfile.Srgb });
        using Raster<Rgb8> expected = cmyk.ConvertColor<Rgb8>(ColorProfile.Srgb);
        using Raster<Rgb8> plain = Raster.Load<Rgb8>(data);
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(converted));
        Assert.NotEqual(TiffReference.Bytes(plain), TiffReference.Bytes(converted));
    }
}
