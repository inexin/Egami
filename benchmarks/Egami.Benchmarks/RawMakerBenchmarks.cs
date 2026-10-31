using BenchmarkDotNet.Attributes;

using Egami.Formats.Extended;
using Egami.Formats.Extended.Raw;

namespace Egami.Benchmarks;

/// <summary>
/// The maker RAW codecs and the develop on real camera files, one thread: each file in the folder EGAMI_RAW_SAMPLES names
/// (raw.pixls.us CC0 samples; none ship with the repository), loaded as the mosaic (the codec alone) and developed with the
/// defaults. Without the variable there is nothing to run.
/// </summary>
[MemoryDiagnoser]
public class RawMakerBenchmarks
{
    private readonly EgamiContext _context = EgamiContext.Default.WithRaw() with { MaxParallelism = 1 };
    private byte[] _file = [];
    private RawReaderOptions _options = new();

    [ParamsSource(nameof(Files))]
    public string File { get; set; } = "";

    [Params("Mosaic", "Default")]
    public string Develop { get; set; } = "";

    public static IEnumerable<string> Files()
    {
        string? folder = Environment.GetEnvironmentVariable("EGAMI_RAW_SAMPLES");
        return folder is null || !Directory.Exists(folder) ? [] : Directory.GetFiles(folder).Select(Path.GetFileName).Order().Cast<string>();
    }

    [GlobalSetup]
    public void Setup()
    {
        _file = System.IO.File.ReadAllBytes(Path.Combine(Environment.GetEnvironmentVariable("EGAMI_RAW_SAMPLES")!, File));
        _options = Develop == "Mosaic" ? new RawReaderOptions { Output = RawOutput.Mosaic } : new RawReaderOptions();
    }

    [Benchmark]
    public int Load()
    {
        using Raster raster = Raster.Load(_file, _context, _options);
        return raster.Width;
    }
}
