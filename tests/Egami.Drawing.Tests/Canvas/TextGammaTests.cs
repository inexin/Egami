using System.Numerics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

using Egami.Fonts;
using Egami.Operations;
using Egami.Pixels;

namespace Egami.Drawing.Tests.Canvas;

/// <summary>
/// Text gamma and contrast (2.7.5 stage 1): the defaults draw exactly what Egami 1.2.0 drew, the settings change only glyph
/// coverage, and the tables reproduce the power-law blend they stand for.
/// </summary>
public class TextGammaTests
{
    private static Typeface Noto => TextTests.Noto.Value;

    private static Typeface Source => TextTests.Source.Value;

    // SHA-256 of Scenes(CanvasOptions.Default) and its linear twin as Egami 1.2.0 (v1.2.0, unchanged on main until 2.7.5) drew them.
    private const string Encoded120 = "19C18E36337861C8F50D929D2D4490A8A9FFE594D59A42B51ECEA4353619A0C3";
    private const string Linear120 = "ADE5FDF28EE203EF2678920E47299C7CC12561630F95A3796B5C05CD0649CD22";

    [Fact]
    public void DefaultsDrawAsEgami120()
    {
        Assert.Equal(Encoded120, Scenes(CanvasOptions.Default));
        Assert.Equal(Linear120, Scenes(new CanvasOptions { ColorSpace = ColorSpace.Linear }));
    }

    [Fact]
    public void ExplicitDefaultsAndGammaInLinearBlendingDrawAsBefore()
    {
        Assert.Equal(Encoded120, Scenes(new CanvasOptions { TextGamma = 1, TextContrast = 0 }));
        Assert.Equal(Linear120, Scenes(new CanvasOptions { ColorSpace = ColorSpace.Linear, TextGamma = 2.2f }));
    }

    [Theory]
    [InlineData(0.49f)]
    [InlineData(3.01f)]
    [InlineData(float.NaN)]
    [InlineData(float.PositiveInfinity)]
    public void TextGammaOutsideItsRangeThrows(float gamma) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { TextGamma = gamma });

    [Theory]
    [InlineData(-0.01f)]
    [InlineData(1.01f)]
    [InlineData(float.NaN)]
    public void TextContrastOutsideItsRangeThrows(float contrast) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CanvasOptions { TextContrast = contrast });

    [Fact]
    public void RangeEndsAreAccepted()
    {
        Assert.Equal(0.5f, new CanvasOptions { TextGamma = 0.5f }.TextGamma);
        Assert.Equal(3f, new CanvasOptions { TextGamma = 3 }.TextGamma);
        Assert.Equal(1f, new CanvasOptions { TextContrast = 1 }.TextContrast);
        Assert.Equal(1f, CanvasOptions.Default.TextGamma);
        Assert.Equal(0f, CanvasOptions.Default.TextContrast);
    }

    [Theory]
    [InlineData(0.5f)]
    [InlineData(1.4f)]
    [InlineData(2.2f)]
    [InlineData(3f)]
    public void GammaTablesMatchThePowerLaw(float gamma)
    {
        TextBlend blend = TextBlend.For(new CanvasOptions { TextGamma = gamma })!;
        for (int d = 0; d < 256; d += 3)
        {
            for (int s = 0; s < 256; s += 5)
            {
                for (int step = 0; step <= 16; step++)
                {
                    float k = step / 16f;
                    double exact = Math.Pow((k * Math.Pow(s / 255.0, gamma)) + ((1 - k) * Math.Pow(d / 255.0, gamma)), 1 / gamma) * 255;
                    int actual = blend.Mix((byte)d, (byte)s, k);
                    Assert.True(Math.Abs(actual - exact) <= 0.5 + 1e-3, $"γ {gamma}, dst {d}, src {s}, k {k}: {actual} for {exact:F3}");
                }
            }
        }
    }

    [Fact]
    public void ContrastBoostsThinCoverageAndKeepsTheEnds()
    {
        TextBlend blend = TextBlend.For(new CanvasOptions { TextContrast = 0.5f })!;
        Assert.False(blend.HasGamma);
        Assert.Equal(0, blend.Boost(0));
        Assert.Equal(255, blend.Boost(255));
        Assert.Equal(160, blend.Boost(128)); // 0.502 + 0.498 × 0.5 × 0.502 = 0.627
        for (int a = 1; a < 255; a++)
        {
            Assert.True(blend.Boost((byte)a) >= a);
        }
    }

    [Fact]
    public void GammaAboveOneMakesLightTextHeavierAndDarkTextLighter()
    {
        var gamma = new CanvasOptions { TextGamma = 1.8f };
        Assert.True(Ink(gamma, light: true) > Ink(CanvasOptions.Default, light: true));
        Assert.True(Ink(gamma, light: false) < Ink(CanvasOptions.Default, light: false));
        Assert.True(Ink(new CanvasOptions { TextContrast = 0.5f }, light: false) > Ink(CanvasOptions.Default, light: false));
    }

    [Fact]
    public void ShapesAndImagesIgnoreTheTextBlend()
    {
        var tuned = new CanvasOptions { TextGamma = 2, TextContrast = 0.7f };
        Assert.Equal(Shapes(CanvasOptions.Default), Shapes(tuned));
    }

    [Fact]
    public void PaintsTheGammaCannotDrawTakeTheContrastOnly()
    {
        // A gradient goes through the sink: the gamma does nothing to it, the contrast does.
        var gradient = new LinearGradient(Vector2.Zero, new Vector2(200, 0), new RgbaF32(0.8f, 0.1f, 0.1f, 1), new RgbaF32(0.1f, 0.2f, 0.9f, 1));
        Assert.Equal(Draw(CanvasOptions.Default, gradient), Draw(new CanvasOptions { TextGamma = 2 }, gradient));
        Assert.Equal(Draw(new CanvasOptions { TextContrast = 0.4f }, gradient), Draw(new CanvasOptions { TextGamma = 2, TextContrast = 0.4f }, gradient));
        Assert.NotEqual(Draw(CanvasOptions.Default, gradient), Draw(new CanvasOptions { TextContrast = 0.4f }, gradient));
    }

    [Fact]
    [Trait("Category", Egami.Tests.Memory.AllocationMeter.Category)]
    public void WarmGammaTextAllocatesNoMoreThanDefaultText()
    {
        Egami.Tests.Memory.AllocationMeter.SkipUnlessOnDemand();
        var font = new Font(Noto, 13) { Subpixel = false };
        foreach (Font f in new[] { font, font with { Edging = FontEdging.Lcd } })
        {
            long plain = Warm(new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal }, f);
            long gamma = Warm(new CanvasOptions { PixelGeometry = PixelGeometry.RgbHorizontal, TextGamma = 1.4f, TextContrast = 0.25f }, f);
            Assert.True(gamma <= plain, $"{f.Edging}: {gamma} bytes with gamma, {plain} without.");
        }

        static long Warm(CanvasOptions options, Font font)
        {
            using Raster<Rgba8> image = Raster.Create(400, 40, new Rgba8(0, 0x78, 0xd4, 255));
            using RasterCanvas<Rgba8> canvas = image.CreateCanvas(options);
            return Egami.Tests.Memory.AllocationMeter.Fewest(() => canvas.DrawText("The quick brown fox jumps over the lazy dog.", new Vector2(4, 26), font, new SolidPaint(Rgba8.White)));
        }
    }

    // The brightness a line of text adds: white on black when light, else the darkness black text adds on white.
    private static long Ink(CanvasOptions options, bool light)
    {
        Rgba8 back = light ? new Rgba8(0, 0, 0, 255) : Rgba8.White, ink = light ? Rgba8.White : new Rgba8(0, 0, 0, 255);
        using Raster<Rgba8> image = Raster.Create(220, 40, back);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.DrawText("Hamburgefonstiv", new Vector2(4, 26), new Font(Noto, 13), new SolidPaint(ink));
        }

        long sum = 0;
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                Rgba8 p = image.GetPixel(x, y);
                sum += light ? p.R + p.G + p.B : 765 - (p.R + p.G + p.B);
            }
        }

        return sum;
    }

    private static string Draw(CanvasOptions options, Paint paint)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using Raster<Rgba8> image = Raster.Create(220, 40, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.DrawText("Gradient text", new Vector2(4, 26), new Font(Noto, 16), paint);
        }

        Add(hash, image);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static string Shapes(CanvasOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using Raster<Rgba8> image = Raster.Create(120, 80, Rgba8.White);
        using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(options))
        {
            canvas.FillPath(Path.Star(new Vector2(40, 40), 35, 14, 5), new SolidPaint(new Rgba8(20, 60, 180, 255)));
            canvas.StrokePath(Path.Circle(new Vector2(90, 40), 20), new Stroke(1.5f), new SolidPaint(Rgba8.White));
            using Raster<Rgba8> small = Raster.Create(8, 8, new Rgba8(200, 30, 30, 160));
            canvas.DrawImage(small, new RectF(70.3f, 10.6f, 17, 13));
        }

        Add(hash, image);
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    // Grayscale, aliased, gradient and LCD text over opaque and translucent pixels, at the options given; the hash of every pixel.
    private static string Scenes(CanvasOptions options)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        CanvasOptions lcd = options with { PixelGeometry = PixelGeometry.RgbHorizontal };
        var gray = new Font(Noto, 13);
        var light = new Font(Source, 16) { Hinting = FontHinting.Light, Subpixel = false };
        var lcdFont = light with { Edging = FontEdging.Lcd };
        var gradient = new LinearGradient(Vector2.Zero, new Vector2(200, 0), new RgbaF32(0.8f, 0.1f, 0.1f, 1), new RgbaF32(0.1f, 0.2f, 0.9f, 1));

        using (Raster<Rgba8> page = Raster.Create(220, 120, Rgba8.White))
        {
            using (RasterCanvas<Rgba8> canvas = page.CreateCanvas(options))
            {
                canvas.DrawText("Grayscale text, 13 px", new Vector2(4, 18), gray, new SolidPaint(Rgba8.Black));
                canvas.DrawText("Aliased", new Vector2(4, 38), gray, new SolidPaint(new Rgba8(20, 90, 200, 255)) { Antialias = false });
                canvas.DrawText("Gradient text", new Vector2(4, 60), light, gradient);
                canvas.DrawText("Half opacity", new Vector2(4, 82), light, new SolidPaint(new Rgba8(200, 30, 30, 128)));
            }

            Add(hash, page);
        }

        foreach (Rgba8 button in new[] { new Rgba8(0, 0, 0, 255), new Rgba8(0, 0x78, 0xd4, 255) })
        {
            using Raster<Rgba8> image = Raster.Create(200, 40, button);
            using (RasterCanvas<Rgba8> canvas = image.CreateCanvas(lcd))
            {
                canvas.DrawText("Start the course", new Vector2(40, 26), lcdFont, new SolidPaint(Rgba8.White));
            }

            Add(hash, image);
        }

        // LCD text on translucent pixels takes the channels' mean coverage.
        using (Raster<Rgba8> clear = Raster.Create(200, 40, new Rgba8(255, 255, 255, 96)))
        {
            using (RasterCanvas<Rgba8> canvas = clear.CreateCanvas(lcd))
            {
                canvas.DrawText("Translucent", new Vector2(4, 26), lcdFont, new SolidPaint(Rgba8.Black));
            }

            Add(hash, clear);
        }

        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void Add(IncrementalHash hash, Raster<Rgba8> image)
    {
        var row = new Rgba8[image.Width];
        for (int y = 0; y < image.Height; y++)
        {
            for (int x = 0; x < image.Width; x++)
            {
                row[x] = image.GetPixel(x, y);
            }

            hash.AppendData(MemoryMarshal.AsBytes(row.AsSpan()));
        }
    }
}
