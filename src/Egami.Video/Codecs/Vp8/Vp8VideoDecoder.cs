using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Formats.Webp;
using Egami.Pixels;

namespace Egami.Video.Codecs.Vp8;

/// <summary>
/// The VP8 codec adapter: V1's send and receive loop over Egami's <see cref="Vp8StreamDecoder"/>, with a second stream decoder for
/// WebM alpha. Decoding is synchronous, so a shown frame is ready right after its packet is sent and a hidden one gives none.
/// </summary>
internal sealed class Vp8VideoDecoder : IVideoDecoder
{
    /// <summary>VP8's color: BT.601 matrix, limited range, unspecified primaries and transfer (H.273 code points 2, 2, 6).</summary>
    internal static readonly ColorCodePoints Vp8Color = new(2, 2, 6, false);

    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly Vp8StreamDecoder _video;
    private readonly Vp8StreamDecoder? _alpha;
    private readonly ColorCodePoints _color;
    private readonly YuvColor _yuvColor;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private DiagnosticReporter? _diagnostics;
    private YuvRaster<byte>? _alphaScratch;
    private bool _haveKeyframe;
    private bool _waitingForKeyframe;
    private int _packet = -1;
    private bool _disposed;

    public Vp8VideoDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _video = new Vp8StreamDecoder(_egami) { ConcealErrors = _errors == DecodeErrorMode.Conceal };
        if (track.HasAlpha && options.DecodeAlpha)
        {
            _alpha = new Vp8StreamDecoder(_egami) { ConcealErrors = _errors == DecodeErrorMode.Conceal };
        }

        _color = track.Color is { } c ? c.MatrixCoefficients == 2 ? c with { MatrixCoefficients = Vp8Color.MatrixCoefficients } : c : Vp8Color;
        _yuvColor = _color.TryGetYuvColor(track.ChromaSiting ?? ChromaSiting.Center, out YuvColor yuv)
            ? yuv
            : new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Center);
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        bool? bit = FrameKind.IsKeyframe(CodecIds.Vp8, data);
        if (bit is { } frameKey && frameKey != packet.IsKeyframe)
        {
            Report(DiagnosticLevel.Warning, "Vp8.KeyframeFlagMismatch", $"The container marks packet {_packet} as {(packet.IsKeyframe ? "a keyframe" : "not a keyframe")}, but the VP8 frame says {(frameKey ? "it is one" : "it is not")}; the frame's own bit is used.");
        }

        bool key = bit ?? false;
        if (_waitingForKeyframe && !key)
        {
            // Skip mode after a broken frame: nothing until the next keyframe.
            return;
        }

        if (!_haveKeyframe && !key && _errors != DecodeErrorMode.Conceal)
        {
            Report(DiagnosticLevel.Warning, "Vp8.MissingReference", $"Packet {_packet} is an inter frame with no keyframe before it.");
            if (_errors == DecodeErrorMode.Skip)
            {
                _waitingForKeyframe = true;
                return;
            }
        }

        Vp8FrameInfo info;
        Vp8FrameInfo? alphaInfo = null;
        try
        {
            info = _video.Decode(data);
            if (_alpha is not null && AlphaData(packet) is { } alpha)
            {
                alphaInfo = _alpha.Decode(alpha.Span);
            }
        }
        catch (CorruptDataException e) when (_errors != DecodeErrorMode.Throw)
        {
            // Conceal reaches here only when nothing can stand in for the frame; both modes then wait for a keyframe.
            Report(DiagnosticLevel.Warning, "Vp8.CorruptFrame", $"Packet {_packet} cannot be decoded ({e.Message}); frames up to the next keyframe are dropped.");
            _video.Reset();
            _alpha?.Reset();
            _haveKeyframe = false;
            _waitingForKeyframe = true;
            return;
        }

        if (info.IsConcealed || alphaInfo is { IsConcealed: true })
        {
            if (_haveKeyframe || key)
            {
                Report(DiagnosticLevel.Warning, "Vp8.CorruptFrame", $"Packet {_packet} is damaged; what could not be read was filled from the previous frame.");
            }
            else
            {
                Report(DiagnosticLevel.Warning, "Vp8.MissingReference", $"Packet {_packet} is an inter frame with no keyframe before it; it was predicted from mid-gray.");
            }
        }

        _haveKeyframe |= key && !info.IsConcealed;
        _waitingForKeyframe = false;
        bool gray = false;
        if (!info.IsShown)
        {
            // A concealed inter frame before any keyframe has no size to show at; the container's size stands in, mid-gray.
            if (!(info.IsConcealed && !_haveKeyframe && !key && _track.Width > 0 && _track.Height > 0))
            {
                return;
            }

            gray = true;
            info = info with { Width = _track.Width, Height = _track.Height };
        }

        YuvRaster<byte> raster = YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, _yuvColor, alpha: _alpha is not null, context: _egami);
        try
        {
            if (gray)
            {
                Fill(raster.Y, 128, info.Height);
                Fill(raster.U!, 128, (info.Height + 1) / 2);
                Fill(raster.V!, 128, (info.Height + 1) / 2);
                if (raster.A is { } a)
                {
                    Fill(a, 255, info.Height);
                }
            }
            else
            {
                _video.CopyFrameTo(raster);
                if (raster.A is { } plane)
                {
                    CopyAlpha(alphaInfo, info, plane);
                }
            }

            _ready.Enqueue(new YuvVideoFrame(raster, packet.Timestamp, packet.Duration, _color) { Hdr = _track.Hdr });
        }
        catch
        {
            raster.Dispose();
            throw;
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

        _video.Reset();
        _alpha?.Reset();
        _haveKeyframe = false;
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

        _video.Dispose();
        _alpha?.Dispose();
        _alphaScratch?.Dispose();
        _diagnostics?.Dispose();
    }

    private static void Fill(YuvPlane<byte> plane, byte value, int rows)
    {
        for (int y = 0; y < rows; y++)
        {
            plane.Row(y).Fill(value);
        }
    }

    private static ReadOnlyMemory<byte>? AlphaData(EncodedPacket packet)
    {
        foreach (PacketAddition addition in packet.Additions)
        {
            if (addition.Id == 1)
            {
                return addition.Data;
            }
        }

        return null;
    }

    private void CopyAlpha(Vp8FrameInfo? alphaInfo, Vp8FrameInfo info, YuvPlane<byte> plane)
    {
        // A frame without its alpha frame, or whose alpha is hidden or another size, is shown opaque.
        if (alphaInfo is not { IsShown: true } a || a.Width != info.Width || a.Height != info.Height)
        {
            if (alphaInfo is { IsShown: true })
            {
                Report(DiagnosticLevel.Warning, "Vp8.CorruptFrame", $"Packet {_packet}'s alpha frame is {alphaInfo.Value.Width}×{alphaInfo.Value.Height}, not {info.Width}×{info.Height}; the frame is shown opaque.");
            }

            Fill(plane, 255, info.Height);
            return;
        }

        if (_alphaScratch is null || _alphaScratch.Width != info.Width || _alphaScratch.Height != info.Height)
        {
            _alphaScratch?.Dispose();
            _alphaScratch = YuvRaster.Create(info.Width, info.Height, ChromaFormat.Yuv420, _yuvColor, context: _egami);
        }

        _alpha!.CopyFrameTo(_alphaScratch);
        for (int y = 0; y < info.Height; y++)
        {
            _alphaScratch.Y.Row(y).CopyTo(plane.Row(y));
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
