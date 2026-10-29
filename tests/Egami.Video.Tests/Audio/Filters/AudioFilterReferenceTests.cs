using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Tests.Audio.Filters;

/// <summary>
/// The V9.1 quality gates (Design Q15): gain, fades, crossfades, pan, mix, delay (stage A) and the EQ (stage B) match FFmpeg's
/// references (tools/video/make-audio-filter-references.py) within -90 dBFS RMS difference.
/// </summary>
public class AudioFilterReferenceTests
{
    private const double Gate = -90;

    public static TheoryData<string> SingleInput => new()
    {
        "gain-minus6db", "gain-half", "fade-in-linear", "fade-out-equalpower", "fade-in-log", "fade-in-exp", "fade-out-scurve",
        "pan-mono-centre", "delay-per-channel", "highpass-80", "lowpass-3000", "bandpass-1000", "bandreject-1000", "allpass-1500",
        "equalizer-1000", "equalizer-octave", "bass-200", "treble-4000",
    };

    [Theory]
    [MemberData(nameof(SingleInput))]
    public void MatchesFfmpeg(string name)
    {
        (string input, IAudioFilter filter) = name switch
        {
            "gain-minus6db" => ("tone", (IAudioFilter)new Gain(-6)),
            "gain-half" => ("tone", Gain.FromFactor(0.5)),
            "fade-in-linear" => ("tone", new AudioFade(FadeDirection.In, Seconds(0.1), Seconds(0.3))),
            "fade-out-equalpower" => ("tone", new AudioFade(FadeDirection.Out, Seconds(0.4), Seconds(0.3), FadeCurve.EqualPower)),
            "fade-in-log" => ("tone", new AudioFade(FadeDirection.In, Seconds(0.1), Seconds(0.3), FadeCurve.Logarithmic)),
            "fade-in-exp" => ("tone", new AudioFade(FadeDirection.In, Seconds(0.1), Seconds(0.3), FadeCurve.Exponential)),
            "fade-out-scurve" => ("tone", new AudioFade(FadeDirection.Out, Seconds(0.2), Seconds(0.5), FadeCurve.SCurve)),
            "pan-mono-centre" => ("voice", new Pan()),
            "delay-per-channel" => ("tone", new Delay([Seconds(0.1), Seconds(0.25)])),
            "highpass-80" => ("noise", new Biquad(BiquadKind.HighPass, 80)),
            "lowpass-3000" => ("noise", new Biquad(BiquadKind.LowPass, 3000)),
            "bandpass-1000" => ("noise", new Biquad(BiquadKind.BandPass, 1000, q: 2)),
            "bandreject-1000" => ("noise", new Biquad(BiquadKind.Notch, 1000, q: 2)),
            "allpass-1500" => ("noise", new Biquad(BiquadKind.AllPass, 1500, q: 0.7)),
            "equalizer-1000" => ("noise", new Biquad(BiquadKind.Peaking, 1000, q: 1.5, gain: 6)),
            "equalizer-octave" => ("noise", Biquad.FromBandwidth(BiquadKind.Peaking, 2000, octaves: 1, gain: -8)),
            "bass-200" => ("noise", new Biquad(BiquadKind.LowShelf, 200, gain: 6)),
            "treble-4000" => ("noise", new Biquad(BiquadKind.HighShelf, 4000, gain: -5)),
            _ => throw new ArgumentOutOfRangeException(nameof(name)),
        };

        float[][] source = FilterHarness.Read(input, out int rate);
        float[][] expected = FilterHarness.Read(name, out _);
        float[][] actual;
        using (filter)
        {
            actual = FilterHarness.Run(filter, source, rate, size: 1000);
        }

        if (name == "fade-in-exp")
        {
            // FFmpeg's exponential fade in leaves -100 dB (its curve's start) before the fade; Egami is silent there.
            for (int c = 0; c < expected.Length; c++)
            {
                expected[c].AsSpan(0, (int)(0.1 * rate)).Clear();
            }
        }

        if (name == "delay-per-channel")
        {
            // FFmpeg's adelay keeps the input's length; Egami hands out the delayed tail too.
            Assert.Equal(source[0].Length + (int)(0.25 * rate), actual[0].Length);
            actual = [.. actual.Select(a => a[..expected[0].Length])];
        }

        double difference = FilterHarness.DifferenceDb(expected, actual);
        Assert.True(difference < Gate, $"{name}: {difference:F1} dBFS RMS difference");
    }

    [Theory]
    [InlineData("crossfade-equalpower", 0.2, FadeCurve.EqualPower)]
    [InlineData("crossfade-linear", 0.25, FadeCurve.Linear)]
    public void CrossfadeMatchesFfmpeg(string name, double seconds, FadeCurve curve)
    {
        float[][] first = FilterHarness.Read("tone", out int rate);
        float[][] second = FilterHarness.Read("tone2", out _);
        float[][] expected = FilterHarness.Read(name, out _);
        using Crossfade crossfade = new(Seconds(seconds), curve);
        List<AudioFrame> output = [];
        foreach ((int input, float[][] signal) in new[] { (0, first), (1, second) })
        {
            foreach (AudioFrame frame in FilterHarness.Frames(signal, rate, 1000))
            {
                using (frame)
                {
                    crossfade.Send(input, frame);
                }
            }

            crossfade.Complete(input);
        }

        while (crossfade.TryReceive(out AudioFrame? frame))
        {
            output.Add(frame);
        }

        FilterHarness.CheckContiguous(output);
        float[][] actual = FilterHarness.Join(output);
        output.ForEach(f => f.Dispose());
        Assert.Equal(first[0].Length + second[0].Length - (int)(seconds * rate), actual[0].Length);
        double difference = FilterHarness.DifferenceDb(expected, actual);
        Assert.True(difference < Gate, $"{name}: {difference:F1} dBFS RMS difference");
    }

    [Fact]
    public void MixMatchesFfmpeg()
    {
        float[][] a = FilterHarness.Read("tone", out int rate);
        float[][] b = FilterHarness.Read("tone2", out _);
        float[][] expected = FilterHarness.Read("mix-weighted", out _);
        using AudioMixer mixer = new(rate, ChannelLayout.Stereo, 2);
        mixer.SetWeight(1, 0.5);

        // Interleave the inputs' frames, as a player would.
        List<AudioFrame> fa = FilterHarness.Frames(a, rate, 1000), fb = FilterHarness.Frames(b, rate, 700);
        for (int i = 0; i < Math.Max(fa.Count, fb.Count); i++)
        {
            if (i < fa.Count)
            {
                mixer.Send(0, fa[i]);
                fa[i].Dispose();
            }

            if (i < fb.Count)
            {
                mixer.Send(1, fb[i]);
                fb[i].Dispose();
            }
        }

        mixer.Flush();
        List<AudioFrame> output = [];
        while (mixer.TryReceive(out AudioFrame? frame))
        {
            output.Add(frame);
        }

        FilterHarness.CheckContiguous(output);
        float[][] actual = FilterHarness.Join(output);
        output.ForEach(f => f.Dispose());
        double difference = FilterHarness.DifferenceDb(expected, actual);
        Assert.True(difference < Gate, $"mix: {difference:F1} dBFS RMS difference");
    }

    private static MediaTime Seconds(double seconds) => MediaTime.FromSeconds(seconds);
}
