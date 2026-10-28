using System.Buffers;
using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Video.Codecs.Ffv1;

/// <summary>
/// The FFV1 codec adapter: V1's send and receive loop over <see cref="Ffv1Decoder"/>. FFV1 frames stand alone or follow the
/// previous keyframe's states, so a frame is ready right after its packet is sent.
/// </summary>
internal sealed class Ffv1VideoDecoder : IVideoDecoder
{
    /// <summary>The color of YCbCr frames whose container says nothing: BT.601 matrix, limited range, unspecified primaries and transfer.</summary>
    private static readonly ColorCodePoints DefaultColor = new(2, 2, 6, false);

    private readonly Ffv1Decoder _decoder;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly ColorCodePoints? _trackColor;
    private readonly YuvColor _yuvColor;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private readonly List<Ffv1SliceError> _sliceErrors = [];
    private readonly bool _configCrcOk;
    private DiagnosticReporter? _diagnostics;
    private YuvRaster? _lastGood;
    private bool _waitingForKeyframe;
    private bool _configReported;
    private int _packet = -1;
    private bool _disposed;

    public Ffv1VideoDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        _egami = context.Egami;
        _errors = options.Errors;
        _trackColor = track.Color;
        _yuvColor = track.Color is { } c && c.TryGetYuvColor(track.ChromaSiting ?? ChromaSiting.Left, out YuvColor yuv)
            ? yuv
            : new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, track.ChromaSiting ?? ChromaSiting.Left);
        _decoder = new Ffv1Decoder(track.Width, track.Height, Ffv1Codec.ConfigurationRecord(track).Span, out _configCrcOk);
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        if (!_configCrcOk && !_configReported)
        {
            _configReported = true;
            Report("Ffv1.ConfigCrc", "The FFV1 configuration record's CRC does not match; its parameters may be damaged.");
            if (_errors == DecodeErrorMode.Throw)
            {
                throw new CorruptDataException("FFV1", -1, "The FFV1 configuration record's CRC does not match.");
            }
        }

        ReadOnlySpan<byte> data = packet.Data.Span;
        if (_waitingForKeyframe && !Ffv1Decoder.IsKeyframe(data))
        {
            return;
        }

        byte[] buffer = ArrayPool<byte>.Shared.Rent(Math.Max(data.Length, 1));
        YuvRaster? raster = null;
        try
        {
            data.CopyTo(buffer);
            _sliceErrors.Clear();
            raster = _decoder.Decode(buffer, data.Length, _yuvColor, _egami, _sliceErrors, out Ffv1FrameInfo info);
            if (_sliceErrors.Count > 0 && !HandleSliceErrors(raster, info))
            {
                raster.Dispose();
                return;
            }

            _waitingForKeyframe = false;
            if (_errors == DecodeErrorMode.Conceal)
            {
                KeepLastGood(raster);
            }

            ColorCodePoints color = _trackColor ?? (raster.Color.Matrix == YuvMatrix.Identity ? new ColorCodePoints(2, 2, 0, true) : DefaultColor);
            _ready.Enqueue(new YuvVideoFrame(raster, packet.Timestamp, packet.Duration, color));
            raster = null;
        }
        catch (Ffv1MissingKeyframeException) when (_errors != DecodeErrorMode.Throw)
        {
            Report("Ffv1.MissingReference", $"Packet {_packet} is not a keyframe and no keyframe came before it; frames up to the next keyframe are dropped.");
            _waitingForKeyframe = true;
        }
        catch (Ffv1MissingKeyframeException e)
        {
            throw new CorruptDataException("FFV1", -1, e.Message);
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            Report("Ffv1.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}); frames up to the next keyframe are dropped.");
            _decoder.Reset();
            _waitingForKeyframe = true;
        }
        finally
        {
            raster?.Dispose();
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    public void Flush()
    {
    }

    public bool TryReceive([NotNullWhen(true)] out YuvVideoFrame? frame) => _ready.TryDequeue(out frame);

    public void Reset()
    {
        while (_ready.TryDequeue(out YuvVideoFrame? frame))
        {
            frame.Dispose();
        }

        _decoder.Reset();
        _waitingForKeyframe = false;
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

        _lastGood?.Dispose();
        _diagnostics?.Dispose();
    }

    /// <summary>Reports damaged slices and says whether the frame is still shown: concealed from the last good frame, or not at all.</summary>
    private bool HandleSliceErrors(YuvRaster raster, Ffv1FrameInfo info)
    {
        foreach (Ffv1SliceError error in _sliceErrors)
        {
            Report(error.CrcMismatch ? "Ffv1.SliceCrc" : "Ffv1.CorruptFrame", $"Packet {_packet}: {error.Reason}.");
        }

        if (_errors == DecodeErrorMode.Throw)
        {
            throw new CorruptDataException("FFV1", -1, $"FFV1 packet {_packet}: {_sliceErrors[0].Reason}.");
        }

        if (_errors == DecodeErrorMode.Skip)
        {
            return false;
        }

        // Conceal: the damaged slices show the last good frame; a slice whose place is unknown takes the whole last frame.
        if (_lastGood is not null && _lastGood.Width == raster.Width && _lastGood.Height == raster.Height
            && _lastGood.ChromaFormat == raster.ChromaFormat && _lastGood.BitDepth == raster.BitDepth && _lastGood.HasAlpha == raster.HasAlpha)
        {
            foreach (Ffv1SliceError error in _sliceErrors)
            {
                Conceal(raster, error.Width > 0 ? error : error with { X = 0, Y = 0, Width = raster.Width, Height = raster.Height });
            }
        }

        return true;
    }

    private void Conceal(YuvRaster raster, Ffv1SliceError area)
    {
        if (raster is YuvRaster<byte> b && _lastGood is YuvRaster<byte> lb)
        {
            CopyArea(b.Y, lb.Y, area, 0, 0);
            CopyChroma(b.U, lb.U, area, raster);
            CopyChroma(b.V, lb.V, area, raster);
            CopyArea(b.A, lb.A, area, 0, 0);
        }
        else if (raster is YuvRaster<ushort> w && _lastGood is YuvRaster<ushort> lw)
        {
            CopyArea(w.Y, lw.Y, area, 0, 0);
            CopyChroma(w.U, lw.U, area, raster);
            CopyChroma(w.V, lw.V, area, raster);
            CopyArea(w.A, lw.A, area, 0, 0);
        }
    }

    private static void CopyChroma<T>(YuvPlane<T>? target, YuvPlane<T>? source, Ffv1SliceError area, YuvRaster raster)
        where T : unmanaged
    {
        if (target is null)
        {
            return;
        }

        (int hs, int vs) = raster.ChromaFormat switch
        {
            ChromaFormat.Yuv420 => (1, 1),
            ChromaFormat.Yuv422 => (1, 0),
            ChromaFormat.Yuv411 => (2, 0),
            ChromaFormat.Yuv410 => (2, 2),
            _ => (0, 0),
        };
        CopyArea(target, source, area, hs, vs);
    }

    private static void CopyArea<T>(YuvPlane<T>? target, YuvPlane<T>? source, Ffv1SliceError area, int hs, int vs)
        where T : unmanaged
    {
        if (target is null || source is null)
        {
            return;
        }

        int x = area.X >> hs;
        int y = area.Y >> vs;
        int w = Math.Min(-(-area.Width >> hs), target.Width - x);
        int h = Math.Min(-(-area.Height >> vs), target.Height - y);
        for (int row = 0; row < h; row++)
        {
            source.Row(y + row).Slice(x, w).CopyTo(target.Row(y + row).Slice(x, w));
        }
    }

    private void KeepLastGood(YuvRaster raster)
    {
        if (_lastGood is null || _lastGood.Width != raster.Width || _lastGood.Height != raster.Height || _lastGood.ChromaFormat != raster.ChromaFormat
            || _lastGood.BitDepth != raster.BitDepth || _lastGood.HasAlpha != raster.HasAlpha)
        {
            _lastGood?.Dispose();
            _lastGood = raster.BitDepth == 8
                ? YuvRaster.Create(raster.Width, raster.Height, raster.ChromaFormat, raster.Color, raster.HasAlpha, _egami)
                : YuvRaster.Create(raster.Width, raster.Height, raster.ChromaFormat, raster.BitDepth, raster.Color, raster.HasAlpha, _egami);
        }

        var whole = new Ffv1SliceError(0, 0, 0, raster.Width, raster.Height, "", false);
        if (raster is YuvRaster<byte> b && _lastGood is YuvRaster<byte> lb)
        {
            CopyArea(lb.Y, b.Y, whole, 0, 0);
            CopyArea(lb.U, b.U, whole with { Width = lb.U?.Width ?? 0, Height = lb.U?.Height ?? 0 }, 0, 0);
            CopyArea(lb.V, b.V, whole with { Width = lb.V?.Width ?? 0, Height = lb.V?.Height ?? 0 }, 0, 0);
            CopyArea(lb.A, b.A, whole, 0, 0);
        }
        else if (raster is YuvRaster<ushort> w && _lastGood is YuvRaster<ushort> lw)
        {
            CopyArea(lw.Y, w.Y, whole, 0, 0);
            CopyArea(lw.U, w.U, whole with { Width = lw.U?.Width ?? 0, Height = lw.U?.Height ?? 0 }, 0, 0);
            CopyArea(lw.V, w.V, whole with { Width = lw.V?.Width ?? 0, Height = lw.V?.Height ?? 0 }, 0, 0);
            CopyArea(lw.A, w.A, whole, 0, 0);
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
