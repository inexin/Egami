namespace Egami.Video.Audio.Filters;

/// <summary>
/// A gain that changes over time, in decibels, for <see cref="Gain.Envelope"/>: a list of <see cref="Keyframe"/>s whose
/// values are decibels. Before the first keyframe the gain is the first value, after the last one the last value, and between
/// two the value eases from one to the next in decibels. A value of <see cref="double.NegativeInfinity"/> is silence; a
/// segment to or from it moves in linear amplitude instead, so the fade still sounds smooth. Keyframe times are the frames'
/// own timestamps, not counted from the first sample.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-gain-envelope"/>
/// </example>
public sealed class GainEnvelope
{
    private readonly Keyframe[] _keys;
    private readonly double[] _seconds;

    /// <summary>Creates an envelope.</summary>
    /// <param name="keyframes">The keyframes, values in decibels, in any order; at least one.</param>
    /// <exception cref="ArgumentException">There are no keyframes, two have the same time, or a value is NaN or positive infinity.</exception>
    public GainEnvelope(IEnumerable<Keyframe> keyframes)
    {
        ArgumentNullException.ThrowIfNull(keyframes);
        _keys = [.. keyframes.OrderBy(k => k.Time)];
        if (_keys.Length == 0)
        {
            throw new ArgumentException("An envelope needs at least one keyframe.", nameof(keyframes));
        }

        _seconds = new double[_keys.Length];
        for (int i = 0; i < _keys.Length; i++)
        {
            if (double.IsNaN(_keys[i].Value) || double.IsPositiveInfinity(_keys[i].Value))
            {
                throw new ArgumentException($"Keyframe {i} has the value {_keys[i].Value}; use a number of decibels, or negative infinity for silence.", nameof(keyframes));
            }

            if (i > 0 && _keys[i].Time == _keys[i - 1].Time)
            {
                throw new ArgumentException($"Two keyframes are at {_keys[i].Time}.", nameof(keyframes));
            }

            _seconds[i] = _keys[i].Time.Seconds;
        }
    }

    /// <summary>Creates an envelope.</summary>
    /// <param name="keyframes">The keyframes, values in decibels.</param>
    public GainEnvelope(params Keyframe[] keyframes)
        : this((IEnumerable<Keyframe>)keyframes)
    {
    }

    /// <summary>Gets the keyframes, in time order.</summary>
    public IReadOnlyList<Keyframe> Keyframes => _keys;

    /// <summary>Returns the gain at a time in decibels.</summary>
    /// <param name="time">The time.</param>
    /// <returns>The gain in decibels; negative infinity for silence.</returns>
    public double DecibelsAt(MediaTime time) => SampleMath.ToDecibels(FactorAt(time.Seconds));

    /// <summary>Returns the linear gain at a time in seconds.</summary>
    internal double FactorAt(double seconds)
    {
        if (seconds <= _seconds[0])
        {
            return SampleMath.FromDecibels(_keys[0].Value);
        }

        int last = _keys.Length - 1;
        if (seconds >= _seconds[last])
        {
            return SampleMath.FromDecibels(_keys[last].Value);
        }

        int i = Array.BinarySearch(_seconds, seconds);
        if (i >= 0)
        {
            return SampleMath.FromDecibels(_keys[i].Value);
        }

        int k = ~i - 1;
        Keyframe a = _keys[k], b = _keys[k + 1];
        double t = Keyframe.Ease(a.Easing, (seconds - _seconds[k]) / (_seconds[k + 1] - _seconds[k]));
        if (double.IsNegativeInfinity(a.Value) || double.IsNegativeInfinity(b.Value))
        {
            double fa = SampleMath.FromDecibels(a.Value), fb = SampleMath.FromDecibels(b.Value);
            return fa + ((fb - fa) * t);
        }

        return SampleMath.FromDecibels(a.Value + ((b.Value - a.Value) * t));
    }
}
