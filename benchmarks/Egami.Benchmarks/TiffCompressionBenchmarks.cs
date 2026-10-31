using BenchmarkDotNet.Attributes;

using Egami.Formats.Tiff;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// TIFF's lossless compressions side by side (TIFF ZSTD task 4): a 2048 × 1536 photo-like RGB image and a screenshot-like one,
/// written and read with LZW, Deflate (level 5) and ZSTD (levels 3, 9 and 19), each with the horizontal predictor, in strips.
/// </summary>
[MemoryDiagnoser]
public class TiffCompressionBenchmarks
{
    private readonly Dictionary<string, byte[]> _files = [];
    private Raster<Rgb8> _photo = null!;
    private Raster<Rgb8> _screenshot = null!;

    [Params("Photo", "Screenshot")]
    public string Image { get; set; } = "Photo";

    [Params("Lzw", "Deflate", "Zstd3", "Zstd9", "Zstd19")]
    public string Codec { get; set; } = "Deflate";

    [GlobalSetup]
    public void Setup()
    {
        using Raster<Rgba8> photo = BenchImages.Photo(2048, 1536);
        _photo = photo.As<Rgb8>();
        _screenshot = BenchImages.Screenshot(2048, 1536);
        using MemoryStream file = new();
        Source.Save(file, TiffFormat.Info, Options);
        _files[Key] = file.ToArray();
        Console.WriteLine($"// {Image} {Codec}: {file.Length} bytes");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
        _screenshot.Dispose();
    }

    private string Key => Image + Codec;

    private Raster<Rgb8> Source => Image == "Photo" ? _photo : _screenshot;

    private TiffWriterOptions Options => Codec switch
    {
        "Lzw" => new TiffWriterOptions { Compression = TiffCompression.Lzw },
        "Deflate" => new TiffWriterOptions { Compression = TiffCompression.Deflate },
        _ => new TiffWriterOptions { Compression = TiffCompression.Zstd, ZstdLevel = int.Parse(Codec[4..], System.Globalization.CultureInfo.InvariantCulture) },
    };

    [Benchmark]
    public long Encode()
    {
        using MemoryStream file = new();
        Source.Save(file, TiffFormat.Info, Options);
        return file.Length;
    }

    [Benchmark]
    public int Decode()
    {
        using Raster<Rgb8> raster = Raster.Load<Rgb8>(_files[Key]);
        return raster.Width;
    }
}
