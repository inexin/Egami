using System.Globalization;

using Egami.Formats.Av1;
using Egami.Video.Codecs;
using Egami.Video.Codecs.Av1;
using Egami.Video.Codecs.Vp9;

namespace Egami.Video.Trimming;

/// <summary>
/// What a trim needs to know about a video track's packets without decoding them: which are keyframes, which show a frame, the
/// quantizer each frame was coded with, and the stream parameters a keyframe states (the ones a re-encoded group must match).
/// VP8 (RFC 6386 section 9), VP9 (VP9 bitstream specification section 6.2) and AV1 (AV1 specification sections 5.5 and 5.9).
/// </summary>
internal sealed class TrimCodec
{
    private readonly VideoTrackInfo _track;
    private Av1SequenceInfo? _sequence;

    public TrimCodec(VideoTrackInfo track)
    {
        _track = track;
        if (track.CodecId == CodecIds.Av1)
        {
            _sequence = CodecHeaders.ConfigSequence(track);
        }
    }

    /// <summary>Gets whether every packet is a frame of its own shown in packet order, with nothing held back or reordered.</summary>
    public bool InOrder => _track.CodecId is CodecIds.Vp8 or CodecIds.Vp9 or CodecIds.Av1 or CodecIds.MotionJpeg or CodecIds.Uncompressed;

    /// <summary>Gets whether smart cut can encode the head of a cut again: VP8, VP9 and AV1.</summary>
    public bool CanMatch => _track.CodecId is CodecIds.Vp8 or CodecIds.Vp9 or CodecIds.Av1;

    /// <summary>Gets the range of the codec's quantizer index: 0 to 127 for VP8, 0 to 255 for VP9 and AV1.</summary>
    public int MaxQuantizer => _track.CodecId == CodecIds.Vp8 ? 127 : 255;

    /// <summary>Gets whether a decoder can start at a packet: the frame's own bits for VP8, VP9 and AV1, else the container's flag.</summary>
    public bool IsKeyframe(EncodedPacket packet) => FrameKind.IsKeyframe(_track.CodecId, packet.Data.Span) ?? packet.IsKeyframe;

    /// <summary>
    /// Gets whether a packet shows a frame: not a hidden VP8 alternate reference, a lone hidden VP9 frame or an AV1 unit of hidden
    /// frames only. Packets of other codecs, and ones too broken to tell, count as shown.
    /// </summary>
    public bool ShowsFrame(EncodedPacket packet)
    {
        ReadOnlySpan<byte> data = packet.Data.Span;
        switch (_track.CodecId)
        {
            case CodecIds.Vp8:
                return data.Length < 3 || ((data[0] >> 4) & 1) != 0;
            case CodecIds.Vp9:
                return !Vp9Superframe.IsLoneHiddenFrame(data);
            case CodecIds.Av1:
                try
                {
                    Av1UnitInfo info = Av1UnitInfo.Read(data, _sequence);
                    _sequence = info.Sequence ?? _sequence;
                    return info.ShowsFrame || !info.HasFrame;
                }
                catch (CorruptDataException)
                {
                    return true;
                }

            default:
                return true;
        }
    }

    /// <summary>Gets the quantizer indexes a packet's headers list (VP8 y_ac_qi, VP9 and AV1 base_q_idx), one per coded frame.</summary>
    public static IEnumerable<int> Quantizers(IReadOnlyList<CodecHeaderField> fields, string codecId)
    {
        string name = codecId == CodecIds.Vp8 ? "y_ac_qi" : "base_q_idx";
        foreach (CodecHeaderField field in fields)
        {
            if (field.Name == name)
            {
                yield return (int)field.Value;
            }
        }
    }

    /// <summary>
    /// Describes what a keyframe says about the whole stream, for comparing a re-encoded group with the source: VP8's version,
    /// size, scaling, color space and clamping; VP9's profile, bit depth, color space, range, subsampling and size; AV1's sequence
    /// header, byte for byte. Null when the packet holds none of it.
    /// </summary>
    public string? StreamParameters(ReadOnlyMemory<byte> keyframe)
    {
        try
        {
            switch (_track.CodecId)
            {
                case CodecIds.Vp8:
                    {
                        IReadOnlyList<CodecHeaderField> fields = Codecs.Headers.Vp8HeaderReader.Read(keyframe.Span);
                        string[] names = ["version", "width", "height", "horizontal_scale", "vertical_scale", "color_space", "clamping_type"];
                        return Join(fields, names);
                    }

                case CodecIds.Vp9:
                    foreach (ReadOnlyMemory<byte> frame in Vp9Superframe.Split(keyframe))
                    {
                        Vp9FrameHeader h = Vp9FrameHeader.Read(frame.Span, _ => (_track.Width, _track.Height));
                        if (h.KeyFrame)
                        {
                            return string.Create(CultureInfo.InvariantCulture,
                                $"profile {h.Profile}, {h.BitDepth}-bit, color_space {h.ColorSpace}, color_range {h.ColorRange}, subsampling {h.SubsamplingX}{h.SubsamplingY}, {h.Width}x{h.Height}");
                        }
                    }

                    return null;

                case CodecIds.Av1:
                    return SequenceHeader(keyframe.Span) is { } obu ? "sequence header " + Convert.ToHexString(Payload(obu)) : null;

                default:
                    return null;
            }
        }
        catch (CorruptDataException)
        {
            return null;
        }
    }

    /// <summary>The sequence header OBU (with its size field) in a temporal unit, or in the track's av1C when the unit has none.</summary>
    public byte[]? SequenceHeader(ReadOnlySpan<byte> unit)
    {
        if (Find(unit) is { } own)
        {
            return own;
        }

        return Av1Config.Parse(_track.CodecPrivate.Span) is { ConfigObus.IsEmpty: false } config ? Find(config.ConfigObus.Span) : null;

        static byte[]? Find(ReadOnlySpan<byte> obus)
        {
            foreach (Av1Obu obu in Av1Obus.Split(obus))
            {
                if (obu.Type == Av1Obus.SequenceHeader)
                {
                    return obus[obu.Start..obu.End].ToArray();
                }
            }

            return null;
        }
    }

    /// <summary>The options that reproduce the stream's sequence header, or null with the reason the encoder cannot.</summary>
    public (Av1SequenceOptions? Options, string? Reason) SequenceOptions(ReadOnlySpan<byte> keyframe)
    {
        if (SequenceHeader(keyframe) is not { } obu)
        {
            return (null, "the stream has no sequence header");
        }

        try
        {
            return (Av1SequenceOptions.FromSequenceHeader(obu), null);
        }
        catch (Exception e) when (e is UnsupportedFormatException or CorruptDataException or ArgumentException)
        {
            return (null, e.Message);
        }
    }

    private static ReadOnlySpan<byte> Payload(byte[] obu)
    {
        Av1Obu header = Av1Obus.ReadHeader(obu, 0);
        return obu.AsSpan(header.PayloadStart, header.PayloadLength);
    }

    private static string Join(IReadOnlyList<CodecHeaderField> fields, string[] names) =>
        string.Join(", ", names.Select(n => $"{n} {fields.FirstOrDefault(f => f.Name == n).Value.ToString(CultureInfo.InvariantCulture)}"));
}
