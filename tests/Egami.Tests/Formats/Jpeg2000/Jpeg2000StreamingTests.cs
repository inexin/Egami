using Egami.Formats;
using Egami.Formats.Jpeg2000;
using Egami.Tests.Formats.Tiff;

namespace Egami.Tests.Formats.Jpeg2000;

/// <summary>JPEG 2000 through the 2.2 row reader, reduced resolutions against OpenJPEG's, and regions as Load gives them.</summary>
public class Jpeg2000StreamingTests(ITestOutputHelper output)
{
    public static TheoryData<string> Files()
    {
        string? folder = Jpeg2000ConformanceTests.Folder();
        return folder is null
            ? ["(none)"]
            : [.. Directory.GetFiles(Path.Combine(folder, "input", "conformance"), "*.j2k").Concat(Directory.GetFiles(Path.Combine(folder, "input", "conformance"), "*.jp2"))
                .Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal)];
    }

    [Theory]
    [MemberData(nameof(Files))]
    public void RowsAndRegionsMatchLoad(string name)
    {
        string? folder = Jpeg2000ConformanceTests.Folder();
        if (folder is null)
        {
            throw LocalCorpus.Skip("jpeg2000");
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name));
        using Raster whole = Raster.Load(data);
        using (RasterDecoder decoder = RasterDecoder.Open(data))
        {
            using Raster rows = decoder.ReadAll();
            Assert.Equal(TiffReference.Bytes(whole), TiffReference.Bytes(rows));
        }

        Rect region = new(whole.Width / 3, whole.Height / 4, Math.Max(1, whole.Width / 2), Math.Max(1, whole.Height / 3));
        using Raster cropped = Raster.Load(data, options: new ReaderOptions { Region = region });
        using Raster expected = whole.Derive(ops => ops.Crop(region), TestContext.Current.CancellationToken);
        Assert.Equal(TiffReference.Bytes(expected), TiffReference.Bytes(cropped));
    }

    public static TheoryData<string, int> Reductions()
    {
        TheoryData<string, int> data = [];
        string? folder = Jpeg2000ConformanceTests.Folder();
        if (folder is null)
        {
            data.Add("(none)", 1);
            return data;
        }

        foreach (string file in Directory.GetFiles(Path.Combine(folder, "input", "conformance"), "p*.j2k").Order(StringComparer.Ordinal))
        {
            data.Add(Path.GetFileNameWithoutExtension(file), 1);
            data.Add(Path.GetFileNameWithoutExtension(file), 2);
        }

        return data;
    }

    [Theory]
    [MemberData(nameof(Reductions))]
    public void ReducedResolutionsMatchOpenJpeg(string name, int reduce)
    {
        string? folder = Jpeg2000ConformanceTests.Folder();
        if (folder is null)
        {
            throw LocalCorpus.Skip("jpeg2000");
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name + ".j2k"));
        J2kCodestream stream = J2kCodestream.Parse(data, SafetyLimits.Default);
        if (stream.MainCod.Levels < reduce)
        {
            return;
        }

        J2kImage image;
        try
        {
            image = new J2kDecoder(stream, reduce, 0, Environment.ProcessorCount, default).Decode();
        }
        catch (InvalidDataException)
        {
            // A tile with fewer levels than the main header's: OpenJPEG refuses these too.
            return;
        }

        for (int c = 0; c < image.Planes.Length; c++)
        {
            string reference = Path.Combine(folder, "..", "j2k-opj-r", $"{name}_r{reduce}_{c}.pgx");
            if (!File.Exists(reference))
            {
                reference = Path.Combine(folder, "..", "j2k-opj-r", $"{name}_r{reduce}.pgx");
                if (c > 0 || !File.Exists(reference))
                {
                    continue;
                }
            }

            (int width, int height, int[] samples) = Jpeg2000ConformanceTests.ReadPgx(reference);
            Assert.Equal((width, height), (image.Widths[c], image.Heights[c]));
            int peak = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                peak = Math.Max(peak, Math.Abs(samples[i] - image.Planes[c][i]));
            }

            output.WriteLine($"{name} reduce {reduce} component {c}: peak {peak}");
            Assert.True(peak <= (stream.CodingStyle(null, c).Reversible ? 0 : 1), $"{name} reduce {reduce} component {c} differs by {peak}.");
        }
    }

    [Fact]
    public void MinimumSizeDropsWaveletLevels()
    {
        string? folder = Jpeg2000ConformanceTests.Folder();
        if (folder is null)
        {
            throw LocalCorpus.Skip("jpeg2000");
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input", "conformance", "p0_07.j2k"));
        using Raster whole = Raster.Load(data);
        using RasterDecoder decoder = RasterDecoder.Open(data, options: new ReaderOptions { MinimumSize = new Size(whole.Width / 4, whole.Height / 4) });
        Assert.True(decoder.Info.WorkingBytes < (long)whole.Width * whole.Height * 4, $"{decoder.Info.WorkingBytes:N0} bytes held for a quarter-size decode.");
        using Raster small = decoder.ReadAll();
        Assert.Equal((whole.Width / 4, whole.Height / 4), (small.Width, small.Height));
    }

    [Theory]
    [InlineData("p0_07.j2k")]
    [InlineData("p1_05.j2k")]
    [InlineData("zoo1.jp2")]
    public void AnyThreadCountGivesTheSamePixels(string name)
    {
        string? folder = Jpeg2000ConformanceTests.Folder();
        if (folder is null)
        {
            throw LocalCorpus.Skip("jpeg2000");
        }

        byte[] data = File.ReadAllBytes(Path.Combine(folder, "input", "conformance", name));
        using Raster one = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 1 });
        using Raster all = Raster.Load(data, EgamiContext.Default with { MaxParallelism = Environment.ProcessorCount });
        using Raster three = Raster.Load(data, EgamiContext.Default with { MaxParallelism = 3 });
        Assert.Equal(TiffReference.Bytes(one), TiffReference.Bytes(all));
        Assert.Equal(TiffReference.Bytes(one), TiffReference.Bytes(three));
    }
}
