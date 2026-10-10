using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Pixels;

/// <summary>Cmyk8 and Cmyk16 (Phase 2.1.1 task 7): the plain formula, native JPEG loading and ink-preserving operations.</summary>
public class CmykTests
{
    private static readonly string CmykJpeg = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-cmyk.jpg");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public void ThePlainFormulaConvertsBothWays()
    {
        Assert.Equal(new Rgba8(255, 255, 255, 255), new Cmyk8(0, 0, 0, 0).ToRgba8());
        Assert.Equal(new Rgba8(0, 0, 0, 255), new Cmyk8(0, 0, 0, 255).ToRgba8());
        Assert.Equal(new Rgba8(0, 255, 255, 255), new Cmyk8(255, 0, 0, 0).ToRgba8());
        Assert.Equal(new Rgba8(64, 128, 128, 255), new Cmyk8(128, 0, 0, 127).ToRgba8());
        Assert.Equal(new Cmyk8(0, 255, 255, 0), Cmyk8.FromRgba8(Rgba8.Red));
        Assert.Equal(new Cmyk8(0, 0, 0, 255), Cmyk8.FromRgba8(new Rgba8(0, 0, 0, 9)));
        Assert.Equal(new Cmyk8(0, 0, 0, 127), Cmyk8.FromRgba8(new Rgba8(128, 128, 128, 255)));
        Assert.Equal(new Cmyk16(0, 65535, 65535, 0), Cmyk16.FromRgba8(Rgba8.Red));
    }

    [Fact]
    public void EveryRouteAgreesWithinTies()
    {
        Random random = new(5);
        for (int i = 0; i < 20000; i++)
        {
            Cmyk8 ink = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256));
            Assert.Equal(ink.ToRgba8(), Rgba8.FromScaled(ink.ToScaled()));
            Rgba8 color = new((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 255);
            // An ink exactly halfway between two levels may round either way in float; integer rounding goes up.
            Cmyk8 exact = Cmyk8.FromRgba8(color), viaFloat = Cmyk8.FromScaled(color.ToScaled());
            Assert.InRange(Math.Abs(exact.C - viaFloat.C), 0, 1);
            Assert.InRange(Math.Abs(exact.M - viaFloat.M), 0, 1);
            Assert.InRange(Math.Abs(exact.Y - viaFloat.Y), 0, 1);
            Assert.Equal(exact.K, viaFloat.K);
            Assert.Equal(color, Cmyk8.FromRgba8(color).ToRgba8());
        }
    }

    [Fact]
    public void CmykToCmykKeepsEveryInk()
    {
        Cmyk8[] narrow = [new(1, 2, 3, 4), new(255, 255, 255, 0), new(0, 0, 0, 255)];
        Cmyk16[] wide = new Cmyk16[narrow.Length];
        PixelConvert.Convert<Cmyk8, Cmyk16>(narrow, wide);
        Assert.Equal(new Cmyk16(257, 514, 771, 1028), wide[0]);
        Assert.Equal(new Cmyk16(65535, 65535, 65535, 0), wide[1]);
        Cmyk8[] back = new Cmyk8[narrow.Length];
        PixelConvert.Convert<Cmyk16, Cmyk8>(wide, back);
        Assert.Equal(narrow, back);
    }

    [Fact]
    public void ACmykJpegLoadsItsInksAndTheirRgbIsUnchanged()
    {
        using Raster<Cmyk8> inks = Raster.Load<Cmyk8>(CmykJpeg);
        using Raster<Rgb8> rgb = Raster.Load<Rgb8>(CmykJpeg);
        Assert.Equal(JpegColorSpace.Cmyk, ((JpegDetails)inks.Metadata.FormatDetails!).ColorSpace);
        using Raster<Rgb8> converted = inks.As<Rgb8>();
        for (int y = 0; y < rgb.Height; y++)
        {
            Assert.True(rgb.Row(y).SequenceEqual(converted.Row(y)), $"row {y}");
        }

        using Raster<Cmyk16> wide = Raster.Load<Cmyk16>(CmykJpeg);
        Assert.Equal(Scale(inks.Row(10)[20].K), wide.Row(10)[20].K);

        static ushort Scale(byte v) => (ushort)(v * 257);
    }

    [Fact]
    public void InkRowsMatchTheRgbConversionForCmykAndYcck()
    {
        Random random = new(9);
        byte[] a = new byte[300], b = new byte[300], c = new byte[300], k = new byte[300];
        random.NextBytes(a);
        random.NextBytes(b);
        random.NextBytes(c);
        random.NextBytes(k);
        foreach (JpegColorSpace space in new[] { JpegColorSpace.Cmyk, JpegColorSpace.Ycck })
        {
            byte[] rgb = new byte[900];
            byte[] cmyk = new byte[1200];
            JpegColor.ToRgb(space, a, b, c, k, rgb);
            JpegColor.ToInk(space, a, b, c, k, cmyk);
            for (int x = 0; x < 300; x++)
            {
                Rgba8 fromInk = new Cmyk8(cmyk[4 * x], cmyk[(4 * x) + 1], cmyk[(4 * x) + 2], cmyk[(4 * x) + 3]).ToRgba8();
                Assert.Equal(new Rgba8(rgb[3 * x], rgb[(3 * x) + 1], rgb[(3 * x) + 2], 255), fromInk);
            }
        }
    }

    [Fact]
    public void FiltersWorkOnTheInksSoRichBlackStaysRich()
    {
        // Rich black made of cyan, magenta and yellow: RGB would rebuild it as pure K.
        Cmyk8 rich = new(255, 255, 255, 0);
        using Raster<Cmyk8> image = Raster.Create<Cmyk8>(16, 16, rich);
        image.Apply(ops => ops.Resize(8, 8).Blur(1).Rotate(30, Egami.Operations.ResampleFilter.Bicubic, Rgba8.White).Convolve(new Egami.Operations.ConvolutionKernel(3, 3, [0, 0, 0, 0, 1, 0, 0, 0, 0]), Egami.Operations.EdgeMode.Clamp, preserveAlpha: true), Ct);
        Cmyk8 middle = image.Row(image.Height / 2)[image.Width / 2];
        Assert.Equal(rich, middle);
        Assert.Equal(new Cmyk8(0, 0, 0, 0), image.Row(0)[0]);    // the white background, as inks
    }

    [Fact]
    public void ColorAdjustmentsGoThroughRgbAndRebuildBlack()
    {
        using Raster<Cmyk8> image = Raster.Create<Cmyk8>(4, 4, new Cmyk8(255, 255, 255, 0));
        image.Apply(ops => ops.Saturation(1.2f), Ct);
        Assert.Equal(new Cmyk8(0, 0, 0, 255), image.Row(0)[0]);
    }

    [Fact]
    public void DrawingBlendsInks()
    {
        using Raster<Cmyk8> page = Raster.Create<Cmyk8>(8, 8, new Cmyk8(255, 255, 255, 0));
        using Raster<Rgba8> clear = Raster.Create<Rgba8>(4, 4, Rgba8.Transparent);
        using Raster<Rgba8> red = Raster.Create<Rgba8>(4, 4, Rgba8.Red);
        page.Apply(ops => ops.DrawImage(clear, new Point(0, 0)).DrawImage(red, new Point(4, 4), 0.5f), Ct);
        Assert.Equal(new Cmyk8(255, 255, 255, 0), page.Row(1)[1]);   // nothing drawn: the inks are untouched
        Assert.Equal(new Cmyk8(128, 255, 255, 0), page.Row(5)[5]);   // half of red's inks (0, 255, 255, 0) over rich black
    }
}
