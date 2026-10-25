namespace Egami.Video;

/// <summary>How an animated value moves from a <see cref="Keyframe"/> to the next one.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-gain-envelope"/>
/// </example>
public enum Easing
{
    /// <summary>A straight line. The default.</summary>
    Linear,

    /// <summary>Starts slowly and speeds up (quadratic).</summary>
    EaseIn,

    /// <summary>Starts fast and slows down (quadratic).</summary>
    EaseOut,

    /// <summary>Starts and ends slowly (smoothstep, 3t² - 2t³).</summary>
    EaseInOut,

    /// <summary>Keeps the value until the next keyframe, then jumps.</summary>
    Hold,
}

/// <summary>
/// A value at a time on a stream's time line, one point of an animation such as a <see cref="Audio.Filters.GainEnvelope"/>.
/// <see cref="Easing"/> says how the value moves on to the next keyframe.
/// </summary>
/// <param name="Time">The time, on the same time line as the frames' timestamps.</param>
/// <param name="Value">The value at that time.</param>
/// <param name="Easing">How the value moves to the next keyframe.</param>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-gain-envelope"/>
/// </example>
public readonly record struct Keyframe(MediaTime Time, double Value, Easing Easing = Easing.Linear)
{
    /// <summary>Returns how far along a segment an eased value is, for a fraction of its time from 0 to 1.</summary>
    internal static double Ease(Easing easing, double t) => easing switch
    {
        Easing.EaseIn => t * t,
        Easing.EaseOut => 1 - ((1 - t) * (1 - t)),
        Easing.EaseInOut => t * t * (3 - (2 * t)),
        Easing.Hold => 0,
        _ => t,
    };
}
