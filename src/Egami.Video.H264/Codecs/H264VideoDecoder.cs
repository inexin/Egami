using System.Diagnostics.CodeAnalysis;
using System.Runtime.Intrinsics;

using Egami.Colors;
using Egami.Pixels;
using Egami.Video.Codecs.H264;

namespace Egami.Video.Codecs;

/// <summary>
/// The H.264 codec adapter: the send and receive loop over <see cref="H264Decoder"/>. Packets are access units, length-prefixed
/// after an avcC record (MP4, Matroska) or Annex B (raw streams, AVI, MPEG-TS). Frames come out in output order, which B-frames
/// make different from decode order, so each frame takes the smallest packet timestamp still waiting. Frames come out at their
/// cropped size, as YuvRaster&lt;byte&gt; at 8 bits and YuvRaster&lt;ushort&gt; above.
/// </summary>
internal sealed class H264VideoDecoder : IVideoDecoder
{
    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly H264Decoder _decoder;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private readonly PriorityQueue<(MediaTime Time, MediaTime? Duration), MediaTime> _times = new();
    private DiagnosticReporter? _diagnostics;
    private HdrMetadata? _hdr;
    private int _packet = -1;
    private bool _disposed;

    public H264VideoDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _decoder = new H264Decoder(_egami.Limits, _errors)
        {
            MaxParallelism = Math.Max(1, _egami.MaxParallelism),
            Report = (id, message) => Report(DiagnosticLevel.Warning, id, message),
        };
        _hdr = track.Hdr;
        if (!track.CodecPrivate.IsEmpty)
        {
            try
            {
                if (H264Config.Parse(track.CodecPrivate) is { } config)
                {
                    _decoder.Configure(config);
                }
            }
            catch (CorruptDataException e)
            {
                Report(DiagnosticLevel.Warning, "H264.CorruptFrame", $"The track's avcC record cannot be read ({e.Message}).");
                if (_errors == DecodeErrorMode.Throw)
                {
                    throw;
                }
            }
        }
    }

    /// <summary>Gets how many frame buffers the decoder holds.</summary>
    internal int FrameBuffers => _decoder.FrameBuffers;

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        int started = _decoder.PicturesStarted;
        try
        {
            _decoder.Decode(packet.Data, null);
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            Report(DiagnosticLevel.Warning, "H264.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}).");
        }
        finally
        {
            for (int i = started; i < _decoder.PicturesStarted; i++)
            {
                _times.Enqueue((packet.Timestamp, packet.Duration), packet.Timestamp);
            }
        }

        TakeOutput();
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _decoder.Flush();
        TakeOutput();
        _times.Clear();
    }

    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        while (_ready.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }

        _decoder.Reset();
        _times.Clear();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        while (_ready.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }

        _diagnostics?.Dispose();
    }

    /// <summary>Gets the chroma siting of an H.264 chroma_sample_loc_type (E.2.1, Figure E-1), or null when absent.</summary>
    internal static ChromaSiting? SitingOf(int chromaSampleLocation) => chromaSampleLocation switch
    {
        0 => ChromaSiting.Left,
        1 => ChromaSiting.Center,
        2 => ChromaSiting.TopLeft,
        _ => null,
    };

    /// <summary>
    /// The frame's color: the VUI's colour description wins over the container's; without either, the matrix is BT.601 for
    /// standard definition and BT.709 above, as players assume.
    /// </summary>
    internal static ColorCodePoints ColorOf(H264Sps sps, ColorCodePoints? container)
    {
        H264Vui? vui = sps.Vui;
        if (vui is not null && (vui.ColourPrimaries != 2 || vui.TransferCharacteristics != 2 || vui.MatrixCoefficients != 2))
        {
            return new ColorCodePoints((byte)vui.ColourPrimaries, (byte)vui.TransferCharacteristics, (byte)vui.MatrixCoefficients, vui.FullRange);
        }

        if (container is { } c)
        {
            return vui is not null && vui.FullRange ? c with { FullRange = true } : c;
        }

        return new ColorCodePoints(2, 2, (byte)(sps.CroppedHeight <= 576 ? 6 : 1), vui?.FullRange ?? false);
    }

    private void TakeOutput()
    {
        foreach (H264Picture picture in _decoder.Output)
        {
            (MediaTime time, MediaTime? duration) = _times.TryDequeue(out var t, out _) ? t : (MediaTime.Zero, null);
            if (picture.Sei?.Hdr is { } hdr)
            {
                _hdr = hdr;
            }

            _ready.Enqueue(MakeFrame(picture, time, duration));
        }

        _decoder.Output.Clear();
    }

    private YuvVideoFrame MakeFrame(H264Picture picture, MediaTime time, MediaTime? duration)
    {
        H264Sps sps = picture.Sps!;
        ColorCodePoints color = ColorOf(sps, _track.Color);
        ChromaSiting siting = _track.ChromaSiting ?? (sps.Vui is { } vui ? SitingOf(vui.ChromaSampleLocation) : null) ?? ChromaSiting.Left;
        YuvColor yuvColor = color.TryGetYuvColor(siting, out YuvColor c) ? c
            : new YuvColor(sps.CroppedHeight <= 576 ? YuvMatrix.Bt601 : YuvMatrix.Bt709, color.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
        ChromaFormat chroma = sps.ChromaFormatIdc switch
        {
            0 => ChromaFormat.Yuv400,
            1 => ChromaFormat.Yuv420,
            _ => ChromaFormat.Yuv422,
        };

        int width = sps.CroppedWidth, height = sps.CroppedHeight;
        YuvRaster raster;
        if (sps.BitDepthLuma == 8 && sps.BitDepthChroma == 8)
        {
            YuvRaster<byte> r8 = YuvRaster.Create(width, height, chroma, yuvColor, false, _egami);
            Copy(picture, sps, r8);
            raster = r8;
        }
        else
        {
            YuvRaster<ushort> r16 = YuvRaster.Create(width, height, chroma, Math.Max(sps.BitDepthLuma, sps.BitDepthChroma), yuvColor, false, _egami);
            Copy(picture, sps, r16);
            raster = r16;
        }

        raster.Metadata.CodePoints = color;
        raster.Metadata.Hdr = _hdr;
        return new YuvVideoFrame(raster, time, duration, color) { Hdr = _hdr, Hdr10Plus = picture.Sei?.Hdr10Plus };
    }

    private static void Copy(H264Picture picture, H264Sps sps, YuvRaster<byte> target)
    {
        int left = sps.CropLeft * sps.CropUnitX, top = sps.CropTop * sps.CropUnitY;
        CopyPlane(picture.Y, picture.Origin + (top * picture.Stride) + left, picture.Stride, target.Y);
        if (target.U is { } u)
        {
            int at = picture.ChromaOrigin + (top / sps.SubHeightC * picture.ChromaStride) + (left / sps.SubWidthC);
            CopyPlane(picture.Cb, at, picture.ChromaStride, u);
            CopyPlane(picture.Cr, at, picture.ChromaStride, target.V!);
        }
    }

    private static void Copy(H264Picture picture, H264Sps sps, YuvRaster<ushort> target)
    {
        int left = sps.CropLeft * sps.CropUnitX, top = sps.CropTop * sps.CropUnitY;
        CopyPlane(picture.Y, picture.Origin + (top * picture.Stride) + left, picture.Stride, target.Y);
        if (target.U is { } u)
        {
            int at = picture.ChromaOrigin + (top / sps.SubHeightC * picture.ChromaStride) + (left / sps.SubWidthC);
            CopyPlane(picture.Cb, at, picture.ChromaStride, u);
            CopyPlane(picture.Cr, at, picture.ChromaStride, target.V!);
        }
    }

    private static void CopyPlane(ushort[] source, int at, int stride, YuvPlane<byte> target)
    {
        for (int y = 0; y < target.Height; y++)
        {
            ReadOnlySpan<ushort> row = source.AsSpan(at + (y * stride), target.Width);
            Span<byte> output = target.Row(y);
            int x = 0;
            if (Vector128.IsHardwareAccelerated)
            {
                // Samples are at most 255 here, so narrowing keeps each one.
                for (; x + 16 <= row.Length; x += 16)
                {
                    Vector128.Narrow(Vector128.Create(row.Slice(x, 8)), Vector128.Create(row.Slice(x + 8, 8))).CopyTo(output.Slice(x, 16));
                }
            }

            for (; x < row.Length; x++)
            {
                output[x] = (byte)row[x];
            }
        }
    }

    private static void CopyPlane(ushort[] source, int at, int stride, YuvPlane<ushort> target)
    {
        for (int y = 0; y < target.Height; y++)
        {
            source.AsSpan(at + (y * stride), target.Width).CopyTo(target.Row(y));
        }
    }

    private void Report(DiagnosticLevel level, string id, string message)
    {
        if (_egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _egami.StartDiagnostics();
        _diagnostics.Report(level, id, message, frame: _packet);
    }
}
