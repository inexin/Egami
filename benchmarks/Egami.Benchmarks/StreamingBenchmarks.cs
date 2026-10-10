using BenchmarkDotNet.Attributes;

using Egami.Formats;
using Egami.Pixels;

namespace Egami.Benchmarks;

/// <summary>
/// Phase 2.2: decoding through RasterDecoder against 1.0 Load, for the speed gate (ReadRows within 2% of Load at one thread).
/// Each format task adds its files here as it starts streaming.
/// </summary>
[MemoryDiagnoser]
public class StreamingBenchmarks
{
    private static readonly EgamiContext OneThread = EgamiContext.Default with { MaxParallelism = 1 };
    private byte[] _file = [];

    [ParamsSource(nameof(Scenarios))]
    public string Scenario { get; set; } = string.Empty;

    public static IEnumerable<string> Scenarios =>
    [
        "png:Photo-RGBA8", "png:Screenshot-RGB8", "png:Palette8", "png:Adam7-RGBA8",
        "jpeg:photo-420.jpg", "jpeg:photo-progressive.jpg", "large:jpeg-8000.jpg", "large:jpeg-8000-restart.jpg",
        .. SimpleFiles.DecodeScenarios.Select(s => "simple:" + s),
    ];

    [GlobalSetup]
    public void Setup()
    {
        string[] parts = Scenario.Split(':');
        _file = parts[0] switch
        {
            "png" => BenchImages.Png(parts[1]),
            "jpeg" => BenchFiles.Jpeg(parts[1]),
            "simple" => SimpleFiles.File(parts[1]),
            _ => BenchFiles.Large(parts[1]),
        };
    }

    [Benchmark(Baseline = true)]
    public int Load()
    {
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_file, OneThread);
        return raster.Width;
    }

    [Benchmark]
    public int ReadRows()
    {
        using RasterDecoder decoder = RasterDecoder.Open(_file, OneThread);
        using Raster<Rgba8> band = Raster.Create<Rgba8>(decoder.Info.Width, 64);
        int rows = 0;
        int n;
        while ((n = decoder.ReadRows(band)) > 0)
        {
            rows += n;
        }

        return rows;
    }

    /// <summary>A centred region of 1/16 the area: the design's region gate asks for at least 2x on the 8,000 x 8,000 baseline JPEG.</summary>
    [Benchmark]
    public int RegionCentre()
    {
        RasterInfo info = Raster.Probe(_file);
        ReaderOptions options = new() { Region = new Rect(info.Width * 3 / 8, info.Height * 3 / 8, info.Width / 4, info.Height / 4) };
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_file, OneThread, options);
        return raster.Width;
    }
}
