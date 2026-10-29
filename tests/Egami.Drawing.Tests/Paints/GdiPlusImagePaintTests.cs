using System.Numerics;

using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

/// <summary>
/// An <see cref="ImagePaint"/> with the GDI+ resample filters gives DrawImage's impulse responses at PixelOffsetMode.Half (2.7.6
/// task 16), as Resize does: one white column in a black row, enlarged 4× and reduced 4×, within two levels.
/// </summary>
public class GdiPlusImagePaintTests
{
    [Theory]
    [InlineData(ResampleFilter.GdiPlusBilinear, 62, new[] { 32, 96, 159, 223, 223, 159, 96, 32, 0 })]
    [InlineData(ResampleFilter.GdiPlusBicubic, 62, new[] { 23, 99, 185, 245, 245, 185, 99, 23, 0 })]
    [InlineData(ResampleFilter.GdiPlusHighQualityBilinear, 60, new[] { 2, 18, 50, 98, 155, 187, 187, 155, 98, 50, 18, 2 })]
    [InlineData(ResampleFilter.GdiPlusHighQualityBicubic, 61, new[] { 3, 50, 114, 177, 215, 216, 179, 117, 52, 4 })]
    public void EnlargingMatchesGdiPlus(ResampleFilter filter, int first, int[] gdiPlus)
    {
        byte[] row = Draw(filter, 32, 16, 128);
        for (int i = 0; i < gdiPlus.Length; i++)
        {
            Assert.InRange(row[first + i], gdiPlus[i] - 2, gdiPlus[i] + 2);
        }
    }

    [Theory]
    [InlineData(ResampleFilter.GdiPlusBilinear, 65, 0, 128)]
    [InlineData(ResampleFilter.GdiPlusBicubic, 65, 0, 143)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBilinear, 64, 24, 40)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBicubic, 65, 9, 61)]
    public void ShrinkingMatchesGdiPlus(ResampleFilter filter, int impulse, int at15, int at16)
    {
        byte[] row = Draw(filter, 128, impulse, 32);
        Assert.InRange(row[15], at15 - 2, at15 + 2);
        Assert.InRange(row[16], at16 - 2, at16 + 2);
    }

    [Theory]
    [InlineData(ResampleFilter.GdiPlusHighQualityBilinear)]
    [InlineData(ResampleFilter.GdiPlusHighQualityBicubic)]
    public void HighQualityRowsShadeTheSameInAnyOrder(ResampleFilter filter)
    {
        // Unrotated high-quality sampling keeps each thread's sums across source rows between calls; the order rows are shaded in,
        // and another shader on the same thread, must not change a pixel.
        Random random = new(7);
        using Raster<Rgba8> source = Raster.Create(37, 23, Rgba8.Transparent);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.SetPixel(x, y, new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)));
            }
        }

        ImagePaint paint = new(source, TileMode.Clamp, TileMode.Mirror, new SamplingOptions { Filter = filter });
        Matrix3x2 inverse = Matrix3x2.CreateTranslation(3.3f, -2.6f) * Matrix3x2.CreateScale(37f / 90, 23f / 71);
        int[] down = [.. Enumerable.Range(-5, 85)];
        PremulRgbaF32[][] expected = Shade(Shading.ImageShader.Create(paint, inverse), down);
        PremulRgbaF32[][] reversed = Shade(Shading.ImageShader.Create(paint, inverse), [.. down.Reverse()]);
        int[] order = [.. down.OrderBy(_ => random.Next())];
        PremulRgbaF32[][] shuffled = Shade(Shading.ImageShader.Create(paint, inverse), order);
        for (int r = 0; r < down.Length; r++)
        {
            Assert.Equal(expected[r], reversed[down.Length - 1 - r]);
            Assert.Equal(expected[order[r] + 5], shuffled[r]);
        }

        static PremulRgbaF32[][] Shade(Shading.ImageShader shader, int[] rows)
        {
            return [.. rows.Select(y =>
            {
                PremulRgbaF32[] row = new PremulRgbaF32[100];
                shader.Shade(y, -4, row);
                return row;
            })];
        }
    }

    private static byte[] Draw(ResampleFilter filter, int width, int impulse, int size)
    {
        using Raster<Rgba8> source = Raster.Create(width, 8, new Rgba8(0, 0, 0, 255));
        for (int y = 0; y < 8; y++)
        {
            source.SetPixel(impulse, y, Rgba8.White);
        }

        using Raster<Rgba8> raster = Raster.Create(size, 8, new Rgba8(0, 0, 0, 255));
        using (RasterCanvas<Rgba8> canvas = raster.CreateCanvas())
        {
            canvas.DrawPaint(new ImagePaint(source, TileMode.Mirror, TileMode.Mirror, new SamplingOptions { Filter = filter })
            {
                LocalMatrix = Matrix3x2.CreateScale((float)size / width, 1),
            });
        }

        return [.. Enumerable.Range(0, size).Select(x => raster.GetPixel(x, 4).G)];
    }
}
