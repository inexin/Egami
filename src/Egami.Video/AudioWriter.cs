using Egami.Video.Audio;
using Egami.Video.Audio.Codecs;
using Egami.Video.Containers;
using Egami.Video.Containers.Flac;

namespace Egami.Video;

/// <summary>
/// Writes an audio file in a few lines, the twin of <see cref="VideoWriter"/>: encodes each frame and muxes it, picking the
/// container from the file extension. Frames may have any length and sample format; the writer resamples, mixes and converts
/// them to what the codec takes. The rate, layout and bit depth come from the options or the first frame.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-write"/>
/// </example>
public sealed class AudioWriter : IDisposable
{
    private readonly Func<IReadOnlyList<TrackInfo>, IMuxer> _createMuxer;
    private readonly AudioWriterOptions _options;
    private readonly MediaContext _context;
    private readonly AudioCodec _codec;
    private IAudioEncoder? _encoder;
    private IMuxer? _muxer;
    private Resampler? _resampler;
    private bool _finished;

    private AudioWriter(Func<IReadOnlyList<TrackInfo>, IMuxer> createMuxer, AudioCodec codec, AudioWriterOptions options, MediaContext context)
    {
        if (!codec.CanEncode)
        {
            throw new NotSupportedException($"{codec.Name} cannot encode.");
        }

        _createMuxer = createMuxer;
        _codec = codec;
        _options = options;
        _context = context;
    }

    /// <summary>Gets the audio track, once the first frame has been written.</summary>
    public AudioTrackInfo? Track => _encoder?.Track;

    /// <summary>Gets how many samples per channel have been written.</summary>
    public long SampleCount { get; private set; }

    /// <summary>
    /// Creates an audio file, choosing the container by the extension (.wav, .w64, .flac, .mkv, .mka, .mp4, .m4a and so on). An
    /// existing file is replaced. Without a codec in the options, .flac files get FLAC, Ogg (.opus, .ogg, .oga) and WebM files
    /// Opus, and the others PCM.
    /// </summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">The codec, rate, layout, bit depth and level.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The writer. The caller must dispose it, which finishes the file.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container writes the extension.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-write"/>
    /// </example>
    public static AudioWriter Create(string path, AudioWriterOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        context ??= MediaContext.Default;
        ContainerFormat format = MediaFile.FormatForPath(path, context);
        options ??= new AudioWriterOptions();
        AudioCodec codec = options.Codec ?? DefaultCodec(format);
        return new AudioWriter(tracks => MediaFile.CreateMuxer(path, tracks, context, options.Metadata), codec, options, context);
    }

    /// <summary>Creates a writer to a stream in the named container.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="formatNameOrExtension">"WAV", "FLAC", ".mka" and so on.</param>
    /// <param name="options">The codec, rate, layout, bit depth and level.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the writer leaves the stream open.</param>
    /// <returns>The writer. The caller must dispose it, which finishes the file.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-write"/>
    /// </example>
    public static AudioWriter Create(Stream stream, string formatNameOrExtension, AudioWriterOptions? options = null, MediaContext? context = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        context ??= MediaContext.Default;
        options ??= new AudioWriterOptions();
        ContainerFormat format = context.FindContainer(formatNameOrExtension) ?? throw new UnsupportedFormatException($"No registered container writes '{formatNameOrExtension}'.");
        AudioCodec codec = options.Codec ?? DefaultCodec(format);
        return new AudioWriter(tracks => MediaFile.CreateMuxer(stream, formatNameOrExtension, tracks, context, leaveOpen, options.Metadata), codec, options, context);
    }

    /// <summary>Writes a frame. Its samples follow the ones before; its timestamp is not used.</summary>
    /// <param name="frame">The frame, in any format. The writer does not keep or dispose it.</param>
    /// <exception cref="ArgumentException">The frame's rate or channel count differs from earlier frames'.</exception>
    public void Write(AudioFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_encoder is null)
        {
            Start(frame);
        }

        if (_resampler is not null)
        {
            _resampler.Send(frame);
            while (_resampler.TryReceive(out AudioFrame? resampled))
            {
                using (resampled)
                {
                    Encode(resampled);
                }
            }
        }
        else
        {
            Encode(frame);
        }
    }

    /// <summary>Flushes the encoder and finishes the file. Called by <see cref="Dispose"/>.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_encoder is null || _muxer is null)
        {
            return;
        }

        if (_resampler is not null)
        {
            _resampler.Flush();
            while (_resampler.TryReceive(out AudioFrame? resampled))
            {
                using (resampled)
                {
                    Encode(resampled);
                }
            }
        }

        _encoder.Flush();
        Drain();
        if (_muxer is ITrackUpdatable updatable)
        {
            updatable.UpdateTrack(_encoder.Track);
        }

        _muxer.Finish();
    }

    /// <summary>Finishes the file and closes it.</summary>
    public void Dispose()
    {
        try
        {
            Finish();
        }
        finally
        {
            _encoder?.Dispose();
            _resampler?.Dispose();
            _muxer?.Dispose();
        }
    }

    internal static AudioCodec DefaultCodec(ContainerFormat format) =>
        format == FlacFormat.Instance ? FlacCodec.Instance
        : format == OggFormat.Instance || format == MatroskaFormat.WebM ? OpusCodec.Instance
        : PcmCodec.Instance;

    /// <summary>Picks the stored bit depth: the option, else the first frame's valid bits where the codec stores them.</summary>
    internal static int BitsFor(AudioCodec codec, int requested, AudioFrame first)
    {
        if (requested > 0)
        {
            return requested;
        }

        if (first.Format.IsFloat())
        {
            return codec.CodecId == CodecIds.PcmFloat ? first.Format.BitsPerSample() : codec.CodecId == CodecIds.Flac ? 24 : 16;
        }

        int bits = first.ValidBits;
        return codec.CodecId switch
        {
            CodecIds.Flac => Math.Clamp(bits, 4, 32),
            CodecIds.PcmFloat => 32,
            _ => bits <= 8 ? 8 : bits <= 16 ? 16 : bits <= 24 ? 24 : 32,
        };
    }

    private void Start(AudioFrame first)
    {
        int rate = _options.SampleRate ?? first.SampleRate;
        ChannelLayout layout = _options.Layout ?? first.Layout;
        _encoder = _codec.CreateEncoder(
            new AudioEncoderOptions
            {
                SampleRate = rate,
                Layout = layout,
                BitsPerSample = BitsFor(_codec, _options.BitsPerSample, first),
                Level = _options.Level,
                Opus = _options.Opus,
            },
            _context);
        if (rate != first.SampleRate)
        {
            _resampler = new Resampler(first.SampleRate, rate, first.Layout, _options.Quality, layout, SampleFormat.F32Planar, _context);
        }

        _muxer = _createMuxer([_encoder.Track]);
    }

    private void Encode(AudioFrame frame)
    {
        AudioFrame input = frame;
        AudioFrame? converted = null;
        if (frame.Layout != _encoder!.Track.EffectiveLayout && frame.ChannelCount != _encoder.Track.Channels)
        {
            converted = frame.Convert(SampleFormat.F32Planar, _encoder.Track.EffectiveLayout, null, _context);
            input = converted;
        }

        try
        {
            _encoder.Send(input);
            SampleCount += input.SampleCount;
            Drain();
        }
        finally
        {
            converted?.Dispose();
        }
    }

    private void Drain()
    {
        while (_encoder!.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                packet.TrackNumber = _encoder.Track.Number;
                _muxer!.WritePacket(packet);
            }
        }
    }
}

/// <summary>What an <see cref="AudioWriter"/> writes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-write"/>
/// </example>
public sealed record AudioWriterOptions
{
    /// <summary>Gets the codec, or <see langword="null"/> for FLAC in .flac files, Opus in Ogg and WebM files and PCM elsewhere.</summary>
    public AudioCodec? Codec { get; init; }

    /// <summary>
    /// Gets the sample rate to write, or <see langword="null"/> for the first frame's; other rates are resampled. Opus codes 8,
    /// 12, 16, 24 and 48 kHz; other rates are resampled to 48 kHz, and the header records the rate asked for.
    /// </summary>
    public int? SampleRate { get; init; }

    /// <summary>Gets the layout to write, or <see langword="null"/> for the first frame's; other layouts are mixed.</summary>
    public ChannelLayout? Layout { get; init; }

    /// <summary>
    /// Gets the bits per sample stored, or 0 to follow the first frame: its valid bits for integer samples (16 for 16-bit, 24 for
    /// 24-bit in S32), and for float samples float PCM of the same size, 24-bit FLAC, or 16-bit integer PCM.
    /// </summary>
    public int BitsPerSample { get; init; }

    /// <summary>Gets the compression level, or <see langword="null"/> for the codec's default (FLAC: 0 to 8, default 5).</summary>
    public int? Level { get; init; }

    /// <summary>Gets how Opus codes, or <see langword="null"/> for <see cref="OpusEncoderOptions.Default"/>.</summary>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-opus-encode"/>
    /// </example>
    public OpusEncoderOptions? Opus { get; init; }

    /// <summary>Gets the resampler's quality, when the rate changes. Default <see cref="ResamplerQuality.Good"/>.</summary>
    public ResamplerQuality Quality { get; init; } = ResamplerQuality.Good;

    /// <summary>Gets the tags, chapters and attachments to write, where the container can hold them.</summary>
    public MediaMetadata Metadata { get; init; } = MediaMetadata.Empty;
}
