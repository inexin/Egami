using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Metadata;

namespace Egami.Tests.Formats.Heic;

/// <summary>
/// Phase 2.9.4 task 2 without the Egami.Heic package: core Egami detects HEIC, Probe reads the container as heif-info does (size
/// after clap and irot, depth, monochrome, alpha), and Load names the package it needs (design question 10).
/// </summary>
public partial class HeicProbeTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "heic");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "heic", "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>
    /// The HEVC-coded corpus files heif-info reads, with an hvc1 primary image or a grid or overlay of them: the ones Probe must
    /// agree on. The JPEG-coded and uncompressed files are core HEIF (task 13), not HEIC.
    /// </summary>
    public static TheoryData<string> ProbedFiles() => [.. Probed()];

    internal static IEnumerable<string> Probed() => Manifest.Value.EnumerateObject()
        .Where(f => f.Value.GetProperty("heif_info_ok").GetBoolean() && f.Value.TryGetProperty("items", out JsonElement items)
            && items.EnumerateArray().Any(i => i.GetProperty("type").GetString() == "hvc1"))
        .Select(f => f.Name).Where(f => !f.StartsWith("broken-", StringComparison.Ordinal)).Order(StringComparer.Ordinal);

    [Theory]
    [MemberData(nameof(ProbedFiles))]
    public void ProbeAgreesWithHeifInfo(string file)
    {
        RasterInfo info = Raster.Probe(Path.Combine(Corpus, file));
        Assert.Equal("HEIC", info.Format.Name);
        Check(info, Manifest.Value.GetProperty(file).GetProperty("heif_info").GetString()!);
    }

    [Theory]
    [MemberData(nameof(ProbedFiles))]
    public void LoadNamesTheEgamiHeicPackage(string file)
    {
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load(Path.Combine(Corpus, file)));
        Assert.Contains("Egami.Heic", e.Message, StringComparison.Ordinal);
        Assert.Contains("WithHeic()", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void AvifIsStillAvif()
    {
        Assert.Equal("AVIF", Raster.Probe(Path.Combine(AppContext.BaseDirectory, "images", "avif", "duck-420-q60.avif")).Format.Name);
    }

    [Theory]
    [InlineData("heic", true)]
    [InlineData("heix", true)]
    [InlineData("hevc", true)]
    [InlineData("avif", false)]
    [InlineData("mif1", false)]
    public void DetectsTheHevcBrands(string brand, bool heic)
    {
        byte[] header = [0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p', .. "mif1"u8, 0, 0, 0, 0, .. System.Text.Encoding.ASCII.GetBytes(brand)];
        Assert.Equal(heic, Egami.Formats.Heic.HeicSupport.Matches(header));
    }

    /// <summary>Checks a probe against heif-info's description of the primary image.</summary>
    internal static void Check(RasterInfo info, string heifInfo)
    {
        Match image = PrimaryImage().Match(heifInfo);
        Assert.True(image.Success, heifInfo);
        (int width, int height) = (int.Parse(image.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(image.Groups[2].Value, CultureInfo.InvariantCulture));

        // heif-info gives the size after clap and irot; Probe gives it after clap, with irot and imir as the orientation.
        bool turned = info.Orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;
        Assert.Equal((width, height), turned ? (info.Height, info.Width) : (info.Width, info.Height));
        int depth = int.Parse(BitDepth().Match(heifInfo).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(depth > 8 ? 16 : 8, info.Layout.BitsPerChannel);
        bool alpha = info.Layout.Alpha != Egami.Pixels.AlphaKind.None;
        Assert.Equal(heifInfo.Contains("alpha channel: yes", StringComparison.Ordinal), alpha);
        Assert.Equal(heifInfo.Contains("colorspace: monochrome", StringComparison.Ordinal), info.Layout.ChannelCount - (alpha ? 1 : 0) == 1);
    }

    [GeneratedRegex(@"image: (\d+)x(\d+) \(id=\d+\), primary")]
    private static partial Regex PrimaryImage();

    [GeneratedRegex(@"bit depth: (\d+)")]
    private static partial Regex BitDepth();
}
