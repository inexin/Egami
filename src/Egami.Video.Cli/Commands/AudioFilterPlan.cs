using System.Diagnostics.CodeAnalysis;
using System.Globalization;

using Egami.Video.Audio;
using Egami.Video.Audio.Filters;

namespace Egami.Video.Cli.Commands;

/// <summary>
/// The audio filters of a command line (V9.1 Design Q14): <c>--gain</c>, <c>--fade-in</c>, <c>--fade-out</c>,
/// <c>--tempo</c>, <c>--trim-silence</c>, <c>--normalize</c> and <c>--af</c>, which takes ffmpeg's <c>-af</c> syntax for the
/// filters Egami.Video has. ffmpeg mode's <c>-af</c> and <c>run</c>'s audio steps parse with the same code. A plan builds a
/// fresh chain for each track; a linear <c>loudnorm</c> or <c>--normalize</c> needs a first pass to measure the track with
/// the filters before it.
/// </summary>
internal sealed class AudioFilterPlan
{
    /// <summary>The ffmpeg filters <c>--af</c> and ffmpeg mode's <c>-af</c> take, for help text.</summary>
    public const string NameList =
        "volume, afade, atempo, adelay, apad, atrim, asetpts, highpass, lowpass, bandpass, bandreject, allpass, equalizer, bass, "
        + "lowshelf, treble, highshelf, acompressor, agate, alimiter, loudnorm, silenceremove";

    /// <summary>The same names, one by one.</summary>
    public static readonly string[] Names = NameList.Split(", ");

    private readonly List<Step> _steps = [];

    /// <summary>Gets whether there is nothing to do.</summary>
    public bool IsEmpty => _steps.Count == 0;

    /// <summary>Gets whether a filter changes the length, so the sound no longer lines up with video.</summary>
    public bool ChangesLength => _steps.Any(s => s.ChangesLength);

    /// <summary>Gets whether a filter cuts silence out, which a track that goes with video refuses.</summary>
    public bool RemovesSilence => _steps.Any(s => s.RemovesSilence);

    /// <summary>Gets whether a linear loudness normalization needs the measuring pass.</summary>
    public bool Measures => _steps.Any(s => s is LoudnessStep { Dynamic: false });

    /// <summary>Gets the steps' names, for messages.</summary>
    public string Describe() => string.Join(", ", _steps.Select(s => s.Name));

    /// <summary>Parses an ffmpeg <c>-af</c> chain and adds its filters.</summary>
    /// <param name="chain">The chain, such as <c>highpass=f=80,volume=-3dB</c>.</param>
    /// <param name="flag">The option it came from, for messages.</param>
    public AudioFilterPlan AddChain(string chain, string flag = "--af")
    {
        foreach (string text in FfmpegTranslator.SplitChain(chain))
        {
            string trimmed = text.Trim();
            if (trimmed.Length == 0)
            {
                continue;
            }

            int equals = trimmed.IndexOf('=', StringComparison.Ordinal);
            string name = (equals < 0 ? trimmed : trimmed[..equals]).Trim();
            Args args = new(name, equals < 0 ? string.Empty : trimmed[(equals + 1)..], flag);
            Add(Parse(name, args, flag));
        }

        return this;
    }

    /// <summary>Adds <c>--gain</c>: decibels, such as -3 or -3dB.</summary>
    public AudioFilterPlan AddGain(string text)
    {
        string number = text.EndsWith("dB", StringComparison.OrdinalIgnoreCase) ? text[..^2] : text;
        double decibels = Number(number, "--gain");
        return Add(new FilterStep("--gain", _ => new Gain(decibels)));
    }

    /// <summary>Adds <c>--fade-in</c> over a duration from the sound's first sample.</summary>
    public AudioFilterPlan AddFadeIn(string text)
    {
        MediaTime duration = Duration(text, "--fade-in");
        return Add(new FilterStep("--fade-in", _ => new LeadFade(duration)));
    }

    /// <summary>Adds <c>--fade-out</c> over a duration up to the sound's last sample.</summary>
    public AudioFilterPlan AddFadeOut(string text)
    {
        MediaTime duration = Duration(text, "--fade-out");
        return Add(new FilterStep("--fade-out", _ => new TailFade(duration)));
    }

    /// <summary>Adds <c>--tempo</c>: a factor from 0.25 to 4, the pitch kept.</summary>
    public AudioFilterPlan AddTempo(string text)
    {
        double factor = Number(text, "--tempo");
        if (factor is < 0.25 or > 4)
        {
            throw new UsageException($"--tempo {text} is not 0.25 to 4.");
        }

        return Add(new FilterStep("--tempo", _ => new Tempo(factor)) { ChangesLength = factor != 1 });
    }

    /// <summary>Adds <c>--trim-silence</c>: cut the silence at the start and the end.</summary>
    public AudioFilterPlan AddTrimSilence() =>
        Add(new FilterStep("--trim-silence", _ => new SilenceRemover()) { ChangesLength = true, RemovesSilence = true });

    /// <summary>Adds <c>--normalize</c>: ebu, atsc, streaming, podcast or an integrated loudness in LUFS.</summary>
    public AudioFilterPlan AddNormalize(string text)
    {
        LoudnessTarget target = text.ToLowerInvariant() switch
        {
            "ebu" or "r128" or "ebur128" => LoudnessTarget.EbuR128,
            "atsc" or "a85" => LoudnessTarget.AtscA85,
            "streaming" => LoudnessTarget.Streaming,
            "podcast" => LoudnessTarget.Podcast,
            _ => double.TryParse(text.EndsWith("LUFS", StringComparison.OrdinalIgnoreCase) ? text[..^4] : text, NumberStyles.Float, CultureInfo.InvariantCulture, out double lufs) && lufs is >= -70 and <= -5
                ? new LoudnessTarget(lufs, -1)
                : throw new UsageException($"--normalize {text} is not ebu, atsc, streaming, podcast or a loudness from -70 to -5 LUFS."),
        };
        return Add(new LoudnessStep("--normalize", target, false));
    }

    /// <summary>Builds the whole chain for a track.</summary>
    /// <param name="rate">The rate the frames come at.</param>
    /// <param name="layout">The channels the frames have.</param>
    /// <param name="measured">The measuring pass's report for a linear normalization, or <see langword="null"/> to normalize in one pass.</param>
    /// <param name="context">The context frames come from.</param>
    public IAudioFilter Build(int rate, ChannelLayout layout, LoudnessReport? measured, MediaContext context)
    {
        AudioFilterChain chain = new(rate, layout, context);
        foreach (Step step in _steps)
        {
            chain.Add(step switch
            {
                FilterStep filter => Make(filter, rate),
                LoudnessStep loud => measured is not null && !loud.Dynamic ? new LoudnessNormalizer(loud.Target, measured) : LoudnessNormalizer.Dynamic(loud.Target),
                _ => throw new InvalidOperationException(),
            });
        }

        return chain;
    }

    /// <summary>Builds the filters before the first loudness normalization, for the measuring pass.</summary>
    public IAudioFilter BuildMeasuring(int rate, ChannelLayout layout, MediaContext context)
    {
        AudioFilterChain chain = new(rate, layout, context);
        foreach (Step step in _steps.TakeWhile(s => s is not LoudnessStep))
        {
            chain.Add(Make((FilterStep)step, rate));
        }

        return chain;
    }

    /// <summary>Measures a track's loudness after the filters before the normalization, for its linear gain.</summary>
    public LoudnessReport Measure(IEnumerable<AudioFrame> frames, int rate, ChannelLayout layout, MediaContext context, CancellationToken cancellationToken)
    {
        LoudnessMeter meter = new();
        using IAudioFilter before = BuildMeasuring(rate, layout, context);
        void Take()
        {
            while (before.TryReceive(out AudioFrame? filtered))
            {
                using (filtered)
                {
                    meter.Add(filtered);
                }
            }
        }

        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                cancellationToken.ThrowIfCancellationRequested();
                before.Send(frame);
            }

            Take();
        }

        before.Flush();
        Take();
        meter.Complete(meter.Duration);
        return meter.Report();
    }

    private static IAudioFilter Make(FilterStep step, int rate)
    {
        try
        {
            return step.Make(rate);
        }
        catch (ArgumentException error)
        {
            throw new UsageException($"{step.Name}: {error.Message.Split(" (Parameter")[0]}");
        }
    }

    private AudioFilterPlan Add(Step step)
    {
        if (step is LoudnessStep && _steps.Any(s => s is LoudnessStep))
        {
            throw new UsageException("Normalize the loudness once: give --normalize or one loudnorm.");
        }

        _steps.Add(step);
        return this;
    }

    private static Step Parse(string name, Args a, string flag)
    {
        string where = $"{flag} {name}";
        switch (name)
        {
            case "volume":
                {
                    string level = a.Take("volume", "1");
                    double decibels = level.EndsWith("dB", StringComparison.OrdinalIgnoreCase)
                        ? Number(level[..^2], where)
                        : SampleDb(Number(level, where));
                    a.Done();
                    return new FilterStep(name, _ => new Gain(decibels));
                }

            case "afade":
                {
                    string type = a.Take("type", "in", "t");
                    FadeDirection direction = type is "in" ? FadeDirection.In : type is "out" ? FadeDirection.Out : throw a.Bad("type", type, "in or out");
                    MediaTime start = Duration(a.Take("start_time", "0", "st"), where);
                    MediaTime duration = Duration(a.Take("duration", "1", "d"), where);
                    string curve = a.Take("curve", "tri", "c");
                    FadeCurve shape = curve switch
                    {
                        "tri" => FadeCurve.Linear,
                        "qsin" => FadeCurve.EqualPower,
                        "log" => FadeCurve.Logarithmic,
                        "exp" => FadeCurve.Exponential,
                        "hsin" => FadeCurve.SCurve,
                        _ => throw a.Bad("curve", curve, "tri, qsin, log, exp or hsin"),
                    };
                    a.Refuse("start_sample", "ss", "nb_samples", "ns");
                    a.Done();
                    return new FilterStep(name, _ => new AudioFade(direction, start, duration, shape));
                }

            case "atempo":
                {
                    double tempo = Number(a.Take("tempo", "1"), where);
                    if (tempo is < 0.25 or > 4)
                    {
                        throw new UsageException($"{where}: egami-video's tempo goes from 0.25 to 4 in one filter, not {tempo.ToString(CultureInfo.InvariantCulture)}.");
                    }

                    a.Done();
                    return new FilterStep(name, _ => new Tempo(tempo)) { ChangesLength = tempo != 1 };
                }

            case "adelay":
                {
                    string[] delays = a.Take("delays", "0").Split('|');
                    bool all = a.Take("all", "0") is "1" or "true";
                    a.Done();
                    return new FilterStep(name, rate =>
                    {
                        MediaTime[] times = [.. delays.Select(d => DelayTime(d.Trim(), rate, where))];
                        return all ? new Delay(times[0]) : new Delay(times);
                    })
                    { ChangesLength = true };
                }

            case "apad":
                {
                    string? padLength = a.TakeOptional("pad_len"), wholeLength = a.TakeOptional("whole_len");
                    string? padDuration = a.TakeOptional("pad_dur"), wholeDuration = a.TakeOptional("whole_dur");
                    a.Done();
                    return new FilterStep(name, rate =>
                        wholeDuration is not null ? Pad.ToLength(Duration(wholeDuration, where))
                        : wholeLength is not null ? Pad.ToLength(new MediaTime((long)Number(wholeLength, where), new Rational(1, rate)))
                        : padDuration is not null ? new Pad(Duration(padDuration, where))
                        : padLength is not null ? new Pad(new MediaTime((long)Number(padLength, where), new Rational(1, rate)))
                        : throw new UsageException($"{where}: egami-video pads by pad_dur, pad_len, whole_dur or whole_len; endless padding has no end."))
                    { ChangesLength = true };
                }

            case "atrim":
                {
                    MediaTime start = Duration(a.Take("start", "0", "starti"), where);
                    string? end = a.TakeOptional("end", "endi"), duration = a.TakeOptional("duration", "durationi");
                    a.Refuse("start_sample", "end_sample", "start_pts", "end_pts");
                    a.Done();
                    MediaTime? stop = end is not null ? Duration(end, where) : duration is not null ? start + Duration(duration, where) : null;
                    return new FilterStep(name, _ => new Trim(start, stop)) { ChangesLength = true };
                }

            case "asetpts":
                {
                    string expression = a.Take("expr", "PTS");
                    a.Done();
                    return expression.Replace(" ", string.Empty, StringComparison.Ordinal) == "PTS-STARTPTS"
                        ? new FilterStep(name, _ => new Retime())
                        : throw new UsageException($"{where}={expression}: egami-video takes only PTS-STARTPTS, a time line from zero.");
                }

            case "highpass" or "lowpass" or "bandpass" or "bandreject" or "allpass" or "equalizer" or "bass" or "lowshelf" or "treble" or "highshelf":
                return Equalizer(name, a, where);

            case "acompressor" or "agate":
                {
                    a.Refuse("level_in", "level_sc", "mix", "mode");
                    double threshold = Level(a.Take("threshold", "0.125"), where);
                    double ratio = Number(a.Take("ratio", "2"), where);
                    MediaTime attack = MediaTime.FromSeconds(Number(a.Take("attack", "20"), where) / 1000);
                    MediaTime release = MediaTime.FromSeconds(Number(a.Take("release", "250"), where) / 1000);
                    double makeup = Level(a.Take("makeup", "1"), where);
                    double knee = Math.Min(24, Level(a.Take("knee", "2.82843"), where));
                    string detection = a.Take("detection", "rms");
                    LevelDetection detect = detection == "peak" ? LevelDetection.Peak : detection == "rms" ? LevelDetection.Rms : throw a.Bad("detection", detection, "peak or rms");
                    a.Take("link", "average");
                    double range = name == "agate" ? Level(a.Take("range", "0.06125"), where) : 0;
                    a.Done();
                    return name == "acompressor"
                        ? new FilterStep(name, _ => new Compressor { Threshold = threshold, Ratio = ratio, Attack = attack, Release = release, Makeup = makeup, Knee = knee, Detection = detect })
                        : new FilterStep(name, _ => new Gate { Threshold = threshold, Ratio = ratio, Attack = attack, Release = release, Knee = knee, Detection = detect, Range = range });
                }

            case "alimiter":
                {
                    a.Refuse("level_in", "level_out", "asc", "asc_level");
                    double limit = Level(a.Take("limit", "1"), where);
                    string attack = a.Take("attack", "5");
                    if (Number(attack, where) != 5)
                    {
                        throw new UsageException($"{where}: egami-video's limiter looks 5 ms ahead; drop attack={attack}.");
                    }

                    MediaTime release = MediaTime.FromSeconds(Number(a.Take("release", "50"), where) / 1000);
                    a.Take("level", "1");
                    a.Done();
                    return new FilterStep(name, _ => new Limiter { Ceiling = limit, Release = release });
                }

            case "loudnorm":
                {
                    double integrated = Number(a.Take("I", "-24", "i"), where);
                    double truePeak = Number(a.Take("TP", "-2", "tp"), where);
                    double range = Number(a.Take("LRA", "7", "lra"), where);
                    bool linear = a.Take("linear", "true") is "true" or "1";
                    a.Refuse("measured_I", "measured_i", "measured_TP", "measured_tp", "measured_LRA", "measured_lra", "measured_thresh", "offset");
                    a.TakeOptional("print_format");
                    a.TakeOptional("dual_mono");
                    a.Done();
                    LoudnessTarget target;
                    try
                    {
                        target = new LoudnessTarget(integrated, truePeak, Math.Max(1, range));
                    }
                    catch (ArgumentOutOfRangeException)
                    {
                        throw new UsageException($"{where}: I must be -70 to -5, TP -9 to 0 and LRA 1 to 50.");
                    }

                    return new LoudnessStep(name, target, !linear);
                }

            case "silenceremove":
                {
                    int startPeriods = (int)Number(a.Take("start_periods", "0"), where);
                    int stopPeriods = (int)Number(a.Take("stop_periods", "0"), where);
                    double threshold = Level(a.Take("start_threshold", "0"), where);
                    string? stopThreshold = a.TakeOptional("stop_threshold");
                    MediaTime gap = Duration(a.Take("stop_duration", "1"), where);
                    a.Take("detection", "rms");
                    a.TakeOptional("start_duration");
                    a.TakeOptional("start_silence");
                    a.TakeOptional("stop_silence");
                    a.TakeOptional("window");
                    a.Done();
                    if (stopThreshold is not null)
                    {
                        threshold = Level(stopThreshold, where);
                    }

                    SilenceRemoval? mode = (startPeriods, stopPeriods) switch
                    {
                        (1, 1) => SilenceRemoval.Trim,
                        (1, -1) => SilenceRemoval.Shorten,
                        _ => null,
                    };
                    if (mode is not { } chosen)
                    {
                        throw new UsageException($"{where}: egami-video cuts the silence at both ends (start_periods=1:stop_periods=1) or shortens every pause too (start_periods=1:stop_periods=-1).");
                    }

                    double level = threshold == double.NegativeInfinity ? -50 : Math.Max(-120, threshold);
                    MediaTime maxGap = gap < MediaTime.FromSeconds(0.3) ? MediaTime.FromSeconds(0.3) : gap;
                    return new FilterStep(name, _ => new SilenceRemover(chosen) { Threshold = level, MaxGap = maxGap }) { ChangesLength = true, RemovesSilence = true };
                }

            default:
                throw new UsageException($"{where}: egami-video has no audio filter '{name}'; {flag} takes {string.Join(", ", Names)}.");
        }
    }

    private static FilterStep Equalizer(string name, Args a, string where)
    {
        bool shelf = name is "bass" or "lowshelf" or "treble" or "highshelf";
        BiquadKind kind = name switch
        {
            "highpass" => BiquadKind.HighPass,
            "lowpass" => BiquadKind.LowPass,
            "bandpass" => BiquadKind.BandPass,
            "bandreject" => BiquadKind.Notch,
            "allpass" => BiquadKind.AllPass,
            "equalizer" => BiquadKind.Peaking,
            "bass" or "lowshelf" => BiquadKind.LowShelf,
            _ => BiquadKind.HighShelf,
        };
        double frequency = Number(a.Take("frequency", name switch { "highpass" => "3000", "lowpass" => "500", "bass" or "lowshelf" => "100", "treble" or "highshelf" => "3000", _ => "0" }, "f"), where);
        if (frequency <= 0)
        {
            throw new UsageException($"{where}: give f, the frequency in Hz.");
        }

        string type = a.Take("width_type", "q", "t");
        double width = Number(a.Take("width", shelf ? "0.5" : name == "equalizer" || name == "allpass" ? "1" : "0.707", "w"), where);
        double gain = kind is BiquadKind.Peaking or BiquadKind.LowShelf or BiquadKind.HighShelf ? Number(a.Take("gain", "0", "g"), where) : 0;
        string poles = a.Take("poles", "2", "p");
        if (poles != "2")
        {
            throw new UsageException($"{where}: egami-video's {name} has two poles; for a steeper one use Butterworth (in the library).");
        }

        if (name == "bandpass" && a.Take("csg", "0") is not ("0" or "false"))
        {
            throw new UsageException($"{where}: egami-video's band pass has 0 dB at its peak (csg=0).");
        }

        a.Take("mix", "1");
        a.Take("channels", "all", "c");
        a.Refuse("normalize", "n", "transform", "a", "precision", "r", "blocksize", "b");
        a.Done();

        // ffmpeg's shelves take a Q 1/sqrt(2) times the Audio EQ Cookbook's: its w=0.5 is the cookbook's 0.7071.
        switch (type)
        {
            case "q":
                {
                    double q = shelf ? width * Math.Sqrt(2) : width;
                    return new FilterStep(name, _ => new Biquad(kind, frequency, q, gain));
                }

            case "o" when !shelf:
                return new FilterStep(name, _ => Biquad.FromBandwidth(kind, frequency, width, gain));

            case "h" or "k" when kind is BiquadKind.BandPass or BiquadKind.Notch or BiquadKind.Peaking or BiquadKind.AllPass:
                {
                    double hertz = type == "k" ? width * 1000 : width;
                    return new FilterStep(name, _ => new Biquad(kind, frequency, frequency / hertz, gain));
                }

            default:
                throw new UsageException($"{where}: egami-video takes width_type q{(shelf ? string.Empty : ", o")}{(kind is BiquadKind.BandPass or BiquadKind.Notch or BiquadKind.Peaking or BiquadKind.AllPass ? ", h or k" : string.Empty)} here, not {type}.");
        }
    }

    /// <summary>Parses a number with the invariant culture.</summary>
    internal static double Number(string text, string where) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) && double.IsFinite(value)
            ? value
            : throw new UsageException($"{where}: '{text}' is not a number.");

    /// <summary>Parses an ffmpeg duration: seconds (2.5), with a unit (500ms, 200000us) or [-][HH:]MM:SS[.m...].</summary>
    internal static MediaTime Duration(string text, string where)
    {
        string t = text.Trim();
        double seconds;
        if (t.Contains(':', StringComparison.Ordinal))
        {
            seconds = 0;
            foreach (string part in t.Split(':'))
            {
                seconds = (seconds * 60) + Number(part, where);
            }
        }
        else if (t.EndsWith("ms", StringComparison.Ordinal))
        {
            seconds = Number(t[..^2], where) / 1000;
        }
        else if (t.EndsWith("us", StringComparison.Ordinal))
        {
            seconds = Number(t[..^2], where) / 1e6;
        }
        else
        {
            seconds = Number(t.TrimEnd('s'), where);
        }

        return seconds >= 0 ? MediaTime.FromSeconds(seconds) : throw new UsageException($"{where}: '{text}' is a negative time.");
    }

    /// <summary>An <c>adelay</c> delay: milliseconds, or samples with S, or seconds with s.</summary>
    private static MediaTime DelayTime(string text, int rate, string where) =>
        text.EndsWith('S') ? new MediaTime((long)Number(text[..^1], where), new Rational(1, rate))
        : text.EndsWith('s') ? MediaTime.FromSeconds(Number(text[..^1], where))
        : MediaTime.FromSeconds(Number(text, where) / 1000);

    /// <summary>A level: decibels with dB, else a linear amplitude.</summary>
    private static double Level(string text, string where) =>
        text.EndsWith("dB", StringComparison.OrdinalIgnoreCase) ? Number(text[..^2], where) : SampleDb(Number(text, where));

    private static double SampleDb(double factor) => factor <= 0 ? double.NegativeInfinity : 20 * Math.Log10(factor);

    /// <summary>One filter's options, by name or in order.</summary>
    private sealed class Args
    {
        private readonly Dictionary<string, string> _named = new(StringComparer.Ordinal);
        private readonly Queue<string> _positional = new();
        private readonly string _filter;
        private readonly string _flag;

        public Args(string filter, string text, string flag)
        {
            _filter = filter;
            _flag = flag;
            foreach (string part in text.Split(':', StringSplitOptions.RemoveEmptyEntries))
            {
                int equals = part.IndexOf('=', StringComparison.Ordinal);
                if (equals < 0)
                {
                    _positional.Enqueue(part.Trim());
                }
                else
                {
                    _named[part[..equals].Trim()] = part[(equals + 1)..].Trim();
                }
            }
        }

        /// <summary>Takes an option by its name or short name, else the next positional value, else the default.</summary>
        public string Take(string name, string fallback, string? shortName = null) => TakeOptional(name, shortName) ?? (_positional.TryDequeue(out string? value) ? value : fallback);

        public string? TakeOptional(string name, string? shortName = null)
        {
            if (_named.Remove(name, out string? value) || (shortName is not null && _named.Remove(shortName, out value)))
            {
                return value;
            }

            return null;
        }

        public void Refuse(params string[] names)
        {
            foreach (string name in names)
            {
                if (_named.ContainsKey(name))
                {
                    throw new UsageException($"{_flag} {_filter}: egami-video does not take {name}.");
                }
            }
        }

        public UsageException Bad(string name, string value, string expected) => new($"{_flag} {_filter}: {name}={value} is not {expected}.");

        public void Done()
        {
            if (_named.Count > 0 || _positional.Count > 0)
            {
                string left = _named.Count > 0 ? _named.Keys.First() : _positional.Peek();
                throw new UsageException($"{_flag} {_filter}: egami-video does not take '{left}'.");
            }
        }
    }

    private abstract record Step(string Name)
    {
        public bool ChangesLength { get; init; }

        public bool RemovesSilence { get; init; }
    }

    private sealed record FilterStep(string Name, Func<int, IAudioFilter> Make) : Step(Name);

    private sealed record LoudnessStep(string Name, LoudnessTarget Target, bool Dynamic) : Step(Name);

    /// <summary>A fade in from the first sample, wherever the sound's time line starts.</summary>
    private sealed class LeadFade(MediaTime duration) : IAudioFilter
    {
        private AudioFade? _fade;

        public MediaTime Latency => MediaTime.Zero;

        public void Send(AudioFrame frame)
        {
            _fade ??= new AudioFade(FadeDirection.In, frame.Timestamp, duration);
            _fade.Send(frame);
        }

        public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame)
        {
            frame = null;
            return _fade is not null && _fade.TryReceive(out frame);
        }

        public void Flush() => _fade?.Flush();

        public void Reset()
        {
            _fade?.Dispose();
            _fade = null;
        }

        public void Dispose() => _fade?.Dispose();
    }

    /// <summary>A fade out up to the last sample: holds the last stretch of the duration until the end is known.</summary>
    private sealed class TailFade(MediaTime duration) : IAudioFilter
    {
        private readonly Queue<AudioFrame> _held = new();
        private readonly Queue<AudioFrame> _ready = new();
        private MediaTime _heldLength;

        public MediaTime Latency => duration;

        public void Send(AudioFrame frame)
        {
            AudioFrame copy = new(SampleFormat.F32Planar, frame.Layout, frame.SampleRate, frame.SampleCount, frame.Timestamp);
            frame.CopyTo(copy);
            _held.Enqueue(copy);

            _heldLength += frame.Duration;
            while (_held.Count > 1 && _heldLength - _held.Peek().Duration >= duration)
            {
                AudioFrame front = _held.Dequeue();
                _heldLength -= front.Duration;
                _ready.Enqueue(front);
            }
        }

        public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

        public void Flush()
        {
            if (_held.Count == 0)
            {
                return;
            }

            AudioFrame last = _held.Last();
            MediaTime end = last.Timestamp + last.Duration;
            MediaTime start = end - duration < _held.Peek().Timestamp ? _held.Peek().Timestamp : end - duration;
            using AudioFade fade = new(FadeDirection.Out, start, end - start);
            while (_held.TryDequeue(out AudioFrame? frame))
            {
                using (frame)
                {
                    fade.Send(frame);
                }

                while (fade.TryReceive(out AudioFrame? faded))
                {
                    _ready.Enqueue(faded);
                }
            }

            fade.Flush();
            while (fade.TryReceive(out AudioFrame? faded))
            {
                _ready.Enqueue(faded);
            }

            _heldLength = MediaTime.Zero;
        }

        public void Reset()
        {
            Drop(_held);
            Drop(_ready);
            _heldLength = MediaTime.Zero;
        }

        public void Dispose() => Reset();

        private static void Drop(Queue<AudioFrame> frames)
        {
            while (frames.TryDequeue(out AudioFrame? frame))
            {
                frame.Dispose();
            }
        }
    }
}
