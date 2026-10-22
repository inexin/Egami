using System.Diagnostics.CodeAnalysis;
using System.Numerics;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio;

/// <summary>How hard a <see cref="Resampler"/> works: the length and steepness of its filter.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-resample"/>
/// </example>
public enum ResamplerQuality
{
    /// <summary>Passband flat to 0.8 of the lower Nyquist frequency, 60 dB stopband: for previews and speech.</summary>
    Fast,

    /// <summary>Passband flat (under 0.1 dB ripple) to 0.9 of the lower Nyquist frequency, 100 dB stopband: transparent for 16-bit output. The default.</summary>
    Good,

    /// <summary>Passband flat to 0.95 of the lower Nyquist frequency, 140 dB stopband: for 24-bit masters.</summary>
    Best,
}

/// <summary>
/// Converts audio between any two sample rates with a polyphase windowed-sinc filter (Kaiser window). The filter's phases come
/// from the rate ratio reduced to lowest terms; when that needs more than 1024 phases (as for unusual rates), coefficients
/// between 1024 precomputed phases are interpolated. It works in 32-bit float and keeps time exact: the first output sample
/// has the first input sample's timestamp and every later one follows at the output rate, so a resampled track stays in
/// sync. Frames of any length and format go in through <see cref="Send"/>; frames come out through <see cref="TryReceive"/>,
/// in <see cref="OutputFormat"/> and <see cref="OutputLayout"/>, so conversion and channel mixing run in the same pass.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-resample"/>
/// </example>
public sealed class Resampler : IDisposable
{
    private const int MaxPhases = 1024;
    private const int MaxTaps = 1 << 13;

    private readonly MediaContext _context;
    private readonly bool _passthrough;
    private readonly long _up;
    private readonly long _down;
    private readonly int _phases;
    private readonly bool _interpolate;
    private readonly int _taps;
    private readonly int _half;
    private readonly float[] _table;
    private readonly float[][] _history;
    private readonly Queue<AudioFrame> _ready = new();
    private readonly ChannelMixer? _mixer;
    private int _length;
    private long _inputIndex;
    private long _phase;
    private long _produced;
    private long _consumed;
    private long _origin;
    private bool _started;
    private float[] _scratch = [];

    /// <summary>Creates a resampler.</summary>
    /// <param name="inputRate">The input sample rate in hertz.</param>
    /// <param name="outputRate">The output sample rate in hertz.</param>
    /// <param name="layout">The input channels.</param>
    /// <param name="quality">The filter quality. Default <see cref="ResamplerQuality.Good"/>.</param>
    /// <param name="context">The context frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">A rate is not positive, or the layout has no channels.</exception>
    /// <exception cref="UnsupportedFormatException">The rates are so far apart that the filter would be over 8192 taps long (a ratio of about 40 at <see cref="ResamplerQuality.Best"/>).</exception>
    public Resampler(int inputRate, int outputRate, ChannelLayout layout, ResamplerQuality quality = ResamplerQuality.Good, MediaContext? context = null)
        : this(inputRate, outputRate, layout, quality, null, SampleFormat.F32Planar, context)
    {
    }

    /// <summary>Creates a resampler that also mixes to another layout and hands out another sample format.</summary>
    /// <param name="inputRate">The input sample rate in hertz.</param>
    /// <param name="outputRate">The output sample rate in hertz.</param>
    /// <param name="layout">The input channels.</param>
    /// <param name="quality">The filter quality.</param>
    /// <param name="outputLayout">The output channels, mixed with the default <see cref="ChannelMixer"/> matrix, or <see langword="null"/> to keep the input's.</param>
    /// <param name="outputFormat">The output sample format.</param>
    /// <param name="context">The context frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    public Resampler(int inputRate, int outputRate, ChannelLayout layout, ResamplerQuality quality, ChannelLayout? outputLayout, SampleFormat outputFormat, MediaContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(inputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(outputRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(layout.ChannelCount, 1, nameof(layout));
        InputRate = inputRate;
        OutputRate = outputRate;
        Layout = layout;
        Quality = quality;
        OutputLayout = outputLayout ?? layout;
        OutputFormat = outputFormat;
        _context = context ?? MediaContext.Default;
        _mixer = OutputLayout == layout ? null : new ChannelMixer(layout, OutputLayout);
        _history = new float[layout.ChannelCount][];
        long g = (long)BigIntegerGcd(inputRate, outputRate);
        _up = outputRate / g;
        _down = inputRate / g;
        _passthrough = inputRate == outputRate;
        (double passband, double attenuation) = quality switch
        {
            ResamplerQuality.Fast => (0.8, 60.0),
            ResamplerQuality.Best => (0.95, 140.0),
            _ => (0.9, 100.0),
        };

        // Cut-off midway between the passband edge and the lower Nyquist frequency, in cycles per input sample.
        double lower = Math.Min(1.0, (double)outputRate / inputRate);
        double cutoff = 0.5 * lower * ((passband + 1.0) / 2);
        double transition = 0.5 * lower * (1.0 - passband);
        double beta = attenuation > 50 ? 0.1102 * (attenuation - 8.7) : (0.5842 * Math.Pow(attenuation - 21, 0.4)) + (0.07886 * (attenuation - 21));
        double taps = Math.Ceiling((attenuation - 7.95) / (14.36 * transition));
        if (taps > MaxTaps)
        {
            throw new UnsupportedFormatException($"Resampling from {inputRate} Hz to {outputRate} Hz needs a filter of {taps} taps; the most is {MaxTaps}.");
        }

        int length = (int)taps;
        _half = Math.Max(2, (length + 1) / 2);
        _taps = 2 * _half;
        _interpolate = _up > MaxPhases;
        _phases = _interpolate ? MaxPhases : (int)_up;
        int rows = _phases + (_interpolate ? 1 : 0);
        _table = new float[rows * _taps];
        double i0Beta = BesselI0(beta);
        for (int p = 0; p < rows; p++)
        {
            double frac = (double)p / _phases;
            double sum = 0;
            for (int t = 0; t < _taps; t++)
            {
                // Tap t multiplies input sample (i - half + 1 + t); its distance from the output position i + frac:
                double x = t - _half + 1 - frac;
                double w = Math.Abs(x) >= _half ? 0 : BesselI0(beta * Math.Sqrt(1 - Math.Pow(x / _half, 2))) / i0Beta;
                double h = 2 * cutoff * Sinc(2 * cutoff * x) * w;
                _table[(p * _taps) + t] = (float)h;
                sum += h;
            }

            // Unity gain at DC for every phase.
            for (int t = 0; t < _taps && sum != 0; t++)
            {
                _table[(p * _taps) + t] = (float)(_table[(p * _taps) + t] / sum);
            }
        }

        Reset();
    }

    /// <summary>Gets the input sample rate.</summary>
    public int InputRate { get; }

    /// <summary>Gets the output sample rate.</summary>
    public int OutputRate { get; }

    /// <summary>Gets the input channels.</summary>
    public ChannelLayout Layout { get; }

    /// <summary>Gets the output channels.</summary>
    public ChannelLayout OutputLayout { get; }

    /// <summary>Gets the output sample format.</summary>
    public SampleFormat OutputFormat { get; }

    /// <summary>Gets the filter quality.</summary>
    public ResamplerQuality Quality { get; }

    /// <summary>Gets the filter's length in input samples: the delay before the last output sample of a block is final.</summary>
    public int FilterLength => _taps;

    /// <summary>Gives the resampler a frame of the input rate and layout, in any sample format. The caller keeps the frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentException">The frame's rate or channel count differs from the input's.</exception>
    public void Send(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != InputRate || frame.ChannelCount != Layout.ChannelCount)
        {
            throw new ArgumentException($"The resampler takes {Layout.ChannelCount} channels at {InputRate} Hz; the frame has {frame.ChannelCount} at {frame.SampleRate} Hz.", nameof(frame));
        }

        if (!_started)
        {
            _started = true;
            _origin = frame.Timestamp.Rescale(new Rational(1, OutputRate)).Ticks;
        }

        int n = frame.SampleCount;
        if (n == 0)
        {
            return;
        }

        if (_passthrough)
        {
            AudioFrame copy = new(OutputFormat, OutputLayout, OutputRate, n, new MediaTime(_origin + _produced, new Rational(1, OutputRate)), _context);
            frame.CopyTo(copy, new AudioConvertOptions { Mixer = _mixer });
            _produced += n;
            _ready.Enqueue(copy);
            return;
        }

        Append(frame);
        Produce(final: false);
    }

    /// <summary>Tells the resampler no more input comes, so it hands out the samples its filter still holds.</summary>
    public void Flush()
    {
        if (_passthrough || !_started)
        {
            return;
        }

        // Feed the filter's tail zeros so the last input samples reach the output; stop at the input's length in output samples.
        int pad = _half + 1;
        EnsureLength(_length + pad);
        for (int c = 0; c < _history.Length; c++)
        {
            _history[c].AsSpan(_length, pad).Clear();
        }

        _length += pad;
        Produce(final: true);
    }

    /// <summary>Takes the next resampled frame.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    /// <summary>Drops the input held and the frames ready, for example after a seek; the next frame starts the time line again.</summary>
    public void Reset()
    {
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }

        _started = false;
        _produced = 0;
        _consumed = 0;
        _inputIndex = 0;
        _phase = 0;

        // Half a filter of zeros before the first sample, so output sample 0 is centred on input sample 0.
        _length = _half - 1;
        EnsureLength(Math.Max(_length, 4096));
        for (int c = 0; c < _history.Length; c++)
        {
            _history[c].AsSpan(0, _length).Clear();
        }
    }

    /// <summary>Drops the frames ready.</summary>
    public void Dispose()
    {
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    private static double Sinc(double x) => x == 0 ? 1 : Math.Sin(Math.PI * x) / (Math.PI * x);

    private static double BesselI0(double x)
    {
        double sum = 1, term = 1, half = x / 2;
        for (int k = 1; k < 64; k++)
        {
            term *= half / k;
            double t2 = term * term;
            sum += t2;
            if (t2 < sum * 1e-17)
            {
                break;
            }
        }

        return sum;
    }

    private static BigInteger BigIntegerGcd(int a, int b) => BigInteger.GreatestCommonDivisor(a, b);

    private void EnsureLength(int length)
    {
        for (int c = 0; c < _history.Length; c++)
        {
            if (_history[c] is null || _history[c].Length < length)
            {
                float[] grown = new float[Math.Max(length, (_history[c]?.Length ?? 0) * 2)];
                _history[c]?.AsSpan(0, Math.Min(_length, _history[c].Length)).CopyTo(grown);
                _history[c] = grown;
            }
        }
    }

    private void Append(AudioFrame frame)
    {
        int n = frame.SampleCount;
        EnsureLength(_length + n);
        for (int c = 0; c < _history.Length; c++)
        {
            SampleConverter.ReadFloats(frame, c, 0, _history[c].AsSpan(_length, n));
        }

        _length += n;
        _consumed += n;
    }

    private void Produce(bool final)
    {
        // Output sample count the whole input maps to: ceil(consumed * up / down).
        long total = ((_consumed * _up) + _down - 1) / _down;
        int count = 0;
        long index = _inputIndex, phase = _phase;
        while (index + _taps <= _length && (!final || _produced + count < total) && (final || index + _taps <= _length))
        {
            count++;
            phase += _down;
            index += phase / _up;
            phase %= _up;
        }

        if (final)
        {
            count = (int)Math.Min(count, Math.Max(0, total - _produced));
        }

        if (count == 0)
        {
            Compact();
            return;
        }

        int channels = _history.Length;
        if (_scratch.Length < count * channels)
        {
            _scratch = new float[count * channels];
        }

        for (int c = 0; c < channels; c++)
        {
            Span<float> output = _scratch.AsSpan(c * count, count);
            ReadOnlySpan<float> input = _history[c];
            index = _inputIndex;
            phase = _phase;
            for (int k = 0; k < count; k++)
            {
                output[k] = Filter(input.Slice((int)index, _taps), phase);
                phase += _down;
                index += phase / _up;
                phase %= _up;
            }
        }

        _inputIndex = index;
        _phase = phase;
        AudioFrame floats = new(SampleFormat.F32Planar, Layout, OutputRate, count, new MediaTime(_origin + _produced, new Rational(1, OutputRate)), _context);
        for (int c = 0; c < channels; c++)
        {
            _scratch.AsSpan(c * count, count).CopyTo(floats.GetSpan<float>(c));
        }

        _produced += count;
        if (OutputFormat == SampleFormat.F32Planar && _mixer is null)
        {
            _ready.Enqueue(floats);
        }
        else
        {
            using (floats)
            {
                _ready.Enqueue(floats.Convert(OutputFormat, OutputLayout, new AudioConvertOptions { Mixer = _mixer }, _context));
            }
        }

        Compact();
    }

    private float Filter(ReadOnlySpan<float> input, long phase)
    {
        if (!_interpolate)
        {
            return Dot(input, _table.AsSpan((int)phase * _taps, _taps));
        }

        double position = (double)phase / _up * _phases;
        int p = Math.Min((int)position, _phases - 1);
        float weight = (float)(position - p);
        float a = Dot(input, _table.AsSpan(p * _taps, _taps));
        float b = Dot(input, _table.AsSpan((p + 1) * _taps, _taps));
        return a + ((b - a) * weight);
    }

    private static float Dot(ReadOnlySpan<float> x, ReadOnlySpan<float> h)
    {
        int i = 0;
        float sum = 0;
        if (Vector.IsHardwareAccelerated && x.Length >= Vector<float>.Count)
        {
            Vector<float> acc = Vector<float>.Zero;
            ReadOnlySpan<Vector<float>> xs = MemoryMarshal.Cast<float, Vector<float>>(x);
            ReadOnlySpan<Vector<float>> hs = MemoryMarshal.Cast<float, Vector<float>>(h);
            for (int v = 0; v < xs.Length; v++)
            {
                acc += xs[v] * hs[v];
            }

            sum = Vector.Sum(acc);
            i = xs.Length * Vector<float>.Count;
        }

        for (; i < x.Length; i++)
        {
            sum += x[i] * h[i];
        }

        return sum;
    }

    /// <summary>Drops input the filter no longer reaches.</summary>
    private void Compact()
    {
        int drop = (int)Math.Min(_inputIndex, _length);
        if (drop < 4096)
        {
            return;
        }

        for (int c = 0; c < _history.Length; c++)
        {
            _history[c].AsSpan(drop, _length - drop).CopyTo(_history[c]);
        }

        _length -= drop;
        _inputIndex -= drop;
    }
}
