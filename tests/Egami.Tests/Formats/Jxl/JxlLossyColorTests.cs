using Egami.Colors;
using Egami.Formats;
using Egami.Formats.Jxl;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.7 task 12: lossy files name their color space (an enum for sRGB, linear, Display P3, Rec. 2020, PQ and HLG, else the
/// ICC profile), keep the raster's bit depth, keep PQ and HLG highlights with libjxl's intensity targets, and carry a gain map
/// in jhgm that loads back as it was. djxl's decodes and cjxl's sizes for the same PQ and HLG input were checked in Docker.
/// </summary>
public class JxlLossyColorTests
{
    private static readonly string GainMapImages = Path.Combine(AppContext.BaseDirectory, "images", "gainmap");

    [Theory]
    [InlineData(16, 10000)]
    [InlineData(18, 1000)]
    public void PqAndHlgKeepTheirHighlights(byte transfer, double nits)
    {
        using Raster<Rgb16> source = Raster.Create<Rgb16>(128, 64);
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                source.Row(y)[x] = new Rgb16((ushort)(x * 400), (ushort)((y * 600) + (x * 100)), 30000);
            }
        }

        source.Metadata.CodePoints = new ColorCodePoints(9, transfer, 0, true);
        using Raster<Rgb16> back = Raster.Load<Rgb16>(Save(source, new JxlWriterOptions { Lossless = false }));
        Assert.Equal(source.Metadata.CodePoints, back.Metadata.CodePoints);
        Assert.Equal(nits, back.Metadata.Hdr!.MaxContentLightLevel);
        double se = 0;
        for (int y = 0; y < source.Height; y++)
        {
            for (int x = 0; x < source.Width; x++)
            {
                double d = (back.Row(y)[x].G - source.Row(y)[x].G) / 65535.0;
                se += d * d;
            }
        }

        Assert.InRange(Math.Sqrt(se / (source.Width * source.Height)), 0, 0.01);
    }

    [Fact]
    public void DisplayP3IsNamedAndKeepsItsGamut()
    {
        using Raster<Rgb8> source = Raster.Create<Rgb8>(64, 64);
        for (int y = 0; y < source.Height; y++)
        {
            source.Row(y).Fill(new Rgb8(250, 10, 10));
        }

        source.Metadata.IccProfile = ColorProfile.DisplayP3.ToIcc();
        byte[] file = Save(source, new JxlWriterOptions { Lossless = false });
        using Raster<Rgb8> back = Raster.Load<Rgb8>(file);
        Assert.True(back.Metadata.GetColorProfile().IsEquivalentTo(ColorProfile.DisplayP3));
        Assert.True(file.Length < 1000, $"{file.Length} bytes: no ICC profile");
        Rgb8 p = back.Row(32)[32];
        Assert.InRange(p.R, 245, 255);
        Assert.InRange(p.G, 0, 16);
    }

    [Theory]
    [InlineData(PixelChannelType.Unorm8)]
    [InlineData(PixelChannelType.Unorm16)]
    [InlineData(PixelChannelType.Float32)]
    public void TheRastersPrecisionIsKept(PixelChannelType type)
    {
        using Raster<Rgb8> eight = Raster.Create<Rgb8>(32, 32);
        using Raster raster = type switch
        {
            PixelChannelType.Unorm8 => eight.As<Rgb8>(),
            PixelChannelType.Unorm16 => eight.As<Rgb16>(),
            _ => eight.As<RgbF32>(),
        };
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = false });
        Assert.Equal(type, Raster.Probe(file.ToArray()).Layout.ChannelType);
    }

    [Fact]
    public void GainMapIsKept()
    {
        using Raster jpeg = Raster.Load(Path.Combine(GainMapImages, "uhdr-default.jpg"));
        GainMap want = jpeg.Metadata.GainMap!;
        using MemoryStream file = new();
        jpeg.Save(file, JxlFormat.Info, new JxlWriterOptions { Lossless = false });
        using Raster jxl = Raster.Load(file.ToArray());
        GainMap map = Assert.IsType<GainMap>(jxl.Metadata.GainMap);
        Assert.Equal((want.Width, want.Height, want.Channels), (map.Width, map.Height, map.Channels));
        // The ISO metadata's fractions, as the JPEG writer writes them.
        GainMapInfo written = Egami.Formats.Jpeg.JpegGainMap.ParseIso(Egami.Formats.Jpeg.JpegGainMapWriter.Iso(want.Info), out _)!;
        Assert.Equal(written with { AlternateProfile = null }, map.Info with { AlternateProfile = null });
        Assert.Equal(want.Pixels, map.Pixels);
    }

    private static byte[] Save<TPixel>(Raster<TPixel> raster, JxlWriterOptions options)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        using MemoryStream file = new();
        raster.Save(file, JxlFormat.Info, options);
        return file.ToArray();
    }
}
