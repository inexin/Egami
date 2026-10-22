using System.Buffers;
using System.Runtime.InteropServices;

namespace Egami.Video.Audio;

/// <summary>
/// A block of decoded audio: <see cref="SampleCount"/> samples for every channel of its <see cref="Layout"/>, stored as its
/// <see cref="Format"/> says, with the time of the first sample. The audio twin of <see cref="VideoFrame"/>: decoders hand
/// frames out, encoders take them, and the memory comes from the context's memory source, so dispose each frame.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-frames"/>
/// </example>
public sealed class AudioFrame : IDisposable
{
    private const int PlaneAlignment = 64;

    private IMemoryOwner<byte>? _owner;
    private readonly int _planeStride;
    private readonly int _capacity;
    private int _start;

    /// <summary>Creates a frame and allocates its samples from the context's memory source. The samples start as silence.</summary>
    /// <param name="format">How the samples are stored.</param>
    /// <param name="layout">The channels.</param>
    /// <param name="sampleRate">The sample rate in hertz.</param>
    /// <param name="sampleCount">The number of samples per channel.</param>
    /// <param name="timestamp">The time of the first sample.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <exception cref="ArgumentOutOfRangeException">The layout has no channels, or the rate or count is out of range.</exception>
    public AudioFrame(SampleFormat format, ChannelLayout layout, int sampleRate, int sampleCount, MediaTime timestamp, MediaContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(layout.ChannelCount, 1, nameof(layout));
        ArgumentOutOfRangeException.ThrowIfLessThan(sampleRate, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(sampleCount);
        int bytes = format.BytesPerSample();
        Format = format;
        Layout = layout;
        SampleRate = sampleRate;
        SampleCount = sampleCount;
        Timestamp = timestamp;
        ValidBits = format.IsFloat() ? 0 : bytes * 8;
        long planeBytes = format.IsPlanar() ? (long)sampleCount * bytes : (long)sampleCount * bytes * layout.ChannelCount;
        long stride = (planeBytes + PlaneAlignment - 1) / PlaneAlignment * PlaneAlignment;
        long total = format.IsPlanar() ? stride * layout.ChannelCount : planeBytes;
        if (total > int.MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), "The frame would be larger than 2 GB.");
        }

        _planeStride = (int)stride;
        _capacity = sampleCount;
        _owner = (context ?? MediaContext.Default).Egami.Memory.Allocate(Math.Max((int)total, 1), ClearMode.None);
        if (format.ToInterleaved() == SampleFormat.U8)
        {
            _owner.Memory.Span[..(int)total].Fill(128);
        }
        else
        {
            _owner.Memory.Span[..(int)total].Clear();
        }
    }

    /// <summary>Gets the time of the first sample.</summary>
    public MediaTime Timestamp { get; set; }

    /// <summary>Gets how long the frame lasts: <see cref="SampleCount"/> over <see cref="SampleRate"/>.</summary>
    public MediaTime Duration => new(SampleCount, new Rational(1, SampleRate));

    /// <summary>Gets the sample rate in hertz.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the number of samples per channel.</summary>
    public int SampleCount { get; private set; }

    /// <summary>Gets the channels and the speakers they feed.</summary>
    public ChannelLayout Layout { get; }

    /// <summary>Gets the number of channels.</summary>
    public int ChannelCount => Layout.ChannelCount;

    /// <summary>Gets how the samples are stored.</summary>
    public SampleFormat Format { get; }

    /// <summary>
    /// Gets how many low bits of each integer sample are used, such as 24 for 24-bit PCM held in <see cref="SampleFormat.S32"/>:
    /// values then run from -2^23 to 2^23 - 1. The full sample size by default; 0 for float formats.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is below 1 or above the sample size, or the format is float.</exception>
    public int ValidBits
    {
        get;
        init
        {
            if (Format.IsFloat() ? value != 0 : value < 1 || value > Format.BitsPerSample())
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Valid bits must be between 1 and the sample size, and 0 for float samples.");
            }

            field = value;
        }
    }

    /// <summary>Gets the number of buffers: one per channel for planar formats, otherwise one.</summary>
    public int PlaneCount => Format.IsPlanar() ? ChannelCount : 1;

    /// <summary>
    /// Returns the samples of a channel (planar formats) or of every channel interleaved (interleaved formats, plane 0).
    /// <typeparamref name="T"/> must have the sample size: <see cref="byte"/>, <see cref="short"/>, <see cref="int"/>,
    /// <see cref="long"/>, <see cref="float"/> or <see cref="double"/>.
    /// </summary>
    /// <typeparam name="T">The sample type.</typeparam>
    /// <param name="channel">The channel for planar formats; 0 for interleaved ones.</param>
    /// <returns>The samples.</returns>
    /// <exception cref="ArgumentException"><typeparamref name="T"/> has the wrong size.</exception>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a plane of the frame.</exception>
    public ReadOnlySpan<T> AsSpan<T>(int channel = 0)
        where T : unmanaged => GetSpan<T>(channel);

    /// <summary>Returns the samples of a channel or of the interleaved buffer for writing. See <see cref="AsSpan{T}(int)"/>.</summary>
    /// <typeparam name="T">The sample type.</typeparam>
    /// <param name="channel">The channel for planar formats; 0 for interleaved ones.</param>
    /// <returns>The samples.</returns>
    public Span<T> GetSpan<T>(int channel = 0)
        where T : unmanaged
    {
        if (Marshal.SizeOf<T>() != Format.BytesPerSample())
        {
            throw new ArgumentException($"{typeof(T).Name} does not match {Format} samples.", nameof(T));
        }

        return MemoryMarshal.Cast<byte, T>(GetBytes(channel));
    }

    /// <summary>Returns the bytes of a channel (planar formats) or of the interleaved buffer for reading or writing.</summary>
    /// <param name="channel">The channel for planar formats; 0 for interleaved ones.</param>
    /// <returns>The bytes.</returns>
    /// <exception cref="ArgumentOutOfRangeException"><paramref name="channel"/> is not a plane of the frame.</exception>
    /// <exception cref="ObjectDisposedException">The frame has been disposed.</exception>
    public Span<byte> GetBytes(int channel = 0)
    {
        ObjectDisposedException.ThrowIf(_owner is null, this);
        ArgumentOutOfRangeException.ThrowIfNegative(channel);
        ArgumentOutOfRangeException.ThrowIfGreaterThanOrEqual(channel, PlaneCount);
        int plane = channel;
        int bytes = Format.BytesPerSample();
        int perSample = Format.IsPlanar() ? bytes : bytes * ChannelCount;
        return _owner.Memory.Span.Slice((plane * _planeStride) + (_start * perSample), SampleCount * perSample);
    }

    /// <summary>
    /// Copies the samples into another frame of the same sample count, converting the sample format and, when the layouts
    /// differ, mixing the channels with <see cref="ChannelMixer"/>'s default matrix. Use a <see cref="Resampler"/> to change
    /// the rate.
    /// </summary>
    /// <param name="destination">The frame to fill.</param>
    /// <param name="options">The dither and mix settings, or <see langword="null"/> for none and the default matrix.</param>
    /// <exception cref="ArgumentException">The destination has another sample count or rate.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-convert"/>
    /// </example>
    public void CopyTo(AudioFrame destination, AudioConvertOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(destination);
        if (destination.SampleCount != SampleCount || destination.SampleRate != SampleRate)
        {
            throw new ArgumentException("The destination must have the same sample count and rate.", nameof(destination));
        }

        options ??= AudioConvertOptions.Default;
        if (destination.Layout == Layout)
        {
            SampleConverter.Convert(this, destination, options.Dither);
            return;
        }

        ChannelMixer mixer = options.Mixer ?? new ChannelMixer(Layout, destination.Layout);
        mixer.Mix(this, destination, options.Dither);
    }

    /// <summary>Returns a copy in another sample format and, optionally, another layout. See <see cref="CopyTo"/>.</summary>
    /// <param name="format">The sample format.</param>
    /// <param name="layout">The layout, or <see langword="null"/> to keep this one.</param>
    /// <param name="options">The dither and mix settings.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The new frame. The caller must dispose it.</returns>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-convert"/>
    /// </example>
    public AudioFrame Convert(SampleFormat format, ChannelLayout? layout = null, AudioConvertOptions? options = null, MediaContext? context = null)
    {
        AudioFrame copy = new(format, layout ?? Layout, SampleRate, SampleCount, Timestamp, context);
        try
        {
            CopyTo(copy, options);
            return copy;
        }
        catch
        {
            copy.Dispose();
            throw;
        }
    }

    /// <summary>Returns a copy of part of the frame, timed from its first sample.</summary>
    /// <param name="start">The first sample to copy.</param>
    /// <param name="count">The number of samples.</param>
    /// <param name="context">The context to allocate from, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The new frame. The caller must dispose it.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The range is not inside the frame.</exception>
    public AudioFrame Slice(int start, int count, MediaContext? context = null)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, SampleCount - start);
        AudioFrame copy = new(Format, Layout, SampleRate, count, TimeOf(start), context) { ValidBits = ValidBits };
        int bytes = Format.BytesPerSample() * (Format.IsPlanar() ? 1 : ChannelCount);
        for (int p = 0; p < PlaneCount; p++)
        {
            GetBytes(p).Slice(start * bytes, count * bytes).CopyTo(copy.GetBytes(p));
        }

        return copy;
    }

    /// <summary>Returns the frame's memory to the memory source. The samples must not be used afterwards.</summary>
    public void Dispose()
    {
        IMemoryOwner<byte>? owner = _owner;
        _owner = null;
        owner?.Dispose();
    }

    /// <inheritdoc/>
    public override string ToString() => $"{SampleCount} samples, {SampleRate} Hz, {Layout}, {Format} at {Timestamp}";

    /// <summary>Returns the time of a sample of the frame.</summary>
    internal MediaTime TimeOf(int sample) => sample == 0 ? Timestamp : Timestamp + new MediaTime(sample, new Rational(1, SampleRate));

    /// <summary>Sets the sample count to any value up to the count the frame was made with, from the first allocated sample.</summary>
    internal void Resize(int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, _capacity);
        _start = 0;
        SampleCount = count;
    }

    /// <summary>Gets the sample count the frame was made with.</summary>
    internal int Capacity => _capacity;

    /// <summary>Drops samples from both ends without copying, moving the timestamp to the new first sample.</summary>
    internal void Trim(int start, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(count, SampleCount - start);
        Timestamp = TimeOf(start);
        _start += start;
        SampleCount = count;
    }
}

/// <summary>How <see cref="AudioFrame.CopyTo"/> converts samples and channels.</summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Audio.cs" region="audio-convert"/>
/// </example>
public sealed record AudioConvertOptions
{
    /// <summary>Gets the defaults: no dither, the default mix matrix.</summary>
    public static AudioConvertOptions Default { get; } = new();

    /// <summary>
    /// Gets whether to add triangular (TPDF) dither of one step of the target when going down in bit depth, such as float or
    /// 24-bit to 16-bit, which turns rounding distortion into steady low-level noise. Off by default, which rounds.
    /// </summary>
    public bool Dither { get; init; }

    /// <summary>Gets the mixer used when the layouts differ, or <see langword="null"/> for the default matrix between them.</summary>
    public ChannelMixer? Mixer { get; init; }
}
