using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Configs;

using Egami.Formats;
using Egami.Formats.Jpeg;
using Egami.Metadata;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// The JPEG design's decode scenarios: the 4000 × 3000 benchmark photo at quality 90 as 4:2:0, 4:4:4, progressive 4:2:0 and gray
/// files from cjpeg (made by tools/make-jpeg-bench-files.py into .cache/bench-jpeg), decoded at full size, with fast upsampling,
/// to Rgba8, and at 1/4 and 1/8 scale; at 1 thread and all cores.
/// </summary>
[MemoryDiagnoser]
[GroupBenchmarksBy(BenchmarkLogicalGroupRule.ByParams)]
public class JpegDecodeBenchmarks
{
    private static readonly JpegReaderOptions Fast = new() { FastUpsampling = true };
    private static readonly ReaderOptions Quarter = new() { MinimumSize = new Size(1000, 750) };
    private static readonly ReaderOptions Eighth = new() { MinimumSize = new Size(500, 375) };

    private byte[] _file = [];
    private EgamiContext _context = EgamiContext.Default;

    [Params("420", "444", "progressive", "gray")]
    public string File { get; set; } = string.Empty;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _file = BenchFiles.Jpeg($"photo-{File}.jpg");
        _context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
    }

    [Benchmark(Baseline = true)]
    public int Decode()
    {
        using Raster raster = Raster.Load(_file, _context);
        return raster.Width;
    }

    [Benchmark]
    public int DecodeFastUpsampling()
    {
        using Raster raster = Raster.Load(_file, _context, Fast);
        return raster.Width;
    }

    [Benchmark]
    public int DecodeToRgba8()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_file, _context);
        return raster.Width;
    }

    [Benchmark]
    public int DecodeQuarter()
    {
        using Raster raster = Raster.Load(_file, _context, Quarter);
        return raster.Width;
    }

    [Benchmark]
    public int DecodeEighth()
    {
        using Raster raster = Raster.Load(_file, _context, Eighth);
        return raster.Width;
    }
}

/// <summary>Benchmark inputs made by tools outside the repo and kept in .cache.</summary>
internal static class BenchFiles
{
    internal static byte[] Jpeg(string name)
    {
        return Read("bench-jpeg", name, "tools/make-jpeg-bench-files.py");
    }

    /// <summary>Reads one of the Phase 2.2 large images.</summary>
    internal static byte[] Large(string name)
    {
        return Read("large", name, "tools/make-large-images.py");
    }

    private static byte[] Read(string folder, string name, string tool)
    {
        string? directory = AppContext.BaseDirectory;
        while (directory is not null && !System.IO.File.Exists(Path.Combine(directory, "Egami.slnx")))
        {
            directory = Path.GetDirectoryName(directory);
        }

        string path = Path.Combine(directory ?? ".", ".cache", folder, name);
        return System.IO.File.Exists(path)
            ? System.IO.File.ReadAllBytes(path)
            : throw new FileNotFoundException($"{path} is missing. Run python {tool} first.", path);
    }
}

/// <summary>The JPEG design's encode scenarios: the 4000 × 3000 benchmark photo as Rgb8, at quality 75 4:2:0 with the standard tables and at quality 90 4:4:4 with optimized tables.</summary>
[MemoryDiagnoser]
public class JpegEncodeBenchmarks
{
    private static readonly JpegWriterOptions Standard = new() { Quality = 75 };
    private static readonly JpegWriterOptions Optimized = new() { Quality = 90, Subsampling = ChromaSubsampling.Subsample444, OptimizeHuffman = true };

    private Raster<Rgb8> _photo = null!;

    [GlobalSetup]
    public void Setup()
    {
        using Raster<Rgba8> photo = BenchImages.Photo(4000, 3000);
        _photo = photo.As<Rgb8>();
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _photo.Dispose();
    }

    [Benchmark(Baseline = true)]
    public int Quality75Half()
    {
        _photo.Save(Stream.Null, JpegFormat.Info, Standard);
        return _photo.Width;
    }

    [Benchmark]
    public int Quality90FullOptimized()
    {
        _photo.Save(Stream.Null, JpegFormat.Info, Optimized);
        return _photo.Width;
    }
}

/// <summary>
/// The JPEG design's probe, EXIF and thumbnail scenarios, on the stand-in camera files from tools/make-jpeg-bench-files.py:
/// probing 50 files; parsing their EXIF blocks, reading 10 typed tags, editing one and writing the block back; and the
/// thumbnail pipeline (load at a target size, AutoOrient, Fit 256 × 256, save at quality 80) on the photo tagged with
/// orientation 6.
/// </summary>
[MemoryDiagnoser]
public class JpegScenarioBenchmarks
{
    private static readonly JpegWriterOptions Quality80 = new() { Quality = 80 };
    private static readonly ReaderOptions Thumbnail = new() { MinimumSize = new Size(256, 256) };

    private byte[][] _files = [];
    private byte[][] _exif = [];
    private byte[] _oriented = [];
    private EgamiContext _context = EgamiContext.Default;

    [Params(1, 0)]
    public int Threads { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        _files = [.. Enumerable.Range(0, CameraFiles.Count).Select(i => BenchFiles.Jpeg($"camera-{i:00}.jpg"))];
        _exif = [.. _files.Select(f => Raster.Load(f, options: new ReaderOptions { MinimumSize = new Size(1, 1) }).Metadata.Exif!.ToBytes().ToArray())];
        _oriented = BenchFiles.Jpeg("photo-orientation-6.jpg");
        _context = EgamiContext.Default with { MaxParallelism = Threads == 0 ? Environment.ProcessorCount : Threads };
    }

    [Benchmark]
    public int Probe50()
    {
        int width = 0;
        foreach (byte[] file in _files)
        {
            width += Raster.Probe(file, _context).Width;
        }

        return width;
    }

    [Benchmark]
    public int Exif50()
    {
        int length = 0;
        foreach (byte[] block in _exif)
        {
            ExifData exif = ExifData.Parse(block);
            _ = (exif.Orientation, exif.DateTaken, exif.CameraMake, exif.CameraModel, exif.Location);
            foreach (ExifKey key in (ExifKey[])[ExifKey.ExposureTime, ExifKey.FNumber, ExifKey.IsoSpeed, ExifKey.FocalLength, ExifKey.LensModel])
            {
                exif.TryGet(key, out _);
            }

            exif.Set(ExifKey.Software, "Egami");
            length += exif.ToBytes().Length;
        }

        return length;
    }

    [Benchmark]
    public int ThumbnailPipeline()
    {
        using Raster raster = Raster.Load(_oriented, _context, Thumbnail);
        raster.Apply(ops => ops.AutoOrient().Fit(256, 256));
        raster.Save(Stream.Null, JpegFormat.Info, Quality80);
        return raster.Width;
    }
}
