using System.Numerics;

using Egami.Drawing.Rasterization;
using Egami.Drawing.Shading;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Paints;

/// <summary>
/// An image paint that reads its image's rows as it shades them, instead of converting the whole image first, gives the same pixels,
/// and does so only when the image is not the pixels being drawn.
/// </summary>
public class ImageRowsTests
{
    public static TheoryData<ResampleFilter, TileMode, ColorSpace> Cases()
    {
        TheoryData<ResampleFilter, TileMode, ColorSpace> data = [];
        foreach (ResampleFilter filter in Enum.GetValues<ResampleFilter>())
        {
            data.Add(filter, TileMode.Clamp, ColorSpace.Encoded);
            data.Add(filter, TileMode.Decal, ColorSpace.Encoded);
        }

        data.Add(ResampleFilter.GdiPlusBilinear, TileMode.Repeat, ColorSpace.Linear);
        data.Add(ResampleFilter.Lanczos3, TileMode.Mirror, ColorSpace.Linear);
        data.Add(ResampleFilter.GdiPlusHighQualityBicubic, TileMode.Mirror, ColorSpace.Encoded);
        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RowsShadeAsTheConvertedImage(ResampleFilter filter, TileMode tile, ColorSpace colorSpace)
    {
        using Raster<Rgba8> source = Noise(150, 120);
        using Raster<PremulRgba8> pixels = Raster.Create<PremulRgba8>(300, 200);
        using RasterTarget<PremulRgba8> target = new(pixels.Frames[0].Plane);
        ImagePaint paint = new(source, tile, tile, new SamplingOptions { Filter = filter, ColorSpace = colorSpace });

        // Enlarged and flipped, and shrunk; rows are shaded in a shuffled order, past the image, in runs up to one chunk (the longest a shader is given).
        Matrix3x2[] mappings = [Matrix3x2.CreateScale(-0.61f, 0.47f) * Matrix3x2.CreateTranslation(140.3f, -9.6f), Matrix3x2.CreateScale(1.7f, 1.3f) * Matrix3x2.CreateTranslation(-20.2f, 3.1f)];
        int[] rows = [.. Enumerable.Range(-10, 220).OrderBy(y => (y * 7919) % 220)];
        foreach (Matrix3x2 inverse in mappings)
        {
            ImageShader converted = ImageShader.Create(paint, inverse);
            ImageShader read = ImageShader.Create(paint, inverse, target);
            Assert.False(converted.ReadsInPlace);
            Assert.True(read.ReadsInPlace);
            foreach (int y in rows)
            {
                foreach (int length in new[] { 37, Shader.Chunk })
                {
                    PremulRgbaF32[] expected = new PremulRgbaF32[length], actual = new PremulRgbaF32[length];
                    converted.Shade(y, -17, expected);
                    read.Shade(y, -17, actual);
                    Assert.Equal(expected, actual);
                }
            }
        }
    }

    [Fact]
    public void AnImageIsConvertedWhenItIsThePixelsDrawn()
    {
        using Raster<Rgba8> source = Noise(150, 120);
        using RasterTarget<Rgba8> itself = new(source.Frames[0].Plane);
        using Raster<Rgba8> other = Raster.Create<Rgba8>(150, 120);
        using RasterTarget<Rgba8> elsewhere = new(other.Frames[0].Plane);
        ImagePaint paint = new(source, sampling: new SamplingOptions { Filter = ResampleFilter.GdiPlusBilinear });
        Matrix3x2 inverse = Matrix3x2.CreateScale(0.5f);
        Assert.False(ImageShader.Create(paint, inverse, itself).ReadsInPlace);
        Assert.True(ImageShader.Create(paint, inverse, elsewhere).ReadsInPlace);
    }

    [Fact]
    public void DrawingAnImageOntoItselfReadsWhatTheDrawStartedWith()
    {
        using Raster<Rgba8> drawn = Noise(150, 120);
        using Raster<Rgba8> copy = Noise(150, 120);
        static Paint Shrunk(Raster image) => new ImagePaint(image, sampling: new SamplingOptions { Filter = ResampleFilter.GdiPlusBicubic })
        {
            LocalMatrix = Matrix3x2.CreateScale(0.8f, 0.9f) * Matrix3x2.CreateTranslation(5, 3),
        };

        using (RasterCanvas<Rgba8> canvas = drawn.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 150, 120), Shrunk(drawn));
        }

        using Raster<Rgba8> expected = Noise(150, 120);
        using (RasterCanvas<Rgba8> canvas = expected.CreateCanvas())
        {
            canvas.FillRect(new RectF(0, 0, 150, 120), Shrunk(copy));
        }

        for (int y = 0; y < 120; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(drawn.Row(y)), $"row {y}");
        }
    }

    private static Raster<Rgba8> Noise(int width, int height)
    {
        Random random = new(11);
        Raster<Rgba8> raster = Raster.Create(width, height, Rgba8.Transparent);
        for (int y = 0; y < height; y++)
        {
            Span<Rgba8> row = raster.Row(y);
            for (int x = 0; x < width; x++)
            {
                row[x] = new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            }
        }

        return raster;
    }
}
