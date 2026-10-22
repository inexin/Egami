using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio;

/// <summary>
/// Re-chunks audio: frames of any length and sample format in, frames of exactly <see cref="FrameSize"/> samples in one format
/// out (the last one shorter), timed by counting samples from the first frame. What encoders cut their packets from.
/// </summary>
internal sealed class AudioChunker : IDisposable
{
    private readonly MediaContext _context;
    private readonly Queue<AudioFrame> _ready = new();
    private AudioFrame? _pending;
    private AudioFrame? _scratch;
    private int _filled;
    private long _origin;
    private long _position;
    private bool _started;

    public AudioChunker(SampleFormat format, int validBits, ChannelLayout layout, int sampleRate, int frameSize, MediaContext context)
    {
        Format = format;
        ValidBits = validBits;
        Layout = layout;
        SampleRate = sampleRate;
        FrameSize = frameSize;
        _context = context;
    }

    public SampleFormat Format { get; }

    public int ValidBits { get; }

    public ChannelLayout Layout { get; }

    public int SampleRate { get; }

    public int FrameSize { get; }

    /// <summary>Gets the total samples taken in so far.</summary>
    public long SamplesIn { get; private set; }

    /// <summary>Gets the time base of the frames handed out: one tick per sample.</summary>
    public Rational TimeBase => new(1, SampleRate);

    public void Push(AudioFrame frame, AudioConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(frame);
        if (frame.SampleRate != SampleRate)
        {
            throw new ArgumentException($"The frame is {frame.SampleRate} Hz; the encoder takes {SampleRate} Hz. Resample it first.", nameof(frame));
        }

        if (frame.Layout != Layout && options?.Mixer is null && frame.ChannelCount != Layout.ChannelCount)
        {
            throw new ArgumentException($"The frame is {frame.Layout}; the encoder takes {Layout}. Mix it first.", nameof(frame));
        }

        if (!_started)
        {
            _started = true;
            _origin = frame.Timestamp.Rescale(TimeBase).Ticks;
        }

        AudioFrame source = frame;
        bool sameLayout = frame.Layout == Layout || frame.ChannelCount == Layout.ChannelCount;
        if (frame.Format != Format || frame.ValidBits != ValidBits || frame.Layout != Layout)
        {
            if (_scratch is null || _scratch.Capacity < frame.SampleCount)
            {
                _scratch?.Dispose();
                _scratch = new AudioFrame(Format, Layout, SampleRate, Math.Max(frame.SampleCount, FrameSize), MediaTime.Zero, _context) { ValidBits = ValidBits };
            }

            _scratch.Resize(frame.SampleCount);
            if (sameLayout && frame.Layout != Layout)
            {
                // Same channel count under another name: take the channels as they are.
                SampleConverter.Convert(frame, _scratch, options?.Dither ?? false);
            }
            else
            {
                frame.CopyTo(_scratch, options);
            }

            source = _scratch;
        }

        int bytes = Format.BytesPerSample() * (Format.IsPlanar() ? 1 : Layout.ChannelCount);
        int offset = 0;
        while (offset < source.SampleCount)
        {
            _pending ??= new AudioFrame(Format, Layout, SampleRate, FrameSize, MediaTime.Zero, _context) { ValidBits = ValidBits };
            int n = Math.Min(FrameSize - _filled, source.SampleCount - offset);
            for (int p = 0; p < source.PlaneCount; p++)
            {
                source.GetBytes(p).Slice(offset * bytes, n * bytes).CopyTo(_pending.GetBytes(p)[(_filled * bytes)..]);
            }

            _filled += n;
            offset += n;
            if (_filled == FrameSize)
            {
                Emit();
            }
        }

        SamplesIn += source.SampleCount;
    }

    /// <summary>Hands out the samples held back as a last, shorter frame.</summary>
    public void Flush()
    {
        if (_filled > 0)
        {
            _pending!.Resize(_filled);
            Emit();
        }
    }

    public bool TryTake([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    public void Dispose()
    {
        _pending?.Dispose();
        _scratch?.Dispose();
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }

    private void Emit()
    {
        AudioFrame frame = _pending!;
        frame.Timestamp = new MediaTime(_origin + _position, TimeBase);
        _position += frame.SampleCount;
        _ready.Enqueue(frame);
        _pending = null;
        _filled = 0;
    }
}
