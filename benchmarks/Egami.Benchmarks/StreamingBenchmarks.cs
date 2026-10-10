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

    public static IEnumerable<string> Scenarios => ["png:Photo-RGBA8", "png:Screenshot-RGB8", "png:Palette8", "png:Adam7-RGBA8"];

    [GlobalSetup]
    public void Setup()
    {
        _file = BenchImages.Png(Scenario[4..]);
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

    [Benchmark]
    public int RegionQuarter()
    {
        RasterInfo info = Raster.Probe(_file);
        ReaderOptions options = new() { Region = new Rect(info.Width / 4, info.Height / 4, info.Width / 4, info.Height / 4) };
        using Raster<Rgba8> raster = Raster.Load<Rgba8>(_file, OneThread, options);
        return raster.Width;
    }
}
