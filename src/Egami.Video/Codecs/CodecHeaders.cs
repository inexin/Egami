using Egami.Formats.Av1;
using Egami.Video.Codecs.Av1;
using Egami.Video.Codecs.Headers;

namespace Egami.Video.Codecs;

/// <summary>
/// Lists what a compressed frame's header says, field by field under the codec specification's names, for tools such as
/// <c>egami-video info --headers</c> and for checking an encoder's output. VP8 (RFC 6386 sections 9 and 19), VP9 (VP9
/// bitstream specification sections 6.2 and 6.3) and AV1 (AV1 specification section 5) are described; later codecs add theirs to
/// the same call. An AV1 frame header depends on the reference frames, so <see cref="CodecHeaderReader"/> reads a track's packets
/// in order to list them whole.
/// </summary>
/// <example>
/// <code language="csharp">
/// using IDemuxer demuxer = MediaFile.OpenDemuxer("clip.webm");
/// TrackInfo track = demuxer.Info.VideoTrack!;
/// while (demuxer.ReadPacket() is { } packet)
/// {
///     using (packet)
///     {
///         if (packet.TrackNumber == track.Number)
///             foreach (CodecHeaderField field in CodecHeaders.Describe(track, packet))
///                 Console.WriteLine($"{field.Name} = {field.Value}");   // key_frame = 0, show_frame = 1, refresh_golden_frame = 0 ...
///     }
/// }
/// </code>
/// </example>
public static class CodecHeaders
{
    /// <summary>
    /// Returns whether <see cref="Describe(TrackInfo, EncodedPacket)"/> knows the track's codec.
    /// </summary>
    /// <param name="track">The track.</param>
    /// <returns>Whether its headers can be described.</returns>
    public static bool CanDescribe(TrackInfo track)
    {
        ArgumentNullException.ThrowIfNull(track);
        return track.CodecId is CodecIds.Vp8 or CodecIds.Vp9 or CodecIds.Av1;
    }

    /// <summary>
    /// Lists the header fields of one packet of <paramref name="track"/>, in bitstream order. Lists in the header give one field
    /// per entry, named with an index (<c>segment_prob[1]</c>); fields the frame does not code are left out, not filled with the
    /// values they imply. For VP8, the coefficient and motion vector probability updates are given as counts
    /// (<c>coeff_prob_updates</c>, <c>mv_prob_updates</c>). For VP9, a superframe lists its index
    /// (<c>frames_in_superframe_minus_1</c>, <c>frame_sizes[i]</c>) and then each frame after a <c>frame</c> field with its
    /// index; each frame gives its uncompressed header, then the compressed header's <c>tx_mode</c> and reference mode with its
    /// probability updates as counts (<c>coef_prob_updates</c> and so on). A VP9 frame that takes its size from a reference is
    /// read with the track's size, which decides how many tile bits it has. For AV1, each OBU gives <c>obu_type</c> (with
    /// <c>temporal_id</c> and <c>spatial_id</c> when it has an extension) and <c>obu_size</c>, then a sequence header its coded
    /// fields, a metadata OBU <c>metadata_type</c> and its fields (content light level, mastering display, scalability,
    /// timecode; ITU-T T.35 as its country code and <c>itu_t_t35_payload_bytes</c>, the payload's length), and a frame header
    /// its start up to <c>error_resilient_mode</c>; the rest needs the reference frames, which <see cref="CodecHeaderReader"/> keeps.
    /// </summary>
    /// <param name="track">The packet's track.</param>
    /// <param name="packet">The packet.</param>
    /// <returns>The fields; empty when the codec is not one <see cref="CanDescribe"/> knows.</returns>
    /// <exception cref="CorruptDataException">The header is cut off or breaks the codec's rules.</exception>
    public static IReadOnlyList<CodecHeaderField> Describe(TrackInfo track, EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(packet);
        return track.CodecId switch
        {
            CodecIds.Vp8 => Vp8HeaderReader.Read(packet.Data.Span),
            CodecIds.Vp9 => Vp9HeaderReader.Read(packet.Data.Span, (track as VideoTrackInfo)?.Width ?? 0, (track as VideoTrackInfo)?.Height ?? 0),
            CodecIds.Av1 => DescribeAv1(packet.Data.Span, ConfigSequence(track), null),
            _ => [],
        };
    }

    /// <summary>The sequence header in a track's av1C, or null.</summary>
    internal static Av1SequenceInfo? ConfigSequence(TrackInfo track)
    {
        try
        {
            return Av1Config.Parse(track.CodecPrivate.Span) is { } config ? Av1SequenceInfo.Find(config.ConfigObus.Span) : null;
        }
        catch (CorruptDataException)
        {
            return null;
        }
    }

    internal static List<CodecHeaderField> DescribeAv1(ReadOnlySpan<byte> unit, Av1SequenceInfo? sequence, IReadOnlyList<List<CodecHeaderField>>? frameHeaders)
    {
        List<CodecHeaderField> fields = [];
        Av1UnitInfo.Read(unit, sequence, fields, frameHeaders);
        return fields;
    }
}

/// <summary>
/// Lists the header fields of a track's packets read in order, keeping what a codec's later headers depend on. For AV1 it
/// decodes each temporal unit with Egami's <see cref="Av1StreamDecoder"/>, so each frame header is listed whole, as the decoder
/// read it (<see cref="Av1StreamDecoder.LastHeaders"/>, under the specification's syntax element and variable names), in place
/// of the start <see cref="CodecHeaders.Describe"/> gives. Other codecs give what <see cref="CodecHeaders.Describe"/> gives.
/// </summary>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-headers"/>
/// </example>
public sealed class CodecHeaderReader : IDisposable
{
    private readonly TrackInfo _track;
    private readonly Av1StreamDecoder? _av1;
    private readonly byte[]? _av1Config;
    private Av1SequenceInfo? _sequence;
    private bool _needSequence = true;

    /// <summary>Starts reading a track's packets.</summary>
    /// <param name="track">The track.</param>
    /// <param name="context">The context whose threads and limits decoding uses, or <see langword="null"/> for <see cref="MediaContext.Default"/>.</param>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-headers"/>
    /// </example>
    public CodecHeaderReader(TrackInfo track, MediaContext? context = null)
    {
        ArgumentNullException.ThrowIfNull(track);
        _track = track;
        if (track.CodecId == CodecIds.Av1)
        {
            _av1 = new Av1StreamDecoder(new Av1DecoderOptions { ApplyFilmGrain = false }, (context ?? MediaContext.Default).Egami);
            _sequence = CodecHeaders.ConfigSequence(track);
            if (Av1Config.Parse(track.CodecPrivate.Span) is { ConfigObus.IsEmpty: false } config)
            {
                _av1Config = config.ConfigObus.ToArray();
            }
        }
    }

    /// <summary>
    /// Lists the header fields of the track's next packet, as <see cref="CodecHeaders.Describe"/> does, with each AV1 frame header
    /// whole. Send every packet from a keyframe on, in order.
    /// </summary>
    /// <param name="packet">The packet.</param>
    /// <returns>The fields; empty when the codec is not one <see cref="CodecHeaders.CanDescribe"/> knows.</returns>
    /// <exception cref="CorruptDataException">The header is cut off or breaks the codec's rules, or a frame needs a reference frame that was not read.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1-headers"/>
    /// </example>
    public IReadOnlyList<CodecHeaderField> Describe(EncodedPacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (_av1 is null)
        {
            return CodecHeaders.Describe(_track, packet);
        }

        ReadOnlySpan<byte> unit = packet.Data.Span;
        Av1UnitInfo info = Av1UnitInfo.Read(unit, _sequence);
        if (_needSequence && info.Sequence is null && _av1Config is not null)
        {
            _av1.Send(_av1Config);
        }

        _av1.Send(unit);
        _needSequence = false;

        // The decoder lists the sequence header's fields, then each frame header's from show_existing_frame on.
        List<List<CodecHeaderField>> frames = [];
        bool inFrame = false;
        foreach (KeyValuePair<string, long> field in _av1.LastHeaders)
        {
            if (field.Key == "show_existing_frame")
            {
                frames.Add([]);
                inFrame = true;
            }
            else if (field.Key == "seq_profile")
            {
                inFrame = false;
            }

            if (inFrame)
            {
                frames[^1].Add(new CodecHeaderField(field.Key, field.Value));
            }
        }

        List<CodecHeaderField> fields = CodecHeaders.DescribeAv1(unit, _sequence, frames);
        _sequence = info.Sequence ?? _sequence;
        return fields;
    }

    /// <summary>Forgets the packets read so far, for example after a seek: the next packet must be a keyframe.</summary>
    public void Reset()
    {
        _av1?.Reset();
        _needSequence = true;
    }

    /// <summary>Drops what the reader keeps.</summary>
    public void Dispose() => _av1?.Dispose();
}

/// <summary>One field of a compressed frame's header, as <see cref="CodecHeaders.Describe"/> lists it.</summary>
/// <param name="Name">The field's name in the codec specification, with an index for list entries (<c>quantizer_update_value[0]</c>).</param>
/// <param name="Value">The value as coded; signed fields carry their sign.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-headers"/>
/// </example>
public readonly record struct CodecHeaderField(string Name, long Value);
