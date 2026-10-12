using Egami.Analysis;
using Egami.Formats.Gif;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.DocSamples;

[Collection("Samples")]
public class Palettes
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void Poster()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region quantize
        // A 16-color poster with Atkinson's dither; the pixel type stays as it was.
        photo.Apply(ops => ops.Quantize(new PaletteOptions { MaxColors = 16, Dither = DitherMode.Atkinson }), Ct);
        photo.Save("poster.png");
        #endregion

        Assert.True(photo.CountColors() <= 16);
    }

    [Fact]
    public void EInk()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region remap
        // Map onto a fixed palette: here an e-ink display's six inks.
        Palette inks = Palette.FromColors([Rgba8.Black, Rgba8.White, new(255, 0, 0, 255), new(255, 255, 0, 255), new(0, 0, 255, 255), new(0, 255, 0, 255)]);
        photo.Apply(ops => ops.Remap(inks, DitherMode.FloydSteinberg), Ct);
        #endregion

        Assert.True(photo.CountColors() <= 6);
    }

    [Fact]
    public void BuildAndReuse()
    {
        using SampleFiles files = new();
        using Raster first = Raster.Load("duck.png");
        using Raster second = Raster.Load("egret.png");

        #region build
        // Build one palette from an image, polished by k-means, and use it for other images and for every GIF frame.
        Palette shared = Palette.Build(first, new PaletteOptions { MaxColors = 64, RefineIterations = 6 });
        second.Apply(ops => ops.Remap(shared), Ct);
        second.Save("egret.gif", new GifWriterOptions { Palette = new PaletteOptions { Palette = shared, Dither = DitherMode.Bayer4x4 } });
        #endregion

        Assert.True(shared.Count <= 64);
    }

    [Fact]
    public void ReadyMade()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region ready-made
        // Ready-made palettes: web-safe colors, black and white, gray levels, and color cubes such as 3-3-2.
        using Raster web = photo.Derive(ops => ops.Remap(Quantization.Palettes.WebSafe, DitherMode.Ordered), Ct);
        using Raster bilevel = photo.Derive(ops => ops.Remap(Quantization.Palettes.BlackAndWhite, DitherMode.Riemersma), Ct);
        using Raster fourGrays = photo.Derive(ops => ops.Remap(Quantization.Palettes.Grayscale(4)), Ct);
        using Raster rgb332 = photo.Derive(ops => ops.Remap(Quantization.Palettes.Uniform(8, 8, 4), DitherMode.Bayer16x16), Ct);
        #endregion

        Assert.True(bilevel.CountColors() <= 2);
    }

    [Fact]
    public void CustomKernel()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region kernel
        // A custom error-diffusion kernel: half the error to the right, a quarter down and a quarter down-left, at 80% strength,
        // scanning every other row backwards.
        ErrorDiffusionKernel kernel = new(new[,] { { 0, 0, 2 }, { 1, 1, 0 } }, divisor: 4, origin: 1);
        photo.Apply(ops => ops.Quantize(new PaletteOptions { MaxColors = 8, Kernel = kernel, DitherStrength = 0.8f, Serpentine = true }), Ct);
        #endregion

        Assert.True(photo.CountColors() <= 8);
    }

    [Fact]
    public void Indexed()
    {
        using SampleFiles files = new();
        using Raster photo = Raster.Load("duck.png");

        #region indexed
        // Indices plus palette, for a PDF indexed-color image or a format of your own.
        using IndexedImage indexed = photo.ToIndexed(new PaletteOptions { MaxColors = 64 });
        byte[] lookup = [.. indexed.Palette.Colors.SelectMany(c => new[] { c.R, c.G, c.B })];
        byte[] samples = indexed.Indices.ToArray();
        #endregion

        Assert.Equal(photo.Width * photo.Height, samples.Length);
        Assert.True(lookup.Length <= 64 * 3);
    }

    [Fact]
    public void Dominant()
    {
        using SampleFiles files = new();
        using Raster logo = Raster.Load("duck.png");

        #region dominant
        // The main colors and how much of the picture each covers, e.g. to pick brand colors from a logo or a placeholder color.
        IReadOnlyList<WeightedColor> colors = logo.DominantColors(5);
        foreach ((Rgba8 color, double weight) in colors)
        {
            Console.WriteLine($"#{color.R:X2}{color.G:X2}{color.B:X2} covers {weight:P0}");
        }
        #endregion

        Assert.Equal(5, colors.Count);
    }
}
