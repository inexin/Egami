namespace Egami.Video.Audio.Filters;

/// <summary>
/// Runs several filters on one input in a single pass: each frame is converted to <see cref="SampleFormat.F32Planar"/> once,
/// then handed from filter to filter in order, in place where the filter keeps the length. The chain is itself an
/// <see cref="IAudioFilter"/>, owns the filters added to it and disposes them with itself.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../../tests/Egami.DocSamples/AudioFilters.cs" region="audio-filter-chain"/>
/// </example>
public sealed class AudioFilterChain : AudioFilter
{
    private readonly List<IAudioFilter> _filters = [];

    /// <summary>Creates an empty chain for one stream.</summary>
    /// <param name="sampleRate">The sample rate of the input in hertz.</param>
    /// <param name="layout">The channels of the input.</param>
    /// <param name="context">The context frames are allocated from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The rate is not positive, or the layout has no channels.</exception>
    public AudioFilterChain(int sampleRate, ChannelLayout layout, MediaContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(layout.ChannelCount, 1, nameof(layout));
        InputRate = sampleRate;
        InputLayout = layout;
        Context = context ?? MediaContext.Default;
    }

    /// <summary>Gets the sample rate the chain takes.</summary>
    public int InputRate { get; }

    /// <summary>Gets the channels the chain takes.</summary>
    public ChannelLayout InputLayout { get; }

    /// <summary>Gets the filters, in the order they run.</summary>
    public IReadOnlyList<IAudioFilter> Filters => _filters;

    private protected override long LatencySamples
    {
        get
        {
            double seconds = 0;
            foreach (IAudioFilter filter in _filters)
            {
                seconds += filter.Latency.Seconds;
            }

            return (long)Math.Round(seconds * SampleRate);
        }
    }

    /// <summary>Adds a filter at the end of the chain. The chain now owns it.</summary>
    /// <param name="filter">The filter.</param>
    /// <returns>This chain, to add more.</returns>
    public AudioFilterChain Add(IAudioFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        if (ReferenceEquals(filter, this) || _filters.Contains(filter))
        {
            throw new ArgumentException("A filter can be in a chain only once.", nameof(filter));
        }

        if (filter is AudioFilter own)
        {
            own.Context = Context;
        }

        _filters.Add(filter);
        return this;
    }

    /// <inheritdoc/>
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            foreach (IAudioFilter filter in _filters)
            {
                filter.Dispose();
            }
        }

        base.Dispose(disposing);
    }

    private protected override void OnStart()
    {
        if (SampleRate != InputRate || Layout != InputLayout)
        {
            throw new ArgumentException($"The chain takes {InputRate} Hz with {InputLayout}; the frame has {SampleRate} Hz and {Layout}.", "frame");
        }
    }

    private protected override void Process(AudioFrame frame) => Push(0, frame);

    private protected override void OnFlush()
    {
        for (int i = 0; i < _filters.Count; i++)
        {
            _filters[i].Flush();
            Drain(i);
        }
    }

    private protected override void OnReset()
    {
        foreach (IAudioFilter filter in _filters)
        {
            filter.Reset();
        }
    }

    private void Push(int stage, AudioFrame frame)
    {
        if (stage == _filters.Count)
        {
            Emit(frame);
            return;
        }

        IAudioFilter filter = _filters[stage];
        if (filter is AudioFilter own)
        {
            own.SendOwned(frame);
        }
        else
        {
            using (frame)
            {
                filter.Send(frame);
            }
        }

        Drain(stage);
    }

    private void Drain(int stage)
    {
        while (_filters[stage].TryReceive(out AudioFrame? output))
        {
            Push(stage + 1, output);
        }
    }
}
