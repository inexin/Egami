using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Quantization;

public class DitherTests(ITestOutputHelper output)
{
    private static readonly Rgba8 Black = new(0, 0, 0, 255);
    private static readonly Rgba8 White = new(255, 255, 255, 255);

    [Fact]
    public void FloydSteinbergMatchesTheHandComputedCase()
    {
        // Gray 100 everywhere, black and white palette. Row 0: 100 -> black (error 100); 100 + 7/16 * 100 = 144 -> white
        // (error -111); 100 - 49 = 51 -> black. Row 1 starts with 5/16 * 100 - 3/16 * 111 = 10 -> 110 -> black, then
        // -302/16 + 7/16 * 110 = 29 -> 129 -> white, then (144 - 7 * 126) / 16 = -46 -> 54 -> black.
        byte[] indices = Map(3, 2, Gray(3, 2, 100), [Black, White], -1, DitherMode.FloydSteinberg);
        Assert.Equal(new byte[] { 0, 1, 0, 0, 1, 0 }, indices);
    }

    [Fact]
    public void TransparentPixelsNeitherReceiveNorSpreadError()
    {
        Rgba8[] pixels = [new(100, 100, 100, 255), default, new(100, 100, 100, 255)];
        byte[] indices = Map(3, 1, pixels, [Black, White, default], 2, DitherMode.FloydSteinberg);
        Assert.Equal(new byte[] { 0, 2, 0 }, indices);
    }

    [Fact]
    public void NoDitheringMapsEachPixelToItsNearest()
    {
        Assert.Equal(new byte[] { 0, 0, 0, 0, 0, 0 }, Map(3, 2, Gray(3, 2, 100), [Black, White], -1, DitherMode.None));
    }

    [Fact]
    public void OrderedDitheringIsAStablePattern()
    {
        byte[] first = Map(16, 16, Gray(16, 16, 128), [Black, White], -1, DitherMode.Ordered);
        byte[] second = Map(16, 16, Gray(16, 16, 128), [Black, White], -1, DitherMode.Ordered);
        Assert.Equal(first, second);
        int white = first.Count(i => i == 1);
        Assert.InRange(white, 112, 144);

        // The same 8 x 8 tile repeats.
        for (int y = 0; y < 16; y++)
        {
            for (int x = 0; x < 16; x++)
            {
                Assert.Equal(first[((y % 8) * 16) + (x % 8)], first[(y * 16) + x]);
            }
        }
    }

    [Fact]
    public void ExactPalettesAreNeverDithered()
    {
        Rgba8[] pixels = [Black, White, new(100, 100, 100, 255), White];
        BuiltPalette palette = PaletteBuilder.Build(1, _ => pixels, PaletteOptions.Default, binaryAlpha: true);
        using PaletteMapper mapper = new(palette.Colors, palette.TransparentIndex, false, 128);
        byte[] indices = new byte[4];
        FrameMapper.Map(4, 1, (_, row) => pixels.CopyTo(row), palette, mapper, DitherMode.FloydSteinberg, indices);
        Assert.Equal(pixels, indices.Select(i => palette.Colors[i]));
    }

    /// <summary>Dithered PSNR for the photo set at 256 colors, recorded next to the undithered baseline.</summary>
    [Theory]
    [InlineData("duck.png")]
    [InlineData("egret.png")]
    public void DitheredPhotoPsnr(string file)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", file));
        int width = photo.Width;
        BuiltPalette palette = PaletteBuilder.Build(photo.Height, y => photo.Row(y).ToArray(), PaletteOptions.Default, binaryAlpha: true);
        using PaletteMapper mapper = new(palette.Colors, palette.TransparentIndex, false, 128);
        foreach (DitherMode mode in new[] { DitherMode.None, DitherMode.FloydSteinberg, DitherMode.Ordered })
        {
            byte[] indices = new byte[width * photo.Height];
            FrameMapper.Map(width, photo.Height, (y, row) => photo.Row(y).CopyTo(row), palette, mapper, mode, indices);
            double sum = 0;
            for (int y = 0; y < photo.Height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    Rgba8 a = photo.Row(y)[x];
                    Rgba8 b = palette.Colors[indices[(y * width) + x]];
                    sum += ((a.R - b.R) * (a.R - b.R)) + ((a.G - b.G) * (a.G - b.G)) + ((a.B - b.B) * (a.B - b.B));
                }
            }

            double psnr = 10 * Math.Log10(255.0 * 255 / (sum / (width * photo.Height * 3)));
            output.WriteLine($"{file} {mode}: {psnr:F2} dB");
            Assert.True(psnr > 30, $"{mode}: {psnr:F2} dB");
        }
    }

    private static Rgba8[] Gray(int width, int height, byte value)
    {
        return [.. Enumerable.Repeat(new Rgba8(value, value, value, 255), width * height)];
    }

    private static byte[] Map(int width, int height, Rgba8[] pixels, Rgba8[] colors, int transparent, DitherMode mode)
    {
        BuiltPalette palette = new(colors, transparent, false, null);
        using PaletteMapper mapper = new(colors, transparent, false, 128);
        byte[] indices = new byte[width * height];
        FrameMapper.Map(width, height, (y, row) => pixels.AsSpan(y * width, width).CopyTo(row), palette, mapper, mode, indices);
        return indices;
    }
}
