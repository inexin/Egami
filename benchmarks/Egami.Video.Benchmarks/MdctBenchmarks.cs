using BenchmarkDotNet.Attributes;

using Egami.Video.Audio.Codecs.Ac3;
using Egami.Video.Audio.Transforms;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The shared MDCT (V5.10, for AAC) at the AAC block lengths, with its windowed overlap-add, and the AC-3 IMDCT that shares its
/// complex FFT. One operation is one block.
/// </summary>
[MemoryDiagnoser]
public class MdctBenchmarks
{
    private readonly Mdct _long = new(2048);
    private readonly Mdct _short = new(256);
    private readonly Ac3Imdct _ac3 = new();
    private readonly float[] _samples = new float[2048];
    private readonly float[] _spectrum = new float[1024];
    private readonly float[] _block = new float[2048];
    private readonly float[] _overlap = new float[1024];
    private readonly float[] _output = new float[1024];
    private readonly float[] _window = MdctWindows.KaiserBesselDerived(2048, 4);
    private readonly float[] _ac3Coefficients = new float[256];
    private readonly float[] _ac3Delay = new float[256];
    private readonly float[] _ac3Output = new float[256];

    [GlobalSetup]
    public void Setup()
    {
        Random random = new(1);
        for (int i = 0; i < _samples.Length; i++)
        {
            _samples[i] = (float)((random.NextDouble() * 2) - 1);
        }

        _long.Forward(_samples, _spectrum);
        for (int i = 0; i < _ac3Coefficients.Length; i++)
        {
            _ac3Coefficients[i] = (float)((random.NextDouble() * 2) - 1);
        }
    }

    [Benchmark(Description = "MDCT forward, N = 2048")]
    public float Forward2048()
    {
        _long.Forward(_samples, _spectrum);
        return _spectrum[1];
    }

    [Benchmark(Description = "MDCT inverse, N = 2048")]
    public float Inverse2048()
    {
        _long.Inverse(_spectrum, _block);
        return _block[1];
    }

    [Benchmark(Description = "MDCT inverse with window and overlap-add, N = 2048")]
    public float InverseOverlap2048()
    {
        _long.InverseOverlap(_spectrum, _window, _window, _overlap, _output, _block);
        return _output[1];
    }

    [Benchmark(Description = "MDCT inverse, N = 256")]
    public float Inverse256()
    {
        _short.Inverse(_spectrum.AsSpan(0, 128), _block.AsSpan(0, 256));
        return _block[1];
    }

    [Benchmark(Description = "AC-3 IMDCT, one 512-sample block")]
    public float Ac3Long()
    {
        _ac3.Transform(_ac3Coefficients, shortBlocks: false, _ac3Delay, _ac3Output);
        return _ac3Output[1];
    }

    [Benchmark(Description = "AC-3 IMDCT, one block-switched block")]
    public float Ac3Short()
    {
        _ac3.Transform(_ac3Coefficients, shortBlocks: true, _ac3Delay, _ac3Output);
        return _ac3Output[1];
    }
}
