using System.IO.Compression;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The JPEG corpus made by tools/make-jpeg-references.py: Probe must report every file's size, layout and orientation, unsupported
/// files must be rejected, and every reference must load at the size its file implies.
/// </summary>
public class JpegCorpusTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg");

    public static TheoryData<string> Files()
    {
        return [.. Manifest().Select(e => e.GetProperty("file").GetString()!)];
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ProbeMatchesTheManifest(string file)
    {
        JsonElement entry = Entry(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        if (entry.TryGetProperty("unsupported", out JsonElement reason))
        {
            UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(data));
            Assert.Contains(reason.GetString()!, e.Message, StringComparison.Ordinal);
            return;
        }

        RasterInfo info = Raster.Probe(data);
        Assert.Equal(JpegFormat.Info, info.Format);
        Assert.Equal((Int(entry, "width"), Int(entry, "height")), (info.Width, info.Height));
        Assert.Equal(Int(entry, "channels") == 1 ? Gray8.Layout : Rgb8.Layout, info.Layout);
        Assert.Equal((ImageOrientation)Int(entry, "orientation"), info.Orientation);

        using MemoryStream stream = new(data);
        using EgamiInput input = new(stream, EgamiContext.Default, TestContext.Current.CancellationToken);
        JpegHeader header = JpegSegmentReader.ReadHeader(input, ReaderOptions.Default);
        Assert.Equal(entry.GetProperty("progressive").GetBoolean(), header.Details.IsProgressive);
        Assert.Equal(entry.GetProperty("sampling").GetString(), string.Join(',', header.Details.Components.Select(c => $"{c.HorizontalSampling}x{c.VerticalSampling}")));
        Assert.Equal(Enum.Parse<JpegColorSpace>(entry.GetProperty("colorSpace").GetString()!), header.Details.ColorSpace);
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void ReferencesLoadAtTheRightSize(string file)
    {
        JsonElement entry = Entry(file);
        if (entry.TryGetProperty("unsupported", out _))
        {
            return;
        }

        int width = Int(entry, "width");
        int height = Int(entry, "height");
        int channels = Int(entry, "channels");
        Assert.Equal(width * height * channels, Load(entry.GetProperty("reference").GetString()!).Length);
        if (entry.TryGetProperty("scaled", out JsonElement scaled))
        {
            foreach (JsonProperty s in scaled.EnumerateObject())
            {
                int scale = int.Parse(s.Name, System.Globalization.CultureInfo.InvariantCulture);
                Assert.Equal(Ceiling(width, scale) * Ceiling(height, scale) * channels, Load(s.Value.GetString()!).Length);
            }
        }

        if (entry.TryGetProperty("fast", out JsonElement fast))
        {
            Assert.Equal(width * height * channels, Load(fast.GetString()!).Length);
        }

        if (entry.TryGetProperty("upright", out JsonElement upright))
        {
            // Orientations 5 to 8 swap the size.
            Assert.Equal(width * height * channels, Load(upright.GetString()!).Length);
        }

        if (entry.TryGetProperty("exif", out JsonElement exif))
        {
            using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, exif.GetString()!)));
            Assert.True(json.RootElement.GetProperty("ifd0").EnumerateObject().Any());
        }
    }

    [Fact]
    public void TheCorpusCoversEveryMode()
    {
        JsonElement[] files = Manifest();
        Assert.True(files.Length >= 40, $"{files.Length} files");
        foreach (string sampling in new[] { "2x2,1x1,1x1", "2x1,1x1,1x1", "1x1,1x1,1x1", "1x2,1x1,1x1", "4x1,1x1,1x1", "1x1" })
        {
            Assert.Contains(files, e => e.TryGetProperty("sampling", out JsonElement s) && s.GetString() == sampling);
        }

        Assert.Contains(files, e => e.TryGetProperty("progressive", out JsonElement p) && p.GetBoolean());
        Assert.Contains(files, e => e.TryGetProperty("colorSpace", out JsonElement c) && c.GetString() == "Cmyk");
        Assert.Equal(8, files.Count(e => e.GetProperty("file").GetString()!.StartsWith("exif-orientation-", StringComparison.Ordinal)));
        Assert.Equal(2, files.Count(e => e.TryGetProperty("unsupported", out _)));
    }

    private static int Ceiling(int size, int scale) => (size + scale - 1) / scale;

    private static int Int(JsonElement entry, string name) => entry.GetProperty(name).GetInt32();

    private static byte[] Load(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name)), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static JsonElement Entry(string file)
    {
        return Manifest().Single(e => e.GetProperty("file").GetString() == file);
    }

    private static JsonElement[] Manifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return [.. json.RootElement.GetProperty("files").EnumerateArray().Select(e => e.Clone())];
    }
}
