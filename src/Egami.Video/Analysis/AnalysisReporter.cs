namespace Egami.Video.Analysis;

/// <summary>Reports the progress of a pass over a file at most every 250 ms, as the other jobs do, plus a final report.</summary>
internal sealed class AnalysisReporter(IProgress<MediaProgress> progress, MediaTime? duration, MediaTime start)
{
    private readonly long _interval = TimeProvider.System.TimestampFrequency / 4;
    private readonly long _started = TimeProvider.System.GetTimestamp();
    private long _next;
    private readonly MediaTime _start = start;
    private MediaTime _position = start;
    private double _fraction;

    public void Advance(MediaTime position, long frames)
    {
        if (position > _position)
        {
            _position = position;
        }

        long now = TimeProvider.System.GetTimestamp();
        if (now >= _next)
        {
            _next = now + _interval;
            Report(now, frames, final: false);
        }
    }

    public void Finish(MediaTime end, long frames)
    {
        _position = end;
        Report(TimeProvider.System.GetTimestamp(), frames, final: true);
    }

    private void Report(long now, long frames, bool final)
    {
        double done = (_position - _start).Seconds;
        double? fraction = final ? 1 : duration is { Ticks: > 0 } d ? Math.Clamp(done / d.Seconds, 0, 1) : null;
        if (fraction is { } f)
        {
            _fraction = Math.Max(_fraction, f);
            fraction = _fraction;
        }

        TimeSpan elapsed = TimeProvider.System.GetElapsedTime(_started, now);
        progress.Report(new MediaProgress
        {
            Position = _position,
            Duration = duration,
            Fraction = fraction,
            Frames = frames,
            Elapsed = elapsed,
            Speed = elapsed > TimeSpan.Zero ? done / elapsed.TotalSeconds : null,
            IsFinal = final,
        });
    }
}
