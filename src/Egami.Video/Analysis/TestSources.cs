using Egami.Pixels;
using Egami.Video.Audio;

namespace Egami.Video.Analysis;

/// <summary>
/// Generates test video and audio: a moving test pattern with frame numbers and timecode, SMPTE color bars, solid colors and
/// gradients, and sine tones, silence, white or pink noise and sweeps. The frames come out one at a time, so any length costs
/// the same memory; hand them to <see cref="VideoWriter.WriteFrame(VideoFrame)"/> and <see cref="VideoWriter.WriteAudio"/>, or
/// to a detector. Dispose each frame.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-test-sources"/>
/// </example>
public static class TestSources
{
    /// <summary>The 75 % bar colors in their usual order: white, yellow, cyan, green, magenta, red, blue.</summary>
    private static readonly (double R, double G, double B)[] Bars75 =
    [
        (0.75, 0.75, 0.75), (0.75, 0.75, 0), (0, 0.75, 0.75), (0, 0.75, 0), (0.75, 0, 0.75), (0.75, 0, 0), (0, 0, 0.75),
    ];

    /// <summary>
    /// Generates Egami's own test pattern: 75 % bars over the top half, a black-to-white ramp, a white square that crosses the
    /// picture once a second, and the frame number and timecode in seven-segment digits, so dropped, repeated or shifted frames
    /// show at a glance.
    /// </summary>
    /// <param name="format">The size, rate and layout, or <see langword="null"/> for <see cref="TestVideoFormat.Default"/>.</param>
    /// <param name="duration">How long the video runs.</param>
    /// <returns>The frames.</returns>
    public static IEnumerable<YuvVideoFrame> Pattern(TestVideoFormat? format, MediaTime duration)
    {
        format ??= TestVideoFormat.Default;
        format.Validate();
        return Frames(format, duration, null, (painter, index, time) => DrawPattern(painter, format, index, time));
    }

    /// <summary>
    /// Generates SMPTE color bars: the HD layout of SMPTE RP 219 (40 % gray sides, 75 % bars, a row with 100 % cyan and blue, a
    /// ramp row with 100 % yellow and red, and a PLUGE row with -2 %, +2 % and +4 % steps) or the SD layout of SMPTE EG 1 (75 %
    /// bars, reverse blue castellations, and -I, white, +Q and a -4 %, 0, +4 % PLUGE). Levels follow the format's matrix, range
    /// and bit depth.
    /// </summary>
    /// <param name="format">The size, rate and layout, or <see langword="null"/> for <see cref="TestVideoFormat.Default"/>.</param>
    /// <param name="duration">How long the video runs.</param>
    /// <param name="style">Which layout; <see cref="ColorBarsStyle.Auto"/> takes HD for pictures 1280 or more wide.</param>
    /// <returns>The frames.</returns>
    public static IEnumerable<YuvVideoFrame> ColorBars(TestVideoFormat? format, MediaTime duration, ColorBarsStyle style = ColorBarsStyle.Auto)
    {
        format ??= TestVideoFormat.Default;
        format.Validate();
        bool hd = style == ColorBarsStyle.Hd || (style == ColorBarsStyle.Auto && format.Width >= 1280);
        return Frames(format, duration, painter =>
        {
            if (hd)
            {
                DrawHdBars(painter);
            }
            else
            {
                DrawSdBars(painter);
            }
        }, null);
    }

    /// <summary>Generates a solid color.</summary>
    /// <param name="format">The size, rate and layout, or <see langword="null"/> for <see cref="TestVideoFormat.Default"/>.</param>
    /// <param name="duration">How long the video runs.</param>
    /// <param name="color">The color, as R'G'B' (alpha is ignored).</param>
    /// <returns>The frames.</returns>
    public static IEnumerable<YuvVideoFrame> Solid(TestVideoFormat? format, MediaTime duration, Rgba8 color)
    {
        format ??= TestVideoFormat.Default;
        format.Validate();
        return Frames(format, duration, painter => painter.Fill(0, 0, painter.Width, painter.Height, color.R / 255.0, color.G / 255.0, color.B / 255.0), null);
    }

    /// <summary>Generates a linear gradient between two colors, across or down the picture.</summary>
    /// <param name="format">The size, rate and layout, or <see langword="null"/> for <see cref="TestVideoFormat.Default"/>.</param>
    /// <param name="duration">How long the video runs.</param>
    /// <param name="from">The color at the left (or top) edge.</param>
    /// <param name="to">The color at the right (or bottom) edge.</param>
    /// <param name="vertical">Whether the gradient runs top to bottom instead of left to right.</param>
    /// <returns>The frames.</returns>
    public static IEnumerable<YuvVideoFrame> Gradient(TestVideoFormat? format, MediaTime duration, Rgba8 from, Rgba8 to, bool vertical = false)
    {
        format ??= TestVideoFormat.Default;
        format.Validate();
        return Frames(format, duration, painter =>
        {
            int steps = vertical ? painter.Height : painter.Width;
            for (int i = 0; i < steps; i++)
            {
                double t = steps > 1 ? (double)i / (steps - 1) : 0;
                double r = (from.R + ((to.R - from.R) * t)) / 255, g = (from.G + ((to.G - from.G) * t)) / 255, b = (from.B + ((to.B - from.B) * t)) / 255;
                if (vertical)
                {
                    painter.Fill(0, i, painter.Width, i + 1, r, g, b);
                }
                else
                {
                    painter.Fill(i, 0, i + 1, painter.Height, r, g, b);
                }
            }
        }, null);
    }

    /// <summary>Generates a sine tone, the same on every channel.</summary>
    /// <param name="format">The rate, channels and frame size, or <see langword="null"/> for <see cref="TestAudioFormat.Default"/>.</param>
    /// <param name="duration">How long the audio runs.</param>
    /// <param name="frequency">The frequency in hertz. Default 1 kHz.</param>
    /// <param name="level">The peak level in dBFS. Default -18 dBFS, the EBU alignment level.</param>
    /// <returns>The frames, as planar 32-bit float.</returns>
    public static IEnumerable<AudioFrame> Sine(TestAudioFormat? format, MediaTime duration, double frequency = 1000, double level = -18)
    {
        format ??= TestAudioFormat.Default;
        format.Validate();
        CheckFrequency(frequency, format.SampleRate, nameof(frequency));
        double amplitude = Math.Pow(10, level / 20);
        double step = 2 * Math.PI * frequency / format.SampleRate;
        return Samples(format, duration, (samples, first, _) =>
        {
            for (int i = 0; i < samples.Length; i++)
            {
                // The phase comes from the sample's index, so it never drifts over long tones.
                samples[i] = (float)(amplitude * Math.Sin(step * (first + i)));
            }
        });
    }

    /// <summary>Generates silence.</summary>
    /// <param name="format">The rate, channels and frame size, or <see langword="null"/> for <see cref="TestAudioFormat.Default"/>.</param>
    /// <param name="duration">How long the audio runs.</param>
    /// <returns>The frames, as planar 32-bit float.</returns>
    public static IEnumerable<AudioFrame> Silence(TestAudioFormat? format, MediaTime duration)
    {
        format ??= TestAudioFormat.Default;
        format.Validate();
        return Samples(format, duration, (samples, _, _) => samples.Clear());
    }

    /// <summary>
    /// Generates noise, different on each channel: white (flat spectrum) or pink (3 dB less per octave, by the Voss-McCartney
    /// method). The same seed gives the same samples.
    /// </summary>
    /// <param name="format">The rate, channels and frame size, or <see langword="null"/> for <see cref="TestAudioFormat.Default"/>.</param>
    /// <param name="duration">How long the audio runs.</param>
    /// <param name="color">White or pink.</param>
    /// <param name="level">The RMS level in dBFS, where a full-scale sine is 0 dBFS. Default -18 dBFS.</param>
    /// <param name="seed">The random seed.</param>
    /// <returns>The frames, as planar 32-bit float.</returns>
    public static IEnumerable<AudioFrame> Noise(TestAudioFormat? format, MediaTime duration, NoiseColor color = NoiseColor.White, double level = -18, int seed = 1)
    {
        format ??= TestAudioFormat.Default;
        format.Validate();
        double rms = Math.Pow(10, level / 20) / Math.Sqrt(2);
        NoiseGenerator[] generators = new NoiseGenerator[format.Layout.ChannelCount];
        for (int c = 0; c < generators.Length; c++)
        {
            generators[c] = new NoiseGenerator(color, rms, unchecked(seed + (c * 7919)));
        }

        return Samples(format, duration, (samples, _, channel) =>
        {
            NoiseGenerator generator = generators[channel];
            for (int i = 0; i < samples.Length; i++)
            {
                samples[i] = generator.Next();
            }
        });
    }

    /// <summary>Generates a logarithmic sine sweep, the same on every channel, for frequency response checks.</summary>
    /// <param name="format">The rate, channels and frame size, or <see langword="null"/> for <see cref="TestAudioFormat.Default"/>.</param>
    /// <param name="duration">How long the sweep takes.</param>
    /// <param name="from">The start frequency in hertz. Default 20 Hz.</param>
    /// <param name="to">The end frequency in hertz. Default 20 kHz, or just under half the sample rate when that is lower.</param>
    /// <param name="level">The peak level in dBFS. Default -18 dBFS.</param>
    /// <returns>The frames, as planar 32-bit float.</returns>
    public static IEnumerable<AudioFrame> Sweep(TestAudioFormat? format, MediaTime duration, double from = 20, double? to = null, double level = -18)
    {
        format ??= TestAudioFormat.Default;
        format.Validate();
        double end = to ?? Math.Min(20000, format.SampleRate * 0.45);
        CheckFrequency(from, format.SampleRate, nameof(from));
        CheckFrequency(end, format.SampleRate, nameof(to));
        double amplitude = Math.Pow(10, level / 20);
        double seconds = Math.Max(duration.Seconds, 1e-9);
        double k = Math.Log(end / from);
        return Samples(format, duration, (samples, first, _) =>
        {
            for (int i = 0; i < samples.Length; i++)
            {
                double t = (double)(first + i) / format.SampleRate;
                double phase = 2 * Math.PI * from * seconds / k * (Math.Exp(t / seconds * k) - 1);
                samples[i] = (float)(amplitude * Math.Sin(phase));
            }
        });
    }

    private static void CheckFrequency(double frequency, int rate, string name)
    {
        if (!(frequency > 0 && frequency < rate / 2.0))
        {
            throw new ArgumentOutOfRangeException(name, frequency, $"The frequency must be above 0 and below half the sample rate ({rate / 2} Hz).");
        }
    }

    private static IEnumerable<YuvVideoFrame> Frames(TestVideoFormat format, MediaTime duration, Action<YuvPainter>? still, Action<YuvPainter, long, MediaTime>? moving)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        return FramesCore(format, duration, still, moving);
    }

    private static IEnumerable<YuvVideoFrame> FramesCore(TestVideoFormat format, MediaTime duration, Action<YuvPainter>? still, Action<YuvPainter, long, MediaTime>? moving)
    {
        Rational timeBase = format.FrameRate.Reciprocal();
        long count = (long)Math.Ceiling((duration.Seconds * format.FrameRate.ToDouble()) - 1e-9);
        YuvRaster? template = null;
        try
        {
            if (still is not null)
            {
                template = YuvPainter.Create(format);
                still(new YuvPainter(template));
            }

            for (long i = 0; i < count; i++)
            {
                MediaTime time = new(i, timeBase);
                YuvRaster raster = YuvPainter.Create(format);
                try
                {
                    if (template is not null)
                    {
                        YuvPainter.Copy(template, raster);
                    }
                    else
                    {
                        moving!(new YuvPainter(raster), i, time);
                    }
                }
                catch
                {
                    raster.Dispose();
                    throw;
                }

                yield return new YuvVideoFrame(raster, time, new MediaTime(1, timeBase));
            }
        }
        finally
        {
            template?.Dispose();
        }
    }

    private static IEnumerable<AudioFrame> Samples(TestAudioFormat format, MediaTime duration, SampleFill fill)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(duration.Ticks, nameof(duration));
        return SamplesCore(format, duration, fill);
    }

    private static IEnumerable<AudioFrame> SamplesCore(TestAudioFormat format, MediaTime duration, SampleFill fill)
    {
        long total = (long)Math.Round(duration.Seconds * format.SampleRate);
        Rational timeBase = new(1, format.SampleRate);
        for (long first = 0; first < total; first += format.FrameSize)
        {
            int count = (int)Math.Min(format.FrameSize, total - first);
            AudioFrame frame = new(SampleFormat.F32Planar, format.Layout, format.SampleRate, count, new MediaTime(first, timeBase), format.Context);
            for (int c = 0; c < format.Layout.ChannelCount; c++)
            {
                fill(frame.GetSpan<float>(c), first, c);
            }

            yield return frame;
        }
    }

    private static void DrawPattern(YuvPainter painter, TestVideoFormat format, long index, MediaTime time)
    {
        int w = painter.Width, h = painter.Height;
        int barsEnd = h / 2, rampEnd = h * 13 / 20, markerEnd = h * 4 / 5;

        // Eight bars: the seven 75 % colors and black.
        for (int i = 0; i < 8; i++)
        {
            (double r, double g, double b) = i < 7 ? Bars75[i] : (0, 0, 0);
            painter.Fill(i * w / 8, 0, (i + 1) * w / 8, barsEnd, r, g, b);
        }

        painter.Ramp(0, barsEnd, w, rampEnd, 0, 1);

        // The marker crosses the band once a second, so its place shows where in the second the frame falls.
        painter.Fill(0, rampEnd, w, markerEnd, 0.15, 0.15, 0.15);
        int size = Math.Max(1, markerEnd - rampEnd);
        double second = time.Seconds - Math.Floor(time.Seconds);
        int x = (int)Math.Round(second * Math.Max(0, w - size));
        painter.Fill(x, rampEnd, x + size, markerEnd, 1, 1, 1);

        painter.Fill(0, markerEnd, w, h, 0, 0, 0);
        int fps = Math.Max(1, (int)Math.Round(format.FrameRate.ToDouble()));
        long whole = (long)Math.Floor(time.Seconds + 1e-9);
        int frameInSecond = (int)(index % fps);
        string timecode = string.Create(System.Globalization.CultureInfo.InvariantCulture,
            $"{whole / 3600 % 100:00}:{whole / 60 % 60:00}:{whole % 60:00}:{frameInSecond:00}");
        string number = index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        int digitHeight = Math.Max(5, (h - markerEnd) * 3 / 5);
        int top = markerEnd + ((h - markerEnd - digitHeight) / 2);
        SevenSegment.Draw(painter, number, Math.Max(1, w / 40), top, digitHeight);
        int width = SevenSegment.Width(timecode, digitHeight);
        SevenSegment.Draw(painter, timecode, w - width - Math.Max(1, w / 40), top, digitHeight);
    }

    /// <summary>SMPTE RP 219 bars, laid out on the standard's 1920-sample widths and scaled to the picture.</summary>
    private static void DrawHdBars(YuvPainter painter)
    {
        int w = painter.Width, h = painter.Height;
        int row1 = h * 7 / 12, row2 = h * 8 / 12, row3 = h * 9 / 12;

        // Pattern 1 widths in a 1920-sample line (the recommended set): d, f, c, c, e, c, c, f, d.
        int[] pattern1 = [240, 206, 206, 206, 204, 206, 206, 206, 240];
        int[] x = Edges(pattern1, w);
        painter.Fill(x[0], 0, x[1], row1, 0.4, 0.4, 0.4);
        for (int i = 0; i < 7; i++)
        {
            (double r, double g, double b) = Bars75[i];
            painter.Fill(x[i + 1], 0, x[i + 2], row1, r, g, b);
        }

        painter.Fill(x[8], 0, x[9], row1, 0.4, 0.4, 0.4);

        // Pattern 2: 100 % cyan, 75 % white (the *2 choice), 75 % white across the bars, 100 % blue.
        painter.Fill(x[0], row1, x[1], row2, 0, 1, 1);
        painter.Fill(x[1], row1, x[8], row2, 0.75, 0.75, 0.75);
        painter.Fill(x[8], row1, x[9], row2, 0, 0, 1);

        // Pattern 3: 100 % yellow, black (the *3 choice), a ramp from black to white under the five middle bars, 100 % white, 100 % red.
        painter.Fill(x[0], row2, x[1], row3, 1, 1, 0);
        painter.Fill(x[1], row2, x[2], row3, 0, 0, 0);
        painter.Ramp(x[2], row2, x[7], row3, 0, 1);
        painter.Fill(x[7], row2, x[8], row3, 1, 1, 1);
        painter.Fill(x[8], row2, x[9], row3, 1, 0, 0);

        // Pattern 4: 15 % gray sides around black, 100 % white, black, the PLUGE (-2 %, 0, +2 %, 0, +4 %) and black, on the
        // standard's 1440-sample middle (recommended widths).
        painter.Fill(x[0], row3, x[1], h, 0.15, 0.15, 0.15);
        painter.Fill(x[8], row3, x[9], h, 0.15, 0.15, 0.15);
        int[] pattern4 = [308, 412, 170, 68, 70, 68, 70, 68, 206];
        double[] levels = [0, 1, 0, -0.02, 0, 0.02, 0, 0.04, 0];
        int[] inner = Edges(pattern4, x[8] - x[1]);
        for (int i = 0; i < levels.Length; i++)
        {
            painter.Fill(x[1] + inner[i], row3, x[1] + inner[i + 1], h, levels[i], levels[i], levels[i]);
        }
    }

    /// <summary>SMPTE EG 1 bars: seven 75 % bars, reverse blue castellations, then -I, 100 % white, +Q, black and a PLUGE.</summary>
    private static void DrawSdBars(YuvPainter painter)
    {
        int w = painter.Width, h = painter.Height;
        int row1 = h * 2 / 3, row2 = h * 3 / 4;
        int[] bar = new int[8];
        for (int i = 0; i <= 7; i++)
        {
            bar[i] = i * w / 7;
        }

        (double R, double G, double B)[] castellations = [(0, 0, 0.75), (0, 0, 0), (0.75, 0, 0.75), (0, 0, 0), (0, 0.75, 0.75), (0, 0, 0), (0.75, 0.75, 0.75)];
        for (int i = 0; i < 7; i++)
        {
            (double r, double g, double b) = Bars75[i];
            painter.Fill(bar[i], 0, bar[i + 1], row1, r, g, b);
            (r, g, b) = castellations[i];
            painter.Fill(bar[i], row1, bar[i + 1], row2, r, g, b);
        }

        // The bottom row in units of a quarter bar: -I, white, +Q and black take five each; the PLUGE steps take a third of a bar
        // each, and black fills the last bar.
        double unit = w / 7.0;
        double[] widths = [1.25, 1.25, 1.25, 1.25, 1.0 / 3, 1.0 / 3, 1.0 / 3, 1];
        (double R, double G, double B)[] colors =
        [
            (0, 0.245600, 0.412545), (1, 1, 1), (0.253605, 0, 0.470286), (0, 0, 0), (-0.04, -0.04, -0.04), (0, 0, 0), (0.04, 0.04, 0.04), (0, 0, 0),
        ];
        double left = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            double right = left + (widths[i] * unit);
            (double r, double g, double b) = colors[i];
            painter.Fill((int)Math.Round(left), row2, i == widths.Length - 1 ? w : (int)Math.Round(right), h, r, g, b);
            left = right;
        }
    }

    /// <summary>Scales widths given in a standard line to the picture: each edge lands where its share of the line falls.</summary>
    private static int[] Edges(int[] widths, int width)
    {
        int total = widths.Sum();
        int[] edges = new int[widths.Length + 1];
        int sum = 0;
        for (int i = 0; i < widths.Length; i++)
        {
            sum += widths[i];
            edges[i + 1] = width == total ? sum : (int)Math.Round((double)sum * width / total);
        }

        return edges;
    }

    private delegate void SampleFill(Span<float> samples, long first, int channel);

    /// <summary>White noise from a uniform source, or pink noise from the Voss-McCartney sum of sixteen rows updated at halving rates.</summary>
    private sealed class NoiseGenerator
    {
        private const int Rows = 16;
        private readonly Random _random;
        private readonly bool _pink;
        private readonly double _scale;
        private readonly double[] _rows = new double[Rows];
        private double _sum;
        private uint _counter;

        public NoiseGenerator(NoiseColor color, double rms, int seed)
        {
            _random = new Random(seed);
            _pink = color == NoiseColor.Pink;

            // A uniform value from -1 to 1 has an RMS of 1/sqrt(3); the pink sum adds seventeen of them.
            _scale = _pink ? rms / Math.Sqrt((Rows + 1) / 3.0) : rms * Math.Sqrt(3);
            if (_pink)
            {
                for (int i = 0; i < Rows; i++)
                {
                    _rows[i] = Uniform();
                    _sum += _rows[i];
                }
            }
        }

        public float Next()
        {
            if (!_pink)
            {
                return (float)Math.Clamp(Uniform() * _scale, -1, 1);
            }

            // Row k changes every 2^k samples: the counter's lowest set bit picks the row.
            _counter++;
            int row = System.Numerics.BitOperations.TrailingZeroCount(_counter);
            if (row < Rows)
            {
                _sum -= _rows[row];
                _rows[row] = Uniform();
                _sum += _rows[row];
            }

            return (float)Math.Clamp((_sum + Uniform()) * _scale, -1, 1);
        }

        private double Uniform() => (_random.NextDouble() * 2) - 1;
    }
}

/// <summary>The size, rate and planes of the video <see cref="TestSources"/> generates.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-test-sources"/>
/// </example>
public sealed record TestVideoFormat
{
    /// <summary>Gets the default format: 1280 by 720 at 30 frames a second, 8-bit 4:2:0, BT.709 limited range.</summary>
    public static TestVideoFormat Default { get; } = new();

    /// <summary>Gets the width in pixels. Default 1280.</summary>
    public int Width { get; init; } = 1280;

    /// <summary>Gets the height in pixels. Default 720.</summary>
    public int Height { get; init; } = 720;

    /// <summary>Gets the frame rate. Default 30.</summary>
    public Rational FrameRate { get; init; } = new(30, 1);

    /// <summary>Gets the chroma subsampling. Default 4:2:0.</summary>
    public ChromaFormat ChromaFormat { get; init; } = ChromaFormat.Yuv420;

    /// <summary>Gets the bit depth, from 8 to 16. Default 8.</summary>
    public int BitDepth { get; init; } = 8;

    /// <summary>Gets the matrix, range and chroma siting. Default BT.709 limited range.</summary>
    public YuvColor Color { get; init; } = YuvColor.Bt709Limited;

    /// <summary>Gets the context the frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</summary>
    public MediaContext? Context { get; init; }

    internal void Validate()
    {
        if (Width < 1 || Height < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Width), $"The size must be at least 1 by 1, not {Width} by {Height}.");
        }

        if (FrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(FrameRate), FrameRate, "The frame rate must be above 0.");
        }

        if (BitDepth is < 8 or > 16)
        {
            throw new ArgumentOutOfRangeException(nameof(BitDepth), BitDepth, "The bit depth must be from 8 to 16.");
        }
    }
}

/// <summary>The rate, channels and frame size of the audio <see cref="TestSources"/> generates.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-test-sources"/>
/// </example>
public sealed record TestAudioFormat
{
    /// <summary>Gets the default format: 48 kHz stereo in frames of 1024 samples.</summary>
    public static TestAudioFormat Default { get; } = new();

    /// <summary>Gets the sample rate in hertz. Default 48000.</summary>
    public int SampleRate { get; init; } = 48000;

    /// <summary>Gets the channels. Default stereo.</summary>
    public ChannelLayout Layout { get; init; } = ChannelLayout.Stereo;

    /// <summary>Gets how many samples each frame holds, the last one fewer. Default 1024.</summary>
    public int FrameSize { get; init; } = 1024;

    /// <summary>Gets the context the frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</summary>
    public MediaContext? Context { get; init; }

    internal void Validate()
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(SampleRate, 1, nameof(SampleRate));
        ArgumentOutOfRangeException.ThrowIfLessThan(FrameSize, 1, nameof(FrameSize));
        ArgumentOutOfRangeException.ThrowIfLessThan(Layout.ChannelCount, 1, nameof(Layout));
    }
}

/// <summary>Which color bar layout <see cref="TestSources.ColorBars"/> draws.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-test-sources"/>
/// </example>
public enum ColorBarsStyle
{
    /// <summary>HD bars for pictures 1280 or more wide, SD bars for smaller ones.</summary>
    Auto,

    /// <summary>The SMPTE RP 219 layout for HD and larger.</summary>
    Hd,

    /// <summary>The SMPTE EG 1 layout for SD.</summary>
    Sd,
}

/// <summary>The spectrum of <see cref="TestSources.Noise"/>.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoAnalysis.cs" region="analysis-test-sources"/>
/// </example>
public enum NoiseColor
{
    /// <summary>The same power at every frequency.</summary>
    White,

    /// <summary>The same power in every octave: 3 dB less per octave, as most music and speech.</summary>
    Pink,
}
