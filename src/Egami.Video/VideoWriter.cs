using Egami.Pixels;
using Egami.Video.Codecs;
using Egami.Video.Containers;

namespace Egami.Video;

/// <summary>
/// Writes a video file from rasters in a few lines: encodes each frame and muxes it, picking the container from the file
/// extension. The picture size comes from the first frame. Other tracks, such as audio copied from a source file, can be
/// written alongside with <see cref="WritePacket"/>.
/// </summary>
/// <example>
/// <code language="csharp">
/// using var writer = VideoWriter.Create("out.mkv",
///     new VideoWriterOptions { Codec = VideoCodecs.MotionJpeg, FrameRate = new Rational(30, 1), Quality = 85 });
/// foreach (Raster&lt;Rgb8&gt; image in images)
///     writer.WriteFrame(image);
/// </code>
/// </example>
public sealed class VideoWriter : IDisposable
{
    private readonly Func<IReadOnlyList<TrackInfo>, IMuxer> _createMuxer;
    private readonly VideoWriterOptions _options;
    private readonly MediaContext _context;
    private readonly int _videoNumber;
    private IVideoEncoder? _encoder;
    private IMuxer? _muxer;
    private long _frames;
    private bool _finished;

    private VideoWriter(Func<IReadOnlyList<TrackInfo>, IMuxer> createMuxer, VideoWriterOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.Codec.CanEncode)
        {
            throw new NotSupportedException($"{options.Codec.Name} cannot encode.");
        }

        _createMuxer = createMuxer;
        _options = options;
        _context = context;
        _videoNumber = options.OtherTracks.Count == 0 ? 1 : options.OtherTracks.Max(t => t.Number) + 1;
    }

    /// <summary>Gets the video track, once the first frame has been written.</summary>
    public VideoTrackInfo? Track => _encoder is null ? null : _encoder.Track with { Number = _videoNumber };

    /// <summary>Gets how many frames have been written.</summary>
    public long FrameCount => _frames;

    /// <summary>Creates a video file, choosing the container by the extension. An existing file is replaced.</summary>
    /// <param name="path">The file path, such as "out.avi" or "out.mkv".</param>
    /// <param name="options">The codec, frame rate and quality.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <returns>The writer. The caller must dispose it, which finishes the file.</returns>
    /// <exception cref="UnsupportedFormatException">No registered container writes the extension.</exception>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-write"/>
    /// </example>
    public static VideoWriter Create(string path, VideoWriterOptions? options = null, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(path);
        context ??= MediaContext.Default;
        _ = MediaFile.FormatForPath(path, context);
        options ??= new VideoWriterOptions();
        return new VideoWriter(tracks => MediaFile.CreateMuxer(path, tracks, context, options.Metadata), options, context);
    }

    /// <summary>Creates a writer to a stream in the named container.</summary>
    /// <param name="stream">Where to write.</param>
    /// <param name="formatNameOrExtension">"AVI", "Matroska", ".mkv" and so on.</param>
    /// <param name="options">The codec, frame rate and quality.</param>
    /// <param name="context">The context, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <param name="leaveOpen">Whether disposing the writer leaves the stream open.</param>
    /// <returns>The writer. The caller must dispose it, which finishes the file.</returns>
    /// <example>
    /// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-write"/>
    /// </example>
    public static VideoWriter Create(Stream stream, string formatNameOrExtension, VideoWriterOptions? options = null, MediaContext? context = null, bool leaveOpen = false)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(formatNameOrExtension);
        context ??= MediaContext.Default;
        options ??= new VideoWriterOptions();
        return new VideoWriter(tracks => MediaFile.CreateMuxer(stream, formatNameOrExtension, tracks, context, leaveOpen, options.Metadata), options, context);
    }

    /// <summary>Writes a frame at the next frame time (frame count over the frame rate).</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="image">The picture. The writer does not keep it.</param>
    public void WriteFrame<TPixel>(Raster<TPixel> image)
        where TPixel : unmanaged, IPixelType<TPixel> => WriteFrame(image, NextTime());

    /// <summary>Writes a frame at a given time.</summary>
    /// <typeparam name="TPixel">The pixel type.</typeparam>
    /// <param name="image">The picture. The writer does not keep it.</param>
    /// <param name="timestamp">When the frame is shown.</param>
    public void WriteFrame<TPixel>(Raster<TPixel> image, MediaTime timestamp)
        where TPixel : unmanaged, IPixelType<TPixel>
    {
        ArgumentNullException.ThrowIfNull(image);
        Encode(new BorrowedFrame(image, null, timestamp));
    }

    /// <summary>Writes a frame given as planes at the next frame time.</summary>
    /// <param name="planes">The planes. The writer does not keep them.</param>
    public void WriteFrame(YuvRaster planes) => WriteFrame(planes, NextTime());

    /// <summary>Writes a frame given as planes at a given time.</summary>
    /// <param name="planes">The planes. The writer does not keep them.</param>
    /// <param name="timestamp">When the frame is shown.</param>
    public void WriteFrame(YuvRaster planes, MediaTime timestamp)
    {
        ArgumentNullException.ThrowIfNull(planes);
        Encode(new BorrowedFrame(null, planes, timestamp));
    }

    /// <summary>Writes a decoded frame at its own timestamp, for example one read from a <see cref="VideoReader"/>.</summary>
    /// <param name="frame">The frame. The writer does not keep or dispose it.</param>
    public void WriteFrame(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        Encode(frame);
    }

    /// <summary>
    /// Writes a packet of one of <see cref="VideoWriterOptions.OtherTracks"/>, numbered as there. Keep packets roughly in
    /// time order with the frames.
    /// </summary>
    /// <param name="packet">The packet. The caller keeps ownership.</param>
    /// <exception cref="InvalidOperationException">No frame has been written yet, so the file is not started.</exception>
    public void WritePacket(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_muxer is null)
        {
            throw new InvalidOperationException("Write the first frame before other packets: it sets the picture size.");
        }

        _muxer.WritePacket(packet);
    }

    /// <summary>Flushes the encoder and finishes the file. Called by <see cref="Dispose"/>.</summary>
    public void Finish()
    {
        if (_finished)
        {
            return;
        }

        _finished = true;
        if (_encoder is not null && _muxer is not null)
        {
            _encoder.Flush();
            Drain();
            _muxer.Finish();
        }
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
            _muxer?.Dispose();
        }
    }

    private MediaTime NextTime() => new(_frames, _options.FrameRate.Reciprocal());

    private void Encode(VideoFrame frame)
    {
        ObjectDisposedException.ThrowIf(_finished, this);
        if (_encoder is null)
        {
            _encoder = _options.Codec.CreateEncoder(
                new VideoEncoderOptions
                {
                    Width = frame.Width,
                    Height = frame.Height,
                    FrameRate = _options.FrameRate,
                    Quality = _options.Quality,
                    ChromaFormat = _options.ChromaFormat,
                },
                _context);
            _encoder.Send(frame);

            // Some encoders (uncompressed video) learn their layout from the first frame.
            _muxer = _createMuxer([.. _options.OtherTracks, _encoder.Track with { Number = _videoNumber }]);
        }
        else
        {
            _encoder.Send(frame);
        }

        _frames++;
        Drain();
    }

    private void Drain()
    {
        while (_encoder!.TryReceive(out EncodedPacket? packet))
        {
            using (packet)
            {
                packet.TrackNumber = _videoNumber;
                _muxer!.WritePacket(packet);
            }
        }
    }

    /// <summary>Wraps a caller's raster or planes as a frame without taking ownership.</summary>
    private sealed class BorrowedFrame(Raster? pixels, YuvRaster? planes, MediaTime timestamp) : VideoFrame(timestamp, null, null)
    {
        public override int Width => pixels?.Width ?? planes!.Width;

        public override int Height => pixels?.Height ?? planes!.Height;

        public override Raster? Pixels => pixels;

        public override YuvRaster? Planes => planes;

        protected override void Dispose(bool disposing)
        {
        }
    }
}

/// <summary>What a <see cref="VideoWriter"/> writes.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/Video.cs" region="video-write"/>
/// </example>
public sealed record VideoWriterOptions
{
    /// <summary>Gets the codec. Default <see cref="VideoCodecs.MotionJpeg"/>.</summary>
    public VideoCodec Codec { get; init; } = MotionJpegCodec.Instance;

    /// <summary>Gets the frame rate. Default 30/1.</summary>
    public Rational FrameRate { get; init; } = new(30, 1);

    /// <summary>Gets the quality from 1 to 100, or <see langword="null"/> for the codec's default.</summary>
    public int? Quality { get; init; }

    /// <summary>Gets the chroma subsampling for RGB frames, or <see langword="null"/> for the codec's default.</summary>
    public ChromaFormat? ChromaFormat { get; init; }

    /// <summary>
    /// Gets tracks written alongside the video, such as audio from the source file, whose packets go through
    /// <see cref="VideoWriter.WritePacket"/>. The video track is numbered after them.
    /// </summary>
    public IReadOnlyList<TrackInfo> OtherTracks { get; init; } = [];

    /// <summary>Gets the tags, chapters and attachments to write, where the container can hold them.</summary>
    public MediaMetadata Metadata { get; init; } = MediaMetadata.Empty;
}
