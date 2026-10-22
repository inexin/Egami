using System.Diagnostics.CodeAnalysis;

using Egami.Formats.Jpeg;
using Egami.Pixels;

namespace Egami.Video.Codecs.MotionJpeg;

/// <summary>
/// Encodes Motion JPEG through Egami's public JPEG writer: planes are written as they are (their own subsampling, no color
/// conversion when they are BT.601 full range), RGB rasters at the chosen chroma format. Every packet is a complete JPEG
/// with its Huffman tables, and a keyframe.
/// </summary>
internal sealed class MotionJpegEncoder : IVideoEncoder
{
    /// <summary>Egami's JPEG default, used when the options leave quality open (V1 design Q5).</summary>
    public const int DefaultQuality = 75;

    private readonly VideoEncoderOptions _options;
    private readonly JpegWriterOptions _planeOptions;
    private readonly JpegWriterOptions _rasterOptions;
    private readonly Queue<EncodedPacket> _ready = new();
    private readonly MemoryStream _buffer = new();
    private bool _disposed;

    public MotionJpegEncoder(VideoEncoderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Width, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfLessThan(options.Height, 1, nameof(options));
        if (options.FrameRate.Numerator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.FrameRate, "The frame rate must be positive.");
        }

        int quality = options.Quality ?? DefaultQuality;
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1, nameof(options));
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100, nameof(options));
        _options = options;
        _planeOptions = new JpegWriterOptions { Quality = quality, StripMetadata = true };
        ChromaFormat chroma = options.ChromaFormat ?? ChromaFormat.Yuv420;
        _rasterOptions = new JpegWriterOptions
        {
            Quality = quality,
            StripMetadata = true,
            ColorType = chroma == ChromaFormat.Yuv400 ? JpegColorType.Gray : JpegColorType.Auto,
            Subsampling = chroma switch
            {
                ChromaFormat.Yuv422 => ChromaSubsampling.Subsample422,
                ChromaFormat.Yuv444 => ChromaSubsampling.Subsample444,
                _ => ChromaSubsampling.Subsample420,
            },
        };
        Rational timeBase = options.FrameRate.Reciprocal();
        Track = new VideoTrackInfo
        {
            Number = 1,
            Kind = TrackKind.Video,
            CodecId = CodecIds.MotionJpeg,
            TimeBase = timeBase,
            Width = options.Width,
            Height = options.Height,
            FrameRate = options.FrameRate,
        };
    }

    public VideoTrackInfo Track { get; }

    public void Send(VideoFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (frame.Width != _options.Width || frame.Height != _options.Height)
        {
            throw new ArgumentException($"The frame is {frame.Width}x{frame.Height}; the encoder makes {_options.Width}x{_options.Height}.", nameof(frame));
        }

        if (frame.Planes is null && frame.Pixels is null)
        {
            throw new ArgumentException("The frame has neither planes nor pixels.", nameof(frame));
        }

        if (_options.TargetFrameBytes is { } target)
        {
            // The highest quality whose frame fits.
            int low = 1, high = _options.Quality ?? 100, best = 1;
            while (low <= high)
            {
                int quality = (low + high) / 2;
                if (Encode(frame, quality) <= target)
                {
                    best = quality;
                    low = quality + 1;
                }
                else
                {
                    high = quality - 1;
                }
            }

            Encode(frame, best);
        }
        else
        {
            Encode(frame, null);
        }

        MediaTime timestamp = frame.Timestamp.Rescale(Track.TimeBase);
        EncodedPacket packet = EncodedPacket.Rent(Track.Number, (int)_buffer.Length, timestamp);
        _buffer.GetBuffer().AsSpan(0, (int)_buffer.Length).CopyTo(packet.GetWritableData().Span);
        packet.IsKeyframe = true;
        packet.Duration = new MediaTime(1, Track.TimeBase);
        _ready.Enqueue(packet);
    }

    public void Flush()
    {
    }

    private long Encode(VideoFrame frame, int? quality)
    {
        _buffer.SetLength(0);
        if (frame.Planes is { } planes)
        {
            planes.Save(_buffer, JpegFormat.Info, quality is { } q ? new JpegWriterOptions { Quality = q, StripMetadata = true } : _planeOptions);
        }
        else
        {
            frame.Pixels!.Save(_buffer, JpegFormat.Info, quality is { } q
                ? new JpegWriterOptions { Quality = q, StripMetadata = true, ColorType = _rasterOptions.ColorType, Subsampling = _rasterOptions.Subsampling }
                : _rasterOptions);
        }

        return _buffer.Length;
    }

    public bool TryReceive([NotNullWhen(true)] out EncodedPacket? packet) => _ready.TryDequeue(out packet);

    public void Dispose()
    {
        _disposed = true;
        while (_ready.TryDequeue(out EncodedPacket? packet))
        {
            packet.Dispose();
        }

        _buffer.Dispose();
    }
}
