using System.Buffers.Binary;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// The Phase 2.9.3 JPEG XL references made by tools/make-jxl-references.py: the committed corpus in images/jxl with djxl's
/// decodes and manifest.json in reference/jxl, and, when .cache/jxl exists, the ISO/IEC 18181-3 conformance suite with its
/// reference images and djxl's and jxl-oxide's decodes. Until the decoder lands these check that the references load and agree
/// with each other; the decoder tasks compare Egami against them.
/// </summary>
public partial class JxlReferenceTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jxl");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(References, "manifest.json"))).RootElement.GetProperty("files").Clone());

    /// <summary>Gets the folder of the fetched suite and its references, or null.</summary>
    internal static string? Cache()
    {
        for (DirectoryInfo? dir = new(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            string candidate = Path.Combine(dir.FullName, ".cache", "jxl");
            if (File.Exists(Path.Combine(candidate, "reference", "conformance", "summary.json")))
            {
                return candidate;
            }
        }

        return null;
    }

    public static TheoryData<string> CorpusFiles() => [.. Files()];

    private static IEnumerable<string> Files()
    {
        return Directory.GetFiles(Corpus, "*.jxl", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(Corpus, f).Replace('\\', '/')).Order(StringComparer.Ordinal);
    }

    [Fact]
    public void ManifestListsEveryCorpusFile()
    {
        Assert.Equal(Files(), Manifest.Value.EnumerateObject().Select(p => p.Name).Order(StringComparer.Ordinal));
    }

    [Theory]
    [MemberData(nameof(CorpusFiles))]
    public void DjxlReferenceLoadsAtTheDisplaySize(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] data = File.ReadAllBytes(Path.Combine(Corpus, file));
        Assert.True(data is [0xFF, 0x0A, ..] or [0, 0, 0, 12, (byte)'J', (byte)'X', (byte)'L', (byte)' ', 13, 10, 0x87, 10, ..], "JPEG XL signature");
        if (!entry.TryGetProperty("reference", out JsonElement reference))
        {
            // Only the Squoosh file djxl 0.12.0 rejects has no djxl decode; jxl-oxide reads it.
            Assert.Equal("squoosh-egret-full-q90-progressive.jxl", file);
            Assert.False(entry.TryGetProperty("oxide_error", out _));
            return;
        }

        (int width, int height, int orientation) = Intrinsic(entry.GetProperty("jxlinfo").GetString()!);
        (int displayWidth, int displayHeight) = orientation >= 5 ? (height, width) : (width, height);
        int[] shape = [.. entry.GetProperty("shape").EnumerateArray().Select(e => e.GetInt32())];
        Assert.Equal([displayHeight, displayWidth], shape[1..3]);

        using Raster raster = Raster.Load(Path.Combine(References, reference.GetString()!));
        Assert.Equal((displayWidth, displayHeight), (raster.Width, raster.Height));
        Assert.Equal(shape[0], raster.FrameCount);

        // jxl-oxide agrees with djxl to float rounding, except where they convert color differently (gray, float lossy, and the
        // color files, which jxl-oxide writes in another space).
        string covers = entry.TryGetProperty("covers", out JsonElement c) ? c.GetString()! : string.Empty;
        double limit = covers.StartsWith("color,", StringComparison.Ordinal) ? 1 : file is "duck-gray-d1.jxl" or "duck-float-d1.jxl" ? 0.05 : 1e-3;
        Assert.InRange(entry.GetProperty("oxide_peak").GetDouble(), 0, limit);
    }

    [Fact]
    public void EveryRecompressedJpegComesBackFromDjxl()
    {
        int count = 0;
        foreach (JsonProperty file in Manifest.Value.EnumerateObject())
        {
            if (file.Value.GetProperty("source").GetString()!.EndsWith(".jpg", StringComparison.Ordinal)
                && file.Value.TryGetProperty("reconstructed_jpeg_sha256", out _))
            {
                Assert.True(file.Value.GetProperty("reconstructs_original").GetBoolean(), file.Name);
                count++;
            }
        }

        Assert.Equal(16, count);
    }

    [Fact]
    public void ExifToolReadsTheExifBoxesPlainAndBrotliCompressed()
    {
        foreach (string file in new[] { "jpeg-exif-tags.jxl", "jpeg-exif-tags-brob.jxl", "jpeg-exif-orientation-6.jxl" })
        {
            JsonElement tags = Manifest.Value.GetProperty(file).GetProperty("exiftool");
            Assert.False(tags.TryGetProperty("ExifTool:Warning", out JsonElement warning), $"{file}: {warning}");
            Assert.Contains(tags.EnumerateObject(), t => t.Name.StartsWith("IFD0:", StringComparison.Ordinal));
        }

        Assert.Contains("brob", Manifest.Value.GetProperty("jpeg-exif-tags-brob.jxl").GetProperty("jxlinfo").GetString(), StringComparison.Ordinal);
        Assert.DoesNotContain("brob", Manifest.Value.GetProperty("jpeg-exif-tags.jxl").GetProperty("jxlinfo").GetString(), StringComparison.Ordinal);
    }

    public static TheoryData<string> ConformanceCases()
    {
        string? cache = Cache();
        return cache is null
            ? ["(none)"]
            : [.. Directory.GetDirectories(Path.Combine(cache, "conformance", "testcases")).Where(d => File.Exists(Path.Combine(d, "test.json")))
                .Select(d => Path.GetFileName(d)!).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(ConformanceCases))]
    public void DjxlDecodeIsWithinTheConformanceThresholds(string name)
    {
        if (Cache() is not string cache)
        {
            return;
        }

        string folder = Path.Combine(cache, "conformance", "testcases", name);
        using JsonDocument spec = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "test.json")));
        JsonElement[] frames = [.. spec.RootElement.GetProperty("frames").EnumerateArray()];
        Npy reference = Npy.Read(Path.Combine(folder, "reference_image.npy"));
        Npy djxl = Npy.Read(Path.Combine(cache, "reference", "conformance", name, "djxl.npy"));
        Assert.Equal(frames.Length, reference.Shape[0]);
        Assert.Equal(reference.Shape[..3], djxl.Shape[..3]);

        int channels = reference.Shape[3];
        int pixels = reference.Shape[1] * reference.Shape[2];
        for (int f = 0; f < frames.Length; f++)
        {
            double peak = 0;
            double[] squares = new double[channels];
            for (int i = 0; i < pixels; i++)
            {
                for (int c = 0; c < channels; c++)
                {
                    double d = Math.Abs(reference.Data[((f * pixels) + i) * channels + c] - djxl.Data[((f * pixels) + i) * djxl.Shape[3] + c]);
                    peak = Math.Max(peak, d);
                    squares[c] += d * d;
                }
            }

            double rms = squares.Max(s => Math.Sqrt(s / pixels));
            Assert.True(peak <= frames[f].GetProperty("peak_error").GetDouble(), $"{name} frame {f}: peak {peak}");
            Assert.True(rms <= frames[f].GetProperty("rms_error").GetDouble(), $"{name} frame {f}: RMS {rms}");
        }

        if (File.Exists(Path.Combine(folder, "reconstructed.jpg")))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "reconstructed.jpg")), File.ReadAllBytes(Path.Combine(cache, "reference", "conformance", name, "djxl.jpg")));
        }
    }

    private static (int Width, int Height, int Orientation) Intrinsic(string jxlinfo)
    {
        Match size = IntrinsicPattern().Match(jxlinfo);
        Match orientation = OrientationPattern().Match(jxlinfo);
        Assert.True(size.Success && orientation.Success, jxlinfo);
        return (int.Parse(size.Groups[1].Value, CultureInfo.InvariantCulture), int.Parse(size.Groups[2].Value, CultureInfo.InvariantCulture),
            int.Parse(orientation.Groups[1].Value, CultureInfo.InvariantCulture));
    }

    [GeneratedRegex(@"Intrinsic dimensions: (\d+)x(\d+)")]
    private static partial Regex IntrinsicPattern();

    [GeneratedRegex(@"Orientation: (\d)")]
    private static partial Regex OrientationPattern();

    /// <summary>A little-endian float32 NumPy array in C order, as the conformance suite, djxl and jxl-oxide write them.</summary>
    internal sealed record Npy(int[] Shape, float[] Data)
    {
        internal static Npy Read(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            Assert.True(bytes is [0x93, (byte)'N', (byte)'U', (byte)'M', (byte)'P', (byte)'Y', ..], path);
            int length = bytes[6] == 1 ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(8)) : (int)BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(8));
            int start = bytes[6] == 1 ? 10 : 12;
            string header = Encoding.ASCII.GetString(bytes, start, length);
            Assert.Contains("'descr': '<f4'", header, StringComparison.Ordinal);
            Assert.Contains("'fortran_order': False", header, StringComparison.Ordinal);
            int open = header.IndexOf("'shape': (", StringComparison.Ordinal) + 10;
            int[] shape = [.. header[open..header.IndexOf(')', open)].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(n => int.Parse(n, CultureInfo.InvariantCulture))];
            float[] data = MemoryMarshal.Cast<byte, float>(bytes.AsSpan(start + length)).ToArray();
            Assert.Equal(shape.Aggregate(1L, (a, b) => a * b), data.Length);
            return new Npy(shape, data);
        }
    }
}
