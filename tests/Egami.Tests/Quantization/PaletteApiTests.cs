using Egami.Analysis;
using Egami.Colors;
using Egami.Formats.Bmp;
using Egami.Formats.Gif;
using Egami.Formats.Png;
using Egami.Formats.Tga;
using Egami.Pixels;
using Egami.Quantization;

namespace Egami.Tests.Quantization;

/// <summary>Phase 2.5.5 stage B: the public palette, quantize and remap, the dithers, fixed palettes in writers, dominant colors.</summary>
public class PaletteApiTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "ops");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static Raster Duck() => Raster.Load(Path.Combine(Images, "duck.png"));

    public static TheoryData<DitherMode> Modes() => [.. Enum.GetValues<DitherMode>()];

    [Fact]
    public void KernelWeightsAreThePublishedOnes()
    {
        static double[] Weights(DitherMode mode) => [.. DitherKernels.Kernel(mode)!.Taps.OrderBy(t => t.Dy).ThenBy(t => t.Dx).Select(t => t.Weight)];
        Assert.Equal([7 / 16.0, 3 / 16.0, 5 / 16.0, 1 / 16.0], Weights(DitherMode.FloydSteinberg));
        Assert.Equal([8 / 32.0, 4 / 32.0, 2 / 32.0, 4 / 32.0, 8 / 32.0, 4 / 32.0, 2 / 32.0], Weights(DitherMode.Burkes));
        Assert.Equal([1 / 8.0, 1 / 8.0, 1 / 8.0, 1 / 8.0, 1 / 8.0, 1 / 8.0], Weights(DitherMode.Atkinson));
        Assert.Equal([2 / 4.0, 1 / 4.0, 1 / 4.0], Weights(DitherMode.SierraLite));
        Assert.Equal(1, Weights(DitherMode.JarvisJudiceNinke).Sum(), 12);
        Assert.Equal(1, Weights(DitherMode.Stucki).Sum(), 12);
        Assert.Equal(1, Weights(DitherMode.Sierra3).Sum(), 12);
        Assert.Equal(1, Weights(DitherMode.Sierra2).Sum(), 12);
        Assert.Equal(1, Weights(DitherMode.StevensonArce).Sum(), 12);
        Assert.Equal(12, DitherKernels.Kernel(DitherMode.JarvisJudiceNinke)!.Taps.Length);
        Assert.Equal(12, DitherKernels.Kernel(DitherMode.StevensonArce)!.Taps.Length);
        Assert.Equal(FrameMapper.Bayer.ToArray(), DitherKernels.Bayer(8));
        Assert.Equal([0, 8, 2, 10, 12, 4, 14, 6, 3, 11, 1, 9, 15, 7, 13, 5], DitherKernels.Bayer(4).Select(b => (int)b));
        Assert.Equal(Enumerable.Range(0, 256), DitherKernels.Bayer(16).Select(b => (int)b).Order());
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void QuantizeLeavesAtMostNColorsWithEveryDither(DitherMode mode)
    {
        using Raster duck = Duck();
        using Raster<Rgba8> eight = duck.As<Rgba8>();
        eight.Apply(o => o.Quantize(new PaletteOptions { MaxColors = 16, Dither = mode }), Ct);
        Assert.InRange(eight.CountColors(), 2, 16);
        using Raster<Rgba16> wide = duck.As<Rgba16>();
        wide.Apply(o => o.Quantize(new PaletteOptions { MaxColors = 16, Dither = mode, Serpentine = true, DitherStrength = 0.7f }), Ct);
        Assert.InRange(wide.CountColors(), 2, 16);
        CompareResult result = Raster.Compare(duck, eight);
        Assert.True(result.Overall.Psnr > 22, $"{mode}: {result.Overall.Psnr:F2} dB");
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void PosterizeTakesEveryDither(DitherMode mode)
    {
        using Raster duck = Duck();
        using Raster<Rgb8> rgb = duck.As<Rgb8>();
        rgb.Apply(o => o.Posterize(3, mode), Ct);
        for (int y = 0; y < rgb.Height; y += 7)
        {
            foreach (Rgb8 p in rgb.Row(y))
            {
                Assert.Contains(p.R, new byte[] { 0, 128, 255 });
                Assert.Contains(p.B, new byte[] { 0, 128, 255 });
            }
        }
    }

    [Fact]
    public void StrengthZeroIsNoDither()
    {
        using Raster duck = Duck();
        using Raster plain = duck.Derive(o => o.Quantize(new PaletteOptions { MaxColors = 32, Dither = DitherMode.None }), Ct);
        foreach (DitherMode mode in new[] { DitherMode.Atkinson, DitherMode.Bayer4x4, DitherMode.Riemersma })
        {
            using Raster none = duck.Derive(o => o.Quantize(new PaletteOptions { MaxColors = 32, Dither = mode, DitherStrength = 0, ExactMatching = false }), Ct);
            Assert.True(Raster.Compare(plain, none).IsIdentical, mode.ToString());
        }
    }

    [Fact]
    public void RefinementAndExactMatchingNeverLowerPsnr()
    {
        foreach (string name in new[] { "duck.png", "egret.png" })
        {
            using Raster image = Raster.Load(Path.Combine(Images, name));
            foreach (DitherMode mode in new[] { DitherMode.None, DitherMode.FloydSteinberg })
            {
                double Psnr(PaletteOptions options)
                {
                    using Raster q = image.Derive(o => o.Quantize(options), Ct);
                    return Raster.Compare(image, q).Overall.Psnr;
                }

                PaletteOptions basic = new() { MaxColors = 32, Dither = mode };
                double baseline = Psnr(basic);
                Assert.True(Psnr(basic with { RefineIterations = 6 }) >= baseline - 1e-9, $"{name} {mode} refined");
                Assert.True(Psnr(basic with { ExactMatching = true }) >= baseline - 0.05, $"{name} {mode} exact");
            }
        }
    }

    [Fact]
    public void DefaultsKeepTheClassicMapping()
    {
        using Raster duck = Duck();
        PaletteOptions defaults = PaletteOptions.Default;
        Assert.True(defaults.IsClassic);
        using MemoryStream a = new(), b = new();
        duck.Save(a, "gif");
        duck.Save(b, "gif", new GifWriterOptions { Palette = new PaletteOptions { Dither = DitherMode.FloydSteinberg, DitherStrength = 1 } });
        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Fact]
    public void FixedPaletteRoundTripsThroughEveryWriter()
    {
        using Raster duck = Duck();
        Palette eink = Palette.FromColors([Rgba8.Black, Rgba8.White, new(255, 0, 0, 255), new(255, 255, 0, 255), new(0, 0, 255, 255), new(0, 255, 0, 255)]);
        PaletteOptions fixedOptions = new() { FixedPalette = eink };
        HashSet<Rgba8> allowed = [.. eink.Colors];
        foreach ((string format, Egami.Formats.WriterOptions options) in new (string, Egami.Formats.WriterOptions)[]
        {
            ("gif", new GifWriterOptions { Palette = fixedOptions }),
            ("png", new PngWriterOptions { ColorType = PngColorType.Palette, Palette = fixedOptions }),
            ("bmp", new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed8, Palette = fixedOptions }),
            ("tga", new TgaWriterOptions { PixelFormat = TgaPixelFormat.Indexed8, Palette = fixedOptions }),
        })
        {
            using MemoryStream file = new();
            duck.Save(file, format, options);
            file.Position = 0;
            using Raster loaded = Raster.Load(file);
            IReadOnlyList<Rgba16>? colors = loaded.GetColors(16);
            Assert.NotNull(colors);
            Assert.All(colors, c => Assert.Contains(c.ToRgba8() with { A = 255 }, allowed));
        }
    }

    [Fact]
    public void OversizePaletteThrowsNamingBothNumbers()
    {
        using Raster duck = Duck();
        Palette big = Palette.FromColors([.. Enumerable.Range(0, 20).Select(i => new Rgba8((byte)(i * 12), 0, 0, 255))]);
        using MemoryStream file = new();
        ArgumentException e = Assert.Throws<ArgumentException>(() =>
            duck.Save(file, "bmp", new BmpWriterOptions { PixelFormat = BmpPixelFormat.Indexed4, Palette = new PaletteOptions { FixedPalette = big } }));
        Assert.Contains("20", e.Message);
        Assert.Contains("16", e.Message);
    }

    [Fact]
    public void RemapKeepsAlphaAndUsesOnlyPaletteColors()
    {
        using Raster<Rgba8> image = Raster.Create<Rgba8>(32, 32, new Rgba8(200, 30, 40, 128));
        image.SetPixel(3, 3, new Rgba8(10, 250, 10, 255));
        Palette palette = Palettes.Uniform(2, 2, 2);
        image.Apply(o => o.Remap(palette, DitherMode.None), Ct);
        Assert.Equal(new Rgba8(255, 0, 0, 128), image.GetPixel(0, 0));
        Assert.Equal(new Rgba8(0, 255, 0, 255), image.GetPixel(3, 3));
    }

    [Fact]
    public void ToIndexedMatchesQuantize()
    {
        using Raster duck = Duck();
        PaletteOptions options = new() { MaxColors = 24, Dither = DitherMode.Sierra2 };
        using IndexedImage indexed = duck.ToIndexed(options);
        using Raster<Rgba8> quantized = duck.As<Rgba8>();
        quantized.Apply(o => o.Quantize(options), Ct);
        Assert.Equal(duck.Width * duck.Height, indexed.Indices.Length);
        Assert.InRange(indexed.Palette.Count, 2, 24);
        for (int y = 0; y < duck.Height; y += 13)
        {
            ReadOnlySpan<byte> row = indexed.Row(y);
            for (int x = 0; x < duck.Width; x++)
            {
                Assert.Equal(quantized.GetPixel(x, y), indexed.Palette.Colors[row[x]]);
            }
        }
    }

    [Fact]
    public void ReadyMadePalettes()
    {
        Assert.Equal(216, Palettes.WebSafe.Count);
        Assert.Contains(new Rgba8(51, 102, 153, 255), Palettes.WebSafe.Colors);
        Assert.Equal(256, Palettes.Uniform(8, 8, 4).Count);
        Assert.Equal([Rgba8.Black, Rgba8.White], Palettes.Grayscale(2).Colors);
        Assert.Equal(new Rgba8(128, 128, 128, 255), Palettes.Grayscale(3).Colors[1]);
        Assert.Throws<ArgumentOutOfRangeException>(() => Palettes.Uniform(7, 7, 7));
        Palette p = Palette.FromColors([Rgba8.Black, Rgba8.White, Rgba8.Transparent], transparentIndex: 2);
        Assert.Equal(1, p.NearestIndex(new Rgba8(200, 200, 200, 255)));
        Assert.Equal(2, p.NearestIndex(new Rgba8(200, 200, 200, 0)));
        Assert.Throws<ArgumentException>(() => Palette.FromColors([]));
    }

    [Fact]
    public void CustomKernelValidates()
    {
        ErrorDiffusionKernel kernel = new(new[,] { { 0, 0, 1 }, { 1, 1, 0 } }, 4, 1);
        Assert.Equal(4, kernel.Divisor);
        Assert.Throws<ArgumentException>(() => new ErrorDiffusionKernel(new[,] { { 1, 0, 1 } }, 4, 1));
        Assert.Throws<ArgumentException>(() => new ErrorDiffusionKernel(new[,] { { 0, 0, 5 } }, 4, 1));
        using Raster duck = Duck();
        using Raster<Rgba8> q = duck.As<Rgba8>();
        q.Apply(o => o.Quantize(new PaletteOptions { MaxColors = 8, Kernel = kernel }), Ct);
        Assert.InRange(q.CountColors(), 2, 8);
    }

    [Fact]
    public void DominantColorsFindKnownAreas()
    {
        Rgba8 red = new(220, 30, 40, 255), blue = new(30, 60, 200, 255), green = new(40, 180, 60, 255);
        using Raster<Rgba8> image = Raster.Create<Rgba8>(500, 400, red);
        for (int y = 0; y < 400; y++)
        {
            for (int x = 250; x < 500; x++)
            {
                image.SetPixel(x, y, y < 240 ? blue : green);
            }
        }

        // A transparent band does not count.
        for (int x = 0; x < 500; x++)
        {
            image.SetPixel(x, 0, Rgba8.Transparent);
        }

        IReadOnlyList<WeightedColor> colors = image.DominantColors(3);
        Assert.Equal(3, colors.Count);
        (Rgba8 Color, double Weight)[] expected = [(red, 0.5), (blue, 0.3), (green, 0.2)];
        for (int i = 0; i < 3; i++)
        {
            OklabColor a = OklabColor.From(colors[i].Color), b = OklabColor.From(expected[i].Color);
            double de = Math.Sqrt(Math.Pow(a.L - b.L, 2) + Math.Pow(a.A - b.A, 2) + Math.Pow(a.B - b.B, 2));
            Assert.True(de <= 0.02, $"{colors[i].Color} vs {expected[i].Color}: ΔE {de:F4}");
            Assert.Equal(expected[i].Weight, colors[i].Weight, 0.01);
        }

        Assert.Equal(colors, image.DominantColors(3));
    }
}
