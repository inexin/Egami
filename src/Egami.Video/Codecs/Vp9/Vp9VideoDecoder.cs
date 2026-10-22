using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Pixels;

namespace Egami.Video.Codecs.Vp9;

/// <summary>
/// The VP9 codec adapter: V1's send and receive loop over <see cref="Vp9StreamDecoder"/>, with a second stream decoder for WebM
/// alpha. Decoding is synchronous: a packet's shown frame is ready right after it is sent, and a packet of hidden frames gives
/// none. Frames come out at their own coded size, as YuvRaster&lt;byte&gt; at 8 bits and YuvRaster&lt;ushort&gt; above.
/// </summary>
internal sealed class Vp9VideoDecoder : IVideoDecoder
{
    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly Vp9StreamDecoder _video;
    private readonly Vp9StreamDecoder? _alpha;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private readonly Vp9CodecPrivate? _codecPrivate;
    private DiagnosticReporter? _diagnostics;
    private YuvVideoFrame? _lastShown;
    private bool _waitingForKeyframe;
    private bool _unsupported;
    private bool _checkedCodecPrivate;
    private int _packet = -1;
    private bool _disposed;

    public Vp9VideoDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _video = new Vp9StreamDecoder(_egami.Memory) { MaxParallelism = Math.Max(1, _egami.MaxParallelism), Limits = _egami.Limits };
        if (track.HasAlpha && options.DecodeAlpha)
        {
            _alpha = new Vp9StreamDecoder(_egami.Memory) { MaxParallelism = Math.Max(1, _egami.MaxParallelism), Limits = _egami.Limits };
        }

        _codecPrivate = Vp9CodecPrivate.Parse(track.CodecPrivate.Span);
    }

    /// <summary>Gets how many frame buffers the stream decoders hold: the picture's and, with alpha, the alpha stream's.</summary>
    internal int FrameBuffers => _video.FrameBuffers + (_alpha?.FrameBuffers ?? 0);

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        if (_unsupported)
        {
            return;
        }

        ReadOnlySpan<byte> data = packet.Data.Span;
        bool? bit = FrameKind.IsKeyframe(CodecIds.Vp9, data);
        if (bit is { } frameKey && frameKey != packet.IsKeyframe)
        {
            Report(DiagnosticLevel.Warning, "Vp9.KeyframeFlagMismatch", $"The container marks packet {_packet} as {(packet.IsKeyframe ? "a keyframe" : "not a keyframe")}, but the VP9 frame says {(frameKey ? "it is one" : "it is not")}; the frame's own bit is used.");
        }

        bool key = bit ?? false;
        if (_waitingForKeyframe && !key && !StartsWithIntraOnly(data))
        {
            Conceal(packet);
            return;
        }

        bool resync = _video.NeedsResync;
        try
        {
            _video.Decode(data);
        }
        catch (UnsupportedFormatException e)
        {
            _unsupported = true;
            Report(DiagnosticLevel.Warning, "Vp9.UnsupportedSubsampling", e.Message);
            if (_errors == DecodeErrorMode.Throw)
            {
                throw;
            }

            return;
        }
        catch (CorruptDataException e)
        {
            bool missing = resync && !key && !StartsWithIntraOnly(data);
            string id = missing ? "Vp9.MissingReference" : "Vp9.CorruptFrame";
            string message = missing
                ? $"Packet {_packet} is an inter frame with no keyframe before it."
                : $"Packet {_packet} cannot be decoded ({e.Message}); frames up to the next keyframe are dropped.";
            Report(DiagnosticLevel.Warning, id, message);
            if (_errors == DecodeErrorMode.Throw)
            {
                throw;
            }

            _waitingForKeyframe = true;
            Conceal(packet);
            return;
        }

        _waitingForKeyframe = false;
        CheckCodecPrivate();
        object? shown = (object?)_video.Shown8 ?? _video.Shown16;
        if (shown is null)
        {
            return;
        }

        if (MakeFrame(shown, packet) is not { } frame)
        {
            // The alpha frame was broken: the picture goes as a broken one would, and the alpha stream waits for a keyframe too.
            _waitingForKeyframe = true;
            Conceal(packet);
            return;
        }

        _ready.Enqueue(frame);
        Remember(frame);
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
        _waitingForKeyframe = false;
        _lastShown?.Dispose();
        _lastShown = null;
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
        _lastShown?.Dispose();
        _diagnostics?.Dispose();
    }

    /// <summary>Gets the H.273 matrix of a VP9 color_space (0 unknown, 1 BT.601, 2 BT.709, 3 SMPTE 170, 4 SMPTE 240, 5 BT.2020, 7 RGB).</summary>
    internal static byte MatrixOf(int colorSpace) => colorSpace switch
    {
        1 or 3 => 6,
        2 => 1,
        4 => 7,
        5 => 9,
        7 => 0,
        _ => 2,
    };

    /// <summary>
    /// The frame's color: the header's matrix and range win over the container's, which gives the primaries and transfer
    /// (the VP9 header has none). Without a container description, BT.601 primaries and transfer are left unspecified.
    /// </summary>
    internal static ColorCodePoints MergeColor(int colorSpace, int colorRange, ColorCodePoints? container) =>
        new(container?.Primaries ?? 2, container?.TransferCharacteristics ?? 2, MatrixOf(colorSpace), colorRange != 0);

    private static bool StartsWithIntraOnly(ReadOnlySpan<byte> data)
    {
        // Bits from the top: frame_marker (2), profile (2, 3 for profile 3), show_existing_frame, frame_type, show_frame,
        // error_resilient_mode, then intra_only when the frame is hidden.
        if (data.Length < 2 || data[0] >> 6 != 2)
        {
            return false;
        }

        int bits = (data[0] << 8) | data[1];
        int profile = ((bits >> 13) & 1) | (((bits >> 12) & 1) << 1);
        int at = profile == 3 ? 10 : 11;
        bool showExisting = ((bits >> at) & 1) != 0;
        bool inter = ((bits >> (at - 1)) & 1) != 0;
        bool show = ((bits >> (at - 2)) & 1) != 0;
        return !showExisting && inter && !show && ((bits >> (at - 4)) & 1) != 0;
    }

    private YuvVideoFrame? MakeFrame(object shown, EncodedPacket packet)
    {
        YuvRaster raster;
        ColorCodePoints color;
        Size render;
        if (shown is Vp9Frame<byte> f8)
        {
            render = new Size(f8.RenderWidth, f8.RenderHeight);
            color = MergeColor(f8.ColorSpace, f8.ColorRange, _track.Color);
            raster = Copy(f8, color, _alpha is not null);
        }
        else
        {
            Vp9Frame<ushort> f16 = (Vp9Frame<ushort>)shown;
            render = new Size(f16.RenderWidth, f16.RenderHeight);
            color = MergeColor(f16.ColorSpace, f16.ColorRange, _track.Color);
            raster = Copy(f16, color, _alpha is not null);
        }

        try
        {
            if (_alpha is not null)
            {
                if (!DecodeAlpha(packet, raster))
                {
                    raster.Dispose();
                    return null;
                }
            }

            // The planes carry the color tags and static HDR metadata too, so saving them as an image keeps them.
            raster.Metadata.CodePoints = color;
            raster.Metadata.Hdr = _track.Hdr;
            return new YuvVideoFrame(raster, packet.Timestamp, packet.Duration, color)
            {
                Hdr = _track.Hdr,
                Hdr10Plus = Hdr10Plus(packet),
                RenderSize = render,
            };
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>The HDR10+ payload of a packet's addition 4 (an ITU-T T.35 message), copied so it outlives the packet.</summary>
    private static Hdr10PlusMetadata? Hdr10Plus(EncodedPacket packet)
    {
        foreach (PacketAddition addition in packet.Additions)
        {
            if (addition.Id == 4)
            {
                return Hdr10PlusMetadata.Parse(addition.Data.ToArray());
            }
        }

        return null;
    }

    private YuvRaster<T> Copy<T>(Vp9Frame<T> frame, ColorCodePoints color, bool alpha)
        where T : unmanaged
    {
        ChromaFormat chroma = frame.SubsamplingX == 0 ? ChromaFormat.Yuv444 : frame.SubsamplingY == 0 ? ChromaFormat.Yuv422 : ChromaFormat.Yuv420;
        ChromaSiting siting = _track.ChromaSiting ?? ChromaSiting.Left;
        YuvColor yuvColor = color.TryGetYuvColor(siting, out YuvColor c) ? c : new YuvColor(YuvMatrix.Bt709, color.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
        YuvRaster<T> raster = typeof(T) == typeof(byte)
            ? (YuvRaster<T>)(object)YuvRaster.Create(frame.Width, frame.Height, chroma, yuvColor, alpha, _egami)
            : (YuvRaster<T>)(object)YuvRaster.Create(frame.Width, frame.Height, chroma, frame.BitDepth, yuvColor, alpha, _egami);
        try
        {
            CopyPlane(frame, 0, raster.Y);
            CopyPlane(frame, 1, raster.U!);
            CopyPlane(frame, 2, raster.V!);
            return raster;
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    private static void CopyPlane<T>(Vp9Frame<T> frame, int plane, YuvPlane<T> target)
        where T : unmanaged
    {
        Span<T> source = frame.Plane(plane);
        int stride = frame.Strides[plane], width = frame.CropWidth(plane), height = frame.CropHeight(plane);
        for (int y = 0; y < height; y++)
        {
            source.Slice(y * stride, width).CopyTo(target.Row(y));
        }
    }

    private bool DecodeAlpha(EncodedPacket packet, YuvRaster raster)
    {
        ReadOnlyMemory<byte>? alphaData = null;
        foreach (PacketAddition addition in packet.Additions)
        {
            if (addition.Id == 1)
            {
                alphaData = addition.Data;
            }
        }

        object? alpha = null;
        if (alphaData is { } a)
        {
            try
            {
                _alpha!.Decode(a.Span);
                alpha = (object?)_alpha.Shown8 ?? _alpha.Shown16;
            }
            catch (CorruptDataException e)
            {
                Report(DiagnosticLevel.Warning, "Vp9.CorruptFrame", $"Packet {_packet}'s alpha frame cannot be decoded ({e.Message}); frames up to the next keyframe are dropped.");
                if (_errors == DecodeErrorMode.Throw)
                {
                    throw;
                }

                return false;
            }
        }

        switch (raster)
        {
            case YuvRaster<byte> r8:
                FillAlpha<byte>(r8, alpha as Vp9Frame<byte>, 255);
                break;
            case YuvRaster<ushort> r16:
                FillAlpha<ushort>(r16, alpha as Vp9Frame<ushort>, (ushort)((1 << r16.BitDepth) - 1));
                break;
        }

        return true;
    }

    private void FillAlpha<T>(YuvRaster<T> raster, Vp9Frame<T>? alpha, T opaque)
        where T : unmanaged
    {
        YuvPlane<T> plane = raster.A!;
        if (alpha is null || alpha.Width != raster.Width || alpha.Height != raster.Height)
        {
            if (alpha is not null)
            {
                Report(DiagnosticLevel.Warning, "Vp9.CorruptFrame", $"Packet {_packet}'s alpha frame is {alpha.Width}×{alpha.Height}, not {raster.Width}×{raster.Height}; the frame is shown opaque.");
            }

            for (int y = 0; y < raster.Height; y++)
            {
                plane.Row(y).Fill(opaque);
            }

            return;
        }

        CopyPlane(alpha, 0, plane);
    }

    /// <summary>Conceal mode: a packet that cannot be decoded shows the last good frame again (mid-gray before any), at its own time.</summary>
    private void Conceal(EncodedPacket packet)
    {
        // A packet of one hidden frame would have shown nothing, so it stands in for nothing either.
        if (_errors != DecodeErrorMode.Conceal || Vp9Superframe.IsLoneHiddenFrame(packet.Data.Span))
        {
            return;
        }

        YuvVideoFrame frame;
        if (_lastShown is not null)
        {
            YuvRaster copy = _lastShown.Raster.Crop(new Rect(0, 0, _lastShown.Width, _lastShown.Height));
            frame = new YuvVideoFrame(copy, packet.Timestamp, packet.Duration, _lastShown.Color) { Hdr = _lastShown.Hdr };
        }
        else
        {
            if (_track.Width <= 0 || _track.Height <= 0)
            {
                return;
            }

            YuvRaster<byte> gray = YuvRaster.Create(_track.Width, _track.Height, ChromaFormat.Yuv420,
                new YuvColor(YuvMatrix.Bt601, YuvRange.Limited, ChromaSiting.Left), _alpha is not null, _egami);
            for (int y = 0; y < gray.Height; y++)
            {
                gray.Y.Row(y).Fill(128);
                gray.A?.Row(y).Fill(255);
            }

            for (int y = 0; y < gray.U!.Height; y++)
            {
                gray.U.Row(y).Fill(128);
                gray.V!.Row(y).Fill(128);
            }

            frame = new YuvVideoFrame(gray, packet.Timestamp, packet.Duration, new ColorCodePoints(2, 2, 6, false)) { Hdr = _track.Hdr };
        }

        _ready.Enqueue(frame);
    }

    private void Remember(YuvVideoFrame frame)
    {
        if (_errors != DecodeErrorMode.Conceal)
        {
            return;
        }

        _lastShown?.Dispose();
        _lastShown = new YuvVideoFrame(frame.Raster.Crop(new Rect(0, 0, frame.Width, frame.Height)), frame.Timestamp, frame.Duration, frame.Color) { Hdr = frame.Hdr };
    }

    private void CheckCodecPrivate()
    {
        if (_checkedCodecPrivate || _codecPrivate is not { } cp || _video.Headers.Count == 0 || !_video.Headers[0].HasColorConfig)
        {
            return;
        }

        _checkedCodecPrivate = true;
        Vp9FrameHeader h = _video.Headers[0];
        if ((cp.Profile is { } p && p != h.Profile) || (cp.BitDepth is { } b && b != h.BitDepth) ||
            (cp.ChromaSubsampling is { } s && (s == 0 ? 1 : s) != Vp9CodecPrivate.SubsamplingOf(h.SubsamplingX, h.SubsamplingY)))
        {
            Report(DiagnosticLevel.Warning, "Vp9.CodecPrivateMismatch",
                $"The track's CodecPrivate says profile {cp.Profile}, {cp.BitDepth} bit, subsampling {cp.ChromaSubsampling}, but the stream is profile {h.Profile}, {h.BitDepth} bit; the stream's own header is used.");
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
