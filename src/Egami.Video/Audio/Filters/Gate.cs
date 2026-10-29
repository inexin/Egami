namespace Egami.Video.Audio.Filters;

/// <summary>
/// Turns the level down below a threshold, like ffmpeg's <c>agate</c>, to keep hiss and room noise out of the pauses: a level
/// 1 dB under the threshold comes out <see cref="Ratio"/> dB under it, but never more than <see cref="Range"/> down.
/// <see cref="Attack"/> sets how fast the gate opens when the level rises and <see cref="Release"/> how fast it closes. Every
/// channel gets the same gain, from the loudest one.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-dynamics"/>
/// </example>
public sealed class Gate : AudioFilter
{
    private DynamicsCore? _core;
    private float[] _detector = [];
    private float[] _gains = [];

    /// <summary>Gets the threshold in dBFS below which the level is turned down; -45 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -120 to 0.</exception>
    public double Threshold { get; init => field = DynamicsCore.CheckDecibels(value, -120, 0, nameof(Threshold)); } = -45;

    /// <summary>Gets how many decibels the output falls for each decibel the level is under the threshold, 1 to 100; 10 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 1 to 100.</exception>
    public double Ratio
    {
        get;
        init
        {
            if (!(value is >= 1 and <= 100))
            {
                throw new ArgumentOutOfRangeException(nameof(Ratio), value, "The ratio must be from 1 to 100.");
            }

            field = value;
        }
    } = 10;

    /// <summary>Gets the most the gate turns the level down, in decibels, -120 to 0; -80 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside -120 to 0.</exception>
    public double Range { get; init => field = DynamicsCore.CheckDecibels(value, -120, 0, nameof(Range)); } = -80;

    /// <summary>Gets the width in decibels of the soft knee around the threshold, 0 (hard) to 24; 6 by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 24.</exception>
    public double Knee { get; init => field = DynamicsCore.CheckDecibels(value, 0, 24, nameof(Knee)); } = 6;

    /// <summary>Gets how fast the gate opens when the level rises, 0 to 10 s; 5 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10 seconds.</exception>
    public MediaTime Attack { get; init => field = DynamicsCore.CheckTime(value, nameof(Attack)); } = MediaTime.FromSeconds(0.005);

    /// <summary>Gets how fast the gate closes when the level falls, 0 to 10 s; 100 ms by default.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is outside 0 to 10 seconds.</exception>
    public MediaTime Release { get; init => field = DynamicsCore.CheckTime(value, nameof(Release)); } = MediaTime.FromSeconds(0.100);

    /// <summary>Gets how the level is measured; <see cref="LevelDetection.Rms"/> by default.</summary>
    public LevelDetection Detection { get; init; } = LevelDetection.Rms;

    private protected override void OnStart()
    {
        _core = new DynamicsCore(true, Threshold, Ratio, Knee, Range, 0, Detection);
        _core.Start(SampleRate, Attack, Release);
    }

    private protected override void OnReset() => _core?.Reset();

    private protected override void Process(AudioFrame frame)
    {
        int n = frame.SampleCount;
        if (_detector.Length < n)
        {
            _detector = new float[n];
            _gains = new float[n];
        }

        Span<float> detector = _detector.AsSpan(0, n), gains = _gains.AsSpan(0, n);
        DynamicsCore.Peaks(frame, detector);
        _core!.Run(detector, gains);
        for (int c = 0; c < frame.ChannelCount; c++)
        {
            SampleMath.Multiply(frame.GetSpan<float>(c), gains);
        }

        Emit(frame);
    }
}
