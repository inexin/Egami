using Egami.Video.Codecs.Av1;

namespace Egami.Video.Codecs;

/// <summary>
/// AV1, as WebM, Matroska, MP4, IVF and raw OBU files carry it, decoded by Egami's AV1 decoder in the Main, High and Professional
/// profiles: 8, 10 and 12 bit, 4:0:0, 4:2:0, 4:2:2 and 4:4:4, film grain, scaled references and scalable streams, every frame equal
/// to libaom's sample for sample. A packet is a temporal unit. Frames come out at their own size (a stream may change size at a
/// key frame) as 8-bit planes, or 16-bit planes holding 10 or 12 bits; a monochrome stream gives 4:0:0 planes. Hidden frames give
/// no frame; a frame shown later with show_existing_frame comes out then. Encoding goes through <see cref="CreateEncoder"/>.
/// </summary>
/// <remarks>
/// <para>
/// The frame's color comes from the sequence header; the container's (<see cref="VideoTrackInfo.Color"/>) fills only what the
/// header leaves unspecified. Content light level and mastering display metadata OBUs give <see cref="VideoFrame.Hdr"/> (held
/// until the stream sends new ones; the track's <see cref="VideoTrackInfo.Hdr"/> before any), and an ITU-T T.35 metadata OBU
/// with HDR10+ gives <see cref="VideoFrame.Hdr10Plus"/>. <see cref="VideoDecoderOptions.ApplyFilmGrain"/> turns film grain off.
/// </para>
/// <para>
/// A track's av1C (<see cref="TrackInfo.CodecPrivate"/>) supplies the sequence header when the first unit, or the first after a
/// seek, has none. Broken data follows <see cref="VideoDecoderOptions.Errors"/> and is reported through the Egami context's
/// diagnostics as <c>Av1.CorruptFrame</c> (the unit's data is broken), <c>Av1.MissingReference</c> (a unit that needs frames
/// from before the first key frame) or <c>Av1.KeyframeFlagMismatch</c> (the container's keyframe flag disagrees with the unit,
/// which wins: a keyframe is a unit starting with a shown key frame). After a broken unit, decoding resumes at the next shown key
/// frame; in <see cref="DecodeErrorMode.Conceal"/> mode the units in between show the last good frame again.
/// </para>
/// </remarks>
/// <example>
/// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1"/>
/// </example>
public sealed class Av1Codec : VideoCodec
{
    private Av1Codec()
    {
    }

    /// <summary>Gets the AV1 codec.</summary>
    public static Av1Codec Instance { get; } = new();

    /// <inheritdoc/>
    public override string Name => "AV1";

    /// <inheritdoc/>
    public override string CodecId => CodecIds.Av1;

    /// <inheritdoc/>
    public override bool CanEncode => true;

    /// <inheritdoc/>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/Video.cs" region="video-av1"/>
    /// </example>
    public override IVideoDecoder CreateDecoder(VideoTrackInfo track, VideoDecoderOptions? options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(track);
        ArgumentNullException.ThrowIfNull(context);
        options ??= VideoDecoderOptions.Default;
        if (options.ScaleDenominator != 1)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.ScaleDenominator, "AV1 decodes at full size only; the scale denominator must be 1.");
        }

        return new Av1VideoDecoder(track, options, context);
    }

    /// <summary>
    /// Creates an AV1 encoder, which codes frames with Egami's <see cref="Egami.Formats.Av1.Av1StreamEncoder"/>. 8 and 10-bit 4:2:0
    /// planes make Main profile streams, 4:4:4 High and 12-bit or 4:2:2 Professional, coded as they are; RGB frames are converted
    /// to 8-bit 4:2:0 BT.601 limited range unless <see cref="Av1EncoderOptions.BitDepth"/> or
    /// <see cref="VideoEncoderOptions.ChromaFormat"/> say otherwise. By default it aims at constant quality 60 at effort 4, looks
    /// 25 frames ahead for scene cuts and for a hidden alternate reference at each group's end with a hidden frame halfway to it,
    /// both shown later with show_existing_frame, and places a keyframe at least every 10 seconds; <see cref="VideoEncoderOptions"/>
    /// chooses bitrate, constant bitrate, two passes or real time instead, and <see cref="VideoEncoderOptions.Av1"/> holds the
    /// expert settings. Each packet is a temporal unit; the track's av1C comes from the sequence header, which also carries the
    /// frames' color code points, and keyframes carry their static HDR metadata as metadata OBUs (HDR10+ goes into the frames
    /// that have it). Alpha is not coded: AV1 has no alpha in Matroska or MP4. The output is the same at any thread count.
    /// </summary>
    /// <param name="options">The picture size, frame rate and how to spend bits.</param>
    /// <param name="context">The context.</param>
    /// <returns>The encoder.</returns>
    /// <exception cref="ArgumentException">The options are out of range, or a bitrate mode has no bitrate.</exception>
    /// <example>
    /// <code language="csharp" source="../../../tests/Egami.DocSamples/VideoEncode.cs" region="video-encode-av1"/>
    /// </example>
    public override IVideoEncoder CreateEncoder(VideoEncoderOptions options, MediaContext context)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(context);
        return new Av1VideoEncoder(options, context);
    }
}
