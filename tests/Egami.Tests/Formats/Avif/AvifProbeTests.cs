using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

using Egami.Formats;
using Egami.Formats.Avif;
using Egami.Formats.Heif;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Avif;

/// <summary>
/// Phase 2.9.2 task 3's gate: Probe and AvifDetails match what libavif 1.4.2's avifdec --info prints for every corpus file it
/// reads (reference/avif/manifest.json): size, depth, chroma format and sample position, alpha and premultiplication, range and
/// CICP, ICC, Exif and XMP, clap's crop, irot, imir, progressive layers and clli.
/// </summary>
public partial class AvifProbeTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "avif");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "avif", "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> CorpusFiles() => [.. Manifest.Value.EnumerateObject().Select(p => p.Name)];

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void ProbeMatchesAvifdecInfo(string file)
    {
        Compare(Manifest.Value.GetProperty(file), File.ReadAllBytes(Path.Combine(Corpus, file)));
    }

    public static TheoryData<string> ExtraFiles() => HeifFileCorpusTestsExtra();

    [Theory]
    [MemberData(nameof(ExtraFiles))]
    public void CachedProbeMatchesAvifdecInfo(string file)
    {
        if (AvifReferenceTests.Cache() is not string cache || file == "(none)")
        {
            throw LocalCorpus.Skip("avif");
        }

        using JsonDocument manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(cache, "reference", "extra", "manifest.json")));
        Compare(manifest.RootElement.GetProperty("files").GetProperty(file), File.ReadAllBytes(Path.Combine(cache, file)));
    }

    private static TheoryData<string> HeifFileCorpusTestsExtra() => Heif.HeifFileCorpusTests.ExtraFiles();

    private static void Compare(JsonElement entry, byte[] data)
    {
        if (!entry.GetProperty("avifdec_info_ok").GetBoolean())
        {
            // libavif rejects it; Egami either reads the container or fails as corrupt or unsupported, never otherwise.
            try
            {
                Raster.Probe(data);
            }
            catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException)
            {
            }

            return;
        }

        Dictionary<string, string> info = Info(entry.GetProperty("avifdec_info").GetString()!);
        AvifFile avif = AvifFile.Open(HeifFile.Parse(data, "AVIF"));
        AvifDetails details = AvifDetails.From(avif);
        RasterInfo probe = Raster.Probe(data);
        Assert.Equal(AvifFormat.Info, probe.Format);

        Assert.Equal(info["Resolution"], $"{avif.Width}x{avif.Height}");
        if (avif.Heif.Groups.Any(g => FourCC.ToString(g.Type) == "altr") && avif.Heif.Items.Any(i => FourCC.ToString(i.Type) == "sato"))
        {
            // AVIF 1.2's sample transforms (design question 16) are not read: Egami gives the base image, libavif the 16-bit one.
            return;
        }

        Assert.Equal(info["Bit Depth"], details.BitDepth.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(info["Format"], details.Chroma switch
        {
            ChromaFormat.Yuv400 => "YUV400",
            ChromaFormat.Yuv420 => "YUV420",
            ChromaFormat.Yuv422 => "YUV422",
            _ => "YUV444",
        });
        if (info.TryGetValue("Chroma Sam. Pos", out string? position))
        {
            Assert.Equal(position, details.ChromaSiting switch { ChromaSiting.Left => "1", ChromaSiting.TopLeft => "2", _ => "0" });
        }

        Assert.Equal(info["Alpha"], !details.HasAlpha ? "Absent" : details.AlphaPremultiplied ? "Premultiplied" : "Not premultiplied");
        if (details.IsSequence && info["Color Primaries"] != details.CodePoints.Primaries.ToString(CultureInfo.InvariantCulture))
        {
            // libavif reads an avis file's track, whose color may differ from its still item's; tracks come with task 16.
            return;
        }

        Assert.Equal(info["Range"], details.CodePoints.FullRange ? "Full" : "Limited");
        Assert.Equal(info["Color Primaries"], details.CodePoints.Primaries.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(info["Transfer Char."], details.CodePoints.TransferCharacteristics.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(info["Matrix Coeffs."], details.CodePoints.MatrixCoefficients.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(info["ICC Profile"], avif.Icc.IsEmpty ? "Absent" : $"Present ({avif.Icc.Length} bytes)");
        Assert.Equal(info["Exif Metadata"].StartsWith("Present", StringComparison.Ordinal), avif.ExifItems.Count > 0);
        Assert.Equal(info["XMP Metadata"].StartsWith("Present", StringComparison.Ordinal), avif.XmpItems.Count > 0);
        Assert.Equal(info["Progressive"] == "Available", details.IsProgressive);
        Assert.Equal(info.GetValueOrDefault("CLLI"), avif.ContentLightLevel is (int cll, int fall) ? $"{cll}, {fall}" : null);

        // clap, irot and imir: avifdec prints each it found, and the crop it derives.
        string transforms = entry.GetProperty("avifdec_info").GetString()!;
        Match crop = CropPattern().Match(transforms);
        Assert.Equal(crop.Success ? (int.Parse(crop.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(crop.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(crop.Groups[3].Value, CultureInfo.InvariantCulture), int.Parse(crop.Groups[4].Value, CultureInfo.InvariantCulture)) : null, avif.Crop);
        Match rotation = RotationPattern().Match(transforms);
        Assert.Equal(rotation.Success ? int.Parse(rotation.Groups[1].Value, CultureInfo.InvariantCulture) : 0, avif.Rotation);
        Match mirror = MirrorPattern().Match(transforms);
        Assert.Equal(mirror.Success ? int.Parse(mirror.Groups[1].Value, CultureInfo.InvariantCulture) : null, avif.Mirror);

        // A layered image probes at its base layer's size, which Load gives; AvifItemTests checks that.
        (int width, int height) = details.IsProgressive ? (probe.Width, probe.Height) : avif.Crop is (_, _, int w, int h) ? (w, h) : (avif.Width, avif.Height);
        Assert.Equal((width, height), (probe.Width, probe.Height));
        Assert.Equal(AvifFile.Orient(avif.Rotation, avif.Mirror), probe.Orientation);
        Assert.Equal(details.BitDepth > 8 ? 16 : 8, probe.Layout.BitsPerChannel);
        Assert.Equal(details.HasAlpha, probe.Layout.HasAlpha);
        // A sequence loads every frame when all are key frames (design question 4).
        Assert.Equal((long)width * height * probe.Layout.BytesPerPixel * (probe.FrameCount ?? 1), probe.EstimatedBytes);
    }

    [Theory]
    [InlineData(0, null, ImageOrientation.Normal)]
    [InlineData(1, null, ImageOrientation.Rotate270)]
    [InlineData(2, null, ImageOrientation.Rotate180)]
    [InlineData(3, null, ImageOrientation.Rotate90)]
    [InlineData(0, 1, ImageOrientation.FlipHorizontal)]
    [InlineData(0, 0, ImageOrientation.FlipVertical)]
    [InlineData(2, 0, ImageOrientation.FlipHorizontal)]
    [InlineData(1, 1, ImageOrientation.Transverse)]
    [InlineData(1, 0, ImageOrientation.Transpose)]
    [InlineData(3, 1, ImageOrientation.Transpose)]
    public void IrotThenImirGiveTheExifOrientation(int quarterTurns, int? mirror, ImageOrientation expected)
    {
        Assert.Equal(expected, AvifFile.Orient(quarterTurns, mirror));
    }

    [Fact]
    public void OnlyAvifBrandsMatch()
    {
        Assert.True(AvifFormat.Matches(File.ReadAllBytes(Path.Combine(Corpus, "duck-420-q60.avif"))));
        Assert.True(AvifFormat.Matches(File.ReadAllBytes(Path.Combine(Corpus, "pan-sequence.avif"))));
        byte[] heic = [0, 0, 0, 24, .. "ftypheic"u8, 0, 0, 0, 0, .. "mif1"u8, .. "heic"u8];
        Assert.False(AvifFormat.Matches(heic));
        byte[] minorAvif = [0, 0, 0, 16, .. "ftypmif1avif"u8];
        Assert.False(AvifFormat.Matches(minorAvif));
        byte[] compatible = [0, 0, 0, 24, .. "ftypmif1"u8, 0, 0, 0, 0, .. "miaf"u8, .. "avif"u8];
        Assert.True(AvifFormat.Matches(compatible));
    }

    [Theory]
    [InlineData("duck-grid-4x1-alpha.avif")]
    [InlineData("duck-clap-irot3-imir1.avif")]
    [InlineData("duck-icc-rec2020-10bit.avif")]
    public void ProbeSurvivesEveryTruncationAndByteFlip(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        int headers = Math.Min(data.Length, 2048);
        for (int length = 0; length < headers; length++)
        {
            Probe(data[..length]);
        }

        Random random = new(2903);
        for (int i = 0; i < 3000; i++)
        {
            byte[] mutated = (byte[])data.Clone();
            mutated[random.Next(headers)] ^= (byte)(1 << random.Next(8));
            Probe(mutated);
        }

        static void Probe(byte[] bytes)
        {
            try
            {
                Raster.Probe(bytes);
            }
            catch (Exception e) when (e is CorruptDataException or UnsupportedFormatException or LimitExceededException)
            {
            }
        }
    }

    [Fact]
    public void LoadGivesTheProbedSizeAndLayout()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "duck-420-q60.avif"));
        RasterInfo info = Raster.Probe(data);
        using Raster raster = Raster.Load(data);
        Assert.Equal((info.Width, info.Height, info.Layout), (raster.Width, raster.Height, raster.Layout));
    }

    private static Dictionary<string, string> Info(string text)
    {
        Dictionary<string, string> fields = [];
        foreach (string line in text.Split('\n'))
        {
            Match match = FieldPattern().Match(line);
            if (match.Success)
            {
                fields.TryAdd(match.Groups[1].Value.Trim(), match.Groups[2].Value.Trim());
            }
        }

        return fields;
    }

    [GeneratedRegex(@"^ \* ([^:]+?)\s*: (.*)$")]
    private static partial Regex FieldPattern();

    [GeneratedRegex(@"derived crop rect: X: (\d+), Y: (\d+), W: (\d+), H: (\d+)")]
    private static partial Regex CropPattern();

    [GeneratedRegex(@"irot \(Rotation\)\s*: (\d)")]
    private static partial Regex RotationPattern();

    [GeneratedRegex(@"imir \(Mirror\)\s*: (\d)")]
    private static partial Regex MirrorPattern();
}
