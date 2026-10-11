using System.Text.Json;

using Egami.Formats.Jpeg;
using Egami.Metadata;

namespace Egami.Tests.Metadata;

/// <summary>
/// Phase 2.1.3 corpus (tests/images/metadata, made by tools/make-metadata-references.py) against ExifTool's reading of each file
/// (tests/reference/metadata). Every file loads, and what 1.0 already reads agrees with ExifTool; the IPTC, XMP and legacy PNG
/// checks of the later tasks build on <see cref="MetadataCorpus"/>.
/// </summary>
public class MetadataCorpusTests
{
    public static TheoryData<string> Files() => [.. MetadataCorpus.Files()];

    [Fact]
    public void EveryFileHasADump()
    {
        Assert.NotEmpty(MetadataCorpus.Files());
        Assert.All(MetadataCorpus.Files(), f => Assert.True(File.Exists(MetadataCorpus.DumpPath(f)), f));
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void FilesLoadAndAgreeWithExifTool(string file)
    {
        JsonElement dump = MetadataCorpus.Dump(file);
        using Raster raster = Raster.Load(MetadataCorpus.Bytes(file));
        Assert.Equal(dump.EnumerateObject().First(p => p.Name.EndsWith(":ImageWidth", StringComparison.Ordinal)).Value.GetInt32(), raster.Width);

        // EXIF that 1.0 reads (JPEG APP1, PNG eXIf): the IFD0 and Exif IFD values ExifTool reports as plain numbers or text.
        if (!file.Contains("legacy-", StringComparison.Ordinal) && dump.EnumerateObject().Any(p => p.Name.StartsWith("IFD0:", StringComparison.Ordinal)))
        {
            ExifData exif = Assert.IsType<ExifData>(raster.Metadata.Exif);
            foreach ((string name, ExifKey key) in (ReadOnlySpan<(string, ExifKey)>)[("IFD0:Orientation", ExifKey.Orientation), ("IFD0:Make", ExifKey.Make), ("IFD0:Model", ExifKey.Model)])
            {
                if (dump.TryGetProperty(name, out JsonElement expected))
                {
                    Assert.True(exif.TryGet(key, out ExifEntry? entry), name);
                    Assert.Equal(expected.ToString(), expected.ValueKind == JsonValueKind.Number ? entry.AsDouble(0).ToString(System.Globalization.CultureInfo.InvariantCulture) : entry.AsString());
                }
            }
        }

        // XMP that 1.0 keeps (JPEG APP1, GIF): present whenever ExifTool finds XMP.
        if (!file.EndsWith(".png", StringComparison.Ordinal) && dump.EnumerateObject().Any(p => p.Name.StartsWith("XMP-", StringComparison.Ordinal)))
        {
            Assert.False(raster.Metadata.Xmp.IsEmpty, "XMP");
        }
    }

    /// <summary>
    /// The stale-thumbnail gap the design asked task 1 to confirm or rule out: auto-orienting a phone photo (orientation 6) keeps
    /// the IFD1 thumbnail, still in the old orientation, while IFD0 now says 1. Confirmed; task 8 drops the thumbnail there.
    /// </summary>
    [Fact]
    public void AutoOrientKeepsAStaleThumbnail()
    {
        using Raster raster = Raster.Load(MetadataCorpus.Bytes("synthetic/phone-orientation6.jpg"));
        Assert.False(raster.Metadata.Exif!.Thumbnail.IsEmpty);
        raster.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        using MemoryStream saved = new();
        raster.Save(saved, JpegFormat.Info);
        using Raster again = Raster.Load(saved.ToArray());
        ExifData exif = again.Metadata.Exif!;
        Assert.Equal(ImageOrientation.Normal, exif.Orientation);
        Assert.False(exif.Thumbnail.IsEmpty);
    }
}

/// <summary>The Phase 2.1.3 metadata corpus and its ExifTool dumps.</summary>
internal static class MetadataCorpus
{
    public static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "metadata");
    public static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "metadata");

    /// <summary>Corpus files as paths relative to the corpus root, with forward slashes, in ordinal order.</summary>
    public static IEnumerable<string> Files() =>
        Directory.EnumerateFiles(Images, "*", SearchOption.AllDirectories)
            .Select(p => Path.GetRelativePath(Images, p).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);

    public static byte[] Bytes(string file) => File.ReadAllBytes(Path.Combine(Images, file));

    public static string DumpPath(string file) => Path.Combine(References, Path.GetFileName(file) + ".json");

    /// <summary>ExifTool's tags for the file: names as "Group:Tag", values as stored (exiftool -n).</summary>
    public static JsonElement Dump(string file)
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(DumpPath(file)));
        return json.RootElement[0].Clone();
    }
}
