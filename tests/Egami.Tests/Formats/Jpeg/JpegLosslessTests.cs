using System.IO.Compression;
using System.Runtime.InteropServices;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Tests.Formats.Jpeg;

/// <summary>Lossless JPEG beyond the corpus comparison: damage, reduced loads, and the raw entry point TIFF and DNG need.</summary>
public class JpegLosslessTests
{
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "depth");
    private static readonly string References = Path.Combine(AppContext.BaseDirectory, "reference", "jpeg", "depth");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Theory]
    [InlineData("ll-rgb-p4-restart.jpg", "ll-rgb-p4-restart.rgb8.gz")]
    [InlineData("lla-rgb.jpg", "lla-rgb.rgb8.gz")]
    [InlineData("ll16-gray-p4.jpg", "ll16-gray-p4.gray16.gz")]
    [InlineData("ll12-rgb-p5-pt3.jpg", "ll12-rgb-p5-pt3.rgb16.gz")]
    public void RawSamplesWithSharedTablesMatchTheReference(string file, string reference)
    {
        // The tables move to a JPEGTables-style stream of their own, as TIFF stores them.
        (byte[] tables, byte[] data) = SplitTables(File.ReadAllBytes(Path.Combine(Images, file)));
        ushort[] samples = JpegEmbedded.DecodeLosslessSamples(tables, data, EgamiContext.Default, out JpegDetails details, Ct);
        Assert.True(details.Lossless);
        byte[] raw = Load(reference);
        int[] expected = details.Precision > 8 ? [.. MemoryMarshal.Cast<byte, ushort>(raw).ToArray().Select(v => (int)v)] : [.. raw.Select(v => (int)v)];
        Assert.Equal(expected, samples.Select(v => (int)v));
    }

    [Fact]
    public void SharedTablesDecodeDctStreamsToo()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "images", "jpeg", "duck-420.jpg"));
        (byte[] tables, byte[] data) = SplitTables(file);
        Assert.True(tables.Length > 100);
        using Raster<Rgb8> embedded = JpegEmbedded.Decode<Rgb8>(tables, data, EgamiContext.Default, Ct);
        using Raster<Rgb8> whole = Raster.Load<Rgb8>(file);
        Assert.Equal(Pixels(whole), Pixels(embedded));
    }

    [Fact]
    public void RawSamplesNeedALosslessStream()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "p12-420.jpg"));
        Assert.Throws<UnsupportedFormatException>(() => JpegEmbedded.DecodeLosslessSamples(default, file, EgamiContext.Default, out _, Ct));
    }

    [Fact]
    public void TwoComponentStreamsDecodeRaw()
    {
        // DNG tiles often have two components. Declare a second one in a gray file's frame: the scan codes only the first, so the
        // second stays zero, and the raw samples interleave the two.
        byte[] gray = File.ReadAllBytes(Path.Combine(Images, "ll16-gray-p4.jpg"));
        ushort[] one = JpegEmbedded.DecodeLosslessSamples(default, gray, EgamiContext.Default, out _, Ct);
        byte[] two = TwoComponents(gray);
        Assert.Throws<UnsupportedFormatException>(() => Raster.Probe(two));
        ushort[] samples = JpegEmbedded.DecodeLosslessSamples(default, two, EgamiContext.Default, out JpegDetails details, Ct);
        Assert.Equal(2, details.Components.Count);
        Assert.Equal(one, samples.Where((_, i) => i % 2 == 0));
        Assert.All(samples.Where((_, i) => i % 2 == 1), v => Assert.Equal(0, v));
    }

    [Fact]
    public void DamagedFilesWarnOrThrow()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "ll-rgb-p1.jpg"));
        byte[] half = file[..(file.Length / 2)];
        using Raster lenient = Raster.Load(half);
        Assert.Single(lenient.Metadata.Warnings);
        Assert.Throws<CorruptDataException>(() => Raster.Load(half, options: new ReaderOptions { Recovery = RecoveryMode.Strict }));

        byte[] arithmetic = File.ReadAllBytes(Path.Combine(Images, "lla-rgb.jpg"));
        using Raster cut = Raster.Load(arithmetic.AsMemory(0, arithmetic.Length / 2));
        Assert.NotEmpty(cut.Metadata.Warnings);
    }

    [Fact]
    public void ReducedLoadsGoThroughTheGenericReduction()
    {
        byte[] file = File.ReadAllBytes(Path.Combine(Images, "ll16-rgb-p2.jpg"));
        using Raster<Rgb16> full = Raster.Load<Rgb16>(file);
        using Raster half = Raster.Load(file, options: new ReaderOptions { MinimumSize = new Size(32, 24) });
        Assert.Equal((32, 24), (half.Width, half.Height));
        Assert.Equal(1, Assert.IsType<JpegDetails>(half.Metadata.FormatDetails).ScaleDenominator);
        Assert.Equal(Rgb16.Layout, half.Layout);

        RasterInfo info = Raster.Probe(file);
        Assert.Equal(Rgb16.Layout, info.Layout);
        Assert.True(info.EstimatedBytes >= 2L * 64 * 48 * 6, $"{info.EstimatedBytes}");
    }

    [Fact]
    public void LosslessFilesHaveNoYuvPlanes()
    {
        using FileStream stream = File.OpenRead(Path.Combine(Images, "ll-ycc-p1.jpg"));
        NotSupportedException e = Assert.Throws<NotSupportedException>(() => YuvRaster.Load(stream));
        Assert.Contains("lossless", e.Message, StringComparison.Ordinal);
    }

    /// <summary>Moves the DQT, DHT and DAC segments of a file into a table stream (SOI, tables, EOI), leaving the rest.</summary>
    private static (byte[] Tables, byte[] Data) SplitTables(byte[] file)
    {
        List<byte> tables = [0xFF, 0xD8];
        List<byte> data = [0xFF, 0xD8];
        int at = 2;
        while (true)
        {
            byte marker = file[at + 1];
            int length = (file[at + 2] << 8) | file[at + 3];
            byte[] segment = file[at..(at + 2 + length)];
            if (marker is 0xDB or 0xC4 or 0xCC)
            {
                tables.AddRange(segment);
            }
            else if (marker == 0xDA)
            {
                data.AddRange(file[at..]);
                break;
            }
            else
            {
                data.AddRange(segment);
            }

            at += 2 + length;
        }

        tables.AddRange([0xFF, 0xD9]);
        return ([.. tables], [.. data]);
    }

    /// <summary>Returns the file with its frame header claiming two components, which only the raw entry point accepts.</summary>
    private static byte[] TwoComponents(byte[] file)
    {
        int at = 2;
        while (file[at + 1] is not (0xC3 or 0xCB))
        {
            at += 2 + ((file[at + 2] << 8) | file[at + 3]);
        }

        // Count 2, the second component a copy of the first.
        int length = (file[at + 2] << 8) | file[at + 3];
        byte[] frame = [.. file[at..(at + 2 + length)], (byte)(file[at + 10] + 1), file[at + 11], file[at + 12]];
        frame[3] += 3;
        frame[9] = 2;
        return [.. file[..at], .. frame, .. file[(at + 2 + length)..]];
    }

    private static Rgb8[] Pixels(Raster<Rgb8> raster) => [.. Enumerable.Range(0, raster.Height).SelectMany(y => raster.Row(y).ToArray())];

    private static byte[] Load(string name)
    {
        using GZipStream gzip = new(File.OpenRead(Path.Combine(References, name)), CompressionMode.Decompress);
        using MemoryStream buffer = new();
        gzip.CopyTo(buffer);
        return buffer.ToArray();
    }
}
