using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Filters;

/// <summary>
/// Joins two streams with an overlap: the end of the first fades out while the start of the second fades in, like ffmpeg's
/// <c>acrossfade</c>. The output is the first stream up to its last <see cref="Duration"/>, then the overlap mixed, then the
/// rest of the second stream, so it lasts the two lengths minus the overlap. The overlap shortens to the shorter stream's
/// length when one is shorter. Send each input's frames with <see cref="Send"/> and end each with <see cref="Complete"/>;
/// frames of the second input wait until the first has ended. Both inputs must have the same rate and channels. Output
/// starts at the first input's first timestamp and runs on without gaps.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-crossfade"/>
/// </example>
public sealed class Crossfade : IDisposable
{
    private const int Chunk = 4096;

    private readonly MediaContext _context;
    private readonly Queue<AudioFrame> _ready = new();
    private SampleBuffer? _first;
    private SampleBuffer? _second;
    private int _rate;
    private ChannelLayout _layout;
    private long _origin;
    private long _produced;
    private bool _firstDone;
    private bool _secondDone;
    private bool _overlapDone;

    /// <summary>Creates a crossfade.</summary>
    /// <param name="duration">The overlap.</param>
    /// <param name="curve">The shape of both fades. Default <see cref="FadeCurve.EqualPower"/>.</param>
    /// <param name="context">The context frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative.</exception>
    public Crossfade(MediaTime duration, FadeCurve curve = FadeCurve.EqualPower, MediaContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        Duration = duration;
        Curve = curve;
        _context = context ?? MediaContext.Default;
    }

    /// <summary>Gets the overlap.</summary>
    public MediaTime Duration { get; }

    /// <summary>Gets the shape of both fades: the second stream fades in along it, the first fades out along its mirror.</summary>
    public FadeCurve Curve { get; }

    /// <summary>Gives a frame of one input, in any sample format. The caller keeps the frame.</summary>
    /// <param name="input">0 for the first stream, 1 for the second.</param>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="input"/> is not 0 or 1.</exception>
    /// <exception cref="ArgumentException">The frame's rate or channels differ from the first frame's.</exception>
    /// <exception cref="InvalidOperationException">The input has been completed.</exception>
    public void Send(int input, AudioFrame frame)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, 1);
        ArgumentNullException.ThrowIfNull(frame);
        if (input == 0 ? _firstDone : _secondDone)
        {
            throw new InvalidOperationException($"Input {input} has been completed.");
        }

        if (_first is null)
        {
            _rate = frame.SampleRate;
            _layout = frame.Layout;
            _first = new SampleBuffer(frame.ChannelCount);
            _second = new SampleBuffer(frame.ChannelCount);
        }
        else if (frame.SampleRate != _rate || frame.Layout != _layout)
        {
            throw new ArgumentException($"The crossfade runs at {_rate} Hz with {_layout}; the frame has {frame.SampleRate} Hz and {frame.Layout}.", nameof(frame));
        }

        if (frame.SampleCount == 0)
        {
            return;
        }

        if (input == 0 && _first.Count == 0 && _produced == 0)
        {
            _origin = frame.Timestamp.Rescale(new Rational(1, _rate)).Ticks;
        }

        Append(input == 0 ? _first : _second!, frame);
        Produce();
    }

    /// <summary>Ends one input.</summary>
    /// <param name="input">0 for the first stream, 1 for the second.</param>
    public void Complete(int input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(input, 1);
        if (input == 0)
        {
            _firstDone = true;
        }
        else
        {
            _secondDone = true;
        }

        Produce();
    }

    /// <summary>Ends both inputs, so the rest comes out.</summary>
    public void Flush()
    {
        _firstDone = true;
        _secondDone = true;
        Produce();
    }

    /// <summary>Takes the next frame.</summary>
    /// <param name="frame">The frame, F32 planar, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    /// <summary>Drops everything held and starts again with two new inputs.</summary>
    public void Reset()
    {
        Drain();
        _first?.Clear();
        _second?.Clear();
        _produced = 0;
        _firstDone = _secondDone = _overlapDone = false;
    }

    /// <summary>Drops the frames ready.</summary>
    public void Dispose() => Drain();

    private static void Append(SampleBuffer buffer, AudioFrame frame)
    {
        if (frame.Format == SampleFormat.F32Planar)
        {
            buffer.Append(frame);
            return;
        }

        using AudioFrame floats = frame.Convert(SampleFormat.F32Planar);
        buffer.Append(floats);
    }

    private void Produce()
    {
        if (_first is null || _second is null)
        {
            return;
        }

        int overlap = (int)Duration.Rescale(new Rational(1, _rate)).Ticks;
        if (!_firstDone)
        {
            // Everything but the last overlap of the first stream is final.
            Output(_first, _first.Count - overlap);
            return;
        }

        if (!_overlapDone)
        {
            int length = Math.Min(overlap, _first.Count);
            if (_second.Count < length && !_secondDone)
            {
                return;
            }

            length = Math.Min(length, _second.Count);
            Output(_first, _first.Count - length);
            Mix(length);
            _overlapDone = true;
        }

        Output(_second, _second.Count);
    }

    private void Mix(int length)
    {
        int channels = _layout.ChannelCount;
        if (length > 0)
        {
            AudioFrame frame = new(SampleFormat.F32Planar, _layout, _rate, length, Time(), _context);
            for (int c = 0; c < channels; c++)
            {
                Span<float> output = frame.GetSpan<float>(c);
                ReadOnlySpan<float> a = _first![c], b = _second![c];
                for (int i = 0; i < length; i++)
                {
                    // As ffmpeg's acrossfade: the second input rises from 0 on the overlap's first sample, and the first input
                    // reaches 0 on its last, one step ahead of the rise.
                    double rise = (double)i / length, fall = (double)(i + 1) / length;
                    output[i] = (float)((a[i] * Fade.CurveGain(Curve, 1 - fall)) + (b[i] * Fade.CurveGain(Curve, rise)));
                }
            }

            _produced += length;
            _ready.Enqueue(frame);
        }

        _first!.Clear();
        _second!.Drop(length);
    }

    private void Output(SampleBuffer buffer, int count)
    {
        while (count > 0)
        {
            int n = Math.Min(count, Chunk);
            AudioFrame frame = new(SampleFormat.F32Planar, _layout, _rate, n, Time(), _context);
            buffer.CopyTo(frame);
            buffer.Drop(n);
            _produced += n;
            count -= n;
            _ready.Enqueue(frame);
        }
    }

    private MediaTime Time() => new(_origin + _produced, new Rational(1, _rate));

    private void Drain()
    {
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }
}
