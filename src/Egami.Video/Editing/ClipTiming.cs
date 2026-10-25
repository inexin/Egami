namespace Egami.Video.Editing;

/// <summary>
/// How a clip's own time maps to its source: speed, then reverse, then loops, then freezes, then the hold at the end. Times
/// are seconds from the clip's start (output side) and from its trimmed source's start (source side).
/// </summary>
internal sealed class ClipTiming
{
    private readonly double _source;
    private readonly double _speed;
    private readonly bool _reversed;
    private readonly double _once;
    private readonly double _looped;
    private readonly (double At, double Length)[] _freezes;
    private readonly double _frozen;

    public ClipTiming(Clip clip, SourceInfo info)
    {
        _source = Math.Max(0, Times.Sub(info.Out, info.In).Seconds);
        _speed = clip.SpeedFactor;
        _reversed = clip.IsReversed;
        _once = _source / _speed;
        _looped = _once * clip.LoopCount;
        _freezes = [.. clip.Freezes.Select(f => (Math.Min(f.At.Seconds, _looped), f.Duration.Seconds))];
        _frozen = _looped + _freezes.Sum(f => f.Length);
        Length = _frozen + clip.HoldDuration.Seconds;
    }

    /// <summary>Gets the clip's length in seconds.</summary>
    public double Length { get; }

    /// <summary>Gets whether the source is read backwards.</summary>
    public bool Reversed => _reversed;

    /// <summary>Gets whether the clip's time runs straight through its source once, at normal speed.</summary>
    public bool IsPlain => _speed == 1 && !_reversed && _looped == _once && _freezes.Length == 0 && Length == _once;

    /// <summary>Returns the source time, from the trimmed source's start, shown at a time from the clip's start.</summary>
    public double SourceAt(double time)
    {
        double x = Math.Clamp(time, 0, _frozen);
        if (x >= _frozen)
        {
            // The hold, and the clip's very end: the last moment of the source.
            x = Math.Max(0, _looped - 1e-6);
        }
        else
        {
            foreach ((double at, double length) in _freezes)
            {
                if (x < at)
                {
                    break;
                }

                if (x < at + length)
                {
                    // Just after the freeze time, so the frame that starts there is the one held.
                    x = at + 1e-6;
                    break;
                }

                x -= length;
            }
        }

        if (_once > 0 && x >= _once)
        {
            x %= _once;
        }

        double s = Math.Min(x * _speed, _source);
        return _reversed ? Math.Max(0, _source - s) : s;
    }

    /// <summary>
    /// The clip's sound as pieces of the source in order: where each piece is in the source, where it plays in the clip, and
    /// whether it plays backwards. Freezes and the hold are silent.
    /// </summary>
    public IEnumerable<(double SourceStart, double SourceEnd, double ClipStart)> Pieces()
    {
        // Spans of the clip's time before freezes and holds: the loops.
        List<(double Start, double End)> loops = [];
        for (double start = 0; start < _looped - 1e-9; start += _once)
        {
            loops.Add((start, Math.Min(start + _once, _looped)));
        }

        foreach ((double start, double end) in loops)
        {
            // Cut the loop at freezes; each part keeps playing after the freeze, shifted by the freezes before it.
            double from = start;
            List<double> cuts = [.. _freezes.Select(f => f.At).Where(a => a > start && a < end), end];
            foreach (double cut in cuts)
            {
                if (cut > from)
                {
                    double shift = _freezes.Where(f => f.At < cut && f.At <= from).Sum(f => f.Length);
                    double a = (from - start) * _speed, b = (cut - start) * _speed;
                    yield return _reversed ? (_source - b, _source - a, from + shift) : (a, b, from + shift);
                }

                from = cut;
            }
        }
    }
}
