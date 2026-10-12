using System.Text;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// Lossless transforms against jpegtran 3.x (tools/make-jpeg-transform-references.py): every operation, edge mode, crop and gray
/// conversion gives the same decoded pixels as jpegtran's file, on every sampling, progressive, arithmetic, 12-bit and CMYK
/// sources with partial MCUs; and the metadata, limits and errors around it.
/// </summary>
public class JpegTransformTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "transform");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg", "transform");
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jpeg");

    public static TheoryData<string> Results()
    {
        return [.. Manifest().Where(e => e.TryGetProperty("result", out _)).Select(e => e.GetProperty("result").GetString()!)];
    }

    public static TheoryData<string, string> PerfectChecks()
    {
        TheoryData<string, string> data = [];
        foreach (JsonElement e in Manifest().Where(e => e.TryGetProperty("perfect", out _)))
        {
            data.Add(e.GetProperty("source").GetString()!, e.GetProperty("operation").GetString()!);
        }

        return data;
    }

    [Fact]
    public void TheCorpusHasPartialEdgesOnEveryKindOfSource()
    {
        JsonElement[] entries = Manifest();
        Assert.True(entries.Count(e => e.TryGetProperty("result", out _)) > 300);
        Assert.Contains(entries, e => e.TryGetProperty("perfect", out JsonElement p) && p.GetBoolean());
        Assert.Contains(entries, e => e.TryGetProperty("perfect", out JsonElement p) && !p.GetBoolean());
        foreach (string source in (string[])["t420.jpg", "t422.jpg", "t440.jpg", "t411.jpg", "t444.jpg", "tgray.jpg", "tprog.jpg", "tari.jpg", "tari-prog.jpg", "t12.jpg", "tcmyk.jpg"])
        {
            RasterInfo info = Raster.Probe(Path.Combine(Images, source));
            Assert.True(info.Width % 16 != 0 && info.Height % 8 != 0, source);
        }
    }

    [Theory]
    [MemberData(nameof(Results))]
    public void MatchesJpegtran(string result)
    {
        JsonElement entry = Manifest().Single(e => e.TryGetProperty("result", out JsonElement r) && r.GetString() == result);
        JpegTransformOptions options = Options(entry);
        byte[] ours = Transform(Path.Combine(Images, entry.GetProperty("source").GetString()!), options, out JpegTransformResult info);
        byte[] theirs = File.ReadAllBytes(Path.Combine(References, result));
        AssertSamePixels(theirs, ours);
        RasterInfo probe = Raster.Probe(theirs);
        Assert.Equal((probe.Width, probe.Height), (info.Width, info.Height));
        Assert.Empty(info.Warnings);
    }

    [Theory]
    [MemberData(nameof(PerfectChecks))]
    public void StrictThrowsWhenJpegtranPerfectFails(string source, string operation)
    {
        bool perfect = Manifest().Single(e => e.TryGetProperty("perfect", out _) && e.GetProperty("source").GetString() == source
            && e.GetProperty("operation").GetString() == operation).GetProperty("perfect").GetBoolean();
        JpegTransformOperation op = Enum.Parse<JpegTransformOperation>(operation);
        string path = Path.Combine(Images, source);
        if (!perfect)
        {
            Assert.Throws<InvalidOperationException>(() => Transform(path, new JpegTransformOptions { Operation = op, Edges = JpegTransformEdges.Strict }, out _));
            return;
        }

        byte[] strict = Transform(path, new JpegTransformOptions { Operation = op, Edges = JpegTransformEdges.Strict }, out _);
        byte[] trim = Transform(path, new JpegTransformOptions { Operation = op, Edges = JpegTransformEdges.Trim }, out _);
        Assert.Equal(trim, strict);
    }

    [Theory]
    [InlineData("t420.jpg")]
    [InlineData("tari-prog.jpg")]
    [InlineData("t12.jpg")]
    [InlineData("tcmyk.jpg")]
    [InlineData("tgray.jpg")]
    public void ProgressiveAndStandardTablesDecodeAsTheOptimizedBaseline(string source)
    {
        string path = Path.Combine(Images, source);
        JpegTransformOptions rotate = new() { Operation = JpegTransformOperation.Rotate270, Edges = JpegTransformEdges.Keep };
        byte[] baseline = Transform(path, rotate, out _);
        byte[] progressive = Transform(path, new JpegTransformOptions { Operation = rotate.Operation, Edges = rotate.Edges, Progressive = true }, out _);
        byte[] standard = Transform(path, new JpegTransformOptions { Operation = rotate.Operation, Edges = rotate.Edges, OptimizeHuffman = false }, out _);
        Assert.True(JpegDetails(progressive).Progressive);
        Assert.False(JpegDetails(baseline).Progressive);
        AssertSamePixels(baseline, progressive);
        AssertSamePixels(baseline, standard);
        if (source != "t12.jpg")
        {
            // 12-bit files always get their own tables.
            Assert.True(standard.Length >= baseline.Length);
        }
    }

    [Fact]
    public void CopyingLeavesThePixelsAsTheyWere()
    {
        foreach (string source in (string[])["t420.jpg", "tari.jpg", "t12.jpg", "tcmyk.jpg"])
        {
            byte[] data = File.ReadAllBytes(Path.Combine(Images, source));
            byte[] copy = Transform(Path.Combine(Images, source), JpegTransformOptions.Default, out JpegTransformResult info);
            AssertSamePixels(data, copy);
            RasterInfo probe = Raster.Probe(data);
            Assert.Equal((probe.Width, probe.Height), (info.Width, info.Height));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void AutoOrientTurnsByTheExifOrientationAndResetsIt(int orientation)
    {
        string path = Path.Combine(Corpus, $"exif-orientation-{orientation}.jpg");
        byte[] auto = Transform(path, new JpegTransformOptions { Operation = JpegTransformOperation.AutoOrient }, out JpegTransformResult info);
        JpegTransformOperation expected = (ImageOrientation)orientation switch
        {
            ImageOrientation.FlipHorizontal => JpegTransformOperation.FlipHorizontal,
            ImageOrientation.Rotate180 => JpegTransformOperation.Rotate180,
            ImageOrientation.FlipVertical => JpegTransformOperation.FlipVertical,
            ImageOrientation.Transpose => JpegTransformOperation.Transpose,
            ImageOrientation.Rotate90 => JpegTransformOperation.Rotate90,
            ImageOrientation.Transverse => JpegTransformOperation.Transverse,
            ImageOrientation.Rotate270 => JpegTransformOperation.Rotate270,
            _ => JpegTransformOperation.None,
        };
        byte[] explicitly = Transform(path, new JpegTransformOptions { Operation = expected }, out _);
        AssertSamePixels(explicitly, auto);

        RasterInfo before = Raster.Probe(path);
        RasterInfo after = Raster.Probe(auto);
        Assert.Equal(ImageOrientation.Normal, after.Orientation);
        bool turned = orientation >= 5;
        Assert.Equal(turned ? (before.Height, before.Width) : (before.Width, before.Height), (after.Width, after.Height));
        Assert.Equal((after.Width, after.Height), (info.Width, info.Height));

        // The upright file shows the same picture as the original loaded with its orientation applied.
        using Raster<Rgb8> shown = Raster.Load<Rgb8>(path);
        shown.Apply(ops => ops.AutoOrient(), TestContext.Current.CancellationToken);
        using Raster<Rgb8> upright = Raster.Load<Rgb8>(auto);
        Assert.Equal((shown.Width, shown.Height), (upright.Width, upright.Height));
        Assert.True(MeanDifference(shown, upright) < 1.0);
    }

    [Fact]
    public void MetadataIsCopiedAndExifFollowsTheResult()
    {
        byte[] source = Tagged(out byte[] icc, out byte[] xmp);
        using MemoryStream input = new(source);
        using MemoryStream output = new();
        JpegTransformResult info = JpegTransform.Apply(input, output, new JpegTransformOptions { Operation = JpegTransformOperation.AutoOrient });
        byte[] result = output.ToArray();
        Assert.Equal((32, 48), (info.Width, info.Height));
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(result);
        RasterMetadata metadata = loaded.Metadata;
        Assert.Equal(icc, metadata.IccProfile.ToArray());
        Assert.Equal(xmp, metadata.Xmp.ToArray());
        Assert.Contains(metadata.Text, t => t.Key == "Comment" && t.Value == "a comment");
        ExifData exif = metadata.Exif!;
        Assert.Equal(ImageOrientation.Normal, exif.Orientation);
        Assert.True(exif.TryGet(ExifKey.PixelXDimension, out ExifEntry? x) && x.AsInteger() == 32);
        Assert.True(exif.TryGet(ExifKey.PixelYDimension, out ExifEntry? y) && y.AsInteger() == 48);
        Assert.Equal("Egami", exif.CameraMake);

        // The 24 × 16 thumbnail was turned with the image.
        using Raster<Rgb8> thumbnail = exif.ReadThumbnail()!;
        Assert.Equal((16, 24), (thumbnail.Width, thumbnail.Height));
    }

    [Fact]
    public void CropDropsTheThumbnailAndGrayConvertsIt()
    {
        byte[] source = Tagged(out _, out _);
        byte[] cropped = Transform(source, new JpegTransformOptions { Crop = new Rect(0, 0, 16, 16) }, out _);
        Assert.True(Exif(cropped).Thumbnail.IsEmpty);

        byte[] gray = Transform(source, new JpegTransformOptions { Grayscale = true }, out JpegTransformResult info);
        Assert.Equal(Gray8.Layout, Raster.Probe(gray).Layout);
        Assert.Equal(1, Raster.Probe(Exif(gray).Thumbnail).Layout.ChannelCount);
        Assert.Empty(info.Warnings);
    }

    [Fact]
    public void WithoutCopyMetadataOnlyTheColorSegmentsStay()
    {
        byte[] source = Tagged(out _, out _);
        byte[] bare = Transform(source, new JpegTransformOptions { Operation = JpegTransformOperation.Rotate90, CopyMetadata = false }, out _);
        Assert.Equal([0xE0], Markers(bare));
        byte[] cmyk = Transform(Path.Combine(Images, "tcmyk.jpg"), new JpegTransformOptions { CopyMetadata = false }, out _);
        Assert.Equal([0xEE], Markers(cmyk));
        AssertSamePixels(File.ReadAllBytes(Path.Combine(Images, "tcmyk.jpg")), cmyk);
    }

    [Fact]
    public void ErrorsSayWhatIsWrong()
    {
        string t420 = Path.Combine(Images, "t420.jpg");
        Assert.Throws<ArgumentOutOfRangeException>(() => Transform(t420, new JpegTransformOptions { Crop = new Rect(40, 0, 10, 10) }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Transform(t420, new JpegTransformOptions { Crop = new Rect(0, 0, 0, 10) }, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => Transform(t420, new JpegTransformOptions { Operation = (JpegTransformOperation)99 }, out _));
        Assert.Throws<NotSupportedException>(() => Transform(Path.Combine(Images, "tcmyk.jpg"), new JpegTransformOptions { Grayscale = true }, out _));
        Assert.Throws<UnsupportedFormatException>(() => Transform(Path.Combine(Corpus, "depth", "ll-rgb-p1.jpg"), JpegTransformOptions.Default, out _));
        Assert.Throws<UnsupportedFormatException>(() => Transform(Path.Combine(Corpus, "depth", "hierarchical.jpg"), JpegTransformOptions.Default, out _));
        Assert.ThrowsAny<CorruptDataException>(() => Transform(Encoding.ASCII.GetBytes("not a jpeg"), JpegTransformOptions.Default, out _));
        Assert.Throws<ArgumentNullException>(() => JpegTransform.Apply(Stream.Null, Stream.Null, null!));
    }

    [Fact]
    public void TheCoefficientsCountAgainstTheMemoryLimit()
    {
        string path = Path.Combine(Corpus, "duck-420.jpg");
        EgamiContext tight = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 100_000 } };
        using FileStream input = File.OpenRead(path);
        Assert.Throws<LimitExceededException>(() => JpegTransform.Apply(input, Stream.Null, JpegTransformOptions.Default, tight));

        // The held output rows of a progressive result count too.
        EgamiContext enough = EgamiContext.Default with { Limits = SafetyLimits.Default with { MaxTotalBytes = 240_000 } };
        input.Position = 0;
        _ = JpegTransform.Apply(input, Stream.Null, JpegTransformOptions.Default, enough);
        input.Position = 0;
        Assert.Throws<LimitExceededException>(() => JpegTransform.Apply(input, Stream.Null, new JpegTransformOptions { Progressive = true }, enough));
    }

    [Fact]
    public void DamagedSourcesAreTransformedWithWarnings()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, "duck-420.jpg"));
        byte[] truncated = data[..(data.Length / 2)];
        List<EgamiDiagnostic> seen = [];
        EgamiContext context = EgamiContext.Default with { Diagnostics = seen.Add };
        using MemoryStream input = new(truncated);
        using MemoryStream output = new();
        JpegTransformResult info = JpegTransform.Apply(input, output, new JpegTransformOptions { Operation = JpegTransformOperation.Rotate90 }, context);
        Assert.Contains(info.Warnings, w => w.Id == "Jpeg.DataEnded");
        Assert.Contains(seen, w => w.Id == "Jpeg.DataEnded");
        using Raster<Rgb8> loaded = Raster.Load<Rgb8>(output.ToArray());
        Assert.Equal((240, 320), (loaded.Width, loaded.Height));
    }

    [Fact]
    public void AFileCanBeTransformedInPlace()
    {
        string path = Path.Combine(Path.GetTempPath(), $"egami-transform-{Guid.NewGuid():N}.jpg");
        try
        {
            File.Copy(Path.Combine(Images, "t420.jpg"), path);
            JpegTransformResult info = JpegTransform.Apply(path, path, new JpegTransformOptions { Operation = JpegTransformOperation.Rotate90 });
            byte[] reference = File.ReadAllBytes(Path.Combine(References, "t420.rotate90-trim.jpg"));
            RasterInfo probe = Raster.Probe(reference);
            Assert.Equal((probe.Width, probe.Height), (info.Width, info.Height));
            AssertSamePixels(reference, File.ReadAllBytes(path));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, $".{Path.GetFileName(path)}*"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void TheResultIsTheSameAtAnyParallelism()
    {
        string path = Path.Combine(Corpus, "duck-progressive.jpg");
        byte[] one = Transform(File.ReadAllBytes(path), new JpegTransformOptions { Operation = JpegTransformOperation.Transverse }, out _, EgamiContext.Default with { MaxParallelism = 1 });
        byte[] all = Transform(File.ReadAllBytes(path), new JpegTransformOptions { Operation = JpegTransformOperation.Transverse }, out _);
        Assert.Equal(one, all);
    }

    private static JpegTransformOptions Options(JsonElement entry)
    {
        Rect? crop = null;
        if (entry.GetProperty("crop") is { ValueKind: JsonValueKind.Array } c)
        {
            int[] v = [.. c.EnumerateArray().Select(n => n.GetInt32())];
            crop = new Rect(v[0], v[1], v[2], v[3]);
        }

        return new JpegTransformOptions
        {
            Operation = Enum.Parse<JpegTransformOperation>(entry.GetProperty("operation").GetString()!),
            Edges = Enum.Parse<JpegTransformEdges>(entry.GetProperty("edges").GetString()!),
            Crop = crop,
            Grayscale = entry.GetProperty("grayscale").GetBoolean(),
        };
    }

    private static byte[] Transform(string path, JpegTransformOptions options, out JpegTransformResult info)
    {
        return Transform(File.ReadAllBytes(path), options, out info);
    }

    private static byte[] Transform(byte[] data, JpegTransformOptions options, out JpegTransformResult info, EgamiContext? context = null)
    {
        using MemoryStream input = new(data);
        using MemoryStream output = new();
        info = JpegTransform.Apply(input, output, options, context);
        return output.ToArray();
    }

    /// <summary>Both files decode, through Egami's reader, to the same size and samples.</summary>
    private static void AssertSamePixels(byte[] expected, byte[] actual)
    {
        using Raster a = Raster.Load(expected);
        using Raster b = Raster.Load(actual);
        Assert.Equal((a.Width, a.Height, a.Layout), (b.Width, b.Height, b.Layout));
        using Raster<Rgba16> x = a.As<Rgba16>();
        using Raster<Rgba16> y = b.As<Rgba16>();
        for (int row = 0; row < x.Height; row++)
        {
            Assert.True(x.Row(row).SequenceEqual(y.Row(row)), $"Row {row} differs.");
        }
    }

    private static double MeanDifference(Raster<Rgb8> a, Raster<Rgb8> b)
    {
        long sum = 0;
        for (int y = 0; y < a.Height; y++)
        {
            Span<Rgb8> ra = a.Row(y);
            Span<Rgb8> rb = b.Row(y);
            for (int x = 0; x < a.Width; x++)
            {
                sum += Math.Abs(ra[x].R - rb[x].R) + Math.Abs(ra[x].G - rb[x].G) + Math.Abs(ra[x].B - rb[x].B);
            }
        }

        return sum / (3.0 * a.Width * a.Height);
    }

    private static JpegDetails JpegDetails(byte[] data)
    {
        using Raster raster = Raster.Load(data);
        return (JpegDetails)raster.Metadata.FormatDetails!;
    }

    private static ExifData Exif(byte[] data)
    {
        using Raster raster = Raster.Load(data);
        return raster.Metadata.Exif!;
    }

    /// <summary>Returns the APPn and COM markers of a file, in order.</summary>
    private static byte[] Markers(byte[] data)
    {
        List<byte> markers = [];
        int i = 2;
        while (data[i + 1] != JpegMarkerSos)
        {
            byte marker = data[i + 1];
            if (marker is >= 0xE0 and <= 0xEF or 0xFE)
            {
                markers.Add(marker);
            }

            i += 2 + ((data[i + 2] << 8) | data[i + 3]);
        }

        return [.. markers];
    }

    private const byte JpegMarkerSos = 0xDA;

    /// <summary>A 48 × 32 4:2:0 photo tagged with orientation 6, pixel dimensions, a 24 × 16 thumbnail, ICC, XMP and a comment.</summary>
    private static byte[] Tagged(out byte[] icc, out byte[] xmp)
    {
        using Raster<Rgb8> photo = Raster.Load<Rgb8>(Path.Combine(Corpus, "duck-420.jpg"));
        photo.Apply(ops => ops.Resize(48, 32));
        ExifData exif = new();
        exif.Set(ExifKey.Make, "Egami");
        exif.Orientation = ImageOrientation.Rotate90;
        exif.Set(ExifKey.PixelXDimension, (ushort)48);
        exif.Set(ExifKey.PixelYDimension, (uint)32);
        exif.SetThumbnail(photo, maxSide: 24);
        photo.Metadata.Exif = exif;
        icc = Egami.Colors.ColorProfile.DisplayP3.ToIcc().ToArray();
        photo.Metadata.IccProfile = icc;
        xmp = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"/>");
        photo.Metadata.Xmp = xmp;
        photo.Metadata.Text.Add(new TextEntry("Comment", "a comment"));
        using MemoryStream stream = new();
        photo.Save(stream, JpegFormat.Info);
        return stream.ToArray();
    }

    private static JsonElement[] Manifest()
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return [.. document.RootElement.GetProperty("entries").EnumerateArray().Select(e => e.Clone())];
    }
}
