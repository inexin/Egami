namespace Egami.Video;

/// <summary>
/// How <see cref="MediaFile.Trim(string, string, MediaTime, MediaTime?, TrimMode, Func{TrackInfo, bool}?, MediaContext?, IProgress{MediaProgress}?, CancellationToken)"/>
/// starts a cut that does not fall on a keyframe.
/// </summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoTrim.cs" region="video-trim"/>
/// </example>
public enum TrimMode
{
    /// <summary>
    /// The default: frame-exact, with the fewest frames encoded again. The frames from the start up to the next keyframe are
    /// decoded and encoded again as a new group that starts with a keyframe and matches the stream (codec, profile, size, bit
    /// depth, chroma format, color, and for AV1 the sequence header byte for byte), at the quantizer the frames it replaces had;
    /// every packet from that keyframe on is copied byte for byte. A start on a keyframe, and Motion JPEG or uncompressed video,
    /// are a plain copy. Codecs Egami.Video cannot encode (H.264, HEVC) start at the keyframe at or before the start, as
    /// <see cref="Keyframe"/> does; a stream the encoder cannot match is encoded again whole, as <see cref="Reencode"/> does.
    /// Both say so in a <see cref="MediaWarning"/>.
    /// </summary>
    Smart,

    /// <summary>
    /// Copies every packet: the cut starts at the video keyframe at or before the start, so it may show a few frames more than
    /// asked, as <c>ffmpeg -ss -c copy</c> does. Nothing is decoded.
    /// </summary>
    Keyframe,

    /// <summary>
    /// Decodes the video from the keyframe before the start and encodes every frame of the cut again, with the same codec at the
    /// quantizer the source had. Frame-exact like <see cref="Smart"/>, but slower and with a second generation of loss.
    /// </summary>
    Reencode,
}

/// <summary>What a trim did: where the cut starts in the input, how much was copied and encoded again, and what it reported.</summary>
/// <example>
/// <code language="csharp" source="../../tests/Egami.DocSamples/VideoTrim.cs" region="video-trim"/>
/// </example>
public sealed record TrimResult
{
    /// <summary>
    /// Gets how the video was cut: the mode asked for, or the one smart cut fell back to (<see cref="TrimMode.Keyframe"/> for a
    /// codec it cannot encode, <see cref="TrimMode.Reencode"/> for a stream it cannot match).
    /// </summary>
    public required TrimMode Mode { get; init; }

    /// <summary>
    /// Gets where the output starts in the input: the first frame at or after the start asked for, or with
    /// <see cref="TrimMode.Keyframe"/> the keyframe at or before it. The output's timestamps start at 0 there.
    /// </summary>
    public required MediaTime Start { get; init; }

    /// <summary>Gets how many frames of the main video track were written, or 0 when there is no video.</summary>
    public long Frames { get; init; }

    /// <summary>Gets how many of those frames were encoded again; the others were copied.</summary>
    public long ReencodedFrames { get; init; }

    /// <summary>Gets how many packets were written, on all tracks.</summary>
    public long Packets { get; init; }

    /// <summary>
    /// Gets what the trim reported, also sent to <see cref="MediaContext.Warnings"/>: <c>trim-keyframe</c> when the cut starts at a
    /// keyframe before the start, <c>trim-reencode</c> when smart cut encoded the whole cut again, and <c>trim-audio-packet</c>
    /// when an audio track could only be cut at a packet boundary.
    /// </summary>
    public IReadOnlyList<MediaWarning> Warnings { get; init; } = [];
}
