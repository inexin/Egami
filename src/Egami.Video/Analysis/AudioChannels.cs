using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// Reads each channel of an audio frame into float buffers that grow with the largest frame seen, so the audio detectors take any
/// sample format and allocate nothing per frame once warm. Also holds the rate and layout of the first frame, which every later
/// frame must share.
/// </summary>
internal sealed class AudioChannels
{
    private float[][] _buffers = [];

    /// <summary>Gets the sample rate, or 0 before the first frame.</summary>
    public int SampleRate { get; private set; }

    /// <summary>Gets the channel layout of the first frame.</summary>
    public ChannelLayout Layout { get; private set; }

    /// <summary>Gets the number of channels, or 0 before the first frame.</summary>
    public int Count => _buffers.Length;

    /// <summary>Reads a frame's samples; <see cref="Channel"/> then returns them.</summary>
    /// <exception cref="InvalidOperationException">The frame's rate or layout differs from the first frame's.</exception>
    public void Read(AudioFrame frame, string owner)
    {
        if (SampleRate == 0)
        {
            SampleRate = frame.SampleRate;
            Layout = frame.Layout;
            _buffers = new float[frame.ChannelCount][];
            for (int c = 0; c < _buffers.Length; c++)
            {
                _buffers[c] = [];
            }
        }
        else if (frame.SampleRate != SampleRate || frame.Layout != Layout)
        {
            throw new InvalidOperationException(
                $"The {owner} took {SampleRate} Hz {Layout} audio and cannot switch to {frame.SampleRate} Hz {frame.Layout}; resample first or use a new one.");
        }

        for (int c = 0; c < _buffers.Length; c++)
        {
            if (_buffers[c].Length < frame.SampleCount)
            {
                _buffers[c] = new float[Math.Max(frame.SampleCount, _buffers[c].Length * 2)];
            }

            SampleConverter.ReadFloats(frame, c, 0, _buffers[c].AsSpan(0, frame.SampleCount));
        }
    }

    /// <summary>Returns the first <paramref name="count"/> samples of a channel from the last frame read.</summary>
    public ReadOnlySpan<float> Channel(int channel, int count) => _buffers[channel].AsSpan(0, count);

    /// <summary>Returns the time of a sample in a frame, in the frame's own sample clock so it stays exact.</summary>
    public static MediaTime TimeOf(AudioFrame frame, long sample)
    {
        Rational rate = new(1, frame.SampleRate);
        return new MediaTime(frame.Timestamp.Rescale(rate).Ticks + sample, rate);
    }

    /// <summary>Converts a linear amplitude or level to dBFS, with silence as negative infinity.</summary>
    public static double Decibels(double amplitude) => amplitude > 0 ? 20 * Math.Log10(amplitude) : double.NegativeInfinity;
}
