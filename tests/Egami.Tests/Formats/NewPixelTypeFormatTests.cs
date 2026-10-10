using System.Runtime.InteropServices;

using Egami.Formats.Png;
using Egami.Pixels;

namespace Egami.Tests.Formats;

/// <summary>
/// Phase 2.1.1 task 9: PNG and PNM read and write Rgb16 and GrayAlpha16 directly, and BMP reads 5-6-5 and 4-4-4-4 bit fields
/// directly. Every corpus file must load exactly as loading the 1.0 way and converting does.
/// </summary>
public class NewPixelTypeFormatTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");

    public static TheoryData<string> PngFiles() => [.. Files("pngsuite", "*.png")];

    public static TheoryData<string> PnmFiles() => [.. Files("pnm", "*")];

    public static TheoryData<string> BmpFiles() => [.. Files("bmp", "*")];

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
