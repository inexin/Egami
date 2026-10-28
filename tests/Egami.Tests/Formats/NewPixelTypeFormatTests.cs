using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Bmp;
using Egami.Formats.Png;
using Egami.Formats.Tga;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Phase 2.1.1 task 9: PNG and PNM read and write Rgb16 and GrayAlpha16 directly, and BMP reads 5-6-5 and 4-4-4-4 bit fields
/// directly; step 2.13 task 2 adds BMP and TGA 5-5-5 and 1-5-5-5. Every corpus file must load exactly as loading the 1.0 way and
/// converting does.
/// </summary>
public class NewPixelTypeFormatTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    public static TheoryData<string> PngFiles() => [.. Files("pngsuite", "*.png")];

    public static TheoryData<string> PnmFiles() => [.. Files("pnm", "*")];

    public static TheoryData<string> BmpFiles() => [.. Files("bmp", "*")];

    public static TheoryData<string> TgaFiles() => [.. Files("tga", "*.tga")];

    [Theory]
    [MemberData(nameof(PngFiles))]
    public void PngLoadsSixteenBitTypesAsTheConversionDoes(string file)
    {
        AssertDirectEqualsConverted<Rgba16, Rgb16>(file);
        AssertDirectEqualsConverted<Rgba16, GrayAlpha16>(file);
    }

    [Theory]
    [MemberData(nameof(PnmFiles))]
    public void PnmLoadsSixteenBitTypesAsTheConversionDoes(string file)
    {
        AssertDirectEqualsConverted<Rgba16, Rgb16>(file);
        AssertDirectEqualsConverted<Rgba16, GrayAlpha16>(file);
    }

    [Theory]
    [MemberData(nameof(BmpFiles))]
    public void BmpLoadsPackedTypesAsTheConversionDoes(string file)
    {
        AssertDirectEqualsConverted<Bgra8, Bgr565>(file);
        AssertDirectEqualsConverted<Bgra8, Bgra4444>(file);
        AssertDirectEqualsConverted<Bgra8, Bgr555>(file);
        AssertDirectEqualsConverted<Bgra8, Bgra5551>(file);
    }

    [Theory]
    [MemberData(nameof(TgaFiles))]
    public void TgaLoadsFiveFiveFiveTypesAsTheConversionDoes(string file)
    {
        AssertDirectEqualsConverted<Bgra8, Bgr555>(file);
        AssertDirectEqualsConverted<Bgra8, Bgra5551>(file);
    }

    [Theory]
    [InlineData(BmpPixelFormat.Bgr555)]
    [InlineData(BmpPixelFormat.Bgr565)]
    [InlineData(BmpPixelFormat.Bgra5551)]
    public void BmpSixteenBitLayoutsRoundTripExactly(BmpPixelFormat layout)
    {
        BmpWriterOptions options = new() { PixelFormat = layout };
        switch (layout)
        {
            case BmpPixelFormat.Bgr555:
                RoundTrip<Bgr555>(0x7FFF, BmpFormat.Info, options);
                break;
            case BmpPixelFormat.Bgr565:
                RoundTrip<Bgr565>(0xFFFF, BmpFormat.Info, options);
                break;
            default:
                RoundTrip<Bgra5551>(0xFFFF, BmpFormat.Info, options);
                break;
        }
    }

    [Theory]
    [InlineData(TgaPixelFormat.Bgr555, false)]
    [InlineData(TgaPixelFormat.Bgra5551, false)]
    [InlineData(TgaPixelFormat.Bgr555, true)]
    [InlineData(TgaPixelFormat.Bgra5551, true)]
    public void TgaSixteenBitLayoutsRoundTripExactly(TgaPixelFormat layout, bool rle)
    {
        TgaWriterOptions options = new() { PixelFormat = layout, Rle = rle };
        if (layout == TgaPixelFormat.Bgr555)
        {
            RoundTrip<Bgr555>(0x7FFF, TgaFormat.Info, options);
        }
        else
        {
            RoundTrip<Bgra5551>(0xFFFF, TgaFormat.Info, options);
        }
    }

    /// <summary>Saves every value the type keeps, 255 pixels a row so the rows end at odd widths, and loads it back.</summary>
    private static void RoundTrip<T>(int mask, FormatInfo format, WriterOptions options)
        where T : unmanaged, IPixelType<T>
    {
        using Raster<T> raster = Raster.Create<T>(255, (mask + 255) / 255);
        int i = 0;
        for (int y = 0; y < raster.Height; y++)
        {
            foreach (ref ushort v in MemoryMarshal.Cast<T, ushort>(raster.Row(y)))
            {
                v = (ushort)(i++ & mask);
            }
        }

        using MemoryStream stream = new();
        raster.Save(stream, format, options);
        stream.Position = 0;
        using Raster<T> back = Raster.Load<T>(stream);
        AssertSame(raster, back);
        stream.Position = 0;
        using Raster<Bgra8> natural = Raster.Load<Bgra8>(stream);
        using Raster<T> converted = natural.As<T>();
        AssertSame(raster, converted);
    }

    [Theory]
    [InlineData(".png")]
    [InlineData(".ppm")]
    [InlineData(".pam")]
    public void SixteenBitTypesRoundTripExactly(string extension)
    {
        using Raster<Rgb16> rgb = Raster.Create<Rgb16>(19, 7);
        using Raster<GrayAlpha16> grayAlpha = Raster.Create<GrayAlpha16>(19, 7);
        Random random = new(8);
        for (int y = 0; y < 7; y++)
        {
            random.NextBytes(MemoryMarshal.AsBytes(rgb.Row(y)));
            random.NextBytes(MemoryMarshal.AsBytes(grayAlpha.Row(y)));
        }

        string path = Path.Combine(Path.GetTempPath(), $"egami-{Guid.NewGuid():N}{extension}");
        try
        {
            rgb.Save(path);
            using (Raster<Rgb16> back = Raster.Load<Rgb16>(path))
            {
                AssertSame(rgb, back);
            }

            if (extension == ".png")
            {
                PngDetails details = (PngDetails)Raster.Load(path).Metadata.FormatDetails!;
                Assert.Equal((PngColorType.Rgb, 16), (details.ColorType, details.BitDepth));
            }

            if (extension != ".ppm")
            {
                grayAlpha.Save(path);
                using Raster<GrayAlpha16> back = Raster.Load<GrayAlpha16>(path);
                AssertSame(grayAlpha, back);
            }
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void AssertDirectEqualsConverted<TOneZero, TNew>(string file)
        where TOneZero : unmanaged, IPixelType<TOneZero>
        where TNew : unmanaged, IPixelType<TNew>
    {
        Raster<TOneZero> before;
        try
        {
            before = Raster.Load<TOneZero>(file);
        }
        catch (EgamiException)
        {
            Assert.ThrowsAny<EgamiException>(() => Raster.Load<TNew>(file).Dispose());
            return;
        }

        using (before)
        using (Raster<TNew> converted = before.As<TNew>())
        using (Raster<TNew> direct = Raster.Load<TNew>(file))
        {
            AssertSame(converted, direct);
        }
    }

    private static void AssertSame<T>(Raster<T> expected, Raster<T> actual)
        where T : unmanaged, IPixelType<T>
    {
        Assert.Equal((expected.Width, expected.Height, expected.FrameCount), (actual.Width, actual.Height, actual.FrameCount));
        for (int f = 0; f < expected.FrameCount; f++)
        {
            for (int y = 0; y < expected.Height; y++)
            {
                Assert.True(
                    MemoryMarshal.AsBytes(expected.Frames[f].Row(y)).SequenceEqual(MemoryMarshal.AsBytes(actual.Frames[f].Row(y))),
                    $"frame {f}, row {y}");
            }
        }
    }

    private static IEnumerable<string> Files(string folder, string pattern)
    {
        return Directory.GetFiles(Path.Combine(Images, folder), pattern)
            .Where(f => !f.EndsWith(".txt", StringComparison.Ordinal) && !f.EndsWith(".md", StringComparison.Ordinal))
            .Order(StringComparer.Ordinal);
    }
}
