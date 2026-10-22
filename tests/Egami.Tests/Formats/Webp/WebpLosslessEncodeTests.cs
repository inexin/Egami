using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Tests.Formats.Webp;

/// <summary>The lossless encoder: exact round trips at every effort, transparent colors kept or not, near-lossless, determinism.</summary>
public class WebpLosslessEncodeTests
{
    public static TheoryData<string, int> Cases()
    {
        TheoryData<string, int> data = [];
        foreach ((string name, Raster<Rgba8> raster) in WebpTestImages.All())
        {
            raster.Dispose();
            for (int effort = 0; effort <= 6; effort++)
            {
                data.Add(name, effort);
            }
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Cases))]
    public void RoundTripsExactly(string name, int effort)
    {
        using Raster<Rgba8> raster = WebpTestImages.Get(name);
        byte[] file = Encode(raster, new WebpWriterOptions { Lossless = true, Effort = effort });
        using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
        Assert.Equal(WebpTestImages.Bytes(raster), WebpTestImages.Bytes(back));
        Assert.Empty(back.Metadata.Warnings);
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(16)]
    [InlineData(17)]
    public void PalettesRoundTripAtEveryWidth(int colors)
    {
        // 2, 4 or 8 indices share a coded pixel up to 16 colors; widths that are not a multiple leave the last one part full.
        for (int width = 1; width <= 19; width++)
        {
            using Raster<Rgba8> raster = Raster.Create<Rgba8>(width, 3);
            for (int y = 0; y < raster.Height; y++)
            {
                for (int x = 0; x < width; x++)
                {
                    int index = ((x * 7) + (y * 3)) % colors;
                    raster.Row(y)[x] = new Rgba8((byte)(index * 13), (byte)(255 - (index * 11)), (byte)(index * 5), (byte)(255 - index));
                }
            }

            byte[] file = Encode(raster, new WebpWriterOptions { Lossless = true });
            using Raster<Rgba8> back = Raster.Load<Rgba8>(file);
            Assert.Equal(WebpTestImages.Bytes(raster), WebpTestImages.Bytes(back));
        }
    }

    [Fact]
    public void OutputDoesNotDependOnTheThreadCount()
    {
        using Raster<Rgba8> photo = Raster.Load<Rgba8>(Path.Combine(AppContext.BaseDirectory, "images", "ops", "duck.png"));
        byte[] one = Save(photo, EgamiContext.Default with { MaxParallelism = 1 });
        byte[] many = Save(photo, EgamiContext.Default with { MaxParallelism = 8 });
        Assert.Equal(one, many);

        static byte[] Save(Raster<Rgba8> raster, EgamiContext context)
        {
            using Raster<Rgba8> copy = Raster.Create<Rgba8>(raster.Width, raster.Height, context);
            for (int y = 0; y < raster.Height; y++)
            {
                raster.Row(y).CopyTo(copy.Row(y));
            }

            using MemoryStream file = new();
            copy.Save(file, WebpFormat.Info, new WebpWriterOptions { Lossless = true });
            return file.ToArray();
        }
    }

    [Fact]
    public void HigherEffortIsNotLarger()
    {
        using Raster<Rgba8> duck = WebpTestImages.Get("duck");
        int low = Encode(duck, new WebpWriterOptions { Lossless = true, Effort = 0 }).Length;
        int high = Encode(duck, new WebpWriterOptions { Lossless = true, Effort = 6 }).Length;
        Assert.True(high <= low, $"effort 6 gives {high} bytes, effort 0 {low}.");
    }

    [Fact]
    public void TransparentColorsAreKeptUnlessAllowedToChange()
    {
        using Raster<Rgba8> raster = WebpTestImages.Get("mixed-alpha");
        byte[] exact = Encode(raster, new WebpWriterOptions { Lossless = true });
        using (Raster<Rgba8> back = Raster.Load<Rgba8>(exact))
        {
            Assert.Equal(WebpTestImages.Bytes(raster), WebpTestImages.Bytes(back));
        }

        byte[] loose = Encode(raster, new WebpWriterOptions { Lossless = true, KeepTransparentColors = false });
        using Raster<Rgba8> cleared = Raster.Load<Rgba8>(loose);
        for (int y = 0; y < raster.Height; y++)
        {
            for (int x = 0; x < raster.Width; x++)
            {
                Rgba8 a = raster.Row(y)[x];
                Rgba8 b = cleared.Row(y)[x];
                Assert.Equal(a.A, b.A);
                if (a.A != 0)
                {
                    Assert.Equal(a, b);
                }
            }
        }

        // Noise under a transparent half costs bits only when it must be kept.
        Random random = new(5);
        using Raster<Rgba8> hidden = WebpTestImages.Make(64, 64, (x, y) => x < 32 ? new Rgba8((byte)(x * 4), (byte)(y * 4), 80, 255) : new Rgba8((byte)random.Next(256), (byte)random.Next(256), (byte)random.Next(256), 0));
        int kept = Encode(hidden, new WebpWriterOptions { Lossless = true }).Length;
        int dropped = Encode(hidden, new WebpWriterOptions { Lossless = true, KeepTransparentColors = false }).Length;
        Assert.True(dropped < kept / 2, $"{dropped} bytes without the hidden colors, {kept} with them.");
    }

    [Fact]
    public void NearLosslessMovesValuesALittleAndShrinks()
    {
        using Raster<Rgba8> duck = WebpTestImages.Get("duck");
        byte[] exact = Encode(duck, new WebpWriterOptions { Lossless = true });
        byte[] near = Encode(duck, new WebpWriterOptions { Lossless = true, NearLossless = 40 });
        Assert.True(near.Length < exact.Length, $"near-lossless gives {near.Length} bytes, exact {exact.Length}.");
        using Raster<Rgba8> back = Raster.Load<Rgba8>(near);
        int worst = 0;
        for (int y = 0; y < duck.Height; y++)
        {
            for (int x = 0; x < duck.Width; x++)
            {
                Rgba8 a = duck.Row(y)[x];
                Rgba8 b = back.Row(y)[x];
                worst = Math.Max(worst, Math.Max(Math.Max(Math.Abs(a.R - b.R), Math.Abs(a.G - b.G)), Math.Max(Math.Abs(a.B - b.B), Math.Abs(a.A - b.A))));
            }
        }

        Assert.InRange(worst, 1, 4);
    }

    [Fact]
    public void TheSameRasterGivesTheSameBytesAtAnyThreadCount()
    {
        using Raster<Rgba8> duck = WebpTestImages.Get("duck");
        byte[] one = Encode(duck, new WebpWriterOptions { Lossless = true, Effort = 6 }, EgamiContext.Default with { MaxParallelism = 1 });
        byte[] many = Encode(duck, new WebpWriterOptions { Lossless = true, Effort = 6 }, EgamiContext.Default with { MaxParallelism = 8 });
        Assert.Equal(one, many);
    }

    [Fact]
    public void EveryPixelTypeIsWrittenAsEightBitRgba()
    {
        using Raster<Rgba8> duck = WebpTestImages.Get("duck");
        using Raster<Gray16> gray = duck.As<Gray16>();
        using Raster<Gray8> expected = duck.As<Gray8>();
        byte[] file = Encode(gray, new WebpWriterOptions { Lossless = true });
        using Raster<Gray8> back = Raster.Load<Gray8>(file);
        for (int y = 0; y < back.Height; y++)
        {
            Assert.True(expected.Row(y).SequenceEqual(back.Row(y)));
        }
    }

    internal static byte[] Encode(Raster raster, WebpWriterOptions options)
    {
        using MemoryStream output = new();
        raster.Save(output, WebpFormat.Info, options);
        return output.ToArray();
    }

    private static byte[] Encode(Raster<Rgba8> raster, WebpWriterOptions options, EgamiContext context)
    {
        using Raster<Rgba8> copy = Raster.Create<Rgba8>(raster.Width, raster.Height, context);
        copy.CopyPixelsFrom(WebpTestImages.Bytes(raster));
        return Encode(copy, options);
    }
}
