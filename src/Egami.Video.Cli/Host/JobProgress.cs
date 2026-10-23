namespace Egami.Video.Cli.Host;

/// <summary>
/// Builds <see cref="MediaProgress"/> reports for the tool's own packet and frame loops, as Egami.Video's jobs do: a first one,
/// then at most one every 250 ms, then a final one. Without a handler it does nothing.
/// </summary>
internal sealed class JobProgress
{
    private readonly IProgress<MediaProgress>? _progress;
    private readonly long _start;
    private readonly MediaTime? _duration;
    private readonly CountingStream? _output;
    private readonly CountingStream? _input;
    private readonly HashSet<int> _videoTracks;
    private readonly CancellationToken _cancellation;
    private long _next;
    private MediaTime _position = MediaTime.Zero;
    private double _positionSeconds;
    private double _offsetSeconds;
    private long _frames;
    private long _packets;
    private double _fraction;

    public JobProgress(IProgress<MediaProgress>? progress, MediaTime? duration, IEnumerable<TrackInfo> tracks, CancellationToken cancellation, CountingStream? output = null, CountingStream? input = null)
    {
        _progress = progress;
        _start = TimeProvider.System.GetTimestamp();
        _duration = duration is { Ticks: > 0 } ? duration : null;
        _output = output;
        _input = input;
        _cancellation = cancellation;
        _videoTracks = [.. tracks.Where(t => t.Kind == TrackKind.Video).Select(t => t.Number)];
        if (_progress is not null)
        {
            _next = _start + (TimeProvider.System.TimestampFrequency / 4);
            Report(final: false);
        }
    }

    /// <summary>Gets or sets the seconds added to timestamps, so a job over several inputs (concat) counts on from the last.</summary>
    public double Offset
    {
        get => _offsetSeconds;
        set => _offsetSeconds = value;
    }

    /// <summary>Counts a written packet, reports if 250 ms have passed, and stops the job when it is cancelled.</summary>
    public void Packet(int trackNumber, MediaTime timestamp)
    {
        _cancellation.ThrowIfCancellationRequested();
        _packets++;
        if (_videoTracks.Contains(trackNumber))
        {
            _frames++;
        }

        Advance(timestamp);
    }

    /// <summary>Counts a frame of a frame job (frames, run, encode), reports if 250 ms have passed, and stops it when cancelled.</summary>
    public void Frame(MediaTime timestamp)
    {
        _cancellation.ThrowIfCancellationRequested();
        _frames++;
        _packets++;
        Advance(timestamp);
    }

    /// <summary>Sends the final report.</summary>
    public void Finish()
    {
        if (_progress is not null)
        {
            Report(final: true);
        }
    }

    private void Advance(MediaTime timestamp)
    {
        double seconds = timestamp.Seconds + _offsetSeconds;
        if (seconds > _positionSeconds)
        {
            _positionSeconds = seconds;
            _position = MediaTime.FromSeconds(seconds);
        }

        if (_progress is null)
        {
            return;
        }

        long now = TimeProvider.System.GetTimestamp();
        if (now >= _next)
        {
            _next = now + (TimeProvider.System.TimestampFrequency / 4);
            Report(final: false);
        }
    }

    private void Report(bool final)
    {
        double? fraction = final ? 1 : _duration is { } duration ? _positionSeconds / duration.Seconds : null;
        if (fraction is { } f)
        {
            _fraction = Math.Max(_fraction, Math.Clamp(f, 0, 1));
            fraction = _fraction;
        }

        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(_start);
        _progress!.Report(new MediaProgress
        {
            Position = _position,
            Duration = _duration,
            Fraction = fraction,
            Frames = _frames,
            Packets = _packets,
            BytesRead = _input?.Count ?? 0,
            BytesWritten = _output?.Count ?? 0,
            Elapsed = elapsed,
            Speed = elapsed > TimeSpan.Zero ? _positionSeconds / elapsed.TotalSeconds : null,
            IsFinal = final,
        });
    }
}

/// <summary>A stream that passes everything through and counts the bytes read or written.</summary>
internal sealed class CountingStream(Stream inner) : Stream
{
    public long Count { get; private set; }

    public override bool CanRead => inner.CanRead;

    public override bool CanSeek => inner.CanSeek;

    public override bool CanWrite => inner.CanWrite;

    public override long Length => inner.Length;

    public override long Position { get => inner.Position; set => inner.Position = value; }

    public override void Flush() => inner.Flush();

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        Count += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);

    public override void SetLength(long value) => inner.SetLength(value);

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        inner.Write(buffer);
        Count = Math.Max(Count, inner.CanSeek ? inner.Length : Count + buffer.Length);
    }
}
