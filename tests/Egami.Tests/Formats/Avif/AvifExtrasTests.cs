using System.Text.Json;

using Egami.Formats.Avif;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// The AVIF extras gate (tasks 1, 3 and 4): Load of every file in images/avif/extras equals avifdec 1.4.2's decode
/// (reference/avif/extras, made by tools/avifref/avifextras.py) sample for sample: sample transforms with --sato at 16 bits and
/// their fallback without it, mini files through libavif's experimental mini reader, and a spatially layered file.
/// </summary>
public class AvifExtrasTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif", "extras");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "avif", "extras");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> Files() => [.. Manifest.Value.EnumerateObject().Where(p => p.Value.TryGetProperty("reference", out _)).Select(p => p.Name)];

    public static TheoryData<string> SatoFiles() => [.. Manifest.Value.EnumerateObject().Where(p => p.Value.TryGetProperty("fallback", out _)).Select(p => p.Name)];

    [Fact]
    public void ManifestListsEveryFile()
    {
        Assert.Equal(Directory.GetFiles(Corpus, "*.avif").Select(Path.GetFileName).Order(StringComparer.Ordinal),
            Manifest.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
        Assert.Contains(Files(), f => f.Data.StartsWith("mini", StringComparison.Ordinal));
        Assert.Contains(Files(), f => f.Data.StartsWith("sato", StringComparison.Ordinal));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void LoadEqualsAvifdec(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        using Raster raster = Raster.Load(data);
        Assert.Equal(Raster.Probe(data).Layout, raster.Layout);
        AvifDetails details = (AvifDetails)raster.Metadata.FormatDetails!;
        Assert.Equal(file.StartsWith("sato", StringComparison.Ordinal), details.HasSampleTransform);
        if (details.HasSampleTransform)
        {
            Assert.Equal(16, details.BitDepth);
        }

        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        AssertEqual(raster, Path.Combine(References, entry.GetProperty("reference").GetString()!), file);
    }

    [Theory]
    [MemberData(nameof(SatoFiles))]
    public void TheFallbackIsThePrimaryImage(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        using Raster raster = Raster.Load(data, options: new AvifReaderOptions { SampleTransform = false });
        Assert.False(((AvifDetails)raster.Metadata.FormatDetails!).HasSampleTransform);
        Assert.Equal(Raster.Probe(data, options: new AvifReaderOptions { SampleTransform = false }).Layout, raster.Layout);
        AssertEqual(raster, Path.Combine(References, entry.GetProperty("fallback").GetString()!), file);
    }

    [Fact]
    public void ASampleTransformGivesSixteenBitPlanes()
    {
        using YuvRaster planes = YuvRaster.Load(Path.Combine(Corpus, "sato-12b8b-q60.avif"));
        Assert.Equal(16, planes.BitDepth);
        using YuvRaster fallback = YuvRaster.Load(Path.Combine(Corpus, "sato-12b8b-q60.avif"), options: new AvifReaderOptions { SampleTransform = false });
        Assert.Equal(12, fallback.BitDepth);
    }

    [Fact]
    public void AMiniFileKeepsItsMetadata()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "mini-icc-exif-xmp.avif"));
        Assert.Equal("AVIF", Raster.Probe(data).Format.Name);
        using Raster raster = Raster.Load(data);
        AvifDetails details = (AvifDetails)raster.Metadata.FormatDetails!;
        Assert.Equal("mif3", details.MajorBrand);
        Assert.Equal(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "icc", "compact", "DisplayP3Compat-v4.icc")), raster.Metadata.IccProfile.ToArray());
        Assert.NotNull(raster.Metadata.Exif);
        Assert.Contains("xmpmeta", System.Text.Encoding.UTF8.GetString(raster.Metadata.Xmp.Span), StringComparison.Ordinal);

        // avifenc-mini writes the CICP but leaves clli out of a mini box (its hdr_flag is 0), as avifdec --info shows.
        using Raster hdr = Raster.Load(Path.Combine(Corpus, "mini-hdr.avif"));
        Assert.Equal(16, hdr.Metadata.CodePoints!.Value.TransferCharacteristics);

        using Raster alpha = Raster.Load(Path.Combine(Corpus, "mini-alpha.avif"));
        Assert.True(((AvifDetails)alpha.Metadata.FormatDetails!).HasAlpha);
    }

    [Fact]
    public void ACutMiniBoxIsCorrupt()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "mini-420.avif"));
        for (int cut = 20; cut < 60; cut += 7)
        {
            byte[] shorter = data[..cut];
            Assert.ThrowsAny<EgamiException>(() => Raster.Load(shorter));
        }

        // A size field that disagrees with the data that follows.
        byte[] wrong = (byte[])data.Clone();
        wrong[^1000] ^= 0xFF;
        byte[] damaged = wrong[..^5];
        Assert.ThrowsAny<EgamiException>(() => Raster.Load(damaged));
    }

    private static void AssertEqual(Raster raster, string reference, string file)
    {
        using Raster<Rgba16> actual = raster.As<Rgba16>();
        using Raster<Rgba16> expected = Raster.Load<Rgba16>(reference);
        Assert.Equal((expected.Width, expected.Height), (actual.Width, actual.Height));
        for (int y = 0; y < actual.Height; y++)
        {
            ReadOnlySpan<Rgba16> a = actual.Row(y), e = expected.Row(y);
            for (int x = 0; x < actual.Width; x++)
            {
                Assert.True(a[x].Equals(e[x]), $"{file} at ({x}, {y}): Egami ({a[x].R}, {a[x].G}, {a[x].B}, {a[x].A}), avifdec ({e[x].R}, {e[x].G}, {e[x].B}, {e[x].A})");
            }
        }
    }
}
