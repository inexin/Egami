namespace Egami.Video.Audio.Filters;

/// <summary>
/// Adds silence at the end of a stream, like ffmpeg's <c>apad</c>: a fixed amount, or as much as it takes for the stream to
/// last at least a total length. The silence comes out on <see cref="AudioFilter.Flush"/>; the input passes through.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-timing"/>
/// </example>
public sealed class Pad : AudioFilter
{
    private const int Chunk = 4096;

    private long _first;
    private long _next;
    private bool _started;

    /// <summary>Creates a pad of a fixed amount of silence.</summary>
    /// <param name="silence">How much silence to add.</param>
    /// <exception cref="ArgumentOutOfRangeException">The amount is negative.</exception>
    public Pad(MediaTime silence)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(silence, MediaTime.Zero);
        Silence = silence;
    }

    /// <summary>Gets the fixed amount of silence added, or zero when <see cref="MinimumLength"/> is used alone.</summary>
    public MediaTime Silence { get; }

    /// <summary>Gets the length the output lasts at least, from its first sample, or <see langword="null"/> for none.</summary>
    public MediaTime? MinimumLength { get; init; }

    /// <summary>Creates a pad that makes the stream last at least a length.</summary>
    /// <param name="length">The length, from the first sample.</param>
    /// <returns>The filter.</returns>
    public static Pad ToLength(MediaTime length) => new(MediaTime.Zero) { MinimumLength = length };

    private protected override void OnReset() => _started = false;

    private protected override void Process(AudioFrame frame)
    {
        long index = IndexOf(frame.Timestamp);
        if (!_started)
        {
            _started = true;
            _first = index;
        }

        _next = index + frame.SampleCount;
        Emit(frame);
    }

    private protected override void OnFlush()
    {
        if (!_started)
        {
            return;
        }

        long end = _next + IndexOf(Silence);
        if (MinimumLength is MediaTime minimum)
        {
            end = Math.Max(end, _first + IndexOf(minimum));
        }

        while (_next < end)
        {
            int n = (int)Math.Min(end - _next, Chunk);
            Emit(NewFrame(Layout, n, _next));
            _next += n;
        }

        _started = false;
    }
}

/// <summary>
/// Keeps the part of a stream between two times on its time line, like ffmpeg's <c>atrim</c>, cut on the exact sample. The
/// samples kept keep their timestamps; follow with <see cref="Retime"/> to start the result at zero.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-timing"/>
/// </example>
public sealed class Trim : AudioFilter
{
    private long _start;
    private long _end;

    /// <summary>Creates a trim.</summary>
    /// <param name="start">The time of the first sample kept.</param>
    /// <param name="end">The time the kept part ends (the first sample dropped), or <see langword="null"/> for the end of the stream.</param>
    /// <exception cref="ArgumentOutOfRangeException">The end is before the start.</exception>
    public Trim(MediaTime start, MediaTime? end = null)
    {
        if (end is MediaTime e)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(e, start, nameof(end));
        }

        Start = start;
        End = end;
    }

    /// <summary>Gets the time of the first sample kept.</summary>
    public MediaTime Start { get; }

    /// <summary>Gets the time the kept part ends, or <see langword="null"/> for the end of the stream.</summary>
    public MediaTime? End { get; }

    private protected override void OnStart()
    {
        _start = IndexOf(Start);
        _end = End is MediaTime end ? IndexOf(end) : long.MaxValue;
    }

    private protected override void Process(AudioFrame frame)
    {
        long first = IndexOf(frame.Timestamp);
        long last = first + frame.SampleCount;
        long from = Math.Max(first, _start), to = Math.Min(last, _end);
        if (to <= from)
        {
            frame.Dispose();
            return;
        }

        if (from != first || to != last)
        {
            frame.Trim((int)(from - first), (int)(to - from));
            frame.Timestamp = TimeAt(from);
        }

        Emit(frame);
    }
}

/// <summary>
/// Gives a stream new timestamps, like ffmpeg's <c>asetpts=PTS-STARTPTS</c>: the first sample gets <see cref="Start"/> and
/// every later one follows on at the sample rate, so gaps and jitter in the input's timestamps go away.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-timing"/>
/// </example>
public sealed class Retime : AudioFilter
{
    private long _next;
    private bool _started;

    /// <summary>Creates a retime.</summary>
    /// <param name="start">The time of the first sample. Default zero.</param>
    public Retime(MediaTime start = default)
    {
        Start = start;
    }

    /// <summary>Gets the time of the first sample.</summary>
    public MediaTime Start { get; }

    private protected override void OnReset() => _started = false;

    private protected override void Process(AudioFrame frame)
    {
        if (!_started)
        {
            _started = true;
            _next = IndexOf(Start);
        }

        frame.Timestamp = TimeAt(_next);
        _next += frame.SampleCount;
        Emit(frame);
    }
}
