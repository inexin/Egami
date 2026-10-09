using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Quantization;

public class PaletteTests(ITestOutputHelper output)
{
    private static readonly string Ops = Path.Combine(AppContext.BaseDirectory, "images", "ops");

    [Fact]
    public void ImagesWith256ColorsOrFewerComeBackExact()
    {
        Rgba8[] pixels = [.. Enumerable.Range(0, 5000).Select(i => new Rgba8((byte)(i % 16 * 17), (byte)(i / 16 % 16 * 17), 9, 255))];
        BuiltPalette palette = Build(pixels, PaletteOptions.Default, binaryAlpha: true);
        Assert.True(palette.Exact);
        Assert.Equal(256, palette.Colors.Length);
        Assert.Equal(-1, palette.TransparentIndex);
        foreach (Rgba8 pixel in pixels)
        {
            Assert.Equal(pixel, palette.Colors[palette.ExactTable!.IndexOf(pixel)]);
        }
    }

    [Fact]
    public void TransparentPixelsTakeOneEntry()
    {
        Rgba8[] pixels = [new(1, 2, 3, 255), new(9, 9, 9, 0), new(200, 9, 9, 10), new(4, 5, 6, 255), new(1, 2, 3, 200)];
        BuiltPalette palette = Build(pixels, PaletteOptions.Default, binaryAlpha: true);
        Assert.True(palette.Exact);
        Assert.Equal(3, palette.Colors.Length);
        Assert.Equal(2, palette.TransparentIndex);
        Assert.Equal(-1, palette.ExactTable!.IndexOf(new Rgba8(200, 9, 9, 10)));
        Assert.Equal(0, palette.ExactTable.IndexOf(new Rgba8(1, 2, 3, 200)));

        // 255 opaque colors plus transparency fit 256; 256 opaque colors plus transparency do not.
        Rgba8[] full = [.. Enumerable.Range(0, 255).Select(i => new Rgba8((byte)i, 0, 0, 255)), default];
        Assert.True(Build(full, PaletteOptions.Default, binaryAlpha: true).Exact);
        Assert.False(Build([.. full, new Rgba8(0, 1, 0, 255)], PaletteOptions.Default, binaryAlpha: true).Exact);
    }

    [Fact]
    public void PartialAlphaIsKeptForPalettePng()
    {
        Rgba8[] pixels = [new(1, 2, 3, 255), new(1, 2, 3, 128), new(0, 0, 0, 0)];
        BuiltPalette palette = Build(pixels, PaletteOptions.Default, binaryAlpha: false);
        Assert.True(palette.Exact);
        Assert.Equal(pixels, palette.Colors);
    }

    [Fact]
    public void WuFindsSeparateClusters()
    {
        // Four tight clusters of many colors each; four palette entries land on their centers.
        Random random = new(5);
        Rgba8[] centers = [new(20, 20, 20, 255), new(230, 40, 40, 255), new(40, 220, 60, 255), new(50, 60, 230, 255)];
        Rgba8[] pixels = [.. Enumerable.Range(0, 20_000).Select(i =>
        {
            Rgba8 c = centers[i % 4];
            return new Rgba8((byte)(c.R + random.Next(-6, 7)), (byte)(c.G + random.Next(-6, 7)), (byte)(c.B + random.Next(-6, 7)), 255);
        })];
        BuiltPalette palette = Build(pixels, new PaletteOptions { MaxColors = 4 }, binaryAlpha: true);
        Assert.False(palette.Exact);
        Assert.Equal(4, palette.Colors.Length);
        foreach (Rgba8 center in centers)
        {
            Rgba8 nearest = palette.Colors.MinBy(c => Distance(c, center));
            Assert.True(Distance(nearest, center) <= 3 * 2 * 2, $"{center} -> {nearest}");
        }
    }

    [Fact]
    public void FourDimensionalWuKeepsAlpha()
    {
        Rgba8[] pixels = [.. Enumerable.Range(0, 4000).Select(i => new Rgba8((byte)i, (byte)(i * 7), (byte)(i * 13), (byte)(i % 2 == 0 ? 255 : 60)))];
        BuiltPalette palette = Build(pixels, new PaletteOptions { MaxColors = 16 }, binaryAlpha: false);
        Assert.False(palette.Exact);
        Assert.Contains(palette.Colors, c => c.A is > 40 and < 80);
        Assert.Contains(palette.Colors, c => c.A > 240);
    }

    [Fact]
    public void MapperFindsTheNearestColor()
    {
        Rgba8[] colors = [new(0, 0, 0, 255), new(255, 255, 255, 255), new(255, 0, 0, 255), default];
        using PaletteMapper mapper = new(colors, 3, withAlpha: false, threshold: 128);
        Assert.Equal(0, mapper.Map(new Rgba8(20, 30, 10, 255)));
        Assert.Equal(1, mapper.Map(new Rgba8(220, 230, 210, 255)));
        Assert.Equal(2, mapper.Map(new Rgba8(200, 30, 10, 255)));
        Assert.Equal(3, mapper.Map(new Rgba8(200, 30, 10, 100)));

        // A cell's answer is its center's, whichever pixel filled it.
        Assert.Equal(mapper.Map(new Rgba8(128, 0, 0, 255)), mapper.Map(new Rgba8(131, 3, 3, 255)));
    }

    [Theory]
    [InlineData(3, false)]
    [InlineData(17, false)]
    [InlineData(256, false)]
    [InlineData(100, true)]
    public void FastSearchesEqualTheFullScalarSearch(int colors, bool withAlpha)
    {
        // Duplicated entries make ties, which both must break toward the lower index.
        Random random = new(colors);
        Rgba8[] palette = [.. Enumerable.Range(0, colors).Select(i => new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256)))];
        palette[^1] = palette[0];
        using PaletteMapper mapper = new(palette, withAlpha ? -1 : colors / 2, withAlpha, 128);
        for (int i = 0; i < 20_000; i++)
        {
            Rgba8 pixel = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            Assert.Equal(mapper.NearestScalar(pixel), mapper.NearestVector(pixel));
        }
    }

    /// <summary>The quantized-palette PSNR baseline for the photo set, at 256 colors without dithering.</summary>
    [Theory]
    [InlineData("duck.png", 34.0)]
    [InlineData("egret.png", 36.0)]
    public void PhotoPsnrBaseline(string file, double floor)
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(Ops, file));
        Rgba8[] pixels = new Rgba8[photo.Width * photo.Height];
        photo.CopyPixelsTo(pixels);
        BuiltPalette palette = Build(pixels, PaletteOptions.Default, binaryAlpha: true);
        Assert.False(palette.Exact);
        using PaletteMapper mapper = new(palette.Colors, palette.TransparentIndex, withAlpha: false, threshold: 128);
        double sum = 0;
        foreach (Rgba8 p in pixels)
        {
            sum += Distance(p, palette.Colors[mapper.Map(p)]);
        }

        double psnr = 10 * Math.Log10(255.0 * 255 / (sum / (pixels.Length * 3)));
        output.WriteLine($"{file}: {palette.Colors.Length} colors, {psnr:F2} dB");
        Assert.True(psnr >= floor, $"{psnr:F2} dB");
    }

    private static int Distance(Rgba8 a, Rgba8 b)
    {
        return ((a.R - b.R) * (a.R - b.R)) + ((a.G - b.G) * (a.G - b.G)) + ((a.B - b.B) * (a.B - b.B));
    }

    private static BuiltPalette Build(Rgba8[] pixels, PaletteOptions options, bool binaryAlpha)
    {
        const int Row = 100;
        int rows = (pixels.Length + Row - 1) / Row;
        return PaletteBuilder.Build(rows, y => pixels.AsMemory(y * Row, Math.Min(Row, pixels.Length - (y * Row))), options, binaryAlpha);
    }
}
