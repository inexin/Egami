using System.Security.Cryptography;
using System.Text.Json;

using Egami.Formats;
using Egami.Formats.Jxl;

namespace Egami.Tests.Formats.Jxl;

/// <summary>
/// Phase 2.9.3 task 15: JxlImage.ReconstructJpeg gives back the JPEG a JPEG XL file was recompressed from, byte for byte
/// (design question 8): every recompressed corpus JPEG against its source, and the conformance cases against their
/// reconstructed.jpg. Their pixels are <see cref="JxlDecodeTests"/>'s.
/// </summary>
public class JxlJpegReconstructionTests
{
    private static readonly string Corpus = Path.Combine(AppContext.BaseDirectory, "images", "jxl");
    private static readonly string Images = Path.Combine(AppContext.BaseDirectory, "images");
    private static readonly Lazy<JsonElement> Manifest = new(() =>
        JsonDocument.Parse(File.ReadAllBytes(Path.Combine(AppContext.BaseDirectory, "reference", "jxl", "manifest.json"))).RootElement.GetProperty("files").Clone());

    public static TheoryData<string> Recompressed()
    {
        TheoryData<string> data = [];
        foreach (JsonProperty file in Manifest.Value.EnumerateObject())
        {
            if (file.Value.TryGetProperty("reconstructed_jpeg_sha256", out _))
            {
                data.Add(file.Name);
            }
        }

        return data;
    }

    public static TheoryData<string> ConformanceJpegs => ["bench_oriented_brg", "bench_oriented_brg_5", "cafe", "cafe_5", "grayscale_jpeg", "grayscale_jpeg_5"];

    private static byte[] Reconstruct(string path)
    {
        using FileStream input = File.OpenRead(path);
        using MemoryStream output = new();
        JxlImage.ReconstructJpeg(input, output);
        return output.ToArray();
    }

    [Theory]
    [MemberData(nameof(Recompressed))]
    public void CorpusJpegsComeBackByteForByte(string file)
    {
        JsonElement entry = Manifest.Value.GetProperty(file);
        byte[] jpeg = Reconstruct(Path.Combine(Corpus, file));
        Assert.Equal(entry.GetProperty("reconstructed_jpeg_sha256").GetString(), Convert.ToHexStringLower(SHA256.HashData(jpeg)));
        string source = entry.GetProperty("source").GetString()!;
        if (source.EndsWith(".jpg", StringComparison.Ordinal))
        {
            Assert.Equal(File.ReadAllBytes(Path.Combine(Images, source)), jpeg);
        }

        using Raster raster = Raster.Load(Path.Combine(Corpus, file));
        Assert.True(((JxlDetails)raster.Metadata.FormatDetails!).HasJpegReconstruction);
    }

    [Theory]
    [MemberData(nameof(ConformanceJpegs))]
    public void ConformanceJpegsComeBackByteForByte(string name)
    {
        if (JxlReferenceTests.Cache() is not string cache)
        {
            return;
        }

        string folder = Path.Combine(cache, "conformance", "testcases", name);
        Assert.Equal(File.ReadAllBytes(Path.Combine(folder, "reconstructed.jpg")), Reconstruct(Path.Combine(folder, "input.jxl")));
    }

    [Fact]
    public void FilesWithoutJbrdSaySo()
    {
        UnsupportedFormatException e = Assert.Throws<UnsupportedFormatException>(() => Reconstruct(Path.Combine(Corpus, "duck-d1.jxl")));
        Assert.Contains("jbrd", e.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ReconstructJpegWritesAFile()
    {
        string output = Path.Combine(Path.GetTempPath(), $"egami-jbrd-{Guid.NewGuid():N}.jpg");
        try
        {
            JxlImage.ReconstructJpeg(Path.Combine(Corpus, "jpeg-duck-420.jxl"), output);
            Assert.Equal(File.ReadAllBytes(Path.Combine(Images, "jpeg", "duck-420.jpg")), File.ReadAllBytes(output));
        }
        finally
        {
            File.Delete(output);
        }
    }
}
