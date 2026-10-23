using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// Measures sample levels per channel and over all channels: peak, RMS, DC offset, crest factor, clipped samples, how often the
/// peak was reached, the noise floor, zero crossings and a histogram in 1 dB steps. It reports what ffmpeg's volumedetect and
/// astats report, from any sample format.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
/// </example>
public sealed class AudioLevelMeter : IAudioDetector
{
    /// <summary>The number of 1 dB histogram bins: 0 to -1 dBFS in the first, -143 dBFS and below (and zero) in the last.</summary>
    public const int HistogramBins = 145;

    private const double NoiseWindowSeconds = 0.05;

    private readonly AudioChannels _input = new();
    private Levels[] _levels = [];
    private int _noiseWindow;
    private const float ClipLow = -1;
    private float _clipHigh = 1;
    private bool _complete;

    /// <summary>Creates a meter.</summary>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
    /// </example>
    public AudioLevelMeter()
    {
    }

    /// <summary>Gets how many samples per channel the meter has taken.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Takes the next frame of samples.</summary>
    /// <param name="frame">The frame, in time order, in any sample format.</param>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> was called, or the rate or layout changed.</exception>
    public void Add(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_complete)
        {
            throw new InvalidOperationException("The meter is complete; create a new one for more audio.");
        }

        _input.Read(frame, "level meter");
        if (_levels.Length == 0)
        {
            _levels = new Levels[_input.Count];
            for (int c = 0; c < _levels.Length; c++)
            {
                _levels[c] = new Levels();
            }

            _noiseWindow = Math.Max(1, (int)Math.Round(frame.SampleRate * NoiseWindowSeconds));
            if (!frame.Format.IsFloat())
            {
                // An integer sample clips at its largest and smallest code; read as floats those are 1 - 2^-(bits-1) and -1.
                int bits = frame.ValidBits > 0 ? frame.ValidBits : frame.Format.BitsPerSample();
                _clipHigh = 1f - (1f / (1L << (bits - 1)));
            }
        }

        for (int c = 0; c < _levels.Length; c++)
        {
            _levels[c].Add(_input.Channel(c, frame.SampleCount), _clipHigh, ClipLow, _noiseWindow);
        }

        SampleCount += frame.SampleCount;
    }

    /// <summary>Marks the end of the audio; the meter takes no more frames.</summary>
    /// <param name="endTime">When the audio ends.</param>
    public void Complete(MediaTime endTime) => _complete = true;

    /// <summary>Returns the levels measured so far.</summary>
    /// <returns>The report, per channel and over all channels.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
    /// </example>
    public AudioLevelReport Report()
    {
        ChannelLevels[] channels = [.. _levels.Select(l => l.ToChannelLevels())];
        Levels all = new();
        foreach (Levels level in _levels)
        {
            all.Merge(level);
        }

        return new AudioLevelReport(SampleCount, _input.SampleRate, all.ToChannelLevels(), channels);
    }

    /// <summary>One channel's running sums.</summary>
    private sealed class Levels
    {
        private readonly long[] _histogram = new long[HistogramBins];
        private long _count, _clipped, _peakCount, _crossings;
        private double _sum, _squares;
        private float _min = float.PositiveInfinity, _max = float.NegativeInfinity, _peak;
        private int _sign;
        private float _windowPeak;
        private int _windowCount;
        private float _floor = float.PositiveInfinity;

        public void Add(ReadOnlySpan<float> samples, float clipHigh, float clipLow, int window)
        {
            double sum = 0, squares = 0;
            for (int i = 0; i < samples.Length; i++)
            {
                float x = samples[i];
                sum += x;
                squares += (double)x * x;
                _min = Math.Min(_min, x);
                _max = Math.Max(_max, x);
                float level = Math.Abs(x);
                if (level > _peak)
                {
                    _peak = level;
                    _peakCount = 1;
                }
                else if (level == _peak && level > 0)
                {
                    _peakCount++;
                }

                if (x >= clipHigh || x <= clipLow)
                {
                    _clipped++;
                }

                int sign = x > 0 ? 1 : x < 0 ? -1 : 0;
                if (sign != 0)
                {
                    if (sign == -_sign)
                    {
                        _crossings++;
                    }

                    _sign = sign;
                }

                _histogram[Bin(level)]++;
                _windowPeak = Math.Max(_windowPeak, level);
                if (++_windowCount == window)
                {
                    // A window of digital silence says nothing about the noise floor, so it is left out.
                    if (_windowPeak > 0)
                    {
                        _floor = Math.Min(_floor, _windowPeak);
                    }

                    _windowPeak = 0;
                    _windowCount = 0;
                }
            }

            _sum += sum;
            _squares += squares;
            _count += samples.Length;
        }

        public void Merge(Levels other)
        {
            _count += other._count;
            _clipped += other._clipped;
            _crossings += other._crossings;
            _sum += other._sum;
            _squares += other._squares;
            _min = Math.Min(_min, other._min);
            _max = Math.Max(_max, other._max);
            _floor = Math.Min(_floor, other._floor);
            if (other._peak > _peak)
            {
                _peak = other._peak;
                _peakCount = other._peakCount;
            }
            else if (other._peak == _peak)
            {
                _peakCount += other._peakCount;
            }

            for (int i = 0; i < _histogram.Length; i++)
            {
                _histogram[i] += other._histogram[i];
            }
        }

        public ChannelLevels ToChannelLevels()
        {
            double rms = _count > 0 ? Math.Sqrt(_squares / _count) : 0;
            double peak = AudioChannels.Decibels(_peak);
            double rmsDb = AudioChannels.Decibels(rms);
            return new ChannelLevels(
                peak,
                rmsDb,
                _count > 0 ? _sum / _count : 0,
                rms > 0 ? peak - rmsDb : 0,
                _count > 0 ? _min : 0,
                _count > 0 ? _max : 0,
                _clipped,
                _peakCount,
                float.IsPositiveInfinity(_floor) ? double.NegativeInfinity : AudioChannels.Decibels(_floor),
                _crossings,
                [.. _histogram]);
        }

        private static int Bin(float level) =>
            level > 0 ? Math.Clamp((int)(-20 * MathF.Log10(level)), 0, HistogramBins - 1) : HistogramBins - 1;
    }
}

/// <summary>What <see cref="AudioLevelMeter"/> measured.</summary>
/// <param name="Samples">How many samples per channel were measured.</param>
/// <param name="SampleRate">The sample rate in hertz.</param>
/// <param name="Overall">The levels over all channels together.</param>
/// <param name="Channels">The levels of each channel, in channel order.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
/// </example>
public sealed record AudioLevelReport(long Samples, int SampleRate, ChannelLevels Overall, IReadOnlyList<ChannelLevels> Channels);

/// <summary>The sample levels of one channel, or of all channels together. Levels are in dBFS, where a full-scale sample is 0.</summary>
/// <param name="Peak">The largest sample level in dBFS, as volumedetect's max_volume.</param>
/// <param name="Rms">The RMS level in dBFS, as volumedetect's mean_volume; a full-scale sine reads -3 dBFS.</param>
/// <param name="DcOffset">The mean sample value, from -1 to 1.</param>
/// <param name="CrestFactor">How far the peak stands above the RMS level, in dB.</param>
/// <param name="Min">The lowest sample value, from -1 to 1.</param>
/// <param name="Max">The highest sample value, from -1 to 1.</param>
/// <param name="Clipped">How many samples sit at full scale: the largest or smallest integer code, or 1 and beyond for float samples.</param>
/// <param name="PeakCount">How many samples reached the peak level.</param>
/// <param name="NoiseFloor">The lowest peak level in dBFS of any 50 ms window, leaving out windows of digital silence, as astats's noise floor.</param>
/// <param name="ZeroCrossings">How many times the signal changed sign.</param>
/// <param name="Histogram">
/// How many samples fell in each 1 dB step below full scale: index 0 counts levels from 0 to -1 dBFS, index <c>k</c> from -k to
/// -(k + 1) dBFS, and the last bin -143 dBFS and below, digital silence included. It has <see cref="AudioLevelMeter.HistogramBins"/> bins.
/// </param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
/// </example>
public sealed record ChannelLevels(
    double Peak,
    double Rms,
    double DcOffset,
    double CrestFactor,
    double Min,
    double Max,
    long Clipped,
    long PeakCount,
    double NoiseFloor,
    long ZeroCrossings,
    IReadOnlyList<long> Histogram);
