namespace Egami.Video.Audio.Filters;

/// <summary>A growable queue of planar float samples, one array per channel: the held input of filters that buffer.</summary>
internal sealed class SampleBuffer
{
    private readonly float[][] _channels;
    private int _start;

    public SampleBuffer(int channels, int capacity = 4096)
    {
        _channels = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            _channels[c] = new float[Math.Max(capacity, 16)];
        }
    }

    /// <summary>Gets the number of channels.</summary>
    public int ChannelCount => _channels.Length;

    /// <summary>Gets the number of samples held per channel.</summary>
    public int Count { get; private set; }

    /// <summary>Returns the samples held of a channel.</summary>
    public Span<float> this[int channel] => _channels[channel].AsSpan(_start, Count);

    /// <summary>Appends a frame's samples, which must be F32 planar with this buffer's channel count.</summary>
    public void Append(AudioFrame frame)
    {
        int n = frame.SampleCount;
        Grow(n);
        for (int c = 0; c < _channels.Length; c++)
        {
            frame.AsSpan<float>(c).CopyTo(_channels[c].AsSpan(_start + Count, n));
        }

        Count += n;
    }

    /// <summary>Appends samples from planar spans, one per channel.</summary>
    public void Append(int channel, ReadOnlySpan<float> samples, bool last)
    {
        if (channel == 0)
        {
            Grow(samples.Length);
        }

        samples.CopyTo(_channels[channel].AsSpan(_start + Count, samples.Length));
        if (last)
        {
            Count += samples.Length;
        }
    }

    /// <summary>Appends silence.</summary>
    public void AppendSilence(int count)
    {
        Grow(count);
        for (int c = 0; c < _channels.Length; c++)
        {
            _channels[c].AsSpan(_start + Count, count).Clear();
        }

        Count += count;
    }

    /// <summary>Copies the first samples into a frame from its first sample, without removing them.</summary>
    public void CopyTo(AudioFrame frame, int start = 0)
    {
        int n = frame.SampleCount;
        for (int c = 0; c < _channels.Length; c++)
        {
            _channels[c].AsSpan(_start + start, n).CopyTo(frame.GetSpan<float>(c));
        }
    }

    /// <summary>Removes samples from the front.</summary>
    public void Drop(int count)
    {
        count = Math.Min(count, Count);
        _start += count;
        Count -= count;
        if (Count == 0)
        {
            _start = 0;
        }
    }

    /// <summary>Removes every sample.</summary>
    public void Clear()
    {
        _start = 0;
        Count = 0;
    }

    private void Grow(int extra)
    {
        int needed = Count + extra;
        if (_start + needed <= _channels[0].Length)
        {
            return;
        }

        if (needed <= _channels[0].Length)
        {
            for (int c = 0; c < _channels.Length; c++)
            {
                _channels[c].AsSpan(_start, Count).CopyTo(_channels[c]);
            }

            _start = 0;
            return;
        }

        int size = Math.Max(needed, _channels[0].Length * 2);
        for (int c = 0; c < _channels.Length; c++)
        {
            float[] grown = new float[size];
            _channels[c].AsSpan(_start, Count).CopyTo(grown);
            _channels[c] = grown;
        }

        _start = 0;
    }
}
