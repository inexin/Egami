using BenchmarkDotNet.Attributes;

using Egami.Video.Analysis;
using Egami.Video.Audio;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V1.4 loudness gate (Design Q14, Build Plan task 21): the meter runs at least 100 times real time on 48 kHz stereo. One
/// operation measures 60 s of pink noise held in memory as float frames, with true peak on (the default), so real time is
/// 60 s divided by the mean.
/// </summary>
[MemoryDiagnoser]
public class LoudnessBenchmarks
{
    private readonly List<AudioFrame> _frames = [];

    [GlobalSetup]
    public void Setup()
    {
        foreach (AudioFrame frame in TestSources.Noise(TestAudioFormat.Default, MediaTime.FromSeconds(60), NoiseColor.Pink, -23))
        {
            _frames.Add(frame);
        }
    }

    [Benchmark(Description = "LoudnessMeter, 60 s of 48 kHz stereo")]
    public double Measure()
    {
        LoudnessMeter meter = new();
        foreach (AudioFrame frame in _frames)
        {
            meter.Add(frame);
        }

        meter.Complete(MediaTime.FromSeconds(60));
        return meter.Integrated;
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (AudioFrame frame in _frames)
        {
            frame.Dispose();
        }
    }
}
