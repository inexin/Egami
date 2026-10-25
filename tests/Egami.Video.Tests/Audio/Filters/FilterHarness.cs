using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Tests.Audio.Filters;

/// <summary>Runs filters over whole signals and compares the results.</summary>
internal static class FilterHarness
{
    public static string PathOf(string name) => Path.Combine(AudioCorpus.Directory, "filters", name + ".flac");

    /// <summary>Reads a reference clip as planar floats.</summary>
    public static float[][] Read(string name, out int rate)
    {
        using AudioReader reader = AudioReader.Open(PathOf(name), new AudioReaderOptions { Errors = DecodeErrorMode.Throw });
        List<AudioFrame> frames = [.. reader.ReadFrames()];
        try
        {
            rate = frames[0].SampleRate;
            return Join(frames);
        }
        finally
        {
            frames.ForEach(f => f.Dispose());
        }
    }

    /// <summary>Cuts planar floats into frames of a size, timed from a start.</summary>
    public static List<AudioFrame> Frames(float[][] signal, int rate, int size = 1024, MediaTime start = default)
    {
        ChannelLayout layout = signal.Length == 1 ? ChannelLayout.Mono : signal.Length == 2 ? ChannelLayout.Stereo : throw new ArgumentException("Mono or stereo only.", nameof(signal));
        List<AudioFrame> frames = [];
        long first = start.Rescale(new Rational(1, rate)).Ticks;
        for (int at = 0; at < signal[0].Length; at += size)
        {
            int n = Math.Min(size, signal[0].Length - at);
            AudioFrame frame = new(SampleFormat.F32Planar, layout, rate, n, new MediaTime(first + at, new Rational(1, rate)));
            for (int c = 0; c < signal.Length; c++)
            {
                signal[c].AsSpan(at, n).CopyTo(frame.GetSpan<float>(c));
            }

            frames.Add(frame);
        }

        return frames;
    }

    /// <summary>Sends frames through a filter, flushes it and joins the output, checking the timestamps run on without gaps.</summary>
    public static float[][] Run(IAudioFilter filter, IEnumerable<AudioFrame> frames, out MediaTime start)
    {
        List<AudioFrame> output = [];
        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                filter.Send(frame);
            }

            Collect(filter, output);
        }

        filter.Flush();
        Collect(filter, output);
        try
        {
            CheckContiguous(output);
            start = output.Count > 0 ? output[0].Timestamp : MediaTime.Zero;
            return Join(output);
        }
        finally
        {
            output.ForEach(f => f.Dispose());
        }
    }

    private static void Collect(IAudioFilter filter, List<AudioFrame> output)
    {
        while (filter.TryReceive(out AudioFrame? frame))
        {
            output.Add(frame);
        }
    }

    public static float[][] Run(IAudioFilter filter, float[][] signal, int rate, int size = 1024) => Run(filter, Frames(signal, rate, size), out _);

    public static void CheckContiguous(IReadOnlyList<AudioFrame> frames)
    {
        for (int i = 1; i < frames.Count; i++)
        {
            Assert.Equal(frames[i - 1].Timestamp + frames[i - 1].Duration, frames[i].Timestamp);
            Assert.Equal(SampleFormat.F32Planar, frames[i].Format);
        }
    }

    public static float[][] Join(IReadOnlyList<AudioFrame> frames)
    {
        if (frames.Count == 0)
        {
            return [[]];
        }

        int channels = frames[0].ChannelCount;
        float[][] result = new float[channels][];
        int total = frames.Sum(f => f.SampleCount);
        for (int c = 0; c < channels; c++)
        {
            result[c] = new float[total];
            int at = 0;
            foreach (AudioFrame frame in frames)
            {
                using AudioFrame floats = frame.Format == SampleFormat.F32Planar ? frame.Slice(0, frame.SampleCount) : frame.Convert(SampleFormat.F32Planar);
                floats.AsSpan<float>(c).CopyTo(result[c].AsSpan(at));
                at += frame.SampleCount;
            }
        }

        return result;
    }

    /// <summary>Returns the RMS of the difference over every channel, in dBFS.</summary>
    public static double DifferenceDb(float[][] a, float[][] b)
    {
        Assert.Equal(a.Length, b.Length);
        double sum = 0;
        long count = 0;
        for (int c = 0; c < a.Length; c++)
        {
            Assert.Equal(a[c].Length, b[c].Length);
            for (int i = 0; i < a[c].Length; i++)
            {
                double d = a[c][i] - b[c][i];
                sum += d * d;
            }

            count += a[c].Length;
        }

        return 10 * Math.Log10((sum / Math.Max(1, count)) + 1e-30);
    }

    public static float[][] Tone(int rate, double seconds, double frequency, double amplitude = 0.5, int channels = 2)
    {
        int n = (int)Math.Round(rate * seconds);
        float[][] signal = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            signal[c] = new float[n];
            for (int i = 0; i < n; i++)
            {
                signal[c][i] = (float)(amplitude * Math.Sin(2 * Math.PI * frequency * (c + 1) * i / rate));
            }
        }

        return signal;
    }

    /// <summary>Estimates a signal's frequency from its rising zero crossings.</summary>
    public static double Frequency(ReadOnlySpan<float> samples, int rate)
    {
        int first = -1, last = -1, crossings = 0;
        for (int i = 1; i < samples.Length; i++)
        {
            if (samples[i - 1] < 0 && samples[i] >= 0)
            {
                if (first < 0)
                {
                    first = i;
                }
                else
                {
                    crossings++;
                }

                last = i;
            }
        }

        return crossings == 0 ? 0 : crossings * (double)rate / (last - first);
    }
}
