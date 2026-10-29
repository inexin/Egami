using Egami.Operations;
using Egami.Pixels;

namespace Egami.Tests.Operations;

/// <summary>
/// The GDI+ resample filters against DrawImage's impulse responses, measured in egami-compare at PixelOffsetMode.Half (Egami's
/// grid) in 2.7.6 task 16: one white column in a black 32-pixel row enlarged 4×, and in a 128-pixel row reduced 4×. Within two levels.
/// </summary>
public class GdiPlusResampleTests
{
    [Theory]
    [InlineData(ResampleFilter.GdiPlusBilinear, 62, new[] { 32, 96, 159, 223, 223, 159, 96, 32, 0 })]
    [InlineData(ResampleFilter.GdiPlusBicubic, 62, new[] { 23, 99, 185, 245, 245, 185, 99, 23, 0 })]
    [InlineData(ResampleFilter.GdiPlusHighQualityBilinear, 60, new[] { 2, 18, 50, 98, 155, 187, 187, 155, 98, 50, 18, 2 })]
    [InlineData(ResampleFilter.GdiPlusHighQualityBicubic, 61, new[] { 3, 50, 114, 177, 215, 216, 179, 117, 52, 4 })]
    public void EnlargingMatchesGdiPlus(ResampleFilter filter, int first, int[] gdiPlus)
    {
        byte[] row = Resize(filter, 32, 16, 128);
        for (int i = 0; i < gdiPlus.Length; i++)
        {
            Assert.InRange(row[first + i], gdiPlus[i] - 2, gdiPlus[i] + 2);
        }
    }

    [Theory]
    [InlineData(ResampleFilter.GdiPlusBilinear, 65, 0, 128)]
    [InlineData(ResampleFilter.GdiPlusBilinear, 64, 0, 0)]
    [InlineData(ResampleFilter.GdiPlusBicubic, 65, 0, 143)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBilinear, 64, 24, 40)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBilinear, 65, 8, 56)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBicubic, 64, 29, 49)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBicubic, 65, 9, 61)]
    public void ShrinkingMatchesGdiPlus(ResampleFilter filter, int impulse, int at15, int at16)
    {
        byte[] row = Resize(filter, 128, impulse, 32);
        Assert.InRange(row[15], at15 - 2, at15 + 2);
        Assert.InRange(row[16], at16 - 2, at16 + 2);
    }

    /// <summary>GDI+'s Bilinear and Bicubic skip pixels when shrinking: a column between two samples vanishes.</summary>
    [Fact]
    public void PointModesSkipPixelsWhenShrinking()
    {
        Assert.All(Resize(ResampleFilter.GdiPlusBilinear, 128, 63, 32), v => Assert.Equal(0, v));
        Assert.Contains(Resize(ResampleFilter.Bilinear, 128, 63, 32), v => v > 0);
    }

    private static byte[] Resize(ResampleFilter filter, int width, int impulse, int size)
    {
        using Raster<Gray8> source = Raster.Create<Gray8>(width, 8);
        for (int y = 0; y < 8; y++)
        {
            source.SetPixel(impulse, y, new Gray8(255));
        }

        using Raster<Gray8> resized = source.Derive(ops => ops.Resize(size, 8, filter));
        return [.. Enumerable.Range(0, size).Select(x => resized.GetPixel(x, 4).L)];
    }
}
