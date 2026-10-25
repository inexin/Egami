using Egami.Video;
using Egami.Video.Analysis;
using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.DocSamples;

[Collection("Samples")]
public class AudioFilters
{
    private static List<AudioFrame> Music(double seconds = 3) =>
        [.. TestSources.Sine(TestAudioFormat.Default, MediaTime.FromSeconds(seconds), 440)];

    private static int Drain(IAudioFilter filter)
    {
        int samples = 0;
        while (filter.TryReceive(out AudioFrame? output))
        {
            samples += output.SampleCount;
            output.Dispose();
        }

        return samples;
    }

    [Fact]
    public void Chain()
    {
        List<AudioFrame> frames = Music();
        int samples = 0;

        #region audio-filter-chain
        // Fade in over 2 s, then turn down 3 dB: frames of any format go in, F32 planar frames come out.
        using AudioFilterChain chain = new AudioFilterChain(48000, ChannelLayout.Stereo)
            .Add(new AudioFade(FadeDirection.In, MediaTime.Zero, MediaTime.FromSeconds(2)))
            .Add(new Gain(-3));
        foreach (AudioFrame frame in frames)
        {
            chain.Send(frame);
            while (chain.TryReceive(out AudioFrame? output))
            {
                samples += output.SampleCount; // write it to an AudioWriter or encoder here
                output.Dispose();
            }
        }

        chain.Flush();
        #endregion
        samples += Drain(chain);
        frames.ForEach(f => f.Dispose());
        Assert.Equal(3 * 48000, samples);
    }

    [Fact]
    public void Envelope()
    {
        List<AudioFrame> frames = Music();

        #region audio-gain-envelope
        // Duck to -12 dB from 1 s to 2 s, easing down and back up over a quarter second.
        using Gain duck = new()
        {
            Envelope = new GainEnvelope(
                new Keyframe(MediaTime.FromSeconds(0.75), 0, Easing.EaseInOut),
                new Keyframe(MediaTime.FromSeconds(1), -12),
                new Keyframe(MediaTime.FromSeconds(2), -12, Easing.EaseInOut),
                new Keyframe(MediaTime.FromSeconds(2.25), 0)),
        };
        #endregion
        foreach (AudioFrame frame in frames)
        {
            duck.Send(frame);
            frame.Dispose();
        }

        Assert.Equal(3 * 48000, Drain(duck));
    }

    [Fact]
    public void Fades()
    {
        List<AudioFrame> frames = Music();

        #region audio-fades
        // A 3 s clip: fade out over its last second along an S curve.
        using AudioFade fadeOut = new(FadeDirection.Out, MediaTime.FromSeconds(2), MediaTime.FromSeconds(1), FadeCurve.SCurve);
        #endregion
        foreach (AudioFrame frame in frames)
        {
            fadeOut.Send(frame);
            frame.Dispose();
        }

        Assert.Equal(3 * 48000, Drain(fadeOut));
    }

    [Fact]
    public void Crossfades()
    {
        List<AudioFrame> first = Music(), second = Music(2);

        #region audio-crossfade
        // Join two clips with a 1 s equal-power overlap: 3 s + 2 s - 1 s = 4 s of output.
        using Crossfade crossfade = new(MediaTime.FromSeconds(1));
        foreach (AudioFrame frame in first)
        {
            crossfade.Send(0, frame);
        }

        crossfade.Complete(0);
        foreach (AudioFrame frame in second)
        {
            crossfade.Send(1, frame);
        }

        crossfade.Complete(1);
        int samples = 0;
        while (crossfade.TryReceive(out AudioFrame? output))
        {
            samples += output.SampleCount;
            output.Dispose();
        }
        #endregion
        first.Concat(second).ToList().ForEach(f => f.Dispose());
        Assert.Equal(4 * 48000, samples);
    }

    [Fact]
    public void Mixing()
    {
        List<AudioFrame> music = Music();
        List<AudioFrame> voice = [.. TestSources.Sine(new TestAudioFormat { Layout = ChannelLayout.Mono, SampleRate = 44100 }, MediaTime.FromSeconds(2), 220)];

        #region audio-mixer
        // A mono voice at 44.1 kHz over stereo music at 48 kHz, the music at half level: the voice is panned a little left,
        // then resampled and placed by its timestamps on the way into the mixer.
        using Pan pan = new(-0.3);
        using AudioMixer mixer = new(48000, ChannelLayout.Stereo, inputs: 2);
        mixer.SetWeight(0, 0.5);
        foreach (AudioFrame frame in music)
        {
            mixer.Send(0, frame);
        }

        foreach (AudioFrame frame in voice)
        {
            pan.Send(frame);
            while (pan.TryReceive(out AudioFrame? panned))
            {
                mixer.Send(1, panned);
                panned.Dispose();
            }
        }

        mixer.Flush();
        #endregion
        int samples = 0;
        while (mixer.TryReceive(out AudioFrame? output))
        {
            samples += output.SampleCount;
            output.Dispose();
        }

        music.Concat(voice).ToList().ForEach(f => f.Dispose());
        Assert.Equal(3 * 48000, samples);
    }

    [Fact]
    public void Timing()
    {
        List<AudioFrame> frames = Music();

        #region audio-timing
        // Keep 0.5 s to 2.5 s, start it at zero, delay the right channel 20 ms and add half a second of silence.
        using AudioFilterChain chain = new AudioFilterChain(48000, ChannelLayout.Stereo)
            .Add(new Trim(MediaTime.FromSeconds(0.5), MediaTime.FromSeconds(2.5)))
            .Add(new Retime())
            .Add(new Delay([MediaTime.Zero, MediaTime.FromSeconds(0.02)]))
            .Add(new Pad(MediaTime.FromSeconds(0.5)));
        #endregion
        foreach (AudioFrame frame in frames)
        {
            chain.Send(frame);
            frame.Dispose();
        }

        int samples = Drain(chain);
        chain.Flush();
        samples += Drain(chain);
        Assert.Equal(120960, samples);
    }

    [Fact]
    public void TempoAndSpeed()
    {
        List<AudioFrame> frames = Music();

        #region audio-tempo
        // A quarter faster with the pitch kept (3 s becomes 2.4 s); Speed would raise the pitch too, like a tape.
        using Tempo tempo = new(1.25, TempoPreset.Speech);
        using Speed speed = new(1.25);
        #endregion
        foreach (AudioFrame frame in frames)
        {
            tempo.Send(frame);
            speed.Send(frame);
            frame.Dispose();
        }

        tempo.Flush();
        speed.Flush();
        Assert.Equal(115200, Drain(tempo));
        Assert.Equal(115200, Drain(speed));
    }
}
