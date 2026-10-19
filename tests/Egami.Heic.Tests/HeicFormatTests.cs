using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Metadata;

namespace Egami.Heic.Tests;

/// <summary>
/// Phase 2.9.4 task 2 with the package: WithHeic replaces core Egami's HEIC entry, Probe reads every HEVC-coded corpus file as
/// heif-info does, and nothing changes for a context without it (design question 10).
/// </summary>
public partial class HeicFormatTests
{
    private static readonly EgamiContext Context = new EgamiContext().WithHeic();
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "heic");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "heic", "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> ProbedFiles() => [.. Manifest.Value.EnumerateObject()
        .Where(f => f.Value.GetProperty("heif_info_ok").GetBoolean() && f.Value.TryGetProperty("items", out JsonElement items)
            && items.EnumerateArray().Any(i => i.GetProperty("type").GetString() == "hvc1"))
        .Select(f => f.Name).Where(f => !f.StartsWith("broken-", StringComparison.Ordinal)).Order(StringComparer.Ordinal)];

    [Fact]
    public void WithHeicReplacesTheCoreEntryInPlace()
    {
        EgamiContext core = new();
        int index = core.Formats.ToList().FindIndex(f => f.Name == "HEIC");
        Assert.True(index >= 0);
        Assert.NotSame(HeicFormat.Info, core.Formats[index]);
        Assert.Same(HeicFormat.Info, Context.Formats[index]);
        Assert.Equal(core.Formats.Count, Context.Formats.Count);
        Assert.Throws<ArgumentNullException>(() => HeicContextExtensions.WithHeic(null!));
    }

    [Theory]
    [MemberData(nameof(ProbedFiles))]
    public void ProbeAgreesWithHeifInfo(string file)
    {
        RasterInfo info = Raster.Probe(Path.Combine(Corpus, file), Context);
        Assert.Same(HeicFormat.Info, info.Format);
        string heifInfo = Manifest.Value.GetProperty(file).GetProperty("heif_info").GetString()!;
        Match image = PrimaryImage().Match(heifInfo);
        (int width, int height) = (int.Parse(image.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(image.Groups[2].Value, CultureInfo.InvariantCulture));
        bool turned = info.Orientation is ImageOrientation.Transpose or ImageOrientation.Rotate90 or ImageOrientation.Transverse or ImageOrientation.Rotate270;
        Assert.Equal((width, height), turned ? (info.Height, info.Width) : (info.Width, info.Height));
        int depth = int.Parse(BitDepth().Match(heifInfo).Groups[1].Value, CultureInfo.InvariantCulture);
        Assert.Equal(depth > 8 ? 16 : 8, info.Layout.BitsPerChannel);
        Assert.Equal(heifInfo.Contains("alpha channel: yes", StringComparison.Ordinal), info.Layout.Alpha != Egami.Pixels.AlphaKind.None);
    }

    [Fact]
    public void LoadNeedsThePackage()
    {
        string path = Path.Combine(Corpus, "hm-main.heic");
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Load(path, new EgamiContext()));
        Assert.Contains("Egami.Heic", e.Message, StringComparison.Ordinal);
        using Raster raster = Raster.Load(path, Context);
        Assert.Equal((320, 240), (raster.Width, raster.Height));
        Assert.Equal("HEIC", raster.Metadata.FormatName);
    }

    [GeneratedRegex(@"image: (\d+)x(\d+) \(id=\d+\), primary")]
    private static partial Regex PrimaryImage();

    [GeneratedRegex(@"bit depth: (\d+)")]
    private static partial Regex BitDepth();
}
