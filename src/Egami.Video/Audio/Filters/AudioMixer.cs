using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Filters;

/// <summary>When an <see cref="AudioMixer"/>'s output ends.</summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-mixer"/>
/// </example>
public enum MixDuration
{
    /// <summary>When the last input ends; inputs that end sooner are silence from then on. The default.</summary>
    Longest,

    /// <summary>When the first input to end ends.</summary>
    Shortest,

    /// <summary>When input 0 ends.</summary>
    First,
}

/// <summary>
/// Mixes several streams into one by adding them, each times its weight, like ffmpeg's <c>amix</c> with
/// <c>normalize=0</c>. Inputs are placed on the output by their timestamps, so one that starts later comes in at its time;
/// a gap inside an input is silence. Inputs at another rate or with other channels are resampled with <see cref="Resampler"/>
/// and remixed with <see cref="ChannelMixer"/>'s default matrix on the way in. The sum is not divided by the number of inputs
/// unless <see cref="Normalize"/> is set, so a voice over music keeps its level; put a limiter after it if the sum can clip.
/// Send each input's frames with <see cref="Send"/> and end each with <see cref="Complete"/>: output waits until every input
/// has either sent something or ended.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-mixer"/>
/// </example>
public sealed class AudioMixer : IDisposable
{
    private const int Chunk = 4096;

    private readonly MediaContext _context;
    private readonly Input[] _inputs;
    private readonly Queue<AudioFrame> _ready = new();
    private readonly int _ramp;
    private long _position;
    private bool _started;
    private bool _ended;

    /// <summary>Creates a mixer.</summary>
    /// <param name="sampleRate">The output sample rate in hertz.</param>
    /// <param name="layout">The output channels.</param>
    /// <param name="inputs">The number of inputs, at least 1.</param>
    /// <param name="context">The context frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive, the layout has no channels, or there are no inputs.</exception>
    public AudioMixer(int sampleRate, ChannelLayout layout, int inputs, MediaContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(layout.ChannelCount, 1, nameof(layout));
        ArgumentOutOfRangeException.ThrowIfLessThan(inputs, 1);
        SampleRate = sampleRate;
        Layout = layout;
        _context = context ?? MediaContext.Default;
        _ramp = ParameterRamp.Length(sampleRate);
        _inputs = new Input[inputs];
        for (int i = 0; i < inputs; i++)
        {
            _inputs[i] = new Input(layout.ChannelCount);
        }
    }

    /// <summary>Gets the output sample rate.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the output channels.</summary>
    public ChannelLayout Layout { get; }

    /// <summary>Gets the number of inputs.</summary>
    public int InputCount => _inputs.Length;

    /// <summary>Gets whether to divide the sum by the sum of the weights, so the mix cannot be louder than its loudest input. Off by default.</summary>
    public bool Normalize { get; init; }

    /// <summary>Gets when the output ends. Default <see cref="MixDuration.Longest"/>.</summary>
    public MixDuration Duration { get; init; }

    /// <summary>Returns an input's weight.</summary>
    /// <param name="input">The input.</param>
    /// <returns>The linear weight; 1 unless set.</returns>
    public double GetWeight(int input) => _inputs[CheckInput(input)].Weight.Target;

    /// <summary>Sets an input's weight, its linear gain in the mix. A change after mixing has started moves over 5 ms.</summary>
    /// <param name="input">The input.</param>
    /// <param name="weight">The linear weight: 1 as it is, 0.5 at half level, 0 muted.</param>
    /// <exception cref="ArgumentOutOfRangeException">The input does not exist, or the weight is NaN or infinite.</exception>
    public void SetWeight(int input, double weight)
    {
        CheckInput(input);
        if (!double.IsFinite(weight))
        {
            throw new ArgumentOutOfRangeException(nameof(weight), weight, "The weight must be a finite number.");
        }

        _inputs[input].Weight.Set(weight, _started ? _ramp : 0);
    }

    /// <summary>Gives a frame of one input, in any sample format, rate and channels. The caller keeps the frame.</summary>
    /// <param name="input">The input.</param>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentOutOfRangeException">The input does not exist.</exception>
    /// <exception cref="ArgumentException">The frame's rate or channels differ from the input's first frame.</exception>
    /// <exception cref="InvalidOperationException">The input has been completed.</exception>
    public void Send(int input, AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Input state = _inputs[CheckInput(input)];
        if (state.Done)
        {
            throw new InvalidOperationException($"Input {input} has been completed.");
        }

        if (state.Rate == 0)
        {
            state.Rate = frame.SampleRate;
            state.Layout = frame.Layout;
            if (frame.SampleRate != SampleRate)
            {
                state.Resampler = new Resampler(frame.SampleRate, SampleRate, frame.Layout, ResamplerQuality.Good, Layout, SampleFormat.F32Planar, _context);
            }
            else if (frame.Layout != Layout)
            {
                state.Mixer = new ChannelMixer(frame.Layout, Layout);
            }
        }
        else if (frame.SampleRate != state.Rate || frame.Layout != state.Layout)
        {
            throw new ArgumentException($"Input {input} runs at {state.Rate} Hz with {state.Layout}; the frame has {frame.SampleRate} Hz and {frame.Layout}.", nameof(frame));
        }

        if (frame.SampleCount == 0)
        {
            return;
        }

        if (state.Resampler is not null)
        {
            state.Resampler.Send(frame);
            TakeResampled(state);
        }
        else if (frame.Format == SampleFormat.F32Planar && state.Mixer is null)
        {
            Place(state, frame);
        }
        else
        {
            using AudioFrame converted = frame.Convert(SampleFormat.F32Planar, Layout, new AudioConvertOptions { Mixer = state.Mixer }, _context);
            Place(state, converted);
        }

        Produce();
    }

    /// <summary>Ends one input.</summary>
    /// <param name="input">The input.</param>
    public void Complete(int input)
    {
        Input state = _inputs[CheckInput(input)];
        if (state.Done)
        {
            return;
        }

        if (state.Resampler is not null)
        {
            state.Resampler.Flush();
            TakeResampled(state);
        }

        state.Done = true;
        Produce();
    }

    /// <summary>Ends every input, so the rest of the mix comes out.</summary>
    public void Flush()
    {
        for (int i = 0; i < _inputs.Length; i++)
        {
            Complete(i);
        }
    }

    /// <summary>Takes the next mixed frame.</summary>
    /// <param name="frame">The frame, F32 planar, which the caller must dispose.</param>
    /// <returns>Whether a frame was ready.</returns>
    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    /// <summary>Drops everything held, for example after a seek; the inputs start again. The weights stay.</summary>
    public void Reset()
    {
        Drain();
        foreach (Input state in _inputs)
        {
            state.Clear();
        }

        _started = false;
        _ended = false;
        _position = 0;
    }

    /// <summary>Drops the frames ready.</summary>
    public void Dispose()
    {
        Drain();
        foreach (Input state in _inputs)
        {
            state.Resampler?.Dispose();
        }
    }

    private int CheckInput(int input)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(input);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(input, _inputs.Length);
        return input;
    }

    private void TakeResampled(Input state)
    {
        while (state.Resampler!.TryReceive(out AudioFrame? output))
        {
            using (output)
            {
                Place(state, output);
            }
        }
    }

    private void Place(Input state, AudioFrame frame)
    {
        long index = frame.Timestamp.Rescale(new Rational(1, SampleRate)).Ticks;
        if (state.Start is null)
        {
            state.Start = index;
        }
        else if (index > state.End)
        {
            // A gap in the input: silence until the frame's time. Earlier or overlapping frames run on without a gap.
            state.Buffer.AppendSilence((int)Math.Min(index - state.End, int.MaxValue / 2));
        }

        state.Buffer.Append(frame);
    }

    private void Produce()
    {
        if (_ended)
        {
            return;
        }

        if (!_started)
        {
            long? origin = null;
            foreach (Input state in _inputs)
            {
                if (state.Start is null && !state.Done)
                {
                    return;
                }

                if (state.Start is long start)
                {
                    origin = origin is null ? start : Math.Min(origin.Value, start);
                }
            }

            if (origin is null)
            {
                return;
            }

            _started = true;
            _position = origin.Value;
        }

        // How far every input's samples are known: the end of what an open input has sent, and at most where the mix ends.
        long known = long.MaxValue, last = long.MinValue, earliestEnd = long.MaxValue;
        bool allDone = true;
        foreach (Input state in _inputs)
        {
            long end = state.Start is null ? long.MinValue : state.End;
            if (!state.Done)
            {
                allDone = false;
                known = Math.Min(known, state.Start is null ? _position : end);
            }
            else
            {
                earliestEnd = Math.Min(earliestEnd, end);
            }

            last = Math.Max(last, end);
        }

        long stop = Duration switch
        {
            MixDuration.Shortest => earliestEnd,
            MixDuration.First => _inputs[0].Done ? (_inputs[0].Start is null ? _position : _inputs[0].End) : long.MaxValue,
            _ => allDone ? last : long.MaxValue,
        };

        long target = Math.Min(known, stop);
        if (allDone && Duration == MixDuration.Longest)
        {
            target = last;
        }

        while (_position < target)
        {
            int n = (int)Math.Min(target - _position, Chunk);
            Mix(n);
        }

        if (_position >= stop)
        {
            _ended = true;
        }
    }

    private void Mix(int n)
    {
        AudioFrame frame = new(SampleFormat.F32Planar, Layout, SampleRate, n, new MediaTime(_position, new Rational(1, SampleRate)), _context);
        double norm = 1;
        if (Normalize)
        {
            double sum = 0;
            foreach (Input state in _inputs)
            {
                sum += Math.Abs(state.Weight.Target);
            }

            norm = sum > 0 ? 1 / sum : 1;
        }

        foreach (Input state in _inputs)
        {
            if (state.Start is not long start)
            {
                continue;
            }

            // The part of this input that falls in [position, position + n).
            int skip = (int)Math.Max(0, start - _position);
            int offset = (int)Math.Max(0, _position - start);
            int count = (int)Math.Clamp(state.Buffer.Count - offset, 0, n - skip);
            if (count > 0)
            {
                if (state.Weight.IsSteady)
                {
                    float gain = (float)(state.Weight.Value * norm);
                    for (int c = 0; c < Layout.ChannelCount; c++)
                    {
                        SampleMath.AddScaled(frame.GetSpan<float>(c).Slice(skip, count), state.Buffer[c].Slice(offset, count), gain);
                    }
                }
                else
                {
                    float[] gains = state.Gains(count);
                    for (int i = 0; i < count; i++)
                    {
                        gains[i] = (float)(state.Weight.Next() * norm);
                    }

                    for (int c = 0; c < Layout.ChannelCount; c++)
                    {
                        SampleMath.AddMultiplied(frame.GetSpan<float>(c).Slice(skip, count), state.Buffer[c].Slice(offset, count), gains.AsSpan(0, count));
                    }
                }
            }

            // Drop what the mix has passed.
            int passed = (int)Math.Clamp(_position + n - start, 0, state.Buffer.Count);
            state.Buffer.Drop(passed);
            state.Start = start + passed;
        }

        _position += n;
        _ready.Enqueue(frame);
    }

    private void Drain()
    {
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    private sealed class Input(int channels)
    {
        private float[] _gains = [];

        public SampleBuffer Buffer { get; } = new(channels);

        public ParameterRamp Weight = new(1);

        public int Rate { get; set; }

        public ChannelLayout Layout { get; set; }

        public Resampler? Resampler { get; set; }

        public ChannelMixer? Mixer { get; set; }

        /// <summary>Gets or sets the output sample index of the first sample held, or <see langword="null"/> before the first frame.</summary>
        public long? Start { get; set; }

        public long End => (Start ?? 0) + Buffer.Count;

        public bool Done { get; set; }

        public float[] Gains(int count)
        {
            if (_gains.Length < count)
            {
                _gains = new float[count];
            }

            return _gains;
        }

        public void Clear()
        {
            Buffer.Clear();
            Resampler?.Reset();
            Start = null;
            Done = false;
        }
    }
}
