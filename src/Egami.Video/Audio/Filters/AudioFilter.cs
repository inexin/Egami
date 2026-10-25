using System.Diagnostics.CodeAnalysis;

namespace Egami.Video.Audio.Filters;

/// <summary>
/// The base of Egami.Video's own audio filters. It takes the rate and channels from the first frame, converts every frame to
/// <see cref="SampleFormat.F32Planar"/> once on the way in, and queues the output. Inside an <see cref="AudioFilterChain"/>
/// the converted frames pass from filter to filter without another copy, and filters that keep the length (gain, fades, pan)
/// work on them in place. To write a filter of your own, implement <see cref="IAudioFilter"/>.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-filter-chain"/>
/// </example>
public abstract class AudioFilter : IAudioFilter
{
    private readonly Queue<AudioFrame> _ready = new();

    private protected AudioFilter()
    {
    }

    /// <inheritdoc/>
    public MediaTime Latency => SampleRate == 0 ? MediaTime.Zero : new MediaTime(LatencySamples, new Rational(1, SampleRate));

    /// <summary>Gets the sample rate of the stream, or 0 before the first frame.</summary>
    private protected int SampleRate { get; private set; }

    /// <summary>Gets the input channels of the stream; meaningful after the first frame.</summary>
    private protected ChannelLayout Layout { get; private set; }

    /// <summary>Gets the lookahead in samples; see <see cref="Latency"/>.</summary>
    private protected virtual long LatencySamples => 0;

    /// <summary>Gets or sets the context output frames are allocated from. An <see cref="AudioFilterChain"/> sets its own.</summary>
    internal MediaContext Context { get; set; } = MediaContext.Default;

    /// <summary>Gives the filter a frame of any sample format. The caller keeps the frame.</summary>
    /// <param name="frame">The frame.</param>
    /// <exception cref="ArgumentException">The frame's rate or channels differ from the stream's first frame.</exception>
    public void Send(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Bind(frame);
        if (frame.SampleCount == 0)
        {
            return;
        }

        AudioFrame copy = new(SampleFormat.F32Planar, frame.Layout, frame.SampleRate, frame.SampleCount, frame.Timestamp, Context);
        try
        {
            frame.CopyTo(copy);
        }
        catch
        {
            copy.Dispose();
            throw;
        }

        Process(copy);
    }

    /// <inheritdoc/>
    public bool TryReceive([NotNullWhen(true)] out AudioFrame? frame) => _ready.TryDequeue(out frame);

    /// <inheritdoc/>
    public void Flush()
    {
        if (SampleRate != 0)
        {
            OnFlush();
        }
    }

    /// <inheritdoc/>
    public void Reset()
    {
        Drain();
        if (SampleRate != 0)
        {
            OnReset();
        }
    }

    /// <summary>Drops the frames ready.</summary>
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>Gives the filter an F32 planar frame it now owns: the chain's path, with no copy.</summary>
    internal void SendOwned(AudioFrame frame)
    {
        try
        {
            Bind(frame);
        }
        catch
        {
            frame.Dispose();
            throw;
        }

        if (frame.SampleCount == 0)
        {
            frame.Dispose();
            return;
        }

        Process(frame);
    }

    /// <summary>Drops the frames ready and anything else the filter holds.</summary>
    /// <param name="disposing">Whether this is called from <see cref="Dispose()"/>.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (disposing)
        {
            Drain();
        }
    }

    /// <summary>Filters an F32 planar frame of the stream's rate and channels, which the filter now owns.</summary>
    private protected abstract void Process(AudioFrame frame);

    /// <summary>Sets up for the stream's rate and channels on the first frame.</summary>
    private protected virtual void OnStart()
    {
    }

    /// <summary>Hands out what the filter holds at the end of the input.</summary>
    private protected virtual void OnFlush()
    {
    }

    /// <summary>Drops what the filter holds; the next frame starts the stream again.</summary>
    private protected virtual void OnReset()
    {
    }

    /// <summary>Queues a frame for <see cref="TryReceive"/>.</summary>
    private protected void Emit(AudioFrame frame) => _ready.Enqueue(frame);

    /// <summary>Returns the index of a frame's first sample on the stream's sample clock.</summary>
    private protected long IndexOf(MediaTime time) => time.Rescale(new Rational(1, SampleRate)).Ticks;

    /// <summary>Returns the time of a sample index on the stream's sample clock.</summary>
    private protected MediaTime TimeAt(long index) => new(index, new Rational(1, SampleRate));

    /// <summary>Allocates an F32 planar output frame of the given channels.</summary>
    private protected AudioFrame NewFrame(ChannelLayout layout, int count, long index) =>
        new(SampleFormat.F32Planar, layout, SampleRate, count, TimeAt(index), Context);

    private void Bind(AudioFrame frame)
    {
        if (SampleRate == 0)
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(frame.SampleRate, 1, nameof(frame));
            SampleRate = frame.SampleRate;
            Layout = frame.Layout;
            OnStart();
            return;
        }

        if (frame.SampleRate != SampleRate || frame.Layout != Layout)
        {
            throw new ArgumentException($"The filter runs at {SampleRate} Hz with {Layout}; the frame has {frame.SampleRate} Hz and {frame.Layout}.", nameof(frame));
        }
    }

    private void Drain()
    {
        while (_ready.TryDequeue(out AudioFrame? frame))
        {
            frame.Dispose();
        }
    }
}
