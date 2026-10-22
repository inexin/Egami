using System.Diagnostics.CodeAnalysis;

using Egami.Colors;
using Egami.Formats.Av1;
using Egami.Pixels;

namespace Egami.Video.Codecs.Av1;

/// <summary>
/// The AV1 codec adapter: the send and receive loop over Egami's <see cref="Av1StreamDecoder"/>. A packet is a temporal unit;
/// decoding is synchronous, so its shown frame is ready right after it is sent, and a unit of hidden frames gives none. Frames
/// come out at their own size, as YuvRaster&lt;byte&gt; at 8 bits and YuvRaster&lt;ushort&gt; at 10 and 12, 4:0:0 for monochrome.
/// </summary>
internal sealed class Av1VideoDecoder : IVideoDecoder
{
    private readonly VideoTrackInfo _track;
    private readonly EgamiContext _egami;
    private readonly DecodeErrorMode _errors;
    private readonly Av1StreamDecoder _decoder;
    private readonly Queue<YuvVideoFrame> _ready = new();
    private readonly byte[]? _configSequence;
    private Av1SequenceInfo? _sequence;
    private DiagnosticReporter? _diagnostics;
    private YuvVideoFrame? _lastShown;
    private HdrMetadata? _streamHdr;
    private bool _needSequence = true;
    private bool _decodedKey;
    private bool _waitingForKeyframe;
    private int _packet = -1;
    private bool _disposed;

    public Av1VideoDecoder(VideoTrackInfo track, VideoDecoderOptions options, MediaContext context)
    {
        _track = track;
        _egami = context.Egami;
        _errors = options.Errors;
        _decoder = new Av1StreamDecoder(new Av1DecoderOptions { ApplyFilmGrain = options.ApplyFilmGrain }, _egami);
        if (Av1Config.Parse(track.CodecPrivate.Span) is { } config && !config.ConfigObus.IsEmpty)
        {
            try
            {
                foreach (Av1Obu obu in Av1Obus.Split(config.ConfigObus.Span))
                {
                    if (obu.Type == Av1Obus.SequenceHeader)
                    {
                        _configSequence = config.ConfigObus.Span[obu.Start..obu.End].ToArray();
                        _sequence = Av1SequenceInfo.Parse(config.ConfigObus.Span.Slice(obu.PayloadStart, obu.PayloadLength));
                        break;
                    }
                }

                // configOBUs may also hold metadata OBUs for the whole track, such as HDR's (FFmpeg copies them there).
                UpdateHdr(Av1UnitInfo.Read(config.ConfigObus.Span, _sequence));
            }
            catch (CorruptDataException)
            {
                // A broken av1C is ignored: the stream's own sequence headers are used.
                _configSequence = null;
                _sequence = null;
            }
        }
    }

    public void Send(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        ObjectDisposedException.ThrowIf(_disposed, this);
        _packet++;
        ReadOnlySpan<byte> data = packet.Data.Span;
        Av1UnitInfo unit;
        try
        {
            unit = Av1UnitInfo.Read(data, _sequence);
        }
        catch (CorruptDataException e)
        {
            if (Failed(packet, e, null))
            {
                throw;
            }

            return;
        }

        if (unit.HasFrame && unit.IsShownKeyFrame != packet.IsKeyframe)
        {
            Report(DiagnosticLevel.Warning, "Av1.KeyframeFlagMismatch",
                $"The container marks packet {_packet} as {(packet.IsKeyframe ? "a keyframe" : "not a keyframe")}, but the AV1 temporal unit {(unit.IsShownKeyFrame ? "starts with a shown key frame" : "does not start with a shown key frame")}; the stream's own frame header is used.");
        }

        if (_waitingForKeyframe && !unit.IsShownKeyFrame)
        {
            Conceal(packet, unit);
            return;
        }

        try
        {
            if (_needSequence && unit.Sequence is null && _configSequence is not null)
            {
                // A unit without a sequence header (as MP4 and Matroska may store them, the header being in av1C) after a start or a seek.
                _decoder.Send(_configSequence);
            }

            _decoder.Send(data);
        }
        catch (CorruptDataException e)
        {
            if (Failed(packet, e, unit))
            {
                throw;
            }

            return;
        }

        _sequence = unit.Sequence ?? _sequence;
        _needSequence = false;
        _decodedKey |= unit.HasKeyFrame;
        _waitingForKeyframe = false;
        UpdateHdr(unit);

        while (_decoder.NextFrame is { } next)
        {
            YuvVideoFrame frame = Receive(next, packet, unit);
            _ready.Enqueue(frame);
            Remember(frame);
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
        _needSequence = true;
        _decodedKey = false;
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

        _decoder.Dispose();
        _lastShown?.Dispose();
        _diagnostics?.Dispose();
    }

    /// <summary>
    /// The frame's color: the sequence header's code points win over the container's, which fills only what the header leaves
    /// unspecified (2); the header always codes the range.
    /// </summary>
    internal static ColorCodePoints MergeColor(ColorCodePoints stream, ColorCodePoints? container) => new(
        stream.Primaries != 2 ? stream.Primaries : container?.Primaries ?? 2,
        stream.TransferCharacteristics != 2 ? stream.TransferCharacteristics : container?.TransferCharacteristics ?? 2,
        stream.MatrixCoefficients != 2 ? stream.MatrixCoefficients : container?.MatrixCoefficients ?? 2,
        stream.FullRange);

    /// <summary>Takes a unit's content light level and mastering display metadata; each holds until the stream sends a new one.</summary>
    private void UpdateHdr(Av1UnitInfo unit)
    {
        if (unit.ContentLightLevel is null && unit.MasteringDisplay is null)
        {
            return;
        }

        HdrMetadata hdr = _streamHdr ?? _track.Hdr ?? new HdrMetadata();
        if (unit.MasteringDisplay is { } mdcv)
        {
            hdr = hdr with { MasteringPrimaries = mdcv.MasteringPrimaries, MasteringMaxLuminance = mdcv.MasteringMaxLuminance, MasteringMinLuminance = mdcv.MasteringMinLuminance };
        }

        if (unit.ContentLightLevel is { } light)
        {
            hdr = hdr with { MaxContentLightLevel = light.MaxCll, MaxFrameAverageLightLevel = light.MaxFall };
        }

        _streamHdr = hdr;
    }

    private YuvVideoFrame Receive(Av1FrameInfo next, EncodedPacket packet, Av1UnitInfo unit)
    {
        ColorCodePoints color = MergeColor(next.Color, _track.Color);
        ChromaSiting siting = _track.ChromaSiting ?? _sequence?.Siting ?? ChromaSiting.Left;
        YuvColor yuvColor = color.TryGetYuvColor(siting, out YuvColor c) ? c : new YuvColor(YuvMatrix.Bt709, color.FullRange ? YuvRange.Full : YuvRange.Limited, siting);
        YuvRaster raster = next.BitDepth == 8
            ? YuvRaster.Create(next.Width, next.Height, next.Chroma, yuvColor, false, _egami)
            : YuvRaster.Create(next.Width, next.Height, next.Chroma, next.BitDepth, yuvColor, false, _egami);
        try
        {
            _decoder.TryReceiveFrame(raster, out Av1FrameInfo info);
            HdrMetadata? hdr = _streamHdr ?? _track.Hdr;
            raster.Metadata.CodePoints = color;
            raster.Metadata.Hdr = hdr;
            return new YuvVideoFrame(raster, packet.Timestamp, packet.Duration, color)
            {
                Hdr = hdr,
                Hdr10Plus = unit.Hdr10Plus ?? Hdr10PlusAddition(packet),
                RenderSize = new Size(info.RenderWidth, info.RenderHeight),
            };
        }
        catch
        {
            raster.Dispose();
            throw;
        }
    }

    /// <summary>An HDR10+ payload a WebM block carries in its addition 4, as for VP9.</summary>
    private static Hdr10PlusMetadata? Hdr10PlusAddition(EncodedPacket packet)
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

    /// <summary>Reports a unit that cannot be decoded and conceals it; returns true in Throw mode, where the caller rethrows.</summary>
    private bool Failed(EncodedPacket packet, CorruptDataException e, Av1UnitInfo? unit)
    {
        bool missing = !_decodedKey && unit is not null && !unit.HasKeyFrame;
        string id = missing ? "Av1.MissingReference" : "Av1.CorruptFrame";
        string message = missing
            ? $"Packet {_packet} refers to frames the decoder does not have: no key frame came before it ({e.Message})."
            : $"Packet {_packet} cannot be decoded ({e.Message}); frames up to the next shown key frame are dropped.";
        Report(DiagnosticLevel.Warning, id, message);
        if (_errors == DecodeErrorMode.Throw)
        {
            return true;
        }

        _waitingForKeyframe = true;
        Conceal(packet, unit);
        return false;
    }

    /// <summary>Conceal mode: a unit that cannot be decoded shows the last good frame again (mid-gray before any), at its own time.</summary>
    private void Conceal(EncodedPacket packet, Av1UnitInfo? unit)
    {
        // A unit of hidden frames would have shown nothing, so it stands in for nothing either.
        if (_errors != DecodeErrorMode.Conceal || unit is { ShowsFrame: false })
        {
            return;
        }

        YuvVideoFrame frame;
        if (_lastShown is not null)
        {
            YuvRaster copy = _lastShown.Raster.Crop(new Rect(0, 0, _lastShown.Width, _lastShown.Height));
            frame = new YuvVideoFrame(copy, packet.Timestamp, packet.Duration, _lastShown.Color) { Hdr = _lastShown.Hdr, RenderSize = _lastShown.RenderSize };
        }
        else
        {
            if (_track.Width <= 0 || _track.Height <= 0)
            {
                return;
            }

            YuvRaster<byte> gray = YuvRaster.Create(_track.Width, _track.Height, ChromaFormat.Yuv420,
                new YuvColor(YuvMatrix.Bt709, YuvRange.Limited, ChromaSiting.Left), false, _egami);
            for (int y = 0; y < gray.Height; y++)
            {
                gray.Y.Row(y).Fill(128);
            }

            for (int y = 0; y < gray.U!.Height; y++)
            {
                gray.U.Row(y).Fill(128);
                gray.V!.Row(y).Fill(128);
            }

            frame = new YuvVideoFrame(gray, packet.Timestamp, packet.Duration, new ColorCodePoints(2, 2, 2, false)) { Hdr = _track.Hdr };
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
        _lastShown = new YuvVideoFrame(frame.Raster.Crop(new Rect(0, 0, frame.Width, frame.Height)), frame.Timestamp, frame.Duration, frame.Color)
        {
            Hdr = frame.Hdr,
            RenderSize = frame.RenderSize,
        };
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
