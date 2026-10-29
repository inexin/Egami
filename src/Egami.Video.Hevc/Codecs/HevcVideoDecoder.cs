using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Heic;
using Egami.Nal;
using Egami.Pixels;

namespace Egami.Video.Codecs;

/// <summary>
/// The HEVC codec adapter: Egami.Video's send and receive loop over Egami.Heic's <see cref="HevcStreamDecoder"/>. Packets are
/// access units, length-prefixed after an hvcC record (MP4, Matroska) or Annex B (raw streams). Frames come out in output order,
/// which B-frames make different from decode order, so each frame takes the smallest packet timestamp still waiting, with the
/// HDR10+ metadata its packet carried. A picture is complete when the next access unit starts, so a frame comes out one packet
/// late, and the last ones at <see cref="Flush"/>.
/// </summary>
internal sealed class HevcVideoDecoder : IVideoDecoder
{
    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly HevcStreamDecoder _decoder;
    private readonly int _lengthSize;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private readonly PriorityQueue<(MediaTime Time, MediaTime? Duration, Hdr10PlusMetadata? Hdr10Plus), MediaTime> _times = new();
    private DiagnosticReporter? _diagnostics;
    private HdrMetadata? _hdr;
    private bool _waitingForIrap;
    private int _packet = -1;
    private bool _disposed;

    public HevcVideoDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _decoder = new HevcStreamDecoder(new HevcDecoderOptions { ConcealErrors = _errors == DecodeErrorMode.Conceal }, _egami);
        _hdr = track.Hdr;
        if (!track.CodecPrivate.IsEmpty)
        {
            try
            {
                _decoder.SetConfiguration(track.CodecPrivate.Span);
                _lengthSize = (track.CodecPrivate.Span[21] & 3) + 1;
            }
            catch (CorruptDataException e)
            {
                Report("Hevc.CorruptFrame", $"The track's hvcC record cannot be read ({e.Message}).");
                if (_errors == DecodeErrorMode.Throw)
                {
                    throw;
                }
            }
        }
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        List<ReadOnlyMemory<byte>> units;
        try
        {
            units = _lengthSize == 0 ? NalFraming.SplitAnnexB(packet.Data) : NalFraming.SplitLengthPrefixed(packet.Data, _lengthSize);
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            Report("Hevc.CorruptFrame", $"Packet {_packet} cannot be split into NAL units ({e.Message}).");
            return;
        }

        (int pictures, bool irap, Hdr10PlusMetadata? hdr10Plus) = Scan(units);
        if (_waitingForIrap)
        {
            if (!irap)
            {
                return;
            }

            _waitingForIrap = false;
        }

        try
        {
            _decoder.Send(packet.Data.Span);
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            // Conceal reaches here only for damage it cannot cover, such as a broken slice header; both modes then start again at
            // the next IRAP picture.
            Report("Hevc.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}); frames up to the next IRAP picture are dropped.");
            TakeOutput();
            _decoder.Reset();
            _times.Clear();
            _waitingForIrap = true;
            return;
        }

        for (int i = 0; i < pictures; i++)
        {
            _times.Enqueue((packet.Timestamp, packet.Duration, hdr10Plus), packet.Timestamp);
        }

        TakeOutput();
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        try
        {
            _decoder.Flush();
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            Report("Hevc.CorruptFrame", $"The last picture cannot be decoded ({e.Message}).");
        }

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
        _waitingForIrap = false;
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

        _decoder.Dispose();
        _diagnostics?.Dispose();
    }

    /// <summary>
    /// The frame's color: the VUI's colour description wins over the container's; without either, the matrix is BT.601 for
    /// standard definition and BT.709 above, as players assume (the decoder's own default).
    /// </summary>
    internal static ColorCodePoints ColorOf(ColorCodePoints stream, int height, ColorCodePoints? container)
    {
        bool unspecified = stream.Primaries == 2 && stream.TransferCharacteristics == 2 && stream.MatrixCoefficients == (height <= 576 ? 6 : 1);
        return unspecified && container is { } c ? (stream.FullRange ? c with { FullRange = true } : c) : stream;
    }

    /// <summary>
    /// What an access unit holds: how many pictures start in it (VCL units of the base layer with first_slice_segment_in_pic_flag),
    /// whether one is an IRAP picture, and the HDR10+ metadata and mastering display and content light level of its prefix SEI.
    /// </summary>
    private (int Pictures, bool Irap, Hdr10PlusMetadata? Hdr10Plus) Scan(List<ReadOnlyMemory<byte>> units)
    {
        int pictures = 0;
        bool irap = false;
        Hdr10PlusMetadata? hdr10Plus = null;
        foreach (ReadOnlyMemory<byte> memory in units)
        {
            ReadOnlySpan<byte> unit = memory.Span;
            if (unit.Length < 3)
            {
                continue;
            }

            int type = (unit[0] >> 1) & 63, layer = ((unit[0] & 1) << 5) | (unit[1] >> 3);
            if (layer != 0)
            {
                continue;
            }

            if (type < 32 && (unit[2] & 0x80) != 0)
            {
                pictures++;
                irap |= type is >= 16 and <= 23;
            }
            else if (type == 39)
            {
                HevcSeiMessages sei = HevcSeiMessages.Read(NalFraming.Unescape(unit));
                hdr10Plus ??= sei.Hdr10Plus;
                if (sei.Hdr is { } hdr)
                {
                    _hdr = hdr;
                }
            }
        }

        return (pictures, irap, hdr10Plus);
    }

    private void TakeOutput()
    {
        while (_decoder.NextFrame is { } next)
        {
            (MediaTime time, MediaTime? duration, Hdr10PlusMetadata? hdr10Plus) = _times.TryDequeue(out var t, out _) ? t : (MediaTime.Zero, null, null);
            ColorCodePoints color = ColorOf(next.Color, next.Height, _track.Color);
            ChromaSiting siting = _track.ChromaSiting ?? ChromaSiting.Left;
            YuvColor yuvColor = color.TryGetYuvColor(siting, out YuvColor c) ? c
                : new YuvColor(next.Height <= 576 ? YuvMatrix.Bt601 : YuvMatrix.Bt709, color.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
            YuvRaster raster = next.BitDepth == 8
                ? YuvRaster.Create(next.Width, next.Height, next.Chroma, yuvColor, false, _egami)
                : YuvRaster.Create(next.Width, next.Height, next.Chroma, next.BitDepth, yuvColor, false, _egami);
            try
            {
                _decoder.TryReceiveFrame(raster, out _);
                raster.Metadata.CodePoints = color;
                raster.Metadata.Hdr = _hdr;
                _ready.Enqueue(new YuvVideoFrame(raster, time, duration, color) { Hdr = _hdr, Hdr10Plus = hdr10Plus });
            }
            catch
            {
                raster.Dispose();
                throw;
            }
        }
    }

    private void Report(string id, string message)
    {
        if (_egami.Diagnostics is null)
        {
            return;
        }

        _diagnostics ??= _egami.StartDiagnostics();
        _diagnostics.Report(DiagnosticLevel.Warning, id, message, frame: _packet);
    }
}
