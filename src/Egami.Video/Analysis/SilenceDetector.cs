using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// Finds silent stretches: runs of samples whose level stays under <see cref="SilenceOptions.Noise"/> for at least
/// <see cref="SilenceOptions.MinDuration"/>. By default a stretch is silent only when every channel is, as ffmpeg's silencedetect
/// does; <see cref="SilenceOptions.PerChannel"/> also finds each channel's own stretches. Times are exact to the sample.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
/// </example>
public sealed class SilenceDetector : IAudioDetector
{
    private readonly SilenceOptions _options;
    private readonly float _threshold;
    private readonly AudioChannels _input = new();
    private readonly Run _all = new();
    private Run[] _channels = [];
    private MediaTime _lastEnd;
    private bool _complete;

    /// <summary>Creates a detector.</summary>
    /// <param name="options">The threshold and length, or <see langword="null"/> for <see cref="SilenceOptions.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
    /// </example>
    public SilenceDetector(SilenceOptions? options = null)
    {
        _options = options ?? SilenceOptions.Default;
        _options.Validate();
        _threshold = (float)Math.Pow(10, _options.Noise / 20);
    }

    /// <summary>Gets the options the detector runs with.</summary>
    public SilenceOptions Options => _options;

    /// <summary>
    /// Gets the stretches where every channel was silent, found so far; each range's value is the highest sample level in it, in
    /// dBFS (negative infinity for digital silence).
    /// </summary>
    public IReadOnlyList<DetectedRange> Ranges => _all.Ranges;

    /// <summary>
    /// Gets each channel's own silent stretches, in channel order, when <see cref="SilenceOptions.PerChannel"/> is set; otherwise
    /// empty.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<DetectedRange>> ChannelRanges => [.. _channels.Select(r => r.Ranges)];

    /// <summary>Gets how many samples per channel the detector has taken.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Takes the next frame of samples.</summary>
    /// <param name="frame">The frame, in time order, in any sample format.</param>
    /// <exception cref="InvalidOperationException"><see cref="Complete"/> was called, or the rate or layout changed.</exception>
    public void Add(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (_complete)
        {
            throw new InvalidOperationException("The detector is complete; create a new one for more audio.");
        }

        _input.Read(frame, "silence detector");
        int count = frame.SampleCount;
        if (_options.PerChannel && _channels.Length == 0)
        {
            _channels = new Run[_input.Count];
            for (int c = 0; c < _channels.Length; c++)
            {
                _channels[c] = new Run();
            }
        }

        for (int i = 0; i < count; i++)
        {
            float loudest = 0;
            for (int c = 0; c < _input.Count; c++)
            {
                float level = Math.Abs(_input.Channel(c, count)[i]);
                loudest = Math.Max(loudest, level);
                if (_channels.Length > 0)
                {
                    _channels[c].Step(level < _threshold, level, frame, i, _options.MinDuration);
                }
            }

            _all.Step(loudest < _threshold, loudest, frame, i, _options.MinDuration);
        }

        SampleCount += count;
        _lastEnd = frame.Timestamp + frame.Duration;
    }

    /// <summary>Ends a silent stretch that runs to the end of the audio.</summary>
    /// <param name="endTime">When the audio ends.</param>
    public void Complete(MediaTime endTime)
    {
        if (_complete)
        {
            return;
        }

        _complete = true;
        MediaTime end = endTime > _lastEnd ? endTime : _lastEnd;
        _all.Close(end, _options.MinDuration);
        foreach (Run run in _channels)
        {
            run.Close(end, _options.MinDuration);
        }
    }

    /// <summary>One channel's (or all channels') open stretch and the stretches found.</summary>
    private sealed class Run
    {
        private readonly List<DetectedRange> _ranges = [];
        private bool _open;
        private MediaTime _start;
        private float _peak;

        public IReadOnlyList<DetectedRange> Ranges => _ranges;

        public void Step(bool quiet, float level, AudioFrame frame, int sample, MediaTime minDuration)
        {
            if (quiet)
            {
                if (!_open)
                {
                    _open = true;
                    _start = AudioChannels.TimeOf(frame, sample);
                    _peak = 0;
                }

                _peak = Math.Max(_peak, level);
            }
            else if (_open)
            {
                Close(AudioChannels.TimeOf(frame, sample), minDuration);
            }
        }

        public void Close(MediaTime end, MediaTime minDuration)
        {
            if (!_open)
            {
                return;
            }

            _open = false;
            if (end - _start >= minDuration)
            {
                _ranges.Add(new DetectedRange(_start, end, AudioChannels.Decibels(_peak)));
            }
        }
    }
}

/// <summary>What <see cref="SilenceDetector"/> counts as silence. The defaults are ffmpeg silencedetect's.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-audio-detectors"/>
/// </example>
public sealed record SilenceOptions
{
    /// <summary>Gets the default options.</summary>
    public static SilenceOptions Default { get; } = new();

    /// <summary>Gets the level in dBFS a sample must stay under to count as silent. Default -60 dBFS, as silencedetect.</summary>
    public double Noise { get; init; } = -60;

    /// <summary>Gets the shortest silent stretch reported. Default 2 s, as silencedetect.</summary>
    public MediaTime MinDuration { get; init; } = MediaTime.FromSeconds(2);

    /// <summary>Gets whether <see cref="SilenceDetector.ChannelRanges"/> also finds each channel's own stretches. Off by default.</summary>
    public bool PerChannel { get; init; }

    internal void Validate()
    {
        if (Noise > 0 || double.IsNaN(Noise))
        {
            throw new ArgumentOutOfRangeException(nameof(Noise), Noise, "The noise level must be 0 dBFS or below.");
        }

        if (MinDuration.Ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinDuration), MinDuration, "The minimum duration cannot be negative.");
        }
    }
}
