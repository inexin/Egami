namespace Egami.Video.Audio.Filters;

/// <summary>The shape of a <see cref="AudioFade"/> or <see cref="Crossfade"/>, as the gain of a fade in over its length.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-fades"/>
/// </example>
public enum FadeCurve
{
    /// <summary>A straight line in amplitude, ffmpeg's <c>tri</c>. The default for <see cref="AudioFade"/>.</summary>
    Linear,

    /// <summary>
    /// A quarter sine, ffmpeg's <c>qsin</c>: the two halves of a crossfade keep the power constant, so unrelated material
    /// keeps its loudness through the overlap. The default for <see cref="Crossfade"/>.
    /// </summary>
    EqualPower,

    /// <summary>1 + 0.2 log10(x), ffmpeg's <c>log</c>: rises fast, then slowly; starts at -8 dB one hundredth of the way in.</summary>
    Logarithmic,

    /// <summary>A straight line in decibels from -100 dB to 0 dB, ffmpeg's <c>exp</c>: rises slowly, then fast.</summary>
    Exponential,

    /// <summary>A half cosine, (1 - cos(πx)) / 2, ffmpeg's <c>hsin</c>: starts and ends slowly.</summary>
    SCurve,
}

/// <summary>Whether a <see cref="AudioFade"/> brings the sound in or takes it out.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-fades"/>
/// </example>
public enum FadeDirection
{
    /// <summary>From silence to full level: silent before the start.</summary>
    In,

    /// <summary>From full level to silence: silent after the end.</summary>
    Out,
}

/// <summary>
/// Fades the sound in or out over a stretch of the stream's time line. A fade in is silent before <see cref="Start"/> and
/// reaches full level on the exact sample at <see cref="Start"/> plus <see cref="Duration"/>; a fade out is full level before
/// <see cref="Start"/> and silent from its end on. Times are on the frames' own timestamps, not counted from the first
/// sample, and are rounded to whole samples; put a <see cref="Retime"/> first to count from zero. The curves give the same
/// gains as ffmpeg's <c>afade</c> with the curve each value names. Works in place.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-fades"/>
/// </example>
public sealed class AudioFade : AudioFilter
{
    private long _start;
    private long _length;
    private float[] _gains = [];

    /// <summary>Creates a fade.</summary>
    /// <param name="direction">In or out.</param>
    /// <param name="start">When the fade starts, on the frames' time line.</param>
    /// <param name="duration">How long it lasts; 0 for a cut.</param>
    /// <param name="curve">The shape. Default <see cref="FadeCurve.Linear"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The duration is negative.</exception>
    public AudioFade(FadeDirection direction, MediaTime start, MediaTime duration, FadeCurve curve = FadeCurve.Linear)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(duration, MediaTime.Zero);
        Direction = direction;
        Start = start;
        Duration = duration;
        Curve = curve;
    }

    /// <summary>Gets whether the fade is in or out.</summary>
    public FadeDirection Direction { get; }

    /// <summary>Gets when the fade starts.</summary>
    public MediaTime Start { get; }

    /// <summary>Gets how long the fade lasts.</summary>
    public MediaTime Duration { get; }

    /// <summary>Gets the shape.</summary>
    public FadeCurve Curve { get; }

    /// <summary>Returns the gain of a fade in a fraction <paramref name="x"/> of the way through, from 0 to 1.</summary>
    internal static double CurveGain(FadeCurve curve, double x)
    {
        x = Math.Clamp(x, 0, 1);
        return curve switch
        {
            FadeCurve.EqualPower => Math.Sin(x * Math.PI / 2),
            FadeCurve.Logarithmic => x <= 0 ? 0 : Math.Clamp(1 + (0.2 * Math.Log10(x)), 0, 1),
            FadeCurve.Exponential => Math.Pow(10, -5 * (1 - x)),
            FadeCurve.SCurve => (1 - Math.Cos(x * Math.PI)) / 2,
            _ => x,
        };
    }

    private protected override void OnStart()
    {
        _start = IndexOf(Start);
        _length = IndexOf(Duration);
    }

    private protected override void Process(AudioFrame frame)
    {
        long first = IndexOf(frame.Timestamp);
        int n = frame.SampleCount;
        long end = _start + _length;
        bool fadeIn = Direction == FadeDirection.In;
        if (first >= end || first + n <= _start)
        {
            // Wholly before or after the fade: full level or silence.
            bool silent = fadeIn ? first + n <= _start : first >= end;
            if (silent)
            {
                for (int c = 0; c < frame.ChannelCount; c++)
                {
                    frame.GetSpan<float>(c).Clear();
                }
            }

            Emit(frame);
            return;
        }

        if (_gains.Length < n)
        {
            _gains = new float[n];
        }

        Span<float> gains = _gains.AsSpan(0, n);
        for (int i = 0; i < n; i++)
        {
            long index = first + i;
            if (index < _start)
            {
                gains[i] = fadeIn ? 0 : 1;
            }
            else if (index >= end)
            {
                gains[i] = fadeIn ? 1 : 0;
            }
            else
            {
                double x = (double)(index - _start) / _length;
                gains[i] = (float)CurveGain(Curve, fadeIn ? x : 1 - x);
            }
        }

        for (int c = 0; c < frame.ChannelCount; c++)
        {
            SampleMath.Multiply(frame.GetSpan<float>(c), gains);
        }

        Emit(frame);
    }
}
