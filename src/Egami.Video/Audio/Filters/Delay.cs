namespace Egami.Video.Audio.Filters;

/// <summary>
/// Delays channels by putting silence in front of them, like ffmpeg's <c>adelay</c>: each channel can have its own delay, for
/// example to line up microphones or fix lip sync. The output keeps the input's timestamps and gets longer by the longest
/// delay; <see cref="AudioFilter.Flush"/> hands out the tail, so no input is lost.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-timing"/>
/// </example>
public sealed class Delay : AudioFilter
{
    private const int Chunk = 4096;

    private readonly MediaTime[] _delays;
    private readonly bool _all;
    private float[][] _lines = [];
    private float[] _scratch = [];
    private long _next;
    private bool _started;

    /// <summary>Creates a delay of every channel.</summary>
    /// <param name="delay">The delay.</param>
    /// <exception cref="ArgumentOutOfRangeException">The delay is negative.</exception>
    public Delay(MediaTime delay)
        : this([delay])
    {
        _all = true;
    }

    /// <summary>Creates a delay per channel.</summary>
    /// <param name="delays">The delay of each channel, in order; channels past the end are not delayed.</param>
    /// <exception cref="ArgumentOutOfRangeException">A delay is negative.</exception>
    public Delay(IReadOnlyList<MediaTime> delays)
    {
        ArgumentNullException.ThrowIfNull(delays);
        foreach (MediaTime delay in delays)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(delay, MediaTime.Zero, nameof(delays));
        }

        _delays = [.. delays];
    }

    /// <summary>Gets the delay of each channel; with one delay for every channel, a list of one.</summary>
    public IReadOnlyList<MediaTime> Delays => _delays;

    private protected override void OnStart()
    {
        int channels = Layout.ChannelCount;
        _lines = new float[channels][];
        for (int c = 0; c < channels; c++)
        {
            MediaTime delay = _all ? _delays[0] : c < _delays.Length ? _delays[c] : MediaTime.Zero;
            _lines[c] = new float[checked((int)IndexOf(delay))];
        }

        OnReset();
    }

    private protected override void OnReset()
    {
        _started = false;
        foreach (float[] line in _lines)
        {
            Array.Clear(line);
        }
    }

    private protected override void Process(AudioFrame frame)
    {
        if (!_started)
        {
            _started = true;
            _next = IndexOf(frame.Timestamp);
        }

        int n = frame.SampleCount;
        for (int c = 0; c < _lines.Length; c++)
        {
            Shift(_lines[c], frame.GetSpan<float>(c));
        }

        frame.Timestamp = TimeAt(_next);
        _next += n;
        Emit(frame);
    }

    private protected override void OnFlush()
    {
        if (!_started)
        {
            return;
        }

        int tail = _lines.Max(l => l.Length);
        while (tail > 0)
        {
            int n = Math.Min(tail, Chunk);
            AudioFrame frame = NewFrame(Layout, n, _next);
            for (int c = 0; c < _lines.Length; c++)
            {
                Shift(_lines[c], frame.GetSpan<float>(c));
            }

            _next += n;
            tail -= n;
            Emit(frame);
        }

        _started = false;
    }

    /// <summary>Puts the held samples in front of the block and keeps the block's last ones: the line holds them oldest first.</summary>
    private void Shift(float[] line, Span<float> block)
    {
        int d = line.Length, n = block.Length;
        if (d == 0)
        {
            return;
        }

        if (_scratch.Length < d + n)
        {
            _scratch = new float[d + n];
        }

        Span<float> all = _scratch.AsSpan(0, d + n);
        line.CopyTo(all);
        block.CopyTo(all[d..]);
        all[..n].CopyTo(block);
        all[n..].CopyTo(line);
    }
}
