using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>
/// The Phase 2.4.2 corpus made by tools/make-jpeg-depth-references.py: arithmetic-coded files decode within 1 level of djpeg,
/// 12-bit files within 1 level at 12 bits, and lossless files to exactly the samples djpeg (or, for arithmetic lossless, Thomas
/// Richter's libjpeg) gives.
/// </summary>
public class JpegDepthTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "depth");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg", "depth");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public static TheoryData<string> Supported()
    {
        return [.. Manifest().Where(e => !e.TryGetProperty("unsupported", out _) && Decodable(e)).Select(e => e.GetProperty("file").GetString()!)];
    }

    /// <summary>Returns whether this step of 2.4.2 decodes the file yet.</summary>
    private static bool Decodable(JsonElement e) => Int(e, "precision") == 8 && !Flag(e, "lossless");

    public static TheoryData<string, int> WithScaledReferences()
    {
        TheoryData<string, int> data = [];
        foreach (JsonElement e in Manifest().Where(e => e.TryGetProperty("scaled", out _) && Decodable(e)))
        {
            foreach (JsonProperty scale in e.GetProperty("scaled").EnumerateObject())
            {
                data.Add(e.GetProperty("file").GetString()!, int.Parse(scale.Name, System.Globalization.CultureInfo.InvariantCulture));
            }
        }

        return data;
    }

    public static TheoryData<string> WithFastReference()
    {
        return [.. Manifest().Where(e => e.TryGetProperty("fast", out _) && Decodable(e)).Select(e => e.GetProperty("file").GetString()!)];
    }

    [Fact]
    public void TheCorpusCoversEveryMode()
    {
        JsonElement[] files = Manifest();
        Assert.Contains(files, e => Flag(e, "arithmetic") && Flag(e, "progressive"));
        Assert.Contains(files, e => Flag(e, "arithmetic") && !Flag(e, "progressive") && !Flag(e, "lossless"));
        Assert.Contains(files, e => Flag(e, "arithmetic") && Flag(e, "lossless"));
        Assert.Contains(files, e => Int(e, "precision") == 12 && Flag(e, "progressive"));
        Assert.Contains(files, e => Int(e, "precision") == 12 && Flag(e, "arithmetic"));
        foreach (int precision in (int[])[4, 8, 10, 12, 14, 16])
        {
            Assert.Contains(files, e => Int(e, "precision") == precision && Flag(e, "lossless"));
        }

        for (int predictor = 1; predictor <= 7; predictor++)
        {
            Assert.Contains(files, e => e.TryGetProperty("predictor", out JsonElement p) && p.GetInt32() == predictor);
        }

        Assert.Single(files, e => e.TryGetProperty("unsupported", out _));
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public void ProbeMatchesTheManifest(string file)
    {
        JsonElement entry = Entry(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        RasterInfo info = Raster.Probe(data);
        Assert.Equal(JpegFormat.Info, info.Format);
        Assert.Equal((Int(entry, "width"), Int(entry, "height")), (info.Width, info.Height));
        Assert.Equal(NaturalLayout(entry), info.Layout);

        using MemoryStream stream = new(data);
        EgamiInput input = new(stream, EgamiContext.Default, Ct);
        JpegDetails details = JpegSegmentReader.ReadHeader(input, ReaderOptions.Default).Details;
        Assert.Equal(Int(entry, "precision"), details.Precision);
        Assert.Equal(Flag(entry, "arithmetic"), details.Arithmetic);
        Assert.Equal(Flag(entry, "lossless"), details.Lossless);
        Assert.Equal(Flag(entry, "progressive"), details.Progressive);
        Assert.Equal(entry.GetProperty("sampling").GetString(), string.Join(',', details.Components.Select(c => $"{c.HorizontalSampling}x{c.VerticalSampling}")));
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public void MatchesTheReference(string file)
    {
        JsonElement entry = Entry(file);
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, file)));
        Assert.Equal(NaturalLayout(entry), raster.Layout);
        Assert.Empty(raster.Metadata.Warnings);
        JpegDetails details = Assert.IsType<JpegDetails>(raster.Metadata.FormatDetails);
        Assert.Equal(Int(entry, "precision"), details.Precision);
        AssertSamples(entry, entry.GetProperty("reference").GetString()!, raster, file);
    }

    [Theory]
    [MemberData(nameof(WithScaledReferences))]
    public void ScaledDecodeMatchesDjpegScale(string file, int scale)
    {
        JsonElement entry = Entry(file);
        int width = (Int(entry, "width") + scale - 1) / scale;
        int height = (Int(entry, "height") + scale - 1) / scale;
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, file)), options: new ReaderOptions { MinimumSize = new Size(width, height) });
        Assert.Equal((width, height), (raster.Width, raster.Height));
        Assert.Equal(scale, Assert.IsType<JpegDetails>(raster.Metadata.FormatDetails).ScaleDenominator);
        AssertSamples(entry, entry.GetProperty("scaled").GetProperty(scale.ToString(System.Globalization.CultureInfo.InvariantCulture)).GetString()!, raster, $"{file} 1/{scale}");
    }

    [Theory]
    [MemberData(nameof(WithFastReference))]
    public void FastUpsamplingMatchesDjpegNoSmooth(string file)
    {
        JsonElement entry = Entry(file);
        using Raster raster = Raster.Load(File.ReadAllBytes(Path.Combine(Images, file)), options: new JpegReaderOptions { FastUpsampling = true });
        AssertSamples(entry, entry.GetProperty("fast").GetString()!, raster, file + " fast");
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public void StreamedRowsMatchTheLoad(string file)
    {
        // A region sends the load through the row source; its rows must be the full decode's.
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        using Raster full = Raster.Load(data);
        Rect region = new(full.Width / 4, full.Height / 3, Math.Max(1, full.Width / 2), Math.Max(1, full.Height / 2));
        using Raster part = Raster.Load(data, options: new ReaderOptions { Region = region });
        Assert.Equal(full.Layout, part.Layout);
        using Raster cropped = full.Derive(ops => ops.Crop(region), Ct);
        Assert.Equal(Bytes(cropped), Bytes(part));
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public void EightBitLoadsRoundTheNaturalDecode(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        using Raster natural = Raster.Load(data);
        using Raster<Rgba8> rgba = Raster.Load<Rgba8>(data);
        using Raster<Rgba8> expected = natural.As<Rgba8>();
        Assert.Equal(Bytes(expected), Bytes(rgba));
    }

    [Theory]
    [MemberData(nameof(Supported))]
    public void EveryParallelismDecodesTheSame(string file)
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, file));
        using Raster serial = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 1 });
        using Raster parallel = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 8 });
        Assert.Equal(Bytes(serial), Bytes(parallel));
    }

    [Fact]
    public void HierarchicalFilesAreRejected()
    {
        byte[] data = File.ReadAllBytes(Path.Combine(Images, "hierarchical.jpg"));
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(data));
        Assert.Contains("Hierarchical", e.Message, StringComparison.Ordinal);
    }

    /// <summary>Compares the raster's samples with a reference at the file's own precision.</summary>
    private static void AssertSamples(JsonElement entry, string reference, Raster raster, string label)
    {
        int precision = Int(entry, "precision");
        int max = (1 << precision) - 1;
        byte[] raw = Load(reference);
        int[] expected = precision > 8
            ? [.. MemoryMarshal.Cast<byte, ushort>(raw).ToArray().Select(v => (int)v)]
            : [.. raw.Select(v => (int)v)];

        // The raster holds the samples scaled to its full range; take them back to the file's precision, which is exact.
        byte[] bytes = Bytes(raster);
        int[] actual = raster is Raster<Rgb16> or Raster<Gray16>
            ? [.. MemoryMarshal.Cast<byte, ushort>(bytes).ToArray().Select(v => (int)(((long)v * max + 32767) / 65535))]
            : [.. bytes.Select(v => (v * max + 127) / 255)];
        Assert.Equal(expected.Length, actual.Length);
        int tolerance = Flag(entry, "lossless") ? 0 : 1;
        int worst = 0;
        for (int i = 0; i < expected.Length; i++)
        {
            worst = Math.Max(worst, Math.Abs(expected[i] - actual[i]));
        }

        Assert.True(worst <= tolerance, $"{label}: off by up to {worst} levels of {max}");
    }

    private static PixelLayoutInfo NaturalLayout(JsonElement entry)
    {
        bool gray = Int(entry, "channels") == 1;
        return Int(entry, "precision") > 8 ? (gray ? Gray16.Layout : Rgb16.Layout) : gray ? Gray8.Layout : Rgb8.Layout;
    }

    private static byte[] Bytes(Raster raster)
    {
        return raster switch
        {
            Raster<Rgb8> r => Pack(r),
            Raster<Gray8> r => Pack(r),
            Raster<Rgb16> r => Pack(r),
            Raster<Gray16> r => Pack(r),
            Raster<Cmyk16> r => Pack(r),
            Raster<Rgba8> r => Pack(r),
            _ => throw new InvalidOperationException(raster.GetType().Name),
        };
    }

    private static byte[] Pack<T>(Raster<T> raster)
        where T : unmanaged, IPixelType<T>
    {
        int rowBytes = raster.Width * raster.Layout.BytesPerPixel;
        byte[] bytes = new byte[rowBytes * raster.Height];
        for (int y = 0; y < raster.Height; y++)
        {
            MemoryMarshal.AsBytes(raster.Row(y)).CopyTo(bytes.AsSpan(y * rowBytes, rowBytes));
        }

        return bytes;
    }

    private static bool Flag(JsonElement entry, string name) => entry.TryGetProperty(name, out JsonElement value) && value.GetBoolean();

    private static int Int(JsonElement entry, string name) => entry.TryGetProperty(name, out JsonElement value) ? value.GetInt32() : 0;

    private static byte[] Load(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name)), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }

    private static JsonElement Entry(string file) => Manifest().Single(e => e.GetProperty("file").GetString() == file);

    private static JsonElement[] Manifest()
    {
        using JsonDocument json = JsonDocument.Parse(File.ReadAllText(Path.Combine(References, "manifest.json")));
        return [.. json.RootElement.GetProperty("files").EnumerateArray().Select(e => e.Clone())];
    }
}
