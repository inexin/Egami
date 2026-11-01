using Egami.Pixels;
using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// Draws pictures of audio: a waveform (the min and max of each column with the RMS level shaded inside, as editors and podcast
/// players show it) and a spectrogram (frequency over time, loudness as color). Both read the audio once with constant memory,
/// so a three-hour file costs no more memory than a short one, and return a plain <see cref="Raster{TPixel}"/> without text;
/// the CLI adds axes and legends.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public static class AudioPictures
{
    /// <summary>Draws the waveform of a file's audio track.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="options">The size, layout and colors, or <see langword="null"/> for <see cref="WaveformOptions.Default"/>.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the drawing between frames.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="OperationCanceledException">The drawing was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
    /// </example>
    public static Raster<Rgba8> Waveform(string path, WaveformOptions? options = null, MediaContext? context = null,
        IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= WaveformOptions.Default;
        options.Validate();
        return FromFile(path, options.Track, context, progress, (frames, duration) => Waveform(frames, duration, options, context, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Draws the waveform of a stream's audio track, as <see cref="Waveform(string, WaveformOptions?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
    /// does a file's. A stream that cannot seek is read once; when its container does not say how long the audio runs, it is
    /// copied to a temporary file on the way, which is deleted after. The caller owns the stream: it is read from its current
    /// position and not disposed.
    /// </summary>
    /// <param name="stream">The media stream.</param>
    /// <param name="options">The size, layout and colors, or <see langword="null"/> for <see cref="WaveformOptions.Default"/>.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the drawing between frames.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="OperationCanceledException">The drawing was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-stream"/>
    /// </example>
    public static Raster<Rgba8> Waveform(Stream stream, WaveformOptions? options = null, MediaContext? context = null,
        IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= WaveformOptions.Default;
        options.Validate();
        return FromStream(stream, options.Track, context, progress, (frames, duration) => Waveform(frames, duration, options, context, cancellationToken), cancellationToken);
    }

    /// <summary>Draws the waveform of a run of audio frames.</summary>
    /// <param name="frames">The frames, in time order, in any sample format; they are read once and not disposed.</param>
    /// <param name="duration">How long the audio runs, which sets how many samples each column covers.</param>
    /// <param name="options">The size, layout and colors, or <see langword="null"/> for <see cref="WaveformOptions.Default"/>.</param>
    /// <param name="context">The context the picture is allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Stops the drawing between frames.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range, or the duration is not above zero.</exception>
    /// <exception cref="OperationCanceledException">The drawing was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
    /// </example>
    public static Raster<Rgba8> Waveform(IEnumerable<AudioFrame> frames, MediaTime duration, WaveformOptions? options = null, MediaContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= WaveformOptions.Default;
        options.Validate();
        CheckDuration(duration);
        WaveformBuilder builder = new(options, duration);
        foreach (AudioFrame frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder.Add(frame);
        }

        return builder.Draw((context ?? MediaContext.Default).Egami);
    }

    /// <summary>Draws the spectrogram of a file's audio track.</summary>
    /// <param name="path">The media file.</param>
    /// <param name="options">The size, window, scale and colors, or <see langword="null"/> for <see cref="SpectrogramOptions.Default"/>.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the drawing between frames.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="OperationCanceledException">The drawing was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
    /// </example>
    public static Raster<Rgba8> Spectrogram(string path, SpectrogramOptions? options = null, MediaContext? context = null,
        IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        options ??= SpectrogramOptions.Default;
        options.Validate();
        return FromFile(path, options.Track, context, progress, (frames, duration) => Spectrogram(frames, duration, options, context, cancellationToken), cancellationToken);
    }

    /// <summary>
    /// Draws the spectrogram of a stream's audio track, as <see cref="Spectrogram(string, SpectrogramOptions?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
    /// does a file's. A stream that cannot seek is read once; when its container does not say how long the audio runs, it is
    /// copied to a temporary file on the way, which is deleted after. The caller owns the stream: it is read from its current
    /// position and not disposed.
    /// </summary>
    /// <param name="stream">The media stream.</param>
    /// <param name="options">The size, window, scale and colors, or <see langword="null"/> for <see cref="SpectrogramOptions.Default"/>.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="progress">Receives <see cref="MediaProgress"/> reports, or <see langword="null"/>.</param>
    /// <param name="cancellationToken">Stops the drawing between frames.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range.</exception>
    /// <exception cref="OperationCanceledException">The drawing was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-stream"/>
    /// </example>
    public static Raster<Rgba8> Spectrogram(Stream stream, SpectrogramOptions? options = null, MediaContext? context = null,
        IProgress<MediaProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        options ??= SpectrogramOptions.Default;
        options.Validate();
        return FromStream(stream, options.Track, context, progress, (frames, duration) => Spectrogram(frames, duration, options, context, cancellationToken), cancellationToken);
    }

    /// <summary>Draws the spectrogram of a run of audio frames.</summary>
    /// <param name="frames">The frames, in time order, in any sample format; they are read once and not disposed.</param>
    /// <param name="duration">How long the audio runs, which sets where each column's window sits.</param>
    /// <param name="options">The size, window, scale and colors, or <see langword="null"/> for <see cref="SpectrogramOptions.Default"/>.</param>
    /// <param name="context">The context the picture is allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Stops the drawing between frames.</param>
    /// <returns>The picture.</returns>
    /// <exception cref="ArgumentOutOfRangeException">An option is out of range, or the duration is not above zero.</exception>
    /// <exception cref="OperationCanceledException">The drawing was canceled.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
    /// </example>
    public static Raster<Rgba8> Spectrogram(IEnumerable<AudioFrame> frames, MediaTime duration, SpectrogramOptions? options = null, MediaContext? context = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(frames);
        options ??= SpectrogramOptions.Default;
        options.Validate();
        CheckDuration(duration);
        SpectrogramBuilder? builder = null;
        foreach (AudioFrame frame in frames)
        {
            cancellationToken.ThrowIfCancellationRequested();
            builder ??= new SpectrogramBuilder(options, duration, frame, (context ?? MediaContext.Default).Egami);
            builder.Add(frame);
        }

        return builder?.Finish() ?? Raster.Create(options.Width, options.Height, Colors.Map(options.Colors, 0), (context ?? MediaContext.Default).Egami);
    }

    private static void CheckDuration(MediaTime duration)
    {
        if (duration.Ticks <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(duration), duration, "The duration must be above zero.");
        }
    }

    /// <summary>Opens the audio track and hands its frames and duration to <paramref name="draw"/>, counting samples first when the file has no duration.</summary>
    private static Raster<Rgba8> FromFile(string path, int? track, MediaContext? context, IProgress<MediaProgress>? progress,
        Func<IEnumerable<AudioFrame>, MediaTime, Raster<Rgba8>> draw, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(path);
        AudioReaderOptions readerOptions = new() { TrackNumber = track };
        return FromSource(() => AudioReader.Open(path, readerOptions, context), progress, draw, cancellationToken);
    }

    /// <summary>
    /// <see cref="FromFile"/> for a stream: a seekable one is opened twice from its position; an unseekable one is drawn in one
    /// pass when its container gives the duration, else copied to a temporary file as it is read, and drawn from that.
    /// </summary>
    private static Raster<Rgba8> FromStream(Stream stream, int? track, MediaContext? context, IProgress<MediaProgress>? progress,
        Func<IEnumerable<AudioFrame>, MediaTime, Raster<Rgba8>> draw, CancellationToken cancellationToken)
    {
        AudioReaderOptions readerOptions = new() { TrackNumber = track };
        if (stream.CanSeek)
        {
            long start = stream.Position;
            return FromSource(
                () =>
                {
                    stream.Position = start;
                    return AudioReader.Open(stream, readerOptions, context, leaveOpen: true);
                },
                progress,
                draw,
                cancellationToken);
        }

        StreamSources.Recording recording = new(stream);
        using (AudioReader reader = AudioReader.Open(recording, readerOptions, context, leaveOpen: true))
        {
            if ((reader.Track.Duration ?? reader.Info.Duration) is { Ticks: > 0 } known)
            {
                return Draw(reader, known, progress, draw);
            }
        }

        using FileStream copy = StreamSources.TemporaryFile();
        recording.CopyAllTo(copy, cancellationToken);
        copy.Position = 0;
        return FromSource(
            () =>
            {
                copy.Position = 0;
                return AudioReader.Open(copy, readerOptions, context, leaveOpen: true);
            },
            progress,
            draw,
            cancellationToken);
    }

    /// <summary>Opens the reader, counting samples in a first pass when the source does not give the duration, and draws.</summary>
    private static Raster<Rgba8> FromSource(Func<AudioReader> open, IProgress<MediaProgress>? progress,
        Func<IEnumerable<AudioFrame>, MediaTime, Raster<Rgba8>> draw, CancellationToken cancellationToken)
    {
        MediaTime? duration;
        using (AudioReader probe = open())
        {
            duration = probe.Track.Duration ?? probe.Info.Duration;
            if (duration is null || duration.Value.Ticks <= 0)
            {
                long samples = 0;
                foreach (AudioFrame frame in probe.ReadFrames())
                {
                    using (frame)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        samples += frame.SampleCount;
                    }
                }

                duration = new MediaTime(Math.Max(samples, 1), new Rational(1, probe.SampleRate));
            }
        }

        using AudioReader reader = open();
        return Draw(reader, duration.Value, progress, draw);
    }

    private static Raster<Rgba8> Draw(AudioReader reader, MediaTime duration, IProgress<MediaProgress>? progress, Func<IEnumerable<AudioFrame>, MediaTime, Raster<Rgba8>> draw)
    {
        AnalysisReporter? reporter = progress is null ? null : new(progress, duration, MediaTime.Zero);
        Raster<Rgba8> picture = draw(Disposing(reader.ReadFrames(), reporter), duration);
        reporter?.Finish(duration, 0);
        return picture;
    }

    private static IEnumerable<AudioFrame> Disposing(IEnumerable<AudioFrame> frames, AnalysisReporter? reporter)
    {
        foreach (AudioFrame frame in frames)
        {
            using (frame)
            {
                yield return frame;
                reporter?.Advance(frame.Timestamp + frame.Duration, 0);
            }
        }
    }

    /// <summary>Returns the first sample of column <paramref name="column"/> when <paramref name="total"/> samples fill <paramref name="width"/> columns.</summary>
    private static long ColumnStart(long column, long total, int width) => column * total / width;

    /// <summary>Collects each column's min, max and sum of squares per band, then draws them.</summary>
    private sealed class WaveformBuilder(WaveformOptions options, MediaTime duration)
    {
        private readonly AudioChannels _input = new();
        private float[] _min = [], _max = [];
        private double[] _squares = [];
        private long[] _counts = [];
        private int _bands;
        private long _total;
        private long _position;

        public void Add(AudioFrame frame)
        {
            _input.Read(frame, "waveform");
            if (_bands == 0)
            {
                _bands = options.Channels == AudioPictureChannels.Stacked ? _input.Count : 1;
                int cells = _bands * options.Width;
                _min = new float[cells];
                _max = new float[cells];
                _squares = new double[cells];
                _counts = new long[cells];
                Array.Fill(_min, float.PositiveInfinity);
                Array.Fill(_max, float.NegativeInfinity);
                _total = Math.Max(1, (long)Math.Round(duration.Seconds * frame.SampleRate));
            }

            int count = frame.SampleCount;
            int i = 0;
            while (i < count)
            {
                long sample = _position + i;
                int column = (int)Math.Min(options.Width - 1, sample * options.Width / _total);
                long end = column == options.Width - 1 ? long.MaxValue : ColumnStart(column + 1, _total, options.Width);
                int run = (int)Math.Min(count - i, Math.Max(1, end - sample));
                for (int c = 0; c < _input.Count; c++)
                {
                    int cell = ((_bands == 1 ? 0 : c) * options.Width) + column;
                    ReadOnlySpan<float> samples = _input.Channel(c, count).Slice(i, run);
                    float min = _min[cell], max = _max[cell];
                    double squares = 0;
                    foreach (float x in samples)
                    {
                        min = Math.Min(min, x);
                        max = Math.Max(max, x);
                        squares += (double)x * x;
                    }

                    _min[cell] = min;
                    _max[cell] = max;
                    _squares[cell] += squares;
                    _counts[cell] += run;
                }

                i += run;
            }

            _position += count;
        }

        public Raster<Rgba8> Draw(EgamiContext context)
        {
            Raster<Rgba8> picture = Raster.Create(options.Width, options.Height, options.Background, context);
            if (_bands == 0)
            {
                return picture;
            }

            for (int band = 0; band < _bands; band++)
            {
                int top = band * options.Height / _bands;
                int height = ((band + 1) * options.Height / _bands) - top;
                double middle = top + (height / 2.0);
                double half = height / 2.0;
                for (int x = 0; x < options.Width; x++)
                {
                    int cell = (band * options.Width) + x;
                    if (_counts[cell] == 0)
                    {
                        continue;
                    }

                    double rms = Math.Sqrt(_squares[cell] / _counts[cell]);
                    int yMax = Row(middle - (half * Scale(_max[cell])), top, height);
                    int yMin = Row(middle - (half * Scale(_min[cell])), top, height);
                    Line(picture, x, yMax, yMin, options.Foreground);
                    if (options.Rms is { } shade)
                    {
                        int rmsTop = Math.Max(yMax, Row(middle - (half * Scale(rms)), top, height));
                        int rmsBottom = Math.Min(yMin, Row(middle + (half * Scale(rms)), top, height));
                        Line(picture, x, rmsTop, rmsBottom, shade);
                    }
                }
            }

            return picture;
        }

        /// <summary>Maps a sample value to -1..1: as it is, or by its level in dB from <see cref="WaveformOptions.Floor"/> to 0.</summary>
        private double Scale(double value)
        {
            if (options.Scale == WaveformScale.Linear)
            {
                return Math.Clamp(value, -1, 1);
            }

            double level = AudioChannels.Decibels(Math.Abs(value));
            double t = double.IsNegativeInfinity(level) ? 0 : Math.Clamp((level - options.Floor) / -options.Floor, 0, 1);
            return Math.Sign(value) * t;
        }

        private static int Row(double y, int top, int height) => Math.Clamp((int)Math.Round(y - 0.5), top, top + height - 1);

        private static void Line(Raster<Rgba8> picture, int x, int from, int to, Rgba8 color)
        {
            for (int y = from; y <= to; y++)
            {
                picture.Row(y)[x] = color;
            }
        }
    }

    /// <summary>Keeps the last window of samples per band and paints a column whenever a column's window is complete.</summary>
    private sealed class SpectrogramBuilder
    {
        private readonly SpectrogramOptions _options;
        private readonly AudioChannels _input = new();
        private readonly RealFft _fft;
        private readonly float[] _window;
        private readonly float[] _scratch;
        private readonly float[] _magnitudes;
        private readonly float[][] _rings;
        private readonly double[][] _rowBins;
        private readonly Rgba8[] _palette;
        private readonly Raster<Rgba8> _picture;
        private readonly long _total;
        private readonly double _scale;
        private int _ringPosition;
        private long _position;
        private int _column;
        private long _nextEnd;

        public SpectrogramBuilder(SpectrogramOptions options, MediaTime duration, AudioFrame first, EgamiContext context)
        {
            _options = options;
            int size = options.WindowSize;
            _fft = new RealFft(size);
            _window = new float[size];
            double sum = 0;
            for (int i = 0; i < size; i++)
            {
                // A periodic Hann window.
                _window[i] = (float)(0.5 - (0.5 * Math.Cos(2 * Math.PI * i / size)));
                sum += _window[i];
            }

            // A full-scale sine then reads 0 dB.
            _scale = 2 / sum;
            _scratch = new float[size];
            _magnitudes = new float[_fft.Bins];
            int bands = options.Channels == AudioPictureChannels.Stacked ? first.ChannelCount : 1;
            _rings = new float[bands][];
            _rowBins = new double[bands][];
            for (int b = 0; b < bands; b++)
            {
                _rings[b] = new float[size];
                int height = ((b + 1) * options.Height / bands) - (b * options.Height / bands);
                _rowBins[b] = RowBins(height, first.SampleRate, size);
            }

            _palette = new Rgba8[256];
            for (int i = 0; i < _palette.Length; i++)
            {
                _palette[i] = Colors.Map(options.Colors, i / 255.0);
            }

            _picture = Raster.Create(options.Width, options.Height, _palette[0], context);
            _total = Math.Max(1, (long)Math.Round(duration.Seconds * first.SampleRate));
            _nextEnd = WindowEnd(0);
        }

        public void Add(AudioFrame frame)
        {
            _input.Read(frame, "spectrogram");
            int count = frame.SampleCount;
            int size = _options.WindowSize;
            int i = 0;
            while (i < count)
            {
                // Copy up to the next column's window end or the ring's end, whichever comes first.
                int run = (int)Math.Min(Math.Min(count - i, size - _ringPosition), Math.Max(1, _nextEnd - _position));
                if (_rings.Length == 1)
                {
                    Span<float> ring = _rings[0].AsSpan(_ringPosition, run);
                    _input.Channel(0, count).Slice(i, run).CopyTo(ring);
                    for (int c = 1; c < _input.Count; c++)
                    {
                        ReadOnlySpan<float> channel = _input.Channel(c, count).Slice(i, run);
                        for (int j = 0; j < run; j++)
                        {
                            ring[j] += channel[j];
                        }
                    }

                    if (_input.Count > 1)
                    {
                        float mix = 1f / _input.Count;
                        for (int j = 0; j < run; j++)
                        {
                            ring[j] *= mix;
                        }
                    }
                }
                else
                {
                    for (int c = 0; c < _rings.Length; c++)
                    {
                        _input.Channel(c, count).Slice(i, run).CopyTo(_rings[c].AsSpan(_ringPosition, run));
                    }
                }

                _ringPosition = (_ringPosition + run) % size;
                _position += run;
                i += run;
                while (_column < _options.Width && _position == _nextEnd)
                {
                    Paint();
                }
            }
        }

        public Raster<Rgba8> Finish()
        {
            // Columns whose windows run past the audio's end are drawn from the last samples, as if the rest were silence.
            while (_column < _options.Width)
            {
                Paint();
            }

            return _picture;
        }

        /// <summary>Returns the sample after the window of column <paramref name="column"/>: the windows spread evenly from the start to the end.</summary>
        private long WindowEnd(int column)
        {
            long size = _options.WindowSize;
            long start = _options.Width > 1 && _total > size ? column * (_total - size) / (_options.Width - 1) : 0;
            return start + size;
        }

        private void Paint()
        {
            for (int band = 0; band < _rings.Length; band++)
            {
                float[] ring = _rings[band];
                int size = ring.Length;
                for (int i = 0; i < size; i++)
                {
                    _scratch[i] = ring[(_ringPosition + i) % size] * _window[i];
                }

                _fft.Magnitudes(_scratch, _magnitudes);
                int top = band * _options.Height / _rings.Length;
                double[] edges = _rowBins[band];
                for (int y = 0; y < edges.Length - 1; y++)
                {
                    // A row that spans whole bins shows the strongest of them, so a tone between two rows' centers still shows.
                    double high = edges[y], low = edges[y + 1];
                    double magnitude = Math.Max(Interpolate(low), Interpolate(high));
                    for (int k = (int)Math.Ceiling(low); k <= high; k++)
                    {
                        magnitude = Math.Max(magnitude, _magnitudes[k]);
                    }

                    double level = AudioChannels.Decibels(magnitude * _scale);
                    double t = double.IsNegativeInfinity(level) ? 0 : Math.Clamp((level - _options.Floor) / (_options.Ceiling - _options.Floor), 0, 1);
                    _picture.Row(top + y)[_column] = _palette[(int)Math.Round(t * 255)];
                }
            }

            _column++;
            _nextEnd = _column < _options.Width ? WindowEnd(_column) : long.MaxValue;
        }

        private double Interpolate(double bin)
        {
            int low = (int)bin;
            int high = Math.Min(low + 1, _magnitudes.Length - 1);
            return _magnitudes[low] + ((bin - low) * (_magnitudes[high] - _magnitudes[low]));
        }

        /// <summary>Returns the fractional FFT bin at each row edge, from the top of the first row (the highest frequency) to the bottom of the last.</summary>
        private double[] RowBins(int height, int rate, int size)
        {
            double nyquist = rate / 2.0;
            double binWidth = (double)rate / size;
            double low = Math.Min(_options.MinFrequency, nyquist / 2);
            double[] edges = new double[height + 1];
            for (int y = 0; y <= height; y++)
            {
                double t = 1 - ((double)y / height);
                double frequency = _options.Scale == FrequencyScale.Linear
                    ? t * nyquist
                    : low * Math.Pow(nyquist / low, t);
                edges[y] = Math.Clamp(frequency / binWidth, 0, size / 2);
            }

            return edges;
        }
    }

    /// <summary>The color maps.</summary>
    private static class Colors
    {
        public static Rgba8 Map(SpectrogramColors colors, double t)
        {
            t = Math.Clamp(t, 0, 1);
            if (colors == SpectrogramColors.Grayscale)
            {
                byte v = (byte)Math.Round(t * 255);
                return new Rgba8(v, v, v, 255);
            }

            // Matplotlib's magma (CC0), as a sixth-degree polynomial fit of its 256-entry table per channel.
            double r = Poly(t, -0.002136485053939582, 0.2516605407371642, 8.353717279216625, -27.66873308576866, 52.17613981234068, -50.76852536473588, 18.65570506591883);
            double g = Poly(t, -0.000749655052795221, 0.6775232436837668, -3.577719514958484, 14.26473078096533, -27.94360607168351, 29.04658282127291, -11.48977351997711);
            double b = Poly(t, -0.005386127855323933, 2.494026599312351, 0.3144679030132573, -13.64921318813922, 12.94416944238394, 4.23415299384598, -5.601961508734096);
            return new Rgba8(Byte(r), Byte(g), Byte(b), 255);
        }

        private static double Poly(double t, double c0, double c1, double c2, double c3, double c4, double c5, double c6) =>
            c0 + (t * (c1 + (t * (c2 + (t * (c3 + (t * (c4 + (t * (c5 + (t * c6)))))))))));

        private static byte Byte(double v) => (byte)Math.Round(Math.Clamp(v, 0, 1) * 255);
    }
}

/// <summary>How <see cref="AudioPictures"/> lays out the channels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public enum AudioPictureChannels
{
    /// <summary>All channels in one picture: the waveform of every channel drawn together, or the spectrogram of their mix.</summary>
    Mixed,

    /// <summary>One strip per channel, top to bottom in channel order.</summary>
    Stacked,
}

/// <summary>How <see cref="AudioPictures.Waveform(IEnumerable{AudioFrame}, MediaTime, WaveformOptions?, MediaContext?, CancellationToken)"/> scales sample values.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public enum WaveformScale
{
    /// <summary>Sample values as they are: full scale at the strip's edge.</summary>
    Linear,

    /// <summary>Levels in dB from <see cref="WaveformOptions.Floor"/> at the middle to 0 dBFS at the edge, so quiet passages show.</summary>
    Decibel,
}

/// <summary>The frequency axis of <see cref="AudioPictures.Spectrogram(IEnumerable{AudioFrame}, MediaTime, SpectrogramOptions?, MediaContext?, CancellationToken)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public enum FrequencyScale
{
    /// <summary>Evenly spaced from 0 Hz at the bottom to half the sample rate at the top.</summary>
    Linear,

    /// <summary>Evenly spaced in octaves from <see cref="SpectrogramOptions.MinFrequency"/> to half the sample rate, as the ear hears.</summary>
    Logarithmic,
}

/// <summary>The colors of <see cref="AudioPictures.Spectrogram(IEnumerable{AudioFrame}, MediaTime, SpectrogramOptions?, MediaContext?, CancellationToken)"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public enum SpectrogramColors
{
    /// <summary>Matplotlib's magma: black through purple and orange to pale yellow, even in perceived lightness.</summary>
    Magma,

    /// <summary>Black to white.</summary>
    Grayscale,
}

/// <summary>The size, layout and colors of an <see cref="AudioPictures"/> waveform.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public sealed record WaveformOptions
{
    /// <summary>Gets the default options.</summary>
    public static WaveformOptions Default { get; } = new();

    /// <summary>Gets the width in pixels; each column covers an even share of the audio. Default 1800.</summary>
    public int Width { get; init; } = 1800;

    /// <summary>Gets the height in pixels. Default 280.</summary>
    public int Height { get; init; } = 280;

    /// <summary>Gets whether the channels share one strip or each get their own. Default <see cref="AudioPictureChannels.Mixed"/>.</summary>
    public AudioPictureChannels Channels { get; init; } = AudioPictureChannels.Mixed;

    /// <summary>Gets how sample values are scaled. Default <see cref="WaveformScale.Linear"/>.</summary>
    public WaveformScale Scale { get; init; } = WaveformScale.Linear;

    /// <summary>Gets the lowest level shown with <see cref="WaveformScale.Decibel"/>, in dBFS. Default -60.</summary>
    public double Floor { get; init; } = -60;

    /// <summary>Gets the color of the min-to-max outline. Default a mid blue.</summary>
    public Rgba8 Foreground { get; init; } = new(46, 110, 196, 255);

    /// <summary>Gets the color of the RMS band inside the outline, or <see langword="null"/> to leave it out. Default a light blue.</summary>
    public Rgba8? Rms { get; init; } = new(132, 180, 236, 255);

    /// <summary>Gets the background color. Default transparent.</summary>
    public Rgba8 Background { get; init; } = new(0, 0, 0, 0);

    /// <summary>Gets the audio track to draw when reading a file, or <see langword="null"/> for the first.</summary>
    public int? Track { get; init; }

    internal void Validate()
    {
        if (Width is < 1 or > 32768)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), Width, "The width must be from 1 to 32768.");
        }

        if (Height is < 1 or > 32768)
        {
            throw new ArgumentOutOfRangeException(nameof(Height), Height, "The height must be from 1 to 32768.");
        }

        if (Floor >= 0 || double.IsNaN(Floor))
        {
            throw new ArgumentOutOfRangeException(nameof(Floor), Floor, "The floor must be below 0 dBFS.");
        }
    }
}

/// <summary>The size, window, scale and colors of an <see cref="AudioPictures"/> spectrogram.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-pictures"/>
/// </example>
public sealed record SpectrogramOptions
{
    /// <summary>Gets the default options.</summary>
    public static SpectrogramOptions Default { get; } = new();

    /// <summary>Gets the width in pixels: one window per column, spread evenly over the audio. Default 1800.</summary>
    public int Width { get; init; } = 1800;

    /// <summary>Gets the height in pixels. Default 512.</summary>
    public int Height { get; init; } = 512;

    /// <summary>Gets the FFT window length in samples, a power of two from 256 to 32768. Default 2048.</summary>
    public int WindowSize { get; init; } = 2048;

    /// <summary>Gets the frequency axis. Default <see cref="FrequencyScale.Linear"/>.</summary>
    public FrequencyScale Scale { get; init; } = FrequencyScale.Linear;

    /// <summary>Gets the lowest frequency on the <see cref="FrequencyScale.Logarithmic"/> axis, in hertz. Default 20.</summary>
    public double MinFrequency { get; init; } = 20;

    /// <summary>Gets the level drawn with the darkest color, in dBFS. Default -120.</summary>
    public double Floor { get; init; } = -120;

    /// <summary>Gets the level drawn with the brightest color, in dBFS, where a full-scale sine reads 0. Default 0.</summary>
    public double Ceiling { get; init; }

    /// <summary>Gets whether the channels are mixed into one picture or each get their own strip. Default <see cref="AudioPictureChannels.Mixed"/>.</summary>
    public AudioPictureChannels Channels { get; init; } = AudioPictureChannels.Mixed;

    /// <summary>Gets the color map. Default <see cref="SpectrogramColors.Magma"/>.</summary>
    public SpectrogramColors Colors { get; init; } = SpectrogramColors.Magma;

    /// <summary>Gets the audio track to draw when reading a file, or <see langword="null"/> for the first.</summary>
    public int? Track { get; init; }

    internal void Validate()
    {
        if (Width is < 1 or > 32768)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), Width, "The width must be from 1 to 32768.");
        }

        if (Height is < 1 or > 32768)
        {
            throw new ArgumentOutOfRangeException(nameof(Height), Height, "The height must be from 1 to 32768.");
        }

        if (WindowSize is < 256 or > 32768 || (WindowSize & (WindowSize - 1)) != 0)
        {
            throw new ArgumentOutOfRangeException(nameof(WindowSize), WindowSize, "The window size must be a power of two from 256 to 32768.");
        }

        if (MinFrequency <= 0 || double.IsNaN(MinFrequency))
        {
            throw new ArgumentOutOfRangeException(nameof(MinFrequency), MinFrequency, "The lowest frequency must be above 0 Hz.");
        }

        if (!(Floor < Ceiling))
        {
            throw new ArgumentOutOfRangeException(nameof(Floor), Floor, "The floor must be below the ceiling.");
        }
    }
}
