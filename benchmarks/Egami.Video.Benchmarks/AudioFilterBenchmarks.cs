using BenchmarkDotNet.Attributes;

using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Benchmarks;

/// <summary>
/// The V9.1 speed gates (Design Q15): a gain chain at least 200 times real time per core and tempo at least 20 times, on 48 kHz
/// stereo. One operation filters 60 s of pink noise held in memory as float frames, so real time is 60 s divided by the mean.
/// Stage B adds the EQ to the gain chain.
/// </summary>
[MemoryDiagnoser]
public class AudioFilterBenchmarks
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

    [Benchmark(Description = "Gain, fade, pan, gain envelope, 60 s of 48 kHz stereo")]
    public long GainChain()
    {
        using AudioFilterChain chain = new AudioFilterChain(48000, ChannelLayout.Stereo)
            .Add(new Gain(-3))
            .Add(new Egami.Video.Audio.Filters.Fade(FadeDirection.In, MediaTime.Zero, MediaTime.FromSeconds(2), FadeCurve.EqualPower))
            .Add(new Pan { Balance = 0.2 })
            .Add(new Gain { Envelope = new GainEnvelope(new Keyframe(MediaTime.Zero, 0), new Keyframe(MediaTime.FromSeconds(60), -6)) });
        return Run(chain);
    }

    [Benchmark(Description = "Tempo 1.25 (Music), 60 s of 48 kHz stereo")]
    public long Tempo()
    {
        using Tempo tempo = new(1.25);
        return Run(tempo);
    }

    [Benchmark(Description = "Speed 1.25, 60 s of 48 kHz stereo")]
    public long Speed()
    {
        using Speed speed = new(1.25);
        return Run(speed);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        foreach (AudioFrame frame in _frames)
        {
            frame.Dispose();
        }
    }

    private long Run(IAudioFilter filter)
    {
        long samples = 0;
        foreach (AudioFrame frame in _frames)
        {
            filter.Send(frame);
            while (filter.TryReceive(out AudioFrame? output))
            {
                samples += output.SampleCount;
                output.Dispose();
            }
        }

        filter.Flush();
        while (filter.TryReceive(out AudioFrame? output))
        {
            samples += output.SampleCount;
            output.Dispose();
        }

        return samples;
    }
}
