using System.Diagnostics.CodeAnalysis;

using Egami.Video.Audio;
using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>
/// Reads the audio of a file in a few lines, the twin of <see cref="VideoReader"/>: opens the container, picks the audio track,
/// decodes it and hands out <see cref="AudioFrame"/>s, converted to the sample format, rate and layout asked for (planar float
/// by default). Codec delay and padding are trimmed, so the audio has exactly the samples the encoder was given; seeking is
/// sample exact.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-read"/>
/// </example>
public sealed class AudioReader : IDisposable
{
    private readonly IDemuxer _demuxer;
    private readonly IAudioDecoder _decoder;
    private readonly MediaContext _context;
    private readonly AudioReaderOptions _options;
    private readonly HashSet<MediaTime> _discard = [];
    private Resampler? _resampler;
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _seekTarget;
    private MediaTime? _emittedEnd;
    private bool _endOfStream;
    private bool _flushed;

    private AudioReader(IDemuxer demuxer, AudioReaderOptions? options, MediaContext context)
    {
        _demuxer = demuxer;
        _context = context;
        _options = options ?? AudioReaderOptions.Default;
        Track = (_options.TrackNumber is { } n
                ? demuxer.Tracks.FirstOrDefault(t => t.Number == n) as AudioTrackInfo
                    ?? throw new ArgumentException($"Track {n} is not an audio track.", nameof(options))
                : demuxer.Tracks.OfType<AudioTrackInfo>().FirstOrDefault())
            ?? throw new UnsupportedFormatException("The file has no audio track.");
        _decoder = AudioCodecs.CreateDecoder(Track, (_options.Decoder ?? AudioDecoderOptions.Default) with { Errors = _options.Errors, KeepPadding = _options.KeepAllSamples }, context);
        SampleRate = _options.SampleRate ?? Track.SampleRate;
        Layout = _options.Layout ?? Track.EffectiveLayout;
    }

    /// <summary>Gets what the file holds.</summary>
    public MediaInfo Info => _demuxer.Info;

    /// <summary>Gets the audio track being read.</summary>
    public AudioTrackInfo Track { get; }

    /// <summary>Gets the sample rate of the frames handed out.</summary>
    public int SampleRate { get; }

    /// <summary>Gets the channels of the frames handed out.</summary>
    public ChannelLayout Layout { get; }

    /// <summary>Gets whether <see cref="Seek"/> works: the file must be seekable.</summary>
    public bool CanSeek => _demuxer.CanSeek;

    /// <summary>Opens an audio or video file and its first audio track.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Which track and what to convert to, or <see langword="null"/> for the first audio track as planar float.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The reader. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches, the file has no audio track, or no codec decodes it.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-read"/>
    /// </example>
    public static AudioReader Open(string path, AudioReaderOptions? options = null, MediaContext? context = null)
    {
        context ??= MediaContext.Default;
        return Create(MediaFile.OpenDemuxer(path, context), options, context);
    }

    /// <summary>Opens audio from a stream, seekable or not.</summary>
    /// <param name="stream">The file, read from its current position.</param>
    /// <param name="options">Which track and what to convert to.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the reader leaves the stream open.</param>
    /// <returns>The reader. The caller must dispose it.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-read"/>
    /// </example>
    public static AudioReader Open(Stream stream, AudioReaderOptions? options = null, MediaContext? context = null, bool leaveOpen = false)
    {
        context ??= MediaContext.Default;
        return Create(MediaFile.OpenDemuxer(stream, context, leaveOpen), options, context);
    }

    /// <summary>Reads the next frame, or returns <see langword="null"/> at the end.</summary>
    /// <returns>The frame, which the caller must dispose, or <see langword="null"/>.</returns>
    /// <exception cref="CorruptDataException">A packet cannot be decoded and <see cref="AudioReaderOptions.Errors"/> is <see cref="DecodeErrorMode.Throw"/>.</exception>
    public AudioFrame? ReadFrame() => TryRead(out AudioFrame? frame) ? frame : null;

    /// <summary>Reads the next frame.</summary>
    /// <param name="frame">The frame, which the caller must dispose.</param>
    /// <returns>Whether a frame was read; <see langword="false"/> at the end.</returns>
    public bool TryRead([NotNullWhen(true)] out AudioFrame? frame)
    {
        while (true)
        {
            if (_resampler is not null && _resampler.TryReceive(out frame))
            {
                return true;
            }

            if (Decoded() is not { } decoded)
            {
                if (_resampler is not null && !_flushed)
                {
                    _flushed = true;
                    _resampler.Flush();
                    continue;
                }

                frame = null;
                return false;
            }

            if (Convert(decoded) is { } converted)
            {
                frame = converted;
                return true;
            }
        }
    }

    /// <summary>Reads every remaining frame. Dispose each frame.</summary>
    /// <returns>The frames.</returns>
    public IEnumerable<AudioFrame> ReadFrames()
    {
        while (TryRead(out AudioFrame? frame))
        {
            yield return frame;
        }
    }

    /// <summary>
    /// Moves so that the next frame starts exactly at <paramref name="time"/>: seeks the container to a packet before it, decodes the
    /// codec's pre-roll (the track's <see cref="AudioTrackInfo.SeekPreRoll"/>) and drops the samples before the time.
    /// </summary>
    /// <param name="time">The target time.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    public void Seek(MediaTime time)
    {
        // One container tick earlier, so a timestamp rounded up (Matroska's milliseconds) still lands before the target.
        MediaTime from = (Track.SeekPreRoll is { } preRoll ? time - preRoll : time) - new MediaTime(1, Track.TimeBase);
        _demuxer.Seek(from, Track.Number);
        _decoder.Reset();
        _resampler?.Reset();
        _discard.Clear();
        _endOfStream = false;
        _flushed = false;
        _seekTarget = time;
        _emittedEnd = null;
    }

    /// <summary>Closes the file.</summary>
    public void Dispose()
    {
        _decoder.Dispose();
        _resampler?.Dispose();
        _demuxer.Dispose();
        _diagnostics?.Dispose();
    }

    private static AudioReader Create(IDemuxer demuxer, AudioReaderOptions? options, MediaContext context)
    {
        try
        {
            return new AudioReader(demuxer, options, context);
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }

    /// <summary>The next decoded frame, trimmed for priming, edit lists and the seek target.</summary>
    private AudioFrame? Decoded()
    {
        while (true)
        {
            AudioFrame? frame;
            try
            {
                if (!_decoder.TryReceive(out frame))
                {
                    frame = null;
                }
            }
            catch (CorruptDataException e) when (_options.Errors != DecodeErrorMode.Throw)
            {
                Report("Audio.CorruptPacket", $"A packet cannot be decoded ({e.Message}); it was dropped.");
                continue;
            }

            if (frame is not null)
            {
                if (Trim(frame))
                {
                    return frame;
                }

                frame.Dispose();
                continue;
            }

            if (_endOfStream)
            {
                return null;
            }

            EncodedPacket? packet = _demuxer.ReadPacket();
            if (packet is null)
            {
                _endOfStream = true;
                _decoder.Flush();
                continue;
            }

            using (packet)
            {
                if (packet.TrackNumber != Track.Number)
                {
                    continue;
                }

                if (packet.IsDiscardable && !_options.KeepAllSamples)
                {
                    _discard.Add(packet.Timestamp);
                }

                try
                {
                    _decoder.Send(packet);
                }
                catch (CorruptDataException e) when (_options.Errors != DecodeErrorMode.Throw)
                {
                    Report("Audio.CorruptPacket", $"A packet cannot be decoded ({e.Message}); it was dropped.");
                }
            }
        }
    }

    /// <summary>Drops samples before time zero (codec delay), of discardable packets, and before a seek target; false when none are left.</summary>
    private bool Trim(AudioFrame frame)
    {
        if (_discard.Count > 0 && _discard.Remove(frame.Timestamp))
        {
            return false;
        }

        MediaTime start = _options.KeepAllSamples ? frame.Timestamp : frame.Timestamp < MediaTime.Zero ? MediaTime.Zero : frame.Timestamp;
        if (_seekTarget is { } target && target > start)
        {
            start = target;
        }

        // Audio comes out once: a chained Ogg link's pre-skip, which overlaps the end of the link before, is dropped.
        if (!_options.KeepAllSamples && _emittedEnd is { } emitted && emitted > start)
        {
            start = emitted;
        }

        if (start > frame.Timestamp)
        {
            // In the frame's own sample ticks: a difference taken in a coarser time base (MediaTime.Zero's seconds) would round away.
            Rational samples = new(1, frame.SampleRate);
            long skip = start.Rescale(samples).Ticks - frame.Timestamp.Rescale(samples).Ticks;
            if (skip >= frame.SampleCount)
            {
                return false;
            }

            frame.Trim((int)skip, frame.SampleCount - (int)skip);
        }

        _seekTarget = null;
        _emittedEnd = frame.Timestamp + frame.Duration;
        return true;
    }

    /// <summary>Converts a decoded frame to the output rate, layout and format; null while the resampler fills.</summary>
    private AudioFrame? Convert(AudioFrame decoded)
    {
        SampleFormat format = _options.SampleFormat ?? decoded.Format;
        if (decoded.SampleRate != SampleRate)
        {
            using (decoded)
            {
                if (_resampler is null || _resampler.InputRate != decoded.SampleRate)
                {
                    _resampler?.Dispose();
                    _resampler = new Resampler(decoded.SampleRate, SampleRate, decoded.Layout, _options.Quality, Layout, format, _context);
                }

                _resampler.Send(decoded);
                return null;
            }
        }

        if (decoded.Format == format && decoded.Layout == Layout)
        {
            return decoded;
        }

        using (decoded)
        {
            return decoded.Convert(format, Layout, new AudioConvertOptions { Dither = _options.Dither }, _context);
        }
    }

    private void Report(string id, string message)
    {
        if (_context.Egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _context.Egami.StartDiagnostics();
        _diagnostics.Report(DiagnosticLevel.Warning, id, message);
    }
}

/// <summary>How an <see cref="AudioReader"/> reads and converts.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Audio.cs" region="audio-read"/>
/// </example>
public sealed record AudioReaderOptions
{
    /// <summary>Gets the default options: the first audio track, as planar float at its own rate and layout.</summary>
    public static AudioReaderOptions Default { get; } = new();

    /// <summary>Gets the track to read, or <see langword="null"/> for the first audio track.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>
    /// Gets the sample format of the frames handed out, or <see langword="null"/> for the codec's own (FLAC's and PCM's integers).
    /// Default <see cref="Audio.SampleFormat.F32Planar"/>.
    /// </summary>
    public SampleFormat? SampleFormat { get; init; } = Audio.SampleFormat.F32Planar;

    /// <summary>Gets the sample rate to resample to, or <see langword="null"/> for the track's.</summary>
    public int? SampleRate { get; init; }

    /// <summary>Gets the layout to mix to with <see cref="ChannelMixer"/>'s default matrix, or <see langword="null"/> for the track's.</summary>
    public ChannelLayout? Layout { get; init; }

    /// <summary>Gets the resampler's quality, when the rate changes. Default <see cref="ResamplerQuality.Good"/>.</summary>
    public ResamplerQuality Quality { get; init; } = ResamplerQuality.Good;

    /// <summary>Gets whether to dither when converting to a smaller integer format. Off by default.</summary>
    public bool Dither { get; init; }

    /// <summary>Gets how to decode, or <see langword="null"/> for the defaults.</summary>
    public AudioDecoderOptions? Decoder { get; init; }

    /// <summary>
    /// Gets what to do with a packet that cannot be decoded: <see cref="DecodeErrorMode.Skip"/> (the default) reports and drops it,
    /// <see cref="DecodeErrorMode.Conceal"/> puts silence or the codec's concealment in its place, and
    /// <see cref="DecodeErrorMode.Throw"/> throws. Overrides the decoder options' own setting.
    /// </summary>
    public DecodeErrorMode Errors { get; init; } = DecodeErrorMode.Skip;

    /// <summary>
    /// Gets whether to keep every decoded sample: the codec's priming delay (samples before time zero, such as Opus's pre-skip), the
    /// padding past a packet's stated duration at the end, and the samples of packets an edit list leaves out. Off by default, so
    /// the audio has exactly the samples the encoder was given.
    /// </summary>
    public bool KeepAllSamples { get; init; }
}
