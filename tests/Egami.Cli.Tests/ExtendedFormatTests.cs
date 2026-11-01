using Egami.Formats;
using Egami.Formats.Extended;

namespace Egami.Cli.Tests;

/// <summary>
/// Egami.Formats.Extended in the tool (its design, Q10; Build Plan task 10): every stable format converts and reports as the
/// library writes and reads it, its writer options are --set keys, and --preview-formats is taken everywhere.
/// </summary>
public sealed class ExtendedFormatTests : IDisposable
{
    private const string Png = "pngsuite/basn6a08.png";

    private static readonly EgamiContext Context = EgamiContext.Default.WithExtendedFormats();

    private readonly Tool _tool = new();

    public void Dispose() => _tool.Dispose();

    public static TheoryData<string, string> Formats() => new()
    {
        { "pcx", "PCX" },
        { "sgi", "SGI" },
        { "ras", "Sun raster" },
        { "xbm", "XBM" },
        { "xpm", "XPM" },
        { "wbmp", "WBMP" },
        { "ff", "Farbfeld" },
    };

    [Theory]
    [MemberData(nameof(Formats))]
    public void ConvertsAndReports(string extension, string name)
    {
        string png = _tool.Copy(Png);
        ToolRun convert = _tool.Run("convert", png, "-o", "out." + extension);
        Assert.True(convert.Code == 0, convert.Error);
        using Raster image = Raster.Load(_tool.PathOf(png));
        using MemoryStream expected = new();
        image.Save(expected, Context.Formats.Single(f => f.Name == name));
        Assert.Equal(expected.ToArray(), File.ReadAllBytes(_tool.PathOf("out." + extension)));

        ToolRun info = _tool.Run("info", "out." + extension);
        Assert.True(info.Code == 0, info.Error);
        Assert.Contains(name, info.Text, StringComparison.Ordinal);

        // And back to PNG.
        ToolRun back = _tool.Run("convert", "out." + extension, "-o", "back.png");
        Assert.True(back.Code == 0, back.Error);
    }

    [Fact]
    public void WriterOptionsAreSetKeys()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("convert", png, "-o", "plain.sgi", "--set", "sgi.rle=false").Code);
        Assert.Equal(0, _tool.Run("convert", png, "-o", "coded.ras", "--set", "sunraster.rle=true").Code);
        Assert.False(((Formats.Extended.Sgi.SgiDetails)Raster.Load(_tool.PathOf("plain.sgi"), Context).Metadata.FormatDetails!).Rle);
        Assert.True(((Formats.Extended.SunRaster.SunRasterDetails)Raster.Load(_tool.PathOf("coded.ras"), Context).Metadata.FormatDetails!).Rle);
        ToolRun keys = _tool.Run("formats", "sgi");
        Assert.Contains("sgi.rle", keys.Text, StringComparison.Ordinal);
    }

    [Fact]
    public void RawOptionsDevelopEveryCommandsInputs()
    {
        // RAW Build Plan task 13: the --raw-* options reach the reader in convert, info and run, and bad values are usage errors.
        string dng = _tool.Copy("fuzz/raw-seeds/uncompressed-16.dng");
        ToolRun plain = _tool.Run("convert", dng, "-o", "plain.png");
        Assert.True(plain.Code == 0, plain.Error);
        ToolRun bright = _tool.Run("convert", dng, "-o", "bright.png", "--raw-exposure", "+1", "--raw-demosaic", "bilinear", "--raw-tone-curve");
        Assert.True(bright.Code == 0, bright.Error);
        using (Raster<Pixels.Rgb16> a = Raster.Load<Pixels.Rgb16>(_tool.PathOf("plain.png")), b = Raster.Load<Pixels.Rgb16>(_tool.PathOf("bright.png")))
        {
            long Sum(Raster<Pixels.Rgb16> r) => Enumerable.Range(0, r.Height).Sum(y => r.Row(y).ToArray().Sum(p => (long)p.G));
            Assert.True(Sum(b) > Sum(a), "--raw-exposure +1 brightens the develop.");
        }

        ToolRun mosaic = _tool.Run("info", dng, "--raw-output", "mosaic");
        Assert.True(mosaic.Code == 0, mosaic.Error);
        Assert.Contains("Gray", mosaic.Text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, _tool.Run("run", dng, "--raw-output", "half-size", "save", "half.png").Code);

        string cr3 = _tool.Copy("fuzz/raw-seeds/canon-synthetic.cr3");
        ToolRun preview = _tool.Run("convert", cr3, "-o", "preview.png", "--raw-output", "preview", "--raw-preview", "1");
        Assert.True(preview.Code == 0, preview.Error);
        Assert.Equal(24, Raster.Probe(_tool.PathOf("preview.png")).Width);

        ToolRun bad = _tool.Run("convert", dng, "-o", "bad.png", "--raw-demosaic", "fancy");
        Assert.NotEqual(0, bad.Code);
        Assert.Contains("--raw-demosaic takes", bad.Error, StringComparison.Ordinal);
    }

    [Fact]
    public void TakesPreviewFormats()
    {
        string png = _tool.Copy(Png);
        Assert.Equal(0, _tool.Run("convert", png, "-o", "out.pcx", "--preview-formats").Code);
        Assert.Equal(0, _tool.Run("info", "out.pcx", "--preview-formats").Code);
        Assert.Equal(0, _tool.Run("run", "out.pcx", "--preview-formats", "save", "again.xpm").Code);
        Assert.True(File.Exists(_tool.PathOf("again.xpm")));
    }
}
