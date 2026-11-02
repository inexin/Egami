using Egami.Metadata;
using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>
/// Reads the frames of a video file in a few lines: opens the container, picks the video track, decodes it and hands out
/// frames, as the codec's planes (<see cref="ReadFrames()"/>) or as RGB rasters (<see cref="ReadFrames{TPixel}"/>). Frame
/// memory comes from the context's pixel memory source, pooled by default, so disposing each frame keeps memory steady over
/// long files.
/// </summary>
/// <example>
/// <code language="csharp">
/// using var reader = VideoReader.Open("camera.avi");
/// foreach (VideoFrame&lt;Rgb8&gt; frame in reader.ReadFrames&lt;Rgb8&gt;())
/// {
///     using (frame)
///     {
///         if (frame.Timestamp.ToTimeSpan().TotalSeconds % 10 &lt; 0.04)
///             frame.Raster.Derive(x => x.Fit(320, 240)).Save($"thumb-{frame.Timestamp.Ticks}.png");
///     }
/// }
/// </code>
/// </example>
public sealed class VideoReader : IDisposable
{
    private readonly IDemuxer _demuxer;
    private readonly IVideoDecoder _decoder;
    private readonly MediaContext _context;
    private readonly VideoReaderOptions _options;
    private bool _endOfStream;
    private DiagnosticReporter? _diagnostics;
    private MediaTime? _seekTarget;
    private YuvVideoFrame? _ahead;
    private readonly HashSet<MediaTime> _discard = [];

    private VideoReader(IDemuxer demuxer, VideoReaderOptions? options, MediaContext context)
    {
        _demuxer = demuxer;
        _context = context;
        _options = options ?? VideoReaderOptions.Default;
        Track = (_options.TrackNumber is { } n
                ? demuxer.Tracks.FirstOrDefault(t => t.Number == n) as VideoTrackInfo
                    ?? throw new ArgumentException($"Track {n} is not a video track.", nameof(options))
                : demuxer.Info.VideoTrack)
            ?? throw new UnsupportedFormatException("The file has no video track.");
        _decoder = VideoCodecs.CreateDecoder(Track, (_options.Decoder ?? VideoDecoderOptions.Default) with { Errors = _options.Errors }, context);
    }

    /// <summary>Gets what the file holds.</summary>
    public MediaInfo Info => _demuxer.Info;

    /// <summary>Gets the video track being read.</summary>
    public VideoTrackInfo Track { get; }

    /// <summary>Gets whether <see cref="Seek(MediaTime)"/> works: the file must be seekable.</summary>
    public bool CanSeek => _demuxer.CanSeek;

    /// <summary>Opens a video file.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Which track and how to decode, or <see langword="null"/> for the first video track.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The reader. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches, the file has no video track, or no codec decodes it.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-read"/>
    /// </example>
    public static VideoReader Open(string path, VideoReaderOptions? options = null, MediaContext? context = null)
    {
        context ??= MediaContext.Default;
        IDemuxer demuxer = MediaFile.OpenDemuxer(path, context);
        return Create(demuxer, options, context);
    }

    /// <summary>Opens a video from a stream, seekable or not.</summary>
    /// <param name="stream">The file, read from its current position.</param>
    /// <param name="options">Which track and how to decode, or <see langword="null"/> for the first video track.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the reader leaves the stream open.</param>
    /// <returns>The reader. The caller must dispose it.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container matches, the file has no video track, or no codec decodes it.</exception>
    /// <exception cref="CorruptDataException">The headers break the format's rules.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-read"/>
    /// </example>
    public static VideoReader Open(Stream stream, VideoReaderOptions? options = null, MediaContext? context = null, bool leaveOpen = false)
    {
        context ??= MediaContext.Default;
        IDemuxer demuxer = MediaFile.OpenDemuxer(stream, context, leaveOpen);
        return Create(demuxer, options, context);
    }

    /// <summary>Opens a video file on the thread pool. See <see cref="Open(string, VideoReaderOptions?, MediaContext?)"/>.</summary>
    /// <param name="path">The file path.</param>
    /// <param name="options">Which track and how to decode.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="cancellationToken">Cancels before the open starts.</param>
    /// <returns>The reader. The caller must dispose it.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-open-async"/>
    /// </example>
    public static Task<VideoReader> OpenAsync(string path, VideoReaderOptions? options = null, MediaContext? context = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(path);
        return Task.Run(() => Open(path, options, context), cancellationToken);
    }

    /// <summary>
    /// Opens a video from a stream. A stream that is not a <see cref="FileStream"/> or <see cref="MemoryStream"/> is first
    /// copied into memory asynchronously, as Egami's <c>LoadAsync</c> does, so no thread blocks on network or pipe reads.
    /// </summary>
    /// <param name="stream">The file, read from its current position.</param>
    /// <param name="options">Which track and how to decode.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the reader leaves the stream open.</param>
    /// <param name="cancellationToken">Cancels the copy.</param>
    /// <returns>The reader. The caller must dispose it.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-open-async"/>
    /// </example>
    public static async Task<VideoReader> OpenAsync(Stream stream, VideoReaderOptions? options = null, MediaContext? context = null, bool leaveOpen = false, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        if (stream is FileStream or MemoryStream)
        {
            return await Task.Run(() => Open(stream, options, context, leaveOpen), cancellationToken).ConfigureAwait(false);
        }

        MemoryStream buffer = new();
        try
        {
            await stream.CopyToAsync(buffer, cancellationToken).ConfigureAwait(false);
            buffer.Position = 0;
            if (!leaveOpen)
            {
                await stream.DisposeAsync().ConfigureAwait(false);
            }

            return Open(buffer, options, context, leaveOpen: false);
        }
        catch
        {
            await buffer.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Reads the next frame in the codec's own planes, or returns <see langword="null"/> at the end.</summary>
    /// <returns>The frame, which the caller must dispose, or <see langword="null"/>.</returns>
    /// <exception cref="CorruptDataException">A frame cannot be decoded and <see cref="VideoReaderOptions.Errors"/> is <see cref="DecodeErrorMode.Throw"/>.</exception>
    public YuvVideoFrame? ReadFrame()
    {
        if (_ahead is { } ahead)
        {
            _ahead = null;
            return ahead;
        }

        if (_seekTarget is not { } target)
        {
            return DecodeFrame();
        }

        // An exact seek: decode from the keyframe and keep the last frame at or before the target.
        _seekTarget = null;
        YuvVideoFrame? held = null;
        while (DecodeFrame() is { } frame)
        {
            if (frame.Timestamp <= target)
            {
                held?.Dispose();
                held = frame;
                continue;
            }

            if (held is null)
            {
                // The target is before the first frame: that frame it is.
                return frame;
            }

            _ahead = frame;
            break;
        }

        return held;
    }

    private YuvVideoFrame? DecodeFrame()
    {
        while (true)
        {
            try
            {
                if (_decoder.TryReceive(out YuvVideoFrame? frame))
                {
                    // A frame outside the part the file shows (an MP4 edit list) was decoded only for the frames after it.
                    if (_discard.Count > 0 && _discard.Remove(frame.Timestamp))
                    {
                        frame.Dispose();
                        continue;
                    }

                    return frame;
                }
            }
            catch (CorruptDataException e) when (_options.Errors != DecodeErrorMode.Throw)
            {
                // Codecs that drop broken frames themselves (VP8) never get here; this covers those that only throw.
                if (_context.Egami.Diagnostics is not null)
                {
                    _diagnostics ??= _context.Egami.StartDiagnostics();
                    _diagnostics.Report(DiagnosticLevel.Warning, "Video.CorruptFrame", $"A frame cannot be decoded ({e.Message}); it was dropped.");
                }

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
                if (packet.TrackNumber == Track.Number)
                {
                    if (packet.IsDiscardable)
                    {
                        _discard.Add(packet.Timestamp);
                    }

                    _decoder.Send(packet);
                }
            }
        }
    }

    /// <summary>Reads every remaining frame in the codec's own planes. Dispose each frame.</summary>
    /// <returns>The frames.</returns>
    public IEnumerable<YuvVideoFrame> ReadFrames()
    {
        while (ReadFrame() is { } frame)
        {
            yield return frame;
        }
    }

    /// <summary>
    /// Reads every remaining frame converted to <typeparamref name="TPixel"/>, such as <see cref="Rgb8"/>. Dispose each frame;
    /// its raster comes from the context's memory source.
    /// </summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <returns>The frames.</returns>
    public IEnumerable<VideoFrame<TPixel>> ReadFrames<TPixel>()
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        while (ReadFrame() is { } frame)
        {
            using (frame)
            {
                Raster<TPixel> raster = frame.ToRaster<TPixel>(_context.Egami);
                if (_options.AutoOrient && Track.Orientation is not (ImageOrientation.Normal or ImageOrientation.Unknown))
                {
                    // Egami's own AutoOrient turns the picture by its EXIF orientation, set here from the track's display matrix.
                    raster.Metadata.Exif ??= new ExifData();
                    raster.Metadata.Exif.Orientation = Track.Orientation;
                    raster.Apply(x => x.AutoOrient());
                }

                yield return new VideoFrame<TPixel>(raster, frame.Timestamp, frame.Duration, frame.Color) { Hdr = frame.Hdr, Hdr10Plus = frame.Hdr10Plus, RenderSize = frame.RenderSize, FieldOrder = frame.FieldOrder, RepeatFields = frame.RepeatFields };
            }
        }
    }

    /// <summary>
    /// Moves so that the next frame is the one shown at <paramref name="time"/>: the last frame starting at or before it (the first
    /// frame when the time is earlier). Decodes from the keyframe before the time and drops the frames up to it, so it costs up to
    /// a keyframe interval of decoding; <see cref="Seek(MediaTime, SeekMode)"/> with <see cref="SeekMode.Keyframe"/> is the fast
    /// seek. Frames in flight are dropped.
    /// </summary>
    /// <param name="time">The target time.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    public void Seek(MediaTime time) => Seek(time, SeekMode.Exact);

    /// <summary>
    /// Moves to <paramref name="time"/>: to the frame shown then (<see cref="SeekMode.Exact"/>), or to the keyframe at or before it
    /// (<see cref="SeekMode.Keyframe"/>), which decodes nothing extra. Frames in flight are dropped.
    /// </summary>
    /// <param name="time">The target time.</param>
    /// <param name="mode">How exact the seek is.</param>
    /// <exception cref="NotSupportedException">The stream is not seekable.</exception>
    public void Seek(MediaTime time, SeekMode mode)
    {
        _demuxer.Seek(time, Track.Number);
        _decoder.Reset();
        _discard.Clear();
        _endOfStream = false;
        _ahead?.Dispose();
        _ahead = null;
        _seekTarget = mode == SeekMode.Exact ? time : null;
    }

    /// <summary>Closes the file and drops frames in flight.</summary>
    public void Dispose()
    {
        _ahead?.Dispose();
        _decoder.Dispose();
        _demuxer.Dispose();
        _diagnostics?.Dispose();
    }

    internal static VideoReader Create(IDemuxer demuxer, VideoReaderOptions? options, MediaContext context)
    {
        try
        {
            return new VideoReader(demuxer, options, context);
        }
        catch
        {
            demuxer.Dispose();
            throw;
        }
    }
}

/// <summary>How a <see cref="VideoReader"/> reads.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-options"/>
/// </example>
public sealed record VideoReaderOptions
{
    /// <summary>Gets the default options.</summary>
    public static VideoReaderOptions Default { get; } = new();

    /// <summary>Gets the track to read, or <see langword="null"/> for the first video track.</summary>
    public int? TrackNumber { get; init; }

    /// <summary>Gets how to decode, such as a reduced scale for thumbnails, or <see langword="null"/> for the defaults.</summary>
    public VideoDecoderOptions? Decoder { get; init; }

    /// <summary>
    /// Gets what to do with a frame that cannot be decoded. <see cref="DecodeErrorMode.Skip"/> (the default) reports it through the
    /// Egami context's diagnostics and drops it, with every frame up to the next keyframe for codecs whose frames depend on earlier
    /// ones; <see cref="DecodeErrorMode.Conceal"/> fills it from the previous frame where the codec can (VP8);
    /// <see cref="DecodeErrorMode.Throw"/> throws <see cref="CorruptDataException"/>. Overrides the decoder options' own setting.
    /// </summary>
    public DecodeErrorMode Errors { get; init; } = DecodeErrorMode.Skip;

    /// <summary>
    /// Gets whether <see cref="VideoReader.ReadFrames{TPixel}"/> turns and mirrors each frame upright by the track's
    /// <see cref="VideoTrackInfo.Orientation"/> (an MP4 or QuickTime display matrix). Off by default: frames come out as coded,
    /// as Egami loads photos without applying their EXIF orientation.
    /// </summary>
    public bool AutoOrient { get; init; }
}

/// <summary>How exact <see cref="VideoReader.Seek(MediaTime, SeekMode)"/> is.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-seek"/>
/// </example>
public enum SeekMode
{
    /// <summary>To the frame shown at the time: decodes from the keyframe before it and drops the frames in between. The default.</summary>
    Exact,

    /// <summary>To the keyframe at or before the time: fast, decodes nothing extra, but may land up to a keyframe interval early.</summary>
    Keyframe,
}
