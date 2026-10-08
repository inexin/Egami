using Egami.Formats;
using Egami.Pixels;

namespace Egami.Tests.Formats.Png;

/// <summary>
/// PngSuite conformance: every valid file decodes to the same pixels as an independent reference decode,
/// and every intentionally broken file (named x*) fails in strict mode.
/// </summary>
public class PngSuiteTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "pngsuite");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "pngsuite");
    private static readonly ReaderOptions Strict = new() { Recovery = RecoveryMode.Strict };

    public static TheoryData<string> ValidFiles()
    {
        return [.. Files().Where(f => !f.StartsWith('x'))];
    }

    public static TheoryData<string> BrokenFiles()
    {
        return [.. Files().Where(f => f.StartsWith('x'))];
    }

    [Fact]
    public void SuiteIsPresent()
    {
        Assert.Equal(162, ValidFiles().Count);
        Assert.Equal(14, BrokenFiles().Count);
    }

    /// <remarks>
    /// The references hold 8 bits per sample. For 16-bit files they hold the high byte, so the 16-bit decode is compared
    /// shifted right by 8; exact 16-bit values are covered by the generated-image tests.
    /// </remarks>
    [Theory]
    [MemberData(nameof(ValidFiles))]
    public void ValidFileMatchesReference(string name)
    {
        string path = Path.Combine(Images, name);
        byte[] expected = File.ReadAllBytes(Path.Combine(References, Path.ChangeExtension(name, ".rgba8")));
        bool sixteenBit = File.ReadAllBytes(path)[24] == 16;

        using Raster<Rgba16> wide = Raster.Load<Rgba16>(path, options: Strict);
        Rgba16[] pixels = new Rgba16[wide.Width * wide.Height];
        wide.CopyPixelsTo(pixels);
        byte[] actual = new byte[pixels.Length * 4];
        for (int i = 0; i < pixels.Length; i++)
        {
            Rgba16 p = pixels[i];
            actual[i * 4] = HighOrRound(p.R);
            actual[(i * 4) + 1] = HighOrRound(p.G);
            actual[(i * 4) + 2] = HighOrRound(p.B);
            actual[(i * 4) + 3] = HighOrRound(p.A);
        }

        Assert.Equal(expected, actual);

        // The natural pixel type and Load<Rgba8> must agree with each other.
        using Raster natural = Raster.Load(path, options: Strict);
        using Raster<Rgba8> converted = natural.As<Rgba8>();
        using Raster<Rgba8> direct = Raster.Load<Rgba8>(path, options: Strict);
        for (int y = 0; y < direct.Height; y++)
        {
            Assert.Equal(direct.Row(y).ToArray(), converted.Row(y).ToArray());
        }

        Assert.Empty(natural.Metadata.Warnings);

        byte HighOrRound(ushort value)
        {
            return sixteenBit ? (byte)(value >> 8) : (byte)(value / 257);
        }
    }

    [Theory]
    [MemberData(nameof(BrokenFiles))]
    public void BrokenFileFailsInStrictMode(string name)
    {
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(Path.Combine(Images, name), options: Strict));
    }

    private static IEnumerable<string> Files()
    {
        return Directory.GetFiles(Images, "*.png").Select(Path.GetFileName).Order(StringComparer.Ordinal)!;
    }
}
