namespace Egami.Formats;

/// <summary>
/// Builds a save's <see cref="SaveProgress"/> reports and checks its cancellation. A save creates one only when the caller passed
/// a handler or a token that can cancel, and makes it <see cref="Current"/> while it encodes, so writers deep in a codec find it
/// without a parameter; a save without one builds, times and checks nothing.
/// </summary>
internal sealed class SaveReporter
{
    private static readonly AsyncLocal<SaveReporter?> Ambient = new();

    private readonly IProgress<SaveProgress>? _progress;
    private readonly TimeProvider _time;
    private readonly long _interval;
    private readonly long _start;
    private readonly object _gate = new();
    private Stream? _output;
    private long _outputStart;
    private long _next;
    private double _fraction;
    private bool _known;
    private bool _claimed;
    private long _planned;
    private long _done;
    private double _partFrom;
    private double _partTo = 1;
    private int _frames;
    private long _written;

    public SaveReporter(IProgress<SaveProgress>? progress, CancellationToken cancellationToken, TimeProvider? time = null)
    {
        _progress = progress;
        Cancellation = cancellationToken;
        _time = time ?? TimeProvider.System;
        _interval = _time.TimestampFrequency / 4;
        _start = _time.GetTimestamp();
    }

    /// <summary>Gets the reporter of the save running on this flow, or <see langword="null"/>.</summary>
    public static SaveReporter? Current => Ambient.Value;

    /// <summary>Gets the save's cancellation.</summary>
    public CancellationToken Cancellation { get; }

    /// <summary>Creates a reporter when either argument asks for one.</summary>
    public static SaveReporter? For(IProgress<SaveProgress>? progress, CancellationToken cancellationToken)
    {
        return progress is null && !cancellationToken.CanBeCanceled ? null : new SaveReporter(progress, cancellationToken);
    }

    /// <summary>Makes this the current reporter until the scope is disposed, and sends the first report.</summary>
    /// <param name="output">The stream written to, whose position counts the bytes when it can seek.</param>
    public Scope Enter(Stream? output)
    {
        Cancellation.ThrowIfCancellationRequested();
        SaveReporter? previous = Ambient.Value;
        Ambient.Value = this;
        Watch(output);
        long now = _time.GetTimestamp();
        _next = now + _interval;
        Send(now, final: false);
        return new Scope(previous, active: true);
    }

    /// <summary>Counts the bytes written to <paramref name="output"/> from here on, when no stream is counted yet and it can seek.</summary>
    public void Watch(Stream? output)
    {
        if (_output is null && output is { CanSeek: true })
        {
            _output = output;
            _outputStart = output.Position;
        }
    }

    /// <summary>
    /// Says that the codec counts the work itself with <see cref="Plan"/> and <see cref="Advance"/>, so the rows handed to it,
    /// which it only gathers, do not move the fraction.
    /// </summary>
    public void Claim() => _claimed = true;

    /// <summary>
    /// Starts a part of the codec's work that covers <paramref name="from"/> to <paramref name="to"/> of the save, such as the
    /// color planes and then the alpha plane, each with its own <see cref="Plan"/>.
    /// </summary>
    public void Part(double from, double to)
    {
        lock (_gate)
        {
            _partFrom = from;
            _partTo = to;
            Volatile.Write(ref _planned, 0);
            Volatile.Write(ref _done, 0);
        }
    }

    /// <summary>Adds <paramref name="units"/> to the codec's work in this part.</summary>
    public void Plan(long units) => Interlocked.Add(ref _planned, units);

    /// <summary>Counts <paramref name="units"/> of the codec's work as done; safe from parallel workers.</summary>
    public void Advance(long units)
    {
        Cancellation.ThrowIfCancellationRequested();
        long done = Interlocked.Add(ref _done, units);
        long planned = Volatile.Read(ref _planned);
        if (planned > 0)
        {
            Update(_partFrom + ((_partTo - _partFrom) * Math.Min(1, (double)done / planned)));
        }
    }

    /// <summary>Records that <paramref name="done"/> of <paramref name="total"/> rows reached the writer.</summary>
    public void Rows(long done, long total)
    {
        Cancellation.ThrowIfCancellationRequested();
        if (!_claimed && total > 0)
        {
            Update((double)done / total);
        }
    }

    /// <summary>Records that <paramref name="done"/> of <paramref name="total"/> frames were written.</summary>
    public void Frames(int done, int total)
    {
        Cancellation.ThrowIfCancellationRequested();
        Volatile.Write(ref _frames, done);
        if (!_claimed && total > 0)
        {
            Update((double)done / total);
        }
        else
        {
            Tick();
        }
    }

    /// <summary>Sends the final report.</summary>
    /// <param name="path">The file written, whose length is the bytes written, or <see langword="null"/> to count the stream.</param>
    public void Finish(string? path = null)
    {
        lock (_gate)
        {
            if (path is not null)
            {
                _output = null;
                _written = new FileInfo(path).Length;
            }

            _frames = Math.Max(_frames, 1);
            Send(_time.GetTimestamp(), final: true);
        }
    }

    private void Update(double fraction)
    {
        lock (_gate)
        {
            _known = true;
            _fraction = Math.Max(_fraction, Math.Clamp(fraction, 0, 1));
            long now = _time.GetTimestamp();
            if (now >= _next)
            {
                _next = now + _interval;
                Send(now, final: false);
            }
        }
    }

    private void Tick()
    {
        lock (_gate)
        {
            long now = _time.GetTimestamp();
            if (now >= _next)
            {
                _next = now + _interval;
                Send(now, final: false);
            }
        }
    }

    private void Send(long now, bool final)
    {
        if (_progress is null)
        {
            return;
        }

        // A file the save wrote through a stream of its own is closed by now: keep the count from the last report.
        if (_output is { CanSeek: true } output)
        {
            _written = Math.Max(0, output.Position - _outputStart);
        }

        _progress.Report(new SaveProgress
        {
            Fraction = final ? 1 : _known ? _fraction : null,
            Frames = _frames,
            BytesWritten = _written,
            Elapsed = _time.GetElapsedTime(_start, now),
            IsFinal = final,
        });
    }

    /// <summary>Restores the reporter that was current before <see cref="Enter"/>; the default scope does nothing.</summary>
    internal readonly struct Scope(SaveReporter? previous, bool active) : IDisposable
    {
        public void Dispose()
        {
            if (active)
            {
                Ambient.Value = previous;
            }
        }
    }
}
