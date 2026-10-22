using Egami.Video.Codecs.Headers;

namespace Egami.Video.Codecs;

/// <summary>
/// Lists what a compressed frame's header says, field by field under the codec specification's names, for tools such as
/// <c>egami-video info --headers</c> and for checking an encoder's output. VP8 (RFC 6386 sections 9 and 19) and VP9 (VP9
/// bitstream specification sections 6.2 and 6.3) are described; later codecs add theirs to the same call.
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
        return track.CodecId is CodecIds.Vp8 or CodecIds.Vp9;
    }

    /// <summary>
    /// Lists the header fields of one packet of <paramref name="track"/>, in bitstream order. Lists in the header give one field
    /// per entry, named with an index (<c>segment_prob[1]</c>); fields the frame does not code are left out, not filled with the
    /// values they imply. For VP8, the coefficient and motion vector probability updates are given as counts
    /// (<c>coeff_prob_updates</c>, <c>mv_prob_updates</c>). For VP9, a superframe lists its index
    /// (<c>frames_in_superframe_minus_1</c>, <c>frame_sizes[i]</c>) and then each frame after a <c>frame</c> field with its
    /// index; each frame gives its uncompressed header, then the compressed header's <c>tx_mode</c> and reference mode with its
    /// probability updates as counts (<c>coef_prob_updates</c> and so on). A VP9 frame that takes its size from a reference is
    /// read with the track's size, which decides how many tile bits it has.
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
            _ => [],
        };
    }
}

/// <summary>One field of a compressed frame's header, as <see cref="CodecHeaders.Describe"/> lists it.</summary>
/// <param name="Name">The field's name in the codec specification, with an index for list entries (<c>quantizer_update_value[0]</c>).</param>
/// <param name="Value">The value as coded; signed fields carry their sign.</param>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-headers"/>
/// </example>
public readonly record struct CodecHeaderField(string Name, long Value);
