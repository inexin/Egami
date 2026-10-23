namespace Egami.Video;

/// <summary>
/// Builds a job's <see cref="MediaProgress"/> reports: a first one, then at most one every 250 ms as packets are written, then
/// a final one. Jobs create it only when the caller passed a handler, so a job without one builds and times nothing.
/// </summary>
internal sealed class ProgressReporter
{
    private readonly IProgress<MediaProgress> _progress;
    private readonly TimeProvider _time;
    private readonly long _interval;
    private readonly long _start;
    private readonly MediaTime? _duration;
    private readonly long _inputLength;
    private readonly JobStream? _input;
    private readonly JobStream? _output;
    private readonly HashSet<int> _videoTracks;
    private long _next;
    private MediaTime _position = MediaTime.Zero;
    private double _positionSeconds;
    private long _frames;
    private long _packets;
    private double _fraction;

    /// <summary>Starts the clock.</summary>
    /// <param name="progress">Where to report.</param>
    /// <param name="duration">The input's duration, or <see langword="null"/>.</param>
    /// <param name="tracks">The tracks written; video tracks count frames.</param>
    /// <param name="input">Counts the bytes read, or <see langword="null"/>.</param>
    /// <param name="inputLength">The input's length from where the job started reading, or 0 when unknown.</param>
    /// <param name="output">Counts the bytes written, or <see langword="null"/>.</param>
    /// <param name="time">The clock, or <see langword="null"/> for <see cref="TimeProvider.System"/>.</param>
    public ProgressReporter(IProgress<MediaProgress> progress, MediaTime? duration, IEnumerable<TrackInfo> tracks, JobStream? input, long inputLength, JobStream? output, TimeProvider? time = null)
    {
        _progress = progress;
        _time = time ?? TimeProvider.System;
        _interval = _time.TimestampFrequency / 4;
        _start = _time.GetTimestamp();
        _duration = duration is { Ticks: > 0 } ? duration : null;
        _input = input;
        _inputLength = inputLength;
        _output = output;
        _videoTracks = [.. tracks.Where(t => t.Kind == TrackKind.Video).Select(t => t.Number)];
    }

    /// <summary>Sends the first report, once the output is open.</summary>
    public void Start()
    {
        long now = _time.GetTimestamp();
        _next = now + _interval;
        Report(now, final: false);
    }

    /// <summary>Counts a written packet and reports if 250 ms have passed since the last report.</summary>
    /// <param name="packet">The packet, as written to the output.</param>
    public void Written(EncodedPacket packet)
    {
        _packets++;
        if (_videoTracks.Contains(packet.TrackNumber))
        {
            _frames++;
        }

        double seconds = packet.Timestamp.Seconds;
        if (seconds > _positionSeconds)
        {
            _positionSeconds = seconds;
            _position = packet.Timestamp;
        }

        long now = _time.GetTimestamp();
        if (now >= _next)
        {
            _next = now + _interval;
            Report(now, final: false);
        }
    }

    /// <summary>Sends the final report.</summary>
    public void Finish() => Report(_time.GetTimestamp(), final: true);

    private void Report(long now, bool final)
    {
        long read = _input?.BytesRead ?? 0;
        double? fraction = null;
        if (final)
        {
            fraction = 1;
        }
        else if (_duration is { } duration)
        {
            fraction = _positionSeconds / duration.Seconds;
        }
        else if (_inputLength > 0 && _input is not null)
        {
            fraction = (double)read / _inputLength;
        }

        if (fraction is { } f)
        {
            _fraction = Math.Max(_fraction, Math.Clamp(f, 0, 1));
            fraction = _fraction;
        }

        TimeSpan elapsed = _time.GetElapsedTime(_start, now);
        _progress.Report(new MediaProgress
        {
            Position = _position,
            Duration = _duration,
            Fraction = fraction,
            Frames = _frames,
            Packets = _packets,
            BytesRead = read,
            BytesWritten = _output?.BytesWritten ?? 0,
            Elapsed = elapsed,
            Speed = elapsed > TimeSpan.Zero ? _positionSeconds / elapsed.TotalSeconds : null,
            IsFinal = final,
        });
    }
}
